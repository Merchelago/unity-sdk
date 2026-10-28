using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VhrGames.Sdk.Editor
{
    /// <summary>
    /// Окно <c>VHR → ИИ-ассистент (MCP)</c>: подключает ИИ-ассистента в Claude Code, Cursor или
    /// VS Code к MCP платформы VHR — создаёт или аккуратно дополняет файлы конфигурации клиентов
    /// в корне Unity-проекта (SDK 1.14.0+).
    /// </summary>
    /// <remarks>
    /// Сервер <c>vhr</c> подключается с заголовком <c>Authorization: Bearer</c> из переменной
    /// окружения <c>VHR_MCP_TOKEN</c>: в файлы пишется только ссылка на переменную, сам токен —
    /// никогда. Публичный сервер документации <c>vhr-docs</c> — без заголовка. Логика файлов —
    /// <see cref="VhrMcpConfig"/>.
    /// </remarks>
    public sealed class VhrMcpWindow : EditorWindow
    {
        private const string MenuPath = "VHR/ИИ-ассистент (MCP)";
        private const string SiteDocsUrl = "https://vhrgames.ru/developer-docs#mcp";
        private const string ReadmeUrl = "https://github.com/Merchelago/unity-sdk#ии-ассистенты-mcp";

        private const string Placeholder = "vhr_pat_…";

        private static readonly string[] EnvCommands =
        {
            "Windows (PowerShell)", "[Environment]::SetEnvironmentVariable(\"" + VhrMcpConfig.TokenEnvVar + "\", \"" + Placeholder + "\", \"User\")",
            "Windows (cmd)", "setx " + VhrMcpConfig.TokenEnvVar + " \"" + Placeholder + "\"",
            "macOS (zsh)", "echo 'export " + VhrMcpConfig.TokenEnvVar + "=\"" + Placeholder + "\"' >> ~/.zshrc",
            "Linux (bash)", "echo 'export " + VhrMcpConfig.TokenEnvVar + "=\"" + Placeholder + "\"' >> ~/.bashrc"
        };

        private static readonly string ClaudeCommands =
            "claude mcp add --transport http " + VhrMcpConfig.ServerName + " " + VhrMcpConfig.ServerUrl +
            " --header 'Authorization: Bearer ${" + VhrMcpConfig.TokenEnvVar + "}'\n" +
            "claude mcp add --transport http " + VhrMcpConfig.DocsServerName + " " + VhrMcpConfig.DocsServerUrl;

        private Vector2 _scroll;
        private bool _envFoldout;
        private bool _previewFoldout;
        private bool _cliFoldout;

        private readonly Dictionary<VhrMcpClient, bool> _selected = new Dictionary<VhrMcpClient, bool>();
        private readonly List<VhrMcpConfig.FileResult> _results = new List<VhrMcpConfig.FileResult>();

        private string _gitignoreMessage;
        private MessageType _gitignoreMessageType;

        // Кеш состояния (файлы и окружение не читаем на каждый OnGUI).
        private double _stateAt = -1;
        private readonly Dictionary<VhrMcpClient, VhrMcpConfig.FileState> _states = new Dictionary<VhrMcpClient, VhrMcpConfig.FileState>();
        private readonly HashSet<VhrMcpClient> _literalToken = new HashSet<VhrMcpClient>();
        private string _gitignorePath;
        private string _gitignorePrefix;
        private string _envProcess;
        private string _envUser;

        [MenuItem(MenuPath, priority = 12)]
        public static void Open()
        {
            var w = GetWindow<VhrMcpWindow>(false, "VHR — ИИ-ассистент", true);
            w.minSize = new Vector2(500, 560);
            w.Show();
        }

        private static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        private static string PrefKey(VhrMcpClient c) => VhrEditorSandbox.Prefix + "Mcp." + c;

        private void OnEnable()
        {
            var root = ProjectRoot;
            foreach (var c in VhrMcpConfig.AllClients)
            {
                // По умолчанию: Claude Code — да; Cursor и VS Code — если в проекте уже есть их папка.
                var byDefault = c == VhrMcpClient.ClaudeCode ||
                                Directory.Exists(Path.Combine(root, c == VhrMcpClient.Cursor ? ".cursor" : ".vscode"));
                _selected[c] = EditorPrefs.GetBool(PrefKey(c), byDefault);
            }
            _stateAt = -1;
        }

        private void OnFocus() => _stateAt = -1;

        private void OnGUI()
        {
            if (_stateAt < 0 || EditorApplication.timeSinceStartup - _stateAt > 2.0)
                RefreshState();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.Space(6);
            DrawIntro();
            EditorGUILayout.Space(8);
            DrawClients();
            EditorGUILayout.Space(8);
            DrawToken();
            EditorGUILayout.Space(8);
            DrawCreate();
            EditorGUILayout.Space(8);
            DrawGitignore();

            EditorGUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"VHR SDK {VhrSdk.SdkVersion}", EditorStyles.miniLabel);
                if (GUILayout.Button("Обновление SDK…", EditorStyles.miniButton, GUILayout.Width(130)))
                    VhrSdkUpdateWindow.Open();
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.EndScrollView();
        }

        // ------------------------------------------------------------- что это

        private static void DrawIntro()
        {
            EditorGUILayout.LabelField("ИИ-ассистент: MCP платформы VHR", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "MCP (Model Context Protocol) подключает ИИ-ассистента в Claude Code, Cursor или VS Code к платформе VHR. " +
                "Ассистент читает документацию SDK и правила платформы, видит ваши игры, их статус и отчёт интеграции SDK, " +
                "ведёт ачивки, товары и лидерборды, смотрит статистику и выкладывает обновления сборки — на обычную модерацию.\n" +
                "Деньги, выплаты, покупки и админские действия ассистенту недоступны, коды ачивок, товаров и таблиц он не меняет.",
                MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Документация MCP ↗")) Application.OpenURL(SiteDocsUrl);
                if (GUILayout.Button("README SDK ↗")) Application.OpenURL(ReadmeUrl);
            }
        }

        // ------------------------------------------------------------ клиенты

        private void DrawClients()
        {
            EditorGUILayout.LabelField("Клиенты", EditorStyles.boldLabel);
            foreach (var c in VhrMcpConfig.AllClients)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    var on = EditorGUILayout.ToggleLeft(
                        new GUIContent($"{VhrMcpConfig.DisplayName(c)}  ·  {VhrMcpConfig.RelativePath(c)}",
                            "Файл в корне Unity-проекта (рядом с Assets/)."),
                        _selected[c], GUILayout.Width(280));
                    if (EditorGUI.EndChangeCheck())
                    {
                        _selected[c] = on;
                        EditorPrefs.SetBool(PrefKey(c), on);
                    }
                    EditorGUILayout.LabelField(StateText(_states.TryGetValue(c, out var st) ? st : VhrMcpConfig.FileState.Missing),
                        EditorStyles.miniLabel);
                }
                if (_literalToken.Contains(c))
                {
                    EditorGUILayout.HelpBox(
                        $"В {VhrMcpConfig.RelativePath(c)} найден токен vhr_pat_… в открытом виде. Уберите его из файла " +
                        $"(«Создать файлы» заменит запись {VhrMcpConfig.ServerName} ссылкой на переменную) и отзовите токен в кабинете, " +
                        "если файл уже попадал в git.",
                        MessageType.Error);
                }
            }
            EditorGUILayout.LabelField(
                $"В файлы записываются два сервера: {VhrMcpConfig.ServerName} — полный, с вашим токеном, и " +
                $"{VhrMcpConfig.DocsServerName} — публичная документация без входа ({VhrMcpConfig.DocsServerUrl}). " +
                "Другие серверы и настройки в файлах не трогаются. Claude Code ищет .mcp.json в папке, где его запустили, — " +
                "запускайте его из корня Unity-проекта.",
                EditorStyles.wordWrappedMiniLabel);
        }

        private static string StateText(VhrMcpConfig.FileState s) => s switch
        {
            VhrMcpConfig.FileState.Configured => "✓ настроен",
            VhrMcpConfig.FileState.NeedsUpdate => "есть, VHR — обновить",
            VhrMcpConfig.FileState.NotConfigured => "есть, без VHR",
            VhrMcpConfig.FileState.Invalid => "некорректный JSON (будет бэкап)",
            _ => "нет файла"
        };

        // -------------------------------------------------------------- токен

        private void DrawToken()
        {
            var env = VhrMcpConfig.TokenEnvVar;
            EditorGUILayout.LabelField($"Личный токен ({env})", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                $"Сервер {VhrMcpConfig.ServerName} работает с личным токеном разработчика: заголовок Authorization: Bearer " +
                $"клиент берёт из переменной окружения {env}. В файлах — только ссылка на неё (Claude Code — " +
                $"{VhrMcpConfig.TokenReference(VhrMcpClient.ClaudeCode)}, Cursor и VS Code — " +
                $"{VhrMcpConfig.TokenReference(VhrMcpClient.Cursor)}), сам токен в проект не попадает. Права " +
                "(чтение, каталог, публикация) и срок выбираются при создании токена в кабинете; он показывается один раз.",
                EditorStyles.wordWrappedMiniLabel);

            if (GUILayout.Button("Создать токен в кабинете ↗"))
                Application.OpenURL(VhrMcpConfig.TokenPageUrl);

            if (!string.IsNullOrEmpty(_envProcess))
            {
                var ok = VhrMcpConfig.LooksLikeToken(_envProcess);
                EditorGUILayout.HelpBox(
                    ok
                        ? $"{env} задана: {VhrMcpConfig.MaskToken(_envProcess)} ✓"
                        : $"{env} задана ({VhrMcpConfig.MaskToken(_envProcess)}), но не похожа на личный токен VHR " +
                          "(vhr_pat_ и 40 букв и цифр). Скопируйте токен из кабинета целиком.",
                    ok ? MessageType.None : MessageType.Warning);
            }
            else if (!string.IsNullOrEmpty(_envUser))
            {
                EditorGUILayout.HelpBox(
                    $"{env} задана для пользователя Windows ({VhrMcpConfig.MaskToken(_envUser)}), но Unity запущен раньше и её " +
                    "не видит — это нормально. Перезапустите ИИ-клиент (терминал с Claude Code, Cursor, VS Code), чтобы он её прочитал.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    $"{env} не задана в окружении, из которого запущен Unity. Задайте её (ниже) и перезапустите ИИ-клиент. " +
                    $"Без неё сервер {VhrMcpConfig.ServerName} ответит 401, а {VhrMcpConfig.DocsServerName} работает и так.",
                    MessageType.Warning);
            }

            _envFoldout = EditorGUILayout.Foldout(_envFoldout, $"Как задать {env}", true);
            if (!_envFoldout) return;

            EditorGUI.indentLevel++;
            for (var i = 0; i < EnvCommands.Length; i += 2)
                DrawCommand(EnvCommands[i], EnvCommands[i + 1]);
            EditorGUILayout.LabelField(
                $"Вместо {Placeholder} вставьте токен из кабинета. Переменная читается при запуске программы: после установки " +
                "перезапустите терминал с Claude Code, Cursor или VS Code (в Windows — закройте все окна). Без командной строки: " +
                "Windows → «Изменение переменных среды текущего пользователя»; macOS/Linux — допишите строку export в ~/.zshrc " +
                "или ~/.bashrc редактором. Команда с токеном остаётся в истории оболочки — если это нежелательно, используйте " +
                "эти способы. macOS: Cursor и VS Code, запущенные из Dock, берут переменные из оболочки входа; если ассистент " +
                "их не видит — запустите редактор из терминала (cursor . или code .).",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUI.indentLevel--;
        }

        private static void DrawCommand(string title, string command)
        {
            EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.SelectableLabel(command, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (GUILayout.Button("Копировать", EditorStyles.miniButton, GUILayout.Width(80)))
                    EditorGUIUtility.systemCopyBuffer = command;
            }
        }

        // -------------------------------------------------------------- файлы

        private void DrawCreate()
        {
            EditorGUILayout.LabelField("Файлы конфигурации", EditorStyles.boldLabel);

            var any = false;
            foreach (var c in VhrMcpConfig.AllClients) any |= _selected[c];

            using (new EditorGUI.DisabledScope(!any))
            {
                if (GUILayout.Button(any ? "Создать файлы" : "Создать файлы (выберите клиента)", GUILayout.Height(28)))
                    CreateFiles();
            }

            foreach (var r in _results)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var type = !r.Ok ? MessageType.Error : r.Merge.WasInvalid ? MessageType.Warning : MessageType.Info;
                    EditorGUILayout.HelpBox(r.Summary(), type);
                    using (new EditorGUI.DisabledScope(!File.Exists(r.Path)))
                    {
                        if (GUILayout.Button("Показать", GUILayout.Width(72), GUILayout.Height(38)))
                            EditorUtility.RevealInFinder(r.Path);
                    }
                }
                if (r.Ok && r.Merge.WasInvalid)
                {
                    EditorGUILayout.LabelField(
                        "В бэкапе — прежнее содержимое файла как есть: перенесите из него нужное и удалите его.",
                        EditorStyles.wordWrappedMiniLabel);
                }
            }

            EditorGUILayout.LabelField(
                "Дальше: Claude Code при первом запуске в папке проекта спросит, доверять ли серверам из .mcp.json, — " +
                "разрешите (проверка — команда /mcp). Cursor — Settings → MCP: включите vhr и vhr-docs. VS Code — откройте " +
                ".vscode/mcp.json и нажмите Start над сервером (или команда «MCP: List Servers»).",
                EditorStyles.wordWrappedMiniLabel);

            _previewFoldout = EditorGUILayout.Foldout(_previewFoldout, "Что будет записано", true);
            if (_previewFoldout)
            {
                EditorGUI.indentLevel++;
                foreach (var c in VhrMcpConfig.AllClients)
                {
                    if (!_selected[c]) continue;
                    var json = VhrMcpConfig.FreshDocument(c).TrimEnd('\n');
                    var lines = json.Split('\n').Length;
                    EditorGUILayout.LabelField($"{VhrMcpConfig.DisplayName(c)} — {VhrMcpConfig.RelativePath(c)}", EditorStyles.miniBoldLabel);
                    EditorGUILayout.SelectableLabel(json, EditorStyles.textArea, GUILayout.Height(lines * EditorStyles.textArea.lineHeight + 8f));
                }
                EditorGUILayout.LabelField(
                    "Если файл уже есть, в него добавляются (или заменяются) только записи vhr и vhr-docs.",
                    EditorStyles.wordWrappedMiniLabel);
                EditorGUI.indentLevel--;
            }

            _cliFoldout = EditorGUILayout.Foldout(_cliFoldout, "Claude Code без файлов — командой", true);
            if (_cliFoldout)
            {
                EditorGUI.indentLevel++;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.SelectableLabel(ClaudeCommands, EditorStyles.textArea, GUILayout.Height(2 * EditorStyles.textArea.lineHeight + 10f));
                    if (GUILayout.Button("Копировать", EditorStyles.miniButton, GUILayout.Width(80)))
                        EditorGUIUtility.systemCopyBuffer = ClaudeCommands;
                }
                EditorGUILayout.LabelField(
                    "Только для вас и только в этом проекте (настройки Claude Code, не файл проекта); с --scope project " +
                    "команда пишет тот же .mcp.json. Одинарные кавычки — для bash, zsh и PowerShell: ${" + VhrMcpConfig.TokenEnvVar +
                    "} подставит сам Claude Code при запуске, а не оболочка. В cmd используйте двойные кавычки.",
                    EditorStyles.wordWrappedMiniLabel);
                EditorGUI.indentLevel--;
            }
        }

        private void CreateFiles()
        {
            _results.Clear();
            var root = ProjectRoot;
            var now = DateTime.Now;
            foreach (var c in VhrMcpConfig.AllClients)
            {
                if (!_selected[c]) continue;
                var r = VhrMcpConfig.WriteConfig(root, c, now);
                _results.Add(r);
                if (!r.Ok) Debug.LogWarning("[VHR SDK] MCP: " + r.Summary());
                else if (r.Merge.WasInvalid) Debug.LogWarning("[VHR SDK] MCP: " + r.Summary());
                else Debug.Log("[VHR SDK] MCP: " + r.Summary());
            }
            _stateAt = -1;
            GUI.FocusControl(null);
        }

        // ---------------------------------------------------------------- git

        private void DrawGitignore()
        {
            EditorGUILayout.LabelField("Git", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Секретов в файлах нет, их можно коммитить: тогда ассистент подключится у всей команды, а каждый задаёт свою " +
                $"{VhrMcpConfig.TokenEnvVar}. Если не хотите хранить их в репозитории, добавьте их в .gitignore.",
                EditorStyles.wordWrappedMiniLabel);

            var any = false;
            foreach (var c in VhrMcpConfig.AllClients) any |= _selected[c];

            if (_gitignorePath == null)
            {
                EditorGUILayout.LabelField("В проекте нет .gitignore — кнопка станет доступна, когда он появится.",
                    EditorStyles.wordWrappedMiniLabel);
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!any))
                    {
                        if (GUILayout.Button("Добавить в .gitignore", GUILayout.Width(180)))
                            AddToGitignore();
                    }
                    EditorGUILayout.LabelField(_gitignorePath, EditorStyles.miniLabel);
                }
            }
            if (!string.IsNullOrEmpty(_gitignoreMessage))
                EditorGUILayout.HelpBox(_gitignoreMessage, _gitignoreMessageType);
        }

        private void AddToGitignore()
        {
            try
            {
                var clients = new List<VhrMcpClient>();
                foreach (var c in VhrMcpConfig.AllClients)
                    if (_selected[c]) clients.Add(c);
                var entries = VhrMcpConfig.GitignoreEntries(clients, _gitignorePrefix);
                var existing = File.ReadAllText(_gitignorePath);
                var updated = VhrMcpConfig.AppendGitignore(existing, entries, out var added);
                if (added > 0) File.WriteAllText(_gitignorePath, updated, new System.Text.UTF8Encoding(false));
                _gitignoreMessage = added > 0
                    ? $"В .gitignore добавлено строк: {added} ({string.Join(", ", entries)})."
                    : "Всё уже есть в .gitignore.";
                _gitignoreMessageType = MessageType.Info;
            }
            catch (Exception e)
            {
                _gitignoreMessage = "Не удалось изменить .gitignore: " + e.Message;
                _gitignoreMessageType = MessageType.Error;
            }
        }

        // ---------------------------------------------------------- состояние

        private void RefreshState()
        {
            _stateAt = EditorApplication.timeSinceStartup;
            var root = ProjectRoot;
            _literalToken.Clear();
            foreach (var c in VhrMcpConfig.AllClients)
            {
                var path = Path.Combine(root, VhrMcpConfig.RelativePath(c));
                try
                {
                    var text = File.Exists(path) ? File.ReadAllText(path) : null;
                    _states[c] = VhrMcpConfig.Inspect(text, c, out var literal);
                    if (literal) _literalToken.Add(c);
                }
                catch
                {
                    _states[c] = VhrMcpConfig.FileState.Invalid;
                }
            }

            _gitignorePath = VhrMcpConfig.FindGitignore(root, out _gitignorePrefix);

            _envProcess = ReadEnv(EnvironmentVariableTarget.Process);
            // Переменная пользователя Windows (реестр) видна сразу после setx, даже если Unity запущен раньше.
            _envUser = Application.platform == RuntimePlatform.WindowsEditor && string.IsNullOrEmpty(_envProcess)
                ? ReadEnv(EnvironmentVariableTarget.User)
                : null;
        }

        private static string ReadEnv(EnvironmentVariableTarget target)
        {
            try { return Environment.GetEnvironmentVariable(VhrMcpConfig.TokenEnvVar, target); }
            catch { return null; }
        }
    }
}
