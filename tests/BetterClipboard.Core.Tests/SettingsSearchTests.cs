using BetterClipboard.Core.Presentation;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Settings' search (CLAUDE.md §2.43; the owner's request, 2026-11-09: "search to settings which fuzzly match to anything of
/// that setting"): parsing the query, every kind of match the class remarks of <see cref="SettingsSearch"/> promise, the
/// ones it refuses on purpose (letters scattered over a long description), the weights, and the highlights mapped back to
/// the shown text through right-to-left marks and diacritics.
/// </summary>
public sealed class SettingsSearchTests
{
    /// <summary>A blank box is the empty query: every setting shows.</summary>
    /// <param name="text">What is in the box.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u0301\u202B")]
    public void Parse_Blank_IsEmpty(string? text) => Assert.True(SettingsSearchQuery.Parse(text).IsEmpty);

    /// <summary>Words are folded (case, diacritics) and each kept once, in typed order.</summary>
    [Fact]
    public void Parse_FoldsAndDedupes()
    {
        Assert.Equal(["espanol", "win+v"], SettingsSearchQuery.Parse("  Español ESPANOL   Win+V espanol ").Tokens);
    }

    /// <summary>A query takes at most eight words, so a pasted paragraph cannot make one keystroke expensive.</summary>
    [Fact]
    public void Parse_TakesAtMostEightWords()
    {
        var query = SettingsSearchQuery.Parse("a b c d e f g h i j");
        Assert.Equal(SettingsSearchQuery.MaxTokens, query.Tokens.Count);
        Assert.Equal("h", query.Tokens[^1]);
    }

    /// <summary>The empty query matches every setting, with score 0 and nothing highlighted.</summary>
    [Fact]
    public void Match_EmptyQuery_MatchesWithoutHighlights()
    {
        var match = SettingsSearch.Match(SettingsSearchQuery.Empty, Doc("Pause capturing", "Stop recording."));
        Assert.NotNull(match);
        Assert.Equal(0, match.Score);
        Assert.All(match.Spans, spans => Assert.Empty(spans));
    }

    /// <summary>
    /// A whole word scores more than a word's start, which scores more than text inside a word; each is highlighted where it
    /// is.
    /// </summary>
    [Fact]
    public void Match_WholeWordBeatsPrefixBeatsInside()
    {
        var doc = Doc("Pause capturing", string.Empty);
        var whole = Match("pause", doc);
        var prefix = Match("paus", doc);
        var inside = Match("aus", doc);

        Assert.Equal([new TextSpan(0, 5)], whole.Spans[0]);
        Assert.Equal([new TextSpan(0, 4)], prefix.Spans[0]);
        Assert.Equal([new TextSpan(1, 3)], inside.Spans[0]);
        Assert.True(whole.Score > prefix.Score);
        Assert.True(prefix.Score > inside.Score);
    }

    /// <summary>A single letter only matches the start of a word: otherwise "a" would find every setting.</summary>
    [Fact]
    public void Match_OneLetter_OnlyAtWordStarts()
    {
        var doc = Doc("Pause capturing", string.Empty);
        Assert.Equal([new TextSpan(6, 1)], Match("c", doc).Spans[0]);
        Assert.Null(SettingsSearch.Match(SettingsSearchQuery.Parse("u"), doc));
    }

    /// <summary>The initials of consecutive words find their setting, each initial highlighted.</summary>
    [Fact]
    public void Match_Initials()
    {
        var match = Match("rfe", Doc("Release from Explorer", string.Empty));
        Assert.Equal([new TextSpan(0, 1), new TextSpan(8, 1), new TextSpan(13, 1)], match.Spans[0]);
    }

    /// <summary>A word's letters in order, from the start of a word and close together, find it ("clpbrd" → "clipboard").</summary>
    [Fact]
    public void Match_LettersInOrder()
    {
        var match = Match("clpbrd", Doc("Windows' own clipboard history", string.Empty));
        // c, l, p, b, r, d of "clipboard" (which starts at 13); l follows c directly, so they merge.
        Assert.Equal([new TextSpan(13, 2), new TextSpan(16, 2), new TextSpan(20, 2)], match.Spans[0]);
    }

    /// <summary>
    /// Letters in order must start at a word's first letter and stay close: "lpbrd" starts inside "clipboard", "cbd" is
    /// spread over nine letters for three, and letters scattered over a long description find nothing.
    /// </summary>
    /// <param name="query">The query.</param>
    [Theory]
    [InlineData("lpbrd")]
    [InlineData("cbd")]
    [InlineData("xqzv")]
    public void Match_LettersInOrder_RefusesWhatIsTooLoose(string query)
    {
        var doc = Doc("Windows' own clipboard history",
            "Bring in everything Win+V still remembers, including its pinned items (decrypted from Windows' pinned store with your own account).");
        Assert.Null(SettingsSearch.Match(SettingsSearchQuery.Parse(query), doc));
    }

