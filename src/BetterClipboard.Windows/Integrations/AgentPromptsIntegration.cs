using System.Diagnostics;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Prompts;
using BetterClipboard.Core.Services;
using BetterClipboard.Windows.Interop;
using ZstdSharp;

namespace BetterClipboard.Windows.Integrations;

/// <summary>
/// Keeps one agent's prompts (Claude Code or Codex) in the prompt archive: a first import of what the agent still has,
/// then every new prompt the moment it lands in the agent's files — reading only the new bytes (CLAUDE.md §2.21).
/// </summary>
/// <remarks>
/// <para>
/// <b>Knowing when.</b> Three triggers, because no single one sees everything:
/// <list type="bullet">
/// <item>Folder watchers (<c>ReadDirectoryChangesW</c> through <see cref="FileSystemWatcher"/>) see files created, renamed
/// (archived, compressed), deleted, and appended to by writers that close the file after each write — Claude Code's and
/// Codex's histories.</item>
/// <item>A poll of the files being written (<see cref="HotPollInterval"/>): a file kept open for appending raises no change
/// notification at all and keeps its old size in the folder (measured: 8 appends, 0 events, 0 bytes listed), which is how
/// Codex writes its session files. The poll opens each such file and compares its real length with the checkpoint;
/// nothing is read unless it moved. "Being written" = grew within <see cref="HotFor"/>, or a thread started within it.</item>
/// <item>A reconcile pass (at start, every <see cref="ReconcileInterval"/>, when the tab opens, after a watcher overflow):
/// every file opened once and its fingerprints checked — catches what happened while BetterClipboard was not running, and
/// an old Codex thread resumed without its file ever being "hot".</item>
/// </list>
/// </para>
/// <para>
/// <b>Knowing what.</b> <see cref="JsonlTail"/> proves a file was only appended to and reads the new lines, or reads it again
/// from its start when it was trimmed, pruned, filtered or replaced (the archive then keeps only prompts newer than the old
/// high-water mark). Codex session files of threads that are not the user's are read up to their first line, once.
/// </para>
/// <para>
/// <b>Cost.</b> All reading happens on one thread in Windows' background mode (lower CPU, I/O and memory priority), one file
/// at a time; writes go through the history worker in slices (<see cref="ClipHistoryService.PromptSliceSize"/>). Nothing
/// is ever written to the agents' folders, and no log line names a file or quotes a prompt (counts only).
/// </para>
/// </remarks>
public sealed class AgentPromptsIntegration : IAsyncDisposable
{
    /// <summary>How often files being written are checked (a few file opens; nothing is read unless one grew).</summary>
    public static readonly TimeSpan HotPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>How often every file is checked.</summary>
    public static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(5);

    /// <summary>How long a file counts as being written after it last grew (or its thread started).</summary>
    public static readonly TimeSpan HotFor = TimeSpan.FromHours(6);

    /// <summary>The quiet time after a change before reading: an agent's write arrives as several notifications.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

    private readonly ClipHistoryService history;
    private readonly AgentPromptSource source;
    private readonly TimeProvider time;

    /// <summary>Guards the request state (<see cref="dirty"/>, the flags, the pass counters, <see cref="waiters"/>) and <see cref="watchers"/>.</summary>
    private readonly object gate = new();

    /// <summary>Paths a watcher reported since the last pass.</summary>
    private readonly HashSet<string> dirty = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Catch-up callers waiting for a pass (completed when <see cref="completedPass"/> reaches their pass).</summary>
    private readonly List<(long Pass, TaskCompletionSource Done)> waiters = [];

    /// <summary>Tracked files by key. Worker thread only.</summary>
    private readonly Dictionary<string, FileTrack> tracks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Wakes the worker.</summary>
    private readonly AutoResetEvent wake = new(false);

