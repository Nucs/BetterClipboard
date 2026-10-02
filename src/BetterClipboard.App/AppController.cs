using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using BetterClipboard.App.Views;
using BetterClipboard.Core;
using BetterClipboard.Core.Cli;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Everything;
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
public sealed partial class AppController
{
    private readonly DispatcherQueue ui;
    private readonly StartupOptions options;
    private readonly AppPaths paths = AppPaths.ResolveDefault();

    /// <summary>
    /// DIB → PNG encoder for <see cref="GetImagePngAsync"/> (the image overlays). Stateless, so one instance is reused.
    /// </summary>
    private readonly WicImageExporter imageExporter = new();
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
    /// voidtools Everything (the panel's Everything tab) while <see cref="AppSettings.ShowEverythingTab"/> is on:
    /// created in <see cref="Start"/> or when the setting is switched on, disposed when it is switched off and on
    /// exit — off means BetterClipboard does not talk to Everything at all. UI thread only.
    /// </summary>
    private EverythingIntegration? everything;

    /// <summary>
    /// The Win+R history (the panel's Run tab): created in <see cref="Start"/>, switched on and off with
    /// <see cref="AppSettings.RecordRunHistory"/>, disposed on exit (which keeps its snapshot, unlike "off").
    /// </summary>
    private RunHistoryIntegration? runHistory;

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

    /// <summary>
    /// Raised on the UI thread when Everything started, stopped, finished loading or was installed, and when the
    /// Everything tab was switched on or off (the flyout shows or hides the tab; Settings refreshes its status).
    /// </summary>
    public event EventHandler? EverythingStatusChanged;

    /// <summary>
    /// Raised on the UI thread when the Win+R history watch started or stopped, Windows' list was read, or a run
    /// was recorded (the flyout shows or hides its Run tab; Settings refreshes its status line).
    /// </summary>
    public event EventHandler? RunHistoryStatusChanged;

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

    /// <summary>The Everything integration while the Everything tab is switched on; <see langword="null"/> while off.</summary>
    public EverythingIntegration? Everything => everything;

    /// <summary>
    /// Whether the panel shows its Everything tab: the setting is on and Everything is installed or running (as
    /// of the last refresh, which every summon starts).
    /// </summary>
    public bool IsEverythingTabAvailable => Settings.Current.ShowEverythingTab && everything?.IsAvailable == true;

    /// <summary>
    /// Whether the panel shows its Run tab: the <see cref="AppSettings.RecordRunHistory"/> setting. Unlike the ShareX
    /// tab it does not wait for anything to be found — Win+R exists on every Windows.
    /// </summary>
    public bool IsRunTabAvailable => settings?.Current.RecordRunHistory == true;

    /// <summary>Whether Windows' Win+R history is being watched right now (the setting on and started).</summary>
    public bool IsRunHistoryWatching => runHistory?.IsWatching == true;

    /// <summary>How many commands Windows' own Win+R list held at the last look (-1 before the first one).</summary>
    public int RunHistoryWindowsCount => runHistory?.WindowsCount ?? -1;

    /// <summary>Win+R runs recorded since the app started (seen in Windows' list or run from the panel).</summary>
    public int RunHistoryRecordedThisSession => runHistory?.RecordedThisSession ?? 0;

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

        // Windows' own screenshots (the Snipping tab): the Screenshots folder is looked up on the pool, then watched
        // (AppController.Snipping.cs).
        StartSnipping();

        // Everything is only talked to while its tab is wanted; the first lookup (window, registry) runs on the pool.
        if (settings.Current.ShowEverythingTab)
        {
            SetEverything(enabled: true);
        }

        // The first look (and, on first activation, the import of Windows' 26) runs on the pool; the watch starts first.
        runHistory = new RunHistoryIntegration(history);
        runHistory.StatusChanged += (_, _) => ui.TryEnqueue(() => RunHistoryStatusChanged?.Invoke(this, EventArgs.Empty));
        _ = StartRunHistoryAsync(runHistory, settings.Current.RecordRunHistory);

        // The shell tabs (Pwsh, Cmd): PowerShell's file is only read when its tab loads; cmd windows are read on a
        // timer while that tab is on, through helper processes (AppController.Shells.cs).
        StartShellHistories();

        // The prompt tabs (Claude, Codex): each agent's files are imported once, then followed byte by byte on a
        // background-mode thread per agent (AppController.Prompts.cs).
        StartPromptArchives();

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

        // Everything may have started or exited since the last look; the tab follows when the refresh lands (ms).
        _ = everything?.RefreshAsync();

        // Same for PowerShell's history file (a profile may have just created it): the Pwsh tab follows in ms.
        RefreshPowerShellAvailability();

