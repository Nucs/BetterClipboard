using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Storage;
using BetterClipboard.Windows.Imaging;
using BetterClipboard.Windows.Integrations;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests for ShareX's folder patterns: the ShareX name tokens, the fixed root in front of a pattern, and
/// the minimal set of folders to watch.
/// </summary>
public sealed class ShareXFolderRuleTests
{
    /// <summary>
    /// A rule accepts exactly the folders its pattern can expand to: date tokens are digits, free-text
    /// tokens one folder name, arguments are skipped, and case-sensitive tokens (<c>%Y</c>) or a <c>%</c>
    /// that starts no token are literal.
    /// </summary>
    /// <param name="pattern">ShareX subfolder pattern.</param>
    /// <param name="folder">Folder of the file relative to the root ("" = directly in it).</param>
    /// <param name="expected">Whether ShareX could have saved there.</param>
    [Theory]
    [InlineData("%y-%mo", "2026-09", true)]
    [InlineData("%y-%mo", "Camera-Roll", false)]
    [InlineData("%y-%mo", "2026-09\\nested", false)]
    [InlineData("%y-%mo", "", false)]
    [InlineData("", "", true)]
    [InlineData("", "2026-09", false)]
    [InlineData("%pn\\%y", "Code\\2026", true)]
    [InlineData("%pn\\%y", "Code\\26", false)]
    [InlineData("Shots-%ra{10}", "Shots-AbC123xYz9", true)]
    [InlineData("Shots-%ra{10}", "Other-AbC123xYz9", false)]
    [InlineData("%mon2 %yy", "September 26", true)]
    [InlineData("%y\\%mo\\%d", "2026\\09\\25", true)]
    [InlineData("%Y", "%Y", true)]
    [InlineData("%Y", "2026", false)]
    [InlineData("100%", "100%", true)]
    [InlineData("%t", "", true)]
    public void Contains_FollowsTheShareXPattern(string pattern, string folder, bool expected)
    {
        var rule = new ShareXFolderRule(@"C:\Shots", pattern);
        var file = folder.Length == 0 ? @"C:\Shots\a.png" : $@"C:\Shots\{folder}\a.png";
        Assert.Equal(expected, rule.Contains(file));
    }

