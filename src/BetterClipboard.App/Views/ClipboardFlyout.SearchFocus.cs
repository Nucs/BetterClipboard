using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace BetterClipboard.App.Views;

/// <summary>
/// Keeps the keyboard focus in the panel's search box: no click, key or WinUI default moves it to another element of the
/// panel. Only the panel's popups — its menus, flyouts, dialogs, viewers and peeks — take it, and they give it back when
/// they close (the owner's rule, 2026-11-21: "We are not supposed to lose focus from the search box (unless a panel/modal
/// opens)"; CLAUDE.md §2.66).
/// </summary>
/// <remarks>
/// <para>
/// <b>What went wrong.</b> Every control of the panel took the focus when clicked: a tab, a header button (the logo, the pin,
/// updates, pause, groups, clear), a group icon, a card, the list; and Tab moved it on to the next control. The panel's keys
/// still worked from there (<see cref="Root_PreviewKeyDown"/> sees every key typed in its tree), but typed text went to the
/// clicked control, which ignores it: the search stopped following the keyboard until a click into the box or Ctrl+F. A
/// few places gave the focus back by hand — the pin's click, a drag of the background or of the tab strip, a closing popup,
/// a click on a tooltip (ClipboardFlyout.ToolTipKeyboard.cs) — and everything else kept it. The owner's log of 2026-11-21
/// shows four clicks into the search box within six minutes.
/// </para>
/// <para>
/// <b>One guard for all of them.</b> WinUI announces every focus move before it happens (<see cref="FocusManager.GettingFocus"/>,
/// raised for every window of this thread, Settings' included), and a move to the panel's own surface — any element of
/// its tree but the search box, or an element WinUI wraps the window's content in — is refused
/// (<see cref="PanelFocus.Decide"/>): cancelled while the search box has the focus, sent to the search box while the focus is
/// somewhere else (a menu that is closing hands it back to the element it was opened from, a window activation picks the
/// first control). Where a move cannot be refused (WinUI marks some moves as not cancellable), the search box takes the
/// focus back right after it (<see cref="RepairSearchFocus"/>). Elements of a popup, and of other windows, are never
/// touched (<see cref="FocusTargetOf"/>). The guard acts only while the panel is on screen.
/// </para>
/// <para>
/// <b>Without the keyboard</b> (ClipboardFlyout.Focusless.cs) a move is only cancelled, never redirected: the panel's keys
/// come through its hook whatever WinUI's focus is, and a focus move would hand the panel's window Win32's focus, an
/// activation in the background. Cancelling also spares the background activation a click used to cause.
/// </para>
/// <para>
/// <b>What a click on a tab still does.</b> It picks the tab, and the strip scrolls to show it whole
/// (<see cref="RevealClickedTab"/>: WinUI did that only for a tab that took the focus). Left and Right then switch tabs, as
/// they did while the clicked tab had the focus (<see cref="NoteTabPressed"/> sets <see cref="arrowNavigating"/>), and the
/// Menu key or Shift+F10 opens that tab's menu (<see cref="clickedTab"/>), as the README's keys table promises, until another
/// key or a click elsewhere.
/// </para>
/// <para>
/// <b>Costs, by design:</b> Tab and Shift+Tab do nothing in the panel (<see cref="Root_PreviewKeyDown"/> takes them; they
/// would only try a move the guard refuses), and a screen reader cannot move the focus to the panel's buttons, which it can
/// still invoke through UI Automation. A clicked card or group icon that is half out of view is no longer scrolled into view
/// by its focus.
/// </para>
/// </remarks>
public sealed partial class ClipboardFlyout
{
    /// <summary>
    /// The search box's inner text box (its template part), where a redirected focus goes; found at first need. The search
    /// box itself would do too (WinUI hands a redirect to a control's first focusable part), but naming the part leaves WinUI
    /// nothing to look up in the middle of a focus change.
    /// </summary>
    private TextBox? searchTextBox;

    /// <summary>
    /// The tab the user clicked last, which the Menu key and Shift+F10 open the menu of, as they did while a click gave the
    /// tab the keyboard focus (§2.40); <see langword="null"/> once another key is pressed, the mouse presses elsewhere in the
    /// panel, the strip is dragged, or the panel prepares a summon. Left and Right carry it along with the selection
    /// (<see cref="CycleTab"/>), as the focus once moved along with it.
    /// </summary>
    private SelectorBarItem? clickedTab;

