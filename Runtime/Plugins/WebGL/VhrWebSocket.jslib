// VHR Games SDK — WebGL WebSocket transport (relay client).
//
// Браузерный WebSocket нельзя дёргать из C# напрямую (в WebGL нет
// System.Net.WebSockets.ClientWebSocket). Этот плагин реализует тонкую обёртку:
// C# создаёт сокет (VhrWs_Connect), шлёт байты (VhrWs_Send), а входящие
// сообщения/события мы прокидываем обратно в C# через указатели на статические
// методы (function pointers), которые C# передаёт один раз при создании.
//
// Маршалинг байтов: бинарные фреймы приходят как ArrayBuffer; копируем их в кучу
// Unity (_malloc + HEAPU8.set) и отдаём C# (ptr,len); C# копирует к себе и
// освобождает (_free). Указатели коллбеков вызываем через dynCall с сигнатурами:
//   onOpen(id)               -> 'vi'
//   onMessage(id, ptr, len)  -> 'viii'
//   onClose(id, errFlag)     -> 'vii'
//
// Файл компилируется ТОЛЬКО для WebGL (папка Plugins/WebGL).
var VhrWebSocketLib = {

  // Таблица сокетов: id -> { ws, onOpen, onMessage, onClose }.
  $vhrWs: {
    sockets: {},
    nextId: 1
  },

  // Создаёт WebSocket к url. Возвращает целочисленный handle (>0) или 0 при
  // ошибке. cbOpen/cbMessage/cbClose — указатели на статические C#-методы.
  VhrWs_Connect__deps: ['$vhrWs'],
  VhrWs_Connect: function (urlPtr, cbOpen, cbMessage, cbClose) {
    var url = UTF8ToString(urlPtr);
    var id = vhrWs.nextId++;
    var sock = { ws: null, onOpen: cbOpen, onMessage: cbMessage, onClose: cbClose };

    try {
      var ws = new WebSocket(url);
      ws.binaryType = 'arraybuffer';

      ws.onopen = function () {
        // dynCall: void(int) — сигнатура 'vi'.
        {{{ makeDynCall('vi', 'sock.onOpen') }}}(id);
      };

      ws.onmessage = function (ev) {
        var data = ev.data;
        if (typeof data === 'string') {
          // Релей шлёт только бинарные фреймы; текст игнорируем.
          return;
        }
        var bytes = new Uint8Array(data);
        var len = bytes.length;
        var ptr = _malloc(len > 0 ? len : 1);
        if (len > 0) {
          HEAPU8.set(bytes, ptr);
        }
        // dynCall: void(int,int,int) — 'viii'. C# копирует и делает _free(ptr).
        {{{ makeDynCall('viii', 'sock.onMessage') }}}(id, ptr, len);
      };

      ws.onclose = function () {
        {{{ makeDynCall('vii', 'sock.onClose') }}}(id, 0);
        delete vhrWs.sockets[id];
      };

      ws.onerror = function () {
        // onerror в браузере не несёт деталей; флаг ошибки = 1.
        // За onerror обычно следует onclose — но на всякий случай чистим тут,
        // если соединение так и не открылось.
        try {
          {{{ makeDynCall('vii', 'sock.onClose') }}}(id, 1);
        } catch (e) { }
      };

      sock.ws = ws;
      vhrWs.sockets[id] = sock;
      return id;
    } catch (e) {
      return 0;
    }
  },

  // Шлёт бинарный фрейм (ptr,len) по сокету id. Копируем из кучи в свежий
  // Uint8Array (subarray ссылается на буфер кучи, который может переехать —
  // .slice() даёт независимую копию).
  VhrWs_Send__deps: ['$vhrWs'],
  VhrWs_Send: function (id, ptr, len) {
    var sock = vhrWs.sockets[id];
    if (!sock || !sock.ws || sock.ws.readyState !== 1 /* OPEN */) {
      return;
    }
    try {
      var view = HEAPU8.subarray(ptr, ptr + len);
      sock.ws.send(new Uint8Array(view)); // копия, не view на кучу
    } catch (e) {
      // Сокет закрылся между проверкой и send — onclose разрулит.
    }
  },

  // Закрывает и удаляет сокет id. Безопасно для несуществующего id.
  VhrWs_Close__deps: ['$vhrWs'],
  VhrWs_Close: function (id) {
    var sock = vhrWs.sockets[id];
    if (!sock) return;
    try {
      if (sock.ws) {
        sock.ws.onopen = null;
        sock.ws.onmessage = null;
        sock.ws.onerror = null;
        // onclose оставляем, чтобы C# получил уведомление; но удаляем из таблицы
        // мы внутри onclose. Если состояние уже CLOSED — уберём вручную ниже.
        sock.ws.close();
      }
    } catch (e) { }
    // Если сокет ещё не открыт (CONNECTING) — onclose может не прийти; подчистим.
    if (!sock.ws || sock.ws.readyState === 3 /* CLOSED */) {
      delete vhrWs.sockets[id];
    }
  },

  // 1, если сокет id открыт (readyState OPEN), иначе 0.
  VhrWs_IsOpen__deps: ['$vhrWs'],
  VhrWs_IsOpen: function (id) {
    var sock = vhrWs.sockets[id];
    return (sock && sock.ws && sock.ws.readyState === 1) ? 1 : 0;
  },

  // Освобождение буфера кучи (malloc'нутого в onMessage / строк WebRTC). Раньше
  // C# импортировал libc 'free' напрямую — под IL2CPP/WebGL это объявляло
  // free(intptr_t) и конфликтовало с free(void*) из emscripten. Своя обёртка над
  // _free решает конфликт. Общая для WebSocket и WebRTC (импортируется как VhrFree).
  VhrFree__deps: ['free'],
  VhrFree: function (ptr) { _free(ptr); }
};

mergeInto(LibraryManager.library, VhrWebSocketLib);
