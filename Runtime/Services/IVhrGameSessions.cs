using System;
using System.Threading;
using System.Threading.Tasks;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Жизненный цикл игровой сессии / game-play session lifecycle.
    /// Backed by <c>{GamesBaseUrl}/api/GameSessions/*</c>.
    /// </summary>
    /// <remarks>
    /// Сессия открывается на старте игры (<see cref="StartAsync"/>), периодически
    /// продлевается «сердцебиением» (<see cref="HeartbeatAsync"/>, чтобы заброшенные
    /// сессии можно было вычистить) и закрывается на выходе (<see cref="EndAsync"/>).
    /// <para/>
    /// Бэкенд разрешает анонимные сессии (<c>[AllowAnonymous]</c>); если игрок
    /// авторизован, <see cref="VhrApiClient"/> сам прикладывает JWT, и сессия
    /// привязывается к пользователю на сервере.
    /// </remarks>
    public interface IVhrGameSessions
    {
        /// <summary>
        /// <c>POST /api/GameSessions/start</c> — открыть игровую сессию для игры
        /// <paramref name="gameId"/>. Возвращает <c>sessionId</c>, который нужно
        /// передавать в <see cref="HeartbeatAsync"/> и <see cref="EndAsync"/>.
        /// </summary>
        /// <param name="gameId">Id игры (GUID-строка).</param>
        /// <param name="ct">Токен отмены.</param>
        Task<string> StartAsync(string gameId, CancellationToken ct = default);

        /// <summary>
        /// <c>POST /api/GameSessions/{sessionId}/end</c> — завершить сессию.
        /// Сервер читает <paramref name="outcome"/> (исход матча);
        /// <paramref name="score"/> и <paramref name="durationSeconds"/> отправляются
        /// в теле для будущей телеметрии.
        /// </summary>
        /// <param name="sessionId">Id сессии из <see cref="StartAsync"/>.</param>
        /// <param name="outcome">Необязательный исход (напр. <c>"win"</c>, <c>"lose"</c>, <c>"quit"</c>).</param>
        /// <param name="score">Необязательный итоговый счёт.</param>
        /// <param name="durationSeconds">Необязательная длительность матча в секундах.</param>
        /// <param name="ct">Токен отмены.</param>
        Task EndAsync(
            string sessionId, string outcome = null, long score = 0, int durationSeconds = 0,
            CancellationToken ct = default);

        /// <summary>
        /// <c>POST /api/GameSessions/{sessionId}/heartbeat</c> — продлить сессию
        /// (обновляет <c>LastActivityAt</c> на сервере). Вызывать периодически,
        /// пока игрок в матче.
        /// </summary>
        /// <param name="sessionId">Id сессии из <see cref="StartAsync"/>.</param>
        /// <param name="outcome">
        /// Зарезервировано для будущего использования; текущий бэкенд heartbeat-исход
        /// не читает.
        /// </param>
        /// <param name="ct">Токен отмены.</param>
        Task HeartbeatAsync(string sessionId, string outcome = null, CancellationToken ct = default);
    }

    /// <summary>HTTP implementation of <see cref="IVhrGameSessions"/>.</summary>
    public sealed class VhrGameSessionsService : IVhrGameSessions
    {
        private readonly VhrApiClient _api;
        private readonly VhrSdkOptions _options;
        private readonly IVhrLog _log;

        /// <summary>Creates the game-sessions service.</summary>
        public VhrGameSessionsService(VhrApiClient api, VhrSdkOptions options, IVhrLog log)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _log = log;
        }

        private string Url(string path) => $"{_options.GamesBaseUrl}/api/GameSessions/{path}";

        /// <inheritdoc />
        public async Task<string> StartAsync(string gameId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(gameId))
                throw new ArgumentException("gameId is required.", nameof(gameId));

            var req = new StartSessionBody { gameId = gameId };
            var res = await _api.SendAsync<VhrSessionStart>("POST", Url("start"), req, ct: ct);
            return res?.sessionId;
        }

        /// <inheritdoc />
        public async Task EndAsync(
            string sessionId, string outcome = null, long score = 0, int durationSeconds = 0,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("sessionId is required.", nameof(sessionId));

            var req = new EndSessionBody
            {
                outcome = outcome,
                score = score,
                durationSeconds = durationSeconds
            };
            await _api.SendAsync<VhrSessionEndResult>(
                "POST", Url($"{sessionId}/end"), req, ct: ct);
        }

        /// <inheritdoc />
        public async Task HeartbeatAsync(string sessionId, string outcome = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("sessionId is required.", nameof(sessionId));

            var req = new HeartbeatBody { outcome = outcome };
            await _api.SendAsync<VhrVoid>("POST", Url($"{sessionId}/heartbeat"), req, ct: ct);
        }

        [Serializable]
        private sealed class StartSessionBody
        {
            public string gameId;
        }

        [Serializable]
        private sealed class EndSessionBody
        {
            public string outcome;
            public long score;
            public int durationSeconds;
        }

        [Serializable]
        private sealed class HeartbeatBody
        {
            public string outcome;
        }
    }

    /// <summary>Ответ <c>POST /api/GameSessions/start</c> — идентификатор открытой сессии.</summary>
    [Serializable]
    public sealed class VhrSessionStart
    {
        /// <summary>Id созданной игровой сессии.</summary>
        public string sessionId;
    }

    /// <summary>Ответ <c>POST /api/GameSessions/{sessionId}/end</c> — серверное сообщение.</summary>
    [Serializable]
    public sealed class VhrSessionEndResult
    {
        /// <summary>Человекочитаемое сообщение от сервера (напр. «Сессия завершена.»).</summary>
        public string message;
    }
}
