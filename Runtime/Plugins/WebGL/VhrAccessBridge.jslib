// VHR Games SDK — WebGL JS interop: доступ к премиум-игре через страницу-хост VHR (SDK 1.13.0+).
//
// Премиум-статус игры и доступ игрока (полный / демо / нет) знает сервер; страница платформы
// (https://vhrgames.ru) сообщает их игре и сама показывает окно покупки полной версии
// («Купить за N монет» / «Оформить Premium» / для ребёнка «Попросить у родителя»).
// Игра (этот iframe) платформенный JWT не получает никогда.
//
// Контракт postMessage:
//   iframe → родитель: { type: 'vhr:access:get', requestId }
//   родитель → iframe: { type: 'vhr:access:state', requestId, state }
//   родитель → iframe: { type: 'vhr:access:changed', state }          — без запроса, при смене доступа
//   iframe → родитель: { type: 'vhr:access:purchase', requestId }
//   родитель → iframe: { type: 'vhr:access:purchase-result', requestId,
//                        result: 'purchased'|'subscribed'|'requested'|'cancelled'|'unavailable', state }
//   state = { gameType: 'regular'|'premium', access: 'full'|'demo'|'none',
//             source: 'free'|'purchase'|'subscription'|'grandfather'|'demo'|'none',
//             price: int|null, inSubscription: bool, hasDemo: bool }
// Ответы принимаются ТОЛЬКО от window.parent и только с origin платформы
// (vhrSdkBridge.isAllowedOrigin из VhrSdkBridge.jslib — один список на все каналы).
//
// Гарантия «игра не зависнет»: на каждый get/purchase C# получает ровно один ответ — от хоста
// или по таймауту (get — несколько секунд: старая версия сайта без премиум-игр молчит;
// purchase — страховочный лимит на окно покупки).
//
// В C# уходят только целые числа через указатель на статический метод:
//   cb(handle, eventCode, status, reason) — сигнатура 'viiii'.
//   eventCode: 1 = состояние (ответ на get), 2 = результат покупки, 3 = доступ изменился (changed).
//   status: для 1 и 3 — 1 состояние есть, 0 нет; для 2 — значение VhrAccessPurchaseStatus:
//           0 unavailable, 1 purchased, 2 subscribed, 3 requested, 4 cancelled.
//   reason: 0 none, 1 timeout, 2 no_host, 3 busy, 4 host_reported, 5 post_failed.
// Состояние C# забирает строкой JSON (уже нормализованной здесь) через VhrAccess_TakePayload(handle)
// — тем же способом возврата строки, что и VhrSdk_GetLatestToken.
//
// Файл компилируется ТОЛЬКО для WebGL (папка Plugins/WebGL).
var VhrAccessBridgeLib = {

  $vhrAccess: {
    emit: null,          // function(handle, eventCode, status, reason) — ставится в VhrAccess_Init
    listenerBound: false,
    nextHandle: 1,
    pending: {},         // requestId -> { handle, kind: 'get'|'purchase', timer }
    payloads: {},        // handle -> JSON-строка с деталями для C#
    RESULT: { unavailable: 0, purchased: 1, subscribed: 2, requested: 3, cancelled: 4, canceled: 4 },

    str: function (v) {
      return typeof v === 'string' ? v.trim().toLowerCase() : '';
    },

    // Состояние хоста → плоский объект с полями без null (JsonUtility в C# не умеет int?).
    normState: function (s) {
      if (!s || typeof s !== 'object') return { hasState: false };
      var gameType = vhrAccess.str(s.gameType);
      var access = vhrAccess.str(s.access);
      if (!gameType && !access) return { hasState: false };
      var price = s.price;
      if (price === undefined || price === null) price = s.priceCoins;
      if (typeof price === 'string' && /^\d+$/.test(price)) price = Number(price);
      var hasPrice = typeof price === 'number' && isFinite(price) && price > 0 && price <= 2147483647;
      return {
        hasState: true,
        gameType: gameType,
        access: access,
        source: vhrAccess.str(s.source),
        hasPrice: hasPrice,
        price: hasPrice ? Math.floor(price) : 0,
        inSubscription: s.inSubscription === true,
        hasDemo: s.hasDemo === true
      };
    },

    // Завершает запрос ровно один раз: чистит таймер, кладёт детали и шлёт ответ в C#.
    finish: function (requestId, status, reason, detail) {
      var p = vhrAccess.pending[requestId];
      if (!p) return;
      delete vhrAccess.pending[requestId];
      if (p.timer) clearTimeout(p.timer);
      vhrAccess.payloads[p.handle] = JSON.stringify(detail || { hasState: false });
      if (vhrAccess.emit) vhrAccess.emit(p.handle, p.kind === 'purchase' ? 2 : 1, status, reason);
    },

    // Общая отправка get/purchase. Возвращает handle (>0) или 0 — нет родителя.
    // Результат НИКОГДА не эмитится синхронно: C# регистрирует обработчик уже после возврата.
    send: function (kind, requestId, timeoutMs) {
      try {
        if (!window.parent || window.parent === window) return 0;
      } catch (e) {
        return 0;
      }

      var handle = vhrAccess.nextHandle++;
      var eventCode = kind === 'purchase' ? 2 : 1;

      if (vhrAccess.pending[requestId]) {
        // Дубликат requestId — отвечаем асинхронно, не ломая исходный запрос.
        setTimeout(function () {
          vhrAccess.payloads[handle] = JSON.stringify({ hasState: false, result: 'unavailable', error: 'busy' });
          if (vhrAccess.emit) vhrAccess.emit(handle, eventCode, 0, 3);
        }, 0);
        return handle;
      }

      var p = { handle: handle, kind: kind, timer: null };
      vhrAccess.pending[requestId] = p;

      // Хост молчит (старая версия сайта / страница сломалась) → ответ по таймауту.
      p.timer = setTimeout(function () {
        vhrAccess.finish(requestId, 0, 1, { hasState: false, result: 'unavailable', error: 'timeout' });
      }, timeoutMs > 0 ? timeoutMs : (kind === 'purchase' ? 900000 : 3000));

      try {
        // Запрос секрета не содержит — targetOrigin "*", как у vhr:ads:show / vhr:sdk:token-request:
        // родитель на любом домене платформы получит его, а его ответ проверяется по origin.
        window.parent.postMessage({ type: kind === 'purchase' ? 'vhr:access:purchase' : 'vhr:access:get', requestId: requestId }, '*');
      } catch (e) {
        setTimeout(function () {
          vhrAccess.finish(requestId, 0, 5, { hasState: false, result: 'unavailable', error: 'post_failed' });
        }, 0);
      }
      return handle;
    }
  },

  // Идемпотентно: запоминает колбэк C# и вешает слушатель сообщений хоста.
  // __deps обязателен — иначе Emscripten выкинет $vhrAccess/$vhrSdkBridge.
  VhrAccess_Init__deps: ['$vhrAccess', '$vhrSdkBridge'],
  VhrAccess_Init: function (cb) {
    vhrAccess.emit = function (handle, eventCode, status, reason) {
      try {
        {{{ makeDynCall('viiii', 'cb') }}}(handle, eventCode, status, reason);
      } catch (e) {
        if (window.console && console.error) console.error('[VHR SDK] access callback failed', e);
      }
    };

    if (vhrAccess.listenerBound) return;
    vhrAccess.listenerBound = true;

    try {
      window.addEventListener('message', function (event) {
        var data = event.data;
        if (!data || typeof data !== 'object') return;
        if (data.type !== 'vhr:access:state' && data.type !== 'vhr:access:changed' &&
            data.type !== 'vhr:access:purchase-result') return;
        // Только прямой родитель (страница платформы), не соседние фреймы/вкладки.
        if (event.source !== window.parent) return;
        if (!vhrSdkBridge.isAllowedOrigin(event.origin)) {
          if (window.console && console.warn) {
            console.warn('[VHR SDK] сообщение доступа ОТКЛОНЕНО с origin: ' + event.origin);
          }
          return;
        }

        if (data.type === 'vhr:access:changed') {
          var cs = vhrAccess.normState(data.state);
          if (!cs.hasState) return;
          var h = vhrAccess.nextHandle++;
          vhrAccess.payloads[h] = JSON.stringify(cs);
          if (vhrAccess.emit) vhrAccess.emit(h, 3, 1, 4);
          return;
        }

        var requestId = typeof data.requestId === 'string' ? data.requestId : '';
        var p = vhrAccess.pending[requestId];
        if (!p) return; // чужой или уже завершённый запрос
        var wantKind = data.type === 'vhr:access:purchase-result' ? 'purchase' : 'get';
        if (p.kind !== wantKind) return; // ответ не того типа — ждём свой

        var d = vhrAccess.normState(data.state);
        if (wantKind === 'get') {
          vhrAccess.finish(requestId, d.hasState ? 1 : 0, 4, d);
          return;
        }

        var r = vhrAccess.str(data.result);
        d.result = r || 'unavailable';
        d.error = typeof data.error === 'string' ? data.error : (typeof data.reason === 'string' ? data.reason : '');
        var status = Object.prototype.hasOwnProperty.call(vhrAccess.RESULT, r) ? vhrAccess.RESULT[r] : 0;
        vhrAccess.finish(requestId, status, 4, d);
      });
    } catch (e) {
      // Нет window (песочница) — Get/Purchase сами ответят по таймауту.
    }
  },

  // 1 — игра во фрейме и (если браузер умеет location.ancestorOrigins) родитель
  // с origin платформы; 0 — точно не хост VHR. Та же логика, что VhrAds_IsHosted.
  VhrAccess_IsHosted__deps: ['$vhrSdkBridge'],
  VhrAccess_IsHosted: function () {
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

  // Спрашивает у хоста состояние доступа. Handle (>0) или 0 — нет родителя.
  VhrAccess_Get__deps: ['$vhrAccess'],
  VhrAccess_Get: function (requestIdPtr, timeoutMs) {
    return vhrAccess.send('get', UTF8ToString(requestIdPtr), timeoutMs);
  },

  // Просит хост открыть окно покупки полной версии. Handle (>0) или 0 — нет родителя.
  VhrAccess_Purchase__deps: ['$vhrAccess'],
  VhrAccess_Purchase: function (requestIdPtr, timeoutMs) {
    return vhrAccess.send('purchase', UTF8ToString(requestIdPtr), timeoutMs);
  },

  // Забирает (и удаляет) JSON с деталями события. Пустая строка, если нет.
  VhrAccess_TakePayload__deps: ['$vhrAccess'],
  VhrAccess_TakePayload: function (handle) {
    var s = vhrAccess.payloads[handle] || '';
    delete vhrAccess.payloads[handle];
    var size = lengthBytesUTF8(s) + 1;
    var buffer = _malloc(size);
    stringToUTF8(s, buffer, size);
    return buffer;
  }
};

mergeInto(LibraryManager.library, VhrAccessBridgeLib);
