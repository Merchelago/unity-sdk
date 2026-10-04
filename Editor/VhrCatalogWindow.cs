using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace VhrGames.Sdk.Editor
{
    /// <summary>
    /// Окно <c>VHR → Каталог игры</c>: товары и достижения игры с настоящего сервера
    /// (по песочному ключу из окна «Тестирование в Editor»), копирование ID и
    /// генерация C#-констант <c>VhrCatalogIds.cs</c>.
    /// </summary>
    /// <remarks>
    /// Окно только читает. Создание и редактирование товаров и достижений — в кабинете
    /// разработчика на сайте: там нужен вход разработчика, а у песочного ключа таких прав нет.
    /// </remarks>
    public sealed class VhrCatalogWindow : EditorWindow
    {
        private const string MenuPath = "VHR/Каталог игры";
        private const string CabinetUrl = "https://vhrgames.ru/dev/games";

        private int _tab; // 0 — товары, 1 — достижения
        private Vector2 _scroll;

        private VhrCatalogItem[] _items;
        private VhrAchievement[] _achievements;
        private string _itemsError;
        private string _achievementsError;
        private int _loading;
        private string _loadedForGame;
        private Action _afterLoad;

        private readonly Dictionary<string, Texture2D> _icons = new Dictionary<string, Texture2D>();
        private readonly HashSet<string> _iconsRequested = new HashSet<string>();

        private static string ConstPathPref => VhrEditorSandbox.Prefix + "CatalogConstPath";

        [MenuItem(MenuPath, priority = 11)]
        public static void Open()
        {
            var w = GetWindow<VhrCatalogWindow>(false, "VHR — каталог игры", true);
            w.minSize = new Vector2(560, 380);
            w.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Каталог игры: товары и достижения", EditorStyles.boldLabel);

            if (!VhrEditorSandbox.InspectKey(VhrEditorSandbox.Key, out var info, out var problem))
            {
                EditorGUILayout.HelpBox(
                    "Каталог читается с сервера по песочному ключу, а ключ " + problem +
                    "\nСохраните ключ в окне VHR → Тестирование в Editor.", MessageType.Warning);
                if (GUILayout.Button("Открыть VHR → Тестирование в Editor"))
                    VhrSandboxWindow.Open();
                return;
            }

            var gid = info.GameId;
            if (_loadedForGame != gid && _loading == 0)
            {
                _loadedForGame = gid;
                _items = null;
                _achievements = null;
                Load(gid);
            }

            EditorGUILayout.LabelField($"Игра (gid): {gid}   ·   сервер: {VhrEditorSandbox.ApiServer}", EditorStyles.miniLabel);
            EditorGUILayout.HelpBox(
                "Окно только читает каталог. Создание и редактирование товаров и достижений — в кабинете разработчика " +
                "на сайте (нужен вход разработчика; песочный ключ на это прав не имеет).", MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_loading > 0))
                {
                    if (GUILayout.Button(_loading > 0 ? "Загружаю…" : "Обновить", GUILayout.Width(110)))
                        Load(gid);
                }
                if (GUILayout.Button("Открыть в кабинете ↗", GUILayout.Width(170)))
                    Application.OpenURL(_tab == 1 ? $"{CabinetUrl}/{Uri.EscapeDataString(gid)}/achievements" : CabinetUrl);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(_loading > 0))
                {
                    if (GUILayout.Button("Сгенерировать C#-константы", GUILayout.Width(210)))
                        GenerateConstants(gid, allowReload: true);
                }
            }

            EditorGUILayout.Space(4);
            _tab = GUILayout.Toolbar(_tab, new[]
            {
                "Товары" + (_items != null ? $" ({_items.Length})" : string.Empty),
                "Достижения" + (_achievements != null ? $" ({_achievements.Length})" : string.Empty)
            });

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (_tab == 0) DrawItems();
            else DrawAchievements();
            EditorGUILayout.EndScrollView();
        }

        // ----------------------------------------------------------- загрузка

        private void Load(string gid)
        {
            var key = VhrEditorSandbox.Key;
            var api = VhrEditorSandbox.ApiServer;
            var g = Uri.EscapeDataString(gid);
            _loading = 2;
            _itemsError = null;
            _achievementsError = null;

            VhrEditorHttp.Send("GET", $"{api}/bridge/api/items?gameId={g}", key, gid, null, r =>
            {
                if (r.Ok) _items = ParseArray<ItemList, VhrCatalogItem>(r.Body, w => w.items, out _itemsError);
                else _itemsError = VhrSandboxWindow.DescribeError(r);
                Done();
            });

            VhrEditorHttp.Send("GET", $"{api}/games/api/Achievements/games/{g}", key, gid, null, r =>
            {
                if (r.Ok) _achievements = ParseArray<AchievementList, VhrAchievement>(r.Body, w => w.items, out _achievementsError);
                else _achievementsError = VhrSandboxWindow.DescribeError(r);
                Done();
            });

            void Done()
            {
                _loading--;
                if (_loading == 0)
                {
                    var next = _afterLoad;
                    _afterLoad = null;
                    next?.Invoke();
                }
                Repaint();
            }
        }

        private static T[] ParseArray<TWrapper, T>(string body, Func<TWrapper, T[]> pick, out string error)
            where TWrapper : class
        {
            error = null;
            if (string.IsNullOrWhiteSpace(body)) return Array.Empty<T>();
            var t = body.TrimStart();
            var json = t.Length > 0 && t[0] == '[' ? "{\"items\":" + body + "}" : body;
            try
            {
                var w = JsonUtility.FromJson<TWrapper>(json);
                return (w == null ? null : pick(w)) ?? Array.Empty<T>();
            }
            catch (Exception e)
            {
                error = "Не удалось разобрать ответ сервера: " + e.Message;
                return Array.Empty<T>();
            }
        }

        // ------------------------------------------------------------ таблицы

        private void DrawItems()
        {
            if (!string.IsNullOrEmpty(_itemsError)) EditorGUILayout.HelpBox("Товары: " + _itemsError, MessageType.Error);
            if (_items == null) { if (_loading > 0) EditorGUILayout.LabelField("Загрузка…"); return; }
            if (_items.Length == 0)
            {
                EditorGUILayout.HelpBox("У игры пока нет активных товаров. Заведите их в кабинете разработчика: игра → Товары.",
                    MessageType.None);
                return;
            }

            Header("Название / ключ", "ID товара (для PurchaseAsync)", "Цена", "Статус");
            foreach (var it in _items)
            {
                if (it == null) continue;
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    DrawIcon(it.iconUrl, null);
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(170)))
                    {
                        EditorGUILayout.LabelField(it.title ?? "—", EditorStyles.boldLabel);
                        if (!string.IsNullOrEmpty(it.code)) EditorGUILayout.LabelField(it.code, EditorStyles.miniLabel);
                        if (!string.IsNullOrEmpty(it.description))
                            EditorGUILayout.LabelField(it.description, EditorStyles.wordWrappedMiniLabel);
                    }
                    EditorGUILayout.SelectableLabel(it.id ?? string.Empty, EditorStyles.miniLabel,
                        GUILayout.Height(EditorGUIUtility.singleLineHeight), GUILayout.MinWidth(120));
                    EditorGUILayout.LabelField($"{it.priceCoins} мон.", GUILayout.Width(70));
                    EditorGUILayout.LabelField(it.active ? "активен" : "скрыт", GUILayout.Width(60));
                    if (GUILayout.Button("Копировать ID", GUILayout.Width(110)))
                        Copy(it.id, it.title);
                }
            }
        }

        private void DrawAchievements()
        {
            if (!string.IsNullOrEmpty(_achievementsError))
                EditorGUILayout.HelpBox("Достижения: " + _achievementsError, MessageType.Error);
            if (_achievements == null) { if (_loading > 0) EditorGUILayout.LabelField("Загрузка…"); return; }
            if (_achievements.Length == 0)
            {
                EditorGUILayout.HelpBox("У игры пока нет достижений. Заведите их в кабинете разработчика: игра → Достижения.",
                    MessageType.None);
                return;
            }

            Header("Название / описание", "Код достижения (ID — под ним)", "Очки", "Редкость");
            foreach (var a in _achievements)
            {
                if (a == null) continue;
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    DrawIcon(a.iconUrl, a.iconUrl);
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(170)))
                    {
                        EditorGUILayout.LabelField(a.title ?? "—", EditorStyles.boldLabel);
                        if (!string.IsNullOrEmpty(a.description))
                            EditorGUILayout.LabelField(a.description, EditorStyles.wordWrappedMiniLabel);
                    }
                    using (new EditorGUILayout.VerticalScope(GUILayout.MinWidth(120)))
                    {
                        EditorGUILayout.SelectableLabel(a.code ?? string.Empty, EditorStyles.miniLabel,
                            GUILayout.Height(EditorGUIUtility.singleLineHeight));
                        EditorGUILayout.SelectableLabel(a.id ?? string.Empty, EditorStyles.miniLabel,
                            GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    }
                    EditorGUILayout.LabelField(a.points.ToString(CultureInfo.InvariantCulture), GUILayout.Width(70));
                    EditorGUILayout.LabelField(string.IsNullOrEmpty(a.rarity) ? "—" : a.rarity, GUILayout.Width(60));
                    if (GUILayout.Button(new GUIContent("Копировать ID", "Копирует код достижения (game.{gameId}.{slug})"),
                            GUILayout.Width(110)))
                        Copy(string.IsNullOrEmpty(a.code) ? a.id : a.code, a.title);
                }
            }
        }

        private static void Header(string c1, string c2, string c3, string c4)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(40);
                EditorGUILayout.LabelField(c1, EditorStyles.miniBoldLabel, GUILayout.Width(170));
                EditorGUILayout.LabelField(c2, EditorStyles.miniBoldLabel, GUILayout.MinWidth(120));
                EditorGUILayout.LabelField(c3, EditorStyles.miniBoldLabel, GUILayout.Width(70));
                EditorGUILayout.LabelField(c4, EditorStyles.miniBoldLabel, GUILayout.Width(60));
                GUILayout.Space(114);
            }
        }

        // Иконка: URL — загружаем превью; иначе (у ачивок бывает emoji) — показываем текст.
        private void DrawIcon(string url, string fallbackText)
        {
            var rect = GUILayoutUtility.GetRect(32, 32, GUILayout.Width(32), GUILayout.Height(32));
            if (!string.IsNullOrEmpty(url) &&
                (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                if (_icons.TryGetValue(url, out var tex) && tex != null)
                {
                    GUI.DrawTexture(rect, tex, ScaleMode.ScaleToFit);
                    return;
                }
                if (_iconsRequested.Add(url))
                {
                    VhrEditorHttp.GetTexture(url, t =>
                    {
                        if (t != null) t.hideFlags = HideFlags.HideAndDontSave;
                        _icons[url] = t;
                        Repaint();
                    });
                }
                GUI.Label(rect, "…", EditorStyles.centeredGreyMiniLabel);
                return;
            }
            GUI.Label(rect, string.IsNullOrEmpty(fallbackText) ? "—" : fallbackText, EditorStyles.centeredGreyMiniLabel);
        }

        private void OnDestroy()
        {
            foreach (var t in _icons.Values)
                if (t != null) DestroyImmediate(t);
            _icons.Clear();
        }

        private void Copy(string id, string title)
        {
            if (string.IsNullOrEmpty(id)) return;
            EditorGUIUtility.systemCopyBuffer = id;
            ShowNotification(new GUIContent("Скопировано: " + (string.IsNullOrEmpty(title) ? id : title)));
        }

        // -------------------------------------------------- генерация констант

        private void GenerateConstants(string gid, bool allowReload)
        {
            bool failed = _items == null || _achievements == null ||
                          !string.IsNullOrEmpty(_itemsError) || !string.IsNullOrEmpty(_achievementsError);
            if (failed && allowReload)
            {
                // Нужны оба списка: сначала (пере)загрузим, затем одна повторная попытка.
                _afterLoad = () => GenerateConstants(gid, allowReload: false);
                if (_loading == 0) Load(gid);
                return;
            }
            if (failed)
            {
                EditorUtility.DisplayDialog("VHR", "Каталог не загрузился — константы не сгенерированы.\n" +
                                                   (_itemsError ?? _achievementsError ?? string.Empty), "OK");
                return;
            }

            var last = EditorPrefs.GetString(ConstPathPref, "Assets/VhrCatalogIds.cs");
            var dir = Path.GetDirectoryName(last)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dir) || !AssetDatabase.IsValidFolder(dir)) dir = "Assets";
            var path = EditorUtility.SaveFilePanelInProject(
                "Сохранить C#-константы каталога", "VhrCatalogIds", "cs",
                "Куда сохранить VhrCatalogIds.cs (повторная генерация перезапишет файл)", dir);
            if (string.IsNullOrEmpty(path)) return;

            var code = VhrCatalogCodeGen.Generate(gid, _items, _achievements);
            File.WriteAllText(path, code, new UTF8Encoding(false));
            EditorPrefs.SetString(ConstPathPref, path);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            Debug.Log($"[VHR SDK] Сгенерирован {path}: товаров {_items.Length}, достижений {_achievements.Length}.");
            ShowNotification(new GUIContent("VhrCatalogIds.cs сгенерирован"));
        }

