using System.Text;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Updates;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for <see cref="ReleaseNotesLanguages"/>: the translations a release body hides are found exactly where the
/// update dialog's parser hides a comment, the English everyone sees never changes, and a click on a release's language
/// line is told apart from an ordinary link (CLAUDE.md §2.67).
/// </summary>
public sealed class ReleaseNotesLanguagesTests
{
    /// <summary>The release page of the sample release, as GitHub's API names it (<c>html_url</c>).</summary>
    private static readonly Uri ReleasePage = new("https://github.com/Nucs/BetterClipboard/releases/tag/v9.9.9");

    /// <summary>The Chinese page of the sample release: the translation as a file in the repository at the release's tag.</summary>
    private static readonly Uri ChinesePage = new("https://github.com/Nucs/BetterClipboard/blob/v9.9.9/docs/release-notes/9.9.9/zh-Hans.md");

    /// <summary>The English part of the sample, as the composer writes it: the language line, the notes, the footer.</summary>
    private static readonly string EnglishPart = string.Join('\n',
        $"**ENG** / [简体中文]({ChinesePage})",
        string.Empty,
        "BetterClipboard 9.9.9",
        string.Empty,
        "```powershell",
        "irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1 | iex",
        "```",
        string.Empty,
        "**A new tab.** It lists what you asked for.",
        "- One detail.",
        string.Empty,
        "**Fixed**",
        "- A thing that was broken.",
        string.Empty,
        "---",
        string.Empty,
        "**Code signing policy:** see [the README](https://github.com/Nucs/BetterClipboard#code-signing-policy).");

    /// <summary>The Chinese translation as the block holds it: its own language line first, then the notes and the footer.</summary>
    private static readonly string ChinesePart = string.Join('\n',
        $"[ENG]({ReleasePage}) / **简体中文**",
        string.Empty,
        "BetterClipboard 9.9.9",
        string.Empty,
        "```powershell",
        "irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1 | iex",
        "```",
        string.Empty,
        "**新标签页。** 列出你要的内容。",
        "- 一个细节。",
        string.Empty,
        "**修复**",
        "- 修好了一处问题。",
        string.Empty,
        "---",
        string.Empty,
        "**代码签名政策：** 请参阅 [README](https://github.com/Nucs/BetterClipboard/blob/main/docs/translations/README.zh-CN.md#code-signing-policy)。");

    /// <summary>The whole sample body: the English part, then the Chinese one in its hidden block, as the release job composes it.</summary>
    private static readonly string Body = EnglishPart + "\n\n<!-- release-notes zh-Hans\n" + ChinesePart + "\n-->\n";

    /// <summary>
    /// Spans that are no translation, each with why. Whatever the parser hides stays hidden; only a well-formed block with
    /// a valid tag and some text is offered as a language.
    /// </summary>
    public static TheoryData<string, string> NotTranslations => new()
    {
        { "not closed", "<!-- release-notes zh-Hans\n中文\n" },
        { "ended early by a \"-->\" inside a line", "<!-- release-notes zh-Hans\n中文 --> 更多\n-->\n" },
        { "closed on a line that holds text, which the block would lose", "<!-- release-notes zh-Hans\n中文\n更多 -->\n" },
        { "a comment of one line", "<!-- release-notes zh-Hans -->\n" },
        { "a tag with an underscore", "<!-- release-notes zh_Hans\n中文\n-->\n" },
        { "no tag", "<!-- release-notes\n中文\n-->\n" },
        { "another marker", "<!-- notes zh-Hans\n中文\n-->\n" },
        { "nothing in it", "<!-- release-notes zh-Hans\n\n\n-->\n" },
        { "English, which is the visible body", "<!-- release-notes en\nEnglish\n-->\n" },
        { "inside a code fence", "```\n<!-- release-notes zh-Hans\n中文\n-->\n```\n" },
        { "inside a tilde fence", "~~~~\n<!-- release-notes zh-Hans\n中文\n-->\n~~~~\n" },
        { "inside a block quote", "> <!-- release-notes zh-Hans\n> 中文\n> -->\n" },
        { "the marker in a paragraph's text", "Text <!-- release-notes zh-Hans\n中文\n-->\n" },
    };

