using BetterClipboard.App.ViewModels;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Prompts;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;
using BetterClipboard.Windows.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace BetterClipboard.App.Views;

/// <summary>
/// Getting around the panel: the tab a summon starts in (the one the user last chose), the arrow keys beyond plain Up and
/// Down (Left and Right through the tabs in a circle, Up from the top row into the groups column), and the mouse selecting
/// the row it is over.
/// </summary>
/// <remarks>
/// <para>
/// <b>The remembered tab</b> (<see cref="rememberedTab"/>, saved as <see cref="AppSettings.LastTab"/>). Only a choice of
/// the user's changes it: a click on a tab, a Left or Right key, a screen reader's select. Every summon — and the hidden
/// panel that waits for it — starts there (<see cref="ResetViewForShow"/>); while that tab cannot be shown (ShareX gone,
/// its setting off, another app's tab not ready yet after a start) the summon starts in "All" without forgetting it, and
/// the hidden panel goes back to it when it returns (<see cref="ReturnToRememberedTabWhileConcealed"/>). The groups shown
/// and the search text are not remembered: a summon still starts over the whole history with an empty search box.
/// </para>
/// <para>
/// <b>The arrow keys</b> (rules: <see cref="PanelNavigation"/>). The search box keeps the keyboard focus the whole time, so
/// every key is routed here from <see cref="Root_PreviewKeyDown"/>. Up and Down move through the rows. Left and Right
/// select the previous and the next tab, wrapping around at both ends — unless the search text is being edited, when they
/// move its caret as in any text box. A new press of Up on the top row walks into the groups column (when it is open and
/// has groups): the lowest group is shown, further presses of Up show the groups above it and finally the whole history
/// again (the clipboard icon), and Right — or the mouse over a row — goes back to the list. While the keys are in the
/// column, the icon they are on wears a ring and the footer says how to get back; Enter, Ctrl+P and Del still act on the
/// selected card.
/// </para>
/// <para>
/// <b>The mouse selects the row it is over</b> — once it really moved (<see cref="PointerSelectionGate"/>): rows also come
/// under a mouse that lies still, when the list scrolls or reloads or the panel appears under the cursor, and WinUI reports
/// "pointer entered" for those too. Selecting on them would take the selection from the keyboard, and Enter after a typed
/// search would paste the row under the mouse instead of the best match.
/// </para>
/// <para>
/// <b>Cost.</b> While the mouse owns the selection, nothing of this runs per mouse move: each row reports only the pointer
/// <i>entering</i> it (<see cref="Card_PointerEntered"/>, once per row crossed), which is also the event WinUI's own
/// hover highlight follows, so highlight and selection never disagree. A move handler exists only while the keyboard owns
/// the selection (<see cref="ItemsList_PointerMovedWhileSuspended"/>): it compares the cursor's position with the spot it
/// was on, and takes itself off the list the moment the mouse takes over. Hovering never scrolls the list — a row that
/// peeks in at the bottom would otherwise pull its neighbors under the cursor one after the other.
/// </para>
/// <para>
/// The tabs and the group icons are not selected by hovering, only by a click (or by the keys above).
/// </para>
/// </remarks>
public sealed partial class ClipboardFlyout
{
    /// <summary>
    /// What <see cref="WatchCardPointer"/> stores in a list row's <see cref="FrameworkElement.Tag"/> once the row reports
    /// the pointer entering it. On the row itself rather than in a table here: the mark must belong to the row for as long
    /// as the list keeps it, whatever happens to the managed wrapper that stands for it. Nothing else uses a row's Tag.
    /// </summary>
    private const string CardWatchedTag = "pointer-watched";

    /// <summary>
    /// The tab the user last chose: where a summon starts. Loaded from <see cref="AppSettings.LastTab"/>, written back by
    /// <see cref="SaveRememberedTab"/> when the panel is concealed.
    /// </summary>
    private ClipFilter rememberedTab;

    /// <summary>
    /// Set while <see cref="SelectFilter"/> changes the selected tab, so <see cref="Filters_SelectionChanged"/> can tell a
    /// selection from code (a reset, the fallback when a tab disappears) from a choice of the user's.
    /// </summary>
    private bool selectingFilterFromCode;

