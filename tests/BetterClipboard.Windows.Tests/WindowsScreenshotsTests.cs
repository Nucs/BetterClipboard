using System.Collections.Concurrent;
using System.Globalization;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Integrations;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Storage;
using BetterClipboard.Windows.Clipboard;
using BetterClipboard.Windows.Imaging;
using BetterClipboard.Windows.Integrations;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests for <see cref="WindowsScreenshotWatcher"/> on a temp folder standing in for the Screenshots folder: each tool's
/// files with their names, never locking a writer out, skipping files copied or moved in, the skip rules, renames, the
/// "handled" signal, catch-up, and a folder created later.
/// </summary>
public sealed class WindowsScreenshotWatcherTests : IDisposable
{
    /// <summary>The Snipping Tool path recorded with its files in these tests.</summary>
    internal const string SnippingToolPath = @"C:\Program Files\WindowsApps\Microsoft.ScreenSketch_11.2607.23.0_x64__8wekyb3d8bbwe\SnippingTool\SnippingTool.exe";

    private readonly string root = Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", Guid.NewGuid().ToString("N"));
    private readonly string folder;
    private readonly string outside;
    private readonly WindowsScreenshotsLocation location;
    private readonly ConcurrentQueue<ClipCapture> delivered = new();
    private readonly ConcurrentQueue<DateTimeOffset> handled = new();

    /// <summary>Creates the stand-in Screenshots folder and a sibling folder outside the watch.</summary>
    public WindowsScreenshotWatcherTests()
    {
        folder = Directory.CreateDirectory(Path.Combine(root, "Screenshots")).FullName;
        outside = Directory.CreateDirectory(Path.Combine(root, "Elsewhere")).FullName;
        location = new WindowsScreenshotsLocation(folder, "test", SnippingToolPath, "11.2607.23.0", SnippingToolSettings.Defaults);
    }

    /// <summary>Deletes the temp tree.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>
    /// Each tool's file becomes exactly one capture named after it — Snipping Tool, Win+PrtScn, or the folder for any
    /// other image — with the PNG's own bytes plus a DIBV5, origin <see cref="ClipOrigin.WindowsScreenshot"/>.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Watch_ImportsEachToolUnderItsName()
    {
        using var watcher = CreateWatcher();
        watcher.Start();
        Assert.Equal(folder, watcher.WatchedFolder);

        var snip = await ShareXScreenshotWatcherTests.PngAsync(40, 30, 1);
        await File.WriteAllBytesAsync(Path.Combine(folder, "BC-TEST Screenshot 2026-10-01 093015.png"), snip, TestContext.Current.CancellationToken);
        await ShareXScreenshotWatcherTests.WaitUntilAsync(() => delivered.Count == 1);
        await File.WriteAllBytesAsync(Path.Combine(folder, "BC-TEST Screenshot (5).png"), await ShareXScreenshotWatcherTests.PngAsync(30, 20, 2), TestContext.Current.CancellationToken);
        await ShareXScreenshotWatcherTests.WaitUntilAsync(() => delivered.Count == 2);
        await File.WriteAllBytesAsync(Path.Combine(folder, "BC-TEST other.png"), await ShareXScreenshotWatcherTests.PngAsync(20, 10, 3), TestContext.Current.CancellationToken);
        await ShareXScreenshotWatcherTests.WaitUntilAsync(() => delivered.Count == 3);
        await Task.Delay(600, TestContext.Current.CancellationToken); // late events of the same saves must not import them again

        var captures = delivered.ToArray();
        Assert.Equal(3, captures.Length);
        Assert.All(captures, c => Assert.Equal(ClipOrigin.WindowsScreenshot, c.Origin));
        Assert.All(captures, c => Assert.Equal([ClipFormatNames.Png, ClipFormatNames.DibV5], c.Formats.Select(f => f.Name)));
        Assert.Equal(snip, captures[0].Formats[0].Data);
        Assert.Equal(("SnippingTool", SnippingToolPath, "Snipping Tool"), (captures[0].Source!.ProcessName, captures[0].Source!.ExecutablePath, captures[0].Source!.DisplayName));
        Assert.Equal("Win+PrtScn", captures[1].Source!.DisplayName);
        Assert.Equal("Screenshots folder", captures[2].Source!.DisplayName);
        Assert.Equal(3, handled.Count);
    }

