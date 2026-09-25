namespace BetterClipboard.Core.Storage;

/// <summary>
/// Limits applied to <b>unpinned</b> history by <see cref="ClipStore.Prune"/>. Pinned entries are never pruned.
/// </summary>
/// <param name="MaxItems">Keep at most this many unpinned entries (most recently used win); 0 = unlimited.</param>
/// <param name="MaxAge">Drop unpinned entries not used for longer than this; <see langword="null"/> = keep forever.</param>
/// <param name="MaxTotalBytes">Cap the total payload size of unpinned entries (oldest dropped first); 0 = unlimited.</param>
public sealed record RetentionPolicy(int MaxItems, TimeSpan? MaxAge, long MaxTotalBytes)
{
    /// <summary>No limits at all — useful for imports and tests.</summary>
    public static RetentionPolicy Unlimited { get; } = new(0, null, 0);
}
