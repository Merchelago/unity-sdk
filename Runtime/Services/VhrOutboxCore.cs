using System;
using System.Collections.Generic;
using System.Text;

// ВАЖНО: в этом файле нет ссылок на UnityEngine — чистую логику очереди (схлопывание, TTL,
// лимит, классификация ответов, паузы, формат файла) проверяют тесты на обычном .NET.
namespace VhrGames.Sdk
{
    /// <summary>Виды отложенных операций в файле очереди (строки — часть формата файла, не менять).</summary>
    internal static class VhrOutboxKinds
    {
        /// <summary><c>POST /api/Leaderboard/score</c>; цель — id таблицы, значение — счёт.</summary>
        internal const string LeaderboardScore = "lb.score";

        /// <summary><c>POST /api/Achievements/unlock</c>; цель — полный код ачивки.</summary>
        internal const string AchievementUnlock = "ach.unlock";

        /// <summary><c>POST /api/Tournaments/{id}/score</c>; цель — id турнира, значение — счёт.</summary>
        internal const string TournamentScore = "tour.score";

        /// <summary><c>POST /api/GameSessions/{id}/end</c>; цель — id сессии.</summary>
        internal const string SessionEnd = "session.end";

        internal static bool IsKnown(string kind)
            => kind == LeaderboardScore || kind == AchievementUnlock || kind == TournamentScore || kind == SessionEnd;

        /// <summary>
        /// Порядок выброса при переполнении очереди: сначала завершения сессий, потом счёт, ачивки — последними.
        /// </summary>
        internal static int DropPriority(string kind)
        {
            switch (kind)
            {
                case SessionEnd: return 0;
                case LeaderboardScore:
                case TournamentScore: return 1;
                default: return 2;
            }
        }
    }

    /// <summary>
    /// Элемент очереди <see cref="VhrOutbox"/>. Содержимое (<see cref="Payload"/>, <see cref="Value"/>)
    /// после постановки не меняется — схлопывание заменяет элемент новым с другим <see cref="Id"/>,
    /// поэтому ответ на запрос «в полёте» всегда относится к тому, что было отправлено.
    /// Меняются только счётчики <see cref="Attempts"/> / <see cref="NextAttemptAt"/>.
    /// </summary>
    internal sealed class VhrOutboxItem
    {
        public string Id;
        public string Kind;
        /// <summary>Игрок (claim <c>sub</c> токена); пусто — анонимная операция (только завершение сессии).</summary>
        public string UserId = string.Empty;
        /// <summary>Когда операция впервые встала в очередь (Unix, мс UTC) — от него считается TTL.</summary>
        public long CreatedAt;
        public int Attempts;
        /// <summary>Не раньше этого момента (Unix, мс UTC); 0 — сразу.</summary>
        public long NextAttemptAt;
        /// <summary>Ключ схлопывания (<see cref="VhrOutboxQueue.KeyFor"/>).</summary>
        public string Key;
        /// <summary>Цель: id таблицы / код ачивки / id турнира / id сессии.</summary>
        public string Target;
        /// <summary>Счёт (рекорды и турниры); иначе 0.</summary>
        public long Value;
        /// <summary>Готовое JSON-тело запроса.</summary>
        public string Payload;

        internal VhrOutboxItem Clone() => (VhrOutboxItem)MemberwiseClone();
    }

    /// <summary>
    /// Состояние очереди отложенных операций: схлопывание, TTL, лимит, выбор следующей отправки,
    /// формат файла. Не потокобезопасно — только главный поток.
    /// </summary>
    internal sealed class VhrOutboxQueue
    {
        internal const int FormatVersion = 1;

        private readonly List<VhrOutboxItem> _items = new List<VhrOutboxItem>();

        internal int Count => _items.Count;

        internal IReadOnlyList<VhrOutboxItem> Items => _items;

        /// <summary>Ключ схлопывания: вид + цель.</summary>
        internal static string KeyFor(string kind, string target) => kind + ":" + (target ?? string.Empty);

        internal VhrOutboxItem Find(string id)
        {
            foreach (var it in _items)
                if (it.Id == id) return it;
            return null;
        }

