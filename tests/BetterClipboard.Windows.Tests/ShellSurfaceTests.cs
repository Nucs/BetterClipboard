using BetterClipboard.Windows.Input;
using BetterClipboard.Windows.Interop;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// The panel over Start, Search and the shell's other surfaces (CLAUDE.md §2.61): what each z-band means
/// (<see cref="ShellSurfaces.KindOfBand"/>, <see cref="ShellSurfaces.IsShellBand"/>), where the panel goes beside Start
/// (<see cref="FlyoutPositioner.Beside"/>), and the real band call on a window of this process. Nothing here sends a key:
/// <see cref="ShellSurfaces.CloseAsync"/> was verified in a test desktop only, since its Esc would reach the PC's own Start.
/// </summary>
public sealed class ShellSurfaceTests
{
    /// <summary>A 1920 × 1080 monitor at 100 % with the taskbar at the bottom.</summary>
    private static readonly ScreenRect Work = new(0, 0, 1920, 1032);

    /// <summary>Windows 11's Start on <see cref="Work"/>: 800 × 750, centered over the taskbar (as measured on 1600 × 900).</summary>
    private static readonly ScreenRect Start = new(560, 282, 1360, 1032);

    /// <summary>
    /// Each band measured on Windows 11 26200 (2026-11-20) gets its meaning, and every band not measured is an app window:
    /// Start (6) and Search (13) are search surfaces, the notification center and Quick settings (4) and Task View (5) are
    /// closed first; the desktop (1), the wallpaper's (12), the UIAccess band (2), the lock screen's (17, 18) and unknown ones
    /// are left alone, as before the bands were read.
    /// </summary>
    /// <param name="band">The band id.</param>
    /// <param name="expected">What it must mean.</param>
    [Theory]
    [MemberData(nameof(BandKinds))]
    public void KindOfBand_ActsOnlyOnTheMeasuredSurfaces(uint band, ShellSurfaceKind expected) =>
        Assert.Equal(expected, ShellSurfaces.KindOfBand(band));

    /// <summary>The cases of <see cref="KindOfBand_ActsOnlyOnTheMeasuredSurfaces"/>: band, meaning.</summary>
    public static TheoryData<uint, ShellSurfaceKind> BandKinds => new()
    {
        { 0, ShellSurfaceKind.None },
        { 1, ShellSurfaceKind.None },
        { 2, ShellSurfaceKind.None },
        { 3, ShellSurfaceKind.None },
        { 4, ShellSurfaceKind.Other },
        { 5, ShellSurfaceKind.Other },
        { 6, ShellSurfaceKind.Search },
        { 7, ShellSurfaceKind.None },
        { 12, ShellSurfaceKind.None },
        { 13, ShellSurfaceKind.Search },
        { 16, ShellSurfaceKind.None },
        { 17, ShellSurfaceKind.None },
        { 18, ShellSurfaceKind.None },
        { uint.MaxValue, ShellSurfaceKind.None },
    };

    /// <summary>
    /// What is never a paste target goes broader than what is acted on: every band above the app windows but the UIAccess
    /// one, measured or not; the default, desktop, UIAccess and wallpaper bands are app windows (or the desktop).
    /// </summary>
    /// <param name="band">The band id.</param>
    /// <param name="expected">Whether it holds only the shell's surfaces.</param>
    [Theory]
    [MemberData(nameof(ShellBands))]
    public void IsShellBand_IsEveryBandAboveTheApps(uint band, bool expected) => Assert.Equal(expected, ShellSurfaces.IsShellBand(band));

    /// <summary>The cases of <see cref="IsShellBand_IsEveryBandAboveTheApps"/>: band, whether it is the shell's.</summary>
    public static TheoryData<uint, bool> ShellBands => new()
    {
        { 0, false },
        { 1, false },
        { 2, false },
        { 12, false },
        { 3, true },
        { 4, true },
        { 5, true },
        { 6, true },
        { 13, true },
        { 16, true },
        { 17, true },
    };

    /// <summary>
    /// Every band <see cref="ShellSurfaces.KindOfBand"/> acts on is one <see cref="ShellSurfaces.IsShellBand"/> counts as the
    /// shell's: a surface the summon closes or stands beside is never a paste target for the pinned panel's tracker.
    /// </summary>
    [Fact]
    public void EverySurfaceActedOn_IsAShellBand()
    {
        for (uint band = 0; band <= 64; band++)
        {
            if (ShellSurfaces.KindOfBand(band) != ShellSurfaceKind.None)
            {
                Assert.True(ShellSurfaces.IsShellBand(band), $"band {band}");
            }
        }
    }

