using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Облачные сохранения игрока (SDK 1.11.0+). Прогресс хранится на сервере VHR:
    /// переживает очистку браузера, переносится между устройствами, другие игры его
    /// не видят. Backed by <c>{BridgeBaseUrl}/api/saves</c> (GameBridgeMS);
    /// авторизация — игровой токен игрока (в Unity Editor Live — песочный ключ), игрок
    /// и игра берутся из токена, <c>userId</c> передавать не нужно.
    /// <para>
    /// <b>Слоты.</b> Сохранение — строка (обычно JSON) в именованном слоте: имя
    /// <c>[a-z0-9_.-]</c>, 1–64 символа (SDK приводит его к нижнему регистру), по
    /// умолчанию <c>"default"</c>. У каждого слота есть версия: 1 при создании, +1 на
    /// каждую запись — по ней SDK замечает, что слот уже перезаписали с другого
    /// устройства или вкладки (<see cref="VhrSaveError.Conflict"/>, <see cref="OnConflict"/>).
    /// </para>
    /// <para>
    /// <b>Лимиты платформы:</b> слот — до 256 КБ после сжатия и до 1 МБ до сжатия;
    /// до 10 слотов на игрока в игре; 12 записей/удалений и 60 чтений в минуту на игрока
    /// в игре. Объём всех сохранений игры ограничен квотой: 50 МБ бесплатно, сверх —
    /// платные пакеты, которые разработчик покупает в кабинете (игра → Облачные
    /// сохранения). Когда квота исчерпана, запись, увеличивающая объём, получает
    /// <see cref="VhrSaveError.QuotaExceeded"/>; уменьшающая или равная — проходит.
    /// </para>
    /// <para>
    /// <b>Как пользоваться:</b> при старте — <see cref="LoadAsync"/> /
    /// <see cref="LoadJsonAsync{T}"/>; по ходу игры — <see cref="QueueSave"/> /
    /// <see cref="QueueSaveJson{T}"/> (SDK сам отправит не чаще раза в
    /// <see cref="VhrSdkOptions.SavesMinIntervalSeconds"/> на слот и сразу — при потере
    /// фокуса, сворачивании, закрытии вкладки, выходе); в важных точках (конец уровня,
    /// выход в меню) — <see cref="FlushAsync"/>. <see cref="SaveAsync"/> — для разовых
    /// явных записей, не каждый кадр.
    /// </para>
    /// <para>
    /// Методы не бросают исключений из-за сети или сервера (кроме
    /// <see cref="ListAsync"/> и <see cref="OperationCanceledException"/> при отмене
    /// <c>ct</c>): итог — в <see cref="VhrSaveLoadResult"/> / <see cref="VhrSaveWriteResult"/>.
    /// Вызывать с главного потока.
    /// </para>
    /// </summary>
    public interface IVhrSaves
    {
        /// <summary>
        /// Автосохранение слота упёрлось в конфликт версий (<c>409</c>): в облаке слот
        /// уже перезаписан с другого устройства или вкладки. Аргументы — слот и версия
        /// на сервере (0 — сохранения на сервере нет). Автосохранение этого слота
        /// приостанавливается, данные придерживаются. Решите, чья версия главнее:
        /// <list type="bullet">
        /// <item><see cref="LoadAsync"/> — взять облачную (придержанное автосохранение
        /// отбрасывается), применить или слить со своей и снова <see cref="QueueSave"/>;</item>
        /// <item><see cref="SaveAsync"/> с <c>expectedVersion</c> = версии сервера (или
        /// <c>null</c>) — перезаписать облако своими данными.</item>
        /// </list>
        /// На главном потоке. Без подписчиков SDK пишет предупреждение в консоль и
        /// облачные данные не трогает.
        /// </summary>
        event Action<string, long> OnConflict;

        /// <summary>
        /// Итог каждой отправки автосохранения (<see cref="QueueSave"/> /
        /// <see cref="FlushAsync"/>): слот и результат — успех (<see cref="VhrSaveWriteResult.Ok"/>)
        /// или ошибка. Удобно для значка «Сохранено в облаке» и для реакции на
        /// <see cref="VhrSaveError.QuotaExceeded"/> / <see cref="VhrSaveError.SlotTooLarge"/>.
        /// Временные сбои (сеть, <c>429</c>) SDK повторяет сам. На главном потоке.
        /// </summary>
        event Action<string, VhrSaveWriteResult> OnAutoSaveResult;

        /// <summary>
        /// Есть ли не отправленные автосохранения или записи «в полёте». Пока
        /// <c>true</c>, при выходе в меню стоит дождаться <see cref="FlushAsync"/>.
        /// </summary>
        bool HasPendingSaves { get; }

        /// <summary>
        /// Лимиты слота, которые сообщил сервер (<see cref="ListAsync"/>, ошибки
        /// <c>413</c>/<c>slots_limit</c>); до первого ответа — значения платформы по
        /// умолчанию (256 КБ, 10 слотов, 1024 КБ).
        /// </summary>
        VhrSaveLimits Limits { get; }

        /// <summary>
        /// <c>GET /api/saves/{slot}</c> — загрузить слот. Есть сохранение —
        /// <see cref="VhrSaveLoadResult.Found"/> = <c>true</c> и <see cref="VhrSaveLoadResult.Data"/>;
        /// сохранения нет (первый запуск) — <c>Found = false</c>, <c>Error = NotFound</c>;
        /// иначе — ошибка (сеть, нет токена…): прогресс в облаке может быть, не начинайте
        /// игру «с нуля» молча. Загрузка запоминает версию слота — автосохранение будет
        /// писать поверх именно неё и заметит чужую запись (<see cref="OnConflict"/>).
        /// </summary>
        Task<VhrSaveLoadResult> LoadAsync(string slot = "default", CancellationToken ct = default);

        /// <summary>
        /// <see cref="LoadAsync"/> + <c>JsonUtility.FromJson&lt;T&gt;</c>. Нет сохранения,
        /// ошибка или JSON не разобрался — вернёт <paramref name="fallback"/> (ошибку
        /// пишет в консоль). Если нужно отличить «нет сохранения» от «нет связи» —
        /// используйте <see cref="LoadAsync"/>. <typeparamref name="T"/> — <c>[Serializable]</c>
        /// класс или структура (правила <c>JsonUtility</c>: без словарей и свойств).
        /// </summary>
        Task<T> LoadJsonAsync<T>(string slot = "default", T fallback = default, CancellationToken ct = default);

        /// <summary>
        /// <c>PUT /api/saves/{slot}</c> — записать слот сразу.
        /// <paramref name="expectedVersion"/>: <c>null</c> — перезаписать как есть
        /// (last-write-wins); <c>0</c> — только создать (если слот уже есть — <c>Conflict</c>);
        /// <c>N</c> — записать, только если на сервере сейчас версия <c>N</c>. При
        /// <see cref="VhrSaveError.Conflict"/> в результате — <see cref="VhrSaveWriteResult.ServerVersion"/>,
        /// при <see cref="VhrSaveError.RateLimited"/> — <see cref="VhrSaveWriteResult.RetryAfterSeconds"/>.
        /// Не вызывайте каждый кадр (лимит — 12 записей в минуту): для этого есть <see cref="QueueSave"/>.
        /// Автосохранения слота, накопленные до вызова, после успешной записи отбрасываются.
        /// </summary>
        Task<VhrSaveWriteResult> SaveAsync(string slot, string data, long? expectedVersion = null, CancellationToken ct = default);

        /// <summary><see cref="SaveAsync"/> для объекта: <c>JsonUtility.ToJson(value)</c>.</summary>
        Task<VhrSaveWriteResult> SaveJsonAsync<T>(string slot, T value, long? expectedVersion = null, CancellationToken ct = default);

        /// <summary>
        /// <c>DELETE /api/saves/{slot}</c> — удалить слот (идемпотентно: удалять
        /// несуществующий — тоже успех) и освободить место в квоте. <c>true</c> — слота
        /// на сервере больше нет; <c>false</c> — ошибка (пишется в консоль).
        /// </summary>
        Task<bool> DeleteAsync(string slot, CancellationToken ct = default);

        /// <summary>
        /// <c>GET /api/saves</c> — слоты игрока в этой игре (без данных): имя, версия,
        /// размер, время записи. Заодно обновляет <see cref="Limits"/>. При ошибке
        /// бросает <see cref="VhrSdkException"/> (<see cref="VhrSdkException.Code"/> /
        /// <see cref="VhrSdkException.ServerCode"/>) — пустой список значит «слотов нет».
        /// </summary>
        Task<IReadOnlyList<VhrSaveSlotInfo>> ListAsync(CancellationToken ct = default);

        /// <summary>
        /// Автосохранение: запомнить последнее состояние слота и отправить его в фоне —
        /// не чаще раза в <see cref="VhrSdkOptions.SavesMinIntervalSeconds"/> (по
        /// умолчанию 10 с, минимум 5) на слот; промежуточные вызовы склеиваются, уходит
        /// последнее значение. Дёшево — можно звать на каждое изменение прогресса.
        /// SDK отправляет накопленное сразу при потере фокуса, сворачивании/скрытии
        /// вкладки, закрытии страницы и выходе; после <c>429</c> повторяет через
        /// <c>Retry-After</c>, при обрыве связи — с нарастающей паузой (данные живут в
        /// памяти до успешной отправки). Если слот загружали (<see cref="LoadAsync"/>),
        /// запись идёт с проверкой версии; чужая запись → <see cref="OnConflict"/>.
        /// </summary>
        void QueueSave(string slot, string data);

        /// <summary><see cref="QueueSave"/> для объекта: <c>JsonUtility.ToJson(value)</c> сразу при вызове.</summary>
        void QueueSaveJson<T>(string slot, T value);

        /// <summary>
        /// Отправить все накопленные автосохранения сейчас, не дожидаясь интервала, и
        /// дождаться записей «в полёте». Не бросает исключений; итог по слотам — в
        /// <see cref="OnAutoSaveResult"/>. Если сервер ответил <c>429</c>, ждёт
        /// <c>Retry-After</c> и повторяет (до 3 попыток); при обрыве связи данные
        /// остаются в очереди автосохранения. Слоты в конфликте не отправляются.
        /// </summary>
        Task FlushAsync();
    }

    /// <summary>Причина неудачи операции с облачными сохранениями (<see cref="IVhrSaves"/>).</summary>
    public enum VhrSaveError
    {
        /// <summary>Ошибки нет.</summary>
        None = 0,

        /// <summary>
        /// Сохранения в слоте нет (<c>404 save_not_found</c>) — обычно первый запуск:
        /// начинайте новую игру.
        /// </summary>
        NotFound = 1,

        /// <summary>
        /// Версия не совпала (<c>409 version_conflict</c>): слот уже перезаписан с другого
        /// устройства/вкладки. Версия сервера — <see cref="VhrSaveWriteResult.ServerVersion"/>.
        /// </summary>
        Conflict = 2,

        /// <summary>
        /// Квота облачных сохранений игры исчерпана (<c>403 storage_quota_exceeded</c>).
        /// Запись, уменьшающая объём, пройдёт. Увеличить квоту может разработчик в кабинете
        /// (игра → Облачные сохранения, платные пакеты); игроку — «облако временно недоступно».
        /// </summary>
        QuotaExceeded = 3,

        /// <summary>
        /// Слот больше лимита (<c>413 slot_too_large</c>): больше <see cref="VhrSaveLimits.maxSlotKb"/>
        /// после сжатия или больше 1 МБ до сжатия. Храните в облаке только прогресс.
        /// </summary>
        SlotTooLarge = 4,

        /// <summary>У игрока уже максимум слотов в игре (<c>403 slots_limit</c>, по умолчанию 10).</summary>
        SlotsLimit = 5,

        /// <summary>
        /// Слишком частые запросы (<c>429 rate_limited</c>; 12 записей и 60 чтений в минуту).
        /// Пауза — <see cref="VhrSaveWriteResult.RetryAfterSeconds"/>. Автосохранение повторяет само.
        /// </summary>
        RateLimited = 6,

        /// <summary>
        /// Нет действующего игрового токена (гость, игра открыта не с vhrgames.ru, токен
        /// не игровой или от другой игры): <c>401</c>/<c>403</c>. В Editor — нет/неверный песочный ключ.
        /// </summary>
        NotAuthenticated = 7,

        /// <summary>
        /// Облачные сохранения недоступны: выключены платформой (<c>503 saves_disabled</c>) или
        /// игра не найдена / не опубликована (<c>404 game_not_found</c>; у реальных игроков —
        /// только активные игры).
        /// </summary>
        Disabled = 8,

        /// <summary>Нет связи с сервером (таймаут, офлайн).</summary>
        Network = 9,

        /// <summary>
        /// Неверное имя слота или тело запроса (<c>400 bad_slot</c> / <c>bad_request</c>), в
        /// том числе отклонено SDK до запроса.
        /// </summary>
        BadSlot = 10,

        /// <summary>
        /// Прочее: ошибка сервера (5xx), требование обновить SDK (<c>426</c>), неожиданный
        /// ответ. Подробности — <see cref="VhrSaveWriteResult.ErrorCode"/> / <c>Message</c>.
        /// </summary>
        Unknown = 11
    }

    /// <summary>Итог <see cref="IVhrSaves.LoadAsync"/>.</summary>
    public sealed class VhrSaveLoadResult
    {
        /// <summary>Слот (в нижнем регистре).</summary>
        public string Slot { get; set; }

        /// <summary><c>true</c> — сохранение есть, данные в <see cref="Data"/>.</summary>
        public bool Found { get; set; }

        /// <summary>Строка сохранения (то, что записали). Пусто, если <see cref="Found"/> = <c>false</c>.</summary>
        public string Data { get; set; }

        /// <summary>Версия слота на сервере (1, 2, …). 0 — сохранения нет.</summary>
        public long Version { get; set; }

        /// <summary>Время последней записи (UTC, ISO-8601), если сохранение есть.</summary>
        public string UpdatedAt { get; set; }

        /// <summary>
        /// <see cref="VhrSaveError.None"/> — загружено; <see cref="VhrSaveError.NotFound"/> —
        /// сохранения нет (новая игра); прочее — ошибка (облачный прогресс может быть, но
        /// прочитать его не удалось).
        /// </summary>
        public VhrSaveError Error { get; set; }

        /// <summary>Машиночитаемый код (<c>save_not_found</c>, <c>unauthorized</c>, <c>connection_error</c>…). Пусто при успехе.</summary>
        public string ErrorCode { get; set; }

        /// <summary>Пояснение по-русски — для логов, не для игрока.</summary>
        public string Message { get; set; }

        internal static VhrSaveLoadResult Fail(string slot, VhrSaveError error, string code, string message)
            => new VhrSaveLoadResult { Slot = slot, Error = error, ErrorCode = code, Message = message };

        /// <inheritdoc />
        public override string ToString() => Found
            ? $"VhrSaveLoadResult('{Slot}': v{Version}, {Data?.Length ?? 0} симв.)"
            : $"VhrSaveLoadResult('{Slot}': {Error}{(string.IsNullOrEmpty(ErrorCode) ? "" : ", " + ErrorCode)})";
    }

    /// <summary>Итог записи или удаления слота (<see cref="IVhrSaves.SaveAsync"/>, автосохранение).</summary>
    public sealed class VhrSaveWriteResult
    {
        /// <summary>Слот (в нижнем регистре).</summary>
        public string Slot { get; set; }

        /// <summary><c>true</c> — записано (<see cref="Error"/> = <see cref="VhrSaveError.None"/>).</summary>
        public bool Ok => Error == VhrSaveError.None;

        /// <summary>Новая версия слота после записи (при успехе).</summary>
        public long Version { get; set; }

        /// <summary>Время записи на сервере (UTC, ISO-8601), при успехе.</summary>
        public string UpdatedAt { get; set; }

        /// <summary>Размер данных до сжатия, байт UTF-8.</summary>
        public int RawSize { get; set; }

        /// <summary>Размер после сжатия на сервере, байт — именно он идёт в лимит слота и квоту игры.</summary>
        public int StoredSize { get; set; }

        /// <summary>Причина неудачи; <see cref="VhrSaveError.None"/> при успехе.</summary>
        public VhrSaveError Error { get; set; }

        /// <summary>
        /// При <see cref="VhrSaveError.Conflict"/> — текущая версия слота на сервере
        /// (0 — сохранения на сервере нет). Иначе 0.
        /// </summary>
        public long ServerVersion { get; set; }

        /// <summary>При <see cref="VhrSaveError.Conflict"/> — время записи серверной версии, если сервер его прислал.</summary>
        public string ServerUpdatedAt { get; set; }

        /// <summary>При <see cref="VhrSaveError.RateLimited"/> — через сколько секунд повторять (≥ 1). Иначе 0.</summary>
        public int RetryAfterSeconds { get; set; }

        /// <summary>
        /// Машиночитаемый код: <c>version_conflict</c>, <c>storage_quota_exceeded</c>,
        /// <c>slot_too_large</c>, <c>slots_limit</c>, <c>rate_limited</c>, <c>saves_disabled</c>,
        /// <c>game_not_found</c>, <c>bad_slot</c>, <c>unauthorized</c>, <c>connection_error</c>…
        /// Пусто при успехе.
        /// </summary>
        public string ErrorCode { get; set; }

        /// <summary>Пояснение по-русски — для логов, не для игрока.</summary>
        public string Message { get; set; }

        internal static VhrSaveWriteResult Fail(string slot, VhrSaveError error, string code, string message)
            => new VhrSaveWriteResult { Slot = slot, Error = error, ErrorCode = code, Message = message };

        /// <inheritdoc />
        public override string ToString() => Ok
            ? $"VhrSaveWriteResult('{Slot}': v{Version}, {RawSize} Б → {StoredSize} Б)"
            : $"VhrSaveWriteResult('{Slot}': {Error}{(string.IsNullOrEmpty(ErrorCode) ? "" : ", " + ErrorCode)}" +
              (Error == VhrSaveError.Conflict ? $", на сервере v{ServerVersion}" : "") +
              (Error == VhrSaveError.RateLimited ? $", повтор через {RetryAfterSeconds} с" : "") + ")";
    }

    /// <summary>
    /// Слот в списке <see cref="IVhrSaves.ListAsync"/> (без данных). Поля — camelCase-JSON
    /// моста (<c>GET /api/saves</c>) для <c>JsonUtility</c>.
    /// </summary>
    [Serializable]
    public sealed class VhrSaveSlotInfo
    {
        /// <summary>Имя слота.</summary>
        public string slot;

        /// <summary>Версия слота (1, 2, …).</summary>
        public long version;

        /// <summary>Размер данных до сжатия, байт UTF-8.</summary>
        public int rawSize;

        /// <summary>Время последней записи (UTC, ISO-8601).</summary>
        public string updatedAt;
    }

    /// <summary>
    /// Лимиты слота (<see cref="IVhrSaves.Limits"/>; <c>limits</c> в ответе <c>GET /api/saves</c>).
    /// Значения по умолчанию — дефолты платформы.
    /// </summary>
    [Serializable]
    public sealed class VhrSaveLimits
    {
        /// <summary>Максимальный размер слота после сжатия, КБ (по умолчанию 256).</summary>
        public int maxSlotKb = 256;

        /// <summary>Максимум слотов на игрока в игре (по умолчанию 10).</summary>
        public int maxSlots = 10;

        /// <summary>Максимальный размер записи до сжатия, КБ (1024 — жёсткий лимит платформы).</summary>
        public int maxRawKb = 1024;

        internal VhrSaveLimits Clone() => new VhrSaveLimits { maxSlotKb = maxSlotKb, maxSlots = maxSlots, maxRawKb = maxRawKb };

        /// <inheritdoc />
        public override string ToString() => $"слот ≤ {maxSlotKb} КБ сжато / {maxRawKb} КБ сырых, слотов ≤ {maxSlots}";
    }

    /// <summary>
    /// Реализация <see cref="IVhrSaves"/>: HTTP к мосту через <see cref="VhrApiClient"/>
    /// (игровой токен и <c>X-Vhr-Game-Id</c> он подставляет сам), очередь
    /// автосохранения и flush по жизненному циклу. В Unity Editor в режиме Simulation —
    /// локальное хранилище с лимитами сервера (<c>Library/VhrSdk/SimSaves</c>).
    /// </summary>
    /// <remarks>
    /// Один общий экземпляр на процесс (<see cref="GetOrCreate"/>): статический фасад
    /// <see cref="VhrSdk.Saves"/> и VContainer получают один и тот же объект — одна
    /// очередь автосохранения и одни подписки на жизненный цикл.
    /// <para>
    /// WebGL: нет тредпула и таймерного потока — все ожидания идут через
    /// <see cref="VhrAsync"/> (кадры + реальное время), без <c>ConfigureAwait(false)</c>;
    /// операции одного слота выполняются строго по очереди.
    /// </para>
    /// </remarks>
    public sealed class VhrSavesService : IVhrSaves, IDisposable
    {
        /// <summary>Слот по умолчанию.</summary>
        public const string DefaultSlot = "default";

        /// <summary>Жёсткий лимит записи до сжатия: 1 МБ UTF-8.</summary>
        public const int MaxRawBytes = 1024 * 1024;

        /// <summary>Лимит сервера: записей/удалений в минуту на игрока в игре (все слоты вместе).</summary>
        internal const int WriteLimitPerMinute = 12;

        /// <summary>Лимит сервера: чтений в минуту на игрока в игре.</summary>
        internal const int ReadLimitPerMinute = 60;

        // Автосохранение держится ниже серверного лимита — запас под явные SaveAsync/DeleteAsync.
        private const int AutoWriteBudgetPerMinute = 10;
        // 429 без Retry-After в заголовке и теле — ждём минуту (окно серверного лимита).
        internal const int DefaultRetryAfterSeconds = 60;
        private const int MaxRetryAfterSeconds = 60;
        private const float FirstNetworkBackoffSeconds = 5f;
        private const float MaxNetworkBackoffSeconds = 60f;
        private const float NoTokenRetrySeconds = 30f;
        private const int FlushAttempts = 3;

        private static VhrSavesService _shared;

        private readonly VhrApiClient _api;
        private readonly VhrSdkOptions _options;
        private readonly IVhrLog _log;

        private sealed class PendingSave
        {
            public string Data;
            public long Seq;
        }

        // Очередь автосохранения: слот → последнее состояние.
        private readonly Dictionary<string, PendingSave> _pending = new Dictionary<string, PendingSave>(StringComparer.Ordinal);
        // Когда последний раз отправляли автосохранение слота (реальное время).
        private readonly Dictionary<string, float> _lastAutoSentAt = new Dictionary<string, float>(StringComparer.Ordinal);
        // Известная версия слота на сервере (0 — сохранения нет).
        private readonly Dictionary<string, long> _known = new Dictionary<string, long>(StringComparer.Ordinal);
        // Загрузка не удалась, версия неизвестна: автосохранение пойдёт с expectedVersion = 0.
        private readonly HashSet<string> _loadFailed = new HashSet<string>(StringComparer.Ordinal);
        // Слоты в конфликте: слот → версия на сервере.
        private readonly Dictionary<string, long> _conflicts = new Dictionary<string, long>(StringComparer.Ordinal);
        // Хвост очереди операций слота (операции одного слота — строго по очереди).
        private readonly Dictionary<string, Task> _tails = new Dictionary<string, Task>(StringComparer.Ordinal);
        // Время записей/удалений, дошедших до сервера, за последнюю минуту.
        private readonly Queue<float> _writeLog = new Queue<float>();
        private readonly HashSet<string> _warnedSlots = new HashSet<string>(StringComparer.Ordinal);

        private VhrSaveLimits _limits = new VhrSaveLimits();
        private long _seq;
        private int _inFlightWrites;
        private float _blockedUntil;    // 429: до этого момента не пишем
        private float _retryNotBefore;  // сеть / нет токена: пауза перед повтором
        private float _backoff;
        private bool _transientWarned;
        private bool _pumpRunning;
        private CancellationTokenSource _pumpCts;
        private bool _disposed;

        /// <inheritdoc />
        public event Action<string, long> OnConflict;

        /// <inheritdoc />
        public event Action<string, VhrSaveWriteResult> OnAutoSaveResult;

        /// <summary>
        /// Создаёт сервис. Обычно не нужно — используйте <see cref="VhrSdk.Saves"/>
        /// или внедрение <see cref="IVhrSaves"/> через <see cref="VhrSdkLifetimeScope"/>.
        /// </summary>
        public VhrSavesService(VhrApiClient api, VhrSdkOptions options, IVhrLog log = null)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _log = log ?? new VhrUnityLog(options.VerboseLogging);

            // Авто-flush: потеря фокуса (на телефоне приходит и перед паузой приложения),
            // выход; на WebGL — ещё скрытие/выгрузка страницы (VhrLifecycleBridge.jslib).
            Application.focusChanged += OnFocusChanged;
            Application.quitting += OnQuitting;
            VhrWebGlLifecycleChannel.PageHidden += OnPageHidden;
            VhrWebGlLifecycleChannel.EnsureInitialized();
        }

        /// <summary>
        /// Общий экземпляр на процесс. Первый вызов создаёт его из переданных
        /// зависимостей, последующие возвращают уже созданный.
        /// </summary>
        public static VhrSavesService GetOrCreate(VhrApiClient api, VhrSdkOptions options, IVhrLog log)
        {
            if (_shared != null && !_shared._disposed) return _shared;
            _shared = new VhrSavesService(api, options, log);
            return _shared;
        }

        /// <summary>Сбрасывает общий экземпляр. Только для тестов / <see cref="VhrSdk.ResetForTests"/>.</summary>
        internal static void ResetShared()
        {
            _shared?.Dispose();
            _shared = null;
        }

        /// <inheritdoc />
        public bool HasPendingSaves => _pending.Count > 0 || _inFlightWrites > 0;

        /// <inheritdoc />
        public VhrSaveLimits Limits => _limits.Clone();

        private bool IsSimulation =>
