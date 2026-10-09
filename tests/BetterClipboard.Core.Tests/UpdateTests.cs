using System.Globalization;
using System.Text;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Updates;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Builders for release lists shared by the update tests: releases in GitHub's JSON shape, and the parsed values.
/// </summary>
internal static class UpdateTestData
{
    /// <summary>A SHA-256 in hex made of one repeated digit, for assets whose content does not matter.</summary>
    /// <param name="digit">The hex digit.</param>
    /// <returns>64 hex digits.</returns>
    public static string Hash(char digit) => new(digit, 64);

    /// <summary>Parses a version that the test knows to be valid.</summary>
    /// <param name="text">The version text.</param>
    /// <returns>The version.</returns>
    /// <exception cref="ArgumentException"><paramref name="text"/> is not a version (a mistake in the test itself).</exception>
    public static SemanticVersion Version(string text) =>
        SemanticVersion.TryParse(text, out var version) ? version : throw new ArgumentException($"'{text}' is not a version.", nameof(text));

    /// <summary>
    /// Builds a release with both architectures' packages and the checksum list, as <c>release.yml</c> publishes one.
    /// </summary>
    /// <param name="version">The release's version (its tag is <c>v</c> + this).</param>
    /// <param name="notes">Its release notes.</param>
    /// <param name="prerelease">Whether GitHub marks it a pre-release.</param>
    /// <param name="architectures">The architectures it has packages for (default: both).</param>
    /// <param name="checksums">Whether it carries <c>SHA256SUMS.txt</c>.</param>
    /// <param name="host">The download host (default: this project's GitHub release downloads).</param>
    /// <returns>The release.</returns>
    public static ReleaseInfo Release(string version, string notes = "", bool prerelease = false, string[]? architectures = null, bool checksums = true, string? host = null)
    {
        var parsed = Version(version);
        host ??= $"https://github.com/{UpdateSource.Repository}/releases/download/v{version}";
        var assets = new List<ReleaseAsset>();
        foreach (var arch in architectures ?? ["x64", "arm64"])
        {
            var name = UpdateCatalog.PackageFileName(parsed, arch);
            assets.Add(new ReleaseAsset(name, 1000, new Uri($"{host}/{name}"), Hash(arch == "x64" ? 'a' : 'b')));
        }

        if (checksums)
        {
            assets.Add(new ReleaseAsset(UpdateCatalog.ChecksumsFileName, 200, new Uri($"{host}/{UpdateCatalog.ChecksumsFileName}"), null));
        }

        return new ReleaseInfo(parsed, "v" + version, "BetterClipboard " + version, notes, new Uri($"https://github.com/{UpdateSource.Repository}/releases/tag/v{version}"),
            new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), prerelease || parsed.IsPrerelease, assets);
    }

    /// <summary>
    /// Writes releases as the JSON GitHub's "list releases" API returns (the members the app reads, plus a few it ignores).
    /// </summary>
    /// <param name="releases">The releases, in the order to list them.</param>
    /// <returns>The JSON text.</returns>
    public static string ToJson(params ReleaseInfo[] releases)
    {
        var json = new StringBuilder("[");
        for (int i = 0; i < releases.Length; i++)
        {
            var release = releases[i];
            json.Append(i == 0 ? string.Empty : ",");
            json.Append("{\"id\":").Append(1000 + i);
            json.Append(",\"tag_name\":").Append(Quote(release.Tag));
            json.Append(",\"name\":").Append(Quote(release.Title));
            json.Append(",\"draft\":false,\"prerelease\":").Append(release.IsPrerelease ? "true" : "false");
            json.Append(",\"html_url\":").Append(Quote(release.PageUrl?.AbsoluteUri ?? string.Empty));
            json.Append(",\"published_at\":").Append(Quote(release.PublishedAt?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) ?? string.Empty));
            json.Append(",\"body\":").Append(Quote(release.Notes));
            json.Append(",\"assets\":[");
            for (int a = 0; a < release.Assets.Count; a++)
            {
                var asset = release.Assets[a];
                json.Append(a == 0 ? string.Empty : ",");
                json.Append("{\"name\":").Append(Quote(asset.Name));
                json.Append(",\"size\":").Append(asset.Size.ToString(CultureInfo.InvariantCulture));
                json.Append(",\"download_count\":3,\"browser_download_url\":").Append(Quote(asset.DownloadUrl.AbsoluteUri));
                json.Append(",\"digest\":").Append(asset.Sha256 is null ? "null" : Quote("sha256:" + asset.Sha256));
                json.Append('}');
            }

            json.Append("]}");
        }

        return json.Append(']').ToString();
    }

    /// <summary>Quotes a string for JSON.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The JSON string literal.</returns>
    private static string Quote(string text) => System.Text.Json.JsonSerializer.Serialize(text);
}

