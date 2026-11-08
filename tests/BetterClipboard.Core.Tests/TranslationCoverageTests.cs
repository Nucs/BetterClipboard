using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Startup;
using BetterClipboard.Core.Updates;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Keeps the interface fully translatable as the code changes: every English text the code hands to UiText has a catalog
/// entry, every entry is still used, every template list (texts a component words in English and the interface
/// translates afterwards) is in the catalogs, and the pieces of the gap work read right in Chinese.
/// </summary>
/// <remarks>
/// Before these guards, a text worded in C# without its catalog entry showed in English in the Chinese interface, and
/// nothing failed: 213 such texts were found on 2026-11-08 (CLAUDE.md §2.37). The source scans read the repository's
/// <c>src</c> folder, so they run wherever the tests build.
/// </remarks>
public sealed class TranslationCoverageTests
{
    /// <summary>A fixed "now" (a Sunday) for the date checks.</summary>
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Every literal passed straight to <c>UiText.Translate</c> or <c>UiText.Format</c>, and every constant passed to them,
    /// is an English catalog entry; otherwise it would show in English in every other language.
    /// </summary>
    [Fact]
    public void EveryTextTheCodeTranslatesHasAnEntry()
    {
        var constants = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var sources = SourceFiles().Select(file => (File: file, Code: CSharpSourceScanner.StripComments(File.ReadAllText(file)))).ToList();
        foreach (var (_, code) in sources)
        {
            var literals = CSharpSourceScanner.Literals(code).ToDictionary(l => l.Start);
            foreach (Match constant in Regex.Matches(code, @"const string (\w+) = "))
            {
                if (literals.TryGetValue(constant.Index + constant.Length, out var literal))
                {
                    (constants.TryGetValue(constant.Groups[1].Value, out var values) ? values : constants[constant.Groups[1].Value] = []).Add(literal.Text);
                }
            }
        }

        var missing = new List<string>();
        foreach (var (file, code) in sources)
        {
            var literals = CSharpSourceScanner.Literals(code);
            var byStart = literals.ToDictionary(l => l.Start);
            foreach (Match call in Regex.Matches(code, @"UiText\.(Translate|Format)\("))
            {
                int open = call.Index + call.Length;
                int close = ClosingParenthesis(code, open, literals);

                // The text is the first argument, or the second one of Format(provider, $"…").
                var first = byStart.GetValueOrDefault(SkipSpace(code, open));
                var text = first ?? (call.Groups[1].Value == "Format" && TopLevelComma(code, open, close, literals) is int comma
                    ? byStart.GetValueOrDefault(SkipSpace(code, comma + 1))
                    : null);
                if (text is not null && !HasEntry(text.Text))
                {
                    missing.Add($"{Path.GetFileName(file)}: {text.Text}");
                }

                // Constants named anywhere in the arguments (outside string literals): a key hint chosen by a ternary.
                foreach (Match name in Regex.Matches(WithoutLiterals(code, open, close, literals), @"\b[A-Z]\w*\b"))
                {
                    if (constants.TryGetValue(name.Value, out var values) && !values.Any(HasEntry))
                    {
                        missing.Add($"{Path.GetFileName(file)}: {name.Value} = {values[0]}");
                    }
                }
            }
        }

        Assert.True(missing.Count == 0, "No catalog entry for:\n" + string.Join("\n", missing.Distinct()));
    }

