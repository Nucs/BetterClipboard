using BetterClipboard.Core.Presentation;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the filter-tab carousel's arithmetic (<see cref="TabStripScroll"/>): when the arrows show, where an arrow press,
/// a wheel turn, a drag and a reveal scroll to, and that every result stays inside the strip.
/// </summary>
/// <remarks>
/// The strip used throughout is the real panel's nine tabs at their measured widths (All 41 … Everything 83 DIPs, 506 in
/// all) in a 300-DIP viewport with 24-DIP arrows, so the largest offset is 206.
/// </remarks>
public sealed class TabStripScrollTests
{
    /// <summary>The arrows' width, as in <c>TabScrollButtonStyle</c>.</summary>
    private const double Arrow = 24;

    /// <summary>The strip's visible width in these tests.</summary>
    private const double Viewport = 300;

    /// <summary>All, Pinned, Text, Images, Links, Files, ShareX, Run, Everything, edge to edge.</summary>
    private static readonly TabExtent[] Tabs = Strip(41, 64, 47, 66, 52, 49, 61, 43, 83);

    /// <summary>The largest offset: content (506) − viewport (300).</summary>
    private static readonly double Scrollable = Tabs[^1].Right - Viewport;

    /// <summary>
    /// "&lt;" shows once the strip left its start, "&gt;" until it reaches its end, both with half a DIP of tolerance for
    /// layout rounding; with every tab in view (or before layout: 0, negative, NaN) neither shows.
    /// </summary>
    [Fact]
    public void Arrows_ShowOnlyWhileThereIsMoreThatWay()
    {
        Assert.False(TabStripScroll.CanScrollBack(0));
        Assert.False(TabStripScroll.CanScrollBack(0.4));
        Assert.True(TabStripScroll.CanScrollBack(0.6));

        Assert.True(TabStripScroll.CanScrollForward(0, 206));
        Assert.True(TabStripScroll.CanScrollForward(205.4, 206));
        Assert.False(TabStripScroll.CanScrollForward(205.6, 206));
        Assert.False(TabStripScroll.CanScrollForward(206, 206));

        Assert.False(TabStripScroll.CanScrollForward(0, 0));
        Assert.False(TabStripScroll.CanScrollForward(0, -12));
        Assert.False(TabStripScroll.CanScrollForward(0, double.NaN));
    }

    /// <summary>
    /// "&gt;" walks the strip tab by tab: each press shows the next tab hidden behind the arrow right next to it, and the
    /// last press snaps to the end instead of stopping a sliver short (which would leave the arrow up for nothing).
    /// </summary>
    [Fact]
    public void Step_Forward_RevealsTheNextTabEachTime()
    {
        double[] expected = [43, 104, 147, 206, 206];
        double offset = 0;
        foreach (var next in expected)
        {
            offset = TabStripScroll.Step(Tabs, offset, Viewport, Scrollable, Arrow, forward: true);
            Assert.Equal(next, offset, 6);
        }

        // At 43 the fifth tab ("Files", 270..319) ends exactly where the ">" arrow begins.
        Assert.Equal(Tabs[5].Right, 43 + Viewport - Arrow, 6);
    }

    /// <summary>
    /// "&lt;" walks back the same way: a tab half under the "&lt;" arrow is shown first, and the strip snaps to its start
    /// when less than an arrow's width would be left before the tab.
    /// </summary>
    [Fact]
    public void Step_Back_RevealsThePreviousTabEachTime()
    {
        double[] expected = [194, 128, 81, 0, 0];
        double offset = Scrollable;
        foreach (var previous in expected)
        {
            offset = TabStripScroll.Step(Tabs, offset, Viewport, Scrollable, Arrow, forward: false);
            Assert.Equal(previous, offset, 6);
        }
    }

    /// <summary>The tabs may come in any order: the step looks for the nearest hidden tab, not the next in the list.</summary>
    [Fact]
    public void Step_DoesNotDependOnTheTabsOrder()
    {
        var shuffled = new[] { Tabs[7], Tabs[2], Tabs[8], Tabs[0], Tabs[5], Tabs[3], Tabs[1], Tabs[6], Tabs[4] };
        Assert.Equal(43, TabStripScroll.Step(shuffled, 0, Viewport, Scrollable, Arrow, forward: true), 6);
        Assert.Equal(194, TabStripScroll.Step(shuffled, Scrollable, Viewport, Scrollable, Arrow, forward: false), 6);
    }

    /// <summary>
    /// With no tab hidden that way (a gap, or rounding) a press goes to that end; with nothing to scroll it stays put; and
    /// an offset outside the strip is clamped first.
    /// </summary>
    [Fact]
    public void Step_EndsAndOutOfRangeOffsets()
    {
        Assert.Equal(100, TabStripScroll.Step([], 0, Viewport, 100, Arrow, forward: true));
        Assert.Equal(0, TabStripScroll.Step([], 50, Viewport, 100, Arrow, forward: false));
        Assert.Equal(0, TabStripScroll.Step(Tabs, 0, Viewport, 0, Arrow, forward: true));
        Assert.Equal(0, TabStripScroll.Step(Tabs, -40, Viewport, Scrollable, Arrow, forward: false));
        Assert.Equal(Scrollable, TabStripScroll.Step(Tabs, 900, Viewport, Scrollable, Arrow, forward: true));
        Assert.Throws<ArgumentNullException>(() => TabStripScroll.Step(null!, 0, Viewport, Scrollable, Arrow, forward: true));
    }

