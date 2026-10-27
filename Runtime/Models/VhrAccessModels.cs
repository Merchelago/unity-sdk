using System;
using System.Globalization;

namespace VhrGames.Sdk
{
    /// <summary>Тип игры на платформе (<see cref="VhrAccessState.GameType"/>, SDK 1.13.0+).</summary>
    public enum VhrGameType
    {
        /// <summary>Обычная игра: бесплатная, с рекламой, без демо-ограничений. Доступ всегда полный.</summary>
        Regular = 0,

        /// <summary>
        /// Премиум-игра: платная (покупка за монеты и/или подписка Premium), без рекламы, может
        /// иметь демо-версию. Премиум-статус ставит только администратор платформы при модерации.
        /// </summary>
        Premium = 1
    }

    /// <summary>Уровень доступа игрока к игре (<see cref="VhrAccessState.Access"/>, SDK 1.13.0+).</summary>
    public enum VhrAccessLevel
    {
        /// <summary>Полный доступ: обычная игра, купленная премиум-игра или подписка Premium.</summary>
        Full = 0,

        /// <summary>Демо-версия премиум-игры: что именно ограничить, решает игра.</summary>
        Demo = 1,

        /// <summary>
        /// Доступа нет (премиум-игра без демо, не куплена). На сайте игра в этом состоянии не
        /// запускается — платформа сразу предлагает покупку; в Editor это состояние для проверки ветки кода.
        /// </summary>
        None = 2
    }

    /// <summary>Откуда у игрока доступ (<see cref="VhrAccessState.Source"/>, SDK 1.13.0+).</summary>
    public enum VhrAccessSource
    {
        /// <summary>
        /// Обычная (бесплатная) игра. У премиум-игры — полный доступ без покупки: команда игры
        /// (автор, участники студии), модератор платформы, тестовый игрок <c>sbx_*</c> песочного ключа.
        /// </summary>
        Free = 0,

        /// <summary>Игра куплена (в том числе подарена родителем).</summary>
        Purchase = 1,

        /// <summary>Подписка Premium (своя или родителя), игра входит в подписку.</summary>
        Subscription = 2,

        /// <summary>Игрок покупал в этой игре до её перевода в премиум — полный доступ сохранён.</summary>
        Grandfather = 3,

        /// <summary>Демо-версия.</summary>
        Demo = 4,

        /// <summary>Доступа нет.</summary>
        None = 5
    }

    /// <summary>
    /// Откуда SDK узнал состояние доступа (<see cref="VhrAccessState.Origin"/>) — для логов и
    /// отладки. Источник правды — сервер; остальное — подсказки, пока ответа сервера нет.
    /// </summary>
    public enum VhrAccessOrigin
    {
        /// <summary>
        /// Ничего не известно (сервер без премиум-игр, нет связи и нет подсказок): обычная игра с
        /// полным доступом — так же, как SDK до 1.13.0.
        /// </summary>
        Default = 0,

        /// <summary>Ответ сервера <c>GET /api/Games/{id}/access</c> — источник правды.</summary>
        Server = 1,

        /// <summary>Страница платформы (<c>postMessage vhr:access:*</c>).</summary>
        Host = 2,

        /// <summary>Claims игрового токена (<c>gtp</c>/<c>acc</c>/<c>asrc</c>): цена и подписка неизвестны.</summary>
        Token = 3,

        /// <summary>Симуляция в Unity Editor (окно <c>VHR → Тестирование в Editor</c>).</summary>
        Simulation = 4
    }

    /// <summary>
    /// Состояние доступа игрока к игре (SDK 1.13.0+, <see cref="IVhrAccess"/>). Одна сборка
    /// работает в обоих режимах: у обычной игры всегда <see cref="IsFull"/> = <c>true</c>.
    /// </summary>
    /// <example><code>
    /// var a = await VhrSdk.Access.GetAsync();
    /// if (a.IsDemo) LimitToFirstLevels();      // демо-ограничение — решает игра
    /// </code></example>
    public sealed class VhrAccessState
    {
        /// <summary>Обычная или премиум-игра.</summary>
        public VhrGameType GameType { get; set; }

        /// <summary>Полный доступ, демо или нет доступа.</summary>
        public VhrAccessLevel Access { get; set; }

        /// <summary>Откуда доступ: бесплатно, покупка, подписка, «старый покупатель», демо.</summary>
        public VhrAccessSource Source { get; set; }

        /// <summary>Цена полной версии в монетах (1 монета = 1 ₽); <c>null</c> — обычная игра или цена неизвестна.</summary>
        public int? PriceCoins { get; set; }

