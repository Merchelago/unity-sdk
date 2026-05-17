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
    allowedOrigins: ["https://vhrgames.ru", "http://localhost:5173"]
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
        // Принимаем сообщения СТРОГО с разрешённых origin родителя.
        if (vhrSdkBridge.allowedOrigins.indexOf(event.origin) === -1) {
          return;
        }
        var data = event.data;
        if (!data || typeof data !== "object") {
          return;
        }
        if (data.type === "vhr:sdk:token" && typeof data.token === "string" && data.token.length > 0) {
          vhrSdkBridge.latestToken = data.token;
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
        for (var i = 0; i < vhrSdkBridge.allowedOrigins.length; i++) {
          window.parent.postMessage({ type: "vhr:sdk:token-request" }, vhrSdkBridge.allowedOrigins[i]);
        }
      }
    } catch (e) {
      // Кросс-доменные ограничения / нет родителя — деградируем тихо.
    }
  }
});
