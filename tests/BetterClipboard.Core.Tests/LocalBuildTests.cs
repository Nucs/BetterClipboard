using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Updates;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Writes development builds into a dev-builds folder the way <c>tools/release/publish-dev.ps1</c> does, and variations
/// of them that are not (yet) complete builds.
/// </summary>
internal static class LocalBuildFixture
{
    /// <summary>When the fixture's builds were published, unless a test says otherwise.</summary>
    public static readonly DateTimeOffset PublishedAt = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Publishes a build: its zip, <c>SHA256SUMS.txt</c>, optional notes, and <c>build.json</c> last.
    /// </summary>
    /// <param name="devBuilds">The dev-builds folder (created when missing).</param>
    /// <param name="version">The build's version.</param>
    /// <param name="package">The zip's bytes.</param>
    /// <param name="notes">The build's notes; empty writes no <c>notes.md</c>.</param>
    /// <param name="architecture">The architecture of the zip.</param>
    /// <param name="checksums">The checksum list's text; <see langword="null"/> writes the true one.</param>
    /// <param name="writeBuildFile">Whether to write <c>build.json</c> (the file that makes the folder a build).</param>
    /// <param name="folderName">The folder's name; <see langword="null"/> for the version, as the script names it.</param>
    /// <param name="publishedAt">The publication time written; <see langword="null"/> for <see cref="PublishedAt"/>.</param>
    /// <returns>The build's folder.</returns>
    public static string Publish(
        string devBuilds,
        string version,
        byte[] package,
        string notes = "",
        string architecture = "x64",
        string? checksums = null,
        bool writeBuildFile = true,
        string? folderName = null,
        DateTimeOffset? publishedAt = null)
    {
        var parsed = UpdateTestData.Version(version);
        var folder = Path.Combine(devBuilds, folderName ?? parsed.ToString());
        Directory.CreateDirectory(folder);
        var zipName = UpdateCatalog.PackageFileName(parsed, architecture);
        File.WriteAllBytes(Path.Combine(folder, zipName), package);
        File.WriteAllText(Path.Combine(folder, UpdateCatalog.ChecksumsFileName), checksums ?? $"{Sha256Of(package)}  {zipName}\n", Encoding.ASCII);
        if (notes.Length > 0)
        {
            File.WriteAllText(Path.Combine(folder, LocalBuildFeed.NotesFileName), notes, new UTF8Encoding(false));
        }

        if (writeBuildFile)
        {
            var at = (publishedAt ?? PublishedAt).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            File.WriteAllText(Path.Combine(folder, LocalBuildFeed.BuildFileName), $"{{\"version\":\"{parsed}\",\"publishedUtc\":\"{at}\",\"commit\":\"0123456789abcdef0123456789abcdef01234567\"}}");
        }

        return folder;
    }

    /// <summary>Deterministic bytes for a "package".</summary>
    /// <param name="length">How many.</param>
    /// <param name="seed">Makes different packages differ.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Package(int length = 50_000, int seed = 7)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>The lower-case hex SHA-256 of bytes.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>64 hex digits.</returns>
    public static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>
/// Tests for <see cref="LocalBuildFeed"/>: which folders of the dev-builds folder are builds, and the verified copy that
/// takes a build's package to the updater.
/// </summary>
public sealed class LocalBuildFeedTests
{
    /// <summary>Complete builds are read newest first, as local builds with file assets, their notes and their time.</summary>
    [Fact]
    public void Read_FindsCompleteBuilds_NewestFirst()
    {
        using var temp = new TempDirectory();
        var devBuilds = Path.Combine(temp.Path, LocalBuildFeed.DirectoryName);
        LocalBuildFixture.Publish(devBuilds, "0.2.10-dev.12.gabc1234", LocalBuildFixture.Package(seed: 1), notes: "- First (abc1234)\n");
        LocalBuildFixture.Publish(devBuilds, "0.2.10-dev.15.gdef5678", LocalBuildFixture.Package(seed: 2), notes: "- Second (def5678)\n");

        var builds = LocalBuildFeed.Read(devBuilds, "x64");

        Assert.Equal(["0.2.10-dev.15.gdef5678", "0.2.10-dev.12.gabc1234"], builds.Select(b => b.Version.ToString()));
        var newest = builds[0];
        Assert.True(newest.IsLocalBuild);
        Assert.True(newest.IsPrerelease);
        Assert.Null(newest.PageUrl);
        Assert.Equal("v0.2.10-dev.15.gdef5678", newest.Tag);
        Assert.Equal("BetterClipboard 0.2.10-dev.15.gdef5678", newest.Title);
        Assert.Equal("- Second (def5678)\n", newest.Notes);
        Assert.Equal(LocalBuildFixture.PublishedAt, newest.PublishedAt);
        var package = newest.FindAsset("BetterClipboard-0.2.10-dev.15.gdef5678-win-x64.zip")!;
        Assert.True(package.DownloadUrl.IsFile);
        Assert.Equal(50_000, package.Size);
        Assert.Null(package.Sha256);
        Assert.NotNull(newest.FindAsset(UpdateCatalog.ChecksumsFileName));
        Assert.True(LocalBuildFeed.IsInside(devBuilds, package.DownloadUrl));
    }

