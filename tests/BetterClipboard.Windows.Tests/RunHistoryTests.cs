using System.Diagnostics;
using BetterClipboard.Core.Integrations;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Storage;
using BetterClipboard.Windows.Integrations;
using BetterClipboard.Windows.Shell;
using Microsoft.Win32;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// A throwaway Win+R list under <c>HKCU\Software\BetterClipboard-Tests\{guid}\RunMRU</c> (never the real one),
/// written the way Windows writes it: <c>a</c>–<c>z</c> values holding the command plus <c>\1</c>, and <c>MRUList</c>.
/// </summary>
/// <remarks>
/// Each list gets a parent of its own: while a list does not exist yet, the watch observes its parent, and test
/// classes run in parallel — a shared parent would see the other tests' keys come and go (and could be deleted
/// under a watch by another test's cleanup).
/// </remarks>
internal sealed class ScratchRunMru : IDisposable
{
    /// <summary>The root of every scratch list (removed when the last test leaves it empty).</summary>
    public const string RootPath = @"Software\BetterClipboard-Tests";

    /// <summary>This list's own parent.</summary>
    private readonly string parentPath = $@"{RootPath}\{Guid.NewGuid():N}";

    /// <summary>Creates the parent (the list itself is created by the first write).</summary>
    public ScratchRunMru()
    {
        KeyPath = $@"{parentPath}\RunMRU";
        Registry.CurrentUser.CreateSubKey(parentPath, writable: true).Dispose();
    }

    /// <summary>The scratch key, relative to <c>HKEY_CURRENT_USER</c>.</summary>
    public string KeyPath { get; }

    /// <summary>Replaces the list: commands most recent first, under the letters a, b, c… in that order.</summary>
    /// <param name="commands">The commands.</param>
    public void Write(params string[] commands)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        var letters = string.Empty;
        for (int i = 0; i < commands.Length; i++)
        {
            var letter = ((char)('a' + i)).ToString();
            key.SetValue(letter, commands[i] + @"\1");
            letters += letter;
        }

        key.SetValue("MRUList", letters);
    }

    /// <summary>
    /// Records a successful run like comctl32's MRU list: a command already listed (compared case-insensitively)
    /// moves to the front; a new one takes the first free letter, or, when all 26 are taken, overwrites the least
    /// recent one.
    /// </summary>
    /// <param name="command">The command as typed.</param>
    public void Run(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        var order = key.GetValue("MRUList") as string ?? string.Empty;
        int index = order.ToList().FindIndex(l => string.Equals(key.GetValue(l.ToString()) as string, command + @"\1", StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            order = order[index] + order.Remove(index, 1);
        }
        else
        {
            char letter = order.Length < RunMru.Capacity ? Enumerable.Range('a', 26).Select(c => (char)c).First(c => !order.Contains(c)) : order[^1];
            key.SetValue(letter.ToString(), command + @"\1");
            order = letter + (order.Length < RunMru.Capacity ? order : order[..^1]);
        }

        key.SetValue("MRUList", order);
    }

    /// <summary>Deletes the scratch list with its parent, and the root when no other test still uses it.</summary>
    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(parentPath, throwOnMissingSubKey: false);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(RootPath, writable: false);
            if (root is { SubKeyCount: 0, ValueCount: 0 })
            {
                Registry.CurrentUser.DeleteSubKey(RootPath, throwOnMissingSubKey: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Another test created a key under it meanwhile; the last one out removes it.
        }
    }
}

/// <summary>
/// Reading and watching a Win+R list: the reader (missing key, order, override forms, the settle wait) and the
/// change watch (writes, a key that appears later, disposal).
/// </summary>
public sealed class RunMruReaderTests : IDisposable
{
    private readonly ScratchRunMru scratch = new();

    /// <summary>Deletes the scratch key.</summary>
    public void Dispose() => scratch.Dispose();

    /// <summary>A key that was never written reads as an empty list without a time.</summary>
    [Fact]
    public void MissingKey_ReadsEmpty()
    {
        var state = RunMruReader.Read(scratch.KeyPath);
        Assert.False(state.KeyExists);
        Assert.Empty(state.Commands);
        Assert.Null(state.LastWriteUtc);
    }