#if UNITY_EDITOR
            _options.IsEditorSimulation;
#else
            false;
#endif

        private static float Now => Time.realtimeSinceStartup;

        private string ListUrl => _options.BridgeBaseUrl + "/api/saves";

        private string SlotUrl(string slot) => ListUrl + "/" + Uri.EscapeDataString(slot);

        private float AutoInterval =>
            Mathf.Max(VhrSdkOptions.MinSavesIntervalSeconds,
                float.IsNaN(_options.SavesMinIntervalSeconds) ? 10f : _options.SavesMinIntervalSeconds);

        // ------------------------------------------------------------ чтение

        /// <inheritdoc />
        public async Task<VhrSaveLoadResult> LoadAsync(string slot = DefaultSlot, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryNormalizeSlot(slot, out var s, out var problem))
                return Log(VhrSaveLoadResult.Fail(slot, VhrSaveError.BadSlot, "bad_slot", problem));
            if (_disposed)
                return Log(VhrSaveLoadResult.Fail(s, VhrSaveError.Unknown, "disposed", "Сервис сохранений уничтожен."));

            return await RunSerialized(s, async () =>
            {
                ct.ThrowIfCancellationRequested();
                var r = await LoadCoreAsync(s, ct);
                if (r.Found || r.Error == VhrSaveError.NotFound)
                {
                    _known[s] = r.Found ? r.Version : 0;
                    _loadFailed.Remove(s);
                    if (_conflicts.Remove(s))
                    {
                        // Игра взяла облачную версию — придержанное автосохранение устарело.
                        _pending.Remove(s);
                        _log.Verbose($"Saves: конфликт слота '{s}' снят загрузкой (версия {r.Version}); придержанное автосохранение отброшено.");
                    }
                }
                else if (!_known.ContainsKey(s))
                {
                    // Что в облаке — неизвестно: автосохранение этого слота пойдёт с
                    // expectedVersion = 0 и не перезапишет облачный прогресс вслепую.
                    _loadFailed.Add(s);
                }
                return Log(r);
            });
        }

        /// <inheritdoc />
        public async Task<T> LoadJsonAsync<T>(string slot = DefaultSlot, T fallback = default, CancellationToken ct = default)
        {
            var r = await LoadAsync(slot, ct);
            if (!r.Found || string.IsNullOrEmpty(r.Data)) return fallback;
            try
            {
                var value = JsonUtility.FromJson<T>(r.Data);
                return value == null ? fallback : value;
            }
            catch (Exception e)
            {
                _log.Warn($"Saves: слот '{r.Slot}' не разобрался как {typeof(T).Name} ({e.Message}) — возвращаю fallback.");
                return fallback;
            }
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<VhrSaveSlotInfo>> ListAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_disposed) throw new ObjectDisposedException(nameof(VhrSavesService));
#if UNITY_EDITOR
            if (IsSimulation)
            {
                var sim = VhrEditorSaves.List(out var retryAfter);
                await Task.Yield(); // как с сервером: результат — не синхронно
                if (sim == null)
                {
                    throw new VhrSdkException("rate_limited",
                        $"Симуляция: больше {ReadLimitPerMinute} чтений в минуту — как на сервере. Повтор через {retryAfter} с.",
                        429, "rate_limited", null) { RetryAfterSeconds = retryAfter };
                }
                _limits = VhrEditorSaves.Limits();
                return sim;
            }
