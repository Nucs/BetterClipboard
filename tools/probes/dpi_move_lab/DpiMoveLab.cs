// ─────────────────────────────────────────────────────────────────────────────────────────────────────
// DPI move lab: what a WinUI 3 window does when the program moves it onto a monitor with another scale, as
// BetterClipboard does with its panel at every summon (CLAUDE.md §2.38).
//
//   DpiMoveLab.exe <log> [--fix]
//
// Windows answers such a move with WM_DPICHANGED inside the move itself, suggesting the asked rectangle scaled once more
// by the ratio of the two scales (tools/probes/probe_dpi_move.cs). The lab moves a window set up like the panel (an
// overlapped window without a title bar, resizable, always on top) from the main monitor to a monitor with another scale
// and back, with AppWindow.MoveAndResize, and logs the bounds it asked for and the bounds the window has right after the
// call and 300 ms later. --fix asks for the bounds again whenever they differ (BetterClipboard's PresentAsync since
// 2026-11-08), and logs whether that holds.
//
// The window is cloaked before it is shown and never activated, so nothing ever appears on screen and the foreground is
// never taken: the lab is safe to run on a PC in use. It needs two monitors with different scales; with one, it says so
// and exits. Build: dotnet build tools/probes/dpi_move_lab -c Release (self-contained).
// ─────────────────────────────────────────────────────────────────────────────────────────────────────
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace DpiMoveLab;

/// <summary>Entry point: WinUI's start-up with a custom Main, like BetterClipboard.App's Program.</summary>
public static class Program
{
    /// <summary>Starts the lab.</summary>
    /// <param name="args">The log file, then options.</param>
    /// <returns>0 (the lab reports in its log).</returns>
    [STAThread]
    public static int Main(string[] args)
    {
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

/// <summary>The lab application: one cloaked window, three moves, a log file.</summary>
public sealed partial class LabApp : Application
{
    /// <summary>The command line.</summary>
    private readonly string[] args;

    /// <summary>The log, written to the log file when the experiment ends.</summary>
    private readonly StringBuilder log = new();

    /// <summary>Creates the app (code only: a plain Grid needs no control styles).</summary>
    /// <param name="args">The command line.</param>
    public LabApp(string[] args) => this.args = args;

    /// <summary>Runs the experiment once WinUI is up, writes the log, and exits.</summary>
    /// <param name="e">Launch data (unused).</param>
    protected override async void OnLaunched(LaunchActivatedEventArgs e)
    {
        bool fix = args.Contains("--fix");
        try
        {
            var monitors = Monitors();
            foreach (var m in monitors)
            {
                Log($"monitor work {Fmt(m.Work)} scale {m.Dpi / 96.0:0.##}{(m.Primary ? " main" : string.Empty)}");
            }

            var main = monitors.First(m => m.Primary);
            var other = monitors.FirstOrDefault(m => m.Dpi != main.Dpi);
            if (other.Dpi == 0)
            {
                Log("No monitor with another scale: nothing to measure.");
                return;
            }

            var window = new Window { Content = new Grid() };
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var presenter = OverlappedPresenter.Create();
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(true, false);
            window.AppWindow.SetPresenter(presenter);
            window.AppWindow.IsShownInSwitchers = false;

            // Cloaked before it is ever shown, and shown without activation: never on screen, never in front.
            int cloak = 1;
            int hr = DwmSetWindowAttribute(hwnd, 13, ref cloak, sizeof(int));
            if (hr != 0)
            {
                Log($"DWM refused to cloak the window (0x{hr:X8}): stopping, the window would be visible.");
                window.Close();
                return;
            }

            await MoveAsync(window, hwnd, main, "start, on the main monitor", fix);
            window.AppWindow.Show(false);
            await MoveAsync(window, hwnd, other, "to the monitor with another scale", fix);
            await MoveAsync(window, hwnd, main, "back to the main monitor", fix);
            await MoveAsync(window, hwnd, other, "to the other monitor again", fix);
            window.Close();
        }
        catch (Exception ex)
        {
            Log($"failed: {ex}");
        }
        finally
        {
            File.WriteAllText(args.Length > 0 ? args[0] : "dpi_move_lab.log", log.ToString());
            Exit();
        }
    }

    /// <summary>
    /// Moves the window to the middle of a monitor's work area at the panel's default size (400 × 560 DIPs) in that
    /// monitor's pixels, and logs what it asked for and what the window holds right after and 300 ms later.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="hwnd">Its handle.</param>
    /// <param name="target">The monitor.</param>
    /// <param name="label">What the step is.</param>
    /// <param name="fix">Ask for the bounds again when they differ.</param>
    /// <returns>A task that completes when the step is logged.</returns>
    private async Task MoveAsync(Window window, nint hwnd, (RECT Work, uint Dpi, bool Primary) target, string label, bool fix)
    {
        double scale = target.Dpi / 96.0;
        int width = (int)Math.Round(400 * scale), height = (int)Math.Round(560 * scale);
        var rect = new RectInt32(
            target.Work.Left + ((target.Work.Right - target.Work.Left - width) / 2),
            target.Work.Top + ((target.Work.Bottom - target.Work.Top - height) / 2),
            width,
            height);
        window.AppWindow.MoveAndResize(rect);
        string now = Bounds(window);
        bool fixedAgain = false;
        if (fix && !Matches(window, rect))
        {
            window.AppWindow.MoveAndResize(rect);
            fixedAgain = true;
        }

        string afterFix = Bounds(window);
        await Task.Delay(300);
        Log($"{label}: asked {rect.X},{rect.Y} {rect.Width}x{rect.Height}; right after {now}{(fixedAgain ? $"; asked again: {afterFix}" : string.Empty)}; " +
            $"300 ms later {Bounds(window)}; window scale {GetDpiForWindow(hwnd) / 96.0:0.##}; matches: {Matches(window, rect)}");
    }

    /// <summary>Whether the window's bounds are exactly the rectangle.</summary>
    /// <param name="window">The window.</param>
    /// <param name="rect">The rectangle.</param>
    /// <returns><see langword="true"/> when position and size agree.</returns>
    private static bool Matches(Window window, RectInt32 rect) =>
        window.AppWindow.Position.X == rect.X && window.AppWindow.Position.Y == rect.Y &&
        window.AppWindow.Size.Width == rect.Width && window.AppWindow.Size.Height == rect.Height;

    /// <summary>The window's bounds as text.</summary>
    /// <param name="window">The window.</param>
    /// <returns>"x,y wxh".</returns>
    private static string Bounds(Window window) =>
        $"{window.AppWindow.Position.X},{window.AppWindow.Position.Y} {window.AppWindow.Size.Width}x{window.AppWindow.Size.Height}";

    /// <summary>Adds a line to the log.</summary>
    /// <param name="line">The line.</param>
    private void Log(string line) => log.AppendLine(line);

    /// <summary>A rectangle as text.</summary>
    /// <param name="r">The rectangle.</param>
    /// <returns>"(l,t)-(r,b)".</returns>
    private static string Fmt(RECT r) => $"({r.Left},{r.Top})-({r.Right},{r.Bottom})";

    /// <summary>Every monitor's work area, scale and main flag.</summary>
    /// <returns>The monitors.</returns>
    private static List<(RECT Work, uint Dpi, bool Primary)> Monitors()
    {
        var found = new List<(RECT Work, uint Dpi, bool Primary)>();
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfoW(monitor, ref info);
            GetDpiForMonitor(monitor, 0, out uint dpi, out _);
            found.Add((info.rcWork, dpi, (info.dwFlags & 1) != 0));
            return true;
        }, 0);
        return found;
    }

