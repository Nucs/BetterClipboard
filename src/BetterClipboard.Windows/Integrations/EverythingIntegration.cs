using System.Diagnostics;
using System.Text;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Everything;

namespace BetterClipboard.Windows.Integrations;

/// <summary>
/// Runs the voidtools Everything integration for the app: knows whether Everything is installed or running,
/// reads the files the user opened in it (the Everything tab's picks), and opens Everything on request. It only
/// reads from Everything — nothing in Everything's settings, index or run history is ever changed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Status.</b> Refreshed on demand (<see cref="RefreshAsync"/>: when the panel or Settings opens, and before
/// each pick query), not by a timer — finding the window costs microseconds, and nothing needs to know about
/// Everything while nobody looks. The installation (registry, a signature check per Everything build) is looked
/// up at most once per <see cref="InstallationRefreshInterval"/>.
/// </para>
/// <para>
/// <b>Picks.</b> Live (<see cref="EverythingPicksSource.Live"/>) while Everything is ready: one
/// <c>runcount:</c> query sorted by date run, ~1–3 ms. While it is not running, still loading its index or hung,
/// the run history it last saved (<c>Run History.csv</c>, complete once Everything has exited) stands in
/// (<see cref="EverythingPicksSource.SavedFile"/>); then only files on local fixed disks are checked for
/// existence — a network path is shown unchecked rather than risking a stall on a dead share.
/// </para>
/// <para>
/// <b>Threads.</b> Members may be called from any thread; <see cref="StatusChanged"/> is raised on a pool thread.
/// </para>
/// </remarks>
public sealed class EverythingIntegration : IDisposable
{
    /// <summary>Deadline for the picks query; Everything answers in milliseconds unless it is updating its index.</summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How often the installation is looked up again (an install or uninstall shows up within this).</summary>
    public static readonly TimeSpan InstallationRefreshInterval = TimeSpan.FromMinutes(1);

    /// <summary>Fields the picks query asks for.</summary>
    private const uint PickFields = EverythingIpc.RequestFullPath | EverythingIpc.RequestSize | EverythingIpc.RequestDateModified
                                    | EverythingIpc.RequestRunCount | EverythingIpc.RequestDateRun;

    private readonly EverythingClient client;
    private readonly Func<EverythingInstallation> locate;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private EverythingStatus status = EverythingStatus.NotRunning;
    private EverythingInstallation installation = EverythingInstallation.NotFound;
    private DateTimeOffset installationCheckedAt = DateTimeOffset.MinValue;

    /// <summary>The executable and instance of the last trusted Everything seen running (portable copies have no install entry).</summary>
    private (string Executable, string Instance)? lastSeen;
    private bool disposed;

