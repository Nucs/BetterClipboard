using System.Globalization;
using System.Text;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Just enough of a C# lexer for the localization guards: comments blanked out, and every string literal (regular,
/// verbatim and interpolated) found with its offset and its text. An interpolated string's text is rebuilt as the
/// composite format the compiler hands to <see cref="FormattableString.Format"/>: <c>$"Hi {name:N0}"</c> becomes
/// <c>Hi {0:N0}</c>, exactly what <c>UiText.Format</c> looks up in the catalogs.
/// </summary>
/// <remarks>
/// Not a full lexer: raw string literals (<c>"""</c>) are skipped, and char literals are stepped over. That covers this
/// repository's sources, which the tests read; a construct it misreads makes a guard report a string it cannot find,
/// never pass one it should not.
/// </remarks>
internal static class CSharpSourceScanner
{
    /// <summary>One string literal of a source.</summary>
    /// <param name="Start">The offset of its first character (<c>$</c>, <c>@</c> or the opening quote).</param>
    /// <param name="End">The offset just after its closing quote.</param>
    /// <param name="Text">Its value; an interpolated literal's composite format.</param>
    internal sealed record Literal(int Start, int End, string Text);

    /// <summary>
    /// Replaces every <c>//</c> and <c>/* */</c> comment outside string literals with spaces (line breaks kept), so
    /// offsets and line numbers stay those of the file and documentation examples are not mistaken for code.
    /// </summary>
    /// <param name="source">C# source text.</param>
    /// <returns>The source without comments.</returns>
    internal static string StripComments(string source)
    {
        var output = source.ToCharArray();
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            if (c == '"' || c == '\'')
            {
                i = SkipQuoted(source, i);
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                int end = source.IndexOf('\n', i);
                end = end < 0 ? source.Length : end;
                Array.Fill(output, ' ', i, end - i);
                i = end;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? source.Length : end + 2;
                for (int k = i; k < end; k++)
                {
                    if (output[k] != '\n')
                    {
                        output[k] = ' ';
                    }
                }

                i = end;
                continue;
            }

            i++;
        }

