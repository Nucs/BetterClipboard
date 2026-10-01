#:property PublishAot=false
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using static Native;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

// ─────────────────────────────────────────────────────────────────────────────────────────────────────
// Probe: what do Windows' own screenshot tools leave behind for a "Screenshots" integration?
//   Win+PrtScn (twinui.dll in explorer.exe) and Snipping Tool's auto-save (Win+Shift+S, PrtScn when it
//   opens Snipping Tool, the app itself) both save PNG files into the Screenshots known folder by default.
//
// Modes:
//   --inventory (default)
//       Read-only and content-free. Known folders (Screenshots, Pictures, Captures, Videos) and whether they
//       are redirected or under OneDrive; ScreenshotIndex; the PrtScn → Snipping Tool switch; twinui.dll's
//       localized Win+PrtScn name format per installed UI language; the Snipping Tool package, its exe's
//       version strings and its auto-save settings (read from a private COPY of its settings hive); then the
//       screenshot folders by file-name class: counts, prefixes, PNG sizes and chunk layout, micro-snips,
//       cloud placeholders. Never prints a file name of the "other" class (Game Bar names carry window
//       titles), never reads pixels, never touches the clipboard.
//   --watch [seconds]   (default 120)
//       Passive live observation while YOU take screenshots (one Win+PrtScn, one Win+Shift+S snip): file
//       events in the screenshot folders (name class, size, write age, which processes hold the file, when it
//       was released and complete), ScreenshotIndex changes, and clipboard notifications (sequence number,
//       owner process and window class, format names, the foreground process). Nothing can disturb the
//       producers: the clipboard is never opened (GetUpdatedClipboardFormats and GetClipboardOwner need no
//       OpenClipboard, so no delayed rendering is triggered and no flush fails on us), holders are asked through
//       an attribute-only handle, and a file's bytes are read only after no other process has held it for two
//       polls. File names are printed as their class only; no pixel and no clipboard data is read.
//   --self-test
//       The watch's machinery on BC-TEST data only: a scratch folder (a two-step PNG write, a file copied in,
//       one moved in, a rename) and a private, anonymous window station (its own clipboard) where a producer
//       writes BC-TEST text plus a delayed-rendered PNG. Scratch folder %TEMP%\bc-screenshots-probe-<pid>,
//       deleted afterwards. The user's folders, clipboard and Win+V history are untouched.
//
// Run: dotnet run tools/probes/probe_screenshots.cs -- --inventory
// Results on Windows 11 26200.8875 (2026-10-01), twinui.dll 10.0.26100.8328, Snipping Tool 11.2607.23.0:
// CLAUDE.md §2.18.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────

