using System.Globalization;
using System.Text;

namespace BetterClipboard.Windows.Input;

/// <summary>Modifier keys of a hotkey. Bit values equal the Win32 <c>MOD_*</c> flags so they pass straight to <c>RegisterHotKey</c>.</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>Alt (<c>MOD_ALT</c>).</summary>
    Alt = 0x1,

    /// <summary>Ctrl (<c>MOD_CONTROL</c>).</summary>
    Control = 0x2,

    /// <summary>Shift (<c>MOD_SHIFT</c>).</summary>
    Shift = 0x4,

    /// <summary>Windows key (<c>MOD_WIN</c>).</summary>
    Win = 0x8,
}

/// <summary>
/// What a key does on its own. It decides whether the key may end a shortcut without Ctrl, Alt or Win, and whether the
/// shortcut box's recorder takes the key or lets it type.
/// </summary>
public enum HotkeyKeyKind
{
    /// <summary>
    /// Not a key a shortcut can end with: a modifier (it is held, not pressed), a mouse button, or a code that carries no
    /// key press (<c>VK_PACKET</c>, <c>VK_PROCESSKEY</c>, <c>VK_NONAME</c>, and <c>0xE8</c>, which BetterClipboard injects
    /// itself as its mask key).
    /// </summary>
    Unusable,

    /// <summary>
    /// Types a character: letters, digits, punctuation, Space, and the numeric keypad's digits and operators. Needs Ctrl,
    /// Alt or Win: alone or with Shift only, the shortcut would make that character untypable in every app.
    /// </summary>
    Typing,

    /// <summary>
    /// Edits text, moves the caret or answers a dialog: Enter, Tab, Backspace, Delete, Insert, Esc, the arrows, Home, End,
    /// Page Up, Page Down and Clear. Needs Ctrl, Alt or Win, like <see cref="Typing"/>.
    /// </summary>
    Editing,

    /// <summary>
    /// Switches an input method (keys of Japanese, Korean and Chinese keyboards). Needs Ctrl, Alt or Win, so the key keeps
    /// switching the input method.
    /// </summary>
    InputMethod,

    /// <summary>F1–F24. Allowed alone or with Shift only.</summary>
    Function,

    /// <summary>
    /// Does nothing in text, so it may be a shortcut alone or with Shift only: Pause, Caps Lock, Num Lock, Scroll Lock,
    /// Print Screen, the Menu key, the media, volume, browser and launch keys, and codes without a name (macro keys).
    /// </summary>
    Standalone,
}

