using System.Globalization;

namespace BetterClipboard.Core.Cli;

/// <summary>
/// A parsed <c>bclip</c> command line: the request for the app plus the options only the client uses
/// (output mode, output file, stdin).
/// </summary>
public sealed record CliInvocation
{
    /// <summary>The request to send; <see langword="null"/> for local-only commands (help, version) and parse errors.</summary>
    public CliRequest? Request { get; init; }

    /// <summary>Print the response as JSON instead of human-readable text.</summary>
    public bool Json { get; init; }

    /// <summary><c>get</c>/<c>wait</c>: write the content to this file instead of stdout.</summary>
    public string? OutputFile { get; init; }

    /// <summary><c>put</c>: read the text from standard input (no text argument, or <c>-</c>).</summary>
    public bool ReadStdin { get; init; }

    /// <summary>Show help (optionally for <see cref="HelpTopic"/>).</summary>
    public bool ShowHelp { get; init; }

    /// <summary>Command whose help was asked for, or <see langword="null"/> for the overview.</summary>
    public string? HelpTopic { get; init; }

    /// <summary>Show the client version.</summary>
    public bool ShowVersion { get; init; }

    /// <summary>Why parsing failed; <see langword="null"/> on success.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Parses <c>bclip</c> arguments. Pure (no I/O, injected clock), so the whole grammar is unit-tested.
/// </summary>
/// <remarks>
/// Grammar: <c>bclip &lt;command&gt; [arguments] [options]</c>; options may appear anywhere after the
/// command, <c>--name=value</c> and <c>--name value</c> are equivalent, and <c>--</c> ends option parsing
/// (for grep patterns that start with a dash). Items are addressed by the id shown in listings or by
/// <c>--recent N</c> — no <c>#1</c>-style syntax, because <c>#</c> starts a comment in bash and
/// PowerShell and an AI agent's command would silently lose its argument.
/// </remarks>
public static class CliArguments
{
    /// <summary>Command aliases (familiar Unix verbs) → canonical command.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["list"] = CliCommands.List, ["ls"] = CliCommands.List, ["history"] = CliCommands.List,
        ["search"] = CliCommands.Search, ["find"] = CliCommands.Search,
        ["grep"] = CliCommands.Grep,
        ["get"] = CliCommands.Get, ["show"] = CliCommands.Get, ["cat"] = CliCommands.Get,
        ["copy"] = CliCommands.Copy, ["cp"] = CliCommands.Copy, ["pick"] = CliCommands.Copy,
        ["put"] = CliCommands.Put, ["set"] = CliCommands.Put,
        ["pin"] = CliCommands.Pin, ["unpin"] = CliCommands.Unpin,
        ["delete"] = CliCommands.Delete, ["rm"] = CliCommands.Delete, ["del"] = CliCommands.Delete,
        ["wait"] = CliCommands.Wait, ["watch"] = CliCommands.Wait,
        ["status"] = CliCommands.Status, ["stats"] = CliCommands.Status,
    };

