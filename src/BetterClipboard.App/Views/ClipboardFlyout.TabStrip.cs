using BetterClipboard.Core.Presentation;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace BetterClipboard.App.Views;

/// <summary>
/// The filter tabs' carousel (All, Pinned, Text, …): the tabs scroll sideways when they do not fit the panel's width, so the
/// panel no longer has to grow for them, and resizing it wider shows more of them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout</b> (<c>TabStrip</c> in the XAML): the <see cref="SelectorBar"/> sits in <see cref="TabsScroller"/>, a
/// horizontal <see cref="ScrollViewer"/> that gives it all the width it wants. "&lt;" and "&gt;" (<see cref="TabsBackButton"/>,
/// <see cref="TabsForwardButton"/>) lie over the strip's edges and show only while there is more to that side
/// (<see cref="TabStripScroll.CanScrollBack"/> / <see cref="TabStripScroll.CanScrollForward"/>); the strip is clipped under a
/// shown arrow, so a label never runs beneath one. Where to scroll for each gesture is <see cref="TabStripScroll"/>'s
/// arithmetic.
/// </para>
/// <para>
/// <b>Gestures:</b> an arrow reveals the next tab (held, it keeps going); the mouse wheel scrolls the strip, the usual
/// wheel as well as a tilt wheel; dragging with the left or middle button grabs and pulls it (past a small threshold, so a
/// click still picks a tab); touch pans it natively. A drag on the strip never moves the window while the strip can
/// scroll (<see cref="IsDragSurface"/>); while every tab fits, its empty space still does, as before.
/// </para>
/// <para>
/// <b>The SelectorBar's template is adjusted twice</b> (<see cref="PrepareTabBarTemplate"/>). It wraps the tabs in an
/// <see cref="ItemsView"/> whose <see cref="ScrollView"/> would claim touch and wheel input at the compositor (it redirects
/// them to its interaction tracker) although it never has anything to scroll here — the outer viewer gives it its full
/// width — so it is silenced. And that ItemsView's <see cref="ItemsRepeater"/> virtualizes (its template sets
/// <c>HorizontalCacheLength="0"</c>): inside a scrolling viewer it realizes only the tabs in view and estimates the rest, so
/// the strip's width changed while scrolling, hidden tabs could not be measured for a step, and screen readers could not
/// reach them (measured 2026-10-01: 9 of 11 tabs realized, steps of a few DIPs, 15 presses to reach the end). It now
/// realizes every tab.
/// </para>
/// </remarks>
public sealed partial class ClipboardFlyout
{
    /// <summary>
    /// The width each arrow covers when its style sets none (<c>TabScrollButtonStyle</c> in App.xaml sets 24): the strip is
    /// clipped by the arrows' own <see cref="FrameworkElement.Width"/>, this only stands in if that is ever unset.
    /// </summary>
    private const double DefaultTabArrowDip = 24;

    /// <summary>
    /// How many viewports' width the tab bar's <see cref="ItemsRepeater"/> keeps realized around the visible part (its
    /// <see cref="ItemsRepeater.HorizontalCacheLength"/>): far more than a dozen short tabs can ever span, so every tab is
    /// laid out — measurable, focusable and visible to screen readers — however far the strip is scrolled. A handful of
    /// tab elements costs nothing to keep.
    /// </summary>
    private const double TabRealizationViewports = 64;

    /// <summary>
    /// Where the last animated scroll of the strip is heading, or <see langword="null"/> when none is running. Arrow
    /// repeats and quick wheel notches step from here instead of from the offset an animation has reached so far, so they
    /// add up instead of repeating the same step.
    /// </summary>
    private double? tabsScrollTarget;

    /// <summary>The pointer of the strip drag in progress (pressed, maybe not moving yet), or <see langword="null"/>.</summary>
    private uint? tabPanPointerId;

    /// <summary>The pointer's position in <see cref="TabsScroller"/> at the press, in DIPs (the viewer itself never moves).</summary>
    private double tabPanStartX;

    /// <summary>The strip's offset at the press.</summary>
    private double tabPanStartOffset;