        /// <summary>Игра входит в подписку Premium (тогда в окне покупки есть «Оформить Premium»).</summary>
        public bool InSubscription { get; set; }

        /// <summary>У премиум-игры есть демо-версия.</summary>
        public bool HasDemo { get; set; }

        /// <summary>Откуда SDK узнал это состояние (для логов; правда — <see cref="VhrAccessOrigin.Server"/>).</summary>
        public VhrAccessOrigin Origin { get; set; }

        /// <summary><c>true</c> — премиум-игра.</summary>
        public bool IsPremium => GameType == VhrGameType.Premium;

        /// <summary><c>true</c> — полный доступ (у обычной игры — всегда).</summary>
        public bool IsFull => Access == VhrAccessLevel.Full;

        /// <summary><c>true</c> — демо-версия премиум-игры: ограничьте контент и предложите полную версию.</summary>
        public bool IsDemo => Access == VhrAccessLevel.Demo;

        /// <summary>
        /// Есть ли смысл показывать кнопку «Купить полную версию»
        /// (<see cref="IVhrAccess.OpenPurchaseAsync"/>): премиум-игра без полного доступа.
        /// </summary>
        public bool CanPurchase => IsPremium && !IsFull;

        /// <summary>Обычная игра с полным доступом (так SDK ведёт себя, пока сервер не сказал иного).</summary>
        public static VhrAccessState Regular(VhrAccessOrigin origin = VhrAccessOrigin.Default) => new VhrAccessState
        {
            GameType = VhrGameType.Regular,
            Access = VhrAccessLevel.Full,
            Source = VhrAccessSource.Free,
            Origin = origin
        };

        /// <summary>Те же ли данные доступа (без учёта <see cref="Origin"/>).</summary>
        public bool SameAs(VhrAccessState other) =>
            other != null &&
            GameType == other.GameType &&
            Access == other.Access &&
            Source == other.Source &&
            PriceCoins == other.PriceCoins &&
            InSubscription == other.InSubscription &&
            HasDemo == other.HasDemo;

        /// <summary>Копия (SDK отдаёт игре копии — правка объекта игрой не меняет кэш SDK).</summary>
        public VhrAccessState Clone() => (VhrAccessState)MemberwiseClone();

        /// <inheritdoc />
        public override string ToString()
        {
            var s = $"VhrAccessState({GameType}, {Access}/{Source}";
            if (PriceCoins.HasValue) s += $", price={PriceCoins.Value}";
            if (InSubscription) s += ", inSubscription";
            if (HasDemo) s += ", hasDemo";
            return s + $", from {Origin})";
        }
    }

    /// <summary>Итог окна покупки полной версии (<see cref="IVhrAccess.OpenPurchaseAsync"/>, SDK 1.13.0+).</summary>
    public enum VhrAccessPurchaseStatus
    {
        /// <summary>
        /// Окно не открылось или покупка невозможна: обычная игра (<c>not_premium</c>), доступ уже
        /// полный (<c>already_full</c>), игра открыта не на vhrgames.ru (<c>not_hosted</c>), сайт
        /// не поддерживает премиум-игры (<c>host_unsupported</c>), уже идёт покупка (<c>busy</c>),
        /// ошибка (<c>host_error</c>, <c>host_timeout</c>). Код — в <see cref="VhrAccessPurchaseResult.ErrorCode"/>.
        /// Значение по умолчанию (0).
        /// </summary>
        Unavailable = 0,

        /// <summary>Игрок купил игру за монеты — доступ полный.</summary>
        Purchased = 1,

        /// <summary>Игрок оформил подписку Premium (игра входит в подписку) — доступ полный.</summary>
        Subscribed = 2,

        /// <summary>
        /// Детский аккаунт: отправлен запрос родителю («Попросить у родителя»). Доступ пока не
        /// изменился; когда родитель одобрит, придёт <see cref="IVhrAccess.AccessChanged"/>.
        /// </summary>
        Requested = 3,

        /// <summary>Игрок закрыл окно. Ничего не изменилось.</summary>
        Cancelled = 4
    }

    /// <summary>Результат <see cref="IVhrAccess.OpenPurchaseAsync"/>. Никогда не <c>null</c>.</summary>
    public sealed class VhrAccessPurchaseResult
    {
        /// <summary>Итог окна покупки.</summary>
        public VhrAccessPurchaseStatus Status { get; set; }

        /// <summary>Состояние доступа после окна (никогда не <c>null</c>). Открывайте полную версию по <see cref="VhrAccessState.IsFull"/>.</summary>
        public VhrAccessState State { get; set; }

