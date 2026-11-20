using BetterClipboard.App.Interop;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Settings;
using BetterClipboard.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace BetterClipboard.App.Views;

/// <summary>
/// The pin — the panel stays open after pasting — and the header's buttons Settings can hide (CLAUDE.md §2.55).
/// </summary>
/// <remarks>
/// <para>
/// <b>The request</b> (2026-11-20): "Before the update icon add a pin icon which keeps window open after pasting and properly
/// pastes as if the window would've closed. The point is to be clicking and pasting repeatedly whatever wanted. Additional
/// feature: all buttons but settings can be hidden in settings." The owner's choices: the pin is remembered until unpinned
/// (<see cref="AppSettings.KeepPanelOpen"/>), and the shortcut brings a pinned panel that waits behind another app forward in
/// place (<see cref="PanelShortcut"/>).
/// </para>
/// <para>
/// <b>Pasting while pinned</b> is the ordinary paste (<c>AppController.DeliverAsync</c>) without the put-away: the panel's
/// overlays close (a viewer would cover the target), the clipboard is written, the window the user was last in takes the
/// foreground and gets Ctrl+V, exactly as when the panel closes — only the panel stays on screen, always on top and no longer
/// active (<see cref="PutAwayForPaste"/>). Its deactivation leaves it up (<see cref="OnActivated"/>), and so does every use of an
/// item (<see cref="Dismiss"/>: <i>Copy only</i>, a file opened, a command run). Only the user closes it: Esc, the shortcut
/// while the panel is in front, Alt+F4, the Settings button (<see cref="CloseByUser"/>). The list is not reset and a bump to the
/// top updates its card in place, so the cards stay where they are under the mouse. When the user goes to another window
/// while one of its viewers or peeks shows, those close and the panel stays (<see cref="CloseOverlaysOfPinnedPanel"/>). Its
/// window is checked to still be always on top whenever another window takes the keyboard (<see cref="KeepOnTop"/>).
/// </para>
/// <para>
/// <b>Which window a pinned paste goes into.</b> The shortcut captures one window; a pinned panel outlives it, and the user
/// clicks into other apps between pastes. While the pinned panel is on screen, a <see cref="ForegroundTracker"/> reports every
/// window of another app that takes the foreground (the taskbar, the tray, Start and Alt+Tab excluded), and the controller
/// makes it the paste target (<c>AppController.NoteForeignForeground</c>). A click on a card activates the panel, so at the
/// click itself the window the user came from is no longer known: it must be followed before.
/// </para>
/// <para>
/// <b>Hiding buttons</b> (<see cref="AppSettings.HiddenPanelButtons"/>) collapses them; their actions stay in reach elsewhere
/// (Ctrl+G, the tray menu, Settings). The pin's mode ends with its button (<see cref="AppSettings.PanelStaysOpen"/>), so no
/// mode is on that the panel does not show. The minimum width follows the buttons shown (<see cref="MinWidthDip"/>): 36 DIPs
/// a button.
/// </para>
/// </remarks>
public sealed partial class ClipboardFlyout
{
    /// <summary>Segoe Fluent Icons "Pin" (E718): the pin's outline while the panel closes after pasting.</summary>
    private const string KeepOpenOffGlyph = "\uE718";

    /// <summary>Segoe Fluent Icons "PinFill" (E842): the filled pin while the panel stays open (also the pinned cards' badge).</summary>
    private const string KeepOpenOnGlyph = "\uE842";

    /// <summary>
    /// Follows the window of another app the user is in while the pinned panel shows (<see cref="UpdateForegroundTracking"/>);
    /// created at the first pinned presentation, <see langword="null"/> before.
    /// </summary>
    private ForegroundTracker? foregroundTracker;

    /// <summary>The pin's look last applied (<see langword="null"/>: none yet), so a settings change that keeps it restyles nothing.</summary>
    private bool? keepOpenLook;

