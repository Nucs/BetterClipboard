using BetterClipboard.Core.Integrations;
using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Shells;

namespace BetterClipboard.Core.Presentation;

/// <summary>
/// Shows the names BetterClipboard itself gives to where a copy came from — "Command Prompt", "Snipping Tool",
/// "Screenshots folder", "PowerShell in VS Code" — in the interface language, while the history keeps them in English.
/// </summary>
/// <remarks>
/// <para>
/// The stored name must stay English: the tabs find their entries by it (<c>ClipFilter.Cmd</c> by "Command Prompt",
/// <c>ClipFilter.Snipping</c> by "Snipping Tool", as SQL literals), and Ignored apps match it. So the translation happens
/// only where a name is shown: the card captions and the forgotten list.
/// </para>
/// <para>
/// Names of other apps come from the apps themselves (their file description, which Windows words in its own language)
/// and pass unchanged; so do product names that are the same in every language ("PowerShell", "Everything", "Win+R",
/// "Win+PrtScn", "ShareX"). A new name BetterClipboard makes up for a source belongs in <see cref="Templates"/> and in
/// every catalog; the tests check the catalogs against this list.
/// </para>
/// </remarks>
public static class SourceNames
{
    /// <summary>
    /// Every source name BetterClipboard makes up that has words to translate, as <see cref="UiText.TranslateFormatted(string?, IReadOnlyList{string})"/>
    /// templates (<c>PowerShell ({0})</c> stands for every PowerShell host without a name of its own).
    /// </summary>
    public static IReadOnlyList<string> Templates { get; } =
    [
        ShellSources.CmdName,
        WindowsScreenshots.SnippingToolName,
        WindowsScreenshots.FolderSourceName,
        PsReadLineHistory.VsCodeHostName,
        PsReadLineHistory.OtherHostTemplate,
    ];

    /// <summary>The name to show for a stored or live source name.</summary>
    /// <param name="name">The source's name as stored (English); <see langword="null"/> gives an empty string.</param>
    /// <returns>Its translation when BetterClipboard made the name up, else the name as it is.</returns>
    public static string Display(string? name) => UiText.TranslateFormatted(name, Templates);
}