/// <summary>
/// A global keyboard shortcut such as <c>Win+V</c>: modifiers plus one virtual key, any key of the keyboard.
/// </summary>
/// <remarks>
/// <para>
/// <b>Names.</b> Parsing accepts friendly names case-insensitively, with or without spaces, dashes and underscores
/// (<c>Ctrl</c>/<c>Control</c>, <c>Win</c>/<c>Windows</c>, letters, digits, <c>F1</c>–<c>F24</c>, <c>Page Up</c>,
/// <c>Num5</c>/<c>Numpad5</c>, <c>PrintScreen</c>/<c>PrtScn</c>, <c>VolumeUp</c>, <c>MediaPlayPause</c>, punctuation such
/// as <c>`</c>, the Win32 names such as <c>VK_SNAPSHOT</c>) and the raw form <c>0xNN</c> for any virtual-key code.
/// <see cref="ToString"/> produces the canonical form <c>Ctrl+Alt+Shift+Win+Key</c> used in settings, and it always
/// parses back to the same gesture: a key without a name is written as <c>0xNN</c>.
/// </para>
/// <para>
/// <b>Safety.</b> <see cref="TryParse(string, out HotkeyGesture, out string)"/> and <see cref="TryCreate"/> refuse, with a
/// reason (<see cref="Problem"/>), the gestures a global shortcut would break: a key that types, edits or switches an input
/// method without Ctrl, Alt or Win (a bare letter would become untypable system-wide); the keys Windows keeps
/// (Ctrl+Alt+Delete, Win+L, Alt+Tab, Alt+Esc, Ctrl+Esc, Ctrl+Shift+Esc, Alt+F4); and the clipboard's own keys (Ctrl+C,
/// Ctrl+X, Ctrl+V, Ctrl+Insert): copying must keep working for BetterClipboard to record copies, and BetterClipboard pastes
/// with Ctrl+V itself, so a Ctrl+V shortcut would reopen the panel instead of pasting.
/// </para>
/// <para>
/// Footgun: the punctuation names are those of the US layout (the key right of the left Shift on an ISO keyboard is
/// <c>Oem102</c>). The virtual-key code, not the printed character, is what is stored, so a shortcut keeps working when the
/// layout changes; only its name can look unfamiliar on another layout.
/// </para>
/// </remarks>
/// <param name="Modifiers">Modifier keys.</param>
/// <param name="VirtualKey">Win32 virtual-key code of the main key.</param>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey)
{
    private const uint VK_TAB = 0x09;
    private const uint VK_ESCAPE = 0x1B;
    private const uint VK_INSERT = 0x2D;
    private const uint VK_DELETE = 0x2E;
    private const uint VK_F4 = 0x73;

    /// <summary>Every virtual-key code with a name or a kind other than the default (unnamed, <see cref="HotkeyKeyKind.Standalone"/>), by code.</summary>
    private static readonly Dictionary<uint, KeyInfo> KeysByCode = BuildKeyTable();

    /// <summary>Lookup from a normalized name or alias (<see cref="NormalizeName"/>) to its virtual-key code.</summary>
    private static readonly Dictionary<string, uint> CodesByName = BuildNameIndex(KeysByCode.Values);

    /// <summary>Whether the gesture includes the Windows key (such combos are usually owned by Explorer).</summary>
    public bool UsesWinKey => (Modifiers & HotkeyModifiers.Win) != 0;

    /// <summary>
    /// Parses a gesture.
    /// </summary>
    /// <param name="text">Text such as <c>Win+V</c>, <c>ctrl + shift + v</c>, <c>Ctrl+`</c> or <c>Ctrl+Alt+Num 5</c>.</param>
    /// <param name="gesture">The parsed gesture; <see langword="default"/> when the result is <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is a valid, safe global hotkey.</returns>
    public static bool TryParse(string? text, out HotkeyGesture gesture) => TryParse(text, out gesture, out _);

    /// <summary>
    /// Parses a gesture and says why when it is not one.
    /// </summary>
    /// <param name="text">Text such as <c>Win+V</c>, <c>ctrl + shift + v</c>, <c>Ctrl+`</c> or <c>Win+Shift+F23</c>.</param>
    /// <param name="gesture">The parsed gesture; <see langword="default"/> when the result is <see langword="false"/>.</param>
    /// <param name="problem">
    /// <see langword="null"/> on success; otherwise one or two sentences for the user: what is wrong and what to do
    /// (shown under Settings' shortcut box and printed by <c>--set-hotkeys</c>).
    /// </param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is a valid, safe global hotkey.</returns>
    public static bool TryParse(string? text, out HotkeyGesture gesture, out string? problem)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            problem = "Type a shortcut, for example Win+V or Ctrl+Shift+V.";
            return false;
        }

        // Split on '+' but allow the '+' key itself only as "Plus" or "NumPlus" (keeps parsing unambiguous).
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            problem = "A '+' alone is not a shortcut. Name the + key as Plus (or NumPlus on the keypad), for example Ctrl+Alt+Plus.";
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var modifier = ParseModifier(parts[i]);
            if (modifier == HotkeyModifiers.None)
            {
                problem = $"\"{parts[i]}\" is not a modifier. Put only Ctrl, Alt, Shift or Win before the key.";
                return false;
            }

            if ((modifiers & modifier) != 0)
            {
                problem = $"{ModifierName(modifier)} is in the shortcut twice.";
                return false;
            }

            modifiers |= modifier;
        }

        var last = parts[^1];
        if (!TryParseKey(last, out var key))
        {
            var trailing = ParseModifier(last);
            problem = trailing != HotkeyModifiers.None
                ? $"{ModifierName(trailing)} is a modifier: hold it with another key, for example {FormatModifiers(modifiers | trailing)}V."
                : $"\"{last}\" is not a key name. Use names such as V, 5, F13, PageDown, Num5, NumPlus, VolumeUp or PrintScreen, or 0xNN for a virtual-key code.";
            return false;
        }

        return TryCreate(modifiers, key, out gesture, out problem);
    }

    /// <summary>
    /// Builds a gesture from keys that were pressed (Settings' recorder) or parsed, refusing the unsafe ones with a reason.
    /// </summary>
    /// <param name="modifiers">The modifiers held.</param>
    /// <param name="virtualKey">The main key's virtual-key code.</param>
    /// <param name="gesture">The gesture; <see langword="default"/> when the result is <see langword="false"/>.</param>
    /// <param name="problem"><see langword="null"/> on success; otherwise why the gesture cannot open BetterClipboard (see <see cref="Problem"/>).</param>
    /// <returns><see langword="true"/> when the gesture is a valid, safe global hotkey.</returns>
    public static bool TryCreate(HotkeyModifiers modifiers, uint virtualKey, out HotkeyGesture gesture, out string? problem)
    {
        problem = Problem(modifiers, virtualKey);
        gesture = problem is null ? new HotkeyGesture(modifiers, virtualKey) : default;
        return problem is null;
    }

    /// <summary>
    /// Why a gesture cannot be a global shortcut that opens BetterClipboard, or <see langword="null"/> when it can.
    /// </summary>
    /// <remarks>
    /// The rules are the class remarks' "Safety" list. Only the ones a person can hit are checked: a gesture Windows itself
    /// refuses to register for another reason fails later, at registration, and its row in Settings shows the error.
    /// </remarks>
    /// <param name="modifiers">The modifiers.</param>
    /// <param name="virtualKey">The main key's virtual-key code; anything outside 0x01–0xFE is refused.</param>
    /// <returns>One or two sentences for the user, or <see langword="null"/>.</returns>
    public static string? Problem(HotkeyModifiers modifiers, uint virtualKey)
    {
        if ((modifiers & ~(HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Win)) != 0)
        {
            return "Only Ctrl, Alt, Shift and Win can be held with the key.";
        }

        var kind = KindOf(virtualKey);
        string key = KeyName(virtualKey);
        if (kind == HotkeyKeyKind.Unusable)
        {
            return UnusableReason(virtualKey, key);
        }

        string gesture = new HotkeyGesture(modifiers, virtualKey).ToString();
        bool ctrl = (modifiers & HotkeyModifiers.Control) != 0;
        bool alt = (modifiers & HotkeyModifiers.Alt) != 0;
        bool win = (modifiers & HotkeyModifiers.Win) != 0;

        // The keys Windows keeps. Ctrl+Alt+Delete never reaches an app, and Win+L locks the PC whatever an app does with
        // it; the others work only as long as nobody takes them, and people rely on them to get out of trouble.
        if (virtualKey == VK_DELETE && ctrl && alt)
        {
            return "Ctrl+Alt+Delete always goes to Windows. Pick another shortcut.";
        }

        if (modifiers == HotkeyModifiers.Win && virtualKey == 'L')
        {
            return "Win+L locks the PC, and Windows does not let any app take it. Pick another shortcut.";
        }

        if (virtualKey == VK_TAB && alt && !win)
        {
            return $"{gesture} switches between windows. Taking it would stop that. Pick another shortcut.";
        }

        if (virtualKey == VK_ESCAPE)
        {
            if (modifiers == HotkeyModifiers.Control)
            {
                return "Ctrl+Esc opens Start. Pick another shortcut.";
            }

            if (modifiers == (HotkeyModifiers.Control | HotkeyModifiers.Shift))
            {
                return "Ctrl+Shift+Esc opens Task Manager, which you need when an app stops responding. Pick another shortcut.";
            }

            if (alt && !ctrl && !win)
            {
                return $"{gesture} switches between windows. Taking it would stop that. Pick another shortcut.";
            }
        }

        if (virtualKey == VK_F4 && modifiers == HotkeyModifiers.Alt)
        {
            return "Alt+F4 closes windows. Taking it would stop that. Pick another shortcut.";
        }

        // The clipboard's own keys: BetterClipboard records what Ctrl+C / Ctrl+X / Ctrl+Insert copy, and pastes with an
        // injected Ctrl+V — a registered Ctrl+V would catch that injection (RegisterHotKey cannot tell it apart) and reopen
        // the panel instead of pasting.
        if (modifiers == HotkeyModifiers.Control)
        {
            switch (virtualKey)
            {
                case 'C':
                case VK_INSERT:
                    return $"{gesture} copies in every app, and BetterClipboard records those copies. Pick another shortcut.";
                case 'X':
                    return "Ctrl+X cuts in every app, and BetterClipboard records what you cut. Pick another shortcut.";
                case 'V':
                    return "Ctrl+V pastes in every app, and BetterClipboard pastes with Ctrl+V itself. Pick another shortcut, for example Ctrl+Shift+V.";
            }
        }

        bool strong = ctrl || alt || win;
        if (!strong && kind is HotkeyKeyKind.Typing or HotkeyKeyKind.Editing or HotkeyKeyKind.InputMethod)
        {
            return modifiers == HotkeyModifiers.None
                ? $"{key} alone would stop that key from working in every other app. Hold Ctrl, Alt or Win with it."
                : $"{gesture} would stop that key from working with Shift in every other app. Hold Ctrl, Alt or Win with it.";
        }

        return null;
    }

    /// <summary>
    /// What <paramref name="virtualKey"/> does on its own (see <see cref="HotkeyKeyKind"/>).
    /// </summary>
    /// <param name="virtualKey">A virtual-key code; anything outside 0x01–0xFE is <see cref="HotkeyKeyKind.Unusable"/>.</param>
    /// <returns>The key's kind.</returns>
    public static HotkeyKeyKind KindOf(uint virtualKey)
    {
        if (virtualKey is 0 or > 0xFE)
        {
            return HotkeyKeyKind.Unusable;
        }

        return KeysByCode.TryGetValue(virtualKey, out var info) ? info.Kind : HotkeyKeyKind.Standalone;
    }

    /// <summary>
    /// Whether <paramref name="virtualKey"/> is a modifier key (Shift, Ctrl, Alt or a Windows key, either side): a key that is
    /// held while another key is pressed, never the end of a shortcut.
    /// </summary>
    /// <param name="virtualKey">A virtual-key code as a keyboard hook reports it (sided, such as <c>VK_LSHIFT</c>) or generic (<c>VK_SHIFT</c>).</param>
    /// <returns><see langword="true"/> for the eleven modifier codes.</returns>
    public static bool IsModifierKey(uint virtualKey) => virtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    /// <summary>
    /// The canonical name of a key as settings store it: the letter or digit itself, <c>F1</c>–<c>F24</c>, a name such as
    /// <c>PageDown</c> or <c>VolumeUp</c>, or <c>0xNN</c> for a code without a name.
    /// </summary>
    /// <param name="virtualKey">A virtual-key code.</param>
    /// <returns>The name; <see cref="TryParse(string, out HotkeyGesture)"/> reads it back as the same code.</returns>
    public static string KeyName(uint virtualKey)
    {
        if (virtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return "F" + (virtualKey - 0x70 + 1).ToString(CultureInfo.InvariantCulture);
        }

        return KeysByCode.TryGetValue(virtualKey, out var info) && info.Name is not null
            ? info.Name
            : "0x" + virtualKey.ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Formats modifiers the way <see cref="ToString"/> starts a gesture, each followed by <c>+</c>: <c>Ctrl+Alt+</c>.
    /// </summary>
    /// <param name="modifiers">The modifiers.</param>
    /// <returns>The prefix; empty for <see cref="HotkeyModifiers.None"/>.</returns>
    public static string FormatModifiers(HotkeyModifiers modifiers)
    {
        var builder = new StringBuilder();
        if ((modifiers & HotkeyModifiers.Control) != 0) builder.Append("Ctrl+");
        if ((modifiers & HotkeyModifiers.Alt) != 0) builder.Append("Alt+");
        if ((modifiers & HotkeyModifiers.Shift) != 0) builder.Append("Shift+");
        if ((modifiers & HotkeyModifiers.Win) != 0) builder.Append("Win+");
        return builder.ToString();
    }

    /// <summary>
    /// Formats the gesture canonically (<c>Ctrl+Alt+Shift+Win+Key</c>).
    /// </summary>
    /// <returns>The display/settings string; it always parses back to this gesture.</returns>
    public override string ToString() => FormatModifiers(Modifiers) + KeyName(VirtualKey);

    /// <summary>Parses one modifier token, either side's name included (a shortcut cannot tell left Ctrl from right Ctrl).</summary>
    /// <param name="token">Token text.</param>
    /// <returns>The modifier, or <see cref="HotkeyModifiers.None"/> when unknown.</returns>
    private static HotkeyModifiers ParseModifier(string token) => NormalizeName(token) switch
    {
        "CTRL" or "CONTROL" or "CTL" or "LCTRL" or "RCTRL" or "LCONTROL" or "RCONTROL" or "LEFTCTRL" or "RIGHTCTRL" => HotkeyModifiers.Control,
        "ALT" or "MENU" or "LALT" or "RALT" or "LEFTALT" or "RIGHTALT" => HotkeyModifiers.Alt,
        "SHIFT" or "LSHIFT" or "RSHIFT" or "LEFTSHIFT" or "RIGHTSHIFT" => HotkeyModifiers.Shift,
        "WIN" or "WINDOWS" or "META" or "SUPER" or "CMD" or "LWIN" or "RWIN" or "LEFTWIN" or "RIGHTWIN" => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };

    /// <summary>The display name of one modifier, as <see cref="FormatModifiers"/> writes it.</summary>
    /// <param name="modifier">A single modifier.</param>
    /// <returns>Its name without the <c>+</c>.</returns>
    private static string ModifierName(HotkeyModifiers modifier) => FormatModifiers(modifier).TrimEnd('+');

    /// <summary>Parses the main key token.</summary>
    /// <param name="token">Token text.</param>
    /// <param name="key">Virtual-key code.</param>
    /// <returns><see langword="true"/> for a known key name or a <c>0xNN</c> code in 0x01–0xFE.</returns>
    private static bool TryParseKey(string token, out uint key)
    {
        key = 0;
        if (token.Length == 1 && char.IsAsciiLetterOrDigit(token[0]))
        {
            key = char.ToUpperInvariant(token[0]); // VK_A..VK_Z and VK_0..VK_9 equal their ASCII codes
            return true;
        }

        if (token.Length is 2 or 3 && (token[0] is 'F' or 'f') &&
            int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 24)
        {
            key = (uint)(0x70 + number - 1);
            return true;
        }

        // The raw form any code can be written in — what ToString uses for a key without a name, so it parses back.
        if (token.Length is 3 or 4 && token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(token.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code) && code is >= 0x01 and <= 0xFE)
        {
            key = code;
            return true;
        }

        return CodesByName.TryGetValue(NormalizeName(token), out key);
    }

    /// <summary>
    /// Folds a name for lookup: upper case, without spaces, and — for names longer than one character — without dashes and
    /// underscores, so <c>Page Up</c>, <c>page-up</c>, <c>PAGE_UP</c> and <c>VK_PRIOR</c>'s spelling all meet. Single
    /// characters stay as they are: <c>-</c> is the minus key.
    /// </summary>
    /// <param name="name">A key or modifier name.</param>
    /// <returns>The folded name.</returns>
    private static string NormalizeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsWhiteSpace(c) || (name.Length > 1 && c is '-' or '_'))
            {
                continue;
            }

            builder.Append(char.ToUpperInvariant(c));
        }

        // A name of only dashes or underscores ("--") folds to nothing; keep it as typed so it matches no key.
        return builder.Length == 0 ? name : builder.ToString();
    }

    /// <summary>Why a key of kind <see cref="HotkeyKeyKind.Unusable"/> cannot end a shortcut.</summary>
    /// <param name="virtualKey">The code.</param>
    /// <param name="name">Its display name.</param>
    /// <returns>One sentence for the user.</returns>
    private static string UnusableReason(uint virtualKey, string name)
    {
        if (IsModifierKey(virtualKey))
        {
            return $"{name} is a modifier: hold it with another key, for example Ctrl+Shift+V.";
        }

        return virtualKey switch
        {
            0x01 or 0x02 or 0x04 or 0x05 or 0x06 => $"{name} is a mouse button, not a key. Pick a key of the keyboard.",
            0xE8 => "0xE8 cannot be used: BetterClipboard sends that code itself, to keep Start closed when it takes a shortcut.",
            0xE7 => "0xE7 (VK_PACKET) carries typed characters, not a key press. Pick a key of the keyboard.",
            0xE5 => "0xE5 (VK_PROCESSKEY) comes from an input method, not from a key. Pick a key of the keyboard.",
            _ => $"{name} is not a key a shortcut can use. Pick a key of the keyboard.",
        };
    }

    /// <summary>Builds the name index: each key's canonical name and aliases, folded with <see cref="NormalizeName"/>.</summary>
    /// <param name="keys">The key table.</param>
    /// <returns>Folded name → virtual-key code.</returns>
    /// <exception cref="InvalidOperationException">Two keys share a name (a mistake in the table, caught by any test that parses).</exception>
    private static Dictionary<string, uint> BuildNameIndex(IEnumerable<KeyInfo> keys)
    {
        var index = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var info in keys)
        {
            foreach (var name in info.AllNames())
            {
                var folded = NormalizeName(name);
                if (index.TryGetValue(folded, out var existing) && existing != info.VirtualKey)
                {
                    throw new InvalidOperationException($"The key name '{name}' is used for both 0x{existing:X2} and 0x{info.VirtualKey:X2}.");
                }

                index[folded] = info.VirtualKey;
            }
        }

        return index;
    }

    /// <summary>
    /// The key table: every virtual-key code that has a name, or a kind other than the default. Codes not listed here are
    /// unnamed (written <c>0xNN</c>) and <see cref="HotkeyKeyKind.Standalone"/>: reserved and OEM-specific codes that only
    /// macro keyboards and remapping tools send. Letters, digits and F1–F24 are named by <see cref="KeyName"/> directly.
    /// </summary>
    /// <remarks>
    /// Each key's Win32 constant (without <c>VK_</c>, and with it) is an alias, so a name copied from Microsoft's
    /// documentation works too. The table is the single source for parsing, formatting and the kinds, so a name added here
    /// is parsed, formatted and classified consistently.
    /// </remarks>
    /// <returns>The table by virtual-key code.</returns>
    private static Dictionary<uint, KeyInfo> BuildKeyTable()
    {
        var table = new Dictionary<uint, KeyInfo>();
        void Add(uint vk, string? name, HotkeyKeyKind kind, string? win32 = null, params string[] aliases)
        {
            var all = new List<string>(aliases);
            if (win32 is not null)
            {
                all.Add(win32);
                all.Add("VK_" + win32);
            }

            table[vk] = new KeyInfo(vk, name, kind, all.ToArray());
        }

        // Not keys a shortcut can end with.
        Add(0x00, null, HotkeyKeyKind.Unusable);
        Add(0x01, "MouseLeft", HotkeyKeyKind.Unusable, "LBUTTON");
        Add(0x02, "MouseRight", HotkeyKeyKind.Unusable, "RBUTTON");
        Add(0x04, "MouseMiddle", HotkeyKeyKind.Unusable, "MBUTTON");
        Add(0x05, "MouseX1", HotkeyKeyKind.Unusable, "XBUTTON1");
        Add(0x06, "MouseX2", HotkeyKeyKind.Unusable, "XBUTTON2");
        Add(0x10, "Shift", HotkeyKeyKind.Unusable);
        Add(0x11, "Ctrl", HotkeyKeyKind.Unusable);
        Add(0x12, "Alt", HotkeyKeyKind.Unusable);
        Add(0x5B, "LeftWin", HotkeyKeyKind.Unusable);
        Add(0x5C, "RightWin", HotkeyKeyKind.Unusable);
        Add(0xA0, "LeftShift", HotkeyKeyKind.Unusable);
        Add(0xA1, "RightShift", HotkeyKeyKind.Unusable);
        Add(0xA2, "LeftCtrl", HotkeyKeyKind.Unusable);
        Add(0xA3, "RightCtrl", HotkeyKeyKind.Unusable);
        Add(0xA4, "LeftAlt", HotkeyKeyKind.Unusable);
        Add(0xA5, "RightAlt", HotkeyKeyKind.Unusable);
        Add(0xE5, null, HotkeyKeyKind.Unusable);
        Add(0xE7, null, HotkeyKeyKind.Unusable);
        Add(0xE8, null, HotkeyKeyKind.Unusable);
        Add(0xFC, null, HotkeyKeyKind.Unusable);

        // Editing, caret and dialog keys.
        Add(0x08, "Backspace", HotkeyKeyKind.Editing, "BACK", "Back", "BS", "BkSp");
        Add(0x09, "Tab", HotkeyKeyKind.Editing, "TAB");
        Add(0x0C, "Clear", HotkeyKeyKind.Editing, "CLEAR");
        Add(0x0D, "Enter", HotkeyKeyKind.Editing, "RETURN", "Return");
        Add(0x1B, "Esc", HotkeyKeyKind.Editing, "ESCAPE", "Escape");
        Add(0x21, "PageUp", HotkeyKeyKind.Editing, "PRIOR", "PgUp", "Prior");
        Add(0x22, "PageDown", HotkeyKeyKind.Editing, "NEXT", "PgDn", "Next");
        Add(0x23, "End", HotkeyKeyKind.Editing, "END");
        Add(0x24, "Home", HotkeyKeyKind.Editing, "HOME");
        Add(0x25, "Left", HotkeyKeyKind.Editing, "LEFT", "LeftArrow", "ArrowLeft");
        Add(0x26, "Up", HotkeyKeyKind.Editing, "UP", "UpArrow", "ArrowUp");
        Add(0x27, "Right", HotkeyKeyKind.Editing, "RIGHT", "RightArrow", "ArrowRight");
        Add(0x28, "Down", HotkeyKeyKind.Editing, "DOWN", "DownArrow", "ArrowDown");
        Add(0x2D, "Insert", HotkeyKeyKind.Editing, "INSERT", "Ins");
        Add(0x2E, "Delete", HotkeyKeyKind.Editing, "DELETE", "Del");

        // Keys that type: Space, punctuation (US names), the keypad's digits and operators. Letters and digits are added below.
        Add(0x20, "Space", HotkeyKeyKind.Typing, "SPACE", "Spacebar");
        Add(0x60, "Num0", HotkeyKeyKind.Typing, "NUMPAD0", "Numpad0", "Keypad0");
        Add(0x61, "Num1", HotkeyKeyKind.Typing, "NUMPAD1", "Numpad1", "Keypad1");
        Add(0x62, "Num2", HotkeyKeyKind.Typing, "NUMPAD2", "Numpad2", "Keypad2");
        Add(0x63, "Num3", HotkeyKeyKind.Typing, "NUMPAD3", "Numpad3", "Keypad3");
        Add(0x64, "Num4", HotkeyKeyKind.Typing, "NUMPAD4", "Numpad4", "Keypad4");
        Add(0x65, "Num5", HotkeyKeyKind.Typing, "NUMPAD5", "Numpad5", "Keypad5");
        Add(0x66, "Num6", HotkeyKeyKind.Typing, "NUMPAD6", "Numpad6", "Keypad6");
        Add(0x67, "Num7", HotkeyKeyKind.Typing, "NUMPAD7", "Numpad7", "Keypad7");
        Add(0x68, "Num8", HotkeyKeyKind.Typing, "NUMPAD8", "Numpad8", "Keypad8");
        Add(0x69, "Num9", HotkeyKeyKind.Typing, "NUMPAD9", "Numpad9", "Keypad9");
        Add(0x6A, "NumMultiply", HotkeyKeyKind.Typing, "MULTIPLY", "Num*", "NumpadMultiply", "Multiply", "NumStar");
        Add(0x6B, "NumPlus", HotkeyKeyKind.Typing, "ADD", "NumAdd", "NumpadAdd", "NumpadPlus", "Add");
        Add(0x6C, "NumSeparator", HotkeyKeyKind.Typing, "SEPARATOR", "NumpadSeparator", "Separator");
        Add(0x6D, "NumMinus", HotkeyKeyKind.Typing, "SUBTRACT", "NumSubtract", "NumpadSubtract", "NumpadMinus", "Subtract");
        Add(0x6E, "NumDecimal", HotkeyKeyKind.Typing, "DECIMAL", "Num.", "NumDot", "NumpadDecimal", "Decimal");
        Add(0x6F, "NumDivide", HotkeyKeyKind.Typing, "DIVIDE", "Num/", "NumSlash", "NumpadDivide", "Divide");
        Add(0xBA, ";", HotkeyKeyKind.Typing, "OEM_1", "Semicolon", "Oem1");
        Add(0xBB, "=", HotkeyKeyKind.Typing, "OEM_PLUS", "Plus", "Equals", "Equal", "OemPlus");
        Add(0xBC, ",", HotkeyKeyKind.Typing, "OEM_COMMA", "Comma", "OemComma");
        Add(0xBD, "-", HotkeyKeyKind.Typing, "OEM_MINUS", "Minus", "Dash", "Hyphen", "OemMinus");
        Add(0xBE, ".", HotkeyKeyKind.Typing, "OEM_PERIOD", "Period", "Dot", "OemPeriod");
        Add(0xBF, "/", HotkeyKeyKind.Typing, "OEM_2", "Slash", "Oem2");
        Add(0xC0, "`", HotkeyKeyKind.Typing, "OEM_3", "Backtick", "Tilde", "Grave", "Backquote", "Oem3");
        Add(0xC1, "AbntC1", HotkeyKeyKind.Typing, "ABNT_C1");
        Add(0xC2, "AbntC2", HotkeyKeyKind.Typing, "ABNT_C2");
        Add(0xDB, "[", HotkeyKeyKind.Typing, "OEM_4", "OpenBracket", "LeftBracket", "Oem4");
        Add(0xDC, "\\", HotkeyKeyKind.Typing, "OEM_5", "Backslash", "Oem5");
        Add(0xDD, "]", HotkeyKeyKind.Typing, "OEM_6", "CloseBracket", "RightBracket", "Oem6");
        Add(0xDE, "'", HotkeyKeyKind.Typing, "OEM_7", "Quote", "Apostrophe", "Oem7");
        Add(0xDF, "Oem8", HotkeyKeyKind.Typing, "OEM_8");
        Add(0xE2, "Oem102", HotkeyKeyKind.Typing, "OEM_102", "IntlBackslash", "<");

        // Input-method keys. The unnamed OEM codes 0xE9–0xF5 include the Japanese keyboard's Eisu, Katakana/Hiragana and
        // Hankaku/Zenkaku keys (VK_OEM_ATTN, VK_OEM_COPY, VK_OEM_AUTO, VK_OEM_ENLW), so they count as input-method keys.
        Add(0x15, "Kana", HotkeyKeyKind.InputMethod, "KANA", "Hangul", "Hanguel", "VK_HANGUL");
        Add(0x16, "ImeOn", HotkeyKeyKind.InputMethod, "IME_ON");
        Add(0x17, "Junja", HotkeyKeyKind.InputMethod, "JUNJA");
        Add(0x18, "Final", HotkeyKeyKind.InputMethod, "FINAL");
        Add(0x19, "Kanji", HotkeyKeyKind.InputMethod, "KANJI", "Hanja", "VK_HANJA");
        Add(0x1A, "ImeOff", HotkeyKeyKind.InputMethod, "IME_OFF");
        Add(0x1C, "Convert", HotkeyKeyKind.InputMethod, "CONVERT");
        Add(0x1D, "NonConvert", HotkeyKeyKind.InputMethod, "NONCONVERT");
        Add(0x1E, "Accept", HotkeyKeyKind.InputMethod, "ACCEPT");
        Add(0x1F, "ModeChange", HotkeyKeyKind.InputMethod, "MODECHANGE");
        for (uint vk = 0xE9; vk <= 0xF5; vk++)
        {
            Add(vk, null, HotkeyKeyKind.InputMethod);
        }

        // Keys that do nothing in text: allowed alone.
        Add(0x03, "Break", HotkeyKeyKind.Standalone, "CANCEL", "Cancel", "CtrlBreak");
        Add(0x13, "Pause", HotkeyKeyKind.Standalone, "PAUSE", "PauseBreak");
        Add(0x14, "CapsLock", HotkeyKeyKind.Standalone, "CAPITAL", "Caps", "Capital");
        Add(0x29, "Select", HotkeyKeyKind.Standalone, "SELECT");
        Add(0x2A, "Print", HotkeyKeyKind.Standalone, "PRINT");
        Add(0x2B, "Execute", HotkeyKeyKind.Standalone, "EXECUTE");
        Add(0x2C, "PrintScreen", HotkeyKeyKind.Standalone, "SNAPSHOT", "PrtScn", "PrtSc", "PrtScr", "PrintScrn", "Snapshot");
        Add(0x2F, "Help", HotkeyKeyKind.Standalone, "HELP");
        Add(0x5D, "Apps", HotkeyKeyKind.Standalone, "APPS", "Application", "ContextMenu", "MenuKey");
        Add(0x5F, "Sleep", HotkeyKeyKind.Standalone, "SLEEP");
        Add(0x90, "NumLock", HotkeyKeyKind.Standalone, "NUMLOCK");
        Add(0x91, "ScrollLock", HotkeyKeyKind.Standalone, "SCROLL", "Scroll", "ScrLk");
        Add(0xA6, "BrowserBack", HotkeyKeyKind.Standalone, "BROWSER_BACK");
        Add(0xA7, "BrowserForward", HotkeyKeyKind.Standalone, "BROWSER_FORWARD");
        Add(0xA8, "BrowserRefresh", HotkeyKeyKind.Standalone, "BROWSER_REFRESH");
        Add(0xA9, "BrowserStop", HotkeyKeyKind.Standalone, "BROWSER_STOP");
        Add(0xAA, "BrowserSearch", HotkeyKeyKind.Standalone, "BROWSER_SEARCH");
        Add(0xAB, "BrowserFavorites", HotkeyKeyKind.Standalone, "BROWSER_FAVORITES");
        Add(0xAC, "BrowserHome", HotkeyKeyKind.Standalone, "BROWSER_HOME");
        Add(0xAD, "VolumeMute", HotkeyKeyKind.Standalone, "VOLUME_MUTE", "Mute");
        Add(0xAE, "VolumeDown", HotkeyKeyKind.Standalone, "VOLUME_DOWN");
        Add(0xAF, "VolumeUp", HotkeyKeyKind.Standalone, "VOLUME_UP");
        Add(0xB0, "MediaNext", HotkeyKeyKind.Standalone, "MEDIA_NEXT_TRACK", "MediaNextTrack", "NextTrack");
        Add(0xB1, "MediaPrevious", HotkeyKeyKind.Standalone, "MEDIA_PREV_TRACK", "MediaPrevTrack", "MediaPreviousTrack", "PrevTrack", "PreviousTrack");
        Add(0xB2, "MediaStop", HotkeyKeyKind.Standalone, "MEDIA_STOP");
        Add(0xB3, "MediaPlayPause", HotkeyKeyKind.Standalone, "MEDIA_PLAY_PAUSE", "PlayPause");
        Add(0xB4, "LaunchMail", HotkeyKeyKind.Standalone, "LAUNCH_MAIL", "Mail");
        Add(0xB5, "LaunchMedia", HotkeyKeyKind.Standalone, "LAUNCH_MEDIA_SELECT", "LaunchMediaSelect", "MediaSelect");
        Add(0xB6, "LaunchApp1", HotkeyKeyKind.Standalone, "LAUNCH_APP1", "App1");
        Add(0xB7, "LaunchApp2", HotkeyKeyKind.Standalone, "LAUNCH_APP2", "App2");
        Add(0xF6, "Attn", HotkeyKeyKind.Standalone, "ATTN");
        Add(0xF7, "CrSel", HotkeyKeyKind.Standalone, "CRSEL");
        Add(0xF8, "ExSel", HotkeyKeyKind.Standalone, "EXSEL");
        Add(0xF9, "EraseEof", HotkeyKeyKind.Standalone, "EREOF");
        Add(0xFA, "Play", HotkeyKeyKind.Standalone, "PLAY");
        Add(0xFB, "Zoom", HotkeyKeyKind.Standalone, "ZOOM");
        Add(0xFD, "PA1", HotkeyKeyKind.Standalone, "PA1");
        Add(0xFE, "OemClear", HotkeyKeyKind.Standalone, "OEM_CLEAR");

        // Letters, digits and F1–F24: named by KeyName itself; listed for their kinds (and VK_ aliases for the F keys).
        for (uint vk = 'A'; vk <= 'Z'; vk++)
        {
            Add(vk, null, HotkeyKeyKind.Typing);
        }

        for (uint vk = '0'; vk <= '9'; vk++)
        {
            Add(vk, null, HotkeyKeyKind.Typing);
        }

        for (uint vk = 0x70; vk <= 0x87; vk++)
        {
            // Passed as the Win32 name, so "VK_F13" parses too ("F13" itself is read before the index is asked).
            Add(vk, null, HotkeyKeyKind.Function, "F" + (vk - 0x70 + 1).ToString(CultureInfo.InvariantCulture));
        }

        return table;
    }

    /// <summary>One row of the key table.</summary>
    /// <param name="VirtualKey">The virtual-key code.</param>
    /// <param name="Name">The canonical name, or <see langword="null"/> when <see cref="KeyName"/> names it (letters, digits, F keys, <c>0xNN</c>).</param>
    /// <param name="Kind">What the key does on its own.</param>
    /// <param name="Aliases">Other accepted names (folded with <see cref="NormalizeName"/> on lookup).</param>
    private sealed record KeyInfo(uint VirtualKey, string? Name, HotkeyKeyKind Kind, string[] Aliases)
    {
        /// <summary>The canonical name (when the table has one) followed by the aliases.</summary>
        /// <returns>Every name that parses to this key through the index.</returns>
        public IEnumerable<string> AllNames() => Name is null ? Aliases : Aliases.Prepend(Name);
    }
}
