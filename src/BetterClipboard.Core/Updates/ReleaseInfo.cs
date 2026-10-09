namespace BetterClipboard.Core.Updates;

/// <summary>
/// One file attached to a release (a release zip, <c>SHA256SUMS.txt</c>, the installer), as the release feed lists it.
/// </summary>
/// <param name="Name">The file's name, e.g. <c>BetterClipboard-0.2.6-win-x64.zip</c>.</param>
/// <param name="Size">Its size in bytes as the feed states it; 0 when the feed does not say. A download of another size is refused.</param>
/// <param name="DownloadUrl">
/// Where to download it. Taken from the feed as is: callers check it with <see cref="UpdateSource.IsTrustedDownload"/>
/// before requesting it.
/// </param>
/// <param name="Sha256">
/// The SHA-256 the host computed when the file was uploaded (GitHub's asset <c>digest</c>), as 64 lower-case hex digits,
/// or <see langword="null"/> when the feed has none. A second witness next to <c>SHA256SUMS.txt</c>.
/// </param>
public sealed record ReleaseAsset(string Name, long Size, Uri DownloadUrl, string? Sha256);

/// <summary>
/// One published release of BetterClipboard: its version, its notes (the changelog text) and its files.
/// </summary>
/// <remarks>
/// Built from network input by <see cref="GitHubReleases.Parse"/>, which bounds every text's length. The notes are
/// Markdown as the maintainer wrote them; they are shown as text and links only, never run or rendered as HTML.
/// </remarks>
/// <param name="Version">The version read from <paramref name="Tag"/>.</param>
/// <param name="Tag">The release's tag, e.g. <c>v0.2.6</c>.</param>
/// <param name="Title">The release's title, e.g. <c>BetterClipboard 0.2.6</c>; empty when the feed has none.</param>
/// <param name="Notes">The release notes in Markdown; empty when the feed has none.</param>
/// <param name="PageUrl">The release's web page, or <see langword="null"/> when the feed has none or it is not an http(s) address.</param>
/// <param name="PublishedAt">When the release was published, or <see langword="null"/> when unknown.</param>
/// <param name="IsPrerelease">Whether the feed marks it a pre-release, or its version has a pre-release part. Never offered as an update.</param>
/// <param name="Assets">Its files; empty when it has none.</param>
public sealed record ReleaseInfo(
    SemanticVersion Version,
    string Tag,
    string Title,
    string Notes,
    Uri? PageUrl,
    DateTimeOffset? PublishedAt,
    bool IsPrerelease,
    IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>
    /// Finds an attached file by its exact name (case-insensitive, as release hosts treat names).
    /// </summary>
    /// <param name="name">The file name.</param>
    /// <returns>The file, or <see langword="null"/> when the release has none of that name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public ReleaseAsset? FindAsset(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var asset in Assets)
        {
            if (string.Equals(asset.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return asset;
            }
        }

        return null;
    }
}
