namespace BetterClipboard.Core.Presentation;

/// <summary>
/// Where one filter tab sits in the panel's tab strip: its left and right edge in DIPs, measured in the strip's content
/// (from the first tab's left edge), so the values do not change while the strip scrolls.
/// </summary>
/// <param name="Left">The tab's left edge.</param>
/// <param name="Right">The tab's right edge (not before <paramref name="Left"/>).</param>
public readonly record struct TabExtent(double Left, double Right)
{
    /// <summary>The tab's width in DIPs.</summary>
    public double Width => Right - Left;
}

/// <summary>
/// The arithmetic of the panel's filter-tab carousel: the tabs (All, Pinned, Text, …) scroll sideways when they do not
/// fit the panel's width, with a "&lt;" arrow at the left edge while there is more to the left and a "&gt;" arrow at the
/// right edge while there is more to the right. Every method answers "which scroll offset next" for one gesture: an arrow
/// press, a wheel turn, a drag, or a tab that must become visible.
/// </summary>
/// <remarks>
/// <para>
/// Pure and free of UI types, so the rules are unit-tested; the panel measures the tabs and the viewport, asks here, and
/// scrolls. All values are DIPs. <c>offset</c> is the strip's horizontal scroll offset (0 = the first tab at the left
/// edge), <c>scrollableWidth</c> the largest offset (content width − viewport width, 0 when every tab fits).
/// </para>
/// <para>
/// <b>Arrows cover the strip's edges.</b> An arrow that shows takes <c>arrowWidth</c> DIPs at its edge, and the strip is
/// clipped there, so a tab is only "visible" when it lies wholly between the arrows that show. Arrows show by
/// <see cref="CanScrollBack"/> / <see cref="CanScrollForward"/> alone, so the obstructed width at any offset is known
/// before scrolling there — <see cref="Step"/> and <see cref="Reveal"/> place a tab right next to the arrow that will
/// still show, and snap to the strip's end when what is left beyond the tab is narrower than an arrow (stopping short
/// would leave an arrow showing for a sliver of nothing).
/// </para>
/// <para>
/// <b>Footgun:</b> every result is clamped to <c>0..scrollableWidth</c>, and a negative or NaN <c>scrollableWidth</c>
/// counts as 0 (nothing to scroll), so callers can pass a viewer's raw values during layout without checks of their own.
/// </para>
/// </remarks>
public static class TabStripScroll
{
    /// <summary>
    /// How close (in DIPs) to an end of the strip counts as at that end. Layout rounding at 125 % and 150 % leaves a
    /// fraction of a DIP between the largest reachable offset and <c>scrollableWidth</c>; without the tolerance the
    /// "&gt;" arrow would stay up at the very end.
    /// </summary>
    public const double EdgeTolerance = 0.5;

    /// <summary>
    /// The wheel delta of one notch of a standard mouse wheel (Win32 <c>WHEEL_DELTA</c>); precision wheels and touchpads
    /// report fractions of it.
    /// </summary>
    public const int WheelDelta = 120;

    /// <summary>
    /// How far one wheel notch scrolls the strip, in DIPs: about one tab (the labels are 45–83 DIPs wide with their
    /// padding), so turning the wheel walks through the tabs instead of jumping past several.
    /// </summary>
    public const double WheelNotchDip = 60;

    /// <summary>Keeps an offset inside the strip.</summary>
    /// <param name="offset">Any offset (NaN counts as 0).</param>
    /// <param name="scrollableWidth">The largest offset (negative or NaN counts as 0).</param>
    /// <returns><paramref name="offset"/> clamped to <c>0..scrollableWidth</c>.</returns>
    public static double Clamp(double offset, double scrollableWidth)
    {
        double max = Max(scrollableWidth);
        return double.IsNaN(offset) ? 0 : Math.Clamp(offset, 0, max);
    }

    /// <summary>Whether the "&lt;" arrow shows: the strip is scrolled away from its left end.</summary>
    /// <param name="offset">The strip's offset.</param>
    /// <returns><see langword="true"/> while more than <see cref="EdgeTolerance"/> is hidden on the left.</returns>
    public static bool CanScrollBack(double offset) => offset > EdgeTolerance;

