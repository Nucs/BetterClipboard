using BetterClipboard.Core.Storage;

namespace BetterClipboard.App.Views;

/// <summary>
/// The Snipping tab's half of the panel (CLAUDE.md §2.18): showing the tab and keeping an open Snipping tab current.
/// Its cards are ordinary image entries, so every card action is the main file's.
/// </summary>
public sealed partial class ClipboardFlyout
{
    /// <summary>
    /// The Screenshots folder watch started or stopped, the setting changed, or a screenshot came in (UI thread): show or
    /// hide the tab. A new screenshot reaches an open tab through the history's own change events, like any copy.
    /// </summary>
    /// <param name="sender">Controller.</param>
    /// <param name="e">Unused.</param>
    private void OnSnippingStatusChanged(object? sender, EventArgs e) => UpdateSnippingTab(applyWidth: true);

    /// <summary>
    /// Shows the Snipping tab while Settings › Snipping Tool and Win+PrtScn is on. If it disappears while selected, the
    /// panel falls back to "All", so the list never stays filtered by an invisible tab.
    /// </summary>
    /// <param name="applyWidth">
    /// Refit the open window to the visible tabs when the tab appeared or disappeared (<see langword="false"/> from
    /// <see cref="ShowAt"/>, which measures the tabs itself right after).
    /// </param>
    private void UpdateSnippingTab(bool applyWidth)
    {
        if (SetTabVisible(SnippingFilter, controller.IsSnippingTabAvailable, ClipFilter.Snipping) && applyWidth && IsOpen)
        {
            // The bar does not scroll: the window grows or shrinks so every visible tab fits.
            ApplyTabsWidth();
        }
    }
}
