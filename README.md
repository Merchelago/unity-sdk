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
| `sdk_outdated` | Версия `sdkVersion` в маркере ниже минимума бэкенда (`minSupported`) | `VHR → Обновление SDK` → «Обновить», затем пересоберите (см. [Обновление SDK](#обновление-sdk)) |

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

> **Премиум-игры (SDK 1.13.0+):** в демо-версии премиум-игры товары каталога не продаются —
> `PurchaseAsync` сразу возвращает `Forbidden` (`demo_access`). Сначала игрок покупает полную версию —
> см. [Премиум-игры](#премиум-игры-vhrsdkaccess-sdk-1130).

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

**Как выдать достижение** (SDK 1.12.0+) — код из кабинета или полный код из `VhrCatalogIds`:

```csharp
var r = await VhrSdk.Achievements.UnlockAsync("first_boss");   // или VhrCatalogIds.Achievements.FirstBoss
if (r.unlocked) ShowToast(r.achievement.title);                // впервые; уже открытое — unlocked = false
// r.queued — нет связи: откроется само, когда связь вернётся (см. «Работа без связи»)
```

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
   запросы ко всем сервисам и цикл облачных сохранений на тестовом слоте.
3. **Live.** Переключите режим на **«Live (песочница)»** и нажмите Play. SDK сам подставит ключ
   (`Authorization: Bearer`) и `GameId` из ключа; адреса — те же, что в сборке.
4. **Тест.** Покупки (`PurchaseAsync` покажет диалог-имитацию окна платформы → confirm/cancel на
   сервере), лидерборды, ачивки, профиль («Тестовый игрок»), друзья, лобби и релей.
5. **«Сбросить тестовые данные»** — баланс снова 10 000, покупки и инвентарь по игре очищены.

| Режим | Что происходит |
|---|---|
| **Simulation** (по умолчанию, если ключа нет) | Экономика локальная: баланс 10 000, диалог подтверждения, цены — `SimulatedItemPrices` / `SimulatedDefaultItemPrice`; реклама — симуляция; облачные сохранения — локально в `Library/VhrSdk/SimSaves` с лимитами сервера; остальные сервисы — как в 1.8 (HTTP с вашим `TokenProvider`) |
| **Live (песочница)** | Все сервисы (и облачные сохранения) — настоящий сервер с песочным ключом; реклама — локальная симуляция (доход не репортится) |

Режим задаётся в окне; из кода — `VhrSdkOptions.EditorMode = VhrEditorMode.Auto | Simulation | LiveSandbox`
(`Auto` — как в окне). В сборках режим не используется.

**Премиум-игра (SDK 1.13.0+).** В том же окне — «Премиум-игра: доступ игрока»: **Regular / Premium Full /
Premium Demo / Premium None**, цена и «входит в подписку». «Симулировать покупку» во время Play присылает
игре `AccessChanged`, как после окна покупки на сайте. Доступ в Editor всегда симулируется (и в Simulation,
и в Live) — см. [Премиум-игры](#премиум-игры-vhrsdkaccess-sdk-1130).

> 🔒 **Ключ работает только в Unity Editor и никогда не попадает в сборку.** Он хранится в
> `EditorPrefs` этого компьютера (отдельно для проекта), читается только под `#if UNITY_EDITOR` и не
> сериализуется ни в сцену, ни в ScriptableObject, ни в ProjectSettings. Перед сборкой SDK проверяет
> проект и предупредит, если ключ всё-таки вставили в ассет или код. Не коммитьте ключ.

Поле «Сервер» в окне — для отладки на своём dev-стенде (по умолчанию прод).

---

## ИИ-ассистенты (MCP)

У платформы VHR есть MCP-сервер (Model Context Protocol) для ИИ-ассистентов: Claude Code, Cursor,
VS Code (Copilot). Подключённый ассистент:

- читает документацию SDK и правила платформы: требования к сборке, модерация, экономика, реклама, премиум;
- видит ваши студии и игры: статус, модерацию, текущий релиз, версию SDK в сборке, отчёт интеграции SDK;
- ведёт каталог: ачивки, товары и лидерборды — создание, правка, CSV-импорт с предпросмотром. Коды не меняются никогда;
- смотрит статистику, отзывы, доходы (только чтение) и квоту облачных сохранений;
- выкладывает обновление сборки — дальше обычная модерация, как при загрузке с сайта.

Деньги, выплаты, покупки и админские действия через MCP недоступны.

| Сервер | Адрес | Вход |
|---|---|---|
| `vhr` | `https://api.vhrweb.ru/mcp` | личный токен в заголовке `Authorization: Bearer` |
| `vhr-docs` | `https://api.vhrweb.ru/mcp/docs` | без входа: только документация и правила, без ваших данных |

### Личный токен

1. **Создайте токен.** Кабинет разработчика → «ИИ-ассистенты (MCP)» — `https://vhrgames.ru/dev/mcp`.
   Выберите права и срок. Токен вида `vhr_pat_…` показывается один раз. Права:
   - `mcp:read` — чтение, есть всегда;
   - `mcp:catalog` — ачивки, товары и лидерборды, только owner/admin студии;
   - `mcp:publish` — загрузка сборки и заявка на премиум, только owner/admin студии.

   Там же видно, когда токен использовался, и там же его можно отозвать.
2. **Сохраните токен в переменную окружения `VHR_MCP_TOKEN`** — не в файл проекта:

   ```powershell
   # Windows, PowerShell
   [Environment]::SetEnvironmentVariable("VHR_MCP_TOKEN", "vhr_pat_…", "User")
   ```

   ```bat
   :: Windows, cmd
   setx VHR_MCP_TOKEN "vhr_pat_…"
   ```

   ```bash
   # macOS (zsh); в Linux с bash — ~/.bashrc
   echo 'export VHR_MCP_TOKEN="vhr_pat_…"' >> ~/.zshrc
   ```

   Без командной строки:
   - Windows — «Изменение переменных среды текущего пользователя»;
   - macOS и Linux — строка `export` в `~/.zshrc` или `~/.bashrc`.

   Команда с токеном остаётся в истории оболочки.
3. **Перезапустите терминал с Claude Code, Cursor или VS Code** — переменная читается при запуске.
   - В Windows закройте все окна программы.
   - В macOS Cursor и VS Code, запущенные из Dock, берут переменные из оболочки входа. Если ассистент
     переменную не видит, запустите редактор из терминала: `cursor .` или `code .`.

### Вариант 1 — окно SDK (Claude Code, Cursor, VS Code)

1. Откройте меню **`VHR → ИИ-ассистент (MCP)`** и отметьте клиентов.
2. Нажмите **«Создать файлы»**. Файлы появятся в корне Unity-проекта, рядом с `Assets/`:

| Клиент | Файл | Ключ серверов | Ссылка на токен |
|---|---|---|---|
| Claude Code | `.mcp.json` | `mcpServers`, `"type": "http"` | `${VHR_MCP_TOKEN}` |
| Cursor | `.cursor/mcp.json` | `mcpServers`, только `url` и `headers` | `${env:VHR_MCP_TOKEN}` |
| VS Code (Copilot) | `.vscode/mcp.json` | `servers`, `"type": "http"` | `${env:VHR_MCP_TOKEN}` |

Вот так выглядит `.mcp.json` для Claude Code:

```json
{
  "mcpServers": {
    "vhr": {
      "type": "http",
      "url": "https://api.vhrweb.ru/mcp",
      "headers": {
        "Authorization": "Bearer ${VHR_MCP_TOKEN}"
      }
    },
    "vhr-docs": {
      "type": "http",
      "url": "https://api.vhrweb.ru/mcp/docs"
    }
  }
}
```

- **Секретов в файлах нет**, только ссылка на переменную: значение подставляет сам клиент при запуске.
  Файлы можно коммитить, тогда ассистент подключится у всей команды, а каждый задаёт свою `VHR_MCP_TOKEN`.
  Не хотите хранить их в git — нажмите «Добавить в .gitignore». Кнопка доступна, если у проекта есть `.gitignore`.
- **Существующие файлы дополняются, а не перезаписываются:**
  - чужие серверы, `inputs`, комментарии и форматирование остаются;
  - записи `vhr` и `vhr-docs` добавляются или обновляются; повторный запуск ничего не меняет;
  - некорректный JSON сохраняется в `<файл>.vhr-backup-ГГГГММДД-ЧЧММСС`, затем файл создаётся заново.
- **Окно показывает:**
  - состояние файлов;
  - видна ли Unity переменная `VHR_MCP_TOKEN` — только префикс токена;
  - предупреждение, если в файле нашёлся токен в открытом виде.
- **После создания файлов:**
  - Claude Code запускайте из корня Unity-проекта. Он спросит, доверять ли серверам из `.mcp.json`, —
    разрешите; проверка — `/mcp`;
  - Cursor — Settings → MCP, включите `vhr` и `vhr-docs`;
  - VS Code — откройте `.vscode/mcp.json` и нажмите **Start** над сервером.

### Вариант 2 — командой (Claude Code)

```bash
claude mcp add --transport http vhr https://api.vhrweb.ru/mcp --header 'Authorization: Bearer ${VHR_MCP_TOKEN}'
claude mcp add --transport http vhr-docs https://api.vhrweb.ru/mcp/docs
```

- **Кавычки.** Одинарные — для bash, zsh и PowerShell: тогда `${VHR_MCP_TOKEN}` подставит сам Claude Code
  при запуске, а токен не окажется в его настройках. В cmd используйте двойные кавычки: cmd `${…}` не раскрывает.
- **Куда записывается.** По умолчанию сервер добавляется только вам и только для этого проекта, в настройки
  Claude Code. С `--scope project` команда пишет общий `.mcp.json`, как окно SDK.

В клиентах, где MCP-коннектор добавляется только по адресу, без своих заголовков, подключите публичный
`vhr-docs` (`https://api.vhrweb.ru/mcp/docs`).

### Если не работает

| Симптом | Причина и решение |
|---|---|
| `vhr` отвечает 401 | Клиент не видит `VHR_MCP_TOKEN`: переменная не задана, или клиент запущен до того, как её задали. Ещё причина — токен истёк или отозван. Claude Code показывает незаданную переменную в `claude mcp list` |
| 403 на изменении каталога или загрузке | У токена нет права `mcp:catalog` / `mcp:publish`, или вы не owner/admin студии этой игры. Детским аккаунтам MCP недоступен |
| Claude Code не видит серверы | Он запущен не из корня Unity-проекта, или серверы `.mcp.json` не одобрены. Сброс выбора — `claude mcp reset-project-choices` |
| Рядом с конфигом появился `*.vhr-backup-*` | В файле был некорректный JSON. Перенесите из бэкапа нужное и удалите его |

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

SDK зависит от **R3** (`com.cysharp.r3` + ядро `org.nuget.r3`) и **VContainer**
(`jp.hadashikick.vcontainer`). Всё ставится через Package Manager из двух реестров — OpenUPM и
UnityNuGet; NuGetForUnity и `.unitypackage` больше не нужны.

### Вариант 1 — установщик одним файлом (рекомендуется)

1. Скачайте [`VhrSdkInstaller.cs`](https://vhrgames.ru/downloads/VhrSdkInstaller.cs) (он же в
   репозитории — [`Installer~/VhrSdkInstaller.cs`](Installer~/VhrSdkInstaller.cs), кнопка «Raw»).
2. Перетащите файл в проект, в `Assets/Editor/` (подойдёт любая папка внутри `Assets`).
3. Unity скомпилирует его и спросит «Установить?» — нажмите **«Установить»**.

Установщик сам:
- узнаёт у сервера VHR последнюю версию SDK (нет связи — ставит встроенную в него версию);
- добавляет в `Packages/manifest.json` недостающие реестры **OpenUPM** и **UnityNuGet** и зависимости:
  SDK (git, `#v<версия>`), R3, ядро R3, VContainer. Чужие записи, их порядок и форматирование файла не
  трогает; копию прежнего файла кладёт в `Library/VhrSdkInstaller/`;
- находит копии R3 от NuGetForUnity / в `Assets/Plugins` и предлагает удалить их — иначе будут дубли
  сборок («Multiple precompiled assemblies with the same name R3.dll»);
- ждёт, пока Unity скачает пакеты, показывает итог и предлагает удалить себя — он больше не нужен.

Запустить ещё раз — меню **`VHR → Установить SDK`**: повторный запуск ничего не дублирует. Так же
переводится на новую схему проект, где SDK ≤ 1.9 и R3 стоят через NuGetForUnity.

### Вариант 2 — вручную

**1. Два реестра** — `Edit ▸ Project Settings ▸ Package Manager ▸ Scoped Registries` (или
`scopedRegistries` в `Packages/manifest.json` проекта — в package.json самого пакета Unity это поле
игнорирует):

| Name | URL | Scopes |
|---|---|---|
| OpenUPM | `https://package.openupm.com` | `com.cysharp`, `jp.hadashikick` |
| UnityNuGet | `https://unitynuget-registry.openupm.com` | `org.nuget` |

**2. SDK по git URL** — `Package Manager ▸ + ▸ Add package from git URL…`:

```
https://github.com/Merchelago/unity-sdk.git#v1.13.0
```

R3, его ядро и VContainer подтянутся сами как зависимости SDK. Итоговый `Packages/manifest.json`:

```json
{
  "dependencies": {
    "ru.vhrgames.sdk": "https://github.com/Merchelago/unity-sdk.git#v1.13.0"
  },
  "scopedRegistries": [
    { "name": "OpenUPM", "url": "https://package.openupm.com", "scopes": ["com.cysharp", "jp.hadashikick"] },
    { "name": "UnityNuGet", "url": "https://unitynuget-registry.openupm.com", "scopes": ["org.nuget"] }
  ]
}
```

> **Был R3 из NuGetForUnity или в `Assets/Plugins`?** Удалите эти копии (`Assets/Packages/R3.*`,
> `Microsoft.Bcl.TimeProvider.*`, `Microsoft.Bcl.AsyncInterfaces.*`, `System.Threading.Channels.*`,
> `System.ComponentModel.Annotations.*`, `System.Runtime.CompilerServices.Unsafe.*`, `Assets/Plugins/R3`)
> и строки о них в `Assets/packages.config` — ядро теперь даёт пакет `org.nuget.r3`. Установщик делает
> это сам.

| Пакет | Версия | Реестр |
|---|---|---|
| `com.cysharp.r3` (R3.Unity) | 1.2.9 | OpenUPM |
| `org.nuget.r3` (ядро R3) + транзитивные `org.nuget.microsoft.bcl.timeprovider` 8.0.0, `org.nuget.microsoft.bcl.asyncinterfaces` 6.0.0, `org.nuget.system.threading.channels` 8.0.0, `org.nuget.system.componentmodel.annotations` 5.0.0, `org.nuget.system.runtime.compilerservices.unsafe` 6.0.0 и др. | 1.2.9 | UnityNuGet |
| `jp.hadashikick.vcontainer` | 1.16.0 | OpenUPM |

---

## Обновление SDK

Меню **`VHR → Обновление SDK`** — всё в одном окне:

- установленная версия и откуда она (git / реестр / встроенная папка / локальная), последняя и
  **минимальная поддерживаемая** версии;
- «Что нового» — раздел CHANGELOG последней версии;
- устаревшие API, которые встречаются в вашем коде (`Assets/**/*.cs`), с переходом к строке;
- зависимости и реестры — что поднимется вместе с обновлением;
- кнопка **«Обновить до X»**: добавит недостающие реестры, поднимет зависимости, прибитые в
  `manifest.json` ниже нужной версии, и переключит SDK на новый тег — одним запросом. Редактор не
  блокируется; после перекомпиляции новая версия SDK сама покажет итог. Если SDK лежит в `Packages/`
  папкой или подключён через `file:`, окно покажет, что сделать вручную.

**Автопроверка.** При запуске редактора SDK раз в сутки сверяется с сервером VHR. Вышла новая версия —
одно предупреждение в консоли (на каждую версию один раз). Сервер недоступен — тишина, всё работает.

**`minSupported`** — версия SDK, ниже которой платформа **не примет новую сборку** игры
(`sdk_outdated` при загрузке). Если ваша версия ниже, редактор покажет ошибку и предложит обновиться,
а WebGL-сборка остановится с подсказкой «VHR → Обновление SDK». Если версия просто не последняя —
сборка пройдёт с предупреждением. `minSupported` поднимается редко — только ради безопасности или денег
и с анонсом заранее.

**Старые сборки продолжают работать.** Уже опубликованная игра не ломается от выхода новой версии SDK.
Новая версия попадёт к игрокам, когда вы пересоберёте игру и загрузите сборку на платформу.

Вручную (без окна) — поменяйте тег в `Packages/manifest.json`:
`"ru.vhrgames.sdk": "https://github.com/Merchelago/unity-sdk.git#v<новая версия>"`. Если там же
прямо указаны `com.cysharp.r3`, `org.nuget.r3` или `jp.hadashikick.vcontainer` (их добавляет
установщик), поднимите их до версий из CHANGELOG: версия, указанная в проекте, важнее требований
пакета. Окно обновления делает это само.

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
| `Unavailable` | Реклама выключена, детский аккаунт, адблок, нет объявления, игра не на vhrgames.ru, премиум-игра (SDK 1.13.0+, `Reason = "premium_game"`) | Продолжить игру / спрятать кнопку |
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

## Облачные сохранения (`VhrSdk.Saves`, SDK 1.11.0+)

Прогресс игрока хранится на сервере VHR: переживает очистку браузера, переносится между
устройствами, другие игры его не видят. Игрок и игра берутся из игрового токена — `userId`
передавать не нужно. Доступ: `VhrSdk.Saves` или внедрение `IVhrSaves` (VContainer) — один
общий экземпляр.

Сохранение — **строка** (обычно JSON) в именованном **слоте** (`[a-z0-9_.-]`, 1–64 символа,
SDK приводит имя к нижнему регистру; по умолчанию `"default"`). У слота есть **версия**: 1 при
создании, +1 на каждую запись — по ней SDK замечает, что слот перезаписали с другого
устройства или вкладки.

| Член | Что делает |
|---|---|
| `LoadAsync(slot = "default")` → `VhrSaveLoadResult` | `Found`, `Data`, `Version`, `UpdatedAt`, `Error`. Нет сохранения — `Found = false`, `Error = NotFound` |
| `LoadJsonAsync<T>(slot, fallback)` | То же + `JsonUtility.FromJson<T>`; нет сохранения или ошибка — `fallback` |
| `SaveAsync(slot, data, expectedVersion = null)` → `VhrSaveWriteResult` | Записать сразу: `Ok`, `Version`, `Error`, `ServerVersion` (при конфликте), `RetryAfterSeconds` (при 429) |
| `SaveJsonAsync<T>(slot, value, expectedVersion)` | То же для объекта (`JsonUtility.ToJson`) |
| `DeleteAsync(slot)` → `bool` | Удалить слот (идемпотентно) и освободить место в квоте |
| `ListAsync()` → `IReadOnlyList<VhrSaveSlotInfo>` | Слоты игрока без данных: `slot`, `version`, `rawSize`, `updatedAt`. При ошибке — `VhrSdkException` |
| `QueueSave(slot, data)` / `QueueSaveJson<T>(slot, value)` | **Автосохранение**: копит последнее состояние и отправляет не чаще раза в `SavesMinIntervalSeconds` на слот |
| `FlushAsync()` | Отправить накопленное сейчас и дождаться записей «в полёте» |
| `event OnConflict(slot, serverVersion)` | Автосохранение упёрлось в чужую запись (409) — слот на паузе, решите, чья версия главнее |
| `event OnAutoSaveResult(slot, result)` | Итог каждой отправки автосохранения (для значка «Сохранено в облаке») |
| `HasPendingSaves`, `Limits` | Есть ли неотправленное; лимиты слота от сервера |

`expectedVersion`: `null` — перезаписать как есть; `0` — только создать (слот уже есть →
`Conflict`); `N` — записать, только если на сервере сейчас версия `N`.

### Пример: загрузка при старте и автосохранение

```csharp
using System;
using UnityEngine;
using VhrGames.Sdk;

[Serializable]
public class Progress
{
    public int level = 1;
    public long gold;
    public string[] unlocked = Array.Empty<string>();
}

public class SaveSystem : MonoBehaviour
{
    private const string Slot = "default";
    private Progress _progress = new Progress();

    // Вызвать после VhrSdk.InitializeAsync(...) (или из DI — IVhrSaves в конструктор).
    public async void LoadOnStart()
    {
        var saves = VhrSdk.Saves;
        saves.OnConflict += OnConflict;
        saves.OnAutoSaveResult += (slot, r) =>
        {
            if (!r.Ok) Debug.LogWarning($"Облако: {r.Error} ({r.ErrorCode})");
        };

        var r = await saves.LoadAsync(Slot);
        if (r.Found)
            _progress = JsonUtility.FromJson<Progress>(r.Data);   // без связи — r.FromCache / r.FromPending (1.12.0+)
        else if (r.Error == VhrSaveError.NotFound)
            _progress = new Progress();          // первый запуск — новая игра
        else
            _progress = LoadLocalCopy();         // гость / нет сети и это устройство облака ещё не видело
    }

    // На каждое изменение прогресса — дёшево, можно часто.
    public void OnLevelCompleted(int level, long gold)
    {
        _progress.level = level + 1;
        _progress.gold = gold;
        VhrSdk.Saves.QueueSaveJson(Slot, _progress);   // уйдёт в фоне, не чаще раза в 10 с
        SaveLocalCopy(_progress);                     // офлайн-страховка (PlayerPrefs)
    }

    // Важные точки (выход в меню, конец сессии) — дождаться отправки.
    public async void OnExitToMenu() => await VhrSdk.Saves.FlushAsync();

    // Слот перезаписали с другого устройства/вкладки: берём облачное и сливаем со своим.
    private async void OnConflict(string slot, long serverVersion)
    {
        var cloud = await VhrSdk.Saves.LoadJsonAsync<Progress>(slot);   // снимает конфликт
        if (cloud != null && cloud.level > _progress.level) _progress = cloud;
        VhrSdk.Saves.QueueSaveJson(slot, _progress);                    // пишет поверх новой версии
    }

    private static Progress LoadLocalCopy() =>
        JsonUtility.FromJson<Progress>(PlayerPrefs.GetString("progress", "{}")) ?? new Progress();

    private static void SaveLocalCopy(Progress p) => PlayerPrefs.SetString("progress", JsonUtility.ToJson(p));
}
```

Как работает автосохранение:
- первое `QueueSave` слота уходит сразу, следующие склеиваются и отправляются не чаще раза в
  `VhrSdkOptions.SavesMinIntervalSeconds` (по умолчанию **10 с**, минимум 5) — уходит последнее значение;
  все автосохранения вместе SDK держит в пределах 10 записей в минуту (лимит сервера — 12);
- накопленное отправляется **сразу** при потере фокуса (на телефоне — и перед паузой приложения),
  скрытии вкладки, сворачивании браузера, закрытии страницы и выходе из игры;
- после `429` — повтор через `Retry-After`; при обрыве связи — повтор с паузой 5 → 10 → … → 60 с и сразу,
  когда сеть вернулась или игрок вернулся во вкладку;
- **SDK 1.12.0+: сначала на устройство.** До подтверждения сервером данные слота лежат и на диске
  (`persistentDataPath/vhr-sdk/saves/…`, на WebGL — IndexedDB браузера): закрыли вкладку без связи — при
  следующем запуске SDK сам догрузит их в облако. `LoadAsync` отдаёт неотправленное (`FromPending`), а без
  связи — последнюю облачную версию, которую видело это устройство (`FromCache`). Подробнее —
  [Работа без связи](#работа-без-связи-vhrsdkconnection-sdk-1120);
- если слот загружали (`LoadAsync`), запись идёт с проверкой версии: чужая запись → `OnConflict`,
  облачные данные не перезаписываются вслепую. Если загрузка не удалась (нет сети и локальной копии) —
  автосохранение пишет с `expectedVersion = 0` и тоже не затрёт облачный прогресс.

> ⚠️ Закрытие вкладки — «лучшее усилие»: браузер может оборвать запрос. Вызывайте `FlushAsync()` в
> естественных точках (конец уровня, выход в меню), а не надейтесь только на автосохранение при выходе.

### Лимиты

| Что | Лимит |
|---|---|
| Размер слота | ≤ **256 КБ** после сжатия (gzip) и ≤ **1 МБ** до сжатия |
| Слотов на игрока в игре | ≤ **10** |
| Частота | **12** записей/удалений и **60** чтений в минуту на игрока в игре (все слоты вместе) → `429` |
| Объём всех сохранений игры | **Квота игры**: 50 МБ бесплатно (по сжатому размеру), сверх — платные пакеты |

**Квота платная сверх бесплатной — её оплачивает разработчик**, не игрок: кабинет разработчика →
игра → **«Облачные сохранения»** → пакеты на 30 дней за монеты баланса (с автопродлением). При 90% и
100% квоты разработчику приходит уведомление. Когда квота исчерпана, запись, **увеличивающая** объём,
получает `QuotaExceeded`; уменьшающая или равная — проходит. Игроку в этом случае покажите «облако
временно недоступно» и сохраняйте локально.

### Ошибки (`VhrSaveError`)

| Значение | Когда | Что делать |
|---|---|---|
| `NotFound` | Сохранения в слоте нет | Новая игра |
| `Conflict` | Слот уже перезаписан (версия не совпала) | Загрузить облачное, слить, сохранить ещё раз |
| `QuotaExceeded` | Квота игры исчерпана | Разработчику — докупить пакет; игроку — сохранить локально |
| `SlotTooLarge` | > 256 КБ сжато или > 1 МБ сырых | Хранить только прогресс, не кэш/ассеты |
| `SlotsLimit` | Уже 10 слотов | Писать в существующий / удалить ненужный |
| `RateLimited` | > 12 записей в минуту | Использовать `QueueSave`; автосохранение повторит само |
| `NotAuthenticated` | Гость / игра открыта не с vhrgames.ru / нет ключа в Editor | Сохранять локально |
| `Disabled` | Сохранения выключены платформой или игра не опубликована | Сохранять локально |
| `Network` | Нет связи | Автосохранение повторит; явный `SaveAsync` — повторить позже |
| `BadSlot` | Неверное имя слота / тело запроса | Исправить имя (`a-z0-9_.-`) |
| `Unknown` | Ошибка сервера (5xx), устаревший SDK (`426`) | См. `ErrorCode` / `Message` |

### Рекомендации

- **Маленький JSON.** Только то, что нужно для продолжения игры: уровень, валюта, открытия,
  настройки. Не кладите кэш, скриншоты, логи, base64-картинки — упрётесь в 256 КБ и в квоту игры.
- **`QueueSave` вместо `SaveAsync` каждый кадр.** `SaveAsync` — для разовых явных записей
  (кнопка «Сохранить»). Частые вызовы упрутся в `429`.
- **Загружайте слот при старте** (`LoadAsync`) — тогда автосохранение защищено от перезаписи
  прогресса с другого устройства, и вы отличите «новая игра» (`NotFound`) от «нет сети».
- **Обрабатывайте `OnConflict`** (игрок играет в двух вкладках или на двух устройствах) и
  `QuotaExceeded` (через `OnAutoSaveResult`).
- **Офлайн и гость.** Без входа на vhrgames.ru облака нет (`NotAuthenticated`) — держите локальную
  копию в `PlayerPrefs` (на WebGL это IndexedDB браузера) и показывайте «войдите, чтобы прогресс
  сохранялся в облаке». У вошедшего игрока при обрыве связи данные не теряются: SDK держит их на
  устройстве и отправит сам (в том числе после перезапуска).
- **Не меняйте формат молча.** Добавляйте в JSON поле версии формата и мигрируйте старые
  сохранения при загрузке.

### Редактор

- **Simulation** — локальное «облако» в `Library/VhrSdk/SimSaves` проекта (не в VCS и не в сборке,
  переживает перезапуск редактора) с теми же правилами: версии, конфликты, 256 КБ / 1 МБ, 10 слотов,
  1024 КБ на песочного игрока, 12 записей и 60 чтений в минуту. Окно **`VHR → Тестирование в Editor`**
  показывает объём и умеет очистить локальные сохранения.
- **Live (песочница)** — настоящий сервер с песочным ключом (сохранения тестового игрока `sbx_*`,
  лимит 1024 КБ на игру, квоту игры не тратят; удаляются, если не обновлялись 30 дней). «Проверить все API»
  прогоняет цикл запись → чтение → версия → конфликт → удаление на тестовом слоте `vhr_sdk_selftest`.

---

## Таблицы лидеров (`VhrSdk.Leaderboard`, SDK 1.12.0+)

У игры может быть **несколько таблиц лидеров** — по таблице на режим, трассу или сложность. Заводить их
в кабинете не нужно: **таблица создаётся сама при первой отправке счёта с новым id**. Кабинет
разработчика (игра → «Таблицы лидеров») — чтобы переименовать, поменять порядок вкладок, скрыть таблицу
на сайте, сбросить её или удалить чужой результат. Игрок и игра берутся из токена, `userId` и `gameId`
передавать не нужно. Доступ: `VhrSdk.Leaderboard` или внедрение `IVhrLeaderboard` (VContainer).

- **Id таблицы** — `a-z0-9`, `_`, `-`, до 32 символов, первый символ — буква или цифра (`"survival"`,
  `"level_3"`, `"hard-mode"`); регистр и пробелы по краям SDK убирает сам. Без id — основная таблица
  `"default"`. До 50 таблиц на игру.
- **Лучший результат.** Сервер хранит у игрока только лучший счёт — за всё время, день, неделю и месяц
  (UTC). Худший счёт можно отправлять: он просто не побьёт рекорд (`improved = false`).
- **Направление и формат** задаются при первой отправке (`VhrBoardOptions`), дальше — в кабинете:
  больше — лучше (очки, волны; по умолчанию) или меньше — лучше (время, ходы); число или время в
  **миллисекундах**. Счёт — целое `long` в пределах ±(2^53−1), можно отрицательный.

| Член | Что делает |
|---|---|
| `SubmitAsync(score)` | Счёт в основную таблицу `"default"` → `VhrScoreResult` |
| `SubmitAsync(board, score)` | Счёт в таблицу `board`; её нет — создастся (больше — лучше, число) |
| `SubmitAsync(board, score, options)` | То же + `VhrBoardOptions { Title, Sort, Format }` — применяются **только при создании** таблицы, передавать можно всегда |
| `GetTopAsync(board = null, period = AllTime, limit = 10)` → `VhrBoardPage` | Топ за период (`AllTime`/`Daily`/`Weekly`/`Monthly`, до 100 строк) + место игрока `me` + `total`. Ники и отформатированный счёт уже внутри. Таблицы ещё нет — пустая страница |
| `GetBoardsAsync()` → `VhrBoardInfo[]` | Таблицы игры в порядке вкладок, включая скрытые на сайте (`hidden = true`) |
| `VhrScoreFormatter.Format(score, board)` | Счёт как на сайте: «12 345 очков», «1:02.345», «1:02:03.004» |

`VhrScoreResult`: `accepted`, `improved` (первый результат или новый рекорд), `best` (лучший за всё
время), `rank` (место за всё время), `board`. `VhrBoardEntry`: `rank`, `userId` (`null` — профиль скрыт),
`name` (скрыт / нет ника — «Игрок»), `score`, `formatted`, `isCurrentUser`.

### Пример: два режима, время «меньше — лучше» и место игрока

```csharp
using UnityEngine;
using VhrGames.Sdk;

public class Records : MonoBehaviour
{
    // «Выживание»: волны, больше — лучше. Таблица "survival" появится при первой отправке.
    public async void OnSurvivalOver(int wave)
    {
        try
        {
            var r = await VhrSdk.Leaderboard.SubmitAsync("survival", wave,
                new VhrBoardOptions { Title = "Выживание" });
            if (r.queued) Debug.Log("Нет связи — рекорд отправится сам, когда она появится");   // 1.12.0+
            else if (r.improved) Debug.Log($"Новый рекорд: {r.best} волн, место #{r.rank}");
        }
        catch (VhrSdkException ex) when (ex.Code == "unauthorized")
        {
            Debug.Log("Войдите на vhrgames.ru, чтобы попасть в таблицу рекордов");   // гость
        }
    }

    // «Спидран»: время в миллисекундах, меньше — лучше.
    public async void OnSpeedrunFinished(float seconds)
    {
        var r = await VhrSdk.Leaderboard.SubmitAsync("speedrun", (long)(seconds * 1000f), new VhrBoardOptions
        {
            Title = "Спидран",
            Sort = VhrBoardSort.LowerIsBetter,
            Format = VhrScoreFormat.Time
        });
        Debug.Log(r.improved ? $"Лучшее время! Место #{r.rank}" : "Рекорд не побит");
    }

    // Обычные очки — в основную таблицу "default".
    public async void OnLevelScore(long score) => await VhrSdk.Leaderboard.SubmitAsync(score);

    // Экран рекордов: топ-10 за неделю и место игрока, даже если он не в топе.
    public async void ShowSpeedrunTop()
    {
        var page = await VhrSdk.Leaderboard.GetTopAsync("speedrun", VhrLeaderboardPeriod.Weekly, 10);
        Debug.Log(page.board.title);
        foreach (var e in page.entries)
            Debug.Log($"{e.rank}. {e.name} — {e.formatted}{(e.isCurrentUser ? " (вы)" : "")}");

        Debug.Log(page.me != null
            ? $"Вы на {page.me.rank}-м месте из {page.total}: {page.me.formatted}"
            : "У вас пока нет результата за неделю");
    }

    // Вкладки режимов — в порядке, заданном в кабинете.
    public async void BuildTabs()
    {
        foreach (var board in await VhrSdk.Leaderboard.GetBoardsAsync())
            Debug.Log($"Вкладка {board.title} ({board.id})");
    }
}
```

> ℹ️ Поменять название, формат или порядок вкладок — в кабинете, а не в коде: `VhrBoardOptions` уже
> существующей таблицы сервер игнорирует. Направление меняется только у пустой таблицы (сбросьте её в
> кабинете). Таблицу, удалённую в кабинете, игра создаст заново при следующей отправке счёта.

### Ошибки

Ошибки — `VhrSdkException`, код сервера — в `ServerCode`:

| `ServerCode` | Когда |
|---|---|
| `bad_board` | Неверный id таблицы (SDK проверяет до запроса) |
| `bad_request` | Счёт вне ±(2^53−1) |
| `board_limit` | У игры уже 50 таблиц — новая не создастся (`Code = "conflict"`) |
| — (`Code = "unauthorized"`) | Гость: отправлять счёт может только вошедший игрок; читать таблицы можно всем |

Нет связи с сервером (сеть, таймаут, 5xx, 429, истёкший токен) — **не ошибка**: `SubmitAsync` вернёт
`VhrScoreResult { accepted = false, queued = true }`, счёт сохранится на устройстве и уйдёт сам
(см. [Работа без связи](#работа-без-связи-vhrsdkconnection-sdk-1120)). Дневные/недельные таблицы посчитают
его по времени доставки на сервер.

### Редактор и миграция

- **Live (песочница)** — настоящий сервер с песочным ключом: результаты тестового игрока `sbx_*` видны
  только ему (в публичных таблицах их нет), а созданные таблицы сразу появляются в кабинете — так удобно
  настроить их до публикации. Ник тестового игрока в таблице — «Тестовый игрок».
- **С 1.11 и раньше:** `SubmitScoreAsync(gameId, score)` и `GetForGameAsync(gameId, limit)` работают как
  прежде (основная таблица `"default"`), но помечены `[Obsolete]` — замените на `SubmitAsync(score)` и
  `GetTopAsync()`. `GetGlobalAsync` (общий рейтинг платформы) не изменился.

---

## Работа без связи (`VhrSdk.Connection`, SDK 1.12.0+)

Связь с серверами может оборваться в любой момент: метро, плохой Wi-Fi, сервер перезапускается. С SDK
1.12.0 данные вошедшего игрока от этого **не теряются**: то, что нельзя отправить сейчас, SDK кладёт на
устройство и отправляет сам, когда связь вернётся — даже если игрок закрыл вкладку и открыл игру завтра.

| Что | Без связи | Где ждёт |
|---|---|---|
| Рекорд `Leaderboard.SubmitAsync(…)` (и старый `SubmitScoreAsync`) | не исключение, а `VhrScoreResult { accepted = false, queued = true }` | очередь на устройстве |
| Достижение `Achievements.UnlockAsync("first_win")` (**новое**) | `VhrUnlockResult { queued = true }` | очередь |
| Турнир `Tournaments.SubmitAsync(id, score)` (**новое**, старый `SubmitScoreAsync` тоже) | `VhrTournamentScoreResult { queued = true }` | очередь |
| Конец сессии `GameSessions.EndAsync(…)` | без исключения | очередь |
| Сохранения `Saves.QueueSave` / `SaveAsync` | данные сразу на устройстве, отправка с повторами | файлы слотов |

**Как это работает**
- Сначала SDK пробует отправить сразу — чтобы вернуть игре итог (новый рекорд, место, «достижение
  открыто»). Не вышло из-за связи (нет сети, таймаут, 5xx, 408, 429, истёкший токен) — операция встаёт в
  очередь. Если браузер точно офлайн, ждать таймаут запроса SDK не будет.
- Очередь — файл `persistentDataPath/vhr-sdk/outbox-{gameId}.json` (на WebGL — IndexedDB браузера; SDK
  синхронизирует его сам, в том числе при скрытии и закрытии вкладки). Переживает перезагрузку страницы и
  перезапуск игры.
- Отправка — при старте игры (как только есть токен игрока), сразу после постановки, когда браузер снова в
  сети, когда игрок вернулся во вкладку, и по таймеру с паузой 2 → 4 → 8 → … → 60 с. После `429` — через
  `Retry-After`, после `401` — когда сайт пришлёт свежий токен.
- Повтор безопасен: на сервере эти операции идемпотентны (хранится лучший результат, ачивка открывается
  один раз). Одинаковые схлопываются: ачивка — одна, рекорды — лучший и худший по таблице (направление
  таблицы SDK может не знать, а сервер возьмёт лучший), турнир — максимум. В очереди — до 300 операций;
  неотправленное за 14 дней выбрасывается с предупреждением.
- Операции уходят от имени того игрока, который их сделал: на общем компьютере чужие ждут своего игрока.
  Гость (без входа) в очередь ничего не ставит — отправлять ему некуда (кроме завершения сессии).
- Сохранения: см. [Облачные сохранения](#облачные-сохранения-vhrsdksaves-sdk-1110) — неотправленные данные
  слота на устройстве, `LoadAsync` отдаёт их (`FromPending`), без связи — последнюю облачную версию
  (`FromCache`). Если запрос дошёл, а ответ потерялся, `409` с нашими же данными считается успехом.

**Что не гарантируется**
- **Покупки и экономика — только онлайн**: покупку подтверждает игрок в окне платформы, отложить её нельзя.
  Друзья, лобби, мультиплеер, чтение таблиц и профилей — тоже только онлайн.
- **Дневные / недельные / месячные таблицы считают отложенный счёт по времени доставки** на сервер, а не по
  времени игры: рекорд, поставленный в воскресенье и доставленный в понедельник, попадёт в новую неделю.
  Таблица «за всё время» от этого не зависит.
- Турнир принимает результат, только пока он активен: доставленный после окончания будет отклонён.
- Если сервер окончательно отклонил операцию (турнир закончился, у игры уже 50 таблиц, такой ачивки нет),
  она выбрасывается: событие `OnDropped` и предупреждение в консоли.
- Очистка данных сайта в браузере и приватное окно стирают и очередь.

| Член `VhrSdk.Connection` (`IVhrConnection`) | Что делает |
|---|---|
| `IsOnline`, `event OnlineChanged(bool)` | Есть ли связь с серверами VHR (последний запрос получил ответ, браузер не офлайн) — для значка «нет связи» |
| `PendingCount`, `event PendingChanged(int)` | Сколько операций текущего игрока ждут отправки (сохранения — `Saves.HasPendingSaves`) |
| `FlushAsync()` | Отправить очередь сейчас и дождаться (не дольше 30 с, без исключений) |
| `event OnDropped(VhrDroppedOperation)` | Операция выброшена без доставки: `Kind`, `Target`, `Value`, `Reason` (`expired`, `limit`, код сервера), `Message` |

### Пример: значок «нет связи», отложенный рекорд и достижение

```csharp
using UnityEngine;
using VhrGames.Sdk;

public class OnlineStatus : MonoBehaviour
{
    [SerializeField] private GameObject offlineIcon;   // «нет связи»
    [SerializeField] private GameObject pendingIcon;   // «есть неотправленное»

    // Вызвать после VhrSdk.InitializeAsync(...) (или внедрить IVhrConnection через VContainer).
    public void Bind()
    {
        var connection = VhrSdk.Connection;
        connection.OnlineChanged += online => offlineIcon.SetActive(!online);
        connection.PendingChanged += count => pendingIcon.SetActive(count > 0);
        connection.OnDropped += op => Debug.LogWarning($"Не отправлено: {op.Kind} {op.Target} — {op.Reason}");
        offlineIcon.SetActive(!connection.IsOnline);
        pendingIcon.SetActive(connection.PendingCount > 0);
    }

    public async void OnRunFinished(long score)
    {
        var r = await VhrSdk.Leaderboard.SubmitAsync("survival", score);
        if (r.queued) Toast("Нет связи — рекорд отправится сам, когда она появится");
        else if (r.improved) Toast($"Новый рекорд! Место #{r.rank}");
    }

    public async void OnBossKilled()
    {
        // Код из кабинета (игра → Достижения); SDK сам допишет game.{gameId}.
        var a = await VhrSdk.Achievements.UnlockAsync("boss_slayer");
        if (a.unlocked) Toast($"Достижение: {a.achievement?.title}");
        // a.queued — откроется, когда появится связь; уже открытое — unlocked = false.
    }

    // Выход в меню: дождаться и сохранений, и очереди.
    public async void OnExitToMenu()
    {
        await VhrSdk.Saves.FlushAsync();
        await VhrSdk.Connection.FlushAsync();
    }

    private static void Toast(string text) => Debug.Log(text);
}
```

> ℹ️ Unity Editor: очередь лежит в `persistentDataPath` редактора и переживает перезапуск Play; в
> Live-песочнице операции идут от тестового игрока `sbx_*`. Полный протокол (классификация ответов,
> формат файла, WebGL и IndexedDB) — [Documentation~/index.md, 6.9](Documentation~/index.md#69-ivhrconnection--работа-без-связи-sdk-1120).

---

## Премиум-игры (`VhrSdk.Access`, SDK 1.13.0+)

Премиум-игра — платная: игрок покупает её за монеты (1 монета = 1 ₽) или играет по подписке
**Premium**, если игра входит в подписку. Рекламы в премиум-игре нет, а демо-версия может быть.

**Как игра становится премиум.** Только решением **администратора платформы** при модерации.
Разработчик подаёт заявку в кабинете (игра → «Премиум»): цена (49–4999 монет), «входит в подписку»,
«есть демо». Окончательные условия ставит администратор. Пока заявку не одобрили, игра обычная:
бесплатная, с рекламой, без демо-ограничений. **Пересобирать игру после одобрения не нужно**: одна сборка
работает в обоих режимах, если она проверяет доступ через `VhrSdk.Access`.

| `GameType` | `Access` | `Source` | Когда | Что делает игра |
|---|---|---|---|---|
| `Regular` | `Full` | `Free` | Обычная игра (или заявка ещё не одобрена) | Всё открыто, реклама есть |
| `Premium` | `Full` | `Purchase` / `Subscription` / `Grandfather` | Игрок купил игру (или её подарил родитель), у него подписка Premium (своя или родителя), или он покупал в этой игре до её перевода в премиум | Всё открыто, рекламы нет |
| `Premium` | `Demo` | `Demo` | Игрок не купил игру, а у неё есть демо-версия | Ограничивает контент и показывает кнопку «Купить полную версию» |
| `Premium` | `None` | `None` | Игрок не купил игру, а демо-версии нет | На сайте игра не запустится: платформа сразу предложит покупку |

Что ограничить в демо (уровни, время, режимы), решает сама игра. Сервер не выдаёт в демо ничего
платного: товары каталога продаются только при полном доступе.

### Пример: проверить доступ, ограничить демо, кнопка «Купить полную версию»

```csharp
using UnityEngine;
using UnityEngine.UI;
using VhrGames.Sdk;

public class PremiumGate : MonoBehaviour
{
    [SerializeField] private GameObject demoBanner;   // «Демо-версия: доступны 3 уровня»
    [SerializeField] private Button buyFullButton;    // «Купить полную версию»
    [SerializeField] private Text buyFullLabel;
    [SerializeField] private int demoLevels = 3;

    private VhrAccessState _access = VhrAccessState.Regular();

    // Вызвать после VhrSdk.InitializeAsync(...) (или внедрить IVhrAccess через VContainer).
    public async void Bind()
    {
        VhrSdk.Access.AccessChanged += Apply;            // подписка — ДО запроса
        buyFullButton.onClick.AddListener(OnBuyFull);
        Apply(await VhrSdk.Access.GetAsync());           // источник правды — сервер
    }

    private void OnDestroy()
    {
        if (VhrSdk.Access != null) VhrSdk.Access.AccessChanged -= Apply;
    }

    // На старте и каждый раз, когда доступ меняется БЕЗ перезапуска игры: покупка в игре
    // или на сайте, подписка Premium, одобрение родителя, решение администратора.
    private void Apply(VhrAccessState access)
    {
        _access = access;
        demoBanner.SetActive(access.IsDemo);
        buyFullButton.gameObject.SetActive(access.CanPurchase);   // премиум и не полный доступ
        if (access.PriceCoins.HasValue)
            buyFullLabel.text = $"Полная версия — {access.PriceCoins} монет";
    }

    // Демо-ограничение решает игра. У обычной игры IsFull всегда true.
    public bool IsLevelAvailable(int level) => _access.IsFull || level <= demoLevels;

    public void OnLevelSelected(int level)
    {
        if (!IsLevelAvailable(level)) { OnBuyFull(); return; }
        LoadLevel(level);
    }

    private async void OnBuyFull()
    {
        buyFullButton.interactable = false;
        var r = await VhrSdk.Access.OpenPurchaseAsync();  // окно платформы, игра его не рисует
        buyFullButton.interactable = true;

        switch (r.Status)
        {
            case VhrAccessPurchaseStatus.Purchased:       // купил за монеты
            case VhrAccessPurchaseStatus.Subscribed:      // оформил Premium
                Apply(r.State);                           // полный доступ сразу (AccessChanged тоже придёт)
                break;
            case VhrAccessPurchaseStatus.Requested:       // детский аккаунт: запрос родителю
                Toast("Запрос отправлен родителю");       // одобрит — придёт AccessChanged
                break;
            case VhrAccessPurchaseStatus.Cancelled:       // закрыл окно
                break;
            default:                                      // Unavailable: r.ErrorCode — для логов
                Debug.Log("Покупка недоступна: " + r.ErrorCode);
                break;
        }
    }

    private void LoadLevel(int level) { /* ... */ }
    private static void Toast(string text) => Debug.Log(text);
}
```

`Apply` может вызваться дважды с одним состоянием (итог окна и `AccessChanged`) — делайте его идемпотентным.

### Что меняется в премиум-игре

- **Реклама — никогда** (ни в полном доступе, ни в демо): `VhrSdk.Ads.IsSupported = false`, а
  `ShowInterstitialAsync`/`ShowRewardedAsync` сразу возвращают `Unavailable` (`Reason = "premium_game"`) без
  запроса к сайту и без `OnAdOpened`. Прячьте рекламные кнопки по `Ads.IsSupported` — тогда одна сборка
  верна в обоих режимах. Сайт в премиум-игре тоже рекламы не показывает.
- **Товары каталога — только при полном доступе.** В демо `Economy.PurchaseAsync` сразу возвращает
  `Forbidden` с кодом `demo_access` (сервер тоже откажет), а `Economy.IsPurchaseAvailable = false`. Каталог
  (`GetItemsAsync`) и купленное (`GetOwnedItemsAsync`) читаются как обычно.
- **Сохранения, таблицы лидеров и достижения в демо работают как обычно**: игрок тот же, поэтому прогресс
  после покупки остаётся.
- **Окно покупки показывает сайт:** «Купить за N монет», «Оформить Premium» (если игра входит в подписку), а для
  детского аккаунта — «Попросить у родителя» (итог `Requested`; когда родитель одобрит, придёт
  `AccessChanged`). Свои окна оплаты в игре запрещены, как и для товаров.
- **Перезапуск не нужен:** после покупки (в игре или на сайте) страница платформы сообщает игре новый доступ,
  SDK присылает `AccessChanged` и перечитывает доступ с сервера.
- **Старые сборки (SDK < 1.13.0)** в премиум-игре: игрок с полным доступом играет как раньше, а без доступа
  игра не запускается. Про демо старая сборка не знает, поэтому демо-ограничения делает только сборка с
  SDK 1.13.0+.

| Член `VhrSdk.Access` (`IVhrAccess`) | Что делает |
|---|---|
| `GetAsync(ct)` → `VhrAccessState` | Доступ с сервера (`GET {Games}/api/Games/{id}/access`). Если сервер недоступен, SDK берёт ответ страницы платформы, подсказку игрового токена или последнее известное состояние, а если не знает ничего — считает игру обычной с полным доступом. Никогда не бросает (кроме отмены `ct`) |
| `Current` | Последнее известное состояние без запроса, никогда не `null` (источник — `Origin`) |
| `event AccessChanged(VhrAccessState)` | Доступ изменился без перезапуска игры. Первая загрузка его не вызывает |
| `OpenPurchaseAsync(ct)` → `VhrAccessPurchaseResult` | Окно платформы «Купить полную версию»: `Status` (`Purchased` / `Subscribed` / `Requested` / `Cancelled` / `Unavailable`), `State`, `IsFull`, `ErrorCode` (`not_premium`, `already_full`, `not_hosted`, `host_unsupported`, `busy`…) |

`VhrAccessState`: `GameType` (`Regular` / `Premium`), `Access` (`Full` / `Demo` / `None`), `Source` (`Free` /
`Purchase` / `Subscription` / `Grandfather` / `Demo` / `None`), `PriceCoins` (`int?`), `InSubscription`,
`HasDemo`, `IsPremium`, `IsFull`, `IsDemo`, `CanPurchase`, `Origin` (`Server` / `Host` / `Token` / `Simulation` /
`Default`).

**Редактор.** Окно **`VHR → Тестирование в Editor`** → «Премиум-игра: доступ игрока»: Regular / Premium Full /
Premium Demo / Premium None, цена, «входит в подписку». Кнопки «Симулировать покупку» и «Симулировать подписку
Premium» во время Play присылают игре `AccessChanged`. `OpenPurchaseAsync` в Editor показывает диалог-имитацию
окна платформы (монеты не тратятся). Доступ в Editor симулируется всегда — и в Simulation, и в Live; что сервер
отвечает тестовому игроку, показывает «Проверить все API».

Протокол между игрой и сайтом (для справки, SDK делает всё сам):
`{ type:'vhr:access:get', requestId }` → `{ type:'vhr:access:state', requestId, state }`;
`{ type:'vhr:access:changed', state }` (без запроса, при смене доступа);
`{ type:'vhr:access:purchase', requestId }` →
`{ type:'vhr:access:purchase-result', requestId, result:'purchased'|'subscribed'|'requested'|'cancelled'|'unavailable', state }`.
Подробно — [Documentation~/index.md, 6.10](Documentation~/index.md#610-ivhraccess--доступ-к-премиум-игре-sdk-1130).

---

## Публичный API кратко

| Сервис | Ключевые методы |
|---|---|
| `IVhrEconomy` | `GetBalanceAsync`, `GetItemsAsync`, `GetOwnedItemsAsync`, `PurchaseAsync` → `VhrPurchaseResult` (+ колбэк `Purchase`), `OpenTopUp`, `IsPurchaseAvailable` · событие `OnBalanceChanged` и `Observable<BalanceChanged> BalanceChanged` · `SpendAsync`/`GrantCoinsAsync` — **отключены** (`[Obsolete]`) |
| `IVhrAchievements` | `UnlockAsync(slug)` → `VhrUnlockResult { unlocked, queued }` (1.12.0+), `GetForGameAsync`, `GetForCurrentGameAsync`, `GetMineAsync`, `GetMineForCurrentGameAsync`, `GetMyGamesAsync`, `GetPlatformAsync` |
| `IVhrLeaderboard` | `SubmitAsync(score)` / `SubmitAsync(board, score[, options])` → `VhrScoreResult` (`queued` — нет связи, счёт в очереди), `GetTopAsync(board, period, limit)` → `VhrBoardPage` (ники, место игрока), `GetBoardsAsync()` → `VhrBoardInfo[]`, `GetGlobalAsync(limit)` · `VhrScoreFormatter` · `GetForGameAsync`/`SubmitScoreAsync` — `[Obsolete]` |
| `IVhrTournaments` | `ListAsync`, `GetStandingsAsync`, `JoinAsync`, `SubmitAsync(id, score)` → `VhrTournamentScoreResult { bestScore, queued }` (1.12.0+), `SubmitScoreAsync` |
| `IVhrConnection` | `IsOnline` + `OnlineChanged`, `PendingCount` + `PendingChanged`, `FlushAsync`, `OnDropped` — связь и очередь отложенных операций (1.12.0+) |
| `IVhrServers` | `BindAsync`, `ListBindingsAsync`, `RequestInstanceAsync` (по умолчанию noop) |
| `IVhrAccess` | `GetAsync` → `VhrAccessState { GameType, Access, Source, PriceCoins, InSubscription, HasDemo, IsFull, IsDemo }`, `Current`, событие `AccessChanged`, `OpenPurchaseAsync` → `VhrAccessPurchaseResult { Status, State }` — премиум-игры (1.13.0+) |
| `IVhrAds` | `ShowInterstitialAsync`, `ShowRewardedAsync` (+ колбэк-версии) → `VhrAdResult { Status }` · события `OnAdOpened` / `OnAdClosed` · `AutoPause`, `IsSupported`, `IsShowing` (в премиум-игре — `Unavailable`) |
| `IVhrSaves` | `LoadAsync` / `LoadJsonAsync<T>`, `SaveAsync` / `SaveJsonAsync<T>`, `DeleteAsync`, `ListAsync`, автосохранение `QueueSave` / `QueueSaveJson<T>` + `FlushAsync` · события `OnConflict` / `OnAutoSaveResult` · `HasPendingSaves`, `Limits` |
| `IVhrSession` | `GameId`, `CurrentToken`, `State`, `Observable<VhrConnectionState> StateChanged` |
| `VhrSdk` (static) | `InitializeAsync`, `Economy`, `Access`, `Ads`, `Saves`, `Leaderboard`, `Achievements`, `Tournaments`, `Connection`, `Servers`, `Session`, `ConnectionState` |

Покупки **идемпотентны** через `externalId` (по умолчанию — новый GUID);
передайте свой стабильный id, чтобы повтор не купил дважды.

Меню Editor: **`VHR → Тестирование в Editor`** (песочный ключ, Simulation / Live,
доступ к премиум-игре, проверка API, сброс), **`VHR → Каталог игры`** (товары и достижения, генерация
`VhrCatalogIds.cs`), **`VHR → ИИ-ассистент (MCP)`** (конфиги MCP для Claude Code, Cursor и VS Code — см.
[ИИ-ассистенты](#ии-ассистенты-mcp)), **`VHR → Обновление SDK`** (версии, что нового, обновление одной кнопкой),
`VHR → Собрать серверный билд`.

Полные форматы запросов/ответов: [Documentation~/index.md](Documentation~/index.md).

---

## Лицензия

См. [LICENSE.md](LICENSE.md). © VHR Games.
