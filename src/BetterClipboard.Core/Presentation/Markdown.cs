using System.Text;

namespace BetterClipboard.Core.Presentation;

/// <summary>How a run of release-notes text is drawn. Flags combine (<c>**`bold code`**</c>).</summary>
[Flags]
public enum MarkdownStyle
{
    /// <summary>Plain text.</summary>
    None = 0,

    /// <summary>Strong emphasis (<c>**text**</c>).</summary>
    Bold = 1,

    /// <summary>Emphasis (<c>*text*</c>).</summary>
    Italic = 2,

    /// <summary>Inline code (<c>`text`</c>): monospace, taken literally.</summary>
    Code = 4,

    /// <summary>Struck through (<c>~~text~~</c>).</summary>
    Strikethrough = 8,
}

/// <summary>
/// One run of text inside a block: its text, its style and where it links to.
/// </summary>
/// <param name="Text">The text to show. A line break (<c>\n</c>) inside it is a hard break the author asked for.</param>
/// <param name="Style">How it is drawn.</param>
/// <param name="Link">
/// The http(s) address the run links to, or <see langword="null"/> for plain text. Links of any other kind (relative,
/// <c>mailto:</c>, <c>javascript:</c>) are never kept: the run is then plain text.
/// </param>
public sealed record MarkdownInline(string Text, MarkdownStyle Style = MarkdownStyle.None, Uri? Link = null);

/// <summary>One block of parsed release notes; the renderer draws each kind its own way.</summary>
public abstract record MarkdownBlock;

/// <summary>
/// A paragraph of text.
/// </summary>
/// <param name="Inlines">Its runs.</param>
/// <param name="Indent">How many list levels it is indented by (a second paragraph inside a list item); 0 for body text.</param>
/// <param name="IsQuote">Whether it is a block quote (<c>&gt; text</c>).</param>
public sealed record MarkdownParagraph(IReadOnlyList<MarkdownInline> Inlines, int Indent = 0, bool IsQuote = false) : MarkdownBlock;

