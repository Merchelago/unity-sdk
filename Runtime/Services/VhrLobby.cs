using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Реализация <see cref="IVhrLobby"/> поверх <see cref="VhrRelay"/>: ездит по
    /// тому же сокету управляющим фреймом <c>0x20</c> (<c>[0x20][utf8 JSON]</c>).
    /// Исходящие — это плоские объекты <c>{"op":...}</c>, входящие — события
    /// <c>{"ev":...}</c> (см. <c>Documentation~/Lobby.md</c>). WebGL-safe: новых
    /// нативных зависимостей нет, всё едет по уже открытому WebSocket релея.
    /// </summary>
    public sealed class VhrLobbyService : IVhrLobby, IDisposable
    {
        private const byte CControl = 0x20;

        private readonly VhrRelay _relay;
        private readonly VhrSdkOptions _options;
        private readonly VhrApiClient _api;

        private bool _subscribed;
        private bool _authSent;

        // Ожидание ближайшего ev "start" (quickmatch/StartAsync).
        private TaskCompletionSource<VhrMatchInfo> _startTcs;
        // Ожидание ближайшего ev "lobby" (create/join).
        private TaskCompletionSource<VhrLobby> _lobbyTcs;

        /// <inheritdoc />
        public VhrLobby CurrentLobby { get; private set; }

        /// <inheritdoc />
        public string SelfUserId { get; private set; }

        /// <inheritdoc />
        public bool IsHost =>
            CurrentLobby != null && !string.IsNullOrEmpty(SelfUserId) &&
            CurrentLobby.hostId == SelfUserId;

        /// <inheritdoc />
        public event Action<VhrLobby> OnLobbyUpdated;
        /// <inheritdoc />
        public event Action<VhrLobbyInvite> OnInviteReceived;
        /// <inheritdoc />
        public event Action<int> OnMatchStarting;
        /// <inheritdoc />
        public event Action<VhrMatchInfo> OnMatchStarted;
        /// <inheritdoc />
        public event Action<string> OnClosed;

        /// <summary>Создаёт лобби-сервис поверх общего релея, опций и api-клиента.</summary>
        public VhrLobbyService(VhrRelay relay, VhrSdkOptions options, VhrApiClient api)
        {
            _relay = relay ?? throw new ArgumentNullException(nameof(relay));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _api = api ?? throw new ArgumentNullException(nameof(api));
        }

        // ---- публичный API ----

        /// <inheritdoc />
        public async Task<VhrMatchInfo> QuickMatchAsync(VhrMatchmakingOptions opts, CancellationToken ct = default)
        {
            opts ??= new VhrMatchmakingOptions();
            await EnsureReadyAsync(ct).ConfigureAwait(false);

            _startTcs = NewReplyTcs<VhrMatchInfo>();
            if (ct.CanBeCanceled) ct.Register(() => _startTcs?.TrySetCanceled());

            var sb = new StringBuilder();
            sb.Append("{\"op\":\"quickmatch\"");
            AppendStr(sb, "gameId", _options.GameId);
            AppendStr(sb, "mode", opts.mode);
            AppendInt(sb, "maxPlayers", opts.maxPlayers);
            AppendInt(sb, "minPlayers", opts.minPlayers);
            AppendBool(sb, "fillBots", opts.fillBots);
            AppendInt(sb, "waitSec", opts.waitSec);
            sb.Append('}');
            SendOp(sb.ToString());

            var info = await AwaitReplyAsync(_startTcs).ConfigureAwait(false);
            await JoinMatchRoomAsync(info, ct).ConfigureAwait(false);
            return info;
        }

        /// <inheritdoc />
        public async Task<VhrLobby> CreateLobbyAsync(VhrLobbyOptions opts)
        {
            opts ??= new VhrLobbyOptions();
            await EnsureReadyAsync(CancellationToken.None).ConfigureAwait(false);

            _lobbyTcs = NewReplyTcs<VhrLobby>();

            var sb = new StringBuilder();
            sb.Append("{\"op\":\"create\"");
            AppendStr(sb, "gameId", _options.GameId);
            AppendStr(sb, "mode", opts.mode);
            AppendInt(sb, "maxPlayers", opts.maxPlayers);
            AppendInt(sb, "minPlayers", opts.minPlayers);
            AppendBool(sb, "fillBots", opts.fillBots);
            AppendBool(sb, "isPrivate", opts.isPrivate);
            sb.Append('}');
            SendOp(sb.ToString());

            return await AwaitReplyAsync(_lobbyTcs).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<VhrLobby> JoinLobbyAsync(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new VhrSdkException("config_invalid", "code обязателен для JoinLobbyAsync.");
            await EnsureReadyAsync(CancellationToken.None).ConfigureAwait(false);

            _lobbyTcs = NewReplyTcs<VhrLobby>();

            var sb = new StringBuilder();
            sb.Append("{\"op\":\"join\"");
            AppendStr(sb, "code", code.Trim());
            sb.Append('}');
            SendOp(sb.ToString());

            return await AwaitReplyAsync(_lobbyTcs).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task LeaveLobbyAsync()
        {
            SendOp("{\"op\":\"leave\"}");
            CurrentLobby = null;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task CancelAsync()
        {
            SendOp("{\"op\":\"cancel\"}");
            _startTcs?.TrySetCanceled();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task SetReadyAsync(bool ready)
        {
            var sb = new StringBuilder();
            sb.Append("{\"op\":\"ready\"");
            AppendBool(sb, "ready", ready);
            sb.Append('}');
            SendOp(sb.ToString());
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task InviteFriendAsync(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new VhrSdkException("config_invalid", "userId обязателен для InviteFriendAsync.");
            var sb = new StringBuilder();
            sb.Append("{\"op\":\"invite\"");
            AppendStr(sb, "userId", userId.Trim());
            sb.Append('}');
            SendOp(sb.ToString());
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task KickAsync(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new VhrSdkException("config_invalid", "userId обязателен для KickAsync.");
            var sb = new StringBuilder();
            sb.Append("{\"op\":\"kick\"");
            AppendStr(sb, "userId", userId.Trim());
            sb.Append('}');
            SendOp(sb.ToString());
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task<VhrMatchInfo> StartAsync()
        {
            _startTcs = NewReplyTcs<VhrMatchInfo>();
            SendOp("{\"op\":\"start\"}");
            var info = await AwaitReplyAsync(_startTcs).ConfigureAwait(false);
            await JoinMatchRoomAsync(info, CancellationToken.None).ConfigureAwait(false);
            return info;
        }

        /// <inheritdoc />
        public async Task<VhrFriend[]> GetFriendsAsync(CancellationToken ct = default)
        {
            // Бэкенд (UnityGamesMS, FriendsController): GET /api/Friends →
            // {"items":[{"userId":"..","since":".."}]}. Имя/онлайн этот эндпоинт
            // не отдаёт — заполняем только userId; name/online остаются пустыми.
            var url = $"{_options.GamesBaseUrl}/api/Friends";
            var page = await _api.SendAsync<FriendList>("GET", url, allowNotImplemented: true, ct: ct);
            var items = page?.items;
            if (items == null || items.Length == 0) return Array.Empty<VhrFriend>();

            var result = new VhrFriend[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                result[i] = new VhrFriend
                {
                    userId = items[i].userId,
                    name = items[i].name,   // null, если бэкенд не резолвит ник
                    online = items[i].online
                };
            }
            return result;
        }

        // ---- инфраструктура: подписка, auth, отправка ----

        // ВАЖНО (WebGL): по умолчанию TCS-продолжения выполняются СИНХРОННО прямо
        // внутри JS-колбэка сокета (нет тредпула, ConfigureAwait(false) тред не
        // переключает) → ответный 0x20-кадр прилетает вложенно в стек отправки,
        // раньше подписки/создания TCS, и теряется → лобби висит вечно.
        // RunContinuationsAsynchronously ставит продолжение в очередь Unity-тика —
        // порядок «отправил → жду → ответ ПОЗЖЕ» сохраняется, как на нативе.
        private static TaskCompletionSource<T> NewReplyTcs<T>() =>
            new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Ждёт ответ сервера с жёстким таймаутом — SDK не висит вечно, даже если кадр потерян.</summary>
        private async Task<T> AwaitReplyAsync<T>(TaskCompletionSource<T> tcs)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using (cts.Token.Register(() => tcs.TrySetException(
                new VhrSdkException("lobby_timeout", "Сервер лобби не ответил вовремя. Попробуйте ещё раз."))))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Гарантирует, что сокет релея открыт, мы подписаны на <c>0x20</c> и
        /// отправили <c>{"op":"auth"}</c> с JWT игрока ровно один раз.
        /// </summary>
        private async Task EnsureReadyAsync(CancellationToken ct)
        {
            // Подписка ДО ConnectAsync: на WebGL ответный 0x20-кадр (или join 0x81)
            // может прийти синхронно (вложенно в onmessage-колбэк) сразу после/во
            // время подключения — если подписаться ПОСЛЕ, кадр потеряется.
            if (!_subscribed)
            {
                _relay.OnControlFrame += HandleControlFrame;
                _relay.OnClosed += HandleRelayClosed;
                _subscribed = true;
            }

            if (!_relay.IsConnected)
                await _relay.ConnectAsync("main", ct).ConfigureAwait(false);

            if (!_authSent)
            {
                var token = _options.TokenProvider?.Invoke();
                var sb = new StringBuilder();
                sb.Append("{\"op\":\"auth\"");
                AppendStr(sb, "token", token ?? string.Empty);
                sb.Append('}');
                SendOp(sb.ToString());
                _authSent = true;
            }
        }

        /// <summary>Кодирует <paramref name="json"/> в <c>0x20</c>-фрейм и шлёт через релей.</summary>
        private void SendOp(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            var frame = new byte[1 + jsonBytes.Length];
            frame[0] = CControl;
            Buffer.BlockCopy(jsonBytes, 0, frame, 1, jsonBytes.Length);
            _relay.SendControl(frame);
        }

        private async Task JoinMatchRoomAsync(VhrMatchInfo info, CancellationToken ct)
        {
            if (info == null || string.IsNullOrEmpty(info.roomId)) return;
            try { await _relay.JoinRoomRawAsync(info.roomId, ct).ConfigureAwait(false); }
            catch { /* комната станет доступна при следующем входе; не валим матч */ }
        }

        private void HandleRelayClosed(string reason)
        {
            _authSent = false;
            try { OnClosed?.Invoke(reason); } catch { /* подписчик */ }
        }

        // ---- разбор входящих 0x20-событий (на главном потоке от VhrRelay.Post) ----

        private void HandleControlFrame(byte type, byte[] payload)
        {
            if (type != CControl || payload == null || payload.Length == 0) return;
            string json;
            try { json = Encoding.UTF8.GetString(payload); }
            catch { return; }

            string ev = ExtractString(json, "ev");
            if (string.IsNullOrEmpty(ev)) return;

            switch (ev)
            {
                case "lobby": HandleLobbyEvent(json); break;
                case "invite": HandleInviteEvent(json); break;
                case "starting": HandleStartingEvent(json); break;
                case "start": HandleStartEvent(json); break;
                case "closed": HandleClosedEvent(json); break;
                case "error": HandleErrorEvent(json); break;
                default: break; // forward-compat
            }
        }

        private void HandleLobbyEvent(string json)
        {
            LobbyEnvelope env = SafeFromJson<LobbyEnvelope>(json);
            var lobby = env?.lobby;
            if (lobby == null) return;

            // Зафиксировать «себя» по hostId, если ник совпал/прислан isSelf нет —
            // отметим isSelf по SelfUserId, если он уже известен.
            if (!string.IsNullOrEmpty(SelfUserId) && lobby.members != null)
            {
                foreach (var m in lobby.members)
                    if (m != null) m.isSelf = m.userId == SelfUserId;
            }

            CurrentLobby = lobby;
            _lobbyTcs?.TrySetResult(lobby);
            try { OnLobbyUpdated?.Invoke(lobby); } catch { /* подписчик */ }
        }

        private void HandleInviteEvent(string json)
        {
            var invite = new VhrLobbyInvite
            {
                fromUserId = ExtractString(json, "fromUserId"),
                fromName = ExtractString(json, "fromName"),
                code = ExtractString(json, "code"),
                gameId = ExtractString(json, "gameId")
            };
            try { OnInviteReceived?.Invoke(invite); } catch { /* подписчик */ }
        }

        private void HandleStartingEvent(string json)
        {
            int countdown = ExtractInt(json, "countdownSec");
            try { OnMatchStarting?.Invoke(countdown); } catch { /* подписчик */ }
        }

        private void HandleStartEvent(string json)
        {
            StartEnvelope env = SafeFromJson<StartEnvelope>(json);
            if (env == null) return;

            // Запомнить себя для последующих IsHost/isSelf.
            if (!string.IsNullOrEmpty(env.selfUserId)) SelfUserId = env.selfUserId;

            var info = new VhrMatchInfo
            {
                roomId = env.roomId,
                hostId = env.hostId,
                selfUserId = !string.IsNullOrEmpty(env.selfUserId) ? env.selfUserId : SelfUserId,
                selfSlot = env.selfSlot,
                botCount = env.botCount,
                botSlots = env.botSlots ?? Array.Empty<int>(),
                players = env.players ?? Array.Empty<VhrMatchPlayer>()
            };
            info.isHost = !string.IsNullOrEmpty(info.selfUserId) && info.selfUserId == info.hostId;

            _startTcs?.TrySetResult(info);
            try { OnMatchStarted?.Invoke(info); } catch { /* подписчик */ }
        }

        private void HandleClosedEvent(string json)
        {
            string reason = ExtractString(json, "reason");
            CurrentLobby = null;
            _startTcs?.TrySetException(new VhrSdkException("lobby_closed", reason ?? "Лобби закрыто."));
            _lobbyTcs?.TrySetException(new VhrSdkException("lobby_closed", reason ?? "Лобби закрыто."));
            try { OnClosed?.Invoke(reason); } catch { /* подписчик */ }
        }

        private void HandleErrorEvent(string json)
        {
            string code = ExtractString(json, "code");
            string message = ExtractString(json, "message");
            var ex = new VhrSdkException(string.IsNullOrEmpty(code) ? "lobby_error" : code,
                message ?? "Ошибка лобби.");
            _startTcs?.TrySetException(ex);
            _lobbyTcs?.TrySetException(ex);
        }

        private static T SafeFromJson<T>(string json) where T : class
        {
            try { return JsonUtility.FromJson<T>(json); }
            catch { return null; }
        }

        public void Dispose()
        {
            if (_subscribed)
            {
                _relay.OnControlFrame -= HandleControlFrame;
                _relay.OnClosed -= HandleRelayClosed;
                _subscribed = false;
            }
            _startTcs?.TrySetCanceled();
            _lobbyTcs?.TrySetCanceled();
        }

        // ---- JSON: сборка исходящих значений ----

        private static void AppendStr(StringBuilder sb, string key, string value)
        {
            if (value == null) return;
            sb.Append(",\"").Append(key).Append("\":\"").Append(EscapeJson(value)).Append('"');
        }

        private static void AppendInt(StringBuilder sb, string key, int value)
        {
            sb.Append(",\"").Append(key).Append("\":").Append(value);
        }

        private static void AppendBool(StringBuilder sb, string key, bool value)
        {
            sb.Append(",\"").Append(key).Append("\":").Append(value ? "true" : "false");
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length + 8);
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
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // ---- JSON: разбор скалярных полей верхнего уровня (для ev/invite/...) ----

        /// <summary>
        /// Достаёт строковое значение по ключу из плоского JSON. Разэкранирует
        /// стандартные escape'ы и <c>\uXXXX</c>. <c>null</c>, если ключа нет.
        /// </summary>
        private static string ExtractString(string json, string key)
        {
            int i = FindValueStart(json, key);
            if (i < 0) return null;
            if (json[i] != '"') return null; // не строковое значение
            i++;
            var sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '"') break;
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
                            { sb.Append((char)code); i += 6; continue; }
                            break;
                    }
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>Достаёт целочисленное значение по ключу из плоского JSON (0, если нет).</summary>
        private static int ExtractInt(string json, string key)
        {
            int i = FindValueStart(json, key);
            if (i < 0) return 0;
            int start = i;
            if (i < json.Length && (json[i] == '-' || json[i] == '+')) i++;
            while (i < json.Length && char.IsDigit(json[i])) i++;
            if (i == start) return 0;
            return int.TryParse(json.Substring(start, i - start),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : 0;
        }

        /// <summary>
        /// Возвращает индекс первого непробельного символа значения для ключа
        /// <c>"key":</c> верхнего уровня (после двоеточия), либо -1.
        /// </summary>
        private static int FindValueStart(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return -1;
            string needle = "\"" + key + "\"";
            int k = json.IndexOf(needle, StringComparison.Ordinal);
            if (k < 0) return -1;
            int i = k + needle.Length;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
            if (i >= json.Length || json[i] != ':') return -1;
            i++;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
            return i < json.Length ? i : -1;
        }

        // ---- DTO-обёртки для JsonUtility (вложенные объекты/массивы) ----

        [Serializable] private sealed class LobbyEnvelope { public string ev; public VhrLobby lobby; }

        [Serializable]
        private sealed class StartEnvelope
        {
            public string ev;
            public string roomId;
            public string hostId;
            public string selfUserId;
            public int selfSlot;
            public int botCount;
            public int[] botSlots;
            public VhrMatchPlayer[] players;
        }

        [Serializable] private sealed class FriendList { public FriendDto[] items; }
        [Serializable] private sealed class FriendDto { public string userId; public string name; public bool online; }
    }
}
