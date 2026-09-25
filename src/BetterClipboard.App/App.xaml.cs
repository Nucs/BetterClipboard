using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace BetterClipboard.App;

/// <summary>
/// The WinUI application object: hosts <see cref="AppController"/> and keeps the process alive as a
/// tray app even when no window is open.
/// </summary>
public partial class App : Application
{
    private readonly StartupOptions options;
    private readonly SingleInstance instance;
    private AppController? controller;

    /// <summary>
    /// Creates the application.
    /// </summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="instance">The (primary) single-instance guard, used to receive activation requests.</param>
    public App(StartupOptions options, SingleInstance instance)
    {
        this.options = options;
        this.instance = instance;
        InitializeComponent();

        // A tray utility must survive closing its last window; only "Exit" ends the process.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Error("Unhandled exception (process terminating).", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("Unobserved task exception.", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Creates the controller and starts capturing.</summary>
    /// <param name="args">Launch arguments (unused; see <see cref="StartupOptions"/>).</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        controller = new AppController(dispatcher, options);
        try
        {
            controller.Start();
        }
        catch (Exception ex)
        {
            // Without storage or the clipboard listener the app is useless; log and quit cleanly
            // rather than lingering as a tray icon that silently records nothing.
            AppLog.Error("Startup failed.", ex);
            AppLog.Shutdown();
            Exit();
            return;
        }

        instance.Listen(command => dispatcher.TryEnqueue(() =>
        {
            switch (command)
            {
                case InstanceCommand.ShowFlyout:
                    // Captured on the UI thread right after the signal: the foreground window is still
                    // whatever the user was working in, so pasting goes back there.
                    controller.ShowFlyout(ForegroundContext.Capture());
                    break;
                case InstanceCommand.Exit:
                    _ = controller.ExitAsync();
                    break;
                default:
                    controller.ShowSettings();
                    break;
            }
        }));
    }

    /// <summary>
    /// Logs UI exceptions and keeps running: a glitch in a view must not stop clipboard capture.
    /// </summary>
    /// <param name="sender">The application.</param>
    /// <param name="e">Exception data.</param>
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled UI exception.", e.Exception);
        e.Handled = true;
    }
}
