using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using BetterClipboard.Core;
using BetterClipboard.Core.Cli;
using BetterClipboard.Core.Settings;

namespace BetterClipboard.Cli;

/// <summary>
/// <c>bclip</c> entry point: parse → gate on the app's setting → send over the pipe (starting the app in
/// the background if needed) → print.
/// </summary>
internal static class Program
{
    /// <summary>First connection attempt: the running app accepts within milliseconds.</summary>
    private static readonly TimeSpan QuickConnect = TimeSpan.FromMilliseconds(800);

    /// <summary>After starting the app: opening the encrypted store and importing may take a few seconds.</summary>
    private static readonly TimeSpan StartupConnect = TimeSpan.FromSeconds(20);

    private static TextWriter stdout = Console.Out;
    private static TextWriter stderr = Console.Error;

    /// <summary>
    /// Runs one command.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>An exit code from <see cref="CliExitCodes"/> (130 on Ctrl+C).</returns>
    private static async Task<int> Main(string[] args)
    {
        (stdout, stderr) = ConsoleText.CreateWriters();
        try
        {
            return await RunAsync(args).ConfigureAwait(false);
        }
        finally
        {
            await stdout.FlushAsync().ConfigureAwait(false);
            await stderr.FlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The command flow (see the class summary).</summary>
    /// <param name="args">Arguments.</param>
    /// <returns>The exit code.</returns>
    private static async Task<int> RunAsync(string[] args)
    {
        var invocation = CliArguments.Parse(args, DateTimeOffset.Now);
        if (invocation.Error is { } error)
        {
            await stderr.WriteLineAsync($"bclip: {error}").ConfigureAwait(false);
            return CliExitCodes.Usage;
        }

        if (invocation.ShowHelp)
        {
            await stdout.WriteAsync(CliArguments.Help(invocation.HelpTopic)).ConfigureAwait(false);
            return CliExitCodes.Ok;
        }

        if (invocation.ShowVersion)
        {
            await stdout.WriteLineAsync($"bclip {Version}").ConfigureAwait(false);
            return CliExitCodes.Ok;
        }

        var request = invocation.Request!;
        if (invocation.ReadStdin)
        {
            if (!Console.IsInputRedirected)
            {
                await stderr.WriteLineAsync("bclip: put needs text — pass it as an argument or pipe it in (echo hello | bclip put).").ConfigureAwait(false);
                return CliExitCodes.Usage;
            }

            request = request with { Text = ConsoleText.TrimOneTrailingNewline(await ConsoleText.ReadStdinAsync().ConfigureAwait(false)) };
        }

        // Fail fast with a clear message when the user has not opted in (the pipe would not exist anyway).
        // An unreadable settings file (app mid-save) is "unknown": fall through and let the pipe decide.
        var paths = AppPaths.ResolveDefault();
        var settings = SettingsStore.TryReadSnapshot(paths.SettingsPath);
        if (settings is { EnableCommandLine: false })
        {
            await stderr.WriteLineAsync("bclip: command-line access is off. Turn it on in BetterClipboard › Settings › Command line.").ConfigureAwait(false);
            return CliExitCodes.Unavailable;
        }

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // Let "bclip wait" end cleanly; the server sees the hang-up and cancels too.
            e.Cancel = true;
            cancel.Cancel();
        };

        CliResponse response;
        try
        {
            response = await SendAsync(request, cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (TimeoutException)
        {
            await stderr.WriteLineAsync(IsInstanceRunning(paths.InstanceName)
                ? "bclip: BetterClipboard is running but does not accept bclip — turn on Settings › Command line (BetterClipboard 0.2.0 or later)."
                : "bclip: BetterClipboard is not running and could not be started — start it, and check that Settings › Command line is on.").ConfigureAwait(false);
            return CliExitCodes.Unavailable;
        }
        catch (UnauthorizedAccessException)
        {
            await stderr.WriteLineAsync("bclip: the BetterClipboard pipe is not owned by your account — refusing to use it.").ConfigureAwait(false);
            return CliExitCodes.Unavailable;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            await stderr.WriteLineAsync($"bclip: {ex.Message}").ConfigureAwait(false);
            return CliExitCodes.Error;
        }

        return await EmitAsync(invocation, response, cancel.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a request; if nothing answers and BetterClipboard is not running in this session, starts it in
    /// the background (only when the setting says command-line access is on) and retries.
    /// </summary>
    /// <param name="request">Request.</param>
    /// <param name="cancellationToken">Ctrl+C.</param>
    /// <returns>The response.</returns>
    /// <exception cref="TimeoutException">Still unreachable.</exception>
    private static async Task<CliResponse> SendAsync(CliRequest request, CancellationToken cancellationToken)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("No user SID.");
        int session = Process.GetCurrentProcess().SessionId;

        // Same scoping as the app: with BETTERCLIPBOARD_DATA_DIR set, bclip talks to that isolated instance only.
        var paths = AppPaths.ResolveDefault();
        var pipeName = CliEndpoint.PipeName(sid, session, paths.InstanceScope);
        try
        {
            return await CliClient.SendAsync(pipeName, request, QuickConnect, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!IsInstanceRunning(paths.InstanceName) && TryStartApp())
        {
            return await CliClient.SendAsync(pipeName, request, StartupConnect, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes the response as JSON or human text and maps it to an exit code.</summary>
    /// <param name="invocation">Parsed command line.</param>
    /// <param name="response">Response.</param>
    /// <param name="cancellationToken">Ctrl+C.</param>
    /// <returns>The exit code.</returns>
    private static async Task<int> EmitAsync(CliInvocation invocation, CliResponse response, CancellationToken cancellationToken)
    {
        var command = invocation.Request!.Command;

        // wait -o FILE on an image: the wait answer carries no pixels, fetch them now.
        if (response.Ok && command == CliCommands.Wait && invocation.OutputFile is not null && response.Item is { Kind: "image" } image)
        {
            response = await SendAsync(new CliRequest { Command = CliCommands.Get, Id = image.Id, Format = "png" }, cancellationToken).ConfigureAwait(false);
            command = CliCommands.Get;
        }

        // Content to a file (get/wait -o): written before printing, so JSON can report it.
        if (response.Ok && invocation.OutputFile is { } file)
        {
            long written = await WriteOutputFileAsync(file, response).ConfigureAwait(false);
            response = response with
            {
                Content = response.Content is null ? null : response.Content with { DataBase64 = null },
                Message = $"Saved {written:N0} bytes to {Path.GetFullPath(file)}.",
            };
            if (!invocation.Json)
            {
                await stderr.WriteLineAsync(response.Message).ConfigureAwait(false);
                return CliExitCodes.Ok;
            }
        }

        bool binary = response.Content?.DataBase64 is not null;
        if (binary)
        {
            // Never dump image bytes into a terminal or an agent's context window.
            response = response with { Content = response.Content! with { DataBase64 = null } };
            await stderr.WriteLineAsync($"bclip: item {response.Item?.Id} is an image — save it with -o FILE.png").ConfigureAwait(false);
            if (!invocation.Json)
            {
                return CliExitCodes.Usage;
            }
        }

        if (invocation.Json)
        {
            await stdout.WriteLineAsync(CliJson.Serialize(response, indented: true)).ConfigureAwait(false);
            return CliExitCodes.For(response);
        }

        if (!response.Ok)
        {
            await stderr.WriteLineAsync($"bclip: {response.Error}").ConfigureAwait(false);
            return CliExitCodes.For(response);
        }

        var text = CliOutput.Render(command, response, DateTimeOffset.Now);
        await stdout.WriteAsync(text).ConfigureAwait(false);

        // Content commands print the payload verbatim; add a final newline only for a human's terminal.
        if (!Console.IsOutputRedirected && text.Length > 0 && !text.EndsWith('\n'))
        {
            await stdout.WriteLineAsync().ConfigureAwait(false);
        }

        if (command == CliCommands.Wait && response.Item is { Kind: "image" } waited)
        {
            await stderr.WriteLineAsync($"bclip: item {waited.Id} is an image — save it with: bclip get {waited.Id} -o FILE.png").ConfigureAwait(false);
        }

        return CliExitCodes.Ok;
    }

    /// <summary>Writes a get/wait payload to a file (PNG bytes, or text as UTF-8 without BOM).</summary>
    /// <param name="file">Target path.</param>
    /// <param name="response">A successful response.</param>
    /// <returns>Bytes written.</returns>
    /// <exception cref="IOException">The file could not be written.</exception>
    private static async Task<long> WriteOutputFileAsync(string file, CliResponse response)
    {
        byte[] bytes = response.Content?.DataBase64 is { } base64
            ? Convert.FromBase64String(base64)
            : new UTF8Encoding(false).GetBytes(response.Content?.Text ?? response.Item?.Text ?? string.Empty);
        await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(false);
        return bytes.Length;
    }

    /// <summary>
    /// Whether the BetterClipboard instance bclip targets is running in this session (then a missing pipe
    /// means "access off", not "not started"). Checks the instance's single-instance lock rather than
    /// process names, so an isolated dev instance and the installed app are told apart.
    /// </summary>
    /// <param name="instanceName">From <see cref="AppPaths.InstanceName"/>.</param>
    /// <returns>Whether it runs.</returns>
    private static bool IsInstanceRunning(string instanceName)
    {
        if (Mutex.TryOpenExisting($@"Local\{instanceName}.Instance", out var mutex))
        {
            mutex.Dispose();
            return true;
        }

        return false;
    }

    /// <summary>Starts the app that ships next to <c>bclip</c> in tray-only mode.</summary>
    /// <returns>Whether it was started.</returns>
    private static bool TryStartApp()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "BetterClipboard.exe");
        if (!File.Exists(exe))
        {
            return false;
        }

        try
        {
            // UseShellExecute = true on purpose: with CreateProcess-style starts the long-lived app inherits
            // bclip's stdout/stderr, so "bclip status | grep" (or an AI tool capturing output) would wait for
            // EOF until the app exits — i.e. hang. ShellExecute passes no handles but still hands down our
            // environment, so a BETTERCLIPBOARD_DATA_DIR override reaches the started (scoped) instance.
            using var started = Process.Start(new ProcessStartInfo(exe, "--background") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
            return started is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Informational version of bclip (matches the app it ships with).</summary>
    private static string Version =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}

/// <summary>
/// Console text I/O that is correct for both humans and machines on Windows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Terminal:</b> <see cref="Console.OutputEncoding"/> is set to UTF-16, which makes .NET write with
/// <c>WriteConsoleW</c> — every character displays, and (unlike UTF-8) the console's code page is left
/// untouched, so the user's shell is not changed after bclip exits.
/// </para>
/// <para>
/// <b>Redirected</b> (pipes, files, AI tools capturing output): raw UTF-8 without BOM — exactly what
/// scripts expect — independent of the console code page.
/// </para>
/// </remarks>
internal static class ConsoleText
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Creates the stdout/stderr writers described in the remarks.</summary>
    /// <returns>(stdout, stderr).</returns>
    public static (TextWriter Out, TextWriter Error) CreateWriters()
    {
        if (!Console.IsOutputRedirected || !Console.IsErrorRedirected)
        {
            try
            {
                Console.OutputEncoding = Encoding.Unicode;
            }
            catch (IOException)
            {
                // No console attached after all; the redirected writers below still work.
            }
        }

        return (
            Console.IsOutputRedirected ? new StreamWriter(Console.OpenStandardOutput(), Utf8) { AutoFlush = false } : Console.Out,
            Console.IsErrorRedirected ? new StreamWriter(Console.OpenStandardError(), Utf8) { AutoFlush = true } : Console.Error);
    }

    /// <summary>
    /// Reads all of standard input as text: UTF-8, or UTF-16/UTF-8 when a byte-order mark says so (Windows
    /// PowerShell pipes UTF-16 to files but not to programs; most tools send UTF-8).
    /// </summary>
    /// <returns>The text.</returns>
    public static async Task<string> ReadStdinAsync()
    {
        using var input = Console.OpenStandardInput();
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        using var reader = new StreamReader(new MemoryStream(bytes), Utf8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Drops one trailing line break (<c>echo text | bclip put</c> should copy "text", not "text\n").
    /// </summary>
    /// <param name="text">Piped text.</param>
    /// <returns>The text without a single trailing <c>\n</c> or <c>\r\n</c>.</returns>
    public static string TrimOneTrailingNewline(string text) =>
        text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2] : text.EndsWith('\n') ? text[..^1] : text;
}
