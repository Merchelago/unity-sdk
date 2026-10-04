# Мультиплеер

В VHR Games два пути сделать многопользовательскую игру:

1. **Простой мультиплеер через релей (рекомендуется, без серверной сборки)** —
   вы собираете **только клиент**, а сервером выступает общий relay платформы.
   Подходит для большинства казуальных и реалтайм-игр (синхронизация позиций,
   чат, ходы, лобби). См. раздел ниже.
2. **Выделенный сервер (Dedicated Server) — продвинутый** — авторитарный
   серверный билд для античита / тяжёлой симуляции. Нужна отдельная серверная
   сборка. См. вторую половину документа.

> Одиночным / клиент-онли играм этот документ не нужен.

> **Нужны лобби и матчмейкинг** (быстрый матч с добивкой ботами, приватные
> лобби, приглашение друзей, готовность, старт)? Они идут поверх релея и уже
> готовы — см. **`Lobby.md`** (`VhrSdk.Lobby`). Серверную сборку для них тоже
> поднимать не нужно.

---

# Простой мультиплеер (релей, без серверной сборки)

Самый быстрый путь: **никакой серверной сборки и никакого zip**. Вы собираете
обычный клиент (WebGL или нативный), а платформа предоставляет общий
**WebSocket-релей**. Клиенты заходят в одну комнату (лобби) и пересылают друг
другу байты — релей доставляет их остальным.

### Что нужно от разработчика

1. Установить SDK (как обычно — см. `README.md`) и инициализировать его:
   `await VhrSdk.InitializeAsync(options)` с заданным `options.GameId`.
2. При загрузке игры на платформе выбрать тип сервера **«Платформенный сервер
   (релей)»**. **Серверную сборку грузить не нужно, zip не нужен.**
3. В коде — подключиться к релею и обмениваться данными:

```csharp
using VhrGames.Sdk;
using System.Text;
using UnityEngine;

public sealed class RelayDemo : MonoBehaviour
{
    async void Start()
    {
        // 1) Подключиться к релею и войти в лобби (код общий у тех, кто играет вместе).
        await VhrSdk.Relay.ConnectAsync("lobby1");

        // 2) Подписаться на данные других игроков.
        VhrSdk.Relay.OnData += (senderId, bytes) =>
        {
            Debug.Log($"От {senderId}: {Encoding.UTF8.GetString(bytes)}");
        };

        // Кто уже в комнате на момент входа + свой id.
        VhrSdk.Relay.OnJoined += (selfId, peers) =>
            Debug.Log($"Я peer {selfId}, в комнате уже: {peers.Length}");
        VhrSdk.Relay.OnPeerJoined += id => Debug.Log($"Вошёл {id}");
        VhrSdk.Relay.OnPeerLeft   += id => Debug.Log($"Вышел {id}");

        // 3) Слать данные всем в комнате…
        VhrSdk.Relay.Send(Encoding.UTF8.GetBytes("привет всем"));
        // …или конкретному пиру:
        // VhrSdk.Relay.SendTo(targetPeerId, bytes);
    }
}
```

Готово — это полноценный мультиплеер без своего сервера.

### Drop-in компонент `VhrRelayBootstrap`

Если не хотите писать подключение руками — бросьте компонент
**`VhrRelayBootstrap`** (`VHR → VHR Relay Bootstrap` в Add Component) на любой
GameObject, задайте поле **`lobbyCode`**. На `Start` он сам подключится к
`VhrSdk.Relay`, войдёт в комнату и пробросит события релея как **UnityEvents**
(`onData`, `onJoined`, `onPeerJoined`, `onPeerLeft`, `onClosed`) — их можно
подцепить прямо в инспекторе — и как обычные C#-события. Слать данные:
`bootstrap.Send(bytes)` / `bootstrap.SendTo(peerId, bytes)`. Всё null-safe.

### Как это устроено

- Комната неймспейсится как **`"{GameId}:{lobbyCode}"`** (по умолчанию
  `lobbyCode = "main"`), `GameId` берётся из `VhrSdkOptions.GameId` — игроки
  разных игр и разных лобби не пересекаются.
- Транспорт — WebSocket, и он **работает в WebGL** (браузерный `WebSocket`
  через плагин) и нативно (`ClientWebSocket`) за единым интерфейсом. На WebGL
  по HTTPS используется `wss://` (дефолт `wss://servers.vhrweb.ru/ws`, поле
  `VhrSdkOptions.RelayBaseUrl`).
- **Низкая задержка в браузере (WebRTC, авто).** В WebGL-сборке релей после
  входа в комнату **сам апгрейдится** на **WebRTC DataChannel** (UDP-подобный,
  unreliable/unordered — заметно ниже задержка, чем у WebSocket поверх TCP).
  Это **прозрачно**: тот же `Send`/`SendTo`/`OnData`, та же комната — **код
  менять не нужно**. Сигналинг идёт по тому же соединению. Если в браузере нет
  WebRTC или соединение не установилось за ~5 с — релей **остаётся на
  WebSocket** (поведение как раньше). Отключить апгрейд:
  `options.PreferWebRtc = false`. Натив/редактор всегда на WebSocket. Текущий
  транспорт виден в `VhrSdk.Relay.Transport` (`"ws"` | `"webrtc"`) — для
  диагностики.
