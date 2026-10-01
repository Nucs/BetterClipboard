using System.Text;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for <see cref="PathDetector"/>: every form of path it recognizes, the prose, code, URLs and commands
/// it must never take for paths (precision first — a sentence in the Files tab is the mistake to avoid), its
/// whitespace rules, the length limit, and how the classifier hands its verdict on.
/// </summary>
public sealed class PathDetectorTests
{
    /// <summary>Paths in every supported form are recognized, with the number of paths in the text.</summary>
    /// <param name="text">The copied text.</param>
    /// <param name="expected">How many paths it holds.</param>
    [Theory]
    // The request's examples and their backslash variants.
    [InlineData("folder/file.cs", 1)]
    [InlineData("C:/a/path", 1)]
    [InlineData("/var/path/file", 1)]
    [InlineData(@"folder\file.cs", 1)]
    [InlineData(@"C:\a\path", 1)]
    [InlineData(@"\var\path\file", 1)]
    // Drives: roots, forward slashes, escaped doubled separators, 8.3 names, Hebrew, trailing separator.
    [InlineData(@"C:\", 1)]
    [InlineData("C:/", 1)]
    [InlineData(@"c:\temp", 1)]
    [InlineData(@"C:\\Users\\me\\file.txt", 1)]
    [InlineData(@"C:\PROGRA~1\foo", 1)]
    [InlineData(@"C:\Users\me\מסמכים\קובץ.txt", 1)]
    [InlineData(@"C:\Users\me\AppData\Local\BetterClipboard\stores\{id}\history.db", 1)]
    [InlineData(@"C:\Users\me\Desktop\", 1)]
    // UNC, device and file URIs.
    [InlineData(@"\\server\share", 1)]
    [InlineData(@"\\wsl$\Ubuntu\home\me", 1)]
    [InlineData(@"\\wsl.localhost\Ubuntu-24.04\home", 1)]
    [InlineData(@"\\?\C:\Windows", 1)]
    [InlineData(@"\\?\UNC\server\share\x.txt", 1)]
    [InlineData(@"\\.\pipe\name", 1)]
    [InlineData("file:///C:/Users/me/x.txt", 1)]
    [InlineData("file://server/share/x%20y.txt", 1)]
    // Home, dot and variable anchors.
    [InlineData("~/.bashrc", 1)]
    [InlineData(@"~\Documents\notes", 1)]
    [InlineData("./build.sh", 1)]
    [InlineData("../src/x.cs", 1)]
    [InlineData(@"..\..\file", 1)]
    [InlineData("../..", 1)]
    [InlineData(@"%LOCALAPPDATA%\BetterClipboard", 1)]
    [InlineData(@"%ProgramFiles(x86)%\Steam\steam.exe", 1)]
    [InlineData(@"%TEMP%\", 1)]
    [InlineData(@"$env:LOCALAPPDATA\BetterClipboard", 1)]
    [InlineData("${workspaceFolder}/src/app.ts", 1)]
    [InlineData(@"${env:USERPROFILE}\Desktop", 1)]
    // Rooted Unix paths, Git Bash drives, URL-style routes.
    [InlineData("/etc/hosts", 1)]
    [InlineData("/usr/local/bin/", 1)]
    [InlineData("/k/source/BetterClipboard", 1)]
    [InlineData("/c/notes.txt", 1)]
    [InlineData("/dev/null", 1)]
    [InlineData("/api/v1/users", 1)]
    // Rooted backslash paths with enough evidence.
    [InlineData(@"\Windows\notepad.exe", 1)]
    [InlineData(@"\Windows\System32\drivers", 1)]
    // Relative paths ending in a file name: extension, dotfile, well-known bare name.
    [InlineData("src/BetterClipboard.Core/Content/ContentClassifier.cs", 1)]
    [InlineData("BetterClipboard.Core/Program.cs", 1)]
    [InlineData(".github/workflows/ci.yml", 1)]
    [InlineData("docs/README.md", 1)]
    [InlineData("photos/2024/IMG_0001.JPG", 1)]
    [InlineData("src/.gitignore", 1)]
    [InlineData("config/.DS_Store", 1)]
    [InlineData("src/Makefile", 1)]
    [InlineData("$HOME/.bashrc", 1)]
    [InlineData("src/Node.js", 1)]
    [InlineData("pages/[id].tsx", 1)]
    [InlineData("v1.2/notes.txt", 1)]
    [InlineData("tools/MyApp.code-workspace", 1)]
    [InlineData("node_modules/lodash.merge/index.js", 1)]
    // Spaces where they cannot start a sentence.
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", 1)]
    [InlineData(@"C:\Program Files", 1)]
    [InlineData(@"C:\Program Files (x86)", 1)]
    [InlineData(@"C:\Program Files\", 1)]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Call of Duty", 1)]
    [InlineData(@"D:\Movies\The Matrix (1999)", 1)]
    [InlineData(@"C:\Users\me\OneDrive - Personal", 1)]
    [InlineData(@"C:\Users\me\Desktop\New folder", 1)]
    [InlineData(@"C:\Users\me\Desktop\New folder (2)", 1)]
    [InlineData(@"C:\Users\me\Backups\My iPhone", 1)]
    [InlineData(@"C:\Users\me\Projects\App v2", 1)]
    [InlineData(@"C:\Users\me\Desktop\New folder\notes.txt", 1)]
    [InlineData(@"C:\Users\me\Desktop\New Text Document.txt", 1)]
    [InlineData(@"C:\Users\me\Pictures\WhatsApp Image 2024-01-01 at 10.00.00.jpeg", 1)]
    [InlineData(@"C:\Users\me\Downloads\Invoice #123.pdf", 1)]
    [InlineData("/mnt/c/Program Files/x", 1)]
    [InlineData("~/Library/Application Support/Code", 1)]
    // Quotes: Explorer's "Copy as path", shells, Markdown; inside quotes lowercase names with spaces count too.
    [InlineData("\"C:\\Program Files\\x.exe\"", 1)]
    [InlineData("\"C:\\Users\\me\\my stuff\"", 1)]
    [InlineData("\"C:\\Users\\me\\Documents\\old projects\\a.cs\"", 1)]
    [InlineData("'src/my file.cs'", 1)]
    [InlineData("'New folder/file.txt'", 1)]
    [InlineData("\"My Documents/notes.txt\"", 1)]
    [InlineData("`src/a.cs`", 1)]
    [InlineData("\u201C/Volumes/Macintosh HD/x\u201D", 1)]
    // Several paths: one per line, or cells of a row separated by tabs.
    [InlineData("C:\\x\\a.txt\r\nC:\\x\\b.txt\r\n", 2)]
    [InlineData("./src/a.cs\n./src/b.cs\n\n./src/c.cs", 3)]
    [InlineData("C:\\a.txt\tC:\\b.txt", 2)]
    [InlineData("\"C:\\a b.txt\"\t\"D:\\c d.txt\"", 2)]
    [InlineData("src/a.cs \t\t src/b.cs", 2)]
    public void RecognizesPaths(string text, int expected)
    {
        Assert.Equal(expected, PathDetector.CountPaths(text));
    }

    /// <summary>Prose, code, URLs, commands and other look-alikes are never paths.</summary>
    /// <param name="text">The copied text.</param>
    [Theory]
    // Prose with slashes: no file name at the end, or no anchor.
    [InlineData("and/or")]
    [InlineData("and/or.")]
    [InlineData("1/2")]
    [InlineData("24/7")]
    [InlineData("w/o")]
    [InlineData("n/a")]
    [InlineData("km/h")]
    [InlineData("TCP/IP")]
    [InlineData("I/O")]
    [InlineData("yes/no?")]
    [InlineData("red/green/blue")]
    [InlineData("path/to/file")]
    [InlineData("his/her/their")]
    [InlineData("1.0.0/2.0.0")]
    [InlineData("v2/v3.0")]
    [InlineData("3.5/5.0")]
    [InlineData("Q3/Q4.2024")]
    [InlineData("50%/50%")]
    [InlineData("$USD/EUR")]
    [InlineData("%d/%m")]
    [InlineData("Mr./Mrs.")]
    [InlineData("U.S./U.K.")]
    [InlineData("e.g.")]
    [InlineData("כן/לא")]
    // Abbreviations, technology names and code that look like folder/file.ext.
    [InlineData("Ph.D/M.Sc")]
    [InlineData("B.A/M.A")]
    [InlineData("React/Next.js")]
    [InlineData("Deno/Node.js")]
    [InlineData("jQuery/Vue.js")]
    [InlineData("C#/ASP.NET")]
    [InlineData("C#/.NET")]
    [InlineData("total/items.Count")]
    [InlineData("x/obj.toString")]
    [InlineData("self.x/self.y")]
    // A bare file name or a single word is not a path.
    [InlineData("notes.txt")]
    [InlineData("example.com")]
    [InlineData("Hello world")]
    [InlineData("x")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    // URLs: links are links, and a host in front of a relative path is a URL without its scheme.
    [InlineData("https://example.com/a.js")]
    [InlineData("example.com/index.html")]
    [InlineData("cdn.jsdelivr.net/npm/x.js")]
    [InlineData("192.168.1.1/admin.php")]
    [InlineData("localhost/a.php")]
    [InlineData("//cdn.example.com/x.js")]
    [InlineData("/search?q=a/b")]
    [InlineData("file:///")]
    [InlineData("file: notes.txt")]
    // Slash commands, switches, Reddit names, regular-expression literals, dates.
    [InlineData("/help")]
    [InlineData("/compact")]
    [InlineData("/home-plugin:brave-search")]
    [InlineData("/load-file docs/a.md")]
    [InlineData("/loop 5m /foo")]
    [InlineData("dir /s /b")]
    [InlineData("/F /T /IM")]
    [InlineData("/r/programming")]
    [InlineData("/u/spez")]
    [InlineData("/c/Users")]
    [InlineData("/abc/gi")]
    [InlineData("/2024/01/15")]
    [InlineData("/tmp/")]
    // Markup, comments, escapes, regular expressions, LaTeX, emoticons.
    [InlineData("</div>")]
    [InlineData("// comment")]
    [InlineData("/* x */")]
    [InlineData(@"\n")]
    [InlineData(@"\\n")]
    [InlineData(@"\\n\\t")]
    [InlineData(@"\x41\x42\x43")]
    [InlineData(@"\d\w\s")]
    [InlineData(@"\w+@\w+\.\w+")]
    [InlineData(@"^\d{3}-\d{4}$")]
    [InlineData(@"\alpha\beta\gamma")]
    [InlineData(@"\left(\right)")]
    [InlineData(@"\Windows\System32")]
    [InlineData(@"\o/")]
    [InlineData(@"¯\_(ツ)_/¯")]
    [InlineData(@"a\b")]
    [InlineData(@"DOMAIN\user")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography")]
    [InlineData("@angular/core")]
    [InlineData("![](docs/images/flyout.png)")]
    // A drive alone, streams, prompts, compiler locations, scp targets.
    [InlineData("C:")]
    [InlineData(":)")]
    [InlineData(@"C:\Users\me\file.txt:Zone.Identifier")]
    [InlineData(@"PS C:\Users\me>")]
    [InlineData(@"C:\Windows\System32>")]
    [InlineData("src/x.cs(10,5): error CS1002: ; expected")]
    [InlineData("src/x.cs:10:5")]
    [InlineData("user@host:/var/log/syslog")]
    [InlineData("mcr.microsoft.com/dotnet/sdk:10.0")]
    // Sentences around paths: words before, verbs or lowercase words after, punctuation, lists.
    [InlineData(@"see C:\x.txt")]
    [InlineData(@"C:\Windows\System32 contains drivers")]
    [InlineData(@"C:\a\path and more")]
    [InlineData(@"C:\temp is where I keep stuff")]
    [InlineData(@"C:\Users\me\Documents is my folder")]
    [InlineData(@"C:\temp is read/write")]
    [InlineData("/var/log is full")]
    [InlineData("\"/var/log is full\"")]
    [InlineData("\"C:\\temp is full\"")]
    [InlineData(@"C:\Users\me\Desktop\my stuff")]
    [InlineData(@"C:\Users\me\Desktop\my stuff\")]
    [InlineData(@"C:\Users\me\Documents\old projects\a.cs")]
    [InlineData("\"my stuff/x.txt\"")]
    [InlineData(@"C:\foo bar")]
    [InlineData(@"C:\Games\Call of")]
    [InlineData(@"C:\Tools -")]
    [InlineData(@"C:\temp\a.txt and b.txt")]
    [InlineData(@"C:\temp\x.txt (copy)")]
    [InlineData(@"C:\temp\file.txt is here")]
    [InlineData(@"C:\x.txt.")]
    [InlineData(@"C:\a.txt, C:\b.txt")]
    [InlineData(@"C:\a.txt; C:\b.txt")]
    [InlineData(@"C:\x.txt # comment")]
    [InlineData(@"C:\a.txt - C:\b.txt")]
    [InlineData(@"C:\a / D:\b")]
    [InlineData("the file \"C:\\a.txt\" is gone")]
    [InlineData("- src/a.cs\n- src/b.cs")]
    [InlineData("git add src/a.cs")]
    [InlineData("src/a.cs\nThis is a sentence.")]
    [InlineData("a / b / c")]
    [InlineData("x = a/b.c")]
    // Command lines: an executable followed by arguments that are paths (all found in real documentation).
    [InlineData("C:\\a.txt D:\\b.txt")]
    [InlineData("/etc/hosts /etc/passwd")]
    [InlineData("src/a.cs src/b.cs")]
    [InlineData("\"C:\\a b.txt\" D:\\c.txt")]
    [InlineData("C:\\tools\\x.exe C:\\data\\in.txt")]
    [InlineData("./validate-hook-schema.sh hooks/hooks.json")]
    [InlineData("./hook-linter.sh ../examples/validate-write.sh")]
    [InlineData("./scripts/validate-agent.sh agents/your-agent.md")]
    [InlineData("./venv/Scripts/pip install -r server/requirements.txt")]
    [InlineData("/usr/bin/env python3 script.py")]
    [InlineData("~/.local/bin/uv run main.py")]
    [InlineData("/usr/local/bin/python3 scripts/build.py")]
    [InlineData(@"C:\Python312\python.exe -m pip install x")]
    [InlineData("`python scripts/build.py`")]
    [InlineData("`glow -p analysis/$1/ASSESSMENT.md`")]
    [InlineData("PY=/c/Users/me/.claude/python/python.exe")]
    [InlineData("&path=/src/Services/MyService.cs")]
    [InlineData("--config=src/app.json")]
    [InlineData("-Iinclude/foo.h")]
    [InlineData("~/.bashrc (user)")]
    // Characters no copied path contains: control and invisible characters, odd spaces, unbalanced quotes.
    [InlineData("C:\\a\u00A0b\\c.txt")]
    [InlineData("C:\\a\u200E\\c.txt")]
    [InlineData("\"C:\\a.txt")]
    [InlineData("'src/a.cs")]
    [InlineData("'a/b.cs' 'c/d.cs")]
    public void RejectsEverythingElse(string text)
    {
        Assert.Equal(0, PathDetector.CountPaths(text));
    }

    /// <summary>The paths come back in order, without their quotes and without surrounding whitespace.</summary>
    [Fact]
    public void GetPaths_ReturnsThePathsInOrder_WithoutQuotesOrWhitespace()
    {
        Assert.Equal(
            [@"C:\Program Files\a b.txt", "D:/x.txt", "src/a.cs"],
            PathDetector.GetPaths("  \"C:\\Program Files\\a b.txt\"\tD:/x.txt\r\n\r\n\tsrc/a.cs  \n"));
        Assert.Empty(PathDetector.GetPaths(null));
        Assert.Empty(PathDetector.GetPaths("C:\\a.txt\nnot a path"));
    }

    /// <summary>Every kind of line ending separates paths: CR, LF, CRLF, NEL, line and paragraph separators, form feed.</summary>
    [Fact]
    public void LineEndings_AllSeparatePaths()
    {
        Assert.Equal(6, PathDetector.CountPaths("C:\\a.txt\rC:\\b.txt\nC:\\c.txt\u0085C:\\d.txt\u2028C:\\e.txt\u2029C:\\f.txt"));
        Assert.Equal(2, PathDetector.CountPaths("C:\\a.txt\fC:\\b.txt"));
    }

    /// <summary>
    /// Texts up to <see cref="PathDetector.MaxTextLength"/> are examined and longer ones never are, which keeps
    /// big clips cheap and lets the store decide from the (cut) search text.
    /// </summary>
    [Fact]
    public void TextsLongerThanTheLimit_AreNeverPaths()
    {
        Assert.Equal(PathDetector.MaxTextLength, PathOfLength(PathDetector.MaxTextLength).Length);
        Assert.Equal(1, PathDetector.CountPaths(PathOfLength(PathDetector.MaxTextLength)));
        Assert.Equal(0, PathDetector.CountPaths(PathOfLength(PathDetector.MaxTextLength + 1)));
    }

    /// <summary>
    /// Only plain and rich text get a path count; the kind stays text (it pastes as text), and links, colors,
    /// file lists and prose get 0.
    /// </summary>
    [Fact]
    public void Classifier_CountsPathsOfTextOnly()
    {
        var text = ContentClassifier.Classify(TestData.Text("C:\\a\\b.txt\nD:\\c.txt"))!;
        Assert.Equal((ClipKind.Text, 2), (text.Kind, text.PathCount));

        var rich = ContentClassifier.Classify(TestData.Rich("src/app.cs", "<code>src/app.cs</code>"))!;
        Assert.Equal((ClipKind.RichText, 1), (rich.Kind, rich.PathCount));

        Assert.Equal(0, ContentClassifier.Classify(TestData.Text("BC-TEST and/or"))!.PathCount);
        Assert.Equal(0, ContentClassifier.Classify(TestData.Text("https://example.com/a.js"))!.PathCount);
        Assert.Equal(0, ContentClassifier.Classify(TestData.Text("#ff00aa"))!.PathCount);
        Assert.Equal(0, ContentClassifier.Classify(TestData.Files(@"C:\a.txt"))!.PathCount);
    }

    /// <summary>Builds a valid drive path of exactly <paramref name="length"/> characters from 200-character segments.</summary>
    /// <param name="length">Wanted length (at least 3).</param>
    /// <returns>The path.</returns>
    private static string PathOfLength(int length)
    {
        var builder = new StringBuilder("C:");
        while (builder.Length < length)
        {
            // Counted before appending the separator; a remainder of one character becomes a trailing
            // separator, which a drive path may have.
            int letters = Math.Min(200, length - builder.Length - 1);
            builder.Append('\\').Append('a', letters);
        }

        return builder.ToString();
    }
}

/// <summary>
/// Tests for paths in the store: the Files filter lists text entries that are nothing but paths next to real
/// file lists (the Text filter keeps them too), verdicts are decided for databases from before the column and
/// for rows an older build wrote, recomputed once for new detector rules, and refreshed by a new copy.
/// </summary>
public sealed class PathTextStoreTests : IDisposable
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private readonly ClipStore store;

    /// <summary>Creates a fresh store per test.</summary>
    public PathTextStoreTests()
    {
        store = new ClipStore(temp.DatabasePath);
        store.Initialize();
    }

    /// <summary>Deletes the temp database.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>Files lists file lists and path texts (newest first); Text still lists the path texts; counts are stored.</summary>
    [Fact]
    public void FilesFilter_ListsFileListsAndPathTexts()
    {
        var files = Add(TestData.Files(@"C:\BC-TEST\a.txt"));
        var path = Add(TestData.Text(@"C:\BC-TEST\b.txt", TestData.Now.AddMinutes(1)));
        var list = Add(TestData.Text("/etc/hosts\n/etc/passwd", TestData.Now.AddMinutes(2)));
        var prose = Add(TestData.Text("BC-TEST and/or", TestData.Now.AddMinutes(3)));

        Assert.Equal((0, 1, 2, 0), (files.PathCount, path.PathCount, list.PathCount, prose.PathCount));
        Assert.True(list.IsPathText);
        Assert.Equal([list.Id, path.Id, files.Id], store.Query(new ClipQuery { Filter = ClipFilter.Files }).Select(e => e.Id));
        Assert.Equal([prose.Id, list.Id, path.Id], store.Query(new ClipQuery { Filter = ClipFilter.Text }).Select(e => e.Id));
        Assert.Equal(2, store.GetEntry(list.Id)!.PathCount);
        // grep -f files scans the same slice (a file list's search text is its paths).
        Assert.Equal([list.Id, path.Id, files.Id], store.GetSearchTexts(ClipFilter.Files, 10).Select(t => t.Id));
    }

    /// <summary>
    /// A database from before the column gets it on the next Initialize and every row is decided from its search
    /// text; Initialize stays idempotent.
    /// </summary>
    [Fact]
    public void Initialize_DecidesPathsOfAnOlderDatabase()
    {
        var path = Add(TestData.Text(@"C:\BC-TEST\old.txt"));
        var prose = Add(TestData.Text("BC-TEST old prose", TestData.Now.AddMinutes(1)));
        var files = Add(TestData.Files(@"C:\BC-TEST\f.txt"));
        ExecuteRaw("ALTER TABLE clips DROP COLUMN path_count; DELETE FROM meta WHERE key LIKE 'fixup.path_text.%';");

        var reopened = Reopen();
        reopened.Initialize();
        Assert.Equal((1, 0, 0), (reopened.GetEntry(path.Id)!.PathCount, reopened.GetEntry(prose.Id)!.PathCount, reopened.GetEntry(files.Id)!.PathCount));
        Assert.Equal([path.Id, files.Id], reopened.Query(new ClipQuery { Filter = ClipFilter.Files }).Select(e => e.Id).Order());
    }

    /// <summary>
    /// Rows without a verdict (written by an older build) are decided at the next start while stored verdicts are
    /// trusted; new detector rules (a new flag) recompute every verdict once.
    /// </summary>
    [Fact]
    public void Initialize_DecidesMissingVerdicts_AndRecomputesAllForNewRules()
    {
        var path = Add(TestData.Text(@"C:\BC-TEST\x.txt"));
        var prose = Add(TestData.Text("BC-TEST prose", TestData.Now.AddMinutes(1)));
        ExecuteRaw($"UPDATE clips SET path_count = NULL WHERE id = {path.Id}; UPDATE clips SET path_count = 5 WHERE id = {prose.Id};");

        var sameRules = Reopen();
        Assert.Equal(1, sameRules.GetEntry(path.Id)!.PathCount);
        Assert.Equal(5, sameRules.GetEntry(prose.Id)!.PathCount);

        ExecuteRaw("DELETE FROM meta WHERE key LIKE 'fixup.path_text.%';");
        var newRules = Reopen();
        Assert.Equal((1, 0), (newRules.GetEntry(path.Id)!.PathCount, newRules.GetEntry(prose.Id)!.PathCount));
    }

    /// <summary>Copying the same text again stores a fresh verdict along with the fresh formats.</summary>
    [Fact]
    public void CopyingAgain_RefreshesTheVerdict()
    {
        var path = Add(TestData.Text(@"C:\BC-TEST\x.txt"));
        ExecuteRaw($"UPDATE clips SET path_count = 0 WHERE id = {path.Id};");
        var again = Add(TestData.Text(@"C:\BC-TEST\x.txt", TestData.Now.AddMinutes(1)));
        Assert.Equal((path.Id, 1), (again.Id, again.PathCount));
    }

    /// <summary>Stores a capture as a live copy.</summary>
    /// <param name="capture">The capture.</param>
    /// <returns>The stored entry.</returns>
    private ClipEntry Add(ClipCapture capture) =>
        store.Upsert(capture, ContentClassifier.Classify(capture)!, null, bumpIfExists: true)!.Entry;

    /// <summary>Opens and initializes a second store over the same file (a restart).</summary>
    /// <returns>The initialized store.</returns>
    private ClipStore Reopen()
    {
        var reopened = new ClipStore(temp.DatabasePath);
        reopened.Initialize();
        return reopened;
    }

    /// <summary>Runs SQL directly against the (plaintext test) database.</summary>
    /// <param name="sql">Statements.</param>
    private void ExecuteRaw(string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
