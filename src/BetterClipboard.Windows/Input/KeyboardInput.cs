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

    /// <summary>
    /// Value stamped into <c>dwExtraInfo</c> of the keys <see cref="PasteKeyGuard"/> types again after a paste: the person's
    /// own keys, held back while the paste kept Ctrl down. Not <see cref="OwnInjectionMarker"/> on purpose — BetterClipboard's
    /// keyboard hook must treat them as the person's keys (a Win+V typed meanwhile still opens the panel), and only the guard
    /// itself lets them pass.
    /// </summary>
    public const nint ReplayMarker = 0x0B0C_C11C;

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
    /// Sends Ctrl+V to whatever window has keyboard focus, Ctrl released in the same call.
    /// </summary>
    /// <remarks>
    /// Not what a paste from the panel uses since 2026-11-17: a window that takes the keys late can see the V with Ctrl
    /// already up when another program resets its key state in between (<see cref="InputThreadWatch"/>), so the paste sends
    /// <see cref="SendCtrlVHoldingCtrl"/> and releases Ctrl once the window took the V (<see cref="ReleaseCtrl"/>).
    /// </remarks>
    /// <returns><see langword="true"/> when all four events were injected (UIPI can block them).</returns>
    public static bool SendCtrlV() =>
        Send([Key(VK_CONTROL, up: false), Key(VK_V, up: false), Key(VK_V, up: true), Key(VK_CONTROL, up: true)]) == 4;

    /// <summary>
    /// Sends Ctrl down, V down and V up to whatever window has keyboard focus, in one call, and leaves Ctrl down: the caller
    /// releases it with <see cref="ReleaseCtrl"/> once the window took the V (<see cref="InputThreadWatch.WaitUntilTaken(nint, CancellationToken)"/>).
    /// </summary>
    /// <remarks>
    /// One call, so nothing else the keyboard or another program types can come between the Ctrl and the V (Windows does not
    /// intersperse other input with the events of one <c>SendInput</c> call). Footgun: a caller must release Ctrl on every
    /// path, also when this reports fewer events injected — a Ctrl left down makes every key the user types Ctrl+key.
    /// </remarks>
    /// <returns><see langword="true"/> when all three events were injected (UIPI can block them).</returns>
    public static bool SendCtrlVHoldingCtrl() =>
        Send([Key(VK_CONTROL, up: false), Key(VK_V, up: false), Key(VK_V, up: true)]) == 3;

    /// <summary>
    /// Types keys again that <see cref="PasteKeyGuard"/> held back, in their order and with their scan codes and extended
    /// flags, stamped <see cref="ReplayMarker"/>.
    /// </summary>
    /// <remarks>
    /// They arrive as injected keys: a program that ignores injected input (some games, a remapper set up so) sees them so,
    /// and Windows drops them (UIPI) when a window running as administrator came to the front meanwhile.
    /// </remarks>
    /// <param name="keys">The held keys, oldest first.</param>
    /// <returns>How many of them were injected.</returns>
    public static int TypeAgain(IReadOnlyList<HeldKey> keys) => keys.Count == 0 ? 0 : (int)Send(ReplayEvents(keys));

    /// <summary>The events <see cref="TypeAgain"/> injects for held keys, in their order (a seam for tests).</summary>
    /// <param name="keys">The held keys, oldest first.</param>
    /// <returns>One keyboard event per key.</returns>
    internal static INPUT[] ReplayEvents(IReadOnlyList<HeldKey> keys)
    {
        var inputs = new INPUT[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            uint flags = (key.IsUp ? KEYEVENTF_KEYUP : 0) | (key.IsExtended ? KEYEVENTF_EXTENDEDKEY : 0);

            // A character another program typed as Unicode (VK_PACKET) carries its code unit in the scan code; it can only be
            // typed again the same way.
            bool unicode = key.VirtualKey == VK_PACKET;
            inputs[i] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = unicode ? (ushort)0 : (ushort)key.VirtualKey,
                        wScan = (ushort)key.ScanCode,
                        dwFlags = flags | (unicode ? KEYEVENTF_UNICODE : 0),
                        dwExtraInfo = ReplayMarker,
                    },
                },
            };
        }

        return inputs;
    }

    /// <summary>Releases the Ctrl that <see cref="SendCtrlVHoldingCtrl"/> left down.</summary>
    /// <remarks>Harmless when Ctrl is up already: the window gets a key-up for a key it does not see down.</remarks>
    /// <returns><see langword="true"/> when the key-up was injected.</returns>
    public static bool ReleaseCtrl() => Send([Key(VK_CONTROL, up: true)]) == 1;

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
