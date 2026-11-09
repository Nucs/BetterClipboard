using System.Globalization;
using System.Text;

namespace BetterClipboard.Core.Presentation;

/// <summary>
/// Which part of a setting a piece of searchable text comes from. It decides how much a match there weighs, never
/// whether a setting matches: a word found only in a status line still finds its setting.
/// </summary>
public enum SearchFieldKind
{
    /// <summary>The setting's title ("Pause capturing"): a match here weighs most.</summary>
    Header,

    /// <summary>Extra words a translator wrote for the setting (synonyms such as "hotkey" for a shortcut): weigh almost like the title.</summary>
    Keywords,

    /// <summary>The section the setting sits in ("Privacy"): typing a section's name finds all of its settings.</summary>
    Section,

    /// <summary>The setting's one-paragraph explanation.</summary>
    Description,

    /// <summary>Everything else on the card: button and option texts, status lines, list rows, accessible names.</summary>
    Other,
}

/// <summary>One piece of a setting's searchable text, and which part of the setting it is.</summary>
/// <param name="Kind">Where on the setting it comes from (decides the weight of a match).</param>
/// <param name="Text">The text as shown (any language; may hold right-to-left embedding marks, which matching skips).</param>
public sealed record SearchField(SearchFieldKind Kind, string Text);

/// <summary>
/// A stretch of a field's text that matched, in UTF-16 offsets into <see cref="SearchField.Text"/> exactly as given —
/// what a text block's highlighter takes. Marks the matching skipped (diacritics written apart, embedding controls) inside
/// the stretch are covered by it.
/// </summary>
/// <param name="Start">First matched UTF-16 unit.</param>
/// <param name="Length">How many units.</param>
public readonly record struct TextSpan(int Start, int Length);

/// <summary>
/// A search for settings, parsed once per keystroke: the query folded the way <see cref="SearchDocument"/> folds the
/// settings' texts (letter case and diacritics ignored) and split into words, every one of which must match.
/// </summary>
public sealed class SettingsSearchQuery
{
    /// <summary>
    /// The most words a query takes; the rest are ignored. Enough for any phrase someone types into a settings search, and
    /// it bounds the work per keystroke.
    /// </summary>
    public const int MaxTokens = 8;

    /// <summary>The empty query: every setting shows, nothing is highlighted.</summary>
    public static readonly SettingsSearchQuery Empty = new([]);

    /// <summary>Creates a query from words that are already folded (see <see cref="Parse"/>).</summary>
    /// <param name="tokens">The folded words, without duplicates.</param>
    private SettingsSearchQuery(IReadOnlyList<string> tokens) => Tokens = tokens;

    /// <summary>The query's words, folded (lower case, diacritics and format characters removed), each once, in typed order.</summary>
    public IReadOnlyList<string> Tokens { get; }

    /// <summary>Whether the query has no words: the page shows everything.</summary>
    public bool IsEmpty => Tokens.Count == 0;

    /// <summary>
    /// Parses what was typed into the search box. Words are separated by white space; each is folded like the settings'
    /// texts, so "Español", "espanol" and "ESPANOL" are one word. A word that folds to nothing (only marks) is dropped.
    /// </summary>
    /// <param name="text">The search box's text; <see langword="null"/> or blank is the empty query.</param>
    /// <returns>The query (<see cref="Empty"/> when there is nothing to look for).</returns>
    public static SettingsSearchQuery Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Empty;
        }

        var folded = FoldedText.Fold(text).Folded;
        var tokens = folded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxTokens)
            .ToArray();
        return tokens.Length == 0 ? Empty : new SettingsSearchQuery(tokens);
    }
}

/// <summary>
/// One setting's searchable texts, folded once so that matching each keystroke costs little: the page has about fifty
/// settings, and their texts change only when a status line does (the window rebuilds its documents then).
/// </summary>
public sealed class SearchDocument
{
    /// <summary>Folds every field of a setting.</summary>
    /// <param name="fields">
    /// The setting's texts. Their order is kept: <see cref="SettingsSearchMatch.Spans"/> is indexed like this list, so a
    /// caller that highlights the title passes it at a known index.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> or one of its texts is <see langword="null"/>.</exception>
    public SearchDocument(IReadOnlyList<SearchField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        Fields = fields;
        Folded = fields.Select(field => FoldedText.Fold(field?.Text ?? throw new ArgumentNullException(nameof(fields)))).ToArray();
    }

