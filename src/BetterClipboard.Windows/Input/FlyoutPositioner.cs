using BetterClipboard.Core.Settings;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Input;

/// <summary>
/// Computes where the flyout goes (physical pixels), Win+V style: just below the text caret, flipped
/// above when there is no room, always fully inside the monitor's work area (never under the taskbar).
/// </summary>
/// <remarks>
/// Pure function so placement rules are unit-tested without windows or monitors. Sizes are in physical
/// pixels — callers scale the DIP size by the target monitor's DPI first (<see cref="MonitorLookup"/>).
/// </remarks>
public static class FlyoutPositioner
{
    /// <summary>Horizontal nudge so the caret stays visible just inside the flyout's left edge.</summary>
    private const int CaretInset = 16;

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
}

/// <summary>
/// Monitor geometry lookups for flyout placement.
/// </summary>
public static class MonitorLookup
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;

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