    /// <summary>Whether the press passed the threshold and now pulls the strip (the pointer is captured).</summary>
    private bool tabPanning;

    /// <summary>
    /// The tab the drag's press landed on, or <see langword="null"/> (the strip's empty space): it has taken the press as
    /// the start of a click, and must be told to forget it when the drag takes over (<see cref="TakeOverTabPress"/>).
    /// </summary>
    private SelectorBarItem? tabPanPressedTab;

    /// <summary>
    /// The clip last given to <see cref="TabsScroller"/> (left inset, right inset, width, height), or <see langword="null"/>
    /// for none: <see cref="UpdateTabArrows"/> runs on every frame of a scroll, and an unchanged clip is not set again.
    /// </summary>
    private (double Left, double Right, double Width, double Height)? appliedTabClip;

    /// <summary>Whether the tabs are wider than the strip, so anything can scroll at all.</summary>
    private bool IsTabStripScrollable => TabsScroller.ScrollableWidth > TabStripScroll.EdgeTolerance;

    /// <summary>
    /// The offset the strip is at or heading to (<see cref="tabsScrollTarget"/>), the base of the next arrow step or wheel
    /// notch.
    /// </summary>
    private double TabsBaseOffset => tabsScrollTarget ?? TabsScroller.HorizontalOffset;

    /// <summary>The strip width each arrow covers while it shows: its own width (<c>TabScrollButtonStyle</c>).</summary>
    private double TabArrowWidth => double.IsFinite(TabsBackButton.Width) && TabsBackButton.Width > 0 ? TabsBackButton.Width : DefaultTabArrowDip;

    /// <summary>
    /// Wires the carousel up (from the constructor, once): arrows follow every change of offset, extent or viewport; the
    /// wheel, drags and bring-into-view requests get their handlers.
    /// </summary>
    /// <remarks>
    /// Several triggers because no single one covers everything: <see cref="ScrollViewer.ViewChanged"/> fires for scrolling
    /// but not for a tab appearing or the panel being resized (the extent or viewport changes, the offset may not), and the
    /// <see cref="ScrollViewer.ScrollableWidthProperty"/> callback fires for those but not for scrolling. The two
    /// <see cref="FrameworkElement.SizeChanged"/> events back the callback up (the viewer's own one also re-cuts the clip,
    /// which follows its size, and the bar's follows a tab appearing): they come after the layout pass, when
    /// <see cref="ScrollViewer.ScrollableWidth"/> is already current. <see cref="UpdateTabArrows"/> is cheap and idempotent,
    /// so a change reported twice costs nothing.
    /// </remarks>
    private void InitializeTabStrip()
    {
        TabsScroller.ViewChanged += TabsScroller_ViewChanged;
        TabsScroller.RegisterPropertyChangedCallback(ScrollViewer.ScrollableWidthProperty, (_, _) => UpdateTabArrows());
        TabsScroller.SizeChanged += (_, _) => UpdateTabArrows();
        Filters.SizeChanged += (_, _) => UpdateTabArrows();
        Filters.Loaded += (_, _) =>
        {
            PrepareTabBarTemplate();
            UpdateTabArrows();
        };

        // handledEventsToo: the SelectorBar's item containers may mark a wheel turn handled although nothing scrolled.
        // On the bar, not on the viewer: the bar comes first on the route, so marking the turn handled here keeps the
        // viewer's own wheel handling from scrolling a second time (it skips handled turns). The arrows lie beside the
        // viewer, not inside it, so they forward their turns themselves.
        Filters.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(TabStrip_PointerWheelChanged), handledEventsToo: true);
        TabsBackButton.PointerWheelChanged += TabStrip_PointerWheelChanged;
        TabsForwardButton.PointerWheelChanged += TabStrip_PointerWheelChanged;
        Filters.BringIntoViewRequested += Filters_BringIntoViewRequested;

