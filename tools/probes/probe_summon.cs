#:property PublishAot=false
#:property AllowUnsafeBlocks=true
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using static Native;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────
// Probe: how fast does the BetterClipboard panel appear after Win+V, as the user sees it (screen pixels), and how soon
// after Win+V does a typed letter reach its search box instead of the app below?
//
// It injects keys (Win+V, Esc, letters) and takes the foreground for its run, so it runs ONLY inside a disposable
// claude-desktops Windows desktop: it refuses where C:\ProgramData\claude-desktop is missing (that folder exists only
// inside those desktops; tools/readme/demo-data.ps1 uses the same guard). Start a BetterClipboard there first and wait
// ~5 s (the panel is prepared 3 s after startup), then:
//
//   dotnet run tools/probes/probe_summon.cs -- [--rounds 15] [--type-ahead 0,10,20,30,50,80,120] [--animations on|off]
//   (or a published copy: dotnet publish tools/probes/probe_summon.cs -r win-x64 --self-contained, run the exe)
//
// Method. A magenta "stage" window covers the work area and holds a Win32 caret, like a text editor (the panel opens
// next to a caret). A calibration summon finds the panel's rectangle; each round then injects Win+V and samples three
// pixel rows across that rectangle (header, two card rows; never the search box, whose caret blinks) from the screen as
// fast as GDI allows, plus the foreground window, for 1.5 s. Reported per round, counted from the injection:
//   foreground  the panel became the foreground window;
//   first       the first sampled pixel differed from before the summon (the window or its backdrop is on screen);
//   complete    from here on the rows equal the final picture (animations finished, the list loaded and drawn);
//   states      distinct pictures from first to complete (1 = it appeared in one step).
// --type-ahead: per delay, Win+V, then a "q" that many ms later; where it went: the stage's WM_CHAR (leaked into the app
// below), the panel's search box (UI Automation), or neither (lost).
// --animations sets Windows' "Animation effects" (SPI_SETCLIENTAREAANIMATION and the minimize/maximize animation) for
// the run and puts them back at the end; the user's PC has both on (checked 2026-10-09).
// Content-free: it reads no clipboard and prints only times and counts.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────

const string GuardFolder = @"C:\ProgramData\claude-desktop";
if (!Directory.Exists(GuardFolder))
{
    Console.Error.WriteLine($"Refusing to run: {GuardFolder} is missing, so this is not a claude-desktops Windows desktop. "
        + "The probe injects Win+V, Esc and letters and takes the foreground.");
    return 2;
}

int rounds = 15;
int[] typeAheadDelays = [];
bool? animations = null;
bool revealTest = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--rounds" when i + 1 < args.Length:
            rounds = int.Parse(args[++i]);
            break;
        case "--type-ahead" when i + 1 < args.Length:
            typeAheadDelays = [.. args[++i].Split(',').Select(int.Parse)];
            break;
        case "--animations" when i + 1 < args.Length:
            animations = args[++i] == "on";
            break;
        case "--reveal-test":
            revealTest = true;
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

// Physical pixels everywhere: window rectangles, the screen DC and SetCursorPos then agree at any scale.
SetProcessDpiAwarenessContext(-4);

var (savedClientArea, savedMinMax) = Animations.Read();
Console.WriteLine($"Animation effects: {savedClientArea}; minimize/maximize animation: {savedMinMax}");
if (animations is { } wanted)
{
    Animations.Write(wanted, wanted);
    Console.WriteLine($"Set both to {wanted} for this run.");
}

if (revealTest)
{
    // DWM's own latency per way of revealing a window, on a plain red window: no BetterClipboard involved.
    using var testStage = Stage.Create();
    testStage.EnsureForeground();
    try
    {
        RevealTest.Run(rounds);
        return 0;
    }
    finally
    {
        if (animations is not null)
        {
            Animations.Write(savedClientArea, savedMinMax);
        }
    }
}

var appProcesses = Process.GetProcessesByName("BetterClipboard").Select(p => (uint)p.Id).ToHashSet();
if (appProcesses.Count == 0)
{
    Console.Error.WriteLine("No BetterClipboard process runs: start one first.");
    return 3;
}

