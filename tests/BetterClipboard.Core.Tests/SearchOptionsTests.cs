using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the search box's toggles in isolation: <see cref="SearchMatcher"/> (match case, whole word, regular
/// expression — the exact semantics the index cannot give) and the candidate prefilter that must never drop a match.
/// </summary>
public sealed class SearchMatcherTests
{
    /// <summary>A blank search is no search, whatever the toggles.</summary>
    [Fact]
    public void Create_BlankIsNoSearch()
    {
        Assert.Null(SearchMatcher.Create(null, SearchOptions.Regex));
        Assert.Null(SearchMatcher.Create("   ", SearchOptions.MatchCase | SearchOptions.WholeWord));
    }

    /// <summary>Words: every one must be found; case only matters with match case, which keeps "Foo" and "foo" apart.</summary>
    [Fact]
    public void Words_AllRequired_CaseOnlyWithMatchCase()
    {
        var plain = Matcher("hello WORLD", SearchOptions.WholeWord);
        Assert.True(plain.IsMatch("Hello world, again"));
        Assert.False(plain.IsMatch("hello there"));

        var exact = Matcher("Hello", SearchOptions.MatchCase);
        Assert.True(exact.IsMatch("Say Hello"));
        Assert.False(exact.IsMatch("say hello"));

        // Under match case both spellings are separate requirements (the classic search drops one as a duplicate).
        var both = Matcher("Foo foo", SearchOptions.MatchCase);
        Assert.False(both.IsMatch("Foo only"));
        Assert.True(both.IsMatch("Foo and foo"));
    }

    /// <summary>VS Code's whole-word rule, including words that begin or end with punctuation and overlaps.</summary>
    /// <param name="term">The searched word.</param>
    /// <param name="text">The entry's text.</param>
    /// <param name="expected">Whether it is found as a whole word.</param>
    [Theory]
    [InlineData("log", "the log file", true)]
    [InlineData("log", "log", true)]
    [InlineData("log", "log.txt", true)]
    [InlineData("log", "my-log", true)]
    [InlineData("log", "login", false)]
    [InlineData("log", "catalog", false)]
    [InlineData("log", "my_log", false)] // '_' is a word character, as in \w
    [InlineData("log", "log2", false)]
    [InlineData(".cs", "file.cs", true)] // starts with a separator: no boundary needed before it
    [InlineData("-v", "run -v now", true)] // \b-v would fail here
    [InlineData("a-a", "xa-a-a", true)] // the first occurrence fails, the overlapping one at 3 stands alone
    [InlineData("שלום", "שלום עולם", true)]
    [InlineData("שלום", "שלומי", false)]
    [InlineData("naïve", "a naïve plan", true)]
    public void WholeWord_FollowsTheBoundaryRule(string term, string text, bool expected)
    {
        Assert.Equal(expected, Matcher(term, SearchOptions.WholeWord).IsMatch(text));
    }

    /// <summary>Characters are read as runes: a letter outside the BMP is one letter, not two separators.</summary>
    [Fact]
    public void WholeWord_ReadsRunesNotChars()
    {
        const string Text = "x\U0001D400y"; // MATHEMATICAL BOLD CAPITAL A (a letter, two UTF-16 units) between x and y
        Assert.False(SearchMatcher.IsWholeWord(Text, 3, 1));
        Assert.False(SearchMatcher.IsWholeWord(Text, 0, 1));
        Assert.True(SearchMatcher.IsWholeWord(Text, 0, Text.Length));
        Assert.Throws<ArgumentOutOfRangeException>(() => SearchMatcher.IsWholeWord(Text, 2, 3));
    }

