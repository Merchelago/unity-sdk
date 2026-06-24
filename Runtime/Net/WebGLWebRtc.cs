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
    /// WebGL-обёртка над браузерным <c>RTCPeerConnection</c> + <c>DataChannel</c>
    /// (плагин <c>Runtime/Plugins/WebGL/VhrWebRtc.jslib</c>). Даёт низколатентный
    /// UDP-подобный (unreliable/unordered) канал поверх WebRTC как <b>апгрейд</b>
    /// над WebSocket-релеем (см. <see cref="VhrRelay"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Сигналинг (offer/answer/ICE) <b>не</b> ходит по WebRTC — он релеится через
    /// уже открытый WebSocket как <c>0x10</c>-фреймы. Релей <b>создаёт</b> offer и
    /// DataChannel <c>"game"</c>; этот класс отвечает answer'ом, трикл-ICE'ит и
    /// принимает канал. Поэтому наружу мы отдаём <see cref="OnLocalAnswer"/> и
    /// <see cref="OnLocalIce"/> — их <see cref="VhrRelay"/> упакует в <c>0x10</c>
    /// и зашлёт по сокету; а входящие answer/ice из <c>0x10</c> подаются обратно
    /// через <see cref="SetOffer"/>/<see cref="AddRemoteIce"/>.
    /// </para>
    /// <para>
    /// В WebGL всё однопоточно: коллбеки из JS приходят на главном Unity-потоке,
    /// поэтому события уже main-thread-safe. Коллбеки статические
    /// (<c>[MonoPInvokeCallback]</c>), экземпляр находится по целочисленному
    /// handle через статическую таблицу <c>Instances</c>.
    /// </para>
    /// <para>
    /// Вне WebGL это затычка: WebRTC-апгрейд только для браузера, натив/редактор
    /// остаются на WebSocket. <see cref="IsSupported"/> на не-WebGL = <c>false</c>.
    /// </para>
    /// </remarks>
    public sealed class WebGLWebRtc : IDisposable
    {
        /// <summary>DataChannel принёс бинарные данные (игровой трафик). Главный поток.</summary>
        public event Action<byte[]> OnData;

        /// <summary>
        /// Готов локальный <c>answer</c> SDP — релейнуть релею как <c>0x10</c>
        /// <c>{"t":"answer","sdp":...}</c>. Аргумент — SDP-строка.
        /// </summary>
        public event Action<string> OnLocalAnswer;

        /// <summary>
        /// Готов локальный ICE-кандидат — релейнуть релею как <c>0x10</c>
        /// <c>{"t":"ice",...}</c>. Аргумент — JSON
        /// <c>{"candidate":..,"sdpMid":..,"sdpMLineIndex":..}</c>.
        /// </summary>
        public event Action<string> OnLocalIce;

        /// <summary>Доступен ли WebRTC-транспорт на этой платформе (только WebGL-сборка).</summary>
#if UNITY_WEBGL && !UNITY_EDITOR
        public static bool IsSupported => true;
#else
        public static bool IsSupported => false;
#endif

#if UNITY_WEBGL && !UNITY_EDITOR
        private delegate void OpenCb(int id);
        private delegate void DataCb(int id, IntPtr ptr, int len);
        private delegate void StringCb(int id, IntPtr strPtr);
        private delegate void ErrorCb(int id);

        [DllImport("__Internal")]
        private static extern int VhrRtc_Start(
            OpenCb onOpen, DataCb onData, StringCb onAnswer, StringCb onIce, ErrorCb onError);

        [DllImport("__Internal")]
        private static extern void VhrRtc_SetOffer(int id, string sdp);

        [DllImport("__Internal")]
        private static extern void VhrRtc_AddIce(int id, string candidateJson);

        [DllImport("__Internal")]
        private static extern void VhrRtc_Send(int id, byte[] data, int len);

        [DllImport("__Internal")]
        private static extern int VhrRtc_IsOpen(int id);

        [DllImport("__Internal")]
        private static extern void VhrRtc_Close(int id);

        // Освобождение буфера/строк кучи. Через обёртку VhrFree (jslib) над _free —
        // прямой EntryPoint="free" под IL2CPP/WebGL конфликтует с libc free(void*).
        [DllImport("__Internal", EntryPoint = "VhrFree")]
        private static extern void VhrFree(IntPtr ptr);

        // handle -> экземпляр. Коллбеки статические, инстанс находим тут.
        private static readonly Dictionary<int, WebGLWebRtc> Instances =
            new Dictionary<int, WebGLWebRtc>();

        private int _id;
        private bool _disposed;
        private TaskCompletionSource<bool> _openTcs;

        /// <summary>Открыт ли DataChannel (готов к игровому трафику).</summary>
        public bool IsOpen => _id != 0 && VhrRtc_IsOpen(_id) == 1;

        /// <summary>
        /// Заводит <c>RTCPeerConnection</c> и ждёт открытия DataChannel или сбоя/
        /// таймаута. Сразу после старта вызывающий должен слать релею
        /// <c>{"t":"webrtc-start"}</c>; на пришедший offer вызвать
        /// <see cref="SetOffer"/>, на ICE — <see cref="AddRemoteIce"/>; локальные
        /// answer/ICE придут через <see cref="OnLocalAnswer"/>/<see cref="OnLocalIce"/>.
        /// </summary>
        /// <param name="timeout">Дедлайн на открытие канала (после — fallback на WS).</param>
        /// <param name="ct">Токен отмены.</param>
        /// <returns><c>true</c>, если DataChannel открылся; иначе <c>false</c>.</returns>
        public async Task<bool> StartAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            if (_id != 0)
                return IsOpen;

            _openTcs = new TaskCompletionSource<bool>();

            _id = VhrRtc_Start(OnOpenStatic, OnDataStatic, OnAnswerStatic, OnIceStatic, OnErrorStatic);
            if (_id == 0)
            {
                // WebRTC недоступен в этом браузере — сразу fallback.
                return false;
            }

            Instances[_id] = this;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            using (timeoutCts.Token.Register(() => _openTcs?.TrySetResult(false)))
            {
                bool opened;
                try { opened = await _openTcs.Task.ConfigureAwait(false); }
                catch { opened = false; }
                return opened && IsOpen;
            }
        }

        /// <summary>Скармливает релейный offer SDP (из <c>0x10</c> <c>{"t":"offer"}</c>).</summary>
        public void SetOffer(string sdp)
        {
            if (_id == 0 || string.IsNullOrEmpty(sdp)) return;
            VhrRtc_SetOffer(_id, sdp);
        }

        /// <summary>Добавляет удалённый ICE-кандидат (JSON из <c>0x10</c> <c>{"t":"ice"}</c>).</summary>
        public void AddRemoteIce(string candidateJson)
        {
            if (_id == 0 || string.IsNullOrEmpty(candidateJson)) return;
            VhrRtc_AddIce(_id, candidateJson);
        }

        /// <summary>Шлёт бинарный игровой фрейм по DataChannel. No-op, если канал не открыт.</summary>
        public void Send(byte[] data)
        {
            if (_id == 0 || data == null || data.Length == 0) return;
            VhrRtc_Send(_id, data, data.Length);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _openTcs?.TrySetResult(false);
            if (_id != 0)
            {
                try { VhrRtc_Close(_id); } catch { /* ignore */ }
                Instances.Remove(_id);
                _id = 0;
            }
        }

        [MonoPInvokeCallback(typeof(OpenCb))]
        private static void OnOpenStatic(int id)
        {
            if (!Instances.TryGetValue(id, out var self)) return;
            self._openTcs?.TrySetResult(true);
        }

        [MonoPInvokeCallback(typeof(DataCb))]
        private static void OnDataStatic(int id, IntPtr ptr, int len)
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
                if (ptr != IntPtr.Zero) VhrFree(ptr);
            }

            if (payload == null) return;
            if (!Instances.TryGetValue(id, out var self)) return;
            try { self.OnData?.Invoke(payload); } catch { /* обработчик игры */ }
        }

        [MonoPInvokeCallback(typeof(StringCb))]
        private static void OnAnswerStatic(int id, IntPtr strPtr)
        {
            var sdp = ReadAndFree(strPtr);
            if (sdp == null) return;
            if (!Instances.TryGetValue(id, out var self)) return;
            try { self.OnLocalAnswer?.Invoke(sdp); } catch { /* relay */ }
        }

        [MonoPInvokeCallback(typeof(StringCb))]
        private static void OnIceStatic(int id, IntPtr strPtr)
        {
            var json = ReadAndFree(strPtr);
            if (json == null) return;
            if (!Instances.TryGetValue(id, out var self)) return;
            try { self.OnLocalIce?.Invoke(json); } catch { /* relay */ }
        }

        [MonoPInvokeCallback(typeof(ErrorCb))]
        private static void OnErrorStatic(int id)
        {
            if (!Instances.TryGetValue(id, out var self)) return;
            // Сбой пира до открытия канала — разблокируем StartAsync как fallback.
            self._openTcs?.TrySetResult(false);
        }

        // Копирует UTF8-строку из кучи (jslib allocStr) и освобождает указатель.
        private static string ReadAndFree(IntPtr strPtr)
        {
            if (strPtr == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUTF8(strPtr); }
            finally { VhrFree(strPtr); }
        }
#else
        // Вне WebGL — затычка: WebRTC только в браузере. Тип существует во всех
        // таргетах, чтобы на него мог ссылаться VhrRelay без #if вокруг полей.
        public bool IsOpen => false;
        public Task<bool> StartAsync(TimeSpan timeout, CancellationToken ct = default) =>
            Task.FromResult(false);
        public void SetOffer(string sdp) { }
        public void AddRemoteIce(string candidateJson) { }
        public void Send(byte[] data) { }
        public void Dispose() { }
#endif
    }
}
