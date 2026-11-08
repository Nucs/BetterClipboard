using BetterClipboard.Core.Settings;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Input;

/// <summary>
/// Computes where the flyout goes (physical pixels), Win+V style: just below the text caret, flipped
/// above when there is no room, always fully inside the monitor's work area (never under the taskbar).
/// </summary>
/// <remarks>
/// Pure functions so placement rules are unit-tested without windows or monitors: which monitor a summon uses
/// (<see cref="ChooseScreen"/>), and where on it the panel goes (<see cref="Place"/>, <see cref="Compute"/>). Sizes are in
/// physical pixels — callers scale the DIP size by the target monitor's DPI first (<see cref="MonitorLookup"/>).
/// </remarks>
public static class FlyoutPositioner
{
    /// <summary>Horizontal nudge so the caret stays visible just inside the flyout's left edge.</summary>
    private const int CaretInset = 16;

    /// <summary>
    /// Chooses the monitor a summon puts the panel on, from the setting and what was captured when the shortcut was
    /// pressed; <see cref="MonitorLookup.FromScreen"/> then reads that monitor's work area and scale.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>Next to the text cursor</b> (the default): the caret's monitor (the point below its left end, where
    /// <see cref="Compute"/> anchors the panel), else the mouse's.</item>
    /// <item><b>Next to the mouse:</b> the mouse's monitor.</item>
    /// <item><b>Center of the main screen:</b> the main monitor, whatever was captured.</item>
    /// <item><b>Center of the current screen</b> — the screen the user is typing on (the owner's choice, 2026-11-08, over
    /// "where the mouse is"): the caret's monitor while the caret lies inside the window the user was in; else the monitor
    /// showing most of that window, when the window marks a screen; else the mouse's monitor.</item>
    /// </list>
    /// <para>
    /// Why the caret must lie inside the window: some apps keep their caret in a hidden helper window parked at the screen's
    /// corner, which would drag the panel to the main monitor while the app is on another. A caret on the desktop (renaming
    /// an icon) still counts: the desktop's window spans every monitor, so the caret is inside it. Why the window must mark
    /// a screen: the desktop's window spans every monitor, and the monitor "showing most of it" would be an arbitrary one
    /// (the main one, with equal monitors) — after a click on another monitor's desktop, the mouse is the better witness.
    /// Without any window (opened from the tray, nothing active) the mouse decides too.
    /// </para>
    /// </remarks>
    /// <param name="placement">The placement setting (<see cref="AppSettings.Placement"/>); an unknown value counts as next to the caret.</param>
    /// <param name="centerOnMainScreen">The main-screen setting (<see cref="AppSettings.CenterOnMainScreen"/>); only <see cref="FlyoutPlacement.CenterScreen"/> reads it.</param>
    /// <param name="context">The caret, the mouse and the window captured when the shortcut was pressed.</param>
    /// <param name="target">What is known about that window (<see cref="MonitorLookup.DescribeTarget"/>); <see langword="default"/> for none.</param>
    /// <returns>The monitor to look up.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static PanelScreen ChooseScreen(FlyoutPlacement placement, bool centerOnMainScreen, ForegroundContext context, TargetWindowFacts target)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (placement)
        {
            case FlyoutPlacement.CenterScreen when centerOnMainScreen:
                return PanelScreen.Main;

            case FlyoutPlacement.CenterScreen:
                if (context.Caret is { } caret && target.Bounds is { } bounds && Contains(bounds, CaretMiddle(caret)))
                {
                    return PanelScreen.At(CaretMiddle(caret));
                }

                return context.TargetWindow != 0 && target.MarksScreen
                    ? PanelScreen.Of(context.TargetWindow)
                    : PanelScreen.At(context.Cursor);

            case FlyoutPlacement.NearCursor:
                return PanelScreen.At(context.Cursor);

            default:
                // Next to the caret: the same point Compute anchors the panel below, so monitor and position agree.
                return PanelScreen.At(context.Caret is { } anchor ? new ScreenPoint(anchor.Left, anchor.Bottom) : context.Cursor);
        }
    }

    /// <summary>
    /// The panel's bounds for a summon, groups column included: next to the caret or the mouse, the list goes exactly
    /// where <see cref="Compute"/> puts it and the column is added on its left (<see cref="ExtendLeft"/>), so the list does
    /// not move when the column is open; centered, the panel is centered as a whole, column included.
    /// </summary>
    /// <remarks>
    /// The column opened or closed while the panel shows still grows or shrinks it on the left (the list stays put), so a
    /// centered panel is off-center by the column's width until the next summon centers it again.
    /// </remarks>
    /// <param name="placement">Placement preference.</param>
    /// <param name="context">Caret/cursor snapshot from the hotkey press.</param>
    /// <param name="workArea">Work area of the chosen monitor (<see cref="ChooseScreen"/>).</param>
    /// <param name="width">The list's width (the panel without the column) in physical pixels.</param>
    /// <param name="height">The panel's height in physical pixels.</param>
    /// <param name="gap">Distance kept between the anchor and the panel, in physical pixels.</param>
    /// <param name="columnWidth">The groups column's width in physical pixels; 0 (or less) while it is closed.</param>
    /// <returns>The bounds, inside <paramref name="workArea"/> (shrunk when it is smaller).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static ScreenRect Place(FlyoutPlacement placement, ForegroundContext context, ScreenRect workArea, int width, int height, int gap, int columnWidth)
    {
        ArgumentNullException.ThrowIfNull(context);
        int column = Math.Max(0, columnWidth);
        if (placement == FlyoutPlacement.CenterScreen)
        {
            return Compute(placement, context, workArea, width + column, height, gap);
        }

        var bounds = Compute(placement, context, workArea, width, height, gap);
        return column > 0 ? ExtendLeft(bounds, column, workArea) : bounds;
    }

    /// <summary>
    /// Computes the flyout rectangle.
    /// </summary>
    /// <param name="placement">Placement preference.</param>
    /// <param name="context">Caret/cursor snapshot from the hotkey press.</param>
    /// <param name="workArea">Work area of the monitor containing the anchor.</param>
    /// <param name="width">Flyout width in physical pixels.</param>
    /// <param name="height">Flyout height in physical pixels.</param>
    /// <param name="gap">Distance kept between the anchor and the flyout, in physical pixels.</param>
    /// <returns>The flyout bounds, clamped to <paramref name="workArea"/> (shrunk if the work area is smaller).</returns>
    public static ScreenRect Compute(FlyoutPlacement placement, ForegroundContext context, ScreenRect workArea, int width, int height, int gap)
    {
        // Never exceed the monitor (tiny/portrait screens): shrink before positioning.
        width = Math.Min(width, workArea.Width);
        height = Math.Min(height, workArea.Height);

        int left, top;
        if (placement == FlyoutPlacement.CenterScreen)
        {
            left = workArea.Left + ((workArea.Width - width) / 2);
            top = workArea.Top + ((workArea.Height - height) / 2);
        }
        else
        {
            var anchor = placement == FlyoutPlacement.NearCaret && context.Caret is { } caret
                ? caret
                : new ScreenRect(context.Cursor.X, context.Cursor.Y, context.Cursor.X + 1, context.Cursor.Y + 1);

            left = anchor.Left - CaretInset;
            top = anchor.Bottom + gap;
            if (top + height > workArea.Bottom && anchor.Top - gap - height >= workArea.Top)
            {
                // Not enough room below but enough above: open upwards so the caret line stays visible.
                top = anchor.Top - gap - height;
            }
        }

        left = Math.Clamp(left, workArea.Left, workArea.Right - width);
        top = Math.Clamp(top, workArea.Top, workArea.Bottom - height);
        return new ScreenRect(left, top, left + width, top + height);
    }

    /// <summary>
    /// Grows the flyout to the left by <paramref name="extra"/> pixels (the groups column), so everything
    /// that was on screen stays where it was and only a new strip appears on the left.
    /// </summary>
    /// <remarks>
    /// Near the work area's left edge there is no room to grow left: the flyout then starts at the edge
    /// and grows right instead (its content shifts right by the missing amount). It is never wider than
    /// the work area, and never moves vertically.
    /// </remarks>
    /// <param name="bounds">Current flyout bounds.</param>
    /// <param name="extra">Pixels to add on the left.</param>
    /// <param name="workArea">Work area of the flyout's monitor.</param>
    /// <returns>The grown bounds.</returns>
    public static ScreenRect ExtendLeft(ScreenRect bounds, int extra, ScreenRect workArea)
    {
        int width = Math.Min(bounds.Width + Math.Max(0, extra), workArea.Width);
        int left = Math.Clamp(bounds.Right - width, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        return new ScreenRect(left, bounds.Top, left + width, bounds.Bottom);
    }

    /// <summary>
    /// Undoes <see cref="ExtendLeft"/>: removes <paramref name="extra"/> pixels from the left, keeping the
    /// right edge (and so the list, search box and buttons) where they are.
    /// </summary>
    /// <param name="bounds">Current flyout bounds.</param>
    /// <param name="extra">Pixels to remove on the left.</param>
    /// <returns>The shrunk bounds (never narrower than 1 pixel).</returns>
    public static ScreenRect ShrinkLeft(ScreenRect bounds, int extra)
    {
        int left = Math.Min(bounds.Left + Math.Max(0, extra), bounds.Right - 1);
        return new ScreenRect(left, bounds.Top, bounds.Right, bounds.Bottom);
    }

    /// <summary>
    /// The middle of the caret's left edge: the point that says which monitor the caret is on, whichever end of a tall
    /// caret touches a monitor's edge.
    /// </summary>
    /// <param name="caret">The caret's rectangle on screen.</param>
    /// <returns>The point.</returns>
    private static ScreenPoint CaretMiddle(ScreenRect caret) => new(caret.Left, caret.Top + (caret.Height / 2));

    /// <summary>Whether a point lies inside a rectangle (right and bottom edges excluded, like the rectangle's own).</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="point">The point.</param>
    /// <returns><see langword="true"/> when inside.</returns>
    private static bool Contains(ScreenRect rect, ScreenPoint point) =>
        point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
}

