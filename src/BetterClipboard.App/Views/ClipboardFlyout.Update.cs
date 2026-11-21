using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Updates;
using BetterClipboard.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace BetterClipboard.App.Views;

/// <summary>
/// The panel's update button (left of Pause) and the update dialog it opens.
/// </summary>
/// <remarks>
/// <para>
/// <b>The button</b> is always there and says the state of updates by its look: like its neighbors while there is
/// nothing to do, filled with the accent color while an update waits that the user did not skip, and a small ring while
/// an approved update downloads or is installed. Its tooltip and accessible name say the same in words
/// (<see cref="UpdateText.ButtonTip"/>). It is hidden when the update system did not start, and when Settings hides it
/// (Buttons in the panel, CLAUDE.md §2.55): the dialog then opens over the Settings button. Once a week, while an update
/// waits, that tooltip opens by itself for a few seconds after a summon (ClipboardFlyout.UpdateReminder.cs) — not while the
/// button is hidden.
/// </para>
/// <para>
/// <b>The dialog</b> (<see cref="UpdateDialog"/>) is the button's flyout: the decision buttons on top, the release notes
/// below them. It is a window of its own so it can be larger than the panel, which has two consequences handled here:
/// </para>
/// <list type="bullet">
/// <item>
/// It is counted in <see cref="openPopups"/> from its <c>Opening</c> event, not from <c>Opened</c>: its window can take
/// the activation from the panel as it appears, and a panel that is deactivated with no popup counted conceals itself
/// (the same order as the image overlays, ClipboardFlyout.ImagePreview.cs).
/// </item>
/// <item>
/// While it is counted, leaving for another app does not conceal the panel. So when it closes, and when the panel is
/// deactivated while it is open, the panel looks at who is in front and conceals itself if the user went elsewhere: an
/// always-on-top panel must not stay in front of the browser that a link in the release notes just opened.
/// </item>
/// </list>
/// </remarks>
public sealed partial class ClipboardFlyout
{
    /// <summary>How the update button looks; kept so that a download's ten reports a second do not restyle it each time.</summary>
    private enum UpdateButtonLook
    {
        /// <summary>Not applied yet.</summary>
        Unset,

        /// <summary>No update system: the button is collapsed.</summary>
        Hidden,

        /// <summary>Nothing to do: an icon like its neighbors.</summary>
        Normal,

        /// <summary>Checking, downloading, verifying or installing: a ring instead of the glyph.</summary>
        Busy,

        /// <summary>An update waits: the accent fill.</summary>
        Attention,
    }

    /// <summary>
    /// How long after the dialog closed the panel looks at who is in front: when the dialog closes by Esc, Windows gives
    /// the keyboard back to the panel a moment later, and looking too early would take that for "the user left".
    /// </summary>
    private static readonly TimeSpan UpdateDialogSettleTime = TimeSpan.FromMilliseconds(80);

    /// <summary>The update dialog; created with the window (it is the update button's flyout).</summary>
    private UpdateDialog? updateDialog;

    private UpdateButtonLook updateButtonLook = UpdateButtonLook.Unset;

    /// <summary>The tooltip last set on the update button (it changes with every percent of a download).</summary>
    private string updateButtonTip = string.Empty;

    /// <summary>Whether the open update dialog is counted in <see cref="openPopups"/> (so it is uncounted exactly once).</summary>
    private bool updateDialogCounted;

    /// <summary>Set by <see cref="OpenUpdateDialogWhenShown"/>: the next summon opens the update dialog once it is on screen.</summary>
    private bool openUpdateDialogOnShow;

    /// <summary>
    /// Opens the update dialog: now when the panel is showing, else as soon as the next summon has put it on screen (the
    /// tray menu's "Update…" entry summons the panel right after calling this).
    /// </summary>
    public void OpenUpdateDialogWhenShown()
    {
        if (IsOpen)
        {
            ShowUpdateDialog();
        }
        else
        {
            openUpdateDialogOnShow = true;
        }
    }

