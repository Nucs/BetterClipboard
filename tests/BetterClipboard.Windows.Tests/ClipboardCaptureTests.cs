using System.Collections.Concurrent;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Windows.Clipboard;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// The capture guarantees, proven against the real Win32 clipboard inside a private window station (so
/// they run by default without touching the user's clipboard): copies a human or a script makes in quick
/// succession are all captured, a zero-delay burst is fully accounted for (nothing disappears silently),
/// the watchdog heals a deaf listener, our own writes are not re-captured, and delayed rendering works.
/// </summary>
[Collection(IsolatedClipboardCollection.Name)]
public sealed class ClipboardCaptureTests : IDisposable
{
    private readonly IsolatedClipboard isolation = new();
    private readonly ConcurrentQueue<string> captured = new();

    /// <summary>Restores the process window station.</summary>
    public void Dispose() => isolation.Dispose();

    /// <summary>
    /// 25 copies 20 ms apart — three times faster than the old 60 ms debounce, which merged them into
    /// one — are all captured, in order, with nothing superseded.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task QuickSuccessiveCopies_AreAllCaptured()
    {
        using var monitor = StartMonitor();
        using var producer = new ClipboardProducer(isolation);
        var copies = Enumerable.Range(1, 25).Select(i => $"quick copy {i}").ToArray();

        await producer.WriteBurstAsync(copies, TimeSpan.FromMilliseconds(20));
        await WaitUntil(monitor, () => captured.Count >= copies.Length);

        Assert.Equal(copies, captured.ToArray());
        Assert.Equal(0, monitor.Statistics.Superseded);
        Assert.Equal(copies.Length, monitor.Statistics.Notifications);
    }

    /// <summary>
    /// 40 copies with no pause at all (faster than any consumer can read) are fully accounted for: every
    /// notification is either read or reported as superseded, what was read is an in-order subsequence,
    /// and the final state is never lost.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task ZeroDelayBurst_IsFullyAccountedFor()
    {
        using var monitor = StartMonitor();
        using var producer = new ClipboardProducer(isolation);
        var burst = Enumerable.Range(1, 40).Select(i => $"burst {i}").ToArray();

        await producer.WriteBurstAsync(burst);
        await WaitUntil(monitor, () => monitor.Statistics.Notifications == burst.Length && Balanced(monitor.Statistics));

        var stats = monitor.Statistics;
        TestContext.Current.TestOutputHelper?.WriteLine($"zero-delay burst of {burst.Length}: {stats}");
        Assert.Equal(burst[^1], captured.Last());
        Assert.Equal(stats.Captured, captured.Count);
        Assert.Equal(burst.Length, stats.Read + stats.Superseded);

        // Captured texts appear in the order they were copied (a subsequence of the burst).
        var indexes = captured.Select(text => Array.IndexOf(burst, text)).ToArray();
        Assert.DoesNotContain(-1, indexes);
        Assert.Equal(indexes.Order(), indexes);
        Assert.Equal(indexes.Distinct().Count(), indexes.Length);
    }

    /// <summary>
    /// Measurement, not a default test (run with <c>-explicit only</c>): how many of 100 programmatic copies
    /// are captured at each gap between copies, three rounds per gap. Prints the table behind CLAUDE.md
    /// §1.9. It asserts only the invariants that hold at every speed — every notification is either read or
    /// counted as overwritten, and the final copy is always captured — because the capture rate itself
    /// depends on the machine and its load.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact(Explicit = true)]
    public async Task CaptureRate_BySpeedOfCopying()
    {
        double[] gapsMs = [0, 0.05, 0.1, 0.25, 0.5, 0.75, 1, 2, 5];
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine("gap (ms) | captured of 100, per round | overwritten before read | locked out");
        foreach (var gapMs in gapsMs)
        {
            var rounds = new List<ClipboardMonitorStatistics>();
            for (int round = 0; round < 3; round++)
            {
                captured.Clear();
                using var monitor = StartMonitor();
                using var producer = new ClipboardProducer(isolation);
                var copies = Enumerable.Range(1, 100).Select(i => $"rate {gapMs} {round} {i}").ToArray();

                await producer.WriteBurstAsync(copies, TimeSpan.FromMilliseconds(gapMs));
                await WaitUntil(monitor, () => monitor.Statistics.Notifications == copies.Length && Balanced(monitor.Statistics));

                var stats = monitor.Statistics;
                Assert.Equal(copies.Length, stats.Read + stats.Superseded + stats.LockedOut);
                Assert.Equal(copies[^1], captured.Last());
                rounds.Add(stats);
            }

            output?.WriteLine($"{gapMs,8} | {string.Join(", ", rounds.Select(r => r.Captured)),-26} | {string.Join(", ", rounds.Select(r => r.Superseded)),-22} | {string.Join(", ", rounds.Select(r => r.LockedOut))}");
        }
    }