    /// <summary>Whether this session logged the guard's first refusal (once a session, content-free: the element's type only).</summary>
    private bool searchFocusHeldLogged;

    /// <summary>Whether this session logged a move the guard could not refuse (once a session).</summary>
    private bool searchFocusRepairLogged;

    /// <summary>Whether this session logged a failure inside the guard's handler (once a session).</summary>
    private bool searchFocusFailureLogged;

    /// <summary>Whether a <see cref="RepairSearchFocus"/> is queued, so a burst of refusals that failed queues one.</summary>
    private bool searchFocusRepairQueued;

    /// <summary>
    /// The press handler <see cref="InitializeSearchFocusGuard"/> added to <see cref="Root"/>, kept so the same delegate can be
    /// removed again: a new delegate for the same method is another object to WinUI, and its removal would do nothing.
    /// </summary>
    private PointerEventHandler? clickedTabPressHandler;

    /// <summary>
    /// Starts the guard (from the constructor, once): the static focus event, and the press handler that ends a clicked tab's
    /// hold on the Menu key when the mouse presses anywhere else in the panel.
    /// </summary>
    /// <remarks>
    /// The press handler is registered with <c>handledEventsToo</c>: a control may mark a press handled (a card does), and a
    /// press on it still ends the tab's hold. It runs after the tab strip's own press handler (a tab is deeper in the tree),
    /// which is where a press on a tab sets the hold.
    /// </remarks>
    private void InitializeSearchFocusGuard()
    {
        FocusManager.GettingFocus += FocusManager_GettingFocus;
        clickedTabPressHandler = new PointerEventHandler(Root_PointerPressedForClickedTab);
        Root.AddHandler(UIElement.PointerPressedEvent, clickedTabPressHandler, handledEventsToo: true);
    }

    /// <summary>
    /// Stops the guard (the window closes for good). The focus event is static: left subscribed, it would keep this window
    /// alive and run for every focus move of the app's other windows.
    /// </summary>
    private void DisposeSearchFocusGuard()
    {
        FocusManager.GettingFocus -= FocusManager_GettingFocus;
        if (clickedTabPressHandler is { } handler)
        {
            Root.RemoveHandler(UIElement.PointerPressedEvent, handler);
            clickedTabPressHandler = null;
        }
    }

