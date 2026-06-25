using System;
using System.Threading;
using System.Threading.Tasks;
using R3;
using UnityEngine; // UnityEngine.Awaitable (Unity 6+)

namespace VhrGames.Sdk
{
    /// <summary>
    /// Static facade / quick-start entry point for games that do <b>not</b> use
    /// VContainer. Call <see cref="InitializeAsync"/> once at boot, then use
    /// <see cref="Economy"/>, <see cref="Leaderboard"/>, <see cref="Servers"/>
    /// and <see cref="Session"/>.
    /// </summary>
    /// <remarks>
    /// When the SDK is wired through <see cref="VhrSdkLifetimeScope"/> the same
    /// instances are bound here too (hybrid mode), so library code can rely on
    /// either path. Not thread-safe; initialize from the main thread.
    /// </remarks>
    public static class VhrSdk
    {
        /// <summary>SDK semantic version. Mirrored into the build marker and sent as <c>X-Vhr-Sdk-Version</c>.</summary>
        public const string SdkVersion = "1.7.6";

        private static VhrSession _session;
        private static VhrSdkOptions _options;
        private static VhrApiClient _api;
        private static VhrRelay _relay;
        private static VhrLobbyService _lobby;

        /// <summary>True once <see cref="InitializeAsync"/> (or the DI entry point) completed.</summary>
        public static bool IsInitialized { get; private set; }

        /// <summary>Economy service. Null until initialized.</summary>
        public static IVhrEconomy Economy { get; private set; }

        /// <summary>Leaderboard service. Null until initialized.</summary>
        public static IVhrLeaderboard Leaderboard { get; private set; }

        /// <summary>Servers service. Null until initialized.</summary>
        public static IVhrServers Servers { get; private set; }

        /// <summary>Tournaments service. Null until initialized.</summary>
        public static IVhrTournaments Tournaments { get; private set; }

        /// <summary>Профиль игрока + батч-резолв ников по userId (см. <see cref="IVhrProfile"/>). Null until initialized.</summary>
        public static IVhrProfile Profile { get; private set; }

        /// <summary>Друзья: список, заявки, статус пары, приглашения (см. <see cref="IVhrFriends"/>). Null until initialized.</summary>
        public static IVhrFriends Friends { get; private set; }

        /// <summary>Прогресс игрока: уровень, XP, коины, ранг, косметика (см. <see cref="IVhrPlayerStats"/>). Null until initialized.</summary>
        public static IVhrPlayerStats PlayerStats { get; private set; }

        /// <summary>Достижения игрока/игр/платформы (см. <see cref="IVhrAchievements"/>). Null until initialized.</summary>
        public static IVhrAchievements Achievements { get; private set; }

        /// <summary>Игровые сессии: старт/энд/heartbeat (см. <see cref="IVhrGameSessions"/>). Null until initialized.</summary>
        public static IVhrGameSessions GameSessions { get; private set; }

        /// <summary>Session / connection-state holder. Null until initialized.</summary>
        public static IVhrSession Session => _session;

        /// <summary>
        /// Клиент <b>платформенного релея</b> для простого мультиплеера без
        /// серверной сборки (см. <see cref="VhrRelay"/>). Лениво создаётся при
        /// первом обращении из опций, переданных в <see cref="InitializeAsync"/>
        /// (нужен инициализированный SDK — иначе <c>null</c>). Один общий
        /// экземпляр на процесс; обращайтесь с главного потока.
        /// </summary>
        /// <example><code>
        /// await VhrSdk.Relay.ConnectAsync("lobby1");
        /// VhrSdk.Relay.OnData += (sender, bytes) => { /* ... */ };
        /// VhrSdk.Relay.Send(bytes);
        /// </code></example>
        public static VhrRelay Relay =>
            _relay ??= _options != null ? new VhrRelay(_options) : null;

        /// <summary>
        /// Лобби и матчмейкинг (быстрый матч с добивкой ботами, приватные лобби,
        /// приглашение друзей, готовность, старт) — см. <see cref="IVhrLobby"/>.
        /// Лениво создаётся поверх того же <see cref="Relay"/> (общий сокет,
        /// управляющий фрейм <c>0x20</c>), опций и api-клиента. Требует
        /// инициализированного SDK (иначе <c>null</c>). Один экземпляр на процесс;
        /// обращайтесь с главного потока.
        /// </summary>
        /// <example><code>
        /// var m = await VhrSdk.Lobby.QuickMatchAsync(
        ///     new VhrMatchmakingOptions { maxPlayers = 4, fillBots = true });
        /// SpawnPlayers(m.players);
        /// SpawnBots(m.botSlots); // боты — на хосте; игра идёт через VhrSdk.Relay
        /// </code></example>
        public static IVhrLobby Lobby =>
            _lobby ??= (_options != null && _api != null)
                ? new VhrLobbyService(Relay, _options, _api)
                : null;

        /// <summary>
        /// Connection-state stream (convenience passthrough to
        /// <see cref="IVhrSession.StateChanged"/>). Empty stream before init.
        /// </summary>
        public static Observable<VhrConnectionState> ConnectionState =>
            _session?.StateChanged ?? Observable.Empty<VhrConnectionState>();

