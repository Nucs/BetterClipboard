using BetterClipboard.Core.TextView;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for <see cref="SyntaxHighlighter"/>: each lexer colors its language's forms, and no input of any language makes a
/// lexer throw, overlap tokens or run past its limit.
/// </summary>
public sealed class SyntaxHighlighterTests
{
    /// <summary>Describes the tokens of a text as "Kind:text" strings, in order.</summary>
    /// <param name="text">The text.</param>
    /// <param name="language">Its language.</param>
    /// <returns>The descriptions.</returns>
    private static string[] Describe(string text, TextLanguage language) =>
        SyntaxHighlighter.Tokenize(text, language).Select(t => $"{t.Kind}:{text.Substring(t.Start, t.Length)}").ToArray();

    /// <summary>Whether a text's tokens include one of a kind with exactly this text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="language">Its language.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="piece">The token's text.</param>
    private static void AssertToken(string text, TextLanguage language, TokenKind kind, string piece) =>
        Assert.Contains($"{kind}:{piece}", Describe(text, language));

    /// <summary>XML: brackets, element names, attribute names and values, entities, comments, CDATA, declarations.</summary>
    [Fact]
    public void Xml_ColorsTagsAttributesAndTheRest()
    {
        Assert.Equal(
            ["Punctuation:<", "Tag:a", "AttributeName:b", "Punctuation:=", "String:\"c\"", "Punctuation:>", "Entity:&amp;", "Punctuation:</", "Tag:a", "Punctuation:>"],
            Describe("<a b=\"c\">x &amp; y</a>", TextLanguage.Xml));

        AssertToken("<!-- BC-TEST -->\n<x/>", TextLanguage.Xml, TokenKind.Comment, "<!-- BC-TEST -->");
        AssertToken("<?xml version=\"1.0\"?>", TextLanguage.Xml, TokenKind.Preprocessor, "<?xml version=\"1.0\"?>");
        AssertToken("<x> <![CDATA[a < b]]> </x>", TextLanguage.Xml, TokenKind.Punctuation, "<![CDATA[");

        // Neighbors of one kind join into one token: fewer runs for RichEdit to draw.
        AssertToken("<x><![CDATA[a]]></x>", TextLanguage.Xml, TokenKind.Punctuation, "><![CDATA[");

        // Text that only looks like a tag start stays text.
        Assert.Empty(Describe("a < b and c > d", TextLanguage.Xml));
    }

    /// <summary>HTML: unquoted values, and script bodies left alone (their "&lt;" is an operator, not a tag).</summary>
    [Fact]
    public void Html_UnquotedValuesAndRawScriptBodies()
    {
        AssertToken("<input type=checkbox checked>", TextLanguage.Html, TokenKind.String, "checkbox");
        var tokens = Describe("<script>if (a < b) { x(); }</script><p>y</p>", TextLanguage.Html);
        Assert.DoesNotContain("Tag:b", tokens);
        Assert.Contains("Tag:p", tokens);
    }

    /// <summary>JSON: keys, strings, numbers, literals, punctuation, and the comments of JSONC.</summary>
    [Fact]
    public void Json_KeysValuesAndComments()
    {
        var tokens = Describe("{ \"id\": 42, \"name\": \"BC\", \"ok\": true, \"x\": null, \"n\": -1.5e3 } // c", TextLanguage.Json);
        Assert.Contains("Key:\"id\"", tokens);
        Assert.Contains("String:\"BC\"", tokens);
        Assert.Contains("Number:42", tokens);
        Assert.Contains("Number:-1.5e3", tokens);
        Assert.Contains("Literal:true", tokens);
        Assert.Contains("Literal:null", tokens);
        Assert.Contains("Comment:// c", tokens);
    }

    /// <summary>C#: keywords, built-in types, PascalCase types, calls, strings of every kind, comments and preprocessor lines.</summary>
    [Fact]
    public void CSharp_Forms()
    {
        const string Code = "#region Setup\npublic sealed class Greeter\n{\n    // say hi\n    public void Greet(string name) => Console.WriteLine($\"Hi {name}\");\n" +
                            "    private const string Path = @\"C:\\a\"\"b\";\n    int x = 0x1F;\n}";
        var tokens = Describe(Code, TextLanguage.CSharp);
        Assert.Contains("Preprocessor:#region Setup", tokens);
        Assert.Contains("Keyword:public", tokens);
        Assert.Contains("Type:Greeter", tokens);
        Assert.Contains("Type:string", tokens);
        Assert.Contains("Comment:// say hi", tokens);
        Assert.Contains("Function:Greet", tokens);
        Assert.Contains("Function:WriteLine", tokens);
        Assert.Contains("String:$\"Hi {name}\"", tokens);
        Assert.Contains("String:@\"C:\\a\"\"b\"", tokens);
        Assert.Contains("Number:0x1F", tokens);
    }

