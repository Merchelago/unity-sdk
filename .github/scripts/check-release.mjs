#!/usr/bin/env node
// Проверка релиза ru.vhrgames.sdk — в CI на push тега v* и локально перед тегом:
//
//   node .github/scripts/check-release.mjs                    # без тега: всё, кроме сверки с тегом
//   node .github/scripts/check-release.mjs --tag v1.10.0      # как в CI
//   node .github/scripts/check-release.mjs --tag v1.10.0 --notes-out notes.md   # + текст релиза
//
// Ошибки (✖) — код выхода 1, релиз не создаётся. Предупреждения (⚠) не мешают.
// Только встроенные модули Node 18+, без npm install.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const args = process.argv.slice(2);
const arg = (name) => {
  const i = args.indexOf(name);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : undefined;
};
const tag = arg('--tag');
const notesOut = arg('--notes-out');

const errors = [];
const warnings = [];
const ok = (m) => console.log('✔ ' + m);
const fail = (m) => { errors.push(m); console.log('✖ ' + m); };
const warn = (m) => { warnings.push(m); console.log('⚠ ' + m); };
const read = (rel) => fs.readFileSync(path.join(root, rel), 'utf8').replace(/^﻿/, '');
const exists = (rel) => fs.existsSync(path.join(root, rel));

const SEMVER = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z.-]+))?$/;

// ------------------------------------------------------------ package.json
let pkg = null;
try {
  pkg = JSON.parse(read('package.json'));
  ok('package.json — валидный JSON');
} catch (e) {
  fail('package.json — невалидный JSON: ' + e.message);
}

// null — версия не прочиталась: проверки, завязанные на неё, пропускаются (ошибка уже выведена).
const version = SEMVER.test(pkg?.version ?? '') ? pkg.version : null;
if (pkg) {
  if (pkg.name !== 'ru.vhrgames.sdk') fail(`package.json: name = "${pkg.name}", ожидается "ru.vhrgames.sdk"`);
  if (!SEMVER.test(version ?? '')) fail(`package.json: version "${version}" — не SemVer (X.Y.Z или X.Y.Z-pre)`);
  else ok(`версия пакета ${version}`);
  for (const [dep, v] of Object.entries(pkg.dependencies ?? {}))
    if (!SEMVER.test(v)) fail(`package.json: зависимость ${dep}: "${v}" — нужна точная версия X.Y.Z (git/file-ссылки в пакете не разрешаются)`);
  // scopedRegistries Unity в пакете игнорирует, но поле — справка для установщика и сервера.
  const scopes = (pkg.scopedRegistries ?? []).flatMap((r) => r.scopes ?? []);
  for (const dep of Object.keys(pkg.dependencies ?? {})) {
    if (dep.startsWith('com.unity.')) continue;
    if (!scopes.some((s) => dep === s || dep.startsWith(s + '.')))
      warn(`зависимость ${dep} не покрыта скоупами scopedRegistries в package.json — проверьте установщик и README`);
  }
}

// ------------------------------------------------------------ тег
if (!tag) {
  console.log('· тег не задан (--tag) — сверка с тегом пропущена');
} else if (version) {
  if (tag === `v${version}`) ok(`тег ${tag} совпадает с версией пакета`);
  else fail(`тег ${tag} не совпадает с версией package.json (${version}) — ожидается v${version}`);
}

// ------------------------------------------------------------ VhrSdk.SdkVersion
if (version) try {
  const m = /public\s+const\s+string\s+SdkVersion\s*=\s*"([^"]+)"/.exec(read('Runtime/VhrSdk.cs'));
  if (!m) fail('Runtime/VhrSdk.cs: не найдена константа SdkVersion');
  else if (m[1] !== version) fail(`VhrSdk.SdkVersion = "${m[1]}", а в package.json ${version}`);
  else ok(`VhrSdk.SdkVersion = ${m[1]}`);
} catch (e) {
  fail('Runtime/VhrSdk.cs не читается: ' + e.message);
}

