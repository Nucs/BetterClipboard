namespace BetterClipboard.Core.TextView;

/// <summary>The markup lexers: XML, HTML and CSS.</summary>
public static partial class SyntaxHighlighter
{
    /// <summary>
    /// Colors XML or HTML: tags (their brackets, names, attributes and values), comments, CDATA sections, processing
    /// instructions, declarations and entity references. Text between tags keeps the default color.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="limit">Where the colored part ends.</param>
    /// <param name="html">
    /// HTML rules: attribute values may be unquoted, and the bodies of <c>&lt;script&gt;</c> and <c>&lt;style&gt;</c> are left
    /// alone (they hold <c>&lt;</c> as an operator, which would otherwise start bogus tags).
    /// </param>
    /// <param name="sink">Receives the tokens.</param>
    private static void LexMarkup(string text, int limit, bool html, TokenSink sink)
    {
        int i = 0;
        while (i < limit)
        {
            int next = text.IndexOfAny(['<', '&'], i, limit - i);
            if (next < 0)
            {
                return;
            }

            i = next;
            sink.Check(i);
            if (text[i] == '&')
            {
                i = LexEntity(text, i, limit, sink);
                continue;
            }

            if (At(text, i, limit, "<!--"))
            {
                int close = text.IndexOf("-->", i + 4, limit - i - 4, StringComparison.Ordinal);
                int end = close < 0 ? limit : close + 3;
                sink.Add(i, end, TokenKind.Comment);
                i = end;
                continue;
            }

            if (At(text, i, limit, "<![CDATA["))
            {
                int close = text.IndexOf("]]>", i + 9, limit - i - 9, StringComparison.Ordinal);
                sink.Add(i, i + 9, TokenKind.Punctuation);
                if (close < 0)
                {
                    return;
                }

                sink.Add(close, close + 3, TokenKind.Punctuation);
                i = close + 3;
                continue;
            }

            if (At(text, i, limit, "<?") || At(text, i, limit, "<!"))
            {
                // <?xml …?>, <?php …?> and <!DOCTYPE …>: one piece, colored as a declaration.
                int close = text.IndexOf('>', i + 2, limit - i - 2);
                int end = close < 0 ? LineEnd(text, i, limit) : close + 1;
                sink.Add(i, end, TokenKind.Preprocessor);
                i = end;
                continue;
            }

            i = LexTag(text, i, limit, html, sink);
        }
    }

    /// <summary>
    /// Colors one tag from its <c>&lt;</c>: the brackets, the element name, each attribute name and value. A <c>&lt;</c>
    /// that does not start a tag (<c>a &lt; b</c> in HTML text) is left as text.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="start">The <c>&lt;</c>.</param>
    /// <param name="limit">Where the colored part ends.</param>
    /// <param name="html">HTML rules (unquoted values, raw script and style bodies).</param>
    /// <param name="sink">Receives the tokens.</param>
    /// <returns>Where the lexer goes on.</returns>
    private static int LexTag(string text, int start, int limit, bool html, TokenSink sink)
    {
        int i = start + 1;
        bool closing = i < limit && text[i] == '/';
        if (closing)
        {
            i++;
        }

        if (i >= limit || !(char.IsLetter(text[i]) || text[i] is '_' or ':'))
        {
            return start + 1;
        }

        sink.Add(start, i, TokenKind.Punctuation);
        int nameStart = i;
        while (i < limit && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or ':' or '.' or '-'))
        {
            i++;
        }

        sink.Add(nameStart, i, TokenKind.Tag);
        var name = text.AsSpan(nameStart, i - nameStart);

