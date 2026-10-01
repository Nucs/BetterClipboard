using System.Text;

namespace BetterClipboard.Core.Everything;

/// <summary>
/// Builds the search texts BetterClipboard sends to voidtools Everything. Everything's syntax gives meaning to
/// spaces, quotes, <c>|</c>, <c>!</c>, <c>&lt;</c>, <c>&gt;</c> and <c>modifier:</c> prefixes, so user text and
/// paths never go in raw.
/// </summary>
/// <remarks>
/// Verified against Everything 1.4.1 and 1.5 (CLAUDE.md §2.14): inside double quotes every character a Windows
/// name may contain is literal; Windows forbids <c>"</c> in names, so dropping it from a term loses nothing.
/// The short function names (<c>runcount:</c>) are used because 1.4 does not know the 1.5 spellings
/// (<c>run-count:</c> returns nothing there).
/// </remarks>
public static class EverythingQuery
{
    /// <summary>
    /// The Everything tab's query: every item opened from Everything at least once (<c>runcount:</c>), narrowed by
    /// the panel's search text — each whitespace-separated word must occur in the full path (<c>path:"word"</c>),
    /// the same "all words, anywhere, any case" rule the history search follows.
    /// </summary>
    /// <param name="searchText">The panel's search text; <see langword="null"/> or blank for every pick.</param>
    /// <returns>The search text for Everything.</returns>
    public static string Picks(string? searchText)
    {
        var builder = new StringBuilder("runcount:");
        foreach (var term in Terms(searchText))
        {
            builder.Append(" path:").Append(Quote(term));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Search text that finds one path in Everything's own window ("Show in Everything"): the path quoted, so spaces
    /// and syntax characters are literal. A trailing separator is dropped (Everything finds nothing for a folder
    /// written with one), except for a drive root.
    /// </summary>
    /// <param name="path">An absolute path.</param>
    /// <returns>The search text.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.</exception>
    public static string ForPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var trimmed = path.Length > 3 ? path.TrimEnd('\\', '/') : path;
        return Quote(trimmed);
    }

    /// <summary>
    /// An Everything command line that opens a search window showing <paramref name="search"/>: <c>-s</c> plus the
    /// text as one argument, quoted with the usual Windows rules (a <c>"</c> inside becomes <c>\"</c>, and
    /// backslashes in front of a quote or the closing quote are doubled), so a quoted path stays one argument.
    /// </summary>
    /// <param name="search">Search text, e.g. from <see cref="ForPath"/>.</param>
    /// <returns>The command line for <see cref="EverythingIpc.EncodeCommandLine"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="search"/> is <see langword="null"/>.</exception>
    public static string ShowCommandLine(string search)
    {
        ArgumentNullException.ThrowIfNull(search);
        return "-s " + QuoteArgument(search);
    }

    /// <summary>Splits search text into words, dropping the quote character (it cannot occur in a path and would end the literal).</summary>
    /// <param name="searchText">The text.</param>
    /// <returns>The non-empty words.</returns>
    private static IEnumerable<string> Terms(string? searchText) =>
        string.IsNullOrWhiteSpace(searchText)
            ? []
            : searchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.Replace("\"", string.Empty, StringComparison.Ordinal))
                .Where(t => t.Length > 0);

    /// <summary>Wraps text in Everything's literal quotes (any <c>"</c> inside is dropped).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The quoted literal.</returns>
    private static string Quote(string text) => "\"" + text.Replace("\"", string.Empty, StringComparison.Ordinal) + "\"";

    /// <summary>Quotes one command-line argument with the CommandLineToArgvW rules.</summary>
    /// <param name="argument">The argument.</param>
    /// <returns>The quoted argument.</returns>
    private static string QuoteArgument(string argument)
    {
        var builder = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char ch in argument)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                // Backslashes before a quote are doubled, and the quote itself escaped.
                builder.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(ch);
            }

            backslashes = 0;
        }

        // Backslashes before the closing quote are doubled too, or the last one would escape it.
        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }
}