    /// <summary>Whether the "&gt;" arrow shows: the strip is not at its right end.</summary>
    /// <param name="offset">The strip's offset.</param>
    /// <param name="scrollableWidth">The largest offset (0 when every tab fits: then never).</param>
    /// <returns><see langword="true"/> while more than <see cref="EdgeTolerance"/> is hidden on the right.</returns>
    public static bool CanScrollForward(double offset, double scrollableWidth) => offset < Max(scrollableWidth) - EdgeTolerance;

    /// <summary>
    /// The offset after one press of an arrow: the next tab beyond the arrow that was pressed becomes wholly visible, right
    /// next to that arrow (or the strip reaches its end) — the carousel moves by tabs, not by a fixed distance.
    /// </summary>
    /// <param name="tabs">The visible tabs' extents, in any order (collapsed tabs are left out by the caller).</param>
    /// <param name="offset">The strip's offset now (or the target of a scroll still running, so held arrows keep stepping).</param>
    /// <param name="viewportWidth">The strip's visible width, arrows included.</param>
    /// <param name="scrollableWidth">The largest offset.</param>
    /// <param name="arrowWidth">The width each arrow covers at its edge while it shows.</param>
    /// <param name="forward"><see langword="true"/> for "&gt;" (towards the last tab), <see langword="false"/> for "&lt;".</param>
    /// <returns>
    /// The new offset, clamped. When no tab is hidden that way, the strip's end in that direction. Always the current
    /// offset when nothing can scroll that way.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="tabs"/> is <see langword="null"/>.</exception>
    public static double Step(IReadOnlyList<TabExtent> tabs, double offset, double viewportWidth, double scrollableWidth, double arrowWidth, bool forward)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        double max = Max(scrollableWidth);
        offset = Clamp(offset, max);
        arrowWidth = Math.Max(0, arrowWidth);
        if (forward)
        {
            if (!CanScrollForward(offset, max))
            {
                return offset;
            }

            // The "<" arrow shows from the first DIP scrolled, the ">" arrow until the end: both cover their edge now.
            double visibleRight = offset + viewportWidth - arrowWidth;
            TabExtent? next = null;
            foreach (var tab in tabs)
            {
                if (tab.Right > visibleRight + EdgeTolerance && (next is null || tab.Left < next.Value.Left))
                {
                    next = tab;
                }
            }

            // Nothing hidden behind the arrow (a gap, or rounding): the end is all that is left.
            return next is { } target ? Forward(target, viewportWidth, max, arrowWidth) : max;
        }

        if (!CanScrollBack(offset))
        {
            return offset;
        }

        double visibleLeft = offset + arrowWidth;
        TabExtent? previous = null;
        foreach (var tab in tabs)
        {
            if (tab.Left < visibleLeft - EdgeTolerance && (previous is null || tab.Right > previous.Value.Right))
            {
                previous = tab;
            }
        }