    /// <summary>
    /// A regular expression: case-insensitive unless matching case, ^ and $ at every line of a CRLF text, and whole
    /// word applied to each match.
    /// </summary>
    [Fact]
    public void Regex_CaseLinesAndWholeWord()
    {
        Assert.True(Matcher(@"err\w+", SearchOptions.Regex).IsMatch("An ERROR occurred"));
        Assert.False(Matcher(@"err\w+", SearchOptions.Regex | SearchOptions.MatchCase).IsMatch("An ERROR occurred"));

        var line = Matcher(@"^\d{3}-\d{4}$", SearchOptions.Regex);
        Assert.True(line.IsMatch("name\r\n555-1234\r\nmore"));
        Assert.True(line.IsMatch("first\rsecond\r555-0000"));
        Assert.False(line.IsMatch("call 555-1234 now"));

        var digits = Matcher(@"\d+", SearchOptions.Regex | SearchOptions.WholeWord);
        Assert.False(digits.IsMatch("abc123"));
        Assert.True(digits.IsMatch("abc123 and 42"));

        // Spaces belong to a pattern, unlike words.
        Assert.False(Matcher("foo bar", SearchOptions.Regex).IsMatch("bar foo"));
        Assert.True(Matcher("foo bar", SearchOptions.Regex).IsMatch("a foo bar b"));
    }

    /// <summary>Entries without text never match, not even a pattern that matches the empty string.</summary>
    [Fact]
    public void EmptyText_NeverMatches()
    {
        var anything = Matcher(".*", SearchOptions.Regex);
        Assert.False(anything.IsMatch(string.Empty));
        Assert.False(anything.IsMatch(null));
        Assert.True(anything.IsMatch("x"));
    }

    /// <summary>An invalid pattern is refused up front, with the parser's reason without its preamble.</summary>
    [Fact]
    public void Regex_Invalid_IsRefusedWithAReason()
    {
        var ex = Assert.Throws<SearchPatternException>(() => SearchMatcher.Create("foo(", SearchOptions.Regex));
        Assert.Equal("Not enough )'s.", ex.Reason);
        Assert.Equal(4, ex.Offset);
        Assert.Equal("foo(", ex.Pattern);
        Assert.IsAssignableFrom<ArgumentException>(ex);

        // The same text is fine as words: only a pattern can be invalid.
        Assert.True(Matcher("foo(", SearchOptions.None).IsMatch("call foo(1)"));
    }

    /// <summary>
    /// The linear engine runs what it can, so a catastrophic pattern returns at once; a pattern that needs the
    /// backtracking engine (a lookahead) gets the timeout and fails as SearchTooSlowException instead of hanging.
    /// </summary>
    [Fact]
    public void Regex_CatastrophicPatterns_CannotHang()
    {
        var text = new string('a', 40) + "!";
        var linear = Matcher("(a+)+b", SearchOptions.Regex);
        Assert.False(linear.UsesBacktracking);
        Assert.False(linear.IsMatch(text));

        var backtracking = Matcher("(?=a)(a+)+b", SearchOptions.Regex);
        Assert.True(backtracking.UsesBacktracking);
        Assert.True(backtracking.IsMatch("aab"));
        var slow = Assert.Throws<SearchTooSlowException>(() => backtracking.IsMatch(text));
        Assert.Equal("(?=a)(a+)+b", slow.Pattern);
    }