    /// <summary>EnumDisplayMonitors callback.</summary>
    /// <param name="monitor">Monitor.</param>
    /// <param name="hdc">Unused.</param>
    /// <param name="rect">Unused.</param>
    /// <param name="data">Unused.</param>
    /// <returns><see langword="true"/> to go on.</returns>
    private delegate bool MonitorEnum(nint monitor, nint hdc, nint rect, nint data);

    /// <summary>A rectangle (<c>RECT</c>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        /// <summary>Left.</summary>
        public int Left;
        /// <summary>Top.</summary>
        public int Top;
        /// <summary>Right.</summary>
        public int Right;
        /// <summary>Bottom.</summary>
        public int Bottom;
    }

    /// <summary>Monitor information (<c>MONITORINFO</c>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        /// <summary>Size of the structure.</summary>
        public uint cbSize;
        /// <summary>Monitor rectangle.</summary>
        public RECT rcMonitor;
        /// <summary>Work area.</summary>
        public RECT rcWork;
        /// <summary>Flags (1 = main monitor).</summary>
        public uint dwFlags;
    }

    /// <summary>Enumerates monitors.</summary>
    /// <param name="hdc">0.</param>
    /// <param name="clip">0.</param>
    /// <param name="callback">Callback.</param>
    /// <param name="data">0.</param>
    /// <returns>Success.</returns>
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnum callback, nint data);

    /// <summary>Reads a monitor's rectangles.</summary>
    /// <param name="monitor">Monitor.</param>
    /// <param name="info">Receives the information.</param>
    /// <returns>Success.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);

    /// <summary>A monitor's effective DPI.</summary>
    /// <param name="monitor">Monitor.</param>
    /// <param name="type">0 = effective.</param>
    /// <param name="x">Horizontal DPI.</param>
    /// <param name="y">Vertical DPI.</param>
    /// <returns>HRESULT.</returns>
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);

    /// <summary>A window's DPI.</summary>
    /// <param name="hwnd">Window.</param>
    /// <returns>The DPI.</returns>
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    /// <summary>Sets a DWM window attribute (13 = cloak).</summary>
    /// <param name="hwnd">Window.</param>
    /// <param name="attribute">Attribute.</param>
    /// <param name="value">Value.</param>
    /// <param name="size">Size of the value.</param>
    /// <returns>HRESULT.</returns>
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
