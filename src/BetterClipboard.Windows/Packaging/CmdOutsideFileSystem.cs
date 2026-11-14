using System.ComponentModel;
using BetterClipboard.Core.Editions;
using BetterClipboard.Windows.Shell;

namespace BetterClipboard.Windows.Packaging;

/// <summary>
/// The Store edition's view of the real file system, through cmd.exe: a program that is not the package's own runs
/// outside the package's environment, so its <c>mkdir</c>, <c>move</c> and <c>if exist</c> act on the real folders, never
/// on the package's private copies (measured, docs/microsoft-store.md §2). Used by <see cref="StoreDataFolderSetup"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Quiet:</b> started with <see cref="QuietProcess"/> (no busy pointer, no window, no inherited handles), with
/// <c>/d</c> (no AutoRun commands from the registry: on the owner's PC one is set), <c>/e:on</c> (mkdir makes missing
/// parents) and <c>/v:off</c> (no delayed expansion, so a <c>!</c> in a path stays literal).
/// </para>
/// <para>
/// <b>Paths never enter the command line.</b> They are handed over in the variables <see cref="FirstPathVariable"/> and
/// <see cref="SecondPathVariable"/> of the child's own environment; cmd expands a variable once, so a <c>%</c>,
/// <c>&amp;</c>, <c>^</c> or <c>)</c> in a user's folder name stays literal, and nothing typed into a path can become a
/// command.
/// </para>
/// <para>
/// <b>Never prompts:</b> <see cref="Move"/> refuses an existing target itself, because cmd's <c>move</c> would ask before
/// overwriting, and a hidden console has nobody to answer. Every command has a deadline.
/// </para>
/// </remarks>
public sealed class CmdOutsideFileSystem : IOutsideFileSystem
{
    /// <summary>The child's variable that holds the first path.</summary>
    public const string FirstPathVariable = "BETTERCLIPBOARD_OUTSIDE_PATH1";

    /// <summary>The child's variable that holds the second path (<see cref="Move"/>'s target).</summary>
    public const string SecondPathVariable = "BETTERCLIPBOARD_OUTSIDE_PATH2";

    /// <summary>How long one command may take; mkdir, a rename and a look take milliseconds.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    /// <summary>cmd.exe in the system folder (never found through the PATH).</summary>
    private static string Cmd => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    /// <inheritdoc/>
    public bool Exists(string path) =>
        Run($"if exist \"%{FirstPathVariable}%\" (exit 0) else (exit 1)", path) == 0;

    /// <inheritdoc/>
    public bool CreateDirectory(string path) =>
        Run($"mkdir \"%{FirstPathVariable}%\"", path) == 0;

    /// <inheritdoc/>
    public bool Move(string source, string target) =>
        !Exists(target) && Run($"move \"%{FirstPathVariable}%\" \"%{SecondPathVariable}%\"", source, target) == 0;

    /// <summary>
    /// Runs one cmd command with the paths in the child's environment and waits for it.
    /// </summary>
    /// <param name="command">The command after <c>/c</c>, naming the paths only through their variables.</param>
    /// <param name="first">The first path.</param>
    /// <param name="second">The second path, or <see langword="null"/>.</param>
    /// <returns>cmd's exit code, or <see langword="null"/> when it could not be started or did not end in time.</returns>
    /// <exception cref="ArgumentException">A path is empty.</exception>
    private static int? Run(string command, string first, string? second = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(first);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [FirstPathVariable] = Path.GetFullPath(first) };
        if (second is not null)
        {
            environment[SecondPathVariable] = Path.GetFullPath(second);
        }

        try
        {
            using var process = QuietProcess.Start(
                Cmd,
                $"/d /e:on /v:off /c {command}",
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                noConsoleWindow: true,
                environment);

            // A cmd that hangs (it never should: nothing here asks) is left to end by itself; the answer is "not done".
            return process.WaitForExit(Deadline) ? process.ExitCode : null;
        }
        catch (Win32Exception)
        {
            // cmd.exe missing or blocked (a policy can forbid it): the caller sees "not done" and falls back.
            return null;
        }
    }
}
