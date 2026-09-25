using System.Runtime.InteropServices;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Clipboard;

/// <summary>Outcome of reading the clipboard.</summary>
/// <param name="Formats">Copied formats in enumeration (replay) order.</param>
/// <param name="ExcludedByProducer">The producer marked the copy private (password managers); nothing was copied.</param>
internal sealed record ClipboardReadResult(IReadOnlyList<ClipFormatData> Formats, bool ExcludedByProducer);

/// <summary>
/// Copies clipboard content into managed memory. Every method requires the clipboard to be <b>open</b>
/// on the calling thread and never closes it — the caller owns the Open/Close pair and should keep the
/// window between them as short as possible (other apps cannot copy or paste meanwhile).
/// </summary>
/// <remarks>
/// <para>
/// <b>Selection.</b> In portable mode only formats every consumer understands are kept: Unicode text, HTML,
/// RTF, one of DIB/DIBV5 (whichever the producer placed first — the other one is synthesized), PNG,
/// the file list with its drop effect, and the browser URL. With <see cref="CaptureOptions.PreserveAllFormats"/>
/// every other HGLOBAL format is added afterwards, lowest priority first to lose when the byte budget runs out.
/// </para>
/// <para>
/// <b>Privacy.</b> The documented opt-out markers (<c>ExcludeClipboardContentFromMonitorProcessing</c>,
/// <c>CanIncludeInClipboardHistory</c> = 0) and the legacy <c>Clipboard Viewer Ignore</c> convention are checked
/// <i>before</i> any content is read, so secrets from password managers are never copied into our memory.
/// </para>
/// </remarks>
internal static class ClipboardReader
{
    /// <summary>
    /// Reads the currently open clipboard.
    /// </summary>
    /// <param name="options">What to copy and the byte budget.</param>
    /// <returns>The copied formats, or an exclusion result.</returns>
    public static ClipboardReadResult Read(CaptureOptions options)
    {
        if (IsExcludedByProducer())
        {
            return new ClipboardReadResult([], ExcludedByProducer: true);
        }

        // Snapshot the enumeration once; order = producer's order of descriptiveness (+ synthesized tail).
        var available = new List<(uint Id, string Name)>();
        for (uint id = EnumClipboardFormats(0); id != 0; id = EnumClipboardFormats(id))
        {
            if (ClipboardFormatRegistry.IsHandleFormat(id))
            {
                continue;
            }

            var name = ClipboardFormatRegistry.GetName(id);
            if (name is not null && !ClipFormatNames.IsPrivacyMarker(name))
            {
                available.Add((id, name));
            }
        }

        bool hasUnicode = available.Exists(f => f.Id == ClipboardFormatRegistry.CF_UNICODETEXT);
        bool hasHDrop = available.Exists(f => f.Id == ClipboardFormatRegistry.CF_HDROP);

        // Whichever DIB flavor comes first is the producer's original; the second is synthesized by the
        // system and would just double the stored bytes.
        uint? dibFlavor = available.Where(f => f.Id is ClipboardFormatRegistry.CF_DIB or ClipboardFormatRegistry.CF_DIBV5)
            .Select(f => (uint?)f.Id).FirstOrDefault();

        var chosen = new List<(int Order, ClipFormatData Data)>();
        long budget = options.MaxItemBytes;

        // Pass 1: portable formats, in priority order (text first — it is what most pastes use).
        foreach (var (order, format) in Prioritized(available, hasHDrop, dibFlavor, options))
        {
            if (TryCopy(format.Id, format.Name, ref budget) is { } data)
            {
                chosen.Add((order, data));
            }
        }

        // Pass 2: app-private formats, only when asked and only within the remaining budget.
        if (options.PreserveAllFormats)
        {
            for (int i = 0; i < available.Count; i++)
            {
                var (id, name) = available[i];
                if (chosen.Exists(c => string.Equals(c.Data.Name, name, StringComparison.Ordinal)) ||
                    IsPortable(id, name) ||
                    (hasUnicode && id is ClipboardFormatRegistry.CF_TEXT or ClipboardFormatRegistry.CF_OEMTEXT) ||
                    id is ClipboardFormatRegistry.CF_DIB or ClipboardFormatRegistry.CF_DIBV5 ||
                    id == ClipboardFormatRegistry.CF_LOCALE ||
                    (!options.CaptureImages && ClipFormatNames.IsImage(name)))
                {
                    continue;
                }

                if (TryCopy(id, name, ref budget) is { } data)
                {
                    chosen.Add((i, data));
                }
            }
        }

        var formats = chosen.OrderBy(c => c.Order).Select(c => c.Data).ToList();
        return new ClipboardReadResult(formats, ExcludedByProducer: false);
    }

