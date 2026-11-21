using System.Runtime.InteropServices;

namespace BetterClipboard.Windows.Interop;

/// <summary>
/// Declarations for running with Windows' UI access (<see cref="Input.UiAccess"/>, CLAUDE.md §2.63): reading the token's
/// UI-access flag, lowering the process's integrity level, and starting a program through the shell, the only way to
/// start one that asks for UI access.
/// </summary>
internal static partial class NativeMethods
{
    /// <summary><c>TOKEN_ADJUST_DEFAULT</c>: change a token's default information, its integrity level among it.</summary>
    internal const uint TOKEN_ADJUST_DEFAULT = 0x0080;

    /// <summary>
    /// <c>TokenUIAccess</c>: the <c>TOKEN_INFORMATION_CLASS</c> value for a DWORD that is non-zero when Windows started the
    /// process with UI access (a signed program in a secure folder whose manifest asks for it).
    /// </summary>
    internal const int TokenUIAccess = 26;

    /// <summary><c>TokenElevationType</c>: the class for a DWORD, 1 default, 2 full (elevated), 3 limited (an administrator with UAC on).</summary>
    internal const int TokenElevationTypeClass = 18;

    /// <summary><c>TokenElevationTypeLimited</c>: the filtered token of an administrator while UAC is on; such a user can elevate with consent.</summary>
    internal const int TokenElevationTypeLimited = 3;

    /// <summary><c>SE_GROUP_INTEGRITY</c>: the attribute a mandatory label's SID carries in a <c>TOKEN_MANDATORY_LABEL</c>.</summary>
    internal const uint SE_GROUP_INTEGRITY = 0x00000020;

    /// <summary>
    /// <c>ERROR_ELEVATION_REQUIRED</c> (740): <c>CreateProcess</c>'s answer for a program whose manifest asks for UI access
    /// (or administrator rights) — measured on Windows 11 26200 also from a process that has UI access itself, and from an
    /// elevated one. Only the shell (and through it the AppInfo service) starts such a program.
    /// </summary>
    internal const int ERROR_ELEVATION_REQUIRED = 740;

    /// <summary><c>SEE_MASK_NOCLOSEPROCESS</c>: <c>ShellExecuteEx</c> hands back the started process's handle in <c>hProcess</c>.</summary>
    internal const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;

    /// <summary>
    /// <c>SYNCHRONIZE</c>: the right to wait on an object - for a process, on its end. Windows grants it to a process of a
    /// lower integrity level too, unlike the rights <see cref="System.Diagnostics.Process.Handle"/> asks for (all of them).
    /// </summary>
    internal const uint SYNCHRONIZE = 0x00100000;

    /// <summary>
    /// Sets one class of token information. Used here for <see cref="TokenIntegrityLevel"/> only, to lower this process's
    /// own level: lowering needs no privilege, raising would need one.
    /// </summary>
    /// <param name="tokenHandle">A token opened with <see cref="TOKEN_ADJUST_DEFAULT"/>.</param>
    /// <param name="tokenInformationClass">The class, e.g. <see cref="TokenIntegrityLevel"/>.</param>
    /// <param name="tokenInformation">The new value (for the level, a <c>TOKEN_MANDATORY_LABEL</c> followed by its SID).</param>
    /// <param name="tokenInformationLength">Its size in bytes.</param>
    /// <returns><see langword="true"/> on success; otherwise read the last error.</returns>
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetTokenInformation(nint tokenHandle, int tokenInformationClass, nint tokenInformation, int tokenInformationLength);

    /// <summary>The id of the process a handle refers to.</summary>
    /// <param name="process">A process handle (from <see cref="SEE_MASK_NOCLOSEPROCESS"/>).</param>
    /// <returns>The id, or 0 when the handle is not a process's.</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint GetProcessId(nint process);
}
