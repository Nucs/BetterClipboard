// ─────────────────────────────────────────────────────────────────────────────────────────────────────
// Preview lab: how well, how fast and at what cost could BetterClipboard's panel preview a rich copy, measured with the
// engines it would use, inside WinUI 3 itself (CLAUDE.md §2.33).
//
//   PreviewLab.exe rtf  <file.rtf>  <log>               RTF in a read-only RichEditBox: load time (first and second),
//                                                        what survives (pictures by kind, tables), memory.
//   PreviewLab.exe html <file.html> <log> [--network]   HTML (CF_HTML with its header, or a plain document) in a WebView2
//                                                        with scripts off and every request blocked (unless --network,
//                                                        the control run): start-up, first and second page, a PNG
//                                                        capture, the requests the filter saw, the WebView2 processes.
//
// The window stays open 120 s for screenshots, then the lab exits. It opens and activates a window, so it refuses to run
// outside a claude-desktops Windows desktop (C:\ProgramData\claude-desktop exists only there; probe_summon.cs uses the
// same guard). Build and copy it there:
//   dotnet publish tools/probes/preview_lab -c Release -o <persist>\lab    (self-contained, ~175 MB)
// Samples: preview_lab/make_samples.py writes BC-TEST documents; preview_lab/serve.ps1 serves them over
// http://127.0.0.1:8123/ and logs every request (a page whose pictures load from it shows whether anything leaked).
// ─────────────────────────────────────────────────────────────────────────────────────────────────────
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Windows.Storage.Streams;

namespace PreviewLab;

/// <summary>Entry point: WinUI's start-up with a custom Main, like BetterClipboard.App's Program.</summary>
public static class Program
{
    /// <summary>The folder that exists only inside claude-desktops Windows desktops.</summary>
    private const string GuardFolder = @"C:\ProgramData\claude-desktop";

    /// <summary>Starts the lab, or refuses outside a disposable desktop (it opens and activates a window).</summary>
    /// <param name="args">Mode, input file, log file, options.</param>
    /// <returns>0, or 2 when refused.</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        if (!Directory.Exists(GuardFolder))
        {
            Console.Error.WriteLine($"Refusing to run: {GuardFolder} is missing, so this is not a claude-desktops Windows desktop. The lab opens and activates a window.");
            return 2;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new LabApp(args);
        });
        return 0;
    }
}

/// <summary>The lab application: one window, one experiment, a log file.</summary>
public sealed partial class LabApp : Application
{
    /// <summary>The command line.</summary>
    private readonly string[] args;

    /// <summary>The log, written to the log file when the experiment ends.</summary>
    private readonly StringBuilder log = new();

    /// <summary>The lab's window.</summary>
    private Window? window;

    /// <summary>Creates the app (App.xaml supplies the WinUI control styles).</summary>
    /// <param name="args">The command line.</param>
    public LabApp(string[] args)
    {
        this.args = args;
        InitializeComponent();
    }

