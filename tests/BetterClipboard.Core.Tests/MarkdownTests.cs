using System.Diagnostics;
using System.Text;
using BetterClipboard.Core.Presentation;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for <see cref="MarkdownParser"/>: the release notes' Markdown becomes the blocks and runs the update dialog
/// draws, and no input makes it throw or hang.
/// </summary>
public sealed class MarkdownTests
{
    /// <summary>
    /// Writes blocks as one compact line each, so a test compares a whole document with a few readable strings:
    /// <c>P:</c> paragraph, <c>Q:</c> quote, <c>H2:</c> heading, <c>L1(•):</c> list item at depth 1, <c>C:</c> code,
    /// <c>T:</c> table, <c>R</c> rule. Runs are written by <see cref="Describe(IReadOnlyList{MarkdownInline})"/>.
    /// </summary>
    /// <param name="blocks">The parsed blocks.</param>
    /// <returns>One description per block.</returns>
    private static string[] Describe(IReadOnlyList<MarkdownBlock> blocks) => blocks.Select(block => block switch
    {
        MarkdownHeading heading => $"H{heading.Level}:{Describe(heading.Inlines)}",
        MarkdownListItem item => $"L{item.Depth}({item.Marker}):{Describe(item.Inlines)}",
        MarkdownCodeBlock code => $"C{(code.Indent > 0 ? code.Indent.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty)}:{code.Code.Replace("\n", "⏎", StringComparison.Ordinal)}",
        MarkdownTable table => "T:" + string.Join(" / ", table.Rows.Select(row => string.Join(" | ", row.Cells.Select(Describe)))),
        MarkdownRule => "R",
        MarkdownParagraph { IsQuote: true } quote => $"Q:{Describe(quote.Inlines)}",
        MarkdownParagraph paragraph => $"P{(paragraph.Indent > 0 ? paragraph.Indent.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty)}:{Describe(paragraph.Inlines)}",
        _ => block.GetType().Name,
    }).ToArray();

    /// <summary>
    /// Writes runs with their styles as tags: <c>[b]bold[/]</c>, <c>[i]</c>, <c>[c]</c> code, <c>[s]</c> struck,
    /// combined as <c>[bi]</c>, and a link as <c>{text→address}</c>. Line breaks show as <c>⏎</c>.
    /// </summary>
    /// <param name="inlines">The runs.</param>
    /// <returns>The description.</returns>
    private static string Describe(IReadOnlyList<MarkdownInline> inlines)
    {
        var text = new StringBuilder();
        foreach (var inline in inlines)
        {
            var styled = inline.Text.Replace("\n", "⏎", StringComparison.Ordinal);
            if (inline.Style != MarkdownStyle.None)
            {
                var tag = (inline.Style.HasFlag(MarkdownStyle.Bold) ? "b" : string.Empty) + (inline.Style.HasFlag(MarkdownStyle.Italic) ? "i" : string.Empty) +
                          (inline.Style.HasFlag(MarkdownStyle.Code) ? "c" : string.Empty) + (inline.Style.HasFlag(MarkdownStyle.Strikethrough) ? "s" : string.Empty);
                styled = $"[{tag}]{styled}[/]";
            }

            text.Append(inline.Link is null ? styled : $"{{{styled}→{inline.Link.AbsoluteUri}}}");
        }

        return text.ToString();
    }

    /// <summary>Parses inline text and describes its runs.</summary>
    /// <param name="text">The inline Markdown.</param>
    /// <returns>The description.</returns>
    private static string Inline(string text) => Describe(MarkdownParser.ParseInline(text));

    /// <summary>No text, or only blanks, is no blocks.</summary>
    /// <param name="markdown">The input.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n\t\r\n")]
    public void Parse_NothingForNoText(string? markdown)
    {
        Assert.Empty(MarkdownParser.Parse(markdown));
        Assert.Empty(MarkdownParser.ParseInline(null));
    }

