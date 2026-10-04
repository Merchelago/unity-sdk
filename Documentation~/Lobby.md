# Лобби и матчмейкинг (`VhrSdk.Lobby`)

Поверх платформенного релея (см. `Multiplayer.md`) SDK даёт **готовое лобби и
матчмейкинг** — без своей серверной сборки:

- **Быстрый матч** — собрать игроков и, если не хватило живых, **добить пустые
  слоты ботами**.
- **Приватные лобби** — создать комнату с кодом и пригласить друзей.
- **Приглашение друзей платформы** — список друзей из VHR.
- **Готовность (ready-up)** и **старт** (хост — авторитет).

Всё это **работает и в WebGL, и в нативе**: лобби ездит по **тому же
WebSocket-соединению**, что и `VhrSdk.Relay` (управляющий фрейм `0x20`), поэтому
никаких новых нативных зависимостей. На старте матча SDK **сам заводит** всех
участников в общую relay-комнату — дальше игра обменивается данными через
`VhrSdk.Relay` (`Send`/`OnData`), как обычно.

> Боты — на стороне игры. SDK сообщает, **какие слоты** отвести ботам
> (`VhrMatchInfo.botSlots`/`botCount`); **спавнит их сама игра** (хост —
> авторитет). Их трафик идёт через `VhrSdk.Relay`, как у живых игроков.

---

## Быстрый старт

```csharp
await VhrSdk.InitializeAsync(options); // как обычно, с options.GameId
```

`VhrSdk.Lobby` (тип `IVhrLobby`) лениво поднимается поверх `VhrSdk.Relay`.

---

## Пример A — быстрый матч с добивкой ботами

```csharp
using VhrGames.Sdk;
using UnityEngine;

public sealed class QuickMatchDemo : MonoBehaviour
{
    async void Start()
    {
        // (необязательно) следить за прогрессом подбора
        VhrSdk.Lobby.OnLobbyUpdated  += l  => Debug.Log($"В лобби: {l.members?.Length}");
        VhrSdk.Lobby.OnMatchStarting += s  => Debug.Log($"Старт через {s} c");

        // Собрать матч на 4: добить пустые слоты ботами, если живых не хватит.
        var m = await VhrSdk.Lobby.QuickMatchAsync(
            new VhrMatchmakingOptions { maxPlayers = 4, fillBots = true });

        // На этом месте SDK уже вошёл в relay-комнату m.roomId.
        SpawnPlayers(m.players);    // живые игроки с их слотами (m.players[i].slot)
        SpawnBots(m.botSlots);      // слоты под ботов — спавнит ХОСТ (m.isHost)

        // Игра идёт через общий релей:
        // VhrSdk.Relay.Send(bytes);
        // VhrSdk.Relay.OnData += (peerId, bytes) => { ... };
    }

    void SpawnPlayers(VhrMatchPlayer[] players) { /* ... */ }
    void SpawnBots(int[] botSlots) { /* только на хосте */ }
}
```

---

## Пример B — приватное лобби + приглашение друзей

```csharp
using VhrGames.Sdk;
using UnityEngine;

public sealed class PrivateLobbyDemo : MonoBehaviour
{
    async void Start()
    {
        // Принять приглашение от друга: войти в его лобби по коду.
        VhrSdk.Lobby.OnInviteReceived += async invite =>
        {
            Debug.Log($"{invite.fromName} зовёт в лобби {invite.code}");
            await VhrSdk.Lobby.JoinLobbyAsync(invite.code);
        };

        // Когда матч стартует (у любого участника) — релей уже в комнате.
        VhrSdk.Lobby.OnMatchStarted += m =>
        {
            SpawnPlayers(m.players);
            SpawnBots(m.botSlots);
        };

        // 1) Хост создаёт приватное лобби (вернётся код для приглашений).
        var lobby = await VhrSdk.Lobby.CreateLobbyAsync(
            new VhrLobbyOptions { maxPlayers = 4, fillBots = true, isPrivate = true });
        Debug.Log($"Код лобби: {lobby.code}");

        // 2) Показать друзей и позвать кого-нибудь.
        VhrFriend[] friends = await VhrSdk.Lobby.GetFriendsAsync();
        foreach (var f in friends)
            await VhrSdk.Lobby.InviteFriendAsync(f.userId);

        // 3) Готовность и старт (хост). Пустые слоты добьются ботами (fillBots).
        await VhrSdk.Lobby.SetReadyAsync(true);
        var m = await VhrSdk.Lobby.StartAsync(); // ждёт старт + авто-вход в комнату
        SpawnPlayers(m.players);
        SpawnBots(m.botSlots);
    }

    void SpawnPlayers(VhrMatchPlayer[] players) { /* ... */ }
    void SpawnBots(int[] botSlots) { /* только на хосте */ }
}
```

---

## API `IVhrLobby`