    /// <summary>
    /// A tab selected from code scrolls into view as little as needed, clear of the arrows; a tab already wholly visible
    /// leaves the strip where it is, and at an end the arrow that is gone no longer counts.
    /// </summary>
    [Fact]
    public void Reveal_ScrollsAsLittleAsNeeded()
    {
        // "Run" (380..423) from the start: its right edge goes next to the ">" arrow.
        Assert.Equal(147, TabStripScroll.Reveal(Tabs[7], 0, Viewport, Scrollable, Arrow), 6);

        // "All" from the middle: back to the start (less than an arrow would be left before it).
        Assert.Equal(0, TabStripScroll.Reveal(Tabs[0], 147, Viewport, Scrollable, Arrow), 6);

        // "Images" (152..218) is wholly between the arrows at 100 (124..376): nothing moves.
        Assert.Equal(100, TabStripScroll.Reveal(Tabs[3], 100, Viewport, Scrollable, Arrow), 6);

        // "Everything" from 147: to the end, where it is wholly visible because the ">" arrow is gone there.
        Assert.Equal(Scrollable, TabStripScroll.Reveal(Tabs[8], 147, Viewport, Scrollable, Arrow), 6);
        Assert.Equal(Scrollable, TabStripScroll.Reveal(Tabs[8], Scrollable, Viewport, Scrollable, Arrow), 6);

        // "Links" (218..270) half under the "<" arrow at 206 (visible from 230): just enough to clear it.
        Assert.Equal(194, TabStripScroll.Reveal(Tabs[4], Scrollable, Viewport, Scrollable, Arrow), 6);
    }

    /// <summary>
    /// The usual wheel scrolls the strip too: down (a negative delta) moves on to later tabs, up goes back; a tilt wheel
    /// goes the way it is tilted. One notch is <see cref="TabStripScroll.WheelNotchDip"/>, fractions scale, ends clamp.
    /// </summary>
    [Fact]
    public void Wheel_ScrollsByNotches()
    {
        Assert.Equal(60, TabStripScroll.Wheel(0, -120, horizontalWheel: false, Scrollable));
        Assert.Equal(0, TabStripScroll.Wheel(0, 120, horizontalWheel: false, Scrollable));
        Assert.Equal(40, TabStripScroll.Wheel(100, 120, horizontalWheel: false, Scrollable));
        Assert.Equal(Scrollable, TabStripScroll.Wheel(180, -120, horizontalWheel: false, Scrollable)); // 240, clamped to 206
        Assert.Equal(60, TabStripScroll.Wheel(0, 120, horizontalWheel: true, Scrollable));
        Assert.Equal(40, TabStripScroll.Wheel(100, -120, horizontalWheel: true, Scrollable));
        Assert.Equal(15, TabStripScroll.Wheel(0, -30, horizontalWheel: false, Scrollable));
        Assert.Equal(0, TabStripScroll.Wheel(0, -120, horizontalWheel: false, scrollableWidth: 0));
    }

    /// <summary>A drag pulls the content with the pointer: dragging left shows later tabs; the strip stops at its ends.</summary>
    [Fact]
    public void Drag_FollowsThePointer()
    {
        Assert.Equal(150, TabStripScroll.Drag(100, -50, Scrollable));
        Assert.Equal(60, TabStripScroll.Drag(100, 40, Scrollable));
        Assert.Equal(0, TabStripScroll.Drag(100, 150, Scrollable));
        Assert.Equal(Scrollable, TabStripScroll.Drag(100, -500, Scrollable));
    }

    /// <summary>Every offset stays inside the strip, also for values a viewer reports before its first layout.</summary>
    [Fact]
    public void Clamp_KeepsOffsetsInsideTheStrip()
    {
        Assert.Equal(0, TabStripScroll.Clamp(double.NaN, 100));
        Assert.Equal(0, TabStripScroll.Clamp(-5, 100));
        Assert.Equal(100, TabStripScroll.Clamp(150, 100));
        Assert.Equal(42, TabStripScroll.Clamp(42, 100));
        Assert.Equal(0, TabStripScroll.Clamp(5, -1));
        Assert.Equal(0, TabStripScroll.Clamp(50, double.NaN));
        Assert.Equal(0, TabStripScroll.Clamp(50, double.PositiveInfinity));
        Assert.Equal(41, Tabs[0].Width);
    }

    /// <summary>Lays tabs of the given widths edge to edge from 0, like the SelectorBar does.</summary>
    /// <param name="widths">The tabs' widths in DIPs, in order.</param>
    /// <returns>Their extents.</returns>
    private static TabExtent[] Strip(params double[] widths)
    {
        var tabs = new TabExtent[widths.Length];
        double left = 0;
        for (int i = 0; i < widths.Length; i++)
        {
            tabs[i] = new TabExtent(left, left + widths[i]);
            left += widths[i];
        }

        return tabs;
    }
}
