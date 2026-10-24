#if UNITY_EDITOR
// ВЕСЬ файл существует только в Unity Editor: в сборку игры не компилируется.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace VhrGames.Sdk
{
    /// <summary>
    /// <b>Только Unity Editor.</b> Локальное «облако» для облачных сохранений в режиме
    /// Simulation (<see cref="VhrSavesService"/>): слоты лежат файлами в
    /// <c>Library/VhrSdk/SimSaves</c> проекта — не в VCS и не в сборке — и переживают
    /// перезапуск редактора и Play.
    /// </summary>
    /// <remarks>
    /// Правила — как у сервера для песочного игрока: версии (1 при создании, +1 на запись),
    /// <c>expectedVersion</c> (<c>null</c> / 0 «только создать» / N) и конфликты
    /// (<c>serverVersion</c> = 0, если слота нет), слот ≤ 256 КБ после gzip и ≤ 1 МБ до
    /// сжатия, ≤ 10 слотов, суммарно ≤ 1024 КБ сжатого на игрока (лимит песочницы:
    /// запись, увеличивающая объём сверх него, — <c>storage_quota_exceeded</c>),
    /// 12 записей/удалений и 60 чтений в минуту (<c>rate_limited</c> + пауза).
    /// Имя слота сюда приходит уже проверенным (<see cref="VhrSavesService.TryNormalizeSlot"/>).
    /// </remarks>
    internal static class VhrEditorSaves
    {
        /// <summary>Лимит слота после сжатия, КБ (дефолт сервера <c>cloudsaves.maxSlotKb</c>).</summary>
        public const int MaxSlotKb = 256;

        /// <summary>Слотов на игрока в игре (дефолт <c>cloudsaves.maxSlotsPerPlayer</c>).</summary>
        public const int MaxSlots = 10;

        /// <summary>Лимит записи до сжатия, КБ (жёсткая константа сервера).</summary>
        public const int MaxRawKb = 1024;

        /// <summary>Лимит песочного игрока в игре по сжатому объёму, КБ (дефолт <c>cloudsaves.sandboxQuotaKb</c>).</summary>
        public const int SandboxQuotaKb = 1024;

        private const string FilePrefix = "slot_"; // "slot_con.json": без зарезервированных имён Windows
        private const string FileSuffix = ".json";

        // Время запросов за последнюю минуту (реальное время) — как партиция rate limiter'а сервера.
        private static readonly Queue<float> Writes = new Queue<float>();
        private static readonly Queue<float> Reads = new Queue<float>();

        /// <summary>Папка локальных сохранений этого проекта.</summary>
        public static string Folder
        {
            get
            {
                string root;
                try { root = Path.GetDirectoryName(Application.dataPath); }
                catch { root = null; }
                return Path.Combine(string.IsNullOrEmpty(root) ? "." : root, "Library", "VhrSdk", "SimSaves");
            }
        }

        /// <summary>Лимиты симуляции в форме ответа сервера.</summary>
        public static VhrSaveLimits Limits()
            => new VhrSaveLimits { maxSlotKb = MaxSlotKb, maxSlots = MaxSlots, maxRawKb = MaxRawKb };

        [Serializable]
        private sealed class Record
        {
            public string slot;
            public string data;
            public long version;
            public int rawSize;
            public int storedSize;
            public string createdAt;
            public string updatedAt;
        }

        // ------------------------------------------------------------ API

        /// <summary>Как <c>GET /api/saves/{slot}</c>.</summary>
        public static VhrSaveLoadResult Get(string slot)
        {
            if (!TakeRate(Reads, VhrSavesService.ReadLimitPerMinute, out var retry))
            {
                return VhrSaveLoadResult.Fail(slot, VhrSaveError.RateLimited, "rate_limited",
                    $"Симуляция: больше {VhrSavesService.ReadLimitPerMinute} чтений в минуту — как на сервере. Повтор через {retry} с.");
            }

            var rec = Read(slot);
            if (rec == null)
                return VhrSaveLoadResult.Fail(slot, VhrSaveError.NotFound, "save_not_found", "Симуляция: сохранения в слоте нет.");

            return new VhrSaveLoadResult
            {
                Slot = slot, Found = true, Data = rec.data ?? string.Empty, Version = rec.version, UpdatedAt = rec.updatedAt
            };
        }

        /// <summary>Как <c>PUT /api/saves/{slot}</c> с телом <c>{ data, expectedVersion }</c>.</summary>
        public static VhrSaveWriteResult Put(string slot, string data, long? expectedVersion)
        {
            if (!TakeRate(Writes, VhrSavesService.WriteLimitPerMinute, out var retry))
            {
                return new VhrSaveWriteResult
                {
                    Slot = slot, Error = VhrSaveError.RateLimited, ErrorCode = "rate_limited", RetryAfterSeconds = retry,
                    Message = $"Симуляция: больше {VhrSavesService.WriteLimitPerMinute} записей в минуту — как на сервере. " +
                              $"Повтор через {retry} с. Для частых сохранений — QueueSave."
                };
            }

            data ??= string.Empty;
            var raw = Encoding.UTF8.GetByteCount(data);
            if (raw > MaxRawKb * 1024)
            {
                return VhrSaveWriteResult.Fail(slot, VhrSaveError.SlotTooLarge, "slot_too_large",
                    $"Симуляция: {raw} Б до сжатия — больше {MaxRawKb} КБ.");
            }
            var stored = GzipSize(data);
            if (stored > MaxSlotKb * 1024)
            {
                return VhrSaveWriteResult.Fail(slot, VhrSaveError.SlotTooLarge, "slot_too_large",
                    $"Симуляция: слот {stored / 1024f:0.#} КБ после сжатия — больше {MaxSlotKb} КБ.");
            }

            var all = ReadAll();
            all.TryGetValue(slot, out var cur);
            long current = cur?.version ?? 0;
            if (expectedVersion.HasValue && expectedVersion.Value != current)
            {
                return new VhrSaveWriteResult
                {
                    Slot = slot, Error = VhrSaveError.Conflict, ErrorCode = "version_conflict",
                    ServerVersion = current, ServerUpdatedAt = cur?.updatedAt,
                    Message = $"Симуляция: ожидалась версия {expectedVersion.Value}, сейчас {current}" +
                              (current == 0 ? " (слота нет)." : ".")
                };
            }

            if (cur == null && all.Count >= MaxSlots)
            {
                return VhrSaveWriteResult.Fail(slot, VhrSaveError.SlotsLimit, "slots_limit",
                    $"Симуляция: уже {MaxSlots} слотов — удалите ненужный или пишите в существующий.");
            }

            long used = 0;
            foreach (var r in all.Values) used += r.storedSize;
            long delta = stored - (cur?.storedSize ?? 0);
            if (delta > 0 && used + delta > SandboxQuotaKb * 1024L)
            {
                return VhrSaveWriteResult.Fail(slot, VhrSaveError.QuotaExceeded, "storage_quota_exceeded",
                    $"Симуляция: лимит песочного игрока — {SandboxQuotaKb} КБ сжатых сохранений на игру. " +
                    "У реальных игроков действует квота игры: 50 МБ бесплатно + пакеты в кабинете разработчика.");
            }

            var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            var rec = new Record
            {
                slot = slot, data = data, version = current + 1, rawSize = raw, storedSize = stored,
                createdAt = cur?.createdAt ?? now, updatedAt = now
            };
            try
            {
                Write(rec);
            }
            catch (Exception e)
            {
                return VhrSaveWriteResult.Fail(slot, VhrSaveError.Unknown, "exception",
                    "Симуляция: не удалось записать файл сохранения: " + e.Message);
            }

            return new VhrSaveWriteResult
            {
                Slot = slot, Version = rec.version, UpdatedAt = now, RawSize = raw, StoredSize = stored
            };
        }

        /// <summary>Как <c>DELETE /api/saves/{slot}</c> (идемпотентно).</summary>
        public static VhrSaveWriteResult Delete(string slot)
        {
            if (!TakeRate(Writes, VhrSavesService.WriteLimitPerMinute, out var retry))
            {
                return new VhrSaveWriteResult
                {
                    Slot = slot, Error = VhrSaveError.RateLimited, ErrorCode = "rate_limited", RetryAfterSeconds = retry,
                    Message = $"Симуляция: больше {VhrSavesService.WriteLimitPerMinute} записей/удалений в минуту. Повтор через {retry} с."
                };
            }
            try
            {
                var path = PathOf(slot);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                return VhrSaveWriteResult.Fail(slot, VhrSaveError.Unknown, "exception",
                    "Симуляция: не удалось удалить файл сохранения: " + e.Message);
            }
            return new VhrSaveWriteResult { Slot = slot };
        }

        /// <summary>Как <c>GET /api/saves</c>. <c>null</c> — сработал лимит чтений (пауза — в <paramref name="retryAfter"/>).</summary>
        public static VhrSaveSlotInfo[] List(out int retryAfter)
        {
            if (!TakeRate(Reads, VhrSavesService.ReadLimitPerMinute, out retryAfter)) return null;
            var all = ReadAll();
            var keys = new List<string>(all.Keys);
            keys.Sort(StringComparer.Ordinal);
            var list = new VhrSaveSlotInfo[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                var r = all[keys[i]];
                list[i] = new VhrSaveSlotInfo { slot = r.slot, version = r.version, rawSize = r.rawSize, updatedAt = r.updatedAt };
            }
            return list;
        }

        // ---------------------------------------------------- для окна VHR

        /// <summary>Сколько слотов и сжатых байт лежит локально (без учёта лимитов запросов).</summary>
        public static void Stats(out int slots, out long storedBytes)
        {
            slots = 0;
            storedBytes = 0;
            foreach (var r in ReadAll().Values)
            {
                slots++;
                storedBytes += r.storedSize;
            }
        }

        /// <summary>Удалить все локальные сохранения симуляции этого проекта.</summary>
        public static void Clear()
        {
            var dir = Folder;
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, FilePrefix + "*" + FileSuffix))
            {
                try { File.Delete(f); }
                catch (Exception e) { Debug.LogWarning("[VHR SDK] Saves (Simulation): не удалось удалить " + f + ": " + e.Message); }
            }
            Writes.Clear();
            Reads.Clear();
        }

        // ---------------------------------------------------------- файлы

        private static string PathOf(string slot) => Path.Combine(Folder, FilePrefix + slot + FileSuffix);

        private static Record Read(string slot)
        {
            try
            {
                var path = PathOf(slot);
                if (!File.Exists(path)) return null;
                var rec = JsonUtility.FromJson<Record>(File.ReadAllText(path, Encoding.UTF8));
                return rec != null && rec.version > 0 ? rec : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VHR SDK] Saves (Simulation): файл слота '{slot}' не читается ({e.Message}) — считаю, что слота нет.");
                return null;
            }
        }

        private static Dictionary<string, Record> ReadAll()
        {
            var map = new Dictionary<string, Record>(StringComparer.Ordinal);
            var dir = Folder;
            if (!Directory.Exists(dir)) return map;
            foreach (var f in Directory.GetFiles(dir, FilePrefix + "*" + FileSuffix))
            {
                var name = Path.GetFileName(f);
                var slot = name.Substring(FilePrefix.Length, name.Length - FilePrefix.Length - FileSuffix.Length);
                var rec = Read(slot);
                if (rec != null) map[slot] = rec;
            }
            return map;
        }

        private static void Write(Record rec)
        {
            Directory.CreateDirectory(Folder);
            var path = PathOf(rec.slot);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonUtility.ToJson(rec), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // Сжатый размер, как его считает сервер: gzip(UTF-8 строки).
        private static int GzipSize(string data)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(data);
                using (var ms = new MemoryStream())
                {
                    using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
                        gz.Write(bytes, 0, bytes.Length);
                    return (int)ms.Length;
                }
            }
            catch
            {
                return Encoding.UTF8.GetByteCount(data);
            }
        }

        private static bool TakeRate(Queue<float> log, int limit, out int retryAfter)
        {
            var now = Time.realtimeSinceStartup;
            while (log.Count > 0 && now - log.Peek() >= 60f) log.Dequeue();
            if (log.Count >= limit)
            {
                retryAfter = Mathf.Max(1, Mathf.CeilToInt(60f - (now - log.Peek())));
                return false;
            }
            log.Enqueue(now);
            retryAfter = 0;
            return true;
        }
    }
}
#endif
