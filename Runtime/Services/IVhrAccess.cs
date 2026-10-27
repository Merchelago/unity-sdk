using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Доступ игрока к игре (SDK 1.13.0+): обычная игра или премиум, полный доступ, демо или
    /// нет доступа, и окно покупки полной версии на платформе.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Одна сборка — оба режима.</b> Пока администратор не одобрил премиум-статус, игра обычная:
    /// <see cref="VhrAccessState.IsFull"/> = <c>true</c>, реклама работает. После одобрения та же
    /// сборка получает <see cref="VhrAccessLevel.Demo"/> у игроков без покупки и
    /// <see cref="VhrAccessLevel.Full"/> у купивших или подписчиков Premium. Что ограничить в демо — решает игра.
    /// </para>
    /// <para>
    /// <b>Источник правды — сервер</b> (<c>GET {GamesBaseUrl}/api/Games/{id}/access</c> игровым
    /// токеном). Страница платформы сообщает о смене доступа сама (<see cref="AccessChanged"/>) —
    /// перезапускать игру после покупки не нужно.
    /// </para>
    /// <para>
    /// <b>Связанные правила.</b> В премиум-игре (и в полном доступе, и в демо) рекламы нет:
    /// <see cref="IVhrAds"/> сразу возвращает <see cref="VhrAdStatus.Unavailable"/> без запроса к сайту.
    /// Товары каталога (<see cref="IVhrEconomy.PurchaseAsync(string, int, CancellationToken)"/>)
    /// продаются только при полном доступе: в демо покупка сразу возвращает
    /// <see cref="VhrPurchaseStatus.Forbidden"/> с кодом <c>demo_access</c> (сервер тоже откажет).
    /// </para>
    /// <para>
    /// <b>Гарантии.</b> Методы не бросают исключений (кроме <see cref="OperationCanceledException"/> при
    /// отмене <c>ct</c>) и всегда завершаются. Вызывайте с главного потока Unity.
    /// </para>
    /// <para>
    /// <b>Unity Editor.</b> Доступ симулируется переключателем в окне <c>VHR → Тестирование в Editor</c>
    /// (Regular / Premium Full / Premium Demo / Premium None) — и в Simulation, и в Live; «Симулировать
    /// покупку» там же присылает <see cref="AccessChanged"/> в запущенную игру.
    /// </para>
    /// </remarks>
    /// <example><code>
    /// VhrSdk.Access.AccessChanged += ApplyAccess;      // подписка — до запроса
    /// ApplyAccess(await VhrSdk.Access.GetAsync());
    ///
    /// void ApplyAccess(VhrAccessState a)
    /// {
    ///     demoBanner.SetActive(a.IsDemo);
    ///     buyButton.gameObject.SetActive(a.CanPurchase);
    ///     lockedLevels.SetActive(!a.IsFull);
    /// }
    ///
    /// // Кнопка «Купить полную версию»
    /// var r = await VhrSdk.Access.OpenPurchaseAsync();
    /// if (r.IsFull) ApplyAccess(r.State);
    /// </code></example>
    public interface IVhrAccess
    {
        /// <summary>
        /// Последнее известное состояние (никогда не <c>null</c>, без запроса): ответ сервера или
        /// хоста, пока его нет — подсказка игрового токена, а без неё — обычная игра с полным
        /// доступом. Откуда данные — <see cref="VhrAccessState.Origin"/>. Для решения на старте
        /// используйте <see cref="GetAsync"/>.
        /// </summary>
        VhrAccessState Current { get; }

        /// <summary>
        /// Доступ изменился без перезапуска игры: покупка или подписка (в игре или на сайте),
        /// одобрение родителя, решение администратора; в Editor — переключатель и «Симулировать
        /// покупку» в окне VHR. Приходит на главном потоке с новым состоянием; первая загрузка
        /// (<see cref="GetAsync"/>) событие не вызывает.
        /// </summary>
        event Action<VhrAccessState> AccessChanged;

        /// <summary>
        /// Состояние доступа с сервера (<c>GET {GamesBaseUrl}/api/Games/{id}/access</c>). Нет
        /// связи / старый сервер — ответ страницы платформы (<c>vhr:access:get</c>), подсказка
        /// игрового токена или последнее известное; ничего — обычная игра с полным доступом.
        /// Не бросает (кроме отмены <paramref name="ct"/>), никогда не <c>null</c>.
        /// </summary>
        Task<VhrAccessState> GetAsync(CancellationToken ct = default);

        /// <summary>
        /// Окно платформы «Купить полную версию»: «Купить за N монет», «Оформить Premium» (если игра
        /// в подписке), для детского аккаунта — «Попросить у родителя». Игра окно не рисует.
        /// Итог — <see cref="VhrAccessPurchaseResult"/> со статусом
        /// <see cref="VhrAccessPurchaseStatus"/> и новым состоянием; при полном доступе приходит и
        /// <see cref="AccessChanged"/>. Обычная игра / доступ уже полный / игра не на vhrgames.ru —
        /// сразу <see cref="VhrAccessPurchaseStatus.Unavailable"/>. Одновременно — одно окно.
        /// </summary>
        /// <param name="ct">Отмена ожидания. Окно на сайте при этом не закрывается: если игрок
        /// всё же купит, придёт <see cref="AccessChanged"/>.</param>
        Task<VhrAccessPurchaseResult> OpenPurchaseAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Реализация <see cref="IVhrAccess"/>: сервер + страница-хост VHR (WebGL,
    /// <see cref="VhrWebGlAccessChannel"/>), в Unity Editor — симуляция из окна VHR. Один общий
    /// экземпляр на процесс (<see cref="GetOrCreate"/>): статический фасад, VContainer, реклама и
    /// экономика видят одно и то же состояние.
    /// </summary>
    public sealed class VhrAccessService : IVhrAccess, IDisposable
    {
        // Сколько ждём ответа хоста на vhr:access:get: сайт отвечает из своего состояния сразу;
        // тишина = старая версия сайта без премиум-игр.
        internal const int HostGetTimeoutMs = 3000;
        // Страховочный лимит на окно покупки (игрок может уйти пополнять счёт / оплачивать подписку).
        internal const int HostPurchaseTimeoutMs = 900000;

        private static VhrAccessService _shared;

        private readonly VhrApiClient _api;
        private readonly VhrSdkOptions _options;
        private readonly IVhrLog _log;

        // Последнее состояние от сервера / хоста / симуляции.
        private VhrAccessState _cache;
        // Что последним видела игра (AccessChanged / GetAsync) — чтобы не слать одно и то же дважды.
        private VhrAccessState _lastNotified = VhrAccessState.Regular();
#if !UNITY_EDITOR
        // Токен, при котором получен _cache: новый токен с другим уровнем доступа свежее кэша.
        private string _cacheToken;
        // Растёт при каждой «новости» о доступе (хост, покупка): ответ сервера на запрос, начатый
        // раньше, мог устареть — тогда SDK переспрашивает.
        private int _changeSeq;
        private Task<VhrAccessState> _inflight;
#endif
        private bool _purchaseInFlight;
        private bool _disposed;

        /// <inheritdoc />
        public event Action<VhrAccessState> AccessChanged;

        /// <summary>
        /// Создаёт сервис. Обычно не нужно — используйте <see cref="VhrSdk.Access"/> или внедрение
        /// <see cref="IVhrAccess"/> через <see cref="VhrSdkLifetimeScope"/>.
        /// </summary>
        public VhrAccessService(VhrApiClient api, VhrSdkOptions options, IVhrLog log = null)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _log = log ?? new VhrUnityLog(options.VerboseLogging);

#if UNITY_EDITOR
            _cache = VhrEditorSandbox.BuildSimAccessState();
            VhrEditorSandbox.SimAccessChanged += OnSimAccessChanged;
            if (_cache.IsPremium)
                _log.Info($"Access: симуляция доступа в Editor — {VhrEditorSandbox.DescribeSimAccess()} " +
                          "(окно VHR → Тестирование в Editor). Реклама в премиум-игре не показывается.");
#else
            // WebGL: слушаем хост сразу, чтобы vhr:access:changed доходил до игры ещё до первого запроса.
            VhrWebGlAccessChannel.EnsureInitialized();
            VhrWebGlAccessChannel.HostAccessChanged += OnHostAccessChanged;
#endif
        }

        /// <summary>
        /// Общий экземпляр на процесс. Первый вызов создаёт его из переданных зависимостей,
        /// последующие возвращают уже созданный.
        /// </summary>
        public static VhrAccessService GetOrCreate(VhrApiClient api, VhrSdkOptions options, IVhrLog log)
        {
            if (_shared != null && !_shared._disposed) return _shared;
            _shared = new VhrAccessService(api, options, log);
            return _shared;
        }

        /// <summary>Сбрасывает общий экземпляр. Только для тестов / <see cref="VhrSdk.ResetForTests"/>.</summary>
        internal static void ResetShared()
        {
            _shared?.Dispose();
            _shared = null;
        }

        /// <summary>
        /// Для рекламы и экономики: последнее известное состояние без запросов. Если сервис доступа
        /// ещё не создан — симуляция Editor или подсказка игрового токена, иначе обычная игра.
        /// </summary>
        internal static VhrAccessState Peek(VhrSdkOptions options)
        {
            var s = _shared;
            if (s != null && !s._disposed) return s.CurrentInternal();
#if UNITY_EDITOR
            return VhrEditorSandbox.BuildSimAccessState();
#else
            return TokenHint(SafeToken(options)) ?? VhrAccessState.Regular();
#endif
        }

        /// <inheritdoc />
        public VhrAccessState Current => CurrentInternal().Clone();

        // Без копии: только для чтения внутри SDK.
        private VhrAccessState CurrentInternal()
        {
            var s = _cache;
#if !UNITY_EDITOR
            // Хост перевыпускает игровой токен при смене доступа: если токен новее кэша и его
            // claims говорят о другом уровне доступа — токен свежее.
            var token = SafeToken(_options);
            if (s == null || !string.Equals(token, _cacheToken, StringComparison.Ordinal))
            {
                var hint = TokenHint(token);
                if (hint != null && (s == null || !VhrAccessCodec.SameLevel(hint, s)))
                    s = VhrAccessCodec.MergeTokenHint(hint, s);
            }
#endif
            return s ?? VhrAccessState.Regular();
        }

        private static string SafeToken(VhrSdkOptions options)
        {
            try { return options?.TokenProvider?.Invoke(); }
            catch { return null; }
        }

#if !UNITY_EDITOR
        // Разбор claims последнего токена (реклама может спрашивать IsSupported каждый кадр).
        private static string s_hintToken;
        private static VhrAccessState s_hint;

        private static VhrAccessState TokenHint(string token)
        {
            if (string.Equals(token, s_hintToken, StringComparison.Ordinal)) return s_hint;
            s_hint = VhrAccessCodec.FromToken(token);
            s_hintToken = token;
            return s_hint;
        }
#endif

        // ------------------------------------------------------------ чтение

        /// <inheritdoc />
        public async Task<VhrAccessState> GetAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_disposed) return CurrentInternal().Clone();

#if UNITY_EDITOR
            // Кадр паузы — как с настоящим сервером, чтобы игра не полагалась на синхронный ответ.
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            return CurrentInternal().Clone();
#else
            var task = _inflight;
            if (task == null || task.IsCompleted)
            {
                task = FetchAsync();
                _inflight = task;
            }
            var st = await VhrAccessAwait.WithCancellation(task, ct);
            return (st ?? CurrentInternal()).Clone();
#endif
        }

