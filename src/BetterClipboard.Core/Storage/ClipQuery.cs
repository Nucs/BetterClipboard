namespace BetterClipboard.Core.Storage;

/// <summary>
/// Which slice of history a query returns (the filter pills in the flyout).
/// </summary>
public enum ClipFilter
{
    /// <summary>Everything.</summary>
    All = 0,

    /// <summary>Only pinned entries.</summary>
    Pinned = 1,

    /// <summary>Text-like entries: plain text, rich text and color literals.</summary>
    Text = 2,

    /// <summary>Image entries.</summary>
    Images = 3,

    /// <summary>Link entries.</summary>
    Links = 4,

    /// <summary>File-list entries.</summary>
    Files = 5,
}

/// <summary>
/// A page request against the history.
/// </summary>
/// <remarks>
/// Paging is offset-based: cheap for the few hundred rows a user actually scrolls through, but items
/// arriving while paging shift offsets by one — the UI tolerates that by de-duplicating on
/// <see cref="Model.ClipEntry.Id"/> when appending pages.
/// </remarks>
public sealed record ClipQuery
{
    /// <summary>
    /// Free-text search. Whitespace-separated terms are ANDed; terms of 3+ characters use the trigram
    /// full-text index (substring semantics), shorter terms fall back to a <c>LIKE</c> scan.
    /// <see langword="null"/>/blank means no search.
    /// </summary>
    public string? SearchText { get; init; }

    /// <summary>The slice to return.</summary>
    public ClipFilter Filter { get; init; } = ClipFilter.All;

    /// <summary>Rows to skip (paging).</summary>
    public int Offset { get; init; }

    /// <summary>Maximum rows to return; clamped to 1–1000 by the store.</summary>
    public int Limit { get; init; } = 100;

    /// <summary>Whether pinned entries sort before unpinned ones (otherwise pure recency).</summary>
    public bool PinnedFirst { get; init; } = true;

    /// <summary>
    /// Only entries last copied or pasted at or after this instant (e.g. "what did I copy in the last
    /// hour" from the command line); <see langword="null"/> = no time limit. Compared against
    /// <see cref="Model.ClipEntry.LastUsedUtc"/>, so an old item copied again counts as recent.
    /// </summary>
    public DateTimeOffset? UsedSince { get; init; }
}
