// =====================================================================================
//  VHR Games SDK — установщик в один шаг (Unity 6)
//
//  1. Положите этот файл в проект — в Assets/Editor/ (подойдёт любая папка внутри Assets).
//  2. Unity скомпилирует его и предложит установить SDK — нажмите «Установить».
//     Запустить ещё раз: меню VHR → Установить SDK.
//
//  Что делает (повторный запуск безопасен — ничего не задублирует):
//   • узнаёт у сервера VHR последнюю версию SDK (нет связи — ставит встроенную версию);
//   • добавляет в Packages/manifest.json недостающие реестры OpenUPM и UnityNuGet (со скоупами)
//     и зависимости: ru.vhrgames.sdk (git, #v<версия>), R3, VContainer и ядро R3 (org.nuget.r3).
//     Чужие записи, их порядок и форматирование файла сохраняются; копия старого файла —
//     в Library/VhrSdkInstaller/;
//   • находит копии ядра R3 из NuGetForUnity / Assets/Plugins и предлагает их удалить
//     (иначе — дубли сборок R3.dll и соседних);
//   • запускает разрешение пакетов, показывает итог и удаляет сам себя (с подтверждением).
//
//  Файл самодостаточен: только UnityEngine, UnityEditor и System — без SDK, R3 и VContainer.
//  Исходник: https://github.com/Merchelago/unity-sdk/blob/main/Installer~/VhrSdkInstaller.cs
// =====================================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.Networking;

namespace VhrGames.Installer
{
    /// <summary>
    /// Установщик VHR SDK одним файлом: реестры + зависимости в <c>Packages/manifest.json</c>,
    /// удаление копий ядра R3 из NuGetForUnity, разрешение пакетов, итог, самоудаление.
    /// </summary>
    [InitializeOnLoad]
    public static class VhrSdkInstaller
    {
        /// <summary>Версия установщика (= версия SDK, вместе с которой он выпущен).</summary>
        public const string InstallerVersion = "1.10.0";

        private const string MenuPath = "VHR/Установить SDK";
        private const string VersionsUrl = "https://api.vhrweb.ru/games/api/Sdk/versions";
        private const string SdkPackage = "ru.vhrgames.sdk";
        private const string Title = "VHR — установка SDK";

        // ---- Фолбэк, если сервер VHR недоступен (офлайн или эндпоинт ещё не развёрнут) ----
        private const string FallbackLatest = "1.10.0";
        private const string FallbackGitUrl = "https://github.com/Merchelago/unity-sdk.git";

        private static readonly string[][] FallbackDependencies =
        {
            new[] { "com.cysharp.r3", "1.2.9" },
            new[] { "jp.hadashikick.vcontainer", "1.16.0" },
            new[] { "org.nuget.r3", "1.2.9" },
        };

        private static readonly Registry[] FallbackRegistries =
        {
            new Registry("OpenUPM", "https://package.openupm.com", "com.cysharp", "jp.hadashikick"),
            new Registry("UnityNuGet", "https://unitynuget-registry.openupm.com", "org.nuget"),
        };

        // ---- Состояние. SessionState переживает перезагрузку домена после разрешения пакетов ----
        private const string PendingKey = "VhrSdkInstaller.Pending";
        private const string StartedKey = "VhrSdkInstaller.StartedAt";
        private const string ExpectedKey = "VhrSdkInstaller.Expected";
        private const string BackupKey = "VhrSdkInstaller.Backup";

        private static bool _busy;

        static VhrSdkInstaller()
        {
            if (Application.isBatchMode) return;
            EditorApplication.delayCall += () =>
            {
                if (SessionState.GetBool(PendingKey, false))
                {
                    WaitForResolveAndFinish();
                    return;
                }
                if (EditorPrefs.GetBool(AutoShownKey, false)) return;
                WhenIdle(() =>
                {
                    if (EditorPrefs.GetBool(AutoShownKey, false)) return;
                    EditorPrefs.SetBool(AutoShownKey, true); // спрашиваем при импорте один раз; дальше — меню
                    Run(true);
                }, 1.0);
            };
        }

        /// <summary>Меню <c>VHR → Установить SDK</c>.</summary>
        [MenuItem(MenuPath, false, 1)]
        public static void Install() => Run(false);

        // =============================================================== поток установки

        private static void Run(bool auto)
        {
            if (_busy) return;
            if (SessionState.GetBool(PendingKey, false))
            {
                EditorUtility.DisplayDialog(Title, "Установка уже идёт — дождитесь, пока Unity разрешит пакеты.", "OK");
                return;
            }
            _busy = true;
            FetchVersions(info =>
            {
                try
                {
                    Execute(info, auto);
                }
                catch (Exception e)
                {
                    SessionState.EraseBool(PendingKey); // иначе следующий запуск решит, что установка ещё идёт
                    Debug.LogException(e);
                    EditorUtility.DisplayDialog(Title, "Установка прервана: " + e.Message, "OK");
                }
                finally
                {
                    _busy = false;
                    EditorUtility.ClearProgressBar();
                }
            });
        }

