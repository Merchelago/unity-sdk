using System;
using System.Collections.Generic;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Копит исходящие кадры одного транспорта за кадр Unity и отдаёт их пачками
    /// (<c>0x06 BATCH</c>). Порядок кадров сохраняется. Один кадр уходит как есть,
    /// кадры длиннее <see cref="VhrRelayProtocol.MaxBatchedFrame"/> или длиннее лимита
    /// сообщения — отдельно. Чистый C#, не потокобезопасен (вызывать с одного потока).
    /// </summary>
    internal sealed class VhrSendBatcher
    {
        private readonly List<byte[]> _frames = new List<byte[]>();
        private readonly int _maxMessageBytes;

        /// <param name="maxMessageBytes">Предел размера одного исходящего сообщения
        /// (для DataChannel ~1150 байт, чтобы влезть в один UDP-пакет).</param>
        public VhrSendBatcher(int maxMessageBytes)
        {
            _maxMessageBytes = Math.Max(64, maxMessageBytes);
        }

        public int Count => _frames.Count;

        public void Add(byte[] frame)
        {
            if (frame != null && frame.Length > 0) _frames.Add(frame);
        }

        /// <summary>Отдаёт накопленное в <paramref name="send"/> и очищает буфер.</summary>
        public void Flush(Action<byte[]> send)
        {
            int n = _frames.Count;
            if (n == 0) return;
            if (n == 1)
            {
                var only = _frames[0];
                _frames.Clear();
                send(only);
                return;
            }

            int i = 0;
            while (i < n)
            {
                var f = _frames[i];
                // Большой кадр — отдельно (u16-длина или не влезет в сообщение с заголовком).
                if (f.Length > VhrRelayProtocol.MaxBatchedFrame || 1 + 2 + f.Length > _maxMessageBytes)
                {
                    send(f);
                    i++;
                    continue;
                }
                // Набираем пачку, пока влезает.
                int start = i, size = 1;
                while (i < n)
                {
                    var g = _frames[i];
                    if (g.Length > VhrRelayProtocol.MaxBatchedFrame) break;
                    if (size + 2 + g.Length > _maxMessageBytes) break;
                    size += 2 + g.Length;
                    i++;
                }
                int count = i - start;
                if (count == 1) send(_frames[start]);
                else send(VhrRelayProtocol.EncodeBatch(VhrRelayProtocol.CBatch, _frames, start, count));
            }
            _frames.Clear();
        }

        public void Clear() => _frames.Clear();
    }
}
