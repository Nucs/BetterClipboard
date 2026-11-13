namespace BetterClipboard.Core.TextView;

/// <summary>The data-format lexers: JSON, YAML, INI/TOML and diffs.</summary>
public static partial class SyntaxHighlighter
{
    /// <summary>
    /// Colors JSON, also with comments, single-quoted strings, unquoted keys and trailing commas (JSONC, JSON5): property
    /// names as keys, other strings, numbers, <c>true</c>/<c>false</c>/<c>null</c>, and the structure's punctuation.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="start">Where to begin (TOML and YAML hand over the value part of a line).</param>
    /// <param name="limit">Where to stop.</param>
    /// <param name="sink">Receives the tokens.</param>
    private static void LexJson(string text, int start, int limit, TokenSink sink)
    {
        int i = start;
        while (i < limit)
        {
            sink.Check(i);
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c is '"' or '\'')
            {
                int end = ScanString(text, i, limit, '\\', doubledQuotes: false, multiline: false);
                sink.Add(i, end, IsFollowedByColon(text, end, limit) ? TokenKind.Key : TokenKind.String);
                i = end;
                continue;
            }

            if (At(text, i, limit, "//"))
            {
                int end = LineEnd(text, i, limit);
                sink.Add(i, end, TokenKind.Comment);
                i = end;
                continue;
            }

            if (At(text, i, limit, "/*"))
            {
                int close = text.IndexOf("*/", i + 2, limit - i - 2, StringComparison.Ordinal);
                int end = close < 0 ? limit : close + 2;
                sink.Add(i, end, TokenKind.Comment);
                i = end;
                continue;
            }

            if (char.IsAsciiDigit(c) || (c is '-' or '+' or '.' && i + 1 < limit && char.IsAsciiDigit(text[i + 1])))
            {
                int end = ScanNumber(text, c is '-' or '+' ? i + 1 : i, limit);
                sink.Add(i, end, TokenKind.Number);
                i = end;
                continue;
            }

            if (IsIdentifierStart(c) || c == '$')
            {
                int end = i + 1;
                while (end < limit && (IsIdentifierPart(text[end]) || text[end] == '$'))
                {
                    end++;
                }

                var word = text.AsSpan(i, end - i);
                var kind = word is "true" or "false" or "null" or "NaN" or "Infinity" ? TokenKind.Literal
                    : IsFollowedByColon(text, end, limit) ? TokenKind.Key
                    : TokenKind.Text;
                sink.Add(i, end, kind);
                i = end;
                continue;
            }

            if (c is '{' or '}' or '[' or ']' or ':' or ',')
            {
                sink.Add(i, i + 1, TokenKind.Punctuation);
            }

            i++;
        }
    }

    /// <summary>Whether the next non-blank character after <paramref name="index"/> on the same line is a colon (a key's).</summary>
    /// <param name="text">The text.</param>
    /// <param name="index">Just past a string or a word.</param>
    /// <param name="limit">Where the colored part ends.</param>
    /// <returns><see langword="true"/> when a colon follows.</returns>
    private static bool IsFollowedByColon(string text, int index, int limit)
    {
        while (index < limit && text[index] is ' ' or '\t')
        {
            index++;
        }

        return index < limit && text[index] == ':';
    }

    /// <summary>
    /// Colors YAML line by line: comments, document markers, list dashes, keys and their colons, and values — quoted or
    /// plain strings, numbers, literals (<c>true</c>, <c>null</c>, <c>~</c>, …), anchors, aliases and tags. Flow
    /// collections (<c>{a: 1}</c>, <c>[x, y]</c>) are colored by the JSON lexer.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="limit">Where the colored part ends.</param>
    /// <param name="sink">Receives the tokens.</param>
    private static void LexYaml(string text, int limit, TokenSink sink)
    {
        int lineStart = 0;
        while (lineStart < limit)
        {
            int lineEnd = LineEnd(text, lineStart, limit);
            int i = lineStart;
            while (i < lineEnd && text[i] is ' ' or '\t')
            {
                i++;
            }

            if (i < lineEnd && text[i] == '#')
            {
                sink.Add(i, lineEnd, TokenKind.Comment);
            }
            else if (i == lineStart && (At(text, i, lineEnd, "---") || At(text, i, lineEnd, "...")))
            {
                sink.Add(i, i + 3, TokenKind.Heading);
                LexYamlValue(text, i + 3, lineEnd, sink);
            }
            else
            {
                // List dashes ("- - item" nests).
                while (i + 1 < lineEnd && text[i] == '-' && text[i + 1] is ' ' or '\t')
                {
                    sink.Add(i, i + 1, TokenKind.Punctuation);
                    i += 2;
                    while (i < lineEnd && text[i] is ' ' or '\t')
                    {
                        i++;
                    }
                }

                if (i + 1 == lineEnd && text[i] == '-')
                {
                    sink.Add(i, i + 1, TokenKind.Punctuation);
                }
                else
                {
                    int colon = FindYamlKeyColon(text, i, lineEnd);
                    if (colon > i)
                    {
                        sink.Add(i, colon, TokenKind.Key);
                        sink.Add(colon, colon + 1, TokenKind.Punctuation);
                        LexYamlValue(text, colon + 1, lineEnd, sink);
                    }
                    else
                    {
                        LexYamlValue(text, i, lineEnd, sink);
                    }
                }
            }

            lineStart = lineEnd + 1;
        }
    }

    /// <summary>
    /// Finds the colon that ends a YAML key on a line: the first <c>:</c> followed by a blank or the line's end, outside
    /// quotes; a quoted key (<c>"a b": 1</c>) counts as one.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="start">The line's first non-blank character after any list dashes.</param>
    /// <param name="lineEnd">The line's end.</param>
    /// <returns>The colon's index, or -1 when the line has no key.</returns>
    private static int FindYamlKeyColon(string text, int start, int lineEnd)
    {
        int i = start;
        if (i < lineEnd && text[i] is '"' or '\'')
        {
            i = ScanString(text, i, lineEnd, text[i] == '"' ? '\\' : '\0', doubledQuotes: text[i] == '\'', multiline: false);
            while (i < lineEnd && text[i] is ' ' or '\t')
            {
                i++;
            }

            return i < lineEnd && text[i] == ':' ? i : -1;
        }

        if (i < lineEnd && text[i] is '{' or '[' or '|' or '>' or '&' or '*' or '!' or '#' or '%' or '@' or '`')
        {
            return -1;
        }

        for (; i < lineEnd; i++)
        {
            if (text[i] == ':' && (i + 1 == lineEnd || text[i + 1] is ' ' or '\t'))
            {
                return i;
            }

            if (text[i] == '#' && i > start && text[i - 1] is ' ' or '\t')
            {
                return -1;
            }
        }

        return -1;
    }

    /// <summary>
    /// Colors a YAML value (the rest of a line after a key or a dash): a comment after a blank and <c>#</c>, quoted
    /// strings, block indicators, anchors, aliases, tags, numbers, literals, flow collections and plain strings.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="start">The value's start.</param>
    /// <param name="lineEnd">The line's end.</param>
    /// <param name="sink">Receives the tokens.</param>
    private static void LexYamlValue(string text, int start, int lineEnd, TokenSink sink)
    {
        int i = start;
        while (i < lineEnd && text[i] is ' ' or '\t')
        {
            i++;
        }

        // A comment cuts the value off: " #" anywhere outside a quoted string. Its token is added after the value's, since
        // tokens must come in text order.
        int valueEnd = FindTrailingComment(text, i, lineEnd, "#");
        LexYamlScalar(text, i, valueEnd, sink);
        sink.Add(valueEnd, lineEnd, TokenKind.Comment);
    }

    /// <summary>
    /// Finds where a trailing comment starts on a line: the first comment marker at the value's start or after a blank,
    /// outside quoted strings.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="start">The value's start.</param>
    /// <param name="lineEnd">The line's end.</param>
    /// <param name="markers">The characters that start a comment (<c>#</c>; <c>;#</c> for INI).</param>
    /// <returns>The comment's start, or <paramref name="lineEnd"/> when the line has none.</returns>
    private static int FindTrailingComment(string text, int start, int lineEnd, string markers)
    {
        char quote = '\0';
        for (int j = start; j < lineEnd; j++)
        {
            char c = text[j];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else if (c == '\\' && quote == '"')
                {
                    j++;
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (markers.Contains(c) && (j == start || text[j - 1] is ' ' or '\t'))
            {
                return j;
            }
        }

        return lineEnd;
    }

    /// <summary>
    /// Colors a YAML value without its comment: quoted strings, block indicators, anchors, aliases, tags, flow
    /// collections, and a plain scalar as a number, a literal or a string.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="i">The value's first character (blanks may come first).</param>
    /// <param name="valueEnd">Where the value ends (a comment or the line's end).</param>
    /// <param name="sink">Receives the tokens.</param>
    private static void LexYamlScalar(string text, int i, int valueEnd, TokenSink sink)
    {
        while (i < valueEnd)
        {
            char c = text[i];
            if (c is ' ' or '\t')
            {
                i++;
                continue;
            }

            if (c is '"' or '\'')
            {
                int end = ScanString(text, i, valueEnd, c == '"' ? '\\' : '\0', doubledQuotes: c == '\'', multiline: false);
                sink.Add(i, end, TokenKind.String);
                i = end;
                continue;
            }

            if (c is '{' or '[')
            {
                LexJson(text, i, valueEnd, sink);
                return;
            }

            if (c is '|' or '>')
            {
                sink.Add(i, valueEnd, TokenKind.Punctuation);
                return;
            }

            if (c is '&' or '*' or '!')
            {
                int end = i + 1;
                while (end < valueEnd && !char.IsWhiteSpace(text[end]))
                {
                    end++;
                }

                sink.Add(i, end, c == '!' ? TokenKind.Type : TokenKind.Variable);
                i = end;
                continue;
            }

            // A plain scalar: the rest of the value, colored as a number, a literal or a string.
            int scalarEnd = valueEnd;
            while (scalarEnd > i && text[scalarEnd - 1] is ' ' or '\t' or '\r')
            {
                scalarEnd--;
            }

            var scalar = text.AsSpan(i, scalarEnd - i);
            var kind = IsYamlLiteral(scalar) ? TokenKind.Literal
                : IsNumeric(scalar) ? TokenKind.Number
                : TokenKind.String;
            sink.Add(i, scalarEnd, kind);
            return;
        }
    }

    /// <summary>Whether a YAML plain scalar is a literal word: booleans, null in its spellings, and YAML 1.1's yes/no/on/off.</summary>
    /// <param name="scalar">The scalar.</param>
    /// <returns><see langword="true"/> for a literal.</returns>
    private static bool IsYamlLiteral(ReadOnlySpan<char> scalar) =>
        scalar is "~" ||
        scalar.Equals("true", StringComparison.OrdinalIgnoreCase) || scalar.Equals("false", StringComparison.OrdinalIgnoreCase) ||
        scalar.Equals("null", StringComparison.OrdinalIgnoreCase) || scalar.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
        scalar.Equals("no", StringComparison.OrdinalIgnoreCase) || scalar.Equals("on", StringComparison.OrdinalIgnoreCase) ||
        scalar.Equals("off", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a value is one number (<c>42</c>, <c>-3.5</c>, <c>1e9</c>, <c>0x1F</c>), with nothing else around it.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> for a number.</returns>
    private static bool IsNumeric(ReadOnlySpan<char> value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        int start = value[0] is '-' or '+' ? 1 : 0;
        if (start >= value.Length || !(char.IsAsciiDigit(value[start]) || (value[start] == '.' && start + 1 < value.Length && char.IsAsciiDigit(value[start + 1]))))
        {
            return false;
        }

        var asText = value.ToString();
        return ScanNumber(asText, start, asText.Length) == asText.Length;
    }

    /// <summary>
    /// Colors INI, TOML, .properties and .editorconfig files line by line: comments (<c>;</c>, <c>#</c>), section headers
    /// (<c>[section]</c>, <c>[[array]]</c>), keys, their separator, and values — TOML values (strings, numbers, booleans,
    /// arrays, inline tables) through the JSON lexer, other values as strings.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="limit">Where the colored part ends.</param>
    /// <param name="sink">Receives the tokens.</param>
    private static void LexIni(string text, int limit, TokenSink sink)
    {
        int lineStart = 0;
        while (lineStart < limit)
        {
            int lineEnd = LineEnd(text, lineStart, limit);
            int i = lineStart;
            while (i < lineEnd && text[i] is ' ' or '\t')
            {
                i++;
            }

            if (i < lineEnd)
            {
                char c = text[i];
                if (c is ';' or '#')
                {
                    sink.Add(i, lineEnd, TokenKind.Comment);
                }
                else if (c == '[')
                {
                    int close = text.LastIndexOf(']', lineEnd - 1, lineEnd - i);
                    int end = close < 0 ? lineEnd : close + 1;
                    sink.Add(i, end, TokenKind.Heading);
                    int comment = end < lineEnd ? text.IndexOfAny([';', '#'], end, lineEnd - end) : -1;
                    if (comment >= 0)
                    {
                        sink.Add(comment, lineEnd, TokenKind.Comment);
                    }
                }
                else
                {
                    int separator = text.IndexOfAny(['=', ':'], i, lineEnd - i);
                    if (separator > i)
                    {
                        int keyEnd = separator;
                        while (keyEnd > i && text[keyEnd - 1] is ' ' or '\t')
                        {
                            keyEnd--;
                        }

                        sink.Add(i, keyEnd, TokenKind.Key);
                        sink.Add(separator, separator + 1, TokenKind.Punctuation);
                        LexIniValue(text, separator + 1, lineEnd, sink);
                    }
                }
            }

            lineStart = lineEnd + 1;
        }
    }

    /// <summary>
    /// Colors an INI or TOML value: a quoted string, a number, a boolean, an array or inline table (through the JSON lexer),
    /// otherwise a plain string; a <c>;</c> or <c>#</c> after a blank starts a comment.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="start">Just past the key's separator.</param>
    /// <param name="lineEnd">The line's end.</param>
    /// <param name="sink">Receives the tokens.</param>
    private static void LexIniValue(string text, int start, int lineEnd, TokenSink sink)
    {
        int i = start;
        while (i < lineEnd && text[i] is ' ' or '\t')
        {
            i++;
        }

        if (i >= lineEnd)
        {
            return;
        }

        // The comment's token comes after the value's: tokens must come in text order.
        int commentStart = FindTrailingComment(text, i, lineEnd, ";#");
        if (text[i] is '"' or '\'' or '[' or '{')
        {
            LexJson(text, i, commentStart, sink);
        }
        else
        {
            int valueEnd = commentStart;
            while (valueEnd > i && text[valueEnd - 1] is ' ' or '\t' or '\r')
            {
                valueEnd--;
            }

            var value = text.AsSpan(i, valueEnd - i);
            var kind = value is "true" or "false" ? TokenKind.Literal : IsNumeric(value) ? TokenKind.Number : TokenKind.String;
            sink.Add(i, valueEnd, kind);
        }

        sink.Add(commentStart, lineEnd, TokenKind.Comment);
    }

    /// <summary>
    /// Colors a unified diff line by line: file and hunk headers, added lines, removed lines, and git's "no newline" note.
    /// Context lines keep the default color.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="limit">Where the colored part ends.</param>
    /// <param name="sink">Receives the tokens.</param>
    private static void LexDiff(string text, int limit, TokenSink sink)
    {
        int lineStart = 0;
        while (lineStart < limit)
        {
            int lineEnd = LineEnd(text, lineStart, limit);
            if (lineEnd > lineStart)
            {
                var kind = At(text, lineStart, lineEnd, "+++") || At(text, lineStart, lineEnd, "---") ||
                           At(text, lineStart, lineEnd, "diff ") || At(text, lineStart, lineEnd, "index ") ||
                           At(text, lineStart, lineEnd, "@@") || At(text, lineStart, lineEnd, "new file mode") ||
                           At(text, lineStart, lineEnd, "deleted file mode") || At(text, lineStart, lineEnd, "rename ") ||
                           At(text, lineStart, lineEnd, "similarity index") || At(text, lineStart, lineEnd, "Binary files")
                    ? TokenKind.Heading
                    : text[lineStart] switch
                    {
                        '+' => TokenKind.Inserted,
                        '-' => TokenKind.Deleted,
                        '\\' => TokenKind.Comment,
                        _ => TokenKind.Text,
                    };
                sink.Add(lineStart, lineEnd, kind);
            }

            lineStart = lineEnd + 1;
        }
    }
}
