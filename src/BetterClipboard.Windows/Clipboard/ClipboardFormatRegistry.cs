using System.Collections.Concurrent;
using BetterClipboard.Core.Model;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Clipboard;

/// <summary>
/// Maps clipboard format IDs to the canonical names BetterClipboard persists, and back.
/// </summary>
/// <remarks>
/// Standard formats (1–17) have fixed IDs and no registered name, so they get <c>CF_*</c> names here.
/// Registered formats (0xC000–0xFFFF) are session atoms: the same name maps to a different ID after a
/// reboot, which is why only names are stored and IDs are re-resolved (and re-registered — idempotent)
/// at paste time. Private (0x0200–0x02FF) and GDI-object (0x0300–0x03FF) ranges are meaningful only to
/// the process that set them and are never captured.
/// </remarks>
internal static class ClipboardFormatRegistry
{
    /// <summary><c>CF_TEXT</c>: ANSI text, synthesized from <c>CF_UNICODETEXT</c>.</summary>
    internal const uint CF_TEXT = 1;
    /// <summary><c>CF_BITMAP</c>: GDI HBITMAP handle (not HGLOBAL memory).</summary>
    internal const uint CF_BITMAP = 2;
    /// <summary><c>CF_METAFILEPICT</c>: metafile picture handle (not HGLOBAL memory).</summary>
    internal const uint CF_METAFILEPICT = 3;
    /// <summary><c>CF_OEMTEXT</c>: OEM text, synthesized.</summary>
    internal const uint CF_OEMTEXT = 7;
    /// <summary><c>CF_DIB</c>.</summary>
    internal const uint CF_DIB = 8;
    /// <summary><c>CF_PALETTE</c>: GDI palette handle.</summary>
    internal const uint CF_PALETTE = 9;
    /// <summary><c>CF_UNICODETEXT</c>.</summary>
    internal const uint CF_UNICODETEXT = 13;
    /// <summary><c>CF_ENHMETAFILE</c>: HENHMETAFILE handle.</summary>
    internal const uint CF_ENHMETAFILE = 14;
    /// <summary><c>CF_HDROP</c>.</summary>
    internal const uint CF_HDROP = 15;
    /// <summary><c>CF_LOCALE</c>.</summary>
    internal const uint CF_LOCALE = 16;
    /// <summary><c>CF_DIBV5</c>.</summary>
    internal const uint CF_DIBV5 = 17;

    private static readonly Dictionary<uint, string> StandardNames = new()
    {
        [1] = "CF_TEXT", [2] = "CF_BITMAP", [3] = "CF_METAFILEPICT", [4] = "CF_SYLK", [5] = "CF_DIF",
        [6] = "CF_TIFF", [7] = "CF_OEMTEXT", [8] = ClipFormatNames.Dib, [9] = "CF_PALETTE", [10] = "CF_PENDATA",
        [11] = "CF_RIFF", [12] = "CF_WAVE", [13] = ClipFormatNames.UnicodeText, [14] = "CF_ENHMETAFILE",
        [15] = ClipFormatNames.HDrop, [16] = ClipFormatNames.Locale, [17] = ClipFormatNames.DibV5,
    };

    private static readonly Dictionary<string, uint> StandardIds =
        StandardNames.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<uint, string> RegisteredNames = new();
    private static readonly ConcurrentDictionary<string, uint> RegisteredIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Resolves a format ID to its canonical name.
    /// </summary>
    /// <param name="id">Format ID from <c>EnumClipboardFormats</c>.</param>
    /// <returns>The canonical name, or <see langword="null"/> for private/GDI/display formats that must not be captured.</returns>
    public static string? GetName(uint id)
    {
        if (StandardNames.TryGetValue(id, out var standard))
        {
            return standard;
        }

        if (id < 0xC000)
        {
            // 0x0080–0x008E display formats, 0x0200–0x03FF private/GDI ranges: owner-specific, not portable.
            return null;
        }

        if (RegisteredNames.TryGetValue(id, out var cached))
        {
            return cached;
        }

        // Registered format names are atoms, limited to 255 chars.
        Span<char> buffer = stackalloc char[256];
        int length;
        unsafe
        {
            fixed (char* pointer = buffer)
            {
                length = GetClipboardFormatName(id, pointer, buffer.Length);
            }
        }

        if (length <= 0)
        {
            return null;
        }

        var name = new string(buffer[..length]);
        RegisteredNames[id] = name;
        RegisteredIds[name] = id;
        return name;
    }

    /// <summary>
    /// Resolves a canonical name to a format ID, registering the name when needed.
    /// </summary>
    /// <param name="name">Canonical format name.</param>
    /// <returns>The ID, or 0 when registration failed (the atom table is exhausted — practically never).</returns>
    public static uint GetOrRegisterId(string name)
    {
        if (StandardIds.TryGetValue(name, out var standard))
        {
            return standard;
        }

        if (RegisteredIds.TryGetValue(name, out var cached))
        {
            return cached;
        }

        uint id = RegisterClipboardFormat(name);
        if (id != 0)
        {
            RegisteredIds[name] = id;
            RegisteredNames[id] = name;
        }

        return id;
    }

    /// <summary>
    /// Returns whether a format's handle is a GDI/metafile object rather than HGLOBAL memory. Such
    /// handles cannot be copied as bytes and are always synthesized from a memory format we do keep
    /// (<c>CF_BITMAP</c> ← <c>CF_DIB</c>, <c>CF_ENHMETAFILE</c> ← <c>CF_METAFILEPICT</c>).
    /// </summary>
    /// <param name="id">Format ID.</param>
    /// <returns><see langword="true"/> for handle formats.</returns>
    public static bool IsHandleFormat(uint id) =>
        id is CF_BITMAP or CF_METAFILEPICT or CF_PALETTE or CF_ENHMETAFILE or 0x0080 or 0x0082 or 0x0083 or 0x008E
        || id is >= 0x0300 and <= 0x03FF;
}
