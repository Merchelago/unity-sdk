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
        /// SDK 1.12.0+: нет связи — исключения нет, результат встаёт в очередь (уйдёт сам) и
        /// возвращается присланный счёт; отличить этот случай — <see cref="SubmitAsync"/>.
        /// </summary>
        Task<long> SubmitScoreAsync(string tournamentId, long score, CancellationToken ct = default);

        /// <summary>
        /// <c>POST /api/Tournaments/{id}/score</c> (SDK 1.12.0+) — отправить результат (засчитывается
        /// лучший, больше — лучше; счёт 0…10^12) и узнать итог: <see cref="VhrTournamentScoreResult.bestScore"/>.
        /// Нет связи (сеть, таймаут, 5xx, истёкший токен) — исключения нет:
        /// <see cref="VhrTournamentScoreResult.queued"/> = <c>true</c>, результат в очереди на устройстве
        /// (<see cref="IVhrConnection"/>) и уйдёт сам, когда связь вернётся. Сервер принимает результат,
        /// только пока турнир активен: доставленный после окончания будет отклонён
        /// (<see cref="IVhrConnection.OnDropped"/>).
        /// Ошибки — <see cref="VhrSdkException"/>: неверный id турнира или счёт — <c>bad_request</c>
        /// (до запроса); турнир закрыт / нет подписки / не та игра — ответ сервера.
        /// </summary>
        Task<VhrTournamentScoreResult> SubmitAsync(string tournamentId, long score, CancellationToken ct = default);
    }

    /// <summary>Итог <see cref="IVhrTournaments.SubmitAsync"/> (SDK 1.12.0+).</summary>
    [Serializable]
    public sealed class VhrTournamentScoreResult
    {
        /// <summary><c>true</c> — сервер принял результат; <c>false</c> вместе с <see cref="queued"/> — в очереди.</summary>
        public bool accepted;

        /// <summary>
        /// <c>true</c> — связи с сервером не было: результат сохранён в очереди на устройстве и уйдёт
        /// сам (<see cref="IVhrConnection"/>). <see cref="bestScore"/> пока неизвестен (0).
        /// </summary>
        public bool queued;

        /// <summary>Лучший засчитанный счёт игрока в турнире (после этой отправки).</summary>
        public long bestScore;

        /// <summary>Id турнира (GUID в нижнем регистре).</summary>
        public string tournamentId;
    }

    /// <summary>HTTP implementation of <see cref="IVhrTournaments"/>.</summary>
    public sealed class VhrTournamentsService : IVhrTournaments
    {
        /// <summary>Потолок счёта турнира на сервере (анти-накрутка): 10^12.</summary>
        public const long MaxScore = 1_000_000_000_000L;

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
            var r = await SubmitAsync(tournamentId, score, ct);
            return r.queued ? score : r.bestScore;
        }

        /// <inheritdoc />
        public async Task<VhrTournamentScoreResult> SubmitAsync(string tournamentId, long score, CancellationToken ct = default)
        {
            if (!Guid.TryParse((tournamentId ?? string.Empty).Trim(), out var tid))
                throw new VhrSdkException("bad_request", $"Неверный id турнира '{tournamentId}': нужен GUID из ListAsync.",
                    0, "bad_request", null);
            if (score < 0 || score > MaxScore)
                throw new VhrSdkException("bad_request", $"Счёт турнира {score} вне диапазона 0…{MaxScore}.",
                    0, "bad_request", null);

            var id = tid.ToString("D");
            string Body(long value) => UnityEngine.JsonUtility.ToJson(new ScoreRequest { score = value });
            var sent = await VhrOutbox.GetOrCreate(_api, _options, null)
                .SendOrQueueAsync(VhrOutboxKinds.TournamentScore, id, score, Body, false, ct);
            if (sent.Queued)
                return new VhrTournamentScoreResult { accepted = false, queued = true, tournamentId = id };

            long best = score; // 200 без тела — засчитан присланный (как раньше)
            if (!string.IsNullOrWhiteSpace(sent.Body))
            {
                try
                {
                    var res = UnityEngine.JsonUtility.FromJson<ScoreResponse>(sent.Body);
                    if (res != null) best = res.bestScore;
                }
                catch (Exception ex)
                {
                    throw new VhrSdkException("deserialize_error",
                        $"Could not parse response from {Url(id + "/score")}: {ex.Message}. Body={sent.Body}", 0, ex);
                }
            }
            return new VhrTournamentScoreResult { accepted = true, bestScore = best, tournamentId = id };
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
