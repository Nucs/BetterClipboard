using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Input;

/// <summary>
/// Whether the user is busy right now (CLAUDE.md §2.46): the silent restart waits for a short pause in keyboard and mouse
/// input, and never runs while a modifier key or a mouse button is held.
/// </summary>
public static class UserActivity
{
    /// <summary>Modifier keys (a shortcut may be on its way) and mouse buttons (a drag may be in progress).</summary>
    private static readonly int[] HeldKeys =
    [
        0x10, // VK_SHIFT
        0x11, // VK_CONTROL
        0x12, // VK_MENU (Alt)
        0x5B, // VK_LWIN
        0x5C, // VK_RWIN
        0x01, // VK_LBUTTON
        0x02, // VK_RBUTTON
        0x04, // VK_MBUTTON
    ];

    /// <summary>
    /// How long ago the session last received keyboard or mouse input (from any program, injected input included).
    /// </summary>
    /// <returns>The time since the last input; <see cref="TimeSpan.Zero"/> when Windows cannot tell, so a caller that waits
    /// for a pause keeps waiting rather than act during input.</returns>
    public static TimeSpan SinceLastInput()
    {
        var info = new LASTINPUTINFO { cbSize = 8 };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        // Both are GetTickCount values: the unsigned difference stays right across the 49.7-day wrap.
        uint elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    /// <summary>Whether a modifier key (Shift, Ctrl, Alt, Win) or a mouse button is down right now.</summary>
    /// <returns><see langword="true"/> while one is held. Reads <see langword="false"/> while the session is locked (no input
    /// desktop), where nothing can be held.</returns>
    public static bool AnyModifierOrButtonHeld() => HeldKeys.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);
}
