using System.Globalization;
using System.Text;
using BetterClipboard.Core.Presentation;

namespace BetterClipboard.Core.TextView;

/// <summary>
/// The fonts and sizes the text viewer draws with. Sizes are in DIPs, like the rest of the app's text; the RTF writer turns
/// them into points.
/// </summary>
/// <param name="ProseFont">
/// The font of a Markdown document's text: its paragraphs, headings, lists, quotes and rules. The code font in
/// <see cref="Default"/>, which says why.
/// </param>
/// <param name="CodeFont">The font of code and plain text, and of a document's code blocks, inline code and tables (a monospace font the PC has).</param>
/// <param name="ProseSize">A Markdown document's body text, in DIPs (headings are larger by their level).</param>
/// <param name="CodeSize">Code, in DIPs.</param>
public sealed record TextViewStyle(string ProseFont, string CodeFont, double ProseSize, double CodeSize)
{
    /// <summary>The smallest and largest text-size factors the viewer allows (50 % to 300 %).</summary>
    public const double MinScale = 0.5, MaxScale = 3.0;

    /// <summary>The size of the text, Markdown documents' body text included, at 100 %, in DIPs.</summary>
    public const double DefaultSize = 13;

    /// <summary>
    /// The style at 100 %: everything in the given code font at <see cref="DefaultSize"/>, Markdown documents included, so
    /// Formatted reads in the same font and size as As copied, and columns of blanks line up in both.
    /// </summary>
    /// <param name="codeFont">The monospace font to use (Cascadia Mono where Windows has it, else Consolas).</param>
    /// <returns>The style.</returns>
    /// <remarks>
    /// The owner's request (2026-11-16): "Can the format of "Formatted" tab be same as of "As copied"? we need mono font so
    /// we will see ascii tables formatted by default just because of the font". A text no language is recognized in is
    /// drawn as Markdown (the detector's fallback), so a table copied from a terminal opens Formatted, and in Segoe UI at
    /// 14 DIPs, the panel's body size and the documents' font until then, its columns did not line up. The parser keeps each
    /// line's layout for the same reason (<see cref="MarkdownParseOptions.KeepLayout"/>). The writer still takes
    /// a separate prose font, so a proportional one is a change of this line.
    /// </remarks>
    public static TextViewStyle Default(string codeFont) => new(codeFont, codeFont, DefaultSize, DefaultSize);

    /// <summary>
    /// The width the text is laid out in, in DIPs (the text box less its padding), or 0 (the default) for no limit. A
    /// table's columns are fitted into it, cells wrapping inside their own column; with no limit every column keeps its
    /// natural width, as a view whose lines do not wrap should (the viewer's Wrap off).
    /// </summary>
    /// <remarks>
    /// The owner's report (2026-11-20), on a transcript's table whose long cells ran into the next column: "this table
    /// seems overflowing to other columns rather than more height per row or more with per column". A table is drawn as a
    /// grid of the code font's characters (<see cref="RtfWriter"/>), which RichEdit cannot fit to the box by itself: a
    /// line wider than the box is wrapped as a whole, so its columns land under other columns. Only the writer, told the
    /// width, can lay out a table that fits. A size in DIPs, not characters: <see cref="Scaled"/> keeps it, so a larger
    /// text size fits fewer characters into the same box. Footgun: a width wider than the real box lets the grid's lines
    /// wrap again; callers subtract every padding and border, and the writer's character width errs on the wide side.
    /// </remarks>
    public double TextWidth { get; init; }

    /// <summary>
    /// The code font's advance per em (one character's width over the font's size), as the app measured it for the font
    /// it draws with: about 0.586 for Cascadia Mono and 0.550 for Consolas. 0 (the default) when not measured; the writer
    /// then assumes 0.6, a little over both.
    /// </summary>
    /// <remarks>
    /// Found in the QA of 2026-11-20: a test desktop without Cascadia Mono drew Consolas, 7.33 DIPs a character, where the
    /// writer had assumed 8.0, so every table column stood 9 % further right than its text needed and the table looked
    /// spread out. A table's tab stops stand at this advance, so the gaps between columns are the two characters meant.
    /// </remarks>
    public double CodeAdvanceEm { get; init; }

    /// <summary>The style with every size multiplied by <paramref name="scale"/>, clamped to <see cref="MinScale"/>–<see cref="MaxScale"/>.</summary>
    /// <param name="scale">The factor (1 = 100 %); NaN counts as 1.</param>
    /// <returns>The scaled style; its <see cref="TextWidth"/> is kept (a width in DIPs, not in characters).</returns>
    public TextViewStyle Scaled(double scale)
    {
        double factor = double.IsFinite(scale) ? Math.Clamp(scale, MinScale, MaxScale) : 1;
        return this with { ProseSize = ProseSize * factor, CodeSize = CodeSize * factor };
    }
}

/// <summary>
/// Writes what the text viewer shows as one RTF document for WinUI's <c>RichEditBox</c>: colored code, or Markdown drawn as
/// a document (headings, lists, quotes and what they hold, code blocks, tables, rules, emphasis and links). RTF gets colored text into
/// RichEdit in one call (one parse in native code), and one document lets a person select across all of it. Its cost still
/// grows with the text, about 7 µs per character in the QA desktop (2026-11-13), so the viewer writes a long code text one
/// page at a time (<see cref="TextViewRendering.Pages"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Characters map one to one.</b> In a code document every character of the text becomes exactly one character of
/// RichEdit's text (a line break becomes its paragraph mark, a tab a tab, every other character itself, a control
/// character its visible picture). So a position in the text is the same position in RichEdit, which the line-number gutter
/// relies on.
/// </para>
/// <para>
/// <b>Links are not live.</b> A Markdown link is drawn underlined in the link color, but nothing in the document can be
/// clicked to open anything: the text comes from the clipboard, and a click in the viewer must never leave for a web page.
/// </para>
/// </remarks>
public static class RtfWriter
{
    /// <summary>Twips (RTF's unit) per DIP: 1440 twips an inch, 96 DIPs an inch.</summary>
    private const double TwipsPerDip = 15;

    /// <summary>The gap after a paragraph, in DIPs.</summary>
    private const double ParagraphGapDip = 8;

    /// <summary>How far one list or quote level indents, in DIPs.</summary>
    private const double IndentDip = 18;

    /// <summary>The width of a list item's marker column (the hanging indent), in DIPs; wider for a long numbered marker (<see cref="MarkerWidth"/>).</summary>
    private const double MarkerDip = 16;

