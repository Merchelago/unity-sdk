using UnityEditor;
using UnityEngine;

namespace VhrGames.Sdk.Editor
{
    /// <summary>
    /// Автопроверка версии SDK при запуске редактора: раз за сессию редактора, к серверу —
    /// не чаще раза в сутки (иначе — сохранённый ответ), асинхронно, без блокировки.
    /// <list type="bullet">
    /// <item>вышла новая версия — одно предупреждение в консоль (для каждой версии один раз);</item>
    /// <item>установленная ниже <c>minSupported</c> — ошибка в консоль и один диалог за сессию
    /// «Загрузка игры на платформу будет отклонена — обновить сейчас?».</item>
    /// </list>
    /// Офлайн и в batch mode — молчит.
    /// </summary>
    [InitializeOnLoad]
    internal static class VhrSdkUpdateChecker
    {
        private const string SessionDoneKey = "VhrSdk.UpdateCheck.Done";
        private const string MinDialogKey = "VhrSdk.UpdateCheck.MinDialogShown";

        // Для какой версии уже предупредили — свой ключ у каждого проекта.
        private static string NotifiedKey => VhrEditorSandbox.Prefix + "Update.NotifiedVersion";

        static VhrSdkUpdateChecker()
        {
            if (Application.isBatchMode) return;
            if (SessionState.GetBool(SessionDoneKey, false)) return;
            SessionState.SetBool(SessionDoneKey, true);
            EditorApplication.delayCall += Run;
        }

        private static void Run()
        {
            if (VhrSdkUpdater.InProgress) return; // итог сообщит обновление
            if (VhrSdkVersionService.IsCheckDue())
            {
                VhrSdkVersionService.FetchAsync(Evaluate);
                return;
            }
            var cached = VhrSdkVersionService.LoadCached();
            if (cached != null) Evaluate(cached);
        }

        private static void Evaluate(VhrSdkVersionsInfo info)
        {
            if (info == null || !info.IsKnown) return;
            var installed = VhrSdkVersionService.InstalledVersion;

            if (info.IsBelowMinimum(installed))
            {
                Debug.LogError($"[VHR SDK] Установлена версия {installed}, а платформа принимает сборки с SDK не ниже " +
                               $"{info.MinSupported}: загрузку игры отклонят (sdk_outdated). Обновите SDK: меню VHR → Обновление SDK.");
                if (SessionState.GetBool(MinDialogKey, false)) return;
                SessionState.SetBool(MinDialogKey, true);
                var install = VhrSdkInstall.Detect();
                var update = EditorUtility.DisplayDialog("VHR SDK устарел",
                    $"Установлен VHR SDK {installed}, минимальная поддерживаемая версия — {info.MinSupported}.\n\n" +
                    "Загрузка игры на платформу будет отклонена — обновить сейчас?",
                    install.CanAutoUpdate ? $"Обновить до {info.Latest}" : "Открыть окно обновления", "Позже");
                if (!update) return;
                VhrSdkUpdateWindow.Open();
                if (install.CanAutoUpdate) VhrSdkUpdater.Start(info);
                return;
            }

            if (info.IsUpdateAvailable(installed) && EditorPrefs.GetString(NotifiedKey, string.Empty) != info.Latest)
            {
                EditorPrefs.SetString(NotifiedKey, info.Latest);
                Debug.LogWarning($"[VHR SDK] Вышла версия {info.Latest} (у вас {installed}). Обновить в один клик: меню VHR → Обновление SDK.");
            }
        }
    }
}
