using System.Globalization;
using System.Text;
using BetterClipboard.Core.Presentation;

namespace BetterClipboard.Core.TextView;

/// <summary>
/// The fonts and sizes the text viewer draws with. Sizes are in DIPs, like the rest of the app's text; the RTF writer turns
/// them into points.
/// </summary>
/// <param name="ProseFont">The font of Markdown text.</param>
/// <param name="CodeFont">The font of code and plain text (a monospace font the PC has).</param>
/// <param name="ProseSize">Markdown body text, in DIPs.</param>
/// <param name="CodeSize">Code, in DIPs.</param>
public sealed record TextViewStyle(string ProseFont, string CodeFont, double ProseSize, double CodeSize)
{
    /// <summary>The smallest and largest text-size factors the viewer allows (50 % to 300 %).</summary>
    public const double MinScale = 0.5, MaxScale = 3.0;

    /// <summary>The style at 100 %: Segoe UI at 14 DIPs (the panel's body size) and the given code font at 13.</summary>
    /// <param name="codeFont">The monospace font to use (Cascadia Mono where Windows has it, else Consolas).</param>
    /// <returns>The style.</returns>
    public static TextViewStyle Default(string codeFont) => new("Segoe UI", codeFont, 14, 13);

    /// <summary>The style with every size multiplied by <paramref name="scale"/>, clamped to <see cref="MinScale"/>–<see cref="MaxScale"/>.</summary>
    /// <param name="scale">The factor (1 = 100 %); NaN counts as 1.</param>
    /// <returns>The scaled style.</returns>
    public TextViewStyle Scaled(double scale)
    {
        double factor = double.IsFinite(scale) ? Math.Clamp(scale, MinScale, MaxScale) : 1;
        return this with { ProseSize = ProseSize * factor, CodeSize = CodeSize * factor };
    }
}

