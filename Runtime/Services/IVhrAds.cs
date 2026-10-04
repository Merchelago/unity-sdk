using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Реклама в игре (SDK 1.8.0+): полноэкранная <b>interstitial</b> и
    /// <b>rewarded</b> (за награду) от Рекламной сети Яндекса.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Как это работает.</b> Игра рекламу не рисует: SDK просит страницу VHR
    /// (родителя iframe) показать полноэкранный блок, страница показывает его
    /// поверх игры и возвращает результат. Перед показом приходит
    /// <see cref="OnAdOpened"/> (поставьте паузу, заглушите звук), после —
    /// <see cref="OnAdClosed"/>. Учёт показов и доход разработчика (10% дохода
    /// платформы от рекламы в его игре) ведёт сайт по факту реальной отрисовки;
    /// игре ничего репортить не нужно.
    /// </para>
    /// <para>
    /// <b>Награда за rewarded — ВНУТРИИГРОВАЯ.</b> SDK лишь сообщает, что просмотр
    /// засчитан (<see cref="VhrAdStatus.Rewarded"/>), а игра сама выдаёт свой
    /// предмет/жизнь/бонус. <b>Платформенные монеты (= реальные рубли) за рекламу
    /// не начисляются</b> — ни через этот API, ни через экономику.
    /// </para>
    /// <para>
    /// <b>Частота.</b> Interstitial — не чаще одного раза в N секунд, rewarded — не
    /// больше M в час (лимиты задаёт платформа; по умолчанию 90 с и 20/ч). При
    /// превышении — <see cref="VhrAdStatus.Cooldown"/>. Rewarded показывайте только
    /// по явному действию игрока (кнопка «Посмотреть рекламу за награду») — это
    /// требование Рекламной сети Яндекса.
    /// </para>
    /// <para>
    /// <b>Гарантии.</b> Методы не бросают исключений (кроме
    /// <see cref="OperationCanceledException"/> при отмене <c>ct</c>) и всегда
    /// завершаются: нет рекламы / адблок / детский аккаунт / игра открыта не на
    /// vhrgames.ru → <see cref="VhrAdStatus.Unavailable"/>. Одновременно идёт
    /// только один показ; повторный вызов во время показа → <see cref="VhrAdStatus.Cooldown"/>.
    /// </para>
    /// <para>
    /// <b>Редактор и не-WebGL.</b> Реклама симулируется: лог + результат через
    /// <see cref="VhrSdkOptions.AdsSimulationDelaySeconds"/> по режиму
    /// <see cref="VhrSdkOptions.AdsSimulation"/>.
    /// </para>
    /// <para>Вызывайте с главного потока Unity.</para>
    /// </remarks>
    /// <example><code>
    /// // Кнопка «Посмотреть рекламу за награду»
    /// var r = await VhrSdk.Ads.ShowRewardedAsync();
    /// if (r.IsRewarded) GiveExtraLife();          // своя внутриигровая награда
    ///
    /// // Между уровнями
    /// await VhrSdk.Ads.ShowInterstitialAsync();
    /// LoadNextLevel();
    /// </code></example>
    public interface IVhrAds
    {
        /// <summary>
        /// Есть ли смысл показывать рекламные кнопки: на WebGL — игра открыта во
        /// фрейме страницы VHR; в редакторе/не-WebGL — <c>true</c> (симуляция).
        /// Даже при <c>true</c> конкретный показ может вернуть <see cref="VhrAdStatus.Unavailable"/>.
        /// </summary>
        bool IsSupported { get; }

        /// <summary>Идёт ли сейчас показ (между запросом и результатом).</summary>
        bool IsShowing { get; }

        /// <summary>
        /// Встроенная авто-пауза: на время рекламы <c>Time.timeScale = 0</c>,
        /// <c>AudioListener.pause = true</c>, курсор разблокирован; после — всё
        /// возвращается как было. По умолчанию — значение
        /// <see cref="VhrSdkOptions.AdsAutoPause"/> (выключено). Если у игры своя
        /// пауза — оставьте выключенным и используйте <see cref="OnAdOpened"/>/<see cref="OnAdClosed"/>.
        /// </summary>
        bool AutoPause { get; set; }

        /// <summary>
        /// Реклама сейчас откроется поверх игры — поставьте паузу и заглушите звук.
        /// Приходит не для каждого вызова: при <see cref="VhrAdStatus.Unavailable"/> /
        /// <see cref="VhrAdStatus.Cooldown"/> до показа событие не возникает.
        /// </summary>
        event Action<VhrAdKind> OnAdOpened;

        /// <summary>
        /// Реклама закрыта — снимите паузу. Приходит ровно один раз после каждого
        /// <see cref="OnAdOpened"/> (парно), с итоговым результатом.
        /// </summary>
        event Action<VhrAdResult> OnAdClosed;

        /// <summary>
        /// Полноэкранная реклама в естественной паузе (между уровнями, после
        /// поражения). Награды нет: успешный исход — <see cref="VhrAdStatus.Closed"/>.
        /// </summary>
        Task<VhrAdResult> ShowInterstitialAsync(CancellationToken ct = default);

        /// <summary>
        /// Реклама за награду. Только по явному согласию игрока. Если вернулось
        /// <see cref="VhrAdStatus.Rewarded"/> — выдайте СВОЮ внутриигровую награду
        /// (платформенные монеты за рекламу не начисляются).
        /// </summary>
        Task<VhrAdResult> ShowRewardedAsync(CancellationToken ct = default);

        /// <summary>Колбэк-версия <see cref="ShowInterstitialAsync"/> для кода без async. Колбэк — на главном потоке.</summary>
        void ShowInterstitial(Action<VhrAdResult> onComplete);

        /// <summary>Колбэк-версия <see cref="ShowRewardedAsync"/> для кода без async. Колбэк — на главном потоке.</summary>
        void ShowRewarded(Action<VhrAdResult> onComplete);
    }

    /// <summary>
    /// Реализация <see cref="IVhrAds"/>: WebGL — через страницу-хост VHR
    /// (<see cref="VhrWebGlAdsChannel"/>), редактор/не-WebGL — симуляция.
    /// Один общий экземпляр на процесс (<see cref="GetOrCreate"/>): статический
    /// фасад и VContainer получают один и тот же объект, поэтому подписки на
    /// события работают при любом способе доступа.
    /// </summary>
    public sealed class VhrAdsService : IVhrAds, IDisposable
    {
        // Сколько ждём ПЕРВОГО ответа хоста (opened/result). Хост VHR отвечает
        // за миллисекунды (сам ждёт конфиг не дольше 3 с); тишина = хоста нет.
        private const int HostAckTimeoutMs = 5000;
        // Страховочный лимит на весь показ после opened (хост сам закрывает показ
        // максимум через 3 мин; это — на случай, если страница сломалась).
        private const int MaxShowMs = 300000;

        private static VhrAdsService _shared;

        private readonly VhrSdkOptions _options;
        private readonly IVhrLog _log;
        private readonly PauseState _pause = new PauseState();

        // Текущий показ. Один за раз.
        private string _activeRequestId;
        private VhrAdKind _activeKind;
        private TaskCompletionSource<VhrAdResult> _activeTcs;
        private bool _openedRaised;
        private bool _disposed;

        /// <inheritdoc />
        public event Action<VhrAdKind> OnAdOpened;

        /// <inheritdoc />
        public event Action<VhrAdResult> OnAdClosed;

        /// <summary>
        /// Создаёт сервис. Обычно не нужно — используйте <see cref="VhrSdk.Ads"/>
        /// или внедрение <see cref="IVhrAds"/> через <see cref="VhrSdkLifetimeScope"/>.
        /// </summary>
        public VhrAdsService(VhrSdkOptions options, IVhrLog log)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _log = log ?? new VhrUnityLog(options.VerboseLogging);
            AutoPause = options.AdsAutoPause;
        }

        /// <summary>
        /// Общий экземпляр на процесс. Первый вызов создаёт его из переданных
        /// опций, последующие возвращают уже созданный.
        /// </summary>
        public static VhrAdsService GetOrCreate(VhrSdkOptions options, IVhrLog log)
            => _shared ??= new VhrAdsService(options, log);

        /// <summary>Сбрасывает общий экземпляр. Только для тестов / <see cref="VhrSdk.ResetForTests"/>.</summary>
        internal static void ResetShared()
        {
            _shared?.Dispose();
            _shared = null;
        }

        /// <inheritdoc />
        public bool IsSupported => !VhrWebGlAdsChannel.IsSupported || VhrWebGlAdsChannel.IsHosted();

        /// <inheritdoc />
        public bool IsShowing => _activeRequestId != null;

        /// <inheritdoc />
        public bool AutoPause { get; set; }

        /// <inheritdoc />
        public Task<VhrAdResult> ShowInterstitialAsync(CancellationToken ct = default)
            => ShowAsync(VhrAdKind.Interstitial, ct);

        /// <inheritdoc />
        public Task<VhrAdResult> ShowRewardedAsync(CancellationToken ct = default)
            => ShowAsync(VhrAdKind.Rewarded, ct);

        /// <inheritdoc />
        public void ShowInterstitial(Action<VhrAdResult> onComplete)
            => _ = RunWithCallbackAsync(VhrAdKind.Interstitial, onComplete);

        /// <inheritdoc />
        public void ShowRewarded(Action<VhrAdResult> onComplete)
            => _ = RunWithCallbackAsync(VhrAdKind.Rewarded, onComplete);

        private async Task RunWithCallbackAsync(VhrAdKind kind, Action<VhrAdResult> onComplete)
        {
            VhrAdResult result;
            try
            {
                result = await ShowAsync(kind, CancellationToken.None);
            }
            catch (Exception e)
            {
                result = new VhrAdResult { Kind = kind, Status = VhrAdStatus.Error, Reason = e.Message };
            }

            try { onComplete?.Invoke(result); }
            catch (Exception e) { _log.Error("Ads: колбэк игры упал: " + e); }
        }

        // ⚠️ НЕ добавлять .ConfigureAwait(false): на WebGL нет тредпула, и
        // продолжение после TrySetResult не выполнится никогда (см. CHANGELOG 1.7.6).
        // RunContinuationsAsynchronously — продолжение игры выполнится на
        // следующем тике Unity, а не вложенно в JS-колбэк.
        private async Task<VhrAdResult> ShowAsync(VhrAdKind kind, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (_disposed)
                return new VhrAdResult { Kind = kind, Status = VhrAdStatus.Unavailable, Reason = "disposed" };
            if (_activeRequestId != null)
                return new VhrAdResult { Kind = kind, Status = VhrAdStatus.Cooldown, Reason = "busy" };

            var requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<VhrAdResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeRequestId = requestId;
            _activeKind = kind;
            _activeTcs = tcs;
            _openedRaised = false;

            _log.Verbose($"Ads: запрос {kind} (requestId={requestId}).");

            try
            {
                if (VhrWebGlAdsChannel.IsSupported)
                    StartWebGl(kind, requestId);
                else
                    _ = SimulateAsync(kind, requestId);
            }
            catch (Exception e)
            {
                Complete(requestId, VhrAdStatus.Error, "start_failed: " + e.Message);
            }

            if (!ct.CanBeCanceled)
                return await tcs.Task;

            // Отмена прекращает только ОЖИДАНИЕ: реклама могла уже открыться, и
            // жизненный цикл (OnAdClosed, снятие авто-паузы) доиграет сам.
            var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancelTcs.TrySetResult(true)))
            {
                var finished = await Task.WhenAny(tcs.Task, cancelTcs.Task);
                if (finished != tcs.Task)
                    throw new OperationCanceledException(ct);
                return await tcs.Task;
            }
        }

        private void StartWebGl(VhrAdKind kind, string requestId)
        {
            if (!VhrWebGlAdsChannel.IsHosted())
            {
                // Сборка открыта напрямую или на чужом сайте — рекламы VHR там нет.
                Complete(requestId, VhrAdStatus.Unavailable, "not_hosted");
                return;
            }

            var kindStr = kind == VhrAdKind.Rewarded ? "rewarded" : "interstitial";
            int handle = VhrWebGlAdsChannel.Show(kindStr, requestId, HostAckTimeoutMs, MaxShowMs,
                (eventCode, status, reason) => OnChannelEvent(requestId, eventCode, status, reason));
            if (handle <= 0)
                Complete(requestId, VhrAdStatus.Unavailable, "no_host");
        }

        private void OnChannelEvent(string requestId, int eventCode, int status, int reason)
        {
            if (eventCode == VhrWebGlAdsChannel.EventOpened)
            {
                RaiseOpened(requestId);
                return;
            }
            if (eventCode != VhrWebGlAdsChannel.EventResult) return;

            var st = Enum.IsDefined(typeof(VhrAdStatus), status) ? (VhrAdStatus)status : VhrAdStatus.Error;
            Complete(requestId, st, ReasonText(reason));
        }

        private static string ReasonText(int reason) => reason switch
        {
            VhrWebGlAdsChannel.ReasonHostTimeout => "host_timeout",
            VhrWebGlAdsChannel.ReasonShowTimeout => "show_timeout",
            VhrWebGlAdsChannel.ReasonNoHost => "no_host",
            VhrWebGlAdsChannel.ReasonBusy => "busy",
            VhrWebGlAdsChannel.ReasonHostReported => "host",
            _ => null
        };

        // Редактор / не-WebGL: честная имитация жизненного цикла, чтобы игру можно
        // было отладить без сайта. Задержка — по реальному времени (Time.realtimeSinceStartup),
        // т.к. при авто-паузе timeScale = 0 и масштабируемое время стоит.
        private async Task SimulateAsync(VhrAdKind kind, string requestId)
        {
            var mode = _options.AdsSimulation;
            _log.Info($"Ads: симуляция {kind} (редактор/не-WebGL), режим {mode}. " +
                      "Реальная реклама показывается только на vhrgames.ru.");

            switch (mode)
            {
                case VhrAdSimulationMode.Unavailable:
                    Complete(requestId, VhrAdStatus.Unavailable, "simulated");
                    return;
                case VhrAdSimulationMode.Cooldown:
                    Complete(requestId, VhrAdStatus.Cooldown, "simulated");
                    return;
                case VhrAdSimulationMode.Error:
                    Complete(requestId, VhrAdStatus.Error, "simulated");
                    return;
            }

            try
            {
                // Кадр, чтобы вызывающий успел подписаться/дождаться — как с реальным хостом.
                await Awaitable.NextFrameAsync();
                RaiseOpened(requestId);

                var delay = Mathf.Max(0f, _options.AdsSimulationDelaySeconds);
                var end = Time.realtimeSinceStartup + delay;
                while (Time.realtimeSinceStartup < end && _activeRequestId == requestId)
                    await Awaitable.NextFrameAsync();

                var status = kind == VhrAdKind.Rewarded && mode == VhrAdSimulationMode.Success
                    ? VhrAdStatus.Rewarded
                    : VhrAdStatus.Closed;
                Complete(requestId, status, "simulated");
            }
            catch (Exception e)
            {
                Complete(requestId, VhrAdStatus.Error, "simulation_failed: " + e.Message);
            }
        }

        private void RaiseOpened(string requestId)
        {
            if (requestId != _activeRequestId || _openedRaised) return;
            _openedRaised = true;
            _log.Verbose($"Ads: реклама открыта ({_activeKind}).");

            if (AutoPause) _pause.Pause();
            try { OnAdOpened?.Invoke(_activeKind); }
            catch (Exception e) { _log.Error("Ads: обработчик OnAdOpened упал: " + e); }
        }

        private void Complete(string requestId, VhrAdStatus status, string reason)
        {
            if (requestId == null || requestId != _activeRequestId) return;

            var result = new VhrAdResult { Kind = _activeKind, Status = status, RequestId = requestId, Reason = reason };
            var tcs = _activeTcs;
            var wasOpened = _openedRaised;

            _activeRequestId = null;
            _activeTcs = null;
            _openedRaised = false;

            if (wasOpened)
            {
                // Снимаем паузу всегда, даже если флаг AutoPause выключили во время показа.
                _pause.Resume();
                try { OnAdClosed?.Invoke(result); }
                catch (Exception e) { _log.Error("Ads: обработчик OnAdClosed упал: " + e); }
            }

            _log.Verbose("Ads: " + result);
            tcs?.TrySetResult(result);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_activeRequestId != null)
                Complete(_activeRequestId, VhrAdStatus.Error, "disposed");
            OnAdOpened = null;
            OnAdClosed = null;
        }

        /// <summary>
        /// Состояние встроенной авто-паузы: запоминает и восстанавливает
        /// timeScale, паузу звука и курсор. Идемпотентно.
        /// </summary>
        private sealed class PauseState
        {
            private bool _paused;
            private float _timeScale;
            private bool _audioPaused;
            private CursorLockMode _lockMode;
            private bool _cursorVisible;

            public void Pause()
            {
                if (_paused) return;
                _paused = true;
                _timeScale = Time.timeScale;
                _audioPaused = AudioListener.pause;
                _lockMode = Cursor.lockState;
                _cursorVisible = Cursor.visible;

                Time.timeScale = 0f;
                AudioListener.pause = true;
                // Иначе в шутерах (pointer lock) игрок не сможет нажать «Закрыть» на рекламе.
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            public void Resume()
            {
                if (!_paused) return;
                _paused = false;
                Time.timeScale = _timeScale;
                AudioListener.pause = _audioPaused;
                // На WebGL повторный захват курсора браузер разрешит только по клику игрока.
                Cursor.lockState = _lockMode;
                Cursor.visible = _cursorVisible;
            }
        }
    }
}
