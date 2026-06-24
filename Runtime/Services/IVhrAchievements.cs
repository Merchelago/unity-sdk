using System;
using System.Threading;
using System.Threading.Tasks;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Достижения (achievements) — чтение каталога платформенных и игровых ачивок,
    /// а также разблокировок игрока. Backed by
    /// <c>{GamesBaseUrl}/api/Achievements/*</c> (UnityGamesMS, AchievementsController).
    /// <para>
    /// Reads only. Разблокировка ачивки (<c>POST /unlock</c>) выполняется хостом
    /// (iframe страницы игры) с JWT игрока и в этот клиент намеренно не вынесена.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Большинство эндпоинтов отдают JSON-массив ВЕРХНЕГО уровня, который
    /// <c>JsonUtility</c> не парсит напрямую — поэтому сырое тело оборачивается в
    /// <c>{"items":[...]}</c> и парсится через приватную обёртку (как в
    /// <see cref="IVhrLobby"/>.GetFriendsAsync). Эндпоинт <c>my-games</c> уже
    /// возвращает <c>{"items":[...]}</c>, поэтому парсится напрямую.
    /// </remarks>
    public interface IVhrAchievements
    {
        /// <summary>
        /// <c>GET /api/Achievements/platform</c> — все платформенные достижения
        /// (публично). Возвращает пустой массив, если их нет.
        /// </summary>
        Task<VhrAchievement[]> GetPlatformAsync(CancellationToken ct = default);

        /// <summary>
        /// <c>GET /api/Achievements/games/{gameId}</c> — все достижения конкретной
        /// игры (публично). Возвращает пустой массив, если их нет.
        /// </summary>
        Task<VhrAchievement[]> GetForGameAsync(string gameId, CancellationToken ct = default);

        /// <summary>
        /// <c>GET /api/Achievements/me</c> (JWT) — разблокированные достижения
        /// текущего пользователя, с вложенным <see cref="VhrUserAchievement.achievement"/>.
        /// </summary>
        Task<VhrUserAchievement[]> GetMineAsync(CancellationToken ct = default);

        /// <summary>
        /// <c>GET /api/Achievements/users/{userId}</c> — разблокированные достижения
        /// произвольного пользователя (публично, read-only).
        /// </summary>
        Task<VhrUserAchievement[]> GetForUserAsync(string userId, CancellationToken ct = default);

        /// <summary>
        /// <c>GET /api/Achievements/my-games</c> (JWT) — ПОЛНЫЙ каталог игровых
        /// достижений для кабинета игрока: все ачивки игр, с которыми игрок
        /// взаимодействовал, сгруппированные по игре. Каждая ачивка несёт флаг
        /// <see cref="VhrAchievement.unlocked"/>/<see cref="VhrAchievement.unlockedAt"/>.
        /// </summary>
        Task<VhrGameAchievements[]> GetMyGamesAsync(CancellationToken ct = default);
    }

    /// <summary>HTTP-реализация <see cref="IVhrAchievements"/>.</summary>
    public sealed class VhrAchievementsService : IVhrAchievements
    {
        private readonly VhrApiClient _api;
        private readonly VhrSdkOptions _options;
        private readonly IVhrLog _log;

        /// <summary>Создаёт сервис достижений.</summary>
        public VhrAchievementsService(VhrApiClient api, VhrSdkOptions options, IVhrLog log)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _log = log;
        }

        private string Url(string path) => $"{_options.GamesBaseUrl}/api/Achievements/{path}";

        /// <inheritdoc />
        public async Task<VhrAchievement[]> GetPlatformAsync(CancellationToken ct = default)
        {
            return await GetArrayAsync(Url("platform"), ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<VhrAchievement[]> GetForGameAsync(string gameId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(gameId))
                throw new VhrSdkException("config_invalid", "gameId обязателен для GetForGameAsync.");
            return await GetArrayAsync(Url($"games/{gameId.Trim()}"), ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<VhrUserAchievement[]> GetMineAsync(CancellationToken ct = default)
        {
            return await GetUserArrayAsync(Url("me"), ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<VhrUserAchievement[]> GetForUserAsync(string userId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new VhrSdkException("config_invalid", "userId обязателен для GetForUserAsync.");
            return await GetUserArrayAsync(Url($"users/{userId.Trim()}"), ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<VhrGameAchievements[]> GetMyGamesAsync(CancellationToken ct = default)
        {
            // Этот эндпоинт уже отдаёт {"items":[...]} — парсим обёрткой напрямую.
            var page = await _api.SendAsync<MyGamesEnvelope>(
                "GET", Url("my-games"), allowNotImplemented: true, ct: ct).ConfigureAwait(false);
            var items = page?.items;
            if (items == null) return Array.Empty<VhrGameAchievements>();
            for (int i = 0; i < items.Length; i++)
                if (items[i] != null) items[i].achievements ??= Array.Empty<VhrAchievement>();
            return items;
        }

        // ---- общие хелперы разбора массива верхнего уровня ----

        // Эндпоинт отдаёт [ {...}, ... ] на верхнем уровне; JsonUtility так не
        // умеет — оборачиваем сырое тело в {"items":[...]} и парсим обёрткой.
        private async Task<VhrAchievement[]> GetArrayAsync(string url, CancellationToken ct)
        {
            var raw = await _api.SendRawAsync("GET", url, null, allowNotImplemented: true, ct: ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<VhrAchievement>();

            var wrapped = "{\"items\":" + raw + "}";
            var page = UnityEngine.JsonUtility.FromJson<AchievementList>(wrapped);
            return page?.items ?? Array.Empty<VhrAchievement>();
        }

        private async Task<VhrUserAchievement[]> GetUserArrayAsync(string url, CancellationToken ct)
        {
            var raw = await _api.SendRawAsync("GET", url, null, allowNotImplemented: true, ct: ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<VhrUserAchievement>();

            var wrapped = "{\"items\":" + raw + "}";
            var page = UnityEngine.JsonUtility.FromJson<UserAchievementList>(wrapped);
            return page?.items ?? Array.Empty<VhrUserAchievement>();
        }

        // ---- DTO-обёртки для JsonUtility ----

        [Serializable] private sealed class AchievementList { public VhrAchievement[] items; }
        [Serializable] private sealed class UserAchievementList { public VhrUserAchievement[] items; }
        [Serializable] private sealed class MyGamesEnvelope { public VhrGameAchievements[] items; }
    }

    /// <summary>
    /// Одно достижение (платформенное или игровое). Соответствует проекции
    /// <c>AchievementsController.Project</c>.
    /// </summary>
    [Serializable]
    public sealed class VhrAchievement
    {
        /// <summary>Id достижения (GUID-строка).</summary>
        public string id;
        /// <summary>Машиночитаемый код, напр. <c>"game.{gameId}.{slug}"</c> или код платформенной ачивки.</summary>
        public string code;
        /// <summary>Заголовок (отображаемое имя).</summary>
        public string title;
        /// <summary>Описание.</summary>
        public string description;
        /// <summary>URL иконки (может быть пустым).</summary>
        public string iconUrl;
        /// <summary>Очки за получение.</summary>
        public int points;
        /// <summary>Редкость: <c>"common"</c>, <c>"rare"</c> и т.п.</summary>
        public string rarity;
        /// <summary>True — платформенная ачивка (не привязана к игре).</summary>
        public bool isPlatform;
        /// <summary>Id игры (GUID-строка); пусто/отсутствует у платформенных.</summary>
        public string gameId;
        /// <summary>Id создателя (GUID-строка).</summary>
        public string createdBy;
        /// <summary>UTC-время создания (ISO-8601).</summary>
        public string createdAt;

        /// <summary>
        /// Только в каталоге <c>my-games</c>: получена ли ачивка текущим игроком.
        /// В остальных выборках поле не приходит и остаётся <c>false</c>.
        /// </summary>
        public bool unlocked;

        /// <summary>
        /// Только в каталоге <c>my-games</c> и только для полученных: UTC-время
        /// разблокировки (ISO-8601). Иначе <c>null</c>/пусто.
        /// </summary>
        public string unlockedAt;
    }

    /// <summary>
    /// Разблокированное игроком достижение. Соответствует проекции
    /// <c>AchievementsController.ProjectPlayer</c> (эндпоинты <c>me</c> / <c>users/{userId}</c>).
    /// </summary>
    [Serializable]
    public sealed class VhrUserAchievement
    {
        /// <summary>Id записи разблокировки (GUID-строка).</summary>
        public string id;
        /// <summary>Id пользователя-владельца (GUID-строка).</summary>
        public string userId;
        /// <summary>Id достижения (GUID-строка).</summary>
        public string achievementId;
        /// <summary>UTC-время разблокировки (ISO-8601).</summary>
        public string unlockedAt;
        /// <summary>Текущий прогресс (для прогрессивных ачивок).</summary>
        public int progress;
        /// <summary>Целевой прогресс (для прогрессивных ачивок).</summary>
        public int progressTarget;
        /// <summary>Название игры (подмешивается сервером для группировки в кабинете).</summary>
        public string gameTitle;
        /// <summary>Само достижение (вложенный объект); может быть <c>null</c>.</summary>
        public VhrAchievement achievement;
    }

    /// <summary>
    /// Группа достижений одной игры для каталога <c>my-games</c>. Соответствует
    /// элементу массива <c>items</c> в ответе <c>GetMyGamesCatalog</c>.
    /// </summary>
    [Serializable]
    public sealed class VhrGameAchievements
    {
        /// <summary>Id игры (GUID-строка).</summary>
        public string gameId;
        /// <summary>Название игры.</summary>
        public string gameTitle;
        /// <summary>Путь/URL обложки игры (может быть пустым).</summary>
        public string gameCoverImagePath;
        /// <summary>Все достижения игры с флагами <c>unlocked</c>/<c>unlockedAt</c>.</summary>
        public VhrAchievement[] achievements;
    }
}