    /// <summary>
    /// Whether the arrow keys steer the panel right now rather than text being edited: set by every arrow key the panel
    /// acts on, cleared when the search text changes, a key goes to the search box, or the box is clicked. While it is set,
    /// Left and Right switch tabs even though the box holds text (<see cref="PanelNavigation.HorizontalArrow"/>).
    /// </summary>
    private bool arrowNavigating;

    /// <summary>
    /// Whether Up and Down walk the groups column instead of the rows (<see cref="EnterGroupsNavigation"/> to
    /// <see cref="LeaveGroupsNavigation"/>). Where the keys are in the column is not stored: it is the group shown
    /// (<see cref="GroupCursorPosition"/>), so the two can never disagree.
    /// </summary>
    private bool groupsNavigation;

    /// <summary>
    /// Set while the arrow keys themselves change the group shown (<see cref="ShowGroupAt"/>): any other change of the
    /// groups shown ends their walk (<see cref="EndGroupsNavigationUnlessMoving"/>).
    /// </summary>
    private bool movingGroupCursor;

    /// <summary>Decides whether the mouse may select the row it is over (it must have really moved since the keyboard or a reload set the selection).</summary>
    private readonly PointerSelectionGate pointerGate = new();

    /// <summary>The one delegate every row's <c>PointerEntered</c> calls (created once: a row is given it once, see <see cref="WatchCardPointer"/>).</summary>
    private PointerEventHandler? cardEnteredHandler;

    /// <summary>
    /// The delegate of the list's move handler, kept so the handler that was added is the one removed
    /// (<see cref="SuspendPointerSelection"/> / <see cref="StopWatchingPointerMoves"/>).
    /// </summary>
    private PointerEventHandler? pointerMovedHandler;

    /// <summary>Whether <see cref="pointerMovedHandler"/> is on the list right now (only while the keyboard owns the selection).</summary>
    private bool watchingPointerMoves;

    /// <summary>Whether the groups column can be walked with the arrow keys: it is open and holds at least one group.</summary>
    private bool CanWalkGroups => groupsPaneOpen && ViewModel.Groups.Count > 0;

    /// <summary>
    /// Wires the navigation up (from the constructor, once): reads the remembered tab, and attaches the handlers that tell
    /// when the search text is edited and when the mouse wheel is used over the list. The tab itself is selected by the
    /// first <see cref="ResetViewForShow"/> (the warm-up's, or a summon's).
    /// </summary>
    private void InitializeNavigation()
    {
        rememberedTab = controller.Settings.Current.LastTabFilter;

        // handledEventsToo: the list's own scroller handles the wheel (and marks it), and the turn must still count as
        // "the mouse is in use".
        ItemsList.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(ItemsList_PointerWheelChanged), handledEventsToo: true);