    /// <summary>
    /// Beside a centered Start on a 1920-pixel monitor there is room on both sides: the panel goes right (a tie), a gap away,
    /// level with Start's top, at its remembered width.
    /// </summary>
    [Fact]
    public void Beside_CenteredStart_GoesRightAtItsWidth()
    {
        var bounds = FlyoutPositioner.Beside(Start, Work, width: 432, height: 560, minWidth: 432, gap: 8, columnWidth: 0);
        Assert.Equal(new ScreenRect(1368, 282, 1800, 842), bounds);
    }

    /// <summary>A remembered width wider than the room beside Start is narrowed to the room, never below the minimum.</summary>
    [Fact]
    public void Beside_WiderThanTheRoom_IsNarrowedToIt()
    {
        var bounds = FlyoutPositioner.Beside(Start, Work, width: 700, height: 560, minWidth: 432, gap: 8, columnWidth: 0);
        Assert.Equal(new ScreenRect(1368, 282, 1920, 842), bounds);
    }

    /// <summary>
    /// No room for the minimum width on either side gives no bounds: the summon closes Start first. Measured case: the test
    /// desktop's 1600-pixel screen leaves 392 pixels beside Start's 800, under the 432 a header of six buttons needs.
    /// </summary>
    [Fact]
    public void Beside_NoRoomForTheMinimum_GivesNothing()
    {
        var work = new ScreenRect(0, 0, 1600, 852);
        var start = new ScreenRect(400, 102, 1200, 852);
        Assert.Null(FlyoutPositioner.Beside(start, work, width: 432, height: 560, minWidth: 432, gap: 8, columnWidth: 0));

        // With fewer buttons shown the header needs less, and the panel fits again.
        Assert.Equal(new ScreenRect(1208, 102, 1568, 662), FlyoutPositioner.Beside(start, work, width: 360, height: 560, minWidth: 360, gap: 8, columnWidth: 0));
    }

    /// <summary>Start aligned to one side leaves the room on the other, and the panel takes that side.</summary>
    [Fact]
    public void Beside_TakesTheSideWithMoreRoom()
    {
        var leftAligned = new ScreenRect(12, 282, 812, 1032);
        Assert.Equal(new ScreenRect(820, 282, 1252, 842), FlyoutPositioner.Beside(leftAligned, Work, 432, 560, 432, 8, 0));

        var rightAligned = new ScreenRect(1108, 282, 1908, 1032);
        Assert.Equal(new ScreenRect(668, 282, 1100, 842), FlyoutPositioner.Beside(rightAligned, Work, 432, 560, 432, 8, 0));
    }

    /// <summary>The groups column widens the panel and counts toward the room it needs.</summary>
    [Fact]
    public void Beside_CountsTheGroupsColumn()
    {
        Assert.Equal(new ScreenRect(1368, 282, 1844, 842), FlyoutPositioner.Beside(Start, Work, 432, 560, 432, 8, columnWidth: 44));

        // 552 pixels of room hold a 432-pixel minimum, but not with a 150-pixel column on top of it.
        Assert.Null(FlyoutPositioner.Beside(Start, Work, 432, 560, 432, 8, columnWidth: 150));
    }

    /// <summary>
    /// The panel's top is level with the surface's, kept inside the work area: a panel taller than the room under that top is
    /// moved up, and one taller than the work area shrinks to it.
    /// </summary>
    [Fact]
    public void Beside_StaysInsideTheWorkArea()
    {
        var low = new ScreenRect(560, 900, 1360, 1032);
        Assert.Equal(new ScreenRect(1368, 472, 1800, 1032), FlyoutPositioner.Beside(low, Work, 432, 560, 432, 8, 0));

        var bounds = FlyoutPositioner.Beside(Start, Work, 432, 5000, 432, 8, 0);
        Assert.Equal(new ScreenRect(1368, 0, 1800, 1032), bounds);
    }

    /// <summary>
    /// A surface that fills the work area (Task View), or lies off it, leaves no room, and an empty work area none either: no
    /// bounds, never a panel outside the work area.
    /// </summary>
    [Fact]
    public void Beside_NoRoomAtAll_GivesNothing()
    {
        Assert.Null(FlyoutPositioner.Beside(Work, Work, 432, 560, 432, 8, 0));
        Assert.Null(FlyoutPositioner.Beside(new ScreenRect(-3000, 0, 4000, 1032), Work, 432, 560, 432, 8, 0));
        Assert.Null(FlyoutPositioner.Beside(Start, new ScreenRect(0, 500, 1920, 500), 432, 560, 432, 8, 0));
    }

