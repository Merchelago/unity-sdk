using System;
using System.ComponentModel;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Итог покупки товара каталога (<see cref="IVhrEconomy.PurchaseAsync"/>, SDK 1.9.0+).
    /// <para>
    /// Покупка в играх VHR всегда подтверждается игроком в окне платформы. Пополнять
    /// баланс в игре, списывать или начислять монеты напрямую нельзя — это делает
    /// только платформа.
    /// </para>
    /// </summary>
    public enum VhrPurchaseStatus
    {
        /// <summary>
        /// Техническая ошибка (сеть, неверный товар, игра открыта не на vhrgames.ru и т.п.).
        /// Подробности — в <see cref="VhrPurchaseResult.ErrorCode"/> / <see cref="VhrPurchaseResult.Message"/>.
        /// Значение по умолчанию (0): «покупка не состоялась».
        /// </summary>
        Error = 0,

        /// <summary>Игрок подтвердил, монеты списаны — выдайте товар.</summary>
        Completed = 1,

        /// <summary>Игрок закрыл окно подтверждения или нажал «Отмена». Монеты не списаны.</summary>
        Cancelled = 2,

        /// <summary>
        /// Не хватает монет. Монеты не списаны. Предложите пополнить счёт на платформе —
        /// <see cref="IVhrEconomy.OpenTopUp"/>.
        /// </summary>
        InsufficientFunds = 3,

        /// <summary>Игрок не успел подтвердить: намерение покупки истекло (живёт 120 с). Монеты не списаны.</summary>
        Expired = 4,

        /// <summary>
        /// Сервер отклонил запрос из-за устаревшей версии SDK (<c>426 sdk_update_required</c>).
        /// Обновите пакет <c>ru.vhrgames.sdk</c> и пересоберите игру.
        /// </summary>
        SdkUpdateRequired = 5,

        /// <summary>
        /// Покупка запрещена платформой: родительский контроль (<c>parental_block</c>),
        /// дневной лимит (<c>daily_limit</c>), токен не игровой (<c>game_token_forbidden</c>),
        /// игра не совпадает с токеном (<c>game_mismatch</c>). Код — в <see cref="VhrPurchaseResult.ErrorCode"/>.
        /// </summary>
        Forbidden = 6
    }

    /// <summary>
    /// Товар каталога игры (<see cref="IVhrEconomy.GetItemsAsync"/>,
    /// <c>GET {BridgeBaseUrl}/api/items?gameId=…</c>). Заводится в кабинете
    /// разработчика на сайте (игра → Товары). Поля названы как camelCase-JSON
    /// моста (<c>GameBridgeMS ItemResponse</c>) для <c>JsonUtility</c>.
    /// </summary>
    [Serializable]
    public sealed class VhrCatalogItem
    {
        /// <summary>Id товара (GUID-строка). Именно его передавайте в <see cref="IVhrEconomy.PurchaseAsync"/>.</summary>
        public string id;

        /// <summary>Id игры (GUID-строка).</summary>
        public string gameId;

        /// <summary>Ключ товара, заданный разработчиком (напр. <c>sword_gold</c>). Удобен для своих таблиц.</summary>
        public string code;

        /// <summary>Название товара (его же игрок видит в окне подтверждения платформы).</summary>
        public string title;

        /// <summary>Описание (может быть пустым).</summary>
        public string description;

        /// <summary>Цена за 1 шт. в монетах (1 монета = 1 рубль).</summary>
        public long priceCoins;

        /// <summary>Активен ли товар (мост отдаёт только активные).</summary>
        public bool active;

        /// <summary>UTC-время создания (ISO-8601).</summary>
        public string createdAt;

        /// <summary>URL иконки, если платформа её отдаёт (сейчас может быть пустым).</summary>
        public string iconUrl;
    }

    /// <summary>
    /// Товар, который текущий игрок уже купил в этой игре
    /// (<see cref="IVhrEconomy.GetOwnedItemsAsync"/>, <c>GET {BridgeBaseUrl}/api/inventory</c>).
    /// Нужен для разовых покупок, восстановления покупок и разблокировок.
    /// </summary>
    [Serializable]
    public sealed class VhrOwnedItem
    {
        /// <summary>Id товара каталога (как <see cref="VhrCatalogItem.id"/>).</summary>
        public string itemId;

        /// <summary>Сколько штук куплено всего.</summary>
        public int quantity;

        /// <summary>UTC-время последней покупки (ISO-8601).</summary>
        public string lastPurchasedAt;
    }

    /// <summary>
    /// Результат <see cref="IVhrEconomy.PurchaseAsync"/>. Никогда не <c>null</c>.
    /// Выдавайте товар только при <see cref="IsCompleted"/>.
    /// </summary>
    /// <remarks>
    /// Обратная совместимость с 1.8: у результата остались члены <c>success</c>,
    /// <c>balance</c>, <c>idempotentReplay</c>, <c>message</c> (как у
    /// <see cref="VhrEconomyResult"/>) и неявное приведение к
    /// <see cref="VhrEconomyResult"/>, поэтому старый код
    /// <c>var r = await PurchaseAsync(...); if (r.success) ...</c> компилируется.
    /// </remarks>
    public sealed class VhrPurchaseResult
    {
        /// <summary>Итог покупки.</summary>
        public VhrPurchaseStatus Status { get; set; }

        /// <summary><c>true</c> — покупка совершена, монеты списаны: выдайте товар.</summary>
        public bool IsCompleted => Status == VhrPurchaseStatus.Completed;

        /// <summary>Id товара каталога, который покупали.</summary>
        public string ItemId { get; set; }

        /// <summary>Количество.</summary>
        public int Quantity { get; set; }

        /// <summary>Название товара из каталога платформы (если сервер его вернул).</summary>
        public string Title { get; set; }

        /// <summary>Итоговая цена в монетах (1 монета = 1 рубль), если сервер её вернул; иначе 0.</summary>
        public long Price { get; set; }

        /// <summary>Id совершённой покупки (для логов/поддержки). Пусто, если покупка не состоялась.</summary>
        public string PurchaseId { get; set; }

        /// <summary>Баланс игрока после операции. Имеет смысл только при <see cref="HasBalance"/>.</summary>
        public long Balance { get; set; }

        /// <summary>Известен ли <see cref="Balance"/> (его прислал сервер или хост).</summary>
        public bool HasBalance { get; set; }

        /// <summary>Id намерения покупки на сервере (если до него дошло).</summary>
        public string IntentId { get; set; }

        /// <summary>Ключ идемпотентности, с которым ушла покупка.</summary>
        public string ExternalId { get; set; }

        /// <summary><c>true</c>, если сервер узнал <see cref="ExternalId"/> и вернул уже совершённую ранее покупку.</summary>
        public bool IdempotentReplay { get; set; }

        /// <summary>
        /// <c>true</c> — покупка тестовая: Unity Editor (режим Simulation — локально, или
        /// Live — песочница с тестовыми монетами). Реальные деньги не тратились.
        /// </summary>
        public bool IsTest { get; set; }

        /// <summary>
        /// Машиночитаемый код для логов и ветвления: <c>not_hosted</c>, <c>busy</c>,
        /// <c>daily_limit</c>, <c>parental_block</c>, <c>game_mismatch</c>,
        /// <c>game_token_forbidden</c>, <c>sdk_update_required</c>, <c>unauthorized</c>,
        /// <c>connection_error</c>, <c>item_unavailable</c>, <c>confirmation_unavailable</c> и т.п.
        /// Пусто при успехе.
        /// </summary>
        public string ErrorCode { get; set; }

        /// <summary>Человекочитаемое пояснение (по-русски) — для логов, не для игрока.</summary>
        public string Message { get; set; }

        // ---- совместимость с VhrEconomyResult (SDK ≤ 1.8) ----

        /// <summary>[Совместимость с 1.8] То же, что <see cref="IsCompleted"/>.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Используйте IsCompleted или Status (VhrPurchaseStatus).", false)]
        public bool success => IsCompleted;

        /// <summary>[Совместимость с 1.8] То же, что <see cref="Balance"/>.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Используйте Balance и HasBalance.", false)]
        public long balance => Balance;

        /// <summary>[Совместимость с 1.8] То же, что <see cref="IdempotentReplay"/>.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Используйте IdempotentReplay.", false)]
        public bool idempotentReplay => IdempotentReplay;

        /// <summary>[Совместимость с 1.8] То же, что <see cref="Message"/>.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Используйте Message и ErrorCode.", false)]
        public string message => Message;

        /// <summary>
        /// Приведение к результату 1.8 — чтобы компилировался код вида
        /// <c>VhrEconomyResult r = await economy.PurchaseAsync(...)</c>.
        /// </summary>
        public static implicit operator VhrEconomyResult(VhrPurchaseResult r) =>
            r == null
                ? null
                : new VhrEconomyResult
                {
                    success = r.IsCompleted,
                    balance = r.Balance,
                    idempotentReplay = r.IdempotentReplay,
                    message = r.Message,
                    code = r.ErrorCode
                };

        /// <inheritdoc />
        public override string ToString()
        {
            var s = $"VhrPurchaseResult({Status}, item={ItemId}x{Quantity}";
            if (Price > 0) s += $", price={Price}";
            if (HasBalance) s += $", balance={Balance}";
            if (!string.IsNullOrEmpty(PurchaseId)) s += $", purchaseId={PurchaseId}";
            if (IdempotentReplay) s += ", replay";
            if (IsTest) s += ", test";
            if (!string.IsNullOrEmpty(ErrorCode)) s += $", code={ErrorCode}";
            return s + ")";
        }

        internal static VhrPurchaseResult Fail(
            VhrPurchaseStatus status, string code, string message, string itemId, int quantity, string externalId = null)
            => new VhrPurchaseResult
            {
                Status = status,
                ErrorCode = code,
                Message = message,
                ItemId = itemId,
                Quantity = quantity,
                ExternalId = externalId
            };
    }
}
