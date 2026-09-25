using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the SQLite store: schema, dedupe/merge semantics, import suppression, search, filters,
/// ordering and retention — the guarantees that make history "not forget".
/// </summary>
public sealed class ClipStoreTests : IDisposable
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private readonly ClipStore store;

    /// <summary>Creates a fresh store per test.</summary>
    public ClipStoreTests()
    {
        store = new ClipStore(temp.DatabasePath);
        store.Initialize();
    }

    /// <summary>Deletes the temp database.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>Initialization is idempotent and survives reopening (persistence across restarts).</summary>
    [Fact]
    public void Initialize_IsIdempotentAndPersistent()
    {
        Add(TestData.Text("persist me"));
        store.Initialize();
        var reopened = new ClipStore(temp.DatabasePath);
        reopened.Initialize();
        Assert.Equal("persist me", reopened.Query(new ClipQuery()).Single().Preview);
    }

    /// <summary>A live copy of existing content bumps it instead of duplicating, and replaces its formats.</summary>
    [Fact]
    public void Upsert_LiveDuplicate_BumpsAndReplacesFormats()
    {
        var first = Add(TestData.Text("same", TestData.Now.AddMinutes(-10)))!;
        Add(TestData.Text("other", TestData.Now.AddMinutes(-5)));
        var again = store.Upsert(TestData.Rich("same", "<b>same</b>"), ContentClassifier.Classify(TestData.Rich("same", "<b>same</b>"))!, null, bumpIfExists: true)!;

        Assert.False(again.IsNew);
        Assert.True(again.Changed);
        Assert.Equal(first.Entry.Id, again.Entry.Id);
        Assert.Equal(2, again.Entry.UseCount);
        Assert.Equal(ClipKind.RichText, again.Entry.Kind);
        Assert.Equal([ClipFormatNames.UnicodeText, ClipFormatNames.Html], store.GetFormats(first.Entry.Id).Select(f => f.Name));
        Assert.Equal(2, store.GetStats().Count);
    }

    /// <summary>Imports never reorder existing entries, but can add a pin.</summary>
    [Fact]
    public void Upsert_ImportOfExisting_OnlyAddsPin()
    {
        var live = Add(TestData.Text("pinned in win+v", TestData.Now))!;
        var import = TestData.Text("pinned in win+v", TestData.Now.AddDays(-3), ClipOrigin.WindowsHistory, pin: true);
        var result = store.Upsert(import, ContentClassifier.Classify(import)!, null, bumpIfExists: false)!;

        Assert.False(result.IsNew);
        Assert.True(result.Changed);
        Assert.True(result.Entry.IsPinned);
        Assert.Equal(live.Entry.LastUsedUtc, result.Entry.LastUsedUtc);
        Assert.Equal(1, result.Entry.UseCount);

        var noop = store.Upsert(import, ContentClassifier.Classify(import)!, null, bumpIfExists: false)!;
        Assert.False(noop.Changed);
    }

    /// <summary>Deleted content is tombstoned: re-import is suppressed, but an explicit new copy restores it.</summary>
    [Fact]
    public void Delete_TombstonesAgainstImportOnly()
    {
        var entry = Add(TestData.Text("secret"))!.Entry;
        Assert.True(store.Delete(entry.Id, TestData.Now));
        Assert.Empty(store.GetFormats(entry.Id));

        var import = TestData.Text("secret", TestData.Now.AddMinutes(1), ClipOrigin.WindowsHistory);
        Assert.Null(store.Upsert(import, ContentClassifier.Classify(import)!, null, bumpIfExists: false));

        Assert.NotNull(Add(TestData.Text("secret", TestData.Now.AddMinutes(2))));
        Assert.NotNull(store.Upsert(import, ContentClassifier.Classify(import)!, null, bumpIfExists: false));
    }

    /// <summary>"Clear" keeps pins and suppresses imports of anything copied before the clear.</summary>
    [Fact]
    public void ClearUnpinned_KeepsPinsAndSuppressesOlderImports()
    {
        var pinned = Add(TestData.Text("keep", pin: true))!.Entry;
        Add(TestData.Text("drop"));
        Assert.Equal(1, store.ClearUnpinned(TestData.Now.AddMinutes(1)));
        Assert.Equal(pinned.Id, store.Query(new ClipQuery()).Single().Id);

        var older = TestData.Text("old import", TestData.Now, ClipOrigin.WindowsHistory);
        Assert.Null(store.Upsert(older, ContentClassifier.Classify(older)!, null, bumpIfExists: false));
        var newer = TestData.Text("new import", TestData.Now.AddMinutes(2), ClipOrigin.WindowsHistory);
        Assert.NotNull(store.Upsert(newer, ContentClassifier.Classify(newer)!, null, bumpIfExists: false));
    }

    /// <summary>Pinned entries sort first by default; otherwise pure recency.</summary>
    [Fact]
    public void Query_OrdersPinnedFirstThenRecency()
    {
        var old = Add(TestData.Text("old", TestData.Now.AddHours(-2)))!.Entry;
        Add(TestData.Text("new", TestData.Now));
        store.SetPinned(old.Id, true, TestData.Now);

        Assert.Equal(["old", "new"], store.Query(new ClipQuery()).Select(e => e.Preview));
        Assert.Equal(["new", "old"], store.Query(new ClipQuery { PinnedFirst = false }).Select(e => e.Preview));
    }

    /// <summary>The trigram index gives substring search, case-insensitively, across languages.</summary>
    [Fact]
    public void Query_SearchIsSubstringAndCaseInsensitive()
    {
        Add(TestData.Text("The Windows Clipboard forgets", TestData.Now.AddMinutes(-3)));
        Add(TestData.Text("שלום עולם מהלוח", TestData.Now.AddMinutes(-2)));
        Add(TestData.Text("unrelated", TestData.Now.AddMinutes(-1)));

        Assert.Equal("The Windows Clipboard forgets", store.Query(new ClipQuery { SearchText = "BOARD" }).Single().Preview);
        Assert.Equal("שלום עולם מהלוח", store.Query(new ClipQuery { SearchText = "עולם" }).Single().Preview);
        Assert.Single(store.Query(new ClipQuery { SearchText = "windows forg" }));
        Assert.Empty(store.Query(new ClipQuery { SearchText = "windows nothing" }));
    }

    /// <summary>Terms shorter than a trigram fall back to LIKE, with wildcards matched literally.</summary>
    [Fact]
    public void Query_ShortTermsAndWildcardsAreLiteral()
    {
        Add(TestData.Text("50% off", TestData.Now.AddMinutes(-2)));
        Add(TestData.Text("500 items", TestData.Now.AddMinutes(-1)));

        Assert.Equal("50% off", store.Query(new ClipQuery { SearchText = "0%" }).Single().Preview);
        Assert.Equal(2, store.Query(new ClipQuery { SearchText = "50" }).Count);
    }

    /// <summary>FTS operators typed by the user are literal text, never syntax errors.</summary>
    [Fact]
    public void Query_FtsOperatorsAreLiteral()
    {
        Add(TestData.Text("say \"NEAR\" AND mean it"));
        Assert.Single(store.Query(new ClipQuery { SearchText = "\"NEAR\"" }));
        Assert.Empty(store.Query(new ClipQuery { SearchText = "OR* -x:y" }));
    }

    /// <summary>Filter tabs select the right kinds.</summary>
    [Fact]
    public void Query_FiltersByKind()
    {
        Add(TestData.Text("text"));
        Add(TestData.Text("https://example.com"));
        Add(TestData.Text("#ffffff"));
        Add(TestData.Files(@"C:\a.txt"));
        Add(TestData.Image(3));

        Assert.Equal(2, store.Query(new ClipQuery { Filter = ClipFilter.Text }).Count);
        Assert.Single(store.Query(new ClipQuery { Filter = ClipFilter.Links }));
        Assert.Single(store.Query(new ClipQuery { Filter = ClipFilter.Files }));
        Assert.Single(store.Query(new ClipQuery { Filter = ClipFilter.Images }));
        Assert.Empty(store.Query(new ClipQuery { Filter = ClipFilter.Pinned }));
    }

    /// <summary>File-list clips are searchable by path.</summary>
    [Fact]
    public void Query_FindsFilesByPath()
    {
        Add(TestData.Files(@"C:\Reports\quarterly.xlsx"));
        Assert.Single(store.Query(new ClipQuery { SearchText = "quarterly" }));
    }

    /// <summary>Paging returns disjoint, ordered pages.</summary>
    [Fact]
    public void Query_Pages()
    {
        for (int i = 0; i < 10; i++)
        {
            Add(TestData.Text($"item {i}", TestData.Now.AddMinutes(i)));
        }

        var first = store.Query(new ClipQuery { Limit = 4 });
        var second = store.Query(new ClipQuery { Limit = 4, Offset = 4 });
        Assert.Equal(["item 9", "item 8", "item 7", "item 6"], first.Select(e => e.Preview));
        Assert.Equal("item 5", second[0].Preview);
    }

    /// <summary>Retention by count keeps the most recently used unpinned entries and all pins.</summary>
    [Fact]
    public void Prune_ByCount()
    {
        var pin = Add(TestData.Text("pin", TestData.Now.AddDays(-100), pin: true))!.Entry;
        for (int i = 0; i < 5; i++)
        {
            Add(TestData.Text($"n{i}", TestData.Now.AddMinutes(i)));
        }

        Assert.Equal(3, store.Prune(new RetentionPolicy(2, null, 0), TestData.Now));
        var remaining = store.Query(new ClipQuery()).Select(e => e.Preview).ToList();
        Assert.Equal(["pin", "n4", "n3"], remaining);
        Assert.True(store.GetEntry(pin.Id)!.IsPinned);
    }

    /// <summary>Retention by age drops stale unpinned entries only.</summary>
    [Fact]
    public void Prune_ByAge()
    {
        Add(TestData.Text("ancient", TestData.Now.AddDays(-40)));
        Add(TestData.Text("ancient pin", TestData.Now.AddDays(-40), pin: true));
        Add(TestData.Text("fresh", TestData.Now.AddDays(-1)));

        Assert.Equal(1, store.Prune(new RetentionPolicy(0, TimeSpan.FromDays(30), 0), TestData.Now));
        Assert.Equal(["ancient pin", "fresh"], store.Query(new ClipQuery()).Select(e => e.Preview));
    }

    /// <summary>Retention by total size drops the least recently used entries first.</summary>
    [Fact]
    public void Prune_BySize()
    {
        Add(new ClipCapture { Formats = TestData.Image(1, 1000).Formats, CapturedAtUtc = TestData.Now.AddMinutes(-5), Source = TestData.Notepad });
        Add(new ClipCapture { Formats = TestData.Image(2, 1000).Formats, CapturedAtUtc = TestData.Now, Source = TestData.Notepad });

        Assert.Equal(1, store.Prune(new RetentionPolicy(0, null, 1500), TestData.Now));
        var survivor = store.Query(new ClipQuery()).Single();
        Assert.Equal(ContentHasher.ForImage(Enumerable.Repeat((byte)2, 1000).ToArray()), survivor.ContentHash);
    }

    /// <summary>Touch moves an entry to the top and counts the use; it never moves recency backwards.</summary>
    [Fact]
    public void Touch_BumpsRecency()
    {
        var a = Add(TestData.Text("a", TestData.Now.AddMinutes(-5)))!.Entry;
        Add(TestData.Text("b", TestData.Now));
        store.Touch(a.Id, TestData.Now.AddMinutes(1));
        var top = store.Query(new ClipQuery()).First();
        Assert.Equal(a.Id, top.Id);
        Assert.Equal(2, top.UseCount);
    }

    /// <summary>Thumbnails and image dimensions are stored and the preview carries the size.</summary>
    [Fact]
    public void Upsert_StoresImageAnalysis()
    {
        var capture = TestData.Image(9);
        var result = store.Upsert(capture, ContentClassifier.Classify(capture)!, new ImageAnalysis(1920, 1080, [1, 2, 3]), bumpIfExists: true)!;
        Assert.True(result.Entry.HasThumbnail);
        Assert.Equal("Image · 1920 × 1080", result.Entry.Preview);
        Assert.Equal([1, 2, 3], store.GetThumbnail(result.Entry.Id));
    }

    /// <summary>Stats count entries, pins and payload bytes.</summary>
    [Fact]
    public void Stats_Aggregate()
    {
        Add(TestData.Text("ab", pin: true));
        Add(TestData.Text("cd"));
        var stats = store.GetStats();
        Assert.Equal(2, stats.Count);
        Assert.Equal(1, stats.PinnedCount);
        Assert.Equal(12, stats.TotalBytes); // 2 × ("xx" + NUL) × 2 bytes
    }

    /// <summary>A database from a newer schema is refused rather than corrupted.</summary>
    [Fact]
    public void Initialize_RefusesNewerSchema()
    {
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidOperationException>(() => new ClipStore(temp.DatabasePath).Initialize());
    }

    /// <summary>
    /// v0.1.0's made-up "Windows clipboard history" source is cleared from imported rows exactly once;
    /// a live copy that really came from an app with that name keeps it.
    /// </summary>
    [Fact]
    public void Initialize_ClearsLegacyImportSourceOnce()
    {
        var legacy = new SourceAppInfo("Windows", null, "Windows clipboard history");
        var imported = Add(TestData.Text("imported", origin: ClipOrigin.WindowsHistory, source: legacy))!.Entry;
        var pinned = Add(TestData.Text("pinned import", origin: ClipOrigin.WindowsPinned, pin: true, source: legacy))!.Entry;
        var live = Add(TestData.Text("live", source: new SourceAppInfo("x", @"C:\x.exe", "Windows clipboard history")))!.Entry;

        // The fix-up already ran when the fixture created this database; forget that to replay an old file.
        ExecuteRaw("DELETE FROM meta WHERE key = 'fixup.import_source.v1';");
        new ClipStore(temp.DatabasePath).Initialize();
        Assert.Null(store.GetEntry(imported.Id)!.SourceAppName);
        Assert.Null(store.GetEntry(pinned.Id)!.SourceAppName);
        Assert.Equal("Windows clipboard history", store.GetEntry(live.Id)!.SourceAppName);

        // Once applied it never runs again (a later row keeps whatever it was given).
        var later = Add(TestData.Text("later import", origin: ClipOrigin.WindowsHistory, source: legacy))!.Entry;
        new ClipStore(temp.DatabasePath).Initialize();
        Assert.Equal("Windows clipboard history", store.GetEntry(later.Id)!.SourceAppName);
    }

    /// <summary>The grep scan returns search text newest first, per filter, skipping text-less images.</summary>
    [Fact]
    public void GetSearchTexts_NewestFirstPerFilter()
    {
        Add(TestData.Text("older", TestData.Now.AddMinutes(-2)));
        Add(TestData.Files(@"C:\f.txt"));
        Add(TestData.Image(9));
        Add(TestData.Text("newer", TestData.Now.AddMinutes(-1)));

        Assert.Equal(["C:\\f.txt", "newer", "older"], store.GetSearchTexts(ClipFilter.All, 10).Select(t => t.Text));
        Assert.Equal(["newer", "older"], store.GetSearchTexts(ClipFilter.Text, 10).Select(t => t.Text));
        Assert.Single(store.GetSearchTexts(ClipFilter.All, 1));
    }

    /// <summary>UsedSince keeps only entries used at or after the instant.</summary>
    [Fact]
    public void Query_UsedSince()
    {
        Add(TestData.Text("two hours ago", TestData.Now.AddHours(-2)));
        Add(TestData.Text("just now", TestData.Now));
        Assert.Equal("just now", store.Query(new ClipQuery { UsedSince = TestData.Now.AddHours(-1) }).Single().Preview);
        Assert.Equal(2, store.Query(new ClipQuery { UsedSince = TestData.Now.AddHours(-2) }).Count);
    }

    /// <summary>
    /// The ShareX tab shows both ways ShareX content arrives — screenshots from its folders and copies whose
    /// source app is ShareX.exe (path, any case, or just the name when the path was unavailable) — and
    /// nothing else, not even an app whose name merely ends in "ShareX".
    /// </summary>
    [Fact]
    public void Query_ShareXFilter_MatchesOriginOrSourceApp()
    {
        Add(TestData.ShareXShot(1, TestData.Now.AddMinutes(-4)));
        Add(TestData.Text("copied from ShareX", TestData.Now.AddMinutes(-3), source: TestData.ShareX));
        Add(TestData.Text("elevated ShareX", TestData.Now.AddMinutes(-2), source: new SourceAppInfo("ShareX", null, "ShareX")));
        Add(TestData.Text("upper case path", TestData.Now.AddMinutes(-1), source: new SourceAppInfo("sharex", @"D:\Tools\SHAREX.EXE", "Portable")));
        Add(TestData.Text("look-alike", TestData.Now, source: new SourceAppInfo("NotShareX", @"C:\Tools\NotShareX.exe", "NotShareX")));
        Add(TestData.Text("notepad", TestData.Now));

        var shareX = store.Query(new ClipQuery { Filter = ClipFilter.ShareX, PinnedFirst = false });
        Assert.Equal(["upper case path", "elevated ShareX", "copied from ShareX", ContentClassifier.DescribeImage(null, null)], shareX.Select(e => e.Preview));
        Assert.Equal(ClipOrigin.ShareX, shareX[^1].Origin);
        Assert.Equal(6, store.Query(new ClipQuery()).Count);
    }

    /// <summary>
    /// A ShareX screenshot bumps an existing duplicate like a live copy (the clipboard copy ShareX made a
    /// moment earlier), but never lifts a tombstone and never survives a clear — unlike a live copy.
    /// </summary>
    [Fact]
    public void Upsert_ShareXShot_BumpsButRespectsTombstonesAndClears()
    {
        var copied = Add(TestData.Image(7))!.Entry;
        var shot = TestData.ShareXShot(7, TestData.Now.AddSeconds(1));
        var merged = store.Upsert(shot, ContentClassifier.Classify(shot)!, null, bumpIfExists: true)!;
        Assert.Equal(copied.Id, merged.Entry.Id);
        Assert.Equal(2, merged.Entry.UseCount);
        Assert.Equal(TestData.Now.AddSeconds(1), merged.Entry.LastUsedUtc);

        Assert.True(store.Delete(copied.Id, TestData.Now.AddMinutes(1)));
        var again = TestData.ShareXShot(7, TestData.Now.AddMinutes(2));
        Assert.Null(store.Upsert(again, ContentClassifier.Classify(again)!, null, bumpIfExists: true));

        store.ClearUnpinned(TestData.Now.AddMinutes(10));
        var beforeClear = TestData.ShareXShot(8, TestData.Now.AddMinutes(9));
        Assert.Null(store.Upsert(beforeClear, ContentClassifier.Classify(beforeClear)!, null, bumpIfExists: true));
        var afterClear = TestData.ShareXShot(8, TestData.Now.AddMinutes(11));
        Assert.NotNull(store.Upsert(afterClear, ContentClassifier.Classify(afterClear)!, null, bumpIfExists: true));
    }

    /// <summary>
    /// Integration state round-trips, persists, and is namespaced: a caller cannot overwrite the store's own
    /// keys (here <c>last_clear_utc</c>, which drives import suppression) or use arbitrary names.
    /// </summary>
    [Fact]
    public void StateValues_RoundTripPersistAndAreNamespaced()
    {
        Assert.Null(store.GetStateValue("sharex.last_seen_utc"));
        store.SetStateValue("sharex.last_seen_utc", "2026-09-25T12:00:00.0000000Z");
        Assert.Equal("2026-09-25T12:00:00.0000000Z", store.GetStateValue("sharex.last_seen_utc"));
        store.SetStateValue("sharex.last_seen_utc", string.Empty);
        Assert.Equal(string.Empty, store.GetStateValue("sharex.last_seen_utc"));

        store.SetStateValue("sharex.last_seen_utc", "kept");
        var reopened = new ClipStore(temp.DatabasePath);
        reopened.Initialize();
        Assert.Equal("kept", reopened.GetStateValue("sharex.last_seen_utc"));

        store.SetStateValue("last_clear_utc", long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var import = TestData.Text("still importable", TestData.Now, ClipOrigin.WindowsHistory);
        Assert.NotNull(store.Upsert(import, ContentClassifier.Classify(import)!, null, bumpIfExists: false));

        Assert.Throws<ArgumentException>(() => store.SetStateValue(" ", "x"));
        Assert.Throws<ArgumentException>(() => store.SetStateValue("with space", "x"));
        Assert.Throws<ArgumentException>(() => store.GetStateValue("semi;colon"));
        Assert.Throws<ArgumentException>(() => store.SetStateValue(new string('a', 65), "x"));
        Assert.Throws<ArgumentNullException>(() => store.SetStateValue("sharex.x", null!));
    }

    /// <summary>Runs SQL directly against the (plaintext test) database.</summary>
    /// <param name="sql">Statement.</param>
    private void ExecuteRaw(string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Classifies and stores a capture as a live copy (or import, per its origin).</summary>
    /// <param name="capture">The capture.</param>
    /// <returns>The upsert result.</returns>
    private UpsertResult? Add(ClipCapture capture) =>
        store.Upsert(capture, ContentClassifier.Classify(capture)!, null, bumpIfExists: capture.Origin == ClipOrigin.Captured);
}
