#:property PublishAot=false
#:property TargetFramework=net10.0-windows10.0.19041.0
using Windows.ApplicationModel.DataTransfer;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────
// Probe: can a background (non-foreground) unpackaged desktop process read Win+V history via WinRT?
// PRIVACY: prints status, counts, timestamps and format names only — never clipboard content.
// Run: dotnet run tools/probes/probe_history.cs
// Verified 2026-09-25: Status=Success from a background console (the "foreground only" rule applies to
// UWP/AppContainer callers). Pinned items appear too; their timestamps equal Pinned\metadata.json.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────
Console.WriteLine($"IsHistoryEnabled={Clipboard.IsHistoryEnabled()} IsRoamingEnabled={Clipboard.IsRoamingEnabled()}");
var result = await Clipboard.GetHistoryItemsAsync();
Console.WriteLine($"Status={result.Status} Count={result.Items.Count}");
foreach (var item in result.Items.Take(30))
{
    Console.WriteLine($"  {item.Timestamp:u} formats=[{string.Join(",", item.Content.AvailableFormats.Take(8))}]");
}
