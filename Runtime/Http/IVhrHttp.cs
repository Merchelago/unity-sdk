using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Minimal, testable HTTP transport abstraction. The SDK never touches
    /// <c>UnityWebRequest</c> directly; everything goes through this interface so
    /// unit tests can substitute a fake. The default implementation is
    /// <see cref="UnityWebRequestHttp"/>.
    /// </summary>
    public interface IVhrHttp
    {
        /// <summary>
        /// Sends an HTTP request and returns the raw response. Never throws on
        /// non-2xx; transport failures are reported via
        /// <see cref="VhrHttpResponse.Error"/> / <see cref="VhrHttpResponse.IsSuccess"/>.
        /// </summary>
        /// <param name="method">HTTP verb (<c>GET</c>, <c>POST</c>, ...).</param>
        /// <param name="url">Absolute request URL.</param>
        /// <param name="jsonBody">JSON request body, or <c>null</c> for none.</param>
        /// <param name="headers">Additional request headers.</param>
        /// <param name="timeoutSeconds">Per-request timeout.</param>
        /// <param name="ct">Cancellation token.</param>
        Task<VhrHttpResponse> SendAsync(
            string method,
            string url,
            string jsonBody,
            IReadOnlyDictionary<string, string> headers,
            int timeoutSeconds,
            CancellationToken ct);
    }
}