    /// <summary>The setting's texts as given.</summary>
    public IReadOnlyList<SearchField> Fields { get; }

    /// <summary>The same texts, folded (one per field, same order).</summary>
    internal IReadOnlyList<FoldedText> Folded { get; }
}

/// <summary>How a query matched one setting: a score, what to highlight in each field, and whether it took a fuzzy match.</summary>
/// <param name="Score">
/// Higher is a better match: the sum over the query's words of their best match, a title counting three times as much as
/// a description. The Settings page keeps its own order, so the score only decides a match's strength in tests and
/// future ranking, never which settings show.
/// </param>
/// <param name="Spans">
/// Per field, in the document's field order: the stretches that matched, sorted and merged (empty for a field that did
/// not match).
/// </param>
/// <param name="IsFuzzy">
/// Whether some word of the query is found in this setting only by its letters in order or with a typo, not as written (a
/// whole word, a word's start, inside a word, or initials). Such a match shows only while no setting matches every word
/// as written (<see cref="SettingsSearch.Shown"/>).
/// </param>
public sealed record SettingsSearchMatch(double Score, IReadOnlyList<IReadOnlyList<TextSpan>> Spans, bool IsFuzzy);

/// <summary>
/// The fuzzy matching of the Settings page's search box (the owner's request, 2026-11-09: "search to settings which
/// fuzzly match to anything of that setting"). Pure and unit-tested.
/// </summary>
/// <remarks>
/// <para>
/// <b>What matches.</b> A setting matches when every word of the query is found somewhere in its texts (its title,
/// keywords, section, description, or anything else on its card). Case, diacritics and right-to-left embedding marks are
/// ignored. Each word is tried in these ways, the best counting:
/// </para>
/// <list type="bullet">
/// <item>a whole word ("dark" in "Light, dark, or follow Windows.");</item>
/// <item>the start of a word ("hist" in "history");</item>
/// <item>the initials of consecutive words ("rfe" for "Release from Explorer");</item>
/// <item>anywhere inside the text ("board" in "clipboard"; also how Chinese, written without spaces, is searched);</item>
/// <item>its letters in order from the start of a word, close together and never across a space ("clpbrd" for
/// "clipboard", "shrtct" for "shortcut", "winv" for "Win+V");</item>
/// <item>a typo: one wrong, missing, extra or swapped letter in a word of five letters or more, two from eight letters
/// ("pasword" for "password").</item>
/// </list>
/// <para>
/// The first four find a word <em>as written</em>; the last two are fuzzy, and are tried only for a word found as written
/// nowhere in the setting, so a fuzzy match never paints a look-alike next to the real word.
/// </para>
/// <para>
/// <b>What does not.</b> A one-letter word matches only the start of a word (otherwise "a" would find every setting).
/// Letters in order must start at a word and stay close together, and typos need a word of five letters or more: a long
/// description holds most short letter sequences somewhere, and those would find settings that have nothing to do with
/// the query.
/// </para>
/// <para>
/// <b>Which settings show</b> (<see cref="Shown"/>): when some setting matches every word as written, only those; the
/// fuzzy ones are the fallback for a mistyped word. Without that rule, "theme" listed the sixteen settings that say
/// "these" or "the mouse" around the one titled "Theme" (the test desktop, 2026-11-09).
/// </para>
/// </remarks>
public static class SettingsSearch
{
    /// <summary>The weight of a match in each kind of field (the title counts three times a description).</summary>
    /// <param name="kind">The field's kind.</param>
    /// <returns>The weight.</returns>
    public static double WeightOf(SearchFieldKind kind) => kind switch
    {
        SearchFieldKind.Header => 3.0,
        SearchFieldKind.Keywords => 2.2,
        SearchFieldKind.Section => 1.8,
        SearchFieldKind.Description => 1.0,
        _ => 0.7,
    };

    /// <summary>Score of a word found as a whole word.</summary>
    internal const double ExactScore = 1.0;

