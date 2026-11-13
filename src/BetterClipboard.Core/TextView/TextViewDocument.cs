using System.Text;
using BetterClipboard.Core.Presentation;

namespace BetterClipboard.Core.TextView;

/// <summary>
/// A text prepared for the text viewer: line breaks normalized, cut to the caller's limit, its language guessed. One per
/// opened card; the views of it (<see cref="TextViewRendering"/>) are made from it.
/// </summary>
/// <remarks>
/// Built off the UI thread (a copy can be megabytes). Normalizing keeps one character per character except line breaks:
/// CRLF, CR, U+2028 and U+2029 become <c>\n</c>, so RichEdit's positions match the text's for the line-number gutter.
/// </remarks>
public sealed class TextViewSource
{
    /// <summary>The re-indented text per language, worked out on first request (<see langword="null"/> = not formattable).</summary>
    private readonly Dictionary<TextLanguage, string?> reindented = [];

    /// <summary>Guards <see cref="reindented"/>: a view may be prepared on any pool thread.</summary>
    private readonly Lock gate = new();

    /// <summary>Creates a prepared source.</summary>
    /// <param name="text">The normalized, possibly cut text.</param>
    /// <param name="originalLength">The normalized text's length before the cut.</param>
    /// <param name="originalLineCount">The whole text's line count.</param>
    /// <param name="detectedLanguage">The guessed language.</param>
    private TextViewSource(string text, int originalLength, int originalLineCount, TextLanguage detectedLanguage)
    {
        Text = text;
        OriginalLength = originalLength;
        OriginalLineCount = originalLineCount;
        DetectedLanguage = detectedLanguage;
    }

    /// <summary>The text shown: normalized line breaks, at most the caller's limit.</summary>
    public string Text { get; }

    /// <summary>How long the whole (normalized) text is; more than <see cref="Text"/>'s length when it was cut.</summary>
    public int OriginalLength { get; }

    /// <summary>How many lines the whole text has (a text without a line break has one).</summary>
    public int OriginalLineCount { get; }

    /// <summary>Whether <see cref="Text"/> is only the start of the text (the peek's limit).</summary>
    public bool IsCut => Text.Length < OriginalLength;

    /// <summary>What <see cref="TextLanguageDetector"/> made of the whole text.</summary>
    public TextLanguage DetectedLanguage { get; }