### Методы

| Метод | Что делает |
|---|---|
| `Task<VhrMatchInfo> QuickMatchAsync(opts, ct)` | Быстрый матч: подбор → ждёт старт → возвращает матч и **входит в комнату**. |
| `Task<VhrLobby> CreateLobbyAsync(opts)` | Создаёт лобби (по умолчанию приватное), возвращает с `code`. |
| `Task<VhrLobby> JoinLobbyAsync(code)` | Вход в лобби по коду. |
| `Task LeaveLobbyAsync()` | Выйти из лобби. |
| `Task CancelAsync()` | Отменить ожидание быстрого матча / лобби. |
| `Task SetReadyAsync(ready)` | Проставить/снять готовность. |
| `Task InviteFriendAsync(userId)` | Пригласить друга в текущее лобби. |
| `Task KickAsync(userId)` | Кикнуть участника (только хост). |
| `Task<VhrMatchInfo> StartAsync()` | Хост стартует матч; ждёт старт + **вход в комнату**. |
| `Task<VhrFriend[]> GetFriendsAsync(ct)` | Список друзей (REST `GET {GamesBaseUrl}/api/Friends`). |

### События

| Событие | Когда |
|---|---|
| `Action<VhrLobby> OnLobbyUpdated` | Лобби изменилось (вход/выход/готовность/смена хоста). |
| `Action<VhrLobbyInvite> OnInviteReceived` | Пришло приглашение в лобби. |
| `Action<int> OnMatchStarting` | Матч вот-вот стартует (обратный отсчёт, сек). |
| `Action<VhrMatchInfo> OnMatchStarted` | Матч стартовал (релей уже в комнате). |
| `Action<string> OnClosed` | Лобби/матч закрылся (причина). |

### Свойства

| Свойство | Что это |
|---|---|
| `VhrLobby CurrentLobby` | Текущее лобби или `null`. |
| `bool IsHost` | Текущий игрок — хост текущего лобби. |
| `string SelfUserId` | UserId текущего игрока (после первых событий релея). |

### `VhrMatchmakingOptions` / `VhrLobbyOptions`

| Поле | Смысл | Дефолт |
|---|---|---|
| `mode` | Игровой режим (метка игры). | `null` |
| `maxPlayers` | Максимум игроков (вкл. ботов). | `2` |
| `minPlayers` | Минимум живых для старта. | `1` |
| `fillBots` | Добивать пустые слоты ботами. | `true` |
| `waitSec` (только matchmaking) | Сколько ждать живых до добивки. | `10` |
| `isPrivate` (только lobby) | Приватное лобби (вход по коду/приглашению). | `true` |

### `VhrMatchInfo`

| Поле | Что это |
|---|---|
| `roomId` | Id общей relay-комнаты (`"match-xxxx"`) — SDK уже вошёл. |
| `hostId` | UserId хоста (авторитет, в т.ч. спавн ботов). |
| `selfUserId` / `selfSlot` | Кто я и мой слот. |
| `isHost` | Я хост (мне спавнить ботов). |
| `botCount` / `botSlots` | Сколько ботов и в каких слотах (спавнит игра). |
| `players` | Живые игроки (`userId`, `name`, `slot`). |

---

## Как это устроено (для любопытных)

- Контроль лобби идёт по тому же сокету, что и релей, **управляющим фреймом
  `0x20`** (`[0x20][utf8 JSON]`). Клиент шлёт `{"op":...}`, релей отвечает
  `{"ev":...}`. Игровой трафик релея (`0x83/0x84/0x85/0x81`) и WebRTC-сигналинг
  (`0x10`) не затрагиваются.
- При первом обращении SDK один раз шлёт `{"op":"auth","token":"<JWT>"}` (JWT
  игрока из `VhrSdkOptions.TokenProvider`).
- На `start` SDK программно входит в комнату матча
  (`VhrRelay.JoinRoomRawAsync(roomId)`) — отдельный seam `VhrRelay`, не ломающий
  обычный вход `ConnectAsync`.
- Релей **не авторитарный**: он пересылает байты и ведёт лобби. Для античита
  используйте выделенный сервер (см. `Multiplayer.md`).

## Готовый экран лобби (drop-in)

Не хотите верстать UI сразу — повесьте компонент **`VhrLobbyPanel`** (меню *Add Component → VHR → VHR Lobby Panel*) на любой объект в сцене. На OnGUI он рисует рабочий экран: быстрый матч (с добивкой ботами), приватное лобби с кодом, список друзей и приглашения, готовность и старт — без настройки Canvas. Это же и эталонная реализация на `VhrSdk.Lobby`: посмотрите код компонента и сделайте свой UI на тех же вызовах. После старта матч идёт через `VhrSdk.Relay`, а ботов спавните по `match.botSlots`/`match.botCount` (хост — авторитет).
