namespace BetterClipboard.Core.Updates;

/// <summary>What the updater is doing right now.</summary>
public enum UpdatePhase
{
    /// <summary>Nothing: waiting for the next check, or for the user.</summary>
    Idle = 0,

    /// <summary>Asking the release feed for the release list.</summary>
    Checking = 1,

    /// <summary>Downloading the approved update's package.</summary>
    Downloading = 2,

    /// <summary>Comparing the download's SHA-256 with the release's checksum list.</summary>
    Verifying = 3,

    /// <summary>The installer runs; it closes this app, replaces its files and starts the new version.</summary>
    Installing = 4,
}

/// <summary>
/// A failed check or installation, as the dialog shows it.
/// </summary>
/// <param name="Failure">The kind of failure.</param>
/// <param name="Message">A complete sentence for the user.</param>
/// <param name="RetryAfter">For a rate limit: when the host accepts requests again, if known.</param>
public sealed record UpdateProblem(UpdateFailure Failure, string Message, DateTimeOffset? RetryAfter = null)
{
    /// <summary>Takes the user-facing parts of an exception.</summary>
    /// <param name="exception">The failure.</param>
    /// <returns>The problem.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    public static UpdateProblem From(UpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new UpdateProblem(exception.Failure, exception.Message, exception.RetryAfter);
    }
}

/// <summary>
/// Everything the user interface shows about updates at one moment: immutable, so a snapshot read on the UI thread never
/// changes under it. <see cref="UpdateService.Current"/> is replaced as a whole on every change.
/// </summary>
public sealed record UpdateSnapshot
{
    /// <summary>The running app's version.</summary>
    public required SemanticVersion CurrentVersion { get; init; }

    /// <summary>How this copy was installed, which decides what the dialog can offer.</summary>
    public UpdateInstallKind InstallKind { get; init; }

    /// <summary>What the updater is doing.</summary>
    public UpdatePhase Phase { get; init; }

    /// <summary>
    /// Whether this copy asks for updates by itself (the setting is on, and the copy is neither a Chocolatey install nor
    /// an isolated instance). When <see langword="false"/>, nothing is requested until the user asks.
    /// </summary>
    public bool AutomaticChecks { get; init; }

    /// <summary>The update that can be installed, or <see langword="null"/> when the app is up to date or nothing is known yet.</summary>
    public UpdateOffer? Offer { get; init; }

    /// <summary>The newest stable release in the last release list, or <see langword="null"/> when none is known.</summary>
    public ReleaseInfo? Latest { get; init; }

    /// <summary>The release notes to show below the dialog's buttons.</summary>
    public Changelog Changelog { get; init; } = Changelog.Empty;

    /// <summary>When the release list was last received (or confirmed unchanged); <see langword="null"/> when never.</summary>
    public DateTimeOffset? LastChecked { get; init; }

    /// <summary>Why the last check failed, or <see langword="null"/> when it succeeded or none ran. A later success clears it.</summary>
    public UpdateProblem? CheckProblem { get; init; }

    /// <summary>Why the last download or installation failed, or <see langword="null"/>. Cleared when a new attempt starts.</summary>
    public UpdateProblem? InstallProblem { get; init; }

    /// <summary>Bytes of the package received so far (while <see cref="Phase"/> is <see cref="UpdatePhase.Downloading"/>).</summary>
    public long DownloadedBytes { get; init; }

    /// <summary>The package's size in bytes, or 0 when unknown.</summary>
    public long TotalBytes { get; init; }

    /// <summary>
    /// Whether the user chose to skip the offered version (<c>AppSettings.SkippedUpdateVersion</c>): it is still offered
    /// in the dialog, but the button is not highlighted for it.
    /// </summary>
    public bool IsSkipped { get; init; }

    /// <summary>Whether a newer release can be installed.</summary>
    public bool IsUpdateAvailable => Offer is not null;

    /// <summary>Whether an approved update is on its way (downloading, verifying or installing).</summary>
    public bool IsInstalling => Phase is UpdatePhase.Downloading or UpdatePhase.Verifying or UpdatePhase.Installing;

    /// <summary>
    /// Whether the panel's update button is highlighted: an update waits that the user has not skipped, or one is being
    /// installed.
    /// </summary>
    public bool WantsAttention => IsInstalling || (IsUpdateAvailable && !IsSkipped);

    /// <summary>
    /// Whether this copy can install <see cref="Offer"/> by itself: it was installed by the installer. Other copies get
    /// a command or a link instead.
    /// </summary>
    public bool CanInstall => IsUpdateAvailable && InstallKind == UpdateInstallKind.Installer;

    /// <summary>The download's progress from 0 to 1, or 0 when the size is unknown.</summary>
    public double DownloadFraction => TotalBytes > 0 ? Math.Clamp((double)DownloadedBytes / TotalBytes, 0, 1) : 0;
}