#if !UNITY_EDITOR
        // Общий для всех вызывающих запрос (без ct: отмена одного вызова не рвёт запрос для других;
        // транспорт сам ограничен RequestTimeoutSeconds, ожидание хоста — HostGetTimeoutMs).
        private async Task<VhrAccessState> FetchAsync()
        {
            try
            {
                for (int attempt = 0; attempt < 3 && !_disposed; attempt++)
                {
                    int seq = _changeSeq;
                    var server = await RequestServerAsync();
                    if (server != null)
                    {
                        // Пока шёл запрос, хост сообщил о смене доступа / прошла покупка — ответ мог устареть.
                        if (seq != _changeSeq) continue;
                        Store(server, notify: NotifyOnFetch(server));
                        return server;
                    }

                    // Сервер недоступен / старый — спросим страницу платформы.
                    var host = await AskHostAsync(CancellationToken.None);
                    if (host != null)
                    {
                        if (seq != _changeSeq) continue;
                        Store(host, notify: NotifyOnFetch(host));
                        return host;
                    }
                    break;
                }
            }
            catch (Exception e)
            {
                _log.Verbose("Access: не удалось получить состояние доступа: " + e.Message);
            }
            return CurrentInternal();
        }

        // Первая загрузка событие не вызывает; изменение уже подтверждённого состояния — вызывает.
        private bool NotifyOnFetch(VhrAccessState fresh)
        {
            var prev = _cache;
            return prev != null && prev.Origin != VhrAccessOrigin.Token && prev.Origin != VhrAccessOrigin.Default &&
                   !prev.SameAs(fresh);
        }

        /// <summary>
        /// <c>GET {GamesBaseUrl}/api/Games/{id}/access</c>. <c>null</c> — нет связи, старый сервер
        /// (404), нет токена и т.п.; <c>403</c> с телом состояния (<c>premium_required</c>) — это ответ «нет доступа».
        /// </summary>
        private async Task<VhrAccessState> RequestServerAsync()
        {
            var gid = _options.ResolveCurrentGameId();
            if (string.IsNullOrWhiteSpace(gid)) return null;
            var url = $"{_options.GamesBaseUrl}/api/Games/{Uri.EscapeDataString(gid)}/access";
            try
            {
                var raw = await _api.SendRawAsync("GET", url);
                var st = VhrAccessCodec.FromJson(raw, VhrAccessOrigin.Server);
                if (st == null) _log.Verbose("Access: сервер вернул ответ без состояния доступа: " + raw);
                return st;
            }
            catch (VhrSdkException ex)
            {
                var st = VhrAccessCodec.FromJson(ex.ResponseBody, VhrAccessOrigin.Server);
                if (st != null) return st;
                _log.Verbose($"Access: сервер не ответил состоянием доступа ({ex.Code}, HTTP {ex.HttpStatus}) — " +
                             "берём ответ страницы платформы / подсказку токена.");
                return null;
            }
        }

        /// <summary>Состояние от страницы платформы (<c>vhr:access:get</c>) или <c>null</c> (не хост / молчит).</summary>
        private async Task<VhrAccessState> AskHostAsync(CancellationToken ct)
        {
            if (!VhrWebGlAccessChannel.IsSupported || !VhrWebGlAccessChannel.IsHosted()) return null;

            var tcs = new TaskCompletionSource<VhrAccessState>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requestId = Guid.NewGuid().ToString("N");
            int handle = VhrWebGlAccessChannel.Get(requestId, HostGetTimeoutMs,
                (status, reason, payload) => tcs.TrySetResult(status == 1 ? payload?.ToState() : null));
            if (handle <= 0) return null;

            // JS отвечает по своему таймеру; сторож C# — на случай сломанной страницы (реальное время).
            var stop = new CancellationTokenSource();
            _ = VhrAsync.WatchdogAsync(HostGetTimeoutMs / 1000f + 1f, stop.Token, () => tcs.TrySetResult(null));
            try
            {
                return await VhrAccessAwait.WithCancellation(tcs.Task, ct);
            }
            finally
            {
                stop.Cancel();
                stop.Dispose();
            }
        }

        /// <summary>
        /// Перечитать с сервера после новости о доступе (не бросает). Идущий запрос переспросит
        /// сам: вызывающий уже сменил номер новости (<c>_changeSeq</c>).
        /// </summary>
        private async Task RefreshQuietlyAsync()
        {
            if (_disposed) return;
            var running = _inflight;
            if (running != null && !running.IsCompleted) return;
            var task = FetchAsync();
            _inflight = task;
            try { await task; }
            catch { /* FetchAsync не бросает */ }
        }

        private void OnHostAccessChanged(VhrWebGlAccessChannel.HostPayload payload)
        {
            if (_disposed) return;
            var st = payload?.ToState();
            if (st == null) return;
            _changeSeq++;
            _log.Verbose("Access: страница платформы сообщила о смене доступа — " + st);
            Store(st, notify: !CurrentInternal().SameAs(st));
            // Источник правды — сервер: перечитываем (токен хост перевыпускает сам).
            _ = RefreshQuietlyAsync();
        }
