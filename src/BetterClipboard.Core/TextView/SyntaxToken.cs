namespace BetterClipboard.Core.TextView;

/// <summary>
/// What a colored piece of code is. The palette (<see cref="SyntaxPalette"/>) gives each kind its color per theme; text
/// between tokens is <see cref="Text"/> and gets the default color.
/// </summary>
public enum TokenKind
{
    /// <summary>Anything else: identifiers, operators, prose inside markup. Drawn in the default color.</summary>
    Text = 0,

    /// <summary>A comment (<c>// x</c>, <c>/* x */</c>, <c># x</c>, <c>&lt;!-- x --&gt;</c>, <c>REM x</c>).</summary>
    Comment,

    /// <summary>A keyword of the language (<c>if</c>, <c>class</c>, <c>SELECT</c>, <c>function</c>).</summary>
    Keyword,

    /// <summary>A type name: a built-in type (<c>int</c>, <c>string</c>) or, where the language spells them so, a PascalCase name.</summary>
    Type,

    /// <summary>A function or command being called or defined (<c>Print(</c>, <c>Get-ChildItem</c>, the first word of a shell command).</summary>
    Function,

    /// <summary>A string or character literal, including an XML attribute's value.</summary>
    String,

    /// <summary>A number literal (<c>42</c>, <c>0x1F</c>, <c>3.5e-2</c>, <c>12px</c>).</summary>
    Number,

    /// <summary>A literal word: <c>true</c>, <c>false</c>, <c>null</c>, <c>None</c>, <c>nil</c>.</summary>
    Literal,

    /// <summary>Markup and structure punctuation: <c>&lt; &gt; /</c> around tags, JSON's braces, commas and colons.</summary>
    Punctuation,

    /// <summary>A preprocessor line, an annotation or an attribute (<c>#include</c>, <c>#region</c>, <c>@Override</c>, <c>#[derive]</c>).</summary>
    Preprocessor,

    /// <summary>A variable spelled with a sigil (<c>$name</c>, <c>%PATH%</c>, <c>${x}</c>, <c>@field</c>).</summary>
    Variable,

    /// <summary>An element name of XML or HTML, or a CSS selector.</summary>
    Tag,

    /// <summary>An attribute name in markup, a PowerShell parameter (<c>-Path</c>), a CSS property.</summary>
    AttributeName,

    /// <summary>A key: a JSON property name, a YAML or INI key.</summary>
    Key,

    /// <summary>A character or entity reference in markup (<c>&amp;amp;</c>), an escape in a string.</summary>
    Entity,

    /// <summary>A section or document header: an INI <c>[section]</c>, a diff's <c>@@ … @@</c> or file line.</summary>
    Heading,

    /// <summary>A line a diff adds (<c>+ …</c>).</summary>
    Inserted,

    /// <summary>A line a diff removes (<c>- …</c>).</summary>
    Deleted,
}

/// <summary>
/// One colored piece of a text: where it starts, how long it is (UTF-16 code units of the text it was made from) and what
/// it is. Tokens of one text never overlap and come in text order.
/// </summary>
/// <param name="Start">Index of the first character.</param>
/// <param name="Length">Number of characters; always positive.</param>
/// <param name="Kind">What it is, which decides its color.</param>
public readonly record struct SyntaxToken(int Start, int Length, TokenKind Kind)
{
    /// <summary>The index just past the token.</summary>
    public int End => Start + Length;
}
