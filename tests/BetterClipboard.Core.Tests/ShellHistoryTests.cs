using System.Text;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Shells;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for reading PowerShell's history file the way PSReadLine reads it (CLAUDE.md §2.16: verified against 2.0.0
/// and 2.3.6): one command per record, multi-line commands joined at backtick + line break, nothing invented.
/// </summary>
public sealed class PsReadLineHistoryTests
{
    /// <summary>Records are split at CRLF, LF and CR alike, and come back oldest first.</summary>
    [Fact]
    public void ParseRecords_SplitsAtEveryLineBreakKind()
    {
        Assert.Equal(["BC-TEST a", "BC-TEST b", "BC-TEST c", "BC-TEST d"],
            PsReadLineHistory.ParseRecords("BC-TEST a\r\nBC-TEST b\nBC-TEST c\rBC-TEST d\r\n"));
    }

    /// <summary>
    /// A multi-line command is stored as <c>line`&lt;LF&gt;line`&lt;LF&gt;line&lt;CRLF&gt;</c> (the bytes PSReadLine writes): it
    /// reads back as one record with <c>\n</c> between its lines, the escaping backticks gone.
    /// </summary>
    [Fact]
    public void ParseRecords_JoinsBacktickContinuations()
    {
        var records = PsReadLineHistory.ParseRecords("BC-TEST before\r\nif ($true) {`\n'BC-TEST-3'`\n}\r\nBC-TEST after\r\n");
        Assert.Equal(["BC-TEST before", "if ($true) {\n'BC-TEST-3'\n}", "BC-TEST after"], records);
    }

    /// <summary>A record whose last line still ends with a backtick (being written, or torn) is dropped, like PSReadLine drops it.</summary>
    [Fact]
    public void ParseRecords_DropsAnUnfinishedContinuation()
    {
        Assert.Equal(["BC-TEST done"], PsReadLineHistory.ParseRecords("BC-TEST done\r\nBC-TEST half`\n"));
        Assert.Equal(["BC-TEST done"], PsReadLineHistory.ParseRecords("BC-TEST done\r\nBC-TEST half`"));
    }

    /// <summary>Blank records (PSReadLine never adds them) and a missing final line break are handled.</summary>
    [Fact]
    public void ParseRecords_SkipsBlankRecordsAndReadsAnUnterminatedLastLine()
    {
        Assert.Equal(["BC-TEST a", "BC-TEST b"], PsReadLineHistory.ParseRecords("\r\nBC-TEST a\r\n   \r\n\r\nBC-TEST b"));
        Assert.Empty(PsReadLineHistory.ParseRecords(string.Empty));
        Assert.Throws<ArgumentNullException>(() => PsReadLineHistory.ParseRecords(null!));
    }

    /// <summary>Decoding skips a BOM (PSReadLine writes none, an editor might) and survives invalid UTF-8.</summary>
    [Fact]
    public void Decode_SkipsTheBomAndReplacesInvalidBytes()
    {
        Assert.Equal("BC-TEST ü", PsReadLineHistory.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("BC-TEST ü")]));
        Assert.Equal("BC-TEST �", PsReadLineHistory.Decode([.. Encoding.UTF8.GetBytes("BC-TEST "), 0xC3]));
    }

    /// <summary>Distinct commands come newest first, each at its newest place, with how often it was typed.</summary>
    [Fact]
    public void NewestFirst_ListsEachCommandOnceAtItsNewestPlace()
    {
        var result = PsReadLineHistory.NewestFirst(["BC-TEST a", "BC-TEST b", "BC-TEST a", "BC-TEST c", "BC-TEST b"]);
        Assert.Equal([("BC-TEST b", 2), ("BC-TEST c", 1), ("BC-TEST a", 2)], result);
    }

    /// <summary>The host in the file name becomes the caption's source (the console host is shared by 5.1 and 7).</summary>
    [Fact]
    public void SourceName_NamesTheHost()
    {
        Assert.Equal("PowerShell", PsReadLineHistory.SourceName("ConsoleHost_history.txt"));
        Assert.Equal("PowerShell in VS Code", PsReadLineHistory.SourceName("Visual Studio Code Host_history.txt"));
        Assert.Equal("PowerShell (Other Host)", PsReadLineHistory.SourceName("Other Host_history.txt"));
    }
}

