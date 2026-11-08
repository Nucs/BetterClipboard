using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace BetterClipboard.App.Views;

/// <summary>
/// Arranging the filter tabs from their right-click menu — <i>Move left</i>, <i>Move right</i>, <i>Hide</i>, <i>Show all
/// hidden</i> — kept in the settings (<see cref="AppSettings.TabOrder"/>, <see cref="AppSettings.HiddenTabs"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The rules</b> are <see cref="TabArrangement"/>'s (pure, unit-tested): "All" is fixed first — never moved, never
/// hidden, and no tab moves before it (the owner's choice, 2026-11-08); a move skips tabs that do not show right now, so
/// it always moves the tab one visible place; the tabs it passes keep their places. The default order is the XAML's,
/// read once here before anything is rearranged (<see cref="defaultTabOrder"/>).
/// </para>
/// <para>
/// <b>Hiding only takes a tab out of the row</b> (the owner's choice, 2026-11-08): what feeds the tab keeps working — the
/// Settings switches of other apps' tabs stay on, so a tab shown again has everything. A tab shows when its own
/// conditions hold (the <c>Update…Tab</c> methods, <see cref="SetTabVisible"/>) and the user did not hide it
/// (<see cref="IsTabHidden"/>); <see cref="IsTabAvailable"/> applies the same, so a hidden tab is never the one a summon
/// opens in, and the Left and Right keys skip it like any tab that does not show.
/// </para>
/// <para>
/// <b>Rearranging the tabs moves the bar's own items</b> (<see cref="ApplyTabOrder"/>): the SelectorBar shows its
/// <c>Items</c> in their order and has no other way to order them. Taking the selected tab out of the collection can drop
/// or move the bar's selection, so <see cref="Filters_SelectionChanged"/> ignores everything while the items move
/// (<see cref="rearrangingTabs"/>), and the shown tab is selected again afterwards — never counted as a choice of the
/// user's, and without reloading the list.
/// </para>
/// <para>
/// <b>Where the menu opens:</b> a right-click (or a touch press-and-hold) on a tab, the Menu key or Shift+F10 while a tab
/// has the keyboard focus (<see cref="Root_PreviewKeyDown"/>), and a right-click on the row's empty space, where only
/// <i>Show all hidden</i> is offered. Items that cannot apply are grayed out: the moves at the ends and on "All",
/// <i>Hide</i> on "All", <i>Show all hidden</i> while nothing is hidden (it says how many are).
/// </para>
/// </remarks>
public sealed partial class ClipboardFlyout
{
    /// <summary>
    /// The tabs' names in the XAML's order — the default order, which <see cref="AppSettings.TabOrder"/> is stored
    /// against. Read once, before the first rearrangement (<see cref="InitializeTabArrangement"/>).
    /// </summary>
    private IReadOnlyList<string> defaultTabOrder = [];

    /// <summary>The full order the bar's items stand in now (<see cref="TabArrangement.Arrange"/>), every tab once.</summary>
    private IReadOnlyList<string> tabOrder = [];

    /// <summary>The tabs the user took out of the row (<see cref="AppSettings.HiddenTabs"/>); never "All".</summary>
    private readonly HashSet<ClipFilter> hiddenTabs = [];

    /// <summary>
    /// Set while <see cref="ApplyTabOrder"/> moves the bar's items: the selection changes the bar reports meanwhile are
    /// side effects of the move, never a choice, and <see cref="Filters_SelectionChanged"/> ignores them.
    /// </summary>
    private bool rearrangingTabs;