    /// <summary>
    /// The advance of one character of a document's fonts, in ems: a little over Cascadia Mono's (0.586) and Consolas'
    /// (0.55), the monospace fonts documents are drawn in (<see cref="TextViewStyle.Default"/>); a proportional font's
    /// digits are narrower. So the estimate errs on the side of room: a numbered marker's column and a table fitted with it
    /// are never too narrow for their text.
    /// </summary>
    private const double CharWidthEm = 0.6;

    /// <summary>The blank between two table columns, in characters of the code font.</summary>
    internal const int ColumnGapChars = 2;

    /// <summary>
    /// The narrowest a table column is cut to when its table does not fit the text width, in characters (a column whose
    /// own cells are narrower keeps their width). A table whose columns do not fit even at this width is wider than its box.
    /// </summary>
    internal const int MinColumnChars = 6;

    /// <summary>
    /// The most tab stops a paragraph gets: RichEdit keeps 32 (<c>MAX_TAB_STOPS</c>) and drops the rest. A table with more
    /// columns than stops plus one is laid out with blanks instead (<see cref="WriteTable"/>).
    /// </summary>
    private const int MaxTabStops = 32;

    /// <summary>The gap after each row of a table some of whose cells wrap, in DIPs: it tells one row's lines from the next row's.</summary>
    private const double RowGapDip = 4;

    /// <summary>
    /// The share of the text width a fitted table leaves free: room for the measured advance being a hair short and for
    /// rounding, so the last column never reaches the box's edge, where RichEdit would wrap the line.
    /// </summary>
    private const double FitMargin = 0.01;

    /// <summary>
    /// Writes a code document: the text in the code font, each token in its kind's color, one RichEdit paragraph per line.
    /// </summary>
    /// <param name="text">The text, with <c>\n</c> line breaks.</param>
    /// <param name="tokens">Its tokens, in order (<see cref="SyntaxHighlighter.Tokenize"/>); may be empty for plain text.</param>
    /// <param name="palette">The theme's colors.</param>
    /// <param name="style">Fonts and sizes.</param>
    /// <param name="cancellationToken">Stops a long run.</param>
    /// <returns>The RTF document.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static string Code(string text, IReadOnlyList<SyntaxToken> tokens, SyntaxPalette palette, TextViewStyle style, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(style);