/// <summary>The three kinds of monitor a summon can choose (<see cref="PanelScreen"/>).</summary>
public enum PanelScreenKind
{
    /// <summary>The monitor that holds a point, or the nearest one: the caret's or the mouse's.</summary>
    Point = 0,

    /// <summary>The monitor that shows most of a window: the window the user was typing in.</summary>
    Window = 1,

    /// <summary>The main monitor: the one Windows' display settings call the main display.</summary>
    Main = 2,
}

/// <summary>
/// The monitor a summon puts the panel on, as chosen (<see cref="FlyoutPositioner.ChooseScreen"/>) and before it is
/// looked up (<see cref="MonitorLookup.FromScreen"/>). Kept apart from the lookup so the choice is unit-tested without
/// monitors.
/// </summary>
/// <param name="Kind">Which kind of monitor.</param>
/// <param name="Point">The point, for <see cref="PanelScreenKind.Point"/>; otherwise unused.</param>
/// <param name="Window">The window, for <see cref="PanelScreenKind.Window"/>; otherwise 0.</param>
public readonly record struct PanelScreen(PanelScreenKind Kind, ScreenPoint Point, nint Window)
{
    /// <summary>The main monitor.</summary>
    public static PanelScreen Main => new(PanelScreenKind.Main, default, 0);

    /// <summary>The monitor that holds a point (or is nearest to it).</summary>
    /// <param name="point">The point in physical screen pixels.</param>
    /// <returns>The choice.</returns>
    public static PanelScreen At(ScreenPoint point) => new(PanelScreenKind.Point, point, 0);

    /// <summary>The monitor that shows most of a window.</summary>
    /// <param name="window">The window.</param>
    /// <returns>The choice.</returns>
    public static PanelScreen Of(nint window) => new(PanelScreenKind.Window, default, window);
}

