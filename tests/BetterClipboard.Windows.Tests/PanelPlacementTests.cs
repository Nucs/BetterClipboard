using System.Runtime.InteropServices;
using BetterClipboard.Core.Settings;
using BetterClipboard.Windows.Input;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Which screen a summon puts the panel on, and where on it (CLAUDE.md §2.38): the rule in
/// <see cref="FlyoutPositioner.ChooseScreen"/> on the owner's three-monitor layout, centering with the groups column
/// (<see cref="FlyoutPositioner.Place"/>), and the Win32 side (<see cref="MonitorLookup"/>) against this PC's real monitors
/// and real (cloaked, never shown) windows.
/// </summary>
public sealed class PanelPlacementTests
{
    /// <summary>The owner's left monitor (100 %): left of the main one, so its coordinates are negative.</summary>
    private static readonly ScreenRect LeftWork = new(-1920, 6, 0, 1038);

    /// <summary>The owner's main monitor (100 %).</summary>
    private static readonly ScreenRect MainWork = new(0, 0, 1920, 1032);

    /// <summary>The owner's right monitor (125 %).</summary>
    private static readonly ScreenRect RightWork = new(1920, 0, 3840, 1020);

    /// <summary>The desktop's window: it spans every monitor.</summary>
    private static readonly ScreenRect Desktop = new(-1920, 0, 3840, 1086);

    /// <summary>A window the user types in on the right monitor.</summary>
    private const nint Editor = 0x1234;

    /// <summary>The editor's bounds on the right monitor (invisible borders included).</summary>
    private static readonly ScreenRect EditorBounds = new(2200, 100, 3400, 900);

    /// <summary>
    /// Next to the text cursor (the default) keeps its old rule: the monitor of the point below the caret's left end, else
    /// the mouse's; the window is not asked.
    /// </summary>
    [Fact]
    public void ChooseScreen_NextToTheCaret_IsTheCaretsMonitorElseTheMouses()
    {
        var withCaret = Context(caret: new ScreenRect(2500, 400, 2502, 420), cursorX: -500, cursorY: 300);
        Assert.Equal(PanelScreen.At(new ScreenPoint(2500, 420)), FlyoutPositioner.ChooseScreen(FlyoutPlacement.NearCaret, false, withCaret, default));

        var withoutCaret = Context(caret: null, cursorX: -500, cursorY: 300);
        Assert.Equal(PanelScreen.At(new ScreenPoint(-500, 300)), FlyoutPositioner.ChooseScreen(FlyoutPlacement.NearCaret, false, withoutCaret, new TargetWindowFacts(EditorBounds, true)));

        // The main-screen choice belongs to the centered placement only.
        Assert.Equal(PanelScreen.At(new ScreenPoint(2500, 420)), FlyoutPositioner.ChooseScreen(FlyoutPlacement.NearCaret, true, withCaret, default));
    }

    /// <summary>Next to the mouse: the mouse's monitor, even with a caret on another one.</summary>
    [Fact]
    public void ChooseScreen_NextToTheMouse_IsTheMousesMonitor()
    {
        var context = Context(caret: new ScreenRect(2500, 400, 2502, 420), cursorX: -500, cursorY: 300);
        Assert.Equal(PanelScreen.At(new ScreenPoint(-500, 300)), FlyoutPositioner.ChooseScreen(FlyoutPlacement.NearCursor, false, context, new TargetWindowFacts(EditorBounds, true)));
    }

    /// <summary>Centered on the main screen: the main monitor, whatever the shortcut captured.</summary>
    [Fact]
    public void ChooseScreen_MainScreen_IgnoresWhatWasCaptured()
    {
        Assert.Equal(PanelScreen.Main, FlyoutPositioner.ChooseScreen(FlyoutPlacement.CenterScreen, true, Context(new ScreenRect(2500, 400, 2502, 420), -500, 300), new TargetWindowFacts(EditorBounds, true)));
        Assert.Equal(PanelScreen.Main, FlyoutPositioner.ChooseScreen(FlyoutPlacement.CenterScreen, true, new ForegroundContext(0, 0, null, new ScreenPoint(-500, 300)), default));
    }