    /// <summary>
    /// A folder that is not a complete build for this architecture is never read as one: a staging folder, one without
    /// <c>build.json</c> or with a broken one, a name that is not its version, a missing or empty package, a missing
    /// checksum list or one that does not list the package, and a build for another architecture only.
    /// </summary>
    [Fact]
    public void Read_SkipsWhatIsNotACompleteBuild()
    {
        using var temp = new TempDirectory();
        var devBuilds = temp.Path;
        var package = LocalBuildFixture.Package();
        LocalBuildFixture.Publish(devBuilds, "0.2.10-dev.1.ga", package, folderName: ".staging-1");
        LocalBuildFixture.Publish(devBuilds, "0.2.10-dev.2.gb", package, writeBuildFile: false);
        File.WriteAllText(Path.Combine(LocalBuildFixture.Publish(devBuilds, "0.2.10-dev.3.gc", package), LocalBuildFeed.BuildFileName), "{\"version\":");
        LocalBuildFixture.Publish(devBuilds, "0.2.10-dev.4.gd", package, folderName: "renamed-copy");
        File.Delete(Path.Combine(LocalBuildFixture.Publish(devBuilds, "0.2.10-dev.5.ge", package), "BetterClipboard-0.2.10-dev.5.ge-win-x64.zip"));
        LocalBuildFixture.Publish(devBuilds, "0.2.10-dev.6.gf", [], checksums: $"{LocalBuildFixture.Sha256Of([])}  BetterClipboard-0.2.10-dev.6.gf-win-x64.zip\n");
        File.Delete(Path.Combine(LocalBuildFixture.Publish(devBuilds, "0.2.10-dev.7.gg", package), UpdateCatalog.ChecksumsFileName));
        LocalBuildFixture.Publish(devBuilds, "0.2.10-dev.8.gh", package, checksums: $"{LocalBuildFixture.Sha256Of(package)}  another.zip\n");
        LocalBuildFixture.Publish(devBuilds, "0.2.10-dev.9.gi", package, architecture: "arm64");
        File.WriteAllText(Path.Combine(LocalBuildFixture.Publish(devBuilds, "0.2.10-dev.10.gj", package, writeBuildFile: false), LocalBuildFeed.BuildFileName), "{\"version\":\"not a version\"}");
        LocalBuildFixture.Publish(devBuilds, "0.2.10-dev.11.gk", package);

        var builds = LocalBuildFeed.Read(devBuilds, "x64");

        Assert.Equal("0.2.10-dev.11.gk", Assert.Single(builds).Version.ToString());
        Assert.Equal("0.2.10-dev.9.gi", Assert.Single(LocalBuildFeed.Read(devBuilds, "arm64")).Version.ToString());
    }

    /// <summary>No folder, a blank path or a folder that is not there reads as no builds; a wrong architecture is a bug.</summary>
    [Fact]
    public void Read_NothingThere_IsEmpty()
    {
        using var temp = new TempDirectory();
        Assert.Empty(LocalBuildFeed.Read(null, "x64"));
        Assert.Empty(LocalBuildFeed.Read("  ", "x64"));
        Assert.Empty(LocalBuildFeed.Read(Path.Combine(temp.Path, "missing"), "x64"));
        Assert.Empty(LocalBuildFeed.Read(temp.Path, "arm64"));
        Assert.Throws<ArgumentException>(() => LocalBuildFeed.Read(temp.Path, "x86"));
    }

