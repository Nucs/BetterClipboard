namespace BetterClipboard.Core.Updates;

/// <summary>
/// Where the updater looks for releases, and which download addresses it trusts: GitHub's releases of this project, or —
/// for tests and isolated runs only — a stand-in feed named by an environment variable.
/// </summary>
/// <remarks>
/// <para>
/// <b>GitHub</b> (<see cref="GitHub"/>): the release list comes from the public API (no account, no token), and a package
/// is downloaded only from this repository's release downloads on <c>github.com</c> over https. The API's answer names
/// the download addresses, so an answer that pointed elsewhere (a tampered cache, a bug) is refused instead of followed.
/// GitHub then redirects to its file servers; the redirect is followed, and the SHA-256 check decides.
/// </para>
/// <para>
/// <b>The override</b> (<see cref="FeedVariable"/>) lets a test serve a release list and packages from a local web
/// server. The app honors it only for an isolated instance (its own data directory), never for the installed one: see
/// <c>AppController</c>. Downloads must then come from the feed's own scheme, host and port, and plain http is accepted
/// only for the loopback host.
/// </para>
/// </remarks>
public sealed class UpdateSource
{
    /// <summary>The GitHub repository whose releases are the updates (<c>owner/name</c>).</summary>
    public const string Repository = "Nucs/BetterClipboard";

    /// <summary>
    /// Environment variable naming a stand-in release feed (the address of a JSON release list) for tests and isolated
    /// runs. Ignored by the installed app.
    /// </summary>
    public const string FeedVariable = "BETTERCLIPBOARD_UPDATE_FEED";

    /// <summary>
    /// The command that installs or updates BetterClipboard from PowerShell (the README's), for copies that cannot update
    /// themselves.
    /// </summary>
    public const string InstallCommand = "irm https://raw.githubusercontent.com/" + Repository + "/main/install.ps1 | iex";

    /// <summary>The command that updates a copy installed with Chocolatey.</summary>
    public const string ChocolateyCommand = "choco upgrade betterclipboard";

    /// <summary>The start of every package download address on GitHub: this repository's release downloads.</summary>
    private const string GitHubDownloadPath = "/" + Repository + "/releases/download/";

    /// <summary>Creates a source.</summary>
    /// <param name="feedUrl">The release list's address.</param>
    /// <param name="releasesPage">The page people read releases on.</param>
    /// <param name="isOverride">Whether this is a stand-in feed.</param>
    private UpdateSource(Uri feedUrl, Uri releasesPage, bool isOverride)
    {
        FeedUrl = feedUrl;
        ReleasesPage = releasesPage;
        IsOverride = isOverride;
    }

    /// <summary>
    /// GitHub's releases of this project. The list holds up to 30 releases, newest first, so a copy several versions
    /// behind gets every changelog in one request.
    /// </summary>
    public static UpdateSource GitHub { get; } = new(
        new Uri("https://api.github.com/repos/" + Repository + "/releases?per_page=30"),
        new Uri("https://github.com/" + Repository + "/releases"),
        isOverride: false);

    /// <summary>The address of the release list (a JSON array in GitHub's format).</summary>
    public Uri FeedUrl { get; }

    /// <summary>The web page that lists the releases, opened in the browser from the dialog's "View on GitHub".</summary>
    public Uri ReleasesPage { get; }

    /// <summary>Whether this is a stand-in feed (<see cref="FeedVariable"/>), not GitHub.</summary>
    public bool IsOverride { get; }

    /// <summary>
    /// Creates a stand-in source from the value of <see cref="FeedVariable"/>.
    /// </summary>
    /// <param name="feedUrl">The variable's value; may be <see langword="null"/> or blank.</param>
    /// <returns>
    /// The source, or <see langword="null"/> when the value is blank, not an absolute address, not http(s), or plain
    /// http to anything but the loopback host (a feed over an open network could be replaced on the way).
    /// </returns>
    public static UpdateSource? TryCreateOverride(string? feedUrl)
    {
        if (string.IsNullOrWhiteSpace(feedUrl) || !Uri.TryCreate(feedUrl.Trim(), UriKind.Absolute, out var url))
        {
            return null;
        }

        bool secure = url.Scheme == Uri.UriSchemeHttps;
        bool loopbackHttp = url.Scheme == Uri.UriSchemeHttp && url.IsLoopback;
        return secure || loopbackHttp ? new UpdateSource(url, url, isOverride: true) : null;
    }

    /// <summary>
    /// Whether a package may be downloaded from <paramref name="url"/>.
    /// </summary>
    /// <param name="url">A download address from the release list.</param>
    /// <returns>
    /// For GitHub: <see langword="true"/> only for https addresses on <c>github.com</c> under this repository's release
    /// downloads. For a stand-in feed: only for addresses on the feed's own scheme, host and port.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="url"/> is <see langword="null"/>.</exception>
    public bool IsTrustedDownload(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || !string.IsNullOrEmpty(url.UserInfo))
        {
            return false;
        }

        if (IsOverride)
        {
            return url.Scheme == FeedUrl.Scheme && url.Port == FeedUrl.Port &&
                   string.Equals(url.Host, FeedUrl.Host, StringComparison.OrdinalIgnoreCase);
        }

        return url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort &&
               string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
               url.AbsolutePath.StartsWith(GitHubDownloadPath, StringComparison.OrdinalIgnoreCase);
    }
}
