using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Documents;
using BetterClipboard.Core.NotepadPlusPlus;
using BetterClipboard.Core.Services;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Integrations;

/// <summary>
/// Runs the Notepad++ integration for the app (the panel's Notepad++ tab; CLAUDE.md §2.41): the text of Notepad++'s unsaved
/// "new" tabs, kept and updated as it changes, and the path of every file opened in Notepad++, recorded as if it was copied.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two halves, two switches.</b> The tabs (<see cref="NotepadPlusPlusTabWatcher"/>) come from Notepad++'s own files; the
/// opened files (<see cref="NotepadPlusPlusFileWatcher"/>) from asking a running Notepad++. Each runs while its setting is on.
/// </para>
/// <para>
/// <b>First activation.</b> The first time the tabs half runs for a history (state <see cref="ImportedStateName"/>), every tab
/// Notepad++ remembers is imported (as an import: pause does not apply, an entry already holding the same text keeps its
/// place). Later starts read every tab again as a catch-up, which records only what changed meanwhile (the store compares
/// each tab's text with what it recorded). Switching the tabs off forgets which entry belongs to which tab and the marker; the
/// entries stay, and switching on again imports the tabs again without duplicating anything.
/// </para>
/// <para>
/// <b>Re-location</b> every <see cref="RefreshInterval"/> and when Settings or the panel ask (<see cref="RefreshAsync"/>): a
/// Notepad++ installed, started portable from another folder, or given another settings folder is followed.
/// </para>
/// <para><b>Threads.</b> Every public member may be called from any thread; state changes are serialized by one gate.</para>
/// </remarks>
public sealed class NotepadPlusPlusIntegration : IAsyncDisposable, INotepadPlusPlusTabTarget
{
    /// <summary>Store state name marking that this history imported Notepad++'s tabs once ("1"; empty = not yet).</summary>
    public const string ImportedStateName = "notepadpp.tabs.imported";

    /// <summary>How often Notepad++ is located again in the background.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private readonly ClipHistoryService history;
    private readonly Func<long> maxItemBytes;
    private readonly Func<NotepadPlusPlusLocation> locate;
    private readonly TimeProvider time;
    private readonly NotepadPlusPlusTiming timing;
    private readonly Func<uint, NotepadPlusPlusTrust>? verify;
    private readonly string windowClass;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private volatile NotepadPlusPlusLocation location = NotepadPlusPlusLocation.None;
    private NotepadPlusPlusTabWatcher? tabWatcher;
    private NotepadPlusPlusFileWatcher? fileWatcher;
    private Timer? periodicRefresh;
    private bool tabsEnabled;
    private bool filesEnabled;
    private bool started;
    private bool disposed;
    private int tabsRecordedThisSession;

    /// <summary>Creates the integration; nothing happens until <see cref="StartAsync"/>.</summary>
    /// <param name="history">Where versions and opened files are stored, and the import marker kept.</param>
    /// <param name="maxItemBytes">The history's largest item in bytes (bigger backups are not read).</param>
    /// <param name="locate">Finds Notepad++; defaults to <see cref="NotepadPlusPlusLocator.Locate"/> (tests pass fakes).</param>
    /// <param name="time">The clock; defaults to the system clock.</param>
    /// <param name="timing">How long tabs rest before they are read; defaults to <see cref="NotepadPlusPlusTiming.Default"/>.</param>
    /// <param name="verify">The owner check of a running Notepad++ (tests pass their own).</param>
    /// <param name="windowClass">Notepad++'s window class (tests use their own).</param>
    /// <exception cref="ArgumentNullException"><paramref name="history"/> or <paramref name="maxItemBytes"/> is <see langword="null"/>.</exception>
    public NotepadPlusPlusIntegration(ClipHistoryService history, Func<long> maxItemBytes, Func<NotepadPlusPlusLocation>? locate = null, TimeProvider? time = null,
        NotepadPlusPlusTiming? timing = null, Func<uint, NotepadPlusPlusTrust>? verify = null, string windowClass = NotepadPlusPlusClient.WindowClass)
    {
        this.history = history ?? throw new ArgumentNullException(nameof(history));
        this.maxItemBytes = maxItemBytes ?? throw new ArgumentNullException(nameof(maxItemBytes));
        this.locate = locate ?? NotepadPlusPlusLocator.Locate;
        this.time = time ?? TimeProvider.System;
        this.timing = timing ?? NotepadPlusPlusTiming.Default;
        this.verify = verify;
        this.windowClass = windowClass;
    }

