using System.Runtime.InteropServices;
using BetterClipboard.Core.Settings;
using BetterClipboard.Windows.Input;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Which screen a summon puts the panel on, and where on it (CLAUDE.md §2.38): the rule in
/// <see cref="FlyoutPositioner.ChooseScreen"/> on the owner's three-monitor layout, centering with the groups column
/// (<see cref="FlyoutPositioner.Place"/>), and the Win32 side (<see cref="MonitorLookup"/>) against this PC's real monitors.
/// </summary>
public sealed class PanelPlacementTests
{
    /// <summary>The owner's left monitor (100 %): left of the main one, so its coordinates are negative.</summary>
    private static readonly ScreenRect LeftWork = new(-1920, 6, 0, 1038);

    /// <summary>The owner's main monitor (100 %).</summary>
    private static readonly ScreenRect MainWork = new(0, 0, 1920, 1032);

    /// <summary>The owner's right monitor (125 %).</summary>
    private static readonly ScreenRect RightWork = new(1920, 0, 3840, 1020);

    /// <summary>A window the user types in on the right monitor.</summary>
    private const nint Editor = 0x1234;

    /// <summary>
    /// Next to the text cursor (the default) keeps its old rule: the monitor of the point below the caret's left end, else
    /// the mouse's; the window is not asked.
    /// </summary>
    [Fact]
    public void ChooseScreen_NextToTheCaret_IsTheCaretsMonitorElseTheMouses()
    {
        var withCaret = Context(caret: new ScreenRect(2500, 400, 2502, 420), cursorX: -500, cursorY: 300);
        Assert.Equal(PanelScreen.At(new ScreenPoint(2500, 420)), FlyoutPositioner.ChooseScreen(FlyoutPlacement.NearCaret, false, withCaret));

        var withoutCaret = Context(caret: null, cursorX: -500, cursorY: 300);
        Assert.Equal(PanelScreen.At(new ScreenPoint(-500, 300)), FlyoutPositioner.ChooseScreen(FlyoutPlacement.NearCaret, false, withoutCaret));

        // The main-screen choice belongs to the centered placement only.
        Assert.Equal(PanelScreen.At(new ScreenPoint(2500, 420)), FlyoutPositioner.ChooseScreen(FlyoutPlacement.NearCaret, true, withCaret));
    }

    /// <summary>Next to the mouse: the mouse's monitor, even with a caret on another one.</summary>
    [Fact]
    public void ChooseScreen_NextToTheMouse_IsTheMousesMonitor()
    {
        var context = Context(caret: new ScreenRect(2500, 400, 2502, 420), cursorX: -500, cursorY: 300);
        Assert.Equal(PanelScreen.At(new ScreenPoint(-500, 300)), FlyoutPositioner.ChooseScreen(FlyoutPlacement.NearCursor, false, context));
    }

    /// <summary>Centered on the main screen: the main monitor, whatever the shortcut captured.</summary>
    [Fact]
    public void ChooseScreen_MainScreen_IgnoresWhatWasCaptured()
    {
        Assert.Equal(PanelScreen.Main, FlyoutPositioner.ChooseScreen(FlyoutPlacement.CenterScreen, true, Context(new ScreenRect(2500, 400, 2502, 420), -500, 300)));
        Assert.Equal(PanelScreen.Main, FlyoutPositioner.ChooseScreen(FlyoutPlacement.CenterScreen, true, new ForegroundContext(0, 0, null, new ScreenPoint(-500, 300))));
    }

