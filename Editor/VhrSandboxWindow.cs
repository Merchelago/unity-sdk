using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VhrGames.Sdk.Editor
{
    /// <summary>
    /// Окно <c>VHR → Тестирование в Editor</c>: песочный ключ, режим SDK в Unity
    /// Editor (Simulation / Live-песочница), проверка ключа и всех API на настоящем
    /// сервере, сброс тестовых данных.
    /// </summary>
    /// <remarks>
    /// Ключ хранится только в <c>EditorPrefs</c> этого компьютера под именем,
    /// уникальным для проекта (<see cref="VhrEditorSandbox"/>), и читается SDK только
    /// в Unity Editor. В сцены, ScriptableObject, ProjectSettings и сборку он не
    /// попадает; дополнительно это проверяет <see cref="VhrSandboxKeyBuildGuard"/>.
    /// </remarks>
    public sealed class VhrSandboxWindow : EditorWindow
    {
        private const string MenuPath = "VHR/Тестирование в Editor";

        private string _keyDraft;
        private bool _showKey;
        private bool _serverFoldout;
        private string _serverDraft;
        private string _relayDraft;
        private Vector2 _scroll;

        // Результат «Проверить» (GET sandbox/me).
        private bool _checking;
        private string _checkError;
        private SandboxMe _me;

        // Смоук-тест всех API.
        private readonly List<SmokeRow> _smoke = new List<SmokeRow>();
        private int _smokePending;

        private bool _resetting;
        private string _resetMessage;

        // Тестовый слот облачных сохранений для «Проверить все API» (создаётся и удаляется).
        private const string SelfTestSlot = "vhr_sdk_selftest";

        // Локальные сохранения Simulation: статистика с кешем (файлы не читаем на каждый OnGUI).
        private int _simSlots;
        private long _simBytes;
        private double _simStatsAt = -1;

        [MenuItem(MenuPath, priority = 10)]
        public static void Open()
        {
            var w = GetWindow<VhrSandboxWindow>(false, "VHR — тест в Editor", true);
            w.minSize = new Vector2(460, 520);
            w.Show();
        }

        private void OnEnable()
        {
            _keyDraft = VhrEditorSandbox.Key;
            _serverDraft = VhrEditorSandbox.ServerOverride;
            _relayDraft = VhrEditorSandbox.RelayOverride;
            _serverFoldout = !string.IsNullOrEmpty(_serverDraft) || !string.IsNullOrEmpty(_relayDraft);
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Тестирование VHR SDK в Unity Editor", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Simulation — всё локально: тестовый баланс, диалог подтверждения покупки, симуляция рекламы, " +
                "облачные сохранения в папке Library проекта (с лимитами сервера).\n" +
                "Live (песочница) — все API SDK ходят на настоящий сервер VHR от имени тестового игрока " +
                "(10 000 тестовых монет): покупки, облачные сохранения, лидерборды, ачивки, профиль, друзья, лобби и релей. " +
                "Реальные деньги не тратятся.",
                MessageType.Info);

            DrawMode();
            EditorGUILayout.Space(8);
            DrawKey();
            EditorGUILayout.Space(8);
            DrawChecks();
            EditorGUILayout.Space(8);
            DrawServer();

            if (EditorApplication.isPlaying)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(
                    "Идёт Play Mode: режим, ключ и сервер применяются при инициализации SDK — перезапустите Play, " +
                    "чтобы изменения вступили в силу.", MessageType.Warning);
            }

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

        // ------------------------------------------------------------- режим

        private void DrawMode()
        {
            EditorGUILayout.LabelField("Режим SDK в Editor", EditorStyles.boldLabel);
            var current = VhrEditorSandbox.SelectedMode == VhrEditorMode.LiveSandbox ? 1 : 0;
            var picked = GUILayout.Toolbar(current, new[] { "Simulation (локально)", "Live (песочница)" });
            if (picked != current)
                VhrEditorSandbox.SelectedMode = picked == 1 ? VhrEditorMode.LiveSandbox : VhrEditorMode.Simulation;

            if (picked == 1 && !VhrEditorSandbox.InspectKey(VhrEditorSandbox.Key, out _, out var problem))
            {
                EditorGUILayout.HelpBox("Live выбран, но " + problem + " Пока ключа нет, SDK работает в режиме Simulation.",
                    MessageType.Warning);
            }

            EditorGUILayout.LabelField(
                "Режим берётся отсюда, если в коде VhrSdkOptions.EditorMode = Auto (по умолчанию). В сборках режим не используется.",
                EditorStyles.wordWrappedMiniLabel);
        }

        // -------------------------------------------------------------- ключ

        private void DrawKey()
        {
            EditorGUILayout.LabelField("Песочный ключ", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _keyDraft = _showKey
                    ? EditorGUILayout.TextField(_keyDraft ?? string.Empty)
                    : EditorGUILayout.PasswordField(_keyDraft ?? string.Empty);
                if (EditorGUI.EndChangeCheck())
                {
                    VhrEditorSandbox.Key = _keyDraft;
                    _me = null;
                    _checkError = null;
                    _smoke.Clear();
                }
                if (GUILayout.Button(_showKey ? "Скрыть" : "Показать", GUILayout.Width(80)))
                    _showKey = !_showKey;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Получить ключ ↗"))
                    Application.OpenURL(VhrEditorSandbox.GetKeyUrl);
                using (new EditorGUI.DisabledScope(!VhrEditorSandbox.HasKey))
                {
                    if (GUILayout.Button("Удалить ключ") &&
                        EditorUtility.DisplayDialog("VHR", "Удалить песочный ключ этого проекта с этого компьютера?", "Удалить", "Отмена"))
                    {
                        VhrEditorSandbox.Key = string.Empty;
                        _keyDraft = string.Empty;
                        _me = null;
                        _smoke.Clear();
                        GUI.FocusControl(null);
                    }
                }
            }

            EditorGUILayout.LabelField(
                "Где взять: сайт → кабинет разработчика → игра → «Тест в Unity Editor». Ключ действует 30 дней.",
                EditorStyles.wordWrappedMiniLabel);

            var key = VhrEditorSandbox.Key;
            if (!string.IsNullOrEmpty(key))
            {
                if (VhrEditorSandbox.InspectKey(key, out var info, out var problem))
                {
                    var until = info.ExpiresAt.HasValue ? info.ExpiresAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "—";
                    EditorGUILayout.HelpBox(
                        $"Игра (gid): {info.GameId}\nТестовый игрок: {(string.IsNullOrEmpty(info.Subject) ? "—" : info.Subject)}\n" +
                        $"Ключ действует до: {until}\n(данные из ключа; нажмите «Проверить», чтобы спросить сервер)",
                        MessageType.None);
                }
                else
                {
                    EditorGUILayout.HelpBox("Ключ не подходит: " + problem, MessageType.Error);
                }
            }

            EditorGUILayout.HelpBox(
                "Ключ работает только в Unity Editor и никогда не попадает в сборку: он хранится в EditorPrefs этого " +
                "компьютера (отдельно для этого проекта) — не в сцене, не в ассете, не в ProjectSettings. " +
                "Не вставляйте ключ в код, ScriptableObject или настройки и не коммитьте его: перед сборкой SDK " +
                "проверяет проект и предупредит, если найдёт ключ.",
                MessageType.Warning);
        }

        // ------------------------------------------------------- проверки

        private void DrawChecks()
        {
            EditorGUILayout.LabelField("Проверка на сервере", EditorStyles.boldLabel);
            var keyOk = VhrEditorSandbox.InspectKey(VhrEditorSandbox.Key, out var info, out _);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!keyOk || _checking))
                {
                    if (GUILayout.Button(_checking ? "Проверяю…" : "Проверить"))
                        CheckSandbox(info);
                }
                using (new EditorGUI.DisabledScope(!keyOk || _smokePending > 0))
                {
                    if (GUILayout.Button(_smokePending > 0 ? $"Проверяю API… ({_smokePending})" : "Проверить все API"))
                        RunSmoke(info);
                }
            }

            if (!string.IsNullOrEmpty(_checkError))
                EditorGUILayout.HelpBox(_checkError, MessageType.Error);
            if (_me != null)
            {
                EditorGUILayout.HelpBox(
                    $"Сервер принял ключ ✓\nИгра: {_me.gameId}\nТестовый игрок: {_me.sandboxUserId}\n" +
                    $"Баланс: {_me.balance} тестовых монет\nКлюч действует до: {FormatDate(_me.expiresAt)}",
                    MessageType.Info);
            }

            if (_smoke.Count > 0)
            {
                EditorGUILayout.LabelField(
                    "Проверка API: GET ко всем сервисам (данные не меняются) + цикл облачных сохранений на тестовом слоте " +
                    $"«{SelfTestSlot}» (создаётся и удаляется):", EditorStyles.wordWrappedMiniLabel);
                foreach (var row in _smoke)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var mark = row.Done ? (row.Ok ? "✓" : "✗") : "…";
                        EditorGUILayout.LabelField(mark, GUILayout.Width(16));
                        EditorGUILayout.LabelField(row.Name, GUILayout.Width(170));
                        EditorGUILayout.SelectableLabel(row.Result ?? string.Empty, EditorStyles.miniLabel,
                            GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    }
                }
            }

            EditorGUILayout.Space(4);
            using (new EditorGUI.DisabledScope(_resetting))
            {
                if (GUILayout.Button(_resetting ? "Сбрасываю…" : "Сбросить тестовые данные"))
                    ResetData(keyOk, info);
            }
            if (!string.IsNullOrEmpty(_resetMessage))
                EditorGUILayout.HelpBox(_resetMessage, MessageType.None);
            EditorGUILayout.LabelField(
                $"Сброс: баланс тестового игрока снова {VhrEditorSandbox.StartBalance}, покупки и инвентарь по этой игре очищены " +
                $"(в Live — на сервере, в Simulation — локально; сейчас локальный баланс {VhrEditorSandbox.SimBalance}).",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(6);
            DrawSimSaves();
        }

        private void DrawSimSaves()
        {
            if (_simStatsAt < 0 || EditorApplication.timeSinceStartup - _simStatsAt > 2.0)
                RefreshSimStats();

            EditorGUILayout.LabelField("Облачные сохранения в режиме Simulation", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                $"Локально: слотов {_simSlots}, {_simBytes / 1024f:0.#} КБ после сжатия. Лимиты — как у песочного игрока на сервере: " +
                $"слот ≤ {VhrEditorSaves.MaxSlotKb} КБ сжато и ≤ {VhrEditorSaves.MaxRawKb} КБ сырых, слотов ≤ {VhrEditorSaves.MaxSlots}, " +
                $"всего ≤ {VhrEditorSaves.SandboxQuotaKb} КБ, 12 записей и 60 чтений в минуту. Папка: Library/VhrSdk/SimSaves " +
                "(не в VCS и не в сборке, переживает перезапуск редактора). В Live сохранения тестового игрока хранятся на сервере.",
                EditorStyles.wordWrappedMiniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Открыть папку"))
                {
                    try
                    {
                        Directory.CreateDirectory(VhrEditorSaves.Folder);
                        EditorUtility.RevealInFinder(VhrEditorSaves.Folder);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[VHR SDK] Не удалось открыть папку сохранений: " + e.Message);
                    }
                }
                using (new EditorGUI.DisabledScope(_simSlots == 0))
                {
                    if (GUILayout.Button("Очистить локальные сохранения") &&
                        EditorUtility.DisplayDialog("VHR — облачные сохранения (Simulation)",
                            $"Удалить локальные сохранения симуляции этого проекта (слотов: {_simSlots})? " +
                            "Сохранения на сервере (режим Live) это не затрагивает.", "Удалить", "Отмена"))
                    {
                        VhrEditorSaves.Clear();
                        RefreshSimStats();
                    }
                }
            }
        }

        private void RefreshSimStats()
        {
            _simStatsAt = EditorApplication.timeSinceStartup;
            try { VhrEditorSaves.Stats(out _simSlots, out _simBytes); }
            catch
            {
                _simSlots = 0;
                _simBytes = 0;
            }
        }

        private void CheckSandbox(VhrJwt.Info info)
        {
            _checking = true;
            _checkError = null;
            _me = null;
            VhrEditorHttp.Send("GET", VhrEditorSandbox.BridgeBaseUrl + "/api/sandbox/me", VhrEditorSandbox.Key,
                info.GameId, null, r =>
                {
                    _checking = false;
                    if (r.Ok)
                    {
                        try { _me = JsonUtility.FromJson<SandboxMe>(r.Body); }
                        catch (Exception e) { _checkError = "Не удалось разобрать ответ сервера: " + e.Message; }
                    }
                    else
                    {
                        _checkError = DescribeError(r);
                    }
                    Repaint();
                });
        }

        private void RunSmoke(VhrJwt.Info info)
        {
            _smoke.Clear();
            var api = VhrEditorSandbox.ApiServer;
            var gid = Uri.EscapeDataString(info.GameId);
            var bridge = api + "/bridge/api/";
            var games = api + "/games/api/";

            void Add(string name, string url)
            {
                var row = new SmokeRow { Name = name };
                _smoke.Add(row);
                _smokePending++;
                VhrEditorHttp.Send("GET", url, VhrEditorSandbox.Key, info.GameId, null, r =>
                {
                    row.Done = true;
                    row.Ok = r.Ok;
                    row.Result = r.Ok ? r.Short() : r.Short() + " — " + Trim(r.Body ?? r.Error, 120);
                    _smokePending--;
                    Repaint();
                });
            }

            Add("Мост: ping", bridge + "ping");
            Add("Песочница: sandbox/me", bridge + "sandbox/me");
            Add("Экономика: баланс", bridge + "balance");
            Add("Экономика: каталог товаров", bridge + "items?gameId=" + gid);
            Add("Экономика: инвентарь", bridge + "inventory?gameId=" + gid);
            Add("Профиль: Auth/me", api + "/auth/api/Auth/me");
            Add("Прогресс: PlayerStats", games + "PlayerStats");
            Add("Достижения игры", games + "Achievements/games/" + gid);
            Add("Мои достижения", games + "Achievements/me");
            Add("Лидерборд игры", games + "Leaderboard/games/" + gid + "?limit=5");
            Add("Друзья", games + "Friends");
            Add("Турниры", games + "Tournaments?status=active");
            Add("Серверы: привязки", api + "/servers/api/bindings?gameId=" + gid);
            Add("Сохранения: список", bridge + "saves");
            RunSavesSmoke(info);
        }

        private SmokeRow StartRow(string name)
        {
            var row = new SmokeRow { Name = name };
            _smoke.Add(row);
            _smokePending++;
            Repaint();
            return row;
        }

        private void FinishRow(SmokeRow row, bool ok, string result)
        {
            row.Done = true;
            row.Ok = ok;
            row.Result = result;
            _smokePending = Math.Max(0, _smokePending - 1);
            Repaint();
        }

        /// <summary>
        /// Цикл облачных сохранений тестового игрока на тестовом слоте: запись → чтение
        /// (данные с кириллицей совпали) → запись с версией (+1) → запись с устаревшей
        /// версией (ожидается <c>409 version_conflict</c>) → удаление → чтение
        /// (ожидается <c>404 save_not_found</c>). 4 записи из 12 разрешённых в минуту.
        /// </summary>
        private void RunSavesSmoke(VhrJwt.Info info)
        {
            var url = VhrEditorSandbox.BridgeBaseUrl + "/api/saves/" + SelfTestSlot;
            var key = VhrEditorSandbox.Key;
            var gid = info.GameId;
            var stamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var data1 = "{\"selfTest\":1,\"at\":\"" + stamp + "\",\"text\":\"Проверка сохранений \\\"VHR\\\" ✓\"}";
            var data2 = "{\"selfTest\":2,\"at\":\"" + stamp + "\"}";

            void Send(string method, string body, Action<VhrEditorHttp.Response> done)
                => VhrEditorHttp.Send(method, url, key, gid, body, done);

            void Cleanup() => Send("DELETE", null, _ => { });

            string Failed(VhrEditorHttp.Response r, string expected = null)
                => r.Short() + " — " + Trim(expected ?? r.Body ?? r.Error, 120);

            var put1 = StartRow("Сохранения: запись");
            Send("PUT", VhrSavesService.BuildPutBody(data1, null), r1 =>
            {
                var w1 = ParseSave(r1.Body);
                if (!r1.Ok || w1 == null || w1.version <= 0)
                {
                    FinishRow(put1, false, Failed(r1));
                    return;
                }
                var v1 = w1.version;
                FinishRow(put1, true, $"{r1.Short()}, версия {v1}, сжато {w1.storedSize} Б");

                var get1 = StartRow("Сохранения: чтение");
                Send("GET", null, r2 =>
                {
                    var g = ParseSave(r2.Body);
                    var same = g != null && g.data == data1;
                    var ok2 = r2.Ok && same && g.version == v1;
                    FinishRow(get1, ok2, ok2
                        ? $"{r2.Short()}, данные совпали, версия {g.version}"
                        : Failed(r2, r2.Ok ? $"версия {g?.version}, данные {(same ? "совпали" : "НЕ совпали")}" : null));
                    if (!ok2)
                    {
                        Cleanup();
                        return;
                    }

                    var put2 = StartRow("Сохранения: версия +1");
                    Send("PUT", VhrSavesService.BuildPutBody(data2, v1), r3 =>
                    {
                        var w2 = ParseSave(r3.Body);
                        var ok3 = r3.Ok && w2 != null && w2.version == v1 + 1;
                        FinishRow(put2, ok3, ok3
                            ? $"{r3.Short()}, expectedVersion {v1} → версия {w2.version}"
                            : Failed(r3, r3.Ok ? $"ожидалась версия {v1 + 1}, пришла {w2?.version}" : null));
                        if (!ok3)
                        {
                            Cleanup();
                            return;
                        }

                        var stale = StartRow("Сохранения: конфликт версий");
                        Send("PUT", VhrSavesService.BuildPutBody(data1, v1), r4 =>
                        {
                            var e = ParseSave(r4.Body);
                            var ok4 = r4.Status == 409 && VhrApiClient.TryReadServerCode(r4.Body) == "version_conflict" &&
                                      e != null && e.serverVersion == v1 + 1;
                            FinishRow(stale, ok4, ok4
                                ? $"409 version_conflict, на сервере версия {e.serverVersion}"
                                : Failed(r4, $"ожидался 409 version_conflict с serverVersion {v1 + 1}"));

                            var del = StartRow("Сохранения: удаление");
                            Send("DELETE", null, r5 =>
                            {
                                FinishRow(del, r5.Ok, r5.Ok ? r5.Short() : Failed(r5));
                                if (!r5.Ok) return;

                                var gone = StartRow("Сохранения: после удаления");
                                Send("GET", null, r6 =>
                                {
                                    var ok6 = r6.Status == 404 && VhrApiClient.TryReadServerCode(r6.Body) == "save_not_found";
                                    FinishRow(gone, ok6, ok6 ? "404 save_not_found" : Failed(r6, "ожидался 404 save_not_found"));
                                });
                            });
                        });
                    });
                });
            });
        }

        private static SaveDto ParseSave(string body)
        {
            if (string.IsNullOrWhiteSpace(body) || body.TrimStart()[0] != '{') return null;
            try { return JsonUtility.FromJson<SaveDto>(body); }
            catch { return null; }
        }

        private void ResetData(bool keyOk, VhrJwt.Info info)
        {
            var live = VhrEditorSandbox.SelectedMode == VhrEditorMode.LiveSandbox && keyOk;
            var text = live
                ? $"Сбросить тестовые данные игры {info.GameId} на сервере? Баланс тестового игрока станет " +
                  $"{VhrEditorSandbox.StartBalance}, его покупки и инвентарь по этой игре будут удалены. " +
                  "Локальная симуляция тоже сбросится."
                : $"Сбросить локальную симуляцию? Тестовый баланс станет {VhrEditorSandbox.StartBalance}, " +
                  "покупки текущего сеанса забудутся.";
            if (!EditorUtility.DisplayDialog("VHR — сброс тестовых данных", text, "Сбросить", "Отмена"))
                return;

            VhrEditorSandbox.ResetSimulation();
            if (!live)
            {
                _resetMessage = "Локальная симуляция сброшена ✓";
                return;
            }

            _resetting = true;
            _resetMessage = null;
            VhrEditorHttp.Send("POST", VhrEditorSandbox.BridgeBaseUrl + "/api/sandbox/reset", VhrEditorSandbox.Key,
                info.GameId, "{}", r =>
                {
                    _resetting = false;
                    _resetMessage = r.Ok
                        ? "Тестовые данные на сервере сброшены ✓ (и локальная симуляция тоже)"
                        : "Сброс на сервере не удался: " + DescribeError(r);
                    if (r.Ok) CheckSandbox(info);
                    Repaint();
                });
        }

        // ------------------------------------------------------------ сервер

        private void DrawServer()
        {
            _serverFoldout = EditorGUILayout.Foldout(_serverFoldout, "Сервер (для отладки на dev-стенде)", true);
            if (!_serverFoldout) return;

            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();
            _serverDraft = EditorGUILayout.TextField(new GUIContent("API",
                "Пусто — прод " + VhrEditorSandbox.ProdApiServer + " (те же адреса, что в сборке). " +
                "Сервисы: {API}/bridge, /games, /auth, /servers, /notifications."), _serverDraft ?? string.Empty);
            _relayDraft = EditorGUILayout.TextField(new GUIContent("Релей (WebSocket)",
                "Пусто — прод " + VhrEditorSandbox.ProdRelay), _relayDraft ?? string.Empty);
            if (EditorGUI.EndChangeCheck())
            {
                VhrEditorSandbox.ServerOverride = _serverDraft;
                VhrEditorSandbox.RelayOverride = _relayDraft;
                _me = null;
                _smoke.Clear();
            }

            EditorGUILayout.LabelField("Сейчас: " + VhrEditorSandbox.ApiServer + " · релей: " +
                                       (string.IsNullOrEmpty(VhrEditorSandbox.RelayOverride) ? VhrEditorSandbox.ProdRelay : VhrEditorSandbox.RelayOverride),
                EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button("Вернуть прод"))
            {
                VhrEditorSandbox.ServerOverride = string.Empty;
                VhrEditorSandbox.RelayOverride = string.Empty;
                _serverDraft = string.Empty;
                _relayDraft = string.Empty;
                GUI.FocusControl(null);
            }
            EditorGUILayout.LabelField(
                "По умолчанию — прод. Меняйте только для отладки на своём стенде; применяется только в режиме Live.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUI.indentLevel--;
        }

        // ----------------------------------------------------------- утилиты

        internal static string DescribeError(VhrEditorHttp.Response r)
        {
            if (r.Status == 0) return "Нет связи с сервером: " + r.Error;
            var code = VhrApiClient.TryReadServerCode(r.Body);
            var hint = r.Status switch
            {
                401 => "Сервер не принял ключ: он истёк, отозван или скопирован не полностью. Получите новый на сайте.",
                403 => "Доступ запрещён" + (string.IsNullOrEmpty(code) ? "." : $" ({code})."),
                404 => "Эндпоинт не найден — возможно, сервер ещё не обновлён или в поле «Сервер» указан не тот адрес.",
                426 => "Сервер требует новую версию VHR SDK — обновите пакет.",
                _ => "Ошибка сервера."
            };
            return $"HTTP {r.Status}{(string.IsNullOrEmpty(code) ? "" : " " + code)}. {hint}";
        }

        private static string FormatDate(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "—";
            return DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
                ? d.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
                : iso;
        }

        private static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Replace('\n', ' ').Replace('\r', ' ');
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private sealed class SmokeRow
        {
            public string Name;
            public bool Done;
            public bool Ok;
            public string Result;
        }

        // Ответ GET sandbox/me: { sandboxUserId, gameId, balance, expiresAt }.
#pragma warning disable CS0649 // поля заполняет JsonUtility
        [Serializable]
        private sealed class SandboxMe
        {
            public string sandboxUserId;
            public string gameId;
            public long balance;
            public string expiresAt;
        }

        // Ответы /api/saves/{slot}: GET { slot, data, version, updatedAt }, PUT { slot, version, updatedAt,
        // rawSize, storedSize }, 409 { code, serverVersion, serverUpdatedAt }.
        [Serializable]
        private sealed class SaveDto
        {
            public string slot;
            public string data;
            public long version;
            public string updatedAt;
            public int storedSize;
            public long serverVersion;
        }
#pragma warning restore CS0649
    }
}
