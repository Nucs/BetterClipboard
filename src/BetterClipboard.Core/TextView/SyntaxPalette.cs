namespace BetterClipboard.Core.TextView;

/// <summary>
/// The colors of the text viewer for one theme: one per <see cref="TokenKind"/>, plus the document colors Markdown uses.
/// Colors are <c>0xRRGGBB</c>.
/// </summary>
/// <remarks>
/// The token colors are VS Code's default themes (Dark+ and Light+), the colors most people who copy code already read it
/// in; the document colors follow Windows' own text colors so prose looks like the rest of the panel.
/// </remarks>
/// <param name="Text">Code and plain text without a token kind.</param>
/// <param name="Comment">Comments.</param>
/// <param name="Keyword">Keywords and literals spelled as words in markup (<see cref="TokenKind.Keyword"/>, <see cref="TokenKind.Literal"/>).</param>
/// <param name="Type">Type names.</param>
/// <param name="Function">Functions, commands and macros.</param>
/// <param name="String">Strings and attribute values.</param>
/// <param name="Number">Numbers.</param>
/// <param name="Punctuation">Markup brackets and structure punctuation.</param>
/// <param name="Preprocessor">Preprocessor lines, annotations and declarations.</param>
/// <param name="Variable">Variables, keys and attribute names.</param>
/// <param name="Tag">Element names and selectors.</param>
/// <param name="Entity">Entity references and section headers.</param>
/// <param name="Inserted">A diff's added lines.</param>
/// <param name="Deleted">A diff's removed lines.</param>
/// <param name="Prose">Markdown body text.</param>
/// <param name="Secondary">Quotes, rules and other quieter Markdown parts.</param>
/// <param name="Link">Links in Markdown.</param>
/// <param name="CodeBackground">The background behind code inside Markdown (code spans and fenced blocks).</param>
public sealed record SyntaxPalette(
    uint Text,
    uint Comment,
    uint Keyword,
    uint Type,
    uint Function,
    uint String,
    uint Number,
    uint Punctuation,
    uint Preprocessor,
    uint Variable,
    uint Tag,
    uint Entity,
    uint Inserted,
    uint Deleted,
    uint Prose,
    uint Secondary,
    uint Link,
    uint CodeBackground)
{
    /// <summary>The dark theme's colors (VS Code Dark+ for code; Windows' dark text colors for prose).</summary>
    public static SyntaxPalette Dark { get; } = new(
        Text: 0xD4D4D4,
        Comment: 0x6A9955,
        Keyword: 0x569CD6,
        Type: 0x4EC9B0,
        Function: 0xDCDCAA,
        String: 0xCE9178,
        Number: 0xB5CEA8,
        Punctuation: 0x808080,
        Preprocessor: 0xC586C0,
        Variable: 0x9CDCFE,
        Tag: 0x569CD6,
        Entity: 0xD7BA7D,
        Inserted: 0x89D185,
        Deleted: 0xF48771,
        Prose: 0xFFFFFF,
        Secondary: 0xB4B4B4,
        Link: 0x99EBFF,
        CodeBackground: 0x343434);

    /// <summary>The light theme's colors (VS Code Light+ for code; Windows' light text colors for prose).</summary>
    public static SyntaxPalette Light { get; } = new(
        Text: 0x1F1F1F,
        Comment: 0x008000,
        Keyword: 0x0000FF,
        Type: 0x267F99,
        Function: 0x795E26,
        String: 0xA31515,
        Number: 0x098658,
        Punctuation: 0x800000,
        Preprocessor: 0xAF00DB,
        Variable: 0x001080,
        Tag: 0x800000,
        Entity: 0xEE0000,
        Inserted: 0x116329,
        Deleted: 0xB31D28,
        Prose: 0x1A1A1A,
        Secondary: 0x5C5C5C,
        Link: 0x003E92,
        CodeBackground: 0xE8E8E8);

    /// <summary>The color a token of a kind is drawn in.</summary>
    /// <param name="kind">The token's kind.</param>
    /// <returns>Its color.</returns>
    public uint ColorOf(TokenKind kind) => kind switch
    {
        TokenKind.Comment => Comment,
        TokenKind.Keyword or TokenKind.Literal => Keyword,
        TokenKind.Type => Type,
        TokenKind.Function => Function,
        TokenKind.String => String,
        TokenKind.Number => Number,
        TokenKind.Punctuation => Punctuation,
        TokenKind.Preprocessor => Preprocessor,
        TokenKind.Variable or TokenKind.AttributeName or TokenKind.Key => Variable,
        TokenKind.Tag => Tag,
        TokenKind.Entity or TokenKind.Heading => Entity,
        TokenKind.Inserted => Inserted,
        TokenKind.Deleted => Deleted,
        _ => Text,
    };
}
