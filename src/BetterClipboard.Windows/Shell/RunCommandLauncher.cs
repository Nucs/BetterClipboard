using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BetterClipboard.Windows.Interop;

namespace BetterClipboard.Windows.Shell;

/// <summary>How running a command ended.</summary>
public enum RunCommandOutcome
{
    /// <summary>Windows started it (a program, a document's app, a folder window, a URI handler).</summary>
    Started = 0,

    /// <summary>The user declined the administrator prompt; nothing ran.</summary>
    Cancelled = 1,

    /// <summary>Windows could not run it (not found, no app for the file, access denied, …); nothing ran.</summary>
    Failed = 2,
}

/// <summary>
/// The result of running a command.
/// </summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Win32Error">The Win32 error code when it did not start (0 when it did).</param>
public sealed record RunCommandResult(RunCommandOutcome Outcome, int Win32Error)
{
    /// <summary>The result of a command Windows started.</summary>
    public static readonly RunCommandResult Started = new(RunCommandOutcome.Started, 0);

    /// <summary>
    /// A short, content-free explanation for the panel and the log (it never repeats the command, which may hold a
    /// secret typed into Win+R).
    /// </summary>
    /// <returns>E.g. "Windows cannot find it." or "Cancelled at the administrator prompt."</returns>
    public string Describe() => Outcome switch
    {
        RunCommandOutcome.Started => "Started.",
        RunCommandOutcome.Cancelled => "Cancelled at the administrator prompt.",
        _ => Win32Error switch
        {
            NativeMethods.ERROR_FILE_NOT_FOUND or NativeMethods.ERROR_PATH_NOT_FOUND => "Windows cannot find it.",
            NativeMethods.ERROR_NO_ASSOCIATION => "No app is set to open it.",
            NativeMethods.ERROR_ACCESS_DENIED => "Access is denied.",
            _ => $"Windows could not run it (error {Win32Error}).",
        },
    };
}

/// <summary>
/// What Windows is asked to open for one command: the file (program, document, folder or URI), its arguments and
/// the working directory.
/// </summary>
/// <param name="File">The file, folder or URI.</param>
/// <param name="Arguments">The arguments, or <see langword="null"/> when there are none.</param>
/// <param name="WorkingDirectory">The working directory, or <see langword="null"/> to let Windows choose.</param>
public sealed record RunInvocation(string File, string? Arguments, string? WorkingDirectory);

/// <summary>
/// Runs a command the way the Win+R dialog does — environment variables expanded, URIs and folders opened,
/// programs found through App Paths and PATH — optionally as administrator.
/// </summary>
/// <remarks>
/// <para>
/// <b>Parsing</b> (<see cref="Parse"/>) follows the Run dialog's documented and observed behavior (Command
/// Palette's MIT-licensed port of it was read for reference, nothing copied): expand variables; a URI
/// (<c>shell:startup</c>, <c>ms-settings:display</c>, <c>https://…</c>) is opened as a whole;
/// <c>SHEvaluateSystemCommandTemplate</c> splits program commands and resolves bare names
/// (<c>notepad</c>, <c>chrome</c> via App Paths); when it refuses (measured on 26200: unquoted paths with spaces,
/// folders, <c>code .</c>, missing files), a quoted program, then the longest run of words that is an existing
/// file or folder, then the first word decides.
/// </para>
/// <para>
/// <b>Working directory:</b> the program's folder when the command named it with a path, else the user's profile
/// folder (what <c>cmd</c> from Win+R starts in).
/// </para>
/// <para>
/// <b>Threads.</b> <c>ShellExecuteEx</c> may load shell extensions and, for "as administrator", blocks until the
/// prompt is answered; it runs on a short-lived STA thread of its own, never on the UI thread. Footgun: call it
/// while the app still holds the foreground (the panel is open), or Windows may open the new window behind the
/// app the user came from.
/// </para>
/// </remarks>
public static partial class RunCommandLauncher
{
    /// <summary>
    /// Runs a command.
    /// </summary>
    /// <param name="command">The command as typed into Win+R (unexpanded).</param>
    /// <param name="asAdministrator">Use the <c>runas</c> verb (the administrator prompt appears).</param>
    /// <param name="ownerWindow">Owner of any Windows UI (the prompt); 0 = none.</param>
    /// <returns>How it ended; never throws for a command Windows refuses.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is null, blank, or spans several lines (Win+R takes one).</exception>
    public static Task<RunCommandResult> RunAsync(string command, bool asAdministrator, nint ownerWindow = 0) =>
        RunCoreAsync(Parse(command), asAdministrator, ownerWindow, quiet: false);

