// VHR Games SDK — WebGL JS interop: сохранение локальных файлов SDK в IndexedDB (SDK 1.12.0+).
//
// Зачем: на WebGL Application.persistentDataPath лежит в IDBFS — файловой системе в памяти,
// которую Emscripten копирует в IndexedDB браузера только по FS.syncfs(false, cb). Без этого
// очередь отложенных операций (VhrSdk.Connection) и неотправленные сохранения (VhrSdk.Saves)
// пропали бы вместе с вкладкой. C# пишет файл и просит синхронизацию:
//   VhrStorage_Sync(0) — отложенно: одна синхронизация через ~1 с после серии записей;
//   VhrStorage_Sync(1) — сразу (страница скрывается/закрывается): в микрозадаче, то есть
//                        после ВСЕХ обработчиков текущего DOM-события — записи, сделанные
//                        ими синхронно, попадут в одну синхронизацию.
// Одновременно идёт не больше одной синхронизации; запрос во время неё повторяет её после.
//
// Файл компилируется ТОЛЬКО для WebGL (папка Plugins/WebGL).
var VhrStorageBridgeLib = {

  $vhrStorage: {
    busy: false,      // идёт FS.syncfs
    again: false,     // за время синхронизации были новые записи — повторить
    timer: 0,         // отложенная синхронизация (setTimeout)
    micro: false,     // синхронизация «сразу» уже запланирована микрозадачей
    warned: false,

    run: function () {
      if (vhrStorage.timer) {
        clearTimeout(vhrStorage.timer);
        vhrStorage.timer = 0;
      }
      if (vhrStorage.busy) {
        vhrStorage.again = true;
        return;
      }
      if (typeof FS === 'undefined' || !FS || typeof FS.syncfs !== 'function') return;
      vhrStorage.busy = true;
      try {
        FS.syncfs(false, function (err) {
          vhrStorage.busy = false;
          if (err && !vhrStorage.warned && window.console && console.warn) {
            vhrStorage.warned = true;
            console.warn('[VHR SDK] не удалось сохранить файлы SDK в IndexedDB', err);
          }
          if (vhrStorage.again) {
            vhrStorage.again = false;
            vhrStorage.run();
          }
        });
      } catch (e) {
        vhrStorage.busy = false;
        if (!vhrStorage.warned && window.console && console.warn) {
          vhrStorage.warned = true;
          console.warn('[VHR SDK] FS.syncfs недоступен', e);
        }
      }
    }
  },

  // __deps обязателен — иначе Emscripten выкинет $vhrStorage (см. VhrLifecycleBridge.jslib).
  VhrStorage_Sync__deps: ['$vhrStorage'],
  VhrStorage_Sync: function (immediate) {
    if (immediate) {
      if (vhrStorage.micro) return;
      if (typeof Promise === 'undefined') {
        vhrStorage.run();
        return;
      }
      vhrStorage.micro = true;
      Promise.resolve().then(function () {
        vhrStorage.micro = false;
        vhrStorage.run();
      });
      return;
    }
    if (!vhrStorage.timer) {
      vhrStorage.timer = setTimeout(function () {
        vhrStorage.timer = 0;
        vhrStorage.run();
      }, 1000);
    }
  }
};

mergeInto(LibraryManager.library, VhrStorageBridgeLib);