using var stage = Stage.Create();
try
{
    // Calibration: summons until two in a row open at the same place (next to the stage's caret). The first summon of a
    // run has opened at the pointer instead (seen 2026-10-09, the caret not reported for it), so one summon is not enough.
    RECT bounds = default;
    bool calibrated = false;
    for (int attempt = 0; attempt < 5 && !calibrated; attempt++)
    {
        stage.EnsureForeground();
        Thread.Sleep(300);
        Keys.WinV();
        nint panel = Polling.Wait(() => Foreground.Panel(appProcesses), TimeSpan.FromSeconds(5));
        if (panel == 0)
        {
            Console.Error.WriteLine("The panel never became the foreground window after Win+V.");
            return 4;
        }

        Thread.Sleep(1500);
        var seen = Foreground.FrameBounds(panel);
        calibrated = attempt > 0 && seen.Left == bounds.Left && seen.Top == bounds.Top;
        bounds = seen;
        stage.Dismiss(appProcesses);
    }

    if (!calibrated)
    {
        Console.Error.WriteLine("The panel opened at a different place in each calibration summon.");
        return 5;
    }

    Console.WriteLine($"Panel: {bounds.Right - bounds.Left} x {bounds.Bottom - bounds.Top} px at ({bounds.Left}, {bounds.Top})");

    using var sampler = new RowSampler(bounds, [0.03, 0.35, 0.50, 0.65, 0.80]);
    var foreground = new List<double>();
    var first = new List<double>();
    var complete = new List<double>();
    for (int round = 1; round <= rounds; round++)
    {
        stage.EnsureForeground();
        Thread.Sleep(300);
        var baseline = sampler.Sample();
        var samples = new List<(long Ticks, ulong[] Rows, bool PanelForeground)>(8192);
        long start = Stopwatch.GetTimestamp();
        long foregroundAt = 0;
        Keys.WinV();
        while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 1500)
        {
            // The foreground check is cheap: poll it on its own clock, before the (slow) screen copy.
            if (foregroundAt == 0 && Foreground.Panel(appProcesses) != 0)
            {
                foregroundAt = Stopwatch.GetTimestamp();
            }

            var rows = sampler.Sample();
            samples.Add((Stopwatch.GetTimestamp(), rows, foregroundAt != 0));
        }

        double Ms(long ticks) => (ticks - start) * 1000.0 / Stopwatch.Frequency;
        nint opened = Foreground.Panel(appProcesses);
        var openedAt = opened == 0 ? default : Foreground.FrameBounds(opened);
        if (opened == 0 || openedAt.Left != bounds.Left || openedAt.Top != bounds.Top)
        {
            Console.WriteLine($"round {round,2}: skipped, the panel {(opened == 0 ? "was not in front" : $"opened at ({openedAt.Left}, {openedAt.Top})")} instead of where calibrated");
            stage.Dismiss(appProcesses);
            continue;
        }

        int firstIndex = samples.FindIndex(s => !s.Rows.SequenceEqual(baseline));
        var final = samples[^1].Rows;
        int completeIndex = samples.Count - 1;
        while (completeIndex > 0 && samples[completeIndex - 1].Rows.SequenceEqual(final))
        {
            completeIndex--;
        }

        int states = 0;
        for (int i = Math.Max(firstIndex, 0); i <= completeIndex && firstIndex >= 0; i++)
        {
            if (i == firstIndex || !samples[i].Rows.SequenceEqual(samples[i - 1].Rows))
            {
                states++;
            }
        }

        // The last ~200 ms must be stable, or "complete" is only where the window ended, not where the picture settled.
        bool settled = Ms(samples[^1].Ticks) - Ms(samples[completeIndex].Ticks) > 200;
        double interval = (Ms(samples[^1].Ticks) - Ms(samples[0].Ticks)) / Math.Max(1, samples.Count - 1);
        Console.WriteLine(
            $"round {round,2}: foreground {Report.Format(foregroundAt == 0 ? null : Ms(foregroundAt))}, "
            + $"first {Report.Format(firstIndex < 0 ? null : Ms(samples[firstIndex].Ticks))}, "
            + $"complete {Report.Format(Ms(samples[completeIndex].Ticks))}{(settled ? string.Empty : " (not settled)")}, "
            + $"states {states}, sampled every {interval:0.0} ms");
        if (foregroundAt != 0)
        {
            foreground.Add(Ms(foregroundAt));
        }

        if (firstIndex >= 0)
        {
            first.Add(Ms(samples[firstIndex].Ticks));
            complete.Add(Ms(samples[completeIndex].Ticks));
        }

        stage.Dismiss(appProcesses);
    }

    Console.WriteLine();
    Console.WriteLine($"foreground  {Report.Summary(foreground)}");
    Console.WriteLine($"first       {Report.Summary(first)}");
    Console.WriteLine($"complete    {Report.Summary(complete)}");

    foreach (int delay in typeAheadDelays)
    {
        int leaked = 0, typed = 0, lost = 0;
        for (int round = 0; round < rounds; round++)
        {
            stage.EnsureForeground();
            Thread.Sleep(300);
            long start = Stopwatch.GetTimestamp();
            Keys.WinV();
            while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < delay)
            {
                Thread.SpinWait(50);
            }

            Keys.Tap(VK_Q);
            Thread.Sleep(800);
            bool inStage = stage.ReceivedSince(start, 'q');
            nint shown = Foreground.Panel(appProcesses);
            bool inPanel = shown != 0 && (Uia.SearchText(shown)?.Contains('q') ?? false);
            if (inStage)
            {
                leaked++;
            }
            else if (inPanel)
            {
                typed++;
            }
            else
            {
                lost++;
            }

            stage.Dismiss(appProcesses);
        }

        Console.WriteLine($"type-ahead {delay,4} ms: search box {typed}/{rounds}, leaked into the app below {leaked}, lost {lost}");
    }

    return 0;
}
finally
{
    if (animations is not null)
    {
        Animations.Write(savedClientArea, savedMinMax);
    }
}

/// <summary>Formatting of the measured times.</summary>
static class Report
{
    /// <summary>Milliseconds with one decimal, or "never".</summary>
    /// <param name="ms">A time, or <see langword="null"/> when the event never happened.</param>
    /// <returns>The text.</returns>
    public static string Format(double? ms) => ms is { } value ? $"{value,6:0.0} ms" : " never  ";

    /// <summary>Median, 90th percentile, minimum and maximum of a list of times.</summary>
    /// <param name="values">Times in ms.</param>
    /// <returns>One line, or "no data".</returns>
    public static string Summary(List<double> values)
    {
        if (values.Count == 0)
        {
            return "no data";
        }

        var sorted = values.Order().ToArray();
        double Percentile(double p) => sorted[(int)Math.Min(sorted.Length - 1, Math.Round(p * (sorted.Length - 1)))];
        return $"median {Percentile(0.5):0.0} ms, p90 {Percentile(0.9):0.0} ms, min {sorted[0]:0.0} ms, max {sorted[^1]:0.0} ms (n={sorted.Length})";
    }
}

