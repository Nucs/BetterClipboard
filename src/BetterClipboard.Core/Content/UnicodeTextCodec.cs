using System.Text;

namespace BetterClipboard.Core.Content;

/// <summary>
/// Encodes/decodes the <c>CF_UNICODETEXT</c> wire format: UTF-16LE code units terminated by a NUL
/// character. Pure byte logic so it is shared by the listener, the importers and the tests.
/// </summary>
public static class UnicodeTextCodec
{
    /// <summary>
    /// Decodes clipboard text, stopping at the first NUL character.
    /// </summary>
    /// <remarks>
    /// The HGLOBAL backing a clipboard format is frequently larger than the string (allocators round
    /// up, and some apps leave garbage after the terminator), so everything after the first NUL is ignored.
    /// An odd trailing byte (malformed data) is dropped rather than throwing, because clipboard content
    /// comes from arbitrary third-party processes.
    /// </remarks>
    /// <param name="data">Raw <c>CF_UNICODETEXT</c> bytes (may be empty).</param>
    /// <returns>The decoded string; <see cref="string.Empty"/> for empty input.</returns>
    public static string Decode(ReadOnlySpan<byte> data)
    {
        // Scan whole UTF-16 code units for the terminator; a NUL byte inside a code unit (e.g. 'A' = 41 00)
        // must not be mistaken for the end of the string.
        int usable = data.Length & ~1;
        int end = usable;
        for (int i = 0; i + 1 < usable; i += 2)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                end = i;
                break;
            }
        }

        return Encoding.Unicode.GetString(data[..end]);
    }

    /// <summary>
    /// Encodes text into clipboard wire format, appending the mandatory NUL terminator.
    /// </summary>
    /// <param name="text">Text to encode; embedded NULs are preserved but readers will stop at the first one.</param>
    /// <returns>UTF-16LE bytes including a two-byte terminator.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static byte[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = new byte[(text.Length + 1) * 2];
        Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
        return bytes;
    }
}
