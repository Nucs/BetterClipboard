using System.Runtime.InteropServices;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Input;

/// <summary>
/// Synthetic keyboard helpers built on <c>SendInput</c>.
/// </summary>
/// <remarks>
/// <c>SendInput</c> is subject to UIPI: a non-elevated BetterClipboard cannot inject into an elevated
/// foreground window (e.g. an admin terminal) — the call reports 0 events injected and nothing happens.
/// Callers treat that as "copied but not pasted".
/// </remarks>
public static class KeyboardInput
{
    /// <summary>
    /// Unassigned virtual key (<c>0xE8</c>) used as a "mask" key: tapping it while Win or Alt is held
    /// makes the system see a chord, so releasing the modifier does not open Start or activate a menu bar.
    /// AutoHotkey uses the same key for the same purpose.
    /// </summary>
    public const ushort MaskKey = 0xE8;

    /// <summary>
    /// Value stamped into <c>dwExtraInfo</c> of every event BetterClipboard injects, so our own keyboard
    /// hook can recognize (and ignore) exactly its own synthetic keys — while still reacting to keys
    /// injected by remappers such as PowerToys Keyboard Manager or AutoHotkey (a user who maps
    /// CapsLock+V to Win+V must still get BetterClipboard).
    /// </summary>
    public const nint OwnInjectionMarker = 0x0B0C_C11B;

    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;
    private const ushort VK_V = 0x56;

    /// <summary>
    /// Reads which modifiers are currently down (asynchronous key state).
    /// </summary>
    /// <returns>The held modifiers.</returns>
    public static HotkeyModifiers GetHeldModifiers()
    {
        var modifiers = HotkeyModifiers.None;
        if (IsDown(VK_CONTROL)) modifiers |= HotkeyModifiers.Control;
        if (IsDown(VK_MENU)) modifiers |= HotkeyModifiers.Alt;
        if (IsDown(VK_SHIFT)) modifiers |= HotkeyModifiers.Shift;
        if (IsDown(VK_LWIN) || IsDown(VK_RWIN)) modifiers |= HotkeyModifiers.Win;
        return modifiers;
    }

    /// <summary>
    /// Taps <see cref="MaskKey"/> (down + up).
    /// </summary>
    /// <returns><see langword="true"/> when both events were injected.</returns>
    public static bool TapMaskKey() => Send([Key(MaskKey, up: false), Key(MaskKey, up: true)]) == 2;

    /// <summary>
    /// Releases every held modifier with synthetic key-ups, tapping the mask key first when Win or Alt is
    /// down so the release does not open Start / focus a menu bar.
    /// </summary>
    /// <remarks>
    /// Needed before injecting Ctrl+V: if the user still holds Shift from Shift+Enter, the target app would
    /// otherwise receive Ctrl+Shift+V, which many apps map to something else.
    /// </remarks>
    public static void ReleaseModifiers()
    {
        var inputs = new List<INPUT>();
        bool winDown = IsDown(VK_LWIN) || IsDown(VK_RWIN);
        bool altDown = IsDown(VK_MENU);
        if (winDown || altDown)
        {
            inputs.Add(Key(MaskKey, up: false));
            inputs.Add(Key(MaskKey, up: true));
        }

        foreach (var vk in new[] { VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN })
        {
            if (IsDown(vk))
            {
                inputs.Add(Key(vk, up: true));
            }
        }

        if (inputs.Count > 0)
        {
            Send([.. inputs]);
        }
    }

    /// <summary>
    /// Sends Ctrl+V to whatever window has keyboard focus.
    /// </summary>
    /// <returns><see langword="true"/> when all four events were injected (UIPI can block them).</returns>
    public static bool SendCtrlV() =>
        Send([Key(VK_CONTROL, up: false), Key(VK_V, up: false), Key(VK_V, up: true), Key(VK_CONTROL, up: true)]) == 4;

    /// <summary>
    /// Injects a no-op mouse event. Counts as "the calling process received the last input event" for
    /// the foreground-lock rules, which lets a subsequent <c>SetForegroundWindow</c> succeed — the
    /// standard workaround (also used by PowerToys) for activating our own window from a background hook.
    /// </summary>
    /// <returns><see langword="true"/> when injected.</returns>
    public static bool SendNoOpMouseInput()
    {
        var input = new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwExtraInfo = OwnInjectionMarker } } };
        return Send([input]) == 1;
    }

    /// <summary>Whether a key is currently down.</summary>
    /// <param name="vk">Virtual key.</param>
    /// <returns><see langword="true"/> when down.</returns>
    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Builds a keyboard event.</summary>
    /// <param name="vk">Virtual key.</param>
    /// <param name="up">Key-up (otherwise key-down).</param>
    /// <returns>The event.</returns>
    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0, dwExtraInfo = OwnInjectionMarker } },
    };

    /// <summary>Injects events.</summary>
    /// <param name="inputs">Events.</param>
    /// <returns>Number of events injected.</returns>
    private static uint Send(INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
}
