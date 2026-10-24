using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// ВАЖНО: в этом файле нет ссылок на UnityEngine — его чистую логику (JSON, атомарная запись,
// файлы сохранений) проверяют тесты на обычном .NET.
namespace VhrGames.Sdk
{
    /// <summary>
    /// Минимальный JSON для локальных файлов SDK (очередь <see cref="VhrOutbox"/>, неотправленные
    /// сохранения и кэш слотов, SDK 1.12.0+). Свой разбор вместо <c>JsonUtility</c>: нужны
    /// <c>null</c> у чисел, строки-JSON внутри JSON и предсказуемое поведение на битом файле —
    /// любая ошибка разбора даёт <see cref="FormatException"/>, а не «пустой объект».
    /// <para>
    /// Модель: объект — <see cref="Dictionary{TKey,TValue}"/> (<c>string → object</c>), массив —
    /// <see cref="List{T}"/>, строка — <see cref="string"/>, целое — <see cref="long"/>, дробное —
    /// <see cref="double"/>, <c>true</c>/<c>false</c> — <see cref="bool"/>, <c>null</c> — <c>null</c>.
    /// </para>
    /// </summary>
    internal static class VhrJsonLite
    {
        private const int MaxDepth = 64;

        /// <summary>Разбирает JSON целиком. Ошибка синтаксиса или мусор после значения — <see cref="FormatException"/>.</summary>
        internal static object Parse(string json)
        {
            if (json == null) throw new FormatException("JSON = null.");
            var r = new Reader(json);
            r.SkipWs();
            var value = r.ReadValue(0);
            r.SkipWs();
            if (!r.End) throw new FormatException($"Лишние символы после JSON (позиция {r.Pos}).");
            return value;
        }

        /// <summary>Строковое поле объекта или <c>null</c> (нет поля / не строка).</summary>
        internal static string Str(Dictionary<string, object> o, string key)
            => o != null && o.TryGetValue(key, out var v) ? v as string : null;

        /// <summary>Целое поле объекта или <paramref name="fallback"/> (нет поля / не число / вне диапазона <c>long</c>).</summary>
        internal static long Long(Dictionary<string, object> o, string key, long fallback)
            => LongOrNull(o, key) ?? fallback;

        /// <summary>Целое поле объекта или <c>null</c> (нет поля, <c>null</c>, не число).</summary>
        internal static long? LongOrNull(Dictionary<string, object> o, string key)
        {
            if (o == null || !o.TryGetValue(key, out var v) || v == null) return null;
            if (v is long l) return l;
            if (v is double d && !double.IsNaN(d) && d >= long.MinValue && d <= long.MaxValue && Math.Floor(d) == d)
                return (long)d;
            return null;
        }