/// <summary>Tests for <see cref="ShellCommand"/>: the text that goes onto the clipboard, and the keys derived from it.</summary>
public sealed class ShellCommandTests
{
    /// <summary>
    /// Line breaks become CRLF (a lone LF or CR, an existing CRLF stays), and the dedupe key and the forget fingerprint
    /// are computed from that text — exactly what storing the command computes, so a stored copy is recognized.
    /// </summary>
    [Fact]
    public void ClipboardText_UsesCrlf_AndTheKeysFollowIt()
    {
        var command = new ShellCommand(ShellKind.PowerShell, "if (1) {\n'BC-TEST'\r}\r\nx", 2, null, "PowerShell");
        Assert.Equal("if (1) {\r\n'BC-TEST'\r\n}\r\nx", command.ClipboardText);
        Assert.Equal(ContentHasher.ForText(command.ClipboardText), command.ContentHash);
        Assert.Equal(ForgetFingerprint.ForText(command.ClipboardText), command.Fingerprint);
        Assert.Equal(ContentClassifier.Classify(TestData.Text(command.ClipboardText))!.ContentHash, command.ContentHash);
        Assert.Same("BC-TEST one line", ShellCommand.ToClipboardText("BC-TEST one line"));
    }

    /// <summary>Blank text, a blank source and a count below 1 are refused.</summary>
    [Fact]
    public void Constructor_RefusesBlankTextAndZeroCount()
    {
        Assert.ThrowsAny<ArgumentException>(() => new ShellCommand(ShellKind.Cmd, "  ", 1, null, "Command Prompt"));
        Assert.ThrowsAny<ArgumentException>(() => new ShellCommand(ShellKind.Cmd, "BC-TEST", 1, null, " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShellCommand(ShellKind.Cmd, "BC-TEST", 0, null, "Command Prompt"));
    }
}

/// <summary>
/// Tests for reading a cmd window's history twice (<see cref="CmdHistory.NewSince"/>, with conhost's measured rules)
/// and for the kept list (CLAUDE.md §2.16).
/// </summary>
public sealed class CmdHistoryTests
{
    private static readonly DateTimeOffset Now = TestData.Now;

    /// <summary>The ways a window's history changes between two reads, and what counts as typed.</summary>
    /// <param name="previous">The earlier read, '|'-separated (empty = none).</param>
    /// <param name="current">The later read.</param>
    /// <param name="expected">The new commands.</param>
    [Theory]
    [InlineData("a|b", "a|b", "")]               // nothing typed (or the same command again: conhost stores it once)
    [InlineData("a|b", "a|b|c", "c")]            // a new command
    [InlineData("a|b|c", "b|c|d", "d")]          // full: the oldest fell off the front
    [InlineData("a|b", "a|b|a", "a")]            // an older command typed again is appended again
    [InlineData("a|b|c", "b|c|a", "a")]          // HistoryNoDup: moved to the end
    [InlineData("a|b", "", "")]                  // cleared (Alt+F7)
    [InlineData("a|b", "c", "c")]                // cleared, then a new command
    [InlineData("a|b", "a", "a")]                // cleared, then an older one again
    [InlineData("", "a|b", "a|b")]               // an empty history before
    [InlineData("a|b|c", "a|x|y", "x|y")]        // nothing explains it: only what was not there before
    public void NewSince_FindsWhatWasTyped(string previous, string current, string expected)
    {
        static string[] Split(string s) => s.Length == 0 ? [] : s.Split('|');
        Assert.Equal(Split(expected), CmdHistory.NewSince(Split(previous), Split(current)));
    }

    /// <summary>
    /// A window seen for the first time adds what it holds without counting known commands again; commands typed
    /// since the last read count once more and move to the front. Order within one call is kept by milliseconds.
    /// </summary>
    [Fact]
    public void Remember_CountsOnlyWhatWasTypedAgain()
    {
        IReadOnlyList<KeptCommand> kept = [new("BC-TEST a", 3, Now.AddHours(-1))];
        var first = CmdHistory.Remember(kept, ["BC-TEST a", "BC-TEST b"], typedAgain: false, Now);
        Assert.Equal([new KeptCommand("BC-TEST b", 1, Now), new KeptCommand("BC-TEST a", 3, Now.AddHours(-1))], first);

        var again = CmdHistory.Remember(first, ["BC-TEST a", "BC-TEST c"], typedAgain: true, Now.AddMinutes(1));
        Assert.Equal(
            [new KeptCommand("BC-TEST c", 1, Now.AddMinutes(1)), new KeptCommand("BC-TEST a", 4, Now.AddMinutes(1).AddMilliseconds(-1)), new KeptCommand("BC-TEST b", 1, Now)],
            again);
    }

    /// <summary>Only one-line, non-blank commands within the length limit are kept; the list keeps the newest <see cref="CmdHistory.MaxKept"/>.</summary>
    [Fact]
    public void Remember_SkipsUnkeepableAndTrims()
    {
        var kept = CmdHistory.Remember([], ["  ", "BC-TEST x\ny", new string('x', CmdHistory.MaxCommandLength + 1), "BC-TEST ok"], typedAgain: true, Now);
        Assert.Equal(["BC-TEST ok"], kept.Select(k => k.Text));

        var many = Enumerable.Range(0, CmdHistory.MaxKept + 5).Select(i => $"BC-TEST {i}").ToList();
        var trimmed = CmdHistory.Remember([], many, typedAgain: true, Now);
        Assert.Equal(CmdHistory.MaxKept, trimmed.Count);
        Assert.Equal($"BC-TEST {CmdHistory.MaxKept + 4}", trimmed[0].Text);
    }

    /// <summary>The kept list round-trips (a tab inside a command included); malformed lines lose only themselves.</summary>
    [Fact]
    public void KeptList_RoundTripsAndToleratesDamage()
    {
        IReadOnlyList<KeptCommand> kept = [new("BC-TEST a\tb", 2, Now), new("BC-TEST c", 1, Now.AddDays(-1))];
        Assert.Equal(kept, CmdHistory.ParseKept(CmdHistory.FormatKept(kept)));

        var damaged = "junk\nx\t1\tBC-TEST bad ticks\n" + CmdHistory.FormatKept(kept) + "1\t0\tBC-TEST zero count\n";
        Assert.Equal(kept, CmdHistory.ParseKept(damaged));
        Assert.Empty(CmdHistory.ParseKept(null));
        Assert.Empty(CmdHistory.ParseKept(string.Empty));
    }
}

/// <summary>
/// Tests for the shell tabs' rows (<see cref="ShellTab"/>): stored entries first, live commands after, shown once,
/// never when forgotten, hidden until typed again.
/// </summary>
public sealed class ShellTabTests
{
    /// <summary>Stored entries come first (pinned on top when asked, then most recently used), then live commands in their order.</summary>
    [Fact]
    public void Merge_StoredFirstThenLiveCommands()
    {
        var older = Entry(1, "BC-TEST kept old", TestData.Now.AddHours(-2));
        var pinned = Entry(2, "BC-TEST kept pinned", TestData.Now.AddHours(-3), pinned: true);
        var newer = Entry(3, "BC-TEST kept new", TestData.Now);
        var live = new[] { Command("BC-TEST live 1"), Command("BC-TEST live 2") };

        var rows = ShellTab.Merge([older, pinned, newer], live, [], new HashSet<string>(), pinnedFirst: true);
        Assert.Equal([2L, 3L, 1L], rows.Where(r => r.Entry is not null).Select(r => r.Entry!.Id));
        Assert.Equal(["BC-TEST live 1", "BC-TEST live 2"], rows.Where(r => r.Command is not null).Select(r => r.Command!.Text));
        Assert.Equal(5, rows.Count);

        var unpinnedOrder = ShellTab.Merge([older, pinned, newer], [], [], new HashSet<string>(), pinnedFirst: false);
        Assert.Equal([3L, 1L, 2L], unpinnedOrder.Select(r => r.Entry!.Id));
    }

    /// <summary>A live command that is also stored shows once (as the entry); a forgotten one never; a repeat once.</summary>
    [Fact]
    public void Merge_DropsStoredForgottenAndRepeatedCommands()
    {
        var stored = Entry(1, "BC-TEST kept", TestData.Now);
        var forgotten = Command("BC-TEST forgotten");
        var live = new[] { Command("BC-TEST kept"), forgotten, Command("BC-TEST shown"), Command("BC-TEST shown") };

        var rows = ShellTab.Merge([stored], live, [], new HashSet<string> { forgotten.Fingerprint }, pinnedFirst: false);
        Assert.Equal([1L], rows.Where(r => r.Entry is not null).Select(r => r.Entry!.Id));
        Assert.Equal(["BC-TEST shown"], rows.Where(r => r.Command is not null).Select(r => r.Command!.Text));
    }

    /// <summary>A hidden command stays hidden at the count it was hidden at, and shows again once typed again.</summary>
    [Fact]
    public void Hide_LastsUntilTypedAgain()
    {
        var twice = Command("BC-TEST hidden", count: 2);
        var hidden = ShellTab.Hide([], twice.ContentHash, twice.Count);
        Assert.Empty(ShellTab.Merge([], [twice], hidden, new HashSet<string>(), false));

        var thrice = Command("BC-TEST hidden", count: 3);
        Assert.Single(ShellTab.Merge([], [thrice], hidden, new HashSet<string>(), false));
    }

    /// <summary>Hides round-trip newest first, keep only the newest <see cref="ShellTab.MaxHidden"/>, and skip damaged lines.</summary>
    [Fact]
    public void Hidden_RoundTripsNewestFirstAndTrims()
    {
        IReadOnlyList<HiddenCommand> hidden = [];
        for (int i = 0; i < ShellTab.MaxHidden + 3; i++)
        {
            hidden = ShellTab.Hide(hidden, $"hash{i}", i + 1);
        }

        Assert.Equal(ShellTab.MaxHidden, hidden.Count);
        Assert.Equal(new HiddenCommand($"hash{ShellTab.MaxHidden + 2}", ShellTab.MaxHidden + 3), hidden[0]);
        Assert.Equal(hidden, ShellTab.ParseHidden(ShellTab.FormatHidden(hidden)));

        // Hiding a hash again moves it to the front with the new count.
        var again = ShellTab.Hide(hidden, "hash5", 99);
        Assert.Equal(new HiddenCommand("hash5", 99), again[0]);
        Assert.Single(again, h => h.ContentHash == "hash5");

        Assert.Equal([new HiddenCommand("ok", 2)], ShellTab.ParseHidden("x\tbad\n0\tzero\n\tnohash\n2\tok\n"));
        Assert.Equal("pwsh.hidden", ShellTab.HiddenStateName(ShellKind.PowerShell));
        Assert.Equal("cmd.hidden", ShellTab.HiddenStateName(ShellKind.Cmd));
    }

    /// <summary>Builds a stored entry with a text's real content hash.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="text">Its text.</param>
    /// <param name="lastUsed">Its last use.</param>
    /// <param name="pinned">Whether pinned.</param>
    /// <returns>The entry.</returns>
    private static ClipEntry Entry(long id, string text, DateTimeOffset lastUsed, bool pinned = false) => new()
    {
        Id = id,
        Kind = ClipKind.Text,
        Preview = text,
        ContentHash = ContentHasher.ForText(text),
        CreatedUtc = lastUsed,
        LastUsedUtc = lastUsed,
        UseCount = 1,
        IsPinned = pinned,
        Origin = ClipOrigin.PowerShell,
    };

    /// <summary>Builds a live PowerShell command.</summary>
    /// <param name="text">Its text.</param>
    /// <param name="count">How often typed.</param>
    /// <returns>The command.</returns>
    private static ShellCommand Command(string text, int count = 1) => new(ShellKind.PowerShell, text, count, null, "PowerShell");
}

/// <summary>Tests for telling a person's cmd windows from the ones tools start (<see cref="InteractiveConsoles"/>).</summary>
public sealed class InteractiveConsolesTests
{
    /// <summary>
    /// The first ancestor that is not a shell decides: Explorer, Windows Terminal and IDEs mean a person; node, an AI
    /// agent or a browser mean a tool. A parent that exited counts as a person; an unknown process is not read.
    /// </summary>
    [Fact]
    public void IsInteractive_DecidesByTheFirstNonShellAncestor()
    {
        var processes = new Dictionary<uint, (string Name, uint Parent)>
        {
            [4] = ("explorer.exe", 2),
            [10] = ("cmd.exe", 4),                // Win+R
            [20] = ("WindowsTerminal.exe", 4),
            [21] = ("pwsh.exe", 20),
            [22] = ("cmd.exe", 21),               // cmd typed in a pwsh tab
            [30] = ("node.exe", 4),
            [31] = ("cmd.exe", 30),               // a tool's cmd
            [32] = ("pwsh.exe", 30),
            [33] = ("cmd.exe", 32),               // a tool's cmd through PowerShell
            [40] = ("cmd.exe", 999),              // parent exited
            [50] = ("cmd.exe", 51),
            [51] = ("cmd.exe", 50),               // a cycle through reused ids
            [60] = ("Code.exe", 4),
            [61] = ("cmd.exe", 60),               // VS Code's terminal
        };

        Assert.True(InteractiveConsoles.IsInteractive(10, processes));
        Assert.True(InteractiveConsoles.IsInteractive(22, processes));
        Assert.False(InteractiveConsoles.IsInteractive(31, processes));
        Assert.False(InteractiveConsoles.IsInteractive(33, processes));
        Assert.True(InteractiveConsoles.IsInteractive(40, processes));
        Assert.True(InteractiveConsoles.IsInteractive(50, processes));
        Assert.True(InteractiveConsoles.IsInteractive(61, processes));
        Assert.False(InteractiveConsoles.IsInteractive(12345, processes));
    }
}

/// <summary>
/// Tests for the shell tabs' stored half: the filters, the origins' merge rules (like an Everything keep: a new
/// event that lifts a tombstone), and forgetting a command that is not stored.
/// </summary>
public sealed class ShellStoreTests : IDisposable
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private readonly ClipStore store;

    /// <summary>Creates a fresh store per test.</summary>
    public ShellStoreTests()
    {
        store = new ClipStore(temp.DatabasePath);
        store.Initialize();
    }

    /// <summary>Deletes the temp database.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>
    /// Each tab's filter returns what was kept from it: by origin, or — for a keep that bumped an older copy — by the
    /// source name the keep set. Neither returns ordinary copies, and the two shells stay apart.
    /// </summary>
    [Fact]
    public void Filters_ReturnTheirKeptCommands()
    {
        var copied = Add(TestData.Text("BC-TEST copied"));
        var pwsh = Add(Keep("BC-TEST pwsh", ShellKind.PowerShell));
        var cmd = Add(Keep("BC-TEST cmd", ShellKind.Cmd));
        var bumped = Add(TestData.Text("BC-TEST copied first, kept later"));
        Add(Keep("BC-TEST copied first, kept later", ShellKind.PowerShell));

        Assert.Equal([bumped.Id, pwsh.Id], store.Query(new ClipQuery { Filter = ClipFilter.PowerShell, PinnedFirst = false }).Select(e => e.Id));
        Assert.Equal([cmd.Id], store.Query(new ClipQuery { Filter = ClipFilter.Cmd }).Select(e => e.Id));
        Assert.Equal(ClipOrigin.Captured, store.GetEntry(bumped.Id)!.Origin);
        Assert.Equal(ShellSources.PowerShellName, store.GetEntry(bumped.Id)!.SourceAppName);
        Assert.DoesNotContain(copied.Id, store.Query(new ClipQuery { Filter = ClipFilter.PowerShell }).Select(e => e.Id));
    }

    /// <summary>
    /// Keeping a command the user deleted earlier stores it again: like a copy, it is an explicit action — while an
    /// import of the same deleted text stays suppressed.
    /// </summary>
    [Fact]
    public void Keep_LiftsATombstone()
    {
        var entry = Add(TestData.Text("BC-TEST deleted"));
        Assert.True(store.Delete(entry.Id, TestData.Now));
        Assert.Null(store.Upsert(TestData.Text("BC-TEST deleted", origin: ClipOrigin.WindowsHistory), Classify("BC-TEST deleted"), null, bumpIfExists: false));
        Assert.NotNull(store.Upsert(Keep("BC-TEST deleted", ShellKind.Cmd), Classify("BC-TEST deleted"), null, bumpIfExists: true));
    }

    /// <summary>
    /// Forgetting a command that is not stored puts it on the list (text facts only); forgetting one that is stored
    /// deletes it and its look-alikes, and the same text from any app is then forgotten.
    /// </summary>
    [Fact]
    public void ForgetText_ListsAndDeletesLookAlikes()
    {
        var a = Add(TestData.Text("BC-TEST secret\r\n"));
        var b = Add(TestData.Text("  BC-TEST secret"));
        var other = Add(TestData.Text("BC-TEST secret 2"));

        var result = store.ForgetText("BC-TEST secret", "PowerShell", TestData.Now);
        Assert.Equal([a.Id, b.Id], result.RemovedIds.Order());
        Assert.Equal(new ForgottenItem(result.Item.Id, ClipKind.Text, 14, null, null, null, "PowerShell", TestData.Now, 0, null), result.Item);
        Assert.NotNull(store.GetEntry(other.Id));
        Assert.True(store.IsForgotten(ForgetFingerprint.ForText("BC-TEST secret")!));

        var notStored = store.ForgetText("BC-TEST never stored", null, TestData.Now);
        Assert.Empty(notStored.RemovedIds);
        Assert.Equal(2, store.CountForgotten());
        Assert.ThrowsAny<ArgumentException>(() => store.ForgetText("   ", null, TestData.Now));
    }

    /// <summary>A kept command, as the controller builds it.</summary>
    /// <param name="text">The command.</param>
    /// <param name="shell">Its shell.</param>
    /// <returns>The capture.</returns>
    private static ClipCapture Keep(string text, ShellKind shell) =>
        TestData.Text(text, origin: ShellSources.OriginOf(shell), source: ShellSources.For(shell));

    /// <summary>Classifies a text capture.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The classification.</returns>
    private static ClassifiedClip Classify(string text) => ContentClassifier.Classify(TestData.Text(text))!;

    /// <summary>Stores a capture like a live copy.</summary>
    /// <param name="capture">The capture.</param>
    /// <returns>The entry.</returns>
    private ClipEntry Add(ClipCapture capture) =>
        store.Upsert(capture, ContentClassifier.Classify(capture)!, null, bumpIfExists: true)!.Entry;
}

/// <summary>
/// Tests for the shell origins in the history service: pause and ignored apps refuse a keep like a copy, and
/// forgetting a command through the worker raises the events the panel and Settings listen to.
/// </summary>
public sealed class ShellServiceTests : IAsyncLifetime
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private CaptureRules rules = new() { Retention = RetentionPolicy.Unlimited };
    private ClipHistoryService service = null!;
    private int forgottenEvents;

