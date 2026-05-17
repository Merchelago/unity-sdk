using UnityEngine;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Tiny logging seam so the SDK never hard-depends on <see cref="Debug"/>
    /// (host games can route SDK logs into their own telemetry).
    /// </summary>
    public interface IVhrLog
    {
        /// <summary>Verbose / diagnostic line (suppressed unless <see cref="VhrSdkOptions.VerboseLogging"/>).</summary>
        void Verbose(string message);

        /// <summary>Informational line.</summary>
        void Info(string message);

        /// <summary>Warning line.</summary>
        void Warn(string message);

        /// <summary>Error line.</summary>
        void Error(string message);
    }

    /// <summary>Default <see cref="IVhrLog"/> writing to the Unity console with a <c>[VHR SDK]</c> prefix.</summary>
    public sealed class VhrUnityLog : IVhrLog
    {
        private readonly bool _verbose;

        /// <summary>Creates the logger. <paramref name="verbose"/> gates <see cref="Verbose"/>.</summary>
        public VhrUnityLog(bool verbose) => _verbose = verbose;

        /// <inheritdoc />
        public void Verbose(string message) { if (_verbose) Debug.Log("[VHR SDK] " + message); }
        /// <inheritdoc />
        public void Info(string message) => Debug.Log("[VHR SDK] " + message);
        /// <inheritdoc />
        public void Warn(string message) => Debug.LogWarning("[VHR SDK] " + message);
        /// <inheritdoc />
        public void Error(string message) => Debug.LogError("[VHR SDK] " + message);
    }
}