/// <summary>
/// What a summon knows about the window the user was in (<see cref="ForegroundContext.TargetWindow"/>), read by
/// <see cref="MonitorLookup.DescribeTarget"/> for <see cref="FlyoutPositioner.ChooseScreen"/>.
/// </summary>
/// <param name="Bounds">
/// The window's rectangle on screen (invisible resize borders included); <see langword="null"/> without a window, for a
/// minimized one (it sits at -32000), or when it could not be read.
/// </param>
/// <param name="MarksScreen">
/// Whether the window says which screen the user works on: it is visible, not minimized, and not the desktop, whose
/// window spans every monitor. <see langword="false"/> without a window.
/// </param>
public readonly record struct TargetWindowFacts(ScreenRect? Bounds, bool MarksScreen);

/// <summary>
/// Monitor geometry lookups for flyout placement.
/// </summary>
public static class MonitorLookup
{
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>
    /// The class names of the desktop's own windows: <c>Progman</c> (the shell window), and <c>WorkerW</c>, which holds the
    /// desktop icons while a wallpaper slideshow or an animated wallpaper runs. A click on the desktop of any monitor makes
    /// one of them the foreground window, and each spans every monitor.
    /// </summary>
    private static readonly string[] DesktopWindowClasses = ["Progman", "WorkerW"];