/// <summary>
/// The stand-in for "the app the user types in": a magenta window over the work area, on its own STA thread, holding a
/// Win32 caret while focused (so the panel opens next to it, as over a text editor) and recording every WM_CHAR it gets
/// (a letter meant for the panel that leaked into the app below).
/// </summary>
sealed class Stage : IDisposable
{
    /// <summary>Caret position in the stage's client area: near the top left, so the panel has room below and right.</summary>
    private const int CaretX = 120, CaretY = 140;

    /// <summary>Characters received, with their Stopwatch timestamps.</summary>
    private static readonly ConcurrentQueue<(long Ticks, char Char)> Chars = new();

    /// <summary>The window procedure, kept alive for as long as the window exists (a collected delegate crashes the app).</summary>
    private static WndProc? procedure;

    /// <summary>The thread that owns the window and pumps its messages (joined by <see cref="Dispose"/>).</summary>
    private readonly Thread thread;

    /// <summary>Wraps a stage window that <see cref="Create"/> made.</summary>
    /// <param name="thread">The thread that owns the window.</param>
    /// <param name="handle">The window.</param>
    /// <param name="bounds">Its screen rectangle.</param>
    private Stage(Thread thread, nint handle, RECT bounds)
    {
        this.thread = thread;
        Handle = handle;
        Bounds = bounds;
    }

    /// <summary>The stage's window.</summary>
    public nint Handle { get; }

    /// <summary>Its screen rectangle (the primary monitor's work area).</summary>
    public RECT Bounds { get; }

    /// <summary>Creates the stage on its own message-loop thread and waits until it exists.</summary>
    /// <returns>The stage.</returns>
    /// <exception cref="InvalidOperationException">The window could not be created.</exception>
    public static Stage Create()
    {
        nint created = 0;
        RECT area = default;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            SystemParametersInfo(SPI_GETWORKAREA, 0, ref area, 0);
            procedure = WindowProcedure;
            var windowClass = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(procedure),
                hInstance = GetModuleHandle(null),
                hbrBackground = CreateSolidBrush(0x00FF00FF),
                lpszClassName = "BetterClipboardSummonProbeStage",
                hCursor = LoadCursor(0, 32512),
            };
            RegisterClassEx(ref windowClass);
            created = CreateWindowEx(0, windowClass.lpszClassName!, "Summon probe stage", WS_POPUP | WS_VISIBLE,
                area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top, 0, 0, windowClass.hInstance, 0);
            ready.Set();
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        })
        { IsBackground = true, Name = "Stage" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (created == 0)
        {
            throw new InvalidOperationException("The stage window could not be created.");
        }

        return new Stage(thread, created, area);
    }

    /// <summary>Whether <paramref name="character"/> reached the stage at or after <paramref name="ticks"/>.</summary>
    /// <param name="ticks">A Stopwatch timestamp.</param>
    /// <param name="character">The character.</param>
    /// <returns><see langword="true"/> when it arrived.</returns>
    public bool ReceivedSince(long ticks, char character) => Chars.Any(c => c.Ticks >= ticks && c.Char == character);

    /// <summary>
    /// Makes the stage the foreground window (a click on it when Windows refuses SetForegroundWindow), then parks the
    /// cursor in its bottom-right corner, away from where the panel opens (no hover effects in the samples).
    /// </summary>
    public void EnsureForeground()
    {
        if (GetForegroundWindow() != Handle)
        {
            SetForegroundWindow(Handle);
            Thread.Sleep(50);
        }

        if (GetForegroundWindow() != Handle)
        {
            Mouse.Click(Bounds.Left + 40, Bounds.Bottom - 40);
            Thread.Sleep(150);
        }

        SetCursorPos(Bounds.Right - 3, Bounds.Bottom - 3);
        uint stageThread = GetWindowThreadProcessId(Handle, out _);
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 2000)
        {
            var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
            if (GetGUIThreadInfo(stageThread, ref info) && info.hwndCaret == Handle)
            {
                return;
            }

            Thread.Sleep(5);
        }

        Console.Error.WriteLine("The stage has no caret after 2 s: the panel will open at the pointer, and the round is skipped.");
    }

    /// <summary>
    /// Closes the panel the way a user does: Esc (a second Esc when the first only emptied the search box), then waits for
    /// the stage to be in front again and gives the panel 700 ms for whatever it does after hiding.
    /// </summary>
    /// <param name="appProcesses">BetterClipboard's process ids.</param>
    public void Dismiss(HashSet<uint> appProcesses)
    {
        Keys.Tap(VK_ESCAPE);
        Thread.Sleep(150);
        if (Foreground.Panel(appProcesses) != 0)
        {
            Keys.Tap(VK_ESCAPE);
        }

        Polling.Wait(() => GetForegroundWindow() == Handle ? Handle : 0, TimeSpan.FromSeconds(2));
        Thread.Sleep(700);
    }

    /// <summary>Closes the stage window and ends its thread.</summary>
    public void Dispose()
    {
        PostMessage(Handle, WM_CLOSE, 0, 0);
        thread.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>The stage's window procedure: a caret while focused, characters recorded, quit on destroy.</summary>
    /// <param name="hwnd">The stage.</param>
    /// <param name="message">The message.</param>
    /// <param name="wParam">Its first parameter (the character for WM_CHAR).</param>
    /// <param name="lParam">Its second parameter.</param>
    /// <returns>The message's result.</returns>
    private static nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WM_SETFOCUS:
                // Like any text editor: a caret while focused, which GetGUIThreadInfo reports to the panel's placement.
                CreateCaret(hwnd, 0, 2, 22);
                SetCaretPos(CaretX, CaretY);
                ShowCaret(hwnd);
                return 0;
            case WM_KILLFOCUS:
                DestroyCaret();
                return 0;
            case WM_CHAR:
                Chars.Enqueue((Stopwatch.GetTimestamp(), (char)wParam));
                return 0;
            case WM_DESTROY:
                PostQuitMessage(0);
                return 0;
            default:
                return DefWindowProc(hwnd, message, wParam, lParam);
        }
    }
}