        /// <summary>Строка в JSON-кавычках с экранированием (<c>null</c> → литерал <c>null</c>).</summary>
        internal static void AppendString(StringBuilder sb, string s)
        {
            if (s == null)
            {
                sb.Append("null");
                return;
            }
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ' || c == (char)0x2028 || c == (char)0x2029)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>Строка в JSON-кавычках (см. <see cref="AppendString"/>).</summary>
        internal static string Quote(string s)
        {
            var sb = new StringBuilder((s?.Length ?? 4) + 2);
            AppendString(sb, s);
            return sb.ToString();
        }

        /// <summary>Пара <c>"имя":значение</c> с запятой перед ней, если это не первое поле.</summary>
        internal static void Field(StringBuilder sb, ref bool first, string name, string value)
        {
            Comma(sb, ref first, name);
            AppendString(sb, value);
        }

        /// <inheritdoc cref="Field(StringBuilder, ref bool, string, string)"/>
        internal static void Field(StringBuilder sb, ref bool first, string name, long value)
        {
            Comma(sb, ref first, name);
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        /// <inheritdoc cref="Field(StringBuilder, ref bool, string, string)"/>
        internal static void Field(StringBuilder sb, ref bool first, string name, long? value)
        {
            Comma(sb, ref first, name);
            sb.Append(value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null");
        }

        private static void Comma(StringBuilder sb, ref bool first, string name)
        {
            if (!first) sb.Append(',');
            first = false;
            AppendString(sb, name);
            sb.Append(':');
        }

        private sealed class Reader
        {
            private readonly string _s;
            private int _i;

            public Reader(string s) => _s = s;

            public int Pos => _i;
            public bool End => _i >= _s.Length;

            public void SkipWs()
            {
                while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t' || _s[_i] == '\n' || _s[_i] == '\r')) _i++;
            }

            private FormatException Error(string what) => new FormatException($"{what} (позиция {_i}).");

            public object ReadValue(int depth)
            {
                if (depth > MaxDepth) throw Error("Слишком глубокая вложенность JSON");
                if (End) throw Error("Неожиданный конец JSON");
                var c = _s[_i];
                switch (c)
                {
                    case '{': return ReadObject(depth);
                    case '[': return ReadArray(depth);
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                        throw Error($"Неожиданный символ '{c}'");
                }
            }

            private void Expect(string word)
            {
                if (_i + word.Length > _s.Length || string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0)
                    throw Error($"Ожидалось '{word}'");
                _i += word.Length;
            }

            private Dictionary<string, object> ReadObject(int depth)
            {
                var o = new Dictionary<string, object>(StringComparer.Ordinal);
                _i++; // {
                SkipWs();
                if (!End && _s[_i] == '}')
                {
                    _i++;
                    return o;
                }
                while (true)
                {
                    SkipWs();
                    if (End || _s[_i] != '"') throw Error("Ожидалось имя поля");
                    var key = ReadString();
                    SkipWs();
                    if (End || _s[_i] != ':') throw Error("Ожидалось ':'");
                    _i++;
                    SkipWs();
                    o[key] = ReadValue(depth + 1);
                    SkipWs();
                    if (End) throw Error("Неожиданный конец объекта");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == '}') { _i++; return o; }
                    throw Error("Ожидалось ',' или '}'");
                }
            }

            private List<object> ReadArray(int depth)
            {
                var a = new List<object>();
                _i++; // [
                SkipWs();
                if (!End && _s[_i] == ']')
                {
                    _i++;
                    return a;
                }
                while (true)
                {
                    SkipWs();
                    a.Add(ReadValue(depth + 1));
                    SkipWs();
                    if (End) throw Error("Неожиданный конец массива");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == ']') { _i++; return a; }
                    throw Error("Ожидалось ',' или ']'");
                }
            }

            private string ReadString()
            {
                _i++; // "
                var sb = new StringBuilder();
                while (true)
                {
                    if (End) throw Error("Незакрытая строка");
                    var c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c < ' ') throw Error("Управляющий символ внутри строки");
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (End) throw Error("Незаконченная escape-последовательность");
                    var e = _s[_i++];
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
                            if (_i + 4 > _s.Length ||
                                !int.TryParse(_s.Substring(_i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                                throw Error("Неверная последовательность \\u");
                            sb.Append((char)code);
                            _i += 4;
                            break;
                        default:
                            throw Error($"Неверная escape-последовательность '\\{e}'");
                    }
                }
            }

            private object ReadNumber()
            {
                int start = _i;
                bool fractional = false;
                while (_i < _s.Length)
                {
                    var c = _s[_i];
                    if ((c >= '0' && c <= '9') || c == '-' || c == '+') { _i++; continue; }
                    if (c == '.' || c == 'e' || c == 'E') { fractional = true; _i++; continue; }
                    break;
                }
                var text = _s.Substring(start, _i - start);
                if (!fractional && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                    return l;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return d;
                throw Error($"Неверное число '{text}'");
            }
        }
    }

    /// <summary>
    /// Атомарная запись маленьких файлов SDK: текст пишется во временный <c>*.tmp</c> рядом и
    /// заменяет основной файл одной операцией (<see cref="File.Replace(string, string, string)"/>,
    /// где её нет — «удалить + переименовать»). Сбой посреди записи оставляет либо старый файл,
    /// либо полный <c>*.tmp</c>, который подхватит <see cref="TryRead"/>.
    /// </summary>
    internal static class VhrAtomicFile
    {
        internal const string TmpSuffix = ".tmp";

        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        /// <summary>
        /// Вызывается после каждой записи/удаления (WebGL: отложенная синхронизация IndexedDB —
        /// подставляет <c>VhrWebGlStorage</c>). <c>null</c> — ничего не делать.
        /// </summary>
        internal static Action Changed;

        /// <summary>Записывает файл атомарно (папки создаются). Ошибки ввода-вывода — исключением.</summary>
        internal static void Write(string path, string content)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = path + TmpSuffix;
            File.WriteAllText(tmp, content ?? string.Empty, Utf8);
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, null);
                }
                catch (Exception e) when (e is PlatformNotSupportedException || e is NotSupportedException ||
                                          e is IOException || e is UnauthorizedAccessException)
                {
                    // Платформа/ФС без ReplaceFile. Между удалением и переименованием цел полный
                    // .tmp — TryRead возьмёт его, если процесс оборвётся именно здесь.
                    File.Delete(path);
                    File.Move(tmp, path);
                }
            }
            else
            {
                File.Move(tmp, path);
            }
            RaiseChanged();
        }

