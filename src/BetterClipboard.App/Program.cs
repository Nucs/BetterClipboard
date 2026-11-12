using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace BetterClipboard.App;

/// <summary>
/// Custom entry point (the XAML-generated <c>Main</c> is disabled via <c>DISABLE_XAML_GENERATED_MAIN</c>)
/// so a second launch can hand over to the running instance <b>before</b> any XAML is initialized.
/// </summary>
public static class Program
{
    /// <summary>
    /// Starts BetterClipboard, or activates the already-running instance and exits.
    /// </summary>
    /// <param name="args">
    /// Command line: <c>--background</c> starts without showing a window (used by "start with Windows"), and does
    /// nothing when BetterClipboard already runs; <c>--all-users-startup</c> (with it, from the sign-in entry an
    /// installation for every user wrote) first asks whether this user wants it (<see cref="ContinueFromAllUsersEntry"/>);
    /// <c>--show-flyout</c> / <c>--exit</c> drive an already-running instance;
    /// <c>--restart-after pid:startTicks</c> (from Settings' <em>Restart now</em>) waits for the process it replaces, and with
    /// <c>--background</c> (and <c>--clipboard-sequence N</c>) is the silent restart after Settings, which first takes the
    /// shortcuts and the tray icon over (<see cref="RestartBridge"/>, CLAUDE.md §2.46);
    /// <c>--read-console-history &lt;pid&gt;</c> is the Cmd tab's helper, <c>--set-hotkeys [--validate] SHORTCUT...</c>
    /// the installer's way to pick the shortcuts, and <c>--set-update-check on|off</c> its way to install with the
    /// automatic update check off (see remarks).
    /// </param>
    /// <returns>
    /// Process exit code (0; the helper's own codes for <c>--read-console-history</c>,
    /// <see cref="BetterClipboard.Windows.Input.HotkeySetupCommand"/>'s for <c>--set-hotkeys</c>, and
    /// <see cref="Core.Updates.UpdateSetupCommand"/>'s for <c>--set-update-check</c>).
    /// </returns>
    /// <remarks>
    /// <para>
    /// The helper mode runs first and alone: no single-instance lock, no XAML, no settings or store — it attaches to
    /// one console, reads its cmd history, detaches and writes it to the standard output the running app gave it
    /// (<see cref="BetterClipboard.Windows.Shell.ConsoleCommandHistory"/>). It is a separate process because a console that closes
    /// while a process is attached ends that process, and that must never be the app itself.
    /// </para>
    /// <para>
    /// <c>--set-hotkeys</c> runs before the lock too: it must answer while the installer has the app closed, and it must
    /// never hand the request to a running instance as a plain launch would (that opens Settings). It refuses to save
    /// while that instance runs (<see cref="BetterClipboard.Windows.Input.HotkeySetupCommand"/>).
    /// </para>
    /// <para>
    /// <c>--set-update-check</c> runs before the lock for the same reasons (<see cref="Core.Updates.UpdateSetupCommand"/>):
    /// the installer switches the check off while the app is closed, so that not even a first request is made.
    /// </para>
    /// </remarks>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == BetterClipboard.Windows.Shell.ConsoleCommandHistory.HelperSwitch)
        {
            // Open standard output before attaching: attaching to a console may swap the process's console handles,
            // and the app reads the result from the pipe it handed over at start.
            using var output = Console.OpenStandardOutput();
            return BetterClipboard.Windows.Shell.ConsoleCommandHistory.RunHelper(args, output);
        }

        if (BetterClipboard.Windows.Input.HotkeySetupCommand.Matches(args))
        {
            return SetHotkeys(args);
        }

        if (Core.Updates.UpdateSetupCommand.Matches(args))
        {
            return SetUpdateCheck(args);
        }

        var options = StartupOptions.Parse(args);

        // Before the single-instance lock: a process that steps aside must never hold the lock the user's own sign-in
        // start needs, or that start would find BetterClipboard "already running" and end too.
        if (options.FromAllUsersEntry && !ContinueFromAllUsersEntry())
        {
            return 0;
        }

