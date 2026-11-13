namespace BetterClipboard.Core.TextView;

/// <summary>
/// Colors code: cuts a text into <see cref="SyntaxToken"/>s (comments, strings, keywords, tags, …) for the text viewer,
/// one small lexer per language family. It only colors — it never decides what is valid code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why its own lexers.</b> The viewer needs colors for a few dozen languages, instantly and offline, for texts up to
/// megabytes, and nothing in the app may run or fetch what it shows. A tokenizer per family (C-like, scripting, markup,
/// data, diff) with keyword tables is a few hundred lines; a highlighting library would add a dependency to ship and to
/// credit for the same result (the Markdown parser's reasoning, <see cref="Presentation.MarkdownParser"/>).
/// </para>
/// <para>
/// <b>Never throws, never hangs.</b> Every lexer walks the text once, left to right; an unterminated string or comment
/// simply ends at the end of its line or of the text. Only the first <c>maxChars</c> characters are colored: past them the
/// text is shown in the default color (a 50 MB log would otherwise cost seconds and a million tokens). The only
/// exception is <see cref="OperationCanceledException"/> from the caller's token, checked every 64 K characters.
/// </para>
/// </remarks>
public static partial class SyntaxHighlighter
{
    /// <summary>
    /// The most characters colored by default (about 4 MB of text): past it, the rest of a text is left in the default
    /// color. Generous for code people copy, bounded for logs and dumps.
    /// </summary>
    public const int DefaultMaxChars = 2_000_000;

    /// <summary>How many characters a lexer walks between two looks at the cancellation token.</summary>
    private const int CancellationStride = 65_536;

    /// <summary>
    /// Cuts a text into colored tokens for a language.
    /// </summary>
    /// <param name="text">The text, with <c>\n</c> line breaks (a <c>\r</c> before them is tolerated as whitespace).</param>
    /// <param name="language">The language to color it as. <see cref="TextLanguage.Markdown"/> and <see cref="TextLanguage.PlainText"/> give no tokens.</param>
    /// <param name="maxChars">The most characters to look at; the rest gets no tokens. Zero or less colors nothing.</param>
    /// <param name="cancellationToken">Stops a long run (the viewer was closed or asked for another language).</param>
    /// <returns>The tokens, in text order, never overlapping, within the first <paramref name="maxChars"/> characters.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IReadOnlyList<SyntaxToken> Tokenize(string text, TextLanguage language, int maxChars = DefaultMaxChars, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        int limit = Math.Min(text.Length, Math.Max(0, maxChars));
        if (limit == 0 || language is TextLanguage.Markdown or TextLanguage.PlainText)
        {
            return [];
        }

        var sink = new TokenSink(cancellationToken);
        switch (language)
        {
            case TextLanguage.Xml:
                LexMarkup(text, limit, html: false, sink);
                break;
            case TextLanguage.Html:
                LexMarkup(text, limit, html: true, sink);
                break;
            case TextLanguage.Json:
                LexJson(text, 0, limit, sink);
                break;
            case TextLanguage.Yaml:
                LexYaml(text, limit, sink);
                break;
            case TextLanguage.Ini:
                LexIni(text, limit, sink);
                break;
            case TextLanguage.Diff:
                LexDiff(text, limit, sink);
                break;
            case TextLanguage.Css:
                LexCss(text, limit, sink);
                break;
            default:
                if (SpecOf(language) is { } spec)
                {
                    LexGeneric(text, limit, spec, sink);
                }

                break;
        }

