using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the size budget's warnings and what they stand on (CLAUDE.md §2.60): the levels (90 %, then every 2 %, and
/// full), each warned once and again only after the usage fell a whole step; the store's measure of what the limits count,
/// its prune that reports removals for the budget, the largest-first listing and the delete of many ids; and the history
/// service that raises each warning once, also across a restart.
/// </summary>
public sealed class HistoryBudgetTests : IDisposable
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();

    /// <summary>Deletes the temp store.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>The levels: 90 to 98 in steps of 2 (rounded down), full from 99.5 % or after a removal for the budget.</summary>
    /// <param name="percent">How full the budget is.</param>
    /// <param name="removedForSize">Whether the prune removed items to fit the budget.</param>
    /// <param name="expected">The level, or <see langword="null"/> below 90 %.</param>
    [Theory]
    [MemberData(nameof(Levels))]
    public void LevelOf_StepsOfTwoFromNinety(double percent, bool removedForSize, int? expected) =>
        Assert.Equal(expected, HistoryBudgetWarnings.LevelOf(percent, removedForSize));

    /// <summary>The level rows, each with what it pins.</summary>
    /// <returns>Percent, a removal for the budget, and the level expected.</returns>
    public static TheoryData<double, bool, int?> Levels() => new()
    {
        // Below the first level: nothing, also for a usage that cannot be told (NaN).
        { 0, false, null },
        { 89.999, false, null },
        { double.NaN, false, null },

        // Each step from its exact value, rounded down until the next.
        { 90, false, 90 },
        { 91.99, false, 90 },
        { 92, false, 92 },
        { 95.5, false, 94 },
        { 97.999, false, 96 },
        { 98, false, 98 },
        { 99.49, false, 98 },

        // Full: from 99.5 %, past 100 % before the prune, or whenever the prune removed items for the budget.
        { 99.5, false, 100 },
        { 140, false, 100 },
        { 12, true, 100 },
        { double.NaN, true, 100 },
    };

    /// <summary>
    /// A walk up and down: each level is warned once on the way up; a jump warns the level reached, not those it passed;
    /// a full budget that keeps removing stays quiet; a usage that fell a whole step below the mark warns again on the way
    /// back up; and without a budget nothing is warned and the mark is left alone.
    /// </summary>
    [Fact]
    public void Decide_WarnsEachLevelOnceAndAgainAfterAFall()
    {
        const long budget = 1_000_000;
        int? mark = null;

        // Steps the usage to the given per mille of the budget and returns the level warned, if any.
        int? Step(long perMille, bool removed = false)
        {
            var decision = HistoryBudgetWarnings.Decide(mark, budget * perMille / 1000, budget, removed);
            mark = decision.Mark;
            return decision.Warn;
        }

        Assert.Null(Step(850));
        Assert.Equal(90, Step(905));
        Assert.Null(Step(915));
        Assert.Equal(92, Step(921));
        Assert.Null(Step(925));
        Assert.Equal(96, Step(970)); // a jump: one warning, at the level reached
        Assert.Equal(100, Step(996));
        Assert.Null(Step(990, removed: true)); // still full, still removing: told once
        Assert.Equal(100, mark);
        Assert.Null(Step(850, removed: true)); // one large item removed: far below, but it was full, so the mark stays
        Assert.Equal(100, mark);

        // Back down within a step of the mark: still marked, nothing new.
        Assert.Null(Step(985));
        Assert.Equal(100, mark);

        // Fallen more than a step: the mark follows the usage, and climbing back warns again.
        Assert.Null(Step(930));
        Assert.Equal(92, mark);
        Assert.Null(Step(931));
        Assert.Equal(94, Step(941));
        Assert.Null(Step(600));
        Assert.Null(mark);
        Assert.Equal(90, Step(900));

        // No budget: nothing warned, the mark kept for when one is set again.
        Assert.Equal(new BudgetWarningDecision(null, 90), HistoryBudgetWarnings.Decide(90, 5_000_000, 0, removedForSize: true));
        Assert.Equal(0.0, HistoryBudgetWarnings.PercentUsed(10, 0));
        Assert.Equal(0.0, HistoryBudgetWarnings.PercentUsed(-10, 100));
    }

    /// <summary>The mark as stored: only the levels themselves read back; anything else is "nothing warned".</summary>
    [Fact]
    public void Mark_FormatAndParse()
    {
        Assert.Equal(string.Empty, HistoryBudgetWarnings.Format(null));
        Assert.Equal("96", HistoryBudgetWarnings.Format(96));
        Assert.Equal(94, HistoryBudgetWarnings.Parse("94"));
        Assert.Equal(100, HistoryBudgetWarnings.Parse("100"));
        foreach (var junk in new[] { null, "", " 94", "89", "101", "-90", "9 4", "x", "94.0" })
        {
            Assert.Null(HistoryBudgetWarnings.Parse(junk));
        }

        Assert.True(new HistoryBudgetWarning(100, 1, 1).IsFull);
        Assert.False(new HistoryBudgetWarning(98, 1, 1).IsFull);
    }

    /// <summary>
    /// What the limits count: entries neither pinned nor grouped, their bytes, the one the age limit reaches first, the
    /// largest entry of all, and the database file; pinned and grouped ones apart.
    /// </summary>
    [Fact]
    public void RetentionStats_CountWhatTheLimitsCount()
    {
        var store = NewStore();
        var empty = store.GetRetentionStats();
        Assert.Equal((0L, 0L, 0L, 0L, (DateTimeOffset?)null, 0L), (empty.CountedItems, empty.CountedBytes, empty.ProtectedItems, empty.ProtectedBytes, empty.OldestCountedUseUtc, empty.LargestItemBytes));
        Assert.True(empty.DatabaseBytes > 0);

        var oldest = Add(store, Text("oldest", 300, -50));
        var middle = Add(store, Text("middle", 200, -40));
        var newest = Add(store, Text("newest", 100, -30));
        var pinned = Add(store, Text("pinned", 900, -60, pin: true));
        var grouped = Add(store, Text("grouped", 800, -70));
        var group = store.CreateGroup("Work", GroupIconCatalog.All[0].Glyph, TestData.Now);
        Assert.True(store.AddToGroup(grouped.Id, group.Id, TestData.Now));

        var stats = store.GetRetentionStats();
        Assert.Equal(3, stats.CountedItems);
        Assert.Equal(oldest.SizeBytes + middle.SizeBytes + newest.SizeBytes, stats.CountedBytes);
        Assert.Equal(2, stats.ProtectedItems);
        Assert.Equal(pinned.SizeBytes + grouped.SizeBytes, stats.ProtectedBytes);
        Assert.Equal(oldest.LastUsedUtc, stats.OldestCountedUseUtc);
        Assert.Equal(pinned.SizeBytes, stats.LargestItemBytes); // the largest of all, pinned ones included
        Assert.True(stats.DatabaseBytes >= empty.DatabaseBytes);
    }

    /// <summary>
    /// The prune reports what the budget counts after it, and how many entries it removed for the budget: the least
    /// recently used first, never a pinned one. Without a budget it measures nothing.
    /// </summary>
    [Fact]
    public void PruneAndMeasure_ReportsTheBudgetsCount()
    {
        var store = NewStore();
        var oldest = Add(store, Text("oldest", 300, -50));
        var middle = Add(store, Text("middle", 300, -40));
        var newest = Add(store, Text("newest", 300, -30));
        Add(store, Text("pinned", 900, -60, pin: true));
        long all = oldest.SizeBytes + middle.SizeBytes + newest.SizeBytes;

        var none = store.PruneAndMeasure(new RetentionPolicy(0, null, 0), TestData.Now);
        Assert.Equal(new PruneResult(0, 0, null), none);

        var fits = store.PruneAndMeasure(new RetentionPolicy(0, null, all), TestData.Now);
        Assert.Equal(new PruneResult(0, 0, all), fits);

        var over = store.PruneAndMeasure(new RetentionPolicy(0, null, all - 1), TestData.Now);
        Assert.Equal(new PruneResult(1, 1, middle.SizeBytes + newest.SizeBytes), over);
        Assert.Null(store.GetEntry(oldest.Id));
        Assert.NotNull(store.GetEntry(middle.Id));

        // An item limit's removals are not the budget's: a prune that only enforces the count reports none for size.
        var byCount = store.PruneAndMeasure(new RetentionPolicy(1, null, all), TestData.Now);
        Assert.Equal((1, 0, newest.SizeBytes), (byCount.Removed, byCount.RemovedForSize, byCount.CountedBytes));
        Assert.Equal(1, store.Prune(new RetentionPolicy(0, null, 1), TestData.Now));
    }

    /// <summary>Largest first: by size, then the most recently used of equal sizes; filters and paging still apply.</summary>
    [Fact]
    public void Query_LargestFirst()
    {
        var store = NewStore();
        var small = Add(store, Text("small", 10, -10));
        var large = Add(store, Text("large", 900, -50));
        var mediumOld = Add(store, Text("medium old", 400, -40));
        var mediumNew = Add(store, Text("medium new", 400, -20));

        var order = store.Query(new ClipQuery { LargestFirst = true, PinnedFirst = true }, TestContext.Current.CancellationToken).Select(e => e.Id).ToList();
        Assert.Equal([large.Id, mediumNew.Id, mediumOld.Id, small.Id], order);
        Assert.Equal([mediumNew.Id, mediumOld.Id], store.Query(new ClipQuery { LargestFirst = true, Offset = 1, Limit = 2 }, TestContext.Current.CancellationToken).Select(e => e.Id));
        Assert.Equal([mediumNew.Id, mediumOld.Id], store.Query(new ClipQuery { LargestFirst = true, SearchText = "medium" }, TestContext.Current.CancellationToken).Select(e => e.Id));
    }

    /// <summary>
    /// Many ids in one call: each deleted once (repeats ignored), ids that do not exist skipped, every deleted content
    /// tombstoned against a later import (like a single delete), and an empty list a no-op.
    /// </summary>
    [Fact]
    public void DeleteMany_DeletesEachOnceAndTombstones()
    {
        var store = NewStore();
        var a = Add(store, Text("first", 50, -10));
        var b = Add(store, Text("second", 50, -9));
        var keep = Add(store, Text("kept", 50, -8));

        Assert.Empty(store.DeleteMany([], TestData.Now));
        var deleted = store.DeleteMany([a.Id, 987_654, b.Id, a.Id], TestData.Now);
        Assert.Equal([a.Id, b.Id], deleted);
        Assert.Equal([keep.Id], store.Query(new ClipQuery(), TestContext.Current.CancellationToken).Select(e => e.Id));

        var import = TestData.Text(Body("first", 50), TestData.Now.AddMinutes(1), ClipOrigin.WindowsHistory);
        Assert.Null(store.Upsert(import, ContentClassifier.Classify(import)!, null, bumpIfExists: false));
        Assert.Throws<ArgumentNullException>(() => store.DeleteMany(null!, TestData.Now));
    }

    /// <summary>
    /// The service raises each level once as copies fill the budget (90, 92, 94, 96, 98, then full), stores the mark in the
    /// history, and a restarted service stays quiet while the budget stays full; deleting below the levels arms it again.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Service_WarnsEachLevelOnce_AlsoAcrossARestart()
    {
        long budget = 0;
        var warnings = new List<HistoryBudgetWarning>();
        var store = NewStore();
        CaptureRules Rules() => new() { Retention = new RetentionPolicy(0, null, Interlocked.Read(ref budget)) };

        var service = Start(store, Rules, warnings);
        var first = (await service.AddAsync(Capture(0)))!;
        long size = first.SizeBytes;

        // Fifty copies of one size fill the budget: the 45th is 90 %, each further one 2 % more.
        Interlocked.Exchange(ref budget, size * 50);
        for (int i = 1; i < 50; i++)
        {
            await service.AddAsync(Capture(i));
        }

        Assert.Equal([90, 92, 94, 96, 98, 100], Levels(warnings));
        Assert.Equal((size * 50, size * 50), (warnings[^1].UsedBytes, warnings[^1].BudgetBytes));
        Assert.Equal("100", store.GetStateValue(HistoryBudgetWarnings.StateName));

        // Over the budget: the oldest copy goes, and the full warning is not repeated.
        await service.AddAsync(Capture(50));
        Assert.Equal(50, store.GetRetentionStats().CountedItems);
        Assert.Equal(6, warnings.Count);
        await service.DisposeAsync();

        // A restarted service reads the mark: still full, still removing, still quiet.
        warnings.Clear();
        service = Start(store, Rules, warnings);
        await service.AddAsync(Capture(51));
        Assert.Empty(warnings);

        // Deleting well below the first level arms it again; the next copies past 90 % warn again.
        var oldest = store.Query(new ClipQuery { Limit = 1000 }, TestContext.Current.CancellationToken).Select(e => e.Id).Skip(20).ToArray();
        Assert.Equal(30, (await service.DeleteManyAsync(oldest)).Count);
        await service.AddAsync(Capture(52));
        Assert.Empty(warnings);
        Assert.Equal(string.Empty, store.GetStateValue(HistoryBudgetWarnings.StateName));
        for (int i = 53; i < 77; i++)
        {
            await service.AddAsync(Capture(i));
        }

        Assert.Equal([90], Levels(warnings));
        await service.DisposeAsync();
    }

    /// <summary>
    /// A subscriber that throws, and a mark that cannot be read, never fail the copy whose prune measured: the copy is
    /// stored and the service keeps going.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Service_AWarningThatFails_NeverFailsTheCopy()
    {
        var store = NewStore();
        store.SetStateValue(HistoryBudgetWarnings.StateName, "not a level");
        long budget = 0;
        var service = new ClipHistoryService(store, null, () => new CaptureRules { Retention = new RetentionPolicy(0, null, Interlocked.Read(ref budget)) });
        service.BudgetWarning += (_, _) => throw new InvalidOperationException("BC-TEST subscriber");
        service.Start();
        try
        {
            var first = (await service.AddAsync(Capture(0)))!;
            Interlocked.Exchange(ref budget, first.SizeBytes);
            Assert.NotNull(await service.AddAsync(Capture(1)));
            Assert.NotNull(await service.AddAsync(Capture(2)));
            Assert.Equal("100", store.GetStateValue(HistoryBudgetWarnings.StateName));
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    /// <summary>
    /// Settings' line under the temporary pictures counts the pictures and their bytes in every folder, never a picture
    /// still being written (<c>.part</c>); a folder not made yet counts nothing.
    /// </summary>
    [Fact]
    public void TempPictures_Measure()
    {
        var root = Path.Combine(temp.Path, "Images");
        Assert.Equal((0, 0L), TempImageFiles.Measure(root));
        Assert.Equal((0, 0L), TempImageFiles.Measure(string.Empty));

        Directory.CreateDirectory(Path.Combine(root, "0123456789ab"));
        Directory.CreateDirectory(Path.Combine(root, "ba9876543210"));
        File.WriteAllBytes(Path.Combine(root, "0123456789ab", "BC-TEST one.png"), new byte[100]);
        File.WriteAllBytes(Path.Combine(root, "ba9876543210", "BC-TEST two.png"), new byte[250]);
        File.WriteAllBytes(Path.Combine(root, "ba9876543210", "BC-TEST three.png.part"), new byte[999]);
        Assert.Equal((2, 350L), TempImageFiles.Measure(root));
    }

    /// <summary>A new, initialized store in the temp folder (one per test: the folder is the test class's).</summary>
    /// <returns>The store.</returns>
    private ClipStore NewStore()
    {
        var store = new ClipStore(temp.DatabasePath);
        store.Initialize();
        return store;
    }

    /// <summary>Starts a history service that records its budget warnings.</summary>
    /// <param name="store">The store.</param>
    /// <param name="rules">The capture rules, read at every copy.</param>
    /// <param name="warnings">Where the warnings go.</param>
    /// <returns>The started service.</returns>
    private static ClipHistoryService Start(ClipStore store, Func<CaptureRules> rules, List<HistoryBudgetWarning> warnings)
    {
        var service = new ClipHistoryService(store, null, rules);
        service.BudgetWarning += (_, warning) =>
        {
            lock (warnings)
            {
                warnings.Add(warning);
            }
        };
        service.Start();
        return service;
    }

    /// <summary>The levels warned so far.</summary>
    /// <param name="warnings">The recorded warnings.</param>
    /// <returns>Their levels, in order.</returns>
    private static int[] Levels(List<HistoryBudgetWarning> warnings)
    {
        lock (warnings)
        {
            return [.. warnings.Select(w => w.Level)];
        }
    }

    /// <summary>A copy of one fixed size (every number gives the same length), a minute apart, so the oldest goes first.</summary>
    /// <param name="number">Which copy (0 to 99).</param>
    /// <returns>The capture.</returns>
    private static ClipCapture Capture(int number) =>
        TestData.Text($"BC-TEST budget copy {number:00} {new string('x', 500)}", TestData.Now.AddMinutes(number));

    /// <summary>A text whose payload grows with <paramref name="length"/>.</summary>
    /// <param name="name">Starts the text (tells entries apart).</param>
    /// <param name="length">How many characters follow the name.</param>
    /// <returns>The text.</returns>
    private static string Body(string name, int length) => $"BC-TEST {name} {new string('y', length)}";

    /// <summary>A text capture of a given size and age.</summary>
    /// <param name="name">Starts the text.</param>
    /// <param name="length">How many characters follow the name.</param>
    /// <param name="minutes">Its capture time, in minutes from <see cref="TestData.Now"/>.</param>
    /// <param name="pin">Pin it.</param>
    /// <returns>The capture.</returns>
    private static ClipCapture Text(string name, int length, int minutes, bool pin = false) =>
        TestData.Text(Body(name, length), TestData.Now.AddMinutes(minutes), pin: pin);

    /// <summary>Stores a live capture.</summary>
    /// <param name="store">The store.</param>
    /// <param name="capture">The capture.</param>
    /// <returns>The stored entry.</returns>
    private static ClipEntry Add(ClipStore store, ClipCapture capture) =>
        store.Upsert(capture, ContentClassifier.Classify(capture)!, null, bumpIfExists: true)!.Entry;
}
