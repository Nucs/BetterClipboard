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
    /// Command line: <c>--background</c> starts without showing a window (used by "start with Windows");
    /// <c>--show-flyout</c> / <c>--exit</c> drive an already-running instance;
    /// <c>--read-console-history &lt;pid&gt;</c> is the Cmd tab's helper, and <c>--set-hotkeys [--validate] SHORTCUT...</c>
    /// the installer's way to pick the shortcuts (see remarks).
    /// </param>
    /// <returns>
    /// Process exit code (0; the helper's own codes for <c>--read-console-history</c>, and
    /// <see cref="BetterClipboard.Windows.Input.HotkeySetupCommand"/>'s for <c>--set-hotkeys</c>).
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

        var options = StartupOptions.Parse(args);
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Scoped by data directory: an isolated dev/test run (BETTERCLIPBOARD_DATA_DIR) gets its own lock and
        // command events, so it neither blocks nor receives the installed app's --exit/--show-flyout.
        using var instance = SingleInstance.Acquire(Core.AppPaths.ResolveDefault().InstanceName);
        if (!instance.IsPrimary)
        {
            // A second launch (Start menu, double click, script) drives the running app instead of
            // starting a twin that would fight over the clipboard listener and the Win+V hook.
            instance.Signal(options.Command);
            return 0;
        }

        if (options.Command == InstanceCommand.Exit)
        {
            return 0; // "--exit" with nothing running: nothing to do.
        }

        Application.Start(initialization =>
        {
            // async/await continuations in the app must resume on the UI thread.
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);

            // The XAML framework keeps the Application alive (Application.Current); no reference needed here.
            new App(options, instance);
        });

        return 0;
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
}