    /// <summary>
    /// When the listener silently stops delivering, the watchdog finds the change, captures it and
    /// re-registers — the next copy arrives through a normal notification again.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Watchdog_RecoversAndReRegistersADeafListener()
    {
        using var monitor = StartMonitor(watchdogMilliseconds: 100);
        using var producer = new ClipboardProducer(isolation);
        await monitor.SimulateListenerLossAsync();

        await producer.WriteBurstAsync(["copied while the listener was deaf"]);
        await WaitUntil(monitor, () => captured.Contains("copied while the listener was deaf"));
        Assert.Equal(1, monitor.Statistics.Recovered);
        Assert.Equal(0, monitor.Statistics.Notifications);

        await producer.WriteBurstAsync(["heard normally again"]);
        await WaitUntil(monitor, () => captured.Contains("heard normally again"));
        Assert.Equal(1, monitor.Statistics.Notifications);
        Assert.Equal(1, monitor.Statistics.Recovered);
    }

    /// <summary>Pasting from history (our own write) is not re-captured; the next foreign copy is.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task OwnWrite_IsIgnoredButForeignCopyIsCaptured()
    {
        using var monitor = StartMonitor();
        using var producer = new ClipboardProducer(isolation);

        await monitor.WriteAsync([new ClipFormatData(ClipFormatNames.UnicodeText, UnicodeTextCodec.Encode("pasted from history"))]);
        await WaitUntil(monitor, () => monitor.Statistics.SelfWrites == 1);
        await producer.WriteBurstAsync(["copied by another app"]);
        await WaitUntil(monitor, () => captured.Count == 1);

        Assert.Equal(["copied by another app"], captured.ToArray());
    }

    /// <summary>
    /// A delay-rendered copy (data promised, produced on demand) is rendered by the producer while we read
    /// it immediately after the notification.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task DelayedRendering_IsRenderedOnDemandAndCaptured()
    {
        using var monitor = StartMonitor();
        using var producer = new ClipboardProducer(isolation);

        await producer.PromiseAsync("rendered only when asked");
        await WaitUntil(monitor, () => captured.Count == 1);

        Assert.Equal("rendered only when asked", captured.Single());
        Assert.Equal(1, producer.RendersServed);
    }

    /// <summary>Starts a monitor on the isolated desktop that records captured text.</summary>
    /// <param name="watchdogMilliseconds">Watchdog period.</param>
    /// <returns>The running monitor.</returns>
    private ClipboardMonitor StartMonitor(uint watchdogMilliseconds = 2000)
    {
        var monitor = new ClipboardMonitor(() => new CaptureOptions(), new SourceAppResolver())
        {
            IsolatedDesktop = isolation.Desktop,
            WatchdogMilliseconds = watchdogMilliseconds,
        };
        monitor.Captured += (_, capture) => captured.Enqueue(UnicodeTextCodec.Decode(capture.Find(ClipFormatNames.UnicodeText)!.Data));
        monitor.Start();
        return monitor;
    }

    /// <summary>The accounting identity from <see cref="ClipboardMonitorStatistics"/> (true once nothing is pending).</summary>
    /// <param name="s">Statistics.</param>
    /// <returns>Whether every notification is accounted for.</returns>
    private static bool Balanced(ClipboardMonitorStatistics s) =>
        s.Notifications == s.Read + s.SelfWrites + s.LockedOut - s.Recovered + s.Superseded;

    /// <summary>Polls <paramref name="condition"/> every 10 ms for up to 5 s.</summary>
    /// <param name="monitor">Monitor (its statistics go into the failure message).</param>
    /// <param name="condition">Condition to await.</param>
    /// <returns>A task completing when the condition holds.</returns>
    private async Task WaitUntil(ClipboardMonitor monitor, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out. Captured {captured.Count}; {monitor.Statistics}.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