#endif

        // ------------------------------------------------------------ покупка

        /// <inheritdoc />
        public async Task<VhrAccessPurchaseResult> OpenPurchaseAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (_disposed)
                return Fail(VhrAccessPurchaseStatus.Unavailable, "disposed", "Сервис доступа уничтожен.");
            if (_purchaseInFlight)
                return Fail(VhrAccessPurchaseStatus.Unavailable, "busy", "Окно покупки уже открыто — дождитесь его результата.");

            _purchaseInFlight = true;
            try
            {
#if UNITY_EDITOR
                return LogResult(await SimulatePurchaseAsync());
#else
                if (!VhrWebGlAccessChannel.IsSupported || !VhrWebGlAccessChannel.IsHosted())
                {
                    var known = VhrWebGlAccessChannel.IsSupported ? CurrentInternal() : await GetAsync(ct);
                    return LogResult(Fail(VhrAccessPurchaseStatus.Unavailable, "not_hosted",
                        "Полная версия покупается только на vhrgames.ru: игра должна быть открыта со страницы платформы " +
                        "(окно покупки показывает сайт).", known));
                }

                // 1. Свежее состояние от страницы платформы — заодно проверка, что сайт знает премиум-игры.
                var host = await AskHostAsync(ct);
                if (host == null)
                {
                    return LogResult(Fail(VhrAccessPurchaseStatus.Unavailable, "host_unsupported",
                        "Страница платформы не ответила на запрос доступа (vhr:access:get) — окно покупки недоступно.",
                        CurrentInternal()));
                }
                _changeSeq++;
                Store(host, notify: !CurrentInternal().SameAs(host));

                var pre = Precheck(host);
                if (pre != null) return LogResult(pre);

                // 2. Окно покупки на сайте.
                return LogResult(await PurchaseViaHostAsync(ct));
#endif
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                return LogResult(Fail(VhrAccessPurchaseStatus.Unavailable, "exception", e.Message));
            }
            finally
            {
                _purchaseInFlight = false;
            }
        }

        // Обычная игра / доступ уже полный — окно не открываем.
        private VhrAccessPurchaseResult Precheck(VhrAccessState st)
        {
            if (!st.IsPremium)
                return Fail(VhrAccessPurchaseStatus.Unavailable, "not_premium",
                    "Игра обычная (не премиум): покупать полную версию не нужно. Премиум-статус ставит администратор после заявки в кабинете.", st);
            if (st.IsFull)
                return Fail(VhrAccessPurchaseStatus.Unavailable, "already_full", "У игрока уже полный доступ.", st);
            return null;
        }