    /// <summary>The root matches case-insensitively and only as a whole folder name.</summary>
    [Fact]
    public void Contains_ComparesTheRootLikeWindows()
    {
        var rule = new ShareXFolderRule(@"C:\Shots\", "%y-%mo");
        Assert.Equal(@"C:\Shots", rule.Root);
        Assert.True(rule.Contains(@"c:\SHOTS\2026-09\A.PNG"));
        Assert.False(rule.Contains(@"C:\ShotsX\2026-09\a.png"));
        Assert.False(rule.Contains(@"relative\2026-09\a.png"));
        Assert.Throws<ArgumentException>(() => new ShareXFolderRule(@"relative\root", "%y"));
    }

    /// <summary>The default pattern becomes two digit groups.</summary>
    [Fact]
    public void ToRegex_DefaultPattern() => Assert.Equal(@"^\d{4}-\d{2}$", ShareXFolderRule.ToRegex("%y-%mo"));

    /// <summary>Rules compare by root (any case) and pattern (exact), so a re-located installation can be diffed.</summary>
    [Fact]
    public void Equality_IgnoresRootCaseOnly()
    {
        Assert.Equal(new ShareXFolderRule(@"C:\Shots", "%y"), new ShareXFolderRule(@"c:\shots\", "%y"));
        Assert.NotEqual(new ShareXFolderRule(@"C:\Shots", "%y"), new ShareXFolderRule(@"C:\Shots", "%Y"));
        Assert.Single(new[] { new ShareXFolderRule(@"C:\A", ""), new ShareXFolderRule(@"C:\a", "") }.Distinct());
    }

    /// <summary>Each root is watched once, and never inside another watched root.</summary>
    [Fact]
    public void MinimalRoots_DropsDuplicatesAndNestedFolders()
    {
        var roots = ShareXFolderRule.MinimalRoots(
        [
            new ShareXFolderRule(@"C:\A", "%y"),
            new ShareXFolderRule(@"C:\A\B", ""),
            new ShareXFolderRule(@"C:\AB", ""),
            new ShareXFolderRule(@"c:\a", "%pn"),
        ]);
        Assert.Equal([@"C:\A", @"C:\AB"], roots);
    }

    /// <summary>
    /// The fixed root ends before the first real token — never at a literal <c>%</c>, which would widen the
    /// watch to a whole drive — and a pattern with nothing fixed and absolute is refused.
    /// </summary>
    /// <param name="pattern">Expanded override pattern.</param>
    /// <param name="root">Expected root, or <see langword="null"/> when refused.</param>
    /// <param name="relative">Expected subfolder pattern.</param>
    [Theory]
    [InlineData(@"D:\Shots\%y-%mo", @"D:\Shots", "%y-%mo")]
    [InlineData(@"D:\Shots\Game-%pn\%y", @"D:\Shots", @"Game-%pn\%y")]
    [InlineData(@"D:\100%\Shots\%y", @"D:\100%\Shots", "%y")]
    [InlineData(@"D:\Shots", @"D:\Shots", "")]
    [InlineData(@"D:\Shots\", @"D:\Shots", "")]
    [InlineData(@"D:\%y", @"D:\", "%y")]
    [InlineData(@"\\server\share\Shots\%y", @"\\server\share\Shots", "%y")]
    [InlineData(@"%y\Shots", null, null)]
    [InlineData(@"relative\%y", null, null)]
    [InlineData("  ", null, null)]
    public void SplitPattern_FindsTheFixedRoot(string pattern, string? root, string? relative)
    {
        var parts = ShareXLocator.SplitPattern(pattern);
        Assert.Equal(root, parts?.Root);
        Assert.Equal(relative, parts?.Relative);
        Assert.Equal(root, ShareXLocator.FixedPrefix(pattern));
    }

    /// <summary>Special folders (any case) and environment variables expand; name tokens stay.</summary>
    [Fact]
    public void ExpandFolderVariables_LikeShareX()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        Assert.Equal(Path.Combine(pictures, "%y"), ShareXLocator.ExpandFolderVariables(@"%mypictures%\%y"));
        Assert.Equal(Environment.GetEnvironmentVariable("USERPROFILE") + @"\Shots", ShareXLocator.ExpandFolderVariables(@"%USERPROFILE%\Shots"));
        Assert.Equal("", ShareXLocator.ExpandFolderVariables(""));
    }
}

/// <summary>
/// Tests for <see cref="ShareXLocator.Resolve"/> against fake ShareX layouts in a temp folder: the
/// personal-folder precedence, ApplicationConfig/HotkeysConfig settings, and robustness to bad files.
/// </summary>
public sealed class ShareXLocatorTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", Guid.NewGuid().ToString("N"));
    private readonly string documents;
    private readonly string localAppData;
    private readonly string install;

    /// <summary>Creates the fake Documents, LocalAppData and program folders (no ShareX files yet).</summary>
    public ShareXLocatorTests()
    {
        documents = Directory.CreateDirectory(Path.Combine(temp, "Documents")).FullName;
        localAppData = Directory.CreateDirectory(Path.Combine(temp, "LocalAppData")).FullName;
        install = Path.Combine(temp, "Program Files", "ShareX");
    }

    /// <summary>Deletes the fake layout.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>No program folder and no personal folder: ShareX is not there (no tab, nothing watched).</summary>
    [Fact]
    public void Resolve_NothingThere_IsNotFound()
    {
        var found = ShareXLocator.Resolve(Inputs());
        Assert.False(found.IsInstalled);
        Assert.Empty(found.Folders);
        Assert.Empty(found.WatchFolders);
    }

    /// <summary>A personal folder with no settings means ShareX's defaults: Screenshots\yyyy-MM.</summary>
    [Fact]
    public void Resolve_PersonalFolderOnly_UsesDefaults()
    {
        var personal = Directory.CreateDirectory(Path.Combine(documents, "ShareX")).FullName;
        var found = ShareXLocator.Resolve(Inputs());
        Assert.True(found.IsInstalled);
        Assert.Null(found.ExecutablePath);
        Assert.Equal(personal, found.PersonalFolder);
        Assert.Equal([new ShareXFolderRule(Path.Combine(personal, "Screenshots"), "%y-%mo")], found.Folders);
        Assert.Equal("-thumbnail", found.ThumbnailSuffix);
        Assert.Equal("test", found.DetectedBy);
    }

    /// <summary>Installed but never started (no personal folder yet): installed, with the default folder to wait for.</summary>
    [Fact]
    public void Resolve_InstalledNeverStarted()
    {
        CreateInstall();
        var found = ShareXLocator.Resolve(Inputs(install));
        Assert.True(found.IsInstalled);
        Assert.Equal(Path.Combine(install, "ShareX.exe"), found.ExecutablePath);
        Assert.Equal(Path.Combine(documents, "ShareX", "Screenshots"), found.Folders.Single().Root);
    }

    /// <summary>Portable beats the registry value, which beats PersonalPath.cfg, which beats the default.</summary>
    [Fact]
    public void Resolve_PersonalFolderPrecedence()
    {
        CreateInstall();
        var configured = Directory.CreateDirectory(Path.Combine(temp, "FromCfg")).FullName;
        Directory.CreateDirectory(Path.Combine(documents, "ShareX"));
        File.WriteAllText(Path.Combine(documents, "ShareX", "PersonalPath.cfg"), configured + Environment.NewLine);
        Assert.Equal(configured, ShareXLocator.Resolve(Inputs(install)).PersonalFolder);

        var registry = Directory.CreateDirectory(Path.Combine(temp, "FromRegistry")).FullName;
        Assert.Equal(registry, ShareXLocator.Resolve(Inputs(install, registry)).PersonalFolder);

        File.WriteAllText(Path.Combine(install, "Portable"), string.Empty);
        Assert.Equal(Path.Combine(install, "ShareX"), ShareXLocator.Resolve(Inputs(install, registry)).PersonalFolder);
    }

    /// <summary>PersonalPath.cfg next to the exe wins over the one in Documents, and relative entries are relative to the exe.</summary>
    [Fact]
    public void Resolve_CfgNextToExe_RelativeToProgramFolder()
    {
        CreateInstall();
        File.WriteAllText(Path.Combine(install, "PersonalPath.cfg"), "Data");
        Directory.CreateDirectory(Path.Combine(documents, "ShareX"));
        File.WriteAllText(Path.Combine(documents, "ShareX", "PersonalPath.cfg"), Path.Combine(temp, "Loser"));
        Assert.Equal(Path.Combine(install, "Data"), ShareXLocator.Resolve(Inputs(install)).PersonalFolder);
    }

    /// <summary>The pre-migration PersonalPath.cfg in %LOCALAPPDATA%\ShareX is still honored.</summary>
    [Fact]
    public void Resolve_LegacyCfgInLocalAppData()
    {
        var legacy = Directory.CreateDirectory(Path.Combine(temp, "Legacy")).FullName;
        Directory.CreateDirectory(Path.Combine(localAppData, "ShareX"));
        File.WriteAllText(Path.Combine(localAppData, "ShareX", "PersonalPath.cfg"), legacy);
        CreateInstall();
        Assert.Equal(legacy, ShareXLocator.Resolve(Inputs(install)).PersonalFolder);
    }

    /// <summary>
    /// ApplicationConfig.json: a missing custom folder falls back to CustomScreenshotsPath2, the subfolder
    /// patterns (incl. an explicitly empty one and the window-title one) become rules, and the default task
    /// settings add an override folder and a thumbnail name. Comments and trailing commas are tolerated.
    /// </summary>
    [Fact]
    public void Resolve_ReadsApplicationConfig()
    {
        var personal = Directory.CreateDirectory(Path.Combine(documents, "ShareX")).FullName;
        var fallback = Directory.CreateDirectory(Path.Combine(temp, "Fallback")).FullName;
        var overrideRoot = Path.Combine(temp, "Override");
        WriteJson(Path.Combine(personal, "ApplicationConfig.json"), $$"""
            {
              // a hand edit may leave comments
              "UseCustomScreenshotsPath": true,
              "CustomScreenshotsPath": {{Json(Path.Combine(temp, "Missing"))}},
              "CustomScreenshotsPath2": {{Json(fallback)}},
              "SaveImageSubFolderPattern": "",
              "SaveImageSubFolderPatternWindow": "%pn",
              "DefaultTaskSettings": {
                "OverrideScreenshotsFolder": true,
                "ScreenshotsFolder": {{Json(overrideRoot + @"\%y")}},
                "ImageSettings": { "ThumbnailName": "_thumb", },
              },
            }
            """);

        var found = ShareXLocator.Resolve(Inputs());
        Assert.Equal(
            [new ShareXFolderRule(fallback, ""), new ShareXFolderRule(fallback, "%pn"), new ShareXFolderRule(overrideRoot, "%y")],
            found.Folders);
        Assert.Equal("_thumb", found.ThumbnailSuffix);
        Assert.Equal([fallback, overrideRoot], found.WatchFolders);
    }

    /// <summary>An existing custom primary folder wins over the fallback; a disabled custom path is ignored.</summary>
    [Fact]
    public void Resolve_CustomPathPrimaryAndDisabled()
    {
        var personal = Directory.CreateDirectory(Path.Combine(documents, "ShareX")).FullName;
        var primary = Directory.CreateDirectory(Path.Combine(temp, "Primary")).FullName;
        var fallback = Directory.CreateDirectory(Path.Combine(temp, "Fallback")).FullName;
        var config = Path.Combine(personal, "ApplicationConfig.json");
        WriteJson(config, $$"""{ "UseCustomScreenshotsPath": true, "CustomScreenshotsPath": {{Json(primary)}}, "CustomScreenshotsPath2": {{Json(fallback)}} }""");
        Assert.Equal(primary, ShareXLocator.Resolve(Inputs()).Folders.Single().Root);

        WriteJson(config, $$"""{ "UseCustomScreenshotsPath": false, "CustomScreenshotsPath": {{Json(primary)}} }""");
        Assert.Equal(Path.Combine(personal, "Screenshots"), ShareXLocator.Resolve(Inputs()).Folders.Single().Root);
    }

    /// <summary>Hotkeys that override the folder add rules (from HotkeysConfig.json, or the custom path ApplicationConfig names).</summary>
    [Fact]
    public void Resolve_HotkeyOverrides()
    {
        var personal = Directory.CreateDirectory(Path.Combine(documents, "ShareX")).FullName;
        var hotkeyRoot = Path.Combine(temp, "Hotkey");
        WriteJson(Path.Combine(personal, "HotkeysConfig.json"), $$"""
            {
              "Hotkeys": [
                { "TaskSettings": { "OverrideScreenshotsFolder": true, "ScreenshotsFolder": {{Json(hotkeyRoot + @"\%y-%mo")}} } },
                { "TaskSettings": { "OverrideScreenshotsFolder": false, "ScreenshotsFolder": {{Json(Path.Combine(temp, "NotUsed"))}} } },
                { "TaskSettings": { "OverrideScreenshotsFolder": true, "ScreenshotsFolder": "%y\\relative" } },
                "not an object"
              ]
            }
            """);
        var found = ShareXLocator.Resolve(Inputs());
        Assert.Equal([new ShareXFolderRule(Path.Combine(personal, "Screenshots"), "%y-%mo"), new ShareXFolderRule(hotkeyRoot, "%y-%mo")], found.Folders);

        var custom = Path.Combine(temp, "custom-hotkeys.json");
        WriteJson(custom, $$"""{ "Hotkeys": [ { "TaskSettings": { "OverrideScreenshotsFolder": true, "ScreenshotsFolder": {{Json(Path.Combine(temp, "Custom"))}} } } ] }""");
        WriteJson(Path.Combine(personal, "ApplicationConfig.json"), $$"""{ "CustomHotkeysConfigPath": {{Json(custom)}} }""");
        Assert.Equal(Path.Combine(temp, "Custom"), ShareXLocator.Resolve(Inputs()).Folders[^1].Root);
    }

    /// <summary>Unreadable settings degrade to ShareX's defaults instead of hiding ShareX.</summary>
    [Fact]
    public void Resolve_BrokenJson_FallsBackToDefaults()
    {
        var personal = Directory.CreateDirectory(Path.Combine(documents, "ShareX")).FullName;
        File.WriteAllText(Path.Combine(personal, "ApplicationConfig.json"), "{ not json");
        File.WriteAllText(Path.Combine(personal, "HotkeysConfig.json"), "[1, 2");
        var found = ShareXLocator.Resolve(Inputs());
        Assert.True(found.IsInstalled);
        Assert.Equal([new ShareXFolderRule(Path.Combine(personal, "Screenshots"), "%y-%mo")], found.Folders);
    }

    /// <summary>Special folders in a custom path expand (the folder need not exist without a fallback).</summary>
    [Fact]
    public void Resolve_ExpandsSpecialFolders()
    {
        var personal = Directory.CreateDirectory(Path.Combine(documents, "ShareX")).FullName;
        WriteJson(Path.Combine(personal, "ApplicationConfig.json"), """{ "UseCustomScreenshotsPath": true, "CustomScreenshotsPath": "%MyPictures%\\BC-TEST-ShareX" }""");
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "BC-TEST-ShareX"),
            ShareXLocator.Resolve(Inputs()).Folders.Single().Root);
    }

    /// <summary>
    /// The override variable points the integration at a fake personal folder and ignores the machine's
    /// real ShareX — what isolated dev runs rely on.
    /// </summary>
    [Fact]
    public void Locate_OverrideVariable_ReplacesTheRealInstall()
    {
        var personal = Directory.CreateDirectory(Path.Combine(temp, "FakeShareX")).FullName;
        var previous = Environment.GetEnvironmentVariable(ShareXLocator.PersonalFolderOverrideVariable);
        Environment.SetEnvironmentVariable(ShareXLocator.PersonalFolderOverrideVariable, personal);
        try
        {
            var found = ShareXLocator.Locate();
            Assert.True(found.IsInstalled);
            Assert.Null(found.ExecutablePath);
            Assert.Equal(personal, found.PersonalFolder);
            Assert.Contains(ShareXLocator.PersonalFolderOverrideVariable, found.DetectedBy, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ShareXLocator.PersonalFolderOverrideVariable, previous);
        }
    }

    /// <summary>Candidates: image extensions (any case), not thumbnails, only in the rules' folders.</summary>
    [Fact]
    public void IsScreenshotFile_FiltersByExtensionThumbnailAndFolder()
    {
        var shareX = new ShareXInstallation(true, null, temp, [new ShareXFolderRule(@"C:\Shots", "%y-%mo")], "-thumbnail", "test");
        Assert.True(shareX.IsScreenshotFile(@"C:\Shots\2026-09\Code_x1.PNG"));
        Assert.True(shareX.IsScreenshotFile(@"C:\Shots\2026-09\Code_x1.webp"));
        Assert.False(shareX.IsScreenshotFile(@"C:\Shots\2026-09\Code_x1-thumbnail.jpg"));
        Assert.False(shareX.IsScreenshotFile(@"C:\Shots\2026-09\Recording.mp4"));
        Assert.False(shareX.IsScreenshotFile(@"C:\Shots\Other\Code_x1.png"));
        Assert.False(ShareXInstallation.NotFound.IsScreenshotFile(@"C:\Shots\2026-09\Code_x1.png"));
    }

    /// <summary>Creates the fake program folder with an (empty) ShareX.exe.</summary>
    private void CreateInstall()
    {
        Directory.CreateDirectory(install);
        File.WriteAllBytes(Path.Combine(install, "ShareX.exe"), []);
    }

    /// <summary>Locator inputs over the fake layout.</summary>
    /// <param name="installDirectory">Program folder, if "installed".</param>
    /// <param name="registryPersonalPath">The registry value, if set.</param>
    /// <returns>The inputs.</returns>
    private ShareXLocatorInputs Inputs(string? installDirectory = null, string? registryPersonalPath = null) =>
        new(installDirectory, registryPersonalPath, documents, localAppData, "test");

    /// <summary>A JSON string literal for a path (escapes backslashes).</summary>
    /// <param name="value">Value.</param>
    /// <returns>The literal with quotes.</returns>
    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    /// <summary>Writes a settings file (UTF-8 with BOM, like ShareX).</summary>
    /// <param name="path">File.</param>
    /// <param name="json">Content.</param>
    private static void WriteJson(string path, string json) => File.WriteAllText(path, json, new System.Text.UTF8Encoding(true));
}

/// <summary>
/// Tests for <see cref="ShareXScreenshotWatcher"/> on a temp folder laid out like ShareX's: one import per
/// save, no half-written files, the skip rules, the "handled" signal, catch-up, and folders created later.
/// </summary>
public sealed class ShareXScreenshotWatcherTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", Guid.NewGuid().ToString("N"), "Screenshots");
    private readonly string month;
    private readonly ShareXInstallation installation;
    private readonly ConcurrentQueue<ClipCapture> delivered = new();
    private readonly ConcurrentQueue<DateTimeOffset> handled = new();

    /// <summary>Creates the screenshots root with a month folder.</summary>
    public ShareXScreenshotWatcherTests()
    {
        month = Directory.CreateDirectory(Path.Combine(root, "2026-09")).FullName;
        installation = new ShareXInstallation(true, @"C:\Program Files\ShareX\ShareX.exe", Path.GetDirectoryName(root)!, [new ShareXFolderRule(root, "%y-%mo")], "-thumbnail", "test");
    }

    /// <summary>Deletes the temp tree.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>A saved screenshot becomes exactly one ShareX capture: original PNG bytes plus a DIBV5, attributed to ShareX.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Watch_ImportsASavedScreenshotOnce()
    {
        using var watcher = CreateWatcher();
        watcher.Start();
        Assert.Equal([root], watcher.WatchedFolders);

        var png = await PngAsync(40, 30, 1);
        await File.WriteAllBytesAsync(Path.Combine(month, "Code_AbCdEf1234.png"), png, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => delivered.Count == 1);
        await Task.Delay(600, TestContext.Current.CancellationToken); // late events of the same save must not import it again

        var capture = Assert.Single(delivered);
        Assert.Equal(ClipOrigin.ShareX, capture.Origin);
        Assert.Equal("ShareX", capture.Source!.ProcessName);
        Assert.Equal(installation.ExecutablePath, capture.Source.ExecutablePath);
        Assert.Equal([ClipFormatNames.Png, ClipFormatNames.DibV5], capture.Formats.Select(f => f.Name));
        Assert.Equal(png, capture.Formats[0].Data);
        Assert.Equal(capture.CapturedAtUtc, Assert.Single(handled));
    }

    /// <summary>While ShareX still holds the file open for writing, nothing is read; the complete file is imported once it closes.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Watch_WaitsForTheWriterToFinish()
    {
        using var watcher = CreateWatcher();
        watcher.Start();
        var png = await PngAsync(64, 48, 2);
        using (var writer = new FileStream(Path.Combine(month, "partial.png"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            writer.Write(png, 0, png.Length / 2);
            writer.Flush();
            await Task.Delay(700, TestContext.Current.CancellationToken); // well past the settle delay
            Assert.Empty(delivered);
            writer.Write(png, png.Length / 2, png.Length - (png.Length / 2));
        }

        await WaitUntilAsync(() => delivered.Count == 1);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.Equal(png, Assert.Single(delivered).Formats[0].Data);
    }

    /// <summary>
    /// Thumbnails, non-images, images outside ShareX's folder pattern and animated GIFs (recordings) are
    /// skipped; the recording still counts as handled so the catch-up never retries it.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Watch_SkipsThumbnailsOtherFilesAndRecordings()
    {
        using var watcher = CreateWatcher();
        watcher.Start();
        var png = await PngAsync(16, 16, 3);
        await File.WriteAllBytesAsync(Path.Combine(month, "shot-thumbnail.png"), png, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(month, "notes.txt"), "BC-TEST", TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(month, "recording.mp4"), [0, 0, 0, 24], TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(root, "Camera-Roll"));
        await File.WriteAllBytesAsync(Path.Combine(root, "Camera-Roll", "photo.png"), png, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(month, "recording.gif"), await AnimatedGifAsync(), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => handled.Count == 1); // the GIF was read and skipped

        var sentinel = await PngAsync(20, 10, 4);
        await File.WriteAllBytesAsync(Path.Combine(month, "real.png"), sentinel, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => delivered.Count == 1);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.Equal(sentinel, Assert.Single(delivered).Formats[0].Data);
        Assert.Equal(2, handled.Count);
    }

    /// <summary>A screenshot the history refused (capture paused) is still reported as handled, with its write time.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Watch_RefusedScreenshot_IsStillHandled()
    {
        using var watcher = CreateWatcher(accept: false);
        watcher.Start();
        var path = Path.Combine(month, "paused.png");
        await File.WriteAllBytesAsync(path, await PngAsync(8, 8, 5), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => !handled.IsEmpty);
        Assert.Equal(new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero), Assert.Single(handled));
    }

    /// <summary>Catch-up imports only files written after the marker, oldest first, capped to the newest N.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task CatchUp_NewerThanMarker_OldestFirst_Capped()
    {
        var now = DateTime.UtcNow;
        for (int hours = 4; hours >= 1; hours--)
        {
            var path = Path.Combine(month, $"shot{hours}.png");
            await File.WriteAllBytesAsync(path, await PngAsync(8, 8, (byte)(10 + hours)), TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(path, now.AddHours(-hours));
        }

        await File.WriteAllBytesAsync(Path.Combine(month, "shot-thumbnail.png"), await PngAsync(8, 8, 99), TestContext.Current.CancellationToken);

        using var watcher = CreateWatcher();
        int imported = await watcher.CatchUpAsync(new DateTimeOffset(now.AddHours(-3.5), TimeSpan.Zero), 2, TestContext.Current.CancellationToken);
        Assert.Equal(2, imported);
        Assert.Equal([now.AddHours(-2), now.AddHours(-1)], delivered.Select(c => c.CapturedAtUtc.UtcDateTime));

        // Again with a larger cap: the two already handled are skipped; only the one the cap left out arrives.
        Assert.Equal(1, await watcher.CatchUpAsync(new DateTimeOffset(now.AddHours(-3.5), TimeSpan.Zero), 10, TestContext.Current.CancellationToken));
        Assert.Equal(now.AddHours(-3), delivered.Last().CapturedAtUtc.UtcDateTime);
    }

    /// <summary>A folder that does not exist yet is not watched; once it exists, <see cref="ShareXScreenshotWatcher.Update"/> starts watching it.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Update_PicksUpAFolderCreatedLater()
    {
        var later = Path.Combine(Path.GetDirectoryName(root)!, "Later");
        var withLater = installation with { Folders = [.. installation.Folders, new ShareXFolderRule(later, "")] };
        using var watcher = CreateWatcher(withLater);
        watcher.Start();
        Assert.Equal([root], watcher.WatchedFolders);
        Assert.False(watcher.Update(withLater));

        Directory.CreateDirectory(later);
        Assert.True(watcher.Update(withLater));
        Assert.Equal([root, later], watcher.WatchedFolders);

        await File.WriteAllBytesAsync(Path.Combine(later, "direct.png"), await PngAsync(8, 8, 6), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => delivered.Count == 1);
    }

    /// <summary>Creates a watcher that records deliveries and handled times.</summary>
    /// <param name="target">Installation (default: the test layout).</param>
    /// <param name="accept">What the fake history answers.</param>
    /// <returns>The watcher (not started).</returns>
    private ShareXScreenshotWatcher CreateWatcher(ShareXInstallation? target = null, bool accept = true)
    {
        var watcher = new ShareXScreenshotWatcher(
            target ?? installation,
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

    /// <summary>Encodes a small opaque image as a real PNG.</summary>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="seed">Varies the pixels.</param>
    /// <returns>PNG bytes.</returns>
    internal static Task<byte[]> PngAsync(int width, int height, byte seed) =>
        ImageCodec.ToPngAsync(DibImage.ToBmpFile(DibImage.CreateDibV5(Pixels(width, height, seed), width, height)), TestContext.Current.CancellationToken);

    /// <summary>Encodes a two-frame GIF (what a ShareX GIF screen recording is).</summary>
    /// <returns>GIF bytes.</returns>
    private static async Task<byte[]> AnimatedGifAsync()
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.GifEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 8, 8, 96, 96, Pixels(8, 8, 1));
        await encoder.GoToNextFrameAsync();
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 8, 8, 96, 96, Pixels(8, 8, 200));
        await encoder.FlushAsync();
        stream.Seek(0);
        var buffer = await stream.ReadAsync(new global::Windows.Storage.Streams.Buffer((uint)stream.Size), (uint)stream.Size, InputStreamOptions.None);
        return buffer.ToArray();
    }

    /// <summary>Opaque BGRA pixels varied by <paramref name="seed"/>.</summary>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="seed">Seed.</param>
    /// <returns>Top-down BGRA8.</returns>
    private static byte[] Pixels(int width, int height, byte seed)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(seed + i);
            pixels[i + 1] = seed;
            pixels[i + 2] = (byte)(i / 4);
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    /// <summary>Polls until <paramref name="condition"/> holds (10 s cap).</summary>
    /// <param name="condition">Condition.</param>
    /// <returns>A task.</returns>
    /// <exception cref="TimeoutException">It never held.</exception>
    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not become true within 10 s.");
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>
/// Tests for <see cref="ShareXIntegration"/> over a real history service: the catch-up marker's life cycle
/// (first activation, restart catch-up, off clears it) and the not-installed case.
/// </summary>
public sealed class ShareXIntegrationTests : IAsyncLifetime
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", Guid.NewGuid().ToString("N"));
    private string month = null!;
    private ShareXInstallation installation = null!;
    private ClipHistoryService history = null!;

    /// <summary>Creates the fake ShareX folders and a history over a temp store.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask InitializeAsync()
    {
        var root = Path.Combine(temp, "Screenshots");
        month = Directory.CreateDirectory(Path.Combine(root, "2026-09")).FullName;
        installation = new ShareXInstallation(true, null, temp, [new ShareXFolderRule(root, "%y-%mo")], "-thumbnail", "test");
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
    /// First activation: the marker becomes "now" and the existing archive is not imported, while a
    /// screenshot saved afterwards arrives live and advances the marker.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task FirstActivation_NoBackfill_ThenLive()
    {
        var old = Path.Combine(month, "archive.png");
        await File.WriteAllBytesAsync(old, await ShareXScreenshotWatcherTests.PngAsync(8, 8, 1), TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));

        var now = DateTimeOffset.UtcNow;
        await using var integration = new ShareXIntegration(history, () => long.MaxValue, () => installation, new FixedTime(now));
        await integration.StartAsync(enable: true);
        Assert.True(integration.IsWatching);
        Assert.Equal(now, Marker(await history.GetStateValueAsync(ShareXIntegration.LastSeenStateName)));

        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Empty(await ShareXItemsAsync());

        await File.WriteAllBytesAsync(Path.Combine(month, "new.png"), await ShareXScreenshotWatcherTests.PngAsync(8, 8, 2), TestContext.Current.CancellationToken);
        await ShareXScreenshotWatcherTests.WaitUntilAsync(() => ShareXItemsAsync().Result.Count == 1);
        Assert.Equal(1, integration.ImportedThisSession);
        await ShareXScreenshotWatcherTests.WaitUntilAsync(() => Marker(history.GetStateValueAsync(ShareXIntegration.LastSeenStateName).Result) > now);
    }

    /// <summary>After a restart, screenshots written after the stored marker are imported; older ones are not.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Restart_CatchesUpSinceTheMarker()
    {
        // Whole seconds: the history keeps milliseconds, file times keep 100 ns ticks.
        var marker = DateTime.UtcNow.AddMinutes(-10);
        marker = new DateTime(marker.Ticks - (marker.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        await history.SetStateValueAsync(ShareXIntegration.LastSeenStateName, marker.ToString("o", CultureInfo.InvariantCulture));
        var before = Path.Combine(month, "before.png");
        var after = Path.Combine(month, "after.png");
        await File.WriteAllBytesAsync(before, await ShareXScreenshotWatcherTests.PngAsync(8, 8, 3), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(after, await ShareXScreenshotWatcherTests.PngAsync(8, 8, 4), TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(before, marker.AddMinutes(-1));
        File.SetLastWriteTimeUtc(after, marker.AddMinutes(1));

        await using var integration = new ShareXIntegration(history, () => long.MaxValue, () => installation);
        await integration.StartAsync(enable: true);
        await ShareXScreenshotWatcherTests.WaitUntilAsync(() => ShareXItemsAsync().Result.Count == 1);
        Assert.Equal(new DateTimeOffset(marker.AddMinutes(1), TimeSpan.Zero), (await ShareXItemsAsync()).Single().LastUsedUtc);
        await ShareXScreenshotWatcherTests.WaitUntilAsync(() => Marker(history.GetStateValueAsync(ShareXIntegration.LastSeenStateName).Result) == new DateTimeOffset(marker.AddMinutes(1), TimeSpan.Zero));
    }

    /// <summary>Off stops watching and clears the marker; on again starts fresh (nothing taken while off is imported).</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Off_ClearsTheMarker_OnStartsFresh()
    {
        await using var integration = new ShareXIntegration(history, () => long.MaxValue, () => installation);
        await integration.StartAsync(enable: true);
        await ShareXScreenshotWatcherTests.WaitUntilAsync(() => !string.IsNullOrEmpty(history.GetStateValueAsync(ShareXIntegration.LastSeenStateName).Result));

        await integration.SetEnabledAsync(false);
        Assert.False(integration.IsWatching);
        Assert.Equal(string.Empty, await history.GetStateValueAsync(ShareXIntegration.LastSeenStateName));

        await File.WriteAllBytesAsync(Path.Combine(month, "while-off.png"), await ShareXScreenshotWatcherTests.PngAsync(8, 8, 5), TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(Path.Combine(month, "while-off.png"), DateTime.UtcNow.AddSeconds(-5));
        await integration.SetEnabledAsync(true);
        Assert.True(integration.IsWatching);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Empty(await ShareXItemsAsync());
    }

    /// <summary>Without ShareX nothing is watched, whatever the setting.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task NotInstalled_WatchesNothing()
    {
        await using var integration = new ShareXIntegration(history, () => long.MaxValue, () => ShareXInstallation.NotFound);
        await integration.StartAsync(enable: true);
        Assert.False(integration.Installation.IsInstalled);
        Assert.False(integration.IsWatching);
        Assert.Empty(integration.WatchedFolders);
    }

    /// <summary>The ShareX tab's items.</summary>
    /// <returns>The entries.</returns>
    private Task<IReadOnlyList<ClipEntry>> ShareXItemsAsync() =>
        history.QueryAsync(new ClipQuery { Filter = ClipFilter.ShareX }, TestContext.Current.CancellationToken);

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
