using BetterClipboard.Core.Content;

namespace BetterClipboard.Core.Everything;

/// <summary>
/// A file or folder the user opened from voidtools Everything — one row of Everything's run history — as the
/// panel's Everything tab shows it. Not a history entry: it lives in Everything and is read live each time.
/// </summary>
/// <remarks>
/// Everything keeps one row per path (how often it was opened, and when last), not one per opening, so a pick
/// is "the latest opening of that file". Everything records an opening when a result is executed (double-click,
/// Enter, Open); copying a result is not one — those copies reach the history as normal captures instead.
/// </remarks>
/// <param name="FullPath">Absolute path, as Everything reports it.</param>
/// <param name="IsFolder">Whether it is a folder (always <see langword="false"/> when read from Run History.csv, which does not say).</param>
/// <param name="Size">Size in bytes, when known.</param>
/// <param name="Modified">Date modified (UTC), when known.</param>
/// <param name="RunCount">How often it was opened from Everything (at least 1 for a real pick).</param>
/// <param name="LastOpened">When it was last opened from Everything (UTC); <see langword="null"/> when Everything has no date.</param>
public sealed record EverythingPick(string FullPath, bool IsFolder, long? Size, DateTimeOffset? Modified, uint RunCount, DateTimeOffset? LastOpened)
{
    /// <summary>
    /// The content hash a stored single-file list of this path has (<see cref="ContentHasher.ForFiles"/>): the key
    /// that merges a pick with a stored copy of the same file, and its "Forget forever" fingerprint.
    /// </summary>
    public string FilesHash => ContentHasher.ForFiles([FullPath]);

    /// <summary>The last path segment (a drive root keeps its full path, e.g. <c>D:\</c>).</summary>
    public string Name
    {
        get
        {
            var trimmed = FullPath.TrimEnd('\\', '/');
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? FullPath : name;
        }
    }

    /// <summary>The containing folder, or <see langword="null"/> for a root.</summary>
    public string? Folder => Path.GetDirectoryName(FullPath.TrimEnd('\\', '/'));
}
