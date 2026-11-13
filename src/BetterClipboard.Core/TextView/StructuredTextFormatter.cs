using System.Text;
using System.Xml;

namespace BetterClipboard.Core.TextView;

/// <summary>
/// Re-indents XML and JSON for the text viewer's "Formatted" view: one element or value per line, nested two spaces per
/// level — while every token keeps its exact characters (entities, escapes, number spellings, quotes, comments).
/// </summary>
/// <remarks>
/// <para>
/// <b>Only whitespace between tokens changes.</b> A .NET writer would re-encode what it read (<c>&amp;gt;</c> for every
/// <c>&gt;</c> in XML text, a raw letter for a JSON escape such as <c>\u00e9</c>), so the formatted text would no longer
/// show what was copied. Here the text is validated with the framework's readers, then re-laid out from its own tokens.
/// </para>
/// <para>
/// <b>XML text stays as it is.</b> An element whose content is only other elements (and comments) gets one child per line;
/// an element with any text in it (mixed content, or a block of prose such as an agent prompt's
/// <c>&lt;instructions&gt;</c>) is written exactly as copied, line breaks and indentation included, as are
/// <c>xml:space="preserve"</c> elements. So no text a person wrote is re-flowed.
/// </para>
/// <para>
/// <b>Safe on any input.</b> DTDs are ignored and nothing is ever resolved or fetched; invalid input is simply "not
/// formattable" (<see langword="false"/>), never an exception; the work is linear and bounded by <see cref="MaxChars"/>.
/// </para>
/// </remarks>
public static class StructuredTextFormatter
{
    /// <summary>The largest text re-indented (about 8 MB); a larger one is shown as copied.</summary>
    public const int MaxChars = 4_000_000;

    /// <summary>One level of indentation.</summary>
    private const string Indent = "  ";

    /// <summary>
    /// Re-indents a text of a language that can be re-indented (<see cref="TextLanguages.CanReindent"/>).
    /// </summary>
    /// <param name="text">The text, with <c>\n</c> line breaks.</param>
    /// <param name="language">Its language.</param>
    /// <param name="formatted">The re-indented text when this returns <see langword="true"/>; otherwise <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops a long run.</param>
    /// <returns>
    /// <see langword="true"/> when the text is valid XML or JSON and re-indenting changed more than trailing whitespace;
    /// <see langword="false"/> for another language, invalid or too large input, or an already tidy text.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static bool TryFormat(string text, TextLanguage language, out string? formatted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        formatted = null;
        if (text.Length == 0 || text.Length > MaxChars)
        {
            return false;
        }

        string? result = language switch
        {
            TextLanguage.Json => FormatJson(text, cancellationToken),
            TextLanguage.Xml => FormatXml(text, cancellationToken),
            _ => null,
        };

        // Formatting that only moved trailing blanks is no formatting: the "As copied" switch would show the same text.
        if (result is null || result.TrimEnd() == text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd())
        {
            return false;
        }