/// <summary>
/// Tests for <see cref="SemanticVersion"/>: which texts are versions, and the order that decides whether a release is newer.
/// </summary>
public sealed class SemanticVersionTests
{
    /// <summary>Tags, plain versions, pre-releases and build metadata all parse, and print without the "v" and the metadata.</summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="expected">The version as it must print.</param>
    [Theory]
    [InlineData("0.2.6", "0.2.6")]
    [InlineData("v0.2.6", "0.2.6")]
    [InlineData("V1.20.300", "1.20.300")]
    [InlineData(" 0.2.6 ", "0.2.6")]
    [InlineData("0.2.6-dev.52539eb", "0.2.6-dev.52539eb")]
    [InlineData("0.2.6-dev.52539eb+52539eb1c0ffee", "0.2.6-dev.52539eb")]
    [InlineData("0.2.5+64cbff6", "0.2.5")]
    [InlineData("1.0.0-rc.01", "1.0.0-rc.1")]
    [InlineData("1.0.0-x-y.z--", "1.0.0-x-y.z--")]
    public void TryParse_AcceptsVersions(string text, string expected)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version));
        Assert.Equal(expected, version.ToString());
    }

    /// <summary>Anything that is not exactly three numbers with well-formed parts is refused (the text comes from the network).</summary>
    /// <param name="text">The text that must not parse.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("0.2.5.20261003")]
    [InlineData("1.2.x")]
    [InlineData("1..3")]
    [InlineData("-1.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-rc..1")]
    [InlineData("1.2.3-rc_1")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3+a b")]
    [InlineData("1.2.3 beta")]
    [InlineData("1.2.12345678901")]
    [InlineData("latest")]
    [InlineData("１.２.３")]
    public void TryParse_RefusesEverythingElse(string? text)
    {
        Assert.False(SemanticVersion.TryParse(text, out var version));
        Assert.Equal(default, version);
    }

    /// <summary>A text longer than any version is refused without being scanned.</summary>
    [Fact]
    public void TryParse_RefusesAVeryLongText()
    {
        Assert.False(SemanticVersion.TryParse("1.2.3-" + new string('a', 500), out _));
    }

    /// <summary>The order of semver.org's own example list, plus the cases the updater depends on.</summary>
    [Fact]
    public void CompareTo_FollowsSemVerPrecedence()
    {
        string[] ascending =
        [
            "0.2.5", "0.2.6-dev.52539eb", "0.2.6-rc.1", "0.2.6", "0.2.7", "0.3.0", "0.10.0",
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0",
            "1.0.0-0.3.7".Replace("1.0.0", "1.0.1", StringComparison.Ordinal), "1.0.1", "2.0.0", "10.0.0",
        ];
        var versions = ascending.Select(UpdateTestData.Version).ToArray();
        for (int i = 0; i < versions.Length; i++)
        {
            for (int j = 0; j < versions.Length; j++)
            {
                Assert.Equal(Math.Sign(i.CompareTo(j)), Math.Sign(versions[i].CompareTo(versions[j])));
                Assert.Equal(i < j, versions[i] < versions[j]);
                Assert.Equal(i >= j, versions[i] >= versions[j]);
            }
        }
    }

    /// <summary>A build installed before its release (a "dev" pre-release) is older than that release: the release is offered to it.</summary>
    [Fact]
    public void ADevelopmentBuild_IsOlderThanItsRelease_AndNewerThanTheReleaseBefore()
    {
        var dev = UpdateTestData.Version("0.2.6-dev.52539eb");
        Assert.True(dev < UpdateTestData.Version("0.2.6"));
        Assert.True(dev > UpdateTestData.Version("0.2.5"));
        Assert.True(dev.IsPrerelease);
        Assert.False(UpdateTestData.Version("0.2.6").IsPrerelease);
    }

    /// <summary>Numeric identifiers compare as numbers of any length (no overflow), and equal versions are equal values.</summary>
    [Fact]
    public void NumericIdentifiers_CompareAsNumbers_AndEqualityAgreesWithOrder()
    {
        Assert.True(UpdateTestData.Version("1.0.0-9") < UpdateTestData.Version("1.0.0-10"));
        Assert.True(UpdateTestData.Version("1.0.0-99999999999999999999") < UpdateTestData.Version("1.0.0-100000000000000000000"));
        Assert.True(UpdateTestData.Version("1.0.0-1") < UpdateTestData.Version("1.0.0-a"));
        Assert.Equal(UpdateTestData.Version("1.0.0-rc.01"), UpdateTestData.Version("v1.0.0-rc.1+build"));
        Assert.Equal(UpdateTestData.Version("1.0.0-rc.01").GetHashCode(), UpdateTestData.Version("1.0.0-rc.1").GetHashCode());
        Assert.True(UpdateTestData.Version("1.0.0") == UpdateTestData.Version("1.0.0+other"));
        Assert.True(UpdateTestData.Version("1.0.0") != UpdateTestData.Version("1.0.1"));
        Assert.NotEqual(UpdateTestData.Version("1.0.0-RC.1"), UpdateTestData.Version("1.0.0-rc.1"));
    }
}

