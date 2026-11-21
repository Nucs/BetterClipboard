namespace BetterClipboard.Windows.Interop;

/// <summary>
/// Declarations for following the foreground window while the panel stays open after pasting
/// (<see cref="Input.ForegroundTracker"/>, CLAUDE.md §2.55). The WinEvent functions themselves — <c>SetWinEventHook</c>,
/// <c>UnhookWinEvent</c>, the <c>WinEventProc</c> callback and the <c>WINEVENT_*</c> flags — are declared in
/// NativeMethods.NotepadPlusPlus.cs, where they were first needed.
/// </summary>
internal static partial class NativeMethods
{
    /// <summary>
    /// <c>EVENT_SYSTEM_FOREGROUND</c>: the foreground window changed; the event's window is the new one. Raised by Windows
    /// itself for every top-level window that takes the foreground, whatever process owns it (an elevated one included).
    /// </summary>
    internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003;

    /// <summary><c>OBJID_WINDOW</c>: the event is about the window itself, not one of its parts.</summary>
    internal const int OBJID_WINDOW = 0;
}