    /// <summary>
    /// A focus move is about to happen somewhere on this thread (any window): refused when it would leave the search box for
    /// another element of the panel (class remarks).
    /// </summary>
    /// <param name="sender">Unused (WinUI passes no sender for the static event).</param>
    /// <param name="args">The move: where from, where to, and the way to refuse or redirect it.</param>
    /// <remarks>
    /// Runs synchronously inside WinUI's focus change, with the focus locked: <c>Focus()</c> may not be called here, only the
    /// args' <c>TryCancel</c> and <c>TrySetNewFocusedElement</c>. Nothing may escape it either (an exception would surface in
    /// WinUI's focus change): everything is caught and logged once.
    /// </remarks>
    private void FocusManager_GettingFocus(object? sender, GettingFocusEventArgs args)
    {
        try
        {
            if (closingForExit || !presented || args.NewFocusedElement is not DependencyObject target)
            {
                return;
            }

            var place = FocusTargetOf(target);
            var action = PanelFocus.Decide(place, presented, focusless, IsInSearchBox(args.OldFocusedElement));
            if (action == PanelFocusAction.Allow)
            {
                return;
            }

            bool held = action == PanelFocusAction.Cancel
                ? args.TryCancel()
                : args.TrySetNewFocusedElement(SearchFocusTarget());
            LogSearchFocusHeld(target, args.FocusState);

            // A move WinUI does not let anyone refuse (a window activation, an element leaving the tree): the search box takes
            // the focus back once it is done. Never without the keyboard, where a focus move would activate the window.
            if (!held && !focusless)
            {
                QueueSearchFocusRepair();
            }

            // A click on something that takes no focus — a tooltip among them — sends it to an element WinUI wraps the
            // window's content in. The tooltip guard used to see that move after the fact and open the update dialog when the
            // click was on the update button's tooltip (ClipboardFlyout.ToolTipKeyboard.cs); the move is refused now, so the
            // click is answered here.
            if (IsRootOrItsAncestor(target) && IsAnyToolTipOpen())
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, OpenUpdateDialogIfItsToolTipWasClicked);
            }
        }
        catch (Exception ex)
        {
            if (!searchFocusFailureLogged)
            {
                searchFocusFailureLogged = true;
                AppLog.Warn($"Keeping the keyboard in the search box failed (logged once a session): {ex.Message}");
            }
        }
    }

    /// <summary>Where an element about to take the focus lies (<see cref="PanelFocusTarget"/>).</summary>
    /// <param name="target">The element.</param>
    /// <returns>
    /// <see cref="PanelFocusTarget.SearchBox"/> for the search box and its parts; <see cref="PanelFocusTarget.Surface"/> for
    /// any other element of the panel's tree, <see cref="Root"/> itself, and the elements WinUI wraps the window's content in;
    /// <see cref="PanelFocusTarget.Elsewhere"/> for a popup's content and anything outside this window.
    /// </returns>
    /// <remarks>
    /// Walks up WinUI's visual tree. A popup's content never leads up to <see cref="Root"/>: it lies under the window's popup
    /// layer (or a window of its own), a sibling of the content, and a popup declared inside the panel's tree (a teaching
    /// tip's) is met on the way up as a <see cref="Popup"/> and stops the walk. An element the tree cannot be walked from (a
    /// text element) counts as elsewhere: the guard never refuses what it cannot place.
    /// </remarks>
    private PanelFocusTarget FocusTargetOf(DependencyObject target)
    {
        if (IsRootOrItsAncestor(target))
        {
            return PanelFocusTarget.Surface;
        }

        for (DependencyObject? current = target; current is not null; current = ParentOf(current))
        {
            if (ReferenceEquals(current, SearchBox))
            {
                return PanelFocusTarget.SearchBox;
            }

            if (current is Popup)
            {
                return PanelFocusTarget.Elsewhere;
            }

            if (ReferenceEquals(current, Root))
            {
                return PanelFocusTarget.Surface;
            }
        }

        return PanelFocusTarget.Elsewhere;
    }

    /// <summary>Whether an element lies in the search box (the box itself or one of its parts).</summary>
    /// <param name="element">The element, or <see langword="null"/> (nothing has the focus).</param>
    /// <returns><see langword="true"/> when the walk up from <paramref name="element"/> meets the search box before the panel's root.</returns>
    private bool IsInSearchBox(DependencyObject? element)
    {
        for (DependencyObject? current = element; current is not null; current = ParentOf(current))
        {
            if (ReferenceEquals(current, SearchBox))
            {
                return true;
            }

            if (ReferenceEquals(current, Root))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an element is <see cref="Root"/> or one of the elements WinUI wraps the window's content in (the scroll viewer
    /// where it parks the focus after a click on something that takes none).
    /// </summary>
    /// <param name="element">The element.</param>
    /// <returns><see langword="true"/> for <see cref="Root"/> and its ancestors.</returns>
    private bool IsRootOrItsAncestor(DependencyObject element)
    {
        for (DependencyObject? current = Root; current is not null; current = ParentOf(current))
        {
            if (ReferenceEquals(current, element))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>An element's parent in WinUI's visual tree, or <see langword="null"/> at the top or where the tree cannot be walked.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The parent, or <see langword="null"/>.</returns>
    private static DependencyObject? ParentOf(DependencyObject element)
    {
        try
        {
            return VisualTreeHelper.GetParent(element);
        }
        catch (ArgumentException)
        {
            // Not part of the visual tree (a text element): the walk ends here.
            return null;
        }
    }

    /// <summary>Where a redirected focus goes: the search box's text box, or the search box while its template is not applied.</summary>
    /// <returns>The element to hand the focus to.</returns>
    private DependencyObject SearchFocusTarget()
    {
        searchTextBox ??= FindDescendant<TextBox>(SearchBox);
        return (DependencyObject?)searchTextBox ?? SearchBox;
    }

    /// <summary>Queues one <see cref="RepairSearchFocus"/>, however many refusals failed meanwhile.</summary>
    private void QueueSearchFocusRepair()
    {
        if (searchFocusRepairQueued)
        {
            return;
        }

        searchFocusRepairQueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, RepairSearchFocus);
    }

    /// <summary>
    /// After a move the guard could not refuse: gives the search box the focus back, when the panel still shows with the
    /// keyboard and in front, none of its popups is open, and the focus is on its surface or with nobody.
    /// </summary>
    /// <remarks>Queued, so it runs once WinUI finished the move (a focus call inside it is not allowed). Logged once a session.</remarks>
    private void RepairSearchFocus()
    {
        searchFocusRepairQueued = false;
        try
        {
            if (closingForExit || !presented || focusless || openPopups != 0 || drag is not null || Root.XamlRoot is not { } root
                || !ForegroundHelper.IsForeground(hwnd))
            {
                return;
            }

            if (FocusManager.GetFocusedElement(root) is DependencyObject focused && FocusTargetOf(focused) != PanelFocusTarget.Surface)
            {
                return; // the search box, a popup, or another window has it: nothing to repair
            }

            SearchBox.Focus(FocusState.Programmatic);
            if (!searchFocusRepairLogged)
            {
                searchFocusRepairLogged = true;
                AppLog.Info("The search box took the keyboard back after a focus move WinUI did not let it refuse (logged once a session).");
            }
        }
        catch (Exception ex)
        {
            // Queued work must not let anything escape into the dispatcher.
            AppLog.Warn($"Giving the search box the keyboard back failed: {ex.Message}");
        }
    }

    /// <summary>The guard's first refusal of the session, in the log: the element's type and how the move came, nothing else.</summary>
    /// <param name="target">The element that wanted the focus.</param>
    /// <param name="state">How the move came (a click, a key, from code).</param>
    private void LogSearchFocusHeld(DependencyObject target, FocusState state)
    {
        if (searchFocusHeldLogged)
        {
            return;
        }

        searchFocusHeldLogged = true;
        AppLog.Info($"The search box keeps the keyboard: a {target.GetType().Name} did not take it ({state}; logged once a session).");
    }

    /// <summary>
    /// A click on something that takes no focus while a tooltip shows: when it was the update button's tooltip, the update
    /// dialog opens, as a click on the button would (§2.30) — what <see cref="RecoverKeyboardFromToolTip"/> did while such a
    /// click still moved the focus.
    /// </summary>
    private void OpenUpdateDialogIfItsToolTipWasClicked()
    {
        try
        {
            if (closingForExit || !IsOpen || openPopups != 0 || drag is not null || !ForegroundHelper.IsForeground(hwnd))
            {
                return;
            }

            if (IsPointerOverUpdateToolTip())
            {
                ShowUpdateDialog();
            }
        }
        catch (Exception ex)
        {
            // Queued work must not let anything escape into the dispatcher.
            AppLog.Warn($"Opening the update dialog from its tooltip failed: {ex.Message}");
        }
    }

    /// <summary>
    /// A press on a tab (any device, any button but the middle one, which a tab ignores): the tab is the one the Menu key
    /// opens the menu of, and Left and Right switch tabs from now on, as they did while a click gave the tab the focus.
    /// Called from the tab strip's press handler (ClipboardFlyout.TabStrip.cs).
    /// </summary>
    /// <param name="tab">The tab pressed.</param>
    private void NoteTabPressed(SelectorBarItem tab)
    {
        clickedTab = tab;
        arrowNavigating = true;
    }

    /// <summary>The clicked tab no longer holds the Menu key: a key, a press elsewhere, a drag of the strip, a summon.</summary>
    private void ForgetClickedTab() => clickedTab = null;

    /// <summary>
    /// A key the panel sees (with or without the keyboard): returns the tab the Menu key would open the menu of, and ends
    /// the clicked tab's hold for every key but those that keep it — Left and Right (they carry it along), the Menu key and
    /// Shift+F10 (they use it), and the modifier keys alone (held for a chord).
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="shift">Whether Shift is held (Shift+F10 is the Menu key's twin).</param>
    /// <returns>The clicked tab while it still shows; <see langword="null"/> when there is none or it was hidden meanwhile.</returns>
    private SelectorBarItem? TakeClickedTabForKey(VirtualKey key, bool shift)
    {
        var tab = clickedTab is { Visibility: Visibility.Visible } shown ? shown : null;
        bool keeps = IsModifierKey(key) || key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Application
            || (key == VirtualKey.F10 && shift);
        if (!keeps)
        {
            clickedTab = null;
        }

        return tab;
    }

    /// <summary>A press anywhere in the panel that is not on a tab ends the clicked tab's hold on the Menu key.</summary>
    /// <param name="sender">The root grid.</param>
    /// <param name="e">Pointer data.</param>
    private void Root_PointerPressedForClickedTab(object sender, PointerRoutedEventArgs e)
    {
        if (clickedTab is not null && FindTab(e.OriginalSource as DependencyObject) is null)
        {
            clickedTab = null;
        }
    }
}