    /// <summary>
    /// Creates the update dialog, makes it the update button's flyout and starts following the update status. Called
    /// once, from the constructor.
    /// </summary>
    private void InitializeUpdateButton()
    {
        // Under the button (over it when there is no room below), its right edge on the button's: the bubble then lies
        // over the panel, not beside it.
        updateDialog = new UpdateDialog(controller, hwnd, alignRight: true)
        {
            // An always-on-top panel would stay in front of the browser the link opens.
            LeavingForBrowser = () => Dismiss(restoreFocus: false),
        };
        updateDialog.Opening += UpdateDialog_Opening;
        updateDialog.Closed += UpdateDialog_Closed;

        // As the button's own flyout, a click opens it and a second click closes it (a Click handler calling ShowAt would
        // reopen it at once: the click that light-dismisses the bubble also reaches the button).
        UpdateButton.Flyout = updateDialog.Flyout;

        // The tooltip as an object, so the weekly reminder can open it from code (ClipboardFlyout.UpdateReminder.cs).
        InitializeUpdateToolTip();
        controller.UpdateStatusChanged += OnUpdateStatusChanged;
        Activated += OnActivatedDuringUpdateDialog;
        ApplyUpdateButton();
    }

    /// <summary>Stops following the update status and closes the dialog and the reminder (the window is closing for good).</summary>
    private void DisposeUpdateButton()
    {
        controller.UpdateStatusChanged -= OnUpdateStatusChanged;
        Activated -= OnActivatedDuringUpdateDialog;
        DisposeUpdateReminder();
        updateDialog?.Hide();
    }

    /// <summary>Closes the update dialog if it is open (a summon, a dismissal).</summary>
    private void CloseUpdateDialog() => updateDialog?.Hide();

    /// <summary>
    /// Opens the update dialog under its button, or under the Settings button while Settings hides the update button (the
    /// tray's <i>Update to …</i> must still open it); does nothing without an update system or while the dialog is open.
    /// </summary>
    /// <remarks>
    /// The dialog is the update button's own flyout (a click on that button toggles it), and a flyout can still be shown at
    /// any element: its placement is worked out from the element it opens at (<c>UpdateDialogLayout</c>, in its
    /// <c>Opening</c>), and both buttons are at the header's right, where it lies over the panel.
    /// </remarks>
    private void ShowUpdateDialog()
    {
        if (updateDialog is not { IsOpen: false } dialog || updateButtonLook == UpdateButtonLook.Hidden)
        {
            return;
        }

        dialog.ShowAt(UpdateButton.Visibility == Visibility.Visible ? UpdateButton : SettingsButton);
    }

    /// <summary>Opens the update dialog when a summon was asked to (<see cref="OpenUpdateDialogWhenShown"/>); called at the end of a summon.</summary>
    private void OpenPendingUpdateDialog()
    {
        if (openUpdateDialogOnShow)
        {
            openUpdateDialogOnShow = false;
            if (IsOpen)
            {
                ShowUpdateDialog();
            }
        }
    }

    /// <summary>The update status changed (UI thread): the button follows; the open dialog follows by itself.</summary>
    /// <param name="sender">The controller.</param>
    /// <param name="e">Event data.</param>
    private void OnUpdateStatusChanged(object? sender, EventArgs e) => ApplyUpdateButton();

    /// <summary>
    /// Gives the update button the look and the words of the current update status. Only what changed is touched.
    /// </summary>
    private void ApplyUpdateButton()
    {
        var snapshot = controller.UpdateStatus;

        // A check that runs while an update is already offered keeps the highlight: the offer is still there, and a
        // button that blinked on every look at the release list would cry wolf.
        var look = snapshot is null ? UpdateButtonLook.Hidden
            : snapshot.IsInstalling || (snapshot.Phase == UpdatePhase.Checking && snapshot.Offer is null) ? UpdateButtonLook.Busy
            : snapshot.WantsAttention ? UpdateButtonLook.Attention
            : UpdateButtonLook.Normal;
        // Shown while there is an update system and Settings does not hide it (Buttons in the panel, CLAUDE.md §2.55), checked
        // at every call: the setting changes without the look changing. Hidden, the dialog opens over Settings instead
        // (ShowUpdateDialog), and the weekly reminder, which is this button's tooltip, stays quiet (ScheduleUpdateReminder).
        var shown = look != UpdateButtonLook.Hidden && !PanelButtons.IsHidden(controller.Settings.Current.HiddenPanelButtons, PanelButton.Updates)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (UpdateButton.Visibility != shown)
        {
            UpdateButton.Visibility = shown;
            if (shown == Visibility.Collapsed)
            {
                CancelUpdateReminder();
            }
            else
            {
                // One button more in the header: a panel on screen narrower than that needs is widened (ClipboardFlyout.KeepOpen.cs).
                FitHeaderWhileShown();
            }
        }

        if (look != updateButtonLook)
        {
            updateButtonLook = look;

            // Whole styles, so their theme brushes resolve in the panel's own theme (see App.xaml).
            UpdateButton.Style = (Style)Application.Current.Resources[look == UpdateButtonLook.Attention ? "IconButtonAttentionStyle" : "IconButtonStyle"];
            bool busy = look == UpdateButtonLook.Busy;
            UpdateGlyph.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            UpdateRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            UpdateRing.IsActive = busy;
        }

        var tip = snapshot is null ? UiText.Translate("Updates") : UpdateText.ButtonTip(snapshot);
        if (tip != updateButtonTip)
        {
            updateButtonTip = tip;

            // The words go into the tooltip the button already owns: setting a new one would close a tooltip that is
            // open (the weekly reminder, or the mouse resting on the button during a download's progress).
            if (updateToolTip is { } owned)
            {
                owned.Content = tip;
            }
            else
            {
                ToolTipService.SetToolTip(UpdateButton, tip);
            }

            AutomationProperties.SetName(UpdateButton, tip);
        }

        // A reminder that waits or shows is about an update the user can take. Approved, skipped or gone: it ends.
        if (updateReminderStage != UpdateReminderStage.None && !UpdateReminder.IsWanted(snapshot))
        {
            CancelUpdateReminder();
        }
    }

