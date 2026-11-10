using System.Globalization;
using BetterClipboard.Windows.Shell;

namespace BetterClipboard.App;

/// <summary>
/// Parsed command line.
/// </summary>
/// <param name="Background">Start silently in the tray (set by the "start with Windows" Run entry).</param>
/// <param name="Command">What a launch asks of an already-running instance (plain launch = <see cref="InstanceCommand.Activate"/>).</param>
/// <param name="FromAllUsersEntry">
/// Started by the sign-in entry an installation for every user of the PC wrote (<see cref="StartupRegistration.AllUsersSwitch"/>):
/// before anything else, <c>Program.Main</c> asks whether this user wants it, and ends the process when not.
/// </param>
/// <param name="RestartAfter">
/// The process this start replaces (<see cref="RestartSwitch"/>): Settings' <em>Restart now</em> starts the new process
/// before the old one exits, and the new one waits for it before it takes the single-instance lock.
/// </param>
public sealed record StartupOptions(bool Background, InstanceCommand Command, bool FromAllUsersEntry = false, RestartTarget? RestartAfter = null)
{
    /// <summary>Switch that opens the flyout in the running instance.</summary>
    public const string ShowFlyoutSwitch = "--show-flyout";

    /// <summary>Switch that asks the running instance to quit.</summary>
    public const string ExitSwitch = "--exit";

    /// <summary>
    /// Switch, followed by <c>pid:startTicks</c>, that makes a start wait for the process it replaces
    /// (<see cref="RestartTarget"/>): Settings' <em>Restart now</em>.
    /// </summary>
    public const string RestartSwitch = "--restart-after";

    /// <summary>
    /// Parses the command line (unknown arguments are ignored so older shortcuts never break startup).
    /// </summary>
    /// <param name="args">Arguments.</param>
    /// <returns>The options. <see cref="StartupRegistration.AllUsersSwitch"/> also counts as a background start: only a sign-in entry passes it.</returns>
    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        bool Has(string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        var command = Has(ExitSwitch) ? InstanceCommand.Exit
            : Has(ShowFlyoutSwitch) ? InstanceCommand.ShowFlyout
            : InstanceCommand.Activate;
        bool fromAllUsers = Has(StartupRegistration.AllUsersSwitch);
        RestartTarget? restartAfter = null;
        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (string.Equals(args[i], RestartSwitch, StringComparison.OrdinalIgnoreCase) && RestartTarget.TryParse(args[i + 1], out var target))
            {
                restartAfter = target;
            }
        }

        return new StartupOptions(Has(StartupRegistration.BackgroundSwitch) || fromAllUsers, command, fromAllUsers, restartAfter);
    }
}

/// <summary>
/// The process a restart replaces, named by its id and its start time: an id alone may be reused by another process
/// the moment the old one ends, and the new start would then wait for a stranger.
/// </summary>
/// <param name="ProcessId">The old process's id.</param>
/// <param name="StartTicks">The old process's start time, as UTC ticks.</param>
public sealed record RestartTarget(int ProcessId, long StartTicks)
{
    /// <summary>The argument that names this process (<c>pid:startTicks</c>), to pass after <see cref="StartupOptions.RestartSwitch"/>.</summary>
    /// <returns>The argument.</returns>
    public string ToArgument() => string.Create(CultureInfo.InvariantCulture, $"{ProcessId}:{StartTicks}");

    /// <summary>Reads the argument <see cref="ToArgument"/> wrote.</summary>
    /// <param name="text">The argument.</param>
    /// <param name="target">The target, when the text is one.</param>
    /// <returns>Whether it is.</returns>
    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RestartTarget? target)
    {
        target = null;
        int colon = text?.IndexOf(':', StringComparison.Ordinal) ?? -1;
        if (colon <= 0
            || !int.TryParse(text.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
            || !long.TryParse(text.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks))
        {
            return false;
        }

        target = new RestartTarget(pid, ticks);
        return true;
    }
}
