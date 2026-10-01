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

    /// <summary>
    /// The stored half of the panel's Pwsh tab: PowerShell commands pasted, copied or kept from the tab
    /// (<see cref="Model.ClipOrigin.PowerShell"/>, or a row such a keep bumped, whose source app became "PowerShell").
    /// </summary>
    /// <remarks>
    /// The tab also lists every command in PowerShell's history file, read live; those are not history entries, so
    /// this filter alone (the command line's <c>-f pwsh</c>) never returns them.
    /// </remarks>
    PowerShell = 9,

    /// <summary>
    /// The stored half of the panel's Cmd tab: Command Prompt commands pasted, copied or kept from the tab
    /// (<see cref="Model.ClipOrigin.Cmd"/>, or a row such a keep bumped, whose source app became "Command Prompt").
    /// </summary>
    /// <remarks>The commands read from cmd windows (and kept after they closed) are not history entries.</remarks>
    Cmd = 10,

    /// <summary>
    /// The stored half of the panel's Claude tab: prompts pasted, copied or kept from the tab
    /// (<see cref="Model.ClipOrigin.ClaudeCode"/>, or a row such a keep bumped), and anything Claude Code itself put on the
    /// clipboard (source app "Claude Code": its executable describes itself so).
    /// </summary>
    /// <remarks>
    /// The tab also lists every prompt in the prompt archive; those are not history entries, so this filter alone (the
    /// command line's <c>-f claude</c>) never returns them — <c>bclip prompts</c> does.
    /// </remarks>
    ClaudeCode = 11,

    /// <summary>
    /// The stored half of the panel's Codex tab: prompts pasted, copied or kept from the tab
    /// (<see cref="Model.ClipOrigin.Codex"/>, or a row such a keep bumped, whose source app became "Codex").
    /// </summary>
    /// <remarks>The archived prompts are not history entries (see <see cref="ClaudeCode"/>).</remarks>
    Codex = 12,
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
    /// but a regular expression scans the whole slice (filter, <see cref="GroupIds"/> and <see cref="UsedSince"/> still
    /// narrow it).
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
    /// Only entries in at least one of these <see cref="Model.ClipGroup"/>s: the panel's selected group icons (one, or
    /// several picked with Ctrl+click or Shift+click); <see langword="null"/> or empty = the regular view over the
    /// whole history. Combines with <see cref="Filter"/>, <see cref="SearchText"/>, <see cref="SearchOptions"/> and
    /// <see cref="UsedSince"/> (e.g. the images of two groups, or a search inside them).
    /// </summary>
    /// <remarks>
    /// The groups are merged, not listed one after the other: an entry in several of them is returned once, and the
    /// page is ordered like any other page (<see cref="PinnedFirst"/>, then most recently used), never group by group,
    /// so paging stays a plain offset over one list. Ids of groups that no longer exist match nothing, and duplicate
    /// ids are ignored.
    /// </remarks>
    public IReadOnlyList<long>? GroupIds { get; init; }
}
