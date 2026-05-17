using System;
using System.Threading;
using System.Threading.Tasks;
using R3;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Soft-currency economy: balances, coin/achievement grants, spends and
    /// purchases. Every mutation is <b>idempotent</b> via a client-generated
    /// <c>externalId</c> (GUID) so retries/replays never double-apply.
    /// Backed by <c>{BridgeBaseUrl}/api/...</c>.
    /// <para>
    /// Скоуп пользователя: при клиентской авторизации по JWT игрока мост
    /// привязывает каждую операцию к subject из токена (self-операции). Параметр
    /// <c>userId</c> в сигнатурах сохранён для стабильности API и серверных
    /// сценариев: для self-операций он может быть собственным id игрока или
    /// пустым (мост игнорирует/форсит его по JWT). Рекомендуется передавать
    /// известный id игрока.
    /// </para>
    /// <para>
    /// <b>Модель экономики: клиент только списывает (anti-mint).</b> Игра,
    /// исполняемая на стороне клиента (WebGL), <b>не может начислять/минтить</b>
    /// монеты — это запрещено мостом на сервере. Из клиентской сборки доступны:
    /// чтение баланса (<see cref="GetBalanceAsync"/>), списание
    /// (<see cref="SpendAsync"/>) и покупка (<see cref="PurchaseAsync"/>) —
    /// инициируются самим игроком. Начисление (<see cref="GrantCoinsAsync"/> и
    /// монетная награда у <see cref="GrantAchievementAsync"/>) — <b>только при
    /// серверной интеграции</b> (<c>X-Internal-Api-Key</c>). Из клиента такие
    /// вызовы вернут <c>403 forbidden</c> / монетная награда форсится в 0.
    /// Методы оставлены в API, т.к. их используют серверные игры.
    /// </para>
    /// </summary>
    public interface IVhrEconomy
    {
        /// <summary>
        /// Hot stream emitting a <see cref="BalanceChanged"/> after every locally
        /// completed grant/spend/purchase. Subscribe with R3 to drive HUD updates.
        /// </summary>
        Observable<BalanceChanged> BalanceChanged { get; }

        /// <summary>
        /// <c>GET /api/balance/{userId}</c> — current coin balance. Для self-вызова
        /// по JWT игрока <paramref name="userId"/> может быть id самого игрока или
        /// пустым (мост резолвит по токену); рекомендуется передавать известный id.
        /// </summary>
        Task<VhrBalance> GetBalanceAsync(string userId, CancellationToken ct = default);

        /// <summary>
        /// <c>POST /api/grant/coins</c> — начислить монеты пользователю.
        /// <para>
        /// <b>Доступно только при серверной интеграции (<c>X-Internal-Api-Key</c>).
        /// Из клиентской WebGL-сборки вернёт <c>403 forbidden</c> — клиент может
        /// только списывать при покупке игроком.</b> Метод оставлен в API для
        /// серверных игр (выделенный сервер / server-to-server).
        /// </para>
        /// </summary>
        /// <param name="userId">Target user.</param>
        /// <param name="amount">Positive coin amount.</param>
        /// <param name="reason">Audit reason (e.g. <c>"level_complete"</c>).</param>
        /// <param name="externalId">
        /// Optional idempotency key. If null a GUID is generated; pass your own to
        /// make a specific gameplay event replay-safe across sessions.
        /// </param>
        Task<VhrEconomyResult> GrantCoinsAsync(
            string userId, long amount, string reason,
            string externalId = null, CancellationToken ct = default);

        /// <summary>
        /// <c>POST /api/grant/achievement</c> — записать разблокировку ачивки
        /// (и монетную награду, которую мост к ней привязывает).
        /// <para>
        /// Из клиента вызывать <b>можно</b> для фиксации факта анлока — но
        /// <b>монетная награда на клиенте игнорируется (форсится в 0)</b>:
        /// начисление монет — только при серверной интеграции
        /// (<c>X-Internal-Api-Key</c>). Из клиентской WebGL-сборки монетная часть
        /// вернёт эффект <c>0</c> / запрос на чистое начисление — <c>403</c>.
        /// Полная монетная награда применяется только при серверной интеграции.
        /// </para>
        /// </summary>
        Task<VhrEconomyResult> GrantAchievementAsync(
            string userId, string achievementId,
            string externalId = null, CancellationToken ct = default);

        /// <summary>
        /// <c>POST /api/spend</c> — deduct coins. Bridge rejects with
        /// <c>409 conflict</c> on insufficient funds (mapped to
        /// <see cref="VhrSdkException"/> code <c>conflict</c>).
        /// </summary>
        Task<VhrEconomyResult> SpendAsync(
            string userId, long amount, string reason,
            string externalId = null, CancellationToken ct = default);

        /// <summary>
        /// <c>POST /api/purchase</c> — buy a catalog item; the bridge resolves
        /// price and debits atomically.
        /// </summary>
        Task<VhrEconomyResult> PurchaseAsync(
            string userId, string itemId, int quantity = 1,
            string externalId = null, CancellationToken ct = default);
    }

    /// <summary>HTTP implementation of <see cref="IVhrEconomy"/>.</summary>
    public sealed class VhrEconomyService : IVhrEconomy, IDisposable
    {
        private readonly VhrApiClient _api;
        private readonly VhrSdkOptions _options;
        private readonly Subject<BalanceChanged> _balanceChanged = new();

        /// <summary>Creates the economy service.</summary>
        public VhrEconomyService(VhrApiClient api, VhrSdkOptions options)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <inheritdoc />
        public Observable<BalanceChanged> BalanceChanged => _balanceChanged;

        private string Url(string path) => $"{_options.BridgeBaseUrl}/api/{path}";

        private static string Id(string externalId)
            => string.IsNullOrEmpty(externalId) ? Guid.NewGuid().ToString("N") : externalId;

        /// <inheritdoc />
        public Task<VhrBalance> GetBalanceAsync(string userId, CancellationToken ct = default)
            => _api.SendAsync<VhrBalance>("GET", Url($"balance/{Uri.EscapeDataString(userId)}"), ct: ct);

        /// <inheritdoc />
        /// <remarks>Server-only: из клиентской WebGL-сборки мост вернёт 403
        /// (anti-mint). Используется серверными играми с X-Internal-Api-Key.</remarks>
        public async Task<VhrEconomyResult> GrantCoinsAsync(
            string userId, long amount, string reason, string externalId = null, CancellationToken ct = default)
        {
            var req = new GrantCoinsRequest
            {
                userId = userId, amount = amount, reason = reason, externalId = Id(externalId)
            };
            var res = await _api.SendAsync<VhrEconomyResult>("POST", Url("grant/coins"), req, ct: ct);
            Emit(userId, res, amount, "grant/coins");
            return res;
        }

        /// <inheritdoc />
        /// <remarks>Из клиента фиксирует факт анлока, но монетная награда
        /// форсится мостом в 0 (anti-mint). Полная награда — только серверная
        /// интеграция (X-Internal-Api-Key).</remarks>
        public async Task<VhrEconomyResult> GrantAchievementAsync(
            string userId, string achievementId, string externalId = null, CancellationToken ct = default)
        {
            var req = new GrantAchievementRequest
            {
                userId = userId, achievementId = achievementId, externalId = Id(externalId)
            };
            var res = await _api.SendAsync<VhrEconomyResult>("POST", Url("grant/achievement"), req, ct: ct);
            Emit(userId, res, 0, "grant/achievement");
            return res;
        }

        /// <inheritdoc />
        public async Task<VhrEconomyResult> SpendAsync(
            string userId, long amount, string reason, string externalId = null, CancellationToken ct = default)
        {
            var req = new SpendRequest
            {
                userId = userId, amount = amount, reason = reason, externalId = Id(externalId)
            };
            var res = await _api.SendAsync<VhrEconomyResult>("POST", Url("spend"), req, ct: ct);
            Emit(userId, res, -amount, "spend");
            return res;
        }

        /// <inheritdoc />
        public async Task<VhrEconomyResult> PurchaseAsync(
            string userId, string itemId, int quantity = 1, string externalId = null, CancellationToken ct = default)
        {
            var req = new PurchaseRequest
            {
                userId = userId, itemId = itemId, quantity = quantity, externalId = Id(externalId)
            };
            var res = await _api.SendAsync<VhrEconomyResult>("POST", Url("purchase"), req, ct: ct);
            Emit(userId, res, 0, "purchase");
            return res;
        }

        private void Emit(string userId, VhrEconomyResult res, long delta, string reason)
        {
            if (res != null && res.success)
                _balanceChanged.OnNext(new BalanceChanged(userId, res.balance, delta, reason));
        }

        /// <inheritdoc />
        public void Dispose() => _balanceChanged.Dispose();

        // --- Request DTOs (JsonUtility-serializable; field names match the bridge contract) ---

        [Serializable] private sealed class GrantCoinsRequest
        { public string userId; public long amount; public string reason; public string externalId; }

        [Serializable] private sealed class GrantAchievementRequest
        { public string userId; public string achievementId; public string externalId; }

        [Serializable] private sealed class SpendRequest
        { public string userId; public long amount; public string reason; public string externalId; }

        [Serializable] private sealed class PurchaseRequest
        { public string userId; public string itemId; public int quantity; public string externalId; }
    }
}