    /// <summary>Raised on a pool thread (or the file watcher's thread) whenever anything the status line shows changed.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>What was found about Notepad++ (<see cref="NotepadPlusPlusLocation.None"/> until located).</summary>
    public NotepadPlusPlusLocation Location => location;

    /// <summary>Whether Notepad++'s tabs are being followed right now.</summary>
    public bool IsWatchingTabs => Volatile.Read(ref tabWatcher) is not null;

    /// <summary>How many unsaved tabs Notepad++'s sessions name (0 while not watching).</summary>
    public int TabCount => Volatile.Read(ref tabWatcher)?.TabCount ?? 0;

    /// <summary>Tab versions recorded since the app started (new entries, updates in place, new copies).</summary>
    public int TabsRecordedThisSession => Volatile.Read(ref tabsRecordedThisSession);

    /// <summary>Whether opened files are being watched for right now.</summary>
    public bool IsWatchingFiles => Volatile.Read(ref fileWatcher) is not null;

    /// <summary>The running Notepad++ windows the file watcher knows (empty while not watching).</summary>
    public IReadOnlyList<NotepadPlusPlusInstance> Instances => Volatile.Read(ref fileWatcher)?.Instances ?? [];

    /// <summary>Files recorded as opened since the app started.</summary>
    public int FilesRecordedThisSession => Volatile.Read(ref fileWatcher)?.RecordedThisSession ?? 0;