    /// <summary>Whether this session logged that the pinned panel had to be put back on top (<see cref="KeepOnTop"/>): said once.</summary>
    private bool onTopRestoreLogged;

    /// <summary>
    /// How often a pinned panel on screen checks that its window is still always on top (<see cref="KeepOnTop"/>): a click
    /// into the window that already has the keyboard raises it over the panel without any notification to watch for.
    /// </summary>
    private static readonly TimeSpan OnTopCheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The check of <see cref="OnTopCheckInterval"/>, running only while a pinned panel shows (<see cref="UpdateForegroundTracking"/>);
    /// <see langword="null"/> until the first pinned presentation.
    /// </summary>
    private DispatcherQueueTimer? onTopTimer;

    /// <summary>
    /// Whether the panel stays open after pasting right now: pinned, with the pin shown (<see cref="AppSettings.PanelStaysOpen"/>).
    /// Read from the settings each time, so a change in Settings counts at once.
    /// </summary>
    public bool StaysOpen => controller.Settings.Current.PanelStaysOpen;

    /// <summary>
    /// The header's buttons, shown or hidden as Settings says, the pin's look, and the following of the foreground window
    /// while pinned. Called from the constructor, and by the controller after every settings change (idempotent and cheap:
    /// only what changed is touched).
    /// </summary>
    /// <remarks>
    /// A panel on screen that now shows more buttons than its width holds is widened (<see cref="FitHeaderWhileShown"/>); the
    /// next summon opens it at the right width anyway.
    /// </remarks>
    public void ApplyHeaderButtons()
    {
        var settings = controller.Settings.Current;
        var hidden = settings.HiddenPanelButtons;
        SetShown(KeepOpenButton, !PanelButtons.IsHidden(hidden, PanelButton.KeepOpen));
        SetShown(PauseButton, !PanelButtons.IsHidden(hidden, PanelButton.Pause));
        SetShown(GroupsToggle, !PanelButtons.IsHidden(hidden, PanelButton.Groups));
        SetShown(ClearButton, !PanelButtons.IsHidden(hidden, PanelButton.Clear));

        // The update button's visibility also depends on whether the update system started: its own method decides.
        ApplyUpdateButton();
        ApplyKeepOpenLook(settings.KeepPanelOpen);
        UpdateForegroundTracking();
        FitHeaderWhileShown();

        // No longer staying open (its pin hidden in Settings) while it waits behind another window: it goes away, as an
        // unpinned panel does when it loses the keyboard (OnActivated), instead of staying on top with nothing to close it.
        // One that has the keyboard stays until it is used or left. Not while a popup of its own shows (it decides then).
        if (IsOpen && !StaysOpen && openPopups == 0 && !ForegroundHelper.IsForeground(hwnd))
        {
            Conceal();
        }

        // Visibility is set only when it changes: every set asks the header for a new layout pass, and this runs after every
        // settings change, most of which have nothing to do with the header.
        static void SetShown(UIElement button, bool shown)
        {
            var visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            if (button.Visibility != visibility)
            {
                button.Visibility = visibility;
            }
        }
    }

    /// <summary>
    /// The user closed the panel — Esc, the shortcut while the panel is in front, Alt+F4, the Settings button: it goes away
    /// even while pinned (the pin stays on for the next summon). <see cref="Dismiss"/> is the other way, for an item that was
    /// used.
    /// </summary>
    /// <param name="restoreFocus">
    /// Give the keyboard back to the window the user was last in (Esc, the shortcut, Alt+F4); <see langword="false"/> when
    /// another of BetterClipboard's windows takes it (Settings).
    /// </param>
    public void CloseByUser(bool restoreFocus)
    {
        if (!IsOpen)
        {
            return;
        }

        // The overlays and the update dialog belong to the open panel (each is a window of its own that would stay behind).
        CloseImageOverlays();
        CloseUpdateDialog();

        // Off the screen, then the foreground to the target while this process still owns it, then the rest (PutAway's
        // remarks have why in this order).
        PutAway(restoreFocus ? controller.PasteTargetWindow : 0, finishSoon: true);
    }

