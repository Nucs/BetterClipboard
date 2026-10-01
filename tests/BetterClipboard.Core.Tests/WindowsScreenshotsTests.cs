using System.Text;
using BetterClipboard.Core.Cli;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Integrations;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the pure rules of the Windows screenshots integration (the Snipping tab; CLAUDE.md §2.18): which tool a file
/// name belongs to (localized names included), what is an image, freshness, completeness from the bytes, and Snipping
/// Tool's own settings.
/// </summary>
public sealed class WindowsScreenshotsRulesTests
{
    /// <summary>
    /// Names are matched by shape, never by the translated word: Snipping Tool's date and time (with a clash suffix), and
    /// Win+PrtScn's index — also after two U+200F marks (a Hebrew UI) and without the space (some languages).
    /// </summary>
    /// <param name="name">File name.</param>
    /// <param name="expected">The tool.</param>
    [Theory]
    [InlineData("Screenshot 2026-10-01 093015.png", ScreenshotTool.SnippingTool)]
    [InlineData("Screenshot 2026-10-01 093015 (2).png", ScreenshotTool.SnippingTool)]
    [InlineData("Captura de pantalla 2026-10-01 093015.PNG", ScreenshotTool.SnippingTool)]
    [InlineData("Screenshot (1).png", ScreenshotTool.WinPrtScn)]
    [InlineData("Screenshot (1234).png", ScreenshotTool.WinPrtScn)]
    [InlineData("\u200F\u200F\u05E6\u05D9\u05DC\u05D5\u05DD \u05DE\u05E1\u05DA (12).png", ScreenshotTool.WinPrtScn)]
    [InlineData("\u5C4F\u5E55\u622A\u56FE(3).png", ScreenshotTool.WinPrtScn)]
    [InlineData(@"C:\Users\x\Pictures\Screenshots\Screenshot (7).png", ScreenshotTool.WinPrtScn)]
    [InlineData("photo.png", ScreenshotTool.Other)]
    [InlineData("Screenshot 2026-10-01 093015.jpg", ScreenshotTool.Other)]
    [InlineData("Screenshot (1).jpg", ScreenshotTool.Other)]
    [InlineData("(1).png", ScreenshotTool.Other)]
    [InlineData("Screenshot 2026-10-01 0930.png", ScreenshotTool.Other)]
    public void Classify_MatchesTheShapeNotTheWord(string name, ScreenshotTool expected) =>
        Assert.Equal(expected, WindowsScreenshots.Classify(name));

    /// <summary>Images are read (any case); recordings, settings files and everything else are not.</summary>
    /// <param name="name">File name.</param>
    /// <param name="expected">Whether it is read.</param>
    [Theory]
    [InlineData("a.png", true)]
    [InlineData("a.JPG", true)]
    [InlineData("a.jpeg", true)]
    [InlineData("a.bmp", true)]
    [InlineData("a.gif", true)]
    [InlineData("a.jxr", true)]
    [InlineData("a.webp", true)]
    [InlineData("Screen Recording 2026-10-01 093015.mp4", false)]
    [InlineData("desktop.ini", false)]
    [InlineData("a.txt", false)]
    [InlineData("a", false)]
    public void IsImageFile(string name, bool expected) => Assert.Equal(expected, WindowsScreenshots.IsImageFile(name));

    /// <summary>
    /// Each tool gets its own source: Snipping Tool under its real process name (one Ignored apps entry skips its files
    /// and its clipboard copies), Win+PrtScn without a process (Explorer is not blamed), and other images apart.
    /// </summary>
    [Fact]
    public void SourceFor_NamesEachTool()
    {
        var snip = WindowsScreenshots.SourceFor(ScreenshotTool.SnippingTool, @"C:\Apps\SnippingTool\SnippingTool.exe");
        Assert.Equal(("SnippingTool", @"C:\Apps\SnippingTool\SnippingTool.exe", "Snipping Tool"), (snip.ProcessName, snip.ExecutablePath, snip.DisplayName));
        Assert.Equal(CaptureRules.NormalizeProcessName("SnippingTool.exe"), snip.ProcessName);

        var win = WindowsScreenshots.SourceFor(ScreenshotTool.WinPrtScn, @"C:\Apps\SnippingTool\SnippingTool.exe");
        Assert.Equal(("Win+PrtScn", (string?)null, "Win+PrtScn"), (win.ProcessName, win.ExecutablePath, win.DisplayName));
        Assert.Equal("Win+PrtScn", CaptureRules.NormalizeProcessName("Win+PrtScn"));

        var other = WindowsScreenshots.SourceFor(ScreenshotTool.Other, null);
        Assert.Equal(("Screenshots", "Screenshots folder"), (other.ProcessName, other.DisplayName));
    }