    /// <summary>
    /// Prepares a text: normalizes its line breaks, guesses its language from the whole text, and cuts it to
    /// <paramref name="maxChars"/> (at a line break when one is near, never inside a surrogate pair).
    /// </summary>
    /// <param name="text">The text as stored.</param>
    /// <param name="maxChars">The most characters to keep (the peek shows a part of a huge text; the viewer all of it).</param>
    /// <param name="cancellationToken">Stops a long run.</param>
    /// <returns>The prepared source.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static TextViewSource Create(string text, int maxChars = int.MaxValue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = Normalize(text);
        cancellationToken.ThrowIfCancellationRequested();
        int lines = 1 + normalized.AsSpan().Count('\n');
        var detected = TextLanguageDetector.Detect(normalized);
        cancellationToken.ThrowIfCancellationRequested();
        return new TextViewSource(Cut(normalized, Math.Max(1, maxChars)), normalized.Length, lines, detected);
    }

    /// <summary>
    /// The text re-indented as a language (<see cref="StructuredTextFormatter"/>), worked out once per language.
    /// </summary>
    /// <param name="language">The language.</param>
    /// <param name="cancellationToken">Stops a long run (nothing is remembered then).</param>
    /// <returns>The re-indented text, or <see langword="null"/> when the language cannot be re-indented or the text is not valid or already tidy.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public string? Reindented(TextLanguage language, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (reindented.TryGetValue(language, out var known))
            {
                return known;
            }
        }

        // A cut text is never re-indented: its structure is not whole, so it would not validate anyway.
        string? result = !IsCut && TextLanguages.CanReindent(language) && StructuredTextFormatter.TryFormat(Text, language, out var formatted, cancellationToken)
            ? formatted
            : null;
        lock (gate)
        {
            reindented[language] = result;
        }

        return result;
    }

    /// <summary>
    /// Normalizes line breaks to <c>\n</c> (CRLF, CR, U+2028, U+2029) and drops a byte order mark at the start; every
    /// other character stays.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The normalized text (the same instance when nothing changed).</returns>
    internal static string Normalize(string text)
    {
        var span = text.AsSpan();
        int bom = span.Length > 0 && span[0] == '\uFEFF' ? 1 : 0;
        if (bom == 0 && span.IndexOfAny('\r', '\u2028', '\u2029') < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (int i = bom; i < span.Length; i++)
        {
            char c = span[i];
            if (c == '\r')
            {
                builder.Append('\n');
                if (i + 1 < span.Length && span[i + 1] == '\n')
                {
                    i++;
                }
            }
            else
            {
                builder.Append(c is '\u2028' or '\u2029' ? '\n' : c);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Cuts a text to at most <paramref name="maxChars"/> characters: at the last line break of the final tenth when
    /// there is one (a peek then ends with a whole line), never between the halves of a surrogate pair.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="maxChars">The limit (positive).</param>
    /// <returns>The text, or its start.</returns>
    internal static string Cut(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        int end = maxChars;
        int lineBreak = text.LastIndexOf('\n', end - 1, Math.Max(1, end / 10));
        if (lineBreak > 0)
        {
            end = lineBreak;
        }
        else if (char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        return text[..end];
    }
}

/// <summary>
/// One view of a <see cref="TextViewSource"/>: as one language, formatted or as copied. Says what the viewer draws (a
/// Markdown document or a code document), holds the tokens or blocks, and writes the RTF (<see cref="ToRtf(SyntaxPalette, TextViewStyle, CancellationToken)"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Formatted and as copied</b> (the owner's switch, 2026-11-13). Formatted draws Markdown as a document and shows XML and
/// JSON re-indented; as copied shows the text exactly as it is — Markdown as its source, XML and JSON with their own line
/// breaks — colored for code. For other languages both are the same, and <see cref="HasFormattedAlternative"/> says so,
/// so the switch can hide.
/// </para>
/// <para>
/// <b>Size limits.</b> Markdown is drawn up to <see cref="MarkdownMaxChars"/>; a longer text is shown as plain text. Code is
/// colored up to <see cref="SyntaxHighlighter.DefaultMaxChars"/>; past that, in the default color.
/// <see cref="IsLimited"/> tells the viewer to say so.
/// </para>
/// <para>
/// <b>Pages.</b> RichEdit takes a long text slowly, on the UI thread: measured in the QA (2026-11-13, a test desktop
/// without a GPU), about 2.5 µs per character as plain text and 7 µs as RTF, so a 6 MB text froze the viewer for 15 s. A
/// code document is therefore cut into <see cref="Pages"/> of at most <see cref="PageMaxChars"/> characters, at line
/// breaks, and RichEdit gets one at a time (<see cref="ToRtf(TextViewPage, SyntaxPalette, TextViewStyle, CancellationToken)"/>,
/// <see cref="PageText"/>). A Markdown document is one page: it is drawn whole, which is why its limit is low.
/// </para>
/// </remarks>
public sealed class TextViewRendering
{
    /// <summary>
    /// The longest text drawn as a Markdown document; a longer one is shown as plain text, in pages. A document is drawn whole
    /// (its blocks do not split into pages), and 100,000 characters took 0.74 s to show in the QA desktop.
    /// </summary>
    public const int MarkdownMaxChars = 100_000;

    /// <summary>
    /// The most characters of a code document that RichEdit gets at once (one page, about 0.45 s as RTF and 0.16 s as plain
    /// text in the QA desktop; a fast PC takes about a third of that). The peek shows at most one page.
    /// </summary>
    public const int PageMaxChars = 64_000;

    /// <summary>The longest code block inside Markdown whose language is guessed when its fence names none.</summary>
    private const int FenceGuessMaxChars = 200_000;

    /// <summary>The tokens of each code block of a Markdown document, by block.</summary>
    private readonly Dictionary<MarkdownCodeBlock, IReadOnlyList<SyntaxToken>> blockTokens = new(ReferenceEqualityComparer.Instance);

    /// <summary>Creates a view (use <see cref="Create"/>).</summary>
    private TextViewRendering()
    {
    }

    /// <summary>The language the view shows the text as.</summary>
    public TextLanguage Language { get; private init; }

    /// <summary>Whether the view is the formatted one (the switch's state), as opposed to as copied.</summary>
    public bool Formatted { get; private init; }

    /// <summary>Whether the view is a drawn Markdown document (no line numbers, prose font); otherwise a code document.</summary>
    public bool IsDocument { get; private init; }

    /// <summary>Whether the text is shown re-indented (XML or JSON, formatted).</summary>
    public bool IsReindented { get; private init; }

    /// <summary>
    /// Whether formatted and as copied differ for this language and text, so the viewer offers the switch: Markdown that is
    /// small enough to draw, or XML and JSON that re-indent.
    /// </summary>
    public bool HasFormattedAlternative { get; private init; }

    /// <summary>The text of a code document, as RichEdit will hold it (re-indented when <see cref="IsReindented"/>); the source text for a document.</summary>
    public string DisplayText { get; private init; } = string.Empty;

    /// <summary>The code document's tokens (empty for plain text and documents).</summary>
    public IReadOnlyList<SyntaxToken> Tokens { get; private init; } = [];

    /// <summary>The Markdown document's blocks (empty for a code document).</summary>
    public IReadOnlyList<MarkdownBlock> Blocks { get; private init; } = [];

    /// <summary>Where each line of <see cref="DisplayText"/> starts (code documents; empty for a Markdown document): the gutter's positions.</summary>
    public IReadOnlyList<int> LineStarts { get; private init; } = [];

    /// <summary>Whether size limits left part of the view uncolored or undrawn (see the class remarks).</summary>
    public bool IsLimited { get; private init; }

    /// <summary>
    /// The view's pages, in order, at least one: a Markdown document and a short text are one page; a long code document is
    /// cut at line breaks into pages of at most <see cref="PageMaxChars"/> characters (class remarks).
    /// </summary>
    public IReadOnlyList<TextViewPage> Pages { get; private init; } = [];

    /// <summary>
    /// Makes a view of a source.
    /// </summary>
    /// <param name="source">The prepared text.</param>
    /// <param name="language">The language to show it as (the detected one, or the user's pick).</param>
    /// <param name="formatted">Formatted (drawn, re-indented) rather than as copied.</param>
    /// <param name="cancellationToken">Stops a long run.</param>
    /// <returns>The view.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static TextViewRendering Create(TextViewSource source, TextLanguage language, bool formatted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        string text = source.Text;
        if (language == TextLanguage.Markdown)
        {
            bool drawable = text.Length <= MarkdownMaxChars;
            if (formatted && drawable)
            {
                var blocks = MarkdownParser.Parse(text, new MarkdownParseOptions(KeepLineBreaks: true, MaxLength: MarkdownMaxChars));
                var view = new TextViewRendering
                {
                    Language = language,
                    Formatted = true,
                    IsDocument = true,
                    HasFormattedAlternative = true,
                    DisplayText = text,
                    Blocks = blocks,
                    Pages = [new TextViewPage(0, 0, text.Length, 0)],
                };
                view.ColorCodeBlocks(cancellationToken);
                return view;
            }

            return CodeView(text, TextLanguage.PlainText, language, formatted, hasAlternative: drawable, isLimited: !drawable, reindented: false, cancellationToken);
        }

        string? reindentedText = TextLanguages.CanReindent(language) ? source.Reindented(language, cancellationToken) : null;
        bool reindent = formatted && reindentedText is not null;
        return CodeView(reindent ? reindentedText! : text, language, language, formatted, hasAlternative: reindentedText is not null, isLimited: false, reindent, cancellationToken);
    }

    /// <summary>Makes a code document view: its tokens and line starts.</summary>
    /// <param name="text">The text to show.</param>
    /// <param name="colorAs">The language its colors come from (<see cref="TextLanguage.PlainText"/> for none).</param>
    /// <param name="language">The language the view stands for (shown in the chip).</param>
    /// <param name="formatted">The switch's state.</param>
    /// <param name="hasAlternative">Whether the other state of the switch shows something else.</param>
    /// <param name="isLimited">Whether a size limit applied already.</param>
    /// <param name="reindented">Whether <paramref name="text"/> is re-indented.</param>
    /// <param name="cancellationToken">Stops a long run.</param>
    /// <returns>The view.</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    private static TextViewRendering CodeView(string text, TextLanguage colorAs, TextLanguage language, bool formatted, bool hasAlternative, bool isLimited, bool reindented, CancellationToken cancellationToken)
    {
        var tokens = SyntaxHighlighter.Tokenize(text, colorAs, SyntaxHighlighter.DefaultMaxChars, cancellationToken);
        var lineStarts = LineStartsOf(text);
        return new TextViewRendering
        {
            Language = language,
            Formatted = formatted,
            IsDocument = false,
            IsReindented = reindented,
            HasFormattedAlternative = hasAlternative,
            DisplayText = text,
            Tokens = tokens,
            LineStarts = lineStarts,
            Pages = PagesOf(text, lineStarts, PageMaxChars),
            IsLimited = isLimited || (colorAs != TextLanguage.PlainText && text.Length > SyntaxHighlighter.DefaultMaxChars),
        };
    }

    /// <summary>
    /// Colors the code blocks of a Markdown document: as the language their fence names, else as the language guessed from
    /// their code (none when the guess is Markdown, or the block is too long to guess).
    /// </summary>
    /// <param name="cancellationToken">Stops a long run.</param>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    private void ColorCodeBlocks(CancellationToken cancellationToken)
    {
        foreach (var block in Blocks.OfType<MarkdownCodeBlock>())
        {
            var language = TextLanguages.FromFenceInfo(block.Info)
                ?? (block.Code.Length <= FenceGuessMaxChars ? TextLanguageDetector.Detect(block.Code) : TextLanguage.PlainText);
            blockTokens[block] = language is TextLanguage.Markdown or TextLanguage.PlainText
                ? []
                : SyntaxHighlighter.Tokenize(block.Code, language, SyntaxHighlighter.DefaultMaxChars, cancellationToken);
        }
    }

    /// <summary>The tokens of a code block of this document (empty when it is not one of its blocks).</summary>
    /// <param name="block">The block.</param>
    /// <returns>Its tokens.</returns>
    public IReadOnlyList<SyntaxToken> TokensOf(MarkdownCodeBlock block) => blockTokens.GetValueOrDefault(block) ?? [];

    /// <summary>
    /// Writes the view as an RTF document for RichEdit, in a theme's colors and a style's fonts and sizes.
    /// </summary>
    /// <param name="palette">The theme's colors.</param>
    /// <param name="style">Fonts and sizes (already scaled to the user's text size).</param>
    /// <param name="cancellationToken">Stops a long run.</param>
    /// <returns>The RTF document.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public string ToRtf(SyntaxPalette palette, TextViewStyle style, CancellationToken cancellationToken = default) =>
        IsDocument
            ? RtfWriter.Markdown(Blocks, TokensOf, palette, style, cancellationToken)
            : RtfWriter.Code(DisplayText, Tokens, palette, style, cancellationToken);

    /// <summary>
    /// Writes one page of the view as an RTF document for RichEdit: a Markdown document whole (its only page), a page of a
    /// code document with its share of the tokens.
    /// </summary>
    /// <param name="page">One of <see cref="Pages"/>.</param>
    /// <param name="palette">The theme's colors.</param>
    /// <param name="style">Fonts and sizes (already scaled to the user's text size).</param>
    /// <param name="cancellationToken">Stops a long run.</param>
    /// <returns>The RTF document.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> lies outside the text.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public string ToRtf(TextViewPage page, SyntaxPalette palette, TextViewStyle style, CancellationToken cancellationToken = default) =>
        IsDocument
            ? RtfWriter.Markdown(Blocks, TokensOf, palette, style, cancellationToken)
            : RtfWriter.Code(PageText(page), PageTokens(page), palette, style, cancellationToken);

    /// <summary>A page's text (the whole text for a document or a single page).</summary>
    /// <param name="page">One of <see cref="Pages"/>.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> lies outside the text.</exception>
    public string PageText(TextViewPage page) =>
        page.Start == 0 && page.Length == DisplayText.Length ? DisplayText : DisplayText.Substring(page.Start, page.Length);

    /// <summary>
    /// A page's share of the code document's tokens, moved to the page's own positions; a token across the page's edge is
    /// cut at it (a string or comment that runs on keeps its color on the next page).
    /// </summary>
    /// <param name="page">One of <see cref="Pages"/>.</param>
    /// <returns>The tokens, in order.</returns>
    public IReadOnlyList<SyntaxToken> PageTokens(TextViewPage page)
    {
        if (Tokens.Count == 0 || (page.Start == 0 && page.Length == DisplayText.Length))
        {
            return Tokens;
        }

        // The first token that ends past the page's start (tokens are in order and do not overlap).
        int low = 0;
        int high = Tokens.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (Tokens[middle].End <= page.Start)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        var tokens = new List<SyntaxToken>();
        for (int i = low; i < Tokens.Count && Tokens[i].Start < page.End; i++)
        {
            int start = Math.Max(Tokens[i].Start, page.Start);
            int end = Math.Min(Tokens[i].End, page.End);
            if (end > start)
            {
                tokens.Add(new SyntaxToken(start - page.Start, end - start, Tokens[i].Kind));
            }
        }

        return tokens;
    }

    /// <summary>The index of the page holding a character position (the last page for a position at or past the end).</summary>
    /// <param name="position">A position in <see cref="DisplayText"/>.</param>
    /// <returns>The page's index in <see cref="Pages"/>.</returns>
    public int PageOf(int position)
    {
        int low = 0;
        int high = Pages.Count - 1;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (Pages[middle].Start <= position)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return Math.Max(0, low);
    }

    /// <summary>The index of the line holding a character position: the last line start at or before it.</summary>
    /// <param name="lineStarts">The line starts, in order, the first being 0.</param>
    /// <param name="position">The position.</param>
    /// <returns>The line's index (0 for a position before the first start).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lineStarts"/> is <see langword="null"/>.</exception>
    public static int LineOf(IReadOnlyList<int> lineStarts, int position)
    {
        ArgumentNullException.ThrowIfNull(lineStarts);
        int low = 0;
        int high = lineStarts.Count - 1;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (lineStarts[middle] <= position)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return Math.Max(0, low);
    }

    /// <summary>
    /// Cuts a text into pages of at most <paramref name="maxChars"/> characters, each ending after a line break when its
    /// window holds one; a single line longer than a page is cut at the limit, never inside a surrogate pair.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="lineStarts">Its line starts (<see cref="LineStartsOf"/>), for each page's first line.</param>
    /// <param name="maxChars">The most characters on a page (at least 2).</param>
    /// <returns>The pages, in order; one empty page for an empty text.</returns>
    internal static TextViewPage[] PagesOf(string text, IReadOnlyList<int> lineStarts, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return [new TextViewPage(0, 0, text.Length, 0)];
        }

        var pages = new List<TextViewPage>((text.Length / maxChars) + 1);
        int start = 0;
        while (start < text.Length)
        {
            int limit = Math.Min(text.Length, start + maxChars);
            int end = limit;
            if (limit < text.Length)
            {
                int lastBreak = text.LastIndexOf('\n', limit - 1, limit - start);
                end = lastBreak >= start ? lastBreak + 1 : limit;
                if (end == limit && char.IsHighSurrogate(text[end - 1]))
                {
                    end--;
                }
            }

            pages.Add(new TextViewPage(pages.Count, start, end - start, LineOf(lineStarts, start)));
            start = end;
        }

        return [.. pages];
    }

    /// <summary>Where each line of a text starts: 0, then one past every <c>\n</c>.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The starts, in order.</returns>
    internal static int[] LineStartsOf(string text)
    {
        var starts = new int[1 + text.AsSpan().Count('\n')];
        int line = 1;
        int index = -1;
        while ((index = text.IndexOf('\n', index + 1)) >= 0)
        {
            starts[line++] = index + 1;
        }

        return starts;
    }
}

/// <summary>
/// One page of a <see cref="TextViewRendering"/>: the part of its text RichEdit gets at a time (the rendering's class remarks).
/// </summary>
/// <param name="Index">The page's index in <see cref="TextViewRendering.Pages"/>.</param>
/// <param name="Start">Where the page starts in <see cref="TextViewRendering.DisplayText"/>.</param>
/// <param name="Length">How many characters it holds.</param>
/// <param name="FirstLine">The index of the line holding its first character (its first line number, less one).</param>
public readonly record struct TextViewPage(int Index, int Start, int Length, int FirstLine)
{
    /// <summary>Where the page ends: one past its last character.</summary>
    public int End => Start + Length;
}
