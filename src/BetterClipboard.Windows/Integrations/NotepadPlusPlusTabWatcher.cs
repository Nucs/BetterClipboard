using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.NotepadPlusPlus;

namespace BetterClipboard.Windows.Integrations;

/// <summary>One version of an unsaved Notepad++ tab, read from its backup file.</summary>
/// <param name="Key">The tab's identity: its backup file's full path (compared case-insensitively).</param>
/// <param name="TabName">The tab's name ("new 3").</param>
/// <param name="Text">The tab's text.</param>
/// <param name="WrittenUtc">When Notepad++ wrote the backup.</param>
public sealed record NotepadPlusPlusTabVersion(string Key, string TabName, string Text, DateTimeOffset WrittenUtc);

/// <summary>Where <see cref="NotepadPlusPlusTabWatcher"/> hands what it finds (the integration; tests use a fake).</summary>
public interface INotepadPlusPlusTabTarget
{
    /// <summary>Records versions of tabs.</summary>
    /// <param name="versions">The versions.</param>
    /// <param name="isImport">Whether this is the first activation's reading of the tabs Notepad++ already remembered.</param>
    /// <returns>A task completing once recorded.</returns>
    Task RecordAsync(IReadOnlyList<NotepadPlusPlusTabVersion> versions, bool isImport);

    /// <summary>A tab was renamed: its backup file moved from <paramref name="oldKey"/> to <paramref name="newKey"/>.</summary>
    /// <param name="oldKey">The old key.</param>
    /// <param name="newKey">The new key.</param>
    /// <param name="newTitle">The tab's new name.</param>
    /// <returns>A task completing once moved.</returns>
    Task RenameAsync(string oldKey, string newKey, string newTitle);

    /// <summary>Tabs Notepad++ no longer remembers (closed, or saved as files).</summary>
    /// <param name="keys">Their keys.</param>
    /// <returns>A task completing once forgotten.</returns>
    Task ForgetAsync(IReadOnlyCollection<string> keys);
}

/// <summary>
/// Follows Notepad++'s unsaved tabs through its own files (CLAUDE.md §2.41): <c>session.xml</c> says which tabs exist and
/// which backup file holds each one's text; the backup files say when the text changed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read once a tab rests, never on every save</b> (the owner's "dont monitor every 7s", 2026-11-08). Notepad++ rewrites a
/// changed tab's backup every 7 seconds while the user types. A change only marks the tab; it is read once no change came for
/// <see cref="QuietTime"/>, or at the latest <see cref="MaxDelay"/> after its first unread change while it keeps changing.
/// Change notifications come from the folder (<see cref="FileSystemWatcher"/>), so nothing is polled; a timer only looks at
/// the marks every <see cref="Tick"/>.
/// </para>
/// <para>
/// <b>Never in Notepad++'s way.</b> Files are opened with every sharing mode granted and closed at once, so Notepad++ can always
/// write, replace and delete them; a file that changes while it is read is read again later. Only the session's untitled tabs
/// count: backups of saved files with unsaved changes, and backups the session no longer names, are not "new" tabs.
/// </para>
/// <para>
/// <b>Renames.</b> Notepad++ renames an untitled tab's backup with the tab and keeps the stamp, but rewrites
/// <c>session.xml</c> only later. The rename notification moves the tab at once; a rename missed while BetterClipboard was not
/// running is matched by its stamp when the session is read.
/// </para>
/// <para>Thread-safe; callbacks to the target are made one at a time on pool threads.</para>
/// </remarks>
public sealed class NotepadPlusPlusTabWatcher : IDisposable
{
    /// <summary>Most versions handed to the target in one call (a first import of hundreds of tabs goes in slices).</summary>
    public const int SliceSize = 50;

