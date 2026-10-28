// Конфиги MCP для ИИ-ассистентов (Claude Code, Cursor, VS Code): что записать и как аккуратно
// слить это с уже существующими файлами. Только System.* — без UnityEditor и UnityEngine, чтобы
// логику мержа можно было проверить обычным .NET-тестом. Окно — VhrMcpWindow.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VhrGames.Sdk.Editor
{
    /// <summary>ИИ-клиент, для которого пишется файл конфигурации MCP.</summary>
    internal enum VhrMcpClient
    {
        /// <summary>Claude Code: <c>.mcp.json</c> в корне проекта, <c>mcpServers</c>, <c>${VAR}</c>.</summary>
        ClaudeCode = 0,

        /// <summary>Cursor: <c>.cursor/mcp.json</c>, <c>mcpServers</c> без <c>type</c>, <c>${env:VAR}</c>.</summary>
        Cursor = 1,

        /// <summary>VS Code (Copilot): <c>.vscode/mcp.json</c>, <c>servers</c>, <c>${env:VAR}</c>.</summary>
        VsCode = 2
    }

    /// <summary>
    /// Конфигурация MCP платформы VHR для ИИ-клиентов и её мерж с файлами проекта.
    /// </summary>
    /// <remarks>
    /// В файлы пишутся ровно два сервера: <c>vhr</c> (полный, заголовок <c>Authorization: Bearer</c>
    /// со ссылкой на переменную окружения <c>VHR_MCP_TOKEN</c>) и <c>vhr-docs</c> (публичная
    /// документация без заголовка). Секреты не пишутся никогда: значения — только константы
    /// этого класса. Остальное содержимое файла (чужие серверы, <c>inputs</c>, комментарии,
    /// форматирование) сохраняется: правки вносятся точечно в текст, а не перезаписью файла.
    /// Некорректный JSON не трогается — сначала копия <c>*.vhr-backup-…</c>, затем файл создаётся заново.
    /// </remarks>
    internal static class VhrMcpConfig
    {
        public const string ServerName = "vhr";
        public const string DocsServerName = "vhr-docs";
        public const string ServerUrl = "https://api.vhrweb.ru/mcp";
        public const string DocsServerUrl = "https://api.vhrweb.ru/mcp/docs";
        public const string TokenEnvVar = "VHR_MCP_TOKEN";
        public const string TokenPageUrl = "https://vhrgames.ru/dev/mcp";
        public const string BackupMarker = ".vhr-backup-";

        private const char Bom = (char)0xFEFF; // метка порядка байтов, если текст прочитан вместе с ней

        public static readonly VhrMcpClient[] AllClients = { VhrMcpClient.ClaudeCode, VhrMcpClient.Cursor, VhrMcpClient.VsCode };

        // Личный токен платформы: vhr_pat_ + 40 символов base62 — чтобы найти его в файле в открытом виде.
        private static readonly Regex LiteralToken = new Regex(@"vhr_pat_[0-9A-Za-z]{8,}", RegexOptions.CultureInvariant);
        private static readonly Regex TokenFormat = new Regex(@"^vhr_pat_[0-9A-Za-z]{40}$", RegexOptions.CultureInvariant);

        // ============================================================ клиенты

        /// <summary>Путь файла относительно корня Unity-проекта (через <c>/</c>).</summary>
        public static string RelativePath(VhrMcpClient c) => c switch
        {
            VhrMcpClient.Cursor => ".cursor/mcp.json",
            VhrMcpClient.VsCode => ".vscode/mcp.json",
            _ => ".mcp.json"
        };

        public static string DisplayName(VhrMcpClient c) => c switch
        {
            VhrMcpClient.Cursor => "Cursor",
            VhrMcpClient.VsCode => "VS Code (Copilot)",
            _ => "Claude Code"
        };

        /// <summary>Ключ объекта серверов: у VS Code — <c>servers</c>, у остальных — <c>mcpServers</c>.</summary>
        public static string RootKey(VhrMcpClient c) => c == VhrMcpClient.VsCode ? "servers" : "mcpServers";

        /// <summary>Ссылка на переменную окружения в синтаксисе клиента (значение не подставляется).</summary>
        public static string TokenReference(VhrMcpClient c) =>
            c == VhrMcpClient.ClaudeCode ? "${" + TokenEnvVar + "}" : "${env:" + TokenEnvVar + "}";

        public static string AuthorizationHeader(VhrMcpClient c) => "Bearer " + TokenReference(c);

        // Cursor определяет удалённый сервер по url, поле type в его документации — только для stdio.
        private static bool WritesType(VhrMcpClient c) => c != VhrMcpClient.Cursor;

        /// <summary>Серверы VHR для клиента в порядке записи: <c>vhr</c>, затем <c>vhr-docs</c>.</summary>
        public static List<KeyValuePair<string, JObj>> DesiredServers(VhrMcpClient c)
        {
            var full = new JObj();
            if (WritesType(c)) full.Add("type", "http");
            full.Add("url", ServerUrl);
            full.Add("headers", new JObj().Add("Authorization", AuthorizationHeader(c)));

            var docs = new JObj();
            if (WritesType(c)) docs.Add("type", "http");
            docs.Add("url", DocsServerUrl);

            return new List<KeyValuePair<string, JObj>>
            {
                new KeyValuePair<string, JObj>(ServerName, full),
                new KeyValuePair<string, JObj>(DocsServerName, docs)
            };
        }

        /// <summary>Содержимое нового файла для клиента (так он выглядит, если файла ещё нет).</summary>
        public static string FreshDocument(VhrMcpClient c, string nl = "\n")
        {
            var servers = new JObj();
            foreach (var s in DesiredServers(c)) servers.Add(s.Key, s.Value);
            return new JObj().Add(RootKey(c), servers).Render("", "  ", nl) + nl;
        }

        // ============================================================ мерж текста

        internal enum ServerChange { Added, Updated, Unchanged }

        internal sealed class MergeOutcome
        {
            /// <summary>Итоговый текст файла (при <see cref="Changed"/> = false совпадает с исходным).</summary>
            public string Text;
            public bool Changed;

            /// <summary>Файла не было или он пустой — создан с нуля.</summary>
            public bool CreatedNew;

            /// <summary>Исходный текст — некорректный JSON (или не объект): нужен бэкап, файл создан заново.</summary>
            public bool WasInvalid;
            public string InvalidReason;

            public readonly List<KeyValuePair<string, ServerChange>> Servers = new List<KeyValuePair<string, ServerChange>>();
        }

        /// <summary>
        /// Сливает серверы VHR с текстом существующего файла клиента. <paramref name="existing"/> = null —
        /// файла нет. Повторный вызов на результате ничего не меняет.
        /// </summary>
        public static MergeOutcome MergeText(string existing, VhrMcpClient client)
        {
            var o = new MergeOutcome();
            var desired = DesiredServers(client);
            var rootKey = RootKey(client);

            if (existing == null || existing.Trim(Bom, ' ', '\t', '\r', '\n').Length == 0)
                return Fresh(o, client, desired, "\n", invalid: false, reason: null);

            var nl = existing.Contains("\r\n") ? "\r\n" : "\n";
            Node root;
            try { root = Parse(existing); }
            catch (FormatException e) { return Fresh(o, client, desired, nl, invalid: true, reason: e.Message); }
            if (root.Kind != JKind.Object)
                return Fresh(o, client, desired, nl, invalid: true, reason: "в корне файла не объект JSON");

            var unit = DetectIndentUnit(existing);
            var text = existing;
            var servers = root.Get(rootKey);

            if (servers == null)
            {
                // Нет объекта серверов — добавляем его целиком последним полем корня.
                var block = new JObj();
                foreach (var s in desired)
                {
                    block.Add(s.Key, s.Value);
                    o.Servers.Add(new KeyValuePair<string, ServerChange>(s.Key, ServerChange.Added));
                }
                text = AppendMembers(text, root, new JObj().Add(rootKey, block), unit, nl);
            }
            else if (servers.Kind != JKind.Object)
            {
                return Fresh(o, client, desired, nl, invalid: true, reason: $"поле \"{rootKey}\" — не объект");
            }
            else
            {
                // 1) Заменяем свои записи, которые отличаются (с конца текста — позиции не съезжают).
                var replacements = new List<KeyValuePair<Node, JObj>>();
                foreach (var s in desired)
                {
                    var found = false;
                    var differs = false;
                    foreach (var m in servers.Members)
                    {
                        if (m.Key != s.Key) continue;
                        found = true;
                        if (s.Value.SameAs(m.Value)) continue;
                        differs = true;
                        replacements.Add(new KeyValuePair<Node, JObj>(m.Value, s.Value));
                    }
                    o.Servers.Add(new KeyValuePair<string, ServerChange>(s.Key,
                        !found ? ServerChange.Added : differs ? ServerChange.Updated : ServerChange.Unchanged));
                }
                replacements.Sort((a, b) => b.Key.Start.CompareTo(a.Key.Start));
                foreach (var r in replacements)
                {
                    var indent = LineIndent(text, r.Key.Start);
                    text = text.Substring(0, r.Key.Start) + r.Value.Render(indent, unit, nl) + text.Substring(r.Key.End);
                }

                // 2) Добавляем недостающие в конец объекта серверов (после перечитывания текста).
                var missing = new JObj();
                foreach (var s in o.Servers)
                    if (s.Value == ServerChange.Added)
                        missing.Add(s.Key, desired.Find(d => d.Key == s.Key).Value);
                if (missing.Count > 0)
                    text = AppendMembers(text, Parse(text).Get(rootKey), missing, unit, nl);
            }

            o.Text = text;
            o.Changed = !string.Equals(text, existing, StringComparison.Ordinal);
            return o;
        }

        private static MergeOutcome Fresh(MergeOutcome o, VhrMcpClient client, List<KeyValuePair<string, JObj>> desired,
            string nl, bool invalid, string reason)
        {
            o.Text = FreshDocument(client, nl);
            o.Changed = true;
            o.CreatedNew = !invalid;
            o.WasInvalid = invalid;
            o.InvalidReason = reason;
            foreach (var s in desired)
                o.Servers.Add(new KeyValuePair<string, ServerChange>(s.Key, ServerChange.Added));
            return o;
        }

        // Дописывает поля members последними в объект obj — одной вставкой в текст.
        private static string AppendMembers(string text, Node obj, JObj members, string unit, string nl)
        {
            var objIndent = LineIndent(text, obj.Start);
            string indent;
            if (obj.Members.Count > 0)
            {
                // Отступ — как у первого поля объекта, если оно начинает строку; иначе — объект + шаг.
                var first = obj.Members[0];
                indent = StartsLine(text, first.KeyStart) ? LineIndent(text, first.KeyStart) : objIndent + unit;
            }
            else
            {
                indent = objIndent + unit;
            }

            var sb = new StringBuilder();
            for (var i = 0; i < members.Fields.Count; i++)
            {
                var f = members.Fields[i];
                if (i > 0) sb.Append(',').Append(nl).Append(indent);
                sb.Append(Quote(f.Key)).Append(": ").Append(((JObj)f.Value).Render(indent, unit, nl));
            }
            var block = sb.ToString();

            if (obj.Members.Count > 0)
            {
                // После последнего поля. Его висячая запятая и комментарий до конца строки остаются при нём:
                // "a": 1, // c   →   "a": 1, // c ⏎ "vhr": …        "a": 1 // c   →   "a": 1, // c ⏎ "vhr": …
                var last = obj.Members[obj.Members.Count - 1].Value;
                var pos = SkipSpaces(text, last.End);
                var hadComma = pos < text.Length && text[pos] == ',';
                if (hadComma) pos = SkipSpaces(text, pos + 1);
                if (string.CompareOrdinal(text, pos, "//", 0, 2) == 0)
                {
                    var eol = text.IndexOf('\n', pos);
                    if (eol < 0) eol = text.Length;
                    if (eol > pos && text[eol - 1] == '\r') eol--;
                    return text.Substring(0, last.End) + (hadComma ? "" : ",") + text.Substring(last.End, eol - last.End) +
                           nl + indent + block + text.Substring(eol);
                }
                return text.Substring(0, last.End) + "," + nl + indent + block + text.Substring(last.End);
            }

            var inner = text.Substring(obj.Start + 1, obj.End - obj.Start - 2);
            if (inner.Trim().Length == 0) // {} или только пробелы — заменяем внутренность целиком
                return text.Substring(0, obj.Start + 1) + nl + indent + block + nl + objIndent + text.Substring(obj.End - 1);
            // Внутри только комментарии — вставляем сразу после '{', комментарии остаются ниже.
            return text.Substring(0, obj.Start + 1) + nl + indent + block + text.Substring(obj.Start + 1);
        }

        private static string LineIndent(string text, int pos)
        {
            var lineStart = text.LastIndexOf('\n', Math.Max(0, Math.Min(pos, text.Length) - 1)) + 1;
            if (pos == 0) lineStart = 0;
            var i = lineStart;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
            return text.Substring(lineStart, i - lineStart);
        }

        private static int SkipSpaces(string text, int pos)
        {
            while (pos < text.Length && (text[pos] == ' ' || text[pos] == '\t')) pos++;
            return pos;
        }

        private static bool StartsLine(string text, int pos)
        {
            for (var i = pos - 1; i >= 0; i--)
            {
                var c = text[i];
                if (c == '\n') return true;
                if (c != ' ' && c != '\t' && c != '\r') return false;
            }
            return true;
        }

        // Шаг отступа файла: первая строка с отступом перед полем ("…). По умолчанию — 2 пробела.
        private static string DetectIndentUnit(string text)
        {
            foreach (var raw in text.Split('\n'))
            {
                var i = 0;
                while (i < raw.Length && (raw[i] == ' ' || raw[i] == '\t')) i++;
                if (i > 0 && i < raw.Length && raw[i] == '"') return raw.Substring(0, i);
            }
            return "  ";
        }

        // ============================================================ состояние файла

        internal enum FileState
        {
            /// <summary>Файла нет.</summary>
            Missing,

            /// <summary>Файл есть, но это не корректный JSON-объект (при записи будет бэкап).</summary>
            Invalid,

            /// <summary>Файл есть, серверов VHR в нём нет.</summary>
            NotConfigured,

            /// <summary>Серверы VHR есть, но не все или в старом виде — «Создать файлы» обновит.</summary>
            NeedsUpdate,

            /// <summary>Оба сервера VHR записаны как нужно.</summary>
            Configured
        }

        /// <summary>Состояние текста файла клиента; <paramref name="literalToken"/> — в файле найден токен в открытом виде.</summary>
        public static FileState Inspect(string text, VhrMcpClient client, out bool literalToken)
        {
            literalToken = text != null && LiteralToken.IsMatch(text);
            if (text == null) return FileState.Missing;
            var probe = MergeText(text, client);
            if (probe.WasInvalid) return FileState.Invalid;
            if (probe.CreatedNew) return FileState.NotConfigured; // пустой файл
            var unchanged = 0;
            var added = 0;
            foreach (var s in probe.Servers)
            {
                if (s.Value == ServerChange.Unchanged) unchanged++;
                if (s.Value == ServerChange.Added) added++;
            }
            if (unchanged == probe.Servers.Count) return FileState.Configured;
            return added == probe.Servers.Count ? FileState.NotConfigured : FileState.NeedsUpdate;
        }

        /// <summary>Похоже ли значение на личный токен VHR (<c>vhr_pat_</c> + 40 символов base62).</summary>
        public static bool LooksLikeToken(string value) => value != null && TokenFormat.IsMatch(value.Trim());

        /// <summary>Безопасный показ токена: только префикс, как в кабинете (<c>vhr_pat_ab12…</c>).</summary>
        public static string MaskToken(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            value = value.Trim();
            return value.StartsWith("vhr_pat_", StringComparison.Ordinal) && value.Length > 12
                ? value.Substring(0, 12) + "…"
                : "значение длиной " + value.Length.ToString(CultureInfo.InvariantCulture);
        }

        // ============================================================ файлы

        internal sealed class FileResult
        {
            public VhrMcpClient Client;
            public string Path;
            public MergeOutcome Merge;
            public string BackupPath;
            public string Error;

            public bool Ok => Error == null;

            /// <summary>Что произошло с файлом — одной-двумя фразами для окна.</summary>
            public string Summary()
            {
                var file = RelativePath(Client);
                if (Error != null) return $"{file}: не удалось записать — {Error}";
                if (Merge.WasInvalid)
                    return $"{file}: был некорректный JSON ({Merge.InvalidReason}). Старое содержимое сохранено в " +
                           $"{System.IO.Path.GetFileName(BackupPath)}, файл создан заново с серверами {ServerName} и {DocsServerName}.";
                if (Merge.CreatedNew) return $"{file}: создан — серверы {ServerName} и {DocsServerName}.";
                if (!Merge.Changed) return $"{file}: без изменений — серверы {ServerName} и {DocsServerName} уже настроены.";

                var parts = new List<string>();
                var added = Names(ServerChange.Added);
                var updated = Names(ServerChange.Updated);
                var same = Names(ServerChange.Unchanged);
                if (added.Length > 0) parts.Add("добавлены " + added);
                if (updated.Length > 0) parts.Add("обновлены " + updated);
                if (same.Length > 0) parts.Add("без изменений " + same);
                return $"{file}: обновлён — {string.Join(", ", parts)}. Остальные серверы и настройки не тронуты.";
            }

            private string Names(ServerChange kind)
            {
                var l = new List<string>();
                foreach (var s in Merge.Servers)
                    if (s.Value == kind) l.Add(s.Key);
                return string.Join(", ", l);
            }
        }

        /// <summary>
        /// Создаёт или обновляет файл клиента в <paramref name="projectRoot"/>. Некорректный файл
        /// сначала копируется в <c>&lt;файл&gt;.vhr-backup-ГГГГММДД-ЧЧММСС</c>. Исключения не бросает.
        /// </summary>
        public static FileResult WriteConfig(string projectRoot, VhrMcpClient client, DateTime now)
        {
            var path = Path.Combine(projectRoot, RelativePath(client).Replace('/', Path.DirectorySeparatorChar));
            var r = new FileResult { Client = client, Path = path };
            try
            {
                string existing = null;
                var bom = false;
                if (File.Exists(path))
                {
                    var bytes = File.ReadAllBytes(path);
                    bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                    existing = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
                }

                r.Merge = MergeText(existing, client);
                if (r.Merge.WasInvalid)
                {
                    r.BackupPath = BackupPathFor(path, now);
                    File.Copy(path, r.BackupPath, false);
                }
                if (!r.Merge.Changed) return r;

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                WriteAtomically(path, r.Merge.Text, bom);
            }
            catch (Exception e)
            {
                r.Error = e.Message;
            }
            return r;
        }

        private static string BackupPathFor(string path, DateTime now)
        {
            var baseName = path + BackupMarker + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var candidate = baseName;
            for (var i = 2; File.Exists(candidate); i++)
                candidate = baseName + "-" + i.ToString(CultureInfo.InvariantCulture);
            return candidate;
        }

        // Через временный файл: если запись оборвётся, исходный файл останется целым.
        private static void WriteAtomically(string path, string text, bool bom)
        {
            var encoding = new UTF8Encoding(bom);
            var tmp = path + ".vhr-tmp";
            try
            {
                File.WriteAllText(tmp, text, encoding);
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch
            {
                // File.Replace недоступен на части файловых систем — пишем напрямую.
                File.WriteAllText(path, text, encoding);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch { /* временный файл — не критично */ }
            }
        }

        // ============================================================ .gitignore

        /// <summary>
        /// .gitignore проекта: в корне Unity-проекта, иначе — в корне git-репозитория выше по дереву.
        /// <paramref name="prefix"/> — путь проекта относительно папки .gitignore (<c>""</c> или <c>"Game/"</c>).
        /// </summary>
        public static string FindGitignore(string projectRoot, out string prefix)
        {
            prefix = string.Empty;
            try
            {
                var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var own = Path.Combine(root, ".gitignore");
                if (File.Exists(own)) return own;
                if (IsGitRoot(root)) return null; // проект — сам корень репозитория, а .gitignore нет

                for (var dir = Directory.GetParent(root); dir != null; dir = dir.Parent)
                {
                    if (!IsGitRoot(dir.FullName)) continue;
                    var ignore = Path.Combine(dir.FullName, ".gitignore");
                    if (!File.Exists(ignore)) return null;
                    var rel = root.Substring(dir.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length)
                        .Replace('\\', '/').Trim('/');
                    prefix = rel.Length == 0 ? string.Empty : rel + "/";
                    return ignore;
                }
            }
            catch
            {
                // нет доступа к родительской папке и т.п. — считаем, что .gitignore нет
            }
            return null;
        }

        private static bool IsGitRoot(string dir)
        {
            var git = Path.Combine(dir, ".git");
            return Directory.Exists(git) || File.Exists(git); // .git-файл — у worktree и сабмодулей
        }

        /// <summary>Строки .gitignore для файлов выбранных клиентов и бэкапов (якорные, от папки .gitignore).</summary>
        public static List<string> GitignoreEntries(IEnumerable<VhrMcpClient> clients, string prefix)
        {
            var p = EscapeGitignore(prefix ?? string.Empty);
            var l = new List<string>();
            foreach (var c in clients) l.Add("/" + p + RelativePath(c));
            l.Add("*" + BackupMarker + "*");
            return l;
        }

        public const string GitignoreHeader = "# VHR SDK: конфиги MCP для ИИ-ассистентов (токен — в переменной окружения VHR_MCP_TOKEN, не в файлах)";

        /// <summary>Дописывает в текст .gitignore отсутствующие строки. <paramref name="added"/> — сколько добавлено.</summary>
        public static string AppendGitignore(string existing, IList<string> entries, out int added)
        {
            existing ??= string.Empty;
            var nl = existing.Contains("\r\n") ? "\r\n" : "\n";
            var have = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in existing.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0 || t[0] == '#') continue;
                have.Add(t.TrimStart('/'));
            }

            var toAdd = new List<string>();
            foreach (var e in entries)
            {
                var key = e.Trim().TrimStart('/');
                if (have.Add(key)) toAdd.Add(e);
            }
            added = toAdd.Count;
            if (added == 0) return existing;

            var sb = new StringBuilder(existing);
            if (existing.Length > 0 && !existing.EndsWith("\n", StringComparison.Ordinal)) sb.Append(nl);
            if (existing.Length > 0) sb.Append(nl);
            if (existing.IndexOf(GitignoreHeader, StringComparison.Ordinal) < 0) sb.Append(GitignoreHeader).Append(nl);
            foreach (var e in toAdd) sb.Append(e).Append(nl);
            return sb.ToString();
        }

        private static string EscapeGitignore(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (c == '\\' || c == '*' || c == '?' || c == '[' || c == '!' || c == '#') sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        // ============================================================ модель для записи

        /// <summary>Упорядоченный JSON-объект из строк и вложенных объектов — то, что пишет SDK.</summary>
        internal sealed class JObj
        {
            public readonly List<KeyValuePair<string, object>> Fields = new List<KeyValuePair<string, object>>();

            public int Count => Fields.Count;

            public JObj Add(string key, string value)
            {
                Fields.Add(new KeyValuePair<string, object>(key, value));
                return this;
            }

            public JObj Add(string key, JObj value)
            {
                Fields.Add(new KeyValuePair<string, object>(key, value));
                return this;
            }

            /// <summary>
            /// Текст объекта: открывающая скобка без отступа (её место задаёт вызывающий), поля —
            /// с отступом <paramref name="indent"/> + <paramref name="unit"/>, закрывающая — с <paramref name="indent"/>.
            /// </summary>
            public string Render(string indent, string unit, string nl)
            {
                if (Fields.Count == 0) return "{}";
                var sb = new StringBuilder("{");
                for (var i = 0; i < Fields.Count; i++)
                {
                    var f = Fields[i];
                    sb.Append(nl).Append(indent).Append(unit).Append(Quote(f.Key)).Append(": ");
                    sb.Append(f.Value is JObj o ? o.Render(indent + unit, unit, nl) : Quote((string)f.Value));
                    if (i < Fields.Count - 1) sb.Append(',');
                }
                return sb.Append(nl).Append(indent).Append('}').ToString();
            }

            /// <summary>Совпадает ли разобранный узел с этим объектом по смыслу (порядок полей не важен).</summary>
            internal bool SameAs(Node n)
            {
                if (n == null || n.Kind != JKind.Object) return false;
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var m in n.Members) keys.Add(m.Key);
                if (keys.Count != Fields.Count) return false;
                foreach (var f in Fields)
                {
                    var v = n.Get(f.Key);
                    if (v == null) return false;
                    if (f.Value is JObj o)
                    {
                        if (!o.SameAs(v)) return false;
                    }
                    else if (v.Kind != JKind.String || v.Str != (string)f.Value)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        internal static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2).Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        // ============================================================ разбор JSON / JSONC

        internal enum JKind { Object, Array, String, Number, True, False, Null }

        /// <summary>Узел с позицией в тексте: [Start, End).</summary>
        internal sealed class Node
        {
            public JKind Kind;
            public int Start;
            public int End;
            public string Str;
            public readonly List<Member> Members = new List<Member>();

            public Node Get(string key)
            {
                if (Kind != JKind.Object) return null;
                for (var i = Members.Count - 1; i >= 0; i--) // как JSON.parse: при дублях выигрывает последний
                    if (Members[i].Key == key) return Members[i].Value;
                return null;
            }
        }

        internal sealed class Member
        {
            public string Key;
            public int KeyStart;
            public Node Value;
        }

        /// <summary>
        /// JSON с поблажками JSONC (как у VS Code и Cursor): комментарии <c>//</c> и <c>/* */</c>,
        /// висячие запятые. Ошибка — <see cref="FormatException"/>.
        /// </summary>
        internal static Node Parse(string text)
        {
            var p = new Parser(text);
            p.Ws();
            var n = p.Value();
            p.Ws();
            if (p.Pos != text.Length) throw p.Err("лишние символы после JSON");
            return n;
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _depth;
            public int Pos;

            public Parser(string s) => _s = s ?? string.Empty;

            public FormatException Err(string what)
            {
                var line = 1;
                for (var i = 0; i < Pos && i < _s.Length; i++)
                    if (_s[i] == '\n') line++;
                return new FormatException($"{what}, строка {line.ToString(CultureInfo.InvariantCulture)}");
            }

            private char Peek() => Pos < _s.Length ? _s[Pos] : '\0';

            public void Ws()
            {
                while (Pos < _s.Length)
                {
                    var c = _s[Pos];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == Bom)
                    {
                        Pos++;
                    }
                    else if (c == '/' && Pos + 1 < _s.Length && _s[Pos + 1] == '/')
                    {
                        while (Pos < _s.Length && _s[Pos] != '\n') Pos++;
                    }
                    else if (c == '/' && Pos + 1 < _s.Length && _s[Pos + 1] == '*')
                    {
                        var end = _s.IndexOf("*/", Pos + 2, StringComparison.Ordinal);
                        if (end < 0) throw Err("незакрытый комментарий /*");
                        Pos = end + 2;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            public Node Value()
            {
                if (Pos >= _s.Length) throw Err("неожиданный конец файла");
                var c = _s[Pos];
                switch (c)
                {
                    case '{': return Obj();
                    case '[': return Arr();
                    case '"':
                        var start = Pos;
                        var str = Str();
                        return new Node { Kind = JKind.String, Start = start, End = Pos, Str = str };
                    case 't': return Lit("true", JKind.True);
                    case 'f': return Lit("false", JKind.False);
                    case 'n': return Lit("null", JKind.Null);
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return Num();
                        throw Err($"неожиданный символ '{c}'");
                }
            }

            private Node Obj()
            {
                if (++_depth > 64) throw Err("слишком глубокая вложенность");
                var n = new Node { Kind = JKind.Object, Start = Pos };
                Pos++;
                while (true)
                {
                    Ws();
                    if (Peek() == '}')
                    {
                        Pos++;
                        break;
                    }
                    if (Peek() != '"') throw Err("ожидалось имя поля в кавычках");
                    var keyStart = Pos;
                    var key = Str();
                    Ws();
                    if (Peek() != ':') throw Err("ожидалось ':'");
                    Pos++;
                    Ws();
                    n.Members.Add(new Member { Key = key, KeyStart = keyStart, Value = Value() });
                    Ws();
                    var c = Peek();
                    if (c == ',')
                    {
                        Pos++;
                        continue; // висячая запятая перед '}' допустима (JSONC)
                    }
                    if (c == '}')
                    {
                        Pos++;
                        break;
                    }
                    throw Err("ожидалось ',' или '}'");
                }
                n.End = Pos;
                _depth--;
                return n;
            }

            private Node Arr()
            {
                if (++_depth > 64) throw Err("слишком глубокая вложенность");
                var n = new Node { Kind = JKind.Array, Start = Pos };
                Pos++;
                while (true)
                {
                    Ws();
                    if (Peek() == ']')
                    {
                        Pos++;
                        break;
                    }
                    Value();
                    Ws();
                    var c = Peek();
                    if (c == ',')
                    {
                        Pos++;
                        continue;
                    }
                    if (c == ']')
                    {
                        Pos++;
                        break;
                    }
                    throw Err("ожидалось ',' или ']'");
                }
                n.End = Pos;
                _depth--;
                return n;
            }

            private string Str()
            {
                Pos++; // открывающая кавычка
                var sb = new StringBuilder();
                while (true)
                {
                    if (Pos >= _s.Length) throw Err("незакрытая строка");
                    var c = _s[Pos++];
                    if (c == '"') return sb.ToString();
                    if (c < ' ') throw Err("управляющий символ в строке");
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (Pos >= _s.Length) throw Err("незакрытая строка");
                    var e = _s[Pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (Pos + 4 > _s.Length || !int.TryParse(_s.Substring(Pos, 4), NumberStyles.AllowHexSpecifier,
                                    CultureInfo.InvariantCulture, out var code))
                                throw Err("неверная последовательность \\u");
                            sb.Append((char)code);
                            Pos += 4;
                            break;
                        default: throw Err("неверная escape-последовательность");
                    }
                }
            }

            private Node Num()
            {
                var start = Pos;
                if (Peek() == '-') Pos++;
                if (Peek() == '0')
                {
                    Pos++;
                    if (Peek() >= '0' && Peek() <= '9') throw Err("неверное число (ведущий ноль)");
                }
                else if (!Digits())
                {
                    throw Err("неверное число");
                }
                if (Peek() == '.')
                {
                    Pos++;
                    if (!Digits()) throw Err("неверное число");
                }
                if (Peek() == 'e' || Peek() == 'E')
                {
                    Pos++;
                    if (Peek() == '+' || Peek() == '-') Pos++;
                    if (!Digits()) throw Err("неверное число");
                }
                return new Node { Kind = JKind.Number, Start = start, End = Pos, Str = _s.Substring(start, Pos - start) };
            }

            private bool Digits()
            {
                var s = Pos;
                while (Pos < _s.Length && _s[Pos] >= '0' && _s[Pos] <= '9') Pos++;
                return Pos > s;
            }

            private Node Lit(string word, JKind kind)
            {
                if (string.CompareOrdinal(_s, Pos, word, 0, word.Length) != 0) throw Err("неожиданный символ");
                var n = new Node { Kind = kind, Start = Pos, End = Pos + word.Length };
                Pos += word.Length;
                return n;
            }
        }
    }
}
