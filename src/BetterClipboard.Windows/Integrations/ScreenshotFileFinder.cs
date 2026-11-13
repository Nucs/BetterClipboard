using System.Diagnostics;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Storage;
using BetterClipboard.Windows.Imaging;

namespace BetterClipboard.Windows.Integrations;

/// <summary>
/// The one-time search for the files of screenshots already in the history when BetterClipboard started keeping a picture's
/// file (CLAUDE.md §2.49; the owner chose "Find their files once … as a migration"): each such picture of a screenshot tab
/// gets the file in that tab's folders that was saved within <see cref="Window"/> of the entry and holds the same pixels.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why by time first.</b> A screenshot's entry is stored the moment its file is written (the watcher's capture time is
/// the file's write time) or the moment the tool copied it, a few seconds from the save. So only the files written near an
/// entry's time are decoded: one per entry as a rule, never the whole folder. The pixels decide (<see cref="ImageCodec.ContentHashAsync"/>
/// equals the entry's content hash), so a file edited since, or another screenshot saved in the same minute, is never taken.
/// </para>
/// <para>
/// <b>Safe on the user's folders.</b> Files are only read, with every sharing mode granted (a tool writing one is never
/// refused); cloud placeholders are skipped (reading one would download it), and so are files over
/// <see cref="MaxFileBytes"/>. A file is given to one entry at most. An entry that meanwhile got a file of its own keeps it
/// (<see cref="ClipStore.SetSourceFile"/> fills only what is missing), so running it next to a live import is harmless.
/// </para>
/// <para>
/// <b>Once.</b> The caller marks each tab's search done in the store's state (<see cref="RunOnceAsync"/>) only when it ran
/// to the end: an exit halfway resumes at the next start, and the search is idempotent anyway. Logs counts only, never a
/// path or a file name (a name can hold a window title).
/// </para>
/// </remarks>
public static class ScreenshotFileFinder
{
    /// <summary>How far from an entry's time its file may have been written (a screenshot's copy and its save lie seconds apart).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    /// <summary>The largest file read to compare (screenshots are a few megabytes; a huge file is not one).</summary>
    public const long MaxFileBytes = 256L * 1024 * 1024;

    /// <summary>File attributes of a cloud placeholder (recall on open or on data access, offline): never read.</summary>
    private const FileAttributes PlaceholderAttributes = (FileAttributes)0x00040000 | (FileAttributes)0x00400000 | FileAttributes.Offline;