        // A silent restart (CLAUDE.md §2.46) first takes the shortcuts and the tray icon over from the old process, so nothing
        // goes missing while the two change places, then waits for the old one to end.
        RestartBridge? bridge = null;
        IDisposable? keptCommands = null;
        if (options.IsSilentRestart && options.RestartAfter is { } silentlyReplaced)
        {
            string name = Core.AppPaths.ResolveDefault().InstanceName;
            keptCommands = SingleInstance.KeepCommandsAlive(name);
            bridge = RestartBridge.Start(Core.AppPaths.ResolveDefault());
            SetNamedEvent(SingleInstance.RestartReadyEventName(name));
            if (!WaitForSilentlyReplacedProcess(silentlyReplaced, SingleInstance.RestartCancelEventName(name)))
            {
                // Called off (the panel opened meanwhile) or the old process never ended: end quietly. Never go on to hand a
                // plain launch to the old process, which would open its Settings.
                bridge.Dispose();
                keptCommands.Dispose();
                return 0;
            }
        }

        // Settings' Restart now starts this process before the old one exits: wait for it, so this start becomes the app
        // instead of handing a plain launch to the instance that is about to quit (which would open its Settings again).
        else if (options.RestartAfter is { } replaced)
        {
            WaitForReplacedProcess(replaced);
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Scoped by data directory: an isolated dev/test run (BETTERCLIPBOARD_DATA_DIR) gets its own lock and
        // command events, so it neither blocks nor receives the installed app's --exit/--show-flyout.
        using var instance = SingleInstance.Acquire(Core.AppPaths.ResolveDefault().InstanceName);

        // The lock's own handles to the command events now keep a command sent during the switch.
        keptCommands?.Dispose();
        if (!instance.IsPrimary)
        {
            if (bridge is not null)
            {
                // Another start became BetterClipboard in the moment between the two processes (bclip starts one when it finds
                // none): nothing to take over, and nothing to show.
                bridge.Dispose();
                return 0;
            }

            if (options.Background && options.Command == InstanceCommand.Activate)
            {
                // A start into the tray that finds BetterClipboard running has nothing left to do. Signalled, it would
                // open Settings in the running app: at every sign-in, whenever two entries start it (the user's and one
                // for every user), and after an update or upgrade that finds another copy running.
                return 0;
            }

            // A second launch (Start menu, double click, script) drives the running app instead of
            // starting a twin that would fight over the clipboard listener and the Win+V hook.
            instance.Signal(options.Command);
            return 0;
        }

        if (options.Command == InstanceCommand.Exit)
        {
            bridge?.Dispose();
            return 0; // "--exit" with nothing running: nothing to do.
        }

        Application.Start(initialization =>
        {
            // async/await continuations in the app must resume on the UI thread.
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);

            // The XAML framework keeps the Application alive (Application.Current); no reference needed here.
            new App(options, instance, bridge);
        });

