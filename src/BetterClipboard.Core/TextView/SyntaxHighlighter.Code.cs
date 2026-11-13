using System.Collections.Frozen;

namespace BetterClipboard.Core.TextView;

/// <summary>The lexer for programming and shell languages: one walk driven by a table per language (<see cref="LexerSpec"/>).</summary>
public static partial class SyntaxHighlighter
{
    /// <summary>
    /// How one programming or shell language is cut into tokens: its comments, strings, word lists and special forms.
    /// One generic walk (<see cref="LexGeneric"/>) reads every language through its table.
    /// </summary>
    private sealed class LexerSpec
    {
        /// <summary>Markers that start a comment running to the end of the line (<c>//</c>, <c>#</c>, <c>--</c>).</summary>
        public string[] LineComments { get; init; } = [];

        /// <summary>
        /// Whether <c>#</c> starts a comment only at the start of a word (shells: <c>a#b</c> and <c>$#</c> are not comments).
        /// </summary>
        public bool HashCommentAtWordStart { get; init; }

        /// <summary>Comment markers with an end (<c>/* */</c>, PowerShell's <c>&lt;# #&gt;</c>).</summary>
        public (string Open, string Close)[] BlockComments { get; init; } = [];

        /// <summary>The characters that delimit strings.</summary>
        public string Quotes { get; init; } = "\"'";

        /// <summary>The escape character inside strings, or <c>'\0'</c> for none.</summary>
        public char Escape { get; init; } = '\\';

        /// <summary>Whether two quotes in a row stand for one inside a string (SQL, PowerShell single quotes).</summary>
        public bool DoubledQuotes { get; init; }

        /// <summary>Quotes whose strings may run over line breaks (JavaScript's and Go's backticks, shells' quotes).</summary>
        public string MultilineQuotes { get; init; } = string.Empty;

        /// <summary>Whether <c>"""</c> and <c>'''</c> open strings that end at the same three quotes (Python, Kotlin, C# raw strings).</summary>
        public bool TripleQuotes { get; init; }

        /// <summary>Letters that may stand right before a quote as part of the string (Python's <c>r f b u</c>, Rust's <c>b r</c>).</summary>
        public string StringPrefixes { get; init; } = string.Empty;

        /// <summary>Whether C#'s verbatim strings exist: <c>@"…"</c> (and <c>$@"…"</c>), with doubled quotes and no escapes.</summary>
        public bool VerbatimStrings { get; init; }

        /// <summary>Whether PowerShell's here-strings exist: <c>@"</c> … <c>"@</c> spanning lines.</summary>
        public bool HereStrings { get; init; }

        /// <summary>The language's keywords.</summary>
        public FrozenSet<string> Keywords { get; init; } = FrozenSet<string>.Empty;

        /// <summary>Built-in type names.</summary>
        public FrozenSet<string> Types { get; init; } = FrozenSet<string>.Empty;

        /// <summary>Literal words (<c>true</c>, <c>null</c>, <c>None</c>).</summary>
        public FrozenSet<string> Literals { get; init; } = FrozenSet<string>.Empty;

        /// <summary>Built-in functions and commands, colored as functions wherever they stand.</summary>
        public FrozenSet<string> Builtins { get; init; } = FrozenSet<string>.Empty;

        /// <summary>Whether words are matched without regard to case (SQL, PowerShell, batch).</summary>
        public bool CaseInsensitive { get; init; }

        /// <summary>Sigils that start a variable (<c>$</c> in PowerShell, shells, PHP; <c>@</c> in Ruby).</summary>
        public string VariableSigils { get; init; } = string.Empty;

        /// <summary>The character that starts an annotation or decorator (<c>@</c>), or <c>'\0'</c>.</summary>
        public char AnnotationSigil { get; init; }

        /// <summary>Whether a <c>#</c> at the start of a line is a preprocessor line (C, C++, C#).</summary>
        public bool HashPreprocessor { get; init; }

        /// <summary>Whether Rust's attributes exist: <c>#[…]</c> and <c>#![…]</c>.</summary>
        public bool RustAttributes { get; init; }

        /// <summary>Whether a PascalCase word is colored as a type (languages that name their types so).</summary>
        public bool PascalCaseTypes { get; init; }

        /// <summary>Whether a word followed by <c>(</c> is colored as a function.</summary>
        public bool CallsAreFunctions { get; init; }

        /// <summary>Whether a word followed by <c>!</c> is a macro call, colored as a function (Rust).</summary>
        public bool BangMacros { get; init; }

        /// <summary>
        /// PowerShell's forms: <c>Verb-Noun</c> commands are functions, <c>-Name</c> parameters are attribute names (and
        /// <c>-eq</c>-like operators keywords), <c>[TypeName]</c> is a type, <c>$true</c>/<c>$false</c>/<c>$null</c> are literals.
        /// </summary>
        public bool PowerShellForms { get; init; }

        /// <summary>Whether the first word of each command (line start, after <c>|</c>, <c>;</c>, <c>&amp;&amp;</c>, …) is a function (shells, batch).</summary>
        public bool CommandWords { get; init; }

        /// <summary>
        /// Batch's forms: <c>REM</c> and <c>::</c> comments at a command's start, <c>%VAR%</c>, <c>%~dp0</c>, <c>%%i</c> and
        /// <c>!VAR!</c> variables, and <c>:label</c> lines.
        /// </summary>
        public bool BatchForms { get; init; }