    /// <summary>
    /// Letters in order never run across a space ("theme" is not "the mouse", which once listed a panel-position card for
    /// it), but they do across a joiner such as "+" ("winv" finds "Win+V", its first three letters merged).
    /// </summary>
    [Fact]
    public void Match_LettersInOrder_NeverAcrossASpace()
    {
        Assert.Null(SettingsSearch.Match(SettingsSearchQuery.Parse("theme"), Doc("Panel position", "Or the one with the mouse when you are on the desktop.")));
        Assert.Equal([new TextSpan(0, 3), new TextSpan(4, 1)], Match("winv", Doc("Win+V", string.Empty)).Spans[0]);
    }

    /// <summary>
    /// A match is fuzzy exactly when some word of the query is found only by its letters in order or with a typo; found
    /// as written — whole, at a word's start, inside a word, or as initials — it is not.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <param name="header">The setting's title.</param>
    /// <param name="fuzzy">Whether the match is fuzzy.</param>
    [Theory]
    [InlineData("password", "Password", false)]
    [InlineData("pass", "Password", false)]
    [InlineData("swor", "Password", false)]
    [InlineData("rfe", "Release from Explorer", false)]
    [InlineData("pasword", "Password", true)]
    [InlineData("psswrd", "Password", true)]
    [InlineData("release pasword", "Release password", true)]
    public void Match_IsFuzzy_OnlyWhenAWordIsNotWritten(string query, string header, bool fuzzy) =>
        Assert.Equal(fuzzy, Match(query, Doc(header, string.Empty)).IsFuzzy);

    /// <summary>
    /// A word the setting has as written is never also matched fuzzily elsewhere in it: "Theme" in the title, and no
    /// highlighted "these" in the description below it.
    /// </summary>
    [Fact]
    public void Match_WrittenSomewhere_NoFuzzyHighlightsElsewhere()
    {
        var match = Match("theme", Doc("Theme", "Pick one of these."));
        Assert.False(match.IsFuzzy);
        Assert.Equal([new TextSpan(0, 5)], match.Spans[0]);
        Assert.Empty(match.Spans[1]);
    }

    /// <summary>
    /// The page shows the settings that have every word as written when there are any, and falls back to the fuzzy ones
    /// only when there are none: "theme" shows the setting titled "Theme", not the one that says "these" — unless no
    /// setting says "theme" at all.
    /// </summary>
    [Fact]
    public void Shown_WrittenMatchesHideFuzzyOnes()
    {
        var written = Match("theme", Doc("Theme", string.Empty));
        var fuzzy = Match("theme", Doc("Look", "Pick one of these."));
        Assert.True(fuzzy.IsFuzzy);

        Assert.Equal([true, false, false], SettingsSearch.Shown([written, fuzzy, null]));
        Assert.Equal([true, false], SettingsSearch.Shown([fuzzy, null]));
        Assert.Empty(SettingsSearch.Shown([]));
        Assert.Throws<ArgumentNullException>(() => SettingsSearch.Shown(null!));
    }

    /// <summary>
    /// A typo finds the meant word: a missing letter ("pasword", "histry") through the letters in order, which highlights
    /// them; a swap plus an extra letter in a long word ("shrotcutt", two typos from eight letters) through the typo measure,
    /// which highlights the whole word. Either way the highlights stay inside the meant word.
    /// </summary>
    /// <param name="query">The mistyped word.</param>
    /// <param name="start">Where the meant word starts.</param>
    /// <param name="length">Its length.</param>
    [Theory]
    [InlineData("pasword", 17, 8)]
    [InlineData("histry", 0, 7)]
    [InlineData("shrotcutt", 42, 8)]
    public void Match_Typos(string query, int start, int length)
    {
        var spans = Match(query, Doc(string.Empty, "History of known password managers, and a shortcut.")).Spans[1];
        Assert.NotEmpty(spans);
        Assert.All(spans, span => Assert.True(span.Start >= start && span.Start + span.Length <= start + length, $"{span} is outside the word"));
    }

    /// <summary>
    /// A short word gets no typo: "fiel" does not find "files" (a swap in four letters is not forgiven — too many short
    /// words are one typo apart), and its letters are not in order there.
    /// </summary>
    [Fact]
    public void Match_NoTypoInShortWords() =>
        Assert.Null(SettingsSearch.Match(SettingsSearchQuery.Parse("fiel"), Doc("Record copied files", "Copies of folders.")));

    /// <summary>
    /// A mistyped start of a much longer word finds it: "paxsword" (a wrong letter, so not the letters in order) is one typo
    /// from the start of "passwordsmanager", and that start is highlighted.
    /// </summary>
    [Fact]
    public void Match_TypoInAWordsStart() =>
        Assert.Equal([new TextSpan(0, 8)], Match("paxsword", Doc("passwordsmanager", string.Empty)).Spans[0]);

