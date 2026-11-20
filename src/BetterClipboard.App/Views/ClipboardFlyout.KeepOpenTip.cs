using BetterClipboard.App.Interop;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Settings;
using BetterClipboard.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace BetterClipboard.App.Views;

/// <summary>
/// The pin's tip: a teaching tip that points at the pin and says in a sentence and a half how pinning works, shown while the
/// pinned panel is on screen until the user closes it, then never again (CLAUDE.md §2.55).
/// </summary>
/// <remarks>
/// <para>
/// <b>The request</b> (2026-11-20): "Add a tooltip shown once until dismissed when pinned enabled that in 1.5 sentences
/// explains how pinning works." A WinUI <see cref="TeachingTip"/>, not a <see cref="ToolTip"/>: a tooltip has no close button
/// (its content takes no pointer input, ClipboardFlyout.ToolTipKeyboard.cs), and it goes away by itself.
/// </para>
/// <para>
/// <b>When</b> (<see cref="AppSettings.KeepOpenTipDue"/>): a moment after the pin is turned on, and at every summon of a
/// pinned panel, for as long as the user never closed it. It stays while the panel stays — through pastes, while another app
/// has the keyboard, while the panel is dragged. The panel going away, unpinning and hiding the pin take it off the screen
/// without spending it, so it comes back with the pinned panel: "shown until dismissed".
/// </para>
/// <para>
/// <b>Closed for good</b> (<see cref="AppSettings.KeepOpenTipDismissed"/>) by its close button (the X) or by Esc while it shows:
/// the first Esc closes the tip, as it closes a viewer before it clears the search or closes the panel.
/// </para>
/// <para>
/// <b>Where</b> (<see cref="ChooseKeepOpenTipPlacement"/>). Over the panel's window, its tail pointing down at the pin, where it
/// covers nothing of the panel — the place the owner chose for the weekly update reminder (CLAUDE.md §2.30). With too little
/// room over the window (the panel at the top of the screen), to the left of the pin, over the header's title; with no room
/// there either, under the pin. A tip in a window of its own (<c>ShouldConstrainToRootBounds</c> off) checks nothing itself:
/// WinUI then takes the preferred placement as it is (its <c>DetermineEffectivePlacement</c>, read 2026-11-20), so the room is
/// measured here.
/// </para>
/// <para>
/// <b>When the panel moves</b> (a drag of its background, a border, the groups column, a button shown in Settings), the tip
/// would stay where it opened: WinUI places it from its target's place inside the window, which a move does not change, and
/// only when it opens or its placement settings change. So every move asks it to place itself again
/// (<see cref="RepositionKeepOpenTip"/>), and it follows the window. Never by closing and opening it again: closing its window
/// during a drag of the panel ended the drag after its first step (the panel moved 7 px), and an open asked for while its close
/// animation still played was turned down, leaving the tip away for good (both found in the live QA, 2026-11-20).
/// </para>
/// <para>
/// <b>Opening and closing</b> go through one check (<see cref="ReconcileKeepOpenTip"/>), run a moment after each request and
/// again after each change it makes, until what shows is what is wanted. WinUI's tip turns a change of <c>IsOpen</c> down
/// while its open or close animation plays — it sets the property back a frame later (its <c>OnIsOpenChanged</c>) — and a drag
/// of the panel holds an open back; both are simply tried again.
/// </para>
/// <para>
/// <b>Never in the panel's way.</b> It is not counted in <see cref="openPopups"/>, so the key model, a paste, the deactivation
/// of a pinned panel and the hover peeks work as they do without it. A tip that is not light-dismissed takes no keyboard when it
/// opens (WinUI's <c>OnPopupOpened</c>); should the keyboard still be found inside it after it opened, the search box takes it
/// back (<see cref="GiveKeyboardBackFromKeepOpenTip"/>). Its window reaches a little over the header, where a press would not
/// bring a pinned panel forward: the panel does that itself while the tip shows (<see cref="Root_PointerPressedUnderKeepOpenTip"/>).
/// The weekly update reminder, which points at the header from above too, gives way while it shows, and the pin's own tooltip
/// opens under the button meanwhile, instead of over the tip's tail.
/// </para>
/// <para>UI thread only.</para>
/// </remarks>
public sealed partial class ClipboardFlyout
{
    /// <summary>
    /// The space the room checks keep between the tip and the edge of the monitor's work area, in DIPs: enough that the tip
    /// does not touch the screen's edge.
    /// </summary>
    private const double KeepOpenTipGapDip = 4;

