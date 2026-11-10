using BetterClipboard.Core.Content;
using BetterClipboard.Core.Integrations;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the tabs that show only with something in them — the Run tab (an entry with a Win+R run time) and the
/// Snipping tab (a picture it lists): the store's two lookups behind the answer, and how the history service keeps the
/// answer current through adds, bumps, deletes, imports, clears, prunes and "Forget forever".
/// </summary>
public sealed class TabContentTests : IAsyncLifetime
{
    /// <summary>Snipping Tool's executable as installed on Windows 11 (package folder).</summary>
    private const string SnippingToolPath = @"C:\Program Files\WindowsApps\Microsoft.ScreenSketch_11.2607.23.0_x64__8wekyb3d8bbwe\SnippingTool\SnippingTool.exe";

    /// <summary>An app that copies pictures and has nothing to do with screenshots.</summary>
    private static readonly SourceAppInfo Paint = new("mspaint", @"C:\Windows\System32\mspaint.exe", "Paint");

    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private readonly List<ClipFilter> changes = [];
    private CaptureRules rules = new() { Retention = RetentionPolicy.Unlimited };
    private ClipStore store = null!;
    private ClipHistoryService? service;

    /// <summary>Creates a store over a temp database (the service is started by each test that needs it).</summary>
    /// <returns>A completed task.</returns>
    public ValueTask InitializeAsync()
    {
        store = new ClipStore(temp.DatabasePath);
        store.Initialize();
        return ValueTask.CompletedTask;
    }

    /// <summary>Stops the service, if one was started, and deletes the temp store.</summary>
    /// <returns>A task completing after shutdown.</returns>
    public async ValueTask DisposeAsync()
    {
        if (service is not null)
        {
            await service.DisposeAsync();
        }

        temp.Dispose();
    }

    /// <summary>
    /// The Run tab's newest entry is the newest one with a run time, whatever its origin: an imported command, a run seen
    /// live, and a text copied first and run later (which a run moves up). Copies that were never run do not count, and
    /// a kind narrows the lookup.
    /// </summary>
    [Fact]
    public void FindNewestInFilter_Run_IsTheNewestEntryWithARunTime()
    {
        Assert.Null(store.FindNewestInFilter(ClipFilter.Run));
        long copied = Store(TestData.Text("BC-TEST copied", TestData.Now));
        Assert.Null(store.FindNewestInFilter(ClipFilter.Run));
        Assert.Equal(copied, store.FindNewestInFilter(ClipFilter.All));

        Store(RunMru.CreateCapture("BC-TEST imported", TestData.Now.AddMinutes(1), observedRun: false));
        long live = Store(RunMru.CreateCapture("BC-TEST live", TestData.Now.AddMinutes(2), observedRun: true));
        Assert.Equal(live, store.FindNewestInFilter(ClipFilter.Run));

        Assert.Equal(copied, Store(RunMru.CreateCapture("BC-TEST copied", TestData.Now.AddMinutes(3), observedRun: true)));
        Assert.Equal(copied, store.FindNewestInFilter(ClipFilter.Run));
        Assert.Equal(copied, store.FindNewestInFilter(ClipFilter.Run, ClipKind.Text));
        Assert.Null(store.FindNewestInFilter(ClipFilter.Run, ClipKind.Image));

        Assert.True(store.Delete(copied, TestData.Now.AddMinutes(4)));
        Assert.Equal(live, store.FindNewestInFilter(ClipFilter.Run));
    }

    /// <summary>
    /// Counting pictures only, the Snipping tab's newest entry is the newest picture it lists — a screenshot file of any
    /// tool, or a picture Snipping Tool copied — never a picture from another app, and never text Snipping Tool copied
    /// (which the tab does list).
    /// </summary>
    [Fact]
    public void FindNewestInFilter_SnippingPictures_OnlyPicturesTheTabLists()
    {
        Store(Picture(1, TestData.Now, Paint));
        long text = Store(TestData.Text("BC-TEST text from Snipping Tool", TestData.Now.AddMinutes(1), source: SnippingTool()));
        Assert.Null(store.FindNewestInFilter(ClipFilter.Snipping, ClipKind.Image));
        Assert.Equal(text, store.FindNewestInFilter(ClipFilter.Snipping));

        long file = Store(Shot(2, TestData.Now.AddMinutes(2), WindowsScreenshots.FolderSource));
        Assert.Equal(file, store.FindNewestInFilter(ClipFilter.Snipping, ClipKind.Image));
        long copied = Store(Picture(3, TestData.Now.AddMinutes(3), SnippingTool()));
        Assert.Equal(copied, store.FindNewestInFilter(ClipFilter.Snipping, ClipKind.Image));
        long winPrtScn = Store(Shot(4, TestData.Now.AddMinutes(4), WindowsScreenshots.WinPrtScnSource));
        Assert.Equal(winPrtScn, store.FindNewestInFilter(ClipFilter.Snipping, ClipKind.Image));

        Store(Picture(5, TestData.Now.AddMinutes(5), Paint));
        Assert.Equal(winPrtScn, store.FindNewestInFilter(ClipFilter.Snipping, ClipKind.Image));
    }