    /// <summary>
    /// Work area and scale factor of the monitor a summon chose (<see cref="FlyoutPositioner.ChooseScreen"/>).
    /// </summary>
    /// <param name="screen">The choice.</param>
    /// <returns>The work area (excludes the taskbar) and the scale factor (1.0 = 96 DPI).</returns>
    public static (ScreenRect WorkArea, double Scale) FromScreen(PanelScreen screen) => screen.Kind switch
    {
        PanelScreenKind.Main => Main(),
        PanelScreenKind.Window when screen.Window != 0 => FromWindow(screen.Window),
        _ => FromPoint(screen.Point),
    };

    /// <summary>
    /// Work area and scale factor of the main monitor (Windows' "main display"). Its top-left corner is the origin of
    /// screen coordinates by definition, so the monitor at (0, 0) is it; <c>MONITOR_DEFAULTTOPRIMARY</c> covers the
    /// moment while displays change.
    /// </summary>
    /// <returns>The work area and scale factor.</returns>
    public static (ScreenRect WorkArea, double Scale) Main() =>
        Describe(MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY));

    /// <summary>
    /// Reads what <see cref="FlyoutPositioner.ChooseScreen"/> needs to know about the window the user was in: where it is,
    /// and whether it marks a screen (<see cref="TargetWindowFacts"/>).
    /// </summary>
    /// <remarks>
    /// Read at the summon, after the shortcut: a few calls of microseconds each, on the UI thread, before the panel moves.
    /// A window that closed meanwhile is described as no window at all.
    /// </remarks>
    /// <param name="window">The window (<see cref="ForegroundContext.TargetWindow"/>); 0 for none.</param>
    /// <returns>The facts; <see langword="default"/> (no bounds, marks nothing) for 0 or a window that no longer exists.</returns>
    public static TargetWindowFacts DescribeTarget(nint window)
    {
        if (window == 0 || !IsWindow(window))
        {
            return default;
        }

        if (IsIconic(window))
        {
            // Minimized: its rectangle is off every screen, and the user is not looking at it.
            return new TargetWindowFacts(null, false);
        }

        ScreenRect? bounds = GetWindowRect(window, out var rect) ? new ScreenRect(rect.Left, rect.Top, rect.Right, rect.Bottom) : null;
        bool marksScreen = IsWindowVisible(window) && !IsDesktopWindowClass(ClassNameOf(window));
        return new TargetWindowFacts(bounds, marksScreen);
    }

    /// <summary>
    /// Whether a window class is one of the desktop's own (<c>Progman</c>, <c>WorkerW</c>), whose windows span every monitor
    /// and so say nothing about which screen the user works on.
    /// </summary>
    /// <param name="className">The class name; <see langword="null"/> or empty for an unreadable one.</param>
    /// <returns><see langword="true"/> for a desktop class, ignoring case as Windows does for class names.</returns>
    public static bool IsDesktopWindowClass(string? className) =>
        className is not null && DesktopWindowClasses.Contains(className, StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads a window's class name.</summary>
    /// <param name="window">The window.</param>
    /// <returns>The class name, or an empty string when it cannot be read.</returns>
    private static unsafe string ClassNameOf(nint window)
    {
        // 256 characters is the longest class name Windows registers.
        char* buffer = stackalloc char[256];
        int length = GetClassName(window, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    /// <summary>
    /// Work area and scale factor of the monitor containing (or nearest to) <paramref name="point"/>.
    /// </summary>
    /// <param name="point">Screen point in physical pixels.</param>
    /// <returns>The work area (excludes the taskbar) and the scale factor (1.0 = 96 DPI).</returns>
    public static (ScreenRect WorkArea, double Scale) FromPoint(ScreenPoint point)
    {
        nint monitor = MonitorFromPoint(new POINT { X = point.X, Y = point.Y }, MONITOR_DEFAULTTONEAREST);
        return Describe(monitor);
    }

    /// <summary>
    /// Work area and scale factor of the monitor showing most of <paramref name="hwnd"/>.
    /// </summary>
    /// <param name="hwnd">A window.</param>
    /// <returns>The work area and scale factor.</returns>
    public static (ScreenRect WorkArea, double Scale) FromWindow(nint hwnd) => Describe(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));

    /// <summary>Reads a monitor's work area and effective DPI.</summary>
    /// <param name="monitor">Monitor handle.</param>
    /// <returns>The work area and scale (falls back to 1.0 when the DPI query fails).</returns>
    private static (ScreenRect WorkArea, double Scale) Describe(nint monitor)
    {
        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return (new ScreenRect(0, 0, 1280, 720), 1.0);
        }

        double scale = GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
        var work = info.rcWork;
        return (new ScreenRect(work.Left, work.Top, work.Right, work.Bottom), scale);
    }
}
