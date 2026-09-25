using System.Globalization;
using System.Text;
using BetterClipboard.Core.Presentation;

namespace BetterClipboard.Core.Cli;

/// <summary>
/// Process exit codes of <c>bclip</c> (documented in its help, stable for scripts and agents).
/// </summary>
public static class CliExitCodes
{
    /// <summary>Success.</summary>
    public const int Ok = 0;

    /// <summary>Nothing found / nothing matched / <c>wait</c> timed out (like <c>grep</c>'s 1).</summary>
    public const int NothingFound = 1;

    /// <summary>Invalid usage or arguments.</summary>
    public const int Usage = 2;

    /// <summary>BetterClipboard is not running, command-line access is off, or the pipe is not ours.</summary>
    public const int Unavailable = 3;

    /// <summary>Any other failure.</summary>
    public const int Error = 4;

    /// <summary>Maps a response to an exit code.</summary>
    /// <param name="response">The response.</param>
    /// <returns>The exit code.</returns>
    public static int For(CliResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Ok ? Ok : response.ErrorCode switch
        {
            CliErrorCodes.NotFound or CliErrorCodes.Timeout => NothingFound,
            CliErrorCodes.BadRequest => Usage,
            _ => Error,
        };
    }
}

/// <summary>
/// Human-readable rendering of responses. Content commands (<c>get</c>, <c>wait</c>) print the payload
/// verbatim — no decoration — so <c>bclip get | other-tool</c> receives exactly what was copied.
/// </summary>
public static class CliOutput
{
    private const int PreviewWidth = 72;

    /// <summary>
    /// Renders a successful response for a terminal.
    /// </summary>
    /// <remarks>
    /// Two shapes, on purpose: <c>get</c>/<c>wait</c> return the payload byte-for-byte (no added line break,
    /// so <c>bclip get 5 &gt; file</c> reproduces the copy exactly — the caller adds a newline only for a
    /// human's terminal); everything else (tables, matches, status, confirmations) is line-terminated text,
    /// so line-oriented tools (<c>read</c>, <c>wc -l</c>, agents splitting on newlines) see complete lines
    /// even when stdout is redirected.
    /// </remarks>
    /// <param name="command">The command that produced it.</param>
    /// <param name="response">The response (<see cref="CliResponse.Ok"/> = <see langword="true"/>).</param>
    /// <param name="now">Clock for relative times.</param>
    /// <returns>The text to write to stdout (may be empty).</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static string Render(string command, CliResponse response, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(response);
        switch (command)
        {
            case CliCommands.List or CliCommands.Search:
                return RenderTable(response.Items ?? [], now);
            case CliCommands.Grep:
                return RenderMatches(response.Items ?? []);
            case CliCommands.Get:
                return response.Content?.Text ?? string.Empty;
            case CliCommands.Wait:
                return response.Item?.Text ?? response.Item?.Preview ?? string.Empty;
            case CliCommands.Status when response.Status is { } status:
                return RenderStatus(status);
            default:
                // Confirmations ("Pinned item 5.") are messages, not content: always a complete line.
                return string.IsNullOrEmpty(response.Message) ? string.Empty : response.Message + "\n";
        }
    }

    /// <summary>Renders a listing as aligned columns: id (* = pinned), kind, age, source, first line.</summary>
    /// <param name="items">Items.</param>
    /// <param name="now">Clock.</param>
    /// <returns>The table.</returns>
    public static string RenderTable(IReadOnlyList<CliItem> items, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(items);
        var builder = new StringBuilder();
        foreach (var item in items)
        {
            var id = item.Id.ToString(CultureInfo.InvariantCulture) + (item.Pinned ? "*" : string.Empty);
            var age = RelativeTimeFormatter.Format(item.LastUsed, now, culture: CultureInfo.InvariantCulture);
            var source = item.Source ?? string.Empty;
            builder.Append(CultureInfo.InvariantCulture, $"{id,7}  {item.Kind,-9} {Cut(age, 14),-14} {Cut(source, 18),-18} {OneLine(item.Preview, PreviewWidth)}").Append('\n');
            if (item.Text is { } text)
            {
                builder.Append(Indent(text)).Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>Renders grep results as <c>id:line: text</c>.</summary>
    /// <param name="items">Items with <see cref="CliItem.Matches"/>.</param>
    /// <returns>The lines.</returns>
    public static string RenderMatches(IReadOnlyList<CliItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var builder = new StringBuilder();
        foreach (var item in items)
        {
            foreach (var match in item.Matches ?? [])
            {
                builder.Append(CultureInfo.InvariantCulture, $"{item.Id}:{match.Line}: {match.Text}").Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>Renders <c>status</c>.</summary>
    /// <param name="status">Status.</param>
    /// <returns>Key: value lines.</returns>
    public static string RenderStatus(CliStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var builder = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"BetterClipboard {status.Version} (protocol v{status.ProtocolVersion})\n")
            .Append(CultureInfo.InvariantCulture, $"Items:    {status.Items:N0} ({status.Pinned:N0} pinned, {FormatBytes(status.TotalBytes)})\n")
            .Append(CultureInfo.InvariantCulture, $"Capture:  {(status.CapturePaused ? "paused" : "on")}\n");

        // Only when there is something to say: most people never forget anything, and a "0" line is noise.
        if (status.Forgotten > 0)
        {
            builder.Append(CultureInfo.InvariantCulture, $"Forgotten: {status.Forgotten:N0} item{(status.Forgotten == 1 ? string.Empty : "s")} never recorded (Settings › Forgotten forever)\n");
        }

        if (status.Capture is { } c)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"Listener: {c.Notifications:N0} changes announced, {c.Read:N0} read, {c.Captured:N0} recorded, {c.Superseded:N0} overwritten before read, {c.LockedOut:N0} locked out, {c.Recovered:N0} recovered by the watchdog\n");
        }

        return builder.ToString();
    }

    /// <summary>Formats a byte count.</summary>
    /// <param name="bytes">Bytes.</param>
    /// <returns>"12.3 MB" style text.</returns>
    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"),
    };

    /// <summary>First line of a text, whitespace-collapsed and cut to <paramref name="width"/> characters.</summary>
    /// <param name="text">Text.</param>
    /// <param name="width">Maximum width.</param>
    /// <returns>The single line.</returns>
    private static string OneLine(string text, int width)
    {
        var line = text.AsSpan().TrimStart();
        int newline = line.IndexOfAny('\r', '\n');
        var first = (newline >= 0 ? line[..newline] : line).ToString().Replace('\t', ' ');
        bool more = newline >= 0 && line[newline..].Trim().Length > 0;
        return Cut(first, width - (more ? 2 : 0)) + (more ? " ↵" : string.Empty);
    }

    /// <summary>Cuts to a width with an ellipsis.</summary>
    /// <param name="text">Text.</param>
    /// <param name="width">Maximum width.</param>
    /// <returns>The cut text.</returns>
    private static string Cut(string text, int width) => text.Length <= width ? text : text[..Math.Max(0, width - 1)] + "…";

    /// <summary>Indents every line of a full text under its table row.</summary>
    /// <param name="text">Text.</param>
    /// <returns>Indented text.</returns>
    private static string Indent(string text) =>
        string.Join('\n', text.Split('\n').Select(line => "         │ " + line.TrimEnd('\r')));
}
