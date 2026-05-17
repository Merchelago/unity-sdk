using System;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace VhrGames.Sdk
{
    /// <summary>
    /// Мост к WebGL-плагину <c>VhrSdkBridge.jslib</c> для жизненного цикла JWT
    /// игрока. Токен игрока живёт ~15 минут, а игровые сессии длиннее, поэтому
    /// родительская страница (<c>https://vhrgames.ru</c>) присылает свежий токен
    /// через <c>postMessage</c>, а SDK умеет его запросить при истечении.
    /// <para>
    /// Контракт сообщений (см. <c>VhrSdkBridge.jslib</c>):
    /// родитель → iframe <c>{ type:'vhr:sdk:token', token:'&lt;jwt&gt;' }</c>;
    /// iframe → родитель <c>{ type:'vhr:sdk:token-request' }</c>. Принимаются
    /// только сообщения с <c>event.origin</c> ∈ {<c>https://vhrgames.ru</c>,
    /// <c>http://localhost:5173</c>}.
    /// </para>
    /// <para>
    /// Вне WebGL (нативные / редакторные сборки) все методы — безопасные
    /// no-op: <see cref="GetLatestToken"/> вернёт <c>null</c>,
    /// <see cref="RequestRefresh"/> ничего не делает. Там обновление токена —
    /// ответственность вашего <see cref="VhrSdkOptions.TokenProvider"/>.
    /// </para>
    /// </summary>
    public static class VhrWebGlTokenChannel
    {
        /// <summary>True только в реальной WebGL-сборке (не в редакторе).</summary>
        public static bool IsSupported =>
#if UNITY_WEBGL && !UNITY_EDITOR
            true;
#else
            false;
#endif

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void VhrSdk_Init();

        [DllImport("__Internal")]
        private static extern string VhrSdk_GetLatestToken();

        [DllImport("__Internal")]
        private static extern void VhrSdk_RequestToken();

        private static bool _initialized;
#endif

        /// <summary>
        /// Идемпотентно устанавливает слушатель <c>postMessage</c> в JS. Вызывать
        /// один раз при инициализации SDK. Вне WebGL — no-op.
        /// </summary>
        public static void EnsureInitialized()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (_initialized) return;
            try
            {
                VhrSdk_Init();
                _initialized = true;
            }
            catch
            {
                // Плагин недоступен (например, тест-окружение) — деградируем тихо.
            }
#endif
        }

        /// <summary>
        /// Последний токен, присланный родительской страницей через
        /// <c>postMessage</c>, либо <c>null</c>, если ничего не получено / не WebGL.
        /// </summary>
        public static string GetLatestToken()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                var t = VhrSdk_GetLatestToken();
                return string.IsNullOrEmpty(t) ? null : t;
            }
            catch
            {
                return null;
            }
#else
            return null;
#endif
        }

        /// <summary>
        /// Просит родительскую страницу обновить и прислать свежий токен
        /// (<c>postMessage { type:'vhr:sdk:token-request' }</c>). Вне WebGL — no-op.
        /// </summary>
        public static void RequestRefresh()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                EnsureInitialized();
                VhrSdk_RequestToken();
            }
            catch
            {
                // Нет родителя / кросс-доменные ограничения — деградируем тихо.
            }
#endif
        }
    }
}