    /// <summary>A file written within the window is fresh; an older one was copied or moved in; clock skew counts as fresh.</summary>
    [Fact]
    public void IsFresh_UsesTheWindow()
    {
        Assert.True(WindowsScreenshots.IsFresh(TestData.Now.AddSeconds(-30), TestData.Now));
        Assert.True(WindowsScreenshots.IsFresh(TestData.Now - WindowsScreenshots.FreshWriteWindow, TestData.Now));
        Assert.False(WindowsScreenshots.IsFresh(TestData.Now.AddMinutes(-3), TestData.Now));
        Assert.False(WindowsScreenshots.IsFresh(TestData.Now.AddYears(-6), TestData.Now));
        Assert.True(WindowsScreenshots.IsFresh(TestData.Now.AddSeconds(5), TestData.Now));
    }

    /// <summary>
    /// Completeness from the bytes alone: PNG by its IEND chunk, JPEG by FFD9, GIF by its trailer, BMP by its declared
    /// size; a short file of such a format is incomplete; formats without an end marker cannot tell.
    /// </summary>
    [Fact]
    public void LooksComplete_PerFormat()
    {
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        byte[] iend = [0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];
        byte[] body = [0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 1, 2, 3];
        Assert.True(WindowsScreenshots.LooksComplete([.. signature, .. body, .. iend], "a.png"));
        Assert.False(WindowsScreenshots.LooksComplete([.. signature, .. body], "a.png"));
        Assert.False(WindowsScreenshots.LooksComplete([.. signature, .. body, .. iend[..^1]], "a.png"));
        Assert.False(WindowsScreenshots.LooksComplete([], "a.png"));
        Assert.False(WindowsScreenshots.LooksComplete([1, 2, 3], "a.png"));

        Assert.True(WindowsScreenshots.LooksComplete([0xFF, 0xD8, 0xFF, 0xE0, 7, 0xFF, 0xD9], "a.jpg"));
        Assert.False(WindowsScreenshots.LooksComplete([0xFF, 0xD8, 0xFF, 0xE0, 7], "a.jpg"));

        Assert.True(WindowsScreenshots.LooksComplete([.. "GIF89a"u8, 1, 2, 0x3B], "a.gif"));
        Assert.False(WindowsScreenshots.LooksComplete([.. "GIF89a"u8, 1, 2], "a.gif"));

        byte[] bmp = [(byte)'B', (byte)'M', 18, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 9, 9, 9, 9];
        Assert.True(WindowsScreenshots.LooksComplete(bmp, "a.bmp"));
        Assert.False(WindowsScreenshots.LooksComplete(bmp[..16], "a.bmp"));

        Assert.Null(WindowsScreenshots.LooksComplete([.. "RIFF"u8, 1, 2, 3, 4, .. "WEBP"u8], "a.webp"));
        Assert.Null(WindowsScreenshots.LooksComplete([], "a.jxr"));
    }