        /// <summary>Whether Ruby's symbols (<c>:name</c>) and instance variables (<c>@x</c>, <c>@@x</c>) are colored.</summary>
        public bool RubyForms { get; init; }

        /// <summary>Whether Rust's lifetimes (<c>'a</c>) are told apart from character literals (<c>'a'</c>).</summary>
        public bool Lifetimes { get; init; }

        /// <summary>Whether PHP's <c>&lt;?php</c> and <c>?&gt;</c> are preprocessor marks.</summary>
        public bool PhpTags { get; init; }
    }

    /// <summary>Makes a word set from space-separated words.</summary>
    /// <param name="words">The words.</param>
    /// <param name="ignoreCase">Whether the set matches without regard to case.</param>
    /// <returns>The frozen set.</returns>
    private static FrozenSet<string> Words(string words, bool ignoreCase = false) =>
        words.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToFrozenSet(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>The C-like lexer settings shared by C#, Java, Kotlin, JavaScript, TypeScript, Go, Rust and C/C++.</summary>
    private static readonly string[] SlashComments = ["//"];

    /// <summary>The <c>/* */</c> block comment.</summary>
    private static readonly (string, string)[] SlashStarComments = [("/*", "*/")];

    /// <summary>JavaScript's keywords, shared by TypeScript.</summary>
    private const string JavaScriptKeywords =
        "break case catch class const continue debugger default delete do else export extends finally for function if import in " +
        "instanceof let new return super switch this throw try typeof var void while with yield async await of static get set from as";

    /// <summary>The tables of the languages the generic lexer reads, built once.</summary>
    private static readonly FrozenDictionary<TextLanguage, LexerSpec> Specs = new Dictionary<TextLanguage, LexerSpec>
    {
        [TextLanguage.CSharp] = new()
        {
            LineComments = SlashComments,
            BlockComments = SlashStarComments,
            TripleQuotes = true,
            VerbatimStrings = true,
            StringPrefixes = "$",
            HashPreprocessor = true,
            PascalCaseTypes = true,
            CallsAreFunctions = true,
            Keywords = Words(
                "abstract as base break case catch checked class const continue default delegate do else enum event explicit " +
                "extern finally fixed for foreach goto if implicit in interface internal is lock namespace new operator out " +
                "override params private protected public readonly ref return sealed sizeof stackalloc static struct switch " +
                "this throw try typeof unchecked unsafe using virtual volatile while add alias and async await by descending " +
                "equals from get global group init into join let managed nameof not notnull on or orderby partial record " +
                "remove required scoped select set unmanaged value var when where with yield file"),
            Types = Words("bool byte char decimal double float int long object sbyte short string uint ulong ushort void nint nuint dynamic"),
            Literals = Words("true false null"),
        },
        [TextLanguage.Java] = new()
        {
            LineComments = SlashComments,
            BlockComments = SlashStarComments,
            TripleQuotes = true,
            AnnotationSigil = '@',
            PascalCaseTypes = true,
            CallsAreFunctions = true,
            Keywords = Words(
                "abstract assert break case catch class const continue default do else enum extends final finally for goto if " +
                "implements import instanceof interface native new package private protected public return static strictfp " +
                "super switch synchronized this throw throws transient try volatile while var record sealed permits yield"),
            Types = Words("boolean byte char double float int long short void"),
            Literals = Words("true false null"),
        },
        [TextLanguage.Kotlin] = new()
        {
            LineComments = SlashComments,
            BlockComments = SlashStarComments,
            TripleQuotes = true,
            AnnotationSigil = '@',
            PascalCaseTypes = true,
            CallsAreFunctions = true,
            Keywords = Words(
                "as break class continue do else for fun if in interface is object package return super this throw try typealias " +
                "typeof val var when while by catch constructor delegate dynamic field file finally get import init param property " +
                "receiver set setparam where actual abstract annotation companion const crossinline data enum expect external final " +
                "infix inline inner internal lateinit noinline open operator out override private protected public reified sealed " +
                "suspend tailrec vararg"),
            Literals = Words("true false null"),
        },
        [TextLanguage.JavaScript] = new()
        {
            LineComments = SlashComments,
            BlockComments = SlashStarComments,
            Quotes = "\"'`",
            MultilineQuotes = "`",
            AnnotationSigil = '@',
            PascalCaseTypes = true,
            CallsAreFunctions = true,
            Keywords = Words(JavaScriptKeywords),
            Literals = Words("true false null undefined NaN Infinity"),
            Builtins = Words("console document window require module exports process globalThis"),
        },
        [TextLanguage.TypeScript] = new()
        {
            LineComments = SlashComments,
            BlockComments = SlashStarComments,
            Quotes = "\"'`",
            MultilineQuotes = "`",
            AnnotationSigil = '@',
            PascalCaseTypes = true,
            CallsAreFunctions = true,
            Keywords = Words(JavaScriptKeywords + " interface type enum namespace declare abstract implements private protected public " +
                "readonly keyof infer is asserts satisfies override unique module global"),
            Types = Words("string number boolean any unknown never void object symbol bigint"),
            Literals = Words("true false null undefined NaN Infinity"),
            Builtins = Words("console document window require module exports process globalThis"),
        },
        [TextLanguage.Python] = new()
        {
            LineComments = ["#"],
            TripleQuotes = true,
            StringPrefixes = "rRbBfFuU",
            AnnotationSigil = '@',
            CallsAreFunctions = true,
            Keywords = Words(
                "and as assert async await break class continue def del elif else except finally for from global if import in is " +
                "lambda nonlocal not or pass raise return try while with yield match case"),
            Literals = Words("True False None self cls"),
            Builtins = Words(
                "print len range open str int float list dict set tuple bool type isinstance super enumerate zip map filter sorted " +
                "reversed min max sum abs any all input format repr hasattr getattr setattr id iter next object property " +
                "staticmethod classmethod bytes frozenset round divmod"),
        },
        [TextLanguage.Ruby] = new()
        {
            LineComments = ["#"],
            VariableSigils = "$",
            RubyForms = true,
            CallsAreFunctions = true,
            PascalCaseTypes = true,
            Keywords = Words(
                "alias and begin break case class def defined? do else elsif end ensure for if in module next not or redo rescue " +
                "retry return super then undef unless until when while yield"),
            Literals = Words("true false nil self"),
            Builtins = Words("require require_relative attr_accessor attr_reader attr_writer private protected public include extend " +
                "lambda proc puts print raise loop p"),
        },
        [TextLanguage.Php] = new()
        {
            LineComments = ["//", "#"],
            BlockComments = SlashStarComments,
            VariableSigils = "$",
            PhpTags = true,
            CallsAreFunctions = true,
            PascalCaseTypes = true,
            CaseInsensitive = true,
            Keywords = Words(
                "abstract and array as break callable case catch class clone const continue declare default do echo else elseif " +
                "empty enddeclare endfor endforeach endif endswitch endwhile extends final finally fn for foreach function global " +
                "goto if implements include include_once instanceof insteadof interface isset list match namespace new or print " +
                "private protected public readonly require require_once return static switch throw trait try unset use var while xor yield",
                ignoreCase: true),
            Literals = Words("true false null", ignoreCase: true),
        },
        [TextLanguage.Go] = new()
        {
            LineComments = SlashComments,
            BlockComments = SlashStarComments,
            Quotes = "\"'`",
            MultilineQuotes = "`",
            CallsAreFunctions = true,
            Keywords = Words(
                "break case chan const continue default defer else fallthrough for func go goto if import interface map package " +
                "range return select struct switch type var"),
            Types = Words("bool byte complex64 complex128 error float32 float64 int int8 int16 int32 int64 rune string uint uint8 uint16 uint32 uint64 uintptr any"),
            Literals = Words("true false nil iota"),
            Builtins = Words("append cap close complex copy delete imag len make new panic print println real recover"),
        },
        [TextLanguage.Rust] = new()
        {
            LineComments = SlashComments,
            BlockComments = SlashStarComments,
            Quotes = "\"'",
            MultilineQuotes = "\"",
            StringPrefixes = "bBrR",
            RustAttributes = true,
            Lifetimes = true,
            BangMacros = true,
            PascalCaseTypes = true,
            CallsAreFunctions = true,
            Keywords = Words(
                "as async await break const continue crate dyn else enum extern fn for if impl in let loop match mod move mut pub " +
                "ref return static struct super trait type unsafe use where while"),
            Types = Words("i8 i16 i32 i64 i128 isize u8 u16 u32 u64 u128 usize f32 f64 bool char str"),
            Literals = Words("true false self Self"),
        },
        [TextLanguage.Cpp] = new()
        {
            LineComments = SlashComments,
            BlockComments = SlashStarComments,
            HashPreprocessor = true,
            CallsAreFunctions = true,
            Keywords = Words(
                "alignas alignof and asm auto bitand bitor break case catch class compl concept const consteval constexpr constinit " +
                "const_cast continue co_await co_return co_yield decltype default delete do dynamic_cast else enum explicit export " +
                "extern for friend goto if inline mutable namespace new noexcept not operator or private protected public register " +
                "reinterpret_cast requires return sizeof static static_assert static_cast struct switch template this thread_local " +
                "throw try typedef typeid typename union using virtual volatile while xor"),
            Types = Words("bool char char8_t char16_t char32_t double float int long short signed unsigned void wchar_t size_t ssize_t " +
                "int8_t int16_t int32_t int64_t uint8_t uint16_t uint32_t uint64_t intptr_t uintptr_t std string vector map"),
            Literals = Words("true false nullptr NULL"),
        },
        [TextLanguage.PowerShell] = new()
        {
            LineComments = ["#"],
            BlockComments = [("<#", "#>")],
            Escape = '`',
            DoubledQuotes = true,
            MultilineQuotes = "\"'",
            HereStrings = true,
            VariableSigils = "$",
            PowerShellForms = true,
            CaseInsensitive = true,
            Keywords = Words(
                "begin break catch class continue data define do dynamicparam else elseif end enum exit filter finally for foreach " +
                "from function hidden if in param process return static switch throw trap try until using var while workflow parallel sequence",
                ignoreCase: true),
        },
        [TextLanguage.Shell] = new()
        {
            LineComments = ["#"],
            HashCommentAtWordStart = true,
            MultilineQuotes = "\"'`",
            Quotes = "\"'`",
            VariableSigils = "$",
            CommandWords = true,
            Keywords = Words(
                "if then else elif fi case esac for while until do done in function select time coproc return exit break continue " +
                "local export readonly declare typeset unset shift source alias set trap eval exec"),
        },
        [TextLanguage.Batch] = new()
        {
            Quotes = "\"",
            Escape = '\0',
            BatchForms = true,
            CommandWords = true,
            CaseInsensitive = true,
            Keywords = Words(
                "if else for do in goto call exit set setlocal endlocal echo off on not exist defined errorlevel equ neq lss leq gtr " +
                "geq shift pushd popd enabledelayedexpansion enableextensions", ignoreCase: true),
        },
        [TextLanguage.Sql] = new()
        {
            LineComments = ["--"],
            BlockComments = SlashStarComments,
            Quotes = "'\"",
            Escape = '\0',
            DoubledQuotes = true,
            MultilineQuotes = "'",
            CaseInsensitive = true,
            CallsAreFunctions = true,
            Keywords = Words(
                "select from where and or not insert into values update set delete create alter drop table view index trigger procedure " +
                "function database schema primary key foreign references unique check default constraint join inner left right full " +
                "outer cross on as distinct group by order having limit offset top union all except intersect case when then else end " +
                "exists in between like ilike is begin commit rollback transaction declare exec execute returns return if while with " +
                "recursive asc desc cascade grant revoke truncate merge using matched replace pragma autoincrement identity go use " +
                "over partition rows range preceding following current row fetch next only", ignoreCase: true),
            Types = Words("int integer bigint smallint tinyint decimal numeric float real double char varchar nvarchar nchar text ntext " +
                "date datetime datetime2 timestamp time bit boolean bool blob binary varbinary uuid uniqueidentifier json jsonb serial money", ignoreCase: true),
            Literals = Words("null true false", ignoreCase: true),
        },
    }.ToFrozenDictionary();

    /// <summary>The PowerShell operators spelled as dashed words; they are keywords, not parameters.</summary>
    private static readonly FrozenSet<string> PowerShellOperators = Words(
        "-eq -ne -gt -ge -lt -le -like -notlike -match -notmatch -replace -contains -notcontains -in -notin -and -or -not -xor " +
        "-band -bor -bxor -bnot -shl -shr -is -isnot -as -split -join -f -ieq -ine -ilike -imatch -ireplace -ceq -cne -clike -cmatch -creplace",
        ignoreCase: true);

    /// <summary>The table the generic lexer reads for a language, or <see langword="null"/> for a language it does not read.</summary>
    /// <param name="language">The language.</param>
    /// <returns>Its table.</returns>
    private static LexerSpec? SpecOf(TextLanguage language) => Specs.GetValueOrDefault(language);

    /// <summary>
    /// Colors a programming or shell language through its table: comments, strings, numbers, words (keywords, types,
    /// literals, built-ins, calls, PascalCase types), variables, annotations and preprocessor lines.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="limit">Where the colored part ends.</param>
    /// <param name="spec">The language's table.</param>
    /// <param name="sink">Receives the tokens.</param>
    private static void LexGeneric(string text, int limit, LexerSpec spec, TokenSink sink)
    {
        bool lineStart = true;    // nothing but blanks since the line's start
        bool commandStart = true; // the next word starts a command (shells, batch)
        int i = 0;
        while (i < limit)
        {
            sink.Check(i);
            char c = text[i];
            if (c == '\n')
            {
                lineStart = true;
                commandStart = true;
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            int next = LexGenericToken(text, i, limit, spec, sink, lineStart, ref commandStart);
            lineStart = false;
            i = next > i ? next : i + 1;
        }
    }

    /// <summary>
    /// Colors the one token starting at <paramref name="i"/> (see <see cref="LexGeneric"/>), or none when the character
    /// there is plain text, and says where the walk goes on.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="i">The token's first character (not a blank).</param>
    /// <param name="limit">Where the colored part ends.</param>
    /// <param name="spec">The language's table.</param>
    /// <param name="sink">Receives the token.</param>
    /// <param name="lineStart">Whether only blanks precede it on its line.</param>
    /// <param name="commandStart">Whether a command starts here (shells); updated for the next token.</param>
    /// <returns>The index just past the token; <paramref name="i"/> + 1 at least.</returns>
    private static int LexGenericToken(string text, int i, int limit, LexerSpec spec, TokenSink sink, bool lineStart, ref bool commandStart)
    {
        char c = text[i];
        char nextChar = i + 1 < limit ? text[i + 1] : '\0';

        if (spec.PhpTags && (At(text, i, limit, "<?php", ignoreCase: true) || At(text, i, limit, "?>")))
        {
            int end = text[i] == '<' ? i + 5 : i + 2;
            sink.Add(i, end, TokenKind.Preprocessor);
            return end;
        }

        if (lineStart && spec.HashPreprocessor && c == '#')
        {
            int end = LineEnd(text, i, limit);
            sink.Add(i, end, TokenKind.Preprocessor);
            return end;
        }

        if (spec.RustAttributes && c == '#' && (nextChar == '[' || (nextChar == '!' && i + 2 < limit && text[i + 2] == '[')))
        {
            int end = ScanBracketed(text, text.IndexOf('[', i, Math.Min(3, limit - i)), limit);
            sink.Add(i, end, TokenKind.Preprocessor);
            return end;
        }

        foreach (var (open, close) in spec.BlockComments)
        {
            if (At(text, i, limit, open))
            {
                int closeAt = text.IndexOf(close, i + open.Length, limit - i - open.Length, StringComparison.Ordinal);
                int end = closeAt < 0 ? limit : closeAt + close.Length;
                sink.Add(i, end, TokenKind.Comment);
                return end;
            }
        }

        foreach (var marker in spec.LineComments)
        {
            if (At(text, i, limit, marker) && (!spec.HashCommentAtWordStart || marker != "#" || i == 0 || char.IsWhiteSpace(text[i - 1]) || text[i - 1] is ';' or '(' or '|' or '&'))
            {
                int end = LineEnd(text, i, limit);
                sink.Add(i, end, TokenKind.Comment);
                return end;
            }
        }

        if (spec.BatchForms && LexBatchForm(text, i, limit, sink, lineStart, commandStart) is int batchEnd and > 0)
        {
            commandStart = false;
            return batchEnd;
        }

        if (LexStringStart(text, i, limit, spec, sink) is int stringEnd and > 0)
        {
            commandStart = false;
            return stringEnd;
        }

        if (spec.VariableSigils.Contains(c) && LexVariable(text, i, limit, spec, sink) is int variableEnd and > 0)
        {
            commandStart = false;
            return variableEnd;
        }

        if (spec.RubyForms && LexRubyForm(text, i, limit, sink) is int rubyEnd and > 0)
        {
            return rubyEnd;
        }

        if (c == spec.AnnotationSigil && c != '\0' && IsIdentifierStart(nextChar))
        {
            int end = i + 1;
            while (end < limit && (IsIdentifierPart(text[end]) || text[end] == '.'))
            {
                end++;
            }

            sink.Add(i, end, TokenKind.Preprocessor);
            return end;
        }

        if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(nextChar)))
        {
            if (i > 0 && IsIdentifierPart(text[i - 1]))
            {
                return i + 1; // the "2" of "utf8" or "x2": part of a word
            }

            int end = ScanNumber(text, i, limit);
            sink.Add(i, end, TokenKind.Number);
            commandStart = false;
            return end;
        }

        if (spec.PowerShellForms && LexPowerShellForm(text, i, limit, sink, ref commandStart) is int psEnd and > 0)
        {
            return psEnd;
        }

        if (IsIdentifierStart(c))
        {
            return LexWord(text, i, limit, spec, sink, ref commandStart);
        }

        // Command separators start a new command in shells and batch files; any other character (the "[" of a test, the
        // dash of an option) ends the command position, so "-f" after "if [" is not taken for a command.
        if (spec.CommandWords)
        {
            commandStart = c is '|' or ';' or '&' or '(' or '`' or '{';
        }

        return i + 1;
    }