    /// <summary>
    /// Every English entry is still used: XAML names its key, or the text is a string literal somewhere in the sources
    /// (passed to UiText, a constant, a template list). A text the code no longer has is dead weight every translator
    /// would translate for nothing.
    /// </summary>
    [Fact]
    public void EveryEntryIsStillUsed()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in SourceFiles())
        {
            foreach (var literal in CSharpSourceScanner.Literals(CSharpSourceScanner.StripComments(File.ReadAllText(file))))
            {
                used.Add(literal.Text);
            }
        }

        var xamlKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in SourceFiles("*.xaml"))
        {
            foreach (Match key in Regex.Matches(File.ReadAllText(file), @"\{loc:Tr Key=(T[0-9a-f]{12})\}"))
            {
                xamlKeys.Add(key.Groups[1].Value);
            }
        }

        var unused = UiText.English.Strings.Where(entry => !xamlKeys.Contains(entry.Key) && !used.Contains(entry.Value))
            .Select(entry => $"{entry.Key}: {entry.Value}").ToList();
        Assert.True(unused.Count == 0, "Not used anywhere (remove with tools/localization/catalog.py remove):\n" + string.Join("\n", unused));
    }

    /// <summary>
    /// Core's lists of texts that are worded in English and translated when shown: update failures, the source names
    /// BetterClipboard makes up, the group icons' names, and the third-party rows' uses and license names.
    /// </summary>
    [Fact]
    public void CoreTemplateLists_HaveEntries()
    {
        var texts = UpdateMessages.Templates
            .Concat(SourceNames.Templates)
            .Concat(GroupIconCatalog.All.Select(icon => icon.Name))
            .Concat(ThirdPartyCatalog.All.Select(project => project.Use))
            .Concat(ThirdPartyCatalog.All.Select(project => project.License).OfType<string>().Where(license => license.Contains(' ', StringComparison.Ordinal)));
        var missing = texts.Where(text => !HasEntry(text)).ToList();
        Assert.True(missing.Count == 0, "No catalog entry for:\n" + string.Join("\n", missing));
    }

    /// <summary>
    /// The group icons' names stay unique in every language: a new group takes its icon's name, and two icons with one
    /// name would give two different groups the same default name.
    /// </summary>
    [Fact]
    public void GroupIconNames_AreUniqueInEveryLanguage()
    {
        foreach (var language in UiText.Languages)
        {
            var names = GroupIconCatalog.All.Select(icon => UiText.Translate(icon.Name, language)).ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        }
    }

    /// <summary>Every catalog's date patterns are patterns .NET accepts, and give the expected dates in Chinese.</summary>
    [Fact]
    public void DatePatterns_AreValidInEveryCatalog()
    {
        var date = new DateTime(2025, 3, 12);
        foreach (var language in UiText.Languages)
        {
            Assert.False(string.IsNullOrWhiteSpace(date.ToString(UiText.Translate("d MMM", language), language.Culture)));
            Assert.False(string.IsNullOrWhiteSpace(date.ToString(UiText.Translate("d MMM yyyy", language), language.Culture)));
        }

        Assert.True(UiText.TryFind("zh-Hans", out var chinese));
        Assert.Equal("3月12日", date.ToString(UiText.Translate("d MMM", chinese), chinese.Culture));
        Assert.Equal("2025年3月12日", date.ToString(UiText.Translate("d MMM yyyy", chinese), chinese.Culture));
    }

    /// <summary>
    /// A text formatted in English from a template is translated with its parts carried over; a part is translated only
    /// when it is itself one of the templates (the release list), never a name that happens to be a catalog word.
    /// </summary>
    [Fact]
    public void TranslateFormatted_CarriesThePartsAndTranslatesOnlyListedFragments()
    {
        Assert.True(UiText.TryFind("zh-Hans", out var chinese));

        string refused = UpdateMessages.Format(UpdateMessages.ServerRefused, UpdateMessages.TheReleaseList, 404);
        Assert.Equal("The server refused the request for the release list (error 404).", refused);
        Assert.Equal("服务器拒绝了对发布列表的请求（错误 404）。", UiText.TranslateFormatted(refused, UpdateMessages.Templates, chinese));

        // "Settings" is a catalog word, but as a file name it is a part, not a fragment of the list: it stays.
        string tooLarge = UpdateMessages.Format(UpdateMessages.PackageTooLarge, "Settings");
        Assert.Equal("Settings 超出了更新允许的大小。", UiText.TranslateFormatted(tooLarge, UpdateMessages.Templates, chinese));

        // A format such as N0 was applied when the English was made; the parts come back as they were written.
        string ended = UpdateMessages.Format(UpdateMessages.DownloadEndedEarly, "BC-TEST.zip", 1234, 5678);
        Assert.Contains("BC-TEST.zip", UiText.TranslateFormatted(ended, UpdateMessages.Templates, chinese), StringComparison.Ordinal);
        Assert.Contains(1234.ToString("N0", CultureInfo.CurrentCulture), UiText.TranslateFormatted(ended, UpdateMessages.Templates, chinese), StringComparison.Ordinal);
    }

    /// <summary>English, no match, an empty text and a template without placeholders all behave plainly.</summary>
    [Fact]
    public void TranslateFormatted_EdgeCases()
    {
        Assert.True(UiText.TryFind("zh-Hans", out var chinese));
        string refused = UpdateMessages.Format(UpdateMessages.ServerRefused, "x", 500);

        Assert.Equal(refused, UiText.TranslateFormatted(refused, UpdateMessages.Templates, UiText.English));
        Assert.Equal("BC-TEST no template has this shape", UiText.TranslateFormatted("BC-TEST no template has this shape", UpdateMessages.Templates, chinese));
        Assert.Equal(string.Empty, UiText.TranslateFormatted(null, UpdateMessages.Templates, chinese));
        Assert.Equal("截图工具", UiText.TranslateFormatted("Snipping Tool", SourceNames.Templates, chinese));

        // Regex characters and braces in a template are literal text, not pattern syntax.
        Assert.Equal("PowerShell（a.b*c）", UiText.TranslateFormatted("PowerShell (a.b*c)", SourceNames.Templates, chinese));
        Assert.Throws<ArgumentNullException>(() => UiText.TranslateFormatted("x", null!, chinese));
    }

    /// <summary>
    /// Dates take the interface language's day names: an English Windows with the Chinese interface shows "周四", a
    /// Chinese Windows with the English interface "Thu"; a regional culture of the interface language keeps its own
    /// formats; the invariant culture stays English for English.
    /// </summary>
    [Fact]
    public void DateCulture_FollowsTheInterfaceLanguage()
    {
        string previous = UiText.Language;
        try
        {
            UiText.Language = "zh-Hans";
            Assert.Equal("zh-Hans", UiText.DateCulture(CultureInfo.GetCultureInfo("en-US")).Name);
            Assert.Equal("zh-CN", UiText.DateCulture(CultureInfo.GetCultureInfo("zh-CN")).Name);
            Assert.Equal("zh-Hans", UiText.DateCulture(CultureInfo.GetCultureInfo("zh-TW")).Name);
            Assert.Equal("zh-Hans", UiText.DateCulture(CultureInfo.InvariantCulture).Name);

            UiText.Language = "en";
            Assert.Equal("en-GB", UiText.DateCulture(CultureInfo.GetCultureInfo("en-GB")).Name);
            Assert.Equal("en", UiText.DateCulture(CultureInfo.GetCultureInfo("zh-CN")).Name);
            Assert.Same(CultureInfo.InvariantCulture, UiText.DateCulture(CultureInfo.InvariantCulture));
        }
        finally { UiText.Language = previous; }
    }

    /// <summary>The panel's dates in Chinese: Chinese day names and Chinese date order, whatever Windows' format is.</summary>
    [Fact]
    public void RelativeTime_InChinese_UsesChineseNamesAndPatterns()
    {
        string previous = UiText.Language;
        try
        {
            UiText.Language = "zh-Hans";
            var zone = TimeZoneInfo.Utc;
            var english = CultureInfo.GetCultureInfo("en-US");
            Assert.StartsWith("昨天 ", RelativeTimeFormatter.Format(Now.AddDays(-1), Now, zone, english), StringComparison.Ordinal);
            Assert.StartsWith("周四 ", RelativeTimeFormatter.Format(Now.AddDays(-3), Now, zone, english), StringComparison.Ordinal);
            Assert.Equal("9月29日", RelativeTimeFormatter.Format(Now.AddDays(-40), Now, zone, english));
            Assert.Equal("2025年10月4日", RelativeTimeFormatter.Format(Now.AddDays(-400), Now, zone, english));

            // bclip's English stays English to the letter.
            Assert.Equal("29 Sep", RelativeTimeFormatter.Format(Now.AddDays(-40), Now, zone, CultureInfo.InvariantCulture, localized: false));

            UiText.Language = "en";
            Assert.StartsWith("Thu ", RelativeTimeFormatter.Format(Now.AddDays(-3), Now, zone, CultureInfo.GetCultureInfo("zh-CN")), StringComparison.Ordinal);
        }
        finally { UiText.Language = previous; }
    }

    /// <summary>
    /// The forgotten list counts in whole phrases: no English noun inside a Chinese line ("23 characters" was), and the
    /// English stays as it was.
    /// </summary>
    [Fact]
    public void ForgottenText_CountsAreWholePhrases()
    {
        var item = new ForgottenItem(1, ClipKind.Text, 23, null, null, null, "Command Prompt", Now.AddMinutes(-5), 3, Now);
        var files = item with { Kind = ClipKind.Files, TextLength = null, FileCount = 1 };
        string previous = UiText.Language;
        try
        {
            UiText.Language = "zh-Hans";
            var invariant = CultureInfo.InvariantCulture;
            Assert.Equal("文本 · 23 个字符", ForgottenText.Title(item, invariant));
            Assert.Equal("1 个文件", ForgottenText.Title(files, invariant));
            string details = ForgottenText.Details(item, Now, TimeZoneInfo.Utc, invariant);
            Assert.Contains("来自 命令提示符", details, StringComparison.Ordinal);
            Assert.Contains("已拦截 3 次", details, StringComparison.Ordinal);
            Assert.DoesNotContain("time", details, StringComparison.Ordinal);

            UiText.Language = "en";
            Assert.Equal("Text · 23 characters", ForgottenText.Title(item, invariant));
            Assert.Equal("1 file", ForgottenText.Title(files, invariant));
            Assert.Contains("kept out 1 time, last just now", ForgottenText.Details(item with { BlockedCount = 1 }, Now, TimeZoneInfo.Utc, invariant), StringComparison.Ordinal);
        }
        finally { UiText.Language = previous; }
    }

    /// <summary>
    /// Only the names BetterClipboard makes up are translated where they are shown; an app's own name passes unchanged,
    /// and the stored name is untouched (the tabs filter by it).
    /// </summary>
    [Fact]
    public void SourceNames_TranslatesOnlyBetterClipboardsOwnNames()
    {
        string previous = UiText.Language;
        try
        {
            UiText.Language = "zh-Hans";
            Assert.Equal("命令提示符", SourceNames.Display("Command Prompt"));
            Assert.Equal("截图文件夹", SourceNames.Display("Screenshots folder"));
            Assert.Equal("VS Code 中的 PowerShell", SourceNames.Display("PowerShell in VS Code"));
            Assert.Equal("PowerShell（MyHost）", SourceNames.Display("PowerShell (MyHost)"));
            Assert.Equal("Notepad", SourceNames.Display("Notepad"));
            Assert.Equal("PowerShell", SourceNames.Display("PowerShell"));
            Assert.Equal("Win+PrtScn", SourceNames.Display("Win+PrtScn"));
            Assert.Equal(string.Empty, SourceNames.Display(null));
        }
        finally { UiText.Language = previous; }
    }

    /// <summary>The update dialog shows a failure in the interface language, while the problem keeps its English for the log.</summary>
    [Fact]
    public void UpdateProblems_AreShownTranslated()
    {
        var problem = new UpdateProblem(UpdateFailure.Verification, UpdateMessages.DownloadMismatch);
        string previous = UiText.Language;
        try
        {
            UiText.Language = "zh-Hans";
            Assert.StartsWith("下载内容与发布版本的校验和不符", UpdateText.ProblemText(problem), StringComparison.Ordinal);
            Assert.Equal(UpdateMessages.DownloadMismatch, problem.Message);

            UiText.Language = "en";
            Assert.Equal(UpdateMessages.DownloadMismatch, UpdateText.ProblemText(problem));
        }
        finally { UiText.Language = previous; }
    }

    /// <summary>"Start with Windows"'s description follows the interface language, its second sentence included.</summary>
    [Fact]
    public void SignInDescription_FollowsTheInterfaceLanguage()
    {
        const string Exe = @"C:\BC-TEST\BetterClipboard.exe";
        var forEveryone = SignInEntries.None with { AllUsersCommand = $"\"{Exe}\" --background --all-users-startup" };
        string previous = UiText.Language;
        try
        {
            UiText.Language = "zh-Hans";
            Assert.Equal("登录后在通知区域静默启动。", SignInStartup.Describe(SignInEntries.None, Exe));
            Assert.Equal("登录后在通知区域静默启动。已设置为此电脑的所有用户启动；关闭此项只会对你生效。", SignInStartup.Describe(forEveryone, Exe));

            UiText.Language = "en";
            Assert.Equal("Start quietly in the notification area when you sign in. It is set up to start for every user of this PC; turning this off changes that only for you.",
                SignInStartup.Describe(forEveryone, Exe));
        }
        finally { UiText.Language = previous; }
    }

    /// <summary>
    /// The names Windows' resource loader gets for a language put the region WinUI ships its text under first
    /// (<c>zh-CN</c>), then the tag; the culture is the tag's own.
    /// </summary>
    [Fact]
    public void WindowsLanguageNames_SpecificFirst()
    {
        Assert.True(UiText.TryFind("zh-Hans", out var chinese));
        Assert.Equal(new[] { "zh-CN", "zh-Hans" }, chinese.WindowsLanguageNames);
        Assert.Equal("zh-Hans", chinese.Culture.Name);
        Assert.Equal(new[] { "en-US", "en" }, UiText.English.WindowsLanguageNames);
    }

    /// <summary>Whether the English catalog has exactly this text under its key.</summary>
    /// <param name="english">An English text.</param>
    /// <returns><see langword="true"/> when the catalog holds it.</returns>
    private static bool HasEntry(string english) =>
        UiText.English.TryGet("T" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(english)))[..12], out string? text) && text == english;

    /// <summary>The offset of the first character after <paramref name="index"/> that is not white space.</summary>
    /// <param name="code">The source.</param>
    /// <param name="index">Where to start.</param>
    /// <returns>The offset.</returns>
    private static int SkipSpace(string code, int index)
    {
        while (index < code.Length && char.IsWhiteSpace(code[index]))
        {
            index++;
        }

        return index;
    }

    /// <summary>The offset of the parenthesis that closes a call, stepping over string literals.</summary>
    /// <param name="code">The source.</param>
    /// <param name="open">The offset just after the call's opening parenthesis.</param>
    /// <param name="literals">The source's literals.</param>
    /// <returns>The closing parenthesis's offset (the end of the source when unbalanced).</returns>
    private static int ClosingParenthesis(string code, int open, List<CSharpSourceScanner.Literal> literals)
    {
        var starts = literals.ToDictionary(l => l.Start, l => l.End);
        int depth = 1;
        for (int i = open; i < code.Length; i++)
        {
            if (starts.TryGetValue(i, out int end))
            {
                i = end - 1;
                continue;
            }

            if (code[i] is '(' or '[' or '{')
            {
                depth++;
            }
            else if (code[i] is ')' or ']' or '}' && --depth == 0)
            {
                return i;
            }
        }

        return code.Length;
    }

    /// <summary>The first comma between a call's parentheses that is not nested in brackets or a literal.</summary>
    /// <param name="code">The source.</param>
    /// <param name="open">The offset just after the opening parenthesis.</param>
    /// <param name="close">The closing parenthesis's offset.</param>
    /// <param name="literals">The source's literals.</param>
    /// <returns>The comma's offset, or <see langword="null"/>.</returns>
    private static int? TopLevelComma(string code, int open, int close, List<CSharpSourceScanner.Literal> literals)
    {
        var starts = literals.ToDictionary(l => l.Start, l => l.End);
        int depth = 0;
        for (int i = open; i < close; i++)
        {
            if (starts.TryGetValue(i, out int end))
            {
                i = end - 1;
                continue;
            }

            if (code[i] is '(' or '[' or '{')
            {
                depth++;
            }
            else if (code[i] is ')' or ']' or '}')
            {
                depth--;
            }
            else if (code[i] == ',' && depth == 0)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>A call's argument text with every string literal blanked, for finding the constants it names.</summary>
    /// <param name="code">The source.</param>
    /// <param name="open">The offset just after the opening parenthesis.</param>
    /// <param name="close">The closing parenthesis's offset.</param>
    /// <param name="literals">The source's literals.</param>
    /// <returns>The arguments without literals.</returns>
    private static string WithoutLiterals(string code, int open, int close, List<CSharpSourceScanner.Literal> literals)
    {
        var text = code.ToCharArray(open, close - open);
        foreach (var literal in literals.Where(l => l.Start >= open && l.Start < close))
        {
            Array.Fill(text, ' ', literal.Start - open, Math.Min(literal.End, close) - literal.Start);
        }

        return new string(text);
    }

    /// <summary>The repository's source files of one kind, without build output.</summary>
    /// <param name="pattern">A file pattern (<c>*.cs</c> by default).</param>
    /// <returns>Their full paths.</returns>
    private static IEnumerable<string> SourceFiles(string pattern = "*.cs")
    {
        string src = Path.Combine(RepoRoot(), "src");
        return Directory.EnumerateFiles(src, pattern, SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(src, file).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));
    }

    /// <summary>The repository's root: the folder above the test binaries that holds the solution.</summary>
    /// <returns>Its path.</returns>
    /// <exception cref="InvalidOperationException">No folder above the test binaries holds BetterClipboard.sln.</exception>
    private static string RepoRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "BetterClipboard.sln")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException($"BetterClipboard.sln was not found above {AppContext.BaseDirectory}.");
    }
}