#if !UNITY_EDITOR
        private async Task<VhrAccessPurchaseResult> PurchaseViaHostAsync(CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<VhrAccessPurchaseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requestId = Guid.NewGuid().ToString("N");
            _log.Verbose($"Access: открываем окно покупки полной версии (requestId={requestId}).");

            int handle = VhrWebGlAccessChannel.Purchase(requestId, HostPurchaseTimeoutMs,
                (status, reason, payload) =>
                {
                    // Итог применяем прямо здесь: даже если игра уже отменила ожидание (ct),
                    // купленная игра откроется через AccessChanged.
                    tcs.TrySetResult(FromHost(status, reason, payload));
                });
            if (handle <= 0)
                return Fail(VhrAccessPurchaseStatus.Unavailable, "not_hosted",
                    "Не удалось связаться со страницей платформы для покупки.", CurrentInternal());

            var stop = new CancellationTokenSource();
            _ = VhrAsync.WatchdogAsync(HostPurchaseTimeoutMs / 1000f + 5f, stop.Token, () =>
                tcs.TrySetResult(Fail(VhrAccessPurchaseStatus.Unavailable, "host_timeout",
                    "Страница платформы не сообщила итог окна покупки.", CurrentInternal())));
            try
            {
                var r = await VhrAccessAwait.WithCancellation(tcs.Task, ct);
                // Хост не прислал состояние после покупки — спросим сервер.
                if ((r.Status == VhrAccessPurchaseStatus.Purchased || r.Status == VhrAccessPurchaseStatus.Subscribed) &&
                    !r.State.IsFull)
                {
                    var fresh = await GetAsync(ct);
                    if (!_lastNotified.SameAs(fresh)) Notify(fresh);
                    r.State = fresh;
                }
                return r;
            }
            finally
            {
                stop.Cancel();
                stop.Dispose();
            }
        }

        private VhrAccessPurchaseResult FromHost(int status, int reason, VhrWebGlAccessChannel.HostPayload p)
        {
            var st = Enum.IsDefined(typeof(VhrAccessPurchaseStatus), status)
                ? (VhrAccessPurchaseStatus)status
                : VhrAccessPurchaseStatus.Unavailable;
            var state = p?.ToState();
            if (state != null)
            {
                _changeSeq++;
                Store(state, notify: !CurrentInternal().SameAs(state));
            }

            var r = new VhrAccessPurchaseResult { Status = st, State = (state ?? CurrentInternal()).Clone() };
            switch (st)
            {
                case VhrAccessPurchaseStatus.Purchased:
                case VhrAccessPurchaseStatus.Subscribed:
                    // Источник правды — сервер (и токен перевыпущен хостом): перечитываем в фоне.
                    if (state != null) _ = RefreshQuietlyAsync();
                    break;
                case VhrAccessPurchaseStatus.Requested:
                    r.Message = "Отправлен запрос родителю. Когда он одобрит покупку, придёт AccessChanged.";
                    break;
                case VhrAccessPurchaseStatus.Cancelled:
                    r.ErrorCode = "cancelled";
                    r.Message = "Игрок закрыл окно покупки.";
                    break;
                default:
                    r.ErrorCode = !string.IsNullOrEmpty(p?.error) ? p.error
                        : reason == VhrWebGlAccessChannel.ReasonTimeout ? "host_timeout"
                        : reason == VhrWebGlAccessChannel.ReasonBusy ? "busy"
                        : reason == VhrWebGlAccessChannel.ReasonPostFailed ? "post_failed"
                        : "unavailable";
                    r.Message = reason == VhrWebGlAccessChannel.ReasonTimeout
                        ? "Страница платформы не сообщила итог окна покупки."
                        : "Платформа сообщила, что покупка сейчас недоступна.";
                    break;
            }
            return r;
        }