    /// <summary>
    /// Creates the integration (nothing is looked up until <see cref="RefreshAsync"/>).
    /// </summary>
    /// <param name="client">The IPC client; <see langword="null"/> creates one (its reply window starts now).</param>
    /// <param name="locate">Installation lookup; <see langword="null"/> = <see cref="EverythingLocator.Locate"/>.</param>
    /// <param name="time">Clock; <see langword="null"/> = system.</param>
    /// <exception cref="System.ComponentModel.Win32Exception">The client's reply window could not be created.</exception>
    public EverythingIntegration(EverythingClient? client = null, Func<EverythingInstallation>? locate = null, TimeProvider? time = null)
    {
        this.client = client ?? new EverythingClient();
        this.locate = locate ?? EverythingLocator.Locate;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Raised on a pool thread after <see cref="Status"/> or <see cref="Installation"/> changed.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>The running Everything, as of the last refresh.</summary>
    public EverythingStatus Status => Volatile.Read(ref status);

    /// <summary>The installed Everything, as of the last lookup.</summary>
    public EverythingInstallation Installation => Volatile.Read(ref installation);

    /// <summary>
    /// Whether the Everything tab belongs in the panel: Everything runs (ready, loading or hung — not an untrusted
    /// impostor) or is installed.
    /// </summary>
    public bool IsAvailable => Status.State is EverythingState.Ready or EverythingState.Loading or EverythingState.NotResponding
                               || Installation.IsInstalled;

    /// <summary>Whether Everything can be started from the panel (installed, or seen running before).</summary>
    public bool CanStart => StartExecutable() is not null;

    /// <summary>
    /// Re-reads the running state (and the installation when due or <paramref name="forceInstallation"/>); raises
    /// <see cref="StatusChanged"/> when anything changed. Serialized; failures are logged, never thrown.
    /// </summary>
    /// <param name="forceInstallation">Look the installation up now (Settings opening, startup).</param>
    /// <returns>A task completing when refreshed.</returns>
    public async Task RefreshAsync(bool forceInstallation = false)
    {
        if (disposed)
        {
            return;
        }

        await refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var next = await Task.Run(client.GetStatus).ConfigureAwait(false);
            var nextInstallation = Installation;
            var now = time.GetUtcNow();
            if (forceInstallation || now - installationCheckedAt >= InstallationRefreshInterval)
            {
                nextInstallation = await Task.Run(locate).ConfigureAwait(false);
                installationCheckedAt = now;
            }

            if (next.ExecutablePath is { } exe)
            {
                lastSeen = (exe, next.Instance ?? string.Empty);
            }

            var previous = Status;
            bool changed = !SameStatus(previous, next) || nextInstallation != Installation;
            Volatile.Write(ref status, next);
            Volatile.Write(ref installation, nextInstallation);
            if (changed)
            {
                AppLog.Info("Everything: " + Describe(next, nextInstallation));
                RaiseStatusChanged();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Checking for Everything failed: {ex.Message}");
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <summary>
    /// The files opened in Everything, newest opening first (see class remarks for live vs saved).
    /// </summary>
    /// <param name="searchText">The panel's search words (each must occur in the path), or <see langword="null"/>.</param>
    /// <param name="maxPicks">Most picks to return.</param>
    /// <param name="cancellationToken">Cancels a superseded load (typing).</param>
    /// <returns>The picks, where they came from, and what kept them from being live; never throws for Everything's state.</returns>
    /// <exception cref="OperationCanceledException">Cancelled, or replaced by a newer query.</exception>
    public async Task<EverythingPicksResult> GetPicksAsync(string? searchText, int maxPicks, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPicks, 1);
        await RefreshAsync().ConfigureAwait(false);
        var current = Status;
        string? problem = current.State switch
        {
            EverythingState.Loading => "Everything is still loading its index",
            EverythingState.NotResponding => "Everything is not responding",
            EverythingState.Untrusted => "the program answering as Everything is not voidtools Everything",
            _ => null,
        };

        if (current.State == EverythingState.Ready)
        {
            try
            {
                var list = await client.QueryAsync(current, EverythingQuery.Picks(searchText), PickFields, EverythingIpc.SortDateRunDescending,
                    (uint)maxPicks, QueryTimeout, cancellationToken).ConfigureAwait(false);
                var picks = list.Items
                    .Where(i => !string.IsNullOrEmpty(i.FullPath))
                    .Select(i => new EverythingPick(i.FullPath!, i.IsFolder, i.Size, i.Modified, Math.Max(1, i.RunCount ?? 1), i.DateRun))
                    .ToList();
                return new EverythingPicksResult(picks, EverythingPicksSource.Live, null);
            }
            catch (TimeoutException)
            {
                problem = "Everything did not answer in time (it may be updating its index)";
            }
            catch (EverythingUnavailableException ex)
            {
                problem = ex.Message.TrimEnd('.');
            }
            catch (FormatException ex)
            {
                AppLog.Warn($"Everything sent a reply that could not be read: {ex.Message}");
                problem = "Everything's answer could not be read";
            }
        }

        // Not running, loading, hung or failed: the run history Everything saved last.
        var saved = await Task.Run(() => ReadSavedPicks(searchText, maxPicks), cancellationToken).ConfigureAwait(false);
        return new EverythingPicksResult(saved, saved.Count > 0 ? EverythingPicksSource.SavedFile : EverythingPicksSource.None, problem);
    }

    /// <summary>
    /// Shows a path in Everything's own window: the running instance gets a <c>-s "path"</c> command line, else the
    /// installed (or last seen) Everything is started with it.
    /// </summary>
    /// <param name="path">An absolute path.</param>
    /// <returns><see langword="true"/> when Everything took the request or was started.</returns>
    public bool ShowInEverything(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var search = EverythingQuery.ForPath(path);
        var current = Status;
        if (current.State is EverythingState.Ready or EverythingState.Loading && client.RunCommandLine(current, EverythingQuery.ShowCommandLine(search)))
        {
            return true;
        }

        return Start(["-s", search]);
    }

    /// <summary>Starts Everything in the background (no window), e.g. from the tab's "not running" state.</summary>
    /// <returns><see langword="true"/> when started.</returns>
    public bool StartEverything() => Start(["-startup"]);

    /// <summary>Destroys the client's reply window.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        client.Dispose();
    }

    /// <summary>
    /// Starts the installed (or last seen) Everything with arguments. ShellExecute: the long-lived child must not
    /// inherit this process's handles (CLAUDE.md §4).
    /// </summary>
    /// <param name="arguments">Command-line arguments (the instance is added when needed).</param>
    /// <returns><see langword="true"/> when a process was started.</returns>
    private bool Start(IReadOnlyList<string> arguments)
    {
        if (StartExecutable() is not { } start)
        {
            return false;
        }

        try
        {
            var info = new ProcessStartInfo(start.Executable) { UseShellExecute = true };
            if (start.Instance.Length > 0)
            {
                info.ArgumentList.Add("-instance");
                info.ArgumentList.Add(start.Instance);
            }

            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
            AppLog.Info($"Started Everything ({string.Join(' ', arguments.Take(1))}).");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            AppLog.Warn($"Starting Everything failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>The executable and instance to start: the installed one, else the last trusted one seen running.</summary>
    /// <returns>The pair, or <see langword="null"/> when none is known.</returns>
    private (string Executable, string Instance)? StartExecutable()
    {
        if (Installation is { IsInstalled: true, ExecutablePath: { } installed } && File.Exists(installed))
        {
            return (installed, string.Empty);
        }

        return lastSeen is { } seen && File.Exists(seen.Executable) ? seen : null;
    }

    /// <summary>
    /// Reads the last saved run history: the newest existing <c>Run History.csv</c> among the candidates, narrowed by
    /// the search words, with files on local fixed disks checked (gone ones dropped, sizes and folders filled in).
    /// </summary>
    /// <param name="searchText">Search words, or <see langword="null"/>.</param>
    /// <param name="maxPicks">Most picks to return.</param>
    /// <returns>The picks; empty when no file is found or readable.</returns>
    private IReadOnlyList<EverythingPick> ReadSavedPicks(string? searchText, int maxPicks)
    {
        var current = Status;
        var executable = current.ExecutablePath ?? Installation.ExecutablePath ?? lastSeen?.Executable;
        var instance = current.ExecutablePath is not null ? current.Instance : lastSeen?.Instance;
        if (executable is null && !Installation.IsInstalled)
        {
            return [];
        }

        var file = EverythingRunHistoryFile.CandidatePaths(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Path.GetDirectoryName(executable),
                instance)
            .Select(path => new FileInfo(path))
            .Where(f => f.Exists && f.Length <= EverythingRunHistoryFile.MaxFileBytes)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (file is null)
        {
            return [];
        }

        string text;
        try
        {
            // Everything may be writing it right now: read without blocking it.
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Reading Everything's saved run history failed: {ex.Message}");
            return [];
        }

        var terms = string.IsNullOrWhiteSpace(searchText)
            ? []
            : searchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var driveTypes = new Dictionary<string, DriveType>(StringComparer.OrdinalIgnoreCase);
        var picks = new List<EverythingPick>(Math.Min(maxPicks, 256));
        foreach (var pick in EverythingRunHistoryFile.Parse(text))
        {
            if (!terms.All(t => pick.FullPath.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (CheckOnDisk(pick, driveTypes) is { } checkedPick)
            {
                picks.Add(checkedPick);
                if (picks.Count == maxPicks)
                {
                    break;
                }
            }
        }

        return picks;
    }

    /// <summary>
    /// Checks a saved pick on disk when that is safe and fast: on a local fixed drive a vanished file is dropped and
    /// a present one gets its size, date and folder flag; anything else (network, removable) is kept unchecked.
    /// </summary>
    /// <param name="pick">The pick from the file.</param>
    /// <param name="driveTypes">Drive types already looked up during this read.</param>
    /// <returns>The pick (updated), or <see langword="null"/> when it is gone.</returns>
    private static EverythingPick? CheckOnDisk(EverythingPick pick, Dictionary<string, DriveType> driveTypes)
    {
        string? root;
        try
        {
            root = Path.GetPathRoot(pick.FullPath);
        }
        catch (ArgumentException)
        {
            return pick;
        }

        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return pick; // UNC: never touched from here (a dead share blocks for tens of seconds)
        }

        if (!driveTypes.TryGetValue(root, out var type))
        {
            try
            {
                type = new DriveInfo(root).DriveType;
            }
            catch (ArgumentException)
            {
                type = DriveType.Unknown;
            }

            driveTypes[root] = type;
        }

        if (type != DriveType.Fixed)
        {
            return pick;
        }

        try
        {
            var file = new FileInfo(pick.FullPath);
            if (file.Exists)
            {
                return pick with { Size = file.Length, Modified = file.LastWriteTimeUtc };
            }

            return Directory.Exists(pick.FullPath) ? pick with { IsFolder = true } : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return pick;
        }
    }

    /// <summary>Whether two statuses describe the same Everything in the same state.</summary>
    /// <param name="a">One status.</param>
    /// <param name="b">The other.</param>
    /// <returns><see langword="true"/> when nothing worth announcing changed.</returns>
    private static bool SameStatus(EverythingStatus a, EverythingStatus b) =>
        a.State == b.State && a.Instance == b.Instance && a.ProcessId == b.ProcessId && a.Version == b.Version && a.IsBusy == b.IsBusy;

    /// <summary>One log line about the state (no user data: versions, states, the signer).</summary>
    /// <param name="status">The running state.</param>
    /// <param name="installation">The installation.</param>
    /// <returns>The description.</returns>
    private static string Describe(EverythingStatus status, EverythingInstallation installation)
    {
        var running = status.State switch
        {
            EverythingState.Ready => $"{status.Version} running{(status.IsBusy ? " (updating its index)" : string.Empty)}, {status.Trust?.Reason}",
            EverythingState.Loading => $"{status.Version} running, loading its index",
            EverythingState.NotResponding => "running but not responding",
            EverythingState.Untrusted => $"a window answering as Everything was ignored: {status.Trust?.Reason}",
            _ => "not running",
        };
        var instance = string.IsNullOrEmpty(status.Instance) ? string.Empty : $" (instance {status.Instance})";
        return $"{running}{instance}; {(installation.IsInstalled ? $"installed {installation.Version}".TrimEnd() : installation.DetectedBy)}.";
    }

    /// <summary>Raises <see cref="StatusChanged"/>, shielding the refresh from subscriber exceptions.</summary>
    private void RaiseStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Error("An Everything status subscriber threw.", ex);
        }
    }
}

/// <summary>Where the Everything tab's picks came from.</summary>
public enum EverythingPicksSource
{
    /// <summary>No picks could be read (or there are none).</summary>
    None,

    /// <summary>Asked from the running Everything just now.</summary>
    Live,

    /// <summary>Read from the run history Everything saved last (it is not running, still loading, or did not answer).</summary>
    SavedFile,
}

/// <summary>The Everything tab's picks and what to tell the user about them.</summary>
/// <param name="Picks">The picks, newest opening first.</param>
/// <param name="Source">Where they came from.</param>
/// <param name="Problem">Why they are not live (e.g. "Everything is still loading its index"), or <see langword="null"/>.</param>
public sealed record EverythingPicksResult(IReadOnlyList<EverythingPick> Picks, EverythingPicksSource Source, string? Problem);
