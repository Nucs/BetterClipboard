using BetterClipboard.Windows.Input;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests for dragging the flyout by its background: the tracker's threshold, arithmetic and cancel
/// point, and the pointer-position lookup's failure mode.
/// </summary>
public sealed class WindowDragTests
{
    /// <summary>
    /// Within the threshold nothing moves (a click stays a click). Past it the window follows the pointer's
    /// full offset from the press, with no jump by the threshold amount, and keeps following even when the
    /// pointer comes back inside the threshold.
    /// </summary>
    [Fact]
    public void Tracker_FollowsPastThreshold()
    {
        var tracker = new WindowDragTracker(thresholdPixels: 10);
        Assert.Null(tracker.Update(new ScreenPoint(5, 5))); // not tracking yet

        tracker.Begin(pointer: new ScreenPoint(500, 300), window: new ScreenPoint(400, 200));
        Assert.True(tracker.IsTracking);
        Assert.Null(tracker.Update(new ScreenPoint(510, 290)));
        Assert.False(tracker.IsMoving);

        Assert.Equal(new ScreenPoint(411, 200), tracker.Update(new ScreenPoint(511, 300)));
        Assert.True(tracker.IsMoving);
        Assert.Equal(new ScreenPoint(402, 201), tracker.Update(new ScreenPoint(502, 301)));

        // Onto a monitor left of / above the primary one: negative screen coordinates are fine.
        Assert.Equal(new ScreenPoint(-700, -80), tracker.Update(new ScreenPoint(-600, 20)));

        // Esc puts the window back where the drag began.
        Assert.Equal(new ScreenPoint(400, 200), tracker.WindowStart);

        Assert.True(tracker.End());
        Assert.False(tracker.IsTracking);
        Assert.Null(tracker.Update(new ScreenPoint(900, 900)));
        Assert.False(tracker.End()); // nothing was moved in a drag that is not running
    }

    /// <summary>A click (press and release without travel) reports that nothing moved.</summary>
    [Fact]
    public void Tracker_ClickDoesNotMove()
    {
        var tracker = new WindowDragTracker(thresholdPixels: -3); // clamped to 0: any travel moves
        Assert.Equal(0, tracker.ThresholdPixels);
        tracker.Begin(new ScreenPoint(10, 10), new ScreenPoint(0, 0));
        Assert.Null(tracker.Update(new ScreenPoint(10, 10)));
        Assert.False(tracker.End());
    }

    /// <summary>
    /// A pointer id Windows has no frame for yields "unknown", so the flyout starts no drag instead of
    /// guessing a position; the cursor itself is always readable on the interactive desktop.
    /// </summary>
    [Fact]
    public void ScreenPointer_UnknownPointerIsUnknown()
    {
        Assert.False(ScreenPointer.TryGetPointer(0xFFFF_FFF0, out var position));
        Assert.Equal(default, position);
        Assert.NotNull(ScreenPointer.Cursor());
    }
}