    /// <summary>
    /// Snipping Tool's settings: nothing stored = defaults (on, the Screenshots folder); the switch off; a chosen folder;
    /// an empty or mistyped value is ignored. Each value carries an 8-byte timestamp after its data.
    /// </summary>
    [Fact]
    public void SnippingToolSettings_Parse()
    {
        byte[] stamp = [1, 2, 3, 4, 5, 6, 7, 8];
        var none = SnippingToolSettings.Parse(new Dictionary<string, (uint, byte[])>());
        Assert.Equal(SnippingToolSettings.Defaults, none);
        Assert.True(none.SavesScreenshots);

        var off = SnippingToolSettings.Parse(new Dictionary<string, (uint, byte[])> { ["AutoSaveScreenshots"] = (SnippingToolSettings.BooleanType, [0, .. stamp]) });
        Assert.Equal(new SnippingToolSettings(false, false), off);
        Assert.False(off.SavesScreenshots);

        var on = SnippingToolSettings.Parse(new Dictionary<string, (uint, byte[])> { ["AutoSaveScreenshots"] = (SnippingToolSettings.BooleanType, [1, .. stamp]) });
        Assert.True(on.SavesScreenshots);

        byte[] folder = [.. Encoding.Unicode.GetBytes("D:\\BC-TEST Shots\0"), .. stamp];
        Assert.True(SnippingToolSettings.Parse(new Dictionary<string, (uint, byte[])> { ["AutoSaveScreenshotsLocation"] = (SnippingToolSettings.StringType, folder) }).HasCustomFolder);
        Assert.False(SnippingToolSettings.Parse(new Dictionary<string, (uint, byte[])> { ["AutoSaveScreenshotsLocation"] = (SnippingToolSettings.StringType, [0, 0, .. stamp]) }).HasCustomFolder);
        Assert.False(SnippingToolSettings.Parse(new Dictionary<string, (uint, byte[])> { ["AutoSaveScreenshotsLocation"] = (1, folder) }).HasCustomFolder);
        Assert.Null(SnippingToolSettings.Parse(new Dictionary<string, (uint, byte[])> { ["AutoSaveScreenshots"] = (SnippingToolSettings.BooleanType, [1]) }).AutoSaveScreenshots);
    }

    /// <summary>The command line names the filter (and two spellings) and the origin; the setting is on by default and persists.</summary>
    [Fact]
    public void CliNames_AndSetting()
    {
        foreach (var name in new[] { "snipping", "SNIP", "screenshots" })
        {
            Assert.True(CliCommandProcessor.TryParseFilter(name, out var filter));
            Assert.Equal(ClipFilter.Snipping, filter);
        }

        Assert.Contains("snipping", CliArguments.Help(CliCommands.List), StringComparison.Ordinal);
        Assert.True(new AppSettings().ImportWindowsScreenshots);

        using var temp = TestData.NewTempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var settings = new SettingsStore(path);
        settings.Load();
        settings.Update(s => s with { ImportWindowsScreenshots = false });
        Assert.False(new SettingsStore(path).Load().ImportWindowsScreenshots);
    }
}

/// <summary>
/// Tests for how the store and the history service treat Windows' screenshots: the Snipping tab's filter, the ShareX-like
/// hybrid merge rules, pause and ignored apps, and the one-time renaming of rows labeled "SnippingTool.exe".
/// </summary>
public sealed class WindowsScreenshotsStoreTests : IDisposable
{
    /// <summary>Snipping Tool's executable as installed on Windows 11 (package folder).</summary>
    private const string SnippingToolPath = @"C:\Program Files\WindowsApps\Microsoft.ScreenSketch_11.2607.23.0_x64__8wekyb3d8bbwe\SnippingTool\SnippingTool.exe";

    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private readonly ClipStore store;

    /// <summary>Creates a store over a temp database.</summary>
    public WindowsScreenshotsStoreTests()
    {
        store = new ClipStore(temp.DatabasePath);
        store.Initialize();
    }