var mode = args.Length > 0 ? args[0] : "--inventory";
Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine($"Windows {Environment.OSVersion.Version} · {RuntimeInformation.FrameworkDescription} · {DateTime.UtcNow:yyyy-MM-dd HH:mm}Z");
switch (mode)
{
    case "--inventory":
        Inventory.Run();
        return 0;
    case "--watch":
        return Watch.Run(args.Length > 1 && int.TryParse(args[1], out var seconds) ? seconds : 120);
    case "--self-test":
        return SelfTest.Run();
    default:
        Console.WriteLine("usage: probe_screenshots.cs -- [--inventory | --watch [seconds] | --self-test]");
        return 2;
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>What kind of file a name in a screenshot folder is, judged by the name alone.</summary>
enum NameClass
{
    /// <summary>Win+PrtScn: twinui's localized <c>"&lt;prefix&gt; (%d)"</c> + <c>.png</c>, N = ScreenshotIndex before the bump.</summary>
    WinPrtScn,

    /// <summary>Snipping Tool's auto-saved image: <c>"&lt;AutoSaveImageFilePrefix&gt; yyyy-MM-dd HHmmss.png"</c>.</summary>
    SnipImage,

    /// <summary>Snipping Tool's auto-saved recording: <c>"&lt;AutoSaveRecordingFilePrefix&gt; yyyy-MM-dd HHmmss.mp4"</c>.</summary>
    SnipRecording,

    /// <summary>Any other image (another tool, a manual save, Game Bar in Captures).</summary>
    OtherImage,

    /// <summary>Any other video.</summary>
    OtherVideo,

    /// <summary>Everything else (desktop.ini, temporary files).</summary>
    Other,
}

/// <summary>Shared facts: where the folders are, how names are classified, how a PNG is summarized.</summary>
static class Shots
{
    /// <summary><c>FOLDERID_Screenshots</c>: <c>Pictures\Screenshots</c> unless redirected; Win+PrtScn and Snipping Tool's default.</summary>
    public static readonly Guid Screenshots = new("b7bede81-df94-4682-a7d8-57a52620b86f");

    /// <summary><c>FOLDERID_Pictures</c>, the Screenshots folder's parent in its folder description.</summary>
    public static readonly Guid Pictures = new("33E28130-4E1E-4676-835A-98395C3BC3BB");

    /// <summary>The "Captures" known folder (<c>Videos\Captures</c>): Xbox Game Bar's screenshots and clips.</summary>
    public static readonly Guid Captures = new("EDC0FE71-98D8-4F4A-B920-C8DC133CB165");

    /// <summary><c>FOLDERID_Videos</c>: Snipping Tool's recordings go to <c>Videos\Screen Recordings</c> by default.</summary>
    public static readonly Guid Videos = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");

    /// <summary>
    /// Snipping Tool's name: prefix, date, six-digit time, an optional " (N)" for a collision (unverified: no
    /// two snips in one second were observed), and the extension.
    /// </summary>
    /// <remarks>Checked before <see cref="WinPrtScnName"/>, whose pattern the collision suffix would also match.</remarks>
    private static readonly Regex SnipName = new(@"^(?<prefix>.+?) (?<date>\d{4}-\d{2}-\d{2}) (?<time>\d{6})(?: ?\((?<n>\d+)\))?\.(?<ext>png|mp4)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Win+PrtScn's name: a prefix (localized, may start with U+200F marks on right-to-left UIs), an optional
    /// space (not every language has one), the index in parentheses, <c>.png</c>.
    /// </summary>
    private static readonly Regex WinPrtScnName = new(@"^(?<prefix>.+?) ?\((?<n>\d+)\)\.png$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Extensions treated as images for <see cref="NameClass.OtherImage"/>.</summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".jxr", ".webp", ".heic", ".tif", ".tiff" };

    /// <summary>Extensions treated as videos for <see cref="NameClass.OtherVideo"/>.</summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm" };

    /// <summary>
    /// Resolves a known folder without creating or verifying it, so a folder that does not exist yet (a profile
    /// that never took a screenshot) still yields the path a watcher would wait for.
    /// </summary>
    /// <param name="id">The KNOWNFOLDERID.</param>
    /// <returns>The path, or <see langword="null"/> when the shell cannot resolve it (a broken redirection).</returns>
    public static string? KnownFolder(Guid id)
    {
        int hr = SHGetKnownFolderPath(in id, KF_FLAG_DONT_VERIFY, 0, out var pointer);
        try
        {
            return hr == 0 ? Marshal.PtrToStringUni(pointer) : null;
        }
        finally
        {
            // The shell allocates the string even on some failures; freeing NULL is a no-op.
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    /// <summary>Classifies a file name the way an integration would, before reading the file.</summary>
    /// <param name="name">File name without folder.</param>
    /// <param name="match">The regex match for the two known patterns (prefix, date, index), else <see langword="null"/>.</param>
    /// <returns>The class.</returns>
    public static NameClass Classify(string name, out Match? match)
    {
        var snip = SnipName.Match(name);
        if (snip.Success)
        {
            match = snip;
            return snip.Groups["ext"].Value.Equals("mp4", StringComparison.OrdinalIgnoreCase) ? NameClass.SnipRecording : NameClass.SnipImage;
        }

        var win = WinPrtScnName.Match(name);
        if (win.Success)
        {
            match = win;
            return NameClass.WinPrtScn;
        }

        match = null;
        var extension = Path.GetExtension(name);
        return ImageExtensions.Contains(extension) ? NameClass.OtherImage
            : VideoExtensions.Contains(extension) ? NameClass.OtherVideo
            : NameClass.Other;
    }

    /// <summary>
    /// Makes a prefix printable: invisible format characters (the U+200F marks of a right-to-left UI) become
    /// <c>\uXXXX</c> escapes, so the output shows they are there.
    /// </summary>
    /// <param name="text">A localized word such as "Screenshot".</param>
    /// <returns>The escaped text.</returns>
    public static string Visible(string text)
    {
        var builder = new StringBuilder();
        foreach (char c in text)
        {
            builder.Append(char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Control
                ? $"\\u{(int)c:X4}"
                : c.ToString());
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reads a PNG's structure: IHDR dimensions and colour type, and (when <paramref name="walkChunks"/>) the
    /// chunk layout with runs of IDAT collapsed and whether IEND ends the file. Never decodes pixels.
    /// </summary>
    /// <param name="path">File to read; opened with ReadWrite|Delete sharing so a writer or Explorer is never blocked.</param>
    /// <param name="walkChunks">Whether to walk every chunk header (reads the whole file).</param>
    /// <returns>The summary, or <see langword="null"/> when the file is not a PNG (or vanished).</returns>
    /// <exception cref="IOException">The file cannot be opened (still being written with exclusive sharing).</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    public static PngSummary? ReadPng(string path, bool walkChunks)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> head = stackalloc byte[33];
        if (stream.Read(head) < 33 || !head[..8].SequenceEqual(PngSignature) || !head[12..16].SequenceEqual("IHDR"u8))
        {
            return null;
        }

        int width = BigEndian(head[16..20]);
        int height = BigEndian(head[20..24]);
        byte colorType = head[25];
        if (!walkChunks)
        {
            return new PngSummary(width, height, colorType, null, null);
        }

        // Walk the chunk headers from the start: length (4) + type (4) + data + CRC (4).
        var layout = new List<string>();
        bool complete = false;
        long position = 8;
        Span<byte> header = stackalloc byte[8];
        while (position + 8 <= stream.Length)
        {
            stream.Position = position;
            if (stream.Read(header) < 8)
            {
                break;
            }

            int length = BigEndian(header[..4]);
            string type = Encoding.ASCII.GetString(header[4..8]);
            if (layout.Count == 0 || layout[^1] != type)
            {
                layout.Add(type);
            }

            position += 12L + (uint)length;
            if (type == "IEND")
            {
                // Complete only when IEND's CRC is the last byte: a writer still appending has not reached it.
                complete = position == stream.Length;
                break;
            }
        }

        return new PngSummary(width, height, colorType, string.Join(' ', layout), complete);
    }

    /// <summary>PNG's eight-byte signature.</summary>
    public static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Reads a big-endian 32-bit integer (PNG's byte order).</summary>
    /// <param name="bytes">Four bytes.</param>
    /// <returns>The value.</returns>
    private static int BigEndian(ReadOnlySpan<byte> bytes) => (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
}

/// <summary>A PNG's structure, without its pixels.</summary>
/// <param name="Width">IHDR width.</param>
/// <param name="Height">IHDR height.</param>
/// <param name="ColorType">IHDR colour type (6 = RGBA, 2 = RGB).</param>
/// <param name="Layout">Chunk types in order with IDAT runs collapsed, when walked.</param>
/// <param name="Complete">Whether IEND ends the file exactly, when walked.</param>
record PngSummary(int Width, int Height, byte ColorType, string? Layout, bool? Complete);

/// <summary>Snipping Tool (package <c>Microsoft.ScreenSketch</c>): where it is and what its settings say.</summary>
static class SnippingTool
{
    /// <summary>The package family name (stable across versions).</summary>
    public const string FamilyName = "Microsoft.ScreenSketch_8wekyb3d8bbwe";

    /// <summary>Its settings hive, an app-data registry file (types are ApplicationData's, values carry an 8-byte timestamp).</summary>
    public static string SettingsHive => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", FamilyName, "Settings", "settings.dat");

    /// <summary>
    /// Finds the installed package through the per-user AppModel repository (listing <c>WindowsApps</c> needs
    /// admin rights; this key does not).
    /// </summary>
    /// <returns>The full package name and its install folder, or <see langword="null"/> when not installed.</returns>
    public static (string FullName, string Root)? FindPackage()
    {
        using var packages = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        var name = packages?.GetSubKeyNames().Where(n => n.StartsWith("Microsoft.ScreenSketch_", StringComparison.OrdinalIgnoreCase)).OrderByDescending(n => n).FirstOrDefault();
        if (name is null)
        {
            return null;
        }

        using var package = packages!.OpenSubKey(name);
        return package?.GetValue("PackageRootFolder") is string root ? (name, root) : null;
    }

    /// <summary>
    /// Reads Snipping Tool's <c>LocalState</c> settings from a private copy of its hive. Loading the live file
    /// with <c>RegLoadAppKey</c> could replay its transaction logs into it — a write to another app's data — so
    /// the hive and its logs are copied first; while Snipping Tool runs the files may be in use, which is reported.
    /// </summary>
    /// <param name="status">What happened (copied, in use, missing).</param>
    /// <returns>Every value: name, ApplicationData type and raw data (timestamp stripped).</returns>
    public static IReadOnlyList<(string Name, uint Type, byte[] Data)> ReadSettings(out string status)
    {
        var result = new List<(string, uint, byte[])>();
        if (!File.Exists(SettingsHive))
        {
            status = "no settings hive (Snipping Tool never ran for this user)";
            return result;
        }

        var scratch = Directory.CreateTempSubdirectory("bc-screenshots-probe-hive-");
        try
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(SettingsHive)!, "settings.dat*"))
            {
                File.Copy(file, Path.Combine(scratch.FullName, Path.GetFileName(file)));
            }

            int rc = RegLoadAppKeyW(Path.Combine(scratch.FullName, "settings.dat"), out var hive, KEY_READ, 0, 0);
            if (rc != 0)
            {
                status = $"RegLoadAppKey on the copy failed ({rc})";
                return result;
            }

            try
            {
                if (RegOpenKeyExW(hive, "LocalState", 0, KEY_READ, out var local) != 0)
                {
                    status = "copied; no LocalState container";
                    return result;
                }

                try
                {
                    var name = new char[16384];
                    var data = new byte[1 << 16];
                    for (uint index = 0; ; index++)
                    {
                        uint nameLength = (uint)name.Length, dataLength = (uint)data.Length;
                        int enumerated = RegEnumValueW(local, index, name, ref nameLength, 0, out uint type, data, ref dataLength);
                        if (enumerated == ERROR_NO_MORE_ITEMS)
                        {
                            break;
                        }

                        if (enumerated != 0)
                        {
                            continue; // an oversized value: not one of the small settings this probe looks at
                        }

                        // ApplicationData appends an 8-byte FILETIME (when the value was last set) to the data.
                        int bodyLength = (int)Math.Max(0, dataLength - 8);
                        result.Add((new string(name, 0, (int)nameLength), type, data[..bodyLength]));
                    }
                }
                finally
                {
                    RegCloseKey(local);
                }
            }
            finally
            {
                // Closing the last handle unloads the private hive, so the scratch files can be deleted.
                RegCloseKey(hive);
            }

            status = "read from a private copy";
            return result;
        }
        catch (IOException ex)
        {
            status = $"hive in use, not read ({ex.GetType().Name}: Snipping Tool is probably running)";
            return result;
        }
        catch (UnauthorizedAccessException ex)
        {
            status = $"hive not readable ({ex.GetType().Name})";
            return result;
        }
        finally
        {
            TryDelete(scratch.FullName);
        }
    }

    /// <summary>Renders an ApplicationData value for the report without printing long strings.</summary>
    /// <param name="type">The registry type (ApplicationData's 0x5F5E1xx range).</param>
    /// <param name="data">The value without its timestamp.</param>
    /// <returns>A short description: a boolean, a number, or a string's length and whether it names an existing folder.</returns>
    public static string Describe(uint type, byte[] data) => type switch
    {
        0x5F5E10B when data.Length >= 1 => data[0] != 0 ? "true" : "false",
        0x5F5E104 when data.Length >= 4 => BitConverter.ToInt32(data).ToString(),
        0x5F5E109 when data.Length >= 8 => BitConverter.ToDouble(data).ToString(System.Globalization.CultureInfo.InvariantCulture),
        0x5F5E10C => DescribeString(Encoding.Unicode.GetString(data).TrimEnd('\0')),
        _ => $"<type 0x{type:X}, {data.Length} bytes>",
    };

    /// <summary>Describes a string setting by shape only (a save location could name a private folder).</summary>
    /// <param name="text">The value.</param>
    /// <returns>Length plus "existing folder" / "a GUID-like token" / "other".</returns>
    private static string DescribeString(string text) =>
        $"<string, {text.Length} chars: " +
        (Directory.Exists(text) ? "an existing folder" : Guid.TryParse(text.Trim('{', '}'), out _) ? "a GUID-like token" : "neither a folder nor a GUID") + ">";

    /// <summary>Deletes a scratch folder, retrying while the unloaded hive's file handle is still being released.</summary>
    /// <param name="folder">The folder.</param>
    public static void TryDelete(string folder)
    {
        for (int attempt = 0; attempt < 20 && Directory.Exists(folder); attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}

/// <summary>twinui.dll's Win+PrtScn file-name format, per installed UI language.</summary>
static class TwinUi
{
    /// <summary>The en-US text that identifies the string resource.</summary>
    private const string EnglishFormat = "Screenshot (%d)";

    /// <summary>
    /// Finds the string id of <see cref="EnglishFormat"/> in the en-US MUI, then reads that id from every
    /// installed language's MUI.
    /// </summary>
    /// <returns>The id (or <see langword="null"/>) and (language, format) pairs.</returns>
    public static (int? Id, List<(string Language, string? Format)> Formats) ReadFormats()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        int? id = LoadStrings(Path.Combine(system, "en-US", "twinui.dll.mui")).Where(pair => pair.Value == EnglishFormat).Select(pair => (int?)pair.Key).FirstOrDefault();
        var formats = new List<(string, string?)>();
        if (id is null)
        {
            return (null, formats);
        }

        foreach (var mui in Directory.GetFiles(system, "twinui.dll.mui", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true }))
        {
            var language = Path.GetFileName(Path.GetDirectoryName(mui)!);
            formats.Add((language, LoadStrings(mui).GetValueOrDefault(id.Value)));
        }

        return (id, formats);
    }

    /// <summary>Loads every string-table entry of a resource-only module.</summary>
    /// <param name="path">A .mui file.</param>
    /// <returns>Id → text; empty when the file cannot be loaded.</returns>
    private static Dictionary<int, string> LoadStrings(string path)
    {
        var result = new Dictionary<int, string>();

        // As a data file: nothing in it runs, and only its resources are mapped.
        nint module = LoadLibraryExW(path, 0, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
        if (module == 0)
        {
            return result;
        }

        try
        {
            var blocks = new List<int>();
            EnumResNameProc collect = (_, _, name, _) =>
            {
                // String tables are numbered resources: block N holds ids (N-1)*16 .. N*16-1.
                if (name is > 0 and < 0x10000)
                {
                    blocks.Add((int)name);
                }

                return true;
            };
            EnumResourceNamesW(module, RT_STRING, collect, 0);
            GC.KeepAlive(collect);
            var buffer = new char[4096];
            foreach (int block in blocks)
            {
                for (int offset = 0; offset < 16; offset++)
                {
                    int stringId = (block - 1) * 16 + offset;
                    int length = LoadStringW(module, (uint)stringId, buffer, buffer.Length);
                    if (length > 0)
                    {
                        result[stringId] = new string(buffer, 0, length);
                    }
                }
            }
        }
        finally
        {
            FreeLibrary(module);
        }

        return result;
    }
}

/// <summary>Describes the clipboard's current state from the outside: no OpenClipboard, no data read.</summary>
static class ClipboardMeta
{
    /// <summary>Names of the predefined formats that matter for screenshots and text.</summary>
    private static readonly Dictionary<uint, string> Predefined = new()
    {
        [1] = "CF_TEXT", [2] = "CF_BITMAP", [3] = "CF_METAFILEPICT", [7] = "CF_OEMTEXT", [8] = "CF_DIB",
        [13] = "CF_UNICODETEXT", [14] = "CF_ENHMETAFILE", [15] = "CF_HDROP", [16] = "CF_LOCALE", [17] = "CF_DIBV5",
    };

    /// <summary>
    /// Owner process and window class plus the format names, read with calls that never open the clipboard, so
    /// a producer's delayed rendering is never triggered and its own OpenClipboard (a flush) never fails on us.
    /// </summary>
    /// <returns>A one-line description.</returns>
    public static string Describe()
    {
        var owner = GetClipboardOwner();
        string ownerText = owner == 0 ? "owner <none>" : $"owner {ProcessName(owner)} [{ClassName(owner)}]";
        var formats = new uint[256];
        string formatText = GetUpdatedClipboardFormats(formats, (uint)formats.Length, out uint count)
            ? string.Join(", ", formats.Take((int)count).Select(FormatName))
            : $"<GetUpdatedClipboardFormats failed {Marshal.GetLastPInvokeError()}>";
        return $"{ownerText} formats: {formatText}";
    }

    /// <summary>Process image name (file name only) of a window's process.</summary>
    /// <param name="hwnd">Window.</param>
    /// <returns>The name, or a placeholder when the process cannot be queried.</returns>
    public static string ProcessName(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return ProcessNameById((int)pid);
    }

    /// <summary>Process image name (file name only) for a process id.</summary>
    /// <param name="processId">The id.</param>
    /// <returns>The name, or <c>&lt;pid N&gt;</c> when the process is gone or protected.</returns>
    public static string ProcessNameById(int processId)
    {
        uint pid = (uint)processId;
        nint process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0)
        {
            return $"<pid {pid}>";
        }

        try
        {
            var buffer = new char[1024];
            uint size = (uint)buffer.Length;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? Path.GetFileName(new string(buffer, 0, (int)size)) : $"<pid {pid}>";
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>A window's class name (e.g. <c>CLIPBRDWNDCLASS</c>, OLE's clipboard window).</summary>
    /// <param name="hwnd">Window.</param>
    /// <returns>The class name, or "?".</returns>
    private static string ClassName(nint hwnd)
    {
        var buffer = new char[256];
        int length = GetClassNameW(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "?";
    }

    /// <summary>A format's name: predefined, registered, or the number.</summary>
    /// <param name="format">Format id.</param>
    /// <returns>Its name.</returns>
    private static string FormatName(uint format)
    {
        if (Predefined.TryGetValue(format, out var known))
        {
            return known;
        }

        var buffer = new char[256];
        int length = GetClipboardFormatNameW(format, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : $"#{format}";
    }
}

/// <summary>Which processes have a file open, asked without taking part in any sharing check.</summary>
static class FileHolders
{
    /// <summary>
    /// Lists the other processes holding <paramref name="path"/> open (NtQueryInformationFile,
    /// FileProcessIdsUsingFileInformation), through a handle that asks only for FILE_READ_ATTRIBUTES — access that
    /// sharing modes do not cover, so even a writer opening the file exclusively at that moment succeeds.
    /// </summary>
    /// <remarks>Slow: ~135–140 ms per call on Windows 11 26200 (measured), so never call it from an event handler.</remarks>
    /// <param name="path">File.</param>
    /// <returns>The process ids without this process's own (its query handle always appears), or <see langword="null"/> when the file is gone.</returns>
    public static IReadOnlyList<int>? Others(string path)
    {
        using var handle = CreateFileW(path, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, 0, OPEN_EXISTING, 0, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        // FILE_PROCESS_IDS_USING_FILE_INFORMATION: ULONG count, then ULONG_PTR ids (8-byte aligned on x64).
        var buffer = new byte[8 + (IntPtr.Size * 512)];
        int status = NtQueryInformationFile(handle, out _, buffer, (uint)buffer.Length, FileProcessIdsUsingFileInformation);
        if (status != 0)
        {
            return [];
        }

        int count = BitConverter.ToInt32(buffer, 0);
        var ids = new List<int>();
        for (int i = 0; i < count && 8 + ((i + 1) * IntPtr.Size) <= buffer.Length; i++)
        {
            int id = (int)BitConverter.ToInt64(buffer, 8 + (i * IntPtr.Size));
            if (id != Environment.ProcessId)
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}

/// <summary>The read-only inventory (default mode).</summary>
static class Inventory
{
    /// <summary>Prints every section; never throws for a missing piece (each is reported instead).</summary>
    public static void Run()
    {
        Console.WriteLine();
        Console.WriteLine("── Known folders (KF_FLAG_DONT_VERIFY: resolved, not created)");
        var screenshots = Shots.KnownFolder(Shots.Screenshots);
        var oneDriveRoots = OneDriveRoots();
        foreach (var (label, id) in new[] { ("Screenshots", Shots.Screenshots), ("Pictures", Shots.Pictures), ("Captures", Shots.Captures), ("Videos", Shots.Videos) })
        {
            var path = Shots.KnownFolder(id);
            bool underOneDrive = path is not null && oneDriveRoots.Any(root => path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"  {label,-12} {(path is null ? "<unresolved>" : Redact(path))}  exists={path is not null && Directory.Exists(path)}  underOneDrive={underOneDrive}");
        }

        using (var shellFolders = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
        {
            // A per-user redirection of Screenshots itself is stored under its GUID; absent = Pictures\Screenshots.
            var redirected = shellFolders?.GetValue("{B7BEDE81-DF94-4682-A7D8-57A52620B86F}", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            Console.WriteLine($"  Screenshots redirected on its own (User Shell Folders value): {(redirected is null ? "no" : "yes")}");
        }

        Console.WriteLine($"  OneDrive accounts with a folder: {oneDriveRoots.Count}");

        Console.WriteLine();
        Console.WriteLine("── Registry");
        using (var explorer = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer"))
        {
            Console.WriteLine($"  ScreenshotIndex (the next Win+PrtScn number): {explorer?.GetValue("ScreenshotIndex")?.ToString() ?? "<absent>"}");
        }

        using (var keyboard = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard"))
        {
            var value = keyboard?.GetValue("PrintScreenKeyForSnippingEnabled");
            Console.WriteLine($"  PrintScreenKeyForSnippingEnabled: {value?.ToString() ?? "<absent> (Windows' default: on since KB5025310, 2023)"}");
        }

        Console.WriteLine();
        Console.WriteLine("── Win+PrtScn (twinui.dll, loaded by explorer.exe)");
        var twinui = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "twinui.dll");
        if (TryReadAllBytes(twinui) is { } twinuiBytes)
        {
            bool mentionsIndex = Contains(twinuiBytes, Encoding.Unicode.GetBytes("ScreenshotIndex"));
            Console.WriteLine($"  twinui.dll {FileVersionInfo.GetVersionInfo(twinui).FileVersion}: contains \"ScreenshotIndex\" = {mentionsIndex}");
        }
        else
        {
            Console.WriteLine("  twinui.dll: missing or unreadable");
        }

        var (formatId, formats) = TwinUi.ReadFormats();
        Console.WriteLine($"  name format: string id {formatId?.ToString() ?? "<not found>"} in twinui.dll.mui");
        foreach (var (language, format) in formats)
        {
            Console.WriteLine($"    {language}: {(format is null ? "<missing>" : Shots.Visible(format))}");
        }

        Console.WriteLine();
        Console.WriteLine("── Snipping Tool");
        var package = SnippingTool.FindPackage();
        Console.WriteLine($"  package: {package?.FullName ?? "<not installed>"}");
        if (package is { } found)
        {
            var exe = Path.Combine(found.Root, "SnippingTool", "SnippingTool.exe");
            if (TryReadAllBytes(exe) is { } bytes)
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                Console.WriteLine($"  SnippingTool.exe: FileDescription \"{info.FileDescription}\", ProductName \"{info.ProductName}\"");

                // The names this research found; one missing in a later version means the settings were renamed.
                var names = new[] { "AutoSaveScreenshots", "AutoSaveScreenshotsLocation", "AutoSaveImageFilePrefix", "AutoSaveRecordings", "AutoSaveRecordingsLocation", "AutoSaveRecordingFilePrefix", "AutoSaveCaptures" };
                Console.WriteLine($"  setting names present in the exe: {string.Join(", ", names.Where(n => Contains(bytes, Encoding.Unicode.GetBytes(n))))}");
            }
            else
            {
                Console.WriteLine("  SnippingTool.exe: missing or unreadable");
            }
        }

        Console.WriteLine($"  running: {Process.GetProcessesByName("SnippingTool").Length} process(es)");
        var settings = SnippingTool.ReadSettings(out var status);
        Console.WriteLine($"  settings ({status}): {settings.Count} LocalState values");
        foreach (var (name, type, data) in settings.Where(s => Regex.IsMatch(s.Name, "Save|Copy|Clipboard|Location|Prefix|Folder", RegexOptions.IgnoreCase)))
        {
            Console.WriteLine($"    {name} = {SnippingTool.Describe(type, data)}");
        }

        if (!settings.Any(s => s.Name.StartsWith("AutoSave", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("    (no AutoSave* value: the defaults apply — auto-save on, the Screenshots folder)");
        }

        Console.WriteLine();
        Console.WriteLine("── Files");
        int? index = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer")?.GetValue("ScreenshotIndex") as int?;
        Report("Screenshots", screenshots, index);
        Report("Captures", Shots.KnownFolder(Shots.Captures), null);
        var videos = Shots.KnownFolder(Shots.Videos);
        Report("Videos\\Screen Recordings", videos is null ? null : Path.Combine(videos, "Screen Recordings"), null);
    }

    /// <summary>Summarizes one folder by name class; prints prefixes and shapes, never a name of another class.</summary>
    /// <param name="label">Folder label for the output.</param>
    /// <param name="folder">The folder (may be missing).</param>
    /// <param name="screenshotIndex">ScreenshotIndex, to compare with the highest Win+PrtScn number.</param>
    private static void Report(string label, string? folder, int? screenshotIndex)
    {
        if (folder is null || !Directory.Exists(folder))
        {
            Console.WriteLine($"  {label}: missing");
            return;
        }

        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        var files = new DirectoryInfo(folder).EnumerateFiles("*", options).ToList();
        int subfolders = Directory.EnumerateDirectories(folder, "*", options).Count();
        Console.WriteLine($"  {label}: {files.Count} files, {subfolders} subfolders");
        foreach (var group in files.GroupBy(f => Shots.Classify(f.Name, out _)).OrderBy(g => g.Key))
        {
            var list = group.OrderBy(f => f.LastWriteTimeUtc).ToList();

            // Files On-Demand placeholders: reading even a header downloads the file, so neither this probe nor an
            // integration's catch-up may open them.
            int placeholders = list.Count(IsPlaceholder);
            Console.WriteLine($"    {group.Key}: {list.Count}, written {list[0].LastWriteTime:yyyy-MM-dd}..{list[^1].LastWriteTime:yyyy-MM-dd}, {list.Min(f => f.Length):N0}..{list.Max(f => f.Length):N0} bytes, cloud placeholders {placeholders}");
            if (group.Key is NameClass.WinPrtScn or NameClass.SnipImage or NameClass.SnipRecording)
            {
                var matches = list.Select(f => { Shots.Classify(f.Name, out var m); return m!; }).ToList();
                foreach (var prefix in matches.GroupBy(m => m.Groups["prefix"].Value))
                {
                    Console.WriteLine($"      prefix \"{Shots.Visible(prefix.Key)}\" × {prefix.Count()}");
                }

                if (group.Key == NameClass.WinPrtScn)
                {
                    int highest = matches.Max(m => int.Parse(m.Groups["n"].Value));
                    Console.WriteLine($"      highest N {highest}, ScreenshotIndex {screenshotIndex?.ToString() ?? "?"} (the index is the NEXT number when it equals N + 1)");
                }
            }

            if (group.Key is NameClass.WinPrtScn or NameClass.SnipImage or NameClass.OtherImage)
            {
                var pngs = list.Where(f => !IsPlaceholder(f)).Select(f => (File: f, Png: TryReadPng(f.FullName, walk: false))).Where(p => p.Png is not null).ToList();
                if (pngs.Count > 0)
                {
                    // An accidental click-drag in the snip overlay saves a few pixels; count them.
                    int micro = pngs.Count(p => p.Png!.Width < 16 || p.Png.Height < 16);
                    Console.WriteLine($"      PNG sizes: {string.Join(", ", pngs.Select(p => $"{p.Png!.Width}x{p.Png.Height}").Distinct().Take(12))}{(pngs.Count > 12 ? ", …" : "")}; under 16 px on a side: {micro} of {pngs.Count}");
                    foreach (var layout in pngs.TakeLast(5).Select(p => TryReadPng(p.File.FullName, walk: true)).Where(p => p is not null).GroupBy(p => (p!.Layout, p.ColorType)))
                    {
                        Console.WriteLine($"      chunks (newest ≤ 5): {layout.Key.Layout} · colour type {layout.Key.ColorType} × {layout.Count()}");
                    }
                }
            }
        }
    }

    /// <summary>Whether a file is a cloud placeholder whose data is not on this disk (opening it would download it).</summary>
    /// <param name="file">File from an enumeration (attributes already fetched, nothing opened).</param>
    /// <returns>Whether any recall-on-open/data-access or offline attribute is set.</returns>
    private static bool IsPlaceholder(FileInfo file) =>
        ((int)file.Attributes & (FILE_ATTRIBUTE_RECALL_ON_OPEN | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | FILE_ATTRIBUTE_OFFLINE)) != 0;

    /// <summary>Reads a whole binary for a string search, or reports that it cannot.</summary>
    /// <param name="path">File.</param>
    /// <returns>The bytes, or <see langword="null"/> when the file is missing or unreadable.</returns>
    private static byte[]? TryReadAllBytes(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads a PNG summary, returning <see langword="null"/> instead of throwing for an unreadable file.</summary>
    /// <param name="path">File.</param>
    /// <param name="walk">Whether to walk the chunks.</param>
    /// <returns>The summary or <see langword="null"/>.</returns>
    private static PngSummary? TryReadPng(string path, bool walk)
    {
        try
        {
            return Shots.ReadPng(path, walk);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>OneDrive folders of signed-in accounts (<c>HKCU\Software\Microsoft\OneDrive\Accounts\*\UserFolder</c>).</summary>
    /// <returns>The folders (possibly none).</returns>
    private static List<string> OneDriveRoots()
    {
        var roots = new List<string>();
        using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
        foreach (var name in accounts?.GetSubKeyNames() ?? [])
        {
            using var account = accounts!.OpenSubKey(name);
            if (account?.GetValue("UserFolder") is string folder && folder.Length > 0)
            {
                roots.Add(folder.TrimEnd('\\'));
            }
        }

        return roots;
    }

    /// <summary>Replaces the profile folder with <c>%USERPROFILE%</c> so the output carries no account name.</summary>
    /// <param name="path">A path.</param>
    /// <returns>The redacted path.</returns>
    private static string Redact(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(profile, StringComparison.OrdinalIgnoreCase) ? "%USERPROFILE%" + path[profile.Length..] : path;
    }

    /// <summary>Whether <paramref name="needle"/> occurs in <paramref name="haystack"/>.</summary>
    /// <param name="haystack">File bytes.</param>
    /// <param name="needle">Bytes to find.</param>
    /// <returns>Whether found.</returns>
    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}

/// <summary>
/// Passive live observation: file events, ScreenshotIndex and clipboard notifications on one timeline. Also the
/// engine of the self-test, pointed at scratch places there.
/// </summary>
sealed class Observer : IDisposable
{
    /// <summary>The timeline's zero: every line carries the milliseconds since the observer was created.</summary>
    private readonly Stopwatch clock = Stopwatch.StartNew();

    /// <summary>Folder watches to dispose; each raises its events one after another, so handlers must stay cheap.</summary>
    private readonly List<FileSystemWatcher> watchers = [];

    /// <summary>Every recorded line, in order, for the self-test's checks (written from several threads).</summary>
    private readonly ConcurrentQueue<string> lines = new();

    /// <summary>Files being followed to completion, so the several events of one save start only one follower.</summary>
    private readonly ConcurrentDictionary<string, byte> following = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether lines are printed as they happen (watch) or only collected (self-test).</summary>
    private readonly bool print;

    /// <summary>Creates an observer.</summary>
    /// <param name="print">Whether to print each line as it happens (the watch) or only collect it (the self-test).</param>
    public Observer(bool print) => this.print = print;

    /// <summary>Every line recorded so far, in order.</summary>
    public IReadOnlyCollection<string> Lines => lines;

    /// <summary>
    /// Watches a folder (non-recursively, like an integration watching the Screenshots folder would) and follows
    /// each touched file until it is readable and, for a PNG, complete.
    /// </summary>
    /// <param name="tag">Folder label for the output.</param>
    /// <param name="folder">The folder; a missing one is reported and skipped.</param>
    /// <param name="printNames">Whether names may be printed (scratch BC-TEST files) or only their class.</param>
    /// <exception cref="ArgumentException">The folder vanished between the existence check and the watch.</exception>
    /// <exception cref="IOException">Windows refused the change notification (e.g. a disconnected network folder).</exception>
    public void WatchFolder(string tag, string? folder, bool printNames)
    {
        if (folder is null || !Directory.Exists(folder))
        {
            Record($"file   ({tag} missing, not watched)");
            return;
        }

        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024,
        };
        void OnEvent(string kind, string path, string? oldPath)
        {
            var name = Path.GetFileName(path);
            var shown = printNames ? name : Shots.Classify(name, out _).ToString();
            var info = new FileInfo(path);

            // Cheap facts only: the watcher raises events one after another, so a slow handler (the holder query
            // takes ~140 ms) would delay every later event. Holders are followed by FollowUntilComplete instead.
            string state = info.Exists
                ? $"size {info.Length,10:N0}  write age {(DateTime.UtcNow - info.LastWriteTimeUtc).TotalSeconds,9:0.0} s"
                : "gone";
            string renamed = oldPath is null ? "" : $" (from {(printNames ? Path.GetFileName(oldPath) : Shots.Classify(Path.GetFileName(oldPath), out _).ToString())})";
            Record($"file   {kind,-8} {tag,-12} {shown}{renamed}  {state}");
            if (info.Exists && following.TryAdd(path, 0))
            {
                _ = Task.Run(() => FollowUntilComplete(tag, path, shown));
            }
        }

        watcher.Created += (_, e) => OnEvent("Created", e.FullPath, null);
        watcher.Changed += (_, e) => OnEvent("Changed", e.FullPath, null);
        watcher.Deleted += (_, e) =>
        {
            string shown = printNames ? e.Name ?? "" : Shots.Classify(e.Name ?? "", out Match? _).ToString();
            Record($"file   Deleted  {tag,-12} {shown}");
        };
        watcher.Renamed += (_, e) => OnEvent("Renamed", e.FullPath, e.OldFullPath);
        watcher.Error += (_, e) => Record($"file   ERROR    {tag}: {e.GetException().GetType().Name}");
        watcher.EnableRaisingEvents = true;
        watchers.Add(watcher);
        Record($"file   watching {tag}");
    }

    /// <summary>Polls ScreenshotIndex and records each change (Win+PrtScn bumps it when it saves).</summary>
    /// <param name="until">When to stop.</param>
    /// <returns>The polling task.</returns>
    public Task WatchScreenshotIndex(CancellationToken until) => Task.Run(async () =>
    {
        int? last = ReadIndex();
        Record($"index  ScreenshotIndex {last?.ToString() ?? "<absent>"}");
        while (!until.IsCancellationRequested)
        {
            await Task.Delay(20, CancellationToken.None);
            int? now = ReadIndex();
            if (now != last)
            {
                Record($"index  ScreenshotIndex {last?.ToString() ?? "<absent>"} -> {now?.ToString() ?? "<absent>"}");
                last = now;
            }
        }
    });

    /// <summary>Records a clipboard notification (called on the listener's window thread).</summary>
    public void RecordClipboard()
    {
        var foreground = GetForegroundWindow();
        Record($"clip   seq {GetClipboardSequenceNumber()}  {ClipboardMeta.Describe()}  foreground {(foreground == 0 ? "<none>" : ClipboardMeta.ProcessName(foreground))}");
    }

    /// <summary>Adds a timestamped line.</summary>
    /// <param name="text">The line.</param>
    public void Record(string text)
    {
        var line = $"+{clock.Elapsed.TotalMilliseconds,9:0} ms  {text}";
        lines.Enqueue(line);
        if (print)
        {
            Console.WriteLine(line);
        }
    }

    /// <summary>Stops the folder watches.</summary>
    public void Dispose()
    {
        foreach (var watcher in watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }

    /// <summary>
    /// Polls a file every 25 ms (up to 15 s) until no other process has held it for two polls in a row and, for a
    /// PNG, IEND ends it; records how long that took and how many reads found it incomplete.
    /// </summary>
    /// <remarks>
    /// Never interferes with the writer. Holders are asked through an attribute-only handle (which takes part in
    /// no sharing check), and the data is opened only once nobody else holds the file, with every sharing mode
    /// granted. An "is the writer done?" open that denies writing (<see cref="FileShare.Read"/>) would make a
    /// writer that closes and reopens its file — a common WinRT StorageFile pattern — fail with a sharing violation
    /// if the reopen landed inside that open.
    /// </remarks>
    /// <param name="tag">Folder label.</param>
    /// <param name="path">File.</param>
    /// <param name="shown">How the file is named in the output.</param>
    private void FollowUntilComplete(string tag, string path, string shown)
    {
        var started = clock.Elapsed;
        var deadline = started + TimeSpan.FromSeconds(15);
        int quietPolls = 0, incompleteReads = 0;
        string? lastHolders = null;
        while (clock.Elapsed < deadline)
        {
            var holders = FileHolders.Others(path);
            if (holders is null)
            {
                Record($"file   VANISHED {tag,-12} {shown}");
                following.TryRemove(path, out _);
                return;
            }

            // Who holds the file and when that changes: the writer, then often an antivirus scan or the shell's
            // thumbnailer — what decides when an integration can read without getting in anyone's way.
            string holderText = Describe(holders);
            if (holderText != lastHolders)
            {
                Record($"file   HELD     {tag,-12} {shown}  by {holderText}");
                lastHolders = holderText;
            }

            quietPolls = holders.Count == 0 ? quietPolls + 1 : 0;
            if (quietPolls >= 2)
            {
                try
                {
                    bool isPng = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
                    var png = isPng ? Shots.ReadPng(path, walkChunks: true) : null;
                    if (!isPng || png?.Complete == true)
                    {
                        var info = new FileInfo(path);
                        string shape = png is null ? "" : $"  {png.Width}x{png.Height} colour type {png.ColorType} chunks {png.Layout}";
                        Record($"file   READY    {tag,-12} {shown}  no other holder and complete {(clock.Elapsed - started).TotalMilliseconds:0} ms after the first event, " +
                               $"write age at ready {(DateTime.UtcNow - info.LastWriteTimeUtc).TotalSeconds:0.0} s, incomplete reads {incompleteReads}{shape}");
                        following.TryRemove(path, out _);
                        return;
                    }

                    incompleteReads++;
                }
                catch (FileNotFoundException)
                {
                    Record($"file   VANISHED {tag,-12} {shown}");
                    following.TryRemove(path, out _);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    incompleteReads++; // an exclusive opener holds it after all; try again
                }
            }

            Thread.Sleep(25);
        }

        Record($"file   TIMEOUT  {tag,-12} {shown}  still held or incomplete after 15 s");
        following.TryRemove(path, out _);
    }

    /// <summary>Names the processes holding a file.</summary>
    /// <param name="holders">Their ids (this process excluded), or <see langword="null"/> when the file is gone.</param>
    /// <returns>"nobody else", "gone" or the process names.</returns>
    private static string Describe(IReadOnlyList<int>? holders) =>
        holders is null ? "gone" : holders.Count == 0 ? "nobody else" : string.Join(", ", holders.Select(ClipboardMeta.ProcessNameById));

    /// <summary>Reads ScreenshotIndex.</summary>
    /// <returns>The value or <see langword="null"/>.</returns>
    private static int? ReadIndex()
    {
        using var explorer = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer");
        return explorer?.GetValue("ScreenshotIndex") as int?;
    }
}

/// <summary>A message-only window on its own thread that receives WM_CLIPBOARDUPDATE.</summary>
static class ListenerWindow
{
    /// <summary>Keeps window procedures alive for the process lifetime (the OS holds raw pointers to them).</summary>
    private static readonly List<WndProc> Procedures = [];

    /// <summary>
    /// Starts a thread with a message-only window. <paramref name="desktop"/> (non-zero) is set as the thread's
    /// desktop first — before any other user32 call, which is why the thread is MTA (an STA thread owns COM's
    /// hidden window, and SetThreadDesktop then fails with ERROR_BUSY).
    /// </summary>
    /// <param name="name">Window class suffix and thread name.</param>
    /// <param name="desktop">A desktop handle (the self-test's private one), or 0 for the current desktop.</param>
    /// <param name="init">Runs on the thread once the window exists.</param>
    /// <param name="handler">Message handler; return <see langword="null"/> for default processing.</param>
    /// <returns>The window handle, once created.</returns>
    /// <exception cref="InvalidOperationException">The desktop, class or window could not be set up.</exception>
    public static nint Start(string name, nint desktop, Action<nint> init, Func<nint, uint, nint, nint, nint?> handler)
    {
        var created = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (desktop != 0 && !SetThreadDesktop(desktop))
                {
                    throw new InvalidOperationException($"SetThreadDesktop failed {Marshal.GetLastPInvokeError()}");
                }

                WndProc procedure = (hwnd, message, wParam, lParam) => handler(hwnd, message, wParam, lParam) ?? DefWindowProcW(hwnd, message, wParam, lParam);
                lock (Procedures)
                {
                    Procedures.Add(procedure);
                }

                var windowClass = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(procedure),
                    lpszClassName = "BCScreenshotsProbe_" + name,
                    hInstance = GetModuleHandleW(null),
                };
                if (RegisterClassExW(ref windowClass) == 0)
                {
                    throw new InvalidOperationException($"RegisterClassEx failed {Marshal.GetLastPInvokeError()}");
                }

                var hwnd = CreateWindowExW(0, windowClass.lpszClassName, name, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, windowClass.hInstance, 0);
                if (hwnd == 0)
                {
                    throw new InvalidOperationException($"CreateWindowEx failed {Marshal.GetLastPInvokeError()}");
                }

                init(hwnd);
                created.SetResult(hwnd);
                while (GetMessageW(out var message, 0, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }
            }
            catch (Exception ex)
            {
                created.TrySetException(ex);
            }
        })
        { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return created.Task.GetAwaiter().GetResult();
    }
}

/// <summary>The live watch (opt-in: the user takes the screenshots).</summary>
static class Watch
{
    /// <summary>Watches for <paramref name="seconds"/> and prints the timeline as it happens.</summary>
    /// <param name="seconds">Duration.</param>
    /// <returns>Exit code 0.</returns>
    /// <exception cref="InvalidOperationException">The listener window could not be created or subscribed.</exception>
    /// <exception cref="ArgumentException">A screenshot folder vanished while its watch was being set up.</exception>
    /// <exception cref="IOException">Windows refused a folder's change notification.</exception>
    public static int Run(int seconds)
    {
        using var observer = new Observer(print: true);
        var videos = Shots.KnownFolder(Shots.Videos);
        observer.WatchFolder("Screenshots", Shots.KnownFolder(Shots.Screenshots), printNames: false);
        observer.WatchFolder("Captures", Shots.KnownFolder(Shots.Captures), printNames: false);
        observer.WatchFolder("Recordings", videos is null ? null : Path.Combine(videos, "Screen Recordings"), printNames: false);
        ListenerWindow.Start("watch", 0, hwnd =>
        {
            if (!AddClipboardFormatListener(hwnd))
            {
                throw new InvalidOperationException($"AddClipboardFormatListener failed {Marshal.GetLastPInvokeError()}");
            }
        }, (hwnd, message, wParam, lParam) =>
        {
            if (message != WM_CLIPBOARDUPDATE)
            {
                return null;
            }

            observer.RecordClipboard();
            return 0;
        });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var index = observer.WatchScreenshotIndex(stop.Token);
        observer.Record($"ready for {seconds} s — take one Win+PrtScn and one Win+Shift+S snip now (nothing is read but metadata)");
        index.Wait();
        observer.Record("done");
        return 0;
    }
}

/// <summary>The self-test: the observer's machinery on BC-TEST data in scratch places only.</summary>
static class SelfTest
{
    /// <summary>Runs the file half, then the clipboard half (which moves the process into a private window station).</summary>
    /// <returns>0 when every check passed, 1 otherwise.</returns>
    /// <exception cref="InvalidOperationException">The private window station, desktop or a window could not be created.</exception>
    /// <exception cref="IOException">The scratch folder could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The temp folder is not writable.</exception>
    public static int Run()
    {
        int failures = 0;
        var scratch = Path.Combine(Path.GetTempPath(), $"bc-screenshots-probe-{Environment.ProcessId}");
        var watched = Directory.CreateDirectory(Path.Combine(scratch, "Screenshots")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(scratch, "Elsewhere")).FullName;
        try
        {
            failures += Files(watched, outside);
        }
        finally
        {
            SnippingTool.TryDelete(scratch);
        }

        failures += Clipboard();
        Console.WriteLine(failures == 0 ? "self-test: all checks passed" : $"self-test: {failures} check(s) FAILED");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// A two-step write (a writer holding the file open between halves), a copy and a move into the folder of
    /// files last written in 2020, and a rename — what an integration must tell apart.
    /// </summary>
    /// <param name="watched">The scratch "Screenshots" folder.</param>
    /// <param name="outside">A scratch sibling folder.</param>
    /// <returns>The number of failed checks.</returns>
    /// <exception cref="IOException">A scratch file could not be written, copied or moved.</exception>
    /// <exception cref="UnauthorizedAccessException">The scratch folder is not writable.</exception>
    private static int Files(string watched, string outside)
    {
        Console.WriteLine();
        Console.WriteLine("── Files (scratch folder, BC-TEST names)");
        using var observer = new Observer(print: false);
        observer.WatchFolder("scratch", watched, printNames: true);
        var png = TinyPng();

        // 1. A fresh screenshot written in two halves while the writer keeps it open (like a slow encoder). The
        // pause covers several follow polls: each holder query takes ~140 ms on its own.
        var fresh = Path.Combine(watched, "BC-TEST Screenshot 2026-10-01 120000.png");
        using (var stream = new FileStream(fresh, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.Write(png, 0, png.Length / 2);
            stream.Flush();
            Thread.Sleep(1500);
            stream.Write(png, png.Length / 2, png.Length - (png.Length / 2));
        }

        // 2. An old screenshot copied in (a copy keeps the source's last-write time).
        var old = Path.Combine(outside, "BC-TEST Screenshot (7).png");
        File.WriteAllBytes(old, png);
        File.SetLastWriteTimeUtc(old, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.Copy(old, Path.Combine(watched, "BC-TEST Screenshot (7).png"));

        // 3. Another old one moved in from the sibling folder (same volume: a rename across folders).
        var moved = Path.Combine(outside, "BC-TEST moved 2020.png");
        File.WriteAllBytes(moved, png);
        File.SetLastWriteTimeUtc(moved, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.Move(moved, Path.Combine(watched, "BC-TEST moved 2020.png"));

        // Let the move's own events be handled while the file is still there under that name.
        Thread.Sleep(600);

        // 4. A rename inside the folder.
        File.Move(Path.Combine(watched, "BC-TEST moved 2020.png"), Path.Combine(watched, "BC-TEST renamed.png"));
        Thread.Sleep(1500);
        foreach (var line in observer.Lines)
        {
            Console.WriteLine("  " + line);
        }

        var all = string.Join('\n', observer.Lines);
        int failures = 0;

        // The writer is this process, so the holder list (which leaves this process out) cannot show it; the
        // half-written file is caught by its missing IEND instead.
        failures += Check("the two-step write is read as incomplete before it becomes READY", Regex.IsMatch(all, @"READY\s+scratch\s+BC-TEST Screenshot 2026-10-01 120000\.png .*incomplete reads [1-9]"));
        failures += Check("a copied-in file is READY with a years-old write time", Regex.IsMatch(all, @"READY\s+scratch\s+BC-TEST Screenshot \(7\)\.png .*write age at ready \d{6,}"));
        failures += Check("a moved-in file arrives as Created (not Renamed) with a years-old write time", Regex.IsMatch(all, @"Created\s+scratch\s+BC-TEST moved 2020\.png\s+size\s+[\d,]+\s+write age\s+\d{6,}"));
        failures += Check("a rename inside the folder arrives as Renamed", all.Contains("Renamed  scratch      BC-TEST renamed.png (from BC-TEST moved 2020.png)"));
        return failures;
    }

    /// <summary>
    /// In a private, anonymous window station (its own clipboard): a producer writes BC-TEST text plus a
    /// delayed-rendered "PNG"; the listener must name the owner and both formats without rendering anything.
    /// </summary>
    /// <returns>The number of failed checks.</returns>
    /// <exception cref="InvalidOperationException">The window station or desktop could not be created.</exception>
    private static int Clipboard()
    {
        Console.WriteLine();
        Console.WriteLine("── Clipboard (private window station, BC-TEST data)");
        nint station = CreateWindowStationW(null, 0, 0x37F, 0);
        if (station == 0 || !SetProcessWindowStation(station))
        {
            throw new InvalidOperationException($"window station setup failed {Marshal.GetLastPInvokeError()}");
        }

        nint desktop = CreateDesktopW("BCScreenshotsProbe", 0, 0, 0, 0x10000000, 0);
        if (desktop == 0)
        {
            throw new InvalidOperationException($"CreateDesktop failed {Marshal.GetLastPInvokeError()}");
        }

        using var observer = new Observer(print: false);
        var notified = new SemaphoreSlim(0);
        int renderRequests = 0;
        ListenerWindow.Start("listener", desktop, hwnd => AddClipboardFormatListener(hwnd), (hwnd, message, wParam, lParam) =>
        {
            if (message != WM_CLIPBOARDUPDATE)
            {
                return null;
            }

            observer.RecordClipboard();
            notified.Release();
            return 0;
        });
        uint png = RegisterClipboardFormatW("PNG");
        nint producer = ListenerWindow.Start("producer", desktop, _ => { }, (hwnd, message, wParam, lParam) =>
        {
            if (message == WM_RENDERFORMAT)
            {
                // Only reached if someone asked for the PNG's data: the listener must never do that.
                Interlocked.Increment(ref renderRequests);
                return 0;
            }

            if (message == WM_APP)
            {
                // The write runs on the producer's own thread: the owner window must belong to the opening thread.
                if (OpenClipboard(hwnd))
                {
                    EmptyClipboard();
                    SetClipboardData(CF_UNICODETEXT, Global("BC-TEST self-test"));
                    SetClipboardData(png, 0);
                    CloseClipboard();
                }

                return 0;
            }

            return null;
        });
        PostMessageW(producer, WM_APP, 0, 0);
        bool got = notified.Wait(TimeSpan.FromSeconds(5));
        Thread.Sleep(200);
        foreach (var line in observer.Lines)
        {
            Console.WriteLine("  " + line);
        }

        var all = string.Join('\n', observer.Lines);
        var self = Path.GetFileName(Environment.ProcessPath ?? "?");
        int failures = 0;
        failures += Check("a notification arrived", got);
        failures += Check($"the owner is this process ({self}) and its producer window", all.Contains($"owner {self} [BCScreenshotsProbe_producer]"));
        failures += Check("both formats are named without opening the clipboard", all.Contains("CF_UNICODETEXT") && all.Contains("PNG"));
        failures += Check("the delayed PNG was never rendered (no data read)", Volatile.Read(ref renderRequests) == 0);
        return failures;
    }

    /// <summary>Prints a check's verdict.</summary>
    /// <param name="what">What is checked.</param>
    /// <param name="passed">Whether it held.</param>
    /// <returns>1 when it failed, else 0.</returns>
    private static int Check(string what, bool passed)
    {
        Console.WriteLine($"  {(passed ? "PASS" : "FAIL")}  {what}");
        return passed ? 0 : 1;
    }

    /// <summary>Allocates a movable global block holding NUL-terminated UTF-16 text (ownership passes to the clipboard on success).</summary>
    /// <param name="text">BC-TEST text.</param>
    /// <returns>The HGLOBAL.</returns>
    private static nint Global(string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        nint memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
        nint pointer = GlobalLock(memory);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        GlobalUnlock(memory);
        return memory;
    }

    /// <summary>Builds a valid 2×2 RGBA PNG in memory (IHDR, IDAT, IEND with real CRCs).</summary>
    /// <returns>The file bytes.</returns>
    private static byte[] TinyPng()
    {
        using var output = new MemoryStream();
        output.Write(Shots.PngSignature);
        WriteChunk(output, "IHDR", [0, 0, 0, 2, 0, 0, 0, 2, 8, 6, 0, 0, 0]);
        using (var raw = new MemoryStream())
        {
            using (var zlib = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                // Two scanlines, filter byte 0, two RGBA pixels each.
                zlib.Write([0, 0xBC, 0x7E, 0x57, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0, 0x00, 0x00, 0x00, 0xFF, 0xBC, 0x7E, 0x57, 0xFF]);
            }

            WriteChunk(output, "IDAT", raw.ToArray());
        }

        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>Writes one PNG chunk: big-endian length, type, data, CRC-32 over type + data.</summary>
    /// <param name="output">Destination.</param>
    /// <param name="type">Four-letter chunk type.</param>
    /// <param name="data">Chunk data.</param>
    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
        output.Write(typeBytes);
        output.Write(data);
        uint crc = Crc32([.. typeBytes, .. data]);
        output.Write([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
    }

    /// <summary>CRC-32 (IEEE, reflected, PNG's checksum).</summary>
    /// <param name="bytes">Input.</param>
    /// <returns>The checksum.</returns>
    private static uint Crc32(byte[] bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }
}

/// <summary>Window procedure signature.</summary>
/// <param name="hwnd">Window.</param>
/// <param name="message">Message.</param>
/// <param name="wParam">First parameter.</param>
/// <param name="lParam">Second parameter.</param>
/// <returns>The message result.</returns>
delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

/// <summary>EnumResourceNames callback.</summary>
/// <param name="module">Module.</param>
/// <param name="type">Resource type.</param>
/// <param name="name">Resource name (an integer id for string tables).</param>
/// <param name="param">Caller data.</param>
/// <returns>Whether to continue.</returns>
delegate bool EnumResNameProc(nint module, nint type, nint name, nint param);

/// <summary>WNDCLASSEXW: registers the probe's message-only window classes.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct WNDCLASSEXW
{
    /// <summary>Size of this structure; RegisterClassEx rejects any other value.</summary>
    public uint cbSize;

    /// <summary>Class styles (none needed for a message-only window).</summary>
    public uint style;

    /// <summary>The window procedure; the delegate behind it must outlive the window (see ListenerWindow).</summary>
    public nint lpfnWndProc;

    /// <summary>Extra class bytes (unused).</summary>
    public int cbClsExtra;

    /// <summary>Extra window bytes (unused).</summary>
    public int cbWndExtra;

    /// <summary>The module registering the class.</summary>
    public nint hInstance;

    /// <summary>Unused.</summary>
    public nint hIcon;

    /// <summary>Unused.</summary>
    public nint hCursor;

    /// <summary>Unused.</summary>
    public nint hbrBackground;

    /// <summary>Unused.</summary>
    public string? lpszMenuName;

    /// <summary>Class name; also what GetClassName reports, which the self-test checks for the producer window.</summary>
    public string lpszClassName;

    /// <summary>Unused.</summary>
    public nint hIconSm;
}

/// <summary>MSG: one message from the thread's queue.</summary>
[StructLayout(LayoutKind.Sequential)]
struct MSG
{
    /// <summary>Target window.</summary>
    public nint hwnd;

    /// <summary>Message id.</summary>
    public uint message;

    /// <summary>First parameter.</summary>
    public nint wParam;

    /// <summary>Second parameter.</summary>
    public nint lParam;

    /// <summary>Post time.</summary>
    public uint time;

    /// <summary>Cursor x at post time.</summary>
    public int x;

    /// <summary>Cursor y at post time.</summary>
    public int y;
}

/// <summary>P/Invoke declarations (probe-local and DllImport-based; the app itself uses LibraryImport).</summary>
static class Native
{
    /// <summary>Sent to every format listener after the clipboard content changed.</summary>
    public const uint WM_CLIPBOARDUPDATE = 0x031D;

    /// <summary>Sent to the owner when someone reads a delayed-rendered format; the self-test counts it to prove nothing was read.</summary>
    public const uint WM_RENDERFORMAT = 0x0305;

    /// <summary>First private message id; the self-test's "write the clipboard now" signal to its producer thread.</summary>
    public const uint WM_APP = 0x8000;

    /// <summary>UTF-16 text.</summary>
    public const uint CF_UNICODETEXT = 13;

    /// <summary>A movable global block, the only kind SetClipboardData accepts.</summary>
    public const uint GMEM_MOVEABLE = 2;

    /// <summary>Resolve a known folder's path without checking that it exists (and without creating it).</summary>
    public const uint KF_FLAG_DONT_VERIFY = 0x4000;

    /// <summary>Registry read access.</summary>
    public const uint KEY_READ = 0x20019;

    /// <summary>The least process access that still allows QueryFullProcessImageName (works on most elevated processes too).</summary>
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>Map a module as data: none of its code runs.</summary>
    public const uint LOAD_LIBRARY_AS_DATAFILE = 0x2;

    /// <summary>Map a module's resources as an image so resource lookups (LoadString) work on a data file.</summary>
    public const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x20;

    /// <summary>Attribute-only access: outside every sharing check, so holding it never blocks a writer.</summary>
    public const uint FILE_READ_ATTRIBUTES = 0x80;

    /// <summary>Let others read.</summary>
    public const uint FILE_SHARE_READ = 1;

    /// <summary>Let others write.</summary>
    public const uint FILE_SHARE_WRITE = 2;

    /// <summary>Let others delete or rename.</summary>
    public const uint FILE_SHARE_DELETE = 4;

    /// <summary>Open only an existing file.</summary>
    public const uint OPEN_EXISTING = 3;

    /// <summary>FILE_INFORMATION_CLASS FileProcessIdsUsingFileInformation: the ids of processes that have the file open.</summary>
    public const int FileProcessIdsUsingFileInformation = 47;

    /// <summary>RegEnumValue's end of the list.</summary>
    public const int ERROR_NO_MORE_ITEMS = 259;

    /// <summary>The data is not available locally (an old-style offline file).</summary>
    public const int FILE_ATTRIBUTE_OFFLINE = 0x1000;

    /// <summary>Opening the file fetches it from a remote store (a cloud placeholder).</summary>
    public const int FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x40000;

    /// <summary>Reading the file's data downloads it (OneDrive Files On-Demand "online-only").</summary>
    public const int FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;

    /// <summary>Parent that makes a window message-only (no UI, no broadcasts, still gets clipboard notifications).</summary>
    public static readonly nint HWND_MESSAGE = -3;

    /// <summary>The string-table resource type.</summary>
    public static readonly nint RT_STRING = 6;

    /// <summary>Resolves a known folder; the returned string must be freed with CoTaskMemFree.</summary>
    /// <param name="id">KNOWNFOLDERID.</param>
    /// <param name="flags">KF_FLAG_*.</param>
    /// <param name="token">User token (0 = the caller).</param>
    /// <param name="path">The path (CoTaskMem).</param>
    /// <returns>An HRESULT.</returns>
    [DllImport("shell32", CharSet = CharSet.Unicode)] public static extern int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);

    /// <summary>Loads an app-data registry hive privately (invisible to other processes); closing the handle unloads it.</summary>
    /// <param name="file">Hive file (the probe passes a private copy only).</param>
    /// <param name="hkey">The hive's root key.</param>
    /// <param name="sam">Access.</param>
    /// <param name="options">0, or REG_PROCESS_APPKEY.</param>
    /// <param name="reserved">0.</param>
    /// <returns>A Win32 error code.</returns>
    [DllImport("advapi32", CharSet = CharSet.Unicode)] public static extern int RegLoadAppKeyW(string file, out nint hkey, uint sam, uint options, uint reserved);

    /// <summary>Opens a subkey.</summary>
    /// <param name="key">Parent key.</param>
    /// <param name="subKey">Subkey name.</param>
    /// <param name="options">0.</param>
    /// <param name="sam">Access.</param>
    /// <param name="result">The subkey.</param>
    /// <returns>A Win32 error code.</returns>
    [DllImport("advapi32", CharSet = CharSet.Unicode)] public static extern int RegOpenKeyExW(nint key, string subKey, uint options, uint sam, out nint result);

    /// <summary>Reads the value at an index with its raw type (ApplicationData uses types .NET does not name).</summary>
    /// <param name="key">Key.</param>
    /// <param name="index">Value index.</param>
    /// <param name="name">Name buffer.</param>
    /// <param name="nameLength">In: buffer chars; out: name chars.</param>
    /// <param name="reserved">0.</param>
    /// <param name="type">Registry type.</param>
    /// <param name="data">Data buffer.</param>
    /// <param name="dataLength">In: buffer bytes; out: data bytes.</param>
    /// <returns>A Win32 error code (ERROR_NO_MORE_ITEMS at the end, ERROR_MORE_DATA for a too-small buffer).</returns>
    [DllImport("advapi32", CharSet = CharSet.Unicode)] public static extern int RegEnumValueW(nint key, uint index, char[] name, ref uint nameLength, nint reserved, out uint type, byte[] data, ref uint dataLength);

    /// <summary>Closes a key (the last handle of a private hive unloads it).</summary>
    /// <param name="key">Key.</param>
    /// <returns>A Win32 error code.</returns>
    [DllImport("advapi32")] public static extern int RegCloseKey(nint key);

    /// <summary>Maps a module (the probe: a .mui as data only).</summary>
    /// <param name="path">File.</param>
    /// <param name="file">0.</param>
    /// <param name="flags">LOAD_LIBRARY_*.</param>
    /// <returns>The module handle, or 0.</returns>
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] public static extern nint LoadLibraryExW(string path, nint file, uint flags);

    /// <summary>Unmaps a module.</summary>
    /// <param name="module">Module.</param>
    /// <returns>Whether it succeeded.</returns>
    [DllImport("kernel32")] public static extern bool FreeLibrary(nint module);

    /// <summary>Enumerates a module's resources of one type.</summary>
    /// <param name="module">Module.</param>
    /// <param name="type">Resource type.</param>
    /// <param name="callback">Called per resource name.</param>
    /// <param name="param">Caller data.</param>
    /// <returns>Whether it succeeded.</returns>
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern bool EnumResourceNamesW(nint module, nint type, EnumResNameProc callback, nint param);

    /// <summary>Loads a string resource.</summary>
    /// <param name="module">Module.</param>
    /// <param name="id">String id.</param>
    /// <param name="buffer">Destination.</param>
    /// <param name="max">Buffer chars.</param>
    /// <returns>Characters copied (0 = none).</returns>
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int LoadStringW(nint module, uint id, char[] buffer, int max);

    /// <summary>Subscribes a window to WM_CLIPBOARDUPDATE.</summary>
    /// <param name="hwnd">Window.</param>
    /// <returns>Whether it succeeded.</returns>
    [DllImport("user32", SetLastError = true)] public static extern bool AddClipboardFormatListener(nint hwnd);

    /// <summary>The clipboard's change counter for this window station.</summary>
    /// <returns>The number.</returns>
    [DllImport("user32")] public static extern uint GetClipboardSequenceNumber();

    /// <summary>The window that owns the clipboard content (no OpenClipboard needed).</summary>
    /// <returns>The window, or 0.</returns>
    [DllImport("user32")] public static extern nint GetClipboardOwner();

    /// <summary>The formats on the clipboard, without opening it.</summary>
    /// <param name="formats">Destination.</param>
    /// <param name="count">Destination size.</param>
    /// <param name="countOut">Formats available.</param>
    /// <returns>Whether it succeeded.</returns>
    [DllImport("user32", SetLastError = true)] public static extern bool GetUpdatedClipboardFormats(uint[] formats, uint count, out uint countOut);

    /// <summary>A registered format's name.</summary>
    /// <param name="format">Format id.</param>
    /// <param name="name">Destination.</param>
    /// <param name="max">Destination chars.</param>
    /// <returns>Characters copied (0 = predefined or unknown).</returns>
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetClipboardFormatNameW(uint format, char[] name, int max);

    /// <summary>Registers (or looks up) a named clipboard format.</summary>
    /// <param name="name">Name.</param>
    /// <returns>The id (per session).</returns>
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern uint RegisterClipboardFormatW(string name);

    /// <summary>Opens the clipboard (self-test producer only; the observer never calls it).</summary>
    /// <param name="hwnd">Owner-to-be.</param>
    /// <returns>Whether it succeeded.</returns>
    [DllImport("user32", SetLastError = true)] public static extern bool OpenClipboard(nint hwnd);

    /// <summary>Closes the clipboard.</summary>
    /// <returns>Whether it succeeded.</returns>
    [DllImport("user32", SetLastError = true)] public static extern bool CloseClipboard();

    /// <summary>Empties the clipboard and makes the opener the owner.</summary>
    /// <returns>Whether it succeeded.</returns>
    [DllImport("user32", SetLastError = true)] public static extern bool EmptyClipboard();

    /// <summary>Puts a format on the clipboard; memory 0 promises it for delayed rendering.</summary>
    /// <param name="format">Format id.</param>
    /// <param name="memory">HGLOBAL (owned by the system on success) or 0.</param>
    /// <returns>The handle, or 0 on failure.</returns>
    [DllImport("user32", SetLastError = true)] public static extern nint SetClipboardData(uint format, nint memory);

    /// <summary>The foreground window (what BetterClipboard falls back to when the clipboard has no owner).</summary>
    /// <returns>The window, or 0.</returns>
    [DllImport("user32")] public static extern nint GetForegroundWindow();

    /// <summary>A window's thread and process.</summary>
    /// <param name="hwnd">Window.</param>
    /// <param name="pid">Process id.</param>
    /// <returns>Thread id.</returns>
    [DllImport("user32")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    /// <summary>A window's class name.</summary>
    /// <param name="hwnd">Window.</param>
    /// <param name="name">Destination.</param>
    /// <param name="max">Destination chars.</param>
    /// <returns>Characters copied.</returns>
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(nint hwnd, char[] name, int max);

    /// <summary>Opens a process.</summary>
    /// <param name="access">Access.</param>
    /// <param name="inherit">Inheritable handle.</param>
    /// <param name="pid">Process id.</param>
    /// <returns>The handle, or 0.</returns>
    [DllImport("kernel32", SetLastError = true)] public static extern nint OpenProcess(uint access, bool inherit, uint pid);

    /// <summary>A process's image path.</summary>
    /// <param name="process">Process handle.</param>
    /// <param name="flags">0 = Win32 path.</param>
    /// <param name="name">Destination.</param>
    /// <param name="size">In: chars; out: chars copied.</param>
    /// <returns>Whether it succeeded.</returns>
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageNameW(nint process, uint flags, char[] name, ref uint size);

    /// <summary>Closes a kernel handle.</summary>
    /// <param name="handle">Handle.</param>
    /// <returns>Whether it succeeded.</returns>
    [DllImport("kernel32")] public static extern bool CloseHandle(nint handle);

    /// <summary>Opens a file (the probe: attribute-only, all sharing granted).</summary>
    /// <param name="path">File.</param>
    /// <param name="access">Desired access.</param>
    /// <param name="share">Share mode.</param>
    /// <param name="security">0.</param>
    /// <param name="disposition">OPEN_EXISTING.</param>
    /// <param name="flags">0.</param>
    /// <param name="template">0.</param>
    /// <returns>The handle (invalid when the file is gone).</returns>
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    /// <summary>Queries a file-information class (the probe: the processes holding the file).</summary>
    /// <param name="handle">File handle.</param>
    /// <param name="status">IO_STATUS_BLOCK (two pointer-sized fields).</param>
    /// <param name="buffer">Destination.</param>
    /// <param name="length">Destination bytes.</param>
    /// <param name="informationClass">FILE_INFORMATION_CLASS.</param>
    /// <returns>An NTSTATUS (0 = success).</returns>
    [DllImport("ntdll")] public static extern int NtQueryInformationFile(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out IoStatusBlock status, byte[] buffer, uint length, int informationClass);

    /// <summary>Allocates global memory.</summary>
    /// <param name="flags">GMEM_*.</param>
    /// <param name="bytes">Size.</param>
    /// <returns>The HGLOBAL, or 0.</returns>
    [DllImport("kernel32", SetLastError = true)] public static extern nint GlobalAlloc(uint flags, nuint bytes);

    /// <summary>Locks global memory for writing.</summary>
    /// <param name="memory">HGLOBAL.</param>
    /// <returns>The pointer.</returns>
    [DllImport("kernel32", SetLastError = true)] public static extern nint GlobalLock(nint memory);

    /// <summary>Unlocks global memory.</summary>
    /// <param name="memory">HGLOBAL.</param>
    /// <returns>Whether it is still locked.</returns>
    [DllImport("kernel32", SetLastError = true)] public static extern bool GlobalUnlock(nint memory);

    /// <summary>Creates a window station; an anonymous one (name null) needs no elevation and has its own clipboard.</summary>
    /// <param name="name">Name, or null.</param>
    /// <param name="flags">0.</param>
    /// <param name="access">Access.</param>
    /// <param name="attributes">0.</param>
    /// <returns>The station, or 0.</returns>
    [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode)] public static extern nint CreateWindowStationW(string? name, uint flags, uint access, nint attributes);

    /// <summary>Moves the process into a window station (every later clipboard call goes there).</summary>
    /// <param name="station">Station.</param>
    /// <returns>Whether it succeeded.</returns>
    [DllImport("user32", SetLastError = true)] public static extern bool SetProcessWindowStation(nint station);

    /// <summary>Creates a desktop in the current window station.</summary>
    /// <param name="name">Name.</param>
    /// <param name="device">0.</param>
    /// <param name="mode">0.</param>
    /// <param name="flags">0.</param>
    /// <param name="access">Access.</param>
    /// <param name="attributes">0.</param>
    /// <returns>The desktop, or 0.</returns>
    [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode)] public static extern nint CreateDesktopW(string name, nint device, nint mode, uint flags, uint access, nint attributes);

    /// <summary>Assigns a desktop to the calling thread (before it creates any window).</summary>
    /// <param name="desktop">Desktop.</param>
    /// <returns>Whether it succeeded.</returns>
    [DllImport("user32", SetLastError = true)] public static extern bool SetThreadDesktop(nint desktop);

    /// <summary>Posts a message.</summary>
    /// <param name="hwnd">Window.</param>
    /// <param name="message">Message.</param>
    /// <param name="wParam">First parameter.</param>
    /// <param name="lParam">Second parameter.</param>
    /// <returns>Whether it was posted.</returns>
    [DllImport("user32", SetLastError = true)] public static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>Default message processing.</summary>
    /// <param name="hwnd">Window.</param>
    /// <param name="message">Message.</param>
    /// <param name="wParam">First parameter.</param>
    /// <param name="lParam">Second parameter.</param>
    /// <returns>The result.</returns>
    [DllImport("user32")] public static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>Registers a window class.</summary>
    /// <param name="windowClass">Class description.</param>
    /// <returns>The class atom, or 0.</returns>
    [DllImport("user32", SetLastError = true)] public static extern ushort RegisterClassExW(ref WNDCLASSEXW windowClass);

    /// <summary>Creates a window (the probe: message-only).</summary>
    /// <param name="exStyle">Extended style.</param>
    /// <param name="className">Class.</param>
    /// <param name="name">Title.</param>
    /// <param name="style">Style.</param>
    /// <param name="x">X.</param>
    /// <param name="y">Y.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="parent">HWND_MESSAGE.</param>
    /// <param name="menu">0.</param>
    /// <param name="instance">Module.</param>
    /// <param name="param">0.</param>
    /// <returns>The window, or 0.</returns>
    [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode)] public static extern nint CreateWindowExW(uint exStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    /// <summary>The handle of a loaded module (null = this exe).</summary>
    /// <param name="name">Module name or null.</param>
    /// <returns>The handle.</returns>
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);

    /// <summary>Waits for the next message.</summary>
    /// <param name="message">The message.</param>
    /// <param name="hwnd">0 = any window of the thread.</param>
    /// <param name="min">0.</param>
    /// <param name="max">0.</param>
    /// <returns>&gt; 0 for a message, 0 for WM_QUIT, -1 on error.</returns>
    [DllImport("user32")] public static extern int GetMessageW(out MSG message, nint hwnd, uint min, uint max);

    /// <summary>Translates key messages.</summary>
    /// <param name="message">The message.</param>
    /// <returns>Whether a character message was posted.</returns>
    [DllImport("user32")] public static extern bool TranslateMessage(ref MSG message);

    /// <summary>Calls the window procedure.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The procedure's result.</returns>
    [DllImport("user32")] public static extern nint DispatchMessageW(ref MSG message);
}

/// <summary>IO_STATUS_BLOCK: the status and byte count of an Nt* file call.</summary>
[StructLayout(LayoutKind.Sequential)]
struct IoStatusBlock
{
    /// <summary>NTSTATUS (a union with a pointer, hence pointer-sized).</summary>
    public nint Status;

    /// <summary>Bytes written to the buffer.</summary>
    public nint Information;
}