    /// <summary>Score of a word found at the start of a word.</summary>
    internal const double PrefixScore = 0.85;

    /// <summary>Score of a word found as the initials of consecutive words.</summary>
    internal const double AcronymScore = 0.7;

    /// <summary>Score of a word found inside a text.</summary>
    internal const double SubstringScore = 0.6;

    /// <summary>Score of a word whose letters were found in order, before the penalty for how spread out they are.</summary>
    internal const double SubsequenceScore = 0.5;

    /// <summary>Score of a word found with one typo (each further typo costs 0.1).</summary>
    internal const double TypoScore = 0.45;

    /// <summary>Score of a word that matches the start of a longer word with one typo ("pasw" is too short; "passwrd" fits).</summary>
    internal const double PrefixTypoScore = 0.4;

    /// <summary>The shortest word a typo is forgiven in; shorter ones have too many neighbors ("file" → "five", "fine").</summary>
    internal const int MinTypoLength = 5;

    /// <summary>From this length, two typos are forgiven.</summary>
    internal const int TwoTypoLength = 8;

    /// <summary>The longest word compared for typos: longer runs (a path, a Chinese sentence) are not words people mistype.</summary>
    internal const int MaxTypoWordLength = 32;

    /// <summary>
    /// Matches a query against one setting.
    /// </summary>
    /// <param name="query">The parsed query.</param>
    /// <param name="document">The setting's folded texts.</param>
    /// <returns>
    /// How it matched, or <see langword="null"/> when a word of the query is found nowhere in the setting. The empty
    /// query matches every setting with score 0, nothing highlighted, and not fuzzy.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> or <paramref name="document"/> is <see langword="null"/>.</exception>
    public static SettingsSearchMatch? Match(SettingsSearchQuery query, SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(document);
        int fieldCount = document.Folded.Count;
        var spans = new List<TextSpan>[fieldCount];
        for (int i = 0; i < fieldCount; i++)
        {
            spans[i] = [];
        }

        double score = 0;
        bool fuzzy = false;
        foreach (var token in query.Tokens)
        {
            double best = 0;
            for (int i = 0; i < fieldCount; i++)
            {
                // Every field is searched, not only until the first hit: a word found in the description is highlighted
                // there even when the title already matched it better.
                var hit = MatchAsWritten(token, document.Folded[i], spans[i]);
                if (hit > 0)
                {
                    best = Math.Max(best, hit * WeightOf(document.Fields[i].Kind));
                }
            }

            if (best <= 0)
            {
                // Found as written nowhere in the setting: only now its letters in order or a typo, in any of its texts.
                // Asked per setting, not per field, so "Theme" in a title is never joined by a highlighted "these" below.
                for (int i = 0; i < fieldCount; i++)
                {
                    var hit = MatchFuzzy(token, document.Folded[i], spans[i]);
                    if (hit > 0)
                    {
                        best = Math.Max(best, hit * WeightOf(document.Fields[i].Kind));
                    }
                }

                if (best <= 0)
                {
                    return null; // every word must match somewhere
                }

                fuzzy = true;
            }

            score += best;
        }

        return new SettingsSearchMatch(score, spans.Select(Merge).ToArray(), fuzzy);
    }

    /// <summary>
    /// Which settings the page shows for one query: when any setting matches every word as written (a whole word, a word's
    /// start, inside a word, or initials), only those; when none does, every match, typos and letters in order included.
    /// A fuzzy match is the fallback for a mistyped word, never company for the setting that has the word: "theme" shows
    /// the setting titled "Theme", not also the sixteen that say "these" (found in the test desktop, 2026-11-09).
    /// </summary>
    /// <param name="matches">Each setting's match in page order; <see langword="null"/> for a setting that did not match.</param>
    /// <returns>One flag per setting, in the same order: <see langword="true"/> for the settings that show.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="matches"/> is <see langword="null"/>.</exception>
    public static bool[] Shown(IReadOnlyList<SettingsSearchMatch?> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        bool anyAsWritten = matches.Any(match => match is { IsFuzzy: false });
        var shown = new bool[matches.Count];
        for (int i = 0; i < matches.Count; i++)
        {
            shown[i] = matches[i] is { } match && !(anyAsWritten && match.IsFuzzy);
        }

        return shown;
    }