#pragma warning disable CS0649 // поля заполняет JsonUtility
        [Serializable] private sealed class ItemList { public VhrCatalogItem[] items; }
        [Serializable] private sealed class AchievementList { public VhrAchievement[] items; }
#pragma warning restore CS0649
    }

    /// <summary>
    /// Генератор <c>VhrCatalogIds.cs</c>: безопасные C#-идентификаторы из ключей/названий
    /// (кириллица транслитерируется), без коллизий, с комментарием (название, цена/очки).
    /// </summary>
    internal static class VhrCatalogCodeGen
    {
        public static string Generate(string gameId, VhrCatalogItem[] items, VhrAchievement[] achievements)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine($"// Сгенерировано VHR SDK {VhrSdk.SdkVersion} (окно VHR → Каталог игры) " +
                          $"{DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC.");
            sb.AppendLine("// Повторная генерация перезаписывает файл — не правьте его вручную.");
            sb.AppendLine("// Товары и достижения заводятся в кабинете разработчика: https://vhrgames.ru/dev/games");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine();
            sb.AppendLine("/// <summary>ID каталога игры VHR: товары и достижения.</summary>");
            sb.AppendLine("public static class VhrCatalogIds");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>Id игры (gid).</summary>");
            sb.AppendLine($"    public const string GameId = \"{Escape(gameId)}\";");
            sb.AppendLine();

            sb.AppendLine("    /// <summary>Товары: значение — id товара для <c>VhrSdk.Economy.PurchaseAsync(null, VhrCatalogIds.Items.X)</c>.</summary>");
            sb.AppendLine("    public static class Items");
            sb.AppendLine("    {");
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var it in items ?? Array.Empty<VhrCatalogItem>())
            {
                if (it == null || string.IsNullOrEmpty(it.id)) continue;
                var name = UniqueName(MakeIdentifier(!string.IsNullOrWhiteSpace(it.code) ? it.code : it.title, "Item"), "Items", used);
                var title = string.IsNullOrWhiteSpace(it.title) ? it.code : it.title;
                var keyNote = !string.IsNullOrWhiteSpace(it.code) ? $" (ключ: {it.code})" : string.Empty;
                sb.AppendLine($"        /// <summary>{Xml(title)} — {it.priceCoins} монет{Xml(keyNote)}.</summary>");
                sb.AppendLine($"        public const string {name} = \"{Escape(it.id)}\";");
            }
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine("    /// <summary>Достижения: значение — код достижения (<c>game.{gameId}.{slug}</c>).</summary>");
            sb.AppendLine("    public static class Achievements");
            sb.AppendLine("    {");
            used.Clear();
            foreach (var a in achievements ?? Array.Empty<VhrAchievement>())
            {
                if (a == null) continue;
                var value = string.IsNullOrEmpty(a.code) ? a.id : a.code;
                if (string.IsNullOrEmpty(value)) continue;
                var source = Slug(a.code);
                if (string.IsNullOrWhiteSpace(source)) source = a.title;
                var name = UniqueName(MakeIdentifier(source, "Achievement"), "Achievements", used);
                var pts = a.points > 0 ? $" — {a.points} очк." : string.Empty;
                sb.AppendLine($"        /// <summary>{Xml(string.IsNullOrWhiteSpace(a.title) ? value : a.title)}{pts}</summary>");
                sb.AppendLine($"        public const string {name} = \"{Escape(value)}\";");
            }
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // game.{gameId}.{slug} → slug
        private static string Slug(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            int dot = code.LastIndexOf('.');
            return dot >= 0 && dot + 1 < code.Length ? code.Substring(dot + 1) : code;
        }

        internal static string MakeIdentifier(string source, string fallback)
        {
            var latin = Transliterate(source ?? string.Empty);
            var sb = new StringBuilder();
            bool upperNext = true;
            foreach (var c in latin)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                if (!ok)
                {
                    upperNext = true;
                    continue;
                }
                sb.Append(upperNext ? char.ToUpperInvariant(c) : c);
                upperNext = false;
            }
            var id = sb.ToString();
            if (id.Length == 0) id = fallback;
            if (char.IsDigit(id[0])) id = "_" + id;
            return id;
        }

        private static string UniqueName(string name, string enclosingType, HashSet<string> used)
        {
            // Член не может называться как вложенный класс (CS0542).
            if (name == enclosingType || name == "VhrCatalogIds") name += "_";
            var candidate = name;
            for (int n = 2; !used.Add(candidate); n++) candidate = name + "_" + n;
            return candidate;
        }

        private static readonly Dictionary<char, string> Ru = new Dictionary<char, string>
        {
            ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e",
            ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m",
            ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u",
            ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch", ['ъ'] = "",
            ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya"
        };

        private static string Transliterate(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                var lower = char.ToLowerInvariant(c);
                if (Ru.TryGetValue(lower, out var lat))
                {
                    if (lat.Length > 0 && c != lower) lat = char.ToUpperInvariant(lat[0]) + lat.Substring(1);
                    sb.Append(lat);
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static string Escape(string s) => (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static string Xml(string s) =>
            (s ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace('\n', ' ').Replace('\r', ' ');
    }
}
