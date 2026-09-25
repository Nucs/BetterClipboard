using System.Diagnostics;
using System.Numerics;
using BetterClipboard.App.Interop;
using BetterClipboard.App.ViewModels;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;
using BetterClipboard.Windows.Input;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace BetterClipboard.App.Views;

/// <summary>
/// The Win+V replacement window: created once, then shown next to the caret and hidden again after
/// each use (showing a warm window is instant; creating a XAML window is not).
/// </summary>
/// <remarks>
/// <para>
/// <b>Chrome.</b> Caption-less, non-resizable, always-on-top, hidden from Alt+Tab and the taskbar, with
/// a desktop-acrylic backdrop and DWM rounded corners — the look of a system flyout.
/// </para>
/// <para>
/// <b>Dismissal.</b> Like a flyout it hides when it loses activation, except while one of its own popups
/// (context menu, clear confirmation) is open — those take activation briefly.
/// </para>
/// <para>
/// <b>Moving.</b> Having no title bar, it moves by dragging its background: anything that is not a
/// control (header, gaps, footer, the list's empty space; for touch and pen not the list, which they pan).
/// The drag runs on pointer capture + <see cref="WindowDragTracker"/> with Win32 screen coordinates, and
/// Esc during a drag puts the window back — like a title-bar drag. Windows' own move loop is not used:
/// in a WinUI window it never sees the button-up and the window sticks to the cursor (see
/// <see cref="ScreenPointer"/>). The next summon anchors the flyout at the caret again, like Win+V.
/// </para>
/// <para>
/// <b>Groups column.</b> The bookmark button (or Ctrl+G) opens a column of group icons on the left. The window
/// grows <see cref="GroupsPaneDip"/> to the left (<see cref="FlyoutPositioner.ExtendLeft"/>), so the list,
/// search box and buttons keep their screen position. The header's logo and title slide left into the
/// column's top: the logo is its first icon and stands for the regular view. Cards dragged onto an icon join
/// that group. Right-click a group icon to rename it, change its icon or delete it; right-click a card to
/// take it out of a group. The open/closed state is remembered (<see cref="AppSettings.ShowGroupsPane"/>).
/// </para>
/// </remarks>
public sealed partial class ClipboardFlyout : Window
{
    /// <summary>Flyout size in DIPs (Win+V is ~360×450; a little larger shows two more cards).</summary>
    private const double WidthDip = 400;

    /// <summary>Flyout height in DIPs.</summary>
    private const double HeightDip = 560;

    /// <summary>Gap between caret and flyout, in DIPs.</summary>
    private const double GapDip = 8;

    /// <summary>Finger travel before a touch drag moves the window: fingers wobble more than a mouse, and a tap must stay a tap.</summary>
    private const double TouchDragThresholdDip = 10;

    /// <summary>Mouse/pen travel before a drag moves the window — the system's drag threshold (4 px at 100 %), so a click stays a click.</summary>
    private const double DragThresholdDip = 4;

    /// <summary>Width of the groups column in DIPs: a 36-DIP icon plus the 8-DIP gap to the list.</summary>
    private const double GroupsPaneDip = 44;

    /// <summary>
    /// Logo button width with the column closed (the logo's old spot, so the header looks as before) and open
    /// (the column's icon width, so the logo sits exactly above the group icons).
    /// </summary>
    private const double LogoClosedDip = 24, LogoOpenDip = 36;

    /// <summary>
    /// Drag-and-drop format that marks a drag of our own cards (comma-separated entry ids). Only our group
    /// icons accept it; other apps and our own text boxes see nothing they understand and refuse the drop.
    /// </summary>
    private const string ClipIdsFormat = "BetterClipboard.ClipIds";

    private readonly AppController controller;
    private readonly nint hwnd;
    private ScrollViewer? scrollViewer;
    private int openPopups;
    private bool closingForExit;
    private bool reloadPending;

    /// <summary>Whether the groups column is showing (mirrors <see cref="AppSettings.ShowGroupsPane"/>).</summary>
    private bool groupsPaneOpen;

    /// <summary>Entry ids of the card drag in progress, or <see langword="null"/>.</summary>
    private long[]? draggedClipIds;

    /// <summary>The background drag in progress, or <see langword="null"/>.</summary>
    private WindowDragTracker? drag;

    /// <summary>Pointer that owns <see cref="drag"/> (a second finger or the mouse must not hijack it).</summary>
    private uint dragPointerId;

