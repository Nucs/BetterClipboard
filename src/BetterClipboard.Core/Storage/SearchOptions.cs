namespace BetterClipboard.Core.Storage;

/// <summary>
/// How <see cref="ClipQuery.SearchText"/> matches: the toggles inside the panel's search box ("Aa", "W", ".*").
/// Flags, so they combine like an IDE's find box (e.g. a case-sensitive regular expression).
/// </summary>
/// <remarks>
/// <see cref="None"/> is the classic search: words ANDed, case-insensitive substrings, answered by the trigram index
/// alone. Any other value runs every candidate entry's indexed text through <see cref="SearchMatcher"/> inside the
/// query. That is exact, but slower: <see cref="Regex"/> scans the whole slice (the index cannot narrow a pattern),
/// while the other two still let the index narrow the candidates first. Only the indexed text is searched, cut at
/// <see cref="Content.ContentClassifier.SearchMaxChars"/> like the classic search.
/// </remarks>
[Flags]
public enum SearchOptions
{
    /// <summary>The classic search (see the enum remarks).</summary>
    None = 0,

    /// <summary>
    /// "Aa": upper and lower case must match exactly (<c>Foo</c> no longer finds <c>foo</c>). For a regular
    /// expression it drops <see cref="System.Text.RegularExpressions.RegexOptions.IgnoreCase"/>.
    /// </summary>
    MatchCase = 1,

    /// <summary>
    /// "W": each word must stand alone (<c>log</c> finds "the log" and "log.txt", not "login" or "my_log").
    /// The boundary rule is VS Code's: see <see cref="SearchMatcher.IsWholeWord"/>.
    /// </summary>
    WholeWord = 2,

    /// <summary>
    /// ".*": the whole search text is one .NET regular expression instead of words — spaces are part of the pattern,
    /// <c>^</c>/<c>$</c> match at every line, and an invalid pattern is refused with
    /// <see cref="SearchPatternException"/> instead of matching nothing.
    /// </summary>
    Regex = 4,
}