    private readonly IReadOnlyList<string> dataFolders;
    private readonly INotepadPlusPlusTabTarget target;
    private readonly Func<long> maxBytes;
    private readonly int ansiCodePage;
    private readonly TimeProvider time;
    private readonly Lock gate = new();
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly Dictionary<string, TabInfo> tabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTimeOffset First, DateTimeOffset Last)> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> watchedBackupFolders = new(StringComparer.OrdinalIgnoreCase);
    private ITimer? timer;
    private bool sessionDirty;
    private bool disposed;

    /// <summary>Creates the watcher; nothing is watched until <see cref="StartAsync"/>.</summary>
    /// <param name="dataFolders">Notepad++'s settings folders (each with its <c>session.xml</c>).</param>
    /// <param name="target">Where versions, renames and closed tabs go.</param>
    /// <param name="maxBytes">The history's largest item in bytes; bigger backups are not read.</param>
    /// <param name="ansiCodePage">The system's ANSI code page, for "ANSI" documents.</param>
    /// <param name="time">The clock (tests pass a fake one); defaults to the system clock.</param>
    /// <param name="quietTime">How long a tab must rest before it is read; default one minute.</param>
    /// <param name="maxDelay">The longest a changing tab waits; default five minutes.</param>
    /// <param name="tick">How often the marks are looked at; default five seconds.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public NotepadPlusPlusTabWatcher(IReadOnlyList<string> dataFolders, INotepadPlusPlusTabTarget target, Func<long> maxBytes, int ansiCodePage,
        TimeProvider? time = null, TimeSpan? quietTime = null, TimeSpan? maxDelay = null, TimeSpan? tick = null)
    {
        this.dataFolders = dataFolders ?? throw new ArgumentNullException(nameof(dataFolders));
        this.target = target ?? throw new ArgumentNullException(nameof(target));
        this.maxBytes = maxBytes ?? throw new ArgumentNullException(nameof(maxBytes));
        this.ansiCodePage = ansiCodePage;
        this.time = time ?? TimeProvider.System;
        QuietTime = quietTime ?? TimeSpan.FromMinutes(1);
        MaxDelay = maxDelay ?? TimeSpan.FromMinutes(5);
        Tick = tick ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Raised on a pool thread after the tabs were read again or a version was handed over (for the status line).</summary>
    public event EventHandler? Changed;

    /// <summary>How long a tab must rest (no new save) before its text is read.</summary>
    public TimeSpan QuietTime { get; }

    /// <summary>The longest a tab that keeps changing waits before it is read anyway.</summary>
    public TimeSpan MaxDelay { get; }

    /// <summary>How often the marks are looked at.</summary>
    public TimeSpan Tick { get; }

    /// <summary>How many unsaved tabs the sessions name.</summary>
    public int TabCount
    {
        get
        {
            lock (gate)
            {
                return tabs.Count;
            }
        }
    }

    /// <summary>How many tabs changed and wait to be read.</summary>
    public int PendingCount
    {
        get
        {
            lock (gate)
            {
                return pending.Count;
            }
        }
    }

    /// <summary>
    /// Starts watching, then reads every unsaved tab once (the catch-up: what changed while BetterClipboard did not watch).
    /// </summary>
    /// <param name="isImport">Whether this is the first activation (the tabs are an import, see <see cref="INotepadPlusPlusTabTarget.RecordAsync"/>).</param>
    /// <returns>A task completing once the catch-up was handed over.</returns>
    /// <exception cref="ObjectDisposedException">Disposed.</exception>
    public async Task StartAsync(bool isImport)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (gate)
        {
            foreach (var folder in dataFolders.Where(Directory.Exists))
            {
                watchers.Add(Watch(folder, "session.xml"));
            }
        }

        // Watch before reading: a change in between is then marked and read later rather than lost.
        await ReloadSessionAsync().ConfigureAwait(false);
        await ReadAllAsync(isImport).ConfigureAwait(false);
        timer = time.CreateTimer(_ => _ = ProcessDueAsync(), null, Tick, Tick);
    }

    /// <summary>Reads the marked tabs that are due (rested, or waited too long); also called by the timer.</summary>
    /// <returns>A task completing once they were handed over.</returns>
    public async Task ProcessDueAsync()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            await serial.WaitAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            bool reload;
            lock (gate)
            {
                reload = sessionDirty;
                sessionDirty = false;
            }

            if (reload)
            {
                await ReloadSessionCoreAsync().ConfigureAwait(false);
            }

            var now = time.GetUtcNow();
            List<(string Key, TabInfo Tab)> due;
            lock (gate)
            {
                due = pending
                    .Where(p => now - p.Value.Last >= QuietTime || now - p.Value.First >= MaxDelay)
                    .Where(p => tabs.ContainsKey(p.Key))
                    .Select(p => (p.Key, tabs[p.Key]))
                    .ToList();
            }

            var versions = new List<NotepadPlusPlusTabVersion>();
            foreach (var (key, tab) in due)
            {
                if (TryRead(key, tab) is { } version)
                {
                    versions.Add(version);
                    lock (gate)
                    {
                        pending.Remove(key);
                    }
                }
            }

            foreach (var slice in versions.Chunk(SliceSize))
            {
                await target.RecordAsync(slice, isImport: false).ConfigureAwait(false);
            }

            if (versions.Count > 0)
            {
                RaiseChanged();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Content-free: never a tab's name or text.
            AppLog.Warn($"Reading Notepad++'s tabs failed ({ex.GetType().Name}: {ex.Message}).");
        }
        finally
        {
            serial.Release();
        }
    }

    /// <summary>Stops watching (marks still waiting are dropped: the next start's catch-up reads those tabs).</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        shutdown.Cancel();
        timer?.Dispose();
        lock (gate)
        {
            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }

            watchers.Clear();
        }
    }

    /// <summary>Reads every unsaved tab now and hands them over in slices.</summary>
    /// <param name="isImport">Whether this is the first activation.</param>
    /// <returns>A task completing once handed over.</returns>
    private async Task ReadAllAsync(bool isImport)
    {
        List<(string Key, TabInfo Tab)> all;
        lock (gate)
        {
            all = tabs.Select(t => (t.Key, t.Value)).ToList();
        }

        var versions = new List<NotepadPlusPlusTabVersion>();
        foreach (var (key, tab) in all)
        {
            if (TryRead(key, tab) is { } version)
            {
                versions.Add(version);
            }
            else
            {
                // Busy or changing right now: read it once it rests.
                Mark(key);
            }
        }

        foreach (var slice in versions.Chunk(SliceSize))
        {
            await target.RecordAsync(slice, isImport).ConfigureAwait(false);
        }

        RaiseChanged();
    }

    /// <summary>Reads the sessions under the processing gate (startup; later reloads go through <see cref="ProcessDueAsync"/>).</summary>
    /// <returns>A task completing once read.</returns>
    private async Task ReloadSessionAsync()
    {
        await serial.WaitAsync(shutdown.Token).ConfigureAwait(false);
        try
        {
            await ReloadSessionCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            serial.Release();
        }
    }

    /// <summary>
    /// Reads every session again: the untitled tabs with a backup, renames matched by stamp, tabs gone, and the backup
    /// folders to watch. A session that cannot be read (half-written) keeps the tabs as they were.
    /// </summary>
    /// <returns>A task completing once the target heard about renames and closed tabs.</returns>
    private async Task ReloadSessionCoreAsync()
    {
        var found = new Dictionary<string, TabInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in dataFolders)
        {
            var path = Path.Combine(folder, "session.xml");
            IReadOnlyList<NotepadPlusPlusSessionFile> files;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                files = NotepadPlusPlusSession.Parse(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                // Missing, being written, or locked: keep what was known; the next change of the file reads it again.
                lock (gate)
                {
                    foreach (var (key, tab) in tabs)
                    {
                        if (tab.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase))
                        {
                            found[key] = tab;
                        }
                    }
                }

                continue;
            }

            foreach (var file in files)
            {
                if (!file.IsUntitled || file.BackupFilePath is not { } backup || !Path.IsPathFullyQualified(backup))
                {
                    continue;
                }

                var key = Path.GetFullPath(backup);
                found[key] = new TabInfo(folder, file.FileName, file.Encoding);
            }
        }

        List<(string Old, string New, string Name)> renames = [];
        List<string> gone;
        List<string> added;
        lock (gate)
        {
            // A rename that happened while nobody watched: one vanished key and one new key with the same stamp and folder.
            var vanished = tabs.Keys.Where(k => !found.ContainsKey(k)).ToList();
            added = found.Keys.Where(k => !tabs.ContainsKey(k)).ToList();
            foreach (var newKey in added.ToList())
            {
                var candidates = vanished.Where(old => SameStampAndFolder(old, newKey)).ToList();
                if (candidates.Count == 1)
                {
                    renames.Add((candidates[0], newKey, found[newKey].Name));
                    vanished.Remove(candidates[0]);
                    added.Remove(newKey);
                }
            }

            gone = vanished;
            tabs.Clear();
            foreach (var (key, tab) in found)
            {
                tabs[key] = tab;
            }

            foreach (var key in gone)
            {
                pending.Remove(key);
            }

            foreach (var folder in found.Keys.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (watchedBackupFolders.Add(folder) && Directory.Exists(folder) && !disposed)
                {
                    watchers.Add(Watch(folder, "*"));
                }
            }
        }

        foreach (var (oldKey, newKey, name) in renames)
        {
            await target.RenameAsync(oldKey, newKey, name).ConfigureAwait(false);
        }

        if (gone.Count > 0)
        {
            await target.ForgetAsync(gone).ConfigureAwait(false);
        }

        foreach (var key in added)
        {
            // A tab new since the last look: read once it rests (it is being written, most likely).
            Mark(key);
        }
    }

    /// <summary>Reads one tab's backup, or <see langword="null"/> when it cannot now (missing, too large, changing, locked).</summary>
    /// <param name="key">The backup's full path.</param>
    /// <param name="tab">The tab.</param>
    /// <returns>The version, or <see langword="null"/>.</returns>
    private NotepadPlusPlusTabVersion? TryRead(string key, TabInfo tab)
    {
        try
        {
            var before = new FileInfo(key);
            if (!before.Exists || before.Length > maxBytes())
            {
                return null;
            }

            byte[] bytes;
            using (var stream = new FileStream(key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }

            var after = new FileInfo(key);
            if (after.Length != before.Length || after.LastWriteTimeUtc != before.LastWriteTimeUtc)
            {
                // Notepad++ wrote while it was read (only older versions write in place): read it again later.
                return null;
            }

            var text = NotepadPlusPlusBackup.DecodeText(bytes, tab.Encoding, ansiCodePage);
            return new NotepadPlusPlusTabVersion(key, tab.Name, text, new DateTimeOffset(after.LastWriteTimeUtc, TimeSpan.Zero));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Creates one folder watcher (pool-thread events).</summary>
    /// <param name="folder">The folder.</param>
    /// <param name="filter">"session.xml" for a settings folder, "*" for a backup folder.</param>
    /// <returns>The running watcher.</returns>
    private FileSystemWatcher Watch(string folder, string filter)
    {
        var watcher = new FileSystemWatcher(folder, filter)
        {
            IncludeSubdirectories = false,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Deleted += OnChanged;
        watcher.Renamed += OnRenamed;
        watcher.Error += (_, _) => MarkEverything();
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    /// <summary>A file changed: the session is read again, or the tab whose backup it is gets a mark.</summary>
    /// <param name="sender">The watcher.</param>
    /// <param name="e">The event.</param>
    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (string.Equals(e.Name, "session.xml", StringComparison.OrdinalIgnoreCase))
        {
            lock (gate)
            {
                sessionDirty = true;
            }

            return;
        }

        if (e.Name is null || e.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || !NotepadPlusPlusBackup.TryParseName(e.Name, out _, out _))
        {
            // Notepad++'s temporary file before its swap, or not a backup at all.
            return;
        }

        var key = Path.GetFullPath(e.FullPath);
        bool known;
        lock (gate)
        {
            known = tabs.ContainsKey(key);
            if (!known)
            {
                // A backup the session does not name yet: Notepad++ rewrites the session right after creating one.
                sessionDirty = true;
            }
        }

        if (e.ChangeType != WatcherChangeTypes.Deleted)
        {
            Mark(key);
        }
    }

    /// <summary>A backup was renamed with its tab: the tab moves to its new key and name at once.</summary>
    /// <param name="sender">The watcher.</param>
    /// <param name="e">The event.</param>
    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (e.Name is null || !NotepadPlusPlusBackup.TryParseName(e.Name, out var newName, out _))
        {
            // Renamed onto the backup from the temporary file (Notepad++'s swap) or into something else: a change of the target.
            OnChanged(sender, new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(e.FullPath) ?? string.Empty, e.Name));
            return;
        }

        var oldKey = Path.GetFullPath(e.OldFullPath);
        var newKey = Path.GetFullPath(e.FullPath);
        TabInfo? moved = null;
        lock (gate)
        {
            if (tabs.Remove(oldKey, out var tab))
            {
                moved = tab with { Name = newName };
                tabs[newKey] = moved;
                if (pending.Remove(oldKey, out var marks))
                {
                    pending[newKey] = marks;
                }
            }
            else
            {
                sessionDirty = true;
            }
        }

        if (moved is not null)
        {
            _ = RenameAsync(oldKey, newKey, newName);
        }
        else
        {
            Mark(newKey);
        }
    }

    /// <summary>Tells the target about a rename without letting a failure escape into the watcher's event.</summary>
    /// <param name="oldKey">The old key.</param>
    /// <param name="newKey">The new key.</param>
    /// <param name="name">The new name.</param>
    /// <returns>A task completing once told (failures logged).</returns>
    private async Task RenameAsync(string oldKey, string newKey, string name)
    {
        try
        {
            await target.RenameAsync(oldKey, newKey, name).ConfigureAwait(false);
            RaiseChanged();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Following a renamed Notepad++ tab failed ({ex.GetType().Name}).");
        }
    }

    /// <summary>Marks a tab as changed now (its first unread change keeps its time).</summary>
    /// <param name="key">The tab's key.</param>
    private void Mark(string key)
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            pending[key] = pending.TryGetValue(key, out var marks) ? (marks.First, now) : (now, now);
        }
    }

    /// <summary>Change notifications were lost (buffer overflow): every tab is read again once it rests, and the session too.</summary>
    private void MarkEverything()
    {
        List<string> keys;
        lock (gate)
        {
            sessionDirty = true;
            keys = [.. tabs.Keys];
        }

        foreach (var key in keys)
        {
            Mark(key);
        }
    }

    /// <summary>Whether two backup keys share their folder and their stamp (a renamed tab's backup).</summary>
    /// <param name="a">One key.</param>
    /// <param name="b">The other.</param>
    /// <returns><see langword="true"/> for the same tab under another name.</returns>
    private static bool SameStampAndFolder(string a, string b) =>
        string.Equals(Path.GetDirectoryName(a), Path.GetDirectoryName(b), StringComparison.OrdinalIgnoreCase)
        && NotepadPlusPlusBackup.TryParseName(Path.GetFileName(a), out _, out var stampA)
        && NotepadPlusPlusBackup.TryParseName(Path.GetFileName(b), out _, out var stampB)
        && stampA == stampB;

    /// <summary>Raises <see cref="Changed"/>, shielding the watcher from subscriber exceptions.</summary>
    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"A Notepad++ tabs subscriber failed ({ex.GetType().Name}).");
        }
    }

    /// <summary>One unsaved tab as the session names it.</summary>
    /// <param name="Folder">The settings folder whose session names it.</param>
    /// <param name="Name">The tab's name.</param>
    /// <param name="Encoding">The session's encoding for it (-1, or a code page).</param>
    private sealed record TabInfo(string Folder, string Name, int Encoding);
}
