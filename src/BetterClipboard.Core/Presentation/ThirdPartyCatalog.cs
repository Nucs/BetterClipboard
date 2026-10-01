namespace BetterClipboard.Core.Presentation;

/// <summary>
/// What a third-party project is to BetterClipboard. It decides which Settings card lists the project and what its
/// credit line says: an integration is someone else's app that the user installs, a component ships inside
/// BetterClipboard's download under its own license.
/// </summary>
public enum ThirdPartyRole
{
    /// <summary>
    /// A separate app BetterClipboard reads from while its setting is on (ShareX, Everything, PowerShell, Claude Code, Codex). It is never
    /// shipped, so no license is credited, and its link is where to get the app from its makers rather than from a
    /// download mirror.
    /// </summary>
    Integration,

    /// <summary>
    /// A runtime or library inside BetterClipboard's download. It is redistributed under its own license, so it also
    /// needs a row in <c>THIRD-PARTY-NOTICES.md</c>; the tests fail while the two lists disagree.
    /// </summary>
    Component,
}

/// <summary>
/// One project in Settings › Third party: who makes it, what BetterClipboard uses it for, and its official link.
/// </summary>
/// <param name="Name">
/// The product's name as its makers write it (".NET", "SQLite3 Multiple Ciphers"). Also the key the tests look up in
/// <c>THIRD-PARTY-NOTICES.md</c>, so renaming an entry means renaming its notices row too.
/// </param>
/// <param name="Role">Integration or shipped component.</param>
/// <param name="Maker">Who makes it ("voidtools", "Ulrich Telle"), shown in the credit line.</param>
/// <param name="License">
/// The license of what BetterClipboard redistributes, as an SPDX id where one exists ("MIT", "Apache-2.0"), word for
/// word as in the notices row. <see langword="null"/> for an integration, which is not redistributed.
/// </param>
/// <param name="Use">One sentence, in the user's terms, on what BetterClipboard uses it for.</param>
/// <param name="Link">The official link, chosen by the rule in <see cref="ThirdPartyCatalog"/>.</param>
/// <param name="Packages">
/// The NuGet package ids this entry credits; the tests fail when a package the app restores is credited nowhere.
/// Empty for an integration, and for code that arrives inside another package (SQLite) or with the .NET SDK.
/// </param>
public sealed record ThirdPartyProject(
    string Name,
    ThirdPartyRole Role,
    string Maker,
    string? License,
    string Use,
    Uri Link,
    IReadOnlyList<string> Packages)
{
    /// <summary>
    /// The short form of <see cref="Link"/> shown on the link button, e.g. "getsharex.com" or
    /// "github.com/microsoft/CsWinRT" (rules in <see cref="ThirdPartyCatalog.LinkText"/>). The full address is left
    /// to the tooltip.
    /// </summary>
    public string LinkText => ThirdPartyCatalog.LinkText(Link);

    /// <summary>
    /// The credit shown after the name. A component says who makes it and under which license BetterClipboard ships
    /// it ("Ulrich Telle · MIT"); an integration says who makes it and that it is not part of BetterClipboard
    /// ("voidtools · separate app"). Not "installed separately": Windows PowerShell comes with Windows.
    /// </summary>
    public string Credit => Role == ThirdPartyRole.Integration ? $"{Maker} · separate app" : $"{Maker} · {License}";
}

/// <summary>
/// The third-party projects BetterClipboard works with or ships: the source of Settings › Third party, which the
/// tests keep in step with <c>THIRD-PARTY-NOTICES.md</c> and with the packages the app restores.
/// </summary>
/// <remarks>
/// <para>
/// <b>The official-link rule.</b> The project's own website when it has one, otherwise its source repository; for a
/// NuGet package, the project URL the package itself declares. Always the final address after redirects: no
/// <c>aka.ms</c> or other short links, which can be repointed; https; no language segment such as <c>/en-us/</c>,
/// so Microsoft's sites pick the viewer's language; no tracking parameters. One exception: the Windows SDK
/// projection's package declares the page of another package, so the Windows SDK's own page is used instead. Checked
/// on 2026-10-01: every link answers 200, and the only redirects add the viewer's language (learn.microsoft.com,
/// dotnet.microsoft.com).
/// </para>
/// <para>
/// <b>Keeping it complete.</b> A new integration adds an <see cref="ThirdPartyRole.Integration"/> entry. A new
/// package that ships adds its id to the matching component, or becomes a new component together with a row in
/// <c>THIRD-PARTY-NOTICES.md</c>. Build-only and test-only packages (the SDK build tools, xUnit) are left out on
/// purpose: nothing of them ships. Entries stay in alphabetical order (ordinal, ignoring case) within each role, so
/// a reader can find a name and a newcomer knows where to go; the tests check that too.
/// </para>
/// </remarks>
public static class ThirdPartyCatalog
{
    /// <summary>
    /// Hosts whose name alone does not say which project a link is about, so <see cref="LinkText"/> keeps the
    /// "owner/repository" part of the path for them.
    /// </summary>
    private static readonly HashSet<string> CodeHosts = new(StringComparer.OrdinalIgnoreCase) { "github.com", "gitlab.com", "codeberg.org" };

