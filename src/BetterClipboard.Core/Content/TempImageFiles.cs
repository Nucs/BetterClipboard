using System.Globalization;
using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.Content;

/// <summary>
/// The pictures the card menu's <em>Save to temp and</em> writes (CLAUDE.md §2.49): where they go, what they are called,
/// how a second save of the same picture finds the first, and which ones the start deletes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where.</b> <c>%TEMP%\BetterClipboard\Images</c> (an isolated instance's under its own scope, so a test instance never
/// deletes the regular one's files), one folder per picture named after its content hash
/// (<see cref="FolderFor"/>): the file inside keeps a readable name — the original file's, or the copy's time — because the
/// name travels with it when the file is pasted into a chat or an e-mail. Windows' temp folder is real also for the
/// Microsoft Store edition (measured, <c>docs/microsoft-store.md</c> §2), so other apps see the same path.
/// </para>
/// <para>
/// <b>Plain files.</b> Unlike the history, these copies are not encrypted: they exist because the user asked for a file. The
/// start deletes the ones older than the setting (<c>AppSettings.TempImageDays</c>, a month by default; 0 = every one at
/// every start), by their last write time — a save that finds its earlier copy touches it, so a picture used again stays.
/// </para>
/// <para>
/// <b>Never over an edit.</b> A save writes a new file next to an earlier one that differs (the user may have edited the
/// earlier one in an image editor), so the earlier one is never overwritten.
/// </para>
/// </remarks>
public static class TempImageFiles
{
    /// <summary>The default age, in days, past which the start deletes a saved picture (a month).</summary>
    public const int DefaultKeepDays = 30;

    /// <summary>The largest age the setting accepts, in days (ten years: "practically never").</summary>
    public const int MaxKeepDays = 3650;

    /// <summary>The number of content-hash characters a picture's folder is named after (48 bits: no clash in practice).</summary>
    private const int FolderHashLength = 12;

    /// <summary>How many differing copies of one name a picture's folder may hold before <see cref="Save"/> gives up.</summary>
    private const int MaxCopiesPerName = 1000;

    /// <summary>The fallback name's pattern, after the copy's local time (Snipping Tool names its files the same way).</summary>
    private const string TimeNamePattern = "'Image 'yyyy-MM-dd HHmmss'.png'";

    /// <summary>
    /// The folder saved pictures go to, under Windows' temp folder.
    /// </summary>
    /// <param name="tempPath">Windows' temp folder (<see cref="Path.GetTempPath"/>).</param>
    /// <param name="instanceScope">The instance's scope (<c>AppPaths.InstanceScope</c>), or <see langword="null"/> for the regular instance.</param>
    /// <returns><c>…\BetterClipboard\Images</c>, or <c>…\BetterClipboard\&lt;scope&gt;\Images</c> for an isolated instance.</returns>
    /// <exception cref="ArgumentException"><paramref name="tempPath"/> is empty.</exception>
    public static string RootIn(string tempPath, string? instanceScope)
    {
        ArgumentException.ThrowIfNullOrEmpty(tempPath);
        return string.IsNullOrEmpty(instanceScope)
            ? Path.Combine(tempPath, "BetterClipboard", "Images")
            : Path.Combine(tempPath, "BetterClipboard", instanceScope, "Images");
    }

    /// <summary>
    /// The folder one picture's saved copies go to: named after its content hash, so every save of the same picture meets
    /// the earlier one, and two pictures with the same file name never meet.
    /// </summary>
    /// <param name="root">The saved pictures' folder (<see cref="RootIn"/>).</param>
    /// <param name="contentHash">The entry's content hash (lowercase hex).</param>
    /// <returns>The folder's path (not created here).</returns>
    /// <exception cref="ArgumentException">An argument is empty, or the hash has a character no folder name may hold.</exception>
    public static string FolderFor(string root, string contentHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(contentHash);
        var name = contentHash.Length > FolderHashLength ? contentHash[..FolderHashLength] : contentHash;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
        {
            throw new ArgumentException("A content hash must be a plain name.", nameof(contentHash));
        }

        return Path.Combine(root, name);
    }