#endif

#if UNITY_EDITOR
        /// <summary>Unity Editor: диалог-имитация окна покупки платформы.</summary>
        private async Task<VhrAccessPurchaseResult> SimulatePurchaseAsync()
        {
            // Кадр паузы — как с настоящим сайтом.
            await Task.Yield();
            var st = CurrentInternal();
            var pre = Precheck(st);
            if (pre != null)
            {
                pre.IsTest = true;
                return pre;
            }

            var choice = VhrEditorSandbox.ShowAccessPurchaseDialog(st);
            VhrAccessPurchaseStatus status;
            switch (choice)
            {
                case 1:
                    VhrEditorSandbox.SimulateAccessPurchase(subscription: false);
                    status = VhrAccessPurchaseStatus.Purchased;
                    break;
                case 2:
                    VhrEditorSandbox.SimulateAccessPurchase(subscription: true);
                    status = VhrAccessPurchaseStatus.Subscribed;
                    break;
                default:
                    return new VhrAccessPurchaseResult
                    {
                        Status = VhrAccessPurchaseStatus.Cancelled, State = st.Clone(), IsTest = true,
                        ErrorCode = "cancelled", Message = "Симуляция: окно покупки закрыто."
                    };
            }
            // SimulateAccessPurchase уже прислал AccessChanged через OnSimAccessChanged.
            return new VhrAccessPurchaseResult
            {
                Status = status, State = CurrentInternal().Clone(), IsTest = true,
                Message = "Симуляция: полная версия открыта локально (монеты не тратились, сервер не вызывался)."
            };
        }

        private void OnSimAccessChanged()
        {
            if (_disposed) return;
            var st = VhrEditorSandbox.BuildSimAccessState();
            Store(st, notify: !CurrentInternal().SameAs(st));
        }