    /// <summary>
    /// Wires the arrangement up (from the constructor, once, before the bar loads): reads the default order from the
    /// XAML, puts the tabs in the stored order, takes the hidden built-in tabs out (the other apps' tabs follow when their
    /// status is applied), and attaches the menus.
    /// </summary>
    /// <remarks>
    /// Before the bar loads, moving its items costs nothing visible and needs no selection care: the first
    /// <see cref="ResetViewForShow"/> selects the tab a summon starts in afterwards, and <see cref="SelectFilter"/> keeps
    /// the items' selection flags right until the bar loads.
    /// </remarks>
    private void InitializeTabArrangement()
    {
        defaultTabOrder = Filters.Items.Select(TabName).ToArray();
        var settings = controller.Settings.Current;
        foreach (var name in settings.HiddenTabs)
        {
            if (TabArrangement.CanHide(name) && AppSettings.TryParseTab(name, out var filter))
            {
                hiddenTabs.Add(filter);
            }
        }

        ApplyTabOrder(TabArrangement.Arrange(defaultTabOrder, settings.TabOrder));
        UpdateBuiltInTabs();

        foreach (var tab in Filters.Items)
        {
            tab.RightTapped += Tab_RightTapped;
        }

        // Not handledEventsToo: a right-click on a tab is handled by the tab's own menu, and must not open this one too.
        TabsScroller.RightTapped += TabsScroller_RightTapped;
    }

    /// <summary>Whether the user hid a tab (right-click › <i>Hide</i>). "All" never is.</summary>
    /// <param name="filter">The tab's filter.</param>
    /// <returns><see langword="true"/> while the tab is hidden.</returns>
    private bool IsTabHidden(ClipFilter filter) => hiddenTabs.Contains(filter);

    /// <summary>
    /// Shows or hides the built-in tabs that can be hidden (Pinned, Text, Images, Links, Files): unlike the other apps'
    /// tabs they have no condition of their own, only the user's choice. A selected tab that goes hands the panel over to
    /// "All" (<see cref="SetTabVisible"/>).
    /// </summary>
    private void UpdateBuiltInTabs()
    {
        foreach (var filter in (ReadOnlySpan<ClipFilter>)[ClipFilter.Pinned, ClipFilter.Text, ClipFilter.Images, ClipFilter.Links, ClipFilter.Files])
        {
            if (TabOf(filter) is { } tab)
            {
                SetTabVisible(tab, available: true, filter);
            }
        }
    }

    /// <summary>A tab's name: its <see cref="FrameworkElement.Tag"/>, the name of its filter.</summary>
    /// <param name="tab">The tab.</param>
    /// <returns>The name; empty for a tab without one (none in the XAML).</returns>
    private static string TabName(SelectorBarItem tab) => tab.Tag as string ?? string.Empty;

    /// <summary>The tab with a name (compared ignoring case), or <see langword="null"/>.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The tab.</returns>
    private SelectorBarItem? TabNamed(string name)
    {
        foreach (var tab in Filters.Items)
        {
            if (string.Equals(TabName(tab), name, StringComparison.OrdinalIgnoreCase))
            {
                return tab;
            }
        }

        return null;
    }

    /// <summary>Whether a tab shows in the row right now — neither hidden nor absent: what the moves count.</summary>
    /// <param name="name">The tab's name.</param>
    /// <returns><see langword="true"/> when the tab is visible.</returns>
    private bool IsTabShown(string name) => TabNamed(name) is { Visibility: Visibility.Visible };