        formatted = result;
        return true;
    }

    /// <summary>Re-indents valid JSON (with comments, trailing commas and several values in a row allowed).</summary>
    /// <param name="text">The text.</param>
    /// <param name="cancellationToken">Stops a long run.</param>
    /// <returns>The re-indented text, or <see langword="null"/> for invalid JSON.</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    private static string? FormatJson(string text, CancellationToken cancellationToken)
    {
        if (!TextLanguageDetector.IsJson(text, isWhole: true))
        {
            return null;
        }

        var output = new StringBuilder(text.Length + (text.Length / 4));
        int depth = 0;
        bool lineHasContent = false;
        int breakStart = -1;  // where the last line break began in the output, while nothing has followed it yet
        int previousEnd = 0;  // where the last token ended in the source
        int i = 0;

        // Starts a new line at the current depth unless the line is still empty.
        void NewLine()
        {
            if (lineHasContent)
            {
                breakStart = output.Length;
                output.Append('\n');
                output.Append(' ', depth * Indent.Length);
                lineHasContent = false;
            }
        }

        // Writes a token's exact characters.
        void Write(int start, int length)
        {
            output.Append(text, start, length);
            lineHasContent = true;
            breakStart = -1;
        }

        while (i < text.Length)
        {
            if ((i & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] is '/' or '*')
            {
                // A comment stays with what it followed: one written on the same line as the token before it ("1, // one")
                // goes back onto that token's line; a line comment ends its line.
                bool lineComment = text[i + 1] == '/';
                int close = lineComment ? text.IndexOf('\n', i) : text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                int commentEnd = close < 0 ? text.Length : lineComment ? close : close + 2;
                bool sameLine = breakStart >= 0 && text.IndexOf('\n', previousEnd, i - previousEnd) < 0;
                if (sameLine)
                {
                    output.Length = breakStart;
                }

                if (sameLine || (lineHasContent && output[^1] != ' '))
                {
                    output.Append(' ');
                }

                Write(i, commentEnd - i);
                if (lineComment || sameLine)
                {
                    NewLine();
                }

                previousEnd = i = commentEnd;
                continue;
            }

            // A value that follows a closed top-level value (JSON Lines: several values in a row) starts its own line.
            if (depth == 0 && lineHasContent && c is not (',' or ':' or '}' or ']'))
            {
                NewLine();
            }

            int end = i + 1;
            switch (c)
            {
                case '{' or '[':
                {
                    // An empty container stays on one line: {} and [].
                    int next = SkipJsonBlanks(text, i + 1);
                    if (next < text.Length && text[next] == (c == '{' ? '}' : ']'))
                    {
                        Write(i, 1);
                        Write(next, 1);
                        end = next + 1;
                        break;
                    }

                    Write(i, 1);
                    depth++;
                    NewLine();
                    break;
                }

                case '}' or ']':
                    depth = Math.Max(0, depth - 1);
                    NewLine();
                    Write(i, 1);
                    break;
                case ',':
                    Write(i, 1);
                    NewLine();
                    break;
                case ':':
                    output.Append(": ");
                    breakStart = -1;
                    break;
                case '"' or '\'':
                    end = SkipJsonString(text, i);
                    Write(i, end - i);
                    break;
                default:
                    while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not (',' or ':' or '{' or '}' or '[' or ']' or '"' or '\'' or '/'))
                    {
                        end++;
                    }

                    Write(i, end - i);
                    break;
            }

            previousEnd = i = end;
        }

        return output.ToString();
    }

    /// <summary>Skips blanks in JSON.</summary>
    /// <param name="text">The text.</param>
    /// <param name="index">Where to start.</param>
    /// <returns>The first non-blank index, or the text's length.</returns>
    private static int SkipJsonBlanks(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    /// <summary>Finds the end of a JSON string that starts at a quote, honoring backslash escapes.</summary>
    /// <param name="text">The text.</param>
    /// <param name="index">The opening quote.</param>
    /// <returns>The index just past the closing quote (or the text's length for an unterminated string).</returns>
    private static int SkipJsonString(string text, int index)
    {
        char quote = text[index];
        for (int i = index + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == quote)
            {
                return i + 1;
            }
        }

        return text.Length;
    }

    /// <summary>A piece of an XML text: where it is in the source and what it is.</summary>
    private enum XmlPieceKind
    {
        /// <summary>Character data between markup.</summary>
        Text,

        /// <summary>A start tag (<c>&lt;a b="c"&gt;</c>).</summary>
        Start,

        /// <summary>A self-closing tag (<c>&lt;a/&gt;</c>).</summary>
        Empty,

        /// <summary>An end tag (<c>&lt;/a&gt;</c>).</summary>
        End,

        /// <summary>A comment, a processing instruction or a declaration: markup without content of its own.</summary>
        Other,

        /// <summary>A CDATA section: text, written as it is.</summary>
        CData,
    }

    /// <summary>An element and what it holds, built from the pieces; leaves are pieces without children.</summary>
    private sealed class XmlNode
    {
        /// <summary>The piece's kind (a start tag for an element).</summary>
        public required XmlPieceKind Kind { get; init; }

        /// <summary>Where the node starts in the source (its start tag's '&lt;' for an element).</summary>
        public required int Start { get; init; }

        /// <summary>Where the node ends (just past its end tag for an element).</summary>
        public int End { get; set; }

        /// <summary>Where an element's start tag ends (its content starts).</summary>
        public int ContentStart { get; init; }

        /// <summary>Where an element's end tag starts (its content ends); its end for anything else.</summary>
        public int ContentEnd { get; set; }

        /// <summary>The element's children, in order.</summary>
        public List<XmlNode> Children { get; } = [];

        /// <summary>Whether the start tag asks for its whitespace to be kept (<c>xml:space="preserve"</c>).</summary>
        public bool Preserve { get; init; }
    }

    /// <summary>Re-indents a well-formed XML text (a document or a fragment with several roots).</summary>
    /// <param name="text">The text.</param>
    /// <param name="cancellationToken">Stops a long run.</param>
    /// <returns>The re-indented text, or <see langword="null"/> for text that is not well-formed XML.</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    private static string? FormatXml(string text, CancellationToken cancellationToken)
    {
        if (!IsWellFormedXml(text))
        {
            return null;
        }

        var root = new XmlNode { Kind = XmlPieceKind.Start, Start = 0, ContentStart = 0 };
        var open = new Stack<XmlNode>();
        open.Push(root);
        int i = 0;
        while (i < text.Length)
        {
            if ((i & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var (kind, end) = ReadXmlPiece(text, i);
            var parent = open.Peek();
            switch (kind)
            {
                case XmlPieceKind.Start:
                    var element = new XmlNode
                    {
                        Kind = XmlPieceKind.Start,
                        Start = i,
                        ContentStart = end,
                        Preserve = text.AsSpan(i, end - i).Contains("xml:space=\"preserve\"", StringComparison.Ordinal) ||
                                   text.AsSpan(i, end - i).Contains("xml:space='preserve'", StringComparison.Ordinal),
                    };
                    parent.Children.Add(element);
                    open.Push(element);
                    break;
                case XmlPieceKind.End:
                    if (open.Count > 1)
                    {
                        var closed = open.Pop();
                        closed.ContentEnd = i;
                        closed.End = end;
                    }

                    break;
                default:
                    parent.Children.Add(new XmlNode { Kind = kind, Start = i, End = end, ContentStart = i, ContentEnd = end });
                    break;
            }

            i = end;
        }

        var output = new StringBuilder(text.Length + (text.Length / 4));
        WriteXmlChildren(text, root, 0, output);
        return output.ToString();
    }

    /// <summary>Whether a text is well-formed XML: a document or a fragment, DTDs ignored, nothing resolved.</summary>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> when the framework's reader reads it to the end.</returns>
    private static bool IsWellFormedXml(string text)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                ConformanceLevel = ConformanceLevel.Fragment,
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreWhitespace = false,
                CheckCharacters = false,
            };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            bool element = false;
            while (reader.Read())
            {
                element |= reader.NodeType == XmlNodeType.Element;
            }

            return element;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <summary>Reads one piece of an XML text at <paramref name="index"/>: its kind and where it ends.</summary>
    /// <param name="text">The text (well-formed).</param>
    /// <param name="index">The piece's first character.</param>
    /// <returns>The piece's kind and the index just past it.</returns>
    private static (XmlPieceKind Kind, int End) ReadXmlPiece(string text, int index)
    {
        if (text[index] != '<')
        {
            int next = text.IndexOf('<', index);
            return (XmlPieceKind.Text, next < 0 ? text.Length : next);
        }

        if (string.CompareOrdinal(text, index, "<!--", 0, 4) == 0)
        {
            int close = text.IndexOf("-->", index + 4, StringComparison.Ordinal);
            return (XmlPieceKind.Other, close < 0 ? text.Length : close + 3);
        }

        if (string.CompareOrdinal(text, index, "<![CDATA[", 0, 9) == 0)
        {
            int close = text.IndexOf("]]>", index + 9, StringComparison.Ordinal);
            return (XmlPieceKind.CData, close < 0 ? text.Length : close + 3);
        }

        if (string.CompareOrdinal(text, index, "<?", 0, 2) == 0)
        {
            int close = text.IndexOf("?>", index + 2, StringComparison.Ordinal);
            return (XmlPieceKind.Other, close < 0 ? text.Length : close + 2);
        }

        if (string.CompareOrdinal(text, index, "<!", 0, 2) == 0)
        {
            // A declaration may hold an internal subset in brackets, with '>' inside it.
            int depth = 0;
            for (int i = index + 2; i < text.Length; i++)
            {
                if (text[i] == '[')
                {
                    depth++;
                }
                else if (text[i] == ']')
                {
                    depth--;
                }
                else if (text[i] == '>' && depth <= 0)
                {
                    return (XmlPieceKind.Other, i + 1);
                }
            }

            return (XmlPieceKind.Other, text.Length);
        }

        bool endTag = index + 1 < text.Length && text[index + 1] == '/';
        char quote = '\0';
        for (int i = index + 1; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return (endTag ? XmlPieceKind.End : text[i - 1] == '/' ? XmlPieceKind.Empty : XmlPieceKind.Start, i + 1);
            }
        }

        return (endTag ? XmlPieceKind.End : XmlPieceKind.Start, text.Length);
    }

    /// <summary>
    /// Writes a node's children one per line at <paramref name="depth"/>; blank text between them is dropped (that is the
    /// re-indenting).
    /// </summary>
    /// <param name="text">The source.</param>
    /// <param name="node">The parent (the invisible root for top-level pieces).</param>
    /// <param name="depth">The children's indentation level.</param>
    /// <param name="output">Receives the text.</param>
    private static void WriteXmlChildren(string text, XmlNode node, int depth, StringBuilder output)
    {
        foreach (var child in node.Children)
        {
            if (child.Kind == XmlPieceKind.Text && string.IsNullOrWhiteSpace(text.AsSpan(child.Start, child.End - child.Start).ToString()))
            {
                continue;
            }

            if (output.Length > 0)
            {
                output.Append('\n');
            }

            output.Append(' ', depth * Indent.Length);
            if (child.Kind != XmlPieceKind.Start || HoldsText(text, child) || child.Preserve || child.Children.Count == 0)
            {
                // Text, markup without content, and elements that hold text: exactly as copied.
                output.Append(text, child.Start, child.End - child.Start);
                continue;
            }

            output.Append(text, child.Start, child.ContentStart - child.Start);
            WriteXmlChildren(text, child, depth + 1, output);
            output.Append('\n');
            output.Append(' ', depth * Indent.Length);
            output.Append(text, child.ContentEnd, child.End - child.ContentEnd);
        }
    }

    /// <summary>Whether an element holds text of its own (non-blank character data or a CDATA section): mixed or text content.</summary>
    /// <param name="text">The source.</param>
    /// <param name="element">The element.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    private static bool HoldsText(string text, XmlNode element)
    {
        foreach (var child in element.Children)
        {
            if (child.Kind == XmlPieceKind.CData ||
                (child.Kind == XmlPieceKind.Text && !text.AsSpan(child.Start, child.End - child.Start).IsWhiteSpace()))
            {
                return true;
            }
        }

        return false;
    }
}