    /// <summary>
    /// Colors a word: a keyword, a literal, a type, a built-in, a command, a call, a macro or a PascalCase type, in that
    /// order; any other word is plain text.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="i">The word's first character.</param>
    /// <param name="limit">Where the colored part ends.</param>
    /// <param name="spec">The language's table.</param>
    /// <param name="sink">Receives the token.</param>
    /// <param name="commandStart">Whether a command starts here; cleared unless the word is a keyword that leads one (<c>then</c>, <c>do</c>, <c>sudo</c>).</param>
    /// <returns>The index just past the word.</returns>
    private static int LexWord(string text, int i, int limit, LexerSpec spec, TokenSink sink, ref bool commandStart)
    {
        int end = i + 1;
        while (end < limit && (IsIdentifierPart(text[end]) || (spec.CommandWords && text[end] is '-' or '.' && end + 1 < limit && IsIdentifierPart(text[end + 1]))))
        {
            end++;
        }

        // Ruby's "defined?" and method names ending in ? or ! belong to the word.
        if (spec.RubyForms && end < limit && text[end] is '?' or '!' && (end + 1 >= limit || text[end + 1] != '='))
        {
            end++;
        }

        // Looked up as a span: a megabyte of code has hundreds of thousands of words, none of which needs a string.
        var word = text.AsSpan(i, end - i);
        int after = end;
        while (after < limit && text[after] is ' ' or '\t')
        {
            after++;
        }

        bool call = after < limit && text[after] == '(';
        TokenKind kind;
        if (Has(spec.Keywords, word))
        {
            // A keyword that leads a command (then, do, else, …) keeps the next word a command.
            commandStart = spec.CommandWords && IsAny(word, CommandLeadingKeywords);
            sink.Add(i, end, TokenKind.Keyword);
            return end;
        }

        kind = Has(spec.Literals, word) ? TokenKind.Literal
            : Has(spec.Types, word) ? TokenKind.Type
            : Has(spec.Builtins, word) ? TokenKind.Function
            : spec.CommandWords && commandStart ? TokenKind.Function
            : spec.BangMacros && end < limit && text[end] == '!' && (end + 1 >= limit || text[end + 1] != '=') ? TokenKind.Function
            : spec.CallsAreFunctions && call ? TokenKind.Function
            : spec.PascalCaseTypes && IsPascalCase(word) ? TokenKind.Type
            : TokenKind.Text;
        if (kind == TokenKind.Function && spec.BangMacros && end < limit && text[end] == '!')
        {
            end++; // the macro's "!" belongs to its name: println!
        }

        // A word that runs another command (sudo apt-get …) keeps the next word a command.
        commandStart = commandStart && spec.CommandWords && IsAny(word, CommandPrefixWords);
        sink.Add(i, end, kind);
        return end;
    }

