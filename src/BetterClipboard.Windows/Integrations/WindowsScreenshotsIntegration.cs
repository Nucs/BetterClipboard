using System.Globalization;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Services;

namespace BetterClipboard.Windows.Integrations;

/// <summary>
/// Runs the Windows screenshots integration for the app (the panel's Snipping tab; CLAUDE.md §2.18): watches the
/// Screenshots folder while the setting is on, catches up on screenshots saved while BetterClipboard was not running, and
/// follows the folder and Snipping Tool when they change.
/// </summary>
/// <remarks>
/// <para>
/// <b>Catch-up marker.</b> <see cref="LastSeenStateName"/> (kept in the encrypted store) is the newest write time of a
/// screenshot already dealt with. At startup, files written after it are imported, at most <see cref="MaxCatchUpFiles"/> of
/// the newest. The first activation sets it to "now", so turning the feature on never backfills the folder's archive.
/// Turning it off clears it, and turning it back on starts fresh: screenshots taken while it was off are not imported
/// afterwards. Exiting the app does not count as off. The same life cycle as <see cref="ShareXIntegration"/>, whose
/// structure this follows (kept separate: the ShareX one also follows ShareX's own settings files).
/// </para>
/// <para>
/// <b>Re-location.</b> The locator runs again when Settings opens or the panel opens while nothing is watched
/// (<see cref="RefreshAsync"/>), and every <see cref="RefreshInterval"/>. That notices a Screenshots folder created by the
/// first screenshot, a redirected Pictures folder, and Snipping Tool being installed or reconfigured. When the watched
/// folder changes, a catch-up from the marker picks up whatever was saved there meanwhile.
/// </para>
/// <para>
/// <b>Threads.</b> Every public member may be called from any thread; state changes are serialized by one gate.
/// <see cref="StatusChanged"/> is raised on a pool thread.
/// </para>
/// </remarks>
public sealed class WindowsScreenshotsIntegration : IAsyncDisposable
{
    /// <summary>Store state name of the catch-up marker (ISO 8601 round-trip UTC time; empty = none).</summary>
    public const string LastSeenStateName = "screenshots.last_seen_utc";

    /// <summary>Most screenshots one catch-up imports (the newest ones), so a long absence cannot flood the history.</summary>
    public const int MaxCatchUpFiles = 100;

    /// <summary>How often the folder and Snipping Tool are located again in the background.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private readonly ClipHistoryService history;
    private readonly Func<long> maxItemBytes;
    private readonly Func<WindowsScreenshotsLocation> locate;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Lock markerGate = new();
    private readonly CancellationTokenSource shutdown = new();
    private volatile WindowsScreenshotsLocation location = WindowsScreenshotsLocation.None;
    private WindowsScreenshotWatcher? watcher;
    private Timer? periodicRefresh;
    private bool enabled;
    private bool started;
    private bool disposed;
    private DateTimeOffset lastSeen;
    private int importedThisSession;

    /// <summary>Creates the integration; nothing happens until <see cref="StartAsync"/>.</summary>
    /// <param name="history">Where screenshots are stored and the marker is kept.</param>
    /// <param name="maxItemBytes">Current largest-item limit in bytes (files above it are skipped unread).</param>
    /// <param name="locate">Finds the folder and Snipping Tool; defaults to <see cref="WindowsScreenshotsLocator.Locate"/> (tests pass fakes).</param>
    /// <param name="time">Clock for the first-activation marker and the fresh-write rule; defaults to the system clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="history"/> or <paramref name="maxItemBytes"/> is <see langword="null"/>.</exception>
    public WindowsScreenshotsIntegration(ClipHistoryService history, Func<long> maxItemBytes, Func<WindowsScreenshotsLocation>? locate = null, TimeProvider? time = null)
    {
        this.history = history ?? throw new ArgumentNullException(nameof(history));
        this.maxItemBytes = maxItemBytes ?? throw new ArgumentNullException(nameof(maxItemBytes));
        this.locate = locate ?? WindowsScreenshotsLocator.Locate;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Raised on a pool thread whenever <see cref="Location"/>, <see cref="IsWatching"/> or the import count changed.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>The last snapshot of the facts (<see cref="WindowsScreenshotsLocation.None"/> until located).</summary>
    public WindowsScreenshotsLocation Location => location;

    /// <summary>Whether the Screenshots folder is being watched right now.</summary>
    public bool IsWatching => Volatile.Read(ref watcher)?.WatchedFolder is not null;

    /// <summary>The folder being watched, or <see langword="null"/>.</summary>
    public string? WatchedFolder => Volatile.Read(ref watcher)?.WatchedFolder;

    /// <summary>Screenshots stored since the app started (new items or refreshed duplicates).</summary>
    public int ImportedThisSession => Volatile.Read(ref importedThisSession);

    /// <summary>Locates the folder and applies <paramref name="enable"/> (watch + catch-up when on). Call once.</summary>
    /// <param name="enable">The <c>ImportWindowsScreenshots</c> setting.</param>
    /// <returns>A task completing once located and applied (the catch-up itself continues in the background).</returns>
    /// <exception cref="InvalidOperationException">Already started.</exception>
    /// <exception cref="ObjectDisposedException">Disposed.</exception>
    public async Task StartAsync(bool enable)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started)
            {
                throw new InvalidOperationException("The Windows screenshots integration was already started.");
            }

