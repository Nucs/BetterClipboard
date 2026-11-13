using System.Diagnostics;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.TextView;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace BetterClipboard.App.Views;

/// <summary>
/// The read-only text surface of the text viewer and the hover peek (ClipboardFlyout.TextPreview.cs): a WinUI
/// <see cref="RichEditBox"/> holding a prepared RTF document (<see cref="TextViewRendering.ToRtf(TextViewPage, SyntaxPalette, TextViewStyle, CancellationToken)"/>), and a line-number
/// gutter left of it for code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why RichEdit.</b> It is the one WinUI control that shows colored text with a selection across all of it: RichEdit
/// lays out lazily (Notepad uses the same engine), and one RTF parse in native code colors a whole page in one call. A
/// <see cref="TextBlock"/> lays out all of its text, and per-line elements cannot share a selection.
/// </para>
/// <para>
/// <b>Why pages.</b> RichEdit still takes text slowly, on the UI thread: in the QA (2026-11-13, a test desktop without a
/// GPU) about 2.5 µs per character as plain text and 7 µs as RTF, so a 6 MB text froze the viewer for 15 s. It gets one
/// page at a time (<see cref="TextViewRendering.Pages"/>), and a page without colors goes in as plain text, in the font
/// and color set as the default character format beforehand (a format pass over 6 MB took about 15 s more).
/// </para>
/// <para>
/// <b>Copying.</b> Ctrl+C and the menu copy plain text (<see cref="RichEditBox.ClipboardCopyFormat"/>): the viewer's colors
/// are not the copy's, and pasting them into another app would be wrong. The copy is a new copy like any other, so the
/// history records it.
/// </para>
/// <para>
/// <b>Line numbers.</b> RichEdit has no gutter. The numbers are drawn beside it, each at the top of its line's first row as
/// RichEdit reports it (<see cref="ITextRange.GetPoint"/>), so a wrapped line keeps one number. Only the lines in view are
/// asked about, again whenever the view scrolls or resizes.
/// </para>
/// <para>UI thread only: it creates XAML elements.</para>
/// </remarks>
internal sealed class TextPreviewPresenter
{
    /// <summary>The gap between the gutter's numbers and the text, in DIPs.</summary>
    private const double GutterGapDip = 12;

    /// <summary>The lines a wheel notch turned outside the text scrolls (<see cref="ScrollByWheel"/>): Windows' default, three.</summary>
    private const int WheelLines = 3;

    /// <summary>A line's height as a multiple of its font size: about the height RichEdit lays a line out at.</summary>
    private const double LineHeightFactor = 1.35;

    /// <summary>The RichEditBox's padding around its text, in DIPs; the gutter adds the top to RichEdit's line positions.</summary>
    private static readonly Thickness EditorPadding = new(6, 4, 6, 4);

    /// <summary>The template brushes a RichEditBox paints its box with; all made transparent, so it never flashes a field color.</summary>
    private static readonly string[] BoxBrushKeys =
    [
        "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
        "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled",
    ];

    /// <summary>The two columns: the gutter, then the text.</summary>
    private readonly Grid root = new();

    /// <summary>Where the numbers are placed; clipped to its own bounds.</summary>
    private readonly Canvas gutter = new();

    /// <summary>The text.</summary>
    private readonly RichEditBox editor;

    /// <summary>The number blocks, reused as the view scrolls (one per line in view).</summary>
    private readonly List<TextBlock> numbers = [];

    /// <summary>
    /// The numbers' style (the theme's tertiary text color). A style, not a brush: its theme resource resolves in the
    /// overlay's theme, and again when that changes.
    /// </summary>
    private readonly Style numberStyle;

    /// <summary>The numbers' font: the code font.</summary>
    private readonly FontFamily codeFont;

    /// <summary>The RichEditBox's own scroller (its template's "ContentElement"), once it is loaded.</summary>
    private ScrollViewer? scroller;

    /// <summary>
    /// Where the wheel scrolls sent from outside the text are going (<see cref="ScrollByWheel"/>), while their animation
    /// runs: quick notches add up from there, not from where the animation is. <see langword="null"/> when none runs.
    /// </summary>
    private double? wheelTargetX;

    /// <summary>The vertical counterpart of <see cref="wheelTargetX"/>.</summary>
    private double? wheelTargetY;

    /// <summary>What is shown, or <see langword="null"/> before the first <see cref="Show"/>.</summary>
    private TextViewRendering? rendering;