    /// <summary>
    /// 2,000 random surfaces and sizes: a panel placed beside a surface never overlaps it, never leaves the work area, is
    /// never narrower than its minimum, and none is placed only when no side has room for that minimum.
    /// </summary>
    [Fact]
    public void Beside_RandomSurfaces_NeverOverlapOrLeaveTheWorkArea()
    {
        var random = new Random(2060);
        for (int round = 0; round < 2000; round++)
        {
            var work = new ScreenRect(random.Next(-2000, 2000), random.Next(-500, 500), 0, 0);
            work = work with { Right = work.Left + random.Next(300, 4000), Bottom = work.Top + random.Next(300, 2200) };
            int surfaceLeft = random.Next(work.Left - 200, work.Right);
            int surfaceTop = random.Next(work.Top, work.Bottom);
            var surface = new ScreenRect(surfaceLeft, surfaceTop, surfaceLeft + random.Next(1, 1500), surfaceTop + random.Next(1, 1200));
            int minWidth = random.Next(1, 600);
            int width = random.Next(1, 900);
            int height = random.Next(1, 2400);
            int gap = random.Next(0, 20);
            int column = random.Next(0, 2) == 0 ? 0 : random.Next(1, 80);

            var bounds = FlyoutPositioner.Beside(surface, work, width, height, minWidth, gap, column);

            // The room on each side inside the work area, a gap away from the surface.
            int roomLeft = Math.Min(surface.Left - gap, work.Right) - work.Left;
            int roomRight = work.Right - Math.Max(surface.Right + gap, work.Left);
            if (bounds is not { } b)
            {
                Assert.True(Math.Max(roomLeft, roomRight) < minWidth + column, $"round {round}: room was there");
                continue;
            }

            Assert.False(b.Intersects(surface), $"round {round}: over the surface");
            Assert.True(b.Left >= work.Left && b.Right <= work.Right && b.Top >= work.Top && b.Bottom <= work.Bottom, $"round {round}: outside the work area");
            Assert.True(b.Width >= minWidth + column, $"round {round}: narrower than the minimum");
            Assert.True(b.Height > 0, $"round {round}: no height");
        }
    }

    /// <summary>Rectangles overlap when they share a pixel: touching edges and empty rectangles never do.</summary>
    [Fact]
    public void Intersects_NeedsASharedPixel()
    {
        var a = new ScreenRect(0, 0, 100, 100);
        Assert.True(a.Intersects(new ScreenRect(99, 99, 200, 200)));
        Assert.True(a.Intersects(new ScreenRect(-50, 10, 10, 20)));
        Assert.False(a.Intersects(new ScreenRect(100, 0, 200, 100)));
        Assert.False(a.Intersects(new ScreenRect(0, 100, 100, 200)));
        Assert.False(a.Intersects(new ScreenRect(50, 50, 50, 80)));
        Assert.False(new ScreenRect(10, 10, 10, 10).Intersects(a));
    }

    /// <summary>
    /// A context made without <see cref="ForegroundContext.Capture"/> (the tray, the pinned panel's tracker, tests) names no
    /// shell surface, so nothing is closed or placed beside for it.
    /// </summary>
    [Fact]
    public void ContextsNotCaptured_NameNoSurface()
    {
        Assert.Equal(ShellSurface.None, ForegroundContext.CursorOnly().Surface);
        Assert.Equal(ShellSurface.None, ForegroundContext.ForWindow(0).Surface);
        Assert.Equal(ShellSurface.None, new ForegroundContext(0x1234, 7, null, new ScreenPoint(1, 2)).Surface);
    }

    /// <summary>
    /// The real call: a window of this process is in the desktop band (or the call is missing, which reads as no band), so it
    /// is no shell surface and no shell window; no window and a window that closed have no band.
    /// </summary>
    [Fact]
    public void RealWindows_AreAppWindows()
    {
        Assert.Null(ShellSurfaces.BandOf(0));
        Assert.Equal(ShellSurface.None, ShellSurfaces.Describe(0));
        Assert.False(ShellSurfaces.IsShellWindow(0));

        nint closed;
        using (var window = new MessageWindowThread("Shell surface test", messageOnly: false))
        {
            closed = window.Handle;
            if (ShellSurfaces.BandOf(window.Handle) is { } band)
            {
                Assert.Equal(ShellSurfaces.DesktopBand, band);
            }

            Assert.Equal(ShellSurface.None, ShellSurfaces.Describe(window.Handle));
            Assert.False(ShellSurfaces.IsShellWindow(window.Handle));
        }

        Assert.Null(ShellSurfaces.BandOf(closed));
        Assert.Equal(ShellSurface.None, ShellSurfaces.Describe(closed));
    }
}
