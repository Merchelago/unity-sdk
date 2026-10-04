using System;
using System.Text;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Разбор payload JWT <b>без проверки подписи</b> — подпись проверяет сервер.
    /// SDK читает claims только для удобства: срок жизни (<c>exp</c>) — чтобы
    /// заранее попросить у хоста свежий игровой токен, и <c>gid</c>/<c>sbx</c>/<c>sub</c>
    /// песочного ключа — чтобы в Unity Editor подставить <see cref="VhrSdkOptions.GameId"/>
    /// и показать тестового игрока. Никаких решений о доступе на этих данных не
    /// принимается.
    /// </summary>
    internal static class VhrJwt
    {
        /// <summary>Claims, которые нужны SDK. Пустые строки, если claim нет.</summary>
        internal sealed class Info
        {
            public string Subject = string.Empty;   // sub
            public string GameId = string.Empty;    // gid
            public string Sandbox = string.Empty;   // sbx ("1" у песочного ключа)
            public string Audience = string.Empty;  // aud (сырое значение; у массива — его содержимое)
            public DateTimeOffset? ExpiresAt;       // exp

            public bool IsSandbox => Sandbox == "1" || string.Equals(Sandbox, "true", StringComparison.OrdinalIgnoreCase);

            /// <summary>Игровой токен платформы: audience оканчивается на <c>#game</c>.</summary>
            public bool IsGameAudience => Audience.IndexOf("#game", StringComparison.Ordinal) >= 0;

            public bool IsExpired(TimeSpan skew) =>
                ExpiresAt.HasValue && ExpiresAt.Value <= DateTimeOffset.UtcNow.Add(skew);
        }

        /// <summary>Декодирует payload. <c>false</c>, если строка не похожа на JWT.</summary>
        public static bool TryDecode(string jwt, out Info info)
        {
            info = null;
            if (string.IsNullOrWhiteSpace(jwt)) return false;

            var parts = jwt.Trim().Split('.');
            if (parts.Length != 3 || parts[1].Length == 0) return false;

            string json;
            try
            {
                var b64 = parts[1].Replace('-', '+').Replace('_', '/');
                switch (b64.Length % 4)
                {
                    case 2: b64 += "=="; break;
                    case 3: b64 += "="; break;
                    case 1: return false;
                }
                json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrEmpty(json) || json[0] != '{') return false;

            info = new Info
            {
                Subject = ReadClaim(json, "sub"),
                GameId = ReadClaim(json, "gid"),
                Sandbox = ReadClaim(json, "sbx"),
                Audience = ReadClaim(json, "aud"),
            };

            var exp = ReadClaim(json, "exp");
            if (long.TryParse(exp, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var expSec))
            {
                try { info.ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(expSec); }
                catch { info.ExpiresAt = null; }
            }

            return true;
        }

        /// <summary>Срок жизни токена (<c>exp</c>), если его удалось прочитать.</summary>
        public static bool TryGetExpiry(string jwt, out DateTimeOffset expiresAt)
        {
            expiresAt = default;
            if (!TryDecode(jwt, out var info) || !info.ExpiresAt.HasValue) return false;
            expiresAt = info.ExpiresAt.Value;
            return true;
        }

        /// <summary>
        /// Минимальный толерантный разбор значения claim верхнего уровня: строка
        /// (с экранированием), число, <c>true</c>/<c>false</c> или массив (тогда
        /// возвращается его сырое содержимое — достаточно для проверки <c>aud</c>).
        /// Пустая строка, если claim не найден.
        /// </summary>
        internal static string ReadClaim(string json, string name)
        {
            var needle = "\"" + name + "\"";
            int from = 0;
            while (true)
            {
                int k = json.IndexOf(needle, from, StringComparison.Ordinal);
                if (k < 0) return string.Empty;
                int i = k + needle.Length;
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i >= json.Length || json[i] != ':')
                {
                    from = k + needle.Length; // это было значение, а не ключ — ищем дальше
                    continue;
                }
                i++;
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i >= json.Length) return string.Empty;

                char c = json[i];
                if (c == '"') return ReadString(json, i);
                if (c == '[')
                {
                    int end = json.IndexOf(']', i);
                    return end > i ? json.Substring(i + 1, end - i - 1) : string.Empty;
                }

                int start = i;
                while (i < json.Length && json[i] != ',' && json[i] != '}' && !char.IsWhiteSpace(json[i])) i++;
                var raw = json.Substring(start, i - start);
                return raw == "null" ? string.Empty : raw;
            }
        }

        private static string ReadString(string json, int quoteIndex)
        {
            var sb = new StringBuilder();
            for (int i = quoteIndex + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"') return sb.ToString();
                if (c != '\\' || i + 1 >= json.Length)
                {
                    sb.Append(c);
                    continue;
                }

                char e = json[++i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 < json.Length &&
                            int.TryParse(json.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out var code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(e); break; // \" \\ \/
                }
            }
            return sb.ToString();
        }
    }
}