    /// <summary>
    /// The page of <see cref="rendering"/> RichEdit holds: its positions are the page's own, and the gutter numbers its
    /// lines as lines of the whole text.
    /// </summary>
    private TextViewPage page;

    /// <summary>Whether the gutter shows (the setting, for a code view).</summary>
    private bool lineNumbersWanted;

    /// <summary>The size of the code font, in DIPs (the numbers use it too).</summary>
    private double numberSize = 13;

    /// <summary>Texts shorter than this are never timed (they show in a frame or two).</summary>
    private const int TimedMinChars = 50_000;

    /// <summary>A text that took at least this long to show is logged with its timings.</summary>
    private const long SlowShowMs = 500;

    /// <summary>
    /// A view <see cref="Show"/> was given before the box was loaded, shown from <see cref="Editor_Loaded"/>; or
    /// <see langword="null"/>.
    /// </summary>
    private PendingShow? pendingShow;

    /// <summary>Whether a gutter update is queued (one per layout pass, however many events ask).</summary>
    private bool gutterQueued;

    /// <summary>
    /// Creates the surface.
    /// </summary>
    /// <param name="codeFont">The code font (the numbers are drawn in it).</param>
    /// <param name="numberStyle">A <see cref="TextBlock"/> style that gives the numbers their color.</param>
    public TextPreviewPresenter(FontFamily codeFont, Style numberStyle)
    {
        this.codeFont = codeFont;
        this.numberStyle = numberStyle;
        editor = new RichEditBox
        {
            IsReadOnly = true,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            AcceptsReturn = true,
            ClipboardCopyFormat = RichEditClipboardFormat.PlainText,
            DisabledFormattingAccelerators = DisabledFormattingAccelerators.All,
            BorderThickness = new Thickness(0),
            Padding = EditorPadding,
            MinHeight = 0,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        // No floating command bar over a selection and no built-in menu: the owner of this surface shows its own
        // (ContextMenuRequested), with the card's actions.
        editor.SelectionFlyout = null;
        editor.ContextFlyout = null;
        var transparent = new SolidColorBrush(Colors.Transparent);
        foreach (var key in BoxBrushKeys)
        {
            editor.Resources[key] = transparent;
        }

        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        editor.Loaded += Editor_Loaded;
        editor.SizeChanged += (_, _) => QueueGutterUpdate();
        editor.ContextRequested += Editor_ContextRequested;

        gutter.IsHitTestVisible = false;
        gutter.SizeChanged += (_, e) => gutter.Clip = new RectangleGeometry { Rect = new global::Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(editor, 1);
        root.Children.Add(gutter);
        root.Children.Add(editor);
    }

    /// <summary>
    /// Raised when the user asks for a context menu over the text (right-click, the Menu key, Shift+F10), with where it
    /// should open relative to <see cref="Editor"/>.
    /// </summary>
    public event EventHandler<global::Windows.Foundation.Point>? ContextMenuRequested;

    /// <summary>The element to put into a layout.</summary>
    public FrameworkElement View => root;

    /// <summary>The RichEditBox itself (focus, key handling, the menu's placement target).</summary>
    public RichEditBox Editor => editor;

    /// <summary>The selected text, with <c>\r</c> line breaks as RichEdit holds them turned into <c>\n</c>; empty for none.</summary>
    public string SelectedText => editor.Document.Selection.Text.Replace('\r', '\n');

    /// <summary>
    /// Shows a view: loads its RTF (or, for a large plain view without colors, its text with one format), sets wrapping
    /// and the gutter, and scrolls to the top.
    /// </summary>
    /// <param name="view">The view.</param>
    /// <param name="page">The page of the view to show (one of its <see cref="TextViewRendering.Pages"/>).</param>
    /// <param name="rtf">The page's RTF (<see cref="TextViewRendering.ToRtf(TextViewPage, SyntaxPalette, TextViewStyle, CancellationToken)"/>), or <see langword="null"/> to load the plain text instead.</param>
    /// <param name="style">Fonts and sizes (used for the plain load and the numbers).</param>
    /// <param name="palette">The theme's colors (used for the plain load).</param>
    /// <param name="wrap">Whether long lines wrap.</param>
    /// <param name="lineNumbers">Whether the gutter shows (only ever for a code view).</param>
    /// <remarks>
    /// RichEdit drops an RTF document's fonts and colors when the box gets its template after the text was set: the
    /// viewer's first text came out in the interface font and color (QA, 2026-11-13), while the peek's box, warmed up
    /// beforehand, kept them. So the template is applied first, and a box that is not in a live tree yet is emptied now
    /// and gets the view once it is loaded (a frame later).
    /// </remarks>
    public void Show(TextViewRendering view, TextViewPage page, string? rtf, TextViewStyle style, SyntaxPalette palette, bool wrap, bool lineNumbers)
    {
        editor.ApplyTemplate();
        if (!editor.IsLoaded)
        {
            Clear();
            pendingShow = new PendingShow(view, page, rtf, style, palette, wrap, lineNumbers);
            return;
        }

        pendingShow = null;
        rendering = view;
        this.page = page;
        numberSize = style.CodeSize;
        lineNumbersWanted = lineNumbers;
        var clock = Stopwatch.StartNew();

        // RichEdit refuses new text while read-only, and keeps no undo history for text nobody edits (it would hold a copy).
        editor.IsReadOnly = false;
        editor.Document.UndoLimit = 0;
        if (rtf is not null)
        {
            editor.Document.SetText(TextSetOptions.FormatRtf, rtf);
        }
        else
        {
            // Plain text in one format: no RTF to build or parse for a text too large to color. The format is the
            // document's default, set before the text: formatting a range of megabytes afterwards is a second pass.
            var format = editor.Document.GetDefaultCharacterFormat();
            format.Name = style.CodeFont;
            format.Size = (float)(style.CodeSize * 0.75);
            format.ForegroundColor = ToColor(palette.Text);
            editor.Document.SetDefaultCharacterFormat(format);
            editor.Document.SetText(TextSetOptions.None, view.PageText(page));
        }

        long setMs = clock.ElapsedMilliseconds;
        editor.IsReadOnly = true;
        editor.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        editor.Document.Selection.SetRange(0, 0);
        wheelTargetX = null;
        wheelTargetY = null;
        scroller?.ChangeView(0, 0, null, disableAnimation: true);
        QueueGutterUpdate();
        TimeFirstLayout(page.Length, rtf is not null, setMs, clock);
    }

    /// <summary>
    /// Logs how long a large text took to show, when it took long: setting it, then the first layout after (where RichEdit
    /// breaks the text into lines). Content-free. Measured, not guessed: the size limits of the peek and the viewer rest on
    /// these numbers.
    /// </summary>
    /// <param name="characters">The text's length.</param>
    /// <param name="asRtf">Whether it was loaded as RTF (colored) rather than as plain text.</param>
    /// <param name="setMs">Milliseconds the text took to set.</param>
    /// <param name="clock">The clock started before the text was set.</param>
    private void TimeFirstLayout(int characters, bool asRtf, long setMs, Stopwatch clock)
    {
        if (characters < TimedMinChars)
        {
            return;
        }

        // One layout pass, then the handler goes: a handler left on LayoutUpdated runs after every layout of the window.
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            editor.LayoutUpdated -= handler;
            long totalMs = clock.ElapsedMilliseconds;
            if (totalMs >= SlowShowMs)
            {
                AppLog.Info($"Text surface: {characters:N0} characters as {(asRtf ? "RTF" : "plain text")} took {totalMs:N0} ms to show " +
                    $"(set {setMs:N0} ms, first layout {totalMs - setMs:N0} ms).");
            }
        };
        editor.LayoutUpdated += handler;
    }

    /// <summary>
    /// Scrolls the text as a wheel turned over it would, for a wheel turned elsewhere: the text peek takes the wheel from
    /// anywhere on its surface. Three lines a notch (Windows' default) at the text's size; notches that come quickly add up.
    /// </summary>
    /// <param name="delta">The wheel's delta: 120 a notch, positive away from the user (up), or to the right for a tilt.</param>
    /// <param name="horizontal">A sideways tilt: scrolls sideways (only lines that do not wrap reach past the edge).</param>
    public void ScrollByWheel(int delta, bool horizontal)
    {
        if (scroller is null || delta == 0)
        {
            return;
        }

        double step = delta / 120.0 * WheelLines * numberSize * LineHeightFactor;
        if (horizontal)
        {
            double target = Math.Clamp((wheelTargetX ?? scroller.HorizontalOffset) + step, 0, scroller.ScrollableWidth);
            wheelTargetX = target;
            scroller.ChangeView(target, null, null);
        }
        else
        {
            // Away from the user is up: toward the start of the text.
            double target = Math.Clamp((wheelTargetY ?? scroller.VerticalOffset) - step, 0, scroller.ScrollableHeight);
            wheelTargetY = target;
            scroller.ChangeView(null, target, null);
        }
    }

    /// <summary>
    /// The text scrolled: the gutter follows; once a scroll has come to rest, the next wheel notch starts from where the
    /// text is (<see cref="ScrollByWheel"/>).
    /// </summary>
    /// <param name="sender">The scroller.</param>
    /// <param name="e">Whether the scroll is still moving.</param>
    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate)
        {
            wheelTargetX = null;
            wheelTargetY = null;
        }