    /// <summary>
    /// How one query word matches one folded text as written, adding what to highlight to <paramref name="spans"/>: every
    /// place it occurs (a whole word, a word's start, inside a word), or — when it occurs nowhere in this text — the
    /// initials of consecutive words.
    /// </summary>
    /// <param name="token">The folded word (never empty).</param>
    /// <param name="text">The folded field.</param>
    /// <param name="spans">Receives the original-text stretches to highlight.</param>
    /// <returns>
    /// <see cref="ExactScore"/>, <see cref="PrefixScore"/>, <see cref="AcronymScore"/> or <see cref="SubstringScore"/>
    /// (before the field's weight), 0 when the word is not found as written here.
    /// </returns>
    private static double MatchAsWritten(string token, FoldedText text, List<TextSpan> spans)
    {
        if (text.Folded.Length == 0)
        {
            return 0;
        }

        double best = MatchOccurrences(token, text, spans);
        if (best >= PrefixScore)
        {
            return best; // a whole word or a word start: initials cannot beat it
        }

        if (token.Length >= 2 && token.All(char.IsLetterOrDigit) && Acronym(token, text) is { } acronym && acronym.Score > best)
        {
            // Initials outrank a find inside a word ("rfe" inside "surfer"), but the highlight stays on real occurrences.
            if (best <= 0)
            {
                spans.AddRange(acronym.Spans);
            }

            best = acronym.Score;
        }

        return best;
    }

    /// <summary>
    /// How one query word matches one folded text fuzzily — its letters in order, or with typos — adding the best fuzzy
    /// match's stretches to <paramref name="spans"/>. Asked only for a word the setting has nowhere as written.
    /// </summary>
    /// <param name="token">The folded word (never empty).</param>
    /// <param name="text">The folded field.</param>
    /// <param name="spans">Receives the original-text stretches to highlight.</param>
    /// <returns>The better of the letters-in-order and typo scores (before the field's weight), 0 when neither matches.</returns>
    private static double MatchFuzzy(string token, FoldedText text, List<TextSpan> spans)
    {
        if (text.Folded.Length == 0 || !token.All(char.IsLetterOrDigit))
        {
            return 0; // "+v" or a path is matched as written or not at all
        }

        double best = 0;
        if (token.Length >= 3 && Subsequence(token, text) is { } subsequence)
        {
            best = subsequence.Score;
            spans.AddRange(subsequence.Spans);
        }

        if (token.Length >= MinTypoLength && token.All(char.IsLetter) && Typo(token, text) is { } typo && typo.Score > best)
        {
            // The letters in order, when found, stay highlighted: they show which letters were meant better than a whole word.
            if (best <= 0)
            {
                spans.AddRange(typo.Spans);
            }

            best = typo.Score;
        }

        return best;
    }

    /// <summary>
    /// Finds every occurrence of the word as written: a whole word, a word's start, or anywhere inside (a one-letter word
    /// only at a word's start). Adds each occurrence to <paramref name="spans"/>.
    /// </summary>
    /// <param name="token">The folded word.</param>
    /// <param name="text">The folded field.</param>
    /// <param name="spans">Receives the occurrences.</param>
    /// <returns><see cref="ExactScore"/>, <see cref="PrefixScore"/>, <see cref="SubstringScore"/>, or 0.</returns>
    private static double MatchOccurrences(string token, FoldedText text, List<TextSpan> spans)
    {
        double best = 0;
        var folded = text.Folded;
        bool startsWithWordChar = IsWordChar(token[0]);
        bool endsWithWordChar = IsWordChar(token[^1]);
        for (int at = folded.IndexOf(token, StringComparison.Ordinal); at >= 0; at = folded.IndexOf(token, at + 1, StringComparison.Ordinal))
        {
            // A word start (or end) only means something when the word itself starts (or ends) with a letter or digit:
            // "+v" in "Win+V" is simply found inside.
            bool wordStart = startsWithWordChar && (at == 0 || !IsWordChar(folded[at - 1]));
            int end = at + token.Length;
            bool wordEnd = endsWithWordChar && (end == folded.Length || !IsWordChar(folded[end]));
            double kind = wordStart && wordEnd ? ExactScore : wordStart ? PrefixScore : SubstringScore;
            if (token.Length == 1 && kind < PrefixScore)
            {
                continue; // a single letter inside a word is noise
            }

            best = Math.Max(best, kind);
            spans.Add(text.ToOriginal(at, token.Length));
        }

        return best;
    }