        private static void Execute(VersionsInfo info, bool auto)
        {
            var manifestPath = Path.Combine(ProjectRoot, "Packages", "manifest.json");
            if (!File.Exists(manifestPath))
            {
                EditorUtility.DisplayDialog(Title, "Не найден Packages/manifest.json — откройте этот файл в Unity-проекте.", "OK");
                return;
            }

            var original = ReadText(manifestPath, out var bom);
            Plan plan;
            try
            {
                plan = BuildPlan(original, info);
            }
            catch (FormatException e)
            {
                EditorUtility.DisplayDialog(Title,
                    "Не удалось разобрать Packages/manifest.json: " + e.Message +
                    "\n\nИсправьте файл (это JSON) и запустите снова: VHR → Установить SDK. Файл не изменён.", "OK");
                return;
            }
            var copies = VhrSetupCore.FindR3CoreCopies(ProjectRoot);

            var header = info.FromServer
                ? $"Последняя версия VHR SDK: {info.Latest}."
                : $"Сервер VHR недоступен ({info.Error}) — ставлю встроенную в установщик версию {info.Latest}.";

            // Всё уже на месте — только предложить убрать установщик.
            if (plan.Changes.Count == 0 && copies.Count == 0)
            {
                if (auto && !plan.SdkPresent) return;
                var done = new StringBuilder()
                    .AppendLine($"VHR SDK уже установлен и настроен: {plan.SdkState}.")
                    .AppendLine(header);
                foreach (var n in plan.Notes) done.AppendLine("• " + n);
                done.AppendLine().AppendLine("Обновления — меню VHR → Обновление SDK.")
                    .AppendLine().Append("Установщик больше не нужен — удалить его из проекта?");
                if (EditorUtility.DisplayDialog(Title, done.ToString(), "Удалить установщик", "Оставить"))
                    DeleteSelf();
                return;
            }

            var sb = new StringBuilder().AppendLine(header).AppendLine();
            if (plan.Changes.Count > 0)
            {
                sb.AppendLine("Изменения в Packages/manifest.json:");
                foreach (var c in plan.Changes) sb.AppendLine("  " + c);
            }
            foreach (var n in plan.Notes) sb.AppendLine("• " + n);
            if (copies.Count > 0)
                sb.AppendLine().AppendLine($"Найдены копии ядра R3 вне Package Manager ({copies.Count}) — следующим шагом спрошу, удалить ли их.");
            if (plan.Changes.Count > 0)
                sb.AppendLine().Append("Копия текущего manifest.json сохранится в Library/VhrSdkInstaller/.");

            if (!EditorUtility.DisplayDialog(Title, sb.ToString(), "Установить", "Отмена"))
            {
                Debug.Log("[VHR] Установка SDK отменена. Запустить снова: меню VHR → Установить SDK.");
                return;
            }

            var removeCopies = false;
            if (copies.Count > 0)
            {
                var list = string.Join("\n", copies.Take(12).Select(c => "  " + c.AssetPath + (c.IsFolder ? "/" : "")));
                if (copies.Count > 12) list += $"\n  … и ещё {copies.Count - 12}";
                removeCopies = EditorUtility.DisplayDialog(Title,
                    "Ядро R3 теперь ставится пакетом org.nuget.r3 из реестра UnityNuGet. Эти копии (NuGetForUnity, " +
                    "Assets/Plugins) дадут дубли сборок — ошибку «Multiple precompiled assemblies with the same name»:\n\n" +
                    list + "\n\nУдалить их? Записи в packages.config NuGetForUnity тоже уберу, чтобы он не вернул копии.",
                    "Удалить копии", "Оставить");
            }

            if (plan.Changes.Count == 0 && !removeCopies)
            {
                EditorUtility.DisplayDialog(Title,
                    "Менять нечего: manifest.json уже настроен, а копии R3 вы решили оставить. Если появятся ошибки " +
                    "«Multiple precompiled assemblies», запустите VHR → Установить SDK и удалите копии.", "OK");
                return;
            }

            // ---- применяем ----
            SessionState.SetBool(PendingKey, true);
            SessionState.SetFloat(StartedKey, (float)EditorApplication.timeSinceStartup);
            SessionState.SetString(ExpectedKey, string.Join("\n", plan.Expected.Select(kv => kv.Key + "=" + kv.Value)));
            if (plan.Changes.Count > 0)
            {
                SessionState.SetString(BackupKey, Backup(manifestPath));
                WriteText(manifestPath, plan.NewManifest, bom);
                Debug.Log("[VHR] Packages/manifest.json обновлён:\n" + string.Join("\n", plan.Changes));
            }
            if (removeCopies) DeleteCopies(copies);

            Client.Resolve(); // Unity скачает пакеты (git, OpenUPM, UnityNuGet) и перекомпилирует скрипты
            WaitForResolveAndFinish();
        }

        // Ждём, пока Unity скачает пакеты и перекомпилирует скрипты (домен может перезагрузиться —
        // тогда статический конструктор продолжит ожидание по флагу в SessionState).
        private static void WaitForResolveAndFinish()
        {
            var expected = ParseExpected(SessionState.GetString(ExpectedKey, string.Empty));
            var lastBusy = EditorApplication.timeSinceStartup;
            var nextList = 0.0;
            ListRequest list = null;
            List<UnityEditor.PackageManager.PackageInfo> lastPackages = null;

            // Ошибку разрешения (нет тега, нет сети, нет реестра) Unity пишет в консоль — ловим её,
            // чтобы не ждать таймаута.
            string resolveError = null;
            Application.LogCallback onLog = (message, stack, type) =>
            {
                if ((type == LogType.Error || type == LogType.Exception) && message != null &&
                    (message.IndexOf("resolving packages", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     message.IndexOf(SdkPackage, StringComparison.Ordinal) >= 0))
                    resolveError = message;
            };
            Application.logMessageReceived += onLog;

            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                var now = EditorApplication.timeSinceStartup;
                var elapsed = now - SessionState.GetFloat(StartedKey, (float)now);
                if (elapsed < 0) elapsed = 0;
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    lastBusy = now;
                    return;
                }

                if (resolveError != null && now - lastBusy >= 1)
                {
                    EditorApplication.update -= tick;
                    Application.logMessageReceived -= onLog;
                    Finish(lastPackages, resolveError);
                    return;
                }

                if (list == null)
                {
                    if (elapsed < 3 || now - lastBusy < 2 || now < nextList) return;
                    list = Client.List(true, true);
                    return;
                }
                if (!list.IsCompleted) return;

                var packages = list.Status == StatusCode.Success ? list.Result.ToList() : null;
                list = null;
                if (packages != null) lastPackages = packages;
                if (!AllExpectedPresent(packages, expected) && elapsed < 240)
                {
                    nextList = now + 3; // пакеты ещё разрешаются (git-клон, скачивание)
                    return;
                }
                EditorApplication.update -= tick;
                Application.logMessageReceived -= onLog;
                Finish(packages, null);
            };
            EditorApplication.update += tick;
        }

        private static void Finish(List<UnityEditor.PackageManager.PackageInfo> packages, string resolveError)
        {
            var backup = SessionState.GetString(BackupKey, string.Empty);
            SessionState.EraseBool(PendingKey);
            SessionState.EraseFloat(StartedKey);
            SessionState.EraseString(ExpectedKey);
            SessionState.EraseString(BackupKey);

            var sdk = resolveError == null ? packages?.FirstOrDefault(p => p.name == SdkPackage) : null;
            if (sdk == null)
            {
                var reason = resolveError != null
                    ? "Unity не смогла разрешить пакеты:\n" + (resolveError.Length > 600 ? resolveError.Substring(0, 600) + "…" : resolveError)
                    : "Откройте Window → Package Manager и консоль — там причина.";
                var msg = "VHR SDK не установился. " + reason +
                          "\n\nОбычно причина — нет доступа к github.com или к реестрам (прокси, фаервол, VPN)." +
                          (string.IsNullOrEmpty(backup) ? "" : "\nПрежний manifest.json сохранён: " + backup) +
                          "\n\nПовторить: VHR → Установить SDK.";
                Debug.LogError("[VHR] " + msg);
                var backupAbs = string.IsNullOrEmpty(backup) ? null : Path.Combine(ProjectRoot, backup);
                if (backupAbs == null || !File.Exists(backupAbs))
                {
                    EditorUtility.DisplayDialog(Title, msg, "OK");
                    return;
                }
                // Неразрешимая запись ломает разрешение ВСЕХ пакетов проекта — предлагаем откат.
                if (EditorUtility.DisplayDialog(Title, msg, "Вернуть прежний manifest.json", "Оставить как есть"))
                {
                    try
                    {
                        File.Copy(backupAbs, Path.Combine(ProjectRoot, "Packages", "manifest.json"), true);
                        Debug.Log("[VHR] Packages/manifest.json восстановлен из " + backup);
                        Client.Resolve();
                    }
                    catch (Exception e)
                    {
                        Debug.LogError("[VHR] Не удалось восстановить manifest.json: " + e.Message + ". Копия: " + backup);
                    }
                }
                return;
            }

            var lines = new StringBuilder();
            foreach (var name in new[] { SdkPackage, "com.cysharp.r3", "org.nuget.r3", "jp.hadashikick.vcontainer" })
            {
                var p = packages.FirstOrDefault(x => x.name == name);
                lines.AppendLine(p != null ? $"  {name} {p.version} ({p.source})" : $"  {name} — нет");
            }
            var errors = packages.Where(p => p.errors != null && p.errors.Length > 0)
                .Select(p => $"  {p.name}: {string.Join("; ", p.errors.Select(e => e.message))}").ToList();
            var copiesLeft = VhrSetupCore.FindR3CoreCopies(ProjectRoot);
            var compileFailed = EditorUtility.scriptCompilationFailed;

            Debug.Log("[VHR] Итог установки SDK:\n" + lines);
            if (errors.Count > 0 || compileFailed || copiesLeft.Count > 0)
            {
                var warn = new StringBuilder($"VHR SDK {sdk.version} добавлен, но есть проблемы:\n\n");
                foreach (var e in errors) warn.AppendLine(e);
                if (compileFailed) warn.AppendLine("• Скрипты проекта не компилируются — ошибки в консоли.");
                if (copiesLeft.Count > 0)
                    warn.AppendLine("• В Assets остались копии ядра R3 (" + string.Join(", ", copiesLeft.Take(3).Select(c => c.AssetPath)) +
                                    ") — дубли сборок. Запустите VHR → Установить SDK и удалите их.");
                warn.AppendLine().Append("Установщик оставлен в проекте: исправьте ошибки и запустите его снова.");
                Debug.LogWarning("[VHR] " + warn);
                EditorUtility.DisplayDialog(Title, warn.ToString(), "OK");
                return;
            }

            if (EditorUtility.DisplayDialog(Title,
                    $"VHR SDK {sdk.version} установлен ✓\n\n{lines}\n" +
                    "Дальше: меню VHR → Тестирование в Editor. Обновления SDK — VHR → Обновление SDK.\n\n" +
                    "Установщик больше не нужен — удалить его из проекта?",
                    "Удалить установщик", "Оставить"))
                DeleteSelf();
        }

