using System.Globalization;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Services;

namespace BetterClipboard.Windows.Integrations;

/// <summary>
/// Runs the ShareX integration for the app: finds ShareX, watches its screenshot folders while the
/// setting is on, catches up on screenshots saved while BetterClipboard was not running, and follows
/// ShareX when its settings move the folders.
/// </summary>
/// <remarks>
/// <para>
/// <b>Catch-up marker.</b> <see cref="LastSeenStateName"/> (kept in the encrypted store) is the newest
/// write time of a screenshot file already dealt with. At startup, files written after it are imported,
/// at most <see cref="MaxCatchUpFiles"/> of the newest. The first activation sets it to "now", so turning
/// the feature on never backfills a whole screenshot archive. Turning it off clears it, and turning it back
/// on starts fresh. Screenshots taken while it was off are not imported afterwards; exiting the app does
/// not count as off.
/// </para>
/// <para>
/// <b>Re-location.</b> The locator runs again when ShareX's JSON settings in its personal folder change
/// (debounced), when Settings opens (<see cref="RefreshAsync"/>), and every <see cref="RefreshInterval"/>.
/// The periodic run notices a new install or a screenshots folder that did not exist yet. When the watched
/// folders change, a catch-up from the marker picks up anything saved there meanwhile.
/// </para>
/// <para>
/// <b>Threads.</b> Every public member may be called from any thread; state changes are serialized by one
/// gate. <see cref="StatusChanged"/> is raised on a pool thread.
/// </para>
/// </remarks>
public sealed class ShareXIntegration : IAsyncDisposable
{
    /// <summary>Store state name of the catch-up marker (ISO 8601 round-trip UTC time; empty = none).</summary>
    public const string LastSeenStateName = "sharex.last_seen_utc";

    /// <summary>Most screenshots one catch-up imports (the newest ones), so a long absence cannot flood the history.</summary>
    public const int MaxCatchUpFiles = 100;

    /// <summary>How often ShareX is located again in the background (install, uninstall, folders appearing).</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    /// <summary>Quiet time after a ShareX settings write before re-locating (ShareX writes several files in a row).</summary>
    private static readonly TimeSpan ConfigSettleDelay = TimeSpan.FromSeconds(1);

    private readonly ClipHistoryService history;
    private readonly Func<long> maxItemBytes;
    private readonly Func<ShareXInstallation> locate;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Lock markerGate = new();
    private readonly CancellationTokenSource shutdown = new();
    private volatile ShareXInstallation installation = ShareXInstallation.NotFound;
    private ShareXScreenshotWatcher? watcher;
    private FileSystemWatcher? configWatcher;
    private Timer? configDebounce;
    private Timer? periodicRefresh;
    private bool enabled;
    private bool started;
    private bool disposed;
    private DateTimeOffset lastSeen;
    private int importedThisSession;

