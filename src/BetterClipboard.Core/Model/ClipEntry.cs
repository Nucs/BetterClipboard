namespace BetterClipboard.Core.Model;

/// <summary>
/// An immutable snapshot of one persisted history row (without its format payloads, which are loaded on
/// demand via <see cref="Storage.ClipStore.GetFormats"/> because they can be megabytes).
/// </summary>
/// <remarks>
/// Snapshots never change after being read; mutations (pin, touch, delete) go through the store and a
/// fresh snapshot is read back. That makes entries safe to hand to the UI thread without locking.
/// </remarks>
public sealed class ClipEntry
{
    /// <summary>Database identity (<c>clips.id</c>); stable for the entry's lifetime, never reused.</summary>
    public long Id { get; init; }

    /// <summary>Presentation category decided at capture time.</summary>
    public ClipKind Kind { get; init; }

    /// <summary>Short display text (text head, file names, or an image caption); never the full payload.</summary>
    public string Preview { get; init; } = string.Empty;

    /// <summary>SHA-256 (lowercase hex) of the primary content; the deduplication key.</summary>
    public string ContentHash { get; init; } = string.Empty;

    /// <summary>When the content was first seen (copy time for imports).</summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>When the content was last copied or pasted; the recency sort key.</summary>
    public DateTimeOffset LastUsedUtc { get; init; }

    /// <summary>How many times the content was copied/pasted (1 for a fresh entry).</summary>
    public int UseCount { get; init; }

    /// <summary>Pinned entries are exempt from every retention rule and survive "clear history".</summary>
    public bool IsPinned { get; init; }

    /// <summary>
    /// Ids of the <see cref="ClipGroup"/>s the entry belongs to, ascending; empty when it is in none. Any
    /// membership protects the entry from retention and "clear history" like a pin (see <see cref="IsGrouped"/>).
    /// </summary>
    public IReadOnlyList<long> GroupIds { get; init; } = [];

    /// <summary>Whether the entry is in at least one group — and therefore kept like a pinned entry.</summary>
    public bool IsGrouped => GroupIds.Count > 0;

    /// <summary>Where the entry was first created from.</summary>
    public ClipOrigin Origin { get; init; }

    /// <summary>Friendly name of the source application, when known.</summary>
    public string? SourceAppName { get; init; }

    /// <summary>Executable path of the source application, when known (used for icons and "ignore app").</summary>
    public string? SourceAppPath { get; init; }

    /// <summary>Total stored payload size in bytes across all formats.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Stored format names in replay order (for badges such as HTML/RTF/PNG).</summary>
    public IReadOnlyList<string> FormatNames { get; init; } = [];

    /// <summary>Pixel width for image entries; <see langword="null"/> otherwise or when undecodable.</summary>
    public int? ImageWidth { get; init; }

    /// <summary>Pixel height for image entries; <see langword="null"/> otherwise or when undecodable.</summary>
    public int? ImageHeight { get; init; }

    /// <summary>Whether a PNG thumbnail exists (fetch it with <see cref="Storage.ClipStore.GetThumbnail"/>).</summary>
    public bool HasThumbnail { get; init; }

    /// <summary>
    /// How many file-system paths the entry's text consists of when it is nothing but paths
    /// (<see cref="Content.PathDetector"/>), else 0. A count above 0 lists a text entry under the Files filter
    /// next to real file lists; the kind stays text, so it pastes as text. Entries written by an older build
    /// read 0 until the next start recomputes them.
    /// </summary>
    public int PathCount { get; init; }

    /// <summary>Whether the entry's text is nothing but paths (<see cref="PathCount"/> above 0).</summary>
    public bool IsPathText => PathCount > 0;

    /// <summary>
    /// When the entry's text was last run as a Win+R command (seen in Windows' Run history, or run again from
    /// the panel); <see langword="null"/> when it never was. Set, it lists the entry in the Run tab whatever its
    /// origin — text copied first and run later is one entry. Times of commands Windows remembered from before
    /// BetterClipboard watched are approximate (Windows keeps only the newest run time).
    /// </summary>
    public DateTimeOffset? LastRunUtc { get; init; }

    /// <summary>Whether the entry was ever run as a Win+R command (<see cref="LastRunUtc"/> set): it can be run again.</summary>
    public bool HasRunHistory => LastRunUtc is not null;

    /// <summary>Whether any rich text format (HTML/RTF) is stored, i.e. "paste as plain text" differs from paste.</summary>
    public bool HasRichFormats =>
        FormatNames.Contains(ClipFormatNames.Html) || FormatNames.Contains(ClipFormatNames.Rtf);
}