    private readonly List<FileSystemWatcher> watchers = [];
    private bool reconcileRequested;
    private bool hotRequested;
    private long requestedPass;
    private long completedPass;
    private Thread? worker;
    private CancellationTokenSource? stop;
    private Timer? hotTimer;
    private Timer? reconcileTimer;
    private volatile bool importing;
    private volatile int trackedFiles;
    private volatile int excludedFiles;
    private volatile int addedThisSession;
    private volatile int importDone;
    private volatile int importTotal;
    private bool disposed;

    /// <summary>
    /// Creates the integration; nothing runs until <see cref="SetEnabledAsync"/>.
    /// </summary>
    /// <param name="history">The history service (the archive's writer and reader).</param>
    /// <param name="source">The agent's files.</param>
    /// <param name="time">Clock (tests); <see langword="null"/> = system.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public AgentPromptsIntegration(ClipHistoryService history, AgentPromptSource source, TimeProvider? time = null)
    {
        this.history = history ?? throw new ArgumentNullException(nameof(history));
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Raised (any thread) when the reading started or stopped, a first import made progress or finished, or prompts were
    /// added; Settings refreshes its status line, the panel its tab.
    /// </summary>
    public event EventHandler? StatusChanged;

    /// <summary>The agent.</summary>
    public PromptAgent Agent => source.Agent;

    /// <summary>The agent's folder.</summary>
    public string Root => source.Root;

    /// <summary>Whether the reading runs.</summary>
    public bool IsRunning => worker is not null;

    /// <summary>Whether the first import of the agent's files is in progress.</summary>
    public bool IsImporting => importing && worker is not null;

    /// <summary>Files read so far in the running first import, and how many it has (0 and 0 when none runs).</summary>
    public (int Done, int Total) ImportProgress => (importDone, importTotal);

    /// <summary>Files tracked (read at least once).</summary>
    public int TrackedFiles => trackedFiles;

    /// <summary>Codex session files left out because their thread is not the user's (agents, <c>codex exec</c>).</summary>
    public int ExcludedFiles => excludedFiles;

    /// <summary>Prompts added to the archive since the app started (the first import included).</summary>
    public int AddedThisSession => addedThisSession;

    /// <summary>Whether the agent's folder holds anything to read (cheap; for the panel's tab).</summary>
    /// <returns><see langword="true"/> when a tracked file exists.</returns>
    public bool HasData() => source.HasData();

    /// <summary>
    /// Starts reading (watchers, polls, a first pass — the first import when the archive has none for this agent) or
    /// stops it. Idempotent. Stopping keeps the archive and the checkpoints: on again catches up from where it stopped.
    /// </summary>
    /// <param name="enabled">Desired state.</param>
    /// <returns>A task completing when applied (a running pass is stopped first); failures are logged, never thrown.</returns>
    public async Task SetEnabledAsync(bool enabled)
    {
        if (disposed)
        {
            return;
        }

        if (enabled && worker is null)
        {
            Start();
        }
        else if (!enabled && worker is not null)
        {
            await StopAsync();
            AppLog.Info($"{PromptAgents.NameOf(Agent)} prompts: reading stopped (the stored prompts stay).");
        }

        RaiseStatusChanged();
    }

    /// <summary>
    /// Brings the archive up to date now (the tab is opening): checks the histories and the files being written, and waits
    /// for that pass — at most <paramref name="maxWait"/>, so a slow disk never holds the panel up.
    /// </summary>
    /// <param name="maxWait">How long to wait for the pass.</param>
    /// <returns>A task completing when the pass ended or the wait ran out.</returns>
    public async Task CatchUpAsync(TimeSpan maxWait)
    {
        Task done;
        lock (gate)
        {
            if (worker is null)
            {
                return;
            }

            hotRequested = true;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add((++requestedPass, completion));
            done = completion.Task;
        }

        wake.Set();
        try
        {
            await done.WaitAsync(maxWait);
        }
        catch (TimeoutException)
        {
            // Still reading (a first import): the tab shows what is stored so far and reloads when prompts are added.
        }
    }

    /// <summary>Asks for a full reconcile pass (Settings opened, a watcher lost events).</summary>
    public void RequestReconcile()
    {
        lock (gate)
        {
            reconcileRequested = true;
            requestedPass++;
        }

        wake.Set();
    }

    /// <summary>Stops reading and waits for the worker (exit keeps everything, like "off").</summary>
    /// <returns>A task completing when stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await StopAsync();
        wake.Dispose();
    }

