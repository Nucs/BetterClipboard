using System.Globalization;
using System.Text.RegularExpressions;

namespace BetterClipboard.Core.Content;

/// <summary>
/// Recognizes a clip that is exactly one color literal (<c>#RGB</c>, <c>#RGBA</c>, <c>#RRGGBB</c>,
/// <c>#AARRGGBB</c>, <c>rgb(r,g,b)</c>, <c>rgba(r,g,b,a)</c>) so the UI can render a swatch.
/// </summary>
/// <remarks>
/// Eight-digit hex is ambiguous between CSS (<c>#RRGGBBAA</c>) and XAML/Android (<c>#AARRGGBB</c>);
/// we follow XAML because BetterClipboard's audience copies from Windows tooling. A wrong guess only
/// affects the swatch preview, never the pasted text.
/// </remarks>
public static partial class ColorLiteral
{
    /// <summary>
    /// Tries to parse <paramref name="text"/> as a single color literal (surrounding whitespace allowed).
    /// </summary>
    /// <param name="text">Candidate text; long or multi-line text is rejected quickly.</param>
    /// <param name="color">The parsed color as ARGB bytes; default when parsing fails.</param>
    /// <returns><see langword="true"/> when the whole text is one color literal.</returns>
    public static bool TryParse(string? text, out (byte A, byte R, byte G, byte B) color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40)
        {
            return false;
        }

        var trimmed = text.Trim();
        var hex = HexPattern().Match(trimmed);
        if (hex.Success)
        {
            var digits = hex.Groups[1].Value;
            switch (digits.Length)
            {
                case 3:
                case 4:
                    // Short forms double each nibble: #1af == #11aaff. Four digits are CSS #RGBA.
                    byte r = Nibble(digits[0]), g = Nibble(digits[1]), b = Nibble(digits[2]);
                    byte a = digits.Length == 4 ? Nibble(digits[3]) : (byte)0xFF;
                    color = (a, r, g, b);
                    return true;
                case 6:
                    color = (0xFF, Byte(digits, 0), Byte(digits, 2), Byte(digits, 4));
                    return true;
                case 8:
                    color = (Byte(digits, 0), Byte(digits, 2), Byte(digits, 4), Byte(digits, 6));
                    return true;
            }

            return false;
        }

        var rgb = RgbPattern().Match(trimmed);
        if (!rgb.Success)
        {
            return false;
        }

        if (!TryChannel(rgb.Groups[1].Value, out var red) ||
            !TryChannel(rgb.Groups[2].Value, out var green) ||
            !TryChannel(rgb.Groups[3].Value, out var blue))
        {
            return false;
        }

        byte alpha = 0xFF;
        if (rgb.Groups[4].Success)
        {
            if (!double.TryParse(rgb.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var alphaValue) ||
                alphaValue is < 0 or > 1)
            {
                return false;
            }

            alpha = (byte)Math.Round(alphaValue * 255);
        }

        color = (alpha, red, green, blue);
        return true;
    }

    /// <summary>Expands one hex digit to a full byte (<c>a</c> → <c>0xAA</c>).</summary>
    /// <param name="c">A hex digit already validated by the regex.</param>
    /// <returns>The doubled nibble.</returns>
    private static byte Nibble(char c)
    {
        int v = Convert.ToInt32(c.ToString(), 16);
        return (byte)((v << 4) | v);
    }

    /// <summary>Parses two hex digits at <paramref name="index"/>.</summary>
    /// <param name="digits">Validated hex digits.</param>
    /// <param name="index">Offset of the first digit.</param>
    /// <returns>The byte value.</returns>
    private static byte Byte(string digits, int index) =>
        byte.Parse(digits.AsSpan(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>Parses a decimal 0–255 color channel.</summary>
    /// <param name="value">Digits captured by the regex.</param>
    /// <param name="channel">The channel value when in range.</param>
    /// <returns><see langword="true"/> when the value is within 0–255.</returns>
    private static bool TryChannel(string value, out byte channel) =>
        byte.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out channel);

    [GeneratedRegex(@"^#([0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.CultureInvariant)]
    private static partial Regex HexPattern();

    [GeneratedRegex(@"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*(\d*\.?\d+)\s*)?\)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RgbPattern();
}
