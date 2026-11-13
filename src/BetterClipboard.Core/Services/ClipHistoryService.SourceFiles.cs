using BetterClipboard.Core.Model;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Services;

/// <summary>
/// A picture's source file through the service (CLAUDE.md §2.49): the candidates of the one-time search for the files of
/// screenshots stored before BetterClipboard kept files, and the files it found, recorded through the worker.
/// </summary>
/// <remarks>
/// A file read with a capture needs nothing here: it travels in <see cref="ClipCapture.File"/> through
/// <see cref="AddAsync"/> like any capture. Each recorded file raises <see cref="ClipChangeKind.Updated"/> for its entry, so
/// an open panel's card gains the file's menu items without a reload; the place in the list does not change.
/// </remarks>
public sealed partial class ClipHistoryService
{
    /// <summary>
    /// Lists, on the thread pool, the pictures of a screenshot tab with no file known for them (see
    /// <see cref="ClipStore.FindPicturesWithoutFile"/>).
    /// </summary>
    /// <param name="filter">The tab whose pictures are listed (<see cref="ClipFilter.ShareX"/>, <see cref="ClipFilter.Snipping"/>).</param>
    /// <returns>The candidates, newest first; faults with <see cref="ArgumentException"/> for a tab without a filter, or the store's exception.</returns>
    public Task<IReadOnlyList<PictureWithoutFile>> GetPicturesWithoutFileAsync(ClipFilter filter) =>
        Task.Run(() => store.FindPicturesWithoutFile(filter));

    /// <summary>
    /// Records files found for pictures already in the history, through the worker; an entry that has a file meanwhile (a
    /// live save of the same screenshot, say) keeps its own, newer one.
    /// </summary>
    /// <param name="found">Entries and their files.</param>
    /// <returns>How many entries gained a file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="found"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    public Task<int> SetSourceFilesAsync(IReadOnlyList<(long Id, SourceFile File)> found)
    {
        ArgumentNullException.ThrowIfNull(found);

        // Copied on the caller's thread: the worker runs later, and the caller may reuse its list.
        var copy = found.ToArray();
        return EnqueueAsync(() =>
        {
            int recorded = 0;
            foreach (var (id, file) in copy)
            {
                if (store.SetSourceFile(id, file) is { } entry)
                {
                    recorded++;
                    Raise(new ClipChangedEventArgs(ClipChangeKind.Updated, entry));
                }
            }

            return Task.FromResult(recorded);
        });
    }
}
