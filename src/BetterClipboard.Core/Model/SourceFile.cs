namespace BetterClipboard.Core.Model;

/// <summary>
/// The file a picture was read from or saved to — a screenshot ShareX, Snipping Tool or Win+PrtScn wrote to disk — and what the
/// file looked like when BetterClipboard read it: kept with the history entry (<see cref="ClipEntry.File"/>) so the card can
/// copy its path, copy it as a file, open it or open its folder, and so its details outlive the file.
/// </summary>
/// <remarks>
/// <para>
/// A snapshot, never refreshed by itself: the file may be moved, edited or deleted afterwards. Whatever acts on
/// <see cref="Path"/> must check that it still exists (the panel shows the path-based actions only while it does). The size,
/// dates and format describe the file as it was read, which is what they still say after it is gone.
/// </para>
/// <para>
/// Times are UTC instants; <see langword="null"/> when the file system did not report one. <see cref="Format"/> is the image
/// container the bytes really are (<see cref="Content.ImageFileFormat"/>: "PNG", "JPEG", …), which can disagree with
/// <see cref="Extension"/> when a tool names a file after the wrong format.
/// </para>
/// </remarks>
/// <param name="Path">The absolute path the file had when it was read (a local drive path or a UNC path).</param>
/// <param name="SizeBytes">Its length in bytes when read, or <see langword="null"/> when unknown.</param>
/// <param name="CreatedUtc">When the file system says the file was created, or <see langword="null"/> when unknown.</param>
/// <param name="ModifiedUtc">When it was last written, or <see langword="null"/> when unknown.</param>
/// <param name="Format">The image container its bytes are in ("PNG", "JPEG", …), or <see langword="null"/> when not recognized.</param>
public sealed record SourceFile(string Path, long? SizeBytes, DateTimeOffset? CreatedUtc, DateTimeOffset? ModifiedUtc, string? Format)
{
    /// <summary>The file's name with its extension ("ShareX_x1.png"), or the whole path when it has no name part.</summary>
    public string Name => System.IO.Path.GetFileName(Path) is { Length: > 0 } name ? name : Path;

    /// <summary>The extension including its dot (".png"), or an empty string when the name has none.</summary>
    public string Extension => System.IO.Path.GetExtension(Path);

    /// <summary>The folder the file is in, or <see langword="null"/> for a path without one (a bare name).</summary>
    public string? Folder => System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } folder ? folder : null;

    /// <summary>
    /// Describes a file just read from disk: its path, the size and dates the file system reports now, and the format its
    /// first bytes are in.
    /// </summary>
    /// <remarks>
    /// Call it right after reading the file, so the details match the bytes that were read. A time the file system does not
    /// keep (it reports the year 1601 then) is left out rather than shown as a date four centuries ago.
    /// </remarks>
    /// <param name="info">The file, refreshed after the read (<see cref="FileSystemInfo.Refresh"/>).</param>
    /// <param name="header">The file's first bytes (all of them is fine): <see cref="Content.ImageFileFormat.Detect"/> names the format.</param>
    /// <returns>The description.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="info"/> is <see langword="null"/>.</exception>
    public static SourceFile FromDisk(FileInfo info, ReadOnlySpan<byte> header)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new SourceFile(
            info.FullName,
            info.Exists ? info.Length : header.Length,
            Known(info.CreationTimeUtc),
            Known(info.LastWriteTimeUtc),
            Content.ImageFileFormat.Detect(header));

        // FILETIME 0 (1601-01-01) is how Windows reports "no time kept"; anything before 1980 is no screenshot's date either.
        static DateTimeOffset? Known(DateTime utc) => utc.Year < 1980 ? null : new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }
}
