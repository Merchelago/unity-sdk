using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace VhrGames.Sdk.Editor
{
    /// <summary>
    /// Окно <c>VHR → Обновление SDK</c>: установленная, последняя и минимальная поддерживаемая
    /// версии, что нового, устаревшие API, которые встречаются в коде проекта, состояние
    /// зависимостей и реестров — и обновление одной кнопкой (<see cref="VhrSdkUpdater"/>).
    /// </summary>
    public sealed class VhrSdkUpdateWindow : EditorWindow
    {
        internal const string MenuPath = "VHR/Обновление SDK";
        private const string InstallerUrl = "https://vhrgames.ru/downloads/VhrSdkInstaller.cs";

        private VhrSdkVersionsInfo _info;
        private VhrSdkInstall _install;
        private bool _fetching;
        private ListRequest _listRequest;
        private List<PackageInfo> _packages;
        private List<string> _missingRegistries = new List<string>();
        private List<VhrSetupCore.R3CoreCopy> _copies = new List<VhrSetupCore.R3CoreCopy>();
        private string _notesText;
        private Vector2 _scroll;
        private double _nextRepaint;

        // Поиск устаревших API в Assets/**/*.cs — по кусочку за кадр, чтобы не подвешивать редактор.
        private readonly Dictionary<string, List<string>> _hits = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private List<string> _scanFiles;
        private int _scanIndex;
        private Regex _scanRegex;

        /// <summary>Открывает окно (меню <c>VHR → Обновление SDK</c>).</summary>
        [MenuItem(MenuPath, priority = 30)]
        public static void Open()
        {
            var w = GetWindow<VhrSdkUpdateWindow>(false, "VHR — обновление SDK", true);
            w.minSize = new Vector2(500, 540);
            w.Show();
        }

        private void OnEnable()
        {
            VhrSdkUpdater.Changed += OnUpdaterChanged;
            EditorApplication.update += OnEditorUpdate;
            Reload(VhrSdkVersionService.IsCheckDue());
        }

        private void OnDisable()
        {
            VhrSdkUpdater.Changed -= OnUpdaterChanged;
            EditorApplication.update -= OnEditorUpdate;
            StopScan();
        }

        private void OnUpdaterChanged() => Repaint();

        private void OnEditorUpdate()
        {
            if (_listRequest != null && _listRequest.IsCompleted)
            {
                _packages = _listRequest.Status == StatusCode.Success ? _listRequest.Result.ToList() : null;
                _listRequest = null;
                Repaint();
            }
            if (VhrSdkUpdater.InProgress && EditorApplication.timeSinceStartup > _nextRepaint)
            {
                _nextRepaint = EditorApplication.timeSinceStartup + 0.5;
                Repaint();
            }
        }

        private void Reload(bool askServer)
        {
            _install = VhrSdkInstall.Detect();
            _copies = VhrSetupCore.FindR3CoreCopies(VhrSdkProject.Root);
            _listRequest = Client.List(true, true);
            Apply(VhrSdkVersionService.LoadCached());
            if (_info == null || askServer)
            {
                _fetching = true;
                VhrSdkVersionService.FetchAsync(info =>
                {
                    _fetching = false;
                    if (this == null) return; // окно закрыли, пока шёл запрос
                    Apply(info);
                    Repaint();
                });
            }
        }

        private void Apply(VhrSdkVersionsInfo info)
        {
            if (info == null) return;
            _info = info;
            _notesText = MarkdownToText(info.ReleaseNotes);
            _missingRegistries = info.IsKnown ? VhrSdkProject.MissingRegistries(info) : new List<string>();
            StartScan();
        }

        // ================================================================= GUI

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Обновление VHR SDK", EditorStyles.boldLabel);

            DrawVersions();
            EditorGUILayout.Space(6);
            DrawUpdate();
            DrawCopies();
            EditorGUILayout.Space(8);
            DrawDeprecations();
            DrawNotes();
            EditorGUILayout.Space(8);
            DrawDependencies();
            EditorGUILayout.Space(8);
            DrawHowItWorks();
            EditorGUILayout.Space(8);
            EditorGUILayout.EndScrollView();
        }

        private void DrawVersions()
        {
            var installed = VhrSdkVersionService.InstalledVersion;
            var source = _install?.SourceLabel ?? "—";
            Row("Установлена", installed + (string.IsNullOrEmpty(_install?.PackageId) ? "" : "   ·   " + source));
            if (_install?.Source == PackageSource.Git && !string.IsNullOrEmpty(_install.PackageId))
                Row("", _install.PackageId.Substring(_install.PackageId.IndexOf('@') + 1), EditorStyles.miniLabel);
            else if (string.IsNullOrEmpty(_install?.PackageId))
                Row("Источник", source);

            var known = _info != null && _info.IsKnown;
            Row("Последняя", known ? _info.Latest + FormatReleased(_info.ReleasedAt) : "—");
            Row("Минимальная поддерживаемая", known && _info.HasMinSupported ? _info.MinSupported : "—");

            using (new EditorGUILayout.HorizontalScope())
            {
                string when;
                if (_fetching) when = "Спрашиваю сервер VHR…";
                else if (_info == null) when = "";
                else if (_info.Source == VhrSdkVersionsSource.Server) when = "Проверено только что.";
                else if (_info.Source == VhrSdkVersionsSource.Cache)
                    when = $"Данные от {_info.FetchedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}" +
                           (string.IsNullOrEmpty(_info.Error) ? "." : $" (сейчас сервер не ответил: {_info.Error}).");
                else when = "Сервер VHR недоступен: " + (_info.Error ?? "нет связи") + ".";
                EditorGUILayout.LabelField(when, EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUI.DisabledScope(_fetching))
                {
                    if (GUILayout.Button("Проверить сейчас", GUILayout.Width(130))) Reload(true);
                }
            }

            if (_info == null || !_info.IsKnown)
            {
                if (!_fetching)
                    EditorGUILayout.HelpBox("Нет сведений о версиях: сервер VHR не отвечает. Работать и собирать игру это не мешает — " +
                                            "проверка повторится позже.", MessageType.None);
                return;
            }
            if (_info.IsBelowMinimum(installed))
                EditorGUILayout.HelpBox($"Версия {installed} ниже минимальной поддерживаемой ({_info.MinSupported}): платформа отклонит " +
                                        "загрузку новой сборки игры, а WebGL-сборка в Unity остановится. Обновите SDK.", MessageType.Error);
            else if (_info.IsUpdateAvailable(installed))
                EditorGUILayout.HelpBox($"Доступна версия {_info.Latest}.", MessageType.Warning);
            else
                EditorGUILayout.HelpBox("Установлена последняя версия ✓", MessageType.Info);
        }

        private void DrawUpdate()
        {
            if (VhrSdkUpdater.InProgress || !string.IsNullOrEmpty(VhrSdkUpdater.Status))
            {
                var dots = new string('.', 1 + (int)(EditorApplication.timeSinceStartup * 2) % 3);
                EditorGUILayout.HelpBox((string.IsNullOrEmpty(VhrSdkUpdater.Status) ? "Обновление" : VhrSdkUpdater.Status) + dots +
                                        "\nМожно продолжать работать: после обновления Unity перекомпилирует скрипты и покажет итог.",
                    MessageType.Info);
            }
            else if (!string.IsNullOrEmpty(VhrSdkUpdater.LastResult))
            {
                EditorGUILayout.HelpBox(VhrSdkUpdater.LastResult, VhrSdkUpdater.LastResultIsError ? MessageType.Error : MessageType.Info);
            }

            if (_info == null || !_info.IsUpdateAvailable(VhrSdkVersionService.InstalledVersion) || _install == null) return;

            if (_install.CanAutoUpdate)
            {
                using (new EditorGUI.DisabledScope(VhrSdkUpdater.InProgress))
                {
                    if (GUILayout.Button($"Обновить до {_info.Latest}", GUILayout.Height(30)))
                        VhrSdkUpdater.Start(_info);
                }
                var extra = _missingRegistries.Count > 0
                    ? " Перед этим в Packages/manifest.json добавятся реестры: " + string.Join(", ", _missingRegistries) + "."
                    : string.Empty;
                EditorGUILayout.LabelField(
                    (_install.Source == PackageSource.Git
                        ? $"Пакет переключится на тег v{_info.Latest} того же git-репозитория; "
                        : $"Пакет обновится до {_info.Latest} из реестра; ") +
                    "зависимости, прибитые в manifest.json ниже нужной версии, поднимутся вместе с ним." + extra,
                    EditorStyles.wordWrappedMiniLabel);
                return;
            }

            var git = _info.GitUrl + "#v" + _info.Latest;
            string text;
            switch (_install.Source)
            {
                case PackageSource.Embedded:
                    text = "SDK лежит прямо в папке Packages/ (embedded) — Package Manager его не обновляет. Замените содержимое " +
                           $"папки файлами тега v{_info.Latest} или удалите папку и подключите SDK по git:\n{git}";
                    break;
                case PackageSource.Local:
                case PackageSource.LocalTarball:
                    text = $"SDK подключён из локальной папки или архива. Обновите их до тега v{_info.Latest} или переключитесь на git: " +
                           $"Window → Package Manager → «+» → «Add package from git URL…» →\n{git}";
                    break;
                default:
                    text = "Файлы SDK скопированы в Assets — так они не обновляются. Удалите их и установите SDK установщиком " +
                           $"({InstallerUrl}) или по git:\n{git}";
                    break;
            }
            EditorGUILayout.HelpBox(text, MessageType.Warning);
            if (GUILayout.Button("Скопировать git-ссылку")) EditorGUIUtility.systemCopyBuffer = git;
        }

        private void DrawCopies()
        {
            if (_copies.Count == 0) return;
            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(
                "В Assets лежат копии ядра R3 (NuGetForUnity, Assets/Plugins): " +
                string.Join(", ", _copies.Take(6).Select(c => c.AssetPath)) + (_copies.Count > 6 ? " …" : "") +
                ". С SDK 1.10 ядро R3 ставится пакетом org.nuget.r3 — копии дают дубли сборок " +
                "(«Multiple precompiled assemblies with the same name»).", MessageType.Warning);
            if (GUILayout.Button("Удалить копии") &&
                EditorUtility.DisplayDialog("VHR — копии ядра R3",
                    "Удалить из Assets:\n\n" + string.Join("\n", _copies.Take(15).Select(c => "  " + c.AssetPath)) +
                    "\n\nЗаписи о них в packages.config NuGetForUnity тоже уберутся.", "Удалить", "Отмена"))
            {
                VhrSdkProject.DeleteR3Copies(_copies);
                _copies = VhrSetupCore.FindR3CoreCopies(VhrSdkProject.Root);
            }
        }

        private void DrawDeprecations()
        {
            if (_info == null || _info.Deprecations.Count == 0) return;
            var used = _info.Deprecations.Where(d => _hits.TryGetValue(d.Identifier, out var h) && h.Count > 0).ToList();
            var scanning = _scanFiles != null;
            if (used.Count == 0 && !scanning) return;

            EditorGUILayout.LabelField("Устаревшие API в коде проекта", EditorStyles.boldLabel);
            if (scanning)
                EditorGUILayout.LabelField($"Ищу в Assets/**/*.cs… {_scanIndex}/{_scanFiles.Count}", EditorStyles.miniLabel);
            foreach (var d in used)
            {
                var hits = _hits[d.Identifier];
                EditorGUILayout.HelpBox($"{d.Api}" + (string.IsNullOrEmpty(d.Since) ? "" : $" — устарело с {d.Since}") +
                                        (string.IsNullOrEmpty(d.Message) ? "" : "\n" + d.Message), MessageType.Warning);
                foreach (var hit in hits.Take(8))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(hit, EditorStyles.miniLabel);
                        if (GUILayout.Button("Открыть", EditorStyles.miniButton, GUILayout.Width(70))) OpenHit(hit);
                    }
                }
                if (hits.Count > 8) EditorGUILayout.LabelField($"… и ещё {hits.Count - 8}", EditorStyles.miniLabel);
            }
            EditorGUILayout.LabelField("Поиск по имени — возможны совпадения в комментариях. Устаревшее API работает минимум " +
                                       "до следующей минорной версии, но замените его заранее.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(8);
        }

        private void DrawNotes()
        {
            if (_info == null || !_info.IsKnown) return;
            EditorGUILayout.LabelField($"Что нового в {_info.Latest}", EditorStyles.boldLabel);
            if (!string.IsNullOrEmpty(_notesText))
            {
                var style = new GUIStyle(EditorStyles.textArea) { wordWrap = true, richText = false };
                var width = Mathf.Max(200f, EditorGUIUtility.currentViewWidth - 30f);
                var height = Mathf.Min(320f, style.CalcHeight(new GUIContent(_notesText), width));
                EditorGUILayout.SelectableLabel(_notesText, style, GUILayout.Height(height));
            }
            else
            {
                EditorGUILayout.LabelField("Сервер не прислал описание — см. CHANGELOG.", EditorStyles.miniLabel);
            }
            if (GUILayout.Button("Открыть CHANGELOG ↗", GUILayout.Width(180))) Application.OpenURL(_info.ChangelogUrl);
        }

        private void DrawDependencies()
        {
            if (_info == null) return;
            EditorGUILayout.LabelField("Зависимости SDK " + (_info.IsKnown ? _info.Latest : VhrSdkVersionService.InstalledVersion),
                EditorStyles.boldLabel);
            if (_packages == null)
            {
                EditorGUILayout.LabelField(_listRequest != null ? "Читаю список пакетов…" : "Список пакетов недоступен.", EditorStyles.miniLabel);
                return;
            }
            foreach (var kv in _info.Dependencies.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (kv.Key.StartsWith("com.unity.", StringComparison.Ordinal)) continue;
                var p = _packages.FirstOrDefault(x => x.name == kv.Key);
                string state;
                if (p == null) state = "не установлен — придёт вместе с SDK";
                else if (VhrSetupCore.SemVer.Compare(p.version, kv.Value) >= 0) state = $"{p.version} ✓";
                else if (!p.isDirectDependency) state = $"{p.version} → {kv.Value} (поднимется вместе с SDK)";
                else if (p.source == PackageSource.Registry) state = $"{p.version} → {kv.Value} (обновится кнопкой)";
                else state = $"{p.version} ({p.source}) — нужна {kv.Value}, обновите вручную";
                Row(kv.Key, state);
            }
            if (_info.IsKnown && _missingRegistries.Count > 0)
                EditorGUILayout.LabelField("Не хватает реестров в Packages/manifest.json (добавятся при обновлении): " +
                                           string.Join(", ", _missingRegistries), EditorStyles.wordWrappedMiniLabel);
        }

        private static void DrawHowItWorks()
        {
            EditorGUILayout.HelpBox(
                "• Редактор сам проверяет версию раз в сутки и напомнит в консоли, если вышла новая.\n" +
                "• Минимальная поддерживаемая версия — ниже неё платформа не примет новую сборку игры (sdk_outdated), " +
                "и WebGL-сборка в Unity остановится с подсказкой. Поднимается редко и с анонсом.\n" +
                "• Уже опубликованные сборки продолжают работать. Новая версия SDK попадёт к игрокам после пересборки " +
                "игры и загрузки сборки на платформу.", MessageType.None);
        }

        // ================================================================= поиск устаревших API

        private void StartScan()
        {
            StopScan();
            _hits.Clear();
            if (_info == null || _info.Deprecations.Count == 0) return;
            var ids = _info.Deprecations.Select(d => d.Identifier)
                .Where(s => Regex.IsMatch(s, @"^[A-Za-z_][A-Za-z0-9_]*$")).Distinct().ToList();
            if (ids.Count == 0) return;
            _scanRegex = new Regex(@"\b(" + string.Join("|", ids.Select(Regex.Escape)) + @")\b", RegexOptions.CultureInvariant);
            try
            {
                _scanFiles = Directory.GetFiles(Path.Combine(VhrSdkProject.Root, "Assets"), "*.cs", SearchOption.AllDirectories)
                    .Where(f => !f.Replace('\\', '/').Split('/').Any(seg => seg.StartsWith(".") || seg.EndsWith("~")))
                    .ToList();
            }
            catch
            {
                _scanFiles = null;
                return;
            }
            _scanIndex = 0;
            EditorApplication.update += ScanStep;
        }

        private void StopScan()
        {
            EditorApplication.update -= ScanStep;
            _scanFiles = null;
        }

        private void ScanStep()
        {
            if (_scanFiles == null)
            {
                EditorApplication.update -= ScanStep;
                return;
            }
            var root = VhrSdkProject.Root.Replace('\\', '/').TrimEnd('/') + "/";
            var until = EditorApplication.timeSinceStartup + 0.012; // ~12 мс за кадр
            while (_scanIndex < _scanFiles.Count && EditorApplication.timeSinceStartup < until)
            {
                var file = _scanFiles[_scanIndex++];
                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch { continue; }
                var rel = file.Replace('\\', '/');
                if (rel.StartsWith(root, StringComparison.OrdinalIgnoreCase)) rel = rel.Substring(root.Length);
                for (var i = 0; i < lines.Length; i++)
                {
                    foreach (Match m in _scanRegex.Matches(lines[i]))
                    {
                        if (!_hits.TryGetValue(m.Value, out var list)) _hits[m.Value] = list = new List<string>();
                        if (list.Count < 50) list.Add($"{rel}:{i + 1}");
                    }
                }
            }
            if (_scanIndex >= _scanFiles.Count) StopScan();
            Repaint();
        }

        private static void OpenHit(string hit)
        {
            var colon = hit.LastIndexOf(':');
            var path = colon > 0 ? hit.Substring(0, colon) : hit;
            var line = colon > 0 && int.TryParse(hit.Substring(colon + 1), out var l) ? l : 0;
            var asset = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
            if (asset != null) AssetDatabase.OpenAsset(asset, line);
        }

        // ================================================================= утилиты

        private static void Row(string label, string value, GUIStyle style = null)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(190));
                EditorGUILayout.SelectableLabel(value ?? string.Empty, style ?? EditorStyles.label,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
        }

        private static string FormatReleased(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return string.Empty;
            return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
                ? $"   (выпущена {d.ToLocalTime():dd.MM.yyyy})"
                : string.Empty;
        }

        private static readonly Regex MdImage = new Regex(@"!\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant);
        private static readonly Regex MdLink = new Regex(@"\[([^\]]+)\]\(([^)\s]+)\)", RegexOptions.CultureInvariant);
        private static readonly Regex MdBold = new Regex(@"(\*\*|__)(?=\S)(.+?)(?<=\S)\1", RegexOptions.CultureInvariant);
        private static readonly Regex MdItalic = new Regex(@"(?<![\w*])\*(?=\S)([^*\n]+?)(?<=\S)\*(?![\w*])", RegexOptions.CultureInvariant);
        private static readonly Regex MdCode = new Regex(@"`([^`]+)`", RegexOptions.CultureInvariant);

        /// <summary>Markdown → простой текст: заголовки, списки, ссылки «текст (url)», без * и `.</summary>
        internal static string MarkdownToText(string md)
        {
            if (string.IsNullOrWhiteSpace(md)) return string.Empty;
            var sb = new StringBuilder();
            var inCode = false;
            foreach (var raw in md.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd();
                var t = line.TrimStart();
                if (t.StartsWith("```", StringComparison.Ordinal))
                {
                    inCode = !inCode;
                    continue;
                }
                if (inCode)
                {
                    sb.Append("    ").Append(line).Append('\n');
                    continue;
                }
                var indent = new string(' ', line.Length - t.Length);
                if (t.StartsWith("#", StringComparison.Ordinal))
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(Inline(t.TrimStart('#').Trim())).Append('\n');
                    continue;
                }
                if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal) ||
                    t.StartsWith("+ ", StringComparison.Ordinal))
                    t = "• " + t.Substring(2);
                else if (t.StartsWith(">", StringComparison.Ordinal))
                    t = "│ " + t.TrimStart('>').TrimStart();
                else if (t == "---" || t == "***")
                    t = string.Empty;
                // '\n', а не AppendLine: на Windows тот даёт "\r\n", и схлопывание пустых строк не сработает.
                sb.Append(indent).Append(Inline(t)).Append('\n');
            }
            var text = Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n");
            return text.Trim();
        }

        // Код в `…` прячем за плейсхолдеры (там бывают * и _: «Assets/**/*.cs»), снимаем разметку
        // вокруг — в том числе **жирный с `кодом` внутри** — и возвращаем код без кавычек.
        private static string Inline(string s)
        {
            var codes = new List<string>();
            s = MdCode.Replace(s, m =>
            {
                codes.Add(m.Groups[1].Value);
                return "\u0001" + (codes.Count - 1).ToString(CultureInfo.InvariantCulture) + "\u0002";
            });
            s = MdImage.Replace(s, "$1");
            s = MdLink.Replace(s, m => m.Groups[1].Value == m.Groups[2].Value ? m.Groups[1].Value : $"{m.Groups[1].Value} ({m.Groups[2].Value})");
            s = MdBold.Replace(s, "$2");
            s = MdItalic.Replace(s, "$1");
            return codes.Count == 0
                ? s
                : Regex.Replace(s, "\u0001(\\d+)\u0002", m => codes[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)]);
        }
    }
}
