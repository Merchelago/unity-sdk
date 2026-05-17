# Изменения

Все значимые изменения `ru.vhrgames.sdk` документируются здесь.
Проект следует [Semantic Versioning](https://semver.org/).

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

[1.0.3]: https://github.com/Merchelago/unity-sdk/releases/tag/v1.0.3
[1.0.2]: https://github.com/Merchelago/unity-sdk/releases/tag/v1.0.2
[1.0.1]: https://github.com/Merchelago/unity-sdk/releases/tag/v1.0.1
[1.0.0]: https://github.com/Merchelago/unity-sdk/releases/tag/v1.0.0
