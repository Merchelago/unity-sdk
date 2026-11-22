namespace VhrGames.Sdk
{
    /// <summary>Канал доставки данных релея (см. <see cref="VhrRelay.Send(byte[], VhrChannel)"/>).</summary>
    public enum VhrChannel
    {
        /// <summary>
        /// Как в SDK до 1.15: WebRTC DataChannel, если он поднят (ненадёжно, быстро),
        /// иначе WebSocket (надёжно, по порядку). Поведение <c>Send(byte[])</c> по умолчанию.
        /// </summary>
        Auto = 0,
        /// <summary>
        /// Надёжно и по порядку — всегда WebSocket. Для событий, которые нельзя потерять:
        /// выстрел, смерть, чат, смена хода, конец раунда.
        /// </summary>
        Reliable = 1,
        /// <summary>
        /// Ненадёжно — DataChannel, если поднят, иначе WebSocket (на релее v2 такие кадры
        /// могут отбрасываться у отстающего получателя). Для частых снапшотов позиций:
        /// свежий снапшот важнее старого.
        /// </summary>
        Unreliable = 2,
    }

    /// <summary>
    /// Сетевая статистика соединения с релеем (<see cref="VhrRelay.Stats"/>), считается
    /// на клиенте и обновляется примерно раз в секунду.
    /// </summary>
    public sealed class VhrRelayStats
    {
        /// <summary>Отправлено байт в секунду (все кадры, оба транспорта).</summary>
        public double BytesSentPerSec;
        /// <summary>Получено байт в секунду.</summary>
        public double BytesReceivedPerSec;
        /// <summary>Отправлено сообщений в секунду (сообщение транспорта: пачка = одно сообщение).</summary>
        public double MessagesSentPerSec;
        /// <summary>Получено сообщений в секунду.</summary>
        public double MessagesReceivedPerSec;
        /// <summary>Всего отправлено байт за соединение.</summary>
        public long TotalBytesSent;
        /// <summary>Всего получено байт за соединение.</summary>
        public long TotalBytesReceived;
        /// <summary>Сглаженный RTT до релея, мс (0 — ещё не измерен или релей v1).</summary>
        public double RttMs;
        /// <summary>Последний замер RTT, мс.</summary>
        public double RttLastMs;
        /// <summary>Джиттер RTT, мс.</summary>
        public double JitterMs;
        /// <summary>Доля потерянных пингов за последние ~20 замеров, % (на DataChannel ≈ потери пакетов).</summary>
        public double PingLossPercent;
        /// <summary>Транспорт игровых данных: <c>"webrtc"</c> или <c>"ws"</c>.</summary>
        public string Transport = "ws";
        /// <summary>Версия протокола, согласованная с релеем: 2 или 1 (старый релей), 0 — не подключены.</summary>
        public int ProtocolVersion;
        /// <summary>Регион релея (может быть пустым).</summary>
        public string Region = string.Empty;
        /// <summary>Сколько раз соединение восстанавливалось после обрыва.</summary>
        public int Reconnects;

        internal VhrRelayStats Clone() => (VhrRelayStats)MemberwiseClone();
    }
}
