using BetterClipboard.App.Interop;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Windows.Input;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace BetterClipboard.App.Views;

/// <summary>
/// The panel next to Start or Search (CLAUDE.md §2.61): Win, then Win+V, opens it beside Start without closing Start, and
/// the item picked is pasted into Start's search box, as Windows' own Win+V does.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> The owner's report (2026-11-20): "I pressed Win to open start menu and then Win+V and the BetterClipboard menu
/// has shown behind the win start menu." Asked whether Win+V should close Start and paste into the app beneath, or paste
/// into Start's search box after reopening it, the owner answered: "Has to be another way to seemlessly have access".
/// Windows keeps Start in a band above every window an ordinary process can make (<see cref="ShellSurfaces"/>), so the
/// panel can never be over it; and nothing can take the keyboard from Start while it is open. Beside it is the one place
/// left: a window that never activates, next to Start, takes clicks without closing Start (measured in a test desktop), and
/// the panel's mode without the keyboard (ClipboardFlyout.Focusless.cs) already takes the keys it acts on while another
/// window keeps the keyboard.
/// </para>
/// <para>
/// <b>What the panel does there.</b> It is placed on the side of Start with more room (<see cref="BesideBounds"/>), and
/// shows without the keyboard: arrows, typing, Enter, Esc work as over any app, and the paste goes into Start's search box
/// (its window is the paste target). The hover peeks stay closed (<see cref="besideSurface"/>): they cover the monitor, and
/// most of one would be under Start. Taking the keyboard — a click into the search box, Ctrl+F, a viewer, a name box —
/// closes Start first (<see cref="TakeKeyboardPastSurface"/>), and the next paste goes into the app beneath.
/// </para>
/// <para>
/// <b>When it cannot.</b> No room beside Start for the panel's minimum width, the setting that leaves the keyboard with the
/// app switched off, a pinned panel that Start covers, or another surface (Quick settings, the notification center, Task
/// View): the controller closes the surface first, the way Esc does, and the panel opens over the window in front then
/// (<c>AppController.CloseShellSurfaceThenSummon</c>).
/// </para>
/// </remarks>
public sealed partial class ClipboardFlyout
{
    /// <summary>
    /// The space kept between Start or Search and the panel beside it, in DIPs. Wider than the panel's usual gap: Start draws
    /// 16 pixels past its window's rectangle on each side (measured at 100 % on Windows 11 26200: window 560–1360, visible
    /// 544–1376), so the usual 8 would put the panel's edge under Start's.
    /// </summary>
    private const double BesideGapDip = 24;

    /// <summary>
    /// Whether the panel shows beside Start or Search, without the keyboard, pasting into the search box: the hover peeks stay
    /// closed, the groups column grows away from the surface, and taking the keyboard closes Start first. Cleared when the
    /// mode ends (<see cref="EndFocusless"/>, <see cref="EndModeForKeyboard"/>, <see cref="TakeKeyboardPastSurface"/>).
    /// </summary>
    private bool besideSurface;

    /// <summary>The bounds of the Start or Search the panel stands beside (valid while <see cref="besideSurface"/>).</summary>
    private ScreenRect surfaceBounds;

    /// <summary>
    /// Marks the panel as standing beside the shell surface of <paramref name="context"/>, or not: kept with its bounds, so the
    /// groups column can grow away from it (<see cref="ClearOfSurface"/>).
    /// </summary>
    /// <param name="context">The summon's context.</param>
    /// <param name="beside">Whether the panel stands beside Start or Search for this use.</param>
    private void SetBesideSurface(ForegroundContext context, bool beside)
    {
        besideSurface = beside;
        surfaceBounds = beside ? context.Surface.Bounds : default;
    }

