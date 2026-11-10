using BetterClipboard.Core.Content;
using BetterClipboard.Core.Integrations;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// <i>Hide tab &amp; clipboard rows</i> (CLAUDE.md §2.42): the setting that remembers which hidden tabs hide their rows
/// (<see cref="AppSettings.HiddenTabRows"/>), the store leaving those rows out of a query (<see cref="ClipQuery.HiddenRows"/>)
/// — everywhere, except entries in a group — and counting them for the footer (<see cref="StoreStats.HiddenCount"/>).
/// </summary>
public sealed class HiddenTabRowsTests : IDisposable
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private readonly ClipStore store;

    /// <summary>Creates a store over a temp database.</summary>
    public HiddenTabRowsTests()
    {
        store = new ClipStore(temp.DatabasePath);
        store.Initialize();
    }

    /// <summary>Deletes the temp database.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>A new install, and a settings file from before this member, hide no tab's rows.</summary>
    [Fact]
    public void Settings_DefaultAndFileFromBefore_AreEmpty()
    {
        Assert.Empty(new SettingsStore(Path.Combine(temp.Path, "fresh.json")).Load().HiddenTabRows);

        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ \"HiddenTabs\": [ \"Run\" ], \"MaxItems\": 50 }");
        var old = new SettingsStore(path).Load();
        Assert.Equal(["Run"], old.HiddenTabs);
        Assert.Empty(old.HiddenTabRows);
    }

    /// <summary>
    /// Only tabs that are hidden themselves keep their rows hidden — a tab in the row never misses rows of its own — named
    /// as their filter, each once; unknown names and "All" are dropped. Normalizing twice changes nothing.
    /// </summary>
    [Fact]
    public void Settings_Normalize_KeepsOnlyHiddenTabs()
    {
        var settings = new AppSettings
        {
            HiddenTabs = ["run", "Snipping", "All", "Bogus"],
            HiddenTabRows = ["Run", "text", "all", "Bogus", " snipping ", "RUN", null!, ""],
        }.Normalize();

        Assert.Equal(["Run", "Snipping"], settings.HiddenTabs);
        Assert.Equal(["Run", "Snipping"], settings.HiddenTabRows);
        Assert.Equal(settings.HiddenTabRows, settings.Normalize().HiddenTabRows);
    }

    /// <summary>The tabs hidden with their rows survive a save and a load, next to the hidden tabs.</summary>
    [Fact]
    public void Settings_RoundTrip()
    {
        var path = Path.Combine(temp.Path, "settings.json");
        var settings = new SettingsStore(path);
        settings.Load();
        settings.Update(s => s with { HiddenTabs = ["Images", "NotepadPlusPlus"], HiddenTabRows = ["NotepadPlusPlus"] });

        var loaded = new SettingsStore(path).Load();
        Assert.Equal(["Images", "NotepadPlusPlus"], loaded.HiddenTabs);
        Assert.Equal(["NotepadPlusPlus"], loaded.HiddenTabRows);
    }

    /// <summary>
    /// A tab's rows leave every list: All, the other tabs that list them by kind, and searches — pinned ones too — while an
    /// entry in a group stays wherever it shows, its group view included (the owner's choice, 2026-11-09). Entries of other
    /// tabs are untouched, and no rows are hidden without a tab named.
    /// </summary>
    [Fact]
    public void Query_LeavesTheTabsRowsOut_ExceptGroupedOnes()
    {
        long run = Add(RunMru.CreateCapture("BC-TEST notepad report.txt", TestData.Now, observedRun: true));
        long pinnedRun = Add(RunMru.CreateCapture("BC-TEST calc report", TestData.Now.AddMinutes(1), observedRun: true));
        Assert.True(store.SetPinned(pinnedRun, pinned: true, TestData.Now.AddMinutes(1)));
        long groupedRun = Add(RunMru.CreateCapture("BC-TEST winver report", TestData.Now.AddMinutes(2), observedRun: true));
        var work = store.CreateGroup("Work", GroupIconCatalog.All[0].Glyph, TestData.Now);
        Assert.True(store.AddToGroup(groupedRun, work.Id, TestData.Now.AddMinutes(2)));
        long copy = Add(TestData.Text("BC-TEST report from Notepad", TestData.Now.AddMinutes(3)));
        long shot = Add(Shot(1, TestData.Now.AddMinutes(4)));

        IReadOnlyList<ClipFilter> hideRun = [ClipFilter.Run];
        Assert.Equal([shot, copy, groupedRun], Ids(new ClipQuery { PinnedFirst = false, HiddenRows = hideRun }));
        Assert.Equal([copy, groupedRun], Ids(new ClipQuery { Filter = ClipFilter.Text, HiddenRows = hideRun }));
        Assert.Empty(Ids(new ClipQuery { Filter = ClipFilter.Pinned, HiddenRows = hideRun }));
        Assert.Equal([copy, groupedRun], Ids(new ClipQuery { SearchText = "report", PinnedFirst = false, HiddenRows = hideRun }));
        Assert.Equal([groupedRun], Ids(new ClipQuery { GroupIds = [work.Id], HiddenRows = hideRun }));

        // Nothing hidden: every entry, as before.
        Assert.Equal([pinnedRun, shot, copy, groupedRun, run], Ids(new ClipQuery()));
        Assert.Equal(5, Ids(new ClipQuery { HiddenRows = [] }).Count);
        Assert.Equal(5, Ids(new ClipQuery { HiddenRows = [ClipFilter.All] }).Count);

        // Two tabs at once: their rows together.
        Assert.Equal([copy, groupedRun], Ids(new ClipQuery { PinnedFirst = false, HiddenRows = [ClipFilter.Run, ClipFilter.Snipping] }));
    }

    /// <summary>
    /// An entry a tab's filter cannot decide about (a NULL source path for ShareX's <c>LIKE</c>, an undecided path count for
    /// Files) is not that tab's, so it stays listed: <c>NOT NULL</c> would have dropped it from every list.
    /// </summary>
    [Fact]
    public void Query_AnEntryTheFilterCannotDecide_StaysListed()
    {
        long noPath = Add(TestData.Text("BC-TEST no source path", TestData.Now, source: new SourceAppInfo("tool", null, "Tool")));
        long undecided = Add(TestData.Text("BC-TEST undecided", TestData.Now.AddMinutes(1)));
        long path = Add(TestData.Text(@"C:\BC-TEST\file.txt", TestData.Now.AddMinutes(2)));
        long sharex = Add(TestData.ShareXShot(2, TestData.Now.AddMinutes(3)));
        ExecuteRaw($"UPDATE clips SET path_count = NULL WHERE id = {undecided};");

        Assert.Equal([path, undecided, noPath], Ids(new ClipQuery { PinnedFirst = false, HiddenRows = [ClipFilter.ShareX] }));
        Assert.Equal([sharex, undecided, noPath], Ids(new ClipQuery { PinnedFirst = false, HiddenRows = [ClipFilter.Files] }));
    }

    /// <summary>
    /// The footer's count: the entries the panel leaves out, grouped ones not counted (they stay shown); the total still
    /// counts everything; nothing is counted when no tab is named.
    /// </summary>
    [Fact]
    public void GetStats_CountsTheHiddenRows()
    {
        Add(RunMru.CreateCapture("BC-TEST one", TestData.Now, observedRun: true));
        Add(RunMru.CreateCapture("BC-TEST two", TestData.Now.AddMinutes(1), observedRun: true));
        long grouped = Add(RunMru.CreateCapture("BC-TEST three", TestData.Now.AddMinutes(2), observedRun: true));
        var work = store.CreateGroup("Work", GroupIconCatalog.All[0].Glyph, TestData.Now);
        Assert.True(store.AddToGroup(grouped, work.Id, TestData.Now));
        Add(TestData.Text("BC-TEST copy", TestData.Now.AddMinutes(3)));

        var stats = store.GetStats([ClipFilter.Run]);
        Assert.Equal(4, stats.Count);
        Assert.Equal(2, stats.HiddenCount);
        Assert.Equal(0, store.GetStats().HiddenCount);
        Assert.Equal(0, store.GetStats([]).HiddenCount);
        Assert.Equal(2, store.GetStats([ClipFilter.Run, ClipFilter.Run]).HiddenCount);
        Assert.Equal(3, store.GetStats([ClipFilter.Run, ClipFilter.Text]).HiddenCount);
    }

    /// <summary>The ids a query returns, in its order.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The ids.</returns>
    private List<long> Ids(ClipQuery query) => store.Query(query).Select(e => e.Id).ToList();

    /// <summary>
    /// Stores a capture as the history service does for its origin: a new event bumps a duplicate, an import leaves it
    /// alone (Windows' Win+R list read at the first activation).
    /// </summary>
    /// <param name="capture">The capture.</param>
    /// <returns>The entry's id.</returns>
    private long Add(ClipCapture capture) =>
        store.Upsert(capture, ContentClassifier.Classify(capture)!, null, bumpIfExists: capture.Origin != ClipOrigin.RunDialogHistory)!.Entry.Id;

    /// <summary>A screenshot file as the Screenshots folder watch delivers it (fake PNG bytes: only hashing matters here).</summary>
    /// <param name="seed">Varies the bytes: the same seed is the same picture.</param>
    /// <param name="at">File write time.</param>
    /// <returns>The capture.</returns>
    private static ClipCapture Shot(byte seed, DateTimeOffset at) => new()
    {
        Formats = [new ClipFormatData(ClipFormatNames.Png, Enumerable.Repeat(seed, 64).ToArray())],
        CapturedAtUtc = at,
        Origin = ClipOrigin.WindowsScreenshot,
        Source = WindowsScreenshots.WinPrtScnSource,
    };

    /// <summary>Runs SQL directly against the (plaintext test) database, to make a row an older build could have left.</summary>
    /// <param name="sql">Statement.</param>
    private void ExecuteRaw(string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