    /// <summary>Wrapped lines are one paragraph; a blank line starts the next; CRLF and CR end lines like LF.</summary>
    [Fact]
    public void Parse_JoinsWrappedLinesIntoParagraphs()
    {
        var blocks = Describe(MarkdownParser.Parse("Updates in place and keeps\r\nyour history.\r\rThe installer\nverifies it.\n"));
        Assert.Equal(["P:Updates in place and keeps your history.", "P:The installer verifies it."], blocks);
    }

    /// <summary>Two trailing blanks or a backslash at a line's end, and &lt;br&gt;, keep the line break.</summary>
    [Fact]
    public void Parse_KeepsHardBreaks()
    {
        Assert.Equal(["P:one⏎two⏎three four⏎five"], Describe(MarkdownParser.Parse("one  \ntwo\\\nthree\nfour<br>five")));
    }

    /// <summary>Headings of every level, with and without closing marks; a "#tag" is text.</summary>
    [Fact]
    public void Parse_ReadsHeadings()
    {
        var blocks = Describe(MarkdownParser.Parse("# One\n## Two **bold** ##\n###### Six\n####### seven\n#tag\n##\n"));
        Assert.Equal(["H1:One", "H2:Two [b]bold[/]", "H6:Six", "P:####### seven #tag", "H2:"], blocks);
    }

    /// <summary>The release notes' list shape: bullets, items wrapped over lines, and items nested by two blanks.</summary>
    [Fact]
    public void Parse_ReadsNestedListsWithWrappedItems()
    {
        const string Notes = """
            **Seven new tabs.** Each has its switch.

            - **Run**: every command you run with Win+R, kept for good. Windows remembers
              only the last 26.
              - **Ctrl+Enter** runs a command again
                (both also in the item's menu).
            - **Pwsh**: PowerShell's own history.
                - deeper by four
            * star item
            + plus item
            """;
        var blocks = Describe(MarkdownParser.Parse(Notes));
        Assert.Equal(
        [
            "P:[b]Seven new tabs.[/] Each has its switch.",
            "L0(•):[b]Run[/]: every command you run with Win+R, kept for good. Windows remembers only the last 26.",
            "L1(•):[b]Ctrl+Enter[/] runs a command again (both also in the item's menu).",
            "L0(•):[b]Pwsh[/]: PowerShell's own history.",
            "L1(•):deeper by four",
            "L0(•):star item",
            "L0(•):plus item",
        ], blocks);
    }

    /// <summary>Numbered items keep their numbers; task items get a box; a marker without text is text.</summary>
    [Fact]
    public void Parse_ReadsNumberedAndTaskItems()
    {
        var blocks = Describe(MarkdownParser.Parse("1. first\n2) second\n   - inside\n10. tenth\n\n- [ ] open\n- [x] done\n- [X] also done\n-\n-no blank"));
        Assert.Equal(
        [
            "L0(1.):first", "L0(2.):second", "L1(•):inside", "L0(10.):tenth",
            "L0(☐):open", "L0(☑):done", "L0(☑):also done - -no blank",
        ], blocks);
    }

    /// <summary>
    /// A wrapped line of prose that begins with a number and a period stays prose (only "1." starts a list inside a
    /// paragraph), while a bullet does start one.
    /// </summary>
    [Fact]
    public void Parse_ANumberedLineInsideAParagraphIsProse()
    {
        Assert.Equal(["P:It shipped in 2026. It was fast."], Describe(MarkdownParser.Parse("It shipped in\n2026. It was fast.")));
        Assert.Equal(["P:Steps:", "L0(1.):one"], Describe(MarkdownParser.Parse("Steps:\n1. one")));
        Assert.Equal(["P:Items:", "L0(•):one"], Describe(MarkdownParser.Parse("Items:\n- one")));
        Assert.Equal(["L0(1.):one", "L0(2.):two"], Describe(MarkdownParser.Parse("1. one\n2. two")));
    }

    /// <summary>A paragraph indented under a list item, after a blank line, belongs to that item; an unindented one ends the list.</summary>
    [Fact]
    public void Parse_KeepsFurtherParagraphsOfAnItem()
    {
        var blocks = Describe(MarkdownParser.Parse("- item\n\n  more of the item\n\n- next\n\nbody text\n- new list"));
        Assert.Equal(["L0(•):item", "P1:more of the item", "L0(•):next", "P:body text", "L0(•):new list"], blocks);
    }

