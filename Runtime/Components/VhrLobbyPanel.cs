using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Готовый drop-in экран лобби/матчмейкинга на <see cref="VhrSdk.Lobby"/>.
    /// Повесьте на любой GameObject в сцене — на OnGUI он рисует рабочий лобби-UI:
    /// быстрый матч (с добивкой ботами), приватное лобби с кодом, список друзей
    /// и приглашения, готовность, старт. Никакой настройки Canvas не нужно.
    /// </summary>
    /// <remarks>
    /// Это и рабочий экран «из коробки», и эталонная реализация: посмотрите, как
    /// он дёргает <see cref="VhrSdk.Lobby"/>, и сделайте свой UI на тех же вызовах.
    /// Требует инициализированного SDK (<see cref="VhrSdk.InitializeAsync"/>).
    /// Боты — это игровая логика: после старта спавните их по
    /// <see cref="VhrMatchInfo.botSlots"/>/<see cref="VhrMatchInfo.botCount"/>
    /// (хост = авторитет). Всё null-safe, ошибки логируются и не валят сцену.
    /// </remarks>
    [AddComponentMenu("VHR/VHR Lobby Panel (matchmaking UI)")]
    [DisallowMultipleComponent]
    public sealed class VhrLobbyPanel : MonoBehaviour
    {
        [Header("Параметры матча")]
        [Tooltip("Режим/очередь — игроки матчатся в пределах одного gameId+mode+maxPlayers.")]
        [SerializeField] private string mode = "default";
        [SerializeField, Min(1)] private int maxPlayers = 4;
        [SerializeField, Min(1)] private int minPlayers = 2;
        [Tooltip("Добивать пустые слоты ботами при старте.")]
        [SerializeField] private bool fillBots = true;
        [Tooltip("Сколько ждать набора перед стартом по minPlayers (быстрый матч).")]
        [SerializeField, Min(1)] private int waitSec = 15;

        [Header("UI")]
        [Tooltip("Рисовать встроенный OnGUI-экран. Выключите, если делаете свой UI.")]
        [SerializeField] private bool drawGui = true;

        private enum UiState { Idle, Searching, InLobby, Starting, InMatch }
        private UiState _state = UiState.Idle;

        private VhrLobby _lobby;
        private VhrLobbyInvite _invite;
        private VhrMatchInfo _match;
        private VhrFriend[] _friends;
        private int _countdown;
        private string _joinCode = "";
        private string _status = "";
        private bool _subscribed;
        private bool _friendsLoading;

        private IVhrLobby Lobby => VhrSdk.Lobby;

        // Текущий матч (после старта) — для игровой логики (спавн игроков/ботов).
        public VhrMatchInfo CurrentMatch => _match;

        private void OnEnable() => TrySubscribe();
        private void OnDisable() => Unsubscribe();

        private void TrySubscribe()
        {
            if (_subscribed) return;
            var l = Lobby;
            if (l == null) return; // SDK ещё не инициализирован — попробуем позже из OnGUI
            l.OnLobbyUpdated += HandleLobbyUpdated;
            l.OnInviteReceived += HandleInvite;
            l.OnMatchStarting += HandleStarting;
            l.OnMatchStarted += HandleStarted;
            l.OnClosed += HandleClosed;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            var l = Lobby;
            if (l != null)
            {
                l.OnLobbyUpdated -= HandleLobbyUpdated;
                l.OnInviteReceived -= HandleInvite;
                l.OnMatchStarting -= HandleStarting;
                l.OnMatchStarted -= HandleStarted;
                l.OnClosed -= HandleClosed;
            }
            _subscribed = false;
        }

        // ── События лобби (SDK маршалит их в главный поток) ──
        private void HandleLobbyUpdated(VhrLobby l)
        {
            _lobby = l;
            if (_state != UiState.Starting && _state != UiState.InMatch)
                _state = UiState.InLobby;
        }

        private void HandleInvite(VhrLobbyInvite inv) => _invite = inv;
        private void HandleStarting(int sec) { _countdown = sec; _state = UiState.Starting; }
        private void HandleStarted(VhrMatchInfo m) { _match = m; _state = UiState.InMatch; }

        private void HandleClosed(string reason)
        {
            _lobby = null;
            _status = string.IsNullOrEmpty(reason) ? "Лобби закрыто" : reason;
            if (_state != UiState.InMatch) _state = UiState.Idle;
        }

        // ── Действия (fire-and-forget с логом ошибок) ──
        private async void RunSafe(Func<Task> action, string what)
        {
            try { await action(); }
            catch (Exception e)
            {
                _status = $"{what}: {e.Message}";
                Debug.LogWarning($"[VHR] VhrLobbyPanel/{what}: {e.Message}");
            }
        }

        /// <summary>Быстрый матч: набор игроков, пустые слоты — боты.</summary>
        public void QuickMatch()
        {
            _status = "";
            _state = UiState.Searching;
            RunSafe(() => Lobby.QuickMatchAsync(new VhrMatchmakingOptions
            {
                mode = mode, maxPlayers = maxPlayers, minPlayers = minPlayers,
                fillBots = fillBots, waitSec = waitSec,
            }), "Быстрый матч");
        }

        /// <summary>Создать приватное лобби (для приглашения друзей).</summary>
        public void CreateLobby() =>
            RunSafe(() => Lobby.CreateLobbyAsync(new VhrLobbyOptions
            {
                mode = mode, maxPlayers = maxPlayers, minPlayers = minPlayers,
                fillBots = fillBots, isPrivate = true,
            }), "Создать лобби");

        public void JoinLobby(string code) =>
            RunSafe(() => Lobby.JoinLobbyAsync(code), "Войти в лобби");

        public void ToggleReady()
        {
            var me = _lobby?.members?.FirstOrDefault(m => m.userId == Lobby.SelfUserId);
            bool next = !(me?.ready ?? false);
            RunSafe(() => Lobby.SetReadyAsync(next), "Готовность");
        }

        public void StartMatch() => RunSafe(() => Lobby.StartAsync(), "Старт");
        public void Leave() { _state = UiState.Idle; RunSafe(() => Lobby.LeaveLobbyAsync(), "Выход"); }
        public void Cancel() { _state = UiState.Idle; RunSafe(() => Lobby.CancelAsync(), "Отмена"); }
        public void Invite(string userId) => RunSafe(() => Lobby.InviteFriendAsync(userId), "Приглашение");
        public void Kick(string userId) => RunSafe(() => Lobby.KickAsync(userId), "Кик");

        public async void LoadFriends()
        {
            _friendsLoading = true;
            try { _friends = await Lobby.GetFriendsAsync(); }
            catch (Exception e) { Debug.LogWarning($"[VHR] friends: {e.Message}"); }
            finally { _friendsLoading = false; }
        }

        public void AcceptInvite()
        {
            if (_invite != null) { JoinLobby(_invite.code); _invite = null; }
        }
        public void DeclineInvite() => _invite = null;

        // ── Встроенный экран (можно отключить drawGui и сделать свой UI) ──
        private void OnGUI()
        {
            if (!drawGui) return;
            if (!_subscribed) TrySubscribe();

            GUILayout.BeginArea(new Rect(16, 16, 360, Screen.height - 32), GUI.skin.box);
            GUILayout.Label("<b>VHR Лобби</b>");

            if (Lobby == null)
            {
                GUILayout.Label("SDK не инициализирован.\nВызовите VhrSdk.InitializeAsync(...).");
                GUILayout.EndArea();
                return;
            }

            // Входящее приглашение — поверх любого состояния.
            if (_invite != null)
            {
                GUILayout.Space(6);
                GUILayout.Label($"<b>{_invite.fromName}</b> зовёт в игру (код {_invite.code})");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Принять")) AcceptInvite();
                if (GUILayout.Button("Отклонить")) DeclineInvite();
                GUILayout.EndHorizontal();
                GUILayout.Space(6);
            }

            switch (_state)
            {
                case UiState.Idle: DrawIdle(); break;
                case UiState.Searching: DrawSearching(); break;
                case UiState.InLobby: DrawLobby(); break;
                case UiState.Starting: GUILayout.Label($"Старт через {_countdown}…"); break;
                case UiState.InMatch: DrawMatch(); break;
            }

            if (!string.IsNullOrEmpty(_status))
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label($"<color=#FF9>{_status}</color>");
            }
            GUILayout.EndArea();
        }

        private void DrawIdle()
        {
            GUILayout.Label($"Игроков: {maxPlayers} · боты: {(fillBots ? "да" : "нет")}");
            if (GUILayout.Button("⚡ Быстрый матч")) QuickMatch();
            GUILayout.Space(8);
            if (GUILayout.Button("Создать приватное лобби")) CreateLobby();
            GUILayout.Space(8);
            GUILayout.Label("Войти по коду:");
            GUILayout.BeginHorizontal();
            _joinCode = GUILayout.TextField(_joinCode ?? "", 8, GUILayout.Width(120));
            if (GUILayout.Button("Войти") && !string.IsNullOrWhiteSpace(_joinCode))
                JoinLobby(_joinCode.Trim().ToUpperInvariant());
            GUILayout.EndHorizontal();
        }

        private void DrawSearching()
        {
            GUILayout.Label("Поиск игроков…");
            if (_lobby != null)
                GUILayout.Label($"В лобби: {_lobby.members?.Length ?? 0}/{_lobby.maxPlayers}");
            if (GUILayout.Button("Отмена")) Cancel();
        }

        private void DrawLobby()
        {
            if (_lobby == null) { GUILayout.Label("…"); return; }
            GUILayout.Label($"Код лобби: <b>{_lobby.code}</b>  ({_lobby.members?.Length ?? 0}/{_lobby.maxPlayers})");
            GUILayout.Space(4);

            bool iAmHost = Lobby.IsHost;
            foreach (var m in _lobby.members ?? Array.Empty<VhrLobbyMember>())
            {
                GUILayout.BeginHorizontal();
                var tag = (m.isHost ? "👑 " : "") + (m.userId == Lobby.SelfUserId ? "(вы) " : "");
                GUILayout.Label($"{tag}{m.name}  {(m.ready ? "✔" : "…")}");
                if (iAmHost && m.userId != Lobby.SelfUserId && GUILayout.Button("кик", GUILayout.Width(44)))
                    Kick(m.userId);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6);
            if (GUILayout.Button("Готов / Не готов")) ToggleReady();
            if (iAmHost && GUILayout.Button("▶ Старт")) StartMatch();

            GUILayout.Space(8);
            GUILayout.Label("Пригласить друга:");
            if (GUILayout.Button(_friendsLoading ? "Загрузка…" : "Обновить список друзей")) LoadFriends();
            foreach (var f in _friends ?? Array.Empty<VhrFriend>())
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label((string.IsNullOrEmpty(f.name) ? f.userId : f.name) + (f.online ? " ●" : ""));
                if (GUILayout.Button("позвать", GUILayout.Width(70))) Invite(f.userId);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6);
            if (GUILayout.Button("Выйти из лобби")) Leave();
        }

        private void DrawMatch()
        {
            if (_match == null) { GUILayout.Label("Матч начался."); return; }
            GUILayout.Label("<b>Матч начался!</b>");
            GUILayout.Label($"Комната: {_match.roomId}");
            GUILayout.Label($"Игроков: {_match.players?.Length ?? 0} · ботов: {_match.botCount}");
            GUILayout.Label($"Вы: слот {_match.selfSlot}{(_match.isHost ? " (хост)" : "")}");
            GUILayout.Label("Данные идут через VhrSdk.Relay (Send/OnData).");
            GUILayout.Label("Спавните ботов по match.botSlots — хост авторитет.");
        }

        private void OnDestroy() => Unsubscribe();
    }
}
