using System.Globalization;
using System.Text.Json;
using BetterClipboard.Core.Cli;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;
using Microsoft.Data.Sqlite;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// A picture's source file (CLAUDE.md §2.49): the store's columns and merge rules, an older store gaining them, the one-time
/// search's store side, the service's events, bclip's JSON, the setting, and the pure pieces — the format detector, the
/// temporary pictures' names, saves and clean-up, and the texts.
/// </summary>
public sealed class SourceFileTests : IAsyncLifetime
{
    /// <summary>A ShareX screenshot's file as the watcher reads it.</summary>
    private static readonly SourceFile ShareXFile = new(
        @"C:\Users\BC-TEST\Documents\ShareX\Screenshots\2026-11\chrome_Ab12Cd34Ef.png", 123_456,
        TestData.Now.AddSeconds(-1), TestData.Now, "PNG");

    /// <summary>A newer save of the same picture elsewhere.</summary>
    private static readonly SourceFile NewerFile = new(
        @"D:\BC-TEST\Shots\copy of it.jpg", 98_765, TestData.Now.AddHours(1), TestData.Now.AddHours(1), "JPEG");

    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private readonly List<ClipChangedEventArgs> events = [];
    private ClipStore store = null!;
    private ClipHistoryService? service;

    /// <summary>Creates a store over a temp database.</summary>
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

    /// <summary>A screenshot stored with its file reads back with every detail, to the millisecond.</summary>
    [Fact]
    public void Insert_KeepsTheFileAndItsDetails()
    {
        var entry = Store(Picture(1, ClipOrigin.ShareX, ShareXFile), bump: true);
        Assert.Equal(ShareXFile.Path, entry.File!.Path);
        Assert.Equal(123_456, entry.File.SizeBytes);
        Assert.Equal(ShareXFile.CreatedUtc, entry.File.CreatedUtc);
        Assert.Equal(ShareXFile.ModifiedUtc, entry.File.ModifiedUtc);
        Assert.Equal("PNG", entry.File.Format);
        Assert.Equal("chrome_Ab12Cd34Ef.png", entry.File.Name);
        Assert.Equal(".png", entry.File.Extension);
        Assert.Equal(@"C:\Users\BC-TEST\Documents\ShareX\Screenshots\2026-11", entry.File.Folder);

        // A copy of no file reads without one.
        Assert.Null(Store(Picture(2, ClipOrigin.Captured, file: null), bump: true).File);
    }

    /// <summary>
    /// A bump replaces the file as a whole with a newer one (never a path with the old one's dates), and keeps it when the
    /// capture has none — the clipboard copy ShareX makes of the same picture, which may come after the file.
    /// </summary>
    [Fact]
    public void Bump_ReplacesTheFileWholeOrKeepsIt()
    {
        Store(Picture(1, ClipOrigin.ShareX, ShareXFile), bump: true);
        var copiedAfter = Store(Picture(1, ClipOrigin.Captured, file: null, at: TestData.Now.AddMinutes(1)), bump: true);
        Assert.Equal(ShareXFile, copiedAfter.File);

        var newer = Store(Picture(1, ClipOrigin.ShareX, NewerFile, at: TestData.Now.AddHours(1)), bump: true);
        Assert.Equal(NewerFile, newer.File);

        // A newer file whose details are unknown replaces the dates too, rather than mixing two files.
        var bare = new SourceFile(@"E:\BC-TEST\bare.png", null, null, null, null);
        var replaced = Store(Picture(1, ClipOrigin.ShareX, bare, at: TestData.Now.AddHours(2)), bump: true);
        Assert.Equal(bare, replaced.File);
    }

    /// <summary>The order the other way round: a clipboard copy first, then its file, gains the file.</summary>
    [Fact]
    public void Bump_AFileAfterTheCopy_AddsIt()
    {
        var copy = Store(Picture(7, ClipOrigin.Captured, file: null), bump: true);
        Assert.Null(copy.File);
        var withFile = Store(Picture(7, ClipOrigin.ShareX, ShareXFile, at: TestData.Now.AddSeconds(2)), bump: true);
        Assert.Equal(copy.Id, withFile.Id);
        Assert.Equal(ShareXFile, withFile.File);
    }