    /// <summary>
    /// The full license notices: <c>THIRD-PARTY-NOTICES.md</c> on GitHub. The same file sits next to
    /// <c>BetterClipboard.exe</c> in an installed copy, but a <c>.md</c> file may have no app to open it, and a
    /// browser does; the price is that <c>main</c> may already list a newer version's components.
    /// </summary>
    public static Uri NoticesLink { get; } = new("https://github.com/Nucs/BetterClipboard/blob/main/THIRD-PARTY-NOTICES.md");

    /// <summary>
    /// Every entry: integrations first, then components, each group in alphabetical order. Settings shows the groups
    /// as separate cards (<see cref="Integrations"/>, <see cref="Components"/>).
    /// </summary>
    public static IReadOnlyList<ThirdPartyProject> All { get; } =
    [
        // The prompt archive (CLAUDE.md §2.21) reads Claude Code's prompt history and Codex's session files; neither app is
        // shipped or changed.
        new(
            Name: "Claude Code",
            Role: ThirdPartyRole.Integration,
            Maker: "Anthropic",
            License: null,
            Use: "Every prompt you send in Claude Code (from its prompt history), kept in the panel's Claude tab.",
            Link: new("https://claude.com/product/claude-code"),
            Packages: []),
        new(
            Name: "Codex",
            Role: ThirdPartyRole.Integration,
            Maker: "OpenAI",
            License: null,
            Use: "Every prompt you send in Codex (from its session files), kept in the panel's Codex tab.",
            Link: new("https://openai.com/codex/"),
            Packages: []),
        new(
            Name: "Everything",
            Role: ThirdPartyRole.Integration,
            Maker: "voidtools",
            License: null,
            Use: "The files and folders you open from Everything, in the panel's Everything tab.",
            Link: new("https://www.voidtools.com/"),
            Packages: []),

        // The Pwsh tab reads the history file PSReadLine (PowerShell's line editor) writes for Windows PowerShell and
        // PowerShell 7 alike. Command Prompt, read for the Cmd tab, is part of Windows like Win+V and Win+R, so it is
        // not listed as a third party.
        new(
            Name: "PowerShell",
            Role: ThirdPartyRole.Integration,
            Maker: "Microsoft",
            License: null,
            Use: "Every command you typed in PowerShell (its PSReadLine history), in the panel's Pwsh tab.",
            Link: new("https://learn.microsoft.com/powershell/"),
            Packages: []),
        new(
            Name: "ShareX",
            Role: ThirdPartyRole.Integration,
            Maker: "ShareX team",
            License: null,
            Use: "Every screenshot ShareX saves joins your history, in the panel's ShareX tab.",
            Link: new("https://getsharex.com/"),
            Packages: []),

        // ProtectedData (DPAPI) is part of .NET's own libraries, published as a separate package.
        new(
            Name: ".NET",
            Role: ThirdPartyRole.Component,
            Maker: "Microsoft",
            License: "MIT",
            Use: "The runtime and libraries BetterClipboard is built on, included in the download.",
            Link: new("https://dotnet.microsoft.com/"),
            Packages: ["System.Security.Cryptography.ProtectedData"]),

        // No package of its own: WinRT.Runtime.dll comes with the .NET SDK's Windows targeting pack.
        new(
            Name: "C#/WinRT",
            Role: ThirdPartyRole.Component,
            Maker: "Microsoft",
            License: "MIT",
            Use: "Connects .NET to Windows' own APIs, such as the clipboard history the Windows import reads.",
            Link: new("https://github.com/microsoft/CsWinRT"),
            Packages: []),
        new(
            Name: "CommunityToolkit.Mvvm",
            Role: ThirdPartyRole.Component,
            Maker: "Microsoft",
            License: "MIT",
            Use: "Ties the panel and Settings to the data they show.",
            Link: new("https://github.com/CommunityToolkit/dotnet"),
            Packages: ["CommunityToolkit.Mvvm"]),
        new(
            Name: "Microsoft.Data.Sqlite",
            Role: ThirdPartyRole.Component,
            Maker: "Microsoft",
            License: "MIT",
            Use: "Reads and writes the history database.",
            Link: new("https://learn.microsoft.com/dotnet/standard/data/sqlite/"),
            Packages: ["Microsoft.Data.Sqlite.Core"]),

        // Compiled into SQLite3 Multiple Ciphers' native library, so it has no package of its own.
        new(
            Name: "SQLite",
            Role: ThirdPartyRole.Component,
            Maker: "SQLite developers",
            License: "Public domain",
            Use: "The database engine that stores your history.",
            Link: new("https://sqlite.org/"),
            Packages: []),
        new(
            Name: "SQLite3 Multiple Ciphers",
            Role: ThirdPartyRole.Component,
            Maker: "Ulrich Telle",
            License: "MIT",
            Use: "Encrypts the whole history database on disk (ChaCha20-Poly1305).",
            Link: new("https://utelle.github.io/SQLite3MultipleCiphers/"),
            Packages: ["SQLite3MC.PCLRaw.bundle", "SQLite3MC.PCLRaw.lib", "SQLite3MC.PCLRaw.provider"]),
        new(
            Name: "SQLitePCLRaw",
            Role: ThirdPartyRole.Component,
            Maker: "Eric Sink",
            License: "Apache-2.0",
            Use: "Connects .NET to the SQLite engine.",
            Link: new("https://github.com/ericsink/SQLitePCL.raw"),
            Packages: ["SQLitePCLRaw.core"]),

        // A dependency of the WinUI package: its two DLLs ship, although no BetterClipboard window hosts a WebView2.
        new(
            Name: "WebView2 SDK",
            Role: ThirdPartyRole.Component,
            Maker: "Microsoft",
            License: "BSD-3-Clause",
            Use: "Comes with WinUI 3; BetterClipboard itself shows no web pages.",
            Link: new("https://learn.microsoft.com/microsoft-edge/webview2/"),
            Packages: ["Microsoft.Web.WebView2"]),

        // The binaries' license is Microsoft's Windows App SDK terms (license.txt in each package), not the MIT
        // license of the source on GitHub.
        new(
            Name: "Windows App SDK",
            Role: ThirdPartyRole.Component,
            Maker: "Microsoft",
            License: "Windows App SDK license terms",
            Use: "WinUI 3, which draws the panel and Settings, and its text rendering (DWriteCore).",
            Link: new("https://github.com/microsoft/WindowsAppSDK"),
            Packages:
            [
                "Microsoft.WindowsAppSDK.Base",
                "Microsoft.WindowsAppSDK.DWrite",
                "Microsoft.WindowsAppSDK.Foundation",
                "Microsoft.WindowsAppSDK.InteractiveExperiences",
                "Microsoft.WindowsAppSDK.WinUI",
            ]),

        // Microsoft.Windows.SDK.NET.dll, from the targeting pack the windows10.0.26100 TFM downloads (never a
        // PackageReference). Its package declares the NuGet page of another package (Microsoft.Windows.SDK.Contracts)
        // as its project URL, hence the Windows SDK's own page.
        new(
            Name: "Windows SDK",
            Role: ThirdPartyRole.Component,
            Maker: "Microsoft",
            License: "Windows SDK license terms",
            Use: "Windows' API definitions for .NET, which the Windows import and image decoding call.",
            Link: new("https://learn.microsoft.com/windows/apps/windows-sdk/"),
            Packages: ["Microsoft.Windows.SDK.NET.Ref"]),

        // .NET 10 has no zstd decoder: Codex compresses its cold session files (.jsonl.zst), and the prompt archive reads them.
        new(
            Name: "ZstdSharp",
            Role: ThirdPartyRole.Component,
            Maker: "Oleg Stepanischev",
            License: "MIT",
            Use: "Reads the Codex session files Codex compressed, for the Codex tab.",
            Link: new("https://github.com/oleg-st/ZstdSharp"),
            Packages: ["ZstdSharp.Port"]),
    ];

