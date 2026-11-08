using BetterClipboard.Core.Diagnostics;

namespace BetterClipboard.Windows.Updates;

/// <summary>
/// Tells the update system when the dev-builds folder changed (a build was published or removed), so the update button
/// lights up the moment <c>tools/release/publish-dev.ps1</c> finishes, not at the next hourly look.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two watches.</b> The folder itself, recursively (a build is a subfolder of files), while it exists; and its parent
/// (the data directory), non-recursively and for that one name only, so the folder's appearance — the first publish
/// creates it — and its removal are noticed too. The parent watch sees nothing of the history database or the logs next
/// to it: its filter is the folder's name, and it reports only folders.
/// </para>
/// <para>
/// <b>Quiet first.</b> Publishing writes a 70 MB zip and a few files into a staging folder and then renames it, which
/// raises dozens of events. <see cref="Changed"/> is raised once the folder was quiet for the quiet time (half a second by
/// default), on a pool thread. The reader ignores staging folders anyway (<c>LocalBuildFeed</c>), so a look in the middle
/// of a publish costs nothing but the look.
/// </para>
/// <para>
/// <b>Never in the way.</b> Both watches are change notifications on directory handles that share reading, writing and
/// deleting: publishing, pruning and deleting builds work as if nothing watched. A watch that fails (an overflow, the
/// folder deleted under it) is set up again and reported as a change, so a lost event costs one extra look.
/// </para>
/// <para>Thread-safe; <see cref="Dispose"/> stops everything and is idempotent.</para>
/// </remarks>
public sealed class LocalBuildsWatcher : IDisposable
{
    /// <summary>How long the folder must be quiet before <see cref="Changed"/> is raised, unless the creator sets another.</summary>
    public static readonly TimeSpan DefaultQuietTime = TimeSpan.FromMilliseconds(500);

    private readonly string directory;
    private readonly string? parent;
    private readonly string name;
    private readonly TimeSpan quietTime;
    private readonly Lock gate = new();

    /// <summary>The recursive watch on the dev-builds folder; <see langword="null"/> while it does not exist.</summary>
    private FileSystemWatcher? folderWatcher;

    /// <summary>The watch on the parent for the folder's own name (appearing, disappearing, renamed).</summary>
    private FileSystemWatcher? parentWatcher;

    /// <summary>Fires once the folder was quiet for <see cref="quietTime"/>; re-armed by every event.</summary>
    private Timer? quietTimer;

    private bool started;
    private bool disposed;

    /// <summary>
    /// Creates a watcher for a dev-builds folder; nothing is watched before <see cref="Start"/>.
    /// </summary>
    /// <param name="directory">The dev-builds folder (it need not exist yet).</param>
    /// <param name="quietTime">How long the folder must be quiet before a change is reported; <see langword="null"/> for <see cref="DefaultQuietTime"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is null or blank, or not a valid path.</exception>
    public LocalBuildsWatcher(string directory, TimeSpan? quietTime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        parent = Path.GetDirectoryName(this.directory);
        name = Path.GetFileName(this.directory);
        this.quietTime = quietTime is { } time && time > TimeSpan.Zero ? time : DefaultQuietTime;
    }

    /// <summary>
    /// Raised on a pool thread once the dev-builds folder changed and then stayed quiet: a build appeared, was replaced or
    /// removed, or the folder itself came or went. Subscribers look into the folder again (it is cheap).
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>Whether the folder itself is watched right now (it exists and its watch could be set up); for tests.</summary>
    internal bool IsWatchingFolder
    {
        get
        {
            lock (gate)
            {
                return folderWatcher is not null;
            }
        }
    }