    /// <summary>
    /// The cases of "center of the current screen" (the screen the user is typing on): each row is a context, what is known
    /// about the window, and the monitor chosen.
    /// </summary>
    public static TheoryData<string, ForegroundContext, TargetWindowFacts, PanelScreen> CurrentScreenCases => new()
    {
        // Typing on the right monitor with the mouse parked on the left one: the caret wins.
        { "caret in the window", Context(new ScreenRect(2500, 400, 2502, 420), -500, 300), new TargetWindowFacts(EditorBounds, true), PanelScreen.At(new ScreenPoint(2500, 410)) },

        // No caret (Chromium, WinUI, most Electron apps): the window's monitor, not the mouse's.
        { "window without a caret", Context(null, -500, 300), new TargetWindowFacts(EditorBounds, true), PanelScreen.Of(Editor) },

        // A caret an app parks at the screen's corner (a hidden helper window) must not drag the panel to the main monitor.
        { "caret outside the window", Context(new ScreenRect(0, 0, 1, 16), -500, 300), new TargetWindowFacts(EditorBounds, true), PanelScreen.Of(Editor) },

        // A click on the left monitor's desktop: the desktop spans every monitor, so the mouse says where the user is.
        { "the desktop", Context(null, -500, 300), new TargetWindowFacts(Desktop, false), PanelScreen.At(new ScreenPoint(-500, 300)) },

        // Renaming an icon on the right monitor's desktop: the caret is inside the desktop's window, and it says where.
        { "a caret on the desktop", Context(new ScreenRect(2500, 400, 2502, 420), -500, 300), new TargetWindowFacts(Desktop, false), PanelScreen.At(new ScreenPoint(2500, 410)) },

        // A minimized window: no bounds, marks nothing; the mouse decides.
        { "a minimized window", Context(null, 900, 500), new TargetWindowFacts(null, false), PanelScreen.At(new ScreenPoint(900, 500)) },

        // A window that closed before the summon read it: described as no window at all.
        { "a window that closed", Context(null, 900, 500), default(TargetWindowFacts), PanelScreen.At(new ScreenPoint(900, 500)) },

        // Opened from the tray (no window captured): the mouse decides, even if facts were passed.
        { "opened from the tray", new ForegroundContext(0, 0, null, new ScreenPoint(-500, 300)), new TargetWindowFacts(EditorBounds, true), PanelScreen.At(new ScreenPoint(-500, 300)) },
    };

    /// <summary>"Center of the current screen" picks the screen the user is typing on, case by case.</summary>
    /// <param name="name">What the case is (for the test name).</param>
    /// <param name="context">What the shortcut captured.</param>
    /// <param name="target">What is known about the window.</param>
    /// <param name="expected">The monitor that must be chosen.</param>
    [Theory]
    [MemberData(nameof(CurrentScreenCases))]
    public void ChooseScreen_CurrentScreen_IsWhereTheUserTypes(string name, ForegroundContext context, TargetWindowFacts target, PanelScreen expected)
    {
        Assert.False(string.IsNullOrEmpty(name));
        Assert.Equal(expected, FlyoutPositioner.ChooseScreen(FlyoutPlacement.CenterScreen, false, context, target));
    }

    /// <summary>A placement value no version writes (a hand-edited file) behaves like the default, next to the caret.</summary>
    [Fact]
    public void ChooseScreen_UnknownPlacement_IsNextToTheCaret()
    {
        var context = Context(new ScreenRect(2500, 400, 2502, 420), -500, 300);
        Assert.Equal(PanelScreen.At(new ScreenPoint(2500, 420)), FlyoutPositioner.ChooseScreen((FlyoutPlacement)42, true, context, default));
        Assert.Throws<ArgumentNullException>(() => FlyoutPositioner.ChooseScreen(FlyoutPlacement.CenterScreen, false, null!, default));
    }