    /// <summary>Creates the watchers, timers and the worker thread (the first pass is requested before it runs).</summary>
    private void Start()
    {
        stop = new CancellationTokenSource();
        lock (gate)
        {
            reconcileRequested = true;
            requestedPass++;
        }

        EnsureWatchers();
        hotTimer = new Timer(_ => RequestHot(), null, HotPollInterval, HotPollInterval);
        reconcileTimer = new Timer(_ => RequestReconcile(), null, ReconcileInterval, ReconcileInterval);
        var token = stop.Token;
        worker = new Thread(() => Run(token))
        {
            IsBackground = true,
            Name = $"{PromptAgents.NameOf(Agent)} prompts",
        };
        worker.Start();

        // The first pass was requested above; without this signal it would wait for the first poll tick.
        wake.Set();
        AppLog.Info($"{PromptAgents.NameOf(Agent)} prompts: reading started.");
    }

    /// <summary>Stops the worker (it finishes the file it is reading, then exits), the timers and the watchers.</summary>
    /// <returns>A task completing when stopped.</returns>
    private async Task StopAsync()
    {
        var thread = worker;
        if (thread is null)
        {
            return;
        }

        stop?.Cancel();
        wake.Set();
        if (hotTimer is not null)
        {
            await hotTimer.DisposeAsync();
        }

        if (reconcileTimer is not null)
        {
            await reconcileTimer.DisposeAsync();
        }

        hotTimer = reconcileTimer = null;
        lock (gate)
        {
            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }

            watchers.Clear();
        }

