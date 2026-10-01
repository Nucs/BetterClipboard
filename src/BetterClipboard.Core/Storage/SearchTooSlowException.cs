namespace BetterClipboard.Core.Storage;

/// <summary>
/// A regular expression took longer than <see cref="SearchMatcher.MatchTimeout"/> on a single entry — typically
/// catastrophic backtracking (<c>(a+)+b</c> against a long run of <c>a</c>) — so the whole search was abandoned.
/// </summary>
/// <remarks>
/// Only patterns the linear-time engine cannot run (lookarounds, backreferences, atomic groups, conditionals,
/// <c>\G</c>) can end up here: those fall back to the backtracking engine, which needs the timeout. Failing the
/// whole search on the first slow entry is deliberate — skipping it would silently hide entries that do match, and
/// carrying on would let one pattern pin a core for minutes over a large history.
/// </remarks>
public sealed class SearchTooSlowException : TimeoutException
{
    /// <summary>
    /// Creates the exception.
    /// </summary>
    /// <param name="pattern">The pattern as typed.</param>
    /// <param name="innerException">The engine's timeout (<see cref="System.Text.RegularExpressions.RegexMatchTimeoutException"/>).</param>
    public SearchTooSlowException(string pattern, Exception? innerException = null)
        : base($"The regular expression took longer than {SearchMatcher.MatchTimeout.TotalMilliseconds:0} ms on one item.", innerException)
    {
        Pattern = pattern;
    }

    /// <summary>The pattern as typed.</summary>
    public string Pattern { get; }
}