        return 0;
    }

    /// <summary>
    /// Sets a named event another process created (a silent restart's "ready" signal); nothing happens when it does not
    /// exist (the old process ended already, or it is not waiting).
    /// </summary>
    /// <param name="name">The event's kernel object name.</param>
    private static void SetNamedEvent(string name)
    {
        if (EventWaitHandle.TryOpenExisting(name, out var signal))
        {
            using (signal)
            {
                signal.Set();
            }
        }
    }

    /// <summary>
    /// Waits for the process a silent restart replaces to end, or for it to call the restart off (CLAUDE.md §2.46).
    /// </summary>
    /// <param name="target">The process, by id and start time.</param>
    /// <param name="cancelEventName">The event the old process sets when it calls the restart off.</param>
    /// <returns>
    /// <see langword="true"/> when the old process has ended (or was gone already): take over. <see langword="false"/> when it
    /// called the restart off, or was still there after two minutes: end quietly.
    /// </returns>
    /// <remarks>
    /// Unlike <see cref="WaitForReplacedProcess"/>, a process still running after the wait is never handed a plain launch:
    /// that would open its Settings, and a silent restart shows nothing. Two minutes, not thirty seconds: once it has
    /// started to exit, the old process drains its queued captures and integrations first, and leaving early here would
    /// leave nobody once it is done. Runs before the log is opened, so it logs nothing.
    /// </remarks>
    private static bool WaitForSilentlyReplacedProcess(RestartTarget target, string cancelEventName)
    {
        EventWaitHandle? cancel = EventWaitHandle.TryOpenExisting(cancelEventName, out var opened) ? opened : null;
        try
        {
            using var old = System.Diagnostics.Process.GetProcessById(target.ProcessId);
            if (old.StartTime.ToUniversalTime().Ticks != target.StartTicks)
            {
                return true; // a stranger with the old id: the old process ended already
            }

            using var exited = new ProcessExitHandle(old.Handle);
            WaitHandle[] handles = cancel is null ? [exited] : [exited, cancel];
            return WaitHandle.WaitAny(handles, TimeSpan.FromMinutes(2)) == 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // ArgumentException: it has already ended. The others: it ended while being asked, or cannot be read.
            return true;
        }
        finally
        {
            cancel?.Dispose();
        }
    }

    /// <summary>
    /// A process handle as a <see cref="WaitHandle"/> (signaled when the process ends), so its end can be waited on together
    /// with an event. Borrows the handle: the <see cref="System.Diagnostics.Process"/> that owns it closes it.
    /// </summary>
    private sealed class ProcessExitHandle : WaitHandle
    {
        /// <summary>Wraps the handle without taking ownership.</summary>
        /// <param name="processHandle">A process handle with SYNCHRONIZE access.</param>
        public ProcessExitHandle(nint processHandle) =>
            SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(processHandle, ownsHandle: false);
    }

    /// <summary>
    /// Waits, at most 30 seconds, for the process a restart replaces to end: an exit drains queued captures first, which
    /// usually takes well under a second.
    /// </summary>
    /// <param name="target">The process, by id and start time.</param>
    /// <remarks>
    /// A process with that id but another start time is a stranger that got the id after the old one ended: nothing to
    /// wait for. When the old process is still there after 30 seconds, the start goes on; the single-instance lock then
    /// hands it to the old process as a plain launch, which opens its Settings, so the restart is never lost silently.
    /// Runs before the log is opened, so it logs nothing (the controller logs that it was a restart).
    /// </remarks>
    private static void WaitForReplacedProcess(RestartTarget target)
    {
        try
        {
            using var old = System.Diagnostics.Process.GetProcessById(target.ProcessId);
            if (old.StartTime.ToUniversalTime().Ticks == target.StartTicks)
            {
                old.WaitForExit(TimeSpan.FromSeconds(30));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // ArgumentException: it has already ended. The others: it ended while being asked, or cannot be read; either
            // way there is nothing left to wait for.
        }
    }

    /// <summary>
    /// Whether a process that the all-users sign-in entry started should become BetterClipboard for this user: not when
    /// the user turned "Start with Windows" off in its Settings (<see cref="Core.Settings.AppSettings.SkipAllUsersStartup"/>),
    /// and not when the user has an entry of their own, which starts BetterClipboard itself or was turned off in Windows'
    /// startup apps list (<see cref="Core.Startup.SignInStartup.ShouldContinueFromAllUsersEntry"/>).
    /// </summary>
    /// <returns><see langword="true"/> to go on starting; <see langword="false"/> to end the process at once.</returns>
    /// <remarks>
    /// <para>
    /// Runs before the single-instance lock, before COM and before any XAML, so stepping aside costs one short process at
    /// sign-in. The settings are read without the store (<see cref="Core.Settings.SettingsStore.TryReadSnapshot"/>, which
    /// never sets an unreadable file aside), and the decision is logged, content-free, so "it did not start" can be
    /// explained afterwards.
    /// </para>
    /// <para>
    /// It fails open: when the facts cannot be read, the process starts, as the entry asks. One BetterClipboard too many
    /// costs a moment; none at all would cost the user's copies until they notice.
    /// </para>
    /// </remarks>
    private static bool ContinueFromAllUsersEntry()
    {
        try
        {
            var paths = Core.AppPaths.ResolveDefault();
            var settings = Core.Settings.SettingsStore.TryReadSnapshot(paths.SettingsPath);
            bool skip = settings?.SkipAllUsersStartup ?? false;
            if (Core.Startup.SignInStartup.ShouldContinueFromAllUsersEntry(BetterClipboard.Windows.Shell.StartupRegistration.ReadEntries(), skip))
            {
                return true;
            }

            Core.Diagnostics.AppLog.Initialize(paths.LogDirectory);
            try
            {
                Core.Diagnostics.AppLog.Info(skip
                    ? "Not started by the sign-in entry for every user: this user turned Start with Windows off in Settings."
                    : "Not started by the sign-in entry for every user: this user's own entry starts BetterClipboard, or was turned off in Windows' startup apps.");
            }
            finally
            {
                Core.Diagnostics.AppLog.Shutdown();
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return true;
        }
    }

    /// <summary>
    /// Runs <c>--set-hotkeys</c> against this data folder (the same <c>BETTERCLIPBOARD_DATA_DIR</c> resolution as the app)
    /// and writes its answer to the standard output the caller redirected.
    /// </summary>
    /// <param name="args">The process's arguments, starting with <c>--set-hotkeys</c>.</param>
    /// <returns>The command's exit code.</returns>
    /// <remarks>
    /// The log is opened only when the command may save, so the installer's <c>--validate</c> run from its staging folder
    /// leaves no trace in the user's log. UTF-8 without a byte order mark and LF line ends: PowerShell 5.1 and 7 both read
    /// that from a redirected stream without turning a BOM into a stray character on the first line.
    /// </remarks>
    private static int SetHotkeys(string[] args)
    {
        var paths = Core.AppPaths.ResolveDefault();
        bool validateOnly = BetterClipboard.Windows.Input.HotkeySetupCommand.IsValidateOnly(args);
        if (!validateOnly)
        {
            Core.Diagnostics.AppLog.Initialize(paths.LogDirectory);
        }

        try
        {
            using var stream = Console.OpenStandardOutput();
            using var output = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true, NewLine = "\n" };
            return BetterClipboard.Windows.Input.HotkeySetupCommand.Run(args, output, paths.SettingsPath, () => SingleInstance.IsRunning(paths.InstanceName));
        }
        finally
        {
            if (!validateOnly)
            {
                Core.Diagnostics.AppLog.Shutdown();
            }
        }
    }

    /// <summary>
    /// Runs <c>--set-update-check on|off</c> against this data folder (the same <c>BETTERCLIPBOARD_DATA_DIR</c> resolution
    /// as the app) and writes its answer to the standard output the caller redirected.
    /// </summary>
    /// <param name="args">The process's arguments, starting with <c>--set-update-check</c>.</param>
    /// <returns>The command's exit code (<see cref="Core.Updates.UpdateSetupCommand"/>).</returns>
    /// <remarks>
    /// The log is opened because the command saves a setting, and a change of what the app requests from the network
    /// belongs in the log. Output is UTF-8 without a byte order mark and with LF line ends, like <c>--set-hotkeys</c>:
    /// PowerShell 5.1 and 7 both read that from a redirected stream without a stray first character.
    /// </remarks>
    private static int SetUpdateCheck(string[] args)
    {
        var paths = Core.AppPaths.ResolveDefault();
        Core.Diagnostics.AppLog.Initialize(paths.LogDirectory);
        try
        {
            using var stream = Console.OpenStandardOutput();
            using var output = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true, NewLine = "\n" };
            return Core.Updates.UpdateSetupCommand.Run(args, output, paths.SettingsPath, () => SingleInstance.IsRunning(paths.InstanceName));
        }
        finally
        {
            Core.Diagnostics.AppLog.Shutdown();
        }
    }
}
