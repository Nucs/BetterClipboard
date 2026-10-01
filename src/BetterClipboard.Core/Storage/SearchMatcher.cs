using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BetterClipboard.Core.Storage;

/// <summary>
/// The exact test behind the search box's toggles (<see cref="SearchOptions"/>): whether one entry's text matches.
/// The trigram index cannot answer "match case", "whole word" or a regular expression, so <see cref="ClipStore"/>
/// runs every candidate through <see cref="IsMatch"/> inside the query (after the index narrowed what it can).
/// </summary>
/// <remarks>
/// <para>
/// <b>Words</b> (no <see cref="SearchOptions.Regex"/>): split like the classic search (whitespace, at most
/// <see cref="SearchQueryBuilder.MaxTerms"/>), and every word must be found — as a substring, compared with
/// <see cref="StringComparison.OrdinalIgnoreCase"/> or, with <see cref="SearchOptions.MatchCase"/>,
/// <see cref="StringComparison.Ordinal"/>.
/// </para>
/// <para>
/// <b>Regular expression</b>: the whole text is one .NET pattern (spaces included; not trimmed), always
/// <see cref="RegexOptions.CultureInvariant"/> and <see cref="RegexOptions.Multiline"/>, plus
/// <see cref="RegexOptions.IgnoreCase"/> unless matching case. Line breaks are matched as <c>\n</c> (CRLF and lone CR
/// are normalized first), so <c>^</c>/<c>$</c> work at every line of a Windows text — the price is that a pattern
/// spelling out <c>\r</c> finds nothing. The linear-time <see cref="RegexOptions.NonBacktracking"/> engine runs
/// whatever it supports, so a pattern like <c>(a+)+b</c> cannot hang a search; lookarounds, backreferences, atomic
/// groups, conditionals and <c>\G</c> need the backtracking engine, which gets <see cref="MatchTimeout"/> per attempt.
/// </para>
/// <para>
/// <b>Whole word</b> applies to each word, or to each match of the pattern; see <see cref="IsWholeWord"/>.
/// </para>
/// <para>
/// Entries without text (images, an empty search text) never match: a search is about text, and a pattern that
/// matches the empty string (<c>a*</c>) must not turn every picture into a hit.
/// </para>
/// Immutable and thread-safe: one instance may serve concurrent queries.
/// </remarks>
public sealed class SearchMatcher
{
    /// <summary>
    /// How long the backtracking engine may spend on one match attempt before the search is abandoned with
    /// <see cref="SearchTooSlowException"/> (the command line's <c>grep</c> uses the same budget per line).
    /// </summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>The words that must all be found, or empty for a regular expression.</summary>
    private readonly string[] terms;

    /// <summary>How words compare (ordinal, with or without case).</summary>
    private readonly StringComparison comparison;

    /// <summary>The compiled pattern, or <see langword="null"/> for words.</summary>
    private readonly Regex? regex;

    /// <summary>Whether every hit must also pass <see cref="IsWholeWord"/>.</summary>
    private readonly bool wholeWord;

    /// <summary>The search text as typed, for <see cref="SearchTooSlowException"/>.</summary>
    private readonly string pattern;

    /// <summary>Creates a matcher; see <see cref="Create"/>.</summary>
    /// <param name="options">The toggles.</param>
    /// <param name="terms">The words (empty for a regular expression).</param>
    /// <param name="regex">The compiled pattern, or <see langword="null"/> for words.</param>
    /// <param name="pattern">The search text as typed.</param>
    private SearchMatcher(SearchOptions options, string[] terms, Regex? regex, string pattern)
    {
        Options = options;
        this.terms = terms;
        this.regex = regex;
        this.pattern = pattern;
        comparison = options.HasFlag(SearchOptions.MatchCase) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        wholeWord = options.HasFlag(SearchOptions.WholeWord);
    }

    /// <summary>The toggles this matcher applies.</summary>
    public SearchOptions Options { get; }

    /// <summary>
    /// Whether the pattern runs on the backtracking engine (it uses a construct the linear-time engine lacks), so a
    /// search with it can end in <see cref="SearchTooSlowException"/>. Always <see langword="false"/> for words.
    /// </summary>
    public bool UsesBacktracking => regex is not null && (regex.Options & RegexOptions.NonBacktracking) == 0;

