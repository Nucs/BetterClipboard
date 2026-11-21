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
    /// Asked to run as administrator, it was opened (or tried) without: Windows has no administrator mode for it — a
    /// picture, a document, a folder, a link — so it opened the way Win+R's Ctrl+Shift+Enter opens such a target. Only the
    /// log says so (<see cref="Describe"/> is unchanged): a started run closes the panel, so there is no footer to show it in.
    /// </summary>
    public bool WithoutAdministrator { get; init; }

    /// <summary>
    /// Every sentence <see cref="Describe"/> gives, as composite formats: the panel's footer shows a refusal in the
    /// interface language through <c>UiText.TranslateFormatted</c>, the log keeps the English. A changed sentence must
    /// change here and in every catalog; <c>TranslationTemplateTests</c> check that each outcome matches one.
    /// </summary>
    public static IReadOnlyList<string> DescriptionTemplates { get; } =
    [
        "Started.",
        "Cancelled at the administrator prompt.",
        "Windows cannot find it.",
        "No app is set to open it.",
        "Access is denied.",
        "Windows could not run it (error {0}).",
    ];

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
/// <para>
/// <b>As administrator</b> (the owner's choice, 2026-11-21: "Like Windows", and the card menu's item grayed where it
/// cannot work; CLAUDE.md §2.17). Measured on Windows 11 (26200, UAC on): Explorer's menu offers "Run as administrator"
/// only for programs — <c>.exe</c>, <c>.bat</c>, <c>.cmd</c>, <c>.msc</c>, <c>.cpl</c>, app aliases, shortcuts to
/// programs — and <c>ShellExecuteEx</c> with <c>runas</c> fails with <c>ERROR_NO_ASSOCIATION</c> for a picture, a text
/// file, a folder, a script that opens in an editor, a shortcut to a file, <c>shell:</c> and <c>ms-settings:</c>; Win+R's
/// Ctrl+Shift+Enter opens those normally, and a link too (where <c>runas</c> would elevate Edge, whose <c>https</c>
/// association has a <c>runas</c> verb). So <see cref="CanRunAsAdministrator(string)"/> answers Explorer's question, and a
/// run asked "as administrator" (<see cref="Run"/>) elevates what it can and opens the rest normally, never failing
/// silently as before (a picture's run did nothing, its footer said "No app is set to open it.").
/// </para>
/// </remarks>
public static partial class RunCommandLauncher
{
    /// <summary>
    /// Runs a command.
    /// </summary>
    /// <param name="command">The command as typed into Win+R (unexpanded).</param>
    /// <param name="asAdministrator">
    /// Run it as administrator where Windows can (the <c>runas</c> verb: the administrator prompt appears); a target Windows
    /// has no administrator mode for is opened normally instead (<see cref="RunCommandResult.WithoutAdministrator"/>).
    /// </param>
    /// <param name="ownerWindow">Owner of any Windows UI (the prompt); 0 = none.</param>
    /// <returns>How it ended; never throws for a command Windows refuses.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is null, blank, or spans several lines (Win+R takes one).</exception>
    public static Task<RunCommandResult> RunAsync(string command, bool asAdministrator, nint ownerWindow = 0) =>
        RunCoreAsync(Parse(command), asAdministrator, ownerWindow, quiet: false);

    /// <summary>
    /// Whether Windows can run a Win+R command as administrator: Explorer's question ("Run as administrator" in its menu),
    /// answered from the command's target and its file type's own verbs. The card menu grays its "Run as administrator"
    /// when the answer is <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Reads the file system (the target, a shortcut's file) and the registry, never runs anything and loads no shell
    /// extension; a target on a network share that does not answer blocks it, so callers run it off the UI thread.
    /// </remarks>
    /// <param name="command">The command as typed into Win+R (unexpanded).</param>
    /// <returns>
    /// <see langword="true"/> for a program (its file type has a <c>runas</c> verb: <c>.exe</c>, <c>.bat</c>, <c>.cmd</c>,
    /// <c>.msc</c>, <c>.cpl</c>…) or a shortcut to one; <see langword="false"/> for a picture, a document, a folder, a link
    /// or a <c>shell:</c> place; <see langword="null"/> when only running it tells — a bare name Windows looks up on PATH
    /// when it runs, or a shortcut whose target cannot be read.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is null, blank, or spans several lines (Win+R takes one).</exception>
    public static bool? CanRunAsAdministrator(string command) => CanRunAsAdministrator(Parse(command));

    /// <summary>
    /// <see cref="CanRunAsAdministrator(string)"/> for a command already parsed.
    /// </summary>
    /// <param name="invocation">What Windows is asked to open.</param>
    /// <returns>See <see cref="CanRunAsAdministrator(string)"/>.</returns>
    internal static bool? CanRunAsAdministrator(RunInvocation invocation) => CanRunAsAdministrator(invocation.File, followShortcut: true);

