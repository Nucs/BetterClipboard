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
/// A global keyboard shortcut such as <c>Win+V</c>: modifiers plus one virtual key.
/// </summary>
/// <remarks>
/// Parsing accepts user-friendly names (<c>Ctrl</c>/<c>Control</c>, <c>Win</c>/<c>Windows</c>, letters, digits,
/// <c>F1</c>–<c>F24</c>, <c>Space</c>, <c>Insert</c>, punctuation such as <c>`</c>) case-insensitively;
/// <see cref="ToString"/> produces the canonical form <c>Ctrl+Alt+Shift+Win+Key</c> used in settings.
/// A gesture without modifiers is rejected unless the key is a function key — a bare letter as a global
/// hotkey would make that letter untypable system-wide.
/// </remarks>
/// <param name="Modifiers">Modifier keys.</param>
/// <param name="VirtualKey">Win32 virtual-key code of the main key.</param>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey)
{
    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Esc"] = 0x1B, ["Escape"] = 0x1B,
        ["Insert"] = 0x2D, ["Ins"] = 0x2D, ["Delete"] = 0x2E, ["Del"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
        ["`"] = 0xC0, ["Backtick"] = 0xC0, ["Tilde"] = 0xC0, [";"] = 0xBA, ["="] = 0xBB, [","] = 0xBC, ["-"] = 0xBD,
        ["."] = 0xBE, ["/"] = 0xBF, ["["] = 0xDB, ["\\"] = 0xDC, ["]"] = 0xDD, ["'"] = 0xDE,
    };

    private static readonly Dictionary<uint, string> KeyNames = new()
    {
        [0x20] = "Space", [0x0D] = "Enter", [0x09] = "Tab", [0x1B] = "Esc", [0x2D] = "Insert", [0x2E] = "Delete",
        [0x24] = "Home", [0x23] = "End", [0x21] = "PageUp", [0x22] = "PageDown", [0x26] = "Up", [0x28] = "Down",
        [0x25] = "Left", [0x27] = "Right", [0xC0] = "`", [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-",
        [0xBE] = ".", [0xBF] = "/", [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]", [0xDE] = "'",
    };

    /// <summary>Whether the gesture includes the Windows key (such combos are usually owned by Explorer).</summary>
    public bool UsesWinKey => (Modifiers & HotkeyModifiers.Win) != 0;

    /// <summary>
    /// Parses a gesture.
    /// </summary>
    /// <param name="text">Text such as <c>Win+V</c>, <c>ctrl + shift + v</c> or <c>Ctrl+`</c>.</param>
    /// <param name="gesture">The parsed gesture.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is a valid, safe global hotkey.</returns>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Split on '+' but allow the '+' key itself only as "Plus" (keeps parsing unambiguous).
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var modifier = ParseModifier(parts[i]);
            if (modifier == HotkeyModifiers.None || (modifiers & modifier) != 0)
            {
                return false;
            }

            modifiers |= modifier;
        }

        if (!TryParseKey(parts[^1], out var key))
        {
            return false;
        }

        bool isFunctionKey = key is >= 0x70 and <= 0x87;
        if (modifiers == HotkeyModifiers.None && !isFunctionKey)
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, key);
        return true;
    }

    /// <summary>
    /// Formats the gesture canonically (<c>Ctrl+Alt+Shift+Win+Key</c>).
    /// </summary>
    /// <returns>The display/settings string.</returns>
    public override string ToString()
    {
        var builder = new StringBuilder();
        if ((Modifiers & HotkeyModifiers.Control) != 0) builder.Append("Ctrl+");
        if ((Modifiers & HotkeyModifiers.Alt) != 0) builder.Append("Alt+");
        if ((Modifiers & HotkeyModifiers.Shift) != 0) builder.Append("Shift+");
        if ((Modifiers & HotkeyModifiers.Win) != 0) builder.Append("Win+");
        builder.Append(KeyName(VirtualKey));
        return builder.ToString();
    }

    /// <summary>Parses one modifier token.</summary>
    /// <param name="token">Token text.</param>
    /// <returns>The modifier, or <see cref="HotkeyModifiers.None"/> when unknown.</returns>
    private static HotkeyModifiers ParseModifier(string token) => token.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" or "CTL" => HotkeyModifiers.Control,
        "ALT" or "MENU" => HotkeyModifiers.Alt,
        "SHIFT" => HotkeyModifiers.Shift,
        "WIN" or "WINDOWS" or "META" or "SUPER" or "CMD" => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };

    /// <summary>Parses the main key token.</summary>
    /// <param name="token">Token text.</param>
    /// <param name="key">Virtual-key code.</param>
    /// <returns><see langword="true"/> for a known key.</returns>
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

        if (string.Equals(token, "Plus", StringComparison.OrdinalIgnoreCase))
        {
            key = 0xBB;
            return true;
        }

        return NamedKeys.TryGetValue(token, out key);
    }

    /// <summary>Formats a virtual key.</summary>
    /// <param name="vk">Virtual-key code.</param>
    /// <returns>Its display name.</returns>
    private static string KeyName(uint vk)
    {
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return ((char)vk).ToString();
        }

        if (vk is >= 0x70 and <= 0x87)
        {
            return "F" + (vk - 0x70 + 1).ToString(CultureInfo.InvariantCulture);
        }

        return KeyNames.TryGetValue(vk, out var name) ? name : $"0x{vk:X2}";
    }
}
