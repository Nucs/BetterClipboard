using System.ComponentModel;
using System.Runtime.InteropServices;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Shell;

/// <summary>
/// A process started without Windows' launch feedback (CLAUDE.md §2.46): no "app starting" pointer, no inherited handles,
/// this process's environment and rights. For the silent restart, where the user must not see that anything started.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>.</b> Both of its ways
/// (ShellExecute and CreateProcess) leave the feedback on: Windows shows the busy pointer from the start until the new
/// process asks for its first message, or for a few seconds. A restarting BetterClipboard waits for the old process before
/// it pumps any message, so the pointer would spin under the user's mouse. Only <c>STARTUPINFO.dwFlags</c> =
/// <c>STARTF_FORCEOFFFEEDBACK</c> turns it off, and <see cref="System.Diagnostics.ProcessStartInfo"/> cannot set it.
/// </para>
/// <para>
/// <b>Like ShellExecute otherwise:</b> no handle is inherited (a script that redirected this process's output must not wait
/// for the restarted app), the environment is this process's (an isolated instance comes back as itself), and the working
/// folder is the caller's choice.
/// </para>
/// </remarks>
public sealed class QuietProcess : IDisposable
{
    /// <summary>The process handle; 0 once disposed.</summary>
    private nint handle;

    /// <summary>Wraps a started process.</summary>
    /// <param name="handle">Its handle (owned from now on).</param>
    /// <param name="id">Its id.</param>
    private QuietProcess(nint handle, int id)
    {
        this.handle = handle;
        Id = id;
    }

    /// <summary>The process id.</summary>
    public int Id { get; }

    /// <summary>Whether the process has ended (also <see langword="true"/> after <see cref="Dispose"/>).</summary>
    public bool HasExited => handle == 0 || WaitForSingleObject(handle, 0) == WAIT_OBJECT_0;

    /// <summary>
    /// The process's exit code once it has ended; <see langword="null"/> while it runs, after <see cref="Dispose"/>, or when
    /// Windows cannot tell.
    /// </summary>
    public int? ExitCode =>
        handle != 0 && WaitForSingleObject(handle, 0) == WAIT_OBJECT_0 && GetExitCodeProcess(handle, out uint code) ? unchecked((int)code) : null;

    /// <summary>
    /// Starts a program quietly (see the class remarks).
    /// </summary>
    /// <param name="executablePath">The program's full path.</param>
    /// <param name="arguments">Its arguments, already quoted where they need it (they are appended after the quoted program).</param>
    /// <param name="workingDirectory">Its working folder.</param>
    /// <param name="noConsoleWindow">
    /// Give a console program no console window (<c>CREATE_NO_WINDOW</c>); for tests and helpers. A GUI program, such as
    /// BetterClipboard, ignores it.
    /// </param>
    /// <param name="environment">
    /// Variables to add to (or replace in) this process's environment for the new process only, or <see langword="null"/>
    /// for this process's environment as it is. The way to hand a path to cmd.exe without quoting it into the command line:
    /// cmd expands a variable once, so <c>%</c>, <c>&amp;</c> or <c>^</c> in the path stay literal (<see cref="Packaging.CmdOutsideFileSystem"/>).
    /// </param>
    /// <returns>The started process; dispose it to close its handle (the process keeps running).</returns>
    /// <exception cref="ArgumentException">A path is empty, or a variable's name is empty or holds <c>=</c>.</exception>
    /// <exception cref="Win32Exception">Windows refused to start it (missing file, no access, a program that needs elevation).</exception>
    public static QuietProcess Start(string executablePath, string arguments, string workingDirectory, bool noConsoleWindow = false, IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(executablePath);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        // CreateProcessW may write into the command line, so it gets its own NUL-terminated buffer; the program comes first,
        // quoted, as every program expects in argv[0].
        char[] commandLine = $"\"{executablePath}\" {arguments ?? string.Empty}\0".ToCharArray();
        char[]? block = environment is null ? null : EnvironmentBlock(environment);
        var startup = new STARTUPINFOW { cb = (uint)Marshal.SizeOf<STARTUPINFOW>(), dwFlags = STARTF_FORCEOFFFEEDBACK };
        uint flags = (noConsoleWindow ? CREATE_NO_WINDOW : 0) | (block is null ? 0 : CREATE_UNICODE_ENVIRONMENT);
        bool started;
        PROCESS_INFORMATION info;
        unsafe
        {
            fixed (char* line = commandLine)
            fixed (char* variables = block)
            {
                started = CreateProcess(
                    executablePath,
                    line,
                    0,
                    0,
                    bInheritHandles: false,
                    flags,
                    (nint)variables,
                    workingDirectory,
                    ref startup,
                    out info);
            }
        }

        if (!started)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Starting {Path.GetFileName(executablePath)} failed.");
        }

        // The first thread's handle is of no use here; the process handle is what tells whether it still runs.
        CloseHandle(info.hThread);
        return new QuietProcess(info.hProcess, (int)info.dwProcessId);
    }

    /// <summary>
    /// Builds a Unicode environment block: this process's variables with <paramref name="additions"/> laid over them,
    /// sorted by name without regard to case (what Windows expects of a block), each <c>name=value</c> ended by a NUL, and
    /// one more NUL at the end.
    /// </summary>
    /// <param name="additions">The variables to add or replace.</param>
    /// <returns>The block.</returns>
    /// <exception cref="ArgumentException">A name is empty or holds <c>=</c>.</exception>
    private static char[] EnvironmentBlock(IReadOnlyDictionary<string, string> additions)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && name.Length > 0)
            {
                variables[name] = entry.Value as string ?? string.Empty;
            }
        }

        foreach (var (name, value) in additions)
        {
            if (string.IsNullOrEmpty(name) || name.Contains('=', StringComparison.Ordinal))
            {
                throw new ArgumentException($"'{name}' is not a valid environment variable name.", nameof(additions));
            }

            variables[name] = value ?? string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var (name, value) in variables)
        {
            builder.Append(name).Append('=').Append(value).Append('\0');
        }

        return builder.Append('\0').ToString().ToCharArray();
    }

    /// <summary>Waits for the process to end.</summary>
    /// <param name="timeout">How long to wait at most.</param>
    /// <returns>Whether it ended within the time.</returns>
    public bool WaitForExit(TimeSpan timeout) =>
        handle == 0 || WaitForSingleObject(handle, (uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1)) == WAIT_OBJECT_0;

    /// <summary>Closes the process handle; the process itself keeps running.</summary>
    public void Dispose()
    {
        nint closing = Interlocked.Exchange(ref handle, 0);
        if (closing != 0)
        {
            CloseHandle(closing);
        }
    }
}
