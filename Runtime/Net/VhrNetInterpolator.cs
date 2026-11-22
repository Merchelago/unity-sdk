using System;
using System.Collections.Generic;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Буфер снапшотов с интерполяцией для плавного движения чужих объектов
    /// (snapshot interpolation). Снапшоты приходят с джиттером и не по порядку
    /// (ненадёжный канал) — буфер сортирует их по времени и показывает состояние
    /// «чуть в прошлом» (<see cref="InterpolationDelay"/>), интерполируя между двумя
    /// соседними снапшотами. Задержка подстраивается под фактический интервал и
    /// разброс прихода (jitter buffer).
    /// </summary>
    /// <remarks>
    /// Время — в секундах в одной шкале для всех клиентов; удобнее всего
    /// <see cref="VhrRelay.ServerTime"/>: отправитель кладёт его в снапшот, получатель
    /// передаёт его же в <see cref="Add(double, T, double)"/> вместе со временем прихода.
    /// Чистый C#, не потокобезопасен — используйте с главного потока.
    /// <code>
    /// var interp = new VhrNetInterpolator&lt;Vector3&gt;(Vector3.LerpUnclamped);
    /// // приём: interp.Add(snap.time, snap.pos, VhrSdk.Relay.ServerTime);
    /// // Update: if (interp.TrySampleNow(VhrSdk.Relay.ServerTime, out var p)) transform.position = p;
    /// </code>
    /// </remarks>
    public sealed class VhrNetInterpolator<T>
    {
        /// <summary>
        /// Интерполяция между <paramref name="a"/> и <paramref name="b"/>. Параметр
        /// <paramref name="t"/> бывает больше 1 при экстраполяции — используйте
        /// «unclamped»-версии (<c>Vector3.LerpUnclamped</c>).
        /// </summary>
        public delegate T Lerp(T a, T b, float t);

        private struct Snap { public double Time; public T Value; }

        private readonly Lerp _lerp;
        private readonly List<Snap> _buf;
        private readonly int _capacity;

        private double _lastTime = double.NaN;
        private double _interval;     // EWMA интервала между снапшотами, с
        private double _lateAvg;      // EWMA опоздания (arrival - time), с
        private double _lateDev;      // EWMA отклонения опоздания, с
        private double _delay = -1;   // текущая (сглаженная) задержка
        private bool _hasLate;

        /// <summary>Нижняя граница задержки интерполяции, с. По умолчанию 0.03.</summary>
        public double MinDelay = 0.03;
        /// <summary>Верхняя граница задержки интерполяции, с. По умолчанию 0.5.</summary>
        public double MaxDelay = 0.5;
        /// <summary>Сколько интервалов между снапшотами держать в запасе. По умолчанию 1.</summary>
        public double IntervalMultiplier = 1.0;
        /// <summary>Сколько отклонений опоздания добавлять к задержке (джиттер). По умолчанию 2.</summary>
        public double JitterMultiplier = 2.0;
        /// <summary>Постоянный запас, с. По умолчанию 0.005.</summary>
        public double SafetyMargin = 0.005;
        /// <summary>Насколько можно «додумывать» движение за последним снапшотом, с. 0 — не экстраполировать. По умолчанию 0.1.</summary>
        public double ExtrapolationLimit = 0.1;
        /// <summary>Фиксированная задержка вместо адаптивной (если &gt; 0).</summary>
        public double FixedDelay;

        /// <param name="lerp">Функция интерполяции (например <c>Vector3.LerpUnclamped</c>).</param>
        /// <param name="capacity">Размер буфера снапшотов.</param>
        public VhrNetInterpolator(Lerp lerp, int capacity = 32)
        {
            _lerp = lerp ?? throw new ArgumentNullException(nameof(lerp));
            _capacity = Math.Max(2, capacity);
            _buf = new List<Snap>(_capacity);
        }

        /// <summary>Сколько снапшотов в буфере.</summary>
        public int Count => _buf.Count;

        /// <summary>Время самого свежего снапшота (NaN, если пусто).</summary>
        public double LatestTime => _buf.Count > 0 ? _buf[_buf.Count - 1].Time : double.NaN;

        /// <summary>Оценка интервала между снапшотами, с.</summary>
        public double SnapshotInterval => _interval;

        /// <summary>Текущая задержка интерполяции, с: рендерим состояние на столько в прошлом.</summary>
        public double InterpolationDelay
        {
            get
            {
                if (FixedDelay > 0) return FixedDelay;
                return _delay < 0 ? Clamp(Target()) : _delay;
            }
        }

        /// <summary>Добавляет снапшот без информации о времени прихода (адаптация только по интервалу).</summary>
        public void Add(double time, T value) => Add(time, value, double.NaN);

        /// <summary>
        /// Добавляет снапшот с меткой <paramref name="time"/> (время отправителя) и
        /// временем прихода <paramref name="arrivalTime"/> в той же шкале.
        /// Дубликаты и слишком старые снапшоты отбрасываются.
        /// </summary>
        public void Add(double time, T value, double arrivalTime)
        {
            if (double.IsNaN(time) || double.IsInfinity(time)) return;

            // Статистика интервала — только по снапшотам новее последнего.
            if (!double.IsNaN(_lastTime) && time > _lastTime)
            {
                double dt = time - _lastTime;
                if (dt < 2.0) _interval = _interval <= 0 ? dt : _interval + (dt - _interval) * 0.1;
            }
            if (double.IsNaN(_lastTime) || time > _lastTime) _lastTime = time;

            if (!double.IsNaN(arrivalTime))
            {
                double late = arrivalTime - time;
                if (!_hasLate) { _lateAvg = late; _lateDev = 0; _hasLate = true; }
                else
                {
                    _lateDev += (Math.Abs(late - _lateAvg) - _lateDev) * 0.1;
                    _lateAvg += (late - _lateAvg) * 0.1;
                }
            }

            double target = Clamp(Target());
            _delay = _delay < 0 ? target : _delay + (target - _delay) * 0.1;

            // Вставка по порядку времени.
            int idx = _buf.Count;
            while (idx > 0 && _buf[idx - 1].Time > time) idx--;
            if (idx > 0 && _buf[idx - 1].Time == time) return; // дубликат
            if (_buf.Count >= _capacity)
            {
                if (idx == 0) return; // старее всего буфера — не нужен
                _buf.RemoveAt(0);
                idx--;
            }
            _buf.Insert(idx, new Snap { Time = time, Value = value });
        }

        /// <summary>Состояние на «сейчас минус задержка»: <c>TrySample(now - InterpolationDelay)</c>.</summary>
        public bool TrySampleNow(double now, out T value) => TrySample(now - InterpolationDelay, out value);

        /// <summary>Состояние на момент <paramref name="renderTime"/>. <c>false</c>, если буфер пуст.</summary>
        public bool TrySample(double renderTime, out T value)
        {
            int n = _buf.Count;
            if (n == 0) { value = default; return false; }
            if (n == 1 || renderTime <= _buf[0].Time) { value = _buf[0].Value; return true; }

            var last = _buf[n - 1];
            if (renderTime >= last.Time)
            {
                var prev = _buf[n - 2];
                double span = last.Time - prev.Time;
                double ahead = Math.Min(renderTime - last.Time, Math.Max(0, ExtrapolationLimit));
                if (ahead <= 0 || span <= 0) { value = last.Value; return true; }
                value = _lerp(prev.Value, last.Value, (float)((last.Time - prev.Time + ahead) / span));
                return true;
            }

            // Ищем пару a <= renderTime < b (с конца: обычно renderTime близко к свежим).
            int i = n - 1;
            while (i > 0 && _buf[i - 1].Time > renderTime) i--;
            var a = _buf[i - 1];
            var b = _buf[i];
            double t = (renderTime - a.Time) / (b.Time - a.Time);
            value = _lerp(a.Value, b.Value, (float)t);

            // Старое, что уже не понадобится (оставляем одну точку до renderTime).
            if (i - 1 > 0) _buf.RemoveRange(0, i - 1);
            return true;
        }

        /// <summary>Очищает буфер и статистику (например, после телепорта или реконнекта).</summary>
        public void Clear()
        {
            _buf.Clear();
            _lastTime = double.NaN;
            _interval = 0; _lateAvg = 0; _lateDev = 0; _hasLate = false; _delay = -1;
        }

        private double Target() =>
            _interval * IntervalMultiplier + (_hasLate ? Math.Max(0, _lateAvg) + _lateDev * JitterMultiplier : 0) + SafetyMargin;

        private double Clamp(double d) => Math.Max(MinDelay, Math.Min(MaxDelay, d));
    }
}