    /// <summary>
    /// Gives the keyboard to the pinned panel where it stands, without moving it or resetting its view: the shortcut pressed
    /// while it waits behind the app the user types in (<see cref="PanelShortcutAction.BringForward"/>), and the tray's
    /// <i>Open clipboard</i> or a second launch while it shows. Enter then pastes into that app (the controller made it the
    /// paste target before calling this).
    /// </summary>
    public void BringForward()
    {
        if (!IsOpen)
        {
            return;
        }

        // The mouse may rest on the list: the keyboard now owns the selection until the mouse really moves.
        SuspendPointerSelection();
        Activate();
        ForegroundHelper.Activate(hwnd);
        if (openPopups == 0)
        {
            SearchBox.Focus(FocusState.Programmatic);
        }

        // The selected card's formats, for an Enter right away (reading ahead only runs while the panel shows).
        PrefetchSelected();
    }

    /// <summary>
    /// What remains of putting the panel away when it stays open after a use (<see cref="Dismiss"/> while pinned): its
    /// overlays and the update dialog close, and the keyboard goes back to the window the user was last in when asked.
    /// </summary>
    /// <param name="restoreFocus">Give the keyboard back to that window (<i>Copy only</i>, a failed paste).</param>
    private void KeepOpenAfterUse(bool restoreFocus)
    {
        CloseImageOverlays();
        CloseUpdateDialog();
        KeepOnTop();
        if (restoreFocus)
        {
            // Allowed: this process still owns the foreground (the panel had it). A target of 0 is skipped by the helper.
            ForegroundHelper.Activate(controller.PasteTargetWindow);
        }
    }

    /// <summary>
    /// Keeps the pinned panel above the window that takes the keyboard from it: puts it back into the topmost band when its
    /// window no longer has the flag. Called before a pinned paste or use hands the foreground on, whenever another window
    /// comes to the front while the pinned panel shows, and once a second meanwhile (<see cref="OnTopCheckInterval"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The panel is made always on top once, when its window is set up (<c>ConfigureChrome</c>), and an unpinned panel never
    /// needs more: it is the active window for as long as it shows. A pinned one shows while another window has the keyboard,
    /// and only the flag keeps it visible. In the QA (2026-11-20) one session's window had lost it by its first pinned paste,
    /// and the panel disappeared behind the target although it was not cloaked; the cause was not found (tooltips, peeks,
    /// put-aways and restarts all kept the flag). Hence the checks here. The timer is what catches a click into the window
    /// that already has the keyboard: that raises the window over a panel without the flag, and Windows reports no
    /// foreground change for it.
    /// </para>
    /// <para>
    /// Only when the flag is missing: putting a window back on top also raises it over another app's always-on-top window,
    /// which the user may have put in front on purpose. Logged once per session, content-free, so a recurrence shows in the
    /// log.
    /// </para>
    /// </remarks>
    private void KeepOnTop()
    {
        if (!presented || closingForExit || WindowInterop.IsTopmost(hwnd))
        {
            return;
        }

        bool restored = WindowInterop.MakeTopmost(hwnd);
        if (!onTopRestoreLogged)
        {
            onTopRestoreLogged = true;
            AppLog.Info(restored
                ? "The pinned panel was no longer always on top; it was put back on top."
                : "The pinned panel was no longer always on top, and Windows refused to put it back.");
        }
    }

