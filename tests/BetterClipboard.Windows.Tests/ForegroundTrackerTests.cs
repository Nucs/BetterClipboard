using BetterClipboard.Windows.Input;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests for <see cref="ForegroundTracker"/>, which follows the window of another app the user is in while a pinned panel
/// shows (CLAUDE.md §2.55): which windows count as the shell's own surfaces, and the hook's life cycle.
/// </summary>
/// <remarks>
/// The reports themselves are not tested here: they come only for windows of other processes taking the foreground, and a
/// test that brought another window to the front would take the keyboard from whoever uses this PC. The live check in a
/// claude-desktops Windows desktop covers them (CLAUDE.md §5).
/// </remarks>
public sealed class ForegroundTrackerTests
{
    /// <summary>
    /// The taskbar, the tray's overflow, Start and Search, Alt+Tab and the switch's staging window are shell surfaces, in any
    /// letter case; app windows, the desktop and File Explorer are not, nor is a class that could not be read.
    /// </summary>
    /// <param name="className">The window class.</param>
    /// <param name="expected">Whether it is a shell surface.</param>
    [Theory]
    [InlineData("Shell_TrayWnd", true)]
    [InlineData("shell_traywnd", true)]
    [InlineData("Shell_SecondaryTrayWnd", true)]
    [InlineData("NotifyIconOverflowWindow", true)]
    [InlineData("TopLevelWindowForOverflowXamlIsland", true)]
    [InlineData("Windows.UI.Core.CoreWindow", true)]
    [InlineData("XamlExplorerHostIslandWindow", true)]
    [InlineData("MultitaskingViewFrame", true)]
    [InlineData("TaskSwitcherWnd", true)]
    [InlineData("TaskListThumbnailWnd", true)]
    [InlineData("ForegroundStaging", true)]
    [InlineData("Notepad", false)]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS", false)]
    [InlineData("Chrome_WidgetWin_1", false)]
    [InlineData("ApplicationFrameWindow", false)]
    [InlineData("CabinetWClass", false)]
    [InlineData("Progman", false)]
    [InlineData("WorkerW", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ShellSurfaces_AreNotPasteTargets(string? className, bool expected) =>
        Assert.Equal(expected, ForegroundTracker.IsShellSurface(className));

    /// <summary>
    /// The hook installs and comes off again, any number of times, and stopping or disposing twice is harmless: the panel
    /// starts and stops it with every pinned presentation. (Installing needs no message loop; only the reports do.)
    /// </summary>
    [Fact]
    public void Hook_StartsAndStopsRepeatedly()
    {
        int reports = 0;
        using var tracker = new ForegroundTracker(_ => reports++);
        Assert.False(tracker.IsRunning);

        for (int round = 0; round < 3; round++)
        {
            Assert.True(tracker.Start());
            Assert.True(tracker.IsRunning);
            Assert.True(tracker.Start());
            tracker.Stop();
            Assert.False(tracker.IsRunning);
            tracker.Stop();
        }

        tracker.Dispose();
        Assert.False(tracker.IsRunning);

        // This thread pumps no messages, so nothing can have been reported.
        Assert.Equal(0, reports);
    }

    /// <summary>A tracker needs someone to tell.</summary>
    [Fact]
    public void Constructor_RefusesNoCallback() =>
        Assert.Throws<ArgumentNullException>(() => new ForegroundTracker(null!));

    /// <summary>
    /// A tracked window becomes a paste target without a caret (nothing is placed next to it) and without a timestamp (no
    /// summon is timed from it); no window makes a context with no target, like the tray's.
    /// </summary>
    [Fact]
    public void ForWindow_NamesTheTarget()
    {
        var none = ForegroundContext.ForWindow(0);
        Assert.Equal(0, none.TargetWindow);
        Assert.Equal(0u, none.TargetProcessId);
        Assert.Null(none.Caret);
        Assert.Equal(0, none.CapturedAt);

        // A window that closed meanwhile keeps its handle: the paste then finds it gone and says so, as for any target.
        var gone = ForegroundContext.ForWindow(0x7FFFFFF1);
        Assert.Equal(0x7FFFFFF1, gone.TargetWindow);
        Assert.Null(gone.Caret);
        Assert.Equal(0, gone.CapturedAt);
    }
}
