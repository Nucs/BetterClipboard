using BetterClipboard.Core.Services;
using BetterClipboard.Core.Shells;
using BetterClipboard.Core.Storage;
using BetterClipboard.Windows.Imaging;
using BetterClipboard.Windows.Integrations;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// The Cmd tab's kept commands under Settings' limit (CLAUDE.md §2.62): no limit by default, and a lowered number applies to
/// the stored list at once (<see cref="CmdHistorySource.ApplyLimitAsync"/>) — the newest commands stay, the rest is deleted,
/// and nothing comes back when the limit is raised again. No Command Prompt window is read: the list is set in the store.
/// </summary>
public sealed class CmdHistoryLimitTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);
    private readonly string temp = Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", Guid.NewGuid().ToString("N"));
    private ClipHistoryService history = null!;

    /// <summary>A history over a temp store.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(temp);
        var store = new ClipStore(Path.Combine(temp, "history.db"));
        store.Initialize();
        history = new ClipHistoryService(store, new WinRtImageAnalyzer(), () => new CaptureRules { Retention = RetentionPolicy.Unlimited });
        history.Start();
        return ValueTask.CompletedTask;
    }

    /// <summary>Stops the history and deletes the temp tree.</summary>
    /// <returns>A task.</returns>
    public async ValueTask DisposeAsync()
    {
        await history.DisposeAsync();
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
    /// 1,200 kept commands (above the 1,000 kept until 2026-11-21) stay under no limit; a limit of 3 keeps the 3 newest in the
    /// store and in the source's count, and tells Settings once; raising it again brings nothing back.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task ApplyLimit_KeepsTheNewest_AndOnlyWhenLowered()
    {
        var kept = Enumerable.Range(0, 1200).Select(i => new KeptCommand($"dir BC-TEST {i}", 1, Now.AddMinutes(-i))).ToList();
        await history.SetStateValueAsync(CmdHistory.KeptStateName, CmdHistory.FormatKept(kept));

        int limit = 0;
        await using var source = new CmdHistorySource(history, () => false, @"C:\BC-TEST\none.exe") { KeptLimit = () => limit };
        int events = 0;
        source.StatusChanged += (_, _) => Interlocked.Increment(ref events);

        await source.ApplyLimitAsync();
        Assert.Equal(1200, source.KeptCount);
        Assert.Equal(1200, CmdHistory.ParseKept(await history.GetStateValueAsync(CmdHistory.KeptStateName)).Count);
        Assert.Equal(0, events);

        limit = 3;
        await source.ApplyLimitAsync();
        Assert.Equal(3, source.KeptCount);
        Assert.Equal(
            ["dir BC-TEST 0", "dir BC-TEST 1", "dir BC-TEST 2"],
            CmdHistory.ParseKept(await history.GetStateValueAsync(CmdHistory.KeptStateName)).Select(c => c.Text));
        Assert.Equal(1, events);

        limit = 0;
        await source.ApplyLimitAsync();
        Assert.Equal(3, source.KeptCount);
        Assert.Equal(1, events);
    }
}
