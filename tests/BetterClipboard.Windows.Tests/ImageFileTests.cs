using BetterClipboard.Core.Model;
using BetterClipboard.Core.Storage;
using BetterClipboard.Windows.Imaging;
using BetterClipboard.Windows.Integrations;
using BetterClipboard.Windows.Shell;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// A picture's file on the Windows side (CLAUDE.md §2.49): whether a file is still on disk without ever blocking, the content
/// hash a file's entry would have, and the one-time search's matching of earlier screenshots to their files.
/// </summary>
public sealed class ImageFileTests : IDisposable
{
    private readonly string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>Deletes the temp folder.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>
    /// On a local drive a file is there or not (a folder is no file); a network path is never touched; anything that is no
    /// absolute path, or a drive letter Windows does not have, is not there.
    /// </summary>
    [Fact]
    public void FileOnDisk_ChecksLocalDrivesOnly()
    {
        var path = Path.Combine(folder, "BC-TEST shot.png");
        File.WriteAllBytes(path, [1]);
        Assert.Equal(FilePresence.OnDisk, FileOnDisk.Check(path));
        File.Delete(path);
        Assert.Equal(FilePresence.Missing, FileOnDisk.Check(path));
        Assert.Equal(FilePresence.Missing, FileOnDisk.Check(folder));

        Assert.Equal(FilePresence.Missing, FileOnDisk.Check(null));
        Assert.Equal(FilePresence.Missing, FileOnDisk.Check("   "));
        Assert.Equal(FilePresence.Missing, FileOnDisk.Check(@"relative\shot.png"));
        Assert.Equal(FilePresence.Unknown, FileOnDisk.Check(@"\\bc-test-no-such-server\share\shot.png"));

        // A letter no drive uses (the tests never run with every letter mapped).
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        char free = Enumerable.Range('D', 23).Select(c => (char)c).First(c => !used.Contains(c));
        Assert.Equal(FilePresence.Missing, FileOnDisk.Check($@"{free}:\BC-TEST\shot.png"));
    }

    /// <summary>
    /// The hash a file's entry would have equals the analyzer's pixel hash — for the PNG and for a BMP of the same pixels —
    /// and differs for other pixels.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task ContentHash_IsTheAnalyzersPixelHash()
    {
        var png = await ShareXScreenshotWatcherTests.PngAsync(16, 10, 7);
        var analyzed = await ImageCodec.AnalyzeAsync(png, 8, 8, TestContext.Current.CancellationToken);
        Assert.NotNull(analyzed.PixelHash);
        Assert.Equal(analyzed.PixelHash, await ImageCodec.ContentHashAsync(png, TestContext.Current.CancellationToken));

        var bmp = DibImage.ToBmpFile((await ImageCodec.ToClipboardFormatsAsync(png, TestContext.Current.CancellationToken))[1].Data);
        Assert.Equal(analyzed.PixelHash, await ImageCodec.ContentHashAsync(bmp, TestContext.Current.CancellationToken));

        var other = await ShareXScreenshotWatcherTests.PngAsync(16, 10, 8);
        Assert.NotEqual(analyzed.PixelHash, await ImageCodec.ContentHashAsync(other, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Matching: each picture gets the file saved within two minutes of it with its pixels — the closest such file, never one
    /// already given to another picture, never one with other pixels, never one saved further away; a file that is no picture
    /// is simply no match.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Match_ByTimeThenPixels()
    {
        var at = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var pngA = await ShareXScreenshotWatcherTests.PngAsync(10, 10, 1);
        var pngB = await ShareXScreenshotWatcherTests.PngAsync(10, 10, 2);
        var hashA = (await ImageCodec.AnalyzeAsync(pngA, 8, 8, TestContext.Current.CancellationToken)).PixelHash!;
        var hashB = (await ImageCodec.AnalyzeAsync(pngB, 8, 8, TestContext.Current.CancellationToken)).PixelHash!;

        var a = Write("a.png", pngA, at);                           // A's own save
        var b = Write("b.png", pngB, at.AddSeconds(30));            // another screenshot in the same minute
        var aLater = Write("a again.png", pngA, at.AddMinutes(10)); // the same picture saved again later
        var aFar = Write("a far.png", pngA, at.AddMinutes(30));     // the same pixels, too far from every entry
        Write("not a picture.png", "BC-TEST not an image"u8.ToArray(), at.AddSeconds(5));
        var files = new[] { a, b, aLater, aFar }.Select(p => new FileInfo(p)).Append(new FileInfo(Path.Combine(folder, "not a picture.png"))).ToList();

        PictureWithoutFile Entry(long id, string hash, DateTime created) => new(id, hash, new DateTimeOffset(created, TimeSpan.Zero));
        var found = await ScreenshotFileFinder.MatchAsync(
            [
                Entry(1, hashA, at.AddSeconds(4)),          // copied 4 s after its save
                Entry(2, hashB, at),                        // B, saved 30 s after this entry
                Entry(3, hashA, at.AddMinutes(10).AddSeconds(1)),
                Entry(4, hashA, at.AddSeconds(1)),          // A's twin entry: a.png is taken, a again.png is too far
                Entry(5, "BC-TEST no such picture", at),
            ],
            files,
            TestContext.Current.CancellationToken);

        Assert.Equal([(1L, a), (2L, b), (3L, aLater)], found.Select(f => (f.Id, f.File.Path)));
        Assert.All(found, f => Assert.Equal("PNG", f.File.Format));
        Assert.Equal(pngB.Length, found[1].File.SizeBytes);
        Assert.Equal(new DateTimeOffset(at.AddSeconds(30), TimeSpan.Zero), found[1].File.ModifiedUtc);
    }

    /// <summary>A file gone between the listing and the read is no match, and stops nothing.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Match_AFileGoneMeanwhile_IsNoMatch()
    {
        var at = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var png = await ShareXScreenshotWatcherTests.PngAsync(10, 10, 3);
        var hash = (await ImageCodec.AnalyzeAsync(png, 8, 8, TestContext.Current.CancellationToken)).PixelHash!;
        var path = Write("gone.png", png, at);
        var listed = new FileInfo(path);
        File.Delete(path);

        var found = await ScreenshotFileFinder.MatchAsync([new PictureWithoutFile(1, hash, new DateTimeOffset(at, TimeSpan.Zero))], [listed], TestContext.Current.CancellationToken);
        Assert.Empty(found);
    }

    /// <summary>Writes a file into the temp folder with a given write time.</summary>
    /// <param name="name">File name.</param>
    /// <param name="bytes">Content.</param>
    /// <param name="writtenUtc">Its last write time.</param>
    /// <returns>Its full path.</returns>
    private string Write(string name, byte[] bytes, DateTime writtenUtc)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }
}
