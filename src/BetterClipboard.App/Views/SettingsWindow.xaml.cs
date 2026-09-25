using BetterClipboard.App.Interop;
using BetterClipboard.App.ViewModels;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Settings;
using BetterClipboard.Windows.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace BetterClipboard.App.Views;

/// <summary>
/// The main window: settings, status and maintenance actions. Created on demand and freed when closed
/// (closing it does not quit the app — BetterClipboard keeps running in the tray).
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private const double WidthDip = 940;
    private const double HeightDip = 880;

    private readonly AppController controller;
    private readonly nint hwnd;

    /// <summary>
    /// Creates the window (not yet shown; call <see cref="Present"/>).
    /// </summary>
    /// <param name="controller">App controller.</param>
    public SettingsWindow(AppController controller)
    {
        this.controller = controller;
        ViewModel = new SettingsViewModel(controller);
        InitializeComponent();
        hwnd = WindowNative.GetWindowHandle(this);

        Title = "BetterClipboard";
        AppWindow.SetIcon(AppController.IconPath);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        SystemBackdrop = new MicaBackdrop();
        controller.ApplyTheme(Root);
        SizeAndCenter();

        controller.HotkeyStatusChanged += OnHotkeyStatusChanged;
        controller.HistoryChanged += OnHistoryChanged;
        controller.CommandLineStatusChanged += OnCommandLineStatusChanged;
        controller.Settings.Changed += OnSettingsChanged;
        Closed += OnClosed;
    }

    /// <summary>View model bound by the XAML.</summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>Shows and focuses the window, refreshing live status.</summary>
    public void Present()
    {
        ViewModel.RefreshSystemStatus();
        ViewModel.RefreshHotkeyStatus();
        _ = ViewModel.RefreshStatsAsync();
        AppWindow.Show(true);
        Activate();
        ForegroundHelper.Activate(hwnd);

        // Otherwise XAML auto-focuses the first control (the shortcut box), which shows its text
        // selected and invites accidental edits of the global shortcut.
        Scroller.Focus(FocusState.Programmatic);
    }

    /// <summary>Sizes the window for the current monitor's DPI and centers it in the work area.</summary>
    private void SizeAndCenter()
    {
        double scale = WindowInterop.GetScale(hwnd);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = Math.Min((int)(WidthDip * scale), area.Width);
        int height = Math.Min((int)(HeightDip * scale), area.Height);
        AppWindow.MoveAndResize(new RectInt32(area.X + ((area.Width - width) / 2), area.Y + ((area.Height - height) / 2), width, height));
    }

    /// <summary>Unsubscribes from app-lifetime events so the closed window can be collected.</summary>
    /// <param name="sender">Window.</param>
    /// <param name="args">Close data.</param>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        controller.HotkeyStatusChanged -= OnHotkeyStatusChanged;
        controller.HistoryChanged -= OnHistoryChanged;
        controller.CommandLineStatusChanged -= OnCommandLineStatusChanged;
        controller.Settings.Changed -= OnSettingsChanged;
    }

    /// <summary>The bclip pipe started, stopped or failed: refresh its card.</summary>
    /// <param name="sender">Controller.</param>
    /// <param name="e">Event data.</param>
    private void OnCommandLineStatusChanged(object? sender, EventArgs e) => ViewModel.RefreshCommandLineStatus();

    /// <summary>"Add to PATH" on the command-line card.</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Event data.</param>
    private void AddCommandLineToPath_Click(object sender, RoutedEventArgs e) => ViewModel.AddCommandLineToPath();

    /// <summary>Shortcut re-applied.</summary>
    /// <param name="sender">Controller.</param>
    /// <param name="e">Event data.</param>
    private void OnHotkeyStatusChanged(object? sender, EventArgs e) => ViewModel.RefreshHotkeyStatus();

    /// <summary>History changed: refresh the counters.</summary>
    /// <param name="sender">Controller.</param>
    /// <param name="e">Change data.</param>
    private void OnHistoryChanged(object? sender, ClipChangedEventArgs e) => _ = ViewModel.RefreshStatsAsync();

    /// <summary>Settings changed elsewhere (tray, flyout): mirror them and re-apply the theme.</summary>
    /// <param name="sender">Store.</param>
    /// <param name="settings">New settings.</param>
    private void OnSettingsChanged(object? sender, AppSettings settings) => DispatcherQueue.TryEnqueue(() =>
    {
        ViewModel.Load(settings);
        controller.ApplyTheme(Root);
    });

    /// <summary>Release or restore Win+V in Explorer, after confirmation (restarts Explorer).</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Click data.</param>
    private async void ReleaseWinV_Click(object sender, RoutedEventArgs e)
    {
        bool release = !ViewModel.IsWinVReleased;
        var confirmed = await ConfirmAsync(
            release ? "Release Win+V from Explorer?" : "Give Win+V back to Explorer?",
            release
                ? "Explorer will stop registering Win+V, so BetterClipboard can own it without a keyboard hook. Explorer restarts now: the taskbar disappears for a moment and open File Explorer windows close."
                : "Explorer will register Win+V again (Windows' clipboard history panel). Explorer restarts now: the taskbar disappears for a moment and open File Explorer windows close.",
            release ? "Release and restart Explorer" : "Restore and restart Explorer");
        if (confirmed)
        {
            await ViewModel.SetWinVReleasedAsync(release);
        }
    }

    /// <summary>Apply the ignored-apps list.</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Click data.</param>
    private void ApplyIgnoredApps_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyIgnoredApps();

    /// <summary>Clear unpinned history.</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Click data.</param>
    private async void ClearUnpinned_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync("Clear clipboard history?", "All unpinned items are deleted permanently. Pinned items stay.", "Clear"))
        {
            await controller.History.ClearAsync(includePinned: false);
            await ViewModel.RefreshStatsAsync();
        }
    }

    /// <summary>Clear everything including pins.</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Click data.</param>
    private async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync("Delete everything?", "Every item — including pinned ones — is deleted permanently. This cannot be undone.", "Delete everything"))
        {
            await controller.History.ClearAsync(includePinned: true);
            await ViewModel.RefreshStatsAsync();
        }
    }

    /// <summary>Windows' own history toggle (only acts on user changes, not binding refreshes).</summary>
    /// <param name="sender">Toggle.</param>
    /// <param name="e">Event data.</param>
    private void WindowsHistory_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle && toggle.IsOn != ViewModel.IsWindowsHistoryEnabled)
        {
            ViewModel.SetWindowsHistory(toggle.IsOn);
        }
    }

    /// <summary>Run the Windows import.</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Click data.</param>
    private async void ImportNow_Click(object sender, RoutedEventArgs e) => await ViewModel.ImportNowAsync();

    /// <summary>Open the data folder.</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Click data.</param>
    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => controller.OpenDataFolder();

    /// <summary>Quit the app.</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Click data.</param>
    private void Exit_Click(object sender, RoutedEventArgs e) => _ = controller.ExitAsync();

    /// <summary>Shows a confirmation dialog.</summary>
    /// <param name="title">Title.</param>
    /// <param name="message">Body.</param>
    /// <param name="action">Primary button text.</param>
    /// <returns><see langword="true"/> when confirmed.</returns>
    private async Task<bool> ConfirmAsync(string title, string message, string action)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = Root.ActualTheme,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>x:Bind helper: capture status dot color.</summary>
    /// <param name="paused">Paused state.</param>
    /// <returns>Green when capturing, amber when paused.</returns>
    public Brush CaptureDotBrush(bool paused) =>
        (Brush)Application.Current.Resources[paused ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush"];

    /// <summary>x:Bind helper: capture status text.</summary>
    /// <param name="paused">Paused state.</param>
    /// <returns>The text.</returns>
    public string CaptureText(bool paused) => paused ? "Paused" : "Capturing";

    /// <summary>x:Bind helper: visible when the text is non-empty.</summary>
    /// <param name="text">Text.</param>
    /// <returns>Visibility.</returns>
    public Visibility HasText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>x:Bind helper: Explorer release button text.</summary>
    /// <param name="released">Whether Win+V is released.</param>
    /// <returns>The text.</returns>
    public string ReleaseWinVText(bool released) => released ? "Give back to Explorer…" : "Release…";

    /// <summary>x:Bind helper: boolean negation.</summary>
    /// <param name="value">Value.</param>
    /// <returns>The negation.</returns>
    public bool Not(bool value) => !value;
}
