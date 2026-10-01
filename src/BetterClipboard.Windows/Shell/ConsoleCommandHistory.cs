using System.Globalization;
using System.Text;
using BetterClipboard.Windows.Interop;

namespace BetterClipboard.Windows.Shell;

/// <summary>
/// Reads a console window's command history — what <c>doskey /history</c> shows, the only history <c>cmd.exe</c>
/// has — for the panel's Cmd tab. The reading half runs in a throwaway helper process (<see cref="HelperSwitch"/>);
/// the app only starts it and parses its output.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a helper.</b> The history lives in the console host's memory and is only readable from inside that console
/// (<c>AttachConsole</c>, then <c>GetConsoleCommandHistoryW</c>). A process attached to a console when it closes is
/// terminated with it — measured: exit 0xC000013A within 15 ms, whatever its control handler does (CLAUDE.md
/// §2.16). The user closing a cmd window at the wrong millisecond must never end BetterClipboard, so a helper — a
/// second <c>BetterClipboard.exe</c> that does nothing else — attaches instead, and its death costs nothing.
/// </para>
/// <para>
/// <b>Wire format.</b> The helper writes each command as UTF-8 followed by a NUL byte (a command can contain neither
/// a NUL nor a line break) and exits 0; any failure exits non-zero with nothing written. Nothing reaches a log.
/// </para>
/// </remarks>
public static class ConsoleCommandHistory
{
    /// <summary>The command-line switch that turns <c>BetterClipboard.exe</c> into the helper: <c>--read-console-history &lt;pid&gt;</c>.</summary>
    public const string HelperSwitch = "--read-console-history";

    /// <summary>The program whose history is read: the Command Prompt.</summary>
    public const string CmdExeName = "cmd.exe";

    /// <summary>Exit code: read and written.</summary>
    public const int ExitOk = 0;

    /// <summary>Exit code: bad arguments.</summary>
    public const int ExitUsage = 2;

    /// <summary>Exit code: the console could not be attached (the process ended, has no console, or is not ours).</summary>
    public const int ExitNotAttached = 3;

    /// <summary>
    /// The helper's whole job (called from <c>Program.Main</c> before anything else starts): attach to the console of
    /// the process named on the command line, read its cmd history, detach, write it.
    /// </summary>
    /// <param name="args">The helper's command line: <see cref="HelperSwitch"/> and a process id.</param>
    /// <param name="output">Where to write (the inherited standard output, opened before attaching).</param>
    /// <returns>The exit code (<see cref="ExitOk"/>, <see cref="ExitUsage"/>, <see cref="ExitNotAttached"/>).</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static int RunHelper(IReadOnlyList<string> args, Stream output)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        if (args.Count != 2 || args[0] != HelperSwitch
            || !uint.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var processId) || processId == 0)
        {
            return ExitUsage;
        }

        var commands = ReadAttached(processId, CmdExeName);
        if (commands is null)
        {
            return ExitNotAttached;
        }

        var bytes = Encode(commands);
        output.Write(bytes, 0, bytes.Length);
        output.Flush();
        return ExitOk;
    }

    /// <summary>
    /// Attaches to the console <paramref name="processId"/> uses, reads <paramref name="exeName"/>'s command history,
    /// and detaches. <b>Only in a throwaway process</b> (see class remarks): if the console closes meanwhile, the
    /// calling process is terminated.
    /// </summary>
    /// <param name="processId">A process using the console (the cmd.exe itself).</param>
    /// <param name="exeName">The program whose history to read.</param>
    /// <returns>The commands, oldest first (possibly empty), or <see langword="null"/> when the console could not be attached.</returns>
    public static IReadOnlyList<string>? ReadAttached(uint processId, string exeName)
    {
        // A process can be attached to one console only. The app's own exe is a GUI program with none, but a caller
        // that has one (a test host) must leave it first.
        NativeMethods.FreeConsole();
        if (!NativeMethods.AttachConsole(processId))
        {
            return null;
        }

        try
        {
            // Ctrl+C typed into that window while attached would otherwise end the helper mid-read.
            NativeMethods.SetConsoleCtrlHandler(0, true);
            return Read(exeName);
        }
        finally
        {
            NativeMethods.FreeConsole();
        }
    }

    /// <summary>Writes commands in the helper's wire format (UTF-8, each followed by a NUL byte).</summary>
    /// <param name="commands">The commands.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="commands"/> is <see langword="null"/>.</exception>
    public static byte[] Encode(IReadOnlyList<string> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        using var buffer = new MemoryStream();
        foreach (var command in commands)
        {
            // A NUL inside a command cannot come from the console (it ends the command there); drop it defensively.
            var bytes = Encoding.UTF8.GetBytes(command.Replace("\0", string.Empty, StringComparison.Ordinal));
            buffer.Write(bytes, 0, bytes.Length);
            buffer.WriteByte(0);
        }

        return buffer.ToArray();
    }

    /// <summary>Reads the helper's output back into commands.</summary>
    /// <param name="output">What the helper wrote.</param>
    /// <returns>The commands, oldest first; blank ones dropped.</returns>
    public static IReadOnlyList<string> Decode(ReadOnlySpan<byte> output)
    {
        var commands = new List<string>();
        while (!output.IsEmpty)
        {
            int end = output.IndexOf((byte)0);
            var item = end < 0 ? output : output[..end];
            var text = Encoding.UTF8.GetString(item);
            if (!string.IsNullOrWhiteSpace(text))
            {
                commands.Add(text);
            }

            output = end < 0 ? [] : output[(end + 1)..];
        }

        return commands;
    }

    /// <summary>Reads the attached console's command history for one program.</summary>
    /// <param name="exeName">The program's exe name.</param>
    /// <returns>The commands, oldest first (empty when there are none).</returns>
    private static unsafe List<string> Read(string exeName)
    {
        fixed (char* name = exeName)
        {
            // The two calls are not atomic: a command added in between makes the second one fail for a too-small
            // buffer, so ask with room to spare and retry once with the new size.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                uint length = NativeMethods.GetConsoleCommandHistoryLength(name);
                if (length == 0)
                {
                    return [];
                }

                var buffer = new byte[length + 4096];
                uint copied;
                fixed (byte* data = buffer)
                {
                    copied = NativeMethods.GetConsoleCommandHistory(data, (uint)buffer.Length, name);
                }

                if (copied == 0)
                {
                    continue;
                }

                var text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(copied, (uint)buffer.Length) & ~1);
                return text.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
            }

            return [];
        }
    }
}