    /// <summary>
    /// One entry at a time: in the tab or not, the kind narrowing, every entry for "All", and nothing for an id that does
    /// not exist (never stored, or deleted).
    /// </summary>
    [Fact]
    public void IsInFilter_AnswersForOneEntry()
    {
        long run = Store(RunMru.CreateCapture("BC-TEST run", TestData.Now, observedRun: true));
        long copy = Store(TestData.Text("BC-TEST copy", TestData.Now));
        long snip = Store(Picture(1, TestData.Now, SnippingTool()));

        Assert.True(store.IsInFilter(run, ClipFilter.Run));
        Assert.False(store.IsInFilter(copy, ClipFilter.Run));
        Assert.True(store.IsInFilter(copy, ClipFilter.All));
        Assert.True(store.IsInFilter(snip, ClipFilter.Snipping, ClipKind.Image));
        Assert.False(store.IsInFilter(snip, ClipFilter.Snipping, ClipKind.Text));
        Assert.False(store.IsInFilter(run, ClipFilter.Snipping));
        Assert.False(store.IsInFilter(run + copy + snip + 1000, ClipFilter.All));

        Assert.True(store.Delete(snip, TestData.Now.AddMinutes(1)));
        Assert.False(store.IsInFilter(snip, ClipFilter.Snipping, ClipKind.Image));
    }

    /// <summary>
    /// The Run tab shows with the first run and goes with the last: copies that were never run change nothing, a second
    /// run raises no second event, deleting the entry that proved it looks the tab up again and finds the other run, and
    /// deleting that one empties it.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Service_RunTab_ShowsWithTheFirstRunAndGoesWithTheLast()
    {
        var history = await StartAsync();
        Assert.False(history.HasRunHistory);

        await history.AddAsync(TestData.Text("BC-TEST copy"));
        Assert.False(history.HasRunHistory);
        Assert.Empty(Changes());

        var first = await history.AddAsync(RunMru.CreateCapture("BC-TEST first", TestData.Now.AddMinutes(1), observedRun: true));
        Assert.True(history.HasRunHistory);
        Assert.Equal([ClipFilter.Run], Changes());

        var second = await history.AddAsync(RunMru.CreateCapture("BC-TEST second", TestData.Now.AddMinutes(2), observedRun: true));
        await history.DeleteAsync(first!.Id);
        Assert.True(history.HasRunHistory);
        Assert.Equal([ClipFilter.Run], Changes());

        await history.DeleteAsync(second!.Id);
        Assert.False(history.HasRunHistory);
        Assert.Equal([ClipFilter.Run, ClipFilter.Run], Changes());
        Assert.False(history.HasSnippingImages);
    }

    /// <summary>
    /// An import raises one reset and no event per entry: the first import of Windows' Win+R list still fills the Run tab,
    /// and an import of other items (Windows' clipboard history) leaves it empty.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Service_RunTab_FilledByTheFirstImport()
    {
        var history = await StartAsync();
        Assert.Equal(1, await history.ImportAsync([TestData.Text("BC-TEST from Windows' history", origin: ClipOrigin.WindowsHistory)]));
        Assert.False(history.HasRunHistory);
        Assert.Empty(Changes());

        Assert.Equal(2, await history.ImportAsync(
        [
            RunMru.CreateCapture("BC-TEST notepad", TestData.Now.AddSeconds(-2), observedRun: false),
            RunMru.CreateCapture("BC-TEST calc", TestData.Now.AddSeconds(-1), observedRun: false),
        ]));
        Assert.True(history.HasRunHistory);
        Assert.Equal([ClipFilter.Run], Changes());
    }

    /// <summary>
    /// The Snipping tab shows with the first picture it lists and goes with the last: a picture from another app and text
    /// Snipping Tool copied do not count; a snip Snipping Tool copied does, until the same picture copied again from Paint
    /// takes Paint's name; a screenshot file stays the tab's whoever copies it later (its origin decides); a clear empties
    /// the tab.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Service_SnippingTab_CountsScreenshotsAndSnippingToolPictures()
    {
        var history = await StartAsync();
        await history.AddAsync(Picture(1, TestData.Now, Paint));
        await history.AddAsync(TestData.Text("BC-TEST text from Snipping Tool", TestData.Now.AddMinutes(1), source: SnippingTool()));
        Assert.False(history.HasSnippingImages);
        Assert.Empty(Changes());

        var snip = await history.AddAsync(Picture(2, TestData.Now.AddMinutes(2), SnippingTool()));
        Assert.True(history.HasSnippingImages);
        Assert.Equal([ClipFilter.Snipping], Changes());

        var again = await history.AddAsync(Picture(2, TestData.Now.AddMinutes(3), Paint));
        Assert.Equal(snip!.Id, again!.Id);
        Assert.Equal("Paint", again.SourceAppName);
        Assert.False(history.HasSnippingImages);

        await history.AddAsync(Shot(3, TestData.Now.AddMinutes(4), WindowsScreenshots.WinPrtScnSource));
        await history.AddAsync(Picture(3, TestData.Now.AddMinutes(5), Paint));
        Assert.True(history.HasSnippingImages);

        await history.ClearAsync(includePinned: true);
        Assert.False(history.HasSnippingImages);
        Assert.Equal([ClipFilter.Snipping, ClipFilter.Snipping, ClipFilter.Snipping, ClipFilter.Snipping], Changes());
        Assert.False(history.HasRunHistory);
    }

