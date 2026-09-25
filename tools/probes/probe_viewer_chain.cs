#:property PublishAot=false
#:property AllowUnsafeBlocks=true
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static Native;

// Probe: is the clipboard viewer chain (WM_DRAWCLIPBOARD) synchronous with the producer's CloseClipboard,
// and does it observe every state of a rapid burst where WM_CLIPBOARDUPDATE misses some?
// Runs entirely inside a private, anonymous window station (its own clipboard), so the user's clipboard and
// Win+V history are untouched. Threads are MTA on purpose: an STA thread already owns COM's hidden window,
// which makes SetThreadDesktop fail with ERROR_BUSY. Usage: dotnet run tools/probes/probe_viewer_chain.cs
// Result on Windows 11 26200 (2026-09-25): CloseClipboard 0.01 ms even with a 150 ms viewer (asynchronous);
// 40-copy zero-delay burst -> 40 notifications, 2 distinct states, for both mechanisms. See CLAUDE.md §1.9.

var station = CreateWindowStationW(null, 0, 0x37F, 0);
if (station == 0) throw new Exception("CreateWindowStation failed " + Marshal.GetLastPInvokeError());
if (!SetProcessWindowStation(station)) throw new Exception("SetProcessWindowStation failed " + Marshal.GetLastPInvokeError());
var desktop = CreateDesktopW("ProbeDesktop", 0, 0, 0, 0x10000000, 0);
if (desktop == 0) throw new Exception("CreateDesktop failed " + Marshal.GetLastPInvokeError());
Console.WriteLine("isolated window station + desktop created");

const int N = 40;
var viewerSeen = new ConcurrentQueue<(uint Seq, string? Text)>();
var listenerSeen = new ConcurrentQueue<(uint Seq, string? Text)>();
var viewerDelayMs = 0;
nint viewerHwnd = 0, listenerHwnd = 0, producerHwnd = 0;
nint nextViewer = 0;
bool writingOnViewerThread = false;
bool reentrantDuringOwnClose = false;
var ready = new CountdownEvent(3);
var producerGo = new ManualResetEventSlim();
var renderRequests = 0;

StartWindowThread("viewer", hwnd =>
{
    viewerHwnd = hwnd;
    nextViewer = SetClipboardViewer(hwnd);
    Console.WriteLine($"viewer joined chain (next=0x{nextViewer:X}, lastError={Marshal.GetLastPInvokeError()})");
}, (hwnd, msg, w, l) =>
{
    if (msg == WM_DRAWCLIPBOARD)
    {
        if (writingOnViewerThread) reentrantDuringOwnClose = true;
        var seq = GetClipboardSequenceNumber();
        viewerSeen.Enqueue((seq, ReadText(hwnd)));
        if (viewerDelayMs > 0) Thread.Sleep(viewerDelayMs);
        if (nextViewer != 0) SendMessageW(nextViewer, msg, w, l);
        return 0;
    }
    if (msg == WM_CHANGECBCHAIN)
    {
        if (w == nextViewer) nextViewer = l; else if (nextViewer != 0) SendMessageW(nextViewer, msg, w, l);
        return 0;
    }
    if (msg == WM_APP)
    {
        // Our own write from the viewer thread: is WM_DRAWCLIPBOARD re-entered inside CloseClipboard?
        writingOnViewerThread = true;
        WriteText(hwnd, "own-write", delayed: false);
        writingOnViewerThread = false;
        return 0;
    }
    return null;
});

StartWindowThread("listener", hwnd =>
{
    listenerHwnd = hwnd;
    if (!AddClipboardFormatListener(hwnd)) throw new Exception("AddClipboardFormatListener failed");
}, (hwnd, msg, w, l) =>
{
    if (msg == WM_CLIPBOARDUPDATE)
    {
        listenerSeen.Enqueue((GetClipboardSequenceNumber(), ReadText(hwnd)));
        return 0;
    }
    return null;
});

StartWindowThread("producer", hwnd => producerHwnd = hwnd, (hwnd, msg, w, l) =>
{
    if (msg == WM_RENDERFORMAT)
    {
        Interlocked.Increment(ref renderRequests);
        SetClipboardData(CF_UNICODETEXT, Global("delayed-rendered"));
        return 0;
    }
    return null;
});

