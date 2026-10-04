using System;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AOT;
#endif

namespace VhrGames.Sdk
{
    /// <summary>
    /// Мост к WebGL-плагину <c>VhrAdsBridge.jslib</c>: игра просит родительскую
    /// страницу VHR (<c>https://vhrgames.ru</c>) показать рекламу и получает
    /// события показа.
    /// <para>
    /// Контракт <c>postMessage</c> (см. <c>VhrAdsBridge.jslib</c>):
    /// iframe → родитель <c>{ type:'vhr:ads:show', kind:'interstitial'|'rewarded', requestId }</c>;
    /// родитель → iframe <c>{ type:'vhr:ads:opened', requestId }</c> и
    /// <c>{ type:'vhr:ads:result', requestId, status }</c>. Ответы принимаются
    /// только от <c>window.parent</c> с origin платформы (тот же фильтр, что у
    /// токена — <c>VhrSdkBridge.jslib</c>).
    /// </para>
    /// <para>
    /// Из JS в C# приходят только целые числа (handle, событие, статус, причина) —
    /// без маршалинга строк, тем же способом, что и у WebSocket-плагина
    /// (указатель на статический метод + <c>makeDynCall</c>). В WebGL всё
    /// однопоточно: колбэк приходит на главном потоке.
    /// </para>
    /// <para>Вне WebGL все методы — безопасные заглушки (<see cref="IsSupported"/> = <c>false</c>).</para>
    /// </summary>
    internal static class VhrWebGlAdsChannel
    {
        // Коды событий JS → C# (синхронно с VhrAdsBridge.jslib).
        internal const int EventOpened = 1;
        internal const int EventResult = 2;

        // Коды причин (синхронно с VhrAdsBridge.jslib). Статус кодируется
        // числом, равным значению VhrAdStatus.
        internal const int ReasonNone = 0;
        internal const int ReasonHostTimeout = 1; // хост не ответил (игра не на VHR / старая страница)
        internal const int ReasonShowTimeout = 2; // показ не завершился за отведённое время
        internal const int ReasonNoHost = 3;      // нет родительского окна
        internal const int ReasonBusy = 4;        // уже есть незавершённый запрос
        internal const int ReasonHostReported = 5; // статус прислал хост

        /// <summary>True только в реальной WebGL-сборке (не в редакторе).</summary>
        public static bool IsSupported =>
#if UNITY_WEBGL && !UNITY_EDITOR
            true;
#else
            false;
#endif

#if UNITY_WEBGL && !UNITY_EDITOR
        private delegate void AdsEventCb(int handle, int eventCode, int status, int reason);

        [DllImport("__Internal")]
        private static extern void VhrAds_Init(AdsEventCb cb);

        [DllImport("__Internal")]
        private static extern int VhrAds_IsHosted();

        [DllImport("__Internal")]
        private static extern int VhrAds_Show(string kind, string requestId, int ackTimeoutMs, int maxShowMs);

        // КРИТ: делегат колбэка держим в СТАТИЧЕСКОМ поле — иначе GC соберёт
        // временный делегат из method-group, и указатель в JS станет висячим
        // (тот же баг, что чинили в WebGLVhrSocket в v1.7.5).
        private static readonly AdsEventCb s_onEvent = OnEventStatic;

        // handle -> обработчик (событие, статус, причина).
        private static readonly Dictionary<int, Action<int, int, int>> Handlers =
            new Dictionary<int, Action<int, int, int>>();

        private static bool _initialized;

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            VhrAds_Init(s_onEvent);
            _initialized = true;
        }

        [MonoPInvokeCallback(typeof(AdsEventCb))]
        private static void OnEventStatic(int handle, int eventCode, int status, int reason)
        {
            if (!Handlers.TryGetValue(handle, out var handler)) return;
            if (eventCode == EventResult) Handlers.Remove(handle); // результат — последний для handle
            try { handler(eventCode, status, reason); }
            catch (Exception e) { UnityEngine.Debug.LogError("[VHR SDK] Ads: обработчик события упал: " + e); }
        }
#endif

        /// <summary>
        /// Есть ли шанс, что страница-хост — платформа VHR: игра открыта во фрейме,
        /// и (если браузер даёт <c>location.ancestorOrigins</c>) родитель — vhrgames.ru.
        /// Вне WebGL — <c>false</c>.
        /// </summary>
        public static bool IsHosted()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try { return VhrAds_IsHosted() == 1; }
            catch { return false; }
#else
            return false;
#endif
        }

        /// <summary>
        /// Отправляет хосту запрос показа. Возвращает handle (&gt; 0) или 0, если
        /// запрос отправить нельзя (нет родителя/плагина) — тогда <paramref name="onEvent"/>
        /// не вызовется. Иначе гарантирован ровно один <see cref="EventResult"/>
        /// (от хоста или по таймауту JS-стороны), перед ним — возможно <see cref="EventOpened"/>.
        /// </summary>
        /// <param name="kind"><c>"interstitial"</c> | <c>"rewarded"</c>.</param>
        /// <param name="requestId">Уникальный id запроса.</param>
        /// <param name="ackTimeoutMs">Сколько ждать первого ответа хоста.</param>
        /// <param name="maxShowMs">Страховочный лимит на весь показ после <c>opened</c>.</param>
        /// <param name="onEvent">(событие, статус, причина) — на главном потоке.</param>
        public static int Show(string kind, string requestId, int ackTimeoutMs, int maxShowMs,
            Action<int, int, int> onEvent)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                EnsureInitialized();
                int handle = VhrAds_Show(kind, requestId, ackTimeoutMs, maxShowMs);
                if (handle > 0) Handlers[handle] = onEvent;
                return handle;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[VHR SDK] Ads: плагин VhrAdsBridge недоступен: " + e.Message);
                return 0;
            }
#else
            return 0;
#endif
        }
    }
}
