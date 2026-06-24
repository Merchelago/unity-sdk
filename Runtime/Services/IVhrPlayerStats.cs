using System;
using System.Threading;
using System.Threading.Tasks;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Статистика текущего игрока (профиль/прогрессия). Backed by
    /// <c>{GamesBaseUrl}/api/PlayerStats/*</c>.
    /// <para>
    /// Player stats / progression for the signed-in user. Requires a valid JWT
    /// (the SDK attaches it via <see cref="VhrApiClient"/>).
    /// </para>
    /// </summary>
    public interface IVhrPlayerStats
    {
        /// <summary>
        /// <c>GET /api/PlayerStats</c> — сводная статистика текущего пользователя:
        /// уровень, XP, ранг, монеты, победы/поражения, время в играх, надетая
        /// косметика и бейдж подписки.
        /// <para>
        /// Returns the aggregated stats object for the current user (level, XP,
        /// rank, coins, wins/losses, play time, equipped cosmetics, subscription
        /// badge). The endpoint is JWT-protected; a missing/invalid token surfaces
        /// as a transport error from <see cref="VhrApiClient"/>.
        /// </para>
        /// </summary>
        Task<VhrPlayerStats> GetMyStatsAsync(CancellationToken ct = default);
    }

    /// <summary>HTTP implementation of <see cref="IVhrPlayerStats"/>.</summary>
    public sealed class VhrPlayerStatsService : IVhrPlayerStats
    {
        private readonly VhrApiClient _api;
        private readonly VhrSdkOptions _options;
        private readonly IVhrLog _log;

        /// <summary>Creates the player-stats service.</summary>
        public VhrPlayerStatsService(VhrApiClient api, VhrSdkOptions options, IVhrLog log)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _log = log;
        }

        // База — /api/PlayerStats. Пустой path даёт ровно базовый маршрут (без
        // хвостового слэша), непустой — /api/PlayerStats/<path>.
        // Base is /api/PlayerStats; an empty path yields exactly the base route
        // (no trailing slash), a non-empty path appends "/<path>".
        private string Url(string path) =>
            string.IsNullOrEmpty(path)
                ? $"{_options.GamesBaseUrl}/api/PlayerStats"
                : $"{_options.GamesBaseUrl}/api/PlayerStats/{path}";

        /// <inheritdoc />
        public async Task<VhrPlayerStats> GetMyStatsAsync(CancellationToken ct = default)
        {
            var stats = await _api.SendAsync<VhrPlayerStats>("GET", Url(""), ct: ct);
            if (stats == null)
            {
                _log?.Warn("PlayerStats read returned no body.");
                return new VhrPlayerStats();
            }
            return stats;
        }
    }

    /// <summary>
    /// Сводная статистика игрока (ответ <c>GET /api/PlayerStats</c>).
    /// <para>Aggregated player stats returned by <c>GET /api/PlayerStats</c>.</para>
    /// </summary>
    [Serializable]
    public sealed class VhrPlayerStats
    {
        /// <summary>Победы. / Total wins.</summary>
        public int wins;

        /// <summary>Поражения. / Total losses.</summary>
        public int losses;

        /// <summary>Процент побед (0..100, округлён до 0.1). / Win rate percent.</summary>
        public double winRate;

        /// <summary>Текущая серия побед. / Current win streak.</summary>
        public int currentStreak;

        /// <summary>Суммарное время в играх, минуты. / Total play time in minutes.</summary>
        public long totalPlayedMinutes;

        /// <summary>Текущий уровень игрока. / Current player level.</summary>
        public int level;

        /// <summary>Баланс монет. / Coin balance.</summary>
        public long coins;

        /// <summary>Название любимой игры (или null). / Favorite game title (may be null).</summary>
        public string favoriteGame;

        /// <summary>Id любимой игры (или null). / Favorite game id (may be null).</summary>
        public string favoriteGameId;

        /// <summary>Накопленный опыт. / Accumulated XP.</summary>
        public long xp;

        /// <summary>Сколько всего XP нужно для следующего уровня. / Total XP threshold for next level.</summary>
        public long xpForNextLevel;

        /// <summary>XP, набранный внутри текущего уровня. / XP earned within the current level.</summary>
        public long xpIntoCurrentLevel;

        /// <summary>Суммарное время в играх, секунды. / Total play time in seconds.</summary>
        public long totalSecondsPlayed;

        /// <summary>Всего сессий. / Total play sessions.</summary>
        public int totalSessions;

        /// <summary>Число различных сыгранных игр. / Distinct games played.</summary>
        public int gamesPlayed;

        /// <summary>
        /// True, если у игрока есть хотя бы одна сессия с исходом win/loss
        /// (иначе блок побед скрывается).
        /// <para>True when the player has at least one win/loss session.</para>
        /// </summary>
        public bool hasWinConcept;

        /// <summary>Ранг, производный от уровня. / Rank derived from level.</summary>
        public VhrPlayerRank rank;

        /// <summary>Бейдж подписки. / Subscription badge.</summary>
        public VhrPlayerSubscription subscription;

        /// <summary>Надетая косметика (рамка/цвет ника/титул). / Equipped cosmetics.</summary>
        public VhrPlayerCosmetics cosmetics;
    }

    /// <summary>
    /// Ранг игрока (тир + под-тир), производный от уровня.
    /// <para>Player rank (tier + sub-tier), derived from level.</para>
    /// </summary>
    [Serializable]
    public sealed class VhrPlayerRank
    {
        /// <summary>Тир: «Бронза»…«Гроссмейстер». / Tier name.</summary>
        public string tier;

        /// <summary>Под-тир: "III"|"II"|"I"|"" (грандмастер без под-тиров). / Sub-tier.</summary>
        public string subTier;

        /// <summary>Полная метка, напр. «Золото II». / Full label, e.g. "Gold II".</summary>
        public string label;

        /// <summary>Цвет (hex или "rainbow"). / Color (hex or "rainbow").</summary>
        public string color;

        /// <summary>Иконка (эмодзи). / Icon (emoji).</summary>
        public string icon;

        /// <summary>Индекс тира (0..6). / Tier index (0..6).</summary>
        public int tierIndex;

        /// <summary>Грандмастер (высший тир). / Grandmaster (top tier).</summary>
        public bool isGrandmaster;
    }

    /// <summary>
    /// Бейдж подписки игрока для UI/перков.
    /// <para>Player subscription badge for UI / perks.</para>
    /// </summary>
    [Serializable]
    public sealed class VhrPlayerSubscription
    {
        /// <summary>Подписка активна. / Subscription is active.</summary>
        public bool active;

        /// <summary>Ключ плана (или null). / Plan key (may be null).</summary>
        public string plan;

        /// <summary>Название плана (или null). / Plan title (may be null).</summary>
        public string planTitle;

        /// <summary>Дата окончания (ISO-8601, или null). / Expiry timestamp (ISO-8601, may be null).</summary>
        public string expiresAt;

        /// <summary>Множитель монет (1.0, если нет подписки). / Coin multiplier (1.0 when none).</summary>
        public double coinMultiplier;
    }

    /// <summary>
    /// Надетая косметика игрока (значения для рендера у ника/аватара).
    /// <para>Equipped cosmetics (render values for the player's name / avatar).</para>
    /// </summary>
    [Serializable]
    public sealed class VhrPlayerCosmetics
    {
        /// <summary>Рамка аватара (значение, или null). / Avatar frame value (may be null).</summary>
        public string frame;

        /// <summary>Цвет ника (hex или "rainbow", или null). / Name color (hex or "rainbow", may be null).</summary>
        public string nameColor;

        /// <summary>Текст титула (или null). / Title text (may be null).</summary>
        public string title;

        /// <summary>Редкость титула (или null). / Title rarity (may be null).</summary>
        public string titleRarity;
    }
}
