using System;
using System.Threading;
using System.Threading.Tasks;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Server-binding API for games that need a backing server instance
    /// (multiplayer / authoritative sim). Backed by
    /// <c>{ServersBaseUrl}/api/...</c>.
    /// </summary>
    /// <remarks>
    /// <b>Default provider is a no-op.</b> Single-player / client-only games do not
    /// call this at all. When the servers backend is not provisioned it returns
    /// bindings with <see cref="VhrServerBinding.status"/> = <c>"noop"</c> and an
    /// empty endpoint; treat that as "run locally".
    /// </remarks>
    public interface IVhrServers
    {
        /// <summary>
        /// <c>POST /api/bindings</c> — bind this game to a server pool / region.
        /// </summary>
        Task<VhrServerBinding> BindAsync(
            string gameId, string region = null, CancellationToken ct = default);

        /// <summary><c>GET /api/bindings?gameId=...</c> — existing bindings for a game.</summary>
        Task<VhrServerBinding[]> ListBindingsAsync(string gameId, CancellationToken ct = default);

        /// <summary>
        /// <c>POST /api/instances/request</c> — request a concrete server instance
        /// for a binding (returns the endpoint to connect to once ready).
        /// </summary>
        Task<VhrServerBinding> RequestInstanceAsync(
            string bindingId, CancellationToken ct = default);

        /// <summary>
        /// <c>POST /api/servers/games/{gameId}/match</c> — подобрать игровой сервер
        /// для подключения (matchmaking). Возвращает свободный сервер или поднимает
        /// новый по требованию (в пределах квоты). Адрес — по домену/uuid
        /// (см. <see cref="VhrMatch.connectUri"/>), не сырому IP. Если мест нет и
        /// квота исчерпана — <see cref="VhrMatch.ok"/>=false,
        /// <see cref="VhrMatch.code"/>=<c>"no_capacity"</c>.
        /// </summary>
        /// <param name="gameId">Игра; по умолчанию — <see cref="VhrSdkOptions.GameId"/>.</param>
        Task<VhrMatch> MatchAsync(string gameId = null, CancellationToken ct = default);

        /// <summary>
        /// <c>POST /api/servers/instances/{instanceId}/players</c> — выделенный
        /// игровой сервер репортит текущее число игроков. Server-to-server: требует
        /// <see cref="VhrSdkOptions.InternalApiKey"/> (в клиентских сборках не
        /// задаётся). При заполнении без свободной квоты платформа уведомляет
        /// разработчика. Возвращает <c>true</c> при успешном приёме.
        /// </summary>
        Task<bool> ReportPlayersAsync(string instanceId, int count, CancellationToken ct = default);
    }

    /// <summary>HTTP implementation of <see cref="IVhrServers"/> (noop-aware).</summary>
    public sealed class VhrServersService : IVhrServers
    {
        private readonly VhrApiClient _api;
        private readonly VhrSdkOptions _options;

        /// <summary>Creates the servers service.</summary>
        public VhrServersService(VhrApiClient api, VhrSdkOptions options)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        private string Url(string path) => $"{_options.ServersBaseUrl}/api/{path}";

        /// <inheritdoc />
        public async Task<VhrServerBinding> BindAsync(
            string gameId, string region = null, CancellationToken ct = default)
        {
            var req = new BindRequest { gameId = gameId, region = region };
            var b = await _api.SendAsync<VhrServerBinding>(
                "POST", Url("bindings"), req, allowNotImplemented: true, ct: ct);
            return b ?? Noop(gameId);
        }

        /// <inheritdoc />
        public async Task<VhrServerBinding[]> ListBindingsAsync(string gameId, CancellationToken ct = default)
        {
            var page = await _api.SendAsync<BindingList>(
                "GET", Url($"bindings?gameId={Uri.EscapeDataString(gameId)}"),
                allowNotImplemented: true, ct: ct);
            return page?.items ?? Array.Empty<VhrServerBinding>();
        }

        /// <inheritdoc />
        public async Task<VhrServerBinding> RequestInstanceAsync(string bindingId, CancellationToken ct = default)
        {
            var req = new InstanceRequest { bindingId = bindingId };
            var b = await _api.SendAsync<VhrServerBinding>(
                "POST", Url("instances/request"), req, allowNotImplemented: true, ct: ct);
            return b ?? Noop(_options.GameId);
        }

        /// <inheritdoc />
        public async Task<VhrMatch> MatchAsync(string gameId = null, CancellationToken ct = default)
        {
            var gid = string.IsNullOrEmpty(gameId) ? _options.GameId : gameId;
            try
            {
                var m = await _api.SendAsync<VhrMatch>(
                    "POST", Url($"servers/games/{Uri.EscapeDataString(gid)}/match"),
                    allowNotImplemented: true, ct: ct);
                // null = 501 (эндпоинт ещё не задеплоен) либо пустое тело.
                if (m == null) return new VhrMatch { ok = false, code = "no_server", message = "Серверы недоступны." };
                // ok по умолчанию true; если сервер прислал ok=false телом — не трогаем.
                return m;
            }
            catch (VhrSdkException ex)
            {
                // 409 — нет свободных мест и квота исчерпана (масштабирование).
                return new VhrMatch
                {
                    ok = false,
                    code = ex.HttpStatus == 409 ? "no_capacity" : (ex.Code ?? "error"),
                    message = ex.Message,
                };
            }
        }

        /// <inheritdoc />
        public async Task<bool> ReportPlayersAsync(string instanceId, int count, CancellationToken ct = default)
        {
            var req = new PlayersReport { count = count < 0 ? 0 : count };
            var res = await _api.SendAsync<PlayersAccepted>(
                "POST", Url($"servers/instances/{Uri.EscapeDataString(instanceId)}/players"),
                req, allowNotImplemented: true, ct: ct);
            return res?.accepted ?? false;
        }

        private static VhrServerBinding Noop(string gameId) => new()
        {
            bindingId = "noop", gameId = gameId, endpoint = string.Empty, status = "noop"
        };

        [Serializable] private sealed class BindRequest { public string gameId; public string region; }
        [Serializable] private sealed class InstanceRequest { public string bindingId; }
        [Serializable] private sealed class BindingList { public VhrServerBinding[] items; }
        [Serializable] private sealed class PlayersReport { public int count; }
        [Serializable] private sealed class PlayersAccepted { public bool accepted; }
    }
}