    /// <summary>
    /// Puts the always-on-top flag back on the concealed panel when it is missing — after the warm-up and after each use — so
    /// that a summon beside Start or Search finds it set.
    /// </summary>
    /// <remarks>
    /// Windows ignores the request while Start or Search is open: <c>SetWindowPos(HWND_TOPMOST)</c> reports success and the
    /// flag stays off (measured in a test desktop, 2026-11-20). The summon's own request (<see cref="PresentAsync"/>) then
    /// came too late, and the panel showed beside Start below the app window last in front, visible only where none covered
    /// it. Nor does the presenter's always-on-top setting hold: the flag was off right after the warm-up. A concealed window
    /// is invisible, so the flag costs nothing until the panel shows.
    /// </remarks>
    private void KeepTopmostWhileConcealed()
    {
        if (cloaking && !presented && !closingForExit)
        {
            EnsureTopmost();
        }
    }

    /// <summary>Whether this session logged that the presenter had to set the always-on-top flag again (logged once).</summary>
    private bool topmostThroughPresenterLogged;

    /// <summary>
    /// Makes the panel's window always on top, and checks that it took: a plain <c>SetWindowPos(HWND_TOPMOST)</c> first, then,
    /// when the flag is still off, the presenter's own setting turned off and on again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why the second way: in a test desktop (2026-11-20) the panel's window refused the plain request for good once it had
    /// lost the flag — <c>SetWindowPos</c> reported success and the flag stayed off, from this process and from another,
    /// with Start open and with Notepad in front — while a plain Win32 window took the same request in every state. The
    /// presenter's <c>IsAlwaysOnTop</c> keeps answering <see langword="true"/> meanwhile, so setting it to
    /// <see langword="true"/> again does nothing; turned off and on, WinUI applies it anew.
    /// </para>
    /// <para>Logged once per session when the presenter was needed, content-free, with whether it worked.</para>
    /// </remarks>
    /// <returns><see langword="true"/> when the window is always on top afterwards.</returns>
    private bool EnsureTopmost()
    {
        if (WindowInterop.IsTopmost(hwnd))
        {
            return true;
        }

        WindowInterop.MakeTopmost(hwnd);
        if (WindowInterop.IsTopmost(hwnd))
        {
            return true;
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = false;
            presenter.IsAlwaysOnTop = true;
        }

        bool topmost = WindowInterop.IsTopmost(hwnd);
        if (!topmostThroughPresenterLogged)
        {
            topmostThroughPresenterLogged = true;
            AppLog.Info(topmost
                ? "The panel's window refused always-on-top; its presenter set it again."
                : "The panel's window refused always-on-top, also through its presenter.");
        }

        return topmost;
    }

    /// <summary>
    /// The part of <paramref name="workArea"/> the panel may grow into while it stands beside Start or Search: the side of the
    /// surface the panel is on, a gap away, so the groups column opened there never goes under the surface (it grows away
    /// from it instead, <see cref="FlyoutPositioner.ExtendLeft"/>). The whole work area otherwise.
    /// </summary>
    /// <param name="workArea">The panel's monitor's work area.</param>
    /// <param name="panel">The panel's bounds now.</param>
    /// <returns>The area the panel may use.</returns>
    private ScreenRect ClearOfSurface(ScreenRect workArea, ScreenRect panel)
    {
        if (!besideSurface)
        {
            return workArea;
        }

        int gap = (int)Math.Round(BesideGapDip * Scale);
        if (panel.Left >= surfaceBounds.Right)
        {
            return workArea with { Left = Math.Clamp(surfaceBounds.Right + gap, workArea.Left, workArea.Right) };
        }

        return panel.Right <= surfaceBounds.Left
            ? workArea with { Right = Math.Clamp(surfaceBounds.Left - gap, workArea.Left, workArea.Right) }
            : workArea;
    }

    /// <summary>
    /// Whether a summon over <paramref name="context"/> can leave Start or Search open: it is Start or Search, the panel would
    /// show without the keyboard (<see cref="ShouldLeaveFocus"/>), and the panel fits beside it — or, for the pinned panel
    /// brought forward where it stands, the panel waits without the keyboard and the surface does not cover it.
    /// </summary>
    /// <param name="context">Where the user was when pressing the shortcut.</param>
    /// <param name="bringForward">The pinned panel on screen is brought forward (it does not move).</param>
    /// <returns><see langword="true"/> to open beside the surface; <see langword="false"/> to close it first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public bool CanOpenBeside(ForegroundContext context, bool bringForward)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Surface.Kind != ShellSurfaceKind.Search || !ShouldLeaveFocus(context))
        {
            return false;
        }

