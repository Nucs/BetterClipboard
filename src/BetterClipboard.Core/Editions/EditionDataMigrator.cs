using System.Security.Cryptography;
using BetterClipboard.Core.Security;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Editions;

/// <summary>One file of another edition's data that is brought over.</summary>
/// <param name="RelativePath">Its path below the data folder, e.g. <c>stores\{id}\history.db</c>.</param>
/// <param name="Length">Its size in bytes when it was looked at.</param>
public sealed record EditionDataFile(string RelativePath, long Length);

/// <summary>
/// What another edition of BetterClipboard keeps in its data folder that is worth bringing over: its settings and its
/// history stores (<see cref="EditionDataMigrator.Inspect"/>).
/// </summary>
/// <param name="Directory">The data folder that was looked at.</param>
/// <param name="Files">The files to bring over; empty when the folder is missing or holds nothing of the kind.</param>
/// <param name="TotalBytes">Their sizes added up.</param>
public sealed record EditionData(string Directory, IReadOnlyList<EditionDataFile> Files, long TotalBytes)
{
    /// <summary>Whether there is nothing to bring over.</summary>
    public bool IsEmpty => Files.Count == 0;

    /// <summary>Whether a history store is among the files (anything below <c>stores</c>).</summary>
    public bool HasHistory => Files.Any(f => f.RelativePath.StartsWith(EditionDataMigrator.StoresFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
}

/// <summary>How the check of the copied data went (<see cref="EditionDataMigrator.Validate"/>).</summary>
/// <param name="Ok">Whether the copy is good: it opens with this account's key and SQLite finds it sound.</param>
/// <param name="Reason">Why not, in English (for the log; the window words it itself), or <see langword="null"/>.</param>
/// <param name="Stats">What the copied history holds, when it opened.</param>
/// <param name="Opened">
/// The opened store, when <paramref name="Ok"/>: the app goes on with this very store instead of opening it again.
/// </param>
public sealed record EditionDataCheck(bool Ok, string? Reason, StoreStats? Stats, MachineBoundHistoryResult? Opened);

/// <summary>
/// Brings the data of another edition of BetterClipboard over into the Microsoft Store edition's own folder: looks at
/// what is there, copies it with a checksum per file, checks that the copied history opens, and removes the old folder
/// once everything else succeeded. The Store edition's setup drives it step by step (CLAUDE.md §2.47).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is brought over:</b> <c>settings.json</c> and everything below <c>stores</c> (every store, also one set aside
/// as unreadable, which the app never deletes by itself). Not the logs, the updater's working folder, development
/// builds or the installer's record: they belong to the copy that is being removed.
/// </para>
/// <para>
/// <b>Why a copy and not a move:</b> until the copy is proven good, the old folder is the only history there is. The
/// old folder is removed last, by <see cref="RemoveSource"/>, and only by the caller's decision.
/// </para>
/// <para>
/// <b>The history key needs no conversion:</b> it is sealed to the Windows account and this PC (DPAPI with the machine
/// binding), and the Store edition runs as the same account on the same PC (measured: a blob sealed outside a package
/// opens inside). The database is found by the same store id, which comes from the same binding.
/// </para>
/// <para>
/// <b>The other copy must not run</b> while its files are copied: a database that is being written is not copied
/// consistently. <see cref="CopyAsync"/> opens every file without write sharing, so a running copy makes it fail at
/// once instead of producing a broken history.
/// </para>
/// <para>
/// <b>An unfinished move is undone, never continued.</b> <see cref="CopyAsync"/> first writes a marker file into the
/// target (<see cref="PendingMarkerName"/>), and <see cref="Commit"/> removes it at the point of no return, right
/// before the old folder is deleted. While the marker is there (<see cref="HasPendingMove"/>), what the target holds is
/// a copy whose original still exists: a move the user left, one whose uninstall step failed, or one a crash or a
/// power cut interrupted. The caller then runs <see cref="Rollback"/> and starts over from the original, which may have
/// been used again in the meantime. Without the marker, a half-written copy would count as this edition's own history
/// at the next start, and the real one would never be brought over.
/// </para>
/// </remarks>
public static class EditionDataMigrator
{
    /// <summary>The folder below a data folder that holds the history stores.</summary>
    public const string StoresFolder = "stores";

    /// <summary>The settings file's name in a data folder.</summary>
    public const string SettingsFileName = "settings.json";

    /// <summary>
    /// The marker file in a target folder while a move into it is unfinished (see the class remarks). Not below
    /// <c>stores</c>, so it never counts as history.
    /// </summary>
    public const string PendingMarkerName = "edition-move.pending";

    /// <summary>Room kept free beyond the data itself, for the database's own working files after the copy.</summary>
    private const long FreeSpaceReserve = 64L * 1024 * 1024;

    /// <summary>Copy buffer: large enough that a 200 MB history is a few hundred writes.</summary>
    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// Lists what a data folder holds that is brought over. Reads names and sizes only.
    /// </summary>
    /// <param name="sourceDataDirectory">The other editions' data folder (<see cref="AppPaths.DefaultDataDirectory"/>).</param>
    /// <returns>The files; an empty list for a missing folder.</returns>
    /// <exception cref="ArgumentException"><paramref name="sourceDataDirectory"/> is null or blank.</exception>
    /// <exception cref="IOException">The folder could not be listed.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder may not be read.</exception>
    public static EditionData Inspect(string sourceDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDataDirectory);
        string root = Path.GetFullPath(sourceDataDirectory);
        var files = new List<EditionDataFile>();
        if (!Directory.Exists(root))
        {
            return new EditionData(root, files, 0);
        }

        string settings = Path.Combine(root, SettingsFileName);
        if (File.Exists(settings))
        {
            files.Add(new EditionDataFile(SettingsFileName, new FileInfo(settings).Length));
        }

        string stores = Path.Combine(root, StoresFolder);
        if (Directory.Exists(stores))
        {
            foreach (string path in Directory.EnumerateFiles(stores, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
            {
                // SQLite's shared-memory index is rebuilt by whoever opens the database next; a copy of it is useless.
                if (path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                files.Add(new EditionDataFile(Path.GetRelativePath(root, path), new FileInfo(path).Length));
            }
        }

        return new EditionData(root, files, files.Sum(f => f.Length));
    }

    /// <summary>
    /// Checks, without changing anything, that every file to bring over can be opened for reading. Part of the checks
    /// before a move; the other copy may still run (the files are opened with every sharing mode).
    /// </summary>
    /// <param name="data">What <see cref="Inspect"/> found.</param>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">A file could not be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">A file may not be read.</exception>
    public static void EnsureReadable(EditionData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        foreach (var file in data.Files)
        {
            using var stream = new FileStream(Path.Combine(data.Directory, file.RelativePath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
    }

    /// <summary>
    /// Checks that a folder can be written and deleted in, by making and removing one small file. Used before a move for
    /// both ends: the Store edition's own folder (which is created if missing) and the old data folder that is removed
    /// afterwards.
    /// </summary>
    /// <param name="directory">The folder.</param>
    /// <param name="create">Create the folder when it does not exist; otherwise a missing folder passes (nothing to remove).</param>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is null or blank.</exception>
    /// <exception cref="IOException">The file could not be written or removed.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder may not be written.</exception>
    public static void EnsureWritable(string directory, bool create)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
        {
            if (!create)
            {
                return;
            }

            Directory.CreateDirectory(directory);
        }

        string probe = Path.Combine(directory, $".write-check-{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(probe, [0]);
        File.Delete(probe);
    }

    /// <summary>
    /// How much room the copy needs on the target's drive: the data, a tenth more, and a reserve for the database's
    /// working files.
    /// </summary>
    /// <param name="data">What <see cref="Inspect"/> found.</param>
    /// <returns>The bytes that should be free.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.</exception>
    public static long RequiredFreeBytes(EditionData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.TotalBytes + (data.TotalBytes / 10) + FreeSpaceReserve;
    }

    /// <summary>
    /// The free room on the drive of a folder (which need not exist yet).
    /// </summary>
    /// <param name="directory">A folder on the drive in question.</param>
    /// <returns>The free bytes available to this user, or <see cref="long.MaxValue"/> when the drive cannot be asked (a network path): the copy itself then finds out.</returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is null or blank.</exception>
    public static long FreeBytes(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(directory));
            return string.IsNullOrEmpty(root) ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }

    /// <summary>
    /// Copies the files into the target data folder and proves every copy: the bytes read from the source and the bytes
    /// read back from the copy must have the same SHA-256.
    /// </summary>
    /// <remarks>
    /// The target is marked as an unfinished move first (<see cref="PendingMarkerName"/>) and stays marked when this
    /// returns: the caller ends the move with <see cref="Commit"/> or undoes it with <see cref="Rollback"/>.
    /// </remarks>
    /// <param name="data">What <see cref="Inspect"/> found.</param>
    /// <param name="targetDataDirectory">The Store edition's data folder; created when missing.</param>
    /// <param name="progress">Told the bytes copied so far, after every block; may be <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the copy; the caller then runs <see cref="Rollback"/>.</param>
    /// <returns>A task that completes when every file is copied and proven.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="targetDataDirectory"/> is null or blank, or is the source folder.</exception>
    /// <exception cref="InvalidOperationException">The target already holds a history: two histories cannot be merged, so nothing is copied.</exception>
    /// <exception cref="IOException">A file is in use (the other copy still runs), could not be read or written, or its copy differs from it.</exception>
    /// <exception cref="UnauthorizedAccessException">A file may not be read, or the target may not be written.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static async Task CopyAsync(EditionData data, string targetDataDirectory, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDataDirectory);
        string target = Path.GetFullPath(targetDataDirectory);
        if (string.Equals(Path.TrimEndingDirectorySeparator(target), Path.TrimEndingDirectorySeparator(data.Directory), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The target is the folder the data comes from.", nameof(targetDataDirectory));
        }

        if (new AppPaths(target).HasHistory())
        {
            throw new InvalidOperationException("The target folder already holds a history.");
        }

        Directory.CreateDirectory(target);

        // Before the first file: from here on the target holds a copy whose original still exists, until Commit says
        // otherwise. Written through to the disk, because a power cut is one of the cases it is for.
        await using (var marker = new FileStream(Path.Combine(target, PendingMarkerName), FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            await marker.WriteAsync(System.Text.Encoding.UTF8.GetBytes(data.Directory), cancellationToken).ConfigureAwait(false);
            marker.Flush(flushToDisk: true);
        }

        byte[] buffer = new byte[BufferSize];
        long copied = 0;
        foreach (var file in data.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string from = Path.Combine(data.Directory, file.RelativePath);
            string to = Path.Combine(target, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);

            byte[] sourceHash;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                // FileShare.Read: a copy that still runs has its database open for writing, and this open then fails —
                // which is the point. Copying a database under a writer would produce a history that may not open.
                await using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var destination = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                    progress?.Report(copied);
                }

                // To the disk, not only to the cache: the source is deleted soon after.
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
                sourceHash = hash.GetHashAndReset();
            }

            byte[] copyHash;
            await using (var written = new FileStream(to, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                copyHash = await SHA256.HashDataAsync(written, cancellationToken).ConfigureAwait(false);
            }

            if (!sourceHash.AsSpan().SequenceEqual(copyHash))
            {
                throw new IOException($"The copy of '{file.RelativePath}' differs from its source.");
            }
        }
    }

    /// <summary>
    /// Checks the copied history the way the app will use it: opens this machine's store in the target folder with this
    /// account's key and has SQLite check the database.
    /// </summary>
    /// <param name="target">The Store edition's paths.</param>
    /// <param name="protector">Key sealing (DPAPI for the current user).</param>
    /// <param name="binding">The machine binding; the caller may zero it afterwards.</param>
    /// <param name="now">Clock for a quarantine folder's name.</param>
    /// <returns>
    /// The verdict. Not good when the copied store could not be decrypted (the opener then set it aside and made an
    /// empty one, which is not what was asked for) or when SQLite reports damage. Never throws for those; the reason is
    /// in the result.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static EditionDataCheck Validate(AppPaths target, IKeyProtector protector, byte[] binding, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(binding);
        try
        {
            var opened = MachineBoundHistory.Open(target, protector, binding, now);
            if (opened.QuarantinedDirectory is not null)
            {
                return new EditionDataCheck(false, $"The copied history could not be decrypted with this account's key: {opened.QuarantineReason}", null, null);
            }

            string verdict = opened.Store.QuickCheck();
            if (!string.Equals(verdict, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return new EditionDataCheck(false, $"SQLite reports damage in the copied history: {verdict}", null, null);
            }

            return new EditionDataCheck(true, null, opened.Store.GetStats(), opened);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException or Microsoft.Data.Sqlite.SqliteException)
        {
            return new EditionDataCheck(false, $"The copied history could not be opened: {ex.Message}", null, null);
        }
    }

    /// <summary>
    /// Whether a move into this folder was started and neither finished nor undone (see the class remarks): what it
    /// holds is then a copy whose original still exists, and the caller undoes it with <see cref="Rollback"/>.
    /// </summary>
    /// <param name="targetDataDirectory">The Store edition's data folder.</param>
    /// <returns><see langword="true"/> while the marker file is there.</returns>
    /// <exception cref="ArgumentException"><paramref name="targetDataDirectory"/> is null or blank.</exception>
    public static bool HasPendingMove(string targetDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDataDirectory);
        return File.Exists(Path.Combine(targetDataDirectory, PendingMarkerName));
    }

    /// <summary>
    /// Ends a move for good: the copy in the target is this edition's history from now on, and no later start undoes
    /// it. Called at the point of no return, right before the old folder is deleted (<see cref="RemoveSource"/>).
    /// </summary>
    /// <param name="targetDataDirectory">The Store edition's data folder.</param>
    /// <exception cref="ArgumentException"><paramref name="targetDataDirectory"/> is null or blank.</exception>
    /// <exception cref="IOException">The marker could not be deleted; the old folder must then stay, or the next start would undo a move whose original is gone.</exception>
    /// <exception cref="UnauthorizedAccessException">Deleting the marker was refused; see above.</exception>
    public static void Commit(string targetDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDataDirectory);
        File.Delete(Path.Combine(targetDataDirectory, PendingMarkerName));
    }

    /// <summary>
    /// Removes what <see cref="CopyAsync"/> (and a failed <see cref="Validate"/>) put into the target folder: its
    /// settings file and its whole <c>stores</c> folder, and then the marker of the unfinished move. The source was never
    /// changed, so after this the PC is as before.
    /// </summary>
    /// <remarks>
    /// The marker goes last: when something could not be deleted, the folder stays marked, and the next attempt undoes
    /// the rest instead of taking half a copy for a history.
    /// </remarks>
    /// <param name="targetDataDirectory">The Store edition's data folder.</param>
    /// <exception cref="ArgumentException"><paramref name="targetDataDirectory"/> is null or blank.</exception>
    /// <exception cref="IOException">Something could not be deleted (a database still open).</exception>
    /// <exception cref="UnauthorizedAccessException">Deleting was refused.</exception>
    public static void Rollback(string targetDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDataDirectory);

        // A failed check may have left pooled connections to the copied database open.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        string stores = Path.Combine(targetDataDirectory, StoresFolder);
        if (Directory.Exists(stores))
        {
            Directory.Delete(stores, recursive: true);
        }

        File.Delete(Path.Combine(targetDataDirectory, SettingsFileName));
        File.Delete(Path.Combine(targetDataDirectory, PendingMarkerName));
    }

    /// <summary>
    /// Deletes the other editions' data folder with everything in it, after its history was brought over and proven and
    /// its program was uninstalled. Irreversible: the caller decides, this only does it. Call <see cref="Commit"/> first,
    /// or the next start would undo the copy although its original is gone.
    /// </summary>
    /// <param name="sourceDataDirectory">The folder to delete (<see cref="AppPaths.DefaultDataDirectory"/>).</param>
    /// <returns>
    /// The files that could not be deleted (in use, or refused), empty when the folder is gone. Never throws for those:
    /// leftovers cost disk room, not correctness, and the caller tells the user where they are.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="sourceDataDirectory"/> is null or blank.</exception>
    public static IReadOnlyList<string> RemoveSource(string sourceDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDataDirectory);
        string root = Path.GetFullPath(sourceDataDirectory);
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            Directory.Delete(root, recursive: true);
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Something is held open. Delete what can be deleted, file by file, and report the rest.
        }

        var leftovers = new List<string>();
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList())
        {
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                leftovers.Add(path);
            }
        }

        try
        {
            if (leftovers.Count == 0)
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            leftovers.Add(root);
        }

        return leftovers;
    }
}
