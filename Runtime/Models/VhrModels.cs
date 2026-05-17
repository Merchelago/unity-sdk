using System;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Connection / readiness state of the SDK, surfaced as an R3
    /// <c>Observable&lt;VhrConnectionState&gt;</c> by <see cref="IVhrSession"/>.
    /// </summary>
    public enum VhrConnectionState
    {
        /// <summary>SDK constructed but <see cref="VhrSdk.InitializeAsync"/> not yet called.</summary>
        Uninitialized = 0,

        /// <summary>Initialization in progress (validating options, optional ping).</summary>
        Connecting = 1,

        /// <summary>Options valid and (if pinged) the bridge responded. Ready for calls.</summary>
        Connected = 2,

        /// <summary>Initialization failed or the bridge is unreachable. Calls may still be retried.</summary>
        Faulted = 3
    }

    /// <summary>
    /// The current coin / soft-currency balance for a user as returned by
    /// <c>GET /bridge/api/balance/{userId}</c>.
    /// </summary>
    [Serializable]
    public sealed class VhrBalance
    {
        /// <summary>Owning VHR user id.</summary>
        public string userId;

        /// <summary>Current coin balance (non-negative).</summary>
        public long coins;

        /// <summary>Server-side UTC timestamp the balance was computed (ISO-8601), if provided.</summary>
        public string updatedAtUtc;
    }

    /// <summary>
    /// Event pushed onto <see cref="IVhrEconomy.BalanceChanged"/> whenever a
    /// grant / spend / purchase locally completes and returns a new balance.
    /// </summary>
    [Serializable]
    public readonly struct BalanceChanged
    {
        /// <summary>User whose balance changed.</summary>
        public readonly string UserId;

        /// <summary>Balance after the operation.</summary>
        public readonly long NewBalance;

        /// <summary>Signed delta applied (+grant, -spend).</summary>
        public readonly long Delta;

        /// <summary>Reason tag (e.g. <c>"grant/coins"</c>, <c>"spend"</c>, <c>"purchase"</c>).</summary>
        public readonly string Reason;

        /// <summary>Creates a new <see cref="BalanceChanged"/> event.</summary>
        public BalanceChanged(string userId, long newBalance, long delta, string reason)
        {
            UserId = userId;
            NewBalance = newBalance;
            Delta = delta;
            Reason = reason;
        }
    }

    /// <summary>Result envelope for an economy mutation (grant / spend / purchase).</summary>
    [Serializable]
    public sealed class VhrEconomyResult
    {
        /// <summary>True when the operation was applied (or was an idempotent replay).</summary>
        public bool success;

        /// <summary>Balance after the operation.</summary>
        public long balance;

        /// <summary>True when the server detected this <c>externalId</c> as an idempotent replay.</summary>
        public bool idempotentReplay;

        /// <summary>Human-readable message / error code from the bridge, if any.</summary>
        public string message;
    }

    /// <summary>A single leaderboard row.</summary>
    [Serializable]
    public sealed class VhrLeaderboardEntry
    {
        /// <summary>1-based rank within the requested period.</summary>
        public int rank;

        /// <summary>VHR user id.</summary>
        public string userId;

        /// <summary>Display name, if the bridge resolves it.</summary>
        public string displayName;

        /// <summary>Best score for the period.</summary>
        public long score;
    }

    /// <summary>Top-N leaderboard response.</summary>
    [Serializable]
    public sealed class VhrLeaderboardPage
    {
        /// <summary>Period the page was computed for.</summary>
        public string period;

        /// <summary>Ordered entries (rank ascending).</summary>
        public VhrLeaderboardEntry[] entries;

        /// <summary>
        /// True when the bridge replied <c>501 Not Implemented</c> (leaderboard persistence
        /// is a next-wave seam). <see cref="entries"/> will be empty.
        /// </summary>
        public bool notImplemented;
    }

    /// <summary>Leaderboard aggregation period.</summary>
    public enum VhrLeaderboardPeriod
    {
        /// <summary>All-time best.</summary>
        AllTime = 0,
        /// <summary>Rolling / calendar day.</summary>
        Daily = 1,
        /// <summary>Rolling / calendar week.</summary>
        Weekly = 2,
        /// <summary>Rolling / calendar month.</summary>
        Monthly = 3
    }

    /// <summary>A server binding between a game and a backing server instance.</summary>
    [Serializable]
    public sealed class VhrServerBinding
    {
        /// <summary>Binding id.</summary>
        public string bindingId;

        /// <summary>Game id this binding belongs to.</summary>
        public string gameId;

        /// <summary>Endpoint the game should connect to, or empty for the noop provider.</summary>
        public string endpoint;

        /// <summary>Provider status (e.g. <c>"noop"</c>, <c>"ready"</c>, <c>"provisioning"</c>).</summary>
        public string status;
    }

    /// <summary>Generic transport-level result of an HTTP call made through <see cref="IVhrHttp"/>.</summary>
    public readonly struct VhrHttpResponse
    {
        /// <summary>HTTP status code (0 when the request never reached the server).</summary>
        public readonly long StatusCode;

        /// <summary>Raw response body (may be empty).</summary>
        public readonly string Body;

        /// <summary>True for 2xx.</summary>
        public readonly bool IsSuccess;

        /// <summary>Transport / protocol error text, if any.</summary>
        public readonly string Error;

        /// <summary>Creates an HTTP response value.</summary>
        public VhrHttpResponse(long statusCode, string body, bool isSuccess, string error)
        {
            StatusCode = statusCode;
            Body = body;
            IsSuccess = isSuccess;
            Error = error;
        }
    }
}
