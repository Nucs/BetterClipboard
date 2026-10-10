using System.Diagnostics;
using BetterClipboard.App.Interop;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Updates;
using BetterClipboard.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;

namespace BetterClipboard.App.Views;

/// <summary>
/// The weekly update reminder: while a newer release waits, the update button's own tooltip opens by itself for a few
/// seconds after a summon, at most once a week. Nothing to dismiss, nothing to switch off.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it is.</b> The same tooltip a mouse over the update button shows ("Update available: BetterClipboard 0.2.8"),
/// opened from code. So it looks and reads like every other tooltip of the panel, a screen reader announces it as one, it
/// never takes the keyboard (typing in the search box goes on under it), and it needs no click to go away.
/// </para>
/// <para>
/// <b>Where.</b> Over the panel's window, centered on the button, <see cref="UpdateReminderGapDip"/> above the window's
/// top edge, where it covers nothing of the panel: a tooltip is a window of its own and may leave the panel's bounds.
/// Left to itself, WinUI opens it flush on the button, across the window's edge (measured 2026-10-10), so the reminder
/// names the area to stay clear of (<see cref="ToolTip.PlacementRect"/>). When the panel sits at the top of the screen
/// there is no room over it (<see cref="UpdateReminder.FitsAbove"/>); the reminder then opens to the left of the button,
/// over the header's title, rather than under it, where it would cover the search box the user is about to type in.
/// </para>
/// <para>
/// <b>When</b> (<see cref="UpdateReminder.IsDue"/>, asked through <c>AppController.IsUpdateReminderDue</c>): an update is
/// offered that the user did not skip, this copy checks for updates by itself, and the last reminder is a week old.
/// A summon asks once it has done everything it owes the user (<see cref="ScheduleUpdateReminder"/>); the reminder opens
/// <see cref="UpdateReminderDelay"/> later and closes by itself <see cref="UpdateReminderDuration"/> after that, or with
/// the panel.
/// </para>
/// <para>
/// <b>What counts as shown.</b> Most uses of the panel last a second or two: Win+V, Enter. A reminder that counted from
/// the moment it opened would be spent on a glimpse, and one that counted only after a long look would come back at
/// every summon of a fast user. So the week starts once the reminder has been on screen for
/// <see cref="UpdateReminderCountsAfter"/> in total (<see cref="updateReminderSeenFor"/> adds up the glimpses), and it
/// also starts when the user opens the update dialog (<c>UpdateDialog</c> notes that itself).
/// </para>
/// <para>
/// <b>What ends it early</b> (<see cref="CancelUpdateReminder"/>): the panel going away or being summoned again, the
/// update dialog opening, the window moving or changing size (a tooltip does not follow its window), an update state
/// that no longer calls for a reminder, and the tooltip closing for a reason of its own (the mouse left the button).
/// </para>
/// <para>
/// <b>A click on it opens the update dialog</b>, as a click on the button would: a tooltip that appears by itself and
/// says "update available" gets clicked. A tooltip cannot be clicked as such, and a click on one used to cost the panel
/// its keyboard; ClipboardFlyout.ToolTipKeyboard.cs has the measurement and the repair, which asks
/// <see cref="IsPointerOverUpdateToolTip"/> to tell this tooltip from the others.
/// </para>
/// <para>UI thread only.</para>
/// </remarks>
public sealed partial class ClipboardFlyout
{
    /// <summary>Where the reminder is in its life.</summary>
    private enum UpdateReminderStage
    {
        /// <summary>Nothing is scheduled or showing.</summary>
        None,

        /// <summary>A summon found a reminder due; it opens when the timer fires.</summary>
        Waiting,

        /// <summary>The tooltip is open and has not been on screen long enough to count as shown.</summary>
        Unseen,

        /// <summary>The tooltip is open and counted: the next one is a week away. It closes when the timer fires.</summary>
        Seen,
    }

    /// <summary>
    /// The space between the reminder and what it stays clear of — the window's top edge, or the button's left edge
    /// when there is no room over the window — in DIPs. Enough to read as "next to", not as "stuck on".
    /// </summary>
    private const double UpdateReminderGapDip = 4;

