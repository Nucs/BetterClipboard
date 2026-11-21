using System.Runtime.InteropServices;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Input;

/// <summary>
/// Windows' UI access, which a copy installed with <c>install.ps1 -UIAccess</c> runs with (CLAUDE.md §2.63): its windows that
/// stay on top live in a layer of Windows above Start and Search, so the panel opens over Start the way Windows' own Win+V
/// panel does.
/// </summary>
/// <remarks>
/// <para>
/// <b>What Windows grants.</b> A program whose manifest asks for <c>uiAccess="true"</c>, that lies in a secure folder
/// (Program Files) and carries a signature this PC trusts, is started by the AppInfo service with UI access. Measured on
/// Windows 11 26200 (2026-11-21, a test desktop with UAC on): its always-on-top windows are in the UI-access z-band
/// (<see cref="ShellSurfaces.UiAccessBand"/>), which Windows draws above Start and Search; its other windows stay with
/// the app windows. For an administrator's account the token comes with the High integrity level, not Medium.
/// </para>
/// <para>
/// <b>Why the level is lowered at once</b> (<see cref="LowerIntegrityIfGranted"/>, the first thing <c>Program.Main</c>
/// does). At High, every program BetterClipboard starts — a Win+R command run again, a file opened, the browser for a link
/// — inherited both the High level and UI access (measured: Notepad started that way had both), and every named object the
/// app creates (its command events, <c>bclip</c>'s pipe) would carry a High label that ordinary programs may not write to.
/// Lowered to Medium, the process behaves like any app of the user again — and, measured, its always-on-top windows stay
/// in the UI-access band, also those made after the change. Windows clears the token's UI-access flag with the change, so
/// the input privilege that comes with UI access (typing into programs that run as administrator) is not kept: a paste into
/// such a window stays a copy, as without UI access (CLAUDE.md §2.5).
/// </para>
/// <para>
/// <b>Starting the program again.</b> <c>CreateProcess</c> refuses a program that asks for UI access (740,
/// <see cref="ERROR_ELEVATION_REQUIRED"/>), from any process. Whatever must run <i>with</i> UI access (the silent restart)
/// goes through the shell (<see cref="Shell.QuietProcess.StartThroughShell"/>); whatever only needs the same program without
/// it (the Cmd tab's helper) is started with <see cref="CompatibilityLayerVariable"/> = <see cref="RunAsInvoker"/>, which
/// makes Windows ignore the manifest's request (measured: the helper then started with neither UI access nor the High level).
/// </para>
/// </remarks>
public static class UiAccess
{
    /// <summary>
    /// The environment variable through which Windows' application compatibility layer is asked for a shim
    /// (<c>__COMPAT_LAYER</c>): with <see cref="RunAsInvoker"/>, <c>CreateProcess</c> starts a program whose manifest asks for
    /// UI access as a plain program of the caller's level instead of refusing it.
    /// </summary>
    public const string CompatibilityLayerVariable = "__COMPAT_LAYER";

    /// <summary>The shim that runs a program with its caller's rights, ignoring the manifest's request (<see cref="CompatibilityLayerVariable"/>).</summary>
    public const string RunAsInvoker = "RunAsInvoker";

    /// <summary>The Medium mandatory label's SID, S-1-16-8192, as bytes: revision 1, one sub-authority, authority 16, 0x2000.</summary>
    private static readonly byte[] MediumLabelSid = [1, 1, 0, 0, 0, 0, 0, 16, 0x00, 0x20, 0x00, 0x00];

    /// <summary>
    /// What <see cref="LowerIntegrityIfGranted"/> found and did at this process's start; <see langword="null"/> before it ran
    /// (tests, and every process whose <c>Main</c> is not BetterClipboard's).
    /// </summary>
    public static UiAccessStart? AtStart { get; private set; }

    /// <summary>
    /// Whether this process was started with UI access, so its always-on-top windows stand above Start and Search, and a
    /// program it starts from its own file must go through the shell or the compatibility layer (see the class remarks).
    /// </summary>
    public static bool StartedWithUiAccess => AtStart?.HadUiAccess == true;

    /// <summary>
    /// Reads whether Windows gave this process UI access and, when it did and the integrity level is above Medium, lowers
    /// the level to Medium (see the class remarks); records the outcome in <see cref="AtStart"/>. Called once, first thing in
    /// <c>Program.Main</c>, before anything creates a window, a named object or a file. Never throws.
    /// </summary>
    /// <returns>The outcome (also kept in <see cref="AtStart"/>).</returns>
    public static UiAccessStart LowerIntegrityIfGranted()
    {
        var start = Lower();
        AtStart = start;
        return start;
    }

