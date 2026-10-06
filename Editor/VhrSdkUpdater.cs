using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace VhrGames.Sdk.Editor
{
    /// <summary>
    /// Обновление SDK одной кнопкой: недостающие реестры → (по согласию) удаление копий ядра R3 →
    /// один запрос <see cref="Client.AddAndRemove"/> с зависимостями и самим SDK → Unity
    /// перекомпилирует скрипты и перезагружает домен → уже НОВЫЙ код SDK сообщает итог.
    /// </summary>
    /// <remarks>
    /// Состояние живёт в <see cref="SessionState"/>: переживает перезагрузку домена и смену кода
    /// SDK. Имена ключей <c>VhrSdk.Update.*</c> — контракт между версиями SDK, не переименовывать.
    /// Один запрос вместо цепочки <c>Client.Add</c> — одна перезагрузка домена и атомарность:
    /// если какой-то пакет не найден, проект не меняется.
    /// </remarks>
    [InitializeOnLoad]
    internal static class VhrSdkUpdater
    {
        internal const string TargetKey = "VhrSdk.Update.Target";
        internal const string FromKey = "VhrSdk.Update.From";
        internal const string PackagesKey = "VhrSdk.Update.Packages";
        internal const string AttemptKey = "VhrSdk.Update.Attempt";

        private static AddAndRemoveRequest _request;
        private static double _requestDoneAt;

        /// <summary>Текущий шаг для окна (пусто — обновление не идёт).</summary>
        public static string Status { get; private set; } = string.Empty;

        /// <summary>Итог последней попытки в этой сессии (для окна).</summary>
        public static string LastResult { get; private set; }

        public static bool LastResultIsError { get; private set; }

        public static bool InProgress => !string.IsNullOrEmpty(SessionState.GetString(TargetKey, string.Empty));

        /// <summary>Окно перерисовывается по этому событию.</summary>
        public static event Action Changed;

        static VhrSdkUpdater()
        {
            EditorApplication.delayCall += ResumeAfterReload;
        }

        /// <summary>
        /// Запускает обновление до <see cref="VhrSdkVersionsInfo.Latest"/>. Для встроенного/локального
        /// SDK кнопки нет — окно показывает инструкцию.
        /// </summary>
        public static void Start(VhrSdkVersionsInfo info)
        {
            if (InProgress || info == null) return;
            var install = VhrSdkInstall.Detect();
            if (!install.CanAutoUpdate)
            {
                Fail($"SDK установлен как «{install.SourceLabel}» — обновите его вручную (см. окно VHR → Обновление SDK).", false);
                return;
            }
            if (!info.HasLatest)
            {
                Fail("Нет сведений о последней версии: сервер VHR недоступен. Повторите позже.", false);
                return;
            }

            SetStatus("Проверяю пакеты проекта…");
            var list = Client.List(true, true);
            Poll(() => list.IsCompleted, () =>
            {
                if (list.Status != StatusCode.Success)
                {
                    Fail("Package Manager не ответил: " + list.Error?.message, true);
                    return;
                }
                Proceed(info, install, list.Result.ToList());
            });
        }

        private static void Proceed(VhrSdkVersionsInfo info, VhrSdkInstall install, List<PackageInfo> packages)
        {
            // 1. Реестры — без них Package Manager не найдёт R3, VContainer и ядро R3 (org.nuget.*).
            try
            {
                VhrSdkProject.EnsureRegistries(info);
            }
            catch (Exception e)
            {
                Fail("Не удалось добавить реестры в Packages/manifest.json: " + e.Message, true);
                return;
            }

            // 2. Копии ядра R3 вне Package Manager (NuGetForUnity, Assets/Plugins) — иначе дубли сборок.
            var copies = VhrSetupCore.FindR3CoreCopies(VhrSdkProject.Root);
            if (copies.Count > 0 && info.Dependencies.ContainsKey("org.nuget.r3"))
            {
                var list = string.Join("\n", copies.Take(10).Select(c => "  " + c.AssetPath));
                if (EditorUtility.DisplayDialog("VHR — обновление SDK",
                        "Ядро R3 ставится пакетом org.nuget.r3. Эти копии дадут дубли сборок:\n\n" + list +
                        "\n\nУдалить их (рекомендуется)?", "Удалить", "Оставить"))
                    VhrSdkProject.DeleteR3Copies(copies);
            }

            // 3. Зависимости, прибитые в manifest.json ниже нужной версии: прямая зависимость проекта
            //    побеждает требование SDK, поэтому поднимаем её явно. Косвенные подтянутся сами.
            var add = new List<string>();
            var skipped = new List<string>();
            foreach (var kv in info.Dependencies.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (kv.Key == info.PackageName || kv.Key.StartsWith("com.unity.", StringComparison.Ordinal)) continue;
                var p = packages.FirstOrDefault(x => x.name == kv.Key);
                if (p == null || !p.isDirectDependency) continue;
                if (VhrSetupCore.SemVer.Compare(p.version, kv.Value) >= 0) continue;
                if (p.source == PackageSource.Registry) add.Add(kv.Key + "@" + kv.Value);
                else skipped.Add($"{kv.Key} ({p.source}, {p.version} < {kv.Value})");
            }
            if (skipped.Count > 0)
                Debug.LogWarning("[VHR SDK] Эти зависимости подключены не из реестра и не обновлены автоматически — " +
                                 "поднимите их вручную:\n" + string.Join("\n", skipped));

            // 4. Сам SDK.
            add.Add(install.Source == PackageSource.Git
                ? (install.GitBase ?? info.GitUrl) + "#v" + info.Latest
                : info.PackageName + "@" + info.Latest);

            SessionState.SetString(TargetKey, info.Latest);
            SessionState.SetString(FromKey, VhrSdk.SdkVersion);
            SessionState.SetString(PackagesKey, string.Join("\n", add));
            SessionState.SetInt(AttemptKey, 1);
            LastResult = null;
            // Даём Unity подхватить новый manifest.json (реестры), затем — один запрос.
            EditorApplication.delayCall += () => SendRequest(add);
        }

        private static void SendRequest(List<string> packages)
        {
            if (!InProgress) return;
            SetStatus("Скачиваю и устанавливаю пакеты: " + string.Join(", ", packages.Select(Short)) + "…");
            Debug.Log("[VHR SDK] Обновление: " + string.Join(", ", packages));
            _requestDoneAt = 0;
            _request = Client.AddAndRemove(packages.ToArray(), null);
            EditorApplication.update -= Watch;
            EditorApplication.update += Watch;
        }

        // Следим за запросом, а после успеха — за компиляцией: если она провалилась, домен не
        // перезагрузится и итог нужно сообщить отсюда (старым кодом).
        private static void Watch()
        {
            if (_request == null)
            {
                EditorApplication.update -= Watch;
                return;
            }
            if (!_request.IsCompleted) return;

            if (_request.Status != StatusCode.Success)
            {
                var error = _request.Error?.message ?? "неизвестная ошибка";
                _request = null;
                EditorApplication.update -= Watch;
                Fail("Package Manager не смог обновить пакеты: " + error +
                     "\n\nПроект не изменён. Частые причины: нет доступа к github.com или реестрам, " +
                     "тег версии ещё не опубликован.", true);
                return;
            }

            var now = EditorApplication.timeSinceStartup;
            if (_requestDoneAt <= 0)
            {
                _requestDoneAt = now;
                SetStatus("Пакеты обновлены, Unity перекомпилирует скрипты…");
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            if (EditorUtility.scriptCompilationFailed && now - _requestDoneAt > 3)
            {
                _request = null;
                EditorApplication.update -= Watch;
                // Цель оставляем: когда ошибки исправят, домен перезагрузится с новым SDK и он сообщит успех.
                SetStatus("Ждём исправления ошибок компиляции…");
                LastResult = "Пакеты обновлены до " + SessionState.GetString(TargetKey, "?") + ", но проект не компилируется — " +
                             "исправьте ошибки в консоли. Частая причина — копии R3 из NuGetForUnity (кнопка «Удалить копии» в этом окне).";
                LastResultIsError = true;
                Debug.LogError("[VHR SDK] " + LastResult);
                Changed?.Invoke();
                return;
            }

            if (now - _requestDoneAt > 90)
            {
                // Домен так и не перезагрузился (например, версия не изменилась) — сообщаем сами.
                _request = null;
                EditorApplication.update -= Watch;
                var target = SessionState.GetString(TargetKey, string.Empty);
                ClearState();
                Succeed(VhrSdk.SdkVersion, target);
            }
        }

        // После перезагрузки домена. Если цель достигнута — это уже код новой версии SDK.
        private static void ResumeAfterReload()
        {
            var target = SessionState.GetString(TargetKey, string.Empty);
            if (string.IsNullOrEmpty(target)) return;
            var from = SessionState.GetString(FromKey, string.Empty);

            if (VhrSetupCore.SemVer.Compare(VhrSdk.SdkVersion, target) >= 0)
            {
                ClearState();
                Succeed(from, VhrSdk.SdkVersion);
                return;
            }

            // Код ещё старый, а запрос в этом домене не идёт — его оборвала перезагрузка домена
            // (например, из-за правки manifest.json). Повторяем: запрос идемпотентен.
            if (_request != null) return;
            var attempt = SessionState.GetInt(AttemptKey, 1);
            var packages = SessionState.GetString(PackagesKey, string.Empty)
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (attempt >= 3 || packages.Count == 0)
            {
                ClearState();
                Fail($"Обновление до {target} не завершилось. Проверьте консоль и Window → Package Manager, затем повторите.", true);
                return;
            }
            SessionState.SetInt(AttemptKey, attempt + 1);
            SendRequest(packages);
        }

        private static void Succeed(string from, string to)
        {
            Status = string.Empty;
            LastResultIsError = false;
            LastResult = $"VHR SDK обновлён: {from} → {to}.";
            Debug.Log($"[VHR SDK] Обновлено: {from} → {to}. Игроки получат новую версию после того, как вы пересоберёте игру " +
                      "и загрузите сборку на платформу.");
            Changed?.Invoke();
            if (!Application.isBatchMode)
                EditorUtility.DisplayDialog("VHR SDK обновлён",
                    $"VHR SDK обновлён: {from} → {to} ✓\n\n" +
                    "Уже опубликованные сборки игры продолжают работать. Новая версия SDK попадёт к игрокам после " +
                    "пересборки игры и загрузки сборки на платформу.\n\nЧто изменилось — в окне VHR → Обновление SDK.",
                    "OK");
        }

        private static void Fail(string message, bool log)
        {
            Status = string.Empty;
            LastResult = message;
            LastResultIsError = true;
            if (log) Debug.LogError("[VHR SDK] " + message);
            ClearState();
            Changed?.Invoke();
            if (!Application.isBatchMode && log)
                EditorUtility.DisplayDialog("VHR — обновление SDK", message, "OK");
        }

        private static void ClearState()
        {
            SessionState.EraseString(TargetKey);
            SessionState.EraseString(FromKey);
            SessionState.EraseString(PackagesKey);
            SessionState.EraseInt(AttemptKey);
        }

        private static void SetStatus(string s)
        {
            Status = s;
            Changed?.Invoke();
        }

        private static void Poll(Func<bool> done, Action then)
        {
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (!done()) return;
                EditorApplication.update -= tick;
                try { then(); }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    Fail("Обновление прервано: " + e.Message, true);
                }
            };
            EditorApplication.update += tick;
        }

        private static string Short(string id)
        {
            var hash = id.LastIndexOf('#');
            if (hash >= 0) return VhrSdkVersionService.PackageName + " " + id.Substring(hash + 1);
            return id.Replace('@', ' ');
        }
    }
}