        var rtf = new RtfBuilder(palette, style, text.Length + (tokens.Count * 6) + 512);
        rtf.Raw($@"\pard\f1\fs{HalfPoints(style.CodeSize)}\cf{rtf.ColorIndex(palette.Text)} ");
        rtf.Colored(text, 0, text.Length, tokens, palette.Text, cancellationToken);
        return rtf.Finish();
    }

    /// <summary>
    /// Writes a Markdown document: each block drawn as Markdown draws it, code blocks colored as the language
    /// <paramref name="codeTokens"/> says, and a block quote's blocks one level further right in the quote's look.
    /// </summary>
    /// <param name="blocks">The parsed blocks (<see cref="MarkdownParser.Parse(string?, MarkdownParseOptions)"/>).</param>
    /// <param name="codeTokens">The tokens of a code block's code (empty for none), by block, also for code inside a quote.</param>
    /// <param name="palette">The theme's colors.</param>
    /// <param name="style">Fonts and sizes.</param>
    /// <param name="cancellationToken">Stops a long run.</param>
    /// <returns>The RTF document.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>
    /// <b>The quote's look</b> (2026-11-20, when quotes began to hold blocks of their own). RichEdit draws no bar beside a
    /// paragraph, so a quote is told by its indentation and its color: every block inside it is moved right one level per
    /// quote (<see cref="IndentDip"/>) and drawn in the secondary color, its paragraphs and list items in italics as a
    /// quote's text always was, while headings, tables and code keep their own look. Code keeps its syntax colors.
    /// </remarks>
    public static string Markdown(IReadOnlyList<MarkdownBlock> blocks, Func<MarkdownCodeBlock, IReadOnlyList<SyntaxToken>> codeTokens, SyntaxPalette palette, TextViewStyle style, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(codeTokens);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(style);

        var rtf = new RtfBuilder(palette, style, 4096);
        bool first = true;
        WriteBlocks(rtf, blocks, offset: 0, quoted: false, codeTokens, palette, style, ref first, cancellationToken);
        return rtf.Finish();
    }

    /// <summary>
    /// Writes blocks of a Markdown document in order, a block quote's by recursion (its blocks one level further right).
    /// </summary>
    /// <param name="rtf">The document being written.</param>
    /// <param name="blocks">The blocks: the document's, or a quote's own.</param>
    /// <param name="offset">
    /// How many levels the blocks are moved right of their own indentation: one per quote they are inside, plus the list
    /// levels of a quote that sits inside a list item.
    /// </param>
    /// <param name="quoted">Whether the blocks are inside a quote, so drawn in the quote's look (see <see cref="Markdown"/>).</param>
    /// <param name="codeTokens">The tokens of a code block's code, by block.</param>
    /// <param name="palette">The theme's colors.</param>
    /// <param name="style">Fonts and sizes.</param>
    /// <param name="first">
    /// Whether nothing was written yet in the whole document; cleared by the first block. Each block after the first starts
    /// with the paragraph break that ends the one before, so the document never ends with an empty paragraph, wherever its
    /// last block is nested.
    /// </param>
    /// <param name="cancellationToken">Stops a long run.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    private static void WriteBlocks(RtfBuilder rtf, IReadOnlyList<MarkdownBlock> blocks, int offset, bool quoted, Func<MarkdownCodeBlock, IReadOnlyList<SyntaxToken>> codeTokens, SyntaxPalette palette, TextViewStyle style, ref bool first, CancellationToken cancellationToken)
    {
        int prose = HalfPoints(style.ProseSize);
        int textColor = rtf.ColorIndex(quoted ? palette.Secondary : palette.Prose);
        int gap = Twips(ParagraphGapDip);

        // A quote's paragraphs and list items are italic; the closing control word ends the italics with their block, since
        // a new paragraph (\pard) resets the paragraph's layout but not its characters' style.
        string italic = quoted ? @"\i" : string.Empty;
        string upright = quoted ? @"\i0" : string.Empty;
        foreach (var block in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (block is MarkdownQuote quote)
            {
                // Its blocks one level right of the quote's own place (a quote inside a list item sits under the item's text).
                WriteBlocks(rtf, quote.Blocks, offset + quote.Indent + 1, quoted: true, codeTokens, palette, style, ref first, cancellationToken);
                continue;
            }

            if (block is not (MarkdownHeading or MarkdownListItem or MarkdownCodeBlock or MarkdownTable or MarkdownRule or MarkdownParagraph))
            {
                // A block kind added to the parser later is left out rather than written half.
                continue;
            }

            bool firstOfDocument = first;
            if (!first)
            {
                rtf.Raw("\\par\n");
            }

            first = false;
            switch (block)
            {
                case MarkdownHeading heading:
                {
                    // Larger and bold, with room above it: a heading belongs to what follows it.
                    double factor = heading.Level switch { 1 => 1.5, 2 => 1.3, 3 => 1.15, _ => 1.0 };
                    rtf.Raw($@"\pard{LeftIndent(offset)}\sb{(firstOfDocument ? 0 : Twips(6))}\sa{Twips(4)}\f0\fs{HalfPoints(style.ProseSize * factor)}\cf{textColor}\b ");
                    rtf.Inlines(heading.Inlines);
                    rtf.Raw(@"\b0");
                    break;
                }

                case MarkdownListItem item:
                {
                    // A hanging indent: the marker in its own column, wrapped lines under the item's text.
                    double marker = MarkerWidth(item.Marker, style);
                    int left = Twips(((item.Depth + offset) * IndentDip) + marker);
                    rtf.Raw($@"\pard\fi-{Twips(marker)}\li{left}\tx{left}\sa{Twips(3)}\f0\fs{prose}\cf{textColor}{italic} ");
                    if (item.Marker is MarkdownParser.OpenTask or MarkdownParser.DoneTask)
                    {
                        rtf.Raw(@"{\f2 ");
                        rtf.Text(item.Marker);
                        rtf.Raw("}");
                    }
                    else
                    {
                        rtf.Text(item.Marker);
                    }

                    rtf.Raw(@"\tab ");
                    rtf.Inlines(item.Inlines);
                    rtf.Raw(upright);
                    break;
                }

                case MarkdownCodeBlock code:
                    WriteCodeBlock(rtf, code, codeTokens(code), palette, style, gap, offset, cancellationToken);
                    break;

                case MarkdownTable table:
                    WriteTable(rtf, table, palette, style, gap, offset, quoted);
                    break;

                case MarkdownRule:
                    rtf.Raw($@"\pard{LeftIndent(offset)}\sa{gap}\f0\fs{prose}\cf{rtf.ColorIndex(palette.Secondary)} ");
                    rtf.Text(new string('─', 32));
                    break;

                case MarkdownParagraph paragraph:
                    rtf.Raw($@"\pard\li{Twips((paragraph.Indent + offset) * IndentDip)}\sa{gap}\f0\fs{prose}\cf{textColor}{italic} ");
                    rtf.Inlines(paragraph.Inlines);
                    rtf.Raw(upright);
                    break;
            }
        }
    }

    /// <summary>
    /// The width of a list item's marker column, in DIPs: <see cref="MarkerDip"/> for a bullet or a task box, and room for
    /// a numbered marker and one character after it (<c>1.</c>, <c>10.</c>), so the item's text never touches its number.
    /// </summary>
    /// <param name="marker">The item's marker (<see cref="MarkdownListItem.Marker"/>).</param>
    /// <param name="style">Fonts and sizes: the column grows with the text size.</param>
    /// <returns>The column's width.</returns>
    /// <remarks>
    /// Found in the QA of 2026-11-20: in the monospace font a document is drawn in since 2026-11-16, "1." takes 15.6 of the
    /// column's 16 DIPs, and the tab after it left no visible gap ("1.Hold the rest.").
    /// </remarks>
    private static double MarkerWidth(string marker, TextViewStyle style) =>
        marker is MarkdownParser.Bullet or MarkdownParser.OpenTask or MarkdownParser.DoneTask
            ? MarkerDip
            : Math.Max(MarkerDip, (marker.Length + 1) * CharWidthEm * style.ProseSize);

    /// <summary>
    /// The left indentation of a block that has none of its own (a heading, a table outside a list, a rule) at
    /// <paramref name="levels"/> levels right: nothing at the margin, so a document without quotes is written as before.
    /// </summary>
    /// <param name="levels">How many levels right of the margin.</param>
    /// <returns>The <c>\li</c> control word, or an empty string for 0.</returns>
    private static string LeftIndent(int levels) => levels > 0 ? $@"\li{Twips(levels * IndentDip)}" : string.Empty;

    /// <summary>Writes a fenced code block: its lines in the code font on the code background, colored by its tokens.</summary>
    /// <param name="rtf">The document being written.</param>
    /// <param name="code">The block.</param>
    /// <param name="tokens">Its code's tokens.</param>
    /// <param name="palette">The theme's colors.</param>
    /// <param name="style">Fonts and sizes.</param>
    /// <param name="gap">The gap after the block, in twips.</param>
    /// <param name="offset">How many levels it is moved right of its own indentation (the quotes it is inside).</param>
    /// <param name="cancellationToken">Stops a long run.</param>
    private static void WriteCodeBlock(RtfBuilder rtf, MarkdownCodeBlock code, IReadOnlyList<SyntaxToken> tokens, SyntaxPalette palette, TextViewStyle style, int gap, int offset, CancellationToken cancellationToken)
    {
        int background = rtf.ColorIndex(palette.CodeBackground);
        rtf.Raw($@"\pard\li{Twips(((code.Indent + offset) * IndentDip) + 6)}\sa0\f1\fs{HalfPoints(style.CodeSize)}\cf{rtf.ColorIndex(palette.Text)} ");
        int lineStart = 0;
        int tokenIndex = 0;
        while (true)
        {
            int lineEnd = code.Code.IndexOf('\n', lineStart);
            bool lastLine = lineEnd < 0;
            if (lastLine)
            {
                lineEnd = code.Code.Length;
                rtf.Raw($@"\sa{gap}");
            }

            // The tokens of this line: the colored writer takes them relative to the code, and skips those past the line.
            while (tokenIndex < tokens.Count && tokens[tokenIndex].End <= lineStart)
            {
                tokenIndex++;
            }

            if (lineEnd > lineStart)
            {
                rtf.Raw($@"{{\highlight{background} ");
                rtf.Colored(code.Code, lineStart, lineEnd, tokens, palette.Text, cancellationToken, tokenIndex);
                rtf.Raw("}");
            }

            if (lastLine)
            {
                return;
            }

            rtf.Raw("\\par\n");
            lineStart = lineEnd + 1;
        }
    }

    /// <summary>
    /// Writes a table as an aligned grid of the code font: the header bold over a rule; every column as wide as its widest
    /// cell while the table fits the text width, otherwise the widest columns narrowed and their cells wrapped onto more
    /// lines inside their own column, so a row is as tall as its tallest cell.
    /// </summary>
    /// <param name="rtf">The document being written.</param>
    /// <param name="table">The table.</param>
    /// <param name="palette">The theme's colors.</param>
    /// <param name="style">Fonts, sizes, and the width the table is fitted into (<see cref="TextViewStyle.TextWidth"/>).</param>
    /// <param name="gap">The gap after the table, in twips.</param>
    /// <param name="offset">How many levels it is moved right of its own indentation (the quotes it is inside).</param>
    /// <param name="quoted">Whether it is inside a quote: its cells are then in the quote's secondary color.</param>
    /// <remarks>
    /// <para>
    /// <b>Fitted, never overflowing</b> (the owner's report, 2026-11-20: "this table seems overflowing to other columns
    /// rather than more height per row or more with per column"). Until then a column was at most 48 characters wide, and a
    /// longer cell ran on into the next column: the rest of its row moved right, out of line with the rows above and below.
    /// Now every column keeps its natural width while the table fits. A table that does not is laid out two ways, its widest
    /// columns cut down to one common width (<see cref="FitColumns"/>) or its headers wrapped first
    /// (<see cref="FitColumnsToData"/>), and drawn in whichever takes fewer lines; <see cref="WrapCell"/> breaks the cells
    /// into lines, between words where it can.
    /// </para>
    /// <para>
    /// <b>Columns stand at tab stops.</b> A line's cells are separated by tabs, and every column after the first starts at a
    /// stop of its own: a column starts where it should whatever the glyphs before it are as wide as. An emoji or a Chinese
    /// character comes from a fallback font, wider than the code font's character (it is counted as two, <see cref="CellWidth"/>,
    /// and the two-character gap takes what that leaves). A copy of the drawn table therefore holds tabs between its cells.
    /// RichEdit keeps at most 32 stops a paragraph, so a table of more columns is laid out with blanks, as before.
    /// </para>
    /// <para>
    /// RichEdit's own tables were not used: they draw borders and cell padding, out of the document's monospace look, in
    /// which a table copied from a terminal is drawn too (<see cref="TextViewStyle.Default"/>).
    /// </para>
    /// </remarks>
    private static void WriteTable(RtfBuilder rtf, MarkdownTable table, SyntaxPalette palette, TextViewStyle style, int gap, int offset, bool quoted)
    {
        // A cell's text without its Markdown, its own line breaks (a <br>) kept; a tab made a blank, or it would jump to the
        // next column's stop.
        var rows = table.Rows.Select(row => row.Cells.Select(cell => MarkdownParser.ToPlainText(cell).Replace('\t', ' ')).ToArray()).ToArray();
        int columns = rows.Length == 0 ? 0 : rows.Max(row => row.Length);
        if (columns == 0)
        {
            return;
        }

        // Each column's natural width is its widest line; a column of empty cells keeps one character, so the rule shows it.
        var natural = new int[columns];
        foreach (var row in rows)
        {
            for (int c = 0; c < row.Length; c++)
            {
                natural[c] = Math.Max(natural[c], WidestLine(row[c]));
            }
        }

        for (int c = 0; c < columns; c++)
        {
            natural[c] = Math.Max(1, natural[c]);
        }

        // The characters a line may take right of the table's indentation: the text width over one character's advance.
        // No width means no limit (lines do not wrap), and every column keeps its natural width.
        int levels = table.Indent + offset;
        double charDip = CodeCharWidth(style);
        int budget = style.TextWidth > 0
            ? (int)Math.Clamp(Math.Floor((style.TextWidth - (levels * IndentDip)) / (charDip * (1 + FitMargin))), 1, int.MaxValue)
            : int.MaxValue;
        var widths = FitColumns(natural, budget);
        var cells = WrapRows(rows, widths);
        if (!widths.SequenceEqual(natural))
        {
            // A cut table is laid out both ways, and drawn in whichever takes fewer lines (FitColumnsToData's remarks).
            var dataFirst = FitColumnsToData(natural, DataWidths(rows, natural), budget);
            if (!dataFirst.SequenceEqual(widths))
            {
                var dataFirstCells = WrapRows(rows, dataFirst);
                if (Height(dataFirstCells) < Height(cells))
                {
                    widths = dataFirst;
                    cells = dataFirstCells;
                }
            }
        }

        // When cells wrap, a small gap after each row tells its lines from the next row's (the header has its rule).
        bool spaced = cells.Skip(1).Any(row => row.Any(lines => lines.Count > 1));
        bool tabs = columns - 1 <= MaxTabStops;

        // Every line is a paragraph of its own, so each one carries the table's indentation (under a list item's text, one
        // level right per quote it is inside) and the column stops.
        string paragraph = $@"\pard{LeftIndent(levels)}{(tabs ? TabStops(widths, levels, charDip) : string.Empty)}";
        int textColor = rtf.ColorIndex(quoted ? palette.Secondary : palette.Prose);
        int ruleColor = rtf.ColorIndex(palette.Secondary);
        int size = HalfPoints(style.CodeSize);
        for (int r = 0; r < cells.Length; r++)
        {
            bool header = r == 0;
            bool lastRow = r == cells.Length - 1;
            int height = cells[r].Max(lines => lines.Count);
            for (int line = 0; line < height; line++)
            {
                bool lastLine = line == height - 1;
                int after = header || !lastLine ? 0 : lastRow ? gap : spaced ? Twips(RowGapDip) : 0;
                rtf.Raw($@"{paragraph}\sa{after}\f1\fs{size}\cf{textColor}{(header ? @"\b" : string.Empty)} ");
                rtf.Text(TableLine(cells[r], line, widths, tabs));
                if (!lastLine)
                {
                    rtf.Raw("\\par\n");
                }
            }

            if (header)
            {
                // The rule under the header ends a table of nothing else, so it carries the table's gap then.
                rtf.Raw($@"\b0\par" + "\n" + $@"{paragraph}\sa{(lastRow ? gap : 0)}\cf{ruleColor} ");
                rtf.Text(string.Join(tabs ? "\t" : new string(' ', ColumnGapChars), widths.Select(width => new string('─', width))));
            }

            if (!lastRow)
            {
                rtf.Raw("\\par\n");
            }
        }
    }

    /// <summary>
    /// The width of each table column, in characters: every column's natural width while the table fits
    /// <paramref name="budget"/>, otherwise the widest columns cut down to one common width, the largest that fits.
    /// </summary>
    /// <param name="natural">Each column's natural width (its widest line), at least 1.</param>
    /// <param name="budget">The characters a line may take, the gaps between columns included; <see cref="int.MaxValue"/> for no limit.</param>
    /// <returns>
    /// The widths, one per column, none wider than its natural width; with the gaps they take at most
    /// <paramref name="budget"/>, unless even columns cut to <see cref="MinColumnChars"/> do not fit, when they are that
    /// narrow and the table is wider than its box.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="natural"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// A "water level" over the columns: those narrower than the level keep their whole width (a status, a number, a short
    /// name never wraps), and the room they leave is shared evenly among the wider ones, which wrap. A column is never cut
    /// below <see cref="MinColumnChars"/> (or its own natural width, if less): a column a few characters wide would wrap
    /// every word. <see cref="FitColumnsToData"/> is the other layout a cut table is tried in.
    /// </remarks>
    internal static int[] FitColumns(IReadOnlyList<int> natural, int budget)
    {
        ArgumentNullException.ThrowIfNull(natural);
        long room = RoomFor(natural.Count, budget);
        return Total(natural) <= room ? [.. natural] : LevelWidths(Floors(natural), natural, room);
    }

    /// <summary>
    /// The width of each table column, in characters, the data first: every column's natural width while the table fits
    /// <paramref name="budget"/>; otherwise every column at least as wide as its data (<paramref name="data"/>: the body's
    /// widest line, and its header's longest word), and what is left raised toward the natural widths by one common level;
    /// or, when even the data does not fit, the data's widths cut down by one level as <see cref="FitColumns"/> cuts.
    /// </summary>
    /// <param name="natural">Each column's natural width (its widest line, the header's included), at least 1.</param>
    /// <param name="data">Each column's data width, at least 1 and at most its natural width.</param>
    /// <param name="budget">The characters a line may take, the gaps between columns included; <see cref="int.MaxValue"/> for no limit.</param>
    /// <returns>The widths, one per column, none wider than its natural width.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The two lists differ in length.</exception>
    /// <remarks>
    /// A header is a label: wrapped onto two or three lines it still reads, while a column of values cut short makes every
    /// row taller. "Computes option profit" over values that read "No" and "Yes" keeps 8 characters ("Computes") in this
    /// layout, not 20, and the room goes to the column of long sentences next to it. Neither layout is always the
    /// shorter one, so <see cref="WriteTable"/> draws a cut table in whichever takes fewer lines.
    /// </remarks>
    internal static int[] FitColumnsToData(IReadOnlyList<int> natural, IReadOnlyList<int> data, int budget)
    {
        ArgumentNullException.ThrowIfNull(natural);
        ArgumentNullException.ThrowIfNull(data);
        if (data.Count != natural.Count)
        {
            throw new ArgumentException("A data width is needed for every column.", nameof(data));
        }

        long room = RoomFor(natural.Count, budget);
        if (Total(natural) <= room)
        {
            return [.. natural];
        }

        return Total(data) <= room ? LevelWidths(data, natural, room) : LevelWidths(Floors(data), data, room);
    }

    /// <summary>The characters left for the cells of a line of <paramref name="columns"/> columns: the budget less the gaps between them.</summary>
    /// <param name="columns">The number of columns.</param>
    /// <param name="budget">The characters a line may take.</param>
    /// <returns>The room, which may be negative for a budget smaller than the gaps.</returns>
    private static long RoomFor(int columns, int budget) => budget - ((long)ColumnGapChars * Math.Max(0, columns - 1));

    /// <summary>The sum of widths, without overflow.</summary>
    /// <param name="widths">The widths.</param>
    /// <returns>Their sum.</returns>
    private static long Total(IReadOnlyList<int> widths)
    {
        long sum = 0;
        foreach (int width in widths)
        {
            sum += width;
        }

        return sum;
    }

    /// <summary>The narrowest each column may be cut to: <see cref="MinColumnChars"/>, or the column's own width when less.</summary>
    /// <param name="widths">The columns' widths.</param>
    /// <returns>The floors.</returns>
    private static int[] Floors(IReadOnlyList<int> widths) => [.. widths.Select(width => Math.Min(width, MinColumnChars))];

    /// <summary>
    /// Widths between two bounds at one common level: each column <c>clamp(level, lower, upper)</c> for the highest level
    /// whose widths fit <paramref name="room"/>, and what the level leaves over handed out one character at a time to the
    /// columns still below their upper bound, left to right, so the line takes the whole room.
    /// </summary>
    /// <param name="lower">Each column's lower bound.</param>
    /// <param name="upper">Each column's upper bound, at least its lower bound.</param>
    /// <param name="room">The characters the widths may take together.</param>
    /// <returns>The widths: the lower bounds when even they do not fit.</returns>
    private static int[] LevelWidths(IReadOnlyList<int> lower, IReadOnlyList<int> upper, long room)
    {
        int count = lower.Count;
        if (count == 0)
        {
            return [];
        }

        // The sum grows with the level, so a binary search finds the highest level that fits.
        int low = 0;
        int high = upper.Max();
        while (low < high)
        {
            int level = low + ((high - low + 1) / 2);
            if (WidthsAt(level) <= room)
            {
                low = level;
            }
            else
            {
                high = level - 1;
            }
        }

        var widths = new int[count];
        long used = 0;
        for (int c = 0; c < count; c++)
        {
            widths[c] = WidthAt(c, low);
            used += widths[c];
        }

        // Fewer characters are left than there are columns the next level would widen (that level did not fit), and each
        // of those is below its upper bound: one pass hands them out.
        for (int c = 0; c < count && used < room; c++)
        {
            if (widths[c] < upper[c])
            {
                widths[c]++;
                used++;
            }
        }

        return widths;

        int WidthAt(int column, int level) => Math.Max(lower[column], Math.Min(upper[column], level));

        long WidthsAt(int level)
        {
            long sum = 0;
            for (int c = 0; c < count; c++)
            {
                sum += WidthAt(c, level);
            }

            return sum;
        }
    }

    /// <summary>
    /// A table cell's text as the lines it takes in a column <paramref name="width"/> characters wide: its own line breaks
    /// kept (a <c>&lt;br&gt;</c>), each line broken at the last blank that fits, and a word wider than the column cut where
    /// the column ends. A continuation line starts at the next word, without the blanks before it.
    /// </summary>
    /// <param name="text">The cell's plain text.</param>
    /// <param name="width">The column's width, in characters as <see cref="CellWidth"/> counts them; less than 1 counts as 1.</param>
    /// <returns>
    /// The lines, at least one (an empty cell is one empty line), without trailing blanks; none wider than the column,
    /// except a single character wider than the column itself.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    internal static IReadOnlyList<string> WrapCell(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);
        width = Math.Max(1, width);
        var lines = new List<string>();
        foreach (var part in text.Split('\n'))
        {
            string line = part.TrimEnd();
            if (CellWidth(line) <= width)
            {
                lines.Add(line);
                continue;
            }

            int start = 0;
            while (start < line.Length)
            {
                // The most characters, whole graphemes, that fit from the start, and the last blank among them.
                int end = start;
                int used = 0;
                int lastBlank = -1;
                while (end < line.Length)
                {
                    int length = StringInfo.GetNextTextElementLength(line, end);
                    int advance = ElementWidth(line, end, length);
                    if (used + advance > width)
                    {
                        break;
                    }

                    if (line[end] == ' ')
                    {
                        lastBlank = end;
                    }

                    used += advance;
                    end += length;
                }

                if (end >= line.Length)
                {
                    lines.Add(line[start..]);
                    break;
                }

                // End before the blank that did not fit, else at the last blank that did, else cut the word at the column's
                // end (one grapheme at least, so a character wider than the column still moves on).
                int stop = line[end] == ' ' ? end
                    : lastBlank > start ? lastBlank
                    : Math.Max(end, start + StringInfo.GetNextTextElementLength(line, start));
                lines.Add(line[start..stop].TrimEnd());
                start = stop;
                while (start < line.Length && line[start] == ' ')
                {
                    start++;
                }
            }
        }

        return lines;
    }

    /// <summary>
    /// How many characters of the code font a text takes, by grapheme: two for a wide one (a Chinese, Japanese or Korean
    /// character, a fullwidth form, an emoji), none for a format character or a lone combining mark, one for any other.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>Its width in characters.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// An estimate that errs wide: such glyphs come from fallback fonts, between about 1.7 (a Chinese character) and 2.1 (a
    /// color emoji) of the code font's characters. Wide is the safe side: a column counted too wide has a little more room
    /// than it needs, while one counted too narrow lets its text run into the next column (tab stops still start that
    /// column in place, unless the text passes the stop).
    /// </remarks>
    internal static int CellWidth(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int width = 0;
        for (int i = 0; i < text.Length;)
        {
            int length = StringInfo.GetNextTextElementLength(text, i);
            width += ElementWidth(text, i, length);
            i += length;
        }

        return width;
    }

    /// <summary>The width of one grapheme, in characters of the code font (see <see cref="CellWidth"/>).</summary>
    /// <param name="text">The text holding it.</param>
    /// <param name="start">Where it starts.</param>
    /// <param name="length">Its length in UTF-16 units.</param>
    /// <returns>0, 1 or 2.</returns>
    private static int ElementWidth(string text, int start, int length)
    {
        // A lone surrogate is drawn as a replacement box: one character.
        if (Rune.DecodeFromUtf16(text.AsSpan(start, length), out var rune, out _) != System.Buffers.OperationStatus.Done)
        {
            return 1;
        }

        if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
        {
            return 0;
        }

        // A character followed by the emoji variation selector (⚠️, ❤️) is drawn as a color emoji.
        return IsWide(rune.Value) || text.AsSpan(start, length).Contains('\uFE0F') ? 2 : 1;
    }

    /// <summary>
    /// Whether a code point is drawn about two characters of a monospace font wide: East Asian wide and fullwidth
    /// characters, and the blocks emoji come from. Generous on purpose (some symbols in these blocks are narrower).
    /// </summary>
    /// <param name="codePoint">The code point.</param>
    /// <returns>Whether it counts as two characters.</returns>
    private static bool IsWide(int codePoint) => codePoint is
        (>= 0x1100 and <= 0x115F) // Hangul initial consonants
        or (>= 0x231A and <= 0x231B) or (>= 0x23E9 and <= 0x23F3) // ⌚⌛, ⏩–⏳
        or (>= 0x2600 and <= 0x27BF) // miscellaneous symbols and dingbats: ✅ ❌ ⚡ ✨
        or (>= 0x2B00 and <= 0x2BFF) // arrows and symbols: ⭐ ⬛ ⭕
        or (>= 0x2E80 and <= 0xA4CF) // CJK radicals, punctuation, kana, Bopomofo, CJK ideographs, Yi
        or (>= 0xA960 and <= 0xA97F) // Hangul Jamo extended
        or (>= 0xAC00 and <= 0xD7A3) // Hangul syllables
        or (>= 0xF900 and <= 0xFAFF) // CJK compatibility ideographs
        or (>= 0xFE10 and <= 0xFE19) or (>= 0xFE30 and <= 0xFE6F) // vertical and small CJK forms
        or (>= 0xFF00 and <= 0xFF60) or (>= 0xFFE0 and <= 0xFFE6) // fullwidth forms
        or (>= 0x1F000 and <= 0x1FAFF) // mahjong, cards, enclosed ideographs, emoji and pictographs
        or (>= 0x20000 and <= 0x3FFFD); // CJK ideographs beyond the BMP

    /// <summary>
    /// Each column's data width, for <see cref="FitColumnsToData"/>: the widest line of its body's cells, and at least its
    /// header's longest word, so a header that wraps never has a word cut; at least 1 and at most the natural width.
    /// </summary>
    /// <param name="rows">The table's rows as plain text, the header first.</param>
    /// <param name="natural">Each column's natural width.</param>
    /// <returns>The data widths.</returns>
    private static int[] DataWidths(string[][] rows, IReadOnlyList<int> natural)
    {
        var data = new int[natural.Count];
        for (int c = 0; c < data.Length; c++)
        {
            int width = 0;
            if (c < rows[0].Length)
            {
                foreach (var word in rows[0][c].Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    width = Math.Max(width, CellWidth(word));
                }
            }

            for (int r = 1; r < rows.Length; r++)
            {
                if (c < rows[r].Length)
                {
                    width = Math.Max(width, WidestLine(rows[r][c]));
                }
            }

            data[c] = Math.Clamp(width, 1, natural[c]);
        }

        return data;
    }

    /// <summary>Every row's cells wrapped to their columns' widths (<see cref="WrapCell"/>); a missing cell is an empty one.</summary>
    /// <param name="rows">The table's rows as plain text.</param>
    /// <param name="widths">The columns' widths.</param>
    /// <returns>Per row, per column, the cell's lines.</returns>
    private static IReadOnlyList<string>[][] WrapRows(string[][] rows, IReadOnlyList<int> widths) =>
        [.. rows.Select(row => Enumerable.Range(0, widths.Count).Select(c => WrapCell(c < row.Length ? row[c] : string.Empty, widths[c])).ToArray())];

    /// <summary>How many lines a laid-out table takes: each row as many as its tallest cell (the header's rule not counted).</summary>
    /// <param name="cells">Per row, per column, the cell's lines.</param>
    /// <returns>The lines.</returns>
    private static int Height(IReadOnlyList<string>[][] cells) => cells.Sum(row => row.Max(lines => lines.Count));

    /// <summary>The width of a cell's widest line (a cell's own line breaks start new lines), in characters (<see cref="CellWidth"/>).</summary>
    /// <param name="cell">The cell's plain text.</param>
    /// <returns>The width.</returns>
    private static int WidestLine(string cell)
    {
        int widest = 0;
        foreach (var line in cell.Split('\n'))
        {
            widest = Math.Max(widest, CellWidth(line.TrimEnd()));
        }

        return widest;
    }

    /// <summary>
    /// One line of a table row: each column's piece of that line (the next of its cell's wrapped lines, or nothing), in
    /// order; nothing after the last piece with text, so a copied line has no trailing tabs or blanks.
    /// </summary>
    /// <param name="cells">The row's cells, each as its wrapped lines.</param>
    /// <param name="line">Which line of the row.</param>
    /// <param name="widths">The columns' widths, in characters.</param>
    /// <param name="tabs">
    /// Whether the columns stand at tab stops (cells separated by a tab); otherwise each piece is padded with blanks to its
    /// column's width and the gap.
    /// </param>
    /// <returns>The line's text.</returns>
    private static string TableLine(IReadOnlyList<string>[] cells, int line, IReadOnlyList<int> widths, bool tabs)
    {
        int last = -1;
        for (int c = 0; c < cells.Length; c++)
        {
            if (line < cells[c].Count && cells[c][line].Length > 0)
            {
                last = c;
            }
        }

        var text = new StringBuilder();
        for (int c = 0; c <= last; c++)
        {
            string piece = line < cells[c].Count ? cells[c][line] : string.Empty;
            if (c > 0 && tabs)
            {
                text.Append('\t');
            }

            text.Append(piece);
            if (!tabs && c < last)
            {
                text.Append(' ', Math.Max(0, widths[c] - CellWidth(piece)) + ColumnGapChars);
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// The tab stops of a table's lines (<c>\tx</c>, in twips from the margin): one where each column after the first
    /// starts, its left neighbour's width and the gap after the one before.
    /// </summary>
    /// <param name="widths">The columns' widths, in characters.</param>
    /// <param name="levels">The table's indentation, in levels (its first column starts there).</param>
    /// <param name="charDip">One character's advance, in DIPs.</param>
    /// <returns>The control words, in order.</returns>
    private static string TabStops(IReadOnlyList<int> widths, int levels, double charDip)
    {
        var stops = new StringBuilder();
        double x = levels * IndentDip;
        for (int c = 0; c < widths.Count - 1; c++)
        {
            x += (widths[c] + ColumnGapChars) * charDip;
            stops.Append(CultureInfo.InvariantCulture, $@"\tx{Twips(x)}");
        }

        return stops.ToString();
    }

    /// <summary>
    /// The advance of one character of the code font at the style's code size, in DIPs: the size as RTF rounds it (whole
    /// half-points) times the font's measured advance (<see cref="TextViewStyle.CodeAdvanceEm"/>), or
    /// <see cref="CharWidthEm"/> when it was not measured.
    /// </summary>
    /// <param name="style">Fonts and sizes.</param>
    /// <returns>The advance.</returns>
    private static double CodeCharWidth(TextViewStyle style) =>
        HalfPoints(style.CodeSize) / 1.5 * (style.CodeAdvanceEm > 0 && double.IsFinite(style.CodeAdvanceEm) ? style.CodeAdvanceEm : CharWidthEm);

    /// <summary>A size in DIPs as RTF half-points (<c>\fs</c>): 1 DIP is 0.75 pt.</summary>
    /// <param name="dips">The size in DIPs.</param>
    /// <returns>Half-points, at least 2.</returns>
    internal static int HalfPoints(double dips) => Math.Max(2, (int)Math.Round(dips * 1.5, MidpointRounding.AwayFromZero));

    /// <summary>A length in DIPs as twips.</summary>
    /// <param name="dips">The length in DIPs.</param>
    /// <returns>Twips.</returns>
    private static int Twips(double dips) => (int)Math.Round(dips * TwipsPerDip, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Builds an RTF document: the header with its font and color tables, the body, and the escaping of text into it.
    /// </summary>
    private sealed class RtfBuilder
    {
        /// <summary>The colors of the color table, in table order (index 1 is the first).</summary>
        private readonly List<uint> colors = [];

        /// <summary>The body written so far.</summary>
        private readonly StringBuilder body;

        /// <summary>The fonts.</summary>
        private readonly TextViewStyle style;

        /// <summary>The theme's colors: token kinds, the code background and links.</summary>
        private readonly SyntaxPalette palette;

        /// <summary>Starts an empty document.</summary>
        /// <param name="palette">The theme's colors; all of them go into the color table, so every index is known up front.</param>
        /// <param name="style">Fonts and sizes.</param>
        /// <param name="capacity">A guess at the body's length.</param>
        public RtfBuilder(SyntaxPalette palette, TextViewStyle style, int capacity)
        {
            this.style = style;
            this.palette = palette;
            body = new StringBuilder(Math.Max(256, capacity));
            foreach (var color in new[]
                     {
                         palette.Text, palette.Comment, palette.Keyword, palette.Type, palette.Function, palette.String, palette.Number,
                         palette.Punctuation, palette.Preprocessor, palette.Variable, palette.Tag, palette.Entity, palette.Inserted,
                         palette.Deleted, palette.Prose, palette.Secondary, palette.Link, palette.CodeBackground,
                     })
            {
                if (!colors.Contains(color))
                {
                    colors.Add(color);
                }
            }
        }

        /// <summary>The color table index of a color (1-based; 0 is RTF's automatic color).</summary>
        /// <param name="color">A color of the palette.</param>
        /// <returns>Its index.</returns>
        public int ColorIndex(uint color)
        {
            int index = colors.IndexOf(color);
            if (index < 0)
            {
                colors.Add(color);
                index = colors.Count - 1;
            }

            return index + 1;
        }

        /// <summary>Appends RTF control words as they are.</summary>
        /// <param name="rtf">The control words.</param>
        public void Raw(string rtf) => body.Append(rtf);

        /// <summary>Appends text, escaped (see <see cref="AppendEscaped"/>).</summary>
        /// <param name="text">The text.</param>
        public void Text(string text) => AppendEscaped(text, 0, text.Length);

        /// <summary>
        /// Appends a stretch of text colored by its tokens: each token in its kind's color, the text between them in
        /// <paramref name="defaultColor"/>. Token positions are relative to <paramref name="text"/>.
        /// </summary>
        /// <param name="text">The text the tokens belong to.</param>
        /// <param name="start">Where the stretch starts.</param>
        /// <param name="end">Where it ends.</param>
        /// <param name="tokens">The text's tokens.</param>
        /// <param name="defaultColor">The color between tokens.</param>
        /// <param name="cancellationToken">Stops a long run.</param>
        /// <param name="firstToken">Where to start looking in <paramref name="tokens"/> (a caller walking line by line keeps its place).</param>
        /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
        public void Colored(string text, int start, int end, IReadOnlyList<SyntaxToken> tokens, uint defaultColor, CancellationToken cancellationToken, int firstToken = 0)
        {
            int current = ColorIndex(defaultColor);
            int position = start;
            for (int t = firstToken; t < tokens.Count && position < end; t++)
            {
                var token = tokens[t];
                if (token.End <= position)
                {
                    continue;
                }

                if (token.Start >= end)
                {
                    break;
                }

                if (token.Start > position)
                {
                    SetColor(ref current, ColorIndex(defaultColor));
                    AppendEscaped(text, position, token.Start);
                    position = token.Start;
                }

                int tokenEnd = Math.Min(token.End, end);
                SetColor(ref current, ColorIndex(palette.ColorOf(token.Kind)));
                AppendEscaped(text, position, tokenEnd);
                position = tokenEnd;
                if ((t & 0x3FFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            if (position < end)
            {
                SetColor(ref current, ColorIndex(defaultColor));
                AppendEscaped(text, position, end);
            }

            // Leave the default color current, so whatever follows starts from it.
            SetColor(ref current, ColorIndex(defaultColor));
        }

        /// <summary>Switches the current color when it differs (<c>\cfN</c>).</summary>
        /// <param name="current">The color in effect; updated.</param>
        /// <param name="wanted">The color wanted next.</param>
        private void SetColor(ref int current, int wanted)
        {
            if (current != wanted)
            {
                body.Append(@"\cf").Append(wanted.ToString(CultureInfo.InvariantCulture)).Append(' ');
                current = wanted;
            }
        }

        /// <summary>
        /// Appends Markdown runs: each in a group of its own, so bold, italic, code, strike and links end with their run;
        /// a hard line break inside a run becomes a line break (<c>\line</c>).
        /// </summary>
        /// <param name="inlines">The runs.</param>
        public void Inlines(IReadOnlyList<MarkdownInline> inlines)
        {
            foreach (var inline in inlines)
            {
                var controls = new StringBuilder();
                if (inline.Style.HasFlag(MarkdownStyle.Bold))
                {
                    controls.Append(@"\b");
                }

                if (inline.Style.HasFlag(MarkdownStyle.Italic))
                {
                    controls.Append(@"\i");
                }

                if (inline.Style.HasFlag(MarkdownStyle.Strikethrough))
                {
                    controls.Append(@"\strike");
                }

                if (inline.Style.HasFlag(MarkdownStyle.Code))
                {
                    controls.Append(@"\f1\fs").Append(HalfPoints(style.CodeSize).ToString(CultureInfo.InvariantCulture))
                        .Append(@"\highlight").Append(ColorIndex(palette.CodeBackground).ToString(CultureInfo.InvariantCulture));
                }

                if (inline.Link is not null)
                {
                    controls.Append(@"\ul\cf").Append(ColorIndex(palette.Link).ToString(CultureInfo.InvariantCulture));
                }

                if (controls.Length == 0)
                {
                    AppendEscaped(inline.Text, 0, inline.Text.Length, lineBreak: @"\line ");
                    continue;
                }

                body.Append('{').Append(controls).Append(' ');
                AppendEscaped(inline.Text, 0, inline.Text.Length, lineBreak: @"\line ");
                body.Append('}');
            }
        }

        /// <summary>
        /// Appends text escaped for RTF, one RichEdit character per text character: <c>\ { }</c> escaped, a tab as
        /// <c>\tab</c>, a line break as <paramref name="lineBreak"/>, a control character as its visible picture
        /// (U+2400 block), anything beyond ASCII as <c>\uN?</c>.
        /// </summary>
        /// <param name="text">The text.</param>
        /// <param name="start">Where to start.</param>
        /// <param name="end">Where to stop.</param>
        /// <param name="lineBreak">What a line break becomes: a new paragraph in code, a line break inside a Markdown paragraph.</param>
        public void AppendEscaped(string text, int start, int end, string lineBreak = "\\par\n")
        {
            int run = start;
            for (int i = start; i < end; i++)
            {
                char c = text[i];
                if (c is >= ' ' and <= '~' and not ('\\' or '{' or '}'))
                {
                    continue;
                }

                // Copy the plain run before this character in one go: most text is plain ASCII.
                if (i > run)
                {
                    body.Append(text, run, i - run);
                }

                run = i + 1;
                switch (c)
                {
                    case '\\' or '{' or '}':
                        body.Append('\\').Append(c);
                        break;
                    case '\t':
                        body.Append(@"\tab ");
                        break;
                    case '\n':
                        body.Append(lineBreak);
                        break;
                    case < ' ':
                        AppendUnicode((char)(0x2400 + c));
                        break;
                    case (char)0x7F:
                        AppendUnicode('\u2421');
                        break;
                    default:
                        AppendUnicode(c);
                        break;
                }
            }

            if (end > run)
            {
                body.Append(text, run, end - run);
            }
        }

        /// <summary>Appends one UTF-16 unit as <c>\uN?</c> (N signed, as RTF wants; the <c>?</c> is the skipped fallback).</summary>
        /// <param name="c">The unit.</param>
        private void AppendUnicode(char c) =>
            body.Append(@"\u").Append(((short)c).ToString(CultureInfo.InvariantCulture)).Append('?');

        /// <summary>Puts the header (fonts, colors) in front of the body and closes the document.</summary>
        /// <returns>The RTF text.</returns>
        public string Finish()
        {
            var header = new StringBuilder(512 + body.Length);
            header.Append(@"{\rtf1\ansi\ansicpg1252\deff0\uc1{\fonttbl")
                .Append(@"{\f0\fnil\fcharset0 ").Append(FontName(style.ProseFont)).Append(";}")
                .Append(@"{\f1\fmodern\fcharset0 ").Append(FontName(style.CodeFont)).Append(";}")
                .Append(@"{\f2\fnil\fcharset0 Segoe UI Symbol;}}")
                .Append(@"{\colortbl ;");
            foreach (var color in colors)
            {
                header.Append(@"\red").Append(((color >> 16) & 0xFF).ToString(CultureInfo.InvariantCulture))
                    .Append(@"\green").Append(((color >> 8) & 0xFF).ToString(CultureInfo.InvariantCulture))
                    .Append(@"\blue").Append((color & 0xFF).ToString(CultureInfo.InvariantCulture)).Append(';');
            }

            header.Append("}\n").Append(body).Append('}');
            return header.ToString();
        }

        /// <summary>A font name fit for the font table: without the characters RTF gives a meaning (<c>\ { } ;</c>).</summary>
        /// <param name="name">The name.</param>
        /// <returns>The cleaned name.</returns>
        private static string FontName(string name) =>
            new(name.Where(c => c is not ('\\' or '{' or '}' or ';') && c >= ' ').ToArray());
    }
}
