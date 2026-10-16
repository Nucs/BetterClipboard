#:property PublishAot=false
#:property AllowUnsafeBlocks=true
#:property TargetFramework=net10.0-windows
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Native;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

// ─────────────────────────────────────────────────────────────────────────────────────────────────────
// Probe: what do Word, Teams, LibreOffice, browsers and other apps put on the clipboard for a rich copy, and what could
// BetterClipboard show as a preview of it?
//
// Modes:
//   --describe [--all] [--no-metafile] [--save DIR]
//       The clipboard as it is now, once: every format in the producer's order with its id and size, how long Windows
//       took to hand it over (a long wait means the producer rendered it on request: delayed rendering), and a
//       content-free summary of every format a preview could use. HTML: the CF_HTML offsets, which program made it, tag
//       and CSS-property counts, the <style> blocks in its head, where its images point (data:, file:, http(s):, cid:,
//       blob:), tables, lists, Office markup, active content, Teams' schema.skype.com items. RTF: the generator, picture
//       kinds and bytes, tables, fields, fonts, and the blocks Word adds (theme, data store, latent styles). Metafiles:
//       frame size, records, EMF+, and how long GDI takes to draw them. Bitmaps: size. File lists: count and extensions.
//       Small descriptors: Object Descriptor's object type, vscode-editor-data's language, Chromium's custom MIME types.
//       Only those formats are read by default; --all reads every format (OLE storages included, which asks the producer
//       to render them: slow for a large Office selection). --no-metafile skips CF_ENHMETAFILE (asking Word or Excel
//       for one makes them draw the whole selection).
//   --watch [seconds] [--delay ms] [--all] [--no-metafile] [--save DIR]   (defaults: 120 s, 750 ms)
//       The same for every copy made while it runs. Each copy is read --delay after its notification, so that
//       BetterClipboard (which reads at once) has finished with the clipboard before the probe opens it; a copy that is
//       replaced before then is counted as replaced. This is the live check to run on your own PC while you copy from
//       Word, Teams, Outlook or Excel (CLAUDE.md §2.32): it prints no text, no HTML or RTF content, no file name and no
//       URL path or query, only a known host's name (Teams, Outlook, SharePoint) or "another host".
//   --save DIR
//       Also writes every format it read to DIR, one folder per copy (the raw payloads, the metafile and a PNG drawing of
//       it, and the printed summary), but only for copies whose text or HTML contains the marker BC-TEST, so a real copy
//       is never written to disk. For test documents on a disposable desktop.
//   --census FILE
//       The summary of a saved payload: .rtf, .md (Markdown), or .html with or without the CF_HTML header.
//   --render-rtf FILE.rtf OUT.png [--width px]   (default 640 px)
//       Draws an RTF file with Windows' own RichEdit engine (Msftedit.dll, the engine under WinUI's RichEditBox)
//       through a hidden window and EM_FORMATRANGE: load, layout and draw times, the text length, the OLE objects it
//       made, the height, and a PNG to judge what survives (tables, lists, pictures, colors, fonts).
//   --render-emf FILE.emf OUT.png [--width px]
//       Draws an enhanced metafile with GDI's PlayEnhMetaFile (EMF+ records are not played): time and a PNG.
//
// Run: dotnet run tools/probes/probe_rich_formats.cs -- --watch 120
// It never writes to the clipboard. Results (Windows 11 26200, 2026-10-16): CLAUDE.md §2.32.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────

Console.OutputEncoding = Encoding.UTF8;
var options = Options.Parse(args);
if (options is null)
{
    Console.WriteLine(Options.Usage);
    return 2;
}

Console.WriteLine($"Windows {Environment.OSVersion.Version} · {RuntimeInformation.FrameworkDescription} · {DateTime.UtcNow:yyyy-MM-dd HH:mm}Z");
return options.Mode switch
{
    "--describe" => Modes.Describe(options),
    "--watch" => new ClipboardWatcher(options).Run(),
    "--census" => Modes.Census(options),
    "--render-rtf" => Modes.RenderRtf(options),
    "--render-emf" => Modes.RenderEmf(options),
    _ => 2,
};

// ═════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>The probe's command line: the mode and its arguments.</summary>
sealed class Options
{
    /// <summary>Printed for a command line that does not parse.</summary>
    public const string Usage =
        "usage: probe_rich_formats.cs -- [--describe | --watch [seconds]] [--delay ms] [--all] [--no-metafile] [--save DIR]\n"
        + "       probe_rich_formats.cs -- --census FILE\n"
        + "       probe_rich_formats.cs -- --render-rtf FILE.rtf OUT.png [--width px]\n"
        + "       probe_rich_formats.cs -- --render-emf FILE.emf OUT.png [--width px]";

    /// <summary>The mode, e.g. <c>--watch</c>.</summary>
    public string Mode { get; private set; } = "--describe";

    /// <summary>How long <c>--watch</c> runs, in seconds.</summary>
    public int Seconds { get; private set; } = 120;

    /// <summary>How long <c>--watch</c> waits after a notification before it reads, so that other readers go first.</summary>
    public int DelayMs { get; private set; } = 750;

    /// <summary>Read every format, not only the ones a preview could use (asks OLE producers to render storages).</summary>
    public bool ReadAll { get; private set; }

    /// <summary>Skip <c>CF_ENHMETAFILE</c>: asking Word or Excel for it makes them draw the whole selection.</summary>
    public bool SkipMetafile { get; private set; }

    /// <summary>Where copies marked BC-TEST are saved, or <see langword="null"/> to save nothing.</summary>
    public string? SaveDir { get; private set; }

    /// <summary>The input file of <c>--census</c> and of the render modes.</summary>
    public string? Input { get; private set; }

    /// <summary>The PNG the render modes write.</summary>
    public string? Output { get; private set; }

    /// <summary>The drawing width of the render modes, in pixels; 0 = the picture's own width (metafiles).</summary>
    public int WidthPx { get; private set; }

    /// <summary>Parses the command line.</summary>
    /// <param name="args">The arguments after <c>--</c>.</param>
    /// <returns>The options, or <see langword="null"/> when they do not parse (the caller prints <see cref="Usage"/>).</returns>
    public static Options? Parse(string[] args)
    {
        var options = new Options();
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--describe" or "--watch" or "--census" or "--render-rtf" or "--render-emf" when i == 0:
                    options.Mode = arg;
                    break;
                case "--all":
                    options.ReadAll = true;
                    break;
                case "--no-metafile":
                    options.SkipMetafile = true;
                    break;
                case "--save" when i + 1 < args.Length:
                    options.SaveDir = args[++i];
                    break;
                case "--delay" when i + 1 < args.Length && int.TryParse(args[i + 1], out int delay) && delay >= 0:
                    options.DelayMs = delay;
                    i++;
                    break;
                case "--width" when i + 1 < args.Length && int.TryParse(args[i + 1], out int width) && width is > 0 and <= 8000:
                    options.WidthPx = width;
                    i++;
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        return null;
                    }

                    positional.Add(arg);
                    break;
            }
        }

        switch (options.Mode)
        {
            case "--watch":
                if (positional.Count > 1 || (positional.Count == 1 && !int.TryParse(positional[0], out _)))
                {
                    return null;
                }

                if (positional.Count == 1)
                {
                    options.Seconds = Math.Clamp(int.Parse(positional[0], CultureInfo.InvariantCulture), 1, 3600);
                }

                return options;
            case "--describe":
                return positional.Count == 0 ? options : null;
            case "--census":
                if (positional.Count != 1)
                {
                    return null;
                }

                options.Input = positional[0];
                return options;
            default:
                if (positional.Count != 2)
                {
                    return null;
                }

                options.Input = positional[0];
                options.Output = positional[1];
                return options;
        }
    }
}

/// <summary>The one-shot modes.</summary>
static class Modes
{
    /// <summary><c>--describe</c>: the clipboard as it is now.</summary>
    /// <param name="options">The command line.</param>
    /// <returns>0, or 1 when the clipboard stayed locked.</returns>
    public static int Describe(Options options)
    {
        var copy = ClipboardSnapshot.Take(0, options);
        if (copy is null)
        {
            Console.WriteLine("The clipboard could not be opened: another program held it for 0.5 s.");
            return 1;
        }

        Report.PrintAndSave(copy, 1, options);
        return 0;
    }

    /// <summary><c>--census</c>: the HTML or RTF summary of a saved payload file.</summary>
    /// <param name="options">The command line (<see cref="Options.Input"/> is the file).</param>
    /// <returns>0, or 1 when the file cannot be read.</returns>
    public static int Census(Options options)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(options.Input!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Cannot read the file: {ex.Message}");
            return 1;
        }

        var facts = new List<string>();
        if (options.Input!.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase))
        {
            Rtf.Census(data, facts);
        }
        else if (options.Input.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            Text.Markdown(data, facts);
        }
        else if (Encoding.ASCII.GetString(data, 0, Math.Min(8, data.Length)) == "Version:")
        {
            Html.CfHtml(data, facts);
        }
        else
        {
            Html.Plain(data, facts);
        }

        Console.WriteLine($"{Path.GetFileName(options.Input)} · {data.Length:N0} B");
        foreach (var fact in facts)
        {
            Console.WriteLine("  " + fact);
        }

        return 0;
    }

    /// <summary><c>--render-rtf</c>: draws an RTF file with RichEdit and writes a PNG.</summary>
    /// <param name="options">The command line (input, output, width).</param>
    /// <returns>0, or 1 when the file cannot be read or drawn.</returns>
    public static int RenderRtf(Options options)
    {
        byte[] rtf;
        try
        {
            rtf = File.ReadAllBytes(options.Input!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Cannot read the file: {ex.Message}");
            return 1;
        }

        int width = options.WidthPx > 0 ? options.WidthPx : 640;
        var result = RtfRenderer.Render(rtf, width);
        if (result.Error is { } error)
        {
            Console.WriteLine($"RichEdit could not draw it: {error}");
            return 1;
        }

        File.WriteAllBytes(options.Output!, Png.Encode(result.Pixels, result.Width, result.Height));
        Console.WriteLine($"{Path.GetFileName(options.Input)} · {rtf.Length:N0} B of RTF → {result.Characters:N0} characters, {result.OleObjects} OLE object(s)");
        Console.WriteLine($"  RichEdit {result.EngineVersion}; load {result.LoadMs:0.0} ms, layout {result.LayoutMs:0.0} ms, draw {result.DrawMs:0.0} ms");
        Console.WriteLine($"  {result.Width} × {result.Height} px at {result.Dpi} DPI{(result.Truncated ? " (cut at the height limit)" : "")} → {options.Output}");
        return 0;
    }

    /// <summary><c>--render-emf</c>: draws an enhanced metafile with GDI and writes a PNG.</summary>
    /// <param name="options">The command line (input, output, width).</param>
    /// <returns>0, or 1 when the file cannot be read or is not a metafile.</returns>
    public static int RenderEmf(Options options)
    {
        byte[] bits;
        try
        {
            bits = File.ReadAllBytes(options.Input!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Cannot read the file: {ex.Message}");
            return 1;
        }

        var facts = new List<string>();
        var drawing = Emf.Describe(bits, facts, options.WidthPx);
        Console.WriteLine($"{Path.GetFileName(options.Input)} · {bits.Length:N0} B");
        foreach (var fact in facts)
        {
            Console.WriteLine("  " + fact);
        }

        if (drawing is null)
        {
            return 1;
        }

        File.WriteAllBytes(options.Output!, drawing);
        Console.WriteLine($"  → {options.Output}");
        return 0;
    }
}

/// <summary>
/// <c>--watch</c>: a message-only window that listens for clipboard changes (the same notification BetterClipboard uses)
/// and describes each copy a moment later.
/// </summary>
sealed class ClipboardWatcher
{
    /// <summary>The command line.</summary>
    private readonly Options options;

    /// <summary>The window procedure; held here because the window keeps only a native pointer to it.</summary>
    private readonly WndProc procedure;

    /// <summary>Elapsed time since the watch started (deadline and read delays).</summary>
    private readonly Stopwatch clock = Stopwatch.StartNew();

    /// <summary>The sequence number of a copy waiting for its read, or <see langword="null"/>.</summary>
    private uint? pendingSequence;

    /// <summary>When (in <see cref="clock"/> milliseconds) the pending copy is read.</summary>
    private long pendingAtMs;

    /// <summary>Counters for the closing line.</summary>
    private int notifications, described, replaced;

    /// <summary>Creates the watcher.</summary>
    /// <param name="options">The command line.</param>
    public ClipboardWatcher(Options options)
    {
        this.options = options;
        procedure = WindowProcedure;
    }

    /// <summary>Runs the watch until <see cref="Options.Seconds"/> have passed.</summary>
    /// <returns>0, or 1 when the listener could not be set up.</returns>
    public int Run()
    {
        nint instance = GetModuleHandleW(null);
        var windowClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(procedure),
            hInstance = instance,
            lpszClassName = "BetterClipboardRichFormatsProbe",
        };
        if (RegisterClassExW(ref windowClass) == 0)
        {
            Console.WriteLine($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()}).");
            return 1;
        }

        nint window = CreateWindowExW(0, "BetterClipboardRichFormatsProbe", string.Empty, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, instance, 0);
        if (window == 0 || !AddClipboardFormatListener(window))
        {
            Console.WriteLine($"The clipboard listener could not be set up ({Marshal.GetLastPInvokeError()}).");
            return 1;
        }

        // A 50 ms timer drives both the delayed reads and the deadline, so the loop needs no second thread.
        SetTimer(window, 1, 50, 0);
        Console.WriteLine($"Watching the clipboard for {options.Seconds} s; each copy is read {options.DelayMs} ms after its notification. Copy now.");
        while (GetMessageW(out var message, 0, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }

        RemoveClipboardFormatListener(window);
        DestroyWindow(window);
        Console.WriteLine($"Done: {notifications} notification(s), {described} cop(ies) described, {replaced} replaced before they could be read.");
        return 0;
    }

    /// <summary>The message-only window's procedure.</summary>
    /// <param name="window">The window.</param>
    /// <param name="message">The message.</param>
    /// <param name="wParam">First parameter.</param>
    /// <param name="lParam">Second parameter.</param>
    /// <returns>The message result.</returns>
    private nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        // An exception must never cross back into user32: report it and carry on.
        try
        {
            switch (message)
            {
                case WM_CLIPBOARDUPDATE:
                    notifications++;
                    if (pendingSequence is not null)
                    {
                        replaced++;
                    }

                    pendingSequence = GetClipboardSequenceNumber();
                    pendingAtMs = clock.ElapsedMilliseconds + options.DelayMs;
                    return 0;
                case WM_TIMER:
                    if (pendingSequence is not null && clock.ElapsedMilliseconds >= pendingAtMs)
                    {
                        pendingSequence = null;
                        var copy = ClipboardSnapshot.Take(window, options);
                        if (copy is null)
                        {
                            Console.WriteLine("(a copy could not be read: the clipboard stayed locked for 0.5 s)");
                        }
                        else
                        {
                            described++;
                            Report.PrintAndSave(copy, described, options);
                        }
                    }

                    if (pendingSequence is null && clock.Elapsed.TotalSeconds >= options.Seconds)
                    {
                        PostQuitMessage(0);
                    }

                    return 0;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"(error: {ex.GetType().Name}: {ex.Message})");
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }
}

