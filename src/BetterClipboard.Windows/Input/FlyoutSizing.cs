using BetterClipboard.Core.Settings;

namespace BetterClipboard.Windows.Input;

/// <summary>
/// Converts the panel's remembered size (<see cref="AppSettings.FlyoutWidth"/> / <see cref="AppSettings.FlyoutHeight"/>,
/// DIPs without the groups column) to and from the window's physical pixels on a monitor of a given scale.
/// </summary>
/// <remarks>
/// <para>
/// Pure, so the rules are unit-tested next to <see cref="FlyoutPositioner"/>. All window sizes are outer sizes (frame
/// included), the same as <c>AppWindow.Size</c> and <c>MoveAndResize</c> use, so a size read back after a resize and
/// applied at the next summon gives the same window.
/// </para>
/// <para>
/// <b>The groups column is not part of the remembered width.</b> While it is open the window is
/// <c>extraWidthDip</c> wider on its left (<see cref="FlyoutPositioner.ExtendLeft"/>); a width read from such a window is
/// stored without it, and added again when it is applied, so opening or closing the column never changes what the user
/// chose for the list.
/// </para>
/// <para>
/// <b>Rounding:</b> whole DIPs are stored. At 125 % or 150 % a pixel size can fall between two DIPs, so a size can come
/// back one pixel off after a save — invisible, and it never drifts further: at any Windows scale (100 % and up),
/// converting the stored DIPs to pixels and back gives the same DIPs (the pixel rounding is at most half a pixel, less
/// than half a DIP).
/// </para>
/// </remarks>
public static class FlyoutSizing
{
    /// <summary>
    /// The size to remember for a window the user just resized, in whole DIPs, kept inside the panel's limits.
    /// </summary>
    /// <param name="widthPixels">The window's outer width in physical pixels.</param>
    /// <param name="heightPixels">The window's outer height in physical pixels.</param>
    /// <param name="scale">The window's scale (1.0 = 96 DPI); 0, negative or NaN count as 1.</param>
    /// <param name="extraWidthDip">How much of the width is the groups column (0 while it is closed).</param>
    /// <returns>
    /// The width without the column and the height, clamped to <see cref="AppSettings.MinFlyoutWidth"/> /
    /// <see cref="AppSettings.MinFlyoutHeight"/> .. <see cref="AppSettings.MaxFlyoutSize"/> — the same bounds
    /// <see cref="AppSettings.Normalize"/> applies when loading.
    /// </returns>
    public static (int Width, int Height) ToDips(int widthPixels, int heightPixels, double scale, double extraWidthDip)
    {
        scale = Usable(scale);
        int width = (int)Math.Round((widthPixels / scale) - Math.Max(0, extraWidthDip));
        int height = (int)Math.Round(heightPixels / scale);
        return (Math.Clamp(width, AppSettings.MinFlyoutWidth, AppSettings.MaxFlyoutSize),
                Math.Clamp(height, AppSettings.MinFlyoutHeight, AppSettings.MaxFlyoutSize));
    }

    /// <summary>The window size in physical pixels for a remembered size on a monitor of <paramref name="scale"/>.</summary>
    /// <param name="widthDip">The remembered width (without the groups column).</param>
    /// <param name="heightDip">The remembered height.</param>
    /// <param name="scale">The monitor's scale (1.0 = 96 DPI); 0, negative or NaN count as 1.</param>
    /// <param name="extraWidthDip">The groups column's width while it is open, else 0 (the window grows by it).</param>
    /// <returns>
    /// The outer size in pixels, rounded. Not clamped to any monitor: <see cref="FlyoutPositioner.Compute"/> shrinks it to
    /// the work area of the monitor it opens on, without changing what is remembered.
    /// </returns>
    public static (int Width, int Height) ToPixels(int widthDip, int heightDip, double scale, double extraWidthDip)
    {
        scale = Usable(scale);
        return ((int)Math.Round((widthDip + Math.Max(0, extraWidthDip)) * scale), (int)Math.Round(heightDip * scale));
    }

    /// <summary>
    /// The smallest window Windows may let the user drag the panel to, in physical pixels (the <c>WM_GETMINMAXINFO</c>
    /// minimum track size): the panel's minimum DIPs on the window's current monitor, plus the groups column while open.
    /// </summary>
    /// <param name="scale">The window's current scale (1.0 = 96 DPI); 0, negative or NaN count as 1.</param>
    /// <param name="extraWidthDip">The groups column's width while it is open, else 0.</param>
    /// <returns>The minimum outer width and height in pixels, rounded up so the minimum DIPs always fit.</returns>
    /// <remarks>
    /// Computed per call from the scale the window has then, unlike <c>OverlappedPresenter.PreferredMinimumWidth</c>,
    /// which takes raw pixels and keeps them when the window moves to a monitor with another scale (microsoft-ui-xaml
    /// issues 10452 and 10475).
    /// </remarks>
    public static (int Width, int Height) MinimumTrackSize(double scale, double extraWidthDip)
    {
        scale = Usable(scale);
        return ((int)Math.Ceiling((AppSettings.MinFlyoutWidth + Math.Max(0, extraWidthDip)) * scale),
                (int)Math.Ceiling(AppSettings.MinFlyoutHeight * scale));
    }

    /// <summary>A scale usable as a divisor: anything not positive and finite counts as 1.0 (96 DPI).</summary>
    /// <param name="scale">The raw scale.</param>
    /// <returns>The scale, or 1.</returns>
    private static double Usable(double scale) => double.IsFinite(scale) && scale > 0 ? scale : 1.0;
}
