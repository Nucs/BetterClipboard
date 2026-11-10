using System.Runtime.CompilerServices;
using BetterClipboard.Core.Localization;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Keeps this project's English assertions independent of the machine's display language, as the Core tests do: the
/// Windows helpers word their statuses through <see cref="UiText"/> (Explorer's shortcut plan, for one), and on a
/// Chinese Windows "system" would turn them Chinese and fail tests such as HotkeyTakeoverTests.
/// </summary>
internal static class TestLanguage
{
    /// <summary>Selects English before any test runs.</summary>
    [ModuleInitializer]
    internal static void Initialize() => UiText.Language = UiText.EnglishTag;
}