/// <summary>
/// Copies the panel's rectangle of the screen (what DWM last composed) into a DIB section with ONE BitBlt per sample and
/// hashes a few of its pixel rows, so a change anywhere along a row is seen. One copy per sample matters: in a VM without
/// a GPU every screen BitBlt costs ~25 ms whatever its size, and three one-row copies made a sample take 70-120 ms.
/// </summary>
sealed class RowSampler : IDisposable
{
    /// <summary>GDI objects: the screen DC, the memory DC, the DIB section, the bitmap it replaced, and its pixel memory.</summary>
    private readonly nint screenDc, memoryDc, bitmap, previous, bits;

    /// <summary>The copied rectangle (a few pixels inside the panel's visible bounds).</summary>
    private readonly int left, top, width, height;

    /// <summary>The hashed rows, as offsets inside the copied rectangle.</summary>
    private readonly int[] rows;

    /// <summary>Prepares the copy of <paramref name="area"/> and the rows to hash.</summary>
    /// <param name="area">The panel's screen rectangle.</param>
    /// <param name="fractions">Heights of the hashed rows, 0 = top edge, 1 = bottom edge.</param>
    /// <exception cref="InvalidOperationException">GDI refused a device context or the bitmap.</exception>
    public RowSampler(RECT area, double[] fractions)
    {
        // A few pixels in from the edges: the rounded corners and the border are not content.
        left = area.Left + 6;
        top = area.Top + 6;
        width = Math.Max(1, area.Right - area.Left - 12);
        height = Math.Max(1, area.Bottom - area.Top - 12);
        rows = [.. fractions.Select(f => Math.Clamp((int)Math.Round(height * f), 0, height - 1))];
        screenDc = GetDC(0);
        memoryDc = CreateCompatibleDC(screenDc);
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down: row y is at offset y * stride
            biPlanes = 1,
            biBitCount = 32,
        };
        bitmap = CreateDIBSection(screenDc, ref header, 0, out bits, 0, 0);
        if (screenDc == 0 || memoryDc == 0 || bitmap == 0)
        {
            throw new InvalidOperationException("GDI refused the sampling surfaces.");
        }

        previous = SelectObject(memoryDc, bitmap);
    }

    /// <summary>Copies the rectangle from the screen and hashes each chosen row (FNV-1a, 64 bits).</summary>
    /// <returns>One hash per row.</returns>
    public unsafe ulong[] Sample()
    {
        BitBlt(memoryDc, 0, 0, width, height, screenDc, left, top, SRCCOPY);
        GdiFlush();
        var hashes = new ulong[rows.Length];
        byte* data = (byte*)bits;
        int stride = width * 4;
        for (int i = 0; i < rows.Length; i++)
        {
            ulong hash = 14695981039346656037UL;
            byte* row = data + ((long)rows[i] * stride);
            for (int b = 0; b < stride; b++)
            {
                hash = (hash ^ row[b]) * 1099511628211UL;
            }

            hashes[i] = hash;
        }

        return hashes;
    }

    /// <summary>Releases the GDI objects.</summary>
    public void Dispose()
    {
        SelectObject(memoryDc, previous);
        DeleteObject(bitmap);
        DeleteDC(memoryDc);
        ReleaseDC(0, screenDc);
    }
}

/// <summary>
/// <c>--reveal-test</c>: how long DWM takes to put a window on screen for each way an app can reveal one that is ready —
/// showing it (<c>SW_SHOWNA</c> after <c>SW_HIDE</c>), uncloaking it (DWMWA_CLOAK) and moving it into view (from
/// -32000) — measured on a plain red 400 x 560 window with DWM's transitions off, like the panel's. The panel's own summon
/// adds its activation work on top of whichever it uses.
/// </summary>
static class RevealTest
{
    /// <summary>Where the red window appears.</summary>
    private const int Left = 700, Top = 200, Width = 400, Height = 560;

    /// <summary>The window procedure, kept alive for as long as the window exists.</summary>
    private static WndProc? procedure;

