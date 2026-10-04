using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using R3;
using UnityEngine;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Экономика игры: баланс игрока, каталог товаров, покупки и инвентарь.
    /// Backed by <c>{BridgeBaseUrl}/api/...</c>.
    /// <para>
    /// <b>Правила платформы (SDK 1.9.0+). 1 монета = 1 рубль.</b> Игроки пополняют
    /// счёт <b>только на платформе</b>, а в играх только тратят — и только на товары
    /// каталога игры, которые разработчик завёл в кабинете на сайте:
    /// </para>
    /// <list type="bullet">
    /// <item>пополнять баланс в игре <b>нельзя</b> — для этого есть
    /// <see cref="OpenTopUp"/>: сайт сам откроет окно пополнения платформы;</item>
    /// <item>списывать или начислять монеты напрямую <b>нельзя</b>:
    /// <see cref="SpendAsync"/> и <see cref="GrantCoinsAsync"/> устарели и сразу
    /// возвращают отказ (сервер отвечает <c>403</c>);</item>
    /// <item>каждую покупку (<see cref="PurchaseAsync"/>) <b>подтверждает игрок</b> в
    /// окне платформы — игра его не рисует и подделать не может;</item>
    /// <item>сторонние платёжные системы, свои формы оплаты и внешние ссылки на
    /// оплату <b>запрещены</b> и блокируются платформой: сетевые запросы игры к
    /// внешним доменам блокируются.</item>
    /// </list>
    /// <para>
    /// Покупки идемпотентны по <c>externalId</c> (GUID, если не передан): повтор с
    /// тем же ключом не спишет монеты дважды.
    /// </para>
    /// </summary>
    public interface IVhrEconomy
    {
        /// <summary>
        /// Горячий R3-поток <see cref="BalanceChanged"/>: после успешной покупки и когда
        /// хост сообщает новый баланс (<c>vhr:balance:changed</c>, напр. после пополнения
        /// на платформе). Удобно для HUD.
        /// </summary>
        Observable<BalanceChanged> BalanceChanged { get; }

        /// <summary>
        /// Событие для кода без R3: новый баланс игрока (в монетах). Приходит после
        /// успешной покупки и когда страница платформы сообщает о пополнении
        /// (<c>{ type:'vhr:balance:changed', balance }</c>). На главном потоке.
        /// </summary>
        event Action<long> OnBalanceChanged;

        /// <summary>
        /// Можно ли сейчас показывать магазин: на WebGL — игра открыта на странице
        /// платформы VHR; в Unity Editor — всегда (Simulation или Live-песочница); в
        /// нативных сборках — <c>false</c> (окна подтверждения платформы там нет).
        /// </summary>
        bool IsPurchaseAvailable { get; }

        /// <summary>
        /// <c>GET /api/balance</c> — баланс текущего игрока в монетах. <paramref name="userId"/>
        /// можно не передавать (<c>null</c>): мост берёт игрока из токена.
        /// </summary>
        Task<VhrBalance> GetBalanceAsync(string userId, CancellationToken ct = default);

        /// <summary>
        /// <c>GET /api/items?gameId=…</c> — каталог товаров этой игры (активные товары,
        /// заведённые в кабинете разработчика: игра → Товары). <c>gameId</c> SDK берёт
        /// сам — из claim <c>gid</c> токена или <see cref="VhrSdkOptions.GameId"/>.
        /// В Editor (Simulation) — локальный каталог из <see cref="VhrSdkOptions.SimulatedItemPrices"/>.
        /// </summary>
        Task<VhrCatalogItem[]> GetItemsAsync(CancellationToken ct = default);

        /// <summary>
        /// <c>GET /api/inventory</c> — что текущий игрок уже купил в этой игре:
        /// <c>[{ itemId, quantity, lastPurchasedAt }]</c>. Для разовых покупок
        /// (не продавать второй раз), восстановления покупок на новом устройстве и
        /// разблокировок. В Editor (Simulation) — покупки текущего сеанса редактора.
        /// </summary>
        Task<VhrOwnedItem[]> GetOwnedItemsAsync(CancellationToken ct = default);

        /// <summary>
        /// Покупка товара каталога <b>с подтверждением игроком</b>:
        /// <list type="number">
        /// <item><c>POST /api/purchase</c> — сервер создаёт намерение покупки (живёт 120 с)
        /// или сразу возвращает уже совершённую покупку при повторе того же <paramref name="externalId"/>;</item>
        /// <item>на vhrgames.ru SDK просит страницу платформы показать игроку окно
        /// подтверждения (<c>postMessage vhr:purchase:confirm</c>) и ждёт ответ до 3 минут;
        /// в Unity Editor показывается диалог-имитация этого окна;</item>
        /// <item>итог — <see cref="VhrPurchaseResult"/> со статусом
        /// <see cref="VhrPurchaseStatus"/>.</item>
        /// </list>
        /// Выдавайте товар только при <see cref="VhrPurchaseResult.IsCompleted"/>.
        /// Метод не бросает исключений (кроме <see cref="OperationCanceledException"/>
        /// при отмене <paramref name="ct"/>): все отказы — статусом результата.
        /// Одновременно идёт одна покупка; повторный вызов во время текущей вернёт
        /// <see cref="VhrPurchaseStatus.Error"/> с кодом <c>busy</c>.
        /// <para>
        /// Пополнять баланс, списывать или начислять монеты из игры нельзя — только
        /// товары каталога с подтверждением игрока. Сторонние платёжки запрещены.
        /// </para>
        /// </summary>
        /// <param name="userId">Не обязателен (<c>null</c>): мост берёт игрока из токена.
        /// Оставлен для совместимости с 1.8.</param>
        /// <param name="itemId">Id товара каталога (<see cref="VhrCatalogItem.id"/>).</param>
        /// <param name="quantity">Количество (≥ 1).</param>
        /// <param name="externalId">Ключ идемпотентности. По умолчанию — новый GUID.
        /// Передайте свой стабильный, чтобы повтор (обрыв сети, перезапуск) не купил дважды.</param>
        /// <param name="ct">Отмена ожидания. Окно подтверждения на сайте при этом не
        /// закрывается — если игрок всё же подтвердит, баланс обновится через
        /// <see cref="OnBalanceChanged"/>.</param>
        Task<VhrPurchaseResult> PurchaseAsync(
            string userId, string itemId, int quantity = 1,
            string externalId = null, CancellationToken ct = default);

        /// <summary>
        /// Основной способ покупки: игрок берётся из токена, передавать его не нужно.
        /// <c>await VhrSdk.Economy.PurchaseAsync(VhrCatalogIds.Items.Sword)</c>.
        /// Полный цикл тот же, что у перегрузки с <c>userId</c>: намерение на
        /// сервере → подтверждение игроком в окне платформы → итог.
        /// </summary>
        /// <param name="itemId">Id товара каталога (<see cref="VhrCatalogItem.id"/>).</param>
        /// <param name="quantity">Количество (≥ 1).</param>
        /// <param name="ct">Отмена ожидания (окно на сайте при этом не закрывается).</param>
        Task<VhrPurchaseResult> PurchaseAsync(string itemId, int quantity = 1, CancellationToken ct = default);

        /// <summary>
        /// Колбэк-версия <see cref="PurchaseAsync(string, int, CancellationToken)"/> для кода без async. Колбэк
        /// вызывается ровно один раз, на главном потоке.
        /// </summary>
        void Purchase(string itemId, int quantity, Action<VhrPurchaseResult> onComplete, string externalId = null);

        /// <summary>
        /// Открыть пополнение счёта <b>на платформе</b>. На vhrgames.ru SDK просит
        /// страницу открыть окно пополнения (<c>postMessage { type:'vhr:topup:open' }</c>);
        /// после пополнения придёт <see cref="OnBalanceChanged"/>. В Unity Editor
        /// открывает страницу платформы в браузере (<c>Application.OpenURL</c>) и пишет лог.
        /// Пополнение внутри игры (свои формы, сторонние платёжки) запрещено.
        /// </summary>
        void OpenTopUp();

        /// <summary>
        /// <b>Устарело и отключено.</b> Игры не могут начислять монеты: монеты = рубли,
        /// пополнение — только на платформе (сервер отвечает <c>403</c> на
        /// <c>POST /api/grant/coins</c>). Метод сразу, без запроса к серверу, возвращает
        /// <c>success = false</c>, <c>code = "grant_disabled"</c>. Награждайте игрока
        /// внутриигровыми ценностями.
        /// </summary>
        [Obsolete(VhrEconomyService.GrantObsoleteMessage, false)]
        Task<VhrEconomyResult> GrantCoinsAsync(
            string userId, long amount, string reason,
            string externalId = null, CancellationToken ct = default);

        /// <summary>
        /// <c>POST /api/grant/achievement</c> — записать разблокировку ачивки. Монетная
        /// награда из игры не начисляется (форсится в 0): игры не начисляют монеты.
        /// </summary>
        Task<VhrEconomyResult> GrantAchievementAsync(
            string userId, string achievementId,
            string externalId = null, CancellationToken ct = default);

        /// <summary>
        /// <b>Устарело и отключено.</b> Прямые списания из игры запрещены (сервер отвечает
        /// <c>403 spend_disabled</c>): игрок тратит монеты только на товары каталога,
        /// подтверждая покупку в окне платформы. Метод сразу, без запроса к серверу,
        /// возвращает <c>success = false</c>, <c>code = "spend_disabled"</c>.
        /// Используйте <see cref="PurchaseAsync"/>.
        /// </summary>
        [Obsolete(VhrEconomyService.SpendObsoleteMessage, false)]
        Task<VhrEconomyResult> SpendAsync(
            string userId, long amount, string reason,
            string externalId = null, CancellationToken ct = default);

        /// <summary>
        /// <c>POST /api/ad</c> — <b>устаревший</b> репорт рекламного события из игры
        /// без реального показа рекламы.
        /// <para>
        /// <b>Доход по этому методу больше не засчитывается.</b> С SDK 1.8.0 рекламу
        /// показывает сайт VHR (Рекламная сеть Яндекса), и учёт показов ведёт только
        /// он — по факту отрисовки. Используйте <see cref="IVhrAds.ShowInterstitialAsync"/> /
        /// <see cref="IVhrAds.ShowRewardedAsync"/> (<c>VhrSdk.Ads</c>). Метод оставлен,
        /// чтобы старые сборки компилировались; запрос по-прежнему уходит на сервер.
        /// </para>
        /// </summary>
        [Obsolete("ReportAdAsync устарел: доход по нему больше не засчитывается. Показывайте рекламу через VhrSdk.Ads.ShowInterstitialAsync()/ShowRewardedAsync() — учёт показов делает сайт VHR.")]
        Task<VhrAdResult> ReportAdAsync(string type = "impression", CancellationToken ct = default);
    }

    /// <summary>
    /// Реализация <see cref="IVhrEconomy"/>: HTTP к мосту + подтверждение покупки
    /// через страницу-хост VHR (WebGL, <see cref="VhrWebGlEconomyChannel"/>) или
    /// диалог-имитацию в Unity Editor. В Editor в режиме Simulation экономика целиком
    /// локальная (тестовый баланс, без сервера).
    /// </summary>
    public sealed class VhrEconomyService : IVhrEconomy, IDisposable
    {
        /// <summary>Страница платформы с пополнением кошелька (для Editor и нативных сборок).</summary>
        public const string TopUpPageUrl = "https://vhrgames.ru/profile?tab=shop";

        /// <summary>Сколько ждать ответа хоста на запрос подтверждения (намерение живёт 120 с, хост сам сообщит expired).</summary>
        internal const int HostConfirmTimeoutMs = 180000;

        internal const string SpendObsoleteMessage =
            "SpendAsync отключён с SDK 1.9.0: игры не могут списывать монеты напрямую (сервер отвечает 403 spend_disabled). " +
            "Продавайте товары каталога через PurchaseAsync — игрок подтверждает покупку в окне платформы. " +
            "Метод сразу возвращает success=false, code=\"spend_disabled\" без запроса к серверу.";

        internal const string GrantObsoleteMessage =
            "GrantCoinsAsync отключён с SDK 1.9.0: игры не могут начислять монеты (монеты = рубли, пополнение — только на платформе; " +
            "сервер отвечает 403). Награждайте внутриигровыми ценностями. " +
            "Метод сразу возвращает success=false, code=\"grant_disabled\" без запроса к серверу.";

        private readonly VhrApiClient _api;
        private readonly VhrSdkOptions _options;
        private readonly IVhrLog _log;
        private readonly Subject<BalanceChanged> _balanceChanged = new();

        private long? _lastBalance;
        // Одна покупка на процесс: статический фасад и VContainer создают разные
        // экземпляры сервиса, а окно подтверждения у платформы одно.
        private static bool _purchaseInFlight;

        // Без перезагрузки домена (Enter Play Mode Options) статика переживает Play —
        // сбрасываем, чтобы прерванная покупка не оставила вечный «busy».
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _purchaseInFlight = false;
        private bool _disposed;

        /// <inheritdoc />
        public event Action<long> OnBalanceChanged;

        /// <summary>Creates the economy service.</summary>
        public VhrEconomyService(VhrApiClient api, VhrSdkOptions options, IVhrLog log = null)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _log = log ?? new VhrUnityLog(options.VerboseLogging);

            // WebGL: слушаем хост сразу, чтобы vhr:balance:changed (пополнение на
            // платформе) доходил до игры ещё до первой покупки. Вне WebGL — no-op.
            VhrWebGlEconomyChannel.EnsureInitialized();
            VhrWebGlEconomyChannel.HostBalanceChanged += OnHostBalanceChanged;
        }

        /// <inheritdoc />
        public Observable<BalanceChanged> BalanceChanged => _balanceChanged;

        /// <inheritdoc />
        public bool IsPurchaseAvailable =>
