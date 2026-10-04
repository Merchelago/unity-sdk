using System;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AOT;
#endif

namespace VhrGames.Sdk
{
    /// <summary>
    /// Мост к WebGL-плагину <c>VhrEconomyBridge.jslib</c>: подтверждение покупки
    /// игроком в окне платформы, открытие окна пополнения и новости о балансе от
    /// страницы-хоста VHR (<c>https://vhrgames.ru</c>).
    /// <para>
    /// Контракт <c>postMessage</c> (см. <c>VhrEconomyBridge.jslib</c>):
    /// iframe → родитель <c>{ type:'vhr:purchase:confirm', intentId, requestId }</c>;
    /// родитель → iframe <c>{ type:'vhr:purchase:result', requestId, intentId, status, balance?, purchaseId? }</c>;
    /// iframe → родитель <c>{ type:'vhr:topup:open' }</c>;
    /// родитель → iframe <c>{ type:'vhr:balance:changed', balance }</c>.
    /// Ответы принимаются только от <c>window.parent</c> с origin платформы.
    /// </para>
    /// <para>
    /// JS → C#: целые числа через статический делегат (<c>makeDynCall('viiii')</c>),
    /// детали — строкой JSON через <c>VhrEconomy_TakePayload</c> (тем же способом
    /// возврата строки, что и токен в <c>VhrSdkBridge.jslib</c>). Всё на главном потоке.
    /// </para>
    /// <para>Вне WebGL все методы — безопасные заглушки (<see cref="IsSupported"/> = <c>false</c>).</para>
    /// </summary>
    internal static class VhrWebGlEconomyChannel
    {
        // Коды событий JS → C# (синхронно с VhrEconomyBridge.jslib).
        internal const int EventPurchaseResult = 1;
        internal const int EventBalanceChanged = 2;

        // Статусы результата покупки (синхронно с VhrEconomyBridge.jslib).
        internal const int StatusError = 0;
        internal const int StatusCompleted = 1;
        internal const int StatusCancelled = 2;
        internal const int StatusInsufficientFunds = 3;
        internal const int StatusExpired = 4;

        // Причины (синхронно с VhrEconomyBridge.jslib).
        internal const int ReasonNone = 0;
        internal const int ReasonTimeout = 1;      // хост не ответил за отведённое время
        internal const int ReasonNoHost = 2;       // нет родительского окна
        internal const int ReasonBusy = 3;         // дубликат requestId
        internal const int ReasonHostReported = 4; // статус прислал хост
        internal const int ReasonPostFailed = 5;   // postMessage бросил исключение

        /// <summary>Детали события от хоста (разбираются из JSON).</summary>
        [Serializable]
        internal sealed class HostPayload
        {
            public string status;
            public bool hasBalance;
            public long balance;
            public string purchaseId;
            public string intentId;
            public string error;
        }

        /// <summary>
        /// Хост сообщил новый баланс игрока (<c>vhr:balance:changed</c>), например после
        /// пополнения на платформе. На главном потоке. Вне WebGL не возникает.
        /// </summary>
#pragma warning disable CS0067 // вне WebGL событие не возникает
        internal static event Action<long> HostBalanceChanged;
#pragma warning restore CS0067

        /// <summary>True только в реальной WebGL-сборке (не в редакторе).</summary>
        public static bool IsSupported =>
#if UNITY_WEBGL && !UNITY_EDITOR
            true;
#else
            false;
#endif

#if UNITY_WEBGL && !UNITY_EDITOR
        private delegate void EconomyEventCb(int handle, int eventCode, int status, int reason);

        [DllImport("__Internal")]
        private static extern void VhrEconomy_Init(EconomyEventCb cb);

        [DllImport("__Internal")]
        private static extern int VhrEconomy_IsHosted();

        [DllImport("__Internal")]
        private static extern int VhrEconomy_Confirm(string intentId, string requestId, int timeoutMs);

        [DllImport("__Internal")]
        private static extern string VhrEconomy_TakePayload(int handle);

        [DllImport("__Internal")]
        private static extern int VhrEconomy_OpenTopUp();

