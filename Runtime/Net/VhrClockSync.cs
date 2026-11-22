using System;
using System.Diagnostics;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Оценка RTT, джиттера и смещения часов сервера по парам PING/PONG (NTP-подобно).
    /// Смещение берётся из замера с наименьшим RTT среди последних <c>N</c> — у него
    /// наименьшая ошибка (очереди почти пустые). RTT сглаживается EWMA (1/8), джиттер —
    /// как в RFC 3550 (1/16 от |ΔRTT|). Возвращаемое серверное время не идёт назад.
    /// Потокобезопасен (нативный сокет отдаёт PONG из фонового потока). Чистый C#.
    /// </summary>
    internal sealed class VhrClockSync
    {
        private const int Window = 8;

        private readonly object _sync = new object();
        private readonly long[] _rtt = new long[Window];
        private readonly long[] _offset = new long[Window];
        private int _count;
        private int _next;

        private bool _hasOffset;
        private long _offsetUs;     // serverUs - localUs
        private double _srttUs;
        private double _jitterUs;
        private long _lastRttUs;
        private long _lastServerOut = long.MinValue;

        /// <summary>Локальные монотонные микросекунды (Stopwatch; работает и в WebGL).</summary>
        public static long LocalNowUs()
        {
            long ts = Stopwatch.GetTimestamp();
            return (long)(ts * (1_000_000.0 / Stopwatch.Frequency));
        }

        public bool HasSamples { get { lock (_sync) return _count > 0; } }
        public bool HasOffset { get { lock (_sync) return _hasOffset; } }
        public double RttMs { get { lock (_sync) return _srttUs / 1000.0; } }
        public double LastRttMs { get { lock (_sync) return _lastRttUs / 1000.0; } }
        public double JitterMs { get { lock (_sync) return _jitterUs / 1000.0; } }
        public long OffsetUs { get { lock (_sync) return _offsetUs; } }

        /// <summary>
        /// Грубая начальная оценка по WELCOME (одностороннюю задержку не знаем).
        /// Игнорируется, если уже есть замеры PING/PONG.
        /// </summary>
        public void SetInitial(long serverUs, long localRecvUs)
        {
            lock (_sync)
            {
                if (_count > 0) return;
                _offsetUs = serverUs - localRecvUs;
                _hasOffset = true;
            }
        }

        /// <summary>Добавляет замер: <paramref name="t0"/> — отправка PING, <paramref name="t1"/> — приём PONG (локальные µs).</summary>
        /// <returns>RTT замера в микросекундах (или -1, если замер некорректен).</returns>
        public long AddSample(long t0, long t1, long serverUs)
        {
            long rtt = t1 - t0;
            if (rtt < 0 || rtt > 60_000_000) return -1; // мусор/переполнение
            long offset = serverUs - (t0 + rtt / 2);
            lock (_sync)
            {
                if (_count == 0) { _srttUs = rtt; _jitterUs = 0; }
                else
                {
                    double d = Math.Abs(rtt - _lastRttUs);
                    _jitterUs += (d - _jitterUs) / 16.0;
                    _srttUs += (rtt - _srttUs) / 8.0;
                }
                _lastRttUs = rtt;
                _rtt[_next] = rtt;
                _offset[_next] = offset;
                _next = (_next + 1) % Window;
                if (_count < Window) _count++;

                int best = 0;
                for (int i = 1; i < _count; i++) if (_rtt[i] < _rtt[best]) best = i;
                _offsetUs = _offset[best];
                _hasOffset = true;
            }
            return rtt;
        }

        /// <summary>Оценка серверного времени (µs, Unix) сейчас; монотонна. 0 — оценки нет.</summary>
        public long ServerNowUs()
        {
            long local = LocalNowUs();
            lock (_sync)
            {
                if (!_hasOffset) return 0;
                long v = local + _offsetUs;
                if (v < _lastServerOut) v = _lastServerOut; // не назад при смене лучшего замера
                _lastServerOut = v;
                return v;
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                _count = 0; _next = 0; _hasOffset = false; _offsetUs = 0;
                _srttUs = 0; _jitterUs = 0; _lastRttUs = 0; _lastServerOut = long.MinValue;
            }
        }
    }
}
