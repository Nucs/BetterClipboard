namespace BetterClipboard.Core.Model;

/// <summary>
/// Where a clip came from. Persisted as an integer (<c>clips.origin</c>) — append only, never renumber.
/// </summary>
/// <remarks>
/// The origin changes how duplicates are merged: a live <see cref="Captured"/> copy of existing content
/// bumps it to the top (the user just copied it again), while an import of existing content must
/// <b>not</b> reorder history — imports run at every startup and would otherwise shuffle old items back
/// to the top each time.
/// </remarks>
public enum ClipOrigin
{
    /// <summary>Observed live by our clipboard listener.</summary>
    Captured = 0,

    /// <summary>Imported from the WinRT <c>Clipboard.GetHistoryItemsAsync()</c> history (Win+V).</summary>
    WindowsHistory = 1,

    /// <summary>Imported by decrypting Windows' on-disk pinned store (<c>...\Clipboard\Pinned</c>).</summary>
    WindowsPinned = 2,

    /// <summary>
    /// A screenshot ShareX saved to one of its screenshot folders, picked up by the ShareX integration.
    /// </summary>
    /// <remarks>
    /// Hybrid on purpose. Like <see cref="Captured"/> it is a new event, so an existing duplicate is bumped
    /// and paused capture skips it. Like an import it never lifts a tombstone and is skipped when older
    /// than the last "clear history": the startup catch-up scan can rediscover files, and that must not
    /// bring back a screenshot the user deleted.
    /// </remarks>
    ShareX = 3,

    /// <summary>
    /// A command the user ran with Win+R (the Run dialog) — seen as a change of Windows' Run history while
    /// BetterClipboard watched it, found by a rescan since the last one, or run again from the panel.
    /// </summary>
    /// <remarks>
    /// A new event like <see cref="Captured"/>: an existing duplicate is bumped (the user just ran it again),
    /// paused capture skips it, and it lifts a tombstone — running something again is as explicit as copying
    /// it again. Safe because a rescan only ever reports runs newer than its stored snapshot of the Run
    /// history, never the old list (that arrives as <see cref="RunDialogHistory"/>). The store marks it with
    /// a run time, which puts it in the Run tab.
    /// </remarks>
    RunDialog = 4,

    /// <summary>
    /// A Win+R command Windows still remembered when the Run history was first read (the feature switched on
    /// for this history) — run at some unknown time before BetterClipboard watched.
    /// </summary>
    /// <remarks>
    /// An import: an existing duplicate is not reordered (it only gains a run time), pause does not apply,
    /// and it never resurrects content the user deleted or cleared — switching the feature off and on again
    /// re-reads the same old list and must not bring deleted commands back.
    /// </remarks>
    RunDialogHistory = 5,

    /// <summary>
    /// A file or folder from the panel's Everything tab (something the user opened in voidtools Everything)
    /// that the user pasted, copied or kept there (pin, group, forget).
    /// </summary>
    /// <remarks>
    /// Always the result of an explicit action, never of a background scan, so it behaves exactly like a live
    /// <see cref="Captured"/> copy: an existing duplicate is bumped, a tombstone is lifted (the user asked for
    /// it again), and pause, ignored apps, the size limit and "Forget forever" apply. Only the files the user
    /// acts on are stored; the rest of Everything's run history stays in Everything, shown live by the tab.
    /// </remarks>
    Everything = 6,

    /// <summary>
    /// A PowerShell command from the panel's Pwsh tab (read live from PowerShell's history file) that the user
    /// pasted, copied or kept there (pin, group).
    /// </summary>
    /// <remarks>
    /// An explicit action, never a background scan, so it behaves like a live <see cref="Captured"/> copy (the
    /// <see cref="Everything"/> rules): a duplicate is bumped, a tombstone is lifted, and pause, ignored apps
    /// ("PowerShell"), the size limit and "Forget forever" apply. The thousands of commands in the file stay there —
    /// only the ones acted on become history (see <see cref="Shells.ShellTab"/>).
    /// </remarks>
    PowerShell = 7,

    /// <summary>
    /// A Command Prompt command from the panel's Cmd tab (read from open cmd windows, or kept by BetterClipboard
    /// after its window closed) that the user pasted, copied or kept there.
    /// </summary>
    /// <remarks>Behaves exactly like <see cref="PowerShell"/>; ignored apps match "cmd".</remarks>
    Cmd = 8,
}
