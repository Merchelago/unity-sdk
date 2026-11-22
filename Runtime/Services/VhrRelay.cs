using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using P = VhrGames.Sdk.VhrRelayProtocol;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Высокоуровневый клиент <b>платформенного релея</b>: даёт мультиплеер
    /// <b>без серверной сборки</b>. Разработчик собирает только клиент, а сервером
    /// выступает общий relay платформы (<see cref="VhrSdkOptions.RelayBaseUrl"/>,
    /// по умолчанию <c>wss://servers.vhrweb.ru/ws</c>). Клиенты заходят в общую
    /// «комнату» и пересылают друг другу байты через relay.
    /// </summary>
    /// <remarks>
    /// Комната неймспейсится как <c>"{GameId}:{lobbyCode}"</c> (lobbyCode по
    /// умолчанию <c>"main"</c>), <c>GameId</c> берётся из
    /// <see cref="VhrSdkOptions.GameId"/> — игроки разных игр и разных лобби не
    /// пересекаются.
    /// <para>
    /// Бинарный протокол поверх WebSocket (первый байт — тип, целые
    /// <b>little-endian</b>):
    /// клиент→relay <c>0x01 join [u16 roomLen][utf8 room]</c>,
    /// <c>0x02 data [payload]</c>, <c>0x04 to [i32 targetPeerId][payload]</c>;
    /// relay→клиент <c>0x81 joined [i32 selfId][i32 count][i32×count peers]</c>,
    /// <c>0x83 data [i32 senderId][payload]</c>,
    /// <c>0x84 peerJoined [i32 peerId]</c>, <c>0x85 peerLeft [i32 peerId]</c>.
    /// </para>
    /// <para>
    /// <b>Протокол v2 (SDK 1.15, аддитивно).</b> Сразу после открытия сокета клиент шлёт
    /// <c>0x30 HELLO</c>; новый релей отвечает <c>0xB0 WELCOME</c> — тогда включаются пинг и
    /// серверное время (<see cref="Rtt"/>, <see cref="ServerTime"/>), явные каналы
    /// (<see cref="VhrChannel"/>), пачки за кадр, хост комнаты (<see cref="HostId"/>) и
    /// восстановление после обрыва (<see cref="OnReconnecting"/>/<see cref="OnReconnected"/>).
    /// Старый релей HELLO игнорирует — клиент работает как раньше (<see cref="ProtocolVersion"/> = 1).
    /// Спецификация — <c>back/docs/relay-protocol.md</c>.
    /// </para>
    /// <para>
    /// <b>Поток.</b> На нативе/в редакторе сообщения приходят из фоновой задачи
    /// сокета — relay маршалит все события на главный Unity-поток через
    /// <see cref="SynchronizationContext"/>, захваченный при создании (создавайте
    /// <see cref="VhrRelay"/> с главного потока, напр. через <c>VhrSdk.Relay</c>).
    /// В WebGL всё и так на главном потоке. Методы отправки вызывайте с главного потока.
    /// </para>
    /// </remarks>
    public sealed class VhrRelay : IDisposable
    {
        // Неподтверждённый пинг считается потерянным через столько микросекунд.
        private const long PingLostAfterUs = 3_000_000;
        // Предел одного сообщения пачки: WS — с запасом под лимит релея, DC — один UDP-пакет.
        private const int WsBatchBytes = 32 * 1024;
        private const int DcBatchBytes = 1150;

        // Коды ошибок/закрытия, после которых переподключаться бессмысленно.
        private static readonly string[] FatalCodes =
        {
            "game_unavailable", "capacity", "room_full", "game_mismatch", "game_required",
            "slow_consumer", "server_busy",
        };
        private static readonly string[] FatalCloseReasons =
        {
            "game_unavailable", "capacity", "room full", "rooms", "game_mismatch", "game_required",
            "slow_consumer", "server busy", "too many connections", "message too big",
        };

        private readonly VhrSdkOptions _options;
        private readonly Func<IVhrSocket> _socketFactory;
        private readonly SynchronizationContext _mainThread;

        private IVhrSocket _socket;
        private TaskCompletionSource<bool> _joinedTcs;
        private TaskCompletionSource<bool> _welcomeTcs;
        private string _room;
        // Последняя комната вида "{GameId}:code": релей пускает в «сырые» комнаты (матч лобби)
        // только соединение, уже допущенное в игру, — при новой сессии входим сначала сюда.
        private string _admitRoom;
        // Поколение сессии: ConnectAsync/CloseAsync/Dispose его меняют — цикл переподключения выходит.
        private int _sessionGen;

        // ---- v2 ----
        private volatile bool _v2;
        private volatile uint _caps;
        private volatile byte[] _resumeToken;
        private volatile bool _expectResume;
        private int _hostId;
        private readonly HashSet<int> _peers = new HashSet<int>(); // только главный поток

        // ---- закрытие/переподключение ----
        private volatile bool _userClosed;
        private volatile bool _fatal;
        private volatile bool _reconnecting;
        private volatile bool _suppressJoinEvents;
        private int _reconnects;

        // WebRTC-апгрейд (только WebGL). null/неактивен -> весь трафик по WS.
        private WebGLWebRtc _webRtc;
        private volatile bool _webRtcActive;

        // ---- пинг/часы ----
        private readonly VhrClockSync _clock = new VhrClockSync();
        private readonly object _pingLock = new object();
        private readonly PingSlot[] _pings = new PingSlot[64];
        private readonly Dictionary<uint, TaskCompletionSource<double>> _manualPings =
            new Dictionary<uint, TaskCompletionSource<double>>();
        private uint _pingSeq;
        private long _lastPingUs;

        private struct PingSlot
        {
            public uint Seq;
            public long SentUs;
            public byte State; // 0 — пусто, 1 — ждём, 2 — ответ, 3 — потерян
        }

        // ---- пачки ----
        private readonly object _batchLock = new object();
        private readonly VhrSendBatcher _wsBatch = new VhrSendBatcher(WsBatchBytes);
        private readonly VhrSendBatcher _dcBatch = new VhrSendBatcher(DcBatchBytes);
        private bool _pumpRegistered;

        // ---- статистика ----
        private long _bytesOut, _bytesIn, _msgsOut, _msgsIn;
        private long _statsWindowUs;
        private long _statsBytesOut, _statsBytesIn, _statsMsgsOut, _statsMsgsIn;
        private readonly VhrRelayStats _stats = new VhrRelayStats();

        /// <summary>
        /// Текущий транспорт игрового трафика: <c>"webrtc"</c> (DataChannel,
        /// низколатентный, апгрейд состоялся) или <c>"ws"</c> (WebSocket-релей —
        /// дефолт и fallback). Для диагностики. На нативе/в редакторе всегда
        /// <c>"ws"</c>. С 1.15 апгрейд идёт в фоне — транспорт может смениться после
        /// <see cref="ConnectAsync"/> (см. <see cref="OnTransportChanged"/>).
        /// </summary>
        public string Transport => _webRtcActive ? "webrtc" : "ws";

        /// <summary>Свой peer-id, выданный релеем после join. 0 до входа в комнату.</summary>
        public int SelfId { get; private set; }

        /// <summary>Подключён и вошёл в комнату.</summary>
        public bool IsConnected => _socket != null && _socket.IsOpen && SelfId != 0;

        /// <summary>Идёт восстановление соединения после обрыва (см. <see cref="OnReconnecting"/>).</summary>
        public bool IsReconnecting => _reconnecting;

        /// <summary>
        /// Версия протокола с релеем: <c>2</c> — новый релей (пинг, время, каналы, хост,
        /// резюм), <c>1</c> — старый релей (как SDK до 1.15), <c>0</c> — не подключены.
        /// </summary>
        public int ProtocolVersion => _v2 ? 2 : (SelfId != 0 ? 1 : 0);

        /// <summary>Регион релея из WELCOME (пусто, если релей его не сообщил).</summary>
        public string Region { get; private set; } = string.Empty;

        /// <summary>
        /// Peer-id хоста комнаты по версии релея: самый ранний из присутствующих; при уходе
        /// хоста релей назначает следующего (<see cref="OnHostChanged"/>). <c>0</c> — неизвестно
        /// (старый релей: договоритесь сами, например «хост = наименьший id», см. <see cref="GetPeers"/>).
        /// </summary>
        public int HostId => Volatile.Read(ref _hostId);

        /// <summary>Этот клиент — хост комнаты (<see cref="HostId"/> == <see cref="SelfId"/>).</summary>
        public bool IsHost => SelfId != 0 && HostId == SelfId;

        /// <summary>Сглаженный RTT до релея, мс (0 — ещё не измерен / старый релей).</summary>
        public double Rtt => _clock.RttMs;

        /// <summary>Последний замер RTT, мс.</summary>
        public double RttLast => _clock.LastRttMs;

        /// <summary>Джиттер RTT, мс.</summary>
        public double Jitter => _clock.JitterMs;

        /// <summary>Есть оценка серверного времени (релей v2 прислал WELCOME/PONG).</summary>
        public bool HasServerTime => _clock.HasOffset;

        /// <summary>
        /// Время сервера релея, микросекунды Unix (общая шкала для всех клиентов), монотонно.
        /// <c>0</c>, если оценки нет (старый релей).
        /// </summary>
        public long ServerTimeUs => _clock.ServerNowUs();

        /// <summary>
        /// Время сервера релея в секундах (Unix), одинаковое у всех клиентов с точностью до
        /// ~половины разброса RTT. Если релей старый (<see cref="HasServerTime"/> = false) —
        /// локальные монотонные секунды (у разных клиентов НЕ совпадают).
        /// </summary>
        public double ServerTime
        {
            get
            {
                long us = _clock.ServerNowUs();
                return (us != 0 ? us : VhrClockSync.LocalNowUs()) / 1_000_000.0;
            }
        }

        /// <summary>Сетевая статистика (обновляется раз в секунду; тот же объект).</summary>
        public VhrRelayStats Stats => _stats;

        /// <summary>Пришли данные от другого пира (<c>0x83</c>). На главном потоке.</summary>
        public event Action<int, byte[]> OnData;

        /// <summary>Вход в комнату подтверждён: свой id + уже присутствующие пиры (<c>0x81</c>).</summary>
        public event Action<int, int[]> OnJoined;

        /// <summary>В комнату вошёл новый пир (<c>0x84</c>).</summary>
        public event Action<int> OnPeerJoined;

        /// <summary>Пир покинул комнату (<c>0x85</c>).</summary>
        public event Action<int> OnPeerLeft;

        /// <summary>
        /// Соединение закрылось. Аргумент — причина (может быть <c>null</c>). При
        /// обрыве с включённым восстановлением срабатывает только если восстановить не удалось.
        /// </summary>
        public event Action<string> OnClosed;

        /// <summary>
        /// Управляющий фрейм, который сам <see cref="VhrRelay"/> НЕ обрабатывает
        /// (на сегодня — тип <c>0x20</c>, канал лобби/матчмейкинга). Аргументы —
        /// тип фрейма (первый байт) и его payload (без типа). На главном потоке.
        /// Используется <see cref="VhrLobby"/>, который ездит по тому же сокету.
        /// </summary>
        public event Action<byte, byte[]> OnControlFrame;

        /// <summary>Сменился хост комнаты (релей v2). Аргумент — peer-id нового хоста (0 — комната пуста).</summary>
        public event Action<int> OnHostChanged;

        /// <summary>Связь оборвалась, идёт восстановление (релей v2). Данные в это время не уходят.</summary>
        public event Action OnReconnecting;

        /// <summary>
        /// Связь восстановлена. <c>true</c> — сессия возобновлена: тот же <see cref="SelfId"/>,
        /// изменения состава пришли как <see cref="OnPeerJoined"/>/<see cref="OnPeerLeft"/>.
        /// <c>false</c> — новая сессия: перед этим пришёл обычный <see cref="OnJoined"/> с новым id —
        /// пересинхронизируйте состояние. Пакеты за время обрыва могли потеряться в обоих случаях.
        /// </summary>
        public event Action<bool> OnReconnected;

        /// <summary>Новый замер RTT (мс). На главном потоке.</summary>
        public event Action<double> OnRttUpdated;

        /// <summary>Сменился транспорт игровых данных (<c>"webrtc"</c>/<c>"ws"</c>).</summary>
        public event Action<string> OnTransportChanged;

        /// <summary>
        /// Создаёт relay-клиент. Вызывайте с главного потока (для корректного
        /// маршалинга событий нативного сокета).
        /// </summary>
        /// <param name="options">Конфигурация SDK (нужны <see cref="VhrSdkOptions.GameId"/>
        /// и <see cref="VhrSdkOptions.RelayBaseUrl"/>).</param>
        /// <param name="socketFactory">Необязательная фабрика сокета (тесты). По
        /// умолчанию выбирается платформенный сокет
        /// (<see cref="WebGLVhrSocket"/> в WebGL, иначе <see cref="NativeVhrSocket"/>).</param>
        public VhrRelay(VhrSdkOptions options, Func<IVhrSocket> socketFactory = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _socketFactory = socketFactory ?? DefaultSocketFactory;
            _mainThread = SynchronizationContext.Current;
        }

        private static IVhrSocket DefaultSocketFactory()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return new WebGLVhrSocket();
#else
            return new NativeVhrSocket();
#endif
        }

        /// <summary>Peer-id остальных участников комнаты (копия). Главный поток.</summary>
        public int[] GetPeers()
        {
            var a = new int[_peers.Count];
            _peers.CopyTo(a);
            return a;
        }

        /// <summary>
        /// Подключается к релею и входит в комнату <c>"{GameId}:{lobbyCode}"</c>.
        /// Завершается, когда релей подтвердил вход (<c>0x81 joined</c>);
        /// <see cref="OnJoined"/> при этом тоже срабатывает. Бросает
        /// <see cref="VhrSdkException"/> при сбое подключения. WebRTC-апгрейд (WebGL)
        /// начинается сразу и идёт в фоне — ConnectAsync его не ждёт.
        /// </summary>
        /// <param name="lobbyCode">Код лобби; одинаковый у игроков, которые должны
        /// оказаться вместе. По умолчанию <c>"main"</c>.</param>
        /// <param name="ct">Токен отмены.</param>
        public async Task ConnectAsync(string lobbyCode = "main", CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_options.GameId))
                throw new VhrSdkException("config_invalid", "VhrSdkOptions.GameId обязателен для релея.");
            var baseUrl = _options.RelayBaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new VhrSdkException("config_invalid", "VhrSdkOptions.RelayBaseUrl обязателен для релея.");

            var code = string.IsNullOrWhiteSpace(lobbyCode) ? "main" : lobbyCode.Trim();
            _room = $"{_options.GameId}:{code}";
            _admitRoom = _room;

            // Новая сессия: прерываем возможное переподключение и сбрасываем состояние v2.
            _sessionGen++;
            _reconnecting = false;
            _userClosed = false;
            _fatal = false;

            // Повторный вызов (реконнект после обрыва/таймаута, смена комнаты): прежний сокет
            // отцепляем и закрываем. Иначе он остаётся подписанным, и его поздний onclose
            // обнулит SelfId и уронит ожидания уже НОВОГО соединения (и лобби вместе с ним).
            DetachSocket();

            SelfId = 0;
            ResetProtocolState(clearResume: true);
            EnsurePump();

            var joinedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _joinedTcs = joinedTcs;

            var socket = AttachNewSocket();

            // Жёсткий таймаут на ВЕСЬ connect+join: через 10с — понятная ошибка вместо
            // бесконечного «Создание лобби…». Таймер — по кадрам (VhrAsync): CancelAfter на
            // WebGL не срабатывает (нет таймерного потока). Линкуем с внешним ct.
            const string timeoutMessage = "Релей не подтвердил вход за 10с (нет onopen или SJoined 0x81).";
            using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var ctReg = ct.CanBeCanceled ? ct.Register(() => joinedTcs.TrySetCanceled()) : default;
            var timedOut = false;
            var stop = new CancellationTokenSource();
            _ = VhrAsync.WatchdogAsync(10f, stop.Token, () =>
            {
                timedOut = true;
                joinedTcs.TrySetException(new VhrSdkException("relay_timeout", timeoutMessage));
                try { connCts.Cancel(); } catch (ObjectDisposedException) { /* уже завершились */ }
            });

            try
            {
                Debug.Log($"[VHR Relay] ConnectAsync → {baseUrl} (room={_room})");
                try
                {
                    await socket.ConnectAsync(baseUrl.Trim(), connCts.Token);
                }
                catch (OperationCanceledException) when (timedOut)
                {
                    throw new VhrSdkException("relay_timeout", timeoutMessage);
                }
                Debug.Log("[VHR Relay] сокет ОТКРЫТ → шлю hello+join");

                // v2: HELLO первым кадром, ответа не ждём (старый релей его просто проигнорирует).
                if (_options.RelayProtocolV2)
                    SendWsDirect(P.BuildHello(P.CapsAll, null));
                SendWsDirect(P.BuildJoin(_room));
                // WebRTC-апгрейд — сразу, параллельно с входом (без ожидания 0x81).
                BeginWebRtcUpgrade();
                Debug.Log("[VHR Relay] join отправлен → жду 0x81 SJoined…");

                await joinedTcs.Task;
            }
            finally
            {
                stop.Cancel();
            }
            Debug.Log($"[VHR Relay] JOINED self={SelfId} protocol=v{ProtocolVersion} ✓");
        }

        /// <summary>
        /// Рассылает <paramref name="data"/> всем пирам комнаты по каналу <see cref="VhrChannel.Auto"/>:
        /// по WebRTC DataChannel, если он поднят (<see cref="Transport"/> == <c>"webrtc"</c>), иначе по
        /// WebSocket. Поведение как до 1.15.
        /// </summary>
        public void Send(byte[] data) => SendData(false, 0, data, VhrChannel.Auto);

        /// <summary>
        /// Рассылает <paramref name="data"/> всем пирам комнаты по выбранному каналу.
        /// <code>VhrSdk.Relay.Send(snapshot, VhrChannel.Unreliable); VhrSdk.Relay.Send(hit, VhrChannel.Reliable);</code>
        /// </summary>
        public void Send(byte[] data, VhrChannel channel) => SendData(false, 0, data, channel);

        /// <summary>
        /// Шлёт <paramref name="data"/> конкретному пиру (<c>0x04</c>,
        /// little-endian id) по каналу <see cref="VhrChannel.Auto"/>.
        /// </summary>
        public void SendTo(int peerId, byte[] data) => SendData(true, peerId, data, VhrChannel.Auto);

        /// <summary>Шлёт <paramref name="data"/> конкретному пиру по выбранному каналу.</summary>
        public void SendTo(int peerId, byte[] data, VhrChannel channel) => SendData(true, peerId, data, channel);

        private void SendData(bool to, int peerId, byte[] data, VhrChannel channel)
        {
            if (data == null || data.Length == 0) return;

            var rtc = _webRtc;
            bool dcOpen = _webRtcActive && rtc != null && rtc.IsOpen;
            bool reliableCap = _v2 && (_caps & P.CapReliable) != 0;
            byte plain = to ? P.CTo : P.CData;
            byte reliable = reliableCap ? (to ? P.CToReliable : P.CDataReliable) : plain;

            bool viaDc;
            byte type;
            switch (channel)
            {
                case VhrChannel.Reliable:
                    viaDc = false;
                    type = reliable;
                    break;
                case VhrChannel.Unreliable:
                    viaDc = dcOpen;
                    type = plain;
                    break;
                default:
                    // Auto = как раньше: DC, если открыт; иначе WS, а WS всегда был надёжным —
                    // на релее v2 явно помечаем это 0x03/0x05 (иначе 0x02 стал бы «ненадёжным»).
                    viaDc = dcOpen;
                    type = dcOpen ? plain : reliable;
                    break;
            }

            var frame = P.BuildData(type, to, peerId, data);
            if (viaDc) EnqueueDc(frame);
            else EnqueueWs(frame);
        }

        /// <summary>
        /// Немедленно отправляет накопленные за кадр пачки (<see cref="VhrSdkOptions.RelayBatchSends"/>).
        /// Обычно не нужно: SDK делает это сам в конце каждого кадра.
        /// </summary>
        public void Flush() => FlushBatches();

        /// <summary>
        /// Разовый замер RTT до релея (мс). Требует релей v2; иначе <see cref="VhrSdkException"/>
        /// с кодом <c>relay_unsupported</c>. Таймаут 5 с — <c>relay_timeout</c>. Главный поток.
        /// </summary>
        public async Task<double> PingAsync(CancellationToken ct = default)
        {
            if (!IsConnected) throw new VhrSdkException("relay_closed", "Релей не подключён.");
            if (!_v2) throw new VhrSdkException("relay_unsupported", "Релей не поддерживает пинг (протокол v1).");
            var tcs = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            uint seq = SendPing(tcs);
            using var reg = ct.CanBeCanceled ? ct.Register(() => tcs.TrySetCanceled()) : default;
            var stop = new CancellationTokenSource();
            _ = VhrAsync.WatchdogAsync(5f, stop.Token, () =>
                tcs.TrySetException(new VhrSdkException("relay_timeout", "Релей не ответил на пинг за 5с.")));
            try
            {
                return await tcs.Task;
            }
            finally
            {
                stop.Cancel();
                lock (_pingLock) _manualPings.Remove(seq);
            }
        }

        /// <summary>Закрывает соединение с релеем. Безопасно вызывать повторно. Не переподключается.</summary>
        public async Task CloseAsync()
        {
            _userClosed = true;
            _sessionGen++;
            _reconnecting = false;
            try { FlushBatches(); } catch { /* ignore */ }
            var sock = _socket;
            if (sock == null) return;
            try { await sock.CloseAsync(); }
            catch { /* ignore */ }
        }

        /// <summary>
        /// Шлёт <b>сырой</b> фрейм по тому же сокету, что и релей (несущая для
        /// контроля). Используется <see cref="VhrLobby"/> для отправки
        /// управляющих <c>0x20</c>-фреймов лобби/матчмейкинга. No-op, если сокет
        /// закрыт. Игровой WebRTC-апгрейд этот путь не затрагивает — контроль
        /// всегда идёт по WebSocket.
        /// </summary>
        /// <param name="frame">Готовый фрейм с байтом типа в начале.</param>
        public void SendControl(byte[] frame)
        {
            if (frame == null || frame.Length == 0) return;
            SendWsDirect(frame);
        }

        /// <summary>
        /// Входит в комнату по <b>явному</b> идентификатору <paramref name="roomId"/>
        /// (как есть, без неймспейса <c>"{GameId}:..."</c>). Нужно, чтобы лобби
        /// после старта матча завело всех участников в общую relay-комнату
        /// (напр. <c>"match-xxxx"</c>) и игра сразу могла слать данные через
        /// <see cref="Send(byte[])"/>/<see cref="OnData"/>.
        /// <para>
        /// Требует уже открытого сокета (после <see cref="ConnectAsync"/>).
        /// Завершается по подтверждению входа (<c>0x81 joined</c>); при этом
        /// <see cref="OnJoined"/> срабатывает, а <see cref="SelfId"/> обновляется.
        /// </para>
        /// </summary>
        /// <param name="roomId">Точный id комнаты у релея.</param>
        /// <param name="ct">Токен отмены.</param>
        public async Task JoinRoomRawAsync(string roomId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(roomId))
                throw new VhrSdkException("config_invalid", "roomId обязателен для JoinRoomRawAsync.");
            var sock = _socket;
            if (sock == null || !sock.IsOpen)
                throw new VhrSdkException("relay_closed", "Сокет релея не открыт — сначала ConnectAsync.");

            _room = roomId.Trim();
            if (!string.IsNullOrEmpty(_options.GameId) && _room.StartsWith(_options.GameId + ":", StringComparison.Ordinal))
                _admitRoom = _room;
            SelfId = 0;
            _expectResume = false;
            Interlocked.Exchange(ref _hostId, 0);
            var joinedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _joinedTcs = joinedTcs;
            // Локальная ссылка + снятие регистрации: отмена старого ct не должна ронять
            // следующий вход. Плюс жёсткий таймаут (раньше ожидание 0x81 было бесконечным).
            using var ctReg = ct.CanBeCanceled ? ct.Register(() => joinedTcs.TrySetCanceled()) : default;
            var stop = new CancellationTokenSource();
            var room = _room;
            _ = VhrAsync.WatchdogAsync(10f, stop.Token, () => joinedTcs.TrySetException(
                new VhrSdkException("relay_timeout", $"Релей не подтвердил вход в комнату '{room}' за 10с.")));

            try
            {
                SendWsDirect(P.BuildJoin(_room));
                await joinedTcs.Task;
            }
            finally
            {
                stop.Cancel();
            }
        }

        // ================= внутреннее: сокет =================

        private IVhrSocket AttachNewSocket()
        {
            var socket = _socketFactory();
            _socket = socket;
            socket.OnMessage += HandleWsMessage;
            socket.OnClose += HandleCloseRaw;
            return socket;
        }

        private void ResetProtocolState(bool clearResume)
        {
            _v2 = false;
            _caps = 0;
            _expectResume = false;
            Interlocked.Exchange(ref _hostId, 0);
            if (clearResume) _resumeToken = null;
            _welcomeTcs = null;
        }

        private void EnsurePump()
        {
            if (_pumpRegistered) return;
            try { _pumpRegistered = VhrRelayPump.Register(this); }
            catch { _pumpRegistered = false; /* не с главного потока / нет плеера */ }
        }

        private bool BatchingActive =>
            _options.RelayBatchSends && _v2 && _pumpRegistered && VhrRelayPump.Available
            // Вне кадра (WebGL: обработчик события сокета между кадрами, FixedUpdate) — сразу,
            // иначе ответ ждал бы конца следующего кадра.
            && VhrRelayPump.InFrame;

        private void EnqueueWs(byte[] frame)
        {
            if (BatchingActive)
            {
                lock (_batchLock) _wsBatch.Add(frame);
                return;
            }
            RawWsSend(frame);
        }

        private void EnqueueDc(byte[] frame)
        {
            if (BatchingActive)
            {
                lock (_batchLock) _dcBatch.Add(frame);
                return;
            }
            RawDcSend(frame);
        }

        /// <summary>Отправка по WS в обход пачки, но после неё (порядок кадров сохраняется).</summary>
        private void SendWsDirect(byte[] frame)
        {
            lock (_batchLock)
            {
                _wsBatch.Flush(RawWsSend);
                RawWsSend(frame);
            }
        }

        private void FlushBatches()
        {
            lock (_batchLock)
            {
                _wsBatch.Flush(RawWsSend);
                _dcBatch.Flush(RawDcSend);
            }
        }

        private void RawWsSend(byte[] frame)
        {
            var sock = _socket;
            if (sock == null || !sock.IsOpen) return;
            sock.Send(frame);
            Interlocked.Add(ref _bytesOut, frame.Length);
            Interlocked.Increment(ref _msgsOut);
        }

        private void RawDcSend(byte[] frame)
        {
            var rtc = _webRtc;
            if (_webRtcActive && rtc != null && rtc.IsOpen)
            {
                rtc.Send(frame);
                Interlocked.Add(ref _bytesOut, frame.Length);
                Interlocked.Increment(ref _msgsOut);
                return;
            }
            // Канал закрылся до отправки — тот же кадр по WS.
            RawWsSend(frame);
        }

        // ================= приём =================

        private void HandleWsMessage(byte[] msg)
        {
            if (msg == null || msg.Length < 1) return;
            Interlocked.Add(ref _bytesIn, msg.Length);
            Interlocked.Increment(ref _msgsIn);
            HandleFrame(msg);
        }

        /// <summary>
        /// Игровой фрейм из WebRTC DataChannel. Релей шлёт сюда те же
        /// <c>0x83 data [i32 senderId][payload]</c>, что и по WebSocket (и PONG на пинг по
        /// DataChannel), поэтому парсим тем же путём (на WebGL это уже главный поток).
        /// </summary>
        private void OnDataChannelMessage(byte[] msg)
        {
            if (msg == null || msg.Length < 1) return;
            Interlocked.Add(ref _bytesIn, msg.Length);
            Interlocked.Increment(ref _msgsIn);
            HandleFrame(msg);
        }

        // Может быть на фоновом потоке нативного сокета: парсим здесь, события — на главном.
        private void HandleFrame(byte[] msg)
        {
            if (msg == null || msg.Length < 1) return;
            byte type = msg[0];

            switch (type)
            {
                case P.SBatch:
                {
                    foreach (var sub in P.DecodeBatch(msg))
                        if (sub.Length > 0 && sub[0] != P.SBatch) HandleFrame(sub);
                    break;
                }
                case P.SWelcome:
                {
                    if (!P.TryParseWelcome(msg, out var w)) return;
                    _caps = w.Caps;
                    _resumeToken = (w.Caps & P.CapResume) != 0 && w.ResumeToken != null && w.ResumeToken.Length > 0
                        ? w.ResumeToken : null;
                    Region = w.Region ?? string.Empty;
                    _clock.SetInitial(w.ServerTimeUs, VhrClockSync.LocalNowUs());
                    _expectResume = w.Resumed;
                    _v2 = true;
                    _lastPingUs = 0; // первый пинг — в ближайшем кадре
                    _welcomeTcs?.TrySetResult(w.Resumed);
                    break;
                }
                case P.SPong:
                {
                    if (!P.TryParsePong(msg, out uint seq, out long t0, out long serverUs)) return;
                    HandlePong(seq, t0, VhrClockSync.LocalNowUs(), serverUs);
                    break;
                }
                case P.SHost:
                {
                    if (msg.Length < 5) return;
                    int host = P.ReadI32(msg, 1);
                    int old = Interlocked.Exchange(ref _hostId, host);
                    if (old != host)
                        Post(() => { try { OnHostChanged?.Invoke(host); } catch { /* игра */ } });
                    break;
                }
                case P.SJoined:
                {
                    if (msg.Length < 9) return;
                    int self = P.ReadI32(msg, 1);
                    int count = P.ReadI32(msg, 5);
                    if (count < 0) count = 0;
                    count = Math.Min(count, (msg.Length - 9) / 4);
                    var peers = new int[count];
                    for (int i = 0, off = 9; i < count; i++, off += 4)
                        peers[i] = P.ReadI32(msg, off);

                    bool resume = _expectResume && SelfId == self && self != 0;
                    _expectResume = false;
                    bool suppress = _suppressJoinEvents;
                    SelfId = self;
                    Post(() => ApplyJoined(self, peers, resume, suppress));
                    _joinedTcs?.TrySetResult(true);
                    break;
                }
                case P.SData:
                {
                    if (msg.Length < 5) return;
                    int sender = P.ReadI32(msg, 1);
                    int payloadLen = msg.Length - 5;
                    var payload = new byte[payloadLen];
                    if (payloadLen > 0) Buffer.BlockCopy(msg, 5, payload, 0, payloadLen);
                    Post(() =>
                    {
                        try { OnData?.Invoke(sender, payload); } catch { /* игра */ }
                    });
                    break;
                }
                case P.SPeerJoined:
                {
                    if (msg.Length < 5) return;
                    int peer = P.ReadI32(msg, 1);
                    Post(() =>
                    {
                        _peers.Add(peer);
                        try { OnPeerJoined?.Invoke(peer); } catch { /* игра */ }
                    });
                    break;
                }
                case P.SPeerLeft:
                {
                    if (msg.Length < 5) return;
                    int peer = P.ReadI32(msg, 1);
                    Post(() =>
                    {
                        _peers.Remove(peer);
                        try { OnPeerLeft?.Invoke(peer); } catch { /* игра */ }
                    });
                    break;
                }
                case P.Signal:
                {
                    // WebRTC-сигналинг от релея: [0x10][utf8 JSON]. Маршалим на
                    // главный поток — WebRTC API (WebGL) трогаем только оттуда.
                    if (msg.Length < 2) return;
                    var json = Encoding.UTF8.GetString(msg, 1, msg.Length - 1);
                    Post(() => RouteSignal(json));
                    break;
                }
                case P.Control:
                {
                    // Управляющий канал лобби/матчмейкинга ([0x20][utf8 JSON]).
                    // VhrRelay сам его не интерпретирует — пробрасывает payload
                    // (без байта типа) наружу на главном потоке. Подписчик —
                    // VhrLobby. Пустой payload допустим (передаём пустой массив).
                    int ctrlLen = msg.Length - 1;
                    var ctrlPayload = new byte[ctrlLen];
                    if (ctrlLen > 0) Buffer.BlockCopy(msg, 1, ctrlPayload, 0, ctrlLen);
                    NoteFatalError(ctrlPayload);
                    Post(() =>
                    {
                        try { OnControlFrame?.Invoke(type, ctrlPayload); } catch { /* подписчик */ }
                    });
                    break;
                }
                default:
                    // Неизвестный тип — игнорируем (forward-compat).
                    break;
            }
        }

        /// <summary>Применяет 0x81 на главном потоке: обычный вход, вход-«допуск» или резюм.</summary>
        private void ApplyJoined(int self, int[] peers, bool resume, bool suppress)
        {
            if (resume)
            {
                // Сессия возобновлена: тот же id; сообщаем только реальные изменения состава.
                var now = new HashSet<int>(peers);
                var left = new List<int>();
                foreach (var p in _peers) if (!now.Contains(p)) left.Add(p);
                foreach (var p in left)
                {
                    _peers.Remove(p);
                    try { OnPeerLeft?.Invoke(p); } catch { /* игра */ }
                }
                foreach (var p in peers)
                {
                    if (!_peers.Add(p)) continue;
                    try { OnPeerJoined?.Invoke(p); } catch { /* игра */ }
                }
                return;
            }

            _peers.Clear();
            foreach (var p in peers) _peers.Add(p);
            if (suppress) return; // промежуточный вход в комнату-«допуск» при переподключении
            try { OnJoined?.Invoke(self, peers); } catch { /* игра */ }
        }

        private void NoteFatalError(byte[] json)
        {
            if (json == null || json.Length < 10) return;
            string s;
            try { s = Encoding.UTF8.GetString(json); }
            catch { return; }
            if (s.IndexOf("\"error\"", StringComparison.Ordinal) < 0) return;
            var code = ExtractJsonString(s, "code");
            if (string.IsNullOrEmpty(code)) return;
            foreach (var f in FatalCodes)
                if (code == f) { _fatal = true; return; }
        }

        private static bool IsFatalReason(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return false;
            foreach (var f in FatalCloseReasons)
                if (reason.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private void HandleCloseRaw(string reason)
        {
            // Сокет — несущая для сигналинга и контроля; его закрытие рвёт и
            // WebRTC-апгрейд. Сворачиваем канал и возвращаемся к "ws".
            DisposeRtc();

            // Сокет попытки переподключения закрылся — пусть цикл попробует ещё раз.
            if (_reconnecting)
            {
                var ex = new VhrSdkException("relay_closed", $"Соединение с релеем закрылось: {reason}");
                _welcomeTcs?.TrySetException(ex);
                _joinedTcs?.TrySetException(ex);
                return;
            }

            bool canResume = !_userClosed && !_fatal && _options.RelayAutoReconnect
                             && _v2 && (_caps & P.CapResume) != 0 && _resumeToken != null
                             && SelfId != 0 && !IsFatalReason(reason);
            if (canResume)
            {
                _reconnecting = true;
                int gen = _sessionGen;
                Debug.Log($"[VHR Relay] связь оборвалась ({reason ?? "без причины"}) → восстанавливаю…");
                Post(() =>
                {
                    try { OnReconnecting?.Invoke(); } catch { /* игра */ }
                    _ = ReconnectLoopAsync(gen, reason);
                });
                return;
            }

            // После закрытия мы больше не "подключены": IsConnected = (socket open
            // && SelfId!=0) должен стать false ОДНОЗНАЧНО, чтобы следующий вызов
            // сделал полный реконнект + повторный auth (иначе кадр уйдёт в никуда).
            SelfId = 0;
            Interlocked.Exchange(ref _hostId, 0);

            // Если закрылись до join — разблокируем ConnectAsync ошибкой.
            _joinedTcs?.TrySetException(
                new VhrSdkException("relay_closed", $"Соединение с релеем закрылось: {reason}"));
            Post(() =>
            {
                try { OnClosed?.Invoke(reason); } catch { /* игра */ }
            });
        }

        // ================= переподключение (главный поток) =================

        private async Task ReconnectLoopAsync(int gen, string reason)
        {
            float window = Mathf.Max(1f, _options.RelayReconnectWindowSeconds);
            float start = Time.realtimeSinceStartup;
            float delay = 0.25f;
            while (true)
            {
                if (gen != _sessionGen || _userClosed) return; // сессию заменили/закрыли
                if (Time.realtimeSinceStartup - start >= window) break;
                try { await VhrAsync.DelayRealtimeAsync(delay); }
                catch (OperationCanceledException) { return; }
                delay = Mathf.Min(delay * 2f, 2f);
                if (gen != _sessionGen || _userClosed) return;
                float remaining = window - (Time.realtimeSinceStartup - start);
                if (remaining <= 0.05f) break;

                bool resumed;
                try
                {
                    resumed = await AttemptReconnectAsync(Mathf.Min(remaining, 5f));
                }
                catch (Exception e)
                {
                    if (gen != _sessionGen || _userClosed) return;
                    if (_fatal) break;
                    Debug.Log($"[VHR Relay] попытка восстановления не удалась: {e.Message}");
                    continue;
                }

                if (gen != _sessionGen || _userClosed) return;
                _reconnecting = false;
                _reconnects++;
                Debug.Log($"[VHR Relay] связь восстановлена ({(resumed ? "сессия возобновлена" : "новая сессия")}), self={SelfId}");
                try { OnReconnected?.Invoke(resumed); } catch { /* игра */ }
                BeginWebRtcUpgrade();
                return;
            }

            if (gen != _sessionGen || _userClosed) return;
            _reconnecting = false;
            DetachSocket();
            SelfId = 0;
            Interlocked.Exchange(ref _hostId, 0);
            _peers.Clear();
            Debug.Log("[VHR Relay] восстановить связь не удалось → закрыто");
            try { OnClosed?.Invoke(reason ?? "reconnect_failed"); } catch { /* игра */ }
        }

        /// <summary>Одна попытка: новый сокет → HELLO с токеном → резюм или новый вход в ту же комнату.</summary>
        private async Task<bool> AttemptReconnectAsync(float timeoutSeconds)
        {
            DetachSocket();
            var token = _resumeToken;
            ResetProtocolState(clearResume: false);

            var welcomeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var joinedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _welcomeTcs = welcomeTcs;
            _joinedTcs = joinedTcs;

            var socket = AttachNewSocket();
            using var connCts = new CancellationTokenSource();
            var stop = new CancellationTokenSource();
            _ = VhrAsync.WatchdogAsync(timeoutSeconds, stop.Token, () =>
            {
                var ex = new VhrSdkException("relay_timeout", "Релей не ответил при восстановлении связи.");
                _welcomeTcs?.TrySetException(ex);
                _joinedTcs?.TrySetException(ex);
                try { connCts.Cancel(); } catch (ObjectDisposedException) { }
            });

            try
            {
                await socket.ConnectAsync(_options.RelayBaseUrl.Trim(), connCts.Token);
                SendWsDirect(P.BuildHello(P.CapsAll, token));
                // JOIN нельзя слать до WELCOME: при удачном резюме он перевёл бы нас в комнату
                // заново, и остальные увидели бы выход+вход.
                bool resumed = await welcomeTcs.Task;
                if (!resumed)
                {
                    SelfId = 0;
                    if (!string.IsNullOrEmpty(_admitRoom) && _admitRoom != _room)
                    {
                        _suppressJoinEvents = true;
                        try
                        {
                            SendWsDirect(P.BuildJoin(_admitRoom));
                            await joinedTcs.Task;
                        }
                        finally { _suppressJoinEvents = false; }
                        joinedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _joinedTcs = joinedTcs;
                    }
                    SendWsDirect(P.BuildJoin(_room));
                }
                await joinedTcs.Task;
                return resumed;
            }
            finally
            {
                stop.Cancel();
                _welcomeTcs = null;
            }
        }

        // ================= пинг =================

        private uint SendPing(TaskCompletionSource<double> manual)
        {
            long t0 = VhrClockSync.LocalNowUs();
            uint seq;
            lock (_pingLock)
            {
                seq = ++_pingSeq;
                ref var slot = ref _pings[seq % (uint)_pings.Length];
                slot.Seq = seq;
                slot.SentUs = t0;
                slot.State = 1;
                if (manual != null) _manualPings[seq] = manual;
            }
            _lastPingUs = t0;
            var frame = P.BuildPing(seq, t0);
            // По DataChannel, если он поднят: RTT и потери — те же, что у игровых данных.
            var rtc = _webRtc;
            if (_webRtcActive && rtc != null && rtc.IsOpen) RawDcSend(frame);
            else SendWsDirect(frame);
            return seq;
        }

        private void HandlePong(uint seq, long t0, long t1, long serverUs)
        {
            TaskCompletionSource<double> manual = null;
            lock (_pingLock)
            {
                ref var slot = ref _pings[seq % (uint)_pings.Length];
                if (slot.Seq != seq || slot.State == 2) return; // чужой/повтор
                slot.State = 2;
                if (_manualPings.TryGetValue(seq, out manual)) _manualPings.Remove(seq);
            }
            long rttUs = _clock.AddSample(t0, t1, serverUs);
            if (rttUs < 0) return;
            double ms = rttUs / 1000.0;
            manual?.TrySetResult(ms);
            Post(() => { try { OnRttUpdated?.Invoke(ms); } catch { /* игра */ } });
        }

        private void ExpirePings(long nowUs)
        {
            lock (_pingLock)
            {
                for (int i = 0; i < _pings.Length; i++)
                    if (_pings[i].State == 1 && nowUs - _pings[i].SentUs > PingLostAfterUs)
                        _pings[i].State = 3;
            }
        }

        private double PingLossPercent()
        {
            lock (_pingLock)
            {
                int lost = 0, total = 0;
                uint newest = _pingSeq;
                for (int i = 0; i < _pings.Length; i++)
                {
                    var s = _pings[i];
                    if (s.State < 2 || newest - s.Seq >= 20) continue;
                    total++;
                    if (s.State == 3) lost++;
                }
                return total == 0 ? 0 : 100.0 * lost / total;
            }
        }

        // ================= кадр (VhrRelayPump, главный поток) =================

        internal void Tick()
        {
            // DataChannel закрылся после апгрейда — обратно на WS.
            var rtc = _webRtc;
            if (_webRtcActive && (rtc == null || !rtc.IsOpen))
            {
                DisposeRtc();
                Debug.Log("[VHR Relay] WebRTC DataChannel закрылся → транспорт WS");
                RaiseTransportChanged();
            }

            FlushBatches();

            long now = VhrClockSync.LocalNowUs();
            float interval = _options.RelayPingIntervalSeconds;
            if (_v2 && (_caps & P.CapPing) != 0 && interval > 0f && IsConnected && !_reconnecting
                && now - _lastPingUs >= (long)(interval * 1_000_000))
                SendPing(null);
            ExpirePings(now);
            UpdateStats(now);
        }

        private void UpdateStats(long nowUs)
        {
            if (_statsWindowUs == 0) { _statsWindowUs = nowUs; return; }
            long span = nowUs - _statsWindowUs;
            if (span < 1_000_000) return;
            double sec = span / 1_000_000.0;
            long bo = Interlocked.Read(ref _bytesOut), bi = Interlocked.Read(ref _bytesIn);
            long mo = Interlocked.Read(ref _msgsOut), mi = Interlocked.Read(ref _msgsIn);
            _stats.BytesSentPerSec = (bo - _statsBytesOut) / sec;
            _stats.BytesReceivedPerSec = (bi - _statsBytesIn) / sec;
            _stats.MessagesSentPerSec = (mo - _statsMsgsOut) / sec;
            _stats.MessagesReceivedPerSec = (mi - _statsMsgsIn) / sec;
            _stats.TotalBytesSent = bo;
            _stats.TotalBytesReceived = bi;
            _statsBytesOut = bo; _statsBytesIn = bi; _statsMsgsOut = mo; _statsMsgsIn = mi;
            _statsWindowUs = nowUs;
            _stats.RttMs = _clock.RttMs;
            _stats.RttLastMs = _clock.LastRttMs;
            _stats.JitterMs = _clock.JitterMs;
            _stats.PingLossPercent = PingLossPercent();
            _stats.Transport = Transport;
            _stats.ProtocolVersion = ProtocolVersion;
            _stats.Region = Region;
            _stats.Reconnects = _reconnects;
        }

        // ================= WebRTC (только WebGL) =================

        /// <summary>
        /// На WebGL в фоне поднимает WebRTC DataChannel поверх открытого сокета: шлёт
        /// <c>0x10 {"t":"webrtc-start"}</c>, отвечает на offer релея, трикл-ICE'ит и при
        /// открытии канала переключает игровой трафик на него. Не-WebGL и
        /// <c>PreferWebRtc=false</c> — no-op. Никогда не бросает: апгрейд best-effort.
        /// </summary>
        private void BeginWebRtcUpgrade()
        {
            if (_options.PreferWebRtc != true) return;
            if (!WebGLWebRtc.IsSupported) return; // натив/редактор -> WS
            var sock = _socket;
            if (sock == null || !sock.IsOpen) return;
            _ = UpgradeWebRtcAsync(sock);
        }

        private async Task UpgradeWebRtcAsync(IVhrSocket sock)
        {
            WebGLWebRtc rtc = null;
            try
            {
                DisposeRtc();
                rtc = new WebGLWebRtc();
                _webRtc = rtc;

                // Локальные answer/ICE релеим релею как 0x10 по тому же сокету.
                rtc.OnLocalAnswer += sdp => SendSignal(BuildAnswerJson(sdp));
                rtc.OnLocalIce += candidateJson => SendSignal(BuildIceJson(candidateJson));
                // Игровые данные из DataChannel поднимаем как обычный 0x83-парс.
                rtc.OnData += OnDataChannelMessage;

                float timeout = _options.WebRtcTimeoutSeconds > 0f ? _options.WebRtcTimeoutSeconds : 4f;
                // Peer заводится синхронно внутри StartAsync — offer релея будет куда принять.
                var openTask = rtc.StartAsync(TimeSpan.FromSeconds(timeout));
                SendSignal("{\"t\":\"webrtc-start\"}");
                bool ok = await openTask;

                if (_webRtc != rtc || _socket != sock)
                {
                    // Пока ждали — сокет/апгрейд сменились.
                    try { rtc.Dispose(); } catch { /* ignore */ }
                    return;
                }
                if (ok)
                {
                    _webRtcActive = true; // с этого момента Unreliable/Auto идут по каналу
                    Debug.Log("[VHR Relay] WebRTC DataChannel открыт → транспорт webrtc");
                    RaiseTransportChanged();
                }
                else
                {
                    _webRtc = null;
                    try { rtc.Dispose(); } catch { /* ignore */ }
                    Debug.Log("[VHR Relay] WebRTC не поднялся → остаёмся на WS");
                }
            }
            catch
            {
                // Любой сбой -> тихо остаёмся на WebSocket.
                if (_webRtc == rtc) { _webRtcActive = false; _webRtc = null; }
                try { rtc?.Dispose(); } catch { /* ignore */ }
            }
        }

        private void DisposeRtc()
        {
            var rtc = _webRtc;
            _webRtc = null;
            _webRtcActive = false;
            if (rtc != null)
            {
                try { rtc.Dispose(); } catch { /* ignore */ }
            }
        }

        private void RaiseTransportChanged()
        {
            var t = Transport;
            Post(() => { try { OnTransportChanged?.Invoke(t); } catch { /* игра */ } });
        }

        /// <summary>Шлёт сигналинг-JSON релею как <c>0x10</c>-фрейм по сокету.</summary>
        private void SendSignal(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            var frame = new byte[1 + jsonBytes.Length];
            frame[0] = P.Signal;
            Buffer.BlockCopy(jsonBytes, 0, frame, 1, jsonBytes.Length);
            SendWsDirect(frame);
        }

        /// <summary>
        /// Маршрутизирует входящий сигналинг-JSON (<c>0x10</c>) в WebRTC-апгрейд:
        /// <c>{"t":"offer","sdp":...}</c> → <c>SetOffer</c>,
        /// <c>{"t":"ice",...}</c> → <c>AddRemoteIce</c>,
        /// <c>{"t":"webrtc-unavailable"}</c> → сразу остаёмся на WS. Главный поток.
        /// </summary>
        private void RouteSignal(string json)
        {
            var rtc = _webRtc;
            if (rtc == null || string.IsNullOrEmpty(json)) return;

            string type = ExtractJsonString(json, "t");
            if (type == "offer")
            {
                var sdp = ExtractJsonString(json, "sdp");
                if (!string.IsNullOrEmpty(sdp)) rtc.SetOffer(sdp);
            }
            else if (type == "ice")
            {
                // Передаём весь объект как JSON кандидата (candidate/sdpMid/
                // sdpMLineIndex) — jslib его сам распарсит.
                rtc.AddRemoteIce(json);
            }
            else if (type == "webrtc-unavailable")
            {
                // Релей не может поднять peer (нет ICE-порта и т.п.) — не ждём таймаута.
                rtc.Abort();
            }
            // {"t":"answer"} клиент не получает (offer создаёт релей) — игнор.
        }

        /// <summary>Собирает <c>{"t":"answer","sdp":"&lt;sdp&gt;"}</c> (sdp экранируется).</summary>
        private static string BuildAnswerJson(string sdp)
        {
            return "{\"t\":\"answer\",\"sdp\":\"" + EscapeJson(sdp) + "\"}";
        }

        /// <summary>
        /// Оборачивает локальный кандидат для отправки. jslib отдаёт уже готовый
        /// объект <c>{"candidate":..,"sdpMid":..,"sdpMLineIndex":..}</c>; нам
        /// нужно лишь добавить тег <c>"t":"ice"</c>, сохранив поля кандидата.
        /// </summary>
        private static string BuildIceJson(string candidateJson)
        {
            if (string.IsNullOrEmpty(candidateJson)) return "{\"t\":\"ice\"}";
            int brace = candidateJson.IndexOf('{');
            // Вставляем "t":"ice", сразу после открывающей скобки объекта.
            if (brace >= 0)
            {
                string inner = candidateJson.Substring(brace + 1);
                return "{\"t\":\"ice\"," + inner;
            }
            return "{\"t\":\"ice\"}";
        }

        /// <summary>
        /// Достаёт строковое значение по ключу из плоского JSON-объекта (значения
        /// типа строка). Достаточно для сигналинга <c>t</c>/<c>sdp</c>; корректно
        /// разэкранирует <c>\"</c>, <c>\\</c>, <c>\n</c>, <c>\r</c>, <c>\t</c>,
        /// <c>\/</c> и <c>\uXXXX</c>. Возвращает <c>null</c>, если ключа нет.
        /// </summary>
        private static string ExtractJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            // Ищем "key" как ключ (за двоеточием — строковое значение).
            string needle = "\"" + key + "\"";
            int k = json.IndexOf(needle, StringComparison.Ordinal);
            if (k < 0) return null;
            int i = k + needle.Length;
            // Пропускаем пробелы и двоеточие.
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
            if (i >= json.Length || json[i] != ':') return null;
            i++;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
            if (i >= json.Length || json[i] != '"') return null;
            i++; // вошли в строку

            var sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '"') break; // конец строки
                if (c == '\\' && i + 1 < json.Length)
                {
                    char n = json[i + 1];
                    switch (n)
                    {
                        case '"': sb.Append('"'); i += 2; continue;
                        case '\\': sb.Append('\\'); i += 2; continue;
                        case '/': sb.Append('/'); i += 2; continue;
                        case 'n': sb.Append('\n'); i += 2; continue;
                        case 'r': sb.Append('\r'); i += 2; continue;
                        case 't': sb.Append('\t'); i += 2; continue;
                        case 'b': sb.Append('\b'); i += 2; continue;
                        case 'f': sb.Append('\f'); i += 2; continue;
                        case 'u':
                            if (i + 5 < json.Length &&
                                int.TryParse(json.Substring(i + 2, 4),
                                    System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out int code))
                            {
                                sb.Append((char)code);
                                i += 6;
                                continue;
                            }
                            break;
                    }
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>Экранирует строку для вставки в JSON-значение (SDP многострочный).</summary>
        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Публикует <paramref name="action"/> на главном Unity-потоке через
        /// захваченный <see cref="SynchronizationContext"/>. Если контекста нет
        /// (создан вне Unity-потока) или мы уже на нём — выполняет синхронно.
        /// </summary>
        private void Post(Action action)
        {
            var ctx = _mainThread;
            if (ctx == null || ctx == SynchronizationContext.Current)
            {
                action();
                return;
            }
            ctx.Post(_ => action(), null);
        }

        /// <summary>Закрывает соединение без события <see cref="OnClosed"/> и снимает с насоса.</summary>
        public void Dispose()
        {
            _userClosed = true;
            _sessionGen++;
            _reconnecting = false;
            try { VhrRelayPump.Unregister(this); } catch { /* ignore */ }
            _pumpRegistered = false;
            DetachSocket();
        }

        /// <summary>Отцепляет и закрывает текущие сокет и WebRTC-канал (без события <see cref="OnClosed"/>).</summary>
        private void DetachSocket()
        {
            DisposeRtc();
            lock (_batchLock)
            {
                _wsBatch.Clear();
                _dcBatch.Clear();
            }

            var sock = _socket;
            _socket = null;
            if (sock != null)
            {
                sock.OnMessage -= HandleWsMessage;
                sock.OnClose -= HandleCloseRaw;
                try { sock.Dispose(); } catch { /* ignore */ }
            }
        }
    }
}