        /// <summary><c>true</c> — доступ теперь полный (<see cref="VhrAccessState.IsFull"/> у <see cref="State"/>).</summary>
        public bool IsFull => State != null && State.IsFull;

        /// <summary><c>true</c> — Unity Editor: покупка симулирована, монеты не тратились.</summary>
        public bool IsTest { get; set; }

        /// <summary>
        /// Машиночитаемая причина для логов: <c>not_premium</c>, <c>already_full</c>, <c>not_hosted</c>,
        /// <c>host_unsupported</c>, <c>busy</c>, <c>host_timeout</c>, <c>host_error</c>, <c>disposed</c>,
        /// <c>cancelled</c>… Пусто при успехе.
        /// </summary>
        public string ErrorCode { get; set; }

        /// <summary>Человекочитаемое пояснение (по-русски) — для логов, не для игрока.</summary>
        public string Message { get; set; }

        /// <inheritdoc />
        public override string ToString()
        {
            var s = $"VhrAccessPurchaseResult({Status}";
            if (!string.IsNullOrEmpty(ErrorCode)) s += $", code={ErrorCode}";
            if (IsTest) s += ", test";
            return s + $", {State})";
        }
    }

    /// <summary>
    /// Чистая логика доступа (без UnityEngine — проверяется тестами на .NET): разбор ответа
    /// сервера, сообщений хоста и claims игрового токена, сравнение состояний.
    /// </summary>
    internal static class VhrAccessCodec
    {
        /// <summary>Строка → тип игры. Пусто/неизвестно → <c>null</c>.</summary>
        internal static VhrGameType? ParseGameType(string s)
        {
            switch (Norm(s))
            {
                case "premium": return VhrGameType.Premium;
                case "regular": return VhrGameType.Regular;
                default: return null;
            }
        }

        /// <summary>Строка → уровень доступа. Пусто/неизвестно → <c>null</c>.</summary>
        internal static VhrAccessLevel? ParseAccess(string s)
        {
            switch (Norm(s))
            {
                case "full": return VhrAccessLevel.Full;
                case "demo": return VhrAccessLevel.Demo;
                case "none": return VhrAccessLevel.None;
                default: return null;
            }
        }

        /// <summary>Строка → источник доступа. Пусто/неизвестно → <c>null</c>.</summary>
        internal static VhrAccessSource? ParseSource(string s)
        {
            switch (Norm(s))
            {
                case "free": return VhrAccessSource.Free;
                case "purchase":
                case "parent_gift": // владение, подаренное родителем, — тоже покупка
                case "admin":
                    return VhrAccessSource.Purchase;
                case "subscription": return VhrAccessSource.Subscription;
                case "grandfather": return VhrAccessSource.Grandfather;
                case "demo": return VhrAccessSource.Demo;
                case "none": return VhrAccessSource.None;
                default: return null;
            }
        }

        /// <summary>
        /// Состояние из полей контракта. <c>null</c> — это не состояние доступа (нет ни
        /// <c>gameType</c>, ни <c>access</c>). Неизвестные значения — консервативно: у
        /// обычной игры доступ всегда полный; у премиум-игры неизвестный доступ — демо (если оно есть) или «нет».
        /// </summary>
        internal static VhrAccessState FromParts(string gameType, string access, string source,
            int? price, bool inSubscription, bool hasDemo, VhrAccessOrigin origin)
        {
            var gt = ParseGameType(gameType);
            var acc = ParseAccess(access);
            if (gt == null && acc == null) return null;

            // Без gameType: «демо»/«нет доступа» бывают только у премиум-игры.
            var type = gt ?? (acc == VhrAccessLevel.Demo || acc == VhrAccessLevel.None
                ? VhrGameType.Premium
                : VhrGameType.Regular);

            var st = new VhrAccessState
            {
                GameType = type,
                PriceCoins = price.HasValue && price.Value > 0 ? price : null,
                InSubscription = inSubscription,
                HasDemo = hasDemo,
                Origin = origin
            };

            if (type == VhrGameType.Regular)
            {
                // Контракт: regular → full/free. Одна сборка работает в обоих режимах.
                st.Access = VhrAccessLevel.Full;
                st.Source = VhrAccessSource.Free;
                return st;
            }

            st.Access = acc ?? (hasDemo ? VhrAccessLevel.Demo : VhrAccessLevel.None);
            var src = ParseSource(source);
            switch (st.Access)
            {
                case VhrAccessLevel.Full:
                    // Полный доступ премиум-игры: покупка, подписка, «старый покупатель» или free —
                    // команда игры / модератор / песочный игрок (сервер отдаёт full/free). Демо и
                    // «без доступа» источником полного доступа не бывают.
                    st.Source = src == VhrAccessSource.Subscription || src == VhrAccessSource.Grandfather ||
                                src == VhrAccessSource.Free
                        ? src.Value
                        : VhrAccessSource.Purchase;
                    break;
                case VhrAccessLevel.Demo:
                    st.Source = VhrAccessSource.Demo;
                    st.HasDemo = true;
                    break;
                default:
                    st.Source = VhrAccessSource.None;
                    break;
            }
            return st;
        }

