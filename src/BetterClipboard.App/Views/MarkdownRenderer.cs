using BetterClipboard.Core.Presentation;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace BetterClipboard.App.Views;

/// <summary>
/// The styles a <see cref="MarkdownRenderer"/> draws with. They come from the XAML of the view that shows the notes, so
/// their theme brushes resolve in that view's own theme (see <c>UpdateView.xaml</c>).
/// </summary>
/// <param name="Text">Body text: paragraphs, list items, table cells.</param>
/// <param name="CodeBorder">The box around a code block.</param>
/// <param name="QuoteBorder">The bar on the left of a block quote.</param>
/// <param name="Rule">A horizontal rule.</param>
/// <param name="TableCell">The frame of one table cell (its bottom line and padding).</param>
/// <param name="Monospace">The font of code, inline and in blocks.</param>
internal sealed record MarkdownStyles(Style Text, Style CodeBorder, Style QuoteBorder, Style Rule, Style TableCell, FontFamily Monospace);

/// <summary>
/// Draws parsed release notes (<see cref="MarkdownParser"/>) with plain WinUI text controls: one element per block,
/// appended to a panel.
/// </summary>
/// <remarks>
/// <para>
/// WinUI 3 has no Markdown control, and release notes need little: text with emphasis and links, headings, lists, code,
/// the odd table. Each block becomes a <see cref="TextBlock"/> (or a small grid around some), so the result scrolls,
/// wraps and follows the theme like any other text of the app.
/// </para>
/// <para>
/// <b>Links never navigate by themselves.</b> A <see cref="Hyperlink"/> gets no <c>NavigateUri</c>; its click is handed
/// to the caller, who decides how the app leaves for the browser (the panel conceals itself first). The parser has
/// already dropped every link that is not http or https.
/// </para>
/// <para>UI thread only: it creates XAML elements.</para>
/// </remarks>
internal static class MarkdownRenderer
{
    /// <summary>How far one list level indents, in DIPs: about the width of a bullet and its gap.</summary>
    private const double IndentDip = 18;

    /// <summary>The gap between a list marker and the item's text.</summary>
    private const double MarkerGapDip = 6;

    /// <summary>The narrowest marker column, so that bullets of one list line up whatever the glyph's own width.</summary>
    private const double MarkerMinWidthDip = 12;

    /// <summary>Extra space above a heading, on top of the panel's own spacing: a heading belongs to what follows it.</summary>
    private const double HeadingGapDip = 6;

    /// <summary>The font size of code, a little under the body's 14 so that long commands wrap less.</summary>
    private const double CodeFontSize = 12.5;

    /// <summary>
    /// The font of task-list boxes: Windows' symbol font draws the empty and the ticked box alike, as plain outlines.
    /// Created per use: a <see cref="FontFamily"/> is a XAML object and belongs to the thread that made it.
    /// </summary>
    private static FontFamily TaskMarkerFont => new("Segoe UI Symbol");

    /// <summary>
    /// Appends blocks to a panel, in order.
    /// </summary>
    /// <param name="target">The panel that receives the elements (a vertical <see cref="StackPanel"/>).</param>
    /// <param name="blocks">The parsed notes.</param>
    /// <param name="styles">The styles to draw with.</param>
    /// <param name="openLink">Called with a link's address when the user clicks it.</param>
    /// <param name="maxBlocks">The most blocks drawn; the rest is left out (see the return value).</param>
    /// <returns>How many blocks were left out because of <paramref name="maxBlocks"/>; 0 when everything was drawn.</returns>
    public static int Append(Panel target, IReadOnlyList<MarkdownBlock> blocks, MarkdownStyles styles, Action<Uri> openLink, int maxBlocks = int.MaxValue)
    {
        int drawn = 0;
        foreach (var block in blocks)
        {
            if (drawn >= maxBlocks)
            {
                break;
            }

            // The first element of a panel needs no gap above it, not even a heading.
            bool first = target.Children.Count == 0;
            target.Children.Add(Create(block, styles, openLink, first));
            drawn++;
        }

        return blocks.Count - drawn;
    }

