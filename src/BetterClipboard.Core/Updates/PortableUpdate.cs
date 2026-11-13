namespace BetterClipboard.Core.Updates;

/// <summary>
/// The rules of a portable copy's own update (<see cref="UpdateInstallKind.Portable"/>: a release zip extracted by hand):
/// which offers it may install in its own folder, and the names that the old version, the new version's helper and the
/// app's start share.
/// </summary>
/// <remarks>
/// <para>
/// <b>How it goes</b> (CLAUDE.md §2.29). The update service downloads and verifies the release zip as for an installed
/// copy. The Windows layer unpacks it into a staging folder of the updates folder and starts the <b>new</b> version's
/// <c>BetterClipboard.exe</c> from there with <see cref="ApplySwitch"/>. That helper asks the running copy to exit, waits
/// for it, replaces the folder's files one by one (each old file renamed into a backup folder first, so a failure puts the
/// whole previous version back), never touches a file the release does not ship (a portable folder may hold the user's
/// own files), writes the same result file <c>install.ps1</c> writes, and starts the new version from the same path.
/// </para>
/// <para>
/// <b>The helper's command line is a contract</b> with every release to come, like <c>install.ps1 -Update</c>'s: the old
/// version passes <see cref="ApplySwitch"/>, <see cref="TargetSwitch"/>, <see cref="WaitForSwitch"/>,
/// <see cref="ResultSwitch"/> and <see cref="LogSwitch"/> to the new version's exe. A later version may add arguments only
/// when every release it can update to accepts them, and never removes these.
/// </para>
/// <para>
/// <b>Only offers that know the helper.</b> A release older than <see cref="MinimumVersion"/> would not recognize
/// <see cref="ApplySwitch"/> and would start as a plain launch from the staging folder (versions ignore unknown
/// arguments), so such an offer — the way back from a portable development build to an older release — gets the
/// download page and the install command instead (<see cref="Accepts"/>).
/// </para>
/// </remarks>
public static class PortableUpdate
{
    /// <summary>
    /// The first release whose <c>BetterClipboard.exe</c> understands <see cref="ApplySwitch"/>. Footgun: it must name the
    /// first release that really ships the helper; if that release gets another number, raise this with it.
    /// </summary>
    public static readonly SemanticVersion MinimumVersion = new(0, 2, 11);

    /// <summary>The switch that runs <c>BetterClipboard.exe</c> as the helper that applies an update to a portable folder.</summary>
    public const string ApplySwitch = "--apply-portable-update";

    /// <summary>Followed by the portable folder to update (the running copy's folder).</summary>
    public const string TargetSwitch = "--target";

    /// <summary>Followed by the running copy's process as <c>pid:startTicks</c> (UTC ticks of its start), which the helper waits for.</summary>
    public const string WaitForSwitch = "--wait-for";

    /// <summary>Followed by the result file the app reads at its next start (<see cref="UpdateResult"/>).</summary>
    public const string ResultSwitch = "--result";

    /// <summary>Followed by the log file the helper writes what it did to.</summary>
    public const string LogSwitch = "--log";

    /// <summary>
    /// The start of the name of the folder, inside the portable folder, that holds the previous version's files during an
    /// update. A suffix makes each run's folder its own; the app removes leftovers (a file another program kept open) at
    /// its next start.
    /// </summary>
    public const string BackupFolderPrefix = ".betterclipboard-update-backup";

    /// <summary>The start of the name of a staging folder in the updates folder (<see cref="StagingFolderName"/>).</summary>
    public const string StagingFolderPrefix = "portable-";

    /// <summary>
    /// Whether a portable copy may install <paramref name="offered"/> in its own folder: the folder can be written, and the
    /// release is new enough to apply itself (<see cref="MinimumVersion"/>).
    /// </summary>
    /// <param name="offered">The offered release's version.</param>
    /// <param name="folderWritable">Whether this user can create and delete files in the copy's folder.</param>
    /// <returns><see langword="true"/> when "Update and restart" can be offered.</returns>
    public static bool Accepts(SemanticVersion offered, bool folderWritable) => folderWritable && offered >= MinimumVersion;

    /// <summary>The staging folder's name for one version, inside the updates folder.</summary>
    /// <param name="version">The version being installed.</param>
    /// <returns>E.g. <c>portable-0.2.12</c>.</returns>
    public static string StagingFolderName(SemanticVersion version) => StagingFolderPrefix + version;
}
