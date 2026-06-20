using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
    /// <b>Поток.</b> На нативе/в редакторе сообщения приходят из фоновой задачи
    /// сокета — relay маршалит все события на главный Unity-поток через
    /// <see cref="SynchronizationContext"/>, захваченный при создании (создавайте
    /// <see cref="VhrRelay"/> с главного потока, напр. через <c>VhrSdk.Relay</c>).
    /// В WebGL всё и так на главном потоке.
    /// </para>
    /// </remarks>
    public sealed class VhrRelay : IDisposable
    {
        // Типы сообщений протокола.
        private const byte CJoin = 0x01;
        private const byte CData = 0x02;
        private const byte CTo = 0x04;
        private const byte SJoined = 0x81;
        private const byte SData = 0x83;
        private const byte SPeerJoined = 0x84;
        private const byte SPeerLeft = 0x85;
        // 0x10 = WebRTC-сигналинг (offer/answer/ice) поверх той же сокет-связи:
        // [0x10][utf8 JSON]. Двунаправленный. Сам игровой трафик по WebRTC уже
        // не носит этот байт — он идёт по DataChannel в исходном бинарном виде.
        private const byte CSignal = 0x10;
        private const byte SSignal = 0x10;

        private readonly VhrSdkOptions _options;
        private readonly Func<IVhrSocket> _socketFactory;
        private readonly SynchronizationContext _mainThread;

        private IVhrSocket _socket;
        private TaskCompletionSource<bool> _joinedTcs;
        private string _room;

        // WebRTC-апгрейд (только WebGL). null/неактивен -> весь трафик по WS.
        private WebGLWebRtc _webRtc;
        private volatile bool _webRtcActive;

        /// <summary>
        /// Текущий транспорт игрового трафика: <c>"webrtc"</c> (DataChannel,
        /// низколатентный, апгрейд состоялся) или <c>"ws"</c> (WebSocket-релей —
        /// дефолт и fallback). Для диагностики. На нативе/в редакторе всегда
        /// <c>"ws"</c>.
        /// </summary>
        public string Transport => _webRtcActive ? "webrtc" : "ws";

        /// <summary>Свой peer-id, выданный релеем после join. 0 до входа в комнату.</summary>
        public int SelfId { get; private set; }

        /// <summary>Подключён и вошёл в комнату.</summary>
        public bool IsConnected => _socket != null && _socket.IsOpen && SelfId != 0;

        /// <summary>Пришли данные от другого пира (<c>0x83</c>). На главном потоке.</summary>
        public event Action<int, byte[]> OnData;

        /// <summary>Вход в комнату подтверждён: свой id + уже присутствующие пиры (<c>0x81</c>).</summary>
        public event Action<int, int[]> OnJoined;

        /// <summary>В комнату вошёл новый пир (<c>0x84</c>).</summary>
        public event Action<int> OnPeerJoined;

        /// <summary>Пир покинул комнату (<c>0x85</c>).</summary>
        public event Action<int> OnPeerLeft;

        /// <summary>Соединение закрылось. Аргумент — причина (может быть <c>null</c>).</summary>
        public event Action<string> OnClosed;

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

        /// <summary>
        /// Подключается к релею и входит в комнату <c>"{GameId}:{lobbyCode}"</c>.
        /// Завершается, когда релей подтвердил вход (<c>0x81 joined</c>);
        /// <see cref="OnJoined"/> при этом тоже срабатывает. Бросает
        /// <see cref="VhrSdkException"/> при сбое подключения.
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

            SelfId = 0;
            _joinedTcs = new TaskCompletionSource<bool>();

            _socket = _socketFactory();
            _socket.OnMessage += HandleMessageRaw;
            _socket.OnClose += HandleCloseRaw;

            if (ct.CanBeCanceled)
                ct.Register(() => _joinedTcs?.TrySetCanceled());

            await _socket.ConnectAsync(baseUrl.Trim(), ct).ConfigureAwait(false);

            // Шлём join сразу после открытия.
            SendJoin(_room);

            // Ждём подтверждения входа (0x81).
            await _joinedTcs.Task.ConfigureAwait(false);

            // Опциональный апгрейд на WebRTC (только WebGL). Прозрачно для игры:
            // тот же Send/OnData. При любом сбое/таймауте остаёмся на WebSocket.
            await TryUpgradeToWebRtcAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// На WebGL пытается поднять WebRTC DataChannel поверх уже открытого
        /// сокета: шлёт <c>0x10 {"t":"webrtc-start"}</c>, отвечает на offer
        /// релея, трикл-ICE'ит, и при открытии канала переключает игровой трафик
        /// на него. Не-WebGL и <c>PreferWebRtc=false</c> — мгновенный no-op
        /// (остаёмся на WS). Никогда не бросает: апгрейд best-effort.
        /// </summary>
        private async Task TryUpgradeToWebRtcAsync(CancellationToken ct)
        {
            if (_options.PreferWebRtc != true) return;
            if (!WebGLWebRtc.IsSupported) return; // натив/редактор -> WS
            var sock = _socket;
            if (sock == null || !sock.IsOpen) return;

            WebGLWebRtc rtc = null;
            try
            {
                rtc = new WebGLWebRtc();
                _webRtc = rtc;

                // Локальные answer/ICE релеим релею как 0x10 по тому же сокету.
                rtc.OnLocalAnswer += sdp => SendSignal(BuildAnswerJson(sdp));
                rtc.OnLocalIce += candidateJson => SendSignal(BuildIceJson(candidateJson));
                // Игровые данные из DataChannel поднимаем как обычный 0x83-парс.
                rtc.OnData += OnDataChannelMessage;

                // Просим релей начать (он создаст offer и DataChannel "game").
                SendSignal("{\"t\":\"webrtc-start\"}");

                // Ждём открытия канала или таймаута 5 c. Входящие offer/ice
                // прилетают через HandleMessageRaw -> RouteSignal -> rtc.*.
                bool ok = await rtc.StartAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                if (ok)
                {
                    _webRtcActive = true; // с этого момента Send() идёт по каналу
                }
                else
                {
                    _webRtc = null;
                    try { rtc.Dispose(); } catch { /* ignore */ }
                }
            }
            catch
            {
                // Любой сбой -> тихо остаёмся на WebSocket.
                _webRtcActive = false;
                _webRtc = null;
                try { rtc?.Dispose(); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Рассылает <paramref name="data"/> всем пирам комнаты (<c>0x02</c>).
        /// Если поднят WebRTC-апгрейд (<see cref="Transport"/> == <c>"webrtc"</c>),
        /// уходит по низколатентному DataChannel; иначе — по WebSocket. Прозрачно
        /// для вызывающего.
        /// </summary>
        public void Send(byte[] data)
        {
            if (data == null || data.Length == 0) return;

            var frame = new byte[1 + data.Length];
            frame[0] = CData;
            Buffer.BlockCopy(data, 0, frame, 1, data.Length);

            // Игровой трафик — по WebRTC, если канал открыт; иначе по сокету.
            var rtc = _webRtc;
            if (_webRtcActive && rtc != null && rtc.IsOpen)
            {
                rtc.Send(frame);
                return;
            }

            var sock = _socket;
            if (sock == null || !sock.IsOpen) return;
            sock.Send(frame);
        }

        /// <summary>
        /// Шлёт <paramref name="data"/> конкретному пиру (<c>0x04</c>,
        /// little-endian id). По WebRTC DataChannel при активном апгрейде, иначе
        /// по WebSocket.
        /// </summary>
        public void SendTo(int peerId, byte[] data)
        {
            if (data == null || data.Length == 0) return;

            var frame = new byte[1 + 4 + data.Length];
            frame[0] = CTo;
            WriteInt32LE(frame, 1, peerId);
            Buffer.BlockCopy(data, 0, frame, 5, data.Length);

            var rtc = _webRtc;
            if (_webRtcActive && rtc != null && rtc.IsOpen)
            {
                rtc.Send(frame);
                return;
            }

            var sock = _socket;
            if (sock == null || !sock.IsOpen) return;
            sock.Send(frame);
        }

        /// <summary>Закрывает соединение с релеем. Безопасно вызывать повторно.</summary>
        public async Task CloseAsync()
        {
            var sock = _socket;
            if (sock == null) return;
            try { await sock.CloseAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
        }

        private void SendJoin(string room)
        {
            var roomBytes = Encoding.UTF8.GetBytes(room);
            // ushort длины little-endian.
            var frame = new byte[1 + 2 + roomBytes.Length];
            frame[0] = CJoin;
            frame[1] = (byte)(roomBytes.Length & 0xFF);
            frame[2] = (byte)((roomBytes.Length >> 8) & 0xFF);
            Buffer.BlockCopy(roomBytes, 0, frame, 3, roomBytes.Length);
            _socket.Send(frame);
        }

        // ---- приём (может быть на фоновом потоке нативного сокета) ----

        private void HandleMessageRaw(byte[] msg)
        {
            // Парсим вне главного потока (дёшево), события публикуем на главном.
            if (msg == null || msg.Length < 1) return;
            byte type = msg[0];

            switch (type)
            {
                case SJoined:
                {
                    if (msg.Length < 9) return;
                    int self = ReadInt32LE(msg, 1);
                    int count = ReadInt32LE(msg, 5);
                    if (count < 0) count = 0;
                    var peers = new int[count];
                    int off = 9;
                    for (int i = 0; i < count && off + 4 <= msg.Length; i++, off += 4)
                        peers[i] = ReadInt32LE(msg, off);

                    SelfId = self;
                    Post(() =>
                    {
                        try { OnJoined?.Invoke(self, peers); } catch { /* игра */ }
                    });
                    _joinedTcs?.TrySetResult(true);
                    break;
                }
                case SData:
                {
                    if (msg.Length < 5) return;
                    int sender = ReadInt32LE(msg, 1);
                    int payloadLen = msg.Length - 5;
                    var payload = new byte[payloadLen];
                    if (payloadLen > 0) Buffer.BlockCopy(msg, 5, payload, 0, payloadLen);
                    Post(() =>
                    {
                        try { OnData?.Invoke(sender, payload); } catch { /* игра */ }
                    });
                    break;
                }
                case SPeerJoined:
                {
                    if (msg.Length < 5) return;
                    int peer = ReadInt32LE(msg, 1);
                    Post(() =>
                    {
                        try { OnPeerJoined?.Invoke(peer); } catch { /* игра */ }
                    });
                    break;
                }
                case SPeerLeft:
                {
                    if (msg.Length < 5) return;
                    int peer = ReadInt32LE(msg, 1);
                    Post(() =>
                    {
                        try { OnPeerLeft?.Invoke(peer); } catch { /* игра */ }
                    });
                    break;
                }
                case SSignal:
                {
                    // WebRTC-сигналинг от релея: [0x10][utf8 JSON]. Маршалим на
                    // главный поток — WebRTC API (WebGL) трогаем только оттуда.
                    if (msg.Length < 2) return;
                    var json = Encoding.UTF8.GetString(msg, 1, msg.Length - 1);
                    Post(() => RouteSignal(json));
                    break;
                }
                default:
                    // Неизвестный тип — игнорируем (forward-compat).
                    break;
            }
        }

        private void HandleCloseRaw(string reason)
        {
            // Сокет — несущая для сигналинга и контроля; его закрытие рвёт и
            // WebRTC-апгрейд. Сворачиваем канал и возвращаемся к "ws".
            var rtc = _webRtc;
            _webRtc = null;
            _webRtcActive = false;
            if (rtc != null)
            {
                try { rtc.Dispose(); } catch { /* ignore */ }
            }

            // Если закрылись до join — разблокируем ConnectAsync ошибкой.
            _joinedTcs?.TrySetException(
                new VhrSdkException("relay_closed", $"Соединение с релеем закрылось: {reason}"));
            Post(() =>
            {
                try { OnClosed?.Invoke(reason); } catch { /* игра */ }
            });
        }

        // ---- WebRTC-апгрейд: сигналинг (0x10) и приём по DataChannel ----

        /// <summary>
        /// Игровой фрейм из WebRTC DataChannel. Релей шлёт сюда те же
        /// <c>0x83 data [i32 senderId][payload]</c>, что и по WebSocket, поэтому
        /// парсим тем же путём (на WebGL это уже главный поток).
        /// </summary>
        private void OnDataChannelMessage(byte[] msg)
        {
            HandleMessageRaw(msg);
        }

        /// <summary>Шлёт сигналинг-JSON релею как <c>0x10</c>-фрейм по сокету.</summary>
        private void SendSignal(string json)
        {
            var sock = _socket;
            if (sock == null || !sock.IsOpen || string.IsNullOrEmpty(json)) return;
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            var frame = new byte[1 + jsonBytes.Length];
            frame[0] = CSignal;
            Buffer.BlockCopy(jsonBytes, 0, frame, 1, jsonBytes.Length);
            sock.Send(frame);
        }

        /// <summary>
        /// Маршрутизирует входящий сигналинг-JSON (<c>0x10</c>) в WebRTC-апгрейд:
        /// <c>{"t":"offer","sdp":...}</c> → <c>SetOffer</c>,
        /// <c>{"t":"ice",...}</c> → <c>AddRemoteIce</c>. Главный поток.
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

        private static void WriteInt32LE(byte[] buf, int offset, int value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static int ReadInt32LE(byte[] buf, int offset)
        {
            return buf[offset]
                | (buf[offset + 1] << 8)
                | (buf[offset + 2] << 16)
                | (buf[offset + 3] << 24);
        }

        public void Dispose()
        {
            var rtc = _webRtc;
            _webRtc = null;
            _webRtcActive = false;
            if (rtc != null)
            {
                try { rtc.Dispose(); } catch { /* ignore */ }
            }

            var sock = _socket;
            _socket = null;
            if (sock != null)
            {
                sock.OnMessage -= HandleMessageRaw;
                sock.OnClose -= HandleCloseRaw;
                try { sock.Dispose(); } catch { /* ignore */ }
            }
        }
    }
}
