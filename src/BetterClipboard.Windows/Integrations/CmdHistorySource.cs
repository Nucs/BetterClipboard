using System.Diagnostics;
using System.Globalization;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Shells;
using BetterClipboard.Windows.Interop;
using BetterClipboard.Windows.Shell;

namespace BetterClipboard.Windows.Integrations;

/// <summary>
/// The Command Prompt history behind the panel's Cmd tab: reads the histories of open cmd windows through helper
/// processes, and keeps what it saw (<see cref="CmdHistory"/>) so commands outlive their window.
/// </summary>
/// <remarks>
/// <para>
/// <b>When it reads.</b> Every <see cref="ScanInterval"/> while it is on (a process snapshot — microseconds — and a
/// helper only per interactive cmd window, usually none), and when the Cmd tab loads (<see cref="GetCommandsAsync"/>,
/// at most once per <see cref="FreshFor"/>). cmd notifies nobody when a command is typed (a Windows Terminal tab
/// raises no console events at all, CLAUDE.md §2.16), so the timer is what saves the commands of a window closed
/// before the tab is opened; a window closed within one interval of its last command loses what came after the
/// last read.
/// </para>
/// <para>
/// <b>How.</b> Each interactive cmd (<see cref="InteractiveConsoles"/>) in this session gets one helper:
/// <c>BetterClipboard.exe --read-console-history &lt;pid&gt;</c> (<see cref="ConsoleCommandHistory"/>), at most
/// <see cref="MaxParallelReads"/> at once, each with a <see cref="HelperTimeout"/>. A window read before is compared
/// with its last read (<see cref="CmdHistory.NewSinceRead"/>), so a command typed again counts again; a window seen for
/// the first time adds its commands without counting them again, each one the list does not keep placed by the window's
/// own order (<see cref="CmdHistory.Remember"/>).
/// </para>
/// <para>
/// <b>The reader's memory</b> (<see cref="CmdHistory.WindowsStateName"/>, fingerprints only): each open window's last read,
/// written whenever it changed — also while paused, like the Win+R snapshot — and loaded at the first read of a process. So
/// a window open through a restart (the silent restart a few seconds after Settings closed, an update) is a window read
/// before. Until 2026-11-21 the memory lived in the process alone: after <em>Delete stored…</em> or a lowered limit, the
/// restart that followed closing Settings kept the open windows' removed commands again (CLAUDE.md §2.62).
/// </para>
/// <para>
/// <b>Kept list</b> (<see cref="CmdHistory.KeptStateName"/>, in the encrypted store, as many commands as
/// <see cref="KeptLimit"/> allows — Settings' <see cref="Core.Settings.AppSettings.MaxKeptCmdCommands"/>, no limit by default;
/// the newest 1,000 until 2026-11-21): written only when it changed, and never while capture is paused — reads still happen
/// then, so the tab shows open windows' commands, but nothing new is kept. A lowered limit applies at once
/// (<see cref="ApplyLimitAsync"/>). Switching the feature off stops the timer and deletes the list. Nothing of it reaches a
/// log (counts only).
/// </para>
/// Thread-safe: reads are serialized by a gate; events are raised on the pool.
/// </remarks>
public sealed class CmdHistorySource : IAsyncDisposable
{
    /// <summary>How often open cmd windows are read while the feature is on.</summary>
    public static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);

    /// <summary>How old a read may be for the tab to show it without reading again (typing a search reloads).</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(2);

    /// <summary>How long one helper may take before it is ended (it normally needs ~60 ms).</summary>
    public static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Most helpers running at once.</summary>
    public const int MaxParallelReads = 4;

    private readonly ClipHistoryService history;
    private readonly Func<bool> isPaused;
    private readonly string helperPath;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>
    /// Each window's last read as fingerprints (<see cref="CmdHistory.Fingerprint"/>), oldest first, by process id and start
    /// time (ids are reused): the reader's memory, saved in the store when it changes (<see cref="savedWindows"/>).
    /// Guarded by <see cref="gate"/>.
    /// </summary>
    private readonly Dictionary<(uint ProcessId, long StartTicks), IReadOnlyList<string>> lastReads = [];

    /// <summary>
    /// The reader's memory as last loaded or written (<see cref="CmdHistory.FormatWindows"/>); <see langword="null"/> until
    /// the first read of this process loaded it. Guarded by <see cref="gate"/>.
    /// </summary>
    private string? savedWindows;

    /// <summary>The kept list as last loaded or written; <see langword="null"/> until loaded. Guarded by <see cref="gate"/>.</summary>
    private IReadOnlyList<KeptCommand>? kept;

    /// <summary>
    /// What each open window held at the last read, oldest first, for the tab's view (<see cref="GetCommandsAsync"/>): one
    /// list per window, because <see cref="CmdHistory.Remember"/> places what the list does not keep by each window's own
    /// order. Guarded by <see cref="gate"/>.
    /// </summary>
    private IReadOnlyList<IReadOnlyList<string>> openWindowCommands = [];

    private DateTimeOffset lastRead = DateTimeOffset.MinValue;
    private Timer? timer;
    private bool disposed;

    /// <summary>
    /// Creates the source; nothing runs until <see cref="SetEnabledAsync"/>.
    /// </summary>
    /// <param name="history">The history service (state storage for the kept list).</param>
    /// <param name="isPaused">Whether capture is paused right now (nothing new is kept then).</param>
    /// <param name="helperPath">The helper executable: <c>BetterClipboard.exe</c> itself (<see cref="Environment.ProcessPath"/>).</param>
    /// <param name="time">Clock (tests); <see langword="null"/> = system.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="helperPath"/> is blank.</exception>
    public CmdHistorySource(ClipHistoryService history, Func<bool> isPaused, string helperPath, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(isPaused);
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
        this.history = history;
        this.isPaused = isPaused;
        this.helperPath = helperPath;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// The most commands the kept list may hold, asked at every read (<see cref="Core.Settings.ListLimit"/>: 0 = no limit, the
    /// default). The app passes Settings' <see cref="Core.Settings.AppSettings.MaxKeptCmdCommands"/>; tests leave it unlimited.
    /// </summary>
    public Func<int> KeptLimit { get; init; } = static () => 0;

    /// <summary>Raised (pool thread) after a read changed the kept list, or the feature was switched on or off.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Whether the periodic reading runs.</summary>
    public bool IsRunning => timer is not null;

    /// <summary>Interactive cmd windows found at the last read (-1 before the first).</summary>
    public int OpenWindows { get; private set; } = -1;

    /// <summary>Commands kept so far (0 before the list was loaded).</summary>
    public int KeptCount => kept?.Count ?? 0;

    /// <summary>
    /// Switches the feature on (start the timer and read now) or off (stop, forget the kept list). Idempotent.
    /// </summary>
    /// <param name="enabled">Desired state.</param>
    /// <returns>A task completing when applied; failures are logged, never thrown.</returns>
    public async Task SetEnabledAsync(bool enabled)
    {
        if (disposed)
        {
            return;
        }

        if (enabled && timer is null)
        {
            timer = new Timer(_ => _ = ReadAsync("timer", CancellationToken.None), null, TimeSpan.Zero, ScanInterval);
            AppLog.Info("Reading Command Prompt windows' history (the Cmd tab).");
        }
        else if (!enabled && timer is not null)
        {
            await timer.DisposeAsync();
            timer = null;
            await gate.WaitAsync();
            try
            {
                kept = [];
                lastReads.Clear();
                openWindowCommands = [];
                OpenWindows = -1;
                await history.SetStateValueAsync(CmdHistory.KeptStateName, string.Empty);

                // The reader's memory goes too: on again, open windows are read as new ones, as on a first activation.
                await history.SetStateValueAsync(CmdHistory.WindowsStateName, string.Empty);
                savedWindows = string.Empty;
                AppLog.Info("Cmd tab switched off; the kept Command Prompt commands were deleted.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                AppLog.Warn($"Deleting the kept Command Prompt commands failed ({ex.GetType().Name}).");
            }
            finally
            {
                gate.Release();
            }
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Forgets the kept commands (Settings' <em>Delete stored…</em> on the Cmd card), in memory and in the store, while the
    /// reading goes on. Each open window's last read is kept — in the reader's memory, which outlives a restart
    /// (<see cref="CmdHistory.WindowsStateName"/>) — so what the windows hold now is not kept again on the next read, also
    /// not after the silent restart that follows closing Settings; commands typed from now on are. The windows themselves are
    /// never touched: the tab still shows what they hold while they are open.
    /// </summary>
    /// <remarks>
    /// Without this, the list in memory would be written back at the next read (every 30 s), undoing a deletion made
    /// through the store alone. Waits for a read in progress (the same gate), so a read cannot write the old list after it.
    /// Until 2026-11-21 the reader's memory lived in the process alone, and the restart after Settings closed kept the open
    /// windows' commands again (CLAUDE.md §2.62).
    /// </remarks>
    /// <returns>A task completing when forgotten; failures are logged, never thrown.</returns>
    public async Task ForgetKeptAsync()
    {
        if (disposed)
        {
            return;
        }

        await gate.WaitAsync();
        try
        {
            kept = [];
            await history.SetStateValueAsync(CmdHistory.KeptStateName, string.Empty);
            AppLog.Info("The kept Command Prompt commands were deleted from Settings; open windows are read on.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLog.Warn($"Deleting the kept Command Prompt commands failed ({ex.GetType().Name}).");
        }
        finally
        {
            gate.Release();
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Applies <see cref="KeptLimit"/> to the kept list now, in memory and in the store: a lowered limit deletes the commands
    /// seen longest ago at once, so Settings' count and the tab follow the number the user just typed instead of the next
    /// read (up to <see cref="ScanInterval"/> later). Nothing changes when the list fits, or before it was ever loaded and
    /// the store holds none.
    /// </summary>
    /// <remarks>
    /// Applied also while capture is paused: pausing stops keeping new commands, not the user's own limit. Waits for a read in
    /// progress (the same gate), so a read cannot write the longer list after it.
    /// </remarks>
    /// <returns>A task completing when applied; failures are logged, never thrown.</returns>
    public async Task ApplyLimitAsync()
    {
        if (disposed)
        {
            return;
        }

        bool trimmed = false;
        await gate.WaitAsync();
        try
        {
            kept ??= CmdHistory.ParseKept(await history.GetStateValueAsync(CmdHistory.KeptStateName));
            var next = CmdHistory.Trim(kept, KeptLimit());
            if (next.Count < kept.Count)
            {
                await history.SetStateValueAsync(CmdHistory.KeptStateName, CmdHistory.FormatKept(next));
                AppLog.Info($"The Cmd tab's kept commands follow their new limit: {kept.Count - next.Count} deleted, {next.Count} kept.");
                kept = next;
                trimmed = true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLog.Warn($"Applying the limit of kept Command Prompt commands failed ({ex.GetType().Name}).");
        }
        finally
        {
            gate.Release();
        }

        if (trimmed)
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The Cmd tab's live commands: the kept list plus whatever open windows hold, newest first. Reads the windows
    /// first unless the last read is fresher than <see cref="FreshFor"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels a superseded load (a read in flight still finishes for the timer's sake).</param>
    /// <returns>The commands (empty when nothing was seen).</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<IReadOnlyList<ShellCommand>> GetCommandsAsync(CancellationToken cancellationToken)
    {
        if (time.GetUtcNow() - lastRead > FreshFor)
        {
            await ReadAsync("tab", cancellationToken);
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            var list = kept ?? [];

            // The tab also shows what open windows hold besides the kept list, not counted again: what was typed while
            // capture is paused, and what Settings' Delete stored… forgot. Each window as one seen for the first time, so a
            // command the list does not keep goes below the window's next newer one, within the limit a read would keep:
            // under a lowered limit the tab shows the kept newest, never a window's older commands (CLAUDE.md §2.62; until
            // 2026-11-21 all windows' commands went in as one list, each near now, and pushed the kept newest out).
            var now = time.GetUtcNow();
            foreach (var window in openWindowCommands)
            {
                list = CmdHistory.Remember(list, window, typedAgain: false, now, KeptLimit());
            }

            return list.Select(c => new ShellCommand(ShellKind.Cmd, c.Text, c.Count, c.LastSeen, ShellSources.CmdName)).ToList();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reads every interactive cmd window now and keeps what is new (see class remarks). Serialized: a read that
    /// finds another in progress waits for it and returns. Never throws.
    /// </summary>
    /// <param name="reason">For the log ("timer", "tab").</param>
    /// <param name="cancellationToken">Cancels waiting for the gate.</param>
    /// <returns>A task completing when done.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled while waiting.</exception>
    public async Task ReadAsync(string reason, CancellationToken cancellationToken)
    {
        if (disposed)
        {
            return;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            kept ??= CmdHistory.ParseKept(await history.GetStateValueAsync(CmdHistory.KeptStateName));
            if (savedWindows is null)
            {
                // The first read of this process: the reader's memory from the one before (a restart, the silent one after
                // Settings closed included), so windows open through it are windows read before, not new ones.
                savedWindows = await history.GetStateValueAsync(CmdHistory.WindowsStateName) ?? string.Empty;
                foreach (var (window, fingerprints) in CmdHistory.ParseWindows(savedWindows))
                {
                    lastReads.TryAdd(window, fingerprints);
                }
            }

            var windows = FindInteractiveCmdWindows();
            var reads = await ReadWindowsAsync(windows);
            var now = time.GetUtcNow();
            int limit = KeptLimit();
            var next = kept;
            var open = new List<IReadOnlyList<string>>();
            var seen = new HashSet<(uint, long)>();
            foreach (var (window, commands) in reads)
            {
                seen.Add(window);
                open.Add(commands);
                next = lastReads.TryGetValue(window, out var previous)
                    ? CmdHistory.Remember(next, CmdHistory.NewSinceRead(previous, commands), typedAgain: true, now, limit)
                    : CmdHistory.Remember(next, commands, typedAgain: false, now, limit);
                lastReads[window] = commands.Select(CmdHistory.Fingerprint).ToList();
            }

            // Windows that closed: their last read is all there will ever be.
            foreach (var gone in lastReads.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                lastReads.Remove(gone);
            }

            openWindowCommands = open;
            OpenWindows = windows.Count;
            lastRead = now;
            bool changed = !SameList(kept, next);
            if (changed && !isPaused())
            {
                await history.SetStateValueAsync(CmdHistory.KeptStateName, CmdHistory.FormatKept(next, limit));
                kept = next;

                // Counts only, never a command.
                AppLog.Info($"Command Prompt history read ({reason}): {windows.Count} window(s), {kept.Count} command(s) kept.");
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }

            // The reader's memory, after the kept list: when that write fails, the saved memory stays behind it, and a restart
            // reads those windows as seen first instead of skipping commands that were never kept. Also while paused —
            // fingerprints only, like the Win+R snapshot — so what was typed while paused stays unkept after a restart too.
            var memory = CmdHistory.FormatWindows(lastReads);
            if (!string.Equals(memory, savedWindows, StringComparison.Ordinal))
            {
                await history.SetStateValueAsync(CmdHistory.WindowsStateName, memory);
                savedWindows = memory;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"Reading Command Prompt windows' history failed ({ex.GetType().Name}: {ex.Message}).");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Stops the timer and waits for a read in flight. The kept list stays (exit is not "off").</summary>
    /// <returns>A task completing when stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (timer is not null)
        {
            await timer.DisposeAsync();
            timer = null;
        }

        await gate.WaitAsync();
        gate.Release();
    }

    /// <summary>
    /// The interactive cmd.exe processes of this session, with their start times (process ids are reused).
    /// </summary>
    /// <returns>Process id and start ticks of each window to read.</returns>
    private static List<(uint ProcessId, long StartTicks)> FindInteractiveCmdWindows()
    {
        var processes = SnapshotProcesses();
        uint session = NativeMethods.ProcessIdToSessionId((uint)Environment.ProcessId, out var own) ? own : uint.MaxValue;
        var windows = new List<(uint, long)>();
        foreach (var (pid, info) in processes)
        {
            if (!info.Name.Equals(ConsoleCommandHistory.CmdExeName, StringComparison.OrdinalIgnoreCase)
                || !NativeMethods.ProcessIdToSessionId(pid, out var processSession) || processSession != session
                || !InteractiveConsoles.IsInteractive(pid, processes))
            {
                continue;
            }

            try
            {
                using var process = Process.GetProcessById((int)pid);
                windows.Add((pid, process.StartTime.ToUniversalTime().Ticks));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Exited meanwhile, or not ours to query: nothing to read.
            }
        }

        return windows;
    }

    /// <summary>A snapshot of the running processes: id → image name and parent id.</summary>
    /// <returns>The processes (empty when the snapshot failed).</returns>
    private static unsafe Dictionary<uint, (string Name, uint Parent)> SnapshotProcesses()
    {
        var processes = new Dictionary<uint, (string, uint)>();
        nint snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snapshot == NativeMethods.INVALID_HANDLE_VALUE)
        {
            return processes;
        }

        try
        {
            var entry = new NativeMethods.PROCESSENTRY32W { dwSize = (uint)sizeof(NativeMethods.PROCESSENTRY32W) };
            for (bool more = NativeMethods.Process32First(snapshot, ref entry); more; more = NativeMethods.Process32Next(snapshot, ref entry))
            {
                processes[entry.th32ProcessID] = (new string(entry.szExeFile), entry.th32ParentProcessID);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return processes;
    }

    /// <summary>Reads windows through helpers, <see cref="MaxParallelReads"/> at a time.</summary>
    /// <param name="windows">The windows.</param>
    /// <returns>Each window that could be read, with its commands (oldest first).</returns>
    private async Task<List<((uint, long) Window, IReadOnlyList<string> Commands)>> ReadWindowsAsync(List<(uint ProcessId, long StartTicks)> windows)
    {
        var results = new List<((uint, long), IReadOnlyList<string>)>();
        using var throttle = new SemaphoreSlim(MaxParallelReads);
        var tasks = windows.Select(async window =>
        {
            await throttle.WaitAsync();
            try
            {
                var commands = await ReadWithHelperAsync(window.ProcessId);
                if (commands is not null)
                {
                    lock (results)
                    {
                        results.Add((window, commands));
                    }
                }
            }
            finally
            {
                throttle.Release();
            }
        });
        await Task.WhenAll(tasks);
        return results;
    }

    /// <summary>
    /// Runs one helper (<c>--read-console-history &lt;pid&gt;</c>) and reads what it writes. A helper that hangs is
    /// ended after <see cref="HelperTimeout"/>.
    /// </summary>
    /// <param name="processId">The cmd.exe.</param>
    /// <returns>Its commands, oldest first, or <see langword="null"/> when it could not be read.</returns>
    private async Task<IReadOnlyList<string>?> ReadWithHelperAsync(uint processId)
    {
        var start = new ProcessStartInfo(helperPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,

            // The helper is the GUI exe: no console window either way; this keeps a console exe (tests) hidden too.
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(ConsoleCommandHistory.HelperSwitch);
        start.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        using var timeout = new CancellationTokenSource(HelperTimeout);
        Process? helper = null;
        try
        {
            helper = Process.Start(start);
            if (helper is null)
            {
                return null;
            }

            using var output = new MemoryStream();
            await helper.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
            await helper.WaitForExitAsync(timeout.Token);
            return helper.ExitCode == ConsoleCommandHistory.ExitOk ? ConsoleCommandHistory.Decode(output.ToArray()) : null;
        }
        catch (OperationCanceledException)
        {
            AppLog.Warn("A Command Prompt history helper did not finish in time and was ended.");
            TryKill(helper);
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            AppLog.Warn($"Starting a Command Prompt history helper failed: {ex.Message}");
            TryKill(helper);
            return null;
        }
        finally
        {
            helper?.Dispose();
        }
    }

    /// <summary>Ends a helper that is still running (our own child: it only reads).</summary>
    /// <param name="helper">The helper, or <see langword="null"/>.</param>
    private static void TryKill(Process? helper)
    {
        try
        {
            if (helper is { HasExited: false })
            {
                helper.Kill();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Exited meanwhile.
        }
    }

    /// <summary>Whether two kept lists hold the same commands with the same counts and times.</summary>
    /// <param name="a">One list.</param>
    /// <param name="b">The other.</param>
    /// <returns><see langword="true"/> when equal (in order).</returns>
    private static bool SameList(IReadOnlyList<KeptCommand> a, IReadOnlyList<KeptCommand> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First == p.Second);
}
