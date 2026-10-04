// VHR Games SDK — WebGL JS interop: покупки и пополнение через страницу-хост VHR.
//
// Игра (этот iframe) НЕ проводит оплату и НЕ подтверждает покупку сама: сервер
// создаёт намерение покупки (intent), а подтверждает его ИГРОК в модальном окне
// платформы на родительской странице (https://vhrgames.ru). Пополнение счёта —
// только на платформе: игра лишь просит сайт открыть окно пополнения.
//
// Контракт postMessage:
//   iframe → родитель: { type: 'vhr:purchase:confirm', intentId, requestId }
//   родитель → iframe: { type: 'vhr:purchase:result', requestId, intentId,
//                        status: 'completed'|'cancelled'|'insufficient_funds'|'expired'|'error',
//                        balance?, purchaseId? }
//   iframe → родитель: { type: 'vhr:topup:open' }
//   родитель → iframe: { type: 'vhr:balance:changed', balance }
// Ответы принимаются ТОЛЬКО от window.parent и только с origin платформы
// (vhrSdkBridge.isAllowedOrigin из VhrSdkBridge.jslib — один список на все каналы).
//
// Гарантия «игра не зависнет»: на каждый confirm C# получает ровно один result —
// от хоста или по таймауту (по умолчанию 3 мин → expired; намерение на сервере
// живёт 120 с, хост сам сообщит expired раньше).
//
// В C# уходят только целые числа через указатель на статический метод:
//   cb(handle, eventCode, status, reason) — сигнатура 'viiii'.
//   eventCode: 1 = результат покупки, 2 = хост сообщил новый баланс.
//   status: 0 error, 1 completed, 2 cancelled, 3 insufficient_funds, 4 expired.
//   reason: 0 none, 1 timeout, 2 no_host, 3 busy, 4 host_reported, 5 post_failed.
// Детали (balance, purchaseId) C# забирает строкой JSON через VhrEconomy_TakePayload(handle)
// — тем же способом возврата строки, что и VhrSdk_GetLatestToken.
//
// Файл компилируется ТОЛЬКО для WebGL (папка Plugins/WebGL).
var VhrEconomyBridgeLib = {

  $vhrEconomy: {
    emit: null,          // function(handle, eventCode, status, reason) — ставится в VhrEconomy_Init
    listenerBound: false,
    nextHandle: 1,
    pending: {},         // requestId -> { handle, intentId, timer }
    payloads: {},        // handle -> JSON-строка с деталями для C#
    STATUS: { error: 0, completed: 1, cancelled: 2, insufficient_funds: 3, expired: 4 },

    // Баланс: целое число >= 0 или null (неизвестен / мусор).
    toBalance: function (v) {
      if (typeof v === 'string' && v.length > 0 && /^\d+$/.test(v)) v = Number(v);
      if (typeof v !== 'number' || !isFinite(v) || v < 0) return null;
      return Math.floor(v);
    },

    // Origin родителя для targetOrigin: точный, если браузер его знает и он из
    // списка платформы; иначе '*' (сообщения секретов не содержат, ответы
    // проверяются по origin в слушателе).
    parentTarget: function () {
      try {
        var anc = window.location && window.location.ancestorOrigins;
        if (anc && anc.length > 0 && vhrSdkBridge.isAllowedOrigin(anc[0])) return anc[0];
      } catch (e) { }
      return '*';
    },

    // Завершает запрос ровно один раз: чистит таймер, кладёт детали и шлёт result в C#.
    finish: function (requestId, status, reason, detail) {
      var p = vhrEconomy.pending[requestId];
      if (!p) return;
      delete vhrEconomy.pending[requestId];
      if (p.timer) clearTimeout(p.timer);
      var d = detail || {};
      d.intentId = p.intentId;
      vhrEconomy.payloads[p.handle] = JSON.stringify(d);
      if (vhrEconomy.emit) vhrEconomy.emit(p.handle, 1, status, reason);
    }
  },

  // Идемпотентно: запоминает колбэк C# и вешает слушатель ответов хоста.
  // __deps обязателен — иначе Emscripten выкинет $vhrEconomy/$vhrSdkBridge.
  VhrEconomy_Init__deps: ['$vhrEconomy', '$vhrSdkBridge'],
  VhrEconomy_Init: function (cb) {
    vhrEconomy.emit = function (handle, eventCode, status, reason) {
      try {
        {{{ makeDynCall('viiii', 'cb') }}}(handle, eventCode, status, reason);
      } catch (e) {
        if (window.console && console.error) console.error('[VHR SDK] economy callback failed', e);
      }
    };

    if (vhrEconomy.listenerBound) return;
    vhrEconomy.listenerBound = true;

    try {
      window.addEventListener('message', function (event) {
        var data = event.data;
        if (!data || typeof data !== 'object') return;
        if (data.type !== 'vhr:purchase:result' && data.type !== 'vhr:balance:changed') return;
        // Только прямой родитель (страница платформы), не соседние фреймы/вкладки.
        if (event.source !== window.parent) return;
        if (!vhrSdkBridge.isAllowedOrigin(event.origin)) {
          if (window.console && console.warn) {
            console.warn('[VHR SDK] сообщение экономики ОТКЛОНЕНО с origin: ' + event.origin);
          }
          return;
        }

        if (data.type === 'vhr:balance:changed') {
          var nb = vhrEconomy.toBalance(data.balance);
          if (nb === null) return;
          var h = vhrEconomy.nextHandle++;
          vhrEconomy.payloads[h] = JSON.stringify({ status: 'balance', hasBalance: true, balance: nb });
          if (vhrEconomy.emit) vhrEconomy.emit(h, 2, 0, 0);
          return;
        }

        var requestId = typeof data.requestId === 'string' ? data.requestId : '';
        var p = vhrEconomy.pending[requestId];
        if (!p) return; // чужой или уже завершённый запрос
        if (typeof data.intentId === 'string' && data.intentId.length > 0 && data.intentId !== p.intentId) {
          if (window.console && console.warn) {
            console.warn('[VHR SDK] результат покупки с чужим intentId отклонён');
          }
          return;
        }

        var status = Object.prototype.hasOwnProperty.call(vhrEconomy.STATUS, data.status)
          ? vhrEconomy.STATUS[data.status] : vhrEconomy.STATUS.error;
        var bal = vhrEconomy.toBalance(data.balance);
        var pid = data.purchaseId;
        vhrEconomy.finish(requestId, status, 4, {
          status: typeof data.status === 'string' ? data.status : 'error',
          hasBalance: bal !== null,
          balance: bal === null ? 0 : bal,
          purchaseId: (typeof pid === 'string' || typeof pid === 'number') ? String(pid) : '',
          error: typeof data.error === 'string' ? data.error : (typeof data.reason === 'string' ? data.reason : '')
        });
      });
    } catch (e) {
      // Нет window (песочница) — Confirm сам ответит по таймауту.
    }
  },

  // 1 — игра во фрейме и (если браузер умеет location.ancestorOrigins) родитель
  // с origin платформы; 0 — точно не хост VHR. Та же логика, что VhrAds_IsHosted.
  VhrEconomy_IsHosted__deps: ['$vhrSdkBridge'],
  VhrEconomy_IsHosted: function () {
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

  // Просит хост показать окно подтверждения покупки. Возвращает handle (>0) или
  // 0, если отправить нельзя (нет родителя) — тогда колбэка не будет.
  // ВАЖНО: результат НИКОГДА не эмитится синхронно внутри этого вызова —
  // C# регистрирует обработчик handle уже после возврата.
  VhrEconomy_Confirm__deps: ['$vhrEconomy', '$vhrSdkBridge'],
  VhrEconomy_Confirm: function (intentIdPtr, requestIdPtr, timeoutMs) {
    var intentId = UTF8ToString(intentIdPtr);
    var requestId = UTF8ToString(requestIdPtr);
    try {
      if (!window.parent || window.parent === window) return 0;
    } catch (e) {
      return 0;
    }

    var handle = vhrEconomy.nextHandle++;

    if (vhrEconomy.pending[requestId]) {
      // Дубликат requestId — отвечаем асинхронно, не ломая исходный запрос.
      setTimeout(function () {
        vhrEconomy.payloads[handle] = JSON.stringify({ status: 'error', error: 'busy', intentId: intentId });
        if (vhrEconomy.emit) vhrEconomy.emit(handle, 1, vhrEconomy.STATUS.error, 3);
      }, 0);
      return handle;
    }

    var p = { handle: handle, intentId: intentId, timer: null };
    vhrEconomy.pending[requestId] = p;

    // Хост молчит (старая версия сайта / страница сломалась) → expired, игра не ждёт вечно.
    p.timer = setTimeout(function () {
      vhrEconomy.finish(requestId, vhrEconomy.STATUS.expired, 1, { status: 'expired', error: 'timeout' });
    }, timeoutMs > 0 ? timeoutMs : 180000);

    try {
      window.parent.postMessage({ type: 'vhr:purchase:confirm', intentId: intentId, requestId: requestId },
        vhrEconomy.parentTarget());
    } catch (e) {
      setTimeout(function () {
        vhrEconomy.finish(requestId, vhrEconomy.STATUS.error, 5, { status: 'error', error: 'post_failed' });
      }, 0);
    }
    return handle;
  },

  // Забирает (и удаляет) JSON с деталями события. Пустая строка, если нет.
  VhrEconomy_TakePayload__deps: ['$vhrEconomy'],
  VhrEconomy_TakePayload: function (handle) {
    var s = vhrEconomy.payloads[handle] || '';
    delete vhrEconomy.payloads[handle];
    var size = lengthBytesUTF8(s) + 1;
    var buffer = _malloc(size);
    stringToUTF8(s, buffer, size);
    return buffer;
  },

  // Просит сайт открыть окно пополнения счёта платформы. 1 — запрос отправлен,
  // 0 — родителя нет (игра открыта не во фрейме платформы).
  VhrEconomy_OpenTopUp__deps: ['$vhrEconomy', '$vhrSdkBridge'],
  VhrEconomy_OpenTopUp: function () {
    try {
      if (!window.parent || window.parent === window) return 0;
      window.parent.postMessage({ type: 'vhr:topup:open' }, vhrEconomy.parentTarget());
      return 1;
    } catch (e) {
      return 0;
    }
  }
};

mergeInto(LibraryManager.library, VhrEconomyBridgeLib);
