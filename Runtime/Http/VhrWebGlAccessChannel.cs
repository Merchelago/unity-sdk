using System;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AOT;
#endif

namespace VhrGames.Sdk
{
    /// <summary>
    /// Мост к WebGL-плагину <c>VhrAccessBridge.jslib</c> (SDK 1.13.0+): доступ к премиум-игре
    /// через страницу-хост VHR (<c>https://vhrgames.ru</c>).
    /// <para>
    /// Контракт <c>postMessage</c> (см. <c>VhrAccessBridge.jslib</c>):
    /// iframe → родитель <c>{ type:'vhr:access:get', requestId }</c>;
    /// родитель → iframe <c>{ type:'vhr:access:state', requestId, state }</c>;
    /// родитель → iframe (без запроса, при смене доступа) <c>{ type:'vhr:access:changed', state }</c>;
    /// iframe → родитель <c>{ type:'vhr:access:purchase', requestId }</c>;
    /// родитель → iframe <c>{ type:'vhr:access:purchase-result', requestId, result, state }</c>,
    /// <c>result</c> = <c>purchased|subscribed|requested|cancelled|unavailable</c>.
    /// Ответы принимаются только от <c>window.parent</c> с origin платформы. Платформенный JWT
    /// игра не получает никогда.
    /// </para>
    /// <para>
    /// JS → C#: целые числа через статический делегат (<c>makeDynCall('viiii')</c>), состояние —
    /// строкой JSON (уже нормализованной в JS) через <c>VhrAccess_TakePayload</c>, как у экономики.
    /// Всё на главном потоке; результат никогда не приходит синхронно внутри вызова.
    /// </para>
    /// <para>Вне WebGL все методы — безопасные заглушки (<see cref="IsSupported"/> = <c>false</c>).</para>
    /// </summary>
    internal static class VhrWebGlAccessChannel
    {
        // Коды событий JS → C# (синхронно с VhrAccessBridge.jslib).
        internal const int EventState = 1;          // ответ на vhr:access:get
        internal const int EventPurchaseResult = 2; // ответ на vhr:access:purchase
        internal const int EventChanged = 3;        // vhr:access:changed (без запроса)

        // Статусы результата покупки (синхронно с VhrAccessBridge.jslib) = значения VhrAccessPurchaseStatus.
        // Для EventState/EventChanged: 1 — состояние есть, 0 — нет (таймаут/мусор).

        // Причины (синхронно с VhrAccessBridge.jslib).
        internal const int ReasonNone = 0;
        internal const int ReasonTimeout = 1;      // хост не ответил за отведённое время
        internal const int ReasonNoHost = 2;       // нет родительского окна
        internal const int ReasonBusy = 3;         // дубликат requestId
        internal const int ReasonHostReported = 4; // ответ прислал хост
        internal const int ReasonPostFailed = 5;   // postMessage бросил исключение

        /// <summary>Нормализованное в JS состояние/результат от хоста (разбирается JsonUtility).</summary>
        [Serializable]
        internal sealed class HostPayload
        {
            public bool hasState;
            public string gameType;
            public string access;
            public string source;
            public bool hasPrice;
            public int price;
            public bool inSubscription;
            public bool hasDemo;
            public string result;
            public string error;

            /// <summary>Состояние доступа из payload или <c>null</c>.</summary>
            internal VhrAccessState ToState() =>
                hasState
                    ? VhrAccessCodec.FromParts(gameType, access, source, hasPrice ? price : (int?)null,
                        inSubscription, hasDemo, VhrAccessOrigin.Host)
                    : null;
        }

        /// <summary>
        /// Хост сообщил о смене доступа (<c>vhr:access:changed</c>): покупка на сайте, подписка,
        /// одобрение родителя, решение администратора. На главном потоке. Вне WebGL не возникает.
        /// </summary>
#pragma warning disable CS0067 // вне WebGL событие не возникает
        internal static event Action<HostPayload> HostAccessChanged;
#pragma warning restore CS0067

        /// <summary>True только в реальной WebGL-сборке (не в редакторе).</summary>
        public static bool IsSupported =>
#if UNITY_WEBGL && !UNITY_EDITOR
            true;
#else
            false;
#endif

#if UNITY_WEBGL && !UNITY_EDITOR
        private delegate void AccessEventCb(int handle, int eventCode, int status, int reason);

        [DllImport("__Internal")]
        private static extern void VhrAccess_Init(AccessEventCb cb);

        [DllImport("__Internal")]
        private static extern int VhrAccess_IsHosted();

