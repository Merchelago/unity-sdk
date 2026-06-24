using System;
using UnityEngine;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Immutable-ish configuration object for the VHR SDK. Construct one and pass it to
    /// <see cref="VhrSdk.InitializeAsync"/> (non-DI) or register it inside a
    /// <see cref="VhrSdkLifetimeScope"/> (VContainer).
    /// </summary>
    /// <remarks>
    /// All URL fields default to the production VHR bridge. Override them for staging.
    /// The auth/session token is supplied lazily via <see cref="TokenProvider"/> so the
    /// host game can refresh it (e.g. after re-login) without rebuilding the SDK.
    /// <para>
    /// Модель авторизации клиента: WebGL-игры авторизуются <b>JWT игрока</b>.
    /// Сайт VHR встраивает игру с <c>?access_token=...</c> в URL страницы; если
    /// <see cref="TokenProvider"/> не задан, SDK использует
    /// <see cref="DefaultWebGlTokenProvider"/>, который сам считывает токен из
    /// <see cref="Application.absoluteURL"/>. Никакой общий секрет в публичную
    /// клиентскую сборку не кладётся.
    /// </para>
    /// </remarks>
    [Serializable]
    public sealed class VhrSdkOptions
    {
        /// <summary>
        /// Base URL of the VHR bridge API (economy / leaderboard / ping).
        /// Default: <c>https://api.vhrweb.ru/bridge</c>. Endpoints are appended as
        /// <c>{BridgeBaseUrl}/api/...</c>.
        /// </summary>
        public string BridgeBaseUrl = "https://api.vhrweb.ru/bridge";

        /// <summary>
        /// Base URL of the server-binding API. Default: <c>https://api.vhrweb.ru/servers</c>.
        /// Endpoints are appended as <c>{ServersBaseUrl}/api/...</c>.
        /// </summary>
        public string ServersBaseUrl = "https://api.vhrweb.ru/servers";

        /// <summary>
        /// Base URL of the games API (UnityGamesMS) — игры/друзья/статы/лидерборды/
        /// ачивки/магазин и пр. Default: <c>https://api.vhrweb.ru/games</c>.
        /// Endpoints appended as <c>{GamesBaseUrl}/api/...</c>.
        /// </summary>
        public string GamesBaseUrl = "https://api.vhrweb.ru/games";

        /// <summary>
        /// Base URL of the auth API (AuthMS) — профиль игрока (<c>/api/Auth/me</c>)
        /// и батч-резолв ников (<c>/api/Auth/users/resolve</c>). Default:
        /// <c>https://api.vhrweb.ru/auth</c>. Endpoints appended as <c>{AuthBaseUrl}/api/...</c>.
        /// </summary>
        public string AuthBaseUrl = "https://api.vhrweb.ru/auth";

        /// <summary>
        /// Base URL of the notifications API (NotificationMS) — уведомления игрока
        /// (<c>/api/Notifications/...</c>). Default: <c>https://api.vhrweb.ru/notifications</c>.
        /// </summary>
        public string NotificationsBaseUrl = "https://api.vhrweb.ru/notifications";

        /// <summary>
        /// WebSocket-адрес платформенного <b>релея</b> для простого мультиплеера
        /// без серверной сборки (см. <see cref="VhrRelay"/>). По умолчанию
        /// <c>wss://servers.vhrweb.ru/ws</c>. Должен начинаться с <c>ws://</c> или
        /// <c>wss://</c>; для WebGL-страницы по HTTPS используйте <c>wss://</c>.
        /// </summary>
        public string RelayBaseUrl = "wss://servers.vhrweb.ru/ws";

        /// <summary>
        /// Разрешить релею в <b>WebGL-сборке</b> авто-апгрейд транспорта с
        /// WebSocket на <b>WebRTC DataChannel</b> (низколатентный, UDP-подобный
        /// unreliable/unordered) после входа в комнату. По умолчанию <c>true</c>.
        /// <para>
        /// Полностью прозрачно для разработчика: тот же <c>Send</c>/<c>OnData</c> и
        /// та же семантика комнаты. При сбое/таймауте апгрейда (нет WebRTC в
        /// браузере, ICE не сошёлся за ~5 с и т.п.) релей остаётся на WebSocket.
        /// На нативе/в редакторе опция игнорируется — там всегда WebSocket.
        /// Текущий транспорт виден в <see cref="VhrRelay.Transport"/>.
        /// </para>
        /// </summary>
        public bool PreferWebRtc = true;

        /// <summary>
        /// The VHR game id this build belongs to. Required. Sent as the
        /// <c>X-Vhr-Game-Id</c> header and used in server-binding calls.
        /// </summary>
        public string GameId;

        /// <summary>
        /// ОПЦИОНАЛЬНО. ТОЛЬКО для серверного использования (server-to-server /
        /// выделенный сервер); НЕ задавайте в клиентских WebGL-сборках — это
        /// утечёт секрет в публичный билд. Клиент авторизуется JWT игрока
        /// (см. <see cref="TokenProvider"/>). Если задан и непуст, шлётся как
        /// заголовок <c>X-Internal-Api-Key</c>; пустое значение не отправляется.
        /// </summary>
        public string InternalApiKey;

        /// <summary>
        /// Первичный механизм авторизации: ленивый провайдер JWT текущего игрока.
        /// Вызывается на каждый запрос; вернуть <c>null</c>/пусто для анонимных
        /// вызовов. Возвращённое значение шлётся как <c>Authorization: Bearer {token}</c>.
        /// <para>
        /// Если оставить <c>null</c>, SDK на WebGL автоматически подставит
        /// <see cref="DefaultWebGlTokenProvider"/>, который читает
        /// <c>access_token</c> из URL страницы (сайт VHR встраивает игру с
        /// <c>?access_token=...</c>). В нативных / редакторных сборках URL-параметра
        /// нет — там задавайте <see cref="TokenProvider"/> явно (например, привязав
        /// его к своей системе авторизации).
        /// </para>
        /// </summary>
        public Func<string> TokenProvider = null;

        /// <summary>When true, <see cref="VhrSdk.InitializeAsync"/> calls
        /// <c>GET {BridgeBaseUrl}/api/ping</c> and reflects the result in the
        /// connection-state stream. Default: true.</summary>
        public bool PingOnInitialize = true;

        /// <summary>Per-request timeout in seconds. Default: 15.</summary>
        public int RequestTimeoutSeconds = 15;

        /// <summary>When true, the SDK logs request/response lines via <see cref="IVhrLog"/>.</summary>
        public bool VerboseLogging = false;

        /// <summary>
        /// Validates required fields. Throws <see cref="VhrSdkException"/> with a
        /// stable error code if the configuration is unusable.
        /// </summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(BridgeBaseUrl))
                throw new VhrSdkException("config_invalid", "VhrSdkOptions.BridgeBaseUrl is required.");
            if (string.IsNullOrWhiteSpace(ServersBaseUrl))
                throw new VhrSdkException("config_invalid", "VhrSdkOptions.ServersBaseUrl is required.");
            if (string.IsNullOrWhiteSpace(GameId))
                throw new VhrSdkException("config_invalid", "VhrSdkOptions.GameId is required.");
            if (RequestTimeoutSeconds <= 0)
                throw new VhrSdkException("config_invalid", "VhrSdkOptions.RequestTimeoutSeconds must be > 0.");

            // InternalApiKey НЕ обязателен: клиентские WebGL-сборки авторизуются
            // JWT игрока, серверный ключ — опционален и только для серверов.

            // Авторизация по умолчанию: если провайдер токена не задан, на WebGL
            // подставляем дефолтный, который читает access_token из URL страницы.
            TokenProvider ??= DefaultWebGlTokenProvider();

            // Normalize: strip trailing slashes so endpoint concatenation is predictable.
            BridgeBaseUrl = BridgeBaseUrl.TrimEnd('/');
            ServersBaseUrl = ServersBaseUrl.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(GamesBaseUrl))
                GamesBaseUrl = "https://api.vhrweb.ru/games";
            GamesBaseUrl = GamesBaseUrl.TrimEnd('/');

            if (string.IsNullOrWhiteSpace(AuthBaseUrl))
                AuthBaseUrl = "https://api.vhrweb.ru/auth";
            AuthBaseUrl = AuthBaseUrl.TrimEnd('/');

            if (string.IsNullOrWhiteSpace(NotificationsBaseUrl))
                NotificationsBaseUrl = "https://api.vhrweb.ru/notifications";
            NotificationsBaseUrl = NotificationsBaseUrl.TrimEnd('/');

            // Релей опционален; пустое значение восстанавливаем дефолтом, чтобы
            // VhrSdk.Relay всегда был рабочим. URL не триммим по '/' — путь
            // (/ws) значим для WebSocket-эндпоинта.
            if (string.IsNullOrWhiteSpace(RelayBaseUrl))
                RelayBaseUrl = "wss://servers.vhrweb.ru/ws";
            RelayBaseUrl = RelayBaseUrl.Trim();
        }

        /// <summary>
        /// Дефолтный провайдер токена для WebGL. На каждый вызов:
        /// <list type="number">
        /// <item>сначала берёт самый свежий токен из
        /// <see cref="VhrWebGlTokenChannel.GetLatestToken"/> — его присылает
        /// родительская страница (<c>https://vhrgames.ru</c>) через
        /// <c>postMessage</c>, и он обновляется по мере истечения 15-минутного
        /// JWT (см. <see cref="VhrWebGlTokenChannel"/>);</item>
        /// <item>если канал пуст (родитель ещё не прислал токен, самый старт
        /// сессии) — fallback на разбор первичного <c>access_token</c> из
        /// <see cref="Application.absoluteURL"/> (сайт VHR встраивает игру с
        /// <c>?access_token=...</c> один раз при загрузке).</item>
        /// </list>
        /// Вне WebGL (нативные / редакторные сборки) канал — no-op, а URL обычно
        /// не содержит токена, поэтому провайдер вернёт <c>null</c> — там задайте
        /// <see cref="TokenProvider"/> явно (желательно обновляемый).
        /// </summary>
        /// <remarks>
        /// Парсинг URL устойчив: токен ищется и в строке запроса
        /// (<c>?a=b&amp;access_token=...</c>), и во фрагменте
        /// (<c>#access_token=...</c>), значение URL-декодируется, аккуратно
        /// обрабатываются отсутствие схемы/хоста и пустой URL. Делегат безопасно
        /// вызывать повторно: при обновлении токена родителем следующий вызов
        /// вернёт уже новый JWT без пересборки.
        /// </remarks>
        public static Func<string> DefaultWebGlTokenProvider()
        {
            VhrWebGlTokenChannel.EnsureInitialized();
            return () =>
            {
                // Приоритет — свежий токен, присланный родителем через postMessage.
                var latest = VhrWebGlTokenChannel.GetLatestToken();
                if (!string.IsNullOrEmpty(latest))
                    return latest;

                // Fallback: первичный токен из URL страницы (?access_token=...).
                return ExtractAccessToken(SafeAbsoluteUrl());
            };
        }

        private static string SafeAbsoluteUrl()
        {
            try { return Application.absoluteURL; }
            catch { return null; }
        }

        /// <summary>
        /// Извлекает значение <c>access_token</c> из абсолютного URL (строка
        /// запроса либо фрагмент). Возвращает <c>null</c>, если URL пуст или
        /// параметр отсутствует.
        /// </summary>
        internal static string ExtractAccessToken(string absoluteUrl)
        {
            if (string.IsNullOrEmpty(absoluteUrl))
                return null;

            // Собираем кандидатные строки параметров: после '?' и после '#'.
            string FromMarker(char marker)
            {
                int i = absoluteUrl.IndexOf(marker);
                return i >= 0 && i + 1 < absoluteUrl.Length ? absoluteUrl.Substring(i + 1) : null;
            }

            foreach (var query in new[] { FromMarker('?'), FromMarker('#') })
            {
                if (string.IsNullOrEmpty(query))
                    continue;

                foreach (var pair in query.Split('&'))
                {
                    if (string.IsNullOrEmpty(pair))
                        continue;

                    int eq = pair.IndexOf('=');
                    string key = eq >= 0 ? pair.Substring(0, eq) : pair;
                    if (!string.Equals(key, "access_token", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string raw = eq >= 0 && eq + 1 < pair.Length ? pair.Substring(eq + 1) : string.Empty;
                    if (string.IsNullOrEmpty(raw))
                        return null;

                    try { return Uri.UnescapeDataString(raw); }
                    catch { return raw; }
                }
            }

            return null;
        }
    }

    /// <summary>
    /// All errors raised by the SDK carry a stable, machine-readable
    /// <see cref="Code"/> (e.g. <c>config_invalid</c>, <c>http_error</c>,
    /// <c>not_implemented</c>, <c>sdk_required</c>).
    /// </summary>
    public sealed class VhrSdkException : Exception
    {
        /// <summary>Stable machine-readable error code.</summary>
        public string Code { get; }

        /// <summary>HTTP status code when the error originated from a transport call; otherwise 0.</summary>
        public long HttpStatus { get; }

        /// <summary>Creates a new SDK exception.</summary>
        public VhrSdkException(string code, string message, long httpStatus = 0, Exception inner = null)
            : base(message, inner)
        {
            Code = code;
            HttpStatus = httpStatus;
        }
    }
}