        if (bringForward)
        {
            // Only a pinned panel that waits without the keyboard takes the keys back next to Start (BringForwardWithoutFocus);
            // one that took the keyboard the usual way would have to be activated, which Start refuses.
            if (!focusless)
            {
                return false;
            }

            // A pinned panel stays where the user left it: beside Start only when Start leaves it uncovered.
            var position = AppWindow.Position;
            var size = AppWindow.Size;
            var panel = new ScreenRect(position.X, position.Y, position.X + size.Width, position.Y + size.Height);
            return !panel.Intersects(context.Surface.Bounds);
        }

        return BesideBounds(context, controller.Settings.Current.ShowGroupsPane) is not null;
    }

    /// <summary>
    /// Where a summon puts the panel beside Start or Search (<see cref="FlyoutPositioner.Beside"/>): on the surface's monitor,
    /// at the remembered size in that monitor's pixels (narrower when the room beside it is), the groups column included.
    /// </summary>
    /// <param name="context">The summon's context; its surface is Start or Search.</param>
    /// <param name="groupsColumnOpen">Whether the groups column will show (it widens the panel).</param>
    /// <returns>The bounds, or <see langword="null"/> when the panel's minimum width does not fit on either side.</returns>
    private RectInt32? BesideBounds(ForegroundContext context, bool groupsColumnOpen)
    {
        var settings = controller.Settings.Current;
        var (workArea, scale) = MonitorLookup.FromWindow(context.TargetWindow);
        var (width, height) = FlyoutSizing.ToPixels(PanelWidthDip(settings), settings.FlyoutHeight, scale, extraWidthDip: 0);
        var (minWidth, _) = FlyoutSizing.ToPixels(MinWidthDip(), settings.FlyoutHeight, scale, extraWidthDip: 0);
        int column = groupsColumnOpen ? (int)Math.Round(GroupsPaneDip * scale) : 0;
        var bounds = FlyoutPositioner.Beside(context.Surface.Bounds, workArea, width, height, minWidth, (int)Math.Round(BesideGapDip * scale), column);
        return bounds is { } b ? new RectInt32(b.Left, b.Top, b.Width, b.Height) : null;
    }

    /// <summary>
    /// The panel beside Start or Search takes the keyboard (<see cref="TakeKeyboard"/>): Start is closed first, since nothing
    /// takes the keyboard from it, then the panel is activated; its next paste goes into the window in front by then.
    /// </summary>
    /// <param name="reason">Why, for the log line (content-free: "a click into the search box", "Ctrl+F").</param>
    /// <remarks>
    /// <para>
    /// The window keeps <c>WS_EX_NOACTIVATE</c> until Start is gone: Windows then gives the foreground back to the app the user
    /// came from, not to the panel, so the controller learns the new paste target from it
    /// (<c>AppController.CloseShellSurfaceForKeyboardAsync</c>). The caller's own steps (focusing the search box, opening a
    /// viewer) run at once; the activation follows some 20 to 50 ms later, keeping the focus they set.
    /// </para>
    /// <para><c>async void</c>: started by the key or click handler, awaited by nobody; it catches everything itself.</para>
    /// </remarks>
    private async void TakeKeyboardPastSurface(string reason)
    {
        try
        {
            // The mode ends now: the keys go back (to Start, for the moment until it closes) and nothing is taken twice.
            focusless = false;
            besideSurface = false;
            surfaceBounds = default;
            LeaveKeysToApp();
            AppLog.Info($"The panel took the keyboard: {reason}; Start or Search was closed first (nothing takes the keyboard from it).");
            await controller.CloseShellSurfaceForKeyboardAsync();
            if (!presented)
            {
                return; // put away while Start closed (the shortcut pressed again)
            }

            WindowInterop.SetNoActivate(hwnd, false);
            Activate();
            ForegroundHelper.Activate(hwnd);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Taking the keyboard past Start or Search failed: {ex.Message}");
        }
    }
}
