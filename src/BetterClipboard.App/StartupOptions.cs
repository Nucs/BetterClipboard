using BetterClipboard.Windows.Shell;

namespace BetterClipboard.App;

/// <summary>
/// Parsed command line.
/// </summary>
/// <param name="Background">Start silently in the tray (set by the "start with Windows" Run entry).</param>
/// <param name="Command">What a launch asks of an already-running instance (plain launch = <see cref="InstanceCommand.Activate"/>).</param>
public sealed record StartupOptions(bool Background, InstanceCommand Command)
{
    /// <summary>Switch that opens the flyout in the running instance.</summary>
    public const string ShowFlyoutSwitch = "--show-flyout";

    /// <summary>Switch that asks the running instance to quit.</summary>
    public const string ExitSwitch = "--exit";

    /// <summary>
    /// Parses the command line (unknown arguments are ignored so older shortcuts never break startup).
    /// </summary>
    /// <param name="args">Arguments.</param>
    /// <returns>The options.</returns>
    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        bool Has(string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        var command = Has(ExitSwitch) ? InstanceCommand.Exit
            : Has(ShowFlyoutSwitch) ? InstanceCommand.ShowFlyout
            : InstanceCommand.Activate;
        return new StartupOptions(Has(StartupRegistration.BackgroundSwitch), command);
    }
}
