using System.Text;
using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.Content;

/// <summary>
/// The classification of a capture: what it is, how it is shown, how it is searched and how it is deduplicated.
/// </summary>
/// <param name="Kind">Presentation category.</param>
/// <param name="Preview">Display text, already truncated to <see cref="ContentClassifier.PreviewMaxChars"/>.</param>
/// <param name="SearchText">Text fed to the full-text index, truncated to <see cref="ContentClassifier.SearchMaxChars"/>.</param>
/// <param name="ContentHash">Deduplication key from <see cref="ContentHasher"/>.</param>
/// <param name="PlainText">Full decoded text when the clip has text; <see langword="null"/> otherwise.</param>
/// <param name="FilePaths">Decoded file list for <see cref="ClipKind.Files"/>; empty otherwise.</param>
public sealed record ClassifiedClip(
    ClipKind Kind,
    string Preview,
    string SearchText,
    string ContentHash,
    string? PlainText,
    IReadOnlyList<string> FilePaths);

/// <summary>
/// Turns a raw <see cref="ClipCapture"/> into a <see cref="ClassifiedClip"/>. Pure function — no I/O, no
/// platform calls — so the decision table below is fully unit-tested.
/// </summary>
/// <remarks>
/// Priority order (first match wins), chosen to match what the user meant by the copy:
/// <list type="number">
/// <item><b>Files</b> — Explorer copies carry <c>CF_HDROP</c> (sometimes plus name text); the list is the meaning.</item>
/// <item><b>Text</b> — refined to Link / Color / RichText. Office and browsers attach a bitmap rendering to
/// text copies; that bitmap is kept for paste fidelity but the clip is still text.</item>
/// <item><b>Image</b> — bitmaps without text (screenshots, "Copy image").</item>
/// <item><b>Rich-only</b> — HTML/RTF without plain text (rare; some web apps).</item>
/// </list>
/// Anything else (e.g. only private app formats) is not a clip and yields <see langword="null"/>.
/// </remarks>
public static class ContentClassifier
{
    /// <summary>Maximum preview length in characters; the card shows only a few lines anyway.</summary>
    public const int PreviewMaxChars = 1000;

    /// <summary>
    /// Maximum characters indexed for search. Bounds the trigram index (≈3 index entries per char) so one
    /// pasted 50 MB log cannot bloat the database; text past the cap is stored but not searchable.
    /// </summary>
    public const int SearchMaxChars = 32 * 1024;

    /// <summary>
    /// Classifies a capture.
    /// </summary>
    /// <param name="capture">The raw capture from the listener or an importer.</param>
    /// <returns>The classification, or <see langword="null"/> when the capture has nothing we can show or replay.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="capture"/> is <see langword="null"/>.</exception>
    public static ClassifiedClip? Classify(ClipCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);

        var hdrop = capture.Find(ClipFormatNames.HDrop);
        if (hdrop is not null)
        {
            var paths = DropFilesCodec.Decode(hdrop.Data);
            if (paths.Count > 0)
            {
                return new ClassifiedClip(
                    ClipKind.Files,
                    DescribeFiles(paths),
                    Truncate(string.Join('\n', paths), SearchMaxChars),
                    ContentHasher.ForFiles(paths),
                    null,
                    paths);
            }
        }

        var textFormat = capture.Find(ClipFormatNames.UnicodeText);
        var text = textFormat is null ? null : UnicodeTextCodec.Decode(textFormat.Data);
        bool hasRich = capture.Find(ClipFormatNames.Html) is not null || capture.Find(ClipFormatNames.Rtf) is not null;
        if (!string.IsNullOrEmpty(text))
        {
            var kind = LinkDetector.TryGetLink(text, out _) ? ClipKind.Link
                : ColorLiteral.TryParse(text, out _) ? ClipKind.Color
                : hasRich ? ClipKind.RichText
                : ClipKind.Text;
            return new ClassifiedClip(
                kind,
                BuildTextPreview(text),
                Truncate(text, SearchMaxChars),
                ContentHasher.ForText(text),
                text,
                []);
        }

        var image = FindPrimaryImage(capture);
        if (image is not null)
        {
            return new ClassifiedClip(ClipKind.Image, DescribeImage(null, null), string.Empty, ContentHasher.ForImage(image.Data), null, []);
        }

