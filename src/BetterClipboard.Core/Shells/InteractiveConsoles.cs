namespace BetterClipboard.Core.Shells;

/// <summary>
/// Decides which running <c>cmd.exe</c> processes are windows a person typed into, and which were started by tools.
/// Only the former are read for the Cmd tab: every read costs a helper process, and on a developer's machine tools
/// start cmd constantly (158 of them were running on 2026-10-01, every one under node, claude, codex or chrome —
/// CLAUDE.md §2.16).
/// </summary>
/// <remarks>
/// The rule walks up the parent chain through shells (cmd, PowerShell, the console host), which pass on whatever
/// started them, to the first other process: a known place people open shells from (<see cref="Hosts"/>: Explorer,
/// Win+R, Windows Terminal, IDEs and terminal emulators, Task Manager, BetterClipboard's own Run tab) makes it
/// interactive; anything else (a build tool, an AI agent, a browser) does not. A parent that has exited counts as
/// interactive — a window someone started with <c>start cmd</c> from a cmd they then closed — since tools rarely
/// leave a cmd running behind them, and a wrong guess only costs one read of an empty history.
/// </remarks>
public static class InteractiveConsoles
{
    /// <summary>Processes people open shells from (image names, case-insensitive).</summary>
    public static readonly IReadOnlySet<string> Hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe",           // Start menu, Win+R, a shortcut, the address bar
        "WindowsTerminal.exe",    // Windows Terminal (and its Preview) starts each tab's shell itself
        "OpenConsole.exe",
        "Code.exe",               // VS Code's integrated terminal
        "Code - Insiders.exe",
        "Cursor.exe",
        "devenv.exe",             // Visual Studio's terminal
        "rider64.exe",
        "idea64.exe",
        "pycharm64.exe",
        "webstorm64.exe",
        "clion64.exe",
        "goland64.exe",
        "ConEmu64.exe",
        "ConEmu.exe",
        "ConEmuC64.exe",
        "ConEmuC.exe",
        "Tabby.exe",
        "wezterm-gui.exe",
        "alacritty.exe",
        "Hyper.exe",
        "mintty.exe",
        "Far.exe",
        "TOTALCMD64.EXE",
        "TOTALCMD.EXE",
        "doublecmd.exe",
        "Taskmgr.exe",            // Task Manager › Run new task
        "BetterClipboard.exe",    // the Run tab's Ctrl+Enter
        "powershell_ise.exe",
    };

    /// <summary>Processes that pass on whatever started them (a cmd inside a cmd, PowerShell, the console host).</summary>
    public static readonly IReadOnlySet<string> PassThrough = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe",
        "powershell.exe",
        "pwsh.exe",
        "conhost.exe",
    };

    /// <summary>Longest parent chain followed (a guard against a cycle through a reused process id).</summary>
    public const int MaxDepth = 16;

    /// <summary>
    /// Whether a process is an interactive shell window (see class remarks).
    /// </summary>
    /// <param name="processId">The process (a cmd.exe).</param>
    /// <param name="processes">A snapshot: process id → image name and parent id.</param>
    /// <returns><see langword="true"/> when it should be read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processes"/> is <see langword="null"/>.</exception>
    public static bool IsInteractive(uint processId, IReadOnlyDictionary<uint, (string Name, uint Parent)> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        if (!processes.TryGetValue(processId, out var current))
        {
            return false;
        }

        var visited = new HashSet<uint> { processId };
        for (int depth = 0; depth < MaxDepth; depth++)
        {
            // A parent id equal to an id already walked (or 0, the idle process) is a reused id, not a parent.
            if (current.Parent == 0 || !visited.Add(current.Parent) || !processes.TryGetValue(current.Parent, out var parent))
            {
                return true; // the parent has exited (see remarks)
            }

            if (Hosts.Contains(parent.Name))
            {
                return true;
            }

            if (!PassThrough.Contains(parent.Name))
            {
                return false;
            }

            current = parent;
        }

        return false;
    }
}
