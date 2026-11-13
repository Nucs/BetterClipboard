using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// What a card knows for the text viewer (the eye's viewer and the hover peek of a long text, ClipboardFlyout.TextPreview.cs):
/// whether it has text to show, and whether its card shows less than all of it.
/// </summary>
public sealed partial class ClipItemViewModel
{
    /// <summary>
    /// Whether the card's 4-line text block currently cuts its preview off (WinUI's <c>TextBlock.IsTextTrimmed</c>, reported
    /// by the view as the card is laid out); half of <see cref="IsCutOff"/>.
    /// </summary>
    private bool textTrimmed;

    /// <summary>
    /// Whether the card shows less than its whole text: its text block trims the preview, or the preview itself is only the
    /// start of the text. Only then does the card get the eye, the thin "⋯" line under the text and the hover peek (the
    /// owner's choice, 2026-11-13: a text the card already shows whole keeps the plain card).
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
    /// Whether the stored preview is only the start of the text: a text preview cut at <see cref="ContentClassifier.PreviewMaxChars"/>
    /// (it then ends with "…" at that length), or a file list's "+N more" tail.
    /// </summary>
    public bool PreviewIsCut => Kind == ClipKind.Files
        ? Preview.Contains(" more", StringComparison.Ordinal)
        : Preview.Length >= ContentClassifier.PreviewMaxChars - 1 && Preview.EndsWith('…');

    /// <summary>
    /// Takes the card's text block's word on trimming (its <c>IsTextTrimmed</c> after a layout) and works out
    /// <see cref="IsCutOff"/> again.
    /// </summary>
    /// <param name="trimmed">Whether the text block trims the preview now.</param>
    public void ReportTextTrimmed(bool trimmed)
    {
        textTrimmed = trimmed;
        RefreshCutOff();
    }

    /// <summary>Works <see cref="IsCutOff"/> out from the trimming the view reported and the preview itself.</summary>
    private void RefreshCutOff() => IsCutOff = CanViewText && (textTrimmed || PreviewIsCut);
}
