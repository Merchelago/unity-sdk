using System;
using R3;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Holds per-run identity (game id + lazily-resolved auth token) and exposes
    /// the SDK connection state as an R3 <see cref="Observable{T}"/>.
    /// </summary>
    public interface IVhrSession
    {
        /// <summary>The configured VHR game id.</summary>
        string GameId { get; }

        /// <summary>Current auth/session token (may be null for anonymous play).</summary>
        string CurrentToken { get; }

        /// <summary>Latest known connection state.</summary>
        VhrConnectionState State { get; }

        /// <summary>
        /// Hot stream of connection-state transitions. Replays the current value to
        /// new subscribers (backed by a <c>ReactiveProperty</c>).
        /// </summary>
        Observable<VhrConnectionState> StateChanged { get; }
    }

    /// <summary>Default <see cref="IVhrSession"/>. The owning SDK pushes state via <see cref="SetState"/>.</summary>
    public sealed class VhrSession : IVhrSession, IDisposable
    {
        private readonly VhrSdkOptions _options;
        private readonly ReactiveProperty<VhrConnectionState> _state =
            new(VhrConnectionState.Uninitialized);

        /// <summary>Creates the session from validated options.</summary>
        public VhrSession(VhrSdkOptions options) => _options = options;

        /// <inheritdoc />
        public string GameId => _options.GameId;

        /// <inheritdoc />
        public string CurrentToken => _options.TokenProvider?.Invoke();

        /// <inheritdoc />
        public VhrConnectionState State => _state.Value;

        /// <inheritdoc />
        public Observable<VhrConnectionState> StateChanged => _state;

        /// <summary>Internal: updates the connection state and notifies subscribers.</summary>
        internal void SetState(VhrConnectionState state) => _state.Value = state;

        /// <inheritdoc />
        public void Dispose() => _state.Dispose();
    }
}
