using System.Collections.Generic;
using UnityEngine;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Скрытый служебный объект: раз в кадр (в самом конце LateUpdate) даёт каждому
    /// <see cref="VhrRelay"/> отправить накопленную за кадр пачку, послать пинг и
    /// пересчитать статистику. Создаётся сам при первом подключении, переживает
    /// смену сцен. Только главный поток.
    /// </summary>
    [AddComponentMenu("")]
    [DefaultExecutionOrder(32000)]
    internal sealed class VhrRelayPump : MonoBehaviour
    {
        private static VhrRelayPump _instance;
        private static readonly List<VhrRelay> Relays = new List<VhrRelay>();
        private static readonly List<VhrRelay> Snapshot = new List<VhrRelay>();

        /// <summary>Есть ли насос (в режиме редактирования без Play его нет — батчинг тогда выключен).</summary>
        internal static bool Available => _instance != null;

        /// <summary>
        /// Идёт кадр Unity (между началом Update и концом LateUpdate). Вне кадра — например,
        /// в WebGL обработчик <c>OnData</c> вызывается из события сокета между кадрами — пачку
        /// копить нельзя: она ушла бы только в конце СЛЕДУЮЩЕГО кадра (до ~16 мс задержки
        /// ответа). Тогда <see cref="VhrRelay"/> отправляет сразу. Из FixedUpdate — тоже сразу.
        /// </summary>
        internal static bool InFrame;

        /// <summary>Регистрирует relay; создаёт насос при необходимости. Вызывать с главного потока.</summary>
        internal static bool Register(VhrRelay relay)
        {
            if (!Application.isPlaying) return false;
            if (_instance == null)
            {
                var go = new GameObject("VHR Relay Pump") { hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave };
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<VhrRelayPump>();
                go.AddComponent<VhrRelayFrameStart>();
            }
            if (!Relays.Contains(relay)) Relays.Add(relay);
            return true;
        }

        internal static void Unregister(VhrRelay relay) => Relays.Remove(relay);

        private void LateUpdate()
        {
            try { TickAll(); }
            finally { InFrame = false; } // дальше — между кадрами: отправки сразу
        }

        private static void TickAll()
        {
            if (Relays.Count == 0) return;
            Snapshot.Clear();
            Snapshot.AddRange(Relays);
            foreach (var r in Snapshot)
            {
                try { r.Tick(); }
                catch (System.Exception e) { Debug.LogException(e); }
            }
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
            InFrame = false;
        }
    }
}
