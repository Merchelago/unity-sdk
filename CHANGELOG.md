# Изменения

Все значимые изменения `ru.vhrgames.sdk` документируются здесь.
Проект следует [Semantic Versioning](https://semver.org/).

## [Unreleased]

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

[1.0.0]: https://github.com/merchelago/vhrgames-unity-sdk/releases/tag/v1.0.0
