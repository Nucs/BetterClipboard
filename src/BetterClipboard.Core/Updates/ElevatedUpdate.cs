namespace BetterClipboard.Core.Updates;

/// <summary>
/// The rule of an update that needs administrator rights: a copy the installer put into a folder only administrators may
/// change — Program Files, where a copy with Windows' UI access must live (<c>install.ps1 -UIAccess</c>, CLAUDE.md §2.63) —
/// is replaced by the new release's <c>install.ps1 -Update</c> started as administrator, after one consent prompt.
/// </summary>
/// <remarks>
/// <para>
/// <b>The command line is the same contract</b> (CLAUDE.md §2.29): only the way the installer is started differs (the shell's
/// <c>runas</c> verb instead of <c>CreateProcess</c>), so every release whose installer knows <c>-Update</c> could run it.
/// </para>
/// <para>
/// <b>Only offers that know how to run elevated.</b> An installer started as administrator starts BetterClipboard again
/// itself, and an installer from before <see cref="MinimumVersion"/> starts it directly: the app would then run as
/// administrator, and a copy with UI access would lose it (its new program carries no signature). From that version on,
/// <c>install.ps1 -Update</c> starts the app through Explorer with the user's normal rights and signs a copy with UI access
/// again. Such an older offer — the way back from a development build to an older release — is refused with a message
/// that names the installer command instead (<see cref="Accepts"/>).
/// </para>
/// </remarks>
public static class ElevatedUpdate
{
    /// <summary>
    /// The first version whose <c>install.ps1 -Update</c> may run as administrator (see the class remarks): the development
    /// build that brought UI access. Footgun: every release from 0.2.12 on is newer; a development build of 0.2.12 is newer
    /// only from this commit count on, which is this change's own.
    /// </summary>
    public static readonly SemanticVersion MinimumVersion = new(0, 2, 12, "dev.65");

    /// <summary>Whether an update to <paramref name="offered"/> may run its installer as administrator.</summary>
    /// <param name="offered">The version to install.</param>
    /// <returns><see langword="true"/> from <see cref="MinimumVersion"/> on.</returns>
    public static bool Accepts(SemanticVersion offered) => offered >= MinimumVersion;
}
