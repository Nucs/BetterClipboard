using BetterClipboard.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace BetterClipboard.App.Views;

/// <summary>
/// Keeps the keyboard in the panel when the user clicks on one of its tooltips.
/// </summary>
/// <remarks>
/// <para>
/// <b>The trap</b> (measured 2026-10-10 on Windows 11, WinUI 2.3.9). A tooltip is a window of its own, and nothing in it
/// can be clicked or focused. A click on it still arrives in the panel's XAML, finds nothing that takes the keyboard,
/// and WinUI then gives the keyboard to the scroll viewer that wraps the whole window content — an <i>ancestor</i> of
/// <see cref="Root"/>. From then on the panel is in front and deaf: its key model listens on <see cref="Root"/>, which
/// that scroll viewer's keys never pass through, and the search box shows no caret. Typed letters went nowhere until
/// the panel was summoned again.
/// </para>
/// <para>
/// A mouse rarely clicks the tooltip it just made appear. It matters since the update button's tooltip opens by itself
/// once a week and says "Update available" (ClipboardFlyout.UpdateReminder.cs): that one gets clicked.
/// </para>
/// <para>
/// <b>The repair.</b> Whenever an element of the panel loses the keyboard while a tooltip is open, the panel looks at
/// who has it now. If nobody useful does — no element of the panel, none inside a menu or flyout — the search box gets
/// it back, the way it does after a drag of the background (<see cref="EndDrag"/>). A click on the update button's own
/// tooltip also opens the update dialog, as a click on the button would: that is what the click meant.
/// </para>
/// <para>
/// It only acts while some tooltip is open, so every other way the keyboard moves in the panel is left as it was.
/// UI thread only.
/// </para>
/// </remarks>
public sealed partial class ClipboardFlyout
{
    /// <summary>Starts watching for the keyboard leaving the panel's elements. Called once, from the constructor.</summary>
    private void InitializeToolTipKeyboardGuard() => Root.LostFocus += Root_LostFocus;

    /// <summary>Stops watching (the window is closing for good).</summary>
    private void DisposeToolTipKeyboardGuard() => Root.LostFocus -= Root_LostFocus;

    /// <summary>
    /// An element of the panel lost the keyboard. That happens all the time (the arrow keys walk the cards, a click
    /// lands on a tab), so the tests that end most calls come first and cost nothing; the look at who has the keyboard
    /// now is queued, because WinUI raises this event before the new holder is settled.
    /// </summary>
    /// <param name="sender">The root grid.</param>
    /// <param name="e">Event data.</param>
    private void Root_LostFocus(object sender, RoutedEventArgs e)
    {
        // A popup of ours (a menu, the update dialog, an image overlay) takes the keyboard on purpose; a drag gives it
        // back by itself when it ends; a concealed panel has none to keep.
        if (!IsOpen || openPopups != 0 || drag is not null || !IsAnyToolTipOpen())
        {
            return;
        }

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, RecoverKeyboardFromToolTip);
    }

    /// <summary>
    /// Whether a popup is open that is not one of ours: with <see cref="openPopups"/> at zero that is a tooltip (the
    /// only other popup, a text box's own menu, holds the keyboard itself and is told apart later).
    /// </summary>
    /// <returns><see langword="true"/> when the window has an open popup.</returns>
    private bool IsAnyToolTipOpen() =>
        Root.XamlRoot is { } root && VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Count > 0;

    /// <summary>
    /// Gives the keyboard back to the search box if a click on a tooltip left it with nobody (class remarks); a click
    /// on the update button's tooltip opens the update dialog instead, which takes the keyboard and returns it to the
    /// search box when it closes.
    /// </summary>
    private void RecoverKeyboardFromToolTip()
    {
        try
        {
            if (closingForExit || !IsOpen || openPopups != 0 || drag is not null || Root.XamlRoot is not { } root)
            {
                return;
            }

            // Another window in front: the user left, and the panel is about to conceal itself. Not ours to take back.
            if (!IsKeyboardOrphaned(root) || !ForegroundHelper.IsForeground(hwnd))
            {
                return;
            }

            if (IsPointerOverUpdateToolTip())
            {
                ShowUpdateDialog();

                // Counted from its Opening event, which ShowAt raises before it returns: the dialog is on its way and
                // takes the keyboard. Not counted: it could not open (no update button), so the search box it is.
                if (openPopups != 0)
                {
                    return;
                }
            }

            SearchBox.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            // Queued work must not let anything escape into the dispatcher.
            Core.Diagnostics.AppLog.Warn($"Giving the keyboard back after a click on a tooltip failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether the keyboard is with nobody who can use it: no element has it, or one that is neither part of the panel's
    /// content nor inside an open popup — in practice the scroll viewer around the window content, where WinUI parks the
    /// keyboard after a click on something that takes none.
    /// </summary>
    /// <param name="root">The window's XAML root.</param>
    /// <returns><see langword="true"/> when the search box should take the keyboard back.</returns>
    private bool IsKeyboardOrphaned(XamlRoot root)
    {
        if (FocusManager.GetFocusedElement(root) is not DependencyObject focused)
        {
            return true;
        }

        var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(root);
        for (DependencyObject? current = focused; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, Root))
            {
                return false; // an element of the panel has it
            }

            foreach (var popup in popups)
            {
                if (ReferenceEquals(current, popup) || ReferenceEquals(current, popup.Child))
                {
                    return false; // an element inside a menu or flyout has it (a text box's own menu, for one)
                }
            }
        }

        return true;
    }
}