        // КРИТ: делегат колбэка держим в СТАТИЧЕСКОМ поле — иначе GC соберёт
        // временный делегат из method-group, и указатель в JS станет висячим
        // (тот же баг, что чинили в WebGLVhrSocket в v1.7.5).
        private static readonly EconomyEventCb s_onEvent = OnEventStatic;

        // handle -> обработчик результата покупки (статус, причина, детали).
        private static readonly Dictionary<int, Action<int, int, HostPayload>> Handlers =
            new Dictionary<int, Action<int, int, HostPayload>>();

        private static bool _initialized;

        [MonoPInvokeCallback(typeof(EconomyEventCb))]
        private static void OnEventStatic(int handle, int eventCode, int status, int reason)
        {
            HostPayload payload = null;
            try { payload = Parse(VhrEconomy_TakePayload(handle)); }
            catch (Exception e) { UnityEngine.Debug.LogWarning("[VHR SDK] Economy: не удалось прочитать детали события: " + e.Message); }
            payload ??= new HostPayload();

            if (eventCode == EventBalanceChanged)
            {
                if (!payload.hasBalance) return;
                try { HostBalanceChanged?.Invoke(payload.balance); }
                catch (Exception e) { UnityEngine.Debug.LogError("[VHR SDK] Economy: обработчик баланса упал: " + e); }
                return;
            }

            if (eventCode != EventPurchaseResult) return;
            if (!Handlers.TryGetValue(handle, out var handler)) return;
            Handlers.Remove(handle); // результат — последний для handle
            try { handler(status, reason, payload); }
            catch (Exception e) { UnityEngine.Debug.LogError("[VHR SDK] Economy: обработчик покупки упал: " + e); }
        }

        private static HostPayload Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return UnityEngine.JsonUtility.FromJson<HostPayload>(json); }
            catch { return null; }
        }
#endif

        /// <summary>
        /// Идемпотентно ставит слушатель сообщений хоста (результаты покупок и
        /// <c>vhr:balance:changed</c>). Вызывается при создании сервиса экономики,
        /// чтобы новости о балансе приходили ещё до первой покупки. Вне WebGL — no-op.
        /// </summary>
        public static void EnsureInitialized()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (_initialized) return;
            try
            {
                VhrEconomy_Init(s_onEvent);
                _initialized = true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[VHR SDK] Economy: плагин VhrEconomyBridge недоступен: " + e.Message);
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
            try { return VhrEconomy_IsHosted() == 1; }
            catch { return false; }
#else
            return false;
#endif
        }

        /// <summary>
        /// Просит хост показать игроку окно подтверждения покупки. Возвращает handle
        /// (&gt; 0) или 0, если запрос отправить нельзя (нет родителя/плагина) — тогда
        /// <paramref name="onResult"/> не вызовется. Иначе гарантирован ровно один вызов
        /// (от хоста или по таймауту JS-стороны).
        /// </summary>
        /// <param name="intentId">Id намерения покупки от сервера.</param>
        /// <param name="requestId">Уникальный id запроса.</param>
        /// <param name="timeoutMs">Сколько ждать ответа хоста, затем <c>expired</c>.</param>
        /// <param name="onResult">(статус, причина, детали) — на главном потоке.</param>
        public static int Confirm(string intentId, string requestId, int timeoutMs,
            Action<int, int, HostPayload> onResult)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                EnsureInitialized();
                int handle = VhrEconomy_Confirm(intentId, requestId, timeoutMs);
                if (handle > 0) Handlers[handle] = onResult;
                return handle;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[VHR SDK] Economy: плагин VhrEconomyBridge недоступен: " + e.Message);
                return 0;
            }
#else
            return 0;
#endif
        }

        /// <summary>
        /// Просит сайт открыть окно пополнения счёта (<c>vhr:topup:open</c>).
        /// <c>true</c> — запрос отправлен. Вне WebGL — <c>false</c>.
        /// </summary>
        public static bool OpenTopUp()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                EnsureInitialized();
                return VhrEconomy_OpenTopUp() == 1;
            }
            catch
            {
                return false;
            }
#else
            return false;
#endif
        }
    }
}