/// <summary>
/// Tests for <see cref="GitHubReleases"/>: reading GitHub's release list, and refusing or skipping what is not one.
/// </summary>
public sealed class GitHubReleasesTests
{
    /// <summary>A list in the API's shape yields every release with its notes, page, date and files.</summary>
    [Fact]
    public void Parse_ReadsReleasesAndTheirFiles()
    {
        var json = UpdateTestData.ToJson(UpdateTestData.Release("0.2.6", "**New**\n- one\n- two"), UpdateTestData.Release("0.2.5", "older"));
        var releases = GitHubReleases.Parse(Encoding.UTF8.GetBytes(json));

        Assert.Equal(2, releases.Count);
        var first = releases[0];
        Assert.Equal("0.2.6", first.Version.ToString());
        Assert.Equal("v0.2.6", first.Tag);
        Assert.Equal("BetterClipboard 0.2.6", first.Title);
        Assert.Equal("**New**\n- one\n- two", first.Notes);
        Assert.Equal("https://github.com/Nucs/BetterClipboard/releases/tag/v0.2.6", first.PageUrl?.AbsoluteUri);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), first.PublishedAt);
        Assert.False(first.IsPrerelease);
        Assert.Equal(3, first.Assets.Count);

        var package = first.FindAsset("betterclipboard-0.2.6-WIN-x64.zip");
        Assert.NotNull(package);
        Assert.Equal(1000, package.Size);
        Assert.Equal(UpdateTestData.Hash('a'), package.Sha256);
        Assert.Equal("https://github.com/Nucs/BetterClipboard/releases/download/v0.2.6/BetterClipboard-0.2.6-win-x64.zip", package.DownloadUrl.AbsoluteUri);
        Assert.Null(first.FindAsset(UpdateCatalog.ChecksumsFileName)!.Sha256);
        Assert.Null(first.FindAsset("nothing.zip"));
    }

    /// <summary>A single release object (the "latest release" API) and a byte order mark are accepted.</summary>
    [Fact]
    public void Parse_AcceptsOneReleaseObject_AndAByteOrderMark()
    {
        var one = UpdateTestData.ToJson(UpdateTestData.Release("1.2.3")).Trim('[', ']');
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(one)).ToArray();
        var releases = GitHubReleases.Parse(bytes);
        Assert.Equal("1.2.3", Assert.Single(releases).Version.ToString());
    }

    /// <summary>Drafts and releases whose tag is not a version are left out; a pre-release is kept and marked, by flag or by version.</summary>
    [Fact]
    public void Parse_SkipsDraftsAndUnversionedTags_AndMarksPrereleases()
    {
        const string Json = """
            [
              {"tag_name":"v2.0.0","draft":true,"assets":[]},
              {"tag_name":"nightly","assets":[]},
              {"tag_name":"v1.1.0-rc.1","prerelease":false,"assets":[]},
              {"tag_name":"v1.0.5","prerelease":true,"assets":[]},
              {"tag_name":"v1.0.0"}
            ]
            """;
        var releases = GitHubReleases.Parse(Encoding.UTF8.GetBytes(Json));
        Assert.Equal(["1.1.0-rc.1", "1.0.5", "1.0.0"], releases.Select(r => r.Version.ToString()));
        Assert.Equal([true, true, false], releases.Select(r => r.IsPrerelease));
        Assert.Empty(releases[2].Assets);
        Assert.Equal(string.Empty, releases[2].Notes);
        Assert.Null(releases[2].PageUrl);
        Assert.Null(releases[2].PublishedAt);
    }

    /// <summary>
    /// Members of the wrong type or with unusable values drop only what they belong to: a file without a name or with an
    /// address that is not http(s), a digest of another algorithm, a size that is not a number.
    /// </summary>
    [Fact]
    public void Parse_SurvivesWrongTypesAndBadValues()
    {
        const string Json = """
            [
              42,
              "text",
              {"tag_name":7},
              {"tag_name":"v1.0.0","name":null,"body":17,"html_url":"javascript:alert(1)","published_at":"yesterday","prerelease":"yes",
               "assets":[
                 5,
                 {"name":"","browser_download_url":"https://example.org/a.zip"},
                 {"name":"no-url.zip"},
                 {"name":"ftp.zip","browser_download_url":"ftp://example.org/a.zip"},
                 {"name":"relative.zip","browser_download_url":"/a.zip"},
                 {"name":"md5.zip","size":"big","browser_download_url":"https://example.org/md5.zip","digest":"md5:0123"},
                 {"name":"short.zip","size":-5,"browser_download_url":"https://example.org/short.zip","digest":"sha256:abc"},
                 {"name":"good.zip","size":12,"browser_download_url":"https://example.org/good.zip","digest":"SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}
               ]}
            ]
            """;
        var release = Assert.Single(GitHubReleases.Parse(Encoding.UTF8.GetBytes(Json)));
        Assert.Equal(string.Empty, release.Title);
        Assert.Equal(string.Empty, release.Notes);
        Assert.Null(release.PageUrl);
        Assert.Null(release.PublishedAt);
        Assert.False(release.IsPrerelease);
        Assert.Equal(["md5.zip", "short.zip", "good.zip"], release.Assets.Select(a => a.Name));
        Assert.Equal([0L, 0L, 12L], release.Assets.Select(a => a.Size));
        Assert.Equal([null, null, UpdateTestData.Hash('a')], release.Assets.Select(a => a.Sha256));
    }

    /// <summary>Text that is not JSON, or JSON that is not a list or an object, is a format error for the caller to report.</summary>
    /// <param name="text">The answer's text.</param>
    [Theory]
    [InlineData("")]
    [InlineData("<html>rate limited</html>")]
    [InlineData("[{\"tag_name\":\"v1.0.0\"")]
    [InlineData("\"v1.0.0\"")]
    [InlineData("17")]
    public void Parse_ThrowsForWhatIsNoReleaseList(string text)
    {
        Assert.Throws<FormatException>(() => GitHubReleases.Parse(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Oversized input is bounded: notes are cut, and only the first releases and files are read.</summary>
    [Fact]
    public void Parse_BoundsNotesReleasesAndFiles()
    {
        var huge = UpdateTestData.Release("9.9.9", new string('n', GitHubReleases.MaxNotesLength + 500));
        Assert.Equal(GitHubReleases.MaxNotesLength, Assert.Single(GitHubReleases.Parse(Encoding.UTF8.GetBytes(UpdateTestData.ToJson(huge)))).Notes.Length);

        var many = Enumerable.Range(0, GitHubReleases.MaxReleases + 20).Select(i => UpdateTestData.Release($"1.0.{i}")).ToArray();
        Assert.Equal(GitHubReleases.MaxReleases, GitHubReleases.Parse(Encoding.UTF8.GetBytes(UpdateTestData.ToJson(many))).Count);

        var assets = string.Join(",", Enumerable.Range(0, GitHubReleases.MaxAssets + 20).Select(i => $"{{\"name\":\"f{i}.bin\",\"browser_download_url\":\"https://example.org/f{i}.bin\"}}"));
        var crowded = GitHubReleases.Parse(Encoding.UTF8.GetBytes($"[{{\"tag_name\":\"v1.0.0\",\"assets\":[{assets}]}}]"));
        Assert.Equal(GitHubReleases.MaxAssets, crowded[0].Assets.Count);
    }
}

/// <summary>
/// Tests for <see cref="UpdateCatalog"/>: which release is offered, and which release notes the dialog shows.
/// </summary>
public sealed class UpdateCatalogTests
{
    /// <summary>The package's name is the one the release script writes.</summary>
    [Fact]
    public void PackageFileName_MatchesTheReleaseScript()
    {
        Assert.Equal("BetterClipboard-0.2.6-win-x64.zip", UpdateCatalog.PackageFileName(UpdateTestData.Version("0.2.6"), "x64"));
        Assert.Equal("BetterClipboard-1.0.0-rc.1-win-arm64.zip", UpdateCatalog.PackageFileName(UpdateTestData.Version("v1.0.0-rc.1"), " ARM64 "));
        Assert.Throws<ArgumentException>(() => UpdateCatalog.PackageFileName(UpdateTestData.Version("1.0.0"), "x86"));
    }

    /// <summary>The newest stable release above the running version is offered, with this architecture's package.</summary>
    [Fact]
    public void FindUpdate_OffersTheNewestStableRelease()
    {
        ReleaseInfo[] releases = [UpdateTestData.Release("0.2.5"), UpdateTestData.Release("0.2.7"), UpdateTestData.Release("0.2.6"), UpdateTestData.Release("0.3.0-rc.1")];
        var offer = UpdateCatalog.FindUpdate(UpdateTestData.Version("0.2.5"), releases, "arm64");
        Assert.NotNull(offer);
        Assert.Equal("0.2.7", offer.Release.Version.ToString());
        Assert.Equal("BetterClipboard-0.2.7-win-arm64.zip", offer.Package.Name);
        Assert.Equal(UpdateCatalog.ChecksumsFileName, offer.Checksums.Name);
    }

    /// <summary>Nothing is offered to the newest version, to a newer development build, or from a list of pre-releases only.</summary>
    /// <param name="current">The running version.</param>
    [Theory]
    [InlineData("0.2.7")]
    [InlineData("0.2.8-dev.abc")]
    [InlineData("1.0.0")]
    public void FindUpdate_OffersNothingWhenUpToDate(string current)
    {
        ReleaseInfo[] releases = [UpdateTestData.Release("0.2.7"), UpdateTestData.Release("0.2.6"), UpdateTestData.Release("0.3.0-rc.1"), UpdateTestData.Release("0.9.0", prerelease: true)];
        Assert.Null(UpdateCatalog.FindUpdate(UpdateTestData.Version(current), releases, "x64"));
    }

    /// <summary>A development build of a version is offered that version's release.</summary>
    [Fact]
    public void FindUpdate_OffersTheReleaseToItsOwnDevelopmentBuild()
    {
        var offer = UpdateCatalog.FindUpdate(UpdateTestData.Version("0.2.6-dev.52539eb"), [UpdateTestData.Release("0.2.6"), UpdateTestData.Release("0.2.5")], "x64");
        Assert.Equal("0.2.6", offer?.Release.Version.ToString());
    }

    /// <summary>
    /// A release without this architecture's package or without its checksum list cannot be verified, so the next
    /// complete one is offered instead (or nothing).
    /// </summary>
    [Fact]
    public void FindUpdate_SkipsReleasesThatAreNotComplete()
    {
        ReleaseInfo[] releases =
        [
            UpdateTestData.Release("0.4.0", checksums: false),
            UpdateTestData.Release("0.3.0", architectures: ["arm64"]),
            UpdateTestData.Release("0.2.9"),
        ];
        Assert.Equal("0.2.9", UpdateCatalog.FindUpdate(UpdateTestData.Version("0.2.5"), releases, "x64")?.Release.Version.ToString());
        Assert.Equal("0.3.0", UpdateCatalog.FindUpdate(UpdateTestData.Version("0.2.5"), releases, "arm64")?.Release.Version.ToString());
        Assert.Null(UpdateCatalog.FindUpdate(UpdateTestData.Version("0.2.9"), releases, "x64"));
    }

    /// <summary>The newest stable release is found whatever the list's order, complete or not.</summary>
    [Fact]
    public void LatestStable_IgnoresPrereleasesAndOrder()
    {
        ReleaseInfo[] releases = [UpdateTestData.Release("0.2.5"), UpdateTestData.Release("1.0.0-rc.1"), UpdateTestData.Release("0.4.0", checksums: false), UpdateTestData.Release("0.2.9")];
        Assert.Equal("0.4.0", UpdateCatalog.LatestStable(releases)?.Version.ToString());
        Assert.Null(UpdateCatalog.LatestStable([UpdateTestData.Release("1.0.0-rc.1")]));
        Assert.Null(UpdateCatalog.LatestStable([]));
    }

    /// <summary>Behind by several releases: all their notes, newest first, without pre-releases.</summary>
    [Fact]
    public void SelectChangelog_ListsEveryNewerRelease()
    {
        ReleaseInfo[] releases = [UpdateTestData.Release("0.2.5"), UpdateTestData.Release("0.2.6"), UpdateTestData.Release("0.3.0-rc.1"), UpdateTestData.Release("0.2.7"), UpdateTestData.Release("0.2.3")];
        var changelog = UpdateCatalog.SelectChangelog(UpdateTestData.Version("0.2.5"), releases);
        Assert.Equal(ChangelogKind.NewerReleases, changelog.Kind);
        Assert.Equal(["0.2.7", "0.2.6"], changelog.Releases.Select(r => r.Version.ToString()));
        Assert.Equal(0, changelog.OlderReleasesLeftOut);
    }

    /// <summary>A copy very far behind gets the newest notes and a count of the rest.</summary>
    [Fact]
    public void SelectChangelog_CapsTheListAndCountsTheRest()
    {
        var releases = Enumerable.Range(1, UpdateCatalog.MaxChangelogReleases + 4).Select(i => UpdateTestData.Release($"1.0.{i}")).ToArray();
        var changelog = UpdateCatalog.SelectChangelog(UpdateTestData.Version("1.0.0"), releases);
        Assert.Equal(UpdateCatalog.MaxChangelogReleases, changelog.Releases.Count);
        Assert.Equal($"1.0.{UpdateCatalog.MaxChangelogReleases + 4}", changelog.Releases[0].Version.ToString());
        Assert.Equal(4, changelog.OlderReleasesLeftOut);
    }

    /// <summary>Up to date: the running version's own notes; a development build or an unlisted version: the latest release's.</summary>
    [Fact]
    public void SelectChangelog_ShowsTheCurrentOrTheLatestRelease()
    {
        ReleaseInfo[] releases = [UpdateTestData.Release("0.2.6"), UpdateTestData.Release("0.2.5"), UpdateTestData.Release("0.3.0-rc.1")];

        var own = UpdateCatalog.SelectChangelog(UpdateTestData.Version("0.2.6"), releases);
        Assert.Equal(ChangelogKind.CurrentRelease, own.Kind);
        Assert.Equal("0.2.6", Assert.Single(own.Releases).Version.ToString());

        var development = UpdateCatalog.SelectChangelog(UpdateTestData.Version("0.2.7-dev.1"), releases);
        Assert.Equal(ChangelogKind.LatestRelease, development.Kind);
        Assert.Equal("0.2.6", Assert.Single(development.Releases).Version.ToString());

        Assert.Same(Changelog.Empty, UpdateCatalog.SelectChangelog(UpdateTestData.Version("0.2.6"), []));
        Assert.Equal(ChangelogKind.None, UpdateCatalog.SelectChangelog(UpdateTestData.Version("0.2.6"), [UpdateTestData.Release("0.3.0-rc.1")]).Kind);
    }
}

/// <summary>
/// Tests for <see cref="Sha256Sums"/>: the checksum list is read exactly as <c>install.ps1</c> reads it.
/// </summary>
public sealed class Sha256SumsTests
{
    /// <summary>The release script's format: lower-case hash, two blanks, the name; one line per file, LF.</summary>
    [Fact]
    public void TryFind_ReadsTheReleaseScriptsFormat()
    {
        var text = $"{UpdateTestData.Hash('1')}  BetterClipboard-0.2.6-win-x64.zip\n{UpdateTestData.Hash('2')}  BetterClipboard-0.2.6-win-arm64.zip\n";
        Assert.True(Sha256Sums.TryFind(text, "BetterClipboard-0.2.6-win-arm64.zip", out var hash));
        Assert.Equal(UpdateTestData.Hash('2'), hash);
    }

    /// <summary>Other writers' variants are read too: upper case, CRLF, a tab, sha256sum's binary-mode star, indentation.</summary>
    [Fact]
    public void TryFind_AcceptsCommonVariants()
    {
        var upper = UpdateTestData.Hash('A');
        var text = $"# comment\r\n\r\n  {upper} *a.zip\r\n{UpdateTestData.Hash('b')}\tb.zip\r\n";
        Assert.True(Sha256Sums.TryFind(text, "a.zip", out var first));
        Assert.Equal(upper.ToLowerInvariant(), first);
        Assert.True(Sha256Sums.TryFind(text, "b.zip", out var second));
        Assert.Equal(UpdateTestData.Hash('b'), second);
    }

    /// <summary>No entry, a malformed line, another file's name, or two entries that disagree: nothing is vouched for.</summary>
    /// <param name="text">The checksum list's content.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a.zip")]
    [InlineData("zzzz  a.zip")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg  a.zip")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdefa.zip")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  A.ZIP")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  sub/a.zip")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  a.zip.sig")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  a.zip\nfedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210  a.zip")]
    public void TryFind_FindsNothingToTrust(string? text)
    {
        Assert.False(Sha256Sums.TryFind(text, "a.zip", out var hash));
        Assert.Equal(string.Empty, hash);
    }

    /// <summary>The same entry twice is one entry.</summary>
    [Fact]
    public void TryFind_AcceptsARepeatedIdenticalEntry()
    {
        var line = $"{UpdateTestData.Hash('c')}  a.zip\n";
        Assert.True(Sha256Sums.TryFind(line + line.ToUpperInvariant().Replace("A.ZIP", "a.zip", StringComparison.Ordinal), "a.zip", out var hash));
        Assert.Equal(UpdateTestData.Hash('c'), hash);
    }

    /// <summary>The file name must be given.</summary>
    [Fact]
    public void TryFind_NeedsAFileName()
    {
        Assert.Throws<ArgumentException>(() => Sha256Sums.TryFind("x", " ", out _));
    }
}

