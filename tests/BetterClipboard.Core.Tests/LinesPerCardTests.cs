using System.Text;
using System.Text.Json;
using BetterClipboard.Core.Cli;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Prompts;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;
using Microsoft.Data.Sqlite;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Lines per card (CLAUDE.md §2.57): the setting and its bounds, the part of a preview a card hands its text block, the
/// longer stored previews that let forty lines fill (and the short one screen readers and <c>bclip</c> keep), and the
/// one-time rebuild of the previews earlier versions cut at 1,000 characters and five file names.
/// </summary>
public sealed class LinesPerCardTests : IDisposable
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();

    /// <summary>Deletes the temp directory and its databases.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>Lines a card can be asked for, with the lines its text block then gets (one more, within the bounds).</summary>
    public static TheoryData<int, int> LineCounts => new()
    {
        { 1, 2 },
        { 4, 5 },
        { 17, 18 },
        { 40, 41 },
        { 0, 2 },        // below the bounds: one line, as the settings would keep it
        { -3, 2 },
        { 41, 41 },      // above the bounds: forty lines
        { int.MaxValue, 41 },
    };

    /// <summary>A new install shows four lines, what every card showed before the number became a setting.</summary>
    [Fact]
    public void Defaults_FourLines()
    {
        var settings = new SettingsStore(Path.Combine(temp.Path, "settings.json")).Load();
        Assert.Equal(4, settings.LinesPerCard);
        Assert.Equal(AppSettings.DefaultLinesPerCard, new AppSettings().LinesPerCard);
        Assert.Equal((1, 40), (AppSettings.MinLinesPerCard, AppSettings.MaxLinesPerCard));
    }

    /// <summary>
    /// The number survives a save and a load under its own name; a file from before the setting (which an older version
    /// also writes) reads four, and a hand-edited number outside 1 to 40 is kept within them.
    /// </summary>
    [Fact]
    public void Setting_RoundTrips_AndIsKeptWithinItsBounds()
    {
        var path = Path.Combine(temp.Path, "settings.json");
        var store = new SettingsStore(path);
        store.Load();
        store.Update(s => s with { LinesPerCard = 17 });
        Assert.Equal(17, new SettingsStore(path).Load().LinesPerCard);
        using (var file = JsonDocument.Parse(File.ReadAllText(path)))
        {
            Assert.Equal(17, file.RootElement.GetProperty(nameof(AppSettings.LinesPerCard)).GetInt32());
        }

        File.WriteAllText(path, "{ \"MaxItems\": 50 }");
        var before = new SettingsStore(path).Load();
        Assert.Equal(4, before.LinesPerCard);
        Assert.Equal(50, before.MaxItems);

        File.WriteAllText(path, "{ \"LinesPerCard\": 0 }");
        Assert.Equal(1, new SettingsStore(path).Load().LinesPerCard);
        File.WriteAllText(path, "{ \"LinesPerCard\": 1000 }");
        Assert.Equal(40, new SettingsStore(path).Load().LinesPerCard);
        Assert.Equal(1, (new AppSettings { LinesPerCard = int.MinValue }).Normalize().LinesPerCard);
    }

    /// <summary>
    /// The bounds the rest relies on: a file list's preview names as many files as a card shows lines; a preview holds
    /// forty lines of hundreds of characters; and twice the preview stays below the indexed text by more than a character,
    /// which makes the rebuild from the indexed text exact.
    /// </summary>
    [Fact]
    public void Bounds_HoldTogether()
    {
        Assert.True(ContentClassifier.PreviewMaxFiles >= AppSettings.MaxLinesPerCard);
        Assert.True(ContentClassifier.PreviewMaxChars >= AppSettings.MaxLinesPerCard * 400);
        Assert.True((ContentClassifier.PreviewMaxChars * 2) + 1 < ContentClassifier.SearchMaxChars);
        Assert.True(ContentClassifier.PreviewMaxChars <= (AppSettings.MaxLinesPerCard + 1) * CardPreview.MaxCharsPerLine);
        Assert.Equal(1000, ContentClassifier.ShortPreviewChars);
        Assert.Equal(5, ContentClassifier.LegacyPreviewFiles);
    }

    /// <summary>
    /// A preview of many lines is cut after one line more than the card shows, so its text block still trims it; a preview
    /// that fits is handed over whole; the line count is kept within its bounds.
    /// </summary>
    /// <param name="lines">The lines asked for.</param>
    /// <param name="kept">The lines the text block gets.</param>
    [Theory]
    [MemberData(nameof(LineCounts))]
    public void CardPreview_KeepsOneLineMoreThanTheCardShows(int lines, int kept)
    {
        var fifty = string.Join('\n', Enumerable.Range(1, 50).Select(i => $"BC-TEST line {i}"));
        var part = CardPreview.For(fifty, lines);
        Assert.True(part.IsCut);
        Assert.Equal(string.Join('\n', Enumerable.Range(1, kept).Select(i => $"BC-TEST line {i}")), part.Text);

        // Exactly the lines kept, or fewer: handed over whole.
        var fits = string.Join('\n', Enumerable.Range(1, kept).Select(i => $"line {i}"));
        Assert.Equal(new CardPreviewPart(fits, false), CardPreview.For(fits, lines));
        Assert.Equal(new CardPreviewPart(string.Empty, false), CardPreview.For(string.Empty, lines));
    }

    /// <summary>
    /// One long line (a paragraph, a minified file) is cut by characters: 500 a line for one line more than the card shows,
    /// never between the halves of a surrogate pair; at forty lines the longest preview is handed over whole.
    /// </summary>
    [Fact]
    public void CardPreview_CutsALongLineByCharacters()
    {
        var paragraph = new string('x', ContentClassifier.PreviewMaxChars);
        var four = CardPreview.For(paragraph, 4);
        Assert.Equal((5 * CardPreview.MaxCharsPerLine, true), (four.Text.Length, four.IsCut));
        Assert.Equal(new CardPreviewPart(paragraph, false), CardPreview.For(paragraph, 40));

        // A surrogate pair across the cut: the cut steps back before it.
        var emoji = new string('a', (2 * CardPreview.MaxCharsPerLine) - 1) + "😀😀";
        var one = CardPreview.For(emoji, 1);
        Assert.True(one.IsCut);
        Assert.Equal((2 * CardPreview.MaxCharsPerLine) - 1, one.Text.Length);
        Assert.False(char.IsHighSurrogate(one.Text[^1]));
        Assert.Throws<ArgumentNullException>(() => CardPreview.For(null!, 4));
    }

    /// <summary>
    /// Random previews and line counts: the text is always the start of the preview, cut exactly when it is shorter, with
    /// at most one line more than the card shows and at most 500 characters a line of them, and never a lone high surrogate
    /// at its end.
    /// </summary>
    [Fact]
    public void CardPreview_RandomPreviews_KeepEveryRule()
    {
        var random = new Random(20261120);
        string[] pieces = ["BC", "-", "TEST", " ", "\n", "\n\n", "😀", "שלום", "表格", new string('w', 300), "\t"];
        for (int round = 0; round < 2_000; round++)
        {
            var builder = new StringBuilder();
            int count = random.Next(0, 120);
            for (int i = 0; i < count; i++)
            {
                builder.Append(pieces[random.Next(pieces.Length)]);
            }

            var preview = builder.ToString();
            int lines = random.Next(-2, 45);
            var part = CardPreview.For(preview, lines);
            int kept = Math.Clamp(lines, 1, 40) + 1;
            Assert.StartsWith(part.Text, preview, StringComparison.Ordinal);
            Assert.Equal(part.Text.Length < preview.Length, part.IsCut);
            Assert.True(part.Text.Count(c => c == '\n') < kept);
            Assert.True(part.Text.Length <= kept * CardPreview.MaxCharsPerLine);
            Assert.False(part.Text.Length > 0 && char.IsHighSurrogate(part.Text[^1]));
        }
    }

    /// <summary>
    /// A long text keeps a preview of up to 16,000 characters now (1,000 before), and a file list up to forty names, with
    /// "+N more" only beyond them; a shorter list has no such line.
    /// </summary>
    [Fact]
    public void Previews_HoldEnoughForFortyLines()
    {
        var text = string.Join("\r\n", Enumerable.Range(1, 3_000).Select(i => $"BC-TEST line {i:D4} of a long log"));
        var preview = ContentClassifier.BuildTextPreview(text);
        Assert.Equal(ContentClassifier.PreviewMaxChars, preview.Length);
        Assert.EndsWith("…", preview, StringComparison.Ordinal);
        Assert.StartsWith("BC-TEST line 0001 of a long log\nBC-TEST line 0002", preview, StringComparison.Ordinal);

        var many = Enumerable.Range(1, 43).Select(i => $@"C:\BC-TEST\file{i}.txt").ToArray();
        var files = ContentClassifier.DescribeFiles(many);
        Assert.Equal(41, files.Split('\n').Length);
        Assert.StartsWith("file1.txt\nfile2.txt", files, StringComparison.Ordinal);
        Assert.EndsWith("file40.txt\n+3 more", files, StringComparison.Ordinal);
        Assert.Equal(string.Join('\n', many.Take(40).Select(Path.GetFileName)), ContentClassifier.DescribeFiles([.. many.Take(40)]));
        Assert.Throws<ArgumentNullException>(() => ContentClassifier.DescribeFiles(null!));
    }

    /// <summary>
    /// Texts whose preview must come out the same from the indexed text as from the whole text: long ones, ones of exactly
    /// the lengths around the caps, ones that normalize to much less (line ends, leading blank lines), and ones with
    /// surrogate pairs across the index's cut.
    /// </summary>
    public static TheoryData<string> RebuildTexts => new()
    {
        new string('x', 40_000),
        string.Concat(Enumerable.Repeat("a\r\n", 14_000)),
        new string('\n', 20_000) + "BC-TEST" + new string('y', 20_000),
        new string('z', (ContentClassifier.PreviewMaxChars * 2) + 1),
        new string('z', ContentClassifier.SearchMaxChars),
        new string('z', ContentClassifier.SearchMaxChars + 1),
        string.Concat(Enumerable.Repeat("😀", 20_000)),
        "a" + string.Concat(Enumerable.Repeat("😀", 20_000)),
        string.Concat(Enumerable.Repeat("BC-TEST שלום 表格\r\n", 3_000)) + "  \r\n",
    };

    /// <summary>
    /// The preview rebuilt from the indexed text, read the way the store's rebuild reads it (SQLite's <c>substr</c>, which
    /// counts code points), equals the preview the classifier built from the whole text: the rebuild loses nothing.
    /// </summary>
    /// <param name="text">The copied text.</param>
    [Theory]
    [MemberData(nameof(RebuildTexts))]
    public void Preview_FromTheIndexedText_EqualsTheOneFromTheWholeText(string text)
    {
        var classified = ContentClassifier.Classify(TestData.Text(text))!;
        Assert.Equal(classified.Preview, ContentClassifier.BuildTextPreview(classified.SearchText));
        Assert.Equal(classified.Preview, ContentClassifier.BuildTextPreview(CodePoints(classified.SearchText, (ContentClassifier.PreviewMaxChars * 2) + 1)));
        Assert.Equal(classified.Preview, ContentClassifier.BuildTextPreview(CodePoints(text, (ContentClassifier.PreviewMaxChars * 2) + 1)));
        Assert.DoesNotContain('\uFFFD', classified.Preview);
    }

    /// <summary>
    /// The head the classifier normalizes is cut between surrogate pairs, never inside one: a text whose head normalizes to
    /// almost nothing (16,000 blank lines) and ends half-way through an emoji gives "a…", where a cut inside the pair would
    /// leave a lone high surrogate that SQLite stores as U+FFFD.
    /// </summary>
    [Fact]
    public void Preview_NeverEndsWithALoneSurrogate()
    {
        var text = string.Concat(Enumerable.Repeat("\r\n", ContentClassifier.PreviewMaxChars - 1)) + "a😀zz";
        Assert.True(char.IsHighSurrogate(text[(ContentClassifier.PreviewMaxChars * 2) - 1]));
        Assert.Equal("a…", ContentClassifier.BuildTextPreview(text));
    }

    /// <summary>
    /// The short preview is what previews were before they grew: a long one cut at 999 characters with "…", the same as
    /// the old classifier gave; a short one as it is; never a split surrogate pair.
    /// </summary>
    [Fact]
    public void ShortPreview_IsWhatPreviewsWereBefore()
    {
        var text = string.Join("\r\n", Enumerable.Range(1, 400).Select(i => $"BC-TEST {i} lorem ipsum dolor"));
        Assert.Equal(OldPreview(text), ContentClassifier.ShortPreview(ContentClassifier.BuildTextPreview(text)));
        Assert.Equal("BC-TEST short", ContentClassifier.ShortPreview("BC-TEST short"));
        var thousand = new string('k', 1000);
        Assert.Same(thousand, ContentClassifier.ShortPreview(thousand));
        var emoji = new string('e', 998) + "😀😀";
        var cut = ContentClassifier.ShortPreview(emoji);
        Assert.Equal(new string('e', 998) + "…", cut);
        Assert.Throws<ArgumentNullException>(() => ContentClassifier.ShortPreview(null!));
    }

    /// <summary>
    /// A preview counts as cut when it ends the way a cut one does: a file list's "+N more" line, or "…" at the old length
    /// or more (previews earlier versions cut, and the long ones of now); a short text that merely ends with "…" does not.
    /// </summary>
    [Fact]
    public void IsCutPreview_RecognizesOldAndNewCuts()
    {
        var text = string.Join('\n', Enumerable.Range(1, 2_000).Select(i => $"BC-TEST {i}"));
        Assert.True(ContentClassifier.IsCutPreview(ClipKind.Text, ContentClassifier.BuildTextPreview(text)));
        Assert.True(ContentClassifier.IsCutPreview(ClipKind.RichText, OldPreview(text)));
        Assert.True(ContentClassifier.IsCutPreview(ClipKind.Text, new string('a', 998) + "…"));
        Assert.False(ContentClassifier.IsCutPreview(ClipKind.Text, new string('a', 997) + "…"));
        Assert.False(ContentClassifier.IsCutPreview(ClipKind.Text, "Wait…"));
        Assert.True(ContentClassifier.IsCutPreview(ClipKind.Files, "a\nb\nc\nd\ne\n+38 more"));
        Assert.True(ContentClassifier.IsCutPreview(ClipKind.Files, ContentClassifier.DescribeFiles([.. Enumerable.Range(1, 41).Select(i => $"f{i}")])));
        Assert.False(ContentClassifier.IsCutPreview(ClipKind.Files, ContentClassifier.DescribeFiles([.. Enumerable.Range(1, 40).Select(i => $"f{i}")])));
        Assert.False(ContentClassifier.IsCutPreview(ClipKind.Files, "one more"));
        Assert.Throws<ArgumentNullException>(() => ContentClassifier.IsCutPreview(ClipKind.Text, null!));
    }

    /// <summary>
    /// The store rebuilds once the previews an earlier version cut short: texts from their indexed text, a file list from
    /// its own file list, an archived prompt from its whole text, each equal to what the classifier gives now; a text that
    /// merely ends with "…", a whole text and a picture stay as they are; the second start changes nothing.
    /// </summary>
    [Fact]
    public void Initialize_RebuildsShortPreviewsOnce()
    {
        var store = new ClipStore(temp.DatabasePath);
        store.Initialize();
        Assert.Equal(0, store.RebuiltPreviews);

        var log = string.Join("\r\n", Enumerable.Range(1, 2_500).Select(i => $"BC-TEST {i} of a log line"));
        var huge = string.Concat(Enumerable.Repeat("BC-TEST paragraph without breaks ", 2_000));
        var paths = Enumerable.Range(1, 43).Select(i => $@"C:\BC-TEST\file{i}.txt").ToArray();
        var long1 = Add(store, TestData.Text(log, TestData.Now.AddMinutes(-5)));
        var long2 = Add(store, TestData.Rich(huge, "<p>BC-TEST</p>"));
        var files = Add(store, TestData.Files(paths));
        var ellipsis = Add(store, TestData.Text("BC-TEST wait…", TestData.Now.AddMinutes(-4)));
        var whole = Add(store, TestData.Text("BC-TEST short", TestData.Now.AddMinutes(-3)));
        var picture = Add(store, TestData.Image(7));
        store.IngestPrompts(
            new PromptBatch
            {
                Agent = PromptAgent.ClaudeCode,
                Kind = PromptSourceKind.ClaudeHistory,
                FileKey = "BC-TEST",
                Path = "BC-TEST",
                Checkpoint = new TailCheckpoint { Offset = 1, Length = 1 },
                Prompts = [new AgentPrompt(PromptSourceKind.ClaudeHistory, "k-long", "s-1", null, TestData.Now, log), new AgentPrompt(PromptSourceKind.ClaudeHistory, "k-short", "s-1", null, TestData.Now, "BC-TEST hi")],
            },
            long.MaxValue,
            writeCheckpoint: true,
            TestData.Now);
        var prompts = store.QueryPrompts(new PromptQuery { Distinct = false }, includeText: false, CancellationToken.None);
        long longPrompt = prompts.Single(p => p.Preview.StartsWith("BC-TEST 1 of", StringComparison.Ordinal)).Id;

        // An earlier version's file: its previews cut at 1,000 characters and five names, and the rebuild not run yet.
        Raw("UPDATE clips SET preview = $p WHERE id = $id;", ("$p", OldPreview(log)), ("$id", long1));
        Raw("UPDATE clips SET preview = $p WHERE id = $id;", ("$p", OldPreview(huge)), ("$id", long2));
        Raw("UPDATE clips SET preview = $p WHERE id = $id;", ("$p", "file1.txt\nfile2.txt\nfile3.txt\nfile4.txt\nfile5.txt\n+38 more"), ("$id", files));
        Raw("UPDATE prompts SET preview = $p WHERE id = $id;", ("$p", OldPreview(log)), ("$id", longPrompt));
        Raw("DELETE FROM meta WHERE key = 'fixup.long_previews.v1';");

        var reopened = new ClipStore(temp.DatabasePath);
        reopened.Initialize();
        Assert.Equal(4, reopened.RebuiltPreviews);
        Assert.Equal(ContentClassifier.BuildTextPreview(log), reopened.GetEntry(long1)!.Preview);
        Assert.Equal(ContentClassifier.BuildTextPreview(huge), reopened.GetEntry(long2)!.Preview);
        Assert.Equal(ContentClassifier.DescribeFiles(paths), reopened.GetEntry(files)!.Preview);
        Assert.Equal(ContentClassifier.BuildTextPreview(log), reopened.GetPrompt(longPrompt)!.Preview);
        Assert.Equal("BC-TEST wait…", reopened.GetEntry(ellipsis)!.Preview);
        Assert.Equal("BC-TEST short", reopened.GetEntry(whole)!.Preview);
        Assert.Equal(ContentClassifier.DescribeImage(null, null), reopened.GetEntry(picture)!.Preview);

        // The index is unchanged: a search still finds the rows by their text.
        Assert.Equal([long1], reopened.Query(new ClipQuery { SearchText = "BC-TEST 10 of a log line" }).Select(e => e.Id));
        Assert.Equal([long2], reopened.Query(new ClipQuery { SearchText = "without breaks" }).Select(e => e.Id));

        // Once done, never again: a short preview written afterwards (by an older version) stays as it is.
        Raw("UPDATE clips SET preview = $p WHERE id = $id;", ("$p", OldPreview(log)), ("$id", long1));
        var again = new ClipStore(temp.DatabasePath);
        again.Initialize();
        Assert.Equal(0, again.RebuiltPreviews);
        Assert.Equal(OldPreview(log), again.GetEntry(long1)!.Preview);
    }

    /// <summary>
    /// <c>bclip</c>'s JSON keeps the short preview (at most 1,000 characters, as before previews grew), for history items
    /// and archived prompts alike; a short preview goes out as it is.
    /// </summary>
    [Fact]
    public void Cli_KeepsTheShortPreview()
    {
        var text = string.Join('\n', Enumerable.Range(1, 2_000).Select(i => $"BC-TEST {i}"));
        var preview = ContentClassifier.BuildTextPreview(text);
        var item = CliCommandProcessor.ToItem(new ClipEntry { Id = 1, Kind = ClipKind.Text, Preview = preview });
        Assert.Equal(ContentClassifier.ShortPreview(preview), item.Preview);
        Assert.Equal(1000, item.Preview.Length);
        Assert.Equal("BC-TEST", CliCommandProcessor.ToItem(new ClipEntry { Id = 2, Kind = ClipKind.Text, Preview = "BC-TEST" }).Preview);
    }

    /// <summary>
    /// The preview the classifier gave before 2026-11-20 (1,000 characters at most): newlines unified, trailing white space
    /// and leading blank lines dropped, cut at 999 characters with "…" when longer than 1,000 or longer than 2,000 raw. A copy
    /// of the old rule, so the rebuild can be fed the previews earlier versions stored.
    /// </summary>
    /// <param name="text">The copied text.</param>
    /// <returns>The old preview.</returns>
    private static string OldPreview(string text)
    {
        var head = text.Length > 2000 ? text[..2000] : text;
        head = head.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd();
        int first = 0;
        while (first < head.Length && (head[first] == '\n' || head[first] == ' ' || head[first] == '\t'))
        {
            int lineEnd = head.IndexOf('\n', first);
            if (lineEnd < 0 || !string.IsNullOrWhiteSpace(head[first..lineEnd]))
            {
                break;
            }

            first = lineEnd + 1;
        }

        head = head[first..];
        if (head.Length <= 1000 && text.Length <= 2000)
        {
            return head;
        }

        int cut = char.IsHighSurrogate(head[998]) ? 998 : 999;
        return head[..cut] + "…";
    }

    /// <summary>The first <paramref name="count"/> code points of a text, as SQLite's <c>substr</c> counts them.</summary>
    /// <param name="text">The text.</param>
    /// <param name="count">How many code points.</param>
    /// <returns>The start of the text.</returns>
    private static string CodePoints(string text, int count)
    {
        var builder = new StringBuilder();
        int taken = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (taken++ == count)
            {
                break;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    /// <summary>Classifies and stores a capture (a live copy), returning its id.</summary>
    /// <param name="store">The store.</param>
    /// <param name="capture">The capture.</param>
    /// <returns>The entry's id.</returns>
    private static long Add(ClipStore store, ClipCapture capture) =>
        store.Upsert(capture, ContentClassifier.Classify(capture)!, null, bumpIfExists: true)!.Entry.Id;

    /// <summary>Runs a statement on the test database directly, the way an earlier version left its rows.</summary>
    /// <param name="sql">The statement.</param>
    /// <param name="parameters">Its parameters.</param>
    private void Raw(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection($"Data Source={temp.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }
}
