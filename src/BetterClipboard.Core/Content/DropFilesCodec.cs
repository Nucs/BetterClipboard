using System.Buffers.Binary;
using System.Text;

namespace BetterClipboard.Core.Content;

/// <summary>
/// Encodes/decodes <c>CF_HDROP</c> payloads: a 20-byte <c>DROPFILES</c> header followed by a
/// double-NUL-terminated list of NUL-terminated paths (UTF-16 when <c>fWide</c> is set, ANSI otherwise).
/// </summary>
/// <remarks>
/// Layout (all little-endian): <c>DWORD pFiles</c> (offset of the path list), <c>POINT pt</c> (8 bytes,
/// drop point — irrelevant for clipboard), <c>BOOL fNC</c>, <c>BOOL fWide</c>. Parsing never trusts the
/// producer: offsets are bounds-checked and malformed input yields an empty list instead of an exception.
/// </remarks>
public static class DropFilesCodec
{
    /// <summary>Size of the <c>DROPFILES</c> header in bytes.</summary>
    public const int HeaderSize = 20;

    /// <summary>
    /// Parses the file paths out of a <c>CF_HDROP</c> payload.
    /// </summary>
    /// <param name="data">Raw <c>CF_HDROP</c> bytes.</param>
    /// <returns>The paths in producer order; empty when the payload is malformed or lists no files.</returns>
    public static IReadOnlyList<string> Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
        {
            return [];
        }

        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data);
        bool wide = BinaryPrimitives.ReadInt32LittleEndian(data[16..]) != 0;
        if (offset < HeaderSize || offset >= data.Length)
        {
            return [];
        }

        var list = data[(int)offset..];
        var paths = new List<string>();
        if (wide)
        {
            // Walk UTF-16 code units; each path ends at a NUL unit and the list ends at an empty path.
            int start = 0;
            for (int i = 0; i + 1 < list.Length; i += 2)
            {
                if (list[i] != 0 || list[i + 1] != 0)
                {
                    continue;
                }

                if (i == start)
                {
                    break;
                }

                paths.Add(Encoding.Unicode.GetString(list[start..i]));
                start = i + 2;
            }
        }
        else
        {
            // ANSI lists are rare (pre-Unicode apps). Core cannot know the producer's ANSI code page, so
            // Latin-1 is used as a lossless byte→char fallback: ASCII paths are exact, non-ASCII names may
            // render wrong. Modern producers (Explorer, file dialogs) always set fWide.
            int start = 0;
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] != 0)
                {
                    continue;
                }

                if (i == start)
                {
                    break;
                }

                paths.Add(Encoding.Latin1.GetString(list[start..i]));
                start = i + 1;
            }
        }

        return paths;
    }

    /// <summary>
    /// Builds a <c>CF_HDROP</c> payload (wide/UTF-16 form) for the given paths.
    /// </summary>
    /// <param name="paths">Absolute file-system paths; order is preserved.</param>
    /// <returns>A payload Explorer and file dialogs accept for paste.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A path is null, empty or contains a NUL character (it would split the list).</exception>
    public static byte[] Encode(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var builder = new StringBuilder();
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path) || path.Contains('\0'))
            {
                throw new ArgumentException("Drop-file paths must be non-empty and must not contain NUL.", nameof(paths));
            }

            builder.Append(path).Append('\0');
        }

        // Final empty string terminates the list (double NUL).
        builder.Append('\0');
        var listBytes = Encoding.Unicode.GetBytes(builder.ToString());
        var result = new byte[HeaderSize + listBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16), 1);
        listBytes.CopyTo(result, HeaderSize);
        return result;
    }
}
