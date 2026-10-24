using System;
using System.Threading;
using System.Threading.Tasks;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Достижения (achievements) — чтение каталога платформенных и игровых ачивок,
    /// разблокировок игрока и (SDK 1.12.0+) выдача ачивки своей игры
    /// (<see cref="UnlockAsync"/>). Backed by
    /// <c>{GamesBaseUrl}/api/Achievements/*</c> (UnityGamesMS, AchievementsController).
    /// <para>
    /// Выдавать можно только ачивки своей игры (игровой токен с <c>gid</c> этой игры), заведённые в
    /// кабинете разработчика (игра → Достижения); платформенные выдаёт только сервер.
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

        /// <summary>
        /// Все достижения <b>текущей игры</b> — как <see cref="GetForGameAsync"/>, но
        /// <c>gameId</c> SDK берёт сам: claim <c>gid</c> токена (игровой токен платформы
        /// или песочный ключ в Editor), иначе <see cref="VhrSdkOptions.GameId"/>.
        /// Достижения заводятся в кабинете разработчика (игра → Достижения).
        /// </summary>
        Task<VhrAchievement[]> GetForCurrentGameAsync(CancellationToken ct = default);

        /// <summary>
        /// Достижения текущего игрока <b>в текущей игре</b>. Отдельного серверного
        /// эндпоинта нет — это <see cref="GetMineAsync"/>, отфильтрованный по id игры
        /// (из <c>gid</c> токена или <see cref="VhrSdkOptions.GameId"/>).
        /// </summary>
        Task<VhrUserAchievement[]> GetMineForCurrentGameAsync(CancellationToken ct = default);

        /// <summary>
        /// <c>POST /api/Achievements/unlock</c> (SDK 1.12.0+) — выдать игроку ачивку текущей игры.
        /// <paramref name="slug"/> — короткий код из кабинета (<c>"first_win"</c>; регистр не важен,
        /// пробелы → <c>_</c>, как на сервере) или полный код <c>game.{gameId}.{slug}</c>; SDK сам
        /// подставит <c>game.{gameId}.</c> (id игры — из <c>gid</c> токена или
        /// <see cref="VhrSdkOptions.GameId"/>).
        /// <para>
        /// Итог: <see cref="VhrUnlockResult.unlocked"/> = <c>true</c> — открыта сейчас впервые
        /// (покажите тост); <c>false</c> — уже была открыта. Повторные вызовы дёшевы: в пределах
        /// запуска SDK запоминает подтверждённые ачивки и не ходит на сервер. Нет связи (сеть, таймаут,
        /// 5xx, истёкший токен) — исключения нет: <see cref="VhrUnlockResult.queued"/> = <c>true</c>,
        /// ачивка в очереди на устройстве (<see cref="IVhrConnection"/>) и откроется, когда связь
        /// вернётся (в том числе после перезапуска игры).
        /// </para>
        /// Ошибки — <see cref="VhrSdkException"/>: гость (нет токена) — <c>unauthorized</c>; ачивки
        /// нет — <c>not_found</c>; платформенная — <c>ServerCode = platform_achievement</c>; чужая
        /// игра — <c>game_mismatch</c>; пустой код — <c>bad_request</c> (до запроса).
        /// </summary>
        /// <example><code>
        /// var r = await VhrSdk.Achievements.UnlockAsync("first_win");
        /// if (r.unlocked) ShowToast(r.achievement.title);
        /// </code></example>
        Task<VhrUnlockResult> UnlockAsync(string slug, CancellationToken ct = default);
    }

    /// <summary>Итог <see cref="IVhrAchievements.UnlockAsync"/> (SDK 1.12.0+).</summary>
    [Serializable]
    public sealed class VhrUnlockResult
    {
        /// <summary><c>true</c> — ачивка открыта сейчас впервые; <c>false</c> — уже была открыта или запрос в очереди.</summary>
        public bool unlocked;

        /// <summary>
        /// <c>true</c> — связи с сервером не было: запрос сохранён в очереди на устройстве и уйдёт сам,
        /// когда связь вернётся (<see cref="IVhrConnection"/>). <see cref="unlocked"/> пока <c>false</c>.
        /// </summary>
        public bool queued;

        /// <summary>Полный код ачивки: <c>game.{gameId}.{slug}</c>.</summary>
        public string code;

        /// <summary>Ачивка из ответа сервера (название, иконка, очки); <c>null</c>, если запрос в очереди или уже был подтверждён в этом запуске.</summary>
        public VhrAchievement achievement;
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
            return await GetArrayAsync(Url("platform"), ct);
        }

        /// <inheritdoc />
        public async Task<VhrAchievement[]> GetForGameAsync(string gameId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(gameId))
                throw new VhrSdkException("config_invalid", "gameId обязателен для GetForGameAsync.");
            return await GetArrayAsync(Url($"games/{gameId.Trim()}"), ct);
        }

        /// <inheritdoc />
        public async Task<VhrUserAchievement[]> GetMineAsync(CancellationToken ct = default)
        {
            return await GetUserArrayAsync(Url("me"), ct);
        }

        /// <inheritdoc />
        public async Task<VhrUserAchievement[]> GetForUserAsync(string userId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new VhrSdkException("config_invalid", "userId обязателен для GetForUserAsync.");
            return await GetUserArrayAsync(Url($"users/{userId.Trim()}"), ct);
        }

        /// <inheritdoc />
        public async Task<VhrGameAchievements[]> GetMyGamesAsync(CancellationToken ct = default)
        {
            // Этот эндпоинт уже отдаёт {"items":[...]} — парсим обёрткой напрямую.
            var page = await _api.SendAsync<MyGamesEnvelope>(
                "GET", Url("my-games"), allowNotImplemented: true, ct: ct);
            var items = page?.items;
            if (items == null) return Array.Empty<VhrGameAchievements>();
            for (int i = 0; i < items.Length; i++)
                if (items[i] != null) items[i].achievements ??= Array.Empty<VhrAchievement>();
            return items;
        }

        /// <inheritdoc />
        public Task<VhrAchievement[]> GetForCurrentGameAsync(CancellationToken ct = default)
            => GetForGameAsync(CurrentGameIdOrThrow(), ct);

        /// <inheritdoc />
        public async Task<VhrUserAchievement[]> GetMineForCurrentGameAsync(CancellationToken ct = default)
        {
            var gid = CurrentGameIdOrThrow();
            var mine = await GetMineAsync(ct);
            if (mine == null || mine.Length == 0) return Array.Empty<VhrUserAchievement>();

            var list = new System.Collections.Generic.List<VhrUserAchievement>(mine.Length);
            foreach (var ua in mine)
            {
                var ach = ua?.achievement;
                if (ach == null) continue;
                if (string.Equals(ach.gameId, gid, StringComparison.OrdinalIgnoreCase)
                    // Подстраховка, если gameId не пришёл: код игровой ачивки — game.{gameId}.{slug}.
                    || (!string.IsNullOrEmpty(ach.code) &&
                        ach.code.StartsWith("game." + gid + ".", StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(ua);
                }
            }
            return list.ToArray();
        }

        private string CurrentGameIdOrThrow()
        {
            var gid = _options.ResolveCurrentGameId();
            if (string.IsNullOrWhiteSpace(gid))
                throw new VhrSdkException("config_invalid", "Не известен id игры: задайте VhrSdkOptions.GameId.");
            return gid;
        }

        // ---- выдача ачивки (SDK 1.12.0+) ----

        // Ачивки, которые сервер уже подтвердил в этом запуске («{userId}|{код}»): повторный
        // UnlockAsync (хоть каждый кадр) не ходит на сервер.
        private readonly System.Collections.Generic.HashSet<string> _confirmed =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        /// <inheritdoc />
        public async Task<VhrUnlockResult> UnlockAsync(string slug, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var code = BuildCode(CurrentGameIdOrThrow(), slug);
            var confirmKey = _options.ResolveCurrentUserId() + "|" + code;
            if (_confirmed.Contains(confirmKey))
                return new VhrUnlockResult { unlocked = false, code = code };

            var body = UnityEngine.JsonUtility.ToJson(new UnlockBody { code = code });
            var sent = await VhrOutbox.GetOrCreate(_api, _options, _log)
                .SendOrQueueAsync(VhrOutboxKinds.AchievementUnlock, code, 0, _ => body, false, ct);
            if (sent.Queued)
                return new VhrUnlockResult { queued = true, code = code };

            UnlockResponse dto = null;
            if (!string.IsNullOrWhiteSpace(sent.Body))
            {
                try { dto = UnityEngine.JsonUtility.FromJson<UnlockResponse>(sent.Body); }
                catch (Exception e) { _log?.Warn($"[VHR Achievements] не удалось разобрать ответ на выдачу ачивки: {e.Message}"); }
            }
            _confirmed.Add(confirmKey);

            // JsonUtility не оставляет вложенный объект null — пустой объект без id считаем «нет».
            var ach = dto?.achievement;
            if (ach != null && string.IsNullOrEmpty(ach.id) && string.IsNullOrEmpty(ach.code)) ach = null;
            return new VhrUnlockResult { unlocked = dto != null && dto.unlocked, code = code, achievement = ach };
        }

        /// <summary>
        /// Полный код ачивки как на сервере (<c>GameAchievementRules.BuildCode</c>): полный код своей
        /// игры (<c>game.…</c>) — как есть; иначе <c>game.{gameId}.{slug}</c>, где gameId — GUID в
        /// нижнем регистре, slug — в нижнем регистре с <c>_</c> вместо пробелов.
        /// </summary>
        internal static string BuildCode(string gameId, string slug)
        {
            var raw = (slug ?? string.Empty).Trim();
            if (raw.Length == 0)
                throw new VhrSdkException("bad_request",
                    "Пустой код достижения: передайте код из кабинета разработчика (напр. \"first_win\").",
                    0, "bad_request", null);
            if (raw.StartsWith("game.", StringComparison.OrdinalIgnoreCase)) return raw;

            var gid = Guid.TryParse(gameId, out var g) ? g.ToString("D") : (gameId ?? string.Empty).Trim().ToLowerInvariant();
            return "game." + gid + "." + raw.ToLowerInvariant().Replace(' ', '_');
        }

        // ---- общие хелперы разбора массива верхнего уровня ----

        // Эндпоинт отдаёт [ {...}, ... ] на верхнем уровне; JsonUtility так не
        // умеет — оборачиваем сырое тело в {"items":[...]} и парсим обёрткой.
        private async Task<VhrAchievement[]> GetArrayAsync(string url, CancellationToken ct)
        {
            var raw = await _api.SendRawAsync("GET", url, null, allowNotImplemented: true, ct: ct)
                ;
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<VhrAchievement>();

            var wrapped = "{\"items\":" + raw + "}";
            var page = UnityEngine.JsonUtility.FromJson<AchievementList>(wrapped);
            return page?.items ?? Array.Empty<VhrAchievement>();
        }

        private async Task<VhrUserAchievement[]> GetUserArrayAsync(string url, CancellationToken ct)
        {
            var raw = await _api.SendRawAsync("GET", url, null, allowNotImplemented: true, ct: ct)
                ;
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<VhrUserAchievement>();

            var wrapped = "{\"items\":" + raw + "}";
            var page = UnityEngine.JsonUtility.FromJson<UserAchievementList>(wrapped);
            return page?.items ?? Array.Empty<VhrUserAchievement>();
        }

        // ---- DTO-обёртки для JsonUtility ----

        [Serializable] private sealed class AchievementList { public VhrAchievement[] items; }
        [Serializable] private sealed class UserAchievementList { public VhrUserAchievement[] items; }
        [Serializable] private sealed class MyGamesEnvelope { public VhrGameAchievements[] items; }

        // POST /api/Achievements/unlock: тело { code }, ответ { unlocked, achievement }.
        [Serializable] private sealed class UnlockBody { public string code; }
#pragma warning disable CS0649 // поля заполняет JsonUtility
        [Serializable] private sealed class UnlockResponse { public bool unlocked; public VhrAchievement achievement; }
#pragma warning restore CS0649
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
