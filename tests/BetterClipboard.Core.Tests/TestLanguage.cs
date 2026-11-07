using System.Runtime.CompilerServices;
using BetterClipboard.Core.Localization;

namespace BetterClipboard.Core.Tests;

/// <summary>Keeps existing English presentation assertions independent of the developer machine's display language.</summary>
internal static class TestLanguage
{
    /// <summary>Sets a deterministic baseline; localization tests explicitly select and restore other languages.</summary>
    [ModuleInitializer]
    internal static void Initialize() => UiText.Language = "en";
}