    /// <summary>
    /// Notes longer than the dialog shows are cut, a byte order mark is dropped, and a title from <c>build.json</c> is
    /// used when it has one.
    /// </summary>
    [Fact]
    public void Read_BoundsTheNotes_AndTakesATitle()
    {
        using var temp = new TempDirectory();
        var folder = LocalBuildFixture.Publish(temp.Path, "0.2.10-dev.3.gabc", LocalBuildFixture.Package(), notes: new string('x', 300_000));
        File.WriteAllText(Path.Combine(folder, LocalBuildFeed.BuildFileName), "{\"version\":\"0.2.10-dev.3.gabc\",\"title\":\"  My build  \",\"publishedUtc\":\"later\"}");
        var build = Assert.Single(LocalBuildFeed.Read(temp.Path, "x64"));
        Assert.Equal(GitHubReleases.MaxNotesLength, build.Notes.Length);
        Assert.Equal("My build", build.Title);
        Assert.Null(build.PublishedAt);

        File.WriteAllText(Path.Combine(folder, LocalBuildFeed.NotesFileName), "- Fix (abc)\n", new UTF8Encoding(true));
        Assert.Equal("- Fix (abc)\n", Assert.Single(LocalBuildFeed.Read(temp.Path, "x64")).Notes);
    }