    /// <summary>
    /// How long after a summon the reminder opens. Late enough that creating the tooltip's window is not part of the
    /// summon itself (which must put the panel on screen in one step) and that a tooltip appearing draws the eye; early
    /// enough to be seen in a use of the panel that lasts a second.
    /// </summary>
    private static readonly TimeSpan UpdateReminderDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>How long the reminder must have been on screen, in total, to count as shown (see the class remarks).</summary>
    private static readonly TimeSpan UpdateReminderCountsAfter = TimeSpan.FromMilliseconds(1500);

    /// <summary>How long an undisturbed reminder stays open: time to read one line twice, like a tooltip of Windows.</summary>
    private static readonly TimeSpan UpdateReminderDuration = TimeSpan.FromSeconds(6);

    /// <summary>
    /// The update button's tooltip, as an object rather than the string XAML would wrap: only a <see cref="ToolTip"/>
    /// that the button owns can be opened from code. Created in <see cref="InitializeUpdateButton"/>.
    /// </summary>
    private ToolTip? updateToolTip;

    /// <summary>Drives the reminder from stage to stage; created on first use, one shot per start.</summary>
    private DispatcherQueueTimer? updateReminderTimer;

    private UpdateReminderStage updateReminderStage = UpdateReminderStage.None;

    /// <summary>Stopwatch timestamp of the moment the open reminder appeared.</summary>
    private long updateReminderOpenedAt;

    /// <summary>
    /// How long reminders that ended before they counted were on screen, added up since the last one that counted. Kept
    /// for the session only: after a restart the glimpses start from zero, which costs at most one more of them.
    /// </summary>
    private TimeSpan updateReminderSeenFor = TimeSpan.Zero;

    /// <summary>
    /// Gives the update button its tooltip as an object the reminder can open. Called once, from
    /// <see cref="InitializeUpdateButton"/>, before the first <see cref="ApplyUpdateButton"/>.
    /// </summary>
    private void InitializeUpdateToolTip()
    {
        var tip = new ToolTip { Content = "Updates" };
        tip.Closed += UpdateToolTip_Closed;
        ToolTipService.SetToolTip(UpdateButton, tip);
        updateToolTip = tip;
    }

    /// <summary>
    /// Asks, at the end of a summon, whether the weekly reminder is due, and if so lets it open a moment later. Does
    /// nothing while a reminder is already waiting or showing.
    /// </summary>
    private void ScheduleUpdateReminder()
    {
        if (updateReminderStage != UpdateReminderStage.None || !IsOpen || !controller.IsUpdateReminderDue)
        {
            return;
        }

        updateReminderStage = UpdateReminderStage.Waiting;
        StartUpdateReminderTimer(UpdateReminderDelay);
    }

    /// <summary>
    /// Ends the reminder wherever it is: a waiting one never opens, an open one closes. A reminder that had not counted
    /// yet keeps the time it was on screen, so the next one counts sooner (<see cref="updateReminderSeenFor"/>).
    /// Safe to call at any time, also when there is no reminder.
    /// </summary>
    private void CancelUpdateReminder()
    {
        updateReminderTimer?.Stop();
        var stage = updateReminderStage;

        // Cleared before the tooltip closes: closing raises Closed, whose handler must find nothing left to end.
        updateReminderStage = UpdateReminderStage.None;
        if (stage is not (UpdateReminderStage.Unseen or UpdateReminderStage.Seen))
        {
            return;
        }

        if (stage == UpdateReminderStage.Unseen)
        {
            updateReminderSeenFor += Stopwatch.GetElapsedTime(updateReminderOpenedAt);
        }

        AppWindow.Changed -= OnWindowChangedDuringUpdateReminder;
        if (updateToolTip is { } tip)
        {
            try
            {
                tip.IsOpen = false;

                // Back to where a mouse over the button expects its tooltip: on the button, wherever WinUI finds room.
                tip.Placement = PlacementMode.Top;
                tip.ClearValue(ToolTip.PlacementRectProperty);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // The window is closing under it; there is nothing left to close.
                AppLog.Info($"Closing the update reminder failed ({ex.GetType().Name}).");
            }
        }
    }

    /// <summary>Stops the reminder for good and lets go of its timer and tooltip events (the window is closing).</summary>
    private void DisposeUpdateReminder()
    {
        CancelUpdateReminder();
        if (updateReminderTimer is { } timer)
        {
            timer.Tick -= UpdateReminderTimer_Tick;
            updateReminderTimer = null;
        }

        if (updateToolTip is { } tip)
        {
            tip.Closed -= UpdateToolTip_Closed;
        }
    }

