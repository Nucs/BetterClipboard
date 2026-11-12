using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Windows.Diagnostics;
using Microsoft.UI.Dispatching;

namespace BetterClipboard.App;

/// <summary>
/// Giving memory back (CLAUDE.md §2.45): after Settings closes, after the panel is put away, after a list refresh while it
/// is hidden, and once the start's work is done, the controller asks for a reclaim; it runs once things have been quiet
/// for a while (<see cref="MemoryReclaimPlanner"/>), never while the panel or Settings is on screen, on a pool thread
/// (<see cref="ProcessMemory.Reclaim"/>), and trims the working set every time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> Measured 2026-11-12 on 0.2.10 (a Windows 11 test desktop, a seeded history of 3,300 entries and 15,000
/// prompts): what the panel, a thumbnail or the image peek leaves behind is mostly native memory owned by WinUI, given back
/// only when the .NET wrappers that hold it are collected, and an idle tray app may not collect for hours. On the owner's
/// PC the installed app held 267 MB of private memory after two days, with 9 MB of live .NET objects.
/// </para>
/// <para>
/// <b>The trim</b> (the owner's choice, 2026-11-12: "After every use", over trimming only after a long idle or never): every
/// reclaim also hands the pages the app is not using right now to Windows, so Task Manager's "Memory" shows a few megabytes
/// while BetterClipboard waits (5 MB in the test desktop, against 25 to 35 without). The price, measured there: a summon
/// after a trim waits for its pages to come back from RAM, 41 ms instead of 28 (median of five); after Windows gave that
/// RAM to another program, they come back from the page file, slower — which Windows' own trimming under memory pressure
/// costs anyway.
/// </para>
/// <para>
/// <b>Cost.</b> One full, compacting collection twice (some tens of milliseconds at these heap sizes), after use, so never
/// between a key and the panel.
/// </para>
/// </remarks>
public sealed partial class AppController
{
    /// <summary>How long after Settings closed: its window and XAML tree are torn down by then.</summary>
    private static readonly TimeSpan SettingsClosedReclaimDelay = TimeSpan.FromSeconds(3);

    /// <summary>How long after the panel was put away: a few more summons in a row end before anything is collected.</summary>
    private static readonly TimeSpan PanelReclaimDelay = TimeSpan.FromSeconds(30);

    /// <summary>How long after the hidden panel refreshed its list (a copy came in): copies come in bursts.</summary>
    private static readonly TimeSpan RefreshReclaimDelay = TimeSpan.FromSeconds(60);

    /// <summary>How long after the start: the imports, the warm-up and the first catch-ups are done by then.</summary>
    private static readonly TimeSpan StartupReclaimDelay = TimeSpan.FromSeconds(90);

    /// <summary>How long a due reclaim waits again while the panel or Settings is on screen.</summary>
    private static readonly TimeSpan BusyRetry = TimeSpan.FromSeconds(10);

    /// <summary>A reclaim that gave back less than this is not logged (most after a summon give back a few megabytes).</summary>
    private const long ReclaimLogThresholdBytes = 8L * 1024 * 1024;

    /// <summary>When the next reclaim runs, and why; UI thread only.</summary>
    private readonly MemoryReclaimPlanner memoryPlanner = new();

    /// <summary>Fires when the pending reclaim is due; created on first use, UI thread only.</summary>
    private DispatcherQueueTimer? memoryTimer;

    /// <summary>Whether a reclaim runs on the pool right now (a second one would only repeat it).</summary>
    private bool reclaiming;

    /// <summary>
    /// The panel was put away (every way it goes, <c>ClipboardFlyout.Conceal</c>): give back what the use left behind — the
    /// cards of a search, decoded images, the image peek — once no summon follows for <see cref="PanelReclaimDelay"/>.
    /// UI thread only.
    /// </summary>
    internal void NotePanelPutAway() => RequestMemoryReclaim("using the panel", PanelReclaimDelay);

    /// <summary>
    /// The hidden panel reloaded its list because copies came in: give back the cards it replaced once the copies stop
    /// (<see cref="RefreshReclaimDelay"/>). UI thread only.
    /// </summary>
    internal void NoteConcealedListRefreshed() => RequestMemoryReclaim("a list refresh", RefreshReclaimDelay);

    /// <summary>
    /// Asks for memory to be given back once things have been quiet for <paramref name="delay"/> (see the class remarks).
    /// UI thread only; cheap, so callers may ask after every use.
    /// </summary>
    /// <param name="reason">Why, for the log ("Settings closed"); content-free.</param>
    /// <param name="delay">How long to stay quiet first.</param>
    internal void RequestMemoryReclaim(string reason, TimeSpan delay)
    {
        if (exiting)
        {
            return;
        }

        memoryPlanner.Request(DateTimeOffset.UtcNow, delay, reason);
        ArmMemoryTimer();
    }

    /// <summary>Starts the timer for the pending reclaim (or again, for its new due time).</summary>
    private void ArmMemoryTimer()
    {
        if (memoryPlanner.DueAt is not { } due)
        {
            return;
        }

        if (memoryTimer is null)
        {
            memoryTimer = ui.CreateTimer();
            memoryTimer.IsRepeating = false;
            memoryTimer.Tick += MemoryTimer_Tick;
        }

        var wait = due - DateTimeOffset.UtcNow;
        memoryTimer.Stop();
        memoryTimer.Interval = wait > TimeSpan.FromMilliseconds(50) ? wait : TimeSpan.FromMilliseconds(50);
        memoryTimer.Start();
    }

    /// <summary>The pending reclaim may be due: run it on the pool, or wait while the panel or Settings shows.</summary>
    /// <param name="sender">The timer.</param>
    /// <param name="args">Nothing.</param>
    private void MemoryTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (exiting || reclaiming)
        {
            return;
        }

        // On screen: a collection pauses every thread, and the user is using the window right now.
        bool busy = flyout?.IsOpen == true || settingsWindow is not null;
        if (!memoryPlanner.TryTake(DateTimeOffset.UtcNow, busy, out var reasons))
        {
            if (busy && memoryPlanner.DueAt is not null)
            {
                // Looked at again shortly: the window may be put away without a request of its own (Settings closing
                // asks for one, but a summon that ends in a paste is put away like any other).
                sender.Interval = BusyRetry;
                sender.Start();
            }
            else
            {
                // Woke before the earliest reason was due (its due time moved): wait for it.
                ArmMemoryTimer();
            }

            return;
        }

        reclaiming = true;
        _ = ReclaimMemoryAsync(string.Join(", ", reasons));
    }

    /// <summary>
    /// Gives memory back on a pool thread and logs what it gave when that was worth a line. Never throws (fire-and-forget
    /// safe): a failed reclaim only means the memory comes back later, at the runtime's own next collection.
    /// </summary>
    /// <param name="reasons">Why, for the log.</param>
    /// <returns>A task completing when done.</returns>
    private async Task ReclaimMemoryAsync(string reasons)
    {
        try
        {
            var (before, after) = await Task.Run(() =>
            {
                var measured = ProcessMemory.Read();
                ProcessMemory.Reclaim(trimWorkingSet: true);
                return (measured, ProcessMemory.Read());
            }).ConfigureAwait(true);

            if (before.PrivateBytes - after.PrivateBytes >= ReclaimLogThresholdBytes)
            {
                AppLog.Info($"Memory after {reasons}: {MemoryUsage.DescribeRelease(before, after)}; now {after.Describe()}.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Giving memory back failed: {ex.Message}");
        }
        finally
        {
            // Reasons that were not due yet keep waiting for their own time.
            reclaiming = false;
            ArmMemoryTimer();
        }
    }
}
