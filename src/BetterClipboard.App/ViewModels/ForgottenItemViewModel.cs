using BetterClipboard.Core.Model;
using BetterClipboard.Core.Presentation;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// One row of Settings › Forgotten forever: what the item was and what happened since. Immutable — the whole
/// list is rebuilt from the store on every change (it is small), which also refreshes the relative times.
/// </summary>
/// <param name="Id">The list entry id for "Allow again".</param>
/// <param name="Title">E.g. "Text · 23 characters".</param>
/// <param name="Details">E.g. "From Chrome · forgotten 5 min ago · kept out 3 times, last just now".</param>
public sealed record ForgottenItemViewModel(long Id, string Title, string Details)
{
    /// <summary>
    /// Accessible name of the row's button. Every row has an "Allow again" button, so the name says which
    /// item it is for — otherwise a screen reader (or UI Automation) sees a column of identical buttons.
    /// </summary>
    public string AllowAgainName => $"Allow again: {Title}";

    /// <summary>
    /// Builds a row from a store entry.
    /// </summary>
    /// <param name="item">The entry.</param>
    /// <param name="now">The reference "now" for relative times.</param>
    /// <returns>The row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is <see langword="null"/>.</exception>
    public static ForgottenItemViewModel From(ForgottenItem item, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new ForgottenItemViewModel(item.Id, ForgottenText.Title(item), ForgottenText.Details(item, now));
    }
}