    /// <summary>Deletes the temp database.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>
    /// The Snipping tab lists screenshot files, Snipping Tool's clipboard copies (by path, also under the old
    /// "SnippingTool.exe" label), and rows a screenshot import bumped (by the tools' names) — nothing else: not a
    /// look-alike executable, not ShareX, not an ordinary copy.
    /// </summary>
    [Fact]
    public void Query_SnippingFilter_MatchesFilesCopiesAndToolNames()
    {
        Upsert(Shot(1, TestData.Now.AddMinutes(-6), WindowsScreenshots.SnippingToolSource(SnippingToolPath)));
        Upsert(Shot(2, TestData.Now.AddMinutes(-5), WindowsScreenshots.WinPrtScnSource));
        Add(TestData.Text("BC-TEST copied by Snipping Tool", TestData.Now.AddMinutes(-4), source: new SourceAppInfo("SnippingTool", SnippingToolPath, "Snipping Tool")));
        Add(TestData.Text("BC-TEST old label", TestData.Now.AddMinutes(-3), source: new SourceAppInfo("SnippingTool", SnippingToolPath, "SnippingTool.exe")));
        Add(TestData.Text("BC-TEST bumped by Win+PrtScn", TestData.Now.AddMinutes(-2), source: WindowsScreenshots.WinPrtScnSource));
        Add(TestData.Text("BC-TEST look-alike", TestData.Now.AddMinutes(-1), source: new SourceAppInfo("NotSnippingTool", @"C:\Tools\NotSnippingTool.exe", "Not Snipping Tool")));
        Upsert(TestData.ShareXShot(3, TestData.Now.AddSeconds(-30)));
        Add(TestData.Text("BC-TEST notepad", TestData.Now));

        var snipping = store.Query(new ClipQuery { Filter = ClipFilter.Snipping, PinnedFirst = false });
        Assert.Equal(
            ["BC-TEST bumped by Win+PrtScn", "BC-TEST old label", "BC-TEST copied by Snipping Tool", ContentClassifier.DescribeImage(null, null), ContentClassifier.DescribeImage(null, null)],
            snipping.Select(e => e.Preview));
        Assert.All(snipping.TakeLast(2), e => Assert.Equal(ClipOrigin.WindowsScreenshot, e.Origin));
        Assert.Equal(8, store.Query(new ClipQuery()).Count);
    }

    /// <summary>
    /// A screenshot bumps an existing duplicate like a live copy (Snipping Tool's clipboard copy of the same snip, stored a
    /// moment earlier), taking the tool's name, but never lifts a tombstone and never survives a clear — like ShareX's.
    /// </summary>
    [Fact]
    public void Upsert_Screenshot_BumpsButRespectsTombstonesAndClears()
    {
        var copied = Add(TestData.Image(7))!.Entry;
        var merged = Upsert(Shot(7, TestData.Now.AddSeconds(1), WindowsScreenshots.WinPrtScnSource))!;
        Assert.Equal(copied.Id, merged.Entry.Id);
        Assert.Equal(2, merged.Entry.UseCount);
        Assert.Equal("Win+PrtScn", merged.Entry.SourceAppName);
        Assert.Contains(merged.Entry.Id, store.Query(new ClipQuery { Filter = ClipFilter.Snipping }).Select(e => e.Id));

        Assert.True(store.Delete(copied.Id, TestData.Now.AddMinutes(1)));
        Assert.Null(Upsert(Shot(7, TestData.Now.AddMinutes(2), WindowsScreenshots.WinPrtScnSource)));

        store.ClearUnpinned(TestData.Now.AddMinutes(10));
        Assert.Null(Upsert(Shot(8, TestData.Now.AddMinutes(9), WindowsScreenshots.WinPrtScnSource)));
        Assert.NotNull(Upsert(Shot(8, TestData.Now.AddMinutes(11), WindowsScreenshots.WinPrtScnSource)));
    }