        // =============================================================== план изменений

        private sealed class Plan
        {
            public string NewManifest;
            public List<string> Changes = new List<string>();
            public readonly List<string> Notes = new List<string>();
            public readonly Dictionary<string, string> Expected = new Dictionary<string, string>(StringComparer.Ordinal);
            public string SdkState = "не установлен";
            public bool SdkPresent;
        }

        private static Plan BuildPlan(string manifestText, VersionsInfo info)
        {
            var m = new VhrSetupCore.Manifest(manifestText);
            var plan = new Plan();

            // 1. Реестры (без них Unity не найдёт R3, VContainer и ядро R3 — и не разрешит даже git-пакет SDK).
            foreach (var r in info.Registries) m.EnsureRegistry(r.Name, r.Url, r.Scopes);

            // 2. Зависимости из этих реестров. Пакеты Unity (com.unity.*) придут вместе с SDK сами.
            foreach (var kv in info.Dependencies.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (!info.Registries.Any(r => r.Scopes.Any(s => VhrSetupCore.ScopeCovers(s, kv.Key)))) continue;
                var cur = m.GetDependency(kv.Key);
                if (cur == null)
                {
                    m.SetDependency(kv.Key, kv.Value);
                    plan.Expected[kv.Key] = kv.Value;
                }
                else if (VhrSetupCore.ClassifyDependency(cur) == VhrSetupCore.DepSource.Registry)
                {
                    if (VhrSetupCore.SemVer.Compare(cur, kv.Value) < 0)
                    {
                        m.SetDependency(kv.Key, kv.Value);
                        plan.Expected[kv.Key] = kv.Value;
                    }
                }
                else
                {
                    plan.Notes.Add($"{kv.Key}: «{cur}» — подключён не из реестра, оставляю как есть (нужна версия ≥ {kv.Value}).");
                }
            }

            // 3. Сам SDK.
            var curSdk = m.GetDependency(SdkPackage);
            if (IsEmbeddedSdk())
            {
                plan.SdkPresent = true;
                plan.SdkState = "встроенный пакет в папке Packages/";
                plan.Notes.Add("SDK лежит прямо в Packages/ (embedded) — обновляйте эту папку сами.");
            }
            else if (curSdk == null)
            {
                m.SetDependency(SdkPackage, info.GitUrl + "#v" + info.Latest);
                plan.Expected[SdkPackage] = info.Latest;
            }
            else
            {
                plan.SdkPresent = true;
                switch (VhrSetupCore.ClassifyDependency(curSdk))
                {
                    case VhrSetupCore.DepSource.Git:
                        var tag = VhrSetupCore.GitVersionTag(curSdk);
                        plan.SdkState = tag != null ? $"v{tag} (git)" : "git без версии";
                        if (tag == null)
                            plan.Notes.Add("ru.vhrgames.sdk подключён по git без тега #vX.Y.Z — версию не трогаю.");
                        else if (VhrSetupCore.SemVer.Compare(tag, info.Latest) < 0)
                        {
                            m.SetDependency(SdkPackage, VhrSetupCore.GitBase(curSdk) + "#v" + info.Latest);
                            plan.Expected[SdkPackage] = info.Latest;
                        }
                        break;
                    case VhrSetupCore.DepSource.Registry:
                        plan.SdkState = curSdk + " (реестр)";
                        if (VhrSetupCore.SemVer.Compare(curSdk, info.Latest) < 0)
                        {
                            m.SetDependency(SdkPackage, info.Latest);
                            plan.Expected[SdkPackage] = info.Latest;
                        }
                        break;
                    default:
                        plan.SdkState = "локальный пакет (" + curSdk + ")";
                        plan.Notes.Add("SDK подключён локально (file:) — версию не трогаю.");
                        break;
                }
            }

            if (!IsUnity6OrNewer())
                plan.Notes.Add($"VHR SDK требует Unity 6 (6000.0+), а у вас {Application.unityVersion}.");

            plan.Changes = m.Changes;
            plan.NewManifest = m.Text;
            return plan;
        }

