using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Чтение/запись лидерборда. Бэкенд — реальный
    /// <c>{GamesBaseUrl}/api/Leaderboard/*</c> (UnityGamesMS,
    /// <c>LeaderboardController</c>), источник очков — таблица
    /// <c>GameLeaderboardScores</c> (bucket <c>all</c>), куда SDK пишет результаты
    /// через GameBridge. Раньше клиент смотрел в несуществующий seam
    /// <c>{BridgeBaseUrl}/api/leaderboard/*</c> (501) — это убрано.
    /// </summary>
    /// <remarks>
    /// Эндпоинты <c>global</c> и <c>games/{gameId}</c> отдают JSON-массив верхнего
    /// уровня, который <see cref="JsonUtility"/> не парсит напрямую — поэтому тело
    /// оборачивается в <c>{"items":[...]}</c> (как в
    /// <see cref="VhrLobbyService.GetFriendsAsync"/> / <see cref="VhrTournamentsService"/>).
    /// Ник игрока (<see cref="VhrLeaderboardRow.name"/>) бэкенд по этим эндпоинтам
    /// не резолвит для global/games (приходит <c>null</c>) — фронт резолвит его по
    /// <see cref="VhrLeaderboardRow.userId"/>; <c>POST score</c> возвращает ник.
    /// </remarks>
    public interface IVhrLeaderboard
    {
        /// <summary>
        /// <c>GET /api/Leaderboard/global?limit=...</c> — лучшие результаты по всем
        /// играм (bucket <c>all</c>), отсортированы по убыванию очков.
        /// </summary>
        Task<VhrLeaderboardRow[]> GetGlobalAsync(int limit = 100, CancellationToken ct = default);

        /// <summary>
        /// <c>GET /api/Leaderboard/games/{gameId}?limit=...</c> — топ для конкретной
        /// игры. Если запрос авторизован (JWT игрока), у своих строк проставляется
        /// <see cref="VhrLeaderboardRow.isCurrentUser"/>.
        /// </summary>
        Task<VhrLeaderboardRow[]> GetForGameAsync(string gameId, int limit = 100, CancellationToken ct = default);

        /// <summary>
        /// <c>POST /api/Leaderboard/score</c> (JWT) — upsert результата текущего
        /// пользователя для игры. Возвращает сохранённую строку (включая
        /// разрешённый ник в <see cref="VhrLeaderboardRow.name"/>).
        /// </summary>
        Task<VhrLeaderboardRow> SubmitScoreAsync(string gameId, long score, CancellationToken ct = default);
    }

    /// <summary>
    /// Одна строка лидерборда — форма реального ответа
    /// <c>{GamesBaseUrl}/api/Leaderboard/*</c> (UnityGamesMS).
    /// </summary>
    /// <remarks>
    /// Отдельный тип (не <c>VhrLeaderboardEntry</c> из <c>Runtime/Models</c>):
    /// у старой модели поля <c>rank</c>/<c>displayName</c> не совпадают с реальным
    /// контрактом, а сама она <c>sealed</c> в неизменяемом файле моделей — поэтому
    /// здесь объявлен совместимый по полям тип.
    /// </remarks>
    [Serializable]
    public sealed class VhrLeaderboardRow
    {
        /// <summary>Id строки результата (PK в <c>GameLeaderboardScores</c>).</summary>
        public int id;

        /// <summary>VHR user id владельца результата.</summary>
        public string userId;

        /// <summary>
        /// Ник игрока. Для <c>global</c>/<c>games/{id}</c> бэкенд отдаёт <c>null</c>
        /// (фронт резолвит по <see cref="userId"/>); <c>POST score</c> возвращает ник
        /// (там бэкенд кладёт его в поле <c>playerName</c> — мапится сюда).
        /// </summary>
        public string name;

        /// <summary>Id игры (может быть <c>null</c>/пустым, если бэкенд не разобрал GUID).</summary>
        public string gameId;

        /// <summary>Лучший результат игрока (по убыванию в выдаче).</summary>
        public long score;

        /// <summary>UTC-время записи результата (ISO-8601), если отдано.</summary>
        public string createdAt;

        /// <summary>
        /// <c>true</c>, если строка принадлежит текущему авторизованному пользователю.
        /// Заполняется только эндпоинтом <c>games/{gameId}</c>; для <c>global</c> — всегда <c>false</c>.
        /// </summary>
        public bool isCurrentUser;
    }

    /// <summary>HTTP-реализация <see cref="IVhrLeaderboard"/> поверх <see cref="VhrApiClient"/>.</summary>
    public sealed class VhrLeaderboardService : IVhrLeaderboard
    {
        private readonly VhrApiClient _api;
        private readonly VhrSdkOptions _options;
        private readonly IVhrLog _log;

        /// <summary>Создаёт сервис лидерборда.</summary>
        public VhrLeaderboardService(VhrApiClient api, VhrSdkOptions options, IVhrLog log)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _log = log;
        }

        private string Url(string path) => $"{_options.GamesBaseUrl}/api/Leaderboard/{path}";

        /// <inheritdoc />
        public async Task<VhrLeaderboardRow[]> GetGlobalAsync(int limit = 100, CancellationToken ct = default)
        {
            var raw = await _api.SendRawAsync(
                "GET", Url($"global?limit={limit}"), allowNotImplemented: true, ct: ct);
            return Wrap(raw)?.items ?? Array.Empty<VhrLeaderboardRow>();
        }

        /// <inheritdoc />
        public async Task<VhrLeaderboardRow[]> GetForGameAsync(
            string gameId, int limit = 100, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(gameId))
                throw new VhrSdkException("config_invalid", "gameId обязателен для GetForGameAsync.");

            var raw = await _api.SendRawAsync(
                "GET", Url($"games/{Uri.EscapeDataString(gameId)}?limit={limit}"),
                allowNotImplemented: true, ct: ct);
            return Wrap(raw)?.items ?? Array.Empty<VhrLeaderboardRow>();
        }

        /// <inheritdoc />
        public async Task<VhrLeaderboardRow> SubmitScoreAsync(
            string gameId, long score, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(gameId))
                throw new VhrSdkException("config_invalid", "gameId обязателен для SubmitScoreAsync.");

            var req = new SubmitScoreRequest { gameId = gameId, score = score };
            var res = await _api.SendAsync<SubmitScoreResponse>("POST", Url("score"), req, ct: ct);
            if (res == null)
            {
                _log?.Warn("Leaderboard score submit: пустой ответ сервера.");
                return new VhrLeaderboardRow { gameId = gameId, score = score };
            }

            // Бэкенд отдаёт ник в поле playerName — мапим в name.
            return new VhrLeaderboardRow
            {
                id = res.id,
                userId = res.userId,
                name = res.playerName,
                gameId = res.gameId,
                score = res.score,
                createdAt = res.createdAt
            };
        }

        // JsonUtility не парсит JSON-массив верхнего уровня — оборачиваем тело
        // в {"items":[...]} и парсим обёрткой (см. VhrLobbyService.GetFriendsAsync).
        private static LeaderboardArray Wrap(string rawArrayJson)
        {
            if (string.IsNullOrWhiteSpace(rawArrayJson)) return null;
            try { return JsonUtility.FromJson<LeaderboardArray>("{\"items\":" + rawArrayJson + "}"); }
            catch { return null; }
        }

        [Serializable] private sealed class LeaderboardArray { public VhrLeaderboardRow[] items; }

        // Тело POST /api/Leaderboard/score: { gameId, score }.
        [Serializable] private sealed class SubmitScoreRequest { public string gameId; public long score; }

        // Ответ POST /api/Leaderboard/score: { id, userId, playerName, gameId, score, createdAt }.
        [Serializable]
        private sealed class SubmitScoreResponse
        {
            public int id;
            public string userId;
            public string playerName;
            public string gameId;
            public long score;
            public string createdAt;
        }
    }
}