    /// <summary>
    /// Every word of the query must match, each anywhere in the setting: a word found only in the section's title still
    /// counts, a word found nowhere makes no match.
    /// </summary>
    [Fact]
    public void Match_EveryWordSomewhere()
    {
        var doc = Doc("Pause capturing", "Stop recording.", (SearchFieldKind.Section, "Privacy"));
        Assert.NotNull(SettingsSearch.Match(SettingsSearchQuery.Parse("pause privacy"), doc));
        Assert.Null(SettingsSearch.Match(SettingsSearchQuery.Parse("pause updates"), doc));
    }

    /// <summary>The same word weighs more in a title than in a description, and more in keywords than in other texts.</summary>
    [Fact]
    public void Match_Weights()
    {
        var inTitle = Match("theme", Doc("Theme", string.Empty));
        var inDescription = Match("theme", Doc("Look", "The theme."));
        var inKeywords = Match("dark", Doc("Theme", string.Empty, (SearchFieldKind.Keywords, "dark mode")));
        var inOther = Match("dark", Doc("Theme", string.Empty, (SearchFieldKind.Other, "Dark")));
        Assert.True(inTitle.Score > inDescription.Score);
        Assert.True(inKeywords.Score > inOther.Score);
    }

    /// <summary>Every occurrence is highlighted, and stretches that touch or overlap become one.</summary>
    [Fact]
    public void Match_HighlightsEveryOccurrence_Merged()
    {
        Assert.Equal([new TextSpan(0, 4), new TextSpan(11, 4)], Match("copy", Doc("Copy only, copy it", string.Empty)).Spans[0]);
        Assert.Equal([new TextSpan(0, 9)], Match("clip clipboard", Doc("Clipboard", string.Empty)).Spans[0]);
    }

    /// <summary>
    /// Highlights land on the shown text: past a Hebrew translation's right-to-left embedding mark, and over a letter's
    /// diacritics whether precomposed ("ñ") or written apart ("e" + combining acute).
    /// </summary>
    [Fact]
    public void Match_SpansMapBackToTheShownText()
    {
        Assert.Equal([new TextSpan(7, 7)], Match("בהגדרות", Doc("\u202Bחיפוש בהגדרות\u202C", string.Empty)).Spans[0]);
        Assert.Equal([new TextSpan(0, 7)], Match("espanol", Doc("Español", string.Empty)).Spans[0]);
        Assert.Equal([new TextSpan(0, 5)], Match("CAFE", Doc("Cafe\u0301 au lait", string.Empty)).Spans[0]);
    }

    /// <summary>Chinese, written without spaces, is found inside its text.</summary>
    [Fact]
    public void Match_ChineseInsideItsText() =>
        Assert.Equal([new TextSpan(2, 2)], Match("快捷", Doc("打开快捷键", string.Empty)).Spans[0]);

    /// <summary>Known distances of the typo measure (adjacent swaps count once), and its early stop.</summary>
    /// <param name="a">One word.</param>
    /// <param name="b">The other.</param>
    /// <param name="max">The largest distance of interest.</param>
    /// <param name="expected">The distance, or max + 1 beyond it.</param>
    [Theory]
    [InlineData("pasword", "password", 2, 1)]
    [InlineData("clipbaord", "clipboard", 2, 1)]
    [InlineData("kitten", "sitting", 3, 3)]
    [InlineData("kitten", "sitting", 1, 2)]
    [InlineData("same", "same", 0, 0)]
    public void Distance_KnownAnswers(string a, string b, int max, int expected) =>
        Assert.Equal(expected, SettingsSearch.Distance(a, b, 0, b.Length, max));

    /// <summary>A document refuses a field without text: a null would surface as a crash on the next keystroke.</summary>
    [Fact]
    public void Document_RefusesNullText() =>
        Assert.Throws<ArgumentNullException>(() => new SearchDocument([new SearchField(SearchFieldKind.Header, null!)]));

    /// <summary>A setting's document: its title and description first (indexes 0 and 1), then any other fields.</summary>
    /// <param name="header">The title.</param>
    /// <param name="description">The description.</param>
    /// <param name="others">More fields.</param>
    /// <returns>The document.</returns>
    private static SearchDocument Doc(string header, string description, params (SearchFieldKind Kind, string Text)[] others) =>
        new([new SearchField(SearchFieldKind.Header, header), new SearchField(SearchFieldKind.Description, description), .. others.Select(o => new SearchField(o.Kind, o.Text))]);

    /// <summary>Matches a query that must match.</summary>
    /// <param name="query">The query text.</param>
    /// <param name="doc">The document.</param>
    /// <returns>The match.</returns>
    /// <exception cref="Xunit.Sdk.XunitException">The query did not match (fails the test with the query named).</exception>
    private static SettingsSearchMatch Match(string query, SearchDocument doc) =>
        SettingsSearch.Match(SettingsSearchQuery.Parse(query), doc) ?? throw new Xunit.Sdk.XunitException($"\"{query}\" did not match.");
}
