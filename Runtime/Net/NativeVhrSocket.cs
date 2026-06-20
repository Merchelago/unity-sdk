using System;
using System.Threading;
using System.Threading.Tasks;
#if !UNITY_WEBGL || UNITY_EDITOR
using System.Net.WebSockets;
#endif

namespace VhrGames.Sdk
{
    /// <summary>
    /// Нативная / редакторная реализация <see cref="IVhrSocket"/> на базе
    /// <c>System.Net.WebSockets.ClientWebSocket</c>. Доступна везде, кроме
    /// реальной WebGL-сборки (там <c>ClientWebSocket</c> не работает — используется
    /// <see cref="WebGLVhrSocket"/>).
    /// </summary>
    /// <remarks>
    /// Приём идёт в фоновой задаче (<see cref="ReceiveLoopAsync"/>), поэтому
    /// события <see cref="OnMessage"/>/<see cref="OnClose"/> вызываются <b>не</b>
    /// на главном Unity-потоке. <see cref="VhrRelay"/> маршалит их на главный
    /// поток, прежде чем отдать игре.
    /// </remarks>
    public sealed class NativeVhrSocket : IVhrSocket
    {
        public event Action OnOpen;
        public event Action<byte[]> OnMessage;
        public event Action<string> OnClose;

#if !UNITY_WEBGL || UNITY_EDITOR
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private readonly object _sendLock = new object();
        private Task _sendChain = Task.CompletedTask;
        private volatile bool _closed;

        public bool IsOpen => _ws != null && _ws.State == WebSocketState.Open;

        public async Task ConnectAsync(string url, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(url))
                throw new ArgumentException("url is required", nameof(url));

            _ws = new ClientWebSocket();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            await _ws.ConnectAsync(new Uri(url), _cts.Token).ConfigureAwait(false);

            try { OnOpen?.Invoke(); } catch { /* обработчик игры — не валим транспорт */ }

            // Фоновый цикл приёма; не ждём его здесь.
            _ = ReceiveLoopAsync(_cts.Token);
        }

        public void Send(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            var ws = _ws;
            if (ws == null || ws.State != WebSocketState.Open) return;

            // Сериализуем отправки: ClientWebSocket не допускает параллельных
            // SendAsync. Гоняем их через цепочку задач под локом.
            lock (_sendLock)
            {
                var prev = _sendChain;
                _sendChain = prev.ContinueWith(async _ =>
                {
                    try
                    {
                        await ws.SendAsync(
                            new ArraySegment<byte>(data),
                            WebSocketMessageType.Binary,
                            endOfMessage: true,
                            _cts != null ? _cts.Token : CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // Сокет мог закрыться — приёмный цикл выдаст OnClose.
                    }
                }, TaskScheduler.Default).Unwrap();
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[8192];
            var ws = _ws;
            string reason = null;

            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    using var ms = new System.IO.MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct)
                            .ConfigureAwait(false);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            reason = result.CloseStatusDescription;
                            try
                            {
                                await ws.CloseOutputAsync(
                                    WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                                    .ConfigureAwait(false);
                            }
                            catch { /* ignore */ }
                            RaiseClose(reason);
                            return;
                        }

                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Binary && ms.Length > 0)
                    {
                        var payload = ms.ToArray();
                        try { OnMessage?.Invoke(payload); } catch { /* обработчик игры */ }
                    }
                    // Текстовые фреймы релеем не используются — игнорируем.
                }
            }
            catch (OperationCanceledException)
            {
                // Нормальное завершение при Close/Dispose.
            }
            catch (Exception e)
            {
                reason = e.Message;
            }

            RaiseClose(reason);
        }

        private void RaiseClose(string reason)
        {
            if (_closed) return;
            _closed = true;
            try { OnClose?.Invoke(reason); } catch { /* обработчик игры */ }
        }

        public async Task CloseAsync()
        {
            var ws = _ws;
            try { _cts?.Cancel(); } catch { /* ignore */ }

            if (ws != null && (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived))
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch { /* ignore */ }
            }

            RaiseClose(null);
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { _cts?.Dispose(); } catch { /* ignore */ }
            try { _ws?.Dispose(); } catch { /* ignore */ }
            _ws = null;
            _cts = null;
        }
#else
        // В реальной WebGL-сборке этот тип скомпилируется как пустышка-затычка:
        // фактически используется WebGLVhrSocket. ClientWebSocket в WebGL нет.
        public bool IsOpen => false;
        public Task ConnectAsync(string url, CancellationToken ct = default) =>
            throw new NotSupportedException("NativeVhrSocket недоступен в WebGL — используйте WebGLVhrSocket.");
        public void Send(byte[] data) { }
        public Task CloseAsync() => Task.CompletedTask;
        public void Dispose() { }
#endif
    }
}
