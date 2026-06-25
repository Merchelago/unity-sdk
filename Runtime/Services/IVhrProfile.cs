using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Профиль текущего игрока и batch-резолв публичных данных пользователей по
    /// их Id. Backed by <c>{AuthBaseUrl}/api/Auth/*</c> (AuthMS).
    /// <para>
    /// Profile of the current player plus a batch resolver that maps user ids to
    /// publicly-safe display data (nickname + child flag). Used for member
    /// «chips»: studio pages, review authors, leaderboard rows, lobby members.
    /// </para>
    /// </summary>
    public interface IVhrProfile
    {
        /// <summary>
        /// <c>GET /api/Auth/me</c> — текущий аутентифицированный пользователь
        /// (по JWT из <see cref="VhrSdkOptions.TokenProvider"/>). Бросает
        /// <see cref="VhrSdkException"/> с кодом <c>unauthorized</c>, если токен
        /// отсутствует/просрочен.
        /// <para>
        /// Returns the currently authenticated user. Requires a valid player JWT.
        /// </para>
        /// </summary>
        Task<VhrUser> GetMeAsync(CancellationToken ct = default);

        /// <summary>
        /// <c>POST /api/Auth/users/resolve</c> — публичный batch-резолвер: по
        /// массиву Id возвращает для каждого НАЙДЕННОГО пользователя
        /// <c>{ id, nickName, isChild }</c>. Неизвестные id просто отсутствуют в
        /// ответе. Пустой/<c>null</c> вход — пустой массив без обращения к сети.
        /// Запрос автоматически дробится на пачки по 100 id (лимит бэкенда).
        /// <para>
        /// Public batch resolver: maps each known id to a publicly-safe record.
        /// Unknown ids are omitted. Empty/null input returns an empty array
        /// without a network call. Chunks requests in batches of 100 ids.
        /// </para>
        /// </summary>
        Task<VhrUserRef[]> ResolveAsync(string[] userIds, CancellationToken ct = default);

        /// <summary>
        /// Удобная обёртка над <see cref="ResolveAsync"/>: возвращает словарь
        /// <c>userId → nickName</c> только для найденных пользователей.
        /// <para>
        /// Convenience wrapper over <see cref="ResolveAsync"/> returning a
        /// <c>userId → nickName</c> map for the resolved users only.
        /// </para>
        /// </summary>
        Task<Dictionary<string, string>> ResolveNamesAsync(string[] userIds, CancellationToken ct = default);
    }

    /// <summary>HTTP-реализация <see cref="IVhrProfile"/> поверх AuthMS.</summary>
    public sealed class VhrProfileService : IVhrProfile
    {
        // Лимит бэкенда на один запрос users/resolve (см. AuthController.ResolveUsers).
        private const int ResolveBatchSize = 100;

        private readonly VhrApiClient _api;
        private readonly VhrSdkOptions _options;
        private readonly IVhrLog _log;

        /// <summary>Создаёт сервис профиля.</summary>
        public VhrProfileService(VhrApiClient api, VhrSdkOptions options, IVhrLog log)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _log = log;
        }

        private string Url(string path) => $"{_options.AuthBaseUrl}/api/Auth/{path}";

        /// <inheritdoc />
        public async Task<VhrUser> GetMeAsync(CancellationToken ct = default)
        {
            var user = await _api.SendAsync<VhrUser>("GET", Url("me"), ct: ct);
            if (user != null)
                user.roles ??= Array.Empty<string>();
            return user;
        }

        /// <inheritdoc />
        public async Task<VhrUserRef[]> ResolveAsync(string[] userIds, CancellationToken ct = default)
        {
            if (userIds == null || userIds.Length == 0)
                return Array.Empty<VhrUserRef>();

            // Один запрос (типичный случай) — без лишних аллокаций списка.
            if (userIds.Length <= ResolveBatchSize)
                return await ResolveBatchAsync(userIds, ct);

            // Бэкенд режет > 100 id (400 too_many_ids) — дробим на пачки и склеиваем.
            var all = new List<VhrUserRef>(userIds.Length);
            for (int offset = 0; offset < userIds.Length; offset += ResolveBatchSize)
            {
                int count = Math.Min(ResolveBatchSize, userIds.Length - offset);
                var batch = new string[count];
                Array.Copy(userIds, offset, batch, 0, count);

                var part = await ResolveBatchAsync(batch, ct);
                if (part != null && part.Length > 0)
                    all.AddRange(part);
            }
            return all.ToArray();
        }

        /// <inheritdoc />
        public async Task<Dictionary<string, string>> ResolveNamesAsync(string[] userIds, CancellationToken ct = default)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var refs = await ResolveAsync(userIds, ct);
            if (refs == null) return map;

            foreach (var r in refs)
            {
                if (r != null && !string.IsNullOrEmpty(r.id))
                    map[r.id] = r.nickName;
            }
            return map;
        }

        /// <summary>
        /// Один вызов <c>POST users/resolve</c> (≤100 id). Ответ — JSON-массив
        /// верхнего уровня, который <c>JsonUtility</c> не парсит напрямую,
        /// поэтому берём сырое тело и оборачиваем в <c>{"items":[...]}</c>.
        /// </summary>
        private async Task<VhrUserRef[]> ResolveBatchAsync(string[] batch, CancellationToken ct)
        {
            var req = new ResolveRequest { ids = batch };
            var raw = await _api.SendRawAsync("POST", Url("users/resolve"), req, ct: ct);
            if (string.IsNullOrWhiteSpace(raw))
                return Array.Empty<VhrUserRef>();

            ResolveList wrapper;
            try
            {
                wrapper = UnityEngine.JsonUtility.FromJson<ResolveList>("{\"items\":" + raw + "}");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[VHR Profile] не удалось разобрать ответ users/resolve: {ex.Message}");
                return Array.Empty<VhrUserRef>();
            }

            return wrapper?.items ?? Array.Empty<VhrUserRef>();
        }

        // Тело запроса users/resolve: { "ids": [...] }. JsonUtility сериализует
        // публичное поле как есть, бэкенд читает регистронезависимо.
        [Serializable] private sealed class ResolveRequest { public string[] ids; }

        // Обёртка для разбора JSON-массива верхнего уровня через JsonUtility.
        [Serializable] private sealed class ResolveList { public VhrUserRef[] items; }
    }

    /// <summary>
    /// Текущий аутентифицированный пользователь (<c>GET /api/Auth/me</c>). Поля
    /// названы ровно как camelCase-JSON бэкенда (AuthMS), чтобы их парсил
    /// <c>JsonUtility</c>. Чувствительные данные (сессии и т.п.) сюда не входят.
    /// <para>
    /// The currently authenticated user. Field names match the backend camelCase
    /// JSON exactly so Unity's <c>JsonUtility</c> can deserialize them.
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class VhrUser
    {
        /// <summary>VHR user id (GUID-строка).</summary>
        public string id;

        /// <summary>Email пользователя.</summary>
        public string email;

        /// <summary>Подтверждён ли email.</summary>
        public bool emailConfirmed;

        /// <summary>Имя пользователя (Identity UserName).</summary>
        public string userName;

        /// <summary>Телефон, если задан.</summary>
        public string phoneNumber;

        /// <summary>True, если это детский аккаунт.</summary>
        public bool isChild;

        /// <summary>Id родительского аккаунта для детского профиля, иначе пусто.</summary>
        public string parentId;

        /// <summary>Роли пользователя (напр. <c>"User"</c>, <c>"Developer"</c>, <c>"Admin"</c>).</summary>
        public string[] roles;
    }

    /// <summary>
    /// Публично-безопасная карточка пользователя из batch-резолвера
    /// (<c>POST /api/Auth/users/resolve</c>): только id, никнейм и флаг ребёнка.
    /// Поля названы ровно как camelCase-JSON бэкенда для <c>JsonUtility</c>.
    /// <para>
    /// Publicly-safe user reference from the batch resolver: id, nickname and
    /// child flag only. No email/phone/last-login — this is a public endpoint.
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class VhrUserRef
    {
        /// <summary>VHR user id.</summary>
        public string id;

        /// <summary>Отображаемый ник (может быть пустым для старых аккаунтов).</summary>
        public string nickName;

        /// <summary>True, если это детский аккаунт.</summary>
        public bool isChild;
    }
}