        // handledEventsToo: a press on a tab may be marked handled by its container, and the drag must still start there.
        TabsScroller.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(TabsScroller_PointerPressed), handledEventsToo: true);
        TabsScroller.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(TabsScroller_PointerMoved), handledEventsToo: true);
        TabsScroller.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(TabsScroller_PointerEnded), handledEventsToo: true);
        TabsScroller.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(TabsScroller_PointerEnded), handledEventsToo: true);
        TabsScroller.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(TabsScroller_PointerCaptureLost), handledEventsToo: true);
    }

    /// <summary>
    /// Puts the strip back at its start without animating (every summon shows "All", the first tab, selected) and forgets
    /// any scroll still heading elsewhere.
    /// </summary>
    private void ResetTabStrip()
    {
        tabsScrollTarget = null;
        EndTabPan(focusSearch: false);
        TabsScroller.ChangeView(0, null, null, disableAnimation: true);
        UpdateTabArrows();
    }

    /// <summary>
    /// Shows or hides each arrow by whether there is more to its side, and clips the strip under the arrows that show (so
    /// no tab label runs beneath one, and nothing under an arrow can be clicked by mistake).
    /// </summary>
    private void UpdateTabArrows()
    {
        double offset = TabsScroller.HorizontalOffset;
        bool back = TabStripScroll.CanScrollBack(offset);
        bool forward = TabStripScroll.CanScrollForward(offset, TabsScroller.ScrollableWidth);
        TabsBackButton.Visibility = back ? Visibility.Visible : Visibility.Collapsed;
        TabsForwardButton.Visibility = forward ? Visibility.Visible : Visibility.Collapsed;

        double width = TabsScroller.ActualWidth, height = TabsScroller.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return; // not laid out yet: SizeChanged comes back here
        }

        (double Left, double Right, double Width, double Height)? clip = back || forward
            ? (back ? TabArrowWidth : 0, forward ? TabArrowWidth : 0, width, height)
            : null;
        if (clip == appliedTabClip)
        {
            return; // the same arrows at the same size: nothing to re-cut (this runs on every frame of a scroll)
        }

        appliedTabClip = clip;
        TabsScroller.Clip = clip is { } cut
            ? new RectangleGeometry { Rect = new Rect(cut.Left, 0, Math.Max(0, cut.Width - cut.Left - cut.Right), cut.Height) }
            : null;
    }

    /// <summary>The strip scrolled (by any gesture, or an animation step): arrows follow, a finished animation is forgotten.</summary>
    /// <param name="sender">The viewer.</param>
    /// <param name="e">Whether more of this scroll is coming.</param>
    private void TabsScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate)
        {
            tabsScrollTarget = null;
        }

        UpdateTabArrows();
    }

    /// <summary>"&lt;": reveal the previous tab hidden on the left (repeats while held).</summary>
    /// <param name="sender">The arrow.</param>
    /// <param name="e">Click data.</param>
    private void TabsBackButton_Click(object sender, RoutedEventArgs e) => StepTabs(forward: false);

    /// <summary>"&gt;": reveal the next tab hidden on the right (repeats while held).</summary>
    /// <param name="sender">The arrow.</param>
    /// <param name="e">Click data.</param>
    private void TabsForwardButton_Click(object sender, RoutedEventArgs e) => StepTabs(forward: true);

    /// <summary>Scrolls the strip by one tab (<see cref="TabStripScroll.Step"/>), animated.</summary>
    /// <param name="forward"><see langword="true"/> towards the last tab.</param>
    private void StepTabs(bool forward)
    {
        double target = TabStripScroll.Step(MeasureTabExtents(), TabsBaseOffset, TabsScroller.ViewportWidth, TabsScroller.ScrollableWidth, TabArrowWidth, forward);
        ScrollTabsTo(target, animate: true);
    }

    /// <summary>
    /// Scrolls the strip so the selected tab lies wholly between the arrows (<see cref="TabStripScroll.Reveal"/>): for a
    /// selection made from code, which unlike a click or a key brings nothing into view by itself.
    /// </summary>
    /// <param name="animate">Glide there (the panel is open) or jump (it is being prepared for showing).</param>
    private void RevealSelectedTab(bool animate)
    {
        if (Filters.SelectedItem is not { } selected || selected.ActualWidth <= 0 || !IsTabStripScrollable)
        {
            return; // not laid out yet, or nothing to scroll
        }

        double left = selected.TransformToVisual(Filters).TransformPoint(default).X;
        var extent = new TabExtent(left, left + selected.ActualWidth);
        ScrollTabsTo(TabStripScroll.Reveal(extent, TabsBaseOffset, TabsScroller.ViewportWidth, TabsScroller.ScrollableWidth, TabArrowWidth), animate);
    }

    /// <summary>
    /// The visible tabs' extents in the strip's content (<see cref="Filters"/>' own coordinates, which do not move while the
    /// strip scrolls). Collapsed tabs and tabs not laid out yet are left out.
    /// </summary>
    /// <returns>The extents, in tab order.</returns>
    private List<TabExtent> MeasureTabExtents()
    {
        var extents = new List<TabExtent>(Filters.Items.Count);
        foreach (var tab in Filters.Items)
        {
            if (tab.Visibility != Visibility.Visible || tab.ActualWidth <= 0)
            {
                continue;
            }

            double left = tab.TransformToVisual(Filters).TransformPoint(default).X;
            extents.Add(new TabExtent(left, left + tab.ActualWidth));
        }

        return extents;
    }

    /// <summary>
    /// Scrolls the strip to <paramref name="offset"/> (clamped). Animated scrolls remember their target
    /// (<see cref="tabsScrollTarget"/>) until they finish; a target the strip is at or heading to already is left alone.
    /// </summary>
    /// <param name="offset">The wanted offset.</param>
    /// <param name="animate">Glide (arrows, wheel) or jump (a drag follows the pointer directly).</param>
    private void ScrollTabsTo(double offset, bool animate)
    {
        double target = TabStripScroll.Clamp(offset, TabsScroller.ScrollableWidth);
        if (Math.Abs(target - TabsBaseOffset) <= TabStripScroll.EdgeTolerance)
        {
            return;
        }

        tabsScrollTarget = animate ? target : null;
        TabsScroller.ChangeView(target, null, null, disableAnimation: !animate);
    }

    /// <summary>
    /// A wheel turn over the strip or its arrows scrolls it (<see cref="TabStripScroll.Wheel"/>); quick notches add up.
    /// While every tab fits, the turn is left alone: there is nothing to scroll.
    /// </summary>
    /// <param name="sender">The SelectorBar or an arrow.</param>
    /// <param name="e">Wheel data.</param>
    private void TabStrip_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!IsTabStripScrollable)
        {
            return;
        }

        var properties = e.GetCurrentPoint(TabsScroller).Properties;
        if (properties.MouseWheelDelta != 0)
        {
            ScrollTabsTo(TabStripScroll.Wheel(TabsBaseOffset, properties.MouseWheelDelta, properties.IsHorizontalMouseWheel, TabsScroller.ScrollableWidth), animate: true);
        }

        // Handled even at an end: a turn over the strip is the strip's, nothing else in the panel should act on it.
        e.Handled = true;
    }

    /// <summary>
    /// A tab asked to be brought into view (it was clicked, or got the focus): widen the request by an arrow's width on each
    /// side before the viewer acts on it, so the tab ends up clear of the arrows instead of half under one.
    /// </summary>
    /// <param name="sender">The SelectorBar (between the bar's own scroller, which passes the request on, and <see cref="TabsScroller"/>).</param>
    /// <param name="args">The request; its rect is in its target element's coordinates, so it is widened there.</param>
    private void Filters_BringIntoViewRequested(UIElement sender, BringIntoViewRequestedEventArgs args)
    {
        var rect = args.TargetRect;
        if (args.Handled || rect.Width <= 0 || !IsTabStripScrollable)
        {
            return;
        }

        double arrow = TabArrowWidth;
        args.TargetRect = new Rect(rect.X - arrow, rect.Y, rect.Width + (2 * arrow), rect.Height);
    }

    /// <summary>
    /// A mouse press (left or middle button) on the scrollable strip may start a drag that pulls it; nothing is captured yet,
    /// so a click without travel still reaches the tab and picks it.
    /// </summary>
    /// <param name="sender">The viewer.</param>
    /// <param name="e">Pointer data.</param>
    private void TabsScroller_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (tabPanPointerId is not null || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || !IsTabStripScrollable)
        {
            return; // touch and pen pan natively; with every tab in view there is nothing to pull
        }

        var point = e.GetCurrentPoint(TabsScroller);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsMiddleButtonPressed)
        {
            return; // the right button opens menus
        }

        tabPanPointerId = e.Pointer.PointerId;
        tabPanStartX = point.Position.X;
        tabPanStartOffset = TabsScroller.HorizontalOffset;
        tabPanning = false;
        tabPanPressedTab = FindTab(e.OriginalSource as DependencyObject);
    }

    /// <summary>
    /// Past the threshold the drag takes the press over (<see cref="TakeOverTabPress"/>) and pulls the strip with the
    /// pointer (<see cref="TabStripScroll.Drag"/>).
    /// </summary>
    /// <param name="sender">The viewer.</param>
    /// <param name="e">Pointer data.</param>
    private void TabsScroller_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (tabPanPointerId != e.Pointer.PointerId)
        {
            return;
        }

        var point = e.GetCurrentPoint(TabsScroller);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsMiddleButtonPressed)
        {
            // The release went somewhere else (a menu opened, the window lost the pointer): this drag is over.
            EndTabPan(focusSearch: tabPanning);
            return;
        }

        double travel = point.Position.X - tabPanStartX;
        if (!tabPanning)
        {
            // The system's drag threshold, like the window drag: a click stays a click.
            if (Math.Abs(travel) <= DragThresholdDip)
            {
                return;
            }

            if (!TakeOverTabPress(e.Pointer))
            {
                // No capture, no drag: the press stays the tab's (its release may still pick it, like any click).
                tabPanPointerId = null;
                tabPanPressedTab = null;
                return;
            }

            tabPanning = true;
            tabsScrollTarget = null;
        }

        ScrollTabsTo(TabStripScroll.Drag(tabPanStartOffset, travel, TabsScroller.ScrollableWidth), animate: false);
        e.Handled = true;
    }

    /// <summary>
    /// Moves the drag's pointer capture to <see cref="TabsScroller"/>, making the pressed tab forget the press on the way.
    /// </summary>
    /// <param name="pointer">The dragging pointer (pressed).</param>
    /// <returns><see langword="true"/> when the viewer holds the capture.</returns>
    /// <remarks>
    /// <para>
    /// The capture is what keeps the press from picking a tab: tabs select on the button's release (WinUI's ItemsView acts
    /// on <c>PointerReleased</c>, microsoft-ui-xaml <c>ItemsViewInteractions.cpp</c>), and with the viewer holding the
    /// capture the release never reaches the tab.
    /// </para>
    /// <para>
    /// The hand-over goes through the tab: it captures first, so the viewer's capture takes the pointer <i>from</i> it and
    /// raises <c>PointerCaptureLost</c> on it — where a tab (an <c>ItemContainer</c>) forgets a press, resetting its
    /// pressed and pointer-over state. Capturing on the viewer alone left the tab without any event: it stayed drawn
    /// pressed after the drag (seen in the live test's captures, 2026-10-01), and a later release over it, from a press
    /// elsewhere, would still have picked it.
    /// </para>
    /// <para>
    /// Should the viewer ever fail to take the capture from the tab, the tab gives its capture back — which raises the same
    /// <c>PointerCaptureLost</c> on it — and the viewer captures alone afterwards, so the hand-over can never cost the drag
    /// itself.
    /// </para>
    /// </remarks>
    private bool TakeOverTabPress(Pointer pointer)
    {
        bool tabCaptured = tabPanPressedTab?.CapturePointer(pointer) ?? false;
        if (TabsScroller.CapturePointer(pointer))
        {
            return true;
        }

        if (!tabCaptured)
        {
            return false;
        }

        tabPanPressedTab!.ReleasePointerCapture(pointer);
        return TabsScroller.CapturePointer(pointer);
    }

    /// <summary>Release or cancel ends the drag; a drag that pulled the strip hands the keyboard back to the search box.</summary>
    /// <param name="sender">The viewer.</param>
    /// <param name="e">Pointer data.</param>
    private void TabsScroller_PointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (tabPanPointerId != e.Pointer.PointerId)
        {
            return;
        }

        bool pulled = tabPanning;
        EndTabPan(focusSearch: pulled);
        if (pulled)
        {
            // The release belongs to the drag: no tab under the pointer acts on it.
            e.Handled = true;
        }
    }

    /// <summary>
    /// The viewer lost the drag's capture (Windows took it, or the panel hid): the drag ends. A tab losing its capture to
    /// the viewer — the hand-over at the drag's start (<see cref="TakeOverTabPress"/>) — bubbles here too and is ignored.
    /// </summary>
    /// <param name="sender">The viewer.</param>
    /// <param name="e">Pointer data; its original source is the element that lost the capture.</param>
    private void TabsScroller_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, TabsScroller))
        {
            TabsScroller_PointerEnded(sender, e);
        }
    }

    /// <summary>Ends the strip drag, if any, releasing the capture.</summary>
    /// <param name="focusSearch">Give the search box the keyboard back (typing must keep filtering after a drag).</param>
    private void EndTabPan(bool focusSearch)
    {
        if (tabPanPointerId is null)
        {
            return;
        }

        // Clear first: releasing the capture raises PointerCaptureLost, which comes back here.
        bool captured = tabPanning;
        tabPanPointerId = null;
        tabPanning = false;
        tabPanPressedTab = null;
        if (captured)
        {
            TabsScroller.ReleasePointerCaptures();
        }

        if (focusSearch && IsOpen)
        {
            SearchBox.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>The filter tab an element belongs to (itself or an ancestor inside the strip), or <see langword="null"/>.</summary>
    /// <param name="element">The pressed element (an event's original source).</param>
    /// <returns>The tab, or <see langword="null"/> for the strip's empty space or anything outside it.</returns>
    private SelectorBarItem? FindTab(DependencyObject? element)
    {
        for (var current = element; current is not null && !ReferenceEquals(current, TabsScroller); current = VisualTreeHelper.GetParent(current))
        {
            if (current is SelectorBarItem tab)
            {
                return tab;
            }
        }

        return null;
    }

    /// <summary>
    /// Adjusts the SelectorBar's template for life inside <see cref="TabsScroller"/> (once its template exists, on
    /// <see cref="FrameworkElement.Loaded"/>):
    /// <list type="bullet">
    /// <item>its own scroller (<see cref="ItemsView.ScrollView"/>) gives up the input it would otherwise claim — touch, pen
    /// and the wheel, which it redirects to its compositor-side interaction tracker — and scrolls in no direction. It never
    /// has anything to scroll here (the outer viewer gives the bar its full width), so nothing is lost; left alone it would
    /// swallow the gestures meant for <see cref="TabsScroller"/>;</item>
    /// <item>its <see cref="ItemsRepeater"/> realizes every tab (<see cref="TabRealizationViewports"/>) instead of only those
    /// in view, so the strip's width is exact and every tab can be measured, focused and read out.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Keyboard input is left to the inner scroller: the bar's arrow-key navigation between tabs is the ItemsView's own. A
    /// template without these parts (a future WinUI) is left as it is: the carousel then still scrolls, with the old
    /// template's flaws (estimated width while scrolling, gestures possibly taken by the inner scroller).
    /// </remarks>
    private void PrepareTabBarTemplate()
    {
        if (FindDescendant<ItemsView>(Filters) is not { } items)
        {
            return;
        }

        if (items.ScrollView is { } inner)
        {
            inner.HorizontalScrollMode = ScrollingScrollMode.Disabled;
            inner.VerticalScrollMode = ScrollingScrollMode.Disabled;
            inner.ZoomMode = ScrollingZoomMode.Disabled;
            inner.IgnoredInputKinds = ScrollingInputKinds.Touch | ScrollingInputKinds.Pen | ScrollingInputKinds.MouseWheel;
        }

        if (FindDescendant<ItemsRepeater>(items) is { } repeater)
        {
            repeater.HorizontalCacheLength = TabRealizationViewports;
        }
    }
}
