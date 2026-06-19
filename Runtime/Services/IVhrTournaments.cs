using System;
using System.Threading;
using System.Threading.Tasks;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Турниры платформы. Игрок может смотреть список/таблицу, присоединяться и
    /// отправлять результат. Бэкенд — <c>{GamesBaseUrl}/api/Tournaments/*</c>
    /// (UnityGamesMS). Призовой пул раздаётся платформой при финализации.
    /// </summary>
    public interface IVhrTournaments
    {
        /// <summary>
        /// <c>GET /api/Tournaments?status=...</c> — список турниров.
        /// status: <c>"active"</c> | <c>"upcoming"</c> | <c>"ended"</c> | <c>"all"</c>.
        /// </summary>
        Task<VhrTournament[]> ListAsync(string status = "active", CancellationToken ct = default);

        /// <summary><c>GET /api/Tournaments/{id}/standings</c> — таблица результатов.</summary>
        Task<VhrTournamentStanding[]> GetStandingsAsync(string tournamentId, CancellationToken ct = default);

        /// <summary><c>POST /api/Tournaments/{id}/join</c> — присоединиться к турниру.</summary>
        Task<bool> JoinAsync(string tournamentId, CancellationToken ct = default);

        /// <summary>
        /// <c>POST /api/Tournaments/{id}/score</c> — отправить результат (сохраняется
        /// лучший). Возвращает лучший засчитанный счёт игрока в турнире.
        /// </summary>
        Task<long> SubmitScoreAsync(string tournamentId, long score, CancellationToken ct = default);
    }

    /// <summary>HTTP implementation of <see cref="IVhrTournaments"/>.</summary>
    public sealed class VhrTournamentsService : IVhrTournaments
    {
        private readonly VhrApiClient _api;
        private readonly VhrSdkOptions _options;

        /// <summary>Creates the tournaments service.</summary>
        public VhrTournamentsService(VhrApiClient api, VhrSdkOptions options)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        private string Url(string path) => $"{_options.GamesBaseUrl}/api/Tournaments/{path}";

        /// <inheritdoc />
        public async Task<VhrTournament[]> ListAsync(string status = "active", CancellationToken ct = default)
        {
            var s = string.IsNullOrWhiteSpace(status) ? "active" : status;
            var raw = await _api.SendRawAsync(
                "GET", $"{_options.GamesBaseUrl}/api/Tournaments?status={Uri.EscapeDataString(s)}",
                allowNotImplemented: true, ct: ct);
            return Wrap<TournamentArray, VhrTournament>(raw)?.items ?? Array.Empty<VhrTournament>();
        }

        /// <inheritdoc />
        public async Task<VhrTournamentStanding[]> GetStandingsAsync(string tournamentId, CancellationToken ct = default)
        {
            var raw = await _api.SendRawAsync(
                "GET", Url($"{Uri.EscapeDataString(tournamentId)}/standings"),
                allowNotImplemented: true, ct: ct);
            return Wrap<StandingArray, VhrTournamentStanding>(raw)?.items ?? Array.Empty<VhrTournamentStanding>();
        }

        /// <inheritdoc />
        public async Task<bool> JoinAsync(string tournamentId, CancellationToken ct = default)
        {
            var res = await _api.SendAsync<JoinResponse>(
                "POST", Url($"{Uri.EscapeDataString(tournamentId)}/join"), allowNotImplemented: true, ct: ct);
            return res?.joined ?? true; // 200 без тела считаем успехом
        }

        /// <inheritdoc />
        public async Task<long> SubmitScoreAsync(string tournamentId, long score, CancellationToken ct = default)
        {
            var req = new ScoreRequest { score = score };
            var res = await _api.SendAsync<ScoreResponse>(
                "POST", Url($"{Uri.EscapeDataString(tournamentId)}/score"), req, ct: ct);
            return res?.bestScore ?? score;
        }

        // JsonUtility не парсит JSON-массив верхнего уровня — оборачиваем тело
        // в {"items":[...]} и парсим конкретной обёрткой.
        private static TWrap Wrap<TWrap, TItem>(string rawArrayJson) where TWrap : class
        {
            if (string.IsNullOrWhiteSpace(rawArrayJson)) return null;
            try { return UnityEngine.JsonUtility.FromJson<TWrap>("{\"items\":" + rawArrayJson + "}"); }
            catch { return null; }
        }

        [Serializable] private sealed class TournamentArray { public VhrTournament[] items; }
        [Serializable] private sealed class StandingArray { public VhrTournamentStanding[] items; }
        [Serializable] private sealed class JoinResponse { public bool joined; }
        [Serializable] private sealed class ScoreRequest { public long score; }
        [Serializable] private sealed class ScoreResponse { public long bestScore; }
    }
}