    /// <summary>
    /// The user went to another window while the pinned panel showed a popup — a viewer, a peek, a menu, the update dialog
    /// (<see cref="ConcealIfUserLeft"/> looked who is in front): the panel stays up, as after any deactivation, and its viewers,
    /// peeks and update dialog close.
    /// </summary>
    /// <param name="cause">What made the panel look (which kind of popup showed), for the log line; content-free.</param>
    /// <remarks>
    /// They are windows of their own over the user's work — a viewer dims the whole monitor — shown for a use of the panel
    /// the user has left, and nothing else would close them until the user came back: the panel is no longer the active
    /// window, so no click elsewhere reaches it. A menu or flyout closed itself already (light dismiss), and nothing is logged
    /// for it.
    /// </remarks>
    private void CloseOverlaysOfPinnedPanel(string cause)
    {
        bool overlayShown = textViewer is not null || viewerPopup is not null || textPeekPopup is not null || peekPopup is not null
            || updateDialog is { IsOpen: true };
        if (!overlayShown)
        {
            return;
        }

        AppLog.Info($"The pinned panel stays up, its viewers and peeks closed: the user went to another window while {cause}.");
        CloseImageOverlays();
        CloseUpdateDialog();
    }

    /// <summary>
    /// The pin was clicked: the panel stays open after pasting from now on, or closes after pasting again — remembered until
    /// clicked again, also across restarts (the owner's choice).
    /// </summary>
    /// <param name="sender">The pin.</param>
    /// <param name="e">Click data.</param>
    private void KeepOpenButton_Click(object sender, RoutedEventArgs e)
    {
        bool pin = !controller.Settings.Current.KeepPanelOpen;
        try
        {
            // The settings change makes the controller apply the header again (ApplyHeaderButtons): the look, the tracking.
            controller.Settings.Update(s => s with { KeepPanelOpen = pin });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The change holds for this session (the store keeps it in memory); it is written with the next save. No change
            // event comes, so the header is applied below either way.
            AppLog.Warn($"Saving the panel's pin failed: {ex.Message}");
        }

        ApplyHeaderButtons();

        // Content-free: the state only. A support question "it does not close any more" finds its answer here.
        AppLog.Info(pin ? "Panel pinned: it stays open after pasting." : "Panel unpinned: it closes after pasting again.");

        // A click leaves the keyboard on the button; typing must search again.
        SearchBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Gives the pin the look of its state — the outline on the plain style, or the filled pin on the active style — with
    /// tooltip and accessible name saying what a click does. Whole styles from <c>App.xaml</c>, so their theme brushes
    /// resolve in the panel's own theme (CLAUDE.md §4), the same as the groups toggle.
    /// </summary>
    /// <param name="pinned">The stored pin (<see cref="AppSettings.KeepPanelOpen"/>), whether or not the pin shows.</param>
    private void ApplyKeepOpenLook(bool pinned)
    {
        if (keepOpenLook == pinned)
        {
            return;
        }

        keepOpenLook = pinned;
        KeepOpenButton.Style = (Style)Application.Current.Resources[pinned ? "IconButtonActiveStyle" : "IconButtonStyle"];
        KeepOpenGlyph.Glyph = pinned ? KeepOpenOnGlyph : KeepOpenOffGlyph;
        ToolTipService.SetToolTip(
            KeepOpenButton,
            pinned ? UiText.Translate("Pinned: the panel stays open after pasting. Click to unpin.") : UiText.Translate("Pin: keep the panel open after pasting"));
        AutomationProperties.SetName(
            KeepOpenButton,
            pinned ? UiText.Translate("Unpin: close the panel after pasting") : UiText.Translate("Pin: keep the panel open after pasting"));
    }

    /// <summary>
    /// Follows the foreground window, and checks once a second that the panel is still always on top, while the pinned panel
    /// is on screen; stops both otherwise (the panel put away, unpinned, the pin hidden, the app exiting). Idempotent; called
    /// whenever one of those changes.
    /// </summary>
    private void UpdateForegroundTracking()
    {
        if (presented && StaysOpen && !closingForExit)
        {
            foregroundTracker ??= new ForegroundTracker(window =>
            {
                // The window the user is in now is where the next paste goes, and the panel stays above it.
                controller.NoteForeignForeground(window);
                KeepOnTop();
            });
            if (!foregroundTracker.IsRunning && !foregroundTracker.Start())
            {
                // Without it, a pinned panel keeps pasting into the window it was opened from: worse, never wrong.
                AppLog.Warn("Following the window in front failed; the pinned panel pastes into the window it was opened from.");
            }

            if (onTopTimer is null)
            {
                onTopTimer = DispatcherQueue.CreateTimer();
                onTopTimer.Interval = OnTopCheckInterval;
                onTopTimer.IsRepeating = true;
                onTopTimer.Tick += OnTopTimer_Tick;
            }

            if (!onTopTimer.IsRunning)
            {
                onTopTimer.Start();
            }
        }
        else
        {
            foregroundTracker?.Stop();
            onTopTimer?.Stop();
        }
    }

    /// <summary>The once-a-second check while a pinned panel shows (<see cref="OnTopCheckInterval"/>).</summary>
    /// <param name="sender">The timer.</param>
    /// <param name="args">Unused.</param>
    private void OnTopTimer_Tick(DispatcherQueueTimer sender, object args) => KeepOnTop();

    /// <summary>
    /// Stops following the foreground window and checking the flag for good (the window is closing for the app's exit): the
    /// timer's handler is taken off too, since a handler on a timer that refers to the window keeps it alive (CLAUDE.md §4).
    /// </summary>
    private void DisposeForegroundTracking()
    {
        foregroundTracker?.Dispose();
        foregroundTracker = null;
        if (onTopTimer is not null)
        {
            onTopTimer.Stop();
            onTopTimer.Tick -= OnTopTimer_Tick;
            onTopTimer = null;
        }
    }

    /// <summary>
    /// The narrowest the panel may be now, in DIPs without the groups column: what its header's buttons need
    /// (<see cref="AppSettings.MinFlyoutWidthFor"/>), counting the update button only when this copy has one.
    /// </summary>
    /// <returns>The minimum width in DIPs.</returns>
    private int MinWidthDip() =>
        AppSettings.MinFlyoutWidthFor(PanelButtons.CountShown(controller.Settings.Current.HiddenPanelButtons, hasUpdateButton: controller.UpdateStatus is not null));

    /// <summary>
    /// The width a summon opens the panel at, in DIPs without the groups column: the remembered width, widened to what the
    /// header needs (<see cref="MinWidthDip"/>) without saving that, so hiding buttons gives the remembered width back.
    /// </summary>
    /// <param name="settings">The settings snapshot the summon reads.</param>
    /// <returns>The width in DIPs.</returns>
    private int PanelWidthDip(AppSettings settings) => Math.Max(settings.FlyoutWidth, MinWidthDip());

    /// <summary>
    /// Widens the panel on screen when its header now shows more buttons than its width holds (a button shown in Settings
    /// while a pinned panel stays up). It grows on its left, as for the groups column (<see cref="FlyoutPositioner.ExtendLeft"/>):
    /// the buttons and the list stay where they are, and the panel stays inside its monitor's work area — it grows right only
    /// at the work area's left edge. Not saved; a summon opens it at the needed width by itself.
    /// </summary>
    /// <remarks>
    /// Found in the QA (2026-11-20): keeping the top-left corner instead pushed a panel that stood at the screen's right edge
    /// 32 px past it, the gear half off the screen.
    /// </remarks>
    private void FitHeaderWhileShown()
    {
        if (!presented)
        {
            return;
        }

        var needed = FlyoutSizing.MinimumTrackSize(WindowInterop.GetScale(hwnd), groupsPaneOpen ? GroupsPaneDip : 0, MinWidthDip());
        var size = AppWindow.Size;
        if (size.Width < needed.Width)
        {
            var position = AppWindow.Position;
            var current = new ScreenRect(position.X, position.Y, position.X + size.Width, position.Y + size.Height);
            var next = FlyoutPositioner.ExtendLeft(current, needed.Width - size.Width, MonitorLookup.FromWindow(hwnd).WorkArea);
            AppWindow.MoveAndResize(new RectInt32(next.Left, next.Top, next.Width, next.Height));
        }
    }
}
