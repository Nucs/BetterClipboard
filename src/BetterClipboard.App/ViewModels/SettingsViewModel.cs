using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Settings;
using BetterClipboard.Windows.Clipboard;
using BetterClipboard.Windows.Input;
using BetterClipboard.Windows.Shell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// Backs the settings window: two-way settings properties (each change is persisted immediately through
/// <see cref="Core.Settings.SettingsStore.Update"/>) plus read-only status for the shortcut, Windows'
/// own clipboard history, statistics and the import.
/// </summary>
/// <remarks>
/// UI thread only. While <see cref="Load"/> copies a snapshot into the properties, the generated
/// change hooks are muted (<c>loading</c>) so reading settings never writes them back.
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppController controller;
    private bool loading;

    /// <summary>
    /// Creates the view model and loads the current settings/status.
    /// </summary>
    /// <param name="controller">App controller.</param>
    public SettingsViewModel(AppController controller)
    {
        this.controller = controller;

        // Written in the saved (canonical) form: a preset labelled "Win+Alt+V" would come back as
        // "Alt+Win+V" once saved, so the box would change under the user right after picking it.
        HotkeyPresets = new[] { "Win+V", "Win+Alt+V", "Ctrl+Shift+V", "Ctrl+Alt+V", "Ctrl+`", "Alt+Insert" }
            .Select(preset => HotkeyGesture.TryParse(preset, out var gesture) ? gesture.ToString() : preset)
            .ToArray();
        Load(controller.Settings.Current);
        RefreshSystemStatus();
    }

    /// <summary>Suggested shortcuts for the presets menu next to the shortcut box, in canonical form.</summary>
    public IReadOnlyList<string> HotkeyPresets { get; }

    /// <summary>Version string for the About card.</summary>
    public string VersionText => $"Version {AppController.Version}";

    /// <summary>Data folder path.</summary>
    public string DataFolder => controller.Paths.DataDirectory;

    /// <summary>
    /// One-paragraph description of how the history is protected on disk (cipher, key sealing, store id,
    /// and a warning when an unreadable store was set aside at startup).
    /// </summary>
    public string EncryptionStatus
    {
        get
        {
            var info = controller.StorageInfo;
            if (info is null || !info.Store.IsEncrypted)
            {
                return "Not encrypted.";
            }

            var text = "On — every item, the search index and thumbnails are encrypted (ChaCha20-Poly1305). " +
                       $"The key is sealed to this PC and your Windows account. Store {info.StoreId}.";
            return info.QuarantinedDirectory is null
                ? text
                : text + $" A previous history that could not be decrypted here was set aside in '{Path.GetFileName(info.QuarantinedDirectory)}'.";
        }
    }

    // ───── Settings (persisted on change) ─────

    /// <summary>Shortcut text; only valid gestures are persisted.</summary>
    [ObservableProperty]
    public partial string OpenHotkey { get; set; } = string.Empty;

    /// <summary>See <see cref="AppSettings.UseKeyboardHookFallback"/>.</summary>
    [ObservableProperty]
    public partial bool UseKeyboardHookFallback { get; set; }

    /// <summary>See <see cref="AppSettings.MaxItems"/>.</summary>
    [ObservableProperty]
    public partial double MaxItems { get; set; }

    /// <summary>See <see cref="AppSettings.RetentionDays"/>.</summary>
    [ObservableProperty]
    public partial double RetentionDays { get; set; }

    /// <summary>See <see cref="AppSettings.MaxItemSizeMB"/>.</summary>
    [ObservableProperty]
    public partial double MaxItemSizeMB { get; set; }

    /// <summary>See <see cref="AppSettings.MaxTotalSizeMB"/>.</summary>
    [ObservableProperty]
    public partial double MaxTotalSizeMB { get; set; }

    /// <summary>See <see cref="AppSettings.CaptureImages"/>.</summary>
    [ObservableProperty]
    public partial bool CaptureImages { get; set; }

    /// <summary>See <see cref="AppSettings.CaptureFiles"/>.</summary>
    [ObservableProperty]
    public partial bool CaptureFiles { get; set; }

    /// <summary>See <see cref="AppSettings.PreserveAllFormats"/>.</summary>
    [ObservableProperty]
    public partial bool PreserveAllFormats { get; set; }

    /// <summary>See <see cref="AppSettings.PasteOnSelect"/>.</summary>
    [ObservableProperty]
    public partial bool PasteOnSelect { get; set; }

    /// <summary>See <see cref="AppSettings.MoveToTopOnPaste"/>.</summary>
    [ObservableProperty]
    public partial bool MoveToTopOnPaste { get; set; }

    /// <summary>See <see cref="AppSettings.PinnedOnTop"/>.</summary>
    [ObservableProperty]
    public partial bool PinnedOnTop { get; set; }

    /// <summary>See <see cref="AppSettings.ImportWindowsHistoryOnStartup"/>.</summary>
    [ObservableProperty]
    public partial bool ImportWindowsHistoryOnStartup { get; set; }

    /// <summary>See <see cref="AppSettings.IsCapturePaused"/>.</summary>
    [ObservableProperty]
    public partial bool IsCapturePaused { get; set; }

    /// <summary>Ignored apps, one per line or comma-separated; applied on <see cref="ApplyIgnoredApps"/>.</summary>
    [ObservableProperty]
    public partial string IgnoredAppsText { get; set; } = string.Empty;

    /// <summary>Index into Near caret / Near cursor / Center.</summary>
    [ObservableProperty]
    public partial int PlacementIndex { get; set; }

    /// <summary>Index into System / Light / Dark.</summary>
    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    /// <summary>Start with Windows (Run key; not stored in settings.json).</summary>
    [ObservableProperty]
    public partial bool LaunchAtStartup { get; set; }

    /// <summary>See <see cref="AppSettings.EnableCommandLine"/> (off by default).</summary>
    [ObservableProperty]
    public partial bool EnableCommandLine { get; set; }

    /// <summary>See <see cref="AppSettings.ImportShareXScreenshots"/> (on by default).</summary>
    [ObservableProperty]
    public partial bool ImportShareXScreenshots { get; set; }

    // ───── Status ─────

    /// <summary>How the shortcut is wired right now.</summary>
    [ObservableProperty]
    public partial string HotkeyStatus { get; set; } = string.Empty;

    /// <summary>Whether the shortcut is active (drives the status color).</summary>
    [ObservableProperty]
    public partial bool IsHotkeyActive { get; set; }

    /// <summary>"1,234 items · 56.1 MB · 12 pinned · 30 in groups", plus "· 2 forgotten" once anything is forgotten forever.</summary>
    [ObservableProperty]
    public partial string StatsText { get; set; } = "…";

    /// <summary>
    /// "Since start: 523 copies read · none missed …" — the listener's accounting, so a missed copy is
    /// visible instead of silent.
    /// </summary>
    [ObservableProperty]
    public partial string CaptureReliabilityText { get; set; } = "…";

    /// <summary>Last import result.</summary>
    [ObservableProperty]
    public partial string ImportStatus { get; set; } = string.Empty;

    /// <summary>Whether an import is running (disables the button).</summary>
    [ObservableProperty]
    public partial bool IsImporting { get; set; }

    /// <summary>Windows' own history toggle state (null when never configured).</summary>
    [ObservableProperty]
    public partial bool IsWindowsHistoryEnabled { get; set; }

    /// <summary>Human description of Windows' own history state.</summary>
    [ObservableProperty]
    public partial string WindowsHistoryStatus { get; set; } = string.Empty;

    /// <summary>Whether Explorer is told to release Win+V.</summary>
    [ObservableProperty]
    public partial bool IsWinVReleased { get; set; }

    /// <summary>Validation message for the shortcut box.</summary>
    [ObservableProperty]
    public partial string HotkeyError { get; set; } = string.Empty;

    /// <summary>Where bclip is, whether it is on PATH, and whether the pipe is being served (or why not).</summary>
    [ObservableProperty]
    public partial string CommandLineStatus { get; set; } = string.Empty;

    /// <summary>Whether "Add to PATH" makes sense (bclip.exe exists and its folder is not on PATH yet).</summary>
    [ObservableProperty]
    public partial bool CanAddCommandLineToPath { get; set; }

    /// <summary>Whether ShareX was found, how, which folders are watched, and how many screenshots came in.</summary>
    [ObservableProperty]
    public partial string ShareXStatus { get; set; } = string.Empty;

    /// <summary>
    /// The "Forget forever" list, most recently forgotten first (see <see cref="RefreshForgottenAsync"/>).
    /// Replaced wholesale on refresh; UI thread only.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<ForgottenItemViewModel> Forgotten { get; } = [];

    /// <summary>One line over the list: "Nothing is forgotten." or "3 items are never recorded.".</summary>
    [ObservableProperty]
    public partial string ForgottenSummary { get; set; } = string.Empty;

    /// <summary>Whether the list has entries (shows it, enables "Allow all again").</summary>
    [ObservableProperty]
    public partial bool HasForgotten { get; set; }

    /// <summary>
    /// Copies a settings snapshot into the properties without persisting anything.
    /// </summary>
    /// <param name="settings">The snapshot.</param>
    public void Load(AppSettings settings)
    {
        loading = true;
        try
        {
            OpenHotkey = settings.OpenHotkey;
            UseKeyboardHookFallback = settings.UseKeyboardHookFallback;
            MaxItems = settings.MaxItems;
            RetentionDays = settings.RetentionDays;
            MaxItemSizeMB = settings.MaxItemSizeMB;
            MaxTotalSizeMB = settings.MaxTotalSizeMB;
            CaptureImages = settings.CaptureImages;
            CaptureFiles = settings.CaptureFiles;
            PreserveAllFormats = settings.PreserveAllFormats;
            PasteOnSelect = settings.PasteOnSelect;
            MoveToTopOnPaste = settings.MoveToTopOnPaste;
            PinnedOnTop = settings.PinnedOnTop;
            ImportWindowsHistoryOnStartup = settings.ImportWindowsHistoryOnStartup;
            IsCapturePaused = settings.IsCapturePaused;
            IgnoredAppsText = string.Join(Environment.NewLine, settings.IgnoredApps);
            PlacementIndex = (int)settings.Placement;
            ThemeIndex = (int)settings.Theme;
            LaunchAtStartup = StartupRegistration.IsEnabled(AppController.ExecutablePath);
            EnableCommandLine = settings.EnableCommandLine;
            ImportShareXScreenshots = settings.ImportShareXScreenshots;
            RefreshHotkeyStatus();
            RefreshCommandLineStatus();
            RefreshShareXStatus();
        }
        finally
        {
            loading = false;
        }
    }

    /// <summary>Re-reads bclip's location, PATH state and whether the pipe is live.</summary>
    public void RefreshCommandLineStatus()
    {
        bool exists = File.Exists(AppController.CommandLinePath);
        bool onPath = exists && UserPath.Contains(AppController.CommandLineDirectory);
        CanAddCommandLineToPath = exists && !onPath;
        var where = exists
            ? $"{AppController.CommandLinePath}{(onPath ? " · on your PATH (new terminals run it as bclip)" : " · not on your PATH")}"
            : "bclip.exe is not next to the app (development build) — release builds include it.";
        var state = controller.CommandLineError is { } error ? $"Could not start: {error}"
            : controller.IsCommandLineActive ? "Serving bclip now."
            : "Off — bclip is refused.";
        CommandLineStatus = $"{state}\n{where}";
    }

    /// <summary>
    /// Re-reads the ShareX integration state. Folders are listed as watched only while really watched, so
    /// a folder that does not exist yet (ShareX creates it with the first screenshot) is visible as such.
    /// </summary>
    public void RefreshShareXStatus()
    {
        var shareX = controller.ShareX;
        if (!shareX.IsInstalled)
        {
            ShareXStatus = "ShareX was not found on this PC. Install it and this turns on by itself (checked when Settings opens and every few minutes).";
            return;
        }

        var found = shareX.ExecutablePath is { } exe ? $"ShareX found: {exe} (via {shareX.DetectedBy})." : $"ShareX found (via {shareX.DetectedBy}).";
        if (!ImportShareXScreenshots)
        {
            ShareXStatus = $"{found}\nOff — screenshots are not imported; copies made by ShareX are still recorded and shown in its tab.";
            return;
        }

        var watched = controller.ShareXWatchedFolders;
        var folders = watched.Count > 0
            ? $"Watching: {string.Join("; ", watched)}"
            : $"Waiting for ShareX to create its screenshots folder ({string.Join("; ", shareX.WatchFolders)}).";
        int imported = controller.ShareXImportedThisSession;
        var count = imported > 0 ? $"\n{imported:N0} screenshot{(imported == 1 ? string.Empty : "s")} added since BetterClipboard started." : string.Empty;
        ShareXStatus = $"{found}\n{folders}{count}";
    }

    /// <summary>Adds bclip's folder to the user PATH and refreshes the status text.</summary>
    public void AddCommandLineToPath()
    {
        try
        {
            controller.AddCommandLineToPath();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            AppLog.Warn($"Adding bclip to PATH failed: {ex.Message}");
        }

        RefreshCommandLineStatus();
    }

    /// <summary>Re-reads the shortcut registration state.</summary>
    public void RefreshHotkeyStatus()
    {
        var status = controller.HotkeyStatus;
        IsHotkeyActive = status.IsActive;
        HotkeyStatus = status.Gesture.VirtualKey == 0 ? "Registering…" : status.Describe();
    }

    /// <summary>Re-reads Windows-side state (clipboard history toggle, Explorer hotkey release).</summary>
    public void RefreshSystemStatus()
    {
        var enabled = WindowsClipboardSettings.IsWindowsHistoryEnabled();
        IsWindowsHistoryEnabled = enabled == true;
        WindowsHistoryStatus = WindowsClipboardSettings.IsWindowsHistoryBlockedByPolicy()
            ? "Disabled by your organization's policy."
            : enabled == true
                ? "On — Windows keeps its own 25-item copy of what you copy (and forgets it on restart)."
                : "Off — BetterClipboard is your only clipboard history. Win+V pins can still be imported.";
        IsWinVReleased = WindowsClipboardSettings.IsWinVReleasedByExplorer();
    }

    /// <summary>
    /// Refreshes the statistics text.
    /// </summary>
    /// <returns>A task completing when updated.</returns>
    public async Task RefreshStatsAsync()
    {
        try
        {
            var stats = await controller.History.GetStatsAsync();
            StatsText = $"{stats.Count:N0} items · {FormatBytes(stats.TotalBytes)} · {stats.PinnedCount:N0} pinned · {stats.GroupedCount:N0} in groups" +
                        (stats.ForgottenCount > 0 ? $" · {stats.ForgottenCount:N0} forgotten" : string.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Stats failed: {ex.Message}");
        }

        CaptureReliabilityText = DescribeCapture(controller.CaptureStatistics);
    }

    /// <summary>
    /// Re-reads the "Forget forever" list (rows, summary, and the stats line that counts it).
    /// </summary>
    /// <returns>A task completing when updated (failures are logged; the old rows stay).</returns>
    public async Task RefreshForgottenAsync()
    {
        try
        {
            var items = await controller.History.GetForgottenAsync();
            var now = DateTimeOffset.UtcNow;
            Forgotten.Clear();
            foreach (var item in items)
            {
                Forgotten.Add(ForgottenItemViewModel.From(item, now));
            }

            HasForgotten = items.Count > 0;
            ForgottenSummary = items.Count switch
            {
                0 => "Nothing is forgotten. To forget something, right-click it in the panel (or press the Menu key) and choose Forget forever.",
                1 => "1 item is never recorded:",
                _ => $"{items.Count:N0} items are never recorded:",
            };
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Reading the forgotten list failed: {ex.Message}");
        }

        await RefreshStatsAsync();
    }

    /// <summary>"Allow again" on one row: its content is recorded again from its next copy.</summary>
    /// <param name="id">The list entry id.</param>
    /// <returns>A task completing when stored (the list refreshes through <see cref="AppController.ForgottenChanged"/>).</returns>
    public async Task AllowAgainAsync(long id)
    {
        try
        {
            await controller.History.AllowAgainAsync(id);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Allowing a forgotten item again failed: {ex.Message}");
        }
    }

    /// <summary>"Allow all again": empties the list.</summary>
    /// <returns>A task completing when stored.</returns>
    public async Task AllowAllAgainAsync()
    {
        try
        {
            await controller.History.AllowAllAgainAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Allowing all forgotten items again failed: {ex.Message}");
        }

        // No event fires when the list was already empty; refresh anyway so the page matches the store.
        await RefreshForgottenAsync();
    }

    /// <summary>Turns the listener accounting into one honest sentence for the settings page.</summary>
    /// <param name="stats">Statistics, or <see langword="null"/> when capture has not started.</param>
    /// <returns>The description.</returns>
    private static string DescribeCapture(ClipboardMonitorStatistics? stats)
    {
        if (stats is not { } s)
        {
            return "Not watching the clipboard.";
        }

        var text = $"Since start: {s.Read:N0} clipboard change{(s.Read == 1 ? "" : "s")} read the moment Windows announced them";
        text += s.Superseded == 0
            ? " — none overwritten before they could be read."
            : $" — {s.Superseded:N0} replaced by a newer one within a fraction of a millisecond (usually one app updating the same copy twice).";
        if (s.LockedOut > 0)
        {
            text += $" {s.LockedOut:N0} skipped because another app kept the clipboard locked.";
        }

        if (s.Recovered > 0)
        {
            text += $" The watchdog recovered {s.Recovered:N0} change{(s.Recovered == 1 ? "" : "s")} Windows never announced.";
        }

        return text;
    }

    /// <summary>
    /// Imports Windows' clipboard history now.
    /// </summary>
    /// <returns>A task completing when the import finished.</returns>
    public async Task ImportNowAsync()
    {
        IsImporting = true;
        ImportStatus = "Importing…";
        try
        {
            ImportStatus = await controller.ImportWindowsHistoryAsync();
            await RefreshStatsAsync();
        }
        finally
        {
            IsImporting = false;
        }
    }

    /// <summary>
    /// Persists the ignored-apps text (normalized: one process name per entry).
    /// </summary>
    public void ApplyIgnoredApps()
    {
        var apps = ParseIgnoredApps(IgnoredAppsText);
        Update(s => s with { IgnoredApps = apps });
    }

    /// <summary>
    /// Appends every catalogued password manager and authenticator missing from the text box — including
    /// ones the user deleted earlier, which the automatic seeding never brings back — and saves the list.
    /// </summary>
    /// <remarks>
    /// Merges into the text box rather than the saved list, so unsaved edits the user typed are kept (and
    /// saved with it) instead of being overwritten.
    /// </remarks>
    /// <returns>How many process names were added (0 when all were already listed).</returns>
    public int AddKnownPasswordManagers()
    {
        var merged = KnownPasswordManagers.AddMissing(ParseIgnoredApps(IgnoredAppsText), out int added);
        IgnoredAppsText = string.Join(Environment.NewLine, merged);
        ApplyIgnoredApps();
        return added;
    }

    /// <summary>Splits the ignored-apps text box into entries (lines, commas or semicolons).</summary>
    /// <param name="text">The text.</param>
    /// <returns>Trimmed, non-empty entries in typed order.</returns>
    private static string[] ParseIgnoredApps(string text) =>
        text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Turns Windows' own clipboard history on or off.
    /// </summary>
    /// <param name="enabled">New state.</param>
    public void SetWindowsHistory(bool enabled)
    {
        try
        {
            WindowsClipboardSettings.SetWindowsHistoryEnabled(enabled);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Warn($"Changing Windows clipboard history failed: {ex.Message}");
        }

        RefreshSystemStatus();
    }

    /// <summary>
    /// Releases Win+V from Explorer (or gives it back) and restarts Explorer so it takes effect.
    /// </summary>
    /// <param name="release">Release (true) or restore (false).</param>
    /// <returns>A task completing after Explorer restarted and the shortcut was re-applied.</returns>
    public async Task SetWinVReleasedAsync(bool release)
    {
        try
        {
            WindowsClipboardSettings.SetWinVReleasedByExplorer(release);
            await WindowsClipboardSettings.RestartExplorer();

            // Give the new Explorer a moment to register (or skip) its hotkeys, then re-apply ours so we
            // switch between RegisterHotKey and the hook as appropriate.
            await Task.Delay(1500);
            await controller.ApplyHotkeyAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Changing Explorer's Win+V registration failed.", ex);
        }

        RefreshSystemStatus();
        RefreshHotkeyStatus();
    }

    /// <summary>Formats a byte count (KB/MB/GB).</summary>
    /// <param name="bytes">Byte count.</param>
    /// <returns>The formatted size.</returns>
    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    /// <summary>Persists a settings change unless loading.</summary>
    /// <param name="mutate">The change.</param>
    private void Update(Func<AppSettings, AppSettings> mutate)
    {
        if (loading)
        {
            return;
        }

        try
        {
            controller.Settings.Update(mutate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Saving settings failed.", ex);
        }
    }

    /// <summary>Validates and persists the shortcut.</summary>
    /// <param name="value">New text.</param>
    partial void OnOpenHotkeyChanged(string value)
    {
        if (loading)
        {
            return;
        }

        if (HotkeyGesture.TryParse(value, out var gesture))
        {
            HotkeyError = string.Empty;
            Update(s => s with { OpenHotkey = gesture.ToString() });
        }
        else
        {
            HotkeyError = "Not a valid shortcut. Use modifiers + a key, e.g. Win+V or Ctrl+Shift+V.";
        }
    }

    /// <summary>Persists the change.</summary>
    /// <param name="value">New value.</param>
    partial void OnUseKeyboardHookFallbackChanged(bool value) => Update(s => s with { UseKeyboardHookFallback = value });

    /// <summary>Persists the change (NumberBox yields NaN when cleared; ignored).</summary>
    /// <param name="value">New value.</param>
    partial void OnMaxItemsChanged(double value)
    {
        if (!double.IsNaN(value))
        {
            Update(s => s with { MaxItems = (int)value });
        }
    }

    /// <summary>Persists the change.</summary>
    /// <param name="value">New value.</param>
    partial void OnRetentionDaysChanged(double value)
    {
        if (!double.IsNaN(value))
        {
            Update(s => s with { RetentionDays = (int)value });
        }
    }

    /// <summary>Persists the change.</summary>
    /// <param name="value">New value.</param>
    partial void OnMaxItemSizeMBChanged(double value)
    {
        if (!double.IsNaN(value))
        {
            Update(s => s with { MaxItemSizeMB = (int)value });
        }
    }

    /// <summary>Persists the change.</summary>
    /// <param name="value">New value.</param>
    partial void OnMaxTotalSizeMBChanged(double value)
    {
        if (!double.IsNaN(value))
        {
            Update(s => s with { MaxTotalSizeMB = (int)value });
        }
    }

    /// <summary>Persists the change.</summary>
    /// <param name="value">New value.</param>
    partial void OnCaptureImagesChanged(bool value) => Update(s => s with { CaptureImages = value });

    /// <summary>Persists the change.</summary>
    /// <param name="value">New value.</param>
    partial void OnCaptureFilesChanged(bool value) => Update(s => s with { CaptureFiles = value });

    /// <summary>Persists the change.</summary>
    /// <param name="value">New value.</param>
    partial void OnPreserveAllFormatsChanged(bool value) => Update(s => s with { PreserveAllFormats = value });

    /// <summary>Persists the change.</summary>
    /// <param name="value">New value.</param>
    partial void OnPasteOnSelectChanged(bool value) => Update(s => s with { PasteOnSelect = value });

    /// <summary>Persists the change.</summary>
    /// <param name="value">New value.</param>
    partial void OnMoveToTopOnPasteChanged(bool value) => Update(s => s with { MoveToTopOnPaste = value });

    /// <summary>Persists the change.</summary>
    /// <param name="value">New value.</param>
    partial void OnPinnedOnTopChanged(bool value) => Update(s => s with { PinnedOnTop = value });

    /// <summary>Persists the change.</summary>
    /// <param name="value">New value.</param>
    partial void OnImportWindowsHistoryOnStartupChanged(bool value) => Update(s => s with { ImportWindowsHistoryOnStartup = value });

    /// <summary>Persists the change.</summary>
    /// <param name="value">New value.</param>
    partial void OnIsCapturePausedChanged(bool value) => Update(s => s with { IsCapturePaused = value });

    /// <summary>Persists the change; the controller starts or stops the pipe when it sees the new settings.</summary>
    /// <param name="value">New value.</param>
    partial void OnEnableCommandLineChanged(bool value) => Update(s => s with { EnableCommandLine = value });

    /// <summary>Persists the ShareX import switch (the controller starts or stops the folder watch).</summary>
    /// <param name="value">New value.</param>
    partial void OnImportShareXScreenshotsChanged(bool value) => Update(s => s with { ImportShareXScreenshots = value });

    /// <summary>Persists the change.</summary>
    /// <param name="value">New value.</param>
    partial void OnPlacementIndexChanged(int value)
    {
        if (value >= 0)
        {
            Update(s => s with { Placement = (FlyoutPlacement)value });
        }
    }

    /// <summary>Persists the change.</summary>
    /// <param name="value">New value.</param>
    partial void OnThemeIndexChanged(int value)
    {
        if (value >= 0)
        {
            Update(s => s with { Theme = (AppTheme)value });
        }
    }

    /// <summary>Adds/removes the Run entry.</summary>
    /// <param name="value">New value.</param>
    partial void OnLaunchAtStartupChanged(bool value)
    {
        if (loading)
        {
            return;
        }

        try
        {
            StartupRegistration.SetEnabled(value, AppController.ExecutablePath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Warn($"Changing start-with-Windows failed: {ex.Message}");
        }
    }
}