        return new string(output);
    }

    /// <summary>Every string literal of a comment-free source, in order (literals nested in interpolation holes included).</summary>
    /// <param name="source">C# source text, comments already blanked (<see cref="StripComments"/>).</param>
    /// <returns>The literals.</returns>
    internal static List<Literal> Literals(string source)
    {
        var found = new List<Literal>();
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            if (c == '\'')
            {
                i = SkipQuoted(source, i);
                continue;
            }

            if (c == '"' || ((c == '$' || c == '@') && i + 1 < source.Length && source[i + 1] is '"' or '$' or '@'))
            {
                i = ReadLiteral(source, i, found);
                continue;
            }

            i++;
        }

        return found;
    }

    /// <summary>
    /// Reads one literal starting at <paramref name="start"/> (its prefix, if any), adds it and the literals in its
    /// interpolation holes to <paramref name="found"/>, and returns the offset after it.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="start">Offset of the literal's first character.</param>
    /// <param name="found">Where literals are collected.</param>
    /// <returns>The offset just after the literal.</returns>
    private static int ReadLiteral(string source, int start, List<Literal> found)
    {
        int i = start;
        bool interpolated = false, verbatim = false;
        while (i < source.Length && source[i] is '$' or '@')
        {
            interpolated |= source[i] == '$';
            verbatim |= source[i] == '@';
            i++;
        }

        if (i >= source.Length || source[i] != '"')
        {
            return i;
        }

        if (string.CompareOrdinal(source, i, "\"\"\"", 0, 3) == 0)
        {
            // A raw string literal: not used for UI text here; skip to its end.
            int close = source.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
            return close < 0 ? source.Length : close + 3;
        }

        i++;
        var text = new StringBuilder();
        int hole = 0;
        var nested = new List<Literal>();
        while (i < source.Length)
        {
            char c = source[i];
            if (c == '"')
            {
                if (verbatim && i + 1 < source.Length && source[i + 1] == '"')
                {
                    text.Append('"');
                    i += 2;
                    continue;
                }

                i++;
                break;
            }

            if (interpolated && (c == '{' || c == '}') && i + 1 < source.Length && source[i + 1] == c)
            {
                // An escaped brace stays escaped in the composite format.
                text.Append(c).Append(c);
                i += 2;
                continue;
            }

            if (interpolated && c == '{')
            {
                i = ReadHole(source, i + 1, hole++, text, nested);
                continue;
            }

            if (!verbatim && c == '\\' && i + 1 < source.Length)
            {
                i = ReadEscape(source, i, text);
                continue;
            }

            text.Append(c);
            i++;
        }

        found.Add(new Literal(start, i, text.ToString()));
        found.AddRange(nested);
        return i;
    }

    /// <summary>
    /// Reads an interpolation hole (after its <c>{</c>) and writes its composite-format placeholder: the hole's index,
    /// then its alignment and format as written.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="start">Offset just after the hole's opening brace.</param>
    /// <param name="index">The hole's index in its literal.</param>
    /// <param name="text">The literal's text so far.</param>
    /// <param name="nested">Where literals inside the hole's expression are collected.</param>
    /// <returns>The offset just after the hole's closing brace.</returns>
    private static int ReadHole(string source, int start, int index, StringBuilder text, List<Literal> nested)
    {
        int depth = 0, i = start, alignment = -1, format = -1;
        while (i < source.Length)
        {
            char c = source[i];
            if (c == '"' || ((c == '$' || c == '@') && i + 1 < source.Length && source[i + 1] is '"' or '$' or '@'))
            {
                i = ReadLiteral(source, i, nested);
                continue;
            }

            if (c == '\'')
            {
                i = SkipQuoted(source, i);
                continue;
            }

            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']')
            {
                depth--;
            }
            else if (c == '}')
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
            }
            else if (depth == 0 && c == ':' && format < 0)
            {
                format = i;
            }
            else if (depth == 0 && c == ',' && format < 0 && alignment < 0)
            {
                alignment = i;
            }

            i++;
        }

        text.Append('{').Append(index.ToString(CultureInfo.InvariantCulture));
        if (alignment >= 0)
        {
            text.Append(source, alignment, (format >= 0 ? format : i) - alignment);
        }

        if (format >= 0)
        {
            text.Append(source, format, i - format);
        }

        text.Append('}');
        return i + 1;
    }

    /// <summary>Decodes one backslash escape of a regular literal into <paramref name="text"/>.</summary>
    /// <param name="source">The source.</param>
    /// <param name="start">Offset of the backslash.</param>
    /// <param name="text">The literal's text so far.</param>
    /// <returns>The offset after the escape.</returns>
    private static int ReadEscape(string source, int start, StringBuilder text)
    {
        char next = source[start + 1];
        if (next == 'u' && start + 6 <= source.Length)
        {
            text.Append((char)int.Parse(source.AsSpan(start + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            return start + 6;
        }

        text.Append(next switch
        {
            'n' => '\n',
            't' => '\t',
            'r' => '\r',
            '0' => '\0',
            _ => next,
        });
        return start + 2;
    }

    /// <summary>Steps over a regular string or char literal that starts at <paramref name="start"/>.</summary>
    /// <param name="source">The source.</param>
    /// <param name="start">Offset of the opening quote.</param>
    /// <returns>The offset after the closing quote.</returns>
    private static int SkipQuoted(string source, int start)
    {
        char quote = source[start];

        // Verbatim whichever order the prefix is written in (@"…", $@"…", @$"…"): there a backslash is no escape.
        bool verbatim = false;
        for (int p = start - 1; quote == '"' && p >= 0 && source[p] is '$' or '@'; p--)
        {
            verbatim |= source[p] == '@';
        }

        int i = start + 1;
        while (i < source.Length && source[i] != quote)
        {
            if (verbatim && source[i] == '"' && i + 1 < source.Length && source[i + 1] == '"')
            {
                i += 2;
                continue;
            }

            i += !verbatim && source[i] == '\\' ? 2 : 1;
        }

        return i + 1;
    }
}
