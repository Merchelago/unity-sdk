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

        /// <summary>Environment variable the platform sets to this server instance id.</summary>
        public const string InstanceIdEnvVar = "VHR_INSTANCE_ID";

        /// <summary>Environment variable the platform sets to the game id.</summary>
        public const string GameIdEnvVar = "VHR_GAME_ID";

        /// <summary>Environment variable the platform sets to the internal server-to-server api key.</summary>
        public const string InternalKeyEnvVar = "VHR_INTERNAL_KEY";

        /// <summary>Environment variable the platform sets to the public servers API base URL.</summary>
        public const string ServersBaseUrlEnvVar = "VHR_SERVERS_BASE_URL";

        /// <summary>Conventional Unity default port used when the env var is missing/invalid.</summary>
        public const int DefaultListenPort = 7777;

        /// <summary>Fallback servers base URL used when <c>VHR_SERVERS_BASE_URL</c> is absent.</summary>
        public const string DefaultServersBaseUrl = "https://api.vhrweb.ru/servers";

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

        /// <summary>
        /// Id этого серверного инстанса, который платформа задаёт через env
        /// <c>VHR_INSTANCE_ID</c>. Используйте его для
        /// <see cref="IVhrServers.ReportPlayersAsync"/>. В клиентских / редакторных
        /// сборках переменной нет — вернётся пустая строка. Никогда не кидает.
        /// </summary>
        public static string InstanceId => ReadEnv(InstanceIdEnvVar);

        /// <summary>
        /// Id игры, который платформа задаёт серверному контейнеру через env
        /// <c>VHR_GAME_ID</c>. Пустая строка, если переменной нет. Никогда не кидает.
        /// </summary>
        public static string GameId => ReadEnv(GameIdEnvVar);

        /// <summary>
        /// Внутренний ключ server-to-server, который платформа <b>инъектит в
        /// контейнер</b> через env <c>VHR_INTERNAL_KEY</c> (он <b>не</b> зашит в
        /// билд). Нужен для <see cref="IVhrServers.ReportPlayersAsync"/>. Пустая
        /// строка, если переменной нет (клиентские сборки). Никогда не кидает.
        /// </summary>
        public static string InternalKey => ReadEnv(InternalKeyEnvVar);

        /// <summary>
        /// Публичный базовый адрес серверного API, который платформа задаёт через
        /// env <c>VHR_SERVERS_BASE_URL</c> (напр. <c>https://servers.vhrweb.ru</c>).
        /// Если переменной нет — <see cref="DefaultServersBaseUrl"/>. Никогда не кидает.
        /// </summary>
        public static string ServersBaseUrl
        {
            get
            {
                var raw = ReadEnv(ServersBaseUrlEnvVar);
                return string.IsNullOrWhiteSpace(raw) ? DefaultServersBaseUrl : raw.Trim();
            }
        }

        /// <summary>
        /// <c>true</c>, когда процесс запущен платформой как выделенный сервер —
        /// то есть env <c>VHR_INSTANCE_ID</c> задана и непуста. В редакторе и в
        /// клиентских (WebGL) сборках вернёт <c>false</c>. Никогда не кидает.
        /// </summary>
        public static bool IsServerBuild => !string.IsNullOrEmpty(InstanceId);

        /// <summary>
        /// Безопасно читает переменную окружения: триммит, возвращает пустую строку
        /// при отсутствии/ошибке. Никогда не кидает.
        /// </summary>
        private static string ReadEnv(string name)
        {
            string raw;
            try { raw = Environment.GetEnvironmentVariable(name); }
            catch { return string.Empty; }
            return string.IsNullOrWhiteSpace(raw) ? string.Empty : raw.Trim();
        }
    }
}
