using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace VhrGames.Sdk.Editor
{
    /// <summary>
    /// Перед WebGL-сборкой сверяет версию SDK с сервером (свежий ответ или сохранённый):
    /// ниже <c>minSupported</c> — сборка останавливается (платформа всё равно отклонит загрузку
    /// с <c>sdk_outdated</c>), ниже <c>latest</c> — предупреждение. Нет связи и нет кэша — не мешает.
    /// </summary>
    public sealed class VhrSdkVersionBuildGuard : IPreprocessBuildWithReport
    {
        /// <summary>Раньше проверки ключа и маркера сборки: нет смысла собирать то, что отклонят.</summary>
        public int callbackOrder => -20;

        /// <inheritdoc />
        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.WebGL) return;

            var info = VhrSdkVersionService.GetForBuild();
            var installed = VhrSdkVersionService.InstalledVersion;
            if (!info.IsKnown)
            {
                Debug.Log($"[VHR SDK] Не удалось проверить актуальность SDK {installed} ({info.Error ?? "нет связи"}) — сборка продолжается.");
                return;
            }

            if (info.IsBelowMinimum(installed))
                throw new BuildFailedException(
                    $"[VHR SDK] Версия SDK {installed} ниже минимально поддерживаемой {info.MinSupported}: платформа отклонит " +
                    "загрузку этой сборки (sdk_outdated). Обновите SDK — меню VHR → Обновление SDK — и соберите игру заново.");

            if (info.IsUpdateAvailable(installed))
                Debug.LogWarning($"[VHR SDK] Собирается с SDK {installed}, а последняя версия — {info.Latest}. " +
                                 "Сборка пройдёт; обновиться можно в меню VHR → Обновление SDK.");
        }
    }
}
