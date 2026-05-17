using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Default <see cref="IVhrHttp"/> backed by <see cref="UnityWebRequest"/>.
    /// WebGL-safe (no threads, no <c>System.Net.Http</c>); awaits the request
    /// operation via Unity's <see cref="Awaitable"/> bridge.
    /// </summary>
    public sealed class UnityWebRequestHttp : IVhrHttp
    {
        private readonly IVhrLog _log;

        /// <summary>Creates the transport. <paramref name="log"/> may be null.</summary>
        public UnityWebRequestHttp(IVhrLog log = null) => _log = log;

        /// <inheritdoc />
        public async Task<VhrHttpResponse> SendAsync(
            string method,
            string url,
            string jsonBody,
            IReadOnlyDictionary<string, string> headers,
            int timeoutSeconds,
            CancellationToken ct)
        {
            using var req = new UnityWebRequest(url, method);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.timeout = Mathf.Max(1, timeoutSeconds);

            if (!string.IsNullOrEmpty(jsonBody))
            {
                var bytes = Encoding.UTF8.GetBytes(jsonBody);
                req.uploadHandler = new UploadHandlerRaw(bytes) { contentType = "application/json" };
                req.SetRequestHeader("Content-Type", "application/json");
            }

            req.SetRequestHeader("Accept", "application/json");
            if (headers != null)
            {
                foreach (var kv in headers)
                {
                    if (!string.IsNullOrEmpty(kv.Value))
                        req.SetRequestHeader(kv.Key, kv.Value);
                }
            }

            _log?.Verbose($"[VHR HTTP] {method} {url}");

            try
            {
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    if (ct.IsCancellationRequested)
                    {
                        req.Abort();
                        return new VhrHttpResponse(0, null, false, "canceled");
                    }
                    // Yield a frame. Awaitable.NextFrameAsync exists in Unity 6.
                    await Awaitable.NextFrameAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                req.Abort();
                return new VhrHttpResponse(0, null, false, "canceled");
            }
            catch (Exception ex)
            {
                return new VhrHttpResponse(0, null, false, ex.Message);
            }

            var status = req.responseCode;
            var body = req.downloadHandler != null ? req.downloadHandler.text : null;

            bool transportOk = req.result == UnityWebRequest.Result.Success ||
                                req.result == UnityWebRequest.Result.ProtocolError; // HTTP error still has a body
            bool isSuccess = req.result == UnityWebRequest.Result.Success &&
                             status >= 200 && status < 300;

            string error = isSuccess
                ? null
                : (req.result == UnityWebRequest.Result.ProtocolError
                    ? $"HTTP {status}"
                    : req.error);

            _log?.Verbose($"[VHR HTTP] <- {status} ({req.result}) {Truncate(body)}");

            if (!transportOk && string.IsNullOrEmpty(error))
                error = req.error ?? "connection_error";

            return new VhrHttpResponse(status, body, isSuccess, error);
        }

        private static string Truncate(string s)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= 256 ? s : s.Substring(0, 256) + "…");
    }
}
