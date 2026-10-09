namespace BetterClipboard.Core.Updates;

/// <summary>
/// An update the app can install: a release newer than the running version, with the package for this PC's architecture
/// and the checksum list that vouches for it.
/// </summary>
/// <param name="Release">The release.</param>
/// <param name="Package">Its zip for this architecture (<see cref="UpdateCatalog.PackageFileName"/>).</param>
/// <param name="Checksums">Its <c>SHA256SUMS.txt</c>, which must list <paramref name="Package"/>.</param>
public sealed record UpdateOffer(ReleaseInfo Release, ReleaseAsset Package, ReleaseAsset Checksums);

/// <summary>What the releases in a <see cref="Changelog"/> are, relative to the running version.</summary>
public enum ChangelogKind
{
    /// <summary>Nothing to show (no release list yet, or it holds no stable release).</summary>
    None = 0,

    /// <summary>Releases newer than the running version, newest first: what an update brings.</summary>
    NewerReleases = 1,

    /// <summary>The release of exactly the running version: what this version brought.</summary>
    CurrentRelease = 2,

    /// <summary>
    /// The newest stable release, which is not the running version: the running build is newer than every release (a
    /// development build), or its own release is not in the list.
    /// </summary>
    LatestRelease = 3,
}

/// <summary>
/// The release notes the update dialog shows below its buttons.
/// </summary>
/// <param name="Kind">What <paramref name="Releases"/> are.</param>
/// <param name="Releases">The releases whose notes are shown, newest first; empty for <see cref="ChangelogKind.None"/>.</param>
/// <param name="OlderReleasesLeftOut">
/// How many more newer releases exist than <paramref name="Releases"/> holds (a copy many versions behind): the dialog
/// points at the releases page for them.
/// </param>
public sealed record Changelog(ChangelogKind Kind, IReadOnlyList<ReleaseInfo> Releases, int OlderReleasesLeftOut = 0)
{
    /// <summary>The changelog before any release list is known.</summary>
    public static Changelog Empty { get; } = new(ChangelogKind.None, []);
}

/// <summary>
/// Decides, from a release list and the running version, whether there is an update and which release notes to show.
/// Pure: no network, no clock.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stable releases only.</b> A pre-release (<c>v0.3.0-rc.1</c>, or one GitHub marks as such) is never offered and
/// never listed: people who install a release must not be moved to a test build by an update button.
/// </para>
/// <para>
/// <b>Only complete releases are offered:</b> the release must carry this architecture's zip and <c>SHA256SUMS.txt</c>.
/// A newer release without them is skipped in favor of the next one that has them, so an update is never offered that
/// could not be verified.
/// </para>
/// </remarks>
public static class UpdateCatalog
{
    /// <summary>The name of the checksum list every release carries (written by <c>tools/release/package.ps1</c>).</summary>
    public const string ChecksumsFileName = "SHA256SUMS.txt";

    /// <summary>The most newer releases whose notes the dialog shows; a copy further behind gets a link to the rest.</summary>
    public const int MaxChangelogReleases = 10;

    /// <summary>
    /// The name of a release's package for one architecture: <c>BetterClipboard-&lt;version&gt;-win-&lt;architecture&gt;.zip</c>
    /// — the name <c>package.ps1</c> writes and <c>install.ps1</c> looks for.
    /// </summary>
    /// <param name="version">The release's version.</param>
    /// <param name="architecture"><c>x64</c> or <c>arm64</c>.</param>
    /// <returns>The file name.</returns>
    /// <exception cref="ArgumentException"><paramref name="architecture"/> is neither <c>x64</c> nor <c>arm64</c>.</exception>
    public static string PackageFileName(SemanticVersion version, string architecture) =>
        $"BetterClipboard-{version}-win-{NormalizeArchitecture(architecture)}.zip";

