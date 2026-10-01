using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Integrations;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Services;

namespace BetterClipboard.Windows.Integrations;

/// <summary>
/// Keeps every command run with Win+R in the history, beyond the 26 Windows remembers: imports what Windows
/// remembers on first activation, records each new run the moment Windows writes it, and rescans on request
/// (the panel's Run tab is entered).
/// </summary>
/// <remarks>
/// <para>
/// <b>Snapshot.</b> <see cref="SnapshotStateName"/> (kept in the encrypted store, fingerprints only) is Windows'
/// list as last seen. A rescan compares the list now with it (<see cref="RunMru.PlanCaptures"/>): no snapshot
/// means first activation, which imports the whole list as <see cref="ClipOrigin.RunDialogHistory"/>; afterwards
/// only the commands run since are stored, as <see cref="ClipOrigin.RunDialog"/>. The snapshot moves only after
/// the captures were handed to the history, so a failure is retried by the next rescan. Runs made while capture
/// was paused are skipped by the history and still move the snapshot: pausing means they are never recorded.
/// </para>
/// <para>
/// <b>Off</b> stops the watch and clears the snapshot, so switching back on starts over with what Windows
/// remembers then: commands already stored only gain a run time (an import never duplicates or reorders).
/// Exiting the app is not "off": the next start finds the runs made meanwhile.
/// </para>
/// <para>
/// <b>Read only.</b> Windows' list is never written — neither runs from the panel nor deletions here touch it.
/// </para>
/// <para>
/// <b>Threads.</b> Every public member may be called from any thread; rescans are serialized by one gate.
/// <see cref="StatusChanged"/> is raised on a pool thread.
/// </para>
/// </remarks>
public sealed class RunHistoryIntegration : IAsyncDisposable
{
    /// <summary>Store state name of the snapshot (<see cref="RunMru.FormatSnapshot"/>; empty = none).</summary>
    public const string SnapshotStateName = "runmru.snapshot";

    /// <summary>
    /// Environment variable that points the integration at another key under <c>HKEY_CURRENT_USER</c> (tests and
    /// isolated dev runs use a scratch key with BC-TEST commands, so the user's Win+R history is never read).
    /// </summary>
    public const string KeyOverrideVariable = "BETTERCLIPBOARD_RUNMRU_KEY";

    /// <summary>Quiet time after a change notification before rescanning (one run writes the key twice).</summary>
    public static readonly TimeSpan WatchDebounce = TimeSpan.FromMilliseconds(150);

    private readonly ClipHistoryService history;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private RunMruWatcher? watcher;
    private Timer? debounce;
    private volatile bool enabled;
    private bool started;
    private bool disposed;
    private int windowsCount = -1;
    private int recordedThisSession;

    /// <summary>
    /// Creates the integration; nothing happens until <see cref="StartAsync"/>.
    /// </summary>
    /// <param name="history">Where commands are stored and the snapshot is kept.</param>
    /// <param name="keyPath">
    /// The key to read, relative to <c>HKEY_CURRENT_USER</c>; <see langword="null"/> = <see cref="KeyOverrideVariable"/>
    /// when set, else Windows' own (<see cref="RunMru.DefaultKeyPath"/>).
    /// </param>
    /// <param name="time">Clock for run times and the settle check; defaults to the system clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="history"/> is <see langword="null"/>.</exception>
    public RunHistoryIntegration(ClipHistoryService history, string? keyPath = null, TimeProvider? time = null)
    {
        this.history = history ?? throw new ArgumentNullException(nameof(history));
        KeyPath = RunMruReader.ResolveKeyPath(keyPath ?? Environment.GetEnvironmentVariable(KeyOverrideVariable));
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Raised on a pool thread whenever <see cref="IsWatching"/>, <see cref="WindowsCount"/> or
    /// <see cref="RecordedThisSession"/> changed.
    /// </summary>
    public event EventHandler? StatusChanged;

    /// <summary>The key read, relative to <c>HKEY_CURRENT_USER</c> (Windows' own unless overridden).</summary>
    public string KeyPath { get; }

    /// <summary>Whether recording is on (the <c>RecordRunHistory</c> setting as last applied).</summary>
    public bool IsEnabled => enabled;

    /// <summary>Whether Windows' list is being watched right now.</summary>
    public bool IsWatching => Volatile.Read(ref watcher) is not null;

    /// <summary>How many commands Windows remembered at the last look; -1 before the first one.</summary>
    public int WindowsCount => Volatile.Read(ref windowsCount);

    /// <summary>Runs recorded since the app started (seen in Windows' list or run from the panel), not the first import.</summary>
    public int RecordedThisSession => Volatile.Read(ref recordedThisSession);

    /// <summary>
    /// Applies <paramref name="enable"/>: when on, starts the watch and reads the list (importing it on first
    /// activation). Call once.
    /// </summary>
    /// <param name="enable">The <c>RecordRunHistory</c> setting.</param>
    /// <returns>A task completing once the first look is stored.</returns>
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
                throw new InvalidOperationException("The Win+R history integration was already started.");
            }