    /// <summary>A cancelled token stops the match before any work.</summary>
    [Fact]
    public void IsMatch_HonorsCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => Matcher("x", SearchOptions.WholeWord).IsMatch("x", cancelled.Token));
    }

    /// <summary>
    /// The prefilter keeps the classic index terms (a superset), adds nothing for a pattern, and leaves a short
    /// cased non-ASCII word to the matcher (LIKE folds ASCII only) while caseless scripts keep their LIKE.
    /// </summary>
    [Fact]
    public void Prefilter_IsASupersetOfTheMatcher()
    {
        AssertPrefilter("\"clip\"", [@"%5\%%"], SearchQueryBuilder.BuildPrefilter("clip 5%", SearchOptions.WholeWord));
        AssertPrefilter(null, [], SearchQueryBuilder.BuildPrefilter("^clip$", SearchOptions.Regex | SearchOptions.WholeWord));
        AssertPrefilter(null, [], SearchQueryBuilder.BuildPrefilter("éa", SearchOptions.WholeWord));
        AssertPrefilter(null, ["%éa%"], SearchQueryBuilder.BuildPrefilter("éa", SearchOptions.MatchCase));
        AssertPrefilter(null, ["%של%"], SearchQueryBuilder.BuildPrefilter("של", SearchOptions.WholeWord));
    }

    /// <summary>Compiles a matcher that must exist (non-blank search).</summary>
    /// <param name="search">Search text.</param>
    /// <param name="options">Toggles.</param>
    /// <returns>The matcher.</returns>
    private static SearchMatcher Matcher(string search, SearchOptions options) =>
        SearchMatcher.Create(search, options) ?? throw new InvalidOperationException("Blank search.");

    /// <summary>Compares a prefilter part by part (a tuple holding a list compares the list by reference).</summary>
    /// <param name="fts">Expected FTS expression.</param>
    /// <param name="likes">Expected LIKE patterns, in order.</param>
    /// <param name="actual">The builder's result.</param>
    private static void AssertPrefilter(string? fts, string[] likes, (string? Fts, IReadOnlyList<string> Likes) actual)
    {
        Assert.Equal(fts, actual.Fts);
        Assert.Equal(likes, actual.Likes);
    }
}