    /// <summary>
    /// The name a saved picture gets: the original file's when one is known (with <c>.png</c> when the original was another
    /// format, since the saved copy is a PNG), else <c>Image yyyy-MM-dd HHmmss.png</c> after when it was first copied.
    /// </summary>
    /// <param name="file">The picture's source file, or <see langword="null"/>.</param>
    /// <param name="copiedUtc">When the entry was first stored (<see cref="ClipEntry.CreatedUtc"/>).</param>
    /// <param name="zone">The time zone the fallback name's time is told in (the user's, <see cref="TimeZoneInfo.Local"/>).</param>
    /// <returns>A plain file name (no folder), always ending in <c>.png</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="zone"/> is <see langword="null"/>.</exception>
    public static string FileNameFor(SourceFile? file, DateTimeOffset copiedUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (file is not null)
        {
            // A name from a real path is valid already; a stored one could still be odd (a hand-edited store, a UNC root).
            var stem = Path.GetFileNameWithoutExtension(file.Name);
            stem = string.Concat(stem.Where(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0)).Trim().TrimEnd('.');
            if (stem.Length > 0)
            {
                return stem + ".png";
            }
        }

        return TimeZoneInfo.ConvertTime(copiedUtc, zone).ToString(TimeNamePattern, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Writes a picture's PNG into its folder under <paramref name="fileName"/>, or finds the copy an earlier save left there.
    /// </summary>
    /// <remarks>
    /// An earlier copy with the same bytes is used again and its write time touched (the start's deletion counts from the
    /// last use). One that differs — edited since — is left alone, and the new copy is numbered (<c>name (2).png</c>). The
    /// file is written under a temporary name and renamed into place, so another app never reads half a picture.
    /// </remarks>
    /// <param name="root">The saved pictures' folder (<see cref="RootIn"/>).</param>
    /// <param name="contentHash">The entry's content hash (names the picture's folder).</param>
    /// <param name="fileName">The name to give it (<see cref="FileNameFor"/>).</param>
    /// <param name="png">The picture's PNG bytes.</param>
    /// <param name="nowUtc">The time to touch an earlier copy with.</param>
    /// <returns>The saved file's full path.</returns>
    /// <exception cref="ArgumentException">An argument is empty, or <paramref name="fileName"/> is not a plain name.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="png"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The folder or file could not be written (disk full, a name taken by a folder, …).</exception>
    /// <exception cref="UnauthorizedAccessException">Windows refused the write.</exception>
    public static string Save(string root, string contentHash, string fileName, byte[] png, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(png);
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName is "." or "..")
        {
            throw new ArgumentException("The file name must be a plain name.", nameof(fileName));
        }

        var folder = FolderFor(root, contentHash);
        Directory.CreateDirectory(folder);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (int number = 1; number <= MaxCopiesPerName; number++)
        {
            var path = Path.Combine(folder, number == 1 ? fileName : $"{stem} ({number.ToString(CultureInfo.InvariantCulture)}){extension}");
            if (File.Exists(path))
            {
                if (SameBytes(path, png))
                {
                    File.SetLastWriteTimeUtc(path, nowUtc.UtcDateTime);
                    return path;
                }

                continue; // edited since (or another picture's name clash): never written over
            }

            if (Directory.Exists(path))
            {
                continue;
            }

            // Written aside and renamed: a paste that reads the file the moment it appears never meets half a picture.
            var partial = path + ".part";
            File.WriteAllBytes(partial, png);
            File.Move(partial, path, overwrite: false);
            return path;
        }

        // Only a folder filled by hand (or a runaway caller) gets here: refuse rather than loop on.
        throw new IOException($"The folder already holds {MaxCopiesPerName} copies named like this picture.");
    }

    /// <summary>
    /// Whether the start deletes a saved picture: older than <paramref name="keepDays"/> by its last write (every one when 0).
    /// </summary>
    /// <param name="lastWriteUtc">The file's last write time.</param>
    /// <param name="nowUtc">Now.</param>
    /// <param name="keepDays">The setting (<c>AppSettings.TempImageDays</c>); negative counts as 0.</param>
    /// <returns><see langword="true"/> to delete it.</returns>
    public static bool IsExpired(DateTimeOffset lastWriteUtc, DateTimeOffset nowUtc, int keepDays) =>
        keepDays <= 0 || nowUtc - lastWriteUtc >= TimeSpan.FromDays(keepDays);

    /// <summary>
    /// Deletes the saved pictures the setting no longer keeps, and the folders that leaves empty (the start's clean-up).
    /// </summary>
    /// <remarks>
    /// Only files under <paramref name="root"/> are touched, and a file another app still holds (an editor that has it open,
    /// say) is skipped without a word: the next start tries again. Leftovers of an interrupted save (<c>.part</c>) are deleted
    /// whatever their age once they are an hour old.
    /// </remarks>
    /// <param name="root">The saved pictures' folder (<see cref="RootIn"/>); nothing happens when it does not exist.</param>
    /// <param name="nowUtc">Now.</param>
    /// <param name="keepDays">The setting (<c>AppSettings.TempImageDays</c>).</param>
    /// <returns>How many pictures were deleted.</returns>
    public static int Clean(string root, DateTimeOffset nowUtc, int keepDays)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return 0;
        }

        int deleted = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            var written = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            bool leftover = file.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && nowUtc - written >= TimeSpan.FromHours(1);
            if (!leftover && !IsExpired(written, nowUtc, keepDays))
            {
                continue;
            }

            try
            {
                file.Delete();
                deleted += leftover ? 0 : 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held by another app (an editor), or read-only: kept until a later start.
            }
        }

        // Deepest first, so a folder emptied by its children's removal goes too; a folder with anything left stays.
        foreach (var folder in new DirectoryInfo(root).EnumerateDirectories("*", options).OrderByDescending(d => d.FullName.Length))
        {
            try
            {
                if (!folder.EnumerateFileSystemInfos().Any())
                {
                    folder.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use (an Explorer window showing it): kept.
            }
        }

        return deleted;
    }

    /// <summary>Whether a file holds exactly <paramref name="bytes"/> (length first, so a different picture costs no read).</summary>
    /// <param name="path">The file.</param>
    /// <param name="bytes">The bytes to compare with.</param>
    /// <returns><see langword="true"/> when equal; <see langword="false"/> when different or unreadable.</returns>
    private static bool SameBytes(string path, byte[] bytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length != bytes.LongLength)
            {
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var existing = new byte[bytes.Length];
            stream.ReadExactly(existing);
            return existing.AsSpan().SequenceEqual(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // unreadable: treated as different, so a new copy is written next to it
        }
    }
}
