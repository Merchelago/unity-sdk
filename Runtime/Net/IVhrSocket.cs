using System;
using System.Threading;
using System.Threading.Tasks;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Минимальная абстракция WebSocket-транспорта с двумя реализациями за одним
    /// интерфейсом: <see cref="NativeVhrSocket"/> (нативные / редакторные сборки,
    /// на базе <c>System.Net.WebSockets.ClientWebSocket</c>) и
    /// <see cref="WebGLVhrSocket"/> (WebGL, на базе браузерного <c>WebSocket</c>
    /// через <c>VhrWebSocket.jslib</c>). Высокоуровневый клиент
    /// <see cref="VhrRelay"/> работает только через этот интерфейс и не знает,
    /// какой бэкенд под ним.
    /// </summary>
    /// <remarks>
    /// Протокол — <b>бинарный</b> (<c>arraybuffer</c> в WebGL,
    /// <c>WebSocketMessageType.Binary</c> нативно). Все события могут приходить
    /// не на главном Unity-потоке (нативный бэкенд читает из фоновой задачи) —
    /// см. реализации; <see cref="VhrRelay"/> при необходимости маршалит их на
    /// главный поток. В WebGL всё и так на главном потоке.
    /// </remarks>
    public interface IVhrSocket : IDisposable
    {
        /// <summary>Открыт ли сокет в данный момент.</summary>
        bool IsOpen { get; }

        /// <summary>Сокет успешно открылся (рукопожатие завершено).</summary>
        event Action OnOpen;

        /// <summary>Пришло бинарное сообщение. Массив принадлежит вызывающему —
        /// копируйте, если нужно сохранить дольше обработчика.</summary>
        event Action<byte[]> OnMessage;

        /// <summary>Сокет закрылся (нормально или из-за ошибки). Аргумент —
        /// человекочитаемая причина (может быть <c>null</c>).</summary>
        event Action<string> OnClose;

        /// <summary>
        /// Подключается к <paramref name="url"/> (<c>ws://</c> или <c>wss://</c>).
        /// Завершается, когда соединение открыто; событие <see cref="OnOpen"/>
        /// также вызывается. Бросает при ошибке подключения (нативно); в WebGL
        /// ошибка приходит через <see cref="OnClose"/>.
        /// </summary>
        Task ConnectAsync(string url, CancellationToken ct = default);

        /// <summary>Шлёт бинарный фрейм. No-op, если сокет не открыт.</summary>
        void Send(byte[] data);

        /// <summary>Закрывает сокет. Безопасно вызывать повторно.</summary>
        Task CloseAsync();
    }
}