    /// <summary>
    /// Returns whether the producer asked clipboard monitors to ignore this copy.
    /// </summary>
    /// <returns><see langword="true"/> for private copies.</returns>
    internal static bool IsExcludedByProducer()
    {
        if (IsAvailable(ClipFormatNames.ExcludeFromMonitor) || IsAvailable(ClipFormatNames.ViewerIgnore))
        {
            return true;
        }

        uint canInclude = ClipboardFormatRegistry.GetOrRegisterId(ClipFormatNames.CanIncludeInHistory);
        if (canInclude != 0 && IsClipboardFormatAvailable(canInclude))
        {
            // DWORD 0 = "do not include". Anything unreadable is treated as exclusion: when in doubt about
            // a secret, don't record it. (64-byte cap: GlobalSize may round a 4-byte allocation up.)
            var value = CopyHandle(GetClipboardData(canInclude), 64);
            return value is not { Length: >= 4 } || BitConverter.ToUInt32(value, 0) == 0;
        }

        return false;
    }

    /// <summary>Yields the portable formats present, with their original enumeration index for ordering.</summary>
    /// <param name="available">Enumerated formats.</param>
    /// <param name="hasHDrop">Whether a file list is present.</param>
    /// <param name="dibFlavor">The original DIB flavor, if any.</param>
    /// <param name="options">Capture options.</param>
    /// <returns>Formats to copy, highest priority first.</returns>
    private static IEnumerable<(int Order, (uint Id, string Name) Format)> Prioritized(
        List<(uint Id, string Name)> available, bool hasHDrop, uint? dibFlavor, CaptureOptions options)
    {
        var wanted = new List<string> { ClipFormatNames.UnicodeText };
        if (options.CaptureFiles && hasHDrop)
        {
            wanted.Add(ClipFormatNames.HDrop);
            wanted.Add(ClipFormatNames.PreferredDropEffect);
        }

        wanted.Add(ClipFormatNames.Html);
        wanted.Add(ClipFormatNames.Rtf);
        wanted.Add(ClipFormatNames.UrlW);
        if (options.CaptureImages)
        {
            wanted.Add(ClipFormatNames.Png);
            wanted.Add(ClipFormatNames.PngMime);
            if (dibFlavor is { } flavor)
            {
                wanted.Add(flavor == ClipboardFormatRegistry.CF_DIBV5 ? ClipFormatNames.DibV5 : ClipFormatNames.Dib);
            }
        }

        foreach (var name in wanted)
        {
            int index = available.FindIndex(f => string.Equals(f.Name, name, StringComparison.Ordinal));
            if (index >= 0)
            {
                yield return (index, available[index]);
            }
        }
    }

    /// <summary>Whether a format belongs to the portable set handled by pass 1.</summary>
    /// <param name="id">Format id.</param>
    /// <param name="name">Format name.</param>
    /// <returns><see langword="true"/> for portable formats.</returns>
    private static bool IsPortable(uint id, string name) =>
        id is ClipboardFormatRegistry.CF_UNICODETEXT or ClipboardFormatRegistry.CF_HDROP
        || name is ClipFormatNames.Html or ClipFormatNames.Rtf or ClipFormatNames.UrlW or ClipFormatNames.Png
            or ClipFormatNames.PngMime or ClipFormatNames.PreferredDropEffect;

    /// <summary>Copies one format if it fits the remaining budget.</summary>
    /// <param name="id">Format id.</param>
    /// <param name="name">Canonical name.</param>
    /// <param name="budget">Remaining byte budget; reduced on success.</param>
    /// <returns>The copied data, or <see langword="null"/> when unavailable, not memory-backed, or over budget.</returns>
    private static ClipFormatData? TryCopy(uint id, string name, ref long budget)
    {
        // GetClipboardData renders delayed formats synchronously in the producer; 0 means it declined.
        nint handle = GetClipboardData(id);
        if (handle == 0)
        {
            return null;
        }

        var bytes = CopyHandle(handle, budget);
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        if (id == ClipboardFormatRegistry.CF_UNICODETEXT)
        {
            // HGLOBALs are often larger than their string (allocation rounding, garbage after the NUL);
            // store exactly "text + NUL" so equal texts are byte-identical on disk.
            bytes = UnicodeTextCodec.Encode(UnicodeTextCodec.Decode(bytes));
        }

        budget -= bytes.Length;
        return new ClipFormatData(name, bytes);
    }

    /// <summary>Copies an HGLOBAL's bytes, refusing blocks larger than <paramref name="maxBytes"/>.</summary>
    /// <param name="handle">Clipboard-owned handle (never freed here).</param>
    /// <param name="maxBytes">Upper bound.</param>
    /// <returns>The bytes, or <see langword="null"/> when the handle is not lockable memory or too large.</returns>
    private static byte[]? CopyHandle(nint handle, long maxBytes)
    {
        if (handle == 0)
        {
            return null;
        }

        // GlobalSize is 0 for non-HGLOBAL handles (e.g. OLE formats exposed via IStream/IStorage).
        long size = (long)GlobalSize(handle);
        if (size <= 0 || size > maxBytes || size > Array.MaxLength)
        {
            return null;
        }

        nint pointer = GlobalLock(handle);
        if (pointer == 0)
        {
            return null;
        }

        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, (int)size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    /// <summary>Whether a registered format with <paramref name="name"/> is on the clipboard.</summary>
    /// <param name="name">Registered format name.</param>
    /// <returns><see langword="true"/> when present.</returns>
    private static bool IsAvailable(string name)
    {
        uint id = ClipboardFormatRegistry.GetOrRegisterId(name);
        return id != 0 && IsClipboardFormatAvailable(id);
    }
}