    /// <summary>
    /// Centered, the panel is centered as a whole, groups column included: on the right monitor at 125 % a 500 × 700 list
    /// with its 55-pixel column is centered as 555 × 700; without the column the list alone is.
    /// </summary>
    [Fact]
    public void Place_Centered_CentersThePanelWithItsColumn()
    {
        var context = Context(null, 0, 0);
        Assert.Equal(new ScreenRect(2602, 160, 3157, 860), FlyoutPositioner.Place(FlyoutPlacement.CenterScreen, context, RightWork, 500, 700, 10, 55));
        Assert.Equal(new ScreenRect(2630, 160, 3130, 860), FlyoutPositioner.Place(FlyoutPlacement.CenterScreen, context, RightWork, 500, 700, 10, 0));
        Assert.Equal(new ScreenRect(2630, 160, 3130, 860), FlyoutPositioner.Place(FlyoutPlacement.CenterScreen, context, RightWork, 500, 700, 10, -5));
    }

    /// <summary>The monitor left of the main one has negative coordinates; centering works there as anywhere.</summary>
    [Fact]
    public void Place_Centered_OnTheMonitorLeftOfTheMainOne()
    {
        Assert.Equal(new ScreenRect(-1160, 242, -760, 802), FlyoutPositioner.Place(FlyoutPlacement.CenterScreen, Context(null, 0, 0), LeftWork, 400, 560, 8, 0));
        Assert.Equal(new ScreenRect(760, 236, 1160, 796), FlyoutPositioner.Place(FlyoutPlacement.CenterScreen, Context(null, 0, 0), MainWork, 400, 560, 8, 0));
    }

    /// <summary>A centered panel larger than the work area is shrunk to it, column included, and never leaves it.</summary>
    [Fact]
    public void Place_Centered_ShrinksToTheWorkArea()
    {
        var small = new ScreenRect(100, 50, 500, 450);
        Assert.Equal(small, FlyoutPositioner.Place(FlyoutPlacement.CenterScreen, Context(null, 0, 0), small, 400, 560, 8, 44));
    }

    /// <summary>
    /// Next to the caret, the list goes where it always went and the column is added on its left: the same bounds as
    /// <see cref="FlyoutPositioner.Compute"/> followed by <see cref="FlyoutPositioner.ExtendLeft"/>.
    /// </summary>
    [Fact]
    public void Place_NextToTheCaret_AddsTheColumnOnTheLeft()
    {
        var context = Context(new ScreenRect(500, 300, 502, 320), 0, 0);
        var list = FlyoutPositioner.Compute(FlyoutPlacement.NearCaret, context, MainWork, 400, 560, 8);
        Assert.Equal(new ScreenRect(484, 328, 884, 888), list);
        Assert.Equal(list, FlyoutPositioner.Place(FlyoutPlacement.NearCaret, context, MainWork, 400, 560, 8, 0));
        Assert.Equal(new ScreenRect(440, 328, 884, 888), FlyoutPositioner.Place(FlyoutPlacement.NearCaret, context, MainWork, 400, 560, 8, 44));
        Assert.Throws<ArgumentNullException>(() => FlyoutPositioner.Place(FlyoutPlacement.CenterScreen, null!, MainWork, 400, 560, 8, 0));
    }

