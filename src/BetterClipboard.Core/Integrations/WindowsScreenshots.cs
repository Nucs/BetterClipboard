using System.Text;
using System.Text.RegularExpressions;
using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.Integrations;

/// <summary>Which of Windows' screenshot tools a file in the Screenshots folder came from, judged by its name alone.</summary>
public enum ScreenshotTool
{
    /// <summary>Snipping Tool's auto-save: <c>"&lt;prefix&gt; yyyy-MM-dd HHmmss.png"</c> (Win+Shift+S, PrtScn, the app itself).</summary>
    SnippingTool,

    /// <summary>Win+PrtScn (twinui.dll in Explorer): <c>"&lt;prefix&gt; (N).png"</c>, N from <c>ScreenshotIndex</c>.</summary>
    WinPrtScn,

    /// <summary>Any other image in the folder: another tool, OneDrive, or a file saved there by hand.</summary>
    Other,
}

/// <summary>
/// Pure rules of the Windows screenshots integration (the panel's Snipping tab; CLAUDE.md §2.18 has the verified facts):
/// what counts as a screenshot file, which tool made it, when a file is completely written, and what Snipping Tool's
/// own settings say. The Windows side watches the folder and calls these.
/// </summary>
/// <remarks>
/// <para>
/// <b>Names are localized.</b> Both tools build the file name from a translated word ("Screenshot" in English; a
/// right-to-left UI puts two U+200F marks before its word), so only the shape is matched: the date and time
/// Snipping Tool appends, or the index in parentheses Win+PrtScn appends. Snipping Tool's shape is tested first,
/// because a suffix it might add for two snips in one second would also match Win+PrtScn's.
/// </para>
/// <para>
/// <b>Fresh writes only.</b> A screenshot is written the moment it is taken. A file copied or moved into the folder
/// keeps its old write time (a copy shows it only once the copy is complete — measured), so a file whose write time
/// is older than <see cref="FreshWriteWindow"/> once complete is not a new screenshot. The startup catch-up decides by
/// its own marker instead.
/// </para>
/// </remarks>
public static partial class WindowsScreenshots
{
    /// <summary>Snipping Tool's display name, on cards and in the Snipping tab's filter.</summary>
    public const string SnippingToolName = "Snipping Tool";

    /// <summary>The display and process name given to Win+PrtScn shots (Explorer must not be blamed for them).</summary>
    public const string WinPrtScnName = "Win+PrtScn";

    /// <summary>Snipping Tool's executable (package <c>Microsoft.ScreenSketch</c>, folder <c>SnippingTool</c>).</summary>
    public const string SnippingToolExecutableName = "SnippingTool.exe";

    /// <summary>
    /// How old a file's write time may be, once the file is complete, for it to count as a screenshot just taken.
    /// Generous: a busy machine can take seconds to report a change; a copied or moved file is usually days old.
    /// </summary>
    public static readonly TimeSpan FreshWriteWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The source of a Win+PrtScn shot: no process (twinui.dll runs inside Explorer), the name "Win+PrtScn". Adding
    /// "Win+PrtScn" to Ignored apps skips them.
    /// </summary>
    public static readonly SourceAppInfo WinPrtScnSource = new(WinPrtScnName, null, WinPrtScnName);

    /// <summary>
    /// The source of an image of no known tool. Its process name "Screenshots" lets Ignored apps skip such files
    /// without skipping the two tools.
    /// </summary>
    public static readonly SourceAppInfo FolderSource = new("Screenshots", null, "Screenshots folder");