/// <summary>One read of the clipboard: who put it there and every format with what the probe learned about it.</summary>
sealed class ClipboardSnapshot
{
    /// <summary>The clipboard sequence number while the probe held it.</summary>
    public uint Sequence { get; private set; }

    /// <summary>The clipboard owner as "process (window class)"; OLE producers show OLE's own clipboard window class.</summary>
    public string Owner { get; private set; } = string.Empty;

    /// <summary>The foreground window's process, the usual guess for the source app.</summary>
    public string Foreground { get; private set; } = string.Empty;

    /// <summary>How long the probe kept the clipboard open (other programs cannot copy or paste meanwhile).</summary>
    public double HeldMs { get; private set; }

    /// <summary>The formats in the producer's order (synthesized ones last).</summary>
    public List<FormatReport> Formats { get; } = [];

    /// <summary>When the probe read it (local time, for folder names and the report).</summary>
    public DateTime ReadAt { get; private set; }

    /// <summary>
    /// Whether the copy's text (or, without text, its HTML) contains the marker <c>BC-TEST</c>: only such copies are ever
    /// saved.
    /// </summary>
    public bool HasTestMarker =>
        Formats.Any(f => f.Name == "CF_UNICODETEXT" && f.Data is { } d && Text.Utf16Z(d).Contains(Report.TestMarker, StringComparison.Ordinal))
        || (!Formats.Any(f => f.Name == "CF_UNICODETEXT" && f.Data is not null)
            && Formats.Any(f => f.Name.Contains("HTML", StringComparison.OrdinalIgnoreCase) && f.Data is { } h && Encoding.UTF8.GetString(h).Contains(Report.TestMarker, StringComparison.Ordinal)));

    /// <summary>Opens the clipboard, lists its formats, reads the useful ones, closes it, then analyzes them.</summary>
    /// <param name="owner">The window that opens the clipboard (0 = this thread's task).</param>
    /// <param name="options">Which formats to read.</param>
    /// <returns>The snapshot, or <see langword="null"/> when the clipboard stayed locked for 0.5 s.</returns>
    public static ClipboardSnapshot? Take(nint owner, Options options)
    {
        var snapshot = new ClipboardSnapshot
        {
            Owner = WindowInfo.Describe(GetClipboardOwner(), withClass: true),
            Foreground = WindowInfo.Describe(GetForegroundWindow(), withClass: false),
            ReadAt = DateTime.Now,
        };

        // Another reader (BetterClipboard itself) may hold it for a moment: retry like BetterClipboard does.
        bool opened = false;
        for (int attempt = 0; attempt < 50 && !opened; attempt++)
        {
            opened = OpenClipboard(owner);
            if (!opened)
            {
                Thread.Sleep(10);
            }
        }

        if (!opened)
        {
            return null;
        }

        var held = Stopwatch.StartNew();
        try
        {
            snapshot.Sequence = GetClipboardSequenceNumber();
            for (uint id = EnumClipboardFormats(0); id != 0; id = EnumClipboardFormats(id))
            {
                snapshot.Formats.Add(new FormatReport(id, FormatNames.Of(id)));
            }

            foreach (var format in snapshot.Formats)
            {
                if (format.Id == CF_ENHMETAFILE && options.SkipMetafile)
                {
                    continue;
                }

                if (options.ReadAll || FormatNames.IsUseful(format.Id, format.Name))
                {
                    format.ReadWhileOpen();
                }
            }
        }
        finally
        {
            held.Stop();
            CloseClipboard();
        }

        snapshot.HeldMs = held.Elapsed.TotalMilliseconds;

        // Everything below works on copies, so it runs after the clipboard is free again.
        foreach (var format in snapshot.Formats)
        {
            Analyzers.Analyze(format);
        }

        return snapshot;
    }
}

/// <summary>One clipboard format of a snapshot: how it was stored, how long it took to get, and its summary.</summary>
sealed class FormatReport
{
    /// <summary>The largest payload the probe copies (a bigger one is only measured).</summary>
    private const long MaxBytes = 256L * 1024 * 1024;