        var rich = capture.Find(ClipFormatNames.Html) ?? capture.Find(ClipFormatNames.Rtf);
        if (rich is not null && rich.Data.Length > 0)
        {
            return new ClassifiedClip(ClipKind.RichText, "Formatted content", string.Empty, ContentHasher.ForRichOnly(rich.Data), null, []);
        }

        return null;
    }

    /// <summary>
    /// Picks the image format used for hashing and thumbnails: PNG first (lossless and alpha-correct
    /// from browsers/Office), then DIBV5 (alpha-capable), then plain DIB.
    /// </summary>
    /// <param name="capture">The capture to inspect.</param>
    /// <returns>The chosen format, or <see langword="null"/> when no image format is present.</returns>
    public static ClipFormatData? FindPrimaryImage(ClipCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return capture.Find(ClipFormatNames.Png)
            ?? capture.Find(ClipFormatNames.PngMime)
            ?? capture.Find(ClipFormatNames.DibV5)
            ?? capture.Find(ClipFormatNames.Dib);
    }

    /// <summary>
    /// Builds the caption shown for image clips.
    /// </summary>
    /// <param name="width">Pixel width when known.</param>
    /// <param name="height">Pixel height when known.</param>
    /// <returns>"Image" or "Image · W × H".</returns>
    public static string DescribeImage(int? width, int? height) =>
        width is > 0 && height is > 0 ? $"Image · {width} × {height}" : "Image";

    /// <summary>
    /// Builds the preview for a file list: up to five file names, one per line, plus a "+N more" tail.
    /// </summary>
    /// <param name="paths">Decoded paths (non-empty).</param>
    /// <returns>The preview text.</returns>
    public static string DescribeFiles(IReadOnlyList<string> paths)
    {
        const int shown = 5;
        var builder = new StringBuilder();
        for (int i = 0; i < paths.Count && i < shown; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            // Trailing separators (folders copied from some shells) would make GetFileName return "".
            var trimmed = paths[i].TrimEnd('\\', '/');
            var name = Path.GetFileName(trimmed);
            builder.Append(string.IsNullOrEmpty(name) ? trimmed : name);
        }

        if (paths.Count > shown)
        {
            builder.Append('\n').Append('+').Append(paths.Count - shown).Append(" more");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Normalizes text for display: unified <c>\n</c> newlines, leading blank lines and trailing
    /// whitespace removed, truncated with an ellipsis. Whitespace-only text gets a visible placeholder
    /// so the card is never blank.
    /// </summary>
    /// <param name="text">Full clip text.</param>
    /// <returns>The preview string.</returns>
    public static string BuildTextPreview(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return $"(whitespace · {text.Length} char{(text.Length == 1 ? "" : "s")})";
        }

        // Only normalize the head we will display — normalizing a 50 MB string just to show 1000 chars
        // would burn CPU on the capture path.
        var head = text.Length > PreviewMaxChars * 2 ? text[..(PreviewMaxChars * 2)] : text;
        head = head.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        head = head.TrimEnd();
        int firstContent = 0;
        while (firstContent < head.Length && (head[firstContent] == '\n' || head[firstContent] == ' ' || head[firstContent] == '\t'))
        {
            // Skip leading blank lines but keep the indentation of the first real line.
            int lineEnd = head.IndexOf('\n', firstContent);
            if (lineEnd < 0 || !string.IsNullOrWhiteSpace(head[firstContent..lineEnd]))
            {
                break;
            }

            firstContent = lineEnd + 1;
        }

        head = head[firstContent..];
        bool truncated = head.Length > PreviewMaxChars || text.Length > PreviewMaxChars * 2;
        return truncated ? Truncate(head, PreviewMaxChars - 1) + "…" : head;
    }

    /// <summary>Cuts <paramref name="text"/> to at most <paramref name="max"/> chars without splitting a surrogate pair.</summary>
    /// <param name="text">Input text.</param>
    /// <param name="max">Maximum length in UTF-16 code units.</param>
    /// <returns>The possibly shortened text.</returns>
    private static string Truncate(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        // Splitting between a high and low surrogate would produce an invalid string that SQLite's UTF-8
        // conversion replaces with U+FFFD; step back one unit instead.
        int cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..cut];
    }
}
