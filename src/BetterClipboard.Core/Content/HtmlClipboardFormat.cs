using System.Globalization;
using System.Text;

namespace BetterClipboard.Core.Content;

/// <summary>
/// Reads Windows' <c>HTML Format</c> (CF_HTML) payload: a UTF-8 document preceded by an ASCII header
/// (<c>Version:</c>, <c>StartHTML:</c>, <c>EndHTML:</c>, <c>StartFragment:</c>, <c>EndFragment:</c>) whose
/// offsets are <b>byte</b> positions into the whole payload.
/// </summary>
/// <remarks>
/// Producers are sloppy — offsets of <c>-1</c>, missing keys, offsets past the end, a trailing NUL — so every
/// step degrades gracefully: fragment → whole HTML → everything after the header, never an exception.
/// </remarks>
public static class HtmlClipboardFormat
{
    /// <summary>
    /// Extracts what the user actually copied (the fragment), falling back to the full HTML.
    /// </summary>
    /// <param name="data">The raw CF_HTML bytes.</param>
    /// <returns>The HTML text (possibly empty).</returns>
    public static string ExtractFragment(ReadOnlySpan<byte> data)
    {
        // Some producers include the terminating NUL in the payload.
        int length = data.IndexOf((byte)0);
        if (length >= 0)
        {
            data = data[..length];
        }

        var header = ReadHeader(data, out int headerEnd);
        if (TrySlice(data, header, "StartFragment", "EndFragment", out var fragment) ||
            TrySlice(data, header, "StartHTML", "EndHTML", out fragment))
        {
            return Encoding.UTF8.GetString(fragment);
        }

        return Encoding.UTF8.GetString(data[headerEnd..]);
    }

    /// <summary>Parses the leading <c>Key:Value</c> lines.</summary>
    /// <param name="data">Payload.</param>
    /// <param name="headerEnd">Byte offset just past the header lines.</param>
    /// <returns>Numeric header values by key (case-insensitive).</returns>
    private static Dictionary<string, long> ReadHeader(ReadOnlySpan<byte> data, out int headerEnd)
    {
        var values = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        int position = 0;
        while (position < data.Length && data[position] != (byte)'<')
        {
            int lineEnd = data[position..].IndexOfAny((byte)'\r', (byte)'\n');
            var line = lineEnd < 0 ? data[position..] : data.Slice(position, lineEnd);
            int colon = line.IndexOf((byte)':');
            if (colon <= 0)
            {
                break; // not a header line: the document starts here
            }

            var key = Encoding.ASCII.GetString(line[..colon]).Trim();
            if (long.TryParse(Encoding.ASCII.GetString(line[(colon + 1)..]).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
            {
                values[key] = value;
            }

            position += lineEnd < 0 ? line.Length : lineEnd;
            while (position < data.Length && data[position] is (byte)'\r' or (byte)'\n')
            {
                position++;
            }
        }

        headerEnd = position;
        return values;
    }

    /// <summary>Slices <c>[start, end)</c> when both header offsets are present and sane.</summary>
    /// <param name="data">Payload.</param>
    /// <param name="header">Parsed header.</param>
    /// <param name="startKey">Start offset key.</param>
    /// <param name="endKey">End offset key.</param>
    /// <param name="slice">The slice when valid.</param>
    /// <returns>Whether a non-empty, in-bounds slice was found.</returns>
    private static bool TrySlice(ReadOnlySpan<byte> data, Dictionary<string, long> header, string startKey, string endKey, out ReadOnlySpan<byte> slice)
    {
        slice = default;
        if (!header.TryGetValue(startKey, out long start) || !header.TryGetValue(endKey, out long end) ||
            start < 0 || end <= start || start >= data.Length)
        {
            return false;
        }

        slice = data[(int)start..(int)Math.Min(end, data.Length)];
        return true;
    }
}
