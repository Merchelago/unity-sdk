// VHR Games SDK — WebGL WebRTC transport (relay DataChannel upgrade).
//
// Browser WebRTC нельзя дёргать из C# напрямую (в WebGL нет нативного стека
// ICE/DTLS/SCTP). Этот плагин — тонкая обёртка над RTCPeerConnection:
//   C# создаёт peer (VhrRtc_Start), скармливает релейный offer (VhrRtc_SetOffer),
//   подсыпает удалённые ICE-кандидаты (VhrRtc_AddIce), шлёт байты по
//   DataChannel (VhrRtc_Send); входящие события/данные мы прокидываем обратно
//   в C# через указатели на статические методы (function pointers), которые C#
//   передаёт один раз при создании.
//
// Сигнальный обмен (offer/answer/ICE) НЕ ходит по WebRTC — его релеит C# через
// уже открытый WebSocket (0x10-фреймы). Этот плагин только:
//   - создаёт RTCPeerConnection (STUN google),
//   - на релейный offer делает answer и отдаёт его SDP в C# (onAnswer),
//   - трикл-ICE: локальные кандидаты -> C# (onIceCandidate), удалённые <- C#,
//   - принимает DataChannel "game" (ondatachannel, релей его создаёт),
//   - сообщает об открытии канала (onOpen) и о входящих данных (onData).
//
// Маршалинг байтов: входящие сообщения DataChannel приходят как ArrayBuffer;
// копируем их в кучу Unity (_malloc + HEAPU8.set) и отдаём C# (ptr,len); C#
// копирует к себе и освобождает (_free). Строки (SDP/JSON кандидата) копируем в
// кучу через _malloc + stringToUTF8 и тоже отдаём C# (он делает _free).
// Сигнатуры dynCall коллбеков:
//   onOpen(id)                 -> 'vi'
//   onData(id, ptr, len)       -> 'viii'
//   onAnswer(id, sdpPtr)       -> 'vii'
//   onIce(id, candidateJsonPtr)-> 'vii'
//   onError(id)                -> 'vi'
//
// Файл компилируется ТОЛЬКО для WebGL (папка Plugins/WebGL).
var VhrWebRtcLib = {

  // Таблица peer'ов: id -> { pc, channel, callbacks..., open }.
  $vhrRtc: {
    peers: {},
    nextId: 1,

    // Копирует JS-строку в кучу Unity и возвращает указатель (C# делает _free).
    // Возвращает 0 для null/undefined.
    allocStr: function (s) {
      if (s === null || s === undefined) return 0;
      var size = lengthBytesUTF8(s) + 1;
      var ptr = _malloc(size);
      stringToUTF8(s, ptr, size);
      return ptr;
    }
  },

  // Создаёт RTCPeerConnection (STUN google) и возвращает целочисленный handle
  // (>0) или 0 при ошибке. Коллбеки — указатели на статические C#-методы.
  // На этом этапе мы только заводим peer и вешаем обработчики; offer придёт
  // позже через VhrRtc_SetOffer.
  VhrRtc_Start__deps: ['$vhrRtc'],
  VhrRtc_Start: function (cbOpen, cbData, cbAnswer, cbIce, cbError) {
    // Нет WebRTC в этом браузере — пусть C# уйдёт в WS-fallback.
    if (typeof RTCPeerConnection === 'undefined') {
      return 0;
    }

    var id = vhrRtc.nextId++;
    var peer = {
      pc: null,
      channel: null,
      open: false,
      onOpen: cbOpen,
      onData: cbData,
      onAnswer: cbAnswer,
      onIce: cbIce,
      onError: cbError
    };

    try {
      var pc = new RTCPeerConnection({
        iceServers: [{ urls: 'stun:stun.l.google.com:19302' }]
      });

      // Трикл-ICE: локальные кандидаты отдаём C# (он зашлёт их релею как 0x10).
      // Завершающий null-кандидат игнорируем (C# его не использует).
      pc.onicecandidate = function (ev) {
        if (!ev || !ev.candidate) return;
        var c = ev.candidate;
        // Сериализуем кандидат в JSON, который ждёт C#/релей.
        var payload = JSON.stringify({
          candidate: c.candidate,
          sdpMid: c.sdpMid,
          sdpMLineIndex: c.sdpMLineIndex
        });
        var ptr = vhrRtc.allocStr(payload);
        {{{ makeDynCall('vii', 'peer.onIce') }}}(id, ptr);
      };

      // Релей создаёт DataChannel "game" на своей стороне — мы принимаем его и
      // вешаем обработчики прямо тут (без отдельного хелпера/postset).
      pc.ondatachannel = function (ev) {
        var ch = ev.channel;
        try { ch.binaryType = 'arraybuffer'; } catch (e) {}
        peer.channel = ch;

        ch.onopen = function () {
          peer.open = true;
          // dynCall: void(int) — 'vi'.
          {{{ makeDynCall('vi', 'peer.onOpen') }}}(id);
        };

        ch.onmessage = function (mev) {
          var data = mev.data;
          if (typeof data === 'string') {
            // Игровой трафик только бинарный; текст игнорируем.
            return;
          }
          var bytes = new Uint8Array(data);
          var len = bytes.length;
          var ptr = _malloc(len > 0 ? len : 1);
          if (len > 0) {
            HEAPU8.set(bytes, ptr);
          }
          // dynCall: void(int,int,int) — 'viii'. C# копирует и _free(ptr).
          {{{ makeDynCall('viii', 'peer.onData') }}}(id, ptr, len);
        };

        ch.onclose = function () {
          peer.open = false;
        };
      };

      pc.onconnectionstatechange = function () {
        var st = pc.connectionState;
        if (st === 'failed' || st === 'closed') {
          if (!peer.open) {
            {{{ makeDynCall('vi', 'peer.onError') }}}(id);
          }
        }
      };

      peer.pc = pc;
      vhrRtc.peers[id] = peer;
      return id;
    } catch (e) {
      return 0;
    }
  },

  // Принимает релейный offer SDP: setRemoteDescription -> createAnswer ->
  // setLocalDescription -> отдаёт answer SDP в C# (onAnswer). C# зашлёт answer
  // релею как 0x10. Удалённые ICE-кандидаты можно добавлять и до, и после
  // (addIceCandidate буферизуется браузером после setRemoteDescription).
  VhrRtc_SetOffer__deps: ['$vhrRtc'],
  VhrRtc_SetOffer: function (id, sdpPtr) {
    var peer = vhrRtc.peers[id];
    if (!peer || !peer.pc) return;
    var sdp = UTF8ToString(sdpPtr);
    var pc = peer.pc;
    try {
      pc.setRemoteDescription({ type: 'offer', sdp: sdp })
        .then(function () { return pc.createAnswer(); })
        .then(function (answer) {
          return pc.setLocalDescription(answer).then(function () {
            var ptr = vhrRtc.allocStr(pc.localDescription.sdp);
            {{{ makeDynCall('vii', 'peer.onAnswer') }}}(id, ptr);
          });
        })
        .catch(function (e) {
          {{{ makeDynCall('vi', 'peer.onError') }}}(id);
        });
    } catch (e) {
      {{{ makeDynCall('vi', 'peer.onError') }}}(id);
    }
  },

  // Добавляет удалённый ICE-кандидат (JSON {candidate,sdpMid,sdpMLineIndex}),
  // присланный релеем через 0x10 и переданный сюда C#. Пустую строку/мусор
  // молча игнорируем.
  VhrRtc_AddIce__deps: ['$vhrRtc'],
  VhrRtc_AddIce: function (id, candidateJsonPtr) {
    var peer = vhrRtc.peers[id];
    if (!peer || !peer.pc) return;
    var json = UTF8ToString(candidateJsonPtr);
    if (!json) return;
    var obj;
    try { obj = JSON.parse(json); } catch (e) { return; }
    if (!obj || !obj.candidate) return;
    try {
      peer.pc.addIceCandidate(new RTCIceCandidate({
        candidate: obj.candidate,
        sdpMid: obj.sdpMid,
        sdpMLineIndex: obj.sdpMLineIndex
      })).catch(function (e) { /* запоздавший/невалидный кандидат — игнор */ });
    } catch (e) { /* ignore */ }
  },

  // Шлёт бинарный фрейм (ptr,len) по DataChannel. No-op, если канал не открыт.
  // Копируем из кучи в свежий Uint8Array (subarray ссылается на буфер кучи,
  // который может переехать — new Uint8Array(view) даёт независимую копию).
  VhrRtc_Send__deps: ['$vhrRtc'],
  VhrRtc_Send: function (id, ptr, len) {
    var peer = vhrRtc.peers[id];
    if (!peer || !peer.channel || peer.channel.readyState !== 'open') {
      return;
    }
    try {
      var view = HEAPU8.subarray(ptr, ptr + len);
      peer.channel.send(new Uint8Array(view)); // копия, не view на кучу
    } catch (e) {
      // Канал закрылся между проверкой и send — onclose/onError разрулит.
    }
  },

  // 1, если DataChannel id открыт, иначе 0.
  VhrRtc_IsOpen__deps: ['$vhrRtc'],
  VhrRtc_IsOpen: function (id) {
    var peer = vhrRtc.peers[id];
    return (peer && peer.channel && peer.channel.readyState === 'open') ? 1 : 0;
  },

  // Закрывает и удаляет peer id. Безопасно для несуществующего id.
  VhrRtc_Close__deps: ['$vhrRtc'],
  VhrRtc_Close: function (id) {
    var peer = vhrRtc.peers[id];
    if (!peer) return;
    try {
      if (peer.channel) {
        peer.channel.onopen = null;
        peer.channel.onmessage = null;
        peer.channel.onclose = null;
        try { peer.channel.close(); } catch (e) {}
      }
      if (peer.pc) {
        peer.pc.onicecandidate = null;
        peer.pc.ondatachannel = null;
        peer.pc.onconnectionstatechange = null;
        try { peer.pc.close(); } catch (e) {}
      }
    } catch (e) { }
    delete vhrRtc.peers[id];
  }
};

mergeInto(LibraryManager.library, VhrWebRtcLib);
