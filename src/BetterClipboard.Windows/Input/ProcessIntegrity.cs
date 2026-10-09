using System.Runtime.InteropServices;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Input;

/// <summary>
/// Integrity levels of processes — Windows' mandatory label: Low 0x1000, Medium 0x2000 (a normal app, also an
/// administrator's with UAC on), High 0x3000 (run as administrator), System 0x4000 — to know before typing into a window
/// whether Windows lets BetterClipboard do it.
/// </summary>
/// <remarks>
/// User Interface Privilege Isolation (UIPI) drops input that a process injects into a window of a higher level, and it does
/// so silently: <c>SendInput</c> still reports every event as injected (Microsoft documents that neither its return value nor
/// <c>GetLastError</c> shows a UIPI block). So a paste into an elevated terminal looked successful and did nothing — found in
/// the QA pass of 2026-10-09 on a Windows 11 VM with UAC on. Comparing the levels first is the only reliable check.
/// </remarks>
public static class ProcessIntegrity
{
    /// <summary>The level of a normal app (and of an administrator's apps while UAC is on).</summary>
    public const int Medium = 0x2000;

    /// <summary>The level of an elevated app ("Run as administrator").</summary>
    public const int High = 0x3000;

    /// <summary>This process's level, read once (it cannot change while the process runs).</summary>
    private static readonly Lazy<int?> CurrentLevel = new(() => OfProcessHandle(GetCurrentProcess()));

    /// <summary>The integrity level of this process, or <see langword="null"/> when it cannot be read.</summary>
    public static int? Current => CurrentLevel.Value;

    /// <summary>
    /// The integrity level of a process.
    /// </summary>
    /// <param name="processId">The process id.</param>
    /// <returns>Its level (e.g. <see cref="High"/>), or <see langword="null"/> when the process is gone or may not be queried.</returns>
    public static int? OfProcess(uint processId)
    {
        // PROCESS_QUERY_LIMITED_INFORMATION is granted for elevated processes of the same user too; reading the token's
        // label is a read, which the token's no-write-up policy allows from a lower level.
        nint process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            return OfProcessHandle(process);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>
    /// The integrity level of the process that owns a window.
    /// </summary>
    /// <param name="window">A window handle.</param>
    /// <returns>The owner's level, or <see langword="null"/> when the window is gone or its process may not be queried.</returns>
    public static int? OfWindow(nint window)
    {
        if (window == 0)
        {
            return null;
        }

        GetWindowThreadProcessId(window, out uint processId);
        return processId == 0 ? null : OfProcess(processId);
    }

    /// <summary>
    /// Whether keys BetterClipboard injects would be dropped by UIPI for <paramref name="window"/>: its process runs at a
    /// higher level than this one (typically: it runs as administrator, BetterClipboard does not).
    /// </summary>
    /// <param name="window">The target window.</param>
    /// <returns><see langword="true"/> only when both levels are known and the window's is higher.</returns>
    public static bool IsAboveCurrent(nint window) => OfWindow(window) is { } target && Current is { } own && target > own;

    /// <summary>Reads the integrity level from a process handle's token.</summary>
    /// <param name="process">A process handle with query access (or the current-process pseudo-handle).</param>
    /// <returns>The level, or <see langword="null"/> when the token cannot be read.</returns>
    private static int? OfProcessHandle(nint process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out nint token))
        {
            return null;
        }

        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, 0, 0, out int size);
            if (size <= 0)
            {
                return null;
            }

            nint buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _))
                {
                    return null;
                }

                // TOKEN_MANDATORY_LABEL starts with SID_AND_ATTRIBUTES, whose first member is the label's SID; the level is
                // its last sub-authority (S-1-16-12288 = High).
                nint sid = Marshal.ReadIntPtr(buffer);
                int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return count == 0 ? null : Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