/// <summary>
/// Tests for <see cref="UpdateSource"/>: which download addresses the updater trusts.
/// </summary>
public sealed class UpdateSourceTests
{
    /// <summary>GitHub: only this repository's release downloads over https.</summary>
    /// <param name="url">A download address.</param>
    /// <param name="trusted">Whether it may be downloaded from.</param>
    [Theory]
    [InlineData("https://github.com/Nucs/BetterClipboard/releases/download/v0.2.6/BetterClipboard-0.2.6-win-x64.zip", true)]
    [InlineData("https://GITHUB.com/nucs/betterclipboard/releases/download/v0.2.6/SHA256SUMS.txt", true)]
    [InlineData("http://github.com/Nucs/BetterClipboard/releases/download/v0.2.6/a.zip", false)]
    [InlineData("https://github.com:8443/Nucs/BetterClipboard/releases/download/v0.2.6/a.zip", false)]
    [InlineData("https://github.com/Someone/BetterClipboard/releases/download/v0.2.6/a.zip", false)]
    [InlineData("https://github.com/Nucs/BetterClipboard-fork/releases/download/v0.2.6/a.zip", false)]
    [InlineData("https://github.com.evil.example/Nucs/BetterClipboard/releases/download/v0.2.6/a.zip", false)]
    [InlineData("https://evil.example/github.com/Nucs/BetterClipboard/releases/download/v0.2.6/a.zip", false)]
    [InlineData("https://user@github.com/Nucs/BetterClipboard/releases/download/v0.2.6/a.zip", false)]
    [InlineData("https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1", false)]
    [InlineData("https://github.com/Nucs/BetterClipboard/archive/refs/tags/v0.2.6.zip", false)]
    public void GitHub_TrustsOnlyItsOwnReleaseDownloads(string url, bool trusted)
    {
        Assert.Equal(trusted, UpdateSource.GitHub.IsTrustedDownload(new Uri(url)));
        Assert.False(UpdateSource.GitHub.IsOverride);
        Assert.Equal("api.github.com", UpdateSource.GitHub.FeedUrl.Host);
    }

