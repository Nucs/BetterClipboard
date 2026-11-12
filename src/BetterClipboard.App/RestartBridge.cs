using BetterClipboard.Core;
using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Settings;
using BetterClipboard.Windows.Input;
using BetterClipboard.Windows.Shell;

namespace BetterClipboard.App;

/// <summary>
/// What a silent restart's new process holds before the old one lets go (CLAUDE.md §2.46): the shortcuts, through a
/// keyboard hook, and its own tray icon. Created in <c>Program.Main</c> while the old process still runs; the controller
/// takes both over once it has started (<see cref="TakeTray"/>, <see cref="TakeOverShortcuts"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> The owner's choice (2026-11-12) restarts right after Settings closes, while the user is at the PC, and
/// nobody may see or feel it. Between the old process letting go and the new one being ready lie about a second. Without
/// the bridge a Win+V in that second would do nothing (Win+V released from Explorer) or open Windows' own clipboard panel
/// (not released), and the tray icon would vanish for that second.
/// </para>
/// <para>
/// <b>The shortcuts.</b> A low-level hook installed later is called first, so from the moment the bridge holds them every
/// press reaches the new process: the old one, still registered, never sees it. A press is remembered (the last one,
/// with the window the user was in) and the controller opens the panel for it once it is ready; never lost, at worst a
/// second late. Not when the user turned the hook off (<see cref="AppSettings.UseKeyboardHookFallback"/>), and not for an
/// isolated instance while the installed app runs (the rule of <c>AppController.ApplyHotkeyAsync</c>): those restarts
/// keep the gap.
/// </para>
/// <para>
/// <b>The tray icon.</b> The new icon is added hidden (<c>NIS_HIDDEN</c>), and the old process, as it lets go, reveals it
/// and removes its own back to back (<see cref="TrayIcon.HandOverTo"/>), so the icon neither vanishes nor shows twice. The
/// first version added it visibly: an icon pinned to the taskbar then showed twice for about 60 ms. Clicks on the new icon
/// before the controller wires it do nothing.
/// </para>
/// <para>
/// <b>Never stuck.</b> A bridge the controller never takes over (its start failed) lets go of the shortcuts after
/// <see cref="MaxHold"/>, so a broken start never swallows Win+V for good; the process's end removes the hook too.
/// </para>
/// </remarks>
public sealed class RestartBridge : IDisposable
{
    /// <summary>How long the bridge holds the shortcuts at most when nobody takes them over.</summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromMinutes(3);

    /// <summary>Guards <see cref="pending"/>, <see cref="presses"/> and <see cref="hotkeys"/>.</summary>
    private readonly Lock gate = new();

    /// <summary>The hook holding the shortcuts, or <see langword="null"/> once taken over (or never held).</summary>
    private HotkeyService? hotkeys;

    /// <summary>The tray icon, until the controller takes it.</summary>
    private TrayIcon? tray;

    /// <summary>Where the user was at the last press, or <see langword="null"/> without a press.</summary>
    private ForegroundContext? pending;

    /// <summary>How many presses the bridge caught.</summary>
    private int presses;

    /// <summary>Lets go of the shortcuts after <see cref="MaxHold"/>.</summary>
    private Timer? expiry;

    /// <summary>Use <see cref="Start"/>.</summary>
    private RestartBridge()
    {
    }

    /// <summary>Whether the bridge holds the shortcuts with its hook (false when hooking is not allowed or failed).</summary>
    public bool HoldsShortcuts { get; private set; }

    /// <summary>
    /// Starts holding the shortcuts and shows the new tray icon. Never throws: whatever fails is left to the controller,
    /// which then registers the shortcuts and adds the icon the usual way (with the gap the bridge would have closed).
    /// </summary>
    /// <param name="paths">This instance's paths (its settings and its scope).</param>
    /// <returns>The bridge; dispose it when nobody takes it over.</returns>
    /// <remarks>Runs before the log is opened, so it logs nothing; the controller logs what the bridge held.</remarks>
    public static RestartBridge Start(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var bridge = new RestartBridge();
        AppSettings settings;
        try
        {
            settings = SettingsStore.TryReadSnapshot(paths.SettingsPath) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            settings = new AppSettings();
        }

        // The tooltip is worded in the user's language from the start; the controller sets the same language again.
        UiText.Language = settings.InterfaceLanguage;
        try
        {
            // Hidden: the old process reveals it at the moment it removes its own (TrayIcon.HandOverTo).
            bridge.tray = new TrayIcon(AppController.IconPath, AppController.TrayTooltip(settings), startHidden: true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No icon from the bridge: the controller adds one later, and the old icon is then missed for a moment.
            bridge.tray = null;
        }

        bool allowHook = settings.UseKeyboardHookFallback
            && !(paths.InstanceScope is not null && SingleInstance.IsRunning(AppPaths.DefaultInstanceName));
        var gestures = HotkeyList.Parse(settings.OpenHotkeys).Gestures;
        if (allowHook && gestures.Count > 0)
        {
            try
            {
                var service = new HotkeyService();
                service.Pressed += bridge.OnPressed;
                var held = service.HoldWithHookAsync(gestures).Wait(TimeSpan.FromSeconds(2))
                    && service.Registrations.Any(r => r.Mode == HotkeyMode.KeyboardHook);
                if (held)
                {
                    bridge.hotkeys = service;
                    bridge.HoldsShortcuts = true;
                    bridge.expiry = new Timer(_ => bridge.TakeOverShortcuts(), null, MaxHold, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    service.Dispose();
                }
            }
            catch (Exception ex) when (ex is AggregateException or ObjectDisposedException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                bridge.HoldsShortcuts = false;
            }
        }

        return bridge;
    }

    /// <summary>
    /// Hands the tray icon to the controller (once): it wires its clicks and menu and words its tooltip.
    /// </summary>
    /// <returns>The icon, or <see langword="null"/> when the bridge has none (the controller then adds its own).</returns>
    public TrayIcon? TakeTray() => Interlocked.Exchange(ref tray, null);

    /// <summary>
    /// Lets go of the shortcuts (the controller has registered its own by now) and tells what was pressed meanwhile.
    /// Safe to call more than once; later calls return nothing.
    /// </summary>
    /// <returns>Where the user was at the last press and how many presses the bridge caught; the place is
    /// <see langword="null"/> without a press.</returns>
    public (ForegroundContext? Pending, int Presses) TakeOverShortcuts()
    {
        HotkeyService? service;
        ForegroundContext? last;
        int count;
        lock (gate)
        {
            service = hotkeys;
            hotkeys = null;
            last = pending;
            pending = null;
            count = presses;
            presses = 0;
        }

        expiry?.Dispose();
        service?.Dispose();
        return (last, count);
    }

    /// <summary>Lets go of the shortcuts and removes the tray icon if nobody took them.</summary>
    public void Dispose()
    {
        _ = TakeOverShortcuts();
        TakeTray()?.Dispose();
    }

    /// <summary>A press of a held shortcut: remembered for the controller (the last one wins).</summary>
    /// <param name="sender">The hook service.</param>
    /// <param name="context">Where the user was.</param>
    private void OnPressed(object? sender, ForegroundContext context)
    {
        lock (gate)
        {
            pending = context;
            presses++;
        }
    }
}