    /// <summary>An import (no bump) adds a file only to a row that has none, and never replaces one.</summary>
    [Fact]
    public void Import_FillsAMissingFileOnly()
    {
        var copy = Store(Picture(3, ClipOrigin.Captured, file: null), bump: true);
        var imported = Upsert(Picture(3, ClipOrigin.WindowsHistory, ShareXFile), bump: false)!;
        Assert.True(imported.Changed);
        Assert.Equal(copy.Id, imported.Entry.Id);
        Assert.Equal(ShareXFile, imported.Entry.File);
        Assert.Equal(copy.LastUsedUtc, imported.Entry.LastUsedUtc); // an import never reorders

        var again = Upsert(Picture(3, ClipOrigin.WindowsHistory, NewerFile), bump: false)!;
        Assert.False(again.Changed);
        Assert.Equal(ShareXFile, again.Entry.File);
    }

    /// <summary>
    /// A store from before the columns gets them at the next start: existing rows read without a file, and new ones keep theirs.
    /// </summary>
    [Fact]
    public void Columns_AddedToAnOlderStore()
    {
        long id = Store(Picture(4, ClipOrigin.Captured, file: null), bump: true).Id;
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "ALTER TABLE clips DROP COLUMN file_path; ALTER TABLE clips DROP COLUMN file_size; ALTER TABLE clips DROP COLUMN file_created_utc; " +
                "ALTER TABLE clips DROP COLUMN file_modified_utc; ALTER TABLE clips DROP COLUMN file_format;";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        store = new ClipStore(temp.DatabasePath);
        store.Initialize();
        Assert.Null(store.GetEntry(id)!.File);
        Assert.Equal(ShareXFile, Store(Picture(5, ClipOrigin.ShareX, ShareXFile), bump: true).File);
    }

    /// <summary>
    /// The search's candidates: pictures of the tab without a file, newest first — not ones with a file, not text, not
    /// another tab's; a tab without a filter is refused.
    /// </summary>
    [Fact]
    public void FindPicturesWithoutFile_ListsTheTabsPicturesWithoutOne()
    {
        var shareXSource = new SourceAppInfo("ShareX", @"C:\Program Files\ShareX\ShareX.exe", "ShareX");
        var older = Store(Picture(10, ClipOrigin.ShareX, file: null, at: TestData.Now.AddHours(-2)), bump: true);
        var copied = Store(Picture(11, ClipOrigin.Captured, file: null, at: TestData.Now.AddHours(-1), source: shareXSource), bump: true);
        Store(Picture(12, ClipOrigin.ShareX, ShareXFile), bump: true);                            // has its file
        Store(Picture(13, ClipOrigin.Captured, file: null), bump: true);                            // not ShareX's
        Store(TestData.Text("BC-TEST copied in ShareX", source: shareXSource), bump: true);         // not a picture
        var snip = Store(Picture(14, ClipOrigin.WindowsScreenshot, file: null), bump: true);

        var shareX = store.FindPicturesWithoutFile(ClipFilter.ShareX);
        Assert.Equal([copied.Id, older.Id], shareX.Select(p => p.Id));
        Assert.Equal(copied.ContentHash, shareX[0].ContentHash);
        Assert.Equal(copied.CreatedUtc, shareX[0].CreatedUtc);
        Assert.Equal([snip.Id], store.FindPicturesWithoutFile(ClipFilter.Snipping).Select(p => p.Id));
        Assert.Throws<ArgumentException>(() => store.FindPicturesWithoutFile(ClipFilter.All));
    }

    /// <summary>The search records a file only where none is: never over one, and never for an entry that is gone.</summary>
    [Fact]
    public void SetSourceFile_FillsOnlyWhatIsMissing()
    {
        var bare = Store(Picture(20, ClipOrigin.ShareX, file: null), bump: true);
        var recorded = store.SetSourceFile(bare.Id, ShareXFile);
        Assert.Equal(ShareXFile, recorded!.File);
        Assert.Equal(bare.LastUsedUtc, recorded.LastUsedUtc); // knowing the file is not using the picture

        Assert.Null(store.SetSourceFile(bare.Id, NewerFile));
        Assert.Equal(ShareXFile, store.GetEntry(bare.Id)!.File);
        Assert.Null(store.SetSourceFile(987_654, ShareXFile));
    }

