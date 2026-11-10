using System.Runtime.InteropServices;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.NotepadPlusPlus;
using BetterClipboard.Windows.Interop;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Integrations;

/// <summary>What the file watcher knows about one running Notepad++ (for the status line).</summary>
/// <param name="ProcessId">Its process.</param>
/// <param name="Version">Its version, when it answered.</param>
/// <param name="OpenFiles">
/// Files open at the last look (<see cref="NotepadPlusPlusOpenTracker.OpenFileCount"/>: untitled tabs and Notepad++'s hidden
/// placeholder document not counted).
/// </param>
/// <param name="Problem">Why it cannot be asked (runs as administrator, not signed by Notepad++), or <see langword="null"/>.</param>
public sealed record NotepadPlusPlusInstance(uint ProcessId, string? Version, int OpenFiles, string? Problem);

/// <summary>
/// Sees every file opened in a running Notepad++ the moment it opens, by asking Notepad++ itself through its plugin messages
/// (the owner's "Ask Notepad++ (exact)", 2026-11-08; CLAUDE.md §2.41), and hands each opened file's path over.
/// </summary>
/// <remarks>
/// <para>
/// <b>When it looks.</b> At once when Notepad++'s title changes — opening a file brings it to the front, which changes the
/// title (a WinEvent hook on that one process; no polling) — and every <see cref="ScanInterval"/>: a scan finds Notepad++
/// windows that appeared or went, and asks each one how many documents it has open (one message), which also catches files
/// opened in the background (Find in Files' "Open all"). A look whose title and count did not change asks nothing more.
/// </para>
/// <para>
/// <b>What a look does:</b> lists the open documents' buffer ids and the one in front, reads the paths of the new ones (and of
/// the one in front, to see a Save As) through <see cref="NotepadPlusPlusClient"/>, and lets
/// <see cref="NotepadPlusPlusOpenTracker"/> decide which are openings — never the session Notepad++ restores at its start, but
/// the files it was started with when BetterClipboard saw it start.
/// </para>
/// <para>
/// <b>Threads.</b> Everything runs on one dedicated window thread (the hook needs its message loop, and the clients are not
/// thread-safe); the opened paths are handed over from it without waiting (the history service queues them in order).
/// </para>
/// </remarks>
public sealed class NotepadPlusPlusFileWatcher : IDisposable
{
    /// <summary>How often running Notepad++ windows are found and their document counts asked.</summary>
    public static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(5);

    /// <summary>Timer id of the periodic scan.</summary>
    private const nuint ScanTimerId = 1;

    /// <summary>Timer id of the short wait after a title change (several changes in a row become one look).</summary>
    private const nuint LookTimerId = 2;

    /// <summary>How long after a title change the look runs.</summary>
    private const uint LookDelayMs = 150;

    private readonly Func<string, string, DateTimeOffset, Task> deliver;
    private readonly Func<uint, NotepadPlusPlusTrust>? verify;
    private readonly string windowClass;
    private readonly TimeProvider time;
    private readonly Dictionary<uint, Watched> processes = [];
    private readonly Dictionary<uint, string> problems = [];
    private readonly WinEventProc hookCallback;
    private readonly nint hookPointer;
    private MessageWindowThread? thread;
    private DateTimeOffset startedUtc;
    private volatile IReadOnlyList<NotepadPlusPlusInstance> instances = [];
    private int recordedThisSession;
    private bool disposed;

    /// <summary>Creates the watcher; nothing happens until <see cref="Start"/>.</summary>
    /// <param name="deliver">Records an opened file: (path, Notepad++'s executable, when). Called on the watcher's thread; must not block.</param>
    /// <param name="verify">The owner check (tests pass their own); defaults to <see cref="NotepadPlusPlusOwnerVerifier.VerifyProcess"/>.</param>
    /// <param name="windowClass">The window class to find (tests use their own); defaults to Notepad++'s.</param>
    /// <param name="time">The clock; defaults to the system clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deliver"/> is <see langword="null"/>.</exception>
    public NotepadPlusPlusFileWatcher(Func<string, string, DateTimeOffset, Task> deliver, Func<uint, NotepadPlusPlusTrust>? verify = null, string windowClass = NotepadPlusPlusClient.WindowClass, TimeProvider? time = null)
    {
        this.deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
        this.verify = verify;
        this.windowClass = windowClass;
        this.time = time ?? TimeProvider.System;

        // Kept in a field for the hook's lifetime: the pointer handed to Windows must never outlive the delegate.
        hookCallback = OnWinEvent;
        hookPointer = Marshal.GetFunctionPointerForDelegate(hookCallback);
    }

    /// <summary>Raised on the watcher's thread after a look changed what is known (instances, problems, a file recorded).</summary>
    public event EventHandler? StatusChanged;

    /// <summary>The running Notepad++ windows as of the last scan.</summary>
    public IReadOnlyList<NotepadPlusPlusInstance> Instances => instances;