        [DllImport("__Internal")]
        private static extern int VhrAccess_Get(string requestId, int timeoutMs);

        [DllImport("__Internal")]
        private static extern int VhrAccess_Purchase(string requestId, int timeoutMs);

        [DllImport("__Internal")]
        private static extern string VhrAccess_TakePayload(int handle);

        // КРИТ: делегат колбэка держим в СТАТИЧЕСКОМ поле — иначе GC соберёт
        // временный делегат из method-group, и указатель в JS станет висячим
        // (тот же баг, что чинили в WebGLVhrSocket в v1.7.5).
        private static readonly AccessEventCb s_onEvent = OnEventStatic;

        // handle -> обработчик ответа (статус, причина, детали).
        private static readonly Dictionary<int, Action<int, int, HostPayload>> Handlers =
            new Dictionary<int, Action<int, int, HostPayload>>();

        private static bool _initialized;

        [MonoPInvokeCallback(typeof(AccessEventCb))]
        private static void OnEventStatic(int handle, int eventCode, int status, int reason)
        {
            HostPayload payload = null;
            try { payload = Parse(VhrAccess_TakePayload(handle)); }
            catch (Exception e) { UnityEngine.Debug.LogWarning("[VHR SDK] Access: не удалось прочитать детали события: " + e.Message); }
            payload ??= new HostPayload();

            if (eventCode == EventChanged)
            {
                if (!payload.hasState) return;
                try { HostAccessChanged?.Invoke(payload); }
                catch (Exception e) { UnityEngine.Debug.LogError("[VHR SDK] Access: обработчик смены доступа упал: " + e); }
                return;
            }

            if (eventCode != EventState && eventCode != EventPurchaseResult) return;
            if (!Handlers.TryGetValue(handle, out var handler)) return;
            Handlers.Remove(handle); // ответ — последний для handle
            try { handler(status, reason, payload); }
            catch (Exception e) { UnityEngine.Debug.LogError("[VHR SDK] Access: обработчик ответа упал: " + e); }
        }

        private static HostPayload Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return UnityEngine.JsonUtility.FromJson<HostPayload>(json); }
            catch { return null; }
        }
#endif

        /// <summary>
        /// Идемпотентно ставит слушатель сообщений хоста (ответы и <c>vhr:access:changed</c>).
        /// Вызывается при создании сервиса доступа, чтобы смена доступа доходила до игры сразу. Вне WebGL — no-op.
        /// </summary>
        public static void EnsureInitialized()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (_initialized) return;
            try
            {
                VhrAccess_Init(s_onEvent);
                _initialized = true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[VHR SDK] Access: плагин VhrAccessBridge недоступен: " + e.Message);
            }
#endif
        }

        /// <summary>
        /// Открыта ли игра во фрейме страницы VHR (с учётом <c>location.ancestorOrigins</c>,
        /// где браузер его даёт). Вне WebGL — <c>false</c>.
        /// </summary>
        public static bool IsHosted()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try { return VhrAccess_IsHosted() == 1; }
            catch { return false; }
#else
            return false;
#endif
        }

        /// <summary>
        /// Спрашивает у хоста текущее состояние доступа (<c>vhr:access:get</c>). Возвращает handle
        /// (&gt; 0) или 0, если запрос отправить нельзя — тогда <paramref name="onResult"/> не
        /// вызовется. Иначе гарантирован ровно один вызов (от хоста или по таймауту JS-стороны;
        /// статус 1 — состояние есть).
        /// </summary>
        public static int Get(string requestId, int timeoutMs, Action<int, int, HostPayload> onResult)
            => Send(false, requestId, timeoutMs, onResult);

        /// <summary>
        /// Просит хост открыть окно покупки полной версии (<c>vhr:access:purchase</c>). Handle или 0
        /// (как у <see cref="Get"/>); статус ответа — значение <see cref="VhrAccessPurchaseStatus"/>.
        /// </summary>
        public static int Purchase(string requestId, int timeoutMs, Action<int, int, HostPayload> onResult)
            => Send(true, requestId, timeoutMs, onResult);

        private static int Send(bool purchase, string requestId, int timeoutMs, Action<int, int, HostPayload> onResult)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                EnsureInitialized();
                int handle = purchase ? VhrAccess_Purchase(requestId, timeoutMs) : VhrAccess_Get(requestId, timeoutMs);
                if (handle > 0) Handlers[handle] = onResult;
                return handle;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[VHR SDK] Access: плагин VhrAccessBridge недоступен: " + e.Message);
                return 0;
            }
#else
            return 0;
#endif
        }
    }
}
