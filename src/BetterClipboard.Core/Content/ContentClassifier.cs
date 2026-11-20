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
/// <param name="PathCount">
/// How many paths the text consists of when it is nothing but file-system paths (<see cref="PathDetector"/>),
/// which lists a <see cref="ClipKind.Text"/> or <see cref="ClipKind.RichText"/> clip under the Files filter
/// too; 0 for every other text and for every other kind. It never changes the kind, so the clip still pastes
/// and searches as text.
/// </param>
public sealed record ClassifiedClip(
    ClipKind Kind,
    string Preview,
    string SearchText,
    string ContentHash,
    string? PlainText,
    IReadOnlyList<string> FilePaths,
    int PathCount = 0);

/// <summary>
/// Turns a raw <see cref="ClipCapture"/> into a <see cref="ClassifiedClip"/>. Pure function — no I/O, no
/// platform calls — so the decision table below is fully unit-tested.
/// </summary>
/// <remarks>
/// Priority order (first match wins), chosen to match what the user meant by the copy:
/// <list type="number">
/// <item><b>Files</b> — Explorer copies carry <c>CF_HDROP</c> (sometimes plus name text); the list is the meaning.</item>
/// <item><b>Text</b> — refined to Link / Color / RichText. Office and browsers attach a bitmap rendering to
/// text copies; that bitmap is kept for paste fidelity but the clip is still text. Text that is nothing but
/// paths stays Text/RichText and only gets <see cref="ClassifiedClip.PathCount"/>, which adds it to the Files
/// filter: it was copied as text, so it must keep pasting as text.</item>
/// <item><b>Image</b> — bitmaps without text (screenshots, "Copy image").</item>
/// <item><b>Rich-only</b> — HTML/RTF without plain text (rare; some web apps).</item>
/// </list>
/// Anything else (e.g. only private app formats) is not a clip and yields <see langword="null"/>.
/// </remarks>
public static class ContentClassifier
{
    /// <summary>
    /// The longest preview stored, in characters: enough text for a card of <see cref="Settings.AppSettings.MaxLinesPerCard"/>
    /// lines on a wide screen (forty lines of 400 characters, a monospace line some 3,000 DIPs wide). The card hands its text
    /// block only what its line count can show (<see cref="Presentation.CardPreview"/>), and the text viewer reads the whole
    /// text, never the preview (CLAUDE.md §2.57).
    /// </summary>
    /// <remarks>
    /// <para>
    /// 1,000 until 2026-11-20, when every card showed four lines. When the number of lines became a setting, a long text ran
    /// out of preview after some twenty lines, and the owner's answer was: "The text viewer is supposed to contain the full
    /// text so keep as long as needed". Previews earlier versions cut short are rebuilt once (<c>ClipStore</c>'s
    /// <c>fixup.long_previews.v1</c>); <see cref="ShortPreviewChars"/> keeps the old length where a short preview reads
    /// better. Only a text longer than the old cap stores more than before.
    /// </para>
    /// <para>
    /// Twice this must stay below <see cref="SearchMaxChars"/>, by more than one character: <see cref="BuildTextPreview"/>
    /// looks at twice the cap, and the rebuild works from the indexed text, which holds the first
    /// <see cref="SearchMaxChars"/> characters (or one fewer, when a surrogate pair straddles the cut). Then a preview built
    /// from the indexed text equals the one built from the whole text (a unit test holds the two together).
    /// </para>
    /// </remarks>
    public const int PreviewMaxChars = 16_000;

    /// <summary>
    /// The length of the short preview, in characters (<see cref="ShortPreview"/>): the stored preview's length until
    /// 2026-11-20, kept where a short preview reads better than a long one — a card's name for screen readers, which read it
    /// whole whenever the card is selected, and <c>bclip</c>'s JSON, which scripts and agents read for every item.
    /// </summary>
    /// <remarks>Also the shortest a cut text preview can be (<see cref="IsCutPreview"/>): earlier versions cut there.</remarks>
    public const int ShortPreviewChars = 1000;

    /// <summary>
    /// The most file names a file list's preview holds before its "+N more" line (<see cref="DescribeFiles"/>): as many as a
    /// card shows lines at most (<see cref="Settings.AppSettings.MaxLinesPerCard"/>).
    /// </summary>
    /// <remarks>
    /// Five until 2026-11-20 (<see cref="LegacyPreviewFiles"/>). The caption counts the files from the preview's lines, so a
    /// list of up to this many reads "12 files", and only a longer one "many files".
    /// </remarks>
    public const int PreviewMaxFiles = 40;

    /// <summary>
    /// The file names a file list's preview held before 2026-11-20: five. Entries an earlier version stored keep that shape
    /// until the store rebuilds them, and a version older than the rebuild may store more after it, so
    /// <see cref="Presentation.PreviewText"/> still recognizes it.
    /// </summary>
    public const int LegacyPreviewFiles = 5;

