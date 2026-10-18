namespace BetterClipboard.Core.Startup;

/// <summary>
/// What Windows holds for starting BetterClipboard at sign-in: the user's own "Start with Windows" entry and the one an
/// installation made for every user of the PC, each with Windows' own on/off mark from its startup apps list.
/// </summary>
/// <param name="UserCommand">
/// The command of the user's entry (the <c>BetterClipboard</c> value of <c>HKCU\…\Run</c>), or <see langword="null"/>
/// when there is none.
/// </param>
/// <param name="UserTargetExists">
/// Whether the program the user's entry names exists. An entry whose program is gone (moved, uninstalled) starts nothing,
/// so it does not stand in the all-users entry's way.
/// </param>
/// <param name="UserDisabledInWindows">
/// Whether Windows' startup apps list (Task Manager, Settings › Apps › Startup) has the user's entry turned off. Windows
/// keeps that mark under the value's name, also while the value itself is missing.
/// </param>
/// <param name="AllUsersCommand">
/// The command of the all-users entry (the <c>BetterClipboard</c> value of <c>HKLM\…\Run</c> in the 64-bit view), or
/// <see langword="null"/> when there is none.
/// </param>
/// <param name="AllUsersDisabledInWindows">
/// Whether an administrator turned the all-users entry off in Windows' startup apps list. Windows keeps that mark for the
/// whole PC (<c>HKLM\…\StartupApproved\Run</c>), never per user, which is why a user's own "off" for the all-users entry
/// lives in BetterClipboard's settings instead (<see cref="SignInStartup"/>).
/// </param>
public sealed record SignInEntries(
    string? UserCommand,
    bool UserTargetExists,
    bool UserDisabledInWindows,
    string? AllUsersCommand,
    bool AllUsersDisabledInWindows)
{
    /// <summary>
    /// No entry anywhere. A registry that cannot be read is taken for this, so the switch then shows "off" and turning it
    /// on writes the user's own entry, which always works without administrator rights.
    /// </summary>
    public static SignInEntries None { get; } = new(null, false, false, null, false);
}

/// <summary>What one flip of the "Start with Windows" switch does to the user's own entry.</summary>
public enum UserEntryChange
{
    /// <summary>Leave it as it is.</summary>
    Keep = 0,

    /// <summary>Write it so that it starts this copy (<see cref="SignInStartup.UserCommandFor"/>), replacing what it held.</summary>
    Write = 1,

    /// <summary>Delete it.</summary>
    Remove = 2,
}

/// <summary>What one flip of the "Start with Windows" switch changes (<see cref="SignInStartup.Plan"/>).</summary>
/// <param name="UserEntry">What happens to the user's own entry.</param>
/// <param name="ClearUserDisabled">
/// Whether Windows' "off" mark for the user's entry must be removed. A value written again keeps the mark Windows holds
/// under its name, and Windows would go on skipping it although the switch says "on".
/// </param>
/// <param name="SkipAllUsers">The new value of <c>AppSettings.SkipAllUsersStartup</c>.</param>
public sealed record SignInChange(UserEntryChange UserEntry, bool ClearUserDisabled, bool SkipAllUsers);

/// <summary>
/// The rules of "Start with Windows" when two entries can start BetterClipboard at sign-in: the user's own and the one an
/// installation made for every user of the PC.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two entries.</b> The user's entry is a <c>Run</c> value in the user's own registry (<c>HKCU</c>). It is written
/// by <c>install.ps1</c> for the user who installs, by the Chocolatey package for the signed-in user who installs it, and
/// by this switch. The all-users entry is the same value in the PC's registry (<c>HKLM</c>), written by an installation
/// that is not the signed-in user's own: <c>install.ps1 -AllUsers</c> (which a run as SYSTEM, or from another account's
/// elevated prompt on someone else's desktop, picks by itself) and the Chocolatey package when SYSTEM or another account
/// installs it, or with <c>/AllUsersStartup</c>. Windows starts the user's entries and the all-users entries at every
/// sign-in, so without a rule both would start BetterClipboard, and the second start would only reach the first.
/// </para>
/// <para>
/// <b>The user's own entry comes first.</b> The all-users entry carries <see cref="AllUsersSwitch"/>, and a process that it
/// starts ends at once, before it takes the single-instance lock, when the user has an entry of their own whose program
/// exists, enabled or not (<see cref="ShouldContinueFromAllUsersEntry"/>). So one BetterClipboard starts, the one the
/// user chose, and an "off" in Windows' startup apps list for the user's entry still means off. Deciding before the lock
/// matters: a process that took the lock and then ended could have turned the user's own start away as "already
/// running".
/// </para>
/// <para>
/// <b>A user's "off" for the all-users entry</b> is kept in BetterClipboard's settings
/// (<c>AppSettings.SkipAllUsersStartup</c>) and honored by that early end. Windows keeps its on/off mark for an all-users
/// entry for the whole PC only, and only an administrator may change it: on the development PC (2026-10-18) every
/// <c>HKLM\…\Run</c> value had its mark in <c>HKLM\…\StartupApproved\Run</c> and none in the user's
/// <c>HKCU\…\StartupApproved\Run</c>.
/// </para>
/// <para>Pure (no registry, no files): the Windows layer reads the facts into <see cref="SignInEntries"/> and applies a <see cref="SignInChange"/>.</para>
/// </remarks>
public static class SignInStartup
{
    /// <summary>The switch that starts BetterClipboard into the notification area without a window; every sign-in entry has it.</summary>
    public const string BackgroundSwitch = "--background";