        /// <summary>
        /// Разбор JSON состояния: ответ <c>GET /api/Games/{id}/access</c> (или тело <c>403 premium_required</c>):
        /// <c>{ gameType, access, source, price|priceCoins, inSubscription, hasDemo }</c>, в том числе
        /// внутри обёртки (<c>{ state: {...} }</c>). <c>null</c> — в JSON нет состояния доступа.
        /// </summary>
        internal static VhrAccessState FromJson(string json, VhrAccessOrigin origin)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var t = json.TrimStart();
            if (t.Length == 0 || t[0] != '{') return null;
            try
            {
                var price = ReadInt(t, "price") ?? ReadInt(t, "priceCoins");
                return FromParts(
                    VhrJwt.ReadClaim(t, "gameType"),
                    VhrJwt.ReadClaim(t, "access"),
                    VhrJwt.ReadClaim(t, "source"),
                    price,
                    ReadBool(t, "inSubscription"),
                    ReadBool(t, "hasDemo"),
                    origin);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Подсказка из claims игрового токена: <c>gtp</c> (regular|premium), <c>acc</c> (full|demo),
        /// <c>asrc</c> (источник). Цена и подписка в токене не передаются. <c>null</c> — токен без
        /// claims доступа (сервер до 1.13 или не JWT).
        /// </summary>
        internal static VhrAccessState FromToken(string jwt)
        {
            if (!VhrJwt.TryDecode(jwt, out var info)) return null;
            if (string.IsNullOrEmpty(info.GameType)) return null;
            var acc = ParseAccess(info.AccessLevel);
            var st = FromParts(info.GameType, info.AccessLevel, info.AccessSource, null,
                ParseSource(info.AccessSource) == VhrAccessSource.Subscription,
                acc == VhrAccessLevel.Demo, VhrAccessOrigin.Token);
            return st;
        }

        /// <summary>
        /// Тот же уровень доступа: тип игры и доступ (источник, цена и флаги не сравниваются —
        /// в токене их может не быть).
        /// </summary>
        internal static bool SameLevel(VhrAccessState a, VhrAccessState b) =>
            a != null && b != null && a.GameType == b.GameType && a.Access == b.Access;

        /// <summary>
        /// Подсказка токена поверх известного состояния: уровень — из токена (он новее), цена,
        /// подписка и демо — из известного состояния той же игры (в токене их нет).
        /// </summary>
        internal static VhrAccessState MergeTokenHint(VhrAccessState hint, VhrAccessState known)
        {
            if (hint == null) return known?.Clone();
            var r = hint.Clone();
            if (known != null && known.GameType == r.GameType)
            {
                r.PriceCoins ??= known.PriceCoins;
                r.InSubscription = r.InSubscription || known.InSubscription;
                r.HasDemo = r.HasDemo || known.HasDemo;
            }
            return r;
        }

        /// <summary>Статус результата окна покупки из строки хоста.</summary>
        internal static VhrAccessPurchaseStatus ParsePurchaseResult(string s)
        {
            switch (Norm(s))
            {
                case "purchased": return VhrAccessPurchaseStatus.Purchased;
                case "subscribed": return VhrAccessPurchaseStatus.Subscribed;
                case "requested": return VhrAccessPurchaseStatus.Requested;
                case "cancelled":
                case "canceled":
                    return VhrAccessPurchaseStatus.Cancelled;
                default: return VhrAccessPurchaseStatus.Unavailable;
            }
        }

        private static string Norm(string s) => string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim().ToLowerInvariant();

        private static int? ReadInt(string json, string name)
        {
            var raw = VhrJwt.ReadClaim(json, name);
            if (string.IsNullOrEmpty(raw)) return null;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
            // 199.0 и т.п.
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) &&
                d >= 0 && d <= int.MaxValue)
                return (int)Math.Floor(d);
            return null;
        }

        private static bool ReadBool(string json, string name) =>
            string.Equals(VhrJwt.ReadClaim(json, name), "true", StringComparison.OrdinalIgnoreCase);
    }
}