    /// <summary>
    /// Maximum characters indexed for search. Bounds the trigram index (≈3 index entries per char) so one
    /// pasted 50 MB log cannot bloat the database; text past the cap is stored but not searchable.
    /// </summary>
    public const int SearchMaxChars = 32 * 1024;

    /// <summary>
    /// The preview of formatted content without plain text (HTML or RTF alone): there is no text to show, so the card
    /// says what it is.
    /// </summary>
    /// <remarks>
    /// Stored in English, like every preview: <c>bclip</c> prints it as it is, and the card shows it in the interface
    /// language through <see cref="Presentation.PreviewText"/>, which recognizes it by this exact text. Changing it changes
    /// what entries stored from now on say, while older entries keep the old words: change it only together with
    /// <see cref="Presentation.PreviewText"/> and its tests.
    /// </remarks>
    public const string RichOnlyPreview = "Formatted content";

    /// <summary>
    /// The preview of a picture whose size is not known: "Image" (with a size it is "Image · W × H", see
    /// <see cref="DescribeImage(int?, int?)"/>).
    /// </summary>
    /// <remarks>Stored in English and shown translated, like <see cref="RichOnlyPreview"/>; the same footgun applies.</remarks>
    public const string ImagePreview = "Image";

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

            // Links and colors are never paths (a web URL is a link even when it ends in a file name), so only
            // plain and rich text is asked; the detector is linear and rejects long texts up front.
            int pathCount = kind is ClipKind.Text or ClipKind.RichText ? PathDetector.CountPaths(text) : 0;
            return new ClassifiedClip(
                kind,
                BuildTextPreview(text),
                Truncate(text, SearchMaxChars),
                ContentHasher.ForText(text),
                text,
                [],
                pathCount);
        }

        var image = FindPrimaryImage(capture);
        if (image is not null)
        {
            return new ClassifiedClip(ClipKind.Image, DescribeImage(null, null), string.Empty, ContentHasher.ForImage(image.Data), null, []);
        }

        var rich = capture.Find(ClipFormatNames.Html) ?? capture.Find(ClipFormatNames.Rtf);
        if (rich is not null && rich.Data.Length > 0)
        {
            return new ClassifiedClip(ClipKind.RichText, RichOnlyPreview, string.Empty, ContentHasher.ForRichOnly(rich.Data), null, []);
        }

        return null;
    }

    /// <summary>
    /// Whether a classified copy is text made only of whitespace (spaces, tabs, line breaks: <see cref="char.IsWhiteSpace(char)"/>)
    /// with no picture next to it — a copy the history does not keep (<c>ClipHistoryService</c>; CLAUDE.md §2.3).
    /// </summary>
    /// <remarks>
    /// Why such a copy is left out: its card can only read "(whitespace · 1 char)", which is easy to miss in a quick Win+V,
    /// ↓, Enter, and once used it moves up to sit right under the newest copy, where ↓ lands. On 2026-11-17 a single space
    /// copied by accident in Notepad++ was pasted four times that way by someone who believed they had picked text. The copy
    /// itself still reaches the clipboard: only the history skips it. A copy whose text is blank but which carries a picture
    /// (Office and browsers attach a rendering to some copies) is a picture worth keeping, so it does not count.
    /// </remarks>
    /// <param name="classified">The copy's classification (<see cref="Classify"/>).</param>
    /// <param name="capture">The copy itself, asked for a picture format.</param>
    /// <returns><see langword="true"/> for blank text without a picture; <see langword="false"/> for anything else.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="classified"/> or <paramref name="capture"/> is <see langword="null"/>.</exception>
    public static bool IsWhitespaceOnly(ClassifiedClip classified, ClipCapture capture)
    {
        ArgumentNullException.ThrowIfNull(classified);
        ArgumentNullException.ThrowIfNull(capture);
        return classified.Kind is ClipKind.Text or ClipKind.RichText
            && classified.PlainText is { Length: > 0 } text
            && string.IsNullOrWhiteSpace(text)
            && FindPrimaryImage(capture) is null;
    }

    /// <summary>
    /// The image formats <see cref="FindPrimaryImage"/> looks for, most wanted first: what a reader that needs only the
    /// picture asks the store for (<c>ClipStore.GetFirstFormat</c>), so it never reads the DIB next to a PNG.
    /// </summary>
    /// <remarks>Must stay in <see cref="FindPrimaryImage"/>'s order; a unit test holds the two together.</remarks>
    public static IReadOnlyList<string> PrimaryImageFormats { get; } =
        [ClipFormatNames.Png, ClipFormatNames.PngMime, ClipFormatNames.DibV5, ClipFormatNames.Dib];

    /// <summary>
    /// Picks the image format used for hashing and thumbnails: PNG first (lossless and alpha-correct
    /// from browsers/Office), then DIBV5 (alpha-capable), then plain DIB.
    /// </summary>
    /// <param name="capture">The capture to inspect.</param>
    /// <returns>The chosen format, or <see langword="null"/> when no image format is present.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="capture"/> is <see langword="null"/>.</exception>
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
    /// <remarks>
    /// The preview is stored in English and shown translated: <see cref="Presentation.PreviewText"/> recognizes
    /// both shapes. Change them only together with it (its tests run this method's output through it).
    /// </remarks>
    /// <param name="width">Pixel width when known.</param>
    /// <param name="height">Pixel height when known.</param>
    /// <returns>"Image" or "Image · W × H".</returns>
    public static string DescribeImage(int? width, int? height) =>
        width is > 0 && height is > 0 ? $"{ImagePreview} · {width} × {height}" : ImagePreview;

    /// <summary>
    /// Builds the preview for a file list: up to <see cref="PreviewMaxFiles"/> file names, one per line, plus a "+N more"
    /// tail when there are more.
    /// </summary>
    /// <remarks>
    /// The "+N more" line is stored in English and shown translated: <see cref="Presentation.PreviewText"/> recognizes it
    /// as the line after a file list's names (after <see cref="PreviewMaxFiles"/> of them, or <see cref="LegacyPreviewFiles"/>
    /// in an entry stored before 2026-11-20). Change the number of names or the tail only together with it.
    /// </remarks>
    /// <param name="paths">Decoded paths (non-empty).</param>
    /// <returns>The preview text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is <see langword="null"/>.</exception>
    public static string DescribeFiles(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        const int shown = PreviewMaxFiles;
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
    /// <remarks>
    /// That placeholder, "(whitespace · N chars)", is stored in English and shown translated:
    /// <see cref="Presentation.PreviewText"/> recognizes its exact shape. Change it only together with it.
    /// </remarks>
    /// <param name="text">Full clip text.</param>
    /// <returns>The preview string.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static string BuildTextPreview(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return $"(whitespace · {text.Length} char{(text.Length == 1 ? "" : "s")})";
        }

        // Only normalize the head we will display — normalizing a 50 MB string just to show a preview would burn CPU on the
        // capture path. Cut without splitting a surrogate pair: a lone high surrogate at the head's end would survive a
        // preview that normalization shortened below the cap, and SQLite would store it as U+FFFD.
        var head = Truncate(text, PreviewMaxChars * 2);
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

    /// <summary>
    /// A preview cut to <see cref="ShortPreviewChars"/> with "…", the way <see cref="BuildTextPreview"/> cuts: for a screen
    /// reader's card name and <c>bclip</c>'s JSON, which read a preview whole and kept the old length when previews grew.
    /// </summary>
    /// <remarks>
    /// For a text preview it gives what the preview was before 2026-11-20, apart from one edge: a text that earlier versions
    /// cut only for its length (over 2,000 characters raw, but under 1,000 once its white space was trimmed) now comes back
    /// whole, without "…".
    /// </remarks>
    /// <param name="preview">A stored preview, or one shown from it.</param>
    /// <returns>The preview itself when it is short enough; otherwise its start and "…".</returns>
    /// <exception cref="ArgumentNullException"><paramref name="preview"/> is <see langword="null"/>.</exception>
    public static string ShortPreview(string preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return preview.Length <= ShortPreviewChars ? preview : Truncate(preview, ShortPreviewChars - 1) + "…";
    }

    /// <summary>
    /// Whether a stored preview is only the start of its content: a file list's preview that ends with its "+N more" line,
    /// or a text preview cut with "…" (<see cref="BuildTextPreview"/>), now or by an earlier version at
    /// <see cref="ShortPreviewChars"/>. A card whose preview is cut always gets the eye and the dotted line, also when its
    /// text block shows every line of the preview.
    /// </summary>
    /// <remarks>
    /// A text that itself ends with "…" and is at least <see cref="ShortPreviewChars"/> long counts as cut too: its card
    /// offers the viewer for a text it already shows whole, which costs nothing. Ask with the stored preview, which is
    /// English; the shown one translates the file list's last line.
    /// </remarks>
    /// <param name="kind">The entry's kind.</param>
    /// <param name="preview">The stored preview (<c>ClipEntry.Preview</c>).</param>
    /// <returns><see langword="true"/> when the preview leaves part of the content out.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="preview"/> is <see langword="null"/>.</exception>
    public static bool IsCutPreview(ClipKind kind, string preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return kind == ClipKind.Files
            ? preview.Contains("\n+", StringComparison.Ordinal) && preview.EndsWith(" more", StringComparison.Ordinal)
            : preview.Length >= ShortPreviewChars - 1 && preview.EndsWith('…');
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