    /// <summary>The work of <see cref="LowerIntegrityIfGranted"/>.</summary>
    /// <returns>The outcome.</returns>
    private static UiAccessStart Lower()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_ADJUST_DEFAULT, out nint token))
        {
            return new UiAccessStart(false, null, null, null, Marshal.GetLastPInvokeError());
        }

        try
        {
            bool hadUiAccess = ReadDword(token, TokenUIAccess) is > 0;
            int? before = ProcessIntegrity.OfToken(token);
            if (!hadUiAccess || before is null or <= ProcessIntegrity.Medium)
            {
                // Nothing to lower: a normal start, or UI access at Medium already (a standard user's account).
                return new UiAccessStart(hadUiAccess, before, before, hadUiAccess, null);
            }

            int? error = SetMedium(token) ? null : Marshal.GetLastPInvokeError();
            return new UiAccessStart(true, before, ProcessIntegrity.OfToken(token), ReadDword(token, TokenUIAccess) is > 0, error);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>Whether the current user is an administrator who can elevate with consent (a split token while UAC is on).</summary>
    /// <remarks>
    /// The updater asks it before it runs an installer as administrator (<c>ScriptUpdateInstaller</c>): such a user sees one
    /// consent prompt; a standard user would be asked for another account's password, and that account's elevated installer
    /// would not be the one whose records and data folder the update belongs to.
    /// </remarks>
    /// <returns><see langword="true"/> for the limited token of an administrator; <see langword="false"/> otherwise or when it cannot be read.</returns>
    public static bool CanElevateWithConsent()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out nint token))
        {
            return false;
        }

        try
        {
            return ReadDword(token, TokenElevationTypeClass) == TokenElevationTypeLimited;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>Reads one DWORD-sized class of token information.</summary>
    /// <param name="token">A token opened with <see cref="TOKEN_QUERY"/>.</param>
    /// <param name="informationClass">The class (<see cref="TokenUIAccess"/>, <see cref="TokenElevationTypeClass"/>).</param>
    /// <returns>The value, or <see langword="null"/> when it cannot be read.</returns>
    private static int? ReadDword(nint token, int informationClass)
    {
        nint buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            return GetTokenInformation(token, informationClass, buffer, sizeof(int), out _) ? Marshal.ReadInt32(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Lowers the token's integrity level to Medium.</summary>
    /// <param name="token">This process's token, opened with <see cref="TOKEN_ADJUST_DEFAULT"/>.</param>
    /// <returns><see langword="true"/> when Windows took the change.</returns>
    private static bool SetMedium(nint token)
    {
        // TOKEN_MANDATORY_LABEL is SID_AND_ATTRIBUTES: a pointer to the SID and a DWORD of attributes, padded to two pointers
        // on 64-bit Windows. The SID itself follows in the same buffer, so the pointer stays valid during the call.
        int header = IntPtr.Size * 2;
        nint label = Marshal.AllocHGlobal(header + MediumLabelSid.Length);
        try
        {
            nint sid = label + header;
            Marshal.Copy(MediumLabelSid, 0, sid, MediumLabelSid.Length);
            Marshal.WriteIntPtr(label, sid);
            Marshal.WriteInt32(label + IntPtr.Size, unchecked((int)SE_GROUP_INTEGRITY));
            return SetTokenInformation(token, TokenIntegrityLevel, label, header + MediumLabelSid.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(label);
        }
    }
}

/// <summary>
/// What a process found and did about UI access at its start (<see cref="UiAccess.LowerIntegrityIfGranted"/>).
/// </summary>
/// <param name="HadUiAccess">Whether Windows started it with UI access.</param>
/// <param name="LevelBefore">Its integrity level at start (<see cref="ProcessIntegrity.Medium"/>, <see cref="ProcessIntegrity.High"/>), or <see langword="null"/> when unreadable.</param>
/// <param name="LevelAfter">Its level afterwards, or <see langword="null"/> when unreadable.</param>
/// <param name="UiAccessAfter">Whether the token still has UI access afterwards (Windows clears it when the level is lowered), or <see langword="null"/> when unreadable.</param>
/// <param name="Error">The Win32 error of a step that failed (opening the token, lowering the level), or <see langword="null"/>.</param>
public sealed record UiAccessStart(bool HadUiAccess, int? LevelBefore, int? LevelAfter, bool? UiAccessAfter, int? Error)
{
    /// <summary>
    /// One content-free log line about the start, or <see langword="null"/> when there is nothing to say (no UI access, and
    /// nothing failed).
    /// </summary>
    /// <returns>The line, or <see langword="null"/>.</returns>
    public string? Describe()
    {
        if (!HadUiAccess)
        {
            return Error is { } failed ? $"Could not read whether this copy runs with Windows' UI access (error {failed})." : null;
        }

        string levels = LevelBefore == LevelAfter
            ? $"at the {Name(LevelAfter)} integrity level"
            : $"its integrity level lowered from {Name(LevelBefore)} to {Name(LevelAfter)}, so programs it starts run as usual";
        string failure = Error is { } error ? $"; lowering the level failed (error {error}), so programs it starts get UI access and its level too" : string.Empty;
        return $"This copy runs with Windows' UI access: its panel can open above Start and Search ({levels}{failure}).";
    }

    /// <summary>A level's name for the log.</summary>
    /// <param name="level">The level, or <see langword="null"/>.</param>
    /// <returns>"Medium", "High", a hex value, or "unknown".</returns>
    private static string Name(int? level) => level switch
    {
        ProcessIntegrity.Medium => "Medium",
        ProcessIntegrity.High => "High",
        null => "unknown",
        _ => $"0x{level.Value:x}",
    };
}
