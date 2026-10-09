using System.Globalization;
using BetterClipboard.Core.Updates;

namespace BetterClipboard.Core.Presentation;

/// <summary>
/// The words the update button, the update dialog and the Settings card say for an <see cref="UpdateSnapshot"/>.
/// </summary>
/// <remarks>
/// Pure and clock-injected like <see cref="RelativeTimeFormatter"/>, so the wording is unit-tested and one snapshot reads
/// the same in the panel, in Settings and for a screen reader.
/// </remarks>
public static class UpdateText
{
    /// <summary>
    /// The update button's tooltip and accessible name: what a click will show.
    /// </summary>
    /// <param name="snapshot">The state.</param>
    /// <returns>E.g. "Update available: BetterClipboard 0.2.7", "BetterClipboard 0.2.6 is up to date", "Updates".</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public static string ButtonTip(UpdateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var offered = snapshot.Offer?.Release.Version.ToString();
        return snapshot.Phase switch
        {
            UpdatePhase.Downloading when offered is not null =>
                snapshot.TotalBytes > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"Downloading BetterClipboard {offered}: {snapshot.DownloadFraction * 100:0} %")
                    : $"Downloading BetterClipboard {offered}",
            UpdatePhase.Verifying when offered is not null => $"Checking the download of BetterClipboard {offered}",
            UpdatePhase.Installing when offered is not null => $"Installing BetterClipboard {offered}",
            UpdatePhase.Checking when offered is null => "Checking for updates",
            _ when offered is not null && snapshot.IsSkipped => $"BetterClipboard {offered} is available (you skipped it)",
            _ when offered is not null => $"Update available: BetterClipboard {offered}",
            _ when snapshot.LastChecked is not null => $"BetterClipboard {snapshot.CurrentVersion} is up to date",
            _ => "Updates",
        };
    }

    /// <summary>
    /// The dialog's title line.
    /// </summary>
    /// <param name="snapshot">The state.</param>
    /// <returns>E.g. "BetterClipboard 0.2.7 is available", "BetterClipboard is up to date", "Checking for updates…".</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public static string Title(UpdateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var offered = snapshot.Offer?.Release.Version.ToString();
        return snapshot.Phase switch
        {
            UpdatePhase.Downloading when offered is not null => $"Downloading BetterClipboard {offered}…",
            UpdatePhase.Verifying when offered is not null => "Checking the download…",
            UpdatePhase.Installing when offered is not null => $"Installing BetterClipboard {offered}…",
            _ when offered is not null => $"BetterClipboard {offered} is available",
            UpdatePhase.Checking => "Checking for updates…",
            _ when snapshot.LastChecked is not null => "BetterClipboard is up to date",
            _ when snapshot.CheckProblem is not null => "Could not check for updates",
            _ => NeutralTitle,
        };
    }

    /// <summary>The title while nothing is known yet (no check ran, none failed): only what the dialog is about.</summary>
    private const string NeutralTitle = "Updates";

    /// <summary>Ends a title with a full stop, unless it already ends a sentence (an ellipsis counts).</summary>
    /// <param name="title">A title from <see cref="Title"/>.</param>
    /// <returns>The title as a sentence.</returns>
    private static string AsSentence(string title) =>
        title.Length == 0 || title[^1] is '.' or '!' or '?' or '…' ? title : title + ".";

    /// <summary>
    /// The lines under the dialog's title: what the state means for the user and what the buttons will do.
    /// </summary>
    /// <param name="snapshot">The state.</param>
    /// <param name="now">The reference "now" for relative times.</param>
    /// <param name="zone">Time zone for calendar-day wording (defaults to local).</param>
    /// <param name="culture">Culture for numbers and dates (defaults to the current UI culture).</param>
    /// <returns>One or two sentences.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public static string Detail(UpdateSnapshot snapshot, DateTimeOffset now, TimeZoneInfo? zone = null, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        culture ??= CultureInfo.CurrentUICulture;
        switch (snapshot.Phase)
        {
            case UpdatePhase.Downloading:
                return Progress(snapshot.DownloadedBytes, snapshot.TotalBytes, culture);
            case UpdatePhase.Verifying:
                return "Comparing the download with the release's SHA-256 checksum.";
            case UpdatePhase.Installing:
                return "BetterClipboard closes and starts again by itself in a moment. Your history is kept.";
        }

        if (snapshot.Offer is { } offer)
        {
            var released = offer.Release.PublishedAt is { } at ? $" Released {RelativeTimeFormatter.Format(at, now, zone, culture)}." : string.Empty;
            var have = $"You have {snapshot.CurrentVersion}.{released}";
            return snapshot.InstallKind switch
            {
                UpdateInstallKind.Installer => $"{have} Updating downloads it from GitHub, checks it, and restarts BetterClipboard. Your history is kept.",
                UpdateInstallKind.Chocolatey => $"{have} This copy was installed with Chocolatey, which updates it: {UpdateSource.ChocolateyCommand}",
                _ => $"{have} This copy was not installed with the installer, so it does not replace itself. Get the new version from the download page, or install it with the command.",
            };
        }

        var checkedText = snapshot.LastChecked is { } last ? $"Checked {RelativeTimeFormatter.Format(last, now, zone, culture)}." : string.Empty;
        if (snapshot.Phase == UpdatePhase.Checking)
        {
            return $"Version {snapshot.CurrentVersion}. Asking GitHub for the list of releases.";
        }

        if (snapshot.LastChecked is not null)
        {
            // A build that is newer than every release (installed before its release) says so, or "up to date" would puzzle.
            var newer = snapshot.Latest is { } latest && snapshot.CurrentVersion > latest.Version
                ? $"Version {snapshot.CurrentVersion}, newer than the latest release ({latest.Version})."
                : $"Version {snapshot.CurrentVersion} is the latest release.";
            return $"{newer} {checkedText}";
        }

        if (!snapshot.AutomaticChecks)
        {
            return $"Version {snapshot.CurrentVersion}. {AutomaticChecksOff(snapshot.InstallKind)}";
        }

        return $"Version {snapshot.CurrentVersion}. BetterClipboard has not checked for updates yet.";
    }

    /// <summary>
    /// The Settings card's status line: one sentence about the state, plus the last check or the last problem.
    /// </summary>
    /// <param name="snapshot">The state.</param>
    /// <param name="now">The reference "now" for relative times.</param>
    /// <param name="zone">Time zone for calendar-day wording (defaults to local).</param>
    /// <param name="culture">Culture for numbers and dates (defaults to the current UI culture).</param>
    /// <returns>The status text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public static string SettingsStatus(UpdateSnapshot snapshot, DateTimeOffset now, TimeZoneInfo? zone = null, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // The dialog shows the title as a heading over the detail; here both are one line of prose, so the title becomes
        // a sentence of its own. The bare "Updates" (nothing known yet) says nothing and is left out.
        var title = Title(snapshot);
        var detail = Detail(snapshot, now, zone, culture);
        var lines = new List<string>(5) { title == NeutralTitle ? detail : $"{AsSentence(title)} {detail}".Trim() };

        // Without this line a skipped version looks forgotten: it is offered, yet nothing is highlighted for it.
        if (snapshot.IsSkipped && !snapshot.IsInstalling)
        {
            lines.Add("You skipped this version, so the update button is not highlighted for it.");
        }

        if (snapshot.CheckProblem is { } check && snapshot.Phase != UpdatePhase.Checking)
        {
            lines.Add($"The last check failed: {check.Message}");
        }

        if (snapshot.InstallProblem is { } install)
        {
            lines.Add(install.Message);
        }

        if (!snapshot.AutomaticChecks && snapshot.LastChecked is not null)
        {
            lines.Add(AutomaticChecksOff(snapshot.InstallKind));
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// The heading over the release notes.
    /// </summary>
    /// <param name="changelog">The changelog.</param>
    /// <returns>"What is new", "What is new in this version", "The latest release", or empty for no notes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="changelog"/> is <see langword="null"/>.</exception>
    public static string ChangelogHeading(Changelog changelog)
    {
        ArgumentNullException.ThrowIfNull(changelog);
        return changelog.Kind switch
        {
            ChangelogKind.NewerReleases => "What is new",
            ChangelogKind.CurrentRelease => "What is new in this version",
            ChangelogKind.LatestRelease => "The latest release",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// The line over one release's notes: its version and when it was published.
    /// </summary>
    /// <param name="release">The release.</param>
    /// <param name="zone">Time zone for the date (defaults to local).</param>
    /// <param name="culture">Culture for the date (defaults to the current UI culture).</param>
    /// <returns>E.g. "0.2.7 · 12 Oct 2026", or the version alone when the date is unknown.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="release"/> is <see langword="null"/>.</exception>
    public static string ReleaseHeading(ReleaseInfo release, TimeZoneInfo? zone = null, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(release);
        culture ??= CultureInfo.CurrentUICulture;
        return release.PublishedAt is { } at
            ? $"{release.Version} · {TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local).ToString("d MMM yyyy", culture)}"
            : release.Version.ToString();
    }

    /// <summary>
    /// How far a download is, in megabytes.
    /// </summary>
    /// <param name="downloaded">Bytes received.</param>
    /// <param name="total">The package's size in bytes; 0 when unknown.</param>
    /// <param name="culture">Culture for the numbers (defaults to the current UI culture).</param>
    /// <returns>E.g. "23.4 of 70.8 MB", or "23.4 MB" when the size is unknown.</returns>
    public static string Progress(long downloaded, long total, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        const double Megabyte = 1024d * 1024d;
        double done = Math.Max(0, downloaded) / Megabyte;
        return total > 0
            ? string.Create(culture, $"{done:0.0} of {total / Megabyte:0.0} MB")
            : string.Create(culture, $"{done:0.0} MB");
    }

    /// <summary>Why this copy does not check by itself, and what does instead.</summary>
    /// <param name="kind">How the copy was installed.</param>
    /// <returns>One sentence.</returns>
    private static string AutomaticChecksOff(UpdateInstallKind kind) => kind == UpdateInstallKind.Chocolatey
        ? "Chocolatey updates this copy, so BetterClipboard does not check by itself."
        : "Automatic checks are off: BetterClipboard asks GitHub only when you press Check now.";
}
