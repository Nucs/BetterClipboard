using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.Services;

/// <summary>What happened to the history.</summary>
public enum ClipChangeKind
{
    /// <summary>A new entry was stored; <see cref="ClipChangedEventArgs.Entry"/> is set.</summary>
    Added,

    /// <summary>An existing entry changed (recency, pin, formats); <see cref="ClipChangedEventArgs.Entry"/> is set.</summary>
    Updated,

    /// <summary>An entry was deleted; <see cref="ClipChangedEventArgs.EntryId"/> is set.</summary>
    Removed,

    /// <summary>Many entries changed at once (clear, prune, import batch) — reload everything.</summary>
    Reset,
}

/// <summary>
/// Notification raised by <see cref="ClipHistoryService.Changed"/>. Raised on the history worker
/// thread — UI subscribers must marshal to their dispatcher.
/// </summary>
public sealed class ClipChangedEventArgs : EventArgs
{
    /// <summary>
    /// Creates the event data.
    /// </summary>
    /// <param name="kind">What happened.</param>
    /// <param name="entry">The affected entry for <see cref="ClipChangeKind.Added"/>/<see cref="ClipChangeKind.Updated"/>.</param>
    /// <param name="entryId">The affected id (always set when a single entry is involved).</param>
    public ClipChangedEventArgs(ClipChangeKind kind, ClipEntry? entry = null, long? entryId = null)
    {
        Kind = kind;
        Entry = entry;
        EntryId = entryId ?? entry?.Id;
    }

    /// <summary>What happened.</summary>
    public ClipChangeKind Kind { get; }

    /// <summary>The entry as it is now, for adds and updates.</summary>
    public ClipEntry? Entry { get; }

    /// <summary>The affected entry id, when a single entry is involved.</summary>
    public long? EntryId { get; }
}