// ------------------------------------------------------------ CHANGELOG
let notes = '';
if (version) try {
  const lines = read('CHANGELOG.md').replace(/\r\n/g, '\n').split('\n');
  const esc = (version ?? '').replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const head = new RegExp(`^##\\s+\\[?v?${esc}\\]?(\\s|$)`);
  const start = lines.findIndex((l) => head.test(l));
  if (start < 0) {
    fail(`CHANGELOG.md: нет раздела «## [${version}]»`);
  } else {
    let end = lines.findIndex((l, i) => i > start && /^##\s/.test(l));
    if (end < 0) end = lines.length;
    notes = lines.slice(start + 1, end).join('\n').replace(/\n+---\s*$/, '').trim();
    if (!notes) fail(`CHANGELOG.md: раздел ${version} пустой`);
    else ok(`CHANGELOG.md: раздел ${version} (${notes.split('\n').length} строк)`);
    if (!/\d{4}-\d{2}-\d{2}/.test(lines[start])) warn(`CHANGELOG.md: у заголовка раздела ${version} нет даты (YYYY-MM-DD)`);
  }
} catch (e) {
  fail('CHANGELOG.md не читается: ' + e.message);
}
if (notesOut && notes) {
  fs.writeFileSync(notesOut, notes + '\n');
  console.log(`· текст релиза записан в ${notesOut}`);
}

// ------------------------------------------------------------ .meta и GUID
// Что видит Unity в пакете: всё, кроме скрытого (".git", ".github"), папок/файлов с "~" на
// конце, "cvs" и "*.tmp". Каждому такому файлу и папке нужен .meta, каждому .meta — пара.
const hidden = (name) => name.startsWith('.') || name.endsWith('~') || name.toLowerCase() === 'cvs' || name.endsWith('.tmp');
const guids = new Map();
let assets = 0;
let metas = 0;
(function walk(relDir) {
  const abs = path.join(root, relDir);
  const entries = fs.readdirSync(abs, { withFileTypes: true });
  const names = new Set(entries.map((e) => e.name));
  for (const e of entries) {
    if (hidden(e.name)) continue;
    const rel = relDir ? `${relDir}/${e.name}` : e.name;
    if (e.name.endsWith('.meta')) {
      metas++;
      const target = e.name.slice(0, -5);
      if (!names.has(target) || hidden(target)) fail(`${rel}: .meta без файла или папки (осиротевший)`);
      const text = fs.readFileSync(path.join(abs, e.name), 'utf8');
      const g = /^guid:\s*(\S+)\s*$/m.exec(text)?.[1];
      if (!g) fail(`${rel}: нет guid`);
      else if (!/^[0-9a-f]{32}$/.test(g)) fail(`${rel}: guid "${g}" — нужно 32 hex-символа в нижнем регистре`);
      else if (guids.has(g)) fail(`${rel}: guid ${g} уже занят ${guids.get(g)}`);
      else guids.set(g, rel);
      const isDir = names.has(target) && fs.statSync(path.join(abs, target)).isDirectory();
      if (isDir && !/^folderAsset:\s*yes/m.test(text)) warn(`${rel}: у .meta папки нет "folderAsset: yes"`);
      continue;
    }
    assets++;
    if (!names.has(e.name + '.meta')) fail(`${rel}${e.isDirectory() ? '/' : ''}: нет ${e.name}.meta (в пакете из git Unity его не создаст — ассет пропадёт)`);
    if (e.isDirectory()) walk(rel);
  }
})('');
if (!errors.some((m) => m.includes('.meta') || m.includes('guid'))) ok(`.meta: ${assets} файлов и папок, ${metas} .meta, GUID уникальны`);

// ------------------------------------------------------------ JSON-ассеты
for (const rel of ['Runtime/VhrGames.Sdk.asmdef', 'Editor/VhrGames.Sdk.Editor.asmdef']) {
  try {
    JSON.parse(read(rel));
  } catch (e) {
    fail(`${rel} — невалидный JSON: ${e.message}`);
  }
}

// ------------------------------------------------------------ ядро установщика
const CORE_START = '// >>> VHR-SETUP-CORE';
const CORE_END = '// <<< VHR-SETUP-CORE';
const core = (rel) => {
  const t = read(rel).replace(/\r\n/g, '\n');
  const a = t.indexOf(CORE_START);
  const b = t.indexOf(CORE_END);
  return a >= 0 && b > a ? t.slice(a, b) : null;
};
const installerRel = 'Installer~/VhrSdkInstaller.cs';
if (exists(installerRel) && exists('Editor/VhrSetupCore.cs')) {
  const a = core(installerRel);
  const b = core('Editor/VhrSetupCore.cs');
  if (a === null || b === null) fail('нет маркеров VHR-SETUP-CORE в установщике или Editor/VhrSetupCore.cs');
  else if (a !== b) fail('ядро VHR-SETUP-CORE в Installer~/VhrSdkInstaller.cs и Editor/VhrSetupCore.cs различается — правьте оба файла одинаково');
  else ok('ядро установщика совпадает с Editor/VhrSetupCore.cs');

  // Встроенные в установщик значения (используются, когда сервер VHR недоступен).
  const src = read(installerRel);
  const ins = /InstallerVersion\s*=\s*"([^"]+)"/.exec(src)?.[1];
  const fb = /FallbackLatest\s*=\s*"([^"]+)"/.exec(src)?.[1];
  if (version && ins !== version) warn(`установщик: InstallerVersion = "${ins}", версия пакета ${version}`);
  if (version && fb !== version) warn(`установщик: FallbackLatest = "${fb}" — офлайн поставится не ${version}`);
  if (/^\s*using\s+(VhrGames\.Sdk|R3|VContainer)\b/m.test(src)) fail('установщик ссылается на SDK/R3/VContainer — он должен компилироваться без них');
  const fbDeps = Object.fromEntries([...src.matchAll(/new\[\]\s*\{\s*"([^"]+)",\s*"([^"]+)"\s*\}/g)].map((m) => [m[1], m[2]]));
  for (const [dep, v] of Object.entries(pkg?.dependencies ?? {})) {
    if (dep.startsWith('com.unity.')) continue;
    if (fbDeps[dep] !== v) warn(`установщик: FallbackDependencies[${dep}] = ${fbDeps[dep] ?? 'нет'}, в package.json ${v}`);
  }
  if (!warnings.some((w) => w.startsWith('установщик'))) ok('встроенные версии установщика совпадают с package.json');
} else {
  warn('нет Installer~/VhrSdkInstaller.cs или Editor/VhrSetupCore.cs — сверка ядра пропущена');
}

// ------------------------------------------------------------ итог
console.log('');
if (errors.length) {
  console.log(`ПРОВЕРКА НЕ ПРОЙДЕНА: ошибок ${errors.length}, предупреждений ${warnings.length}`);
  process.exit(1);
}
console.log(`Проверка пройдена${warnings.length ? `, предупреждений: ${warnings.length}` : ''}.`);