    /// <summary>Creates the report of a listed format (nothing read yet).</summary>
    /// <param name="id">The clipboard format id (registered ids differ per session).</param>
    /// <param name="name">Its name.</param>
    public FormatReport(uint id, string name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>The format id.</summary>
    public uint Id { get; }

    /// <summary>The format name (standard formats by their <c>CF_</c> name).</summary>
    public string Name { get; }

    /// <summary>Whether the probe asked for the data.</summary>
    public bool WasRead { get; private set; }

    /// <summary>How the data came: memory, a metafile or bitmap handle, not memory, or declined.</summary>
    public string Storage { get; private set; } = "not read";

    /// <summary>The size in bytes, or -1 when unknown.</summary>
    public long Bytes { get; private set; } = -1;

    /// <summary>How long <c>GetClipboardData</c> took: a long wait means the producer rendered it on request.</summary>
    public double FetchMs { get; private set; }

    /// <summary>The copied payload (memory formats) or the metafile's bits, or <see langword="null"/>.</summary>
    public byte[]? Data { get; private set; }

    /// <summary>Content-free facts for the report.</summary>
    public List<string> Facts { get; } = [];

    /// <summary>A PNG drawing of a metafile (saved with <c>--save</c>), or <see langword="null"/>.</summary>
    public byte[]? Drawing { get; set; }

    /// <summary>Reads the data while the clipboard is open: copies memory, the metafile's bits, or a bitmap's size.</summary>
    public void ReadWhileOpen()
    {
        WasRead = true;
        var watch = Stopwatch.StartNew();
        nint handle = GetClipboardData(Id);
        FetchMs = watch.Elapsed.TotalMilliseconds;
        if (handle == 0)
        {
            Storage = "declined (no data)";
            return;
        }

        switch (Id)
        {
            case CF_ENHMETAFILE:
            {
                // Copy the bits now: the handle belongs to the clipboard and is drawn after it is closed.
                uint size = GetEnhMetaFileBits(handle, 0, null);
                Storage = "metafile handle";
                Bytes = size;
                if (size > 0 && size <= MaxBytes)
                {
                    Data = new byte[size];
                    GetEnhMetaFileBits(handle, size, Data);
                }

                return;
            }

            case CF_BITMAP:
                Storage = "GDI bitmap handle";
                if (GetObjectW(handle, Marshal.SizeOf<BITMAP>(), out var bitmap) != 0)
                {
                    Facts.Add($"GDI bitmap {bitmap.bmWidth} × {bitmap.bmHeight}, {bitmap.bmBitsPixel} bpp (Windows makes CF_DIB from it)");
                }

                return;
            case CF_PALETTE:
                Storage = "palette handle";
                return;
            case CF_METAFILEPICT:
                Storage = "memory (METAFILEPICT + a WMF handle)";
                ReadMetafilePict(handle);
                return;
        }

        long bytes = (long)GlobalSize(handle);
        if (bytes <= 0)
        {
            Storage = "not memory (an OLE storage or stream)";
            return;
        }

        Storage = "memory";
        Bytes = bytes;
        if (bytes > MaxBytes)
        {
            Facts.Add("larger than the probe copies");
            return;
        }

        nint pointer = GlobalLock(handle);
        if (pointer == 0)
        {
            return;
        }

        try
        {
            Data = new byte[bytes];
            Marshal.Copy(pointer, Data, 0, (int)bytes);
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    /// <summary>Notes a METAFILEPICT's mapping mode, extent and the size of its WMF.</summary>
    /// <param name="handle">The clipboard's HGLOBAL with the METAFILEPICT.</param>
    private void ReadMetafilePict(nint handle)
    {
        nint pointer = GlobalLock(handle);
        if (pointer == 0)
        {
            return;
        }

        try
        {
            var pict = Marshal.PtrToStructure<METAFILEPICT>(pointer);
            Bytes = GetMetaFileBitsEx(pict.hMF, 0, null);
            Facts.Add($"WMF {Bytes:N0} B, mapping mode {pict.mm}, extent {pict.xExt} × {pict.yExt}");
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }
}

/// <summary>Clipboard format names and which of them a preview could use.</summary>
static class FormatNames
{
    /// <summary>Registered formats the probe reads by default: rich text, pictures and the small descriptors.</summary>
    private static readonly HashSet<string> Useful = new(StringComparer.Ordinal)
    {
        "HTML Format", "HTML (HyperText Markup Language)", "text/html", "Rich Text Format", "Richtext Format", "RTF As Text",
        "PNG", "JFIF", "GIF", "UniformResourceLocatorW", "UniformResourceLocator", "FileGroupDescriptorW",
        "Object Descriptor", "Link Source Descriptor", "Star Object Descriptor (XML)", "vscode-editor-data",
        "Chromium Web Custom MIME Data Format", "Chromium internal source URL", "text/x-moz-url-priv",
        "text/_moz_htmlcontext", "text/_moz_htmlinfo", "XML Spreadsheet", "Csv", "Preferred DropEffect",
        "ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "Clipboard Viewer Ignore",
        "CanUploadToCloudClipboard", "Markdown", "text/markdown", "text/x-markdown",
    };

    /// <summary>The name of a format id: the <c>CF_</c> name of a standard format, else its registered name.</summary>
    /// <param name="id">The format id.</param>
    /// <returns>The name, or <c>0xNNNN</c> when it has none.</returns>
    public static string Of(uint id) => id switch
    {
        1 => "CF_TEXT",
        2 => "CF_BITMAP",
        3 => "CF_METAFILEPICT",
        4 => "CF_SYLK",
        5 => "CF_DIF",
        6 => "CF_TIFF",
        7 => "CF_OEMTEXT",
        8 => "CF_DIB",
        9 => "CF_PALETTE",
        10 => "CF_PENDATA",
        11 => "CF_RIFF",
        12 => "CF_WAVE",
        13 => "CF_UNICODETEXT",
        14 => "CF_ENHMETAFILE",
        15 => "CF_HDROP",
        16 => "CF_LOCALE",
        17 => "CF_DIBV5",
        0x80 => "CF_OWNERDISPLAY",
        0x81 => "CF_DSPTEXT",
        0x82 => "CF_DSPBITMAP",
        0x83 => "CF_DSPMETAFILEPICT",
        0x8E => "CF_DSPENHMETAFILE",
        _ => Registered(id),
    };

    /// <summary>Whether the probe reads a format by default.</summary>
    /// <param name="id">The format id.</param>
    /// <param name="name">The format name.</param>
    /// <returns><see langword="true"/> for text, file lists, bitmaps, metafiles, rich text and the descriptors.</returns>
    public static bool IsUseful(uint id, string name) =>
        id is 13 or 15 or 8 or 17 or 14 or 2 or 16
        || Useful.Contains(name)
        || name.StartsWith("image/", StringComparison.Ordinal);

    /// <summary>A registered format's name.</summary>
    /// <param name="id">The format id.</param>
    /// <returns>The name, or <c>0xNNNN</c>.</returns>
    private static string Registered(uint id)
    {
        var buffer = new char[256];
        int length = GetClipboardFormatNameW(id, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : $"0x{id:X4}";
    }
}

/// <summary>Turns a format's bytes into content-free facts; one method per kind of format.</summary>
static class Analyzers
{
    /// <summary>Adds the facts of a format that was read (no-op for one that was not).</summary>
    /// <param name="format">The format.</param>
    public static void Analyze(FormatReport format)
    {
        if (format.Data is not { } data)
        {
            return;
        }

        // A malformed payload from some app must not end the watch: the facts say what could not be summarized.
        try
        {
            switch (format.Name)
            {
                case "CF_UNICODETEXT":
                    Text.Facts(data, format.Facts);
                    break;
                case "HTML Format":
                    Html.CfHtml(data, format.Facts);
                    break;
                case "HTML (HyperText Markup Language)" or "text/html":
                    Html.Plain(data, format.Facts);
                    break;
                case "Rich Text Format" or "Richtext Format" or "RTF As Text":
                    Rtf.Census(data, format.Facts);
                    break;
                case "CF_ENHMETAFILE":
                    format.Drawing = Emf.Describe(data, format.Facts, 0);
                    break;
                case "Markdown" or "text/markdown" or "text/x-markdown":
                    Text.Markdown(data, format.Facts);
                    break;
                case "CF_DIB" or "CF_DIBV5":
                    Pictures.Dib(data, format.Facts);
                    break;
                case "PNG" or "image/png":
                    Pictures.Png(data, format.Facts);
                    break;
                case "JFIF" or "image/jpeg":
                    Pictures.Jpeg(data, format.Facts);
                    break;
                case "GIF" or "image/gif":
                    Pictures.Gif(data, format.Facts);
                    break;
                case "image/svg+xml":
                    Pictures.Svg(data, format.Facts);
                    break;
                case "CF_HDROP":
                    Files.DropList(data, format.Facts);
                    break;
                case "FileGroupDescriptorW":
                    Files.Descriptors(data, format.Facts);
                    break;
                case "UniformResourceLocatorW" or "text/x-moz-url-priv":
                    format.Facts.Add("URL: " + Privacy.DescribeUrl(Text.Utf16Z(data)));
                    break;
                case "UniformResourceLocator":
                    format.Facts.Add("URL: " + Privacy.DescribeUrl(Encoding.Latin1.GetString(data).TrimEnd('\0')));
                    break;
                case "Chromium internal source URL":
                    format.Facts.Add("source page: " + Privacy.DescribeUrl(Text.Guess(data)));
                    break;
                case "Object Descriptor" or "Link Source Descriptor" or "Star Object Descriptor (XML)":
                    Descriptors.ObjectDescriptor(data, format.Facts);
                    break;
                case "vscode-editor-data":
                    Descriptors.VsCode(data, format.Facts);
                    break;
                case "Chromium Web Custom MIME Data Format":
                    Descriptors.ChromiumCustom(data, format.Facts);
                    break;
                case "XML Spreadsheet":
                    string xml = Encoding.UTF8.GetString(data);
                    format.Facts.Add($"rows {Count(xml, "<Row")}, cells {Count(xml, "<Cell")}");
                    break;
                case "Csv":
                    format.Facts.Add($"{Encoding.Latin1.GetString(data).TrimEnd('\0').Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} line(s)");
                    break;
                case "CF_LOCALE" when data.Length >= 4:
                    int lcid = BitConverter.ToInt32(data, 0);
                    format.Facts.Add($"locale {lcid} ({SafeCulture(lcid)})");
                    break;
                case "ExcludeClipboardContentFromMonitorProcessing" or "Clipboard Viewer Ignore":
                    format.Facts.Add("privacy marker: BetterClipboard skips this copy");
                    break;
                case "CanIncludeInClipboardHistory" when data.Length >= 4:
                    format.Facts.Add($"privacy marker = {BitConverter.ToUInt32(data, 0)} (0: BetterClipboard skips this copy)");
                    break;
            }
        }
        catch (Exception ex)
        {
            format.Facts.Add($"(could not summarize: {ex.GetType().Name})");
        }
    }

    /// <summary>Counts the non-overlapping occurrences of a literal, ignoring case.</summary>
    /// <param name="text">The text.</param>
    /// <param name="literal">What to count.</param>
    /// <returns>The count.</returns>
    public static int Count(string text, string literal)
    {
        int count = 0;
        for (int at = text.IndexOf(literal, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(literal, at + literal.Length, StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }

        return count;
    }

    /// <summary>A culture name for a locale id, or "unknown".</summary>
    /// <param name="lcid">The locale id.</param>
    /// <returns>The culture name.</returns>
    private static string SafeCulture(int lcid)
    {
        try
        {
            return CultureInfo.GetCultureInfo(lcid).Name;
        }
        catch (CultureNotFoundException)
        {
            return "unknown";
        }
    }
}

/// <summary>Plain-text facts: sizes and shapes, never the text.</summary>
static class Text
{
    /// <summary>Decodes UTF-16LE up to the first NUL (clipboard text is NUL-terminated, often with garbage after).</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The text.</returns>
    public static string Utf16Z(byte[] data)
    {
        string text = Encoding.Unicode.GetString(data, 0, data.Length & ~1);
        int end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    /// <summary>Decodes a string of unknown width: UTF-16 when every second byte of the start is 0, else UTF-8.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The text up to the first NUL.</returns>
    public static string Guess(byte[] data)
    {
        bool wide = data.Length >= 4 && data[1] == 0 && data[3] == 0;
        if (wide)
        {
            return Utf16Z(data);
        }

        string text = Encoding.UTF8.GetString(data);
        int end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    /// <summary>Adds the text's length, lines and, for tab-separated rows, the table shape.</summary>
    /// <param name="data">CF_UNICODETEXT bytes.</param>
    /// <param name="facts">Where the facts go.</param>
    public static void Facts(byte[] data, List<string> facts)
    {
        string text = Utf16Z(data);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        facts.Add($"{text.Length:N0} characters, {lines.Length:N0} line(s), longest {lines.Max(l => l.Length):N0}");

        // A range copied from a spreadsheet or a table copied from a page arrives as tab-separated rows.
        var rows = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        if (rows.Length >= 1 && rows.All(r => r.Contains('\t')))
        {
            facts.Add($"tab-separated: {rows.Length} row(s) × up to {rows.Max(r => r.Count(c => c == '\t')) + 1} column(s)");
        }

        if (text.Contains(Report.TestMarker, StringComparison.Ordinal))
        {
            facts.Add("contains the BC-TEST marker");
        }
    }

    /// <summary>Adds a Markdown payload's structure (LibreOffice 26.2 offers one): headings, lists, tables, links, pictures.</summary>
    /// <param name="data">The bytes (UTF-8, or UTF-16 when every second byte is 0).</param>
    /// <param name="facts">Where the facts go.</param>
    public static void Markdown(byte[] data, List<string> facts)
    {
        string text = Guess(data);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int headings = lines.Count(l => Regex.IsMatch(l, @"^#{1,6}\s"));
        int items = lines.Count(l => Regex.IsMatch(l, @"^\s*([-*+]|\d+[.)])\s"));
        int tableRows = lines.Count(l => l.TrimStart().StartsWith('|'));
        int pictures = Regex.Matches(text, @"!\[[^\]]*\]\(([^)]*)\)").Count;
        int links = Regex.Matches(text, @"(?<!!)\[[^\]]*\]\([^)]*\)").Count;
        int fences = lines.Count(l => l.TrimStart().StartsWith("```", StringComparison.Ordinal)) / 2;
        int emphasis = Regex.Matches(text, @"\*\*[^*]+\*\*|(?<![*\w])\*[^*\s][^*]*\*(?!\*)").Count;
        facts.Add($"Markdown {text.Length:N0} chars, {lines.Length} line(s): headings {headings}, list items {items}, table rows {tableRows}, "
            + $"links {links}, pictures {pictures} ({string.Join(' ', Regex.Matches(text, @"!\[[^\]]*\]\(([a-zA-Z][a-zA-Z0-9+.-]*):").Select(m => m.Groups[1].Value.ToLowerInvariant()).GroupBy(s => s).Select(g => $"{g.Key}×{g.Count()}"))}), "
            + $"code fences {fences}, bold/italic {emphasis}");
        if (text.Contains(Report.TestMarker, StringComparison.Ordinal))
        {
            facts.Add("contains the BC-TEST marker");
        }
    }
}

/// <summary>HTML facts: structure only (tags, CSS property names, URL schemes), never text or values.</summary>
static class Html
{
    /// <summary>Summarizes a CF_HTML payload ("HTML Format"): its header, then the census of its document and fragment.</summary>
    /// <param name="data">The payload (UTF-8 with an ASCII header).</param>
    /// <param name="facts">Where the facts go.</param>
    public static void CfHtml(byte[] data, List<string> facts)
    {
        int firstTag = Array.IndexOf(data, (byte)'<');
        int headerLength = firstTag >= 0 ? Math.Min(firstTag, 4096) : Math.Min(data.Length, 4096);
        string header = Encoding.ASCII.GetString(data, 0, headerLength);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(header, @"^([A-Za-z]+):(.*?)\r?$", RegexOptions.Multiline))
        {
            fields[match.Groups[1].Value] = match.Groups[2].Value.Trim();
        }

        long Offset(string key) => fields.TryGetValue(key, out var value) && long.TryParse(value, out long n) ? n : -1;
        long startHtml = Offset("StartHTML"), endHtml = Offset("EndHTML"), startFragment = Offset("StartFragment"), endFragment = Offset("EndFragment");
        bool documentValid = startHtml >= 0 && endHtml > startHtml && endHtml <= data.Length;
        bool fragmentValid = startFragment >= 0 && endFragment >= startFragment && endFragment <= data.Length;
        string document = documentValid ? Encoding.UTF8.GetString(data, (int)startHtml, (int)(endHtml - startHtml))
            : Encoding.UTF8.GetString(data, headerLength, data.Length - headerLength);
        string fragment = fragmentValid ? Encoding.UTF8.GetString(data, (int)startFragment, (int)(endFragment - startFragment)) : document;
        string source = fields.TryGetValue("SourceURL", out var url) ? Privacy.DescribeUrl(url) : "none";
        facts.Add($"CF_HTML {fields.GetValueOrDefault("Version", "?")}: document {document.Length:N0} chars{(documentValid ? "" : " (no valid StartHTML/EndHTML)")}, "
            + $"fragment {fragment.Length:N0} chars{(fragmentValid ? "" : " (no valid fragment offsets)")}, SourceURL {source}");
        Census(document, fragment, facts);
    }

    /// <summary>Summarizes plain HTML without the CF_HTML header (LibreOffice's second HTML format, <c>text/html</c>).</summary>
    /// <param name="data">UTF-8 HTML.</param>
    /// <param name="facts">Where the facts go.</param>
    public static void Plain(byte[] data, List<string> facts)
    {
        string document = Encoding.UTF8.GetString(data).TrimEnd('\0');
        facts.Add($"plain HTML document, {document.Length:N0} chars");
        Census(document, document, facts);
    }

    /// <summary>Counts what a renderer would have to handle in the copied HTML.</summary>
    /// <param name="document">The whole document (its head holds Word's and Outlook's style classes).</param>
    /// <param name="fragment">The part that was selected.</param>
    /// <param name="facts">Where the facts go.</param>
    private static void Census(string document, string fragment, List<string> facts)
    {
        const RegexOptions Ignore = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var generator = Regex.Match(document, @"<meta\s+name\s*=\s*[""']?Generator[""']?\s+content\s*=\s*[""']?([^""'>]+)", Ignore);
        if (generator.Success)
        {
            facts.Add("generator: " + generator.Groups[1].Value.Trim());
        }

        var tags = Regex.Matches(fragment, @"<([A-Za-z][A-Za-z0-9]*(?::[A-Za-z0-9]+)?)")
            .GroupBy(m => m.Groups[1].Value.ToLowerInvariant())
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        facts.Add($"fragment tags ({tags.Sum(g => g.Count())}): " + string.Join(' ', tags.Take(28).Select(g => $"{g.Key}×{g.Count()}"))
            + (tags.Count > 28 ? $" (+{tags.Count - 28} more kinds)" : string.Empty));

        // Word and Outlook style their paragraphs through classes defined in the head's <style>: a renderer given only the
        // fragment loses those styles.
        var styleBlocks = Regex.Matches(document, @"<style[^>]*>(.*?)</style>", Ignore | RegexOptions.Singleline);
        int externalSheets = Regex.Matches(document, @"<link[^>]+rel\s*=\s*[""']?stylesheet", Ignore).Count;
        facts.Add($"head: {styleBlocks.Count} <style> block(s), {styleBlocks.Sum(m => m.Groups[1].Length):N0} chars of CSS; {externalSheets} external stylesheet(s); <base>: {(Regex.IsMatch(document, "<base\\s", Ignore) ? "yes" : "no")}");

        var styleValues = Regex.Matches(fragment, @"\sstyle\s*=\s*(?:""([^""]*)""|'([^']*)')", Ignore)
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToList();
        var properties = styleValues.SelectMany(v => Regex.Matches(v, @"(?:^|;)\s*([-A-Za-z]+)\s*:").Select(m => m.Groups[1].Value.ToLowerInvariant()))
            .GroupBy(p => p).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        int mso = properties.Where(g => g.Key.StartsWith("mso-", StringComparison.Ordinal)).Sum(g => g.Count());
        facts.Add($"inline styles: {styleValues.Count} attribute(s), {properties.Count} propert(ies) (mso-*: {mso}): "
            + string.Join(' ', properties.Take(18).Select(g => $"{g.Key}×{g.Count()}")));
        int classes = Regex.Matches(fragment, @"\sclass\s*=", Ignore).Count;
        if (classes > 0)
        {
            facts.Add($"class attributes: {classes}");
        }

        // Where images point decides what an offline preview can show: data: is inside the copy, file: may be a temp
        // file that is gone later (Word's msohtmlclip), http(s) needs the network, cid: and blob: are unreachable.
        var images = Regex.Matches(fragment, @"<img\b[^>]*?\ssrc\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", Ignore)
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).ToList();
        int imageTags = Regex.Matches(fragment, @"<img\b", Ignore).Count;
        int vmlImages = Regex.Matches(fragment, @"<v:imagedata\b", Ignore).Count;
        var cssUrls = Regex.Matches(document, @"url\(\s*[""']?([^)""']+)", Ignore).Select(m => m.Groups[1].Value).ToList();
        facts.Add($"images: {imageTags} <img> ({DescribeSchemes(images)}); VML images {vmlImages}; CSS url(): {cssUrls.Count} ({DescribeSchemes(cssUrls)})");

        var links = Regex.Matches(fragment, @"<a\b[^>]*?\shref\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", Ignore)
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).ToList();
        facts.Add($"tables {Count(tags, "table")} (rows {Count(tags, "tr")}, cells {Count(tags, "td") + Count(tags, "th")}); "
            + $"lists ul {Count(tags, "ul")} ol {Count(tags, "ol")} li {Count(tags, "li")}; headings {Enumerable.Range(1, 6).Sum(n => Count(tags, "h" + n))}; "
            + $"links {links.Count} ({DescribeSchemes(links)}); code/pre {Count(tags, "code") + Count(tags, "pre")}");

        int conditional = Analyzers.Count(document, "<!--[if");
        int officeParagraphs = Count(tags, "o:p");
        int vml = tags.Where(g => g.Key.StartsWith("v:", StringComparison.Ordinal)).Sum(g => g.Count());
        if (conditional + officeParagraphs + vml > 0)
        {
            facts.Add($"Office markup: {conditional} conditional comment(s) <!--[if …]>, <o:p> {officeParagraphs}, VML elements {vml}");
        }

        int handlers = Regex.Matches(fragment, @"\son[a-z]+\s*=", Ignore).Count;
        facts.Add($"active content: script {Count(tags, "script")}, iframe {Count(tags, "iframe")}, object/embed {Count(tags, "object") + Count(tags, "embed")}, "
            + $"on* handlers {handlers}, svg {Count(tags, "svg")}, video/audio {Count(tags, "video") + Count(tags, "audio")}, form/input {Count(tags, "form") + Count(tags, "input")}");

        // Teams (and Skype before it) mark emoji, mentions and similar items with schema.skype.com item types.
        var itemTypes = Regex.Matches(fragment, @"itemtype\s*=\s*[""']?https?://schema\.skype\.com/([A-Za-z]+)", Ignore)
            .GroupBy(m => m.Groups[1].Value).Select(g => $"{g.Key}×{g.Count()}").ToList();
        if (itemTypes.Count > 0)
        {
            facts.Add("schema.skype.com items: " + string.Join(' ', itemTypes));
        }

        int dataAttributes = Regex.Matches(fragment, @"\sdata-[a-z0-9-]+\s*=", Ignore).Count;
        string plain = Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(fragment, "<[^>]*>", " ")), @"\s+", " ").Trim();
        facts.Add($"fragment text ≈ {plain.Length:N0} chars; data-* attributes {dataAttributes}");
        if (fragment.Contains(Report.TestMarker, StringComparison.Ordinal))
        {
            facts.Add("contains the BC-TEST marker");
        }
    }

    /// <summary>Counts one tag in the census.</summary>
    /// <param name="tags">The census.</param>
    /// <param name="tag">The tag name (lowercase).</param>
    /// <returns>Its count.</returns>
    private static int Count(List<IGrouping<string, Match>> tags, string tag) => tags.FirstOrDefault(g => g.Key == tag)?.Count() ?? 0;

    /// <summary>Groups URLs by scheme ("data:image/png×2 https×1 relative×1"); never prints a host or a path.</summary>
    /// <param name="urls">The URLs.</param>
    /// <returns>The summary, or "none".</returns>
    private static string DescribeSchemes(List<string> urls)
    {
        if (urls.Count == 0)
        {
            return "none";
        }

        return string.Join(' ', urls.Select(Scheme).GroupBy(s => s).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}×{g.Count()}"));
    }

    /// <summary>The scheme of a URL as a preview sees it.</summary>
    /// <param name="url">The URL as written in the HTML.</param>
    /// <returns>"data:image/png", "https", "file", "relative", …</returns>
    private static string Scheme(string url)
    {
        url = WebUtility.HtmlDecode(url).Trim();
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int end = url.IndexOfAny([';', ',']);
            return end > 5 ? "data:" + url[5..end].ToLowerInvariant() : "data:";
        }

        var match = Regex.Match(url, "^([A-Za-z][A-Za-z0-9+.-]*):");
        return match.Success && match.Groups[1].Length > 1 ? match.Groups[1].Value.ToLowerInvariant() : url.Length > 2 && url[1] == ':' ? "drive path" : "relative";
    }
}

/// <summary>RTF facts: the generator, structure counts and where the bytes go; never text.</summary>
static class Rtf
{
    /// <summary>Control words counted for the report.</summary>
    private static readonly string[] Counted =
    [
        "par", "page", "sect", "trowd", "cell", "row", "nestcell", "field", "listtext", "pntext", "ls", "pict", "shppict",
        "nonshppict", "object", "objdata", "shp", "footnote", "u", "b", "i", "ul", "cf", "highlight", "cb", "fs", "f",
    ];

    /// <summary>Summarizes an RTF payload.</summary>
    /// <param name="data">The RTF bytes (7-bit ASCII with escapes).</param>
    /// <param name="facts">Where the facts go.</param>
    public static void Census(byte[] data, List<string> facts)
    {
        string rtf = Encoding.Latin1.GetString(data).TrimEnd('\0');
        var generator = Regex.Match(rtf, @"\{\\\*\\generator\s+([^;}]*)");
        // Calc writes "{\rtf\ansi" without the version number; RichEdit accepts it, so the probe does too.
        var header = Regex.Match(rtf, @"^\{\\rtf(\d*)(\\[a-z]+\d*)*");
        facts.Add($"{(generator.Success ? "generator: " + generator.Groups[1].Value.Trim() : "no generator group")}; header {(header.Success ? header.Value[1..Math.Min(header.Value.Length, 60)] : "missing")}");

        var words = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(rtf, @"\\([a-z]{1,32})(-?\d{1,10})?"))
        {
            string word = match.Groups[1].Value;
            words[word] = words.GetValueOrDefault(word) + 1;
        }

        int Word(string name) => words.GetValueOrDefault(name);
        int hyperlinks = Regex.Matches(rtf, @"\\fldinst[^{}]*?\{?[^{}]*?HYPERLINK", RegexOptions.IgnoreCase).Count;
        facts.Add($"paragraphs {Word("par")}, pages {Word("page")}; tables: row definitions {Word("trowd")}, cells {Word("cell")}, nested cells {Word("nestcell")}; "
            + $"fields {Word("field")} (hyperlinks {hyperlinks}); list items {Word("listtext") + Word("pntext")}; Unicode escapes {Word("u")}");

        var pictures = Pictures(rtf);
        if (pictures.Count > 0)
        {
            long pictureBytes = pictures.Sum(p => p.Bytes);
            int fallbacks = pictures.Count(p => p.InFallback);
            facts.Add($"pictures {pictures.Count} ({string.Join(' ', pictures.GroupBy(p => p.Kind).Select(g => $"{g.Key}×{g.Count()}"))}), "
                + $"{pictureBytes:N0} B of picture data ({100.0 * pictureBytes * 2 / Math.Max(1, rtf.Length):0}% of the RTF as hex); "
                + $"{fallbacks} are \\nonshppict fallback copies; sizes {string.Join(", ", pictures.Where(p => !p.InFallback).Take(6).Select(p => $"{p.WidthPx}×{p.HeightPx}"))}");
        }
        else
        {
            facts.Add("pictures: none");
        }

        var blocks = new List<string>();
        foreach (var name in new[] { "themedata", "colorschememapping", "latentstyles", "datastore", "xmlnstbl", "rsidtbl", "listtable", "listoverridetable", "stylesheet", "fonttbl", "colortbl", "info", "pgdsctbl", "mmathPr" })
        {
            var span = GroupOf(rtf, name);
            if (span is { } s)
            {
                blocks.Add($"{name} {s.Length:N0}");
            }
        }

        facts.Add("blocks (chars): " + (blocks.Count > 0 ? string.Join(", ", blocks) : "none"));
        var fonts = Fonts(rtf);
        facts.Add($"fonts {fonts.Count}: {string.Join(", ", fonts.Take(8))}{(fonts.Count > 8 ? ", …" : string.Empty)}; "
            + $"colors {(GroupOf(rtf, "colortbl") is { } colors ? colors.Count(c => c == ';') : 0)}; "
            + $"objects {Word("object")} (objdata {(GroupOf(rtf, "objdata") is { } objdata ? objdata.Length : 0):N0} chars); shapes {Word("shp")}");
        if (rtf.Contains(Report.TestMarker, StringComparison.Ordinal))
        {
            facts.Add("contains the BC-TEST marker");
        }
    }

    /// <summary>One <c>\pict</c> group: its kind, the bytes its hex data holds, its goal size and whether it is a fallback copy.</summary>
    /// <param name="Kind">pngblip, jpegblip, emfblip, wmetafile, dibitmap, …</param>
    /// <param name="Bytes">Bytes of picture data (hex digits / 2).</param>
    /// <param name="WidthPx">Goal width at 96 DPI.</param>
    /// <param name="HeightPx">Goal height at 96 DPI.</param>
    /// <param name="InFallback">Inside <c>\nonshppict</c>: the old-reader copy Word writes next to the real one.</param>
    private sealed record Picture(string Kind, long Bytes, int WidthPx, int HeightPx, bool InFallback);

    /// <summary>Finds every <c>\pict</c> group and measures it.</summary>
    /// <param name="rtf">The RTF.</param>
    /// <returns>The pictures in document order.</returns>
    private static List<Picture> Pictures(string rtf)
    {
        var fallbackSpans = new List<(int Start, int End)>();
        for (int at = rtf.IndexOf("{\\nonshppict", StringComparison.Ordinal); at >= 0; at = rtf.IndexOf("{\\nonshppict", at + 1, StringComparison.Ordinal))
        {
            fallbackSpans.Add((at, GroupEnd(rtf, at)));
        }

        var pictures = new List<Picture>();
        for (int at = rtf.IndexOf("{\\pict", StringComparison.Ordinal); at >= 0; at = rtf.IndexOf("{\\pict", at + 1, StringComparison.Ordinal))
        {
            int end = GroupEnd(rtf, at);
            string group = rtf[at..end];
            var kind = Regex.Match(group, @"\\(pngblip|jpegblip|emfblip|wmetafile|dibitmap|wbitmap|macpict|pmmetafile)");
            int Goal(string word)
            {
                var m = Regex.Match(group, @"\\" + word + @"(\d+)");
                return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 15 : 0; // twips → px at 96 DPI
            }

            // The data is the hex text left after removing nested groups ({\*\blipuid …}, {\*\picprop …}) and control words.
            string data = Regex.Replace(StripNestedGroups(group[1..^1]), @"\\[a-z]+-?\d* ?", string.Empty);
            long hex = data.Count(Uri.IsHexDigit);
            pictures.Add(new Picture(kind.Success ? kind.Groups[1].Value : "unknown", hex / 2, Goal("picwgoal"), Goal("pichgoal"), fallbackSpans.Any(s => at > s.Start && at < s.End)));
        }

        return pictures;
    }

    /// <summary>The font names of the font table.</summary>
    /// <param name="rtf">The RTF.</param>
    /// <returns>Names in table order.</returns>
    private static List<string> Fonts(string rtf)
    {
        var names = new List<string>();
        if (GroupOf(rtf, "fonttbl") is not { } table)
        {
            return names;
        }

        foreach (Match entry in Regex.Matches(table, @"\{\\f\d+"))
        {
            int end = GroupEnd(table, entry.Index);
            string body = Regex.Replace(StripNestedGroups(table[(entry.Index + 1)..(end - 1)]), @"\\[a-z]+-?\d* ?", string.Empty);
            string name = body.Split(';')[0].Trim();
            if (name.Length > 0)
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>The text of the first group that starts with a control word (<c>{\name</c> or <c>{\*\name</c>).</summary>
    /// <param name="rtf">The RTF.</param>
    /// <param name="name">The control word.</param>
    /// <returns>The group's text, or <see langword="null"/> when there is none.</returns>
    private static string? GroupOf(string rtf, string name)
    {
        var match = Regex.Match(rtf, @"\{(\\\*)?\\" + name + @"(?![a-z])");
        if (!match.Success)
        {
            return null;
        }

        int end = GroupEnd(rtf, match.Index);
        return rtf[match.Index..end];
    }

    /// <summary>The index just past the brace that closes the group opening at <paramref name="start"/>.</summary>
    /// <param name="rtf">The RTF.</param>
    /// <param name="start">The index of the group's <c>{</c>.</param>
    /// <returns>The end index (exclusive); the text's end for an unclosed group.</returns>
    private static int GroupEnd(string rtf, int start)
    {
        int depth = 0;
        for (int i = start; i < rtf.Length; i++)
        {
            char c = rtf[i];
            if (c == '\\')
            {
                // \{ \} \\ are literal characters; \binN is followed by N raw bytes that may hold braces.
                if (i + 3 < rtf.Length && string.CompareOrdinal(rtf, i + 1, "bin", 0, 3) == 0)
                {
                    var bin = Regex.Match(rtf[(i + 4)..Math.Min(rtf.Length, i + 16)], @"^(\d+) ?");
                    if (bin.Success)
                    {
                        i += 3 + bin.Length + int.Parse(bin.Groups[1].Value, CultureInfo.InvariantCulture);
                        continue;
                    }
                }

                i++;
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && --depth == 0)
            {
                return i + 1;
            }
        }

        return rtf.Length;
    }

    /// <summary>Removes every brace group inside a group's body, keeping the body's own text.</summary>
    /// <param name="body">The text between a group's braces.</param>
    /// <returns>The body without nested groups.</returns>
    private static string StripNestedGroups(string body)
    {
        var builder = new StringBuilder(body.Length);
        for (int i = 0; i < body.Length; i++)
        {
            if (body[i] == '\\' && i + 1 < body.Length)
            {
                builder.Append(body, i, 2);
                i++;
            }
            else if (body[i] == '{')
            {
                i = GroupEnd(body, i) - 1;
            }
            else
            {
                builder.Append(body[i]);
            }
        }

        return builder.ToString();
    }
}

/// <summary>Enhanced metafiles: header facts, record kinds, EMF+ and a timed GDI drawing.</summary>
static class Emf
{
    /// <summary>Names of the record types worth naming in the histogram.</summary>
    private static readonly Dictionary<uint, string> RecordNames = new()
    {
        [1] = "HEADER", [14] = "EOF", [18] = "SETBKMODE", [19] = "SETPOLYFILLMODE", [20] = "SETROP2", [21] = "SETSTRETCHBLTMODE",
        [22] = "SETTEXTALIGN", [37] = "SELECTOBJECT", [38] = "CREATEPEN",
        [39] = "CREATEBRUSHINDIRECT", [40] = "DELETEOBJECT", [42] = "ELLIPSE", [43] = "RECTANGLE", [59] = "BEGINPATH",
        [60] = "ENDPATH", [62] = "FILLPATH", [64] = "STROKEPATH", [67] = "SELECTCLIPPATH", [70] = "GDICOMMENT",
        [76] = "BITBLT", [77] = "STRETCHBLT", [80] = "SETDIBITSTODEVICE", [81] = "STRETCHDIBITS", [82] = "EXTCREATEFONTINDIRECTW",
        [83] = "EXTTEXTOUTA", [84] = "EXTTEXTOUTW", [86] = "POLYGON16", [87] = "POLYLINE16", [88] = "POLYBEZIERTO16",
        [91] = "POLYPOLYGON16", [95] = "EXTCREATEPEN", [108] = "SMALLTEXTOUT", [114] = "ALPHABLEND", [116] = "TRANSPARENTBLT",
        [33] = "SAVEDC", [34] = "RESTOREDC", [35] = "SETWORLDTRANSFORM", [36] = "MODIFYWORLDTRANSFORM", [9] = "SETWINDOWEXTEX",
        [10] = "SETWINDOWORGEX", [11] = "SETVIEWPORTEXTEX", [12] = "SETVIEWPORTORGEX", [17] = "SETMAPMODE", [24] = "SETTEXTCOLOR",
        [25] = "SETBKCOLOR", [30] = "INTERSECTCLIPRECT", [75] = "EXTSELECTCLIPRGN", [27] = "MOVETOEX", [54] = "LINETO",
    };

    /// <summary>Adds the facts of a metafile and draws it.</summary>
    /// <param name="bits">The EMF bytes.</param>
    /// <param name="facts">Where the facts go.</param>
    /// <param name="widthPx">The drawing width; 0 = the frame's width at 96 DPI (at most 2400 px on the longer side).</param>
    /// <returns>A PNG of the drawing, or <see langword="null"/> when it is not a metafile.</returns>
    public static byte[]? Describe(byte[] bits, List<string> facts, int widthPx)
    {
        if (bits.Length < 108 || BitConverter.ToUInt32(bits, 0) != 1 || BitConverter.ToUInt32(bits, 40) != 0x464D4520)
        {
            facts.Add("not an enhanced metafile");
            return null;
        }

        int frameLeft = BitConverter.ToInt32(bits, 24), frameTop = BitConverter.ToInt32(bits, 28);
        int frameRight = BitConverter.ToInt32(bits, 32), frameBottom = BitConverter.ToInt32(bits, 36);
        double widthMm = (frameRight - frameLeft) / 100.0, heightMm = (frameBottom - frameTop) / 100.0;
        uint records = BitConverter.ToUInt32(bits, 52);
        uint descriptionChars = BitConverter.ToUInt32(bits, 60), descriptionOffset = BitConverter.ToUInt32(bits, 64);
        string application = string.Empty;
        if (descriptionChars > 0 && descriptionOffset + (descriptionChars * 2) <= bits.Length)
        {
            // "Application\0Picture name\0\0": only the application is printed (the picture name can be a document title).
            application = Encoding.Unicode.GetString(bits, (int)descriptionOffset, (int)descriptionChars * 2).Split('\0')[0];
        }

        var histogram = new Dictionary<uint, int>();
        int emfPlus = 0;
        for (int offset = 0; offset + 8 <= bits.Length;)
        {
            uint type = BitConverter.ToUInt32(bits, offset);
            uint size = BitConverter.ToUInt32(bits, offset + 4);
            if (size < 8 || offset + size > bits.Length)
            {
                break;
            }

            histogram[type] = histogram.GetValueOrDefault(type) + 1;

            // EMR_GDICOMMENT whose data starts with "EMF+": a GDI+ record carried inside the classic metafile.
            if (type == 70 && size >= 16 && BitConverter.ToUInt32(bits, offset + 12) == 0x2B464D45)
            {
                emfPlus++;
            }

            offset += (int)size;
        }

        facts.Add($"EMF frame {widthMm:0.#} × {heightMm:0.#} mm, {records:N0} records, {bits.Length:N0} B{(application.Length > 0 ? $", made by \"{application}\"" : string.Empty)}");
        facts.Add("records: " + string.Join(' ', histogram.OrderByDescending(p => p.Value).Take(10).Select(p => $"{RecordNames.GetValueOrDefault(p.Key, p.Key.ToString(CultureInfo.InvariantCulture))}×{p.Value}")));
        facts.Add(emfPlus > 0 ? $"EMF+ dual: {emfPlus} EMF+ record(s) (GDI+ plays those; PlayEnhMetaFile plays the GDI fallback)" : "GDI records only (no EMF+)");

        // At 96 DPI the frame's millimetres become pixels; the longer side is capped so a huge selection stays drawable.
        double naturalWidth = Math.Max(1, widthMm / 25.4 * 96), naturalHeight = Math.Max(1, heightMm / 25.4 * 96);
        double scale = widthPx > 0 ? widthPx / naturalWidth : Math.Min(1, 2400 / Math.Max(naturalWidth, naturalHeight));
        int width = Math.Max(1, (int)Math.Round(naturalWidth * scale)), height = Math.Max(1, (int)Math.Round(naturalHeight * scale));
        nint metafile = SetEnhMetaFileBits((uint)bits.Length, bits);
        if (metafile == 0)
        {
            facts.Add("GDI refused the bits");
            return null;
        }

        try
        {
            var watch = Stopwatch.StartNew();
            var pixels = Gdi.Draw(width, height, dc =>
            {
                var area = new RECT { Left = 0, Top = 0, Right = width, Bottom = height };
                PlayEnhMetaFile(dc, metafile, ref area);
            });
            facts.Add($"drawn with PlayEnhMetaFile at {width} × {height} px in {watch.Elapsed.TotalMilliseconds:0.0} ms");
            return Png.Encode(pixels, width, height);
        }
        finally
        {
            DeleteEnhMetaFile(metafile);
        }
    }
}

/// <summary>Bitmap headers and picture dimensions; never pixels.</summary>
static class Pictures
{
    /// <summary>Adds a DIB's header facts.</summary>
    /// <param name="data">CF_DIB or CF_DIBV5 bytes.</param>
    /// <param name="facts">Where the facts go.</param>
    public static void Dib(byte[] data, List<string> facts)
    {
        if (data.Length < 40)
        {
            facts.Add("too short for a DIB header");
            return;
        }

        uint headerSize = BitConverter.ToUInt32(data, 0);
        int width = BitConverter.ToInt32(data, 4), height = BitConverter.ToInt32(data, 8);
        ushort bits = BitConverter.ToUInt16(data, 14);
        uint compression = BitConverter.ToUInt32(data, 16);
        string header = headerSize switch { 40 => "BITMAPINFOHEADER", 108 => "BITMAPV4HEADER", 124 => "BITMAPV5HEADER", _ => $"{headerSize}-byte header" };
        string kind = compression switch { 0 => "BI_RGB", 3 => "BI_BITFIELDS", 4 => "BI_JPEG", 5 => "BI_PNG", _ => compression.ToString(CultureInfo.InvariantCulture) };
        facts.Add($"{header}: {width} × {Math.Abs(height)} ({(height < 0 ? "top-down" : "bottom-up")}), {bits} bpp, {kind}");
    }

    /// <summary>Adds a PNG's dimensions.</summary>
    /// <param name="data">PNG bytes.</param>
    /// <param name="facts">Where the facts go.</param>
    public static void Png(byte[] data, List<string> facts)
    {
        facts.Add(data.Length >= 24 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G'
            ? $"PNG {ReadBigEndian(data, 16)} × {ReadBigEndian(data, 20)}, colour type {data[25]}"
            : "not a PNG");
    }

    /// <summary>Adds a JPEG's dimensions from its first frame header.</summary>
    /// <param name="data">JPEG bytes.</param>
    /// <param name="facts">Where the facts go.</param>
    public static void Jpeg(byte[] data, List<string> facts)
    {
        for (int i = 2; i + 9 < data.Length;)
        {
            if (data[i] != 0xFF)
            {
                break;
            }

            byte marker = data[i + 1];
            int length = (data[i + 2] << 8) | data[i + 3];

            // SOF0–SOF15 carry the size, except DHT (C4), JPG (C8) and DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                facts.Add($"JPEG {(data[i + 7] << 8) | data[i + 8]} × {(data[i + 5] << 8) | data[i + 6]}");
                return;
            }

            i += 2 + length;
        }

        facts.Add("JPEG (no frame header found)");
    }

    /// <summary>Adds a GIF's logical screen size.</summary>
    /// <param name="data">GIF bytes.</param>
    /// <param name="facts">Where the facts go.</param>
    public static void Gif(byte[] data, List<string> facts) =>
        facts.Add(data.Length >= 10 ? $"GIF {BitConverter.ToUInt16(data, 6)} × {BitConverter.ToUInt16(data, 8)}" : "not a GIF");

    /// <summary>Adds the root element's size attributes of an SVG.</summary>
    /// <param name="data">SVG bytes.</param>
    /// <param name="facts">Where the facts go.</param>
    public static void Svg(byte[] data, List<string> facts)
    {
        var root = Regex.Match(Encoding.UTF8.GetString(data), @"<svg\b[^>]*>", RegexOptions.IgnoreCase);
        var attributes = Regex.Matches(root.Value, @"\s(width|height|viewBox)\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase)
            .Select(m => $"{m.Groups[1].Value}={m.Groups[2].Value}");
        facts.Add(root.Success ? "SVG " + string.Join(' ', attributes) : "no <svg> root");
    }

    /// <summary>Reads a big-endian 32-bit value.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="offset">Where the value starts.</param>
    /// <returns>The value.</returns>
    private static int ReadBigEndian(byte[] data, int offset) => (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
}

/// <summary>File lists: counts and extensions, never names.</summary>
static class Files
{
    /// <summary>Adds the count and extensions of a CF_HDROP list.</summary>
    /// <param name="data">The DROPFILES block.</param>
    /// <param name="facts">Where the facts go.</param>
    public static void DropList(byte[] data, List<string> facts)
    {
        if (data.Length < 20)
        {
            facts.Add("too short for DROPFILES");
            return;
        }

        int start = BitConverter.ToInt32(data, 0);
        bool wide = BitConverter.ToInt32(data, 16) != 0;
        string block = wide ? Encoding.Unicode.GetString(data, start, (data.Length - start) & ~1) : Encoding.Latin1.GetString(data, start, data.Length - start);
        var paths = block.Split('\0').TakeWhile(p => p.Length > 0).ToList();
        facts.Add($"{paths.Count} file(s): " + string.Join(' ', paths.Select(p => Path.GetExtension(p).ToLowerInvariant() is { Length: > 0 } e ? e : "(none)")
            .GroupBy(e => e).Select(g => $"{g.Key}×{g.Count()}")));
    }

    /// <summary>Adds the count and extensions of a FileGroupDescriptorW (virtual files: Outlook attachments, zip contents).</summary>
    /// <param name="data">The FILEGROUPDESCRIPTORW block.</param>
    /// <param name="facts">Where the facts go.</param>
    public static void Descriptors(byte[] data, List<string> facts)
    {
        const int DescriptorSize = 592, NameOffset = 72;
        int count = data.Length >= 4 ? BitConverter.ToInt32(data, 0) : 0;
        var extensions = new List<string>();
        for (int i = 0; i < count && 4 + ((i + 1) * DescriptorSize) <= data.Length; i++)
        {
            string name = Text.Utf16Z(data[(4 + (i * DescriptorSize) + NameOffset)..(4 + ((i + 1) * DescriptorSize))]);
            extensions.Add(Path.GetExtension(name).ToLowerInvariant() is { Length: > 0 } e ? e : "(none)");
        }

        facts.Add($"{count} virtual file(s): " + string.Join(' ', extensions.GroupBy(e => e).Select(g => $"{g.Key}×{g.Count()}")));
    }
}

/// <summary>The small descriptor formats apps add next to the content.</summary>
static class Descriptors
{
    /// <summary>Adds an OBJECTDESCRIPTOR's object type ("Microsoft Word Document"); its source path is never read.</summary>
    /// <param name="data">The OBJECTDESCRIPTOR block.</param>
    /// <param name="facts">Where the facts go.</param>
    public static void ObjectDescriptor(byte[] data, List<string> facts)
    {
        if (data.Length < 52)
        {
            facts.Add("too short for OBJECTDESCRIPTOR");
            return;
        }

        int typeOffset = BitConverter.ToInt32(data, 44);
        string type = typeOffset > 0 && typeOffset < data.Length ? Text.Utf16Z(data[typeOffset..]) : "(none)";
        facts.Add($"object type: {type}; class {new Guid(data[4..20])}");
    }

    /// <summary>Adds the editor language VS Code records next to a copy.</summary>
    /// <param name="data">The JSON.</param>
    /// <param name="facts">Where the facts go.</param>
    public static void VsCode(byte[] data, List<string> facts)
    {
        using var json = JsonDocument.Parse(Text.Guess(data));
        string mode = json.RootElement.TryGetProperty("mode", out var value) ? value.ToString() : "(none)";
        facts.Add($"VS Code language: {mode}");
    }

    /// <summary>Adds the MIME types inside Chromium's custom-data pickle (web pages' own clipboard types); never their data.</summary>
    /// <param name="data">The pickle: a 4-byte payload size, a count, then (type, data) UTF-16 string pairs, 4-byte aligned.</param>
    /// <param name="facts">Where the facts go.</param>
    public static void ChromiumCustom(byte[] data, List<string> facts)
    {
        var types = new List<string>();
        int offset = 4;
        int count = data.Length >= 8 ? BitConverter.ToInt32(data, offset) : 0;
        offset += 4;
        for (int i = 0; i < count && offset + 4 <= data.Length; i++)
        {
            string? type = ReadString16(data, ref offset);
            string? value = ReadString16(data, ref offset);
            if (type is null || value is null)
            {
                break;
            }

            types.Add($"{type} ({value.Length:N0} chars)");
        }

        facts.Add($"custom types {count}: " + string.Join(", ", types));
    }

    /// <summary>Reads one pickled UTF-16 string: a 32-bit length in characters, the characters, padding to 4 bytes.</summary>
    /// <param name="data">The pickle.</param>
    /// <param name="offset">The read position; advanced past the string.</param>
    /// <returns>The string, or <see langword="null"/> when it runs past the end.</returns>
    private static string? ReadString16(byte[] data, ref int offset)
    {
        if (offset + 4 > data.Length)
        {
            return null;
        }

        int length = BitConverter.ToInt32(data, offset);
        offset += 4;
        if (length < 0 || offset + (length * 2) > data.Length)
        {
            return null;
        }

        string value = Encoding.Unicode.GetString(data, offset, length * 2);
        offset += ((length * 2) + 3) & ~3;
        return value;
    }
}

/// <summary>What may be printed about a URL: the scheme, and the host only when it is a well-known service.</summary>
static class Privacy
{
    /// <summary>Hosts (and suffixes) named in the output because they identify the app, not the user's browsing.</summary>
    private static readonly string[] KnownHosts =
    [
        "teams.microsoft.com", "teams.cloud.microsoft", "teams.live.com", "outlook.office.com", "outlook.office365.com",
        "outlook.live.com", "outlook.cloud.microsoft", "www.office.com", ".sharepoint.com", ".officeapps.live.com",
        "localhost", "127.0.0.1",
    ];

    /// <summary>Describes a URL without its path or query: "https · teams.microsoft.com" or "https · another host".</summary>
    /// <param name="url">The URL.</param>
    /// <returns>The description.</returns>
    public static string DescribeUrl(string url)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url.Length == 0 ? "empty" : "not an absolute URL";
        }

        if (uri.IsFile)
        {
            return "file (a local document)";
        }

        string host = uri.Host.ToLowerInvariant();
        string known = KnownHosts.FirstOrDefault(k => k.StartsWith('.') ? host.EndsWith(k, StringComparison.Ordinal) : host == k) ?? string.Empty;
        return $"{uri.Scheme} · {(known.Length == 0 ? "another host" : known.StartsWith('.') ? "*" + known : known)}";
    }
}

/// <summary>Prints a snapshot and saves BC-TEST copies.</summary>
static class Report
{
    /// <summary>The marker every test document carries; only copies containing it are saved.</summary>
    public const string TestMarker = "BC-TEST";

    /// <summary>Prints a snapshot and, with <c>--save</c> and the marker, writes its payloads.</summary>
    /// <param name="copy">The snapshot.</param>
    /// <param name="number">Its number in this run.</param>
    /// <param name="options">The command line.</param>
    public static void PrintAndSave(ClipboardSnapshot copy, int number, Options options)
    {
        var text = new StringBuilder();
        text.AppendLine($"Copy {number} at {copy.ReadAt:HH:mm:ss.fff} · sequence {copy.Sequence} · owner {copy.Owner} · foreground {copy.Foreground} · "
            + $"{copy.Formats.Count} formats · the probe held the clipboard {copy.HeldMs:0.0} ms");
        int index = 0;
        foreach (var format in copy.Formats)
        {
            index++;
            string size = format.Bytes >= 0 ? $"{format.Bytes:N0} B" : "—";
            string fetch = format.WasRead ? $" · handed over in {format.FetchMs:0.0} ms{(format.FetchMs >= 2 ? " (rendered on request)" : string.Empty)}" : string.Empty;
            text.AppendLine($"  {index,2}. 0x{format.Id:X4} {format.Name,-42} {size,14}  {format.Storage}{fetch}");
            foreach (var fact in format.Facts)
            {
                text.AppendLine("        " + fact);
            }
        }

        Console.Write(text.ToString());
        if (options.SaveDir is null)
        {
            return;
        }

        if (!copy.HasTestMarker)
        {
            Console.WriteLine("  (not saved: the copy has no BC-TEST marker)");
            return;
        }

        string owner = Regex.Replace(copy.Owner.Split(' ')[0], @"[^A-Za-z0-9._-]", "_");
        string folder = Path.Combine(options.SaveDir, $"{copy.ReadAt:yyyyMMdd-HHmmss-fff}_{owner}");
        Directory.CreateDirectory(folder);
        index = 0;
        foreach (var format in copy.Formats)
        {
            index++;
            if (format.Data is not { } data)
            {
                continue;
            }

            string name = $"{index:00}_{Regex.Replace(format.Name, @"[^A-Za-z0-9._-]", "_")}";
            string extension = format.Name switch
            {
                "HTML Format" or "HTML (HyperText Markup Language)" or "text/html" => ".html",
                "Rich Text Format" or "Richtext Format" or "RTF As Text" => ".rtf",
                "PNG" or "image/png" => ".png",
                "JFIF" or "image/jpeg" => ".jpg",
                "GIF" or "image/gif" => ".gif",
                "image/svg+xml" => ".svg",
                "CF_ENHMETAFILE" => ".emf",
                "CF_UNICODETEXT" => ".txt",
                "Markdown" or "text/markdown" or "text/x-markdown" => ".md",
                _ => ".bin",
            };

            // Text is saved as UTF-8 so it opens anywhere; everything else exactly as the clipboard held it.
            File.WriteAllBytes(Path.Combine(folder, name + extension), format.Name == "CF_UNICODETEXT" ? Encoding.UTF8.GetBytes(Text.Utf16Z(data)) : data);
            if (format.Drawing is { } drawing)
            {
                File.WriteAllBytes(Path.Combine(folder, name + ".drawn.png"), drawing);
            }
        }

        File.WriteAllText(Path.Combine(folder, "describe.txt"), text.ToString());
        Console.WriteLine($"  saved to {folder}");
    }
}

/// <summary>Draws with GDI into a 32-bit top-down DIB section and returns its pixels.</summary>
static class Gdi
{
    /// <summary>Creates a white canvas, lets <paramref name="paint"/> draw on its DC, and copies the pixels out.</summary>
    /// <param name="width">Canvas width in pixels.</param>
    /// <param name="height">Canvas height in pixels.</param>
    /// <param name="paint">Draws on the memory DC.</param>
    /// <returns>BGRA pixels, top row first (alpha as GDI left it; <see cref="Png.Encode"/> makes it opaque).</returns>
    /// <exception cref="InvalidOperationException">The DIB section could not be created (too large, or out of GDI resources).</exception>
    public static byte[] Draw(int width, int height, Action<nint> paint)
    {
        nint screen = GetDC(0);
        nint dc = CreateCompatibleDC(screen);
        ReleaseDC(0, screen);
        var header = new BITMAPINFOHEADER { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        nint section = CreateDIBSection(dc, ref header, 0, out nint bits, 0, 0);
        if (section == 0)
        {
            DeleteDC(dc);
            throw new InvalidOperationException($"CreateDIBSection {width} × {height} failed.");
        }

        nint previous = SelectObject(dc, section);
        try
        {
            PatBlt(dc, 0, 0, width, height, WHITENESS);
            paint(dc);
            GdiFlush();
            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            SelectObject(dc, previous);
            DeleteObject(section);
            DeleteDC(dc);
        }
    }
}

/// <summary>
/// Draws RTF with Windows' RichEdit (Msftedit.dll) in a hidden window: the engine WinUI's RichEditBox is built on, so its
/// verdict on tables, lists and pictures predicts a RichEditBox preview.
/// </summary>
static class RtfRenderer
{
    /// <summary>The tallest drawing, in pixels; longer documents are cut there.</summary>
    private const int MaxHeightPx = 8000;

    /// <summary>The bytes the running EM_STREAMIN reads (one render per thread, see <see cref="ReadChunk"/>).</summary>
    [ThreadStatic]
    private static MemoryStream? streamSource;

    /// <summary>What a render produced and how long each step took.</summary>
    /// <param name="Pixels">BGRA pixels, top row first.</param>
    /// <param name="Width">Width in pixels.</param>
    /// <param name="Height">Height in pixels.</param>
    /// <param name="Dpi">The DPI RichEdit laid out for.</param>
    /// <param name="Characters">Text length after loading.</param>
    /// <param name="OleObjects">OLE objects RichEdit created (pictures it could not hold natively).</param>
    /// <param name="LoadMs">EM_STREAMIN time.</param>
    /// <param name="LayoutMs">The measuring EM_FORMATRANGE.</param>
    /// <param name="DrawMs">The drawing EM_FORMATRANGE.</param>
    /// <param name="Truncated">The text did not fit in <see cref="MaxHeightPx"/>.</param>
    /// <param name="EngineVersion">Msftedit.dll's file version.</param>
    /// <param name="Error">Why it failed, or <see langword="null"/>.</param>
    public sealed record Result(byte[] Pixels, int Width, int Height, int Dpi, int Characters, int OleObjects, double LoadMs, double LayoutMs, double DrawMs, bool Truncated, string EngineVersion, string? Error);

    /// <summary>Loads and draws RTF on a dedicated STA thread (RichEdit creates picture objects through OLE).</summary>
    /// <param name="rtf">The RTF bytes.</param>
    /// <param name="widthPx">The drawing width.</param>
    /// <returns>The result (its <see cref="Result.Error"/> says why when it failed).</returns>
    public static Result Render(byte[] rtf, int widthPx)
    {
        Result? result = null;
        var thread = new Thread(() => result = RenderOnThisThread(rtf, widthPx));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result!;
    }

    /// <summary>The render itself; must run on an STA thread.</summary>
    /// <param name="rtf">The RTF bytes.</param>
    /// <param name="widthPx">The drawing width.</param>
    /// <returns>The result.</returns>
    private static Result RenderOnThisThread(byte[] rtf, int widthPx)
    {
        OleInitialize(0);
        nint library = LoadLibraryW("Msftedit.dll");
        string version = library != 0 ? FileVersionInfo.GetVersionInfo(Path.Combine(Environment.SystemDirectory, "msftedit.dll")).FileVersion ?? "?" : "?";
        nint window = CreateWindowExW(0, "RICHEDIT50W", string.Empty, WS_POPUP | ES_MULTILINE | ES_READONLY, 0, 0, widthPx, 400, 0, 0, GetModuleHandleW(null), 0);
        if (window == 0)
        {
            return new Result([], 0, 0, 0, 0, 0, 0, 0, 0, false, version, $"no RICHEDIT50W window ({Marshal.GetLastPInvokeError()})");
        }

        // Without a callback that hands out OLE storage, RichEdit drops every \pict on load (measured: 0 objects, no
        // picture). WinForms' RichTextBox installs the same kind of callback.
        var callback = new OleCallback();
        nint callbackPointer = Marshal.GetComInterfaceForObject(callback, typeof(IRichEditOleCallback));
        try
        {
            SendMessageW(window, EM_EXLIMITTEXT, 0, int.MaxValue);
            SendMessageW(window, EM_SETOLECALLBACK, 0, callbackPointer);

            // EM_STREAMIN pulls the bytes through a callback: an [UnmanagedCallersOnly] function, not a marshalled delegate.
            streamSource = new MemoryStream(rtf);
            EDITSTREAM stream;
            unsafe
            {
                stream = new EDITSTREAM { dwCookie = 0, dwError = 0, pfnCallback = (nint)(delegate* unmanaged<nint, nint, int, int*, uint>)&ReadChunk };
            }

            var watch = Stopwatch.StartNew();
            SendMessageW(window, EM_STREAMIN, SF_RTF, ref stream);
            double loadMs = watch.Elapsed.TotalMilliseconds;
            streamSource = null;
            int characters = (int)SendMessageW(window, WM_GETTEXTLENGTH, 0, 0);
            int objects = OleObjectCount(window);

            nint screen = GetDC(0);
            nint measureDc = CreateCompatibleDC(screen);
            ReleaseDC(0, screen);
            int dpi = GetDeviceCaps(measureDc, LOGPIXELSX);
            int widthTwips = widthPx * 1440 / dpi;
            var range = new FORMATRANGE
            {
                hdc = measureDc,
                hdcTarget = measureDc,
                rc = new RECT { Right = widthTwips, Bottom = MaxHeightPx * 1440 / dpi },
                rcPage = new RECT { Right = widthTwips, Bottom = MaxHeightPx * 1440 / dpi },
                chrg = new CHARRANGE { cpMin = 0, cpMax = -1 },
            };

            // Measure: wParam 0 lays out without drawing and leaves the bottom of the last line in rc.bottom.
            watch.Restart();
            int next = (int)SendMessageW(window, EM_FORMATRANGE, 0, ref range);
            double layoutMs = watch.Elapsed.TotalMilliseconds;
            SendMessageW(window, EM_FORMATRANGE, 0, 0);
            DeleteDC(measureDc);
            int height = Math.Clamp((int)Math.Ceiling(range.rc.Bottom * dpi / 1440.0) + 4, 16, MaxHeightPx);

            // EM_FORMATRANGE counts a paragraph end as one character, WM_GETTEXTLENGTH as two (CRLF): compare heights instead.
            bool truncated = height >= MaxHeightPx && next > 0;

            double drawMs = 0;
            var pixels = Gdi.Draw(widthPx, height, dc =>
            {
                var draw = new FORMATRANGE
                {
                    hdc = dc,
                    hdcTarget = dc,
                    rc = new RECT { Right = widthTwips, Bottom = height * 1440 / dpi },
                    rcPage = new RECT { Right = widthTwips, Bottom = height * 1440 / dpi },
                    chrg = new CHARRANGE { cpMin = 0, cpMax = -1 },
                };
                var drawWatch = Stopwatch.StartNew();
                SendMessageW(window, EM_FORMATRANGE, 1, ref draw);
                drawMs = drawWatch.Elapsed.TotalMilliseconds;
                SendMessageW(window, EM_FORMATRANGE, 0, 0);
            });

            return new Result(pixels, widthPx, height, dpi, characters, objects, loadMs, layoutMs, drawMs, truncated, version, null);
        }
        finally
        {
            DestroyWindow(window);
            Marshal.Release(callbackPointer);
            GC.KeepAlive(callback);
            OleUninitialize();
        }
    }

    /// <summary>EM_STREAMIN's callback: copies the next bytes of <see cref="streamSource"/> into RichEdit's buffer.</summary>
    /// <param name="cookie">EDITSTREAM.dwCookie (unused: the source is <see cref="streamSource"/>).</param>
    /// <param name="buffer">RichEdit's buffer.</param>
    /// <param name="wanted">How many bytes it wants.</param>
    /// <param name="given">Where to store how many were copied (0 = the end).</param>
    /// <returns>0 to continue; 1 stops the load (nothing may throw back into RichEdit).</returns>
    [UnmanagedCallersOnly]
    private static unsafe uint ReadChunk(nint cookie, nint buffer, int wanted, int* given)
    {
        try
        {
            *given = streamSource!.Read(new Span<byte>((void*)buffer, wanted));
            return 0;
        }
        catch (Exception)
        {
            *given = 0;
            return 1;
        }
    }

    /// <summary>Asks RichEdit how many OLE objects it holds (IRichEditOle::GetObjectCount, the fifth vtable slot).</summary>
    /// <param name="window">The RichEdit window.</param>
    /// <returns>The count, or -1 when the interface is unavailable.</returns>
    private static unsafe int OleObjectCount(nint window)
    {
        nint ole = 0;
        if (SendMessageW(window, EM_GETOLEINTERFACE, 0, ref ole) == 0 || ole == 0)
        {
            return -1;
        }

        try
        {
            // IUnknown (3 slots), then GetClientSite, then GetObjectCount.
            var vtable = *(nint**)ole;
            var getObjectCount = (delegate* unmanaged[Stdcall]<nint, int>)vtable[4];
            return getObjectCount(ole);
        }
        finally
        {
            Marshal.Release(ole);
        }
    }
}

/// <summary>
/// RichEdit's OLE callback (<c>IRichEditOleCallback</c>): RichEdit asks it for storage for every object it creates, a
/// <c>\pict</c> included. Only <see cref="GetNewStorage"/> does work; the rest accept or decline politely.
/// </summary>
[ComImport]
[Guid("00020D03-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IRichEditOleCallback
{
    /// <summary>Storage for a new object.</summary>
    /// <param name="storage">The IStorage.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetNewStorage(out nint storage);

    /// <summary>In-place activation context (not supported).</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="document">The document window.</param>
    /// <param name="frameInfo">Frame information.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetInPlaceContext(out nint frame, out nint document, nint frameInfo);

    /// <summary>Shows or hides container UI.</summary>
    /// <param name="show">Show it.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int ShowContainerUI(int show);

    /// <summary>Whether an object may be inserted.</summary>
    /// <param name="classId">Its class.</param>
    /// <param name="storage">Its storage.</param>
    /// <param name="position">Where.</param>
    /// <returns>An HRESULT (S_OK accepts).</returns>
    [PreserveSig]
    int QueryInsertObject(nint classId, nint storage, int position);

    /// <summary>An object is being deleted.</summary>
    /// <param name="oleObject">The object.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int DeleteObject(nint oleObject);

    /// <summary>Whether pasted data may be accepted.</summary>
    /// <param name="dataObject">The data.</param>
    /// <param name="format">The format.</param>
    /// <param name="operation">The operation.</param>
    /// <param name="really">Whether it is the real paste.</param>
    /// <param name="metafile">A metafile picture.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int QueryAcceptData(nint dataObject, nint format, uint operation, int really, nint metafile);

    /// <summary>Context help mode.</summary>
    /// <param name="enter">Entering it.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int ContextSensitiveHelp(int enter);

    /// <summary>Clipboard data for a copy (not supported: RichEdit then makes its own).</summary>
    /// <param name="range">The characters.</param>
    /// <param name="operation">The operation.</param>
    /// <param name="dataObject">The data.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetClipboardData(nint range, uint operation, out nint dataObject);

    /// <summary>Drag-and-drop effect.</summary>
    /// <param name="drag">Dragging.</param>
    /// <param name="keyState">Key state.</param>
    /// <param name="effect">The effect.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetDragDropEffect(int drag, uint keyState, nint effect);

    /// <summary>A context menu.</summary>
    /// <param name="selectionType">Selection type.</param>
    /// <param name="oleObject">The object.</param>
    /// <param name="range">The characters.</param>
    /// <param name="menu">The menu.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetContextMenu(ushort selectionType, nint oleObject, nint range, out nint menu);
}

/// <summary>The probe's <see cref="IRichEditOleCallback"/>: storage on memory for every object, nothing else.</summary>
sealed class OleCallback : IRichEditOleCallback
{
    /// <summary>E_NOTIMPL.</summary>
    private const int NotImplemented = unchecked((int)0x80004001);

    /// <inheritdoc/>
    public int GetNewStorage(out nint storage)
    {
        storage = 0;
        int hr = CreateILockBytesOnHGlobal(0, true, out nint lockBytes);
        if (hr < 0)
        {
            return hr;
        }

        try
        {
            // STGM_SHARE_EXCLUSIVE | STGM_CREATE | STGM_READWRITE: what RichEdit expects of a new object's storage.
            return StgCreateDocfileOnILockBytes(lockBytes, 0x1012, 0, out storage);
        }
        finally
        {
            Marshal.Release(lockBytes); // the storage holds its own reference
        }
    }

    /// <inheritdoc/>
    public int GetInPlaceContext(out nint frame, out nint document, nint frameInfo)
    {
        frame = 0;
        document = 0;
        return NotImplemented;
    }

    /// <inheritdoc/>
    public int ShowContainerUI(int show) => 0;

    /// <inheritdoc/>
    public int QueryInsertObject(nint classId, nint storage, int position) => 0;

    /// <inheritdoc/>
    public int DeleteObject(nint oleObject) => 0;

    /// <inheritdoc/>
    public int QueryAcceptData(nint dataObject, nint format, uint operation, int really, nint metafile) => 0;

    /// <inheritdoc/>
    public int ContextSensitiveHelp(int enter) => 0;

    /// <inheritdoc/>
    public int GetClipboardData(nint range, uint operation, out nint dataObject)
    {
        dataObject = 0;
        return NotImplemented;
    }

    /// <inheritdoc/>
    public int GetDragDropEffect(int drag, uint keyState, nint effect) => 0;

    /// <inheritdoc/>
    public int GetContextMenu(ushort selectionType, nint oleObject, nint range, out nint menu)
    {
        menu = 0;
        return 0;
    }
}

/// <summary>A minimal PNG encoder (RGBA, no filtering), so the probe needs no imaging library.</summary>
static class Png
{
    /// <summary>The CRC-32 table of the PNG specification.</summary>
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Encodes BGRA pixels (top row first) as an opaque PNG.</summary>
    /// <param name="bgra">The pixels.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <returns>The PNG bytes.</returns>
    public static byte[] Encode(byte[] bgra, int width, int height)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        WriteChunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + (width * 4)];
            for (int y = 0; y < height; y++)
            {
                row[0] = 0; // filter type "none"
                for (int x = 0; x < width; x++)
                {
                    int source = ((y * width) + x) * 4;
                    row[1 + (x * 4)] = bgra[source + 2];
                    row[2 + (x * 4)] = bgra[source + 1];
                    row[3 + (x * 4)] = bgra[source];
                    row[4 + (x * 4)] = 255; // GDI leaves alpha 0: the drawing is opaque
                }

                zlib.Write(row);
            }
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>Writes one chunk: length, type, data, CRC over type and data.</summary>
    /// <param name="output">The PNG stream.</param>
    /// <param name="type">The four-letter type.</param>
    /// <param name="data">The chunk data.</param>
    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var buffer = new byte[4];
        WriteBigEndian(buffer, 0, (uint)data.Length);
        output.Write(buffer);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in typeBytes.Concat(data))
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        WriteBigEndian(buffer, 0, crc ^ 0xFFFFFFFF);
        output.Write(buffer);
    }

    /// <summary>Writes a big-endian 32-bit value.</summary>
    /// <param name="buffer">The target.</param>
    /// <param name="offset">Where to write.</param>
    /// <param name="value">The value.</param>
    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    /// <summary>Builds the CRC-32 table (polynomial 0xEDB88320).</summary>
    /// <returns>The table.</returns>
    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}

/// <summary>Names a window for the report: its process and, optionally, its class.</summary>
static class WindowInfo
{
    /// <summary>Describes a window as "process (class)".</summary>
    /// <param name="window">The window, or 0.</param>
    /// <param name="withClass">Whether to add the window class (OLE producers own the clipboard through CLIPBRDWNDCLASS).</param>
    /// <returns>The description, or "none".</returns>
    public static string Describe(nint window, bool withClass)
    {
        if (window == 0)
        {
            return "none";
        }

        GetWindowThreadProcessId(window, out uint processId);
        string process;
        try
        {
            using var p = Process.GetProcessById((int)processId);
            process = p.ProcessName;
        }
        catch (ArgumentException)
        {
            process = $"pid {processId} (exited)";
        }

        if (!withClass)
        {
            return process;
        }

        var buffer = new char[256];
        int length = GetClassNameW(window, buffer, buffer.Length);
        return $"{process} ({new string(buffer, 0, Math.Max(0, length))})";
    }
}

/// <summary>Win32 declarations.</summary>
static class Native
{
    /// <summary>Standard clipboard formats with special storage.</summary>
    public const uint CF_BITMAP = 2, CF_METAFILEPICT = 3, CF_PALETTE = 9, CF_ENHMETAFILE = 14;

    /// <summary>Messages.</summary>
    public const uint WM_TIMER = 0x0113, WM_CLIPBOARDUPDATE = 0x031D, WM_GETTEXTLENGTH = 0x000E;

    /// <summary>RichEdit messages (WM_USER + n).</summary>
    public const uint EM_EXLIMITTEXT = 0x0435, EM_FORMATRANGE = 0x0439, EM_GETOLEINTERFACE = 0x043C, EM_SETOLECALLBACK = 0x0446, EM_STREAMIN = 0x0449;

    /// <summary>EM_STREAMIN's format: RTF.</summary>
    public const nint SF_RTF = 2;

    /// <summary>Window styles: a popup (never shown), multi-line and read-only RichEdit.</summary>
    public const uint WS_POPUP = 0x80000000, ES_MULTILINE = 0x0004, ES_READONLY = 0x0800;

    /// <summary>The parent of message-only windows.</summary>
    public const nint HWND_MESSAGE = -3;

    /// <summary>PatBlt's white fill and GetDeviceCaps' horizontal DPI index.</summary>
    public const uint WHITENESS = 0x00FF0062;

    /// <summary>GetDeviceCaps index of the horizontal DPI.</summary>
    public const int LOGPIXELSX = 88;

    /// <summary>A window procedure.</summary>
    /// <param name="window">Window.</param>
    /// <param name="message">Message.</param>
    /// <param name="wParam">First parameter.</param>
    /// <param name="lParam">Second parameter.</param>
    /// <returns>The result.</returns>
    public delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);

    /// <summary>A rectangle.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        /// <summary>Edges.</summary>
        public int Left, Top, Right, Bottom;
    }

    /// <summary>A message from the queue.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        /// <summary>Window, message and parameters.</summary>
        public nint hwnd, message, wParam, lParam;

        /// <summary>Time and cursor position.</summary>
        public int time, x, y, lPrivate;
    }

    /// <summary>A window class.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        /// <summary>Size and style.</summary>
        public uint cbSize, style;

        /// <summary>The window procedure.</summary>
        public nint lpfnWndProc;

        /// <summary>Extra bytes.</summary>
        public int cbClsExtra, cbWndExtra;

        /// <summary>Handles.</summary>
        public nint hInstance, hIcon, hCursor, hbrBackground;

        /// <summary>Names.</summary>
        public string? lpszMenuName, lpszClassName;

        /// <summary>Small icon.</summary>
        public nint hIconSm;
    }

