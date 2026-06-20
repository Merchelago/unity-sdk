#if UNITY_EDITOR
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace VhrGames.Sdk.Editor
{
    /// <summary>
    /// Однокнопочная сборка совместимого <b>выделенного сервера</b> (Unity
    /// Dedicated Server, Linux x86_64) и упаковка результата в zip, который
    /// разработчик загружает на платформу через тумблер «Мультиплеер».
    /// </summary>
    /// <remarks>
    /// Меню <c>VHR → Собрать серверный билд (Linux x86_64)</c>:
    /// <list type="number">
    /// <item>переключает активную платформу на <see cref="BuildTarget.StandaloneLinux64"/>
    /// с подтаргетом <see cref="StandaloneBuildSubtarget.Server"/> (Dedicated Server);</item>
    /// <item>собирает включённые в Build Settings сцены в <c>Builds/Server/</c>;</item>
    /// <item>зипует папку в <c>Builds/vhr-server.zip</c>;</item>
    /// <item>восстанавливает предыдущую платформу/подтаргет.</item>
    /// </list>
    /// Весь editor-код обёрнут в <c>#if UNITY_EDITOR</c> и лежит в Editor-сборке —
    /// в рантайм-билд не попадает.
    /// </remarks>
    public static class VhrBuildMenu
    {
        private const string MenuPath = "VHR/Собрать серверный билд (Linux x86_64)";

        // Папки относительно корня проекта (рядом с Assets/).
        private const string OutputDir = "Builds/Server";
        private const string ZipPath = "Builds/vhr-server.zip";
        private const string ExecutableName = "vhr-server.x86_64";

        [MenuItem(MenuPath)]
        public static void BuildDedicatedServer()
        {
            // Собираем только включённые сцены из Build Settings (в порядке списка).
            var scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled && !string.IsNullOrEmpty(s.path))
                .Select(s => s.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                Debug.LogError(
                    "[VHR] Нет включённых сцен в Build Settings. " +
                    "Добавьте серверную сцену (File → Build Settings → Scenes In Build) и повторите.");
                return;
            }

            // Запоминаем текущую платформу/подтаргет, чтобы вернуть как было.
            BuildTarget prevTarget = EditorUserBuildSettings.activeBuildTarget;
            BuildTargetGroup prevGroup = BuildPipeline.GetBuildTargetGroup(prevTarget);
            StandaloneBuildSubtarget prevSubtarget = EditorUserBuildSettings.standaloneBuildSubtarget;

            try
            {
                // Готовим выходную папку (чистим прошлый билд, чтобы zip не тащил мусор).
                PrepareOutputDirectory();

                // Переключаемся на Dedicated Server (Linux x86_64).
                // SwitchActiveBuildTarget нужен, чтобы корректно собрать серверный билд
                // даже если активная платформа сейчас другая (напр. WebGL).
                EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Server;
                bool switched = EditorUserBuildSettings.SwitchActiveBuildTarget(
                    BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64);
                if (!switched)
                {
                    Debug.LogError(
                        "[VHR] Не удалось переключиться на платформу Linux Dedicated Server. " +
                        "Установите модуль 'Linux Dedicated Server Build Support' через Unity Hub и повторите.");
                    return;
                }

                var options = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = Path.Combine(OutputDir, ExecutableName),
                    target = BuildTarget.StandaloneLinux64,
                    targetGroup = BuildTargetGroup.Standalone,
                    subtarget = (int)StandaloneBuildSubtarget.Server,
                    options = BuildOptions.None,
                };

                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;

                if (summary.result != BuildResult.Succeeded)
                {
                    Debug.LogError(
                        $"[VHR] Сборка серверного билда не удалась: result={summary.result}, " +
                        $"ошибок={summary.totalErrors}. Смотрите Console / Editor.log.");
                    return;
                }

                // Зипуем папку билда в Builds/vhr-server.zip.
                string zipFull = ZipBuildFolder();

                Debug.Log(
                    $"[VHR] Серверный билд готов: {zipFull}\n" +
                    "Загрузите этот zip на платформе, включив тумблер «Мультиплеер» " +
                    "на странице загрузки игры.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[VHR] Ошибка при сборке серверного билда: {e}");
            }
            finally
            {
                // Восстанавливаем платформу/подтаргет, как было до сборки.
                try
                {
                    EditorUserBuildSettings.standaloneBuildSubtarget = prevSubtarget;
                    if (EditorUserBuildSettings.activeBuildTarget != prevTarget)
                        EditorUserBuildSettings.SwitchActiveBuildTarget(prevGroup, prevTarget);
                }
                catch (Exception e)
                {
                    Debug.LogWarning(
                        $"[VHR] Не удалось восстановить исходную платформу сборки: {e.Message}. " +
                        "Проверьте Build Settings вручную.");
                }
            }
        }

        /// <summary>Создаёт чистую выходную папку <c>Builds/Server</c>.</summary>
        private static void PrepareOutputDirectory()
        {
            if (Directory.Exists(OutputDir))
                Directory.Delete(OutputDir, recursive: true);
            Directory.CreateDirectory(OutputDir);
        }

        /// <summary>
        /// Зипует <see cref="OutputDir"/> в <see cref="ZipPath"/> (перезаписывая
        /// прошлый архив). Возвращает абсолютный путь к zip.
        /// </summary>
        private static string ZipBuildFolder()
        {
            if (File.Exists(ZipPath))
                File.Delete(ZipPath);

            // На всякий случай убедимся, что папка Builds/ существует.
            string zipDir = Path.GetDirectoryName(ZipPath);
            if (!string.IsNullOrEmpty(zipDir) && !Directory.Exists(zipDir))
                Directory.CreateDirectory(zipDir);

            ZipFile.CreateFromDirectory(
                OutputDir, ZipPath, System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: false);

            return Path.GetFullPath(ZipPath);
        }
    }
}
#endif
