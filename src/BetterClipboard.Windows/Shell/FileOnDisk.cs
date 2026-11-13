namespace BetterClipboard.Windows.Shell;

/// <summary>What <see cref="FileOnDisk.Check"/> could tell about a path.</summary>
public enum FilePresence
{
    /// <summary>The file is there (checked on a local drive).</summary>
    OnDisk,

    /// <summary>The file is not there: deleted, moved, renamed, or its drive is gone.</summary>
    Missing,

    /// <summary>
    /// Not checked: the path is on a network share or a drive whose check could block (an optical drive spinning up, a dead
    /// share timing out). Treat it as there, and let the action report what it finds.
    /// </summary>
    Unknown,
}

/// <summary>
/// Tells whether a file is still on disk without ever blocking the caller on a slow drive: the card menu asks it while it
/// opens (on the UI thread), so a picture's path-based actions show only while the file exists (CLAUDE.md §2.49).
/// </summary>
/// <remarks>
/// Local fixed, removable and RAM drives are checked (a lookup in the file system's cache, microseconds; a removable drive
/// without its medium answers "not there" at once). A UNC path, a mapped network drive, an optical drive or a drive of an
/// unknown kind is never touched — a dead share blocks for tens of seconds (the Everything tab's rule, CLAUDE.md §2.14) — and
/// answers <see cref="FilePresence.Unknown"/>. Only files count: a folder at the path is <see cref="FilePresence.Missing"/>,
/// since every action that asks acts on a file.
/// </remarks>
public static class FileOnDisk
{
    /// <summary>Checks whether a file exists, when that is safe and fast (see the class remarks).</summary>
    /// <param name="path">An absolute path; anything else (empty, relative, a bare name) is <see cref="FilePresence.Missing"/>.</param>
    /// <returns>Whether the file is there, not there, or was not checked.</returns>
    public static FilePresence Check(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return FilePresence.Missing;
        }

        string? root;
        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return FilePresence.Missing;
            }

            root = Path.GetPathRoot(path);
        }
        catch (ArgumentException)
        {
            return FilePresence.Missing;
        }

        // UNC and device paths (\\server\share, \\?\…): never touched from here.
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return FilePresence.Unknown;
        }

        DriveType type;
        try
        {
            // GetDriveType only asks the mount manager: no I/O on the drive itself, so it is fast even for a dead share.
            type = new DriveInfo(root).DriveType;
        }
        catch (ArgumentException)
        {
            return FilePresence.Unknown;
        }

        switch (type)
        {
            case DriveType.NoRootDirectory:
                return FilePresence.Missing; // the drive letter is not there at all (an unplugged USB drive)
            case DriveType.Fixed or DriveType.Removable or DriveType.Ram:
                try
                {
                    return File.Exists(path) ? FilePresence.OnDisk : FilePresence.Missing;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return FilePresence.Unknown;
                }

            default:
                return FilePresence.Unknown; // network, optical, or a kind Windows does not know
        }
    }
}
