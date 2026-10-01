using System.Text;

namespace BetterClipboard.Core.Shells;

/// <summary>
/// Reads PowerShell's history file — what PSReadLine writes to <c>%APPDATA%\Microsoft\Windows\PowerShell\PSReadLine\
/// &lt;host&gt;_history.txt</c> — with PSReadLine's own rules. Pure: the caller reads the file (see CLAUDE.md §2.16
/// for how to open it without disturbing PowerShell).
/// </summary>
/// <remarks>
/// <para>
/// <b>Format</b> (verified against PSReadLine 2.0.0 and 2.3.6): UTF-8, one accepted command per record, oldest
/// first, appended when the user presses Enter — before the command runs, failing commands included. A multi-line
/// command stores each inner line break as a backtick followed by a bare LF; the record ends with CRLF. PSReadLine
/// reads it back line by line (CRLF, LF and CR all end a line) and joins every line that ends with a backtick to the
/// next one. A one-line command that really ends with a backtick is therefore misread by PSReadLine too; this class
/// reads exactly what PowerShell's own Up arrow and Ctrl+R show, nothing cleverer.
/// </para>
/// <para>
/// The file holds no times, no host, no directory and no exit status: order is all there is. A record still being
/// written (the last line ends with a backtick and nothing follows) is dropped, like PSReadLine drops it.
/// </para>
/// </remarks>
public static class PsReadLineHistory
{
    /// <summary>
    /// Decodes the file's bytes: UTF-8 (a BOM, which PSReadLine never writes, is skipped); invalid sequences become
    /// U+FFFD rather than failing the whole file.
    /// </summary>
    /// <param name="bytes">The file's content.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bytes"/> is <see langword="null"/>.</exception>
    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }

    /// <summary>
    /// Splits the file's text into commands, oldest first, with PSReadLine's continuation rule.
    /// </summary>
    /// <param name="text">The decoded file.</param>
    /// <returns>The commands; a multi-line one has <c>\n</c> between its lines. Blank records are left out (PSReadLine never adds them).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> ParseRecords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var records = new List<string>();
        var current = new StringBuilder();
        int start = 0;
        while (start < text.Length)
        {
            // One line: up to CRLF, LF or CR (File.ReadAllLines' rule, which PSReadLine reads with).
            int length = text.AsSpan(start).IndexOfAny('\r', '\n');
            bool lastLine = length < 0;
            var line = lastLine ? text.AsSpan(start) : text.AsSpan(start, length);
            int breakAt = start + length;
            start = lastLine ? text.Length
                : breakAt + (text[breakAt] == '\r' && breakAt + 1 < text.Length && text[breakAt + 1] == '\n' ? 2 : 1);

            if (line.EndsWith("`", StringComparison.Ordinal))
            {
                // An inner line break of a multi-line command: the backtick is PSReadLine's escape, not content.
                current.Append(line[..^1]).Append('\n');
                continue;
            }

            current.Append(line);
            AddRecord(records, current);
        }

        // Whatever is left in `current` ended with a backtick and nothing after it: a record PSReadLine has not
        // finished writing (or a torn file). PSReadLine's own reader drops it too.
        return records;
    }

    /// <summary>
    /// The distinct commands, newest first, with how often each one was typed. A command typed several times is
    /// listed once, at its newest place.
    /// </summary>
    /// <param name="records">Commands oldest first (<see cref="ParseRecords"/>).</param>
    /// <returns>Text and count pairs, newest first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="records"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<(string Text, int Count)> NewestFirst(IReadOnlyList<string> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            counts[record] = counts.TryGetValue(record, out var count) ? count + 1 : 1;
        }

        var result = new List<(string, int)>(counts.Count);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        for (int i = records.Count - 1; i >= 0; i--)
        {
            if (listed.Add(records[i]))
            {
                result.Add((records[i], counts[records[i]]));
            }
        }

        return result;
    }

    /// <summary>
    /// The caption name of a history file's host, from the file name PSReadLine builds (<c>&lt;host&gt;_history.txt</c>).
    /// </summary>
    /// <param name="fileName">The file name (no folder).</param>
    /// <returns>"PowerShell" for the console host (Windows Terminal, conhost, a plain VS Code terminal — 5.1 and 7 share
    /// it), "PowerShell in VS Code" for the PowerShell extension's host, else "PowerShell (host)".</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileName"/> is <see langword="null"/>.</exception>
    public static string SourceName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        const string Suffix = "_history.txt";
        var host = fileName.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) ? fileName[..^Suffix.Length] : fileName;
        return host switch
        {
            "ConsoleHost" or "" => "PowerShell",
            "Visual Studio Code Host" => "PowerShell in VS Code",
            _ => $"PowerShell ({host})",
        };
    }

    /// <summary>Adds the record in <paramref name="current"/> unless it is blank, then clears the builder.</summary>
    /// <param name="records">The records so far.</param>
    /// <param name="current">The record being assembled.</param>
    private static void AddRecord(List<string> records, StringBuilder current)
    {
        if (current.Length > 0)
        {
            var record = current.ToString();
            if (!string.IsNullOrWhiteSpace(record))
            {
                records.Add(record);
            }
        }

        current.Clear();
    }
}