    /// <summary>The desktop's own window classes are recognized (in any case, as Windows compares class names), and nothing else.</summary>
    /// <param name="className">A class name.</param>
    /// <param name="isDesktop">Whether it is the desktop's.</param>
    [Theory]
    [InlineData("Progman", true)]
    [InlineData("WorkerW", true)]
    [InlineData("progman", true)]
    [InlineData("Shell_TrayWnd", false)]
    [InlineData("Shell_SecondaryTrayWnd", false)]
    [InlineData("Notepad", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDesktopWindowClass(string? className, bool isDesktop)
    {
        Assert.Equal(isDesktop, MonitorLookup.IsDesktopWindowClass(className));
    }

    /// <summary>
    /// The main monitor is the one at the origin: <see cref="MonitorLookup.Main"/> and a choice of
    /// <see cref="PanelScreen.Main"/> read the monitor Windows flags as primary, on this PC's real monitors.
    /// </summary>
    [Fact]
    public void Lookup_MainIsTheMonitorWindowsFlagsAsPrimary()
    {
        var monitors = RealMonitors();
        Assert.SkipWhen(monitors.Count == 0, "No monitors to enumerate in this session.");
        var primary = monitors.Single(m => m.Primary);

        Assert.Equal(primary.Work, MonitorLookup.Main().WorkArea);
        Assert.Equal(primary.Work, MonitorLookup.FromScreen(PanelScreen.Main).WorkArea);
        Assert.Equal(primary.Dpi / 96.0, MonitorLookup.Main().Scale, 3);
    }

    /// <summary>A point inside each real monitor chooses that monitor, with that monitor's scale (negative coordinates included).</summary>
    [Fact]
    public void Lookup_APointChoosesItsOwnMonitor()
    {
        var monitors = RealMonitors();
        Assert.SkipWhen(monitors.Count == 0, "No monitors to enumerate in this session.");
        foreach (var monitor in monitors)
        {
            var middle = new ScreenPoint((monitor.Work.Left + monitor.Work.Right) / 2, (monitor.Work.Top + monitor.Work.Bottom) / 2);
            var (work, scale) = MonitorLookup.FromScreen(PanelScreen.At(middle));
            Assert.Equal(monitor.Work, work);
            Assert.Equal(monitor.Dpi / 96.0, scale, 3);
        }
    }

    /// <summary>
    /// What a summon reads about real windows: none, a handle that is no window, a hidden window, a visible one (cloaked, so
    /// it is never on screen), the same one minimized, and the desktop's own window.
    /// </summary>
    [Fact]
    public void DescribeTarget_RealWindows()
    {
        Assert.Equal(default, MonitorLookup.DescribeTarget(0));
        Assert.Equal(default, MonitorLookup.DescribeTarget(unchecked((nint)0x7FFFFFF0)));

        // A tool window that never activates and is cloaked before it is shown: nothing appears on the screen or the
        // taskbar, and the foreground never moves.
        nint window = CreateWindowExW(WsExToolWindow | WsExNoActivate, "Static", "BC-TEST placement", WsPopup, 200, 150, 320, 240, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            var hidden = MonitorLookup.DescribeTarget(window);
            Assert.False(hidden.MarksScreen);
            Assert.NotNull(hidden.Bounds);

            // Shown only once cloaked: where DWM refuses, the rest is skipped rather than flash a window on someone's screen.
            int cloak = 1;
            Assert.SkipWhen(DwmSetWindowAttribute(window, 13, ref cloak, sizeof(int)) != 0, "DWM did not cloak the test window, so it is not shown.");
            ShowWindow(window, SwShowNoActivate);
            var shown = MonitorLookup.DescribeTarget(window);
            Assert.True(shown.MarksScreen);
            Assert.True(GetWindowRect(window, out var rect));
            Assert.Equal(new ScreenRect(rect.Left, rect.Top, rect.Right, rect.Bottom), shown.Bounds);

            // Chosen as the current screen without a caret, the window's monitor is the monitor that holds its middle.
            var context = new ForegroundContext(window, 0, null, new ScreenPoint(-30000, -30000));
            var chosen = FlyoutPositioner.ChooseScreen(FlyoutPlacement.CenterScreen, false, context, shown);
            Assert.Equal(PanelScreen.Of(window), chosen);
            var middle = new ScreenPoint((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
            Assert.Equal(MonitorLookup.FromPoint(middle), MonitorLookup.FromScreen(chosen));

            ShowWindow(window, SwShowMinNoActive);
            Assert.Equal(new TargetWindowFacts(null, false), MonitorLookup.DescribeTarget(window));
        }
        finally
        {
            DestroyWindow(window);
        }

        Assert.Equal(default, MonitorLookup.DescribeTarget(window));
        nint desktop = GetShellWindow();
        Assert.SkipWhen(desktop == 0, "No shell (Explorer) runs in this session, so there is no desktop window to describe.");
        var facts = MonitorLookup.DescribeTarget(desktop);
        Assert.False(facts.MarksScreen);
        Assert.NotNull(facts.Bounds);
    }

    /// <summary>Builds a context whose window is <see cref="Editor"/>.</summary>
    /// <param name="caret">Caret rectangle, if any.</param>
    /// <param name="cursorX">Mouse X.</param>
    /// <param name="cursorY">Mouse Y.</param>
    /// <returns>The context.</returns>
    private static ForegroundContext Context(ScreenRect? caret, int cursorX, int cursorY) =>
        new(Editor, 42, caret, new ScreenPoint(cursorX, cursorY));

    /// <summary>Every monitor of this session: work area, effective DPI, primary flag.</summary>
    /// <returns>The monitors.</returns>
    private static List<(ScreenRect Work, uint Dpi, bool Primary)> RealMonitors()
    {
        var found = new List<(ScreenRect Work, uint Dpi, bool Primary)>();
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfoW(monitor, ref info))
            {
                uint dpi = GetDpiForMonitor(monitor, 0, out uint x, out _) == 0 && x > 0 ? x : 96;
                found.Add((new ScreenRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom), dpi, (info.dwFlags & 1) != 0));
            }

            return true;
        }, 0);
        return found;
    }