    /// <summary>
    /// Through the service: a capture's file is stored with it, and the search's files are recorded through the worker with
    /// one Updated event per entry that gained one.
    /// </summary>
    /// <returns>A task completing after the checks.</returns>
    [Fact]
    public async Task Service_StoresAndRecordsFiles()
    {
        var history = Start();
        var added = await history.AddAsync(Picture(30, ClipOrigin.ShareX, ShareXFile));
        Assert.Equal(ShareXFile, added!.File);

        var bare = (await history.AddAsync(Picture(31, ClipOrigin.ShareX, file: null)))!;
        var candidates = await history.GetPicturesWithoutFileAsync(ClipFilter.ShareX);
        Assert.Equal([bare.Id], candidates.Select(c => c.Id));

        lock (events)
        {
            events.Clear();
        }

        int recorded = await history.SetSourceFilesAsync([(bare.Id, NewerFile), (added.Id, NewerFile)]);
        Assert.Equal(1, recorded);
        lock (events)
        {
            var updated = Assert.Single(events);
            Assert.Equal(ClipChangeKind.Updated, updated.Kind);
            Assert.Equal(NewerFile, updated.Entry!.File);
        }

        Assert.Empty(await history.GetPicturesWithoutFileAsync(ClipFilter.ShareX));
    }