    /// <summary>
    /// While a file is incomplete nothing is imported — and the writer can still close and reopen its own file for writing
    /// at any moment (WinRT's StorageFile does), which a check that denies writing would refuse. Once complete, it arrives once.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Watch_NeverLocksTheWriterOut_AndWaitsForTheEnd()
    {
        using var watcher = CreateWatcher();
        watcher.Start();
        var png = await ShareXScreenshotWatcherTests.PngAsync(64, 48, 4);
        var path = Path.Combine(folder, "BC-TEST Screenshot 2026-10-01 120000.png");
        await using (var first = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            first.Write(png, 0, png.Length / 2);
        }

        // Well past the settle delay, while the watcher keeps reading the half file: every reopen must succeed.
        var until = DateTime.UtcNow.AddMilliseconds(900);
        int reopened = 0;
        while (DateTime.UtcNow < until)
        {
            await using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                reopened++;
            }

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(reopened > 20);
        Assert.Empty(delivered);
        await using (var rest = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            rest.Write(png, png.Length / 2, png.Length - (png.Length / 2));
        }

        await ShareXScreenshotWatcherTests.WaitUntilAsync(() => delivered.Count == 1);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.Equal(png, Assert.Single(delivered).Formats[0].Data);
    }

    /// <summary>
    /// Images copied or moved into the folder keep their old write time: not screenshots, not imported, not "handled"
    /// (the catch-up marker must not move for them). A screenshot saved afterwards still arrives.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Watch_SkipsFilesCopiedOrMovedIn()
    {
        using var watcher = CreateWatcher();
        watcher.Start();
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var copied = Path.Combine(outside, "BC-TEST Screenshot (3).png");
        await File.WriteAllBytesAsync(copied, await ShareXScreenshotWatcherTests.PngAsync(16, 16, 5), TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(copied, old);
        File.Copy(copied, Path.Combine(folder, "BC-TEST Screenshot (3).png"));
        var moved = Path.Combine(outside, "BC-TEST Screenshot 2020-01-01 000000.png");
        await File.WriteAllBytesAsync(moved, await ShareXScreenshotWatcherTests.PngAsync(16, 16, 6), TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(moved, old);
        File.Move(moved, Path.Combine(folder, "BC-TEST Screenshot 2020-01-01 000000.png"));
        await Task.Delay(1200, TestContext.Current.CancellationToken);
        Assert.Empty(delivered);
        Assert.Empty(handled);

        var sentinel = await ShareXScreenshotWatcherTests.PngAsync(12, 12, 7);
        await File.WriteAllBytesAsync(Path.Combine(folder, "BC-TEST Screenshot (4).png"), sentinel, TestContext.Current.CancellationToken);
        await ShareXScreenshotWatcherTests.WaitUntilAsync(() => delivered.Count == 1);
        Assert.Equal(sentinel, Assert.Single(delivered).Formats[0].Data);
    }

    /// <summary>
    /// Recordings, settings files, text and subfolders are left alone; an animated GIF is read and skipped but counts as
    /// handled; a rename right after a save does not import the file again.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Watch_SkipsNonImagesSubfoldersAndAnimations_AndRenames()
    {
        using var watcher = CreateWatcher();
        watcher.Start();
        var png = await ShareXScreenshotWatcherTests.PngAsync(16, 16, 8);
        await File.WriteAllBytesAsync(Path.Combine(folder, "Screen Recording 2026-10-01 093015.mp4"), [0, 0, 0, 24], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folder, "desktop.ini"), "[.ShellClassInfo]", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "BC-TEST", TestContext.Current.CancellationToken);
        var sorted = Directory.CreateDirectory(Path.Combine(folder, "Sorted")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(sorted, "BC-TEST Screenshot (1).png"), png, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(folder, "BC-TEST recording.gif"), await ShareXScreenshotWatcherTests.AnimatedGifAsync(), TestContext.Current.CancellationToken);
        await ShareXScreenshotWatcherTests.WaitUntilAsync(() => handled.Count == 1);

        var shot = Path.Combine(folder, "BC-TEST Screenshot (9).png");
        await File.WriteAllBytesAsync(shot, png, TestContext.Current.CancellationToken);
        await ShareXScreenshotWatcherTests.WaitUntilAsync(() => delivered.Count == 1);
        File.Move(shot, Path.Combine(folder, "BC-TEST bug report.png"));
        await Task.Delay(800, TestContext.Current.CancellationToken);

        Assert.Equal(png, Assert.Single(delivered).Formats[0].Data);
        Assert.Equal(2, handled.Count);
    }

    /// <summary>A screenshot the history refused (capture paused) is still reported as handled, with its write time.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Watch_RefusedScreenshot_IsStillHandled()
    {
        using var watcher = CreateWatcher(accept: false);
        watcher.Start();
        var path = Path.Combine(folder, "BC-TEST Screenshot (2).png");
        await File.WriteAllBytesAsync(path, await ShareXScreenshotWatcherTests.PngAsync(8, 8, 9), TestContext.Current.CancellationToken);
        await ShareXScreenshotWatcherTests.WaitUntilAsync(() => !handled.IsEmpty);
        Assert.Equal(new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero), Assert.Single(handled));
    }

