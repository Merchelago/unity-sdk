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

        private static VhrServerBinding Noop(string gameId) => new()
        {
            bindingId = "noop", gameId = gameId, endpoint = string.Empty, status = "noop"
        };

        [Serializable] private sealed class BindRequest { public string gameId; public string region; }
        [Serializable] private sealed class InstanceRequest { public string bindingId; }
        [Serializable] private sealed class BindingList { public VhrServerBinding[] items; }
    }
}
