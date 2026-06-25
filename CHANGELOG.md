# Изменения

Все значимые изменения `ru.vhrgames.sdk` документируются здесь.
Проект следует [Semantic Versioning](https://semver.org/).

## [1.7.3] - 2026-06-25

### Исправлено (КРИТ: протухший токен на старте лобби)
- **`VhrLobbyService.AcquirePlayerTokenAsync` теперь учитывает срок жизни токена (`IsTokenStale`).**
  JWT платформы живёт ~15 мин; к моменту создания лобби токен в URL `?access_token=`/кэше мог
  УЖЕ протухнуть. v1.7.2 просил свежий только если провайдер пуст — а протухший НЕпустой токен
  слал как есть → релей `auth_failed`. Теперь SDK декодирует `exp` (без проверки подписи) и, если
  токен пуст ИЛИ истёк/истекает в ближайшие 90 c, просит родителя обновить (`vhr:sdk:token-request`
  → его refresh → `auth:token-refreshed` → postMessage) и ждёт НЕпротухший до ~3 c. Свежий токен —
  отдаётся сразу. Подтверждено live-тестом: бэкенд (relay + REST) принимает валидный токен и создаёт
  лобби — баг был исключительно в доставке свежего токена клиентом.
- Диагностика в консоль уточнена: `токен свежий получен` / `токен ПРОТУХ и обновить не удалось` /
  `ТОКЕН ОТСУТСТВУЕТ`.

## [1.7.2] - 2026-06-25

### Исправлено (КРИТ: токен игрока на WebGL — лобби висло + друзья не грузились)
- **`VhrLobbyService`: токен игрока берётся ДО отправки relay-`auth`** (`AcquirePlayerTokenAsync`).
  Корень: страница платформы шлёт JWT в iframe через `postMessage` на `onLoad` — РАНЬШЕ, чем
  SDK навешивает слушатель (Unity ещё грузится), а в URL токена могло не быть → `auth` уходил
  ПУСТЫМ, релей его отвергал: «Создание лобби…» висело, а REST `/api/Friends` падал в 401
  (друзей не видно). Теперь если провайдер вернул пусто — SDK просит токен у родителя
  (`vhr:sdk:token-request`) и ждёт его появления до ~2 c, затем шлёт `auth`. В консоль пишется
  `[VHR Lobby] auth → токен получен/ОТСУТСТВУЕТ` для диагностики.
- **`VhrSdkBridge.jslib`: расширен и стал диагностируемым.** Принимаем токен с любого https-
  сабдомена `vhrgames.ru` (вкл. `www.`) + localhost; **отклонённые origin логируются**
  (`console.warn`), принятые — `console.log` (видно в F12, какой родитель шлёт токен). Запрос
  токена шлётся с `targetOrigin "*"` (в нём нет секрета), чтобы родитель на любом домене ответил.
- **`VhrLobbyService.HandleRelayClosed`: обрыв сокета сразу фейлит ждущие `_lobbyTcs`/`_startTcs`**
  (а не ждёт 15-с таймаут) — UI мгновенно показывает ошибку вместо лишних секунд ожидания.
- **`VhrLobbyService`: обрабатывается `ev:auth` → `SelfUserId`** выставляется сразу после auth
  (раньше — только в `ev:start`), поэтому `IsHost`/`isSelf` корректны в лобби до старта матча.

## [1.7.1] - 2026-06-24

### Исправлено (КРИТ: лобби висло на WebGL)
- **Релей/лобби: продолжения TaskCompletionSource теперь `RunContinuationsAsynchronously`.**
  На WebGL (нет тредпула) await-продолжения выполнялись СИНХРОННО внутри JS-колбэка
  сокета, инвертируя порядок «отправил → жду → ответ позже»: ответный управляющий
  кадр (`ev:lobby`/`ev:error`/join `0x81`) приходил вложенно в стек отправки, раньше
  подписки/создания TCS, и терялся → `CreateLobbyAsync`/`QuickMatchAsync`/`StartAsync`
  висели вечно («Создание лобби…»). В редакторе (нативный сокет, фоновые потоки) баг
  не воспроизводился. Применено ко всем TCS: `VhrRelay._joinedTcs`, `VhrLobby._lobbyTcs`/
  `_startTcs`, `WebGLVhrSocket._connectTcs`, `WebGLWebRtc._openTcs`.
- **`VhrLobbyService.EnsureReadyAsync`: подписка на `OnControlFrame`/`OnClosed` ДО `ConnectAsync`**
  (а не после) — чтобы кадр, пришедший во время/сразу после подключения, не потерялся.
- **Жёсткий таймаут (15с) на ожидание ответа лобби** (`AwaitReplyAsync`) — SDK больше не
  висит вечно даже при потере кадра, а отдаёт `VhrSdkException("lobby_timeout")`.
- **`VhrRelay`: сброс `SelfId=0` при закрытии сокета** — `IsConnected` однозначно становится
  false, следующий вызов делает полный реконнект + повторный auth.

## [1.7.0] - 2026-06-23

### Добавлено
- **Профиль игрока — `VhrSdk.Profile` (`IVhrProfile`).** `GetMeAsync()` (текущий игрок:
  id/email/userName/roles), `ResolveAsync(userIds)` и `ResolveNamesAsync(userIds)` —
  **батч-резолв ников по userId** через `AuthMS /api/Auth/users/resolve` (до 100 за запрос,
  с авто-чанкингом). Основа для отображения имён друзей/лидербордов/авторов.
- **Друзья — `VhrSdk.Friends` (`IVhrFriends`).** Полный API поверх `UnityGamesMS/FriendsController`:
  список друзей, заявки (входящие/исходящие), отправить/принять/отклонить/отозвать,
  статус пары, удалить, приглашение в игру.
- **Прогресс — `VhrSdk.PlayerStats` (`IVhrPlayerStats`).** `GetMyStatsAsync()`: уровень, XP,
  коины, ранг (tier/subTier/label/...), косметика, winRate, серия, подписка.
- **Достижения — `VhrSdk.Achievements` (`IVhrAchievements`).** Платформенные/игровые ачивки,
  свои/чужие разблокировки, `my-games`.
- **Сессии — `VhrSdk.GameSessions` (`IVhrGameSessions`).** `StartAsync`/`EndAsync`/`HeartbeatAsync`
  (трекинг времени/исхода — питает XP/статы).
- **Базовые URL:** `VhrSdkOptions.AuthBaseUrl` (`/auth`) и `NotificationsBaseUrl` (`/notifications`).

### Изменено
- **`IVhrLeaderboard` переведён на реальный бэкенд** `UnityGamesMS /api/Leaderboard`
  (`GetGlobalAsync`/`GetForGameAsync`/`SubmitScoreAsync(gameId, score)`); убран устаревший
  501-seam на `BridgeBaseUrl/api/leaderboard`. (Старые `SubmitAsync(userId,score)`/`GetTopAsync`
  заменены — вызовов в проектах не было.)

## [1.6.1] - 2026-06-20

### Добавлено
- `VhrLobbyPanel` — готовый drop-in экран лобби/матчмейкинга (OnGUI): быстрый матч с добивкой
  ботами, приватное лобби с кодом, список друзей и приглашения, готовность, старт. Повесить на
  GameObject — работает без настройки Canvas; одновременно эталонная реализация на `VhrSdk.Lobby`.

## [1.6.0] - 2026-06-20

### Добавлено
- **Лобби и матчмейкинг — `VhrSdk.Lobby` (`IVhrLobby`).** Полный API подбора
  игроков поверх того же релея: **быстрый матч** (собрать игроков, добить пустые
  слоты ботами), **приватные лобби**, **приглашение друзей платформы**,
  **готовность (ready-up)** и **старт**. Работает в **WebGL и нативе** —
  ездит по **тому же WebSocket-соединению**, что и `VhrRelay`, новым
  управляющим фреймом `0x20` (`[0x20][utf8 JSON]`); новых нативных зависимостей
  нет.
  - `QuickMatchAsync(opts)` — шлёт `quickmatch`, ждёт старта, возвращает
    `VhrMatchInfo` и **сам входит** в relay-комнату матча (`"match-xxxx"`), так
    что игра сразу шлёт данные через `VhrSdk.Relay`. По ходу — события
    `OnLobbyUpdated`/`OnMatchStarting`.
  - `CreateLobbyAsync(opts)` (приватное лобби с `code`), `JoinLobbyAsync(code)`,
    `LeaveLobbyAsync()`, `CancelAsync()`, `SetReadyAsync(ready)`,
    `InviteFriendAsync(userId)`, `KickAsync(userId)`,
    `StartAsync()` (хост; ждёт старт + авто-вход в комнату).
  - `GetFriendsAsync()` — REST `GET {GamesBaseUrl}/api/Friends` (UnityGamesMS,
    `FriendsController`) → список друзей для приглашения.
  - События: `OnLobbyUpdated`, `OnInviteReceived`, `OnMatchStarting`,
    `OnMatchStarted`, `OnClosed`; свойства `CurrentLobby`, `IsHost`,
    `SelfUserId`. Все события маршалятся на главный Unity-поток (как у `VhrRelay`).
  - **Боты — на стороне игры.** SDK отдаёт `VhrMatchInfo.botSlots`/`botCount`;
    спавнит ботов сама игра (хост — авторитет), трафик идёт через `VhrSdk.Relay`.
  - Новые модели `Runtime/Models/VhrLobbyModels.cs`: `VhrLobby`,
    `VhrLobbyMember`, `VhrLobbyInvite`, `VhrMatchInfo`, `VhrMatchPlayer`,
    `VhrMatchmakingOptions`, `VhrLobbyOptions`, `VhrFriend`.
- **Seam в `VhrRelay` для общего сокета.** Новый метод `SendControl(frame)`
  (шлёт сырой фрейм по сокету релея), событие `OnControlFrame(type, payload)`
  (фреймы, которые сам релей не обрабатывает — сегодня `0x20`), и
  `JoinRoomRawAsync(roomId)` (вход в комнату по явному id — для входа в
  `"match-xxxx"` на старте матча). Обработка релеем `0x83/0x84/0x85/0x81/0x10`
  не изменилась.
- **Аксессор** `VhrSdk.Lobby` — ленивый `VhrLobbyService` поверх общего
  `VhrSdk.Relay`, опций и api-клиента.

### Изменено
- Новая документация `Documentation~/Lobby.md`: полный API лобби/матчмейкинга +
  примеры (быстрый матч с ботами; приватное лобби + приглашение друзей). Ссылка
  добавлена в `Documentation~/Multiplayer.md`.

## [1.5.0] - 2026-06-20

### Добавлено
- **Низколатентный WebRTC-апгрейд для WebGL (UDP-подобный DataChannel) с
  авто-fallback на WebSocket.** В браузере релей (`VhrRelay`) после входа в
  комнату прозрачно пытается поднять **WebRTC DataChannel** `"game"`
  (unreliable/unordered — UDP-подобный, низкая задержка) и переключить на него
  игровой трафик. При недоступности WebRTC, несхождении ICE или таймауте (~5 с)
  релей **остаётся на WebSocket** — поведение 1.4 без изменений. Полностью
  **прозрачно для разработчика**: тот же `Send`/`SendTo`/`OnData` и та же
  семантика комнаты; код менять не нужно. Натив/редактор всегда на WebSocket.
  - Новый WebGL-плагин `Runtime/Plugins/WebGL/VhrWebRtc.jslib` (браузерный
    `RTCPeerConnection`, STUN `stun:stun.l.google.com:19302`) + C#-мост
    `Runtime/Net/WebGLWebRtc.cs` (handle-таблица + `[MonoPInvokeCallback]`).
  - Сигналинг (offer/answer/ICE) релеится по **той же** сокет-связи новым
    фреймом `0x10` (`[0x10][utf8 JSON]`): релей создаёт offer и DataChannel,
    клиент отвечает answer'ом и трикл-ICE'ит. Контроль (join/peer-события)
    остаётся на WebSocket.
  - Новая опция `VhrSdkOptions.PreferWebRtc` (по умолчанию `true`) — можно
    выключить апгрейд и принудительно остаться на WebSocket.
  - Новое диагностическое свойство `VhrRelay.Transport` (`"ws"` | `"webrtc"`).

### Изменено
- `Documentation~/Multiplayer.md`: отмечено, что в браузере релей сам
  апгрейдится на WebRTC для низкой задержки, WebSocket — fallback; код менять
  не нужно.

## [1.4.0] - 2026-06-20

### Добавлено
- **Простой мультиплеер через релей — без серверной сборки.** Новый клиент
  `VhrRelay` (`Runtime/Services/VhrRelay.cs`): разработчик собирает **только
  клиент**, подключается к общему платформенному relay
  (`wss://servers.vhrweb.ru/ws`, поле `VhrSdkOptions.RelayBaseUrl`), входит в
  комнату `"{GameId}:{lobbyCode}"` (lobbyCode по умолчанию `"main"`) и
  пересылает байты другим игрокам. API: `ConnectAsync(lobbyCode)`,
  `Send(bytes)` (broadcast), `SendTo(peerId, bytes)`, события
  `OnData`, `OnJoined`, `OnPeerJoined`, `OnPeerLeft`, `OnClosed`, `CloseAsync()`.
  Бинарный протокол little-endian (`0x01/0x02/0x04` → relay,
  `0x81/0x83/0x84/0x85` ← relay).
- **WebGL-совместимый WebSocket-транспорт** за одним интерфейсом `IVhrSocket`
  (`Runtime/Net/`): `NativeVhrSocket` (натив/редактор, `ClientWebSocket`) и
  `WebGLVhrSocket` (WebGL, браузерный `WebSocket` через новый плагин
  `Runtime/Plugins/WebGL/VhrWebSocket.jslib`). `ClientWebSocket` в WebGL не
  работает — поэтому два бэкенда. События нативного сокета маршалятся на главный
  Unity-поток.
- **Drop-in компонент** `VhrRelayBootstrap`
  (`Runtime/Components/VhrRelayBootstrap.cs`): на `Start` подключается к
  `VhrSdk.Relay`, входит в лобби (поле `lobbyCode`) и пробрасывает события релея
  как UnityEvents (для инспектора) и C#-события. Null-safe.
- **Аксессор** `VhrSdk.Relay` — ленивый общий `VhrRelay` из опций инициализации.
- **Опция** `VhrSdkOptions.RelayBaseUrl` (по умолчанию `wss://servers.vhrweb.ru/ws`).

### Изменено
- `Documentation~/Multiplayer.md`: добавлен раздел «Простой мультиплеер (релей,
  без серверной сборки)» как основной путь; раздел про выделенный сервер помечен
  как «продвинутый».

## [1.3.0] - 2026-06-20

### Добавлено
- **Zero-config выделенный сервер.** Платформа теперь сама инъектит в серверный
  контейнер переменные окружения (`VHR_SERVER_PORT`, `VHR_INSTANCE_ID`,
  `VHR_GAME_ID`, `VHR_INTERNAL_KEY`, `VHR_SERVERS_BASE_URL`) — разработчик ничего
  не настраивает, **секретный ключ больше не зашивается в билд**.
- **Drop-in компонент** `VhrServerHost` (`Runtime/Components/VhrServerHost.cs`):
  перетащите на любой GameObject в серверной сцене — он сам, только в серверной
  сборке (`VhrServer.IsServerBuild`), поднимает SDK из env и периодически
  (по умолчанию раз в 15 с) репортит число игроков через `ReportPlayersAsync`.
  Обновляйте `CurrentPlayers` или задайте `PlayerCountProvider`. Всё null-safe,
  ошибки заглушаются (`Debug.LogWarning`).
- **Однокнопочная сборка сервера** — меню
  `VHR → Собрать серверный билд (Linux x86_64)` (`Editor/VhrBuildMenu.cs`):
  переключает на Dedicated Server (Linux x86_64), собирает включённые сцены в
  `Builds/Server/`, зипует в `Builds/vhr-server.zip` и возвращает платформу как
  было.
- **Хелперы env** в `VhrServer`: `InstanceId`, `GameId`, `InternalKey`,
  `ServersBaseUrl`, `IsServerBuild` (никогда не кидают, разумные дефолты).
- **Фабрика без DI** `VhrSdk.CreateStandaloneServers(options)` — собирает
  самодостаточный `IVhrServers` той же цепочкой, что и обычная инициализация,
  без VContainer и без глобального init.

### Изменено
- `Documentation~/Multiplayer.md` упрощён под новый поток: бросить `VhrServerHost`
  на серверный GameObject → меню `VHR → Собрать серверный билд` → залить
  `vhr-server.zip` через тумблер «Мультиплеер». Убраны ручные инструкции про
  задание `InternalApiKey` в серверной сборке (теперь авто через env).

## [1.2.0] - 2026-06-20

### Добавлено
- **Мультиплеер на выделенном сервере (Dedicated Server).** Платформа сама
  собирает Docker-образ из билда Unity **Dedicated Server** (Linux x86_64) и
  запускает контейнеры. Контейнер стартует как
  `-batchmode -nographics -port <PORT>` и экспортит порт в env
  `VHR_SERVER_PORT`. Модерация — один раз, дальше платформа билдит/гоняет образ.
- **Хелпер порта** — `VhrServer.ListenPort` (+ `VhrServer.TryReadPort(out int)`):
  читает `VHR_SERVER_PORT` из окружения, валидирует диапазон 1..65535, дефолт
  `7777`. Сервер биндит транспорт на `0.0.0.0:<ListenPort>`.
- **Документация** — `Documentation~/Multiplayer.md`: как собрать совместимый
  выделенный сервер, загрузить zip (тумблер «Мультиплеер»), подключиться на
  клиенте через `MatchAsync` → `VhrMatch.connectUri`, репортить игроков через
  `ReportPlayersAsync` (server-to-server, `InternalApiKey` только в серверных
  сборках), и про квоту серверов.

## [1.1.1] - 2026-06-20

### Исправлено
- `IVhrServers.MatchAsync` — добавлен `allowNotImplemented: true` (если эндпоинт
  `/match` ещё не задеплоен → терпимый возврат вместо исключения) и убрана
  безусловная перезапись `ok` (если сервер прислал телом `ok=false`, не затираем).

## [1.1.0] - 2026-06-20

### Добавлено
- **Матчмейкинг серверов** — `IVhrServers.MatchAsync(gameId?)`: подбирает игровой
  сервер для подключения (свободный или поднимает новый в пределах квоты).
  Подключение по **домену/uuid** (не сырому IP) и **UDP/TCP** — результат
  `VhrMatch` несёт готовый `connectUri` (напр. `udp://abc.servers.vhrgames.ru:34521`),
  `protocol`, слоты/игроков. При исчерпании квоты `ok=false`, `code="no_capacity"`.
- **Репорт игроков** — `IVhrServers.ReportPlayersAsync(instanceId, count)` для
  выделенных серверов (server-to-server, требует `InternalApiKey`); при заполнении
  без свободной квоты платформа уведомляет разработчика.
- **Реклама** — `IVhrEconomy.ReportAdAsync(type)`: репорт показа/клика/награды;
  доход считает сервер (анти-накрутка), доля начисляется разработчику игры.
- **Турниры** — новый сервис `IVhrTournaments`: `ListAsync(status)`,
  `GetStandingsAsync(id)`, `JoinAsync(id)`, `SubmitScoreAsync(id, score)`. База —
  `VhrSdkOptions.GamesBaseUrl` (по умолчанию `https://api.vhrweb.ru/games`).
- `VhrApiClient.SendRawAsync` — сырое тело ответа (для эндпоинтов с JSON-массивом
  верхнего уровня, который JsonUtility не парсит напрямую).

## [1.0.3] - 2026-05-17

### Исправлено
- **Критично (WebGL halt):** `VhrSdkBridge.jslib` — добавлены
  `*__deps: ['$vhrSdkBridge']` к `VhrSdk_Init`/`VhrSdk_GetLatestToken`/
  `VhrSdk_RequestToken`. Без `__deps` Emscripten dead-strip'ал объект
  `$vhrSdkBridge`, и в рантайме падало
  `ReferenceError: vhrSdkBridge is not defined` в `_VhrSdk_Init`
  (игра halt'илась на старте).

## [1.0.2] - 2026-05-17

### Исправлено
- `VhrSdk.SdkVersion` синхронизирован с версией пакета (был зашит `1.0.0`,
  из-за чего build-marker `vhr-sdk.json` писал устаревшую версию).
  Теперь константа == `package.json` version.

## [1.0.1] - 2026-05-17

### Исправлено
- Сборка в Unity 6: добавлен `using UnityEngine;` в `VhrSdk.cs`
  (`Awaitable` — `UnityEngine.Awaitable`, ошибка CS0246).
- `VhrSdkEntryPoint` переведён с `IAsyncStartable` (требует UniTask) на
  `IStartable` — fire-and-forget init через `Awaitable` + try/catch, без
  зависимости от UniTask. Устраняет каскадную ошибку резолва
  `VhrGames.Sdk.Editor` (Burst/Mono.Cecil).
- В репозиторий добавлены Unity `.meta`-файлы для всех ассетов/папок
  пакета (git-UPM пакет immutable — Unity их сам не генерирует).

### Добавлено
- **Жизненный цикл JWT (~15 мин).** WebGL-плагин
  `Runtime/Plugins/WebGL/VhrSdkBridge.jslib` + C#-обёртка
  `VhrWebGlTokenChannel`: родитель (`https://vhrgames.ru`, дев
  `http://localhost:5173`) присылает свежий токен через `postMessage`
  (`vhr:sdk:token`), SDK может запросить обновление (`vhr:sdk:token-request`).
  Дефолтный WebGL `TokenProvider` отдаёт приоритет токену из канала, fallback —
  `?access_token` из URL.
- `VhrApiClient`: на `401` запрашивает обновление токена, ждёт ~5 с новый
  (отличный от использованного) и повторяет запрос **ровно один раз**;
  WebGL-safe (без потоков, через `Awaitable`). Вне WebGL — один перечит
  `TokenProvider`.

### Изменено
- **Модель экономики: клиент только списывает (anti-mint).** `GrantCoinsAsync`
  и монетная награда `GrantAchievementAsync` помечены как **server-only**
  (из клиентской WebGL-сборки → `403 forbidden` / награда форсится в 0).
  Документация (`README.md`, `Documentation~/index.md`) и семпл
  (`EconomySample.cs`) переведены на сценарий списания/покупки.

## [1.0.0] - 2026-05-17

### Добавлено
- Первый релиз обязательного SDK VHR Games для Unity 6.
- Handshake через маркер сборки: `VhrSdkBuildMarker` пишет
  `Assets/StreamingAssets/vhr-sdk.json` перед каждой сборкой
  (`sdk`, `sdkVersion`, `unityVersion`, `buildTimeUtc`, `buildTarget`). Бэкенд
  проверяет его на `confirm-upload`; нет → `sdk_required`,
  устарел → `sdk_outdated`.
- VContainer-бутстрап `VhrSdkLifetimeScope` + `VhrSdkEntryPoint`, и статический
  фасад без DI `VhrSdk` (гибридный биндинг).
- Объект конфигурации `VhrSdkOptions` с валидацией и ленивым token-провайдером.
- Сервисы:
  - `IVhrEconomy` — баланс / начисление монет / начисление ачивки / списание /
    покупка, идемпотентно через клиентский `externalId`, `Observable<BalanceChanged>`.
  - `IVhrLeaderboard` — submit / top, seam следующей волны, терпим к 501.
  - `IVhrServers` — bind / list / request instance, по умолчанию noop-aware.
  - `IVhrSession` — id игры, токен, R3-поток состояния подключения.
- Тестируемый транспортный seam `IVhrHttp` с `UnityWebRequestHttp` (WebGL-safe)
  и типизированный `VhrApiClient`.
- Документация (`README.md`, `Documentation~/index.md`) и `Samples~/Basic`.

[1.6.0]: https://github.com/Merchelago/unity-sdk/releases/tag/v1.6.0
[1.5.0]: https://github.com/Merchelago/unity-sdk/releases/tag/v1.5.0
[1.4.0]: https://github.com/Merchelago/unity-sdk/releases/tag/v1.4.0
[1.3.0]: https://github.com/Merchelago/unity-sdk/releases/tag/v1.3.0
[1.2.0]: https://github.com/Merchelago/unity-sdk/releases/tag/v1.2.0
[1.0.3]: https://github.com/Merchelago/unity-sdk/releases/tag/v1.0.3
[1.0.2]: https://github.com/Merchelago/unity-sdk/releases/tag/v1.0.2
[1.0.1]: https://github.com/Merchelago/unity-sdk/releases/tag/v1.0.1
[1.0.0]: https://github.com/Merchelago/unity-sdk/releases/tag/v1.0.0
