using System;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Лобби (комната ожидания) платформенного матчмейкинга. Живёт на релее
    /// (управляющий канал <c>0x20</c>), пока игроки собираются и проставляют
    /// готовность; на старте превращается в игровую relay-комнату
    /// (<see cref="VhrMatchInfo.roomId"/>). Поля — публичные, в стиле
    /// <see cref="UnityEngine.JsonUtility"/>.
    /// </summary>
    [Serializable]
    public sealed class VhrLobby
    {
        /// <summary>Код лобби (для приглашений / присоединения по коду).</summary>
        public string code;
        /// <summary>Игра, к которой относится лобби.</summary>
        public string gameId;
        /// <summary>Игровой режим (произвольная метка игры).</summary>
        public string mode;
        /// <summary>UserId хоста лобби (его авторитет — старт/кик).</summary>
        public string hostId;
        /// <summary>Максимум игроков (включая ботов-заполнителей).</summary>
        public int maxPlayers;
        /// <summary>Минимум живых игроков для старта.</summary>
        public int minPlayers;
        /// <summary>Добивать ли пустые слоты ботами на старте.</summary>
        public bool fillBots;
        /// <summary>Состояние лобби (<c>"waiting"</c> | <c>"starting"</c> | ...), как прислал релей.</summary>
        public string state;
        /// <summary>Текущие участники лобби.</summary>
        public VhrLobbyMember[] members;
    }

    /// <summary>Участник лобби.</summary>
    [Serializable]
    public sealed class VhrLobbyMember
    {
        /// <summary>VHR user id участника.</summary>
        public string userId;
        /// <summary>Отображаемое имя/ник.</summary>
        public string name;
        /// <summary>Готов ли участник.</summary>
        public bool ready;
        /// <summary>Является ли участник хостом.</summary>
        public bool isHost;
        /// <summary>Это сам текущий игрок (проставляется SDK по <see cref="VhrLobby.hostId"/>/auth).</summary>
        public bool isSelf;
    }

    /// <summary>
    /// Входящее приглашение в лобби от другого игрока (событие
    /// <see cref="IVhrLobby.OnInviteReceived"/>). Чтобы принять — вызвать
    /// <see cref="IVhrLobby.JoinLobbyAsync"/> с <see cref="code"/>.
    /// </summary>
    [Serializable]
    public sealed class VhrLobbyInvite
    {
        /// <summary>UserId пригласившего.</summary>
        public string fromUserId;
        /// <summary>Ник пригласившего.</summary>
        public string fromName;
        /// <summary>Код лобби, в которое зовут.</summary>
        public string code;
        /// <summary>Игра приглашения.</summary>
        public string gameId;
    }

    /// <summary>
    /// Итог старта матча: общая relay-комната (<see cref="roomId"/>), раскладка
    /// слотов живых игроков и слотов под ботов. После получения этого объекта
    /// SDK уже входит в комнату <see cref="roomId"/>, и игра может слать данные
    /// через <c>VhrSdk.Relay</c>. <b>Ботов спавнит сама игра</b> (хост —
    /// авторитет) по <see cref="botSlots"/>/<see cref="botCount"/>.
    /// </summary>
    [Serializable]
    public sealed class VhrMatchInfo
    {
        /// <summary>Id общей relay-комнаты матча (напр. <c>"match-xxxx"</c>).</summary>
        public string roomId;
        /// <summary>UserId хоста матча (авторитет, в т.ч. спавн ботов).</summary>
        public string hostId;
        /// <summary>UserId самого игрока.</summary>
        public string selfUserId;
        /// <summary>Является ли текущий игрок хостом.</summary>
        public bool isHost;
        /// <summary>Слот текущего игрока.</summary>
        public int selfSlot;
        /// <summary>Сколько слотов отведено ботам.</summary>
        public int botCount;
        /// <summary>Индексы слотов под ботов (их спавнит хост).</summary>
        public int[] botSlots;
        /// <summary>Живые игроки матча с их слотами.</summary>
        public VhrMatchPlayer[] players;
    }

    /// <summary>Живой игрок в стартовавшем матче.</summary>
    [Serializable]
    public sealed class VhrMatchPlayer
    {
        /// <summary>VHR user id.</summary>
        public string userId;
        /// <summary>Отображаемое имя.</summary>
        public string name;
        /// <summary>Слот игрока в матче.</summary>
        public int slot;
    }

    /// <summary>
    /// Параметры быстрого матча (<see cref="IVhrLobby.QuickMatchAsync"/>):
    /// собрать игроков, при нехватке — добить ботами.
    /// </summary>
    [Serializable]
    public sealed class VhrMatchmakingOptions
    {
        /// <summary>Игровой режим (метка игры; влияет на пул подбора).</summary>
        public string mode;
        /// <summary>Максимум игроков в матче (вкл. ботов). По умолчанию 2.</summary>
        public int maxPlayers = 2;
        /// <summary>Минимум живых игроков, ниже которого матч не стартует. По умолчанию 1.</summary>
        public int minPlayers = 1;
        /// <summary>Добивать пустые слоты ботами (иначе ждать живых). По умолчанию true.</summary>
        public bool fillBots = true;
        /// <summary>Сколько секунд ждать живых перед добивкой ботами. По умолчанию 10.</summary>
        public int waitSec = 10;
    }

    /// <summary>
    /// Параметры создания лобби (<see cref="IVhrLobby.CreateLobbyAsync"/>) —
    /// приватная комната под приглашение друзей.
    /// </summary>
    [Serializable]
    public sealed class VhrLobbyOptions
    {
        /// <summary>Игровой режим.</summary>
        public string mode;
        /// <summary>Максимум игроков (вкл. ботов). По умолчанию 2.</summary>
        public int maxPlayers = 2;
        /// <summary>Минимум живых для старта. По умолчанию 1.</summary>
        public int minPlayers = 1;
        /// <summary>Добивать пустые слоты ботами на старте. По умолчанию true.</summary>
        public bool fillBots = true;
        /// <summary>Приватное лобби (не попадает в публичный подбор; вход по коду/приглашению). По умолчанию true.</summary>
        public bool isPrivate = true;
    }

    /// <summary>
    /// Друг игрока для списка приглашения (<see cref="IVhrLobby.GetFriendsAsync"/>).
    /// </summary>
    [Serializable]
    public sealed class VhrFriend
    {
        /// <summary>VHR user id друга.</summary>
        public string userId;
        /// <summary>Отображаемое имя/ник (если бэкенд его резолвит).</summary>
        public string name;
        /// <summary>Онлайн ли друг сейчас (если бэкенд это отдаёт).</summary>
        public bool online;
    }
}