    /// <summary>
    /// Runs the search for one screenshot tab unless the store says it ran already, and marks it done when it ran to the end.
    /// </summary>
    /// <param name="history">The history.</param>
    /// <param name="tab">The tab whose pictures are searched for (<see cref="ClipFilter.ShareX"/>, <see cref="ClipFilter.Snipping"/>).</param>
    /// <param name="doneStateName">The state name that marks this tab's search done.</param>
    /// <param name="listFiles">Lists the image files of the tab's folders (called on the thread pool; may return an empty list).</param>
    /// <param name="label">What the log calls these screenshots ("ShareX", "Snipping Tool and Win+PrtScn").</param>
    /// <param name="cancellationToken">Stops the search (exit): it then resumes at the next start.</param>
    /// <returns>A task completing when done, skipped or stopped; failures are logged, never thrown (fire-and-forget safe).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="history"/> or <paramref name="listFiles"/> is <see langword="null"/> (a caller's bug: the returned task faults with it before anything runs).</exception>
    public static async Task RunOnceAsync(
        ClipHistoryService history, ClipFilter tab, string doneStateName, Func<IReadOnlyList<FileInfo>> listFiles, string label, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(listFiles);
        try
        {
            if (!string.IsNullOrEmpty(await history.GetStateValueAsync(doneStateName).ConfigureAwait(false)))
            {
                return;
            }

            var clock = Stopwatch.StartNew();
            var candidates = await history.GetPicturesWithoutFileAsync(tab).ConfigureAwait(false);
            int found = 0;
            if (candidates.Count > 0)
            {
                var files = await Task.Run(listFiles, cancellationToken).ConfigureAwait(false);
                var matches = await MatchAsync(candidates, files, cancellationToken).ConfigureAwait(false);
                found = matches.Count == 0 ? 0 : await history.SetSourceFilesAsync(matches).ConfigureAwait(false);
            }

            await history.SetStateValueAsync(doneStateName, DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
            AppLog.Info($"Earlier {label} screenshots: found the files of {found} of {candidates.Count} in {clock.ElapsedMilliseconds} ms (searched once; new ones keep their file as they are saved).");
        }
        catch (OperationCanceledException)
        {
            // Exiting: not marked done, so the next start resumes it.
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Finding the files of earlier {label} screenshots failed ({ex.GetType().Name}: {ex.Message}); the next start tries again.");
        }
    }

    /// <summary>
    /// Finds, for each picture, the file among <paramref name="files"/> written within <see cref="Window"/> of it whose pixels
    /// are the picture's (see the class remarks). Each file is read and decoded at most once, and given to one picture at most.
    /// </summary>
    /// <param name="candidates">Pictures with no file known (<see cref="ClipStore.FindPicturesWithoutFile"/>).</param>
    /// <param name="files">The image files of the tab's folders.</param>
    /// <param name="cancellationToken">Stops between files.</param>
    /// <returns>The pictures found and their files, with each file's details as read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidates"/> or <paramref name="files"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">Stopped.</exception>
    public static async Task<IReadOnlyList<(long Id, SourceFile File)>> MatchAsync(
        IReadOnlyList<PictureWithoutFile> candidates, IReadOnlyList<FileInfo> files, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(files);
        var byTime = files.Where(f => (f.Attributes & PlaceholderAttributes) == 0)
            .OrderBy(f => f.LastWriteTimeUtc.Ticks)
            .ToArray();
        var times = byTime.Select(f => f.LastWriteTimeUtc.Ticks).ToArray();
        var read = new Dictionary<string, (string? Hash, SourceFile? File)>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<(long Id, SourceFile File)>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long at = candidate.CreatedUtc.UtcTicks;
            int first = LowerBound(times, at - Window.Ticks);

            // The files in the window, the closest to the entry's time first: its own save is nearly always the first one read.
            var near = new List<FileInfo>();
            for (int i = first; i < byTime.Length && times[i] <= at + Window.Ticks; i++)
            {
                if (!taken.Contains(byTime[i].FullName))
                {
                    near.Add(byTime[i]);
                }
            }

            foreach (var file in near.OrderBy(f => Math.Abs(f.LastWriteTimeUtc.Ticks - at)))
            {
                if (!read.TryGetValue(file.FullName, out var known))
                {
                    known = await ReadAsync(file, cancellationToken).ConfigureAwait(false);
                    read[file.FullName] = known;
                }

                if (known.Hash is not null && known.File is not null && string.Equals(known.Hash, candidate.ContentHash, StringComparison.Ordinal))
                {
                    taken.Add(file.FullName);
                    found.Add((candidate.Id, known.File));
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>Reads one file and computes its content hash and details; any failure makes it "no match".</summary>
    /// <param name="file">The file.</param>
    /// <param name="cancellationToken">Stops the decode.</param>
    /// <returns>Its content hash and details, or nulls when it is too large, gone, unreadable or not a picture.</returns>
    /// <exception cref="OperationCanceledException">Stopped.</exception>
    private static async Task<(string? Hash, SourceFile? File)> ReadAsync(FileInfo file, CancellationToken cancellationToken)
    {
        try
        {
            file.Refresh();
            if (!file.Exists || file.Length == 0 || file.Length > MaxFileBytes)
            {
                return (null, null);
            }

            byte[] bytes;
            await using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var copy = new MemoryStream(checked((int)Math.Min(stream.Length, int.MaxValue)));
                await stream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
                bytes = copy.ToArray();
            }

            var hash = await ImageCodec.ContentHashAsync(bytes, cancellationToken).ConfigureAwait(false);
            file.Refresh();
            return (hash, SourceFile.FromDisk(file, bytes));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Gone, locked without sharing, or not a picture WIC can read: not this entry's file. Nothing is logged per file
            // (its name could hold a window title); the summary counts what was found.
            return (null, null);
        }
    }

    /// <summary>The index of the first value not below <paramref name="value"/> in an ascending array (its length when none).</summary>
    /// <param name="sorted">Ascending values.</param>
    /// <param name="value">The value to place.</param>
    /// <returns>The index.</returns>
    private static int LowerBound(long[] sorted, long value)
    {
        int index = Array.BinarySearch(sorted, value);
        if (index < 0)
        {
            return ~index;
        }

        // Equal values may repeat: step back to the first of them.
        while (index > 0 && sorted[index - 1] == value)
        {
            index--;
        }

        return index;
    }
}
