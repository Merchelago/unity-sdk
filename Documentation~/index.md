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
  - [6.9 Работа без связи (`IVhrConnection`, SDK 1.12.0+)](#69-ivhrconnection--работа-без-связи-sdk-1120)
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

Проще всего — установщиком одним файлом `Installer~/VhrSdkInstaller.cs`
(`https://vhrgames.ru/downloads/VhrSdkInstaller.cs`): положите его в `Assets/Editor/`, он добавит
реестры OpenUPM (`com.cysharp`, `jp.hadashikick`) и UnityNuGet (`org.nuget`) и зависимости. R3
(`com.cysharp.r3` + ядро `org.nuget.r3`) и VContainer — зависимости пакета, ставятся через UPM
автоматически. Ручные шаги — в корневом [README](../README.md#установка), обновление —
[README → Обновление SDK](../README.md#обновление-sdk) (окно `VHR → Обновление SDK`, данные —
анонимный `GET {API}/games/api/Sdk/versions`).

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
В игре — чтение и (SDK 1.12.0+) выдача ачивок своей игры. Id игры SDK берёт из claim `gid` токена,
иначе `VhrSdkOptions.GameId`.

| Метод | HTTP | Ответ |
|---|---|---|
| `Economy.GetItemsAsync()` | `GET {Bridge}/api/items?gameId=<gid>` | `VhrCatalogItem[]` — `id` (в `PurchaseAsync`), `code`, `title`, `description`, `priceCoins`, `active`, `createdAt`, `iconUrl` |
| `Economy.GetOwnedItemsAsync()` | `GET {Bridge}/api/inventory?gameId=<gid>` | `VhrOwnedItem[]` — `itemId`, `quantity`, `lastPurchasedAt` |
| `Achievements.GetForCurrentGameAsync()` | `GET {Games}/api/Achievements/games/<gid>` | `VhrAchievement[]` |
| `Achievements.GetMineForCurrentGameAsync()` | `GET {Games}/api/Achievements/me` + фильтр по игре | `VhrUserAchievement[]` |
| `Achievements.UnlockAsync(slug)` (1.12.0+) | `POST {Games}/api/Achievements/unlock` `{ code: "game.<gid>.<slug>" }` | `VhrUnlockResult { unlocked, queued, code, achievement }`; без связи — в очередь ([6.9](#69-ivhrconnection--работа-без-связи-sdk-1120)) |

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
| **Simulation** | Локально: баланс 10 000 (`SessionState`), диалог подтверждения, цены `SimulatedItemPrices` / `SimulatedDefaultItemPrice`, «инвентарь» сеанса | Облачные сохранения — локально (`Library/VhrSdk/SimSaves`, лимиты сервера, см. [6.8](#68-ivhrsaves--облачные-сохранения-sdk-1110)); остальные — как в 1.8 (HTTP с вашим `TokenProvider`) | Симуляция |
| **LiveSandbox** | Настоящий мост: намерение → диалог → `purchase-intents/{id}/confirm` | Настоящий сервер: Saves, Achievements, Friends, GameSessions, Leaderboard, PlayerStats, Profile (`/api/Auth/me` → «Тестовый игрок», `isSandbox`), Tournaments, Servers, Lobby/Relay | Симуляция (доход не репортится) |

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
«Проверить все API» делает read-only GET ко всем сервисам и показывает коды ответов, плюс
цикл облачных сохранений на тестовом слоте `vhr_sdk_selftest`: `PUT` → `GET` (данные совпали) →
`PUT` с `expectedVersion` (версия +1) → `PUT` с устаревшей версией (`409 version_conflict`) →
`DELETE` → `GET` (`404 save_not_found`). Там же — объём локальных сохранений Simulation и их очистка.

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
| `SavesMinIntervalSeconds` | `10` | Автосохранение `IVhrSaves.QueueSave`: слот уходит не чаще раза в N секунд реального времени. Меньше 5 нельзя — `Validate()` поднимет до 5 (`MinSavesIntervalSeconds`). В `VhrSdkLifetimeScope` — поле инспектора |
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

### 6.2 `IVhrLeaderboard` — таблицы лидеров (несколько на игру с SDK 1.12.0)

Доступ: `VhrSdk.Leaderboard` или внедрение `IVhrLeaderboard` (VContainer). Базовый URL — `GamesBaseUrl`
(UnityGamesMS, `LeaderboardController`); `{gameId}` — `gid` токена, иначе `VhrSdkOptions.GameId`.
Отправка — только с токеном игрока (игровой или песочный); чтение — и анонимно.

**Code-first.** Таблица создаётся при первой отправке счёта с новым id (`board`); подсказки `title`,
`sort`, `format` применяются только при создании, потом таблицей управляет кабинет разработчика.
Id таблицы: `Trim().ToLowerInvariant()`, пусто → `default`, затем `^[a-z0-9][a-z0-9_-]{0,31}$` (SDK
проверяет до запроса: `VhrSdkException` с `Code = ServerCode = "bad_board"`). До 50 таблиц на игру.
Счёт — `long`, |score| ≤ 2^53−1 (иначе `bad_request` до запроса). Сервер хранит лучший результат по
направлению таблицы в бакетах `all`, `d:yyyy-MM-dd`, `w:YYYY-Www`, `m:yyyy-MM` (UTC); при равенстве
выше тот, кто достиг счёта раньше.

| Член | HTTP (`{GamesBaseUrl}`) | Запрос | Результат |
|---|---|---|---|
| `SubmitAsync(score)` | `POST /api/Leaderboard/score` | `{ gameId, score, board: "default", title: "", sort: "", format: "" }` | `VhrScoreResult` |
| `SubmitAsync(board, score)` | то же | `{ gameId, score, board, title: "", sort: "", format: "" }` | `VhrScoreResult` |
| `SubmitAsync(board, score, options)` | то же | `+ title (≤ 64), sort: "desc"\|"asc", format: "number"\|"time"` | `VhrScoreResult` |
| `GetTopAsync(board = null, period = AllTime, limit = 10)` | `GET /api/Leaderboard/games/{gameId}/boards/{board}?period=all\|day\|week\|month&limit=1..100` | — | `VhrBoardPage` (+ батч ников) |
| `GetBoardsAsync()` | `GET /api/Leaderboard/games/{gameId}/boards` | — | `VhrBoardInfo[]` |
| `GetGlobalAsync(limit = 100)` | `GET /api/Leaderboard/global?limit=` | — | `VhrLeaderboardRow[]` (основные таблицы «больше — лучше» всех игр) |
| `GetForGameAsync(gameId, limit = 100)` `[Obsolete]` | `GET /api/Leaderboard/games/{gameId}?limit=` | — | `VhrLeaderboardRow[]` (основная таблица, старый формат) |
| `SubmitScoreAsync(gameId, score)` `[Obsolete]` | `POST /api/Leaderboard/score` | `{ gameId, score, board: "default" }` | `VhrLeaderboardRow` (без клампа `long`) |

Вместо незаданных подсказок SDK шлёт пустые `title`/`sort`/`format` — сервер считает их «не задано» (`desc`, `number`, название по id). `gameId` в теле дублирует токен для совместимости.

**Ответы → типы SDK:**

- `POST score` → `{ id, userId, playerName, gameId, score, createdAt, board, improved, best, rank }` →
  `VhrScoreResult { accepted (true; false — только если сервер явно пришлёт "accepted": false), improved,
  best, rank, board }`. `board` принимается строкой или объектом `{ id, … }`.
- `GET boards/{board}` → `{ board: { id, title, sort, format, suffix, isPrimary }, period, entries: [ { rank,
  userId | null, score, updatedAt, isCurrentUser } ], me: { rank, score } | null, total }` →
  `VhrBoardPage { board: VhrBoardInfo, period: VhrLeaderboardPeriod, entries: VhrBoardEntry[], me: VhrBoardEntry
  | null, total }`. `JsonUtility` превращает `"me": null` в пустой объект — SDK отдаёт `me = null`, если
  `rank` не положительный. У `me` — `isCurrentUser = true`, `userId` из строки топа или `sub` токена.
  `404 board_not_found` (в таблицу ещё никто не отправлял счёт) → пустая страница (`entries = []`,
  `me = null`, `total = 0`, мета по умолчанию), а не исключение.
- `GET boards` → `{ primary, boards: [ { id, title, sort, format, suffix, isPrimary, hidden, order } ] }` →
  `VhrBoardInfo[]` по порядку вкладок. Нет таблиц — сервер отдаёт синтезированную `default`.
- `sort`: `desc` → `VhrBoardSort.HigherIsBetter`, `asc` → `LowerIsBetter`; `format`: `number` →
  `VhrScoreFormat.Number`, `time` → `Time` (маппинг ручной — `JsonUtility` не читает enum из строк).

**Ники.** `GetTopAsync` собирает `userId` строк и «меня» и резолвит их одним вызовом
`IVhrProfile.ResolveNamesAsync` (`POST {AuthBaseUrl}/api/Auth/users/resolve`, пачки по 100). Скрытый профиль
(`userId = null`), пустой ник, ошибка резолва — `"Игрок"` (страница не падает); `sbx_*` — `"Тестовый игрок"`
без запроса. `VhrSdk` и `VhrSdkLifetimeScope` передают сервису общий `IVhrProfile`; конструктор без профиля
создаёт свой `VhrProfileService`.

**Формат** (`VhrScoreFormatter`, `VhrBoardEntry.formatted`): `Number` — разряды через пробел + суффикс
таблицы (`12345`, «очков» → «12 345 очков»); `Time` — миллисекунды → `m:ss.fff` или `h:mm:ss.fff`
(`62345` → «1:02.345», `3723004` → «1:02:03.004»), суффикс не добавляется.

**Ошибки:** `400 bad_board` / `bad_request`, `409 board_limit` (`Code = "conflict"`), `401` (гость на
отправке) — `VhrSdkException` с кодом сервера в `ServerCode`.

**Без связи (SDK 1.12.0+):** сеть, таймаут, 5xx, 408, 429 и 401 вошедшего игрока не бросают исключение —
счёт встаёт в очередь на устройстве и уходит сам, ответ — `VhrScoreResult { accepted = false, queued = true,
board }` (`improved`/`best`/`rank` неизвестны). Устаревший `SubmitScoreAsync` в этом случае возвращает строку с
присланным счётом и `id = 0`. Подробно — [6.9](#69-ivhrconnection--работа-без-связи-sdk-1120).

`VhrLeaderboardRow` (старый формат): `id`, `userId`, `name`, `gameId`, `score`, `createdAt`, `isCurrentUser`.

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

`InitializeAsync`, `IsInitialized`, `Economy`, `Ads`, `Saves`, `Leaderboard`, `Servers`,
`Achievements`, `Profile`, `Friends`, `PlayerStats`, `GameSessions`, `Tournaments`,
`Connection` (1.12.0+, [6.9](#69-ivhrconnection--работа-без-связи-sdk-1120)),
`Relay`, `Lobby`, `Session`, `ConnectionState`, `SdkVersion`, `ResetForTests()`.

### 6.6 Транспортный seam

`IVhrHttp` (по умолчанию `UnityWebRequestHttp`, WebGL-safe) внедряется для
тестов; `VhrApiClient` обрабатывает заголовки, JSON через `JsonUtility` и
маппит не-2xx в `VhrSdkException` (коды: `unauthorized`, `forbidden`,
`not_found`, `conflict`, `sdk_update_required` (426), `rate_limited` (429),
`not_implemented`, `connection_error`, `http_error`, `deserialize_error`); код из
тела ответа моста (`{ "code": "..." }`) — в `ServerCode`, тело — в `ResponseBody`, заголовок
`Retry-After` (секунды или HTTP-дата) — в `RetryAfterSeconds` (SDK 1.11.0+; транспорт передаёт его
в `VhrHttpResponse.RetryAfter` для ответов с ошибкой). SDK 1.12.0+: каждый ответ (кроме отмены
вызывающим) обновляет `VhrSdk.Connection.IsOnline` — «нет связи» = статус 0, 502, 504 или 503 без кода
приложения.

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

### 6.8 `IVhrSaves` — облачные сохранения (SDK 1.11.0+)

Доступ: `VhrSdk.Saves` или внедрение `IVhrSaves` (VContainer) — один общий экземпляр
(`VhrSavesService.GetOrCreate`): одна очередь автосохранения и одни подписки на жизненный цикл.
Мост принимает **только игровой токен** (обычный или песочный); игрок = `sub`, игра = `gid` токена,
`X-Vhr-Game-Id` (если передан) обязан совпасть. Реальные игроки — только для активных игр, песочный —
и для черновиков. JSON camelCase.

| Член | HTTP (`{BridgeBaseUrl}`) | Ответ / результат |
|---|---|---|
| `LoadAsync(slot = "default")` | `GET /api/saves/{slot}` | 200 `{ slot, data, version, updatedAt }` → `VhrSaveLoadResult { Found, Data, Version, UpdatedAt }`; 404 `save_not_found` → `Found = false`, `Error = NotFound` |
| `LoadJsonAsync<T>(slot, fallback)` | то же | `JsonUtility.FromJson<T>(data)`; нет сохранения / ошибка / не разобралось → `fallback` |
| `SaveAsync(slot, data, expectedVersion)` | `PUT /api/saves/{slot}` `{ data, expectedVersion }` | 200 `{ slot, version, updatedAt, rawSize, storedSize }` → `VhrSaveWriteResult { Ok, Version, RawSize, StoredSize }` |
| `SaveJsonAsync<T>(slot, value, expectedVersion)` | то же | `JsonUtility.ToJson(value)` |
| `DeleteAsync(slot)` | `DELETE /api/saves/{slot}` | 204 → `true` (идемпотентно) |
| `ListAsync()` | `GET /api/saves` | `{ slots: [ { slot, version, rawSize, updatedAt } ], limits: { maxSlotKb, maxSlots, maxRawKb } }` → `VhrSaveSlotInfo[]`, `Limits`; ошибка → `VhrSdkException` |
| `QueueSave(slot, data)` / `QueueSaveJson<T>` | `PUT` в фоне | итог — `OnAutoSaveResult(slot, result)` |
| `FlushAsync()` | `PUT` накопленного сразу | — |
| `event Action<string, long> OnConflict` | — | автосохранение получило 409 |
| `HasPendingSaves`, `Limits` | — | есть неотправленное; лимиты слота (дефолт 256 / 10 / 1024) |

**Тело `PUT`** собирается вручную (не `JsonUtility`: тот не умеет `null` у `long?`):
`{"data":"<строка>","expectedVersion":null|0|N}` в UTF-8; экранируются только `"`, `\` и управляющие
символы — кириллица и прочий не-ASCII идут как есть (тело сервера ограничено 1.5 МБ при сыром лимите
1 МБ). `expectedVersion`: `null` — last-write-wins; `0` — только создать (есть → 409); `N` — только если
текущая версия `N`.

**Слот:** `^[a-z0-9_.-]{1,64}$` кроме `.` и `..`; SDK обрезает пробелы и приводит к нижнему регистру,
неверное имя отклоняет до запроса (`BadSlot`). Строку больше 1 МБ UTF-8 SDK тоже не отправляет
(`SlotTooLarge`). Без токена запрос не уходит (`NotAuthenticated`).

**Ошибки → `VhrSaveError`:**

| Ответ моста | `Error` | Дополнительно |
|---|---|---|
| 404 `save_not_found` | `NotFound` | на `DELETE` считается успехом |
| 409 `version_conflict` | `Conflict` | `ServerVersion` (0 — слота нет), `ServerUpdatedAt` |
| 403 `storage_quota_exceeded` | `QuotaExceeded` | квота игры; уменьшающая запись проходит |
| 413 `slot_too_large` | `SlotTooLarge` | `maxSlotKb` / `maxRawKb` из тела → `Limits` |
| 403 `slots_limit` | `SlotsLimit` | `maxSlots` из тела → `Limits` |
| 429 `rate_limited` | `RateLimited` | `RetryAfterSeconds`: заголовок `Retry-After` → поле `retryAfter` тела → 60 с |
| 401, прочие 403 (`game_mismatch`, `game_token_forbidden`, …) | `NotAuthenticated` | |
| 503 `saves_disabled`, 404 `game_not_found` | `Disabled` | |
| 400 `bad_slot` / `bad_request` | `BadSlot` | |
| нет связи / таймаут | `Network` | |
| 5xx, 426, 404 без кода, нечитаемый ответ | `Unknown` | `ErrorCode` / `Message` |

**Автосохранение (`QueueSave`):**
- очередь хранит последнее значение слота; первое уходит сразу, дальше — не чаще раза в
  `SavesMinIntervalSeconds` на слот (дефолт 10 с, минимум 5); все автосохранения вместе — не больше
  10 записей за скользящую минуту (запас под явные `SaveAsync`/`DeleteAsync` до серверных 12);
- операции одного слота (`Load`/`Save`/`Delete`/автосохранение) выполняются строго по очереди;
- `expectedVersion` автосохранения: известная версия слота (после `LoadAsync`/успешной записи;
  0 после `NotFound`/`DeleteAsync`); если загрузка слота не удалась — 0 (не перезаписывать облако
  вслепую); слот в сессии не загружали — `null`;
- 409 → слот в конфликте: данные придерживаются, `OnConflict(slot, serverVersion)`, запись слота на
  паузе. Снимается `LoadAsync(slot)` (придержанное отбрасывается) или успешным `SaveAsync(slot, …)`;
- 429 (на любой записи/удалении — лимит общий) → пауза `Retry-After` для всех автосохранений, данные в очереди;
- нет связи / 5xx → повтор с паузой 5 → 10 → 20 → 40 → 60 с (SDK 1.12.0+: событие `online`,
  `visibilitychange → visible` или возврат фокуса сбрасывают паузу — повтор сразу); `saves_disabled` → 60 с;
  нет токена → 30 с (без запроса); `426 sdk_update_required`, `QuotaExceeded`, `SlotTooLarge`, `SlotsLimit`,
  `BadSlot` — данные отбрасываются из памяти (повтор не поможет), следующий `QueueSave` пробует снова;
- явный `SaveAsync`/`DeleteAsync` отменяет автосохранения слота, накопленные **до** вызова;
- **flush сразу**: `Application.focusChanged(false)` (на телефоне приходит и перед паузой приложения),
  `Application.quitting`, а на WebGL — `visibilitychange → hidden` и `pagehide`
  (`VhrLifecycleBridge.jslib`; колбэк синхронно внутри DOM-события, чтобы запрос успел начаться).
  Закрытие вкладки — «лучшее усилие»;
- WebGL: ожидания — `VhrAsync` (кадры + `Time.realtimeSinceStartup`, пауза игры не мешает), TCS с
  `RunContinuationsAsynchronously`, без `ConfigureAwait(false)`, `Task.Delay` и `CancelAfter`.

**Сначала локально (SDK 1.12.0+)** — файлы `Application.persistentDataPath/vhr-sdk/saves/{gameId}/{userId}/`
(`userId` — `sub` токена; гость и Editor Simulation файлов не пишут; на WebGL — IndexedDB, см. 6.9):
- `QueueSave` кладёт данные слота в `slot_{slot}.pending.json` = `{ v, slot, data, baseVersion, seq, savedAt }`
  (`baseVersion`: `null` | `0` | `N` — версия, поверх которой писать): первая запись сразу, дальше не чаще
  раза в секунду на слот и синхронно при `focusChanged(false)`, `visibilitychange → hidden`, `pagehide`,
  `quitting`, `FlushAsync`; `SaveAsync` — сразу, до запроса. Запись атомарная (`*.tmp` + замена);
- подтверждение сервера → pending-файл удаляется (если для слота не накопилось новее; иначе он
  переписывается поверх новой версии — чтобы после перезапуска не было ложного конфликта), а
  `slot_{slot}.cache.json` = `{ v, slot, data, version, updatedAt }` обновляется; успешная загрузка тоже
  обновляет кэш, `NotFound` и `DeleteAsync` его удаляют;
- старт: как только известен игрок (токен может прийти позже — ждём до 120 с, плюс проверка при каждом
  вызове `Saves`), pending-файлы поднимаются в очередь автосохранения и уходят с
  `expectedVersion = baseVersion`;
- `LoadAsync`: есть неотправленное (слот не в конфликте) → оно, `FromPending = true`, без запроса; сеть /
  5xx / 429 / `Disabled` → кэш, `FromCache = true` (`Found = true`, `Error = None`, версия кэша становится
  известной версией слота — запись поверх чужой версии даст конфликт); иначе — как раньше;
- `SaveAsync`: временная ошибка (сеть, 5xx, 429, `Disabled`, 401 вошедшего игрока) — результат ошибочный,
  но данные встают в очередь автосохранения с этим же `expectedVersion` (и снимают конфликт слота —
  это решение игры); окончательная ошибка или отмена `ct` — pending-файл удаляется;
- **409 после обрыва связи** (автосохранение и `SaveAsync`): при `serverVersion > 0` SDK перечитывает слот;
  на сервере ровно наши данные → успех (прошлый запрос дошёл, а ответ потерялся), иначе — конфликт как раньше.

**Лимиты и квота** (настройки платформы, дефолты): слот ≤ 256 КБ сжатого (`gzip` UTF-8) и ≤ 1 МБ сырого;
≤ 10 слотов на игрока в игре; 12 записей/удалений и 60 чтений в минуту (игрок + игра); квота игры —
50 МБ бесплатно по сжатому размеру, сверх — пакеты (1024 МБ за 199 монет на 30 дней), которые
**оплачивает разработчик** в кабинете (игра → «Облачные сохранения», автопродление). Песочные сохранения
(Editor Live) квоту игры не тратят: лимит 1024 КБ на тестового игрока в игре, удаляются, если не
обновлялись 30 дней.

**Editor Simulation:** `VhrEditorSaves` — файлы `Library/VhrSdk/SimSaves/slot_<slot>.json`, те же версии,
`expectedVersion` и конфликты, gzip-размер, 256 КБ / 1 МБ, 10 слотов, 1024 КБ суммарно
(`storage_quota_exceeded`), 12 записей и 60 чтений в минуту (`rate_limited` с паузой).

### 6.9 `IVhrConnection` — работа без связи (SDK 1.12.0+)

Доступ: `VhrSdk.Connection` или внедрение `IVhrConnection` (VContainer) — один общий экземпляр
(`VhrOutbox`); им же пользуются сервисы SDK.

| Член | Описание |
|---|---|
| `bool IsOnline` / `event Action<bool> OnlineChanged` | Связь с сервисами VHR: последний запрос SDK получил ответ (любой, кроме 502/504 и 503 без кода) и браузер не сообщил `offline`. Начальное значение — `navigator.onLine` (вне WebGL — `true`) |
| `int PendingCount` / `event Action<int> PendingChanged` | Операции текущего игрока в очереди (сохранения — отдельно: `IVhrSaves.HasPendingSaves`) |
| `Task FlushAsync()` | Отправить очередь сейчас (паузы после сбоев сбрасываются, кроме `Retry-After`) и ждать не дольше 30 с; без исключений |
| `event Action<VhrDroppedOperation> OnDropped` | Выброшено без доставки: `Kind`, `Target`, `Value`, `Reason` (`expired` / `limit` / код сервера / `http_{статус}`), `HttpStatus`, `Message`, `QueuedAtUtc` |

**Операции, которые встают в очередь** (все — `POST` на `GamesBaseUrl`; на сервере идемпотентны):

| Вызов | HTTP | Тело | Без связи | Схлопывание в очереди |
|---|---|---|---|---|
| `Leaderboard.SubmitAsync(…)`, `SubmitScoreAsync` `[Obsolete]` | `/api/Leaderboard/score` | как в [6.2](#62-ivhrleaderboard--таблицы-лидеров-несколько-на-игру-с-sdk-1120) | `VhrScoreResult { accepted = false, queued = true }` (старый — строка со счётом, `id = 0`) | по таблице — максимум и минимум ожидающих (направление таблицы клиенту может быть неизвестно; сервер возьмёт лучший); тела пересобираются с подсказками последней отправки |
| `Achievements.UnlockAsync(slug)` | `/api/Achievements/unlock` | `{ code: "game.{gameId}.{slug}" }` | `VhrUnlockResult { queued = true, code }` | одна на код |
| `Tournaments.SubmitAsync(id, score)`, `SubmitScoreAsync` | `/api/Tournaments/{id}/score` | `{ score }` | `VhrTournamentScoreResult { queued = true }` (старый — присланный счёт) | максимум |
| `GameSessions.EndAsync(sessionId, …)` | `/api/GameSessions/{id}/end` | `{ outcome, score, durationSeconds }` | без исключения | последнее |

Не встают в очередь (только онлайн): покупки и экономика (нужно подтверждение игрока), `HeartbeatAsync`,
друзья, лобби, чтение таблиц, профилей и ачивок.

**`UnlockAsync`.** Код строится как на сервере (`GameAchievementRules.BuildCode`): полный `game.…` — как
есть; иначе `game.{gameId}.{slug}`, где `gameId` — GUID (`D`, нижний регистр) из `gid` токена или
`VhrSdkOptions.GameId`, slug — в нижнем регистре, пробелы → `_`; пустой — `bad_request` до запроса. Ответ
`{ unlocked, achievement }` → `VhrUnlockResult { unlocked, code, achievement }` (`unlocked = false` — уже была
открыта). Подтверждённые сервером коды запоминаются на время запуска: повторный вызов — без запроса
(`unlocked = false`). Ошибки сервера: `400 platform_achievement`, `403 game_mismatch`, `404` (нет ачивки).

**`Tournaments.SubmitAsync`** проверяет до запроса id (GUID) и счёт (0…10^12, как сервер) —
`VhrSdkException("bad_request")`. Ответ `{ bestScore }` → `VhrTournamentScoreResult { accepted, bestScore,
tournamentId }`. Сервер принимает результат только у активного турнира: отложенный, доставленный после
окончания, будет отклонён (`400` → `OnDropped`).

**Сначала онлайн.** Вызов отправляет запрос сразу — игре нужен итог (рекорд, место, «открыто»). Браузер
точно офлайн (`navigator.onLine === false`) — сразу в очередь, без ожидания; иначе в худшем случае вызов
ждёт `RequestTimeoutSeconds` (+5 с страховки транспорта). Классификация ответа:

| Ответ | Онлайн-вызов | Операция из очереди |
|---|---|---|
| 2xx | результат | удалена (доставлена) |
| нет ответа / таймаут (статус 0), 5xx (кроме 501), 408 | в очередь | повтор: пауза 2 → 4 → 8 → 16 → 32 → 60 с + до 20% джиттера |
| 429 | в очередь | повтор после `Retry-After` (нет — 60 с, не больше 5 мин) |
| 401 (после попытки `VhrApiClient` обновить токен) | в очередь | повтор, когда хост пришлёт новый токен (или через 30 с) |
| 400 `no_session` (ачивка: сессию игры ещё не записали) | в очередь | повтор через 5 мин / при следующем запуске |
| прочие 4xx, 501 | `VhrSdkException`, как раньше | выброс + `OnDropped` + предупреждение |
| отмена `ct` | `OperationCanceledException`, в очередь не ставится | — |

Гость (нет токена или токен не JWT — нет `sub`): в очередь ставится только `EndAsync` (сессии бывают
анонимными), остальные вызовы бросают исключение, как раньше.

**Когда отправляется очередь:** при старте SDK (как только есть токен игрока), сразу после постановки,
после любого успешного запроса SDK (связь вернулась), по событиям `online` и `visibilitychange → visible`
(WebGL, `VhrLifecycleBridge.jslib`), при возврате фокуса и по таймеру. Строго по одной операции, самые
ранние первыми; сбой связи / 429 / 401 прерывает проход до конца паузы; браузер офлайн — не шлёт вовсе.
Ожидания — по кадрам и `Time.realtimeSinceStartup` (пауза игры и `timeScale = 0` не мешают), без
тредпула, `Task.Delay` и `CancelAfter`.

**Файл очереди:** `Application.persistentDataPath/vhr-sdk/outbox-{gameId}.json`:

```json
{"v":1,"items":[{"id":"…","kind":"lb.score","userId":"<sub>","createdAt":1730000000000,"attempts":0,
  "nextAttemptAt":0,"key":"lb.score:survival","target":"survival","value":42,"payload":"{\"gameId\":…}"}]}
```

`kind`: `lb.score` | `ach.unlock` | `tour.score` | `session.end`; время — Unix, мс UTC; `payload` — JSON-тело
запроса строкой. Файл переписывается атомарно после каждого изменения (`*.tmp` + `File.Replace`; если
запись оборвалась между шагами, читается `*.tmp`), пустая очередь — файл удаляется. Разбор — собственный
(не `JsonUtility`): битый файл → пустая очередь, копия `outbox-{gameId}.json.bad` и предупреждение; негодные
элементы пропускаются по одному.
- отправляются только операции текущего игрока (`sub` токена = `userId`); чужие ждут своего игрока;
- TTL — 14 дней от первой постановки (`Reason = "expired"`); лимит — 300 операций (`"limit"`: выбрасываются
  сначала завершения сессий, потом счёт, ачивки — последними; внутри вида — самые старые).

**WebGL и IndexedDB.** `persistentDataPath` на WebGL — IDBFS (файлы в памяти вкладки). После каждой записи SDK
просит `FS.syncfs(false)` (`VhrStorageBridge.jslib`: одна синхронизация на серию записей через ~1 с), а при
`visibilitychange → hidden` и `pagehide` — немедленную (в микрозадаче — после всех обработчиков события,
чтобы попали и сделанные в них записи). Окно риска — ~1 с между записью и синхронизацией, если вкладку
убьют без событий страницы. Очистка данных сайта и приватное окно стирают и очередь.

**Editor.** Очередь — в `persistentDataPath` редактора и переживает перезапуск Play; в Live-песочнице —
операции тестового игрока `sbx_*` (его ключом). Simulation-сохранения локальных pending/cache-файлов не пишут
(«облако» и так на диске).

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
| `not_implemented` не брошен, но нет данных | Seam серверов 501 / лидерборд вернул пустой массив | Ожидаемо, пока бэкенд не поставлен; гейтите UI серверов по `status=="noop"` |
| Реклама всегда `Unavailable` на WebGL | Игра открыта не со страницы vhrgames.ru, реклама выключена платформой, детский аккаунт, адблок или нет объявления | Проверяйте на vhrgames.ru; это нормальный исход — продолжайте игру / прячьте кнопку |
| Реклама часто `Cooldown` | Лимиты частоты платформы (interstitial ~ раз в 90 с, rewarded ~ 20/ч) или повторный вызов во время показа | Не вызывайте чаще; предложите rewarded позже |
| Состояние подключения застряло в `Faulted` | Мост недоступен на init | Не фатально; вызовы ретраятся по требованию. Проверьте сеть/URL |
| Лидерборд → `bad_board` | Id таблицы не по правилам (`a-z0-9_-`, до 32 символов, первый — буква/цифра) | Исправьте id: `"survival"`, `"level_1"`; SDK отклоняет его до запроса |
| Лидерборд → `board_limit` (409) | У игры уже 50 таблиц, новая не создаётся | Удалите лишние таблицы в кабинете (игра → «Таблицы лидеров») или переиспользуйте id |
| Лидерборд: время показывается числом / «больше — лучше» вместо «меньше» | Таблица создана первой отправкой без `VhrBoardOptions` (подсказки применяются только при создании) | Кабинет → таблица → формат; направление — после сброса таблицы |
| Лидерборд: ники «Игрок» | Профиль игрока скрыт, ник пуст или резолв ников (`users/resolve`) не удался | Ожидаемо; при ошибке резолва — предупреждение `[VHR Leaderboard]` в консоли |
| Сохранения → `NotAuthenticated` | Игрок не вошёл на vhrgames.ru (гость), игра открыта не со страницы VHR, в Editor нет песочного ключа | Ожидаемо для гостя — сохраняйте локально (`PlayerPrefs`); в Editor — Simulation или ключ в окне VHR |
| Сохранения → `Disabled` (`game_not_found`) | Реальный игрок в неопубликованной игре | Проверяйте черновик в Editor Live; после публикации заработает |
| Сохранения → `RateLimited` | `SaveAsync` слишком часто (> 12 записей в минуту на игрока) | Используйте `QueueSave` (+ `FlushAsync` в ключевых точках) |
| Сохранения → `QuotaExceeded` | Квота облачных сохранений игры исчерпана | Кабинет разработчика → игра → «Облачные сохранения» → пакет; уменьшите размер сохранений |
| Автосохранение «замолчало», в консоли «конфликт версий» | Слот перезаписан с другого устройства/вкладки, `OnConflict` не обработан | Подпишитесь на `OnConflict`: `LoadAsync` (взять облачное) или `SaveAsync(slot, data, serverVersion)` |
| Рекорд / ачивка / турнир вернули `queued = true` | Сервер был недоступен (сеть, таймаут, 5xx, 429, истёкший токен) | Ожидаемо: операция в очереди на устройстве и уйдёт сама (`VhrSdk.Connection.PendingCount`). Покажите «отправится, когда появится связь» |
| Отправка рекорда «висит» ~15–20 с без сети | Браузер считает, что сеть есть, — SDK ждёт таймаут запроса | Ожидаемо; уменьшите `RequestTimeoutSeconds`. При `navigator.onLine = false` счёт встаёт в очередь сразу |
| `PendingCount` не уменьшается | Нет связи (`IsOnline = false`), токен не обновляется (401), или ждёт `no_session` | Проверьте сеть / страницу VHR; ачивка без записанной сессии игры повторяется раз в 5 минут и при следующем запуске |
| В консоли «выброшено из очереди без доставки», `OnDropped` | Сервер окончательно отклонил операцию (турнир закончился, `board_limit`, ачивки нет) или 14 дней без связи | Смотрите `Reason` / `Message`; для турнира после окончания — ожидаемо |
| «файл очереди отложенных операций повреждён» | Обрезанный или испорченный файл (очистка IndexedDB посреди записи и т.п.) | Очередь начата заново, копия — `outbox-{gameId}.json.bad` |
| `LoadAsync` вернул `FromCache = true` / `FromPending = true` | Сервер недоступен — локальная копия облачной версии / есть неотправленное сохранение этого устройства | Можно играть; неотправленное уйдёт само, запись поверх чужой версии даст `OnConflict` |
