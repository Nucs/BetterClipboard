using BetterClipboard.Core.Presentation;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// One row of Settings › Third party: a project's name and credit, what BetterClipboard uses it for, and the official
/// link its button opens. Immutable and built once per Settings window, since the catalog is fixed at build time.
/// </summary>
/// <param name="Name">The project, e.g. "SQLite3 Multiple Ciphers".</param>
/// <param name="Credit">Its maker and terms, e.g. "Ulrich Telle · MIT", shown after the name in secondary text.</param>
/// <param name="Use">What BetterClipboard uses it for, one sentence.</param>
/// <param name="LinkText">The link button's text: the site, e.g. "utelle.github.io" or "github.com/microsoft/CsWinRT".</param>
/// <param name="Link">The official link the button opens in the browser.</param>
public sealed record ThirdPartyItemViewModel(string Name, string Credit, string Use, string LinkText, Uri Link)
{
    /// <summary>
    /// The credit as the second run of the name line. Two spaces in front keep it apart from the name — a bound value,
    /// so XAML's whitespace rules cannot swallow them — without a separator character a screen reader would read out.
    /// </summary>
    public string CreditRun => "  " + Credit;

    /// <summary>
    /// Accessible name of the link button. Every row has one, and bare sites do not say which project they belong to
    /// ("learn.microsoft.com" opens two different ones), so the name leads with the project: "SQLite: sqlite.org".
    /// </summary>
    public string LinkName => $"{Name}: {LinkText}";

    /// <summary>The whole address, for the link's tooltip: the button shows only the site.</summary>
    public string LinkTip => Link.AbsoluteUri;

    /// <summary>
    /// Builds a row from a catalog entry.
    /// </summary>
    /// <param name="project">The entry.</param>
    /// <returns>The row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> is <see langword="null"/>.</exception>
    public static ThirdPartyItemViewModel From(ThirdPartyProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new ThirdPartyItemViewModel(project.Name, project.Credit, project.Use, project.LinkText, project.Link);
    }
}