- Релей **не авторитарный**: он только пересылает байты. Для античита/серверной
  симуляции используйте выделенный сервер (ниже).

---

# Выделенный сервер (Dedicated Server) — продвинутый

Платформа VHR Games умеет хостить **выделенный игровой сервер** вашей игры. Вы
собираете билд Unity **Dedicated Server** (Linux x86_64) одной кнопкой и
загружаете полученный zip — платформа сама собирает из него Docker-образ,
запускает контейнеры и раздаёт клиентам адреса через матчмейкинг. Вам не нужно
поднимать инфраструктуру и **не нужно ничего настраивать вручную**: порт, ключ и
адрес платформа задаёт серверу сама через переменные окружения.

> Это для **многопользовательских** игр с авторитарным сервером. Если вам хватает
> релея (см. выше) — этот раздел можно пропустить.

---

## TL;DR — три шага

1. **Сервер:** перетащите компонент **`VhrServerHost`** на любой GameObject в
   серверной сцене (и обновляйте его `CurrentPlayers`).
2. **Сборка:** меню **`VHR → Собрать серверный билд (Linux x86_64)`** → получите
   `Builds/vhr-server.zip` → загрузите его на платформе, включив тумблер
   **«Мультиплеер»**.
3. **Клиент:** `await VhrSdk.Servers.MatchAsync()` → подключитесь к
   `match.connectUri`.

---

## 1. Сервер: drop-in компонент `VhrServerHost`

Перетащите **`VhrServerHost`** (`VHR → VHR Server Host` в меню Add Component) на
любой GameObject в **серверной сцене**. Больше ничего настраивать не нужно:

- Компонент активен **только в серверной сборке** (когда платформа задала env
  `VHR_INSTANCE_ID`). В клиентских / редакторных сборках он — безопасный no-op,
  поэтому его можно держать прямо в общей сцене.
- Порт, серверный ключ и адрес API платформа **инъектит в контейнер через env**
  (`VHR_SERVER_PORT`, `VHR_INTERNAL_KEY`, `VHR_SERVERS_BASE_URL`, `VHR_GAME_ID`,
  `VHR_INSTANCE_ID`). Секрет **не зашивается** в билд.
- Компонент сам поднимает SDK из этих env и периодически (по умолчанию раз в
  **15 с**, поле `reportIntervalSeconds`) репортит число игроков платформе.

Вам нужно лишь сообщать ему текущее число игроков — одним из двух способов:

```csharp
using VhrGames.Sdk;
using UnityEngine;

public sealed class MyServerGlue : MonoBehaviour
{
    [SerializeField] private VhrServerHost vhrHost;

    void OnPlayerJoined() => vhrHost.CurrentPlayers++;
    void OnPlayerLeft()   => vhrHost.CurrentPlayers--;

    // Либо, если число игроков уже хранит ваш NetworkManager, задайте провайдер
    // один раз — он имеет приоритет над CurrentPlayers:
    void Start()
    {
        // vhrHost.PlayerCountProvider =
        //     () => NetworkManager.Singleton.ConnectedClients.Count;
    }
}
```

> Всё внутри `VhrServerHost` null-safe: сетевые ошибки заглушаются
> (`Debug.LogWarning`) и не валят игровой сервер.

### Порт транспорта (обязательно)

`VhrServerHost` берёт на себя репорт игроков, но **привязку вашего сетевого
транспорта к порту вы по-прежнему делаете сами** (SDK не знает про ваш
NetworkManager). В серверной сцене забиндите транспорт на порт из
`VhrServer.ListenPort` (читает `VHR_SERVER_PORT`, дефолт `7777`) и на `0.0.0.0`:

```csharp
using VhrGames.Sdk;
using UnityEngine;

public sealed class DedicatedServerBoot : MonoBehaviour
{
    void Start()
    {
        int port = VhrServer.ListenPort; // env VHR_SERVER_PORT, иначе 7777

        // Пример (UTP / Mirror / FishNet / etc):
        // transport.ConnectionData.Address = "0.0.0.0";
        // transport.ConnectionData.Port    = (ushort)port;
        // networkManager.StartServer();

        Debug.Log($"[VHR] Dedicated server listening on 0.0.0.0:{port}");
    }
}
```

> `VhrServer.ListenPort` валидирует значение (диапазон 1..65535) и никогда не
> кидает исключение. Биндьтесь именно на `0.0.0.0` (не `127.0.0.1`), иначе
> контейнер не примет внешние подключения.

---

## 2. Сборка и загрузка

### Однокнопочная сборка

Меню **`VHR → Собрать серверный билд (Linux x86_64)`**:

- переключает проект на **Dedicated Server (Linux x86_64)**;
- собирает **включённые в Build Settings** сцены в `Builds/Server/`;
- упаковывает результат в **`Builds/vhr-server.zip`**;
- возвращает исходную платформу/подтаргет как было.