    /// <summary>Catch-up imports only image files written after the marker, oldest first, capped to the newest N — whatever their age.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task CatchUp_NewerThanMarker_OldestFirst_Capped()
    {
        var now = DateTime.UtcNow;
        for (int hours = 4; hours >= 1; hours--)
        {
            var path = Path.Combine(folder, $"BC-TEST Screenshot ({hours}).png");
            await File.WriteAllBytesAsync(path, await ShareXScreenshotWatcherTests.PngAsync(8, 8, (byte)(10 + hours)), TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(path, now.AddHours(-hours));
        }

        await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "BC-TEST", TestContext.Current.CancellationToken);

        using var watcher = CreateWatcher();
        int imported = await watcher.CatchUpAsync(new DateTimeOffset(now.AddHours(-3.5), TimeSpan.Zero), 2, TestContext.Current.CancellationToken);
        Assert.Equal(2, imported);
        Assert.Equal([now.AddHours(-2), now.AddHours(-1)], delivered.Select(c => c.CapturedAtUtc.UtcDateTime));

        // Again with a larger cap: the two already handled are skipped; only the one the cap left out arrives.
        Assert.Equal(1, await watcher.CatchUpAsync(new DateTimeOffset(now.AddHours(-3.5), TimeSpan.Zero), 10, TestContext.Current.CancellationToken));
        Assert.Equal(now.AddHours(-3), delivered.Last().CapturedAtUtc.UtcDateTime);
    }

    /// <summary>A folder that does not exist yet is not watched; once it exists, <see cref="WindowsScreenshotWatcher.Update"/> starts watching it.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Update_PicksUpTheFolderOnceItExists()
    {
        var later = Path.Combine(root, "Later", "Screenshots");
        var waiting = location with { Folder = later };
        using var watcher = CreateWatcher(waiting);
        watcher.Start();
        Assert.Null(watcher.WatchedFolder);
        Assert.False(watcher.Update(waiting));

        Directory.CreateDirectory(later);
        Assert.True(watcher.Update(waiting));
        Assert.Equal(later, watcher.WatchedFolder);
        Assert.False(watcher.Update(waiting));

        await File.WriteAllBytesAsync(Path.Combine(later, "BC-TEST Screenshot (1).png"), await ShareXScreenshotWatcherTests.PngAsync(8, 8, 20), TestContext.Current.CancellationToken);
        await ShareXScreenshotWatcherTests.WaitUntilAsync(() => delivered.Count == 1);
    }

