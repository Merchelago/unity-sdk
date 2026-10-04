using System;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Connection / readiness state of the SDK, surfaced as an R3
    /// <c>Observable&lt;VhrConnectionState&gt;</c> by <see cref="IVhrSession"/>.
    /// </summary>
    public enum VhrConnectionState
    {
        /// <summary>SDK constructed but <see cref="VhrSdk.InitializeAsync"/> not yet called.</summary>
        Uninitialized = 0,

        /// <summary>Initialization in progress (validating options, optional ping).</summary>
        Connecting = 1,

        /// <summary>Options valid and (if pinged) the bridge responded. Ready for calls.</summary>
        Connected = 2,

        /// <summary>Initialization failed or the bridge is unreachable. Calls may still be retried.</summary>
        Faulted = 3
    }

    /// <summary>
    /// The current coin / soft-currency balance for a user as returned by
    /// <c>GET /bridge/api/balance/{userId}</c>.
    /// </summary>
    [Serializable]
    public sealed class VhrBalance
    {
        /// <summary>Owning VHR user id.</summary>
        public string userId;

        /// <summary>Current coin balance (non-negative). 1 монета = 1 рубль.</summary>
        public long coins;

        /// <summary>
        /// Тот же баланс под именем, которое отдаёт мост (<c>{ userId, balance }</c>).
        /// SDK 1.9.0 синхронизирует <see cref="coins"/> и <see cref="balance"/> — раньше
        /// <see cref="coins"/> из ответа моста оставался 0.
        /// </summary>
        public long balance;

        /// <summary>Server-side UTC timestamp the balance was computed (ISO-8601), if provided.</summary>
        public string updatedAtUtc;
    }

    /// <summary>
    /// Event pushed onto <see cref="IVhrEconomy.BalanceChanged"/> whenever a
    /// grant / spend / purchase locally completes and returns a new balance.
    /// </summary>
    [Serializable]
    public readonly struct BalanceChanged
    {
        /// <summary>User whose balance changed.</summary>
        public readonly string UserId;

        /// <summary>Balance after the operation.</summary>
        public readonly long NewBalance;

        /// <summary>Signed delta applied (+grant, -spend).</summary>
        public readonly long Delta;

        /// <summary>Reason tag (e.g. <c>"grant/coins"</c>, <c>"spend"</c>, <c>"purchase"</c>).</summary>
        public readonly string Reason;

        /// <summary>Creates a new <see cref="BalanceChanged"/> event.</summary>
        public BalanceChanged(string userId, long newBalance, long delta, string reason)
        {
            UserId = userId;
            NewBalance = newBalance;
            Delta = delta;
            Reason = reason;
        }
    }

    /// <summary>
    /// Result envelope for an economy mutation (grant / achievement). С SDK 1.9.0
    /// покупка возвращает <see cref="VhrPurchaseResult"/> (приводится к этому типу
    /// неявно), а устаревшие <c>SpendAsync</c>/<c>GrantCoinsAsync</c> — этот тип с
    /// <c>success = false</c> и кодом в <see cref="code"/>.
    /// </summary>
    [Serializable]
    public sealed class VhrEconomyResult
    {
        /// <summary>True when the operation was applied (or was an idempotent replay).</summary>
        public bool success;

        /// <summary>Balance after the operation.</summary>
        public long balance;

        /// <summary>True when the server detected this <c>externalId</c> as an idempotent replay.</summary>
        public bool idempotentReplay;

        /// <summary>Human-readable message / error code from the bridge, if any.</summary>
        public string message;

        /// <summary>
        /// Машиночитаемый код отказа (SDK 1.9.0+), напр. <c>spend_disabled</c>,
        /// <c>grant_disabled</c>. Пусто при успехе.
        /// </summary>
        public string code;
    }

    /// <summary>A single leaderboard row.</summary>
    [Serializable]
    public sealed class VhrLeaderboardEntry
    {
        /// <summary>1-based rank within the requested period.</summary>
        public int rank;

        /// <summary>VHR user id.</summary>
        public string userId;

        /// <summary>Display name, if the bridge resolves it.</summary>
        public string displayName;

        /// <summary>Best score for the period.</summary>
        public long score;
    }

    /// <summary>Top-N leaderboard response.</summary>
    [Serializable]
    public sealed class VhrLeaderboardPage
    {
        /// <summary>Period the page was computed for.</summary>
        public string period;

        /// <summary>Ordered entries (rank ascending).</summary>
        public VhrLeaderboardEntry[] entries;

        /// <summary>
        /// True when the bridge replied <c>501 Not Implemented</c> (leaderboard persistence
        /// is a next-wave seam). <see cref="entries"/> will be empty.
        /// </summary>
        public bool notImplemented;
    }

    /// <summary>Leaderboard aggregation period.</summary>
    public enum VhrLeaderboardPeriod
    {
        /// <summary>All-time best.</summary>
        AllTime = 0,
        /// <summary>Rolling / calendar day.</summary>
        Daily = 1,
        /// <summary>Rolling / calendar week.</summary>
        Weekly = 2,
        /// <summary>Rolling / calendar month.</summary>
        Monthly = 3
    }

    /// <summary>A server binding between a game and a backing server instance.</summary>
    [Serializable]
    public sealed class VhrServerBinding
    {
        /// <summary>Binding id.</summary>
        public string bindingId;

        /// <summary>Game id this binding belongs to.</summary>
        public string gameId;

        /// <summary>Endpoint the game should connect to, or empty for the noop provider.</summary>
        public string endpoint;

        /// <summary>Provider status (e.g. <c>"noop"</c>, <c>"ready"</c>, <c>"provisioning"</c>).</summary>
        public string status;
    }

    /// <summary>
    /// Подобранный игровой сервер для подключения (matchmaking). Адрес —
    /// по домену/uuid (<see cref="host"/>), а не сырому IP; <see cref="connectUri"/>
    /// уже включает протокол (udp/tcp). Если мест нет и квота исчерпана — сервер
    /// вернёт 409 (<see cref="code"/>=<c>"no_capacity"</c>), что SDK маппит в
    /// результат с <see cref="ok"/>=false.
    /// </summary>
    [Serializable]
    public sealed class VhrMatch
    {
        /// <summary>True, если сервер подобран и готов к подключению.</summary>
        public bool ok = true;
        /// <summary>Id инстанса сервера.</summary>
        public string instanceId;
        /// <summary>Хост подключения (домен/uuid, не сырой IP).</summary>
        public string host;
        /// <summary>Порт.</summary>
        public int port;
        /// <summary>Транспорт: <c>"udp"</c> | <c>"tcp"</c>.</summary>
        public string protocol;
        /// <summary>Готовая строка подключения, напр. <c>udp://abc.servers.vhrgames.ru:34521</c>.</summary>
        public string connectUri;
        /// <summary>Вместимость сервера (слотов).</summary>
        public int slots;
        /// <summary>Текущее число игроков на сервере.</summary>
        public int players;
        /// <summary>Машиночитаемый код при отказе (напр. <c>"no_capacity"</c>).</summary>
        public string code;
        /// <summary>Человекочитаемое сообщение при отказе.</summary>
        public string message;
    }

    /// <summary>Вид рекламы, которую игра просит показать (см. <see cref="IVhrAds"/>).</summary>
    public enum VhrAdKind
    {
        /// <summary>Полноэкранная реклама в естественной паузе (между уровнями). Без награды.</summary>
        Interstitial = 0,

        /// <summary>Реклама за награду — только по явному согласию игрока (кнопка «Посмотреть рекламу»).</summary>
        Rewarded = 1
    }

    /// <summary>
    /// Исход показа рекламы (<see cref="IVhrAds.ShowInterstitialAsync"/> /
    /// <see cref="IVhrAds.ShowRewardedAsync"/>).
    /// </summary>
    public enum VhrAdStatus
    {
        /// <summary>
        /// Рекламы сейчас нет: реклама выключена платформой, детский аккаунт,
        /// блокировщик рекламы, нет подходящего объявления, игра открыта не на
        /// сайте VHR. Просто продолжайте игру / спрячьте кнопку «за награду».
        /// Значение по умолчанию (0) — «ничего не показали».
        /// </summary>
        Unavailable = 0,

        /// <summary>Rewarded досмотрен — игра выдаёт СВОЮ внутриигровую награду.</summary>
        Rewarded = 1,

        /// <summary>Реклама была показана и закрыта (для interstitial — обычный исход; для rewarded — закрыли раньше, награды нет).</summary>
        Closed = 2,

        /// <summary>Сработал частотный лимит (или уже идёт другой показ). Попробуйте позже.</summary>
        Cooldown = 3,

        /// <summary>Техническая ошибка показа. Игра продолжается как обычно.</summary>
        Error = 4
    }

    /// <summary>
    /// Что возвращает симуляция рекламы в редакторе / не-WebGL сборке
    /// (<see cref="VhrSdkOptions.AdsSimulation"/>). Позволяет проверить все
    /// ветки игрового кода без сайта.
    /// </summary>
    public enum VhrAdSimulationMode
    {
        /// <summary>Реклама «показана»: rewarded → <see cref="VhrAdStatus.Rewarded"/>, interstitial → <see cref="VhrAdStatus.Closed"/>.</summary>
        Success = 0,

        /// <summary>Реклама «показана», но закрыта раньше: всегда <see cref="VhrAdStatus.Closed"/> (rewarded без награды).</summary>
        ClosedEarly = 1,

        /// <summary>Сразу <see cref="VhrAdStatus.Unavailable"/> (нет рекламы / адблок).</summary>
        Unavailable = 2,

        /// <summary>Сразу <see cref="VhrAdStatus.Cooldown"/> (сработал частотный лимит).</summary>
        Cooldown = 3,

        /// <summary>Сразу <see cref="VhrAdStatus.Error"/>.</summary>
        Error = 4
    }

    /// <summary>
    /// Результат рекламного вызова.
    /// <para>
    /// Основное назначение (SDK 1.8.0+): итог показа через <see cref="IVhrAds"/> —
    /// см. <see cref="Status"/>, <see cref="IsRewarded"/>, <see cref="Kind"/>.
    /// </para>
    /// <para>
    /// Поля <see cref="accepted"/> / <see cref="revenue"/> — наследие устаревшего
    /// <see cref="IVhrEconomy.ReportAdAsync"/> (ответ сервера, заполняется
    /// <c>JsonUtility</c>); для нового API они не используются.
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class VhrAdResult
    {
        /// <summary>[Устарело, только <see cref="IVhrEconomy.ReportAdAsync"/>] Принято сервером.</summary>
        public bool accepted;
        /// <summary>[Устарело, только <see cref="IVhrEconomy.ReportAdAsync"/>] Выручка, которую вернул сервер. Доход по этому пути больше не засчитывается.</summary>
        public long revenue;

        /// <summary>Исход показа.</summary>
        public VhrAdStatus Status { get; set; }

        /// <summary>Какой вид рекламы запрашивался.</summary>
        public VhrAdKind Kind { get; set; }

        /// <summary>Id запроса показа (для логов/сопоставления с <see cref="IVhrAds.OnAdOpened"/>).</summary>
        public string RequestId { get; set; }

        /// <summary>
        /// Машиночитаемая причина для логов (напр. <c>"busy"</c>, <c>"not_hosted"</c>,
        /// <c>"host_timeout"</c>, <c>"simulated"</c>). Может быть <c>null</c>.
        /// Не стройте на ней игровую логику — ориентируйтесь на <see cref="Status"/>.
        /// </summary>
        public string Reason { get; set; }

        /// <summary>
        /// <c>true</c> — rewarded засчитан, игра может выдать свою внутриигровую
        /// награду. Платформенные монеты за рекламу НЕ начисляются.
        /// </summary>
        public bool IsRewarded => Status == VhrAdStatus.Rewarded;

        /// <summary><c>true</c>, если реклама действительно была на экране (<see cref="VhrAdStatus.Rewarded"/> или <see cref="VhrAdStatus.Closed"/>).</summary>
        public bool WasShown => Status == VhrAdStatus.Rewarded || Status == VhrAdStatus.Closed;

        /// <inheritdoc />
        public override string ToString() =>
            $"VhrAdResult({Kind}: {Status}{(string.IsNullOrEmpty(Reason) ? "" : ", " + Reason)})";
    }

    /// <summary>Турнир платформы.</summary>
    [Serializable]
    public sealed class VhrTournament
    {
        public string id;
        public string title;
        public string description;
        public string gameId;
        public string startAt;
        public string endAt;
        public int prizePoolCoins;
        public string bannerColor;
        /// <summary><c>"upcoming"</c> | <c>"active"</c> | <c>"ended"</c> | <c>"finished"</c>.</summary>
        public string status;
        public int participants;
    }

    /// <summary>Строка таблицы результатов турнира.</summary>
    [Serializable]
    public sealed class VhrTournamentStanding
    {
        public int rank;
        public string userId;
        public string displayName;
        public long score;
        public int prizeCoins;
    }

    /// <summary>Generic transport-level result of an HTTP call made through <see cref="IVhrHttp"/>.</summary>
    public readonly struct VhrHttpResponse
    {
        /// <summary>HTTP status code (0 when the request never reached the server).</summary>
        public readonly long StatusCode;

        /// <summary>Raw response body (may be empty).</summary>
        public readonly string Body;

        /// <summary>True for 2xx.</summary>
        public readonly bool IsSuccess;

        /// <summary>Transport / protocol error text, if any.</summary>
        public readonly string Error;

        /// <summary>Creates an HTTP response value.</summary>
        public VhrHttpResponse(long statusCode, string body, bool isSuccess, string error)
        {
            StatusCode = statusCode;
            Body = body;
            IsSuccess = isSuccess;
            Error = error;
        }
    }
}