    /// <summary>The list comes back most recent first, without the suffix, with the key's last write as its time.</summary>
    [Fact]
    public void Read_ReturnsTheListAndItsTime()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-2);
        scratch.Write("BC-TEST c", "BC-TEST b", "BC-TEST a");
        scratch.Run("BC-TEST a");

        var state = RunMruReader.Read(scratch.KeyPath);
        Assert.True(state.KeyExists);
        Assert.Equal(["BC-TEST a", "BC-TEST c", "BC-TEST b"], state.Commands);
        Assert.InRange(state.LastWriteUtc!.Value, before, DateTimeOffset.UtcNow.AddSeconds(2));
    }

    /// <summary>The override accepts the forms people paste and always stays under HKEY_CURRENT_USER.</summary>
    [Fact]
    public void ResolveKeyPath_AcceptsPastedForms()
    {
        Assert.Equal(RunMru.DefaultKeyPath, RunMruReader.ResolveKeyPath(null));
        Assert.Equal(RunMru.DefaultKeyPath, RunMruReader.ResolveKeyPath("  "));
        Assert.Equal(@"Software\X\RunMRU", RunMruReader.ResolveKeyPath(@"Software\X\RunMRU"));
        Assert.Equal(@"Software\X\RunMRU", RunMruReader.ResolveKeyPath(@"HKCU\Software\X\RunMRU\"));
        Assert.Equal(@"Software\X\RunMRU", RunMruReader.ResolveKeyPath(@"HKCU:\Software\X\RunMRU"));
        Assert.Equal(@"Software\X\RunMRU", RunMruReader.ResolveKeyPath(@"hkey_current_user\Software\X\RunMRU"));
        Assert.Equal(RunMru.DefaultKeyPath, RunMruReader.ResolveKeyPath(@"HKCU\"));
    }

    /// <summary>
    /// A key written a moment ago is read only once it has been quiet for the settle time (one run is two writes);
    /// a quiet key is read at once.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task ReadSettled_WaitsForAFreshWrite_Only()
    {
        // Timed from before the write: the read must come a settle time after it. The margin allows for the key's
        // last-write time being stamped up to one clock tick (15.6 ms) early.
        var sinceWrite = Stopwatch.StartNew();
        scratch.Write("BC-TEST settle");
        var state = await RunMruReader.ReadSettledAsync(scratch.KeyPath, TimeProvider.System, TestContext.Current.CancellationToken);
        sinceWrite.Stop();
        Assert.Equal(["BC-TEST settle"], state.Commands);
        Assert.True(sinceWrite.Elapsed >= TimeSpan.FromMilliseconds(120), $"returned {sinceWrite.Elapsed.TotalMilliseconds:0} ms after the write");

        var quiet = Stopwatch.StartNew();
        await RunMruReader.ReadSettledAsync(scratch.KeyPath, TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.True(quiet.Elapsed < TimeSpan.FromMilliseconds(100), $"waited {quiet.Elapsed.TotalMilliseconds:0} ms for a quiet key");
    }

    /// <summary>
    /// The watch reports the key appearing (watched through its parent) and every later write; after disposal it
    /// reports nothing.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Watcher_ReportsCreationAndWrites_UntilDisposed()
    {
        int changes = 0;
        var watcher = new RunMruWatcher(scratch.KeyPath);
        watcher.Changed += (_, _) => Interlocked.Increment(ref changes);
        watcher.Start();
        await Task.Delay(200, TestContext.Current.CancellationToken); // let the thread arm its first watch
        Assert.Equal(0, Volatile.Read(ref changes));

        scratch.Write("BC-TEST created");
        await RunHistoryIntegrationTests.WaitUntilAsync(() => Volatile.Read(ref changes) >= 1);

        int afterCreate = Volatile.Read(ref changes);
        scratch.Run("BC-TEST written");
        await RunHistoryIntegrationTests.WaitUntilAsync(() => Volatile.Read(ref changes) > afterCreate);

        watcher.Dispose();
        int afterDispose = Volatile.Read(ref changes);
        scratch.Run("BC-TEST unseen");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(afterDispose, Volatile.Read(ref changes));
    }
}

/// <summary>
/// <see cref="RunHistoryIntegration"/> over a real history and a scratch list: the first import, live runs,
/// re-runs, runs made while the app was away, off and on again, pause, a list that does not exist yet, keeping
/// more than Windows' 26, and runs started from the panel.
/// </summary>
public sealed class RunHistoryIntegrationTests : IAsyncLifetime
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", Guid.NewGuid().ToString("N"));
    private readonly ScratchRunMru scratch = new();
    private CaptureRules rules = new() { Retention = RetentionPolicy.Unlimited };
    private ClipHistoryService history = null!;

    /// <summary>Creates a history over a temp store.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(temp);
        var store = new ClipStore(Path.Combine(temp, "history.db"));
        store.Initialize();
        history = new ClipHistoryService(store, null, () => rules);
        history.Start();
        return ValueTask.CompletedTask;
    }

    /// <summary>Stops the history, deletes the temp store and the scratch key.</summary>
    /// <returns>A task.</returns>
    public async ValueTask DisposeAsync()
    {
        await history.DisposeAsync();
        scratch.Dispose();
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
    /// First activation imports Windows' list (newest on top, as imports), stores the snapshot, and watches.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task FirstActivation_ImportsWhatWindowsRemembers()
    {
        scratch.Write("BC-TEST c", "BC-TEST b", "BC-TEST a");
        await using var integration = new RunHistoryIntegration(history, scratch.KeyPath);
        await integration.StartAsync(enable: true);

        Assert.True(integration.IsWatching);
        Assert.Equal(3, integration.WindowsCount);
        Assert.Equal(0, integration.RecordedThisSession);
        var runs = await RunsAsync();
        Assert.Equal(["BC-TEST c", "BC-TEST b", "BC-TEST a"], runs.Select(e => e.Preview));
        Assert.All(runs, e => Assert.Equal(ClipOrigin.RunDialogHistory, e.Origin));
        Assert.True(RunMru.TryParseSnapshot(await history.GetStateValueAsync(RunHistoryIntegration.SnapshotStateName), out var snapshot));
        Assert.Equal(3, snapshot.Count);
    }

    /// <summary>A run is recorded the moment Windows writes it; an older command run again moves to the top.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task LiveRuns_AreRecorded_AndReRunsMoveUp()
    {
        scratch.Write("BC-TEST c", "BC-TEST b", "BC-TEST a");
        await using var integration = new RunHistoryIntegration(history, scratch.KeyPath);
        await integration.StartAsync(enable: true);

        scratch.Run("BC-TEST new");
        await WaitUntilAsync(() => RunsAsync().Result.FirstOrDefault()?.Preview == "BC-TEST new");
        Assert.Equal(ClipOrigin.RunDialog, (await RunsAsync())[0].Origin);
        await WaitUntilAsync(() => integration.RecordedThisSession == 1);

        scratch.Run("BC-TEST a");
        await WaitUntilAsync(() => RunsAsync().Result.FirstOrDefault()?.Preview == "BC-TEST a");
        Assert.Equal(["BC-TEST a", "BC-TEST new", "BC-TEST c", "BC-TEST b"], (await RunsAsync()).Select(e => e.Preview));
        Assert.Equal(2, integration.RecordedThisSession);
    }

    /// <summary>Runs made while the app was not running are found by the next start, as runs (not an import).</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Restart_FindsRunsMadeMeanwhile()
    {
        scratch.Write("BC-TEST a");
        await using (var first = new RunHistoryIntegration(history, scratch.KeyPath))
        {
            await first.StartAsync(enable: true);
        }

        scratch.Run("BC-TEST away 1");
        scratch.Run("BC-TEST away 2");
        await using var second = new RunHistoryIntegration(history, scratch.KeyPath);
        await second.StartAsync(enable: true);

        var runs = await RunsAsync();
        Assert.Equal(["BC-TEST away 2", "BC-TEST away 1", "BC-TEST a"], runs.Select(e => e.Preview));
        Assert.Equal([ClipOrigin.RunDialog, ClipOrigin.RunDialog, ClipOrigin.RunDialogHistory], runs.Select(e => e.Origin));
        Assert.Equal(2, second.RecordedThisSession);
    }

    /// <summary>
    /// Off stops watching and clears the snapshot; on again imports what Windows remembers then, without
    /// duplicating what is already kept.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task OffThenOn_StartsOver_WithoutDuplicates()
    {
        scratch.Write("BC-TEST b", "BC-TEST a");
        await using var integration = new RunHistoryIntegration(history, scratch.KeyPath);
        await integration.StartAsync(enable: true);

        await integration.SetEnabledAsync(false);
        Assert.False(integration.IsWatching);
        Assert.Equal(string.Empty, await history.GetStateValueAsync(RunHistoryIntegration.SnapshotStateName));

        scratch.Run("BC-TEST while off");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.Equal(2, (await RunsAsync()).Count);

        await integration.SetEnabledAsync(true);
        var runs = await RunsAsync();
        Assert.Equal(3, runs.Count);
        Assert.Equal(ClipOrigin.RunDialogHistory, runs.Single(e => e.Preview == "BC-TEST while off").Origin);
    }

    /// <summary>
    /// A run made while capture is paused is never recorded — not even by a later rescan — although the first
    /// import still happens while paused.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Paused_RunsAreSkippedForGood()
    {
        rules = rules with { IsPaused = true };
        scratch.Write("BC-TEST remembered");
        await using var integration = new RunHistoryIntegration(history, scratch.KeyPath);
        await integration.StartAsync(enable: true);
        Assert.Single(await RunsAsync());

        var before = await history.GetStateValueAsync(RunHistoryIntegration.SnapshotStateName);
        scratch.Run("BC-TEST paused");
        await WaitUntilAsync(() => history.GetStateValueAsync(RunHistoryIntegration.SnapshotStateName).Result != before);

        rules = rules with { IsPaused = false };
        Assert.Equal(0, await integration.RescanAsync("test"));
        Assert.Equal(["BC-TEST remembered"], (await RunsAsync()).Select(e => e.Preview));
    }

    /// <summary>
    /// A list that does not exist yet (Win+R never used) starts as an empty snapshot, so the very first run is a
    /// run, not an import.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task MissingList_FirstRunIsARun()
    {
        await using var integration = new RunHistoryIntegration(history, scratch.KeyPath);
        await integration.StartAsync(enable: true);
        Assert.Equal(0, integration.WindowsCount);
        Assert.Equal("v1:", await history.GetStateValueAsync(RunHistoryIntegration.SnapshotStateName));

        scratch.Run("BC-TEST first ever");
        await WaitUntilAsync(() => RunsAsync().Result.Count == 1);
        Assert.Equal(ClipOrigin.RunDialog, (await RunsAsync())[0].Origin);
    }

    /// <summary>
    /// The point of the feature: with Windows' list full, every new run evicts its oldest command there, and the
    /// history keeps all of them.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task FullList_KeepsWhatWindowsEvicts()
    {
        scratch.Write(Enumerable.Range(1, RunMru.Capacity).Select(i => $"BC-TEST {i:00}").Reverse().ToArray());
        await using var integration = new RunHistoryIntegration(history, scratch.KeyPath);
        await integration.StartAsync(enable: true);
        Assert.Equal(RunMru.Capacity, (await RunsAsync()).Count);

        scratch.Run("BC-TEST 27");
        scratch.Run("BC-TEST 28");
        await WaitUntilAsync(() => RunsAsync().Result.Count == RunMru.Capacity + 2);

        Assert.Equal(RunMru.Capacity, RunMruReader.Read(scratch.KeyPath).Commands.Count);
        Assert.DoesNotContain("BC-TEST 01", RunMruReader.Read(scratch.KeyPath).Commands);
        Assert.Contains(await RunsAsync(), e => e.Preview == "BC-TEST 01");
        Assert.Equal(["BC-TEST 28", "BC-TEST 27"], (await RunsAsync()).Take(2).Select(e => e.Preview));
    }

    /// <summary>
    /// A rescan racing the watch (entering the Run tab right after a run) records the run once: the snapshot
    /// moves under the same gate.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task RescanRacingTheWatch_RecordsOnce()
    {
        scratch.Write("BC-TEST a");
        await using var integration = new RunHistoryIntegration(history, scratch.KeyPath);
        await integration.StartAsync(enable: true);

        scratch.Run("BC-TEST raced");
        await Task.WhenAll(integration.RescanAsync("tab"), integration.RescanAsync("tab"));
        await WaitUntilAsync(() => RunsAsync().Result.Count == 2);
        await Task.Delay(500, TestContext.Current.CancellationToken); // the watch's own rescan lands meanwhile
        Assert.Equal(1, integration.RecordedThisSession);
        Assert.Equal(1, (await RunsAsync()).Single(e => e.Preview == "BC-TEST raced").UseCount);
    }

    /// <summary>A command run from the panel moves to the top with a new run time (Windows' list is not written).</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task RecordRun_MovesTheCommandUp()
    {
        scratch.Write("BC-TEST b", "BC-TEST a");
        await using var integration = new RunHistoryIntegration(history, scratch.KeyPath);
        await integration.StartAsync(enable: true);
        var before = (await RunsAsync()).Single(e => e.Preview == "BC-TEST a");

        var entry = await integration.RecordRunAsync("BC-TEST a");
        Assert.Equal(before.Id, entry!.Id);
        Assert.True(entry.LastRunUtc > before.LastRunUtc);
        Assert.Equal("BC-TEST a", (await RunsAsync())[0].Preview);
        Assert.Equal(1, integration.RecordedThisSession);
        Assert.Equal(["BC-TEST b", "BC-TEST a"], RunMruReader.Read(scratch.KeyPath).Commands);
    }

    /// <summary>Polls a condition every 25 ms for up to 10 s.</summary>
    /// <param name="condition">The condition.</param>
    /// <returns>A task completing once it holds.</returns>
    /// <exception cref="TimeoutException">It did not hold within 10 s.</exception>
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

    /// <summary>The Run tab's entries, newest first.</summary>
    /// <returns>The entries.</returns>
    private Task<IReadOnlyList<ClipEntry>> RunsAsync() =>
        history.QueryAsync(new ClipQuery { Filter = ClipFilter.Run, PinnedFirst = false, Limit = 1000 });
}

/// <summary>
/// <see cref="RunCommandLauncher"/>: how commands are split like the Run dialog does (URIs, programs, variables,
/// paths with spaces, quotes, folders, relative paths, drives, the last resort) and real hidden launches. Nothing
/// visible is ever started.
/// </summary>
public sealed class RunCommandLauncherTests : IDisposable
{
    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly string temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", Guid.NewGuid().ToString("N"), "BC-TEST dir with spaces")).FullName;

    /// <summary>Deletes the temp tree.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(temp)!, recursive: true);
        }
        catch (IOException)
        {
            // Best effort (a hidden cmd may still hold its output file for a moment).
        }
    }

    /// <summary>URIs are opened as a whole, in the profile folder.</summary>
    /// <param name="uri">The command.</param>
    [Theory]
    [InlineData("https://example.com/BC-TEST?a=1 b")]
    [InlineData("shell:startup")]
    [InlineData("ms-settings:display")]
    public void Parse_Uri_IsOpenedWhole(string uri)
    {
        var invocation = RunCommandLauncher.Parse(uri);
        Assert.Equal(uri, invocation.File);
        Assert.Null(invocation.Arguments);
        Assert.Equal(Profile, invocation.WorkingDirectory);
    }

    /// <summary>A program command is resolved by Windows and starts in the profile folder.</summary>
    [Fact]
    public void Parse_ProgramCommand()
    {
        var invocation = RunCommandLauncher.Parse("cmd /c echo BC-TEST");
        Assert.Equal("cmd.exe", Path.GetFileName(invocation.File), ignoreCase: true);
        Assert.True(File.Exists(invocation.File));
        Assert.Equal("/c echo BC-TEST", invocation.Arguments);
        Assert.Equal(Profile, invocation.WorkingDirectory);
    }

    /// <summary>Variables are expanded, and a program named with a path starts in its own folder.</summary>
    [Fact]
    public void Parse_ExpandsVariables_AndUsesTheProgramsFolder()
    {
        var invocation = RunCommandLauncher.Parse(@"%WINDIR%\System32\cmd.exe /c echo BC-TEST");
        Assert.True(File.Exists(invocation.File));
        Assert.Equal("/c echo BC-TEST", invocation.Arguments);
        Assert.Equal(Path.GetDirectoryName(invocation.File), invocation.WorkingDirectory, ignoreCase: true);
    }

    /// <summary>Unquoted and quoted paths with spaces, and folders, are found as a whole.</summary>
    [Fact]
    public void Parse_PathsWithSpaces_QuotesAndFolders()
    {
        var file = Path.Combine(temp, "BC-TEST tool.txt");
        File.WriteAllText(file, "BC-TEST");

        var unquoted = RunCommandLauncher.Parse($"{file} --flag one");
        Assert.Equal((file, "--flag one", temp), (unquoted.File, unquoted.Arguments, unquoted.WorkingDirectory));

        var quoted = RunCommandLauncher.Parse($"\"{file}\" arg");
        Assert.Equal((file, "arg"), (quoted.File, quoted.Arguments));

        var folder = RunCommandLauncher.Parse(temp);
        Assert.Equal((temp, (string?)null, temp), (folder.File, folder.Arguments, folder.WorkingDirectory));
    }

    /// <summary>Relative locations are relative to the profile folder; a bare drive means its root.</summary>
    [Fact]
    public void Parse_RelativeAndDrive()
    {
        Assert.Equal(Profile, RunCommandLauncher.Parse(".").File);
        Assert.Equal(Path.GetDirectoryName(Profile), RunCommandLauncher.Parse("..").File);
        var drive = Path.GetPathRoot(Environment.SystemDirectory)!; // "C:\"
        Assert.Equal(drive, RunCommandLauncher.Parse(drive.TrimEnd('\\')).File);
    }

    /// <summary>What nothing resolves is split at the first space, for Windows' own search (PATH, PATHEXT).</summary>
    [Fact]
    public void Parse_LastResort_SplitsAtTheFirstSpace()
    {
        var name = $"bc-test-no-such-program-{Guid.NewGuid():N}";
        var invocation = RunCommandLauncher.Parse($"{name} some args");
        Assert.Equal((name, "some args", Profile), (invocation.File, invocation.Arguments, invocation.WorkingDirectory));
    }

    /// <summary>Blank and multi-line commands are refused (Win+R takes one line).</summary>
    [Fact]
    public void Parse_RefusesBlankAndMultiLine()
    {
        Assert.Throws<ArgumentException>(() => RunCommandLauncher.Parse("  "));
        Assert.Throws<ArgumentException>(() => RunCommandLauncher.Parse("cmd\r\ncalc"));
        Assert.Throws<ArgumentException>(() => RunCommandLauncher.Parse("cmd\ncalc"));
    }

    /// <summary>
    /// A real launch (hidden <c>cmd</c> writing a BC-TEST file) starts; a missing program fails with "cannot find"
    /// instead of a Windows message box.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task RunQuietly_StartsOrReportsWhyNot()
    {
        var output = Path.Combine(temp, "out.txt");
        var started = await RunCommandLauncher.RunQuietlyAsync($"cmd /c echo BC-TEST>\"{output}\"");
        Assert.Equal(RunCommandOutcome.Started, started.Outcome);
        await RunHistoryIntegrationTests.WaitUntilAsync(() => File.Exists(output) && TryRead(output).Contains("BC-TEST", StringComparison.Ordinal));

        var missing = await RunCommandLauncher.RunQuietlyAsync($"bc-test-no-such-program-{Guid.NewGuid():N}");
        Assert.Equal(RunCommandOutcome.Failed, missing.Outcome);
        Assert.Equal(2, missing.Win32Error);
        Assert.Equal("Windows cannot find it.", missing.Describe());
        Assert.Equal("Cancelled at the administrator prompt.", new RunCommandResult(RunCommandOutcome.Cancelled, 1223).Describe());
    }

    /// <summary>Reads a file that another process may still be writing.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Its text, or empty while it is locked.</returns>
    private static string TryRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}
