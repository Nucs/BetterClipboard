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
}