    /// <summary>Runs each way <paramref name="rounds"/> times and prints the time to the first changed pixel.</summary>
    /// <param name="rounds">Rounds per way.</param>
    /// <exception cref="InvalidOperationException">The red window could not be created.</exception>
    public static void Run(int rounds)
    {
        nint window = 0;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            procedure = (hwnd, message, wParam, lParam) => message == WM_DESTROY ? Quit() : DefWindowProc(hwnd, message, wParam, lParam);
            var windowClass = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(procedure),
                hInstance = GetModuleHandle(null),
                hbrBackground = CreateSolidBrush(0x000000FF),
                lpszClassName = "BetterClipboardRevealProbe",
            };
            RegisterClassEx(ref windowClass);
            window = CreateWindowEx(0x00000008 /* WS_EX_TOPMOST */, windowClass.lpszClassName!, "Reveal probe", WS_POPUP,
                Left, Top, Width, Height, 0, 0, windowClass.hInstance, 0);
            ready.Set();
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        })
        { IsBackground = true, Name = "Reveal" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (window == 0)
        {
            throw new InvalidOperationException("The red window could not be created.");
        }

        int disabled = 1;
        DwmSetWindowAttribute(window, 3 /* DWMWA_TRANSITIONS_FORCEDISABLED */, ref disabled, sizeof(int));
        var area = new RECT { Left = Left, Top = Top, Right = Left + Width, Bottom = Top + Height };
        using var sampler = new RowSampler(area, [0.5]);
        var ways = new (string Name, Action Conceal, Action Reveal)[]
        {
            ("show (SW_HIDE, then SW_SHOWNA)", () => ShowWindow(window, 0), () => ShowWindow(window, 8)),
            ("uncloak (DWMWA_CLOAK)", () => Cloak(window, true), () => Cloak(window, false)),
            ("move into view (from -32000)", () => Move(window, -32000, -32000), () => Move(window, Left, Top)),
        };

        foreach (var (name, conceal, reveal) in ways)
        {
            // Start from a shown, uncloaked window in place, then conceal it the way under test.
            Cloak(window, false);
            Move(window, Left, Top);
            ShowWindow(window, 8);
            var times = new List<double>();
            for (int round = 0; round < rounds; round++)
            {
                conceal();
                Thread.Sleep(400);
                var baseline = sampler.Sample();
                long start = Stopwatch.GetTimestamp();
                reveal();
                while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 1000)
                {
                    if (!sampler.Sample().SequenceEqual(baseline))
                    {
                        times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                        break;
                    }
                }
            }

            Console.WriteLine($"{name,-34} {Report.Summary(times)}");
        }

        PostMessage(window, WM_CLOSE, 0, 0);
        thread.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>Ends the red window's message loop.</summary>
    /// <returns>0.</returns>
    private static nint Quit()
    {
        PostQuitMessage(0);
        return 0;
    }

    /// <summary>Cloaks or uncloaks the red window.</summary>
    /// <param name="window">The window.</param>
    /// <param name="cloaked">Cloak or uncloak.</param>
    private static void Cloak(nint window, bool cloaked)
    {
        int value = cloaked ? 1 : 0;
        DwmSetWindowAttribute(window, 13 /* DWMWA_CLOAK */, ref value, sizeof(int));
    }

    /// <summary>Moves the red window without activating it or changing its size or z-order.</summary>
    /// <param name="window">The window.</param>
    /// <param name="x">New left edge.</param>
    /// <param name="y">New top edge.</param>
    private static void Move(nint window, int x, int y) => SetWindowPos(window, 0, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010 /* NOSIZE | NOZORDER | NOACTIVATE */);
}

/// <summary>Waiting on conditions instead of fixed sleeps.</summary>
static class Polling
{
    /// <summary>Polls <paramref name="probe"/> every millisecond until it returns non-zero or <paramref name="timeout"/> passes.</summary>
    /// <param name="probe">The condition; non-zero ends the wait.</param>
    /// <param name="timeout">The longest wait.</param>
    /// <returns>The probe's last result (0 on a timeout).</returns>
    public static nint Wait(Func<nint> probe, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        nint result;
        while ((result = probe()) == 0 && watch.Elapsed < timeout)
        {
            Thread.Sleep(1);
        }

        return result;
    }
}

/// <summary>Questions about the foreground window.</summary>
static class Foreground
{
    /// <summary>The foreground window when it belongs to BetterClipboard (the panel, during a summon), else 0.</summary>
    /// <param name="appProcesses">BetterClipboard's process ids.</param>
    /// <returns>The window or 0.</returns>
    public static nint Panel(HashSet<uint> appProcesses)
    {
        nint hwnd = GetForegroundWindow();
        GetWindowThreadProcessId(hwnd, out uint pid);
        return hwnd != 0 && appProcesses.Contains(pid) ? hwnd : 0;
    }

    /// <summary>The window's visible bounds (DWM's extended frame: without the invisible resize borders).</summary>
    /// <param name="hwnd">The window.</param>
    /// <returns>Screen rectangle in physical pixels.</returns>
    public static RECT FrameBounds(nint hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, 9, out RECT bounds, Marshal.SizeOf<RECT>()) != 0)
        {
            GetWindowRect(hwnd, out bounds);
        }

        return bounds;
    }
}

/// <summary>Injected keyboard input.</summary>
static class Keys
{
    /// <summary>Win+V: Win down, V down, V up, Win up, in one SendInput call (no gap a scheduler could widen).</summary>
    public static void WinV() => Send(
        Key(VK_LWIN, down: true, extended: true),
        Key(VK_V, down: true),
        Key(VK_V, down: false),
        Key(VK_LWIN, down: false, extended: true));

    /// <summary>Presses and releases one key.</summary>
    /// <param name="vk">Virtual key.</param>
    public static void Tap(ushort vk) => Send(Key(vk, down: true), Key(vk, down: false));

    /// <summary>One keyboard event.</summary>
    /// <param name="vk">Virtual key.</param>
    /// <param name="down">Key down, else key up.</param>
    /// <param name="extended">An extended key (the Windows keys are).</param>
    /// <returns>The event.</returns>
    private static INPUT Key(ushort vk, bool down, bool extended = false) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT
        {
            wVk = vk,
            wScan = (ushort)MapVirtualKey(vk, 0),
            dwFlags = (down ? 0u : KEYEVENTF_KEYUP) | (extended ? KEYEVENTF_EXTENDEDKEY : 0u),
        },
    };

    /// <summary>Injects the events and reports a short count (UIPI drops them silently, which this cannot see).</summary>
    /// <param name="inputs">The events.</param>
    public static void Send(params INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            Console.Error.WriteLine($"SendInput injected {sent} of {inputs.Length} events (error {Marshal.GetLastWin32Error()}).");
        }
    }
}

