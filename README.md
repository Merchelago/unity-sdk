# VHR Games SDK (Unity 6)

`ru.vhrgames.sdk` — **обязательный** пакет интеграции для каждой игры VHR Games
на Unity 6 (WebGL). Он предоставляет сервисы экономики, лидербордов, привязки
серверов и сессии, которые общаются с backend-мостом VHR, и — что критично —
эмитит **маркер сборки** (`vhr-sdk.json`), который бэкенд проверяет при загрузке
сборки.

> ⛔ **Нет маркера → загрузка отклонена.** Сборка, собранная без этого SDK, не
> содержит `vhr-sdk.json`. На шаге `confirm-upload` бэкенд вернёт `sdk_required`
> и сборка **не будет опубликована**. Это сделано намеренно: маркер пишет
> только SDK, поэтому его интеграция не опциональна.

- **Unity:** 6000.0+
- **Реактивность:** [R3](https://github.com/Cysharp/R3) (Cysharp Reactive Extensions)
- **DI:** [VContainer](https://github.com/hadashiA/VContainer)
- **Неймспейс:** `VhrGames.Sdk` (runtime), `VhrGames.Sdk.Editor` (build-хуки и окна `VHR → …`)

---

## Почему SDK обязателен

Издательский конвейер VHR отказывает любой сборке, для которой нельзя
подтвердить интеграцию SDK. Подтверждение = файл-маркер `vhr-sdk.json` внутри
загруженного WebGL-артефакта.

| Результат бэкенда на confirm-upload | Причина | Решение |
|---|---|---|
| `sdk_required` | В сборке нет `vhr-sdk.json` | Установите этот пакет — он сам пишет маркер при сборке |
| `sdk_outdated` | Версия `sdkVersion` в маркере ниже минимума бэкенда | Обновите пакет и пересоберите |

Полный контракт маркера и справочник API — в
[Documentation~/index.md](Documentation~/index.md).

---

## Где взять данные

| Что | Где взять | Это секрет? |
|---|---|---|
| **`GameId`** | ID игры из **кабинета разработчика** на сайте, страница `/dev/games`. Присваивается автоматически при загрузке игры. | Нет. Просто идентификатор. |
| **Авторизация (JWT игрока)** | Сайт VHR сам прокидывает JWT игрока во встроенную WebGL-игру через query-параметр `?access_token=...` на странице. SDK берёт его автоматически — дефолтный `TokenProvider` на WebGL читает `access_token` из URL страницы (`Application.absoluteURL`). **Разработчику НИЧЕГО секретного в сборку класть не нужно.** | Токен принадлежит игроку и выдаётся сайтом в рантайме; в сборку не зашивается. |
| **`InternalApiKey`** | Только для **серверных игр / server-to-server** (выделенный сервер). Выдаётся отдельно для серверной интеграции. | **Да.** **НИКОГДА** не кладите в клиентскую WebGL-сборку — публичный билд распространяется игрокам, секрет утечёт. В клиенте поле оставляйте пустым. |

**Кратко по модели авторизации:** клиентская WebGL-игра авторизуется
**только JWT игрока**. Никакого общего ключа в публичной сборке нет. Мост
(`GameBridgeMS`) принимает `Authorization: Bearer <jwt>` и привязывает каждую
операцию экономики к пользователю из токена (self-операции). `userId` для
self-операций можно передавать своим (рекомендуется) или оставлять пустым —
мост резолвит игрока по токену.

В **Unity Editor** токен игрока не нужен: для проверки всех API на настоящем
сервере используйте песочный ключ (см. «Тестирование в Unity Editor» ниже). Для
**нативных** сборок задавайте `TokenProvider` явно.

**Игровой токен (SDK 1.9.0).** На платформе хост передаёт игре **игровой** токен
(audience `…#game`, claims `gid`, `sub`, срок 60 минут) сообщением `vhr:sdk:token`.
SDK сам просит свежий (`vhr:sdk:token-request`) за 5 минут до истечения и при `401`,
дожидается его и повторяет запрос — **прозрачно для кода игры, делать ничего не нужно**. Детали — в
[Documentation~/index.md](Documentation~/index.md) (раздел про жизненный цикл
токена). Для нативных сборок просто отдавайте из `TokenProvider` актуальный
(обновляемый вашей системой авторизации) токен.

---

## Экономика: игроки пополняют счёт на платформе, а в играх только тратят

**1 монета = 1 рубль.** Поэтому правила строгие и проверяются сервером, а не честным словом игры:

| Операция | Из игры (WebGL) | Как правильно |
|---|---|---|
| `GetBalanceAsync` — баланс | ✅ | — |
| `GetItemsAsync` / `GetOwnedItemsAsync` — каталог и купленное | ✅ | — |
| `PurchaseAsync` — купить **товар каталога** | ✅ **игрок подтверждает в окне платформы** | — |
| `OpenTopUp` — «Пополнить» | ✅ открывает окно **платформы** | — |
| Пополнение внутри игры | ⛔ запрещено | `OpenTopUp()` |
| `SpendAsync` — прямое списание | ⛔ отключено (`spend_disabled`, сервер — `403`) | товар каталога + `PurchaseAsync` |
| `GrantCoinsAsync` — начисление монет | ⛔ отключено (`grant_disabled`, сервер — `403`) | внутриигровые награды |
| `GrantAchievementAsync` — факт анлока ачивки | ✅, монетная награда = 0 | — |

---

## Покупки в играх (SDK 1.9.0+)

Игрок покупает **только товары каталога игры**, которые разработчик завёл в кабинете на сайте
(игра → Товары), и **каждую покупку подтверждает сам** в окне платформы. Игра это окно не рисует
и подделать не может.

```
Игра: PurchaseAsync(itemId) ──POST /bridge/api/purchase──► сервер: намерение (живёт 120 с)
      ◄── 202 { status:"confirmation_required", intentId, title, price, ... }
Игра ──postMessage { type:'vhr:purchase:confirm', intentId, requestId }──► страница vhrgames.ru
                                    окно платформы: «Купить „Меч“ за 150 монет?»
Игра ◄──{ type:'vhr:purchase:result', requestId, intentId, status, balance?, purchaseId? }──
      status: completed | cancelled | insufficient_funds | expired | error
```

Повтор с тем же `externalId` (обрыв сети, перезапуск) не спишет монеты дважды: сервер сразу
вернёт `{ status:"completed", purchaseId, balance }`. SDK ждёт ответа окна до 3 минут; нет
ответа — `Expired`. Одновременно идёт одна покупка.

### Пример магазина

```csharp
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using VhrGames.Sdk;

public class Shop : MonoBehaviour
{
    [SerializeField] private Button topUpButton;
    [SerializeField] private Text balanceText;

    private async void Start()
    {
        // Баланс для HUD: после покупок и после пополнения на платформе.
        VhrSdk.Economy.OnBalanceChanged += coins => balanceText.text = coins.ToString();
        balanceText.text = (await VhrSdk.Economy.GetBalanceAsync(null))?.coins.ToString();

        // «Пополнить» — окно пополнения откроет сама платформа.
        topUpButton.onClick.AddListener(() => VhrSdk.Economy.OpenTopUp());

        // Вне vhrgames.ru (и в нативных сборках) покупать негде — прячем магазин.
        gameObject.SetActive(VhrSdk.Economy.IsPurchaseAvailable);

        var items = await VhrSdk.Economy.GetItemsAsync();        // каталог игры
        var owned = await VhrSdk.Economy.GetOwnedItemsAsync();   // что уже куплено
        foreach (var item in items)
        {
            bool have = owned.Any(o => o.itemId == item.id);
            CreateShopRow(item.title, item.priceCoins, have, () => Buy(item.id));
        }
    }

    private async void Buy(string itemId)
    {
        // Свой стабильный externalId (например, id заказа в вашей игре) защищает от двойной покупки при повторе.
        var r = await VhrSdk.Economy.PurchaseAsync(null, itemId, externalId: NewOrderId(itemId));

        switch (r.Status)
        {
            case VhrPurchaseStatus.Completed:          // монеты списаны — выдаём товар
                GiveItem(itemId);
                break;
            case VhrPurchaseStatus.InsufficientFunds:  // не хватает монет — пополнить на платформе
                VhrSdk.Economy.OpenTopUp();
                break;
            case VhrPurchaseStatus.Cancelled:          // игрок закрыл окно
            case VhrPurchaseStatus.Expired:            // не успел подтвердить (120 с)
                break;
            case VhrPurchaseStatus.Forbidden:          // parental_block, daily_limit, game_mismatch…
                ShowMessage("Покупка сейчас недоступна");
                Debug.Log(r.ErrorCode);
                break;
            case VhrPurchaseStatus.SdkUpdateRequired:  // старый SDK — обновите пакет
                Debug.LogError(r.Message);
                break;
            default:                                   // Error: сеть, not_hosted, busy…
                Debug.LogWarning($"{r.ErrorCode}: {r.Message}");
                break;
        }
    }

    // Без async — колбэк-перегрузка:
    public void BuyWithCallback(string itemId) =>
        VhrSdk.Economy.Purchase(itemId, 1, r => { if (r.IsCompleted) GiveItem(itemId); });

    private string NewOrderId(string itemId) => $"{itemId}-{System.Guid.NewGuid():N}";
    private void CreateShopRow(string title, long price, bool owned, System.Action onBuy) { /* ваш UI */ }
    private void GiveItem(string itemId) { /* выдать товар */ }
    private void ShowMessage(string text) { /* ваш UI */ }
}
```

| Статус | Что значит | Что делать |
|---|---|---|
| `Completed` | Игрок подтвердил, монеты списаны | Выдать товар (`IsCompleted`) |
| `Cancelled` | Игрок закрыл окно / «Отмена» | Ничего |
| `InsufficientFunds` | Не хватает монет | Предложить `OpenTopUp()` |
| `Expired` | Не подтвердил за 120 с / окно не ответило за 3 мин | Предложить повторить |
| `Forbidden` | `parental_block`, `daily_limit`, `game_token_forbidden`, `game_mismatch` (в `ErrorCode`) | Сообщить игроку; проверить `GameId` |
| `SdkUpdateRequired` | Сервер требует новый SDK (`426`) | Обновить пакет и пересобрать |
| `Error` | Сеть, `not_hosted` (игра не на vhrgames.ru), `busy`, неизвестный товар | Лог, повтор позже |

`PurchaseAsync` не бросает исключений (кроме `OperationCanceledException` при отмене `ct`).

## Что запрещено

- **Пополнение счёта в игре** — свои окна, кнопки «купить монеты», «пакеты монет». Пополнение
  только на платформе: `VhrSdk.Economy.OpenTopUp()`.
- **Прямые списания и начисления монет** — `SpendAsync` и `GrantCoinsAsync` отключены
  (сервер отвечает `403`). Платить можно только за товары каталога через `PurchaseAsync`.
- **Сторонние платёжные системы** (свой эквайринг, другие платёжные сервисы, криптовалюта и т.п.) и
  **внешние ссылки на оплату** — запрещены и блокируются платформой; сетевые запросы игры к
  внешним доменам блокируются.
- **Покупка без подтверждения игроком** — невозможна: намерение подтверждает только окно платформы.

---

## Каталог игры: товары и достижения

**Где завести.** Сайт → кабинет разработчика → игра → **Товары** и **Достижения**
(`https://vhrgames.ru/dev/games`, ачивки — `/dev/games/{gameId}/achievements`). Создание и
редактирование — только там (нужен вход разработчика).

**Как прочитать в игре** (id игры SDK берёт сам — из токена или `GameId`):

```csharp
VhrCatalogItem[] items = await VhrSdk.Economy.GetItemsAsync();        // id, code, title, description, priceCoins
VhrOwnedItem[]  owned = await VhrSdk.Economy.GetOwnedItemsAsync();    // itemId, quantity, lastPurchasedAt
VhrAchievement[] all  = await VhrSdk.Achievements.GetForCurrentGameAsync();
VhrUserAchievement[] mine = await VhrSdk.Achievements.GetMineForCurrentGameAsync();
```

`GetOwnedItemsAsync` нужен для разовых покупок (не продавать второй раз), восстановления покупок
на новом устройстве и разблокировок. В `PurchaseAsync` передавайте **`item.id`**.

**Как увидеть в Unity Editor.** Окно **`VHR → Каталог игры`** (нужен песочный ключ из окна
«Тестирование в Editor»): вкладки «Товары» и «Достижения» с ID, названием, ценой/описанием,
иконкой и статусом, «Копировать ID», «Обновить», «Открыть в кабинете». Кнопка
**«Сгенерировать C#-константы»** создаёт `VhrCatalogIds.cs` (повторная генерация перезаписывает):

```csharp
await VhrSdk.Economy.PurchaseAsync(null, VhrCatalogIds.Items.SwordGold);
bool boss = mine.Any(u => u.achievement?.code == VhrCatalogIds.Achievements.FirstBoss);
```

---

## Тестирование в Unity Editor

Все API SDK можно проверить в Editor **на настоящем сервере** до выгрузки на платформу — от имени
тестового игрока `sbx_*` с **10 000 тестовых монет** (реальные деньги не тратятся).

1. **Ключ.** Сайт → кабинет разработчика → игра → **«Тест в Unity Editor»** → скопируйте песочный
   ключ (действует 30 дней).
2. **Окно.** Unity → меню **`VHR → Тестирование в Editor`** → вставьте ключ → **«Проверить»**
   (покажет игру, тестового игрока, баланс и срок ключа). «Проверить все API» прогонит read-only
   запросы ко всем сервисам.
3. **Live.** Переключите режим на **«Live (песочница)»** и нажмите Play. SDK сам подставит ключ
   (`Authorization: Bearer`) и `GameId` из ключа; адреса — те же, что в сборке.
4. **Тест.** Покупки (`PurchaseAsync` покажет диалог-имитацию окна платформы → confirm/cancel на
   сервере), лидерборды, ачивки, профиль («Тестовый игрок»), друзья, лобби и релей.
5. **«Сбросить тестовые данные»** — баланс снова 10 000, покупки и инвентарь по игре очищены.

| Режим | Что происходит |
|---|---|
| **Simulation** (по умолчанию, если ключа нет) | Экономика локальная: баланс 10 000, диалог подтверждения, цены — `SimulatedItemPrices` / `SimulatedDefaultItemPrice`; реклама — симуляция; остальные сервисы — как в 1.8 (HTTP с вашим `TokenProvider`) |
| **Live (песочница)** | Все сервисы — настоящий сервер с песочным ключом; реклама — локальная симуляция (доход не репортится) |

Режим задаётся в окне; из кода — `VhrSdkOptions.EditorMode = VhrEditorMode.Auto | Simulation | LiveSandbox`
(`Auto` — как в окне). В сборках режим не используется.

> 🔒 **Ключ работает только в Unity Editor и никогда не попадает в сборку.** Он хранится в
> `EditorPrefs` этого компьютера (отдельно для проекта), читается только под `#if UNITY_EDITOR` и не
> сериализуется ни в сцену, ни в ScriptableObject, ни в ProjectSettings. Перед сборкой SDK проверяет
> проект и предупредит, если ключ всё-таки вставили в ассет или код. Не коммитьте ключ.

Поле «Сервер» в окне — для отладки на своём dev-стенде (по умолчанию прод).

---

## Миграция с 1.8 на 1.9

- **`SpendAsync` / `GrantCoinsAsync` устарели** (`[Obsolete]`) и сразу возвращают
  `success = false` (`code = "spend_disabled"` / `"grant_disabled"`) без запроса к серверу. Заведите
  товары в кабинете и продавайте их через `PurchaseAsync`; награды — внутриигровые.
- **Покупки теперь с подтверждением игрока.** `PurchaseAsync` возвращает `VhrPurchaseResult`;
  проверяйте `r.Status` / `r.IsCompleted`. Старый код (`r.success`, `r.balance`,
  `VhrEconomyResult r = await PurchaseAsync(...)`) компилируется с предупреждением, но учтите новые
  исходы: `Cancelled`, `Expired`, `Forbidden`.
- `userId` в `PurchaseAsync`/`GetBalanceAsync` можно передавать `null` — игрок берётся из токена.
- Кнопку «Пополнить» ведите на `OpenTopUp()`, баланс в HUD — по `OnBalanceChanged`.
- **Старые версии SDK получают `426 sdk_update_required`** на покупках — обновите пакет до 1.9.0
  и пересоберите игру.

---

## Установка

Пакет **не** содержит R3 и VContainer. Установите их первыми.

### 1. Добавьте scoped-реестр OpenUPM

`Edit ▸ Project Settings ▸ Package Manager ▸ Scoped Registries`:

```
Name:   OpenUPM
URL:    https://package.openupm.com
Scopes: com.cysharp.r3
        jp.hadashikick.vcontainer
```

### 2. Установите зависимости (Package Manager ▸ My Registries)

- **VContainer** — `jp.hadashikick.vcontainer`
- **R3** — `com.cysharp.r3`
  R3 также нужны его core managed-DLL (`R3`, `ObservableCollections`,
  `System.Threading.Channels`, ...). Установите их через **NuGetForUnity**
  (пакет `R3`) или из `.unitypackage` со страницы релизов R3 — по инструкции R3.
  Для интеграции с Unity также добавьте `R3.Unity` (идёт в составе UPM-пакета R3).

### 3. Установите этот SDK

`Package Manager ▸ + ▸ Add package from git URL…`:

```
https://github.com/Merchelago/unity-sdk.git#v1.9.0
```

или добавьте в `Packages/manifest.json`:

```json
"ru.vhrgames.sdk": "https://github.com/Merchelago/unity-sdk.git#v1.9.0"
```

---

## Быстрый старт

### Вариант A — без DI (статический фасад)

```csharp
using VhrGames.Sdk;
using R3;

// Клиентская WebGL-сборка: только GameId. Токен игрока SDK получает сам от
// страницы платформы. Никакого InternalApiKey в клиентскую сборку!
var options = new VhrSdkOptions
{
    GameId = "your-game-id"   // ID из кабинета разработчика, страница /dev/games
};

await VhrSdk.InitializeAsync(options);

VhrSdk.ConnectionState.Subscribe(s => Debug.Log($"SDK: {s}"));
VhrSdk.Economy.OnBalanceChanged += coins => hud.SetCoins(coins);

var bal = await VhrSdk.Economy.GetBalanceAsync(null);      // игрок — из токена

// Покупка товара каталога — игрок подтверждает её в окне платформы:
var r = await VhrSdk.Economy.PurchaseAsync(null, "<id товара из каталога>");
if (r.IsCompleted) GiveItem();

// «Пополнить» — только на платформе:
VhrSdk.Economy.OpenTopUp();

// ⛔ Пополнять, списывать (SpendAsync) и начислять (GrantCoinsAsync) монеты из игры нельзя.
```

> **Unity Editor:** токен не нужен — `VHR → Тестирование в Editor` (Simulation или
> Live-песочница с ключом). **Нативная сборка** (нет `?access_token` в URL): задайте
> провайдер токена явно — `TokenProvider = () => MyHostAuth.CurrentPlayerJwt`.
>
> **Серверная игра** (server-to-server): тогда и только тогда добавьте
> `InternalApiKey = "..."`. В клиентских билдах это запрещено.

### Вариант B — VContainer

Добавьте `VhrSdkLifetimeScope` на bootstrap-GameObject, задайте **Game Id** в
инспекторе (поле Internal API Key в клиентской WebGL-сборке оставьте **пустым**
— оно только для серверных игр; **Editor Mode** — `Auto`, режим берётся из окна
`VHR → Тестирование в Editor`), затем внедряйте через конструктор где угодно:

```csharp
public sealed class Hud : IStartable
{
    private readonly IVhrEconomy _economy;
    public Hud(IVhrEconomy economy) => _economy = economy;

    public void Start() =>
        _economy.BalanceChanged.Subscribe(e => Render(e.NewBalance));
}
```

Scope регистрирует `VhrSdkEntryPoint`, который сам вызывает `InitializeAsync` и
также биндит статический фасад (гибридный режим), так что код библиотек может
использовать любой путь.

---

## Реклама в игре (`VhrSdk.Ads`, SDK 1.8.0+)

Игра может показывать полноэкранную рекламу **Рекламной сети Яндекса** двух видов:

| Вид | Метод | Когда показывать | Награда |
|---|---|---|---|
| Interstitial | `ShowInterstitialAsync()` | В естественной паузе: между уровнями, после поражения, при выходе в меню. Не посреди активного геймплея | Нет — успешный исход `Closed` |
| Rewarded | `ShowRewardedAsync()` | **Только по явному согласию игрока** — кнопка «Посмотреть рекламу за награду» (требование РСЯ) | Если досмотрел — `Rewarded` |

Рекламу рисует **сайт VHR** поверх игры (игра в iframe лишь просит показ через
`postMessage`), и он же ведёт учёт показов. Свои рекламные SDK и скрипты в сборку
встраивать не нужно, репортить показы из игры — тоже.

### Награда — только внутриигровая

> ⚠️ SDK лишь сообщает, что просмотр засчитан (`VhrAdStatus.Rewarded`). Награду
> выдаёт **сама игра** — жизнь, сундук, удвоение очков. **Платформенные монеты
> (= реальные рубли) за рекламу не начисляются** — ни через `IVhrAds`, ни через
> экономику (anti-mint).

### Пример: кнопка «Посмотреть рекламу за награду» и реклама между уровнями

```csharp
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using VhrGames.Sdk;

public class AdsExample : MonoBehaviour
{
    [SerializeField] private Button watchAdButton;

    private void Start()
    {
        // Вариант 1: встроенная авто-пауза (timeScale = 0, звук на паузе, курсор свободен).
        VhrSdk.Ads.AutoPause = true;

        // Вариант 2: своя пауза — через события (парные: на каждый Opened придёт Closed).
        // VhrSdk.Ads.OnAdOpened += kind => myGame.Pause();
        // VhrSdk.Ads.OnAdClosed += result => myGame.Resume();

        // Вне сайта VHR (WebGL открыт напрямую) рекламы не будет — кнопку не показываем.
        watchAdButton.gameObject.SetActive(VhrSdk.Ads.IsSupported);
        watchAdButton.onClick.AddListener(OnWatchAdClicked);
    }

    // Rewarded — только по нажатию игрока.
    private async void OnWatchAdClicked()
    {
        watchAdButton.interactable = false;
        var result = await VhrSdk.Ads.ShowRewardedAsync();
        switch (result.Status)
        {
            case VhrAdStatus.Rewarded:
                PlayerLives.Add(1);          // СВОЯ внутриигровая награда
                break;
            case VhrAdStatus.Unavailable:    // нет рекламы / адблок / детский аккаунт
                watchAdButton.gameObject.SetActive(false);
                break;
            case VhrAdStatus.Cooldown:       // лимит частоты — предложим позже
            case VhrAdStatus.Closed:         // закрыл раньше — награды нет
            case VhrAdStatus.Error:
                break;
        }
        watchAdButton.interactable = true;
    }

    // Interstitial между уровнями: результат не важен — игра просто продолжается.
    public async Task OnLevelCompletedAsync()
    {
        await VhrSdk.Ads.ShowInterstitialAsync();
        LoadNextLevel();
    }

    // Без async — колбэк-перегрузка:
    public void OnChestOffer() =>
        VhrSdk.Ads.ShowRewarded(r => { if (r.IsRewarded) OpenBonusChest(); });
}
```

### Результаты (`VhrAdStatus`) и гарантии

| Статус | Значение | Что делать |
|---|---|---|
| `Rewarded` | Rewarded досмотрен | Выдать свою награду |
| `Closed` | Реклама показана и закрыта (rewarded — раньше времени) | Продолжить игру; награды нет |
| `Unavailable` | Реклама выключена, детский аккаунт, адблок, нет объявления, игра не на vhrgames.ru | Продолжить игру / спрятать кнопку |
| `Cooldown` | Лимит частоты или уже идёт другой показ | Предложить позже |
| `Error` | Техническая ошибка | Продолжить игру |

- Методы **не бросают исключений** (кроме `OperationCanceledException` при отмене
  `ct`) и **всегда завершаются**: хост не ответил за 5 с → `Unavailable`, показ
  завис → `Closed` максимум через 5 мин. Игра не зависнет на паузе.
- Одновременно идёт один показ. `OnAdOpened` приходит только если реклама
  действительно открывается; на каждый `OnAdOpened` — ровно один `OnAdClosed`.
- В шутерах с pointer lock освобождайте курсор в `OnAdOpened` (авто-пауза делает
  это сама), иначе игрок не сможет закрыть рекламу. Полноэкранный режим браузера
  сайт перед показом снимает сам.

### Правила частоты

- **Interstitial** — не чаще одного раза в N секунд (по умолчанию **90 с**);
- **Rewarded** — не больше M показов в час (по умолчанию **20**);
- значения задаёт платформа и может менять без обновления SDK; превышение →
  `Cooldown`. Дополнительно сама Рекламная сеть Яндекса может ограничивать частоту
  полноэкранных блоков — тогда `Unavailable`;
- лимиты считаются на игрока (браузер), а не на игру, и перепроверяются сервером.

### Доход разработчика

- Вы получаете **10% дохода платформы от рекламы, показанной в вашей игре**
  (interstitial, rewarded и преролл перед запуском игры, если он включён).
- Засчитываются только **реальные показы**, которые сайт подтвердил по факту
  отрисовки рекламы, и только авторизованным игрокам; повторы и накрутку сервер
  отсекает. Игре ничего репортить не нужно.
- Начисления видны на сайте в разделе «Доходы» (`/dev/earnings`) как ожидающие
  подтверждения и становятся **доступны к выводу после закрытия отчётного периода**,
  когда платформа сверит показы со статистикой Рекламной сети Яндекса.

> `IVhrEconomy.ReportAdAsync` (отчёт без реального показа) помечен `[Obsolete]`:
> **доход по нему больше не засчитывается**. Используйте `VhrSdk.Ads`.

### Редактор и не-WebGL сборки

Реклама симулируется: в консоль пишется лог, через `AdsSimulationDelaySeconds`
(по умолчанию 1.5 с реального времени) приходит результат по режиму
`VhrSdkOptions.AdsSimulation`:

```csharp
var options = new VhrSdkOptions
{
    GameId = "<id из /dev/games>",
    AdsAutoPause = true,
    AdsSimulation = VhrAdSimulationMode.Success,   // ClosedEarly | Unavailable | Cooldown | Error
    AdsSimulationDelaySeconds = 1.5f
};
```

Протокол между игрой и сайтом (для справки, SDK делает всё сам):
`{ type:'vhr:ads:show', kind:'interstitial'|'rewarded', requestId }` →
`{ type:'vhr:ads:opened', requestId }` →
`{ type:'vhr:ads:result', requestId, status:'rewarded'|'closed'|'error'|'unavailable'|'cooldown' }`.

---

## Публичный API кратко

| Сервис | Ключевые методы |
|---|---|
| `IVhrEconomy` | `GetBalanceAsync`, `GetItemsAsync`, `GetOwnedItemsAsync`, `PurchaseAsync` → `VhrPurchaseResult` (+ колбэк `Purchase`), `OpenTopUp`, `IsPurchaseAvailable` · событие `OnBalanceChanged` и `Observable<BalanceChanged> BalanceChanged` · `SpendAsync`/`GrantCoinsAsync` — **отключены** (`[Obsolete]`) |
| `IVhrAchievements` | `GetForGameAsync`, `GetForCurrentGameAsync`, `GetMineAsync`, `GetMineForCurrentGameAsync`, `GetMyGamesAsync`, `GetPlatformAsync` |
| `IVhrLeaderboard` | `SubmitAsync`, `GetTopAsync` (seam следующей волны, терпим к 501) |
| `IVhrServers` | `BindAsync`, `ListBindingsAsync`, `RequestInstanceAsync` (по умолчанию noop) |
| `IVhrAds` | `ShowInterstitialAsync`, `ShowRewardedAsync` (+ колбэк-версии) → `VhrAdResult { Status }` · события `OnAdOpened` / `OnAdClosed` · `AutoPause`, `IsSupported`, `IsShowing` |
| `IVhrSession` | `GameId`, `CurrentToken`, `State`, `Observable<VhrConnectionState> StateChanged` |
| `VhrSdk` (static) | `InitializeAsync`, `Economy`, `Ads`, `Leaderboard`, `Servers`, `Session`, `ConnectionState` |

Покупки **идемпотентны** через `externalId` (по умолчанию — новый GUID);
передайте свой стабильный id, чтобы повтор не купил дважды.

Меню Editor: **`VHR → Тестирование в Editor`** (песочный ключ, Simulation / Live,
проверка API, сброс), **`VHR → Каталог игры`** (товары и достижения, генерация
`VhrCatalogIds.cs`), `VHR → Собрать серверный билд`.

Полные форматы запросов/ответов: [Documentation~/index.md](Documentation~/index.md).

---

## Лицензия

См. [LICENSE.md](LICENSE.md). © VHR Games.
