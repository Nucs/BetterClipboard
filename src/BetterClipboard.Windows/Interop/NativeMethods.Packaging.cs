using System.Runtime.InteropServices;

namespace BetterClipboard.Windows.Interop;

/// <summary>Declarations the Microsoft Store edition needs (CLAUDE.md §2.47).</summary>
internal static partial class NativeMethods
{
    /// <summary><c>RESTART_NO_CRASH</c>: do not restart the process after a crash.</summary>
    internal const uint RESTART_NO_CRASH = 1;

    /// <summary><c>RESTART_NO_HANG</c>: do not restart the process after it was ended for hanging.</summary>
    internal const uint RESTART_NO_HANG = 2;

    /// <summary><c>RESTART_NO_REBOOT</c>: do not restart the process after a reboot for an update of Windows.</summary>
    internal const uint RESTART_NO_REBOOT = 8;

    /// <summary>
    /// Registers the process to be started again by Windows, with a command line, after Windows closed it: for a
    /// packaged app that is when the Microsoft Store installs its update.
    /// </summary>
    /// <param name="pwzCommandline">The arguments for the restart (not the program), at most 1,024 characters; <see langword="null"/> for none.</param>
    /// <param name="dwFlags"><c>RESTART_NO_*</c> flags naming the cases in which it must not be restarted.</param>
    /// <returns>An HRESULT; 0 on success.</returns>
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegisterApplicationRestart(string? pwzCommandline, uint dwFlags);
}
