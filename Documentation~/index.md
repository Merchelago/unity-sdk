# VHR Games SDK — Справочник

- [1. Почему SDK обязателен](#1-почему-sdk-обязателен)
- [2. Контракт маркера сборки `vhr-sdk.json`](#2-контракт-маркера-сборки-vhr-sdkjson)
- [3. Установка](#3-установка)
- [3a. Где взять данные и как работает авторизация](#3a-где-взять-данные-и-как-работает-авторизация)
- [3b. Жизненный цикл токена](#3b-жизненный-цикл-токена-игровой-токен--60-мин)
- [3c. Модель экономики: пополнение — на платформе, в играх — только покупки](#3c-модель-экономики-пополнение--на-платформе-в-играх--только-покупки)
- [3d. Покупка: намерение → подтверждение игроком → итог](#3d-покупка-намерение--подтверждение-игроком--итог)
- [3e. Каталог игры: товары и достижения](#3e-каталог-игры-товары-и-достижения)
- [3f. Тестирование в Unity Editor](#3f-тестирование-в-unity-editor-simulation--live-песочница)
- [3g. Миграция с 1.8 на 1.9](#3g-миграция-с-18-на-19)
- [4. Конфигурация (`VhrSdkOptions`)](#4-конфигурация-vhrsdkoptions)
- [5. Инициализация и состояние подключения](#5-инициализация-и-состояние-подключения)
- [6. Справочник API](#6-справочник-api)
- [7. Модель идемпотентности](#7-модель-идемпотентности)
- [8. Траблшутинг](#8-траблшутинг)

---

## 1. Почему SDK обязателен

Издательский бэкенд VHR не примет сборку, которую не может верифицировать.
Верификация опирается на единственный артефакт — файл-маркер `vhr-sdk.json`,
который **пишет только этот SDK** (через editor build-хук `VhrSdkBuildMarker`).
Сборка, не интегрировавшая SDK, не содержит маркера и отклоняется на
`confirm-upload`.

```
Unity build ──► VhrSdkBuildMarker (IPreprocessBuildWithReport)
                    └─ пишет Assets/StreamingAssets/vhr-sdk.json
Unity пакует StreamingAssets ──► Build/StreamingAssets/vhr-sdk.json
Загрузка артефакта ──► бэкенд confirm-upload
                    ├─ маркер есть и версия ОК ─────► опубликовано
                    ├─ маркера нет ────────────────► 4xx  sdk_required
                    └─ версия маркера слишком низкая ► 4xx  sdk_outdated
```

## 2. Контракт маркера сборки `vhr-sdk.json`

Пишется в `Assets/StreamingAssets/vhr-sdk.json` перед каждой сборкой и попадает
без изменений внутрь вывода сборки (`StreamingAssets/vhr-sdk.json`).

```json
{
  "sdk": "vhr-unity-sdk",
  "sdkVersion": "1.0.0",
  "unityVersion": "6000.0.23f1",
  "buildTimeUtc": "2026-05-17T10:22:31.004Z",
  "buildTarget": "WebGL"
}
```

| Поле | Тип | Значение | Правило бэкенда |
|---|---|---|---|
| `sdk` | string | Логический id SDK, константа `vhr-unity-sdk` | Должно быть равно `vhr-unity-sdk` |
| `sdkVersion` | string (semver) | Версия этого пакета | Должна быть ≥ минимума бэкенда, иначе `sdk_outdated` |
| `unityVersion` | string | `Application.unityVersion` | Информационно / телеметрия |
| `buildTimeUtc` | string (ISO-8601 UTC) | Когда маркер записан | Информационно |
| `buildTarget` | string | Платформа `BuildReport` | Информационно |

Бэкенд читает этот файл из загруженного WebGL-бандла во время `confirm-upload`.
**Файла нет ⇒ `sdk_required` ⇒ сборка не публикуется.** Не редактируйте и не
удаляйте маркер вручную — build-хук перезаписывает его каждую сборку.

## 3. Установка

R3 и VContainer внешние (OpenUPM). Точные шаги по реестру/скоупам — в корневом
[README](../README.md#установка).

## 3a. Где взять данные и как работает авторизация

| Что | Где взять | Секрет? |
|---|---|---|
| **`GameId`** | ID игры из **кабинета разработчика** на сайте, страница `/dev/games`. Присваивается автоматически при загрузке игры. Шлётся как `X-Vhr-Game-Id`. | Нет |
| **JWT игрока (авторизация клиента)** | Сайт VHR встраивает WebGL-игру с `?access_token=<jwt>` в URL страницы. Дефолтный `TokenProvider` на WebGL сам читает `access_token` из `Application.absoluteURL` и шлёт его как `Authorization: Bearer <jwt>`. **Ничего секретного в сборку класть не нужно.** | Токен игрока, выдаётся в рантайме |
| **`InternalApiKey`** | Только для **серверных игр / server-to-server**. Шлётся как `X-Internal-Api-Key`, и только если непуст. | **Да — НИКОГДА не в клиентскую WebGL-сборку** |

**Модель.** Клиентские WebGL-игры авторизуются **только JWT игрока** — общего
ключа в публичном билде нет. Мост (`GameBridgeMS`) принимает
`Authorization: Bearer <jwt>` и привязывает каждую операцию экономики к
пользователю из токена (self-операции). `userId` в методах сохранён для
стабильности API и серверных сценариев: для self-операций его можно передавать
своим (рекомендуется) или оставлять пустым — мост резолвит/форсит пользователя
по токену. `X-Internal-Api-Key` стал **опциональным** и предназначен только для
серверного / выделенного-сервера сценария.

**Дефолтный провайдер токена.** Если `TokenProvider == null`,
`VhrSdkOptions.Validate()` подставляет
`VhrSdkOptions.DefaultWebGlTokenProvider()` — он парсит `access_token` из строки
запроса либо фрагмента URL страницы, URL-декодирует значение и устойчив к
пустому/некорректному URL. В **нативных / редакторных** сборках параметра в URL
нет — там задавайте `TokenProvider` явно (например,
`TokenProvider = () => MyHostAuth.CurrentPlayerJwt`).

## 3b. Жизненный цикл токена (игровой токен — 60 мин)

**Зачем.** Сайт VHR встраивает игру в `<iframe>` (origin = `api.vhrweb.ru`),
URL которого **один раз при загрузке** несёт `?access_token=<jwt>`. Этот JWT
игрока живёт ограниченное время (игровой токен SDK 1.9 — **60 минут**), а игровые сессии бывают длиннее. После истечения
вызовы моста начинают возвращать `401`. Перечитать `?access_token` из URL
бесполезно — там лежит тот же первоначальный (уже протухший) токен.

**Как (автоматически на сайте VHR).** Жизненный цикл закрыт через
`postMessage` между родительской страницей и iframe игры:

```
Родитель vhrgames.ru ──{ type:'vhr:sdk:token', token:'<свежий jwt>' }──► iframe (SDK)
iframe (SDK)          ──{ type:'vhr:sdk:token-request' }──────────────► Родитель vhrgames.ru
```

- WebGL-плагин `VhrSdkBridge.jslib` при загрузке вешает
  `window.addEventListener('message', …)` и принимает сообщения **строго** с
  `event.origin ∈ { https://vhrgames.ru, http://localhost:5173 }`
  (второй — для локального дев-режима). Сообщение
  `{ type:'vhr:sdk:token', token }` сохраняет свежий токен в JS.
- Дефолтный WebGL `TokenProvider` на каждый запрос сначала берёт **последний**
  токен из канала (`VhrWebGlTokenChannel.GetLatestToken()`), и только если
  родитель ещё ничего не прислал — fallback на разбор первичного
  `?access_token` из URL.
- **Поток `401 → refresh → retry` (ровно один повтор):** когда мост отвечает
  `401`, `VhrApiClient` вызывает `VhrWebGlTokenChannel.RequestRefresh()` —
  iframe постит родителю `{ type:'vhr:sdk:token-request' }`. SDK ждёт до **~5 с**
  (кооперативно, через `Awaitable`, без потоков — WebGL-safe), опрашивая
  провайдер, пока не появится **новый** токен (отличный от только что
  использованного). Получив его — **повторяет запрос ровно один раз** с новым
  `Authorization: Bearer …`. Если снова `401` (или новый токен не пришёл за
  5 с) — ошибка возвращается как обычно (код `unauthorized`).
- Вне WebGL `postMessage` нет: SDK делает один перечит `TokenProvider` (вдруг
  ваша система авторизации уже подложила свежий токен) и при наличии нового
  значения повторяет запрос один раз.

**SDK 1.9.0 — игровой токен.** Хост передаёт игре тем же сообщением `vhr:sdk:token`
**игровой** токен (audience `…#game`, claims `gid`, `sub`, `sbx`), срок 60 минут, и
обновляет его по `vhr:sdk:token-request`. SDK дополнительно обновляет его **заранее**:
если до истечения меньше 5 минут — фоново шлёт `token-request` (не чаще раза в 30 с);
если токен уже истёк — перед запросом шлёт `token-request` и ждёт новый до ~3 с (не
больше одного раза на конкретный токен, чтобы сбитые часы браузера не тормозили
каждый запрос). Обновление по `401` (повтор ровно один раз) работает как раньше.

**Что должен делать разработчик игры.** На сайте VHR — **ничего**: обновление
полностью автоматическое и прозрачно для кода игры (родительский хост
`vhrgames.ru` обновляется отдельно и отвечает на запрос токена). Для
**нативных / редакторных** сборок задайте `TokenProvider`, который возвращает
*актуальный* (обновляемый вашей системой авторизации) JWT — SDK сам перечитает
его на `401`.

> Контракт сообщений (фиксирован): тип `vhr:sdk:token` (родитель→игра, с полем
> `token`) и `vhr:sdk:token-request` (игра→родитель). Разрешённые origin:
> `https://vhrgames.ru` (прод) и `http://localhost:5173` (дев).

## 3c. Модель экономики: пополнение — на платформе, в играх — только покупки

**1 монета = 1 рубль.** Игроки пополняют счёт **только на платформе**, а в играх
только тратят — на товары каталога игры и только с подтверждением в окне платформы.
Это проверяет сервер (мост), а SDK 1.9.0 не даёт даже отправить запрещённый запрос.

| Операция | Из игры (игровой токен) | Серверная интеграция (`X-Internal-Api-Key`) |
|---|---|---|
| `GetBalanceAsync` — чтение баланса | ✅ | ✅ |
| `GetItemsAsync` / `GetOwnedItemsAsync` — каталог / купленное | ✅ | ✅ |
| `PurchaseAsync` — покупка товара каталога | ✅ **с подтверждением игрока** | — |
| `OpenTopUp` — пополнение | ✅ открывает окно **платформы** | — |
| `GrantAchievementAsync` — факт анлока | ✅ записывается, **монетная награда = 0** | ✅ |
| `SpendAsync` — прямое списание | ⛔ отключено в SDK (`spend_disabled`), мост — `403` | ⛔ |
| `GrantCoinsAsync` — начисление монет | ⛔ отключено в SDK (`grant_disabled`), мост — `403` | ⛔ |

**Что запрещено:** пополнение счёта внутри игры (свои окна «купить монеты»),
прямые списания и начисления монет, сторонние платёжные системы и внешние ссылки на
оплату — они блокируются платформой (сетевые запросы игры к внешним доменам
блокируются), покупка без подтверждения игроком.

## 3d. Покупка: намерение → подтверждение игроком → итог

```
PurchaseAsync(null, itemId, quantity, externalId)
 1. POST {Bridge}/api/purchase   { userId, itemId, quantity, externalId, gameId }
      202 { status:"confirmation_required", intentId, itemId, title, quantity, price, expiresAt }
      200 { status:"completed", purchaseId, balance }        ← идемпотентный повтор externalId
      426 sdk_update_required · 403 game_token_forbidden | game_mismatch | parental_block
      429 daily_limit · 409 insufficient_funds
 2a. WebGL на vhrgames.ru:
      iframe → родитель  { type:'vhr:purchase:confirm', intentId, requestId }
      родитель → iframe  { type:'vhr:purchase:result', requestId, intentId,
                           status:'completed'|'cancelled'|'insufficient_funds'|'expired'|'error',
                           balance?, purchaseId? }
      Ждём до 3 минут (намерение живёт 120 с, хост сам пришлёт expired), затем Expired.
 2b. Unity Editor (Live): диалог-имитация окна платформы →
      POST {Bridge}/api/purchase-intents/{intentId}/confirm   (только песочные намерения)
      POST {Bridge}/api/purchase-intents/{intentId}/cancel
 2c. Unity Editor (Simulation): всё локально, без сервера.
 3. VhrPurchaseResult { Status, PurchaseId, Balance/HasBalance, Title, Price, IntentId, ErrorCode, Message }
```

- Ответы хоста принимаются **только** от `window.parent` с origin платформы (тот же
  фильтр, что у токена), `requestId` и `intentId` сверяются; на каждый запрос —
  ровно один итог (от хоста или по таймауту).
- WebGL вне страницы платформы → сразу `Error`/`not_hosted` (намерение даже не создаётся);
  нативные сборки → `Error`/`confirmation_unavailable`.
- Одна покупка за раз; повторный вызов во время текущей → `Error`/`busy`.
- `PurchaseAsync` не бросает исключений, кроме `OperationCanceledException` при отмене
  `ct` (окно на сайте при этом не закрывается — если игрок подтвердит, придёт `OnBalanceChanged`).

**Пополнение.** `OpenTopUp()` → `postMessage { type:'vhr:topup:open' }` родителю; сайт
открывает окно пополнения платформы. Хост сообщает новый баланс
`{ type:'vhr:balance:changed', balance }` → `IVhrEconomy.OnBalanceChanged` и R3-поток
`BalanceChanged` (они же срабатывают после успешной покупки). В Unity Editor
`OpenTopUp` открывает `https://vhrgames.ru/profile?tab=shop` в браузере.

| `VhrPurchaseStatus` | Значение |
|---|---|
| `Completed` | Подтверждено, монеты списаны — выдайте товар |
| `Cancelled` | Игрок отменил |
| `InsufficientFunds` | Не хватает монет — предложите `OpenTopUp()` |
| `Expired` | Не подтвердил вовремя / окно не ответило |
| `Forbidden` | `parental_block`, `daily_limit`, `game_token_forbidden`, `game_mismatch` (см. `ErrorCode`) |
| `SdkUpdateRequired` | `426` — обновите SDK |
| `Error` | Сеть, `not_hosted`, `busy`, `not_found` и т.п. |

## 3e. Каталог игры: товары и достижения

Товары и достижения заводятся в кабинете разработчика на сайте (игра → **Товары** /
**Достижения**; `https://vhrgames.ru/dev/games`, ачивки — `/dev/games/{gameId}/achievements`).
В игре — только чтение. Id игры SDK берёт из claim `gid` токена, иначе `VhrSdkOptions.GameId`.

| Метод | HTTP | Ответ |
|---|---|---|
| `Economy.GetItemsAsync()` | `GET {Bridge}/api/items?gameId=<gid>` | `VhrCatalogItem[]` — `id` (в `PurchaseAsync`), `code`, `title`, `description`, `priceCoins`, `active`, `createdAt`, `iconUrl` |
| `Economy.GetOwnedItemsAsync()` | `GET {Bridge}/api/inventory?gameId=<gid>` | `VhrOwnedItem[]` — `itemId`, `quantity`, `lastPurchasedAt` |
| `Achievements.GetForCurrentGameAsync()` | `GET {Games}/api/Achievements/games/<gid>` | `VhrAchievement[]` |
| `Achievements.GetMineForCurrentGameAsync()` | `GET {Games}/api/Achievements/me` + фильтр по игре | `VhrUserAchievement[]` |

В Unity Editor каталог виден в окне **`VHR → Каталог игры`** (песочный ключ): товары и
достижения с ID, ценой/описанием, иконкой и статусом, «Копировать ID», «Обновить»,
«Открыть в кабинете» и **«Сгенерировать C#-константы»** → `VhrCatalogIds.cs`:

```csharp
public static class VhrCatalogIds
{
    public const string GameId = "…";
    public static class Items { /// <summary>Золотой меч — 150 монет</summary>
        public const string SwordGold = "<id товара>"; }
    public static class Achievements { /// <summary>Первый босс — 10 очк.</summary>
        public const string FirstBoss = "game.<gid>.first_boss"; }
}
```

Идентификаторы — из ключа (`code`) или названия (кириллица транслитерируется), без
коллизий (`Sword`, `Sword_2`); повторная генерация перезаписывает файл. Окно только
читает: создание и редактирование — в кабинете (у песочного ключа нет таких прав).

## 3f. Тестирование в Unity Editor (Simulation / Live-песочница)

`VhrSdkOptions.EditorMode` (`Auto` по умолчанию — как выбрано в окне
**`VHR → Тестирование в Editor`**; без ключа — Simulation). В сборках не используется.

| Режим | Экономика | Остальные сервисы | Реклама |
|---|---|---|---|
| **Simulation** | Локально: баланс 10 000 (`SessionState`), диалог подтверждения, цены `SimulatedItemPrices` / `SimulatedDefaultItemPrice`, «инвентарь» сеанса | Как в 1.8 (HTTP с вашим `TokenProvider`) | Симуляция |
| **LiveSandbox** | Настоящий мост: намерение → диалог → `purchase-intents/{id}/confirm` | Настоящий сервер: Achievements, Friends, GameSessions, Leaderboard, PlayerStats, Profile (`/api/Auth/me` → «Тестовый игрок», `isSandbox`), Tournaments, Servers, Lobby/Relay | Симуляция (доход не репортится) |

В LiveSandbox `Validate()` подставляет `TokenProvider = () => <песочный ключ>`
(`Authorization: Bearer`) и `GameId = gid` ключа (подпись не проверяется — это делает
сервер). Ключ должен быть песочным (`sbx = "1"`), игровым (`aud …#game`), с `gid` и не
истёкшим — иначе ошибка в консоли и откат в Simulation. Адреса — продовые
(`BridgeBaseUrl`, `GamesBaseUrl`, `AuthBaseUrl`, `ServersBaseUrl`, `RelayBaseUrl`);
поле «Сервер» в окне переопределяет их для dev-стенда (`{сервер}/bridge`, `/games`, …).

**Шаги:** ключ на сайте (кабинет → игра → «Тест в Unity Editor», 30 дней) → окно VHR →
вставить → «Проверить» (`GET sandbox/me`: игра, тестовый игрок `sbx_*`, баланс, срок) →
режим **Live** → Play → тест покупок, лидербордов, ачивок → **«Сбросить тестовые данные»**
(`POST sandbox/reset`: баланс 10 000, покупки и инвентарь по игре очищены). Кнопка
«Проверить все API» делает read-only GET ко всем сервисам и показывает коды ответов.

**Безопасность ключа.** Хранится только в `EditorPrefs` (имя уникально для проекта —
хеш `Application.dataPath`), читается только под `#if UNITY_EDITOR` (код в сборку не
компилируется), ни в какой ассет не сериализуется. `VhrSandboxKeyBuildGuard`
(`IPreprocessBuildWithReport`) перед сборкой ищет ключ в сценах, префабах, `.asset`,
ProjectSettings, скриптах и предупреждает (в интерактивном режиме — с вопросом
«продолжить сборку?»).

## 3g. Миграция с 1.8 на 1.9

- `SpendAsync` / `GrantCoinsAsync` — `[Obsolete]`, сразу `success = false`
  (`spend_disabled` / `grant_disabled`) без запроса. Продавайте товары каталога через `PurchaseAsync`.
- `PurchaseAsync` возвращает `VhrPurchaseResult` (статусы выше). Код 1.8 (`r.success`,
  `r.balance`, `VhrEconomyResult r = await …`) компилируется с предупреждением; обработайте
  `Cancelled` / `Expired` / `Forbidden`.
- `userId` можно передавать `null`. «Пополнить» → `OpenTopUp()`, HUD → `OnBalanceChanged`.
- Старые SDK получают `426 sdk_update_required` на покупках — обновитесь до 1.9.0.

## 4. Конфигурация (`VhrSdkOptions`)

| Поле | По умолчанию | Примечания |
|---|---|---|
| `BridgeBaseUrl` | `https://api.vhrweb.ru/bridge` | Корень экономики/лидерборда/ping; вызовы идут на `{base}/api/...` |
| `ServersBaseUrl` | `https://api.vhrweb.ru/servers` | Корень привязки серверов; вызовы идут на `{base}/api/...` |
| `GameId` | — (обязательно) | ID игры из кабинета разработчика (`/dev/games`). Шлётся как `X-Vhr-Game-Id`. Не секрет |
| `InternalApiKey` | — (опционально) | **Только серверные сборки.** Шлётся как `X-Internal-Api-Key`, только если непуст. **НЕ задавать в клиентских WebGL** — секрет утечёт |
| `TokenProvider` | `null` → дефолт WebGL | Авторизация (JWT игрока). Если `null`, `Validate()` подставит `DefaultWebGlTokenProvider()`: сперва свежий токен из `postMessage`-канала (`VhrWebGlTokenChannel`), иначе fallback на `access_token` из URL. Лениво резолвится на каждый запрос → `Authorization: Bearer …`. На 401 SDK сам запросит обновление и повторит запрос (см. [3b](#3b-жизненный-цикл-токена-игровой-токен--60-мин)) |
| `PingOnInitialize` | `true` | `GET /api/ping` во время init; отражается в состоянии подключения |
| `RequestTimeoutSeconds` | `15` | Таймаут на запрос |
| `VerboseLogging` | `false` | Логирует строки запрос/ответ |
| `EditorMode` | `Auto` | Только Unity Editor: `Auto` (как в окне VHR) / `Simulation` / `LiveSandbox`. См. [3f](#3f-тестирование-в-unity-editor-simulation--live-песочница) |
| `SimulatedDefaultItemPrice` | `100` | Цена товара за 1 шт. в режиме Simulation |
| `SimulatedItemPrices` | `null` | `itemId → цена` для Simulation (и локальный «каталог» `GetItemsAsync`) |
| `IsEditorSimulation` / `IsEditorLiveSandbox` | — | Только чтение, после `Validate()`: итоговый режим в Editor (в сборках `false`) |

`Validate()` бросает `VhrSdkException("config_invalid", …)` при отсутствии
`GameId` / базовых URL или неверном таймауте, подставляет дефолтный WebGL
`TokenProvider` (если он `null`) и обрезает хвостовые слэши у базовых URL.
`InternalApiKey` **не требуется** — клиент авторизуется JWT игрока.

## 5. Инициализация и состояние подключения

`VhrSdk.InitializeAsync(options)` (без DI) или `VhrSdkEntryPoint` из
`VhrSdkLifetimeScope` (DI) выполняют одну и ту же процедуру:

1. `options.Validate()`
2. Сборка графа сервисов (`IVhrHttp` → `VhrApiClient` → сервисы)
3. Лог `Initializing VHR SDK v1.0.0 …`
4. Состояние → `Connecting`
5. Если `PingOnInitialize`: `GET {BridgeBaseUrl}/api/ping`
   - успех → `Connected`
   - ошибка → `Faulted` (не фатально; вызовы повторяются по требованию)
6. `IsInitialized = true`

Подписка через R3:

```csharp
VhrSdk.ConnectionState.Subscribe(state => { /* Connecting/Connected/Faulted */ });
```

`VhrConnectionState`: `Uninitialized | Connecting | Connected | Faulted`.

## 6. Справочник API

Все эндпоинты ниже относительны `BridgeBaseUrl`, если не указано иное. Каждый
запрос несёт `X-Vhr-Game-Id`, `X-Vhr-Sdk-Version`, `Authorization: Bearer <jwt>`
(JWT игрока — основной путь авторизации клиента, если токен доступен) и
`X-Internal-Api-Key` **только** при заданном `InternalApiKey` (серверный
сценарий; пустой заголовок не отправляется).

### 6.1 `IVhrEconomy`

| Член | HTTP | Доступ из игры | Результат |
|---|---|---|---|
| `GetBalanceAsync(userId?)` | `GET /api/balance[/{userId}]` | ✅ (`userId` можно `null`) | `VhrBalance { userId, coins, balance }` |
| `GetItemsAsync()` | `GET /api/items?gameId=<gid>` | ✅ | `VhrCatalogItem[]` |
| `GetOwnedItemsAsync()` | `GET /api/inventory?gameId=<gid>` | ✅ | `VhrOwnedItem[]` |
| `PurchaseAsync(userId?, itemId, quantity, externalId?, ct)` | `POST /api/purchase` → подтверждение игроком (см. [3d](#3d-покупка-намерение--подтверждение-игроком--итог)) | ✅ | `VhrPurchaseResult` |
| `Purchase(itemId, quantity, onComplete, externalId?)` | то же, колбэк | ✅ | `Action<VhrPurchaseResult>` |
| `OpenTopUp()` | `postMessage vhr:topup:open` (Editor — браузер) | ✅ | — |
| `IsPurchaseAvailable` | — | — | можно ли показывать магазин |
| `event Action<long> OnBalanceChanged`, `Observable<BalanceChanged> BalanceChanged` | `vhr:balance:changed` / после покупки | — | новый баланс |
| `GrantAchievementAsync(userId, achievementId, externalId?)` | `POST /api/grant/achievement` | ⚠️ анлок записывается, монеты = 0 | `VhrEconomyResult` |
| `SpendAsync(...)` `[Obsolete]` | — (запрос не отправляется) | ⛔ `spend_disabled` | `VhrEconomyResult { success=false, code }` |
| `GrantCoinsAsync(...)` `[Obsolete]` | — (запрос не отправляется) | ⛔ `grant_disabled` | `VhrEconomyResult { success=false, code }` |

Тело `POST /api/purchase`: `{ userId, itemId, quantity, externalId, gameId }` (заголовки
`X-Vhr-Game-Id`, `X-Vhr-Sdk-Version: 1.9.0`, `Authorization: Bearer <игровой токен>`).

### 6.2 `IVhrLeaderboard`  *(seam следующей волны)*

| Метод | HTTP | Запрос | Ответ |
|---|---|---|---|
| `SubmitAsync(userId, score)` | `POST /api/leaderboard/submit` | `{ userId, score }` | `{ accepted }`; **501 → возвращает `false`** |
| `GetTopAsync(period, limit)` | `GET /api/leaderboard/top?period=&limit=` | — | `VhrLeaderboardPage`; **501 → `{ notImplemented:true, entries:[] }`** |

Серверная персистентность ещё не поставлена; мост отвечает `501`. Клиент **не
бросает** исключение на 501, чтобы код игры можно было интегрировать уже сейчас
и он заработал, когда бэкенд будет готов.

### 6.3 `IVhrServers`  *(по умолчанию noop)*

| Метод | HTTP | Запрос | Ответ |
|---|---|---|---|
| `BindAsync(gameId, region?)` | `POST /api/bindings` | `{ gameId, region }` | `VhrServerBinding`; нет бэкенда → `{ status:"noop", endpoint:"" }` |
| `ListBindingsAsync(gameId)` | `GET /api/bindings?gameId=` | — | `VhrServerBinding[]` (пусто, если нет) |
| `RequestInstanceAsync(bindingId)` | `POST /api/instances/request` | `{ bindingId }` | `VhrServerBinding`; по умолчанию noop |

Базовый URL — `ServersBaseUrl`. Клиентские игры это не вызывают; `noop`-биндинг
означает «запускать локально».

### 6.4 `IVhrSession`

`GameId`, `CurrentToken`, `State`, `Observable<VhrConnectionState> StateChanged`
(повторяет текущее значение новым подписчикам).

### 6.5 Статический фасад `VhrSdk`

`InitializeAsync`, `IsInitialized`, `Economy`, `Ads`, `Leaderboard`, `Servers`,
`Achievements`, `Profile`, `Friends`, `PlayerStats`, `GameSessions`, `Tournaments`,
`Relay`, `Lobby`, `Session`, `ConnectionState`, `SdkVersion`, `ResetForTests()`.

### 6.6 Транспортный seam

`IVhrHttp` (по умолчанию `UnityWebRequestHttp`, WebGL-safe) внедряется для
тестов; `VhrApiClient` обрабатывает заголовки, JSON через `JsonUtility` и
маппит не-2xx в `VhrSdkException` (коды: `unauthorized`, `forbidden`,
`not_found`, `conflict`, `sdk_update_required` (426), `rate_limited` (429),
`not_implemented`, `connection_error`, `http_error`, `deserialize_error`); код из
тела ответа моста (`{ "code": "..." }`) — в `ServerCode`, тело — в `ResponseBody`.

### 6.7 `IVhrAds` — реклама (SDK 1.8.0+)

Доступ: `VhrSdk.Ads` или внедрение `IVhrAds` (VContainer) — один общий экземпляр.

| Член | Описание |
|---|---|
| `Task<VhrAdResult> ShowInterstitialAsync(ct)` | Полноэкранная реклама в паузе игры. Без награды: успех = `Closed` |
| `Task<VhrAdResult> ShowRewardedAsync(ct)` | Реклама за награду, только по явному согласию игрока. `Rewarded` → игра выдаёт СВОЮ награду |
| `void ShowInterstitial(Action<VhrAdResult>)` / `void ShowRewarded(Action<VhrAdResult>)` | Колбэк-версии для кода без async |
| `event Action<VhrAdKind> OnAdOpened` | Реклама открывается — пауза + тишина |
| `event Action<VhrAdResult> OnAdClosed` | Реклама закрыта — снять паузу (парно с `OnAdOpened`) |
| `bool AutoPause` | Встроенная авто-пауза (`Time.timeScale`, `AudioListener.pause`, курсор). По умолчанию `VhrSdkOptions.AdsAutoPause` = `false` |
| `bool IsSupported` / `bool IsShowing` | Есть ли хост VHR (WebGL) / идёт ли показ |

`VhrAdResult`: `Status` (`Rewarded | Closed | Unavailable | Cooldown | Error`), `Kind`,
`RequestId`, `Reason` (для логов), `IsRewarded`, `WasShown`.

Как это устроено: игра шлёт родителю (страница vhrgames.ru)
`postMessage { type:'vhr:ads:show', kind:'interstitial'|'rewarded', requestId }`; страница
отвечает `{ type:'vhr:ads:opened', requestId }` и затем
`{ type:'vhr:ads:result', requestId, status }`, сама показывает полноэкранный блок
Рекламной сети Яндекса и сама учитывает показ (доход разработчика — 10% дохода платформы
от рекламы в его игре, доступен к выводу после закрытия отчётного периода). Таймауты:
нет ответа хоста 5 с → `Unavailable`; показ дольше 5 мин → `Closed`. Вне хоста VHR —
`Unavailable`; в редакторе/не-WebGL — симуляция (`AdsSimulation`, `AdsSimulationDelaySeconds`).

> Награда за rewarded — **только внутриигровая**. Платформенные монеты за рекламу не
> начисляются. `IVhrEconomy.ReportAdAsync` устарел (`[Obsolete]`): доход по нему больше не
> засчитывается.

## 7. Модель идемпотентности

Каждая покупка шлёт `externalId`. Если его не передать — SDK
генерирует GUID на вызов. Чтобы сделать *конкретное игровое событие* безопасным
к повторам через ретраи, краши и сессии, передайте свой стабильный id (напр.
`$"order-{orderId}"`). Мост дедуплицирует по `externalId`: повтор сразу вернёт
`{ status:"completed", purchaseId, balance }` без нового списания
(`VhrPurchaseResult.IdempotentReplay = true`).

## 8. Траблшутинг

| Симптом | Причина | Решение |
|---|---|---|
| Загрузка отклонена `sdk_required` | В сборке нет `vhr-sdk.json` | Убедитесь, что пакет установлен; проверьте в консоли `[VHR SDK] Wrote build marker`; убедитесь, что есть `Build/StreamingAssets/vhr-sdk.json` |
| Загрузка отклонена `sdk_outdated` | Версия маркера ниже минимума бэкенда | Обновите `ru.vhrgames.sdk`, пересоберите, загрузите снова |
| `config_invalid` при init | Нет `GameId` / базовых URL / неверный таймаут | Заполните `VhrSdkOptions.GameId` (из `/dev/games`). `InternalApiKey` НЕ требуется. `TokenProvider == null` — норма: на WebGL подставится дефолт |
| `unauthorized` (401) на WebGL после долгой сессии | Токен протух (игровой — 60 мин), родитель не прислал свежий токен | SDK сам делает `token-request` к `vhrgames.ru` и повторяет запрос. Если 401 остаётся: игра открыта не со страницы VHR / родитель не отвечает на `vhr:sdk:token-request` / origin не `https://vhrgames.ru`. См. [3b](#3b-жизненный-цикл-токена-игровой-токен--60-мин) |
| `unauthorized` (401) на WebGL сразу | В URL страницы нет `access_token` | Игра должна быть открыта со страницы VHR (сайт прокидывает `?access_token`). Проверьте `Application.absoluteURL`. Не задавайте свой `InternalApiKey` в клиенте |
| `unauthorized` (401) в нативной/редакторной сборке | `TokenProvider` не задан или возвращает протухший токен | Задайте `TokenProvider`, возвращающий *актуальный* (обновляемый) JWT игрока из вашей системы авторизации — SDK перечитает его на 401 |
| `SpendAsync`/`GrantCoinsAsync` вернули `success=false`, `spend_disabled`/`grant_disabled` | С 1.9.0 списания и начисления из игры отключены | Ожидаемо. Продавайте товары каталога через `PurchaseAsync`. См. [3c](#3c-модель-экономики-пополнение--на-платформе-в-играх--только-покупки) |
| Покупка → `Error` / `not_hosted` | WebGL открыт не со страницы vhrgames.ru | Проверяйте на платформе или в Unity Editor (Simulation / Live) |
| Покупка → `Expired` через 3 минуты | Сайт не ответил на `vhr:purchase:confirm` (старая версия страницы) или игрок не подтвердил за 120 с | Обновите страницу; проверьте, что сайт поддерживает покупки 1.9 |
| Покупка → `Forbidden` (`game_mismatch`) | `GameId` в опциях не совпадает с `gid` токена | Возьмите `GameId` из `/dev/games` (в Editor Live он берётся из ключа) |
| Покупка → `Forbidden` (`parental_block` / `daily_limit`) | Родительский контроль / дневной лимит покупок | Ожидаемо — сообщите игроку |
| Покупка → `SdkUpdateRequired` | Мост требует SDK ≥ 1.9.0 (`426`) | Обновите пакет и пересоберите |
| Editor: «Live выбран, но …» и работа в Simulation | Нет ключа / ключ не песочный / истёк | `VHR → Тестирование в Editor` → вставьте ключ из кабинета → «Проверить» |
| Editor Live: `401` на всех запросах | Ключ отозван, истёк или скопирован не полностью | Получите новый ключ на сайте |
| Ачивка записалась, а монет не прибавилось | Монетная награда `grant/achievement` из игры форсится в 0 | Ожидаемо: игры не начисляют монеты |
| `not_implemented` не брошен, но нет данных | Seam лидерборда/серверов 501 | Ожидаемо, пока бэкенд не поставлен; гейтите UI по `notImplemented` / `status=="noop"` |
| Реклама всегда `Unavailable` на WebGL | Игра открыта не со страницы vhrgames.ru, реклама выключена платформой, детский аккаунт, адблок или нет объявления | Проверяйте на vhrgames.ru; это нормальный исход — продолжайте игру / прячьте кнопку |
| Реклама часто `Cooldown` | Лимиты частоты платформы (interstitial ~ раз в 90 с, rewarded ~ 20/ч) или повторный вызов во время показа | Не вызывайте чаще; предложите rewarded позже |
| Состояние подключения застряло в `Faulted` | Мост недоступен на init | Не фатально; вызовы ретраятся по требованию. Проверьте сеть/URL |
