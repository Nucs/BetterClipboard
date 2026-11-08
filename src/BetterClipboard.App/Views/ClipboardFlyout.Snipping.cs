using BetterClipboard.Core.Storage;

namespace BetterClipboard.App.Views;

/// <summary>
/// The Snipping tab's half of the panel (CLAUDE.md §2.18): showing the tab and keeping an open Snipping tab current.
/// Its cards are ordinary image entries, so every card action is the main file's.
/// </summary>
public sealed partial class ClipboardFlyout
{
    /// <summary>
    /// The Screenshots folder watch started or stopped, the setting changed, a screenshot came in, or the history gained its
    /// first picture for the tab or lost its last (UI thread): show or hide the tab, and prepare the hidden panel for it
    /// when it is the tab the user last chose and has just come (back). A new screenshot reaches an open tab through the
    /// history's own change events, like any copy.
    /// </summary>
    /// <param name="sender">Controller.</param>
    /// <param name="e">Unused.</param>
    private void OnSnippingStatusChanged(object? sender, EventArgs e)
    {
        UpdateSnippingTab();
        ReturnToRememberedTabWhileConcealed();
    }

    /// <summary>
    /// Shows the Snipping tab while Settings › Snipping Tool and Win+PrtScn is on and the history holds a picture the tab
    /// lists (<see cref="AppController.IsSnippingTabAvailable"/>). If it disappears while selected — switched off, or its
    /// last picture deleted from the open tab — the panel falls back to "All", so the list never stays filtered by an
    /// invisible tab. The tab strip scrolls when the tabs no longer fit (its arrows follow the strip's new width by
    /// themselves).
    /// </summary>
    private void UpdateSnippingTab() => SetTabVisible(SnippingFilter, controller.IsSnippingTabAvailable, ClipFilter.Snipping);
}
