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
    /// <c>--show-flyout</c> / <c>--exit</c> drive an already-running instance.
    /// </param>
    /// <returns>Process exit code (0).</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        var options = StartupOptions.Parse(args);
        WinRT.ComWrappersSupport.InitializeComWrappers();

        using var instance = SingleInstance.Acquire("BetterClipboard");
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
