using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Таблицы лидеров игры. Бэкенд — <c>{GamesBaseUrl}/api/Leaderboard/*</c> (UnityGamesMS,
    /// <c>LeaderboardController</c>), очки — таблица <c>GameLeaderboardScores</c>.
    /// <para>
    /// SDK 1.12.0+: у игры может быть <b>несколько таблиц</b> — режимы, трассы, сложности. Таблица
    /// создаётся сама при первой отправке счёта с новым id (<see cref="SubmitAsync(string, long, CancellationToken)"/>),
    /// заводить её в кабинете не нужно; кабинет — для названий, порядка вкладок, скрытия и модерации.
    /// Без id счёт идёт в основную таблицу <c>"default"</c>. Сервер хранит лучший результат
    /// игрока по направлению таблицы (больше — лучше или меньше — лучше) за всё время, день,
    /// неделю и месяц (UTC). Игра берётся из <see cref="VhrSdkOptions.GameId"/> (в Editor Live — из
    /// песочного ключа), игрок — из токена.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Ошибки — <see cref="VhrSdkException"/>; код сервера — в <see cref="VhrSdkException.ServerCode"/>:
    /// <c>bad_board</c> (неверный id таблицы — SDK проверяет его до запроса), <c>bad_request</c>
    /// (счёт вне ±(2^53−1)), <c>board_limit</c> (у игры уже 50 таблиц). Гость (нет токена) —
    /// <c>unauthorized</c> на отправке; читать таблицы можно и без входа.
    /// <para>
    /// SDK 1.12.0+: <b>без связи счёт не теряется.</b> Если сервер недоступен (сеть, таймаут, 5xx,
    /// 429, истёкший токен), отправка не бросает исключение, а возвращает
    /// <see cref="VhrScoreResult"/> с <c>accepted = false</c>, <c>queued = true</c>: счёт лежит в
    /// очереди на диске (<see cref="IVhrConnection"/>) и уйдёт сам, когда связь вернётся (в том
    /// числе после перезапуска игры). Дневные/недельные таблицы посчитают его по времени доставки.
    /// </para>
    /// </remarks>
    public interface IVhrLeaderboard
    {
        /// <summary>
        /// Отправить счёт в основную таблицу <c>"default"</c> (<c>POST /api/Leaderboard/score</c>).
        /// Сервер оставит лучший результат игрока; итог — новый ли это рекорд, лучший счёт и место.
        /// Нет связи — <see cref="VhrScoreResult.queued"/> = <c>true</c> (счёт в очереди, уйдёт сам).
        /// </summary>
        Task<VhrScoreResult> SubmitAsync(long score, CancellationToken ct = default);

        /// <summary>
        /// Отправить счёт в таблицу <paramref name="board"/> (<c>a-z0-9_-</c>, до 32 символов; регистр
        /// и пробелы по краям не важны; <c>null</c>/пусто — <c>"default"</c>). Нет такой таблицы — сервер
        /// создаст её: больше — лучше, формат «число». Для времени/«меньше — лучше» используйте перегрузку
        /// с <see cref="VhrBoardOptions"/>.
        /// </summary>
        Task<VhrScoreResult> SubmitAsync(string board, long score, CancellationToken ct = default);

        /// <summary>
        /// Как <see cref="SubmitAsync(string, long, CancellationToken)"/>, плюс настройки таблицы на случай,
        /// если её ещё нет: название, направление, формат. Применяются <b>только при первом создании</b>
        /// (дальше рулит кабинет) — передавать их можно при каждой отправке.
        /// </summary>
        /// <example><code>
        /// // Время прохождения в мс, меньше — лучше:
        /// await VhrSdk.Leaderboard.SubmitAsync("speedrun", timeMs, new VhrBoardOptions
        /// {
        ///     Title = "Спидран", Sort = VhrBoardSort.LowerIsBetter, Format = VhrScoreFormat.Time
        /// });
        /// </code></example>
        Task<VhrScoreResult> SubmitAsync(string board, long score, VhrBoardOptions options, CancellationToken ct = default);

        /// <summary>
        /// Топ таблицы за период с местом текущего игрока
        /// (<c>GET /api/Leaderboard/games/{gameId}/boards/{board}?period=&amp;limit=</c>).
        /// <paramref name="board"/> <c>null</c> — <c>"default"</c> (та же таблица, что у
        /// <see cref="SubmitAsync(long, CancellationToken)"/>). <paramref name="limit"/> — 1..100.
        /// Ники SDK получает сам (<see cref="VhrBoardEntry.name"/>), счёт уже отформатирован
        /// (<see cref="VhrBoardEntry.formatted"/>). Таблицы ещё нет (в неё никто не отправлял счёт) —
        /// пустая страница, а не ошибка.
        /// </summary>
        Task<VhrBoardPage> GetTopAsync(
            string board = null,
            VhrLeaderboardPeriod period = VhrLeaderboardPeriod.AllTime,
            int limit = 10,
            CancellationToken ct = default);

        /// <summary>
        /// Таблицы игры в порядке вкладок (<c>GET /api/Leaderboard/games/{gameId}/boards</c>), включая
        /// скрытые на сайте (<see cref="VhrBoardInfo.hidden"/>). Пока таблиц нет — одна основная <c>"default"</c>.
        /// </summary>
        Task<VhrBoardInfo[]> GetBoardsAsync(CancellationToken ct = default);

        /// <summary>
        /// <c>GET /api/Leaderboard/global?limit=...</c> — лучшие результаты по всем играм платформы
        /// (основные таблицы «больше — лучше», bucket <c>all</c>), по убыванию очков. Ник не резолвится
        /// (<see cref="VhrLeaderboardRow.name"/> = <c>null</c>).
        /// </summary>
        Task<VhrLeaderboardRow[]> GetGlobalAsync(int limit = 100, CancellationToken ct = default);

        /// <summary>
        /// <c>GET /api/Leaderboard/games/{gameId}?limit=...</c> — топ основной таблицы игры в старом
        /// формате (без ников, мест и формата счёта).
        /// </summary>
        [Obsolete("Устарело с SDK 1.12.0: используйте GetTopAsync(board, period, limit) — места, ники, место игрока и формат счёта.", false)]
        Task<VhrLeaderboardRow[]> GetForGameAsync(string gameId, int limit = 100, CancellationToken ct = default);

        /// <summary>
        /// <c>POST /api/Leaderboard/score</c> — счёт в таблицу <c>"default"</c> (как
        /// <see cref="SubmitAsync(long, CancellationToken)"/>, без ограничения диапазона на клиенте).
        /// Нет связи (SDK 1.12.0+) — счёт встаёт в очередь, возвращается строка с присланным счётом и <c>id = 0</c>.
        /// </summary>
        [Obsolete("Устарело с SDK 1.12.0: используйте SubmitAsync(score) или SubmitAsync(board, score) — несколько таблиц, рекорд и место игрока.", false)]
        Task<VhrLeaderboardRow> SubmitScoreAsync(string gameId, long score, CancellationToken ct = default);
    }

    /// <summary>
    /// Строка лидерборда в старом формате (<see cref="IVhrLeaderboard.GetGlobalAsync"/> и устаревшие
    /// <c>GetForGameAsync</c>/<c>SubmitScoreAsync</c>) — форма ответа
    /// <c>{GamesBaseUrl}/api/Leaderboard/*</c>. Для таблиц SDK 1.12.0+ — <see cref="VhrBoardEntry"/>.
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
        /// <summary>Id строки результата.</summary>
        public int id;

        /// <summary>VHR user id владельца результата.</summary>
        public string userId;

        /// <summary>
        /// Ник игрока. Для <c>global</c>/<c>games/{id}</c> бэкенд отдаёт <c>null</c>
        /// (резолвите по <see cref="userId"/> через <see cref="IVhrProfile.ResolveNamesAsync"/>);
        /// <c>POST score</c> возвращает ник (поле <c>playerName</c> — мапится сюда).
        /// </summary>
        public string name;

        /// <summary>Id игры (может быть <c>null</c>/пустым, если бэкенд не разобрал GUID).</summary>
        public string gameId;

        /// <summary>Лучший результат игрока (в выдаче — по направлению таблицы).</summary>
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
        /// <summary>Id основной таблицы, в которую идёт счёт без id.</summary>
        public const string DefaultBoard = "default";

        /// <summary>Максимум |счёта|: 2^53−1 (точно представим в JSON/JavaScript).</summary>
        public const long MaxAbsScore = 9_007_199_254_740_991L;

        // Ник, если профиль скрыт / не найден / резолв не удался, и ник тестового игрока песочницы.
        internal const string FallbackName = "Игрок";
        internal const string SandboxName = "Тестовый игрок";

        private const int MaxLimit = 100;

        private readonly VhrApiClient _api;
        private readonly VhrSdkOptions _options;
        private readonly IVhrLog _log;
        private readonly IVhrProfile _profile;

        /// <summary>
        /// Создаёт сервис лидерборда со своим <see cref="VhrProfileService"/> для ников
        /// (конструктор до SDK 1.12.0; SDK и <see cref="VhrSdkLifetimeScope"/> передают общий профиль).
        /// </summary>
        public VhrLeaderboardService(VhrApiClient api, VhrSdkOptions options, IVhrLog log)
            : this(api, options, log, null)
        {
        }

        /// <summary>
        /// Создаёт сервис лидерборда. <paramref name="profile"/> резолвит ники в
        /// <see cref="GetTopAsync"/>; <c>null</c> — создаётся свой <see cref="VhrProfileService"/>.
        /// </summary>
        public VhrLeaderboardService(VhrApiClient api, VhrSdkOptions options, IVhrLog log, IVhrProfile profile)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _log = log;
            _profile = profile ?? new VhrProfileService(api, options, log);
        }

        private string Url(string path) => $"{_options.GamesBaseUrl}/api/Leaderboard/{path}";

        // ------------------------------------------------------------ таблицы (1.12.0)

        /// <inheritdoc />
        public Task<VhrScoreResult> SubmitAsync(long score, CancellationToken ct = default)
            => SubmitAsync(DefaultBoard, score, null, ct);

        /// <inheritdoc />
        public Task<VhrScoreResult> SubmitAsync(string board, long score, CancellationToken ct = default)
            => SubmitAsync(board, score, null, ct);

        /// <inheritdoc />
        public async Task<VhrScoreResult> SubmitAsync(
            string board, long score, VhrBoardOptions options, CancellationToken ct = default)
        {
            var slug = NormalizeBoardOrThrow(board);
            if (score > MaxAbsScore || score < -MaxAbsScore)
                throw new VhrSdkException("bad_request",
                    $"Счёт {score} вне допустимого диапазона ±{MaxAbsScore} (2^53−1).", 0, "bad_request", null);

            var gameId = CurrentGameIdOrThrow();
            // Подсказки для первого создания таблицы; пустые строки сервер считает «не задано».
            var title = TrimTitle(options?.Title);
            var sort = options != null ? VhrBoardWire.ToWire(options.Sort) : string.Empty;
            var format = options != null ? VhrBoardWire.ToWire(options.Format) : string.Empty;

            // Тело для любого счёта: очередь схлопывает ожидающие счета таблицы до лучшего и худшего
            // и пересобирает их тела с подсказками этой (самой свежей) отправки.
            string Body(long value) => JsonUtility.ToJson(new SubmitRequest
            {
                gameId = gameId, score = value, board = slug, title = title, sort = sort, format = format
            });

            var sent = await Outbox.SendOrQueueAsync(VhrOutboxKinds.LeaderboardScore, slug, score, Body, false, ct);
            if (sent.Queued)
                return new VhrScoreResult { accepted = false, queued = true, board = slug };
            return ParseScoreResult(sent.Body, slug);
        }

        // Общая очередь отложенных операций (SDK 1.12.0+): тот же экземпляр, что VhrSdk.Connection.
        private VhrOutbox Outbox => VhrOutbox.GetOrCreate(_api, _options, _log);

        /// <inheritdoc />
        public async Task<VhrBoardPage> GetTopAsync(
            string board = null,
            VhrLeaderboardPeriod period = VhrLeaderboardPeriod.AllTime,
            int limit = 10,
            CancellationToken ct = default)
        {
            var slug = NormalizeBoardOrThrow(board);
            var gid = CurrentGameIdOrThrow();
            limit = Math.Max(1, Math.Min(MaxLimit, limit));

            var url = Url($"games/{Uri.EscapeDataString(gid)}/boards/{Uri.EscapeDataString(slug)}" +
                          $"?period={VhrBoardWire.ToWire(period)}&limit={limit}");

            PageDto dto;
            try
            {
                dto = await _api.SendAsync<PageDto>("GET", url, ct: ct);
            }
            catch (VhrSdkException ex) when (ex.HttpStatus == 404 &&
                                             string.Equals(ex.ServerCode, "board_not_found", StringComparison.OrdinalIgnoreCase))
            {
                // Code-first: таблица появится при первой отправке счёта — до этого она просто пустая.
                _log?.Verbose($"[VHR Leaderboard] таблицы '{slug}' пока нет — пустая страница.");
                return EmptyPage(slug, period);
            }

            if (dto == null)
                return EmptyPage(slug, period);

            var info = MapBoard(dto.board, slug);
            var page = new VhrBoardPage
            {
                board = info,
                period = VhrBoardWire.ParsePeriod(dto.period, period),
                total = Math.Max(0, dto.total)
            };

            var src = dto.entries ?? Array.Empty<EntryDto>();
            var entries = new List<VhrBoardEntry>(src.Length);
            VhrBoardEntry mine = null;
            foreach (var e in src)
            {
                if (e == null) continue;
                var entry = new VhrBoardEntry
                {
                    rank = e.rank,
                    userId = NullIfEmpty(e.userId),
                    score = e.score,
                    formatted = VhrScoreFormatter.Format(e.score, info),
                    isCurrentUser = e.isCurrentUser
                };
                if (entry.isCurrentUser && mine == null) mine = entry;
                entries.Add(entry);
            }
            page.entries = entries.ToArray();

            // JsonUtility не оставляет вложенный объект null: "me": null приходит пустым объектом
            // с rank = 0. Места на сервере 1-based, поэтому rank > 0 — признак настоящего «меня».
            if (dto.me != null && dto.me.rank > 0)
            {
                page.me = new VhrBoardEntry
                {
                    rank = dto.me.rank,
                    userId = mine?.userId ?? CurrentUserIdOrNull(),
                    score = dto.me.score,
                    formatted = VhrScoreFormatter.Format(dto.me.score, info),
                    isCurrentUser = true
                };
            }

            await FillNamesAsync(page, ct);
            return page;
        }

        /// <inheritdoc />
        public async Task<VhrBoardInfo[]> GetBoardsAsync(CancellationToken ct = default)
        {
            var gid = CurrentGameIdOrThrow();
            var dto = await _api.SendAsync<BoardsDto>("GET", Url($"games/{Uri.EscapeDataString(gid)}/boards"), ct: ct);
            var src = dto?.boards;
            if (src == null || src.Length == 0) return Array.Empty<VhrBoardInfo>();

            var primary = VhrBoardWire.TryNormalizeBoard(dto.primary, out var p) ? p : null;
            var anyPrimary = false;
            var list = new List<VhrBoardInfo>(src.Length);
            foreach (var b in src)
            {
                if (b == null || string.IsNullOrWhiteSpace(b.id)) continue;
                var info = MapBoard(b, null);
                anyPrimary |= info.isPrimary;
                list.Add(info);
            }

            // Страховка: флаг основной не пришёл в элементах — берём поле primary ответа.
            if (!anyPrimary && primary != null)
            {
                foreach (var info in list)
                    if (info.id == primary) info.isPrimary = true;
            }
            return list.ToArray();
        }

        // ------------------------------------------------------------ старый API

        /// <inheritdoc />
        public async Task<VhrLeaderboardRow[]> GetGlobalAsync(int limit = 100, CancellationToken ct = default)
        {
            var raw = await _api.SendRawAsync(
                "GET", Url($"global?limit={limit}"), allowNotImplemented: true, ct: ct);
            return Wrap(raw)?.items ?? Array.Empty<VhrLeaderboardRow>();
        }

        /// <inheritdoc />
        [Obsolete("Устарело с SDK 1.12.0: используйте GetTopAsync(board, period, limit) — места, ники, место игрока и формат счёта.", false)]
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
        [Obsolete("Устарело с SDK 1.12.0: используйте SubmitAsync(score) или SubmitAsync(board, score) — несколько таблиц, рекорд и место игрока.", false)]
        public async Task<VhrLeaderboardRow> SubmitScoreAsync(
            string gameId, long score, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(gameId))
                throw new VhrSdkException("config_invalid", "gameId обязателен для SubmitScoreAsync.");

            // Тот же POST, что у SubmitAsync, — в основную таблицу default; long без клампа.
            string Body(long value) => JsonUtility.ToJson(new SubmitRequest
            {
                gameId = gameId,
                score = value,
                board = DefaultBoard,
                title = string.Empty,
                sort = string.Empty,
                format = string.Empty
            });

            // Нет связи — счёт в очереди (SDK 1.12.0+); строка-заглушка с присланным счётом.
            var sent = await Outbox.SendOrQueueAsync(VhrOutboxKinds.LeaderboardScore, DefaultBoard, score, Body, false, ct);
            if (sent.Queued)
                return new VhrLeaderboardRow { gameId = gameId, score = score };

            ScoreResponse res = null;
            if (!string.IsNullOrWhiteSpace(sent.Body))
            {
                try { res = JsonUtility.FromJson<ScoreResponse>(sent.Body); }
                catch (Exception ex)
                {
                    throw new VhrSdkException("deserialize_error",
                        $"Could not parse response from {Url("score")}: {ex.Message}. Body={sent.Body}", 0, ex);
                }
            }
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

        // ------------------------------------------------------------ helpers

        private string CurrentGameIdOrThrow()
        {
            var gid = _options.ResolveCurrentGameId();
            if (string.IsNullOrWhiteSpace(gid))
                throw new VhrSdkException("config_invalid", "Не известен id игры: задайте VhrSdkOptions.GameId.");
            return gid;
        }

        private static string NormalizeBoardOrThrow(string board)
        {
            if (VhrBoardWire.TryNormalizeBoard(board, out var slug)) return slug;
            throw new VhrSdkException("bad_board",
                $"Неверный id таблицы лидеров '{board}': латиница в нижнем регистре, цифры, '_' и '-', " +
                "до 32 символов, первый символ — буква или цифра (напр. \"survival\", \"level_1\").",
                0, "bad_board", null);
        }

        private static string TrimTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return string.Empty;
            var t = title.Trim();
            return t.Length <= 64 ? t : t.Substring(0, 64).TrimEnd();
        }

        private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        // sub текущего токена (игровой токен / песочный ключ) — userId для «меня» вне топа.
        private string CurrentUserIdOrNull()
        {
            try
            {
                var token = _options.TokenProvider?.Invoke();
                return VhrJwt.TryDecode(token, out var info) ? NullIfEmpty(info.Subject) : null;
            }
            catch
            {
                return null;
            }
        }

        private static VhrBoardInfo MapBoard(BoardDto b, string fallbackSlug)
        {
            if (b == null || string.IsNullOrWhiteSpace(b.id))
                return DefaultInfo(fallbackSlug ?? DefaultBoard);

            var id = VhrBoardWire.TryNormalizeBoard(b.id, out var s) ? s : b.id.Trim();
            return new VhrBoardInfo
            {
                id = id,
                title = string.IsNullOrWhiteSpace(b.title) ? VhrBoardWire.DefaultTitle(id) : b.title,
                sort = VhrBoardWire.ParseSort(b.sort),
                format = VhrBoardWire.ParseFormat(b.format),
                suffix = b.suffix ?? string.Empty,
                isPrimary = b.isPrimary,
                hidden = b.hidden
            };
        }

        // Мета «по умолчанию» (как синтезирует сервер): больше — лучше, число, без суффикса.
        private static VhrBoardInfo DefaultInfo(string slug) => new VhrBoardInfo
        {
            id = slug,
            title = VhrBoardWire.DefaultTitle(slug),
            sort = VhrBoardSort.HigherIsBetter,
            format = VhrScoreFormat.Number,
            suffix = string.Empty,
            isPrimary = slug == DefaultBoard
        };

        private static VhrBoardPage EmptyPage(string slug, VhrLeaderboardPeriod period) => new VhrBoardPage
        {
            board = DefaultInfo(slug),
            period = period,
            entries = Array.Empty<VhrBoardEntry>(),
            me = null,
            total = 0
        };

        /// <summary>
        /// Ники для строк страницы одним батчем через <see cref="IVhrProfile.ResolveNamesAsync"/>.
        /// Скрытый профиль, пустой ник и любая ошибка резолва — «Игрок» (страница не падает);
        /// тестовый игрок песочницы (<c>sbx_*</c>) — «Тестовый игрок» без запроса.
        /// </summary>
        private async Task FillNamesAsync(VhrBoardPage page, CancellationToken ct)
        {
            var ids = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Collect(VhrBoardEntry e)
            {
                if (e?.userId == null || IsSandboxId(e.userId)) return;
                if (seen.Add(e.userId)) ids.Add(e.userId);
            }
            foreach (var e in page.entries) Collect(e);
            Collect(page.me);

            Dictionary<string, string> names = null;
            if (ids.Count > 0)
            {
                try
                {
                    var resolved = await _profile.ResolveNamesAsync(ids.ToArray(), ct);
                    if (resolved != null)
                    {
                        // Без учёта регистра: GUID в очках и в AuthMS могут различаться регистром.
                        names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var kv in resolved)
                            if (kv.Key != null) names[kv.Key] = kv.Value;
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _log?.Warn($"[VHR Leaderboard] не удалось получить ники ({ex.Message}) — показываем «{FallbackName}».");
                }
            }

            string NameOf(string userId)
            {
                if (userId == null) return FallbackName;
                if (IsSandboxId(userId)) return SandboxName;
                return names != null && names.TryGetValue(userId, out var n) && !string.IsNullOrWhiteSpace(n)
                    ? n
                    : FallbackName;
            }

            foreach (var e in page.entries) e.name = NameOf(e.userId);
            if (page.me != null) page.me.name = NameOf(page.me.userId);
        }

        private static bool IsSandboxId(string userId) =>
            userId.StartsWith("sbx_", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Разбор ответа <c>POST score</c>: <c>{ id, userId, playerName, gameId, score, createdAt,
        /// board, improved, best, rank }</c>. <c>board</c> — строка-id или объект с <c>id</c>;
        /// <c>accepted</c> (если сервер его пришлёт) — <c>false</c> только явным значением.
        /// </summary>
        private VhrScoreResult ParseScoreResult(string raw, string slug)
        {
            var result = new VhrScoreResult { accepted = true, board = slug };
            if (string.IsNullOrWhiteSpace(raw))
            {
                _log?.Warn("[VHR Leaderboard] пустой ответ на отправку счёта.");
                return result;
            }

            try
            {
                var dto = JsonUtility.FromJson<ScoreResponse>(raw);
                if (dto != null)
                {
                    result.improved = dto.improved;
                    result.best = dto.best;
                    result.rank = Math.Max(0, dto.rank);
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[VHR Leaderboard] не удалось разобрать ответ на отправку счёта: {ex.Message}");
            }

            if (string.Equals(VhrJwt.ReadClaim(raw, "accepted"), "false", StringComparison.OrdinalIgnoreCase))
                result.accepted = false;

            var board = ReadBoardId(raw);
            if (board != null) result.board = board;
            return result;
        }

        /// <summary>
        /// Id таблицы из поля <c>board</c> верхнего уровня: строка (<c>"board":"survival"</c>) или
        /// объект (<c>"board":{"id":"survival",…}</c>). <c>null</c>, если поля нет или id неверный.
        /// </summary>
        internal static string ReadBoardId(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            const string needle = "\"board\"";
            int k = json.IndexOf(needle, StringComparison.Ordinal);
            while (k >= 0)
            {
                int i = k + needle.Length;
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i >= json.Length) return null;
                if (json[i] != ':')
                {
                    k = json.IndexOf(needle, k + needle.Length, StringComparison.Ordinal); // это было значение
                    continue;
                }
                i++;
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i >= json.Length) return null;

                string id = json[i] == '"' ? VhrJwt.ReadClaim(json.Substring(k), "board")
                    : json[i] == '{' ? VhrJwt.ReadClaim(json.Substring(i), "id")
                    : null;
                return !string.IsNullOrWhiteSpace(id) && VhrBoardWire.TryNormalizeBoard(id, out var slug) ? slug : null;
            }
            return null;
        }

        // JsonUtility не парсит JSON-массив верхнего уровня — оборачиваем тело
        // в {"items":[...]} и парсим обёрткой (см. VhrLobbyService.GetFriendsAsync).
        private static LeaderboardArray Wrap(string rawArrayJson)
        {
            if (string.IsNullOrWhiteSpace(rawArrayJson)) return null;
            try { return JsonUtility.FromJson<LeaderboardArray>("{\"items\":" + rawArrayJson + "}"); }
            catch { return null; }
        }

#pragma warning disable CS0649 // поля DTO заполняет JsonUtility
        [Serializable] private sealed class LeaderboardArray { public VhrLeaderboardRow[] items; }

        // Тело POST /api/Leaderboard/score: { gameId, score, board, title, sort, format }.
        // Пустые title/sort/format сервер понимает как «не задано» (подсказки только при создании таблицы).
        [Serializable]
        private sealed class SubmitRequest
        {
            public string gameId;
            public long score;
            public string board;
            public string title;
            public string sort;
            public string format;
        }

        // Ответ POST /api/Leaderboard/score: { id, userId, playerName, gameId, score, createdAt,
        // board, improved, best, rank }. board разбирается отдельно (ReadBoardId).
        [Serializable]
        private sealed class ScoreResponse
        {
            public int id;
            public string userId;
            public string playerName;
            public string gameId;
            public long score;
            public string createdAt;
            public bool improved;
            public long best;
            public int rank;
        }

        // Мета таблицы: { id, title, sort: "desc|asc", format: "number|time", suffix, isPrimary, hidden, order }.
        [Serializable]
        private sealed class BoardDto
        {
            public string id;
            public string title;
            public string sort;
            public string format;
            public string suffix;
            public bool isPrimary;
            public bool hidden;
            public int order;
        }

        // GET games/{gameId}/boards: { primary, boards: [...] }.
        [Serializable]
        private sealed class BoardsDto
        {
            public string primary;
            public BoardDto[] boards;
        }

        // Строка топа: { rank, userId|null, score, updatedAt, isCurrentUser }.
        [Serializable]
        private sealed class EntryDto
        {
            public int rank;
            public string userId;
            public long score;
            public string updatedAt;
            public bool isCurrentUser;
        }

        // «Я»: { rank, score } | null.
        [Serializable]
        private sealed class MeDto
        {
            public int rank;
            public long score;
        }

        // GET games/{gameId}/boards/{board}: { board, period, entries, me, total }.
        [Serializable]
        private sealed class PageDto
        {
            public BoardDto board;
            public string period;
            public EntryDto[] entries;
            public MeDto me;
            public int total;
        }
#pragma warning restore CS0649
    }

    /// <summary>
    /// Строки протокола таблиц лидеров ↔ перечисления SDK и правила id таблицы (как на сервере).
    /// <c>JsonUtility</c> не умеет enum из строк — маппинг ручной.
    /// </summary>
    internal static class VhrBoardWire
    {
        internal const int MaxBoardIdLength = 32;

        internal static string ToWire(VhrBoardSort sort) => sort == VhrBoardSort.LowerIsBetter ? "asc" : "desc";

        internal static string ToWire(VhrScoreFormat format) => format == VhrScoreFormat.Time ? "time" : "number";

        internal static string ToWire(VhrLeaderboardPeriod period) => period switch
        {
            VhrLeaderboardPeriod.Daily => "day",
            VhrLeaderboardPeriod.Weekly => "week",
            VhrLeaderboardPeriod.Monthly => "month",
            _ => "all"
        };

        internal static VhrBoardSort ParseSort(string s) =>
            string.Equals(s?.Trim(), "asc", StringComparison.OrdinalIgnoreCase)
                ? VhrBoardSort.LowerIsBetter
                : VhrBoardSort.HigherIsBetter;

        internal static VhrScoreFormat ParseFormat(string s) =>
            string.Equals(s?.Trim(), "time", StringComparison.OrdinalIgnoreCase)
                ? VhrScoreFormat.Time
                : VhrScoreFormat.Number;

        internal static VhrLeaderboardPeriod ParsePeriod(string s, VhrLeaderboardPeriod fallback)
        {
            switch ((s ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "all": case "alltime": return VhrLeaderboardPeriod.AllTime;
                case "day": case "daily": return VhrLeaderboardPeriod.Daily;
                case "week": case "weekly": return VhrLeaderboardPeriod.Weekly;
                case "month": case "monthly": return VhrLeaderboardPeriod.Monthly;
                default: return fallback;
            }
        }

        /// <summary>
        /// Id таблицы как на сервере: <c>Trim().ToLowerInvariant()</c>, пусто/<c>null</c> → <c>default</c>,
        /// затем <c>^[a-z0-9][a-z0-9_-]{0,31}$</c>.
        /// </summary>
        internal static bool TryNormalizeBoard(string board, out string slug)
        {
            slug = string.IsNullOrWhiteSpace(board)
                ? VhrLeaderboardService.DefaultBoard
                : board.Trim().ToLowerInvariant();
            if (slug.Length > MaxBoardIdLength) return false;
            for (int i = 0; i < slug.Length; i++)
            {
                char c = slug[i];
                bool alnum = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                if (alnum) continue;
                if (i > 0 && (c == '_' || c == '-')) continue;
                return false;
            }
            return true;
        }

        /// <summary>Название по умолчанию (как на сервере): <c>default</c> → «Рекорды», иначе id с пробелами и заглавной.</summary>
        internal static string DefaultTitle(string slug)
        {
            if (string.IsNullOrEmpty(slug) || slug == VhrLeaderboardService.DefaultBoard) return "Рекорды";
            var t = slug.Replace('_', ' ').Replace('-', ' ').Trim();
            if (t.Length == 0) return slug;
            return char.ToUpperInvariant(t[0]) + t.Substring(1);
        }
    }
}