    /// <summary>The copy writes the package where the updater wants it, hashes it on the way and reports its progress.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task CopyPackage_CopiesHashesAndReportsProgress()
    {
        using var temp = new TempDirectory();
        var devBuilds = Path.Combine(temp.Path, "dev-builds");
        var bytes = LocalBuildFixture.Package(300_000);
        LocalBuildFixture.Publish(devBuilds, "0.2.10-dev.4.gabc", bytes);
        var build = Assert.Single(LocalBuildFeed.Read(devBuilds, "x64"));
        var target = Path.Combine(temp.Path, "updates", "BetterClipboard-0.2.10-dev.4.gabc-win-x64.zip");
        var reports = new List<long>();

        var sha256 = await LocalBuildFeed.CopyPackageAsync(build.Assets[0], devBuilds, target, new SyncProgress(reports.Add), TestContext.Current.CancellationToken);

        Assert.Equal(LocalBuildFixture.Sha256Of(bytes), sha256);
        Assert.Equal(bytes, File.ReadAllBytes(target));
        Assert.False(File.Exists(target + ".part"));
        Assert.Equal(bytes.Length, reports[^1]);
        Assert.Contains($"{sha256}  BetterClipboard-0.2.10-dev.4.gabc-win-x64.zip", await LocalBuildFeed.ReadChecksumsAsync(build.Assets[1], devBuilds, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    /// <summary>
    /// Files outside the dev-builds folder are never read, a package that changed after it was offered is refused, and a
    /// package that is gone is a storage failure — each without leaving a partial copy.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task CopyPackage_RefusesWhatItMustNotUse()
    {
        using var temp = new TempDirectory();
        var devBuilds = Path.Combine(temp.Path, "dev-builds");
        var bytes = LocalBuildFixture.Package();
        var folder = LocalBuildFixture.Publish(devBuilds, "0.2.10-dev.4.gabc", bytes);
        var build = Assert.Single(LocalBuildFeed.Read(devBuilds, "x64"));
        var package = build.Assets[0];
        var target = Path.Combine(temp.Path, "updates", package.Name);
        var token = TestContext.Current.CancellationToken;

        // Outside: a file next to the folder, reached through "..".
        var outside = Path.Combine(temp.Path, "evil.zip");
        File.WriteAllBytes(outside, bytes);
        var escape = package with { DownloadUrl = new Uri(Path.Combine(devBuilds, "..", "evil.zip")) };
        var refused = await Assert.ThrowsAsync<UpdateException>(() => LocalBuildFeed.CopyPackageAsync(escape, devBuilds, target, null, token));
        Assert.Equal(UpdateFailure.BadResponse, refused.Failure);
        var outsideList = await Assert.ThrowsAsync<UpdateException>(() => LocalBuildFeed.ReadChecksumsAsync(build.Assets[1] with { DownloadUrl = new Uri(outside) }, devBuilds, token));
        Assert.Equal(UpdateFailure.BadResponse, outsideList.Failure);

        // Changed: published again with another size under the same version.
        File.WriteAllBytes(Path.Combine(folder, package.Name), LocalBuildFixture.Package(bytes.Length + 10, seed: 9));
        var changed = await Assert.ThrowsAsync<UpdateException>(() => LocalBuildFeed.CopyPackageAsync(package, devBuilds, target, null, token));
        Assert.Equal(UpdateFailure.BadResponse, changed.Failure);

        // Gone.
        File.Delete(Path.Combine(folder, package.Name));
        var gone = await Assert.ThrowsAsync<UpdateException>(() => LocalBuildFeed.CopyPackageAsync(package, devBuilds, target, null, token));
        Assert.Equal(UpdateFailure.Storage, gone.Failure);

        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    /// <summary>A cancelled copy leaves neither the package nor a partial file behind.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task CopyPackage_Cancelled_LeavesNothing()
    {
        using var temp = new TempDirectory();
        var devBuilds = Path.Combine(temp.Path, "dev-builds");
        LocalBuildFixture.Publish(devBuilds, "0.2.10-dev.4.gabc", LocalBuildFixture.Package(2_000_000));
        var build = Assert.Single(LocalBuildFeed.Read(devBuilds, "x64"));
        var target = Path.Combine(temp.Path, "updates", build.Assets[0].Name);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalBuildFeed.CopyPackageAsync(build.Assets[0], devBuilds, target, null, cancel.Token));

        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    /// <summary>Only <c>file:</c> addresses of files strictly inside the folder are inside it.</summary>
    /// <param name="relative">The file, relative to the dev-builds folder; <see langword="null"/> for a web address.</param>
    /// <param name="inside">Whether it counts as inside.</param>
    [Theory]
    [InlineData("0.2.10-dev.1.ga\\BetterClipboard-0.2.10-dev.1.ga-win-x64.zip", true)]
    [InlineData("a.zip", true)]
    [InlineData("..\\a.zip", false)]
    [InlineData("..\\dev-builds-other\\a.zip", false)]
    [InlineData("0.2.10-dev.1.ga\\..\\..\\a.zip", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsInside_OnlyFilesInTheFolder(string? relative, bool inside)
    {
        using var temp = new TempDirectory();
        var devBuilds = Path.Combine(temp.Path, "dev-builds");
        var url = relative is null ? new Uri("https://github.com/Nucs/BetterClipboard/releases/download/v1/a.zip") : new Uri(Path.Combine(devBuilds, relative));
        Assert.Equal(inside, LocalBuildFeed.IsInside(devBuilds, url));
        Assert.False(LocalBuildFeed.IsInside(null, url));
        Assert.False(LocalBuildFeed.IsInside(devBuilds, null));
    }

    /// <summary>An <see cref="IProgress{T}"/> that reports on the calling thread, so a test sees every report in order.</summary>
    /// <param name="handler">Receives each report.</param>
    private sealed class SyncProgress(Action<long> handler) : IProgress<long>
    {
        /// <inheritdoc />
        public void Report(long value) => handler(value);
    }
}

/// <summary>
/// Tests for the owner's rule of 2026-11-08 in <see cref="UpdateCatalog"/>: a newer local build comes first; otherwise a
/// development build is always offered GitHub's latest release, any other build only a newer one.
/// </summary>
public sealed class LocalBuildCatalogTests
{
    /// <summary>A local build as <see cref="LocalBuildFeed.Read"/> lists it.</summary>
    /// <param name="version">Its version.</param>
    /// <param name="notes">Its notes.</param>
    /// <param name="architecture">The architecture of its package.</param>
    /// <returns>The build.</returns>
    private static ReleaseInfo Local(string version, string notes = "", string architecture = "x64")
    {
        var parsed = UpdateTestData.Version(version);
        var folder = $@"C:\Data\dev-builds\{parsed}";
        var name = UpdateCatalog.PackageFileName(parsed, architecture);
        return new ReleaseInfo(parsed, "v" + parsed, "BetterClipboard " + parsed, notes, null, LocalBuildFixture.PublishedAt, parsed.IsPrerelease,
        [
            new ReleaseAsset(name, 1000, new Uri($@"{folder}\{name}"), null),
            new ReleaseAsset(UpdateCatalog.ChecksumsFileName, 100, new Uri($@"{folder}\{UpdateCatalog.ChecksumsFileName}"), null),
        ])
        {
            IsLocalBuild = true,
        };
    }

    /// <summary>What a development build is: the first pre-release identifier is <c>dev</c>, in any case.</summary>
    /// <param name="version">The version.</param>
    /// <param name="dev">Whether it is a development build.</param>
    [Theory]
    [InlineData("0.2.10-dev.12.gabc1234", true)]
    [InlineData("0.2.6-dev.52539eb", true)]
    [InlineData("0.2.8-dev.walk3", true)]
    [InlineData("0.2.10-DEV.1", true)]
    [InlineData("0.2.10-dev", true)]
    [InlineData("0.2.10", false)]
    [InlineData("1.0.0-rc.1", false)]
    [InlineData("0.2.6-devtools.1", false)]
    [InlineData("0.2.6-rc.dev", false)]
    [InlineData("0.2.6+dev", false)]
    public void IsDevBuild_ReadsTheFirstIdentifier(string version, bool dev) =>
        Assert.Equal(dev, UpdateCatalog.IsDevBuild(UpdateTestData.Version(version)));

    /// <summary>The owner's table, row by row (installed, GitHub's releases, local builds, what is offered).</summary>
    /// <param name="installed">The running version.</param>
    /// <param name="github">GitHub's latest release.</param>
    /// <param name="local">The local builds, comma-separated; empty for none.</param>
    /// <param name="offered">The version offered.</param>
    /// <param name="fromFolder">Whether the offer comes from the dev-builds folder.</param>
    [Theory]
    [InlineData("0.2.10-dev.12", "0.2.9", "", "0.2.9", false)]
    [InlineData("0.2.10-dev.12", "0.2.9", "0.2.10-dev.15", "0.2.10-dev.15", true)]
    [InlineData("0.2.10-dev.12", "0.2.10", "", "0.2.10", false)]
    [InlineData("0.2.10-dev.12", "0.2.10", "0.2.10-dev.15", "0.2.10-dev.15", true)]
    [InlineData("0.2.10-dev.12", "0.2.10", "0.2.11-dev.3", "0.2.11-dev.3", true)]
    [InlineData("0.2.10-dev.12", "0.2.9", "0.2.10-dev.12", "0.2.9", false)]
    [InlineData("0.2.10-dev.12", "0.2.9", "0.2.10-dev.8,0.2.10-dev.12", "0.2.9", false)]
    public void FindUpdate_FollowsTheOwnersTable(string installed, string github, string local, string offered, bool fromFolder)
    {
        ReleaseInfo[] releases = [UpdateTestData.Release(github), UpdateTestData.Release("0.2.8")];
        ReleaseInfo[] builds = [.. local.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => Local(v))];

        var offer = UpdateCatalog.FindUpdate(UpdateTestData.Version(installed), releases, builds, "x64");

        Assert.NotNull(offer);
        Assert.Equal(offered, offer.Release.Version.ToString());
        Assert.Equal(fromFolder, offer.Release.IsLocalBuild);
        Assert.Equal(UpdateCatalog.PackageFileName(offer.Release.Version, "x64"), offer.Package.Name);
    }

    /// <summary>
    /// A release (or a release candidate) is offered a newer local build too, but never an older one, and GitHub only
    /// when newer — the rule before local builds existed.
    /// </summary>
    [Fact]
    public void FindUpdate_ForAReleaseBuild()
    {
        ReleaseInfo[] releases = [UpdateTestData.Release("0.2.9"), UpdateTestData.Release("0.2.8")];

        Assert.Equal("0.2.10-dev.3", UpdateCatalog.FindUpdate(UpdateTestData.Version("0.2.9"), releases, [Local("0.2.10-dev.3")], "x64")?.Release.Version.ToString());
        Assert.Null(UpdateCatalog.FindUpdate(UpdateTestData.Version("0.2.9"), releases, [Local("0.2.9-dev.3")], "x64"));
        Assert.Null(UpdateCatalog.FindUpdate(UpdateTestData.Version("0.2.9"), releases, [], "x64"));
        Assert.Equal("0.2.9", UpdateCatalog.FindUpdate(UpdateTestData.Version("0.2.8"), releases, [Local("0.2.8-dev.3")], "x64")?.Release.Version.ToString());
        Assert.Equal("0.2.9", UpdateCatalog.FindUpdate(UpdateTestData.Version("0.2.9-rc.1"), releases, [], "x64")?.Release.Version.ToString());
        Assert.Null(UpdateCatalog.FindUpdate(UpdateTestData.Version("0.3.0-rc.1"), releases, [], "x64"));
    }

    /// <summary>
    /// A local build without this architecture's package is passed over for the next one; without any local build and
    /// any release, nothing is offered; entries in the wrong list are ignored.
    /// </summary>
    [Fact]
    public void FindUpdate_SkipsIncompleteAndMisplacedEntries()
    {
        ReleaseInfo[] builds = [Local("0.2.10-dev.20", architecture: "arm64"), Local("0.2.10-dev.15")];
        Assert.Equal("0.2.10-dev.15", UpdateCatalog.FindUpdate(UpdateTestData.Version("0.2.10-dev.12"), [], builds, "x64")?.Release.Version.ToString());
        Assert.Equal("0.2.10-dev.20", UpdateCatalog.FindUpdate(UpdateTestData.Version("0.2.10-dev.12"), [], builds, "arm64")?.Release.Version.ToString());
        Assert.Null(UpdateCatalog.FindUpdate(UpdateTestData.Version("0.2.10-dev.12"), [], [], "x64"));

        // A GitHub release in the local list, and a local build in the release list, are never offered from there.
        Assert.Null(UpdateCatalog.FindUpdate(UpdateTestData.Version("0.2.10-dev.12"), [Local("0.2.10-dev.15")], [UpdateTestData.Release("0.2.11")], "x64"));
        Assert.Throws<ArgumentNullException>(() => UpdateCatalog.FindUpdate(UpdateTestData.Version("0.2.9"), [], null!, "x64"));
    }

    /// <summary>
    /// A local offer shows the notes of every local build newer than the running one, newest first; any other offer
    /// GitHub's notes; and without a release list, the running build's own local notes.
    /// </summary>
    [Fact]
    public void SelectChangelog_FollowsWhereTheOfferComesFrom()
    {
        var current = UpdateTestData.Version("0.2.10-dev.12");
        ReleaseInfo[] builds = [Local("0.2.10-dev.12", "- twelve"), Local("0.2.10-dev.15", "- fifteen"), Local("0.2.10-dev.13", "- thirteen"), Local("0.2.10-dev.9", "- nine")];
        ReleaseInfo[] releases = [UpdateTestData.Release("0.2.9", "Notes of 0.2.9")];

        var localOffer = UpdateCatalog.FindUpdate(current, releases, builds, "x64");
        var local = UpdateCatalog.SelectChangelog(current, releases, builds, localOffer);
        Assert.Equal(ChangelogKind.NewerReleases, local.Kind);
        Assert.Equal(["- fifteen", "- thirteen"], local.Releases.Select(r => r.Notes));

        var githubOffer = UpdateCatalog.FindUpdate(current, releases, [Local("0.2.10-dev.12")], "x64");
        var github = UpdateCatalog.SelectChangelog(current, releases, [Local("0.2.10-dev.12")], githubOffer);
        Assert.Equal(ChangelogKind.LatestRelease, github.Kind);
        Assert.Equal("Notes of 0.2.9", Assert.Single(github.Releases).Notes);

        var offline = UpdateCatalog.SelectChangelog(current, [], [Local("0.2.10-dev.12", "- twelve")], null);
        Assert.Equal(ChangelogKind.CurrentRelease, offline.Kind);
        Assert.Equal("- twelve", Assert.Single(offline.Releases).Notes);
        Assert.Same(Changelog.Empty, UpdateCatalog.SelectChangelog(current, [], [], null));
    }

    /// <summary>A local offer further behind than the dialog lists counts the builds it leaves out.</summary>
    [Fact]
    public void SelectChangelog_CapsTheLocalBuilds()
    {
        var builds = Enumerable.Range(1, UpdateCatalog.MaxChangelogReleases + 3).Select(i => Local($"0.2.10-dev.{i}")).ToArray();
        var current = UpdateTestData.Version("0.2.9");
        var changelog = UpdateCatalog.SelectChangelog(current, [], builds, UpdateCatalog.FindUpdate(current, [], builds, "x64"));
        Assert.Equal(UpdateCatalog.MaxChangelogReleases, changelog.Releases.Count);
        Assert.Equal($"0.2.10-dev.{UpdateCatalog.MaxChangelogReleases + 3}", changelog.Releases[0].Version.ToString());
        Assert.Equal(3, changelog.OlderReleasesLeftOut);
    }
}

/// <summary>
/// Tests for the words of local builds and of the way back from a development build (<see cref="UpdateText"/>), and for
/// the reminder that development builds never get (<see cref="UpdateReminder"/>).
/// </summary>
public sealed class LocalBuildTextTests
{
    /// <summary>A fixed "now", two hours after the fixture's builds were published.</summary>
    private static readonly DateTimeOffset Now = LocalBuildFixture.PublishedAt.AddHours(2);

    /// <summary>Invariant wording for numbers and dates.</summary>
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>A snapshot offering a release or build.</summary>
    /// <param name="current">The running version.</param>
    /// <param name="offered">The offered release or build.</param>
    /// <param name="kind">How the copy was installed.</param>
    /// <returns>The snapshot, checked just now, with automatic checks on.</returns>
    private static UpdateSnapshot Offering(string current, ReleaseInfo offered, UpdateInstallKind kind = UpdateInstallKind.Installer)
    {
        var offer = UpdateCatalog.FindUpdate(UpdateTestData.Version(current), offered.IsLocalBuild ? [] : [offered], offered.IsLocalBuild ? [offered] : [], "x64");
        Assert.NotNull(offer);
        return new UpdateSnapshot { CurrentVersion = UpdateTestData.Version(current), InstallKind = kind, AutomaticChecks = true, Offer = offer, LastChecked = Now };
    }

    /// <summary>A local build as the feed reads it.</summary>
    /// <param name="version">Its version.</param>
    /// <returns>The build.</returns>
    private static ReleaseInfo Local(string version)
    {
        var release = UpdateTestData.Release(version) with { PageUrl = null, PublishedAt = LocalBuildFixture.PublishedAt };
        return release with { IsLocalBuild = true };
    }

    /// <summary>A local build is named as a dev build, its time is when it was published, and approving it copies it.</summary>
    [Fact]
    public void LocalBuild_SaysWhereItComesFrom()
    {
        var snapshot = Offering("0.2.10-dev.12", Local("0.2.10-dev.15"));
        Assert.True(snapshot.IsLocalOffer);
        Assert.False(snapshot.OffersOlderVersion);
        Assert.Equal("Dev build available: BetterClipboard 0.2.10-dev.15", UpdateText.ButtonTip(snapshot));
        Assert.Equal("BetterClipboard 0.2.10-dev.15 is available", UpdateText.Title(snapshot));
        Assert.Equal(
            "You have 0.2.10-dev.12. Published 2 h ago. It is a development build from the dev-builds folder on this PC: updating copies it from there, checks it, and restarts BetterClipboard. Your history is kept.",
            UpdateText.Detail(snapshot, Now, TimeZoneInfo.Utc, Culture));
        Assert.Equal("Update and restart", UpdateText.ApproveButton(snapshot));
        Assert.Equal("Update to 0.2.10-dev.15…", UpdateText.OfferAction(snapshot));
        Assert.Equal("BetterClipboard 0.2.10-dev.15 is available (you skipped it)", UpdateText.ButtonTip(snapshot with { IsSkipped = true }));

        var copying = snapshot with { Phase = UpdatePhase.Downloading, DownloadedBytes = 50, TotalBytes = 200 };
        Assert.Equal("Copying BetterClipboard 0.2.10-dev.15: 25 %", UpdateText.ButtonTip(copying));
        Assert.Equal("Copying BetterClipboard 0.2.10-dev.15", UpdateText.ButtonTip(copying with { TotalBytes = 0 }));
        Assert.Equal("Copying BetterClipboard 0.2.10-dev.15…", UpdateText.Title(copying));
        var checking = snapshot with { Phase = UpdatePhase.Verifying };
        Assert.Equal("Checking the copy of BetterClipboard 0.2.10-dev.15", UpdateText.ButtonTip(checking));
        Assert.Equal("Checking the copy…", UpdateText.Title(checking));
        Assert.Equal("Comparing the copy with the build's SHA-256 checksum.", UpdateText.Detail(checking, Now, TimeZoneInfo.Utc, Culture));
    }

    /// <summary>The latest release offered to a newer development build is "the way back": install, not update.</summary>
    [Fact]
    public void OlderRelease_IsTheWayBack()
    {
        var snapshot = Offering("0.2.10-dev.12", UpdateTestData.Release("0.2.9"));
        Assert.True(snapshot.OffersOlderVersion);
        Assert.False(snapshot.IsLocalOffer);
        Assert.Equal("Back to the release: BetterClipboard 0.2.9", UpdateText.ButtonTip(snapshot));
        Assert.Equal("BetterClipboard 0.2.9 is the latest release", UpdateText.Title(snapshot));
        Assert.Equal(
            "You have 0.2.10-dev.12, a development build that is newer than this release. Installing it replaces the development build with the official release from GitHub and restarts BetterClipboard. Your history is kept.",
            UpdateText.Detail(snapshot, Now, TimeZoneInfo.Utc, Culture));
        Assert.Equal("Install and restart", UpdateText.ApproveButton(snapshot));
        Assert.Equal("Install 0.2.9…", UpdateText.OfferAction(snapshot));
        Assert.StartsWith("BetterClipboard 0.2.9 is the latest release. You have 0.2.10-dev.12,", UpdateText.SettingsStatus(snapshot, Now, TimeZoneInfo.Utc, Culture), StringComparison.Ordinal);

        // A copy that does not install by itself keeps its own words.
        Assert.Contains("does not replace itself", UpdateText.Detail(Offering("0.2.10-dev.12", UpdateTestData.Release("0.2.9"), UpdateInstallKind.Portable), Now, TimeZoneInfo.Utc, Culture), StringComparison.Ordinal);

        // The release of the development build's own version is an ordinary update.
        var newer = Offering("0.2.10-dev.12", UpdateTestData.Release("0.2.10"));
        Assert.False(newer.OffersOlderVersion);
        Assert.Equal("Update available: BetterClipboard 0.2.10", UpdateText.ButtonTip(newer));
        Assert.Equal("Update and restart", UpdateText.ApproveButton(newer));
        Assert.Throws<ArgumentException>(() => UpdateText.OfferAction(newer with { Offer = null }));
    }

    /// <summary>
    /// A development build is never reminded: its button is lit on purpose (the way back), and its user publishes its
    /// builds. A release build still is.
    /// </summary>
    [Fact]
    public void Reminder_NeverForADevelopmentBuild()
    {
        Assert.False(UpdateReminder.IsWanted(Offering("0.2.10-dev.12", UpdateTestData.Release("0.2.9"))));
        Assert.False(UpdateReminder.IsWanted(Offering("0.2.10-dev.12", Local("0.2.10-dev.15"))));
        Assert.False(UpdateReminder.IsDue(Offering("0.2.10-dev.12", UpdateTestData.Release("0.2.10")), null, Now));
        Assert.True(UpdateReminder.IsWanted(Offering("0.2.9", Local("0.2.10-dev.15"))));
        Assert.True(UpdateReminder.IsWanted(Offering("0.2.9", UpdateTestData.Release("0.2.10"))));
    }

    /// <summary>The new words are in both catalogs (the Chinese one translates every English entry).</summary>
    [Fact]
    public void NewWords_AreTranslated()
    {
        string previous = Localization.UiText.Language;
        try
        {
            Localization.UiText.Language = "zh-Hans";
            var snapshot = Offering("0.2.10-dev.12", Local("0.2.10-dev.15"));
            Assert.Equal("开发版本可用：BetterClipboard 0.2.10-dev.15", UpdateText.ButtonTip(snapshot));
            Assert.Equal("安装并重启", UpdateText.ApproveButton(Offering("0.2.10-dev.12", UpdateTestData.Release("0.2.9"))));
        }
        finally
        {
            Localization.UiText.Language = previous;
        }
    }
}
