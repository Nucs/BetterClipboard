#:property PublishAot=false
#:property TargetFramework=net10.0-windows10.0.19041.0
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────
// Removes ONLY the test items an E2E run put into Windows' clipboard history (Win+V): texts containing
// "BC-TEST", the test link/color, the CLAUDE.md file-drop and the 256×256 BetterClipboard icon image —
// and only items newer than the given start time. Prints counts only, never item content.
// Run: dotnet run tools/e2e/cleanup_history.cs -- 2026-09-25T06:20:00Z
// ─────────────────────────────────────────────────────────────────────────────────────────────────────
if (args.Length == 0 || !DateTimeOffset.TryParse(args[0], out var since))
{
    Console.WriteLine("usage: dotnet run tools/e2e/cleanup_history.cs -- <test-start-time-ISO-8601>");
    return;
}
var result = await Clipboard.GetHistoryItemsAsync();
Console.WriteLine($"status={result.Status} items={result.Items.Count}");
int deleted = 0;
foreach (var item in result.Items)
{
    if (item.Timestamp < since)
    {
        continue; // anything older than this test session is certainly the user's own
    }

    var content = item.Content;
    bool mine = false;
    if (content.Contains(StandardDataFormats.Text))
    {
        var text = await content.GetTextAsync();
        mine = text.Contains("BC-TEST", StringComparison.Ordinal) || text == "https://bc-test.example.com/link" || text == "#1E90FF";
    }
    else if (content.Contains(StandardDataFormats.StorageItems))
    {
        var files = await content.GetStorageItemsAsync();
        // The e2e recipe copies the repo's own CLAUDE.md as its file-drop sample; the tool runs from the
        // repo root (see the usage line), so the relative path resolves to that same file on any checkout.
        var sample = Path.GetFullPath("CLAUDE.md");
        mine = files.Any(f => f.Path.Equals(sample, StringComparison.OrdinalIgnoreCase));
    }
    else if (content.Contains(StandardDataFormats.Bitmap))
    {
        var reference = await content.GetBitmapAsync();
        using var stream = await reference.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        mine = decoder.PixelWidth == 256 && decoder.PixelHeight == 256; // the BetterClipboard icon used by the tests
    }

    if (mine && Clipboard.DeleteItemFromHistory(item))
    {
        deleted++;
    }
}

Console.WriteLine($"deleted test items: {deleted}");
