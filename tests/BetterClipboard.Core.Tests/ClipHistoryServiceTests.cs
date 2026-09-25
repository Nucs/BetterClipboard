using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the capture pipeline: rules (pause, ignore list, size), image analysis, events, imports.
/// </summary>
public sealed class ClipHistoryServiceTests : IAsyncLifetime
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private readonly List<ClipChangedEventArgs> events = [];
    private CaptureRules rules = new() { Retention = RetentionPolicy.Unlimited };
    private ClipHistoryService service = null!;
    private FakeImageAnalyzer analyzer = null!;

    /// <summary>Creates and starts a service over a temp store.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask InitializeAsync()
    {
        var store = new ClipStore(temp.DatabasePath);
        store.Initialize();
        analyzer = new FakeImageAnalyzer();
        service = new ClipHistoryService(store, analyzer, () => rules);
        service.Changed += (_, e) =>
        {
            lock (events)
            {
                events.Add(e);
            }
        };
        service.Start();
        return ValueTask.CompletedTask;
    }

    /// <summary>Stops the service and deletes the temp store.</summary>
    /// <returns>A task completing after shutdown.</returns>
    public async ValueTask DisposeAsync()
    {
        await service.DisposeAsync();
        temp.Dispose();
    }

    /// <summary>A new capture is stored and announced; a repeat is an update, not a second entry.</summary>
    [Fact]
    public async Task Capture_AddsThenUpdates()
    {
        var first = await service.AddAsync(TestData.Text("hello"));
        var second = await service.AddAsync(TestData.Text("hello", TestData.Now.AddMinutes(1)));
        Assert.NotNull(first);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal([ClipChangeKind.Added, ClipChangeKind.Updated], Kinds());
    }

    /// <summary>Paused capture drops live copies but still accepts imports.</summary>
    [Fact]
    public async Task Pause_DropsLiveCapturesOnly()
    {
        rules = rules with { IsPaused = true };
        Assert.Null(await service.AddAsync(TestData.Text("live")));
        Assert.NotNull(await service.AddAsync(TestData.Text("import", origin: ClipOrigin.WindowsHistory)));
    }

    /// <summary>Copies from ignored apps are never recorded (name normalization included).</summary>
    [Fact]
    public async Task IgnoredApps_AreSkipped()
    {
        rules = rules with { IgnoredProcessNames = new HashSet<string>(["keepass"], StringComparer.OrdinalIgnoreCase) };
        var keepass = new SourceAppInfo("KeePass", @"C:\KeePass\KeePass.exe", "KeePass");
        Assert.Null(await service.AddAsync(TestData.Text("hunter2", source: keepass)));
        Assert.NotNull(await service.AddAsync(TestData.Text("fine")));
    }

    /// <summary>Oversized copies are skipped.</summary>
    [Fact]
    public async Task SizeLimit_IsEnforced()
    {
        rules = rules with { MaxItemBytes = 10 };
        Assert.Null(await service.AddAsync(TestData.Text("this is more than ten bytes")));
    }

    /// <summary>Image and file capture can be switched off independently.</summary>
    [Fact]
    public async Task KindSwitches_AreHonored()
    {
        rules = rules with { CaptureImages = false, CaptureFiles = false };
        Assert.Null(await service.AddAsync(TestData.Image(1)));
        Assert.Null(await service.AddAsync(TestData.Files(@"C:\x.txt")));
    }

    /// <summary>Image captures are analyzed and the thumbnail/dimensions are persisted.</summary>
    [Fact]
    public async Task Images_AreAnalyzed()
    {
        var entry = await service.AddAsync(TestData.Image(5));
        Assert.Equal(1, analyzer.Calls);
        Assert.Equal(640, entry!.ImageWidth);
        Assert.True(entry.HasThumbnail);
    }

    /// <summary>
    /// The same picture in two encodings (e.g. a live DIB copy and a PNG from the Windows-history import)
    /// is one clip: the analyzer's pixel hash replaces the byte hash as the dedupe key.
    /// </summary>
    [Fact]
    public async Task Images_DedupeByPixels()
    {
        analyzer.PixelHash = "same-pixels";
        var first = await service.AddAsync(TestData.Image(1));
        var second = await service.AddAsync(TestData.Image(2));
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal(2, second.UseCount);
    }

    /// <summary>A failing analyzer costs only the thumbnail, never the clip.</summary>
    [Fact]
    public async Task AnalyzerFailure_StillStoresImage()
    {
        analyzer.Throw = true;
        var entry = await service.AddAsync(TestData.Image(6));
        Assert.NotNull(entry);
        Assert.False(entry!.HasThumbnail);
    }

    /// <summary>Batch imports raise one reset and report how many entries were new.</summary>
    [Fact]
    public async Task Import_RaisesSingleReset()
    {
        await service.AddAsync(TestData.Text("already here"));
        events.Clear();
        int created = await service.ImportAsync(
        [
            TestData.Text("already here", origin: ClipOrigin.WindowsHistory),
            TestData.Text("from windows", origin: ClipOrigin.WindowsHistory),
            TestData.Text("pinned in windows", origin: ClipOrigin.WindowsPinned, pin: true),
        ]);
        Assert.Equal(2, created);
        Assert.Equal([ClipChangeKind.Reset], Kinds());
        var pinned = await service.QueryAsync(new ClipQuery { Filter = ClipFilter.Pinned }, TestContext.Current.CancellationToken);
        Assert.Equal("pinned in windows", pinned.Single().Preview);
    }

    /// <summary>User mutations are applied in order and announced.</summary>
    [Fact]
    public async Task Mutations_AreSerializedAndAnnounced()
    {
        var entry = (await service.AddAsync(TestData.Text("mutate me")))!;
        await service.SetPinnedAsync(entry.Id, true);
        await service.MarkUsedAsync(entry.Id);
        await service.DeleteAsync(entry.Id);
        Assert.Equal([ClipChangeKind.Added, ClipChangeKind.Updated, ClipChangeKind.Updated, ClipChangeKind.Removed], Kinds());
        Assert.Empty(await service.QueryAsync(new ClipQuery(), TestContext.Current.CancellationToken));
    }

    /// <summary>Retention runs after every new entry.</summary>
    [Fact]
    public async Task Retention_AppliesOnCapture()
    {
        rules = rules with { Retention = new RetentionPolicy(2, null, 0) };
        for (int i = 0; i < 4; i++)
        {
            await service.AddAsync(TestData.Text($"t{i}", TestData.Now.AddMinutes(i)));
        }

        Assert.Equal(["t3", "t2"], (await service.QueryAsync(new ClipQuery(), TestContext.Current.CancellationToken)).Select(e => e.Preview));
    }

    /// <summary>Queued work fails fast after shutdown instead of hanging.</summary>
    [Fact]
    public async Task AfterDispose_OperationsFail()
    {
        await service.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddAsync(TestData.Text("late")));
        Assert.False(service.TryEnqueueCapture(TestData.Text("late")));
    }

    /// <summary>Snapshot of the recorded event kinds.</summary>
    /// <returns>The kinds in order.</returns>
    private List<ClipChangeKind> Kinds()
    {
        lock (events)
        {
            return events.Select(e => e.Kind).ToList();
        }
    }

    /// <summary>Deterministic analyzer: 640×480 with a tiny thumbnail, or throws on demand.</summary>
    private sealed class FakeImageAnalyzer : IImageAnalyzer
    {
        /// <summary>Number of analyze calls.</summary>
        public int Calls { get; private set; }

        /// <summary>When set, analysis throws.</summary>
        public bool Throw { get; set; }

        /// <summary>Pixel hash to report (simulates "same pixels, different encoding").</summary>
        public string? PixelHash { get; set; }

        /// <inheritdoc />
        public Task<ImageAnalysis?> AnalyzeAsync(ClipCapture capture, CancellationToken cancellationToken)
        {
            Calls++;
            if (Throw)
            {
                throw new InvalidDataException("corrupt bitmap");
            }

            return Task.FromResult<ImageAnalysis?>(new ImageAnalysis(640, 480, [0x89, 0x50], PixelHash));
        }
    }
}