    /// <summary>
    /// Compiles the search box's text and toggles.
    /// </summary>
    /// <param name="search">The search text. <see langword="null"/>, empty or whitespace-only means "no search".</param>
    /// <param name="options">The toggles.</param>
    /// <returns>The matcher, or <see langword="null"/> when there is nothing to search for.</returns>
    /// <exception cref="SearchPatternException">
    /// <paramref name="options"/> has <see cref="SearchOptions.Regex"/> and <paramref name="search"/> is not a valid
    /// .NET regular expression.
    /// </exception>
    public static SearchMatcher? Create(string? search, SearchOptions options)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return null;
        }

        if (!options.HasFlag(SearchOptions.Regex))
        {
            return new SearchMatcher(options, [.. SearchQueryBuilder.SplitTerms(search, options.HasFlag(SearchOptions.MatchCase))], null, search);
        }

        var regexOptions = RegexOptions.CultureInvariant | RegexOptions.Multiline;
        if (!options.HasFlag(SearchOptions.MatchCase))
        {
            regexOptions |= RegexOptions.IgnoreCase;
        }

        Regex regex;
        try
        {
            // Linear time in the text, so no timeout: whatever the user types, one entry costs at most a scan.
            regex = new Regex(search, regexOptions | RegexOptions.NonBacktracking);
        }
        catch (NotSupportedException)
        {
            // A valid pattern the linear engine cannot run (lookaround, backreference, …): it parsed already, so this
            // constructor cannot fail. The timeout is what keeps catastrophic backtracking from hanging the search.
            regex = new Regex(search, regexOptions, MatchTimeout);
        }
        catch (RegexParseException ex)
        {
            throw new SearchPatternException(search, ReasonOf(ex), ex.Offset, ex);
        }
        catch (ArgumentException ex)
        {
            // Not a parse error with a position (e.g. a pattern the runtime rejects as a whole).
            throw new SearchPatternException(search, ex.Message, -1, ex);
        }

        return new SearchMatcher(options, [], regex, search);
    }

    /// <summary>
    /// Whether an entry's text matches.
    /// </summary>
    /// <param name="text">The entry's indexed text; <see langword="null"/> or empty never matches.</param>
    /// <param name="cancellationToken">Checked once per call, so a superseded search over thousands of entries stops at the next one.</param>
    /// <returns><see langword="true"/> when every word is found (or the pattern matches), honoring the toggles.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="SearchTooSlowException">A backtracking pattern exceeded <see cref="MatchTimeout"/> on this text.</exception>
    public bool IsMatch(string? text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (regex is null)
        {
            foreach (var term in terms)
            {
                if (!ContainsTerm(text, term))
                {
                    return false;
                }
            }

            return true;
        }

        try
        {
            return MatchesPattern(text);
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw new SearchTooSlowException(pattern, ex);
        }
    }

    /// <summary>
    /// Whether the span <c>[index, index + length)</c> of <paramref name="text"/> stands as a whole word — VS Code's
    /// rule, applied on each side: the side is at the start/end of the text, or the character beyond it is not a word
    /// character, or the span's own first/last character is not one.
    /// </summary>
    /// <remarks>
    /// The last clause is what makes words that begin or end with punctuation work, where <c>\b</c> fails:
    /// <c>.cs</c> is a whole word in "file.cs" because the span starts with a separator, and <c>-v</c> is one in
    /// "run -v" (<c>\b-v</c> would need a word character before the dash). Word characters are letters, decimal
    /// digits, combining marks and connector punctuation (<c>_</c>), as in <c>\w</c> — so "log" is not a whole word in
    /// "my_log", but is in "my-log". Characters are read as runes, so a letter outside the Basic Multilingual Plane
    /// counts as one letter rather than as two separators, and a lone surrogate counts as a separator.
    /// </remarks>
    /// <param name="text">The text.</param>
    /// <param name="index">Start of the span.</param>
    /// <param name="length">Length of the span (0 for an empty regex match).</param>
    /// <returns><see langword="true"/> when both sides are word boundaries.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The span does not lie inside <paramref name="text"/>.</exception>
    public static bool IsWholeWord(string text, int index, int length)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, text.Length - index);

        int end = index + length;
        bool left = index == 0
            || !IsWordRune(RuneEndingAt(text, index))
            || (length > 0 && !IsWordRune(RuneStartingAt(text, index)));
        bool right = end == text.Length
            || !IsWordRune(RuneStartingAt(text, end))
            || (length > 0 && !IsWordRune(RuneEndingAt(text, end)));
        return left && right;
    }

    /// <summary>Whether a word (no regular expression) occurs in <paramref name="text"/>, honoring case and whole word.</summary>
    /// <param name="text">The entry's text.</param>
    /// <param name="term">One word of the search.</param>
    /// <returns><see langword="true"/> when found.</returns>
    private bool ContainsTerm(string text, string term)
    {
        if (!wholeWord)
        {
            return text.Contains(term, comparison);
        }

        // Every occurrence until one stands alone. Ordinal comparisons (with or without case) match char for char, so
        // an occurrence is exactly term.Length long. The next search starts one char further, not after the
        // occurrence: overlaps can be whole words where the first one was not ("a-a" in "xa-a-a" at 3, not at 1).
        // Each search resumes where the last one stopped, so the cost stays about one pass over the text.
        for (int at = text.IndexOf(term, comparison); at >= 0; at = text.IndexOf(term, at + 1, comparison))
        {
            if (IsWholeWord(text, at, term.Length))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the regular expression matches <paramref name="text"/>, honoring whole word.</summary>
    /// <param name="text">The entry's text (line endings not yet normalized).</param>
    /// <returns><see langword="true"/> when matched.</returns>
    /// <exception cref="RegexMatchTimeoutException">The backtracking engine ran out of <see cref="MatchTimeout"/>.</exception>
    private bool MatchesPattern(string text)
    {
        // CRLF and lone CR become LF so '$' (which .NET only sees before '\n') ends every line; texts without a CR —
        // most of them — are matched without a copy.
        var input = text.Contains('\r') ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : text;
        if (!wholeWord)
        {
            return regex!.IsMatch(input);
        }

        // Like a global search in VS Code: the next attempt starts after the previous match (NextMatch steps past an
        // empty one). That keeps the scan linear, where retrying one char later would make a long greedy run
        // quadratic; the price is that a whole-word match overlapping a rejected one is not seen.
        for (var match = regex!.Match(input); match.Success; match = match.NextMatch())
        {
            if (IsWholeWord(input, match.Index, match.Length))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The rune that starts at <paramref name="index"/> (a lone surrogate reads as U+FFFD, a separator).</summary>
    /// <param name="text">The text.</param>
    /// <param name="index">An index below <c>text.Length</c>.</param>
    /// <returns>The rune.</returns>
    private static Rune RuneStartingAt(string text, int index) =>
        Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out _) == OperationStatus.Done ? rune : Rune.ReplacementChar;

    /// <summary>The rune that ends just before <paramref name="index"/> (a lone surrogate reads as U+FFFD, a separator).</summary>
    /// <param name="text">The text.</param>
    /// <param name="index">An index above 0.</param>
    /// <returns>The rune.</returns>
    private static Rune RuneEndingAt(string text, int index) =>
        Rune.DecodeLastFromUtf16(text.AsSpan(0, index), out var rune, out _) == OperationStatus.Done ? rune : Rune.ReplacementChar;

    /// <summary>Whether a rune is a word character: a letter, decimal digit, combining mark or connector punctuation (<c>_</c>).</summary>
    /// <param name="rune">The rune.</param>
    /// <returns><see langword="true"/> for a word character.</returns>
    private static bool IsWordRune(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.ConnectorPunctuation => true,
        _ => false,
    };

    /// <summary>
    /// The parser's reason without its "Invalid pattern '…' at offset N." preamble, which repeats what the user typed.
    /// </summary>
    /// <param name="ex">The parse error.</param>
    /// <returns>One sentence, e.g. "Not enough )'s.".</returns>
    private static string ReasonOf(RegexParseException ex)
    {
        // The preamble is "Invalid pattern '{pattern}' at offset {offset}. "; a localized runtime may word it
        // differently, in which case the whole message is kept.
        var marker = $" at offset {ex.Offset.ToString(CultureInfo.InvariantCulture)}. ";
        int at = ex.Message.IndexOf(marker, StringComparison.Ordinal);
        return at >= 0 ? ex.Message[(at + marker.Length)..].Trim() : ex.Message;
    }
}
