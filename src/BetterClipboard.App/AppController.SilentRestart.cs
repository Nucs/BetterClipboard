using System.Diagnostics;
using System.Globalization;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Localization;
using BetterClipboard.Windows.Diagnostics;
using BetterClipboard.Windows.Input;
using BetterClipboard.Windows.Shell;
using Microsoft.UI.Dispatching;

namespace BetterClipboard.App;

/// <summary>
/// The silent restart after Settings (CLAUDE.md §2.46): Settings leaves memory behind that only a new process gives back,
/// so a few seconds after it closes BetterClipboard replaces itself with a fresh process, unseen and unfelt.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's choice</b> (2026-11-12): "right after Settings closes", while the clipboard panel is not open, with no
/// switch; "User must not see or feel or have UI flash or open accidentally during this silent restart."
/// </para>
/// <para>
/// <b>When</b> (<see cref="SilentRestartRules"/>): at least 5 s after Settings closed, nothing of the app on screen, no
/// update installing, no command-line request being served, no modifier key or mouse button held, and 2 s without keyboard
/// or mouse input. Checked once a second until then.
/// </para>
/// <para>
/// <b>How, in order:</b>
/// </para>
/// <list type="number">
/// <item>The new process is started with Windows' launch feedback off (<see cref="QuietProcess"/>: no busy pointer), told
/// whom it replaces and the last clipboard state this process handled.</item>
/// <item>It adds its tray icon hidden and holds the shortcuts with a keyboard hook (<see cref="RestartBridge"/>), then
/// says so through a named event; this process waits for that up to 3 s.</item>
/// <item>If the panel, Settings or the language picker opened meanwhile, or an update began, the restart is called off:
/// the new process ends quietly (its hidden icon goes with it) and this one tries again later.</item>
/// <item>Otherwise this process reveals the new process's icon and removes its own back to back
/// (<see cref="TrayIcon.HandOverTo"/>), leaves later commands to the new process, and exits the usual way: queued captures
/// drained, integrations stopped at their checkpoints.</item>
/// <item>The new process takes the lock, starts, reads a clipboard change made in between, registers the shortcuts and
/// lets the bridge go; a press the bridge caught opens the panel then.</item>
/// </list>
/// <para>
/// <b>What is never shown:</b> no window opens (a background start, no Settings, never the language picker: a restart that
/// would ask for a language does not happen), the focus stays where it is, and the panel's warm-up draws cloaked as at
/// every start.
/// </para>
/// </remarks>
public sealed partial class AppController
{
    /// <summary>How long the restart waits for the new process to hold the shortcuts and show its tray icon.</summary>
    private static readonly TimeSpan RestartReadyTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How long a called-off restart keeps its cancel signal for a new process that is slow to read it.</summary>
    private static readonly TimeSpan RestartCancelGrace = TimeSpan.FromSeconds(10);

    /// <summary>Whether Settings was opened since this process started. UI thread only.</summary>
    private bool settingsUsedSinceStart;

    /// <summary>When Settings last closed (<see cref="Environment.TickCount64"/>), or <see langword="null"/>. UI thread only.</summary>
    private long? settingsClosedAt;

    /// <summary>Asks <see cref="SilentRestartRules"/> once a second while a restart is wanted; created on first use. UI thread only.</summary>
    private DispatcherQueueTimer? silentRestartTimer;

    /// <summary>A restart attempt runs right now (between starting the new process and exiting or calling it off). UI thread only.</summary>
    private bool silentRestartInProgress;

    /// <summary>
    /// A new process failed to start or ended before taking over: no more attempts in this process's lifetime, so a broken
    /// environment never turns into a loop of starts. UI thread only.
    /// </summary>
    private bool silentRestartGivenUp;

    /// <summary>
    /// What this process held before the old one let go, when it is a silent restart's new process (<see cref="App"/> sets
    /// it); <see langword="null"/> for any other start. Taken over once, in <see cref="ApplyHotkeysAndTakeOverAsync"/>.
    /// </summary>
    public RestartBridge? RestartBridge { get; init; }

