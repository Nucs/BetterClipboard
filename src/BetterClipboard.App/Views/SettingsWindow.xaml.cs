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
    /// Records shortcuts pressed in the shortcut box; created when the box first gets focus, disposed with the window. Its
    /// keyboard hook exists only while the box listens (see <see cref="HotkeyRecorder"/>).
    /// </summary>
    private HotkeyRecorder? recorder;

    /// <summary>Whether this window is the active one (its <see cref="Window.Activated"/> state); the box records only then.</summary>
    private bool isActive;

    /// <summary>
    /// Whether the box should be listening right now — set at once on focus and activation changes, so a start that
    /// completes after the box already lost focus does not show the "Listening" hint.
    /// </summary>
    private bool wantListening;

    /// <summary>Set when the window closes: no recorder (and no keyboard hook) may be created after that.</summary>
    private bool closed;

    /// <summary>
    /// The update dialog of Settings › Updates (the same dialog as the panel's update button opens): the flyout of the
    /// card's second button.
    /// </summary>
    private readonly UpdateDialog updateDialog;

    /// <summary>Whether that button has the accent style right now (an update waits); kept so it is restyled only on a change.</summary>
    private bool updateDialogButtonAccent;

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
        controller.ShareXStatusChanged += OnShareXStatusChanged;
        controller.SnippingStatusChanged += OnSnippingStatusChanged;
        controller.EverythingStatusChanged += OnEverythingStatusChanged;
        controller.RunHistoryStatusChanged += OnRunHistoryStatusChanged;
        controller.ShellHistoryStatusChanged += OnShellHistoryStatusChanged;
        controller.PromptsStatusChanged += OnPromptsStatusChanged;
        controller.ForgottenChanged += OnForgottenChanged;
        controller.UpdateStatusChanged += OnUpdateStatusChanged;
        controller.Settings.Changed += OnSettingsChanged;
        Closed += OnClosed;
        Activated += OnActivated;
        BuildHotkeyPresetsMenu();

        // The update dialog opens under its button (over it when there is no room below), left edges aligned: the card's
        // buttons sit at the card's left.
        updateDialog = new UpdateDialog(controller, hwnd, alignRight: false);
        UpdateDialogButton.Flyout = updateDialog.Flyout;
        ApplyUpdateDialogButton();
    }

    /// <summary>View model bound by the XAML.</summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>Shows and focuses the window, refreshing live status.</summary>
    public void Present()
    {
        ViewModel.RefreshSystemStatus();
        ViewModel.RefreshHotkeyStatus();
        ViewModel.RefreshShareXStatus();
        ViewModel.RefreshSnippingStatus();
        ViewModel.RefreshEverythingStatus();
        ViewModel.RefreshRunHistoryStatus();
        ViewModel.RefreshShellHistoryStatus();
        ViewModel.RefreshPromptsStatus();

        // "Checked 5 min ago" is worded when read: say it as of now, not as of the last change.
        ViewModel.RefreshUpdateStatus();
        ApplyUpdateDialogButton();

        // Also refreshes the stats line, which counts the forgotten items.
        _ = ViewModel.RefreshForgottenAsync();
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
        controller.ShareXStatusChanged -= OnShareXStatusChanged;
        controller.SnippingStatusChanged -= OnSnippingStatusChanged;
        controller.EverythingStatusChanged -= OnEverythingStatusChanged;
        controller.RunHistoryStatusChanged -= OnRunHistoryStatusChanged;
        controller.ShellHistoryStatusChanged -= OnShellHistoryStatusChanged;
        controller.PromptsStatusChanged -= OnPromptsStatusChanged;
        controller.ForgottenChanged -= OnForgottenChanged;
        controller.UpdateStatusChanged -= OnUpdateStatusChanged;
        controller.Settings.Changed -= OnSettingsChanged;
        Activated -= OnActivated;

        // Closing the dialog also ends its own subscription to the update status.
        updateDialog.Hide();

        // The recorder's hook must never outlive the box it records for.
        closed = true;
        wantListening = false;
        recorder?.Dispose();
        recorder = null;
    }

    /// <summary>
    /// The window was activated or deactivated: the shortcut box records only while its window is in front, so it stops on
    /// deactivation (another app's keys must never be taken) and starts again when the window comes back with the box focused.
    /// </summary>
    /// <param name="sender">This window.</param>
    /// <param name="args">The new activation state.</param>
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        isActive = args.WindowActivationState != WindowActivationState.Deactivated;
        if (isActive && HotkeyBox.FocusState != FocusState.Unfocused)
        {
            StartHotkeyListening();
        }
        else if (!isActive)
        {
            StopHotkeyListening();
        }
    }

    /// <summary>The shortcut box got focus: record shortcuts while the window is active.</summary>
    /// <param name="sender">The shortcut box.</param>
    /// <param name="e">Event data.</param>
    private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (isActive)
        {
            StartHotkeyListening();
        }
    }

    /// <summary>The shortcut box lost focus (Tab, a click elsewhere, the menu): stop recording.</summary>
    /// <param name="sender">The shortcut box.</param>
    /// <param name="e">Event data.</param>
    private void HotkeyBox_LostFocus(object sender, RoutedEventArgs e) => StopHotkeyListening();

    /// <summary>
    /// Starts the recorder (creating it on first use) and shows how to record. A hook Windows refuses leaves typing working
    /// and says so under the box.
    /// </summary>
    private async void StartHotkeyListening()
    {
        if (closed)
        {
            return; // a late focus event of a closing window must not hook the keyboard again
        }

        wantListening = true;
        try
        {
            recorder ??= CreateRecorder();
            await recorder.StartAsync();
            if (wantListening)
            {
                ViewModel.BeginHotkeyListening();
            }
            else
            {
                // Focus or activation went away while the hook was being installed: take it out again.
                await recorder.StopAsync();
            }
        }
        catch (Exception ex)
        {
            // async void's caller has nothing to catch it: a failure here must not crash the app.
            ViewModel.HotkeyListeningFailed(ex.Message);
        }
    }

    /// <summary>Stops the recorder (its hook goes once a just-recorded key is released) and hides the hint.</summary>
    private async void StopHotkeyListening()
    {
        wantListening = false;
        ViewModel.EndHotkeyListening();
        try
        {
            if (recorder is { } active)
            {
                await active.StopAsync();
            }
        }
        catch (Exception ex)
        {
            // async void's caller has nothing to catch it: a failure here must not crash the app.
            Core.Diagnostics.AppLog.Warn($"Stopping the shortcut recorder failed: {ex.Message}");
        }
    }

    /// <summary>Creates the recorder for this window and routes its recordings to the box, on the UI thread.</summary>
    /// <returns>The recorder (nothing hooked yet).</returns>
    private HotkeyRecorder CreateRecorder()
    {
        var created = new HotkeyRecorder(hwnd);
        created.Captured += (_, gesture) => DispatcherQueue.TryEnqueue(() =>
        {
            // A recording that arrives after the box lost focus is dropped: the hint is gone, so it would surprise.
            if (!wantListening)
            {
                return;
            }

            ViewModel.OnHotkeyRecorded(gesture);
            HotkeyBox.SelectionStart = HotkeyBox.Text.Length;
        });
        return created;
    }

    /// <summary>Something was forgotten, allowed again or kept out once more: rebuild the list.</summary>
    /// <param name="sender">Controller.</param>
    /// <param name="e">Event data.</param>
    private void OnForgottenChanged(object? sender, EventArgs e) => _ = ViewModel.RefreshForgottenAsync();

    /// <summary>"Allow again" on one row of Forgotten forever (no confirmation: it only restores the default, recording).</summary>
    /// <param name="sender">The row's button; its <c>Tag</c> holds the list entry id.</param>
    /// <param name="e">Click data.</param>
    private async void AllowAgain_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: long id })
        {
            await ViewModel.AllowAgainAsync(id);
        }
    }

    /// <summary>"Allow all again", after confirmation.</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Click data.</param>
    private async void AllowAllForgotten_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync(
                "Allow everything you forgot?",
                "All of it is recorded again from its next copy. Nothing that was deleted comes back.",
                "Allow all again"))
        {
            await ViewModel.AllowAllAgainAsync();
        }
    }

    /// <summary>ShareX found/lost, watch started/stopped, or a screenshot came in: refresh its card.</summary>
    /// <param name="sender">Controller.</param>
    /// <param name="e">Event data.</param>
    private void OnShareXStatusChanged(object? sender, EventArgs e) => ViewModel.RefreshShareXStatus();

    /// <summary>The Screenshots folder watch started or stopped, the folder or Snipping Tool changed, or a screenshot came in: refresh its card.</summary>
    /// <param name="sender">Controller.</param>
    /// <param name="e">Event data.</param>
    private void OnSnippingStatusChanged(object? sender, EventArgs e) => ViewModel.RefreshSnippingStatus();

    /// <summary>Everything started, stopped, finished loading or was found installed, or the tab was switched: refresh its card.</summary>
    /// <param name="sender">Controller.</param>
    /// <param name="e">Event data.</param>
    private void OnEverythingStatusChanged(object? sender, EventArgs e) => ViewModel.RefreshEverythingStatus();

    /// <summary>The Win+R watch started or stopped, Windows' list was read, or a run was recorded: refresh its card.</summary>
    /// <param name="sender">Controller.</param>
    /// <param name="e">Event data.</param>
    private void OnRunHistoryStatusChanged(object? sender, EventArgs e) => ViewModel.RefreshRunHistoryStatus();

    /// <summary>A shell tab was switched, PowerShell's file found or lost, or the Cmd tab kept new commands: refresh their cards.</summary>
    /// <param name="sender">Controller.</param>
    /// <param name="e">Event data.</param>
    private void OnShellHistoryStatusChanged(object? sender, EventArgs e) => ViewModel.RefreshShellHistoryStatus();

    /// <summary>A prompt archive started, stopped, imported or stored prompts: refresh both prompt cards.</summary>
    /// <param name="sender">Controller.</param>
    /// <param name="agent">The agent whose archive changed (both cards are cheap to refresh).</param>
    private void OnPromptsStatusChanged(object? sender, Core.Prompts.PromptAgent agent) => ViewModel.RefreshPromptsStatus();

    /// <summary>"Delete stored prompts…" on the Claude Code card, after confirmation.</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Click data.</param>
    private void DeleteClaudeCodePrompts_Click(object sender, RoutedEventArgs e) => _ = DeleteStoredPromptsAsync(Core.Prompts.PromptAgent.ClaudeCode);

    /// <summary>"Delete stored prompts…" on the Codex card, after confirmation.</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Click data.</param>
    private void DeleteCodexPrompts_Click(object sender, RoutedEventArgs e) => _ = DeleteStoredPromptsAsync(Core.Prompts.PromptAgent.Codex);

    /// <summary>
    /// Asks before deleting an agent's prompt archive — it cannot be undone, and prompts the agent has pruned meanwhile are
    /// gone for good — and says what happens next while the switch is on (what the agent still has is read again).
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <returns>A task completing when done (never throws).</returns>
    private async Task DeleteStoredPromptsAsync(Core.Prompts.PromptAgent agent)
    {
        var name = Core.Prompts.PromptAgents.NameOf(agent);
        long count = agent == Core.Prompts.PromptAgent.Codex ? ViewModel.CodexPromptCount : ViewModel.ClaudeCodePromptCount;
        bool on = agent == Core.Prompts.PromptAgent.Codex ? ViewModel.KeepCodexPrompts : ViewModel.KeepClaudeCodePrompts;
        try
        {
            if (await ConfirmAsync(
                    $"Delete {count:N0} stored {name} prompt{(count == 1 ? string.Empty : "s")}?",
                    $"BetterClipboard's copy is deleted; {name}'s own files are not changed. Prompts {name} no longer has are gone for good."
                    + (on ? $" The switch is on, so what {name} still has is read again right away — turn it off first to keep them out." : string.Empty),
                    "Delete"))
            {
                await ViewModel.DeleteStoredPromptsAsync(agent);
            }
        }
        catch (Exception ex)
        {
            // async void's caller has nothing to catch it: a failure here must not crash the app.
            Core.Diagnostics.AppLog.Warn($"Deleting the stored {name} prompts failed: {ex.Message}");
        }
    }

    /// <summary>
    /// A check started or ended, an update was found, a download moved on, or the update settings changed: refresh the
    /// Updates card. (The open update dialog follows the same event by itself.)
    /// </summary>
    /// <param name="sender">Controller.</param>
    /// <param name="e">Event data.</param>
    private void OnUpdateStatusChanged(object? sender, EventArgs e)
    {
        ViewModel.RefreshUpdateStatus();
        ApplyUpdateDialogButton();
    }

    /// <summary>
    /// Makes the button that opens the update dialog the accent button while an update waits, and a plain one otherwise.
    /// A whole style, so its colors resolve in the window's own theme.
    /// </summary>
    private void ApplyUpdateDialogButton()
    {
        bool accent = ViewModel.IsUpdateWaiting;
        if (accent != updateDialogButtonAccent || UpdateDialogButton.Style is null)
        {
            updateDialogButtonAccent = accent;
            UpdateDialogButton.Style = (Style)Application.Current.Resources[accent ? "AccentButtonStyle" : "DefaultButtonStyle"];
        }
    }

    /// <summary>"Check now" on the Updates card: asks GitHub once, whatever the automatic switch says.</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Event data.</param>
    private void CheckForUpdates_Click(object sender, RoutedEventArgs e) => _ = ViewModel.CheckForUpdatesNowAsync();

    /// <summary>The bclip pipe started, stopped or failed: refresh its card.</summary>
    /// <param name="sender">Controller.</param>
    /// <param name="e">Event data.</param>
    private void OnCommandLineStatusChanged(object? sender, EventArgs e) => ViewModel.RefreshCommandLineStatus();

    /// <summary>"Add to PATH" on the command-line card.</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Event data.</param>
    private void AddCommandLineToPath_Click(object sender, RoutedEventArgs e) => ViewModel.AddCommandLineToPath();

    /// <summary>
    /// Shortcuts re-applied: refresh the rows, and re-install the recorder's hook while the box listens — the shortcuts'
    /// takeover hook was just installed again, and the newest hook is called first, so without this Win+V pressed in the box
    /// would open the panel instead of being recorded.
    /// </summary>
    /// <param name="sender">Controller.</param>
    /// <param name="e">Event data.</param>
    private async void OnHotkeyStatusChanged(object? sender, EventArgs e)
    {
        ViewModel.RefreshHotkeyStatus();
        try
        {
            if (recorder is { IsListening: true } active)
            {
                await active.RestartAsync();
            }
        }
        catch (Exception ex)
        {
            // async void's caller has nothing to catch it; the old hook stays, so recording still works for most keys.
            Core.Diagnostics.AppLog.Warn($"Re-installing the shortcut recorder's hook failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Fills the menu next to the shortcut box (<see cref="MenuFlyout"/> has no ItemsSource): the presets, then "Used
    /// before" — the shortcuts of <see cref="Core.Settings.AppSettings.HotkeyHistory"/> that are not presets, newest first —
    /// as toggles. Picking one adds it through <see cref="SettingsViewModel.TogglePreset"/> (validated and saved exactly like
    /// typed text), or removes it when it is in the list already, which its check shows.
    /// </summary>
    /// <remarks>
    /// Rebuilt each time the menu opens, since the history changes with every added shortcut; also built once up front, so
    /// the menu is never empty when it is first asked to open.
    /// </remarks>
    private void BuildHotkeyPresetsMenu()
    {
        HotkeyPresetsMenu.Items.Clear();
        foreach (var preset in ViewModel.HotkeyPresets)
        {
            HotkeyPresetsMenu.Items.Add(ShortcutToggle(preset));
        }

        HotkeyPresetsMenu.Items.Add(new MenuFlyoutSeparator());

        // A disabled item serves as the section's heading: MenuFlyout has no header items.
        HotkeyPresetsMenu.Items.Add(new MenuFlyoutItem { Text = "Used before", IsEnabled = false });
        var usedBefore = ViewModel.UsedBeforeHotkeys;
        if (usedBefore.Count == 0)
        {
            HotkeyPresetsMenu.Items.Add(new MenuFlyoutItem { Text = "Shortcuts you add are listed here", IsEnabled = false });
            return;
        }

        foreach (var text in usedBefore)
        {
            HotkeyPresetsMenu.Items.Add(ShortcutToggle(text));
        }

        // No icon: one item with an icon makes the menu reserve an icon column for every item, shifting all the shortcuts.
        HotkeyPresetsMenu.Items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem { Text = "Clear shortcuts used before" };
        clear.Click += (_, _) => ViewModel.ClearHotkeyHistory();
        HotkeyPresetsMenu.Items.Add(clear);
    }

    /// <summary>One toggle of the menu: checked while the shortcut is in the list; a click adds or removes it.</summary>
    /// <param name="text">The shortcut, in canonical form.</param>
    /// <returns>The menu item.</returns>
    private ToggleMenuFlyoutItem ShortcutToggle(string text)
    {
        var item = new ToggleMenuFlyoutItem { Text = text, Tag = text, IsChecked = ViewModel.HasHotkey(text) };
        item.Click += (_, _) => ViewModel.TogglePreset(text);
        return item;
    }

    /// <summary>Rebuilds the menu right before it shows, so its "Used before" part and the checks are current.</summary>
    /// <param name="sender">The presets menu.</param>
    /// <param name="e">Event data.</param>
    private void HotkeyPresetsMenu_Opening(object sender, object e) => BuildHotkeyPresetsMenu();

    /// <summary>
    /// Enter adds the typed shortcut to the list (the box itself saves nothing, so a half-typed shortcut never becomes a
    /// global hotkey).
    /// </summary>
    /// <param name="sender">The shortcut box.</param>
    /// <param name="e">Key data.</param>
    private void HotkeyBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Enter)
        {
            ViewModel.AddHotkey(HotkeyBox.Text);
            e.Handled = true;
        }
    }

    /// <summary>The Add button next to the shortcut box: adds what is typed there.</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Click data.</param>
    private void AddHotkey_Click(object sender, RoutedEventArgs e) => ViewModel.AddHotkey(HotkeyBox.Text);

    /// <summary>Remove on a shortcut's row; its <c>Tag</c> holds the row's shortcut text.</summary>
    /// <param name="sender">The row's button.</param>
    /// <param name="e">Click data.</param>
    private void RemoveHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string text })
        {
            ViewModel.RemoveHotkey(text);
        }
    }

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

    /// <summary>
    /// Release the Win+letter shortcuts from Explorer, or give them back, after confirmation (restarts Explorer). The plan is
    /// read once, before the dialog, so the keys confirmed are exactly the keys changed.
    /// </summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Click data.</param>
    private async void ReleaseFromExplorer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.RefreshExplorerRelease();
            var plan = ViewModel.ExplorerPlan;
            if (plan.Action == BetterClipboard.Windows.Shell.ExplorerReleaseAction.None)
            {
                return;
            }

            bool release = plan.Action == BetterClipboard.Windows.Shell.ExplorerReleaseAction.Release;
            bool one = plan.Keys.Count == 1;
            var names = plan.KeyNames;
            const string Restart = "Explorer restarts now: the taskbar disappears for a moment and open File Explorer windows close.";
            var confirmed = await ConfirmAsync(
                release ? $"Release {names} from Explorer?" : $"Give {names} back to Explorer?",
                release
                    ? $"Explorer will stop registering {names}, so BetterClipboard can own {(one ? "it" : "them")} without a keyboard hook. While BetterClipboard is not running, {(one ? "it does" : "they do")} nothing. {Restart}"
                    : $"Explorer will register {names} again (Windows' own {(one ? "shortcut" : "shortcuts")}). {Restart}",
                release ? "Release and restart Explorer" : "Restore and restart Explorer");
            if (confirmed)
            {
                await ViewModel.ApplyExplorerReleaseAsync(plan);
            }
        }
        catch (Exception ex)
        {
            // async void's caller has nothing to catch it: a failure here must not crash the app.
            Core.Diagnostics.AppLog.Warn($"Changing Explorer's shortcut registrations failed: {ex.Message}");
        }
    }

    /// <summary>Apply the ignored-apps list.</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Click data.</param>
    private void ApplyIgnoredApps_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyIgnoredApps();

    /// <summary>Re-add every known password manager / authenticator to the ignored-apps list and save it.</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Click data.</param>
    private void AddKnownPasswordManagers_Click(object sender, RoutedEventArgs e) => ViewModel.AddKnownPasswordManagers();

    /// <summary>Clear history except pinned items and items in groups.</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Click data.</param>
    private async void ClearUnpinned_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync("Clear clipboard history?", "Items are deleted permanently. Pinned items and items in groups stay.", "Clear"))
        {
            await controller.History.ClearAsync(includePinned: false);
            await ViewModel.RefreshStatsAsync();
        }
    }

    /// <summary>Clear everything including pins and grouped items (the groups themselves stay, empty).</summary>
    /// <param name="sender">Button.</param>
    /// <param name="e">Click data.</param>
    private async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync("Delete everything?", "Every item — including pinned ones and those in groups — is deleted permanently. Your groups stay, empty. This cannot be undone.", "Delete everything"))
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

    /// <summary>x:Bind helper: boolean negation.</summary>
    /// <param name="value">Value.</param>
    /// <returns>The negation.</returns>
    public bool Not(bool value) => !value;

    /// <summary>x:Bind helper: visible when <paramref name="value"/> is true.</summary>
    /// <param name="value">Value.</param>
    /// <returns>Visibility.</returns>
    public Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