    /// <summary>
    /// The dialog is about to appear: count it as an open popup now, before its window can take the activation, so the
    /// panel is not concealed by its own dialog and the panel's key model leaves the dialog's keys alone.
    /// </summary>
    /// <param name="sender">The dialog.</param>
    /// <param name="e">Event data.</param>
    private void UpdateDialog_Opening(object? sender, EventArgs e)
    {
        if (!updateDialogCounted)
        {
            updateDialogCounted = true;
            openPopups++;
        }

        // The dialog says everything the reminder says, and more: a reminder that waits or shows gives way to it. (The
        // dialog itself starts the reminder's new week, UpdateDialog.OnOpening.)
        CancelUpdateReminder();
    }

    /// <summary>
    /// The dialog closed: uncount it, then either give the keyboard back to the search box or — when the user went to
    /// another window while it was open — conceal the panel, which no deactivation will do any more.
    /// </summary>
    /// <param name="sender">The dialog.</param>
    /// <param name="e">Event data.</param>
    private async void UpdateDialog_Closed(object? sender, EventArgs e)
    {
        try
        {
            if (updateDialogCounted)
            {
                updateDialogCounted = false;
                openPopups = Math.Max(0, openPopups - 1);
            }

            // Windows hands the foreground back to the panel a moment after the bubble's window is gone.
            await Task.Delay(UpdateDialogSettleTime);
            if (closingForExit || !IsOpen || openPopups != 0)
            {
                // The app is exiting (an approved update closes it while the dialog still shows "Installing"): the window
                // is gone. Or the panel was concealed meanwhile, or another popup of ours took over.
                return;
            }

            if (ForegroundHelper.IsForeground(hwnd))
            {
                SearchBox.Focus(FocusState.Programmatic);
            }
            else if (!StaysOpen && !focusless)
            {
                // Without the keyboard the panel is never in front, and its hooks report a leave themselves
                // (ClipboardFlyout.Focusless.cs).
                // A pinned panel stays up while the user is elsewhere, like after any other deactivation (OnActivated).
                Conceal();
            }
        }
        catch (Exception ex)
        {
            // An async void handler must not let anything escape into the dispatcher.
            Core.Diagnostics.AppLog.Warn($"Closing the update dialog failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The panel was deactivated while the update dialog is open. That happens when the keyboard moves into the dialog's
    /// own window (nothing to do) and when the user switches to another app: then the dialog is closed, and its
    /// <c>Closed</c> handler conceals the panel. Light dismiss normally closes the dialog first; this is the net under it.
    /// </summary>
    /// <param name="sender">The window.</param>
    /// <param name="args">Activation data.</param>
    private void OnActivatedDuringUpdateDialog(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated || updateDialog is not { IsOpen: true } dialog)
        {
            return;
        }

        // Asked after the switch has completed (the event arrives in the middle of it). "Another process is in front" is
        // the test that can never mistake the dialog's own window for the user leaving.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (dialog.IsOpen && ForegroundHelper.IsAnotherProcessInForeground())
            {
                dialog.Hide();
            }
        });
    }
}
