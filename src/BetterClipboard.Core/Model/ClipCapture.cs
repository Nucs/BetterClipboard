namespace BetterClipboard.Core.Model;

/// <summary>
/// A raw snapshot of clipboard content handed from the platform layer (live listener or an importer) to
/// the capture pipeline. It is not yet classified, deduplicated or persisted.
/// </summary>
/// <remarks>
/// Captures are immutable once built and may cross threads freely (listener thread → service worker).
/// Keep them small: the listener already dropped formats over the size cap, and nothing here should be
/// retained after <see cref="Services.ClipHistoryService"/> has stored it.
/// </remarks>
public sealed class ClipCapture
{
    /// <summary>
    /// The captured formats in the clipboard's enumeration order (most descriptive first — the order
    /// they will be replayed in). Privacy marker formats are never included.
    /// </summary>
    public required IReadOnlyList<ClipFormatData> Formats { get; init; }

    /// <summary>
    /// When the content was copied. For live captures this is "now"; imports carry the original
    /// Windows timestamp so imported items slot into history chronologically.
    /// </summary>
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The application that produced the content, when known.</summary>
    public SourceAppInfo? Source { get; init; }

    /// <summary>Where the capture came from; controls duplicate-merge behavior (see <see cref="ClipOrigin"/>).</summary>
    public ClipOrigin Origin { get; init; } = ClipOrigin.Captured;

    /// <summary>
    /// Request to pin the stored entry (used when importing items the user pinned in Win+V). Pinning is
    /// only ever added by a capture, never removed — an import cannot unpin something the user pinned here.
    /// </summary>
    public bool Pin { get; init; }

    /// <summary>
    /// Finds a format by canonical name.
    /// </summary>
    /// <param name="name">Canonical format name (ordinal comparison).</param>
    /// <returns>The first matching format, or <see langword="null"/> when absent.</returns>
    public ClipFormatData? Find(string name)
    {
        foreach (var format in Formats)
        {
            if (string.Equals(format.Name, name, StringComparison.Ordinal))
            {
                return format;
            }
        }

        return null;
    }

    /// <summary>Total payload size across all formats, in bytes (what the entry will cost on disk).</summary>
    public long TotalBytes
    {
        get
        {
            long total = 0;
            foreach (var format in Formats)
            {
                total += format.Data.LongLength;
            }

            return total;
        }
    }
}