#endif
            if (!HasToken())
                throw new VhrSdkException("unauthorized", NoTokenMessage, 401, "unauthorized", null);

            var raw = await _api.SendRawAsync("GET", ListUrl, ct: ct);
            var dto = ParseJson<ListResponseDto>(raw);
            if (dto == null)
                throw new VhrSdkException("deserialize_error", "Не удалось разобрать список сохранений: " + Truncate(raw));
            if (dto.limits != null && dto.limits.maxSlotKb > 0 && dto.limits.maxSlots > 0)
                _limits = dto.limits.Clone();
            return dto.slots ?? Array.Empty<VhrSaveSlotInfo>();
        }

        // ------------------------------------------------------------ запись

        /// <inheritdoc />
        public async Task<VhrSaveWriteResult> SaveAsync(
            string slot, string data, long? expectedVersion = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryNormalizeSlot(slot, out var s, out var problem))
                return Log(VhrSaveWriteResult.Fail(slot, VhrSaveError.BadSlot, "bad_slot", problem));
            if (data == null)
                return Log(VhrSaveWriteResult.Fail(s, VhrSaveError.BadSlot, "bad_request",
                    "data = null: передайте строку (для объекта — SaveJsonAsync)."));
            if (expectedVersion.HasValue && expectedVersion.Value < 0)
                return Log(VhrSaveWriteResult.Fail(s, VhrSaveError.BadSlot, "bad_request",
                    "expectedVersion < 0: допустимо null, 0 (только создать) или версия ≥ 1."));
            if (_disposed)
                return Log(VhrSaveWriteResult.Fail(s, VhrSaveError.Unknown, "disposed", "Сервис сохранений уничтожен."));

            var mark = _seq; // автосохранения, накопленные до этого вызова
            return await RunWriteSerialized(s, async () =>
            {
                ct.ThrowIfCancellationRequested();
                var r = await WriteCoreAsync(s, data, expectedVersion, ct);
                NoteRateLimit(r);
                if (r.Ok)
                {
                    OnWriteSucceeded(s, r);
                    // Явная запись новее автосохранений, накопленных до неё.
                    if (_pending.TryGetValue(s, out var p) && p.Seq <= mark) _pending.Remove(s);
                    if (_conflicts.Remove(s))
                        _log.Verbose($"Saves: конфликт слота '{s}' снят явной записью (версия {r.Version}).");
                    if (_pending.Count > 0) EnsurePump();
                }
                return Log(r);
            });
        }

        /// <inheritdoc />
        public Task<VhrSaveWriteResult> SaveJsonAsync<T>(
            string slot, T value, long? expectedVersion = null, CancellationToken ct = default)
        {
            if (!TryToJson(value, out var json, out var problem))
                return Task.FromResult(Log(VhrSaveWriteResult.Fail(slot, VhrSaveError.BadSlot, "bad_request", problem)));
            return SaveAsync(slot, json, expectedVersion, ct);
        }

        /// <inheritdoc />
        public async Task<bool> DeleteAsync(string slot, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryNormalizeSlot(slot, out var s, out var problem))
            {
                _log.Warn("Saves: DeleteAsync — " + problem);
                return false;
            }
            if (_disposed) return false;

            var mark = _seq;
            return await RunWriteSerialized(s, async () =>
            {
                ct.ThrowIfCancellationRequested();
                var r = await DeleteCoreAsync(s, ct);
                NoteRateLimit(r);
                if (!r.Ok)
                {
                    _log.Warn($"Saves: удалить слот '{s}' не удалось — {r.Error} ({r.ErrorCode}). {r.Message}");
                    return false;
                }

                _known[s] = 0;
                _loadFailed.Remove(s);
                _conflicts.Remove(s);
                // Удаление отменяет автосохранения слота, накопленные до него.
                if (_pending.TryGetValue(s, out var p) && p.Seq <= mark) _pending.Remove(s);
                _log.Verbose($"Saves: слот '{s}' удалён.");
                return true;
            });
        }

        // ---------------------------------------------------- автосохранение

        /// <inheritdoc />
        public void QueueSave(string slot, string data)
        {
            if (_disposed) return;
            if (!TryNormalizeSlot(slot, out var s, out var problem))
            {
                if (_warnedSlots.Add(slot ?? string.Empty)) _log.Warn("Saves: QueueSave — " + problem);
                return;
            }
            if (data == null)
            {
                if (_warnedSlots.Add("null:" + s)) _log.Warn($"Saves: QueueSave('{s}') — data = null, пропускаю.");
                return;
            }

            if (_pending.TryGetValue(s, out var p))
            {
                p.Data = data;
                p.Seq = ++_seq;
            }
            else
            {
                _pending[s] = new PendingSave { Data = data, Seq = ++_seq };
            }
            EnsurePump();
        }

        /// <inheritdoc />
        public void QueueSaveJson<T>(string slot, T value)
        {
            if (_disposed) return;
            if (!TryToJson(value, out var json, out var problem))
            {
                _log.Warn($"Saves: QueueSaveJson('{slot}') — {problem}");
                return;
            }
            QueueSave(slot, json);
        }

        /// <inheritdoc />
        public async Task FlushAsync()
        {
            if (_disposed) return;

            for (int attempt = 0; attempt < FlushAttempts; attempt++)
            {
                // Сервер просил подождать (429) — ждём по реальному времени.
                var wait = _blockedUntil - Now;
                if (wait > 0f)
                {
                    try { await VhrAsync.DelayRealtimeAsync(Mathf.Min(wait, MaxRetryAfterSeconds)); }
                    catch (OperationCanceledException) { return; }
                    if (_disposed) return;
                }

                // Сначала список слотов: отправка синхронно меняет _pending.
                var slots = new List<string>();
                foreach (var kv in _pending)
                {
                    if (!_conflicts.ContainsKey(kv.Key)) slots.Add(kv.Key);
                }
                if (slots.Count == 0) break;

                // Слоты независимы — уходят параллельно (при закрытии вкладки важно
                // успеть начать все запросы внутри события страницы).
                var tasks = new Task<VhrSaveWriteResult>[slots.Count];
                for (int i = 0; i < slots.Count; i++) tasks[i] = SendPendingAsync(slots[i]);
                var results = await Task.WhenAll(tasks);

                bool limited = false;
                foreach (var r in results)
                {
                    if (r != null && r.Error == VhrSaveError.RateLimited) limited = true;
                }
                if (!limited) break;
            }

            // Дождаться записей, уже начатых в фоне (цикл автосохранения, явные SaveAsync).
            if (_tails.Count > 0)
                await Task.WhenAll(new List<Task>(_tails.Values));
        }

        private void EnsurePump()
        {
            if (_pumpRunning || _disposed) return;
            _ = PumpAsync();
        }

        /// <summary>
        /// Цикл автосохранения: отправляет слоты, у которых истёк интервал, с учётом
        /// паузы после 429, паузы после обрыва связи и бюджета записей в минуту.
        /// Завершается, когда отправлять нечего (следующий <see cref="QueueSave"/> запустит снова).
        /// </summary>
        private async Task PumpAsync()
        {
            _pumpRunning = true;
            _pumpCts ??= new CancellationTokenSource();
            var ct = _pumpCts.Token;
            try
            {
                while (!_disposed && !ct.IsCancellationRequested)
                {
                    var now = Now;
                    var notBefore = GlobalNotBefore(now);
                    string due = null;
                    var wait = float.MaxValue;
                    foreach (var kv in _pending)
                    {
                        if (_conflicts.ContainsKey(kv.Key)) continue;
                        var at = Mathf.Max(notBefore,
                            _lastAutoSentAt.TryGetValue(kv.Key, out var last) ? last + AutoInterval : 0f);
                        if (at <= now)
                        {
                            due = kv.Key;
                            break;
                        }
                        if (at - now < wait) wait = at - now;
                    }

                    if (due != null)
                    {
                        await SendPendingAsync(due);
                        continue;
                    }
                    if (wait == float.MaxValue) break; // отправлять нечего (или только слоты в конфликте)

                    // По реальному времени (пауза игры не тормозит автосохранение); короткими
                    // шагами — чтобы заметить новые слоты и снятую паузу.
                    await VhrAsync.DelayRealtimeAsync(Mathf.Min(wait, 1f), ct);
                }
            }
            catch (OperationCanceledException)
            {
                // выход из игры / Dispose
            }
            catch (Exception e)
            {
                _log.Error("Saves: сбой цикла автосохранения: " + e);
            }
            finally
            {
                _pumpRunning = false;
            }
        }

        private float GlobalNotBefore(float now)
        {
            var at = Mathf.Max(_blockedUntil, _retryNotBefore);
            PruneWriteLog(now);
            if (_writeLog.Count >= AutoWriteBudgetPerMinute)
                at = Mathf.Max(at, _writeLog.Peek() + 60f);
            return at;
        }

        // Лимит записей на сервере общий для всех слотов игрока в игре: 429 на любой
        // записи/удалении ставит на паузу и автосохранение.
        private void NoteRateLimit(VhrSaveWriteResult r)
        {
            if (r == null || r.Error != VhrSaveError.RateLimited) return;
            var until = Now + Math.Max(1, r.RetryAfterSeconds);
            if (until > _blockedUntil) _blockedUntil = until;
        }

        private void PruneWriteLog(float now)
        {
            while (_writeLog.Count > 0 && now - _writeLog.Peek() >= 60f) _writeLog.Dequeue();
        }

        private void NoteWrite()
        {
            var now = Now;
            PruneWriteLog(now);
            _writeLog.Enqueue(now);
        }

        /// <summary>Отправляет накопленное автосохранение слота (если оно ещё есть). <c>null</c> — отправлять нечего.</summary>
        private Task<VhrSaveWriteResult> SendPendingAsync(string s)
            => RunWriteSerialized(s, async () =>
            {
                if (_disposed || _conflicts.ContainsKey(s) || !_pending.TryGetValue(s, out var p))
                    return null;

                _pending.Remove(s);
                _lastAutoSentAt[s] = Now;
                var r = await WriteCoreAsync(s, p.Data, AutoExpectedVersion(s), CancellationToken.None);
                HandleAutoResult(s, p, r);
                return r;
            });

        /// <summary>
        /// Версия, поверх которой пишет автосохранение: известная (после загрузки/записи),
        /// 0 — если загрузка не удалась (не перезаписывать облако вслепую), иначе <c>null</c>
        /// (слот в этой сессии не загружали — last-write-wins).
        /// </summary>
        private long? AutoExpectedVersion(string s)
        {
            if (_known.TryGetValue(s, out var v)) return v;
            if (_loadFailed.Contains(s)) return 0;
            return null;
        }

        private void HandleAutoResult(string s, PendingSave p, VhrSaveWriteResult r)
        {
            if (r.Ok)
            {
                OnWriteSucceeded(s, r);
                _log.Verbose($"Saves: автосохранение слота '{s}' → версия {r.Version} ({r.RawSize} Б, сжато {r.StoredSize} Б).");
            }
            else
            {
                switch (r.Error)
                {
                    case VhrSaveError.Conflict:
                        _conflicts[s] = r.ServerVersion;
                        Requeue(s, p);
                        _log.Warn($"Saves: конфликт версий слота '{s}' — в облаке версия {r.ServerVersion} " +
                                  "(сохранено с другого устройства или вкладки). Автосохранение слота приостановлено, " +
                                  "облачные данные не тронуты. Решение — в обработчике OnConflict: LoadAsync(slot) — взять " +
                                  $"облачное, или SaveAsync(slot, data, expectedVersion: {r.ServerVersion}) — перезаписать своим.");
                        RaiseConflict(s, r.ServerVersion);
                        break;

                    case VhrSaveError.RateLimited:
                        NoteRateLimit(r);
                        Requeue(s, p);
                        WarnTransient($"Saves: сервер ограничил частоту записей (429) — автосохранение слота '{s}' повторится через {r.RetryAfterSeconds} с.");
                        break;

                    case VhrSaveError.Unknown when r.ErrorCode == "sdk_update_required":
                        // Повтор не поможет, пока игру не пересоберут с новым SDK.
                        _log.Error($"Saves: автосохранение слота '{s}' отклонено — {r.Message}");
                        break;

                    case VhrSaveError.Network:
                    case VhrSaveError.Unknown:
                    case VhrSaveError.Disabled:
                        _backoff = r.Error == VhrSaveError.Disabled ? MaxNetworkBackoffSeconds
                            : _backoff <= 0f ? FirstNetworkBackoffSeconds
                            : Mathf.Min(_backoff * 2f, MaxNetworkBackoffSeconds);
                        _retryNotBefore = Now + _backoff;
                        Requeue(s, p);
                        WarnTransient($"Saves: автосохранение слота '{s}' не отправлено ({r.Error}: {r.Message}) — " +
                                      $"данные в памяти, повтор через {_backoff:0} с.");
                        break;

                    case VhrSaveError.NotAuthenticated:
                        _retryNotBefore = Now + NoTokenRetrySeconds;
                        Requeue(s, p);
                        WarnTransient($"Saves: автосохранение слота '{s}' не отправлено — {r.Message}");
                        break;

                    default:
                        // QuotaExceeded, SlotTooLarge, SlotsLimit, BadSlot: повтор тех же данных не поможет.
                        _log.Warn($"Saves: автосохранение слота '{s}' отклонено ({r.Error}, {r.ErrorCode}). {r.Message} " +
                                  "Эти данные не сохранены; следующий QueueSave попробует снова.");
                        break;
                }
            }
            RaiseAutoSaveResult(s, r);
        }

        // Вернуть данные в очередь, если за время отправки не накопилось новых.
        private void Requeue(string s, PendingSave p)
        {
            if (!_pending.ContainsKey(s)) _pending[s] = p;
            EnsurePump();
        }

        private void OnWriteSucceeded(string s, VhrSaveWriteResult r)
        {
            // Записи слота идут строго по очереди — версия из ответа и есть текущая.
            _known[s] = r.Version;
            _loadFailed.Remove(s);
            _backoff = 0f;
            _retryNotBefore = 0f;
            _transientWarned = false;
        }

        private void WarnTransient(string message)
        {
            // Одно предупреждение на серию сбоев, дальше — только подробный лог.
            if (_transientWarned) _log.Verbose(message);
            else
            {
                _transientWarned = true;
                _log.Warn(message);
            }
        }

        private void RaiseConflict(string s, long serverVersion)
        {
            try { OnConflict?.Invoke(s, serverVersion); }
            catch (Exception e) { _log.Error("Saves: обработчик OnConflict упал: " + e); }
        }

        private void RaiseAutoSaveResult(string s, VhrSaveWriteResult r)
        {
            try { OnAutoSaveResult?.Invoke(s, r); }
            catch (Exception e) { _log.Error("Saves: обработчик OnAutoSaveResult упал: " + e); }
        }

        // -------------------------------------------------- жизненный цикл

        private void OnFocusChanged(bool focused)
        {
            if (!focused) FlushInBackground("потеря фокуса");
        }

        private void OnPageHidden(int code)
            => FlushInBackground(code == VhrWebGlLifecycleChannel.EventPageHide ? "страница закрывается" : "страница скрыта");

        private void OnQuitting()
        {
            FlushInBackground("выход из игры");
            // Цикл ожидания больше не нужен; уже начатые запросы доедут сами (если процесс успеет).
            _pumpCts?.Cancel();
            _pumpCts = null;
        }

        private void FlushInBackground(string reason)
        {
            if (_disposed || !HasSendablePending()) return;
            _log.Verbose($"Saves: {reason} — отправляю накопленные сохранения сразу.");
            _ = FlushQuietlyAsync();
        }

        private async Task FlushQuietlyAsync()
        {
            try { await FlushAsync(); }
            catch (Exception e) { _log.Warn("Saves: фоновая отправка сохранений не удалась: " + e.Message); }
        }

        private bool HasSendablePending()
        {
            foreach (var k in _pending.Keys)
            {
                if (!_conflicts.ContainsKey(k)) return true;
            }
            return false;
        }

        // ---------------------------------------------- очередь операций слота

        // ⚠️ НЕ добавлять .ConfigureAwait(false): на WebGL нет тредпула, и продолжение
        // не выполнится никогда (см. CHANGELOG 1.7.6). Хвост — TCS с
        // RunContinuationsAsynchronously: следующая операция стартует на следующем тике Unity.
        private async Task<T> RunSerialized<T>(string slot, Func<Task<T>> op)
        {
            _tails.TryGetValue(slot, out var prev);
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _tails[slot] = done.Task;
            try
            {
                // prev не падает (TrySetResult в finally); завершён — идём синхронно.
                if (prev != null && !prev.IsCompleted) await prev;
                return await op();
            }
            finally
            {
                if (_tails.TryGetValue(slot, out var cur) && cur == done.Task) _tails.Remove(slot);
                done.TrySetResult(true);
            }
        }

        private async Task<T> RunWriteSerialized<T>(string slot, Func<Task<T>> op)
        {
            _inFlightWrites++;
            try { return await RunSerialized(slot, op); }
            finally { _inFlightWrites--; }
        }

        // ------------------------------------------------------- транспорт

        private async Task<VhrSaveLoadResult> LoadCoreAsync(string s, CancellationToken ct)
        {
#if UNITY_EDITOR
            if (IsSimulation)
            {
                var sim = VhrEditorSaves.Get(s);
                await Task.Yield();
                return sim;
            }
#endif
            if (!HasToken())
                return VhrSaveLoadResult.Fail(s, VhrSaveError.NotAuthenticated, "unauthorized", NoTokenMessage);

            try
            {
                var raw = await _api.SendRawAsync("GET", SlotUrl(s), ct: ct);
                var dto = ParseJson<LoadResponseDto>(raw);
                if (dto == null)
                    return VhrSaveLoadResult.Fail(s, VhrSaveError.Unknown, "empty_response",
                        "Сервер вернул пустой или нечитаемый ответ: " + Truncate(raw));
                return new VhrSaveLoadResult
                {
                    Slot = s, Found = true, Data = dto.data ?? string.Empty, Version = dto.version, UpdatedAt = dto.updatedAt
                };
            }
            catch (VhrSdkException ex)
            {
                // Транспорт на отмену отвечает «canceled» (status 0) — отдаём честную отмену.
                ct.ThrowIfCancellationRequested();
                var e = FromHttpError(ex);
                return VhrSaveLoadResult.Fail(s, e.Kind, e.Code, e.Message);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                return VhrSaveLoadResult.Fail(s, VhrSaveError.Unknown, "exception", e.Message);
            }
        }

        private async Task<VhrSaveWriteResult> WriteCoreAsync(string s, string data, long? expected, CancellationToken ct)
        {
            int rawSize;
            try { rawSize = Encoding.UTF8.GetByteCount(data); }
            catch (Exception e)
            {
                return VhrSaveWriteResult.Fail(s, VhrSaveError.BadSlot, "bad_request",
                    "Строка сохранения не кодируется в UTF-8: " + e.Message);
            }
            // Жёсткий лимит платформы до сжатия — не гоняем заведомо отклоняемый запрос.
            if (rawSize > MaxRawBytes)
            {
                return VhrSaveWriteResult.Fail(s, VhrSaveError.SlotTooLarge, "slot_too_large",
                    $"Сохранение {rawSize} Б — больше 1 МБ до сжатия (лимит платформы). Храните в облаке только прогресс.");
            }

#if UNITY_EDITOR
            if (IsSimulation)
            {
                NoteWrite();
                var sim = VhrEditorSaves.Put(s, data, expected);
                await Task.Yield();
                return sim;
            }
#endif
            if (!HasToken())
                return VhrSaveWriteResult.Fail(s, VhrSaveError.NotAuthenticated, "unauthorized", NoTokenMessage);

            var body = new VhrRawJson(BuildPutBody(data, expected));
            NoteWrite();
            try
            {
                var raw = await _api.SendRawAsync("PUT", SlotUrl(s), body, ct: ct);
                var dto = ParseJson<WriteResponseDto>(raw);
                if (dto == null || dto.version <= 0)
                {
                    return VhrSaveWriteResult.Fail(s, VhrSaveError.Unknown, "empty_response",
                        "Сервер ответил на запись без версии: " + Truncate(raw));
                }
                return new VhrSaveWriteResult
                {
                    Slot = s,
                    Version = dto.version,
                    UpdatedAt = dto.updatedAt,
                    RawSize = dto.rawSize > 0 ? dto.rawSize : rawSize,
                    StoredSize = dto.storedSize
                };
            }
            catch (VhrSdkException ex)
            {
                ct.ThrowIfCancellationRequested();
                var e = FromHttpError(ex);
                return new VhrSaveWriteResult
                {
                    Slot = s,
                    Error = e.Kind,
                    ErrorCode = e.Code,
                    Message = e.Message,
                    ServerVersion = e.Kind == VhrSaveError.Conflict ? e.ServerVersion : 0,
                    ServerUpdatedAt = e.Kind == VhrSaveError.Conflict ? e.ServerUpdatedAt : null,
                    RetryAfterSeconds = e.Kind == VhrSaveError.RateLimited ? e.RetryAfter : 0
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                return VhrSaveWriteResult.Fail(s, VhrSaveError.Unknown, "exception", e.Message);
            }
        }

        private async Task<VhrSaveWriteResult> DeleteCoreAsync(string s, CancellationToken ct)
        {
#if UNITY_EDITOR
            if (IsSimulation)
            {
                NoteWrite();
                var sim = VhrEditorSaves.Delete(s);
                await Task.Yield();
                return sim;
            }
#endif
            if (!HasToken())
                return VhrSaveWriteResult.Fail(s, VhrSaveError.NotAuthenticated, "unauthorized", NoTokenMessage);

            NoteWrite();
            try
            {
                await _api.SendRawAsync("DELETE", SlotUrl(s), ct: ct);
                return new VhrSaveWriteResult { Slot = s };
            }
            catch (VhrSdkException ex)
            {
                ct.ThrowIfCancellationRequested();
                var e = FromHttpError(ex);
                // Удаление идемпотентно: «нечего удалять» — тоже успех.
                if (e.Kind == VhrSaveError.NotFound) return new VhrSaveWriteResult { Slot = s };
                return new VhrSaveWriteResult
                {
                    Slot = s, Error = e.Kind, ErrorCode = e.Code, Message = e.Message,
                    RetryAfterSeconds = e.Kind == VhrSaveError.RateLimited ? e.RetryAfter : 0
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                return VhrSaveWriteResult.Fail(s, VhrSaveError.Unknown, "exception", e.Message);
            }
        }

        private bool HasToken()
        {
            try { return !string.IsNullOrEmpty(_options.TokenProvider?.Invoke()); }
            catch { return false; }
        }

        private static string NoTokenMessage =>
#if UNITY_EDITOR
            "Нет токена игрока: в Unity Editor включите Live (песочный ключ) в окне VHR → Тестирование в Editor " +
            "или режим Simulation (локальные сохранения).";
#else
            "Нет токена игрока: облачные сохранения доступны только авторизованному игроку на vhrgames.ru. " +
            "Гостю сохраняйте прогресс локально (PlayerPrefs).";
#endif

        // ------------------------------------------------------ ошибки моста

        private struct ErrorInfo
        {
            public VhrSaveError Kind;
            public string Code;
            public string Message;
            public long ServerVersion;
            public string ServerUpdatedAt;
            public int RetryAfter;
        }

        /// <summary>HTTP-ошибка моста → <see cref="VhrSaveError"/> (по коду из тела <c>{ code }</c> и HTTP-статусу).</summary>
        private ErrorInfo FromHttpError(VhrSdkException ex)
        {
            var dto = ParseJson<ErrorDto>(ex.ResponseBody);
            var code = (ex.ServerCode ?? dto?.code ?? string.Empty).Trim().ToLowerInvariant();
            var http = ex.HttpStatus;
            var kind = MapError(http, code);

            if (dto != null)
            {
                if (dto.maxSlotKb > 0) _limits.maxSlotKb = dto.maxSlotKb;
                if (dto.maxRawKb > 0) _limits.maxRawKb = dto.maxRawKb;
                if (dto.maxSlots > 0) _limits.maxSlots = dto.maxSlots;
            }

            var info = new ErrorInfo
            {
                Kind = kind,
                Code = code.Length > 0 ? code : (http == 0 ? "connection_error" : ex.Code),
                ServerVersion = dto?.serverVersion ?? 0,
                ServerUpdatedAt = dto?.serverUpdatedAt,
                RetryAfter = 0
            };

            if (kind == VhrSaveError.RateLimited)
            {
                // Заголовок Retry-After (сервер открывает его через CORS — виден и на WebGL),
                // иначе поле retryAfter тела, иначе минута.
                int sec = ex.RetryAfterSeconds;
                if (sec <= 0 && dto != null) sec = dto.retryAfter > 0 ? dto.retryAfter : dto.retryAfterSeconds;
                if (sec <= 0) sec = DefaultRetryAfterSeconds;
                info.RetryAfter = Mathf.Clamp(sec, 1, MaxRetryAfterSeconds);
            }

            info.Message = Describe(kind, code, http, info);
            if (!string.IsNullOrWhiteSpace(dto?.message)) info.Message += " Сервер: " + dto.message.Trim();
            return info;
        }

        private static VhrSaveError MapError(long http, string code)
        {
            switch (code)
            {
                case "save_not_found": return VhrSaveError.NotFound;
                case "version_conflict": return VhrSaveError.Conflict;
                case "storage_quota_exceeded": return VhrSaveError.QuotaExceeded;
                case "slot_too_large": return VhrSaveError.SlotTooLarge;
                case "slots_limit": return VhrSaveError.SlotsLimit;
                case "rate_limited": return VhrSaveError.RateLimited;
                case "saves_disabled":
                case "game_not_found": return VhrSaveError.Disabled;
                case "bad_slot":
                case "bad_request": return VhrSaveError.BadSlot;
            }

            switch (http)
            {
                case 0: return VhrSaveError.Network;
                case 400: return VhrSaveError.BadSlot;
                case 401:
                case 403: return VhrSaveError.NotAuthenticated;
                case 409: return VhrSaveError.Conflict;
                case 413: return VhrSaveError.SlotTooLarge;
                case 429: return VhrSaveError.RateLimited;
                // 404 без кода save_not_found — не «сохранения нет», а, скорее, сервер без
                // эндпоинта: не даём игре решить, что прогресса нет.
                default: return VhrSaveError.Unknown;
            }
        }

        private string Describe(VhrSaveError kind, string code, long http, ErrorInfo info)
        {
            switch (kind)
            {
                case VhrSaveError.NotFound:
                    return "Сохранения в слоте нет.";
                case VhrSaveError.Conflict:
                    return $"Версия не совпала: на сервере версия {info.ServerVersion} (слот перезаписан с другого устройства или вкладки).";
                case VhrSaveError.QuotaExceeded:
                    return "Квота облачных сохранений игры исчерпана: запись, увеличивающая объём, отклонена (уменьшающая пройдёт). " +
                           "Увеличить квоту может разработчик в кабинете: игра → Облачные сохранения.";
                case VhrSaveError.SlotTooLarge:
                    return $"Слот больше лимита: {_limits.maxSlotKb} КБ после сжатия и {_limits.maxRawKb} КБ до сжатия.";
                case VhrSaveError.SlotsLimit:
                    return $"У игрока уже максимум слотов ({_limits.maxSlots}) — пишите в существующий или удалите ненужный (DeleteAsync).";
                case VhrSaveError.RateLimited:
                    return $"Слишком частые запросы (лимит: {WriteLimitPerMinute} записей и {ReadLimitPerMinute} чтений в минуту), " +
                           $"повтор через {info.RetryAfter} с. Для частых сохранений — QueueSave.";
                case VhrSaveError.NotAuthenticated:
#if UNITY_EDITOR
                    return "Сервер не принял ключ" + (code.Length > 0 ? $" ({code})" : "") +
                           ": проверьте песочный ключ в окне VHR → Тестирование в Editor.";
#else
                    return code == "game_mismatch"
                        ? "Игра запроса не совпадает с игрой токена (gid): проверьте VhrSdkOptions.GameId."
                        : "Нет действующего игрового токена" + (code.Length > 0 ? $" ({code})" : "") +
                          ": игрок должен быть авторизован на vhrgames.ru.";
#endif
                case VhrSaveError.Disabled:
                    return code == "game_not_found"
                        ? "Игра не найдена или не опубликована: у реальных игроков сохранения работают только в активных играх " +
                          "(черновик — только в Unity Editor Live)."
                        : "Облачные сохранения временно отключены платформой.";
                case VhrSaveError.Network:
                    return "Нет связи с сервером VHR.";
                case VhrSaveError.BadSlot:
                    return "Сервер отклонил слот или тело запроса" + (code.Length > 0 ? $" ({code})." : ".");
                default:
                    if (http == 426 || code == "sdk_update_required")
                        return $"Сервер требует новую версию VHR SDK (сейчас {VhrSdk.SdkVersion}) — обновите пакет ru.vhrgames.sdk.";
                    if (http == 404)
                        return "Эндпоинт сохранений не найден (HTTP 404) — сервер ещё не обновлён или неверный BridgeBaseUrl.";
                    return $"Ошибка сервера (HTTP {http}).";
            }
        }

        // ---------------------------------------------------------- утилиты

        /// <summary>
        /// Имя слота → нижний регистр и проверка <c>^[a-z0-9_.-]{1,64}$</c>, кроме <c>"."</c>
        /// и <c>".."</c> — как на сервере.
        /// </summary>
        internal static bool TryNormalizeSlot(string slot, out string normalized, out string problem)
        {
            normalized = (slot ?? string.Empty).Trim().ToLowerInvariant();
            problem = null;
            if (normalized.Length == 0)
                problem = "пустое имя слота (по умолчанию — \"default\").";
            else if (normalized.Length > 64)
                problem = $"имя слота длиннее 64 символов: '{normalized}'.";
            else
            {
                foreach (var c in normalized)
                {
                    bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-';
                    if (!ok)
                    {
                        problem = $"недопустимый символ '{c}' в имени слота '{slot}': только a-z, 0-9, '_', '.', '-'.";
                        break;
                    }
                }
                // "." и ".." нормализуют браузер/nginx в пути URL — сервер их отклоняет (bad_slot).
                if (problem == null && (normalized == "." || normalized == ".."))
                    problem = $"имя слота '{slot}' недопустимо (\".\" и \"..\" ломают путь URL).";
            }
            return problem == null;
        }

        private static bool TryToJson<T>(T value, out string json, out string problem)
        {
            json = null;
            problem = null;
            if (value == null)
            {
                problem = "value = null: сохранять нечего.";
                return false;
            }
            try { json = JsonUtility.ToJson(value); }
            catch (Exception e)
            {
                problem = "JsonUtility.ToJson не справился: " + e.Message;
                return false;
            }
            if (string.IsNullOrEmpty(json))
            {
                problem = $"JsonUtility.ToJson вернул пустую строку для {typeof(T).Name} — нужен [Serializable] класс или структура.";
                return false;
            }
            return true;
        }

        /// <summary>Тело <c>PUT /api/saves/{slot}</c>: <c>{ "data": "…", "expectedVersion": null | N }</c>.</summary>
        internal static string BuildPutBody(string data, long? expectedVersion)
        {
            var sb = new StringBuilder((data?.Length ?? 0) + 48);
            sb.Append("{\"data\":");
            AppendJsonString(sb, data ?? string.Empty);
            sb.Append(",\"expectedVersion\":");
            sb.Append(expectedVersion.HasValue
                ? expectedVersion.Value.ToString(CultureInfo.InvariantCulture)
                : "null");
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendJsonString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ' || c == (char)0x2028 || c == (char)0x2029)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static T ParseJson<T>(string raw) where T : class
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var t = raw.TrimStart();
            if (t.Length == 0 || t[0] != '{') return null;
            try { return JsonUtility.FromJson<T>(raw); }
            catch { return null; }
        }

        private static string Truncate(string s)
            => string.IsNullOrEmpty(s) ? "<пусто>" : (s.Length <= 200 ? s : s.Substring(0, 200) + "…");

        private VhrSaveLoadResult Log(VhrSaveLoadResult r)
        {
            if (r.Found) _log.Verbose($"Saves: слот '{r.Slot}' загружен, версия {r.Version}.");
            else if (r.Error == VhrSaveError.NotFound) _log.Verbose($"Saves: в слоте '{r.Slot}' сохранения нет.");
            else _log.Warn($"Saves: загрузить слот '{r.Slot}' не удалось — {r.Error} ({r.ErrorCode}). {r.Message}");
            return r;
        }

        private VhrSaveWriteResult Log(VhrSaveWriteResult r)
        {
            if (r.Ok) _log.Verbose($"Saves: слот '{r.Slot}' записан, версия {r.Version} ({r.RawSize} Б, сжато {r.StoredSize} Б).");
            else if (r.Error == VhrSaveError.Conflict) _log.Verbose($"Saves: запись слота '{r.Slot}' — конфликт версий, на сервере версия {r.ServerVersion}.");
            else _log.Warn($"Saves: записать слот '{r.Slot}' не удалось — {r.Error} ({r.ErrorCode}). {r.Message}");
            return r;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Application.focusChanged -= OnFocusChanged;
            Application.quitting -= OnQuitting;
            VhrWebGlLifecycleChannel.PageHidden -= OnPageHidden;
            _pumpCts?.Cancel();
            _pumpCts = null;
            if (_pending.Count > 0)
                _log.Warn($"Saves: сервис уничтожен, не отправлено автосохранений: {_pending.Count}.");
            _pending.Clear();
            OnConflict = null;
            OnAutoSaveResult = null;
            if (ReferenceEquals(_shared, this)) _shared = null;
        }

        // --- DTO (JsonUtility; имена полей — контракт моста /api/saves) ---
#pragma warning disable CS0649 // поля заполняет JsonUtility

        // GET /api/saves/{slot}: { slot, data, version, updatedAt }
        [Serializable] private sealed class LoadResponseDto
        { public string slot; public string data; public long version; public string updatedAt; }

        // PUT /api/saves/{slot}: { slot, version, updatedAt, rawSize, storedSize }
        [Serializable] private sealed class WriteResponseDto
        { public string slot; public long version; public string updatedAt; public int rawSize; public int storedSize; }

        // GET /api/saves: { slots: [ { slot, version, rawSize, updatedAt } ], limits: { maxSlotKb, maxSlots, maxRawKb } }
        [Serializable] private sealed class ListResponseDto
        { public VhrSaveSlotInfo[] slots; public VhrSaveLimits limits; }

        // Ошибки: { code, message, ... } (+ serverVersion/serverUpdatedAt у 409 — serverVersion = 0, если слота нет;
        // maxSlotKb/maxRawKb у 413; maxSlots у slots_limit; retryAfter у 429 — дублирует заголовок Retry-After).
        [Serializable] private sealed class ErrorDto
        {
            public string code;
            public string message;
            public long serverVersion;
            public string serverUpdatedAt;
            public int maxSlotKb;
            public int maxRawKb;
            public int maxSlots;
            public int retryAfter;
            public int retryAfterSeconds;
        }
#pragma warning restore CS0649
    }
}
