using System.Reflection;
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
    /// the installer's way to pick the shortcuts, <c>--set-update-check on|off</c> its way to install with the
    /// automatic update check off, and <c>--apply-portable-update …</c> (<see cref="Core.Updates.PortableUpdate"/>) makes
    /// this program the helper that puts a new version into a portable copy's folder (see remarks).
    /// </param>
    /// <returns>
    /// Process exit code (0; the helper's own codes for <c>--read-console-history</c>,
    /// <see cref="BetterClipboard.Windows.Input.HotkeySetupCommand"/>'s for <c>--set-hotkeys</c>,
    /// <see cref="Core.Updates.UpdateSetupCommand"/>'s for <c>--set-update-check</c>, and
    /// <see cref="BetterClipboard.Windows.Updates.PortableUpdateCommand"/>'s for <c>--apply-portable-update</c>).
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
    /// <para>
    /// <c>--apply-portable-update</c> runs first of all, before the lock: it is the <b>new</b> version, started from its
    /// staging folder by the copy it replaces, which still holds the lock until this helper asks it to exit
    /// (<see cref="ApplyPortableUpdate"/>).
    /// </para>
    /// <para>
    /// <b>A newer copy takes over.</b> A plain launch that finds an older BetterClipboard running asks it to exit and becomes
    /// BetterClipboard itself, instead of opening the older copy's Settings (<see cref="TakeOverFromOlderCopy"/>,
    /// CLAUDE.md §2.29).
    /// </para>
    /// </remarks>
    [STAThread]
    public static int Main(string[] args)
    {
        if (BetterClipboard.Windows.Updates.PortableUpdateCommand.Matches(args))
        {
            return ApplyPortableUpdate(args);
        }

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

        // Where this process keeps its data decides the instance's name: an isolated dev/test run
        // (BETTERCLIPBOARD_DATA_DIR) and the regular instance never share a lock or command events. Resolved once,
        // before anything waits on a named object; the Microsoft Store edition's folder comes from its package
        // (a kernel32 call, so it needs neither COM nor XAML).
        var paths = ResolvePaths();

        // A silent restart (CLAUDE.md §2.46) first takes the shortcuts and the tray icon over from the old process, so nothing
        // goes missing while the two change places, then waits for the old one to end.
        RestartBridge? bridge = null;
        IDisposable? keptCommands = null;
        if (options.IsSilentRestart && options.RestartAfter is { } silentlyReplaced)
        {
            string name = paths.InstanceName;
            keptCommands = SingleInstance.KeepCommandsAlive(name);
            bridge = RestartBridge.Start(paths);
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

        // The Microsoft Store edition (CLAUDE.md §2.47) starts with Windows through its startup task, which runs the
        // program without arguments: such a start stays in the tray like one with --background.
        if (!options.Background && options.Command == InstanceCommand.Activate && args.Length == 0
            && BetterClipboard.Windows.Shell.PackagedStartup.WasStartedByTask())
        {
            options = options with { Background = true };
        }

        // Scoped by data directory: an isolated dev/test run (BETTERCLIPBOARD_DATA_DIR) gets its own lock and
        // command events, so it neither blocks nor receives the installed app's --exit/--show-flyout. The Microsoft
        // Store edition's regular instance shares the lock with every other edition and marks it while it holds it
        // (CLAUDE.md §2.47).
        bool storeEdition = BetterClipboard.Windows.Packaging.PackageIdentity.IsPackaged && paths.InstanceScope is null;

        // Before the lock, like the all-users entry above: a Store edition that Windows started while it still has
        // something to settle with the user must never hold the lock, not even for the moment it takes to find out.
        // Another installed copy's own sign-in start would find BetterClipboard "already running" and end, and then
        // none would run.
        if (storeEdition && options.Background && !options.IsSilentRestart && options.Command == InstanceCommand.Activate
            && !ContinueStoreEditionInBackground(paths))
        {
            return 0;
        }

        using var instance = SingleInstance.Acquire(paths.InstanceName, storeEdition);

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

            // The Store edition, started by hand while another edition is the running copy: handing over would open that
            // copy's Settings, and the user would never see the edition they started. It goes on instead, without the
            // lock, into its setup, which says what it found and replaces that copy (AppController.Store.cs).
            bool replacesAnotherEdition = storeEdition && options.Command == InstanceCommand.Activate && !instance.PrimaryIsStoreEdition();
            if (replacesAnotherEdition && !instance.TryClaimStoreSetup())
            {
                // Another process of this edition already has its setup window open (the lock is still the other
                // copy's, so the usual hand-over cannot reach it): it was asked to show that window.
                return 0;
            }

            if (!replacesAnotherEdition)
            {
                // A newer copy started by hand while an older one runs (a release extracted into a new folder and opened
                // from there): it takes the older one's place, or the user would get the old version's Settings and keep
                // running it (seen in the portable-update test, CLAUDE.md §2.29).
                if (!storeEdition && options.Command == InstanceCommand.Activate && TakeOverFromOlderCopy(instance, out var olderCopies))
                {
                    options = options with { TookOverFrom = olderCopies };
                }
                else
                {
                    // A second launch (Start menu, double click, script) drives the running app instead of
                    // starting a twin that would fight over the clipboard listener and the Win+V hook.
                    instance.Signal(options.Command);
                    return 0;
                }
            }
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
    /// The data locations of this process: the folder in <c>BETTERCLIPBOARD_DATA_DIR</c> when set, the Microsoft Store
    /// package's own folder when the process runs from that package (CLAUDE.md §2.47), otherwise
    /// <c>%LOCALAPPDATA%\BetterClipboard</c>. One place, so the app, its setup commands and its sign-in check can never
    /// disagree about it.
    /// </summary>
    /// <returns>The paths.</returns>
    internal static Core.AppPaths ResolvePaths() =>
        Core.AppPaths.ResolveDefault(BetterClipboard.Windows.Packaging.PackageIdentity.FamilyName);

    /// <summary>How long a newer copy waits for the older one it asked to exit (which saves its queued copies first).</summary>
    private static readonly TimeSpan TakeOverWait = TimeSpan.FromMinutes(2);

    /// <summary>
    /// This program's version, as its build stamped it, without the build metadata (<c>0.2.11</c> from <c>0.2.11+abc…</c>).
    /// Read here rather than from <see cref="AppController.Version"/>: before XAML starts, no class of the UI is touched.
    /// </summary>
    /// <returns>The version text.</returns>
    private static string OwnVersionText() =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>
    /// Runs this program as a portable copy's update helper (<c>--apply-portable-update</c>, CLAUDE.md §2.29): this process
    /// is the new version, started from its staging folder by the copy it replaces.
    /// </summary>
    /// <param name="args">The process's arguments, starting with <see cref="Core.Updates.PortableUpdate.ApplySwitch"/>.</param>
    /// <returns>One of <see cref="BetterClipboard.Windows.Updates.PortableUpdateCommand"/>'s exit codes.</returns>
    /// <remarks>
    /// The data folder is resolved as the app resolves it (the helper inherits the old copy's environment, an isolated
    /// instance's <c>BETTERCLIPBOARD_DATA_DIR</c> included), so the exit request reaches exactly the instance that asked for
    /// the update. Nothing here takes the lock: the old copy holds it until it exits, and the new version started afterwards
    /// takes it as any start does.
    /// </remarks>
    private static int ApplyPortableUpdate(string[] args)
    {
        if (!BetterClipboard.Windows.Updates.PortableUpdateCommand.TryParse(args, out var request))
        {
            return BetterClipboard.Windows.Updates.PortableUpdateCommand.BadArguments;
        }

        var paths = ResolvePaths();
        var helper = new BetterClipboard.Windows.Updates.PortableUpdateHelper
        {
            SourceDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            Version = OwnVersionText(),
            RequestExit = () => SingleInstance.TrySignal(paths.InstanceName, InstanceCommand.Exit),
        };
        return helper.Run(request);
    }

    /// <summary>
    /// When a plain launch finds BetterClipboard running and every other copy in the session is an older version
    /// (<see cref="Core.Startup.InstanceTakeover"/>): asks the running copy to exit and takes the single-instance lock,
    /// so this newer copy becomes BetterClipboard.
    /// </summary>
    /// <param name="instance">This process's guard, not the primary one yet.</param>
    /// <param name="olderCopies">For the log: the copies that were replaced, e.g. <c>0.2.10 (process 4312)</c>.</param>
    /// <returns>
    /// <see langword="true"/> once this process holds the lock; <see langword="false"/> to hand the launch over as before
    /// (no newer version, something unclear, or the running copy did not exit within <see cref="TakeOverWait"/>).
    /// </returns>
    /// <remarks>
    /// <para>
    /// Runs on the thread that acquired the lock (a mutex belongs to a thread). The exit request goes through the instance's
    /// own event, so only the copy that holds this instance's lock receives it; every version understands it. Runs before
    /// the log is opened (the running copy writes to the same file), so the controller logs it, from
    /// <see cref="StartupOptions.TookOverFrom"/>.
    /// </para>
    /// <para>
    /// A running copy that does not exit in time is left as it is: a request it never read is withdrawn (the commands are
    /// reset), and the launch is handed over as a plain one, which opens its Settings.
    /// </para>
    /// </remarks>
    private static bool TakeOverFromOlderCopy(SingleInstance instance, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? olderCopies)
    {
        olderCopies = null;
        try
        {
            if (!Core.Updates.SemanticVersion.TryParse(OwnVersionText(), out var own) || Environment.ProcessPath is not { } ownPath)
            {
                return false;
            }

            var others = BetterClipboard.Windows.Shell.RunningCopies.ListOthers();
            if (!Core.Startup.InstanceTakeover.ShouldTakeOver(own, ownPath, others))
            {
                return false;
            }

            instance.Signal(InstanceCommand.Exit);
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (waited.Elapsed < TakeOverWait)
            {
                if (instance.TryBecomePrimary())
                {
                    // Commands sent to the copy that is gone (a launch in the moment between) are not for this one.
                    instance.DiscardPendingCommands();
                    olderCopies = string.Join(", ", others.Select(copy => $"{copy.Version} (process {copy.ProcessId})"));
                    return true;
                }

                Thread.Sleep(100);
            }

            instance.DiscardPendingCommands();
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or IOException)
        {
            // The process list or the lock could not be read: hand over as always.
            return false;
        }
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
            var paths = ResolvePaths();
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
    /// Whether a Microsoft Store edition that Windows started — at sign-in through its startup task, or again after the
    /// Store updated it — should become BetterClipboard: not before its welcome was accepted (nothing may be recorded
    /// without the user's yes, CLAUDE.md §2.47), and not while another copy of BetterClipboard is installed (only one
    /// edition is meant to be installed; which one stays is settled with the user, in a window, and a start by Windows
    /// never opens one).
    /// </summary>
    /// <param name="paths">This process's data locations (the package's own folder).</param>
    /// <returns><see langword="true"/> to go on starting; <see langword="false"/> to end the process at once.</returns>
    /// <remarks>
    /// <para>
    /// Runs before the single-instance lock, before COM and before any XAML: a settings snapshot (read without the
    /// store, never setting an unreadable file aside) and a few registry reads. Settings that cannot be read count as
    /// "no welcome yet": consent that cannot be shown is asked for again, when the user opens BetterClipboard by hand.
    /// </para>
    /// <para>
    /// The decision is logged, content-free, so "it did not start at sign-in" can be explained afterwards. The other
    /// copy, when it has its own sign-in entry, is the one that runs; opened by hand, this edition offers to take its
    /// place (<c>AppController.Store.cs</c>).
    /// </para>
    /// </remarks>
    private static bool ContinueStoreEditionInBackground(Core.AppPaths paths)
    {
        bool welcomed = Core.Settings.SettingsStore.TryReadSnapshot(paths.SettingsPath)?.StoreWelcomeAccepted == true;
        var installed = BetterClipboard.Windows.Store.OtherEditionLocator.Find(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), regularInstanceHeldByAnother: false);
        if (welcomed && installed.Count == 0)
        {
            return true;
        }

        try
        {
            paths.EnsureCreated();
            Core.Diagnostics.AppLog.Initialize(paths.LogDirectory);
            try
            {
                Core.Diagnostics.AppLog.Info(installed.Count > 0
                    ? $"Microsoft Store edition: not started in the background: another copy of BetterClipboard is installed ({string.Join(", ", installed.Select(e => e.Kind))}). Opened by hand, this edition offers to take its place."
                    : "Microsoft Store edition: not started in the background: its welcome was not accepted yet, so nothing is recorded. The welcome shows when BetterClipboard is opened by hand.");
            }
            finally
            {
                Core.Diagnostics.AppLog.Shutdown();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log could not be written; the decision stands without its note.
        }

        return false;
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
        var paths = ResolvePaths();
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
        var paths = ResolvePaths();
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