ready.Wait();
Thread.Sleep(200);
viewerSeen.Clear(); listenerSeen.Clear();

// Burst 1: N back-to-back copies with no viewer delay.
var closeTimes = new List<double>();
RunOnProducer(() =>
{
    for (int i = 0; i < N; i++) closeTimes.Add(WriteText(producerHwnd, $"burst-{i}", delayed: false));
});
Thread.Sleep(500);
Report("burst (no delay)", closeTimes);

// Burst 2: viewer sleeps 150 ms per notification -> does CloseClipboard block for it?
viewerSeen.Clear(); listenerSeen.Clear(); closeTimes.Clear();
viewerDelayMs = 150;
RunOnProducer(() =>
{
    for (int i = 0; i < 3; i++) closeTimes.Add(WriteText(producerHwnd, $"slow-{i}", delayed: false));
});
viewerDelayMs = 0;
Thread.Sleep(500);
Report("slow viewer (150 ms)", closeTimes);

// Delayed rendering: producer promises CF_UNICODETEXT and renders on demand while blocked in CloseClipboard.
viewerSeen.Clear(); closeTimes.Clear();
RunOnProducer(() => closeTimes.Add(WriteText(producerHwnd, null, delayed: true)));
Thread.Sleep(300);
Console.WriteLine($"delayed rendering: viewer read '{viewerSeen.LastOrDefault().Text}', WM_RENDERFORMAT served {renderRequests}x, close took {closeTimes[0]:0.0} ms");

// Own write on the viewer thread.
SendMessageW(viewerHwnd, WM_APP, 0, 0);
Console.WriteLine($"own write: WM_DRAWCLIPBOARD re-entered inside our CloseClipboard = {reentrantDuringOwnClose}");

ChangeClipboardChainOnViewer();
Console.WriteLine("done");
Environment.Exit(0);

void Report(string label, List<double> times)
{
    var viewerDistinct = viewerSeen.Select(v => v.Text).Where(t => t is not null).Distinct().Count();
    var listenerDistinct = listenerSeen.Select(v => v.Text).Where(t => t is not null).Distinct().Count();
    Console.WriteLine($"{label}: writes={times.Count} viewer notifications={viewerSeen.Count} distinct={viewerDistinct} | " +
                      $"listener notifications={listenerSeen.Count} distinct={listenerDistinct} | CloseClipboard avg={times.Average():0.00} ms max={times.Max():0.00} ms");
}

void RunOnProducer(Action action)
{
    var done = new ManualResetEventSlim();
    Exception? error = null;
    producerWork.Enqueue(() => { try { action(); } catch (Exception ex) { error = ex; } finally { done.Set(); } });
    PostMessageW(producerHwnd, WM_APP + 1, 0, 0);
    done.Wait();
    if (error is not null) throw error;
}

void ChangeClipboardChainOnViewer() => SendMessageW(viewerHwnd, WM_APP + 2, 0, 0);

double WriteText(nint owner, string? text, bool delayed)
{
    while (!OpenClipboard(owner)) Thread.Sleep(1);
    EmptyClipboard();
    SetClipboardData(CF_UNICODETEXT, delayed ? 0 : Global(text!));
    var sw = Stopwatch.StartNew();
    CloseClipboard();
    return sw.Elapsed.TotalMilliseconds;
}

static string? ReadText(nint hwnd)
{
    for (int i = 0; i < 50 && !OpenClipboard(hwnd); i++) Thread.Sleep(1);
    try
    {
        var h = GetClipboardData(CF_UNICODETEXT);
        if (h == 0) return null;
        var p = GlobalLock(h);
        try { return Marshal.PtrToStringUni(p); } finally { GlobalUnlock(h); }
    }
    finally { CloseClipboard(); }
}

static nint Global(string text)
{
    var bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
    var h = GlobalAlloc(2, (nuint)bytes.Length);
    var p = GlobalLock(h);
    Marshal.Copy(bytes, 0, p, bytes.Length);
    GlobalUnlock(h);
    return h;
}