    /// <summary>
    /// The word's letters as the initials of consecutive words ("rfe" → "Release from Explorer"). The first such run wins.
    /// </summary>
    /// <param name="token">The folded word (letters and digits only, two or more).</param>
    /// <param name="text">The folded field.</param>
    /// <returns>The match with each initial as a stretch, or <see langword="null"/>.</returns>
    private static FuzzyHit? Acronym(string token, FoldedText text)
    {
        var words = text.Words;
        for (int first = 0; first + token.Length <= words.Count; first++)
        {
            bool all = true;
            for (int k = 0; k < token.Length && all; k++)
            {
                all = text.Folded[words[first + k].Start] == token[k];
            }

            if (all)
            {
                var spans = new List<TextSpan>(token.Length);
                for (int k = 0; k < token.Length; k++)
                {
                    spans.Add(text.ToOriginal(words[first + k].Start, 1));
                }

                return new FuzzyHit(AcronymScore, spans);
            }
        }

        return null;
    }

    /// <summary>
    /// The word's letters in order, starting at a word's first letter and staying within a short stretch
    /// (<c>max(2 × length, length + 3)</c> characters) that holds no space: "clpbrd" in "clipboard", "winv" in "Win+V",
    /// never "theme" in "the mouse". The tightest such stretch wins.
    /// </summary>
    /// <param name="token">The folded word (letters and digits only, three or more).</param>
    /// <param name="text">The folded field.</param>
    /// <returns>The match with each found letter as a stretch, or <see langword="null"/>.</returns>
    private static FuzzyHit? Subsequence(string token, FoldedText text)
    {
        var folded = text.Folded;
        int maxSpan = Math.Max(token.Length * 2, token.Length + 3);
        int bestSpan = int.MaxValue;
        int[]? bestPositions = null;
        var positions = new int[token.Length];
        foreach (var word in text.Words)
        {
            if (folded[word.Start] != token[0])
            {
                continue;
            }

            positions[0] = word.Start;
            int limit = Math.Min(folded.Length, word.Start + maxSpan);
            int k = 1;
            for (int i = word.Start + 1; i < limit && k < token.Length; i++)
            {
                if (char.IsWhiteSpace(folded[i]))
                {
                    break; // letters spread over two words are a coincidence, not a word typed in short
                }

                if (folded[i] == token[k])
                {
                    positions[k++] = i;
                }
            }

            if (k == token.Length)
            {
                int span = positions[^1] - positions[0] + 1;
                if (span < bestSpan)
                {
                    bestSpan = span;
                    bestPositions = (int[])positions.Clone();
                }
            }
        }

        if (bestPositions is null)
        {
            return null;
        }

        // Compactness: all letters next to each other would be a substring (scored higher above); spread out costs.
        double score = SubsequenceScore * token.Length / bestSpan;
        var spans = bestPositions.Select(position => text.ToOriginal(position, 1)).ToList();
        return new FuzzyHit(score, spans);
    }