    /// <summary>Shell keywords after which the next word starts a command (<c>then ls</c>, <c>do echo</c>).</summary>
    private static readonly string[] CommandLeadingKeywords = ["then", "do", "else", "elif", "if", "while", "until", "time", "exec"];

    /// <summary>Commands that run the command after them (<c>sudo apt-get</c>), whose next word is colored as a command too.</summary>
    private static readonly string[] CommandPrefixWords = ["sudo", "nohup", "env", "xargs", "exec", "time", "watch", "doas"];

    /// <summary>Whether a word set holds a word given as a span, without making a string of it.</summary>
    /// <param name="set">The set (ordinal, or ordinal ignoring case).</param>
    /// <param name="word">The word.</param>
    /// <returns><see langword="true"/> when the set holds it.</returns>
    private static bool Has(FrozenSet<string> set, ReadOnlySpan<char> word) =>
        set.Count > 0 && set.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word);

    /// <summary>Whether a word equals one of a few words, ignoring case.</summary>
    /// <param name="word">The word.</param>
    /// <param name="words">The candidates.</param>
    /// <returns><see langword="true"/> on a match.</returns>
    private static bool IsAny(ReadOnlySpan<char> word, string[] words)
    {
        foreach (var candidate in words)
        {
            if (word.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a word is PascalCase: an upper-case letter first, a lower-case letter somewhere after it (so <c>URL</c> and <c>MAX</c> are not).</summary>
    /// <param name="word">The word.</param>
    /// <returns><see langword="true"/> for PascalCase.</returns>
    private static bool IsPascalCase(ReadOnlySpan<char> word)
    {
        if (word.Length < 2 || !char.IsUpper(word[0]))
        {
            return false;
        }

        foreach (char c in word[1..])
        {
            if (char.IsLower(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Colors a string that starts at <paramref name="i"/>: with a prefix (Python's <c>f"…"</c>, Rust's <c>r"…"</c>, C#'s
    /// <c>$@"…"</c>), triple-quoted, verbatim, a PowerShell here-string, or plain; Rust's lifetimes are left out.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="i">Where a string may start.</param>
    /// <param name="limit">Where the colored part ends.</param>
    /// <param name="spec">The language's table.</param>
    /// <param name="sink">Receives the token.</param>
    /// <returns>The index just past the string, or 0 when no string starts here.</returns>
    private static int LexStringStart(string text, int i, int limit, LexerSpec spec, TokenSink sink)
    {
        int quoteAt = i;
        while (quoteAt < limit && quoteAt - i < 3 && spec.StringPrefixes.Contains(text[quoteAt]) && (quoteAt == i || IsIdentifierPart(text[quoteAt - 1]) || text[quoteAt - 1] is '$' or '@'))
        {
            quoteAt++;
        }

        // A prefix counts only before a quote, and only at the start of a word ("buf" is not b + "uf").
        if (quoteAt > i && (i > 0 && IsIdentifierPart(text[i - 1])))
        {
            return 0;
        }

        if (spec.VerbatimStrings && quoteAt < limit && text[quoteAt] == '@' && quoteAt + 1 < limit && text[quoteAt + 1] == '"')
        {
            int end = ScanString(text, quoteAt + 1, limit, '\0', doubledQuotes: true, multiline: true);
            sink.Add(i, end, TokenKind.String);
            return end;
        }

        if (spec.HereStrings && quoteAt == i && text[i] == '@' && i + 1 < limit && text[i + 1] is '"' or '\'')
        {
            string close = text[i + 1] + "@";
            int closeAt = text.IndexOf("\n" + close, i + 2, limit - i - 2, StringComparison.Ordinal);
            int end = closeAt < 0 ? limit : closeAt + 3;
            sink.Add(i, end, TokenKind.String);
            return end;
        }

        if (quoteAt >= limit || !spec.Quotes.Contains(text[quoteAt]))
        {
            return 0;
        }

        char quote = text[quoteAt];
        if (spec.TripleQuotes && quote is '"' or '\'' && At(text, quoteAt, limit, new string(quote, 3)))
        {
            string triple = new(quote, 3);
            int closeAt = text.IndexOf(triple, quoteAt + 3, limit - quoteAt - 3, StringComparison.Ordinal);
            int end = closeAt < 0 ? limit : closeAt + 3;
            sink.Add(i, end, TokenKind.String);
            return end;
        }

        if (spec.Lifetimes && quote == '\'' && quoteAt == i)
        {
            // 'a' and '\n' are characters; 'a (no closing quote right after one character) is a lifetime.
            bool character = (quoteAt + 2 < limit && text[quoteAt + 2] == '\'') || (quoteAt + 1 < limit && text[quoteAt + 1] == '\\');
            if (!character)
            {
                int end = quoteAt + 1;
                while (end < limit && IsIdentifierPart(text[end]))
                {
                    end++;
                }

                sink.Add(i, end, TokenKind.Type);
                return end;
            }
        }

        bool multiline = spec.MultilineQuotes.Contains(quote);
        char escape = spec.Escape;
        bool doubled = spec.DoubledQuotes;
        if (spec.PowerShellForms && quote == '\'')
        {
            escape = '\0'; // single-quoted PowerShell strings know only doubled quotes
        }

        if (spec.CommandWords && quote == '\'')
        {
            escape = '\0'; // nothing escapes inside a shell's single quotes
        }

        int stringEnd = ScanString(text, quoteAt, limit, escape, doubled, multiline);
        sink.Add(i, stringEnd, TokenKind.String);
        return stringEnd;
    }

    /// <summary>
    /// Colors a variable that starts with a sigil: <c>$name</c>, <c>${…}</c>, PowerShell's <c>$env:PATH</c> and
    /// <c>$true</c> (a literal), shells' <c>$1</c>, <c>$?</c>, <c>$@</c>.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="i">The sigil.</param>
    /// <param name="limit">Where the colored part ends.</param>
    /// <param name="spec">The language's table.</param>
    /// <param name="sink">Receives the token.</param>
    /// <returns>The index just past the variable, or 0 when the sigil starts none (a lone <c>$</c>).</returns>
    private static int LexVariable(string text, int i, int limit, LexerSpec spec, TokenSink sink)
    {
        if (i + 1 >= limit)
        {
            return 0;
        }

        char next = text[i + 1];
        if (next == '{')
        {
            int close = text.IndexOf('}', i + 2, Math.Min(limit, LineEnd(text, i, limit)) - i - 2);
            int end = close < 0 ? i + 2 : close + 1;
            sink.Add(i, end, TokenKind.Variable);
            return end;
        }

        if (spec.CommandWords && next is '@' or '?' or '#' or '$' or '!' or '*' or '-' or (>= '0' and <= '9'))
        {
            sink.Add(i, i + 2, TokenKind.Variable);
            return i + 2;
        }

        if (!IsIdentifierStart(next))
        {
            return 0;
        }

        int wordEnd = i + 2;
        while (wordEnd < limit && (IsIdentifierPart(text[wordEnd]) || (spec.PowerShellForms && text[wordEnd] == ':' && wordEnd + 1 < limit && IsIdentifierStart(text[wordEnd + 1]))))
        {
            wordEnd++;
        }

        var name = text.AsSpan(i + 1, wordEnd - i - 1);
        bool literal = spec.PowerShellForms && (name.Equals("true", StringComparison.OrdinalIgnoreCase) || name.Equals("false", StringComparison.OrdinalIgnoreCase) || name.Equals("null", StringComparison.OrdinalIgnoreCase));
        sink.Add(i, wordEnd, literal ? TokenKind.Literal : TokenKind.Variable);
        return wordEnd;
    }

    /// <summary>
    /// Colors PowerShell's own forms at <paramref name="i"/>: a <c>-Parameter</c> or a <c>-eq</c>-like operator, a
    /// <c>[TypeName]</c>, and a <c>Verb-Noun</c> command.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="i">The token's first character.</param>
    /// <param name="limit">Where the colored part ends.</param>
    /// <param name="sink">Receives the token.</param>
    /// <param name="commandStart">Unused by PowerShell's forms; kept false after one.</param>
    /// <returns>The index just past the form, or 0 when none starts here.</returns>
    private static int LexPowerShellForm(string text, int i, int limit, TokenSink sink, ref bool commandStart)
    {
        char c = text[i];
        if (c == '-' && i + 1 < limit && char.IsLetter(text[i + 1]) && (i == 0 || char.IsWhiteSpace(text[i - 1]) || text[i - 1] is '(' or '{'))
        {
            int end = i + 2;
            while (end < limit && IsIdentifierPart(text[end]))
            {
                end++;
            }

            sink.Add(i, end, PowerShellOperators.Contains(text.Substring(i, end - i)) ? TokenKind.Keyword : TokenKind.AttributeName);
            commandStart = false;
            return end;
        }

        if (c == '[' && i + 1 < limit && IsIdentifierStart(text[i + 1]))
        {
            int end = i + 1;
            while (end < limit && (IsIdentifierPart(text[end]) || text[end] is '.' or '[' or ']' or ','))
            {
                if (text[end] == ']' && (end + 1 >= limit || text[end + 1] != ']'))
                {
                    break;
                }

                end++;
            }

            if (end < limit && text[end] == ']')
            {
                sink.Add(i + 1, end, TokenKind.Type);
                return end + 1;
            }

            return 0;
        }

        if (IsIdentifierStart(c))
        {
            // Verb-Noun: letters, a dash, a letter (Get-ChildItem, Invoke-RestMethod); dashes may repeat in module commands.
            int end = i + 1;
            while (end < limit && IsIdentifierPart(text[end]))
            {
                end++;
            }

            if (end + 1 < limit && text[end] == '-' && char.IsLetter(text[end + 1]))
            {
                end++;
                while (end < limit && (IsIdentifierPart(text[end]) || (text[end] == '-' && end + 1 < limit && char.IsLetter(text[end + 1]))))
                {
                    end++;
                }

                sink.Add(i, end, TokenKind.Function);
                commandStart = false;
                return end;
            }
        }

        return 0;
    }

    /// <summary>
    /// Colors batch's own forms at <paramref name="i"/>: <c>REM</c> and <c>::</c> comments where a command starts, a
    /// <c>:label</c> line, <c>%VAR%</c>, <c>%~dp0</c>, <c>%%i</c>, <c>%1</c> and <c>!VAR!</c> variables, and the <c>@</c>
    /// before a command.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="i">The token's first character.</param>
    /// <param name="limit">Where the colored part ends.</param>
    /// <param name="sink">Receives the token.</param>
    /// <param name="lineStart">Whether only blanks precede it on its line.</param>
    /// <param name="commandStart">Whether a command starts here.</param>
    /// <returns>The index just past the form, or 0 when none starts here.</returns>
    private static int LexBatchForm(string text, int i, int limit, TokenSink sink, bool lineStart, bool commandStart)
    {
        char c = text[i];
        if (commandStart && (At(text, i, limit, "rem", ignoreCase: true) && (i + 3 >= limit || char.IsWhiteSpace(text[i + 3]))))
        {
            int end = LineEnd(text, i, limit);
            sink.Add(i, end, TokenKind.Comment);
            return end;
        }

        if (lineStart && At(text, i, limit, "::"))
        {
            int end = LineEnd(text, i, limit);
            sink.Add(i, end, TokenKind.Comment);
            return end;
        }

        if (lineStart && c == ':' && i + 1 < limit && IsIdentifierStart(text[i + 1]))
        {
            int end = i + 1;
            while (end < limit && !char.IsWhiteSpace(text[end]))
            {
                end++;
            }

            sink.Add(i, end, TokenKind.Function);
            return end;
        }

        if (c == '%')
        {
            if (i + 1 < limit && text[i + 1] == '%' && i + 2 < limit && char.IsLetter(text[i + 2]))
            {
                sink.Add(i, i + 3, TokenKind.Variable); // %%i
                return i + 3;
            }

            if (i + 1 < limit && (char.IsAsciiDigit(text[i + 1]) || text[i + 1] == '*'))
            {
                sink.Add(i, i + 2, TokenKind.Variable); // %1, %*
                return i + 2;
            }

            if (i + 1 < limit && text[i + 1] == '~')
            {
                int end = i + 2;
                while (end < limit && char.IsAsciiLetterOrDigit(text[end]))
                {
                    end++;
                }

                sink.Add(i, end, TokenKind.Variable); // %~dp0, %~nx1
                return end;
            }

            int close = text.IndexOf('%', i + 1, LineEnd(text, i, limit) - i - 1);
            if (close > i + 1)
            {
                sink.Add(i, close + 1, TokenKind.Variable);
                return close + 1;
            }

            return 0;
        }

        if (c == '!' && i + 1 < limit && IsIdentifierStart(text[i + 1]))
        {
            int close = text.IndexOf('!', i + 1, LineEnd(text, i, limit) - i - 1);
            if (close > i + 1)
            {
                sink.Add(i, close + 1, TokenKind.Variable);
                return close + 1;
            }
        }

        if (c == '@' && commandStart)
        {
            sink.Add(i, i + 1, TokenKind.Punctuation);
            return i + 1;
        }

        return 0;
    }

    /// <summary>Colors Ruby's symbols (<c>:name</c>, not <c>::</c>) and instance and class variables (<c>@x</c>, <c>@@x</c>).</summary>
    /// <param name="text">The text.</param>
    /// <param name="i">The token's first character.</param>
    /// <param name="limit">Where the colored part ends.</param>
    /// <param name="sink">Receives the token.</param>
    /// <returns>The index just past the form, or 0 when none starts here.</returns>
    private static int LexRubyForm(string text, int i, int limit, TokenSink sink)
    {
        char c = text[i];
        if (c == ':' && i + 1 < limit && IsIdentifierStart(text[i + 1]) && (i == 0 || text[i - 1] != ':'))
        {
            int end = i + 2;
            while (end < limit && IsIdentifierPart(text[end]))
            {
                end++;
            }

            sink.Add(i, end, TokenKind.Literal);
            return end;
        }

        if (c == '@')
        {
            int start = i + 1 < limit && text[i + 1] == '@' ? i + 2 : i + 1;
            if (start < limit && IsIdentifierStart(text[start]))
            {
                int end = start + 1;
                while (end < limit && IsIdentifierPart(text[end]))
                {
                    end++;
                }

                sink.Add(i, end, TokenKind.Variable);
                return end;
            }
        }

        return 0;
    }

    /// <summary>Finds the end of a bracketed group (<c>[…]</c>, nested), as Rust's attributes need.</summary>
    /// <param name="text">The text.</param>
    /// <param name="open">The opening bracket's index, or -1.</param>
    /// <param name="limit">Where the colored part ends.</param>
    /// <returns>The index just past the closing bracket, or the line's end when it does not close.</returns>
    private static int ScanBracketed(string text, int open, int limit)
    {
        if (open < 0)
        {
            return limit;
        }

        int depth = 0;
        for (int i = open; i < limit; i++)
        {
            if (text[i] == '[')
            {
                depth++;
            }
            else if (text[i] == ']' && --depth == 0)
            {
                return i + 1;
            }
            else if (text[i] == '\n')
            {
                return i;
            }
        }

        return limit;
    }
}