        private static bool IsEmbeddedSdk()
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(Path.Combine(ProjectRoot, "Packages")))
                {
                    var pj = Path.Combine(dir, "package.json");
                    if (File.Exists(pj) && VhrSetupCore.TryParseJson(File.ReadAllText(pj), out var n) &&
                        n.GetString("name") == SdkPackage)
                        return true;
                }
            }
            catch
            {
                // нет доступа к папке — считаем, что не встроен
            }
            return false;
        }

        private static Dictionary<string, string> ParseExpected(string s)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in (s ?? string.Empty).Split('\n'))
            {
                var i = line.IndexOf('=');
                if (i > 0) d[line.Substring(0, i)] = line.Substring(i + 1);
            }
            return d;
        }

        private static bool AllExpectedPresent(List<UnityEditor.PackageManager.PackageInfo> packages,
            Dictionary<string, string> expected)
        {
            if (packages == null) return false;
            if (!packages.Any(p => p.name == SdkPackage)) return false;
            foreach (var kv in expected)
            {
                var p = packages.FirstOrDefault(x => x.name == kv.Key);
                if (p == null || VhrSetupCore.SemVer.Compare(p.version, kv.Value) < 0) return false;
            }
            return true;
        }

        // =============================================================== сервер VHR

        private sealed class Registry
        {
            public readonly string Name;
            public readonly string Url;
            public readonly List<string> Scopes;

            public Registry(string name, string url, params string[] scopes)
            {
                Name = name;
                Url = url;
                Scopes = new List<string>(scopes);
            }
        }

        private sealed class VersionsInfo
        {
            public string Latest = FallbackLatest;
            public string GitUrl = FallbackGitUrl;
            public bool FromServer;
            public string Error;
            public readonly Dictionary<string, string> Dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly List<Registry> Registries = new List<Registry>();
        }

        private static void FetchVersions(Action<VersionsInfo> done)
        {
            UnityWebRequest req;
            try
            {
                req = UnityWebRequest.Get(VersionsUrl);
                req.timeout = 10;
                req.SetRequestHeader("Accept", "application/json");
                req.SetRequestHeader("X-Vhr-Sdk-Installer", InstallerVersion);
                req.SendWebRequest();
            }
            catch (Exception e)
            {
                done(BuildInfo(null, e.Message));
                return;
            }

            EditorUtility.DisplayProgressBar(Title, "Узнаю последнюю версию VHR SDK…", 0.3f);
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (!req.isDone) return;
                EditorApplication.update -= tick;
                EditorUtility.ClearProgressBar();

                VhrSetupCore.JNode root = null;
                string error = null;
                try
                {
                    if (req.result != UnityWebRequest.Result.Success)
                        error = req.responseCode > 0 ? "HTTP " + req.responseCode : req.error ?? "нет связи";
                    else if (!VhrSetupCore.TryParseJson(req.downloadHandler.text, out root) || root.Kind != VhrSetupCore.JKind.Object)
                    {
                        root = null;
                        error = "некорректный ответ сервера";
                    }
                }
                catch (Exception e)
                {
                    error = e.Message;
                }
                finally
                {
                    req.Dispose();
                }
                done(BuildInfo(root, error));
            };
            EditorApplication.update += tick;
        }

        // Ответ сервера поверх встроенных значений: версии зависимостей — максимальные, реестры — объединение.
        private static VersionsInfo BuildInfo(VhrSetupCore.JNode root, string error)
        {
            var info = new VersionsInfo { Error = error };
            foreach (var d in FallbackDependencies) info.Dependencies[d[0]] = d[1];
            foreach (var r in FallbackRegistries) info.Registries.Add(new Registry(r.Name, r.Url, r.Scopes.ToArray()));
            if (root == null) return info;

            var latest = root.GetString("latest");
            if (VhrSetupCore.SemVer.IsValid(latest))
            {
                info.Latest = latest.Trim().TrimStart('v', 'V');
                info.FromServer = true;
            }
            var git = root.GetString("gitUrl");
            if (!string.IsNullOrWhiteSpace(git) && git.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                info.GitUrl = git.Trim();

            var deps = root.Get("dependencies");
            if (deps != null)
                foreach (var kv in deps.ToStringMap())
                {
                    if (!VhrSetupCore.SemVer.IsValid(kv.Value)) continue;
                    if (!info.Dependencies.TryGetValue(kv.Key, out var have) || VhrSetupCore.SemVer.Compare(have, kv.Value) < 0)
                        info.Dependencies[kv.Key] = kv.Value;
                }

            var regs = root.Get("registries");
            if (regs != null && regs.Kind == VhrSetupCore.JKind.Array)
                foreach (var r in regs.Items)
                {
                    var url = r.GetString("url");
                    if (string.IsNullOrWhiteSpace(url) || !url.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
                    var scopes = r.Get("scopes")?.ToStringList() ?? new List<string>();
                    var same = info.Registries.FirstOrDefault(x => VhrSetupCore.SameUrl(x.Url, url));
                    if (same == null)
                        info.Registries.Add(new Registry(r.GetString("name") ?? url, url.Trim(), scopes.ToArray()));
                    else
                        foreach (var s in scopes)
                            if (!same.Scopes.Contains(s)) same.Scopes.Add(s);
                }
            return info;
        }

        // =============================================================== файлы

        private static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        private static string AutoShownKey => "VhrSdkInstaller." + ProjectHash() + ".AutoShown";

        private static string ProjectHash()
        {
            var path = (Application.dataPath ?? string.Empty).Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
            ulong h = 14695981039346656037UL; // FNV-1a 64
            foreach (var b in Encoding.UTF8.GetBytes(path))
            {
                h ^= b;
                h *= 1099511628211UL;
            }
            return h.ToString("x16", CultureInfo.InvariantCulture);
        }

        private static bool IsUnity6OrNewer()
        {
            var v = Application.unityVersion ?? string.Empty;
            var dot = v.IndexOf('.');
            return dot > 0 && int.TryParse(v.Substring(0, dot), out var major) && major >= 6000;
        }

        private static string ReadText(string path, out bool bom)
        {
            var bytes = File.ReadAllBytes(path);
            bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var skip = bom ? 3 : 0;
            return new UTF8Encoding(false).GetString(bytes, skip, bytes.Length - skip);
        }

        private static void WriteText(string path, string text, bool bom) =>
            File.WriteAllText(path, text, new UTF8Encoding(bom));

        private static string Backup(string manifestPath)
        {
            try
            {
                var dir = Path.Combine(ProjectRoot, "Library", "VhrSdkInstaller");
                Directory.CreateDirectory(dir);
                var dst = Path.Combine(dir, "manifest-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
                File.Copy(manifestPath, dst, true);
                return "Library/VhrSdkInstaller/" + Path.GetFileName(dst);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VHR] Не удалось сохранить копию manifest.json: " + e.Message);
                return string.Empty;
            }
        }

        private static void DeleteCopies(List<VhrSetupCore.R3CoreCopy> copies)
        {
            var ids = new List<string>();
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var c in copies)
                {
                    var paths = new List<string> { c.AssetPath };
                    if (!c.IsFolder)
                        foreach (var ext in new[] { ".xml", ".pdb" })
                        {
                            var sibling = Path.ChangeExtension(c.AssetPath, ext);
                            if (File.Exists(Path.Combine(ProjectRoot, sibling))) paths.Add(sibling);
                        }
                    foreach (var p in paths)
                        if (!AssetDatabase.DeleteAsset(p)) DeleteFromDisk(p);
                    if (!string.IsNullOrEmpty(c.NuGetId)) ids.Add(c.NuGetId);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            foreach (var c in copies) RemoveEmptyParents(c.AssetPath);
            var configs = VhrSetupCore.RemoveFromNuGetPackagesConfig(ProjectRoot, ids);
            Debug.Log("[VHR] Удалены копии ядра R3:\n" + string.Join("\n", copies.Select(c => c.AssetPath)) +
                      (configs.Count > 0 ? "\nИз packages.config NuGetForUnity убраны: " + string.Join(", ", ids) : string.Empty));
            AssetDatabase.Refresh();
        }

        private static void DeleteFromDisk(string assetPath)
        {
            try
            {
                var abs = Path.Combine(ProjectRoot, assetPath);
                if (Directory.Exists(abs)) Directory.Delete(abs, true);
                else if (File.Exists(abs)) File.Delete(abs);
                if (File.Exists(abs + ".meta")) File.Delete(abs + ".meta");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VHR] Не удалось удалить {assetPath}: {e.Message}");
            }
        }

        // Пустые папки, оставшиеся от копий (например, Assets/Plugins/R3). Сами Assets/Plugins и
        // Assets/Packages не трогаем.
        private static void RemoveEmptyParents(string assetPath)
        {
            var dir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            while (!string.IsNullOrEmpty(dir) && dir.StartsWith("Assets/", StringComparison.Ordinal) &&
                   dir != "Assets/Plugins" && dir != "Assets/Packages")
            {
                var abs = Path.Combine(ProjectRoot, dir);
                if (!Directory.Exists(abs) || Directory.EnumerateFileSystemEntries(abs).Any()) break;
                if (!AssetDatabase.DeleteAsset(dir)) DeleteFromDisk(dir);
                dir = Path.GetDirectoryName(dir)?.Replace('\\', '/');
            }
        }

        private static void DeleteSelf()
        {
            EditorPrefs.DeleteKey(AutoShownKey); // если файл положат снова — он снова предложит установку
            var path = FindSelfPath();
            if (path == null)
            {
                Debug.LogWarning("[VHR] Не нашёл файл установщика — удалите VhrSdkInstaller.cs из Assets вручную.");
                return;
            }
            if (AssetDatabase.DeleteAsset(path)) Debug.Log("[VHR] Установщик удалён: " + path);
            else Debug.LogWarning("[VHR] Не удалось удалить " + path + " — удалите файл вручную.");
        }

        private static string FindSelfPath([CallerFilePath] string compiledPath = "")
        {
            var p = (compiledPath ?? string.Empty).Replace('\\', '/');
            var root = ProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            if (p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) p = p.Substring(root.Length);
            if (p.StartsWith("Assets/", StringComparison.Ordinal) && File.Exists(Path.Combine(ProjectRoot, p))) return p;

            foreach (var guid in AssetDatabase.FindAssets("VhrSdkInstaller t:MonoScript"))
            {
                var ap = AssetDatabase.GUIDToAssetPath(guid);
                if (!ap.StartsWith("Assets/", StringComparison.Ordinal) || !ap.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (File.ReadAllText(Path.Combine(ProjectRoot, ap)).Contains("namespace VhrGames.Installer")) return ap;
                }
                catch
                {
                    // недоступный файл — ищем дальше
                }
            }
            return null;
        }

        private static void WhenIdle(Action action, double minDelaySeconds)
        {
            var start = EditorApplication.timeSinceStartup;
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (EditorApplication.timeSinceStartup - start < minDelaySeconds) return;
                if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                    EditorApplication.isPlayingOrWillChangePlaymode) return;
                EditorApplication.update -= tick;
                try { action(); }
                catch (Exception e) { Debug.LogException(e); }
            };
            EditorApplication.update += tick;
        }
    }

    // >>> VHR-SETUP-CORE
    // Общее ядро установщика (Installer~/VhrSdkInstaller.cs) и окна «VHR → Обновление SDK»
    // (Editor/VhrSetupCore.cs). Текст между маркерами в обоих файлах ОДИНАКОВЫЙ — это проверяет
    // .github/scripts/check-release.mjs. Только System.*: без UnityEditor, SDK, R3 и VContainer.
    internal static class VhrSetupCore
    {
        // ================================================================= JSON

        internal enum JKind { Object, Array, String, Number, True, False, Null }

        /// <summary>Узел JSON с позицией в исходном тексте: [Start, End).</summary>
        internal sealed class JNode
        {
            public JKind Kind;
            public int Start;
            public int End;
            public string Str;
            public readonly List<JMember> Members = new List<JMember>();
            public readonly List<JNode> Items = new List<JNode>();

            public JNode Get(string key)
            {
                if (Kind != JKind.Object) return null;
                for (var i = Members.Count - 1; i >= 0; i--) // как JSON.parse: при дублях выигрывает последний
                    if (Members[i].Key == key) return Members[i].Value;
                return null;
            }

            public string GetString(string key)
            {
                var n = Get(key);
                return n != null && n.Kind == JKind.String ? n.Str : null;
            }

            public Dictionary<string, string> ToStringMap()
            {
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                if (Kind == JKind.Object)
                    foreach (var m in Members)
                        if (m.Value.Kind == JKind.String) d[m.Key] = m.Value.Str;
                return d;
            }

            public List<string> ToStringList()
            {
                var l = new List<string>();
                if (Kind == JKind.Array)
                    foreach (var n in Items)
                        if (n.Kind == JKind.String) l.Add(n.Str);
                return l;
            }
        }

        internal sealed class JMember
        {
            public string Key;
            public int KeyStart;
            public JNode Value;
        }

        /// <summary>Строгий JSON (RFC 8259). Ошибка — <see cref="FormatException"/>.</summary>
        internal static JNode ParseJson(string text)
        {
            var p = new JsonParser(text);
            p.Ws();
            var n = p.Value();
            p.Ws();
            if (p.Pos != p.Length) throw p.Err("лишние символы после JSON");
            return n;
        }

        internal static bool TryParseJson(string text, out JNode node)
        {
            try
            {
                node = ParseJson(text);
                return true;
            }
            catch (FormatException)
            {
                node = null;
                return false;
            }
        }

        internal static string Quote(string s)
        {
            var sb = new StringBuilder((s ?? string.Empty).Length + 2).Append('"');
            foreach (var c in s ?? string.Empty)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private sealed class JsonParser
        {
            private readonly string _s;
            private int _depth;
            public int Pos;

            public JsonParser(string s) => _s = s ?? string.Empty;

            public int Length => _s.Length;

            public FormatException Err(string what) => new FormatException($"{what} (символ {Pos})");

            private char Peek() => Pos < _s.Length ? _s[Pos] : '\0';

            public void Ws()
            {
                while (Pos < _s.Length)
                {
                    var c = _s[Pos];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || (c == '﻿' && Pos == 0)) Pos++;
                    else break;
                }
            }

            public JNode Value()
            {
                if (Pos >= _s.Length) throw Err("неожиданный конец JSON");
                var c = _s[Pos];
                switch (c)
                {
                    case '{': return Obj();
                    case '[': return Arr();
                    case '"':
                        var start = Pos;
                        var str = Str();
                        return new JNode { Kind = JKind.String, Start = start, End = Pos, Str = str };
                    case 't': return Lit("true", JKind.True);
                    case 'f': return Lit("false", JKind.False);
                    case 'n': return Lit("null", JKind.Null);
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return Num();
                        throw Err($"неожиданный символ '{c}'");
                }
            }

            private JNode Obj()
            {
                if (++_depth > 64) throw Err("слишком глубокая вложенность");
                var n = new JNode { Kind = JKind.Object, Start = Pos };
                Pos++;
                Ws();
                if (Peek() == '}')
                {
                    Pos++;
                }
                else
                {
                    while (true)
                    {
                        Ws();
                        if (Peek() != '"') throw Err("ожидалось имя поля в кавычках");
                        var keyStart = Pos;
                        var key = Str();
                        Ws();
                        if (Peek() != ':') throw Err("ожидалось ':'");
                        Pos++;
                        Ws();
                        n.Members.Add(new JMember { Key = key, KeyStart = keyStart, Value = Value() });
                        Ws();
                        var c = Peek();
                        Pos++;
                        if (c == ',') continue;
                        if (c == '}') break;
                        Pos--;
                        throw Err("ожидалось ',' или '}'");
                    }
                }
                n.End = Pos;
                _depth--;
                return n;
            }

            private JNode Arr()
            {
                if (++_depth > 64) throw Err("слишком глубокая вложенность");
                var n = new JNode { Kind = JKind.Array, Start = Pos };
                Pos++;
                Ws();
                if (Peek() == ']')
                {
                    Pos++;
                }
                else
                {
                    while (true)
                    {
                        Ws();
                        n.Items.Add(Value());
                        Ws();
                        var c = Peek();
                        Pos++;
                        if (c == ',') continue;
                        if (c == ']') break;
                        Pos--;
                        throw Err("ожидалось ',' или ']'");
                    }
                }
                n.End = Pos;
                _depth--;
                return n;
            }

            private string Str()
            {
                Pos++; // открывающая кавычка
                var sb = new StringBuilder();
                while (true)
                {
                    if (Pos >= _s.Length) throw Err("незакрытая строка");
                    var c = _s[Pos++];
                    if (c == '"') return sb.ToString();
                    if (c < ' ') throw Err("управляющий символ в строке");
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (Pos >= _s.Length) throw Err("незакрытая строка");
                    var e = _s[Pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (Pos + 4 > _s.Length || !int.TryParse(_s.Substring(Pos, 4), NumberStyles.AllowHexSpecifier,
                                    CultureInfo.InvariantCulture, out var code))
                                throw Err("неверная последовательность \\u");
                            sb.Append((char)code);
                            Pos += 4;
                            break;
                        default: throw Err("неверная escape-последовательность");
                    }
                }
            }

            private JNode Num()
            {
                var start = Pos;
                if (Peek() == '-') Pos++;
                if (!Digits()) throw Err("неверное число");
                if (Peek() == '.')
                {
                    Pos++;
                    if (!Digits()) throw Err("неверное число");
                }
                if (Peek() == 'e' || Peek() == 'E')
                {
                    Pos++;
                    if (Peek() == '+' || Peek() == '-') Pos++;
                    if (!Digits()) throw Err("неверное число");
                }
                return new JNode { Kind = JKind.Number, Start = start, End = Pos, Str = _s.Substring(start, Pos - start) };
            }

            private bool Digits()
            {
                var s = Pos;
                while (Pos < _s.Length && _s[Pos] >= '0' && _s[Pos] <= '9') Pos++;
                return Pos > s;
            }

            private JNode Lit(string word, JKind kind)
            {
                if (string.CompareOrdinal(_s, Pos, word, 0, word.Length) != 0) throw Err("неожиданный символ");
                var n = new JNode { Kind = kind, Start = Pos, End = Pos + word.Length };
                Pos += word.Length;
                return n;
            }
        }

        // ================================================================= SemVer

        /// <summary>SemVer 2.0: «1.10.0», «v1.10.0», «1.10.0-preview.1». Некорректная строка меньше любой корректной.</summary>
        internal static class SemVer
        {
            public static bool TryParse(string s, out int[] core, out string pre)
            {
                core = null;
                pre = null;
                if (string.IsNullOrWhiteSpace(s)) return false;
                s = s.Trim();
                if (s[0] == 'v' || s[0] == 'V') s = s.Substring(1);
                var plus = s.IndexOf('+');
                if (plus >= 0) s = s.Substring(0, plus);
                var dash = s.IndexOf('-');
                if (dash >= 0)
                {
                    pre = s.Substring(dash + 1);
                    s = s.Substring(0, dash);
                    if (pre.Length == 0) return false;
                }
                var parts = s.Split('.');
                if (parts.Length < 2 || parts.Length > 3) return false;
                var nums = new int[3];
                for (var i = 0; i < parts.Length; i++)
                    if (parts[i].Length == 0 || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i]))
                        return false;
                core = nums;
                return true;
            }

            public static bool IsValid(string s) => TryParse(s, out _, out _);

            /// <summary>&lt;0 — a меньше b, 0 — равны, &gt;0 — a больше.</summary>
            public static int Compare(string a, string b)
            {
                var okA = TryParse(a, out var ca, out var pa);
                var okB = TryParse(b, out var cb, out var pb);
                if (!okA || !okB) return okA == okB ? 0 : (okA ? 1 : -1);
                for (var i = 0; i < 3; i++)
                    if (ca[i] != cb[i]) return ca[i] < cb[i] ? -1 : 1;
                if (pa == null || pb == null) return pa == pb ? 0 : (pa == null ? 1 : -1); // релиз > пре-релиза
                var xa = pa.Split('.');
                var xb = pb.Split('.');
                for (var i = 0; i < Math.Min(xa.Length, xb.Length); i++)
                {
                    var na = int.TryParse(xa[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ia);
                    var nb = int.TryParse(xb[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ib);
                    int r;
                    if (na && nb) r = ia.CompareTo(ib);
                    else if (na != nb) r = na ? -1 : 1;
                    else r = string.CompareOrdinal(xa[i], xb[i]);
                    if (r != 0) return r < 0 ? -1 : 1;
                }
                return xa.Length.CompareTo(xb.Length);
            }
        }

        // ================================================================= зависимости

        internal enum DepSource { Registry, Git, Local, Other }

        /// <summary>Вид записи в dependencies: «1.2.9» — реестр, «https://…git#v1.9.0» — git, «file:…» — локальный.</summary>
        internal static DepSource ClassifyDependency(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return DepSource.Other;
            var v = value.Trim();
            if (v.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return DepSource.Local;
            if (char.IsDigit(v[0]) && SemVer.IsValid(v)) return DepSource.Registry;
            if (v.StartsWith("git", StringComparison.OrdinalIgnoreCase) || v.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                v.StartsWith("ssh:", StringComparison.OrdinalIgnoreCase) || v.Contains(".git"))
                return DepSource.Git;
            return DepSource.Other;
        }

        /// <summary>Версия из git-ссылки: «…unity-sdk.git#v1.9.0» → «1.9.0»; без тега — null.</summary>
        internal static string GitVersionTag(string value)
        {
            var i = (value ?? string.Empty).LastIndexOf('#');
            if (i < 0) return null;
            var frag = value.Substring(i + 1).Trim();
            return SemVer.IsValid(frag) ? frag.TrimStart('v', 'V') : null;
        }

        /// <summary>Git-ссылка без «#тега».</summary>
        internal static string GitBase(string value)
        {
            var i = (value ?? string.Empty).IndexOf('#');
            return i < 0 ? value : value.Substring(0, i);
        }

        /// <summary>Покрывает ли скоуп реестра пакет: «com.cysharp» → «com.cysharp.r3».</summary>
        internal static bool ScopeCovers(string scope, string packageName)
        {
            if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrEmpty(packageName)) return false;
            scope = scope.Trim();
            return packageName == scope || packageName.StartsWith(scope + ".", StringComparison.Ordinal);
        }

        internal static bool SameUrl(string a, string b) =>
            string.Equals((a ?? string.Empty).Trim().TrimEnd('/'), (b ?? string.Empty).Trim().TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);

        // ================================================================= manifest.json

        /// <summary>
        /// Точечная правка <c>Packages/manifest.json</c>: вставки и замены прямо в тексте, без
        /// пересериализации — порядок, отступы, переводы строк и чужие записи остаются как были.
        /// После каждой правки текст заново разбирается (битый JSON не получится).
        /// </summary>
        internal sealed class Manifest
        {
            private readonly string _nl;
            private readonly string _unit = "  ";

            /// <summary>Человекочитаемый список изменений («+ …», «~ …»).</summary>
            public readonly List<string> Changes = new List<string>();

            public string Text { get; private set; }

            public Manifest(string text)
            {
                Text = text ?? string.Empty;
                var root = ParseJson(Text);
                if (root.Kind != JKind.Object) throw new FormatException("корень manifest.json должен быть объектом");
                _nl = Text.Contains("\r\n") ? "\r\n" : "\n";
                if (root.Members.Count > 0 && !SameLine(root.Start, root.Members[0].KeyStart))
                {
                    var ind = LineIndent(root.Members[0].KeyStart);
                    if (ind.Length > 0) _unit = ind;
                }
            }

            public JNode Root => ParseJson(Text);

            public string GetDependency(string name) => Root.Get("dependencies")?.GetString(name);

            public Dictionary<string, string> GetDependencies() =>
                Root.Get("dependencies")?.ToStringMap() ?? new Dictionary<string, string>(StringComparer.Ordinal);

            /// <summary>Добавляет или меняет зависимость. Новая встаёт по алфавиту, если список отсортирован.</summary>
            public bool SetDependency(string name, string value)
            {
                var root = Root;
                var deps = root.Get("dependencies");
                if (deps == null)
                {
                    InsertMember(root, "dependencies", ind => "{" + _nl + ind + _unit + Quote(name) + ": " + Quote(value) + _nl + ind + "}", false);
                    Changes.Add($"+ {name}: {value}");
                    return true;
                }
                if (deps.Kind != JKind.Object) throw new FormatException("\"dependencies\" в manifest.json — не объект");
                var cur = deps.Get(name);
                if (cur == null)
                {
                    InsertMember(deps, name, _ => Quote(value), true);
                    Changes.Add($"+ {name}: {value}");
                    return true;
                }
                if (cur.Kind == JKind.String && cur.Str == value) return false;
                var old = cur.Kind == JKind.String ? cur.Str : Text.Substring(cur.Start, cur.End - cur.Start);
                Replace(cur.Start, cur.End, Quote(value));
                Changes.Add($"~ {name}: {old} → {value}");
                return true;
            }

            /// <summary>Покрыт ли пакет/скоуп каким-нибудь scoped-реестром проекта.</summary>
            public bool IsScopeCovered(string scopeOrPackage)
            {
                var regs = Root.Get("scopedRegistries");
                if (regs == null || regs.Kind != JKind.Array) return false;
                foreach (var r in regs.Items)
                {
                    var scopes = r.Get("scopes");
                    if (scopes == null) continue;
                    foreach (var s in scopes.ToStringList())
                        if (ScopeCovers(s, scopeOrPackage)) return true;
                }
                return false;
            }

            /// <summary>
            /// Добавляет недостающие скоупы: в реестр с тем же URL, иначе — новым реестром. Скоуп,
            /// уже покрытый любым реестром (тем же или более общим), не трогаем.
            /// </summary>
            public bool EnsureRegistry(string name, string url, IList<string> scopes)
            {
                if (string.IsNullOrWhiteSpace(url) || scopes == null) return false;
                var missing = new List<string>();
                foreach (var raw in scopes)
                {
                    var s = (raw ?? string.Empty).Trim();
                    if (s.Length > 0 && !missing.Contains(s) && !IsScopeCovered(s)) missing.Add(s);
                }
                if (missing.Count == 0) return false;

                var root = Root;
                var regs = root.Get("scopedRegistries");
                if (regs == null)
                {
                    InsertMember(root, "scopedRegistries",
                        ind => "[" + _nl + ind + _unit + RenderRegistry(name, url, missing, ind + _unit) + _nl + ind + "]", false);
                    Changes.Add($"+ реестр {name} ({url}): {string.Join(", ", missing)}");
                    return true;
                }
                if (regs.Kind != JKind.Array) throw new FormatException("\"scopedRegistries\" в manifest.json — не массив");

                var idx = regs.Items.FindIndex(r => r.Kind == JKind.Object && SameUrl(r.GetString("url"), url));
                if (idx < 0)
                {
                    InsertItem(regs, ind => RenderRegistry(name, url, missing, ind));
                    Changes.Add($"+ реестр {name} ({url}): {string.Join(", ", missing)}");
                    return true;
                }

                var regName = regs.Items[idx].GetString("name") ?? url;
                foreach (var s in missing)
                {
                    var reg = Root.Get("scopedRegistries").Items[idx];
                    var sc = reg.Get("scopes");
                    if (sc == null) InsertMember(reg, "scopes", _ => "[" + Quote(s) + "]", false);
                    else if (sc.Kind == JKind.Array) InsertItem(sc, _ => Quote(s));
                    else throw new FormatException("\"scopes\" реестра " + regName + " — не массив");
                }
                Changes.Add($"+ скоупы в реестр {regName}: {string.Join(", ", missing)}");
                return true;
            }

            private string RenderRegistry(string name, string url, List<string> scopes, string ind)
            {
                var i1 = ind + _unit;
                var i2 = i1 + _unit;
                var sb = new StringBuilder("{").Append(_nl)
                    .Append(i1).Append("\"name\": ").Append(Quote(name)).Append(',').Append(_nl)
                    .Append(i1).Append("\"url\": ").Append(Quote(url)).Append(',').Append(_nl)
                    .Append(i1).Append("\"scopes\": [").Append(_nl);
                for (var k = 0; k < scopes.Count; k++)
                    sb.Append(i2).Append(Quote(scopes[k])).Append(k < scopes.Count - 1 ? "," : string.Empty).Append(_nl);
                return sb.Append(i1).Append(']').Append(_nl).Append(ind).Append('}').ToString();
            }

            // render(indent) — текст значения для строки с отступом indent.
            private void InsertMember(JNode obj, string key, Func<string, string> render, bool sorted)
            {
                var members = obj.Members;
                if (members.Count == 0)
                {
                    var objIndent = LineIndent(obj.Start);
                    var child = objIndent + _unit;
                    Replace(obj.Start + 1, obj.End - 1, _nl + child + Quote(key) + ": " + render(child) + _nl + objIndent);
                    return;
                }

                var at = members.Count;
                if (sorted && IsSorted(members))
                    for (var i = 0; i < members.Count; i++)
                        if (string.CompareOrdinal(members[i].Key, key) > 0)
                        {
                            at = i;
                            break;
                        }

                if (at < members.Count)
                {
                    var m = members[at];
                    var prevEnd = at > 0 ? members[at - 1].Value.End : obj.Start;
                    if (SameLine(prevEnd, m.KeyStart))
                    {
                        Insert(m.KeyStart, Quote(key) + ": " + render(LineIndent(m.KeyStart) + _unit) + ", ");
                    }
                    else
                    {
                        var ind = LineIndent(m.KeyStart);
                        Insert(m.KeyStart, Quote(key) + ": " + render(ind) + "," + _nl + ind);
                    }
                    return;
                }

                var last = members[members.Count - 1];
                if (SameLine(obj.Start, last.KeyStart))
                {
                    Insert(last.Value.End, ", " + Quote(key) + ": " + render(LineIndent(obj.Start) + _unit));
                }
                else
                {
                    var ind = LineIndent(last.KeyStart);
                    Insert(last.Value.End, "," + _nl + ind + Quote(key) + ": " + render(ind));
                }
            }

            private void InsertItem(JNode arr, Func<string, string> render)
            {
                if (arr.Items.Count == 0)
                {
                    var arrIndent = LineIndent(arr.Start);
                    var child = arrIndent + _unit;
                    Replace(arr.Start + 1, arr.End - 1, _nl + child + render(child) + _nl + arrIndent);
                    return;
                }
                var last = arr.Items[arr.Items.Count - 1];
                if (SameLine(arr.Start, last.Start))
                {
                    Insert(last.End, ", " + render(LineIndent(arr.Start) + _unit));
                }
                else
                {
                    var ind = LineIndent(last.Start);
                    Insert(last.End, "," + _nl + ind + render(ind));
                }
            }

            private static bool IsSorted(List<JMember> members)
            {
                for (var i = 1; i < members.Count; i++)
                    if (string.CompareOrdinal(members[i - 1].Key, members[i].Key) > 0) return false;
                return true;
            }

            private bool SameLine(int a, int b)
            {
                for (var i = Math.Min(a, b); i < Math.Max(a, b) && i < Text.Length; i++)
                    if (Text[i] == '\n') return false;
                return true;
            }

            private string LineIndent(int pos)
            {
                var lineStart = pos;
                while (lineStart > 0 && Text[lineStart - 1] != '\n') lineStart--;
                var e = lineStart;
                while (e < Text.Length && (Text[e] == ' ' || Text[e] == '\t')) e++;
                return Text.Substring(lineStart, e - lineStart);
            }

            private void Insert(int pos, string s) => Replace(pos, pos, s);

            private void Replace(int start, int end, string with)
            {
                var updated = Text.Substring(0, start) + with + Text.Substring(end);
                if (!TryParseJson(updated, out _))
                    throw new InvalidOperationException("внутренняя ошибка правки manifest.json — файл не изменён");
                Text = updated;
            }
        }

        // ================================================================= копии ядра R3

        /// <summary>
        /// Сборки ядра R3, которые в Unity 2021.2+ даёт пакет org.nuget.r3 (и его зависимости).
        /// Те же DLL в Assets (NuGetForUnity, Assets/Plugins, .unitypackage) — дубли.
        /// </summary>
        internal static readonly string[] R3CoreAssemblies =
        {
            "R3.dll",
            "Microsoft.Bcl.TimeProvider.dll",
            "Microsoft.Bcl.AsyncInterfaces.dll",
            "System.Threading.Channels.dll",
            "System.ComponentModel.Annotations.dll",
            "System.Runtime.CompilerServices.Unsafe.dll",
        };

        internal sealed class R3CoreCopy
        {
            /// <summary>Что удалять: папка пакета NuGetForUnity («Assets/Packages/R3.1.2.9») или сама DLL.</summary>
            public string AssetPath;
            public bool IsFolder;
            /// <summary>Id пакета NuGetForUnity (для packages.config) или null.</summary>
            public string NuGetId;
            public readonly List<string> Assemblies = new List<string>();
        }

        // «…/<Id>.<Версия>/lib/…» — раскладка NuGetForUnity (папка репозитория может быть любой).
        private static readonly Regex NuGetPackageFolder = new Regex(
            @"^(?<dir>(?:.*/)?(?<id>[^/]+?)\.(?<ver>\d+(?:\.\d+){1,3}(?:-[0-9A-Za-z.\-]+)?))/lib/",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static List<R3CoreCopy> FindR3CoreCopies(string projectRoot)
        {
            var result = new List<R3CoreCopy>();
            var assets = Path.Combine(projectRoot, "Assets");
            if (!Directory.Exists(assets)) return result;

            var names = new HashSet<string>(R3CoreAssemblies, StringComparer.OrdinalIgnoreCase);
            var byPath = new Dictionary<string, R3CoreCopy>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in EnumerateVisibleFiles(assets, "*.dll"))
            {
                var fileName = Path.GetFileName(file);
                if (!names.Contains(fileName)) continue;
                var rel = "Assets/" + file.Substring(assets.Length).Replace('\\', '/').TrimStart('/');
                var unit = rel;
                var folder = false;
                string id = null;
                var m = NuGetPackageFolder.Match(rel);
                if (m.Success)
                {
                    unit = m.Groups["dir"].Value;
                    folder = true;
                    id = m.Groups["id"].Value;
                }
                if (!byPath.TryGetValue(unit, out var copy))
                {
                    copy = new R3CoreCopy { AssetPath = unit, IsFolder = folder, NuGetId = id };
                    byPath[unit] = copy;
                    result.Add(copy);
                }
                if (!copy.Assemblies.Contains(fileName)) copy.Assemblies.Add(fileName);
            }
            return result;
        }

        /// <summary>Убирает &lt;package id="…"/&gt; из packages.config NuGetForUnity, чтобы он не восстановил удалённое.</summary>
        internal static List<string> RemoveFromNuGetPackagesConfig(string projectRoot, ICollection<string> ids)
        {
            var changed = new List<string>();
            if (ids == null || ids.Count == 0) return changed;
            var set = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            var assets = Path.Combine(projectRoot, "Assets");
            if (!Directory.Exists(assets)) return changed;
            var entry = new Regex(@"^[ \t]*<package\s[^>]*?\bid\s*=\s*""(?<id>[^""]+)""[^>]*?/>[ \t]*(\r?\n)?",
                RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
            foreach (var file in EnumerateVisibleFiles(assets, "packages.config"))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var updated = entry.Replace(text, m => set.Contains(m.Groups["id"].Value) ? string.Empty : m.Value);
                    if (updated == text) continue;
                    File.WriteAllText(file, updated);
                    changed.Add(file);
                }
                catch (IOException)
                {
                    // файл занят — пропускаем, NuGetForUnity просто попробует восстановить пакет
                }
                catch (UnauthorizedAccessException)
                {
                    // нет прав — аналогично
                }
            }
            return changed;
        }

        // Файлы, которые видит Unity: без скрытых папок («.git», «.idea») и папок с «~» на конце.
        private static IEnumerable<string> EnumerateVisibleFiles(string dir, string pattern)
        {
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                string[] files, dirs;
                try
                {
                    files = Directory.GetFiles(d, pattern);
                    dirs = Directory.GetDirectories(d);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (var f in files) yield return f;
                foreach (var sub in dirs)
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith("~", StringComparison.Ordinal)) continue;
                    stack.Push(sub);
                }
            }
        }
    }
    // <<< VHR-SETUP-CORE
}
#endif
