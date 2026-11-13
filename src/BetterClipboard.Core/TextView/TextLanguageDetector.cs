using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BetterClipboard.Core.TextView;

/// <summary>
/// Guesses what a copied text is — XML, JSON, C#, a shell command, prose — so the text viewer can color or draw it. Anything
/// it does not recognize as code is <see cref="TextLanguage.Markdown"/> (the owner's fallback, 2026-11-13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Precision over recall.</b> A miss leaves code drawn as Markdown — readable, only without colors; a false hit would
/// draw a person's prose in the code font with keywords colored. So a language must win clearly: enough points, on enough
/// of the lines, and more than the prose and Markdown signals together.
/// </para>
/// <para>
/// <b>How.</b> Structure first: JSON that parses, a text that opens with a tag (XML, HTML, PHP), a diff's headers, a
/// shebang, Markdown code fences. Then each of the first <see cref="MaxLines"/> lines is scored against weighted
/// patterns per language (<c>using System;</c> for C#, <c>def x():</c> for Python, <c>Get-ChildItem</c> for PowerShell),
/// and against prose (sentences) and Markdown (headings, lists, links). Only the first <see cref="SampleChars"/> characters
/// are read, so a 50 MB text costs what its head costs.
/// </para>
/// <para>
/// <b>Never throws, never hangs.</b> Patterns run interpreted with a timeout per match (a timed-out pattern counts as
/// no match), lines are cut at <see cref="MaxLineLength"/> characters, and JSON is checked by a forward-only reader.
/// </para>
/// </remarks>
public static partial class TextLanguageDetector
{
    /// <summary>How much of a text is read: 64 K characters, ample for the head of any file.</summary>
    public const int SampleChars = 64 * 1024;

    /// <summary>How many non-blank lines are scored.</summary>
    public const int MaxLines = 200;

    /// <summary>How much of one line the patterns see; a minified line is judged by its start.</summary>
    public const int MaxLineLength = 400;

    /// <summary>The fewest points a language needs to be chosen at all.</summary>
    private const double MinScore = 3;

    /// <summary>How long one pattern may run on one line before it counts as no match.</summary>
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary>A weighted pattern: a line matching <see cref="Pattern"/> gives <see cref="Language"/> <see cref="Weight"/> points.</summary>
    /// <param name="Language">The language the pattern speaks for.</param>
    /// <param name="Weight">Its points; distinctive forms weigh 3, shared ones 1 or less.</param>
    /// <param name="Pattern">The pattern, run on each line.</param>
    private sealed record Feature(TextLanguage Language, double Weight, Regex Pattern);

    /// <summary>The per-line patterns of every language, built on first use.</summary>
    private static readonly Lazy<Feature[]> Features = new(BuildFeatures);

    /// <summary>
    /// The element names that make a tagged text HTML rather than XML, in HTML's lower case: XAML's <c>&lt;Button&gt;</c>
    /// and <c>&lt;Grid&gt;</c> are XML.
    /// </summary>
    private static readonly HashSet<string> HtmlTags = new(StringComparer.Ordinal)
    {
        "html", "head", "body", "div", "span", "p", "a", "ul", "ol", "li", "table", "tr", "td", "th", "thead", "tbody", "img",
        "br", "hr", "meta", "link", "script", "style", "form", "input", "button", "h1", "h2", "h3", "h4", "h5", "h6", "section",
        "article", "nav", "header", "footer", "main", "label", "select", "option", "textarea", "pre", "code", "em", "strong",
        "b", "i", "u", "iframe", "title", "aside", "figure", "blockquote", "small", "sup", "sub", "canvas", "video", "audio",
    };

