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
    /// (<see cref="IVhrSaves.FlushAsync"/>).
    /// <para>
    /// Из JS приходит только код события — тем же способом, что у рекламы и
    /// WebSocket (указатель на статический метод + <c>makeDynCall</c>). Колбэк
    /// вызывается синхронно внутри DOM-события, на главном потоке.
    /// </para>
    /// <para>Вне WebGL — безопасная заглушка: событие <see cref="PageHidden"/> не приходит.</para>
    /// </summary>
    internal static class VhrWebGlLifecycleChannel
    {
        /// <summary>Страница скрыта (<c>visibilitychange</c> → <c>hidden</c>).</summary>
        internal const int EventHidden = 1;

        /// <summary>Страница выгружается (<c>pagehide</c>: закрытие вкладки, переход).</summary>
        internal const int EventPageHide = 2;

#pragma warning disable CS0067 // вне WebGL событие не вызывается
        /// <summary>Страница скрыта или выгружается; аргумент — <see cref="EventHidden"/> / <see cref="EventPageHide"/>.</summary>
        internal static event Action<int> PageHidden;
#pragma warning restore CS0067

#if UNITY_WEBGL && !UNITY_EDITOR
        private delegate void LifecycleCb(int code);

        [DllImport("__Internal")]
        private static extern void VhrLifecycle_Init(LifecycleCb cb);

        // КРИТ: делегат колбэка держим в СТАТИЧЕСКОМ поле — иначе GC соберёт
        // временный делегат из method-group, и указатель в JS станет висячим
        // (тот же баг, что чинили в WebGLVhrSocket в v1.7.5).
        private static readonly LifecycleCb s_onEvent = OnEventStatic;

        private static bool _initialized;

        [MonoPInvokeCallback(typeof(LifecycleCb))]
        private static void OnEventStatic(int code)
        {
            try { PageHidden?.Invoke(code); }
            catch (Exception e) { UnityEngine.Debug.LogError("[VHR SDK] Lifecycle: обработчик события упал: " + e); }
        }
#endif

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
