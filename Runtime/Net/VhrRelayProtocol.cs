using System;
using System.Collections.Generic;
using System.Text;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Кадры протокола релея v2 (см. <c>back/docs/relay-protocol.md</c>): типы, флаги
    /// возможностей и сборка/разбор кадров. Чистый C# без UnityEngine — покрывается
    /// тестами вне Unity. Все целые — little-endian.
    /// </summary>
    internal static class VhrRelayProtocol
    {
        // ---- v1 ----
        public const byte CJoin = 0x01;
        public const byte CData = 0x02;
        public const byte CTo = 0x04;
        public const byte SJoined = 0x81;
        public const byte SData = 0x83;
        public const byte SPeerJoined = 0x84;
        public const byte SPeerLeft = 0x85;
        public const byte Signal = 0x10;
        public const byte Control = 0x20;

        // ---- v2 ----
        public const byte CDataReliable = 0x03;
        public const byte CToReliable = 0x05;
        public const byte CBatch = 0x06;
        public const byte CHello = 0x30;
        public const byte CPing = 0x31;
        public const byte SWelcome = 0xB0;
        public const byte SPong = 0xB1;
        public const byte SHost = 0xB4;
        public const byte SBatch = 0xC6;

        public const byte Version = 2;

        // Биты caps (HELLO/WELCOME).
        public const uint CapPing = 0x01;
        public const uint CapBatch = 0x02;
        public const uint CapHost = 0x04;
        public const uint CapResume = 0x08;
        public const uint CapReliable = 0x10;
        public const uint CapsAll = CapPing | CapBatch | CapHost | CapResume | CapReliable;

        /// <summary>Бит0 flags в WELCOME — сессия возобновлена.</summary>
        public const byte WelcomeFlagResumed = 0x01;

        /// <summary>Наибольшая длина вложенного кадра в BATCH (u16).</summary>
        public const int MaxBatchedFrame = 0xFFFF;

        // ---- сборка ----

        public static byte[] BuildHello(uint caps, byte[] resumeToken)
        {
            int tokLen = resumeToken == null ? 0 : Math.Min(resumeToken.Length, 255);
            var f = new byte[1 + 1 + 4 + 1 + tokLen];
            f[0] = CHello;
            f[1] = Version;
            WriteU32(f, 2, caps);
            f[6] = (byte)tokLen;
            if (tokLen > 0) Buffer.BlockCopy(resumeToken, 0, f, 7, tokLen);
            return f;
        }

        public static byte[] BuildPing(uint seq, long clientTimeUs)
        {
            var f = new byte[1 + 4 + 8];
            f[0] = CPing;
            WriteU32(f, 1, seq);
            WriteI64(f, 5, clientTimeUs);
            return f;
        }

        public static byte[] BuildJoin(string room)
        {
            var roomBytes = Encoding.UTF8.GetBytes(room ?? string.Empty);
            var f = new byte[3 + roomBytes.Length];
            f[0] = CJoin;
            f[1] = (byte)(roomBytes.Length & 0xFF);
            f[2] = (byte)((roomBytes.Length >> 8) & 0xFF);
            Buffer.BlockCopy(roomBytes, 0, f, 3, roomBytes.Length);
            return f;
        }

        /// <summary>Кадр данных: всем (<paramref name="to"/>=false) или одному пиру.</summary>
        public static byte[] BuildData(byte type, bool to, int peerId, byte[] data)
        {
            int head = to ? 5 : 1;
            var f = new byte[head + data.Length];
            f[0] = type;
            if (to) WriteI32(f, 1, peerId);
            Buffer.BlockCopy(data, 0, f, head, data.Length);
            return f;
        }

        /// <summary>
        /// Склеивает <paramref name="count"/> кадров начиная с <paramref name="start"/> в один
        /// BATCH-кадр <c>[type]([u16 len][frame])*</c>.
        /// </summary>
        public static byte[] EncodeBatch(byte type, IList<byte[]> frames, int start, int count)
        {
            int size = 1;
            for (int i = 0; i < count; i++) size += 2 + frames[start + i].Length;
            var f = new byte[size];
            f[0] = type;
            int off = 1;
            for (int i = 0; i < count; i++)
            {
                var fr = frames[start + i];
                f[off] = (byte)(fr.Length & 0xFF);
                f[off + 1] = (byte)((fr.Length >> 8) & 0xFF);
                Buffer.BlockCopy(fr, 0, f, off + 2, fr.Length);
                off += 2 + fr.Length;
            }
            return f;
        }

        /// <summary>
        /// Разбирает BATCH-кадр (первый байт — тип) на вложенные кадры. Битый хвост
        /// отбрасывается, уже разобранное остаётся.
        /// </summary>
        public static List<byte[]> DecodeBatch(byte[] msg)
        {
            var list = new List<byte[]>();
            int off = 1;
            while (off + 2 <= msg.Length)
            {
                int len = msg[off] | (msg[off + 1] << 8);
                off += 2;
                if (len <= 0 || off + len > msg.Length) break;
                var fr = new byte[len];
                Buffer.BlockCopy(msg, off, fr, 0, len);
                list.Add(fr);
                off += len;
            }
            return list;
        }

        // ---- разбор ----

        /// <summary>WELCOME (<c>0xB0</c>).</summary>
        public struct Welcome
        {
            public byte Version;
            public uint Caps;
            public long ServerTimeUs;
            public byte[] ResumeToken;
            public bool Resumed;
            public string Region;
        }

        public static bool TryParseWelcome(byte[] m, out Welcome w)
        {
            w = default;
            // [0xB0][u8 ver][u32 caps][i64 time][u8 tokLen][token][u8 flags][u8 regionLen][region]
            if (m == null || m.Length < 1 + 1 + 4 + 8 + 1) return false;
            w.Version = m[1];
            w.Caps = ReadU32(m, 2);
            w.ServerTimeUs = ReadI64(m, 6);
            int off = 14;
            int tokLen = m[off++];
            if (off + tokLen > m.Length) return false;
            if (tokLen > 0)
            {
                w.ResumeToken = new byte[tokLen];
                Buffer.BlockCopy(m, off, w.ResumeToken, 0, tokLen);
            }
            off += tokLen;
            // flags и region — необязательный хвост (терпим к короткому кадру).
            if (off < m.Length) w.Resumed = (m[off++] & WelcomeFlagResumed) != 0;
            if (off < m.Length)
            {
                int rl = m[off++];
                if (off + rl <= m.Length && rl > 0) w.Region = Encoding.UTF8.GetString(m, off, rl);
            }
            w.Region ??= string.Empty;
            return true;
        }

        public static bool TryParsePong(byte[] m, out uint seq, out long clientTimeUs, out long serverTimeUs)
        {
            seq = 0; clientTimeUs = 0; serverTimeUs = 0;
            if (m == null || m.Length < 1 + 4 + 8 + 8) return false;
            seq = ReadU32(m, 1);
            clientTimeUs = ReadI64(m, 5);
            serverTimeUs = ReadI64(m, 13);
            return true;
        }

        // ---- LE ----

        public static void WriteI32(byte[] b, int o, int v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }

        public static void WriteU32(byte[] b, int o, uint v) => WriteI32(b, o, unchecked((int)v));

        public static void WriteI64(byte[] b, int o, long v)
        {
            for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (8 * i));
        }

        public static int ReadI32(byte[] b, int o) =>
            b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);

        public static uint ReadU32(byte[] b, int o) => unchecked((uint)ReadI32(b, o));

        public static long ReadI64(byte[] b, int o)
        {
            long v = 0;
            for (int i = 7; i >= 0; i--) v = (v << 8) | b[o + i];
            return v;
        }
    }
}
