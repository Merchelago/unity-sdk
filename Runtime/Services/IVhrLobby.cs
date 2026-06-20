using System;
using System.Threading;
using System.Threading.Tasks;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Лобби и матчмейкинг поверх платформенного релея. Даёт играм быстрый матч
    /// (собрать игроков, добить пустые слоты ботами), приватные лобби, приглашение
    /// друзей платформы, готовность (ready-up) и старт. Работает в <b>WebGL и
    /// нативе</b> — ездит по тому же WebSocket-соединению, что и
    /// <see cref="VhrRelay"/> (управляющий фрейм <c>0x20</c> = <c>[0x20][utf8 JSON]</c>),
    /// поэтому никаких новых нативных зависимостей.
    /// </summary>
    /// <remarks>
    /// На старте матча SDK сам заводит участников в общую relay-комнату
    /// (<see cref="VhrMatchInfo.roomId"/>), после чего игра обменивается данными
    /// через <c>VhrSdk.Relay</c> (<c>Send</c>/<c>OnData</c>). <b>Ботов спавнит
    /// сама игра</b> (хост — авторитет) по <see cref="VhrMatchInfo.botSlots"/>.
    /// Все события маршалятся на главный Unity-поток (как у <see cref="VhrRelay"/>).
    /// </remarks>
    public interface IVhrLobby
    {
        /// <summary>Текущее лобби (или <c>null</c>, если не в лобби).</summary>
        VhrLobby CurrentLobby { get; }

        /// <summary>Является ли текущий игрок хостом текущего лобби.</summary>
        bool IsHost { get; }

        /// <summary>UserId текущего игрока (из <c>0x20</c>-событий релея; может быть <c>null</c> до первого).</summary>
        string SelfUserId { get; }

        /// <summary>Лобби обновилось (вход/выход/готовность/смена хоста).</summary>
        event Action<VhrLobby> OnLobbyUpdated;

        /// <summary>Пришло приглашение в лобби от другого игрока.</summary>
        event Action<VhrLobbyInvite> OnInviteReceived;

        /// <summary>Матч вот-вот стартует: аргумент — обратный отсчёт в секундах.</summary>
        event Action<int> OnMatchStarting;

        /// <summary>Матч стартовал: общая комната, слоты, боты. Релей уже в комнате.</summary>
        event Action<VhrMatchInfo> OnMatchStarted;

        /// <summary>Лобби/матч закрылся. Аргумент — причина (может быть <c>null</c>).</summary>
        event Action<string> OnClosed;

        /// <summary>
        /// Быстрый матч: отправляет запрос на подбор, ждёт старта и возвращает
        /// <see cref="VhrMatchInfo"/>. По ходу ожидания дёргает
        /// <see cref="OnLobbyUpdated"/>/<see cref="OnMatchStarting"/>. На старте
        /// <b>сам входит</b> в relay-комнату <see cref="VhrMatchInfo.roomId"/>, так
        /// что игра сразу может использовать <c>VhrSdk.Relay</c>.
        /// </summary>
        Task<VhrMatchInfo> QuickMatchAsync(VhrMatchmakingOptions opts, CancellationToken ct = default);

        /// <summary>
        /// Создаёт лобби (по умолчанию приватное) и возвращает его с
        /// <see cref="VhrLobby.code"/> — этот код можно показать/разослать друзьям.
        /// Создатель становится хостом.
        /// </summary>
        Task<VhrLobby> CreateLobbyAsync(VhrLobbyOptions opts);

        /// <summary>Присоединяется к лобби по коду. Возвращает актуальное лобби.</summary>
        Task<VhrLobby> JoinLobbyAsync(string code);

        /// <summary>Покидает текущее лобби.</summary>
        Task LeaveLobbyAsync();

        /// <summary>Отменяет ожидание быстрого матча (или текущее лобби, если хост).</summary>
        Task CancelAsync();

        /// <summary>Проставляет/снимает готовность текущего игрока.</summary>
        Task SetReadyAsync(bool ready);

        /// <summary>Приглашает друга в текущее лобби по его userId.</summary>
        Task InviteFriendAsync(string userId);

        /// <summary>Кикает участника из лобби (только хост).</summary>
        Task KickAsync(string userId);

        /// <summary>
        /// Хост стартует матч: шлёт <c>start</c>, ждёт ev <c>start</c>, возвращает
        /// <see cref="VhrMatchInfo"/> и <b>сам входит</b> в relay-комнату матча.
        /// </summary>
        Task<VhrMatchInfo> StartAsync();

        /// <summary>
        /// Список друзей игрока (REST в games API) — чтобы показать, кого можно
        /// пригласить. Бэкенд: <c>GET {GamesBaseUrl}/api/Friends</c>.
        /// </summary>
        Task<VhrFriend[]> GetFriendsAsync(CancellationToken ct = default);
    }
}
