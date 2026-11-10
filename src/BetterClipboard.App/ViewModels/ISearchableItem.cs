namespace BetterClipboard.App.ViewModels;

/// <summary>
/// A row of a Settings list (a shortcut, a forgotten item, a third-party project, a language) that tells Settings' search
/// what it shows, so typing "sqlite" finds the card whose list holds that row (the owner's request: search "anything of
/// that setting").
/// </summary>
/// <remarks>
/// The search reads rows from their lists' items, not from the screen: a combo box shows only its chosen item, and a row
/// scrolled out of a bounded list is not drawn, yet both belong to the setting.
/// </remarks>
public interface ISearchableItem
{
    /// <summary>The texts the row shows, in the interface language (blank ones are skipped).</summary>
    IEnumerable<string> SearchTexts { get; }
}