    /// <summary>
    /// Creates the (hidden) flyout window.
    /// </summary>
    /// <param name="controller">App controller.</param>
    public ClipboardFlyout(AppController controller)
    {
        this.controller = controller;
        ViewModel = new FlyoutViewModel(controller);
        InitializeComponent();
        hwnd = WindowNative.GetWindowHandle(this);
        ConfigureChrome();

        Activated += OnActivated;
        AppWindow.Closing += OnClosing;
        controller.HistoryChanged += OnHistoryChanged;
        controller.ShareXStatusChanged += OnShareXStatusChanged;
        controller.GroupsChanged += OnGroupsChanged;
        ViewModel.GroupsReloaded += (_, _) => RebuildGroupButtons();
        ViewModel.PropertyChanged += (_, e) =>
        {
            // Any route to another view (icon click, reset on show, deleted group) must move the highlight.
            if (e.PropertyName == nameof(FlyoutViewModel.SelectedGroupId))
            {
                UpdateGroupSelectionVisuals();
            }

            // The group name column fills and trims only while it has text (see TitlePanel in the XAML).
            if (e.PropertyName == nameof(FlyoutViewModel.GroupTitle))
            {
                GroupTitleColumn.Width = ViewModel.GroupTitle.Length == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            }
        };
        ItemsList.Loaded += (_, _) => HookScrollViewer();

        // Background drags move the window (see class remarks). handledEventsToo: a control may mark a
        // press on its empty space as handled although that space looks like background, so IsDragSurface
        // decides what is background instead of relying on whoever handled the press.
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Root_PointerPressed), handledEventsToo: true);
        Root.PointerMoved += Root_PointerMoved;
        Root.PointerReleased += Root_PointerEnded;
        Root.PointerCanceled += Root_PointerEnded;
        Root.PointerCaptureLost += Root_PointerEnded;

        // Like Win+V, the top item is always "armed": after every (re)load — typing a search, switching
        // a filter — select the first card so Enter pastes the best match immediately.
        ViewModel.Reloaded += (_, _) =>
        {
            if (ItemsList.SelectedIndex < 0 && ViewModel.Items.Count > 0)
            {
                SelectIndex(0);
            }
        };
    }

    /// <summary>View model bound by the XAML.</summary>
    public FlyoutViewModel ViewModel { get; }

    /// <summary>Window handle (used to detect "shortcut pressed while we are in front").</summary>
    public nint Handle => hwnd;

    /// <summary>Whether the flyout is currently shown.</summary>
    public bool IsOpen => AppWindow.IsVisible;

    /// <summary>
    /// Shows the flyout for a summon: resets search, reloads history, positions it by the caret and
    /// takes focus (search box focused so the user can type immediately).
    /// </summary>
    /// <param name="context">Caret/cursor snapshot.</param>
    /// <param name="placement">Placement preference.</param>
    public async void ShowAt(ForegroundContext context, FlyoutPlacement placement)
    {
        try
        {
            // A drag interrupted by hiding normally ends through capture loss; never let one survive into
            // a new summon, where the next pointer move would jump the window.
            EndDrag();
            controller.ApplyTheme(Root);
            ViewModel.ResetForShow();
            UpdateShareXTab();
            SelectFilter(ClipFilter.All);
            groupsPaneOpen = controller.Settings.Current.ShowGroupsPane;
            ApplyGroupsPaneLayout();

            // Show and take focus FIRST, load after: keys typed right after the shortcut must land in our
            // search box. Loading first (even for a few ms, more on a cold start) lets fast typists' keys
            // leak into the document underneath. The list briefly shows its previous (near-identical) content.
            var anchorPoint = placement == FlyoutPlacement.CenterScreen && context.TargetWindow != 0
                ? default
                : (context.Caret is { } caret && placement == FlyoutPlacement.NearCaret
                    ? new ScreenPoint(caret.Left, caret.Bottom)
                    : context.Cursor);
            var (workArea, scale) = placement == FlyoutPlacement.CenterScreen && context.TargetWindow != 0
                ? MonitorLookup.FromWindow(context.TargetWindow)
                : MonitorLookup.FromPoint(anchorPoint);
            var bounds = FlyoutPositioner.Compute(placement, context, workArea,
                (int)Math.Round(WidthDip * scale), (int)Math.Round(HeightDip * scale), (int)Math.Round(GapDip * scale));
            if (groupsPaneOpen)
            {
                // Place the list where it always goes, then add the column on its left.
                bounds = FlyoutPositioner.ExtendLeft(bounds, (int)Math.Round(GroupsPaneDip * scale), workArea);
            }

            var rect = new RectInt32(bounds.Left, bounds.Top, bounds.Width, bounds.Height);

            AppWindow.MoveAndResize(rect);
            AppWindow.Show(true);
            // Moving between monitors with different DPI may have rescaled the window on show; re-apply.
            AppWindow.MoveAndResize(rect);
            Activate();
            ForegroundHelper.Activate(hwnd);

            SearchBox.Focus(FocusState.Programmatic);
            PlayEntranceAnimation();

            // Groups load alongside the list: cards created before the groups arrive get their badges when
            // LoadGroupsAsync refreshes them.
            var groups = ViewModel.LoadGroupsAsync();
            await ViewModel.ReloadAsync();
            SelectIndex(0);
            await groups;
        }
        catch (Exception ex)
        {
            AppLog.Error("Showing the flyout failed.", ex);
        }
    }

    /// <summary>
    /// Hides the flyout.
    /// </summary>
    /// <param name="restoreFocus">Re-activate the window the user came from (Esc / toggle), as opposed to a paste which handles focus itself.</param>
    public void Dismiss(bool restoreFocus)
    {
        if (!IsOpen)
        {
            return;
        }

        // Order matters: re-activate the target while we are still the foreground window (then
        // SetForegroundWindow is always allowed), and only then hide. Hiding first hands activation to
        // whatever window Windows picks next, after which we may no longer steal it back.
        if (restoreFocus)
        {
            ForegroundHelper.Activate(controller.PasteTargetWindow);
        }

        AppWindow.Hide();
    }

    /// <summary>Really closes the window during app exit (normal closes are turned into hides).</summary>
    public void CloseForExit()
    {
        closingForExit = true;
        controller.HistoryChanged -= OnHistoryChanged;
        controller.ShareXStatusChanged -= OnShareXStatusChanged;
        controller.GroupsChanged -= OnGroupsChanged;
        Close();
    }

    /// <summary>Applies window chrome (see class remarks).</summary>
    private void ConfigureChrome()
    {
        Title = "BetterClipboard";
        AppWindow.SetIcon(AppController.IconPath);
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        SystemBackdrop = new DesktopAcrylicBackdrop();
        WindowInterop.UseRoundedCorners(hwnd);
    }

    /// <summary>Slides and fades the content in (150 ms) for a polished appearance.</summary>
    private void PlayEntranceAnimation()
    {
        var visual = ElementCompositionPreview.GetElementVisual(Root);
        ElementCompositionPreview.SetIsTranslationEnabled(Root, true);
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, easing);
        fade.Duration = TimeSpan.FromMilliseconds(150);

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3(0, 10, 0));
        slide.InsertKeyFrame(1f, Vector3.Zero, easing);
        slide.Duration = TimeSpan.FromMilliseconds(220);

        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Translation", slide);
    }

    /// <summary>Hides on deactivation (flyout semantics) unless one of our popups took activation.</summary>
    /// <param name="sender">The window.</param>
    /// <param name="args">Activation data.</param>
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (openPopups == 0 && IsOpen)
            {
                AppWindow.Hide();
            }

            return;
        }

        // (Re)take keyboard focus on every activation: on the very first show the XAML tree is not
        // loaded yet when ShowAt runs, so its Focus() call is a silent no-op — typing would go nowhere.
        if (openPopups == 0)
        {
            SearchBox.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>Turns user/system close requests into a hide so the warm window can be reused.</summary>
    /// <param name="sender">The app window.</param>
    /// <param name="args">Close data.</param>
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!closingForExit)
        {
            args.Cancel = true;
            Dismiss(restoreFocus: true);
        }
    }

    /// <summary>Keeps the list current while visible; defers reloads while hidden.</summary>
    /// <param name="sender">Controller.</param>
    /// <param name="change">What changed.</param>
    private async void OnHistoryChanged(object? sender, ClipChangedEventArgs change)
    {
        if (!IsOpen || !ViewModel.TryApplyInPlace(change) || reloadPending)
        {
            return;
        }

        // Coalesce bursts (e.g. an import) into one reload.
        reloadPending = true;
        try
        {
            await Task.Delay(80);
            if (IsOpen)
            {
                var selected = (ItemsList.SelectedItem as ClipItemViewModel)?.Id;
                await ViewModel.ReloadAsync();
                if (selected is { } id && ViewModel.Items.FirstOrDefault(i => i.Id == id) is { } again)
                {
                    ItemsList.SelectedItem = again;
                }
            }
        }
        finally
        {
            reloadPending = false;
        }
    }

    /// <summary>Keyboard model: arrows move, Enter pastes, Esc closes, Ctrl+P pins, Del deletes, Ctrl+1..9 quick-paste.</summary>
    /// <param name="sender">Root grid.</param>
    /// <param name="e">Key data.</param>
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsKeyDown(VirtualKey.Control);
        bool shift = IsKeyDown(VirtualKey.Shift);
        switch (e.Key)
        {
            case VirtualKey.Down:
                MoveSelection(1);
                break;
            case VirtualKey.Up:
                MoveSelection(-1);
                break;
            case VirtualKey.PageDown:
                MoveSelection(5);
                break;
            case VirtualKey.PageUp:
                MoveSelection(-5);
                break;
            case VirtualKey.Enter:
                // Fall back to the first card if the selection was lost (e.g. mid-reload).
                if ((Selected ?? ViewModel.Items.FirstOrDefault()) is { } toPaste)
                {
                    _ = controller.PasteAsync(toPaste.Entry, plainText: shift);
                }

                break;
            case VirtualKey.Escape when drag is { IsMoving: true } moving:
                // Mid-drag, Esc cancels the move like it does for a title-bar drag — and only that; the
                // flyout stays open and the search is kept.
                AppWindow.Move(new PointInt32(moving.WindowStart.X, moving.WindowStart.Y));
                EndDrag();
                break;
            case VirtualKey.Escape:
                if (!string.IsNullOrEmpty(ViewModel.SearchText))
                {
                    ViewModel.SearchText = string.Empty;
                }
                else
                {
                    Dismiss(restoreFocus: true);
                }

                break;
            case VirtualKey.Delete when shift || string.IsNullOrEmpty(ViewModel.SearchText):
                if (Selected is { } toDelete)
                {
                    DeleteItem(toDelete);
                }

                break;
            case VirtualKey.P when ctrl:
                if (Selected is { } toPin)
                {
                    _ = controller.History.SetPinnedAsync(toPin.Id, !toPin.IsPinned);
                }

                break;
            case VirtualKey.F when ctrl:
                SearchBox.Focus(FocusState.Keyboard);
                break;
            case VirtualKey.G when ctrl:
                SetGroupsPane(!groupsPaneOpen);
                break;
            case >= VirtualKey.Number1 and <= VirtualKey.Number9 when ctrl:
                int index = e.Key - VirtualKey.Number1;
                if (index < ViewModel.Items.Count)
                {
                    _ = controller.PasteAsync(ViewModel.Items[index].Entry, plainText: shift);
                }

                break;
            case VirtualKey.Application:
            case VirtualKey.F10 when shift:
                if (Selected is { } forMenu && ItemsList.ContainerFromItem(forMenu) is FrameworkElement container)
                {
                    ShowItemMenu(forMenu, container, new global::Windows.Foundation.Point(24, 24));
                }

                break;
            default:
                return; // not ours: let the search box handle typing
        }

        e.Handled = true;
    }

    /// <summary>
    /// A primary-button press (or touch/pen contact) on the background starts a <see cref="drag"/>;
    /// presses on controls are left alone.
    /// </summary>
    /// <param name="sender">Root grid.</param>
    /// <param name="e">Pointer data.</param>
    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var device = e.Pointer.PointerDeviceType;
        if (drag is not null || e.OriginalSource is not DependencyObject source || !IsDragSurface(source, device))
        {
            return;
        }

        // Right- and middle-clicks on the background keep doing nothing (XAML reports the logical,
        // swap-aware primary button as "left").
        if (device == PointerDeviceType.Mouse && !e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Capture keeps the moves coming when a fast drag leaves the window, and guarantees the release
        // (or a capture-lost) arrives — so the drag always ends. No screen position (secure desktop,
        // unknown pointer) means no drag rather than a guessed one.
        if (PointerOnScreen(e) is not { } pointer || !Root.CapturePointer(e.Pointer))
        {
            return;
        }

        double threshold = device == PointerDeviceType.Touch ? TouchDragThresholdDip : DragThresholdDip;
        drag = new WindowDragTracker((int)Math.Round(threshold * Scale));
        dragPointerId = e.Pointer.PointerId;
        drag.Begin(pointer, new ScreenPoint(AppWindow.Position.X, AppWindow.Position.Y));
        e.Handled = true;
    }

    /// <summary>Moves the window with the drag once it passed the threshold.</summary>
    /// <param name="sender">Root grid.</param>
    /// <param name="e">Pointer data.</param>
    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (drag is null || e.Pointer.PointerId != dragPointerId)
        {
            return;
        }

        if (PointerOnScreen(e) is { } pointer && drag.Update(pointer) is { } next)
        {
            AppWindow.Move(new PointInt32(next.X, next.Y));
        }

        e.Handled = true;
    }

    /// <summary>Ends the drag on release, cancel or capture loss (e.g. the flyout was dismissed mid-drag).</summary>
    /// <param name="sender">Root grid.</param>
    /// <param name="e">Pointer data.</param>
    private void Root_PointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (drag is not null && e.Pointer.PointerId == dragPointerId)
        {
            EndDrag();
        }
    }

    /// <summary>
    /// Ends the current drag, if any: releases the capture and gives the search box its focus back — a
    /// press on the list's empty space focused the list, and typing must keep filtering.
    /// </summary>
    private void EndDrag()
    {
        if (drag is null)
        {
            return;
        }

        // Clear the field first: releasing the capture raises PointerCaptureLost, which re-enters here.
        drag.End();
        drag = null;
        Root.ReleasePointerCaptures();
        if (IsOpen)
        {
            SearchBox.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>
    /// Whether a press on <paramref name="source"/> is on the flyout's background, which moves the
    /// window, rather than on something that reacts to it.
    /// </summary>
    /// <remarks>
    /// Walks from the pressed element up to <see cref="Root"/>. Any control on the way (buttons, the search
    /// box, filter pills, cards, scroll bars) means "not background". Plain visuals — the title, icons,
    /// the paused chip, the footer, gaps, the list's and the filter bar's empty space — are background.
    /// Touch and pen inside the list are not, because a finger drag there must keep scrolling the list.
    /// A source outside Root's tree (popups) is never background.
    /// </remarks>
    /// <param name="source">The element that received the press (<see cref="RoutedEventArgs.OriginalSource"/>).</param>
    /// <param name="device">Pointer type.</param>
    /// <returns><see langword="true"/> when a drag from here should move the window.</returns>
    private bool IsDragSurface(DependencyObject source, PointerDeviceType device)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, Root))
            {
                return true;
            }

            switch (current)
            {
                case ButtonBase or TextBox or AutoSuggestBox or PasswordBox or RichEditBox
                    or SelectorItem or SelectorBarItem or RangeBase or Thumb or ToggleSwitch:
                    return false;
                case ListViewBase when device != PointerDeviceType.Mouse:
                    return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Screen position of the pointer behind <paramref name="e"/>, independent of the window's own
    /// position (see <see cref="ScreenPointer"/> for why positions inside the moving window won't do).
    /// </summary>
    /// <param name="e">Pointer data.</param>
    /// <returns>The position in physical screen pixels, or <see langword="null"/> when Windows cannot say.</returns>
    private static ScreenPoint? PointerOnScreen(PointerRoutedEventArgs e) =>
        e.Pointer.PointerDeviceType == PointerDeviceType.Mouse
            ? ScreenPointer.Cursor()
            : ScreenPointer.TryGetPointer(e.Pointer.PointerId, out var point) ? point : null;

    /// <summary>Current DIP-to-pixel scale of the flyout's monitor (1.0 before the content is loaded).</summary>
    private double Scale => Root.XamlRoot?.RasterizationScale ?? 1.0;

    /// <summary>Click on a card pastes it.</summary>
    /// <param name="sender">List.</param>
    /// <param name="e">Click data.</param>
    private void ItemsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ClipItemViewModel item)
        {
            _ = controller.PasteAsync(item.Entry, plainText: IsKeyDown(VirtualKey.Shift));
        }
    }

    /// <summary>Right-click opens the item menu.</summary>
    /// <param name="sender">List.</param>
    /// <param name="e">Tap data.</param>
    private void ItemsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ClipItemViewModel item)
        {
            ItemsList.SelectedItem = item;
            ShowItemMenu(item, ItemsList, e.GetPosition(ItemsList));
            e.Handled = true;
        }
    }

    /// <summary>Starts thumbnail loading as cards are realized (virtualization-friendly lazy loading).</summary>
    /// <param name="sender">List.</param>
    /// <param name="args">Container data.</param>
    private void ItemsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is ClipItemViewModel item)
        {
            item.EnsureThumbnail();
        }
    }

    /// <summary>Filter pill changed.</summary>
    /// <param name="sender">Selector bar.</param>
    /// <param name="args">Change data.</param>
    private void Filters_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string tag && Enum.TryParse<ClipFilter>(tag, out var filter))
        {
            ViewModel.Filter = filter;
        }
    }

    /// <summary>Pause/resume capture.</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Click data.</param>
    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        var paused = !controller.Settings.Current.IsCapturePaused;
        controller.Settings.Update(s => s with { IsCapturePaused = paused });
        ViewModel.IsPaused = paused;
    }

    /// <summary>Confirmed "clear history" (unpinned only).</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Click data.</param>
    private void ConfirmClear_Click(object sender, RoutedEventArgs e)
    {
        ClearFlyout.Hide();
        _ = controller.History.ClearAsync(includePinned: false);
    }

    /// <summary>Opens settings (and hides the flyout).</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Click data.</param>
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Dismiss(restoreFocus: false);
        controller.ShowSettings();
    }

    /// <summary>Tracks our own popups so their activation does not dismiss the flyout.</summary>
    /// <param name="sender">Popup.</param>
    /// <param name="e">Event data.</param>
    private void Popup_Opened(object? sender, object e) => openPopups++;

    /// <summary>Tracks our own popups closing.</summary>
    /// <param name="sender">Popup.</param>
    /// <param name="e">Event data.</param>
    private void Popup_Closed(object? sender, object e)
    {
        openPopups = Math.Max(0, openPopups - 1);
        SearchBox.Focus(FocusState.Programmatic);
    }

    /// <summary>Shows the per-item context menu.</summary>
    /// <param name="item">The card.</param>
    /// <param name="target">Placement target.</param>
    /// <param name="position">Position relative to <paramref name="target"/>.</param>
    private void ShowItemMenu(ClipItemViewModel item, FrameworkElement target, global::Windows.Foundation.Point position)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("Paste", "\uE77F", "Enter", () => _ = controller.PasteAsync(item.Entry, plainText: false)));
        if (item.Entry.HasRichFormats || item.Kind == ClipKind.Files)
        {
            menu.Items.Add(MenuItem(item.Kind == ClipKind.Files ? "Paste paths as text" : "Paste as plain text", "\uE8D2", "Shift+Enter",
                () => _ = controller.PasteAsync(item.Entry, plainText: true)));
        }

        menu.Items.Add(MenuItem("Copy only", "\uE8C8", null, () => _ = controller.PasteAsync(item.Entry, plainText: false, paste: false)));
        menu.Items.Add(MenuItem(item.IsPinned ? "Unpin" : "Pin", item.IsPinned ? "\uE77A" : "\uE718", "Ctrl+P",
            () => _ = controller.History.SetPinnedAsync(item.Id, !item.IsPinned)));

        if (item.Kind == ClipKind.Link && LinkDetector.TryGetLink(item.Preview, out var uri))
        {
            menu.Items.Add(MenuItem("Open link", "\uE8A7", null, () => { Dismiss(false); _ = Launcher.LaunchUriAsync(uri!); }));
        }
        else if (item.Kind == ClipKind.Files)
        {
            menu.Items.Add(MenuItem("Show in Explorer", "\uE8B7", null, () => { Dismiss(false); _ = RevealFilesAsync(item); }));
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        // In a group view the most likely wish is "not in here anymore": one click, no submenu.
        if (ViewModel.SelectedGroup is { } viewed && item.IsInGroup(viewed.Id))
        {
            menu.Items.Add(MenuItem($"Remove from {viewed.Name}", "\uE738", null, () => _ = RemoveFromGroupAsync(item, viewed)));
        }

        menu.Items.Add(BuildGroupsSubMenu(item));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("Delete", "\uE74D", "Del", () => DeleteItem(item)));

        menu.Opened += Popup_Opened;
        menu.Closed += Popup_Closed;
        menu.ShowAt(target, new FlyoutShowOptions { Position = position });
    }

    /// <summary>Builds a menu item with glyph and accelerator hint.</summary>
    /// <param name="text">Label.</param>
    /// <param name="glyph">Fluent glyph.</param>
    /// <param name="accelerator">Displayed shortcut text (handled by the key model, not the menu).</param>
    /// <param name="action">Click action.</param>
    /// <returns>The item.</returns>
    private static MenuFlyoutItem MenuItem(string text, string glyph, string? accelerator, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        if (accelerator is not null)
        {
            item.KeyboardAcceleratorTextOverride = accelerator;
        }

        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Deletes an item, keeping the selection on its neighbor.</summary>
    /// <param name="item">The card.</param>
    private void DeleteItem(ClipItemViewModel item)
    {
        int index = ViewModel.Items.IndexOf(item);
        _ = controller.History.DeleteAsync(item.Id);
        if (index >= 0 && ViewModel.Items.Count > 1)
        {
            SelectIndex(Math.Min(index + 1, ViewModel.Items.Count - 1) == index ? index - 1 : index + 1);
        }
    }

    /// <summary>Opens Explorer with the first file of a file-list entry selected.</summary>
    /// <param name="item">The card.</param>
    /// <returns>A task completing when Explorer was launched.</returns>
    private async Task RevealFilesAsync(ClipItemViewModel item)
    {
        var formats = await controller.History.GetFormatsAsync(item.Id);
        var drop = formats.FirstOrDefault(f => f.Name == ClipFormatNames.HDrop);
        var first = drop is null ? null : DropFilesCodec.Decode(drop.Data).FirstOrDefault();
        if (first is not null)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{first}\"") { UseShellExecute = true })?.Dispose();
        }
    }

    /// <summary>The selected card, if any.</summary>
    private ClipItemViewModel? Selected => ItemsList.SelectedItem as ClipItemViewModel;

    /// <summary>Moves the selection by <paramref name="delta"/> rows, clamped.</summary>
    /// <param name="delta">Rows to move.</param>
    private void MoveSelection(int delta)
    {
        if (ViewModel.Items.Count == 0)
        {
            return;
        }

        int current = ItemsList.SelectedIndex < 0 ? -1 : ItemsList.SelectedIndex;
        SelectIndex(Math.Clamp(current + delta, 0, ViewModel.Items.Count - 1));
    }

    /// <summary>Selects a row and scrolls it into view.</summary>
    /// <param name="index">Row index.</param>
    private void SelectIndex(int index)
    {
        if (index < 0 || index >= ViewModel.Items.Count)
        {
            return;
        }

        ItemsList.SelectedIndex = index;
        ItemsList.ScrollIntoView(ViewModel.Items[index]);
    }

    /// <summary>
    /// The "Groups" submenu of a card: one toggle per group (checked = the card is in it), so a card can join
    /// or leave any group without dragging — the keyboard and screen-reader route.
    /// </summary>
    /// <param name="item">The card.</param>
    /// <returns>The submenu.</returns>
    private MenuFlyoutSubItem BuildGroupsSubMenu(ClipItemViewModel item)
    {
        var submenu = new MenuFlyoutSubItem { Text = "Groups", Icon = new FontIcon { Glyph = "\uE8EC" } };
        if (ViewModel.Groups.Count == 0)
        {
            submenu.Items.Add(new MenuFlyoutItem { Text = "No groups yet: open the groups column (Ctrl+G) and press +", IsEnabled = false });
            return submenu;
        }

        foreach (var group in ViewModel.Groups)
        {
            var toggle = new ToggleMenuFlyoutItem { Text = group.Name, IsChecked = item.IsInGroup(group.Id), Icon = new FontIcon { Glyph = group.Glyph } };

            // IsChecked has already flipped when Click arrives: it is the requested state.
            toggle.Click += (_, _) => _ = toggle.IsChecked ? AddToGroupAsync([item.Id], group) : RemoveFromGroupAsync(item, group);
            submenu.Items.Add(toggle);
        }

        return submenu;
    }

    /// <summary>Groups were created, renamed, re-iconed, deleted or changed membership (UI thread): reload the column.</summary>
    /// <param name="sender">Controller.</param>
    /// <param name="e">Unused.</param>
    private void OnGroupsChanged(object? sender, EventArgs e) => _ = ViewModel.LoadGroupsAsync();

    /// <summary>The bookmark button: open or close the groups column.</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Click data.</param>
    private void GroupsToggle_Click(object sender, RoutedEventArgs e) => SetGroupsPane(!groupsPaneOpen);

    /// <summary>The logo (the column's top icon): back to the regular view.</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Click data.</param>
    private void LogoButton_Click(object sender, RoutedEventArgs e) => SelectGroup(null);

    /// <summary>"+" at the bottom of the column: pick an icon for a new group.</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Click data.</param>
    private void AddGroupButton_Click(object sender, RoutedEventArgs e) => ShowIconPicker(AddGroupButton, existing: null);

    /// <summary>
    /// Opens or closes the groups column: layout, window size (the window grows or shrinks on its left
    /// side), the header slide, and the remembered setting. Closing it also leaves a group view — a view
    /// whose group icon is hidden would be a trap.
    /// </summary>
    /// <param name="open">Desired state.</param>
    private void SetGroupsPane(bool open)
    {
        if (open == groupsPaneOpen)
        {
            return;
        }

        groupsPaneOpen = open;
        controller.Settings.Update(s => s with { ShowGroupsPane = open });
        if (!open && ViewModel.SelectedGroupId is not null)
        {
            ViewModel.SelectedGroupId = null;
        }

        int windowShift = 0;
        if (IsOpen)
        {
            var position = AppWindow.Position;
            var size = AppWindow.Size;
            var current = new ScreenRect(position.X, position.Y, position.X + size.Width, position.Y + size.Height);
            int extra = (int)Math.Round(GroupsPaneDip * Scale);
            var next = open
                ? FlyoutPositioner.ExtendLeft(current, extra, MonitorLookup.FromWindow(hwnd).WorkArea)
                : FlyoutPositioner.ShrinkLeft(current, extra);
            AppWindow.MoveAndResize(new RectInt32(next.Left, next.Top, next.Width, next.Height));
            windowShift = next.Left - current.Left;
        }

        ApplyGroupsPaneLayout();
        if (IsOpen)
        {
            PlayGroupsPaneAnimation(open, windowShift / Scale);
        }

        SearchBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Lays the flyout out for the current <see cref="groupsPaneOpen"/> (no window resize): column width, the
    /// logo's width (24 → 36 so it tops the icon column), card dragging, and the toggle's look.
    /// </summary>
    private void ApplyGroupsPaneLayout()
    {
        GroupsColumn.Width = new GridLength(groupsPaneOpen ? GroupsPaneDip : 0);
        GroupsPane.Visibility = groupsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        LogoButton.Width = groupsPaneOpen ? LogoOpenDip : LogoClosedDip;

        // Dragging a card only makes sense when there is somewhere to drop it.
        ItemsList.CanDragItems = groupsPaneOpen;
        GroupsToggle.Style = (Style)Application.Current.Resources[groupsPaneOpen ? "IconButtonActiveStyle" : "IconButtonStyle"];
        RibbonOutline.Visibility = groupsPaneOpen ? Visibility.Collapsed : Visibility.Visible;
        RibbonFilled.Visibility = groupsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(GroupsToggle, groupsPaneOpen ? "Hide groups" : "Show groups");
        UpdateGroupSelectionVisuals();
    }

    /// <summary>
    /// Makes the column change look continuous. The window jumped by <paramref name="windowShiftDip"/>, and
    /// inside it the logo and title moved right by 6 and 12 DIP (the logo cell grew from 24 to 36). Both
    /// start where they were on screen and glide to their new place; the icons fade in.
    /// </summary>
    /// <param name="open">Whether the column just opened.</param>
    /// <param name="windowShiftDip">How far the window's left edge moved, in DIPs (negative = left).</param>
    private void PlayGroupsPaneAnimation(bool open, double windowShiftDip)
    {
        double cellShift = open ? (LogoOpenDip - LogoClosedDip) / 2 : -(LogoOpenDip - LogoClosedDip) / 2;
        double titleShift = open ? LogoOpenDip - LogoClosedDip : -(LogoOpenDip - LogoClosedDip);
        Slide(LogoButton, -(windowShiftDip + cellShift));
        Slide(TitlePanel, -(windowShiftDip + titleShift));
        if (open)
        {
            FadeIn(GroupsPane);
        }

        void Slide(UIElement element, double fromX)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;
            var slide = compositor.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(0f, new Vector3((float)fromX, 0, 0));
            slide.InsertKeyFrame(1f, Vector3.Zero, compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
            slide.Duration = TimeSpan.FromMilliseconds(200);
            visual.StartAnimation("Translation", slide);
        }

        static void FadeIn(UIElement element)
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f);
            fade.Duration = TimeSpan.FromMilliseconds(180);
            visual.StartAnimation("Opacity", fade);
        }
    }

    /// <summary>Switches the list to a group (or the regular view) and gives typing back to the search box.</summary>
    /// <param name="groupId">Group id, or <see langword="null"/> for everything.</param>
    private void SelectGroup(long? groupId)
    {
        ViewModel.SelectedGroupId = groupId;
        SearchBox.Focus(FocusState.Programmatic);
    }

    /// <summary>Recreates the column's icons from <see cref="FlyoutViewModel.Groups"/>.</summary>
    private void RebuildGroupButtons()
    {
        GroupButtons.Children.Clear();
        foreach (var group in ViewModel.Groups)
        {
            GroupButtons.Children.Add(CreateGroupButton(group));
        }

        UpdateGroupSelectionVisuals();
    }

    /// <summary>
    /// One group icon: click shows the group (click again: back to everything), right-click opens its menu,
    /// and it accepts dropped cards.
    /// </summary>
    /// <param name="group">The group.</param>
    /// <returns>The button (its <see cref="FrameworkElement.Tag"/> is the group id).</returns>
    private Button CreateGroupButton(ClipGroup group)
    {
        var button = new Button
        {
            Style = GroupStyle(selected: false),
            Content = group.Glyph,
            Tag = group.Id,
            AllowDrop = true,
        };
        string count = group.ItemCount == 1 ? "1 item" : $"{group.ItemCount:N0} items";
        ToolTipService.SetToolTip(button, $"{group.Name} · {count}");
        AutomationProperties.SetName(button, $"{group.Name} group, {count}");
        button.Click += (_, _) => SelectGroup(ViewModel.SelectedGroupId == group.Id ? null : group.Id);
        button.RightTapped += (_, e) =>
        {
            ShowGroupMenu(group, button, e.GetPosition(button));
            e.Handled = true;
        };
        button.DragEnter += (_, e) => AcceptCardDrag(button, group, e);
        button.DragOver += (_, e) => AcceptCardDrag(button, group, e);
        button.DragLeave += (_, _) => SetDropHighlight(button, on: false);
        button.Drop += (_, e) => _ = DropCardsAsync(button, group, e);
        return button;
    }

    /// <summary>Highlights the regular-view logo or the selected group icon (only while the column is open).</summary>
    private void UpdateGroupSelectionVisuals()
    {
        long? selected = ViewModel.SelectedGroupId;
        foreach (var button in GroupButtons.Children.OfType<Button>())
        {
            button.Style = GroupStyle(selected: button.Tag is long id && id == selected);
        }

        // Style resets Width: set it again right after, the logo's width depends on the column state.
        LogoButton.Style = GroupStyle(selected: groupsPaneOpen && selected is null);
        LogoButton.Width = groupsPaneOpen ? LogoOpenDip : LogoClosedDip;
        LogoButton.Height = 32;
    }

    /// <summary>The group-icon style for a state (see App.xaml: whole styles, so brushes follow the flyout's theme).</summary>
    /// <param name="selected">Selected look.</param>
    /// <returns>The style.</returns>
    private static Style GroupStyle(bool selected) =>
        (Style)Application.Current.Resources[selected ? "GroupButtonSelectedStyle" : "GroupButtonStyle"];

    /// <summary>Shows or clears the drop highlight of a group icon.</summary>
    /// <param name="button">The icon.</param>
    /// <param name="on">Highlight on.</param>
    private void SetDropHighlight(Button button, bool on)
    {
        bool selected = button.Tag is long id && id == ViewModel.SelectedGroupId;
        button.Style = on ? (Style)Application.Current.Resources["GroupButtonDropTargetStyle"] : GroupStyle(selected);
    }

    /// <summary>A card drag starts: remember its entries and mark the data as ours.</summary>
    /// <param name="sender">List.</param>
    /// <param name="e">Drag data.</param>
    private void ItemsList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var ids = e.Items.OfType<ClipItemViewModel>().Select(i => i.Id).ToArray();
        if (!groupsPaneOpen || ids.Length == 0)
        {
            e.Cancel = true;
            return;
        }

        draggedClipIds = ids;
        e.Data.SetData(ClipIdsFormat, string.Join(",", ids));
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        AppLog.Info($"Card drag started ({ids.Length} card(s)).");
    }

    /// <summary>The card drag ended (dropped anywhere, or cancelled).</summary>
    /// <param name="sender">List.</param>
    /// <param name="args">Drag result.</param>
    private void ItemsList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => draggedClipIds = null;

    /// <summary>A drag hovers a group icon: accept our cards ("Add to Work"), refuse anything else.</summary>
    /// <param name="button">The icon.</param>
    /// <param name="group">Its group.</param>
    /// <param name="e">Drag data.</param>
    private void AcceptCardDrag(Button button, ClipGroup group, DragEventArgs e)
    {
        if (draggedClipIds is { Length: > 0 } && e.DataView.Contains(ClipIdsFormat))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = $"Add to {group.Name}";
            SetDropHighlight(button, on: true);
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }

        e.Handled = true;
    }

    /// <summary>Cards were dropped on a group icon: add them to the group.</summary>
    /// <param name="button">The icon.</param>
    /// <param name="group">Its group.</param>
    /// <param name="e">Drop data.</param>
    /// <returns>A task completing when stored (failures logged).</returns>
    private async Task DropCardsAsync(Button button, ClipGroup group, DragEventArgs e)
    {
        // Copy before awaiting: DragItemsCompleted clears the field as soon as this handler yields.
        var ids = draggedClipIds;
        SetDropHighlight(button, on: false);
        e.Handled = true;
        if (ids is { Length: > 0 })
        {
            // Counts only: the log never names cards (their text is clipboard content).
            AppLog.Info($"Dropped {ids.Length} card(s) on group {group.Id}.");
            await AddToGroupAsync(ids, group);
        }
    }

    /// <summary>Adds entries to a group (logged on failure; the worker announces the change).</summary>
    /// <param name="clipIds">Entry ids.</param>
    /// <param name="group">The group.</param>
    /// <returns>A task completing when stored.</returns>
    private async Task AddToGroupAsync(IReadOnlyList<long> clipIds, ClipGroup group)
    {
        try
        {
            await controller.History.AddToGroupAsync(clipIds, group.Id);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Adding to group {group.Id} failed: {ex.Message}");
        }
    }

    /// <summary>Takes a card out of a group; it re-enters retention with a fresh clock if that was its last group.</summary>
    /// <param name="item">The card.</param>
    /// <param name="group">The group.</param>
    /// <returns>A task completing when stored.</returns>
    private async Task RemoveFromGroupAsync(ClipItemViewModel item, ClipGroup group)
    {
        try
        {
            await controller.History.RemoveFromGroupAsync(item.Id, group.Id);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Removing entry {item.Id} from group {group.Id} failed: {ex.Message}");
        }
    }

    /// <summary>The right-click menu of a group icon: rename, change icon, delete.</summary>
    /// <param name="group">The group.</param>
    /// <param name="button">Its icon (placement target).</param>
    /// <param name="position">Position relative to <paramref name="button"/>.</param>
    private void ShowGroupMenu(ClipGroup group, Button button, global::Windows.Foundation.Point position)
    {
        var menu = new MenuFlyout();

        // The follow-up flyouts open after the menu has closed (queued), never on top of it.
        menu.Items.Add(MenuItem("Rename…", "\uE8AC", null, () => DispatcherQueue.TryEnqueue(() => ShowRenameFlyout(group, button))));
        menu.Items.Add(MenuItem("Change icon…", "\uE790", null, () => DispatcherQueue.TryEnqueue(() => ShowIconPicker(button, group))));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("Delete group", "\uE74D", null, () => DispatcherQueue.TryEnqueue(() => ShowDeleteGroupFlyout(group, button))));
        menu.Opened += Popup_Opened;
        menu.Closed += Popup_Closed;
        menu.ShowAt(button, new FlyoutShowOptions { Position = position });
    }

    /// <summary>
    /// The icon picker: a grid of <see cref="GroupIconCatalog"/> icons. For a new group it also asks for an
    /// optional name (default: the icon's name). Picking an icon creates the group or changes its icon at once.
    /// </summary>
    /// <param name="anchor">Placement target.</param>
    /// <param name="existing">The group whose icon changes, or <see langword="null"/> to create one.</param>
    private void ShowIconPicker(FrameworkElement anchor, ClipGroup? existing)
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.RightEdgeAlignedBottom };
        var panel = new StackPanel { Width = 280, Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = existing is null ? "New group" : $"Icon for {existing.Name}",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });

        TextBox? nameBox = null;
        if (existing is null)
        {
            nameBox = new TextBox { PlaceholderText = "Name (optional)", MaxLength = ClipGroup.MaxNameLength };
            AutomationProperties.SetName(nameBox, "Group name");
            panel.Children.Add(nameBox);
        }

        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 7, ItemWidth = 38, ItemHeight = 38 };
        foreach (var icon in GroupIconCatalog.All)
        {
            var choice = new Button { Style = GroupStyle(selected: existing?.Glyph == icon.Glyph), Content = icon.Glyph };
            ToolTipService.SetToolTip(choice, icon.Name);
            AutomationProperties.SetName(choice, icon.Name);
            choice.Click += (_, _) =>
            {
                flyout.Hide();
                _ = PickIconAsync(existing, icon, nameBox?.Text);
            };
            grid.Children.Add(choice);
        }

        panel.Children.Add(new ScrollViewer { Content = grid, MaxHeight = 228, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        if (existing is null)
        {
            panel.Children.Add(new TextBlock { Text = "Then drag cards onto the new icon to add them.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        }

        flyout.Content = panel;
        flyout.Opened += (s, e) =>
        {
            Popup_Opened(s, e);
            nameBox?.Focus(FocusState.Programmatic);
        };
        flyout.Closed += Popup_Closed;
        flyout.ShowAt(anchor);
    }

    /// <summary>Creates a group with the picked icon, or gives an existing one the new icon.</summary>
    /// <param name="existing">The group to change, or <see langword="null"/> to create.</param>
    /// <param name="icon">The picked icon.</param>
    /// <param name="typedName">The name typed for a new group (blank = the icon's name).</param>
    /// <returns>A task completing when stored (failures logged).</returns>
    private async Task PickIconAsync(ClipGroup? existing, GroupIcon icon, string? typedName)
    {
        try
        {
            if (existing is null)
            {
                string name = string.IsNullOrWhiteSpace(typedName) ? icon.Name : typedName.Trim();
                await controller.History.CreateGroupAsync(name, icon.Glyph);
            }
            else if (existing.Glyph != icon.Glyph)
            {
                await controller.History.UpdateGroupAsync(existing.Id, name: null, glyph: icon.Glyph);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Saving a group icon failed: {ex.Message}");
        }
    }

    /// <summary>Small flyout to rename a group (Enter or the button saves; Esc cancels).</summary>
    /// <param name="group">The group.</param>
    /// <param name="anchor">Placement target.</param>
    private void ShowRenameFlyout(ClipGroup group, FrameworkElement anchor)
    {
        var box = new TextBox { Text = group.Name, MaxLength = ClipGroup.MaxNameLength, Width = 220 };
        AutomationProperties.SetName(box, "Group name");
        var save = new Button { Content = "Rename", HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "Rename group", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        panel.Children.Add(box);
        panel.Children.Add(save);
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.RightEdgeAlignedTop };

        void Commit()
        {
            var name = box.Text.Trim();
            flyout.Hide();
            if (name.Length > 0 && name != group.Name)
            {
                _ = RenameGroupAsync(group, name);
            }
        }

        save.Click += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                Commit();
            }
        };
        flyout.Opened += (s, e) =>
        {
            Popup_Opened(s, e);
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        flyout.Closed += Popup_Closed;
        flyout.ShowAt(anchor);
    }

    /// <summary>Stores a new group name (failures logged).</summary>
    /// <param name="group">The group.</param>
    /// <param name="name">New name.</param>
    /// <returns>A task completing when stored.</returns>
    private async Task RenameGroupAsync(ClipGroup group, string name)
    {
        try
        {
            await controller.History.UpdateGroupAsync(group.Id, name, glyph: null);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Renaming group {group.Id} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Confirms deleting a group. Its items stay in the history, but those in no other group lose their
    /// protection (they re-enter retention from now) — worth one confirmation click.
    /// </summary>
    /// <param name="group">The group.</param>
    /// <param name="anchor">Placement target.</param>
    private void ShowDeleteGroupFlyout(ClipGroup group, FrameworkElement anchor)
    {
        var delete = new Button { Content = "Delete group", HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var panel = new StackPanel { MaxWidth = 260, Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = $"Delete “{group.Name}”?", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = group.ItemCount == 0
                ? "The group is empty."
                : "Its items stay in your history. Those in no other group are no longer kept like pinned items.",
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(delete);
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.RightEdgeAlignedTop };
        delete.Click += (_, _) =>
        {
            flyout.Hide();
            _ = DeleteGroupAsync(group);
        };
        flyout.Opened += Popup_Opened;
        flyout.Closed += Popup_Closed;
        flyout.ShowAt(anchor);
    }

    /// <summary>Deletes a group (failures logged); a deleted selected group falls back to the regular view.</summary>
    /// <param name="group">The group.</param>
    /// <returns>A task completing when stored.</returns>
    private async Task DeleteGroupAsync(ClipGroup group)
    {
        try
        {
            if (ViewModel.SelectedGroupId == group.Id)
            {
                ViewModel.SelectedGroupId = null;
            }

            await controller.History.DeleteGroupAsync(group.Id);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Deleting group {group.Id} failed: {ex.Message}");
        }
    }

    /// <summary>ShareX was found or lost (UI thread): show or hide its tab.</summary>
    /// <param name="sender">Controller.</param>
    /// <param name="e">Unused.</param>
    private void OnShareXStatusChanged(object? sender, EventArgs e) => UpdateShareXTab();

    /// <summary>
    /// Shows the ShareX tab only while ShareX is installed. If it disappears while selected, falls back to
    /// "All" so the list never stays filtered by an invisible tab.
    /// </summary>
    private void UpdateShareXTab()
    {
        bool installed = controller.ShareX.IsInstalled;
        ShareXFilter.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        if (!installed && ViewModel.Filter == ClipFilter.ShareX)
        {
            SelectFilter(ClipFilter.All);
        }
    }

    /// <summary>Selects a filter pill without raising a reload (used on show).</summary>
    /// <param name="filter">The filter.</param>
    private void SelectFilter(ClipFilter filter)
    {
        foreach (var item in Filters.Items)
        {
            if (item.Tag is string tag && tag == filter.ToString())
            {
                Filters.SelectedItem = item;
            }
        }
    }

    /// <summary>Finds the list's scroll viewer and loads more items near the end.</summary>
    private void HookScrollViewer()
    {
        if (scrollViewer is not null)
        {
            return;
        }

        scrollViewer = FindDescendant<ScrollViewer>(ItemsList);
        if (scrollViewer is not null)
        {
            scrollViewer.ViewChanged += (_, _) =>
            {
                if (scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 300)
                {
                    _ = ViewModel.LoadMoreAsync();
                }
            };
        }
    }

    /// <summary>Depth-first search of the visual tree.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="root">Where to start.</param>
    /// <returns>The first descendant of type <typeparamref name="T"/>, or <see langword="null"/>.</returns>
    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>Whether a key is down on the UI thread.</summary>
    /// <param name="key">Key.</param>
    /// <returns><see langword="true"/> when down.</returns>
    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>x:Bind helper: paused chip visibility.</summary>
    /// <param name="paused">Paused state.</param>
    /// <returns>Visibility.</returns>
    public Visibility PausedVisibility(bool paused) => paused ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>x:Bind helper: empty-state visibility.</summary>
    /// <param name="empty">Whether the list is empty.</param>
    /// <returns>Visibility.</returns>
    public Visibility EmptyVisibility(bool empty) => empty ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>x:Bind helper: visible only with text (the header's group name).</summary>
    /// <param name="text">The text.</param>
    /// <returns>Visibility.</returns>
    public Visibility TextVisibility(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>x:Bind helper: pause button glyph (Play when paused, Pause otherwise).</summary>
    /// <param name="paused">Paused state.</param>
    /// <returns>The glyph.</returns>
    public string PauseGlyph(bool paused) => paused ? "\uE768" : "\uE769";

    /// <summary>x:Bind helper: pause button tooltip.</summary>
    /// <param name="paused">Paused state.</param>
    /// <returns>The tooltip.</returns>
    public string PauseTooltip(bool paused) => paused ? "Resume capturing" : "Pause capturing";
}
