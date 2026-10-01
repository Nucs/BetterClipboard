using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.Shells;

/// <summary>
/// The source-app identities of commands kept from the shell tabs — what their history entries carry, what the
/// tabs' stored halves filter on, and what "Ignored apps" matches.
/// </summary>
/// <remarks>
/// The display names are unique on purpose: a copy made in a terminal is attributed to the terminal's window
/// (Windows Terminal, the console host), never to the shell, so only a keep from a shell tab produces them, and
/// <see cref="Storage.ClipFilter.PowerShell"/> / <see cref="Storage.ClipFilter.Cmd"/> can find a row such a keep
/// bumped by its source name alone. They are SQL literals in the store's filter: never put a quote in them.
/// </remarks>
public static class ShellSources
{
    /// <summary>Display name of commands kept from the Pwsh tab.</summary>
    public const string PowerShellName = "PowerShell";

    /// <summary>Display name of commands kept from the Cmd tab (Windows' own name for <c>cmd.exe</c>'s window).</summary>
    public const string CmdName = "Command Prompt";

    /// <summary>
    /// The source of a kept command. The process name is what "Ignored apps" matches: <c>powershell</c> (so ignoring
    /// <c>powershell.exe</c> also refuses keeps from the Pwsh tab) or <c>cmd</c>.
    /// </summary>
    /// <param name="shell">The shell.</param>
    /// <returns>The source app info (no executable path: the command did not come from one process).</returns>
    public static SourceAppInfo For(ShellKind shell) => shell == ShellKind.PowerShell
        ? new SourceAppInfo("powershell", null, PowerShellName)
        : new SourceAppInfo("cmd", null, CmdName);

    /// <summary>The origin of a kept command.</summary>
    /// <param name="shell">The shell.</param>
    /// <returns><see cref="ClipOrigin.PowerShell"/> or <see cref="ClipOrigin.Cmd"/>.</returns>
    public static ClipOrigin OriginOf(ShellKind shell) => shell == ShellKind.PowerShell ? ClipOrigin.PowerShell : ClipOrigin.Cmd;
}
