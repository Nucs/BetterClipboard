using BetterClipboard.Core.Cli;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Integrations;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// The pure Win+R list logic (<see cref="RunMru"/>): reading the list, fingerprints, which commands ran since a
/// snapshot, the snapshot format, and the captures one look stores. Commands here are BC-TEST strings only.
/// </summary>
public sealed class RunMruTests
{
    /// <summary>Raw values as Windows writes them: the command plus <c>\1</c>.</summary>
    private static readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = @"notepad\1",
        ["b"] = @"cmd /c echo BC-TEST\1",
        ["c"] = @"C:\BC-TEST\folder\1",
    };

    /// <summary>The list follows <c>MRUList</c> (most recent first) and loses the show-command suffix.</summary>
    [Fact]
    public void Parse_FollowsMruListOrder_AndStripsTheShowCommand()
    {
        Assert.Equal([@"C:\BC-TEST\folder", "notepad", "cmd /c echo BC-TEST"], RunMru.Parse("cab", Values));
        Assert.Empty(RunMru.Parse(null, Values));
        Assert.Empty(RunMru.Parse(string.Empty, Values));
    }

    /// <summary>
    /// The key is user-editable: letters without a value, characters outside a–z, blank values and a command
    /// listed twice (only possible by hand, compared like the dialog: case-insensitively) are skipped.
    /// </summary>
    [Fact]
    public void Parse_SkipsWhatTheDialogWouldNotShow()
    {
        var values = new Dictionary<string, string>(Values, StringComparer.OrdinalIgnoreCase)
        {
            ["d"] = @"   \1",
            ["e"] = @"NOTEPAD\1",
            ["f"] = "  spaced BC-TEST  ",
        };

        Assert.Equal(["notepad", "cmd /c echo BC-TEST", "spaced BC-TEST"], RunMru.Parse("axb?dZef", values));
    }

    /// <summary>Only a backslash with one or two digits at the very end is the suffix.</summary>
    [Fact]
    public void StripShowCommand_RemovesOnlyTheSuffix()
    {
        Assert.Equal("calc", RunMru.StripShowCommand(@"calc\1"));
        Assert.Equal("calc", RunMru.StripShowCommand(@"calc\12"));
        Assert.Equal(@"calc\123", RunMru.StripShowCommand(@"calc\123"));
        Assert.Equal("calc", RunMru.StripShowCommand("calc"));
        Assert.Equal(@"C:\BC-TEST", RunMru.StripShowCommand(@"C:\BC-TEST\1"));
        Assert.Throws<ArgumentNullException>(() => RunMru.StripShowCommand(null!));
    }

    /// <summary>
    /// Known answers (Python: <c>hashlib.sha256(s.upper().encode()).hexdigest()[:16]</c>) freeze the snapshot
    /// format: a changed fingerprint makes every remembered command look like a new run after an update.
    /// </summary>
    [Fact]
    public void Fingerprint_KnownAnswers_CaseInsensitive()
    {
        Assert.Equal("dd606b024c328553", RunMru.Fingerprint("notepad"));
        Assert.Equal("dd606b024c328553", RunMru.Fingerprint("NotePad"));
        Assert.Equal("ef8ee5d78919e240", RunMru.Fingerprint("cmd /c echo BC-TEST"));
        Assert.Equal("747ea8422bae717e", RunMru.Fingerprint("écho BC-TEST"));
        Assert.Equal("dbbc38d727b823f9", RunMru.Fingerprint("פנקס BC-TEST"));
        Assert.NotEqual(RunMru.Fingerprint("notepad"), RunMru.Fingerprint("notepad2"));
    }

    /// <summary>The runs since a snapshot, for every way the list changes (see <see cref="RunMru"/>'s remarks).</summary>
    /// <param name="previous">The list at the snapshot, most recent first (letters stand for commands).</param>
    /// <param name="current">The list now.</param>
    /// <param name="expected">The commands reported as run, most recent first.</param>
    [Theory]
    [InlineData("abc", "abc", "")] // nothing ran (or the newest ran again: invisible by design)
    [InlineData("abc", "dabc", "d")] // a new command, list not full
    [InlineData("abc", "dab", "d")] // a new command evicting the oldest (full list)
    [InlineData("abc", "cab", "c")] // an older command ran again
    [InlineData("abc", "edab", "ed")] // two new commands
    [InlineData("abc", "bdac", "bd")] // a new command, then an older one again
    [InlineData("abc", "", "")] // the list was cleared
    [InlineData("abc", "ac", "")] // an entry deleted by hand
    [InlineData("", "ba", "ba")] // the first runs into an empty list
    [InlineData("ab", "ab", "")] // ran b then a again: back in the old order, so invisible (documented limit)
    public void RunsSince_ReportsTheMovedPrefix(string previous, string current, string expected)
    {
        var commands = current.Select(Command).ToArray();
        var snapshot = previous.Select(letter => RunMru.Fingerprint(Command(letter))).ToArray();
        Assert.Equal(expected.Select(Command), RunMru.RunsSince(snapshot, commands));
    }

    /// <summary>The comparison is case-insensitive like the dialog's: a command retyped in other case is no run.</summary>
    [Fact]
    public void RunsSince_IgnoresLetterCase()
    {
        Assert.Empty(RunMru.RunsSince([RunMru.Fingerprint("NOTEPAD")], ["notepad"]));
    }

    /// <summary>The snapshot round-trips; an empty list is a snapshot, a missing or foreign value is not.</summary>
    [Fact]
    public void Snapshot_RoundTrips_AndTellsFirstLookApart()
    {
        var stored = RunMru.FormatSnapshot(["notepad", "cmd /c echo BC-TEST"]);
        Assert.Equal("v1:dd606b024c328553,ef8ee5d78919e240", stored);
        Assert.True(RunMru.TryParseSnapshot(stored, out var fingerprints));
        Assert.Equal(["dd606b024c328553", "ef8ee5d78919e240"], fingerprints);

        Assert.Equal("v1:", RunMru.FormatSnapshot([]));
        Assert.True(RunMru.TryParseSnapshot("v1:", out var empty));
        Assert.Empty(empty);

        Assert.False(RunMru.TryParseSnapshot(null, out _));
        Assert.False(RunMru.TryParseSnapshot(string.Empty, out _)); // switched off
        Assert.False(RunMru.TryParseSnapshot("v0:dd606b024c328553", out _));
    }

    /// <summary>
    /// The first look imports what Windows remembers, oldest first, one second apart below the key's last write;
    /// later looks store only the runs, as new events, a millisecond apart.
    /// </summary>
    [Fact]
    public void PlanCaptures_ImportsFirst_ThenOnlyRuns()
    {
        var newest = TestData.Now;
        string[] list = ["BC-TEST c", "BC-TEST b", "BC-TEST a"];

        var first = RunMru.PlanCaptures(null, list, newest);
        Assert.Equal(["BC-TEST a", "BC-TEST b", "BC-TEST c"], first.Select(TextOf));
        Assert.Equal([newest.AddSeconds(-2), newest.AddSeconds(-1), newest], first.Select(c => c.CapturedAtUtc));
        Assert.All(first, c => Assert.Equal(ClipOrigin.RunDialogHistory, c.Origin));
        Assert.All(first, c => Assert.Equal(RunMru.Source, c.Source));

        var snapshot = list.Select(RunMru.Fingerprint).ToArray();
        string[] now = ["BC-TEST e", "BC-TEST d", "BC-TEST c", "BC-TEST b"];
        var runs = RunMru.PlanCaptures(snapshot, now, newest);
        Assert.Equal(["BC-TEST d", "BC-TEST e"], runs.Select(TextOf));
        Assert.Equal([newest.AddMilliseconds(-1), newest], runs.Select(c => c.CapturedAtUtc));
        Assert.All(runs, c => Assert.Equal(ClipOrigin.RunDialog, c.Origin));

        Assert.Empty(RunMru.PlanCaptures(snapshot, list, newest));
    }

    /// <summary>A capture is the command as plain text; a blank command is refused.</summary>
    [Fact]
    public void CreateCapture_IsPlainText()
    {
        var capture = RunMru.CreateCapture("cmd /c echo BC-TEST", TestData.Now, observedRun: true);
        Assert.Equal([ClipFormatNames.UnicodeText], capture.Formats.Select(f => f.Name));
        Assert.Equal("cmd /c echo BC-TEST", TextOf(capture));
        Assert.Equal("Win+R", capture.Source!.DisplayName);
        Assert.Null(capture.Source.ExecutablePath);
        Assert.Throws<ArgumentException>(() => RunMru.CreateCapture("  ", TestData.Now, observedRun: false));
    }

    /// <summary>The test command a letter stands for.</summary>
    /// <param name="letter">A letter.</param>
    /// <returns>"BC-TEST x".</returns>
    private static string Command(char letter) => $"BC-TEST {letter}";

    /// <summary>The text of a plain-text capture.</summary>
    /// <param name="capture">The capture.</param>
    /// <returns>Its text.</returns>
    internal static string TextOf(ClipCapture capture) => UnicodeTextCodec.Decode(capture.Formats.Single(f => f.Name == ClipFormatNames.UnicodeText).Data);
}

