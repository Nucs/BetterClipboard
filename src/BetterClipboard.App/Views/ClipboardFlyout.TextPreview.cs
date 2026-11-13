using BetterClipboard.App.ViewModels;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.TextView;
using BetterClipboard.Windows.Input;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace BetterClipboard.App.Views;

/// <summary>
/// The text overlays on the panel's cards (the owner's request, 2026-11-13: a long Codex prompt showed four lines of
/// XML and nothing more): a hover <b>peek</b> that shows the whole text, and the eye's <b>viewer</b>, both with Markdown
/// drawn, code colored in its detected language, and XML and JSON re-indented.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which cards.</b> Only a card that shows less than its whole text (<see cref="ClipItemViewModel.IsCutOff"/>) gets the
/// eye, a thin "⋯" line under its text and the hover peek; any card with text can be opened from its menu's View (F3).
/// </para>
/// <para>
/// <b>The peek is a hover card, not a tooltip.</b> It opens centered on the panel after the mouse rests on a cut-off card,
/// and stays while the mouse is on it or on the card it came from: the text can be scrolled with the wheel, selected and
/// copied, and right-clicked for the card's own menu. Leaving both, clicking outside it, or Esc closes it. (The image peek,
/// ClipboardFlyout.ImagePreview.cs, closes on any move; the owner asked for selection and a menu in this one.)
/// </para>
/// <para>
/// <b>The viewer</b> dims the screen and shows the whole text in a large card centered on the panel, with a Formatted / As
/// copied switch (when they differ), Wrap, Line numbers, a language picker (Markdown when nothing else is recognized) and
/// the text size (Ctrl+wheel, Ctrl+= and Ctrl+-). Wrap, line numbers and the size are remembered in the settings.
/// </para>
/// <para>
/// <b>Large texts.</b> The text is read and prepared off the UI thread (<see cref="TextViewSource"/>,
/// <see cref="TextViewRendering"/>), and shown in RichEdit (<see cref="TextPreviewPresenter"/>), which lays text out lazily.
/// The peek shows at most <see cref="PeekMaxChars"/> characters and says so; the viewer shows everything, a page at a time
/// (<see cref="TextViewRendering.Pages"/>: RichEdit takes megabytes slowly, on the UI thread), colored up to
/// <see cref="SyntaxHighlighter.DefaultMaxChars"/>. A page without colors is loaded as plain text, faster than its RTF.
/// </para>
/// <para>
/// Both overlays are windowed popups in the panel's own <c>XamlRoot</c>, counted in <see cref="openPopups"/> before they
/// open (the image overlays' reason: a deactivation as they appear must not conceal the panel), and closed with the panel.
/// </para>
/// </remarks>
public sealed partial class ClipboardFlyout
{
    /// <summary>
    /// The most characters the hover peek shows: one page of the viewer (<see cref="TextViewRendering.PageMaxChars"/>),
    /// which RichEdit shows in a fraction of a second; F3 opens the rest.
    /// </summary>
    private const int PeekMaxChars = TextViewRendering.PageMaxChars;

    /// <summary>A peek of a text up to this long is sized to its text; a longer one gets the full height at once.</summary>
    private const int ShortPeekChars = 20_000;

    /// <summary>The peek's widest, in DIPs: a comfortable reading width, whatever the monitor.</summary>
    private const double TextPeekMaxWidthDip = 900;

    /// <summary>The share of the work area the peek may use at most, in each direction.</summary>
    private const double TextPeekMaxFraction = 0.8;

    /// <summary>The viewer's widest, in DIPs.</summary>
    private const double TextViewerMaxWidthDip = 1400;

    /// <summary>The share of the work area the viewer uses, in each direction.</summary>
    private const double TextViewerFraction = 0.9;

    /// <summary>How much one text-size step (a wheel notch, Ctrl+= or Ctrl+-) changes the size: 10 points of percent.</summary>
    private const double TextSizeStep = 0.1;

    /// <summary>Segoe Fluent Icons RedEye (E7B3, checked by rendering): View, here and on the image cards.</summary>
    private const string ViewGlyph = "\uE7B3";