    /// <summary>
    /// The word against every word of the text with typos allowed (Damerau–Levenshtein, adjacent swaps counting once): one
    /// typo from <see cref="MinTypoLength"/> letters, two from <see cref="TwoTypoLength"/>. Also the start of a longer word
    /// with one typo. The closest word wins, the first among equals.
    /// </summary>
    /// <param name="token">The folded word (letters only, <see cref="MinTypoLength"/> or more).</param>
    /// <param name="text">The folded field.</param>
    /// <returns>The match with the whole matched word (or matched start) as the stretch, or <see langword="null"/>.</returns>
    private static FuzzyHit? Typo(string token, FoldedText text)
    {
        int allowed = token.Length >= TwoTypoLength ? 2 : 1;
        FuzzyHit? best = null;
        foreach (var word in text.Words)
        {
            if (word.Length > MaxTypoWordLength)
            {
                continue;
            }

            if (Math.Abs(word.Length - token.Length) <= allowed)
            {
                int distance = Distance(token, text.Folded, word.Start, word.Length, allowed);
                if (distance <= allowed)
                {
                    double score = TypoScore - (0.1 * (distance - 1));
                    if (best is null || score > best.Score)
                    {
                        best = new FuzzyHit(score, [text.ToOriginal(word.Start, word.Length)]);
                    }

                    continue;
                }
            }

            // "passwrd" as the start of "passwords": compare with the word's first letters, one typo allowed.
            if (word.Length > token.Length)
            {
                for (int length = token.Length - 1; length <= token.Length + 1; length++)
                {
                    if (length < word.Length && Distance(token, text.Folded, word.Start, length, 1) <= 1)
                    {
                        if (best is null || PrefixTypoScore > best.Score)
                        {
                            best = new FuzzyHit(PrefixTypoScore, [text.ToOriginal(word.Start, length)]);
                        }

                        break;
                    }
                }
            }
        }

        return best;
    }

    /// <summary>
    /// The optimal-string-alignment distance (Damerau–Levenshtein without repeated edits of one stretch) between the word
    /// and a stretch of the text, giving up early once it must exceed <paramref name="max"/>.
    /// </summary>
    /// <param name="a">The query word.</param>
    /// <param name="text">The folded text holding the other word.</param>
    /// <param name="start">Where the other word starts in <paramref name="text"/>.</param>
    /// <param name="length">The other word's length.</param>
    /// <param name="max">The largest distance of interest.</param>
    /// <returns>The distance, or <c>max + 1</c> when it is larger than <paramref name="max"/>.</returns>
    internal static int Distance(string a, string text, int start, int length, int max)
    {
        if (Math.Abs(a.Length - length) > max)
        {
            return max + 1;
        }

        // Three rows: the one before the previous is what a swap of two letters needs.
        var previous2 = new int[length + 1];
        var previous = new int[length + 1];
        var current = new int[length + 1];
        for (int j = 0; j <= length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            int rowMin = current[0];
            for (int j = 1; j <= length; j++)
            {
                char bj = text[start + j - 1];
                int cost = a[i - 1] == bj ? 0 : 1;
                int value = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == text[start + j - 2] && a[i - 2] == bj)
                {
                    value = Math.Min(value, previous2[j - 2] + 1);
                }

                current[j] = value;
                rowMin = Math.Min(rowMin, value);
            }

            if (rowMin > max)
            {
                return max + 1; // every alignment is already too far apart
            }

            (previous2, previous, current) = (previous, current, previous2);
        }

        return Math.Min(previous[length], max + 1);
    }

    /// <summary>Sorts stretches and merges those that touch or overlap, so a highlighter paints each letter once.</summary>
    /// <param name="spans">The stretches of one field.</param>
    /// <returns>Sorted, disjoint stretches.</returns>
    private static IReadOnlyList<TextSpan> Merge(List<TextSpan> spans)
    {
        if (spans.Count <= 1)
        {
            return spans.ToArray();
        }

        var sorted = spans.OrderBy(s => s.Start).ThenByDescending(s => s.Length).ToList();
        var merged = new List<TextSpan> { sorted[0] };
        foreach (var span in sorted.Skip(1))
        {
            var last = merged[^1];
            int lastEnd = last.Start + last.Length;
            if (span.Start <= lastEnd)
            {
                merged[^1] = new TextSpan(last.Start, Math.Max(lastEnd, span.Start + span.Length) - last.Start);
            }
            else
            {
                merged.Add(span);
            }
        }

        return merged;
    }

    /// <summary>Whether a folded character belongs to a word: a letter or a digit (Chinese characters are letters).</summary>
    /// <param name="c">The character.</param>
    /// <returns><see langword="true"/> for a word character.</returns>
    internal static bool IsWordChar(char c) => char.IsLetterOrDigit(c);

    /// <summary>A fuzzy match of one word in one field: its score and what to highlight.</summary>
    /// <param name="Score">The score before the field's weight.</param>
    /// <param name="Spans">The stretches of the original text.</param>
    private sealed record FuzzyHit(double Score, IReadOnlyList<TextSpan> Spans);
}

