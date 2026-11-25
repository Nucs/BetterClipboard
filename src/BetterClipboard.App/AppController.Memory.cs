using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Settings;
using BetterClipboard.Windows.Diagnostics;
using Microsoft.UI.Dispatching;

namespace BetterClipboard.App;

/// <summary>
/// Giving memory back (CLAUDE.md §2.45): after Settings closes, after the panel is put away, after a list refresh while it
/// is hidden, and once the start's work is done, the controller asks for a reclaim; it runs once things have been quiet
/// for a while (<see cref="MemoryReclaimPlanner"/>), never while the panel or Settings is on screen, on a pool thread
/// (<see cref="ProcessMemory.Reclaim"/>). It frees what is no longer used, and keeps the working set unless Settings › App ›
/// Keep the panel in memory says the panel has gone unused long enough (<see cref="PanelMemory"/>, CLAUDE.md §2.69): by
/// default the pages the panel uses stay in memory for the next Win+V.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> Measured 2026-11-12 on 0.2.10 (a Windows 11 test desktop, a seeded history of 3,300 entries and 15,000
/// prompts): what the panel, a thumbnail or the image peek leaves behind is mostly native memory owned by WinUI, given back
/// only when the .NET wrappers that hold it are collected, and an idle tray app may not collect for hours. On the owner's
/// PC the installed app held 267 MB of private memory after two days, with 9 MB of live .NET objects.
/// </para>
/// <para>
/// <b>No trim of the working set by default</b> (the owner's choice, 2026-11-15: "Keep the panel ready", over handing pages
/// back after 10 minutes unused or after every use; CLAUDE.md §2.53). From 2026-11-12 every reclaim also handed every page
/// to Windows (the owner's choice then: "After every use"), so Task Manager's "Memory" read 0.2 MB while BetterClipboard
/// waited. On the owner's PC, short of memory (138 GB committed on 64 GB), Windows reused that RAM, and a Win+V after a pause
/// waited for its pages to come back from disk (382 and 538 ms at worst under 0.2.11). A test desktop counted 6,000 to
/// 10,000 page faults per summon after a trim, against 75 to 570 without. Now "Memory" reads about 40 to 50 MB at rest on
/// the owner's PC; Windows can still trim the app by itself when it needs the RAM, oldest pages first, and the summon's log
/// line then shows thousands of page faults.
/// </para>
/// <para>
/// <b>The trim as a choice</b> (issue #14, 2026-11-24: "BetterClipboard stays around 40-50 MB … With the previous version, it
/// would drop to around 4-6 MB"; the owner's slider, 2026-11-25). With a keep time, a reclaim also trims once the panel has
/// gone unused that long (<see cref="PanelMemory.ShouldTrim"/>): the one after its use (asked for sooner when the keep time
/// is shorter than its usual 30 seconds, <see cref="PanelMemory.ReclaimDelayAfterUse"/>), one at the keep time's end
/// (<see cref="keepTimer"/>), and every later one while the panel stays unused (a copy refreshes the hidden list, which
/// touches its pages again). Committed memory is the same with and without (the owner's PC: a median of 145 MB with the trim
/// and 144 MB without, over 98 and 66 reclaims): a trim only takes pages out of the working set.
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
    /// When the panel was last put away, or the process started (the warm-up builds and draws the panel, and nothing has used
    /// it yet): what Settings › App › Keep the panel in memory counts from (<see cref="PanelMemory"/>). UI thread only.
    /// </summary>
    private DateTimeOffset lastPanelUseUtc = DateTimeOffset.UtcNow;

    /// <summary>
    /// Fires when the panel has gone unused for the keep time (<see cref="PanelMemory.TrimDueAt"/>), to ask for the reclaim
    /// that trims; only for keep times longer than <see cref="PanelReclaimDelay"/> (a shorter one is that reclaim's own delay).
    /// Created on first use, UI thread only.
    /// </summary>
    private DispatcherQueueTimer? keepTimer;

    /// <summary>
    /// Whether this session logged a trim already: the first one says why the pages went back to Windows; later ones show
    /// only in the reclaims' own lines ("memory 0.3 MB"), or the log would gain a line at every use with "Never keep in
    /// memory". UI thread only.
    /// </summary>
    private bool trimLogged;

    /// <summary>The keep time the timer was last armed for, to re-arm it only when Settings changed it. UI thread only.</summary>
    private int? armedKeepSeconds;

    /// <summary>
    /// Settings › App › Keep the panel in memory as it stands (<see cref="AppSettings.KeepPanelInMemorySeconds"/>), or "never
    /// release" (the default) before the settings are loaded.
    /// </summary>
    private int? KeepPanelInMemorySeconds => settings?.Current.KeepPanelInMemorySeconds;

    /// <summary>
    /// The panel was put away (every way it goes, <c>ClipboardFlyout.FinishPuttingAway</c>): give back what the use left behind — the
    /// cards of a search, decoded images, the image peek — once no summon follows for <see cref="PanelReclaimDelay"/>, or sooner
    /// when Settings keeps the panel in memory for less (<see cref="PanelMemory.ReclaimDelayAfterUse"/>); and start counting the
    /// keep time from now. UI thread only.
    /// </summary>
    internal void NotePanelPutAway()
    {
        lastPanelUseUtc = DateTimeOffset.UtcNow;
        RequestMemoryReclaim("using the panel", PanelMemory.ReclaimDelayAfterUse(KeepPanelInMemorySeconds, PanelReclaimDelay));
        ArmKeepTimer();
    }

    /// <summary>
    /// Settings changed: when the keep time is another one, the timer is set for it (counted from the last use as before), and
    /// a keep time that has already passed asks for its trim at once. Cheap and idempotent: an unchanged keep time does
    /// nothing. UI thread only.
    /// </summary>
    internal void ApplyPanelMemorySetting()
    {
        var keep = KeepPanelInMemorySeconds;
        if (keep == armedKeepSeconds)
        {
            return;
        }

        AppLog.Info($"Keep the panel in memory: {PanelMemory.DescribeForLog(keep)}.");
        ArmKeepTimer();
        if (PanelMemory.ShouldTrim(keep, lastPanelUseUtc, DateTimeOffset.UtcNow))
        {
            RequestMemoryReclaim("a shorter keep time for the panel's memory", TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Sets the keep timer for the end of the keep time after the last use, or stops it when the keep time is short enough to
    /// be the reclaim's own delay (<see cref="NotePanelPutAway"/>), or when the setting never releases. UI thread only.
    /// </summary>
    private void ArmKeepTimer()
    {
        var keep = KeepPanelInMemorySeconds;
        armedKeepSeconds = keep;
        if (exiting || PanelMemory.TrimDueAt(keep, lastPanelUseUtc) is not { } due ||
            TimeSpan.FromSeconds(keep.GetValueOrDefault()) <= PanelReclaimDelay)
        {
            keepTimer?.Stop();
            return;
        }

        if (keepTimer is null)
        {
            keepTimer = ui.CreateTimer();
            keepTimer.IsRepeating = false;
            keepTimer.Tick += KeepTimer_Tick;
        }

        var wait = due - DateTimeOffset.UtcNow;
        keepTimer.Stop();
        keepTimer.Interval = wait > TimeSpan.FromMilliseconds(50) ? wait : TimeSpan.FromMilliseconds(50);
        keepTimer.Start();
    }

    /// <summary>
    /// The keep time after the last use has run out: ask for the reclaim that hands the panel's pages to Windows. A use since
    /// then (the timer is re-armed at every put-away) or a changed setting re-checks instead of trimming.
    /// </summary>
    /// <param name="sender">The timer.</param>
    /// <param name="args">Nothing.</param>
    private void KeepTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (exiting)
        {
            return;
        }

        var keep = KeepPanelInMemorySeconds;
        if (!PanelMemory.ShouldTrim(keep, lastPanelUseUtc, DateTimeOffset.UtcNow))
        {
            ArmKeepTimer();
            return;
        }

        RequestMemoryReclaim($"the panel unused for {PanelMemory.DescribeForLog(keep)}", TimeSpan.Zero);
    }

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

        // Decided here, on the UI thread that owns the clock of the last use: the panel is not on screen (busy above), so
        // only the time since it was put away decides (PanelMemory.ShouldTrim; never, by default).
        var keep = KeepPanelInMemorySeconds;
        bool trim = PanelMemory.ShouldTrim(keep, lastPanelUseUtc, DateTimeOffset.UtcNow);
        _ = ReclaimMemoryAsync(string.Join(", ", reasons), trim, keep);
    }

    /// <summary>
    /// Gives memory back on a pool thread and logs what it gave when that was worth a line. Never throws (fire-and-forget
    /// safe): a failed reclaim only means the memory comes back later, at the runtime's own next collection.
    /// </summary>
    /// <param name="reasons">Why, for the log.</param>
    /// <param name="trim">
    /// Also hand the working set to Windows (Settings › App › Keep the panel in memory said the panel has gone unused long
    /// enough); otherwise the pages the panel uses stay in memory for the next Win+V.
    /// </param>
    /// <param name="keep">The keep time the decision was made with, for the log.</param>
    /// <returns>A task completing when done.</returns>
    private async Task ReclaimMemoryAsync(string reasons, bool trim, int? keep)
    {
        try
        {
            var (before, after) = await Task.Run(() =>
            {
                var measured = ProcessMemory.Read();

                // The collections run before the trim, so the pages they touch (the heap they compact) go back too.
                ProcessMemory.Reclaim(trimWorkingSet: trim);
                return (measured, ProcessMemory.Read());
            }).ConfigureAwait(true);

            if (before.PrivateBytes - after.PrivateBytes >= ReclaimLogThresholdBytes)
            {
                AppLog.Info($"Memory after {reasons}: {MemoryUsage.DescribeRelease(before, after)}; now {after.Describe()}.");
            }

            // The first trim of a session says why the panel's pages left the working set: a slow Win+V after it (the summon's
            // line counts thousands of page faults) is explained by this line. Content-free like every memory line.
            if (trim && !trimLogged)
            {
                trimLogged = true;
                AppLog.Info($"Gave the panel's memory back to Windows ({reasons}; Settings › App › Keep the panel in memory: " +
                    $"{PanelMemory.DescribeForLog(keep)}): {MemoryUsage.DescribeWorkingSetChange(before, after)}. Later trims " +
                    "show only in the reclaims' own lines.");
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
