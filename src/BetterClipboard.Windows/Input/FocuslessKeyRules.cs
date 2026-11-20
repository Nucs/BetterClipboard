namespace BetterClipboard.Windows.Input;

/// <summary>
/// Which keys the panel takes from the app while it shows without the keyboard (<see cref="FocuslessPanelInput"/>, CLAUDE.md
/// §2.59), and which of them may type a character into its search. Pure: the hook asks with a key and the modifiers held, and
/// the rules are unit-tested.
/// </summary>
/// <remarks>
/// <para>
/// <b>Take nearly everything.</b> While the panel shows, a key the user types is meant for the panel, as when it had the
/// keyboard: a letter searches, Enter pastes, Esc closes. A key that reached the app instead would land in the very control
/// the user wants to paste into — a rename box would take the letter, F5 would refresh the folder and end the rename. So every
/// key is taken (used by the panel, or dropped), except the ones listed below.
/// </para>
/// <para>
/// <b>Never taken:</b> the modifier keys themselves (the app and Windows must keep seeing them, or a shortcut pressed later
/// would find Ctrl stuck), anything with a Windows key held (Windows' own shortcuts, and the panel's own Win+V, which its
/// shortcut service handles), Alt+Tab and the other system switches (Alt+Esc, Ctrl+Esc, Ctrl+Shift+Esc), the lock keys
/// (their light and toggle are system-wide), Print Screen, Sleep, the media, volume, browser and launch keys (other programs
/// listen for them everywhere), the keys that switch an input method's mode, and the mask key BetterClipboard and others tap
/// so that releasing Win or Alt opens nothing.
/// </para>
/// </remarks>
public static class FocuslessKeyRules
{
    /// <summary>The virtual key Windows reports for a character another program typed as Unicode (<c>VK_PACKET</c>).</summary>
    public const uint PacketKey = 0xE7;

    /// <summary>Whether a key is Shift, Ctrl, Alt or a Windows key (either side, or the side-less codes).</summary>
    /// <param name="virtualKey">The virtual key.</param>
    /// <returns><see langword="true"/> for a modifier key.</returns>
    public static bool IsModifier(uint virtualKey) => virtualKey is
        0x10 or 0x11 or 0x12 // VK_SHIFT, VK_CONTROL, VK_MENU
        or (>= 0xA0 and <= 0xA5) // VK_LSHIFT .. VK_RMENU
        or 0x5B or 0x5C; // VK_LWIN, VK_RWIN

    /// <summary>Whether the panel takes this key-down from the app (see the class remarks for the keys it leaves alone).</summary>
    /// <param name="virtualKey">The virtual key of the key-down.</param>
    /// <param name="held">The modifiers held at that moment.</param>
    /// <returns><see langword="true"/> to take the key (the app never sees it); <see langword="false"/> to let it through.</returns>
    public static bool Takes(uint virtualKey, HotkeyModifiers held)
    {
        if (IsModifier(virtualKey) || (held & HotkeyModifiers.Win) != 0)
        {
            return false;
        }

        bool alt = (held & HotkeyModifiers.Alt) != 0;
        bool ctrl = (held & HotkeyModifiers.Control) != 0;
        return virtualKey switch
        {
            // Alt+Tab (and Ctrl+Alt+Tab, its sticky form): the task switcher.
            0x09 when alt => false,

            // Alt+Esc (next window), Ctrl+Esc (Start), Ctrl+Shift+Esc (Task Manager).
            0x1B when alt || ctrl => false,
            _ => !PassesAlways(virtualKey),
        };
    }

    /// <summary>
    /// Whether a key taken with these modifiers may type a character: plain or with Shift, or with Ctrl and Alt together
    /// (AltGr, which types € or @ on many layouts). Ctrl alone and Alt alone are shortcuts, never typing.
    /// </summary>
    /// <param name="virtualKey">The virtual key.</param>
    /// <param name="held">The modifiers held.</param>
    /// <returns><see langword="true"/> when the keyboard layout should be asked what the key types.</returns>
    public static bool MayType(uint virtualKey, HotkeyModifiers held)
    {
        bool alt = (held & HotkeyModifiers.Alt) != 0;
        bool ctrl = (held & HotkeyModifiers.Control) != 0;
        return alt == ctrl && (held & HotkeyModifiers.Win) == 0 && !IsModifier(virtualKey) && !PassesAlways(virtualKey);
    }

    /// <summary>
    /// The printable part of what a key typed: control characters (what Enter, Tab, Backspace, Esc and Ctrl+letter translate
    /// to) are left out.
    /// </summary>
    /// <param name="typed">The characters the layout gave.</param>
    /// <returns>The printable characters, or <see langword="null"/> when none is left.</returns>
    public static string? Printable(ReadOnlySpan<char> typed)
    {
        Span<char> kept = stackalloc char[typed.Length];
        int count = 0;
        foreach (char c in typed)
        {
            if (!char.IsControl(c))
            {
                kept[count++] = c;
            }
        }

        return count == 0 ? null : new string(kept[..count]);
    }

    /// <summary>Keys that always reach the app (and Windows), whatever is held: see the class remarks.</summary>
    /// <param name="virtualKey">The virtual key.</param>
    /// <returns><see langword="true"/> for a key the panel never takes.</returns>
    private static bool PassesAlways(uint virtualKey) => virtualKey switch
    {
        0x14 or 0x90 or 0x91 => true, // Caps Lock, Num Lock, Scroll Lock
        0x2C or 0x5F => true, // Print Screen, Sleep
        >= 0xA6 and <= 0xB7 => true, // browser, volume, media and launch keys
        0x15 or 0x17 or 0x18 or 0x19 or 0x1C or 0x1D or 0x1E or 0x1F => true, // input method modes (Kana/Hangul, Junja, Final, Hanja/Kanji, Convert, NonConvert, Accept, ModeChange)
        0xE5 => true, // VK_PROCESSKEY: a key an input method already took
        >= 0xF0 and <= 0xF6 => true, // the Japanese input method's OEM keys (Eisu, Katakana/Hiragana, Hankaku/Zenkaku, Roman)
        KeyboardInput.MaskKey => true, // tapped so that releasing Win or Alt opens nothing
        _ => false,
    };
}