    /// <summary>Creates a watcher that records deliveries and handled times.</summary>
    /// <param name="target">Location (default: the test folder).</param>
    /// <param name="accept">What the fake history answers.</param>
    /// <returns>The watcher (not started).</returns>
    private WindowsScreenshotWatcher CreateWatcher(WindowsScreenshotsLocation? target = null, bool accept = true)
    {
        var watcher = new WindowsScreenshotWatcher(
            target ?? location,
            capture =>
            {
                if (accept)
                {
                    delivered.Enqueue(capture);
                }

                return Task.FromResult(accept);
            },
            () => 64L * 1024 * 1024)
        {
            SettleDelay = TimeSpan.FromMilliseconds(50),
            ReadTimeout = TimeSpan.FromSeconds(10),
        };
        watcher.Handled += (_, when) => handled.Enqueue(when);
        return watcher;
    }
}

/// <summary>
/// Tests for <see cref="WindowsScreenshotsIntegration"/> over a real history service: the catch-up marker's life cycle,
/// a folder that appears later, and the merge of a snip's clipboard copy with its file (the open question of CLAUDE.md
/// §2.18 for the bitmaps a clipboard can carry).
/// </summary>
public sealed class WindowsScreenshotsIntegrationTests : IAsyncLifetime
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", Guid.NewGuid().ToString("N"));
    private string folder = null!;
    private WindowsScreenshotsLocation location = null!;
    private ClipHistoryService history = null!;

    /// <summary>Creates the stand-in Screenshots folder and a history over a temp store (with the real image analyzer).</summary>
    /// <returns>A completed task.</returns>
    public ValueTask InitializeAsync()
    {
        folder = Directory.CreateDirectory(Path.Combine(temp, "Screenshots")).FullName;
        location = new WindowsScreenshotsLocation(folder, "test", WindowsScreenshotWatcherTests.SnippingToolPath, "11.2607.23.0", SnippingToolSettings.Defaults);
        var store = new ClipStore(Path.Combine(temp, "history.db"));
        store.Initialize();
        history = new ClipHistoryService(store, new WinRtImageAnalyzer(), () => new CaptureRules { Retention = RetentionPolicy.Unlimited });
        history.Start();
        return ValueTask.CompletedTask;
    }

    /// <summary>Stops the history and deletes the temp tree.</summary>
    /// <returns>A task.</returns>
    public async ValueTask DisposeAsync()
    {
        await history.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>
    /// First activation: the marker becomes "now" and the folder's archive is not imported, while a screenshot saved
    /// afterwards arrives live, lands in the Snipping tab, and advances the marker.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task FirstActivation_NoBackfill_ThenLive()
    {
        var old = Path.Combine(folder, "BC-TEST Screenshot (1).png");
        await File.WriteAllBytesAsync(old, await ShareXScreenshotWatcherTests.PngAsync(8, 8, 1), TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));

        var now = DateTimeOffset.UtcNow;
        await using var integration = new WindowsScreenshotsIntegration(history, () => long.MaxValue, () => location, new FixedTime(now));
        await integration.StartAsync(enable: true);
        Assert.True(integration.IsWatching);
        Assert.Equal(now, Marker(await history.GetStateValueAsync(WindowsScreenshotsIntegration.LastSeenStateName)));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Empty(await SnippingItemsAsync());

        await File.WriteAllBytesAsync(Path.Combine(folder, "BC-TEST Screenshot (2).png"), await ShareXScreenshotWatcherTests.PngAsync(8, 8, 2), TestContext.Current.CancellationToken);
        await ShareXScreenshotWatcherTests.WaitUntilAsync(() => SnippingItemsAsync().Result.Count == 1);
        var entry = (await SnippingItemsAsync()).Single();
        Assert.Equal((ClipOrigin.WindowsScreenshot, "Win+PrtScn"), (entry.Origin, entry.SourceAppName));
        Assert.Equal(1, integration.ImportedThisSession);
        await ShareXScreenshotWatcherTests.WaitUntilAsync(() => Marker(history.GetStateValueAsync(WindowsScreenshotsIntegration.LastSeenStateName).Result) > now);
    }

    /// <summary>After a restart, screenshots written after the stored marker are imported; older ones are not.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Restart_CatchesUpSinceTheMarker()
    {
        // Whole seconds: the history keeps milliseconds, file times keep 100 ns ticks.
        var marker = DateTime.UtcNow.AddMinutes(-10);
        marker = new DateTime(marker.Ticks - (marker.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        await history.SetStateValueAsync(WindowsScreenshotsIntegration.LastSeenStateName, marker.ToString("o", CultureInfo.InvariantCulture));
        var before = Path.Combine(folder, "BC-TEST Screenshot 2026-10-01 090000.png");
        var after = Path.Combine(folder, "BC-TEST Screenshot 2026-10-01 091000.png");
        await File.WriteAllBytesAsync(before, await ShareXScreenshotWatcherTests.PngAsync(8, 8, 3), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(after, await ShareXScreenshotWatcherTests.PngAsync(8, 8, 4), TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(before, marker.AddMinutes(-1));
        File.SetLastWriteTimeUtc(after, marker.AddMinutes(1));

        await using var integration = new WindowsScreenshotsIntegration(history, () => long.MaxValue, () => location);
        await integration.StartAsync(enable: true);
        await ShareXScreenshotWatcherTests.WaitUntilAsync(() => SnippingItemsAsync().Result.Count == 1);
        var entry = (await SnippingItemsAsync()).Single();
        Assert.Equal(new DateTimeOffset(marker.AddMinutes(1), TimeSpan.Zero), entry.LastUsedUtc);
        Assert.Equal("Snipping Tool", entry.SourceAppName);
        await ShareXScreenshotWatcherTests.WaitUntilAsync(() => Marker(history.GetStateValueAsync(WindowsScreenshotsIntegration.LastSeenStateName).Result) == new DateTimeOffset(marker.AddMinutes(1), TimeSpan.Zero));
    }

    /// <summary>Off stops watching and clears the marker; on again starts fresh (nothing taken while off is imported).</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Off_ClearsTheMarker_OnStartsFresh()
    {
        await using var integration = new WindowsScreenshotsIntegration(history, () => long.MaxValue, () => location);
        await integration.StartAsync(enable: true);
        await ShareXScreenshotWatcherTests.WaitUntilAsync(() => !string.IsNullOrEmpty(history.GetStateValueAsync(WindowsScreenshotsIntegration.LastSeenStateName).Result));

        await integration.SetEnabledAsync(false);
        Assert.False(integration.IsWatching);
        Assert.Equal(string.Empty, await history.GetStateValueAsync(WindowsScreenshotsIntegration.LastSeenStateName));

        var whileOff = Path.Combine(folder, "BC-TEST Screenshot (5).png");
        await File.WriteAllBytesAsync(whileOff, await ShareXScreenshotWatcherTests.PngAsync(8, 8, 5), TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(whileOff, DateTime.UtcNow.AddSeconds(-5));
        await integration.SetEnabledAsync(true);
        Assert.True(integration.IsWatching);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Empty(await SnippingItemsAsync());
    }

    /// <summary>
    /// A Screenshots folder that does not exist yet is waited for; the refresh that finds it starts the watch and catches up
    /// on the screenshot that created it.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task MissingFolder_IsPickedUpByTheRefresh()
    {
        var later = Path.Combine(temp, "Pictures", "Screenshots");
        var waiting = location with { Folder = later };
        await using var integration = new WindowsScreenshotsIntegration(history, () => long.MaxValue, () => waiting);
        await integration.StartAsync(enable: true);
        Assert.False(integration.IsWatching);
        Assert.Null(integration.WatchedFolder);

        Directory.CreateDirectory(later);
        await File.WriteAllBytesAsync(Path.Combine(later, "BC-TEST Screenshot (1).png"), await ShareXScreenshotWatcherTests.PngAsync(8, 8, 6), TestContext.Current.CancellationToken);
        await integration.RefreshAsync();
        Assert.True(integration.IsWatching);
        Assert.Equal(later, integration.WatchedFolder);
        await ShareXScreenshotWatcherTests.WaitUntilAsync(() => SnippingItemsAsync().Result.Count == 1);
    }

    /// <summary>
    /// A screenshot's clipboard copy and its file are one entry, whichever bitmap the copy carried: a DIBV5 with alpha, or
    /// a 32-bit BI_RGB DIB whose fourth bytes are 0 (what screen captures through GDI produce). The pixel hash merges them,
    /// so the Snipping tab shows one card, named after the tool, not two.
    /// </summary>
    /// <param name="biRgbWithZeroAlpha">Which bitmap the clipboard copy carries.</param>
    /// <returns>A task.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClipboardCopyAndFile_AreOneEntry(bool biRgbWithZeroAlpha)
    {
        const int width = 24, height = 16;
        var pixels = OpaquePixels(width, height);
        var dib = biRgbWithZeroAlpha ? BiRgbDib(pixels, width, height) : DibImage.CreateDibV5(pixels, width, height);
        var copy = await history.AddAsync(new ClipCapture
        {
            Formats = [new ClipFormatData(biRgbWithZeroAlpha ? ClipFormatNames.Dib : ClipFormatNames.DibV5, dib)],
            CapturedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
            Source = new SourceAppInfo("SnippingTool", WindowsScreenshotWatcherTests.SnippingToolPath, "Snipping Tool"),
            Origin = ClipOrigin.Captured,
        });
        Assert.NotNull(copy);

        await using var integration = new WindowsScreenshotsIntegration(history, () => long.MaxValue, () => location);
        await integration.StartAsync(enable: true);
        var png = await ImageCodec.ToPngAsync(DibImage.ToBmpFile(DibImage.CreateDibV5(pixels, width, height)), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(folder, "BC-TEST Screenshot 2026-10-01 120500.png"), png, TestContext.Current.CancellationToken);
        await ShareXScreenshotWatcherTests.WaitUntilAsync(() => integration.ImportedThisSession == 1);

        var all = await history.QueryAsync(new ClipQuery(), TestContext.Current.CancellationToken);
        var entry = Assert.Single(all);
        Assert.Equal(copy!.Id, entry.Id);
        Assert.Equal(2, entry.UseCount);
        Assert.Equal("Snipping Tool", entry.SourceAppName);
        Assert.Single(await SnippingItemsAsync());
    }

    /// <summary>The Snipping tab's items.</summary>
    /// <returns>The entries.</returns>
    private Task<IReadOnlyList<ClipEntry>> SnippingItemsAsync() =>
        history.QueryAsync(new ClipQuery { Filter = ClipFilter.Snipping }, TestContext.Current.CancellationToken);

    /// <summary>Opaque top-down BGRA pixels of a simple pattern.</summary>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <returns>BGRA8, alpha 255.</returns>
    private static byte[] OpaquePixels(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(i * 7);
            pixels[i + 1] = (byte)(i / 4);
            pixels[i + 2] = (byte)(255 - (i / 4));
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    /// <summary>
    /// A packed 32-bit BI_RGB DIB (BITMAPINFOHEADER, bottom-up rows) whose fourth byte is 0 for every pixel — the layout a
    /// GDI screen capture puts on the clipboard, where that byte means nothing.
    /// </summary>
    /// <param name="topDownBgra">Pixels, top row first.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <returns>The DIB.</returns>
    private static byte[] BiRgbDib(byte[] topDownBgra, int width, int height)
    {
        var dib = new byte[40 + topDownBgra.Length];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(width).CopyTo(dib, 4);
        BitConverter.GetBytes(height).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)32).CopyTo(dib, 14);
        BitConverter.GetBytes(topDownBgra.Length).CopyTo(dib, 20);
        int stride = width * 4;
        for (int row = 0; row < height; row++)
        {
            Array.Copy(topDownBgra, row * stride, dib, 40 + ((height - 1 - row) * stride), stride);
        }

        for (int i = 40 + 3; i < dib.Length; i += 4)
        {
            dib[i] = 0;
        }

        return dib;
    }

    /// <summary>Parses a stored marker.</summary>
    /// <param name="raw">Stored value.</param>
    /// <returns>The instant, or <see cref="DateTimeOffset.MinValue"/> when absent.</returns>
    private static DateTimeOffset Marker(string? raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value) ? value : DateTimeOffset.MinValue;

    /// <summary>A clock stopped at one instant.</summary>
    /// <param name="now">The instant.</param>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>Tests for the locator and the source resolver's name choice (CLAUDE.md §2.18).</summary>
public sealed class WindowsScreenshotsLocatorTests
{
    /// <summary>
    /// A card names an executable by its description, else its product — skipping runtime names and a description that is
    /// only the file name (Snipping Tool's "SnippingTool.exe" → "Snipping Tool").
    /// </summary>
    /// <param name="description">FileDescription.</param>
    /// <param name="product">ProductName.</param>
    /// <param name="fileName">Executable file name.</param>
    /// <param name="expected">The name shown.</param>
    [Theory]
    [InlineData("SnippingTool.exe", "Snipping Tool", "SnippingTool.exe", "Snipping Tool")]
    [InlineData("snippingtool.EXE", "Snipping Tool", "SnippingTool.exe", "Snipping Tool")]
    [InlineData("Notepad", "Microsoft® Windows® Operating System", "notepad.exe", "Notepad")]
    [InlineData("Electron", "Slack", "slack.exe", "Slack")]
    [InlineData(" ", "Paint", "mspaint.exe", "Paint")]
    [InlineData("x.exe", null, "x.exe", null)]
    [InlineData(null, null, "x.exe", null)]
    public void ChooseDisplayName_SkipsTheFileName(string? description, string? product, string fileName, string? expected) =>
        Assert.Equal(expected, SourceAppResolver.ChooseDisplayName(description, product, fileName));

    /// <summary>
    /// The override variable replaces the folder (so tests and isolated instances never watch the real one); without it
    /// the Screenshots known folder resolves without being created.
    /// </summary>
    [Fact]
    public void ResolveFolder_OverrideOrKnownFolder()
    {
        var previous = Environment.GetEnvironmentVariable(WindowsScreenshotsLocator.FolderOverrideVariable);
        try
        {
            var fake = Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", "BC-TEST Screenshots");
            Environment.SetEnvironmentVariable(WindowsScreenshotsLocator.FolderOverrideVariable, fake);
            Assert.Equal(fake, WindowsScreenshotsLocator.ResolveFolder(out var by));
            Assert.Equal(WindowsScreenshotsLocator.FolderOverrideVariable, by);

            Environment.SetEnvironmentVariable(WindowsScreenshotsLocator.FolderOverrideVariable, null);
            var real = WindowsScreenshotsLocator.ResolveFolder(out by);
            Assert.False(string.IsNullOrEmpty(real));
            Assert.True(Path.IsPathFullyQualified(real));
            Assert.Equal("the Screenshots known folder", by);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WindowsScreenshotsLocator.FolderOverrideVariable, previous);
        }
    }

    /// <summary>
    /// Snipping Tool is found through the per-user package list when installed (a real executable and version), and its
    /// settings are read from a private copy without throwing — wherever the tests run, installed or not.
    /// </summary>
    [Fact]
    public void FindSnippingTool_AndItsSettings_NeverThrow()
    {
        var (path, version) = WindowsScreenshotsLocator.FindSnippingTool();
        if (path is not null)
        {
            Assert.True(File.Exists(path));
            Assert.Equal(WindowsScreenshots.SnippingToolExecutableName, Path.GetFileName(path), ignoreCase: true);
            Assert.NotNull(version);
        }

        _ = WindowsScreenshotsLocator.ReadSnippingToolSettings();
        var located = WindowsScreenshotsLocator.Locate();
        Assert.Equal(path is not null, located.IsSnippingToolInstalled);
    }
}