            started = true;
            await ApplyAsync(enable).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Turns recording on or off (the setting changed). Off stops the watch and clears the snapshot.
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

            await ApplyAsync(enable).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reads Windows' list now and stores what is new since the last look. Failures are logged, never thrown
    /// (fire-and-forget safe).
    /// </summary>
    /// <param name="reason">Why (for the log only: "tab", "watch", …).</param>
    /// <returns>How many entries were stored or refreshed; 0 while off, before start, after disposal, or on failure.</returns>
    public async Task<int> RescanAsync(string reason)
    {
        try
        {
            await gate.WaitAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }

        try
        {
            return !disposed && started && enabled ? await RescanCoreAsync(reason).ConfigureAwait(false) : 0;
        }
        catch (OperationCanceledException)
        {
            // Shutting down while waiting for the key to settle; the snapshot still says where to resume.
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Reading the Win+R history failed ({reason}; {ex.GetType().Name}: {ex.Message}).");
            return 0;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Records a command run from the panel (Windows' list is not written, so it would never show up there): it moves
    /// to the top of the history with a new run time, like a run seen in Windows' list.
    /// </summary>
    /// <remarks>
    /// Recorded whether or not the setting is on — the user ran a stored command explicitly — but like every new
    /// event not while capture is paused, and not when the command is on the "Forget forever" list.
    /// </remarks>
    /// <param name="command">The command as stored (unexpanded).</param>
    /// <returns>The entry, or <see langword="null"/> when the history's rules skipped it.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is null or blank.</exception>
    /// <exception cref="InvalidOperationException">The history is shutting down.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">Persisting failed.</exception>
    public async Task<ClipEntry?> RecordRunAsync(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var entry = await history.AddAsync(RunMru.CreateCapture(command.Trim(), time.GetUtcNow(), observedRun: true)).ConfigureAwait(false);
        if (entry is not null)
        {
            Interlocked.Increment(ref recordedThisSession);
            RaiseStatusChanged();
        }

        return entry;
    }

    /// <summary>Stops the watch; the snapshot is kept (exit is not "off").</summary>
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
            StopWatching();
            if (debounce is not null)
            {
                await debounce.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Brings the watch and the snapshot in line with the setting (under the gate).</summary>
    /// <param name="enable">The setting.</param>
    /// <returns>A task completing once applied.</returns>
    private async Task ApplyAsync(bool enable)
    {
        enabled = enable;
        if (enable)
        {
            // Watch BEFORE the first read: a run in between is then reported again instead of falling into a gap
            // between the read and the watch (a rescan of an unchanged list stores nothing).
            StartWatching();
            try
            {
                await RescanCoreAsync("start").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The watch stays: the next change, or entering the Run tab, tries again.
                AppLog.Warn($"Reading the Win+R history failed (start; {ex.GetType().Name}: {ex.Message}).");
            }
        }
        else
        {
            bool stopped = StopWatching();
            await ClearSnapshotAsync().ConfigureAwait(false);
            if (stopped)
            {
                AppLog.Info("Stopped keeping the Win+R history.");
            }
        }

        RaiseStatusChanged();
    }

    /// <summary>Reads the list and stores what is new since the snapshot, then moves the snapshot (under the gate).</summary>
    /// <param name="reason">For the log.</param>
    /// <returns>How many entries were stored or refreshed.</returns>
    /// <exception cref="OperationCanceledException">Shutting down while waiting for the key to settle.</exception>
    /// <exception cref="Exception">Reading the key or storing failed (the snapshot is left alone).</exception>
    private async Task<int> RescanCoreAsync(string reason)
    {
        var state = await RunMruReader.ReadSettledAsync(KeyPath, time, shutdown.Token).ConfigureAwait(false);
        var stored = await history.GetStateValueAsync(SnapshotStateName).ConfigureAwait(false);
        bool hasSnapshot = RunMru.TryParseSnapshot(stored, out var previous);
        var captures = RunMru.PlanCaptures(hasSnapshot ? previous : null, state.Commands, state.LastWriteUtc ?? time.GetUtcNow());

        int count = 0;
        if (!hasSnapshot)
        {
            // First activation: one batch, one list refresh, and no reordering of anything already stored.
            count = captures.Count == 0 ? 0 : await history.ImportAsync(captures).ConfigureAwait(false);
            AppLog.Info($"Keeping the Win+R history: imported {count} new of {captures.Count} command{(captures.Count == 1 ? string.Empty : "s")} Windows remembered.");
        }
        else if (captures.Count > 0)
        {
            foreach (var capture in captures)
            {
                if (await history.AddAsync(capture).ConfigureAwait(false) is not null)
                {
                    count++;
                }
            }

            Interlocked.Add(ref recordedThisSession, count);
            AppLog.Info($"Win+R: {captures.Count} new run{(captures.Count == 1 ? string.Empty : "s")} found ({reason}), {count} recorded.");
        }

        // Only after the captures are stored: a failure above leaves the old snapshot, so the next look retries.
        var snapshot = RunMru.FormatSnapshot(state.Commands);
        if (!string.Equals(stored, snapshot, StringComparison.Ordinal))
        {
            await history.SetStateValueAsync(SnapshotStateName, snapshot).ConfigureAwait(false);
        }

        if (Interlocked.Exchange(ref windowsCount, state.Commands.Count) != state.Commands.Count || count > 0)
        {
            RaiseStatusChanged();
        }

        return count;
    }

    /// <summary>Starts the change watch (no-op while watching).</summary>
    private void StartWatching()
    {
        if (watcher is not null)
        {
            return;
        }

        debounce ??= new Timer(_ => _ = RescanAsync("watch"), null, Timeout.Infinite, Timeout.Infinite);
        var created = new RunMruWatcher(KeyPath);
        created.Changed += OnKeyChanged;
        created.Start();
        Volatile.Write(ref watcher, created);
        AppLog.Info(KeyPath == RunMru.DefaultKeyPath
            ? "Watching the Win+R history."
            : $"Watching the Win+R history at HKCU\\{KeyPath} ({KeyOverrideVariable}).");
    }

    /// <summary>Stops the change watch.</summary>
    /// <returns>Whether a watch was running.</returns>
    private bool StopWatching()
    {
        var running = Interlocked.Exchange(ref watcher, null);
        if (running is null)
        {
            return false;
        }

        running.Changed -= OnKeyChanged;
        running.Dispose();
        try
        {
            debounce?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently; nothing left to cancel.
        }

        return true;
    }

    /// <summary>(Re)arms the rescan debounce after a change notification (watcher thread; returns at once).</summary>
    /// <param name="sender">The watcher.</param>
    /// <param name="e">Unused.</param>
    private void OnKeyChanged(object? sender, EventArgs e)
    {
        try
        {
            debounce?.Change(WatchDebounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposed while a notification was in flight.
        }
    }

    /// <summary>Forgets the snapshot so the next activation starts over (only writes when one is stored).</summary>
    /// <returns>A task completing once cleared.</returns>
    private async Task ClearSnapshotAsync()
    {
        try
        {
            if (!string.IsNullOrEmpty(await history.GetStateValueAsync(SnapshotStateName).ConfigureAwait(false)))
            {
                await history.SetStateValueAsync(SnapshotStateName, string.Empty).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Clearing the Win+R history snapshot failed ({ex.GetType().Name}).");
        }
    }

    /// <summary>Raises <see cref="StatusChanged"/> (subscriber exceptions are logged, not propagated).</summary>
    private void RaiseStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"A Win+R history status subscriber failed ({ex.GetType().Name}).");
        }
    }
}