            started = true;
            enabled = enable;
            periodicRefresh = new Timer(_ => _ = RefreshAsync(), null, RefreshInterval, RefreshInterval);
            await ApplyAsync(relocate: true).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Turns the import on or off (the setting changed). Off stops watching and clears the catch-up marker.</summary>
    /// <param name="enable">New setting value.</param>
    /// <returns>A task completing once applied; a no-op when the value did not change or before <see cref="StartAsync"/>.</returns>
    public async Task SetEnabledAsync(bool enable)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed || !started || enable == enabled)
            {
                return;
            }

            enabled = enable;
            await ApplyAsync(relocate: enable).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Locates the folder and Snipping Tool again and follows any change. Failures are logged, never thrown.</summary>
    /// <returns>A task completing once refreshed.</returns>
    public async Task RefreshAsync()
    {
        try
        {
            await gate.WaitAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (!disposed && started)
            {
                await ApplyAsync(relocate: true).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Refreshing the Windows screenshots integration failed ({ex.GetType().Name}: {ex.Message}).");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Stops watching and the timer; the catch-up marker is kept (exit is not "off").</summary>
    /// <returns>A task completing once stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (periodicRefresh is not null)
            {
                await periodicRefresh.DisposeAsync().ConfigureAwait(false);
            }

            StopWatching();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Brings the running state in line with the setting and the latest facts (under the gate).</summary>
    /// <param name="relocate">Run the locator first (off the calling thread: registry, a known-folder lookup, a small file copy).</param>
    /// <returns>A task completing once applied.</returns>
    private async Task ApplyAsync(bool relocate)
    {
        var previous = location;
        if (relocate)
        {
            try
            {
                location = await Task.Run(locate).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The locator never throws by design; an exception here is unexpected — keep the last facts.
                AppLog.Warn($"Locating the Screenshots folder failed ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        var current = location;
        bool changed = !current.WatchesSameAs(previous) || current.SnippingToolPath != previous.SnippingToolPath || current.SnippingTool != previous.SnippingTool;
        if (!current.WatchesSameAs(previous) || current.SnippingToolPath != previous.SnippingToolPath)
        {
            AppLog.Info($"Screenshots folder: {current.Folder ?? "not resolvable"} ({current.DetectedBy}); Snipping Tool " +
                        (current.SnippingToolVersion is { } version ? $"{version} installed" : "not installed") + ".");
        }

        if (enabled && current.Folder is not null)
        {
            if (watcher is null)
            {
                await StartWatchingAsync(current).ConfigureAwait(false);
                changed = true;
            }
            else if (watcher.Update(current))
            {
                // The folder appeared or moved: whatever was saved there meanwhile is still unseen.
                AppLog.Info(watcher.WatchedFolder is null ? "The Screenshots folder is gone; waiting for it." : "Watching the Screenshots folder again.");
                if (watcher.WatchedFolder is not null)
                {
                    StartCatchUp(watcher, MarkerOrNow());
                }

                changed = true;
            }
        }
        else
        {
            changed |= StopWatching();
            if (!enabled)
            {
                await ClearMarkerAsync().ConfigureAwait(false);
            }
        }

        if (changed)
        {
            RaiseStatusChanged();
        }
    }

    /// <summary>Creates the folder watcher, then imports what was missed since the marker (or sets the marker on first use).</summary>
    /// <param name="current">Location facts.</param>
    /// <returns>A task completing once watching (the catch-up runs on).</returns>
    private async Task StartWatchingAsync(WindowsScreenshotsLocation current)
    {
        var created = new WindowsScreenshotWatcher(current, DeliverAsync, maxItemBytes, time);
        created.Handled += OnHandled;
        created.EventsLost += (_, _) => StartCatchUp(created, MarkerOrNow());
        created.Start();
        Volatile.Write(ref watcher, created);
        if (created.WatchedFolder is not null)
        {
            AppLog.Info("Watching the Screenshots folder for new screenshots (Snipping Tool, Win+PrtScn).");
        }

        // Start watching BEFORE reading the marker: a screenshot saved in between is then seen live rather than falling into
        // a gap between the scan and the watch (the dedupe drops doubles).
        var marker = await ReadMarkerAsync().ConfigureAwait(false);
        if (marker is { } since)
        {
            lock (markerGate)
            {
                lastSeen = since > lastSeen ? since : lastSeen;
            }

            StartCatchUp(created, since);
        }
        else
        {
            // First activation: from now on, never the archive of screenshots taken before. Awaited (outside the lock) so a
            // caller of StartAsync/SetEnabledAsync finds the marker stored when it returns.
            var now = time.GetUtcNow();
            Task? written;
            lock (markerGate)
            {
                lastSeen = now;
                written = PersistMarker(now);
            }

            if (written is not null)
            {
                try
                {
                    await written.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Already logged by PersistMarker's continuation; the in-memory marker still guards this session.
                }
            }
        }
    }

    /// <summary>Stops the folder watcher.</summary>
    /// <returns>Whether a watcher existed.</returns>
    private bool StopWatching()
    {
        var running = Interlocked.Exchange(ref watcher, null);
        if (running is null)
        {
            return false;
        }

        running.Handled -= OnHandled;
        running.Dispose();
        AppLog.Info("Stopped watching the Screenshots folder.");
        return true;
    }

    /// <summary>Runs a catch-up in the background (logged, never thrown).</summary>
    /// <param name="target">The watcher to run it on.</param>
    /// <param name="since">Files written after this instant.</param>
    private void StartCatchUp(WindowsScreenshotWatcher target, DateTimeOffset since)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                int imported = await target.CatchUpAsync(since, MaxCatchUpFiles, shutdown.Token).ConfigureAwait(false);
                if (imported > 0)
                {
                    AppLog.Info($"Imported {imported} screenshot{(imported == 1 ? string.Empty : "s")} saved while BetterClipboard was not watching.");
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down; the marker still says where to resume.
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Catching up on screenshots failed ({ex.GetType().Name}: {ex.Message}).");
            }
        });
    }

    /// <summary>Stores one screenshot through the history worker.</summary>
    /// <param name="capture">The capture.</param>
    /// <returns>Whether it was stored (new or refreshed); <see langword="false"/> when the rules rejected it (paused, ignored, too large, forgotten).</returns>
    /// <exception cref="InvalidOperationException">The history is shutting down (the watcher then leaves the marker alone).</exception>
    private async Task<bool> DeliverAsync(ClipCapture capture)
    {
        var entry = await history.AddAsync(capture).ConfigureAwait(false);
        if (entry is null)
        {
            return false;
        }

        Interlocked.Increment(ref importedThisSession);
        RaiseStatusChanged();
        return true;
    }

    /// <summary>Advances the catch-up marker (monotonic) after a file was dealt with.</summary>
    /// <param name="sender">The watcher.</param>
    /// <param name="writtenUtc">The file's write time.</param>
    private void OnHandled(object? sender, DateTimeOffset writtenUtc)
    {
        lock (markerGate)
        {
            if (writtenUtc <= lastSeen)
            {
                return;
            }

            lastSeen = writtenUtc;

            // Enqueued under the lock: the worker applies writes in queue order, so a later marker can never be overwritten
            // by an earlier one racing it.
            PersistMarker(writtenUtc);
        }
    }

    /// <summary>The marker to catch up from after lost events or a folder change.</summary>
    /// <returns>The in-memory marker, or now when none is set yet.</returns>
    private DateTimeOffset MarkerOrNow()
    {
        lock (markerGate)
        {
            return lastSeen == default ? time.GetUtcNow() : lastSeen;
        }
    }

    /// <summary>
    /// Queues the marker write. The call itself never throws: failures are logged, and the returned task only lets a caller
    /// wait for the write.
    /// </summary>
    /// <param name="value">The marker.</param>
    /// <returns>The queued write (faults when it failed), or <see langword="null"/> when it could not even be queued.</returns>
    private Task? PersistMarker(DateTimeOffset value)
    {
        try
        {
            var write = history.SetStateValueAsync(LastSeenStateName, value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
            _ = write.ContinueWith(
                task => AppLog.Warn($"Saving the screenshots catch-up marker failed ({task.Exception?.GetBaseException().GetType().Name})."),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return write;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            AppLog.Warn($"Saving the screenshots catch-up marker failed ({ex.GetType().Name}).");
            return null;
        }
    }

    /// <summary>Reads the stored marker.</summary>
    /// <returns>The marker, or <see langword="null"/> when none is stored (first activation, or cleared).</returns>
    private async Task<DateTimeOffset?> ReadMarkerAsync()
    {
        try
        {
            var raw = await history.GetStateValueAsync(LastSeenStateName).ConfigureAwait(false);
            return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null;
        }
        catch (Exception ex)
        {
            // Unreadable = treat as first activation: better to miss old screenshots than to import an archive.
            AppLog.Warn($"Reading the screenshots catch-up marker failed ({ex.GetType().Name}).");
            return null;
        }
    }

    /// <summary>Forgets the marker so the next activation starts fresh (only writes when one is stored).</summary>
    /// <returns>A task completing once cleared.</returns>
    private async Task ClearMarkerAsync()
    {
        lock (markerGate)
        {
            lastSeen = default;
        }

        try
        {
            if (!string.IsNullOrEmpty(await history.GetStateValueAsync(LastSeenStateName).ConfigureAwait(false)))
            {
                await history.SetStateValueAsync(LastSeenStateName, string.Empty).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Clearing the screenshots catch-up marker failed ({ex.GetType().Name}).");
        }
    }

    /// <summary>Raises <see cref="StatusChanged"/> (subscriber exceptions are logged, not propagated into the watcher).</summary>
    private void RaiseStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"A screenshots status subscriber failed ({ex.GetType().Name}).");
        }
    }
}