    /// <summary>Links and what a click on them does in the sample release: switch to a language, or open (null).</summary>
    public static TheoryData<string, string?> Links => new()
    {
        { "https://github.com/Nucs/BetterClipboard/releases/tag/v9.9.9", "en" },
        { "https://github.com/Nucs/BetterClipboard/releases/tag/v9.9.9/", "en" },
        { "https://GITHUB.com/nucs/betterclipboard/releases/tag/V9.9.9", "en" },
        { "https://github.com/Nucs/BetterClipboard/releases/tag/v9.9.9?x=1#top", "en" },
        { "https://github.com/Nucs/BetterClipboard/blob/v9.9.9/docs/release-notes/9.9.9/zh-Hans.md", "zh-Hans" },
        { "https://github.com/Nucs/BetterClipboard/blob/main/docs/release-notes/9.9.9/ZH-HANS.md", "zh-Hans" },
        { "https://github.com/Nucs/BetterClipboard/blob/v9.9.9/docs/release-notes/9.9.9/ru.md", null },
        { "https://github.com/Nucs/BetterClipboard/releases/tag/v9.9.8", null },
        { "https://github.com/Nucs/BetterClipboard#code-signing-policy", null },
        { "https://github.com/Nucs/BetterClipboard/releases/download/v9.9.9/SHA256SUMS.txt", null },
        { "https://example.com/Nucs/BetterClipboard/blob/v9.9.9/docs/release-notes/9.9.9/zh-Hans.md", null },
        { "https://github.com/Nucs/BetterClipboard/raw/v9.9.9/docs/release-notes/9.9.9/zh-Hans.md", null },
        { "https://github.com/Nucs/BetterClipboard/blob/v9.9.9/docs/release-notes/zh-Hans.md", null },
    };

    /// <summary>A body without blocks (every release before 0.2.12) is its English alone, with nothing to switch to.</summary>
    [Fact]
    public void Split_ABodyWithoutTranslations_IsTheEnglishAlone()
    {
        var variants = ReleaseNotesLanguages.Split(EnglishPart);

        Assert.Same(EnglishPart, variants.English);
        Assert.Empty(variants.Translations);
        Assert.Empty(variants.Pages);
        Assert.False(variants.HasTranslations);
    }

    /// <summary>A release without notes (the feed gave none, or an empty body) is an empty English.</summary>
    [Fact]
    public void Split_NothingIsAnEmptyEnglish()
    {
        foreach (var notes in new[] { null, string.Empty })
        {
            var variants = ReleaseNotesLanguages.Split(notes);
            Assert.Equal(string.Empty, variants.English);
            Assert.False(variants.HasTranslations);
        }
    }

    /// <summary>
    /// The composed body gives back the block's text exactly (its language line, notes and footer) under its tag, looked
    /// up in any case, and the page the English's language line links to.
    /// </summary>
    [Fact]
    public void Split_TheComposedBody_GivesTheTranslationAndItsPage()
    {
        var variants = ReleaseNotesLanguages.Split(Body);

        Assert.Same(Body, variants.English);
        Assert.True(variants.HasTranslations);
        Assert.Equal(ChinesePart, Assert.Single(variants.Translations, pair => pair.Key == "zh-Hans").Value);
        Assert.Equal(ChinesePage, variants.Pages["zh-Hans"]);

        // Looked up the way the dialog asks: by the interface language's tag, in any case.
        Assert.True(variants.Translations.ContainsKey("ZH-HANS"));
    }

    /// <summary>
    /// The English the dialog draws of the composed body is exactly the English part, the block hidden, as every
    /// installed copy draws it; the translation draws as a document of its own that starts with its language line.
    /// </summary>
    [Fact]
    public void Split_LeavesTheEnglishTheParserDraws_AsBefore()
    {
        Assert.Equal(Describe(MarkdownParser.Parse(EnglishPart)), Describe(MarkdownParser.Parse(Body)));
        Assert.Equal(Describe(MarkdownParser.Parse(EnglishPart)), Describe(MarkdownParser.Parse(ReleaseNotesLanguages.Split(Body).English)));

        var chinese = MarkdownParser.Parse(ReleaseNotesLanguages.TextFor(ReleaseNotesLanguages.Split(Body), "zh-Hans"));
        Assert.Equal($"P:{{ENG→{ReleasePage.AbsoluteUri}}} / [b]简体中文[/]", Describe(chinese)[0]);
    }