/// <summary>
/// Tests for the toggles through the store: the SQL function runs inside the real query, so ordering, paging,
/// filters and groups still apply; errors surface as their own exceptions; the toggles persist in settings.
/// </summary>
public sealed class SearchOptionsStoreTests : IDisposable
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private readonly ClipStore store;

    /// <summary>Creates a fresh store per test.</summary>
    public SearchOptionsStoreTests()
    {
        store = new ClipStore(temp.DatabasePath);
        store.Initialize();
    }

    /// <summary>Deletes the temp database.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>Match case and whole word narrow what the classic search finds, through the index prefilter.</summary>
    [Fact]
    public void Query_MatchCaseAndWholeWord()
    {
        Add("Hello World", -4);
        Add("hello world", -3);
        Add("login form", -2);
        Add("log file", -1);

        Assert.Equal(2, Search("Hello", SearchOptions.None).Count);
        Assert.Equal(["Hello World"], Previews(Search("Hello", SearchOptions.MatchCase)));
        Assert.Equal(["log file", "login form"], Previews(Search("log", SearchOptions.None)));
        Assert.Equal(["log file"], Previews(Search("log", SearchOptions.WholeWord)));

        // Short words take the LIKE prefilter; a cased non-ASCII one goes to the matcher alone and is still found.
        Add("a b c", 0);
        Add("ÉA x", 1);
        Assert.Equal(["a b c"], Previews(Search("a", SearchOptions.WholeWord)));
        Assert.Equal(["ÉA x"], Previews(Search("éa", SearchOptions.WholeWord)));
    }

    /// <summary>A pattern is matched per line (CRLF texts included); images never match, even ".*".</summary>
    [Fact]
    public void Query_Regex_LinesAndNoImages()
    {
        Add("555-1234", -3);
        Add("call 555-1234 now", -2);
        Add("name\r\n555-0000\r\n", -1);
        store.Upsert(TestData.Image(3), ContentClassifier.Classify(TestData.Image(3))!, null, bumpIfExists: true);

        Assert.Equal(["name\r\n555-0000\r\n", "555-1234"], Texts(Search(@"^\d{3}-\d{4}$", SearchOptions.Regex)));
        Assert.Equal(3, Search(".*", SearchOptions.Regex).Count);
    }

    /// <summary>The function sits inside the real query: pinned-first order, paging, filters and groups all still apply.</summary>
    [Fact]
    public void Query_Regex_KeepsOrderPagingFiltersAndGroups()
    {
        var ids = Enumerable.Range(0, 5).Select(i => Add($"item {i}", i)).ToList();
        Add("other", 10);
        store.SetPinned(ids[0], true, TestData.Now);
        var regex = @"item \d";

        Assert.Equal(["item 0", "item 4", "item 3", "item 2", "item 1"], Previews(Search(regex, SearchOptions.Regex)));
        Assert.Equal(["item 3", "item 2"], Previews(store.Query(new ClipQuery { SearchText = regex, SearchOptions = SearchOptions.Regex, Offset = 2, Limit = 2 })));
        Assert.Empty(store.Query(new ClipQuery { SearchText = regex, SearchOptions = SearchOptions.Regex, Filter = ClipFilter.Images }));

        var group = store.CreateGroup("Work", Presentation.GroupIconCatalog.All[0].Glyph, TestData.Now);
        store.AddToGroup(ids[2], group.Id, TestData.Now);
        Assert.Equal(["item 2"], Previews(store.Query(new ClipQuery { SearchText = regex, SearchOptions = SearchOptions.Regex, GroupId = group.Id })));

        // A plain query on the same (pooled) connection afterwards is unaffected by the function.
        Assert.Equal(6, store.Query(new ClipQuery()).Count);
    }

    /// <summary>
    /// Failures keep their own type through SQLite (which only carries a message back): an invalid pattern, a
    /// pattern that runs out of time, and a cancelled scan.
    /// </summary>
    [Fact]
    public void Query_Failures_KeepTheirType()
    {
        Add(new string('a', 40) + "!", 0);

        Assert.Throws<SearchPatternException>(() => Search("foo(", SearchOptions.Regex));
        Assert.Throws<SearchTooSlowException>(() => Search("(?=a)(a+)+b", SearchOptions.Regex));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            store.Query(new ClipQuery { SearchText = "a", SearchOptions = SearchOptions.WholeWord }, cancelled.Token));

        // The classic search does not go through the matcher, so the same token changes nothing there.
        Assert.Single(store.Query(new ClipQuery { SearchText = "aaa" }, cancelled.Token));
    }

    /// <summary>The three toggles default to off and survive a save and reload of the settings file.</summary>
    [Fact]
    public void Settings_TogglesPersist()
    {
        var path = Path.Combine(temp.Path, "settings.json");
        var settings = new SettingsStore(path);
        var loaded = settings.Load();
        Assert.False(loaded.SearchMatchCase || loaded.SearchWholeWord || loaded.SearchUseRegex);

        settings.Update(s => s with { SearchMatchCase = true, SearchUseRegex = true });
        var reloaded = new SettingsStore(path).Load();
        Assert.True(reloaded.SearchMatchCase);
        Assert.False(reloaded.SearchWholeWord);
        Assert.True(reloaded.SearchUseRegex);
    }

    /// <summary>Stores a live text copy at <see cref="TestData.Now"/> plus <paramref name="minutes"/>.</summary>
    /// <param name="text">The text.</param>
    /// <param name="minutes">Offset from <see cref="TestData.Now"/> (later = more recent).</param>
    /// <returns>The entry id.</returns>
    private long Add(string text, int minutes)
    {
        var capture = TestData.Text(text, TestData.Now.AddMinutes(minutes));
        return store.Upsert(capture, ContentClassifier.Classify(capture)!, null, bumpIfExists: true)!.Entry.Id;
    }

    /// <summary>Runs a search with toggles over the whole history.</summary>
    /// <param name="search">Search text.</param>
    /// <param name="options">Toggles.</param>
    /// <returns>The entries, pinned first then newest first.</returns>
    private IReadOnlyList<ClipEntry> Search(string search, SearchOptions options) =>
        store.Query(new ClipQuery { SearchText = search, SearchOptions = options });

    /// <summary>The cards' preview texts, in order.</summary>
    /// <param name="entries">Entries.</param>
    /// <returns>Their previews.</returns>
    private static string[] Previews(IReadOnlyList<ClipEntry> entries) => entries.Select(e => e.Preview).ToArray();

    /// <summary>The entries' full texts, in order (previews flatten line breaks).</summary>
    /// <param name="entries">Entries.</param>
    /// <returns>Their texts.</returns>
    private string[] Texts(IReadOnlyList<ClipEntry> entries) =>
        entries.Select(e => UnicodeTextCodec.Decode(store.GetFormats(e.Id).First(f => f.Name == ClipFormatNames.UnicodeText).Data)).ToArray();
}