    /// <summary>A stand-in feed is accepted over https anywhere, and over http only on the loopback host.</summary>
    /// <param name="feed">The override variable's value.</param>
    /// <param name="accepted">Whether it makes a source.</param>
    [Theory]
    [InlineData("http://127.0.0.1:8765/releases", true)]
    [InlineData("http://localhost:8765/releases", true)]
    [InlineData("http://[::1]:8765/releases", true)]
    [InlineData("https://updates.example.org/releases.json", true)]
    [InlineData("http://updates.example.org/releases.json", false)]
    [InlineData("ftp://127.0.0.1/releases", false)]
    [InlineData("releases.json", false)]
    [InlineData("  ", false)]
    [InlineData(null, false)]
    public void TryCreateOverride_AcceptsHttpsAndLoopbackHttp(string? feed, bool accepted)
    {
        var source = UpdateSource.TryCreateOverride(feed);
        Assert.Equal(accepted, source is not null);
        Assert.True(source?.IsOverride ?? true);
    }

    /// <summary>A stand-in feed's downloads must come from the feed's own scheme, host and port.</summary>
    [Fact]
    public void Override_TrustsOnlyItsOwnOrigin()
    {
        var source = UpdateSource.TryCreateOverride("http://127.0.0.1:8765/releases")!;
        Assert.True(source.IsTrustedDownload(new Uri("http://127.0.0.1:8765/files/a.zip")));
        Assert.False(source.IsTrustedDownload(new Uri("http://127.0.0.1:9999/files/a.zip")));
        Assert.False(source.IsTrustedDownload(new Uri("https://127.0.0.1:8765/files/a.zip")));
        Assert.False(source.IsTrustedDownload(new Uri("http://localhost:8765/files/a.zip")));
        Assert.False(source.IsTrustedDownload(new Uri("https://github.com/Nucs/BetterClipboard/releases/download/v1/a.zip")));
    }
}