    /// <summary>
    /// Guesses the language of a text.
    /// </summary>
    /// <param name="text">The text; may be <see langword="null"/>.</param>
    /// <returns>The language; <see cref="TextLanguage.Markdown"/> for prose, anything unrecognized, and no text.</returns>
    public static TextLanguage Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return TextLanguage.Markdown;
        }

        string sample = text.Length > SampleChars ? text[..SampleChars] : text;
        var trimmed = sample.AsSpan().TrimStart([' ', '\t', '\r', '\n', '\uFEFF']);

        if (StructuralGuess(text, sample, trimmed) is { } structural)
        {
            return structural;
        }

        return ScoreLines(sample);
    }

    /// <summary>
    /// The answers that structure alone gives: JSON that parses, a tag at the start, a diff, a shebang, Markdown fences.
    /// </summary>
    /// <param name="text">The whole text (JSON is validated on all of it when it is not huge).</param>
    /// <param name="sample">Its head.</param>
    /// <param name="trimmed">The head without leading blanks.</param>
    /// <returns>The language, or <see langword="null"/> to let the line scores decide.</returns>
    private static TextLanguage? StructuralGuess(string text, string sample, ReadOnlySpan<char> trimmed)
    {
        char first = trimmed[0];

        // JSON with comments may open with one ("// settings for x"): the first value comes after them.
        char firstValue = SkipLeadingComments(trimmed) is { Length: > 0 } afterComments ? afterComments[0] : first;
        if (firstValue is '{' or '[' && IsJson(text.Length <= 4 * SampleChars ? text : sample, isWhole: text.Length <= 4 * SampleChars))
        {
            return TextLanguage.Json;
        }

        if (first == '<')
        {
            if (trimmed.StartsWith("<?php", StringComparison.OrdinalIgnoreCase))
            {
                return TextLanguage.Php;
            }

            if (trimmed.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            {
                return TextLanguage.Html;
            }

            if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
            {
                return TextLanguage.Xml;
            }

            if (MarkupGuess(trimmed) is { } markup)
            {
                return markup;
            }
        }

        if (IsDiff(sample))
        {
            return TextLanguage.Diff;
        }

        if (trimmed.StartsWith("#!"))
        {
            int lineEnd = trimmed.IndexOf('\n');
            if (ShebangLanguage(lineEnd >= 0 ? trimmed[..lineEnd] : trimmed) is { } interpreter)
            {
                return interpreter;
            }
        }

        // Prose with fenced code is Markdown: its code blocks are colored by their own fence word.
        return HasCodeFence(sample) ? TextLanguage.Markdown : null;
    }

    /// <summary>Skips comments and blanks at a text's start (<c>// x</c> lines, <c>/* x */</c>), as JSON with comments may open with them.</summary>
    /// <param name="text">The text's head without leading blanks.</param>
    /// <returns>What follows the comments; the text itself when it opens with no comment, empty when it is only comments.</returns>
    private static ReadOnlySpan<char> SkipLeadingComments(ReadOnlySpan<char> text)
    {
        while (true)
        {
            if (text.StartsWith("//"))
            {
                int lineEnd = text.IndexOf('\n');
                text = lineEnd < 0 ? [] : text[(lineEnd + 1)..];
            }
            else if (text.StartsWith("/*"))
            {
                int close = text.IndexOf("*/");
                text = close < 0 ? [] : text[(close + 2)..];
            }
            else
            {
                return text;
            }

            text = text.TrimStart();
        }
    }

    /// <summary>
    /// Whether a text is JSON (with comments and trailing commas allowed, several values in a row allowed for JSON Lines):
    /// read to its end — or, for a huge text's head, to the head's end without an error.
    /// </summary>
    /// <param name="text">The text or its head.</param>
    /// <param name="isWhole">Whether <paramref name="text"/> is the whole text (then it must end where a value ends).</param>
    /// <returns><see langword="true"/> for JSON.</returns>
    internal static bool IsJson(string text, bool isWhole)
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text), isWhole, new JsonReaderState(new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                AllowMultipleValues = true,
                MaxDepth = 256,
            }));
            int tokens = 0;
            while (reader.Read())
            {
                tokens++;
            }

            // "[1]" is JSON too, but a bracketed word ("[x]" in a list of notes) never reads at all.
            return tokens >= 2;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Tells XML from HTML for a text that opens with a tag, or says it is neither (a lone <c>&lt;</c>, a placeholder like
    /// <c>&lt;your name&gt;</c> before prose).
    /// </summary>
    /// <param name="trimmed">The text's head without leading blanks; starts with <c>&lt;</c>.</param>
    /// <returns><see cref="TextLanguage.Html"/>, <see cref="TextLanguage.Xml"/>, or <see langword="null"/>.</returns>
    private static TextLanguage? MarkupGuess(ReadOnlySpan<char> trimmed)
    {
        int i = 1;
        if (i < trimmed.Length && trimmed[i] == '!')
        {
            // A comment or declaration first: markup if more tags follow.
            return trimmed.IndexOf("</") > 0 ? TextLanguage.Xml : null;
        }

        int nameStart = i;
        while (i < trimmed.Length && (char.IsLetterOrDigit(trimmed[i]) || trimmed[i] is '_' or ':' or '-' or '.'))
        {
            i++;
        }

        if (i == nameStart || !char.IsLetter(trimmed[nameStart]))
        {
            return null;
        }

        var name = trimmed[nameStart..i].ToString();

        // The first tag must close with '>' on its own terms (attributes allowed): "<your name>" has a space but no '=',
        // so it is a placeholder in prose, not a tag with attributes.
        int close = trimmed.IndexOf('>');
        if (close < 0)
        {
            return null;
        }

        var tag = trimmed[i..close];
        if (tag.Trim().Length > 0 && tag.IndexOf('=') < 0 && !tag.Trim().SequenceEqual("/"))
        {
            return null;
        }

        // XML needs its element to close somewhere (or the tag to close itself); HTML may leave elements open.
        bool closes = trimmed.IndexOf(("</" + name).AsSpan(), StringComparison.Ordinal) > 0 || trimmed[..(close + 1)].EndsWith("/>");
        if (HtmlTags.Contains(name))
        {
            return TextLanguage.Html;
        }

        return closes ? TextLanguage.Xml : null;
    }

    /// <summary>Whether a text is a unified diff: a git header, or file lines followed by a hunk header.</summary>
    /// <param name="sample">The text's head.</param>
    /// <returns><see langword="true"/> for a diff.</returns>
    private static bool IsDiff(string sample)
    {
        if (sample.StartsWith("diff --git ", StringComparison.Ordinal) || sample.StartsWith("Index: ", StringComparison.Ordinal))
        {
            return true;
        }

        int minus = sample.IndexOf("--- ", StringComparison.Ordinal);
        int plus = minus >= 0 ? sample.IndexOf("\n+++ ", minus, StringComparison.Ordinal) : -1;
        int hunk = plus >= 0 ? sample.IndexOf("\n@@ -", plus, StringComparison.Ordinal) : -1;
        return minus >= 0 && (minus == 0 || sample[minus - 1] == '\n') && plus > minus && hunk > plus;
    }

    /// <summary>The language a shebang line runs (<c>#!/usr/bin/env python3</c> → Python).</summary>
    /// <param name="shebang">The first line.</param>
    /// <returns>The language, or <see langword="null"/> for an interpreter it does not know.</returns>
    private static TextLanguage? ShebangLanguage(ReadOnlySpan<char> shebang)
    {
        var line = shebang.ToString();
        return line.Contains("python", StringComparison.Ordinal) ? TextLanguage.Python
            : line.Contains("pwsh", StringComparison.Ordinal) || line.Contains("powershell", StringComparison.OrdinalIgnoreCase) ? TextLanguage.PowerShell
            : line.Contains("node", StringComparison.Ordinal) || line.Contains("deno", StringComparison.Ordinal) ? TextLanguage.JavaScript
            : line.Contains("ruby", StringComparison.Ordinal) ? TextLanguage.Ruby
            : line.Contains("php", StringComparison.Ordinal) ? TextLanguage.Php
            : line.EndsWith("sh", StringComparison.Ordinal) || line.Contains("bash", StringComparison.Ordinal) || line.Contains("/sh ", StringComparison.Ordinal) || line.Contains("zsh", StringComparison.Ordinal) ? TextLanguage.Shell
            : null;
    }

    /// <summary>Whether a text holds a Markdown code fence: a line that starts with three backticks or tildes.</summary>
    /// <param name="sample">The text's head.</param>
    /// <returns><see langword="true"/> when a fence is there.</returns>
    private static bool HasCodeFence(string sample)
    {
        foreach (var line in sample.AsSpan().EnumerateLines())
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Scores the head's lines for every language, for prose and for Markdown, and picks the code language that wins
    /// clearly (see the class remarks), else Markdown.
    /// </summary>
    /// <param name="sample">The text's head.</param>
    /// <returns>The language.</returns>
    private static TextLanguage ScoreLines(string sample)
    {
        var scores = new Dictionary<TextLanguage, double>();
        var linesHit = new Dictionary<TextLanguage, int>();
        double prose = 0;
        double markdown = 0;
        double headings = 0;
        int lines = 0;

        foreach (var raw in sample.AsSpan().EnumerateLines())
        {
            if (raw.IsWhiteSpace())
            {
                continue;
            }

            if (++lines > MaxLines)
            {
                break;
            }

            var line = (raw.Length > MaxLineLength ? raw[..MaxLineLength] : raw).ToString();
            prose += ProseWeight(line);

            // A heading is also what a "# comment" line of Python, a shell or YAML looks like, and code can have dozens:
            // headings count for at most three, while a document rarely needs more to look like one.
            double lineMarkdown = MarkdownWeight(line);
            if (HeadingWeight(line) is var heading and > 0)
            {
                lineMarkdown -= heading;
                if (headings < 3 * heading)
                {
                    lineMarkdown += heading;
                    headings += heading;
                }
            }

            markdown += lineMarkdown;

            var hitHere = new HashSet<TextLanguage>();
            foreach (var feature in Features.Value)
            {
                if (Matches(feature.Pattern, line))
                {
                    scores[feature.Language] = scores.GetValueOrDefault(feature.Language) + feature.Weight;
                    hitHere.Add(feature.Language);
                }
            }

            foreach (var language in hitHere)
            {
                linesHit[language] = linesHit.GetValueOrDefault(language) + 1;
            }
        }

        if (lines == 0)
        {
            return TextLanguage.Markdown;
        }

        // TypeScript is JavaScript with types: its own patterns only add to what JavaScript's found.
        if (scores.TryGetValue(TextLanguage.TypeScript, out double typeScript) && scores.TryGetValue(TextLanguage.JavaScript, out double javaScript))
        {
            scores[TextLanguage.TypeScript] = typeScript + javaScript;
            linesHit[TextLanguage.TypeScript] = Math.Max(linesHit.GetValueOrDefault(TextLanguage.TypeScript), linesHit.GetValueOrDefault(TextLanguage.JavaScript));
            if (typeScript < 2)
            {
                scores.Remove(TextLanguage.TypeScript);
            }
        }

        var best = scores.OrderByDescending(pair => pair.Value).ThenBy(pair => (int)pair.Key).FirstOrDefault();
        if (best.Value < MinScore)
        {
            return TextLanguage.Markdown;
        }

        // Clearly code: points beyond the prose and Markdown signals, and on enough of the lines (a third, or every line
        // of a text of one or two lines).
        double coverage = linesHit.GetValueOrDefault(best.Key) / (double)lines;
        bool enoughLines = coverage >= 0.3 || (lines <= 2 && coverage >= 0.5);
        return best.Value > prose + markdown && enoughLines ? best.Key : TextLanguage.Markdown;
    }

    /// <summary>Runs a pattern on a line, counting a timed-out run as no match.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <param name="line">The line.</param>
    /// <returns><see langword="true"/> on a match.</returns>
    private static bool Matches(Regex pattern, string line)
    {
        try
        {
            return pattern.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// How much a line reads like a sentence: six or more words, nearly all letters and spaces, not ending the way code
    /// lines end (<c>;</c>, <c>{</c>, <c>)</c>, <c>,</c>), and not a comment line.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>1 for a sentence, 0.5 for a short one, 0 otherwise.</returns>
    internal static double ProseWeight(string line)
    {
        // Comment lines are the code's own prose: they must not make a commented script look like a document. A '#' line
        // is a comment (Python, shells, YAML) or a Markdown heading, which counts as Markdown instead.
        var trimmed = line.AsSpan().Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.StartsWith("*") || trimmed.StartsWith("<!--") ||
            trimmed.StartsWith("--") || trimmed.StartsWith("#") || trimmed.StartsWith("::") || trimmed.StartsWith("rem ", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        char last = trimmed[^1];
        if (last is ';' or '{' or '}' or ')' or ',' or '(' or '[' or ']' or '=' or '>')
        {
            return 0;
        }

        int words = 0;
        int letters = 0;
        bool inWord = false;
        foreach (char c in trimmed)
        {
            if (char.IsLetter(c))
            {
                letters++;
                if (!inWord)
                {
                    words++;
                    inWord = true;
                }
            }
            else
            {
                inWord = c is '\'' or '\u2019' or '-' && inWord;
                if (c == ' ')
                {
                    letters++; // spaces count with letters: "a b c" reads as text
                }
            }
        }

        double share = letters / (double)trimmed.Length;
        return words >= 6 && share >= 0.85 ? 1 : words >= 3 && share >= 0.85 && last is '.' or '!' or '?' ? 0.5 : 0;
    }

    /// <summary>How much a line looks like Markdown: a heading, a list item, a quote, a table row, emphasis, a link.</summary>
    /// <param name="line">The line.</param>
    /// <returns>The line's Markdown points.</returns>
    internal static double MarkdownWeight(string line)
    {
        double weight = HeadingWeight(line);
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal) || OrderedItemPattern().IsMatch(trimmed))
        {
            weight += 0.5;
        }

        if (trimmed.StartsWith("> ", StringComparison.Ordinal))
        {
            weight += 1;
        }

        if (trimmed.StartsWith('|') && trimmed.TrimEnd().EndsWith('|'))
        {
            weight += 1;
        }

        if (LinkPattern().IsMatch(line))
        {
            weight += 1.5;
        }

        if (BoldPattern().IsMatch(line))
        {
            weight += 1;
        }

        return weight;
    }

    /// <summary>The Markdown points of a heading line (<c># Title</c>): 1.5, or 0 for any other line.</summary>
    /// <param name="line">The line.</param>
    /// <returns>The heading's points.</returns>
    private static double HeadingWeight(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length > 2 && trimmed[0] == '#' && HeadingPattern().IsMatch(trimmed) ? 1.5 : 0;
    }

    /// <summary>A Markdown heading: one to six hashes, a space, then text that starts with a letter or digit.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"^#{1,6} +[\p{L}\p{N}`*_]", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingPattern();

    /// <summary>A numbered list item: digits, a dot or parenthesis, a space.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"^\d{1,3}[.)] +\S", RegexOptions.CultureInvariant)]
    private static partial Regex OrderedItemPattern();

    /// <summary>A Markdown link or image: <c>[text](address)</c>.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"\[[^\]\n]{1,200}\]\([^)\s]{1,500}\)", RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();

    /// <summary>Bold text: <c>**words**</c> or <c>__words__</c> around a non-blank start and end.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"(\*\*|__)\S[^*_\n]{0,200}\S\1", RegexOptions.CultureInvariant)]
    private static partial Regex BoldPattern();

    /// <summary>Builds every language's line patterns (interpreted, each with a timeout).</summary>
    /// <returns>The features.</returns>
    private static Feature[] BuildFeatures()
    {
        var list = new List<Feature>();

        // Adds one pattern; case-insensitive ones say so.
        void Add(TextLanguage language, double weight, string pattern, bool ignoreCase = false) =>
            list.Add(new Feature(language, weight, new Regex(pattern, RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), PatternTimeout)));

        Add(TextLanguage.CSharp, 3, @"^\s*using\s+(static\s+)?[A-Z][\w.]*\s*;\s*$");
        Add(TextLanguage.CSharp, 3, @"^\s*namespace\s+[A-Z][\w.]*\s*[;{]?\s*$");
        Add(TextLanguage.CSharp, 2, @"\b(public|private|protected|internal)\s+((static|readonly|sealed|abstract|override|async|virtual|partial|const|new)\s+)*(class|record|struct|interface|enum|void|Task|string|int|bool|var|IEnumerable|List|Dictionary)\b");
        Add(TextLanguage.CSharp, 3, @"\{\s*get;\s*((private\s+)?(set|init);)?\s*\}");
        Add(TextLanguage.CSharp, 3, @"\basync\s+Task\b|\bawait\s+\w+Async\(");
        Add(TextLanguage.CSharp, 3, @"\bConsole\.(Write|WriteLine|ReadLine)\(");
        Add(TextLanguage.CSharp, 1.5, @"^\s*\[[A-Z]\w*(\(.*\))?\]\s*$");
        Add(TextLanguage.CSharp, 2, @"^\s*#(region|endregion|nullable|pragma\s+warning)\b");
        Add(TextLanguage.CSharp, 1, @"\b(var|string|int|bool)\s+\w+\s*=\s*.*;\s*$");
        Add(TextLanguage.CSharp, 1, @"\b(nameof|typeof|default)\(|\bforeach\s*\(\s*var\b|\$""[^""]*\{");

        Add(TextLanguage.Java, 3, @"^\s*package\s+[a-z][\w.]*\s*;\s*$");
        Add(TextLanguage.Java, 3, @"^\s*import\s+(static\s+)?[a-z][\w.]*(\.\*)?\s*;\s*$");
        Add(TextLanguage.Java, 3, @"\bSystem\.out\.print(ln|f)?\(");
        Add(TextLanguage.Java, 3, @"\bpublic\s+static\s+void\s+main\s*\(\s*String");
        Add(TextLanguage.Java, 2, @"^\s*@(Override|Autowired|Test|Deprecated|SuppressWarnings|Bean|Component|Service|Entity)\b");
        Add(TextLanguage.Java, 2, @"\bthrows\s+[A-Z]\w*(Exception|Error)\b");
        Add(TextLanguage.Java, 1, @"\b(final\s+)?(String|int|boolean|long|double)\s+\w+\s*=\s*.*;\s*$");

        Add(TextLanguage.Kotlin, 3, @"^\s*((private|public|internal|override|suspend|inline)\s+)*fun\s+(<[^>]+>\s*)?[\w.]+\s*\(");
        Add(TextLanguage.Kotlin, 1.5, @"^\s*(val|var)\s+\w+(\s*:\s*[A-Z][\w<>?, ]*)?\s*=\s*[^;]*$");
        Add(TextLanguage.Kotlin, 2, @"^\s*package\s+[a-z][\w.]*\.[\w.]+\s*$");
        Add(TextLanguage.Kotlin, 3, @"\bdata\s+class\b|\bcompanion\s+object\b|\blateinit\s+var\b");
        Add(TextLanguage.Kotlin, 1, @"\bwhen\s*(\(.*\))?\s*\{|\?:|\bprintln\(");

        Add(TextLanguage.JavaScript, 2, @"\bfunction\s*\*?\s*\w*\s*\([^)$]*\)\s*\{");
        Add(TextLanguage.JavaScript, 1.5, @"^\s*(export\s+)?(const|let)\s+[\w{[]");
        Add(TextLanguage.JavaScript, 3, @"\bconsole\.(log|error|warn|info|debug)\(");
        Add(TextLanguage.JavaScript, 3, @"\brequire\(\s*['""]|\bmodule\.exports\b");
        Add(TextLanguage.JavaScript, 2, @"^\s*import\s+.*\s+from\s+['""]|^\s*import\s+['""]");
        Add(TextLanguage.JavaScript, 2, @"^\s*export\s+(default|const|function|class|async)\b");
        Add(TextLanguage.JavaScript, 1.5, @"===|!==|\bdocument\.\w|\bwindow\.\w|\bundefined\b");
        Add(TextLanguage.JavaScript, 1, @"=>\s*[{(]|\)\s*=>");

        Add(TextLanguage.TypeScript, 2, @":\s*(string|number|boolean|any|void|unknown|never)(\[\])?\s*[;,)=]");
        Add(TextLanguage.TypeScript, 3, @"^\s*(export\s+)?interface\s+[A-Z]\w*(<[^>]*>)?\s*(extends\s+[\w<>, ]+)?\{?\s*$");
        Add(TextLanguage.TypeScript, 3, @"^\s*(export\s+)?type\s+[A-Z]\w*(<[^>]*>)?\s*=");
        Add(TextLanguage.TypeScript, 2, @"\bas\s+(const|string|number|any|unknown)\b|\b(private|public|readonly)\s+\w+\s*:\s*\w");

        Add(TextLanguage.Python, 3, @"^\s*(async\s+)?def\s+\w+\s*\(.*\)\s*(->\s*[^:]+)?:\s*(#.*)?$");
        Add(TextLanguage.Python, 3, @"^\s*class\s+\w+(\(.*\))?\s*:\s*$");
        Add(TextLanguage.Python, 3, @"^\s*from\s+[\w.]+\s+import\s+[\w*(]");
        Add(TextLanguage.Python, 1.5, @"^\s*import\s+[a-z_][\w.]*(\s+as\s+\w+)?(\s*,\s*[a-z_][\w.]*)*\s*$");
        Add(TextLanguage.Python, 2, @"\bself\.\w+");
        Add(TextLanguage.Python, 3, @"^\s*(elif\b.*|except\b.*|else|try|finally)\s*:\s*$");
        Add(TextLanguage.Python, 3, @"if\s+__name__\s*==\s*['""]__main__['""]");
        Add(TextLanguage.Python, 1.5, @"^\s*(for|while|if|with)\s+[^{;]*:\s*$");
        Add(TextLanguage.Python, 1, @"\b(None|True|False)\b|\blambda\s+\w*\s*:|\bf['""][^'""]*\{");

        Add(TextLanguage.Ruby, 2.5, @"^\s*def\s+(self\.)?\w+[?!]?(\(.*\))?\s*$");
        Add(TextLanguage.Ruby, 1.5, @"^\s*end\s*$");
        Add(TextLanguage.Ruby, 3, @"^\s*require(_relative)?\s+['""]");
        Add(TextLanguage.Ruby, 3, @"\battr_(accessor|reader|writer)\b|\bdo\s*\|[\w, ]+\|");
        Add(TextLanguage.Ruby, 2, @"^\s*puts\s|\.each(_with_index)?\s+do\b|\belsif\b");
        Add(TextLanguage.Ruby, 1.5, @"^\s*(module|class)\s+[A-Z]\w*(\s*<\s*[A-Z][\w:]*)?\s*$|@\w+\s*=[^=]");

        Add(TextLanguage.Php, 3, @"^\s*namespace\s+[A-Z][\w\\]*\s*;");
        Add(TextLanguage.Php, 3, @"\bpublic\s+(static\s+)?function\b|\$this->\w");
        Add(TextLanguage.Php, 1.5, @"^\s*\$\w+\s*=\s*.*;\s*$");
        Add(TextLanguage.Php, 2, @"\bfunction\s+\w+\s*\([^)]*\$\w+");

        Add(TextLanguage.Go, 2.5, @"^\s*package\s+[a-z]\w*\s*$");
        Add(TextLanguage.Go, 3, @"^\s*func\s+(\(\s*\w+\s+\*?[\w.]+\s*\)\s*)?\w+\s*\(");
        Add(TextLanguage.Go, 1.5, @"\w\s*:=\s*");
        Add(TextLanguage.Go, 2, @"^\s*import\s+(\(\s*$|""[\w./-]+""\s*$)");
        Add(TextLanguage.Go, 3, @"\bfmt\.\w+\(|\berr\s*!=\s*nil\b|\bgo\s+func\b");
        Add(TextLanguage.Go, 3, @"^\s*type\s+\w+\s+(struct|interface)\s*\{");
        Add(TextLanguage.Go, 1.5, @"^\s*defer\s|\bchan\s+\w|\bmake\(\s*(map|\[\]|chan)");

        Add(TextLanguage.Rust, 3, @"^\s*(pub(\(crate\))?\s+)?(async\s+)?fn\s+\w+\s*(<[^>]*>)?\s*\(");
        Add(TextLanguage.Rust, 3, @"\blet\s+mut\s+\w|#\[derive\(|\b(println|format|vec|panic|assert_eq)!\s*[(\[]");
        Add(TextLanguage.Rust, 2.5, @"^\s*use\s+(std|crate|super|self)?(::)?[\w:]+(::\{[^}]*\})?\s*;\s*$");
        Add(TextLanguage.Rust, 2, @"^\s*impl\b|&mut\s+\w|->\s*(Result|Option|Self|&?str|String|Vec)\b");
        Add(TextLanguage.Rust, 1, @"\b(Some|Ok|Err)\(|\bmatch\s+[\w.&]+\s*\{");

        Add(TextLanguage.Cpp, 3, @"^\s*#\s*include\s*[<""]");
        Add(TextLanguage.Cpp, 2, @"^\s*#\s*(define|ifdef|ifndef|endif|pragma\s+once)\b");
        Add(TextLanguage.Cpp, 3, @"\bstd::\w|\bint\s+main\s*\(|\bcout\s*<<|\bcin\s*>>");
        Add(TextLanguage.Cpp, 2, @"\b(printf|scanf|malloc|free|memcpy|strlen)\s*\(|\bnullptr\b|\btemplate\s*<");
        Add(TextLanguage.Cpp, 1.5, @"\bunsigned\s+(int|char|long)\b|\bchar\s*\*\s*\w|^\s*typedef\s");

        Add(TextLanguage.PowerShell, 3, @"\b(Get|Set|New|Remove|Add|Invoke|Write|Start|Stop|Test|Import|Export|Select|Where|ForEach|Out|ConvertTo|ConvertFrom|Join|Split|Copy|Move|Rename|Clear|Enable|Disable|Register|Resolve|Wait|Measure|Sort|Group|Format|Read|Update|Install|Uninstall|Expand|Compress|Push|Pop)-[A-Z][A-Za-z]+");
        Add(TextLanguage.PowerShell, 2.5, @"\$_\b|\$PSItem\b|\$env:\w+|\$PSScriptRoot\b|\$(true|false|null)\b|\$LASTEXITCODE\b", ignoreCase: true);
        Add(TextLanguage.PowerShell, 2, @"\s-(eq|ne|gt|lt|ge|le|like|notlike|match|notmatch|contains|in|notin)\s", ignoreCase: true);
        Add(TextLanguage.PowerShell, 3, @"^\s*param\s*\(|^\s*\[CmdletBinding\(", ignoreCase: true);
        Add(TextLanguage.PowerShell, 2, @"\[(string|int|bool|switch|PSCustomObject|array|hashtable|datetime|object)(\[\])?\]\s*\$", ignoreCase: true);
        Add(TextLanguage.PowerShell, 2, @"\|\s*(%|\?)\s*\{|@\{\s*\w+\s*=");
        Add(TextLanguage.PowerShell, 1, @"^\s*\$\w+\s*=\s*[^;]*$|\s-[A-Z][a-z]+(\s|$)");

        Add(TextLanguage.Shell, 5, @"^#!\s*/(usr/)?bin/(env\s+)?(ba|z|k|da)?sh\b");
        Add(TextLanguage.Shell, 1.5, @"^\s*(sudo\s+)?(echo|export|cd|ls|cat|grep|sed|awk|apt(-get)?|yum|dnf|brew|npm|npx|pnpm|yarn|pip3?|git|docker|kubectl|curl|wget|chmod|chown|mkdir|rm|cp|mv|tar|ssh|scp|make|source|alias|systemctl|journalctl|find|xargs|tail|head)\s+\S");
        Add(TextLanguage.Shell, 2, @"\$\{\w+[^}]*\}|\$\([a-z]");
        Add(TextLanguage.Shell, 3, @"^\s*(if\s+\[\[?\s|fi\s*$|then\s*$|done\s*$|esac\s*$|do\s*$|elif\s+\[)");
        Add(TextLanguage.Shell, 2, @"\s2>&1\b|\s>\s*/dev/null|\|\s*(grep|awk|sed|xargs|sort|uniq|wc|head|tail)\b");
        Add(TextLanguage.Shell, 1, @"^\s*(export\s+)?[A-Z_][A-Z0-9_]*=\S|\s(&&|\|\|)\s");

        Add(TextLanguage.Batch, 4, @"^\s*@?echo\s+(off|on)\s*$", ignoreCase: true);
        Add(TextLanguage.Batch, 3, @"^\s*(rem\s|::)", ignoreCase: true);
        Add(TextLanguage.Batch, 2.5, @"%[A-Za-z_]\w*%|%~[a-z]*\d|%%[A-Za-z]\b");
        Add(TextLanguage.Batch, 2, @"^\s*(set\s+(/[ap]\s+)?""?\w+=|setlocal\b|endlocal\b|goto\s+:?\w+|call\s+:\w+)", ignoreCase: true);
        Add(TextLanguage.Batch, 2, @"^\s*:[A-Za-z_]\w*\s*$");
        Add(TextLanguage.Batch, 3, @"\bif\s+(not\s+)?(exist|defined|errorlevel)\b", ignoreCase: true);

        Add(TextLanguage.Sql, 3, @"^\s*(SELECT\s+(DISTINCT\s+|TOP\s+\d+\s+)?[\w*""\[(]|INSERT\s+INTO\s|UPDATE\s+[\w.\[""]+\s+SET\s|DELETE\s+FROM\s|CREATE\s+(OR\s+REPLACE\s+)?(TABLE|VIEW|INDEX|UNIQUE\s+INDEX|PROCEDURE|FUNCTION|DATABASE|TRIGGER)\s|ALTER\s+TABLE\s|DROP\s+(TABLE|VIEW|INDEX)\s|WITH\s+\w+\s+AS\s*\()");
        Add(TextLanguage.Sql, 1.5, @"^\s*(select\s+[\w*.,\s()]+\s+from\s+[\w.]+|insert\s+into\s+[\w.]+\s*\(|update\s+[\w.]+\s+set\s+\w+\s*=|delete\s+from\s+[\w.]+(\s+where\b|\s*;))");
        Add(TextLanguage.Sql, 1.5, @"\b(FROM|WHERE|JOIN|GROUP\s+BY|ORDER\s+BY|HAVING|LIMIT|VALUES)\b");
        Add(TextLanguage.Sql, 1, @"\b(INNER|LEFT|RIGHT|FULL|CROSS)\s+(OUTER\s+)?JOIN\b|\bPRIMARY\s+KEY\b|\bNOT\s+NULL\b|\bVARCHAR\(");

        Add(TextLanguage.Css, 3, @"@media\s|@import\s|@keyframes\s|@font-face\b|!important\b");
        Add(TextLanguage.Css, 2, @"^\s*(--[\w-]+|color|background(-color|-image)?|margin(-\w+)?|padding(-\w+)?|display|position|top|left|right|bottom|width|height|max-width|min-width|max-height|min-height|font(-family|-size|-weight|-style)?|line-height|text-(align|decoration|transform)|border(-[\w-]+)?|box-shadow|box-sizing|opacity|z-index|overflow(-[xy])?|flex(-[\w-]+)?|grid(-[\w-]+)?|align-(items|self|content)|justify-(content|items|self)|gap|transition|transform|animation|cursor|content|visibility|white-space|outline)\s*:\s*[^;{}]+;\s*$");
        Add(TextLanguage.Css, 2, @"^\s*([.#][\w-]+|[a-z][\w-]*)([.#:][\w-]+|\[[^\]]*\]|\s*[,>+~ ]\s*[.#]?[\w-]+)*\s*(::?[\w-]+(\([^)]*\))?)?\s*\{\s*$");

        Add(TextLanguage.Yaml, 2, @"^---\s*$");
        Add(TextLanguage.Yaml, 1, @"^\s*[\w.-]+:\s*$");
        Add(TextLanguage.Yaml, 0.8, @"^\s*[\w.-]+:\s+[^\s{].*$");
        Add(TextLanguage.Yaml, 1.5, @"^\s*-\s+[\w.-]+:\s");

        Add(TextLanguage.Ini, 2, @"^\s*\[[\w .:""-]+\]\s*$");
        Add(TextLanguage.Ini, 1, @"^\s*[\w.-]+\s*=\s*\S");
        Add(TextLanguage.Ini, 1, @"^\s*;");

        return [.. list];
    }
}
