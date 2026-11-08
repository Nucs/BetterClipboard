using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Reads the app's sources the way the catalogs see them: every English text XAML names (<c>{loc:Tr Text=…}</c> and
/// <c>{loc:Tr Key=…}</c>), every literal C# passes to <c>UiText.Translate</c>, <c>UiText.Format</c> and
/// <c>UiText.Plural</c> (interpolated strings as the composite format the runtime looks up), and every string literal
/// anywhere, so <c>LocalizationTests</c> can check that the code and the catalogs agree.
/// </summary>
/// <remarks>
/// A small lexer, not a compiler: it knows C#'s comments, char literals, regular, verbatim and interpolated strings
/// (holes nest), and skips raw strings. Text handed to UiText in a variable is not seen as a call argument; it still
/// counts as "used" when its literal appears anywhere in the sources.
/// </remarks>
internal sealed partial class UiTextSourceScan
{
    /// <summary>Scans a source tree.</summary>
    /// <param name="srcRoot">The repository's <c>src</c> folder.</param>
    public UiTextSourceScan(string srcRoot)
    {
        foreach (string file in Directory.EnumerateFiles(srcRoot, "*.*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(srcRoot, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            {
                continue;
            }

            if (file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                ScanXaml(relative, File.ReadAllText(file));
            }
            else if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                ScanCSharp(relative, File.ReadAllText(file));
            }
        }
    }

    /// <summary>English texts XAML names with <c>Text=</c>, with where.</summary>
    public List<(string Text, string Where)> XamlTexts { get; } = [];

    /// <summary>Hash keys XAML names with <c>Key=</c>, with where.</summary>
    public List<(string Key, string Where)> XamlKeys { get; } = [];

    /// <summary>Literals passed to <c>UiText.Translate</c> and <c>UiText.Format</c> (composite formats), with where.</summary>
    public List<(string Text, string Where)> Lookups { get; } = [];

    /// <summary>The (one, other) English forms of every <c>UiText.Plural</c> call, with where.</summary>
    public List<(string One, string Other, string Where)> Plurals { get; } = [];

    /// <summary>Every string literal in the C# sources and every attribute value in the XAML: what "used" means.</summary>
    public HashSet<string> AllLiterals { get; } = new(StringComparer.Ordinal);

    /// <summary>Collects one XAML file's Tr uses and attribute values.</summary>
    /// <param name="relative">The file's path, for messages.</param>
    /// <param name="xaml">Its text.</param>
    private void ScanXaml(string relative, string xaml)
    {
        foreach (Match match in TrText().Matches(xaml))
        {
            string raw = match.Groups["single"].Success ? match.Groups["single"].Value : match.Groups["double"].Value;
            string text = WebUtility.HtmlDecode(raw);
            XamlTexts.Add((text, $"{relative}:{Line(xaml, match.Index)}"));
            AllLiterals.Add(text);
        }

        foreach (Match match in TrKey().Matches(xaml))
        {
            XamlKeys.Add((match.Groups[1].Value, $"{relative}:{Line(xaml, match.Index)}"));
        }

        foreach (Match match in XmlAttribute().Matches(xaml))
        {
            AllLiterals.Add(WebUtility.HtmlDecode(match.Groups[1].Value));
        }
    }

    /// <summary>Collects one C# file's literals and UiText calls.</summary>
    /// <param name="relative">The file's path, for messages.</param>
    /// <param name="source">Its text.</param>
    private void ScanCSharp(string relative, string source)
    {
        var lexer = new Lexer(source);
        foreach (var literal in lexer.Literals)
        {
            AllLiterals.Add(literal.Value);
        }

        foreach (Match call in UiTextCall().Matches(source))
        {
            int open = call.Index + call.Length - 1;
            if (lexer.IsInComment(call.Index))
            {
                continue;
            }

            var arguments = lexer.ArgumentsAt(open);
            string where = $"{relative}:{Line(source, call.Index)}";
            var literals = arguments.Select(a => lexer.LiteralSpanning(a)).ToList();
            switch (call.Groups[1].Value)
            {
                case "Translate" when literals.Count > 0 && literals[0] is { } text:
                    Lookups.Add((text.Value, where));
                    break;
                case "Format" when literals.Count > 0 && literals[^1] is { Interpolated: true } template:
                    Lookups.Add((template.Value, where));
                    break;
                case "Plural":
                    var forms = literals.Where(l => l is { Interpolated: true }).ToList();
                    if (forms.Count == 2)
                    {
                        Plurals.Add((forms[0]!.Value, forms[1]!.Value, where));
                    }

                    break;
            }
        }
    }

    /// <summary>The 1-based line number of a position.</summary>
    /// <param name="text">The text.</param>
    /// <param name="index">The position.</param>
    /// <returns>Its line.</returns>
    private static int Line(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    /// <summary>A Tr use with an English text, quoted with apostrophes or with XML-escaped double quotes.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"\{loc:Tr Text=(?:'(?<single>[^']*)'|&quot;(?<double>.*?)&quot;)\}")]
    private static partial Regex TrText();

    /// <summary>A Tr use with a hash key.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"\{loc:Tr Key=(T[0-9a-f]{12})\}")]
    private static partial Regex TrKey();

    /// <summary>Any double-quoted XML attribute value.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"=""([^""]*)""")]
    private static partial Regex XmlAttribute();

    /// <summary>A call of one of UiText's lookups, up to its opening parenthesis.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"\bUiText\.(Translate|Format|Plural)\(")]
    private static partial Regex UiTextCall();

    /// <summary>One string literal: its runtime value (the composite format for an interpolated one) and where it is.</summary>
    /// <param name="Start">Its first character (the <c>$</c>, <c>@</c> or quote).</param>
    /// <param name="End">The position after its closing quote.</param>
    /// <param name="Value">Its value.</param>
    /// <param name="Interpolated">Whether it is an interpolated string.</param>
    internal sealed record Literal(int Start, int End, string Value, bool Interpolated);

    /// <summary>Finds a C# file's comments and string literals, and splits a call's arguments.</summary>
    private sealed class Lexer
    {
        /// <summary>The source.</summary>
        private readonly string source;

        /// <summary>Spans of comments, as (start, end): a call named in a comment (a doc comment's cref) is no call.</summary>
        private readonly List<(int Start, int End)> comments = [];

        /// <summary>Lexes a file.</summary>
        /// <param name="source">The file's text.</param>
        public Lexer(string source)
        {
            this.source = source;
            int i = 0;
            while (i < source.Length)
            {
                i = Step(i, Literals);
            }
        }

        /// <summary>Every string literal of the file, those inside interpolation holes included.</summary>
        public List<Literal> Literals { get; } = [];

        /// <summary>Whether a position lies inside a comment.</summary>
        /// <param name="index">The position.</param>
        /// <returns>Whether it does.</returns>
        public bool IsInComment(int index) => comments.Any(s => index >= s.Start && index < s.End);

        /// <summary>The top-level arguments of the call whose opening parenthesis is at a position, as trimmed spans.</summary>
        /// <param name="open">The opening parenthesis.</param>
        /// <returns>The arguments as (start, end).</returns>
        public List<(int Start, int End)> ArgumentsAt(int open)
        {
            var arguments = new List<(int, int)>();
            int depth = 0;
            int start = open + 1;
            var inner = new List<Literal>();
            for (int i = open + 1; i < source.Length;)
            {
                char c = source[i];
                if (c is '"' or '@' or '$' or '\'' or '/')
                {
                    int next = Step(i, inner);
                    if (next != i + 1 || c is '"' or '\'')
                    {
                        i = next;
                        continue;
                    }
                }

                if (c is '(' or '[' or '{')
                {
                    depth++;
                }
                else if (c is ')' or ']' or '}')
                {
                    if (depth == 0)
                    {
                        arguments.Add(Trim(start, i));
                        return arguments;
                    }

                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    arguments.Add(Trim(start, i));
                    start = i + 1;
                }

                i++;
            }

            return arguments;
        }

        /// <summary>The literal that makes up a whole argument, when it is exactly one literal.</summary>
        /// <param name="argument">The argument's span.</param>
        /// <returns>The literal, or <see langword="null"/>.</returns>
        public Literal? LiteralSpanning((int Start, int End) argument) =>
            Literals.FirstOrDefault(l => l.Start == argument.Start && l.End == argument.End);

        /// <summary>A span without surrounding whitespace.</summary>
        /// <param name="start">Start.</param>
        /// <param name="end">End.</param>
        /// <returns>The trimmed span.</returns>
        private (int, int) Trim(int start, int end)
        {
            while (start < end && char.IsWhiteSpace(source[start]))
            {
                start++;
            }

            while (end > start && char.IsWhiteSpace(source[end - 1]))
            {
                end--;
            }

            return (start, end);
        }

        /// <summary>
        /// Moves past whatever starts at a position: a comment, a char literal, a string literal (added to a list), or
        /// one other character.
        /// </summary>
        /// <param name="i">The position.</param>
        /// <param name="found">Where string literals go.</param>
        /// <returns>The next position.</returns>
        private int Step(int i, List<Literal> found)
        {
            char c = source[i];
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                int end = source.IndexOf('\n', i);
                end = end < 0 ? source.Length : end;
                comments.Add((i, end));
                return end;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? source.Length : end + 2;
                comments.Add((i, end));
                return end;
            }

            if (c == '\'')
            {
                // A char literal: '"', '\'', '\\', '"'.
                int end = i + 1;
                while (end < source.Length && source[end] != '\'')
                {
                    end += source[end] == '\\' ? 2 : 1;
                }

                return Math.Min(end + 1, source.Length);
            }

            int quote = i;
            bool interpolated = false;
            bool verbatim = false;
            while (quote < source.Length && source[quote] is '$' or '@')
            {
                interpolated |= source[quote] == '$';
                verbatim |= source[quote] == '@';
                quote++;
            }

            if (quote >= source.Length || source[quote] != '"' || (quote > i && quote - i > 2))
            {
                return i + 1;
            }

            if (quote + 2 < source.Length && source[quote + 1] == '"' && source[quote + 2] == '"')
            {
                // A raw string literal ("""…"""): skipped whole, never UI text here.
                int run = quote;
                while (run < source.Length && source[run] == '"')
                {
                    run++;
                }

                string fence = new('"', run - quote);
                int close = source.IndexOf(fence, run, StringComparison.Ordinal);
                return close < 0 ? source.Length : close + fence.Length;
            }

            var (value, after) = ReadString(quote + 1, interpolated, verbatim, found);
            found.Add(new Literal(i, after, value, interpolated));
            return after;
        }

        /// <summary>
        /// Reads a string literal's body: escapes decoded, and for an interpolated one each hole replaced by
        /// <c>{index[,alignment][:format]}</c> in order, doubled braces kept (the composite format the runtime sees).
        /// </summary>
        /// <param name="i">The first character after the opening quote.</param>
        /// <param name="interpolated">Whether holes are expressions.</param>
        /// <param name="verbatim">Whether <c>""</c> is a quote and backslashes are literal.</param>
        /// <param name="found">Where literals inside the holes go.</param>
        /// <returns>The value and the position after the closing quote.</returns>
        private (string Value, int After) ReadString(int i, bool interpolated, bool verbatim, List<Literal> found)
        {
            var value = new StringBuilder();
            int hole = 0;
            while (i < source.Length)
            {
                char c = source[i];
                if (c == '"')
                {
                    if (verbatim && i + 1 < source.Length && source[i + 1] == '"')
                    {
                        value.Append('"');
                        i += 2;
                        continue;
                    }

                    return (value.ToString(), i + 1);
                }

                if (!verbatim && c == '\\' && i + 1 < source.Length)
                {
                    i = Escape(i, value);
                    continue;
                }

                if (interpolated && (c == '{' || c == '}'))
                {
                    if (i + 1 < source.Length && source[i + 1] == c)
                    {
                        value.Append(c).Append(c);
                        i += 2;
                        continue;
                    }

                    if (c == '{')
                    {
                        i = Hole(i + 1, value, hole++, found);
                        continue;
                    }
                }

                value.Append(c);
                i++;
            }

            return (value.ToString(), i);
        }

        /// <summary>Skips one interpolation hole, writing its composite placeholder.</summary>
        /// <param name="i">The first character of the expression.</param>
        /// <param name="value">The literal's value so far.</param>
        /// <param name="index">The hole's index.</param>
        /// <param name="found">Where literals inside the hole go.</param>
        /// <returns>The position after the hole's closing brace.</returns>
        private int Hole(int i, StringBuilder value, int index, List<Literal> found)
        {
            int depth = 0;
            int alignment = -1;
            int format = -1;
            int start = i;
            while (i < source.Length)
            {
                char c = source[i];
                if (format < 0 && c is '"' or '@' or '$' or '\'')
                {
                    int next = Step(i, found);
                    if (next != i + 1 || c is '"' or '\'')
                    {
                        i = next;
                        continue;
                    }
                }

                if (format < 0 && c is '(' or '[' or '{')
                {
                    depth++;
                }
                else if (format < 0 && c is ')' or ']')
                {
                    depth--;
                }
                else if (c == '}' && (depth == 0 || format >= 0))
                {
                    if (format < 0 && depth > 0)
                    {
                        depth--;
                    }
                    else
                    {
                        value.Append('{').Append(index.ToString(CultureInfo.InvariantCulture));
                        if (alignment >= 0)
                        {
                            value.Append(source, alignment, (format >= 0 ? format : i) - alignment);
                        }

                        if (format >= 0)
                        {
                            value.Append(source, format, i - format);
                        }

                        value.Append('}');
                        return i + 1;
                    }
                }
                else if (c == '}' && depth > 0)
                {
                    depth--;
                }
                else if (depth == 0 && format < 0 && c == ',' && i > start)
                {
                    alignment = i;
                }
                else if (depth == 0 && format < 0 && c == ':'
                    && !(i + 1 < source.Length && source[i + 1] == ':') && source[i - 1] != ':')
                {
                    format = i;
                }

                i++;
            }

            return i;
        }

        /// <summary>Decodes one escape sequence of a regular string.</summary>
        /// <param name="i">The backslash.</param>
        /// <param name="value">Where the character goes.</param>
        /// <returns>The position after the sequence.</returns>
        private int Escape(int i, StringBuilder value)
        {
            char e = source[i + 1];
            switch (e)
            {
                case 'n': value.Append('\n'); return i + 2;
                case 'r': value.Append('\r'); return i + 2;
                case 't': value.Append('\t'); return i + 2;
                case '0': value.Append('\0'); return i + 2;
                case 'a': value.Append('\a'); return i + 2;
                case 'b': value.Append('\b'); return i + 2;
                case 'f': value.Append('\f'); return i + 2;
                case 'v': value.Append('\v'); return i + 2;
                case 'e': value.Append('\u001b'); return i + 2;
                case 'u':
                    value.Append((char)int.Parse(source.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    return i + 6;
                case 'U':
                    value.Append(char.ConvertFromUtf32(int.Parse(source.AsSpan(i + 2, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                    return i + 10;
                case 'x':
                    int end = i + 2;
                    while (end < source.Length && end < i + 6 && Uri.IsHexDigit(source[end]))
                    {
                        end++;
                    }

                    value.Append((char)int.Parse(source.AsSpan(i + 2, end - i - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    return end;
                default:
                    value.Append(e);
                    return i + 2;
            }
        }
    }
}