        QueueGutterUpdate();
    }

    /// <summary>Switches wrapping without reloading the text.</summary>
    /// <param name="wrap">Whether long lines wrap.</param>
    public void SetWrap(bool wrap)
    {
        editor.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        QueueGutterUpdate();
    }

    /// <summary>Shows or hides the gutter without reloading the text.</summary>
    /// <param name="lineNumbers">Whether it shows (for a code view).</param>
    public void SetLineNumbers(bool lineNumbers)
    {
        lineNumbersWanted = lineNumbers;
        QueueGutterUpdate();
    }

    /// <summary>The position of the first character in view, so a reload (another text size) can come back to it.</summary>
    /// <returns>A character position.</returns>
    public int FirstVisiblePosition()
    {
        try
        {
            // RichEdit counts in the page; the caller counts in the whole text.
            return page.Start + editor.Document.GetRangeFromPoint(new global::Windows.Foundation.Point(EditorPadding.Left + 1, EditorPadding.Top + 1), PointOptions.ClientCoordinates).StartPosition;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Reading the text viewer's first visible position failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>Scrolls a character position to the top of the view (after a reload at another text size).</summary>
    /// <param name="position">The position.</param>
    public void ScrollToPosition(int position)
    {
        try
        {
            int local = Math.Clamp(position - page.Start, 0, page.Length);
            editor.Document.GetRange(local, local).ScrollIntoView(PointOptions.Start);
            QueueGutterUpdate();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Scrolling the text viewer back to its place failed: {ex.Message}");
        }
    }

    /// <summary>Selects all of the text.</summary>
    public void SelectAll() => editor.Document.Selection.SetRange(0, int.MaxValue);

    /// <summary>Copies the selection the way Ctrl+C does (plain text).</summary>
    public void CopySelection() => editor.Document.Selection.Copy();

    /// <summary>
    /// Forgets the shown text (the overlay closed): the document and its formatting can be megabytes, and the next view loads
    /// its own anyway.
    /// </summary>
    public void Clear()
    {
        rendering = null;
        page = default;
        editor.IsReadOnly = false;
        editor.Document.SetText(TextSetOptions.None, string.Empty);
        editor.IsReadOnly = true;
        HideNumbers(0);
    }

    /// <summary>Finds the RichEditBox's own scroller once its template is applied, to follow its scrolling.</summary>
    /// <param name="sender">The RichEditBox.</param>
    /// <param name="e">Event data.</param>
    private void Editor_Loaded(object sender, RoutedEventArgs e)
    {
        if (scroller is null && FindScroller(editor) is { } found)
        {
            // Once: the box is loaded again each time it moves to another overlay, and its scroller stays the same.
            scroller = found;
            scroller.ViewChanging += (_, _) => QueueGutterUpdate();
            scroller.ViewChanged += Scroller_ViewChanged;
        }

        // A view given before the box was loaded (see Show's remarks).
        if (pendingShow is { } pending)
        {
            Show(pending.View, pending.Page, pending.Rtf, pending.Style, pending.Palette, pending.Wrap, pending.LineNumbers);
        }

        QueueGutterUpdate();
    }

    /// <summary>A context-menu request over the text: handled here (no built-in menu), passed on with its position.</summary>
    /// <param name="sender">The RichEditBox.</param>
    /// <param name="e">Event data.</param>
    private void Editor_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;

        // A keyboard request (Menu key, Shift+F10) has no pointer position: open near the top-left of the text.
        if (!e.TryGetPosition(editor, out var point))
        {
            point = new global::Windows.Foundation.Point(24, 24);
        }

        ContextMenuRequested?.Invoke(this, point);
    }

    /// <summary>Queues one gutter update for after the current layout pass (scrolling raises many events per frame).</summary>
    private void QueueGutterUpdate()
    {
        if (gutterQueued)
        {
            return;
        }

        gutterQueued = true;
        editor.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            gutterQueued = false;
            UpdateGutter();
        });
    }

    /// <summary>
    /// Places a number at the top of every line in view (RichEdit's own positions, so wrapped lines and any font fallback are
    /// followed), or hides the gutter for a Markdown document, plain views without numbers wanted, and no text.
    /// </summary>
    private void UpdateGutter()
    {
        if (rendering is not { IsDocument: false } view || !lineNumbersWanted || view.LineStarts.Count == 0 || !editor.IsLoaded)
        {
            gutter.Width = 0;
            HideNumbers(0);
            return;
        }

        try
        {
            var starts = view.LineStarts;
            int digits = starts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
            double digitWidth = numberSize * 0.6;
            gutter.Width = Math.Ceiling((Math.Max(2, digits) * digitWidth) + GutterGapDip);

            // The first line in view: the one holding the character at the view's top-left corner. RichEdit counts in the
            // page, the line starts in the whole text. A line that began on an earlier page (a page cut inside a very long
            // line) has its number there.
            int top = page.Start + editor.Document.GetRangeFromPoint(new global::Windows.Foundation.Point(EditorPadding.Left + 1, EditorPadding.Top + 1), PointOptions.ClientCoordinates).StartPosition;
            int line = TextViewRendering.LineOf(starts, top);
            if (starts[line] < page.Start)
            {
                line++;
            }

            // The line starting where this page ends is the next page's first; only the text's own last line (after a final
            // line break) starts at the last page's end.
            bool lastPage = page.End >= view.DisplayText.Length;
            double viewport = scroller?.ViewportHeight ?? editor.ActualHeight;
            int shown = 0;
            for (; line < starts.Count && (starts[line] < page.End || (lastPage && starts[line] == page.End)) && shown < 1000; line++)
            {
                int local = starts[line] - page.Start;
                editor.Document.GetRange(local, local).GetPoint(
                    HorizontalCharacterAlignment.Left, VerticalCharacterAlignment.Top, PointOptions.ClientCoordinates | PointOptions.AllowOffClient, out var point);

                // RichEdit's client coordinates start inside the box's padding; the gutter starts at the box's top.
                double y = point.Y + EditorPadding.Top;
                if (y > viewport + EditorPadding.Top)
                {
                    break;
                }

                var number = NumberBlock(shown++);
                number.Text = (line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                number.Width = gutter.Width - GutterGapDip;
                number.FontSize = numberSize;
                Canvas.SetTop(number, y);
            }

            HideNumbers(shown);
        }
        catch (Exception ex)
        {
            // A position RichEdit cannot report (a document being replaced) hides the numbers until the next update.
            AppLog.Warn($"Placing the text viewer's line numbers failed: {ex.Message}");
            HideNumbers(0);
        }
    }

    /// <summary>The number block at an index of the pool, made and added to the gutter on first use.</summary>
    /// <param name="index">The pool index.</param>
    /// <returns>The block, visible.</returns>
    private TextBlock NumberBlock(int index)
    {
        if (index == numbers.Count)
        {
            var block = new TextBlock
            {
                FontFamily = codeFont,
                Style = numberStyle,
                TextAlignment = TextAlignment.Right,
                IsTextScaleFactorEnabled = false,
            };
            numbers.Add(block);
            gutter.Children.Add(block);
        }

        numbers[index].Visibility = Visibility.Visible;
        return numbers[index];
    }

    /// <summary>Hides the pooled number blocks from <paramref name="from"/> on.</summary>
    /// <param name="from">The first index to hide.</param>
    private void HideNumbers(int from)
    {
        for (int i = from; i < numbers.Count; i++)
        {
            numbers[i].Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The first <see cref="ScrollViewer"/> inside an element's template.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The scroller, or <see langword="null"/>.</returns>
    private static ScrollViewer? FindScroller(DependencyObject element)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is ScrollViewer found)
            {
                return found;
            }

            if (FindScroller(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    /// <summary>The arguments of a <see cref="Show"/> call kept until the box is loaded.</summary>
    /// <param name="View">The view.</param>
    /// <param name="Page">The page of the view.</param>
    /// <param name="Rtf">The page's RTF, or <see langword="null"/> for the plain load.</param>
    /// <param name="Style">Fonts and sizes.</param>
    /// <param name="Palette">The theme's colors.</param>
    /// <param name="Wrap">Whether long lines wrap.</param>
    /// <param name="LineNumbers">Whether the gutter shows.</param>
    private sealed record PendingShow(TextViewRendering View, TextViewPage Page, string? Rtf, TextViewStyle Style, SyntaxPalette Palette, bool Wrap, bool LineNumbers);

    /// <summary>An <c>0xRRGGBB</c> color as an opaque WinUI color.</summary>
    /// <param name="rgb">The color.</param>
    /// <returns>The color.</returns>
    internal static global::Windows.UI.Color ToColor(uint rgb) =>
        global::Windows.UI.Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