/// <summary>
/// Writes what the text viewer shows as one RTF document for WinUI's <c>RichEditBox</c>: colored code, or Markdown drawn as
/// a document (headings, lists, quotes, code blocks, tables, rules, emphasis and links). RTF gets colored text into
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

    /// <summary>The width of a list item's marker column (the hanging indent), in DIPs.</summary>
    private const double MarkerDip = 16;

    /// <summary>The widest a table column is laid out, in characters; longer cells overflow their column.</summary>
    private const int MaxColumnChars = 48;

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
    /// <paramref name="codeTokens"/> says.
    /// </summary>
    /// <param name="blocks">The parsed blocks (<see cref="MarkdownParser.Parse(string?, MarkdownParseOptions)"/>).</param>
    /// <param name="codeTokens">The tokens of a code block's code (empty for none), by block.</param>
    /// <param name="palette">The theme's colors.</param>
    /// <param name="style">Fonts and sizes.</param>
    /// <param name="cancellationToken">Stops a long run.</param>
    /// <returns>The RTF document.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static string Markdown(IReadOnlyList<MarkdownBlock> blocks, Func<MarkdownCodeBlock, IReadOnlyList<SyntaxToken>> codeTokens, SyntaxPalette palette, TextViewStyle style, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(codeTokens);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(style);

        var rtf = new RtfBuilder(palette, style, 4096);
        int prose = HalfPoints(style.ProseSize);
        int proseColor = rtf.ColorIndex(palette.Prose);
        int gap = Twips(ParagraphGapDip);
        for (int index = 0; index < blocks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool last = index == blocks.Count - 1;
            switch (blocks[index])
            {
                case MarkdownHeading heading:
                {
                    // Larger and bold, with room above it: a heading belongs to what follows it.
                    double factor = heading.Level switch { 1 => 1.5, 2 => 1.3, 3 => 1.15, _ => 1.0 };
                    rtf.Raw($@"\pard\sb{(index == 0 ? 0 : Twips(6))}\sa{Twips(4)}\f0\fs{HalfPoints(style.ProseSize * factor)}\cf{proseColor}\b ");
                    rtf.Inlines(heading.Inlines);
                    rtf.Raw(@"\b0");
                    break;
                }

                case MarkdownListItem item:
                {
                    // A hanging indent: the marker in its own column, wrapped lines under the item's text.
                    int left = Twips((item.Depth * IndentDip) + MarkerDip);
                    rtf.Raw($@"\pard\fi-{Twips(MarkerDip)}\li{left}\tx{left}\sa{Twips(3)}\f0\fs{prose}\cf{proseColor} ");
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
                    break;
                }

                case MarkdownCodeBlock code:
                    WriteCodeBlock(rtf, code, codeTokens(code), palette, style, gap, cancellationToken);
                    break;

                case MarkdownTable table:
                    WriteTable(rtf, table, palette, style, gap);
                    break;

                case MarkdownRule:
                    rtf.Raw($@"\pard\sa{gap}\f0\fs{prose}\cf{rtf.ColorIndex(palette.Secondary)} ");
                    rtf.Text(new string('─', 32));
                    break;

                case MarkdownParagraph { IsQuote: true } quote:
                    rtf.Raw($@"\pard\li{Twips(IndentDip)}\sa{gap}\f0\fs{prose}\cf{rtf.ColorIndex(palette.Secondary)}\i ");
                    rtf.Inlines(quote.Inlines);
                    rtf.Raw(@"\i0");
                    break;

                case MarkdownParagraph paragraph:
                    rtf.Raw($@"\pard\li{Twips(paragraph.Indent * IndentDip)}\sa{gap}\f0\fs{prose}\cf{proseColor} ");
                    rtf.Inlines(paragraph.Inlines);
                    break;

                default:
                    continue;
            }

            if (!last)
            {
                rtf.Raw("\\par\n");
            }
        }

        return rtf.Finish();
    }

    /// <summary>Writes a fenced code block: its lines in the code font on the code background, colored by its tokens.</summary>
    /// <param name="rtf">The document being written.</param>
    /// <param name="code">The block.</param>
    /// <param name="tokens">Its code's tokens.</param>
    /// <param name="palette">The theme's colors.</param>
    /// <param name="style">Fonts and sizes.</param>
    /// <param name="gap">The gap after the block, in twips.</param>
    /// <param name="cancellationToken">Stops a long run.</param>
    private static void WriteCodeBlock(RtfBuilder rtf, MarkdownCodeBlock code, IReadOnlyList<SyntaxToken> tokens, SyntaxPalette palette, TextViewStyle style, int gap, CancellationToken cancellationToken)
    {
        int background = rtf.ColorIndex(palette.CodeBackground);
        rtf.Raw($@"\pard\li{Twips((code.Indent * IndentDip) + 6)}\sa0\f1\fs{HalfPoints(style.CodeSize)}\cf{rtf.ColorIndex(palette.Text)} ");
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
    /// Writes a table as an aligned monospace grid: cells padded to their column's width, the header bold and underlined
    /// with a rule. RichEdit's own tables would need column widths in twips, which a window the user resizes cannot have.
    /// </summary>
    /// <param name="rtf">The document being written.</param>
    /// <param name="table">The table.</param>
    /// <param name="palette">The theme's colors.</param>
    /// <param name="style">Fonts and sizes.</param>
    /// <param name="gap">The gap after the table, in twips.</param>
    private static void WriteTable(RtfBuilder rtf, MarkdownTable table, SyntaxPalette palette, TextViewStyle style, int gap)
    {
        var rows = table.Rows.Select(row => row.Cells.Select(cell => MarkdownParser.ToPlainText(cell).Replace('\n', ' ')).ToArray()).ToArray();
        int columns = rows.Length == 0 ? 0 : rows.Max(row => row.Length);
        var widths = new int[columns];
        foreach (var row in rows)
        {
            for (int c = 0; c < row.Length; c++)
            {
                widths[c] = Math.Min(MaxColumnChars, Math.Max(widths[c], row[c].Length));
            }
        }

        int textColor = rtf.ColorIndex(palette.Prose);
        int ruleColor = rtf.ColorIndex(palette.Secondary);
        for (int r = 0; r < rows.Length; r++)
        {
            bool lastRow = r == rows.Length - 1;
            rtf.Raw($@"\pard\sa{(lastRow ? gap : 0)}\f1\fs{HalfPoints(style.CodeSize)}\cf{textColor}{(r == 0 ? @"\b" : string.Empty)} ");
            var line = new StringBuilder();
            for (int c = 0; c < columns; c++)
            {
                var cell = c < rows[r].Length ? rows[r][c] : string.Empty;
                line.Append(cell.PadRight(widths[c]));
                if (c < columns - 1)
                {
                    line.Append("  ");
                }
            }

            rtf.Text(line.ToString().TrimEnd());
            if (r == 0)
            {
                rtf.Raw($@"\b0\par" + "\n" + $@"\pard\sa0\cf{ruleColor} ");
                rtf.Text(string.Join("  ", widths.Select(width => new string('─', Math.Max(1, width)))));
            }

            if (!lastRow)
            {
                rtf.Raw("\\par\n");
            }
        }
    }

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
