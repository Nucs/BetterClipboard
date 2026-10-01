using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BetterClipboard.Core.Presentation;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for Settings › Third party (<see cref="ThirdPartyCatalog"/>): the link wording, the official-link rule, and
/// that the catalog stays complete — every package the app ships is credited, and every component agrees with its row
/// in <c>THIRD-PARTY-NOTICES.md</c>, in both directions.
/// </summary>
public sealed class ThirdPartyCatalogTests
{
    /// <summary>
    /// Packages that never reach the user, so the catalog leaves them out on purpose: the SDK build tools (MSIX
    /// tooling, resource compilers) run at build time only, and xUnit runs these tests. Anything else that restores
    /// has to be credited.
    /// </summary>
    private static readonly HashSet<string> BuildOnlyPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.Windows.SDK.BuildTools",
        "Microsoft.Windows.SDK.BuildTools.MSIX",
        "xunit.v3",
    };

    /// <summary>Hosts that only redirect: a link through one can be repointed later, so it is no official address.</summary>
    private static readonly string[] Redirectors = ["aka.ms", "go.microsoft.com", "bit.ly", "tinyurl.com", "t.co"];

    /// <summary>Every link the section shows (each entry's, plus the notices link), one theory case per link.</summary>
    public static TheoryData<string> AllLinks
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var link in ThirdPartyCatalog.All.Select(project => project.Link).Append(ThirdPartyCatalog.NoticesLink))
            {
                data.Add(link.OriginalString);
            }

            return data;
        }
    }

    /// <summary>
    /// The button shows the host without "www.", plus "owner/repository" on a code host, where the host alone names no
    /// project; the rest of the path, a trailing slash and the host's letter case never show.
    /// </summary>
    /// <param name="link">The link.</param>
    /// <param name="expected">The expected button text.</param>
    [Theory]
    [InlineData("https://getsharex.com/", "getsharex.com")]
    [InlineData("https://www.voidtools.com/", "voidtools.com")]
    [InlineData("https://learn.microsoft.com/microsoft-edge/webview2/", "learn.microsoft.com")]
    [InlineData("https://github.com/microsoft/CsWinRT", "github.com/microsoft/CsWinRT")]
    [InlineData("https://GitHub.com/ericsink/SQLitePCL.raw/tree/main/src", "github.com/ericsink/SQLitePCL.raw")]
    [InlineData("https://www.github.com/CommunityToolkit/dotnet/", "github.com/CommunityToolkit/dotnet")]
    [InlineData("https://gitlab.com/some%20group/project", "gitlab.com/some group/project")]
    [InlineData("https://github.com/", "github.com")]
    public void LinkText_IsTheHost_PlusTheRepositoryOnACodeHost(string link, string expected) =>
        Assert.Equal(expected, ThirdPartyCatalog.LinkText(new Uri(link)));

    /// <summary>A relative link has no host to show, and a missing one is a caller's bug: both throw.</summary>
    [Fact]
    public void LinkText_RefusesRelativeAndMissingLinks()
    {
        Assert.Throws<ArgumentException>(() => ThirdPartyCatalog.LinkText(new Uri("docs/notes.md", UriKind.Relative)));
        Assert.Throws<ArgumentNullException>(() => ThirdPartyCatalog.LinkText(null!));
    }

    /// <summary>
    /// The credit names the maker and, for a component, the license BetterClipboard ships it under; for an
    /// integration it says the app is not part of BetterClipboard.
    /// </summary>
    [Fact]
    public void Credit_NamesTheMakerAndTheTerms()
    {
        Assert.Equal("Ulrich Telle · MIT", Find("SQLite3 Multiple Ciphers").Credit);
        Assert.Equal("Microsoft · Windows App SDK license terms", Find("Windows App SDK").Credit);
        Assert.Equal("SQLite developers · Public domain", Find("SQLite").Credit);
        Assert.Equal("voidtools · separate app", Find("Everything").Credit);
        Assert.Equal("Microsoft · separate app", Find("PowerShell").Credit);
        Assert.Equal("ShareX team · separate app", Find("ShareX").Credit);
    }

    /// <summary>
    /// Every link is an official address as <see cref="ThirdPartyCatalog"/> defines it: https on the default port, no
    /// credentials, query or fragment (no tracking), no redirector host, no language segment.
    /// </summary>
    /// <param name="link">The link, as written in the catalog.</param>
    [Theory]
    [MemberData(nameof(AllLinks))]
    public void Links_FollowTheOfficialLinkRule(string link)
    {
        var uri = new Uri(link, UriKind.Absolute);
        Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
        Assert.True(uri.IsDefaultPort, "The default https port");
        Assert.Empty(uri.UserInfo);
        Assert.Empty(uri.Query);
        Assert.Empty(uri.Fragment);
        Assert.DoesNotContain(uri.Host, Redirectors, StringComparer.OrdinalIgnoreCase);

        // "/en-us/…" would pin every viewer to one language; without it Microsoft's sites pick the viewer's own.
        Assert.DoesNotMatch("^/[a-z]{2}-[a-z]{2}(/|$)", uri.AbsolutePath.ToLowerInvariant());
    }

    /// <summary>
    /// Every entry says who makes it and what it is for; components name a license, integrations neither a license
    /// nor packages (they are not redistributed); names are unique, integrations come first, and each role stays in
    /// alphabetical order.
    /// </summary>
    [Fact]
    public void Entries_AreComplete_AndInOrder()
    {
        var all = ThirdPartyCatalog.All;
        Assert.Equal(all.Count, all.Select(project => project.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(all, ThirdPartyCatalog.Integrations.Concat(ThirdPartyCatalog.Components));
        Assert.NotEmpty(ThirdPartyCatalog.Integrations);
        Assert.NotEmpty(ThirdPartyCatalog.Components);
        foreach (var project in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(project.Name));
            Assert.False(string.IsNullOrWhiteSpace(project.Maker), project.Name);
            Assert.EndsWith(".", project.Use, StringComparison.Ordinal);
            if (project.Role == ThirdPartyRole.Component)
            {
                Assert.False(string.IsNullOrWhiteSpace(project.License), project.Name);
            }
            else
            {
                Assert.Null(project.License);
                Assert.Empty(project.Packages);
            }
        }

        foreach (var role in new[] { ThirdPartyCatalog.Integrations, ThirdPartyCatalog.Components })
        {
            var names = role.Select(project => project.Name).ToArray();
            Assert.Equal(names.Order(StringComparer.OrdinalIgnoreCase), names);
        }
    }

    /// <summary>
    /// Every package in <c>Directory.Packages.props</c> is credited by a component unless it is build- or test-only,
    /// so adding a package without crediting it fails here.
    /// </summary>
    [Fact]
    public void EveryCentralPackage_IsCredited()
    {
        var props = XDocument.Load(Path.Combine(RepoRoot(), "Directory.Packages.props"));
        var ids = props.Descendants("PackageVersion").Select(element => (string?)element.Attribute("Include")).OfType<string>().ToArray();
        Assert.NotEmpty(ids);
        var credited = CreditedPackages();
        Assert.All(ids.Where(id => !BuildOnlyPackages.Contains(id)), id => Assert.Contains(id, credited));
    }

    /// <summary>
    /// Every package the app and <c>bclip</c> restore — transitive ones included, which is how WebView2 arrives with
    /// WinUI — is credited unless it is build-only. Read from the projects' restore output, so this needs a restored
    /// solution (any build does it); skipped on a fresh clone.
    /// </summary>
    [Fact]
    public void EveryPackageTheAppRestores_IsCredited()
    {
        string[] assetFiles =
        [
            Path.Combine(RepoRoot(), "src", "BetterClipboard.App", "obj", "project.assets.json"),
            Path.Combine(RepoRoot(), "src", "BetterClipboard.Cli", "obj", "project.assets.json"),
        ];
        var restored = assetFiles.Where(File.Exists).ToArray();
        Assert.SkipWhen(restored.Length == 0, "Restore the solution first (dotnet build BetterClipboard.sln); the transitive packages are read from the projects' obj folders.");

        var credited = CreditedPackages();
        foreach (var file in restored)
        {
            using var assets = JsonDocument.Parse(File.ReadAllBytes(file));

            // "libraries" holds every resolved package as "Id/Version"; project references are listed there too.
            var packages = assets.RootElement.GetProperty("libraries").EnumerateObject()
                .Where(library => library.Value.GetProperty("type").GetString() == "package")
                .Select(library => library.Name[..library.Name.IndexOf('/', StringComparison.Ordinal)])
                .ToArray();
            Assert.NotEmpty(packages);
            Assert.All(packages.Where(id => !BuildOnlyPackages.Contains(id)), id => Assert.Contains(id, credited));
        }
    }

    /// <summary>
    /// Every component has exactly one row in <c>THIRD-PARTY-NOTICES.md</c> whose license cell reads the same, every
    /// row there is a component of the catalog, and integrations — which are not redistributed — have no row.
    /// </summary>
    [Fact]
    public void Components_AndTheNoticesRows_Agree()
    {
        var rows = File.ReadAllLines(Path.Combine(RepoRoot(), "THIRD-PARTY-NOTICES.md"))
            .Where(line => line.StartsWith("| [", StringComparison.Ordinal))
            .ToArray();
        foreach (var component in ThirdPartyCatalog.Components)
        {
            var row = Assert.Single(rows, line => line.StartsWith($"| [{component.Name}](", StringComparison.Ordinal));
            Assert.EndsWith($"| {component.License} |", row, StringComparison.Ordinal);
        }

        var componentNames = ThirdPartyCatalog.Components.Select(component => component.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var name = Regex.Match(row, @"^\| \[(?<name>[^\]]+)\]\(").Groups["name"].Value;
            Assert.Contains(name, componentNames);
        }

        foreach (var integration in ThirdPartyCatalog.Integrations)
        {
            Assert.DoesNotContain(rows, line => line.StartsWith($"| [{integration.Name}](", StringComparison.Ordinal));
        }
    }

    /// <summary>The "License notices" link leads to the notices file this repository really has.</summary>
    [Fact]
    public void NoticesLink_PointsAtTheNoticesFile()
    {
        Assert.EndsWith("/THIRD-PARTY-NOTICES.md", ThirdPartyCatalog.NoticesLink.AbsolutePath, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "THIRD-PARTY-NOTICES.md")));
    }

    /// <summary>Looks an entry up by name.</summary>
    /// <param name="name">The entry's name.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="InvalidOperationException">No entry has that name.</exception>
    private static ThirdPartyProject Find(string name) => ThirdPartyCatalog.All.First(project => project.Name == name);

    /// <summary>Every package id some component credits.</summary>
    /// <returns>The ids, compared ignoring case like NuGet does.</returns>
    private static HashSet<string> CreditedPackages() =>
        ThirdPartyCatalog.Components.SelectMany(component => component.Packages).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The repository root: the nearest folder above the test binaries that holds <c>BetterClipboard.sln</c>. A method
    /// rather than a static field, so a missing root fails only the tests that read files, not the whole class.
    /// </summary>
    /// <returns>The root folder.</returns>
    /// <exception cref="InvalidOperationException">The tests do not run from inside the repository.</exception>
    private static string RepoRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "BetterClipboard.sln")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException($"BetterClipboard.sln was not found above {AppContext.BaseDirectory}.");
    }
}