#endif

        // ------------------------------------------------------------ состояние

        private void Store(VhrAccessState st, bool notify)
        {
            if (st == null || _disposed) return;
            _cache = st.Clone();
#if !UNITY_EDITOR
            _cacheToken = SafeToken(_options);
#endif
            if (notify && !_lastNotified.SameAs(st)) Notify(st);
            else _lastNotified = st.Clone();
        }

        private void Notify(VhrAccessState st)
        {
            _lastNotified = st.Clone();
            _log.Verbose("Access: доступ изменился — " + st);
            try { AccessChanged?.Invoke(st.Clone()); }
            catch (Exception e) { _log.Error("Access: обработчик AccessChanged упал: " + e); }
        }

        private VhrAccessPurchaseResult Fail(VhrAccessPurchaseStatus status, string code, string message,
            VhrAccessState state = null) => new VhrAccessPurchaseResult
        {
            Status = status,
            ErrorCode = code,
            Message = message,
            State = (state ?? CurrentInternal()).Clone(),
#if UNITY_EDITOR
            IsTest = true
#endif
        };

        private VhrAccessPurchaseResult LogResult(VhrAccessPurchaseResult r)
        {
            if (r.Status == VhrAccessPurchaseStatus.Unavailable)
                _log.Warn($"Access: окно покупки недоступно — {r}. {r.Message}");
            else
                _log.Verbose("Access: " + r);
            return r;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
#if UNITY_EDITOR
            VhrEditorSandbox.SimAccessChanged -= OnSimAccessChanged;
#else
            VhrWebGlAccessChannel.HostAccessChanged -= OnHostAccessChanged;
#endif
            AccessChanged = null;
            if (ReferenceEquals(_shared, this)) _shared = null;
        }
    }

    /// <summary>Ожидание задачи с отменой через <see cref="CancellationToken"/> (WebGL-safe, без таймеров).</summary>
    internal static class VhrAccessAwait
    {
        // ⚠️ НЕ добавлять .ConfigureAwait(false): на WebGL нет тредпула (см. CHANGELOG 1.7.6).
        internal static async Task<T> WithCancellation<T>(Task<T> task, CancellationToken ct)
        {
            if (!ct.CanBeCanceled || task.IsCompleted) return await task;
            var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancelTcs.TrySetResult(true)))
            {
                var finished = await Task.WhenAny(task, cancelTcs.Task);
                if (finished != task) throw new OperationCanceledException(ct);
                return await task;
            }
        }
    }
}
