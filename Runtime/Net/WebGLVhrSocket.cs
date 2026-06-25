using System;
using System.Threading;
using System.Threading.Tasks;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AOT;
#endif

namespace VhrGames.Sdk
{
    /// <summary>
    /// WebGL-реализация <see cref="IVhrSocket"/> поверх браузерного
    /// <c>WebSocket</c> через плагин <c>Runtime/Plugins/WebGL/VhrWebSocket.jslib</c>.
    /// Нужна потому, что <c>System.Net.WebSockets.ClientWebSocket</c> в WebGL не
    /// работает.
    /// </summary>
    /// <remarks>
    /// В WebGL всё однопоточно: коллбеки из JS приходят на главном Unity-потоке,
    /// поэтому события <see cref="OnOpen"/>/<see cref="OnMessage"/>/<see cref="OnClose"/>
    /// уже main-thread-safe (доп. маршалинг не нужен).
    /// <para>
    /// Коллбеки JS статические (<c>[MonoPInvokeCallback]</c>), поэтому экземпляры
    /// находятся по целочисленному handle через статическую таблицу
    /// <c>Instances</c>.
    /// </para>
    /// </remarks>
    public sealed class WebGLVhrSocket : IVhrSocket
    {
        public event Action OnOpen;
        public event Action<byte[]> OnMessage;
        public event Action<string> OnClose;

#if UNITY_WEBGL && !UNITY_EDITOR
        private delegate void OpenCb(int id);
        private delegate void MessageCb(int id, IntPtr ptr, int len);
        private delegate void CloseCb(int id, int errFlag);

        [DllImport("__Internal")]
        private static extern int VhrWs_Connect(string url, OpenCb onOpen, MessageCb onMessage, CloseCb onClose);

        [DllImport("__Internal")]
        private static extern void VhrWs_Send(int id, byte[] data, int len);

        [DllImport("__Internal")]
        private static extern void VhrWs_Close(int id);

        [DllImport("__Internal")]
        private static extern int VhrWs_IsOpen(int id);

        // handle -> экземпляр. Коллбеки статические, инстанс находим тут.
        private static readonly Dictionary<int, WebGLVhrSocket> Instances =
            new Dictionary<int, WebGLVhrSocket>();

        private int _id;
        private bool _closed;
        private TaskCompletionSource<bool> _connectTcs;

        public bool IsOpen => _id != 0 && VhrWs_IsOpen(_id) == 1;

        public Task ConnectAsync(string url, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(url))
                throw new ArgumentException("url is required", nameof(url));

            // WebGL: продолжения асинхронно (в очередь тика), иначе onopen-колбэк
            // выполнит await-продолжение синхронно и инвертирует порядок отправки/приёма.
            _connectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _id = VhrWs_Connect(url, OnOpenStatic, OnMessageStatic, OnCloseStatic);
            if (_id == 0)
            {
                _connectTcs.TrySetException(
                    new VhrSdkException("relay_connect_failed", "Не удалось создать WebSocket (WebGL)."));
                return _connectTcs.Task;
            }

            Instances[_id] = this;

            if (ct.CanBeCanceled)
                ct.Register(() => _connectTcs?.TrySetCanceled());

            return _connectTcs.Task;
        }

        public void Send(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            if (_id == 0) return;
            VhrWs_Send(_id, data, data.Length);
        }

        public Task CloseAsync()
        {
            if (_id != 0)
            {
                try { VhrWs_Close(_id); } catch { /* ignore */ }
            }
            // OnClose придёт из JS-коллбека; форсируем на случай, если сокет был
            // CONNECTING и onclose не вызовется.
            RaiseClose(null);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_id != 0)
            {
                try { VhrWs_Close(_id); } catch { /* ignore */ }
                Instances.Remove(_id);
                _id = 0;
            }
        }

        [MonoPInvokeCallback(typeof(OpenCb))]
        private static void OnOpenStatic(int id)
        {
            UnityEngine.Debug.Log("[VHR WS] onopen id=" + id);
            if (!Instances.TryGetValue(id, out var self))
            {
                UnityEngine.Debug.LogWarning("[VHR WS] onopen: инстанс " + id + " не найден (гонка регистрации Instances!)");
                return;
            }
            try { self.OnOpen?.Invoke(); } catch { /* обработчик игры */ }
            self._connectTcs?.TrySetResult(true);
        }

        [MonoPInvokeCallback(typeof(MessageCb))]
        private static void OnMessageStatic(int id, IntPtr ptr, int len)
        {
            // Всегда освобождаем буфер, выделенный в jslib (_malloc).
            byte[] payload = null;
            try
            {
                if (len > 0 && ptr != IntPtr.Zero)
                {
                    payload = new byte[len];
                    Marshal.Copy(ptr, payload, 0, len);
                }
            }
            finally
            {
                if (ptr != IntPtr.Zero)
                    VhrFree(ptr);
            }

            if (payload == null) return;
            UnityEngine.Debug.Log("[VHR WS] msg len=" + payload.Length + " type=0x" + payload[0].ToString("X2"));
            if (!Instances.TryGetValue(id, out var self)) return;
            try { self.OnMessage?.Invoke(payload); } catch { /* обработчик игры */ }
        }

        [MonoPInvokeCallback(typeof(CloseCb))]
        private static void OnCloseStatic(int id, int errFlag)
        {
            UnityEngine.Debug.Log("[VHR WS] onclose id=" + id + " err=" + errFlag);
            if (!Instances.TryGetValue(id, out var self)) return;
            var reason = errFlag != 0 ? "websocket error" : null;
            // Если соединение так и не открылось — разблокируем ConnectAsync.
            self._connectTcs?.TrySetException(
                new VhrSdkException("relay_connect_failed", "WebSocket закрылся до открытия."));
            self.RaiseClose(reason);
        }

        private void RaiseClose(string reason)
        {
            if (_closed) return;
            _closed = true;
            if (_id != 0) Instances.Remove(_id);
            try { OnClose?.Invoke(reason); } catch { /* обработчик игры */ }
        }

        // Освобождение буфера кучи. Через обёртку VhrFree (jslib) над _free, иначе
        // EntryPoint="free" под IL2CPP/WebGL объявляет free(intptr_t) и конфликтует
        // с free(void*) из emscripten (ошибка сборки).
        [DllImport("__Internal", EntryPoint = "VhrFree")]
        private static extern void VhrFree(IntPtr ptr);
#else
        // Вне WebGL используется NativeVhrSocket; здесь — затычка, чтобы тип
        // существовал во всех таргетах (на него ссылается фабрика сокета).
        public bool IsOpen => false;
        public Task ConnectAsync(string url, CancellationToken ct = default) =>
            throw new NotSupportedException("WebGLVhrSocket доступен только в WebGL-сборке.");
        public void Send(byte[] data) { }
        public Task CloseAsync() => Task.CompletedTask;
        public void Dispose() { }
#endif
    }
}
