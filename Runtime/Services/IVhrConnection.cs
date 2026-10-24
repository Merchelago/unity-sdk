using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Связь с серверами VHR и очередь отложенных операций (SDK 1.12.0+): <see cref="VhrSdk.Connection"/>
    /// или внедрение через <see cref="VhrSdkLifetimeScope"/> (один общий экземпляр).
    /// <para>
    /// <b>Что не теряется при обрыве связи.</b> Если сервер недоступен (нет сети, таймаут, 5xx, 408,
    /// 429, истёкший токен), рекорды (<see cref="IVhrLeaderboard.SubmitAsync(string, long, CancellationToken)"/>),
    /// достижения (<see cref="IVhrAchievements.UnlockAsync"/>), турнирный счёт
    /// (<see cref="IVhrTournaments.SubmitAsync"/>) и завершение игровой сессии
    /// (<see cref="IVhrGameSessions.EndAsync"/>) не бросают исключение, а встают в эту очередь: она
    /// хранится в файле (<c>persistentDataPath/vhr-sdk/outbox-{gameId}.json</c>; на WebGL — IndexedDB
    /// браузера) и переживает закрытие вкладки и перезапуск игры. SDK сам отправит их при старте, при
    /// возвращении связи и во вкладку, и по таймеру с паузой 2 → 4 → … → 60 с. Все эти операции на
    /// сервере идемпотентны (лучший результат, ачивка один раз) — повтор безопасен. Сохранения
    /// (<see cref="IVhrSaves"/>) защищены отдельно: неотправленные слоты тоже лежат на диске.
    /// </para>
    /// <para>
    /// <b>Что только онлайн:</b> покупки и экономика (нужно подтверждение игрока), друзья, лобби,
    /// чтение таблиц. Дневные/недельные таблицы считают отложенный счёт по времени <b>доставки</b>.
    /// Операции отправляются от имени того игрока, который их сделал (claim <c>sub</c> токена):
    /// чужие ждут своего игрока. Неотправленное за 14 дней выбрасывается; в очереди — до 300
    /// операций (одинаковые схлопываются: ачивка — одна, рекорд — лучший и худший по таблице,
    /// турнир — максимум).
    /// </para>
    /// Вызывать с главного потока; события — на главном потоке.
    /// </summary>
    /// <example><code>
    /// VhrSdk.Connection.OnlineChanged += online => offlineBadge.SetActive(!online);
    /// VhrSdk.Connection.PendingChanged += n => pendingLabel.text = n > 0 ? $"Ждут отправки: {n}" : "";
    /// </code></example>
    public interface IVhrConnection
    {
        /// <summary>
        /// Есть ли связь с серверами VHR: последний запрос SDK получил ответ сервиса (даже ошибку), и
        /// браузер не сообщил об отключении сети. <c>false</c> — нет ответа (сеть, таймаут, 502/504) или
        /// браузер офлайн. До первого запроса — <c>true</c> (на WebGL — по <c>navigator.onLine</c>).
        /// </summary>
        bool IsOnline { get; }

        /// <summary>Изменился <see cref="IsOnline"/> (аргумент — новое значение). На главном потоке.</summary>
        event Action<bool> OnlineChanged;

        /// <summary>
        /// Сколько операций текущего игрока ждут отправки в очереди (рекорды, достижения, турнирный
        /// счёт, завершение сессии). Неотправленные сохранения сюда не входят — см.
        /// <see cref="IVhrSaves.HasPendingSaves"/>.
        /// </summary>
        int PendingCount { get; }

        /// <summary>Изменился <see cref="PendingCount"/> (аргумент — новое значение). На главном потоке.</summary>
        event Action<int> PendingChanged;

        /// <summary>
        /// Операция выброшена из очереди без доставки: сервер отклонил её окончательно (4xx: турнир
        /// закончился, таблица не создаётся — <c>board_limit</c>, ачивки нет…), она пролежала 14 дней
        /// или очередь переполнилась. Подробности — в <see cref="VhrDroppedOperation"/>; SDK
        /// дополнительно пишет предупреждение в консоль. На главном потоке.
        /// </summary>
        event Action<VhrDroppedOperation> OnDropped;

        /// <summary>
        /// Отправить очередь текущего игрока сейчас (паузы после сбоев сбрасываются) и дождаться —
        /// но не дольше 30 с реального времени: дальше отправка продолжится в фоне. Не бросает
        /// исключений. Сохранения не трогает — для них <see cref="IVhrSaves.FlushAsync"/>.
        /// </summary>
        Task FlushAsync();
    }

    /// <summary>Вид отложенной операции (<see cref="VhrDroppedOperation.Kind"/>).</summary>
    public enum VhrOutboxKind
    {
        /// <summary>Счёт в таблицу лидеров (<see cref="VhrDroppedOperation.Target"/> — id таблицы).</summary>
        LeaderboardScore = 0,

        /// <summary>Достижение (<see cref="VhrDroppedOperation.Target"/> — полный код <c>game.{gameId}.{slug}</c>).</summary>
        AchievementUnlock = 1,

        /// <summary>Результат в турнир (<see cref="VhrDroppedOperation.Target"/> — id турнира).</summary>
        TournamentScore = 2,

        /// <summary>Завершение игровой сессии (<see cref="VhrDroppedOperation.Target"/> — id сессии).</summary>
        SessionEnd = 3
    }

    /// <summary>Отложенная операция, выброшенная из очереди без доставки (<see cref="IVhrConnection.OnDropped"/>).</summary>
    public sealed class VhrDroppedOperation
    {
        internal VhrDroppedOperation(VhrOutboxKind kind, string target, long value, string reason, long httpStatus,
            string message, DateTime queuedAtUtc)
        {
            Kind = kind;
            Target = target ?? string.Empty;
            Value = value;
            Reason = reason ?? string.Empty;
            HttpStatus = httpStatus;
            Message = message ?? string.Empty;
            QueuedAtUtc = queuedAtUtc;
        }

        /// <summary>Что это было.</summary>
        public VhrOutboxKind Kind { get; }

        /// <summary>Id таблицы / код достижения / id турнира / id сессии.</summary>
        public string Target { get; }

        /// <summary>Счёт (для рекорда и турнира), иначе 0.</summary>
        public long Value { get; }

        /// <summary>
        /// Почему: <c>expired</c> — не доставлено за 14 дней; <c>limit</c> — очередь переполнена (300);
        /// иначе код сервера (<c>board_limit</c>, <c>bad_board</c>, <c>platform_achievement</c>,
        /// <c>game_mismatch</c>…) или <c>http_{статус}</c>, если кода нет.
        /// </summary>
        public string Reason { get; }

        /// <summary>HTTP-статус окончательного отказа; 0 — для <c>expired</c>/<c>limit</c>.</summary>
        public long HttpStatus { get; }

        /// <summary>Пояснение (сообщение сервера или SDK) — для логов, не для игрока.</summary>
        public string Message { get; }

        /// <summary>Когда операция встала в очередь (UTC).</summary>
        public DateTime QueuedAtUtc { get; }

        /// <inheritdoc />
        public override string ToString() =>
            $"VhrDroppedOperation({Kind} '{Target}'{(Value != 0 ? ", " + Value : "")}: {Reason}" +
            (HttpStatus > 0 ? $", HTTP {HttpStatus}" : "") + ")";
    }

    /// <summary>
    /// Состояние связи с серверами VHR (<see cref="IVhrConnection.IsOnline"/>): обновляется по
    /// каждому ответу <see cref="VhrApiClient"/> и по событиям сети браузера (WebGL). Главный поток.
    /// </summary>
    internal static class VhrConnectivity
    {
        private static bool _initialized;
        private static bool _online = true;

        /// <summary>Изменилось состояние связи.</summary>
        internal static event Action<bool> Changed;

        internal static bool IsOnline
        {
            get
            {
                EnsureInitialized();
                return _online;
            }
        }

        internal static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;
            VhrWebGlLifecycleChannel.EnsureInitialized();
            VhrWebGlLifecycleChannel.BrowserOnlineChanged += SetOnline;
            _online = VhrWebGlLifecycleChannel.IsBrowserOnline;
        }

        /// <summary>Итог HTTP-запроса: дошёл ли он до сервисов VHR (<see cref="VhrDeliveryRules.IsReachable"/>).</summary>
        internal static void ReportResponse(long status, string serverCode)
            => SetOnline(VhrDeliveryRules.IsReachable(status, serverCode));

        internal static void SetOnline(bool online)
        {
            EnsureInitialized();
            if (_online == online) return;
            _online = online;
            var handler = Changed;
            if (handler == null) return;
            try { handler(online); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    /// <summary>Итог «сначала онлайн, иначе в очередь» (<see cref="VhrOutbox.SendOrQueueAsync"/>).</summary>
    internal readonly struct VhrSendResult
    {
        internal static readonly VhrSendResult Enqueued = new VhrSendResult(true, null);

        /// <summary><c>true</c> — сервер недоступен, операция в очереди.</summary>
        internal readonly bool Queued;

        /// <summary>Тело успешного ответа (может быть <c>null</c>).</summary>
        internal readonly string Body;

        internal VhrSendResult(bool queued, string body)
        {
            Queued = queued;
            Body = body;
        }
    }

    /// <summary>
    /// Надёжная очередь отложенных операций (реализация <see cref="IVhrConnection"/>, SDK 1.12.0+).
    /// Один общий экземпляр на процесс (<see cref="GetOrCreate"/>): сервисы SDK, статический фасад и
    /// VContainer получают один и тот же объект — одна очередь, один файл, одни подписки.
    /// </summary>
    /// <remarks>
    /// Файл очереди переписывается атомарно после каждого изменения (<see cref="VhrAtomicFile"/>); на
    /// WebGL после записи — отложенная синхронизация IndexedDB и немедленная при скрытии/закрытии
    /// страницы (<see cref="VhrWebGlStorage"/>). Отправка — строго по одной операции, без тредпула:
    /// ожидания — по кадрам и реальному времени (<see cref="VhrAsync"/>), без <c>ConfigureAwait(false)</c>.
    /// </remarks>
    internal sealed class VhrOutbox : IVhrConnection, IDisposable
    {
        // Ожидание между проходами — короткими шагами, чтобы заметить токен, смену игрока и «пинки».
        private const float IdleStepSeconds = 2f;
        private const float FlushTimeoutSeconds = 30f;

        private static VhrOutbox _shared;

        private readonly VhrApiClient _api;
        private readonly VhrSdkOptions _options;
        private readonly IVhrLog _log;
        private readonly string _path; // null — очередь только в памяти (нет persistentDataPath)
        private readonly System.Random _rng = new System.Random();
        private VhrOutboxQueue _queue = new VhrOutboxQueue();

        private bool _disposed;
        private bool _pumpRunning;
        private CancellationTokenSource _pumpCts;
        private bool _kick;
        private Task _pass;

        // Общие паузы отправки (реальное время).
        private int _netFailures;
        private float _notBefore;       // после сбоя связи / 5xx
        private float _blockedUntil;    // после 429
        private string _authWaitToken;  // после 401: ждём токен, отличный от этого
        private float _authWaitUntil;

        private int _lastPending;
        private bool _transientWarned;
        private bool _queuedInfoShown;
        private bool _persistWarned;

        /// <inheritdoc />
        public event Action<bool> OnlineChanged;

        /// <inheritdoc />
        public event Action<int> PendingChanged;

        /// <inheritdoc />
        public event Action<VhrDroppedOperation> OnDropped;

        private VhrOutbox(VhrApiClient api, VhrSdkOptions options, IVhrLog log)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _log = log ?? new VhrUnityLog(options.VerboseLogging);

            var root = VhrWebGlStorage.TryGetRoot();
            if (root != null)
            {
                var game = VhrAtomicFile.SafeSegment((options.GameId ?? string.Empty).Trim().ToLowerInvariant());
                _path = Path.Combine(root, "outbox-" + game + ".json");
            }
            VhrWebGlStorage.EnsureInitialized();
            Load();
            _lastPending = PendingCount;

            VhrConnectivity.Changed += OnConnectivityChanged;
            VhrWebGlLifecycleChannel.PageHidden += OnPageHidden;
            VhrWebGlLifecycleChannel.PageVisible += OnPageVisible;
            VhrWebGlLifecycleChannel.BrowserOnlineChanged += OnBrowserOnlineChanged;
            Application.focusChanged += OnFocusChanged;
            Application.quitting += OnQuitting;
            VhrWebGlLifecycleChannel.EnsureInitialized();
            VhrConnectivity.EnsureInitialized();

            // При старте SDK: отправить накопленное, как только будут связь и токен игрока.
            if (_queue.Count > 0) EnsurePump();
        }

        /// <summary>
        /// Общий экземпляр на процесс. Первый вызов создаёт его (и читает файл очереди), последующие
        /// возвращают уже созданный.
        /// </summary>
        internal static VhrOutbox GetOrCreate(VhrApiClient api, VhrSdkOptions options, IVhrLog log)
        {
            if (_shared != null && !_shared._disposed) return _shared;
            _shared = new VhrOutbox(api, options, log);
            return _shared;
        }

        /// <summary>Сбрасывает общий экземпляр (файл очереди остаётся). Только для <see cref="VhrSdk.ResetForTests"/>.</summary>
        internal static void ResetShared()
        {
            _shared?.Dispose();
            _shared = null;
        }

        /// <inheritdoc />
        public bool IsOnline => VhrConnectivity.IsOnline;

        /// <inheritdoc />
        public int PendingCount => _queue.CountFor(CurrentUserId);

        private string CurrentUserId => _options.ResolveCurrentUserId();

        private static float Now => Time.realtimeSinceStartup;

        private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // ------------------------------------------------------------ для сервисов SDK

        /// <summary>
        /// «Сначала онлайн, иначе в очередь»: отправляет <c>POST</c> сразу (чтобы вернуть игре итог —
        /// рекорд, место, «открыто»), а при временной ошибке (<see cref="VhrDeliveryRules.IsTransient"/>)
        /// ставит операцию в очередь и возвращает <see cref="VhrSendResult.Enqueued"/> вместо исключения.
        /// Окончательные ошибки (4xx) бросаются как раньше (<see cref="VhrSdkException"/>), отмена
        /// <paramref name="ct"/> — <see cref="OperationCanceledException"/> (без постановки в очередь).
        /// Без игрока (гость, нет токена) в очередь ставится только <paramref name="allowAnonymous"/>-операция.
        /// </summary>
        /// <param name="kind">Вид (<see cref="VhrOutboxKinds"/>).</param>
        /// <param name="target">Цель: id таблицы / код ачивки / id турнира / id сессии.</param>
        /// <param name="value">Счёт (для схлопывания), иначе 0.</param>
        /// <param name="payloadFor">JSON-тело для счёта (схлопывание пересобирает тела с подсказками последней отправки).</param>
        /// <param name="allowAnonymous">Можно ли ставить в очередь без игрока (завершение сессии).</param>
        /// <param name="ct">Отмена вызывающим.</param>
        internal async Task<VhrSendResult> SendOrQueueAsync(
            string kind, string target, long value, Func<long, string> payloadFor, bool allowAnonymous, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var userId = CurrentUserId;
            bool canQueue = !_disposed && (userId.Length > 0 || allowAnonymous);
            var payload = payloadFor(value);

            // Браузер точно офлайн — не ждём таймаут запроса.
            if (canQueue && !VhrWebGlLifecycleChannel.IsBrowserOnline)
            {
                VhrConnectivity.SetOnline(false);
                Enqueue(kind, userId, target, value, payloadFor, payload, 0, "браузер офлайн");
                return VhrSendResult.Enqueued;
            }

            try
            {
                var body = await _api.SendRawAsync("POST", UrlFor(kind, target), new VhrRawJson(payload), ct: ct);
                NoteSuccess();
                return new VhrSendResult(false, body);
            }
            catch (VhrSdkException ex)
            {
                // Транспорт на отмену отвечает «canceled» (status 0) — отдаём честную отмену.
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                var d = VhrDeliveryRules.Classify(ex.HttpStatus, ex.ServerCode);
                if (!canQueue || !VhrDeliveryRules.IsTransient(d)) throw;

                NoteFailure(d, ex.RetryAfterSeconds);
                var next = d == VhrDelivery.RetryLater ? NowMs + VhrDeliveryRules.RetryLaterSeconds * 1000L : 0;
                Enqueue(kind, userId, target, value, payloadFor, payload, next, ReasonText(d, ex.HttpStatus, ex.ServerCode));
                return VhrSendResult.Enqueued;
            }
        }

        private void Enqueue(string kind, string userId, string target, long value, Func<long, string> payloadFor,
            string payload, long nextAttemptAt, string reason)
        {
            var item = new VhrOutboxItem
            {
                Id = VhrOutboxQueue.NewId(),
                Kind = kind,
                UserId = userId ?? string.Empty,
                CreatedAt = NowMs,
                NextAttemptAt = nextAttemptAt,
                Key = VhrOutboxQueue.KeyFor(kind, target),
                Target = target ?? string.Empty,
                Value = value,
                Payload = payload
            };
            var changed = _queue.Enqueue(item, payloadFor);
            var dropped = new List<VhrOutboxItem>();
            _queue.EnforceLimit(VhrDeliveryRules.MaxItems, dropped);
            if (changed || dropped.Count > 0) Persist();
            foreach (var d in dropped)
                RaiseDropped(d, "limit", 0, $"в очереди больше {VhrDeliveryRules.MaxItems} операций — выброшена самая старая");

            var message = $"Связь: сервер недоступен ({reason}) — {Describe(item)} в очереди, отправлю, когда связь вернётся.";
            if (_queuedInfoShown) _log.Verbose(message);
            else
            {
                // Одно сообщение на серию, дальше — только подробный лог.
                _queuedInfoShown = true;
                _log.Info(message);
            }
            NotifyPending();
            EnsurePump();
        }

        // ------------------------------------------------------------ IVhrConnection

        /// <inheritdoc />
        public async Task FlushAsync()
        {
            if (_disposed) return;
            Kick(true);

            // Жёсткий потолок ожидания: WebGL без тредпула — сторож по кадрам и реальному времени.
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stop = new CancellationTokenSource();
            _ = VhrAsync.WatchdogAsync(FlushTimeoutSeconds, stop.Token, () => done.TrySetResult(false));
            _ = FlushCoreAsync(done);
            var finished = await done.Task;
            stop.Cancel();
            if (!finished)
                _log.Verbose($"Связь: FlushAsync не уложился в {FlushTimeoutSeconds:0} с — отправка продолжится в фоне.");
        }

        private async Task FlushCoreAsync(TaskCompletionSource<bool> done)
        {
            try
            {
                // Идущий проход начат до «пинка» — дождаться его и пройти ещё раз без пауз.
                if (_pass != null && !_pass.IsCompleted) await _pass;
                await RunPassAsync();
            }
            catch (Exception e)
            {
                _log.Warn("Связь: FlushAsync — " + e.Message);
            }
            finally
            {
                done.TrySetResult(true);
            }
        }

        // ------------------------------------------------------------ отправка

        private void EnsurePump()
        {
            if (_pumpRunning || _disposed || _queue.Count == 0) return;
            _ = PumpAsync();
        }

        /// <summary>
        /// Цикл очереди: проход отправки → ожидание (до паузы после сбоя, срока операции или «пинка») →
        /// снова. Живёт, пока в очереди что-то есть (в том числе операции другого игрока — вдруг он
        /// войдёт); пустая очередь — выход, следующая постановка запустит снова.
        /// </summary>
        private async Task PumpAsync()
        {
            _pumpRunning = true;
            _pumpCts ??= new CancellationTokenSource();
            var ct = _pumpCts.Token;
            try
            {
                // Первый проход — со следующего кадра: при старте SDK игра успеет подписаться на
                // PendingChanged/OnDropped, а постановка из вызова не отправляется внутри него же.
                await Awaitable.NextFrameAsync(ct);
                while (!_disposed && !ct.IsCancellationRequested && _queue.Count > 0)
                {
                    await RunPassAsync();
                    if (_disposed || ct.IsCancellationRequested || _queue.Count == 0) break;
                    await WaitAsync(NextWakeSeconds(), ct);
                }
            }
            catch (OperationCanceledException)
            {
                // выход из игры / Dispose
            }
            catch (Exception e)
            {
                _log.Error("Связь: сбой цикла отправки очереди: " + e);
            }
            finally
            {
                _pumpRunning = false;
            }
        }

        // Один проход за раз: цикл и FlushAsync не шлют одну операцию параллельно.
        private Task RunPassAsync()
        {
            if (_pass != null && !_pass.IsCompleted) return _pass;
            _pass = PassAsync();
            return _pass;
        }

        /// <summary>
        /// Отправляет операции текущего игрока по одной (самые ранние первыми), пока они есть и пока
        /// не случился сбой связи / 429 / 401 — тогда остальное ждёт паузы.
        /// </summary>
        private async Task PassAsync()
        {
            PruneExpired();
            while (!_disposed && GateOpen())
            {
                var item = _queue.NextDue(CurrentUserId, NowMs);
                if (item == null) break;
                var d = await SendItemAsync(item);
                if (d == VhrDelivery.RetryNetwork || d == VhrDelivery.RetryRateLimited || d == VhrDelivery.RetryAuth) break;
            }
            NotifyPending();
        }

        private bool GateOpen()
        {
            var now = Now;
            if (now < _blockedUntil || now < _notBefore) return false;
            if (_authWaitToken != null)
            {
                // 401: ждём свежий токен (до 30 с), потом пробуем и с прежним.
                if (now < _authWaitUntil && string.Equals(CurrentToken(), _authWaitToken, StringComparison.Ordinal))
                    return false;
                _authWaitToken = null;
            }
            // Браузер точно офлайн — запросы бессмысленны; событие online «пнёт» очередь.
            return VhrWebGlLifecycleChannel.IsBrowserOnline;
        }

        private float NextWakeSeconds()
        {
            var now = Now;
            float wait = IdleStepSeconds;
            var gate = Mathf.Max(_notBefore, _blockedUntil);
            if (gate > now) wait = Mathf.Min(wait, gate - now);
            else
            {
                var due = _queue.EarliestDue(CurrentUserId);
                if (due.HasValue) wait = Mathf.Min(wait, Mathf.Max(0f, (due.Value - NowMs) / 1000f));
            }
            return Mathf.Max(0.05f, wait);
        }

        private async Task WaitAsync(float seconds, CancellationToken ct)
        {
            // По реальному времени (пауза игры и timeScale = 0 не тормозят очередь); «пинок» будит сразу.
            var end = Now + seconds;
            while (!_kick && Now < end)
            {
                ct.ThrowIfCancellationRequested();
                await Awaitable.NextFrameAsync(ct);
            }
            _kick = false;
        }

        private async Task<VhrDelivery> SendItemAsync(VhrOutboxItem item)
        {
            item.Attempts++;
            VhrDelivery d;
            long status = 0;
            string code = null, message = null, body = null;
            int retryAfter = 0;
            try
            {
                // Без отмены: начатый запрос доводим до ответа или таймаута транспорта.
                body = await _api.SendRawAsync("POST", UrlFor(item.Kind, item.Target), new VhrRawJson(item.Payload),
                    ct: CancellationToken.None);
                d = VhrDelivery.Delivered;
            }
            catch (VhrSdkException ex)
            {
                status = ex.HttpStatus;
                code = ex.ServerCode;
                retryAfter = ex.RetryAfterSeconds;
                message = ServerMessage(ex);
                d = VhrDeliveryRules.Classify(status, code);
            }
            catch (Exception ex)
            {
                message = ex.Message;
                d = VhrDelivery.RetryNetwork;
            }
            if (_disposed) return d;

            // Пока запрос летел, схлопывание могло заменить элемент новым (лучший счёт) — тогда
            // ответ относится к старому, а новый остаётся в очереди.
            bool present = _queue.Find(item.Id) != null;
            switch (d)
            {
                case VhrDelivery.Delivered:
                    NoteSuccess();
                    if (present && _queue.Remove(item.Id)) Persist();
                    _log.Verbose($"Связь: доставлено из очереди — {Describe(item)}{DescribeDelivered(item, body)}.");
                    break;

                case VhrDelivery.RetryLater:
                    if (present)
                    {
                        item.NextAttemptAt = NowMs + VhrDeliveryRules.RetryLaterSeconds * 1000L;
                        Persist();
                    }
                    _log.Verbose($"Связь: {Describe(item)} — сервер просит повторить позже ({code}: {message}); " +
                                 $"следующая попытка через {VhrDeliveryRules.RetryLaterSeconds / 60} мин или при следующем запуске.");
                    break;

                case VhrDelivery.Drop:
                    if (present && _queue.Remove(item.Id)) Persist();
                    RaiseDropped(item, string.IsNullOrEmpty(code) ? "http_" + status : code, status, message);
                    break;

                default: // сеть / 429 / 401 — повторим после паузы
                    NoteFailure(d, retryAfter);
                    if (present) Persist(); // счётчик попыток
                    WarnTransient($"Связь: {Describe(item)} пока не отправлено ({ReasonText(d, status, code)}) — " +
                                  $"данные в очереди, повтор через {PauseSeconds():0} с.");
                    break;
            }
            NotifyPending();
            return d;
        }

        private void NoteSuccess()
        {
            _netFailures = 0;
            _notBefore = 0f;
            _transientWarned = false;
            _queuedInfoShown = false;
        }

        private void NoteFailure(VhrDelivery d, int retryAfterSeconds)
        {
            var now = Now;
            switch (d)
            {
                case VhrDelivery.RetryNetwork:
                    _netFailures++;
                    _notBefore = now + (float)VhrDeliveryRules.BackoffSeconds(_netFailures, _rng.NextDouble());
                    break;
                case VhrDelivery.RetryRateLimited:
                    _blockedUntil = Mathf.Max(_blockedUntil, now + VhrDeliveryRules.RetryAfter(retryAfterSeconds));
                    break;
                case VhrDelivery.RetryAuth:
                    // VhrApiClient уже попросил свежий токен и подождал; ждём его появления фоном.
                    _authWaitToken = CurrentToken();
                    _authWaitUntil = now + VhrDeliveryRules.AuthWaitSeconds;
                    try { VhrWebGlTokenChannel.RequestRefresh(); }
                    catch { /* деградируем тихо */ }
                    break;
            }
        }

        private float PauseSeconds()
        {
            var now = Now;
            var gate = Mathf.Max(_notBefore, _blockedUntil);
            if (_authWaitToken != null) gate = Mathf.Max(gate, _authWaitUntil);
            return Mathf.Max(0f, gate - now);
        }

        /// <summary>«Пинок»: связь могла вернуться — отправить сейчас (пауза после 429 сохраняется).</summary>
        private void Kick(bool resetBackoff)
        {
            if (_disposed) return;
            if (resetBackoff)
            {
                _netFailures = 0;
                _notBefore = 0f;
            }
            _kick = true;
            EnsurePump();
        }

        private string CurrentToken()
        {
            try { return _options.TokenProvider?.Invoke() ?? string.Empty; }
            catch { return string.Empty; }
        }

        // ------------------------------------------------------------ состояние очереди

        private void Load()
        {
            if (_path == null) return;
            string json;
            try
            {
                if (!VhrAtomicFile.TryRead(_path, out json)) return;
            }
            catch (Exception e)
            {
                _log.Warn("Связь: не удалось прочитать файл очереди отложенных операций — начинаю с пустой очереди: " + e.Message);
                return;
            }

            if (!VhrOutboxQueue.TryDeserialize(json, out var q, out var skipped, out var error))
            {
                _log.Warn($"Связь: файл очереди отложенных операций повреждён ({error}) — начинаю с пустой очереди " +
                          $"(копия файла: {Path.GetFileName(_path)}.bad).");
                try { VhrAtomicFile.MoveAside(_path, _path + ".bad"); }
                catch
                {
                    try { VhrAtomicFile.Delete(_path); }
                    catch { /* перезапишется при следующей постановке */ }
                }
                return;
            }

            _queue = q;
            if (skipped > 0)
            {
                _log.Warn($"Связь: в файле очереди пропущено негодных записей: {skipped}.");
                Persist();
            }
            PruneExpired();
            var dropped = new List<VhrOutboxItem>();
            _queue.EnforceLimit(VhrDeliveryRules.MaxItems, dropped);
            if (dropped.Count > 0)
            {
                Persist();
                foreach (var d in dropped) RaiseDropped(d, "limit", 0, "очередь переполнена");
            }
            if (_queue.Count > 0)
                _log.Info($"Связь: с прошлых запусков ждут отправки операций: {_queue.Count} — отправлю, как только будут связь и токен игрока.");
        }

        private void PruneExpired()
        {
            var dropped = new List<VhrOutboxItem>();
            _queue.PruneExpired(NowMs, VhrDeliveryRules.TtlMs, dropped);
            if (dropped.Count == 0) return;
            Persist();
            foreach (var d in dropped)
                RaiseDropped(d, "expired", 0, $"не удалось отправить за {VhrDeliveryRules.TtlDays} дней");
            NotifyPending();
        }

        private void Persist()
        {
            if (_path == null) return;
            try
            {
                if (_queue.Count == 0) VhrAtomicFile.Delete(_path);
                else VhrAtomicFile.Write(_path, _queue.Serialize());
                _persistWarned = false;
            }
            catch (Exception e)
            {
                if (_persistWarned) return;
                _persistWarned = true;
                _log.Warn("Связь: не удалось записать файл очереди — до перезапуска игры операции живут только в памяти: " + e.Message);
            }
        }

        private void NotifyPending()
        {
            var n = PendingCount;
            if (n == _lastPending) return;
            _lastPending = n;
            try { PendingChanged?.Invoke(n); }
            catch (Exception e) { _log.Error("Связь: обработчик PendingChanged упал: " + e); }
        }

        private void RaiseDropped(VhrOutboxItem item, string reason, long status, string message)
        {
            DateTime queuedAt;
            try { queuedAt = DateTimeOffset.FromUnixTimeMilliseconds(item.CreatedAt).UtcDateTime; }
            catch (ArgumentOutOfRangeException) { queuedAt = DateTime.MinValue; }

            _log.Warn($"Связь: {Describe(item)} выброшено из очереди без доставки — {message} ({reason}).");
            var op = new VhrDroppedOperation(KindOf(item.Kind), item.Target, item.Value, reason, status, message, queuedAt);
            try { OnDropped?.Invoke(op); }
            catch (Exception e) { _log.Error("Связь: обработчик OnDropped упал: " + e); }
        }

        private void WarnTransient(string message)
        {
            // Одно предупреждение на серию сбоев, дальше — только подробный лог.
            if (_transientWarned) _log.Verbose(message);
            else
            {
                _transientWarned = true;
                _log.Warn(message);
            }
        }

        // ------------------------------------------------------------ жизненный цикл

        private void OnConnectivityChanged(bool online)
        {
            try { OnlineChanged?.Invoke(online); }
            catch (Exception e) { _log.Error("Связь: обработчик OnlineChanged упал: " + e); }
            // Любой запрос SDK снова дошёл до сервера — связь вернулась.
            if (online && _queue.Count > 0) Kick(true);
        }

        // Файл очереди пишется сразу при каждом изменении — осталось успеть сохранить его в IndexedDB.
        private void OnPageHidden(int code) => VhrWebGlStorage.RequestSync(true);

        private void OnPageVisible()
        {
            if (_queue.Count > 0) Kick(true);
        }

        private void OnBrowserOnlineChanged(bool online)
        {
            if (online && _queue.Count > 0) Kick(true);
        }

        private void OnFocusChanged(bool focused)
        {
            if (focused && _queue.Count > 0) Kick(true);
        }

        private void OnQuitting()
        {
            VhrWebGlStorage.RequestSync(true);
            // Начатый запрос доедет сам (если процесс успеет); очередь — в файле.
            _pumpCts?.Cancel();
            _pumpCts = null;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            VhrConnectivity.Changed -= OnConnectivityChanged;
            VhrWebGlLifecycleChannel.PageHidden -= OnPageHidden;
            VhrWebGlLifecycleChannel.PageVisible -= OnPageVisible;
            VhrWebGlLifecycleChannel.BrowserOnlineChanged -= OnBrowserOnlineChanged;
            Application.focusChanged -= OnFocusChanged;
            Application.quitting -= OnQuitting;
            _pumpCts?.Cancel();
            _pumpCts = null;
            OnlineChanged = null;
            PendingChanged = null;
            OnDropped = null;
            if (ReferenceEquals(_shared, this)) _shared = null;
        }

        // ------------------------------------------------------------ утилиты

        private string UrlFor(string kind, string target)
        {
            var games = _options.GamesBaseUrl;
            switch (kind)
            {
                case VhrOutboxKinds.LeaderboardScore: return games + "/api/Leaderboard/score";
                case VhrOutboxKinds.AchievementUnlock: return games + "/api/Achievements/unlock";
                case VhrOutboxKinds.TournamentScore: return games + "/api/Tournaments/" + Uri.EscapeDataString(target ?? string.Empty) + "/score";
                case VhrOutboxKinds.SessionEnd: return games + "/api/GameSessions/" + Uri.EscapeDataString(target ?? string.Empty) + "/end";
                default: throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный вид отложенной операции.");
            }
        }

        private static VhrOutboxKind KindOf(string kind)
        {
            switch (kind)
            {
                case VhrOutboxKinds.AchievementUnlock: return VhrOutboxKind.AchievementUnlock;
                case VhrOutboxKinds.TournamentScore: return VhrOutboxKind.TournamentScore;
                case VhrOutboxKinds.SessionEnd: return VhrOutboxKind.SessionEnd;
                default: return VhrOutboxKind.LeaderboardScore;
            }
        }

        private static string Describe(VhrOutboxItem it)
        {
            switch (it.Kind)
            {
                case VhrOutboxKinds.LeaderboardScore: return $"счёт {it.Value} в таблицу '{it.Target}'";
                case VhrOutboxKinds.AchievementUnlock: return $"достижение '{it.Target}'";
                case VhrOutboxKinds.TournamentScore: return $"результат {it.Value} в турнир {it.Target}";
                case VhrOutboxKinds.SessionEnd: return $"завершение игровой сессии {it.Target}";
                default: return it.Kind;
            }
        }

        private static string DescribeDelivered(VhrOutboxItem it, string body)
        {
            if (string.IsNullOrEmpty(body) || body.TrimStart().Length == 0 || body.TrimStart()[0] != '{') return string.Empty;
            switch (it.Kind)
            {
                case VhrOutboxKinds.LeaderboardScore:
                    var best = VhrJwt.ReadClaim(body, "best");
                    var improved = VhrJwt.ReadClaim(body, "improved");
                    return string.IsNullOrEmpty(best) ? string.Empty : $" (лучший {best}{(improved == "true" ? ", новый рекорд" : "")})";
                case VhrOutboxKinds.AchievementUnlock:
                    return VhrJwt.ReadClaim(body, "unlocked") == "true" ? " (открыто)" : " (уже было открыто)";
                default:
                    return string.Empty;
            }
        }

        private static string ReasonText(VhrDelivery d, long status, string code)
        {
            switch (d)
            {
                case VhrDelivery.RetryRateLimited: return "слишком частые запросы, 429";
                case VhrDelivery.RetryAuth: return "токен игрока истёк, 401 — жду свежий";
                case VhrDelivery.RetryLater: return "сервер просит позже: " + code;
                default: return status > 0 ? $"HTTP {status}" : "нет ответа";
            }
        }

        // Сообщение сервера из тела { message } / { Message }, иначе — HTTP-статус.
        private static string ServerMessage(VhrSdkException ex)
        {
            var body = ex.ResponseBody;
            if (!string.IsNullOrWhiteSpace(body) && body.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                var m = VhrJwt.ReadClaim(body, "message");
                if (string.IsNullOrWhiteSpace(m)) m = VhrJwt.ReadClaim(body, "Message");
                if (!string.IsNullOrWhiteSpace(m)) return m.Trim();
            }
            return ex.HttpStatus > 0 ? $"HTTP {ex.HttpStatus}" : ex.Message;
        }
    }
}
