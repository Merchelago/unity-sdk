using System;
using System.Threading;
using System.Threading.Tasks;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Leaderboard submit / read. Backed by <c>{BridgeBaseUrl}/api/leaderboard/*</c>.
    /// </summary>
    /// <remarks>
    /// <b>Next-wave seam:</b> server-side leaderboard persistence is not yet shipped.
    /// The bridge currently answers <c>501 Not Implemented</c>. This client handles
    /// that gracefully: <see cref="SubmitAsync"/> returns <c>false</c> and
    /// <see cref="GetTopAsync"/> returns a page with
    /// <see cref="VhrLeaderboardPage.notImplemented"/> = true (empty entries) instead
    /// of throwing, so game code can ship now and "just work" once the backend lands.
    /// </remarks>
    public interface IVhrLeaderboard
    {
        /// <summary>
        /// <c>POST /api/leaderboard/submit</c> — submit a score for the current
        /// user. Returns <c>false</c> if the backend is not implemented yet (501).
        /// </summary>
        Task<bool> SubmitAsync(string userId, long score, CancellationToken ct = default);

        /// <summary>
        /// <c>GET /api/leaderboard/top?period=...</c> — top entries for a period.
        /// Returns a page flagged <see cref="VhrLeaderboardPage.notImplemented"/>
        /// when the backend is a 501 seam.
        /// </summary>
        Task<VhrLeaderboardPage> GetTopAsync(
            VhrLeaderboardPeriod period = VhrLeaderboardPeriod.AllTime,
            int limit = 50, CancellationToken ct = default);
    }

    /// <summary>HTTP implementation of <see cref="IVhrLeaderboard"/>.</summary>
    public sealed class VhrLeaderboardService : IVhrLeaderboard
    {
        private readonly VhrApiClient _api;
        private readonly VhrSdkOptions _options;
        private readonly IVhrLog _log;

        /// <summary>Creates the leaderboard service.</summary>
        public VhrLeaderboardService(VhrApiClient api, VhrSdkOptions options, IVhrLog log)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _log = log;
        }

        private string Url(string path) => $"{_options.BridgeBaseUrl}/api/leaderboard/{path}";

        /// <inheritdoc />
        public async Task<bool> SubmitAsync(string userId, long score, CancellationToken ct = default)
        {
            var req = new SubmitRequest { userId = userId, score = score };
            var res = await _api.SendAsync<SubmitResponse>(
                "POST", Url("submit"), req, allowNotImplemented: true, ct: ct);
            if (res == null)
            {
                _log?.Warn("Leaderboard submit skipped: backend not implemented yet (501).");
                return false;
            }
            return res.accepted;
        }

        /// <inheritdoc />
        public async Task<VhrLeaderboardPage> GetTopAsync(
            VhrLeaderboardPeriod period = VhrLeaderboardPeriod.AllTime,
            int limit = 50, CancellationToken ct = default)
        {
            var p = period.ToString().ToLowerInvariant();
            var page = await _api.SendAsync<VhrLeaderboardPage>(
                "GET", Url($"top?period={p}&limit={limit}"), allowNotImplemented: true, ct: ct);

            if (page == null)
            {
                _log?.Warn("Leaderboard read skipped: backend not implemented yet (501).");
                return new VhrLeaderboardPage
                {
                    period = p, entries = Array.Empty<VhrLeaderboardEntry>(), notImplemented = true
                };
            }
            page.entries ??= Array.Empty<VhrLeaderboardEntry>();
            return page;
        }

        [Serializable] private sealed class SubmitRequest { public string userId; public long score; }
        [Serializable] private sealed class SubmitResponse { public bool accepted; }
    }
}
