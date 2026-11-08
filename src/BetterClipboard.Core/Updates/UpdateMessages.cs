using System.Globalization;

namespace BetterClipboard.Core.Updates;

/// <summary>
/// Every sentence the update system words for a failure (the message of an <see cref="UpdateException"/> and
/// <see cref="UpdateProblem.Message"/>), as a composite format in English.
/// </summary>
/// <remarks>
/// <para>
/// One message serves two readers: the log keeps it in English, and the update dialog and Settings show it in the
/// interface language. So the messages are made only from these templates (<see cref="Format"/>), and the interface
/// translates a finished message back through <see cref="Templates"/>
/// (<see cref="Localization.UiText.TranslateFormatted(string?, IReadOnlyList{string})"/>). A message worded anywhere else
/// would show in English; a new template needs an entry in every catalog, which the tests check against this list.
/// </para>
/// <para>
/// What goes into a placeholder is not translated: file names, versions, numbers, a path, and text from outside the app
/// (the network stack's reason, the installer's own report, a file system error in Windows' language). The one
/// exception is a fragment that is itself in <see cref="Templates"/> (<see cref="TheReleaseList"/>).
/// </para>
/// </remarks>
public static class UpdateMessages
{
    /// <summary>What a request for the release list is called inside a sentence ("… the request for the release list …").</summary>
    public const string TheReleaseList = "the release list";

    /// <summary>The release list as the subject that starts a sentence ("The release list is larger than expected …").</summary>
    public const string ReleaseListSubject = "The release list";

    /// <summary>The release list arrived but is not one (a parse failure).</summary>
    public const string ReleaseListUnreadable = "The release list from the server could not be read.";

    /// <summary>{0}: a package whose stated size is beyond any update.</summary>
    public const string PackageTooLarge = "{0} is larger than an update can be.";

    /// <summary>{0}: a package whose announced length disagrees with the release.</summary>
    public const string PackageSizeDiffers = "{0} has another size on the server than the release states.";

    /// <summary>{0}: a package that kept coming past its stated size.</summary>
    public const string PackageLargerThanStated = "{0} is larger on the server than the release states.";

    /// <summary>{0}: the package; {1}, {2}: bytes received and expected (grouped digits).</summary>
    public const string DownloadEndedEarly = "The download of {0} ended early ({1:N0} of {2:N0} bytes).";

    /// <summary>{0}: a package whose download address is not one of the project's release downloads.</summary>
    public const string UntrustedAddress = "The release list names a download address for {0} that is not one of this project's release downloads, so it was not used.";

    /// <summary>{0}: the local time the rate limit ends (short time pattern).</summary>
    public const string RateLimitedUntil = "GitHub limits how often this network address may ask for releases. Try again after {0}.";

    /// <summary>A rate limit whose end GitHub did not say.</summary>
    public const string RateLimited = "GitHub limits how often this network address may ask for releases. Try again later.";

    /// <summary>{0}: what was asked for; {1}: the HTTP status (500 or above).</summary>
    public const string ServerUnavailable = "The server could not answer the request for {0} right now (error {1}).";

    /// <summary>{0}: what was asked for; {1}: the HTTP status (below 500).</summary>
    public const string ServerRefused = "The server refused the request for {0} (error {1}).";

    /// <summary>{0}: what was being read (<see cref="ReleaseListSubject"/> or a file name).</summary>
    public const string TooLargeToRead = "{0} is larger than expected and was not read.";

    /// <summary>{0}: what was asked for.</summary>
    public const string ServerTimedOut = "The server did not answer in time ({0}).";

    /// <summary>{0}: the network stack's innermost reason, without its final period.</summary>
    public const string ServerUnreachable = "The server could not be reached ({0}).";

    /// <summary>{0}: the file system's reason (in Windows' language).</summary>
    public const string NotSaved = "The update could not be saved on this PC: {0}";

    /// <summary>{0}: the package the checksum list does not list.</summary>
    public const string ChecksumMissing = "The release's checksum list has no entry for {0}, so the update cannot be verified. Nothing was installed.";

    /// <summary>{0}: the package whose two checksums disagree.</summary>
    public const string ChecksumsDisagree = "The release's two checksums for {0} disagree, so the update cannot be trusted. Nothing was installed.";

    /// <summary>The download was complete but its hash is not the release's.</summary>
    public const string DownloadMismatch = "The download does not match the release's checksum, so it was deleted. Nothing was installed. Try again; if it happens again, download the release from GitHub.";

    /// <summary>A bug inside a check.</summary>
    public const string CheckFailedUnexpectedly = "The check for updates failed unexpectedly. The log has the details.";

    /// <summary>{0}: the installer's time limit in minutes; {1}: its log.</summary>
    public const string InstallerTimedOut = "The installer did not finish in {0:0} minutes. Details: {1}";

    /// <summary>A bug inside an installation.</summary>
    public const string UpdateFailedUnexpectedly = "The update failed unexpectedly. The installed version is unchanged. The log has the details.";

    /// <summary>{0}: the installer's own report (English: it is a PowerShell script's).</summary>
    public const string InstallerReported = "The installer could not update BetterClipboard: {0}";

    /// <summary>{0}: the installer's exit code; {1}: its log.</summary>
    public const string InstallerEnded = "The installer ended without updating BetterClipboard (code {0}). The installed version is unchanged. Details: {1}";

    /// <summary>{0}: the installer's exit code (it never ran its first line: no log).</summary>
    public const string InstallerNeverStarted = "The installer ended before it could start (code {0}), so the installed version is unchanged. If this PC blocks PowerShell scripts, get the new version from the download page.";

    /// <summary>
    /// Every template above, for translating a finished message (and the release list's two names, which the messages
    /// carry inside them).
    /// </summary>
    public static IReadOnlyList<string> Templates { get; } =
    [
        ReleaseListUnreadable, PackageTooLarge, PackageSizeDiffers, PackageLargerThanStated, DownloadEndedEarly, UntrustedAddress,
        RateLimitedUntil, RateLimited, ServerUnavailable, ServerRefused, TooLargeToRead, ServerTimedOut, ServerUnreachable, NotSaved,
        ChecksumMissing, ChecksumsDisagree, DownloadMismatch, CheckFailedUnexpectedly, InstallerTimedOut, UpdateFailedUnexpectedly,
        InstallerReported, InstallerEnded, InstallerNeverStarted, TheReleaseList, ReleaseListSubject,
    ];

    /// <summary>
    /// Words a message from one of the templates, in English, with numbers formatted the way the messages always were
    /// (the current culture, like the string interpolation it replaces).
    /// </summary>
    /// <param name="template">One of the constants above.</param>
    /// <param name="arguments">Its placeholders' values.</param>
    /// <returns>The message.</returns>
    /// <exception cref="FormatException">The template and the arguments do not fit (a bug the tests catch).</exception>
    public static string Format(string template, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, template, arguments);
}