        internal bool Remove(string id)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Id != id) continue;
                _items.RemoveAt(i);
                return true;
            }
            return false;
        }

        /// <summary>Сколько операций ждёт отправки от игрока <paramref name="userId"/>.</summary>
        internal int CountFor(string userId)
        {
            userId ??= string.Empty;
            int n = 0;
            foreach (var it in _items)
                if (it.UserId == userId) n++;
            return n;
        }

        /// <summary>
        /// Ставит операцию в очередь со схлопыванием по ключу (только среди операций того же игрока):
        /// <list type="bullet">
        /// <item>ачивка — одна на код (повторная постановка ничего не меняет);</item>
        /// <item>рекорд — по таблице остаются максимум и минимум ожидающих счетов: направление таблицы
        /// клиенту может быть неизвестно, а сервер хранит лучший по направлению — из двух крайних
        /// доедет нужный;</item>
        /// <item>турнир — максимум (сервер хранит лучший, больше — лучше);</item>
        /// <item>завершение сессии — последнее.</item>
        /// </list>
        /// <paramref name="payloadFor"/> строит тело для счёта (с актуальными подсказками таблицы).
        /// </summary>
        /// <returns><c>true</c> — очередь изменилась.</returns>
        internal bool Enqueue(VhrOutboxItem incoming, Func<long, string> payloadFor)
        {
            if (incoming == null) throw new ArgumentNullException(nameof(incoming));
            incoming.UserId ??= string.Empty;
            incoming.Key ??= KeyFor(incoming.Kind, incoming.Target);
            if (incoming.Payload == null && payloadFor != null) incoming.Payload = payloadFor(incoming.Value);

            var same = new List<VhrOutboxItem>();
            foreach (var it in _items)
                if (it.UserId == incoming.UserId && it.Key == incoming.Key) same.Add(it);

            if (same.Count == 0)
            {
                _items.Add(incoming);
                return true;
            }

            switch (incoming.Kind)
            {
                case VhrOutboxKinds.AchievementUnlock:
                    return false; // ачивка уже ждёт — повтор ничего не добавляет

                case VhrOutboxKinds.TournamentScore:
                {
                    var best = same[0];
                    foreach (var it in same)
                        if (it.Value > best.Value) best = it;
                    if (incoming.Value <= best.Value && same.Count == 1) return false;
                    var keep = incoming.Value > best.Value ? incoming : best;
                    RemoveAll(same);
                    _items.Add(keep);
                    return true;
                }

                case VhrOutboxKinds.LeaderboardScore:
                    return CollapseExtremes(same, incoming, payloadFor);

                default: // SessionEnd и прочее: последнее значение заменяет прежнее
                    incoming.CreatedAt = MinCreated(same, incoming.CreatedAt);
                    RemoveAll(same);
                    _items.Add(incoming);
                    return true;
            }
        }

        // Рекорд: оставить максимум и минимум (одно значение, если они равны). Элемент, у которого и
        // значение, и тело не изменились, остаётся как есть; иначе — новый элемент (новый Id) с
        // актуальным телом и временем постановки того, кто держал это значение (TTL честный).
        private bool CollapseExtremes(List<VhrOutboxItem> same, VhrOutboxItem incoming, Func<long, string> payloadFor)
        {
            long max = incoming.Value, min = incoming.Value;
            foreach (var it in same)
            {
                if (it.Value > max) max = it.Value;
                if (it.Value < min) min = it.Value;
            }

            var result = new List<VhrOutboxItem>(2);
            result.Add(Pick(max));
            if (min != max) result.Add(Pick(min));

            bool changed = same.Count != result.Count;
            foreach (var r in result)
                if (!same.Contains(r)) changed = true;
            if (!changed) return false;

            RemoveAll(same);
            _items.AddRange(result);
            return true;

            VhrOutboxItem Pick(long value)
            {
                // Тело для этого счёта с подсказками таблицы из самой свежей отправки.
                var payload = payloadFor != null ? payloadFor(value) : (value == incoming.Value ? incoming.Payload : null);
                VhrOutboxItem holder = null;
                foreach (var it in same)
                {
                    if (it.Value != value) continue;
                    if (payload == null || it.Payload == payload) return it; // ничего не изменилось
                    holder ??= it;
                }
                if (holder == null)
                {
                    // Значение пришло с новой отправкой (иначе его держал бы кто-то из same).
                    if (payload != null) incoming.Payload = payload;
                    return incoming;
                }
                // Значение то же, тело изменилось (новые подсказки): новый элемент, TTL — от прежнего.
                var fresh = incoming.Clone();
                fresh.Id = NewId();
                fresh.Value = value;
                fresh.Payload = payload;
                fresh.CreatedAt = holder.CreatedAt;
                fresh.Attempts = holder.Attempts;
                fresh.NextAttemptAt = 0;
                return fresh;
            }
        }

        private static long MinCreated(List<VhrOutboxItem> items, long fallback)
        {
            long min = fallback;
            foreach (var it in items)
                if (it.CreatedAt > 0 && it.CreatedAt < min) min = it.CreatedAt;
            return min;
        }

        private void RemoveAll(List<VhrOutboxItem> items)
        {
            foreach (var it in items) _items.Remove(it);
        }

        /// <summary>Убирает операции старше <paramref name="ttlMs"/> (от постановки) в <paramref name="dropped"/>.</summary>
        internal void PruneExpired(long nowMs, long ttlMs, List<VhrOutboxItem> dropped)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (nowMs - _items[i].CreatedAt < ttlMs) continue;
                dropped?.Add(_items[i]);
                _items.RemoveAt(i);
            }
        }

        /// <summary>
        /// Держит не больше <paramref name="max"/> операций: лишние уходят в <paramref name="dropped"/> —
        /// сначала завершения сессий, потом счёт, ачивки последними; внутри вида — самые старые.
        /// </summary>
        internal void EnforceLimit(int max, List<VhrOutboxItem> dropped)
        {
            if (_items.Count <= max) return;
            var order = new List<VhrOutboxItem>(_items);
            order.Sort((a, b) =>
            {
                var p = VhrOutboxKinds.DropPriority(a.Kind).CompareTo(VhrOutboxKinds.DropPriority(b.Kind));
                return p != 0 ? p : a.CreatedAt.CompareTo(b.CreatedAt);
            });
            for (int i = 0; _items.Count > max && i < order.Count; i++)
            {
                _items.Remove(order[i]);
                dropped?.Add(order[i]);
            }
        }

        /// <summary>
        /// Следующая операция игрока <paramref name="userId"/>, которую можно отправить сейчас
        /// (<see cref="VhrOutboxItem.NextAttemptAt"/> наступил), — самая ранняя по постановке.
        /// </summary>
        internal VhrOutboxItem NextDue(string userId, long nowMs)
        {
            userId ??= string.Empty;
            VhrOutboxItem best = null;
            foreach (var it in _items)
            {
                if (it.UserId != userId || it.NextAttemptAt > nowMs) continue;
                if (best == null || it.CreatedAt < best.CreatedAt) best = it;
            }
            return best;
        }

        /// <summary>Ближайший момент, когда у игрока что-то станет можно отправить; <c>null</c> — у него ничего нет.</summary>
        internal long? EarliestDue(string userId)
        {
            userId ??= string.Empty;
            long? min = null;
            foreach (var it in _items)
            {
                if (it.UserId != userId) continue;
                if (!min.HasValue || it.NextAttemptAt < min.Value) min = it.NextAttemptAt;
            }
            return min;
        }

        // ------------------------------------------------------------ файл

        /// <summary>
        /// Формат файла: <c>{"v":1,"items":[{ id, kind, userId, createdAt, attempts, nextAttemptAt, key,
        /// target, value, payload }]}</c>; <c>payload</c> — JSON-тело запроса строкой.
        /// </summary>
        internal string Serialize()
        {
            var sb = new StringBuilder(64 + _items.Count * 256);
            sb.Append("{\"v\":").Append(FormatVersion).Append(",\"items\":[");
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (i > 0) sb.Append(',');
                bool first = true;
                sb.Append('{');
                VhrJsonLite.Field(sb, ref first, "id", it.Id);
                VhrJsonLite.Field(sb, ref first, "kind", it.Kind);
                VhrJsonLite.Field(sb, ref first, "userId", it.UserId ?? string.Empty);
                VhrJsonLite.Field(sb, ref first, "createdAt", it.CreatedAt);
                VhrJsonLite.Field(sb, ref first, "attempts", (long)it.Attempts);
                VhrJsonLite.Field(sb, ref first, "nextAttemptAt", it.NextAttemptAt);
                VhrJsonLite.Field(sb, ref first, "key", it.Key);
                VhrJsonLite.Field(sb, ref first, "target", it.Target ?? string.Empty);
                VhrJsonLite.Field(sb, ref first, "value", it.Value);
                VhrJsonLite.Field(sb, ref first, "payload", it.Payload);
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// Разбирает файл очереди. <c>false</c> — файл битый целиком (<paramref name="error"/> — почему);
        /// отдельные негодные элементы (неизвестный вид, нет id/тела/времени) пропускаются, их число —
        /// в <paramref name="skipped"/>.
        /// </summary>
        internal static bool TryDeserialize(string json, out VhrOutboxQueue queue, out int skipped, out string error)
        {
            queue = null;
            skipped = 0;
            error = null;
            object root;
            try { root = VhrJsonLite.Parse(json); }
            catch (FormatException e)
            {
                error = e.Message;
                return false;
            }
            if (!(root is Dictionary<string, object> o) || !o.TryGetValue("items", out var itemsObj) || !(itemsObj is List<object> items))
            {
                error = "нет массива items";
                return false;
            }

            queue = new VhrOutboxQueue();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in items)
            {
                if (!(raw is Dictionary<string, object> e))
                {
                    skipped++;
                    continue;
                }
                var it = new VhrOutboxItem
                {
                    Id = VhrJsonLite.Str(e, "id"),
                    Kind = VhrJsonLite.Str(e, "kind"),
                    UserId = VhrJsonLite.Str(e, "userId") ?? string.Empty,
                    CreatedAt = VhrJsonLite.Long(e, "createdAt", 0),
                    Attempts = (int)Math.Max(0, Math.Min(int.MaxValue, VhrJsonLite.Long(e, "attempts", 0))),
                    NextAttemptAt = Math.Max(0, VhrJsonLite.Long(e, "nextAttemptAt", 0)),
                    Key = VhrJsonLite.Str(e, "key"),
                    Target = VhrJsonLite.Str(e, "target") ?? string.Empty,
                    Value = VhrJsonLite.Long(e, "value", 0),
                    Payload = VhrJsonLite.Str(e, "payload")
                };
                if (string.IsNullOrEmpty(it.Id) || !VhrOutboxKinds.IsKnown(it.Kind) || it.CreatedAt <= 0 ||
                    it.Payload == null || !ids.Add(it.Id))
                {
                    skipped++;
                    continue;
                }
                if (string.IsNullOrEmpty(it.Key)) it.Key = KeyFor(it.Kind, it.Target);
                queue._items.Add(it);
            }
            return true;
        }

        internal static string NewId() => Guid.NewGuid().ToString("N");
    }

    /// <summary>Что делать с операцией по итогу попытки отправки.</summary>
    internal enum VhrDelivery
    {
        /// <summary>2xx — доставлено, убрать из очереди.</summary>
        Delivered = 0,

        /// <summary>Нет связи, таймаут, 5xx, 408 — повторить с нарастающей паузой.</summary>
        RetryNetwork = 1,

        /// <summary>429 — повторить после <c>Retry-After</c>.</summary>
        RetryRateLimited = 2,

        /// <summary>401 — дождаться свежего токена и повторить.</summary>
        RetryAuth = 3,

        /// <summary>
        /// Временный отказ сервера по смыслу (<c>no_session</c>: сессию игры ещё не записали) —
        /// повторить позже, не трогая паузу связи.
        /// </summary>
        RetryLater = 4,

        /// <summary>Прочие 4xx (и 501) — повтор не поможет: выбросить и сообщить (<c>OnDropped</c>).</summary>
        Drop = 5
    }

    /// <summary>Правила доставки отложенных операций: классификация ответов, паузы, TTL, лимит.</summary>
    internal static class VhrDeliveryRules
    {
        /// <summary>Сколько операция живёт в очереди: 14 дней, затем выбрасывается с предупреждением.</summary>
        internal const int TtlDays = 14;

        internal const long TtlMs = TtlDays * 24L * 60 * 60 * 1000;

        /// <summary>Максимум операций в очереди (все игроки устройства вместе).</summary>
        internal const int MaxItems = 300;

        /// <summary>Пауза после обрыва связи: 2 → 4 → 8 → … → 60 с (+ джиттер до 20%).</summary>
        internal const double FirstBackoffSeconds = 2;

        internal const double MaxBackoffSeconds = 60;

        /// <summary><c>429</c> без <c>Retry-After</c> — минута; больше 5 минут не ждём.</summary>
        internal const int DefaultRetryAfterSeconds = 60;

        internal const int MaxRetryAfterSeconds = 300;

        /// <summary>Повтор «позже по смыслу» (<see cref="VhrDelivery.RetryLater"/>): раз в 5 минут.</summary>
        internal const int RetryLaterSeconds = 300;

        /// <summary>Сколько ждать свежий токен после 401, прежде чем попробовать снова с тем же.</summary>
        internal const int AuthWaitSeconds = 30;

        /// <summary>Итог попытки по HTTP-статусу (0 — нет ответа: сеть/таймаут) и коду сервера из тела.</summary>
        internal static VhrDelivery Classify(long status, string serverCode)
        {
            if (status >= 200 && status < 300) return VhrDelivery.Delivered;
            if (status <= 0) return VhrDelivery.RetryNetwork;
            if (status == 401) return VhrDelivery.RetryAuth;
            if (status == 408) return VhrDelivery.RetryNetwork;
            if (status == 429) return VhrDelivery.RetryRateLimited;
            if (status == 501) return VhrDelivery.Drop; // эндпоинта нет — повтор не поможет
            if (status >= 500) return VhrDelivery.RetryNetwork;

            var code = (serverCode ?? string.Empty).Trim().ToLowerInvariant();
            // Ачивка: «вы ещё не играли в эту игру» — сессию пишет страница игры; если игра началась
            // без связи, сессия появится при следующем запуске онлайн.
            if (code == "no_session") return VhrDelivery.RetryLater;
            return VhrDelivery.Drop;
        }

        /// <summary>Временная ли ошибка (стоит ли ставить операцию в очередь вместо исключения).</summary>
        internal static bool IsTransient(VhrDelivery d) => d != VhrDelivery.Delivered && d != VhrDelivery.Drop;

        /// <summary>
        /// Пауза после <paramref name="failures"/>-го подряд сбоя связи: 2, 4, 8, 16, 32, 60, 60… с,
        /// плюс джиттер до 20% (<paramref name="random01"/> ∈ [0, 1)) — чтобы клиенты не били сервер разом.
        /// </summary>
        internal static double BackoffSeconds(int failures, double random01)
        {
            if (failures < 1) failures = 1;
            double baseSec = failures >= 6 ? MaxBackoffSeconds : Math.Min(MaxBackoffSeconds, FirstBackoffSeconds * Math.Pow(2, failures - 1));
            if (double.IsNaN(random01) || random01 < 0) random01 = 0;
            if (random01 >= 1) random01 = 0.999;
            return baseSec * (1 + 0.2 * random01);
        }

        /// <summary><c>Retry-After</c> (с) → пауза: 0/нет — минута, не больше 5 минут, не меньше секунды.</summary>
        internal static int RetryAfter(int headerSeconds)
        {
            if (headerSeconds <= 0) return DefaultRetryAfterSeconds;
            return Math.Max(1, Math.Min(MaxRetryAfterSeconds, headerSeconds));
        }

        /// <summary>
        /// Дошёл ли запрос до сервисов VHR (для <c>IVhrConnection.IsOnline</c>): нет ответа, 502/504 и
        /// 503 без кода приложения (шлюз не достучался до сервиса) — «нет связи»; любой другой ответ,
        /// даже ошибка, — связь есть.
        /// </summary>
        internal static bool IsReachable(long status, string serverCode)
        {
            if (status <= 0) return false;
            if (status == 502 || status == 504) return false;
            if (status == 503 && string.IsNullOrWhiteSpace(serverCode)) return false;
            return true;
        }
    }
}
