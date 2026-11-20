using System.Diagnostics;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Windows.Input;
using Microsoft.UI.Xaml;

namespace BetterClipboard.App.Views;

/// <summary>
/// The panel goes away when the user goes to another window, also while one of its own popups shows (a viewer, a peek, a
/// menu, a flyout); and a hover peek never opens by itself over a mouse that did not move.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bug</b> (the owner's report, 2026-11-20: "if you open "View whole text" eye icon (maybe for image too) then the
/// window doesn't close after we closed the viewing panel that opened. It should've closed."). <see cref="OnActivated"/>
/// conceals the panel when it is deactivated, but only while no popup of its own is counted (<see cref="openPopups"/>),
/// because a popup that took the activation must not conceal its own panel. In the WinUI this app ships, none does:
/// measured in the QA (CLAUDE.md §2.56), the panel stays the foreground window while a viewer, a peek, a menu, a flyout or
/// the update dialog shows, and the popups' windows are owned by the panel's window. So a deactivation while one of them
/// showed was always the user going elsewhere: another app from the taskbar or with Alt+Tab, Settings, a click beside a
/// menu. It was ignored. A menu then closed by itself (light dismiss), a viewer stayed and kept dimming the monitor, and
/// the panel stayed on top of the other window for good: it was no longer the active window, so no later click elsewhere
/// could deactivate it, and only the shortcut pressed again put it away. The update dialog alone had checks of its own
/// (ClipboardFlyout.Update.cs).
/// </para>
/// <para>
/// <b>Now</b> a deactivation while a popup is counted, and the last counted popup closing, ask who is in front once Windows
/// has finished the switch (<see cref="ConcealIfUserLeft"/>). The panel, or a window it owns (its popups), keeps everything
/// as it is. Any other window closes the viewers and peeks and the update dialog, and conceals the panel: another app, and
/// also Settings, which conceals the panel when nothing of it is open too (a click on Settings is a click elsewhere).
/// </para>
/// <para>
/// <b>The keyboard after a popup.</b> A popup that closes gives the keyboard back to a viewer that is still open, or to the
/// search box, only while the panel shows and is in front (<see cref="GiveKeyboardBackAfterPopup"/>): the popup may have
/// closed because the user went elsewhere, and a programmatic focus in the panel must not reach after the keyboard then. A
/// precaution more than a finding: Settings runs on the panel's own thread, and Win32's <c>SetFocus</c> activates the
/// window that receives the focus.
/// </para>
/// <para>
/// <b>A peek that opened by itself</b> (<see cref="MouseMovedSincePanelChanged"/>). The hover peeks started their countdown
/// on WinUI's "pointer entered", which also comes for a card that appears under a mouse lying still: WinUI replays the last
/// pointer position after every frame that changed the picture. Found in the QA (2026-11-20): Esc or the X closed the text
/// viewer, the mouse still rested where the eye had been, and 450 ms later the text peek opened with the same text, so the
/// viewer looked as if Esc had not closed it (4 of 8 and 3 of 8 tries, depending on whether the look came before the
/// peek). The same could happen after a summon under the mouse, a reload, and a peek closed by a click or a key. A
/// countdown now starts only once the mouse really moved since the panel last changed under it: the hover selection's rule
/// (<see cref="PointerSelectionGate"/>), asked without taking the selection.
/// </para>
/// <para>UI thread only.</para>
/// </remarks>
public sealed partial class ClipboardFlyout
{
    /// <summary>
    /// How long after the deactivation, or after the last popup closed, the panel looks at who is in front: Windows is in
    /// the middle of the switch when the deactivation arrives. The update dialog waits as long for the same reason
    /// (ClipboardFlyout.Update.cs).
    /// </summary>
    private static readonly TimeSpan LeaveSettleTime = TimeSpan.FromMilliseconds(80);

    /// <summary>
    /// The longest the panel waits for Windows to name a foreground window at all: during a switch there can be none for a
    /// moment. Still none after that (a locked screen, a secure desktop), the panel counts as left: it is not where the
    /// keyboard is either.
    /// </summary>
    private static readonly TimeSpan NoForegroundWait = TimeSpan.FromMilliseconds(500);

    /// <summary>How often the panel looks again while Windows names no foreground window.</summary>
    private static readonly TimeSpan NoForegroundPoll = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Set while a look started by <see cref="ConcealIfUserLeft"/> waits: the deactivation and the light-dismissed menu's
    /// closing come from one switch, and one look serves both.
    /// </summary>
    private bool leaveLookPending;