/// <summary>
/// How the store keeps Win+R commands: the run time column, the Run filter, the merge rules of the two origins,
/// tombstones, and a store from before the column.
/// </summary>
public sealed class RunHistoryStoreTests : IDisposable
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private readonly ClipStore store;

    /// <summary>Creates a fresh store per test.</summary>
    public RunHistoryStoreTests()
    {
        store = new ClipStore(temp.DatabasePath);
        store.Initialize();
    }

    /// <summary>Deletes the temp database.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>A run gets a run time and is the only entry in the Run filter; the stats count it.</summary>
    [Fact]
    public void Run_SetsRunTime_AndJoinsTheRunFilter()
    {
        var copy = Add(TestData.Text("BC-TEST copied"), bump: true);
        var run = Add(RunMru.CreateCapture("BC-TEST cmd", TestData.Now.AddMinutes(1), observedRun: true), bump: true);

        Assert.Null(copy.LastRunUtc);
        Assert.False(copy.HasRunHistory);
        Assert.Equal(TestData.Now.AddMinutes(1), run.LastRunUtc);
        Assert.Equal(ClipOrigin.RunDialog, run.Origin);
        Assert.Equal("Win+R", run.SourceAppName);
        Assert.Equal([run.Id], store.Query(new ClipQuery { Filter = ClipFilter.Run }).Select(e => e.Id));
        Assert.Equal(1, store.GetStats().RunCount);
    }

    /// <summary>
    /// An imported command that is already in history (copied earlier) joins the Run tab but keeps its place: an
    /// import never reorders.
    /// </summary>
    [Fact]
    public void Import_MarksAnExistingCopy_WithoutReordering()
    {
        var copy = Add(TestData.Text("BC-TEST notepad"), bump: true);
        var other = Add(TestData.Text("BC-TEST other", TestData.Now.AddMinutes(1)), bump: true);
        var imported = Add(RunMru.CreateCapture("BC-TEST notepad", TestData.Now.AddMinutes(2), observedRun: false), bump: false);

        Assert.Equal(copy.Id, imported.Id);
        Assert.Equal(TestData.Now, imported.LastUsedUtc);
        Assert.Equal(TestData.Now.AddMinutes(2), imported.LastRunUtc);
        Assert.Equal(ClipOrigin.Captured, imported.Origin);
        Assert.Equal([other.Id, copy.Id], store.Query(new ClipQuery { PinnedFirst = false }).Select(e => e.Id));
        Assert.Equal([copy.Id], store.Query(new ClipQuery { Filter = ClipFilter.Run }).Select(e => e.Id));
    }

    /// <summary>A run of something copied before is a new event: it moves up and counts as a use.</summary>
    [Fact]
    public void Run_BumpsAnExistingCopy()
    {
        var copy = Add(TestData.Text("BC-TEST calc"), bump: true);
        var run = Add(RunMru.CreateCapture("BC-TEST calc", TestData.Now.AddMinutes(5), observedRun: true), bump: true);

        Assert.Equal(copy.Id, run.Id);
        Assert.Equal(TestData.Now.AddMinutes(5), run.LastUsedUtc);
        Assert.Equal(TestData.Now.AddMinutes(5), run.LastRunUtc);
        Assert.Equal(2, run.UseCount);
    }

    /// <summary>The run time only moves forward, and other origins keep it (a later copy is not a run).</summary>
    [Fact]
    public void RunTime_NeverMovesBack_AndSurvivesCopies()
    {
        var run = Add(RunMru.CreateCapture("BC-TEST regedit", TestData.Now.AddMinutes(5), observedRun: true), bump: true);
        var reimported = Add(RunMru.CreateCapture("BC-TEST regedit", TestData.Now, observedRun: false), bump: false);
        var copied = Add(TestData.Text("BC-TEST regedit", TestData.Now.AddMinutes(9)), bump: true);

        Assert.Equal(run.Id, copied.Id);
        Assert.Equal(TestData.Now.AddMinutes(5), reimported.LastRunUtc);
        Assert.Equal(TestData.Now.AddMinutes(5), copied.LastRunUtc);
        Assert.Equal(TestData.Now.AddMinutes(9), copied.LastUsedUtc);
    }

    /// <summary>
    /// A deleted command never comes back through the old list (an import), but running it again (a new event)
    /// records it again.
    /// </summary>
    [Fact]
    public void Tombstones_StopImports_NotNewRuns()
    {
        var run = Add(RunMru.CreateCapture("BC-TEST gone", TestData.Now, observedRun: true), bump: true);
        Assert.True(store.Delete(run.Id, TestData.Now.AddMinutes(1)));

        var import = RunMru.CreateCapture("BC-TEST gone", TestData.Now, observedRun: false);
        Assert.Null(store.Upsert(import, ContentClassifier.Classify(import)!, null, bumpIfExists: false));

        var again = Add(RunMru.CreateCapture("BC-TEST gone", TestData.Now.AddMinutes(2), observedRun: true), bump: true);
        Assert.Equal(TestData.Now.AddMinutes(2), again.LastRunUtc);
    }

    /// <summary>
    /// A store from before the column (an older build) gets it at the next Initialize, idempotently; its rows
    /// simply have no run time until Windows' list marks them again.
    /// </summary>
    [Fact]
    public void Initialize_AddsTheColumnToAnOlderStore()
    {
        var run = Add(RunMru.CreateCapture("BC-TEST old", TestData.Now, observedRun: true), bump: true);
        ExecuteRaw("DROP INDEX ix_clips_run; ALTER TABLE clips DROP COLUMN run_last_utc;");

        var reopened = new ClipStore(temp.DatabasePath);
        reopened.Initialize();
        reopened.Initialize();
        Assert.Null(reopened.GetEntry(run.Id)!.LastRunUtc);
        Assert.Empty(reopened.Query(new ClipQuery { Filter = ClipFilter.Run }));

        var marked = reopened.Upsert(
            RunMru.CreateCapture("BC-TEST old", TestData.Now.AddMinutes(1), observedRun: false),
            ContentClassifier.Classify(RunMru.CreateCapture("BC-TEST old", TestData.Now, observedRun: false))!,
            null,
            bumpIfExists: false)!.Entry;
        Assert.Equal(TestData.Now.AddMinutes(1), marked.LastRunUtc);
        Assert.Equal([run.Id], reopened.Query(new ClipQuery { Filter = ClipFilter.Run }).Select(e => e.Id));
    }

    /// <summary>Stores a capture like the history worker would.</summary>
    /// <param name="capture">The capture.</param>
    /// <param name="bump">Whether a duplicate counts as a new event.</param>
    /// <returns>The stored entry.</returns>
    private ClipEntry Add(ClipCapture capture, bool bump) =>
        store.Upsert(capture, ContentClassifier.Classify(capture)!, null, bumpIfExists: bump)!.Entry;

    /// <summary>Runs SQL directly against the (plaintext test) database.</summary>
    /// <param name="sql">Statements.</param>
    private void ExecuteRaw(string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// The history service's rules for Win+R commands (pause, ignored apps, Forget forever) and the command line's
/// view of them (<c>-f run</c>, origins, <c>lastRun</c>).
/// </summary>
public sealed class RunHistoryServiceTests : IAsyncLifetime
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private CaptureRules rules = new() { Retention = RetentionPolicy.Unlimited };
    private ClipHistoryService history = null!;

    /// <summary>Starts a service over a temp store.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask InitializeAsync()
    {
        var store = new ClipStore(temp.DatabasePath);
        store.Initialize();
        history = new ClipHistoryService(store, null, () => rules);
        history.Start();
        return ValueTask.CompletedTask;
    }

    /// <summary>Stops the service and deletes the store.</summary>
    /// <returns>A task.</returns>
    public async ValueTask DisposeAsync()
    {
        await history.DisposeAsync();
        temp.Dispose();
    }

    /// <summary>
    /// Pause skips runs (new events) but not the first import of what Windows already remembered; adding
    /// "Win+R" to Ignored apps skips both, like the Settings switch.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Pause_SkipsRuns_IgnoredSkipsBoth()
    {
        rules = rules with { IsPaused = true };
        Assert.Null(await history.AddAsync(RunMru.CreateCapture("BC-TEST paused run", TestData.Now, observedRun: true)));
        Assert.Equal(1, await history.ImportAsync([RunMru.CreateCapture("BC-TEST remembered", TestData.Now, observedRun: false)]));

        rules = rules with { IsPaused = false, IgnoredProcessNames = new HashSet<string>(["Win+R"], StringComparer.OrdinalIgnoreCase) };
        Assert.Null(await history.AddAsync(RunMru.CreateCapture("BC-TEST ignored run", TestData.Now, observedRun: true)));
        Assert.Equal(0, await history.ImportAsync([RunMru.CreateCapture("BC-TEST ignored import", TestData.Now, observedRun: false)]));
    }

    /// <summary>A forgotten command is kept out of every Win+R channel, and only a run counts as "kept out".</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task ForgottenCommand_IsKeptOut()
    {
        var run = (await history.AddAsync(RunMru.CreateCapture("BC-TEST secret", TestData.Now, observedRun: true)))!;
        Assert.NotNull(await history.ForgetAsync(run.Id));

        Assert.Equal(0, await history.ImportAsync([RunMru.CreateCapture("BC-TEST secret", TestData.Now.AddMinutes(1), observedRun: false)]));
        Assert.Equal(0, (await history.GetForgottenAsync()).Single().BlockedCount);
        Assert.Null(await history.AddAsync(RunMru.CreateCapture("BC-TEST secret", TestData.Now.AddMinutes(2), observedRun: true)));
        Assert.Equal(1, (await history.GetForgottenAsync()).Single().BlockedCount);
    }

    /// <summary>The command line lists runs with <c>-f run</c> (also "runs"), labels both origins and reports the run time.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task CommandLine_ListsRuns()
    {
        await history.AddAsync(TestData.Text("BC-TEST unrelated"));
        var imported = (await history.AddAsync(RunMru.CreateCapture("BC-TEST imported", TestData.Now.AddMinutes(1), observedRun: false)))!;
        var ran = (await history.AddAsync(RunMru.CreateCapture("BC-TEST ran", TestData.Now.AddMinutes(2), observedRun: true)))!;
        var processor = new CliCommandProcessor(history, new NoClipboard(), new NoExporter(), () => new AppSettings(), () => null, "9.9.9");

        foreach (var filter in new[] { "run", "runs" })
        {
            var response = await processor.ExecuteAsync(new CliRequest { Command = CliCommands.List, Filter = filter }, TestContext.Current.CancellationToken);
            Assert.Equal([ran.Id, imported.Id], response.Items!.Select(i => i.Id));
            Assert.Equal(["run", "run-history"], response.Items!.Select(i => i.Origin));
            Assert.Equal([TestData.Now.AddMinutes(2), TestData.Now.AddMinutes(1)], response.Items!.Select(i => i.LastRun!.Value));
        }

        var all = await processor.ExecuteAsync(new CliRequest { Command = CliCommands.List }, TestContext.Current.CancellationToken);
        Assert.Null(all.Items!.Single(i => i.Preview == "BC-TEST unrelated").LastRun);
        Assert.Contains("\"lastRun\"", CliJson.Serialize(all), StringComparison.Ordinal);
        Assert.Equal("run", CliArguments.Parse(["list", "-f", "Run"], TestData.Now).Request!.Filter);
    }

    /// <summary>A clipboard that refuses writes (these tests never write).</summary>
    private sealed class NoClipboard : IClipboardWriter
    {
        /// <inheritdoc />
        public Task WriteAsync(IReadOnlyList<ClipFormatData> formats) => throw new InvalidOperationException("No clipboard in this test.");
    }

    /// <summary>An exporter that has no images (these tests never export).</summary>
    private sealed class NoExporter : IImageExporter
    {
        /// <inheritdoc />
        public Task<byte[]?> ToPngAsync(IReadOnlyList<ClipFormatData> formats, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
    }
}
