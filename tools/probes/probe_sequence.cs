#:property PublishAot=false
using System.Runtime.InteropServices;

// Probe (isolated window station, never touches the user's clipboard): which clipboard calls bump
// GetClipboardSequenceNumber, and does a delayed render (WM_RENDERFORMAT -> SetClipboardData) bump it or
// raise WM_CLIPBOARDUPDATE? Usage: dotnet run tools/probes/probe_sequence.cs
// Result on Windows 11 26200: Empty +1, each Set +1, Close +2, staged add notifies once, delayed render
// neither bumps nor notifies. See CLAUDE.md §1.9.
var station = CreateWindowStationW(null, 0, 0x37F, 0);
SetProcessWindowStation(station);
var desktop = CreateDesktopW("SeqDesktop", 0, 0, 0, 0x10000000, 0);
SetThreadDesktop(desktop);

int notifications = 0;
int renders = 0;
WndProc proc = (h, m, w, l) =>
{
    if (m == 0x031D) { notifications++; return 0; }
    if (m == 0x0305) { renders++; Console.WriteLine($"  in WM_RENDERFORMAT before Set: {Seq()}"); SetClipboardData(13, Global("rendered")); Console.WriteLine($"  in WM_RENDERFORMAT after Set:  {Seq()}"); return 0; }
    return DefWindowProcW(h, m, w, l);
};
var cls = new WNDCLASSEXW { cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc), lpszClassName = "BCSeq", hInstance = GetModuleHandleW(null) };
RegisterClassExW(ref cls);
var owner = CreateWindowExW(0, "BCSeq", "owner", 0, 0, 0, 0, 0, -3, 0, cls.hInstance, 0);
var listenerThreadReady = new ManualResetEventSlim();

AddClipboardFormatListener(owner);
Console.WriteLine($"start: {Seq()}");
OpenClipboard(owner); Console.WriteLine($"after Open:  {Seq()}");
EmptyClipboard(); Console.WriteLine($"after Empty: {Seq()}");
SetClipboardData(13, Global("one")); Console.WriteLine($"after Set#1: {Seq()}");
SetClipboardData(1, GlobalA("one")); Console.WriteLine($"after Set#2: {Seq()}");
CloseClipboard(); Console.WriteLine($"after Close: {Seq()}");
Pump(); Console.WriteLine($"notifications after write: {notifications}");

// Same-owner staged add (no EmptyClipboard): allowed? bumps? notifies?
notifications = 0;
OpenClipboard(owner);
var fmt = RegisterClipboardFormatW("HTML Format");
Console.WriteLine($"staged add: SetClipboardData ok={SetClipboardData(fmt, GlobalA("<b>one</b>")) != 0} seq={Seq()}");
CloseClipboard(); Pump(); Console.WriteLine($"staged add: after Close {Seq()}, notifications={notifications}");

// Delayed rendering, rendered by a reader on another thread (like our monitor reading a producer's promise).
notifications = 0;
OpenClipboard(owner); EmptyClipboard(); SetClipboardData(13, 0); CloseClipboard();
Pump();
Console.WriteLine($"delayed promise written: seq={Seq()} notifications={notifications}");
notifications = 0;
var reader = new Thread(() =>
{
    SetThreadDesktop(desktop);
    var rcls = new WNDCLASSEXW { cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(readerProc), lpszClassName = "BCSeqReader", hInstance = GetModuleHandleW(null) };
    RegisterClassExW(ref rcls);
    var rh = CreateWindowExW(0, "BCSeqReader", "reader", 0, 0, 0, 0, 0, -3, 0, rcls.hInstance, 0);
    OpenClipboard(rh);
    Console.WriteLine($"reader: open seq={Seq()}");
    var data = GetClipboardData(13);
    Console.WriteLine($"reader: after GetClipboardData (render) seq={Seq()} gotData={data != 0}");
    CloseClipboard();
    Console.WriteLine($"reader: after Close seq={Seq()}");
});
reader.Start();
while (reader.IsAlive) { Pump(); Thread.Sleep(1); }
Pump(); Thread.Sleep(50); Pump();
Console.WriteLine($"renders={renders} notifications caused by the render={notifications}");

uint Seq() => GetClipboardSequenceNumber();
void Pump() { while (PeekMessageW(out var msg, 0, 0, 0, 1)) { DispatchMessageW(ref msg); } }
static nint Global(string text) { var b = System.Text.Encoding.Unicode.GetBytes(text + "\0"); var h = GlobalAlloc(2, (nuint)b.Length); Marshal.Copy(b, 0, GlobalLock(h), b.Length); GlobalUnlock(h); return h; }
static nint GlobalA(string text) { var b = System.Text.Encoding.ASCII.GetBytes(text + "\0"); var h = GlobalAlloc(2, (nuint)b.Length); Marshal.Copy(b, 0, GlobalLock(h), b.Length); GlobalUnlock(h); return h; }

partial class Program
{
    static readonly WndProc readerProc = (h, m, w, l) => DefWindowProcW(h, m, w, l);
    [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode)] static extern nint CreateWindowStationW(string? name, uint flags, uint access, nint sa);
    [DllImport("user32")] static extern bool SetProcessWindowStation(nint s);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern nint CreateDesktopW(string name, nint d, nint dm, uint f, uint a, nint sa);
    [DllImport("user32")] static extern bool SetThreadDesktop(nint d);
    [DllImport("user32")] static extern bool AddClipboardFormatListener(nint h);
    [DllImport("user32")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32")] static extern bool OpenClipboard(nint h);
    [DllImport("user32")] static extern bool CloseClipboard();
    [DllImport("user32")] static extern bool EmptyClipboard();
    [DllImport("user32")] static extern nint SetClipboardData(uint f, nint m);
    [DllImport("user32")] static extern nint GetClipboardData(uint f);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormatW(string name);
    [DllImport("kernel32")] static extern nint GlobalAlloc(uint f, nuint b);
    [DllImport("kernel32")] static extern nint GlobalLock(nint m);
    [DllImport("kernel32")] static extern bool GlobalUnlock(nint m);
    [DllImport("user32")] static extern nint DefWindowProcW(nint h, uint m, nint w, nint l);
    [DllImport("user32")] static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern nint CreateWindowExW(uint e, string c, string n, uint s, int x, int y, int w, int h, nint p, nint m, nint i, nint a);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string? n);
    [DllImport("user32")] static extern bool PeekMessageW(out MSG m, nint h, uint a, uint b, uint r);
    [DllImport("user32")] static extern nint DispatchMessageW(ref MSG m);
}

delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct WNDCLASSEXW { public uint cbSize, style; public nint lpfnWndProc; public int cbClsExtra, cbWndExtra; public nint hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public nint hIconSm; }
[StructLayout(LayoutKind.Sequential)]
struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; }
