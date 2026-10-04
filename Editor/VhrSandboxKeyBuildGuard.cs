using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace VhrGames.Sdk.Editor
{
    /// <summary>
    /// Перед каждой сборкой проверяет, что песочный ключ (окно <c>VHR → Тестирование
    /// в Editor</c>) не попал в проект: сцены, префабы, ScriptableObject и другие
    /// <c>.asset</c>, ProjectSettings, StreamingAssets/Resources, скрипты.
    /// </summary>
    /// <remarks>
    /// SDK сам ключ в сборку не кладёт: он хранится в <c>EditorPrefs</c>, а код, который
    /// его читает, компилируется только в Editor (<c>#if UNITY_EDITOR</c>). Эта проверка
    /// ловит ручные ошибки — ключ вставили в поле компонента, ScriptableObject или код.
    /// При находке — ошибка в консоли и (в интерактивном режиме) вопрос «продолжить
    /// сборку?»; в batch mode — только предупреждение.
    /// </remarks>
    public sealed class VhrSandboxKeyBuildGuard : IPreprocessBuildWithReport
    {
        private const long MaxFileBytes = 32L * 1024 * 1024;

        private static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".asset", ".prefab", ".unity", ".json", ".txt", ".cs", ".xml", ".yaml", ".yml", ".bytes",
            ".jslib", ".jspre", ".js", ".html", ".htm", ".csv", ".ini", ".cfg", ".config", ".playable", ".mat"
        };

        private static readonly string[] Roots = { "Assets", "ProjectSettings", "Packages" };

        /// <summary>Раньше маркера сборки (порядок не критичен).</summary>
        public int callbackOrder => -10;

        /// <inheritdoc />
        public void OnPreprocessBuild(BuildReport report)
        {
            string key;
            try { key = VhrEditorSandbox.Key; }
            catch { return; }
            if (string.IsNullOrEmpty(key)) return;

            // Ищем подпись JWT (3-я часть) — она уникальна и ловит ключ даже без
            // префикса "Bearer " или с переносом заголовка; короткую — целиком.
            var parts = key.Split('.');
            var needle = parts.Length == 3 && parts[2].Length >= 20 ? parts[2] : key;

            var hits = new List<string>();
            foreach (var root in Roots)
            {
                if (!Directory.Exists(root)) continue;
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
                catch { continue; }

                foreach (var f in files)
                {
                    if (hits.Count >= 20) break;
                    if (!Extensions.Contains(Path.GetExtension(f))) continue;
                    try
                    {
                        var fi = new FileInfo(f);
                        if (fi.Length == 0 || fi.Length > MaxFileBytes) continue;
                        if (File.ReadAllText(f).IndexOf(needle, StringComparison.Ordinal) >= 0)
                            hits.Add(f.Replace('\\', '/'));
                    }
                    catch
                    {
                        // файл занят/недоступен — пропускаем
                    }
                }
            }

            if (hits.Count == 0)
            {
                Debug.Log("[VHR SDK] Проверка сборки: песочный ключ в файлах проекта не найден ✓ (он хранится только в EditorPrefs).");
                return;
            }

            var list = string.Join("\n", hits);
            var message =
                "Песочный ключ VHR найден в файлах проекта — он может попасть в сборку:\n" + list +
                "\n\nКлюч должен храниться только в окне VHR → Тестирование в Editor (EditorPrefs). " +
                "Удалите его из этих файлов; если ключ уже утёк — получите новый на сайте.";
            Debug.LogError("[VHR SDK] " + message);

            if (Application.isBatchMode)
            {
                Debug.LogWarning("[VHR SDK] Batch mode: сборка продолжается, но ключ нужно убрать из проекта.");
                return;
            }

            if (!EditorUtility.DisplayDialog("VHR — песочный ключ в проекте", message, "Продолжить сборку", "Отменить сборку"))
                throw new BuildFailedException("[VHR SDK] Сборка отменена: песочный ключ найден в файлах проекта.");
        }
    }
}