    /// <summary>
    /// A store that already has a run and a screenshot when the service starts: both tabs are known to have entries before
    /// the first work item, and each is announced once.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Service_LooksUpWhatTheStoreHasAtStart()
    {
        Store(RunMru.CreateCapture("BC-TEST run before", TestData.Now, observedRun: true));
        Store(Shot(1, TestData.Now, WindowsScreenshots.FolderSource));

        var history = await StartAsync();
        Assert.True(history.HasRunHistory);
        Assert.True(history.HasSnippingImages);
        Assert.Equal([ClipFilter.Run, ClipFilter.Snipping], Changes());
    }

    /// <summary>
    /// Retention and "Forget forever" take entries too: a prune by count that removes the only run empties the Run tab
    /// (the screenshot it kept still shows its tab), and forgetting the only screenshot empties the Snipping tab.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Service_PruneAndForget_TakeTheLastEntry()
    {
        rules = rules with { Retention = new RetentionPolicy(2, null, 0) };
        var history = await StartAsync();
        await history.AddAsync(RunMru.CreateCapture("BC-TEST oldest run", TestData.Now, observedRun: true));
        var shot = await history.AddAsync(Shot(1, TestData.Now.AddMinutes(1), WindowsScreenshots.FolderSource));
        Assert.True(history.HasRunHistory);
        Assert.True(history.HasSnippingImages);

        await history.AddAsync(TestData.Text("BC-TEST newer copy", TestData.Now.AddMinutes(2)));
        Assert.False(history.HasRunHistory);
        Assert.True(history.HasSnippingImages);

        Assert.NotNull(await history.ForgetAsync(shot!.Id));
        Assert.False(history.HasSnippingImages);
        Assert.Equal([ClipFilter.Run, ClipFilter.Snipping, ClipFilter.Run, ClipFilter.Snipping], Changes());
    }

    /// <summary>
    /// Starts a history service over the test store, recording <see cref="ClipHistoryService.TabContentChanged"/>, and
    /// waits for one round trip of its worker: the start-up lookup runs before the first work item.
    /// </summary>
    /// <returns>The started service.</returns>
    private async Task<ClipHistoryService> StartAsync()
    {
        service = new ClipHistoryService(store, null, () => rules);
        service.TabContentChanged += (_, filter) =>
        {
            lock (changes)
            {
                changes.Add(filter);
            }
        };
        service.Start();
        await service.PruneAsync();
        return service;
    }

    /// <summary>The tabs announced so far, in order (copied under the lock: the worker adds to the list).</summary>
    /// <returns>The announced tabs.</returns>
    private ClipFilter[] Changes()
    {
        lock (changes)
        {
            return [.. changes];
        }
    }

    /// <summary>
    /// Stores a capture straight into the store, as the service would: a duplicate is bumped by a new event and left alone by
    /// an import (Windows' Win+R list read at the first activation).
    /// </summary>
    /// <param name="capture">The capture.</param>
    /// <returns>The entry's id.</returns>
    private long Store(ClipCapture capture) =>
        store.Upsert(capture, ContentClassifier.Classify(capture)!, null, bumpIfExists: capture.Origin != ClipOrigin.RunDialogHistory)!.Entry.Id;

    /// <summary>Snipping Tool as the source of its clipboard copies.</summary>
    /// <returns>The source.</returns>
    private static SourceAppInfo SnippingTool() => WindowsScreenshots.SnippingToolSource(SnippingToolPath);

    /// <summary>A picture copied to the clipboard (fake PNG bytes: only hashing matters here).</summary>
    /// <param name="seed">Varies the bytes: the same seed is the same picture.</param>
    /// <param name="at">When it was copied.</param>
    /// <param name="source">The app that copied it.</param>
    /// <returns>The capture.</returns>
    private static ClipCapture Picture(byte seed, DateTimeOffset at, SourceAppInfo source) => new()
    {
        Formats = [new ClipFormatData(ClipFormatNames.Png, Enumerable.Repeat(seed, 64).ToArray())],
        CapturedAtUtc = at,
        Source = source,
    };

    /// <summary>A screenshot file as the Screenshots folder watch delivers it.</summary>
    /// <param name="seed">Varies the bytes: the same seed is the same picture.</param>
    /// <param name="at">File write time.</param>
    /// <param name="source">The tool that saved it.</param>
    /// <returns>The capture.</returns>
    private static ClipCapture Shot(byte seed, DateTimeOffset at, SourceAppInfo source) => new()
    {
        Formats = [new ClipFormatData(ClipFormatNames.Png, Enumerable.Repeat(seed, 64).ToArray())],
        CapturedAtUtc = at,
        Origin = ClipOrigin.WindowsScreenshot,
        Source = source,
    };
}