        return sink.Tokens;
    }

    /// <summary>
    /// Collects tokens: drops empty and plain-text ones, joins neighbors of the same kind (fewer runs to draw), and looks at
    /// the cancellation token as the lexer moves on.
    /// </summary>
    private sealed class TokenSink
    {
        /// <summary>The caller's token, looked at every <see cref="CancellationStride"/> characters.</summary>
        private readonly CancellationToken cancellationToken;

        /// <summary>The text position at which the token is looked at next.</summary>
        private int nextCheck = CancellationStride;

        /// <summary>Creates an empty sink.</summary>
        /// <param name="cancellationToken">The caller's token.</param>
        public TokenSink(CancellationToken cancellationToken) => this.cancellationToken = cancellationToken;

        /// <summary>The tokens so far, in text order.</summary>
        public List<SyntaxToken> Tokens { get; } = [];

        /// <summary>
        /// Adds a token from <paramref name="start"/> up to (not including) <paramref name="end"/>. Plain text and empty
        /// ranges are ignored; a token that starts where the last one of the same kind ended extends it.
        /// </summary>
        /// <param name="start">First character.</param>
        /// <param name="end">One past the last character.</param>
        /// <param name="kind">What it is.</param>
        /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
        public void Add(int start, int end, TokenKind kind)
        {
            if (end <= start || kind == TokenKind.Text)
            {
                return;
            }

            if (Tokens.Count > 0 && Tokens[^1] is var last && last.Kind == kind && last.End == start)
            {
                Tokens[^1] = last with { Length = end - last.Start };
            }
            else
            {
                Tokens.Add(new SyntaxToken(start, end - start, kind));
            }

            Check(end);
        }

        /// <summary>Looks at the cancellation token when the lexer has walked far enough since the last look.</summary>
        /// <param name="position">Where the lexer is.</param>
        /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
        public void Check(int position)
        {
            if (position >= nextCheck)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nextCheck = position + CancellationStride;
            }
        }
    }

    /// <summary>The index of the end of the line containing <paramref name="index"/> (its <c>\n</c>, or the limit).</summary>
    /// <param name="text">The text.</param>
    /// <param name="index">A position.</param>
    /// <param name="limit">Where the colored part ends.</param>
    /// <returns>The index of the line break, or <paramref name="limit"/>.</returns>
    private static int LineEnd(string text, int index, int limit)
    {
        int end = text.IndexOf('\n', index, limit - index);
        return end < 0 ? limit : end;
    }

    /// <summary>Whether <paramref name="text"/> holds <paramref name="value"/> at <paramref name="index"/>, within the limit.</summary>
    /// <param name="text">The text.</param>
    /// <param name="index">Where to look.</param>
    /// <param name="limit">Where the colored part ends.</param>
    /// <param name="value">What to look for.</param>
    /// <param name="ignoreCase">Compare without regard to ASCII case.</param>
    /// <returns><see langword="true"/> when it is there.</returns>
    private static bool At(string text, int index, int limit, string value, bool ignoreCase = false) =>
        index + value.Length <= limit &&
        string.Compare(text, index, value, 0, value.Length, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) == 0;

    /// <summary>Whether a character can start an identifier: a letter (any script) or an underscore.</summary>
    /// <param name="c">The character.</param>
    /// <returns><see langword="true"/> for identifier starts.</returns>
    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    /// <summary>Whether a character can continue an identifier: a letter, a digit or an underscore.</summary>
    /// <param name="c">The character.</param>
    /// <returns><see langword="true"/> for identifier characters.</returns>
    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Reads a number literal at <paramref name="index"/>: hex, binary or octal with a prefix, or decimal with a fraction,
    /// an exponent, digit separators (<c>1_000</c>, <c>1'000</c> is left alone) and a type suffix (<c>10L</c>, <c>2.5f</c>,
    /// <c>12px</c> in CSS).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="index">The first character: a digit, or a dot followed by one.</param>
    /// <param name="limit">Where the colored part ends.</param>
    /// <returns>The index just past the number.</returns>
    private static int ScanNumber(string text, int index, int limit)
    {
        int i = index;
        if (text[i] == '0' && i + 1 < limit && text[i + 1] is 'x' or 'X' or 'b' or 'B' or 'o' or 'O')
        {
            i += 2;
            while (i < limit && (char.IsAsciiHexDigit(text[i]) || text[i] == '_'))
            {
                i++;
            }
        }
        else
        {
            while (i < limit && (char.IsAsciiDigit(text[i]) || text[i] == '_'))
            {
                i++;
            }

            if (i + 1 < limit && text[i] == '.' && char.IsAsciiDigit(text[i + 1]))
            {
                i++;
                while (i < limit && (char.IsAsciiDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }
            }

            if (i + 1 < limit && text[i] is 'e' or 'E' && (char.IsAsciiDigit(text[i + 1]) || (text[i + 1] is '+' or '-' && i + 2 < limit && char.IsAsciiDigit(text[i + 2]))))
            {
                i += 2;
                while (i < limit && char.IsAsciiDigit(text[i]))
                {
                    i++;
                }
            }
        }

        // A suffix: type letters (u, L, f, m, n) or a CSS unit (px, em, %), never more than a few letters.
        int suffix = i;
        while (i < limit && i - suffix < 4 && (char.IsAsciiLetter(text[i]) || text[i] == '%'))
        {
            i++;
        }

        return i;
    }

    /// <summary>
    /// Reads a quoted string from its opening quote: up to the matching quote, honoring an escape character and doubled
    /// quotes when the language has them; a string that does not close ends at its line's end (or, for languages whose
    /// strings span lines, at the limit).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="index">The opening quote.</param>
    /// <param name="limit">Where the colored part ends.</param>
    /// <param name="escape">The escape character, or <c>'\0'</c> for none.</param>
    /// <param name="doubledQuotes">Whether two quotes in a row stand for one (SQL, C# verbatim strings, PowerShell's single quotes).</param>
    /// <param name="multiline">Whether the string may run over line breaks (JavaScript's backticks, Go's raw strings, verbatim strings).</param>
    /// <returns>The index just past the closing quote, or where the string had to end.</returns>
    private static int ScanString(string text, int index, int limit, char escape, bool doubledQuotes, bool multiline)
    {
        char quote = text[index];
        int i = index + 1;
        while (i < limit)
        {
            char c = text[i];
            if (escape != '\0' && c == escape && i + 1 < limit)
            {
                i += 2;
                continue;
            }

            if (c == quote)
            {
                if (doubledQuotes && i + 1 < limit && text[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            if (c == '\n' && !multiline)
            {
                return i;
            }

            i++;
        }

        return limit;
    }
}