    /// <summary>
    /// Stops listening for commands from later launches without letting go of the instance lock (set by <see cref="App"/>):
    /// a process handing over to a silent restart leaves the commands sent from then on to the new process.
    /// </summary>
    public Action? StopListeningForCommands { get; init; }

    /// <summary>Settings was opened: this process now carries memory only a restart gives back; no restart while it shows.</summary>
    private void NoteSettingsOpened()
    {
        settingsUsedSinceStart = true;
        silentRestartTimer?.Stop();
    }

    /// <summary>Settings closed: ask about the restart once a second from now on (see the class remarks).</summary>
    private void ScheduleSilentRestart()
    {
        settingsClosedAt = Environment.TickCount64;
        if (exiting || silentRestartGivenUp || !settingsUsedSinceStart)
        {
            return;
        }

        if (silentRestartTimer is null)
        {
            silentRestartTimer = ui.CreateTimer();
            silentRestartTimer.Interval = SilentRestartRules.CheckInterval;
            silentRestartTimer.IsRepeating = true;
            silentRestartTimer.Tick += SilentRestartTimer_Tick;
        }

        silentRestartTimer.Start();
    }

    /// <summary>One check: restart now, keep waiting, or stop asking.</summary>
    /// <param name="sender">The timer.</param>
    /// <param name="args">Nothing.</param>
    private void SilentRestartTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (silentRestartInProgress)
        {
            return;
        }

