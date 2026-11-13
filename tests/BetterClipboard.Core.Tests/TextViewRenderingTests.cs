using System.Globalization;
using System.Text;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.TextView;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the text viewer's preparation (<see cref="TextViewSource"/>, <see cref="TextViewRendering"/>), its RTF
/// (<see cref="RtfWriter"/>), and the Markdown parser's additions it relies on (kept line breaks, fence info).
/// </summary>
public sealed class TextViewRenderingTests
{
    /// <summary>The style the tests write with.</summary>
    private static readonly TextViewStyle Style = TextViewStyle.Default("Cascadia Mono");

    /// <summary>
    /// Reads the text back out of an RTF document the way RichEdit would: control symbols decoded, <c>\par</c> and
    /// <c>\line</c> as line breaks, <c>\tab</c> as a tab, <c>\uN?</c> as its character, the font and color tables skipped.
    /// </summary>
    /// <param name="rtf">The document.</param>
    /// <returns>Its text.</returns>
    internal static string RtfText(string rtf)
    {
        var text = new StringBuilder();
        int skipDepth = 0;
        int depth = 0;
        for (int i = 0; i < rtf.Length; i++)
        {
            char c = rtf[i];
            if (c == '{')
            {
                depth++;
                continue;
            }

            if (c == '}')
            {
                if (skipDepth == depth)
                {
                    skipDepth = 0;
                }

                depth--;
                continue;
            }

            if (c is '\r' or '\n')
            {
                continue;
            }

            if (c != '\\')
            {
                if (skipDepth == 0)
                {
                    text.Append(c);
                }

                continue;
            }

            char next = rtf[i + 1];
            if (next is '\\' or '{' or '}')
            {
                if (skipDepth == 0)
                {
                    text.Append(next);
                }

                i++;
                continue;
            }

            int wordStart = i + 1;
            int j = wordStart;
            while (j < rtf.Length && char.IsAsciiLetterLower(rtf[j]))
            {
                j++;
            }

            var word = rtf[wordStart..j];
            int numberStart = j;
            if (j < rtf.Length && rtf[j] == '-')
            {
                j++;
            }

            while (j < rtf.Length && char.IsAsciiDigit(rtf[j]))
            {
                j++;
            }

            var number = rtf[numberStart..j];
            if (j < rtf.Length && rtf[j] == ' ')
            {
                j++;
            }

            i = j - 1;
            if (word is "fonttbl" or "colortbl")
            {
                skipDepth = depth;
                continue;
            }

            if (skipDepth != 0)
            {
                continue;
            }

            switch (word)
            {
                case "par" or "line":
                    text.Append('\n');
                    break;
                case "tab":
                    text.Append('\t');
                    break;
                case "u":
                    text.Append((char)(ushort)short.Parse(number, CultureInfo.InvariantCulture));
                    i++; // the fallback character
                    break;
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// A code document holds the text exactly: every character comes back out of the RTF, escapes, tabs, Unicode beyond
    /// the BMP and RTF's own special characters included — so RichEdit positions equal text positions (the gutter's rule).
    /// </summary>
    [Fact]
    public void Code_RoundTripsEveryCharacter()
    {
        const string Text = "if (a) { b = \"c\\d\"; }\n\tשלום 😀 \u202Ex\n{}\\ end";
        var tokens = SyntaxHighlighter.Tokenize(Text, TextLanguage.CSharp);
        var rtf = RtfWriter.Code(Text, tokens, SyntaxPalette.Dark, Style);
        Assert.StartsWith(@"{\rtf1", rtf, StringComparison.Ordinal);
        Assert.Equal(Text, RtfText(rtf));
        Assert.Contains(@"\cf", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\fs20", rtf, StringComparison.Ordinal); // 13 DIPs = 9.75 pt = 19.5 half-points, rounded away from zero
    }

    /// <summary>Control characters become their visible pictures, one for one, so positions still line up.</summary>
    [Fact]
    public void Code_ControlCharactersBecomePictures()
    {
        var rtf = RtfWriter.Code("a\u0001b\u007F", [], SyntaxPalette.Light, Style);
        Assert.Equal("a␁b␡", RtfText(rtf));
    }

    /// <summary>The color table holds the palette, and a token switches to its kind's color and back.</summary>
    [Fact]
    public void Code_ColorsTokensFromThePalette()
    {
        var rtf = RtfWriter.Code("int x;", SyntaxHighlighter.Tokenize("int x;", TextLanguage.CSharp), SyntaxPalette.Dark, Style);
        Assert.Contains(@"\red212\green212\blue212;", rtf, StringComparison.Ordinal); // Dark+ text, D4D4D4
        Assert.Contains(@"\red78\green201\blue176;", rtf, StringComparison.Ordinal);  // Dark+ type, 4EC9B0
    }

    /// <summary>A Markdown document draws its blocks: heading text, list markers, code lines, a table grid, a rule.</summary>
    [Fact]
    public void Markdown_DrawsEveryBlock()
    {
        const string Markdown = "# Plan\n\nShip **Friday**.\nSecond line kept.\n\n- one\n- [x] done\n\n```xml\n<a>1</a>\n```\n\n| A | Bee |\n|---|---|\n| 1 | 2 |\n\n---\n> quoted";
        var source = TextViewSource.Create(Markdown);
        var view = TextViewRendering.Create(source, TextLanguage.Markdown, formatted: true);
        Assert.True(view.IsDocument);
        var text = RtfText(view.ToRtf(SyntaxPalette.Dark, Style));
        Assert.Contains("Plan\n", text, StringComparison.Ordinal);
        Assert.Contains("Ship Friday.\nSecond line kept.", text, StringComparison.Ordinal); // the break the author typed stays
        Assert.Contains("•\tone", text, StringComparison.Ordinal);
        Assert.Contains("☑\tdone", text, StringComparison.Ordinal);
        Assert.Contains("<a>1</a>", text, StringComparison.Ordinal);
        Assert.Contains("A  Bee\n─  ───\n1  2", text, StringComparison.Ordinal);
        Assert.Contains(new string('─', 32), text, StringComparison.Ordinal);
        Assert.Contains("quoted", text, StringComparison.Ordinal);

        // The fenced block is colored as the language its fence names.
        var code = view.Blocks.OfType<MarkdownCodeBlock>().Single();
        Assert.Equal("xml", code.Info);
        Assert.Contains(view.TokensOf(code), t => t.Kind == TokenKind.Tag);
    }

    /// <summary>
    /// The owner's case: an XML prompt is shown as XML, colored; it is already tidy, so there is no formatted alternative
    /// and the switch can hide. Minified XML re-indents, and as copied shows it as it was.
    /// </summary>
    [Fact]
    public void Xml_ViewsAndTheSwitch()
    {
        var tidy = TextViewSource.Create(TextLanguageDetectorTests.HeartbeatXml);
        Assert.Equal(TextLanguage.Xml, tidy.DetectedLanguage);
        var tidyView = TextViewRendering.Create(tidy, tidy.DetectedLanguage, formatted: true);
        Assert.False(tidyView.IsDocument);
        Assert.False(tidyView.HasFormattedAlternative);
        Assert.Equal(TextLanguageDetectorTests.HeartbeatXml, tidyView.DisplayText);
        Assert.Contains(tidyView.Tokens, t => t.Kind == TokenKind.Tag);

        var minified = TextViewSource.Create("<r><a>1</a><b>2</b></r>");
        var formatted = TextViewRendering.Create(minified, TextLanguage.Xml, formatted: true);
        Assert.True(formatted.IsReindented);
        Assert.True(formatted.HasFormattedAlternative);
        Assert.Equal("<r>\n  <a>1</a>\n  <b>2</b>\n</r>", formatted.DisplayText);
        Assert.Equal([0, 4, 15, 26], formatted.LineStarts);

        var asCopied = TextViewRendering.Create(minified, TextLanguage.Xml, formatted: false);
        Assert.False(asCopied.IsReindented);
        Assert.True(asCopied.HasFormattedAlternative);
        Assert.Equal("<r><a>1</a><b>2</b></r>", asCopied.DisplayText);
    }

    /// <summary>Markdown as copied is the plain source in the code font; another code language has nothing to switch.</summary>
    [Fact]
    public void AsCopied_AndLanguagesWithoutAlternative()
    {
        var source = TextViewSource.Create("# x\n\n*y*");
        var raw = TextViewRendering.Create(source, TextLanguage.Markdown, formatted: false);
        Assert.False(raw.IsDocument);
        Assert.True(raw.HasFormattedAlternative);
        Assert.Empty(raw.Tokens);
        Assert.Equal("# x\n\n*y*", RtfText(raw.ToRtf(SyntaxPalette.Light, Style)));

        var csharp = TextViewRendering.Create(TextViewSource.Create("int x = 1;"), TextLanguage.CSharp, formatted: true);
        Assert.False(csharp.HasFormattedAlternative);
        Assert.NotEmpty(csharp.Tokens);
    }

    /// <summary>A text too long to draw as Markdown is shown as plain text, and the view says it was limited.</summary>
    [Fact]
    public void Markdown_TooLongIsPlain()
    {
        var source = TextViewSource.Create(string.Concat(Enumerable.Repeat("A line of prose that goes on.\n", (TextViewRendering.MarkdownMaxChars / 30) + 10)));
        var view = TextViewRendering.Create(source, TextLanguage.Markdown, formatted: true);
        Assert.False(view.IsDocument);
        Assert.True(view.IsLimited);
        Assert.False(view.HasFormattedAlternative);
    }

    /// <summary>Line breaks are normalized (CRLF, CR, U+2028, U+2029), a byte order mark goes, and a clean text is kept as is.</summary>
    [Fact]
    public void Source_NormalizesLineBreaks()
    {
        Assert.Equal("a\nb\nc\nd\ne", TextViewSource.Normalize("\uFEFFa\r\nb\rc\u2028d\u2029e"));
        const string Clean = "a\nb";
        Assert.Same(Clean, TextViewSource.Normalize(Clean));
    }

    /// <summary>A cut ends at a line break when one is near, never inside a surrogate pair, and the source says it was cut.</summary>
    [Fact]
    public void Source_CutsCleanly()
    {
        // The cut looks for a line break in its last tenth: 10 characters of 100 here.
        var lines = new string('a', 95) + "\n" + new string('b', 20);
        Assert.Equal(new string('a', 95), TextViewSource.Cut(lines, 100));
        Assert.Equal(new string('a', 30), TextViewSource.Cut(new string('a', 40), 30));
        var emoji = new string('x', 9) + "😀";
        Assert.Equal(new string('x', 9), TextViewSource.Cut(emoji, 10));

        var source = TextViewSource.Create(string.Concat(Enumerable.Repeat("line\n", 1000)), maxChars: 100);
        Assert.True(source.IsCut);
        Assert.Equal(1001, source.OriginalLineCount);
        Assert.True(source.Text.Length <= 100);
        Assert.Null(source.Reindented(TextLanguage.Json)); // a cut text is never re-indented
    }

    /// <summary>The Markdown parser keeps typed line breaks only when asked, and keeps a fence's info string.</summary>
    [Fact]
    public void MarkdownParser_KeepLineBreaksAndFenceInfo()
    {
        var joined = (MarkdownParagraph)MarkdownParser.Parse("one\ntwo")[0];
        Assert.Equal("one two", MarkdownParser.ToPlainText(joined.Inlines));
        var kept = (MarkdownParagraph)MarkdownParser.Parse("one\ntwo", new MarkdownParseOptions(KeepLineBreaks: true))[0];
        Assert.Equal("one\ntwo", MarkdownParser.ToPlainText(kept.Inlines));

        var fence = (MarkdownCodeBlock)MarkdownParser.Parse("``` js title=app.js\nx\n```")[0];
        Assert.Equal("js title=app.js", fence.Info);
        Assert.Equal(TextLanguage.JavaScript, TextLanguages.FromFenceInfo(fence.Info));
        Assert.Null(((MarkdownCodeBlock)MarkdownParser.Parse("```\nx\n```")[0]).Info);
    }

    /// <summary>Line starts: one per line, an empty last line included.</summary>
    [Fact]
    public void LineStarts_OnePerLine()
    {
        Assert.Equal([0], TextViewRendering.LineStartsOf(string.Empty));
        Assert.Equal([0, 2, 3], TextViewRendering.LineStartsOf("a\n\n"));
    }

    /// <summary>Every language in the picker has a name, and only Markdown is drawn as a document.</summary>
    [Fact]
    public void Languages_NamesAndKinds()
    {
        Assert.Equal(Enum.GetValues<TextLanguage>().Length, TextLanguages.All.Count);
        Assert.All(TextLanguages.All, language => Assert.False(string.IsNullOrWhiteSpace(TextLanguages.NameOf(language))));
        Assert.False(TextLanguages.IsCode(TextLanguage.Markdown));
        Assert.True(TextLanguages.IsCode(TextLanguage.PlainText));
        Assert.Equal(TextLanguage.CSharp, TextLanguages.FromFenceInfo("C#"));
        Assert.Null(TextLanguages.FromFenceInfo("klingon"));
    }

    /// <summary>
    /// Pages end after a line break, never hold more than the limit, follow each other without gaps or overlaps, cover the
    /// whole text, and know their first line.
    /// </summary>
    [Fact]
    public void Pages_CutAtLineBreaks()
    {
        // A cut at the limit would end the first page inside "bbbbbb".
        const string text = "aaaa\nbbbbbb\ncc\nddd";
        var pages = TextViewRendering.PagesOf(text, TextViewRendering.LineStartsOf(text), maxChars: 10);

        Assert.Equal(["aaaa\n", "bbbbbb\ncc\n", "ddd"], pages.Select(page => text.Substring(page.Start, page.Length)));
        Assert.Equal([0, 1, 3], pages.Select(page => page.FirstLine));
        Assert.Equal([0, 1, 2], pages.Select(page => page.Index));
        AssertTiles(text, pages, maxChars: 10);
    }

    /// <summary>
    /// A line longer than a page is cut at the limit (its next part begins the next page, on the same line), never between
    /// the two halves of a surrogate pair; a short text and an empty one are one page.
    /// </summary>
    [Fact]
    public void Pages_LongLinesShortAndEmptyTexts()
    {
        string longLine = new string('x', 25) + "\nend";
        var pages = TextViewRendering.PagesOf(longLine, TextViewRendering.LineStartsOf(longLine), maxChars: 10);
        Assert.Equal([0, 0, 0], pages.Select(page => page.FirstLine));
        Assert.Equal("xxxxx\nend", longLine.Substring(pages[^1].Start, pages[^1].Length));
        AssertTiles(longLine, pages, maxChars: 10);

        // An emoji (two UTF-16 units) across the limit stays whole.
        string pairs = "abcdefghi\U0001F600tail";
        var pairPages = TextViewRendering.PagesOf(pairs, TextViewRendering.LineStartsOf(pairs), maxChars: 10);
        Assert.Equal(9, pairPages[0].Length);
        AssertTiles(pairs, pairPages, maxChars: 10);

        Assert.Equal([new TextViewPage(0, 0, 3, 0)], TextViewRendering.PagesOf("abc", [0], maxChars: 10));
        Assert.Equal([new TextViewPage(0, 0, 0, 0)], TextViewRendering.PagesOf(string.Empty, [0], maxChars: 10));
    }

    /// <summary>
    /// A long code document has pages of at most <see cref="TextViewRendering.PageMaxChars"/>; each page's RTF holds exactly
    /// that page's text, its tokens are the document's tokens cut at the page's edges and moved to its own positions, and a
    /// position finds its page. A Markdown document is one page.
    /// </summary>
    [Fact]
    public void Pages_OfACodeDocument()
    {
        var code = new StringBuilder();
        for (int i = 0; code.Length < (TextViewRendering.PageMaxChars * 2) + 500; i++)
        {
            code.Append(CultureInfo.InvariantCulture, $"var name{i} = \"BC-TEST {i}\"; // line {i}\n");
        }

        // A block comment over two lines, starting at a line start shortly before the first page's limit: its first line ends
        // inside the page and its second runs past the limit, so the page ends inside the comment.
        string full = code.ToString();
        int at = full.LastIndexOf('\n', TextViewRendering.PageMaxChars - 60) + 1;
        string text = full.Insert(at, "/* " + new string('s', 30) + "\n" + new string('t', 100) + " */\n");
        var view = TextViewRendering.Create(TextViewSource.Create(text), TextLanguage.CSharp, formatted: true);

        Assert.Equal(3, view.Pages.Count);
        AssertTiles(view.DisplayText, view.Pages, TextViewRendering.PageMaxChars);
        var rebuilt = new List<SyntaxToken>();
        foreach (var page in view.Pages)
        {
            Assert.Equal(view.PageText(page), RtfText(view.ToRtf(page, SyntaxPalette.Dark, Style)));
            Assert.Equal(page.Index, view.PageOf(page.Start));
            Assert.Equal(page.Index, view.PageOf(page.End - 1));
            Assert.Equal(TextViewRendering.LineOf(view.LineStarts, page.Start), page.FirstLine);
            Assert.True(page.End == view.DisplayText.Length || view.DisplayText[page.End - 1] == '\n', "a page ends after a line break");
            foreach (var token in view.PageTokens(page))
            {
                Assert.InRange(token.Start, 0, page.Length - 1);
                Assert.InRange(token.End, 1, page.Length);
                rebuilt.Add(token with { Start = token.Start + page.Start });
            }
        }

        // The page tokens put back together are the document's tokens, cut at the pages' edges and nothing else.
        var expected = new List<SyntaxToken>();
        foreach (var token in view.Tokens)
        {
            int start = token.Start;
            foreach (var page in view.Pages.Where(page => page.Start < token.End && token.Start < page.End))
            {
                int end = Math.Min(token.End, page.End);
                expected.Add(new SyntaxToken(start, end - start, token.Kind));
                start = end;
            }
        }

        Assert.Equal(expected, rebuilt);
        Assert.Contains(rebuilt, token => token.Kind == TokenKind.Comment && token.End == view.Pages[0].End);
        Assert.Equal(view.Pages.Count - 1, view.PageOf(view.DisplayText.Length));

        var document = TextViewRendering.Create(TextViewSource.Create("# BC-TEST\n\ntext"), TextLanguage.Markdown, formatted: true);
        Assert.True(document.IsDocument);
        Assert.Equal([new TextViewPage(0, 0, document.DisplayText.Length, 0)], document.Pages);
        Assert.Equal(document.ToRtf(SyntaxPalette.Light, Style), document.ToRtf(document.Pages[0], SyntaxPalette.Light, Style));
    }

    /// <summary>The line of a position: the last line start at or before it.</summary>
    [Fact]
    public void LineOf_FindsTheLine()
    {
        int[] starts = [0, 5, 6, 12];
        Assert.Equal([0, 0, 1, 2, 2, 3, 3], new[] { 0, 4, 5, 6, 11, 12, 99 }.Select(position => TextViewRendering.LineOf(starts, position)));
        Assert.Equal(0, TextViewRendering.LineOf([0], 7));
        Assert.Throws<ArgumentNullException>(() => TextViewRendering.LineOf(null!, 0));
    }

    /// <summary>Checks that pages tile a text: in order, contiguous, none empty unless the text is, none over the limit.</summary>
    /// <param name="text">The text.</param>
    /// <param name="pages">Its pages.</param>
    /// <param name="maxChars">The page limit.</param>
    private static void AssertTiles(string text, IReadOnlyList<TextViewPage> pages, int maxChars)
    {
        Assert.NotEmpty(pages);
        int next = 0;
        foreach (var page in pages)
        {
            Assert.Equal(next, page.Start);
            Assert.InRange(page.Length, text.Length == 0 ? 0 : 1, maxChars);
            next = page.End;
        }

        Assert.Equal(text.Length, next);
    }

    /// <summary>The text size scales every size and stays within its bounds.</summary>
    [Fact]
    public void Style_ScalesWithinBounds()
    {
        Assert.Equal(28, Style.Scaled(2).ProseSize);
        Assert.Equal(Style.ProseSize * TextViewStyle.MaxScale, Style.Scaled(10).ProseSize);
        Assert.Equal(Style.ProseSize * TextViewStyle.MinScale, Style.Scaled(0.01).ProseSize);
        Assert.Equal(Style.ProseSize, Style.Scaled(double.NaN).ProseSize);
    }
}