    /// <summary>
    /// How wide the tip's text is, in DIPs: the tip at its widest (336, <c>TeachingTipMaxWidth</c> in WinUI's generic.xaml)
    /// less its border (2), its padding (24) and the room its close button keeps beside the text (28). The text of every
    /// language fills that width, so the tip takes its widest.
    /// </summary>
    private const double KeepOpenTipTextWidthDip = 282;

    /// <summary>
    /// What the tip adds to its text's height above or below its target, in DIPs: its border (2), its padding (24) and its
    /// tail (8), from WinUI's generic.xaml.
    /// </summary>
    private const double KeepOpenTipChromeDip = 34;

    /// <summary>
    /// How much room the tip takes beside its target, in DIPs: the tip at its widest (336) and its tail (8).
    /// </summary>
    private const double KeepOpenTipWidthDip = 344;

    /// <summary>
    /// How long the tip waits before it opens: after a summon, after the pin's click. Late enough that creating the tip's
    /// window is not part of the summon, which must put the panel on screen in one step; early enough to be seen with the
    /// click that turned the pin on.
    /// </summary>
    private static readonly TimeSpan KeepOpenTipSettleTime = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// How soon the check looks again after it changed something (<see cref="ReconcileKeepOpenTip"/>): longer than a frame, in
    /// which WinUI turns a change down while the tip animates, and short enough that a tip turned down while closing leaves
    /// the screen with its panel.
    /// </summary>
    private static readonly TimeSpan KeepOpenTipRecheckTime = TimeSpan.FromMilliseconds(200);

    /// <summary>Runs <see cref="ReconcileKeepOpenTip"/> once its wait is over; created on first use.</summary>
    private DispatcherQueueTimer? keepOpenTipTimer;

    /// <summary>
    /// Whether <see cref="OnWindowChangedDuringKeepOpenTip"/> is subscribed: while the tip shows or is about to, so that it
    /// follows the window, until it is off the screen for good.
    /// </summary>
    private bool keepOpenTipWatchingWindow;

    /// <summary>
    /// Which of two placement margins, a hundredth of a DIP apart, the tip has: each reposition switches it, so that WinUI
    /// places the tip again (<see cref="RepositionKeepOpenTip"/>).
    /// </summary>
    private bool keepOpenTipMarginNudged;

    /// <summary>Whether this session logged that the tip showed: said once, content-free.</summary>
    private bool keepOpenTipShowLogged;

    /// <summary>Whether this session logged that the keyboard had to be taken back from the tip: said once, content-free.</summary>
    private bool keepOpenTipKeyboardLogged;

