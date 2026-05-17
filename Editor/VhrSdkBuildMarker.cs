using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace VhrGames.Sdk.Editor
{
    /// <summary>
    /// <b>SDK handshake — the reason this package is MANDATORY.</b>
    /// <para>
    /// Before every build this hook writes
    /// <c>Assets/StreamingAssets/vhr-sdk.json</c>. Unity copies
    /// <c>StreamingAssets</c> verbatim into the build output (for WebGL:
    /// <c>Build/StreamingAssets/vhr-sdk.json</c>), so the marker ships inside the
    /// uploaded artifact.
    /// </para>
    /// <para>
    /// On <c>confirm-upload</c> the VHR backend scans the uploaded build for this
    /// file and validates its contents. <b>No marker → the upload is rejected
    /// (<c>sdk_required</c>).</b> A marker whose <c>sdkVersion</c> is below the
    /// backend's minimum is rejected with <c>sdk_outdated</c>. Because only this
    /// SDK package emits the marker, a build that did not integrate the SDK simply
    /// cannot be published.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Marker JSON contract (exact field names):
    /// <code>
    /// {
    ///   "sdk": "vhr-unity-sdk",
    ///   "sdkVersion": "1.0.0",
    ///   "unityVersion": "&lt;Application.unityVersion&gt;",
    ///   "buildTimeUtc": "&lt;ISO-8601 UTC&gt;"
    /// }
    /// </code>
    /// The post-build step intentionally leaves the file in <c>StreamingAssets</c>
    /// so subsequent editor sessions keep the asset under version control if
    /// desired; it is harmless and overwritten on the next build.
    /// </remarks>
    public sealed class VhrSdkBuildMarker : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        /// <summary>Logical SDK id written to the marker (must match backend validator).</summary>
        public const string MarkerSdkId = "vhr-unity-sdk";

        /// <summary>Marker file name expected by the backend.</summary>
        public const string MarkerFileName = "vhr-sdk.json";

        private const string StreamingAssetsDir = "Assets/StreamingAssets";

        /// <summary>Runs early so the marker exists before asset packaging.</summary>
        public int callbackOrder => 0;

        /// <summary>Writes / refreshes the marker before the build is packaged.</summary>
        public void OnPreprocessBuild(BuildReport report)
        {
            try
            {
                WriteMarker(report.summary.platform);
            }
            catch (Exception ex)
            {
                // Fail the build loudly: a missing marker means an unpublishable artifact.
                throw new BuildFailedException(
                    $"[VHR SDK] Failed to write mandatory build marker '{MarkerFileName}': {ex.Message}");
            }
        }

        /// <summary>Logs the outcome and the marker location inside the build.</summary>
        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform == BuildTarget.WebGL)
            {
                Debug.Log(
                    $"[VHR SDK] Build marker shipped at " +
                    $"'{report.summary.outputPath}/StreamingAssets/{MarkerFileName}'. " +
                    $"Backend will validate this on confirm-upload.");
            }
            else
            {
                Debug.Log($"[VHR SDK] Build marker written ({MarkerFileName}).");
            }
        }

        /// <summary>Serializes and writes the marker into <c>StreamingAssets</c>.</summary>
        private static void WriteMarker(BuildTarget platform)
        {
            if (!Directory.Exists(StreamingAssetsDir))
                Directory.CreateDirectory(StreamingAssetsDir);

            var marker = new VhrSdkMarker
            {
                sdk = MarkerSdkId,
                sdkVersion = VhrSdk.SdkVersion,
                unityVersion = Application.unityVersion,
                buildTimeUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                buildTarget = platform.ToString()
            };

            var path = Path.Combine(StreamingAssetsDir, MarkerFileName);
            File.WriteAllText(path, JsonUtility.ToJson(marker, prettyPrint: true));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            Debug.Log($"[VHR SDK] Wrote build marker v{VhrSdk.SdkVersion} → {path}");
        }

        /// <summary>Serializable marker payload. Field names are part of the backend contract.</summary>
        [Serializable]
        private sealed class VhrSdkMarker
        {
            public string sdk;
            public string sdkVersion;
            public string unityVersion;
            public string buildTimeUtc;
            public string buildTarget;
        }
    }
}