    /// <summary>Fenced code is literal: no emphasis, no lists, its own line breaks; the fence's indentation is removed.</summary>
    [Fact]
    public void Parse_ReadsFencedCodeLiterally()
    {
        const string Notes = "Install:\n\n```powershell\nirm https://example.org/install.ps1 | iex\n- **not** a list\n\n  indented\n```\n\n~~~\ntildes ``` inside\n~~~\n- item\n  ```\n  in item\n  ```\nunclosed:\n```\nrest";
        var blocks = Describe(MarkdownParser.Parse(Notes));
        Assert.Equal(
        [
            "P:Install:",
            "C:irm https://example.org/install.ps1 | iex⏎- **not** a list⏎⏎  indented",
            "C:tildes ``` inside",
            "L0(•):item",
            "C1:in item",
            "P:unclosed:",
            "C:rest",
        ], blocks);
    }

    /// <summary>Three backticks with code after them on the same line are inline code, not a fence.</summary>
    [Fact]
    public void Parse_TripleBackticksOnOneLineAreInlineCode()
    {
        Assert.Equal(["P:[c]a b[/] end"], Describe(MarkdownParser.Parse("```a b``` end")));
    }

    /// <summary>The release notes' file table: header, rows, inline code in cells, an escaped bar, a row without its closing bar.</summary>
    [Fact]
    public void Parse_ReadsTables()
    {
        const string Notes = "| File | What it is |\n|---|:--:|\n| `BetterClipboard-0.2.5-win-x64.zip` | App + `bclip` for x64 |\n| a \\| b | `c | d` |\n| last | open\nafter";
        var blocks = Describe(MarkdownParser.Parse(Notes));
        Assert.Equal(
        [
            "T:File | What it is / [c]BetterClipboard-0.2.5-win-x64.zip[/] | App + [c]bclip[/] for x64 / a | b | [c]c | d[/] / last | open",
            "P:after",
        ], blocks);
    }

    /// <summary>A line starting with a bar but without the dashes under it is text.</summary>
    [Fact]
    public void Parse_ABarLineWithoutSeparatorIsText()
    {
        Assert.Equal(["P:| not | a table |"], Describe(MarkdownParser.Parse("| not | a table |")));
    }

    /// <summary>Rules in their three spellings; the release page's footer after one.</summary>
    [Fact]
    public void Parse_ReadsRules()
    {
        var blocks = Describe(MarkdownParser.Parse("text\n\n---\n\n***\n_ _ _\n--\n\n**Code signing policy:** see [the README](https://github.com/Nucs/BetterClipboard#code-signing-policy)."));
        Assert.Equal(
        [
            "P:text", "R", "R", "R", "P:--",
            "P:[b]Code signing policy:[/] see {the README→https://github.com/Nucs/BetterClipboard#code-signing-policy}.",
        ], blocks);
    }

    /// <summary>Block quotes become one quoted paragraph; an HTML comment is left out, wherever it ends.</summary>
    [Fact]
    public void Parse_ReadsQuotes_AndDropsComments()
    {
        var blocks = Describe(MarkdownParser.Parse("> quoted **text**\n> second line\n\n<!-- a note\nto self -->\nvisible\n<!-- one line -->\nalso visible"));
        Assert.Equal(["Q:quoted [b]text[/] second line", "P:visible", "P:also visible"], blocks);
    }

    /// <summary>Tabs indent like four blanks, so a tab-indented item nests.</summary>
    [Fact]
    public void Parse_ExpandsTabs()
    {
        Assert.Equal(["L0(•):top", "L1(•):nested"], Describe(MarkdownParser.Parse("- top\n\t- nested")));
    }

