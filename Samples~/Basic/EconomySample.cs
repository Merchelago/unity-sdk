using System.Linq;
using System.Threading;
using R3;
using UnityEngine;
using VhrGames.Sdk;

namespace VhrGames.Sdk.Samples
{
    /// <summary>
    /// Минимальный магазин без DI (SDK 1.9.0+): инициализация, баланс, каталог товаров
    /// игры, покупка с подтверждением игроком, «что уже куплено» и кнопка «Пополнить».
    /// <para>
    /// Правила платформы: игроки пополняют счёт ТОЛЬКО на платформе, а в игре только
    /// тратят — на товары каталога, подтверждая покупку в окне платформы. Пополнять,
    /// списывать или начислять монеты из игры нельзя (SpendAsync/GrantCoinsAsync
    /// устарели и сразу возвращают отказ). 1 монета = 1 рубль.
    /// </para>
    /// <para>
    /// В Unity Editor: VHR → Тестирование в Editor. Simulation — локальный баланс и
    /// диалог подтверждения; Live — настоящий сервер с песочным ключом (тестовые монеты).
    /// </para>
    /// Повесьте на GameObject и задайте поля в инспекторе.
    /// </summary>
    public sealed class EconomySample : MonoBehaviour
    {
        // GameId — ID игры из кабинета разработчика на сайте (страница /dev/games).
        // Это НЕ секрет. В Editor (Live) SDK возьмёт id игры из песочного ключа.
        [SerializeField] private string gameId = "demo-game";

        private readonly CompositeDisposable _subs = new();
        private CancellationTokenSource _cts;

        private async void Start()
        {
            _cts = new CancellationTokenSource();

            var options = new VhrSdkOptions
            {
                GameId = gameId,
                VerboseLogging = true
                // Токен игрока SDK получает сам от страницы платформы — ничего
                // секретного в сборку класть не нужно (InternalApiKey тут НЕ задаём).
            };

            await VhrSdk.InitializeAsync(options, ct: _cts.Token);

            // Баланс для HUD: после покупок и после пополнения на платформе.
            VhrSdk.Economy.OnBalanceChanged += coins => Debug.Log($"[Sample] баланс: {coins}");
            // То же через R3:
            VhrSdk.Economy.BalanceChanged
                .Subscribe(e => Debug.Log($"[Sample] balance={e.NewBalance} (Δ{e.Delta}, {e.Reason})"))
                .AddTo(_subs);

            var balance = await VhrSdk.Economy.GetBalanceAsync(null, _cts.Token);
            Debug.Log($"[Sample] стартовый баланс = {balance?.coins}");

            // Каталог товаров игры (заводится в кабинете: игра → Товары) и что уже куплено.
            var items = await VhrSdk.Economy.GetItemsAsync(_cts.Token);
            var owned = await VhrSdk.Economy.GetOwnedItemsAsync(_cts.Token);
            foreach (var it in items)
            {
                var have = owned.FirstOrDefault(o => o.itemId == it.id)?.quantity ?? 0;
                Debug.Log($"[Sample] {it.title} — {it.priceCoins} монет (куплено: {have}) id={it.id}");
            }

            if (items.Length > 0 && VhrSdk.Economy.IsPurchaseAvailable)
                await BuyAsync(items[0].id);
        }

        /// <summary>Кнопка «Купить»: игрок подтверждает покупку в окне платформы.</summary>
        public async System.Threading.Tasks.Task BuyAsync(string itemId)
        {
            // Стабильный externalId защищает от двойной покупки при повторе/обрыве сети.
            var r = await VhrSdk.Economy.PurchaseAsync(null, itemId, quantity: 1,
                externalId: $"sample-{itemId}-001", ct: _cts.Token);

            switch (r.Status)
            {
                case VhrPurchaseStatus.Completed:
                    Debug.Log($"[Sample] куплено! purchaseId={r.PurchaseId}, баланс={r.Balance}");
                    // TODO: выдать товар игроку.
                    break;
                case VhrPurchaseStatus.InsufficientFunds:
                    Debug.Log("[Sample] не хватает монет — предлагаем пополнить на платформе");
                    VhrSdk.Economy.OpenTopUp();
                    break;
                case VhrPurchaseStatus.Cancelled:
                case VhrPurchaseStatus.Expired:
                    Debug.Log("[Sample] игрок передумал / не успел подтвердить");
                    break;
                case VhrPurchaseStatus.Forbidden:
                    Debug.Log($"[Sample] покупка запрещена платформой: {r.ErrorCode}"); // parental_block, daily_limit…
                    break;
                case VhrPurchaseStatus.SdkUpdateRequired:
                    Debug.LogError("[Sample] обновите пакет ru.vhrgames.sdk");
                    break;
                default:
                    Debug.LogWarning($"[Sample] ошибка покупки: {r.ErrorCode} {r.Message}");
                    break;
            }
        }

        /// <summary>Кнопка «Пополнить»: окно пополнения откроет сама платформа.</summary>
        public void OnTopUpClicked() => VhrSdk.Economy.OpenTopUp();

        private void OnDestroy()
        {
            _subs.Dispose();
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}