    /// <summary>
    /// The history service treats a screenshot as a new event: pause drops it, Ignored apps skip each tool by its process
    /// name ("SnippingTool.exe" as typed by a user, "Win+PrtScn"), and a later shot of the same pixels moves to the top.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Service_ObeysPauseIgnoreAndBump()
    {
        var rules = new CaptureRules { Retention = RetentionPolicy.Unlimited, IsPaused = true };
        await using var service = new ClipHistoryService(store, null, () => rules);
        service.Start();
        var snip = WindowsScreenshots.SnippingToolSource(SnippingToolPath);
        Assert.Null(await service.AddAsync(Shot(1, TestData.Now, snip)));

        rules = rules with { IsPaused = false, IgnoredProcessNames = new HashSet<string>([CaptureRules.NormalizeProcessName("SnippingTool.exe"), "Win+PrtScn"], StringComparer.OrdinalIgnoreCase) };
        Assert.Null(await service.AddAsync(Shot(1, TestData.Now, snip)));
        Assert.Null(await service.AddAsync(Shot(2, TestData.Now, WindowsScreenshots.WinPrtScnSource)));
        Assert.NotNull(await service.AddAsync(Shot(3, TestData.Now, WindowsScreenshots.FolderSource)));

        rules = rules with { IgnoredProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) };
        var first = await service.AddAsync(Shot(1, TestData.Now.AddMinutes(-5), snip));
        await service.AddAsync(TestData.Text("BC-TEST newer text", TestData.Now.AddMinutes(-1)));
        var again = await service.AddAsync(Shot(1, TestData.Now, snip));
        Assert.Equal(first!.Id, again!.Id);
        Assert.Equal(first.Id, (await service.QueryAsync(new ClipQuery { PinnedFirst = false }, TestContext.Current.CancellationToken))[0].Id);
    }

    /// <summary>
    /// Rows labeled "SnippingTool.exe" by older builds become "Snipping Tool" once — only when their path is Snipping
    /// Tool's — and the fix-up never runs again.
    /// </summary>
    [Fact]
    public void Fixup_RenamesSnippingToolLabelsOnce()
    {
        var snip = Add(TestData.Text("BC-TEST snip copy", source: new SourceAppInfo("SnippingTool", SnippingToolPath, "SnippingTool.exe")))!.Entry;
        var other = Add(TestData.Text("BC-TEST other tool", TestData.Now.AddMinutes(1), source: new SourceAppInfo("Tool", @"C:\Tools\Tool.exe", "SnippingTool.exe")))!.Entry;
        var noPath = Add(TestData.Text("BC-TEST no path", TestData.Now.AddMinutes(2), source: new SourceAppInfo("SnippingTool", null, "SnippingTool.exe")))!.Entry;

        // The fix-up already ran when the fixture created this database; forget that to replay an old file.
        ExecuteRaw("DELETE FROM meta WHERE key = 'fixup.snipping_tool_name.v1';");
        new ClipStore(temp.DatabasePath).Initialize();
        Assert.Equal("Snipping Tool", store.GetEntry(snip.Id)!.SourceAppName);
        Assert.Equal("SnippingTool.exe", store.GetEntry(other.Id)!.SourceAppName);
        Assert.Equal("SnippingTool.exe", store.GetEntry(noPath.Id)!.SourceAppName);

        // Once applied it never runs again (a later row keeps whatever it was given), and the older fix-up's flag is untouched.
        var later = Add(TestData.Text("BC-TEST later", TestData.Now.AddMinutes(3), source: new SourceAppInfo("SnippingTool", SnippingToolPath, "SnippingTool.exe")))!.Entry;
        new ClipStore(temp.DatabasePath).Initialize();
        Assert.Equal("SnippingTool.exe", store.GetEntry(later.Id)!.SourceAppName);
    }

    /// <summary>The command line names the origin "screenshot".</summary>
    [Fact]
    public void CliItem_NamesTheOrigin()
    {
        var entry = Upsert(Shot(4, TestData.Now, WindowsScreenshots.WinPrtScnSource))!.Entry;
        Assert.Equal("screenshot", CliCommandProcessor.ToItem(entry).Origin);
        Assert.Equal("Win+PrtScn", CliCommandProcessor.ToItem(entry).Source);
    }

    /// <summary>A screenshot as the folder watch delivers it (fake PNG bytes: only hashing matters here).</summary>
    /// <param name="seed">Varies the bytes so different seeds are different images.</param>
    /// <param name="at">File write time.</param>
    /// <param name="source">The tool.</param>
    /// <returns>The capture.</returns>
    private static ClipCapture Shot(byte seed, DateTimeOffset at, SourceAppInfo source) => new()
    {
        Formats = [new ClipFormatData(ClipFormatNames.Png, Enumerable.Repeat(seed, 64).ToArray())],
        CapturedAtUtc = at,
        Origin = ClipOrigin.WindowsScreenshot,
        Source = source,
    };

    /// <summary>Stores a capture the way the history service does for a screenshot (a new event: duplicates are bumped).</summary>
    /// <param name="capture">The capture.</param>
    /// <returns>The upsert result, or <see langword="null"/> when suppressed.</returns>
    private UpsertResult? Upsert(ClipCapture capture) => store.Upsert(capture, ContentClassifier.Classify(capture)!, null, bumpIfExists: true);

    /// <summary>Stores a capture as a live copy (or an import, per its origin).</summary>
    /// <param name="capture">The capture.</param>
    /// <returns>The upsert result.</returns>
    private UpsertResult? Add(ClipCapture capture) =>
        store.Upsert(capture, ContentClassifier.Classify(capture)!, null, bumpIfExists: capture.Origin == ClipOrigin.Captured);

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
}