    /// <summary>
    /// Puts the bar's items in an order, moving only those out of place, and keeps the shown tab selected without the
    /// move counting as a choice or reloading the list.
    /// </summary>
    /// <param name="order">The full order (<see cref="TabArrangement.Arrange"/> or <see cref="TabArrangement.Move"/>).</param>
    /// <remarks>
    /// The SelectorBar's inner ItemsView follows its <c>Items</c>: an item taken out and put back is laid out again in its
    /// new place, the same element with the same handlers. The selection may come back by itself — the ItemsView selects
    /// an item flagged <c>IsSelected</c> when it prepares it again (<c>ItemsView::OnItemsRepeaterElementPrepared</c>, see
    /// <see cref="SelectFilter"/>) — or not, so the shown tab is selected again here explicitly, still inside the guard.
    /// </remarks>
    private void ApplyTabOrder(IReadOnlyList<string> order)
    {
        var shown = TabOf(ViewModel.Filter);
        rearrangingTabs = true;
        try
        {
            for (int i = 0; i < order.Count; i++)
            {
                int current = -1;
                for (int j = i; j < Filters.Items.Count; j++)
                {
                    if (string.Equals(TabName(Filters.Items[j]), order[i], StringComparison.OrdinalIgnoreCase))
                    {
                        current = j;
                        break;
                    }
                }

                if (current > i)
                {
                    var tab = Filters.Items[current];
                    Filters.Items.RemoveAt(current);
                    Filters.Items.Insert(i, tab);
                }
            }

            // Before the bar loads, the selection is the items' flags, set by SelectFilter: nothing to restore yet.
            if (Filters.IsLoaded && shown is not null && !ReferenceEquals(Filters.SelectedItem, shown))
            {
                Filters.SelectedItem = shown;
            }
        }
        finally
        {
            rearrangingTabs = false;
        }

        tabOrder = order;
    }

    /// <summary>Moves a tab one shown place (<see cref="TabArrangement.Move"/>), keeps it in view, and saves the order.</summary>
    /// <param name="tab">The tab.</param>
    /// <param name="toRight"><see langword="true"/> for <i>Move right</i>.</param>
    private void MoveTab(SelectorBarItem tab, bool toRight)
    {
        var order = TabArrangement.Move(tabOrder, TabName(tab), toRight, IsTabShown);
        if (ReferenceEquals(order, tabOrder))
        {
            return; // the menu offers only possible moves; a stale menu (the row changed under it) does nothing
        }

        ApplyTabOrder(order);
        AppLog.Info($"Tabs: {TabName(tab)} moved {(toRight ? "right" : "left")}.");
        SaveTabArrangement();

        // The tab's new place is known after a layout pass; then it is brought clear of the arrows, like a clicked tab.
        Filters.UpdateLayout();
        RevealTab(tab, animate: true);
    }

    /// <summary>
    /// Takes a tab out of the row and saves that. When it is the tab shown, the panel goes to "All", which then also
    /// becomes the tab the next summon opens in: leaving a tab by hiding it is the user's choice to be elsewhere.
    /// </summary>
    /// <param name="tab">The tab (never "All": the menu grays <i>Hide</i> out there).</param>
    private void HideTab(SelectorBarItem tab)
    {
        string name = TabName(tab);
        if (!TabArrangement.CanHide(name) || !AppSettings.TryParseTab(name, out var filter) || !hiddenTabs.Add(filter))
        {
            return;
        }

        bool wasShown = ViewModel.Filter == filter;
        AppLog.Info($"Tabs: {name} hidden.");
        SaveTabArrangement();

        // Every tab's visibility follows the new set; the one hidden goes, and a selected one hands over to All.
        UpdateBuiltInTabs();
        UpdateSourceTabs();
        if (wasShown)
        {
            RememberTab(ClipFilter.All);
        }
    }

    /// <summary>
    /// Brings every hidden tab back and saves that. Each shows again where it stands in the order, as soon as its own
    /// conditions hold (another app's tab still needs that app).
    /// </summary>
    private void ShowAllHiddenTabs()
    {
        if (hiddenTabs.Count == 0)
        {
            return;
        }

        AppLog.Info($"Tabs: {hiddenTabs.Count} hidden shown again.");
        hiddenTabs.Clear();
        SaveTabArrangement();
        UpdateBuiltInTabs();
        UpdateSourceTabs();
    }

    /// <summary>
    /// Writes the order (empty while it is the default, <see cref="TabArrangement.ToStored"/>) and the hidden tabs to the
    /// settings. A failed write is logged; the arrangement then lasts until the app closes.
    /// </summary>
    private void SaveTabArrangement()
    {
        var order = TabArrangement.ToStored(tabOrder, defaultTabOrder);
        var hidden = tabOrder.Where(name => AppSettings.TryParseTab(name, out var filter) && hiddenTabs.Contains(filter)).ToArray();
        try
        {
            controller.Settings.Update(s => s with { TabOrder = order, HiddenTabs = hidden });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The panel keeps the arrangement in memory; it only fails to reach the next start.
            AppLog.Warn($"Saving the tabs' order failed: {ex.Message}");
        }
    }