    /// <summary>A body with Windows line ends gives the same translation, with plain line feeds.</summary>
    [Fact]
    public void Split_WindowsLineEnds_ReadTheSame()
    {
        var variants = ReleaseNotesLanguages.Split(Body.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal(ChinesePart, variants.Translations["zh-Hans"]);
    }

    /// <summary>Only a well-formed block is a translation; the body stays the English whatever the span holds.</summary>
    /// <param name="why">What is wrong with the span, for the failure message.</param>
    /// <param name="block">The span after the English part.</param>
    [Theory]
    [MemberData(nameof(NotTranslations))]
    public void Split_OnlyAWellFormedBlockIsATranslation(string why, string block)
    {
        var body = EnglishPart + "\n\n" + block;

        var variants = ReleaseNotesLanguages.Split(body);

        Assert.False(variants.HasTranslations, why);
        Assert.Same(body, variants.English);
    }

    /// <summary>
    /// The parser trims a line before it looks for <c>&lt;!--</c> and ends a comment at any line containing
    /// <c>--&gt;</c>: an indented marker and a closing line with blanks around it are the same block for both.
    /// </summary>
    [Fact]
    public void Split_AcceptsWhatTheParserAccepts_AroundTheMarkers()
    {
        var body = EnglishPart + "\n\n   <!-- release-notes zh-Hans   \n中文\n  -->  \n";

        Assert.Equal("中文", ReleaseNotesLanguages.Split(body).Translations["zh-Hans"]);
    }

    /// <summary>A closed code fence ends there: a comment inside it is code, and a block after it is found.</summary>
    [Fact]
    public void Split_AFenceClosed_LeavesTheBlockAfterItFound()
    {
        var body = "```\n<!-- not a block, code -->\n```\n\n<!-- release-notes zh-Hans\n中文\n-->\n";

        Assert.Equal("中文", ReleaseNotesLanguages.Split(body).Translations["zh-Hans"]);
    }

    /// <summary>
    /// A fence that opens inside a hidden span is hidden with it, as the parser hides it: it never turns the rest of the
    /// body into code, so the blocks after the span are found. The span may be a plain comment, or a translation whose
    /// own fence a translator left open.
    /// </summary>
    [Fact]
    public void Split_AFenceInsideAHiddenSpan_HidesNothingAfterIt()
    {
        var afterComment = EnglishPart + "\n\n<!-- a note\n```\n-->\n\n<!-- release-notes zh-Hans\n中文\n-->\n";
        var afterBlock = EnglishPart + "\n\n<!-- release-notes zh-Hans\n```text\n中文\n-->\n\n<!-- release-notes ru\nрусский\n-->\n";

        Assert.Equal("中文", ReleaseNotesLanguages.Split(afterComment).Translations["zh-Hans"]);
        var both = ReleaseNotesLanguages.Split(afterBlock);
        Assert.Equal("```text\n中文", both.Translations["zh-Hans"]);
        Assert.Equal("русский", both.Translations["ru"]);

        // What the dialog draws agrees: both bodies draw as their English part alone.
        Assert.Equal(Describe(MarkdownParser.Parse(EnglishPart)), Describe(MarkdownParser.Parse(afterComment)));
        Assert.Equal(Describe(MarkdownParser.Parse(EnglishPart)), Describe(MarkdownParser.Parse(afterBlock)));
    }

    /// <summary>A second block of the same language (in any case) is ignored: the first one wins.</summary>
    [Fact]
    public void Split_TheFirstBlockOfALanguageWins()
    {
        var body = EnglishPart + "\n\n<!-- release-notes zh-Hans\n第一\n-->\n\n<!-- release-notes zh-hans\n第二\n-->\n";

        var variants = ReleaseNotesLanguages.Split(body);

        Assert.Equal("第一", Assert.Single(variants.Translations).Value);
    }

    /// <summary>A body with more blocks than <see cref="ReleaseNotesLanguages.MaxTranslations"/> keeps the first ones only.</summary>
    [Fact]
    public void Split_KeepsAtMostMaxTranslations()
    {
        var body = new StringBuilder(EnglishPart);
        var tags = Enumerable.Range(0, ReleaseNotesLanguages.MaxTranslations + 5).Select(i => $"x{(char)('a' + (i / 26))}{(char)('a' + (i % 26))}").ToList();
        foreach (var tag in tags)
        {
            body.Append("\n\n<!-- release-notes ").Append(tag).Append('\n').Append("text of ").Append(tag).Append("\n-->\n");
        }

        var variants = ReleaseNotesLanguages.Split(body.ToString());

        Assert.Equal(ReleaseNotesLanguages.MaxTranslations, variants.Translations.Count);
        Assert.Equal(tags.Take(ReleaseNotesLanguages.MaxTranslations), variants.Translations.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>The language line may name a page of a language the release has no block for: no page is kept for it.</summary>
    [Fact]
    public void Split_APageIsKeptOnlyForATranslationTheReleaseHas()
    {
        var russianPage = "https://github.com/Nucs/BetterClipboard/blob/v9.9.9/docs/release-notes/9.9.9/ru.md";
        var body = Body.Replace("**ENG** / ", $"**ENG** / [Русский]({russianPage}) / ", StringComparison.Ordinal);

        var variants = ReleaseNotesLanguages.Split(body);

        Assert.Equal(ChinesePage, Assert.Single(variants.Pages).Value);
    }

    /// <summary>A release is shown in the wanted language when it carries it, in its own spelling of the tag, else in English.</summary>
    /// <param name="wanted">The wanted language (the interface's, or one picked on a language line).</param>
    /// <param name="shown">The language the sample release is shown in.</param>
    [Theory]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    [InlineData("en", "en")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("ZH-HANS", "zh-Hans")]
    [InlineData("ru", "en")]
    [InlineData("he", "en")]
    public void ShownLanguage_IsTheWantedTranslation_ElseEnglish(string? wanted, string shown)
    {
        Assert.Equal(shown, ReleaseNotesLanguages.ShownLanguage(ReleaseNotesLanguages.Split(Body), wanted));
    }

    /// <summary>The text drawn for a language is its translation, or the English body for English and for a missing one.</summary>
    [Fact]
    public void TextFor_GivesTheTranslation_OrTheEnglish()
    {
        var variants = ReleaseNotesLanguages.Split(Body);

        Assert.Equal(ChinesePart, ReleaseNotesLanguages.TextFor(variants, "zh-Hans"));
        Assert.Equal(ChinesePart, ReleaseNotesLanguages.TextFor(variants, "zh-hans"));
        Assert.Same(Body, ReleaseNotesLanguages.TextFor(variants, "en"));
        Assert.Same(Body, ReleaseNotesLanguages.TextFor(variants, "ru"));
        Assert.Same(Body, ReleaseNotesLanguages.TextFor(variants, null));
    }

    /// <summary>
    /// Only the language line's links switch: the release's own page means English (whatever its case, a trailing slash,
    /// a query or a fragment), the page of one of its translations means that language; every other link opens.
    /// </summary>
    /// <param name="address">The clicked address.</param>
    /// <param name="language">The language it switches to, or <see langword="null"/> for a link that opens.</param>
    [Theory]
    [MemberData(nameof(Links))]
    public void LanguageOfLink_SwitchesOnlyForTheLanguageLine(string address, string? language)
    {
        Assert.Equal(language, ReleaseNotesLanguages.LanguageOfLink(new Uri(address), ReleaseNotesLanguages.Split(Body), ReleasePage));
    }

    /// <summary>In a release without translations, a link to its own page or to a translation is an ordinary link.</summary>
    [Fact]
    public void LanguageOfLink_InAReleaseWithoutTranslations_EveryLinkIsALink()
    {
        var variants = ReleaseNotesLanguages.Split(EnglishPart);

        Assert.Null(ReleaseNotesLanguages.LanguageOfLink(ReleasePage, variants, ReleasePage));
        Assert.Null(ReleaseNotesLanguages.LanguageOfLink(ChinesePage, variants, ReleasePage));
    }

    /// <summary>Without a known release page, the way back to English is not recognized, the translations still are.</summary>
    [Fact]
    public void LanguageOfLink_WithoutAReleasePage_StillKnowsTheTranslations()
    {
        var variants = ReleaseNotesLanguages.Split(Body);

        Assert.Null(ReleaseNotesLanguages.LanguageOfLink(ReleasePage, variants, releasePage: null));
        Assert.Equal("zh-Hans", ReleaseNotesLanguages.LanguageOfLink(ChinesePage, variants, releasePage: null));
    }

    /// <summary>
    /// "View on GitHub" opens the translation's page while its notes are read in it, else the release's own page; a
    /// translation without a link to its page keeps the release's page.
    /// </summary>
    [Fact]
    public void PageFor_IsTheTranslationsPage_ElseTheRelease()
    {
        var variants = ReleaseNotesLanguages.Split(Body);

        Assert.Equal(ChinesePage, ReleaseNotesLanguages.PageFor(variants, "zh-Hans", ReleasePage));
        Assert.Equal(ReleasePage, ReleaseNotesLanguages.PageFor(variants, "en", ReleasePage));
        Assert.Equal(ReleasePage, ReleaseNotesLanguages.PageFor(variants, "ru", ReleasePage));
        Assert.Null(ReleaseNotesLanguages.PageFor(ReleaseNotesLanguages.Split(EnglishPart), "zh-Hans", null));

        var withoutLine = ReleaseNotesLanguages.Split(Body.Replace($"[简体中文]({ChinesePage})", "简体中文", StringComparison.Ordinal));
        Assert.Equal(ReleasePage, ReleaseNotesLanguages.PageFor(withoutLine, "zh-Hans", ReleasePage));
    }

    /// <summary>
    /// 2,000 random bodies of markers, fences, comment ends and text never make it throw, keep the English as given, keep
    /// at most the cap, never call English a translation, and never put a <c>--&gt;</c> or nothing into a translation.
    /// </summary>
    [Fact]
    public void Split_RandomBodies_NeverThrow_AndKeepTheirBounds()
    {
        string[] pieces =
        [
            "<!-- release-notes zh-Hans", "<!-- release-notes ru", "<!-- release-notes en", "<!--", "-->", " --> ", "a --> b",
            "```", "~~~", "````", "text", "**bold**", "> quote", "- item", string.Empty, "<!-- release-notes zh-Hans -->",
            "<!-- release-notes x" + new string('y', 50), "中文", "\r", "[ENG](https://github.com/Nucs/BetterClipboard/releases/tag/v1.0.0)",
        ];
        var random = new Random(2065);
        for (int round = 0; round < 2_000; round++)
        {
            var body = string.Join('\n', Enumerable.Range(0, random.Next(0, 40)).Select(_ => pieces[random.Next(pieces.Length)]));

            var variants = ReleaseNotesLanguages.Split(body);

            Assert.Same(body, variants.English);
            Assert.True(variants.Translations.Count <= ReleaseNotesLanguages.MaxTranslations);
            Assert.DoesNotContain(variants.Translations.Keys, key => key.Equals("en", StringComparison.OrdinalIgnoreCase));
            foreach (var text in variants.Translations.Values)
            {
                Assert.DoesNotContain("-->", text, StringComparison.Ordinal);
                Assert.NotEqual(string.Empty, text);
            }
        }
    }

    /// <summary>
    /// Writes parsed blocks as one compact line each (<c>P:</c> paragraph, <c>H2:</c> heading, <c>L0(•):</c> list item,
    /// <c>C:</c> code, <c>T:</c> table, <c>R</c> rule, <c>Q«…»</c> quote), runs with their styles as tags and links as
    /// <c>{text→address}</c>, so two documents compare as strings.
    /// </summary>
    /// <param name="blocks">The blocks.</param>
    /// <returns>One description per block.</returns>
    private static string[] Describe(IReadOnlyList<MarkdownBlock> blocks) => blocks.Select(block => block switch
    {
        MarkdownHeading heading => $"H{heading.Level}:{Describe(heading.Inlines)}",
        MarkdownListItem item => $"L{item.Depth}({item.Marker}):{Describe(item.Inlines)}",
        MarkdownCodeBlock code => $"C:{code.Code}",
        MarkdownTable table => "T:" + string.Join(" / ", table.Rows.Select(row => string.Join(" | ", row.Cells.Select(Describe)))),
        MarkdownRule => "R",
        MarkdownQuote quote => $"Q«{string.Join(" ¦ ", Describe(quote.Blocks))}»",
        MarkdownParagraph paragraph => $"P:{Describe(paragraph.Inlines)}",
        _ => block.GetType().Name,
    }).ToArray();

    /// <summary>Writes runs with their styles as tags (<c>[b]bold[/]</c>) and links as <c>{text→address}</c>.</summary>
    /// <param name="inlines">The runs.</param>
    /// <returns>The description.</returns>
    private static string Describe(IReadOnlyList<MarkdownInline> inlines)
    {
        var text = new StringBuilder();
        foreach (var inline in inlines)
        {
            var styled = inline.Style.HasFlag(MarkdownStyle.Bold) ? $"[b]{inline.Text}[/]" : inline.Text;
            text.Append(inline.Link is null ? styled : $"{{{styled}→{inline.Link.AbsoluteUri}}}");
        }

        return text.ToString();
    }
}