    /// <summary>The log file the experiment writes (third argument, or one in the temp folder).</summary>
    private string LogPath => args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "preview-lab.log");

    /// <inheritdoc/>
    /// <exception cref="Exception">Anything that fails while the window is created is logged to <c>&lt;log&gt;.crash.txt</c> and rethrown.</exception>
    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        string crashLog = LogPath + ".crash.txt";

        // A failure inside a WinUI callback ends the process as a stowed exception (0xC000027B) with no message: keep one.
        UnhandledException += (_, failure) => File.AppendAllText(crashLog, $"unhandled: {failure.Exception}\n");
        try
        {
            window = new Window { Title = "BC-TEST preview lab" };
            window.AppWindow.MoveAndResize(new RectInt32(60, 40, 860, 820));
            _ = RunAsync();
        }
        catch (Exception ex)
        {
            File.AppendAllText(crashLog, $"OnLaunched: {ex}\n");
            throw;
        }
    }

    /// <summary>Runs the experiment named by the first argument, writes the log, and exits 120 s later.</summary>
    /// <returns>A task.</returns>
    private async Task RunAsync()
    {
        try
        {
            Log($"lab {string.Join(' ', args)} · {DateTime.Now:HH:mm:ss.fff} · process {Environment.ProcessId}");
            Log($"memory before: working set {Process.GetCurrentProcess().WorkingSet64 / 1048576.0:0.0} MB");
            switch (args.ElementAtOrDefault(0))
            {
                case "rtf":
                    await RtfAsync(File.ReadAllBytes(args[1]));
                    break;
                case "html":
                    await HtmlAsync(File.ReadAllBytes(args[1]), args.Contains("--network"));
                    break;
                default:
                    Log("usage: rtf|html <file> <log> [--network]");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"FAILED: {ex}");
        }
        finally
        {
            File.WriteAllText(LogPath, log.ToString());
        }

        // Left on screen for screenshots; the lab is a measuring tool, not something to keep running.
        await Task.Delay(TimeSpan.FromSeconds(120));
        Exit();
    }

    /// <summary>Loads RTF into a read-only RichEditBox and reports the time, what survived and the memory.</summary>
    /// <param name="rtfBytes">The RTF.</param>
    /// <returns>A task.</returns>
    private async Task RtfAsync(byte[] rtfBytes)
    {
        string rtf = Encoding.Latin1.GetString(rtfBytes).TrimEnd('\0');

        // SetText is refused while the box is read-only: load, then lock it (a preview must not be editable).
        var box = new RichEditBox { IsReadOnly = false, TextWrapping = TextWrapping.Wrap };
        window!.Content = box;
        var loaded = new TaskCompletionSource();
        box.Loaded += (_, _) => loaded.TrySetResult();
        window.Activate();
        await loaded.Task;

        var watch = Stopwatch.StartNew();
        box.Document.SetText(TextSetOptions.FormatRtf, rtf);
        double setMs = watch.Elapsed.TotalMilliseconds;
        box.IsReadOnly = true;
        box.Document.GetText(TextGetOptions.None, out string plain);
        box.Document.GetText(TextGetOptions.FormatRtf, out string roundTrip);
        Log($"RTF {rtf.Length:N0} chars → SetText(FormatRtf) {setMs:0.0} ms; plain text {plain.Length:N0} chars");
        Log($"round trip RTF {roundTrip.Length:N0} chars: \\pict {Count(roundTrip, "\\pict")}, pngblip {Count(roundTrip, "\\pngblip")}, jpegblip {Count(roundTrip, "\\jpegblip")}, "
            + $"emfblip {Count(roundTrip, "\\emfblip")}, wmetafile {Count(roundTrip, "\\wmetafile")}, dibitmap {Count(roundTrip, "\\dibitmap")}, objects {Count(roundTrip, "\\object")}");
        Log($"input RTF: \\pict {Count(rtf, "\\pict")}, \\trowd {Count(rtf, "\\trowd")}; round trip: \\trowd {Count(roundTrip, "\\trowd")}, \\cell {Count(roundTrip, "\\cell")}");

        // A second load of the same text: what a hover peek over a card already seen would pay.
        watch.Restart();
        box.IsReadOnly = false;
        box.Document.SetText(TextSetOptions.FormatRtf, rtf);
        box.IsReadOnly = true;
        Log($"second SetText {watch.Elapsed.TotalMilliseconds:0.0} ms");
        await Task.Delay(500);
        Log($"memory after: working set {Process.GetCurrentProcess().WorkingSet64 / 1048576.0:0.0} MB");
    }

    /// <summary>Renders HTML in a WebView2 with scripts off and (unless asked) every request blocked.</summary>
    /// <param name="htmlBytes">CF_HTML (with its header) or a plain HTML document.</param>
    /// <param name="allowNetwork">Let requests through: the control run that proves the blocking is what kept them in.</param>
    /// <returns>A task.</returns>
    private async Task HtmlAsync(byte[] htmlBytes, bool allowNetwork)
    {
        string html = Document(htmlBytes);
        Log($"HTML document {html.Length:N0} chars ({Encoding.UTF8.GetByteCount(html):N0} bytes; NavigateToString takes at most 2 MB)");

        // Its own profile folder: a preview must never share cookies or caches with anything else.
        string dataFolder = Path.Combine(Path.GetTempPath(), "bc-preview-lab-webview2");
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", dataFolder);
        Log($"user data folder existed before: {Directory.Exists(dataFolder)}");

        var view = new WebView2();
        window!.Content = view;
        window.Activate();
        var watch = Stopwatch.StartNew();
        await view.EnsureCoreWebView2Async();
        double startMs = watch.Elapsed.TotalMilliseconds;
        var core = view.CoreWebView2;
        Log($"EnsureCoreWebView2Async {startMs:0} ms; runtime {core.Environment.BrowserVersionString}");

        // PowerToys Peek's lockdown (MIT, studied, not copied): no scripts, dialogs, dev tools, host objects or autofill.
        core.Settings.IsScriptEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        int intercepted = 0, blocked = 0;
        var origins = new Dictionary<string, int>();

        // Every request goes through the filter; a 403 answered here never reaches the network (no DNS, no connection).
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, request) =>
        {
            intercepted++;
            var uri = new Uri(request.Request.Uri);
            string origin = $"{uri.Scheme}://{uri.Host}";
            origins[origin] = origins.GetValueOrDefault(origin) + 1;
            if (!allowNetwork)
            {
                blocked++;
                request.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", string.Empty);
            }
        };

        for (int round = 1; round <= 2; round++)
        {
            var navigated = new TaskCompletionSource<bool>();
            void OnCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs completed) => navigated.TrySetResult(completed.IsSuccess);
            core.NavigationCompleted += OnCompleted;
            watch.Restart();
            core.NavigateToString(html);
            bool ok = await navigated.Task;
            double navigateMs = watch.Elapsed.TotalMilliseconds;
            core.NavigationCompleted -= OnCompleted;
            await Task.Delay(300);
            watch.Restart();
            using var stream = new InMemoryRandomAccessStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            double captureMs = watch.Elapsed.TotalMilliseconds;
            var bytes = new byte[stream.Size];
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);
            }

            string png = Path.Combine(Path.GetDirectoryName(LogPath) ?? Path.GetTempPath(), $"{Path.GetFileNameWithoutExtension(LogPath)}-round{round}.png");
            File.WriteAllBytes(png, bytes);
            Log($"round {round}: NavigateToString → NavigationCompleted {navigateMs:0} ms (success {ok}); CapturePreviewAsync {captureMs:0} ms → {png}");
        }

        Log($"requests seen by the filter: {intercepted}, blocked: {blocked}; by origin: {string.Join(", ", origins.Select(p => $"{p.Key}×{p.Value}"))}");
        long workingSet = 0, privateBytes = 0;
        var kinds = new List<string>();
        foreach (var info in core.Environment.GetProcessInfos())
        {
            try
            {
                using var process = Process.GetProcessById(info.ProcessId);
                workingSet += process.WorkingSet64;
                privateBytes += process.PrivateMemorySize64;
                kinds.Add(info.Kind.ToString());
            }
            catch (ArgumentException)
            {
                kinds.Add(info.Kind + " (gone)");
            }
        }

        Log($"WebView2 processes {kinds.Count}: {string.Join(", ", kinds)}; working set {workingSet / 1048576.0:0} MB, private {privateBytes / 1048576.0:0} MB");
        Log($"lab process: working set {Process.GetCurrentProcess().WorkingSet64 / 1048576.0:0.0} MB");
    }

    /// <summary>The HTML document of a CF_HTML payload (StartHTML..EndHTML), or the bytes as they are.</summary>
    /// <param name="bytes">The payload.</param>
    /// <returns>The document (UTF-8 decoded).</returns>
    private static string Document(byte[] bytes)
    {
        string head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 400));
        var start = Regex.Match(head, @"StartHTML:(\d+)");
        var end = Regex.Match(head, @"EndHTML:(\d+)");
        if (head.StartsWith("Version:", StringComparison.Ordinal) && start.Success && end.Success)
        {
            int from = int.Parse(start.Groups[1].Value), to = Math.Min(int.Parse(end.Groups[1].Value), bytes.Length);
            if (from >= 0 && to > from)
            {
                return Encoding.UTF8.GetString(bytes, from, to - from);
            }
        }

        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }

    /// <summary>Counts the non-overlapping occurrences of a literal.</summary>
    /// <param name="text">The text.</param>
    /// <param name="literal">The literal.</param>
    /// <returns>The count.</returns>
    private static int Count(string text, string literal)
    {
        int count = 0;
        for (int at = text.IndexOf(literal, StringComparison.Ordinal); at >= 0; at = text.IndexOf(literal, at + literal.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>Adds a line to the log (and to the debugger output).</summary>
    /// <param name="line">The line.</param>
    private void Log(string line)
    {
        log.AppendLine(line);
        Debug.WriteLine(line);
    }
}
