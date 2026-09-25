using System.Text;
using BetterClipboard.Core.Cli;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests every <c>bclip</c> command against a real store and history service, with a fake clipboard: the
/// panel's semantics (newest first, replay rules, recording of <c>put</c>), targeting, the content
/// representations, the safety limits (delete needs an id, slow regexes), and <c>wait</c>.
/// </summary>
public sealed class CliCommandProcessorTests : IAsyncLifetime
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private readonly FakeClipboard clipboard = new();
    private AppSettings settings = new();
    private ClipHistoryService history = null!;
    private CliCommandProcessor processor = null!;

    /// <summary>Starts a service over a temp store and a processor over it.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask InitializeAsync()
    {
        var store = new ClipStore(temp.DatabasePath);
        store.Initialize();
        history = new ClipHistoryService(store, null, () => new CaptureRules { Retention = RetentionPolicy.Unlimited, IsPaused = settings.IsCapturePaused });
        history.Start();
        processor = new CliCommandProcessor(history, clipboard, new FakeExporter(), () => settings, () => new CliCaptureStats(5, 4, 4, 1, 0, 0, 0), "9.9.9");
        return ValueTask.CompletedTask;
    }

    /// <summary>Stops the service and deletes the store.</summary>
    /// <returns>A task.</returns>
    public async ValueTask DisposeAsync()
    {
        await history.DisposeAsync();
        temp.Dispose();
    }

    /// <summary>list is newest first (pins not hoisted), honors filter, limit and --since, and --full adds text.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task List_NewestFirst_Filters_Since_Full()
    {
        var old = await AddText("old pinned", TestData.Now.AddHours(-3), pin: true);
        await AddText("middle", TestData.Now.AddHours(-2));
        var newest = await AddText("newest line\nsecond line", TestData.Now);

        var all = await Run(new CliRequest { Command = CliCommands.List });
        Assert.Equal(["newest line\nsecond line", "middle", "old pinned"], all.Items!.Select(i => i.Preview));
        Assert.Equal(newest.Id, all.Items![0].Id);
        Assert.Null(all.Items![0].Text);

        Assert.Equal(old.Id, (await Run(new CliRequest { Command = CliCommands.List, Filter = "pinned" })).Items!.Single().Id);
        Assert.Single((await Run(new CliRequest { Command = CliCommands.List, Limit = 1 })).Items!);
        Assert.Equal(2, (await Run(new CliRequest { Command = CliCommands.List, Since = TestData.Now.AddHours(-2) })).Items!.Count);
        Assert.Equal("newest line\nsecond line", (await Run(new CliRequest { Command = CliCommands.List, Limit = 1, Full = true })).Items!.Single().Text);
        Assert.Equal(CliErrorCodes.BadRequest, (await Run(new CliRequest { Command = CliCommands.List, Filter = "videos" })).ErrorCode);
    }

    /// <summary>The sharex filter lists ShareX screenshots and ShareX copies, labelled by origin.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task List_ShareXFilter_AndOrigin()
    {
        await AddText("unrelated");
        var shot = (await history.AddAsync(TestData.ShareXShot(3, TestData.Now.AddMinutes(1))))!;
        var copy = (await history.AddAsync(TestData.Text("copied in ShareX", TestData.Now.AddMinutes(2), source: TestData.ShareX)))!;

        var items = (await Run(new CliRequest { Command = CliCommands.List, Filter = "ShareX" })).Items!;
        Assert.Equal([copy.Id, shot.Id], items.Select(i => i.Id));
        Assert.Equal(["copied", "sharex"], items.Select(i => i.Origin));
        Assert.Equal("ShareX", items[1].Source);
    }

    /// <summary>search uses the panel's substring search; no match is a not-found error.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Search_SubstringOrNotFound()
    {
        await AddText("The Windows Clipboard forgets");
        await AddText("שלום עולם", TestData.Now.AddMinutes(1));
        Assert.Equal("The Windows Clipboard forgets", (await Run(new CliRequest { Command = CliCommands.Search, Query = "board" })).Items!.Single().Preview);
        Assert.Equal("שלום עולם", (await Run(new CliRequest { Command = CliCommands.Search, Query = "עולם" })).Items!.Single().Preview);
        Assert.Equal(CliErrorCodes.NotFound, (await Run(new CliRequest { Command = CliCommands.Search, Query = "absent" })).ErrorCode);
    }

    /// <summary>grep reports matching lines with numbers, honors -i, and rejects invalid or catastrophic patterns.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Grep_LinesCaseAndSafety()
    {
        var log = await AddText("start\nERROR: disk full\nok\nerror: retry");
        await AddText("unrelated", TestData.Now.AddMinutes(1));

        var sensitive = await Run(new CliRequest { Command = CliCommands.Grep, Query = "ERROR" });
        var item = sensitive.Items!.Single();
        Assert.Equal(log.Id, item.Id);
        Assert.Equal([2], item.Matches!.Select(m => m.Line));
        Assert.Equal("ERROR: disk full", item.Matches![0].Text);
        Assert.Equal(2, sensitive.Scanned);

        var insensitive = await Run(new CliRequest { Command = CliCommands.Grep, Query = "^error", IgnoreCase = true });
        Assert.Equal([2, 4], insensitive.Items!.Single().Matches!.Select(m => m.Line));

        Assert.Equal(CliErrorCodes.NotFound, (await Run(new CliRequest { Command = CliCommands.Grep, Query = "zzz" })).ErrorCode);
        Assert.Equal(CliErrorCodes.BadRequest, (await Run(new CliRequest { Command = CliCommands.Grep, Query = "(unclosed" })).ErrorCode);

        await AddText(new string('a', 40) + "!", TestData.Now.AddMinutes(2));
        var slow = await Run(new CliRequest { Command = CliCommands.Grep, Query = "^(a+)+$" });
        Assert.Equal(CliErrorCodes.BadRequest, slow.ErrorCode);
        Assert.Contains("too slow", slow.Error, StringComparison.Ordinal);
    }

    /// <summary>get defaults to the latest item, serves text exactly, and targets by id or --recent.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Get_TextAndTargeting()
    {
        var first = await AddText("  exact\ttext\r\n", TestData.Now.AddMinutes(-1));
        await AddText("latest", TestData.Now);

        var latest = await Run(new CliRequest { Command = CliCommands.Get });
        Assert.Equal("latest", latest.Content!.Text);
        Assert.Equal("text/plain", latest.Content.MediaType);
        Assert.Equal("  exact\ttext\r\n", (await Run(new CliRequest { Command = CliCommands.Get, Id = first.Id })).Content!.Text);
        Assert.Equal(first.Id, (await Run(new CliRequest { Command = CliCommands.Get, Recent = 2 })).Item!.Id);
        Assert.Equal(CliErrorCodes.NotFound, (await Run(new CliRequest { Command = CliCommands.Get, Id = 999 })).ErrorCode);
        Assert.Equal(CliErrorCodes.NotFound, (await Run(new CliRequest { Command = CliCommands.Get, Recent = 5 })).ErrorCode);
        Assert.Equal(CliErrorCodes.BadRequest, (await Run(new CliRequest { Command = CliCommands.Get, Format = "mp3" })).ErrorCode);
    }

    /// <summary>get serves HTML fragments, file lists, PNG (stored or exported) and the format inventory.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Get_Representations()
    {
        var rich = await history.AddAsync(TestData.Rich("bold", "Version:0.9\r\nStartHTML:-1\r\nEndHTML:-1\r\nStartFragment:-1\r\nEndFragment:-1\r\n<b>bold</b>"));
        Assert.Equal("<b>bold</b>", (await Run(new CliRequest { Command = CliCommands.Get, Id = rich!.Id, Format = "html" })).Content!.Text);
        Assert.Equal(CliErrorCodes.Unsupported, (await Run(new CliRequest { Command = CliCommands.Get, Id = rich.Id, Format = "rtf" })).ErrorCode);

        var files = await history.AddAsync(TestData.Files(@"C:\a.txt", @"C:\b c.txt"));
        var fileContent = (await Run(new CliRequest { Command = CliCommands.Get, Id = files!.Id })).Content!;
        Assert.Equal("files", fileContent.Format);
        Assert.Equal([@"C:\a.txt", @"C:\b c.txt"], fileContent.Files);
        Assert.Equal("C:\\a.txt\nC:\\b c.txt", fileContent.Text);

        var image = await history.AddAsync(new ClipCapture { Formats = [new ClipFormatData(ClipFormatNames.Dib, [1, 2, 3])], CapturedAtUtc = TestData.Now });
        var png = (await Run(new CliRequest { Command = CliCommands.Get, Id = image!.Id })).Content!;
        Assert.Equal("image/png", png.MediaType);
        Assert.Equal(FakeExporter.Png, Convert.FromBase64String(png.DataBase64!));

        var inventory = (await Run(new CliRequest { Command = CliCommands.Get, Id = image.Id, Format = "formats" })).Content!;
        Assert.Equal(new CliFormatInfo(ClipFormatNames.Dib, 3), inventory.StoredFormats!.Single());
    }

    /// <summary>copy replays like the panel (plain text strips formats) and moves the item to the top.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Copy_ReplaysAndMarksUsed()
    {
        var rich = await history.AddAsync(TestData.Rich("bold", "<b>bold</b>"));
        await AddText("newer", TestData.Now.AddMinutes(5));

        var copied = await Run(new CliRequest { Command = CliCommands.Copy, Id = rich!.Id });
        Assert.True(copied.Ok);
        Assert.Equal([ClipFormatNames.UnicodeText, ClipFormatNames.Html], clipboard.Last!.Select(f => f.Name));
        Assert.Contains($"item {rich.Id}", copied.Message, StringComparison.Ordinal);

        await Run(new CliRequest { Command = CliCommands.Copy, Id = rich.Id, PlainText = true });
        Assert.Equal([ClipFormatNames.UnicodeText], clipboard.Last!.Select(f => f.Name));
        Assert.True(copied.Item!.UseCount >= 2);
    }

    /// <summary>put writes the clipboard and records the text as a live copy from bclip; paused capture only writes.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Put_WritesAndRecords()
    {
        var put = await Run(new CliRequest { Command = CliCommands.Put, Text = "from an agent ✓" });
        Assert.True(put.Ok);
        Assert.Equal("from an agent ✓", UnicodeTextCodec.Decode(clipboard.Last!.Single().Data));
        Assert.Equal("bclip", put.Item!.Source);
        Assert.Equal("copied", put.Item.Origin);
        Assert.Equal("from an agent ✓", (await Run(new CliRequest { Command = CliCommands.Get })).Content!.Text);

        settings = settings with { IsCapturePaused = true };
        var paused = await Run(new CliRequest { Command = CliCommands.Put, Text = "not recorded" });
        Assert.True(paused.Ok);
        Assert.Null(paused.Item);
        Assert.Equal("not recorded", UnicodeTextCodec.Decode(clipboard.Last!.Single().Data));

        Assert.Equal(CliErrorCodes.BadRequest, (await Run(new CliRequest { Command = CliCommands.Put, Text = "" })).ErrorCode);
    }

    /// <summary>pin/unpin default to the latest item; delete refuses without an explicit target.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Pin_Unpin_Delete()
    {
        var item = await AddText("keep me");
        Assert.True((await Run(new CliRequest { Command = CliCommands.Pin })).Item!.Pinned);
        Assert.False((await Run(new CliRequest { Command = CliCommands.Unpin, Id = item.Id })).Item!.Pinned);

        Assert.Equal(CliErrorCodes.BadRequest, (await Run(new CliRequest { Command = CliCommands.Delete })).ErrorCode);
        Assert.True((await Run(new CliRequest { Command = CliCommands.Delete, Id = item.Id })).Ok);
        Assert.Equal(CliErrorCodes.NotFound, (await Run(new CliRequest { Command = CliCommands.Get, Id = item.Id })).ErrorCode);
    }

    /// <summary>wait completes with the next copy (full text included) and times out otherwise.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Wait_NextCopyOrTimeout()
    {
        await AddText("before waiting", TestData.Now.AddHours(-1));
        var waiting = Run(new CliRequest { Command = CliCommands.Wait, TimeoutSeconds = 10 });
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);

        await history.AddAsync(TestData.Text("copied while waiting\nline 2"));
        var arrived = await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(arrived.Ok);
        Assert.Equal("copied while waiting\nline 2", arrived.Item!.Text);

        var timeout = await Run(new CliRequest { Command = CliCommands.Wait, TimeoutSeconds = 1 });
        Assert.Equal(CliErrorCodes.Timeout, timeout.ErrorCode);
    }

    /// <summary>status reports version, counts and listener accounting; unknown commands and protocols are refused.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Status_And_Refusals()
    {
        await AddText("one");
        var status = (await Run(new CliRequest { Command = CliCommands.Status })).Status!;
        Assert.Equal("9.9.9", status.Version);
        Assert.Equal(1, status.Items);
        Assert.Equal(1, status.Capture!.Superseded);

        Assert.Equal(CliErrorCodes.BadRequest, (await Run(new CliRequest { Command = "format-disk" })).ErrorCode);
        Assert.Equal(CliErrorCodes.BadRequest, (await Run(new CliRequest { Command = CliCommands.List, ProtocolVersion = 99 })).ErrorCode);
    }

    /// <summary>Runs a request with the test's cancellation token.</summary>
    /// <param name="request">Request.</param>
    /// <returns>The response.</returns>
    private Task<CliResponse> Run(CliRequest request) => processor.ExecuteAsync(request, TestContext.Current.CancellationToken);

    /// <summary>Adds a text capture.</summary>
    /// <param name="text">Text.</param>
    /// <param name="at">Capture time.</param>
    /// <param name="pin">Pin it.</param>
    /// <returns>The stored entry.</returns>
    private async Task<ClipEntry> AddText(string text, DateTimeOffset? at = null, bool pin = false) =>
        (await history.AddAsync(TestData.Text(text, at, pin: pin)))!;

    /// <summary>Records clipboard writes instead of touching the real clipboard.</summary>
    private sealed class FakeClipboard : IClipboardWriter
    {
        /// <summary>The formats of the last write.</summary>
        public IReadOnlyList<ClipFormatData>? Last { get; private set; }

        /// <inheritdoc />
        public Task WriteAsync(IReadOnlyList<ClipFormatData> formats)
        {
            Last = formats;
            return Task.CompletedTask;
        }
    }

    /// <summary>Returns a fixed "PNG" for any bitmap, so DIB-only items are exportable.</summary>
    private sealed class FakeExporter : IImageExporter
    {
        /// <summary>The bytes returned.</summary>
        public static readonly byte[] Png = Encoding.ASCII.GetBytes("fake-png");

        /// <inheritdoc />
        public Task<byte[]?> ToPngAsync(IReadOnlyList<ClipFormatData> formats, CancellationToken cancellationToken) =>
            Task.FromResult<byte[]?>(formats.Any(f => f.Name is ClipFormatNames.Dib or ClipFormatNames.DibV5) ? Png : null);
    }
}
