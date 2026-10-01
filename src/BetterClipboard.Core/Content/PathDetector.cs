using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BetterClipboard.Core.Content;

/// <summary>
/// Recognizes text that is nothing but file-system paths (<c>C:\a\b.txt</c>, <c>/var/log/syslog</c>,
/// <c>folder/file.cs</c>, …), so the Files tab can list copied paths next to real file lists. Precision
/// first: a wrong "yes" puts a sentence into the Files tab, while a wrong "no" only leaves a path in the Text
/// tab, where it is listed anyway — so anything that could be prose is not a path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape.</b> Every non-empty line (surrounding whitespace and the kind of line ending do not matter) must
/// be one path, or several separated by tabs (a row copied from a table). One line that is not paths makes the
/// whole text "not paths". A space never separates paths: paths in a row after spaces are a command line, an
/// executable and its arguments (<c>./lint.sh src/a.sh</c>, <c>C:\x.exe C:\in.txt</c>). A path may be wrapped
/// in matching quotes — <c>"…"</c> (Explorer's "Copy as path"), <c>'…'</c>, <c>`…`</c> (Markdown), <c>“…”</c>,
/// <c>‘…’</c>.
/// </para>
/// <para>
/// <b>Evidence, by how a path starts.</b>
/// <list type="bullet">
/// <item>Anchors nothing else starts with are enough on their own: a drive (<c>C:\</c>, <c>C:/</c>), UNC
/// (<c>\\server\share</c>), device (<c>\\?\</c>, <c>\\.\</c>), <c>file:</c> URI, home (<c>~/</c>), dot
/// (<c>./</c>, <c>..\</c>) and variables (<c>%APPDATA%\</c>, <c>$env:TEMP\</c>, <c>${HOME}/</c>).</item>
/// <item><c>/a/b</c> needs two segments ("/help" is a slash command, "/s" a switch) and is refused for
/// single-letter roots without a file name ("/r/programming") and regular-expression literals
/// ("/abc/gi").</item>
/// <item><c>\a\b</c> needs a file name at the end or three segments; single letters ("\d\w"), escapes only
/// ("\x41\x42") or a LaTeX command ("\alpha\beta") refuse it.</item>
/// <item>A bare relative path needs a real file name at the end — an extension in one case (<c>.cs</c>,
/// <c>.JPG</c>; "items.Count" is code), a dotfile (<c>.gitignore</c>) or a well-known bare name
/// (<c>Makefile</c>) — which is what separates <c>folder/file.cs</c> from "and/or", "24/7" and "TCP/IP". A host
/// name in front ("example.com/index.html"), an assignment or option in front ("PY=/c/x.exe", "-Iinclude/x.h"),
/// a dotted abbreviation ("Ph.D/M.Sc"), a pair of technology names ("React/Next.js") or, with two segments, a
/// file name in front ("self.x/self.y") refuse it.</item>
/// </list>
/// </para>
/// <para>
/// <b>Whitespace.</b> A space belongs to a path only where it can start neither a sentence nor the arguments of
/// a command: inside a segment after a strong anchor, after the first segment of <c>/a/b</c>, or inside quotes
/// (where the first segment of a bare relative path still has to pass the unquoted test, because a backticked
/// <c>python scripts/build.py</c> is a command) — single spaces, never next to a separator. No word of such a
/// segment may be an option ("-r") or, unless it is the last word, a complete file name ("script.sh args",
/// "file.txt is here", "~/.bashrc (user)"), and no word after the first a verb ("System32 contains drivers").
/// Unquoted, every word after the first must also read like a name: capitalized, a number, a symbol, a
/// bracketed name ("(x86)", but not "(copy)"), brand-cased ("iPhone"), a version ("v2") or a title connector
/// ("Program Files (x86)", "Call of Duty", "New Text Document.txt") — or the segment is Windows' own
/// "New folder". So lowercase names with spaces (<c>C:\Users\me\my stuff</c>) count only when quoted: that miss
/// is the price of never taking "C:\Windows is where …" or "./venv/bin/pip install x/y.txt" for a path.
/// </para>
/// <para>
/// Pure and deterministic, so the verdict can be stored and recomputed; texts longer than
/// <see cref="MaxTextLength"/> are rejected before any parsing. Changing a verdict for any input means bumping
/// <see cref="RulesVersion"/>, which makes the store recompute every saved verdict.
/// </para>
/// </remarks>
public static partial class PathDetector
{
    /// <summary>
    /// Version of the rules. The store keeps a verdict per entry and recomputes all of them once when this
    /// number changes, so bump it with every change that turns any text's verdict — otherwise entries saved
    /// before the change keep the old answer and the Files tab disagrees with new copies of the same text.
    /// </summary>
    public const int RulesVersion = 1;

