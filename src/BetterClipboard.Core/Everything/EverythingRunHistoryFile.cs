using System.Globalization;
using System.Text;

namespace BetterClipboard.Core.Everything;

/// <summary>
/// Reads Everything's <c>Run History.csv</c>: the run history as Everything last saved it. The Everything tab
/// falls back to it while Everything is not running, when the live query has nobody to ask.
/// </summary>
/// <remarks>
/// <para>
/// <b>Format</b> (verified with 1.4.1.1032 and 1.5.0.1423b): a header <c>Filename,Run Count,Last Run Date</c>,
/// then one row per path — the path quoted (<c>""</c> inside a quoted field is a quote), the count, and the last
/// run as a FILETIME in decimal (UTC). Columns are found by their header names, so a future column order still
/// reads; a row whose path is blank is skipped, and an unreadable count or date only blanks that field.
/// </para>
/// <para>
/// <b>When it is current.</b> Everything writes the file only when it saves (at exit, or on
/// <c>EVERYTHING_IPC_SAVE_RUN_HISTORY</c>), never on a pick. While Everything runs the file lags behind and the
/// live query is the truth; once it has exited, the file is complete. It is a snapshot of paths only: whether a
/// file still exists, and whether it is a folder, the caller checks itself.
/// </para>
/// </remarks>
public static class EverythingRunHistoryFile
{
    /// <summary>The file name of the unnamed instance (1.4 and 1.5 beta).</summary>
    public const string FileName = "Run History.csv";

    /// <summary>Largest file read; a real run history is a few hundred KB at most, and a corrupt multi-GB file must not be loaded whole.</summary>
    public const long MaxFileBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Where an instance's run history can be: <c>%APPDATA%\Everything</c> (installed, the default "store settings
    /// in %APPDATA%") and next to <c>Everything.exe</c> (portable, or that setting off). A named instance appends
    /// <c>-name</c> to the file name (verified: <c>Run History-BCPROBE140580.csv</c>; the 1.5 alpha used <c>-1.5a</c>).
    /// </summary>
    /// <param name="appDataFolder"><c>%APPDATA%</c>, or <see langword="null"/> when unknown.</param>
    /// <param name="executableFolder">The folder of <c>Everything.exe</c>, or <see langword="null"/> when unknown.</param>
    /// <param name="instance">Instance name; <see langword="null"/> or empty for the unnamed instance.</param>
    /// <returns>Candidate paths, most likely first; the caller reads the newest one that exists.</returns>
    public static IReadOnlyList<string> CandidatePaths(string? appDataFolder, string? executableFolder, string? instance)
    {
        var name = string.IsNullOrEmpty(instance) ? FileName : $"Run History-{instance}.csv";
        var paths = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(appDataFolder))
        {
            paths.Add(Path.Combine(appDataFolder, "Everything", name));
        }

        if (!string.IsNullOrWhiteSpace(executableFolder))
        {
            paths.Add(Path.Combine(executableFolder, name));
        }

        return paths;
    }

    /// <summary>
    /// Parses the file's text into picks, newest run first (rows without a date last, then by run count).
    /// </summary>
    /// <param name="text">The whole file as text.</param>
    /// <returns>The picks; empty for an empty file or one without a recognizable header.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<EverythingPick> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var rows = ReadRecords(text).GetEnumerator();
        if (!rows.MoveNext())
        {
            return [];
        }

        var header = rows.Current;
        int pathColumn = header.FindIndex(h => h.Equals("Filename", StringComparison.OrdinalIgnoreCase));
        int countColumn = header.FindIndex(h => h.Equals("Run Count", StringComparison.OrdinalIgnoreCase));
        int dateColumn = header.FindIndex(h => h.Equals("Last Run Date", StringComparison.OrdinalIgnoreCase));
        if (pathColumn < 0)
        {
            // Not a run history (or a format we do not know): better nothing than misread columns.
            return [];
        }

        var picks = new List<EverythingPick>();
        while (rows.MoveNext())
        {
            var row = rows.Current;
            var path = Field(row, pathColumn)?.Trim();
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            uint count = uint.TryParse(Field(row, countColumn), NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : 1;
            DateTimeOffset? lastRun = long.TryParse(Field(row, dateColumn), NumberStyles.None, CultureInfo.InvariantCulture, out var fileTime)
                ? EverythingIpc.FromFileTime(fileTime)
                : null;
            picks.Add(new EverythingPick(path, IsFolder: false, Size: null, Modified: null, RunCount: count, LastOpened: lastRun));
        }

        return picks
            .OrderByDescending(p => p.LastOpened ?? DateTimeOffset.MinValue)
            .ThenByDescending(p => p.RunCount)
            .ToList();
    }

    /// <summary>The field at <paramref name="index"/>, or <see langword="null"/> when the column is missing or the row is short.</summary>
    /// <param name="row">A record's fields.</param>
    /// <param name="index">Column index (−1 = not in the header).</param>
    /// <returns>The field.</returns>
    private static string? Field(List<string> row, int index) => index >= 0 && index < row.Count ? row[index] : null;

    /// <summary>
    /// Splits CSV text into records (RFC 4180: commas separate, quotes enclose, <c>""</c> is a quote inside quotes,
    /// a line break inside quotes belongs to the field). Blank lines are skipped. A file cut off inside a quoted
    /// field (Everything killed mid-write, a full disk) loses that last record: half a path names a file that does
    /// not exist, and offering it to paste would be worse than leaving it out.
    /// </summary>
    /// <param name="text">The CSV text.</param>
    /// <returns>Each record's fields.</returns>
    private static IEnumerable<List<string>> ReadRecords(string text)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    quoted = true;
                    any = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    any = true;
                    break;
                case '\r' or '\n':
                    if (any || field.Length > 0)
                    {
                        fields.Add(field.ToString());
                        yield return fields;
                        fields = [];
                    }

                    field.Clear();
                    any = false;
                    break;
                case '\uFEFF' when i == 0:
                    // A UTF-8 byte order mark decoded as text.
                    break;
                default:
                    field.Append(ch);
                    any = true;
                    break;
            }
        }

        // Still inside quotes at the end: the file was cut off mid-field (see summary), so the record is dropped.
        if (!quoted && (any || field.Length > 0))
        {
            fields.Add(field.ToString());
            yield return fields;
        }
    }
}