    /// <summary>The apps BetterClipboard works with, for the "Works with" card (a new list on every call).</summary>
    public static IReadOnlyList<ThirdPartyProject> Integrations => [.. All.Where(project => project.Role == ThirdPartyRole.Integration)];

    /// <summary>The components inside BetterClipboard, for the "Built with" card (a new list on every call).</summary>
    public static IReadOnlyList<ThirdPartyProject> Components => [.. All.Where(project => project.Role == ThirdPartyRole.Component)];

    /// <summary>
    /// The short, recognizable form of a link for a button: the host without "www.", plus "/owner/repository" on a
    /// code host (GitHub, GitLab, Codeberg), where the host alone does not say which project it is. The rest of the
    /// path is dropped, since the button names a site rather than a page; the tooltip shows the whole address.
    /// </summary>
    /// <param name="link">An absolute link.</param>
    /// <returns>E.g. "getsharex.com" for <c>https://getsharex.com/</c>, "voidtools.com" for
    /// <c>https://www.voidtools.com/</c>, "github.com/microsoft/CsWinRT" for <c>https://github.com/microsoft/CsWinRT</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="link"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="link"/> is relative, so it has no host to show.</exception>
    public static string LinkText(Uri link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (!link.IsAbsoluteUri)
        {
            throw new ArgumentException("A relative link has no host to show.", nameof(link));
        }

        // Uri lower-cases the host of http(s) links, so "GitHub.com" and "github.com" read the same.
        var host = link.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? link.Host[4..] : link.Host;
        if (!CodeHosts.Contains(host))
        {
            return host;
        }

        // AbsolutePath keeps escapes (%20); show the characters they stand for.
        var repository = link.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(Uri.UnescapeDataString);
        return string.Join('/', repository.Prepend(host));
    }
}