    /// <summary>Python: definitions, decorators, prefixed and triple-quoted strings (over lines), literals and comments.</summary>
    [Fact]
    public void Python_Forms()
    {
        const string Code = "@dataclass\ndef run(self, path=None):\n    \"\"\"Docs\n    more\"\"\"\n    return f\"{path}\"  # done";
        var tokens = Describe(Code, TextLanguage.Python);
        Assert.Contains("Preprocessor:@dataclass", tokens);
        Assert.Contains("Keyword:def", tokens);
        Assert.Contains("Function:run", tokens);
        Assert.Contains("Literal:self", tokens);
        Assert.Contains("Literal:None", tokens);
        Assert.Contains("String:\"\"\"Docs\n    more\"\"\"", tokens);
        Assert.Contains("String:f\"{path}\"", tokens);
        Assert.Contains("Comment:# done", tokens);
    }

    /// <summary>PowerShell: Verb-Noun commands, parameters, operators, variables, literals, type names, here-strings, comments.</summary>
    [Fact]
    public void PowerShell_Forms()
    {
        const string Code = "<# block #>\nGet-ChildItem -Path $env:TEMP | Where-Object { $_.Length -gt 1MB -and $true }\n[string]$name = @\"\nhere\n\"@\n# end";
        var tokens = Describe(Code, TextLanguage.PowerShell);
        Assert.Contains("Comment:<# block #>", tokens);
        Assert.Contains("Function:Get-ChildItem", tokens);
        Assert.Contains("AttributeName:-Path", tokens);
        Assert.Contains("Variable:$env:TEMP", tokens);
        Assert.Contains("Function:Where-Object", tokens);
        Assert.Contains("Keyword:-gt", tokens);
        Assert.Contains("Keyword:-and", tokens);
        Assert.Contains("Literal:$true", tokens);
        Assert.Contains("Type:string", tokens);
        Assert.Contains("String:@\"\nhere\n\"@", tokens);
        Assert.Contains("Comment:# end", tokens);
    }

    /// <summary>Shell: commands at a command's start (also after sudo and pipes), variables, strings, and '#' only as a word start.</summary>
    [Fact]
    public void Shell_Forms()
    {
        var tokens = Describe("sudo apt-get install -y git | tee log # note\necho \"$HOME\" a#b ${X} $1", TextLanguage.Shell);
        Assert.Contains("Function:sudo", tokens);
        Assert.Contains("Function:apt-get", tokens);
        Assert.Contains("Function:tee", tokens);
        Assert.Contains("Comment:# note", tokens);
        Assert.Contains("Function:echo", tokens);
        Assert.Contains("String:\"$HOME\"", tokens);
        Assert.Contains("Variable:${X}", tokens);
        Assert.Contains("Variable:$1", tokens);
        Assert.DoesNotContain(tokens, t => t.StartsWith("Comment:#b", StringComparison.Ordinal));
        Assert.DoesNotContain("Function:install", tokens);
    }

    /// <summary>Batch: REM and :: comments, labels, every variable form, and keywords without regard to case.</summary>
    [Fact]
    public void Batch_Forms()
    {
        var tokens = Describe("@ECHO OFF\nREM BC-TEST\n:: note\nset NAME=%~dp0\nfor %%i in (*) do echo %%i !X!\n:end", TextLanguage.Batch);
        Assert.Contains("Keyword:ECHO", tokens);
        Assert.Contains("Comment:REM BC-TEST", tokens);
        Assert.Contains("Comment::: note", tokens);
        Assert.Contains("Variable:%~dp0", tokens);
        Assert.Contains("Variable:%%i", tokens);
        Assert.Contains("Variable:!X!", tokens);
        Assert.Contains("Function::end", tokens);
    }

    /// <summary>SQL: keywords in any case, strings with doubled quotes, comments, types and function calls.</summary>
    [Fact]
    public void Sql_Forms()
    {
        var tokens = Describe("select count(*) FROM t -- all\nWHERE name = 'O''Brien' AND id IN (1, 2)\n/* x */ CAST(a AS varchar)", TextLanguage.Sql);
        Assert.Contains("Keyword:select", tokens);
        Assert.Contains("Keyword:FROM", tokens);
        Assert.Contains("Function:count", tokens);
        Assert.Contains("Comment:-- all", tokens);
        Assert.Contains("String:'O''Brien'", tokens);
        Assert.Contains("Comment:/* x */", tokens);
        Assert.Contains("Type:varchar", tokens);
    }

    /// <summary>Rust: lifetimes are not character literals, macros include their "!", attributes are preprocessor.</summary>
    [Fact]
    public void Rust_Forms()
    {
        var tokens = Describe("#[derive(Debug)]\nfn f<'a>(x: &'a str) -> char { println!(\"{}\", x); 'c' }", TextLanguage.Rust);
        Assert.Contains("Preprocessor:#[derive(Debug)]", tokens);
        Assert.Contains("Type:'a", tokens);
        Assert.Contains("Function:println!", tokens);
        Assert.Contains("String:'c'", tokens);
    }