    /// <summary>
    /// Gives the tip its target, the pin, and lets a press on the panel bring it forward while the tip shows
    /// (<see cref="Root_PointerPressedUnderKeepOpenTip"/>). Called once, from the constructor: the target in code rather than an
    /// x:Bind, which would only bind at the window's first activation.
    /// </summary>
    private void InitializeKeepOpenTip()
    {
        KeepOpenTip.Target = KeepOpenButton;

        // handledEventsToo: the header's buttons mark their presses handled, and those are the presses that matter here.
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Root_PointerPressedUnderKeepOpenTip), handledEventsToo: true);
    }

    /// <summary>
    /// A press on the panel while the tip shows and another app has the keyboard brings the panel forward, as a press on any
    /// part of a window does.
    /// </summary>
    /// <param name="sender">The root grid.</param>
    /// <param name="e">Pointer data.</param>
    /// <remarks>
    /// The tip's window reaches over the top of the panel's header: below its tail it keeps transparent rows (its template's
    /// tail margin), about 18 px of the header buttons' upper half at 100 %. A press there still reaches the button under it,
    /// but Windows does not bring the panel forward — the press went to the tip's window. Found in the live QA (2026-11-20): a
    /// click on the pin of a pinned panel waiting behind Notepad unpinned it, and the panel, no longer staying open and not in
    /// front, went away at once (<see cref="ApplyHeaderButtons"/>), where the same click without the tip brought it forward.
    /// Only while the tip shows: otherwise Windows does it itself.
    /// </remarks>
    private void Root_PointerPressedUnderKeepOpenTip(object sender, PointerRoutedEventArgs e)
    {
        if (!KeepOpenTip.IsOpen || !IsOpen || ForegroundHelper.IsForeground(hwnd))
        {
            return;
        }

        Activate();
        ForegroundHelper.Activate(hwnd);
    }

    /// <summary>
    /// Asks for the tip to show or to go, as the panel and the settings say now (<see cref="KeepOpenTipWanted"/>): a tip that
    /// is no longer wanted closes at once, so it leaves with its panel; one that is wanted opens a moment later. Idempotent;
    /// called after every settings change (<see cref="ApplyHeaderButtons"/>), at the end of a summon, and when a pinned panel
    /// is brought forward.
    /// </summary>
    private void UpdateKeepOpenTip()
    {
        if (KeepOpenTipWanted())
        {
            WatchWindowForKeepOpenTip(true);
            if (!KeepOpenTip.IsOpen && keepOpenTipTimer is not { IsRunning: true })
            {
                StartKeepOpenTipCheck(KeepOpenTipSettleTime);
            }

            return;
        }

        if (KeepOpenTip.IsOpen)
        {
            CloseKeepOpenTip();
        }
        else
        {
            SettleKeepOpenTip();
        }
    }

    /// <summary>
    /// Whether the tip should be on screen now: the panel shows, the app is not exiting, and the tip is due
    /// (<see cref="AppSettings.KeepOpenTipDue"/>: pinned, with the pin shown, never closed by the user).
    /// </summary>
    /// <returns><see langword="true"/> while the tip should show.</returns>
    private bool KeepOpenTipWanted() =>
        presented && !closingForExit && controller.Settings.Current.KeepOpenTipDue && KeepOpenButton.Visibility == Visibility.Visible;

    /// <summary>
    /// Takes the tip off the screen now, without spending it (the panel going away, unpinned, the pin hidden, the app
    /// exiting), and looks again shortly, since WinUI turns a close down while the tip's open animation plays.
    /// </summary>
    private void CloseKeepOpenTip()
    {
        if (!KeepOpenTip.IsOpen)
        {
            SettleKeepOpenTip();
            return;
        }

        try
        {
            // Closed from code: its Closed event says Programmatic, which KeepOpenTip_Closed leaves alone.
            KeepOpenTip.IsOpen = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // The window is closing under it; there is nothing left to close.
            AppLog.Info($"Closing the pin's tip failed ({ex.GetType().Name}).");
        }

        StartKeepOpenTipCheck(KeepOpenTipRecheckTime);
    }

    /// <summary>
    /// Ends the tip's bookkeeping once it is off the screen and not wanted: no check waits, the window is no longer followed,
    /// and the pin's tooltip opens over the button again.
    /// </summary>
    private void SettleKeepOpenTip()
    {
        keepOpenTipTimer?.Stop();
        WatchWindowForKeepOpenTip(false);
        ToolTipService.SetPlacement(KeepOpenButton, PlacementMode.Top);
    }

    /// <summary>Runs <see cref="ReconcileKeepOpenTip"/> after <paramref name="wait"/> (a later call starts the wait again).</summary>
    /// <param name="wait">How long to wait.</param>
    private void StartKeepOpenTipCheck(TimeSpan wait)
    {
        if (keepOpenTipTimer is null)
        {
            keepOpenTipTimer = DispatcherQueue.CreateTimer();
            keepOpenTipTimer.IsRepeating = false;
            keepOpenTipTimer.Tick += KeepOpenTipTimer_Tick;
        }

        keepOpenTipTimer.Stop();
        keepOpenTipTimer.Interval = wait;
        keepOpenTipTimer.Start();
    }

    /// <summary>The wait is over: the check runs (<see cref="ReconcileKeepOpenTip"/>).</summary>
    /// <param name="sender">The timer.</param>
    /// <param name="args">Unused.</param>
    private void KeepOpenTipTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        try
        {
            ReconcileKeepOpenTip();
        }
        catch (Exception ex)
        {
            // A timer handler must not let anything escape into the dispatcher, and a tip is never worth a crash. The next
            // summon of the pinned panel tries again.
            AppLog.Warn($"Showing or closing the pin's tip failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Makes what shows what is wanted: opens the tip when it should show and does not (not during a drag of the panel),
    /// closes it when it shows and should not, and looks again shortly after each change, which WinUI may have turned down
    /// (class remarks). Once the two agree, it stops; a tip that just opened gives the keyboard back if it took it.
    /// </summary>
    private void ReconcileKeepOpenTip()
    {
        bool wanted = KeepOpenTipWanted();
        if (wanted == KeepOpenTip.IsOpen)
        {
            if (wanted)
            {
                GiveKeyboardBackFromKeepOpenTip();
            }
            else
            {
                SettleKeepOpenTip();
            }

            return;
        }

        if (!wanted)
        {
            KeepOpenTip.IsOpen = false;
        }
        else if (drag is null)
        {
            OpenKeepOpenTip();
        }

        // Turned down while the tip animated, or held back by a drag: look again.
        StartKeepOpenTipCheck(KeepOpenTipRecheckTime);
    }

    /// <summary>
    /// Opens the tip where there is room for it (<see cref="ChooseKeepOpenTipPlacement"/>). The weekly update reminder closes
    /// first: both point at the header from above.
    /// </summary>
    private void OpenKeepOpenTip()
    {
        CancelUpdateReminder();
        KeepOpenTip.PreferredPlacement = ChooseKeepOpenTipPlacement();

        // The pin's own tooltip would open over the tip's tail, flush on the button (its default place): under the button
        // meanwhile. Set back when the tip is off the screen for good (SettleKeepOpenTip).
        ToolTipService.SetPlacement(KeepOpenButton, PlacementMode.Bottom);
        KeepOpenTip.IsOpen = true;
        if (!keepOpenTipShowLogged)
        {
            keepOpenTipShowLogged = true;
            AppLog.Info("The pin's tip shows: it says how pinning works while the panel is pinned, until the user closes it.");
        }
    }

    /// <summary>
    /// Places the open tip again, at its target's current place on the screen and on the side there is room on: after any move
    /// or resize of the window, during a drag at every step. WinUI places its tip only when it opens or when its placement
    /// settings change (its <c>OnPropertyChanged</c>: <c>PreferredPlacement</c>, <c>PlacementMargin</c>), so a placement
    /// that stays the same is asked for through a placement margin a hundredth of a DIP away from the last one: a change of
    /// its offsets, too small to see, that moves its window to the panel's.
    /// </summary>
    private void RepositionKeepOpenTip()
    {
        if (!KeepOpenTip.IsOpen)
        {
            return;
        }

        var placement = ChooseKeepOpenTipPlacement();
        if (KeepOpenTip.PreferredPlacement != placement)
        {
            KeepOpenTip.PreferredPlacement = placement;
            return;
        }

        keepOpenTipMarginNudged = !keepOpenTipMarginNudged;
        KeepOpenTip.PlacementMargin = new Thickness(keepOpenTipMarginNudged ? 0.01 : 0);
    }

    /// <summary>
    /// The user closed the tip — Esc while it showed, or its close button, which closed it already: it never shows again
    /// (<see cref="AppSettings.KeepOpenTipDismissed"/>), and the search box gets the keyboard back when the panel is in
    /// front.
    /// </summary>
    /// <param name="how">How it was closed, for the log line; content-free.</param>
    private void DismissKeepOpenTip(string how)
    {
        try
        {
            // The settings change makes the controller apply the header again (ApplyHeaderButtons), which closes the tip
            // through UpdateKeepOpenTip now that it is no longer due.
            controller.Settings.Update(s => s with { KeepOpenTipDismissed = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held for this session (the store keeps it in memory) and written with the next save; no change event comes,
            // so the tip is closed below either way.
            AppLog.Warn($"Saving that the pin's tip was closed failed: {ex.Message}");
        }

        AppLog.Info($"The pin's tip was closed with {how}; it does not show again.");
        CloseKeepOpenTip();

        // A click on the X put the keyboard on the button, which is gone now: typing must search again. Only while the panel
        // is in front: a pinned panel behind the app the user types in must not take the keyboard from it.
        if (IsOpen && openPopups == 0 && ForegroundHelper.IsForeground(hwnd))
        {
            SearchBox.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>
    /// The tip closed. By its close button: the user dismissed it, for good (<see cref="DismissKeepOpenTip"/>). From code: the
    /// check that asked for it follows up. It is never light-dismissed.
    /// </summary>
    /// <param name="sender">The tip.</param>
    /// <param name="args">Why it closed.</param>
    private void KeepOpenTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args)
    {
        try
        {
            if (args.Reason == TeachingTipCloseReason.CloseButton)
            {
                DismissKeepOpenTip("its close button");
            }
        }
        catch (Exception ex)
        {
            // An event handler of a XAML control: nothing may escape into the dispatcher.
            AppLog.Warn($"Closing the pin's tip failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Gives the search box the keyboard back when it is found inside the tip's window although nobody went there: the panel
    /// is in front, none of its own popups is open, and the element that has the keyboard lies outside the panel's content.
    /// A tip that is not light-dismissed takes no keyboard when it opens, so this should never act; when it does, the keys typed
    /// next would go to the tip and never reach the search box. Logged once, content-free.
    /// </summary>
    private void GiveKeyboardBackFromKeepOpenTip()
    {
        if (!IsOpen || openPopups != 0 || drag is not null || Root.XamlRoot is not { } root || !ForegroundHelper.IsForeground(hwnd))
        {
            return;
        }

        if (FocusManager.GetFocusedElement(root) is not DependencyObject focused || IsInsideRoot(focused))
        {
            return;
        }

        SearchBox.Focus(FocusState.Programmatic);
        if (!keepOpenTipKeyboardLogged)
        {
            keepOpenTipKeyboardLogged = true;
            AppLog.Info("The keyboard was outside the panel while the pin's tip showed; the search box took it back.");
        }

        // Whether an element lies in the panel's own content (Root), as opposed to a popup's or the window's frame.
        bool IsInsideRoot(DependencyObject element)
        {
            for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (ReferenceEquals(current, Root))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Starts or stops following the window's moves and resizes for the tip (<see cref="keepOpenTipWatchingWindow"/>).
    /// </summary>
    /// <param name="watch"><see langword="true"/> to follow them, <see langword="false"/> to stop.</param>
    private void WatchWindowForKeepOpenTip(bool watch)
    {
        if (watch == keepOpenTipWatchingWindow)
        {
            return;
        }

        keepOpenTipWatchingWindow = watch;
        if (watch)
        {
            AppWindow.Changed += OnWindowChangedDuringKeepOpenTip;
        }
        else
        {
            AppWindow.Changed -= OnWindowChangedDuringKeepOpenTip;
        }
    }

    /// <summary>
    /// The window moved or changed size while the tip shows or is about to: an open tip is placed again at once, so it
    /// follows the window (<see cref="RepositionKeepOpenTip"/>); one about to open waits until the window rests.
    /// </summary>
    /// <param name="sender">The window.</param>
    /// <param name="args">What changed.</param>
    private void OnWindowChangedDuringKeepOpenTip(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange)
        {
            return;
        }

        try
        {
            if (KeepOpenTip.IsOpen)
            {
                RepositionKeepOpenTip();
            }
            else if (KeepOpenTipWanted())
            {
                StartKeepOpenTipCheck(KeepOpenTipSettleTime);
            }
        }
        catch (Exception ex)
        {
            // A window event handler: nothing may escape into the dispatcher.
            AppLog.Warn($"Moving the pin's tip with its panel failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Where the tip goes, by the room around the pin on its monitor: over the window when the tip fits between the top of the
    /// work area and the pin; else to the left of the pin, over the header's title, when it fits between the work area's left
    /// edge and the pin; else under the pin, over the search box, which the user can close it from. The tip's height is its
    /// text in the current language, measured at the width it wraps to, plus its border, padding and tail.
    /// </summary>
    /// <returns>The placement for the tip.</returns>
    private TeachingTipPlacementMode ChooseKeepOpenTipPlacement()
    {
        // Where the pin lies in the window's content, in DIPs: the tail points at it.
        var pin = KeepOpenButton.TransformToVisual(null).TransformPoint(default);
        var text = new TextBlock
        {
            Text = KeepOpenTip.Subtitle,
            TextWrapping = TextWrapping.WrapWholeWords,
            FontFamily = KeepOpenTip.FontFamily,
            FontSize = KeepOpenTip.FontSize,
        };
        text.Measure(new Size(KeepOpenTipTextWidthDip, double.PositiveInfinity));
        double height = text.DesiredSize.Height + KeepOpenTipChromeDip + KeepOpenTipGapDip;
        if (RoomAboveWindowDip() + Math.Max(0, pin.Y) >= height)
        {
            return TeachingTipPlacementMode.Top;
        }

        var (workArea, scale) = MonitorLookup.FromWindow(hwnd);
        var (clientLeft, _) = WindowInterop.GetClientOrigin(hwnd);
        double roomLeft = scale > 0 ? ((clientLeft - workArea.Left) / scale) + pin.X : 0;
        return roomLeft >= KeepOpenTipWidthDip + KeepOpenTipGapDip ? TeachingTipPlacementMode.Left : TeachingTipPlacementMode.Bottom;
    }

    /// <summary>
    /// Takes the tip off the screen for good and lets go of its timer and window event (the window is closing for the app's
    /// exit): a handler on a timer or on the window that refers to the window keeps it alive (CLAUDE.md §4).
    /// </summary>
    private void DisposeKeepOpenTip()
    {
        if (KeepOpenTip.IsOpen)
        {
            try
            {
                KeepOpenTip.IsOpen = false;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                AppLog.Info($"Closing the pin's tip at exit failed ({ex.GetType().Name}).");
            }
        }

        SettleKeepOpenTip();
        if (keepOpenTipTimer is { } timer)
        {
            timer.Tick -= KeepOpenTipTimer_Tick;
            keepOpenTipTimer = null;
        }
    }
}
