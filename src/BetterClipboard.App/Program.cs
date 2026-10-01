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
    /// <c>--read-console-history &lt;pid&gt;</c> is the Cmd tab's helper (see remarks).
    /// </param>
    /// <returns>Process exit code (0; the helper's own codes for <c>--read-console-history</c>).</returns>
    /// <remarks>
    /// The helper mode runs first and alone: no single-instance lock, no XAML, no settings or store — it attaches to
    /// one console, reads its cmd history, detaches and writes it to the standard output the running app gave it
    /// (<see cref="BetterClipboard.Windows.Shell.ConsoleCommandHistory"/>). It is a separate process because a console that closes
    /// while a process is attached ends that process, and that must never be the app itself.
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
}
