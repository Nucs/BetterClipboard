namespace BetterClipboard.Core.TextView;

/// <summary>
/// What a text is, as far as the text viewer is concerned: Markdown (drawn), plain text (as it is), or a language whose
/// syntax is colored. The viewer's language picker lists them in <see cref="TextLanguages.All"/>'s order.
/// </summary>
/// <remarks>
/// <see cref="Markdown"/> is the default and the fallback for anything not recognized as code (the owner's choice,
/// 2026-11-13): prose renders well as Markdown, and Markdown's own marks become styles. Values are never stored, so the
/// order and the numbers may change freely; a name is what a person sees (<see cref="TextLanguages.NameOf"/>).
/// </remarks>
public enum TextLanguage
{
    /// <summary>Prose or Markdown: headings, lists, emphasis, code blocks and tables are drawn; line breaks are kept.</summary>
    Markdown = 0,

    /// <summary>The text exactly as it is, in the code font, without colors.</summary>
    PlainText,

    /// <summary>XML and its dialects (XAML, SVG, csproj, plist, custom tags such as an agent's &lt;heartbeat&gt;).</summary>
    Xml,

    /// <summary>HTML pages and fragments.</summary>
    Html,

    /// <summary>JSON, also with comments and trailing commas (JSONC).</summary>
    Json,

    /// <summary>YAML.</summary>
    Yaml,

    /// <summary>INI, TOML, .properties and .editorconfig files: sections and key/value lines.</summary>
    Ini,

    /// <summary>C#.</summary>
    CSharp,

    /// <summary>Java.</summary>
    Java,

    /// <summary>Kotlin.</summary>
    Kotlin,

    /// <summary>JavaScript, also JSX.</summary>
    JavaScript,

    /// <summary>TypeScript, also TSX.</summary>
    TypeScript,

    /// <summary>Python.</summary>
    Python,

    /// <summary>Ruby.</summary>
    Ruby,

    /// <summary>PHP.</summary>
    Php,

    /// <summary>Go.</summary>
    Go,

    /// <summary>Rust.</summary>
    Rust,

    /// <summary>C and C++ (and their headers).</summary>
    Cpp,

    /// <summary>PowerShell (Windows PowerShell and PowerShell 7).</summary>
    PowerShell,

    /// <summary>Unix shells: sh, Bash, Zsh, and command lines typed into them.</summary>
    Shell,

    /// <summary>Windows batch files and Command Prompt commands.</summary>
    Batch,

    /// <summary>SQL.</summary>
    Sql,

    /// <summary>CSS, also SCSS and Less.</summary>
    Css,

    /// <summary>A unified diff or patch: added, removed and context lines.</summary>
    Diff,
}

/// <summary>
/// Facts about <see cref="TextLanguage"/> values: their names, their order in the picker, which of them are code, which
/// can be re-indented, and the language a Markdown code fence names (<c>```csharp</c>).
/// </summary>
public static class TextLanguages
{
    /// <summary>
    /// Every language in the order the viewer's picker lists them: Markdown and plain text first (the two ways to read
    /// prose), then the data formats, then programming and shell languages roughly by how often they are copied.
    /// </summary>
    public static IReadOnlyList<TextLanguage> All { get; } =
    [
        TextLanguage.Markdown, TextLanguage.PlainText,
        TextLanguage.Xml, TextLanguage.Html, TextLanguage.Json, TextLanguage.Yaml, TextLanguage.Ini,
        TextLanguage.CSharp, TextLanguage.JavaScript, TextLanguage.TypeScript, TextLanguage.Python,
        TextLanguage.PowerShell, TextLanguage.Shell, TextLanguage.Batch, TextLanguage.Sql,
        TextLanguage.Java, TextLanguage.Kotlin, TextLanguage.Go, TextLanguage.Rust, TextLanguage.Cpp,
        TextLanguage.Ruby, TextLanguage.Php, TextLanguage.Css, TextLanguage.Diff,
    ];