    /// <summary>Starts (or restarts) the reminder's one-shot timer.</summary>
    /// <param name="interval">When it fires; a time that already passed fires at once.</param>
    private void StartUpdateReminderTimer(TimeSpan interval)
    {
        if (updateReminderTimer is null)
        {
            updateReminderTimer = DispatcherQueue.CreateTimer();
            updateReminderTimer.IsRepeating = false;
            updateReminderTimer.Tick += UpdateReminderTimer_Tick;
        }

        updateReminderTimer.Stop();
        updateReminderTimer.Interval = interval > TimeSpan.Zero ? interval : TimeSpan.FromMilliseconds(1);
        updateReminderTimer.Start();
    }

    /// <summary>
    /// The reminder's next step is due: open it, count it as shown, or close it, by <see cref="updateReminderStage"/>.
    /// </summary>
    /// <param name="sender">The timer.</param>
    /// <param name="args">Unused.</param>
    private void UpdateReminderTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            switch (updateReminderStage)
            {
                case UpdateReminderStage.Waiting:
                    updateReminderStage = UpdateReminderStage.None;

                    // Asked again: the panel may have gone, a menu or the update dialog may have opened, and the update
                    // may have been approved or skipped since the summon.
                    if (!CanShowUpdateReminder())
                    {
                        break;
                    }

                    // The stage says "open" before the tooltip is: whatever goes wrong while it opens, the catch below
                    // ends up in CancelUpdateReminder, which then closes what did open.
                    updateReminderOpenedAt = Stopwatch.GetTimestamp();
                    updateReminderStage = UpdateReminderStage.Unseen;
                    if (!OpenUpdateReminder())
                    {
                        updateReminderStage = UpdateReminderStage.None;
                        break;
                    }

                    StartUpdateReminderTimer(UpdateReminderCountsAfter - updateReminderSeenFor);
                    break;

                case UpdateReminderStage.Unseen:
                    // On screen long enough, this time and the times before: the week starts now.
                    updateReminderStage = UpdateReminderStage.Seen;
                    updateReminderSeenFor = TimeSpan.Zero;
                    controller.NoteUpdateReminderShown(byReminder: true);
                    StartUpdateReminderTimer(UpdateReminderDuration - Stopwatch.GetElapsedTime(updateReminderOpenedAt));
                    break;

                case UpdateReminderStage.Seen:
                    CancelUpdateReminder();
                    break;
            }
        }
        catch (Exception ex)
        {
            // A timer handler must not let anything escape into the dispatcher, and a reminder is never worth a crash.
            AppLog.Warn($"The update reminder failed: {ex.Message}");
            CancelUpdateReminder();
        }
    }

    /// <summary>
    /// Whether the reminder may open now: the panel is on screen with nothing of ours over it, the update button shows
    /// that an update waits, its tooltip is not open already, and the reminder is still due.
    /// </summary>
    /// <returns><see langword="true"/> to open it.</returns>
    private bool CanShowUpdateReminder() =>
        IsOpen
        && openPopups == 0
        && drag is null
        && updateButtonLook == UpdateButtonLook.Attention
        && updateToolTip is { IsOpen: false }
        && controller.IsUpdateReminderDue;

    /// <summary>
    /// Opens the update button's tooltip: over the window when there is room there, else to the left of the button.
    /// </summary>
    /// <returns><see langword="true"/> when the tooltip is open.</returns>
    private bool OpenUpdateReminder()
    {
        if (updateToolTip is not { } tip)
        {
            return false;
        }

        bool above = UpdateReminder.FitsAbove(RoomAboveWindowDip());
        tip.Placement = above ? PlacementMode.Top : PlacementMode.Left;
        tip.PlacementRect = UpdateReminderAnchor(above);
        tip.IsOpen = true;
        if (!tip.IsOpen)
        {
            tip.Placement = PlacementMode.Top;
            tip.ClearValue(ToolTip.PlacementRectProperty);
            return false;
        }

        // A tooltip stays where it opened: when the window moves or changes size under it, it must go.
        AppWindow.Changed += OnWindowChangedDuringUpdateReminder;
        return true;
    }

    /// <summary>
    /// The area the reminder is placed against, in the update button's own coordinates (DIPs): WinUI puts a tooltip flush
    /// on the edge of this area, centered on it.
    /// </summary>
    /// <param name="above">
    /// <see langword="true"/> for the reminder over the window: the area is the button stretched up past the window's top
    /// edge. <see langword="false"/> for the reminder left of the button: the button widened to its left.
    /// </param>
    /// <returns>The area; its far edge lies <see cref="UpdateReminderGapDip"/> beyond the window's top or the button's left.</returns>
    private Rect UpdateReminderAnchor(bool above)
    {
        double width = UpdateButton.ActualWidth;
        double height = UpdateButton.ActualHeight;
        if (!above)
        {
            return new Rect(-UpdateReminderGapDip, 0, width + UpdateReminderGapDip, height);
        }

        // How far the button's top lies under the top of the window's content (the header's padding).
        double inset = Math.Max(0, UpdateButton.TransformToVisual(null).TransformPoint(default).Y);
        return new Rect(0, -(inset + UpdateReminderGapDip), width, height + inset + UpdateReminderGapDip);
    }

    /// <summary>
    /// The free height over the panel on its monitor: from the top of the work area to the top of the window's content,
    /// in DIPs.
    /// </summary>
    /// <returns>The room in DIPs; 0 when it cannot be measured (the reminder then opens beside the button).</returns>
    private double RoomAboveWindowDip()
    {
        var (workArea, scale) = MonitorLookup.FromWindow(hwnd);
        var (_, clientTop) = WindowInterop.GetClientOrigin(hwnd);
        return scale > 0 ? (clientTop - workArea.Top) / scale : 0;
    }

    /// <summary>
    /// Whether the mouse pointer is over the update button's open tooltip, wherever that tooltip is: over the window as
    /// the reminder, or by the button after the mouse rested there. Used to tell a click on this tooltip from a click
    /// on any other (ClipboardFlyout.ToolTipKeyboard.cs).
    /// </summary>
    /// <remarks>
    /// The tooltip's place comes from XAML (its offset from the window content's origin, negative over the window) and
    /// is turned into screen pixels like the update dialog's (<c>UpdateDialog.Arrange</c>). Measured 2026-10-10 at 100 %:
    /// computed left 787, top 256, 231 × 32, against 784–1020 × 256–287 in a screenshot.
    /// </remarks>
    /// <returns><see langword="true"/> when the tooltip is open and the pointer is inside it.</returns>
    private bool IsPointerOverUpdateToolTip()
    {
        if (updateToolTip is not { IsOpen: true } tip || Root.XamlRoot is not { } root || ScreenPointer.Cursor() is not { } cursor)
        {
            return false;
        }

        try
        {
            double scale = root.RasterizationScale > 0 ? root.RasterizationScale : 1.0;
            var (clientLeft, clientTop) = WindowInterop.GetClientOrigin(hwnd);
            var origin = tip.TransformToVisual(null).TransformPoint(default);
            double left = clientLeft + (origin.X * scale);
            double top = clientTop + (origin.Y * scale);
            return cursor.X >= left && cursor.X < left + (tip.ActualWidth * scale)
                && cursor.Y >= top && cursor.Y < top + (tip.ActualHeight * scale);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // The tooltip left the tree between the click and here: it cannot be under the pointer any more.
            return false;
        }
    }

    /// <summary>
    /// The tooltip closed. When the reminder did not close it itself, something else did (the mouse came over the button
    /// and left again, which closes any tooltip): the reminder is over, and its timer must not act on a closed tooltip.
    /// </summary>
    /// <param name="sender">The tooltip.</param>
    /// <param name="e">Event data.</param>
    private void UpdateToolTip_Closed(object sender, RoutedEventArgs e)
    {
        if (updateReminderStage is UpdateReminderStage.Unseen or UpdateReminderStage.Seen)
        {
            CancelUpdateReminder();
        }
    }

    /// <summary>
    /// The window moved or changed size while the reminder is open (a drag of its background or of a border, the groups
    /// column opening): the tooltip would stay behind, pointing at nothing, so it closes.
    /// </summary>
    /// <param name="sender">The window.</param>
    /// <param name="args">What changed.</param>
    private void OnWindowChangedDuringUpdateReminder(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange)
        {
            CancelUpdateReminder();
        }
    }
}