/// <summary>
/// A heading (<c>## text</c>).
/// </summary>
/// <param name="Level">1 for the largest to 6 for the smallest.</param>
/// <param name="Inlines">Its runs.</param>
public sealed record MarkdownHeading(int Level, IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

/// <summary>
/// One item of a list.
/// </summary>
/// <param name="Depth">0 for a top-level item, 1 for an item nested in one, and so on.</param>
/// <param name="Marker">What is drawn in front: a bullet for <c>-</c>/<c>*</c>/<c>+</c> items, the author's number (<c>3.</c>) for numbered ones, a box for task items.</param>
/// <param name="Inlines">Its runs.</param>
public sealed record MarkdownListItem(int Depth, string Marker, IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

/// <summary>
/// A fenced code block (three backticks or tildes): shown in a monospace box, taken literally.
/// </summary>
/// <param name="Code">The code, lines separated by <c>\n</c>, without the fences.</param>
/// <param name="Indent">How many list levels it is indented by; 0 outside a list.</param>
public sealed record MarkdownCodeBlock(string Code, int Indent = 0) : MarkdownBlock;

/// <summary>
/// One row of a table.
/// </summary>
/// <param name="Cells">The row's cells, each a list of runs. Rows of one table may differ in length when the author's did.</param>
public sealed record MarkdownTableRow(IReadOnlyList<IReadOnlyList<MarkdownInline>> Cells);

/// <summary>
/// A table (<c>| a | b |</c> lines with a <c>|---|---|</c> line under the first).
/// </summary>
/// <param name="Rows">Its rows; the first one is the header.</param>
public sealed record MarkdownTable(IReadOnlyList<MarkdownTableRow> Rows) : MarkdownBlock;

/// <summary>A horizontal rule (<c>---</c>).</summary>
public sealed record MarkdownRule : MarkdownBlock;

/// <summary>
/// Reads the Markdown of release notes into blocks the update dialog can draw with plain text controls: paragraphs,
/// headings, lists, code blocks, tables and rules, with bold, italic, code, strikethrough and links inside.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why its own parser.</b> The dialog needs only what release notes use, and WinUI 3 has no Markdown control; a
/// library would add a dependency to ship and to credit for a few hundred lines. HTML is not interpreted at all
/// (<c>&lt;br&gt;</c> excepted): a tag is shown as the text it is, so nothing in a release's notes can run or load
/// anything.
/// </para>
/// <para>
/// <b>Never throws, never hangs.</b> The text comes from the network. Input is cut at <see cref="MaxLength"/>, emphasis
/// and links nest at most <see cref="MaxNesting"/> deep, a paragraph longer than <see cref="MaxInlineLength"/> is taken
/// as plain text, and a search for a closing mark that failed once is not repeated — so the work stays proportional to the
/// text for well-formed notes and bounded for any other.
/// </para>
/// <para>
/// It follows CommonMark and GitHub's flavor where release notes rely on them, and is simpler elsewhere: no setext
/// headings, no reference links, no nested block quotes, no indented code blocks (an indented line is text).
/// </para>
/// </remarks>
public static class MarkdownParser
{
    /// <summary>The most characters parsed; longer notes are cut here.</summary>
    public const int MaxLength = 200_000;

    /// <summary>The longest paragraph whose emphasis and links are parsed; a longer one is shown as plain text.</summary>
    public const int MaxInlineLength = 20_000;

    /// <summary>How deep emphasis and links may nest before the rest is taken as plain text.</summary>
    public const int MaxNesting = 12;

    /// <summary>The bullet drawn for unordered list items.</summary>
    public const string Bullet = "•";

    /// <summary>The marker of an open task item (<c>- [ ] text</c>).</summary>
    public const string OpenTask = "☐";

    /// <summary>The marker of a done task item (<c>- [x] text</c>).</summary>
    public const string DoneTask = "☑";

    /// <summary>The no-break space (U+00A0) that <c>&amp;nbsp;</c> stands for, written as an escape so that it is visible here.</summary>
    private const char NoBreakSpace = '\u00A0';

    /// <summary>
    /// Parses Markdown into blocks.
    /// </summary>
    /// <param name="markdown">The text; may be <see langword="null"/> or empty.</param>
    /// <returns>The blocks in reading order; empty for no text.</returns>
    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        var blocks = new List<MarkdownBlock>();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return blocks;
        }

        if (markdown.Length > MaxLength)
        {
            markdown = markdown[..MaxLength];
        }

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var pending = new PendingText();
        var levels = new List<ListLevel>();
        bool afterBlank = false;
        bool previousHardBreak = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = ExpandTabs(lines[i]);
            int indent = CountIndent(line);
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                Flush(blocks, pending);
                afterBlank = true;
                previousHardBreak = false;
                continue;
            }

            if (TryReadFence(trimmed, out char fenceChar, out int fenceLength))
            {
                Flush(blocks, pending);
                var code = new StringBuilder();
                bool first = true;
                for (i++; i < lines.Length; i++)
                {
                    var codeLine = ExpandTabs(lines[i]);
                    if (IsClosingFence(codeLine.Trim(), fenceChar, fenceLength))
                    {
                        break;
                    }

                    if (!first)
                    {
                        code.Append('\n');
                    }

                    first = false;

                    // The fence's own indentation is not part of the code.
                    code.Append(codeLine, Math.Min(indent, CountIndent(codeLine)), codeLine.Length - Math.Min(indent, CountIndent(codeLine)));
                }

                blocks.Add(new MarkdownCodeBlock(code.ToString(), levels.Count > 0 && indent >= levels[^1].ContentIndent ? levels.Count : 0));
                afterBlank = false;
                previousHardBreak = false;
                continue;
            }

            if (trimmed.StartsWith("<!--", StringComparison.Ordinal))
            {
                // A comment is the author's note to themselves: skipped up to its end, wherever that is.
                Flush(blocks, pending);
                while (i < lines.Length && !lines[i].Contains("-->", StringComparison.Ordinal))
                {
                    i++;
                }

                continue;
            }

            if (indent <= 3 && TryReadHeading(trimmed, out int level, out var headingText))
            {
                Flush(blocks, pending);
                levels.Clear();
                blocks.Add(new MarkdownHeading(level, ParseInline(headingText)));
                afterBlank = false;
                previousHardBreak = false;
                continue;
            }

            if (indent <= 3 && IsRule(trimmed))
            {
                Flush(blocks, pending);
                levels.Clear();
                blocks.Add(new MarkdownRule());
                afterBlank = false;
                previousHardBreak = false;
                continue;
            }

            if (trimmed[0] == '|' && i + 1 < lines.Length && IsTableSeparator(lines[i + 1].Trim()))
            {
                Flush(blocks, pending);
                levels.Clear();
                var rows = new List<MarkdownTableRow> { ReadTableRow(trimmed) };
                for (i += 2; i < lines.Length && lines[i].TrimStart().StartsWith('|'); i++)
                {
                    rows.Add(ReadTableRow(lines[i].Trim()));
                }

                // The loop stopped on the first line that is not a row: look at it again.
                i--;
                blocks.Add(new MarkdownTable(rows));
                afterBlank = false;
                previousHardBreak = false;
                continue;
            }

            if (indent <= 3 && trimmed[0] == '>')
            {
                var quoted = trimmed.AsSpan(1).TrimStart().ToString();
                if (pending.Kind == PendingKind.Quote && !afterBlank)
                {
                    pending.Append(quoted, previousHardBreak);
                }
                else
                {
                    Flush(blocks, pending);
                    levels.Clear();
                    pending.Start(PendingKind.Quote, quoted);
                }

                afterBlank = false;
                previousHardBreak = EndsWithHardBreak(line);
                continue;
            }

            // A numbered item starts a list in the middle of a paragraph only as "1." (CommonMark's rule): a wrapped
            // line of prose that happens to begin with "2026. " stays prose.
            if (TryReadListItem(trimmed, out var marker, out int markerWidth, out var itemText) &&
                (afterBlank || pending.Kind is not (PendingKind.Paragraph or PendingKind.Quote) || !char.IsAsciiDigit(marker[0]) || marker == "1."))
            {
                Flush(blocks, pending);

                // Close the levels this item is left of; then it is a sibling of the level it reaches, or opens a
                // deeper one when it starts at or right of that level's text.
                while (levels.Count > 0 && levels[^1].Indent > indent)
                {
                    levels.RemoveAt(levels.Count - 1);
                }

                var listLevel = new ListLevel(indent, indent + markerWidth);
                if (levels.Count > 0 && indent < levels[^1].ContentIndent)
                {
                    levels[^1] = listLevel;
                }
                else
                {
                    levels.Add(listLevel);
                }

                pending.Start(PendingKind.ListItem, itemText, depth: levels.Count - 1, marker: marker);
                afterBlank = false;
                previousHardBreak = EndsWithHardBreak(line);
                continue;
            }

            // A line of text.
            if (pending.Kind != PendingKind.None && !afterBlank)
            {
                pending.Append(trimmed, previousHardBreak);
            }
            else if (afterBlank && levels.Count > 0 && indent >= levels[^1].ContentIndent)
            {
                // A further paragraph of the list item above: drawn under that item's text.
                Flush(blocks, pending);
                pending.Start(PendingKind.Paragraph, trimmed, depth: levels.Count);
            }
            else
            {
                Flush(blocks, pending);
                levels.Clear();
                pending.Start(PendingKind.Paragraph, trimmed);
            }

            afterBlank = false;
            previousHardBreak = EndsWithHardBreak(line);
        }

        Flush(blocks, pending);
        return blocks;
    }

    /// <summary>
    /// Parses the emphasis, code, links and escapes of one block's text.
    /// </summary>
    /// <param name="text">The block's text (several source lines already joined); may be <see langword="null"/>.</param>
    /// <returns>The runs, neighbours of equal style and link merged; empty for no text.</returns>
    public static IReadOnlyList<MarkdownInline> ParseInline(string? text)
    {
        var output = new List<MarkdownInline>();
        if (string.IsNullOrEmpty(text))
        {
            return output;
        }

        if (text.Length > MaxInlineLength)
        {
            output.Add(new MarkdownInline(text));
            return output;
        }

        ParseInline(text.AsSpan(), MarkdownStyle.None, null, output, 0);
        return output;
    }

    /// <summary>
    /// The text of runs without their styles and links: what a screen reader says, and what tests compare.
    /// </summary>
    /// <param name="inlines">The runs.</param>
    /// <returns>Their texts joined.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inlines"/> is <see langword="null"/>.</exception>
    public static string ToPlainText(IReadOnlyList<MarkdownInline> inlines)
    {
        ArgumentNullException.ThrowIfNull(inlines);
        return inlines.Count == 1 ? inlines[0].Text : string.Concat(inlines.Select(i => i.Text));
    }

    /// <summary>Emits the block being collected, if any, and resets the collector.</summary>
    /// <param name="blocks">The output.</param>
    /// <param name="pending">The collector.</param>
    private static void Flush(List<MarkdownBlock> blocks, PendingText pending)
    {
        if (pending.Kind == PendingKind.None)
        {
            return;
        }

        var inlines = ParseInline(pending.Text.ToString());
        blocks.Add(pending.Kind switch
        {
            PendingKind.ListItem => new MarkdownListItem(pending.Depth, pending.Marker, inlines),
            PendingKind.Quote => new MarkdownParagraph(inlines, IsQuote: true),
            _ => new MarkdownParagraph(inlines, pending.Depth),
        });
        pending.Reset();
    }

    /// <summary>Replaces tabs by spaces up to the next multiple of four, as Markdown counts indentation.</summary>
    /// <param name="line">A source line.</param>
    /// <returns>The line without tabs.</returns>
    private static string ExpandTabs(string line)
    {
        if (!line.Contains('\t'))
        {
            return line;
        }

        var builder = new StringBuilder(line.Length + 8);
        foreach (char c in line)
        {
            if (c == '\t')
            {
                builder.Append(' ', 4 - (builder.Length % 4));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>Counts a line's leading spaces.</summary>
    /// <param name="line">A line without tabs.</param>
    /// <returns>The count.</returns>
    private static int CountIndent(string line)
    {
        int count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Whether a source line ends with a hard break: two or more spaces, or a backslash, before the line end.
    /// </summary>
    /// <param name="line">The untrimmed line.</param>
    /// <returns><see langword="true"/> when the next line must start on a new line.</returns>
    private static bool EndsWithHardBreak(string line) =>
        line.EndsWith("  ", StringComparison.Ordinal) || (line.EndsWith('\\') && !line.EndsWith(@"\\", StringComparison.Ordinal));

    /// <summary>Reads a code fence's opening line.</summary>
    /// <param name="trimmed">The trimmed line.</param>
    /// <param name="fenceChar">The fence's character: a backtick or a tilde.</param>
    /// <param name="fenceLength">How many of them open it (at least three).</param>
    /// <returns><see langword="true"/> for an opening fence.</returns>
    private static bool TryReadFence(string trimmed, out char fenceChar, out int fenceLength)
    {
        fenceChar = trimmed[0];
        fenceLength = 0;
        if (fenceChar is not ('`' or '~'))
        {
            return false;
        }

        while (fenceLength < trimmed.Length && trimmed[fenceLength] == fenceChar)
        {
            fenceLength++;
        }

        // A backtick fence's info text holds no backtick: otherwise the line is inline code, such as ```x```.
        return fenceLength >= 3 && (fenceChar != '`' || trimmed.IndexOf('`', fenceLength) < 0);
    }

    /// <summary>Whether a line closes the open code fence: at least as many fence characters, and nothing else.</summary>
    /// <param name="trimmed">The trimmed line.</param>
    /// <param name="fenceChar">The open fence's character.</param>
    /// <param name="fenceLength">The open fence's length.</param>
    /// <returns><see langword="true"/> for the closing fence.</returns>
    private static bool IsClosingFence(string trimmed, char fenceChar, int fenceLength)
    {
        if (trimmed.Length < fenceLength)
        {
            return false;
        }

        foreach (char c in trimmed)
        {
            if (c != fenceChar)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads an ATX heading (<c>## text</c>, optionally closed with <c>##</c>).</summary>
    /// <param name="trimmed">The trimmed line.</param>
    /// <param name="level">The heading's level, 1 to 6.</param>
    /// <param name="text">Its text.</param>
    /// <returns><see langword="true"/> for a heading.</returns>
    private static bool TryReadHeading(string trimmed, out int level, out string text)
    {
        level = 0;
        text = string.Empty;
        while (level < trimmed.Length && trimmed[level] == '#')
        {
            level++;
        }

        // "#tag" is text: the marks need a blank after them (or nothing at all).
        if (level is 0 or > 6 || (level < trimmed.Length && trimmed[level] != ' '))
        {
            return false;
        }

        var rest = trimmed.AsSpan(level).Trim();
        var withoutClosing = rest.TrimEnd('#');
        if (withoutClosing.Length < rest.Length && (withoutClosing.IsEmpty || withoutClosing[^1] == ' '))
        {
            rest = withoutClosing.TrimEnd();
        }

        text = rest.ToString();
        return true;
    }

    /// <summary>Whether a line is a horizontal rule: three or more of one of <c>- * _</c>, blanks allowed between.</summary>
    /// <param name="trimmed">The trimmed line.</param>
    /// <returns><see langword="true"/> for a rule.</returns>
    private static bool IsRule(string trimmed)
    {
        char mark = trimmed[0];
        if (mark is not ('-' or '*' or '_'))
        {
            return false;
        }

        int count = 0;
        foreach (char c in trimmed)
        {
            if (c == mark)
            {
                count++;
            }
            else if (c != ' ')
            {
                return false;
            }
        }

        return count >= 3;
    }

    /// <summary>Whether a line is the dashes under a table's header: cells of dashes with optional colons.</summary>
    /// <param name="trimmed">The trimmed line.</param>
    /// <returns><see langword="true"/> for a separator line.</returns>
    private static bool IsTableSeparator(string trimmed)
    {
        if (trimmed.Length < 3 || trimmed[0] != '|' || !trimmed.Contains('-'))
        {
            return false;
        }

        foreach (char c in trimmed)
        {
            if (c is not ('|' or '-' or ':' or ' '))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Splits a table line into cells at the bars that are not escaped and not inside inline code.
    /// </summary>
    /// <param name="trimmed">The trimmed line, starting with a bar.</param>
    /// <returns>The row.</returns>
    private static MarkdownTableRow ReadTableRow(string trimmed)
    {
        var cells = new List<IReadOnlyList<MarkdownInline>>();
        var cell = new StringBuilder();
        bool inCode = false;
        for (int i = 1; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (c == '\\' && i + 1 < trimmed.Length && trimmed[i + 1] == '|')
            {
                cell.Append('|');
                i++;
            }
            else if (c == '`')
            {
                inCode = !inCode;
                cell.Append(c);
            }
            else if (c == '|' && !inCode)
            {
                cells.Add(ParseInline(cell.ToString().Trim()));
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        // Text after the last bar is a cell too; nothing after it is the usual closing bar.
        if (cell.ToString().Trim() is { Length: > 0 } last)
        {
            cells.Add(ParseInline(last));
        }

        return new MarkdownTableRow(cells);
    }

    /// <summary>
    /// Reads a list item's marker: <c>-</c>, <c>*</c> or <c>+</c>, or a number with <c>.</c> or <c>)</c>, then a blank.
    /// </summary>
    /// <param name="trimmed">The trimmed line.</param>
    /// <param name="marker">What to draw in front of the item.</param>
    /// <param name="markerWidth">The width of the source marker and its blank: where the item's text starts.</param>
    /// <param name="text">The item's text.</param>
    /// <returns><see langword="true"/> for a list item with text.</returns>
    private static bool TryReadListItem(string trimmed, out string marker, out int markerWidth, out string text)
    {
        marker = Bullet;
        markerWidth = 0;
        text = string.Empty;
        int end;
        if (trimmed[0] is '-' or '*' or '+')
        {
            end = 1;
        }
        else
        {
            end = 0;
            while (end < trimmed.Length && end < 9 && char.IsAsciiDigit(trimmed[end]))
            {
                end++;
            }

            if (end == 0 || end >= trimmed.Length || trimmed[end] is not ('.' or ')'))
            {
                return false;
            }

            marker = trimmed[..end] + ".";
            end++;
        }

        if (end >= trimmed.Length || trimmed[end] != ' ')
        {
            return false;
        }

        var rest = trimmed.AsSpan(end).TrimStart();
        if (rest.IsEmpty)
        {
            return false;
        }

        markerWidth = trimmed.Length - rest.Length;

        // GitHub's task items: the box replaces the bullet.
        if (rest.Length > 3 && rest[0] == '[' && rest[2] == ']' && rest[3] == ' ' && rest[1] is ' ' or 'x' or 'X')
        {
            marker = rest[1] == ' ' ? OpenTask : DoneTask;
            rest = rest[4..].TrimStart();
        }

        text = rest.ToString();
        return true;
    }

    /// <summary>
    /// Parses one stretch of inline text into <paramref name="output"/>, with <paramref name="style"/> and
    /// <paramref name="link"/> applying to all of it.
    /// </summary>
    /// <param name="text">The stretch.</param>
    /// <param name="style">The style inherited from the marks around it.</param>
    /// <param name="link">The link it is inside of, or <see langword="null"/>.</param>
    /// <param name="output">Receives the runs.</param>
    /// <param name="nesting">How deep this stretch is nested.</param>
    private static void ParseInline(ReadOnlySpan<char> text, MarkdownStyle style, Uri? link, List<MarkdownInline> output, int nesting)
    {
        var buffer = new StringBuilder();

        // A search for a closing mark that reached the end of this stretch without success need not run again from a
        // later position: it would fail too. Without this, a text of many unclosed marks would cost quadratic time.
        bool noBoldStar = false, noBoldUnderscore = false, noItalicStar = false, noItalicUnderscore = false, noStrike = false, noBracket = false;
        bool nested = nesting >= MaxNesting;

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            switch (c)
            {
                case '\\' when i + 1 < text.Length && char.IsAsciiLetterOrDigit(text[i + 1]) is false && text[i + 1] < 128 && !char.IsWhiteSpace(text[i + 1]):
                    // A backslash before punctuation takes that character literally.
                    buffer.Append(text[i + 1]);
                    i += 2;
                    continue;

                case '`':
                {
                    int run = RunLength(text, i, '`');
                    int close = FindBacktickClose(text, i + run, run);
                    if (close < 0)
                    {
                        buffer.Append('`', run);
                        i += run;
                        continue;
                    }

                    FlushText(buffer, style, link, output);
                    Add(output, NormalizeCode(text[(i + run)..close]), style | MarkdownStyle.Code, link);
                    i = close + run;
                    continue;
                }

                case '!' when i + 1 < text.Length && text[i + 1] == '[' && !nested && !noBracket:
                case '[' when !nested && !noBracket:
                {
                    int open = c == '!' ? i + 1 : i;
                    if (TryReadLink(text, open, out int labelEnd, out int end, out var target, ref noBracket))
                    {
                        FlushText(buffer, style, link, output);
                        var label = text[(open + 1)..labelEnd];

                        // An image is shown as its description; inside a link, the outer link wins (links do not nest).
                        if (c == '!' && label.IsEmpty)
                        {
                            Add(output, "image", style, link ?? target);
                        }
                        else
                        {
                            ParseInline(label, style, link ?? target, output, nesting + 1);
                        }

                        i = end;
                        continue;
                    }

                    break;
                }

                case '<':
                {
                    int close = text[i..].IndexOf('>');
                    if (close > 1)
                    {
                        var inside = text.Slice(i + 1, close - 1);
                        if (TryCreateHttpUri(inside, out var auto))
                        {
                            FlushText(buffer, style, link, output);
                            Add(output, inside.ToString(), style, link ?? auto);
                            i += close + 1;
                            continue;
                        }

                        if (inside.Equals("br", StringComparison.OrdinalIgnoreCase) || inside.Equals("br/", StringComparison.OrdinalIgnoreCase) || inside.Equals("br /", StringComparison.OrdinalIgnoreCase))
                        {
                            buffer.Append('\n');
                            i += close + 1;
                            continue;
                        }
                    }

                    break;
                }

                case 'h' when link is null && IsBareUrlStart(text, i):
                {
                    int end = FindBareUrlEnd(text, i);
                    if (TryCreateHttpUri(text[i..end], out var bare))
                    {
                        FlushText(buffer, style, link, output);
                        Add(output, text[i..end].ToString(), style, bare);
                        i = end;
                        continue;
                    }

                    break;
                }

                case '*' or '_' when !nested:
                {
                    int run = RunLength(text, i, c);
                    bool canOpen = i + run < text.Length && !char.IsWhiteSpace(text[i + run]) &&
                                   (c == '*' || i == 0 || !char.IsLetterOrDigit(text[i - 1]));
                    ref bool noBold = ref (c == '*' ? ref noBoldStar : ref noBoldUnderscore);
                    ref bool noItalic = ref (c == '*' ? ref noItalicStar : ref noItalicUnderscore);
                    if (canOpen && run >= 2 && !noBold)
                    {
                        int close = FindEmphasisClose(text, i + 2, c, 2, preferRunEnd: run >= 3);
                        if (close >= 0)
                        {
                            FlushText(buffer, style, link, output);
                            ParseInline(text[(i + 2)..close], style | MarkdownStyle.Bold, link, output, nesting + 1);
                            i = close + 2;
                            continue;
                        }

                        noBold = true;
                    }

                    if (canOpen && run is 1 or 3 && !noItalic)
                    {
                        int close = FindEmphasisClose(text, i + 1, c, 1, preferRunEnd: false);
                        if (close >= 0)
                        {
                            FlushText(buffer, style, link, output);
                            ParseInline(text[(i + 1)..close], style | MarkdownStyle.Italic, link, output, nesting + 1);
                            i = close + 1;
                            continue;
                        }

                        noItalic = true;
                    }

                    // Not emphasis: the marks are text.
                    buffer.Append(c, run);
                    i += run;
                    continue;
                }

                case '~' when !nested && !noStrike && i + 2 < text.Length && text[i + 1] == '~' && !char.IsWhiteSpace(text[i + 2]):
                {
                    int close = FindEmphasisClose(text, i + 2, '~', 2, preferRunEnd: false);
                    if (close >= 0)
                    {
                        FlushText(buffer, style, link, output);
                        ParseInline(text[(i + 2)..close], style | MarkdownStyle.Strikethrough, link, output, nesting + 1);
                        i = close + 2;
                        continue;
                    }

                    noStrike = true;
                    break;
                }

                case '&':
                {
                    if (TryReadEntity(text[i..], out char decoded, out int length))
                    {
                        buffer.Append(decoded);
                        i += length;
                        continue;
                    }

                    break;
                }
            }

            buffer.Append(c);
            i++;
        }

        FlushText(buffer, style, link, output);
    }

    /// <summary>Emits the plain text collected so far.</summary>
    /// <param name="buffer">The collected text; emptied.</param>
    /// <param name="style">Its style.</param>
    /// <param name="link">Its link.</param>
    /// <param name="output">Receives the run.</param>
    private static void FlushText(StringBuilder buffer, MarkdownStyle style, Uri? link, List<MarkdownInline> output)
    {
        if (buffer.Length > 0)
        {
            Add(output, buffer.ToString(), style, link);
            buffer.Clear();
        }
    }

    /// <summary>Adds a run, merging it into the previous one when style and link are the same.</summary>
    /// <param name="output">The runs so far.</param>
    /// <param name="text">The run's text.</param>
    /// <param name="style">Its style.</param>
    /// <param name="link">Its link.</param>
    private static void Add(List<MarkdownInline> output, string text, MarkdownStyle style, Uri? link)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (output.Count > 0 && output[^1] is { } last && last.Style == style && Equals(last.Link, link))
        {
            output[^1] = last with { Text = last.Text + text };
        }
        else
        {
            output.Add(new MarkdownInline(text, style, link));
        }
    }

    /// <summary>Counts how many times <paramref name="mark"/> repeats from <paramref name="start"/>.</summary>
    /// <param name="text">The text.</param>
    /// <param name="start">Where the run starts.</param>
    /// <param name="mark">The repeated character.</param>
    /// <returns>The run's length (at least 1).</returns>
    private static int RunLength(ReadOnlySpan<char> text, int start, char mark)
    {
        int end = start;
        while (end < text.Length && text[end] == mark)
        {
            end++;
        }

        return end - start;
    }

    /// <summary>Finds the backtick run of exactly <paramref name="length"/> that closes an inline code span.</summary>
    /// <param name="text">The text.</param>
    /// <param name="from">Where to start looking (after the opening run).</param>
    /// <param name="length">The opening run's length.</param>
    /// <returns>The closing run's start, or -1.</returns>
    private static int FindBacktickClose(ReadOnlySpan<char> text, int from, int length)
    {
        for (int i = from; i < text.Length; i++)
        {
            if (text[i] != '`')
            {
                continue;
            }

            int run = RunLength(text, i, '`');
            if (run == length)
            {
                return i;
            }

            i += run - 1;
        }

        return -1;
    }

    /// <summary>
    /// The content of an inline code span as shown: line breaks become blanks, and one blank at each end is dropped when
    /// both ends have one (the way to write a span that starts or ends with a backtick).
    /// </summary>
    /// <param name="content">The text between the backtick runs.</param>
    /// <returns>The code.</returns>
    private static string NormalizeCode(ReadOnlySpan<char> content)
    {
        var code = content.ToString().Replace('\n', ' ');
        return code.Length > 2 && code[0] == ' ' && code[^1] == ' ' && code.AsSpan().Trim().Length > 0 ? code[1..^1] : code;
    }

    /// <summary>
    /// Finds the mark that closes emphasis opened before <paramref name="from"/>, skipping inline code and escaped
    /// characters.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="from">Where to start looking (after the opening mark).</param>
    /// <param name="mark">The emphasis character.</param>
    /// <param name="length">1 for italic, 2 for bold and strikethrough.</param>
    /// <param name="preferRunEnd">
    /// For bold opened by three or more marks (<c>***both***</c>): close with the last two marks of a longer run, so the
    /// italic inside keeps its own.
    /// </param>
    /// <returns>The closing mark's start, or -1.</returns>
    private static int FindEmphasisClose(ReadOnlySpan<char> text, int from, char mark, int length, bool preferRunEnd)
    {
        for (int i = from; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\')
            {
                i++;
                continue;
            }

            if (c == '`')
            {
                int codeRun = RunLength(text, i, '`');
                int codeClose = FindBacktickClose(text, i + codeRun, codeRun);
                i = codeClose < 0 ? i + codeRun - 1 : codeClose + codeRun - 1;
                continue;
            }

            if (c != mark)
            {
                continue;
            }

            int run = RunLength(text, i, mark);

            // A closing mark follows text, and an underscore does not close in the middle of a word.
            bool canClose = i > from && !char.IsWhiteSpace(text[i - 1]) &&
                            (mark != '_' || i + run >= text.Length || !char.IsLetterOrDigit(text[i + run]));
            if (canClose && (length == 1 ? run is 1 or 3 : run >= 2))
            {
                // Italic takes the last mark of a run of three (the first two close a bold inside).
                return length == 1 ? i + run - 1 : preferRunEnd ? i + run - 2 : i;
            }

            i += run - 1;
        }

        return -1;
    }

    /// <summary>
    /// Reads <c>[label](address "title")</c> starting at its opening bracket.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="open">The index of <c>[</c>.</param>
    /// <param name="labelEnd">The index of the label's closing <c>]</c>.</param>
    /// <param name="end">The index after the closing <c>)</c>.</param>
    /// <param name="target">The address when it is http(s); <see langword="null"/> for any other, which leaves the label plain text.</param>
    /// <param name="noBracket">Set when no closing bracket exists up to the end, so later brackets need not be searched.</param>
    /// <returns><see langword="true"/> when the text at <paramref name="open"/> has the shape of a link.</returns>
    private static bool TryReadLink(ReadOnlySpan<char> text, int open, out int labelEnd, out int end, out Uri? target, ref bool noBracket)
    {
        labelEnd = end = -1;
        target = null;
        int depth = 0;
        int close = -1;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\')
            {
                i++;
            }
            else if (c == '[')
            {
                depth++;
            }
            else if (c == ']' && --depth == 0)
            {
                close = i;
                break;
            }
        }

        if (close < 0)
        {
            noBracket = true;
            return false;
        }

        if (close + 1 >= text.Length || text[close + 1] != '(')
        {
            return false;
        }

        // The address ends at the bracket that balances the opening one; a title in quotes may follow the address.
        int parens = 0;
        int addressEnd = -1;
        for (int i = close + 1; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\')
            {
                i++;
            }
            else if (c == '(')
            {
                parens++;
            }
            else if (c == ')' && --parens == 0)
            {
                addressEnd = i;
                break;
            }
            else if (c == '\n')
            {
                break;
            }
        }

        if (addressEnd < 0)
        {
            return false;
        }

        var address = text[(close + 2)..addressEnd].Trim();
        int blank = address.IndexOf(' ');
        if (blank > 0)
        {
            address = address[..blank];
        }

        if (address.Length > 1 && address[0] == '<' && address[^1] == '>')
        {
            address = address[1..^1];
        }

        TryCreateHttpUri(address, out target);
        labelEnd = close;
        end = addressEnd + 1;
        return true;
    }

    /// <summary>Creates an absolute http or https address from text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="uri">The address.</param>
    /// <returns><see langword="true"/> for an http(s) address without blanks.</returns>
    private static bool TryCreateHttpUri(ReadOnlySpan<char> text, out Uri? uri)
    {
        uri = null;
        if (text.Length is < 8 or > 2048 || text.IndexOfAny(' ', '\n', '"') >= 0 ||
            !(text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return Uri.TryCreate(text.ToString(), UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }

    /// <summary>Whether a bare address (<c>https://…</c> written without brackets) starts here, at the start of a word.</summary>
    /// <param name="text">The text.</param>
    /// <param name="index">The index of the <c>h</c>.</param>
    /// <returns><see langword="true"/> when an address starts here.</returns>
    private static bool IsBareUrlStart(ReadOnlySpan<char> text, int index)
    {
        if (index > 0 && (char.IsLetterOrDigit(text[index - 1]) || text[index - 1] is '/' or '.' or '_' or '-'))
        {
            return false;
        }

        var rest = text[index..];
        return rest.StartsWith("https://", StringComparison.Ordinal) || rest.StartsWith("http://", StringComparison.Ordinal);
    }

    /// <summary>
    /// Finds where a bare address ends: at a blank or an angle bracket, without the sentence punctuation after it and
    /// without a closing bracket that has no partner inside the address.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="start">Where the address starts.</param>
    /// <returns>The index after its last character.</returns>
    private static int FindBareUrlEnd(ReadOnlySpan<char> text, int start)
    {
        int end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('<' or '>' or '`' or '|'))
        {
            end++;
        }

        while (end > start)
        {
            char last = text[end - 1];
            if (last is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"' or '*' or '_')
            {
                end--;
            }
            else if (last == ')' && text[start..end].Count('(') < text[start..end].Count(')'))
            {
                end--;
            }
            else
            {
                break;
            }
        }

        return end;
    }

    /// <summary>Decodes the few HTML entities people type in Markdown.</summary>
    /// <param name="text">The text starting at <c>&amp;</c>.</param>
    /// <param name="decoded">The character.</param>
    /// <param name="length">How many characters the entity takes.</param>
    /// <returns><see langword="true"/> for a known entity.</returns>
    private static bool TryReadEntity(ReadOnlySpan<char> text, out char decoded, out int length)
    {
        ReadOnlySpan<(string Name, char Value)> entities =
        [
            ("&amp;", '&'), ("&lt;", '<'), ("&gt;", '>'), ("&quot;", '"'), ("&#39;", '\''), ("&apos;", '\''), ("&nbsp;", NoBreakSpace),
        ];
        foreach (var (name, value) in entities)
        {
            if (text.StartsWith(name, StringComparison.Ordinal))
            {
                decoded = value;
                length = name.Length;
                return true;
            }
        }

        decoded = '\0';
        length = 0;
        return false;
    }

    /// <summary>What the block collector is collecting.</summary>
    private enum PendingKind
    {
        /// <summary>Nothing.</summary>
        None,

        /// <summary>A paragraph.</summary>
        Paragraph,

        /// <summary>A list item.</summary>
        ListItem,

        /// <summary>A block quote.</summary>
        Quote,
    }

    /// <summary>
    /// One open list level.
    /// </summary>
    /// <param name="Indent">The column of its items' markers.</param>
    /// <param name="ContentIndent">The column of its items' text; a marker at or right of it starts a nested list.</param>
    private readonly record struct ListLevel(int Indent, int ContentIndent);

    /// <summary>Collects the source lines of one text block until the block ends.</summary>
    private sealed class PendingText
    {
        /// <summary>What is being collected.</summary>
        public PendingKind Kind { get; private set; }

        /// <summary>The block's text so far, source lines joined.</summary>
        public StringBuilder Text { get; } = new();

        /// <summary>A list item's depth, or a paragraph's indentation in list levels.</summary>
        public int Depth { get; private set; }

        /// <summary>A list item's marker.</summary>
        public string Marker { get; private set; } = Bullet;

        /// <summary>Starts a block.</summary>
        /// <param name="kind">Its kind.</param>
        /// <param name="text">Its first line.</param>
        /// <param name="depth">Its depth or indentation.</param>
        /// <param name="marker">A list item's marker.</param>
        public void Start(PendingKind kind, string text, int depth = 0, string marker = Bullet)
        {
            Kind = kind;
            Depth = depth;
            Marker = marker;
            Text.Clear().Append(text);
        }

        /// <summary>Adds a further source line: after a blank, or on a new line when the author asked for a hard break.</summary>
        /// <param name="text">The line, trimmed.</param>
        /// <param name="hardBreak">Whether the line before ended with a hard break.</param>
        public void Append(string text, bool hardBreak)
        {
            if (hardBreak)
            {
                // The marks of the break itself (trailing blanks were trimmed; a backslash was not).
                if (Text.Length > 0 && Text[^1] == '\\')
                {
                    Text.Length--;
                }

                Text.Append('\n');
            }
            else
            {
                Text.Append(' ');
            }

            Text.Append(text);
        }

        /// <summary>Forgets the block.</summary>
        public void Reset()
        {
            Kind = PendingKind.None;
            Depth = 0;
            Marker = Bullet;
            Text.Clear();
        }
    }
}
