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

    /// <summary>
    /// File lists (files copied in Explorer) and text entries that are nothing but file-system paths
    /// (<see cref="Model.ClipEntry.PathCount"/> above 0, e.g. <c>C:\a\b.txt</c> or <c>src/app.cs</c> copied as
    /// text) — the latter also stay under <see cref="Text"/>.
    /// </summary>
    Files = 5,

    /// <summary>
    /// Everything that came from ShareX: screenshots picked up from its folders (<see cref="Model.ClipOrigin.ShareX"/>)
    /// and anything ShareX put on the clipboard (source app <c>ShareX.exe</c>).
    /// </summary>
    ShareX = 6,

    /// <summary>
    /// Commands run with Win+R (the Run dialog): every entry with a run time (<see cref="Model.ClipEntry.LastRunUtc"/>),
    /// whatever its origin — kept here long after Windows' own list of 26 forgot them.
    /// </summary>
    Run = 7,

    /// <summary>
    /// The stored half of the panel's Everything tab: items pasted, copied or kept from the tab
    /// (<see cref="Model.ClipOrigin.Everything"/>) and anything voidtools Everything put on the clipboard (source
    /// app <c>Everything.exe</c>, e.g. Ctrl+C or Ctrl+Shift+C on a result).
    /// </summary>
    /// <remarks>
    /// The tab also lists the files opened in Everything, live from Everything's run history. Those are not
    /// history entries, so this filter alone (the command line's <c>-f everything</c>) never returns them.
    /// </remarks>
    Everything = 8,
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

    /// <summary>
    /// How <see cref="SearchText"/> matches — the search box's "Aa" (match case), "W" (whole word) and ".*" (regular
    /// expression) toggles. <see cref="SearchOptions.None"/>, the default, is the classic search described there.
    /// </summary>
    /// <remarks>
    /// Anything else makes the store run each candidate's indexed text through <see cref="SearchMatcher"/>: exact,
    /// but a regular expression scans the whole slice (filter, group and <see cref="UsedSince"/> still narrow it).
    /// An invalid pattern makes the query throw <see cref="SearchPatternException"/>; a pattern that needs the
    /// backtracking engine and runs too long on one entry, <see cref="SearchTooSlowException"/>.
    /// </remarks>
    public SearchOptions SearchOptions { get; init; }

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

    /// <summary>
    /// Only entries in this <see cref="Model.ClipGroup"/> (the panel's selected group icon);
    /// <see langword="null"/> = the regular view over the whole history. Combines with
    /// <see cref="Filter"/> and <see cref="SearchText"/> (e.g. the images of one group).
    /// </summary>
    public long? GroupId { get; init; }
}