    /// <summary>Creates the element for one block.</summary>
    /// <param name="block">The block.</param>
    /// <param name="styles">The styles to draw with.</param>
    /// <param name="openLink">Called when a link is clicked.</param>
    /// <param name="first">Whether the element will be the first of its panel (no extra gap above).</param>
    /// <returns>The element.</returns>
    private static FrameworkElement Create(MarkdownBlock block, MarkdownStyles styles, Action<Uri> openLink, bool first)
    {
        switch (block)
        {
            case MarkdownHeading heading:
            {
                var text = CreateText(heading.Inlines, styles, openLink);
                text.FontWeight = FontWeights.SemiBold;
                text.FontSize = heading.Level switch { 1 => 18, 2 => 16, _ => 14 };
                text.Margin = new Thickness(0, first ? 0 : HeadingGapDip, 0, 0);

                // Screen readers move between headings; levels deeper than the platform's nine do not occur (at most 6).
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(
                    text,
                    (Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel)Math.Clamp(heading.Level + 2, 3, 9));
                return text;
            }

            case MarkdownListItem item:
            {
                // Two columns: the marker, then the text, so that a wrapped line starts under the text, not the marker.
                var grid = new Grid { Margin = new Thickness(item.Depth * IndentDip, 0, 0, 0), ColumnSpacing = MarkerGapDip };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = MarkerMinWidthDip });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var marker = new TextBlock { Text = item.Marker, Style = styles.Text, HorizontalAlignment = HorizontalAlignment.Right };
                if (item.Marker is MarkdownParser.OpenTask or MarkdownParser.DoneTask)
                {
                    // Both boxes from the symbol font: the default font takes the ticked box from the emoji font (large,
                    // in color) and the empty one from the text font (small, plain), so a task list looked uneven.
                    marker.FontFamily = TaskMarkerFont;
                }

                // The marker is decoration for the eye; a screen reader gets the item's text.
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(marker, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                var text = CreateText(item.Inlines, styles, openLink);
                Grid.SetColumn(text, 1);
                grid.Children.Add(marker);
                grid.Children.Add(text);
                return grid;
            }

            case MarkdownCodeBlock code:
                return new Border
                {
                    Style = styles.CodeBorder,
                    Margin = new Thickness(code.Indent * IndentDip, 0, 0, 0),
                    Child = new TextBlock
                    {
                        Text = code.Code,
                        FontFamily = styles.Monospace,
                        FontSize = CodeFontSize,

                        // Wrapped, not scrolled sideways: a second scroll direction inside the notes' scroller is hard to use.
                        TextWrapping = TextWrapping.Wrap,
                    },
                };

            case MarkdownTable table:
                return CreateTable(table, styles, openLink);

            case MarkdownRule:
                return new Border { Style = styles.Rule };

            case MarkdownParagraph paragraph:
            {
                var text = CreateText(paragraph.Inlines, styles, openLink);
                if (paragraph.IsQuote)
                {
                    return new Border { Style = styles.QuoteBorder, Margin = new Thickness(paragraph.Indent * IndentDip, 0, 0, 0), Child = text };
                }

                text.Margin = new Thickness(paragraph.Indent * IndentDip, 0, 0, 0);
                return text;
            }

            default:
                // A block kind added to the parser later is shown as nothing rather than failing the whole dialog.
                return new Border();
        }
    }

    /// <summary>
    /// Lays a table out as a grid of wrapped cells with a line under each row; the first row is the header.
    /// </summary>
    /// <param name="table">The table.</param>
    /// <param name="styles">The styles to draw with.</param>
    /// <param name="openLink">Called when a link is clicked.</param>
    /// <returns>The grid.</returns>
    private static Grid CreateTable(MarkdownTable table, MarkdownStyles styles, Action<Uri> openLink)
    {
        var grid = new Grid();

        // Rows may differ in length when the author's did; the widest row sets the columns.
        int columns = table.Rows.Count == 0 ? 0 : table.Rows.Max(row => row.Cells.Count);
        for (int column = 0; column < columns; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (int row = 0; row < table.Rows.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cells = table.Rows[row].Cells;
            for (int column = 0; column < cells.Count; column++)
            {
                var text = CreateText(cells[column], styles, openLink);
                if (row == 0)
                {
                    text.FontWeight = FontWeights.SemiBold;
                }

                var cell = new Border { Style = styles.TableCell, Child = text };
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }

        return grid;
    }

    /// <summary>Creates a wrapped text block from runs.</summary>
    /// <param name="inlines">The runs.</param>
    /// <param name="styles">The styles to draw with.</param>
    /// <param name="openLink">Called when a link is clicked.</param>
    /// <returns>The text block.</returns>
    private static TextBlock CreateText(IReadOnlyList<MarkdownInline> inlines, MarkdownStyles styles, Action<Uri> openLink)
    {
        var text = new TextBlock { Style = styles.Text };
        foreach (var inline in inlines)
        {
            if (inline.Link is { } link)
            {
                var hyperlink = new Hyperlink();
                AddRuns(hyperlink.Inlines, inline, styles);

                // No NavigateUri: the caller opens the link (see the class remarks). The tooltip shows where it leads.
                hyperlink.Click += (_, _) => openLink(link);
                ToolTipService.SetToolTip(hyperlink, link.AbsoluteUri);
                text.Inlines.Add(hyperlink);
            }
            else
            {
                AddRuns(text.Inlines, inline, styles);
            }
        }

        return text;
    }

    /// <summary>
    /// Adds one parsed run to an inline collection: its text in its style, with a line break wherever the author asked
    /// for a hard break.
    /// </summary>
    /// <param name="target">The collection of a text block or of a hyperlink.</param>
    /// <param name="inline">The run.</param>
    /// <param name="styles">The styles to draw with (the code font).</param>
    private static void AddRuns(InlineCollection target, MarkdownInline inline, MarkdownStyles styles)
    {
        var lines = inline.Text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                target.Add(new LineBreak());
            }

            if (lines[i].Length == 0)
            {
                continue;
            }

            var run = new Run { Text = lines[i] };
            if (inline.Style.HasFlag(MarkdownStyle.Bold))
            {
                run.FontWeight = FontWeights.SemiBold;
            }

            if (inline.Style.HasFlag(MarkdownStyle.Italic))
            {
                run.FontStyle = global::Windows.UI.Text.FontStyle.Italic;
            }

            if (inline.Style.HasFlag(MarkdownStyle.Code))
            {
                run.FontFamily = styles.Monospace;
            }

            if (inline.Style.HasFlag(MarkdownStyle.Strikethrough))
            {
                run.TextDecorations = global::Windows.UI.Text.TextDecorations.Strikethrough;
            }

            target.Add(run);
        }
    }
}