        // handledEventsToo: the text box handles the press itself (it places the caret).
        SearchBox.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SearchBox_PointerPressed), handledEventsToo: true);

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FlyoutViewModel.SearchText))
            {
                OnSearchTextEdited();
            }
        };
    }

    /// <summary>
    /// The tab a summon starts in: the one the user last chose, or "All" while that tab cannot be shown. The remembered
    /// tab itself is left alone, so the panel is back in it once it can be shown again.
    /// </summary>
    /// <returns>The tab to select.</returns>
    private ClipFilter StartFilter() => IsTabAvailable(rememberedTab) ? rememberedTab : ClipFilter.All;

    /// <summary>
    /// Whether a tab shows right now, by the same conditions the <c>Update…Tab</c> methods apply to the tabs of other
    /// apps; the six built-in tabs always show.
    /// </summary>
    /// <param name="filter">The tab's filter.</param>
    /// <returns><see langword="true"/> when the panel has that tab and it is visible (or about to be made visible).</returns>
    private bool IsTabAvailable(ClipFilter filter) => filter switch
    {
        ClipFilter.ShareX => controller.ShareX.IsInstalled,
        ClipFilter.Snipping => controller.IsSnippingTabAvailable,
        ClipFilter.Run => controller.IsRunTabAvailable,
        ClipFilter.PowerShell => controller.IsPowerShellTabAvailable,
        ClipFilter.Cmd => controller.IsCmdTabAvailable,
        ClipFilter.ClaudeCode => controller.IsPromptTabAvailable(PromptAgent.ClaudeCode),
        ClipFilter.Codex => controller.IsPromptTabAvailable(PromptAgent.Codex),
        ClipFilter.Everything => controller.IsEverythingTabAvailable,

        // All, Pinned, Text, Images, Links, Files. A filter that has no tab at all (a future one) is never available.
        _ => TabOf(filter) is not null,
    };

    /// <summary>The tab that stands for a filter (its <see cref="FrameworkElement.Tag"/> is the filter's name).</summary>
    /// <param name="filter">The filter.</param>
    /// <returns>The tab, or <see langword="null"/> when the strip has none for it.</returns>
    private SelectorBarItem? TabOf(ClipFilter filter)
    {
        string name = filter.ToString();
        foreach (var tab in Filters.Items)
        {
            if (tab.Tag is string tag && tag == name)
            {
                return tab;
            }
        }

        return null;
    }

    /// <summary>
    /// Notes the tab the user chose (a click, a Left or Right key, a screen reader's select): the next summon starts in
    /// it. It reaches the settings file when the panel is concealed (<see cref="SaveRememberedTab"/>).
    /// </summary>
    /// <param name="filter">The chosen tab.</param>
    private void RememberTab(ClipFilter filter) => rememberedTab = filter;

    /// <summary>
    /// Writes the remembered tab to the settings if it changed since it was last saved — once per use of the panel, when it
    /// is concealed, rather than on every tab change (the arrow keys step through a dozen tabs in a second, and every
    /// save writes the file and re-applies the settings).
    /// </summary>
    /// <remarks>
    /// Queued at low priority instead of written here: concealing is on a paste's way to its Ctrl+V, and a file write has
    /// no place there. Limit: a tab chosen in a panel that is still open when the app exits is not saved (the queue no
    /// longer runs). A failed write is logged; the tab is then remembered only until the app closes.
    /// </remarks>
    private void SaveRememberedTab()
    {
        if (controller.Settings.Current.LastTabFilter == rememberedTab)
        {
            return;
        }

        // Spelled out: Windows.System (imported for the key codes) has a DispatcherQueuePriority of its own.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            // Read again: a summon in between may have changed the choice, or brought it back to what is saved.
            var tab = rememberedTab;
            if (controller.Settings.Current.LastTabFilter == tab)
            {
                return;
            }

            try
            {
                controller.Settings.Update(s => s with { LastTab = tab.ToString() });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The store keeps the change in memory; it only fails to reach the next start.
                AppLog.Warn($"Saving the panel's last tab failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Shows or hides the tabs of other apps by what is installed, running and switched on now (each falls back to "All"
    /// when it disappears while selected). Cheap and idempotent: the status events keep the tabs current anyway, this
    /// catches whatever they did not announce before the view is reset.
    /// </summary>
    private void UpdateSourceTabs()
    {
        UpdateShareXTab();
        UpdateSnippingTab();
        UpdateEverythingTab();
        UpdateRunTab();
        UpdateShellTabs();
        UpdatePromptTabs();
    }

    /// <summary>
    /// Puts the view where a summon starts: the tab the user last chose ("All" while that tab is not there), an empty
    /// search box, the regular view over the whole history, the tab strip showing the selected tab, and the arrow keys
    /// back on the rows. Called when the panel is concealed (so the next summon finds it ready) and again by the summon
    /// itself, where it then changes nothing.
    /// </summary>
    private void ResetViewForShow()
    {
        // The view model first: the tabs' updates below then find a filter whose tab exists, and fall back for nothing.
        var start = StartFilter();
        ViewModel.ResetForShow(start);
        UpdateSourceTabs();
        SelectFilter(start);
        PlaceTabStripForShow();
        arrowNavigating = false;
        LeaveGroupsNavigation();
    }

    /// <summary>
    /// A tab appeared or disappeared while the panel is hidden: if the tab the user last chose is back (or gone), the
    /// hidden panel is prepared again, so the next summon shows the right tab with its list already loaded instead of
    /// switching tabs in front of the user. An open panel is left alone: its tab is the user's to change.
    /// </summary>
    private void ReturnToRememberedTabWhileConcealed()
    {
        if (!presented && StartFilter() != ViewModel.Filter)
        {
            PrepareForNextSummon();
        }
    }

    /// <summary>
    /// Up, Down, Page Up or Page Down: moves the selection through the rows, walks from the top row into the groups column
    /// (a new press of Up only), or — while the keys are in that column — moves through the groups.
    /// </summary>
    /// <param name="delta">Rows (or groups) to move: negative is up.</param>
    /// <param name="isRepeat">Whether the key repeats because it is held down (a held Up stops at the top row).</param>
    private void MoveVertical(int delta, bool isRepeat)
    {
        arrowNavigating = true;
        if (groupsNavigation)
        {
            MoveGroupCursor(delta);
            return;
        }

        var move = PanelNavigation.MoveInList(ItemsList.SelectedIndex, ViewModel.Items.Count, delta, isRepeat, CanWalkGroups);
        if (move.EntersGroups)
        {
            EnterGroupsNavigation();
        }
        else if (move.Index >= 0)
        {
            // The keyboard owns the selection now: the list may scroll rows under a mouse that lies still.
            SuspendPointerSelection();
            SelectIndex(move.Index);
        }
    }

    /// <summary>
    /// Left or Right: switches to the neighboring tab (in a circle), leaves the groups column, or does nothing because the
    /// search box needs the key (<see cref="PanelNavigation.HorizontalArrow"/>).
    /// </summary>
    /// <param name="right"><see langword="true"/> for Right.</param>
    /// <param name="isRepeat">Whether the key repeats because it is held down (a held key stops at the first or last tab).</param>
    /// <param name="hasModifier">Whether Ctrl, Shift, Alt or the Windows key is held.</param>
    /// <param name="typedIntoText">Whether the key was typed into a text box (the search box).</param>
    /// <returns><see langword="true"/> when the panel took the key; <see langword="false"/> to leave it to the focused control.</returns>
    private bool MoveHorizontal(bool right, bool isRepeat, bool hasModifier, bool typedIntoText)
    {
        var action = PanelNavigation.HorizontalArrow(
            right, hasModifier, groupsNavigation, typedIntoText, !string.IsNullOrEmpty(ViewModel.SearchText), arrowNavigating);
        switch (action)
        {
            case HorizontalArrowAction.NotHandled:
                return false;
            case HorizontalArrowAction.PreviousTab:
            case HorizontalArrowAction.NextTab:
                CycleTab(forward: action == HorizontalArrowAction.NextTab, isRepeat);
                break;
            case HorizontalArrowAction.LeaveGroups:
                LeaveGroupsNavigation();
                break;
            case HorizontalArrowAction.Swallow:
                break; // taken, so the caret stays where it is
        }

        arrowNavigating = true;
        return true;
    }

    /// <summary>
    /// Selects the tab before or after the selected one among the tabs that show, wrapping around at both ends
    /// (<see cref="PanelNavigation.CycleTab"/>), and remembers it as the user's choice.
    /// </summary>
    /// <param name="forward"><see langword="true"/> towards the last tab.</param>
    /// <param name="isRepeat">Whether the key repeats because it is held down (then it does not wrap).</param>
    private void CycleTab(bool forward, bool isRepeat)
    {
        // By Tag, not by reference: two reads of the same tab need not hand back the same managed object.
        string? selected = Filters.SelectedItem?.Tag as string;
        var visible = new List<SelectorBarItem>(Filters.Items.Count);
        int current = -1;
        foreach (var tab in Filters.Items)
        {
            if (tab.Visibility != Visibility.Visible)
            {
                continue;
            }

            if (selected is not null && tab.Tag is string tag && tag == selected)
            {
                current = visible.Count;
            }

            visible.Add(tab);
        }

        int next = PanelNavigation.CycleTab(current, visible.Count, forward, isRepeat);
        if (next < 0 || next == current || visible[next].Tag is not string name || !Enum.TryParse<ClipFilter>(name, out var filter))
        {
            return;
        }

        // When a tab itself has the keyboard (the user tabbed or clicked into the strip), the focus moves along with the
        // selection: left behind, its focus rectangle would sit on a tab that is no longer the selected one.
        bool focusInStrip = Root.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is SelectorBarItem;

        // Reloads the list (which arms its first card), and glides the strip to the tab while the panel is open.
        SelectFilter(filter);
        RememberTab(filter);
        if (focusInStrip)
        {
            visible[next].Focus(FocusState.Programmatic);
        }
    }

    /// <summary>
    /// A key went on to the search box instead of being handled here: its text is being edited (or its caret moved), so
    /// Left and Right belong to the text again until the next arrow key the panel acts on. Modifier keys alone change
    /// nothing — holding Shift for a capital letter is not an edit yet.
    /// </summary>
    /// <param name="e">The key the panel did not handle.</param>
    private void NoteKeyLeftToText(KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox && !IsModifierKey(e.Key))
        {
            arrowNavigating = false;
        }
    }

    /// <summary>Whether a key is Shift, Ctrl, Alt or the Windows key (either side): held for a chord, not an action by itself.</summary>
    /// <param name="key">The key.</param>
    /// <returns><see langword="true"/> for a modifier key.</returns>
    private static bool IsModifierKey(VirtualKey key) => key is
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
        VirtualKey.LeftWindows or VirtualKey.RightWindows;

    /// <summary>
    /// The search text changed (typed, pasted, cleared with Esc, or reset for a summon): the text is being edited, so Left
    /// and Right move its caret again, and a walk through the groups column ends — after typing, Up and Down are expected
    /// to move through the matches.
    /// </summary>
    private void OnSearchTextEdited()
    {
        arrowNavigating = false;
        LeaveGroupsNavigation();
    }

    /// <summary>A click into the search box: its text is being edited, so Left and Right move its caret again.</summary>
    /// <param name="sender">The search box.</param>
    /// <param name="e">Pointer data.</param>
    private void SearchBox_PointerPressed(object sender, PointerRoutedEventArgs e) => arrowNavigating = false;

    /// <summary>
    /// Where the arrow keys are in the groups column, as <see cref="PanelNavigation"/> counts positions: 0 for the clipboard
    /// icon (the regular view), 1 and up for the groups in column order. Derived from the groups shown — the keys always
    /// stand on the group they show — so it needs no state of its own; of several merged groups it is the lowest.
    /// </summary>
    /// <returns>The position; 0 also when the group shown is no longer in the column.</returns>
    private int GroupCursorPosition()
    {
        var shown = ViewModel.GroupSelection.Ids;
        if (shown.Count == 0)
        {
            return 0;
        }

        long id = shown[^1];
        var groups = ViewModel.Groups;
        for (int i = 0; i < groups.Count; i++)
        {
            if (groups[i].Id == id)
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// Up was pressed on the top row: the keys walk into the groups column. The lowest group is shown and wears the ring;
    /// from here Up and Down move through the groups (<see cref="MoveGroupCursor"/>) until Right, the mouse over a row,
    /// typing, or a click on an icon ends the walk (<see cref="LeaveGroupsNavigation"/>).
    /// </summary>
    private void EnterGroupsNavigation()
    {
        groupsNavigation = true;
        ViewModel.SetGroupsNavigation(true);

        // The list is about to show another group's cards under a mouse that may lie on it.
        SuspendPointerSelection();
        ShowGroupAt(PanelNavigation.EnterGroups(ViewModel.Groups.Count));

        // Also when the lowest group was the one shown already: no change event moves the ring then.
        UpdateGroupSelectionVisuals();
    }

    /// <summary>Up, Down or a page key while the keys are in the groups column: shows the group (or the whole history) they land on.</summary>
    /// <param name="delta">Positions to move: negative is up, towards the clipboard icon.</param>
    private void MoveGroupCursor(int delta)
    {
        int current = GroupCursorPosition();
        int next = PanelNavigation.MoveInGroups(current, ViewModel.Groups.Count, delta);
        if (next != current)
        {
            SuspendPointerSelection();
            ShowGroupAt(next);
        }
    }

    /// <summary>
    /// Shows what a position of the groups column stands for — the whole history (0) or one group alone — as the arrow
    /// keys' own change, and scrolls that icon into view when the column is longer than the panel.
    /// </summary>
    /// <param name="position">The position (see <see cref="GroupCursorPosition"/>); one outside the column counts as 0.</param>
    private void ShowGroupAt(int position)
    {
        var groups = ViewModel.Groups;
        bool regularView = position <= 0 || position > groups.Count;

        // Flagged, so the change event of this selection does not end the walk it belongs to.
        movingGroupCursor = true;
        try
        {
            if (regularView)
            {
                ViewModel.ShowAllHistory();
            }
            else
            {
                ViewModel.ShowOnlyGroup(groups[position - 1].Id);
            }
        }
        finally
        {
            movingGroupCursor = false;
        }

        // The buttons are in column order, like the groups (RebuildGroupButtons); the clipboard icon is always in view.
        if (!regularView && position - 1 < GroupButtons.Children.Count)
        {
            GroupButtons.Children[position - 1].StartBringIntoView();
        }
    }

    /// <summary>
    /// Ends the arrow keys' walk through the groups column: Up and Down move through the rows again, the ring goes, and
    /// the footer shows the tab's own key hints. The group shown stays shown. No-op when the keys are not in the column.
    /// </summary>
    private void LeaveGroupsNavigation()
    {
        if (!groupsNavigation)
        {
            return;
        }

        groupsNavigation = false;
        ViewModel.SetGroupsNavigation(false);
        UpdateGroupSelectionVisuals();
    }

    /// <summary>
    /// The groups shown changed: unless the arrow keys made the change themselves, their walk through the column ends — a
    /// click on an icon, the icon menu, a deleted group or the reset for a summon decided what is shown.
    /// </summary>
    private void EndGroupsNavigationUnlessMoving()
    {
        if (!movingGroupCursor)
        {
            LeaveGroupsNavigation();
        }
    }

    /// <summary>The column was reloaded: a walk through it ends when it has no group left to walk (or is closed).</summary>
    private void EndGroupsNavigationIfUnreachable()
    {
        if (!CanWalkGroups)
        {
            LeaveGroupsNavigation();
        }
    }

    /// <summary>
    /// The keyboard or the panel itself set the selection (an arrow key, a reload that armed the first card, a summon):
    /// the mouse selects nothing until it has really left the spot it is on now (<see cref="PointerSelectionGate"/>), and
    /// the list's moves are watched for that moment.
    /// </summary>
    private void SuspendPointerSelection()
    {
        // The system's drag threshold (4 px at 100 %): a mouse nudged while typing has not "moved".
        int threshold = (int)Math.Round(DragThresholdDip * Scale);
        if (ScreenPointer.Cursor() is { } cursor)
        {
            pointerGate.Suspend(cursor.X, cursor.Y, threshold);
        }
        else
        {
            pointerGate.Suspend(threshold);
        }

        if (!watchingPointerMoves)
        {
            // The plain event, added and removed with the same delegate: this pair is switched on and off with every
            // change between keyboard and mouse, so the removal must be one that is sure to find what was added.
            pointerMovedHandler ??= ItemsList_PointerMovedWhileSuspended;
            ItemsList.PointerMoved += pointerMovedHandler;
            watchingPointerMoves = true;
        }
    }

    /// <summary>
    /// Takes the move handler off the list: the mouse owns the selection, and from here on each row's own
    /// <c>PointerEntered</c> is all that is needed (nothing runs per mouse move).
    /// </summary>
    private void StopWatchingPointerMoves()
    {
        if (watchingPointerMoves && pointerMovedHandler is { } handler)
        {
            ItemsList.PointerMoved -= handler;
            watchingPointerMoves = false;
        }
    }

    /// <summary>
    /// Whether the mouse owns the selection at this pointer event: it does already, or the cursor has now left the spot
    /// recorded when the keyboard last set the selection — which also ends the watch for that moment.
    /// </summary>
    /// <returns><see langword="true"/> when the row under the pointer may be selected.</returns>
    private bool IsPointerSelecting()
    {
        if (!pointerGate.IsPointerActive
            && (ScreenPointer.Cursor() is not { } cursor || !pointerGate.Observe(cursor.X, cursor.Y)))
        {
            return false; // still on the spot (or the position cannot be read): the event says nothing about movement
        }

        StopWatchingPointerMoves();
        return true;
    }

    /// <summary>
    /// Whether hovering may select rows at all right now: the panel shows, and nothing else has the pointer's attention —
    /// one of our menus or flyouts, the image viewer, a window drag, a card being dragged onto a group.
    /// </summary>
    /// <returns><see langword="true"/> when a row under the mouse may be selected.</returns>
    private bool CanPointerSelect() => presented && openPopups == 0 && drag is null && draggedClipIds is null;

    /// <summary>
    /// Makes a list row report the pointer entering it (once per row: rows are reused for other cards as the list scrolls,
    /// and <see cref="ItemsList_ContainerContentChanging"/> calls this every time).
    /// </summary>
    /// <param name="row">The row's container; <see langword="null"/> is ignored.</param>
    private void WatchCardPointer(SelectorItem? row)
    {
        if (row is null || row.Tag is not null)
        {
            return;
        }

        row.Tag = CardWatchedTag;
        cardEnteredHandler ??= Card_PointerEntered;
        row.PointerEntered += cardEnteredHandler;
    }

    /// <summary>
    /// The pointer came over a row: the mouse selects it, if it owns the selection (<see cref="IsPointerSelecting"/>).
    /// Raised once per row crossed, and also by WinUI for a row that scrolled under a mouse lying still — which is why
    /// the event alone decides nothing.
    /// </summary>
    /// <param name="sender">The row's container.</param>
    /// <param name="e">Pointer data.</param>
    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        // Touch and pen select by tapping; only a mouse (also a touchpad's pointer) hovers.
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || !CanPointerSelect() || !IsPointerSelecting())
        {
            return;
        }

        if (sender is SelectorItem row && ItemsList.ItemFromContainer(row) is ClipItemViewModel card)
        {
            SelectByPointer(card);
        }
    }

    /// <summary>
    /// A mouse move over the list while the keyboard owns the selection: once the cursor has really left its spot, the
    /// mouse takes over and the row it is on is selected — the row it was resting on all along raises no
    /// <c>PointerEntered</c> for that. Attached only until then (<see cref="StopWatchingPointerMoves"/>).
    /// </summary>
    /// <param name="sender">The list.</param>
    /// <param name="e">Pointer data; its original source is the element under the pointer.</param>
    private void ItemsList_PointerMovedWhileSuspended(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || !CanPointerSelect() || !IsPointerSelecting())
        {
            return;
        }

        if (CardUnder(e.OriginalSource as DependencyObject) is { } card)
        {
            SelectByPointer(card);
        }
    }

    /// <summary>
    /// The mouse wheel (or a touchpad's scroll) turned over the list: the mouse is in use on purpose, so it owns the
    /// selection from now on, without having to move first. The rows that scroll under the cursor are then selected as
    /// WinUI reports them (it replays the pointer's position once the scroll has settled).
    /// </summary>
    /// <param name="sender">The list.</param>
    /// <param name="e">Wheel data.</param>
    private void ItemsList_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType is not (PointerDeviceType.Touch or PointerDeviceType.Pen) && CanPointerSelect())
        {
            pointerGate.Activate();
            StopWatchingPointerMoves();
        }
    }

    /// <summary>
    /// Selects the card the mouse is over, without scrolling to it (it is under the cursor, so it is in view; scrolling
    /// would move other rows under the cursor and select those in turn). The mouse over a row also ends a walk of the
    /// arrow keys through the groups column: Up and Down continue from this row.
    /// </summary>
    /// <param name="card">The card under the mouse.</param>
    private void SelectByPointer(ClipItemViewModel card)
    {
        LeaveGroupsNavigation();
        if (!ReferenceEquals(ItemsList.SelectedItem, card))
        {
            ItemsList.SelectedItem = card;
        }
    }

    /// <summary>The card of the list row an element belongs to (itself or an ancestor inside the list).</summary>
    /// <param name="element">The element under the pointer (an event's original source).</param>
    /// <returns>The card, or <see langword="null"/> for the list's empty space or anything outside it.</returns>
    private ClipItemViewModel? CardUnder(DependencyObject? element)
    {
        for (var current = element; current is not null && !ReferenceEquals(current, ItemsList); current = VisualTreeHelper.GetParent(current))
        {
            if (current is SelectorItem row)
            {
                return ItemsList.ItemFromContainer(row) as ClipItemViewModel;
            }
        }

        return null;
    }
}
