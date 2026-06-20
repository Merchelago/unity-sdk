using System;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Server-side helpers for <b>dedicated server builds</b> that the VHR platform
    /// builds (Docker image) and runs from a developer's Unity Dedicated Server
    /// (Linux x86_64) build.
    /// </summary>
    /// <remarks>
    /// The platform starts the container with
    /// <c>-batchmode -nographics -port &lt;PORT&gt;</c> and exports the same value as
    /// the environment variable <c>VHR_SERVER_PORT</c>. Your server must bind its
    /// transport to that port on <c>0.0.0.0</c>. Use <see cref="ListenPort"/> to read
    /// it safely (falls back to the conventional Unity default <c>7777</c> in the
    /// editor / when the variable is absent).
    /// <para>
    /// Эти хелперы относятся <b>только к серверным сборкам</b>. В клиентских
    /// (WebGL) билдах переменная окружения обычно отсутствует и
    /// <see cref="ListenPort"/> вернёт <see cref="DefaultListenPort"/>.
    /// </para>
    /// </remarks>
    public static class VhrServer
    {
        /// <summary>Environment variable the platform sets to the listen/container port.</summary>
        public const string ListenPortEnvVar = "VHR_SERVER_PORT";

        /// <summary>Conventional Unity default port used when the env var is missing/invalid.</summary>
        public const int DefaultListenPort = 7777;

        /// <summary>
        /// Port the dedicated server should bind to, read from the
        /// <c>VHR_SERVER_PORT</c> environment variable (set by the platform when it
        /// runs your server container). Returns <see cref="DefaultListenPort"/>
        /// (<c>7777</c>) when the variable is unset, empty, non-numeric or out of the
        /// valid TCP/UDP range (1..65535).
        /// </summary>
        /// <remarks>
        /// Bind your transport to this port on <c>0.0.0.0</c>, e.g.
        /// <c>transport.Port = VhrServer.ListenPort;</c>. The same value is also
        /// passed to the build as the <c>-port</c> command-line argument; prefer the
        /// env var (this property) as the single source of truth.
        /// </remarks>
        public static int ListenPort => TryReadPort(out var port) ? port : DefaultListenPort;

        /// <summary>
        /// Reads and validates <c>VHR_SERVER_PORT</c>. Returns <c>true</c> and the
        /// parsed port (1..65535) when present and valid; otherwise <c>false</c>.
        /// Never throws.
        /// </summary>
        public static bool TryReadPort(out int port)
        {
            port = DefaultListenPort;
            string raw;
            try { raw = Environment.GetEnvironmentVariable(ListenPortEnvVar); }
            catch { return false; }

            if (string.IsNullOrWhiteSpace(raw)) return false;
            if (!int.TryParse(raw.Trim(), out var parsed)) return false;
            if (parsed < 1 || parsed > 65535) return false;

            port = parsed;
            return true;
        }
    }
}
