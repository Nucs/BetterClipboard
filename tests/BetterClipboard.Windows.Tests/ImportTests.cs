using System.Security.Cryptography;
using System.Text;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Windows.Import;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests for DPAPI-NG and the Windows pinned-store importer, using a synthetic store laid out exactly like
/// the real one (CLAUDE.md §1.4) and encrypted with the same <c>LOCAL=user</c> descriptor.
/// </summary>
public sealed class ImportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", Guid.NewGuid().ToString("N"), "Pinned");

    /// <summary>Deletes the synthetic store.</summary>
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

    /// <summary>Protect/unprotect round-trips; blobs are CMS (ASN.1 SEQUENCE) like Windows' pinned files.</summary>
    [Fact]
    public void DpapiNg_RoundTrips()
    {
        var secret = Encoding.UTF8.GetBytes("pinned secret");
        var blob = DpapiNg.Protect(secret);
        Assert.Equal(0x30, blob[0]);
        Assert.Equal(secret, DpapiNg.Unprotect(blob));
    }

    /// <summary>Tampered blobs fail loudly (GCM authentication) instead of yielding garbage.</summary>
    [Fact]
    public void DpapiNg_DetectsTampering()
    {
        var blob = DpapiNg.Protect([1, 2, 3, 4, 5, 6, 7, 8]);
        blob[^1] ^= 0xFF;
        Assert.Throws<CryptographicException>(() => DpapiNg.Unprotect(blob));
    }

    /// <summary>A missing store yields nothing (fresh machines, history never used).</summary>
    [Fact]
    public void PinnedStore_MissingIsEmpty()
    {
        var store = new WindowsPinnedStore(root);
        Assert.Empty(store.ReadItems());
        Assert.Empty(store.ReadPinnedTimestamps());
    }

    /// <summary>Items are found through metadata.json, decrypted, and timestamps are parsed as UTC.</summary>
    [Fact]
    public void PinnedStore_ReadsAndDecrypts()
    {
        WriteItem("{11111111-1111-1111-1111-111111111111}", "2026-03-07T10:17:25Z", ("Text", "hello pinned world"), ("HTML Format", "Version:0.9"));
        var store = new WindowsPinnedStore(root);
        var item = Assert.Single(store.ReadItems());
        Assert.Equal(new DateTimeOffset(2026, 3, 7, 10, 17, 25, TimeSpan.Zero), item.Timestamp);
        Assert.Equal("hello pinned world", Encoding.Unicode.GetString(item.Formats.Single(f => f.Name == "Text").Data));
        Assert.Contains(new DateTimeOffset(2026, 3, 7, 10, 17, 25, TimeSpan.Zero), store.ReadPinnedTimestamps());
    }

    /// <summary>Undecryptable formats are skipped, the rest of the item survives.</summary>
    [Fact]
    public void PinnedStore_SkipsCorruptFormats()
    {
        var folder = WriteItem("{22222222-2222-2222-2222-222222222222}", "2026-01-01T00:00:00Z", ("Text", "keep me"), ("Locale", "x"));
        File.WriteAllBytes(Path.Combine(folder, Convert.ToBase64String(Encoding.UTF8.GetBytes("Locale"))), [0x30, 0x01, 0x02]);
        var item = Assert.Single(new WindowsPinnedStore(root).ReadItems());
        Assert.Equal("Text", Assert.Single(item.Formats).Name);
    }

    /// <summary>The importer turns pins into pinned captures with proper clipboard formats (no WinRT needed).</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Importer_ConvertsPins()
    {
        WriteItem("{33333333-3333-3333-3333-333333333333}", "2026-02-02T02:02:02Z", ("Text", "pinned via Win+V"), ("Rich Text Format", @"{\rtf1 hi}"));
        var importer = new WindowsHistoryImporter(new WindowsPinnedStore(root));
        var result = await importer.ReadAllAsync(includeHistory: false, TestContext.Current.CancellationToken);
        var capture = Assert.Single(result.Captures);
        Assert.True(capture.Pin);
        Assert.Equal(ClipOrigin.WindowsPinned, capture.Origin);
        Assert.Equal("pinned via Win+V", UnicodeTextCodec.Decode(capture.Find(ClipFormatNames.UnicodeText)!.Data));
        Assert.Equal(@"{\rtf1 hi}", Encoding.Latin1.GetString(capture.Find(ClipFormatNames.Rtf)!.Data));
        Assert.Equal(1, result.PinnedOnDisk);
    }

    /// <summary>
    /// Writes one pinned item exactly like Windows does: UTF-16LE+BOM metadata, Base64 file names,
    /// DPAPI-NG (LOCAL=user) payloads, UTF-16LE strings without terminator.
    /// </summary>
    /// <param name="id">Item GUID folder name.</param>
    /// <param name="timestamp">ISO timestamp.</param>
    /// <param name="formats">(format name, string value) pairs.</param>
    /// <returns>The item folder.</returns>
    private string WriteItem(string id, string timestamp, params (string Name, string Value)[] formats)
    {
        var storeFolder = Path.Combine(root, "{B0A2FD76-EA7E-4551-8D71-67E42B2E9D08}");
        var itemFolder = Path.Combine(storeFolder, id);
        Directory.CreateDirectory(itemFolder);
        File.WriteAllText(Path.Combine(storeFolder, "metadata.json"),
            $"{{\"items\":{{\"{id}\":{{\"timestamp\":\"{timestamp}\",\"source\":\"Local\"}}}}}}", Encoding.Unicode);

        var formatMetadata = string.Join(",", formats.Select(f => $"\"{f.Name}\":{{\"dataType\":\"String\",\"collectionType\":\"None\",\"isEncrypted\":true}}"));
        File.WriteAllText(Path.Combine(itemFolder, "metadata.json"),
            $"{{\"formatMetadata\":{{{formatMetadata}}},\"sourceAppId\":\"\",\"property\":{{}}}}", Encoding.Unicode);
        foreach (var (name, value) in formats)
        {
            var file = Path.Combine(itemFolder, Convert.ToBase64String(Encoding.UTF8.GetBytes(name)));
            File.WriteAllBytes(file, DpapiNg.Protect(Encoding.Unicode.GetBytes(value)));
        }

        return itemFolder;
    }
}