    /// <summary>YAML: keys, list dashes, comments, plain and quoted values, numbers and literals.</summary>
    [Fact]
    public void Yaml_Forms()
    {
        var tokens = Describe("---\nname: build # c\nsteps:\n  - uses: actions/checkout@v5\n    retries: 3\n    debug: true\n    title: \"x: y\"", TextLanguage.Yaml);
        Assert.Contains("Heading:---", tokens);
        Assert.Contains("Key:name", tokens);
        Assert.Contains("String:build", tokens);
        Assert.Contains("Comment:# c", tokens);
        Assert.Contains("Punctuation:-", tokens);
        Assert.Contains("Key:uses", tokens);
        Assert.Contains("Number:3", tokens);
        Assert.Contains("Literal:true", tokens);
        Assert.Contains("String:\"x: y\"", tokens);
    }

    /// <summary>INI and TOML: sections, keys, comments, and typed values.</summary>
    [Fact]
    public void Ini_Forms()
    {
        var tokens = Describe("; comment\n[core]\nautocrlf = true\nname = \"BC\"\nports = [80, 443]", TextLanguage.Ini);
        Assert.Contains("Comment:; comment", tokens);
        Assert.Contains("Heading:[core]", tokens);
        Assert.Contains("Key:autocrlf", tokens);
        Assert.Contains("Literal:true", tokens);
        Assert.Contains("String:\"BC\"", tokens);
        Assert.Contains("Number:443", tokens);
    }

    /// <summary>A diff's lines: headers, hunks, added and removed lines; context stays plain.</summary>
    [Fact]
    public void Diff_Lines() =>
        Assert.Equal(
            ["Heading:--- a/x", "Heading:+++ b/x", "Heading:@@ -1 +1 @@", "Deleted:-old", "Inserted:+new", "Comment:\\ No newline at end of file"],
            Describe("--- a/x\n+++ b/x\n@@ -1 +1 @@\n-old\n+new\n same\n\\ No newline at end of file", TextLanguage.Diff));

    /// <summary>CSS: selectors, properties, numbers with units, colors, at-rules and !important.</summary>
    [Fact]
    public void Css_Forms()
    {
        var tokens = Describe("@media (max-width: 600px) {\n  .card > a:hover {\n    color: #fff !important;\n    margin: 0 1.5rem;\n  }\n}", TextLanguage.Css);
        Assert.Contains("Keyword:@media", tokens);
        Assert.Contains("Tag:.card", tokens);
        Assert.Contains("Tag:a:hover", tokens);
        Assert.Contains("AttributeName:color", tokens);
        Assert.Contains("Number:#fff", tokens);
        Assert.Contains("Keyword:!important", tokens);
        Assert.Contains("Number:1.5rem", tokens);
    }

    /// <summary>Markdown and plain text get no tokens; nothing past the limit is colored; cancellation stops a run.</summary>
    [Fact]
    public void Limits_AndLanguagesWithoutColors()
    {
        Assert.Empty(SyntaxHighlighter.Tokenize("# x\nint y;", TextLanguage.Markdown));
        Assert.Empty(SyntaxHighlighter.Tokenize("int y;", TextLanguage.PlainText));
        Assert.Empty(SyntaxHighlighter.Tokenize("int y;", TextLanguage.CSharp, maxChars: 0));

        var limited = SyntaxHighlighter.Tokenize("int a; int b; int c;", TextLanguage.CSharp, maxChars: 7);
        Assert.All(limited, t => Assert.True(t.End <= 7));

        var big = string.Concat(Enumerable.Repeat("int x = 1; // c\n", 50_000));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => SyntaxHighlighter.Tokenize(big, TextLanguage.CSharp, cancellationToken: cancelled.Token));
    }

    /// <summary>
    /// Random text in every language: no lexer throws, and tokens are in order, never overlap, are never empty and stay
    /// within the text (seeded, so a failure repeats).
    /// </summary>
    /// <param name="seed">The random seed.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void AnyInput_TokensStayOrderedAndInside(int seed)
    {
        const string Alphabet = "ab_Z09 \t\n\"'`#/*-<>!?:;,.{}[]()=$@%&|\\~^+rbf";
        var random = new Random(seed);
        foreach (var language in TextLanguages.All)
        {
            for (int round = 0; round < 60; round++)
            {
                var chars = new char[random.Next(0, 300)];
                for (int i = 0; i < chars.Length; i++)
                {
                    chars[i] = Alphabet[random.Next(Alphabet.Length)];
                }

                var text = new string(chars);
                var tokens = SyntaxHighlighter.Tokenize(text, language);
                int previousEnd = 0;
                foreach (var token in tokens)
                {
                    Assert.True(token.Length > 0, $"{language}: empty token");
                    Assert.True(token.Start >= previousEnd, $"{language}: overlap at {token.Start} in {text}");
                    Assert.True(token.End <= text.Length, $"{language}: past the end");
                    previousEnd = token.End;
                }
            }
        }
    }
}
