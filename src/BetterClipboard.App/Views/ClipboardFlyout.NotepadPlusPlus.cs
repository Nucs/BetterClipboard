using BetterClipboard.Core.Storage;

namespace BetterClipboard.App.Views;

/// <summary>
/// The Notepad++ tab's half of the panel (CLAUDE.md §2.41): showing the tab and keeping it current. Its cards are ordinary
/// text entries — a tab's text, or an opened file's path — so every card action is the main file's.
/// </summary>
public sealed partial class ClipboardFlyout
{
    /// <summary>
    /// Something about Notepad++ changed, a setting changed, or the history gained its first entry for the tab or lost its last
    /// (UI thread): show or hide the tab, and prepare the hidden panel for it when it is the tab the user last chose and has
    /// just come (back). A tab's newer text and an opened file reach an open tab through the history's own change events.
    /// </summary>
    /// <param name="sender">Controller.</param>
    /// <param name="e">Unused.</param>
    private void OnNotepadPlusPlusStatusChanged(object? sender, EventArgs e)
    {
        UpdateNotepadPlusPlusTab();
        ReturnToRememberedTabWhileConcealed();
    }

    /// <summary>
    /// Shows the Notepad++ tab while one of its Settings switches is on and the history holds something it lists
    /// (<see cref="AppController.IsNotepadPlusPlusTabAvailable"/>). If it disappears while selected, the panel falls back to
    /// "All", so the list never stays filtered by an invisible tab.
    /// </summary>
    private void UpdateNotepadPlusPlusTab() => SetTabVisible(NotepadPlusPlusFilter, controller.IsNotepadPlusPlusTabAvailable, ClipFilter.NotepadPlusPlus);
}
