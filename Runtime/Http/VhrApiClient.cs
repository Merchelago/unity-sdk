using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Typed JSON wrapper over <see cref="IVhrHttp"/>. Owns header injection
    /// (auth token, optional internal API key, game id), JSON (de)serialization
    /// via <see cref="JsonUtility"/>, and uniform error mapping to
    /// <see cref="VhrSdkException"/>.
    /// <para>
    /// Авторизация: всегда шлём <c>Authorization: Bearer &lt;token&gt;</c>, когда
    /// <see cref="VhrSdkOptions.TokenProvider"/> вернул непустой токен (JWT
    /// игрока — основной путь для клиентских WebGL-сборок).
    /// <c>X-Internal-Api-Key</c> добавляется ТОЛЬКО если
    /// <see cref="VhrSdkOptions.InternalApiKey"/> непуст (серверный сценарий);
    /// пустой заголовок не отправляется.
    /// </para>
    /// </summary>
    public sealed class VhrApiClient
    {
        private readonly IVhrHttp _http;
        private readonly VhrSdkOptions _options;
        private readonly IVhrLog _log;

        /// <summary>Creates the API client.</summary>
        public VhrApiClient(IVhrHttp http, VhrSdkOptions options, IVhrLog log)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _log = log;
        }

        /// <summary>
        /// Sends a request and deserializes a 2xx body into <typeparamref name="TResponse"/>.
        /// Throws <see cref="VhrSdkException"/> on transport / non-2xx errors
        /// (except 501, see <paramref name="allowNotImplemented"/>).
        /// </summary>
        /// <typeparam name="TResponse">Serializable response DTO.</typeparam>
        /// <param name="method">HTTP verb.</param>
        /// <param name="absoluteUrl">Fully-qualified URL.</param>
        /// <param name="body">Optional request DTO (serialized to JSON).</param>
        /// <param name="allowNotImplemented">
        /// When true, a <c>501</c> response returns <c>default</c> instead of throwing
        /// (used by the leaderboard next-wave seam).
        /// </param>
        /// <param name="ct">Cancellation token.</param>
        public async Task<TResponse> SendAsync<TResponse>(
            string method,
            string absoluteUrl,
            object body = null,
            bool allowNotImplemented = false,
            CancellationToken ct = default)
        {
            string json = body == null ? null : JsonUtility.ToJson(body);

            // Токен, использованный для этой попытки (для детекции "новый ли").
            var headers = BuildHeaders();
            headers.TryGetValue("Authorization", out var usedAuth);

            var resp = await _http.SendAsync(
                method, absoluteUrl, json, headers, _options.RequestTimeoutSeconds, ct);

            // Жизненный цикл JWT: токен живёт ~15 мин, сессии длиннее. На 401 от
            // моста просим родительскую страницу (vhrgames.ru) прислать свежий
            // токен через postMessage, ждём НОВЫЙ (отличный от использованного)
            // и повторяем запрос РОВНО один раз. Вне WebGL postMessage нет —
            // просто перечитываем TokenProvider (вдруг хост уже обновил).
            if (resp.StatusCode == 401)
            {
                var refreshed = await TryRefreshTokenAsync(usedAuth, ct);
                if (refreshed)
                {
                    var retryHeaders = BuildHeaders();
                    resp = await _http.SendAsync(
                        method, absoluteUrl, json, retryHeaders, _options.RequestTimeoutSeconds, ct);
                }
            }

            if (allowNotImplemented && resp.StatusCode == 501)
                return default;

            if (!resp.IsSuccess)
            {
                throw new VhrSdkException(
                    MapErrorCode(resp.StatusCode),
                    $"{method} {absoluteUrl} failed: {resp.Error} (status {resp.StatusCode}) body={resp.Body}",
                    resp.StatusCode);
            }

            if (string.IsNullOrWhiteSpace(resp.Body) || typeof(TResponse) == typeof(VhrVoid))
                return default;

            try
            {
                return JsonUtility.FromJson<TResponse>(resp.Body);
            }
            catch (Exception ex)
            {
                throw new VhrSdkException("deserialize_error",
                    $"Could not parse response from {absoluteUrl}: {ex.Message}. Body={resp.Body}",
                    resp.StatusCode, ex);
            }
        }

        /// <summary>Builds the standard header set, evaluating the lazy token provider.</summary>
        private Dictionary<string, string> BuildHeaders()
        {
            var h = new Dictionary<string, string>
            {
                ["X-Vhr-Game-Id"] = _options.GameId,
                ["X-Vhr-Sdk-Version"] = VhrSdk.SdkVersion
            };

            // Серверный ключ — опционален; в клиентских WebGL-сборках он не
            // задаётся, поэтому пустой заголовок не шлём (не утекает секрет).
            if (!string.IsNullOrEmpty(_options.InternalApiKey))
                h["X-Internal-Api-Key"] = _options.InternalApiKey;

            // Основной путь авторизации: JWT игрока. На WebGL TokenProvider по
            // умолчанию читает access_token из URL страницы.
            var token = _options.TokenProvider?.Invoke();
            if (!string.IsNullOrEmpty(token))
                h["Authorization"] = "Bearer " + token;

            return h;
        }

        /// <summary>
        /// Реакция на 401: пытается получить НОВЫЙ токен (отличный от
        /// <paramref name="usedAuthHeader"/>, который только что не сработал).
        /// На WebGL дёргает <see cref="VhrWebGlTokenChannel.RequestRefresh"/>
        /// (postMessage родителю) и опрашивает
        /// <see cref="VhrSdkOptions.TokenProvider"/> до ~5 с с короткими паузами,
        /// пока не появится новое значение. Вне WebGL postMessage нет — делает
        /// один перечит провайдера (вдруг хост уже подложил свежий токен).
        /// WebGL-safe: без потоков, кооперативное ожидание через
        /// <see cref="Awaitable"/>.
        /// </summary>
        /// <returns><c>true</c>, если доступен новый непустой токен и стоит
        /// повторить запрос; иначе <c>false</c>.</returns>
        private async Task<bool> TryRefreshTokenAsync(string usedAuthHeader, CancellationToken ct)
        {
            var provider = _options.TokenProvider;
            if (provider == null)
                return false;

            // "Bearer X" → "X" для честного сравнения с тем, что вернёт провайдер.
            string usedToken = usedAuthHeader;
            if (!string.IsNullOrEmpty(usedToken) && usedToken.StartsWith("Bearer "))
                usedToken = usedToken.Substring("Bearer ".Length);

            bool IsNew(string t) => !string.IsNullOrEmpty(t) && t != usedToken;

            // Вне WebGL postMessage недоступен: один перечит провайдера.
            if (!VhrWebGlTokenChannel.IsSupported)
            {
                try { return IsNew(provider.Invoke()); }
                catch { return false; }
            }

            // WebGL: попросим родителя (vhrgames.ru) прислать свежий токен и
            // подождём появления НОВОГО значения до ~5 с (≈ 10 шагов по 0.5 с).
            try { VhrWebGlTokenChannel.RequestRefresh(); }
            catch { /* деградируем тихо */ }

            const int maxAttempts = 10;
            for (int i = 0; i < maxAttempts; i++)
            {
                if (ct.IsCancellationRequested)
                    return false;

                try
                {
                    if (IsNew(provider.Invoke()))
                    {
                        _log?.Verbose("[VHR HTTP] токен обновлён родителем — повтор запроса");
                        return true;
                    }
                }
                catch
                {
                    // Провайдер кинул — прекращаем попытки обновления.
                    return false;
                }

                try { await Awaitable.WaitForSecondsAsync(0.5f, ct); }
                catch (OperationCanceledException) { return false; }
            }

            _log?.Warn("[VHR HTTP] свежий токен не получен за отведённое время — оставляем 401");
            return false;
        }

        private static string MapErrorCode(long status) => status switch
        {
            401 => "unauthorized",
            403 => "forbidden",
            404 => "not_found",
            409 => "conflict",
            501 => "not_implemented",
            0 => "connection_error",
            _ => "http_error"
        };
    }

    /// <summary>Marker type for endpoints with no meaningful response body.</summary>
    [Serializable]
    public sealed class VhrVoid { }
}
