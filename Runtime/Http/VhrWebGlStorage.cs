using System;
using System.IO;
using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace VhrGames.Sdk
{
    /// <summary>
    /// Локальные файлы SDK (SDK 1.12.0+): корень <c>Application.persistentDataPath/vhr-sdk</c> и
    /// синхронизация с IndexedDB на WebGL через <c>VhrStorageBridge.jslib</c>.
    /// <para>
    /// На WebGL <c>persistentDataPath</c> — IDBFS: запись попадает в память, а в IndexedDB браузера —
    /// только после <c>FS.syncfs</c>. Каждая атомарная запись (<see cref="VhrAtomicFile"/>) просит
    /// отложенную синхронизацию (одна на серию записей, через ~1 с), скрытие/закрытие страницы —
    /// немедленную (<see cref="RequestSync"/> с <c>immediate = true</c>). Вне WebGL файлы сразу на
    /// диске — синхронизация не нужна (no-op).
    /// </para>
    /// </summary>
    internal static class VhrWebGlStorage
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void VhrStorage_Sync(int immediate);

        private static bool _broken;
#endif

        private static string _root;
        private static bool _rootResolved;

        /// <summary>
        /// Подключает отложенную синхронизацию к каждой записи <see cref="VhrAtomicFile"/>.
        /// Идемпотентно; вызывать с главного потока.
        /// </summary>
        internal static void EnsureInitialized()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            VhrAtomicFile.Changed = OnFileChanged;
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        private static void OnFileChanged() => RequestSync(false);
#endif

        /// <summary>
        /// Корень локальных файлов SDK (<c>persistentDataPath/vhr-sdk</c>) или <c>null</c>, если
        /// платформа не дала <c>persistentDataPath</c> — тогда очередь и неотправленные сохранения
        /// живут только в памяти. Первый вызов — с главного потока.
        /// </summary>
        internal static string TryGetRoot()
        {
            if (_rootResolved) return _root;
            _rootResolved = true;
            try
            {
                var p = Application.persistentDataPath;
                _root = string.IsNullOrEmpty(p) ? null : Path.Combine(p, "vhr-sdk");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VHR SDK] persistentDataPath недоступен — офлайн-очередь только в памяти: " + e.Message);
                _root = null;
            }
            return _root;
        }

        /// <summary>
        /// WebGL: сохранить файловую систему в IndexedDB — отложенно (<paramref name="immediate"/> =
        /// <c>false</c>, одна синхронизация на серию записей) или сразу после текущего события
        /// страницы (<c>true</c>: скрытие/закрытие вкладки). Вне WebGL — no-op.
        /// </summary>
        internal static void RequestSync(bool immediate)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (_broken) return;
            try { VhrStorage_Sync(immediate ? 1 : 0); }
            catch (Exception e)
            {
                _broken = true;
                Debug.LogWarning("[VHR SDK] плагин VhrStorageBridge недоступен — файлы SDK могут не пережить закрытие вкладки: " + e.Message);
            }
#endif
        }
    }
}
