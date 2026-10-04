// VHR Games SDK — WebGL JS interop: реклама через страницу-хост VHR.
//
// Игра (этот iframe, origin api.vhrweb.ru) сама рекламу НЕ рисует и НЕ
// репортит: она просит родительскую страницу (https://vhrgames.ru) показать
// полноэкранную рекламу Рекламной сети Яндекса и ждёт результат. Учёт показов и
// доход разработчика ведёт сайт по факту реальной отрисовки — так игру нельзя
// «накрутить».
//
// Контракт postMessage:
//   iframe → родитель: { type: 'vhr:ads:show',   kind: 'interstitial'|'rewarded', requestId }
//   родитель → iframe: { type: 'vhr:ads:opened', requestId }
//                      — реклама открывается: игре пора поставить паузу и заглушить звук
//   родитель → iframe: { type: 'vhr:ads:result', requestId,
//                        status: 'rewarded'|'closed'|'error'|'unavailable'|'cooldown', reason? }
// Ответы принимаются ТОЛЬКО от window.parent и только с origin платформы
// (vhrSdkBridge.isAllowedOrigin из VhrSdkBridge.jslib — один список на оба канала).
//
// Гарантия «игра не зависнет»: на каждый запрос C# получает ровно один result —
// от хоста, либо по таймауту (хост молчит → unavailable; показ затянулся → closed).
//
// В C# уходят только целые числа через указатель на статический метод:
//   cb(handle, eventCode, status, reason) — сигнатура 'viiii'.
//   eventCode: 1 = opened, 2 = result.
//   status = значение enum VhrAdStatus: 0 unavailable, 1 rewarded, 2 closed, 3 cooldown, 4 error.
//   reason: 0 none, 1 host_timeout, 2 show_timeout, 3 no_host, 4 busy, 5 host_reported.
//
// Файл компилируется ТОЛЬКО для WebGL (папка Plugins/WebGL).
var VhrAdsBridgeLib = {

  $vhrAds: {
    emit: null,          // function(handle, eventCode, status, reason) — ставится в VhrAds_Init
    listenerBound: false,
    nextHandle: 1,
    pending: {},         // requestId -> { handle, opened, ackTimer, maxTimer, maxShowMs }
    STATUS: { unavailable: 0, rewarded: 1, closed: 2, cooldown: 3, error: 4 },

    // Завершает запрос ровно один раз: чистит таймеры и шлёт result в C#.
    finish: function (requestId, status, reason) {
      var p = vhrAds.pending[requestId];
      if (!p) return;
      delete vhrAds.pending[requestId];
      if (p.ackTimer) clearTimeout(p.ackTimer);
      if (p.maxTimer) clearTimeout(p.maxTimer);
      if (vhrAds.emit) vhrAds.emit(p.handle, 2, status, reason);
    }
  },

  // Идемпотентно: запоминает колбэк C# и вешает слушатель ответов хоста.
  // __deps обязателен — иначе Emscripten выкинет $vhrAds/$vhrSdkBridge
  // (ReferenceError в рантайме, как было с vhrSdkBridge).
  VhrAds_Init__deps: ['$vhrAds', '$vhrSdkBridge'],
  VhrAds_Init: function (cb) {
    // Колбэк держим в замыкании (тот же приём, что sock.onOpen в VhrWebSocket.jslib).
    vhrAds.emit = function (handle, eventCode, status, reason) {
      try {
        {{{ makeDynCall('viiii', 'cb') }}}(handle, eventCode, status, reason);
      } catch (e) {
        if (window.console && console.error) console.error('[VHR SDK] ads callback failed', e);
      }
    };

    if (vhrAds.listenerBound) return;
    vhrAds.listenerBound = true;

    try {
      window.addEventListener('message', function (event) {
        var data = event.data;
        if (!data || typeof data !== 'object') return;
        if (data.type !== 'vhr:ads:opened' && data.type !== 'vhr:ads:result') return;
        // Только прямой родитель (страница платформы), не соседние фреймы/вкладки.
        if (event.source !== window.parent) return;
        if (!vhrSdkBridge.isAllowedOrigin(event.origin)) {
          if (window.console && console.warn) {
            console.warn('[VHR SDK] ответ рекламы ОТКЛОНЁН с origin: ' + event.origin);
          }
          return;
        }
        var requestId = typeof data.requestId === 'string' ? data.requestId : '';
        var p = vhrAds.pending[requestId];
        if (!p) return; // чужой или уже завершённый запрос

        if (data.type === 'vhr:ads:opened') {
          if (p.opened) return;
          p.opened = true;
          if (p.ackTimer) { clearTimeout(p.ackTimer); p.ackTimer = null; }
          // Страховка: хост гарантирует result, но если страница сломалась —
          // через maxShowMs отпускаем игру (реклама была открыта → closed).
          p.maxTimer = setTimeout(function () {
            vhrAds.finish(requestId, vhrAds.STATUS.closed, 2);
          }, p.maxShowMs);
          if (vhrAds.emit) vhrAds.emit(p.handle, 1, 0, 0);
          return;
        }

        var status = vhrAds.STATUS.hasOwnProperty(data.status) ? vhrAds.STATUS[data.status] : vhrAds.STATUS.error;
        vhrAds.finish(requestId, status, 5);
      });
    } catch (e) {
      // Нет window (песочница) — Show сам вернёт 0 / ответит по таймауту.
    }
  },

  // 1 — игра во фрейме и (если браузер умеет location.ancestorOrigins)
  // родитель с origin платформы; 0 — точно не хост VHR (открыта напрямую или
  // на чужом сайте). Firefox ancestorOrigins не даёт — тогда 1, а отсутствие
  // хоста выявит таймаут ожидания ответа.
  VhrAds_IsHosted__deps: ['$vhrSdkBridge'],
  VhrAds_IsHosted: function () {
    try {
      if (!window.parent || window.parent === window) return 0;
      var anc = window.location && window.location.ancestorOrigins;
      if (anc && anc.length > 0) {
        return vhrSdkBridge.isAllowedOrigin(anc[0]) ? 1 : 0;
      }
      return 1;
    } catch (e) {
      return 0;
    }
  },

  // Отправляет хосту запрос показа. Возвращает handle (>0) или 0, если
  // отправить нельзя (нет родителя) — тогда колбэков не будет.
  // ВАЖНО: результат НИКОГДА не эмитится синхронно внутри этого вызова —
  // C# регистрирует обработчик handle уже после возврата.
  VhrAds_Show__deps: ['$vhrAds'],
  VhrAds_Show: function (kindPtr, requestIdPtr, ackTimeoutMs, maxShowMs) {
    var kind = UTF8ToString(kindPtr);
    var requestId = UTF8ToString(requestIdPtr);
    try {
      if (!window.parent || window.parent === window) return 0;
    } catch (e) {
      return 0;
    }

    var handle = vhrAds.nextHandle++;
    var p = { handle: handle, opened: false, ackTimer: null, maxTimer: null, maxShowMs: maxShowMs > 0 ? maxShowMs : 300000 };

    if (vhrAds.pending[requestId]) {
      // Дубликат requestId — отвечаем асинхронно, не ломая исходный запрос.
      setTimeout(function () {
        if (vhrAds.emit) vhrAds.emit(handle, 2, vhrAds.STATUS.cooldown, 4);
      }, 0);
      return handle;
    }
    vhrAds.pending[requestId] = p;

    // Хост молчит (игра открыта не на VHR / старая версия сайта без рекламы) →
    // unavailable, чтобы игра не ждала вечно.
    p.ackTimer = setTimeout(function () {
      vhrAds.finish(requestId, vhrAds.STATUS.unavailable, 1);
    }, ackTimeoutMs > 0 ? ackTimeoutMs : 5000);

    try {
      // Запрос секрета не содержит — targetOrigin "*", как у vhr:sdk:token-request:
      // родитель на любом домене платформы получит его, а его ответ мы проверим по origin.
      window.parent.postMessage({ type: 'vhr:ads:show', kind: kind, requestId: requestId }, '*');
    } catch (e) {
      setTimeout(function () {
        vhrAds.finish(requestId, vhrAds.STATUS.unavailable, 3);
      }, 0);
    }
    return handle;
  }
};

mergeInto(LibraryManager.library, VhrAdsBridgeLib);
