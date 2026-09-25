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

        var terms = search
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTerms)
            .ToList();

        var fts = new StringBuilder();
        var likes = new List<string>();
        foreach (var term in terms)
        {
            // Count text elements rather than UTF-16 units so a single emoji (2 units) is not treated as
            // a 2-char term and a 3-letter Hebrew/CJK word is correctly routed to the trigram index.
            if (new System.Globalization.StringInfo(term).LengthInTextElements >= 3)
            {
                if (fts.Length > 0)
                {
                    fts.Append(" AND ");
                }

                fts.Append('"').Append(term.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                likes.Add("%" + EscapeLike(term) + "%");
            }
        }

        return (fts.Length > 0 ? fts.ToString() : null, likes);
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
