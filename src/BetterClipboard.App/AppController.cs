using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using BetterClipboard.App.Views;
using BetterClipboard.Core;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Security;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;
using BetterClipboard.Windows.Clipboard;
using BetterClipboard.Windows.Imaging;
using BetterClipboard.Windows.Import;
using BetterClipboard.Windows.Input;
using BetterClipboard.Windows.Security;
using BetterClipboard.Windows.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace BetterClipboard.App;

/// <summary>
/// Composition root and orchestrator: owns every long-lived service (storage, capture, hotkey, tray) and
/// every window, and implements the user-level flows (summon, paste, import, exit).
/// </summary>
/// <remarks>
/// <para>
/// <b>Threads.</b> Public members are called on the UI thread unless noted. Services raise events on their
/// own threads (history worker, clipboard thread, input thread, tray thread); this class marshals them to
/// the UI with <see cref="DispatcherQueue.TryEnqueue(DispatcherQueueHandler)"/> before touching windows.
/// </para>
/// <para>
/// <b>Lifetime.</b> Created once by <see cref="App"/>; lives until <see cref="ExitAsync"/>. Windows are
/// created lazily: the flyout once (then shown/hidden), the settings window on demand (closed = freed).
/// </para>
/// </remarks>
public sealed class AppController
{
    private readonly DispatcherQueue ui;
    private readonly StartupOptions options;
    private readonly AppPaths paths = AppPaths.ResolveDefault();
    private SettingsStore? settings;
    private ClipHistoryService? history;
    private ClipboardMonitor? monitor;
    private HotkeyService? hotkeys;
    private TrayIcon? tray;
    private ClipboardFlyout? flyout;
    private SettingsWindow? settingsWindow;
    private volatile CaptureRules rules = new();
    private ForegroundContext? pasteTarget;
    private (string Hotkey, bool HookFallback) appliedHotkey;
    private bool exiting;

    /// <summary>
    /// Creates the controller; nothing starts until <see cref="Start"/>.
    /// </summary>
    /// <param name="ui">The UI thread's dispatcher.</param>
    /// <param name="options">Command-line options.</param>
    public AppController(DispatcherQueue ui, StartupOptions options)
    {
        this.ui = ui;
        this.options = options;
    }

    /// <summary>Raised on the UI thread after the history changed (forwarded from the worker thread).</summary>
    public event EventHandler<ClipChangedEventArgs>? HistoryChanged;

    /// <summary>Raised on the UI thread after the global shortcut was (re)applied.</summary>
    public event EventHandler? HotkeyStatusChanged;

    /// <summary>Data locations (database, settings, logs).</summary>
    public AppPaths Paths => paths;

    /// <summary>Settings store (valid after <see cref="Start"/>).</summary>
    public SettingsStore Settings => settings ?? throw new InvalidOperationException("Not started.");

    /// <summary>History service (valid after <see cref="Start"/>).</summary>
    public ClipHistoryService History => history ?? throw new InvalidOperationException("Not started.");

    /// <summary>
    /// How the encrypted history store was opened (store id, folder, migration/quarantine flags) — for the
    /// settings page. <see langword="null"/> before <see cref="Start"/>. Holds no key material.
    /// </summary>
    public MachineBoundHistoryResult? StorageInfo { get; private set; }

    /// <summary>
    /// Capture accounting of the clipboard listener since startup (notifications, reads, superseded,
    /// watchdog recoveries); <see langword="null"/> before <see cref="Start"/>. Cheap to read from any thread.
    /// </summary>
    public ClipboardMonitorStatistics? CaptureStatistics => monitor?.Statistics;

    /// <summary>Current state of the global shortcut.</summary>
    public HotkeyRegistration HotkeyStatus => hotkeys?.Current ?? new HotkeyRegistration(default, HotkeyMode.None, 0);