    /// <summary>Starts watching. Calling it again does nothing.</summary>
    /// <exception cref="ObjectDisposedException">The watcher was disposed.</exception>
    public void Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started)
            {
                return;
            }

            started = true;
            quietTimer = new Timer(_ => OnQuiet(), null, Timeout.Infinite, Timeout.Infinite);
            ArmParent();
            ArmFolder();
        }
    }

    /// <summary>Stops watching; a pending report is dropped.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Close(ref folderWatcher);
            Close(ref parentWatcher);
            quietTimer?.Dispose();
            quietTimer = null;
        }
    }

    /// <summary>
    /// Sets up the watch on the parent for the folder's name. The caller holds <see cref="gate"/>. Without a parent (the
    /// folder is a drive's root) or when the watch cannot be set up, the folder's own watch and the update service's hourly
    /// look are what remains.
    /// </summary>
    private void ArmParent()
    {
        if (parent is null || !Directory.Exists(parent))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(parent, name)
            {
                IncludeSubdirectories = false,

                // Folders only: the history database and the logs next to the folder change all the time, never by name.
                NotifyFilter = NotifyFilters.DirectoryName,
            };
            watcher.Created += (_, _) => OnFolderCameOrWent();
            watcher.Deleted += (_, _) => OnFolderCameOrWent();
            watcher.Renamed += (_, _) => OnFolderCameOrWent();
            watcher.Error += (_, e) => OnWatchFailed(e.GetException(), parentWatch: true);
            watcher.EnableRaisingEvents = true;
            parentWatcher = watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            AppLog.Info($"The data folder cannot be watched for the dev-builds folder ({ex.GetType().Name}); local builds are looked at hourly and when the update dialog opens.");
        }
    }

    /// <summary>
    /// Sets up the recursive watch on the folder when it exists, replacing a previous one. The caller holds <see cref="gate"/>.
    /// </summary>
    private void ArmFolder()
    {
        Close(ref folderWatcher);
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,

                // 64 KB, the documented maximum: a publish writes a few files, a prune deletes whole folders of them.
                InternalBufferSize = 64 * 1024,
            };
            watcher.Created += (_, _) => Schedule();
            watcher.Changed += (_, _) => Schedule();
            watcher.Deleted += (_, _) => Schedule();
            watcher.Renamed += (_, _) => Schedule();
            watcher.Error += (_, e) => OnWatchFailed(e.GetException(), parentWatch: false);
            watcher.EnableRaisingEvents = true;
            folderWatcher = watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Gone between the check and the watch, or not watchable: the parent's watch notices when it comes back.
            AppLog.Info($"The dev-builds folder cannot be watched ({ex.GetType().Name}); local builds are looked at hourly and when the update dialog opens.");
        }
    }

    /// <summary>The folder appeared, disappeared or was renamed: watch it (or stop), and report a change.</summary>
    private void OnFolderCameOrWent()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            ArmFolder();
        }

        Schedule();
    }

    /// <summary>
    /// A watch failed (its buffer overflowed, or the folder it watched was deleted): set it up again and report a change,
    /// since events may have been lost.
    /// </summary>
    /// <param name="exception">What the watch reported, for the log (its type only: the message may hold a path).</param>
    /// <param name="parentWatch">Whether the parent's watch failed (else the folder's).</param>
    private void OnWatchFailed(Exception exception, bool parentWatch)
    {
        AppLog.Info($"The {(parentWatch ? "data folder" : "dev-builds folder")} watch stopped ({exception.GetType().Name}); setting it up again.");
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            if (parentWatch)
            {
                Close(ref parentWatcher);
                ArmParent();
            }

            ArmFolder();
        }

        Schedule();
    }

    /// <summary>Re-arms the quiet timer: the report waits until the folder was quiet for <see cref="quietTime"/>.</summary>
    private void Schedule()
    {
        lock (gate)
        {
            if (!disposed)
            {
                quietTimer?.Change(quietTime, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>The folder was quiet long enough: report the change. A subscriber's failure is logged, never thrown into the pool.</summary>
    private void OnQuiet()
    {
        if (Volatile.Read(ref disposed))
        {
            return;
        }

        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Error("A dev-builds folder subscriber failed.", ex);
        }
    }

    /// <summary>Stops and disposes a watch, leaving its field empty.</summary>
    /// <param name="watcher">The field holding the watch.</param>
    private static void Close(ref FileSystemWatcher? watcher)
    {
        if (watcher is null)
        {
            return;
        }

        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
        watcher = null;
    }
}
