using BetterClipboard.Windows.Interop;

namespace BetterClipboard.Windows.Packaging;

/// <summary>
/// Asks Windows to start the app again after Windows itself closed it (CLAUDE.md §2.47): for the Microsoft Store
/// edition that is the moment the Store installs an update. Without it an update would leave the user without a
/// clipboard history until they sign in again or open the app by hand, and nothing would tell them.
/// </summary>
/// <remarks>
/// Microsoft's packaging guidance: an app that the Store updates while it runs is closed, and is restarted afterwards
/// only when it registered for a restart (<c>RegisterApplicationRestart</c>). Windows restarts only a process that ran
/// for at least 60 seconds, so an app that fails right after starting cannot loop.
/// </remarks>
public static class PackagedRestart
{
    /// <summary>
    /// Registers this process to be started again with <paramref name="arguments"/> after Windows closed it for an
    /// update. Not after a crash or a hang (a broken build must not be started over and over), and not after a restart
    /// of Windows (the app's sign-in start does that).
    /// </summary>
    /// <param name="arguments">
    /// The arguments for the restarted process, without the program (at most 1,024 characters), e.g. <c>--background</c>
    /// so it comes back in the tray; <see langword="null"/> for none.
    /// </param>
    /// <returns>
    /// The call's HRESULT: 0 when registered. A failure only means that the app stays closed after an update until its
    /// next start; callers log it and go on.
    /// </returns>
    public static int RegisterAfterUpdate(string? arguments) =>
        NativeMethods.RegisterApplicationRestart(arguments, NativeMethods.RESTART_NO_CRASH | NativeMethods.RESTART_NO_HANG | NativeMethods.RESTART_NO_REBOOT);
}