    /// <summary>
    /// Once Windows has finished the current switch, looks whether the user went to another window, and if so closes the
    /// panel's viewers, peeks and update dialog and conceals the panel — a pinned panel (CLAUDE.md §2.55) only loses those
    /// and stays up. A no-op when the panel is not showing, when it or a window it owns is in front, and while another look
    /// is pending.
    /// </summary>
    /// <param name="cause">
    /// What made the panel look, for the log line (content-free: which kind of popup showed, or that the last one closed).
    /// </param>
    /// <remarks>
    /// <para>
    /// Called from <see cref="OnActivated"/> when the panel is deactivated while a popup of its own is counted (a
    /// deactivation with none conceals the panel at once, as it always did), and from <see cref="Popup_Closed"/> when the
    /// last counted popup closed: a deactivation can come while Windows names no foreground window yet, and a look then
    /// cannot tell; the popup closing (a menu closes by itself when its window is left) asks again.
    /// </para>
    /// <para>
    /// "In front" is <see cref="ForegroundHelper.IsForeground"/>: the panel's window, or a window it owns. The panel's
    /// popups are windows owned by the panel's (measured in the QA), so a popup that took the keyboard would never count as
    /// the user leaving. Settings is not owned by the panel: going to it conceals the panel, as it does when nothing of the
    /// panel is open.
    /// </para>
    /// <para>
    /// <c>async void</c>: it is started from event handlers and awaited by nobody; it catches everything itself.
    /// </para>
    /// </remarks>
    private async void ConcealIfUserLeft(string cause)
    {
        if (leaveLookPending || closingForExit || !IsOpen)
        {
            return;
        }

        leaveLookPending = true;
        try
        {
            await Task.Delay(LeaveSettleTime);

            // During a switch Windows can name no foreground window for a moment, and neither question below can be
            // answered then: wait for one, briefly.
            var waited = Stopwatch.StartNew();
            while (!ForegroundHelper.IsForeground(hwnd) && !ForegroundHelper.IsAnotherWindowInForeground(hwnd) &&
                waited.Elapsed < NoForegroundWait)
            {
                await Task.Delay(NoForegroundPoll);
            }

            // Concealed meanwhile (the user pasted, pressed Esc, the shortcut again), the app is exiting, or the panel or
            // one of its popups has the keyboard: nothing to do.
            if (closingForExit || !IsOpen || ForegroundHelper.IsForeground(hwnd))
            {
                return;
            }

            if (StaysOpen)
            {
                // Pinned: the panel itself stays up while the user works elsewhere; only what it showed for the use they
                // left goes (ClipboardFlyout.KeepOpen.cs).
                CloseOverlaysOfPinnedPanel(cause);
                return;
            }

            AppLog.Info($"The panel went away: the user went to another window while {cause}.");

            // The viewers and peeks are windows of their own that would stay on screen without the panel (a viewer dims
            // the whole monitor); so is the update dialog. Menus and flyouts closed themselves when their window was left.
            CloseImageOverlays();
            CloseUpdateDialog();
            Conceal();
        }
        catch (Exception ex)
        {
            // async void: nothing above may let an exception escape into the dispatcher.
            AppLog.Warn($"Looking whether the user left the panel failed: {ex.Message}");
        }
        finally
        {
            leaveLookPending = false;
        }
    }

    /// <summary>
    /// Names the kind of popup the panel shows, for <see cref="ConcealIfUserLeft"/>'s log line: one phrase, content-free.
    /// </summary>
    /// <returns>"the text viewer showed", "a peek showed", "a menu or flyout showed", and so on.</returns>
    /// <remarks>Read at the deactivation: a menu closes by itself during the switch, so a later read would name nothing.</remarks>
    private string DescribeOpenPopups()
    {
        if (textViewer is not null)
        {
            return "the text viewer showed";
        }

        if (viewerPopup is not null)
        {
            return "the image viewer showed";
        }

        if (textPeekPopup is not null || peekPopup is not null)
        {
            return "a peek showed";
        }

        if (updateDialog is { IsOpen: true })
        {
            return "the update dialog showed";
        }

        return "a menu or flyout showed";
    }

    /// <summary>
    /// Whether the mouse really moved since the panel last changed under it (a summon, a reload, the keyboard's selection,
    /// a popup that closed): the hover selection's rule (<see cref="PointerSelectionGate.HasMoved"/>), asked without taking
    /// the selection. The peeks start their countdown only then (class remarks).
    /// </summary>
    /// <returns>
    /// <see langword="true"/> once the cursor has left the spot it was on at that change by more than the drag threshold, or
    /// the mouse owns the selection already (it moved, or its wheel turned over the list); <see langword="false"/> while it
    /// rests there, and when its position cannot be read (a secure desktop): a peek then waits for the next move.
    /// </returns>
    /// <remarks>
    /// Never <see cref="IsPointerSelecting"/>, which hands the selection to the mouse: the peeks' handlers run on the card's
    /// inner elements before the list's own handler, which would then find the selection taken and skip the row under the
    /// mouse.
    /// </remarks>
    private bool MouseMovedSincePanelChanged() => ScreenPointer.Cursor() is { } cursor && pointerGate.HasMoved(cursor.X, cursor.Y);

    /// <summary>
    /// After one of the panel's popups closed: the keyboard goes to a viewer that is still open (its text, its backdrop),
    /// else to the search box, but only while the panel shows and is in front (class remarks).
    /// </summary>
    /// <remarks>
    /// Not when another window is in front: the popup closed because the user went there (a menu closes by itself when its
    /// window is left), and <see cref="ConcealIfUserLeft"/> is about to conceal the panel. A panel that is in front again
    /// later gets the keyboard from its own activation (<see cref="OnActivated"/>).
    /// </remarks>
    private void GiveKeyboardBackAfterPopup()
    {
        if (!IsOpen || !ForegroundHelper.IsForeground(hwnd))
        {
            return;
        }

        if (!FocusOpenViewer())
        {
            SearchBox.Focus(FocusState.Programmatic);
        }
    }
}
