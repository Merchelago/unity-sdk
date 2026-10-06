// Общее ядро установки и обновления SDK — КОПИЯ региона VHR-SETUP-CORE из
// Installer~/VhrSdkInstaller.cs (установщик обязан быть одним файлом без зависимостей от SDK).
// Меняйте оба файла одинаково: .github/scripts/check-release.mjs сверяет текст между маркерами.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VhrGames.Sdk.Editor
{
    // >>> VHR-SETUP-CORE
    // Общее ядро установщика (Installer~/VhrSdkInstaller.cs) и окна «VHR → Обновление SDK»
    // (Editor/VhrSetupCore.cs). Текст между маркерами в обоих файлах ОДИНАКОВЫЙ — это проверяет
    // .github/scripts/check-release.mjs. Только System.*: без UnityEditor, SDK, R3 и VContainer.
    internal static class VhrSetupCore
    {
        // ================================================================= JSON

        internal enum JKind { Object, Array, String, Number, True, False, Null }

        /// <summary>Узел JSON с позицией в исходном тексте: [Start, End).</summary>
        internal sealed class JNode
        {
            public JKind Kind;
            public int Start;
            public int End;
            public string Str;
            public readonly List<JMember> Members = new List<JMember>();
            public readonly List<JNode> Items = new List<JNode>();

            public JNode Get(string key)
            {
                if (Kind != JKind.Object) return null;
                for (var i = Members.Count - 1; i >= 0; i--) // как JSON.parse: при дублях выигрывает последний
                    if (Members[i].Key == key) return Members[i].Value;
                return null;
            }

            public string GetString(string key)
            {
                var n = Get(key);
                return n != null && n.Kind == JKind.String ? n.Str : null;
            }

            public Dictionary<string, string> ToStringMap()
            {
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                if (Kind == JKind.Object)
                    foreach (var m in Members)
                        if (m.Value.Kind == JKind.String) d[m.Key] = m.Value.Str;
                return d;
            }

            public List<string> ToStringList()
            {
                var l = new List<string>();
                if (Kind == JKind.Array)
                    foreach (var n in Items)
                        if (n.Kind == JKind.String) l.Add(n.Str);
                return l;
            }
        }

        internal sealed class JMember
        {
            public string Key;
            public int KeyStart;
            public JNode Value;
        }

        /// <summary>Строгий JSON (RFC 8259). Ошибка — <see cref="FormatException"/>.</summary>
        internal static JNode ParseJson(string text)
        {
            var p = new JsonParser(text);
            p.Ws();
            var n = p.Value();
            p.Ws();
            if (p.Pos != p.Length) throw p.Err("лишние символы после JSON");
            return n;
        }

        internal static bool TryParseJson(string text, out JNode node)
        {
            try
            {
                node = ParseJson(text);
                return true;
            }
            catch (FormatException)
            {
                node = null;
                return false;
            }
        }

        internal static string Quote(string s)
        {
            var sb = new StringBuilder((s ?? string.Empty).Length + 2).Append('"');
            foreach (var c in s ?? string.Empty)
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

        private sealed class JsonParser
        {
            private readonly string _s;
            private int _depth;
            public int Pos;

            public JsonParser(string s) => _s = s ?? string.Empty;

            public int Length => _s.Length;

            public FormatException Err(string what) => new FormatException($"{what} (символ {Pos})");

            private char Peek() => Pos < _s.Length ? _s[Pos] : '\0';

            public void Ws()
            {
                while (Pos < _s.Length)
                {
                    var c = _s[Pos];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || (c == '﻿' && Pos == 0)) Pos++;
                    else break;
                }
            }

            public JNode Value()
            {
                if (Pos >= _s.Length) throw Err("неожиданный конец JSON");
                var c = _s[Pos];
                switch (c)
                {
                    case '{': return Obj();
                    case '[': return Arr();
                    case '"':
                        var start = Pos;
                        var str = Str();
                        return new JNode { Kind = JKind.String, Start = start, End = Pos, Str = str };
                    case 't': return Lit("true", JKind.True);
                    case 'f': return Lit("false", JKind.False);
                    case 'n': return Lit("null", JKind.Null);
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return Num();
                        throw Err($"неожиданный символ '{c}'");
                }
            }

            private JNode Obj()
            {
                if (++_depth > 64) throw Err("слишком глубокая вложенность");
                var n = new JNode { Kind = JKind.Object, Start = Pos };
                Pos++;
                Ws();
                if (Peek() == '}')
                {
                    Pos++;
                }
                else
                {
                    while (true)
                    {
                        Ws();
                        if (Peek() != '"') throw Err("ожидалось имя поля в кавычках");
                        var keyStart = Pos;
                        var key = Str();
                        Ws();
                        if (Peek() != ':') throw Err("ожидалось ':'");
                        Pos++;
                        Ws();
                        n.Members.Add(new JMember { Key = key, KeyStart = keyStart, Value = Value() });
                        Ws();
                        var c = Peek();
                        Pos++;
                        if (c == ',') continue;
                        if (c == '}') break;
                        Pos--;
                        throw Err("ожидалось ',' или '}'");
                    }
                }
                n.End = Pos;
                _depth--;
                return n;
            }

            private JNode Arr()
            {
                if (++_depth > 64) throw Err("слишком глубокая вложенность");
                var n = new JNode { Kind = JKind.Array, Start = Pos };
                Pos++;
                Ws();
                if (Peek() == ']')
                {
                    Pos++;
                }
                else
                {
                    while (true)
                    {
                        Ws();
                        n.Items.Add(Value());
                        Ws();
                        var c = Peek();
                        Pos++;
                        if (c == ',') continue;
                        if (c == ']') break;
                        Pos--;
                        throw Err("ожидалось ',' или ']'");
                    }
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

            private JNode Num()
            {
                var start = Pos;
                if (Peek() == '-') Pos++;
                if (!Digits()) throw Err("неверное число");
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
                return new JNode { Kind = JKind.Number, Start = start, End = Pos, Str = _s.Substring(start, Pos - start) };
            }

            private bool Digits()
            {
                var s = Pos;
                while (Pos < _s.Length && _s[Pos] >= '0' && _s[Pos] <= '9') Pos++;
                return Pos > s;
            }

            private JNode Lit(string word, JKind kind)
            {
                if (string.CompareOrdinal(_s, Pos, word, 0, word.Length) != 0) throw Err("неожиданный символ");
                var n = new JNode { Kind = kind, Start = Pos, End = Pos + word.Length };
                Pos += word.Length;
                return n;
            }
        }

        // ================================================================= SemVer

        /// <summary>SemVer 2.0: «1.10.0», «v1.10.0», «1.10.0-preview.1». Некорректная строка меньше любой корректной.</summary>
        internal static class SemVer
        {
            public static bool TryParse(string s, out int[] core, out string pre)
            {
                core = null;
                pre = null;
                if (string.IsNullOrWhiteSpace(s)) return false;
                s = s.Trim();
                if (s[0] == 'v' || s[0] == 'V') s = s.Substring(1);
                var plus = s.IndexOf('+');
                if (plus >= 0) s = s.Substring(0, plus);
                var dash = s.IndexOf('-');
                if (dash >= 0)
                {
                    pre = s.Substring(dash + 1);
                    s = s.Substring(0, dash);
                    if (pre.Length == 0) return false;
                }
                var parts = s.Split('.');
                if (parts.Length < 2 || parts.Length > 3) return false;
                var nums = new int[3];
                for (var i = 0; i < parts.Length; i++)
                    if (parts[i].Length == 0 || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i]))
                        return false;
                core = nums;
                return true;
            }

            public static bool IsValid(string s) => TryParse(s, out _, out _);

            /// <summary>&lt;0 — a меньше b, 0 — равны, &gt;0 — a больше.</summary>
            public static int Compare(string a, string b)
            {
                var okA = TryParse(a, out var ca, out var pa);
                var okB = TryParse(b, out var cb, out var pb);
                if (!okA || !okB) return okA == okB ? 0 : (okA ? 1 : -1);
                for (var i = 0; i < 3; i++)
                    if (ca[i] != cb[i]) return ca[i] < cb[i] ? -1 : 1;
                if (pa == null || pb == null) return pa == pb ? 0 : (pa == null ? 1 : -1); // релиз > пре-релиза
                var xa = pa.Split('.');
                var xb = pb.Split('.');
                for (var i = 0; i < Math.Min(xa.Length, xb.Length); i++)
                {
                    var na = int.TryParse(xa[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ia);
                    var nb = int.TryParse(xb[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ib);
                    int r;
                    if (na && nb) r = ia.CompareTo(ib);
                    else if (na != nb) r = na ? -1 : 1;
                    else r = string.CompareOrdinal(xa[i], xb[i]);
                    if (r != 0) return r < 0 ? -1 : 1;
                }
                return xa.Length.CompareTo(xb.Length);
            }
        }

        // ================================================================= зависимости

        internal enum DepSource { Registry, Git, Local, Other }

        /// <summary>Вид записи в dependencies: «1.2.9» — реестр, «https://…git#v1.9.0» — git, «file:…» — локальный.</summary>
        internal static DepSource ClassifyDependency(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return DepSource.Other;
            var v = value.Trim();
            if (v.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return DepSource.Local;
            if (char.IsDigit(v[0]) && SemVer.IsValid(v)) return DepSource.Registry;
            if (v.StartsWith("git", StringComparison.OrdinalIgnoreCase) || v.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                v.StartsWith("ssh:", StringComparison.OrdinalIgnoreCase) || v.Contains(".git"))
                return DepSource.Git;
            return DepSource.Other;
        }

        /// <summary>Версия из git-ссылки: «…unity-sdk.git#v1.9.0» → «1.9.0»; без тега — null.</summary>
        internal static string GitVersionTag(string value)
        {
            var i = (value ?? string.Empty).LastIndexOf('#');
            if (i < 0) return null;
            var frag = value.Substring(i + 1).Trim();
            return SemVer.IsValid(frag) ? frag.TrimStart('v', 'V') : null;
        }

        /// <summary>Git-ссылка без «#тега».</summary>
        internal static string GitBase(string value)
        {
            var i = (value ?? string.Empty).IndexOf('#');
            return i < 0 ? value : value.Substring(0, i);
        }

        /// <summary>Покрывает ли скоуп реестра пакет: «com.cysharp» → «com.cysharp.r3».</summary>
        internal static bool ScopeCovers(string scope, string packageName)
        {
            if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrEmpty(packageName)) return false;
            scope = scope.Trim();
            return packageName == scope || packageName.StartsWith(scope + ".", StringComparison.Ordinal);
        }

        internal static bool SameUrl(string a, string b) =>
            string.Equals((a ?? string.Empty).Trim().TrimEnd('/'), (b ?? string.Empty).Trim().TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);

        // ================================================================= manifest.json

        /// <summary>
        /// Точечная правка <c>Packages/manifest.json</c>: вставки и замены прямо в тексте, без
        /// пересериализации — порядок, отступы, переводы строк и чужие записи остаются как были.
        /// После каждой правки текст заново разбирается (битый JSON не получится).
        /// </summary>
        internal sealed class Manifest
        {
            private readonly string _nl;
            private readonly string _unit = "  ";

            /// <summary>Человекочитаемый список изменений («+ …», «~ …»).</summary>
            public readonly List<string> Changes = new List<string>();

            public string Text { get; private set; }

            public Manifest(string text)
            {
                Text = text ?? string.Empty;
                var root = ParseJson(Text);
                if (root.Kind != JKind.Object) throw new FormatException("корень manifest.json должен быть объектом");
                _nl = Text.Contains("\r\n") ? "\r\n" : "\n";
                if (root.Members.Count > 0 && !SameLine(root.Start, root.Members[0].KeyStart))
                {
                    var ind = LineIndent(root.Members[0].KeyStart);
                    if (ind.Length > 0) _unit = ind;
                }
            }

            public JNode Root => ParseJson(Text);

            public string GetDependency(string name) => Root.Get("dependencies")?.GetString(name);

            public Dictionary<string, string> GetDependencies() =>
                Root.Get("dependencies")?.ToStringMap() ?? new Dictionary<string, string>(StringComparer.Ordinal);

            /// <summary>Добавляет или меняет зависимость. Новая встаёт по алфавиту, если список отсортирован.</summary>
            public bool SetDependency(string name, string value)
            {
                var root = Root;
                var deps = root.Get("dependencies");
                if (deps == null)
                {
                    InsertMember(root, "dependencies", ind => "{" + _nl + ind + _unit + Quote(name) + ": " + Quote(value) + _nl + ind + "}", false);
                    Changes.Add($"+ {name}: {value}");
                    return true;
                }
                if (deps.Kind != JKind.Object) throw new FormatException("\"dependencies\" в manifest.json — не объект");
                var cur = deps.Get(name);
                if (cur == null)
                {
                    InsertMember(deps, name, _ => Quote(value), true);
                    Changes.Add($"+ {name}: {value}");
                    return true;
                }
                if (cur.Kind == JKind.String && cur.Str == value) return false;
                var old = cur.Kind == JKind.String ? cur.Str : Text.Substring(cur.Start, cur.End - cur.Start);
                Replace(cur.Start, cur.End, Quote(value));
                Changes.Add($"~ {name}: {old} → {value}");
                return true;
            }

            /// <summary>Покрыт ли пакет/скоуп каким-нибудь scoped-реестром проекта.</summary>
            public bool IsScopeCovered(string scopeOrPackage)
            {
                var regs = Root.Get("scopedRegistries");
                if (regs == null || regs.Kind != JKind.Array) return false;
                foreach (var r in regs.Items)
                {
                    var scopes = r.Get("scopes");
                    if (scopes == null) continue;
                    foreach (var s in scopes.ToStringList())
                        if (ScopeCovers(s, scopeOrPackage)) return true;
                }
                return false;
            }

            /// <summary>
            /// Добавляет недостающие скоупы: в реестр с тем же URL, иначе — новым реестром. Скоуп,
            /// уже покрытый любым реестром (тем же или более общим), не трогаем.
            /// </summary>
            public bool EnsureRegistry(string name, string url, IList<string> scopes)
            {
                if (string.IsNullOrWhiteSpace(url) || scopes == null) return false;
                var missing = new List<string>();
                foreach (var raw in scopes)
                {
                    var s = (raw ?? string.Empty).Trim();
                    if (s.Length > 0 && !missing.Contains(s) && !IsScopeCovered(s)) missing.Add(s);
                }
                if (missing.Count == 0) return false;

                var root = Root;
                var regs = root.Get("scopedRegistries");
                if (regs == null)
                {
                    InsertMember(root, "scopedRegistries",
                        ind => "[" + _nl + ind + _unit + RenderRegistry(name, url, missing, ind + _unit) + _nl + ind + "]", false);
                    Changes.Add($"+ реестр {name} ({url}): {string.Join(", ", missing)}");
                    return true;
                }
                if (regs.Kind != JKind.Array) throw new FormatException("\"scopedRegistries\" в manifest.json — не массив");

                var idx = regs.Items.FindIndex(r => r.Kind == JKind.Object && SameUrl(r.GetString("url"), url));
                if (idx < 0)
                {
                    InsertItem(regs, ind => RenderRegistry(name, url, missing, ind));
                    Changes.Add($"+ реестр {name} ({url}): {string.Join(", ", missing)}");
                    return true;
                }

                var regName = regs.Items[idx].GetString("name") ?? url;
                foreach (var s in missing)
                {
                    var reg = Root.Get("scopedRegistries").Items[idx];
                    var sc = reg.Get("scopes");
                    if (sc == null) InsertMember(reg, "scopes", _ => "[" + Quote(s) + "]", false);
                    else if (sc.Kind == JKind.Array) InsertItem(sc, _ => Quote(s));
                    else throw new FormatException("\"scopes\" реестра " + regName + " — не массив");
                }
                Changes.Add($"+ скоупы в реестр {regName}: {string.Join(", ", missing)}");
                return true;
            }

            private string RenderRegistry(string name, string url, List<string> scopes, string ind)
            {
                var i1 = ind + _unit;
                var i2 = i1 + _unit;
                var sb = new StringBuilder("{").Append(_nl)
                    .Append(i1).Append("\"name\": ").Append(Quote(name)).Append(',').Append(_nl)
                    .Append(i1).Append("\"url\": ").Append(Quote(url)).Append(',').Append(_nl)
                    .Append(i1).Append("\"scopes\": [").Append(_nl);
                for (var k = 0; k < scopes.Count; k++)
                    sb.Append(i2).Append(Quote(scopes[k])).Append(k < scopes.Count - 1 ? "," : string.Empty).Append(_nl);
                return sb.Append(i1).Append(']').Append(_nl).Append(ind).Append('}').ToString();
            }

            // render(indent) — текст значения для строки с отступом indent.
            private void InsertMember(JNode obj, string key, Func<string, string> render, bool sorted)
            {
                var members = obj.Members;
                if (members.Count == 0)
                {
                    var objIndent = LineIndent(obj.Start);
                    var child = objIndent + _unit;
                    Replace(obj.Start + 1, obj.End - 1, _nl + child + Quote(key) + ": " + render(child) + _nl + objIndent);
                    return;
                }

                var at = members.Count;
                if (sorted && IsSorted(members))
                    for (var i = 0; i < members.Count; i++)
                        if (string.CompareOrdinal(members[i].Key, key) > 0)
                        {
                            at = i;
                            break;
                        }

                if (at < members.Count)
                {
                    var m = members[at];
                    var prevEnd = at > 0 ? members[at - 1].Value.End : obj.Start;
                    if (SameLine(prevEnd, m.KeyStart))
                    {
                        Insert(m.KeyStart, Quote(key) + ": " + render(LineIndent(m.KeyStart) + _unit) + ", ");
                    }
                    else
                    {
                        var ind = LineIndent(m.KeyStart);
                        Insert(m.KeyStart, Quote(key) + ": " + render(ind) + "," + _nl + ind);
                    }
                    return;
                }

                var last = members[members.Count - 1];
                if (SameLine(obj.Start, last.KeyStart))
                {
                    Insert(last.Value.End, ", " + Quote(key) + ": " + render(LineIndent(obj.Start) + _unit));
                }
                else
                {
                    var ind = LineIndent(last.KeyStart);
                    Insert(last.Value.End, "," + _nl + ind + Quote(key) + ": " + render(ind));
                }
            }

            private void InsertItem(JNode arr, Func<string, string> render)
            {
                if (arr.Items.Count == 0)
                {
                    var arrIndent = LineIndent(arr.Start);
                    var child = arrIndent + _unit;
                    Replace(arr.Start + 1, arr.End - 1, _nl + child + render(child) + _nl + arrIndent);
                    return;
                }
                var last = arr.Items[arr.Items.Count - 1];
                if (SameLine(arr.Start, last.Start))
                {
                    Insert(last.End, ", " + render(LineIndent(arr.Start) + _unit));
                }
                else
                {
                    var ind = LineIndent(last.Start);
                    Insert(last.End, "," + _nl + ind + render(ind));
                }
            }

            private static bool IsSorted(List<JMember> members)
            {
                for (var i = 1; i < members.Count; i++)
                    if (string.CompareOrdinal(members[i - 1].Key, members[i].Key) > 0) return false;
                return true;
            }

            private bool SameLine(int a, int b)
            {
                for (var i = Math.Min(a, b); i < Math.Max(a, b) && i < Text.Length; i++)
                    if (Text[i] == '\n') return false;
                return true;
            }

            private string LineIndent(int pos)
            {
                var lineStart = pos;
                while (lineStart > 0 && Text[lineStart - 1] != '\n') lineStart--;
                var e = lineStart;
                while (e < Text.Length && (Text[e] == ' ' || Text[e] == '\t')) e++;
                return Text.Substring(lineStart, e - lineStart);
            }

            private void Insert(int pos, string s) => Replace(pos, pos, s);

            private void Replace(int start, int end, string with)
            {
                var updated = Text.Substring(0, start) + with + Text.Substring(end);
                if (!TryParseJson(updated, out _))
                    throw new InvalidOperationException("внутренняя ошибка правки manifest.json — файл не изменён");
                Text = updated;
            }
        }

        // ================================================================= копии ядра R3

        /// <summary>
        /// Сборки ядра R3, которые в Unity 2021.2+ даёт пакет org.nuget.r3 (и его зависимости).
        /// Те же DLL в Assets (NuGetForUnity, Assets/Plugins, .unitypackage) — дубли.
        /// </summary>
        internal static readonly string[] R3CoreAssemblies =
        {
            "R3.dll",
            "Microsoft.Bcl.TimeProvider.dll",
            "Microsoft.Bcl.AsyncInterfaces.dll",
            "System.Threading.Channels.dll",
            "System.ComponentModel.Annotations.dll",
            "System.Runtime.CompilerServices.Unsafe.dll",
        };

        internal sealed class R3CoreCopy
        {
            /// <summary>Что удалять: папка пакета NuGetForUnity («Assets/Packages/R3.1.2.9») или сама DLL.</summary>
            public string AssetPath;
            public bool IsFolder;
            /// <summary>Id пакета NuGetForUnity (для packages.config) или null.</summary>
            public string NuGetId;
            public readonly List<string> Assemblies = new List<string>();
        }

        // «…/<Id>.<Версия>/lib/…» — раскладка NuGetForUnity (папка репозитория может быть любой).
        private static readonly Regex NuGetPackageFolder = new Regex(
            @"^(?<dir>(?:.*/)?(?<id>[^/]+?)\.(?<ver>\d+(?:\.\d+){1,3}(?:-[0-9A-Za-z.\-]+)?))/lib/",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static List<R3CoreCopy> FindR3CoreCopies(string projectRoot)
        {
            var result = new List<R3CoreCopy>();
            var assets = Path.Combine(projectRoot, "Assets");
            if (!Directory.Exists(assets)) return result;

            var names = new HashSet<string>(R3CoreAssemblies, StringComparer.OrdinalIgnoreCase);
            var byPath = new Dictionary<string, R3CoreCopy>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in EnumerateVisibleFiles(assets, "*.dll"))
            {
                var fileName = Path.GetFileName(file);
                if (!names.Contains(fileName)) continue;
                var rel = "Assets/" + file.Substring(assets.Length).Replace('\\', '/').TrimStart('/');
                var unit = rel;
                var folder = false;
                string id = null;
                var m = NuGetPackageFolder.Match(rel);
                if (m.Success)
                {
                    unit = m.Groups["dir"].Value;
                    folder = true;
                    id = m.Groups["id"].Value;
                }
                if (!byPath.TryGetValue(unit, out var copy))
                {
                    copy = new R3CoreCopy { AssetPath = unit, IsFolder = folder, NuGetId = id };
                    byPath[unit] = copy;
                    result.Add(copy);
                }
                if (!copy.Assemblies.Contains(fileName)) copy.Assemblies.Add(fileName);
            }
            return result;
        }

        /// <summary>Убирает &lt;package id="…"/&gt; из packages.config NuGetForUnity, чтобы он не восстановил удалённое.</summary>
        internal static List<string> RemoveFromNuGetPackagesConfig(string projectRoot, ICollection<string> ids)
        {
            var changed = new List<string>();
            if (ids == null || ids.Count == 0) return changed;
            var set = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            var assets = Path.Combine(projectRoot, "Assets");
            if (!Directory.Exists(assets)) return changed;
            var entry = new Regex(@"^[ \t]*<package\s[^>]*?\bid\s*=\s*""(?<id>[^""]+)""[^>]*?/>[ \t]*(\r?\n)?",
                RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
            foreach (var file in EnumerateVisibleFiles(assets, "packages.config"))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var updated = entry.Replace(text, m => set.Contains(m.Groups["id"].Value) ? string.Empty : m.Value);
                    if (updated == text) continue;
                    File.WriteAllText(file, updated);
                    changed.Add(file);
                }
                catch (IOException)
                {
                    // файл занят — пропускаем, NuGetForUnity просто попробует восстановить пакет
                }
                catch (UnauthorizedAccessException)
                {
                    // нет прав — аналогично
                }
            }
            return changed;
        }

        // Файлы, которые видит Unity: без скрытых папок («.git», «.idea») и папок с «~» на конце.
        private static IEnumerable<string> EnumerateVisibleFiles(string dir, string pattern)
        {
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                string[] files, dirs;
                try
                {
                    files = Directory.GetFiles(d, pattern);
                    dirs = Directory.GetDirectories(d);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (var f in files) yield return f;
                foreach (var sub in dirs)
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith("~", StringComparison.Ordinal)) continue;
                    stack.Push(sub);
                }
            }
        }
    }
    // <<< VHR-SETUP-CORE
}