/// <summary>
/// Tests for <see cref="InstalledCopy"/> and <see cref="UpdateResult"/>: telling a copy the installer owns from one
/// extracted by hand, and reading what the installer left behind.
/// </summary>
public sealed class InstalledCopyTests
{
    /// <summary>Either of the installer's records naming the running folder makes it an installer copy; neither: portable.</summary>
    /// <param name="appDirectory">The folder the app runs from.</param>
    /// <param name="registered">The Installed-apps entry's install location.</param>
    /// <param name="state">installer.json's install folder.</param>
    /// <param name="expected">The kind of copy.</param>
    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\BetterClipboard", @"C:\Users\me\AppData\Local\Programs\BetterClipboard", null, UpdateInstallKind.Installer)]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\BetterClipboard", @"c:\users\ME\appdata\local\programs\betterclipboard\", null, UpdateInstallKind.Installer)]
    [InlineData(@"C:\Apps\BC", null, @"C:\Apps\BC", UpdateInstallKind.Installer)]
    [InlineData(@"C:\Apps\BC\", @"D:\Elsewhere", @"C:\Apps\BC", UpdateInstallKind.Installer)]
    [InlineData(@"C:\Tools", @"C:\Users\me\AppData\Local\Programs\BetterClipboard", @"C:\Users\me\AppData\Local\Programs\BetterClipboard", UpdateInstallKind.Portable)]
    [InlineData(@"C:\Tools", null, null, UpdateInstallKind.Portable)]
    [InlineData(@"C:\Tools", "", "  ", UpdateInstallKind.Portable)]
    [InlineData(@"C:\Tools", @"C:\Tools\Sub", @"C:\", UpdateInstallKind.Portable)]
    [InlineData(@"C:\ProgramData\chocolatey\lib\betterclipboard\tools\app", @"C:\ProgramData\chocolatey\lib\betterclipboard\tools\app", null, UpdateInstallKind.Chocolatey)]
    [InlineData(@"D:\choco\LIB\BetterClipboard\Tools\App\", null, null, UpdateInstallKind.Chocolatey)]
    [InlineData(@"C:\lib\betterclipboard\tools\app\sub", null, null, UpdateInstallKind.Portable)]
    public void Classify_ReadsTheInstallersRecords(string appDirectory, string? registered, string? state, UpdateInstallKind expected)
    {
        Assert.Equal(expected, InstalledCopy.Classify(appDirectory, registered, state));
    }

    /// <summary>A record that is not a path at all names no folder (and never throws).</summary>
    [Fact]
    public void SameFolder_ToleratesGarbage()
    {
        Assert.False(InstalledCopy.SameFolder(@"C:\Apps\BC", "\0bad\0"));
        Assert.False(InstalledCopy.SameFolder(@"C:\Apps\BC", null));
        Assert.True(InstalledCopy.SameFolder(@"C:\Apps\BC", @"C:\Apps\Other\..\BC\"));
    }

    /// <summary>installer.json as Windows PowerShell 5.1 writes it (with a byte order mark) yields its installDir.</summary>
    [Fact]
    public void ReadInstallerStateDirectory_ReadsWhatTheInstallerWrote()
    {
        using var temp = TestData.NewTempDirectory();
        var path = Path.Combine(temp.Path, "installer.json");
        File.WriteAllText(path, "{\r\n  \"version\": \"0.2.6\",\r\n  \"installDir\": \"C:\\\\Apps\\\\BC\",\r\n  \"releasedKeys\": \"V\"\r\n}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal(@"C:\Apps\BC", InstalledCopy.ReadInstallerStateDirectory(path));
    }

    /// <summary>A missing, broken or unrelated file proves nothing.</summary>
    /// <param name="content">The file's content, or <see langword="null"/> for no file.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"installDir\": 5}")]
    [InlineData("{\"version\": \"1.0.0\"}")]
    public void ReadInstallerStateDirectory_IsNullForAnythingElse(string? content)
    {
        using var temp = TestData.NewTempDirectory();
        var path = Path.Combine(temp.Path, "installer.json");
        if (content is not null)
        {
            File.WriteAllText(path, content);
        }

        Assert.Null(InstalledCopy.ReadInstallerStateDirectory(path));
        Assert.Null(InstalledCopy.ReadInstallerStateDirectory(null));
        Assert.Null(InstalledCopy.ReadInstallerStateDirectory(temp.Path));
    }

    /// <summary>The installer's result file is read whether it reports success or a failure with its message.</summary>
    [Fact]
    public void UpdateResult_ReadsSuccessAndFailure()
    {
        using var temp = TestData.NewTempDirectory();
        var path = Path.Combine(temp.Path, "result.json");

        File.WriteAllText(path, "{\"version\":\"0.2.7\",\"ok\":true,\"error\":\"\",\"finishedUtc\":\"2026-10-09T10:11:12.0000000Z\"}", new UTF8Encoding(true));
        var ok = UpdateResult.TryRead(path);
        Assert.NotNull(ok);
        Assert.True(ok.Succeeded);
        Assert.Equal("0.2.7", ok.Version);
        Assert.Equal(string.Empty, ok.Error);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 10, 11, 12, TimeSpan.Zero), ok.FinishedAt);

        File.WriteAllText(path, "{\"version\":\"0.2.7\",\"ok\":false,\"error\":\"  The install folder stayed in use.  \"}");
        var failed = UpdateResult.TryRead(path);
        Assert.NotNull(failed);
        Assert.False(failed.Succeeded);
        Assert.Equal("The install folder stayed in use.", failed.Error);
        Assert.Null(failed.FinishedAt);
    }

    /// <summary>A result without a verdict, a broken one or none is no result; a very long error is cut.</summary>
    [Fact]
    public void UpdateResult_IsNullWithoutAVerdict_AndBoundsTheError()
    {
        using var temp = TestData.NewTempDirectory();
        var path = Path.Combine(temp.Path, "result.json");
        Assert.Null(UpdateResult.TryRead(path));
        Assert.Null(UpdateResult.TryRead(null));

        foreach (var content in new[] { "", "{", "[]", "{\"version\":\"1.0.0\"}", "{\"ok\":\"true\"}" })
        {
            File.WriteAllText(path, content);
            Assert.Null(UpdateResult.TryRead(path));
        }

        File.WriteAllText(path, "{\"ok\":false,\"error\":\"" + new string('e', 5000) + "\"}");
        Assert.Equal(2000, UpdateResult.TryRead(path)!.Error.Length);
    }
}

/// <summary>
/// Tests for the update settings and <see cref="UpdateSetupCommand"/> (<c>--set-update-check</c>, the installer's switch).
/// </summary>
public sealed class UpdateSettingsTests
{
    /// <summary>The check is on by default, nothing is skipped, and both survive a save and load.</summary>
    [Fact]
    public void Settings_DefaultsAndRoundTrip()
    {
        var defaults = new AppSettings().Normalize();
        Assert.True(defaults.CheckForUpdates);
        Assert.Equal(string.Empty, defaults.SkippedUpdateVersion);

        using var temp = TestData.NewTempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var store = new SettingsStore(path);
        store.Load();
        store.Update(s => s with { CheckForUpdates = false, SkippedUpdateVersion = "  0.2.7  " });

        var reloaded = new SettingsStore(path).Load();
        Assert.False(reloaded.CheckForUpdates);
        Assert.Equal("0.2.7", reloaded.SkippedUpdateVersion);
    }

    /// <summary>A settings file from before the updater keeps working: the check defaults to on.</summary>
    [Fact]
    public void Settings_FromBeforeTheUpdater_GetTheDefaults()
    {
        using var temp = TestData.NewTempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ \"OpenHotkey\": \"Win+V\", \"MaxItems\": 500 }");
        var loaded = new SettingsStore(path).Load();
        Assert.True(loaded.CheckForUpdates);
        Assert.Equal(string.Empty, loaded.SkippedUpdateVersion);
        Assert.Equal(500, loaded.MaxItems);
    }

    /// <summary>A hand-edited skipped version that is absurdly long or null is dropped.</summary>
    [Fact]
    public void Settings_NormalizeTheSkippedVersion()
    {
        Assert.Equal(string.Empty, (new AppSettings { SkippedUpdateVersion = new string('9', 200) }).Normalize().SkippedUpdateVersion);
        Assert.Equal(string.Empty, (new AppSettings { SkippedUpdateVersion = null! }).Normalize().SkippedUpdateVersion);
    }

    /// <summary>The panel's minimum width leaves room for the header's fifth button, and a narrower saved width is raised to it.</summary>
    [Fact]
    public void Settings_MinimumWidthHoldsFiveHeaderButtons()
    {
        Assert.Equal(396, AppSettings.MinFlyoutWidth);
        Assert.True(AppSettings.DefaultFlyoutWidth >= AppSettings.MinFlyoutWidth);
        Assert.Equal(AppSettings.MinFlyoutWidth, (new AppSettings { FlyoutWidth = 360 }).Normalize().FlyoutWidth);
    }

    /// <summary>The command switches the check off and on in the settings file and says what it did.</summary>
    [Fact]
    public void Command_SavesOffAndOn()
    {
        using var temp = TestData.NewTempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        new SettingsStore(path).Update(s => s with { MaxItems = 123 });

        var output = new StringWriter();
        Assert.Equal(UpdateSetupCommand.ExitOk, UpdateSetupCommand.Run(["--set-update-check", "OFF"], output, path, () => false));
        Assert.Equal("update-check: off", output.ToString().Trim());
        var saved = new SettingsStore(path).Load();
        Assert.False(saved.CheckForUpdates);
        Assert.Equal(123, saved.MaxItems);

        output = new StringWriter();
        Assert.Equal(UpdateSetupCommand.ExitOk, UpdateSetupCommand.Run(["--SET-UPDATE-CHECK", "on"], output, path, () => false));
        Assert.Equal("update-check: on", output.ToString().Trim());
        Assert.True(new SettingsStore(path).Load().CheckForUpdates);
    }

    /// <summary>Anything but "on" or "off" is refused with an explanation, and nothing is written.</summary>
    /// <param name="values">The arguments after the switch.</param>
    [Theory]
    [InlineData]
    [InlineData("maybe")]
    [InlineData("on", "off")]
    [InlineData("")]
    public void Command_RefusesOtherArguments(params string[] values)
    {
        using var temp = TestData.NewTempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var output = new StringWriter();
        string[] args = ["--set-update-check", .. values];
        Assert.Equal(UpdateSetupCommand.ExitInvalid, UpdateSetupCommand.Run(args, output, path, () => false));
        Assert.StartsWith("error:", output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    /// <summary>While the app runs with that data folder, nothing is saved: the running app would overwrite the file.</summary>
    [Fact]
    public void Command_RefusesWhileTheAppRuns()
    {
        using var temp = TestData.NewTempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var output = new StringWriter();
        Assert.Equal(UpdateSetupCommand.ExitAppRunning, UpdateSetupCommand.Run(["--set-update-check", "off"], output, path, () => true));
        Assert.StartsWith("error:", output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    /// <summary>The command is recognized only as the first argument.</summary>
    [Fact]
    public void Command_MatchesOnlyAsTheFirstArgument()
    {
        Assert.True(UpdateSetupCommand.Matches(["--set-update-check", "off"]));
        Assert.False(UpdateSetupCommand.Matches(["--background", "--set-update-check"]));
        Assert.False(UpdateSetupCommand.Matches([]));
        Assert.Throws<ArgumentException>(() => UpdateSetupCommand.Run(["--other"], new StringWriter(), "settings.json", () => false));
    }
}

/// <summary>
/// Tests for <see cref="UpdateText"/>: what the update button, the dialog and Settings say in each state.
/// </summary>
public sealed class UpdateTextTests
{
    /// <summary>A fixed "now" two days after the test releases' publication.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Invariant wording for numbers and dates.</summary>
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>A snapshot of a copy running 0.2.6.</summary>
    /// <param name="kind">How it was installed.</param>
    /// <returns>The snapshot, with automatic checks on.</returns>
    private static UpdateSnapshot Running(UpdateInstallKind kind = UpdateInstallKind.Installer) =>
        new() { CurrentVersion = UpdateTestData.Version("0.2.6"), InstallKind = kind, AutomaticChecks = true };

    /// <summary>The offer of 0.2.7 for x64.</summary>
    /// <returns>The offer.</returns>
    private static UpdateOffer Offer()
    {
        var release = UpdateTestData.Release("0.2.7");
        return new UpdateOffer(release, release.FindAsset("BetterClipboard-0.2.7-win-x64.zip")!, release.FindAsset(UpdateCatalog.ChecksumsFileName)!);
    }

    /// <summary>Before any check the button says only what it is; while checking it says so.</summary>
    [Fact]
    public void BeforeTheFirstCheck()
    {
        var fresh = Running();
        Assert.Equal("Updates", UpdateText.ButtonTip(fresh));
        Assert.Equal("Updates", UpdateText.Title(fresh));
        Assert.Equal("Version 0.2.6. BetterClipboard has not checked for updates yet.", UpdateText.Detail(fresh, Now, TimeZoneInfo.Utc, Culture));

        var checking = fresh with { Phase = UpdatePhase.Checking };
        Assert.Equal("Checking for updates", UpdateText.ButtonTip(checking));
        Assert.Equal("Checking for updates…", UpdateText.Title(checking));
        Assert.Contains("Asking GitHub", UpdateText.Detail(checking, Now, TimeZoneInfo.Utc, Culture), StringComparison.Ordinal);
    }

    /// <summary>Up to date: the version, and when that was last confirmed.</summary>
    [Fact]
    public void UpToDate()
    {
        var snapshot = Running() with { LastChecked = Now.AddMinutes(-5), Latest = UpdateTestData.Release("0.2.6") };
        Assert.Equal("BetterClipboard 0.2.6 is up to date", UpdateText.ButtonTip(snapshot));
        Assert.Equal("BetterClipboard is up to date", UpdateText.Title(snapshot));
        Assert.Equal("Version 0.2.6 is the latest release. Checked 5 min ago.", UpdateText.Detail(snapshot, Now, TimeZoneInfo.Utc, Culture));
    }

    /// <summary>A build newer than every release says so instead of calling itself "the latest release".</summary>
    [Fact]
    public void NewerThanTheLatestRelease()
    {
        var snapshot = new UpdateSnapshot
        {
            CurrentVersion = UpdateTestData.Version("0.2.7-dev.abc"),
            LastChecked = Now.AddHours(-2),
            Latest = UpdateTestData.Release("0.2.6"),
            AutomaticChecks = true,
        };
        Assert.Equal("Version 0.2.7-dev.abc, newer than the latest release (0.2.6). Checked 2 h ago.", UpdateText.Detail(snapshot, Now, TimeZoneInfo.Utc, Culture));
    }

    /// <summary>An offer names the version everywhere, and the detail says what approving does for this kind of copy.</summary>
    [Fact]
    public void UpdateAvailable_PerInstallKind()
    {
        var installer = Running() with { Offer = Offer(), LastChecked = Now };
        Assert.Equal("Update available: BetterClipboard 0.2.7", UpdateText.ButtonTip(installer));
        Assert.Equal("BetterClipboard 0.2.7 is available", UpdateText.Title(installer));
        var detail = UpdateText.Detail(installer, Now, TimeZoneInfo.Utc, Culture);
        Assert.StartsWith("You have 0.2.6. Released Thu 12:00.", detail, StringComparison.Ordinal);
        Assert.Contains("restarts BetterClipboard", detail, StringComparison.Ordinal);

        var chocolatey = UpdateText.Detail(Running(UpdateInstallKind.Chocolatey) with { Offer = Offer() }, Now, TimeZoneInfo.Utc, Culture);
        Assert.Contains("choco upgrade betterclipboard", chocolatey, StringComparison.Ordinal);

        var portable = UpdateText.Detail(Running(UpdateInstallKind.Portable) with { Offer = Offer() }, Now, TimeZoneInfo.Utc, Culture);
        Assert.Contains("does not replace itself", portable, StringComparison.Ordinal);

        Assert.Equal("BetterClipboard 0.2.7 is available (you skipped it)", UpdateText.ButtonTip(installer with { IsSkipped = true }));
    }

    /// <summary>Each step of an approved update has its own words, with the download's progress.</summary>
    [Fact]
    public void WhileInstalling()
    {
        var downloading = Running() with { Offer = Offer(), Phase = UpdatePhase.Downloading, DownloadedBytes = 24_536_678, TotalBytes = 74_280_969 };
        Assert.Equal("Downloading BetterClipboard 0.2.7: 33 %", UpdateText.ButtonTip(downloading));
        Assert.Equal("Downloading BetterClipboard 0.2.7…", UpdateText.Title(downloading));
        Assert.Equal("23.4 of 70.8 MB", UpdateText.Detail(downloading, Now, TimeZoneInfo.Utc, Culture));

        var verifying = downloading with { Phase = UpdatePhase.Verifying };
        Assert.Equal("Checking the download…", UpdateText.Title(verifying));
        Assert.Contains("SHA-256", UpdateText.Detail(verifying, Now, TimeZoneInfo.Utc, Culture), StringComparison.Ordinal);

        var installing = downloading with { Phase = UpdatePhase.Installing };
        Assert.Equal("Installing BetterClipboard 0.2.7", UpdateText.ButtonTip(installing));
        Assert.Contains("Your history is kept", UpdateText.Detail(installing, Now, TimeZoneInfo.Utc, Culture), StringComparison.Ordinal);
    }

    /// <summary>A failed first check has its own title; a copy that does not check by itself says why.</summary>
    [Fact]
    public void ProblemsAndSwitchedOffChecks()
    {
        var failed = Running() with { CheckProblem = new UpdateProblem(UpdateFailure.Network, "The server could not be reached (no such host).") };
        Assert.Equal("Could not check for updates", UpdateText.Title(failed));
        Assert.Contains("The last check failed: The server could not be reached (no such host).", UpdateText.SettingsStatus(failed, Now, TimeZoneInfo.Utc, Culture), StringComparison.Ordinal);

        var off = Running() with { AutomaticChecks = false };
        Assert.Contains("Automatic checks are off", UpdateText.Detail(off, Now, TimeZoneInfo.Utc, Culture), StringComparison.Ordinal);

        var chocolatey = Running(UpdateInstallKind.Chocolatey) with { AutomaticChecks = false };
        Assert.Contains("Chocolatey updates this copy", UpdateText.Detail(chocolatey, Now, TimeZoneInfo.Utc, Culture), StringComparison.Ordinal);

        var installFailed = Running() with { Offer = Offer(), InstallProblem = new UpdateProblem(UpdateFailure.Verification, "The download does not match.") };
        Assert.EndsWith("The download does not match.", UpdateText.SettingsStatus(installFailed, Now, TimeZoneInfo.Utc, Culture), StringComparison.Ordinal);
    }

    /// <summary>
    /// The Settings line reads as prose: the dialog's title becomes a sentence of its own (an ellipsis already ends one),
    /// and the bare "Updates" title, which says nothing, is left out.
    /// </summary>
    [Fact]
    public void SettingsStatus_ReadsAsSentences()
    {
        var upToDate = Running() with { LastChecked = Now.AddMinutes(-5), Latest = UpdateTestData.Release("0.2.6") };
        Assert.Equal(
            "BetterClipboard is up to date. Version 0.2.6 is the latest release. Checked 5 min ago.",
            UpdateText.SettingsStatus(upToDate, Now, TimeZoneInfo.Utc, Culture));

        Assert.Equal(
            "Version 0.2.6. BetterClipboard has not checked for updates yet.",
            UpdateText.SettingsStatus(Running(), Now, TimeZoneInfo.Utc, Culture));

        var checking = Running() with { Phase = UpdatePhase.Checking };
        Assert.StartsWith("Checking for updates… Version 0.2.6.", UpdateText.SettingsStatus(checking, Now, TimeZoneInfo.Utc, Culture), StringComparison.Ordinal);

        var offered = Running() with { Offer = Offer(), LastChecked = Now };
        Assert.StartsWith("BetterClipboard 0.2.7 is available. You have 0.2.6.", UpdateText.SettingsStatus(offered, Now, TimeZoneInfo.Utc, Culture), StringComparison.Ordinal);

        // A skipped version says why nothing is highlighted for it; once it is being installed, that no longer matters.
        var skipped = offered with { IsSkipped = true };
        Assert.Contains("\nYou skipped this version, so the update button is not highlighted for it.", UpdateText.SettingsStatus(skipped, Now, TimeZoneInfo.Utc, Culture), StringComparison.Ordinal);
        Assert.DoesNotContain("skipped", UpdateText.SettingsStatus(skipped with { Phase = UpdatePhase.Downloading }, Now, TimeZoneInfo.Utc, Culture), StringComparison.Ordinal);
        Assert.DoesNotContain("skipped", UpdateText.SettingsStatus(offered, Now, TimeZoneInfo.Utc, Culture), StringComparison.Ordinal);

        // A copy that does not check by itself says so once it has a result to show.
        var manual = upToDate with { AutomaticChecks = false };
        Assert.EndsWith("\nAutomatic checks are off: BetterClipboard asks GitHub only when you press Check now.", UpdateText.SettingsStatus(manual, Now, TimeZoneInfo.Utc, Culture), StringComparison.Ordinal);
    }

    /// <summary>The headings over the notes, and the progress wording without a known size.</summary>
    [Fact]
    public void HeadingsAndProgress()
    {
        Assert.Equal("What is new", UpdateText.ChangelogHeading(new Changelog(ChangelogKind.NewerReleases, [])));
        Assert.Equal("What is new in this version", UpdateText.ChangelogHeading(new Changelog(ChangelogKind.CurrentRelease, [])));
        Assert.Equal("The latest release", UpdateText.ChangelogHeading(new Changelog(ChangelogKind.LatestRelease, [])));
        Assert.Equal(string.Empty, UpdateText.ChangelogHeading(Changelog.Empty));

        Assert.Equal("0.2.7 · 1 Oct 2026", UpdateText.ReleaseHeading(UpdateTestData.Release("0.2.7"), TimeZoneInfo.Utc, Culture));
        Assert.Equal("0.2.7", UpdateText.ReleaseHeading(UpdateTestData.Release("0.2.7") with { PublishedAt = null }, TimeZoneInfo.Utc, Culture));

        Assert.Equal("1.5 MB", UpdateText.Progress(1_572_864, 0, Culture));
        Assert.Equal("0.0 of 1.0 MB", UpdateText.Progress(-5, 1_048_576, Culture));
    }
}

/// <summary>
/// Tests for <see cref="UpdateDialogLayout"/>: on which side of its button the update dialog opens, and how tall.
/// </summary>
public sealed class UpdateDialogLayoutTests
{
    /// <summary>
    /// Under the button when that side is comfortable or the larger one, otherwise over it; the height is the room on
    /// that side less the margin, never more than the dialog's full height and never less than its minimum.
    /// </summary>
    /// <param name="roomBelow">DIPs from the button's bottom to the bottom of the work area.</param>
    /// <param name="roomAbove">DIPs from the top of the work area to the button's top.</param>
    /// <param name="below">Whether the dialog opens under the button.</param>
    /// <param name="maxHeight">The height it may take there.</param>
    [Theory]
    [InlineData(700, 100, true, 600)]   // plenty of room below: the full height
    [InlineData(456, 800, true, 440)]   // just comfortable below: below wins although above has more
    [InlineData(455, 800, false, 600)]  // one DIP short of comfortable: the larger side, above
    [InlineData(100, 400, false, 384)]  // above, as tall as the room there
    [InlineData(300, 200, true, 300)]   // cramped on both sides: the larger one, at the minimum height
    [InlineData(516, 286, true, 500)]   // the panel in the middle of a 900-pixel screen (measured): below, shortened
    [InlineData(0, 0, true, 300)]
    [InlineData(-50, -20, true, 300)]   // a button outside the work area: no room counts as none, not as negative
    public void Choose_PrefersBelow_AndFitsTheRoom(double roomBelow, double roomAbove, bool below, double maxHeight)
    {
        var placement = UpdateDialogLayout.Choose(roomBelow, roomAbove);

        Assert.Equal(below, placement.Below);
        Assert.Equal(maxHeight, placement.MaxHeightDip);
    }

    /// <summary>Measurements that are not numbers (a window without a monitor yet) count as no room instead of poisoning the result.</summary>
    [Fact]
    public void Choose_ToleratesValuesThatAreNotNumbers()
    {
        Assert.Equal(new UpdateDialogPlacement(true, UpdateDialogLayout.MinimumHeightDip), UpdateDialogLayout.Choose(double.NaN, double.NaN));
        Assert.Equal(new UpdateDialogPlacement(false, 484), UpdateDialogLayout.Choose(double.NaN, 500));
        Assert.Equal(new UpdateDialogPlacement(true, UpdateDialogLayout.MinimumHeightDip), UpdateDialogLayout.Choose(double.PositiveInfinity, double.NegativeInfinity));
    }

    /// <summary>The limits keep their order, or the clamp in <see cref="UpdateDialogLayout.Choose"/> would throw.</summary>
    [Fact]
    public void Limits_AreOrdered()
    {
        Assert.True(UpdateDialogLayout.MinimumHeightDip < UpdateDialogLayout.ComfortableHeightDip);
        Assert.True(UpdateDialogLayout.ComfortableHeightDip < UpdateDialogLayout.PreferredHeightDip);
        Assert.True(UpdateDialogLayout.MarginDip > 0);
    }
}