/// <summary>Injected mouse input.</summary>
static class Mouse
{
    /// <summary>Moves the cursor to a screen point and clicks the left button there.</summary>
    /// <param name="x">Screen x in physical pixels.</param>
    /// <param name="y">Screen y in physical pixels.</param>
    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        Keys.Send(
            new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } },
            new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } });
    }
}

/// <summary>UI Automation reads of the panel.</summary>
static class Uia
{
    /// <summary>The panel's search box text (its only edit control while no popup is open), or <see langword="null"/>.</summary>
    /// <param name="panel">The panel's window.</param>
    /// <returns>The text, or <see langword="null"/> when it could not be read (the reason is printed).</returns>
    public static string? SearchText(nint panel)
    {
        try
        {
            var edit = AutomationElement.FromHandle(panel).FindFirst(
                TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            return edit?.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern) == true ? ((ValuePattern)pattern).Current.Value : null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"UI Automation could not read the search box: {ex.Message}");
            return null;
        }
    }
}

/// <summary>Windows' "Animation effects" switches.</summary>
static class Animations
{
    /// <summary>Reads the client-area animation switch and the minimize/maximize animation.</summary>
    /// <returns>Both values.</returns>
    public static (bool ClientArea, bool MinMax) Read()
    {
        int clientArea = 0;
        SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref clientArea, 0);
        var info = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>() };
        SystemParametersInfo(SPI_GETANIMATION, info.cbSize, ref info, 0);
        return (clientArea != 0, info.iMinAnimate != 0);
    }

    /// <summary>Sets both switches for the session and tells running apps (as the Settings app does).</summary>
    /// <param name="clientArea">The client-area animation switch.</param>
    /// <param name="minMax">The minimize/maximize animation.</param>
    public static void Write(bool clientArea, bool minMax)
    {
        SystemParametersInfo(SPI_SETCLIENTAREAANIMATION, 0, clientArea ? 1 : 0, SPIF_SENDCHANGE);
        var info = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>(), iMinAnimate = minMax ? 1 : 0 };
        SystemParametersInfo(SPI_SETANIMATION, info.cbSize, ref info, SPIF_SENDCHANGE);
    }
}

/// <summary>Win32 imports and constants the probe uses.</summary>
static class Native
{
    /// <summary>Message ids.</summary>
    public const uint WM_DESTROY = 0x0002, WM_SETFOCUS = 0x0007, WM_KILLFOCUS = 0x0008, WM_CLOSE = 0x0010, WM_CHAR = 0x0102;

    /// <summary>Window styles.</summary>
    public const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;

    /// <summary>Virtual keys.</summary>
    public const ushort VK_LWIN = 0x5B, VK_V = 0x56, VK_Q = 0x51, VK_ESCAPE = 0x1B;

    /// <summary>SendInput constants.</summary>
    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1, KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2,
        MOUSEEVENTF_LEFTDOWN = 2, MOUSEEVENTF_LEFTUP = 4;

    /// <summary>SystemParametersInfo actions and flags.</summary>
    public const uint SPI_GETWORKAREA = 0x0030, SPI_GETANIMATION = 0x0048, SPI_SETANIMATION = 0x0049,
        SPI_GETCLIENTAREAANIMATION = 0x1042, SPI_SETCLIENTAREAANIMATION = 0x1043, SPIF_SENDCHANGE = 2;

    /// <summary>BitBlt's plain copy.</summary>
    public const uint SRCCOPY = 0x00CC0020;

    /// <summary>A window procedure.</summary>
    /// <param name="hwnd">Window.</param>
    /// <param name="message">Message.</param>
    /// <param name="wParam">First parameter.</param>
    /// <param name="lParam">Second parameter.</param>
    /// <returns>The result.</returns>
    public delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>A rectangle in screen pixels.</summary>
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
        /// <summary>Fields of MSG.</summary>
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

    /// <summary>A keyboard event for SendInput.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        /// <summary>Key codes.</summary>
        public ushort wVk, wScan;

        /// <summary>Flags and time.</summary>
        public uint dwFlags, time;

