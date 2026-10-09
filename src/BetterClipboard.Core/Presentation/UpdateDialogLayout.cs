namespace BetterClipboard.Core.Presentation;

/// <summary>
/// Where the update dialog opens relative to its button, and how tall it may be there.
/// </summary>
/// <param name="Below"><see langword="true"/> to open under the button, <see langword="false"/> to open over it.</param>
/// <param name="MaxHeightDip">The most height the dialog may take on that side, in DIPs.</param>
public readonly record struct UpdateDialogPlacement(bool Below, double MaxHeightDip);

/// <summary>
/// Decides on which side of its button the update dialog opens: under it when there is room, else over it, and in
/// either case no taller than the room on that side — so the dialog stays attached to the button it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// Why the app decides instead of the framework: the panel opens wherever the caret or the pointer is, often in the
/// lower half of the screen. A flyout that does not fit on its preferred side is moved by WinUI to any side that takes
/// it whole — measured on a 900-pixel-high screen: to the left of the button and up against the top of the screen, away
/// from the button and over the panel's title. The dialog's release notes scroll, so it can simply be shorter instead.
/// </para>
/// <para>Pure arithmetic in DIPs, so it is unit-tested; the app measures the room and applies the answer.</para>
/// </remarks>
public static class UpdateDialogLayout
{
    /// <summary>The dialog's full height: its title, buttons and the whole height its release notes may take.</summary>
    public const double PreferredHeightDip = 600;

    /// <summary>
    /// The height at which the dialog is comfortable to read (the notes still show a dozen lines). With this much room
    /// under the button, the dialog opens there even when there is more room over it: under is where a button's bubble
    /// is expected.
    /// </summary>
    public const double ComfortableHeightDip = 440;

    /// <summary>
    /// The least height the dialog is given. On a screen with less room on both sides, the framework's own fallback
    /// places it wherever it fits.
    /// </summary>
    public const double MinimumHeightDip = 300;

    /// <summary>
    /// Room that is not the dialog's: the gap between the button and the bubble, and a little air to the edge of the
    /// screen's work area.
    /// </summary>
    public const double MarginDip = 16;

    /// <summary>
    /// Picks the side and the height.
    /// </summary>
    /// <param name="roomBelowDip">The distance from the button's bottom edge to the bottom of the work area, in DIPs.</param>
    /// <param name="roomAboveDip">The distance from the top of the work area to the button's top edge, in DIPs.</param>
    /// <returns>
    /// Under the button when that side is comfortable or the larger one; otherwise over it. The height is the room on
    /// that side less <see cref="MarginDip"/>, within <see cref="MinimumHeightDip"/> and <see cref="PreferredHeightDip"/>.
    /// Values that are not numbers count as no room.
    /// </returns>
    public static UpdateDialogPlacement Choose(double roomBelowDip, double roomAboveDip)
    {
        double below = Usable(roomBelowDip);
        double above = Usable(roomAboveDip);
        bool openBelow = below >= ComfortableHeightDip || below >= above;
        return new UpdateDialogPlacement(openBelow, Math.Clamp(openBelow ? below : above, MinimumHeightDip, PreferredHeightDip));
    }

    /// <summary>The room a side really offers the dialog.</summary>
    /// <param name="roomDip">The measured distance; may be negative (a button outside the work area) or not a number.</param>
    /// <returns>The room less the margin, never negative.</returns>
    private static double Usable(double roomDip) =>
        double.IsFinite(roomDip) ? Math.Max(0, roomDip - MarginDip) : 0;
}
