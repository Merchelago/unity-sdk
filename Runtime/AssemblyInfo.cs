using System.Runtime.CompilerServices;

// Editor-сборка SDK (окна «VHR → Тестирование в Editor» и «VHR → Каталог игры»,
// проверка сборки на утечку песочного ключа) использует внутренние Editor-only
// хелперы рантайм-сборки (VhrEditorSandbox, VhrJwt). В публичный API они не выходят.
[assembly: InternalsVisibleTo("VhrGames.Sdk.Editor")]
