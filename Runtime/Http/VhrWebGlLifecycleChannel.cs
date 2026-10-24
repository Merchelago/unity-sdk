using System;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
using AOT;
#endif

namespace VhrGames.Sdk
{
    /// <summary>
    /// Мост к WebGL-плагину <c>VhrLifecycleBridge.jslib</c> (SDK 1.11.0+): страница
    /// игры скрыта (<c>visibilitychange</c> → <c>hidden</c>: другая вкладка, браузер
    /// свёрнут, игрок ушёл из приложения на телефоне) или выгружается (<c>pagehide</c>).
    /// По этим событиям облачные сохранения сразу отправляют накопленное
    /// (<see cref="IVhrSaves.FlushAsync"/>), а локальные файлы SDK синхронизируются в IndexedDB.
    /// <para>
    /// SDK 1.12.0+: ещё возврат во вкладку (<c>visible</c>) и сеть браузера (<c>online</c> /
    /// <c>offline</c>) — по ним очередь отложенных операций (<see cref="IVhrConnection"/>) и
    /// автосохранение сразу пробуют отправить накопленное без связи.
    /// </para>
    /// <para>
    /// Из JS приходит только код события — тем же способом, что у рекламы и
    /// WebSocket (указатель на статический метод + <c>makeDynCall</c>). Колбэк
    /// вызывается синхронно внутри DOM-события, на главном потоке.
    /// </para>
    /// <para>Вне WebGL — безопасная заглушка: события не приходят, браузер всегда «в сети».</para>
    /// </summary>
    internal static class VhrWebGlLifecycleChannel
    {
        /// <summary>Страница скрыта (<c>visibilitychange</c> → <c>hidden</c>).</summary>
        internal const int EventHidden = 1;

        /// <summary>Страница выгружается (<c>pagehide</c>: закрытие вкладки, переход).</summary>
        internal const int EventPageHide = 2;

        /// <summary>Страница снова видна (<c>visibilitychange</c> → <c>visible</c>), SDK 1.12.0+.</summary>
        internal const int EventVisible = 3;

        /// <summary>Браузер снова в сети (<c>online</c>), SDK 1.12.0+.</summary>
        internal const int EventOnline = 4;

        /// <summary>Браузер потерял сеть (<c>offline</c>), SDK 1.12.0+.</summary>
        internal const int EventOffline = 5;

#pragma warning disable CS0067 // вне WebGL события не вызываются
        /// <summary>Страница скрыта или выгружается; аргумент — <see cref="EventHidden"/> / <see cref="EventPageHide"/>.</summary>
        internal static event Action<int> PageHidden;

        /// <summary>Страница снова видна (игрок вернулся во вкладку / в приложение).</summary>
        internal static event Action PageVisible;

        /// <summary>Сеть браузера: <c>true</c> — <c>online</c>, <c>false</c> — <c>offline</c>.</summary>
        internal static event Action<bool> BrowserOnlineChanged;
#pragma warning restore CS0067

#if UNITY_WEBGL && !UNITY_EDITOR
        private delegate void LifecycleCb(int code);

        [DllImport("__Internal")]
        private static extern void VhrLifecycle_Init(LifecycleCb cb);

        [DllImport("__Internal")]
        private static extern int VhrLifecycle_IsOnline();

        // КРИТ: делегат колбэка держим в СТАТИЧЕСКОМ поле — иначе GC соберёт
        // временный делегат из method-group, и указатель в JS станет висячим
        // (тот же баг, что чинили в WebGLVhrSocket в v1.7.5).
        private static readonly LifecycleCb s_onEvent = OnEventStatic;

        private static bool _initialized;
        private static bool _isOnlineBroken;

        [MonoPInvokeCallback(typeof(LifecycleCb))]
        private static void OnEventStatic(int code)
        {
            try
            {
                switch (code)
                {
                    case EventHidden:
                    case EventPageHide:
                        PageHidden?.Invoke(code);
                        break;
                    case EventVisible:
                        PageVisible?.Invoke();
                        break;
                    case EventOnline:
                        BrowserOnlineChanged?.Invoke(true);
                        break;
                    case EventOffline:
                        BrowserOnlineChanged?.Invoke(false);
                        break;
                }
            }
            catch (Exception e) { UnityEngine.Debug.LogError("[VHR SDK] Lifecycle: обработчик события упал: " + e); }
        }
#endif

        /// <summary>
        /// <c>false</c> — браузер точно офлайн (<c>navigator.onLine === false</c>): запросы к серверу
        /// бессмысленны. <c>true</c> — сеть, по мнению браузера, есть (связь с серверами VHR это не
        /// гарантирует). Вне WebGL — всегда <c>true</c>.
        /// </summary>
        internal static bool IsBrowserOnline
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                if (_isOnlineBroken) return true;
                try { return VhrLifecycle_IsOnline() != 0; }
                catch
                {
                    // Старый/чужой jslib без функции — считаем «в сети», решают ответы сервера.
                    _isOnlineBroken = true;
                    return true;
                }
#else
                return true;
#endif
            }
        }

        /// <summary>Идемпотентно вешает слушатели страницы. Вне WebGL — no-op.</summary>
        internal static void EnsureInitialized()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (_initialized) return;
            try
            {
                VhrLifecycle_Init(s_onEvent);
                _initialized = true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[VHR SDK] Lifecycle: плагин VhrLifecycleBridge недоступен: " + e.Message);
            }
#endif
        }
    }
}