        /// <summary>
        /// Читает файл; если основного нет, а есть <c>*.tmp</c> (запись оборвалась между шагами) —
        /// читает его. <c>false</c> — файла нет. Ошибки ввода-вывода — исключением.
        /// </summary>
        internal static bool TryRead(string path, out string content)
        {
            if (File.Exists(path))
            {
                content = File.ReadAllText(path, Utf8);
                return true;
            }
            var tmp = path + TmpSuffix;
            if (File.Exists(tmp))
            {
                content = File.ReadAllText(tmp, Utf8);
                return true;
            }
            content = null;
            return false;
        }

        /// <summary>Удаляет файл и его <c>*.tmp</c> (нет — не ошибка).</summary>
        internal static void Delete(string path)
        {
            bool any = false;
            if (File.Exists(path)) { File.Delete(path); any = true; }
            var tmp = path + TmpSuffix;
            if (File.Exists(tmp)) { File.Delete(tmp); any = true; }
            if (any) RaiseChanged();
        }

        /// <summary>Переносит файл в <paramref name="backupPath"/> (старая копия затирается); нет файла — ничего.</summary>
        internal static void MoveAside(string path, string backupPath)
        {
            if (!File.Exists(path)) return;
            if (File.Exists(backupPath)) File.Delete(backupPath);
            File.Move(path, backupPath);
            var tmp = path + TmpSuffix;
            if (File.Exists(tmp)) File.Delete(tmp);
            RaiseChanged();
        }

        private static void RaiseChanged()
        {
            try { Changed?.Invoke(); }
            catch
            {
                // синхронизация ФС — «лучшее усилие», запись уже сделана
            }
        }