    /// <summary>bclip's JSON lists a picture's file with every detail, and leaves the member out when no file is known.</summary>
    [Fact]
    public void Cli_ListsTheFile()
    {
        var item = CliCommandProcessor.ToItem(Store(Picture(40, ClipOrigin.ShareX, ShareXFile), bump: true));
        using var json = JsonDocument.Parse(CliJson.Serialize(new CliResponse { Ok = true, Item = item }));
        var file = json.RootElement.GetProperty("item").GetProperty("file");
        Assert.Equal(ShareXFile.Path, file.GetProperty("path").GetString());
        Assert.Equal("chrome_Ab12Cd34Ef.png", file.GetProperty("name").GetString());
        Assert.Equal(".png", file.GetProperty("extension").GetString());
        Assert.Equal("PNG", file.GetProperty("format").GetString());
        Assert.Equal(123_456, file.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(ShareXFile.ModifiedUtc, file.GetProperty("modified").GetDateTimeOffset());
        Assert.Equal(ShareXFile.CreatedUtc, file.GetProperty("created").GetDateTimeOffset());

        var bare = CliCommandProcessor.ToItem(Store(Picture(41, ClipOrigin.Captured, file: null), bump: true));
        using var none = JsonDocument.Parse(CliJson.Serialize(new CliResponse { Ok = true, Item = bare }));
        Assert.False(none.RootElement.GetProperty("item").TryGetProperty("file", out _));
    }

    /// <summary>The setting: a month by default, clamped to 0…3650 when a hand edit goes past, kept through the file.</summary>
    [Fact]
    public void Setting_DefaultsClampsAndRoundTrips()
    {
        Assert.Equal(30, new AppSettings().TempImageDays);
        Assert.Equal(0, new AppSettings { TempImageDays = -5 }.Normalize().TempImageDays);
        Assert.Equal(TempImageFiles.MaxKeepDays, new AppSettings { TempImageDays = 99_999 }.Normalize().TempImageDays);

        var path = System.IO.Path.Combine(temp.Path, "settings.json");
        var settingsStore = new SettingsStore(path);
        settingsStore.Update(s => s with { TempImageDays = 3 });
        Assert.Equal(3, new SettingsStore(path).Load().TempImageDays);
    }

    /// <summary>The formats an image file's first bytes say, whatever its name says; anything else is unknown.</summary>
    /// <param name="hex">The first bytes, as hex.</param>
    /// <param name="expected">The format, or <see langword="null"/>.</param>
    [Theory]
    [InlineData("89504E470D0A1A0A0000000D49484452", "PNG")]
    [InlineData("FFD8FFE000104A464946", "JPEG")]
    [InlineData("474946383961", "GIF")]
    [InlineData("474946383761", "GIF")]
    [InlineData("424D361000000000000036000000", "BMP")]
    [InlineData("49492A00", "TIFF")]
    [InlineData("4D4D002A", "TIFF")]
    [InlineData("4949BC01", "JPEG XR")]
    [InlineData("52494646241000005745425056503820", "WebP")]
    [InlineData("0000001C6674797061766966000000", "AVIF")]
    [InlineData("000000186674797068656963000000", "HEIF")]
    [InlineData("0000002066747970697336000000", null)]
    [InlineData("00000100010010100000", "ICO")]
    [InlineData("424D20746578742C206E6F74", null)]
    [InlineData("89504E47", null)]
    [InlineData("", null)]
    [InlineData("48656C6C6F2C20776F726C6421", null)]
    public void ImageFileFormat_FromTheFirstBytes(string hex, string? expected) =>
        Assert.Equal(expected, ImageFileFormat.Detect(Convert.FromHexString(hex)));

    /// <summary>A file just read: its size, both dates and its format; a time Windows does not keep is left out.</summary>
    [Fact]
    public void FromDisk_DescribesTheFileAsRead()
    {
        var path = System.IO.Path.Combine(temp.Path, "BC-TEST shot.png");
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        File.WriteAllBytes(path, bytes);
        var written = new DateTime(2026, 11, 13, 9, 30, 15, DateTimeKind.Utc);
        File.SetCreationTimeUtc(path, written.AddMinutes(-5));
        File.SetLastWriteTimeUtc(path, written);

        var file = SourceFile.FromDisk(new FileInfo(path), bytes);
        Assert.Equal(path, file.Path);
        Assert.Equal(bytes.Length, file.SizeBytes);
        Assert.Equal(new DateTimeOffset(written.AddMinutes(-5)), file.CreatedUtc);
        Assert.Equal(new DateTimeOffset(written), file.ModifiedUtc);
        Assert.Equal("PNG", file.Format);

        File.SetLastWriteTimeUtc(path, new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(1));
        Assert.Null(SourceFile.FromDisk(new FileInfo(path), bytes).ModifiedUtc);
    }

    /// <summary>
    /// Where saved pictures go: the regular instance's folder, an isolated instance's own, a folder per picture named after
    /// its hash; a hash that could leave the folder is refused.
    /// </summary>
    [Fact]
    public void TempFolders_PerInstanceAndPerPicture()
    {
        Assert.Equal(@"C:\T\BetterClipboard\Images", TempImageFiles.RootIn(@"C:\T", null));
        Assert.Equal(@"C:\T\BetterClipboard\dir-0a1b2c3d4e5f\Images", TempImageFiles.RootIn(@"C:\T", "dir-0a1b2c3d4e5f"));
        Assert.Equal(@"C:\T\Images\0123456789ab", TempImageFiles.FolderFor(@"C:\T\Images", "0123456789abcdef0123"));
        Assert.Equal(@"C:\T\Images\abc", TempImageFiles.FolderFor(@"C:\T\Images", "abc"));
        Assert.Throws<ArgumentException>(() => TempImageFiles.FolderFor(@"C:\T\Images", @"..\x"));
        Assert.Throws<ArgumentException>(() => TempImageFiles.FolderFor(@"C:\T\Images", ".."));
        Assert.Throws<ArgumentException>(() => TempImageFiles.RootIn(string.Empty, null));
    }

    /// <summary>
    /// A saved picture's name: the original's (as a PNG), else the copy's local time; names that would be no names fall back.
    /// </summary>
    [Fact]
    public void TempNames_OriginalOrTime()
    {
        var copied = new DateTimeOffset(2026, 11, 13, 9, 5, 1, TimeSpan.Zero);
        var plus2 = TimeZoneInfo.CreateCustomTimeZone("BC-TEST+2", TimeSpan.FromHours(2), "BC-TEST+2", "BC-TEST+2");
        Assert.Equal("chrome_Ab12Cd34Ef.png", TempImageFiles.FileNameFor(ShareXFile, copied, plus2));
        Assert.Equal("copy of it.png", TempImageFiles.FileNameFor(NewerFile, copied, plus2));
        Assert.Equal("Image 2026-11-13 110501.png", TempImageFiles.FileNameFor(null, copied, plus2));
        Assert.Equal("Image 2026-11-13 090501.png", TempImageFiles.FileNameFor(new SourceFile(@"C:\x\...png", null, null, null, null), copied, TimeZoneInfo.Utc));
        Assert.Equal("Image 2026-11-13 090501.png", TempImageFiles.FileNameFor(new SourceFile(@"\\server\", null, null, null, null), copied, TimeZoneInfo.Utc));
    }

    /// <summary>
    /// Saving: writes the PNG (no partial file left), finds an identical earlier copy and touches it, and never writes over a
    /// copy edited since — the new one is numbered.
    /// </summary>
    [Fact]
    public void TempSave_ReusesNeverOverwrites()
    {
        var root = System.IO.Path.Combine(temp.Path, "Images");
        byte[] png = [1, 2, 3, 4, 5];
        var first = TempImageFiles.Save(root, "feedfacecafe0001", "shot.png", png, TestData.Now);
        Assert.Equal(System.IO.Path.Combine(root, "feedfacecafe", "shot.png"), first);
        Assert.Equal(png, File.ReadAllBytes(first));
        Assert.Empty(Directory.GetFiles(System.IO.Path.GetDirectoryName(first)!, "*.part"));

        File.SetLastWriteTimeUtc(first, TestData.Now.AddDays(-3).UtcDateTime);
        Assert.Equal(first, TempImageFiles.Save(root, "feedfacecafe0001", "shot.png", png, TestData.Now));
        Assert.Equal(TestData.Now.UtcDateTime, File.GetLastWriteTimeUtc(first));

        File.WriteAllBytes(first, [9, 9, 9]); // "edited in Paint"
        var second = TempImageFiles.Save(root, "feedfacecafe0001", "shot.png", png, TestData.Now);
        Assert.Equal(System.IO.Path.Combine(root, "feedfacecafe", "shot (2).png"), second);
        Assert.Equal([9, 9, 9], File.ReadAllBytes(first));
        Assert.Equal(second, TempImageFiles.Save(root, "feedfacecafe0001", "shot.png", png, TestData.Now));
        Assert.Throws<ArgumentException>(() => TempImageFiles.Save(root, "feedfacecafe0001", @"..\shot.png", png, TestData.Now));
    }

    /// <summary>The age rule: 0 deletes every one; otherwise only those unused for at least that many days.</summary>
    [Fact]
    public void TempExpiry_ByLastUse()
    {
        Assert.True(TempImageFiles.IsExpired(TestData.Now, TestData.Now, keepDays: 0));
        Assert.True(TempImageFiles.IsExpired(TestData.Now, TestData.Now, keepDays: -1));
        Assert.False(TempImageFiles.IsExpired(TestData.Now.AddDays(-30).AddSeconds(1), TestData.Now, keepDays: 30));
        Assert.True(TempImageFiles.IsExpired(TestData.Now.AddDays(-30), TestData.Now, keepDays: 30));
    }

    /// <summary>
    /// The start's clean-up: deletes old pictures and the folders that leaves empty, keeps young ones, deletes an hour-old
    /// leftover of an interrupted save whatever the setting; a missing folder is fine.
    /// </summary>
    [Fact]
    public void TempClean_DeletesOldKeepsYoung()
    {
        var root = System.IO.Path.Combine(temp.Path, "Images");
        var old = TempImageFiles.Save(root, "000000000001", "old.png", [1], TestData.Now);
        var young = TempImageFiles.Save(root, "000000000002", "young.png", [2], TestData.Now);
        var leftover = System.IO.Path.Combine(root, "000000000002", "half.png.part");
        File.WriteAllBytes(leftover, [3]);
        File.SetLastWriteTimeUtc(old, TestData.Now.AddDays(-31).UtcDateTime);
        File.SetLastWriteTimeUtc(young, TestData.Now.AddDays(-1).UtcDateTime);
        File.SetLastWriteTimeUtc(leftover, TestData.Now.AddHours(-2).UtcDateTime);

        Assert.Equal(1, TempImageFiles.Clean(root, TestData.Now, keepDays: 30));
        Assert.False(File.Exists(old));
        Assert.False(Directory.Exists(System.IO.Path.GetDirectoryName(old)));
        Assert.True(File.Exists(young));
        Assert.False(File.Exists(leftover));

        Assert.Equal(1, TempImageFiles.Clean(root, TestData.Now, keepDays: 0));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        Assert.Equal(0, TempImageFiles.Clean(System.IO.Path.Combine(temp.Path, "none"), TestData.Now, keepDays: 0));
    }

    /// <summary>The menu's line: the name, then format, size and last change; or "moved or deleted" when the file is gone.</summary>
    [Fact]
    public void Text_MenuLabel()
    {
        // The size follows the current culture's decimal mark (ByteSize_LikeExplorer checks the format itself).
        Assert.Equal($"chrome_Ab12Cd34Ef.png\nPNG · {ByteSizeText.Format(123_456)} · modified 5 min ago", SourceFileText.MenuLabel(ShareXFile, onDisk: true, TestData.Now.AddMinutes(5)));
        Assert.Equal("chrome_Ab12Cd34Ef.png\nmoved or deleted", SourceFileText.MenuLabel(ShareXFile, onDisk: false, TestData.Now));
        Assert.Equal("bare", SourceFileText.MenuLabel(new SourceFile(@"C:\x\bare", null, null, null, null), onDisk: true, TestData.Now));
    }

    /// <summary>
    /// The viewer's facts and dates: the bytes' format, else the extension; the size and pixel size when known; the creation
    /// time only when it is more than a minute from the last change.
    /// </summary>
    [Fact]
    public void Text_ViewerFactsAndDates()
    {
        Assert.Equal(["PNG", ByteSizeText.Format(123_456), "1920 × 1080"], SourceFileText.Facts(ShareXFile, 1920, 1080));
        Assert.Equal(["JPG"], SourceFileText.Facts(new SourceFile(@"C:\x\a.jpg", null, null, null, null), null, null));
        Assert.Empty(SourceFileText.Facts(new SourceFile(@"C:\x\a", null, null, null, null), 0, 10));

        var culture = CultureInfo.GetCultureInfo("en-GB");
        Assert.Equal(["modified 25/09/2026 12:00"], SourceFileText.Dates(ShareXFile, TimeZoneInfo.Utc, culture));
        var edited = ShareXFile with { CreatedUtc = TestData.Now.AddDays(-2) };
        Assert.Equal(["created 23/09/2026 12:00", "modified 25/09/2026 12:00"], SourceFileText.Dates(edited, TimeZoneInfo.Utc, culture));
        Assert.Empty(SourceFileText.Dates(new SourceFile(@"C:\x\a.png", null, null, null, null), TimeZoneInfo.Utc, culture));
    }

    /// <summary>Sizes like Explorer's: binary units, at most one or two decimals.</summary>
    /// <param name="bytes">The byte count.</param>
    /// <param name="expected">The text in the invariant culture's decimal mark.</param>
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(123_456L, "120.6 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    [InlineData(3L * 1024 * 1024 * 1024 + (768L * 1024 * 1024), "3.75 GB")]
    public void ByteSize_LikeExplorer(long bytes, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, ByteSizeText.Format(bytes));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>A picture capture (fake PNG bytes; Core tests hash bytes, not pixels).</summary>
    /// <param name="seed">Varies the picture.</param>
    /// <param name="origin">Where it came from.</param>
    /// <param name="file">The file it was read from, or <see langword="null"/>.</param>
    /// <param name="at">When (defaults to <see cref="TestData.Now"/>).</param>
    /// <param name="source">The app (defaults to Notepad).</param>
    /// <returns>The capture.</returns>
    private static ClipCapture Picture(byte seed, ClipOrigin origin, SourceFile? file, DateTimeOffset? at = null, SourceAppInfo? source = null) => new()
    {
        Formats = [new ClipFormatData(ClipFormatNames.Png, Enumerable.Repeat(seed, 64).ToArray())],
        CapturedAtUtc = at ?? TestData.Now,
        Origin = origin,
        Source = source ?? TestData.Notepad,
        File = file,
    };

    /// <summary>Stores a capture straight into the store.</summary>
    /// <param name="capture">The capture.</param>
    /// <param name="bump">Whether a duplicate is bumped (a new event) or left alone (an import).</param>
    /// <returns>The upsert's result, or <see langword="null"/> for a suppressed import.</returns>
    private UpsertResult? Upsert(ClipCapture capture, bool bump) =>
        store.Upsert(capture, ContentClassifier.Classify(capture)!, null, bumpIfExists: bump);

    /// <summary>Stores a capture and returns its entry (fails the test when it was not stored).</summary>
    /// <param name="capture">The capture.</param>
    /// <param name="bump">Whether a duplicate is bumped.</param>
    /// <returns>The stored entry.</returns>
    private ClipEntry Store(ClipCapture capture, bool bump) => Upsert(capture, bump)!.Entry;

    /// <summary>Starts a history service over the test store, recording its events.</summary>
    /// <returns>The started service.</returns>
    private ClipHistoryService Start()
    {
        service = new ClipHistoryService(store, null, () => new CaptureRules { Retention = RetentionPolicy.Unlimited });
        service.Changed += (_, change) =>
        {
            lock (events)
            {
                events.Add(change);
            }
        };
        service.Start();
        return service;
    }
}