    /// <summary>Image extensions read from the folder; recordings (.mp4) and everything else are left alone.</summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".jxr", ".webp", ".tif", ".tiff", ".heic", ".heif",
    };

    /// <summary>PNG's IEND chunk: zero length, the type, and its fixed CRC — the last 12 bytes of every complete PNG.</summary>
    private static ReadOnlySpan<byte> PngEnd => [0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0xAE, 0x42, 0x60, 0x82];

    /// <summary>
    /// The source recorded for a Snipping Tool file: process <c>SnippingTool</c> — the same name its clipboard copies
    /// carry, so one Ignored apps entry skips both — and the display name "Snipping Tool".
    /// </summary>
    /// <param name="executablePath">The installed <c>SnippingTool.exe</c>, when known.</param>
    /// <returns>The source.</returns>
    public static SourceAppInfo SnippingToolSource(string? executablePath) => new("SnippingTool", executablePath, SnippingToolName);

    /// <summary>Whether a file name has one of the image extensions the integration reads.</summary>
    /// <param name="fileName">A file name or path.</param>
    /// <returns>Whether it is read.</returns>
    public static bool IsImageFile(string fileName) => ImageExtensions.Contains(Path.GetExtension(fileName));

    /// <summary>Tells which tool saved a file, from its name's shape (see the class remarks).</summary>
    /// <param name="fileName">The file name (a path is reduced to its name).</param>
    /// <returns>The tool; <see cref="ScreenshotTool.Other"/> for any other image name.</returns>
    public static ScreenshotTool Classify(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return SnippingToolFileName().IsMatch(name) ? ScreenshotTool.SnippingTool
            : WinPrtScnFileName().IsMatch(name) ? ScreenshotTool.WinPrtScn
            : ScreenshotTool.Other;
    }

    /// <summary>The source to record for a file of <paramref name="tool"/>.</summary>
    /// <param name="tool">From <see cref="Classify"/>.</param>
    /// <param name="snippingToolPath">The installed <c>SnippingTool.exe</c>, when known.</param>
    /// <returns>The source.</returns>
    public static SourceAppInfo SourceFor(ScreenshotTool tool, string? snippingToolPath) => tool switch
    {
        ScreenshotTool.SnippingTool => SnippingToolSource(snippingToolPath),
        ScreenshotTool.WinPrtScn => WinPrtScnSource,
        _ => FolderSource,
    };

    /// <summary>Whether a complete file's write time makes it a screenshot just taken (see the class remarks).</summary>
    /// <param name="writtenUtc">The file's last write time.</param>
    /// <param name="nowUtc">Now.</param>
    /// <returns>Whether it is fresh; a write time in the future (clock skew) counts as fresh.</returns>
    public static bool IsFresh(DateTimeOffset writtenUtc, DateTimeOffset nowUtc) => nowUtc - writtenUtc <= FreshWriteWindow;

    /// <summary>
    /// Whether an image file's bytes are complete, decided from the bytes alone, so that the reader never has to lock
    /// the writer out to find out: PNG ends with its IEND chunk, JPEG with its end-of-image marker, GIF with its
    /// trailer, and BMP is as long as its header says.
    /// </summary>
    /// <param name="bytes">The file as read so far (the whole file).</param>
    /// <param name="fileName">Its name (the extension picks the check when the bytes do not tell).</param>
    /// <returns>
    /// <see langword="true"/> when complete, <see langword="false"/> when known to be incomplete, and
    /// <see langword="null"/> for formats without an end marker (JPEG XR, WebP, TIFF, HEIF): the caller then waits until
    /// size and write time stop changing.
    /// </returns>
    public static bool? LooksComplete(ReadOnlySpan<byte> bytes, string fileName)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return bytes.EndsWith(PngEnd);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            // Encoders end with FFD9; anything after it (rare) would make a complete file look incomplete until the
            // caller's timeout, which is the safe direction.
            return bytes.Length >= 4 && bytes[^2] == 0xFF && bytes[^1] == 0xD9;
        }

        if (bytes.Length >= 6 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
        {
            return bytes[^1] == 0x3B;
        }

        if (bytes.Length >= 14 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
        {
            uint declared = BitConverter.ToUInt32(bytes[2..6]);
            return declared > 14 && bytes.Length >= declared;
        }

        // An empty or short file of a format with a signature is still being written; other formats cannot tell.
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            ? false
            : null;
    }

    /// <summary>Snipping Tool's file name: prefix, date, six-digit time, an optional " (N)" for a clash, <c>.png</c>.</summary>
    /// <returns>The source-generated regex.</returns>
    [GeneratedRegex(@"^.+? \d{4}-\d{2}-\d{2} \d{6}(?: ?\(\d+\))?\.png$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SnippingToolFileName();

    /// <summary>Win+PrtScn's file name: a prefix (it may start with U+200F marks), an optional space, <c>(N).png</c>.</summary>
    /// <returns>The source-generated regex.</returns>
    [GeneratedRegex(@"^.+? ?\(\d+\)\.png$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WinPrtScnFileName();
}

/// <summary>What Snipping Tool's own settings say about saving screenshots (read from a private copy of its hive).</summary>
/// <param name="AutoSaveScreenshots">
/// Its "Automatically save original screenshots" switch: <see langword="null"/> when never changed, which means on
/// (the default since Snipping Tool 11.2209.2.0).
/// </param>
/// <param name="HasCustomFolder">
/// Whether it saves to a folder the user chose (<c>AutoSaveScreenshotsLocation</c> is set). That folder is kept through
/// the app's own file-access list, in a form not known, so the integration cannot follow it; Settings says so.
/// </param>
public sealed record SnippingToolSettings(bool? AutoSaveScreenshots, bool HasCustomFolder)
{
    /// <summary>The settings of a Snipping Tool that never changed them: auto-save on, the Screenshots folder.</summary>
    public static readonly SnippingToolSettings Defaults = new(null, false);

    /// <summary>ApplicationData's registry type of a stored boolean.</summary>
    public const uint BooleanType = 0x5F5E10B;

    /// <summary>ApplicationData's registry type of a stored string.</summary>
    public const uint StringType = 0x5F5E10C;

    /// <summary>Whether auto-save is on (an unchanged switch is on).</summary>
    public bool SavesScreenshots => AutoSaveScreenshots != false;

    /// <summary>
    /// Reads the two values from Snipping Tool's <c>LocalState</c> container. Each ApplicationData value is its data
    /// followed by an 8-byte timestamp of when it was set; values of an unexpected type or size are ignored.
    /// </summary>
    /// <param name="localState">Value name → (registry type, raw data with the timestamp), as enumerated from the hive.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="localState"/> is <see langword="null"/>.</exception>
    public static SnippingToolSettings Parse(IReadOnlyDictionary<string, (uint Type, byte[] Data)> localState)
    {
        ArgumentNullException.ThrowIfNull(localState);
        bool? autoSave = localState.TryGetValue("AutoSaveScreenshots", out var save) && save.Type == BooleanType && save.Data.Length >= 9
            ? save.Data[0] != 0
            : null;
        bool custom = localState.TryGetValue("AutoSaveScreenshotsLocation", out var location) && location.Type == StringType
            && location.Data.Length > 8 && Encoding.Unicode.GetString(location.Data, 0, location.Data.Length - 8).TrimEnd('\0').Trim().Length > 0;
        return new SnippingToolSettings(autoSave, custom);
    }
}