        // And for the agents' prompts (an agent installed or first used meanwhile): the Claude and Codex tabs follow.
        RefreshPromptAvailability();

        // A Screenshots folder that did not exist may have been created by the first screenshot: look again (only then).
        RefreshSnippingIfWaiting();
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

        // Same for the Screenshots folder and Snipping Tool: its card shows the folder and Snipping Tool's saving as of now.
        _ = snipping?.RefreshAsync();

        // Same for Everything: its card shows the install and the running state as of now.
        _ = everything?.RefreshAsync(forceInstallation: true);
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
    /// Pastes (or only copies) a file opened in Everything — the Everything tab's live picks, which are not history
    /// entries: the file itself (<c>CF_HDROP</c>, drop effect "copy"), or its path as text.
    /// </summary>
    /// <remarks>
    /// What went through the clipboard becomes history like any copy: it is stored as an Everything item
    /// (<see cref="ClipOrigin.Everything"/>), and then the tab shows that entry in the pick's place. Pause, ignored
    /// apps ("Everything") and "Forget forever" apply in the history service. Our own clipboard write is not
    /// captured by the listener (echo suppression), which is why it is stored explicitly.
    /// </remarks>
    /// <param name="pick">The pick.</param>
    /// <param name="plainText">Paste the path as text instead of the file.</param>
    /// <param name="paste">Inject Ctrl+V into the target (otherwise only copy).</param>
    /// <returns>A task completing when done; failures are logged, never thrown (fire-and-forget safe).</returns>
    public async Task PastePickAsync(EverythingPick pick, bool plainText, bool paste = true)
    {
        var target = pasteTarget?.TargetWindow ?? 0;
        try
        {
            var formats = ReplayFormats.ForFiles([pick.FullPath], plainText);
            await monitor!.WriteAsync(formats);
            _ = StorePickAsync(formats, pin: false);
            flyout?.Dismiss(restoreFocus: true);
            if (paste && Settings.Current.PasteOnSelect && target != 0)
            {
                await PasteInjector.PasteIntoAsync(target);
            }
        }
        catch (Exception ex)
        {
            // Content-free: never the path.
            AppLog.Error("Pasting a file opened in Everything failed.", ex);
            flyout?.Dismiss(restoreFocus: true);
        }
    }

    /// <summary>
    /// Keeps a pick in the history (Pin, or adding it to a group): stores the file like an Explorer copy, as an
    /// Everything item, so it stays when Everything's run history forgets it.
    /// </summary>
    /// <param name="pick">The pick.</param>
    /// <param name="pin">Pin the stored entry.</param>
    /// <returns>The stored entry, or <see langword="null"/> when it was not kept (paused, Everything ignored, forgotten, failed — logged).</returns>
    public Task<ClipEntry?> KeepPickAsync(EverythingPick pick, bool pin) =>
        StorePickAsync(ReplayFormats.ForFiles([pick.FullPath], plainTextOnly: false), pin);

    /// <summary>
    /// Hides a pick from the Everything tab until it is opened in Everything again (Delete on a pick: nothing in
    /// Everything is changed — the integration only reads). Kept in the encrypted store.
    /// </summary>
    /// <param name="pick">The pick.</param>
    /// <returns>A task completing when stored; failures are logged, never thrown.</returns>
    public async Task HidePickAsync(EverythingPick pick)
    {
        try
        {
            var hidden = EverythingTab.ParseHidden(await History.GetStateValueAsync(EverythingTab.HiddenStateName));
            var next = EverythingTab.Hide(hidden, pick, DateTimeOffset.UtcNow);
            await History.SetStateValueAsync(EverythingTab.HiddenStateName, EverythingTab.FormatHidden(next));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Hiding a file in the Everything tab failed: {ex.Message}");
        }
    }

