using System;
using System.Threading;
using System.Threading.Tasks;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Социальный граф игрока: друзья, заявки в друзья, статус пары и приглашение
    /// друга в игру. Backed by <c>{GamesBaseUrl}/api/Friends/*</c> (UnityGamesMS,
    /// <c>FriendsController</c>).
    /// <para>
    /// Social graph for the current player: friends list, friend requests, pair
    /// status and game invites. All endpoints require the player JWT
    /// (<see cref="VhrSdkOptions.TokenProvider"/>); the "other" side of every pair
    /// is the current user implied by that token.
    /// </para>
    /// <remarks>
    /// Этот сервис НЕ резолвит ники/онлайн — бэкенд отдаёт только строковые
    /// <c>userId</c>. Имена игра подмешивает сама через <c>IVhrProfile</c>.
    /// <br/>
    /// This service does not resolve display names — the backend returns bare
    /// <c>userId</c> strings only. Merge names client-side via <c>IVhrProfile</c>.
    /// </remarks>
    /// </summary>
    public interface IVhrFriends
    {
        /// <summary>
        /// <c>GET /api/Friends</c> — список друзей текущего игрока (accepted).
        /// <br/>The current player's accepted friends. Each item carries the
        /// other user's id and the time the friendship was confirmed.
        /// </summary>
        Task<VhrFriendRef[]> GetFriendsAsync(CancellationToken ct = default);

        /// <summary>
        /// <c>GET /api/Friends/requests?direction=incoming|outgoing</c> —
        /// pending-заявки текущего игрока.
        /// <br/>Pending friend requests. <paramref name="direction"/> is
        /// <c>"incoming"</c> (default) or <c>"outgoing"</c>; any other value is
        /// treated as incoming by the backend.
        /// </summary>
        Task<VhrFriendRequest[]> GetRequestsAsync(string direction = "incoming", CancellationToken ct = default);

        /// <summary>
        /// <c>POST /api/Friends/requests</c> body <c>{ userId }</c> — отправить
        /// заявку в друзья. Возвращает статус: <c>"pending"</c> либо
        /// <c>"accepted"</c> (если адресат уже звал нас — автопринятие).
        /// <br/>Send a friend request. Returns the resulting
        /// <see cref="VhrFriendStatus.status"/> (<c>"pending"</c>, or
        /// <c>"accepted"</c> when the target had already invited us) and the
        /// request id in <see cref="VhrFriendStatus.requestId"/>.
        /// </summary>
        Task<VhrFriendStatus> SendRequestAsync(string userId, CancellationToken ct = default);

        /// <summary>
        /// <c>POST /api/Friends/requests/{id}/accept</c> — принять входящую
        /// заявку (только адресат).
        /// <br/>Accept an incoming request (addressee only).
        /// </summary>
        Task AcceptRequestAsync(string requestId, CancellationToken ct = default);

        /// <summary>
        /// <c>POST /api/Friends/requests/{id}/decline</c> — отклонить входящую
        /// заявку (только адресат).
        /// <br/>Decline an incoming request (addressee only).
        /// </summary>
        Task DeclineRequestAsync(string requestId, CancellationToken ct = default);

        /// <summary>
        /// <c>DELETE /api/Friends/requests/{id}</c> — отозвать СВОЮ исходящую
        /// pending-заявку (204 No Content).
        /// <br/>Cancel your own outgoing pending request (204 No Content).
        /// </summary>
        Task CancelRequestAsync(string requestId, CancellationToken ct = default);

        /// <summary>
        /// <c>DELETE /api/Friends/{userId}</c> — удалить пользователя из друзей
        /// (204 No Content).
        /// <br/>Remove a user from friends (204 No Content).
        /// </summary>
        Task RemoveFriendAsync(string userId, CancellationToken ct = default);

        /// <summary>
        /// <c>GET /api/Friends/status/{userId}</c> — статус пары «я ↔ userId»:
        /// <c>"self"</c> | <c>"friends"</c> | <c>"outgoing"</c> | <c>"incoming"</c>
        /// | <c>"none"</c>. Для <c>outgoing</c>/<c>incoming</c> также приходит
        /// <see cref="VhrFriendStatus.requestId"/>.
        /// <br/>Relationship between the current user and <paramref name="userId"/>.
        /// </summary>
        Task<VhrFriendStatus> GetStatusAsync(string userId, CancellationToken ct = default);

        /// <summary>
        /// <c>POST /api/Friends/invite</c> body <c>{ userId, gameId }</c> —
        /// пригласить друга сыграть в игру (только между друзьями; игра должна
        /// быть active).
        /// <br/>Invite a friend to play <paramref name="gameId"/>. Only allowed
        /// between friends; the game must exist and be active.
        /// </summary>
        Task InviteToGameAsync(string userId, string gameId, CancellationToken ct = default);
    }

    /// <summary>HTTP-реализация <see cref="IVhrFriends"/> поверх <see cref="VhrApiClient"/>.
    /// <br/>HTTP implementation of <see cref="IVhrFriends"/>.</summary>
    public sealed class VhrFriendsService : IVhrFriends
    {
        private readonly VhrApiClient _api;
        private readonly VhrSdkOptions _options;
        private readonly IVhrLog _log;

        /// <summary>Создаёт сервис друзей. <br/>Creates the friends service.</summary>
        public VhrFriendsService(VhrApiClient api, VhrSdkOptions options, IVhrLog log)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _log = log;
        }

        /// <summary>База маршрута без хвостового сегмента: <c>{GamesBaseUrl}/api/Friends</c>.</summary>
        private string BaseUrl() => $"{_options.GamesBaseUrl}/api/Friends";

        /// <summary>Маршрут с сегментом: <c>{GamesBaseUrl}/api/Friends/{path}</c>.</summary>
        private string Url(string path) => $"{_options.GamesBaseUrl}/api/Friends/{path}";

        /// <inheritdoc />
        public async Task<VhrFriendRef[]> GetFriendsAsync(CancellationToken ct = default)
        {
            // Ответ — ОБЪЕКТ {"items":[{"userId":"..","since":".."}]}, парсится
            // напрямую обёрткой (не top-level массив).
            var page = await _api.SendAsync<FriendListDto>("GET", BaseUrl(), ct: ct);
            var items = page?.items;
            return items ?? Array.Empty<VhrFriendRef>();
        }

        /// <inheritdoc />
        public async Task<VhrFriendRequest[]> GetRequestsAsync(string direction = "incoming", CancellationToken ct = default)
        {
            var dir = string.IsNullOrWhiteSpace(direction) ? "incoming" : direction.Trim().ToLowerInvariant();
            // {"items":[{"id","fromUserId","toUserId","createdAt"}]} — обёртка.
            var page = await _api.SendAsync<RequestListDto>("GET", Url($"requests?direction={dir}"), ct: ct);
            var items = page?.items;
            return items ?? Array.Empty<VhrFriendRequest>();
        }

        /// <inheritdoc />
        public async Task<VhrFriendStatus> SendRequestAsync(string userId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new VhrSdkException("config_invalid", "userId обязателен для SendRequestAsync.");

            var req = new UserIdBody { userId = userId.Trim() };
            // Ответ {id, status}; кладём в VhrFriendStatus (id → requestId).
            var res = await _api.SendAsync<CreateRequestDto>("POST", Url("requests"), req, ct: ct);
            return new VhrFriendStatus
            {
                status = res?.status,
                requestId = res?.id
            };
        }

        /// <inheritdoc />
        public async Task AcceptRequestAsync(string requestId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(requestId))
                throw new VhrSdkException("config_invalid", "requestId обязателен для AcceptRequestAsync.");
            // Ответ {message}; тело не нужно — VhrVoid.
            await _api.SendAsync<VhrVoid>("POST", Url($"requests/{requestId.Trim()}/accept"), ct: ct);
        }

        /// <inheritdoc />
        public async Task DeclineRequestAsync(string requestId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(requestId))
                throw new VhrSdkException("config_invalid", "requestId обязателен для DeclineRequestAsync.");
            await _api.SendAsync<VhrVoid>("POST", Url($"requests/{requestId.Trim()}/decline"), ct: ct);
        }

        /// <inheritdoc />
        public async Task CancelRequestAsync(string requestId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(requestId))
                throw new VhrSdkException("config_invalid", "requestId обязателен для CancelRequestAsync.");
            // 204 No Content.
            await _api.SendAsync<VhrVoid>("DELETE", Url($"requests/{requestId.Trim()}"), ct: ct);
        }

        /// <inheritdoc />
        public async Task RemoveFriendAsync(string userId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new VhrSdkException("config_invalid", "userId обязателен для RemoveFriendAsync.");
            // 204 No Content.
            await _api.SendAsync<VhrVoid>("DELETE", Url(userId.Trim()), ct: ct);
        }

        /// <inheritdoc />
        public async Task<VhrFriendStatus> GetStatusAsync(string userId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new VhrSdkException("config_invalid", "userId обязателен для GetStatusAsync.");
            // {status, requestId?} — парсится напрямую в VhrFriendStatus.
            var res = await _api.SendAsync<VhrFriendStatus>("GET", Url($"status/{userId.Trim()}"), ct: ct);
            return res ?? new VhrFriendStatus { status = "none" };
        }

        /// <inheritdoc />
        public async Task InviteToGameAsync(string userId, string gameId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new VhrSdkException("config_invalid", "userId обязателен для InviteToGameAsync.");
            if (string.IsNullOrWhiteSpace(gameId))
                throw new VhrSdkException("config_invalid", "gameId обязателен для InviteToGameAsync.");

            var body = new InviteBody { userId = userId.Trim(), gameId = gameId.Trim() };
            // Ответ {message}; тело не нужно — VhrVoid.
            await _api.SendAsync<VhrVoid>("POST", Url("invite"), body, ct: ct);
        }

        // ---- request DTO (исходящие тела) ----

        [Serializable] private sealed class UserIdBody { public string userId; }
        [Serializable] private sealed class InviteBody { public string userId; public string gameId; }

        // ---- response DTO-обёртки для JsonUtility ----
        // ({"items":[...]} парсится напрямую; для status/create используем
        // публичные/служебные типы ниже.)

        [Serializable] private sealed class FriendListDto { public VhrFriendRef[] items; }
        [Serializable] private sealed class RequestListDto { public VhrFriendRequest[] items; }
        [Serializable] private sealed class CreateRequestDto { public string id; public string status; }
    }

    /// <summary>
    /// Друг текущего игрока (элемент <c>GET /api/Friends</c>). Без ника/онлайна —
    /// только id и время дружбы; ник подмешивает игра через <c>IVhrProfile</c>.
    /// <br/>A friend of the current player. Bare id + since-time only; resolve the
    /// display name client-side. Distinct from <see cref="VhrFriend"/> (lobby
    /// invite model) on purpose.
    /// </summary>
    [Serializable]
    public sealed class VhrFriendRef
    {
        /// <summary>VHR user id друга. <br/>The friend's VHR user id.</summary>
        public string userId;

        /// <summary>Когда дружба подтверждена (ISO-8601 UTC).
        /// <br/>When the friendship was confirmed (ISO-8601 UTC).</summary>
        public string since;
    }

    /// <summary>
    /// Заявка в друзья (элемент <c>GET /api/Friends/requests</c>).
    /// <br/>A pending friend request.
    /// </summary>
    [Serializable]
    public sealed class VhrFriendRequest
    {
        /// <summary>Id заявки (Guid-строка). <br/>Request id (Guid string).</summary>
        public string id;

        /// <summary>Кто отправил. <br/>Requester user id.</summary>
        public string fromUserId;

        /// <summary>Кому адресована. <br/>Addressee user id.</summary>
        public string toUserId;

        /// <summary>Когда создана (ISO-8601 UTC). <br/>Created at (ISO-8601 UTC).</summary>
        public string createdAt;
    }

    /// <summary>
    /// Статус пары «текущий игрок ↔ userId» (<c>GET /api/Friends/status/{userId}</c>),
    /// либо итог отправки заявки (<c>POST /api/Friends/requests</c>).
    /// <br/>Relationship status, or the outcome of sending a request.
    /// </summary>
    [Serializable]
    public sealed class VhrFriendStatus
    {
        /// <summary>
        /// <c>"self"</c> | <c>"friends"</c> | <c>"outgoing"</c> | <c>"incoming"</c>
        /// | <c>"none"</c> для статуса; <c>"pending"</c> | <c>"accepted"</c> как
        /// итог отправки заявки.
        /// <br/>Relationship status, or send-request outcome.
        /// </summary>
        public string status;

        /// <summary>
        /// Id связанной pending-заявки для <c>outgoing</c>/<c>incoming</c> (и id
        /// созданной заявки при отправке); иначе пусто.
        /// <br/>Related pending request id for <c>outgoing</c>/<c>incoming</c>
        /// (and the new request id on send); empty otherwise.
        /// </summary>
        public string requestId;
    }
}
