using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// What a card knows about its text: how many lines of it the card shows (Settings › Shortcut › Lines per card), the text
/// its text block is given for them, and for the text viewer (the eye's viewer and the hover peek of a long text,
/// ClipboardFlyout.TextPreview.cs) whether it has text to show and whether the card shows less than all of it.
/// </summary>
public sealed partial class ClipItemViewModel
{
    /// <summary>
    /// The lines every card made from now on shows (<see cref="AppSettings.LinesPerCard"/>): set by the panel when it is made
    /// and whenever the setting changes (<see cref="FlyoutViewModel.ApplyLinesPerCard"/>), which also tells the cards that
    /// exist already. UI thread only, like every card.
    /// </summary>
    /// <remarks>
    /// Shared rather than handed to each card: cards are made in five places (the history's cards, Everything's picks, the
    /// shell tabs' commands, the prompt tabs' archive and the merged tabs' kept entries), and every one of them shows the same
    /// number of lines. There is one panel per process.
    /// </remarks>
    public static int DefaultCardLines { get; set; } = AppSettings.DefaultLinesPerCard;

    /// <summary>
    /// Whether the card's text block currently cuts its text off (WinUI's <c>TextBlock.IsTextTrimmed</c>, reported by the view
    /// as the card is laid out); a part of <see cref="IsCutOff"/>.
    /// </summary>
    private bool textTrimmed;

    /// <summary>
    /// Whether <see cref="CardText"/> is shorter than the preview (<see cref="CardPreviewPart.IsCut"/>); a part of
    /// <see cref="IsCutOff"/>, for the one case the text block cannot see (a cut by characters on a very wide panel).
    /// </summary>
    private bool cardTextCut;

    /// <summary>
    /// How many lines of its text the card shows at most (the text block's <c>MaxLines</c>): <see cref="DefaultCardLines"/>
    /// when the card was made, then whatever <see cref="ApplyCardLines"/> sets.
    /// </summary>
    [ObservableProperty]
    public partial int CardLines { get; private set; } = DefaultCardLines;

    /// <summary>
    /// The text the card's text block shows: the start of <see cref="Preview"/> that <see cref="CardLines"/> lines can show,
    /// plus one line, so the block ends with "…" where the text goes on (<see cref="CardPreview.For"/>). Kept in step with the
    /// entry and the line count (<see cref="RefreshCardText"/>); bound one way, so an entry updated in place (a Notepad++ tab
    /// edited while its card shows) shows its new text.
    /// </summary>
    [ObservableProperty]
    public partial string CardText { get; private set; } = string.Empty;

    /// <summary>
    /// Whether the card shows less than its whole text: its text block trims its text, the text block was given only the
    /// start of the preview, or the preview itself is only the start of the content. Only then does the card get the eye, the
    /// thin "⋯" line under the text and the hover peek (the owner's choice, 2026-11-13: a text the card already shows whole
    /// keeps the plain card).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoreVisibility))]
    public partial bool IsCutOff { get; private set; }

    /// <summary>The eye and the "⋯" line: shown while <see cref="IsCutOff"/>.</summary>
    public Visibility MoreVisibility => IsCutOff ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Whether the card has text the text viewer can show: text, formatted text, links and file lists (their paths), and the
    /// cards of the shell and prompt tabs. Not images and colors (images have their own viewer), not Everything picks (one
    /// path, shown whole).
    /// </summary>
    public bool CanViewText => Pick is null && Kind is not (ClipKind.Image or ClipKind.Color);

    /// <summary>
    /// Whether the stored preview is only the start of the content: a text preview cut with "…" (now at
    /// <see cref="ContentClassifier.PreviewMaxChars"/>, by earlier versions at 1,000 characters), or a file list's "+N more"
    /// line (<see cref="ContentClassifier.IsCutPreview"/>). Asked of the stored preview, which is English: the shown one
    /// translates the file list's last line.
    /// </summary>
    public bool PreviewIsCut => ContentClassifier.IsCutPreview(Kind, Entry.Preview);

    /// <summary>
    /// Shows <paramref name="lines"/> lines of text on this card from now on (the setting changed): the text block's line
    /// count and the text it is given follow, and so does whether the card counts as cut off.
    /// </summary>
    /// <param name="lines">The lines per card (<see cref="AppSettings.LinesPerCard"/>); kept within its bounds.</param>
    public void ApplyCardLines(int lines)
    {
        CardLines = Math.Clamp(lines, AppSettings.MinLinesPerCard, AppSettings.MaxLinesPerCard);
        RefreshCardText();
    }

    /// <summary>
    /// Takes the card's text block's word on trimming (its <c>IsTextTrimmed</c> after a layout) and works out
    /// <see cref="IsCutOff"/> again.
    /// </summary>
    /// <param name="trimmed">Whether the text block trims its text now.</param>
    public void ReportTextTrimmed(bool trimmed)
    {
        textTrimmed = trimmed;
        RefreshCutOff();
    }

    /// <summary>
    /// Works <see cref="CardText"/> out from the shown preview and <see cref="CardLines"/>, then <see cref="IsCutOff"/>. The
    /// generated setter announces only a text that changed, so a reload that keeps the card leaves its text block alone.
    /// </summary>
    private void RefreshCardText()
    {
        var part = CardPreview.For(Preview, CardLines);
        CardText = part.Text;
        cardTextCut = part.IsCut;
        RefreshCutOff();
    }

    /// <summary>Works <see cref="IsCutOff"/> out from the trimming the view reported, the text block's text and the preview.</summary>
    private void RefreshCutOff() => IsCutOff = CanViewText && (textTrimmed || cardTextCut || PreviewIsCut);
}