        /// <summary>Extra information.</summary>
        public nint dwExtraInfo;
    }

    /// <summary>A mouse event for SendInput.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        /// <summary>Movement.</summary>
        public int dx, dy;

        /// <summary>Data, flags and time.</summary>
        public uint mouseData, dwFlags, time;

        /// <summary>Extra information.</summary>
        public nint dwExtraInfo;
    }

    /// <summary>SendInput's event: 40 bytes on x64 (type, padding, a 32-byte union).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct INPUT
    {
        /// <summary>INPUT_MOUSE or INPUT_KEYBOARD.</summary>
        [FieldOffset(0)] public uint type;

        /// <summary>The keyboard event.</summary>
        [FieldOffset(8)] public KEYBDINPUT ki;

        /// <summary>The mouse event.</summary>
        [FieldOffset(8)] public MOUSEINPUT mi;
    }

    /// <summary>A DIB header.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        /// <summary>Size.</summary>
        public uint biSize;

        /// <summary>Dimensions.</summary>
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

    /// <summary>A thread's GUI state: its focus, active window and caret.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        /// <summary>Size of the structure, and flags.</summary>
        public uint cbSize, flags;

        /// <summary>Active, focus, capture, menu owner, move/size and caret windows.</summary>
        public nint hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;

        /// <summary>The caret's rectangle in its window's client coordinates.</summary>
        public RECT rcCaret;
    }

    /// <summary>The minimize/maximize animation setting.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ANIMATIONINFO
    {
        /// <summary>Size of the structure.</summary>
        public uint cbSize;

        /// <summary>Non-zero when the animation is on.</summary>
        public int iMinAnimate;
    }

    /// <summary>Sets the process's DPI awareness; -4 = per-monitor v2, so every coordinate is in physical pixels.</summary>
    /// <param name="value">A DPI_AWARENESS_CONTEXT value.</param>
    /// <returns><see langword="true"/> when set (false when already set, e.g. by a manifest).</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetProcessDpiAwarenessContext(nint value);

    /// <summary>Injects input events into the session's input stream (UIPI drops them silently for higher-level windows).</summary>
    /// <param name="count">Number of events.</param>
    /// <param name="inputs">The events.</param>
    /// <param name="size">Size of one <see cref="INPUT"/> (40 on x64).</param>
    /// <returns>How many were injected.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, INPUT[] inputs, int size);

    /// <summary>Translates a virtual key to its scan code (map type 0).</summary>
    /// <param name="code">The virtual key.</param>
    /// <param name="mapType">0 = virtual key to scan code.</param>
    /// <returns>The scan code, or 0.</returns>
    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint code, uint mapType);

    /// <summary>The window that receives keyboard input now.</summary>
    /// <returns>The window, or 0 while activation is changing.</returns>
    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    /// <summary>Asks Windows to activate a window (refused unless this process may take the foreground).</summary>
    /// <param name="hwnd">The window.</param>
    /// <returns><see langword="true"/> when it was brought forward.</returns>
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(nint hwnd);

    /// <summary>The thread and process that own a window.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="processId">Receives the process id.</param>
    /// <returns>The thread id (0 for an invalid window).</returns>
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    /// <summary>A GUI thread's state (the caret window tells whether the stage holds its caret yet).</summary>
    /// <param name="thread">The thread.</param>
    /// <param name="info">Receives the state; its <c>cbSize</c> must be set.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    public static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);

    /// <summary>A window's outer rectangle, invisible resize borders included.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="rect">Receives the rectangle.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(nint hwnd, out RECT rect);

    /// <summary>Moves the mouse cursor.</summary>
    /// <param name="x">Screen x.</param>
    /// <param name="y">Screen y.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    /// <summary>Registers a window class.</summary>
    /// <param name="windowClass">The class.</param>
    /// <returns>Its atom, or 0 on failure.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    /// <summary>Creates a window.</summary>
    /// <param name="exStyle">Extended styles.</param>
    /// <param name="className">A registered class.</param>
    /// <param name="title">The title.</param>
    /// <param name="style">Styles.</param>
    /// <param name="x">Left edge.</param>
    /// <param name="y">Top edge.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="parent">Parent or owner (0 = none).</param>
    /// <param name="menu">Menu (0 = none).</param>
    /// <param name="instance">Module of the class.</param>
    /// <param name="param">Creation data (0 = none).</param>
    /// <returns>The window, or 0 on failure.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    /// <summary>Windows' default handling of a message.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="message">The message.</param>
    /// <param name="wParam">First parameter.</param>
    /// <param name="lParam">Second parameter.</param>
    /// <returns>The default result.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>Waits for the next message of this thread.</summary>
    /// <param name="message">Receives the message.</param>
    /// <param name="hwnd">0 = any window of the thread.</param>
    /// <param name="min">Lowest message id (0 = no filter).</param>
    /// <param name="max">Highest message id (0 = no filter).</param>
    /// <returns>&gt; 0 for a message, 0 for WM_QUIT, -1 on error.</returns>
    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG message, nint hwnd, uint min, uint max);

    /// <summary>Turns key messages into WM_CHAR (without it the stage would see no characters).</summary>
    /// <param name="message">The message.</param>
    /// <returns><see langword="true"/> when a character message was posted.</returns>
    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG message);

    /// <summary>Sends a message to its window procedure.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The procedure's result.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint DispatchMessage(ref MSG message);

    /// <summary>Queues a message for a window's thread.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="message">The message.</param>
    /// <param name="wParam">First parameter.</param>
    /// <param name="lParam">Second parameter.</param>
    /// <returns><see langword="true"/> when queued.</returns>
    [DllImport("user32.dll")]
    public static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>Ends this thread's message loop (GetMessage returns 0).</summary>
    /// <param name="exitCode">The exit code carried by WM_QUIT.</param>
    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int exitCode);

    /// <summary>Loads a system cursor.</summary>
    /// <param name="instance">0 for a system cursor.</param>
    /// <param name="name">The cursor id (32512 = the arrow).</param>
    /// <returns>The cursor.</returns>
    [DllImport("user32.dll")]
    public static extern nint LoadCursor(nint instance, int name);

    /// <summary>Gives this thread a caret (one per thread; reported by GetGUIThreadInfo to the panel's placement).</summary>
    /// <param name="hwnd">The window that owns it.</param>
    /// <param name="bitmap">0 = a solid caret.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    public static extern bool CreateCaret(nint hwnd, nint bitmap, int width, int height);

    /// <summary>Moves this thread's caret.</summary>
    /// <param name="x">Client x.</param>
    /// <param name="y">Client y.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    public static extern bool SetCaretPos(int x, int y);

    /// <summary>Shows this thread's caret.</summary>
    /// <param name="hwnd">The window that owns it.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    public static extern bool ShowCaret(nint hwnd);

    /// <summary>Destroys this thread's caret.</summary>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    public static extern bool DestroyCaret();

    /// <summary>Reads a rectangle setting (the work area).</summary>
    /// <param name="action">The SPI_ action.</param>
    /// <param name="param">Action-specific.</param>
    /// <param name="value">Receives the rectangle.</param>
    /// <param name="flags">Update flags.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    public static extern bool SystemParametersInfo(uint action, uint param, ref RECT value, uint flags);

    /// <summary>Reads a BOOL setting (the client-area animation).</summary>
    /// <param name="action">The SPI_ action.</param>
    /// <param name="param">Action-specific.</param>
    /// <param name="value">Receives the value.</param>
    /// <param name="flags">Update flags.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    public static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint flags);

    /// <summary>Writes a setting passed by value (the client-area animation).</summary>
    /// <param name="action">The SPI_ action.</param>
    /// <param name="param">Action-specific.</param>
    /// <param name="value">The value.</param>
    /// <param name="flags">Update flags (SPIF_SENDCHANGE tells running apps).</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    public static extern bool SystemParametersInfo(uint action, uint param, nint value, uint flags);

    /// <summary>Reads or writes the minimize/maximize animation.</summary>
    /// <param name="action">SPI_GETANIMATION or SPI_SETANIMATION.</param>
    /// <param name="param">The structure's size.</param>
    /// <param name="value">The setting.</param>
    /// <param name="flags">Update flags.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    public static extern bool SystemParametersInfo(uint action, uint param, ref ANIMATIONINFO value, uint flags);

    /// <summary>A device context; for window 0, the whole screen as DWM last composed it.</summary>
    /// <param name="hwnd">0 for the screen.</param>
    /// <returns>The DC (release it with <see cref="ReleaseDC"/>).</returns>
    [DllImport("user32.dll")]
    public static extern nint GetDC(nint hwnd);

    /// <summary>Releases a DC from <see cref="GetDC"/>.</summary>
    /// <param name="hwnd">The window it came from.</param>
    /// <param name="dc">The DC.</param>
    /// <returns>1 when released.</returns>
    [DllImport("user32.dll")]
    public static extern int ReleaseDC(nint hwnd, nint dc);

    /// <summary>A solid brush (the stage's magenta background).</summary>
    /// <param name="color">0x00BBGGRR.</param>
    /// <returns>The brush.</returns>
    [DllImport("gdi32.dll")]
    public static extern nint CreateSolidBrush(uint color);

    /// <summary>A memory DC compatible with another.</summary>
    /// <param name="dc">The reference DC.</param>
    /// <returns>The memory DC.</returns>
    [DllImport("gdi32.dll")]
    public static extern nint CreateCompatibleDC(nint dc);

    /// <summary>Deletes a memory DC.</summary>
    /// <param name="dc">The DC.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(nint dc);

    /// <summary>A bitmap whose pixels this process can read directly.</summary>
    /// <param name="dc">A reference DC.</param>
    /// <param name="header">Size and format (32 bpp, top-down).</param>
    /// <param name="usage">0 = RGB colors.</param>
    /// <param name="bits">Receives the pixel memory.</param>
    /// <param name="section">0 = memory of its own.</param>
    /// <param name="offset">0.</param>
    /// <returns>The bitmap, or 0.</returns>
    [DllImport("gdi32.dll")]
    public static extern nint CreateDIBSection(nint dc, ref BITMAPINFOHEADER header, uint usage, out nint bits, nint section, uint offset);

    /// <summary>Selects a GDI object into a DC.</summary>
    /// <param name="dc">The DC.</param>
    /// <param name="gdiObject">The object.</param>
    /// <returns>The object it replaced.</returns>
    [DllImport("gdi32.dll")]
    public static extern nint SelectObject(nint dc, nint gdiObject);

    /// <summary>Deletes a GDI object.</summary>
    /// <param name="gdiObject">The object.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(nint gdiObject);

    /// <summary>Copies pixels between DCs (here: from the screen into the DIB section).</summary>
    /// <param name="destination">Target DC.</param>
    /// <param name="x">Target x.</param>
    /// <param name="y">Target y.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="source">Source DC.</param>
    /// <param name="sourceX">Source x.</param>
    /// <param name="sourceY">Source y.</param>
    /// <param name="operation">SRCCOPY.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);

    /// <summary>Completes queued GDI work, so the DIB section's memory holds the copied pixels.</summary>
    /// <returns><see langword="true"/> when everything was done.</returns>
    [DllImport("gdi32.dll")]
    public static extern bool GdiFlush();

    /// <summary>Reads a DWM attribute of a window (9 = the visible frame bounds).</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="attribute">The attribute.</param>
    /// <param name="value">Receives the rectangle.</param>
    /// <param name="size">Its size.</param>
    /// <returns>0 (S_OK) on success.</returns>
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out RECT value, int size);

    /// <summary>Shows or hides a window (0 = SW_HIDE, 8 = SW_SHOWNA: show without activating).</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="command">The SW_ command.</param>
    /// <returns>Whether it was visible before.</returns>
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(nint hwnd, int command);

    /// <summary>Moves, sizes or reorders a window.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="insertAfter">The z-order neighbour (ignored with SWP_NOZORDER).</param>
    /// <param name="x">Left edge.</param>
    /// <param name="y">Top edge.</param>
    /// <param name="width">Width (ignored with SWP_NOSIZE).</param>
    /// <param name="height">Height (ignored with SWP_NOSIZE).</param>
    /// <param name="flags">SWP_ flags.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    /// <summary>Sets a DWM attribute of a window of this process (cloak, transitions).</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="attribute">The attribute.</param>
    /// <param name="value">Its value.</param>
    /// <param name="size">The value's size.</param>
    /// <returns>0 (S_OK) on success.</returns>
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, int size);

    /// <summary>The handle of a loaded module.</summary>
    /// <param name="name"><see langword="null"/> = this executable.</param>
    /// <returns>The module.</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandle(string? name);
}