    /// <summary>
    /// Runs a command for a test: hidden, never elevated, and not counted for Start's "most used" list (a real run
    /// is, like Win+R's — a test must not leave that trace in the user's profile).
    /// </summary>
    /// <param name="command">The command as typed into Win+R (unexpanded).</param>
    /// <returns>How it ended.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is null, blank, or spans several lines.</exception>
    internal static Task<RunCommandResult> RunQuietlyAsync(string command) => RunCoreAsync(Parse(command), asAdministrator: false, ownerWindow: 0, quiet: true);

    /// <summary>Runs <paramref name="invocation"/> on a short-lived STA thread (see the class remarks).</summary>
    /// <param name="invocation">What to open.</param>
    /// <param name="asAdministrator">Use the <c>runas</c> verb.</param>
    /// <param name="ownerWindow">Owner of any Windows UI.</param>
    /// <param name="quiet">Hidden window and no usage logging (tests).</param>
    /// <returns>How it ended; faults only on an unexpected exception inside the launch thread.</returns>
    private static Task<RunCommandResult> RunCoreAsync(RunInvocation invocation, bool asAdministrator, nint ownerWindow, bool quiet)
    {
        var completion = new TaskCompletionSource<RunCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Execute(invocation, asAdministrator, ownerWindow, quiet));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Run command",
        };

        // STA: shell extensions and DDE-based handlers that ShellExecuteEx may load expect a single-threaded apartment.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    /// <summary>
    /// Decides what Windows is asked to open (see the class remarks). Reads the file system and App Paths (through
    /// <c>SHEvaluateSystemCommandTemplate</c>), never runs anything.
    /// </summary>
    /// <param name="command">The command as typed into Win+R (unexpanded).</param>
    /// <returns>The invocation.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is null, blank, or spans several lines (Win+R takes one).</exception>
    public static RunInvocation Parse(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (command.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            throw new ArgumentException("A Win+R command is a single line.", nameof(command));
        }

        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultDirectory = string.IsNullOrEmpty(profile) ? null : profile;

        // Relative locations (., .., ..\x) are relative to the dialog's working directory, the profile folder —
        // never to this app's own current directory.
        var basePath = defaultDirectory ?? Environment.CurrentDirectory;
        if (IsUri(expanded))
        {
            return new RunInvocation(expanded, null, defaultDirectory);
        }

        if (TryEvaluateSystemCommand(expanded, out var program, out var arguments))
        {
            return new RunInvocation(program, arguments, WorkingDirectoryFor(FirstWord(expanded), program, defaultDirectory, basePath));
        }

        // A quoted program: everything inside the quotes, the rest is arguments.
        if (expanded[0] == '"')
        {
            int end = expanded.IndexOf('"', 1);
            if (end > 1)
            {
                var quoted = expanded[1..end];
                var file = Locate(quoted, basePath) ?? quoted;
                var rest = expanded[(end + 1)..].Trim();
                return new RunInvocation(file, rest.Length == 0 ? null : rest, WorkingDirectoryFor(quoted, file, defaultDirectory, basePath));
            }
        }

        // An unquoted path with spaces: the longest run of words that names something existing.
        var words = expanded.Split(' ');
        for (int count = words.Length; count >= 1; count--)
        {
            var candidate = string.Join(' ', words, 0, count);
            if (Locate(candidate, basePath) is { } file)
            {
                var rest = string.Join(' ', words, count, words.Length - count).Trim();
                return new RunInvocation(file, rest.Length == 0 ? null : rest, WorkingDirectoryFor(candidate, file, defaultDirectory, basePath));
            }
        }

        // Last resort, like the dialog: the first word is the file (ShellExecuteEx still searches PATH and
        // PATHEXT for it, which finds scripts such as code.cmd), the rest are its arguments.
        int space = expanded.IndexOf(' ');
        var first = space < 0 ? expanded : expanded[..space];
        var tail = space < 0 ? string.Empty : expanded[(space + 1)..].Trim();
        return new RunInvocation(first, tail.Length == 0 ? null : tail, WorkingDirectoryFor(first, first, defaultDirectory, basePath));
    }

    /// <summary>Calls <c>ShellExecuteEx</c> (the launch thread).</summary>
    /// <param name="invocation">What to open.</param>
    /// <param name="asAdministrator">Use the <c>runas</c> verb.</param>
    /// <param name="ownerWindow">Owner of any Windows UI.</param>
    /// <param name="quiet">Hide the new window and skip usage logging (tests).</param>
    /// <returns>How it ended.</returns>
    private static unsafe RunCommandResult Execute(RunInvocation invocation, bool asAdministrator, nint ownerWindow, bool quiet)
    {
        uint mask = NativeMethods.SEE_MASK_NOASYNC | NativeMethods.SEE_MASK_DOENVSUBST | NativeMethods.SEE_MASK_FLAG_NO_UI | NativeMethods.SEE_MASK_INVOKEIDLIST;
        if (!quiet)
        {
            mask |= NativeMethods.SEE_MASK_FLAG_LOG_USAGE;
        }

        fixed (char* file = invocation.File)
        fixed (char* arguments = invocation.Arguments)
        fixed (char* directory = invocation.WorkingDirectory)
        fixed (char* verb = asAdministrator ? "runas" : null)
        {
            var info = new NativeMethods.SHELLEXECUTEINFOW
            {
                cbSize = (uint)sizeof(NativeMethods.SHELLEXECUTEINFOW),

                // The Run dialog's flags: wait for DDE and extensions (this thread ends right after), expand
                // variables, count the launch for Start's "most used" (not in tests), and let context-menu handlers
                // run the verb. FLAG_NO_UI: the panel reports errors itself instead of Windows' message box.
                fMask = mask,
                hwnd = ownerWindow,
                lpVerb = verb,
                lpFile = file,
                lpParameters = arguments,
                lpDirectory = directory,
                nShow = quiet ? NativeMethods.SW_HIDE : NativeMethods.SW_SHOWNORMAL,
            };

            if (NativeMethods.ShellExecuteEx(ref info))
            {
                return RunCommandResult.Started;
            }

            int error = Marshal.GetLastPInvokeError();
            return error == NativeMethods.ERROR_CANCELLED
                ? new RunCommandResult(RunCommandOutcome.Cancelled, error)
                : new RunCommandResult(RunCommandOutcome.Failed, error == 0 ? NativeMethods.ERROR_FILE_NOT_FOUND : error);
        }
    }

    /// <summary>
    /// Lets Windows split a program command and resolve the program (App Paths, PATH, the system folders).
    /// </summary>
    /// <param name="command">The expanded command.</param>
    /// <param name="program">The program's full path.</param>
    /// <param name="arguments">The arguments, or <see langword="null"/>.</param>
    /// <returns>Whether Windows accepted it as a program command.</returns>
    private static bool TryEvaluateSystemCommand(string command, out string program, out string? arguments)
    {
        program = string.Empty;
        arguments = null;
        nint application = 0, commandLine = 0, parameters = 0;
        try
        {
            if (NativeMethods.SHEvaluateSystemCommandTemplate(command, out application, out commandLine, out parameters) < 0 || application == 0)
            {
                return false;
            }

            program = Marshal.PtrToStringUni(application) ?? string.Empty;
            var tail = parameters == 0 ? null : Marshal.PtrToStringUni(parameters)?.Trim();
            arguments = string.IsNullOrEmpty(tail) ? null : tail;
            return program.Length > 0;
        }
        finally
        {
            // All three are allocated by the shell with CoTaskMemAlloc; FreeCoTaskMem ignores 0.
            Marshal.FreeCoTaskMem(application);
            Marshal.FreeCoTaskMem(commandLine);
            Marshal.FreeCoTaskMem(parameters);
        }
    }

    /// <summary>
    /// Resolves a location typed with path info to an existing file or folder (or a program named without its
    /// <c>.exe</c>). Bare names are left to Windows' own search (App Paths, PATH), which a file-system check here
    /// would get wrong.
    /// </summary>
    /// <param name="candidate">The words that might be a location.</param>
    /// <param name="basePath">What relative locations are relative to (the profile folder).</param>
    /// <returns>The full path of what exists, or <see langword="null"/>.</returns>
    private static string? Locate(string candidate, string basePath)
    {
        if (!HasPathInfo(candidate))
        {
            return null;
        }

        // "D:" means the drive, not that drive's current folder (which is what a drive-relative path resolves to).
        if (candidate.Length == 2 && candidate[1] == ':' && char.IsAsciiLetter(candidate[0]))
        {
            candidate += "\\";
        }

        try
        {
            var full = Path.IsPathFullyQualified(candidate) ? candidate : Path.GetFullPath(candidate, basePath);
            if (File.Exists(full) || Directory.Exists(full))
            {
                return full;
            }

            return File.Exists(full + ".exe") ? full + ".exe" : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Characters no path may hold (the words were not a location after all).
            return null;
        }
    }

    /// <summary>
    /// The working directory: the target's folder when the command named it with a path, else the default.
    /// </summary>
    /// <param name="typed">The program or file part as typed (decides whether it had path info).</param>
    /// <param name="resolved">The resolved file (its folder is used).</param>
    /// <param name="defaultDirectory">The profile folder, or <see langword="null"/>.</param>
    /// <param name="basePath">What a relative <paramref name="resolved"/> is relative to.</param>
    /// <returns>The directory, or <paramref name="defaultDirectory"/>.</returns>
    private static string? WorkingDirectoryFor(string typed, string resolved, string? defaultDirectory, string basePath)
    {
        if (!HasPathInfo(typed))
        {
            return defaultDirectory;
        }

        try
        {
            // A folder opens in itself; a file's working directory is its own folder.
            var full = Path.GetFullPath(resolved.Trim('"'), basePath);
            var directory = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
            return string.IsNullOrEmpty(directory) ? defaultDirectory : directory;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return defaultDirectory;
        }
    }

    /// <summary>
    /// Whether text names a location rather than a bare name: a separator, a drive colon, or <c>.</c>/<c>..</c>.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> for <c>C:\x</c>, <c>.\x</c>, <c>..</c>, <c>\\server\share</c>, <c>dir/x</c>.</returns>
    private static bool HasPathInfo(string text) => text is "." or ".." || text.AsSpan().IndexOfAny('\\', '/', ':') >= 0;

    /// <summary>The first space-separated word (or the quoted part) of a command.</summary>
    /// <param name="command">The command.</param>
    /// <returns>The word.</returns>
    private static string FirstWord(string command)
    {
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : command.Trim('"');
        }

        int space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }

    /// <summary>
    /// Whether the command is a URI to open as a whole: a scheme of two or more characters (one letter is a drive,
    /// <c>C:\x</c>), never a UNC path.
    /// </summary>
    /// <param name="command">The expanded command.</param>
    /// <returns><see langword="true"/> for <c>https://…</c>, <c>shell:startup</c>, <c>ms-settings:display</c>, <c>mailto:…</c>.</returns>
    private static bool IsUri(string command) => !command.StartsWith(@"\\", StringComparison.Ordinal) && UriScheme().IsMatch(command);

    /// <summary>A URI scheme at the start (RFC 3986 characters, at least two), followed by a colon.</summary>
    /// <returns>The compiled expression.</returns>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]+:", RegexOptions.CultureInvariant)]
    private static partial Regex UriScheme();
}
