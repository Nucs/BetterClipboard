using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using BetterClipboard.App.Views;
using BetterClipboard.Core;
using BetterClipboard.Core.Cli;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Security;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;
using BetterClipboard.Windows.Cli;
using BetterClipboard.Windows.Clipboard;
using BetterClipboard.Windows.Imaging;
using BetterClipboard.Windows.Import;
using BetterClipboard.Windows.Input;
using BetterClipboard.Windows.Integrations;
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

    /// <summary>The bclip pipe server while command-line access is on; <see langword="null"/> otherwise.</summary>
    private CliPipeServer? commandLine;

    /// <summary>Serializes starting/stopping <see cref="commandLine"/> (toggling quickly must not race two servers).</summary>
    private readonly SemaphoreSlim commandLineGate = new(1, 1);

    /// <summary>ShareX detection and screenshot import (created in <see cref="Start"/>, disposed on exit).</summary>
    private ShareXIntegration? shareX;

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

    /// <summary>
    /// Raised on the UI thread after groups were created, renamed, re-iconed, deleted, or gained/lost items
    /// (forwarded from the worker thread); the flyout rebuilds its groups column.
    /// </summary>
    public event EventHandler? GroupsChanged;

    /// <summary>
    /// Raised on the UI thread after the "Forget forever" list changed: something was forgotten, allowed again,
    /// or kept out once more (forwarded from the worker thread); Settings refreshes its list.
    /// </summary>
    public event EventHandler? ForgottenChanged;

    /// <summary>Raised on the UI thread after command-line access was switched on or off (or failed to start).</summary>
    public event EventHandler? CommandLineStatusChanged;

    /// <summary>
    /// Raised on the UI thread when ShareX was found or lost, its screenshot watch started or stopped, or a
    /// screenshot was imported (the flyout shows or hides its ShareX tab; Settings refreshes its status).
    /// </summary>
    public event EventHandler? ShareXStatusChanged;

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

    /// <summary>Whether the bclip pipe is being served right now.</summary>
    public bool IsCommandLineActive => commandLine is not null;

    /// <summary>Why command-line access could not start (e.g. the pipe name is taken), or <see langword="null"/>.</summary>
    public string? CommandLineError { get; private set; }

    /// <summary>
    /// What is known about ShareX (<see cref="ShareXInstallation.NotFound"/> until located, which happens
    /// off the UI thread shortly after <see cref="Start"/>). Decides whether the flyout has a ShareX tab.
    /// </summary>
    public ShareXInstallation ShareX => shareX?.Installation ?? ShareXInstallation.NotFound;

    /// <summary>Whether ShareX's screenshot folders are being watched right now (installed and the setting on).</summary>
    public bool IsShareXWatching => shareX?.IsWatching == true;

    /// <summary>The ShareX screenshot folders being watched (empty when not watching).</summary>
    public IReadOnlyList<string> ShareXWatchedFolders => shareX?.WatchedFolders ?? [];

    /// <summary>ShareX screenshots stored since the app started.</summary>
    public int ShareXImportedThisSession => shareX?.ImportedThisSession ?? 0;

    /// <summary>Where <c>bclip.exe</c> ships: next to the app (release builds; absent in a dev build's bin folder).</summary>
    public static string CommandLinePath => Path.Combine(AppContext.BaseDirectory, "bclip.exe");

    /// <summary>The folder to put on PATH so <c>bclip</c> runs by name.</summary>
    public static string CommandLineDirectory => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

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
        history.GroupsChanged += (_, _) => ui.TryEnqueue(() => GroupsChanged?.Invoke(this, EventArgs.Empty));
        history.ForgottenChanged += (_, _) => ui.TryEnqueue(() => ForgottenChanged?.Invoke(this, EventArgs.Empty));
        history.Start();
        _ = history.PruneAsync();

        monitor = new ClipboardMonitor(CreateCaptureOptions, new SourceAppResolver());
        monitor.Captured += (_, capture) => history.TryEnqueueCapture(capture);
        monitor.Start();

        // Located on a pool thread (registry + a few small files); the tab appears once it is found.
        shareX = new ShareXIntegration(history, () => Settings.Current.MaxItemSizeMB * 1024L * 1024L);
        shareX.StatusChanged += (_, _) => ui.TryEnqueue(() => ShareXStatusChanged?.Invoke(this, EventArgs.Empty));
        _ = StartShareXAsync(shareX, settings.Current.ImportShareXScreenshots);

        if (settings.Current.EnableCommandLine)
        {
            _ = SetCommandLineAsync(enabled: true);
        }

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

        // Opening Settings is when a user who just installed ShareX looks for it: re-locate now instead of
        // waiting for the periodic check.
        _ = shareX?.RefreshAsync();
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

        // An isolated dev/test instance must never steal the shortcut from the installed app: a keyboard
        // hook installed later is called first and would swallow Win+V before the user's instance sees it.
        bool allowHook = current.UseKeyboardHookFallback;
        if (allowHook && paths.InstanceScope is not null && SingleInstance.IsRunning(AppPaths.DefaultInstanceName))
        {
            allowHook = false;
            AppLog.Info("Isolated instance while the installed BetterClipboard runs: not intercepting shortcuts with a keyboard hook.");
        }

        var result = await hotkeys.ApplyAsync(gesture, allowHook);
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

        // Stop answering bclip before the history it reads from is drained and disposed.
        await SetCommandLineAsync(enabled: false);

        // Stop the ShareX watch before the history closes: a screenshot delivered after that would fail,
        // and the catch-up marker must only move for screenshots that were really stored.
        if (shareX is not null)
        {
            await shareX.DisposeAsync();
        }
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

            if (next.EnableCommandLine != IsCommandLineActive && !exiting)
            {
                await SetCommandLineAsync(next.EnableCommandLine);
            }

            // Idempotent: the integration ignores an unchanged value.
            if (shareX is not null && !exiting)
            {
                await shareX.SetEnabledAsync(next.ImportShareXScreenshots);
            }
        });
    }

    /// <summary>
    /// Starts the ShareX integration without letting a failure escape into the fire-and-forget caller.
    /// </summary>
    /// <param name="integration">The integration.</param>
    /// <param name="enable">The <c>ImportShareXScreenshots</c> setting at startup.</param>
    /// <returns>A task completing once started (or failed, logged).</returns>
    private static async Task StartShareXAsync(ShareXIntegration integration, bool enable)
    {
        try
        {
            await integration.StartAsync(enable);
        }
        catch (Exception ex)
        {
            // ShareX support is optional: the rest of the app keeps working without it.
            AppLog.Error("Starting the ShareX integration failed.", ex);
        }
    }

    /// <summary>
    /// Adds the app folder (which holds <c>bclip.exe</c>) to the user PATH, so new terminals and AI tools
    /// can run <c>bclip</c> by name.
    /// </summary>
    /// <returns><see langword="true"/> when the PATH changed; <see langword="false"/> when it was already there.</returns>
    /// <exception cref="UnauthorizedAccessException">The user environment is locked by policy.</exception>
    /// <exception cref="System.Security.SecurityException">The user environment is not accessible.</exception>
    public bool AddCommandLineToPath()
    {
        bool changed = UserPath.Add(CommandLineDirectory);
        AppLog.Info(changed ? $"Added {CommandLineDirectory} to the user PATH." : "bclip's folder was already on the user PATH.");
        return changed;
    }

    /// <summary>
    /// Starts or stops the bclip pipe server (idempotent, serialized). Failures are logged and surfaced
    /// through <see cref="CommandLineError"/> rather than thrown: the rest of the app keeps working.
    /// </summary>
    /// <param name="enabled">Desired state.</param>
    /// <returns>A task completing once the server is in the desired state (or failed to start).</returns>
    private async Task SetCommandLineAsync(bool enabled)
    {
        await commandLineGate.WaitAsync();
        try
        {
            if (enabled && commandLine is null && history is not null && monitor is not null)
            {
                var processor = new CliCommandProcessor(history, monitor, new WicImageExporter(), () => Settings.Current, CaptureStatisticsForCli, Version);
                var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("No user SID.");
                var server = new CliPipeServer(CliEndpoint.PipeName(sid, Process.GetCurrentProcess().SessionId, paths.InstanceScope), processor.ExecuteAsync);
                try
                {
                    server.Start();
                    commandLine = server;
                    CommandLineError = null;
                    AppLog.Info("Command-line access is on (bclip).");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // FirstPipeInstance failed: someone else holds the name. Never share it.
                    CommandLineError = $"The bclip pipe is already in use by another program ({ex.Message}).";
                    AppLog.Warn(CommandLineError);
                    await server.DisposeAsync();
                }
            }
            else if (!enabled && commandLine is not null)
            {
                var server = commandLine;
                commandLine = null;
                await server.DisposeAsync();
                AppLog.Info("Command-line access is off.");
            }
        }
        finally
        {
            commandLineGate.Release();
        }

        ui.TryEnqueue(() => CommandLineStatusChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>The listener accounting in the command line's wire shape.</summary>
    /// <returns>The statistics, or <see langword="null"/> before capture started.</returns>
    private CliCaptureStats? CaptureStatisticsForCli() => monitor?.Statistics is { } s
        ? new CliCaptureStats(s.Notifications, s.Read, s.Captured, s.Superseded, s.SelfWrites, s.LockedOut, s.Recovered)
        : null;

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