    /// <summary>
    /// The switch the all-users entry adds after <see cref="BackgroundSwitch"/>. A process started with it first asks
    /// <see cref="ShouldContinueFromAllUsersEntry"/> whether this user wants it. Changing the text orphans the entries that
    /// installed copies wrote: their processes would no longer recognize themselves and would ignore the user's "off".
    /// </summary>
    public const string AllUsersSwitch = "--all-users-startup";

    /// <summary>
    /// The user's own entry for a program: the format the app's switch, <c>install.ps1</c> and the Chocolatey package all
    /// write, so each of them recognizes the others' entry.
    /// </summary>
    /// <param name="executablePath">Full path of <c>BetterClipboard.exe</c>.</param>
    /// <returns><c>"&lt;exe&gt;" --background</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="executablePath"/> is null or blank.</exception>
    public static string UserCommandFor(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return $"\"{executablePath}\" {BackgroundSwitch}";
    }

    /// <summary>
    /// The all-users entry for a program, in the format both installers write.
    /// </summary>
    /// <param name="executablePath">Full path of <c>BetterClipboard.exe</c> in a folder every user may read.</param>
    /// <returns><c>"&lt;exe&gt;" --background --all-users-startup</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="executablePath"/> is null or blank.</exception>
    public static string AllUsersCommandFor(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return $"\"{executablePath}\" {BackgroundSwitch} {AllUsersSwitch}";
    }

    /// <summary>
    /// Whether an entry's command starts the program at <paramref name="executablePath"/>: the path appears in it, in any
    /// letter case. The rule the app's switch always used, so an entry an older version wrote still counts.
    /// </summary>
    /// <param name="command">The entry's command, or <see langword="null"/> for no entry.</param>
    /// <param name="executablePath">Full path of the running <c>BetterClipboard.exe</c>.</param>
    /// <returns><see langword="true"/> when the entry starts this program.</returns>
    public static bool Names(string? command, string executablePath) =>
        !string.IsNullOrWhiteSpace(command) && !string.IsNullOrWhiteSpace(executablePath) &&
        command.Contains(executablePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The program an entry's command starts: the quoted path when the command starts with a quote (the format every
    /// BetterClipboard writer uses), else everything up to and including the first <c>.exe</c>, else the first word.
    /// </summary>
    /// <param name="command">The entry's command, or <see langword="null"/>.</param>
    /// <returns>The program's path as written (environment variables not expanded), or <see langword="null"/> for none.</returns>
    public static string? TargetOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var text = command.Trim();
        if (text[0] == '"')
        {
            // An unterminated quote names nothing that could be started.
            int end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        // Unquoted, as a hand-made entry may be: Windows itself splits at the first ".exe" for a path with spaces.
        int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0)
        {
            return text[..(exe + 4)];
        }

        int space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }

    /// <summary>
    /// Whether this copy starts when this user signs in: what the "Start with Windows" switch shows.
    /// </summary>
    /// <param name="entries">The entries Windows holds.</param>
    /// <param name="executablePath">Full path of the running <c>BetterClipboard.exe</c>.</param>
    /// <param name="skipAllUsers">This user's <c>AppSettings.SkipAllUsersStartup</c>.</param>
    /// <returns>
    /// When the user has an entry whose program exists, whether that entry starts this copy and Windows has it on;
    /// otherwise whether the all-users entry starts this copy, Windows has it on, and the user did not turn it off here.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="executablePath"/> is null or blank.</exception>
    public static bool StartsThisCopy(SignInEntries entries, string executablePath, bool skipAllUsers)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (HasLiveUserEntry(entries))
        {
            // The user's entry decides alone: a process the all-users entry starts steps aside for it.
            return Names(entries.UserCommand, executablePath) && !entries.UserDisabledInWindows;
        }

