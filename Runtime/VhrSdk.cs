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
        public const string SdkVersion = "1.1.1";

        private static VhrSession _session;

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

        /// <summary>Session / connection-state holder. Null until initialized.</summary>
        public static IVhrSession Session => _session;

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

            // Жизненный цикл JWT: на WebGL подписываемся на postMessage от
            // родителя (vhrgames.ru) как можно раньше, чтобы свежий токен был
            // готов до первого 401. Вне WebGL — безопасный no-op.
            VhrWebGlTokenChannel.EnsureInitialized();

            Economy = new VhrEconomyService(api, options);
            Leaderboard = new VhrLeaderboardService(api, options, log);
            Servers = new VhrServersService(api, options);
            Tournaments = new VhrTournamentsService(api, options);

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
        /// Resets static state. Editor / test only — do not call in shipping code.
        /// </summary>
        public static void ResetForTests()
        {
            (_session as IDisposable)?.Dispose();
            (Economy as IDisposable)?.Dispose();
            _session = null;
            Economy = null;
            Leaderboard = null;
            Servers = null;
            Tournaments = null;
            IsInitialized = false;
        }
    }
}