void StartWindowThread(string name, Action<nint> init, Func<nint, uint, nint, nint, nint?> handler)
{
    var thread = new Thread(() =>
    {
        if (!SetThreadDesktop(desktop)) throw new Exception("SetThreadDesktop failed " + Marshal.GetLastPInvokeError());
        WndProc proc = (h, m, w, l) =>
        {
            if (m == WM_APP + 1) { while (producerWork.TryDequeue(out var work)) work(); return 0; }
            if (m == WM_APP + 2) { ChangeClipboardChain(h, nextViewer); return 0; }
            return handler(h, m, w, l) ?? DefWindowProcW(h, m, w, l);
        };
        procs.Add(proc);
        var cls = new WNDCLASSEXW { cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc), lpszClassName = "BCProbe_" + name, hInstance = GetModuleHandleW(null) };
        if (RegisterClassExW(ref cls) == 0) throw new Exception("RegisterClassEx failed " + Marshal.GetLastPInvokeError());
        var hwnd = CreateWindowExW(0, cls.lpszClassName, name, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, cls.hInstance, 0);
        if (hwnd == 0) throw new Exception("CreateWindowEx failed " + Marshal.GetLastPInvokeError());
        init(hwnd);
        ready.Signal();
        while (GetMessageW(out var msg, 0, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
    }) { IsBackground = true, Name = name };
    thread.Start();
}

partial class Program
{
    static readonly ConcurrentQueue<Action> producerWork = new();
    static readonly List<WndProc> procs = new();
}

delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct WNDCLASSEXW
{
    public uint cbSize, style; public nint lpfnWndProc; public int cbClsExtra, cbWndExtra;
    public nint hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public nint hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; }

static class Native
{
    public const uint WM_DRAWCLIPBOARD = 0x0308, WM_CHANGECBCHAIN = 0x030D, WM_CLIPBOARDUPDATE = 0x031D, WM_RENDERFORMAT = 0x0305, WM_APP = 0x8000, CF_UNICODETEXT = 13;
    public static readonly nint HWND_MESSAGE = -3;
    [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode)] public static extern nint CreateWindowStationW(string? name, uint flags, uint access, nint sa);
    [DllImport("user32", SetLastError = true)] public static extern bool SetProcessWindowStation(nint winsta);
    [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode)] public static extern nint CreateDesktopW(string name, nint device, nint devmode, uint flags, uint access, nint sa);
    [DllImport("user32", SetLastError = true)] public static extern bool SetThreadDesktop(nint desktop);
    [DllImport("user32", SetLastError = true)] public static extern nint SetClipboardViewer(nint hwnd);
    [DllImport("user32", SetLastError = true)] public static extern bool ChangeClipboardChain(nint remove, nint next);
    [DllImport("user32", SetLastError = true)] public static extern bool AddClipboardFormatListener(nint hwnd);
    [DllImport("user32")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32", SetLastError = true)] public static extern bool OpenClipboard(nint hwnd);
    [DllImport("user32", SetLastError = true)] public static extern bool CloseClipboard();
    [DllImport("user32", SetLastError = true)] public static extern bool EmptyClipboard();
    [DllImport("user32", SetLastError = true)] public static extern nint SetClipboardData(uint format, nint mem);
    [DllImport("user32", SetLastError = true)] public static extern nint GetClipboardData(uint format);
    [DllImport("kernel32", SetLastError = true)] public static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32", SetLastError = true)] public static extern nint GlobalLock(nint mem);
    [DllImport("kernel32", SetLastError = true)] public static extern bool GlobalUnlock(nint mem);
    [DllImport("user32", SetLastError = true)] public static extern nint SendMessageW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32", SetLastError = true)] public static extern bool PostMessageW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32", SetLastError = true)] public static extern nint DefWindowProcW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32", SetLastError = true)] public static extern ushort RegisterClassExW(ref WNDCLASSEXW cls);
    [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode)] public static extern nint CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);
    [DllImport("user32")] public static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32")] public static extern nint DispatchMessageW(ref MSG msg);
}