        return Names(entries.AllUsersCommand, executablePath) && !entries.AllUsersDisabledInWindows && !skipAllUsers;
    }

    /// <summary>
    /// Whether a BetterClipboard that the all-users entry started (<see cref="AllUsersSwitch"/>) should go on starting.
    /// </summary>
    /// <remarks>
    /// Asked before the single-instance lock is taken, so a process that steps aside never holds the lock that the user's
    /// own start needs. Windows' mark for the all-users entry is not consulted: Windows started the process, so the entry
    /// is on.
    /// </remarks>
    /// <param name="entries">The entries Windows holds.</param>
    /// <param name="skipAllUsers">This user's <c>AppSettings.SkipAllUsersStartup</c>.</param>
    /// <returns>
    /// <see langword="false"/> when this user turned it off here, or has an entry of their own whose program exists
    /// (that entry starts BetterClipboard, or the user turned it off in Windows' list); otherwise <see langword="true"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    public static bool ShouldContinueFromAllUsersEntry(SignInEntries entries, bool skipAllUsers)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return !skipAllUsers && !HasLiveUserEntry(entries);
    }

    /// <summary>
    /// What turning the "Start with Windows" switch on or off changes, so that afterwards
    /// <see cref="StartsThisCopy"/> answers <paramref name="enable"/> (unless an administrator's mark on the all-users
    /// entry stands in the way of "on", which the user's own entry then covers).
    /// </summary>
    /// <param name="enable">The switch's new position.</param>
    /// <param name="entries">The entries Windows holds now.</param>
    /// <param name="executablePath">Full path of the running <c>BetterClipboard.exe</c>.</param>
    /// <param name="skipAllUsers">This user's <c>AppSettings.SkipAllUsersStartup</c> now.</param>
    /// <returns>
    /// Off: the user's entry for this copy is removed, and the all-users entry is declined for this user when it starts
    /// this copy (also while an administrator has it turned off: it would start this user again once turned back on).
    /// On: the all-users entry is used when it starts this copy and Windows has it on — the user's entry is then only
    /// cleaned up (a stale one removed, an "off" mark on one for this copy cleared) — otherwise the user's own entry is
    /// written for this copy, as the switch always did.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="executablePath"/> is null or blank.</exception>
    public static SignInChange Plan(bool enable, SignInEntries entries, string executablePath, bool skipAllUsers)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        bool allUsersStartsThis = Names(entries.AllUsersCommand, executablePath);
        if (!enable)
        {
            // An entry for another copy is that copy's: the switch of this one never showed it as "on".
            var user = Names(entries.UserCommand, executablePath) ? UserEntryChange.Remove : UserEntryChange.Keep;
            return new SignInChange(user, ClearUserDisabled: false, SkipAllUsers: skipAllUsers || allUsersStartsThis);
        }

        if (allUsersStartsThis && !entries.AllUsersDisabledInWindows)
        {
            if (entries.UserCommand is null)
            {
                return new SignInChange(UserEntryChange.Keep, ClearUserDisabled: false, SkipAllUsers: false);
            }

            if (!entries.UserTargetExists)
            {
                // A stale entry starts nothing and only makes Windows try; with it gone the all-users entry does the work.
                return new SignInChange(UserEntryChange.Remove, ClearUserDisabled: false, SkipAllUsers: false);
            }

            if (Names(entries.UserCommand, executablePath))
            {
                // The user's entry for this copy wins over the all-users one anyway; it only must not stay "off".
                return new SignInChange(UserEntryChange.Keep, ClearUserDisabled: entries.UserDisabledInWindows, SkipAllUsers: false);
            }
        }

        // No usable all-users entry, or the user's entry starts another copy, which would win: this copy's own entry.
        return new SignInChange(UserEntryChange.Write, ClearUserDisabled: entries.UserDisabledInWindows, SkipAllUsers: false);
    }

    /// <summary>
    /// The Settings card's description line under "Start with Windows": what the switch does, plus a sentence when an
    /// installation set BetterClipboard up for every user of the PC, so that "off" does not read as "off for everyone".
    /// </summary>
    /// <param name="entries">The entries Windows holds.</param>
    /// <param name="executablePath">Full path of the running <c>BetterClipboard.exe</c>.</param>
    /// <returns>One or two sentences.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    public static string Describe(SignInEntries entries, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(entries);
        const string Plain = "Start quietly in the notification area when you sign in.";
        if (!Names(entries.AllUsersCommand, executablePath))
        {
            return Plain;
        }

        return entries.AllUsersDisabledInWindows
            ? $"{Plain} Its entry for every user of this PC is turned off in Windows' startup apps, so turning this on adds one just for you."
            : $"{Plain} It is set up to start for every user of this PC; turning this off changes that only for you.";
    }

    /// <summary>Whether the user has an entry of their own whose program exists: one that Windows will try to start.</summary>
    /// <param name="entries">The entries Windows holds.</param>
    /// <returns><see langword="true"/> for such an entry, enabled in Windows' list or not.</returns>
    private static bool HasLiveUserEntry(SignInEntries entries) =>
        !string.IsNullOrWhiteSpace(entries.UserCommand) && entries.UserTargetExists;
}