В конце путь к zip и напоминание выводятся в Console. Если меню ругается, что
нет включённых сцен — добавьте серверную сцену в *File → Build Settings → Scenes
In Build*. Если не удалось переключить платформу — установите модуль **Linux
Dedicated Server Build Support** через Unity Hub.

> Архив `vhr-server.zip` содержит исполняемый `*.x86_64` и парную папку `*_Data`
> в корне — ровно то, что ждёт платформа.

### Загрузка

1. В кабинете разработчика на странице загрузки игры включите тумблер
   **«Мультиплеер»**.
2. Загрузите **`vhr-server.zip`**, собранный меню выше.
3. **Модерация проходит один раз.** После одобрения платформа сама собирает
   Docker-образ и запускает контейнеры по требованию — пере-собирать и
   пере-модерировать на каждый матч не нужно.

---

## 3. Клиент: получить сервер и подключиться

Клиент (обычная WebGL / нативная сборка с этим SDK) вызывает матчмейкинг и
подключается по адресу из ответа. Адрес выдаётся **по домену/uuid** (не сырому
IP) и уже содержит протокол:

```csharp
using VhrGames.Sdk;
using UnityEngine;

public async void ConnectToMatch()
{
    // Подбирает свободный сервер или поднимает новый в пределах квоты.
    VhrMatch match = await VhrSdk.Servers.MatchAsync();

    if (!match.ok)
    {
        // Напр. code == "no_capacity" — мест нет и квота исчерпана.
        Debug.LogWarning($"[VHR] Нет сервера: {match.code} — {match.message}");
        return;
    }

    // Готовая строка подключения, напр. "udp://abc.servers.vhrgames.ru:34521".
    Debug.Log($"[VHR] connectUri = {match.connectUri} " +
              $"(host={match.host}, port={match.port}, protocol={match.protocol}, " +
              $"slots={match.slots}, players={match.players})");

    // Передайте match.host / match.port / match.protocol в ваш транспорт:
    // transport.ConnectionData.Address = match.host;
    // transport.ConnectionData.Port    = (ushort)match.port;
    // networkManager.StartClient();
}
```

Поля `VhrMatch`:

| Поле | Что это |
|---|---|
| `ok` | `true`, если сервер подобран и готов к подключению |
| `instanceId` | Id инстанса сервера |
| `host` | Хост подключения (домен/uuid, не сырой IP) |
| `port` | Порт |
| `protocol` | Транспорт: `"udp"` \| `"tcp"` |
| `connectUri` | Готовая строка, напр. `udp://abc.servers.vhrgames.ru:34521` |
| `slots` | Вместимость сервера (слотов) |
| `players` | Текущее число игроков |
| `code` / `message` | Код/сообщение при отказе (напр. `"no_capacity"`) |

---

## 4. Репорт игроков — уже автоматизирован

Платформе нужно знать загрузку серверов, чтобы масштабироваться и уведомлять
разработчика. **Это делает `VhrServerHost` за вас** (см. раздел 1): он сам
периодически шлёт `ReportPlayersAsync(VhrServer.InstanceId, CurrentPlayers)`
server-to-server, используя ключ из env `VHR_INTERNAL_KEY`, который платформа
инъектит в контейнер. Никакой `InternalApiKey` в билд класть **не нужно** — и не
нужно вызывать `ReportPlayersAsync` вручную.

Если хотите репортить вручную (свой цикл / свой момент), низкоуровневый вызов
по-прежнему доступен:

```csharp
using VhrGames.Sdk;

bool accepted = await VhrSdk.Servers.ReportPlayersAsync(
    VhrServer.InstanceId, currentPlayers);
```

> На выделенном сервере секрет приходит из окружения (`VHR_INTERNAL_KEY`), а не из
> билда. В клиентских (WebGL) сборках этого ключа нет — поэтому репорт игроков
> делает именно сервер, а не клиент.

---

## 5. Квота серверов

- В рамках тарифа доступен **1 бесплатный сервер** (вместимость ~50–100 слотов).
- Когда мест не хватает и квота исчерпана, `MatchAsync` вернёт `ok=false` с
  `code="no_capacity"`.
- Чтобы масштабироваться (больше параллельных матчей / игроков) — **докупите
  дополнительные серверы** в кабинете разработчика.

---

## Чек-лист

- [ ] На серверном GameObject висит компонент **`VhrServerHost`**, обновляется
      `CurrentPlayers` (или задан `PlayerCountProvider`).
- [ ] Транспорт сервера слушает `VhrServer.ListenPort` на `0.0.0.0`.
- [ ] Серверная сцена включена в *Build Settings → Scenes In Build*.
- [ ] Собрано меню **`VHR → Собрать серверный билд (Linux x86_64)`** →
      `Builds/vhr-server.zip`.
- [ ] Включён тумблер **«Мультиплеер»** при загрузке, залит `vhr-server.zip`.
- [ ] Клиент: `await VhrSdk.Servers.MatchAsync()` → подключение к
      `match.connectUri`.
- [ ] Нужно больше параллельных матчей — докупить серверы (квота).
