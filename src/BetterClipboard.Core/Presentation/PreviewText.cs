using System.Globalization;
using System.Text.RegularExpressions;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.Presentation;

/// <summary>
/// Shows the few previews BetterClipboard words itself — "Formatted content", "(whitespace · 3 chars)", a file list's
/// "+3 more", a picture's "Image · 1920 × 1080" — in the interface language, while the history keeps them in English.
/// Every other preview is what the user copied and is shown exactly as it is.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ContentClassifier"/> words a preview once, when a copy is stored, and the store keeps it: <c>bclip</c> prints
/// it (in English, like all of its output, for scripts), and the card shows it. Until 2026-11-16 the card showed these
/// words in English in every language. They are translated here, where they are shown, like the source names
/// BetterClipboard makes up (<see cref="SourceNames"/>), and never in the store, so entries stored by any version read
/// the same.
/// </para>
/// <para>
/// A preview is the app's own words only when both its exact shape and the entry say so, so a copy whose text happens to
/// read the same is never translated:
/// </para>
/// <list type="bullet">
/// <item><see cref="ContentClassifier.RichOnlyPreview"/>: formatted text stored without plain text (no
/// <see cref="ClipFormatNames.UnicodeText"/> among its formats), whose card has no text of its own to show.</item>
/// <item>"(whitespace · N chars)": a text or formatted text of nothing but white space
/// (<see cref="ContentClassifier.BuildTextPreview(string)"/>); a copied text reading exactly like that is the one case
/// the shape alone cannot rule out, and it would only lose its English wording.</item>
/// <item><see cref="ContentClassifier.ImagePreview"/> and "Image · W × H": every picture's preview, which is never text
/// the user copied.</item>
/// <item>"+N more": the sixth line of a file list's preview, after the five names
/// (<see cref="ContentClassifier.DescribeFiles(IReadOnlyList{string})"/>); a file's name never stands there.</item>
/// </list>
/// <para>
/// Footgun: changing one of those shapes in <see cref="ContentClassifier"/> must change the matching rule here, or the
/// card shows the new English words in every language. The tests run the classifier's own output through
/// <see cref="Display(ClipKind, string, IReadOnlyList{string})"/> to catch it. Entries stored before such a change keep
/// the old shape, so a rule for an old shape must stay.
/// </para>
/// </remarks>
public static partial class PreviewText
{
    /// <summary>How many file names a file list's preview shows before its "+N more" line (as the classifier words it).</summary>
    private const int FileNamesShown = 5;

    /// <summary>The preview to show for a stored entry, in the interface language when the app worded it.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The preview: the translation of the app's own words, or the user's content as it is.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
    public static string Display(ClipEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Display(entry.Kind, entry.Preview, entry.FormatNames);
    }

    /// <summary>The preview to show for an entry described by its kind, its stored preview and its stored formats.</summary>
    /// <param name="kind">The entry's kind.</param>
    /// <param name="preview">The stored preview (English when the app worded it).</param>
    /// <param name="formatNames">The names of the entry's stored formats (tells formatted text without plain text apart).</param>
    /// <returns>The preview: the translation of the app's own words, or <paramref name="preview"/> as it is.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="preview"/> or <paramref name="formatNames"/> is <see langword="null"/>.</exception>
    public static string Display(ClipKind kind, string preview, IReadOnlyList<string> formatNames)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(formatNames);
        switch (kind)
        {
            case ClipKind.Image:
                return ImageText(preview);
            case ClipKind.Files:
                return FilesText(preview);
            case ClipKind.RichText when preview == ContentClassifier.RichOnlyPreview && !formatNames.Contains(ClipFormatNames.UnicodeText):
                // Formatted content that never had plain text: the words are the app's, not a copy's text.
                return UiText.Translate("Formatted content");
            case ClipKind.Text or ClipKind.RichText:
                return WhitespaceText(preview);
            default:
                return preview;
        }
    }

    /// <summary>A picture's preview in the interface language: "Image", or "Image · W × H" with its size.</summary>
    /// <param name="preview">The stored preview.</param>
    /// <returns>The translation, or the preview as it is when it has neither shape (an entry stored some other way).</returns>
    private static string ImageText(string preview)
    {
        if (preview == ContentClassifier.ImagePreview)
        {
            return UiText.Translate("Image");
        }

        var match = ImageSizePattern().Match(preview);
        if (!match.Success
            || !int.TryParse(match.Groups["width"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int width)
            || !int.TryParse(match.Groups["height"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int height))
        {
            return preview;
        }

        // The forgotten list's title uses the same words (ForgottenText.Title): one catalog entry for both.
        return UiText.Format($"Image · {width} × {height}");
    }

    /// <summary>A file list's preview with its "+N more" line in the interface language; the file names stay as they are.</summary>
    /// <param name="preview">The stored preview: one name per line.</param>
    /// <returns>The preview with the last line translated, or as it is when it holds five names or fewer.</returns>
    private static string FilesText(string preview)
    {
        // Only the line after the five names can be the app's: a cheap test first, since most lists are short.
        int lastBreak = preview.LastIndexOf('\n');
        if (lastBreak < 0 || preview.AsSpan().Count('\n') != FileNamesShown)
        {
            return preview;
        }

        var match = MoreFilesPattern().Match(preview[(lastBreak + 1)..]);
        if (!match.Success || !long.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long more))
        {
            return preview;
        }

        // The Delete stored dialog says the same after its examples: one catalog entry for both.
        return string.Concat(preview.AsSpan(0, lastBreak + 1), UiText.Format($"+{more:N0} more"));
    }

    /// <summary>A white-space-only text's placeholder in the interface language, counted with the language's plural forms.</summary>
    /// <param name="preview">The stored preview.</param>
    /// <returns>The translation, or the preview as it is when it is anything else (the text the user copied).</returns>
    private static string WhitespaceText(string preview)
    {
        // Every text preview passes here: a prefix test keeps the regular expression for the rare placeholder.
        if (!preview.StartsWith("(whitespace · ", StringComparison.Ordinal))
        {
            return preview;
        }

        var match = WhitespacePattern().Match(preview);
        if (!match.Success || !long.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long count))
        {
            return preview;
        }

        return UiText.Plural(count, $"(whitespace · {count:N0} char)", $"(whitespace · {count:N0} chars)");
    }

    /// <summary>
    /// "Image · W × H" as <see cref="ContentClassifier.DescribeImage(int?, int?)"/> words it: the size in plain ASCII
    /// digits (an <see cref="int"/> formatted without separators), a middle dot and a multiplication sign.
    /// </summary>
    /// <returns>The compiled pattern, with the named groups <c>width</c> and <c>height</c>.</returns>
    [GeneratedRegex(@"^Image · (?<width>[0-9]+) × (?<height>[0-9]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageSizePattern();

    /// <summary>The "+N more" line of a file list's preview, as <see cref="ContentClassifier.DescribeFiles(IReadOnlyList{string})"/> words it.</summary>
    /// <returns>The compiled pattern, with the named group <c>count</c>.</returns>
    [GeneratedRegex(@"^\+(?<count>[0-9]+) more$", RegexOptions.CultureInvariant)]
    private static partial Regex MoreFilesPattern();

    /// <summary>
    /// The placeholder <see cref="ContentClassifier.BuildTextPreview(string)"/> gives a text of nothing but white space:
    /// "(whitespace · 1 char)", "(whitespace · 3 chars)".
    /// </summary>
    /// <returns>The compiled pattern, with the named group <c>count</c>.</returns>
    [GeneratedRegex(@"^\(whitespace · (?<count>[0-9]+) chars?\)$", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();
}