    /// <summary>
    /// Longest text examined, in UTF-16 code units: half of <see cref="ContentClassifier.SearchMaxChars"/>.
    /// </summary>
    /// <remarks>
    /// The store recomputes verdicts from the indexed search text, which is the full text cut at the search cap.
    /// Because every text this long or longer is "not paths", a cut copy (always at least this long) gets the
    /// same verdict as the full text — no payload has to be reloaded. It still fits a few hundred paths.
    /// </remarks>
    public const int MaxTextLength = ContentClassifier.SearchMaxChars / 2;

    /// <summary>Longest single path accepted: Windows' long-path limit.</summary>
    private const int MaxPathLength = 32_767;

    /// <summary>Longest single segment accepted: the file-name limit of NTFS and of most Unix file systems.</summary>
    private const int MaxSegmentLength = 255;

    /// <summary>Both separators Windows accepts; Unix paths only use the second.</summary>
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>
    /// Words that turn a segment with spaces into a clause ("C:\temp is read/write", "System32 contains
    /// drivers"). Only verbs that practically never appear in folder or file names: a word here refuses the
    /// path even inside quotes, so ordinary words ("have", "do", "be") stay out to keep "nice to have" a name.
    /// </summary>
    private static readonly FrozenSet<string> ClauseWords = new[]
    {
        "is", "are", "was", "were", "has", "does", "will", "would", "should", "could", "must",
        "isn't", "aren't", "wasn't", "weren't", "hasn't", "doesn't", "don't", "didn't", "can't", "cannot",
        "won't", "wouldn't", "shouldn't", "couldn't",
        "contains", "includes", "stores", "shows", "means", "refers", "exists",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Lowercase words titles keep lowercase ("Call of Duty", "Lord of the Rings", "Tools for Windows"); allowed
    /// between name words of a final folder name with spaces, never as its last word ("C:\Games\Call of").
    /// </summary>
    private static readonly FrozenSet<string> TitleConnectors = new[]
    {
        "of", "the", "and", "a", "an", "for", "in", "on", "at", "to", "by", "with", "from",
        "de", "la", "le", "les", "el", "du", "des", "del", "da", "di", "van", "von", "der", "den", "und", "et", "y",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Well-known file names without an extension, accepted as the file name that proves a relative path
    /// (<c>src/Makefile</c>, <c>docs/LICENSE</c>). Case-sensitive: they are always written this way.
    /// </summary>
    private static readonly FrozenSet<string> BareFileNames = new[]
    {
        "Makefile", "makefile", "GNUmakefile", "Dockerfile", "Containerfile", "Jenkinsfile", "Vagrantfile",
        "Procfile", "Gemfile", "Rakefile", "Brewfile", "Podfile", "Justfile", "justfile", "Snakefile", "Caddyfile",
        "LICENSE", "LICENCE", "COPYING", "README", "CHANGELOG", "AUTHORS", "CONTRIBUTORS", "CODEOWNERS", "NOTICE",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Technology names spelled like file names. "React/Next.js" or "C#/ASP.NET" in a job post has exactly the
    /// shape of <c>folder/file.cs</c>; a two-segment path ending in one of these is refused when its first
    /// segment is a name too (capitalized, camel-cased or listed). Case-sensitive, so <c>src/node.js</c> and
    /// <c>src/Node.js</c> (a lowercase folder) still count.
    /// </summary>
    private static readonly FrozenSet<string> TechnologyNames = new[]
    {
        "Node.js", "Next.js", "Nuxt.js", "Vue.js", "React.js", "Express.js", "Nest.js", "Ember.js", "Backbone.js",
        "Angular.js", "Three.js", "D3.js", "Chart.js", "Alpine.js", "Solid.js", "Svelte.js", "Deno.js", "Bun.js",
        "Moment.js", "Day.js", "Pixi.js", "Phaser.js", "Knockout.js", "Meteor.js", "Koa.js", "Hapi.js", "Sails.js",
        "Mocha.js", "Electron.js", "Vite.js", "Socket.IO", "Socket.io", "Cypress.io",
        "ASP.NET", "VB.NET", "ADO.NET", "ML.NET",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// LaTeX commands that chain like a backslash path ("\alpha\beta\gamma", "\left\right"); one of them as a
    /// segment refuses a <c>\a\b</c> path. A drive or a quote in front still makes it a path.
    /// </summary>
    private static readonly FrozenSet<string> LatexCommands = new[]
    {
        "alpha", "beta", "gamma", "delta", "epsilon", "varepsilon", "zeta", "eta", "theta", "vartheta", "iota",
        "kappa", "lambda", "mu", "nu", "xi", "pi", "rho", "sigma", "tau", "upsilon", "phi", "varphi", "chi", "psi",
        "omega", "Gamma", "Delta", "Theta", "Lambda", "Xi", "Pi", "Sigma", "Phi", "Psi", "Omega",
        "frac", "dfrac", "sqrt", "sum", "prod", "int", "oint", "lim", "infty", "partial", "nabla", "cdot", "cdots",
        "ldots", "times", "pm", "leq", "geq", "neq", "approx", "equiv", "left", "right", "big", "Big", "bigg",
        "mathbb", "mathcal", "mathrm", "mathbf", "textbf", "textit", "emph", "begin", "end", "item", "section",
        "subsection", "label", "ref", "cite", "hline", "newline", "quad", "qquad", "hspace", "vspace", "forall",
        "exists", "subset", "cup", "cap", "rightarrow", "leftarrow", "Rightarrow", "Leftarrow", "overline", "hat",
        "vec", "tilde",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>How a path starts, which decides how much evidence the rest of it must bring.</summary>
    private enum Anchor
    {
        /// <summary><c>C:\</c> or <c>C:/</c>.</summary>
        Drive,

        /// <summary><c>\\server\share</c>.</summary>
        Unc,

        /// <summary><c>\\?\…</c> or <c>\\.\…</c>.</summary>
        Device,

        /// <summary><c>~/</c> or <c>~\</c>.</summary>
        Home,

        /// <summary><c>./</c>, <c>.\</c>, <c>../</c> or <c>..\</c>.</summary>
        Dot,

        /// <summary><c>%NAME%\</c>, <c>$env:NAME\</c> or <c>${NAME}/</c>.</summary>
        Variable,

        /// <summary><c>/a/b</c> — weak: slash commands, switches and URL routes start the same way.</summary>
        Root,

        /// <summary><c>\a\b</c> — weak: escapes, regular expressions and LaTeX start the same way.</summary>
        BackslashRoot,

        /// <summary>No anchor at all (<c>folder/file.cs</c>) — the weakest form.</summary>
        None,
    }

    /// <summary>
    /// Returns the paths when <paramref name="text"/> consists of nothing but paths, in order and without their
    /// quotes; an empty list when it is anything else.
    /// </summary>
    /// <param name="text">Candidate text, e.g. a clip's plain text; <see langword="null"/> is allowed.</param>
    /// <returns>The paths (at least one), or an empty list for "not paths".</returns>
    public static IReadOnlyList<string> GetPaths(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
        {
            return [];
        }

        var paths = new List<string>();
        // EnumerateLines splits at every line ending (CRLF, LF, CR, NEL, LS, PS, FF), so a list copied from any
        // app or terminal reads the same.
        foreach (var rawLine in text.AsSpan().EnumerateLines())
        {
            var line = rawLine.Trim();
            if (!line.IsEmpty && !TryReadLine(line.ToString(), paths))
            {
                // One line that is not paths makes the text prose that happens to mention paths.
                return [];
            }
        }

        return paths;
    }

    /// <summary>
    /// Counts the paths when <paramref name="text"/> consists of nothing but paths (see <see cref="GetPaths"/>).
    /// </summary>
    /// <param name="text">Candidate text; <see langword="null"/> is allowed.</param>
    /// <returns>The number of paths, or 0 when the text is not just paths.</returns>
    public static int CountPaths(string? text) => GetPaths(text).Count;

    /// <summary>
    /// Reads one trimmed, non-empty line as one path, or as several separated by tabs, appending them to
    /// <paramref name="paths"/> only when the whole line is paths.
    /// </summary>
    /// <param name="line">The line, without surrounding whitespace.</param>
    /// <param name="paths">Receives the line's paths on success; untouched on failure.</param>
    /// <returns>Whether the line is nothing but paths.</returns>
    private static bool TryReadLine(string line, List<string> paths)
    {
        // One path per line, spaces included ("C:\Program Files\x.txt"). A space never separates two paths:
        // that is a command line ("./lint.sh src/a.sh"), whose executable and arguments are paths too.
        if (TryReadPath(line, out var single))
        {
            paths.Add(single);
            return true;
        }

        // A tab does: cells of a row copied from a table or a spreadsheet.
        var cells = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return cells.Length > 1 && TryReadAll(cells, paths);
    }

    /// <summary>Reads every piece as a path; all or nothing.</summary>
    /// <param name="pieces">Candidate pieces of one line.</param>
    /// <param name="paths">Receives all of them when every piece is a path; untouched otherwise.</param>
    /// <returns>Whether every piece is a path.</returns>
    private static bool TryReadAll(IReadOnlyList<string> pieces, List<string> paths)
    {
        var found = new string[pieces.Count];
        for (int i = 0; i < pieces.Count; i++)
        {
            if (!TryReadPath(pieces[i], out found[i]))
            {
                return false;
            }
        }

        paths.AddRange(found);
        return true;
    }

    /// <summary>Reads one piece (a whole line, or one cell of it) as a single path.</summary>
    /// <param name="piece">Non-empty text without surrounding whitespace, possibly quoted.</param>
    /// <param name="path">The path without its quotes; empty on failure.</param>
    /// <returns>Whether the piece is exactly one path.</returns>
    private static bool TryReadPath(string piece, out string path)
    {
        path = string.Empty;
        if (!TryUnquote(piece, out var candidate, out bool quoted) || candidate.Length > MaxPathLength || !IsPath(candidate, quoted))
        {
            return false;
        }

        path = candidate;
        return true;
    }

    /// <summary>Removes one pair of matching quotes around the whole piece.</summary>
    /// <param name="piece">Non-empty candidate.</param>
    /// <param name="inner">The content inside the quotes (trimmed), or the piece itself when it is not quoted.</param>
    /// <param name="quoted">Whether quotes were removed.</param>
    /// <returns><see langword="false"/> when an opening quote has no partner at the end, or the content holds the delimiters again.</returns>
    private static bool TryUnquote(string piece, out string inner, out bool quoted)
    {
        inner = piece;
        quoted = false;
        if (ClosingQuote(piece[0]) is not char close)
        {
            return true;
        }

        // An opening quote needs its partner at the very end; otherwise it is a quoted word inside prose.
        if (piece.Length < 3 || piece[^1] != close)
        {
            return false;
        }

        inner = piece[1..^1].Trim();
        quoted = true;

        // The delimiters inside again would mean nested or adjacent quoted strings ("'a' 'b'"), not one path.
        return inner.Length > 0 && inner.IndexOf(piece[0]) < 0 && inner.IndexOf(close) < 0;
    }

    /// <summary>The closing partner of an opening quote character.</summary>
    /// <param name="c">Candidate opening quote.</param>
    /// <returns>The closing character, or <see langword="null"/> when <paramref name="c"/> does not open a quote.</returns>
    private static char? ClosingQuote(char c) => c switch
    {
        '"' => '"',
        '\'' => '\'',
        '`' => '`',
        '\u201C' => '\u201D', // “ … ”
        '\u2018' => '\u2019', // ‘ … ’
        _ => null,
    };

    /// <summary>Decides whether an unquoted candidate is one path, dispatching on how it starts.</summary>
    /// <param name="p">The candidate (non-empty, trimmed, quotes removed).</param>
    /// <param name="quoted">Whether it was quoted, which makes spaces unambiguous.</param>
    /// <returns>Whether it is a path.</returns>
    private static bool IsPath(string p, bool quoted)
    {
        // "\\?\" holds the one '?' a path may contain, so the character check starts after the device prefix.
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return HasOnlyPathCharacters(p[4..]) && CheckSegments(p[4..], Anchor.Device, quoted);
        }

        if (!HasOnlyPathCharacters(p))
        {
            return false;
        }

        if (p.StartsWith("file:/", StringComparison.OrdinalIgnoreCase))
        {
            return IsFileUri(p);
        }

        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return IsUncPath(p[2..], quoted);
        }

        if (p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && IsSeparator(p[2]))
        {
            return CheckSegments(p[3..], Anchor.Drive, quoted);
        }

        if (p.Length >= 2 && p[0] == '~' && IsSeparator(p[1]))
        {
            return CheckSegments(p[2..], Anchor.Home, quoted);
        }

        if (p.Length >= 2 && p[0] == '.' && IsSeparator(p[1]))
        {
            return CheckSegments(p[2..], Anchor.Dot, quoted);
        }

        if (p.Length >= 3 && p[0] == '.' && p[1] == '.' && IsSeparator(p[2]))
        {
            return CheckSegments(p[3..], Anchor.Dot, quoted);
        }

        if (VariablePrefix().Match(p) is { Success: true } variable)
        {
            return CheckSegments(p[variable.Length..], Anchor.Variable, quoted);
        }

        return p[0] switch
        {
            '/' => CheckSegments(p[1..], Anchor.Root, quoted),
            '\\' => CheckSegments(p[1..], Anchor.BackslashRoot, quoted),
            _ => CheckSegments(p, Anchor.None, quoted),
        };
    }

    /// <summary>
    /// Whether every character may appear in a path as people copy it: no character Windows forbids in names
    /// (<c>&lt; &gt; " | ? *</c>), no backtick or curly double quote (both mark prose or code), no control or
    /// invisible formatting character, and no whitespace other than the plain space.
    /// </summary>
    /// <param name="p">The candidate.</param>
    /// <returns>Whether all characters are acceptable (positions of <c>:</c> and spaces are checked later).</returns>
    private static bool HasOnlyPathCharacters(string p)
    {
        for (int i = 0; i < p.Length; i++)
        {
            char c = p[i];
            if (char.IsHighSurrogate(c) && i + 1 < p.Length && char.IsLowSurrogate(p[i + 1]))
            {
                // An emoji or a supplementary-plane letter: legal in names.
                i++;
                continue;
            }

            if (c is '<' or '>' or '"' or '|' or '?' or '*' or '`' or '\u201C' or '\u201D')
            {
                return false;
            }

            switch (char.GetUnicodeCategory(c))
            {
                // Tabs and line breaks (Control), bidi marks and zero-width characters (Format), and a lone half
                // of a surrogate pair never belong to a path someone means to copy.
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.Surrogate:
                    return false;

                // No-break, thin and ideographic spaces come from formatted documents, not from file names.
                case UnicodeCategory.SpaceSeparator when c != ' ':
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a candidate starting with <c>file:/</c> is a file URI with a path (<c>file:///C:/a.txt</c>,
    /// <c>file://server/share</c>).
    /// </summary>
    /// <param name="p">The candidate.</param>
    /// <returns>Whether it parses as a <c>file:</c> URI with more than a bare root.</returns>
    private static bool IsFileUri(string p)
    {
        // A URI writes a space as %20: whitespace means words follow the URI.
        if (p.Any(char.IsWhiteSpace))
        {
            return false;
        }

        return Uri.TryCreate(p, UriKind.Absolute, out var uri) && uri.IsFile && uri.AbsolutePath.Length > 1;
    }

    /// <summary>Checks <c>\\server\share[\…]</c> after the leading <c>\\</c>.</summary>
    /// <param name="rest">Everything after the two leading backslashes.</param>
    /// <param name="quoted">Whether the path was quoted.</param>
    /// <returns>Whether it is a UNC path with a server and a share.</returns>
    private static bool IsUncPath(string rest, bool quoted)
    {
        // A server of at least two characters followed by a share: "\\server" alone is rarely copied, and a
        // one-letter "server" is how an escaped "\n" or "\t" in source code starts ("\\n\\t").
        int separator = rest.IndexOfAny(Separators);
        if (separator < 2 || separator > MaxSegmentLength)
        {
            return false;
        }

        for (int i = 0; i < separator; i++)
        {
            char c = rest[i];
            if (!char.IsLetterOrDigit(c) && c is not ('.' or '-' or '_' or '$' or '@'))
            {
                return false;
            }
        }

        return CheckSegments(rest[(separator + 1)..], Anchor.Unc, quoted);
    }

    /// <summary>
    /// Splits what follows the anchor into segments, validates each one (characters, spaces, words) and then
    /// asks for the evidence this anchor needs.
    /// </summary>
    /// <param name="rest">Everything after the anchor (the whole candidate for <see cref="Anchor.None"/>).</param>
    /// <param name="anchor">How the candidate starts.</param>
    /// <param name="quoted">Whether the candidate was quoted.</param>
    /// <returns>Whether the candidate is a path.</returns>
    private static bool CheckSegments(string rest, Anchor anchor, bool quoted)
    {
        // Strong anchors may also carry spaces and doubled separators ("C:\\Users\\me" escaped in JSON or C#);
        // in the weak forms a doubled separator means a URL ("//cdn/x.js"), a comment or an escape.
        bool strong = anchor is not (Anchor.Root or Anchor.BackslashRoot or Anchor.None);
        var parts = rest.Split(Separators);
        bool trailing = parts.Length > 1 && parts[^1].Length == 0;
        var segments = new List<string>(parts.Length);
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
            {
                segments.Add(parts[i]);
            }
            else if (i < parts.Length - 1 && !strong)
            {
                return false;
            }
        }

        for (int i = 0; i < segments.Count; i++)
        {
            // The first segment of /a/b is where a slash command ends and its arguments begin ("/load-file
            // docs/a.md"); real top-level folders have no spaces, quoted or not. The first segment of a bare
            // relative path is where a command's name ends ("python scripts/build.py", often backticked), so
            // it may hold spaces only quoted and must then still read like a name ('New folder/a.txt').
            bool spacesAllowed = anchor switch
            {
                Anchor.Root => i > 0,
                Anchor.None => quoted,
                _ => quoted || strong,
            };
            if (!IsValidSegment(segments[i], spacesAllowed, driveAllowed: anchor == Anchor.Device && i == 0))
            {
                return false;
            }

            bool wordsAsQuoted = quoted && !(anchor == Anchor.None && i == 0);
            if (segments[i].Contains(' ') && !AreWordsPlausible(segments[i], wordsAsQuoted))
            {
                return false;
            }
        }

        return anchor switch
        {
            // "C:\" and "%TEMP%\" are folders themselves.
            Anchor.Drive or Anchor.Variable => true,
            Anchor.Unc or Anchor.Device or Anchor.Home or Anchor.Dot => segments.Count > 0,
            Anchor.Root => HasRootEvidence(segments, trailing),
            Anchor.BackslashRoot => HasBackslashRootEvidence(segments, trailing),
            _ => HasRelativeEvidence(segments, trailing),
        };
    }

    /// <summary>Validates one non-empty segment on its own.</summary>
    /// <param name="segment">The segment.</param>
    /// <param name="spacesAllowed">Whether this form of path may have spaces in this segment.</param>
    /// <param name="driveAllowed">Whether the segment may be a drive (<c>C:</c>), as in <c>\\?\C:\…</c>.</param>
    /// <returns>Whether the segment can be part of a path.</returns>
    private static bool IsValidSegment(string segment, bool spacesAllowed, bool driveAllowed)
    {
        if (segment.Length > MaxSegmentLength)
        {
            return false;
        }

        if (segment is "." or "..")
        {
            return true;
        }

        // Windows strips trailing dots, so "..." and "name." are not names; and a name never ends with sentence
        // punctuation ("see C:\x.txt.", "C:\a.txt, C:\b.txt") or with '=' (an assignment: "&path=/src/x.cs").
        char last = segment[^1];
        if (last is '.' or ',' or ';' or '=')
        {
            return false;
        }

        // ':' is a drive or a stream ("file.txt:Zone.Identifier", "user@host:dir"), never part of a name.
        int colon = segment.IndexOf(':');
        if (colon >= 0 && !(driveAllowed && segment.Length == 2 && colon == 1 && char.IsAsciiLetter(segment[0])))
        {
            return false;
        }

        // Single spaces inside the segment only: a space next to a separator ("C:\a / D:\b") or a doubled one
        // is how prose places paths.
        return !segment.Contains(' ')
            || (spacesAllowed && segment[0] != ' ' && last != ' ' && !segment.Contains("  ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Checks the words of a segment that contains spaces, folder or file alike (see the class remarks): whether
    /// they can be a name rather than a sentence or the arguments of a command.
    /// </summary>
    /// <param name="segment">A valid segment with single inner spaces.</param>
    /// <param name="quoted">
    /// Whether quotes vouch for the segment: then the words need not look like a name ("my stuff"), and only
    /// options, file names before more words and verbs refuse it.
    /// </param>
    /// <returns>Whether the words can be a name.</returns>
    private static bool AreWordsPlausible(string segment, bool quoted)
    {
        var words = segment.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            var word = words[i];

            // An option means a command line ("./venv/bin/pip install -r reqs/dev.txt"); a complete file name
            // before more words means a command or a sentence about that file ("./lint.sh src/a.sh",
            // "C:\x.exe arg", "file.txt is here", "~/.bashrc (user)"); a verb means a clause ("System32 contains
            // drivers"). None of these is ever part of a name, quoted or not.
            if (IsOption(word)
                || (i < words.Length - 1 && (HasExtension(word) || IsDotFile(word)))
                || (i > 0 && ClauseWords.Contains(word)))
            {
                return false;
            }
        }

        if (quoted || NewFolder().IsMatch(segment))
        {
            return true;
        }

        // Unquoted, lowercase words after the first are what separates a sentence or arguments ("temp is here",
        // "pip install server", "uv run main.py") from a name ("Program Files", "New Text Document.txt").
        for (int i = 1; i < words.Length; i++)
        {
            if (!IsNameWord(words[i]) && !TitleConnectors.Contains(words[i]))
            {
                return false;
            }
        }

        // A connector or a lone symbol at the end belongs to a cut-off phrase ("C:\Games\Call of", "C:\Tools -").
        var lastWord = words[^1];
        return !TitleConnectors.Contains(lastWord) && !(lastWord.Length == 1 && !char.IsLetterOrDigit(lastWord[0]));
    }

    /// <summary>Whether a word is a command-line option: <c>-r</c>, <c>-rf</c>, <c>--force</c>.</summary>
    /// <param name="word">A non-empty word.</param>
    /// <returns><see langword="true"/> for one or two dashes followed by a letter; a lone "-" (as in "OneDrive - Personal") is no option.</returns>
    private static bool IsOption(string word) =>
        word.Length >= 2 && word[0] == '-'
        && (char.IsLetter(word[1]) || (word[1] == '-' && word.Length > 2 && char.IsLetter(word[2])));

    /// <summary>
    /// Whether a word reads as part of a name: capitalized, a number, a symbol ("-", "&amp;", "#2"), brand-cased
    /// ("iPhone", "macOS"), a version ("v2"), or in brackets with a name inside ("(x86)", "(2)", "[Beta]" — but
    /// not "(copy)" or "(user)", which are remarks).
    /// </summary>
    /// <param name="word">A non-empty word.</param>
    /// <returns><see langword="false"/> for ordinary lowercase words and for words of scripts without case.</returns>
    private static bool IsNameWord(string word)
    {
        if (word[0] is '(' or '[' or '{')
        {
            var inside = word.AsSpan(1).TrimEnd(")]}");
            return inside.IsEmpty || char.IsUpper(inside[0]) || inside.IndexOfAnyInRange('0', '9') >= 0;
        }

        char first = word[0];
        return char.IsUpper(first) || char.IsDigit(first) || !char.IsLetter(first)
            || word.Any(char.IsUpper)
            || VersionWord().IsMatch(word);
    }

    /// <summary>Evidence for <c>/a/b</c> (see the class remarks).</summary>
    /// <param name="segments">The segments after the leading slash.</param>
    /// <param name="trailing">Whether the path ends with a separator.</param>
    /// <returns>Whether the segments make it a path.</returns>
    private static bool HasRootEvidence(List<string> segments, bool trailing)
    {
        // One segment is a slash command or a switch ("/help", "/s"); digits only are dates or fractions.
        if (segments.Count < 2 || segments.TrueForAll(IsDigitsOnly))
        {
            return false;
        }

        if (segments.Count == 2)
        {
            string first = segments[0], last = segments[1];

            // "/r/programming", "/u/name": a single-letter root needs a file name ("/c/notes.txt" in Git Bash).
            if (first.Length == 1 && char.IsAsciiLetter(first[0]) && (trailing || !HasFileName(last)))
            {
                return false;
            }

            // "/abc/gi": a JavaScript regular expression with its flags.
            if (!trailing && RegexFlags().IsMatch(last))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Evidence for <c>\a\b</c> (see the class remarks).</summary>
    /// <param name="segments">The segments after the leading backslash.</param>
    /// <param name="trailing">Whether the path ends with a separator.</param>
    /// <returns>Whether the segments make it a path.</returns>
    private static bool HasBackslashRootEvidence(List<string> segments, bool trailing)
    {
        if (segments.Count < 2)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            // Single letters are regex classes and escapes ("\d\w", "\n\t"); LaTeX chains its commands.
            if (segment.Length < 2 || LatexCommands.Contains(segment))
            {
                return false;
            }
        }

        if (segments.TrueForAll(segment => EscapeSequence().IsMatch(segment)))
        {
            return false;
        }

        return segments.Count >= 3 || (!trailing && HasFileName(segments[^1]));
    }

    /// <summary>Evidence for a path without an anchor (see the class remarks).</summary>
    /// <param name="segments">All segments.</param>
    /// <param name="trailing">Whether the path ends with a separator.</param>
    /// <returns>Whether the segments make it a path.</returns>
    private static bool HasRelativeEvidence(List<string> segments, bool trailing)
    {
        // Only a file name at the end tells folder/file.cs from "and/or", "24/7", "TCP/IP" or "src/".
        if (segments.Count < 2 || trailing || !HasFileName(segments[^1]))
        {
            return false;
        }

        // "example.com/index.html", "192.168.1.1/admin.php": a URL without its scheme. "PY=/c/x.exe",
        // "--config=src/a.json", "-Iinclude/x.h": an assignment or an option in front of the path.
        var first = segments[0];
        if (LooksLikeHost(first) || first.Contains('=') || first[0] == '-')
        {
            return false;
        }

        foreach (var segment in segments)
        {
            // "Ph.D/M.Sc", "B.A/M.A": degrees and abbreviations have the dots of file names.
            if (Abbreviation().IsMatch(segment))
            {
                return false;
            }
        }

        // Two file names around a slash ("self.x/self.y", "a.b/c.d") divide one value by another; a folder
        // named like a file is rare, and it is never alone in front of the file (BetterClipboard.Core/x.cs
        // still counts: "Core" is no extension).
        return !(segments.Count == 2 && (HasExtension(segments[0]) || IsTechnologyPair(segments[0], segments[1])));
    }

    /// <summary>Whether a final segment is a file name: an extension, a dotfile or a well-known bare name.</summary>
    /// <param name="name">The segment.</param>
    /// <returns><see langword="true"/> for <c>a.cs</c>, <c>.gitignore</c>, <c>Makefile</c>.</returns>
    private static bool HasFileName(string name) => HasExtension(name) || IsDotFile(name) || BareFileNames.Contains(name);

    /// <summary>
    /// Whether a name ends in a file extension: a dot after a non-empty stem, then 1–16 ASCII letters, digits,
    /// <c>_</c> or <c>-</c> starting with a letter or digit, containing a letter, and in one case — so "1.5",
    /// "v2.0" and "e.g." have none, nor do member accesses such as "items.Count" or "obj.toString", while
    /// <c>.cs</c>, <c>.7z</c>, <c>.JPG</c> and <c>.code-workspace</c> count.
    /// </summary>
    /// <param name="name">A segment or a word.</param>
    /// <returns>Whether it has an extension.</returns>
    private static bool HasExtension(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1 || name.Length - dot - 1 > 16 || !char.IsAsciiLetterOrDigit(name[dot + 1]))
        {
            return false;
        }

        bool lower = false, upper = false;
        foreach (char c in name.AsSpan(dot + 1))
        {
            if (char.IsAsciiLetterLower(c))
            {
                lower = true;
            }
            else if (char.IsAsciiLetterUpper(c))
            {
                upper = true;
            }
            else if (!char.IsAsciiDigit(c) && c is not ('_' or '-'))
            {
                return false;
            }
        }

        // Extensions are written in one case (.txt, .JPG); "Count", "Length" and "toString" are members of code.
        return lower != upper;
    }

    /// <summary>
    /// Whether a name is a dotfile (<c>.gitignore</c>, <c>.env</c>, <c>.DS_Store</c>): a dot, then at least two
    /// ASCII letters, digits, dots, <c>_</c> or <c>-</c>, with a lowercase letter — "C#/.NET" writes the
    /// technology in capitals, real dotfiles never are all caps.
    /// </summary>
    /// <param name="name">A segment.</param>
    /// <returns>Whether it is a dotfile name.</returns>
    private static bool IsDotFile(string name)
    {
        if (name.Length < 3 || name[0] != '.' || name[1] == '.')
        {
            return false;
        }

        bool lower = false;
        foreach (char c in name.AsSpan(1))
        {
            if (char.IsAsciiLetterLower(c))
            {
                lower = true;
            }
            else if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return lower;
    }

    /// <summary>Whether a segment consists of ASCII digits only.</summary>
    /// <param name="segment">A segment.</param>
    /// <returns>Whether every character is 0–9.</returns>
    private static bool IsDigitsOnly(string segment) => segment.All(char.IsAsciiDigit);

    /// <summary>Whether the first segment of a relative path is a host: <c>localhost</c>, an IPv4 address or a lowercase domain with a common TLD.</summary>
    /// <param name="segment">The first segment.</param>
    /// <returns>Whether it names a host rather than a folder.</returns>
    private static bool LooksLikeHost(string segment) =>
        segment == "localhost" || IPv4Address().IsMatch(segment) || HostName().IsMatch(segment);

    /// <summary>Whether a two-segment relative path is a pair of technology names (see <see cref="TechnologyNames"/>).</summary>
    /// <param name="first">The first segment.</param>
    /// <param name="last">The second segment.</param>
    /// <returns>Whether it reads as "this or that technology".</returns>
    private static bool IsTechnologyPair(string first, string last) =>
        TechnologyNames.Contains(last) && (TechnologyNames.Contains(first) || first.Any(char.IsUpper));

    /// <summary>Whether a character separates path segments.</summary>
    /// <param name="c">The character.</param>
    /// <returns><see langword="true"/> for <c>\</c> and <c>/</c>.</returns>
    private static bool IsSeparator(char c) => c is '\\' or '/';

    /// <summary>
    /// A variable anchor including its separator: <c>%NAME%</c> (Windows, parentheses allowed for
    /// <c>%ProgramFiles(x86)%</c>), <c>$env:NAME</c> (PowerShell) or <c>${NAME}</c>/<c>${env:NAME}</c>
    /// (shells, VS Code).
    /// </summary>
    /// <returns>The compiled expression.</returns>
    [GeneratedRegex(@"^(?:%[A-Za-z_][A-Za-z0-9_.()-]*%|\$(?i:env):[A-Za-z_][A-Za-z0-9_]*|\$\{(?:(?i:env):)?[A-Za-z_][A-Za-z0-9_.()-]*\})[\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex VariablePrefix();

    /// <summary>The flags of a JavaScript regular-expression literal (<c>g</c>, <c>gi</c>, <c>imsu</c>).</summary>
    /// <returns>The compiled expression.</returns>
    [GeneratedRegex("^[dgimsuvy]{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex RegexFlags();

    /// <summary>A segment that is an escape sequence without its backslash (<c>x41</c>, <c>u00e9</c>, <c>U0001F600</c>, <c>012</c>).</summary>
    /// <returns>The compiled expression.</returns>
    [GeneratedRegex("^(?:x[0-9A-Fa-f]{2,4}|u[0-9A-Fa-f]{4}|U[0-9A-Fa-f]{8}|[0-7]{2,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex EscapeSequence();

    /// <summary>A dotted abbreviation: capitalized groups of one to three letters joined by dots (<c>Ph.D</c>, <c>M.Sc</c>, <c>U.S</c>).</summary>
    /// <returns>The compiled expression.</returns>
    [GeneratedRegex(@"^(?:\p{Lu}\p{Ll}{0,2}\.)+\p{Lu}\p{Ll}{0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex Abbreviation();

    /// <summary>A lowercase domain name ending in a common top-level domain (<c>example.com</c>, <c>cdn.jsdelivr.net</c>).</summary>
    /// <returns>The compiled expression.</returns>
    [GeneratedRegex("^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)+(?:com|net|org|io|dev|app|ai|co|info|biz|edu|gov|uk|de|fr|il|ru|cn|jp|br|nl|eu|us|ca|au|xyz|me|tv|ly|gg|site|online|cloud|tech)$", RegexOptions.CultureInvariant)]
    private static partial Regex HostName();

    /// <summary>A dotted IPv4 address.</summary>
    /// <returns>The compiled expression.</returns>
    [GeneratedRegex("^[0-9]{1,3}(?:\\.[0-9]{1,3}){3}$", RegexOptions.CultureInvariant)]
    private static partial Regex IPv4Address();

    /// <summary>Windows' name for a new folder, numbered when taken: <c>New folder</c>, <c>New folder (2)</c>.</summary>
    /// <returns>The compiled expression.</returns>
    [GeneratedRegex("^New folder(?: \\([0-9]+\\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex NewFolder();

    /// <summary>A version word: <c>v</c> followed by digits and dots (<c>v2</c>, <c>v1.0.3</c>).</summary>
    /// <returns>The compiled expression.</returns>
    [GeneratedRegex("^v[0-9][0-9.]*$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionWord();
}
