using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Settings;
using BetterClipboard.Windows.Input;
using Windows.Graphics;

namespace BetterClipboard.App.Views;

/// <summary>
/// Where a summon puts the panel (Settings › Shortcut › Panel position, <see cref="AppSettings.Placement"/>): next to the
/// text cursor (the default), next to the mouse, or centered on the main screen or on the screen the user is typing on —
/// and putting it there at exactly the size asked for, also on a monitor with another scale.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which screen, then where on it.</b> <see cref="FlyoutPositioner.ChooseScreen"/> picks the monitor from the setting
/// and what the shortcut captured (caret, window, mouse; its remarks have the rule for "the current screen"),
/// <see cref="MonitorLookup.FromScreen"/> reads that monitor's work area and scale, and <see cref="FlyoutPositioner.Place"/>
/// puts the remembered size there: below the caret or the mouse with the groups column added on the left, or centered as
/// a whole.
/// </para>
/// <para>
/// <b>Monitors with different scales</b> (found 2026-11-08 on the owner's PC: two monitors at 100 %, one at 125 %).
/// When the program moves a window onto a monitor with another scale, Windows sends <c>WM_DPICHANGED</c> inside the move
/// itself, suggesting the asked rectangle scaled once more by the ratio of the two scales, and WinUI takes that
/// suggestion: asked for 500 × 700 on the 125 % monitor, the panel came out 625 × 875 (and 320 × 448 for 400 × 560 back on
/// a 100 % one), measured with <c>tools/probes/dpi_move_lab</c>. Every summon that changed monitors since the panel was
/// first placed by the program got the wrong size that way, in every placement. <see cref="MoveAndResizeExactly"/> asks a
/// second time when the bounds differ; the window is on the new monitor by then, so nothing rescales it again.
/// </para>
/// </remarks>
public sealed partial class ClipboardFlyout
{
    /// <summary>Whether a rescaled move was logged this session (<see cref="MoveAndResizeExactly"/> logs the first one only).</summary>
    private bool rescaleLogged;

    /// <summary>
    /// Where a summon puts the panel, in physical screen pixels: the chosen screen (<see cref="FlyoutPositioner.ChooseScreen"/>),
    /// the remembered size in that screen's pixels with the groups column while it is open, placed next to the caret or the
    /// mouse or centered (<see cref="FlyoutPositioner.Place"/>), never outside that screen's work area.
    /// </summary>
    /// <param name="context">The caret, mouse and window captured when the shortcut was pressed.</param>
    /// <param name="placement">The placement setting.</param>
    /// <param name="centerOnMainScreen">The main-screen setting (only a centered placement reads it).</param>
    /// <param name="groupsColumnOpen">Whether the groups column will show (it widens the panel on its left).</param>
    /// <returns>The bounds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    private RectInt32 SummonBounds(ForegroundContext context, FlyoutPlacement placement, bool centerOnMainScreen, bool groupsColumnOpen)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = controller.Settings.Current;

        // The window the user was in is described only when the rule can use it (centered on the current screen): a few
        // calls of microseconds, but nothing a summon next to the caret should pay for.
        var target = placement == FlyoutPlacement.CenterScreen && !centerOnMainScreen
            ? MonitorLookup.DescribeTarget(context.TargetWindow)
            : default;
        var screen = FlyoutPositioner.ChooseScreen(placement, centerOnMainScreen, context, target);
        var (workArea, scale) = MonitorLookup.FromScreen(screen);

        // The size the user last dragged the panel to (without the column, added in whole pixels by Place), in this
        // monitor's pixels; Place shrinks it to a smaller work area without changing what is remembered.
        var (width, height) = FlyoutSizing.ToPixels(settings.FlyoutWidth, settings.FlyoutHeight, scale, extraWidthDip: 0);
        int column = groupsColumnOpen ? (int)Math.Round(GroupsPaneDip * scale) : 0;
        var bounds = FlyoutPositioner.Place(placement, context, workArea, width, height, (int)Math.Round(GapDip * scale), column);
        return new RectInt32(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    /// <summary>
    /// Moves and resizes the panel's window to <paramref name="rect"/>, and asks again once when Windows changed the
    /// bounds on the way: a move onto a monitor with another scale makes WinUI rescale the window a second time (see the
    /// class remarks). The first such correction of a session is logged, content-free.
    /// </summary>
    /// <remarks>
    /// Asked twice at most: on the second call the window is already on its monitor, so nothing rescales it, and bounds that
    /// still differ come from a limit (the minimum size on a work area smaller than it), which a third call would not change.
    /// </remarks>
    /// <param name="rect">The bounds in physical screen pixels.</param>
    private void MoveAndResizeExactly(RectInt32 rect)
    {
        AppWindow.MoveAndResize(rect);
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        if (position.X == rect.X && position.Y == rect.Y && size.Width == rect.Width && size.Height == rect.Height)
        {
            return;
        }

        AppWindow.MoveAndResize(rect);
        if (!rescaleLogged)
        {
            rescaleLogged = true;
            var after = AppWindow.Size;
            AppLog.Info(
                $"The panel came out {size.Width}x{size.Height} instead of {rect.Width}x{rect.Height} (moved to a monitor with " +
                $"another scale?); its bounds were set again: {after.Width}x{after.Height}.");
        }
    }
}