    /// <summary>Locates Notepad++ and applies both settings. Call once.</summary>
    /// <param name="keepTabs">The <c>KeepNotepadPlusPlusTabs</c> setting.</param>
    /// <param name="recordFiles">The <c>RecordNotepadPlusPlusFiles</c> setting.</param>
    /// <returns>A task completing once applied (the tabs' catch-up included).</returns>
    /// <exception cref="InvalidOperationException">Already started.</exception>
    /// <exception cref="ObjectDisposedException">Disposed.</exception>
    public async Task StartAsync(bool keepTabs, bool recordFiles)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started)
            {
                throw new InvalidOperationException("The Notepad++ integration was already started.");
            }

            started = true;
            tabsEnabled = keepTabs;
            filesEnabled = recordFiles;
            periodicRefresh = new Timer(_ => _ = RefreshAsync(), null, RefreshInterval, RefreshInterval);
            await ApplyAsync(relocate: true).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Follows changed settings (a no-op for unchanged values or before <see cref="StartAsync"/>).</summary>
    /// <param name="keepTabs">The tabs setting.</param>
    /// <param name="recordFiles">The opened-files setting.</param>
    /// <returns>A task completing once applied.</returns>
    public async Task SetEnabledAsync(bool keepTabs, bool recordFiles)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed || !started || (keepTabs == tabsEnabled && recordFiles == filesEnabled))
            {
                return;
            }

            bool tabsTurnedOff = tabsEnabled && !keepTabs;
            tabsEnabled = keepTabs;
            filesEnabled = recordFiles;
            await ApplyAsync(relocate: true).ConfigureAwait(false);
            if (tabsTurnedOff)
            {
                await ForgetTabsAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Locates Notepad++ again and follows any change. Failures are logged, never thrown.</summary>
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
                if (fileWatcher is { } files)
                {
                    await files.ScanNowAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Refreshing the Notepad++ integration failed ({ex.GetType().Name}: {ex.Message}).");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Stops both halves; the import marker and the tab mappings are kept (exit is not "off").</summary>
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

            StopTabs();
            StopFiles();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc/>
    async Task INotepadPlusPlusTabTarget.RecordAsync(IReadOnlyList<NotepadPlusPlusTabVersion> versions, bool isImport)
    {
        var executable = location.ExecutablePath;
        var documents = versions
            .Select(v => new TrackedDocument(NotepadPlusPlusSources.DocumentScope, v.Key, v.TabName, NotepadPlusPlusSources.TabCapture(v.Text, v.TabName, v.WrittenUtc, executable)))
            .ToList();
        IReadOnlyList<TrackedDocumentOutcome> outcomes = documents.Count == 1 && !isImport
            ? [await history.RecordDocumentAsync(documents[0]).ConfigureAwait(false)]
            : await history.RecordDocumentsAsync(documents, isImport).ConfigureAwait(false);
        int recorded = outcomes.Count(o => o.Kind is TrackedDocumentOutcomeKind.Created or TrackedDocumentOutcomeKind.Updated or TrackedDocumentOutcomeKind.NewCopy);
        if (recorded > 0)
        {
            Interlocked.Add(ref tabsRecordedThisSession, recorded);

            // Content-free: counts by kind, never a tab's name or text.
            int created = outcomes.Count(o => o.Kind == TrackedDocumentOutcomeKind.Created);
            int updated = outcomes.Count(o => o.Kind == TrackedDocumentOutcomeKind.Updated);
            int copies = outcomes.Count(o => o.Kind == TrackedDocumentOutcomeKind.NewCopy);
            AppLog.Info($"Notepad++ tabs: {created} new, {updated} updated in place, {copies} kept as a new copy{(isImport ? " (first import)" : string.Empty)}.");
            RaiseStatusChanged();
        }
    }

    /// <inheritdoc/>
    Task INotepadPlusPlusTabTarget.RenameAsync(string oldKey, string newKey, string newTitle) =>
        history.RenameDocumentAsync(NotepadPlusPlusSources.DocumentScope, oldKey, newKey, newTitle);

    /// <inheritdoc/>
    Task INotepadPlusPlusTabTarget.ForgetAsync(IReadOnlyCollection<string> keys) =>
        history.RemoveDocumentsAsync(NotepadPlusPlusSources.DocumentScope, keys);

    /// <summary>Brings both halves in line with the settings and the latest facts (under the gate).</summary>
    /// <param name="relocate">Run the locator first (off the calling thread: registry, processes, a small file).</param>
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
                // The locator never throws by design; keep the last facts.
                AppLog.Warn($"Locating Notepad++ failed ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        var current = location;
        bool changed = false;
        if (!current.WatchesSameAs(previous) || current.ExecutablePath != previous.ExecutablePath)
        {
            // Content-free: the version, how it was found, and how many settings folders (never their paths).
            AppLog.Info($"Notepad++: {(current.IsInstalled ? current.Version ?? "version unknown" : "not installed")} ({current.DetectedBy}), {current.DataFolders.Count} settings folder(s).");
            changed = true;
        }

        if (tabsEnabled && current.DataFolders.Count > 0)
        {
            if (tabWatcher is null || !current.WatchesSameAs(previous))
            {
                StopTabs();
                await StartTabsAsync(current).ConfigureAwait(false);
                changed = true;
            }
        }
        else
        {
            changed |= StopTabs();
        }

        if (filesEnabled && fileWatcher is null)
        {
            var created = new NotepadPlusPlusFileWatcher(RecordOpenedFileAsync, verify, windowClass, time);
            created.StatusChanged += (_, _) => RaiseStatusChanged();
            Volatile.Write(ref fileWatcher, created);
            created.Start();
            AppLog.Info("Watching for files opened in Notepad++.");
            changed = true;
        }
        else if (!filesEnabled)
        {
            changed |= StopFiles();
        }

        if (changed)
        {
            RaiseStatusChanged();
        }
    }

    /// <summary>Starts following the tabs: the first activation imports them, later starts catch up.</summary>
    /// <param name="current">The location.</param>
    /// <returns>A task completing once the tabs were read once.</returns>
    private async Task StartTabsAsync(NotepadPlusPlusLocation current)
    {
        var created = new NotepadPlusPlusTabWatcher(current.DataFolders, this, maxItemBytes, (int)GetACP(), time, timing.QuietTime, timing.MaxDelay, timing.Tick);
        created.Changed += (_, _) => RaiseStatusChanged();
        Volatile.Write(ref tabWatcher, created);
        bool imported = await IsImportedAsync().ConfigureAwait(false);
        AppLog.Info(imported ? "Following Notepad++'s unsaved tabs (catching up)." : "Following Notepad++'s unsaved tabs (first import).");
        try
        {
            await created.StartAsync(isImport: !imported).ConfigureAwait(false);
            if (!imported)
            {
                await history.SetStateValueAsync(ImportedStateName, "1").ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The watch keeps running; a failed first read is caught up by the next start.
            AppLog.Warn($"Reading Notepad++'s tabs failed ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    /// <summary>Stops following the tabs (the mappings stay: exit is not "off").</summary>
    /// <returns>Whether a watcher was running.</returns>
    private bool StopTabs()
    {
        var running = Interlocked.Exchange(ref tabWatcher, null);
        if (running is null)
        {
            return false;
        }

        running.Dispose();
        AppLog.Info("Stopped following Notepad++'s unsaved tabs.");
        return true;
    }

    /// <summary>Stops watching for opened files.</summary>
    /// <returns>Whether a watcher was running.</returns>
    private bool StopFiles()
    {
        var running = Interlocked.Exchange(ref fileWatcher, null);
        if (running is null)
        {
            return false;
        }

        running.Dispose();
        AppLog.Info("Stopped watching for files opened in Notepad++.");
        return true;
    }

    /// <summary>The tabs were switched off: forget which entry belongs to which tab, and the import marker.</summary>
    /// <returns>A task completing once forgotten (failures logged).</returns>
    private async Task ForgetTabsAsync()
    {
        try
        {
            await history.RemoveDocumentsAsync(NotepadPlusPlusSources.DocumentScope, null).ConfigureAwait(false);
            await history.SetStateValueAsync(ImportedStateName, string.Empty).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Forgetting Notepad++'s tabs failed ({ex.GetType().Name}).");
        }
    }

    /// <summary>Whether this history imported Notepad++'s tabs once.</summary>
    /// <returns><see langword="true"/> when the marker is set.</returns>
    private async Task<bool> IsImportedAsync()
    {
        try
        {
            return !string.IsNullOrEmpty(await history.GetStateValueAsync(ImportedStateName).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            // Unreadable = imported: a catch-up records only what changed, an import of everything would reorder nothing either.
            AppLog.Warn($"Reading the Notepad++ import marker failed ({ex.GetType().Name}).");
            return true;
        }
    }

    /// <summary>Records one opened file (its path, as if copied) through the history worker.</summary>
    /// <param name="path">The file.</param>
    /// <param name="executable">Notepad++'s executable.</param>
    /// <param name="when">When the opening was seen.</param>
    /// <returns>A task completing once stored or rejected.</returns>
    private async Task RecordOpenedFileAsync(string path, string executable, DateTimeOffset when)
    {
        await history.AddAsync(NotepadPlusPlusSources.FileCapture(path, when, executable)).ConfigureAwait(false);
        RaiseStatusChanged();
    }

    /// <summary>Raises <see cref="StatusChanged"/> (subscriber exceptions are logged, never propagated into a watcher).</summary>
    private void RaiseStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"A Notepad++ status subscriber failed ({ex.GetType().Name}).");
        }
    }
}

/// <summary>How long Notepad++'s tabs rest before they are read (see <see cref="NotepadPlusPlusTabWatcher"/>).</summary>
/// <param name="QuietTime">No new save for this long: the tab is read.</param>
/// <param name="MaxDelay">A tab that keeps changing is read at the latest this long after its first unread change.</param>
/// <param name="Tick">How often the marks are looked at.</param>
public sealed record NotepadPlusPlusTiming(TimeSpan QuietTime, TimeSpan MaxDelay, TimeSpan Tick)
{
    /// <summary>A minute of rest, at most five minutes, looked at every five seconds.</summary>
    public static NotepadPlusPlusTiming Default { get; } = new(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5));
}
