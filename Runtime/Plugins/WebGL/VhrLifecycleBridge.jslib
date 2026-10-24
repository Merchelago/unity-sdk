// VHR Games SDK — WebGL JS interop: жизненный цикл страницы (SDK 1.11.0+).
//
// Зачем: облачные сохранения (VhrSdk.Saves) копят автосохранения и отправляют их
// не чаще раза в N секунд. Когда игрок переключает вкладку, сворачивает браузер,
// уходит из приложения на телефоне или закрывает вкладку, накопленное нужно
// отправить сразу. Unity WebGL сообщает только о потере фокуса окна
// (Application.focusChanged), а фрейм без фокуса или мобильный браузер её не
// присылают — поэтому слушаем события страницы:
//   visibilitychange → document.visibilityState === 'hidden'  (код 1)
//   pagehide                                                  (код 2)
//
// В C# уходит только целое число через указатель на статический метод:
//   cb(code) — сигнатура 'vi'. Колбэк вызывается синхронно внутри DOM-события,
//   чтобы запрос успел уйти до того, как браузер остановит цикл кадров вкладки.
//
// Файл компилируется ТОЛЬКО для WebGL (папка Plugins/WebGL).
var VhrLifecycleBridgeLib = {

  $vhrLifecycle: {
    emit: null,        // function(code) — ставится в VhrLifecycle_Init
    listenerBound: false
  },

  // Идемпотентно: запоминает колбэк C# и вешает слушатели страницы.
  // __deps обязателен — иначе Emscripten выкинет $vhrLifecycle
  // (ReferenceError в рантайме, как было с vhrSdkBridge).
  VhrLifecycle_Init__deps: ['$vhrLifecycle'],
  VhrLifecycle_Init: function (cb) {
    // Колбэк держим в замыкании (тот же приём, что в VhrAdsBridge.jslib).
    vhrLifecycle.emit = function (code) {
      try {
        {{{ makeDynCall('vi', 'cb') }}}(code);
      } catch (e) {
        if (window.console && console.error) console.error('[VHR SDK] lifecycle callback failed', e);
      }
    };

    if (vhrLifecycle.listenerBound) return;
    vhrLifecycle.listenerBound = true;

    try {
      document.addEventListener('visibilitychange', function () {
        if (document.visibilityState === 'hidden' && vhrLifecycle.emit) vhrLifecycle.emit(1);
      });
      window.addEventListener('pagehide', function () {
        if (vhrLifecycle.emit) vhrLifecycle.emit(2);
      });
    } catch (e) {
      // Нет document/window (песочница) — остаётся Application.focusChanged.
    }
  }
};

mergeInto(LibraryManager.library, VhrLifecycleBridgeLib);
