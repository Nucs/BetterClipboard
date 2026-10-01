using System.Text;

namespace BetterClipboard.Core.Storage;

/// <summary>
/// Translates a user's search box text into an FTS5 trigram <c>MATCH</c> expression plus <c>LIKE</c>
/// patterns for terms too short for trigrams.
/// </summary>
/// <remarks>
/// The trigram tokenizer gives substring semantics ("board" finds "clipboard"), which is what users
/// expect from a clipboard search, but it cannot match anything shorter than three characters — those
/// terms become <c>LIKE '%x%'</c> filters instead. Every term is quoted as an FTS5 string so operators
/// (<c>AND</c>, <c>NEAR</c>, <c>*</c>, <c>:</c>, <c>-</c>) typed by the user are treated literally and can
/// never produce an FTS syntax error.
/// </remarks>
internal static class SearchQueryBuilder
{
    /// <summary>Upper bound on terms so a pasted paragraph in the search box cannot build a huge query.</summary>
    internal const int MaxTerms = 8;

    /// <summary>The escape character used in generated <c>LIKE</c> patterns.</summary>
    internal const char LikeEscape = '\\';

    /// <summary>
    /// Builds the search predicates.
    /// </summary>
    /// <param name="search">Raw search text; <see langword="null"/> or blank means "no search".</param>
    /// <returns>
    /// The FTS expression (<see langword="null"/> when no term is long enough) and the LIKE patterns
    /// (already wrapped in <c>%</c> and escaped with <see cref="LikeEscape"/>).
    /// </returns>
    public static (string? FtsExpression, IReadOnlyList<string> LikePatterns) Build(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return (null, []);
        }

        var terms = SplitTerms(search, matchCase: false);

        var fts = new StringBuilder();
        var likes = new List<string>();
        foreach (var term in terms)
        {
            // Count text elements rather than UTF-16 units so a single emoji (2 units) is not treated as
            // a 2-char term and a 3-letter Hebrew/CJK word is correctly routed to the trigram index.
            if (new System.Globalization.StringInfo(term).LengthInTextElements >= 3)
            {
                AppendFtsTerm(fts, term);
            }
            else
            {
                likes.Add("%" + EscapeLike(term) + "%");
            }
        }

        return (fts.Length > 0 ? fts.ToString() : null, likes);
    }

    /// <summary>
    /// Builds the predicates that narrow the candidates of a search with <see cref="SearchOptions"/> before
    /// <see cref="SearchMatcher"/> decides each one exactly: they may keep entries that do not match, but must
    /// never drop one that does.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><see cref="SearchOptions.Regex"/>: nothing — the index cannot see inside a pattern, so the matcher scans
    /// the whole slice.</item>
    /// <item>Words (with <see cref="SearchOptions.MatchCase"/> and/or <see cref="SearchOptions.WholeWord"/>): the same
    /// trigram terms as <see cref="Build"/>, a superset because the index folds case and a whole word is also a
    /// substring. A short word becomes a <c>LIKE</c> only when that is a superset too: <c>LIKE</c> folds ASCII
    /// letters only, so a case-insensitive "Éa" would drop "éa" — such a word is left to the matcher alone.</item>
    /// </list>
    /// Footgun: the trigram index folds case by Unicode's simple folding, <see cref="SearchMatcher"/> by
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> (upper-casing). They disagree only for oddities such as the
    /// dotless ı, which the matcher equates with i and the index does not; such an entry is narrowed away.
    /// </remarks>
    /// <param name="search">Raw search text; <see langword="null"/> or blank means "no search".</param>
    /// <param name="options">The search box's toggles (not <see cref="SearchOptions.None"/>: that is <see cref="Build"/>).</param>
    /// <returns>The FTS expression (<see langword="null"/> when none applies) and the escaped LIKE patterns.</returns>
    public static (string? FtsExpression, IReadOnlyList<string> LikePatterns) BuildPrefilter(string? search, SearchOptions options)
    {
        if (string.IsNullOrWhiteSpace(search) || options.HasFlag(SearchOptions.Regex))
        {
            return (null, []);
        }

        bool matchCase = options.HasFlag(SearchOptions.MatchCase);
        var fts = new StringBuilder();
        var likes = new List<string>();
        foreach (var term in SplitTerms(search, matchCase))
        {
            if (new System.Globalization.StringInfo(term).LengthInTextElements >= 3)
            {
                AppendFtsTerm(fts, term);
            }
            else if (matchCase || IsCaseSafeForLike(term))
            {
                likes.Add("%" + EscapeLike(term) + "%");
            }
        }

        return (fts.Length > 0 ? fts.ToString() : null, likes);
    }

    /// <summary>
    /// Whether a case-insensitive <c>LIKE</c> finds every case variant of <paramref name="term"/>: SQLite folds only
    /// ASCII letters, so the term may hold nothing else that has an upper or lower case form. Caseless scripts
    /// (Hebrew, Arabic, CJK), digits and symbols qualify; "é" does not.
    /// </summary>
    /// <param name="term">A raw search term.</param>
    /// <returns><see langword="true"/> when <c>LIKE '%term%'</c> keeps every entry the matcher could accept.</returns>
    private static bool IsCaseSafeForLike(string term)
    {
        // By rune, not by char: a surrogate half has no case mapping of its own, which would let a cased letter
        // outside the Basic Multilingual Plane (Deseret, Adlam) pass as caseless.
        foreach (var rune in term.EnumerateRunes())
        {
            if (!rune.IsAscii && (Rune.ToUpperInvariant(rune) != rune || Rune.ToLowerInvariant(rune) != rune))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Splits search text into the words that must all be found: whitespace-separated, duplicates dropped, at most
    /// <see cref="MaxTerms"/>. Shared by the index predicates and <see cref="SearchMatcher"/>, so both always agree on
    /// which words a search has.
    /// </summary>
    /// <param name="search">Raw search text (not blank).</param>
    /// <param name="matchCase">
    /// Whether "Foo" and "foo" are different words (match case: both must be found) or the same one (dropped as a
    /// duplicate — the classic search).
    /// </param>
    /// <returns>The words, in typing order.</returns>
    internal static List<string> SplitTerms(string search, bool matchCase) => search
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(matchCase ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
        .Take(MaxTerms)
        .ToList();

    /// <summary>Appends one term to an FTS expression as a quoted string (operators typed by the user stay literal), ANDed.</summary>
    /// <param name="fts">The expression built so far.</param>
    /// <param name="term">A raw search term.</param>
    private static void AppendFtsTerm(StringBuilder fts, string term)
    {
        if (fts.Length > 0)
        {
            fts.Append(" AND ");
        }

        fts.Append('"').Append(term.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
    }

    /// <summary>Escapes <c>LIKE</c> wildcards so user input like <c>50%</c> is matched literally.</summary>
    /// <param name="term">A raw search term.</param>
    /// <returns>The escaped term.</returns>
    private static string EscapeLike(string term)
    {
        var builder = new StringBuilder(term.Length + 4);
        foreach (var c in term)
        {
            if (c is '%' or '_' or LikeEscape)
            {
                builder.Append(LikeEscape);
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