#if UNITY_EDITOR
            true;
#else
            VhrWebGlEconomyChannel.IsSupported && VhrWebGlEconomyChannel.IsHosted();
#endif

        private bool IsSimulation =>
#if UNITY_EDITOR
            _options.IsEditorSimulation;
#else
            false;
#endif

        private string Url(string path) => $"{_options.BridgeBaseUrl}/api/{path}";

        private static string Id(string externalId)
            => string.IsNullOrEmpty(externalId) ? Guid.NewGuid().ToString("N") : externalId;

        // ------------------------------------------------------------ чтение

        /// <inheritdoc />
        public async Task<VhrBalance> GetBalanceAsync(string userId, CancellationToken ct = default)
        {
#if UNITY_EDITOR
            if (IsSimulation)
            {
                var sim = VhrEditorSandbox.SimBalance;
                _lastBalance = sim;
                return new VhrBalance
                {
                    userId = string.IsNullOrEmpty(userId) ? "sim_player" : userId,
                    coins = sim,
                    balance = sim,
                    updatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                };
            }
#endif
            // Без userId — свой баланс по токену (мост: balance/{userId?}).
            var path = string.IsNullOrWhiteSpace(userId) ? "balance" : $"balance/{Uri.EscapeDataString(userId.Trim())}";
            var raw = await _api.SendRawAsync("GET", Url(path), ct: ct);
            if (string.IsNullOrWhiteSpace(raw)) return null;

            VhrBalance b;
            try { b = JsonUtility.FromJson<VhrBalance>(raw); }
            catch (Exception ex)
            {
                throw new VhrSdkException("deserialize_error", $"Не удалось разобрать баланс: {ex.Message}. Body={raw}", 0, ex);
            }
            if (b == null) return null;

            // Мост отдаёт { userId, balance }, исторически SDK читал coins — синхронизируем.
            if (raw.IndexOf("\"coins\"", StringComparison.Ordinal) < 0) b.coins = b.balance;
            else if (raw.IndexOf("\"balance\"", StringComparison.Ordinal) < 0) b.balance = b.coins;
            _lastBalance = b.coins;
            return b;
        }

        /// <inheritdoc />
        public async Task<VhrCatalogItem[]> GetItemsAsync(CancellationToken ct = default)
        {
#if UNITY_EDITOR
            if (IsSimulation) return SimulatedCatalog();
#endif
            var gid = _options.ResolveCurrentGameId();
            if (string.IsNullOrWhiteSpace(gid))
                throw new VhrSdkException("config_invalid", "Не известен id игры: задайте VhrSdkOptions.GameId.");

            var raw = await _api.SendRawAsync("GET", Url($"items?gameId={Uri.EscapeDataString(gid)}"), ct: ct);
            var items = ParseList<CatalogList, VhrCatalogItem>(raw, w => w.items) ?? Array.Empty<VhrCatalogItem>();
            return items;
        }

        /// <inheritdoc />
        public async Task<VhrOwnedItem[]> GetOwnedItemsAsync(CancellationToken ct = default)
        {
#if UNITY_EDITOR
            if (IsSimulation) return VhrEditorSandbox.GetSimOwned();
#endif
            var gid = _options.ResolveCurrentGameId();
            var path = string.IsNullOrWhiteSpace(gid) ? "inventory" : $"inventory?gameId={Uri.EscapeDataString(gid)}";
            var raw = await _api.SendRawAsync("GET", Url(path), ct: ct);
            return ParseList<OwnedList, VhrOwnedItem>(raw, w => w.items) ?? Array.Empty<VhrOwnedItem>();
        }

        // JSON-массив верхнего уровня JsonUtility не парсит — оборачиваем в {"items":[...]};
        // если сервер уже отдал объект с items — парсим как есть.
        private static TItem[] ParseList<TWrapper, TItem>(string raw, Func<TWrapper, TItem[]> pick)
            where TWrapper : class
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var t = raw.TrimStart();
            var json = t.Length > 0 && t[0] == '[' ? "{\"items\":" + raw + "}" : raw;
            try
            {
                var w = JsonUtility.FromJson<TWrapper>(json);
                return w == null ? null : pick(w);
            }
            catch (Exception ex)
            {
                throw new VhrSdkException("deserialize_error", $"Не удалось разобрать ответ: {ex.Message}. Body={raw}", 0, ex);
            }
        }

        // ------------------------------------------------------------ покупка

        /// <inheritdoc />
        public void Purchase(string itemId, int quantity, Action<VhrPurchaseResult> onComplete, string externalId = null)
            => _ = RunPurchaseWithCallbackAsync(itemId, quantity, externalId, onComplete);

        private async Task RunPurchaseWithCallbackAsync(
            string itemId, int quantity, string externalId, Action<VhrPurchaseResult> onComplete)
        {
            VhrPurchaseResult result;
            try
            {
                result = await PurchaseAsync(null, itemId, quantity, externalId, CancellationToken.None);
            }
            catch (Exception e)
            {
                result = VhrPurchaseResult.Fail(VhrPurchaseStatus.Error, "exception", e.Message, itemId, quantity, externalId);
            }

            try { onComplete?.Invoke(result); }
            catch (Exception e) { _log.Error("Economy: колбэк покупки упал: " + e); }
        }

        /// <inheritdoc />
        public Task<VhrPurchaseResult> PurchaseAsync(string itemId, int quantity = 1, CancellationToken ct = default)
            => PurchaseAsync(null, itemId, quantity, null, ct);

        // ⚠️ НЕ добавлять .ConfigureAwait(false): на WebGL нет тредпула, и
        // продолжение после TrySetResult не выполнится никогда (см. CHANGELOG 1.7.6).
        /// <inheritdoc />
        public async Task<VhrPurchaseResult> PurchaseAsync(
            string userId, string itemId, int quantity = 1, string externalId = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (_disposed)
                return VhrPurchaseResult.Fail(VhrPurchaseStatus.Error, "disposed", "Сервис экономики уничтожен.", itemId, quantity, externalId);
            if (string.IsNullOrWhiteSpace(itemId))
                return VhrPurchaseResult.Fail(VhrPurchaseStatus.Error, "bad_request",
                    "Не указан itemId (id товара из каталога игры).", itemId, quantity, externalId);
            if (quantity < 1)
                return VhrPurchaseResult.Fail(VhrPurchaseStatus.Error, "bad_request",
                    "quantity должен быть ≥ 1.", itemId, quantity, externalId);
            if (_purchaseInFlight)
                return VhrPurchaseResult.Fail(VhrPurchaseStatus.Error, "busy",
                    "Уже идёт другая покупка — дождитесь её результата.", itemId, quantity, externalId);

            itemId = itemId.Trim();
            var extId = Id(externalId);
            _purchaseInFlight = true;
            try
            {
#if UNITY_EDITOR
                if (IsSimulation)
                    return await SimulatePurchaseAsync(itemId, quantity, extId);
#endif
                // WebGL вне страницы платформы: окна подтверждения нет — даже не
                // создаём намерение на сервере.
                if (VhrWebGlEconomyChannel.IsSupported && !VhrWebGlEconomyChannel.IsHosted())
                {
                    return Log(VhrPurchaseResult.Fail(VhrPurchaseStatus.Error, "not_hosted",
                        "Покупки работают только на vhrgames.ru: игра должна быть открыта со страницы платформы " +
                        "(игрок подтверждает покупку в окне сайта).", itemId, quantity, extId));
                }

                // 1. Намерение покупки (или готовая покупка при идемпотентном повторе).
                var req = new PurchaseRequest
                {
                    userId = string.IsNullOrWhiteSpace(userId) ? null : userId.Trim(),
                    itemId = itemId,
                    quantity = quantity,
                    externalId = extId,
                    gameId = _options.ResolveCurrentGameId()
                };

                string raw;
                try
                {
                    raw = await _api.SendRawAsync("POST", Url("purchase"), req, ct: ct);
                }
                catch (VhrSdkException ex)
                {
                    // Транспорт на отмену отвечает «canceled» (status 0) — отдаём честную отмену.
                    ct.ThrowIfCancellationRequested();
                    return Log(FromHttpError(ex, itemId, quantity, extId));
                }

                var dto = ParseDto(raw);
                if (dto == null)
                    return Log(VhrPurchaseResult.Fail(VhrPurchaseStatus.Error, "empty_response",
                        "Сервер вернул пустой ответ на покупку.", itemId, quantity, extId));

                var status = (dto.status ?? string.Empty).Trim().ToLowerInvariant();
                if (status == "confirmation_required" && !string.IsNullOrEmpty(dto.intentId))
                {
                    // 2. Подтверждение игроком.
                    VhrPurchaseResult confirmed;
#if UNITY_EDITOR
                    confirmed = await ConfirmInEditorAsync(dto, itemId, quantity, extId, ct);
#else
                    if (VhrWebGlEconomyChannel.IsSupported)
                        confirmed = await ConfirmViaHostAsync(dto, itemId, quantity, extId, ct);
                    else
                        confirmed = VhrPurchaseResult.Fail(VhrPurchaseStatus.Error, "confirmation_unavailable",
                            "Подтвердить покупку можно только на vhrgames.ru (WebGL) или в Unity Editor. " +
                            "В этой сборке окна подтверждения платформы нет.", itemId, quantity, extId);
#endif
                    return Log(confirmed);
                }

                // Готовый итог без подтверждения (идемпотентный повтор / старый сервер).
                return Log(Finish(FromDto(dto, raw, itemId, quantity, extId, initialPost: true)));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                return Log(VhrPurchaseResult.Fail(VhrPurchaseStatus.Error, "exception", e.Message, itemId, quantity, extId));
            }
            finally
            {
                _purchaseInFlight = false;
            }
        }

        /// <summary>WebGL: окно подтверждения на странице платформы (postMessage).</summary>
        private async Task<VhrPurchaseResult> ConfirmViaHostAsync(
            PurchaseResponseDto intent, string itemId, int quantity, string extId, CancellationToken ct)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<VhrPurchaseResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            _log.Verbose($"Economy: ждём подтверждения игрока (intent={intent.intentId}, requestId={requestId}).");
            int handle = VhrWebGlEconomyChannel.Confirm(intent.intentId, requestId, HostConfirmTimeoutMs,
                (st, reason, payload) =>
                {
                    // Итог фиксируем (и эмитим баланс) прямо здесь: даже если игра уже
                    // отменила ожидание (ct), подтверждённая покупка обновит баланс.
                    var r = Finish(FromHost(st, reason, payload, intent, itemId, quantity, extId));
                    tcs.TrySetResult(r);
                });

            if (handle <= 0)
            {
                return VhrPurchaseResult.Fail(VhrPurchaseStatus.Error, "not_hosted",
                    "Не удалось связаться со страницей платформы для подтверждения покупки.", itemId, quantity, extId);
            }

            if (!ct.CanBeCanceled)
                return await tcs.Task;

            var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancelTcs.TrySetResult(true)))
            {
                var finished = await Task.WhenAny(tcs.Task, cancelTcs.Task);
                if (finished != tcs.Task)
                    throw new OperationCanceledException(ct);
                return await tcs.Task;
            }
        }

        private VhrPurchaseResult FromHost(int st, int reason, VhrWebGlEconomyChannel.HostPayload p,
            PurchaseResponseDto intent, string itemId, int quantity, string extId)
        {
            var r = new VhrPurchaseResult
            {
                ItemId = itemId,
                Quantity = intent.quantity > 0 ? intent.quantity : quantity,
                Title = intent.title,
                Price = intent.price,
                IntentId = intent.intentId,
                ExternalId = extId,
                PurchaseId = string.IsNullOrEmpty(p?.purchaseId) ? null : p.purchaseId,
                HasBalance = p != null && p.hasBalance,
                Balance = p != null && p.hasBalance ? p.balance : 0
            };

            switch (st)
            {
                case VhrWebGlEconomyChannel.StatusCompleted:
                    r.Status = VhrPurchaseStatus.Completed;
                    break;
                case VhrWebGlEconomyChannel.StatusCancelled:
                    r.Status = VhrPurchaseStatus.Cancelled;
                    r.ErrorCode = "cancelled";
                    r.Message = "Игрок отменил покупку.";
                    break;
                case VhrWebGlEconomyChannel.StatusInsufficientFunds:
                    r.Status = VhrPurchaseStatus.InsufficientFunds;
                    r.ErrorCode = "insufficient_funds";
                    r.Message = "Недостаточно монет. Предложите пополнить счёт на платформе (OpenTopUp).";
                    break;
                case VhrWebGlEconomyChannel.StatusExpired:
                    r.Status = VhrPurchaseStatus.Expired;
                    r.ErrorCode = reason == VhrWebGlEconomyChannel.ReasonTimeout ? "host_timeout" : "expired";
                    r.Message = reason == VhrWebGlEconomyChannel.ReasonTimeout
                        ? "Страница платформы не ответила за 3 минуты — покупка не состоялась."
                        : "Игрок не подтвердил покупку вовремя.";
                    break;
                default:
                    r.Status = VhrPurchaseStatus.Error;
                    r.ErrorCode = !string.IsNullOrEmpty(p?.error) ? p.error
                        : reason == VhrWebGlEconomyChannel.ReasonPostFailed ? "post_failed"
                        : reason == VhrWebGlEconomyChannel.ReasonBusy ? "busy"
                        : "host_error";
                    r.Message = "Платформа сообщила об ошибке покупки.";
                    break;
            }
            return r;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Unity Editor (Live-песочница): диалог-имитация окна платформы → confirm/cancel
        /// намерения на настоящем сервере (подтверждать из Editor можно только песочные намерения).
        /// </summary>
        private async Task<VhrPurchaseResult> ConfirmInEditorAsync(
            PurchaseResponseDto intent, string itemId, int quantity, string extId, CancellationToken ct)
        {
            long? balance = null;
            try
            {
                var b = await GetBalanceAsync(null, ct);
                if (b != null) balance = b.coins;
            }
            catch (VhrSdkException) { /* баланс в диалоге — только справочно */ }

            var title = string.IsNullOrEmpty(intent.title) ? itemId : intent.title;
            var qty = intent.quantity > 0 ? intent.quantity : quantity;
            var intentPath = $"purchase-intents/{Uri.EscapeDataString(intent.intentId)}";

            bool ok = VhrEditorSandbox.ShowPurchaseDialog(title, qty, intent.price, balance, _options.IsEditorLiveSandbox);
            if (!ok)
            {
                try { await _api.SendRawAsync("POST", Url(intentPath + "/cancel"), new EmptyBody(), ct: ct); }
                catch (VhrSdkException ex) { _log.Verbose($"Economy: отмена намерения не дошла ({ex.Code}) — оно истечёт само."); }

                return new VhrPurchaseResult
                {
                    Status = VhrPurchaseStatus.Cancelled, ErrorCode = "cancelled", Message = "Покупка отменена в диалоге Editor.",
                    ItemId = itemId, Quantity = qty, Title = intent.title, Price = intent.price,
                    IntentId = intent.intentId, ExternalId = extId, IsTest = true
                };
            }

            string raw;
            try
            {
                raw = await _api.SendRawAsync("POST", Url(intentPath + "/confirm"), new EmptyBody(), ct: ct);
            }
            catch (VhrSdkException ex)
            {
                var fail = FromHttpError(ex, itemId, qty, extId);
                fail.IntentId = intent.intentId;
                fail.Title = intent.title;
                fail.Price = intent.price;
                fail.IsTest = true;
                if (fail.Status == VhrPurchaseStatus.InsufficientFunds)
                    VhrEditorSandbox.ShowInsufficientFundsDialog(title, intent.price, fail.HasBalance ? fail.Balance : balance ?? 0, true);
                return fail;
            }

            var dto = ParseDto(raw) ?? new PurchaseResponseDto();
            var r = FromDto(dto, raw ?? string.Empty, itemId, qty, extId, initialPost: false);
            r.IntentId = intent.intentId;
            r.Title ??= intent.title;
            if (r.Price == 0) r.Price = intent.price;
            r.IsTest = true;
            if (r.Status == VhrPurchaseStatus.InsufficientFunds)
                VhrEditorSandbox.ShowInsufficientFundsDialog(title, intent.price, r.HasBalance ? r.Balance : balance ?? 0, true);
            return Finish(r);
        }

        /// <summary>Unity Editor (Simulation): локальная покупка без сервера.</summary>
        private async Task<VhrPurchaseResult> SimulatePurchaseAsync(string itemId, int quantity, string extId)
        {
            // Кадр паузы — как с настоящим сервером, чтобы UI игры успел обновиться.
            await Task.Yield();

            if (VhrEditorSandbox.TryGetSimPurchase(extId, out var prevId))
            {
                var bal0 = VhrEditorSandbox.SimBalance;
                return new VhrPurchaseResult
                {
                    Status = VhrPurchaseStatus.Completed, ItemId = itemId, Quantity = quantity, PurchaseId = prevId,
                    Balance = bal0, HasBalance = true, ExternalId = extId, IdempotentReplay = true, IsTest = true,
                    Message = "Симуляция: повтор покупки с тем же externalId — монеты не списаны."
                };
            }

            long unit = _options.SimulatedDefaultItemPrice;
            if (_options.SimulatedItemPrices != null && _options.SimulatedItemPrices.TryGetValue(itemId, out var p))
                unit = p;
            long price = Math.Max(0, unit) * quantity;
            long bal = VhrEditorSandbox.SimBalance;

            if (bal < price)
            {
                VhrEditorSandbox.ShowInsufficientFundsDialog(itemId, price, bal, false);
                return new VhrPurchaseResult
                {
                    Status = VhrPurchaseStatus.InsufficientFunds, ErrorCode = "insufficient_funds",
                    Message = "Симуляция: недостаточно тестовых монет.", ItemId = itemId, Quantity = quantity,
                    Price = price, Balance = bal, HasBalance = true, ExternalId = extId, IsTest = true
                };
            }

            if (!VhrEditorSandbox.ShowPurchaseDialog(itemId, quantity, price, bal, false))
            {
                return new VhrPurchaseResult
                {
                    Status = VhrPurchaseStatus.Cancelled, ErrorCode = "cancelled", Message = "Симуляция: покупка отменена.",
                    ItemId = itemId, Quantity = quantity, Price = price, ExternalId = extId, IsTest = true
                };
            }

            bal -= price;
            VhrEditorSandbox.SimBalance = bal;
            var purchaseId = "sim_" + Guid.NewGuid().ToString("N");
            VhrEditorSandbox.RememberSimPurchase(extId, purchaseId, itemId, quantity);

            return Finish(new VhrPurchaseResult
            {
                Status = VhrPurchaseStatus.Completed, ItemId = itemId, Quantity = quantity, Price = price,
                PurchaseId = purchaseId, Balance = bal, HasBalance = true, ExternalId = extId, IsTest = true,
                Message = "Симуляция: покупка совершена локально (сервер не вызывался)."
            });
        }

        private VhrCatalogItem[] SimulatedCatalog()
        {
            var map = _options.SimulatedItemPrices;
            if (map == null || map.Count == 0) return Array.Empty<VhrCatalogItem>();
            var list = new VhrCatalogItem[map.Count];
            int i = 0;
            foreach (var kv in map)
            {
                list[i++] = new VhrCatalogItem
                {
                    id = kv.Key, code = kv.Key, title = kv.Key, priceCoins = kv.Value, active = true,
                    gameId = _options.GameId, description = "Товар локальной симуляции (VhrSdkOptions.SimulatedItemPrices)."
                };
            }
            return list;
        }
#endif

        // ---------------------------------------------------- разбор ответов

        private static PurchaseResponseDto ParseDto(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try { return JsonUtility.FromJson<PurchaseResponseDto>(raw); }
            catch { return null; }
        }

        /// <param name="initialPost"><c>true</c> — ответ на <c>POST purchase</c>: по контракту 1.9
        /// «completed» сразу (без намерения) приходит только на идемпотентный повтор.</param>
        private VhrPurchaseResult FromDto(PurchaseResponseDto dto, string raw, string itemId, int quantity, string extId,
            bool initialPost)
        {
            bool hasApplied = raw.IndexOf("\"applied\"", StringComparison.Ordinal) >= 0;
            var status = (dto.status ?? string.Empty).Trim().ToLowerInvariant();
            bool hasBalance = raw.IndexOf("\"balance\"", StringComparison.Ordinal) >= 0;
            var r = new VhrPurchaseResult
            {
                ItemId = string.IsNullOrEmpty(dto.itemId) ? itemId : dto.itemId,
                Quantity = dto.quantity > 0 ? dto.quantity : quantity,
                Title = dto.title,
                Price = dto.price,
                PurchaseId = string.IsNullOrEmpty(dto.purchaseId) ? null : dto.purchaseId,
                Balance = hasBalance ? dto.balance : 0,
                HasBalance = hasBalance,
                IntentId = dto.intentId,
                ExternalId = extId,
                IsTest = _options.IsEditorLiveSandbox || _options.IsEditorSimulation
            };

            switch (status)
            {
                case "completed":
                    r.Status = VhrPurchaseStatus.Completed;
                    // Новый сервер: на POST purchase «completed» без намерения = повтор того же
                    // externalId. Явный applied (если сервер его прислал) важнее.
                    r.IdempotentReplay = hasApplied ? !dto.applied : initialPost;
                    break;
                case "cancelled":
                    r.Status = VhrPurchaseStatus.Cancelled; r.ErrorCode = "cancelled";
                    break;
                case "insufficient_funds":
                    r.Status = VhrPurchaseStatus.InsufficientFunds; r.ErrorCode = "insufficient_funds";
                    break;
                case "expired":
                    r.Status = VhrPurchaseStatus.Expired; r.ErrorCode = "expired";
                    break;
                case "":
                    // Сервер до 1.9 (без статуса) уже списал монеты и вернул { balance, purchaseId, applied }.
                    if (!string.IsNullOrEmpty(dto.purchaseId))
                    {
                        r.Status = VhrPurchaseStatus.Completed;
                        r.IdempotentReplay = hasApplied && !dto.applied;
                    }
                    else
                    {
                        r.Status = VhrPurchaseStatus.Error; r.ErrorCode = "unexpected_response";
                        r.Message = "Неожиданный ответ сервера на покупку: " + raw;
                    }
                    break;
                default:
                    r.Status = VhrPurchaseStatus.Error;
                    r.ErrorCode = string.IsNullOrEmpty(dto.code) ? status : dto.code;
                    r.Message = string.IsNullOrEmpty(dto.message) ? "Сервер вернул статус покупки: " + status : dto.message;
                    break;
            }
            return r;
        }

        /// <summary>HTTP-ошибка моста → статус покупки (по телу <c>{ code }</c> и HTTP-статусу).</summary>
        private VhrPurchaseResult FromHttpError(VhrSdkException ex, string itemId, int quantity, string extId)
        {
            var code = (ex.ServerCode ?? string.Empty).ToLowerInvariant();
            var http = ex.HttpStatus;
            var r = VhrPurchaseResult.Fail(VhrPurchaseStatus.Error, string.IsNullOrEmpty(code) ? ex.Code : code,
                ex.Message, itemId, quantity, extId);
            r.IsTest = _options.IsEditorLiveSandbox;

            if (http == 426 || code == "sdk_update_required")
            {
                r.Status = VhrPurchaseStatus.SdkUpdateRequired;
                r.ErrorCode = "sdk_update_required";
                r.Message = $"Сервер требует новую версию VHR SDK (сейчас {VhrSdk.SdkVersion}). Обновите пакет ru.vhrgames.sdk и пересоберите игру.";
            }
            else if (code == "insufficient_funds" || (http == 409 && string.IsNullOrEmpty(code)))
            {
                r.Status = VhrPurchaseStatus.InsufficientFunds;
                r.ErrorCode = "insufficient_funds";
                r.Message = "Недостаточно монет. Предложите пополнить счёт на платформе (OpenTopUp).";
                if (TryReadLong(ex.ResponseBody, "balance", out var bal)) { r.Balance = bal; r.HasBalance = true; }
            }
            else if (http == 410 || code == "expired" || code == "intent_expired")
            {
                r.Status = VhrPurchaseStatus.Expired;
                r.ErrorCode = "expired";
                r.Message = "Намерение покупки истекло (живёт 120 с) — покупка не состоялась.";
            }
            else if (http == 429 || code == "daily_limit")
            {
                r.Status = VhrPurchaseStatus.Forbidden;
                r.ErrorCode = string.IsNullOrEmpty(code) ? "daily_limit" : code;
                r.Message = "Достигнут дневной лимит покупок игрока.";
            }
            else if (http == 403)
            {
                r.Status = VhrPurchaseStatus.Forbidden;
                r.ErrorCode = string.IsNullOrEmpty(code) ? "forbidden" : code;
                r.Message = code switch
                {
                    "parental_block" => "Покупки запрещены родительским контролем аккаунта.",
                    "game_mismatch" => "Игра запроса не совпадает с игрой токена (gid): проверьте VhrSdkOptions.GameId.",
                    "game_token_forbidden" => "Покупку можно совершить только с игровым токеном платформы.",
                    _ => "Платформа запретила покупку."
                };
            }
            else if (http == 401)
            {
                r.ErrorCode = "unauthorized";
                r.Message =
#if UNITY_EDITOR
                    "Сервер не принял ключ: проверьте песочный ключ в окне VHR → Тестирование в Editor.";
#else
                    "Нет действующего токена игрока: игрок должен быть авторизован на vhrgames.ru.";
#endif
            }
            else if (http == 404)
            {
                r.ErrorCode = string.IsNullOrEmpty(code) ? "not_found" : code;
                r.Message = "Товар не найден или снят с продажи (проверьте itemId — это id товара из каталога игры).";
            }
            else if (http == 0)
            {
                r.ErrorCode = "connection_error";
                r.Message = "Нет связи с сервером VHR.";
            }
            return r;
        }

        private static bool TryReadLong(string json, string name, out long value)
        {
            value = 0;
            if (string.IsNullOrEmpty(json)) return false;
            var s = VhrJwt.ReadClaim(json, name);
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Итог покупки: при успехе обновляет баланс (событие + поток R3) один раз.</summary>
        private VhrPurchaseResult Finish(VhrPurchaseResult r)
        {
            if (r == null || !r.IsCompleted) return r;
            if (r.HasBalance)
                EmitBalance(r.Balance, r.IdempotentReplay ? 0 : (r.Price > 0 ? -r.Price : (long?)null), "purchase");
            else if (!r.IdempotentReplay)
                _ = RefreshBalanceQuietlyAsync("purchase");
            return r;
        }

        private VhrPurchaseResult Log(VhrPurchaseResult r)
        {
            if (r == null) return null;
            if (r.IsCompleted || r.Status == VhrPurchaseStatus.Cancelled) _log.Verbose("Economy: " + r);
            else if (r.Status == VhrPurchaseStatus.SdkUpdateRequired) _log.Error("Economy: " + r.Message);
            else _log.Warn($"Economy: покупка не состоялась — {r}. {r.Message}");
            return r;
        }

        // ---------------------------------------------------------- баланс

        private async Task RefreshBalanceQuietlyAsync(string reason)
        {
            try
            {
                var b = await GetBalanceAsync(null, CancellationToken.None);
                if (b != null) EmitBalance(b.coins, null, reason);
            }
            catch (Exception e)
            {
                _log.Verbose("Economy: не удалось обновить баланс после покупки: " + e.Message);
            }
        }

        private void OnHostBalanceChanged(long balance) => EmitBalance(balance, null, "host");

        private void EmitBalance(long newBalance, long? delta, string reason)
        {
            if (_disposed) return;
            long d = delta ?? (_lastBalance.HasValue ? newBalance - _lastBalance.Value : 0);
            _lastBalance = newBalance;
            try { _balanceChanged.OnNext(new BalanceChanged(null, newBalance, d, reason)); }
            catch (Exception e) { _log.Error("Economy: подписчик BalanceChanged упал: " + e); }
            try { OnBalanceChanged?.Invoke(newBalance); }
            catch (Exception e) { _log.Error("Economy: обработчик OnBalanceChanged упал: " + e); }
        }

        // ------------------------------------------------------- пополнение

        /// <inheritdoc />
        public void OpenTopUp()
        {
#if UNITY_EDITOR
            _log.Info("Пополнение: в Unity Editor открываю страницу платформы " + TopUpPageUrl +
                      ". В игре на vhrgames.ru SDK попросит сайт открыть окно пополнения (postMessage vhr:topup:open). " +
                      (IsSimulation
                          ? "В режиме Simulation тестовый баланс от этого не меняется."
                          : "Тестовый баланс песочницы восстанавливает «Сбросить тестовые данные» в окне VHR → Тестирование в Editor."));
            Application.OpenURL(TopUpPageUrl);
#else
            if (VhrWebGlEconomyChannel.IsSupported)
            {
                if (VhrWebGlEconomyChannel.IsHosted() && VhrWebGlEconomyChannel.OpenTopUp())
                {
                    _log.Verbose("Economy: попросили платформу открыть окно пополнения.");
                    return;
                }
                _log.Warn("Пополнение доступно только на vhrgames.ru: игра открыта не со страницы платформы.");
                return;
            }
            _log.Info("Пополнение счёта — на платформе: открываю " + TopUpPageUrl);
            Application.OpenURL(TopUpPageUrl);
#endif
        }

        // ------------------------------------------- отключённые операции

        /// <inheritdoc />
        [Obsolete(GrantObsoleteMessage, false)]
        public Task<VhrEconomyResult> GrantCoinsAsync(
            string userId, long amount, string reason, string externalId = null, CancellationToken ct = default)
        {
            _log.Warn("GrantCoinsAsync отключён: игры не могут начислять монеты (пополнение — только на платформе). Запрос не отправлен.");
            return Task.FromResult(new VhrEconomyResult
            {
                success = false,
                balance = _lastBalance ?? 0,
                code = "grant_disabled",
                message = "Начисление монет из игры запрещено платформой (SDK 1.9.0+)."
            });
        }

        /// <inheritdoc />
        [Obsolete(SpendObsoleteMessage, false)]
        public Task<VhrEconomyResult> SpendAsync(
            string userId, long amount, string reason, string externalId = null, CancellationToken ct = default)
        {
            _log.Warn("SpendAsync отключён: прямые списания из игры запрещены — используйте PurchaseAsync (товар каталога с подтверждением игрока). Запрос не отправлен.");
            return Task.FromResult(new VhrEconomyResult
            {
                success = false,
                balance = _lastBalance ?? 0,
                code = "spend_disabled",
                message = "Прямые списания из игры запрещены платформой (SDK 1.9.0+). Используйте PurchaseAsync."
            });
        }

        /// <inheritdoc />
        /// <remarks>Монетная награда из игры форсится мостом в 0 — игры не начисляют монеты.</remarks>
        public async Task<VhrEconomyResult> GrantAchievementAsync(
            string userId, string achievementId, string externalId = null, CancellationToken ct = default)
        {
#if UNITY_EDITOR
            if (IsSimulation)
            {
                return new VhrEconomyResult
                {
                    success = true, balance = VhrEditorSandbox.SimBalance, message = "simulated",
                };
            }
#endif
            var req = new GrantAchievementRequest
            {
                userId = userId, achievementId = achievementId, externalId = Id(externalId)
            };
            return await _api.SendAsync<VhrEconomyResult>("POST", Url("grant/achievement"), req, ct: ct);
        }

        /// <inheritdoc />
        [Obsolete("ReportAdAsync устарел: доход по нему больше не засчитывается. Показывайте рекламу через VhrSdk.Ads.ShowInterstitialAsync()/ShowRewardedAsync() — учёт показов делает сайт VHR.")]
        public async Task<VhrAdResult> ReportAdAsync(string type = "impression", CancellationToken ct = default)
        {
            var req = new AdReportRequest { type = string.IsNullOrWhiteSpace(type) ? "impression" : type };
            var res = await _api.SendAsync<VhrAdResult>(
                "POST", Url("ad"), req, allowNotImplemented: true, ct: ct);
            return res ?? new VhrAdResult { accepted = false, revenue = 0 };
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            VhrWebGlEconomyChannel.HostBalanceChanged -= OnHostBalanceChanged;
            OnBalanceChanged = null;
            _balanceChanged.Dispose();
        }

        // --- DTO (JsonUtility; имена полей — контракт моста) ---
#pragma warning disable CS0649 // поля заполняет JsonUtility

        [Serializable] private sealed class GrantAchievementRequest
        { public string userId; public string achievementId; public string externalId; }

        [Serializable] private sealed class PurchaseRequest
        { public string userId; public string itemId; public int quantity; public string externalId; public string gameId; }

        [Serializable] private sealed class EmptyBody { }

        // Ответ purchase / purchase-intents/{id}/confirm:
        // 202 { status:"confirmation_required", intentId, itemId, title, quantity, price, expiresAt }
        // 200 { status:"completed", purchaseId, balance } (+ applied у сервера до 1.9)
        [Serializable] private sealed class PurchaseResponseDto
        {
            public string status;
            public string intentId;
            public string itemId;
            public string title;
            public int quantity;
            public long price;
            public string expiresAt;
            public string purchaseId;
            public long balance;
            public bool applied;
            public string code;
            public string message;
        }

        [Serializable] private sealed class AdReportRequest { public string type; }

        [Serializable] private sealed class CatalogList { public VhrCatalogItem[] items; }

        [Serializable] private sealed class OwnedList { public VhrOwnedItem[] items; }
#pragma warning restore CS0649
    }
}