    /// <summary>
    /// The cases of "center of the current screen", the screen with the mouse pointer (the owner's choice, 2026-11-20): each
    /// row is a context and the monitor chosen. Until then the caret, and else the window the user typed in, decided: these
    /// are the cases where they and the mouse disagree, and the mouse wins every one.
    /// </summary>
    public static TheoryData<string, ForegroundContext, PanelScreen> CurrentScreenCases => new()
    {
        // Typing on the right monitor with the mouse on the left one: the left one (the caret decided until 2026-11-20).
        { "typing on another monitor", Context(new ScreenRect(2500, 400, 2502, 420), -500, 300), PanelScreen.At(new ScreenPoint(-500, 300)) },

        // Typing on the left monitor with the mouse on the right one, at another scale: the right one.
        { "the mouse on the monitor at 125 %", Context(new ScreenRect(-1500, 400, -1498, 420), 2500, 600), PanelScreen.At(new ScreenPoint(2500, 600)) },

        // No caret (Chromium, terminals, most Electron apps) in a window on the right monitor: the mouse's monitor, not the
        // window's (the window's decided until 2026-11-20).
        { "a window without a caret", Context(null, -500, 300), PanelScreen.At(new ScreenPoint(-500, 300)) },

        // A caret an app parks at the screen's corner (a hidden helper window): the mouse's monitor all the same.
        { "a caret parked at the corner", Context(new ScreenRect(0, 0, 1, 16), 2500, 600), PanelScreen.At(new ScreenPoint(2500, 600)) },

        // Renaming an icon on the right monitor's desktop with the mouse on the left one: the left one.
        { "a caret on the desktop", new ForegroundContext(0x5678, 7, new ScreenRect(2500, 400, 2502, 420), new ScreenPoint(-500, 300)), PanelScreen.At(new ScreenPoint(-500, 300)) },

        // The mouse on the main monitor's taskbar (outside its work area, inside the monitor): the main monitor's point,
        // which the lookup (MonitorFromPoint) puts on that monitor.
        { "the mouse on the taskbar", Context(new ScreenRect(2500, 400, 2502, 420), 100, 1060), PanelScreen.At(new ScreenPoint(100, 1060)) },

        // Opened from the tray (no window, no caret captured): the mouse's monitor, as before.
        { "opened from the tray", new ForegroundContext(0, 0, null, new ScreenPoint(-500, 300)), PanelScreen.At(new ScreenPoint(-500, 300)) },
    };

    /// <summary>"Center of the current screen" picks the screen with the mouse pointer, case by case.</summary>
    /// <param name="name">What the case is (for the test name).</param>
    /// <param name="context">What the shortcut captured.</param>
    /// <param name="expected">The monitor that must be chosen.</param>
    [Theory]
    [MemberData(nameof(CurrentScreenCases))]
    public void ChooseScreen_CurrentScreen_IsTheMousesMonitor(string name, ForegroundContext context, PanelScreen expected)
    {
        Assert.False(string.IsNullOrEmpty(name));
        Assert.Equal(expected, FlyoutPositioner.ChooseScreen(FlyoutPlacement.CenterScreen, false, context));
    }

    /// <summary>A placement value no version writes (a hand-edited file) behaves like the default, next to the caret.</summary>
    [Fact]
    public void ChooseScreen_UnknownPlacement_IsNextToTheCaret()
    {
        var context = Context(new ScreenRect(2500, 400, 2502, 420), -500, 300);
        Assert.Equal(PanelScreen.At(new ScreenPoint(2500, 420)), FlyoutPositioner.ChooseScreen((FlyoutPlacement)42, true, context));
        Assert.Throws<ArgumentNullException>(() => FlyoutPositioner.ChooseScreen(FlyoutPlacement.CenterScreen, false, null!));
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
    /// On this PC's real monitors, "center of the current screen" looks up the monitor under the mouse, whichever monitor
    /// holds the caret: for each monitor, the mouse in its middle and the caret in the middle of another one (the same one
    /// on a PC with a single monitor) give that monitor's work area and scale.
    /// </summary>
    [Fact]
    public void Lookup_TheCurrentScreenIsTheMousesRealMonitor()
    {
        var monitors = RealMonitors();
        Assert.SkipWhen(monitors.Count == 0, "No monitors to enumerate in this session.");
        for (int i = 0; i < monitors.Count; i++)
        {
            var mouseOn = monitors[i];
            var caretOn = monitors[(i + 1) % monitors.Count];
            var caret = new ScreenRect((caretOn.Work.Left + caretOn.Work.Right) / 2, (caretOn.Work.Top + caretOn.Work.Bottom) / 2,
                ((caretOn.Work.Left + caretOn.Work.Right) / 2) + 2, ((caretOn.Work.Top + caretOn.Work.Bottom) / 2) + 20);
            var mouse = new ScreenPoint((mouseOn.Work.Left + mouseOn.Work.Right) / 2, (mouseOn.Work.Top + mouseOn.Work.Bottom) / 2);

            var (work, scale) = MonitorLookup.FromScreen(FlyoutPositioner.ChooseScreen(FlyoutPlacement.CenterScreen, false, new ForegroundContext(Editor, 42, caret, mouse)));
            Assert.Equal(mouseOn.Work, work);
            Assert.Equal(mouseOn.Dpi / 96.0, scale, 3);
        }
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
}
