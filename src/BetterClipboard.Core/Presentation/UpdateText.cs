using BetterClipboard.Core.Localization;
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
    /// <returns>
    /// E.g. "Update available: BetterClipboard 0.2.7", "Dev build available: BetterClipboard 0.2.10-dev.15.gabc1234"
    /// (a local build), "Back to the release: BetterClipboard 0.2.9" (the latest release offered to a newer development
    /// build), "BetterClipboard 0.2.6 is up to date", "Updates".
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public static string ButtonTip(UpdateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var offered = snapshot.Offer?.Release.Version.ToString();

        // A local build is copied, not downloaded: the words follow what really happens.
        bool local = snapshot.IsLocalOffer;
        return snapshot.Phase switch
        {
            UpdatePhase.Downloading when offered is not null && local =>
                snapshot.TotalBytes > 0
                    ? UiText.Format(CultureInfo.InvariantCulture, $"Copying BetterClipboard {offered}: {snapshot.DownloadFraction * 100:0} %")
                    : UiText.Format($"Copying BetterClipboard {offered}"),
            UpdatePhase.Downloading when offered is not null =>
                snapshot.TotalBytes > 0
                    ? UiText.Format(CultureInfo.InvariantCulture, $"Downloading BetterClipboard {offered}: {snapshot.DownloadFraction * 100:0} %")
                    : UiText.Format($"Downloading BetterClipboard {offered}"),
            UpdatePhase.Verifying when offered is not null && local => UiText.Format($"Checking the copy of BetterClipboard {offered}"),
            UpdatePhase.Verifying when offered is not null => UiText.Format($"Checking the download of BetterClipboard {offered}"),
            UpdatePhase.Installing when offered is not null => UiText.Format($"Installing BetterClipboard {offered}"),
            UpdatePhase.Checking when offered is null => UiText.Translate("Checking for updates"),
            _ when offered is not null && snapshot.IsSkipped => UiText.Format($"BetterClipboard {offered} is available (you skipped it)"),
            _ when offered is not null && local => UiText.Format($"Dev build available: BetterClipboard {offered}"),
            _ when offered is not null && snapshot.OffersOlderVersion => UiText.Format($"Back to the release: BetterClipboard {offered}"),
            _ when offered is not null => UiText.Format($"Update available: BetterClipboard {offered}"),
            _ when snapshot.LastChecked is not null => UiText.Format($"BetterClipboard {snapshot.CurrentVersion} is up to date"),
            _ => UiText.Translate("Updates"),
        };
    }

    /// <summary>
    /// The dialog's title line.
    /// </summary>
    /// <param name="snapshot">The state.</param>
    /// <returns>
    /// E.g. "BetterClipboard 0.2.7 is available", "BetterClipboard 0.2.9 is the latest release" (offered to a newer
    /// development build), "BetterClipboard is up to date", "Checking for updates…".
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public static string Title(UpdateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var offered = snapshot.Offer?.Release.Version.ToString();
        bool local = snapshot.IsLocalOffer;
        return snapshot.Phase switch
        {
            UpdatePhase.Downloading when offered is not null && local => UiText.Format($"Copying BetterClipboard {offered}…"),
            UpdatePhase.Downloading when offered is not null => UiText.Format($"Downloading BetterClipboard {offered}…"),
            UpdatePhase.Verifying when offered is not null && local => UiText.Translate("Checking the copy…"),
            UpdatePhase.Verifying when offered is not null => UiText.Translate("Checking the download…"),
            UpdatePhase.Installing when offered is not null => UiText.Format($"Installing BetterClipboard {offered}…"),

            // "Available" would read as "newer": the way back from a development build says what it is instead.
            _ when offered is not null && snapshot.OffersOlderVersion => UiText.Format($"BetterClipboard {offered} is the latest release"),
            _ when offered is not null => UiText.Format($"BetterClipboard {offered} is available"),
            UpdatePhase.Checking => UiText.Translate("Checking for updates…"),
            _ when snapshot.LastChecked is not null => UiText.Translate("BetterClipboard is up to date"),
            _ when snapshot.CheckProblem is not null => UiText.Translate("Could not check for updates"),
            _ => UiText.Translate(NeutralTitle),
        };
    }

    /// <summary>The title while nothing is known yet (no check ran, none failed): only what the dialog is about.</summary>
    private const string NeutralTitle = "Updates";

    /// <summary>
    /// The words of the entries that open the update dialog for an offer: the Settings card's button and the tray menu's
    /// item.
    /// </summary>
    /// <param name="snapshot">The state; <see cref="UpdateSnapshot.Offer"/> must be set.</param>
    /// <returns>"Update to 0.2.7…", or "Install 0.2.9…" for the latest release offered to a newer development build.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Nothing is offered.</exception>
    public static string OfferAction(UpdateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var offer = snapshot.Offer ?? throw new ArgumentException("Nothing is offered.", nameof(snapshot));
        return snapshot.OffersOlderVersion
            ? UiText.Format($"Install {offer.Release.Version}…")
            : UiText.Format($"Update to {offer.Release.Version}…");
    }

    /// <summary>
    /// The label of the dialog's button that approves the offer, for a copy that installs updates itself.
    /// </summary>
    /// <param name="snapshot">The state.</param>
    /// <returns>"Update and restart", or "Install and restart" when the offer is older than the running development build.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public static string ApproveButton(UpdateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // "Update" to an older version would promise something newer: going back to the release is an install.
        return snapshot.OffersOlderVersion ? UiText.Translate("Install and restart") : UiText.Translate("Update and restart");
    }

    /// <summary>
    /// A failure's message in the interface language. The update system words it once, in English, for the log as well
    /// (<see cref="UpdateMessages"/>); what came from outside the app inside it (the installer's own report, a network or
    /// file system reason) stays as it came.
    /// </summary>
    /// <param name="problem">The failure.</param>
    /// <returns>The message to show.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="problem"/> is <see langword="null"/>.</exception>
    public static string ProblemText(UpdateProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return UiText.TranslateFormatted(problem.Message, UpdateMessages.Templates);
    }

    /// <summary>Ends a title with a full stop, unless it already ends a sentence (an ellipsis counts).</summary>
    /// <remarks>
    /// A right-to-left title comes embedded (<see cref="UiLanguage.IsRightToLeft"/>: U+202B … U+202C around it). Its
    /// sentence ends before those invisible marks, so they are looked past, and the period goes in before them: added
    /// after the closing mark, it would sit outside the embedding, at the right edge, where a Hebrew reader starts.
    /// </remarks>
    /// <param name="title">A title from <see cref="Title"/>.</param>
    /// <returns>The title as a sentence.</returns>
    internal static string AsSentence(string title)
    {
        // Format characters (an embedding's marks, a direction mark) are invisible: the sentence ends at the last visible one.
        int end = title.Length;
        while (end > 0 && char.GetUnicodeCategory(title[end - 1]) == UnicodeCategory.Format)
        {
            end--;
        }

        return end == 0 || title[end - 1] is '.' or '!' or '?' or '…' or '。' or '！' or '？' ? title : title.Insert(end, ".");
    }

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
                return snapshot.IsLocalOffer
                    ? UiText.Translate("Comparing the copy with the build's SHA-256 checksum.")
                    : UiText.Translate("Comparing the download with the release's SHA-256 checksum.");
            case UpdatePhase.Installing:
                return UiText.Translate("BetterClipboard closes and starts again by itself in a moment. Your history is kept.");
        }

        // Only a copy the installer installed is ever offered these two (the others neither read the dev-builds folder nor
        // install anything): the words say what its "Update" or "Install" button does.
        if (snapshot.Offer is { } localOffer && snapshot.IsLocalOffer && snapshot.InstallKind == UpdateInstallKind.Installer)
        {
            var published = localOffer.Release.PublishedAt is { } when ? UiText.Format($" Published {RelativeTimeFormatter.Format(when, now, zone, culture)}.") : string.Empty;
            var haveBuild = UiText.Format($"You have {snapshot.CurrentVersion}.{published}");
            return UiText.Format($"{haveBuild} It is a development build from the dev-builds folder on this PC: updating copies it from there, checks it, and restarts BetterClipboard. Your history is kept.");
        }

        if (snapshot.Offer is not null && snapshot.OffersOlderVersion && snapshot.InstallKind == UpdateInstallKind.Installer)
        {
            return UiText.Format($"You have {snapshot.CurrentVersion}, a development build that is newer than this release. Installing it replaces the development build with the official release from GitHub and restarts BetterClipboard. Your history is kept.");
        }

        if (snapshot.Offer is { } offer)
        {
            var released = offer.Release.PublishedAt is { } at ? UiText.Format($" Released {RelativeTimeFormatter.Format(at, now, zone, culture)}.") : string.Empty;
            var have = UiText.Format($"You have {snapshot.CurrentVersion}.{released}");
            return snapshot.InstallKind switch
            {
                UpdateInstallKind.Installer => UiText.Format($"{have} Updating downloads it from GitHub, checks it, and restarts BetterClipboard. Your history is kept."),
                UpdateInstallKind.Chocolatey => UiText.Format($"{have} This copy was installed with Chocolatey, which updates it: {UpdateSource.ChocolateyCommand}"),
                UpdateInstallKind.AllUsers => UiText.Format($"{have} This copy is installed for every user of this PC, so an administrator updates it: copy the command and run it in PowerShell as administrator."),
                _ => UiText.Format($"{have} This copy was not installed with the installer, so it does not replace itself. Get the new version from the download page, or install it with the command."),
            };
        }

        var checkedText = snapshot.LastChecked is { } last ? UiText.Format($"Checked {RelativeTimeFormatter.Format(last, now, zone, culture)}.") : string.Empty;
        if (snapshot.Phase == UpdatePhase.Checking)
        {
            return UiText.Format($"Version {snapshot.CurrentVersion}. Asking GitHub for the list of releases.");
        }

        if (snapshot.LastChecked is not null)
        {
            // A build that is newer than every release (installed before its release) says so, or "up to date" would puzzle.
            var newer = snapshot.Latest is { } latest && snapshot.CurrentVersion > latest.Version
                ? UiText.Format($"Version {snapshot.CurrentVersion}, newer than the latest release ({latest.Version}).")
                : UiText.Format($"Version {snapshot.CurrentVersion} is the latest release.");

            // Two sentences on one line: in a right-to-left language the line is ordered right to left as a whole.
            return UiText.JoinLine(" ", [newer, checkedText]);
        }

        if (!snapshot.AutomaticChecks)
        {
            return UiText.Format($"Version {snapshot.CurrentVersion}. {AutomaticChecksOff(snapshot.InstallKind)}");
        }

        return UiText.Format($"Version {snapshot.CurrentVersion}. BetterClipboard has not checked for updates yet.");
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
        // Title and detail share one line, so a right-to-left language orders them as one (JoinLine). An empty piece is left
        // out, which gives what trimming "title detail" gave before: no stray space inside a right-to-left embedding.
        var lines = new List<string>(5)
        {
            title == UiText.Translate(NeutralTitle) ? detail
                : UiText.JoinLine(" ", new[] { AsSentence(title), detail }.Where(part => part.Length > 0)).Trim(),
        };

        // Without this line a skipped version looks forgotten: it is offered, yet nothing is highlighted for it.
        if (snapshot.IsSkipped && !snapshot.IsInstalling)
        {
            lines.Add(UiText.Translate("You skipped this version, so the update button is not highlighted for it."));
        }

        if (snapshot.CheckProblem is { } check && snapshot.Phase != UpdatePhase.Checking)
        {
            // The message was worded in English for the log too; it is translated from its template here.
            lines.Add(UiText.Format($"The last check failed: {ProblemText(check)}"));
        }

        if (snapshot.InstallProblem is { } install)
        {
            lines.Add(ProblemText(install));
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
            ChangelogKind.NewerReleases => UiText.Translate("What is new"),
            ChangelogKind.CurrentRelease => UiText.Translate("What is new in this version"),
            ChangelogKind.LatestRelease => UiText.Translate("The latest release"),
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
            ? UiText.Format(culture, $"{done:0.0} of {total / Megabyte:0.0} MB")
            : string.Format(culture, "{0:0.0} MB", done);
    }

    /// <summary>Why this copy does not check by itself, and what does instead.</summary>
    /// <param name="kind">How the copy was installed.</param>
    /// <returns>One sentence.</returns>
    private static string AutomaticChecksOff(UpdateInstallKind kind) => kind switch
    {
        UpdateInstallKind.Chocolatey => UiText.Translate("Chocolatey updates this copy, so BetterClipboard does not check by itself."),
        UpdateInstallKind.AllUsers => UiText.Translate("This copy is installed for every user of this PC and an administrator updates it, so BetterClipboard does not check by itself. Check now still asks GitHub."),
        _ => UiText.Translate("Automatic checks are off: BetterClipboard asks GitHub only when you press Check now."),
    };
}
