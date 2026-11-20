using BetterClipboard.Core.Content;
using BetterClipboard.Core.Settings;

namespace BetterClipboard.Core.Presentation;

/// <summary>
/// What a card in the panel hands its text block: the part of its preview that its number of lines can show, plus one line,
/// so that the text block still ends with "…" where the text goes on (Settings › Shortcut › Lines per card, CLAUDE.md
/// §2.57).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a card does not get its whole preview.</b> A stored preview holds up to
/// <see cref="ContentClassifier.PreviewMaxChars"/> characters, enough for the most lines a card shows on a wide screen,
/// while a card of four lines shows a few hundred of them. A text block is given only what its lines can need, so laying a
/// card out costs about what it did with the 1,000-character previews before 2026-11-20, whatever the setting.
/// </para>
/// <para>
/// <b>The rule.</b> The preview's first <c>lines + 1</c> lines, and at most <c>(lines + 1) ×</c>
/// <see cref="MaxCharsPerLine"/> characters of them, never splitting a surrogate pair. The extra line keeps the text
/// block's own trimming wherever the preview goes on: its last shown line ends with "…", and its <c>IsTextTrimmed</c>
/// (which the card's eye and dotted line follow) says so. <see cref="CardPreviewPart.IsCut"/> covers the one case the text
/// block cannot see: a cut by characters on a panel so wide that the characters kept fill fewer lines than it shows.
/// </para>
/// </remarks>
public static class CardPreview
{
    /// <summary>
    /// The most characters one line of a card is assumed to hold: a bound, not a measure. 500 characters of the card's
    /// monospace font (7.9 DIPs each at its 13.5-DIP size) are some 4,000 DIPs, wider than a panel on a 4K screen at 100 %.
    /// </summary>
    public const int MaxCharsPerLine = 500;

    /// <summary>The part of a preview a card of <paramref name="lines"/> lines hands its text block.</summary>
    /// <param name="preview">The preview the card shows (<c>PreviewText.Display</c> of the stored one).</param>
    /// <param name="lines">
    /// The card's lines (<see cref="AppSettings.LinesPerCard"/>); kept within <see cref="AppSettings.MinLinesPerCard"/>..
    /// <see cref="AppSettings.MaxLinesPerCard"/>, as the settings themselves are.
    /// </param>
    /// <returns>The text to show, and whether it is shorter than the preview.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="preview"/> is <see langword="null"/>.</exception>
    public static CardPreviewPart For(string preview, int lines)
    {
        ArgumentNullException.ThrowIfNull(preview);
        int keptLines = Math.Clamp(lines, AppSettings.MinLinesPerCard, AppSettings.MaxLinesPerCard) + 1;

        // The line break that ends the last line kept: line k ends at the k-th '\n' (previews use '\n' alone,
        // BuildTextPreview normalizes them, and a file list's names are joined with it).
        int end = preview.Length;
        int lineBreak = -1;
        for (int line = 0; line < keptLines; line++)
        {
            lineBreak = preview.IndexOf('\n', lineBreak + 1);
            if (lineBreak < 0)
            {
                break;
            }
        }

        if (lineBreak >= 0)
        {
            end = lineBreak;
        }

        int maxChars = keptLines * MaxCharsPerLine;
        if (end > maxChars)
        {
            // Never between the two halves of a surrogate pair: the text block would draw the lone half as a box.
            end = char.IsHighSurrogate(preview[maxChars - 1]) ? maxChars - 1 : maxChars;
        }

        return end < preview.Length ? new CardPreviewPart(preview[..end], IsCut: true) : new CardPreviewPart(preview, IsCut: false);
    }
}

/// <summary>The text a card hands its text block (<see cref="CardPreview.For"/>).</summary>
/// <param name="Text">The start of the preview, or the whole preview.</param>
/// <param name="IsCut">Whether <paramref name="Text"/> is shorter than the preview: the card then offers the rest (its eye).</param>
public readonly record struct CardPreviewPart(string Text, bool IsCut);