        /// <summary>
        /// Initializes the SDK for non-DI usage: validates options, builds the
        /// service graph, logs the version and (optionally) pings the bridge.
        /// Safe to call once; subsequent calls are ignored.
        /// </summary>
        /// <param name="options">Validated configuration.</param>
        /// <param name="http">Optional transport override (tests). Defaults to <see cref="UnityWebRequestHttp"/>.</param>
        /// <param name="ct">Cancellation token.</param>
        public static async Task InitializeAsync(
            VhrSdkOptions options, IVhrHttp http = null, CancellationToken ct = default)
        {
            if (IsInitialized) return;
            if (options == null) throw new ArgumentNullException(nameof(options));
            options.Validate();

            var log = new VhrUnityLog(options.VerboseLogging);
            http ??= new UnityWebRequestHttp(log);
            var api = new VhrApiClient(http, options, log);
            var session = new VhrSession(options);

            await InitializeCoreAsync(options, api, session, log, ct);
        }

        /// <summary>
        /// Shared init routine used by both <see cref="InitializeAsync"/> and the
        /// VContainer <see cref="VhrSdkEntryPoint"/>. Wires the static facade and
        /// performs the optional handshake ping.
        /// </summary>
        internal static async Awaitable InitializeCoreAsync(
            VhrSdkOptions options, VhrApiClient api, VhrSession session, IVhrLog log,
            CancellationToken ct)
        {
            _session = session;
            _options = options; // нужен для ленивого VhrSdk.Relay
            _api = api;         // нужен для ленивого VhrSdk.Lobby (REST друзей)

            // Жизненный цикл JWT: на WebGL подписываемся на postMessage от
            // родителя (vhrgames.ru) как можно раньше, чтобы свежий токен был
            // готов до первого 401. Вне WebGL — безопасный no-op.
            VhrWebGlTokenChannel.EnsureInitialized();

            Economy = new VhrEconomyService(api, options);
            Leaderboard = new VhrLeaderboardService(api, options, log);
            Servers = new VhrServersService(api, options);
            Tournaments = new VhrTournamentsService(api, options);
            Profile = new VhrProfileService(api, options, log);
            Friends = new VhrFriendsService(api, options, log);
            PlayerStats = new VhrPlayerStatsService(api, options, log);
            Achievements = new VhrAchievementsService(api, options, log);
            GameSessions = new VhrGameSessionsService(api, options, log);

            log.Info($"Initializing VHR SDK v{SdkVersion} for game '{options.GameId}'.");
            session.SetState(VhrConnectionState.Connecting);

            if (options.PingOnInitialize)
            {
                try
                {
                    await api.SendAsync<VhrVoid>("GET", $"{options.BridgeBaseUrl}/api/ping", ct: ct);
                    session.SetState(VhrConnectionState.Connected);
                    log.Info("Bridge ping OK — SDK connected.");
                }
                catch (VhrSdkException ex)
                {
                    // Non-fatal: economy calls may still work / retry later.
                    session.SetState(VhrConnectionState.Faulted);
                    log.Warn($"Bridge ping failed ({ex.Code}: {ex.Message}). SDK will retry on demand.");
                }
            }
            else
            {
                session.SetState(VhrConnectionState.Connected);
            }

            IsInitialized = true;
        }

        /// <summary>
        /// Создаёт <b>самодостаточный</b> экземпляр <see cref="IVhrServers"/> без
        /// DI (VContainer) и без обращения к статическому фасаду / глобальному
        /// <see cref="InitializeAsync"/>. Собирает ту же цепочку объектов, что и
        /// обычная инициализация: <see cref="VhrUnityLog"/> →
        /// <see cref="UnityWebRequestHttp"/> → <see cref="VhrApiClient"/> →
        /// <see cref="VhrServersService"/>.
        /// </summary>
        /// <remarks>
        /// Предназначен для выделенного сервера (см.
        /// <c>VhrServerHost</c>): сервер репортит игроков server-to-server и не
        /// нуждается ни в JWT игрока, ни в ping'е bridge. Передавайте
        /// <see cref="VhrSdkOptions.InternalApiKey"/> (для сервера — из env
        /// <c>VHR_INTERNAL_KEY</c>). Опции валидируются (<see cref="VhrSdkOptions.Validate"/>).
        /// </remarks>
        /// <param name="options">Конфигурация (минимум: <see cref="VhrSdkOptions.GameId"/>,
        /// <see cref="VhrSdkOptions.ServersBaseUrl"/>; для репорта — <see cref="VhrSdkOptions.InternalApiKey"/>).</param>
        /// <param name="http">Необязательная замена транспорта (тесты). По умолчанию <see cref="UnityWebRequestHttp"/>.</param>
        /// <returns>Готовый к использованию <see cref="IVhrServers"/>.</returns>
        public static IVhrServers CreateStandaloneServers(VhrSdkOptions options, IVhrHttp http = null)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            options.Validate();

            var log = new VhrUnityLog(options.VerboseLogging);
            http ??= new UnityWebRequestHttp(log);
            var api = new VhrApiClient(http, options, log);
            return new VhrServersService(api, options);
        }

        /// <summary>
        /// Resets static state. Editor / test only — do not call in shipping code.
        /// </summary>
        public static void ResetForTests()
        {
            (_session as IDisposable)?.Dispose();
            (Economy as IDisposable)?.Dispose();
            _lobby?.Dispose();
            _lobby = null;
            _relay?.Dispose();
            _relay = null;
            _api = null;
            _options = null;
            _session = null;
            Economy = null;
            Leaderboard = null;
            Servers = null;
            Tournaments = null;
            Profile = null;
            Friends = null;
            PlayerStats = null;
            Achievements = null;
            GameSessions = null;
            IsInitialized = false;
        }
    }
}