        switch (SilentRestartRules.Decide(CaptureSilentRestartState()))
        {
            case SilentRestartDecision.NotNeeded:
                sender.Stop();
                break;
            case SilentRestartDecision.Now:
                sender.Stop();
                _ = RestartSilentlyAsync();
                break;
        }
    }

    /// <summary>The facts <see cref="SilentRestartRules"/> decides on, as of now. UI thread only.</summary>
    /// <returns>The state.</returns>
    private SilentRestartState CaptureSilentRestartState()
    {
        var current = settings?.Current;
        return new SilentRestartState
        {
            SettingsUsed = settingsUsedSinceStart && !silentRestartGivenUp,
            Exiting = exiting,
            LanguageWouldBeAsked = current is null || LanguageChoice.ShouldAsk(current.LanguageChosen, current.InterfaceLanguage),
            SettingsOpen = settingsWindow is not null,
            LanguagePickerOpen = languageWindow is not null,
            PanelOpen = flyout?.IsOpen == true,
            UpdateInProgress = UpdateStatus?.IsInstalling == true,
            CommandLineRequests = commandLine?.ActiveConnections ?? 0,
            SinceSettingsClosed = settingsClosedAt is { } closed ? TimeSpan.FromMilliseconds(Environment.TickCount64 - closed) : TimeSpan.Zero,
            SinceInput = UserActivity.SinceLastInput(),
            KeysOrButtonsHeld = UserActivity.AnyModifierOrButtonHeld(),
        };
    }

    /// <summary>
    /// Replaces this process with a fresh one, unseen (see the class remarks). Never throws: a restart that cannot happen
    /// leaves this process running, and the reason in the log.
    /// </summary>
    /// <returns>A task completing when this process exits, or when the restart was called off or failed.</returns>
    private async Task RestartSilentlyAsync()
    {
        if (exiting || silentRestartInProgress)
        {
            return;
        }

        silentRestartInProgress = true;
        string name = paths.InstanceName;

        // Created before the new process starts, so it finds them; reset, in case a handle from an earlier attempt kept one set.
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, SingleInstance.RestartReadyEventName(name));
        var cancel = new EventWaitHandle(false, EventResetMode.ManualReset, SingleInstance.RestartCancelEventName(name));
        ready.Reset();
        cancel.Reset();
        QuietProcess process;
        try
        {
            using var self = Process.GetCurrentProcess();
            var target = new RestartTarget(self.Id, self.StartTime.ToUniversalTime().Ticks);
            string sequence = (monitor?.LastHandledSequence ?? 0).ToString(CultureInfo.InvariantCulture);
            string arguments = string.Join(
                ' ',
                StartupRegistration.BackgroundSwitch,
                StartupOptions.RestartSwitch,
                target.ToArgument(),
                StartupOptions.ClipboardSequenceSwitch,
                sequence);
            process = QuietProcess.Start(ExecutablePath, arguments, AppContext.BaseDirectory);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or ArgumentException)
        {
            AppLog.Error("Silent restart: the new process did not start, so this one keeps running (no more attempts).", ex);
            cancel.Dispose();
            silentRestartGivenUp = true;
            silentRestartInProgress = false;
            return;
        }

        // On the pool: the panel must stay instant while the new process gets ready (a Win+V in this moment still opens it here,
        // and calls the restart off below).
        bool readyInTime = await Task.Run(() => ready.WaitOne(RestartReadyTimeout)).ConfigureAwait(true);
        if (process.HasExited)
        {
            AppLog.Warn("Silent restart: the new process ended before it took over, so this one keeps running (no more attempts).");
            process.Dispose();
            cancel.Dispose();
            silentRestartGivenUp = true;
            silentRestartInProgress = false;
            return;
        }

        if (SilentRestartRules.CallOffReason(CaptureSilentRestartState()) is { } reason)
        {
            // The new process ends quietly once it reads the signal. The event and the process handle stay open until it has
            // (or a grace period has passed): a named event exists only while a handle to it is open, and a new process that
            // never read the signal would wait for this one and hold the shortcuts meanwhile.
            cancel.Set();
            AppLog.Info($"Silent restart called off: {reason}. Trying again once it is quiet.");
            _ = Task.Run(() =>
            {
                using (process)
                using (cancel)
                {
                    process.WaitForExit(RestartCancelGrace);
                }
            });
            silentRestartInProgress = false;
            silentRestartTimer?.Start();
            return;
        }

        int newProcessId = process.Id;
        cancel.Dispose();
        process.Dispose();

        // The new process's icon waits hidden: reveal it and remove ours back to back, so the notification area shows neither
        // two icons nor none (TrayIcon.HandOverTo). When that cannot happen, ours goes now, and the new process shows its own
        // as its controller starts, a moment later.
        bool iconHandedOver = tray?.HandOverTo(newProcessId) == true;
        tray?.Dispose();
        tray = null;

        // Commands sent from now on (a second launch's --show-flyout) wait for the new process instead of reaching this one
        // while it exits.
        StopListeningForCommands?.Invoke();
        var memory = ProcessMemory.Read();
        string shortcuts = readyInTime
            ? "the new process holds the shortcuts"
            : $"the new process did not report within {RestartReadyTimeout.TotalSeconds:0} s, so a shortcut pressed in the next moment may be missed";
        string icon = iconHandedOver
            ? "the tray icon was handed over"
            : "the tray icon could not be handed over, so it is missing for a moment";
        AppLog.Info($"Restarting silently to give back the memory Settings left ({memory.Describe()}); {shortcuts}; {icon}.");
        await ExitAsync();
    }

    /// <summary>
    /// Registers the shortcuts (<see cref="ApplyHotkeyAsync"/>); after a silent restart, then lets the bridge go and opens
    /// the panel for a press it caught during the switch. Never throws (fire-and-forget safe).
    /// </summary>
    /// <returns>A task completing once the shortcuts are applied and the bridge, if any, let go.</returns>
    private async Task ApplyHotkeysAndTakeOverAsync()
    {
        try
        {
            await ApplyHotkeyAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Registering the shortcuts failed.", ex);
        }
        finally
        {
            // Only after ours are registered: the bridge's hook lets go of a shortcut the moment this process holds it, so no
            // press falls between the two.
            if (RestartBridge is { } bridge)
            {
                var (pending, presses) = bridge.TakeOverShortcuts();
                AppLog.Info(bridge.HoldsShortcuts
                    ? $"Silent restart: the shortcuts were held during the switch; {presses} press(es) caught."
                    : "Silent restart: the shortcuts were not held during the switch (keyboard hook not allowed or not installed).");
                if (pending is { } context && !exiting)
                {
                    OnHotkey(context);
                }
            }
        }
    }
}