    /// <summary>Options that take a value.</summary>
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "-n", "--limit", "--offset", "-f", "--filter", "-s", "--since", "-r", "--recent", "--format", "-o", "--out", "-t", "--timeout",
    };

    /// <summary>
    /// Parses a command line.
    /// </summary>
    /// <param name="args">Arguments (without the program name).</param>
    /// <param name="now">Current time, for relative <c>--since</c> values.</param>
    /// <returns>The invocation; check <see cref="CliInvocation.Error"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static CliInvocation Parse(IReadOnlyList<string> args, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
        {
            return new CliInvocation { ShowHelp = true };
        }

        var first = args[0];
        if (first is "-h" or "--help" or "help" or "/?")
        {
            return new CliInvocation { ShowHelp = true, HelpTopic = args.Count > 1 && Aliases.TryGetValue(args[1], out var topic) ? topic : null };
        }

        if (first is "--version" or "version")
        {
            return new CliInvocation { ShowVersion = true };
        }

        if (!Aliases.TryGetValue(first, out var command))
        {
            return Fail($"Unknown command '{first}'. Run 'bclip help' for the list of commands.");
        }

        var positional = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        bool optionsEnded = false;
        for (int i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (optionsEnded || arg == "-" || !arg.StartsWith('-') || IsNegativeNumber(arg))
            {
                positional.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                optionsEnded = true;
                continue;
            }

            string name = arg;
            string? value = null;
            int equals = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 2)
            {
                name = arg[..equals];
                value = arg[(equals + 1)..];
            }

            if (ValueOptions.Contains(name) && value is null)
            {
                if (i + 1 >= args.Count)
                {
                    return Fail($"Option {name} needs a value.");
                }

                value = args[++i];
            }

            if (name is "-h" or "--help")
            {
                return new CliInvocation { ShowHelp = true, HelpTopic = command };
            }

            if (!ValueOptions.Contains(name) && name is not ("-i" or "--ignore-case" or "--full" or "--plain" or "--json"))
            {
                return Fail($"Unknown option '{name}' for {command}. Run 'bclip help {command}'.");
            }

            options[Canonical(name)] = value;
        }

        return Build(command, positional, options, now);
    }

    /// <summary>
    /// Help text: the overview, or one command's details (every command has a topic, so an agent can
    /// discover the exact syntax with <c>bclip help &lt;command&gt;</c> instead of guessing).
    /// </summary>
    /// <param name="topic">A canonical command name, or <see langword="null"/> (unknown names also get the overview).</param>
    /// <returns>The text, always ending with a line break (raw string literals drop the final one, and a
    /// help screen that ends mid-line glues the user's next prompt onto it).</returns>
    public static string Help(string? topic) => HelpBody(topic) + "\n";

    /// <summary>The help screens without their final line break (see <see cref="Help"/>).</summary>
    /// <param name="topic">A canonical command name, or <see langword="null"/>.</param>
    /// <returns>The text.</returns>
    private static string HelpBody(string? topic) => topic switch
    {
        CliCommands.List => """
            bclip list [-n N] [--offset N] [-f FILTER] [-s SINCE] [--full] [--json]
              Recent items, newest first: id (* = pinned), kind, age, source app, first line.
              FILTER: all | pinned | text | images | links | files. --full adds each item's whole text.
            """,
        CliCommands.Search => """
            bclip search <words...> [-n N] [-f FILTER] [-s SINCE] [--full] [--json]
              Items whose text contains every word (case-insensitive substring, any language).
              Exit code 1 when nothing matches.
            """,
        CliCommands.Copy => """
            bclip copy [ID | -r N] [--plain]
              Puts an item back on the clipboard with all its formats (default: the latest item),
              like picking it in the Win+V panel but without pasting. --plain copies text only.
            """,
        CliCommands.Pin or CliCommands.Unpin => """
            bclip pin [ID | -r N]    bclip unpin [ID | -r N]
              Pinned items are kept forever: retention limits and "clear history" skip them.
            """,
        CliCommands.Delete => """
            bclip delete <ID | -r N>
              Deletes an item for good (a later import from Windows' history does not bring it back).
            """,
        CliCommands.Status => """
            bclip status [--json]
              Version, item count and size, whether capture is paused, and listener statistics.
            """,
        CliCommands.Grep => """
            bclip grep <regex> [-i] [-n N] [-f FILTER] [-s SINCE] [--full] [--json]
              Lines of recent items matching a .NET regular expression, printed as id:line: text.
              Scans the 10,000 most recently used items; exit code 1 when nothing matches.
              Quote the pattern; use -- before patterns that start with '-'.
            """,
        CliCommands.Get => """
            bclip get [ID | -r N] [--format F] [-o FILE] [--json]
              Prints an item's content exactly (default: the latest item).
              --format auto | text | html | rtf | files | png | formats
              Images are written as PNG and need -o FILE (then read the file).
            """,
        CliCommands.Put => """
            bclip put [TEXT | -]
              Copies TEXT (or standard input) to the clipboard and saves it in history.
            """,
        CliCommands.Wait => """
            bclip wait [-t SECONDS] [-o FILE] [--json]
              Waits until something new is copied (default 60 s, max 3600) and prints it.
              Exit code 1 on timeout. Handy for "copy the error and I will read it".
            """,
        _ => """
            bclip — BetterClipboard's command line (turn it on in Settings › Command line)

            Usage: bclip <command> [arguments] [options]

              list                  Recent items, newest first
              search <words...>     Items containing all words (substring, any language)
              grep <regex>          Matching lines, like grep -n (see: bclip help grep)
              get [ID]              Print an item's content (default: the latest)
              copy [ID]             Put an item back on the clipboard (default: the latest)
              put [TEXT | -]        Copy text (or stdin) to the clipboard and history
              pin [ID] | unpin [ID] Pin or unpin (default: the latest)
              delete <ID>           Delete an item
              wait                  Wait for the next copy and print it
              status                Version, item counts, capture statistics

            Options:
              -n, --limit N         Maximum items (default 20)        --offset N  Skip N items
              -f, --filter F        all | pinned | text | images | links | files
              -s, --since T         Used within T (30s, 10m, 2h, 7d, 2w) or since a date/time
              -r, --recent N        Target the N-th most recent item (1 = latest) instead of an ID
              -i, --ignore-case     grep: ignore case
                  --full            list/search/grep: include full text (useful with --json)
                  --format F        get: auto | text | html | rtf | files | png | formats
              -o, --out FILE        get/wait: write the content to FILE (required for images)
                  --plain           copy: plain text only
              -t, --timeout S       wait: seconds to wait (default 60)
                  --json            Machine-readable output (for scripts and AI agents)

            Exit codes: 0 ok · 1 nothing found / timeout · 2 bad usage · 3 BetterClipboard not
            reachable or command-line access off · 4 other error.
            """,
    };

    /// <summary>Builds the invocation from the classified tokens.</summary>
    /// <param name="command">Canonical command.</param>
    /// <param name="positional">Positional arguments.</param>
    /// <param name="options">Options by canonical name.</param>
    /// <param name="now">Clock for <c>--since</c>.</param>
    /// <returns>The invocation.</returns>
    private static CliInvocation Build(string command, List<string> positional, Dictionary<string, string?> options, DateTimeOffset now)
    {
        var request = new CliRequest { Command = command };
        bool readStdin = false;

        switch (command)
        {
            case CliCommands.Search:
                if (positional.Count == 0)
                {
                    return Fail("search needs words to look for, e.g. bclip search invoice 2026");
                }

                request = request with { Query = string.Join(' ', positional) };
                break;

            case CliCommands.Grep:
                if (positional.Count != 1)
                {
                    return Fail(positional.Count == 0 ? "grep needs a pattern." : "grep takes one pattern; quote it if it contains spaces.");
                }

                request = request with { Query = positional[0] };
                break;

            case CliCommands.Put:
                readStdin = positional.Count == 0 || positional is ["-"];
                if (!readStdin)
                {
                    request = request with { Text = string.Join(' ', positional) };
                }

                break;

            case CliCommands.Get or CliCommands.Copy or CliCommands.Pin or CliCommands.Unpin or CliCommands.Delete:
                if (positional.Count > 1)
                {
                    return Fail($"{command} takes at most one item id.");
                }

                if (positional.Count == 1)
                {
                    if (!long.TryParse(positional[0], NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0)
                    {
                        return Fail($"'{positional[0]}' is not an item id (ids are the numbers shown by bclip list).");
                    }

                    request = request with { Id = id };
                }

                break;

            default:
                if (positional.Count > 0)
                {
                    return Fail($"{command} takes no arguments (got '{positional[0]}').");
                }

                break;
        }

        foreach (var (name, value) in options)
        {
            switch (name)
            {
                case "--limit" when TryPositive(value, out int limit):
                    request = request with { Limit = limit };
                    break;
                case "--offset" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int offset):
                    request = request with { Offset = offset };
                    break;
                case "--recent" when TryPositive(value, out int recent):
                    request = request with { Recent = recent };
                    break;
                case "--timeout" when TryPositive(value, out int timeout):
                    request = request with { TimeoutSeconds = timeout };
                    break;
                case "--filter":
                    if (!CliCommandProcessor.TryParseFilter(value, out _))
                    {
                        return Fail($"Unknown filter '{value}' (use all, pinned, text, images, links or files).");
                    }

                    request = request with { Filter = value!.Trim().ToLowerInvariant() };
                    break;
                case "--since":
                    if (!TryParseSince(value!, now, out var since))
                    {
                        return Fail($"Cannot read --since '{value}' (use 30s, 10m, 2h, 7d, 2w or a date like 2026-09-25T08:00).");
                    }

                    request = request with { Since = since };
                    break;
                case "--format":
                    request = request with { Format = value };
                    break;
                case "--ignore-case":
                    request = request with { IgnoreCase = true };
                    break;
                case "--full":
                    request = request with { Full = true };
                    break;
                case "--plain":
                    request = request with { PlainText = true };
                    break;
                case "--out" or "--json":
                    break; // client-side only, handled below
                default:
                    return Fail($"Invalid value '{value}' for {name}.");
            }
        }

        if (request.Id is not null && request.Recent is not null)
        {
            return Fail("Give either an item id or --recent, not both.");
        }

        return new CliInvocation
        {
            Request = request,
            Json = options.ContainsKey("--json"),
            OutputFile = options.GetValueOrDefault("--out"),
            ReadStdin = readStdin,
        };
    }

    /// <summary>
    /// Parses <c>--since</c>: a relative span (<c>90s</c>, <c>10m</c>, <c>2h</c>, <c>7d</c>, <c>2w</c>) or
    /// an absolute date/time (local time unless it carries an offset).
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="now">Reference time for relative spans.</param>
    /// <param name="since">The instant.</param>
    /// <returns>Whether the value was understood.</returns>
    public static bool TryParseSince(string value, DateTimeOffset now, out DateTimeOffset since)
    {
        since = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        char unit = char.ToLowerInvariant(value[^1]);
        if (unit is 's' or 'm' or 'h' or 'd' or 'w' &&
            double.TryParse(value[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double amount) && amount >= 0)
        {
            var span = unit switch
            {
                's' => TimeSpan.FromSeconds(amount),
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                'd' => TimeSpan.FromDays(amount),
                _ => TimeSpan.FromDays(amount * 7),
            };
            since = now - span;
            return true;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out since);
    }

    /// <summary>Maps short options to their long names.</summary>
    /// <param name="name">As typed.</param>
    /// <returns>The canonical long name.</returns>
    private static string Canonical(string name) => name switch
    {
        "-n" => "--limit",
        "-f" => "--filter",
        "-s" => "--since",
        "-r" => "--recent",
        "-o" => "--out",
        "-t" => "--timeout",
        "-i" => "--ignore-case",
        _ => name,
    };

    /// <summary>Parses a strictly positive integer.</summary>
    /// <param name="value">Text.</param>
    /// <param name="number">The number.</param>
    /// <returns>Whether it is a positive integer.</returns>
    private static bool TryPositive(string? value, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;

    /// <summary>Whether a token like <c>-5</c> is a number (a positional value), not an option.</summary>
    /// <param name="token">Token.</param>
    /// <returns>Whether it is a negative number.</returns>
    private static bool IsNegativeNumber(string token) =>
        token.Length > 1 && double.TryParse(token, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _);

    /// <summary>A parse failure.</summary>
    /// <param name="error">Message.</param>
    /// <returns>The invocation.</returns>
    private static CliInvocation Fail(string error) => new() { Error = error };
}
