using System;
using UnityEngine;
using UnityEngine.Events;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Drop-in компонент для <b>простого мультиплеера через релей</b> (без
    /// серверной сборки). Перетащите его на GameObject, укажите код лобби — на
    /// <see cref="Start"/> он подключится к платформенному релею
    /// (<see cref="VhrSdk.Relay"/>), войдёт в комнату и пробросит события релея
    /// как UnityEvents (для инспектора) и как обычные C#-события.
    /// </summary>
    /// <remarks>
    /// Требует, чтобы SDK был инициализирован (<see cref="VhrSdk.InitializeAsync"/>)
    /// — отсюда берётся <see cref="VhrSdkOptions.GameId"/> и адрес релея.
    /// Если <see cref="connectOnStart"/> выключить, вызовите <see cref="Connect"/>
    /// вручную позже. Всё null-safe; ошибки логируются через
    /// <see cref="Debug.LogWarning"/> и не валят сцену.
    /// </remarks>
    [AddComponentMenu("VHR/VHR Relay Bootstrap (simple multiplayer)")]
    [DisallowMultipleComponent]
    public sealed class VhrRelayBootstrap : MonoBehaviour
    {
        [Tooltip("Код лобби. Игроки с одинаковым кодом попадают в одну комнату. " +
                 "По умолчанию \"main\".")]
        [SerializeField] private string lobbyCode = "main";

        [Tooltip("Подключаться к релею автоматически в Start.")]
        [SerializeField] private bool connectOnStart = true;

        /// <summary>UnityEvent: вход в комнату подтверждён (selfId, peers).</summary>
        [Serializable] public sealed class JoinedEvent : UnityEvent<int, int[]> { }

        /// <summary>UnityEvent: пришли данные (senderId, bytes).</summary>
        [Serializable] public sealed class DataEvent : UnityEvent<int, byte[]> { }

        /// <summary>UnityEvent: один int-параметр (peerId).</summary>
        [Serializable] public sealed class PeerEvent : UnityEvent<int> { }

        [Header("Events")]
        public JoinedEvent onJoined = new JoinedEvent();
        public DataEvent onData = new DataEvent();
        public PeerEvent onPeerJoined = new PeerEvent();
        public PeerEvent onPeerLeft = new PeerEvent();
        public UnityEvent onClosed = new UnityEvent();

        /// <summary>C#-событие-зеркало <see cref="VhrRelay.OnData"/>.</summary>
        public event Action<int, byte[]> Data;
        /// <summary>C#-событие-зеркало <see cref="VhrRelay.OnJoined"/>.</summary>
        public event Action<int, int[]> Joined;
        /// <summary>C#-событие-зеркало <see cref="VhrRelay.OnPeerJoined"/>.</summary>
        public event Action<int> PeerJoined;
        /// <summary>C#-событие-зеркало <see cref="VhrRelay.OnPeerLeft"/>.</summary>
        public event Action<int> PeerLeft;

        private VhrRelay _relay;
        private bool _subscribed;

        /// <summary>Активный relay-клиент (или <c>null</c> до подключения).</summary>
        public VhrRelay Relay => _relay;

        /// <summary>Код лобби; задаётся в инспекторе или из кода до <see cref="Connect"/>.</summary>
        public string LobbyCode
        {
            get => lobbyCode;
            set => lobbyCode = value;
        }

        private void Start()
        {
            if (connectOnStart)
                Connect();
        }

        /// <summary>
        /// Подключается к релею и входит в комнату (асинхронно, fire-and-forget).
        /// Безопасно вызывать повторно — повторный вызов игнорируется, пока
        /// текущий relay жив.
        /// </summary>
        public async void Connect()
        {
            if (_relay != null) return;

            try
            {
                _relay = VhrSdk.Relay;
                if (_relay == null)
                {
                    Debug.LogWarning("[VHR] VhrRelayBootstrap: VhrSdk.Relay == null. " +
                                     "Сначала вызовите VhrSdk.InitializeAsync(...).");
                    return;
                }

                Subscribe();
                await _relay.ConnectAsync(string.IsNullOrWhiteSpace(lobbyCode) ? "main" : lobbyCode);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VHR] VhrRelayBootstrap: подключение к релею не удалось: {e.Message}");
            }
        }

        /// <summary>Рассылает байты всем в комнате. No-op, если ещё не подключён.</summary>
        public void Send(byte[] data) => _relay?.Send(data);

        /// <summary>Шлёт байты конкретному пиру. No-op, если ещё не подключён.</summary>
        public void SendTo(int peerId, byte[] data) => _relay?.SendTo(peerId, data);

        private void Subscribe()
        {
            if (_subscribed || _relay == null) return;
            _subscribed = true;
            _relay.OnJoined += HandleJoined;
            _relay.OnData += HandleData;
            _relay.OnPeerJoined += HandlePeerJoined;
            _relay.OnPeerLeft += HandlePeerLeft;
            _relay.OnClosed += HandleClosed;
        }

        private void Unsubscribe()
        {
            if (!_subscribed || _relay == null) return;
            _subscribed = false;
            _relay.OnJoined -= HandleJoined;
            _relay.OnData -= HandleData;
            _relay.OnPeerJoined -= HandlePeerJoined;
            _relay.OnPeerLeft -= HandlePeerLeft;
            _relay.OnClosed -= HandleClosed;
        }

        private void HandleJoined(int self, int[] peers)
        {
            try { onJoined?.Invoke(self, peers); } catch (Exception e) { Warn(e); }
            try { Joined?.Invoke(self, peers); } catch (Exception e) { Warn(e); }
        }

        private void HandleData(int sender, byte[] data)
        {
            try { onData?.Invoke(sender, data); } catch (Exception e) { Warn(e); }
            try { Data?.Invoke(sender, data); } catch (Exception e) { Warn(e); }
        }

        private void HandlePeerJoined(int peer)
        {
            try { onPeerJoined?.Invoke(peer); } catch (Exception e) { Warn(e); }
            try { PeerJoined?.Invoke(peer); } catch (Exception e) { Warn(e); }
        }

        private void HandlePeerLeft(int peer)
        {
            try { onPeerLeft?.Invoke(peer); } catch (Exception e) { Warn(e); }
            try { PeerLeft?.Invoke(peer); } catch (Exception e) { Warn(e); }
        }

        private void HandleClosed(string reason)
        {
            try { onClosed?.Invoke(); } catch (Exception e) { Warn(e); }
        }

        private static void Warn(Exception e) =>
            Debug.LogWarning($"[VHR] VhrRelayBootstrap: обработчик события кинул исключение: {e.Message}");

        private void OnDestroy()
        {
            Unsubscribe();
            var relay = _relay;
            _relay = null;
            if (relay != null)
            {
                try { _ = relay.CloseAsync(); } catch { /* ignore */ }
                try { relay.Dispose(); } catch { /* ignore */ }
            }
        }
    }
}