    /// <summary>
    /// Creates the integration; nothing happens until <see cref="StartAsync"/>.
    /// </summary>
    /// <param name="history">Where screenshots are stored and the marker is kept.</param>
    /// <param name="maxItemBytes">Current largest-item limit in bytes (files above it are skipped unread).</param>
    /// <param name="locate">Finds ShareX; defaults to <see cref="ShareXLocator.Locate"/> (tests pass fakes).</param>
    /// <param name="time">Clock for the first-activation marker; defaults to the system clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="history"/> or <paramref name="maxItemBytes"/> is <see langword="null"/>.</exception>
    public ShareXIntegration(ClipHistoryService history, Func<long> maxItemBytes, Func<ShareXInstallation>? locate = null, TimeProvider? time = null)
    {
        this.history = history ?? throw new ArgumentNullException(nameof(history));
        this.maxItemBytes = maxItemBytes ?? throw new ArgumentNullException(nameof(maxItemBytes));
        this.locate = locate ?? ShareXLocator.Locate;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Raised on a pool thread whenever <see cref="Installation"/>, <see cref="IsWatching"/> or the import count changed.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>What was found about ShareX (<see cref="ShareXInstallation.NotFound"/> until located).</summary>
    public ShareXInstallation Installation => installation;

    /// <summary>Whether screenshot folders are being watched right now.</summary>
    public bool IsWatching => Volatile.Read(ref watcher) is not null;

    /// <summary>The folders being watched (empty when not watching).</summary>
    public IReadOnlyList<string> WatchedFolders => Volatile.Read(ref watcher)?.WatchedFolders ?? [];

    /// <summary>Screenshots stored since the app started (new items or refreshed duplicates).</summary>
    public int ImportedThisSession => Volatile.Read(ref importedThisSession);

    /// <summary>
    /// Locates ShareX and applies <paramref name="enable"/> (watch + catch-up when on and installed). Call once.
    /// </summary>
    /// <param name="enable">The <c>ImportShareXScreenshots</c> setting.</param>
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
                throw new InvalidOperationException("The ShareX integration was already started.");
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

    /// <summary>
    /// Turns the import on or off (the setting changed). Off stops watching and clears the catch-up marker.
    /// </summary>
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

    /// <summary>
    /// Locates ShareX again and follows any change (tab visibility, watched folders). Failures are logged,
    /// never thrown (fire-and-forget safe).
    /// </summary>
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
            AppLog.Warn($"Refreshing the ShareX integration failed ({ex.GetType().Name}: {ex.Message}).");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Stops watching and timers; the catch-up marker is kept (exit is not "off").</summary>
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

            configDebounce?.Dispose();
            configWatcher?.Dispose();
            configWatcher = null;
            StopWatching();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Brings the running state in line with the setting and the latest location facts (under the gate).
    /// </summary>
    /// <param name="relocate">Run the locator first (off the calling thread: registry and file reads).</param>
    /// <returns>A task completing once applied.</returns>
    private async Task ApplyAsync(bool relocate)
    {
        var previous = installation;
        if (relocate)
        {
            try
            {
                installation = await Task.Run(locate).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The locator degrades to defaults on bad files; an exception here is unexpected — keep the last facts.
                AppLog.Warn($"Locating ShareX failed ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        var current = installation;
        if (current.IsInstalled != previous.IsInstalled || !current.WatchesSameAs(previous))
        {
            AppLog.Info(current.IsInstalled
                ? $"ShareX found ({current.DetectedBy}); screenshots go to {string.Join(", ", current.Folders.Select(f => f.RelativePattern.Length > 0 ? $"{f.Root} ({f.RelativePattern})" : f.Root))}."
                : "ShareX is not installed.");
        }

        WatchConfiguration(current);
        bool changed = current.IsInstalled != previous.IsInstalled;
        if (enabled && current.IsInstalled)
        {
            if (watcher is null)
            {
                await StartWatchingAsync(current).ConfigureAwait(false);
                changed = true;
            }
            else if (watcher.Update(current))
            {
                // A folder appeared or moved: whatever ShareX saved there meanwhile is still unseen.
                AppLog.Info($"ShareX screenshot folders changed; watching {watcher.WatchedFolders.Count} folder(s).");
                StartCatchUp(watcher, MarkerOrNow());
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
    private async Task StartWatchingAsync(ShareXInstallation current)
    {
        var created = new ShareXScreenshotWatcher(current, DeliverAsync, maxItemBytes);
        created.Handled += OnHandled;
        created.EventsLost += (_, _) => StartCatchUp(created, MarkerOrNow());
        created.Start();
        Volatile.Write(ref watcher, created);
        AppLog.Info($"Watching {created.WatchedFolders.Count} ShareX screenshot folder(s) for new screenshots.");

        // Start watching BEFORE reading the marker: a screenshot saved in between is then seen live
        // rather than falling into a gap between the scan and the watch (the dedupe drops doubles).
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
            // First activation: from now on, never the archive of screenshots taken before. Awaited (outside
            // the lock) so a caller of StartAsync/SetEnabledAsync finds the marker stored when it returns.
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

    /// <summary>Stops the folder watcher (not the configuration watch).</summary>
    /// <returns>Whether a watcher was running.</returns>
    private bool StopWatching()
    {
        var running = Interlocked.Exchange(ref watcher, null);
        if (running is null)
        {
            return false;
        }

        running.Handled -= OnHandled;
        running.Dispose();
        AppLog.Info("Stopped watching ShareX screenshot folders.");
        return true;
    }

    /// <summary>Runs a catch-up in the background (logged, never thrown).</summary>
    /// <param name="target">The watcher to run it on.</param>
    /// <param name="since">Files written after this instant.</param>
    private void StartCatchUp(ShareXScreenshotWatcher target, DateTimeOffset since)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                int imported = await target.CatchUpAsync(since, MaxCatchUpFiles, shutdown.Token).ConfigureAwait(false);
                if (imported > 0)
                {
                    AppLog.Info($"Imported {imported} ShareX screenshot{(imported == 1 ? string.Empty : "s")} saved while BetterClipboard was not watching.");
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down; the marker still says where to resume.
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Catching up on ShareX screenshots failed ({ex.GetType().Name}: {ex.Message}).");
            }
        });
    }

    /// <summary>Stores one screenshot through the history worker.</summary>
    /// <param name="capture">The capture.</param>
    /// <returns>Whether it was stored (new or refreshed); <see langword="false"/> when the rules rejected it (paused, ignored, too large).</returns>
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

            // Enqueued under the lock: the worker applies writes in queue order, so a later marker can
            // never be overwritten by an earlier one racing it.
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
    /// Queues the marker write. The call itself never throws: failures are logged, and the returned task
    /// only lets a caller wait for the write.
    /// </summary>
    /// <param name="value">The marker.</param>
    /// <returns>The queued write (faults when it failed), or <see langword="null"/> when it could not even be queued.</returns>
    private Task? PersistMarker(DateTimeOffset value)
    {
        try
        {
            var write = history.SetStateValueAsync(LastSeenStateName, value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
            _ = write.ContinueWith(
                task => AppLog.Warn($"Saving the ShareX catch-up marker failed ({task.Exception?.GetBaseException().GetType().Name})."),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return write;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            AppLog.Warn($"Saving the ShareX catch-up marker failed ({ex.GetType().Name}).");
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
            AppLog.Warn($"Reading the ShareX catch-up marker failed ({ex.GetType().Name}).");
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
            AppLog.Warn($"Clearing the ShareX catch-up marker failed ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Watches ShareX's personal folder for settings writes (<c>*.json</c>, not recursive — backups live in a
    /// subfolder) and re-locates after they settle. Re-created when the personal folder moves.
    /// </summary>
    /// <param name="current">Location facts.</param>
    private void WatchConfiguration(ShareXInstallation current)
    {
        string? folder = current.IsInstalled && Directory.Exists(current.PersonalFolder) ? current.PersonalFolder : null;
        if (string.Equals(configWatcher?.Path, folder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        configWatcher?.Dispose();
        configWatcher = null;
        if (folder is null)
        {
            return;
        }

        try
        {
            configDebounce ??= new Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
            var created = new FileSystemWatcher(folder, "*.json")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler settle = (_, _) => DebounceRefresh();
            created.Changed += settle;
            created.Created += settle;
            created.Renamed += (_, _) => DebounceRefresh();
            created.EnableRaisingEvents = true;
            configWatcher = created;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Cannot watch ShareX's settings folder ({ex.Message}); changes are picked up every {RefreshInterval.TotalMinutes:0} minutes.");
        }
    }

    /// <summary>(Re)arms the settings-change debounce.</summary>
    private void DebounceRefresh()
    {
        try
        {
            configDebounce?.Change(ConfigSettleDelay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposed while an event was in flight.
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
            AppLog.Warn($"A ShareX status subscriber failed ({ex.GetType().Name}).");
        }
    }
}
