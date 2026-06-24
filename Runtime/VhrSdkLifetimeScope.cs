using System;
using System.Threading;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace VhrGames.Sdk
{
    /// <summary>
    /// VContainer <see cref="LifetimeScope"/> that wires the entire SDK object
    /// graph. Add this component to a bootstrap GameObject, assign
    /// <see cref="optionsAsset"/> (or override <see cref="BuildOptions"/>), and
    /// resolve <see cref="IVhrEconomy"/> / <see cref="IVhrLeaderboard"/> /
    /// <see cref="IVhrServers"/> / <see cref="IVhrSession"/> via constructor
    /// injection anywhere in your game.
    /// </summary>
    /// <remarks>
    /// This scope also registers an <see cref="VhrSdkEntryPoint"/> that calls
    /// <c>InitializeAsync</c> on start, so DI consumers never call init manually.
    /// </remarks>
    public class VhrSdkLifetimeScope : LifetimeScope
    {
        [Header("VHR SDK configuration")]
        [Tooltip("ID игры из кабинета разработчика (страница /dev/games). Обязательно. Это не секрет.")]
        [SerializeField] private string gameId;

        [Tooltip("ТОЛЬКО для серверных игр / server-to-server (X-Internal-Api-Key). " +
                 "НЕ задавайте в клиентских WebGL-сборках — секрет утечёт в публичный билд. " +
                 "Клиент авторизуется JWT игрока (см. BuildOptions / TokenProvider).")]
        [SerializeField] private string internalApiKey;

        [Tooltip("Bridge base URL. Leave default for production.")]
        [SerializeField] private string bridgeBaseUrl = "https://api.vhrweb.ru/bridge";

        [Tooltip("Servers base URL. Leave default for production.")]
        [SerializeField] private string serversBaseUrl = "https://api.vhrweb.ru/servers";

        [Tooltip("Games (UnityGamesMS) base URL — турниры и пр. Leave default for production.")]
        [SerializeField] private string gamesBaseUrl = "https://api.vhrweb.ru/games";

        [Tooltip("Ping the bridge on initialize and reflect it in the connection state.")]
        [SerializeField] private bool pingOnInitialize = true;

        [Tooltip("Log request/response lines.")]
        [SerializeField] private bool verboseLogging;

        /// <summary>
        /// Builds the options. На WebGL <see cref="VhrSdkOptions.TokenProvider"/>
        /// оставлен <c>null</c>: <see cref="VhrSdkOptions.Validate"/> подставит
        /// дефолтный провайдер, читающий JWT игрока из <c>?access_token</c> URL
        /// страницы. Для нативных / редакторных сборок переопределите этот метод
        /// в подклассе и задайте <see cref="VhrSdkOptions.TokenProvider"/> явно,
        /// привязав к своей системе авторизации. <see cref="VhrSdkOptions.InternalApiKey"/>
        /// заполняйте ТОЛЬКО для серверных сборок.
        /// </summary>
        protected virtual VhrSdkOptions BuildOptions() => new()
        {
            GameId = gameId,
            // Опционально: только для серверных сборок. В клиентских WebGL
            // оставьте пустым (поле в инспекторе не заполнять).
            InternalApiKey = internalApiKey,
            BridgeBaseUrl = bridgeBaseUrl,
            ServersBaseUrl = serversBaseUrl,
            GamesBaseUrl = gamesBaseUrl,
            PingOnInitialize = pingOnInitialize,
            VerboseLogging = verboseLogging
            // TokenProvider не задаём: дефолт (WebGL access_token) подставит Validate().
        };

        /// <inheritdoc />
        protected override void Configure(IContainerBuilder builder)
        {
            var options = BuildOptions();
            options.Validate();

            builder.RegisterInstance(options);
            builder.Register<IVhrLog>(_ => new VhrUnityLog(options.VerboseLogging), Lifetime.Singleton);
            builder.Register<IVhrHttp, UnityWebRequestHttp>(Lifetime.Singleton);
            builder.Register<VhrApiClient>(Lifetime.Singleton);

            builder.Register<VhrSession>(Lifetime.Singleton).As<IVhrSession>().AsSelf();
            builder.Register<IVhrEconomy, VhrEconomyService>(Lifetime.Singleton);
            builder.Register<IVhrLeaderboard, VhrLeaderboardService>(Lifetime.Singleton);
            builder.Register<IVhrServers, VhrServersService>(Lifetime.Singleton);
            builder.Register<IVhrTournaments, VhrTournamentsService>(Lifetime.Singleton);
            builder.Register<IVhrProfile, VhrProfileService>(Lifetime.Singleton);
            builder.Register<IVhrFriends, VhrFriendsService>(Lifetime.Singleton);
            builder.Register<IVhrPlayerStats, VhrPlayerStatsService>(Lifetime.Singleton);
            builder.Register<IVhrAchievements, VhrAchievementsService>(Lifetime.Singleton);
            builder.Register<IVhrGameSessions, VhrGameSessionsService>(Lifetime.Singleton);

            builder.RegisterEntryPoint<VhrSdkEntryPoint>();
        }
    }

    /// <summary>
    /// VContainer entry point that drives SDK initialization at startup so DI
    /// consumers don't call <c>InitializeAsync</c> by hand.
    /// </summary>
    public sealed class VhrSdkEntryPoint : IStartable
    {
        private readonly VhrSdkOptions _options;
        private readonly VhrApiClient _api;
        private readonly VhrSession _session;
        private readonly IVhrLog _log;

        /// <summary>Constructor-injected dependencies.</summary>
        public VhrSdkEntryPoint(VhrSdkOptions options, VhrApiClient api, VhrSession session, IVhrLog log)
        {
            _options = options;
            _api = api;
            _session = session;
            _log = log;
        }

        /// <summary>
        /// Runs the shared init routine and binds the static facade for hybrid
        /// use. IStartable is synchronous (no UniTask dependency); we kick off
        /// the async init fire-and-forget and never let it crash startup.
        /// </summary>
        public void Start()
        {
            _ = InitAsync();
        }

        private async Awaitable InitAsync()
        {
            try
            {
                await VhrSdk.InitializeCoreAsync(_options, _api, _session, _log, CancellationToken.None);
            }
            catch (Exception e)
            {
                _log?.Error("VHR SDK init failed: " + e.Message);
            }
        }
    }
}