    /// <summary>Starts a service over a temp store.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask InitializeAsync()
    {
        var store = new ClipStore(temp.DatabasePath);
        store.Initialize();
        service = new ClipHistoryService(store, new NoImages(), () => rules);
        service.ForgottenChanged += (_, _) => Interlocked.Increment(ref forgottenEvents);
        service.Start();
        return ValueTask.CompletedTask;
    }

    /// <summary>Stops the service and deletes the store.</summary>
    /// <returns>A task.</returns>
    public async ValueTask DisposeAsync()
    {
        await service.DisposeAsync();
        temp.Dispose();
    }

    /// <summary>A keep from a shell tab is a new event: refused while paused, and when its shell is an ignored app.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Keep_RespectsPauseAndIgnoredApps()
    {
        var keep = TestData.Text("BC-TEST kept", origin: ClipOrigin.PowerShell, source: ShellSources.For(ShellKind.PowerShell));
        rules = rules with { IsPaused = true };
        Assert.Null(await service.AddAsync(keep));

        rules = rules with { IsPaused = false, IgnoredProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "powershell" } };
        Assert.Null(await service.AddAsync(keep));

        rules = rules with { IgnoredProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) };
        Assert.NotNull(await service.AddAsync(keep));
    }

    /// <summary>Forgetting a command keeps every later copy out and tells Settings.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task ForgetText_KeepsCopiesOut()
    {
        var result = await service.ForgetTextAsync("BC-TEST forgotten command", "Command Prompt");
        Assert.Empty(result.RemovedIds);
        Assert.Equal(1, Volatile.Read(ref forgottenEvents));
        Assert.Null(await service.AddAsync(TestData.Text("BC-TEST forgotten command\r\n")));
    }

    /// <summary>Text-only tests never analyze images.</summary>
    private sealed class NoImages : IImageAnalyzer
    {
        /// <inheritdoc />
        public Task<ImageAnalysis?> AnalyzeAsync(ClipCapture capture, CancellationToken cancellationToken) =>
            Task.FromResult<ImageAnalysis?>(null);
    }
}