        /// <summary>
        /// Безопасный сегмент пути: <c>[A-Za-z0-9_.-]{1,64}</c> (кроме <c>.</c>/<c>..</c>) — как есть,
        /// иначе <c>h</c> + 16 hex стабильного хэша FNV-1a (64 бит).
        /// </summary>
        internal static string SafeSegment(string s)
        {
            s ??= string.Empty;
            bool ok = s.Length > 0 && s.Length <= 64 && s != "." && s != "..";
            for (int i = 0; ok && i < s.Length; i++)
            {
                var c = s[i];
                ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                     c == '_' || c == '-' || c == '.';
            }
            if (ok) return s;

            ulong h = 14695981039346656037UL;
            foreach (var c in s)
            {
                h ^= c;
                h *= 1099511628211UL;
            }
            return "h" + h.ToString("x16", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Локальные файлы облачных сохранений (SDK 1.12.0+, «сначала локально»):
    /// <c>{root}/saves/{gameId}/{userId}/slot_{slot}.pending.json</c> — данные слота, ещё не
    /// подтверждённые сервером, и <c>slot_{slot}.cache.json</c> — последняя известная серверная
    /// версия (для загрузки без связи). Префикс <c>slot_</c> — чтобы имена вроде <c>con</c> не
    /// упирались в зарезервированные имена Windows.
    /// </summary>
    internal sealed class VhrSavesDisk
    {
        private const string Prefix = "slot_";
        internal const string PendingSuffix = ".pending.json";
        internal const string CacheSuffix = ".cache.json";

        private readonly string _root;

        /// <summary>Неотправленные данные слота.</summary>
        internal sealed class PendingRecord
        {
            public string Slot;
            public string Data;
            /// <summary>Версия, поверх которой писать: <c>null</c> — перезаписать как есть, 0 — только создать, N.</summary>
            public long? BaseVersion;
            public long Seq;
            /// <summary>Когда записано (Unix, мс UTC).</summary>
            public long SavedAt;
        }

        /// <summary>Последняя известная серверная версия слота.</summary>
        internal sealed class CacheRecord
        {
            public string Slot;
            public string Data;
            public long Version;
            public string UpdatedAt;
        }

        /// <param name="sdkRoot">Корень локальных файлов SDK (<c>persistentDataPath/vhr-sdk</c>).</param>
        /// <param name="gameId">Id игры — у каждой игры свои файлы.</param>
        internal VhrSavesDisk(string sdkRoot, string gameId)
        {
            _root = Path.Combine(sdkRoot, "saves",
                VhrAtomicFile.SafeSegment((gameId ?? string.Empty).Trim().ToLowerInvariant()));
        }

        internal string UserDir(string userId) => Path.Combine(_root, VhrAtomicFile.SafeSegment(userId));

        internal string PendingPath(string userId, string slot) => Path.Combine(UserDir(userId), Prefix + slot + PendingSuffix);

        internal string CachePath(string userId, string slot) => Path.Combine(UserDir(userId), Prefix + slot + CacheSuffix);

        // ------------------------------------------------------------ pending

        internal void WritePending(string userId, PendingRecord r)
            => VhrAtomicFile.Write(PendingPath(userId, r.Slot), SerializePending(r));

        internal void DeletePending(string userId, string slot) => VhrAtomicFile.Delete(PendingPath(userId, slot));

        /// <summary>
        /// Все неотправленные слоты игрока. Битый файл пропускается (и удаляется) с сообщением в
        /// <paramref name="warn"/>; имя слота в файле должно совпадать с именем файла.
        /// </summary>
        internal List<PendingRecord> ReadAllPending(string userId, Action<string> warn)
        {
            var list = new List<PendingRecord>();
            var dir = UserDir(userId);
            if (!Directory.Exists(dir)) return list;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(file);
                // Основной файл или осиротевший .tmp (запись оборвалась между шагами).
                if (name.EndsWith(PendingSuffix + VhrAtomicFile.TmpSuffix, StringComparison.Ordinal))
                    name = name.Substring(0, name.Length - VhrAtomicFile.TmpSuffix.Length);
                if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(PendingSuffix, StringComparison.Ordinal))
                    continue;
                var slot = name.Substring(Prefix.Length, name.Length - Prefix.Length - PendingSuffix.Length);
                if (slot.Length == 0 || !seen.Add(slot)) continue;

                var path = PendingPath(userId, slot);
                try
                {
                    if (VhrAtomicFile.TryRead(path, out var json) && TryParsePending(json, out var r) && r.Slot == slot)
                    {
                        list.Add(r);
                        continue;
                    }
                }
                catch (Exception e)
                {
                    warn?.Invoke($"не удалось прочитать неотправленное сохранение слота '{slot}': {e.Message}");
                    continue;
                }
                warn?.Invoke($"файл неотправленного сохранения слота '{slot}' повреждён — пропускаю его.");
                try { VhrAtomicFile.Delete(path); } catch { /* не мешаем остальным слотам */ }
            }
            return list;
        }

        internal static string SerializePending(PendingRecord r)
        {
            var sb = new StringBuilder((r.Data?.Length ?? 0) + 128);
            bool first = true;
            sb.Append('{');
            VhrJsonLite.Field(sb, ref first, "v", 1L);
            VhrJsonLite.Field(sb, ref first, "slot", r.Slot);
            VhrJsonLite.Field(sb, ref first, "data", r.Data ?? string.Empty);
            VhrJsonLite.Field(sb, ref first, "baseVersion", r.BaseVersion);
            VhrJsonLite.Field(sb, ref first, "seq", r.Seq);
            VhrJsonLite.Field(sb, ref first, "savedAt", r.SavedAt);
            sb.Append('}');
            return sb.ToString();
        }

        internal static bool TryParsePending(string json, out PendingRecord r)
        {
            r = null;
            try
            {
                if (!(VhrJsonLite.Parse(json) is Dictionary<string, object> o)) return false;
                var slot = VhrJsonLite.Str(o, "slot");
                var data = VhrJsonLite.Str(o, "data");
                if (string.IsNullOrEmpty(slot) || data == null) return false;
                r = new PendingRecord
                {
                    Slot = slot,
                    Data = data,
                    BaseVersion = VhrJsonLite.LongOrNull(o, "baseVersion"),
                    Seq = VhrJsonLite.Long(o, "seq", 0),
                    SavedAt = VhrJsonLite.Long(o, "savedAt", 0)
                };
                if (r.BaseVersion.HasValue && r.BaseVersion.Value < 0) r.BaseVersion = null;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        // ------------------------------------------------------------ cache

        internal void WriteCache(string userId, CacheRecord r)
            => VhrAtomicFile.Write(CachePath(userId, r.Slot), SerializeCache(r));

        internal void DeleteCache(string userId, string slot) => VhrAtomicFile.Delete(CachePath(userId, slot));

        /// <summary>Кэш слота. <c>false</c> — нет файла или он битый (битый удаляется).</summary>
        internal bool TryReadCache(string userId, string slot, out CacheRecord r)
        {
            r = null;
            var path = CachePath(userId, slot);
            if (!VhrAtomicFile.TryRead(path, out var json)) return false;
            if (TryParseCache(json, out r) && r.Slot == slot) return true;
            r = null;
            try { VhrAtomicFile.Delete(path); } catch { /* битый кэш просто не используем */ }
            return false;
        }

        internal static string SerializeCache(CacheRecord r)
        {
            var sb = new StringBuilder((r.Data?.Length ?? 0) + 128);
            bool first = true;
            sb.Append('{');
            VhrJsonLite.Field(sb, ref first, "v", 1L);
            VhrJsonLite.Field(sb, ref first, "slot", r.Slot);
            VhrJsonLite.Field(sb, ref first, "data", r.Data ?? string.Empty);
            VhrJsonLite.Field(sb, ref first, "version", r.Version);
            VhrJsonLite.Field(sb, ref first, "updatedAt", r.UpdatedAt);
            sb.Append('}');
            return sb.ToString();
        }

        internal static bool TryParseCache(string json, out CacheRecord r)
        {
            r = null;
            try
            {
                if (!(VhrJsonLite.Parse(json) is Dictionary<string, object> o)) return false;
                var slot = VhrJsonLite.Str(o, "slot");
                var data = VhrJsonLite.Str(o, "data");
                var version = VhrJsonLite.Long(o, "version", 0);
                if (string.IsNullOrEmpty(slot) || data == null || version <= 0) return false;
                r = new CacheRecord { Slot = slot, Data = data, Version = version, UpdatedAt = VhrJsonLite.Str(o, "updatedAt") };
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
