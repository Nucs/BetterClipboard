using System.Diagnostics;
using System.Numerics;
using BetterClipboard.App.Interop;
using BetterClipboard.App.ViewModels;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;
using BetterClipboard.Windows.Input;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
/// </remarks>
public sealed partial class ClipboardFlyout : Window
{
    /// <summary>Flyout size in DIPs (Win+V is ~360×450; a little larger shows two more cards).</summary>
    private const double WidthDip = 400;

    /// <summary>Flyout height in DIPs.</summary>
    private const double HeightDip = 560;

    /// <summary>Gap between caret and flyout, in DIPs.</summary>
    private const double GapDip = 8;

    private readonly AppController controller;
    private readonly nint hwnd;
    private ScrollViewer? scrollViewer;
    private int openPopups;
    private bool closingForExit;
    private bool reloadPending;

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
        ItemsList.Loaded += (_, _) => HookScrollViewer();

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
            controller.ApplyTheme(Root);
            ViewModel.ResetForShow();
            SelectFilter(ClipFilter.All);

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
            var rect = new RectInt32(bounds.Left, bounds.Top, bounds.Width, bounds.Height);

            AppWindow.MoveAndResize(rect);
            AppWindow.Show(true);
            // Moving between monitors with different DPI may have rescaled the window on show; re-apply.
            AppWindow.MoveAndResize(rect);
            Activate();
            ForegroundHelper.Activate(hwnd);

            SearchBox.Focus(FocusState.Programmatic);
            PlayEntranceAnimation();

            await ViewModel.ReloadAsync();
            SelectIndex(0);
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

    /// <summary>x:Bind helper: pause button glyph (Play when paused, Pause otherwise).</summary>
    /// <param name="paused">Paused state.</param>
    /// <returns>The glyph.</returns>
    public string PauseGlyph(bool paused) => paused ? "\uE768" : "\uE769";

    /// <summary>x:Bind helper: pause button tooltip.</summary>
    /// <param name="paused">Paused state.</param>
    /// <returns>The tooltip.</returns>
    public string PauseTooltip(bool paused) => paused ? "Resume capturing" : "Pause capturing";
}
