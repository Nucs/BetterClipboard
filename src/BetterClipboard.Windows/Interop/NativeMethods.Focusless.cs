using System.Runtime.InteropServices;

namespace BetterClipboard.Windows.Interop;

/// <summary>
/// Declarations for the panel that opens without taking the keyboard (<see cref="Input.FocuslessPanelInput"/>, CLAUDE.md
/// §2.59): the low-level mouse hook that sees a click outside the panel, the window under a point, and turning a key into the
/// characters it types with the keyboard layout of the app the user types in.
/// </summary>
internal static partial class NativeMethods
{
    /// <summary><c>WH_MOUSE_LL</c> hook id: every mouse event of the desktop, before any window gets it.</summary>
    internal const int WH_MOUSE_LL = 14;

    /// <summary><c>WM_LBUTTONDOWN</c>, as a low-level mouse hook reports a left press anywhere (client area or not).</summary>
    internal const uint WM_LBUTTONDOWN = 0x0201;

    /// <summary><c>WM_RBUTTONDOWN</c>: a right press.</summary>
    internal const uint WM_RBUTTONDOWN = 0x0204;

    /// <summary><c>WM_MBUTTONDOWN</c>: a middle press.</summary>
    internal const uint WM_MBUTTONDOWN = 0x0207;

    /// <summary><c>WM_XBUTTONDOWN</c>: a press of a side button (back, forward).</summary>
    internal const uint WM_XBUTTONDOWN = 0x020B;

    /// <summary>
    /// <c>EVENT_SYSTEM_DESKTOPSWITCH</c>: another desktop took the input — the lock screen, a UAC prompt on the secure
    /// desktop. A hook of this desktop sees none of the keys typed there.
    /// </summary>
    internal const uint EVENT_SYSTEM_DESKTOPSWITCH = 0x0020;

    /// <summary><c>VK_SHIFT</c>: either Shift key.</summary>
    internal const uint VK_SHIFT = 0x10;

    /// <summary><c>VK_CONTROL</c>: either Ctrl key.</summary>
    internal const uint VK_CONTROL = 0x11;

    /// <summary><c>VK_MENU</c>: either Alt key.</summary>
    internal const uint VK_MENU = 0x12;

    /// <summary><c>VK_CAPITAL</c>: Caps Lock; its toggle bit decides the case of letters.</summary>
    internal const uint VK_CAPITAL = 0x14;

    /// <summary><c>VK_LSHIFT</c>: the left Shift key, as a low-level hook reports it.</summary>
    internal const uint VK_LSHIFT = 0xA0;

    /// <summary><c>VK_RSHIFT</c>: the right Shift key.</summary>
    internal const uint VK_RSHIFT = 0xA1;

    /// <summary><c>VK_LCONTROL</c>: the left Ctrl key (also the one Windows adds for AltGr).</summary>
    internal const uint VK_LCONTROL = 0xA2;

    /// <summary><c>VK_RCONTROL</c>: the right Ctrl key.</summary>
    internal const uint VK_RCONTROL = 0xA3;

    /// <summary><c>VK_LMENU</c>: the left Alt key.</summary>
    internal const uint VK_LMENU = 0xA4;

    /// <summary><c>VK_RMENU</c>: the right Alt key (AltGr on layouts that have one).</summary>
    internal const uint VK_RMENU = 0xA5;

    /// <summary><c>VK_LWIN</c>: the left Windows key.</summary>
    internal const uint VK_LWIN = 0x5B;

    /// <summary><c>VK_RWIN</c>: the right Windows key.</summary>
    internal const uint VK_RWIN = 0x5C;

    /// <summary>Low-level mouse event data (<c>MSLLHOOKSTRUCT</c>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        /// <summary>The cursor's position in per-monitor-aware screen coordinates.</summary>
        public POINT pt;

        /// <summary>Wheel delta or which side button; unused here.</summary>
        public uint mouseData;

        /// <summary><c>LLMHF_*</c> flags (injected or not).</summary>
        public uint flags;

        /// <summary>Event time.</summary>
        public uint time;

        /// <summary>Extra info (set by injectors).</summary>
        public nuint dwExtraInfo;
    }

    /// <summary>The window at a screen point (visible, enabled, not transparent to clicks): where a click there goes.</summary>
    /// <param name="point">The point in screen coordinates.</param>
    /// <returns>The window, or 0 when none is there.</returns>
    [LibraryImport("user32.dll")]
    internal static partial nint WindowFromPoint(POINT point);

    /// <summary>
    /// The characters a key types with a keyboard layout and a key state. A dead key (a lone accent) answers −1 and is kept
    /// in the calling thread's keyboard state, so the next key comes back combined with it.
    /// </summary>
    /// <param name="wVirtKey">The virtual key.</param>
    /// <param name="wScanCode">Its scan code (bit 15 set for a key-up).</param>
    /// <param name="lpKeyState">256 bytes: bit 7 set for a key that is down, bit 0 for a toggled one.</param>
    /// <param name="pwszBuff">Receives the characters.</param>
    /// <param name="cchBuff">The buffer's size in characters.</param>
    /// <param name="wFlags">0, or 4 to leave the keyboard state alone (Windows 10 1607 and later).</param>
    /// <param name="dwhkl">The keyboard layout.</param>
    /// <returns>The number of characters written; 0 when the key types none; −1 for a dead key.</returns>
    [LibraryImport("user32.dll")]
    internal static unsafe partial int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte* lpKeyState, char* pwszBuff, int cchBuff, uint wFlags, nint dwhkl);

    /// <summary>The keyboard layout of a thread: the one its window's text is typed with.</summary>
    /// <param name="idThread">The thread; 0 for the calling one.</param>
    /// <returns>The layout handle.</returns>
    [LibraryImport("user32.dll")]
    internal static partial nint GetKeyboardLayout(uint idThread);

    /// <summary>A key's state as the calling thread sees it; bit 0 is the toggle (Caps Lock on).</summary>
    /// <param name="nVirtKey">The virtual key.</param>
    /// <returns>The state bits.</returns>
    [LibraryImport("user32.dll")]
    internal static partial short GetKeyState(int nVirtKey);
}
