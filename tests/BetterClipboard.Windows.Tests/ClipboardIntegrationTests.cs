using System.Text;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Windows.Clipboard;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Real-clipboard round trip. <b>Opt-in</b> (set <c>BETTERCLIPBOARD_CLIPBOARD_TESTS=1</c>) because it
/// replaces the user's clipboard content and adds entries to Windows' own clipboard history.
/// </summary>
public sealed class ClipboardIntegrationTests
{
    /// <summary>
    /// One monitor writes text+HTML; a second monitor (a different listener window, i.e. "another app")
    /// captures exactly those bytes, while the writer ignores the echo of its own write.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task WriteThenCapture_RoundTripsAndSuppressesEcho()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("BETTERCLIPBOARD_CLIPBOARD_TESTS") == "1",
            "Touches the real clipboard; set BETTERCLIPBOARD_CLIPBOARD_TESTS=1 to run.");

        var options = new CaptureOptions();
        using var writer = new ClipboardMonitor(() => options, new SourceAppResolver());
        using var reader = new ClipboardMonitor(() => options, new SourceAppResolver());
        var echoes = 0;
        var captured = new TaskCompletionSource<ClipCapture>(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.Captured += (_, _) => Interlocked.Increment(ref echoes);
        reader.Captured += (_, capture) => captured.TrySetResult(capture);
        writer.Start();
        reader.Start();

        var marker = $"BC-TEST integration {Guid.NewGuid():N}";
        var html = Encoding.UTF8.GetBytes($"Version:0.9\r\nStartHTML:0\r\n<b>{marker}</b>");
        await writer.WriteAsync(
        [
            new ClipFormatData(ClipFormatNames.UnicodeText, UnicodeTextCodec.Encode(marker)),
            new ClipFormatData(ClipFormatNames.Html, html),
        ]);

        var capture = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(marker, UnicodeTextCodec.Decode(capture.Find(ClipFormatNames.UnicodeText)!.Data));
        Assert.Equal(html, capture.Find(ClipFormatNames.Html)!.Data.Take(html.Length));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(0, echoes);
    }
}