    /// <summary>Scrolls the strip so a tab lies wholly between the arrows (<see cref="TabStripScroll.Reveal"/>).</summary>
    /// <param name="tab">The tab, laid out.</param>
    /// <param name="animate">Glide there, or jump.</param>
    private void RevealTab(SelectorBarItem tab, bool animate)
    {
        if (tab.ActualWidth <= 0 || !IsTabStripScrollable)
        {
            return; // not laid out yet, or nothing to scroll
        }

        double left = tab.TransformToVisual(Filters).TransformPoint(default).X;
        var extent = new TabExtent(left, left + tab.ActualWidth);
        ScrollTabsTo(TabStripScroll.Reveal(extent, TabsBaseOffset, TabsScroller.ViewportWidth, TabsScroller.ScrollableWidth, TabArrowWidth), animate);
    }

    /// <summary>A right-click (or a touch press-and-hold) on a tab opens its menu.</summary>
    /// <param name="sender">The tab.</param>
    /// <param name="e">Where the click was.</param>
    private void Tab_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is SelectorBarItem tab)
        {
            ShowTabMenu(tab, tab, e.GetPosition(tab));
            e.Handled = true;
        }
    }

    /// <summary>A right-click on the row's empty space opens the menu with <i>Show all hidden</i> alone.</summary>
    /// <param name="sender">The strip's viewer.</param>
    /// <param name="e">Where the click was.</param>
    private void TabsScroller_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        ShowTabMenu(null, TabsScroller, e.GetPosition(TabsScroller));
        e.Handled = true;
    }

    /// <summary>
    /// Opens the tab menu: <i>Move left</i>, <i>Move right</i>, <i>Hide</i> for a tab, and <i>Show all hidden</i> — the
    /// latter alone for the row's empty space. What cannot apply is grayed out rather than left out, so the menu keeps its
    /// shape.
    /// </summary>
    /// <param name="tab">The tab the menu is for, or <see langword="null"/> for the row's empty space.</param>
    /// <param name="target">Placement target.</param>
    /// <param name="position">Position relative to <paramref name="target"/>.</param>
    private void ShowTabMenu(SelectorBarItem? tab, FrameworkElement target, global::Windows.Foundation.Point position)
    {
        var menu = new MenuFlyout();
        if (tab is not null)
        {
            string name = TabName(tab);
            var left = MenuItem(UiText.Translate("Move left"), "\uE72B", null, () => MoveTab(tab, toRight: false));
            left.IsEnabled = TabArrangement.CanMove(tabOrder, name, toRight: false, IsTabShown);
            var right = MenuItem(UiText.Translate("Move right"), "\uE72A", null, () => MoveTab(tab, toRight: true));
            right.IsEnabled = TabArrangement.CanMove(tabOrder, name, toRight: true, IsTabShown);
            var hide = MenuItem(UiText.Translate("Hide"), "\uED1A", null, () => HideTab(tab));
            hide.IsEnabled = TabArrangement.CanHide(name);
            menu.Items.Add(left);
            menu.Items.Add(right);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(hide);
        }

        // The count says how many come back; another app's tab among them shows only while that app is there.
        int count = hiddenTabs.Count;
        var show = MenuItem(count > 0 ? UiText.Format($"Show all hidden ({count})") : UiText.Translate("Show all hidden"), "\uE890", null, ShowAllHiddenTabs);
        show.IsEnabled = count > 0;
        menu.Items.Add(show);

        menu.Opened += Popup_Opened;
        menu.Closed += Popup_Closed;
        menu.ShowAt(target, new FlyoutShowOptions { Position = position });
    }
}
