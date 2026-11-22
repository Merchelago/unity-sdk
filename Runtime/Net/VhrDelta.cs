using System;
using System.IO;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Дельта-сжатие снапшотов: новое состояние кодируется относительно базового
    /// (последнего, которое получатель точно знает) — XOR побайтно, а нулевые
    /// (не изменившиеся) участки сворачиваются в длину серии. Неизменные поля
    /// снапшота почти ничего не стоят.
    /// </summary>
    /// <remarks>
    /// Формат: <c>[varint длина нового]</c>, затем пары
    /// <c>[varint серия нулей][varint длина литерала][литерал XOR-байтов]</c> до конца.
    /// Разная длина базы и нового допустима (байты за концом базы считаются нулями).
    /// Чистый C#, без аллокаций сверх результата; потокобезопасен (нет состояния).
    /// <para>
    /// Базу выбирает игра: обычно — последний снапшот, который получатель
    /// подтвердил (надёжным каналом), или ключевой кадр раз в N тиков. Если
    /// получатель не знает базу — шлите полный снапшот (<c>baseline = null</c>).
    /// </para>
    /// <code>
    /// byte[] delta = VhrDelta.Encode(lastAcked, current);   // у отправителя
    /// byte[] state = VhrDelta.Decode(lastAcked, delta);     // у получателя
    /// </code>
    /// </remarks>
    public static class VhrDelta
    {
        /// <summary>Кодирует <paramref name="current"/> относительно <paramref name="baseline"/> (может быть <c>null</c>).</summary>
        public static byte[] Encode(byte[] baseline, byte[] current)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            baseline ??= Array.Empty<byte>();
            int n = current.Length;
            using var ms = new MemoryStream(Math.Min(n + 8, 64 + n / 4));
            WriteVarint(ms, (uint)n);

            int i = 0;
            while (i < n)
            {
                // Серия нулей XOR (байт не изменился).
                int z = i;
                while (z < n && X(baseline, current, z) == 0) z++;
                int zeros = z - i;
                if (z >= n)
                {
                    // Хвост без изменений: декодер дополнит нулями сам.
                    break;
                }
                // Литерал: до серии из ≥2 нулей (одиночный ноль дешевле оставить в литерале).
                int l = z;
                while (l < n)
                {
                    if (X(baseline, current, l) == 0 && (l + 1 >= n || X(baseline, current, l + 1) == 0)) break;
                    l++;
                }
                WriteVarint(ms, (uint)zeros);
                WriteVarint(ms, (uint)(l - z));
                for (int k = z; k < l; k++) ms.WriteByte(X(baseline, current, k));
                i = l;
            }
            return ms.ToArray();
        }

        /// <summary>Восстанавливает новое состояние. Бросает <see cref="FormatException"/> на битых данных.</summary>
        public static byte[] Decode(byte[] baseline, byte[] delta)
        {
            if (!TryDecode(baseline, delta, out var result))
                throw new FormatException("VhrDelta: повреждённая дельта или неверная база.");
            return result;
        }

        /// <summary>Как <see cref="Decode"/>, но без исключения.</summary>
        public static bool TryDecode(byte[] baseline, byte[] delta, out byte[] result)
        {
            result = null;
            if (delta == null) return false;
            baseline ??= Array.Empty<byte>();
            int p = 0;
            if (!ReadVarint(delta, ref p, out uint len) || len > 64 * 1024 * 1024) return false;
            int n = (int)len;
            var x = new byte[n]; // XOR-поток, по умолчанию нули
            int i = 0;
            while (p < delta.Length)
            {
                if (!ReadVarint(delta, ref p, out uint zeros)) return false;
                if (!ReadVarint(delta, ref p, out uint lit)) return false;
                if (zeros > (uint)(n - i)) return false;
                i += (int)zeros;
                if (lit > (uint)(n - i) || lit > (uint)(delta.Length - p)) return false;
                Buffer.BlockCopy(delta, p, x, i, (int)lit);
                p += (int)lit;
                i += (int)lit;
            }
            for (int k = 0; k < n; k++)
                x[k] = (byte)(x[k] ^ (k < baseline.Length ? baseline[k] : 0));
            result = x;
            return true;
        }

        private static byte X(byte[] b, byte[] c, int i) => (byte)(c[i] ^ (i < b.Length ? b[i] : 0));

        private static void WriteVarint(Stream s, uint v)
        {
            while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
            s.WriteByte((byte)v);
        }

        private static bool ReadVarint(byte[] d, ref int p, out uint v)
        {
            v = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                if (p >= d.Length) return false;
                byte b = d[p++];
                v |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
            }
            return false;
        }
    }
}