    /// <summary>The code font, worked out once: Cascadia Mono where Windows has it (Windows 11), else Consolas.</summary>
    private static readonly Lazy<string> CodeFontName = new(() =>
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var userFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
        return File.Exists(Path.Combine(fonts, "CascadiaMono.ttf")) || File.Exists(Path.Combine(userFonts, "CascadiaMono.ttf")) ? "Cascadia Mono" : "Consolas";
    });

    /// <summary>The card whose text the mouse rests on (drives the peek), or <see langword="null"/>.</summary>
    private ClipItemViewModel? textHoverCard;

    /// <summary>The hovered card's text area: with the peek, it bounds where the mouse may go without closing the peek.</summary>
    private FrameworkElement? textHoverElement;

    /// <summary>Rest timer for the text peek (created on first use, reused).</summary>
    private DispatcherTimer? textHoverTimer;

    /// <summary>The open text peek, or <see langword="null"/>.</summary>
    private Popup? textPeekPopup;

    /// <summary>The card the open peek shows.</summary>
    private ClipItemViewModel? textPeekCard;

    /// <summary>The peek's frame (the visible card), for its bounds.</summary>
    private Border? textPeekFrame;

    /// <summary>The panel inside the peek's frame that holds the text surface (it lets go of the surface when the peek closes).</summary>
    private Panel? textPeekLayout;

    /// <summary>
    /// Where the mouse may go without closing the peek, in the peek surface's coordinates: the frame and the card it came
    /// from, and the space between them.
    /// </summary>
    private Rect textPeekKeepArea;

    /// <summary>The cursor's screen position when the peek opened: a move is only real once the cursor leaves it.</summary>
    private ScreenPoint? textPeekAnchor;

    /// <summary>Whether a context menu opened from the peek is showing (the pointer is on the menu, not lost).</summary>
    private bool textPeekMenuOpen;

    /// <summary>Cancels a peek's text load when the mouse moved on first.</summary>
    private CancellationTokenSource? textPeekLoad;

    /// <summary>
    /// The text surface, made once (RichEdit's start-up cost is paid once, by the warm-up) and reused by the peek and the
    /// viewer: the viewer closes the peek before it opens, and the peek never opens over the viewer, so they never need it
    /// at the same time. A fresh surface cost the viewer's first text about a second in the QA desktop.
    /// </summary>
    private TextPreviewPresenter? textPeekPresenter;

    /// <summary>The open text viewer's state, or <see langword="null"/>.</summary>
    private TextViewerState? textViewer;

    /// <summary>The viewer's text surface: the peek's (<see cref="textPeekPresenter"/>), once the viewer has opened.</summary>
    private TextPreviewPresenter? textViewerPresenter;

    /// <summary>The last text read for a card (one entry): a peek and then the viewer of one card read it once.</summary>
    private (string Key, string Text)? textCache;

    /// <summary>
    /// A text prepared for showing: the source, its view, the page shown and that page's RTF (<see langword="null"/> to load
    /// it as plain text).
    /// </summary>
    /// <param name="Source">The prepared text.</param>
    /// <param name="View">The view shown.</param>
    /// <param name="Page">The page of the view shown.</param>
    /// <param name="Rtf">The page's RTF, or <see langword="null"/> for the plain load.</param>
    private sealed record PreparedText(TextViewSource Source, TextViewRendering View, TextViewPage Page, string? Rtf);

    /// <summary>Everything the open viewer holds: its card, its choices, what it shows, and its controls.</summary>
    private sealed class TextViewerState
    {
        /// <summary>The card shown.</summary>
        public required ClipItemViewModel Card { get; init; }

        /// <summary>The viewer's popup.</summary>
        public required Popup Popup { get; init; }

        /// <summary>The panel that holds the text surface (it lets go of the surface when the viewer closes).</summary>
        public required Panel Content { get; init; }

        /// <summary>The prepared text, once loaded.</summary>
        public TextViewSource? Source { get; set; }

        /// <summary>The view shown, once prepared.</summary>
        public TextViewRendering? View { get; set; }

        /// <summary>The language picked in the viewer, or <see langword="null"/> for the detected one.</summary>
        public TextLanguage? Language { get; set; }

        /// <summary>The Formatted / As copied switch: formatted when <see langword="true"/>.</summary>
        public bool Formatted { get; set; } = true;

        /// <summary>The text size factor (1 = 100 %).</summary>
        public double TextSize { get; set; } = 1;

        /// <summary>Whether long lines wrap.</summary>
        public bool Wrap { get; set; } = true;

        /// <summary>Whether line numbers show next to code.</summary>
        public bool LineNumbers { get; set; } = true;

        /// <summary>Cancels the load or re-render in progress.</summary>
        public CancellationTokenSource Work { get; set; } = new();

        /// <summary>The waiting ring over the text.</summary>
        public required ProgressRing Ring { get; init; }

        /// <summary>The "Formatted" half of the switch.</summary>
        public required ToggleButton FormattedButton { get; init; }

        /// <summary>The "As copied" half of the switch.</summary>
        public required ToggleButton AsCopiedButton { get; init; }

        /// <summary>The switch's panel (hidden when the two views are the same).</summary>
        public required FrameworkElement SwitchPanel { get; init; }

        /// <summary>The Wrap toggle.</summary>
        public required ToggleButton WrapButton { get; init; }

        /// <summary>The Line numbers toggle.</summary>
        public required ToggleButton LinesButton { get; init; }

        /// <summary>The language picker.</summary>
        public required DropDownButton LanguageButton { get; init; }

        /// <summary>The text size in percent.</summary>
        public required TextBlock SizeLabel { get; init; }

        /// <summary>The line under the text: lines, characters, and what a size limit left out.</summary>
        public required TextBlock Status { get; init; }

        /// <summary>Saves the text size a moment after the last change (one settings write per burst of Ctrl+wheel).</summary>
        public DispatcherTimer? SaveSize { get; set; }

        /// <summary>The index of the page shown (<see cref="TextViewRendering.Pages"/>).</summary>
        public int Page { get; set; }

        /// <summary>The page controls (hidden for a text of one page).</summary>
        public required FrameworkElement PagePanel { get; init; }

        /// <summary>"Page 3 of 94".</summary>
        public required TextBlock PageLabel { get; init; }

        /// <summary>The previous page.</summary>
        public required Button PreviousPageButton { get; init; }

        /// <summary>The next page.</summary>
        public required Button NextPageButton { get; init; }
    }

    /// <summary>The card's 4-line text block trimmed or stopped trimming its preview: the card's cut-off state follows.</summary>
    /// <param name="sender">The text block.</param>
    /// <param name="args">Event data.</param>
    private void CardText_IsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args) =>
        (sender.DataContext as ClipItemViewModel)?.ReportTextTrimmed(sender.IsTextTrimmed);

    /// <summary>
    /// A recycled card container shows another card: its trimming is looked at once its layout is done (a card that trims
    /// like the one before raises no trimming change).
    /// </summary>
    /// <param name="sender">The text block.</param>
    /// <param name="args">Event data.</param>
    private void CardText_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is TextBlock block && args.NewValue is ClipItemViewModel)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                (block.DataContext as ClipItemViewModel)?.ReportTextTrimmed(block.IsTextTrimmed));
        }
    }

    /// <summary>The mouse rests on a card's text: when the card is cut off, start reading its text and the peek's countdown.</summary>
    /// <param name="sender">The card's text area.</param>
    /// <param name="e">Pointer data.</param>
    private void TextCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (CardOf(sender) is not { CanViewText: true, IsCutOff: true } card || e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            return;
        }

        textHoverCard = card;
        textHoverElement = sender as FrameworkElement;
        _ = PreloadCardTextAsync(card);
        RestartTextHoverTimer();
    }

    /// <summary>
    /// The mouse moved over a card's text: the peek waits until it rests again. With no countdown running (a peek just
    /// closed, or the mouse came back), the move starts one, as entering the card does: resting there again re-peeks,
    /// like the image peek.
    /// </summary>
    /// <param name="sender">The card's text area.</param>
    /// <param name="e">Pointer data.</param>
    private void TextCard_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (textPeekPopup is not null)
        {
            return;
        }

        if (textHoverCard is null)
        {
            TextCard_PointerEntered(sender, e);
            return;
        }

        RestartTextHoverTimer();
    }

    /// <summary>The mouse left a card's text before the peek opened: stop waiting.</summary>
    /// <param name="sender">The card's text area.</param>
    /// <param name="e">Pointer data.</param>
    private void TextCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // PointerExited bubbles: crossing from the card's text onto its eye raises it too, with the mouse still on the
        // card. Only a pointer outside the area has left it.
        if (IsInside(sender, e))
        {
            return;
        }

        if (textPeekPopup is null)
        {
            textHoverTimer?.Stop();
            textHoverCard = null;
            textHoverElement = null;
        }
    }

    /// <summary>The eye on a cut-off card: open the text viewer.</summary>
    /// <param name="sender">The eye button.</param>
    /// <param name="e">Click data.</param>
    private void ViewText_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card)
        {
            ViewCard(card);
        }
    }

    /// <summary>
    /// View (the card menu's item, F3, the eye): an image card opens the image viewer, a card with text the text viewer.
    /// </summary>
    /// <param name="card">The card.</param>
    private void ViewCard(ClipItemViewModel card)
    {
        HidePeek();
        if (card.Kind == ClipKind.Image)
        {
            _ = ShowViewerAsync(card);
        }
        else if (card.CanViewText)
        {
            _ = ShowTextViewerAsync(card);
        }
    }

    /// <summary>Closes the text peek and the text viewer (the panel goes away, or a new summon starts).</summary>
    private void CloseTextOverlays()
    {
        HideTextPeek();
        CloseTextViewer();
    }

    /// <summary>
    /// Forgets the text read last and the documents of both surfaces (the panel was put away): a text can be megabytes, and
    /// only a peek or the viewer of the same card in the same use needs it again.
    /// </summary>
    private void ReleaseTextCache()
    {
        textCache = null;
        if (textPeekPopup is null)
        {
            textPeekPresenter?.Clear();
        }

        if (textViewer is null)
        {
            textViewerPresenter?.Clear();
        }
    }

    /// <summary>
    /// Creates the peek's surface ahead of the first peek and loads a word into it, so RichEdit's one-time start (most of a
    /// second the first time in a session, CLAUDE.md §2.33) is paid during the panel's warm-up, not by the first hover.
    /// </summary>
    private void WarmUpTextPreview()
    {
        try
        {
            if (textPeekPresenter is not null)
            {
                return;
            }

            // RichEdit starts when its box is loaded into a live tree: a 1×1, invisible box in the panel's own tree, loaded
            // once with a word and taken out again. Nothing of it is seen or hit.
            var presenter = textPeekPresenter = CreateTextPresenter();
            var view = presenter.View;
            view.Width = 1;
            view.Height = 1;
            view.Opacity = 0;
            view.IsHitTestVisible = false;
            RoutedEventHandler? loaded = null;
            loaded = (_, _) =>
            {
                view.Loaded -= loaded;

                // A peek that took the surface first (a summon within the warm-up's frame) owns it now.
                if (!ReferenceEquals(view.Parent, Root))
                {
                    return;
                }

                try
                {
                    var palette = CurrentTextPalette();
                    var style = CurrentTextStyle(1);
                    var sample = TextViewRendering.Create(TextViewSource.Create("BetterClipboard"), TextLanguage.PlainText, formatted: true);
                    presenter.Show(sample, sample.Pages[0], sample.ToRtf(sample.Pages[0], palette, style), style, palette, wrap: true, lineNumbers: false);
                    presenter.Clear();
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Preparing the text viewer ahead of the first peek failed: {ex.Message}");
                }

                // Out of the tree again on the next turn (not while it is being loaded), and back to its own size.
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    Root.Children.Remove(view);
                    view.ClearValue(FrameworkElement.WidthProperty);
                    view.ClearValue(FrameworkElement.HeightProperty);
                    view.Opacity = 1;
                    view.IsHitTestVisible = true;
                });
            };
            view.Loaded += loaded;
            Root.Children.Add(view);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Preparing the text viewer ahead of the first peek failed: {ex.Message}");
        }
    }

    /// <summary>Takes an element out of the panel it is in, if any (a text surface moves between the warm-up and its overlay).</summary>
    /// <param name="element">The element.</param>
    private static void DetachFromParent(FrameworkElement element) => (element.Parent as Panel)?.Children.Remove(element);

    /// <summary>Makes a text surface in the code font, with the theme's tertiary color for line numbers.</summary>
    /// <returns>The surface.</returns>
    private static TextPreviewPresenter CreateTextPresenter() =>
        new(new FontFamily(CodeFontName.Value), OverlayStyle("OverlayTertiaryTextStyle"));

    /// <summary>
    /// One of App.xaml's overlay styles. Overlays take their colors from these, never from a brush looked up here: a
    /// style's theme resources resolve in the panel's theme (the overlay copies it), a looked-up brush in Windows' theme,
    /// which differs whenever Settings › Theme is not "Use system setting".
    /// </summary>
    /// <param name="key">The style's key in App.xaml.</param>
    /// <returns>The style.</returns>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">App.xaml has no style by that key.</exception>
    private static Style OverlayStyle(string key) => (Style)Application.Current.Resources[key];

    /// <summary>The colors for the panel's current theme.</summary>
    /// <returns>The palette.</returns>
    private SyntaxPalette CurrentTextPalette() => Root.ActualTheme == ElementTheme.Light ? SyntaxPalette.Light : SyntaxPalette.Dark;

    /// <summary>The fonts and sizes at a text size.</summary>
    /// <param name="textSize">The size factor (1 = 100 %).</param>
    /// <returns>The style.</returns>
    private static TextViewStyle CurrentTextStyle(double textSize) => TextViewStyle.Default(CodeFontName.Value).Scaled(textSize);

    /// <summary>Restarts the peek's rest timer (it opens the peek once the mouse has rested long enough).</summary>
    private void RestartTextHoverTimer()
    {
        if (!CanShowTextPeek())
        {
            return;
        }

        textHoverTimer ??= CreateTextHoverTimer();
        textHoverTimer.Stop();
        textHoverTimer.Start();
    }

    /// <summary>Creates the one-shot rest timer of the text peek (the image peek's delay).</summary>
    /// <returns>The timer.</returns>
    private DispatcherTimer CreateTextHoverTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoverPeekDelayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _ = ShowTextPeekAsync();
        };
        return timer;
    }

    /// <summary>
    /// Whether a text peek may open now: the panel is up, nothing else is open over it (a menu, an overlay), nothing is being
    /// dragged, and the mouse rests on a cut-off card.
    /// </summary>
    /// <returns><see langword="true"/> when it may.</returns>
    private bool CanShowTextPeek() =>
        IsOpen && textPeekPopup is null && textViewer is null && peekPopup is null && viewerPopup is null && drag is null &&
        draggedClipIds is null && openPopups == 0 && textHoverCard is { IsCutOff: true };

    /// <summary>The key a card's text is cached under: its content (a prompt tab's ids change between loads; its content does not).</summary>
    /// <param name="card">The card.</param>
    /// <returns>The key.</returns>
    private static string TextCacheKey(ClipItemViewModel card) => $"{card.Kind}:{card.Entry.ContentHash}:{Math.Max(0, card.Id)}";

    /// <summary>Reads a card's whole text into the cache without waiting (the hover reads ahead of the peek).</summary>
    /// <param name="card">The card.</param>
    /// <returns>A task completing when read or failed (failures are logged).</returns>
    private async Task PreloadCardTextAsync(ClipItemViewModel card)
    {
        try
        {
            await LoadCardTextAsync(card, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Reading the text of card {card.Id} for its peek failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The whole text behind a card: a prompt's from the archive, a shell command's own, a stored entry's from the store
    /// (<see cref="AppController.GetEntryTextAsync"/>); the preview for anything else. Cached for one card.
    /// </summary>
    /// <param name="card">The card.</param>
    /// <param name="token">Cancels a read whose overlay went away.</param>
    /// <returns>The text, or <see langword="null"/> when it is gone.</returns>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    private async Task<string?> LoadCardTextAsync(ClipItemViewModel card, CancellationToken token)
    {
        var key = TextCacheKey(card);
        if (textCache is { } cached && cached.Key == key)
        {
            return cached.Text;
        }

        string? text = card.Prompt is { } prompt ? await controller.GetPromptTextAsync(prompt).ConfigureAwait(true)
            : card.Command is { } command ? command.Text
            : card.Id > 0 ? await controller.GetEntryTextAsync(card.Id, card.Kind, token).ConfigureAwait(true)
            : card.Preview;
        if (text is not null)
        {
            textCache = (key, text);
        }

        return text;
    }

    /// <summary>
    /// Prepares a text for showing off the UI thread: the source (unless one is given), the view, and the RTF of the page
    /// holding <paramref name="position"/> — or no RTF for a page without colors, which loads faster as plain text.
    /// </summary>
    /// <param name="text">The text (used when <paramref name="source"/> is <see langword="null"/>).</param>
    /// <param name="maxChars">The most characters shown (the peek's limit, or all).</param>
    /// <param name="source">A source prepared before (a re-render in the viewer), or <see langword="null"/>.</param>
    /// <param name="language">The language to show it as, or <see langword="null"/> for the detected one.</param>
    /// <param name="formatted">Formatted rather than as copied.</param>
    /// <param name="palette">The theme's colors.</param>
    /// <param name="style">Fonts and sizes.</param>
    /// <param name="position">A position in the view's text whose page is prepared (0: the first page).</param>
    /// <param name="token">Cancels the work.</param>
    /// <returns>The prepared text.</returns>
    /// <exception cref="OperationCanceledException">The work was cancelled.</exception>
    private static Task<PreparedText> PrepareTextAsync(string text, int maxChars, TextViewSource? source, TextLanguage? language, bool formatted, SyntaxPalette palette, TextViewStyle style, int position, CancellationToken token) =>
        Task.Run(
            () =>
            {
                var prepared = source ?? TextViewSource.Create(text, maxChars, token);
                var view = TextViewRendering.Create(prepared, language ?? prepared.DetectedLanguage, formatted, token);
                return PreparePage(prepared, view, view.PageOf(position), palette, style, token);
            },
            token);

    /// <summary>Prepares another page of a view shown already (the viewer's page controls), off the UI thread.</summary>
    /// <param name="source">The source shown.</param>
    /// <param name="view">Its view.</param>
    /// <param name="pageIndex">The page's index in <see cref="TextViewRendering.Pages"/>.</param>
    /// <param name="palette">The theme's colors.</param>
    /// <param name="style">Fonts and sizes.</param>
    /// <param name="token">Cancels the work.</param>
    /// <returns>The prepared page.</returns>
    /// <exception cref="OperationCanceledException">The work was cancelled.</exception>
    private static Task<PreparedText> PreparePageAsync(TextViewSource source, TextViewRendering view, int pageIndex, SyntaxPalette palette, TextViewStyle style, CancellationToken token) =>
        Task.Run(() => PreparePage(source, view, pageIndex, palette, style, token), token);

    /// <summary>The RTF of one page, or none for a page of a code document without colors (its plain load is faster).</summary>
    /// <param name="source">The source.</param>
    /// <param name="view">Its view.</param>
    /// <param name="pageIndex">The page's index (clamped to the view's pages).</param>
    /// <param name="palette">The theme's colors.</param>
    /// <param name="style">Fonts and sizes.</param>
    /// <param name="token">Cancels the work.</param>
    /// <returns>The prepared page.</returns>
    /// <exception cref="OperationCanceledException">The work was cancelled.</exception>
    private static PreparedText PreparePage(TextViewSource source, TextViewRendering view, int pageIndex, SyntaxPalette palette, TextViewStyle style, CancellationToken token)
    {
        var page = view.Pages[Math.Clamp(pageIndex, 0, view.Pages.Count - 1)];
        bool plainLoad = !view.IsDocument && view.PageTokens(page).Count == 0;
        return new PreparedText(source, view, page, plainLoad ? null : view.ToRtf(page, palette, style, token));
    }

    /// <summary>The name of a language for people: the product's own name, or "Plain text" in the interface language.</summary>
    /// <param name="language">The language.</param>
    /// <returns>Its name.</returns>
    private static string LanguageName(TextLanguage language) =>
        language == TextLanguage.PlainText ? UiText.Translate("Plain text") : TextLanguages.NameOf(language);

    /// <summary>
    /// Builds an overlay's header: the card's glyph, kind and caption, then the language and the line count.
    /// </summary>
    /// <param name="card">The card.</param>
    /// <param name="language">The language shown.</param>
    /// <param name="lines">The text's line count, or <see langword="null"/> to leave it out (the viewer has it in its status line).</param>
    /// <param name="trailing">An element at the header's end (the viewer's close button), or <see langword="null"/>.</param>
    /// <returns>The header.</returns>
    private static Grid BuildTextHeader(ClipItemViewModel card, TextLanguage language, int? lines, UIElement? trailing)
    {
        var header = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The kind and the caption are two text blocks, not two runs of one: a run takes no style, and its colors must
        // come from styles (OverlayStyle). The caption trims; the kind is short.
        var glyph = new FontIcon { Glyph = card.KindGlyph, FontSize = 13, Style = OverlayStyle("OverlayAccentIconStyle") };
        var kind = new TextBlock
        {
            Text = card.KindLabel,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var caption = new TextBlock
        {
            Text = card.Caption,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Style = OverlayStyle("OverlaySecondaryTextStyle"),
        };
        int count = lines ?? 0;
        var lineCount = new TextBlock
        {
            Text = lines is null ? string.Empty : UiText.Plural(count, $"{count:N0} line", $"{count:N0} lines"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Style = OverlayStyle("OverlayTertiaryTextStyle"),
        };
        Grid.SetColumn(kind, 1);
        Grid.SetColumn(caption, 2);
        Grid.SetColumn(lineCount, 3);
        header.Children.Add(glyph);
        header.Children.Add(kind);
        header.Children.Add(caption);
        header.Children.Add(lineCount);
        if (trailing is not null)
        {
            Grid.SetColumn((FrameworkElement)trailing, 4);
            header.Children.Add(trailing);
        }

        // The language as a chip next to the line count (the viewer puts its picker in the toolbar instead).
        if (trailing is null)
        {
            var chip = LanguageChip(language);
            Grid.SetColumn(chip, 4);
            header.Children.Add(chip);
        }

        return header;
    }

    /// <summary>A small rounded chip naming a language (the peek's header).</summary>
    /// <param name="language">The language.</param>
    /// <returns>The chip.</returns>
    private static Border LanguageChip(TextLanguage language) => new()
    {
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(8, 1, 8, 2),
        VerticalAlignment = VerticalAlignment.Center,
        BorderThickness = new Thickness(1),
        Style = OverlayStyle("OverlayChipStyle"),
        Child = new TextBlock { Text = LanguageName(language), FontSize = 11 },
    };

    /// <summary>
    /// The note under a text when something was left out: the peek's limit, or a size limit on drawing or coloring;
    /// empty when nothing was.
    /// </summary>
    /// <param name="prepared">The prepared text.</param>
    /// <returns>The note.</returns>
    private static string TextLimitNote(PreparedText prepared)
    {
        if (prepared.Source.IsCut)
        {
            return UiText.Format($"Showing the first {prepared.Source.Text.Length:N0} of {prepared.Source.OriginalLength:N0} characters. F3 opens all of it.");
        }

        if (prepared.View.IsLimited)
        {
            return prepared.View.Language == TextLanguage.Markdown
                ? UiText.Translate("Too long to draw as Markdown: shown as plain text.")
                : UiText.Format($"Colored up to the first {SyntaxHighlighter.DefaultMaxChars:N0} characters.");
        }

        return string.Empty;
    }

    /// <summary>Opens the peek for the card the mouse rests on, once its text is read and prepared, if the mouse is still there.</summary>
    /// <returns>A task completing when the peek shows or was abandoned (failures are logged, never thrown).</returns>
    private async Task ShowTextPeekAsync()
    {
        try
        {
            if (textHoverCard is not { } card || !CanShowTextPeek() || Root.XamlRoot is null)
            {
                return;
            }

            textPeekLoad?.Cancel();
            var load = new CancellationTokenSource();
            textPeekLoad = load;
            var text = await LoadCardTextAsync(card, load.Token).ConfigureAwait(true);
            if (text is null || textHoverCard != card || !CanShowTextPeek())
            {
                return;
            }

            var settings = controller.Settings.Current;
            var palette = CurrentTextPalette();
            var style = CurrentTextStyle(settings.TextViewerTextSize);
            var prepared = await PrepareTextAsync(text, PeekMaxChars, null, null, formatted: true, palette, style, position: 0, load.Token).ConfigureAwait(true);

            // The mouse may have moved to another card, or the panel gone, while the text was prepared.
            if (load.IsCancellationRequested || textHoverCard != card || !CanShowTextPeek() || Root.XamlRoot is null)
            {
                return;
            }

            ShowTextPeekPopup(card, prepared, style, palette, settings.TextViewerWrap, settings.TextViewerLineNumbers);
        }
        catch (OperationCanceledException)
        {
            // The mouse moved on.
        }
        catch (Exception ex)
        {
            // An async timer callback: an escaping exception would end the app; a missing peek is harmless.
            AppLog.Warn($"Showing the text peek failed: {ex.Message}");
        }
    }

    /// <summary>Builds and opens the peek around a prepared text, centered on the panel.</summary>
    /// <param name="card">The card.</param>
    /// <param name="prepared">Its prepared text.</param>
    /// <param name="style">Fonts and sizes.</param>
    /// <param name="palette">The theme's colors.</param>
    /// <param name="wrap">Whether long lines wrap.</param>
    /// <param name="lineNumbers">Whether code gets line numbers.</param>
    private void ShowTextPeekPopup(ClipItemViewModel card, PreparedText prepared, TextViewStyle style, SyntaxPalette palette, bool wrap, bool lineNumbers)
    {
        var geometry = MonitorOverlayGeometry();
        var presenter = textPeekPresenter ??= CreateTextPresenter();
        presenter.ContextMenuRequested -= TextViewer_ContextMenuRequested;
        presenter.ContextMenuRequested -= TextPeek_ContextMenuRequested;
        presenter.ContextMenuRequested += TextPeek_ContextMenuRequested;

        // The viewer hides the surface while it loads a text; a viewer closed before its text came leaves it hidden.
        presenter.View.Opacity = 1;
        presenter.View.IsHitTestVisible = true;

        bool shortText = prepared.Source.Text.Length <= ShortPeekChars;
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = shortText ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(BuildTextHeader(card, prepared.View.Language, prepared.Source.OriginalLineCount, trailing: null));
        Grid.SetRow(presenter.View, 1);
        DetachFromParent(presenter.View);
        layout.Children.Add(presenter.View);
        var note = TextLimitNote(prepared);
        if (note.Length > 0)
        {
            var noteBlock = new TextBlock
            {
                Text = note,
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Style = OverlayStyle("OverlayTertiaryTextStyle"),
            };
            Grid.SetRow(noteBlock, 2);
            layout.Children.Add(noteBlock);
        }

        double maxHeight = geometry.Height * TextPeekMaxFraction;
        var frame = new Border
        {
            Width = Math.Min(geometry.Width * TextPeekMaxFraction, TextPeekMaxWidthDip),
            MaxHeight = maxHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(14, 12, 10, 12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Style = OverlayStyle("OverlayFrameStyle"),
            Child = layout,
        };
        if (!shortText)
        {
            frame.Height = maxHeight;
        }

        AutomationProperties.SetName(frame, UiText.Format($"{card.KindLabel}: {LanguageName(prepared.View.Language)}"));

        // The surface covers the monitor and catches the pointer around the frame (the peek decides when it is left).
        var surface = new Grid
        {
            Width = geometry.Width,
            Height = geometry.Height,
            Background = new SolidColorBrush(Colors.Transparent),
            RequestedTheme = Root.RequestedTheme,
        };
        surface.Children.Add(frame);
        surface.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(TextPeek_PointerMoved), handledEventsToo: true);
        surface.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(TextPeek_PointerPressed), handledEventsToo: true);
        surface.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(TextPeek_PointerWheelChanged), handledEventsToo: true);
        surface.PointerExited += TextPeek_PointerExited;
        surface.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(TextPeek_KeyDown), handledEventsToo: true);

        // Centered on the panel (the owner's choice), kept inside the work area; again once the frame knows its height.
        var cardArea = AreaInSurface(textHoverElement, geometry);
        void Place()
        {
            double width = frame.ActualWidth > 0 ? frame.ActualWidth : frame.Width;
            double height = frame.ActualHeight > 0 ? frame.ActualHeight : maxHeight;
            double left = Math.Clamp((Root.ActualWidth / 2) - geometry.OffsetX - (width / 2), 8, Math.Max(8, geometry.Width - width - 8));
            double top = Math.Clamp((Root.ActualHeight / 2) - geometry.OffsetY - (height / 2), 8, Math.Max(8, geometry.Height - height - 8));
            frame.Margin = new Thickness(left, top, 0, 0);
            var frameArea = new Rect(left, top, width, height);
            frameArea.Union(cardArea);
            textPeekKeepArea = frameArea;
        }

        Place();
        frame.SizeChanged += (_, _) => Place();

        var popup = new Popup
        {
            XamlRoot = Root.XamlRoot,
            ShouldConstrainToRootBounds = false,
            IsLightDismissEnabled = false,
            HorizontalOffset = geometry.OffsetX,
            VerticalOffset = geometry.OffsetY,
            Child = surface,
        };

        // Counted before it opens (a deactivation as it appears must not conceal the panel); Popup_Closed balances the
        // count and gives the keyboard back to the panel.
        popup.Closed += Popup_Closed;
        textPeekPopup = popup;
        textPeekCard = card;
        textPeekFrame = frame;
        textPeekLayout = layout;
        textPeekAnchor = ScreenPointer.Cursor();
        openPopups++;
        popup.IsOpen = true;
        presenter.Show(prepared.View, prepared.Page, prepared.Rtf, style, palette, wrap, lineNumbers);
    }

    /// <summary>An element's bounds in the overlay surface's coordinates (the surface covers the panel monitor's work area).</summary>
    /// <param name="element">The element, or <see langword="null"/>.</param>
    /// <param name="geometry">The overlay's placement.</param>
    /// <returns>The bounds; an empty rectangle for no element.</returns>
    private Rect AreaInSurface(FrameworkElement? element, (double OffsetX, double OffsetY, double Width, double Height, double Scale) geometry)
    {
        if (element is null || !element.IsLoaded)
        {
            return Rect.Empty;
        }

        try
        {
            var bounds = element.TransformToVisual(Root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return new Rect(bounds.X - geometry.OffsetX, bounds.Y - geometry.OffsetY, bounds.Width, bounds.Height);
        }
        catch (ArgumentException)
        {
            return Rect.Empty; // not in this tree any more (the list reloaded)
        }
    }

    /// <summary>
    /// The mouse moved over the peek's surface: it closes the peek once it is outside the frame and the card, really moved
    /// (not the synthetic move as the surface appears), and no button is held (a selection being dragged).
    /// </summary>
    /// <param name="sender">The surface.</param>
    /// <param name="e">Pointer data.</param>
    private void TextPeek_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (textPeekPopup is null || textPeekMenuOpen || sender is not UIElement surface)
        {
            return;
        }

        var point = e.GetCurrentPoint(surface);
        if (point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed)
        {
            return;
        }

        bool moved = e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse || TextPeekCursorMoved();
        if (moved && !textPeekKeepArea.Contains(point.Position))
        {
            HideTextPeek();
        }
    }

    /// <summary>A press outside the frame closes the peek (and does nothing else); a press inside belongs to the text.</summary>
    /// <param name="sender">The surface.</param>
    /// <param name="e">Pointer data.</param>
    private void TextPeek_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (textPeekFrame is not null && sender is UIElement surface && !FrameContains(surface, e))
        {
            HideTextPeek();
            e.Handled = true;
        }
    }

    /// <summary>The wheel outside the frame closes the peek; inside, the text scrolls (RichEdit's own scroller).</summary>
    /// <param name="sender">The surface.</param>
    /// <param name="e">Pointer data.</param>
    private void TextPeek_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (textPeekFrame is not null && sender is UIElement surface && !FrameContains(surface, e))
        {
            HideTextPeek();
            e.Handled = true;
        }
    }

    /// <summary>Whether a pointer event lies on the peek's frame.</summary>
    /// <param name="surface">The surface.</param>
    /// <param name="e">Pointer data.</param>
    /// <returns><see langword="true"/> inside the frame.</returns>
    private bool FrameContains(UIElement surface, PointerRoutedEventArgs e)
    {
        if (textPeekFrame is not { } frame)
        {
            return false;
        }

        var position = e.GetCurrentPoint(surface).Position;
        var area = new Rect(frame.Margin.Left, frame.Margin.Top, frame.ActualWidth, frame.ActualHeight);
        return area.Contains(position);
    }

    /// <summary>The mouse left the surface altogether (onto the taskbar, another monitor): the peek closes, unless a menu took the pointer.</summary>
    /// <param name="sender">The surface.</param>
    /// <param name="e">Pointer data.</param>
    /// <remarks>
    /// PointerExited bubbles: the text's own parts raise it whenever the mouse crosses from one to another inside the
    /// peek. Taking each for "left the peek" closed it at the first move over the text (found in the QA, 2026-11-13), so
    /// only a pointer that is really outside the surface counts.
    /// </remarks>
    private void TextPeek_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!textPeekMenuOpen && sender is UIElement surface && !e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed &&
            !IsInside(sender, e))
        {
            HideTextPeek();
        }
    }

    /// <summary>Whether a pointer event's position lies within an element's own bounds.</summary>
    /// <param name="sender">The element (a non-element counts as outside).</param>
    /// <param name="e">Pointer data.</param>
    /// <returns><see langword="true"/> when the pointer is over the element's area.</returns>
    private static bool IsInside(object sender, PointerRoutedEventArgs e) =>
        sender is FrameworkElement element &&
        new Rect(0, 0, element.ActualWidth, element.ActualHeight).Contains(e.GetCurrentPoint(element).Position);

    /// <summary>Esc closes the peek; F3 opens its card in the viewer. Other keys belong to the text (Ctrl+C, Ctrl+A, arrows).</summary>
    /// <param name="sender">The surface.</param>
    /// <param name="e">Key data.</param>
    private void TextPeek_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            HideTextPeek();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.F3 && textPeekCard is { } card)
        {
            ViewCard(card);
            e.Handled = true;
        }
    }

    /// <summary>
    /// A right-click in the peek: the card's own menu (Paste, Pin, View, Groups, Delete, …), with Copy and Select all for the
    /// selection on top.
    /// </summary>
    /// <param name="sender">The peek's surface.</param>
    /// <param name="position">Where, relative to the text.</param>
    private void TextPeek_ContextMenuRequested(object? sender, Point position)
    {
        if (textPeekCard is not { } card || textPeekPresenter is not { } presenter)
        {
            return;
        }

        var copy = MenuItem(UiText.Translate("Copy"), "\uE8C8", "Ctrl+C", presenter.CopySelection);
        copy.IsEnabled = presenter.SelectedText.Length > 0;
        var selectAll = MenuItem(UiText.Translate("Select all"), "\uE8B3", "Ctrl+A", presenter.SelectAll);
        textPeekMenuOpen = true;
        var menu = ShowItemMenu(card, presenter.Editor, position, [copy, selectAll, new MenuFlyoutSeparator()]);
        menu.Closed += (_, _) =>
        {
            textPeekMenuOpen = false;

            // A card deleted or forgotten from its menu is gone from the list: its peek goes with it.
            if (!ViewModel.Items.Contains(card))
            {
                HideTextPeek();
                return;
            }

            // The menu's closing gave the keyboard to the panel's search box (Popup_Closed): the peek's text takes it back,
            // queued after that, so Esc and Ctrl+C keep working on the peek.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (textPeekPopup is not null)
                {
                    presenter.Editor.Focus(FocusState.Programmatic);
                }
            });
        };
    }

    /// <summary>Whether the cursor has left the spot where the peek opened (past the image peek's threshold).</summary>
    /// <returns><see langword="true"/> when it has, or when its position cannot be read.</returns>
    private bool TextPeekCursorMoved()
    {
        if (textPeekAnchor is not { } anchor || ScreenPointer.Cursor() is not { } now)
        {
            return true;
        }

        long dx = now.X - anchor.X;
        long dy = now.Y - anchor.Y;
        return ((dx * dx) + (dy * dy)) > (long)PeekMoveThresholdPx * PeekMoveThresholdPx;
    }

    /// <summary>Closes the text peek, if any (safe to call when none is open).</summary>
    private void HideTextPeek()
    {
        textHoverTimer?.Stop();
        textPeekLoad?.Cancel();
        if (textPeekPopup is not { } popup)
        {
            return;
        }

        // Cleared first: closing raises Closed (the count, the keyboard), and a late pointer event must find no peek. The
        // text surface leaves this peek's layout now (not when Closed comes): the next peek may want it at once, and an
        // element has one parent.
        if (textPeekPresenter is not null)
        {
            textPeekLayout?.Children.Remove(textPeekPresenter.View);
        }

        textPeekPopup = null;
        textPeekCard = null;
        textPeekFrame = null;
        textPeekLayout = null;
        textPeekAnchor = null;
        textPeekMenuOpen = false;
        textHoverCard = null;
        textHoverElement = null;
        popup.IsOpen = false;
    }

    /// <summary>
    /// Gives the keyboard to a viewer that is open when a popup closes under it: the text viewer's text (or the viewer
    /// itself while the text loads), or the image viewer.
    /// </summary>
    /// <returns>Whether a viewer took the keyboard (else the caller gives it to the search box).</returns>
    /// <remarks>
    /// A viewer opened from a menu's View opens before that menu has finished closing, and the peek's popup closes as the
    /// viewer opens. Their closing handed the keyboard to the search box, where keys are ignored while a popup is open, so
    /// Esc no longer closed the viewer (QA, 2026-11-13).
    /// </remarks>
    private bool FocusOpenViewer()
    {
        if (textViewer is { } state)
        {
            var target = state.View is not null && textViewerPresenter is { } presenter ? presenter.Editor : state.Popup.Child;
            target?.Focus(FocusState.Programmatic);
            return true;
        }

        if (viewerPopup?.Child is { } imageViewer)
        {
            imageViewer.Focus(FocusState.Programmatic);
            return true;
        }

        return false;
    }

    /// <summary>Opens the text viewer for a card: dims the screen, then shows the whole text once it is read and prepared.</summary>
    /// <param name="card">The card.</param>
    /// <returns>A task completing when the text shows or the load failed (failures are logged and shown, never thrown).</returns>
    private async Task ShowTextViewerAsync(ClipItemViewModel card)
    {
        if (Root.XamlRoot is null)
        {
            return;
        }

        HideTextPeek();
        CloseTextViewer();
        var settings = controller.Settings.Current;
        var state = OpenTextViewerPopup(card, settings);
        try
        {
            var token = state.Work.Token;
            var text = await LoadCardTextAsync(card, token).ConfigureAwait(true);
            if (textViewer != state)
            {
                return;
            }

            if (text is null)
            {
                ShowTextViewerProblem(state, UiText.Translate("This text could not be loaded."));
                return;
            }

            var palette = CurrentTextPalette();
            var style = CurrentTextStyle(state.TextSize);
            var prepared = await PrepareTextAsync(text, int.MaxValue, null, null, state.Formatted, palette, style, position: 0, token).ConfigureAwait(true);
            if (textViewer != state || token.IsCancellationRequested)
            {
                return;
            }

            ApplyTextViewerContent(state, prepared, style, palette, scrollTo: null);
        }
        catch (OperationCanceledException)
        {
            // Closed while loading.
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Showing the text viewer failed: {ex.Message}");
            if (textViewer == state)
            {
                ShowTextViewerProblem(state, UiText.Translate("This text could not be loaded."));
            }
        }
    }

    /// <summary>Builds and opens the viewer's popup (dim backdrop, the document card, its toolbar), waiting for its text.</summary>
    /// <param name="card">The card.</param>
    /// <param name="settings">The settings (text size, wrap, line numbers).</param>
    /// <returns>The viewer's state.</returns>
    private TextViewerState OpenTextViewerPopup(ClipItemViewModel card, Core.Settings.AppSettings settings)
    {
        var geometry = MonitorOverlayGeometry();
        var presenter = textViewerPresenter ??= textPeekPresenter ??= CreateTextPresenter();

        var closeButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 14 },
            Width = 34,
            Height = 30,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(closeButton, UiText.Translate("Close text viewer"));
        ToolTipService.SetToolTip(closeButton, UiText.Translate("Close (Esc)"));
        closeButton.Click += (_, _) => CloseTextViewer();

        var formattedButton = new ToggleButton { Content = UiText.Translate("Formatted"), IsChecked = true, Padding = new Thickness(10, 4, 10, 5) };
        var asCopiedButton = new ToggleButton { Content = UiText.Translate("As copied"), Padding = new Thickness(10, 4, 10, 5) };
        var switchPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Visibility = Visibility.Collapsed, Children = { formattedButton, asCopiedButton } };
        var wrapButton = new ToggleButton { Content = UiText.Translate("Wrap"), IsChecked = settings.TextViewerWrap, Padding = new Thickness(10, 4, 10, 5) };
        ToolTipService.SetToolTip(wrapButton, UiText.Translate("Wrap long lines (off: scroll sideways)"));
        var linesButton = new ToggleButton { Content = UiText.Translate("Line numbers"), IsChecked = settings.TextViewerLineNumbers, Padding = new Thickness(10, 4, 10, 5) };
        var languageButton = new DropDownButton { Content = LanguageName(TextLanguage.Markdown), Padding = new Thickness(10, 4, 8, 5), IsEnabled = false };
        AutomationProperties.SetName(languageButton, UiText.Translate("Text language"));
        ToolTipService.SetToolTip(languageButton, UiText.Translate("Show the text as Markdown, as code in another language, or as plain text"));
        var sizeDown = new Button { Content = "A−", Padding = new Thickness(8, 4, 8, 5) };
        var sizeUp = new Button { Content = "A+", Padding = new Thickness(8, 4, 8, 5) };
        AutomationProperties.SetName(sizeDown, UiText.Translate("Smaller text"));
        AutomationProperties.SetName(sizeUp, UiText.Translate("Larger text"));
        ToolTipService.SetToolTip(sizeDown, UiText.Translate("Smaller text (Ctrl+Minus)"));
        ToolTipService.SetToolTip(sizeUp, UiText.Translate("Larger text (Ctrl+Plus)"));
        var sizeLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 44, TextAlignment = TextAlignment.Center, FontSize = 12 };

        // Pages of a long text (TextViewRendering.Pages): hidden until a text has more than one.
        var previousPage = new Button { Content = new FontIcon { Glyph = "\uE76B", FontSize = 12 }, Padding = new Thickness(8, 7, 8, 7) };
        var nextPage = new Button { Content = new FontIcon { Glyph = "\uE76C", FontSize = 12 }, Padding = new Thickness(8, 7, 8, 7) };
        AutomationProperties.SetName(previousPage, UiText.Translate("Previous page"));
        AutomationProperties.SetName(nextPage, UiText.Translate("Next page"));
        ToolTipService.SetToolTip(previousPage, UiText.Translate("Previous page (Ctrl+PageUp)"));
        ToolTipService.SetToolTip(nextPage, UiText.Translate("Next page (Ctrl+PageDown)"));
        var pageLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 96, TextAlignment = TextAlignment.Center, FontSize = 12 };
        var pagePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Children = { previousPage, pageLabel, nextPage },
        };

        var toolbar = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sizePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { sizeDown, sizeLabel, sizeUp } };
        Grid.SetColumn(wrapButton, 1);
        Grid.SetColumn(linesButton, 2);
        Grid.SetColumn(languageButton, 3);
        Grid.SetColumn(sizePanel, 5);
        toolbar.Children.Add(switchPanel);
        toolbar.Children.Add(wrapButton);
        toolbar.Children.Add(linesButton);
        toolbar.Children.Add(languageButton);
        toolbar.Children.Add(sizePanel);
        Grid.SetColumn(pagePanel, 4);
        toolbar.Children.Add(pagePanel);

        var ring = new ProgressRing { IsActive = true, Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var status = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Style = OverlayStyle("OverlayTertiaryTextStyle"),
        };

        var content = new Grid();
        DetachFromParent(presenter.View);
        content.Children.Add(presenter.View);
        content.Children.Add(ring);

        // Transparent while the text loads, not collapsed: a collapsed box gets its template only when it shows, after
        // its text, and RichEdit then drops the text's fonts and colors (TextPreviewPresenter.Show's remarks).
        presenter.View.Visibility = Visibility.Visible;
        presenter.View.Opacity = 0;
        presenter.View.IsHitTestVisible = false;

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = BuildTextHeader(card, TextLanguage.Markdown, lines: null, closeButton);
        Grid.SetRow(toolbar, 1);
        Grid.SetRow(content, 2);
        Grid.SetRow(status, 3);
        layout.Children.Add(header);
        layout.Children.Add(toolbar);
        layout.Children.Add(content);
        layout.Children.Add(status);

        double width = Math.Min(geometry.Width * TextViewerFraction, TextViewerMaxWidthDip);
        double height = geometry.Height * TextViewerFraction;
        double left = Math.Clamp((Root.ActualWidth / 2) - geometry.OffsetX - (width / 2), 0, Math.Max(0, geometry.Width - width));
        double top = Math.Clamp((Root.ActualHeight / 2) - geometry.OffsetY - (height / 2), 0, Math.Max(0, geometry.Height - height));
        var document = new Border
        {
            Width = width,
            Height = height,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(left, top, 0, 0),
            Padding = new Thickness(16, 12, 12, 12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Style = OverlayStyle("OverlayFrameStyle"),
            Child = layout,
        };

        // A tap on the document is the document's: only a tap on the dim backdrop around it closes the viewer.
        document.Tapped += (_, e) => e.Handled = true;
        var dim = new Grid
        {
            Width = geometry.Width,
            Height = geometry.Height,
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0xB8, 0, 0, 0)),
            RequestedTheme = Root.RequestedTheme,
            IsTabStop = true,
        };
        dim.Children.Add(document);
        dim.Tapped += (_, _) => CloseTextViewer();

        var popup = new Popup
        {
            XamlRoot = Root.XamlRoot,
            ShouldConstrainToRootBounds = false,
            IsLightDismissEnabled = false,
            HorizontalOffset = geometry.OffsetX,
            VerticalOffset = geometry.OffsetY,
            Child = dim,
        };

        var state = new TextViewerState
        {
            Card = card,
            Popup = popup,
            Content = content,
            TextSize = settings.TextViewerTextSize,
            Wrap = settings.TextViewerWrap,
            LineNumbers = settings.TextViewerLineNumbers,
            Ring = ring,
            FormattedButton = formattedButton,
            AsCopiedButton = asCopiedButton,
            SwitchPanel = switchPanel,
            WrapButton = wrapButton,
            LinesButton = linesButton,
            LanguageButton = languageButton,
            SizeLabel = sizeLabel,
            Status = status,
            PagePanel = pagePanel,
            PageLabel = pageLabel,
            PreviousPageButton = previousPage,
            NextPageButton = nextPage,
        };
        previousPage.Click += (_, _) => _ = GoToViewerPageAsync(state, state.Page - 1);
        nextPage.Click += (_, _) => _ = GoToViewerPageAsync(state, state.Page + 1);
        UpdateSizeLabel(state);

        formattedButton.Click += (_, _) => SetViewerFormatted(state, true);
        asCopiedButton.Click += (_, _) => SetViewerFormatted(state, false);
        wrapButton.Click += (_, _) =>
        {
            state.Wrap = wrapButton.IsChecked == true;
            presenter.SetWrap(state.Wrap);
            controller.Settings.Update(s => s with { TextViewerWrap = state.Wrap });
        };
        linesButton.Click += (_, _) =>
        {
            state.LineNumbers = linesButton.IsChecked == true;
            presenter.SetLineNumbers(state.LineNumbers);
            controller.Settings.Update(s => s with { TextViewerLineNumbers = state.LineNumbers });
        };
        sizeDown.Click += (_, _) => ChangeViewerTextSize(state, -TextSizeStep);
        sizeUp.Click += (_, _) => ChangeViewerTextSize(state, TextSizeStep);
        languageButton.Flyout = BuildLanguageMenu(state);
        dim.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(TextViewer_KeyDown), handledEventsToo: true);
        document.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(TextViewer_PointerWheelChanged), handledEventsToo: true);
        presenter.ContextMenuRequested -= TextPeek_ContextMenuRequested;
        presenter.ContextMenuRequested -= TextViewer_ContextMenuRequested;
        presenter.ContextMenuRequested += TextViewer_ContextMenuRequested;

        // Counted before it opens (see the peek); Popup_Closed balances the count and gives the keyboard back.
        popup.Closed += Popup_Closed;
        textViewer = state;
        openPopups++;
        popup.IsOpen = true;
        dim.Focus(FocusState.Programmatic);
        return state;
    }

    /// <summary>Builds the language picker's menu: every language, the detected one marked, the shown one checked.</summary>
    /// <param name="state">The viewer.</param>
    /// <returns>The menu (its marks refreshed each time it opens).</returns>
    /// <remarks>
    /// The items exist from the start: WinUI's <see cref="MenuFlyout"/> does not open while it has none, so items added in
    /// its <c>Opening</c> event never showed (QA, 2026-11-13). <c>Opening</c> only renames the detected one and checks the
    /// one shown.
    /// </remarks>
    private MenuFlyout BuildLanguageMenu(TextViewerState state)
    {
        var menu = new MenuFlyout();
        var items = new List<(TextLanguage Language, RadioMenuFlyoutItem Item)>();
        foreach (var language in TextLanguages.All)
        {
            var item = new RadioMenuFlyoutItem { Text = LanguageName(language), GroupName = "TextViewerLanguage" };
            var chosen = language;
            item.Click += (_, _) => SetViewerLanguage(state, chosen);
            menu.Items.Add(item);
            items.Add((language, item));
        }

        menu.Opening += (_, _) =>
        {
            var detected = state.Source?.DetectedLanguage ?? TextLanguage.Markdown;
            var shown = state.View?.Language ?? detected;
            foreach (var (language, item) in items)
            {
                var name = LanguageName(language);
                item.Text = language == detected ? UiText.Format($"{name} (detected)") : name;
                item.IsChecked = language == shown;
            }
        };

        // Counted like every menu of the panel, so its opening does not conceal the panel.
        menu.Opened += Popup_Opened;
        menu.Closed += (_, _) => openPopups = Math.Max(0, openPopups - 1);
        return menu;
    }

    /// <summary>Shows a prepared text in the viewer: its document, the toolbar's state, the header and the note.</summary>
    /// <param name="state">The viewer.</param>
    /// <param name="prepared">The prepared text.</param>
    /// <param name="style">Fonts and sizes.</param>
    /// <param name="palette">The theme's colors.</param>
    /// <param name="scrollTo">
    /// A position in the view's text to bring to the top (a re-render at another size comes back to the same place), or
    /// <see langword="null"/> for the page's start.
    /// </param>
    private void ApplyTextViewerContent(TextViewerState state, PreparedText prepared, TextViewStyle style, SyntaxPalette palette, int? scrollTo)
    {
        if (textViewerPresenter is not { } presenter)
        {
            return;
        }

        state.Source = prepared.Source;
        state.View = prepared.View;
        state.Page = prepared.Page.Index;
        state.Ring.IsActive = false;
        state.Ring.Visibility = Visibility.Collapsed;
        presenter.View.Opacity = 1;
        presenter.View.IsHitTestVisible = true;
        presenter.Show(prepared.View, prepared.Page, prepared.Rtf, style, palette, state.Wrap, state.LineNumbers);
        if (scrollTo is { } position && position > prepared.Page.Start)
        {
            presenter.ScrollToPosition(position);
        }

        UpdatePageControls(state);

        state.SwitchPanel.Visibility = prepared.View.HasFormattedAlternative ? Visibility.Visible : Visibility.Collapsed;
        state.FormattedButton.IsChecked = state.Formatted;
        state.AsCopiedButton.IsChecked = !state.Formatted;
        state.LinesButton.Visibility = prepared.View.IsDocument ? Visibility.Collapsed : Visibility.Visible;
        state.LanguageButton.Content = LanguageName(prepared.View.Language);
        state.LanguageButton.IsEnabled = true;
        int lines = prepared.Source.OriginalLineCount;
        int characters = prepared.Source.OriginalLength;
        var pieces = new List<string>(3)
        {
            UiText.Plural(lines, $"{lines:N0} line", $"{lines:N0} lines"),
            UiText.Plural(characters, $"{characters:N0} character", $"{characters:N0} characters"),
        };
        if (TextLimitNote(prepared) is { Length: > 0 } note)
        {
            pieces.Add(note);
        }

        // One line of pieces: in a right-to-left language it reads from the count, at the right.
        state.Status.Text = UiText.JoinLine(" · ", pieces);
        presenter.Editor.Focus(FocusState.Programmatic);
    }

    /// <summary>Shows a problem in the viewer instead of a text (it could not be read).</summary>
    /// <param name="state">The viewer.</param>
    /// <param name="message">What went wrong, for people.</param>
    private static void ShowTextViewerProblem(TextViewerState state, string message)
    {
        state.Ring.IsActive = false;
        state.Ring.Visibility = Visibility.Collapsed;
        state.Status.Text = message;
    }

    /// <summary>
    /// Shows another page of the text in the viewer (its page controls, Ctrl+PageUp and Ctrl+PageDown): the same view, so
    /// only that page's RTF is made. A page past either end is ignored.
    /// </summary>
    /// <param name="state">The viewer.</param>
    /// <param name="pageIndex">The page's index.</param>
    /// <returns>A task completing when the page shows (failures are logged, never thrown).</returns>
    private async Task GoToViewerPageAsync(TextViewerState state, int pageIndex)
    {
        if (textViewer != state || state.Source is not { } source || state.View is not { } view ||
            pageIndex < 0 || pageIndex >= view.Pages.Count || pageIndex == state.Page)
        {
            return;
        }

        // A newer request (another page, a size, a language) takes over from this one.
        state.Work.Cancel();
        state.Work = new CancellationTokenSource();
        var token = state.Work.Token;
        try
        {
            var palette = CurrentTextPalette();
            var style = CurrentTextStyle(state.TextSize);
            var prepared = await PreparePageAsync(source, view, pageIndex, palette, style, token).ConfigureAwait(true);
            if (textViewer == state && !token.IsCancellationRequested)
            {
                ApplyTextViewerContent(state, prepared, style, palette, scrollTo: null);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer change took over, or the viewer closed.
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Showing another page of the text viewer failed: {ex.Message}");
        }
    }

    /// <summary>Shows the page controls for a text of several pages ("Page 3 of 94", the arrows enabled where they lead).</summary>
    /// <param name="state">The viewer.</param>
    private static void UpdatePageControls(TextViewerState state)
    {
        int count = state.View?.Pages.Count ?? 1;
        state.PagePanel.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (count > 1)
        {
            int page = state.Page + 1;
            state.PageLabel.Text = UiText.Format($"Page {page:N0} of {count:N0}");
            state.PreviousPageButton.IsEnabled = state.Page > 0;
            state.NextPageButton.IsEnabled = state.Page < count - 1;
        }
    }

    /// <summary>The Formatted / As copied switch: shows the other view of the same text.</summary>
    /// <param name="state">The viewer.</param>
    /// <param name="formatted">Formatted when <see langword="true"/>.</param>
    private void SetViewerFormatted(TextViewerState state, bool formatted)
    {
        state.Formatted = formatted;
        state.FormattedButton.IsChecked = formatted;
        state.AsCopiedButton.IsChecked = !formatted;
        _ = RerenderTextViewerAsync(state, keepPosition: false);
    }

    /// <summary>The language picker: shows the text as another language.</summary>
    /// <param name="state">The viewer.</param>
    /// <param name="language">The language picked.</param>
    private void SetViewerLanguage(TextViewerState state, TextLanguage language)
    {
        state.Language = language;
        _ = RerenderTextViewerAsync(state, keepPosition: false);
    }

    /// <summary>
    /// Changes the viewer's text size by a step (clamped to 50–300 %), redraws at the new size at the same place in the text,
    /// and saves the size a moment after the last change.
    /// </summary>
    /// <param name="state">The viewer.</param>
    /// <param name="step">The change; 0 resets to 100 %.</param>
    private void ChangeViewerTextSize(TextViewerState state, double step)
    {
        double size = step == 0 ? 1 : Math.Round(Math.Clamp(state.TextSize + step, TextViewStyle.MinScale, TextViewStyle.MaxScale), 2);
        if (Math.Abs(size - state.TextSize) < 0.001)
        {
            return;
        }

        state.TextSize = size;
        UpdateSizeLabel(state);
        _ = RerenderTextViewerAsync(state, keepPosition: true);

        // One settings write per burst of Ctrl+wheel notches, not one per notch (every write rewrites the file).
        if (state.SaveSize is null)
        {
            state.SaveSize = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            state.SaveSize.Tick += (_, _) =>
            {
                state.SaveSize.Stop();
                controller.Settings.Update(s => s with { TextViewerTextSize = state.TextSize });
            };
        }

        state.SaveSize.Stop();
        state.SaveSize.Start();
    }

    /// <summary>Shows the text size in percent next to its buttons.</summary>
    /// <param name="state">The viewer.</param>
    private static void UpdateSizeLabel(TextViewerState state) =>
        state.SizeLabel.Text = $"{state.TextSize * 100:0} %";

    /// <summary>
    /// Prepares the viewer's text again for its current choices (language, switch, size) off the UI thread and shows it;
    /// a newer change cancels an older one.
    /// </summary>
    /// <param name="state">The viewer.</param>
    /// <param name="keepPosition">Come back to the same place in the text.</param>
    /// <returns>A task completing when shown or superseded (failures are logged).</returns>
    private async Task RerenderTextViewerAsync(TextViewerState state, bool keepPosition)
    {
        if (state.Source is not { } source)
        {
            return;
        }

        state.Work.Cancel();
        state.Work = new CancellationTokenSource();
        var token = state.Work.Token;
        try
        {
            // The place to come back to, in the text (the page it lies on is the one prepared).
            int? position = keepPosition ? textViewerPresenter?.FirstVisiblePosition() : null;
            var palette = CurrentTextPalette();
            var style = CurrentTextStyle(state.TextSize);
            var prepared = await PrepareTextAsync(source.Text, int.MaxValue, source, state.Language, state.Formatted, palette, style, position ?? 0, token).ConfigureAwait(true);
            if (textViewer == state && !token.IsCancellationRequested)
            {
                ApplyTextViewerContent(state, prepared, style, palette, position);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer change took over, or the viewer closed.
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Redrawing the text viewer failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The viewer's keys: Esc closes, Ctrl+= and Ctrl+- change the text size, Ctrl+0 resets it. The text keeps its own keys
    /// (Ctrl+C, Ctrl+A, arrows, Page Up/Down).
    /// </summary>
    /// <param name="sender">The backdrop.</param>
    /// <param name="e">Key data.</param>
    private void TextViewer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (textViewer is not { } state)
        {
            return;
        }

        bool ctrl = IsKeyDown(VirtualKey.Control);
        switch (e.Key)
        {
            case VirtualKey.Escape:
                CloseTextViewer();
                e.Handled = true;
                break;
            case (VirtualKey)187 or VirtualKey.Add when ctrl: // '=' / '+' on the main keyboard (VK_OEM_PLUS), and the keypad's +
                ChangeViewerTextSize(state, TextSizeStep);
                e.Handled = true;
                break;
            case (VirtualKey)189 or VirtualKey.Subtract when ctrl: // '-' (VK_OEM_MINUS), and the keypad's -
                ChangeViewerTextSize(state, -TextSizeStep);
                e.Handled = true;
                break;
            case VirtualKey.Number0 or VirtualKey.NumberPad0 when ctrl:
                ChangeViewerTextSize(state, 0);
                e.Handled = true;
                break;
            case VirtualKey.PageDown when ctrl:
                _ = GoToViewerPageAsync(state, state.Page + 1);
                e.Handled = true;
                break;
            case VirtualKey.PageUp when ctrl:
                _ = GoToViewerPageAsync(state, state.Page - 1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Ctrl+wheel over the viewer's document changes the text size (the wheel alone scrolls the text).</summary>
    /// <param name="sender">The document.</param>
    /// <param name="e">Pointer data.</param>
    private void TextViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (textViewer is not { } state || !IsKeyDown(VirtualKey.Control) || sender is not UIElement element)
        {
            return;
        }

        int delta = e.GetCurrentPoint(element).Properties.MouseWheelDelta;
        if (delta != 0)
        {
            ChangeViewerTextSize(state, delta > 0 ? TextSizeStep : -TextSizeStep);
        }

        e.Handled = true;
    }

    /// <summary>A right-click in the viewer's text: Copy and Select all for the selection, then the card's own menu.</summary>
    /// <param name="sender">The viewer's surface.</param>
    /// <param name="position">Where, relative to the text.</param>
    private void TextViewer_ContextMenuRequested(object? sender, Point position)
    {
        if (textViewer is not { } state || textViewerPresenter is not { } presenter)
        {
            return;
        }

        var copy = MenuItem(UiText.Translate("Copy"), "\uE8C8", "Ctrl+C", presenter.CopySelection);
        copy.IsEnabled = presenter.SelectedText.Length > 0;
        var selectAll = MenuItem(UiText.Translate("Select all"), "\uE8B3", "Ctrl+A", presenter.SelectAll);
        var menu = ShowItemMenu(state.Card, presenter.Editor, position, [copy, selectAll, new MenuFlyoutSeparator()]);
        menu.Closed += (_, _) =>
        {
            if (!ViewModel.Items.Contains(state.Card))
            {
                CloseTextViewer();
                return;
            }

            // Popup_Closed gave the keyboard to the panel behind the viewer: the viewer takes it back (Esc must close it).
            DispatcherQueue.TryEnqueue(() =>
            {
                if (textViewer == state)
                {
                    presenter.Editor.Focus(FocusState.Programmatic);
                }
            });
        };
    }

    /// <summary>Closes the text viewer, if any (safe to call when none is open).</summary>
    private void CloseTextViewer()
    {
        if (textViewer is not { } state)
        {
            return;
        }

        // Cleared first, so the load continuation and late events find no viewer. The text surface leaves the viewer now,
        // so a viewer opened right after can take it.
        textViewer = null;
        if (textViewerPresenter is not null)
        {
            state.Content.Children.Remove(textViewerPresenter.View);
        }

        state.Work.Cancel();
        if (state.SaveSize is { IsEnabled: true } pending)
        {
            pending.Stop();
            controller.Settings.Update(s => s with { TextViewerTextSize = state.TextSize });
        }

        state.Popup.IsOpen = false; // raises Closed → Popup_Closed (the count, the keyboard)
    }
}
