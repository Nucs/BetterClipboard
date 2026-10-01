namespace BetterClipboard.Core.Storage;

/// <summary>
/// The search text is not a valid regular expression (only with <see cref="SearchOptions.Regex"/>; plain words
/// can never be invalid).
/// </summary>
/// <remarks>
/// Thrown before any database work (<see cref="SearchMatcher.Create"/>), so a query never half-runs with a broken
/// pattern. It is an <see cref="ArgumentException"/> because the input is at fault, which also lets callers that
/// already catch argument errors keep working. <see cref="Reason"/> is meant for people: the panel shows it in its
/// empty state while the user is still typing the pattern (an unfinished <c>(foo</c> is a normal moment, not an error
/// to log).
/// </remarks>
public sealed class SearchPatternException : ArgumentException
{
    /// <summary>
    /// Creates the exception.
    /// </summary>
    /// <param name="pattern">The pattern as typed.</param>
    /// <param name="reason">Why it does not parse, as one sentence (e.g. "Not enough )'s.").</param>
    /// <param name="offset">Where the parser gave up: a 0-based index into <paramref name="pattern"/>, or -1 when unknown.</param>
    /// <param name="innerException">The parser's own exception (<see cref="System.Text.RegularExpressions.RegexParseException"/>).</param>
    public SearchPatternException(string pattern, string reason, int offset, Exception? innerException = null)
        : base($"Not a valid regular expression: {reason}", innerException)
    {
        Pattern = pattern;
        Reason = reason;
        Offset = offset;
    }

    /// <summary>The pattern as typed.</summary>
    public string Pattern { get; }

    /// <summary>Why it does not parse, as one sentence ending with a period, without the pattern itself.</summary>
    public string Reason { get; }

    /// <summary>Where the parser gave up: a 0-based index into <see cref="Pattern"/>, or -1 when unknown.</summary>
    public int Offset { get; }
}