    /// <summary>Files recorded as opened since the watcher started.</summary>
    public int RecordedThisSession => Volatile.Read(ref recordedThisSession);

    /// <summary>Starts the watcher's thread, scans at once, and then every <see cref="ScanInterval"/>.</summary>
    /// <exception cref="ObjectDisposedException">Disposed.</exception>
    /// <exception cref="InvalidOperationException">Already started.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (thread is not null)
        {
            throw new InvalidOperationException("The Notepad++ file watcher was already started.");
        }

        startedUtc = time.GetUtcNow();
        thread = new MessageWindowThread("Notepad++ files") { MessageHandler = OnMessage };
        thread.Post(() =>
        {
            SetTimer(thread.Handle, ScanTimerId, (uint)ScanInterval.TotalMilliseconds, 0);
            Scan();
        });
    }

    /// <summary>Scans now (Settings opened, tests); returns once the scan ran on the watcher's thread.</summary>
    /// <returns>A task completing after the scan.</returns>
    public Task ScanNowAsync() => thread is { } running ? running.InvokeAsync(Scan) : Task.CompletedTask;

    /// <summary>Stops: unhooks, frees the buffers placed in Notepad++, ends the thread.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (thread is { } running)
        {
            try
            {
                running.InvokeAsync(() =>
                {
                    KillTimer(running.Handle, ScanTimerId);
                    KillTimer(running.Handle, LookTimerId);
                    foreach (var watched in processes.Values)
                    {
                        watched.Dispose();
                    }

                    processes.Clear();
                }).Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // The thread is gone already; nothing of it is left to clean.
            }

            running.Dispose();
        }

        GC.KeepAlive(hookCallback);
    }

    /// <summary>The window thread's messages: the two timers.</summary>
    /// <param name="message">The message.</param>
    /// <param name="wParam">The timer id for <c>WM_TIMER</c>.</param>
    /// <param name="lParam">Unused.</param>
    /// <returns>0 when handled; <see langword="null"/> otherwise.</returns>
    private nint? OnMessage(uint message, nint wParam, nint lParam)
    {
        if (message != WM_TIMER)
        {
            return null;
        }

        if ((nuint)wParam == ScanTimerId)
        {
            Scan();
        }
        else if ((nuint)wParam == LookTimerId && thread is { } running)
        {
            KillTimer(running.Handle, LookTimerId);
            foreach (var watched in processes.Values)
            {
                Look(watched, force: false);
            }
        }

        return 0;
    }

    /// <summary>Finds the Notepad++ windows, opens new ones, drops gone ones, and looks at each (watcher thread).</summary>
    private void Scan()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            var windows = NotepadPlusPlusClient.FindWindows(windowClass);
            var alive = windows.Select(w => w.ProcessId).ToHashSet();
            foreach (var gone in processes.Keys.Where(pid => !alive.Contains(pid)).ToList())
            {
                processes[gone].Dispose();
                processes.Remove(gone);
            }

            foreach (var gone in problems.Keys.Where(pid => !alive.Contains(pid)).ToList())
            {
                problems.Remove(gone);
            }

            foreach (var (window, processId) in windows)
            {
                if (!processes.ContainsKey(processId))
                {
                    Open(window, processId);
                }
            }

            foreach (var watched in processes.Values)
            {
                Look(watched, force: false);
            }

            Publish();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Looking for Notepad++ failed ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    /// <summary>Starts following one Notepad++: the client, its startup files, the title hook.</summary>
    /// <param name="window">Its main window.</param>
    /// <param name="processId">Its process.</param>
    private void Open(nint window, uint processId)
    {
        var client = NotepadPlusPlusClient.TryOpen(window, processId, verify, out var problem);
        if (client is null)
        {
            if (!problems.ContainsKey(processId))
            {
                // Content-free: the reason only (administrator, signer).
                AppLog.Info($"Not asking Notepad++ (pid {processId}) which files it opens: {problem}.");
            }

            problems[processId] = problem ?? NotepadPlusPlusClient.CouldNotBeAsked;
            return;
        }

        problems.Remove(processId);

        // A Notepad++ that started while BetterClipboard watched was started to open its command line's files; one that ran
        // before has nothing to add (what it opened then is history BetterClipboard did not see).
        IReadOnlyList<string> startupFiles = client.StartTime() is { } started && started >= startedUtc - TimeSpan.FromSeconds(10)
            ? NotepadPlusPlusCommandLine.FilesToOpen(client.CommandLine())
            : [];
        nint hook = SetWinEventHook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE, 0, hookPointer, processId, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        var watched = new Watched(client, new NotepadPlusPlusOpenTracker(startupFiles), hook, client.Version());
        processes[processId] = watched;

        // Content-free: the version, the process and how many files it was started with — never which.
        AppLog.Info($"Watching Notepad++ {watched.Version ?? "(version unknown)"} (pid {processId}) for opened files; started with {startupFiles.Count} file(s) to open.");
    }

    /// <summary>
    /// Looks at one Notepad++: when its title or document count changed (or it has no baseline yet), lists its documents and
    /// records the files it opened.
    /// </summary>
    /// <param name="watched">The Notepad++.</param>
    /// <param name="force">Look even when nothing seems to have changed.</param>
    private void Look(Watched watched, bool force)
    {
        try
        {
            var (exists, visible) = watched.Client.WindowState();
            if (!exists)
            {
                return;
            }

            string title = watched.Client.Title();
            int? count = watched.Client.OpenCount();
            if (!force && watched.Tracker.HasBaseline && count == watched.LastCount && title == watched.LastTitle)
            {
                return;
            }

            IReadOnlyList<string>? opened = null;
            if (!force && count is not null && count == watched.LastCount && watched.Client.CurrentId() is { } front)
            {
                // Only the title changed (a tab switch, a document becoming dirty): the document in front is enough, unless
                // it is one not seen before.
                opened = watched.Tracker.ObserveFront(front, watched.Client.PathOf);
            }

            watched.LastCount = count;
            watched.LastTitle = title;
            if (opened is null)
            {
                if (watched.Client.Documents() is not { } documents)
                {
                    // Busy or hung right now: the next look tries again.
                    watched.LastCount = null;
                    return;
                }

                opened = watched.Tracker.Observe(documents.Ids, documents.Current, visible, watched.Client.PathOf);
            }

            watched.OpenFiles = watched.Tracker.OpenFileCount;
            foreach (var path in opened)
            {
                Interlocked.Increment(ref recordedThisSession);
                _ = DeliverAsync(path, watched.Client.ExecutablePath);
            }

            if (opened.Count > 0)
            {
                // Content-free: how many, never which.
                AppLog.Info($"Notepad++: {opened.Count} file{(opened.Count == 1 ? string.Empty : "s")} opened.");
                Publish();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Asking Notepad++ about its documents failed ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    /// <summary>Hands one opened file over without letting a failure escape into the watcher's thread.</summary>
    /// <param name="path">The file.</param>
    /// <param name="executable">Notepad++'s executable.</param>
    /// <returns>A task completing once handed over (failures logged).</returns>
    private async Task DeliverAsync(string path, string executable)
    {
        try
        {
            await deliver(path, executable, time.GetUtcNow()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Recording a file opened in Notepad++ failed ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// The title-change hook (watcher thread, out of context): a change of a watched main window's own title schedules a look
    /// shortly after, so a burst of changes (several files opened at once) becomes one look.
    /// </summary>
    /// <param name="hook">The hook.</param>
    /// <param name="eventType">The event.</param>
    /// <param name="hwnd">The window.</param>
    /// <param name="idObject">0 for the window itself.</param>
    /// <param name="idChild">0 for the object itself.</param>
    /// <param name="eventThread">Unused.</param>
    /// <param name="eventTime">Unused.</param>
    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        try
        {
            if (idObject != 0 || idChild != 0 || thread is not { } running || !processes.Values.Any(w => w.Client.Window == hwnd))
            {
                return;
            }

            SetTimer(running.Handle, LookTimerId, LookDelayMs, 0);
        }
        catch (Exception ex)
        {
            // Never let an exception unwind into user32.
            AppLog.Warn($"A Notepad++ title event failed ({ex.GetType().Name}).");
        }
    }

    /// <summary>Publishes the instances for the status line and raises <see cref="StatusChanged"/> when they changed.</summary>
    private void Publish()
    {
        var snapshot = processes.Select(p => new NotepadPlusPlusInstance(p.Key, p.Value.Version, p.Value.OpenFiles, null))
            .Concat(problems.Select(p => new NotepadPlusPlusInstance(p.Key, null, 0, p.Value)))
            .OrderBy(i => i.ProcessId)
            .ToList();
        if (snapshot.SequenceEqual(instances))
        {
            return;
        }

        instances = snapshot;
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"A Notepad++ status subscriber failed ({ex.GetType().Name}).");
        }
    }

    /// <summary>One Notepad++ being followed.</summary>
    private sealed class Watched(NotepadPlusPlusClient client, NotepadPlusPlusOpenTracker tracker, nint hook, string? version) : IDisposable
    {
        /// <summary>The client.</summary>
        public NotepadPlusPlusClient Client { get; } = client;

        /// <summary>The tracker.</summary>
        public NotepadPlusPlusOpenTracker Tracker { get; } = tracker;

        /// <summary>The version it reported when opened.</summary>
        public string? Version { get; } = version;

        /// <summary>The document count at the last look.</summary>
        public int? LastCount { get; set; }

        /// <summary>The title at the last look.</summary>
        public string? LastTitle { get; set; }

        /// <summary>Files open at the last look (for the status line; untitled tabs not counted).</summary>
        public int OpenFiles { get; set; }

        /// <summary>Unhooks and frees the client's buffer.</summary>
        public void Dispose()
        {
            if (hook != 0)
            {
                UnhookWinEvent(hook);
            }

            Client.Dispose();
        }
    }
}