    /// <summary>A DIB header.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        /// <summary>Size.</summary>
        public uint biSize;

        /// <summary>Dimensions (a negative height is top-down).</summary>
        public int biWidth, biHeight;

        /// <summary>Planes and bits per pixel.</summary>
        public ushort biPlanes, biBitCount;

        /// <summary>Compression and image size.</summary>
        public uint biCompression, biSizeImage;

        /// <summary>Resolution.</summary>
        public int biXPelsPerMeter, biYPelsPerMeter;

        /// <summary>Palette counts.</summary>
        public uint biClrUsed, biClrImportant;
    }

    /// <summary>A GDI bitmap's description.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        /// <summary>Type, size and stride.</summary>
        public int bmType, bmWidth, bmHeight, bmWidthBytes;

        /// <summary>Planes and bits per pixel.</summary>
        public ushort bmPlanes, bmBitsPixel;

        /// <summary>The pixels (null for a device-dependent bitmap).</summary>
        public nint bmBits;
    }

    /// <summary>CF_METAFILEPICT's block: mapping mode, extent and the WMF handle.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct METAFILEPICT
    {
        /// <summary>Mapping mode and extent.</summary>
        public int mm, xExt, yExt;

        /// <summary>The Windows metafile.</summary>
        public nint hMF;
    }

    /// <summary>
    /// EM_STREAMIN's stream description. richedit.h packs its structures to 4 bytes (pshpack4.h), so on x64 the callback
    /// sits at offset 12, not 16: with the default packing RichEdit called a garbage pointer and Control Flow Guard ended
    /// the process (0xC0000409).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct EDITSTREAM
    {
        /// <summary>Passed to the callback.</summary>
        public nint dwCookie;

        /// <summary>The callback's error, if any.</summary>
        public uint dwError;

        /// <summary>The callback.</summary>
        public nint pfnCallback;
    }

    /// <summary>A character range (-1 = to the end).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CHARRANGE
    {
        /// <summary>First and last character.</summary>
        public int cpMin, cpMax;
    }

    /// <summary>EM_FORMATRANGE's request: device contexts, the area in twips and the characters (4-byte packed, like EDITSTREAM).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct FORMATRANGE
    {
        /// <summary>Where to draw and what to lay out for.</summary>
        public nint hdc, hdcTarget;

        /// <summary>The area to fill and the page (twips).</summary>
        public RECT rc, rcPage;

        /// <summary>The characters to format.</summary>
        public CHARRANGE chrg;
    }

    /// <summary>Opens the clipboard for reading.</summary>
    /// <param name="owner">The owning window, or 0.</param>
    /// <returns><see langword="true"/> when opened.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool OpenClipboard(nint owner);

    /// <summary>Closes the clipboard.</summary>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseClipboard();

    /// <summary>Enumerates the formats on the open clipboard.</summary>
    /// <param name="format">The previous format, or 0 to start.</param>
    /// <returns>The next format, or 0 at the end.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint EnumClipboardFormats(uint format);

    /// <summary>Gets a registered format's name.</summary>
    /// <param name="format">The format.</param>
    /// <param name="name">The buffer.</param>
    /// <param name="maxCount">The buffer's length.</param>
    /// <returns>The name's length, or 0.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClipboardFormatNameW(uint format, [Out] char[] name, int maxCount);

    /// <summary>Gets a format's data; delayed formats are rendered by their producer first.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The handle (owned by the clipboard), or 0.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint GetClipboardData(uint format);

    /// <summary>The clipboard's sequence number in this window station.</summary>
    /// <returns>The number.</returns>
    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    /// <summary>The clipboard's owner window.</summary>
    /// <returns>The window, or 0.</returns>
    [DllImport("user32.dll")]
    public static extern nint GetClipboardOwner();

    /// <summary>The foreground window.</summary>
    /// <returns>The window, or 0.</returns>
    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    /// <summary>A window's thread and process.</summary>
    /// <param name="window">The window.</param>
    /// <param name="processId">Its process id.</param>
    /// <returns>Its thread id.</returns>
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    /// <summary>A window's class name.</summary>
    /// <param name="window">The window.</param>
    /// <param name="name">The buffer.</param>
    /// <param name="maxCount">The buffer's length.</param>
    /// <returns>The name's length.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(nint window, [Out] char[] name, int maxCount);

    /// <summary>Starts clipboard notifications to a window.</summary>
    /// <param name="window">The window.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AddClipboardFormatListener(nint window);

    /// <summary>Stops clipboard notifications.</summary>
    /// <param name="window">The window.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RemoveClipboardFormatListener(nint window);

    /// <summary>Registers a window class.</summary>
    /// <param name="windowClass">The class.</param>
    /// <returns>The atom, or 0.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEX windowClass);

    /// <summary>Creates a window.</summary>
    /// <param name="exStyle">Extended style.</param>
    /// <param name="className">Class.</param>
    /// <param name="windowName">Title.</param>
    /// <param name="style">Style.</param>
    /// <param name="x">Left.</param>
    /// <param name="y">Top.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="parent">Parent (HWND_MESSAGE for a message-only window).</param>
    /// <param name="menu">Menu or id.</param>
    /// <param name="instance">Module.</param>
    /// <param name="param">Creation data.</param>
    /// <returns>The window, or 0.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    /// <summary>Destroys a window.</summary>
    /// <param name="window">The window.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(nint window);

    /// <summary>Default message handling.</summary>
    /// <param name="window">Window.</param>
    /// <param name="message">Message.</param>
    /// <param name="wParam">First parameter.</param>
    /// <param name="lParam">Second parameter.</param>
    /// <returns>The result.</returns>
    [DllImport("user32.dll")]
    public static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    /// <summary>Gets a message.</summary>
    /// <param name="message">The message.</param>
    /// <param name="window">Filter window.</param>
    /// <param name="min">Filter minimum.</param>
    /// <param name="max">Filter maximum.</param>
    /// <returns>0 on WM_QUIT, -1 on error, else positive.</returns>
    [DllImport("user32.dll")]
    public static extern int GetMessageW(out MSG message, nint window, uint min, uint max);

    /// <summary>Translates key messages.</summary>
    /// <param name="message">The message.</param>
    /// <returns>Whether a character message was posted.</returns>
    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG message);

    /// <summary>Dispatches a message to its window procedure.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The procedure's result.</returns>
    [DllImport("user32.dll")]
    public static extern nint DispatchMessageW(ref MSG message);

    /// <summary>Ends the message loop.</summary>
    /// <param name="exitCode">The exit code.</param>
    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int exitCode);

    /// <summary>Starts a window timer.</summary>
    /// <param name="window">The window.</param>
    /// <param name="id">Timer id.</param>
    /// <param name="milliseconds">Period.</param>
    /// <param name="callback">Callback, or 0 for WM_TIMER.</param>
    /// <returns>The timer id, or 0.</returns>
    [DllImport("user32.dll")]
    public static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);

    /// <summary>Sends a message with plain parameters.</summary>
    /// <param name="window">Window.</param>
    /// <param name="message">Message.</param>
    /// <param name="wParam">First parameter.</param>
    /// <param name="lParam">Second parameter.</param>
    /// <returns>The result.</returns>
    [DllImport("user32.dll")]
    public static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

    /// <summary>Sends EM_STREAMIN.</summary>
    /// <param name="window">Window.</param>
    /// <param name="message">EM_STREAMIN.</param>
    /// <param name="wParam">The stream format.</param>
    /// <param name="stream">The stream description.</param>
    /// <returns>Characters read.</returns>
    [DllImport("user32.dll")]
    public static extern nint SendMessageW(nint window, uint message, nint wParam, ref EDITSTREAM stream);

    /// <summary>Sends EM_FORMATRANGE.</summary>
    /// <param name="window">Window.</param>
    /// <param name="message">EM_FORMATRANGE.</param>
    /// <param name="wParam">1 = draw, 0 = measure.</param>
    /// <param name="range">The request (rc.bottom comes back with the bottom of the last line).</param>
    /// <returns>The index of the first character that did not fit.</returns>
    [DllImport("user32.dll")]
    public static extern nint SendMessageW(nint window, uint message, nint wParam, ref FORMATRANGE range);

    /// <summary>Sends EM_GETOLEINTERFACE.</summary>
    /// <param name="window">Window.</param>
    /// <param name="message">EM_GETOLEINTERFACE.</param>
    /// <param name="wParam">0.</param>
    /// <param name="ole">The IRichEditOle pointer (AddRef'ed).</param>
    /// <returns>Non-zero on success.</returns>
    [DllImport("user32.dll")]
    public static extern nint SendMessageW(nint window, uint message, nint wParam, ref nint ole);

    /// <summary>The screen's (or a window's) DC.</summary>
    /// <param name="window">0 for the screen.</param>
    /// <returns>The DC.</returns>
    [DllImport("user32.dll")]
    public static extern nint GetDC(nint window);

    /// <summary>Releases a DC from <see cref="GetDC"/>.</summary>
    /// <param name="window">The window it came from.</param>
    /// <param name="dc">The DC.</param>
    /// <returns>1 when released.</returns>
    [DllImport("user32.dll")]
    public static extern int ReleaseDC(nint window, nint dc);

    /// <summary>The size of an HGLOBAL (0 for handles that are not memory).</summary>
    /// <param name="memory">The handle.</param>
    /// <returns>The size.</returns>
    [DllImport("kernel32.dll")]
    public static extern nuint GlobalSize(nint memory);

    /// <summary>Locks an HGLOBAL.</summary>
    /// <param name="memory">The handle.</param>
    /// <returns>The pointer, or 0.</returns>
    [DllImport("kernel32.dll")]
    public static extern nint GlobalLock(nint memory);

    /// <summary>Unlocks an HGLOBAL.</summary>
    /// <param name="memory">The handle.</param>
    /// <returns>Whether it is still locked.</returns>
    [DllImport("kernel32.dll")]
    public static extern bool GlobalUnlock(nint memory);

    /// <summary>A loaded module's handle.</summary>
    /// <param name="name">The module, or null for the exe.</param>
    /// <returns>The handle.</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandleW(string? name);

    /// <summary>Loads a DLL.</summary>
    /// <param name="name">The DLL.</param>
    /// <returns>The module, or 0.</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint LoadLibraryW(string name);

    /// <summary>Copies a metafile's bits.</summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="size">The buffer's size (0 asks for the size).</param>
    /// <param name="buffer">The buffer, or null.</param>
    /// <returns>The size.</returns>
    [DllImport("gdi32.dll")]
    public static extern uint GetEnhMetaFileBits(nint metafile, uint size, byte[]? buffer);

    /// <summary>Creates a metafile from bits.</summary>
    /// <param name="size">The size.</param>
    /// <param name="bits">The bits.</param>
    /// <returns>The metafile, or 0.</returns>
    [DllImport("gdi32.dll")]
    public static extern nint SetEnhMetaFileBits(uint size, byte[] bits);

    /// <summary>Deletes a metafile handle.</summary>
    /// <param name="metafile">The metafile.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("gdi32.dll")]
    public static extern bool DeleteEnhMetaFile(nint metafile);

    /// <summary>Plays a metafile's GDI records into a DC.</summary>
    /// <param name="dc">The DC.</param>
    /// <param name="metafile">The metafile.</param>
    /// <param name="bounds">Where it is stretched to.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("gdi32.dll")]
    public static extern bool PlayEnhMetaFile(nint dc, nint metafile, ref RECT bounds);

    /// <summary>The size of a Windows metafile's bits.</summary>
    /// <param name="metafile">The WMF.</param>
    /// <param name="size">The buffer's size (0 asks for the size).</param>
    /// <param name="buffer">The buffer, or null.</param>
    /// <returns>The size.</returns>
    [DllImport("gdi32.dll")]
    public static extern uint GetMetaFileBitsEx(nint metafile, uint size, byte[]? buffer);

    /// <summary>Describes a GDI object (a bitmap here).</summary>
    /// <param name="handle">The object.</param>
    /// <param name="size">The buffer's size.</param>
    /// <param name="bitmap">The description.</param>
    /// <returns>Bytes written, or 0.</returns>
    [DllImport("gdi32.dll")]
    public static extern int GetObjectW(nint handle, int size, out BITMAP bitmap);

    /// <summary>A memory DC.</summary>
    /// <param name="dc">A DC to be compatible with.</param>
    /// <returns>The DC.</returns>
    [DllImport("gdi32.dll")]
    public static extern nint CreateCompatibleDC(nint dc);

    /// <summary>A DIB section.</summary>
    /// <param name="dc">A DC.</param>
    /// <param name="header">The header (no palette: 32 bpp).</param>
    /// <param name="usage">DIB_RGB_COLORS.</param>
    /// <param name="bits">The pixels.</param>
    /// <param name="section">0.</param>
    /// <param name="offset">0.</param>
    /// <returns>The bitmap, or 0.</returns>
    [DllImport("gdi32.dll")]
    public static extern nint CreateDIBSection(nint dc, ref BITMAPINFOHEADER header, uint usage, out nint bits, nint section, uint offset);

    /// <summary>Selects an object into a DC.</summary>
    /// <param name="dc">The DC.</param>
    /// <param name="handle">The object.</param>
    /// <returns>The previous object.</returns>
    [DllImport("gdi32.dll")]
    public static extern nint SelectObject(nint dc, nint handle);

    /// <summary>Deletes a GDI object.</summary>
    /// <param name="handle">The object.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(nint handle);

    /// <summary>Deletes a DC.</summary>
    /// <param name="dc">The DC.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(nint dc);

    /// <summary>Fills a rectangle with a raster operation.</summary>
    /// <param name="dc">The DC.</param>
    /// <param name="x">Left.</param>
    /// <param name="y">Top.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="rop">The operation (WHITENESS).</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("gdi32.dll")]
    public static extern bool PatBlt(nint dc, int x, int y, int width, int height, uint rop);

    /// <summary>A device capability.</summary>
    /// <param name="dc">The DC.</param>
    /// <param name="index">LOGPIXELSX.</param>
    /// <returns>The value.</returns>
    [DllImport("gdi32.dll")]
    public static extern int GetDeviceCaps(nint dc, int index);

    /// <summary>Finishes pending GDI drawing before the pixels are read.</summary>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("gdi32.dll")]
    public static extern bool GdiFlush();

    /// <summary>Initializes OLE on this STA thread (RichEdit's picture objects need it).</summary>
    /// <param name="reserved">0.</param>
    /// <returns>An HRESULT.</returns>
    [DllImport("ole32.dll")]
    public static extern int OleInitialize(nint reserved);

    /// <summary>Uninitializes OLE.</summary>
    [DllImport("ole32.dll")]
    public static extern void OleUninitialize();

    /// <summary>An ILockBytes on memory.</summary>
    /// <param name="memory">An HGLOBAL, or 0 for a new one.</param>
    /// <param name="deleteOnRelease">Free the memory with the last release.</param>
    /// <param name="lockBytes">The ILockBytes.</param>
    /// <returns>An HRESULT.</returns>
    [DllImport("ole32.dll")]
    public static extern int CreateILockBytesOnHGlobal(nint memory, bool deleteOnRelease, out nint lockBytes);

    /// <summary>A compound-file storage on an ILockBytes.</summary>
    /// <param name="lockBytes">The ILockBytes.</param>
    /// <param name="mode">STGM flags.</param>
    /// <param name="reserved">0.</param>
    /// <param name="storage">The IStorage.</param>
    /// <returns>An HRESULT.</returns>
    [DllImport("ole32.dll")]
    public static extern int StgCreateDocfileOnILockBytes(nint lockBytes, uint mode, uint reserved, out nint storage);
}
