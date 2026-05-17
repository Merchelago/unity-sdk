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
- **Неймспейс:** `VhrGames.Sdk` (runtime), `VhrGames.Sdk.Editor` (build-хук)

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

Для **нативных / редакторных** сборок параметра `?access_token` в URL нет —
там задавайте `TokenProvider` явно, привязав его к своей системе авторизации
(желательно обновляемый — см. ниже про срок жизни токена).

**Срок жизни токена (~15 мин).** JWT игрока живёт около 15 минут, а игровые
сессии длиннее. SDK сам это обрабатывает на сайте VHR: родительская страница
(`https://vhrgames.ru`) присылает свежий токен во встроенную игру через
`postMessage`, а SDK при получении `401` от моста автоматически запрашивает
обновление, ждёт новый токен и повторяет запрос — **прозрачно для кода игры,
делать ничего не нужно**. Детали — в
[Documentation~/index.md](Documentation~/index.md) (раздел про жизненный цикл
токена). Для нативных сборок просто отдавайте из `TokenProvider` актуальный
(обновляемый вашей системой авторизации) токен.

---

## Модель экономики: клиент только списывает

Игра, исполняемая на клиенте (WebGL), **не может начислять/минтить монеты** —
это запрещено мостом на сервере (anti-mint). Так монеты нельзя «нарисовать»
из клиентской сборки, которая раздаётся игрокам.

| Операция | Из клиентской WebGL-сборки | Только серверная интеграция (`X-Internal-Api-Key`) |
|---|---|---|
| `GetBalanceAsync` (чтение баланса) | ✅ можно | ✅ |
| `SpendAsync` (списание, инициирует игрок) | ✅ можно | ✅ |
| `PurchaseAsync` (покупка, инициирует игрок) | ✅ можно | ✅ |
| `GrantAchievementAsync` (факт анлока) | ✅ можно записать анлок, **но монетная награда форсится в 0** | ✅ монетная награда применяется |
| `GrantCoinsAsync` (начисление монет) | ⛔ **403 forbidden** | ✅ |

Что это значит для разработчика клиентской игры:

- начислять монеты за уровни/события **из клиента нельзя** — `GrantCoinsAsync`
  вернёт `VhrSdkException` с кодом `forbidden` (HTTP 403);
- `GrantAchievementAsync` из клиента **можно** вызывать, чтобы зафиксировать
  разблокировку ачивки — но привязанная к ней монетная награда на клиенте
  **игнорируется** (применяется только при серверной интеграции);
- игрок может **тратить** заработанное: `SpendAsync` / `PurchaseAsync`
  (игровые покупки) работают из клиента — это списание, не минт;
- если игре нужны серверные начисления — это **серверная игра / выделенный
  сервер** с `InternalApiKey` (server-to-server), а не клиентская WebGL-сборка.

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
https://github.com/Merchelago/unity-sdk.git#v1.0.0
```

или добавьте в `Packages/manifest.json`:

```json
"ru.vhrgames.sdk": "https://github.com/Merchelago/unity-sdk.git#v1.0.0"
```

---

## Быстрый старт

### Вариант A — без DI (статический фасад)

```csharp
using VhrGames.Sdk;
using R3;

// Клиентская WebGL-сборка: только GameId. JWT игрока SDK возьмёт сам из
// ?access_token в URL страницы (дефолтный TokenProvider на WebGL).
// Никакого InternalApiKey в клиентскую сборку!
var options = new VhrSdkOptions
{
    GameId = "your-game-id"   // ID из кабинета разработчика, страница /dev/games
};

await VhrSdk.InitializeAsync(options);

VhrSdk.ConnectionState.Subscribe(s => Debug.Log($"SDK: {s}"));
VhrSdk.Economy.BalanceChanged.Subscribe(e => hud.SetCoins(e.NewBalance));

var bal = await VhrSdk.Economy.GetBalanceAsync(userId);

// Клиент только СПИСЫВАЕТ (anti-mint): начисление монет server-only.
// Игрок что-то покупает за свои монеты:
await VhrSdk.Economy.PurchaseAsync(userId, "skin_blue", quantity: 1);
// ...или прямое списание по игровому событию:
await VhrSdk.Economy.SpendAsync(userId, 50, "revive");

// Зафиксировать разблокировку ачивки можно и из клиента
// (но монетная награда применится только на серверной интеграции):
await VhrSdk.Economy.GrantAchievementAsync(userId, "first_blood");

// ⛔ Начисление монет из клиента НЕДОСТУПНО — вернёт 403 forbidden.
// Это делает только серверная интеграция (InternalApiKey, server-to-server):
// await VhrSdk.Economy.GrantCoinsAsync(userId, 100, "level_complete");
```

> **Нативная / редакторная сборка** (нет `?access_token` в URL): задайте
> провайдер токена явно —
> `TokenProvider = () => MyHostAuth.CurrentPlayerJwt`.
>
> **Серверная игра** (server-to-server): тогда и только тогда добавьте
> `InternalApiKey = "..."`. В клиентских билдах это запрещено.

### Вариант B — VContainer

Добавьте `VhrSdkLifetimeScope` на bootstrap-GameObject, задайте **Game Id** в
инспекторе (поле Internal API Key в клиентской WebGL-сборке оставьте **пустым**
— оно только для серверных игр), затем внедряйте через конструктор где угодно:

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

## Публичный API кратко

| Сервис | Ключевые методы |
|---|---|
| `IVhrEconomy` | `GetBalanceAsync`, `SpendAsync`, `PurchaseAsync` (доступны из клиента) · `GrantCoinsAsync`, монетная награда `GrantAchievementAsync` (**server-only**, из клиента 403) · `Observable<BalanceChanged> BalanceChanged` |
| `IVhrLeaderboard` | `SubmitAsync`, `GetTopAsync` (seam следующей волны, терпим к 501) |
| `IVhrServers` | `BindAsync`, `ListBindingsAsync`, `RequestInstanceAsync` (по умолчанию noop) |
| `IVhrSession` | `GameId`, `CurrentToken`, `State`, `Observable<VhrConnectionState> StateChanged` |
| `VhrSdk` (static) | `InitializeAsync`, `Economy`, `Leaderboard`, `Servers`, `Session`, `ConnectionState` |

Все мутации экономики **идемпотентны** через сгенерированный клиентом
`externalId` (GUID); передайте свой стабильный id, чтобы конкретное игровое
событие было безопасно к повторам.

Полные форматы запросов/ответов: [Documentation~/index.md](Documentation~/index.md).

---

## Лицензия

См. [LICENSE.md](LICENSE.md). © VHR Games.
