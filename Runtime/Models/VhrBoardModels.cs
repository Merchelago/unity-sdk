using System;
using System.Globalization;
using System.Text;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Направление сортировки таблицы лидеров (SDK 1.12.0+). Задаётся один раз — при
    /// первой отправке счёта в новую таблицу (<see cref="VhrBoardOptions.Sort"/>); дальше
    /// его меняют в кабинете разработчика (только пока в таблице нет результатов).
    /// </summary>
    public enum VhrBoardSort
    {
        /// <summary>Больше — лучше: очки, дистанция, волны. По умолчанию. На сервере — <c>desc</c>.</summary>
        HigherIsBetter = 0,

        /// <summary>Меньше — лучше: время прохождения, число ходов. На сервере — <c>asc</c>.</summary>
        LowerIsBetter = 1
    }

    /// <summary>
    /// Как показывать счёт таблицы (SDK 1.12.0+): см. <see cref="VhrScoreFormatter"/>.
    /// </summary>
    public enum VhrScoreFormat
    {
        /// <summary>
        /// Целое число с разрядами: «12 345» (+ суффикс таблицы из кабинета, напр. «12 345 очков»).
        /// По умолчанию. На сервере — <c>number</c>.
        /// </summary>
        Number = 0,

        /// <summary>
        /// Время в <b>миллисекундах</b>: «1:02.345», с часами — «1:02:03.004». Отправляйте
        /// счёт в мс (<c>(long)(seconds * 1000)</c>). На сервере — <c>time</c>.
        /// </summary>
        Time = 1
    }

    /// <summary>
    /// Настройки новой таблицы лидеров для
    /// <see cref="IVhrLeaderboard.SubmitAsync(string, long, VhrBoardOptions, System.Threading.CancellationToken)"/>.
    /// <para>
    /// Применяются <b>только при первом создании</b> таблицы (первая отправка счёта с новым
    /// id). Дальше название, формат, суффикс и порядок вкладок правятся в кабинете
    /// разработчика, а эти подсказки сервер игнорирует — их можно спокойно передавать
    /// при каждой отправке.
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class VhrBoardOptions
    {
        /// <summary>
        /// Название таблицы на сайте (до 64 символов). Пусто — для <c>default</c> «Рекорды»,
        /// иначе id таблицы с пробелами вместо <c>_</c>/<c>-</c> и заглавной первой буквой
        /// (<c>"time_attack"</c> → «Time attack»).
        /// </summary>
        public string Title;

        /// <summary>Направление: больше — лучше (по умолчанию) или меньше — лучше.</summary>
        public VhrBoardSort Sort = VhrBoardSort.HigherIsBetter;

        /// <summary>Формат счёта: число (по умолчанию) или время в миллисекундах.</summary>
        public VhrScoreFormat Format = VhrScoreFormat.Number;
    }

    /// <summary>
    /// Итог отправки счёта (<see cref="IVhrLeaderboard.SubmitAsync(long, System.Threading.CancellationToken)"/>, SDK 1.12.0+).
    /// Сервер хранит <b>лучший</b> результат игрока по направлению таблицы: худший счёт
    /// принимается, но рекорд не меняет (<see cref="improved"/> = <c>false</c>).
    /// </summary>
    [Serializable]
    public sealed class VhrScoreResult
    {
        /// <summary>
        /// <c>true</c> — сервер принял запрос (окончательные ошибки приходят исключением
        /// <see cref="VhrSdkException"/>). <c>false</c> вместе с <see cref="queued"/> — сервер был
        /// недоступен, счёт в очереди.
        /// </summary>
        public bool accepted;

        /// <summary>
        /// <c>true</c> — связи с сервером не было (сеть, таймаут, 5xx, 429, истёкший токен): счёт сохранён
        /// в очереди на устройстве (<see cref="IVhrConnection"/>) и уйдёт сам, когда связь вернётся, в
        /// том числе после перезапуска игры. <see cref="improved"/>, <see cref="best"/>, <see cref="rank"/>
        /// пока неизвестны (<c>false</c>/0) — покажите «рекорд отправится, когда появится связь».
        /// </summary>
        public bool queued;

        /// <summary><c>true</c> — это первый результат игрока в таблице или новый рекорд за всё время.</summary>
        public bool improved;

        /// <summary>Лучший результат игрока за всё время (после этой отправки).</summary>
        public long best;

        /// <summary>Место игрока в таблице за всё время (1 — первое; 0 — сервер не сообщил).</summary>
        public int rank;

        /// <summary>Id таблицы, в которую записан счёт (напр. <c>"default"</c>, <c>"survival"</c>).</summary>
        public string board;
    }

    /// <summary>
    /// Описание таблицы лидеров (SDK 1.12.0+): <see cref="IVhrLeaderboard.GetBoardsAsync"/>,
    /// <see cref="VhrBoardPage.board"/>.
    /// </summary>
    [Serializable]
    public sealed class VhrBoardInfo
    {
        /// <summary>Id таблицы в коде (<c>a-z0-9_-</c>, до 32 символов), напр. <c>"default"</c>.</summary>
        public string id;

        /// <summary>Название для игрока (задаётся при создании или в кабинете).</summary>
        public string title;

        /// <summary>Направление сортировки.</summary>
        public VhrBoardSort sort;

        /// <summary>Формат счёта.</summary>
        public VhrScoreFormat format;

        /// <summary>Суффикс числа из кабинета («очков», «м»…) или пусто.</summary>
        public string suffix;

        /// <summary><c>true</c> — основная таблица игры (её показывают первой на сайте).</summary>
        public bool isPrimary;

        /// <summary>
        /// <c>true</c> — скрыта на сайте разработчиком (игра читать и писать её может).
        /// В <see cref="VhrBoardPage.board"/> всегда <c>false</c>.
        /// </summary>
        public bool hidden;

        /// <summary>Счёт в формате этой таблицы — то же, что <see cref="VhrScoreFormatter.Format(long, VhrBoardInfo)"/>.</summary>
        public string FormatScore(long score) => VhrScoreFormatter.Format(score, this);
    }

    /// <summary>Строка таблицы лидеров (SDK 1.12.0+).</summary>
    [Serializable]
    public sealed class VhrBoardEntry
    {
        /// <summary>Место (1 — первое). При равном счёте выше тот, кто достиг его раньше.</summary>
        public int rank;

        /// <summary>VHR user id игрока или <c>null</c>, если игрок скрыл профиль.</summary>
        public string userId;

        /// <summary>
        /// Ник игрока — SDK получает его сам (<see cref="IVhrProfile.ResolveNamesAsync"/>, одним
        /// запросом на страницу). Скрытый профиль, пустой ник или ошибка — «Игрок»; тестовый
        /// игрок песочницы (<c>sbx_*</c>) — «Тестовый игрок».
        /// </summary>
        public string name;

        /// <summary>Лучший результат игрока за период.</summary>
        public long score;

        /// <summary>Счёт, отформатированный по таблице («12 345 очков», «1:02.345»).</summary>
        public string formatted;

        /// <summary><c>true</c> — это текущий игрок.</summary>
        public bool isCurrentUser;
    }

    /// <summary>Страница таблицы лидеров (<see cref="IVhrLeaderboard.GetTopAsync"/>, SDK 1.12.0+).</summary>
    [Serializable]
    public sealed class VhrBoardPage
    {
        /// <summary>Таблица: название, направление, формат.</summary>
        public VhrBoardInfo board;

        /// <summary>Период, за который посчитаны места.</summary>
        public VhrLeaderboardPeriod period;

        /// <summary>Лучшие игроки по порядку мест (не <c>null</c>; пусто — результатов ещё нет).</summary>
        public VhrBoardEntry[] entries;

        /// <summary>
        /// Место и результат текущего игрока, даже если он не попал в <see cref="entries"/>.
        /// <c>null</c> — у игрока нет результата за период, он гость или скрыт.
        /// </summary>
        public VhrBoardEntry me;

        /// <summary>Сколько всего игроков в таблице за период (для «вы на 57-м месте из 1 204»).</summary>
        public int total;
    }

    /// <summary>
    /// Форматирование счёта по таблице (SDK 1.12.0+) — так же, как на сайте VHR:
    /// число — «12 345» (+ суффикс), время в миллисекундах — «1:02.345» / «1:02:03.004».
    /// </summary>
    public static class VhrScoreFormatter
    {
        /// <summary>
        /// Счёт в формате таблицы <paramref name="board"/>. <c>null</c> — как число без суффикса.
        /// </summary>
        public static string Format(long score, VhrBoardInfo board)
            => board == null
                ? FormatNumber(score)
                : Format(score, board.format, board.suffix);

        /// <summary>
        /// Счёт в формате <paramref name="format"/>: число — с разрядами и суффиксом через
        /// пробел (если задан), время — по <see cref="FormatTime"/> (суффикс не добавляется).
        /// </summary>
        public static string Format(long score, VhrScoreFormat format, string suffix = null)
        {
            if (format == VhrScoreFormat.Time) return FormatTime(score);
            var number = FormatNumber(score);
            return string.IsNullOrWhiteSpace(suffix) ? number : number + " " + suffix.Trim();
        }

        /// <summary>Целое число с разрядами через пробел: <c>12345</c> → «12 345», <c>-1500</c> → «-1 500».</summary>
        public static string FormatNumber(long value)
        {
            var digits = Abs(value).ToString(CultureInfo.InvariantCulture);
            var sb = new StringBuilder(digits.Length + digits.Length / 3 + 1);
            if (value < 0) sb.Append('-');
            for (int i = 0; i < digits.Length; i++)
            {
                if (i > 0 && (digits.Length - i) % 3 == 0) sb.Append(' ');
                sb.Append(digits[i]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Время из миллисекунд: <c>62345</c> → «1:02.345», <c>5120</c> → «0:05.120»,
        /// <c>3723004</c> → «1:02:03.004» (часы — только если они есть).
        /// </summary>
        public static string FormatTime(long milliseconds)
        {
            var ms = Abs(milliseconds);
            var hours = ms / 3_600_000UL;
            var minutes = ms / 60_000UL % 60UL;
            var seconds = ms / 1000UL % 60UL;
            var fraction = ms % 1000UL;
            var sign = milliseconds < 0 ? "-" : string.Empty;
            var inv = CultureInfo.InvariantCulture;
            return hours > 0
                ? sign + hours.ToString(inv) + ":" + minutes.ToString("00", inv) + ":" +
                  seconds.ToString("00", inv) + "." + fraction.ToString("000", inv)
                : sign + minutes.ToString(inv) + ":" + seconds.ToString("00", inv) + "." +
                  fraction.ToString("000", inv);
        }

        // |value| без переполнения на long.MinValue.
        private static ulong Abs(long value) => value < 0 ? (ulong)(-(value + 1)) + 1UL : (ulong)value;
    }
}