    /// <summary>
    /// The fence words (lower case) that name each language, as people write them after <c>```</c> and as GitHub knows
    /// them. A fence word that is missing here leaves the block to <see cref="TextLanguageDetector"/>.
    /// </summary>
    private static readonly Dictionary<string, TextLanguage> FenceWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["md"] = TextLanguage.Markdown, ["markdown"] = TextLanguage.Markdown,
        ["text"] = TextLanguage.PlainText, ["txt"] = TextLanguage.PlainText, ["plain"] = TextLanguage.PlainText,
        ["plaintext"] = TextLanguage.PlainText, ["console"] = TextLanguage.PlainText, ["output"] = TextLanguage.PlainText,
        ["xml"] = TextLanguage.Xml, ["xaml"] = TextLanguage.Xml, ["svg"] = TextLanguage.Xml, ["csproj"] = TextLanguage.Xml,
        ["xsd"] = TextLanguage.Xml, ["xsl"] = TextLanguage.Xml, ["xslt"] = TextLanguage.Xml, ["plist"] = TextLanguage.Xml,
        ["html"] = TextLanguage.Html, ["htm"] = TextLanguage.Html, ["xhtml"] = TextLanguage.Html, ["vue"] = TextLanguage.Html,
        ["json"] = TextLanguage.Json, ["jsonc"] = TextLanguage.Json, ["json5"] = TextLanguage.Json, ["jsonl"] = TextLanguage.Json,
        ["yaml"] = TextLanguage.Yaml, ["yml"] = TextLanguage.Yaml,
        ["ini"] = TextLanguage.Ini, ["toml"] = TextLanguage.Ini, ["cfg"] = TextLanguage.Ini, ["conf"] = TextLanguage.Ini,
        ["properties"] = TextLanguage.Ini, ["editorconfig"] = TextLanguage.Ini, ["gitconfig"] = TextLanguage.Ini,
        ["cs"] = TextLanguage.CSharp, ["csharp"] = TextLanguage.CSharp, ["c#"] = TextLanguage.CSharp,
        ["java"] = TextLanguage.Java,
        ["kotlin"] = TextLanguage.Kotlin, ["kt"] = TextLanguage.Kotlin, ["kts"] = TextLanguage.Kotlin,
        ["js"] = TextLanguage.JavaScript, ["javascript"] = TextLanguage.JavaScript, ["jsx"] = TextLanguage.JavaScript,
        ["mjs"] = TextLanguage.JavaScript, ["cjs"] = TextLanguage.JavaScript, ["node"] = TextLanguage.JavaScript,
        ["ts"] = TextLanguage.TypeScript, ["typescript"] = TextLanguage.TypeScript, ["tsx"] = TextLanguage.TypeScript,
        ["py"] = TextLanguage.Python, ["python"] = TextLanguage.Python, ["python3"] = TextLanguage.Python, ["py3"] = TextLanguage.Python,
        ["rb"] = TextLanguage.Ruby, ["ruby"] = TextLanguage.Ruby,
        ["php"] = TextLanguage.Php,
        ["go"] = TextLanguage.Go, ["golang"] = TextLanguage.Go,
        ["rs"] = TextLanguage.Rust, ["rust"] = TextLanguage.Rust,
        ["c"] = TextLanguage.Cpp, ["h"] = TextLanguage.Cpp, ["cpp"] = TextLanguage.Cpp, ["c++"] = TextLanguage.Cpp,
        ["cc"] = TextLanguage.Cpp, ["cxx"] = TextLanguage.Cpp, ["hpp"] = TextLanguage.Cpp, ["objc"] = TextLanguage.Cpp,
        ["ps1"] = TextLanguage.PowerShell, ["powershell"] = TextLanguage.PowerShell, ["pwsh"] = TextLanguage.PowerShell,
        ["psm1"] = TextLanguage.PowerShell, ["ps"] = TextLanguage.PowerShell,
        ["sh"] = TextLanguage.Shell, ["bash"] = TextLanguage.Shell, ["shell"] = TextLanguage.Shell, ["zsh"] = TextLanguage.Shell,
        ["shellscript"] = TextLanguage.Shell, ["fish"] = TextLanguage.Shell, ["ksh"] = TextLanguage.Shell,
        ["bat"] = TextLanguage.Batch, ["batch"] = TextLanguage.Batch, ["cmd"] = TextLanguage.Batch, ["dos"] = TextLanguage.Batch,
        ["sql"] = TextLanguage.Sql, ["mysql"] = TextLanguage.Sql, ["postgres"] = TextLanguage.Sql, ["postgresql"] = TextLanguage.Sql,
        ["sqlite"] = TextLanguage.Sql, ["tsql"] = TextLanguage.Sql, ["plsql"] = TextLanguage.Sql,
        ["css"] = TextLanguage.Css, ["scss"] = TextLanguage.Css, ["sass"] = TextLanguage.Css, ["less"] = TextLanguage.Css,
        ["diff"] = TextLanguage.Diff, ["patch"] = TextLanguage.Diff, ["udiff"] = TextLanguage.Diff,
    };

    /// <summary>
    /// The name a person sees for a language: the product's own spelling ("C#", "JavaScript", "C / C++"), which stays
    /// English in every interface language like the other product names; only <see cref="TextLanguage.PlainText"/> is a
    /// phrase, and the App translates that one.
    /// </summary>
    /// <param name="language">The language.</param>
    /// <returns>Its name; the enum member's name for a value this version does not know.</returns>
    public static string NameOf(TextLanguage language) => language switch
    {
        TextLanguage.Markdown => "Markdown",
        TextLanguage.PlainText => "Plain text",
        TextLanguage.Xml => "XML",
        TextLanguage.Html => "HTML",
        TextLanguage.Json => "JSON",
        TextLanguage.Yaml => "YAML",
        TextLanguage.Ini => "INI / TOML",
        TextLanguage.CSharp => "C#",
        TextLanguage.Java => "Java",
        TextLanguage.Kotlin => "Kotlin",
        TextLanguage.JavaScript => "JavaScript",
        TextLanguage.TypeScript => "TypeScript",
        TextLanguage.Python => "Python",
        TextLanguage.Ruby => "Ruby",
        TextLanguage.Php => "PHP",
        TextLanguage.Go => "Go",
        TextLanguage.Rust => "Rust",
        TextLanguage.Cpp => "C / C++",
        TextLanguage.PowerShell => "PowerShell",
        TextLanguage.Shell => "Shell",
        TextLanguage.Batch => "Batch",
        TextLanguage.Sql => "SQL",
        TextLanguage.Css => "CSS",
        TextLanguage.Diff => "Diff",
        _ => language.ToString(),
    };

    /// <summary>
    /// Whether a language is shown as code: in the code font, line by line, with line numbers when they are on. Markdown is
    /// drawn as a document instead, and plain text is shown as code without colors.
    /// </summary>
    /// <param name="language">The language.</param>
    /// <returns><see langword="false"/> only for <see cref="TextLanguage.Markdown"/>.</returns>
    public static bool IsCode(TextLanguage language) => language != TextLanguage.Markdown;

    /// <summary>
    /// Whether the viewer can re-indent text of this language (<see cref="StructuredTextFormatter"/>): only XML and JSON,
    /// whose structure can be checked exactly. Re-indenting code would need a parser per language and could change what a
    /// person reads; the owner chose re-indenting for XML and JSON only (2026-11-13).
    /// </summary>
    /// <param name="language">The language.</param>
    /// <returns><see langword="true"/> for XML and JSON.</returns>
    public static bool CanReindent(TextLanguage language) => language is TextLanguage.Xml or TextLanguage.Json;

    /// <summary>
    /// The language a Markdown code fence's info string names (<c>```csharp</c>, <c>``` js title="x"</c>): its first word,
    /// matched without regard to case.
    /// </summary>
    /// <param name="info">The text after the fence's backticks or tildes; may be <see langword="null"/> or blank.</param>
    /// <returns>The language, or <see langword="null"/> when there is no word or it names no known language.</returns>
    public static TextLanguage? FromFenceInfo(string? info)
    {
        if (string.IsNullOrWhiteSpace(info))
        {
            return null;
        }

        // The first word only: "js title=app.js", "python {linenos}" and "{.python}" all name their language first.
        var word = info.Trim().TrimStart('{', '.').Split([' ', '\t', ',', '{', '}'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return word is not null && FenceWords.TryGetValue(word, out var language) ? language : null;
    }
}