    /// <summary>
    /// Carries out a run the way <see cref="RunAsync"/> describes: as administrator where Windows can, otherwise normally.
    /// Pure composition over its two delegates, so the tests check every path without a consent prompt.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Not asked as administrator: one normal run.</item>
    /// <item>Windows has no administrator mode for the target: one normal run, marked
    /// <see cref="RunCommandResult.WithoutAdministrator"/>. <c>runas</c> is not even tried: for a link it would elevate the
    /// browser, which Win+R's Ctrl+Shift+Enter does not do.</item>
    /// <item>A program, or a target only running tells about: <c>runas</c> first. A decline (<c>ERROR_CANCELLED</c>) and
    /// every other failure are reported as they are, except <c>ERROR_NO_ASSOCIATION</c>, which means Windows found no
    /// administrator mode after all (a name it looked up on PATH, a shortcut it could not read): then a normal run, marked
    /// as above.</item>
    /// </list>
    /// </remarks>
    /// <param name="invocation">What Windows is asked to open.</param>
    /// <param name="asAdministrator">Asked to run it as administrator.</param>
    /// <param name="canRunAsAdministrator">The question <see cref="CanRunAsAdministrator(RunInvocation)"/> answers.</param>
    /// <param name="execute">Runs it, elevated (<see langword="true"/>) or not.</param>
    /// <returns>How it ended.</returns>
    /// <exception cref="ArgumentNullException">A delegate is null.</exception>
    internal static RunCommandResult Run(RunInvocation invocation, bool asAdministrator, Func<RunInvocation, bool?> canRunAsAdministrator, Func<bool, RunCommandResult> execute)
    {
        ArgumentNullException.ThrowIfNull(canRunAsAdministrator);
        ArgumentNullException.ThrowIfNull(execute);
        if (!asAdministrator)
        {
            return execute(false);
        }

        if (canRunAsAdministrator(invocation) != false)
        {
            var elevated = execute(true);
            if (elevated is not { Outcome: RunCommandOutcome.Failed, Win32Error: NativeMethods.ERROR_NO_ASSOCIATION })
            {
                return elevated;
            }
        }

        // Windows has no administrator mode for it: open it as Win+R's Ctrl+Shift+Enter does, normally.
        return execute(false) with { WithoutAdministrator = true };
    }

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
    /// <param name="asAdministrator">As administrator where Windows can (<see cref="Run"/>).</param>
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
                // The decision reads the file system (a shortcut's target included), so it runs here too, off the UI thread.
                completion.SetResult(Run(invocation, asAdministrator, CanRunAsAdministrator, elevate => Execute(invocation, elevate, ownerWindow, quiet)));
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

    /// <summary>
    /// The decision behind <see cref="CanRunAsAdministrator(string)"/>, for one target.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only the file type's own verbs count</b> (<see cref="HasAdministratorMode"/>): the catch-all keys every file and
    /// folder shares (<c>*</c>, <c>AllFilesystemObjects</c>, <c>Directory</c>) are left out on purpose. Registry tweaks put
    /// other commands there under the verb name <c>runas</c> (the well-known "Take Ownership" menu entry did:
    /// <c>takeown</c> and <c>icacls</c> on the file), and an elevated run of a picture must never run those instead.
    /// </para>
    /// <para>
    /// A shortcut is judged by its target, once: Explorer offers "Run as administrator" for a shortcut exactly when its
    /// target has an administrator mode. A shortcut to a shortcut is judged as a shortcut (no administrator mode).
    /// </para>
    /// </remarks>
    /// <param name="file">The file, folder, URI or bare name Windows is asked to open.</param>
    /// <param name="followShortcut">Read a <c>.lnk</c> file's target and judge that (only for the command's own target).</param>
    /// <returns>See <see cref="CanRunAsAdministrator(string)"/>.</returns>
    private static bool? CanRunAsAdministrator(string file, bool followShortcut)
    {
        // Links, shell: places and settings pages: Win+R's Ctrl+Shift+Enter opens them normally (measured), and runas
        // on a link would elevate the browser instead.
        if (IsUri(file))
        {
            return false;
        }

        try
        {
            // Parse resolved every location it found to a full path. Anything else (a bare name, a relative path nothing
            // matched) is looked up by Windows itself when it runs, so the disk is not asked here: File.Exists would look
            // in this app's own folder, which is not where Windows searches.
            bool fullyQualified = Path.IsPathFullyQualified(file);

            // A folder's window cannot run elevated; Win+R's Ctrl+Shift+Enter opens it normally (measured).
            if (fullyQualified && Directory.Exists(file))
            {
                return false;
            }

            bool exists = fullyQualified && File.Exists(file);
            var extension = Path.GetExtension(file);
            if (exists && followShortcut && extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                return ShellShortcut.ReadTarget(file) is { } target ? CanRunAsAdministrator(target, followShortcut: false) : null;
            }

            if (extension.Length == 0)
            {
                // A file without a type has no app at all; a bare name that is not here is looked up on PATH (with
                // PATHEXT) when it runs, so only running it tells.
                return exists ? false : null;
            }

            // A file that is gone (or is looked up on PATH) is still judged by its type: running it says "cannot find".
            return HasAdministratorMode(extension);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            // Characters no path may hold, or a location Windows refuses to describe: only running it tells.
            return null;
        }
    }

    /// <summary>
    /// Whether a file type has an administrator mode of its own: a <c>runas</c> verb registered for it, as a command line or
    /// through a COM object. Follows the user's default app for the type (Settings › Default apps) like opening it does.
    /// </summary>
    /// <param name="extension">The extension with its dot (<c>.exe</c>, <c>.png</c>).</param>
    /// <returns><see langword="true"/> when the type has the verb.</returns>
    private static unsafe bool HasAdministratorMode(string extension)
    {
        // A null buffer asks only whether the string exists: S_FALSE (1) when it does, a negative HRESULT
        // (ERROR_NO_ASSOCIATION) when the type lacks the verb.
        uint length = 0;
        if (NativeMethods.AssocQueryString(0, NativeMethods.ASSOCSTR_COMMAND, extension, "runas", null, ref length) >= 0)
        {
            return true;
        }

        length = 0;
        return NativeMethods.AssocQueryString(0, NativeMethods.ASSOCSTR_DELEGATEEXECUTE, extension, "runas", null, ref length) >= 0;
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
