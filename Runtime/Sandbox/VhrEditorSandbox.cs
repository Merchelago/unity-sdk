#if UNITY_EDITOR
// ВЕСЬ файл существует только в Unity Editor: в сборку игры (WebGL и любую
// другую) этот код не компилируется, поэтому песочный ключ физически не может
// оказаться в билде через SDK.
using System;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace VhrGames.Sdk
{
    /// <summary>
    /// <b>Только Unity Editor.</b> Настройки «Тестирования в Editor» (окно
    /// <c>VHR → Тестирование в Editor</c>): песочный ключ, выбранный режим, адрес
    /// сервера для dev-стенда, плюс состояние локальной симуляции экономики.
    /// </summary>
    /// <remarks>
    /// Ключ хранится в <see cref="EditorPrefs"/> текущего пользователя этого
    /// компьютера под именем, уникальным для проекта (хеш <c>Application.dataPath</c>).
    /// Он не пишется ни в сцены, ни в ScriptableObject, ни в ProjectSettings, не
    /// попадает в VCS и в сборку. Локальный баланс симуляции живёт в
    /// <see cref="SessionState"/> (до перезапуска редактора).
    /// </remarks>
    internal static class VhrEditorSandbox
    {
        /// <summary>Прод-адрес API (мост, игры, авторизация, серверы) — как в сборках.</summary>
        public const string ProdApiServer = "https://api.vhrweb.ru";

        /// <summary>Прод-адрес релея (WebSocket) — как в сборках.</summary>
        public const string ProdRelay = "wss://servers.vhrweb.ru/ws";

        /// <summary>Где разработчик получает песочный ключ: кабинет разработчика → игра → «Тест в Unity Editor».</summary>
        public const string GetKeyUrl = "https://vhrgames.ru/dev/games";

        /// <summary>Стартовый баланс тестового игрока (и в песочнице сервера, и в локальной симуляции).</summary>
        public const long StartBalance = 10000;

        private static string _prefix;

        /// <summary>Префикс ключей EditorPrefs/SessionState, уникальный для проекта.</summary>
        internal static string Prefix => _prefix ??= "VhrSdk.EditorTesting." + ProjectHash() + ".";

        private static string ProjectHash()
        {
            string path;
            try { path = Application.dataPath ?? string.Empty; }
            catch { path = string.Empty; }
            path = path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

            // FNV-1a 64: детерминирован между сессиями (string.GetHashCode — нет).
            ulong h = 14695981039346656037UL;
            foreach (var b in Encoding.UTF8.GetBytes(path))
            {
                h ^= b;
                h *= 1099511628211UL;
            }
            return h.ToString("x16", CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- ключ

        /// <summary>Песочный ключ этого проекта (пусто — не задан).</summary>
        public static string Key
        {
            get => EditorPrefs.GetString(Prefix + "Key", string.Empty);
            set
            {
                var v = (value ?? string.Empty).Trim();
                if (v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) v = v.Substring(7).Trim();
                if (v.Length == 0) EditorPrefs.DeleteKey(Prefix + "Key");
                else EditorPrefs.SetString(Prefix + "Key", v);
            }
        }

        /// <summary>Сохранён ли ключ.</summary>
        public static bool HasKey => !string.IsNullOrEmpty(Key);

        /// <summary>
        /// Проверяет ключ локально (без сети): это JWT, песочный (<c>sbx = "1"</c>),
        /// игровой (<c>aud …#game</c>), с <c>gid</c> и не истёк. Подпись проверяет сервер.
        /// </summary>
        public static bool InspectKey(string key, out VhrJwt.Info info, out string problem)
        {
            info = null;
            if (string.IsNullOrWhiteSpace(key))
            {
                problem = "песочный ключ не задан.";
                return false;
            }
            if (!VhrJwt.TryDecode(key, out info))
            {
                problem = "сохранённый ключ не похож на ключ VHR (ожидается JWT из кабинета разработчика).";
                return false;
            }
            if (!info.IsSandbox)
            {
                problem = "это не песочный ключ (нет sbx = 1). Вставьте ключ из кабинета разработчика → игра → " +
                          "«Тест в Unity Editor». Токены настоящих игроков сюда вставлять нельзя.";
                return false;
            }
            if (!string.IsNullOrEmpty(info.Audience) && !info.IsGameAudience)
            {
                problem = "ключ выдан не для игр (audience не оканчивается на #game).";
                return false;
            }
            if (string.IsNullOrEmpty(info.GameId))
            {
                problem = "в ключе нет gid (id игры).";
                return false;
            }
            if (info.IsExpired(TimeSpan.Zero))
            {
                problem = $"срок ключа истёк {info.ExpiresAt.Value.ToLocalTime():dd.MM.yyyy HH:mm}. Получите новый ключ на сайте.";
                return false;
            }
            problem = null;
            return true;
        }

        // --------------------------------------------------------------- режим

        /// <summary>Режим, выбранный в окне VHR (Simulation по умолчанию).</summary>
        public static VhrEditorMode SelectedMode
        {
            get => EditorPrefs.GetInt(Prefix + "Mode", (int)VhrEditorMode.Simulation) == (int)VhrEditorMode.LiveSandbox
                ? VhrEditorMode.LiveSandbox
                : VhrEditorMode.Simulation;
            set => EditorPrefs.SetInt(Prefix + "Mode",
                value == VhrEditorMode.LiveSandbox ? (int)VhrEditorMode.LiveSandbox : (int)VhrEditorMode.Simulation);
        }

        /// <summary>
        /// Итоговый режим для опций: явный из кода или, при <see cref="VhrEditorMode.Auto"/>,
        /// из окна (Live — только если в окне выбран Live и есть ключ).
        /// </summary>
        public static VhrEditorMode Resolve(VhrEditorMode requested)
        {
            if (requested == VhrEditorMode.Simulation || requested == VhrEditorMode.LiveSandbox)
                return requested;
            return SelectedMode == VhrEditorMode.LiveSandbox && HasKey
                ? VhrEditorMode.LiveSandbox
                : VhrEditorMode.Simulation;
        }

        // -------------------------------------------------------------- сервер

        /// <summary>Адрес API для dev-стенда (пусто — прод <see cref="ProdApiServer"/>).</summary>
        public static string ServerOverride
        {
            get => EditorPrefs.GetString(Prefix + "Server", string.Empty);
            set => SetOrDelete("Server", NormalizeUrl(value, ProdApiServer));
        }

        /// <summary>Адрес релея для dev-стенда (пусто — прод <see cref="ProdRelay"/>).</summary>
        public static string RelayOverride
        {
            get => EditorPrefs.GetString(Prefix + "Relay", string.Empty);
            set => SetOrDelete("Relay", NormalizeUrl(value, ProdRelay));
        }

        /// <summary>Фактический адрес API: dev-стенд из окна или прод.</summary>
        public static string ApiServer =>
            string.IsNullOrWhiteSpace(ServerOverride) ? ProdApiServer : ServerOverride.Trim().TrimEnd('/');

        /// <summary>Адрес моста <c>{сервер}/bridge</c> для окна VHR.</summary>
        public static string BridgeBaseUrl => ApiServer + "/bridge";

        private static string NormalizeUrl(string value, string prod)
        {
            var v = (value ?? string.Empty).Trim().TrimEnd('/');
            return string.Equals(v, prod.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ? string.Empty : v;
        }

        private static void SetOrDelete(string name, string value)
        {
            if (string.IsNullOrEmpty(value)) EditorPrefs.DeleteKey(Prefix + name);
            else EditorPrefs.SetString(Prefix + name, value);
        }

        // ------------------------------------------------- применение к опциям

        /// <summary>
        /// Вызывается из <see cref="VhrSdkOptions.Validate"/> один раз. Выставляет
        /// <see cref="VhrSdkOptions.IsEditorSimulation"/> / <see cref="VhrSdkOptions.IsEditorLiveSandbox"/>;
        /// в режиме Live подменяет токен на песочный ключ, <c>GameId</c> — на <c>gid</c>
        /// ключа и, если в окне задан dev-стенд, адреса сервисов.
        /// </summary>
        internal static void ApplyTo(VhrSdkOptions o)
        {
            o.IsEditorSimulation = false;
            o.IsEditorLiveSandbox = false;

            // Серверная интеграция (выделенный сервер / server-to-server) живёт по
            // своим правилам: ни симуляцию, ни песочный ключ к ней не применяем.
            if (!string.IsNullOrEmpty(o.InternalApiKey))
            {
                Debug.Log("[VHR SDK] Editor: задан InternalApiKey (серверная интеграция) — режимы Simulation/Live не применяются.");
                return;
            }

            var mode = Resolve(o.EditorMode);
            if (o.EditorMode == VhrEditorMode.Auto && SelectedMode == VhrEditorMode.LiveSandbox && !HasKey)
                Debug.LogWarning("[VHR SDK] Editor: в окне VHR выбран Live, но песочный ключ не сохранён — работаем в режиме Simulation.");

            if (mode == VhrEditorMode.LiveSandbox)
            {
                var key = Key;
                if (!InspectKey(key, out var info, out var problem))
                {
                    Debug.LogError("[VHR SDK] Editor: выбран режим Live (песочница), но " + problem +
                                   " Откройте VHR → Тестирование в Editor. Пока работаем в режиме Simulation.");
                    o.IsEditorSimulation = true;
                    return;
                }

                if (!string.IsNullOrWhiteSpace(o.GameId) &&
                    !string.Equals(o.GameId.Trim(), info.GameId, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogWarning($"[VHR SDK] Editor (Live): GameId в опциях ('{o.GameId}') не совпадает с игрой " +
                                     $"песочного ключа ('{info.GameId}'). Используем игру ключа — проверьте GameId перед сборкой.");
                }
                o.GameId = info.GameId;

                if (o.TokenProvider != null)
                    Debug.Log("[VHR SDK] Editor (Live): ваш TokenProvider заменён песочным ключом на время теста в редакторе.");
                var captured = key;
                o.TokenProvider = () => captured;

                var server = ServerOverride;
                if (!string.IsNullOrWhiteSpace(server))
                {
                    var b = server.Trim().TrimEnd('/');
                    o.BridgeBaseUrl = b + "/bridge";
                    o.GamesBaseUrl = b + "/games";
                    o.AuthBaseUrl = b + "/auth";
                    o.ServersBaseUrl = b + "/servers";
                    o.NotificationsBaseUrl = b + "/notifications";
                }
                var relay = RelayOverride;
                if (!string.IsNullOrWhiteSpace(relay))
                    o.RelayBaseUrl = relay.Trim();

                o.IsEditorLiveSandbox = true;
                var until = info.ExpiresAt.HasValue ? info.ExpiresAt.Value.ToLocalTime().ToString("dd.MM.yyyy") : "—";
                Debug.Log($"[VHR SDK] Editor: режим Live (песочница) — игра {info.GameId}, тестовый игрок " +
                          $"{(string.IsNullOrEmpty(info.Subject) ? "sbx_*" : info.Subject)}, ключ до {until}. " +
                          $"Все запросы идут на настоящий сервер ({o.BridgeBaseUrl.TrimEnd('/')}), монеты тестовые, " +
                          "реклама симулируется локально.");
                return;
            }

            o.IsEditorSimulation = true;
            Debug.Log($"[VHR SDK] Editor: режим Simulation — экономика локальная (тестовый баланс {SimBalance}), " +
                      "реклама симулируется. Проверить все API на настоящем сервере: VHR → Тестирование в Editor → Live.");
        }

        // ------------------------------------------------ локальная симуляция

        /// <summary>Локальный тестовый баланс режима Simulation (до перезапуска редактора).</summary>
        public static long SimBalance
        {
            get => long.TryParse(SessionState.GetString(Prefix + "SimBalance", string.Empty),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : StartBalance;
            set => SessionState.SetString(Prefix + "SimBalance", value.ToString(CultureInfo.InvariantCulture));
        }

        // Покупки симуляции: строки "externalId \t purchaseId \t itemId \t quantity \t isoTimeUtc".
        private static string[][] SimPurchaseRows()
        {
            var all = SessionState.GetString(Prefix + "SimPurchases", string.Empty);
            var lines = all.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var rows = new string[lines.Length][];
            for (int i = 0; i < lines.Length; i++) rows[i] = lines[i].Split('\t');
            return rows;
        }

        private static string Clean(string s) => (s ?? string.Empty).Replace('\n', ' ').Replace('\t', ' ');

        /// <summary>Повтор покупки с тем же externalId в симуляции — вернуть прошлую покупку.</summary>
        public static bool TryGetSimPurchase(string externalId, out string purchaseId)
        {
            purchaseId = null;
            if (string.IsNullOrEmpty(externalId)) return false;
            foreach (var r in SimPurchaseRows())
            {
                if (r.Length >= 2 && r[0] == Clean(externalId))
                {
                    purchaseId = r[1];
                    return true;
                }
            }
            return false;
        }

        /// <summary>Запомнить покупку симуляции (идемпотентность по externalId + «владение»).</summary>
        public static void RememberSimPurchase(string externalId, string purchaseId, string itemId, int quantity)
        {
            var all = SessionState.GetString(Prefix + "SimPurchases", string.Empty);
            var line = Clean(externalId) + "\t" + Clean(purchaseId) + "\t" + Clean(itemId) + "\t" +
                       quantity.ToString(CultureInfo.InvariantCulture) + "\t" +
                       DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            SessionState.SetString(Prefix + "SimPurchases", all + line + "\n");
        }

        /// <summary>«Инвентарь» симуляции: что куплено в этом сеансе редактора.</summary>
        public static VhrOwnedItem[] GetSimOwned()
        {
            var byItem = new System.Collections.Generic.Dictionary<string, VhrOwnedItem>(StringComparer.Ordinal);
            var order = new System.Collections.Generic.List<VhrOwnedItem>();
            foreach (var r in SimPurchaseRows())
            {
                if (r.Length < 5 || string.IsNullOrEmpty(r[2])) continue;
                int.TryParse(r[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var q);
                if (!byItem.TryGetValue(r[2], out var owned))
                {
                    owned = new VhrOwnedItem { itemId = r[2] };
                    byItem[r[2]] = owned;
                    order.Add(owned);
                }
                owned.quantity += Math.Max(1, q);
                owned.lastPurchasedAt = r[4];
            }
            return order.ToArray();
        }

        /// <summary>Сбросить локальную симуляцию: баланс <see cref="StartBalance"/>, покупки очищены.</summary>
        public static void ResetSimulation()
        {
            SessionState.EraseString(Prefix + "SimBalance");
            SessionState.EraseString(Prefix + "SimPurchases");
        }

        // ---------------------------------------- имитация окна платформы

        /// <summary>
        /// Модальный диалог подтверждения покупки — имитация окна платформы, в котором
        /// на vhrgames.ru игрок подтверждает покупку. <c>true</c> — «Купить».
        /// </summary>
        public static bool ShowPurchaseDialog(string title, int quantity, long price, long? balance, bool live)
        {
            var mode = live ? "Live (песочница, тестовые монеты)" : "Simulation (локально)";
            var priceText = price > 0 ? $"{price} монет (= {price} ₽ на платформе)" : "— (цену определит платформа)";
            var balanceText = balance.HasValue ? $"{balance.Value} монет" : "—";
            var message =
                $"Товар: {title}\n" +
                (quantity > 1 ? $"Количество: {quantity}\n" : string.Empty) +
                $"Цена: {priceText}\n" +
                $"Баланс тестового игрока: {balanceText}\n\n" +
                $"Режим: {mode}.\n" +
                "Это имитация окна подтверждения платформы: на vhrgames.ru игрок подтверждает " +
                "покупку в окне сайта, игра его не рисует.";
            return EditorUtility.DisplayDialog("VHR — подтверждение покупки", message, "Купить", "Отмена");
        }

        /// <summary>Диалог «не хватает монет» — как окно платформы при нехватке средств.</summary>
        public static void ShowInsufficientFundsDialog(string title, long price, long balance, bool live)
        {
            var hint = live
                ? "Тестовый баланс песочницы восстанавливает кнопка «Сбросить тестовые данные» в окне VHR → Тестирование в Editor."
                : "Локальный тестовый баланс восстанавливает кнопка «Сбросить тестовые данные» в окне VHR → Тестирование в Editor.";
            EditorUtility.DisplayDialog("VHR — недостаточно монет",
                $"Товар: {title}\nЦена: {price} монет\nБаланс: {balance} монет\n\n" +
                "На платформе игрок увидит предложение пополнить счёт на сайте. Игра получит статус InsufficientFunds.\n\n" + hint,
                "Понятно");
        }
    }
}
#endif