    /// <summary>
    /// "Forget forever" for a pick: the file is never recorded again, from any app, and the tab stops showing it.
    /// Works without storing it first (and while paused), since a file list's fingerprint is its paths.
    /// </summary>
    /// <param name="pick">The pick.</param>
    /// <returns>A task completing when stored; failures are logged, never thrown.</returns>
    public async Task ForgetPickAsync(EverythingPick pick)
    {
        try
        {
            await History.ForgetFilesAsync([pick.FullPath], "Everything");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Forgetting a file from the Everything tab failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Shows a path in Everything's own window (the running instance, else the installed one is started) and hides
    /// the panel, which would otherwise cover it.
    /// </summary>
    /// <param name="path">An absolute path.</param>
    /// <returns><see langword="true"/> when Everything took the request.</returns>
    public bool ShowInEverything(string path)
    {
        if (everything?.ShowInEverything(path) != true)
        {
            AppLog.Info("Show in Everything: Everything is not available.");
            return false;
        }

        flyout?.Dismiss(restoreFocus: false);
        return true;
    }

    /// <summary>Starts the installed (or last seen) Everything in the background, from the tab's "not running" state.</summary>
    /// <returns><see langword="true"/> when started.</returns>
    public bool StartEverything() => everything?.StartEverything() == true;

    /// <summary>Stores formats of a pick as an Everything item through the history worker.</summary>
    /// <param name="formats">What to store (the file, or its path as text).</param>
    /// <param name="pin">Pin the entry.</param>
    /// <returns>The entry, or <see langword="null"/> when rules rejected it or storing failed (logged).</returns>
    private async Task<ClipEntry?> StorePickAsync(IReadOnlyList<ClipFormatData> formats, bool pin)
    {
        try
        {
            var entry = await History.AddAsync(new ClipCapture
            {
                Formats = formats,
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Origin = ClipOrigin.Everything,
                Pin = pin,
                Source = new SourceAppInfo("Everything", everything?.Status.ExecutablePath ?? everything?.Installation.ExecutablePath, "Everything"),
            });
            if (entry is null)
            {
                AppLog.Info("A file from Everything was not kept (capturing paused, Everything ignored, or forgotten forever).");
            }

            return entry;
        }
        catch (Exception ex)
        {
            AppLog.Error("Keeping a file from Everything failed.", ex);
            return null;
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

    /// <summary>
    /// Loads an image entry as full-resolution PNG bytes for the image overlays (hover peek, eye-icon zoom viewer),
    /// so a large picture is shown crisp rather than by upscaling the small card thumbnail.
    /// </summary>
    /// <param name="id">An image entry's id.</param>
    /// <param name="cancellationToken">Cancels a load whose overlay was dismissed before it finished.</param>
    /// <returns>
    /// PNG bytes — the stored PNG when the producer put one on the clipboard, else a WIC encode of the stored DIB — or
    /// <see langword="null"/> when the entry has no decodable image (the caller then falls back to the card thumbnail).
    /// </returns>
    /// <exception cref="OperationCanceledException">The load was cancelled.</exception>
    public async Task<byte[]?> GetImagePngAsync(long id, CancellationToken cancellationToken = default)
    {
        var formats = await History.GetFormatsAsync(id).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // Prefer the stored PNG as-is (lossless, no re-encode); fall back to encoding whatever bitmap format is stored.
        var png = formats.FirstOrDefault(f => f.Name is ClipFormatNames.Png or ClipFormatNames.PngMime)?.Data;
        png ??= await imageExporter.ToPngAsync(formats, cancellationToken).ConfigureAwait(false);
        return png;
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

        // Same for the Screenshots folder watch (the Snipping tab).
        await DisposeSnippingAsync();

        // Same for the Win+R watch: its snapshot must only move past runs that were really stored, and a rescan
        // in flight is cancelled (the next start finds those runs again).
        if (runHistory is not null)
        {
            await runHistory.DisposeAsync();
        }

        // The cmd reader writes its kept list through the history: stop it (a read in flight finishes) first.
        await DisposeShellHistoriesAsync();

        // Same for the prompt archives: a file's checkpoint must only move with the prompts stored up to it.
        await DisposePromptArchivesAsync();

        // Its reply window lives on its own thread; nothing it holds needs the history.
        everything?.Dispose();
        everything = null;
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

            // Idempotent too: the Screenshots folder watch starts (first activation: from now on) or stops (forgetting its
            // marker); the Snipping tab follows the setting.
            if (!exiting)
            {
                await ApplySnippingSettingsAsync(next);
            }

            // Idempotent too; switching on imports what Windows remembers now, off clears the snapshot. The tab
            // follows through RunHistoryStatusChanged, which applying always raises.
            if (runHistory is not null && !exiting)
            {
                await runHistory.SetEnabledAsync(next.RecordRunHistory);
            }

            // Idempotent like the others: the Cmd tab's reading starts or stops; both shell tabs follow.
            if (!exiting)
            {
                await ApplyShellSettingsAsync(next);
            }

            // Idempotent too: an agent's archive reader starts (catching up from its checkpoints) or stops (the archive stays).
            if (!exiting)
            {
                await ApplyPromptSettingsAsync(next);
            }

            if (next.ShowEverythingTab != (everything is not null) && !exiting)
            {
                SetEverything(next.ShowEverythingTab);
            }
        });
    }

    /// <summary>
    /// Starts or stops the Everything integration (UI thread). Off disposes it, so BetterClipboard stops talking to
    /// Everything at once; on creates it and looks Everything up on the pool. Either way the tab and Settings are
    /// told, so the tab appears or disappears without waiting for the next summon. A failure is logged: the rest
    /// of the app keeps working without the tab.
    /// </summary>
    /// <param name="enabled">Desired state.</param>
    private void SetEverything(bool enabled)
    {
        if (enabled && everything is null)
        {
            try
            {
                var integration = new EverythingIntegration();
                integration.StatusChanged += (_, _) => ui.TryEnqueue(() => EverythingStatusChanged?.Invoke(this, EventArgs.Empty));
                everything = integration;
                _ = integration.RefreshAsync(forceInstallation: true);
            }
            catch (Exception ex)
            {
                AppLog.Error("Starting the Everything integration failed.", ex);
            }
        }
        else if (!enabled && everything is not null)
        {
            var integration = everything;
            everything = null;
            integration.Dispose();
            AppLog.Info("Everything tab switched off.");
        }

        ui.TryEnqueue(() => EverythingStatusChanged?.Invoke(this, EventArgs.Empty));
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
    /// Starts the Win+R history without letting a failure escape into the fire-and-forget caller.
    /// </summary>
    /// <param name="integration">The integration.</param>
    /// <param name="enable">The <c>RecordRunHistory</c> setting at startup.</param>
    /// <returns>A task completing once started (or failed, logged).</returns>
    private static async Task StartRunHistoryAsync(RunHistoryIntegration integration, bool enable)
    {
        try
        {
            await integration.StartAsync(enable);
        }
        catch (Exception ex)
        {
            // Optional like ShareX: capture, paste and the other tabs keep working without it.
            AppLog.Error("Starting the Win+R history failed.", ex);
        }
    }

    /// <summary>
    /// Reads Windows' Win+R list now and stores the runs it shows since the last look (entering the Run tab), on
    /// top of the live watch. Never throws.
    /// </summary>
    /// <returns>How many entries were stored or refreshed (0 while the feature is off).</returns>
    public Task<int> RescanRunHistoryAsync() => runHistory?.RescanAsync("tab") ?? Task.FromResult(0);

    /// <summary>
    /// Runs a stored Win+R command again the way the Run dialog would (Ctrl+Enter on a command, or the card
    /// menu), then closes the panel and records the run, which moves the command to the top of the history.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Launch first, hide after.</b> The panel is the foreground window while the command starts, and Windows lets
    /// a process started by the foreground process bring its window to the front. Hiding first would hand the
    /// foreground back to the app below, and the new window could open behind it. The panel is then hidden
    /// without re-activating that app: the new window keeps the focus.
    /// </para>
    /// <para>
    /// Only offered for entries with a run time (<see cref="ClipEntry.HasRunHistory"/>): commands the user ran
    /// with Win+R before. Windows' own list is not written. The command never reaches the log — Win+R is where
    /// people paste one-off secrets too.
    /// </para>
    /// </remarks>
    /// <param name="entry">The entry (its <c>CF_UNICODETEXT</c> is the command).</param>
    /// <param name="asAdministrator">Run elevated: Windows asks for consent first.</param>
    /// <param name="ownerWindow">The panel's window, owner of the consent prompt.</param>
    /// <returns>How it ended, or <see langword="null"/> when the entry holds no single-line command or reading it failed (logged). Never throws.</returns>
    public async Task<RunCommandResult?> RunCommandAsync(ClipEntry entry, bool asAdministrator, nint ownerWindow)
    {
        try
        {
            var formats = await History.GetFormatsAsync(entry.Id);
            var text = formats.FirstOrDefault(f => f.Name == ClipFormatNames.UnicodeText);
            var command = text is null ? string.Empty : UnicodeTextCodec.Decode(text.Data).Trim();
            if (command.Length == 0 || command.AsSpan().IndexOfAny('\r', '\n') >= 0)
            {
                AppLog.Warn($"Entry {entry.Id} holds no single-line command to run.");
                return null;
            }

            var result = await RunCommandLauncher.RunAsync(command, asAdministrator, ownerWindow);
            AppLog.Info($"Ran entry {entry.Id} as a Win+R command{(asAdministrator ? " as administrator" : string.Empty)}: {result.Describe()}");
            if (result.Outcome != RunCommandOutcome.Started)
            {
                return result;
            }

            flyout?.Dismiss(restoreFocus: false);
            if (runHistory is not null)
            {
                try
                {
                    await runHistory.RecordRunAsync(command);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
                {
                    // It ran; only its new place at the top is lost (shutting down, or the store failed).
                    AppLog.Warn($"Recording the run of entry {entry.Id} failed ({ex.GetType().Name}).");
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Running entry {entry.Id} failed.", ex);
            return null;
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