        // The worker may be waiting on the history worker (a slice being stored): give it time, on the pool.
        await Task.Run(() => thread.Join(TimeSpan.FromSeconds(10)));
        worker = null;
        stop?.Dispose();
        stop = null;
        importing = false;
        ReleaseWaiters(long.MaxValue);
    }

    /// <summary>Marks the files being written for a check (the poll timer).</summary>
    private void RequestHot()
    {
        lock (gate)
        {
            hotRequested = true;
            requestedPass++;
        }

        wake.Set();
    }

    /// <summary>
    /// The worker thread: background mode, the tracked files from the store, then passes until stopped. Synchronous on
    /// purpose — waiting for the history worker blocks this dedicated thread, never the pool or the UI.
    /// </summary>
    /// <param name="token">Stops the loop (and a read between chunks).</param>
    private void Run(CancellationToken token)
    {
        bool background = NativeMethods.SetThreadPriority(NativeMethods.GetCurrentThread(), NativeMethods.THREAD_MODE_BACKGROUND_BEGIN);
        try
        {
            LoadTracks();
            while (!token.IsCancellationRequested)
            {
                WaitHandle.WaitAny([wake, token.WaitHandle]);
                if (token.IsCancellationRequested)
                {
                    break;
                }

                // Let an agent's burst of notifications settle into one pass.
                if (token.WaitHandle.WaitOne(Debounce))
                {
                    break;
                }

                bool reconcile, hot;
                long pass;
                List<string> paths;
                lock (gate)
                {
                    (reconcile, hot, pass) = (reconcileRequested, hotRequested, requestedPass);
                    reconcileRequested = hotRequested = false;
                    paths = [.. dirty];
                    dirty.Clear();
                }

                RunPass(reconcile, hot, paths, token);
                ReleaseWaiters(pass);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Stopping.
        }
        catch (Exception ex)
        {
            // Never seen; logged so a broken archive shows in the log instead of silently stopping.
            AppLog.Error($"{PromptAgents.NameOf(Agent)} prompts: the reader stopped.", ex);
        }
        finally
        {
            if (background)
            {
                NativeMethods.SetThreadPriority(NativeMethods.GetCurrentThread(), NativeMethods.THREAD_MODE_BACKGROUND_END);
            }
        }
    }

    /// <summary>One pass: a full reconcile, or the hot files and the paths the watchers reported.</summary>
    /// <param name="reconcile">Check every file.</param>
    /// <param name="hot">Check the files being written.</param>
    /// <param name="paths">Paths from watcher events.</param>
    /// <param name="token">Stops between files.</param>
    private void RunPass(bool reconcile, bool hot, List<string> paths, CancellationToken token)
    {
        int added = addedThisSession;
        if (reconcile)
        {
            Reconcile(token);
        }
        else
        {
            // Watcher paths are verified (a rename can be anything); a polled file only needs the cheap length check.
            var files = new Dictionary<string, (AgentFile File, bool Verify)>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                if (source.Classify(path) is { } file)
                {
                    files[file.Key] = (file, true);
                }
            }

            if (hot)
            {
                foreach (var file in HotFiles())
                {
                    files.TryAdd(file.Key, (file, false));
                }
            }

            foreach (var (file, verify) in files.Values)
            {
                token.ThrowIfCancellationRequested();
                ProcessSafely(file, verify, token);
            }
        }

        if (addedThisSession != added)
        {
            RaiseStatusChanged();
        }
    }

    /// <summary>
    /// Checks every file of the agent (and re-creates watchers for folders that appeared). The pass of a first import marks
    /// the agent imported when it completes, so later prompts follow the pause rule.
    /// </summary>
    /// <param name="token">Stops between files.</param>
    private void Reconcile(CancellationToken token)
    {
        EnsureWatchers();
        List<AgentFile> files;
        try
        {
            files = [.. source.EnumerateFiles()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"{PromptAgents.NameOf(Agent)} prompts: listing the agent's folder failed ({ex.GetType().Name}).");
            return;
        }

        var watch = Stopwatch.StartNew();
        bool firstImport = importing;
        if (firstImport)
        {
            importDone = 0;
            importTotal = files.Count;
            RaiseStatusChanged();
        }

        int added = addedThisSession;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            ProcessSafely(file, verify: true, token);
            if (firstImport)
            {
                importDone++;

                // A few updates for a long import (Codex: hundreds of session files), not one per file.
                if (importDone % 50 == 0)
                {
                    RaiseStatusChanged();
                }
            }
        }

        if (firstImport)
        {
            history.SetStateValueAsync(PromptArchiveState.ImportedStateName(Agent), PromptArchiveState.FormatImported(time.GetUtcNow())).GetAwaiter().GetResult();
            importing = false;
            importDone = importTotal = 0;

            // Counts only, never a prompt or a file name.
            AppLog.Info($"{PromptAgents.NameOf(Agent)} prompts: first import done in {watch.Elapsed.TotalSeconds:0.0} s: {addedThisSession - added:N0} prompt(s) from {files.Count:N0} file(s) ({excludedFiles:N0} left out: not typed by you).");
            RaiseStatusChanged();
        }
    }

    /// <summary>The files to check on a poll: the histories, and session files of the user's threads written recently.</summary>
    /// <returns>The files at their last known paths.</returns>
    private IEnumerable<AgentFile> HotFiles()
    {
        var since = time.GetUtcNow() - HotFor;
        foreach (var track in tracks.Values)
        {
            if (track.Thread is { IsUserThread: false })
            {
                continue;
            }

            if (track.Kind != PromptSourceKind.CodexRollout || track.LastGrowthUtc >= since || track.Checkpoint.LastWriteUtc >= since || track.Thread?.StartedUtc >= since)
            {
                yield return new AgentFile(track.Path, track.Kind, track.Key, track.Path.EndsWith(CodexSessions.CompressedSuffix, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    /// <summary>Processes one file, logging (content-free) instead of throwing: one unreadable file must not stop a pass.</summary>
    /// <param name="file">The file.</param>
    /// <param name="verify">Check fingerprints even when the length did not change (reconcile, watcher events).</param>
    /// <param name="token">Stops the read.</param>
    private void ProcessSafely(AgentFile file, bool verify, CancellationToken token)
    {
        try
        {
            ProcessFile(file, verify, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Moved (archived, compressed) or deleted meanwhile: its new name arrives as its own event or in the next pass.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ZstdException)
        {
            AppLog.Warn($"{PromptAgents.NameOf(Agent)} prompts: reading a {file.Kind} file failed ({ex.GetType().Name}: {ex.Message}); it is read again at its next change.");
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            // Storing failed (or the history is shutting down): the checkpoint did not move, so the read is repeated.
            AppLog.Warn($"{PromptAgents.NameOf(Agent)} prompts: storing what a {file.Kind} file holds failed ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Reads what is new in one file and stores it (see the class remarks).
    /// </summary>
    /// <param name="file">The file.</param>
    /// <param name="verify">Check fingerprints even when length and id did not change.</param>
    /// <param name="token">Stops the read between chunks.</param>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access was denied.</exception>
    /// <exception cref="ZstdException">A compressed session file is corrupt.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> was cancelled.</exception>
    private void ProcessFile(AgentFile file, bool verify, CancellationToken token)
    {
        tracks.TryGetValue(file.Key, out var track);
        using var stream = PromptFileAccess.WithLockRetries(() => PromptFileAccess.OpenShared(file.Path), token);
        var state = PromptFileAccess.StateOf(stream);
        bool samePath = track is not null && string.Equals(track.Path, file.Path, StringComparison.OrdinalIgnoreCase);

        // A thread that is not the user's: its first line was read once; only its size is followed, never its lines.
        if (track?.Thread is { IsUserThread: false } excluded)
        {
            if (!samePath || track.Checkpoint.Length != state.Length || track.Checkpoint.FileId != state.FileId)
            {
                StoreExcluded(file, state, excluded);
            }

            return;
        }

        // A poll: same length, same file, same place — nothing was appended (a rewrite to the same length and the same id
        // is something no agent does; the reconcile pass verifies the fingerprints anyway).
        if (!verify && track is not null && samePath && track.Checkpoint.Length == state.Length && track.Checkpoint.FileId == state.FileId)
        {
            return;
        }

        var content = PromptFileAccess.ContentOf(stream, file.Compressed);
        try
        {
            var thread = track?.Thread;
            if (file.Kind == PromptSourceKind.CodexRollout && thread is null)
            {
                thread = CodexSessions.ReadThread(PromptFileAccess.ReadFirstLine(content), Path.GetFileName(file.Path));
                if (!thread.IsUserThread)
                {
                    StoreExcluded(file, state, thread);
                    return;
                }

                // Back to the start for the read below: a plain file is seeked by the reader itself, a decompressing
                // stream has to start over.
                if (file.Compressed)
                {
                    content.Dispose();
                    stream.Seek(0, SeekOrigin.Begin);
                    content = PromptFileAccess.ContentOf(stream, compressed: true);
                }
            }

            ReadAndStore(file, state, track, thread, content, token);
        }
        finally
        {
            if (!ReferenceEquals(content, stream))
            {
                content.Dispose();
            }
        }
    }

    /// <summary>Reads the new lines of a file (or all of it after a rewrite) and stores the prompts with the new checkpoint.</summary>
    /// <param name="file">The file.</param>
    /// <param name="state">Its state from the open handle.</param>
    /// <param name="track">What was known about it, or <see langword="null"/>.</param>
    /// <param name="thread">Its Codex thread, or <see langword="null"/>.</param>
    /// <param name="content">The content stream.</param>
    /// <param name="token">Stops the read between chunks.</param>
    /// <exception cref="IOException">Reading failed.</exception>
    private void ReadAndStore(AgentFile file, TailFileState state, FileTrack? track, CodexThread? thread, Stream content, CancellationToken token)
    {
        var prompts = new List<AgentPrompt>();
        var result = JsonlTail.Read(content, state, track?.Checkpoint, line =>
        {
            if (source.ParseLine(file, line, thread) is { } prompt)
            {
                prompts.Add(prompt);
            }
        }, token);

        bool samePath = track is not null && string.Equals(track.Path, file.Path, StringComparison.OrdinalIgnoreCase);
        if (result.Change == TailChange.Unchanged && track is not null && samePath && result.Checkpoint.Length == track.Checkpoint.Length)
        {
            return;
        }

        var batch = new PromptBatch
        {
            Agent = Agent,
            Kind = file.Kind,
            FileKey = file.Key,
            Path = file.Path,
            Checkpoint = result.Checkpoint,
            Prompts = prompts,
            IsImport = importing,
            RewrittenAfterUtc = result.IsReset ? track?.Checkpoint.HighWaterUtc : null,
            Thread = thread,
        };
        var stored = history.IngestPromptsAsync(batch).GetAwaiter().GetResult();

        // Mirror the store's high-water mark (the newest prompt read from the file, stored or not).
        var high = result.Checkpoint.HighWaterUtc;
        foreach (var prompt in prompts)
        {
            if (high is null || prompt.SentUtc > high)
            {
                high = prompt.SentUtc;
            }
        }

        var now = time.GetUtcNow();
        tracks[file.Key] = new FileTrack(file.Key, file.Kind, file.Path, result.Checkpoint with { HighWaterUtc = high }, thread,
            result.BytesRead > 0 ? now : track?.LastGrowthUtc);
        trackedFiles = tracks.Count;
        if (stored.Added > 0)
        {
            addedThisSession += stored.Added;
        }

        if (result.IsReset)
        {
            // Content-free: what happened and how many, never the file or a prompt.
            AppLog.Info($"{PromptAgents.NameOf(Agent)} prompts: a {file.Kind} file was {result.Change.ToString().ToLowerInvariant()} and read again from its start ({stored.Added:N0} new, {stored.Merged + stored.Skipped:N0} known).");
        }
    }

    /// <summary>
    /// Stores the checkpoint of a session file whose thread is not the user's: its whole length counts as read, its thread
    /// is remembered (so its lines are never read), and no prompt is stored.
    /// </summary>
    /// <param name="file">The file.</param>
    /// <param name="state">Its state.</param>
    /// <param name="thread">Its thread (<see cref="CodexThread.IsUserThread"/> is <see langword="false"/>).</param>
    private void StoreExcluded(AgentFile file, TailFileState state, CodexThread thread)
    {
        var checkpoint = new TailCheckpoint { Offset = state.Length, Length = state.Length, LastWriteUtc = state.LastWriteUtc, FileId = state.FileId };
        history.IngestPromptsAsync(new PromptBatch
        {
            Agent = Agent,
            Kind = file.Kind,
            FileKey = file.Key,
            Path = file.Path,
            Checkpoint = checkpoint,
            SkipPrompts = true,
            Thread = thread,
        }).GetAwaiter().GetResult();
        tracks[file.Key] = new FileTrack(file.Key, file.Kind, file.Path, checkpoint, thread, null);
        trackedFiles = tracks.Count;
        excludedFiles = tracks.Values.Count(t => t.Thread is { IsUserThread: false });
    }

    /// <summary>Loads the tracked files and the first-import state from the store (worker thread, at start).</summary>
    private void LoadTracks()
    {
        tracks.Clear();
        foreach (var file in history.GetPromptFilesAsync(Agent).GetAwaiter().GetResult())
        {
            tracks[file.FileKey] = new FileTrack(file.FileKey, file.Kind, file.Path, file.Checkpoint, file.Thread, null);
        }

        trackedFiles = tracks.Count;
        excludedFiles = tracks.Values.Count(t => t.Thread is { IsUserThread: false });
        importing = history.GetStateValueAsync(PromptArchiveState.ImportedStateName(Agent)).GetAwaiter().GetResult() is null;
        if (importing)
        {
            RaiseStatusChanged();
        }
    }

    /// <summary>
    /// Watches every watched folder that exists and is not watched yet. A folder created later (Codex's archive folder) is
    /// picked up by the next reconcile pass.
    /// </summary>
    private void EnsureWatchers()
    {
        lock (gate)
        {
            if (stop is null || stop.IsCancellationRequested)
            {
                return;
            }

            foreach (var watch in source.Watches)
            {
                if (!Directory.Exists(watch.Folder)
                    || watchers.Any(w => string.Equals(w.Path, watch.Folder, StringComparison.OrdinalIgnoreCase) && w.IncludeSubdirectories == watch.Recursive))
                {
                    continue;
                }

                try
                {
                    var watcher = new FileSystemWatcher(watch.Folder, watch.Filter)
                    {
                        IncludeSubdirectories = watch.Recursive,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime,

                        // A burst of new session files and their writes fits; an overflow is answered with a reconcile.
                        InternalBufferSize = 64 * 1024,
                    };
                    watcher.Changed += OnFileEvent;
                    watcher.Created += OnFileEvent;
                    watcher.Renamed += OnRenamed;
                    watcher.Error += (_, e) =>
                    {
                        AppLog.Warn($"{PromptAgents.NameOf(Agent)} prompts: a folder watcher lost events ({e.GetException().GetType().Name}); checking every file.");
                        RequestReconcile();
                    };
                    watcher.EnableRaisingEvents = true;
                    watchers.Add(watcher);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
                {
                    // The folder vanished between the check and the watch, or cannot be watched: the polls still cover it.
                    AppLog.Warn($"{PromptAgents.NameOf(Agent)} prompts: watching a folder failed ({ex.GetType().Name}).");
                }
            }
        }
    }

    /// <summary>A file was created or changed (watcher thread): queue it for the next pass.</summary>
    /// <param name="sender">The watcher.</param>
    /// <param name="e">The event.</param>
    private void OnFileEvent(object sender, FileSystemEventArgs e) => QueuePath(e.FullPath);

    /// <summary>A file was renamed (moved into an archive folder, compressed, renamed over): queue its new name.</summary>
    /// <param name="sender">The watcher.</param>
    /// <param name="e">The event.</param>
    private void OnRenamed(object sender, RenamedEventArgs e) => QueuePath(e.FullPath);

    /// <summary>Queues a path for the next pass and wakes the worker.</summary>
    /// <param name="path">The path.</param>
    private void QueuePath(string path)
    {
        lock (gate)
        {
            dirty.Add(path);
            requestedPass++;
        }

        wake.Set();
    }

    /// <summary>Completes the catch-up waiters whose pass is done.</summary>
    /// <param name="pass">The pass that just completed (<see cref="long.MaxValue"/> to release everyone, when stopping).</param>
    private void ReleaseWaiters(long pass)
    {
        List<TaskCompletionSource> done;
        lock (gate)
        {
            completedPass = Math.Max(completedPass, pass);
            done = [.. waiters.Where(w => w.Pass <= completedPass).Select(w => w.Done)];
            waiters.RemoveAll(w => w.Pass <= completedPass);
        }

        foreach (var completion in done)
        {
            completion.TrySetResult();
        }
    }

    /// <summary>Raises <see cref="StatusChanged"/>, shielding the worker from subscriber exceptions.</summary>
    private void RaiseStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Error("A prompt archive status subscriber threw.", ex);
        }
    }

    /// <summary>What is known about one tracked file.</summary>
    /// <param name="Key">Its tracking key.</param>
    /// <param name="Kind">Its kind.</param>
    /// <param name="Path">Where it was last seen.</param>
    /// <param name="Checkpoint">How far it was read.</param>
    /// <param name="Thread">Its Codex thread, or <see langword="null"/>.</param>
    /// <param name="LastGrowthUtc">When new bytes were last read from it in this session, or <see langword="null"/>.</param>
    private sealed record FileTrack(string Key, PromptSourceKind Kind, string Path, TailCheckpoint Checkpoint, CodexThread? Thread, DateTimeOffset? LastGrowthUtc);
}
