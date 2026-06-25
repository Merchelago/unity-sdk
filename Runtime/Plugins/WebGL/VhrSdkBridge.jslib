// VHR Games SDK — WebGL JS interop bridge.
//
// Назначение: жизненный цикл JWT игрока. Сайт VHR встраивает игру в <iframe>
// (origin = api.vhrweb.ru), URL которого один раз при загрузке несёт
// ?access_token=<jwt>. Токен живёт ~15 минут; игровые сессии длиннее. Чтобы не
// падать в 401 после протухания, родительская страница (https://vhrgames.ru)
// присылает свежий токен через postMessage, а SDK умеет его запросить.
//
// Контракт postMessage:
//   Родитель → iframe:  { type: 'vhr:sdk:token', token: '<jwt>' }
//   iframe → родитель:  { type: 'vhr:sdk:token-request' }
// Принимаются ТОЛЬКО сообщения с event.origin из VHR_ALLOWED_ORIGINS
// (прод vhrgames.ru + localhost:5173 для дев-режима).
//
// Этот файл компилируется ТОЛЬКО для WebGL (папка Plugins/WebGL).
mergeInto(LibraryManager.library, {

  // Внутреннее состояние храним на window, чтобы пережить повторную
  // инициализацию модуля и быть доступным всем экспортам.
  $vhrSdkBridge: {
    latestToken: "",
    listenerBound: false,
    // Разрешённые origin РОДИТЕЛЯ (страницы платформы), присылающего токен.
    // event.origin у postMessage — это origin родителя (vhrgames.ru), НЕ iframe.
    allowedOrigins: ["https://vhrgames.ru", "https://www.vhrgames.ru", "http://localhost:5173"],
    // Принимаем токен с любого https-сабдомена vhrgames.ru (www. и пр.), плюс
    // точные совпадения из списка и localhost для дев-режима. Возврат истории
    // отклонённых origin'ов — в console.warn (см. слушатель), чтобы при смене
    // домена платформы было видно, что добавить.
    isAllowedOrigin: function (origin) {
      if (!origin) return false;
      if (vhrSdkBridge.allowedOrigins.indexOf(origin) !== -1) return true;
      if (/^https:\/\/([a-z0-9-]+\.)*vhrgames\.ru$/i.test(origin)) return true;
      if (/^https?:\/\/(localhost|127\.0\.0\.1)(:\d+)?$/i.test(origin)) return true;
      return false;
    }
  },

  // Идемпотентная установка слушателя message. Вызывается из C# при инициализации
  // WebGL-канала. Безопасно вызывать несколько раз.
  // ВАЖНО: __deps обязателен — иначе Emscripten dead-strip'ает $vhrSdkBridge,
  // и в рантайме будет ReferenceError: vhrSdkBridge is not defined.
  VhrSdk_Init__deps: ['$vhrSdkBridge'],
  VhrSdk_Init: function () {
    if (vhrSdkBridge.listenerBound) {
      return;
    }
    vhrSdkBridge.listenerBound = true;

    try {
      window.addEventListener("message", function (event) {
        var data = event.data;
        if (!data || typeof data !== "object" || data.type !== "vhr:sdk:token") {
          return; // не наш канал — на странице много message-событий, молчим
        }
        // Это попытка прислать токен игрока — проверяем origin родителя.
        if (!vhrSdkBridge.isAllowedOrigin(event.origin)) {
          if (window.console && console.warn) {
            console.warn("[VHR SDK] токен postMessage ОТКЛОНЁН с origin: " + event.origin +
              " — если это страница платформы, добавьте origin в allowedOrigins (VhrSdkBridge.jslib).");
          }
          return;
        }
        if (typeof data.token === "string" && data.token.length > 0) {
          vhrSdkBridge.latestToken = data.token;
          if (window.console && console.log) {
            console.log("[VHR SDK] токен игрока получен от " + event.origin + " (len=" + data.token.length + ")");
          }
        }
      });
    } catch (e) {
      // В песочнице/без window — тихо игнорируем, C# деградирует на URL-парсинг.
    }
  },

  // Возвращает последний полученный токен (или пустую строку). Строку
  // выделяем в куче Unity по стандартному паттерну Unity 6.
  VhrSdk_GetLatestToken__deps: ['$vhrSdkBridge'],
  VhrSdk_GetLatestToken: function () {
    var s = vhrSdkBridge.latestToken || "";
    var size = lengthBytesUTF8(s) + 1;
    var buffer = _malloc(size);
    stringToUTF8(s, buffer, size);
    return buffer;
  },

  // Просит родительскую страницу обновить токен и прислать свежий.
  // Постим во все разрешённые origin (прод + дев); неподходящий просто
  // отбросит сообщение по targetOrigin.
  VhrSdk_RequestToken__deps: ['$vhrSdkBridge'],
  VhrSdk_RequestToken: function () {
    try {
      if (window.parent && window.parent !== window) {
        // Запрос токена секрета НЕ содержит — шлём с targetOrigin "*", чтобы
        // родитель на любом домене (vhrgames.ru / www. / иной) гарантированно
        // получил запрос и ответил токеном (его origin мы потом проверим в слушателе).
        window.parent.postMessage({ type: "vhr:sdk:token-request" }, "*");
      }
    } catch (e) {
      // Кросс-доменные ограничения / нет родителя — деградируем тихо.
    }
  }
});