        // Attributes, up to the closing bracket. A tag that never closes stops at the next '<' (the author's next tag).
        while (i < limit)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '>' || (c == '/' && i + 1 < limit && text[i + 1] == '>'))
            {
                int end = c == '>' ? i + 1 : i + 2;
                sink.Add(i, end, TokenKind.Punctuation);
                i = end;

                // An HTML script or style body is another language: skipped up to its end tag, uncolored.
                if (html && !closing && c == '>' && (name.Equals("script", StringComparison.OrdinalIgnoreCase) || name.Equals("style", StringComparison.OrdinalIgnoreCase)))
                {
                    int bodyEnd = text.IndexOf("</" + name.ToString(), i, limit - i, StringComparison.OrdinalIgnoreCase);
                    return bodyEnd < 0 ? limit : bodyEnd;
                }

                return i;
            }

            if (c == '<')
            {
                return i;
            }

            if (c is '"' or '\'')
            {
                // A value without a name (malformed, or an HTML boolean written oddly): colored as a string anyway.
                int valueEnd = ScanString(text, i, limit, '\0', doubledQuotes: false, multiline: true);
                sink.Add(i, valueEnd, TokenKind.String);
                i = valueEnd;
                continue;
            }

            if (c == '=')
            {
                sink.Add(i, i + 1, TokenKind.Punctuation);
                i++;
                while (i < limit && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                if (i < limit && text[i] is '"' or '\'')
                {
                    int valueEnd = ScanString(text, i, limit, '\0', doubledQuotes: false, multiline: true);
                    sink.Add(i, valueEnd, TokenKind.String);
                    i = valueEnd;
                }
                else if (html)
                {
                    int valueStart = i;
                    while (i < limit && !char.IsWhiteSpace(text[i]) && text[i] != '>')
                    {
                        i++;
                    }

                    sink.Add(valueStart, i, TokenKind.String);
                }

                continue;
            }

            int attributeStart = i;
            while (i < limit && !char.IsWhiteSpace(text[i]) && text[i] is not ('=' or '>' or '/' or '<' or '"' or '\''))
            {
                i++;
            }

            if (i == attributeStart)
            {
                i++; // a lone '/' inside the tag
                continue;
            }

            sink.Add(attributeStart, i, TokenKind.AttributeName);
        }

        return i;
    }

    /// <summary>Colors an entity reference (<c>&amp;amp;</c>, <c>&amp;#169;</c>, <c>&amp;#x1F600;</c>); a lone <c>&amp;</c> stays text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="start">The <c>&amp;</c>.</param>
    /// <param name="limit">Where the colored part ends.</param>
    /// <param name="sink">Receives the token.</param>
    /// <returns>Where the lexer goes on.</returns>
    private static int LexEntity(string text, int start, int limit, TokenSink sink)
    {
        int i = start + 1;
        if (i < limit && text[i] == '#')
        {
            i++;
        }

        int nameStart = i;
        while (i < limit && i - nameStart < 32 && char.IsAsciiLetterOrDigit(text[i]))
        {
            i++;
        }

        if (i > nameStart && i < limit && text[i] == ';')
        {
            sink.Add(start, i + 1, TokenKind.Entity);
            return i + 1;
        }

        return start + 1;
    }

    /// <summary>
    /// Colors CSS, SCSS and Less: comments, selectors, at-rules, property names, and the values' numbers, colors, strings,
    /// functions and <c>!important</c>. Inside a block, a name followed by a colon whose value has no <c>{</c> is a
    /// property; anything else is a nested selector (SCSS nesting).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="limit">Where the colored part ends.</param>
    /// <param name="sink">Receives the tokens.</param>
    private static void LexCss(string text, int limit, TokenSink sink)
    {
        int depth = 0;
        bool inValue = false;
        int i = 0;
        while (i < limit)
        {
            sink.Check(i);
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
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

            if (At(text, i, limit, "//") && (i == 0 || text[i - 1] != ':'))
            {
                // SCSS and Less line comments; "://" inside url(…) is not one.
                int end = LineEnd(text, i, limit);
                sink.Add(i, end, TokenKind.Comment);
                i = end;
                continue;
            }

            if (c is '"' or '\'')
            {
                int end = ScanString(text, i, limit, '\\', doubledQuotes: false, multiline: false);
                sink.Add(i, end, TokenKind.String);
                i = end;
                continue;
            }

            switch (c)
            {
                case '{':
                    depth++;
                    inValue = false;
                    sink.Add(i, i + 1, TokenKind.Punctuation);
                    i++;
                    continue;
                case '}':
                    depth = Math.Max(0, depth - 1);
                    inValue = false;
                    sink.Add(i, i + 1, TokenKind.Punctuation);
                    i++;
                    continue;
                case ';':
                    inValue = false;
                    sink.Add(i, i + 1, TokenKind.Punctuation);
                    i++;
                    continue;
            }

            if (c == '@' || c == '$')
            {
                int end = i + 1;
                while (end < limit && (IsIdentifierPart(text[end]) || text[end] == '-'))
                {
                    end++;
                }

                // An at-rule (@media) or, in SCSS and Less, a variable ($size, @size).
                bool atRule = c == '@' && !inValue && (end < limit && text[end] != ':');
                sink.Add(i, end, atRule ? TokenKind.Keyword : TokenKind.Variable);
                i = end;
                continue;
            }

            if (inValue)
            {
                if (c == '#' && i + 1 < limit && char.IsAsciiHexDigit(text[i + 1]))
                {
                    int end = i + 1;
                    while (end < limit && char.IsAsciiHexDigit(text[end]))
                    {
                        end++;
                    }

                    sink.Add(i, end, TokenKind.Number);
                    i = end;
                    continue;
                }

                if (char.IsAsciiDigit(c) || (c is '.' or '-' && i + 1 < limit && char.IsAsciiDigit(text[i + 1]) && (i == 0 || !IsIdentifierPart(text[i - 1]))))
                {
                    int end = ScanNumber(text, c is '.' or '-' ? i + 1 : i, limit);
                    sink.Add(i, end, TokenKind.Number);
                    i = end;
                    continue;
                }

                if (c == '!')
                {
                    int end = i + 1;
                    while (end < limit && char.IsAsciiLetter(text[end]))
                    {
                        end++;
                    }

                    sink.Add(i, end, TokenKind.Keyword);
                    i = end;
                    continue;
                }

                if (IsIdentifierStart(c) || c == '-')
                {
                    int end = i + 1;
                    while (end < limit && (IsIdentifierPart(text[end]) || text[end] == '-'))
                    {
                        end++;
                    }

                    sink.Add(i, end, end < limit && text[end] == '(' ? TokenKind.Function : TokenKind.Text);
                    i = end;
                    continue;
                }

                i++;
                continue;
            }

            // Outside a value: a property name inside a block, or a selector.
            if (IsIdentifierStart(c) || c is '-' or '.' or '#' or ':' or '*' or '&' or '[' or '>' or '+' or '~' or ',')
            {
                int lineEnd = LineEnd(text, i, limit);
                int colon = text.IndexOf(':', i, lineEnd - i);
                int brace = text.IndexOf('{', i, lineEnd - i);
                int semicolon = text.IndexOf(';', i, lineEnd - i);
                bool property = depth > 0 && (IsIdentifierStart(c) || c == '-') && colon > i && (brace < 0 || (semicolon >= 0 && semicolon < brace)) && IsPropertyName(text, i, colon);
                if (property)
                {
                    sink.Add(i, colon, TokenKind.AttributeName);
                    sink.Add(colon, colon + 1, TokenKind.Punctuation);
                    i = colon + 1;
                    inValue = true;
                    continue;
                }

                // A selector runs to its block's brace (or the line's end); its pieces are colored as tags.
                int selectorEnd = brace >= 0 ? brace : lineEnd;
                for (int j = i; j < selectorEnd;)
                {
                    if (char.IsWhiteSpace(text[j]) || text[j] is ',' or '>' or '+' or '~')
                    {
                        j++;
                        continue;
                    }

                    int pieceEnd = j + 1;
                    while (pieceEnd < selectorEnd && !char.IsWhiteSpace(text[pieceEnd]) && text[pieceEnd] is not (',' or '>' or '+' or '~'))
                    {
                        pieceEnd++;
                    }

                    sink.Add(j, pieceEnd, TokenKind.Tag);
                    j = pieceEnd;
                }

                i = selectorEnd;
                continue;
            }

            i++;
        }
    }

    /// <summary>Whether the text between <paramref name="start"/> and the colon is a single CSS property name (<c>margin-top</c>, <c>--accent</c>).</summary>
    /// <param name="text">The text.</param>
    /// <param name="start">The name's first character.</param>
    /// <param name="colon">The colon after it.</param>
    /// <returns><see langword="true"/> when only name characters and trailing blanks lie between them.</returns>
    private static bool IsPropertyName(string text, int start, int colon)
    {
        int end = colon;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        for (int j = start; j < end; j++)
        {
            if (!(IsIdentifierPart(text[j]) || text[j] == '-'))
            {
                return false;
            }
        }

        return end > start;
    }
}
