#:property PublishAot=false
// ─────────────────────────────────────────────────────────────────────────────────────────────────────
// Probe: what Windows does when a per-monitor-v2 window is moved by SetWindowPos onto a monitor with another
// scale, as BetterClipboard moves its panel at every summon (CLAUDE.md §2.38).
// The window is a cloaked, non-activating tool window: it is never visible and never takes the foreground, so the
// probe is safe on a PC in use. Prints geometry only. It needs two monitors with different scales.
// Run: dotnet run tools/probes/probe_dpi_move.cs [-- --apply]   (--apply: take the suggested rectangle, as WinUI does)
// Verified 2026-11-08 on monitors at 100 % and 125 %: WM_DPICHANGED arrives inside SetWindowPos, suggesting the asked
// rectangle scaled once more (asked 500x700 on the 125 % monitor, suggested 625x875; 400x560 back on 100 %, 320x448).
// tools/probes/dpi_move_lab shows that a WinUI window takes the suggestion, and that asking again fixes it.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────
using System.Runtime.InteropServices;

bool apply = args.Contains("--apply");
SetProcessDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2

var monitors = new List<(RECT Work, uint Dpi, bool Primary)>();
EnumDisplayMonitors(0, 0, (h, _, _, _) =>
{
    var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
    GetMonitorInfoW(h, ref info);
    GetDpiForMonitor(h, 0, out uint dpi, out _);
    monitors.Add((info.rcWork, dpi, (info.dwFlags & 1) != 0));
    return true;
}, 0);
foreach (var m in monitors) Console.WriteLine($"monitor work {Fmt(m.Work)} dpi {m.Dpi}{(m.Primary ? " primary" : "")}");

var primary = monitors.First(m => m.Primary);
var other = monitors.FirstOrDefault(m => m.Dpi != primary.Dpi);
if (other.Dpi == 0) { Console.WriteLine("no monitor with another DPI: nothing to probe"); return; }

var log = new List<string>();
bool inMove = false;
WndProc proc = (hwnd, msg, w, l) =>
{
    if (msg == 0x02E0) // WM_DPICHANGED
    {
        var suggested = Marshal.PtrToStructure<RECT>(l);
        log.Add($"  WM_DPICHANGED dpi {(uint)(w & 0xFFFF)} suggested {Fmt(suggested)} (during SetWindowPos: {inMove})");
        if (apply)
        {
            SetWindowPos(hwnd, 0, suggested.Left, suggested.Top, suggested.Right - suggested.Left, suggested.Bottom - suggested.Top, 0x0014);
        }
        return 0;
    }
    return DefWindowProcW(hwnd, msg, w, l);
};
var cls = new WNDCLASSEXW { cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc), hInstance = GetModuleHandleW(null), lpszClassName = "BC-TEST-DpiMoveProbe" };
RegisterClassExW(ref cls);
double s0 = primary.Dpi / 96.0;
int w0 = (int)Math.Round(400 * s0), h0 = (int)Math.Round(560 * s0);
nint hwnd = CreateWindowExW(0x00000080 | 0x08000000, "BC-TEST-DpiMoveProbe", "BC-TEST", unchecked((int)0x80000000), primary.Work.Left + 100, primary.Work.Top + 100, w0, h0, 0, 0, cls.hInstance, 0);
int cloak = 1;
DwmSetWindowAttribute(hwnd, 13, ref cloak, 4); // DWMWA_CLOAK before showing: never on screen
ShowWindow(hwnd, 8); // SW_SHOWNA
Console.WriteLine($"created at {Fmt(Rect(hwnd))}, window dpi {GetDpiForWindow(hwnd)}");

void MoveTo((RECT Work, uint Dpi, bool Primary) target, string label)
{
    double scale = target.Dpi / 96.0;
    int w = (int)Math.Round(400 * scale), h = (int)Math.Round(560 * scale);
    int x = target.Work.Left + ((target.Work.Right - target.Work.Left - w) / 2);
    int y = target.Work.Top + ((target.Work.Bottom - target.Work.Top - h) / 2);
    log.Clear();
    inMove = true;
    SetWindowPos(hwnd, 0, x, y, w, h, 0x0014); // SWP_NOZORDER | SWP_NOACTIVATE
    inMove = false;
    Pump();
    var got = Rect(hwnd);
    Console.WriteLine($"{label}: asked {x},{y} {w}x{h}; got {Fmt(got)} ({got.Right - got.Left}x{got.Bottom - got.Top}), window dpi {GetDpiForWindow(hwnd)}");
    foreach (var line in log) Console.WriteLine(line);
}

MoveTo(other, "to the other-DPI monitor");
MoveTo(primary, "back to the primary");
MoveTo(other, "to the other-DPI monitor again");
DestroyWindow(hwnd);

static void Pump()
{
    while (PeekMessageW(out var m, 0, 0, 0, 1)) { TranslateMessage(ref m); DispatchMessageW(ref m); }
}
static RECT Rect(nint hwnd) { GetWindowRect(hwnd, out var r); return r; }
static string Fmt(RECT r) => $"({r.Left},{r.Top})-({r.Right},{r.Bottom})";

delegate nint WndProc(nint hwnd, uint msg, nint w, nint l);
delegate bool MonitorEnum(nint h, nint hdc, nint rect, nint data);
[StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public uint cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
[StructLayout(LayoutKind.Sequential)] struct MSG { public nint hwnd; public uint message; public nint wParam; public nint lParam; public uint time; public int x, y; public uint priv; }
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WNDCLASSEXW { public uint cbSize, style; public nint lpfnWndProc; public int cbClsExtra, cbWndExtra; public nint hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public nint hIconSm; }
partial class Program
{
    [DllImport("user32")] public static extern bool SetProcessDpiAwarenessContext(nint value);
    [DllImport("user32")] public static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnum proc, nint data);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(nint h, ref MONITORINFO info);
    [DllImport("shcore")] public static extern int GetDpiForMonitor(nint h, int type, out uint x, out uint y);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassExW(ref WNDCLASSEXW cls);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint CreateWindowExW(int ex, string cls, string title, int style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32")] public static extern nint DefWindowProcW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32")] public static extern bool GetWindowRect(nint hwnd, out RECT r);
    [DllImport("user32")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32")] public static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32")] public static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32")] public static extern bool PeekMessageW(out MSG m, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32")] public static extern nint DispatchMessageW(ref MSG m);
    [DllImport("dwmapi")] public static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);
}
