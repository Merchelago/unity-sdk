using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.Networking;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace VhrGames.Sdk.Editor
{
    /// <summary>Откуда взяты сведения о версиях.</summary>
    internal enum VhrSdkVersionsSource
    {
        /// <summary>Только что получены с сервера.</summary>
        Server,
        /// <summary>Сохранённый ранее ответ сервера (нет связи или проверка была недавно).</summary>
        Cache,
        /// <summary>Сервер ни разу не ответил — встроенные значения, проверки версий не мешают.</summary>
        Fallback
    }

    /// <summary>
    /// Ответ <c>GET {API}/games/api/Sdk/versions</c>: последняя и минимальная поддерживаемая
    /// версии SDK, release notes, зависимости последней версии, реестры, устаревшие API.
    /// </summary>
    internal sealed class VhrSdkVersionsInfo
    {
        public string Latest;
        public string MinSupported;
        public string ReleasedAt;
        public string ReleaseNotes;
        public string ChangelogUrl = VhrSdkVersionService.DefaultChangelogUrl;
        public string GitUrl = VhrSdkVersionService.DefaultGitUrl;
        public string PackageName = VhrSdkVersionService.PackageName;
        public readonly Dictionary<string, string> Dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Features = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly List<Registry> Registries = new List<Registry>();
        public readonly List<Deprecation> Deprecations = new List<Deprecation>();

        public VhrSdkVersionsSource Source;
        public DateTime FetchedAtUtc;
        /// <summary>Почему не удалось спросить сервер (для Cache/Fallback), иначе null.</summary>
        public string Error;

        public sealed class Registry
        {
            public string Name;
            public string Url;
            public readonly List<string> Scopes = new List<string>();
        }

        public sealed class Deprecation
        {
            public string Since;
            public string Api;
            public string Message;

            /// <summary>Имя для поиска в коде: «IVhrEconomy.SpendAsync» → «SpendAsync».</summary>
            public string Identifier
            {
                get
                {
                    var s = (Api ?? string.Empty).Trim();
                    var paren = s.IndexOf('(');
                    if (paren >= 0) s = s.Substring(0, paren);
                    var dot = s.LastIndexOf('.');
                    return dot >= 0 ? s.Substring(dot + 1) : s;
                }
            }
        }

        /// <summary>Есть данные сервера (свежие или из кэша) — можно сравнивать версии.</summary>
        public bool IsKnown => Source != VhrSdkVersionsSource.Fallback;

        public bool HasLatest => IsKnown && VhrSetupCore.SemVer.IsValid(Latest);

        public bool HasMinSupported => IsKnown && VhrSetupCore.SemVer.IsValid(MinSupported);

        /// <summary>Установленная версия ниже минимальной поддерживаемой — загрузку сборки отклонят.</summary>
        public bool IsBelowMinimum(string installed) =>
            HasMinSupported && VhrSetupCore.SemVer.Compare(installed, MinSupported) < 0;

        /// <summary>Вышла версия новее установленной.</summary>
        public bool IsUpdateAvailable(string installed) =>
            HasLatest && VhrSetupCore.SemVer.Compare(installed, Latest) < 0;

        /// <summary>Разбирает ответ сервера; null — не JSON или нет <c>latest</c>.</summary>
        public static VhrSdkVersionsInfo Parse(string json)
        {
            if (!VhrSetupCore.TryParseJson(json, out var root) || root.Kind != VhrSetupCore.JKind.Object) return null;
            var latest = root.GetString("latest");
            if (!VhrSetupCore.SemVer.IsValid(latest)) return null;

            var info = new VhrSdkVersionsInfo
            {
                Latest = latest.Trim().TrimStart('v', 'V'),
                MinSupported = root.GetString("minSupported")?.Trim().TrimStart('v', 'V'),
                ReleasedAt = root.GetString("releasedAt"),
                ReleaseNotes = root.GetString("releaseNotes")
            };
            var changelog = root.GetString("changelogUrl");
            if (IsHttps(changelog)) info.ChangelogUrl = changelog.Trim();
            var git = root.GetString("gitUrl");
            if (IsHttps(git)) info.GitUrl = git.Trim();
            var pkg = root.GetString("packageName");
            if (!string.IsNullOrWhiteSpace(pkg)) info.PackageName = pkg.Trim();

            foreach (var kv in root.Get("dependencies")?.ToStringMap() ?? new Dictionary<string, string>())
                if (VhrSetupCore.SemVer.IsValid(kv.Value)) info.Dependencies[kv.Key] = kv.Value.Trim();
            foreach (var kv in root.Get("features")?.ToStringMap() ?? new Dictionary<string, string>())
                info.Features[kv.Key] = kv.Value;

            var regs = root.Get("registries");
            if (regs != null && regs.Kind == VhrSetupCore.JKind.Array)
                foreach (var r in regs.Items)
                {
                    var url = r.GetString("url");
                    if (!IsHttps(url)) continue;
                    var reg = new Registry { Name = r.GetString("name") ?? url, Url = url.Trim() };
                    reg.Scopes.AddRange(r.Get("scopes")?.ToStringList() ?? new List<string>());
                    info.Registries.Add(reg);
                }

            var deps = root.Get("deprecations");
            if (deps != null && deps.Kind == VhrSetupCore.JKind.Array)
                foreach (var d in deps.Items)
                {
                    var api = d.GetString("api");
                    if (string.IsNullOrWhiteSpace(api)) continue;
                    info.Deprecations.Add(new Deprecation { Since = d.GetString("since"), Api = api.Trim(), Message = d.GetString("message") });
                }

            VhrSdkVersionService.AddDefaultRegistries(info);
            return info;
        }

        private static bool IsHttps(string s) =>
            !string.IsNullOrWhiteSpace(s) && s.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Сведения о версиях SDK: запрос к серверу, кэш в <see cref="EditorPrefs"/>, фолбэк.
    /// Никогда не бросает исключений и не пишет в консоль — офлайн просто «нет данных».
    /// </summary>
    internal static class VhrSdkVersionService
    {
        public const string EndpointPath = "/games/api/Sdk/versions";
        public const string PackageName = "ru.vhrgames.sdk";
        public const string DefaultGitUrl = "https://github.com/Merchelago/unity-sdk.git";
        public const string DefaultChangelogUrl = "https://github.com/Merchelago/unity-sdk/blob/main/CHANGELOG.md";

        /// <summary>Автопроверка — не чаще раза в сутки.</summary>
        public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

        // Сведения о версиях одинаковы для всех проектов — ключи общие для компьютера.
        private const string CacheJsonKey = "VhrSdk.Versions.Json";
        private const string CacheTimeKey = "VhrSdk.Versions.FetchedAtUtc";
        private const string LastAttemptKey = "VhrSdk.Versions.LastAttemptUtc";

        /// <summary>Адрес эндпоинта (учитывает адрес dev-стенда из окна «Тестирование в Editor»).</summary>
        public static string Url => VhrEditorSandbox.ApiServer + EndpointPath;

        /// <summary>Версия SDK, вкомпилированная в проект (её же пишет маркер сборки).</summary>
        public static string InstalledVersion => VhrSdk.SdkVersion;

        /// <summary>Сохранённый ответ сервера или null.</summary>
        public static VhrSdkVersionsInfo LoadCached()
        {
            var json = EditorPrefs.GetString(CacheJsonKey, string.Empty);
            if (string.IsNullOrEmpty(json)) return null;
            var info = VhrSdkVersionsInfo.Parse(json);
            if (info == null) return null;
            info.Source = VhrSdkVersionsSource.Cache;
            info.FetchedAtUtc = ReadUtc(CacheTimeKey) ?? DateTime.MinValue;
            return info;
        }

        /// <summary>Пора ли снова спрашивать сервер (прошло больше суток с прошлой попытки).</summary>
        public static bool IsCheckDue()
        {
            var last = ReadUtc(LastAttemptKey);
            return !last.HasValue || DateTime.UtcNow - last.Value >= CheckInterval || last.Value > DateTime.UtcNow.AddMinutes(5);
        }

        /// <summary>
        /// Асинхронно спрашивает сервер. <paramref name="done"/> вызывается на главном потоке
        /// ровно один раз: свежий ответ, иначе кэш, иначе фолбэк.
        /// </summary>
        public static void FetchAsync(Action<VhrSdkVersionsInfo> done)
        {
            WriteUtc(LastAttemptKey, DateTime.UtcNow);
            VhrEditorHttp.Send("GET", Url, null, null, null, r =>
            {
                VhrSdkVersionsInfo info = null;
                if (r.Ok) info = Store(r.Body);
                done?.Invoke(info ?? CachedOrFallback(r.Ok ? "некорректный ответ сервера" : r.Short()));
            }, 15);
        }

        /// <summary>
        /// Для проверки при сборке: свежий кэш (до часа) → запрос с таймаутом → кэш любой давности → фолбэк.
        /// Блокирует поток (сборка и так синхронная) не дольше <paramref name="timeoutMs"/>.
        /// </summary>
        public static VhrSdkVersionsInfo GetForBuild(int timeoutMs = 5000)
        {
            var cached = LoadCached();
            if (cached != null && DateTime.UtcNow - cached.FetchedAtUtc < TimeSpan.FromHours(1)) return cached;

            string error;
            try
            {
                WriteUtc(LastAttemptKey, DateTime.UtcNow);
                using (var req = UnityWebRequest.Get(Url))
                {
                    req.timeout = Math.Max(1, timeoutMs / 1000);
                    req.SetRequestHeader("Accept", "application/json");
                    req.SetRequestHeader("X-Vhr-Sdk-Version", VhrSdk.SdkVersion);
                    var op = req.SendWebRequest();
                    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs + 500);
                    while (!op.isDone && DateTime.UtcNow < deadline) Thread.Sleep(20);
                    if (!op.isDone)
                    {
                        req.Abort();
                        error = "таймаут";
                    }
                    else if (req.result == UnityWebRequest.Result.Success)
                    {
                        var info = Store(req.downloadHandler.text);
                        if (info != null) return info;
                        error = "некорректный ответ сервера";
                    }
                    else
                    {
                        error = req.responseCode > 0 ? "HTTP " + req.responseCode : req.error;
                    }
                }
            }
            catch (Exception e)
            {
                error = e.Message;
            }
            return CachedOrFallback(error);
        }

        /// <summary>Встроенные значения: «последняя» = установленная, минимума нет — ничего не блокируем.</summary>
        public static VhrSdkVersionsInfo Fallback(string error)
        {
            var info = new VhrSdkVersionsInfo
            {
                Latest = VhrSdk.SdkVersion,
                Source = VhrSdkVersionsSource.Fallback,
                Error = error
            };
            // Зависимости — из package.json установленного SDK, иначе — известные для этой версии.
            var installed = VhrSdkInstall.Detect();
            if (installed.Dependencies.Count > 0)
            {
                foreach (var kv in installed.Dependencies) info.Dependencies[kv.Key] = kv.Value;
            }
            else
            {
                info.Dependencies["com.cysharp.r3"] = "1.2.9";
                info.Dependencies["jp.hadashikick.vcontainer"] = "1.16.0";
                info.Dependencies["org.nuget.r3"] = "1.2.9";
            }
            AddDefaultRegistries(info);
            return info;
        }

        /// <summary>Реестры, без которых не разрешатся зависимости SDK (добавляются к ответу сервера).</summary>
        internal static void AddDefaultRegistries(VhrSdkVersionsInfo info)
        {
            void Ensure(string name, string url, params string[] scopes)
            {
                var reg = info.Registries.FirstOrDefault(r => VhrSetupCore.SameUrl(r.Url, url));
                if (reg == null)
                {
                    reg = new VhrSdkVersionsInfo.Registry { Name = name, Url = url };
                    info.Registries.Add(reg);
                }
                foreach (var s in scopes)
                    if (!reg.Scopes.Contains(s)) reg.Scopes.Add(s);
            }

            Ensure("OpenUPM", "https://package.openupm.com", "com.cysharp", "jp.hadashikick");
            Ensure("UnityNuGet", "https://unitynuget-registry.openupm.com", "org.nuget");
        }

        private static VhrSdkVersionsInfo Store(string json)
        {
            var info = VhrSdkVersionsInfo.Parse(json);
            if (info == null) return null;
            info.Source = VhrSdkVersionsSource.Server;
            info.FetchedAtUtc = DateTime.UtcNow;
            EditorPrefs.SetString(CacheJsonKey, json);
            WriteUtc(CacheTimeKey, info.FetchedAtUtc);
            return info;
        }

        private static VhrSdkVersionsInfo CachedOrFallback(string error)
        {
            var cached = LoadCached();
            if (cached == null) return Fallback(error);
            cached.Error = error;
            return cached;
        }

        private static DateTime? ReadUtc(string key)
        {
            var s = EditorPrefs.GetString(key, string.Empty);
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) &&
                   ticks > 0 && ticks < DateTime.MaxValue.Ticks
                ? new DateTime(ticks, DateTimeKind.Utc)
                : (DateTime?)null;
        }

        private static void WriteUtc(string key, DateTime utc) =>
            EditorPrefs.SetString(key, utc.Ticks.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Как установлен SDK в этом проекте.</summary>
    internal sealed class VhrSdkInstall
    {
        /// <summary>Вкомпилированная версия (<see cref="VhrSdk.SdkVersion"/>) — её проверяет платформа.</summary>
        public string Version = VhrSdk.SdkVersion;
        /// <summary>Версия из package.json (обычно совпадает с <see cref="Version"/>).</summary>
        public string PackageVersion;
        /// <summary>null — SDK не пакет (скопирован в Assets).</summary>
        public PackageSource? Source;
        public string PackageId;
        public string ResolvedPath;
        public readonly Dictionary<string, string> Dependencies = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Git-ссылка без тега, из которой установлен SDK (для git-источника).</summary>
        public string GitBase
        {
            get
            {
                if (Source != PackageSource.Git || string.IsNullOrEmpty(PackageId)) return null;
                var at = PackageId.IndexOf('@');
                return at < 0 ? null : VhrSetupCore.GitBase(PackageId.Substring(at + 1));
            }
        }

        /// <summary>Обновляется ли кнопкой (git или реестр).</summary>
        public bool CanAutoUpdate => Source == PackageSource.Git || Source == PackageSource.Registry;

        public string SourceLabel
        {
            get
            {
                switch (Source)
                {
                    case PackageSource.Git: return "git";
                    case PackageSource.Registry: return "реестр";
                    case PackageSource.Embedded: return "встроенный пакет (папка Packages/)";
                    case PackageSource.Local: return "локальная папка (file:)";
                    case PackageSource.LocalTarball: return "локальный архив .tgz";
                    case null: return "файлы в Assets (не пакет)";
                    default: return Source.Value.ToString();
                }
            }
        }

        public static VhrSdkInstall Detect()
        {
            var result = new VhrSdkInstall();
            try
            {
                var pi = PackageInfo.FindForAssembly(typeof(VhrSdk).Assembly);
                if (pi == null) return result;
                result.Source = pi.source;
                result.PackageVersion = pi.version;
                result.PackageId = pi.packageId;
                result.ResolvedPath = pi.resolvedPath;
                if (pi.dependencies != null)
                    foreach (var d in pi.dependencies)
                        if (!d.name.StartsWith("com.unity.", StringComparison.Ordinal) && VhrSetupCore.SemVer.IsValid(d.version))
                            result.Dependencies[d.name] = d.version;
            }
            catch
            {
                // Package Manager недоступен (например, во время импорта) — считаем «не пакет»
            }
            return result;
        }
    }

    /// <summary>Файлы проекта: manifest.json (с копией в Library), копии ядра R3 в Assets.</summary>
    internal static class VhrSdkProject
    {
        public static string Root => Path.GetDirectoryName(Application.dataPath);

        public static string ManifestPath => Path.Combine(Root, "Packages", "manifest.json");

        public static string ReadManifest(out bool bom)
        {
            var bytes = File.ReadAllBytes(ManifestPath);
            bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var skip = bom ? 3 : 0;
            return new UTF8Encoding(false).GetString(bytes, skip, bytes.Length - skip);
        }

        /// <summary>Пишет manifest.json, сохранив копию прежнего в Library/VhrSdk/. Возвращает путь копии.</summary>
        public static string WriteManifest(string text, bool bom)
        {
            string backup = null;
            try
            {
                var dir = Path.Combine(Root, "Library", "VhrSdk");
                Directory.CreateDirectory(dir);
                var name = "manifest-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json";
                File.Copy(ManifestPath, Path.Combine(dir, name), true);
                backup = "Library/VhrSdk/" + name;
            }
            catch
            {
                // без копии — не повод не обновляться
            }
            File.WriteAllText(ManifestPath, text, new UTF8Encoding(bom));
            return backup;
        }

        /// <summary>Добавляет в manifest.json недостающие реестры. Возвращает список изменений (пустой — уже всё есть).</summary>
        public static List<string> EnsureRegistries(VhrSdkVersionsInfo info)
        {
            var text = ReadManifest(out var bom);
            var m = new VhrSetupCore.Manifest(text);
            foreach (var r in info.Registries) m.EnsureRegistry(r.Name, r.Url, r.Scopes);
            if (m.Changes.Count == 0) return m.Changes;
            var backup = WriteManifest(m.Text, bom);
            Debug.Log("[VHR SDK] Packages/manifest.json: добавлены реестры\n" + string.Join("\n", m.Changes) +
                      (backup != null ? "\nКопия прежнего файла: " + backup : string.Empty));
            return m.Changes;
        }

        /// <summary>Реестры, которых не хватает в manifest.json (для показа в окне).</summary>
        public static List<string> MissingRegistries(VhrSdkVersionsInfo info)
        {
            var missing = new List<string>();
            try
            {
                var m = new VhrSetupCore.Manifest(ReadManifest(out _));
                foreach (var r in info.Registries)
                    if (r.Scopes.Any(s => !m.IsScopeCovered(s))) missing.Add($"{r.Name} ({r.Url})");
            }
            catch
            {
                // нет файла / битый JSON — окно покажет ошибку при обновлении
            }
            return missing;
        }

        /// <summary>Удаляет копии ядра R3 из Assets и записи о них в packages.config NuGetForUnity.</summary>
        public static void DeleteR3Copies(List<VhrSetupCore.R3CoreCopy> copies)
        {
            var ids = new List<string>();
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var c in copies)
                {
                    var paths = new List<string> { c.AssetPath };
                    if (!c.IsFolder)
                        foreach (var ext in new[] { ".xml", ".pdb" })
                        {
                            var sibling = Path.ChangeExtension(c.AssetPath, ext);
                            if (File.Exists(Path.Combine(Root, sibling))) paths.Add(sibling);
                        }
                    foreach (var p in paths)
                        if (!AssetDatabase.DeleteAsset(p)) DeleteFromDisk(p);
                    if (!string.IsNullOrEmpty(c.NuGetId)) ids.Add(c.NuGetId);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            foreach (var c in copies)
            {
                var dir = Path.GetDirectoryName(c.AssetPath)?.Replace('\\', '/');
                while (!string.IsNullOrEmpty(dir) && dir.StartsWith("Assets/", StringComparison.Ordinal) &&
                       dir != "Assets/Plugins" && dir != "Assets/Packages")
                {
                    var abs = Path.Combine(Root, dir);
                    if (!Directory.Exists(abs) || Directory.EnumerateFileSystemEntries(abs).Any()) break;
                    if (!AssetDatabase.DeleteAsset(dir)) DeleteFromDisk(dir);
                    dir = Path.GetDirectoryName(dir)?.Replace('\\', '/');
                }
            }
            VhrSetupCore.RemoveFromNuGetPackagesConfig(Root, ids);
            Debug.Log("[VHR SDK] Удалены копии ядра R3 (его даёт пакет org.nuget.r3):\n" +
                      string.Join("\n", copies.Select(c => c.AssetPath)));
            AssetDatabase.Refresh();
        }

        private static void DeleteFromDisk(string assetPath)
        {
            try
            {
                var abs = Path.Combine(Root, assetPath);
                if (Directory.Exists(abs)) Directory.Delete(abs, true);
                else if (File.Exists(abs)) File.Delete(abs);
                if (File.Exists(abs + ".meta")) File.Delete(abs + ".meta");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VHR SDK] Не удалось удалить {assetPath}: {e.Message}");
            }
        }
    }
}