    /// <summary><c>WS_POPUP</c>: a window without a frame.</summary>
    private const int WsPopup = unchecked((int)0x80000000);

    /// <summary><c>WS_EX_TOOLWINDOW</c>: never on the taskbar.</summary>
    private const int WsExToolWindow = 0x00000080;

    /// <summary><c>WS_EX_NOACTIVATE</c>: never the foreground window.</summary>
    private const int WsExNoActivate = 0x08000000;

    /// <summary><c>SW_SHOWNOACTIVATE</c>.</summary>
    private const int SwShowNoActivate = 4;

    /// <summary><c>SW_SHOWMINNOACTIVE</c>.</summary>
    private const int SwShowMinNoActive = 7;

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
        /// <summary>Flags (1 = primary).</summary>
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

    /// <summary>Creates a window.</summary>
    /// <param name="exStyle">Extended style.</param>
    /// <param name="className">Class.</param>
    /// <param name="title">Title.</param>
    /// <param name="style">Style.</param>
    /// <param name="x">Left.</param>
    /// <param name="y">Top.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="parent">Parent.</param>
    /// <param name="menu">Menu.</param>
    /// <param name="instance">Module.</param>
    /// <param name="param">Creation data.</param>
    /// <returns>The window, or 0.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(int exStyle, string className, string title, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    /// <summary>Destroys a window.</summary>
    /// <param name="window">Window.</param>
    /// <returns>Success.</returns>
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);

    /// <summary>Shows or minimizes a window.</summary>
    /// <param name="window">Window.</param>
    /// <param name="command">SW_ command.</param>
    /// <returns>Whether it was visible before.</returns>
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    /// <summary>A window's rectangle.</summary>
    /// <param name="window">Window.</param>
    /// <param name="rect">Receives it.</param>
    /// <returns>Success.</returns>
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out RECT rect);

    /// <summary>The shell's desktop window.</summary>
    /// <returns>The window, or 0 without a shell.</returns>
    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    /// <summary>Sets a DWM window attribute (13 = cloak).</summary>
    /// <param name="window">Window.</param>
    /// <param name="attribute">Attribute.</param>
    /// <param name="value">Value.</param>
    /// <param name="size">Size of the value.</param>
    /// <returns>HRESULT.</returns>
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