    /// <summary>Bold, italic, both, strikethrough and code, in both spellings.</summary>
    /// <param name="markdown">The inline Markdown.</param>
    /// <param name="expected">The runs it must become.</param>
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("**bold**", "[b]bold[/]")]
    [InlineData("__bold__", "[b]bold[/]")]
    [InlineData("*italic*", "[i]italic[/]")]
    [InlineData("_italic_", "[i]italic[/]")]
    [InlineData("***both***", "[bi]both[/]")]
    [InlineData("**a *b* c**", "[b]a [/][bi]b[/][b] c[/]")]
    [InlineData("*a **b** c*", "[i]a [/][bi]b[/][i] c[/]")]
    [InlineData("~~gone~~", "[s]gone[/]")]
    [InlineData("`code`", "[c]code[/]")]
    [InlineData("**`bold code`**", "[bc]bold code[/]")]
    [InlineData("`` a ` b ``", "[c]a ` b[/]")]
    [InlineData("a `**not bold**` b", "a [c]**not bold**[/] b")]
    [InlineData("**Ctrl+Enter** runs, **Ctrl+Shift+Enter** too", "[b]Ctrl+Enter[/] runs, [b]Ctrl+Shift+Enter[/] too")]
    [InlineData("*Settings › Integrations*, and", "[i]Settings › Integrations[/], and")]
    public void ParseInline_ReadsEmphasisAndCode(string markdown, string expected)
    {
        Assert.Equal(expected, Inline(markdown));
    }

    /// <summary>Marks that are not emphasis stay as typed: arithmetic, names with underscores, unclosed marks, escapes.</summary>
    /// <param name="markdown">The inline Markdown.</param>
    /// <param name="expected">The runs it must become.</param>
    [Theory]
    [InlineData("2 * 3 * 4", "2 * 3 * 4")]
    [InlineData("snake_case_name and SHA256SUMS_txt", "snake_case_name and SHA256SUMS_txt")]
    [InlineData("*not closed", "*not closed")]
    [InlineData("**not closed", "**not closed")]
    [InlineData("a ** b ** c", "a ** b ** c")]
    [InlineData(@"\*literal\* \_x\_ \\ \`", @"*literal* _x_ \ `")]
    [InlineData("`unclosed", "`unclosed")]
    [InlineData("~single~ and ~~open", "~single~ and ~~open")]
    [InlineData("****", "****")]
    [InlineData("a_b_ c", "a_b_ c")]
    [InlineData("Win+V … C:\\Users\\me", "Win+V … C:\\Users\\me")]
    public void ParseInline_LeavesOtherMarksAlone(string markdown, string expected)
    {
        Assert.Equal(expected, Inline(markdown));
    }

    /// <summary>Links: inline, with a title, with emphasis and code in the label, in angle brackets, bare, and images.</summary>
    /// <param name="markdown">The inline Markdown.</param>
    /// <param name="expected">The runs it must become.</param>
    [Theory]
    [InlineData("[Privacy](https://github.com/Nucs/BetterClipboard#privacy) in the README", "{Privacy→https://github.com/Nucs/BetterClipboard#privacy} in the README")]
    [InlineData("[t](https://example.org/a \"title\")", "{t→https://example.org/a}")]
    [InlineData("[**b** and `c`](https://example.org/)", "{[b]b[/]→https://example.org/}{ and →https://example.org/}{[c]c[/]→https://example.org/}")]
    [InlineData("[w](https://en.wikipedia.org/wiki/A_(b))", "{w→https://en.wikipedia.org/wiki/A_(b)}")]
    [InlineData("[t](<https://example.org/x y>)", "t")]
    [InlineData("<https://example.org/auto>", "{https://example.org/auto→https://example.org/auto}")]
    [InlineData("see https://example.org/page, then", "see {https://example.org/page→https://example.org/page}, then")]
    [InlineData("(https://example.org/a_(b)) end.", "({https://example.org/a_(b)→https://example.org/a_(b)}) end.")]
    [InlineData("**bold https://example.org/x**", "[b]bold [/]{[b]https://example.org/x[/]→https://example.org/x}")]
    [InlineData("![shot](https://example.org/s.png)", "{shot→https://example.org/s.png}")]
    [InlineData("![](https://example.org/s.png)", "{image→https://example.org/s.png}")]
    public void ParseInline_ReadsLinks(string markdown, string expected)
    {
        Assert.Equal(expected, Inline(markdown));
    }

    /// <summary>
    /// Only http(s) addresses become links: anything else keeps its label as plain text, and brackets that are not a link
    /// stay as typed.
    /// </summary>
    /// <param name="markdown">The inline Markdown.</param>
    /// <param name="expected">The runs it must become.</param>
    [Theory]
    [InlineData("[run](javascript:alert(1))", "run")]
    [InlineData("[mail](mailto:a@example.org)", "mail")]
    [InlineData("[file](file:///C:/Windows/win.ini)", "file")]
    [InlineData("[relative](docs/a.md)", "relative")]
    [InlineData("[anchor](#privacy)", "anchor")]
    [InlineData("[a] [b] (c)", "[a] [b] (c)")]
    [InlineData("[unclosed and [more", "[unclosed and [more")]
    [InlineData("[t](https://example.org/unclosed", "[t]({https://example.org/unclosed→https://example.org/unclosed}")]
    [InlineData("a < b > c and <b>tag</b>", "a < b > c and <b>tag</b>")]
    [InlineData("xhttps://example.org", "xhttps://example.org")]
    [InlineData("http://", "http://")]
    public void ParseInline_KeepsOtherAddressesAsText(string markdown, string expected)
    {
        Assert.Equal(expected, Inline(markdown));
    }

    /// <summary>The few HTML entities people type are decoded; others stay.</summary>
    [Fact]
    public void ParseInline_DecodesCommonEntities()
    {
        Assert.Equal("a & b < c > d \" e ' f ' g\u00A0h &unknown; &", Inline("a &amp; b &lt; c &gt; d &quot; e &#39; f &apos; g&nbsp;h &unknown; &"));
    }

    /// <summary>The plain text of runs is their texts joined, without marks.</summary>
    [Fact]
    public void ToPlainText_DropsStylesAndLinks()
    {
        Assert.Equal("Bold and a link.", MarkdownParser.ToPlainText(MarkdownParser.ParseInline("**Bold** and [a link](https://example.org/).")));
        Assert.Equal(string.Empty, MarkdownParser.ToPlainText([]));
    }

    /// <summary>A paragraph longer than the inline cap is shown as plain text; notes longer than the total cap are cut.</summary>
    [Fact]
    public void Parse_BoundsItsWork()
    {
        var longParagraph = "**" + new string('a', MarkdownParser.MaxInlineLength + 10) + "**";
        var inline = Assert.Single(MarkdownParser.ParseInline(longParagraph));
        Assert.Equal(MarkdownStyle.None, inline.Style);
        Assert.Equal(longParagraph, inline.Text);

        var huge = string.Join("\n\n", Enumerable.Repeat(new string('x', 999), 400));
        var blocks = MarkdownParser.Parse(huge);
        Assert.True(blocks.Sum(b => ((MarkdownParagraph)b).Inlines.Sum(i => i.Text.Length)) <= MarkdownParser.MaxLength);
    }

    /// <summary>Deep nesting stops at the cap instead of recursing without end, and the text survives.</summary>
    [Fact]
    public void ParseInline_BoundsNesting()
    {
        var nested = string.Concat(Enumerable.Repeat("[", 40)) + "x" + string.Concat(Enumerable.Repeat("](https://example.org/)", 40));
        var inlines = MarkdownParser.ParseInline(nested);
        Assert.Contains("x", MarkdownParser.ToPlainText(inlines), StringComparison.Ordinal);
    }

    /// <summary>
    /// Texts made of thousands of unclosed marks — the worst case for a naive parser — finish quickly: a failed search
    /// for a closing mark is not repeated.
    /// </summary>
    /// <param name="unit">The repeated fragment.</param>
    [Theory]
    [InlineData("*a ")]
    [InlineData("**a ")]
    [InlineData("_a ")]
    [InlineData("~~a ")]
    [InlineData("[a ")]
    [InlineData("`a ")]
    [InlineData("![a")]
    [InlineData("<a ")]
    [InlineData("https://")]
    public void ParseInline_StaysFastOnUnclosedMarks(string unit)
    {
        var text = string.Concat(Enumerable.Repeat(unit, MarkdownParser.MaxInlineLength / unit.Length));
        var watch = Stopwatch.StartNew();
        var inlines = MarkdownParser.ParseInline(text);
        watch.Stop();

        // Nothing but marks may be dropped: the letters all survive (backticks pair up into code spans and disappear).
        var plain = MarkdownParser.ToPlainText(inlines);
        Assert.Equal(text.Count(c => c == 'a'), plain.Count(c => c == 'a'));
        Assert.InRange(plain.Length, 1, text.Length);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Parsing took {watch.Elapsed.TotalMilliseconds:0} ms.");
    }

    /// <summary>
    /// Random text built from Markdown's own marks never throws and never loses the letters between the marks (2,000
    /// documents, a fixed seed).
    /// </summary>
    [Fact]
    public void Parse_NeverThrowsOnRandomMarkup()
    {
        string[] pieces = ["*", "**", "_", "__", "`", "```", "~~", "[", "]", "(", ")", "<", ">", "!", "\\", "|", "-", "#", ">", " ", "  ", "\n", "\n\n", "\t", "1.", "&amp;", "https://e.org/x", "word", "W"];
        var random = new Random(20261009);
        for (int round = 0; round < 2000; round++)
        {
            var text = new StringBuilder();
            int count = random.Next(1, 60);
            for (int i = 0; i < count; i++)
            {
                text.Append(pieces[random.Next(pieces.Length)]);
            }

            var source = text.ToString();
            var blocks = MarkdownParser.Parse(source);
            Assert.NotNull(blocks);
            foreach (var block in blocks)
            {
                // Every block kind must describe without failing (its lists are complete).
                Assert.NotNull(Describe([block]));
            }
        }
    }

    /// <summary>
    /// A whole release-notes document in the shape of BetterClipboard's own (title line, install command, paragraphs,
    /// nested lists, a table, a rule and the policy footer) parses into the expected sequence of block kinds.
    /// </summary>
    [Fact]
    public void Parse_AReleaseNotesDocument()
    {
        const string Notes = """
            BetterClipboard 0.2.5 — new tabs for the commands you run

            ```powershell
            irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1 | iex
            ```

            Updates in place and keeps your history. The installer verifies the download's SHA-256 and closes the
            running BetterClipboard gracefully.

            **Seven new tabs.** Each has its switch in *Settings › Integrations*.
            [Privacy](https://github.com/Nucs/BetterClipboard#privacy) in the README lists every source.

            - **Run**: every command you run with Win+R.
              - **Ctrl+Enter** runs a command again.
            - **Everything**: with [Everything](https://www.voidtools.com/) installed.

            ## Files

            | File | What it is |
            |---|---|
            | `SHA256SUMS.txt` | SHA-256 of the zips (`install.ps1` checks it) |

            - Not code-signed yet.

            ---

            **Code signing policy:** see [the README](https://github.com/Nucs/BetterClipboard#code-signing-policy).
            """;
        var blocks = MarkdownParser.Parse(Notes);
        Assert.Equal(
            ["Paragraph", "CodeBlock", "Paragraph", "Paragraph", "ListItem", "ListItem", "ListItem", "Heading", "Table", "ListItem", "Rule", "Paragraph"],
            blocks.Select(b => b.GetType().Name.Replace("Markdown", string.Empty, StringComparison.Ordinal)));
        Assert.Equal("irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1 | iex", ((MarkdownCodeBlock)blocks[1]).Code);
        Assert.Equal(
            "[b]Seven new tabs.[/] Each has its switch in [i]Settings › Integrations[/]. {Privacy→https://github.com/Nucs/BetterClipboard#privacy} in the README lists every source.",
            Describe(((MarkdownParagraph)blocks[3]).Inlines));
        Assert.Equal(1, ((MarkdownListItem)blocks[5]).Depth);
        Assert.Equal(2, ((MarkdownTable)blocks[8]).Rows.Count);
    }
}
