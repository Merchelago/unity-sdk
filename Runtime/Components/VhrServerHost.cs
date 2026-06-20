using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Drop-in компонент для выделенного сервера: автоматически репортит число
    /// игроков платформе. Перетащите его на любой GameObject в серверной сцене —
    /// больше ничего настраивать не нужно; просто обновляйте
    /// <see cref="CurrentPlayers"/> (или задайте <see cref="PlayerCountProvider"/>).
    /// Порт/ключ/адрес платформа задаёт сама через env
    /// (<c>VHR_SERVER_PORT</c>, <c>VHR_INTERNAL_KEY</c>, <c>VHR_SERVERS_BASE_URL</c>,
    /// <c>VHR_GAME_ID</c>, <c>VHR_INSTANCE_ID</c>).
    /// </summary>
    /// <remarks>
    /// Работает <b>только</b> в серверной сборке: на <see cref="Start"/> проверяет
    /// <see cref="VhrServer.IsServerBuild"/> (true, когда задан
    /// <c>VHR_INSTANCE_ID</c>). В клиентских (WebGL) и редакторных сборках
    /// компонент тихо ничего не делает — его можно безопасно держать в общей сцене.
    /// <para>
    /// SDK-клиент собирается самодостаточно через
    /// <see cref="VhrSdk.CreateStandaloneServers"/> (без VContainer и без
    /// глобальной инициализации), сконфигурированный из env: серверный API из
    /// <see cref="VhrServer.ServersBaseUrl"/>, ключ из
    /// <see cref="VhrServer.InternalKey"/>, игра из <see cref="VhrServer.GameId"/>.
    /// Репорт идёт раз в <see cref="reportIntervalSeconds"/> секунд через
    /// <see cref="IVhrServers.ReportPlayersAsync"/> с
    /// <see cref="VhrServer.InstanceId"/>. Все ошибки заглушаются
    /// (<see cref="Debug.LogWarning"/>), чтобы не уронить игровой сервер.
    /// </para>
    /// </remarks>
    [AddComponentMenu("VHR/VHR Server Host (auto report players)")]
    [DisallowMultipleComponent]
    public sealed class VhrServerHost : MonoBehaviour
    {
        [Tooltip("Как часто (в секундах) сервер репортит число игроков платформе. " +
                 "По умолчанию 15.")]
        [SerializeField] private float reportIntervalSeconds = 15f;

        [Tooltip("Логировать запрос/ответ SDK (по умолчанию выключено).")]
        [SerializeField] private bool verboseLogging;

        /// <summary>
        /// Текущее число игроков на сервере. Обновляйте это поле из своей игровой
        /// логики (вход/выход игрока). Если задан <see cref="PlayerCountProvider"/>,
        /// он имеет приоритет и это поле игнорируется.
        /// </summary>
        public int CurrentPlayers;

        /// <summary>
        /// Необязательный поставщик числа игроков. Если задан — вызывается на каждый
        /// репорт <b>вместо</b> чтения <see cref="CurrentPlayers"/>. Удобно, когда
        /// число игроков уже хранит ваш NetworkManager / транспорт, например
        /// <c>host.PlayerCountProvider = () =&gt; NetworkManager.Singleton.ConnectedClients.Count;</c>.
        /// </summary>
        public Func<int> PlayerCountProvider;

        private IVhrServers _servers;
        private string _instanceId;
        private CancellationTokenSource _cts;

        private void Start()
        {
            // Запускаем цикл репорта только в серверной сборке (есть VHR_INSTANCE_ID).
            // В клиентских / редакторных сборках компонент — безопасный no-op.
            if (!VhrServer.IsServerBuild)
                return;

            try
            {
                _instanceId = VhrServer.InstanceId;

                // Собираем самодостаточный серверный клиент из env, той же цепочкой
                // объектов, что и обычная инициализация SDK. Никакого DI и общего
                // секрета в билде — ключ платформа инъектит в контейнер.
                var options = new VhrSdkOptions
                {
                    GameId = VhrServer.GameId,
                    InternalApiKey = VhrServer.InternalKey, // env VHR_INTERNAL_KEY
                    ServersBaseUrl = VhrServer.ServersBaseUrl, // env VHR_SERVERS_BASE_URL
                    // Серверу не нужны ни JWT игрока, ни ping bridge.
                    PingOnInitialize = false,
                    TokenProvider = () => null,
                    VerboseLogging = verboseLogging,
                };

                _servers = VhrSdk.CreateStandaloneServers(options);
            }
            catch (Exception e)
            {
                // Не валим сервер из-за конфигурации репорта — просто не репортим.
                Debug.LogWarning($"[VHR] VhrServerHost: не удалось инициализировать репорт игроков: {e.Message}");
                _servers = null;
                return;
            }

            _cts = new CancellationTokenSource();
            _ = ReportLoopAsync(_cts.Token);
        }

        private async Task ReportLoopAsync(CancellationToken ct)
        {
            // Минимальный разумный интервал, чтобы не зациклить нулевой задержкой.
            float interval = reportIntervalSeconds < 1f ? 1f : reportIntervalSeconds;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    int count = ResolvePlayerCount();
                    if (_servers != null && !string.IsNullOrEmpty(_instanceId))
                        await _servers.ReportPlayersAsync(_instanceId, count, ct);
                }
                catch (OperationCanceledException)
                {
                    // Нормальное завершение (компонент уничтожен / приложение закрыто).
                    return;
                }
                catch (Exception e)
                {
                    // Сеть/бэкенд недоступны — глушим, повторим на следующем тике.
                    Debug.LogWarning($"[VHR] VhrServerHost: репорт игроков не прошёл: {e.Message}");
                }

                try
                {
                    await Awaitable.WaitForSecondsAsync(interval, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>Берёт число игроков из провайдера (если задан) либо из поля; не &lt; 0.</summary>
        private int ResolvePlayerCount()
        {
            int count;
            var provider = PlayerCountProvider;
            if (provider != null)
            {
                try { count = provider.Invoke(); }
                catch (Exception e)
                {
                    Debug.LogWarning($"[VHR] VhrServerHost: PlayerCountProvider кинул исключение: {e.Message}");
                    count = CurrentPlayers;
                }
            }
            else
            {
                count = CurrentPlayers;
            }

            return count < 0 ? 0 : count;
        }

        private void OnDestroy()
        {
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
            }
            catch
            {
                // ignore
            }
            finally
            {
                _cts = null;
            }
        }
    }
}