        return previous is { } back ? Back(back, max, arrowWidth) : 0;
    }

    /// <summary>
    /// The offset that shows one tab wholly between the arrows (a tab selected from code, or clicked while partly under an
    /// arrow), moving the strip as little as possible.
    /// </summary>
    /// <param name="tab">The tab's extent.</param>
    /// <param name="offset">The strip's offset now.</param>
    /// <param name="viewportWidth">The strip's visible width, arrows included.</param>
    /// <param name="scrollableWidth">The largest offset.</param>
    /// <param name="arrowWidth">The width each arrow covers at its edge while it shows.</param>
    /// <returns>
    /// <paramref name="offset"/> (clamped) when the tab is already wholly visible; otherwise the offset that puts it
    /// right next to the arrow on the side it was hidden on, snapped to the strip's end when less than an arrow is left.
    /// </returns>
    public static double Reveal(TabExtent tab, double offset, double viewportWidth, double scrollableWidth, double arrowWidth)
    {
        double max = Max(scrollableWidth);
        offset = Clamp(offset, max);
        arrowWidth = Math.Max(0, arrowWidth);
        double visibleLeft = offset + (CanScrollBack(offset) ? arrowWidth : 0);
        double visibleRight = offset + viewportWidth - (CanScrollForward(offset, max) ? arrowWidth : 0);
        if (tab.Left < visibleLeft - EdgeTolerance)
        {
            return Back(tab, max, arrowWidth);
        }

        if (tab.Right > visibleRight + EdgeTolerance)
        {
            return Forward(tab, viewportWidth, max, arrowWidth);
        }

        return offset;
    }

    /// <summary>
    /// The offset after a turn of the mouse wheel over the strip. The usual (vertical) wheel scrolls it too, since the
    /// strip has nothing to scroll up and down: turning it down (towards the user) moves on to later tabs, like a
    /// horizontal list in Explorer. A tilt wheel scrolls the way it is tilted.
    /// </summary>
    /// <param name="offset">The strip's offset now (or the target of a scroll still running, so quick notches add up).</param>
    /// <param name="delta">
    /// The wheel delta: <see cref="WheelDelta"/> per notch, positive for away from the user (vertical wheel) or for a
    /// tilt to the right (horizontal wheel).
    /// </param>
    /// <param name="horizontalWheel">Whether the delta comes from a tilt (horizontal) wheel.</param>
    /// <param name="scrollableWidth">The largest offset.</param>
    /// <returns>The new offset, clamped: <see cref="WheelNotchDip"/> per notch.</returns>
    public static double Wheel(double offset, int delta, bool horizontalWheel, double scrollableWidth)
    {
        double distance = delta / (double)WheelDelta * WheelNotchDip;
        return Clamp(horizontalWheel ? offset + distance : offset - distance, scrollableWidth);
    }

    /// <summary>
    /// The offset while the strip is dragged sideways with the mouse ("grab and pull"): the content follows the pointer,
    /// so dragging to the left reveals the tabs on the right.
    /// </summary>
    /// <param name="startOffset">The strip's offset when the drag began.</param>
    /// <param name="pointerTravel">How far the pointer moved right since the press, in DIPs (negative = left).</param>
    /// <param name="scrollableWidth">The largest offset.</param>
    /// <returns>The new offset, clamped (the content stops at the ends while the pointer moves on).</returns>
    public static double Drag(double startOffset, double pointerTravel, double scrollableWidth) =>
        Clamp(startOffset - pointerTravel, scrollableWidth);

    /// <summary>The offset that puts <paramref name="tab"/>'s right edge next to the "&gt;" arrow (or the strip's end).</summary>
    /// <param name="tab">The tab to show.</param>
    /// <param name="viewportWidth">The strip's visible width.</param>
    /// <param name="max">The largest offset (already validated).</param>
    /// <param name="arrowWidth">The arrows' width (already validated).</param>
    /// <returns>The offset, clamped; the end when less than an arrow would be left beyond it.</returns>
    private static double Forward(TabExtent tab, double viewportWidth, double max, double arrowWidth)
    {
        // At this offset the ">" arrow still shows (more is hidden on the right), so the tab must end before it.
        double target = tab.Right + arrowWidth - viewportWidth;
        return target > max - arrowWidth ? max : Clamp(target, max);
    }

    /// <summary>The offset that puts <paramref name="tab"/>'s left edge next to the "&lt;" arrow (or the strip's start).</summary>
    /// <param name="tab">The tab to show.</param>
    /// <param name="max">The largest offset (already validated).</param>
    /// <param name="arrowWidth">The arrows' width (already validated).</param>
    /// <returns>The offset, clamped; 0 when less than an arrow would be left before it.</returns>
    private static double Back(TabExtent tab, double max, double arrowWidth)
    {
        // At this offset the "<" arrow still shows, so the tab must start after it.
        double target = tab.Left - arrowWidth;
        return target < arrowWidth ? 0 : Clamp(target, max);
    }

    /// <summary>A usable largest offset: negative, NaN or infinite values (a strip not laid out yet) count as 0.</summary>
    /// <param name="scrollableWidth">The raw value.</param>
    /// <returns>The value, or 0.</returns>
    private static double Max(double scrollableWidth) =>
        double.IsFinite(scrollableWidth) && scrollableWidth > 0 ? scrollableWidth : 0;
}