    /// <summary>
    /// The newest stable release in <paramref name="releases"/>, complete or not.
    /// </summary>
    /// <param name="releases">The release list, in any order.</param>
    /// <returns>The release, or <see langword="null"/> when the list holds no stable release.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="releases"/> is <see langword="null"/>.</exception>
    public static ReleaseInfo? LatestStable(IReadOnlyList<ReleaseInfo> releases)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ReleaseInfo? latest = null;
        foreach (var release in releases)
        {
            if (!release.IsPrerelease && (latest is null || release.Version > latest.Version))
            {
                latest = release;
            }
        }

        return latest;
    }

    /// <summary>
    /// Finds the update to offer: the newest stable release that is newer than <paramref name="current"/> and complete
    /// for <paramref name="architecture"/>.
    /// </summary>
    /// <param name="current">The running app's version (a pre-release build is older than its own release).</param>
    /// <param name="releases">The release list, in any order.</param>
    /// <param name="architecture">This PC's architecture: <c>x64</c> or <c>arm64</c>.</param>
    /// <returns>The offer, or <see langword="null"/> when the app is up to date or no newer release is complete.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="releases"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="architecture"/> is neither <c>x64</c> nor <c>arm64</c>.</exception>
    public static UpdateOffer? FindUpdate(SemanticVersion current, IReadOnlyList<ReleaseInfo> releases, string architecture)
    {
        ArgumentNullException.ThrowIfNull(releases);
        architecture = NormalizeArchitecture(architecture);
        UpdateOffer? best = null;
        foreach (var release in releases)
        {
            if (release.IsPrerelease || release.Version <= current || (best is not null && release.Version <= best.Release.Version))
            {
                continue;
            }

            var package = release.FindAsset(PackageFileName(release.Version, architecture));
            var checksums = release.FindAsset(ChecksumsFileName);
            if (package is not null && checksums is not null)
            {
                best = new UpdateOffer(release, package, checksums);
            }
        }

        return best;
    }

    /// <summary>
    /// Chooses the release notes to show for a running version: what is new in the releases after it, or, when there are
    /// none, what its own release brought.
    /// </summary>
    /// <param name="current">The running app's version.</param>
    /// <param name="releases">The release list, in any order.</param>
    /// <returns>
    /// The stable releases newer than <paramref name="current"/>, newest first (at most <see cref="MaxChangelogReleases"/>);
    /// else the release of exactly <paramref name="current"/>; else the newest stable release; else
    /// <see cref="Changelog.Empty"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="releases"/> is <see langword="null"/>.</exception>
    public static Changelog SelectChangelog(SemanticVersion current, IReadOnlyList<ReleaseInfo> releases)
    {
        ArgumentNullException.ThrowIfNull(releases);
        var stable = releases.Where(r => !r.IsPrerelease).OrderByDescending(r => r.Version).ToList();
        var newer = stable.Where(r => r.Version > current).ToList();
        if (newer.Count > 0)
        {
            return new Changelog(ChangelogKind.NewerReleases, newer.Take(MaxChangelogReleases).ToArray(), Math.Max(0, newer.Count - MaxChangelogReleases));
        }

        if (stable.FirstOrDefault(r => r.Version == current) is { } own)
        {
            return new Changelog(ChangelogKind.CurrentRelease, [own]);
        }

        return stable.Count > 0 ? new Changelog(ChangelogKind.LatestRelease, [stable[0]]) : Changelog.Empty;
    }

    /// <summary>Checks and lower-cases an architecture name.</summary>
    /// <param name="architecture">The name.</param>
    /// <returns><c>x64</c> or <c>arm64</c>.</returns>
    /// <exception cref="ArgumentException">Another name, or none.</exception>
    private static string NormalizeArchitecture(string architecture) => architecture?.Trim().ToLowerInvariant() switch
    {
        "x64" => "x64",
        "arm64" => "arm64",
        _ => throw new ArgumentException($"BetterClipboard has packages for x64 and arm64 only, not for '{architecture}'.", nameof(architecture)),
    };
}