    /// <summary>Path of the running executable (for the Run key).</summary>
    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "BetterClipboard.exe");

    /// <summary>Path of the multi-size app icon shipped next to the executable.</summary>
    public static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "BetterClipboard.ico");

    /// <summary>Informational version of the app.</summary>
    public static string Version =>
        typeof(AppController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>
    /// Starts storage, capture, the shortcut, the tray icon and (optionally) the Windows-history import.
    /// </summary>
    /// <exception cref="Exception">Storage or the clipboard listener could not start (the app cannot work without them).</exception>
    public void Start()
    {
        paths.EnsureCreated();
        AppLog.Initialize(paths.LogDirectory);
        AppLog.Info($"BetterClipboard {Version} starting (data: {paths.DataDirectory}, background: {options.Background}).");

        settings = new SettingsStore(paths.SettingsPath);
        settings.Load();
        rules = CaptureRules.FromSettings(settings.Current);
        settings.Changed += OnSettingsChanged;

        var store = OpenMachineBoundStore();
        history = new ClipHistoryService(store, new WinRtImageAnalyzer(), () => rules);
        history.Changed += (_, e) => ui.TryEnqueue(() => HistoryChanged?.Invoke(this, e));
        history.Start();
        _ = history.PruneAsync();

        monitor = new ClipboardMonitor(CreateCaptureOptions, new SourceAppResolver());
        monitor.Captured += (_, capture) => history.TryEnqueueCapture(capture);
        monitor.Start();

        hotkeys = new HotkeyService();
        hotkeys.Pressed += (_, context) => ui.TryEnqueue(() => OnHotkey(context));
        _ = ApplyHotkeyAsync();

        tray = new TrayIcon(IconPath, TrayTooltip(settings.Current));
        tray.Invoked += (_, _) => ui.TryEnqueue(() => ShowFlyout(ForegroundContext.CursorOnly()));
        tray.MenuProvider = BuildTrayMenu;

        if (settings.Current.ImportWindowsHistoryOnStartup)
        {
            _ = ImportWindowsHistoryAsync();
        }

        if (!options.Background)
        {
            ShowSettings();
        }
    }

    /// <summary>
    /// Shows the flyout at <paramref name="context"/> (no paste target when opened from the tray).
    /// </summary>
    /// <param name="context">Where the user was.</param>
    public void ShowFlyout(ForegroundContext context)
    {
        if (exiting)
        {
            return;
        }

        pasteTarget = context;
        EnsureFlyout().ShowAt(context, Settings.Current.Placement);
    }

    /// <summary>Opens (or focuses) the settings window.</summary>
    public void ShowSettings()
    {
        if (exiting)
        {
            return;
        }

        if (settingsWindow is null)
        {
            settingsWindow = new SettingsWindow(this);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
        }

        settingsWindow.Present();
    }

    /// <summary>
    /// Replays an entry onto the clipboard and (per settings) pastes it into the window the user came from.
    /// </summary>
    /// <param name="entry">The history entry.</param>
    /// <param name="plainText">Strip formatting ("paste as plain text").</param>
    /// <param name="paste">Inject Ctrl+V into the target (otherwise only copy).</param>
    /// <returns>A task completing when done; failures are logged, never thrown (fire-and-forget safe).</returns>
    public async Task PasteAsync(ClipEntry entry, bool plainText, bool paste = true)
    {
        var target = pasteTarget?.TargetWindow ?? 0;
        try
        {
            // Put the content on the clipboard while the flyout is still up (tens of ms at most)...
            var formats = ReplayFormats.Prepare(await History.GetFormatsAsync(entry.Id), plainText);
            if (formats.Count == 0)
            {
                AppLog.Warn($"Entry {entry.Id} has nothing to paste{(plainText ? " as plain text" : string.Empty)}.");
                flyout?.Dismiss(restoreFocus: true);
                return;
            }

            await monitor!.WriteAsync(formats);
            if (Settings.Current.MoveToTopOnPaste)
            {
                _ = History.MarkUsedAsync(entry.Id);
            }

            // ...then hand activation back to the target while we still own the foreground (see
            // ClipboardFlyout.Dismiss), and only then inject Ctrl+V into it.
            flyout?.Dismiss(restoreFocus: true);
            if (paste && Settings.Current.PasteOnSelect && target != 0)
            {
                await PasteInjector.PasteIntoAsync(target);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Pasting entry {entry.Id} failed.", ex);
            flyout?.Dismiss(restoreFocus: true);
        }
    }

    /// <summary>
    /// Re-applies the shortcut from settings.
    /// </summary>
    /// <returns>The resulting registration.</returns>
    public async Task<HotkeyRegistration> ApplyHotkeyAsync()
    {
        if (hotkeys is null)
        {
            return HotkeyStatus;
        }

        var current = Settings.Current;
        appliedHotkey = (current.OpenHotkey, current.UseKeyboardHookFallback);
        if (!HotkeyGesture.TryParse(current.OpenHotkey, out var gesture))
        {
            AppLog.Warn($"Invalid shortcut '{current.OpenHotkey}' in settings; falling back to Win+V.");
            HotkeyGesture.TryParse("Win+V", out gesture);
        }

        var result = await hotkeys.ApplyAsync(gesture, current.UseKeyboardHookFallback);
        HotkeyStatusChanged?.Invoke(this, EventArgs.Empty);
        tray?.SetTooltip(TrayTooltip(current));
        return result;
    }

    /// <summary>
    /// Imports Windows' own clipboard history and pinned items.
    /// </summary>
    /// <returns>A human-readable summary for the settings page.</returns>
    public async Task<string> ImportWindowsHistoryAsync()
    {
        try
        {
            var importer = new WindowsHistoryImporter();
            var result = await Task.Run(() => importer.ReadAllAsync());
            int created = await History.ImportAsync(result.Captures);
            var summary = $"Imported {created} new item{(created == 1 ? "" : "s")} from Windows " +
                          $"({result.Captures.Count} found; Win+V history: {Describe(result.HistoryStatus)}; pinned on disk: {result.PinnedOnDisk}).";
            AppLog.Info(summary);
            return summary;
        }
        catch (Exception ex)
        {
            AppLog.Error("Importing Windows clipboard history failed.", ex);
            return "Import failed: " + ex.Message;
        }

        static string Describe(string status) => status switch
        {
            "Success" => "read",
            "ClipboardHistoryDisabled" => "turned off in Windows",
            "AccessDenied" => "access denied",
            _ => status,
        };
    }

    /// <summary>
    /// Applies the theme preference to a window's root element.
    /// </summary>
    /// <param name="root">The window content.</param>
    public void ApplyTheme(FrameworkElement root)
    {
        root.RequestedTheme = Settings.Current.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    /// <summary>Opens the data folder in Explorer.</summary>
    public void OpenDataFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{paths.DataDirectory}\"") { UseShellExecute = true })?.Dispose();

    /// <summary>
    /// Stops everything (draining queued captures) and ends the process.
    /// </summary>
    /// <returns>A task completing just before the app exits.</returns>
    public async Task ExitAsync()
    {
        if (exiting)
        {
            return;
        }

        exiting = true;
        AppLog.Info("Exiting.");
        // Release the shortcut first so Win+V instantly belongs to Windows again.
        hotkeys?.Dispose();
        tray?.Dispose();
        monitor?.Dispose();
        if (history is not null)
        {
            await history.DisposeAsync();
        }

        flyout?.CloseForExit();
        settingsWindow?.Close();
        AppLog.Shutdown();
        Application.Current.Exit();
    }

    /// <summary>Handles the global shortcut (UI thread).</summary>
    /// <param name="context">Where the user was when pressing it.</param>
    private void OnHotkey(ForegroundContext context)
    {
        var window = EnsureFlyout();
        if (window.IsOpen)
        {
            // Pressing the shortcut again while the flyout is up closes it, like Win+V does.
            window.Dismiss(restoreFocus: true);
            return;
        }

        ShowFlyout(context);
    }

    /// <summary>Creates the flyout on first use.</summary>
    /// <returns>The flyout.</returns>
    private ClipboardFlyout EnsureFlyout() => flyout ??= new ClipboardFlyout(this);

    /// <summary>
    /// Opens this machine's encrypted history: binds to MachineGuid + user SID, unseals the key with DPAPI,
    /// adopts a legacy plaintext database, and quarantines a store that no longer decrypts.
    /// </summary>
    /// <returns>The initialized store.</returns>
    /// <exception cref="Exception">Any failure <see cref="MachineBoundHistory.Open"/> documents (fatal for startup).</exception>
    private ClipStore OpenMachineBoundStore()
    {
        var identity = MachineIdentity.Current();
        var binding = MachineBinding.Derive(identity.MachineGuid, identity.UserSid);
        try
        {
            var opened = MachineBoundHistory.Open(paths, new DpapiKeyProtector(), binding, DateTimeOffset.Now);
            StorageInfo = opened;

            // Ids and flags only — never the key, the binding or the identifiers they come from.
            AppLog.Info($"History store {opened.StoreId} opened (encrypted: {opened.Store.IsEncrypted}, new key: {opened.CreatedKey}, " +
                        $"adopted legacy: {opened.AdoptedLegacyDatabase}, encrypted legacy in place: {opened.Store.MigratedFromPlaintext}).");
            if (opened.QuarantinedDirectory is not null)
            {
                AppLog.Warn($"The previous history could not be decrypted on this machine and was set aside at '{opened.QuarantinedDirectory}': {opened.QuarantineReason}");
            }

            return opened.Store;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(binding);
        }
    }

    /// <summary>The paste target captured when the flyout was summoned (0 when none).</summary>
    internal nint PasteTargetWindow => pasteTarget?.TargetWindow ?? 0;

    /// <summary>Capture options for the clipboard thread, from the current settings snapshot.</summary>
    /// <returns>The options.</returns>
    private CaptureOptions CreateCaptureOptions()
    {
        var current = Settings.Current;
        return new CaptureOptions
        {
            PreserveAllFormats = current.PreserveAllFormats,
            CaptureImages = current.CaptureImages,
            CaptureFiles = current.CaptureFiles,
            MaxItemBytes = current.MaxItemSizeMB * 1024L * 1024L,
        };
    }

    /// <summary>Reacts to settings changes (any thread; marshals UI work).</summary>
    /// <param name="sender">The store.</param>
    /// <param name="next">The new settings.</param>
    private void OnSettingsChanged(object? sender, AppSettings next)
    {
        var previous = rules;
        rules = CaptureRules.FromSettings(next);
        ui.TryEnqueue(async () =>
        {
            tray?.SetTooltip(TrayTooltip(next));

            // Only re-register when the shortcut inputs changed: re-registering drops and re-installs the
            // hook, which is cheap but would needlessly flicker the status text on unrelated edits.
            if ((next.OpenHotkey, next.UseKeyboardHookFallback) != appliedHotkey)
            {
                await ApplyHotkeyAsync();
            }

            if (previous.Retention != rules.Retention)
            {
                await History.PruneAsync();
            }
        });
    }

    /// <summary>Builds the tray menu (tray thread).</summary>
    /// <returns>The items.</returns>
    private IReadOnlyList<TrayMenuItem> BuildTrayMenu()
    {
        var current = Settings.Current;
        return
        [
            new TrayMenuItem($"Open clipboard\t{current.OpenHotkey}", () => ui.TryEnqueue(() => ShowFlyout(ForegroundContext.CursorOnly()))),
            new TrayMenuItem("Settings…", () => ui.TryEnqueue(ShowSettings)),
            TrayMenuItem.Separator,
            new TrayMenuItem("Pause capturing", () => ui.TryEnqueue(() => Settings.Update(s => s with { IsCapturePaused = !s.IsCapturePaused })), IsChecked: current.IsCapturePaused),
            TrayMenuItem.Separator,
            new TrayMenuItem("Exit", () => ui.TryEnqueue(() => _ = ExitAsync())),
        ];
    }

    /// <summary>Tray hover text reflecting capture state and shortcut.</summary>
    /// <param name="current">Current settings.</param>
    /// <returns>The tooltip.</returns>
    private string TrayTooltip(AppSettings current) =>
        current.IsCapturePaused ? "BetterClipboard — paused" : $"BetterClipboard — {current.OpenHotkey} to open";
}