/// <summary>
/// A text folded for matching — lower case, diacritics written apart or composed into a letter removed, format
/// characters (right-to-left embedding, joiners) skipped — with the way back from each folded character to the original
/// text, and the folded text's words.
/// </summary>
internal sealed class FoldedText
{
    /// <summary>Creates the folded form.</summary>
    /// <param name="original">The original text.</param>
    /// <param name="folded">The folded text.</param>
    /// <param name="map">For each folded character, the index of the original character it came from.</param>
    private FoldedText(string original, string folded, int[] map)
    {
        Original = original;
        Folded = folded;
        Map = map;
        var words = new List<(int Start, int Length)>();
        for (int i = 0; i < folded.Length;)
        {
            if (!SettingsSearch.IsWordChar(folded[i]))
            {
                i++;
                continue;
            }

            int start = i;
            while (i < folded.Length && SettingsSearch.IsWordChar(folded[i]))
            {
                i++;
            }

            words.Add((start, i - start));
        }

        Words = words;
    }

    /// <summary>The text as given.</summary>
    public string Original { get; }

    /// <summary>The folded text matching runs on.</summary>
    public string Folded { get; }

    /// <summary>For each folded character, the index of the original character it came from.</summary>
    public int[] Map { get; }

    /// <summary>The folded text's words (runs of letters and digits), in order.</summary>
    public IReadOnlyList<(int Start, int Length)> Words { get; }

    /// <summary>
    /// Folds a text: every character lowered with the invariant culture; a letter with diacritics becomes its base letter
    /// ("é" → "e", "Ё" → "е"); combining marks and format characters are dropped. One folded character never stands for
    /// more than one original character, so a match maps back exactly.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The folded form.</returns>
    public static FoldedText Fold(string text)
    {
        var folded = new StringBuilder(text.Length);
        var map = new List<int>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c < 0x80)
            {
                // The common case first: ASCII has no marks to strip.
                folded.Append(char.ToLowerInvariant(c));
                map.Add(i);
                continue;
            }

            if (char.IsSurrogate(c))
            {
                folded.Append(c); // kept as is: no letter beyond the BMP needs folding for a settings search
                map.Add(i);
                continue;
            }

            var category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Format or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                continue; // right-to-left embedding of Hebrew text, Hebrew points, a combining accent: invisible to matching
            }

            folded.Append(char.ToLowerInvariant(BaseLetter(c)));
            map.Add(i);
        }

        return new FoldedText(text, folded.ToString(), map.ToArray());
    }

    /// <summary>
    /// Maps a stretch of the folded text back to the original text, covering anything skipped inside it.
    /// </summary>
    /// <param name="start">Start in the folded text.</param>
    /// <param name="length">Length in the folded text (one or more).</param>
    /// <returns>The stretch of the original text.</returns>
    public TextSpan ToOriginal(int start, int length)
    {
        int first = Map[start];
        int last = Map[start + length - 1];

        // A surrogate pair is two folded characters mapped one to one; a combining mark right after the last letter belongs
        // to it ("e" + accent), so the highlight covers it too.
        int end = last + 1;
        while (end < Original.Length && CharUnicodeInfo.GetUnicodeCategory(Original[end]) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
        {
            end++;
        }

        return new TextSpan(first, end - first);
    }

    /// <summary>
    /// The base letter of a precomposed letter ("é" → "e"), or the character itself when its decomposition is not a letter
    /// followed only by marks (a Hangul syllable decomposes into letters, which must not be cut to the first).
    /// </summary>
    /// <param name="c">A non-ASCII, non-surrogate character.</param>
    /// <returns>The character to fold.</returns>
    private static char BaseLetter(char c)
    {
        var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
        if (decomposed.Length == 1)
        {
            return decomposed[0];
        }

        for (int i = 1; i < decomposed.Length; i++)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(decomposed[i]) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
            {
                return c;
            }
        }

        return decomposed[0];
    }
}
