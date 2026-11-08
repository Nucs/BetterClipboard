using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BetterClipboard.Core;
using BetterClipboard.Core.Cli;
using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Settings;

namespace BetterClipboard.Core.Tests;

/// <summary>Checks catalog integrity and language fallback without Windows UI or real clipboard access.</summary>
public sealed class LocalizationTests
{
    /// <summary>A fixed "now" for the relative-time and CLI checks.</summary>
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Where the per-language counts go (the test log).</summary>
    private readonly ITestOutputHelper output;

    /// <summary>Creates the tests with xunit's output.</summary>
    /// <param name="output">The test output.</param>
    public LocalizationTests(ITestOutputHelper output) => this.output = output;

    /// <summary>
    /// Windows display languages and the shipped language each must get: exact tags, parents (zh-SG and zh-Hans-CN reach
    /// zh-Hans through zh-Hans), the bare "zh" that zh-Hans claims, letter case, and every Traditional Chinese name,
    /// which must stay English although its parents end in "zh".
    /// </summary>
    public static TheoryData<string, string> SystemMatches => new()
    {
        { "zh-CN", "zh-Hans" },
        { "zh-SG", "zh-Hans" },
        { "zh-Hans-CN", "zh-Hans" },
        { "zh-Hans", "zh-Hans" },
        { "zh", "zh-Hans" },
        { "ZH-cn", "zh-Hans" },
        { "zh-TW", "en" },
        { "zh-HK", "en" },
        { "zh-MO", "en" },
        { "zh-Hant", "en" },
        { "en-US", "en" },
        { "en-GB", "en" },
        { "he-IL", "en" },
        { "fr-FR", "en" },
        { "", "en" },
        { "not a culture", "en" },
    };

    /// <summary>
    /// Saved selections against Windows: an explicit language wins in any letter case; "system", an empty value and a
    /// tag this build does not ship ("he", a newer version's language) follow Windows.
    /// </summary>
    public static TheoryData<string, string, string> Resolutions => new()
    {
        { "en", "zh-CN", "en" },
        { "zh-Hans", "en-US", "zh-Hans" },
        { "ZH-HANS", "en-US", "zh-Hans" },
        { "system", "zh-CN", "zh-Hans" },
        { "system", "en-US", "en" },
        { "", "zh-CN", "zh-Hans" },
        { "he", "zh-CN", "zh-Hans" },
        { "he", "fr-FR", "en" },
    };

    /// <summary>
    /// When the first start asks: never once a history exists (an update from a version without languages), never over
    /// a language someone saved (an administrator's pre-seeded settings), and in every other first start.
    /// </summary>
    public static TheoryData<bool, string, bool> FirstStarts => new()
    {
        { false, "system", true },
        { false, "SYSTEM", true },
        { false, "", true },
        { false, "  ", true },
        { true, "system", false },
        { false, "en", false },
        { false, "zh-Hans", false },
        { true, "zh-Hans", false },
    };

    /// <summary>
    /// Simplified Chinese is selected only by the explicit choice or a compatible system culture: the same rule every
    /// catalog gets (<see cref="UiText.Resolve"/>), checked here with the one shipped language besides English.
    /// </summary>
    /// <param name="selection">Saved language selection.</param>
    /// <param name="system">System UI language.</param>
    /// <param name="expected">Whether Simplified Chinese is expected.</param>
    [Theory]
    [InlineData("system", "zh-CN", true)]
    [InlineData("system", "zh-SG", true)]
    [InlineData("system", "zh-Hans-CN", true)]
    [InlineData("system", "zh-TW", false)]
    [InlineData("system", "zh-HK", false)]
    [InlineData("system", "en-US", false)]
    [InlineData("en", "zh-CN", false)]
    [InlineData("zh-Hans", "en-US", true)]
    [InlineData("invalid", "en-US", false)]
    public void LanguageSelection(string selection, string system, bool expected) =>
        Assert.Equal(expected, UiText.Resolve(selection, system).Tag == "zh-Hans");

    /// <summary>Missing language fields in old settings retain the system-language default.</summary>
    [Fact]
    public void ExistingSettingsAreCompatible()
    {
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.Equal("system", settings.InterfaceLanguage);
        var roundTrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings with { InterfaceLanguage = "zh-Hans" }))!;
        Assert.Equal("zh-Hans", roundTrip.InterfaceLanguage);
    }

    /// <summary>Unknown text is returned unchanged and explicitly selecting English bypasses Chinese translations.</summary>
    [Fact]
    public void EnglishAndMissingTextFallback()
    {
        string previous = UiText.Language;
        try
        {
            UiText.Language = "zh-Hans";
            Assert.Equal("设置", UiText.Translate("Settings"));
            Assert.Equal("unregistered literal", UiText.Translate("unregistered literal"));
            Assert.Equal("missing-key", UiText.Get("missing-key"));
            UiText.Language = "en";
            Assert.Equal("Settings", UiText.Translate("Settings"));
        }
        finally { UiText.Language = previous; }
    }

    /// <summary>Only templates are translated: group names and paths passed as interpolation arguments stay byte-for-byte intact.</summary>
    [Fact]
    public void FormatPreservesUserArguments()
    {
        string previous = UiText.Language;
        try
        {
            UiText.Language = "zh-Hans";
            string group = "Settings {0} 中文 <>&";
            Assert.Equal("添加到 " + group, UiText.Format($"Add to {group}"));
            string path = @"D:\Clipboard\Files\原文.txt";
            Assert.Equal("正在监听：" + path, UiText.Format($"Watching: {path}"));
        }
        finally { UiText.Language = previous; }
    }

    /// <summary>
    /// Every shipped catalog holds only strings en.json has (or the plural forms its own rules name), none empty, and no
    /// translated template uses a value its English does not pass (it may leave values out, never add one). A language
    /// may lack strings: they show in English, and this test reports how many each one lacks (never as a share).
    /// </summary>
    [Fact]
    public void CatalogsAreWellFormed_AndReportWhatTheyLack()
    {
        var english = UiText.English.Strings;
        Assert.True(english.Count > 450);
        foreach (var language in UiText.AllShipped.Skip(1))
        {
            foreach ((string key, string translated) in language.Strings)
            {
                Assert.False(string.IsNullOrWhiteSpace(translated), $"{language.Tag}: {key} is empty");
                string source;
                if (UiText.TryParsePluralKey(key, out string? other, out var category))
                {
                    Assert.True(english.ContainsKey(UiText.PluralKey(other, PluralCategory.Other)), $"{language.Tag}: {key} is a form of no English plural string");
                    Assert.True(language.Plurals.Categories.Contains(category), $"{language.Tag}: {key} is a form {language.Tag} does not have");
                    source = other;
                }
                else
                {
                    Assert.True(english.TryGetValue(key, out string? text), $"{language.Tag}: {key} is not in en.json (renamed or removed)");
                    source = text;
                }

                int count = MinimumArguments(source);
                Assert.True(CompositeFormat.Parse(translated).MinimumArgumentCount <= count, $"{language.Tag}: {key}");
                _ = string.Format(CultureInfo.InvariantCulture, translated, Enumerable.Repeat<object>(1, count).ToArray());
            }

            var (missing, total) = UiText.Coverage(language);
            output.WriteLine($"{language.Tag}: lacks {missing:N0} of {total:N0} strings; {(UiText.Languages.Contains(language) ? "offered" : "not offered yet")}.");
        }
    }

    /// <summary>
    /// Every shipped catalog's header names its plural rules (<c>{}</c> for a language with only the other form), and
    /// every integer sample written with a rule lands in its category: a forgotten or mistyped rule would word "21
    /// items" wrongly in every count of the app.
    /// </summary>
    [Fact]
    public void EveryCatalogDeclaresItsPluralRules_AndTheirSamplesHold()
    {
        var assembly = typeof(UiText).Assembly;
        foreach (var language in UiText.AllShipped)
        {
            using var stream = assembly.GetManifestResourceStream($"BetterClipboard.Core.Localization.{language.Tag}.json")!;
            using var document = JsonDocument.Parse(stream);
            Assert.True(document.RootElement.GetProperty("$language").TryGetProperty("plurals", out _), $"{language.Tag}: no plurals in its header");
            foreach (var category in language.Plurals.Categories)
            {
                foreach (long sample in language.Plurals.IntegerSamples(category))
                {
                    Assert.Equal(category, language.Plurals.Select(sample));
                }
            }
        }

        Assert.Equal(new[] { PluralCategory.One, PluralCategory.Other }, UiText.English.Plurals.Categories);
    }

    /// <summary>
    /// The offered languages are English first (the source and every fallback), then the others by English name, each
    /// with its own name, its English name and its flag read from the embedded catalogs; Simplified Chinese is among
    /// them, complete enough to offer.
    /// </summary>
    [Fact]
    public void Languages_EnglishFirst_ThenByEnglishName()
    {
        Assert.Same(UiText.English, UiText.Languages[0]);
        Assert.Same(UiText.English, UiText.AllShipped[0]);
        var others = UiText.Languages.Skip(1).Select(l => l.EnglishName).ToList();
        Assert.Equal(others.Order(StringComparer.OrdinalIgnoreCase), others);
        Assert.All(UiText.Languages, language => Assert.Contains(language, UiText.AllShipped));

        var english = UiText.Languages[0];
        Assert.Equal(("English", "English", "us"), (english.Name, english.EnglishName, english.Flag));

        Assert.True(UiText.TryFind("zh-Hans", out var chinese));
        Assert.Equal(("简体中文", "Chinese (Simplified)", "cn"), (chinese.Name, chinese.EnglishName, chinese.Flag));
        Assert.Equal(new[] { "zh" }, chinese.AlsoFor);
    }

    /// <summary>
    /// Every shipped flag is a 3:2 SVG drawn only with what Direct2D's SVG renderer (WinUI's SvgImageSource) draws:
    /// no use, text, style, image or filter elements. Languages not offered yet count too: a partial catalog is offered
    /// the moment enough of it is translated, with no other change that would bring its flag under test.
    /// </summary>
    [Fact]
    public void EveryFlagIsADrawableSvg()
    {
        XNamespace svg = "http://www.w3.org/2000/svg";
        string[] unsupported = ["use", "text", "style", "image", "filter", "foreignObject"];
        foreach (var language in UiText.AllShipped)
        {
            var document = XDocument.Parse(Encoding.UTF8.GetString(language.FlagSvg.Span));
            Assert.Equal(svg + "svg", document.Root!.Name);
            Assert.Equal("0 0 900 600", (string?)document.Root.Attribute("viewBox"));
            Assert.DoesNotContain(document.Descendants(), e => unsupported.Contains(e.Name.LocalName));
            Assert.NotEmpty(document.Descendants(svg + "path"));
        }
    }

    /// <summary>
    /// en.json is the list of every UI string: each key is its own English text, except a hash key, which is the hash of
    /// its text (what tools/localization/catalog.py writes), and a plural string's forms, which hold exactly English's one
    /// and other form, the other form reading as the key. No text is listed twice: C# looks strings up by their English
    /// text, so a duplicate could never be told apart.
    /// </summary>
    [Fact]
    public void EnglishCatalog_KeysAreTheirText()
    {
        var forms = new Dictionary<string, List<PluralCategory>>(StringComparer.Ordinal);
        foreach ((string key, string text) in UiText.English.Strings)
        {
            if (UiText.TryParsePluralKey(key, out string? other, out var category))
            {
                (forms.TryGetValue(other, out var list) ? list : forms[other] = []).Add(category);
                if (category == PluralCategory.Other)
                {
                    Assert.Equal(other, text);
                }

                continue;
            }

            if (UiText.IsHashKey(key))
            {
                string expected = "T" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
                Assert.True(key == expected, $"{key} is not the key of {text}");
                Assert.Equal(expected, UiText.HashKey(text));
                continue;
            }

            Assert.True(key == text, $"{key} must map to itself");
        }

        Assert.All(forms, family => Assert.Equal(new[] { PluralCategory.One, PluralCategory.Other }, family.Value.Order()));
        var plainTexts = UiText.English.Strings.Where(p => !UiText.TryParsePluralKey(p.Key, out _, out _)).Select(p => p.Value);
        Assert.Empty(plainTexts.GroupBy(text => text, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key));
    }

    /// <summary>The shipped language that fits a Windows display language (see <see cref="SystemMatches"/>).</summary>
    /// <param name="system">Windows' display language.</param>
    /// <param name="expected">The expected language's tag.</param>
    [Theory]
    [MemberData(nameof(SystemMatches))]
    public void MatchSystem_PicksTheFittingCatalog(string system, string expected) =>
        Assert.Equal(expected, UiText.MatchSystem(system).Tag);

    /// <summary>What a saved selection means on a given Windows (see <see cref="Resolutions"/>).</summary>
    /// <param name="selection">The saved selection.</param>
    /// <param name="system">Windows' display language.</param>
    /// <param name="expected">The expected language's tag.</param>
    [Theory]
    [MemberData(nameof(Resolutions))]
    public void Resolve_ExplicitChoiceElseWindows(string selection, string system, string expected) =>
        Assert.Equal(expected, UiText.Resolve(selection, system).Tag);

    /// <summary>
    /// A text can be read in a language other than the selected one (the first-start picker words its heading in the
    /// row under the selection), without changing the selection.
    /// </summary>
    [Fact]
    public void TranslateIntoAGivenLanguage_LeavesTheSelectionAlone()
    {
        string previous = UiText.Language;
        try
        {
            UiText.Language = "en";
            Assert.True(UiText.TryFind("zh-Hans", out var chinese));
            Assert.Equal("选择语言", UiText.Translate("Choose your language", chinese));
            Assert.Equal("完成", UiText.Get("Done", chinese));
            Assert.Equal("Choose your language", UiText.Translate("Choose your language"));
            Assert.Equal("en", UiText.Current.Tag);
        }
        finally { UiText.Language = previous; }
    }

    /// <summary>When the first start asks for the language (see <see cref="FirstStarts"/>).</summary>
    /// <param name="historyExisted">Whether a history existed before this start.</param>
    /// <param name="saved">The saved selection.</param>
    /// <param name="expected">Whether the picker is shown.</param>
    [Theory]
    [MemberData(nameof(FirstStarts))]
    public void FirstStart_AsksOnlyWithoutHistoryOrChoice(bool historyExisted, string saved, bool expected) =>
        Assert.Equal(expected, LanguageChoice.ShouldAsk(historyExisted, saved));

    /// <summary>A settings file without the language (null after a hand edit) is no choice either; the suggestion follows Windows.</summary>
    [Fact]
    public void FirstStart_NoSavedLanguage_AsksAndSuggestsWindowsLanguage()
    {
        Assert.True(LanguageChoice.ShouldAsk(historyExisted: false, interfaceLanguage: null));
        Assert.Equal("zh-Hans", LanguageChoice.Suggest("zh-CN").Tag);
        Assert.Equal("en", LanguageChoice.Suggest("de-DE").Tag);
        Assert.Equal("en", LanguageChoice.Suggest(null).Tag);
    }

    /// <summary>
    /// A data folder holds a history once a store folder (any machine's, a quarantined one too) or a v0.1 database is
    /// there; a settings file alone, which installers write, is not one, and neither is an empty stores folder.
    /// </summary>
    [Fact]
    public void HasHistory_OnlyWithAStoreOrALegacyDatabase()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        Assert.False(paths.HasHistory());

        File.WriteAllText(paths.SettingsPath, "{}");
        Assert.False(paths.HasHistory());

        Directory.CreateDirectory(paths.StoresDirectory);
        Assert.False(paths.HasHistory());

        Directory.CreateDirectory(paths.StoreDirectory(Guid.NewGuid()) + ".unreadable-20261108-120000");
        Assert.True(paths.HasHistory());

        using var legacy = new TempDirectory();
        var older = new AppPaths(legacy.Path);
        File.WriteAllBytes(older.LegacyDatabasePath, [0]);
        Assert.True(older.HasHistory());
    }

    /// <summary>
    /// Relative times follow the interface language for the panel and Settings, and stay English when asked (bclip).
    /// </summary>
    [Fact]
    public void RelativeTime_FollowsTheLanguage_UnlessEnglishIsAskedFor()
    {
        string previous = UiText.Language;
        try
        {
            UiText.Language = "zh-Hans";
            var zone = TimeZoneInfo.Utc;
            string localized = RelativeTimeFormatter.Format(Now.AddMinutes(-5), Now, zone, CultureInfo.InvariantCulture);
            Assert.Equal(string.Format(CultureInfo.CurrentCulture, UiText.Translate("{0} min ago"), 5), localized);
            Assert.NotEqual("5 min ago", localized);
            Assert.Equal(UiText.Translate("just now"), RelativeTimeFormatter.Format(Now, Now, zone, CultureInfo.InvariantCulture));

            Assert.Equal("just now", RelativeTimeFormatter.Format(Now, Now, zone, CultureInfo.InvariantCulture, localized: false));
            Assert.Equal("5 min ago", RelativeTimeFormatter.Format(Now.AddMinutes(-5), Now, zone, CultureInfo.InvariantCulture, localized: false));
            Assert.Equal("3 h ago", RelativeTimeFormatter.Format(Now.AddHours(-3), Now, zone, CultureInfo.InvariantCulture, localized: false));
            Assert.StartsWith("Yesterday ", RelativeTimeFormatter.Format(Now.AddDays(-1), Now, zone, CultureInfo.InvariantCulture, localized: false), StringComparison.Ordinal);
        }
        finally { UiText.Language = previous; }
    }

    /// <summary>bclip's tables are read by scripts and agents: English times whatever the interface language says.</summary>
    [Fact]
    public void CliTables_StayEnglish_WhateverTheInterfaceLanguage()
    {
        string previous = UiText.Language;
        try
        {
            UiText.Language = "zh-Hans";
            string items = CliOutput.RenderTable([new CliItem { Id = 1, Kind = "text", Preview = "BC-TEST", LastUsed = Now.AddMinutes(-3) }], Now);
            Assert.Contains("3 min ago", items, StringComparison.Ordinal);

            string prompts = CliOutput.RenderPrompts([new CliPrompt { Id = 2, Agent = "claude", Preview = "BC-TEST", LastSent = Now, Sends = 1 }], Now);
            Assert.Contains("just now", prompts, StringComparison.Ordinal);
        }
        finally { UiText.Language = previous; }
    }

    /// <summary>
    /// Logs stay English whatever the interface language: no log call in the sources passes text through UiText. (A
    /// translated value handed over in a variable is not seen here; keep log lines and shown texts apart.)
    /// </summary>
    [Fact]
    public void NoLogLineIsBuiltFromTranslatedText()
    {
        var offenders = new List<string>();
        string src = Path.Combine(RepoRoot(), "src");
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(src, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            {
                continue;
            }

            string source = File.ReadAllText(file);
            foreach (Match call in Regex.Matches(source, @"AppLog\.(Info|Warn|Error)\("))
            {
                // A log call ends with ");" at the end of its line (also when it spans several lines).
                var end = Regex.Match(source[call.Index..], @"\);[ \t]*\r?\n");
                string statement = end.Success ? source.Substring(call.Index, end.Index) : source[call.Index..];
                if (statement.Contains("UiText.", StringComparison.Ordinal))
                {
                    offenders.Add($"{relative}: {statement[..Math.Min(140, statement.Length)]}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The code and the catalogs agree: every English text XAML names, and every literal C# passes to
    /// <c>UiText.Translate</c> or <c>UiText.Format</c>, is in en.json (or no translator would ever see it); every
    /// <c>UiText.Plural</c> call's forms are en.json's forms of that plural string; every hash key XAML names exists; and
    /// every en.json string still appears somewhere in the sources (else it is left over from a text that changed:
    /// <c>catalog.py rename</c> moves a translation instead of leaving it behind).
    /// </summary>
    [Fact]
    public void CodeAndCatalogsAgree()
    {
        var english = UiText.English.Strings;
        var hashTexts = english.Where(p => UiText.IsHashKey(p.Key)).ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
        var scan = new UiTextSourceScan(Path.Combine(RepoRoot(), "src"));
        Assert.True(scan.XamlTexts.Count > 100 && scan.Lookups.Count > 200 && scan.Plurals.Count > 20, "the scan found too little");

        var problems = new List<string>();
        foreach (var (text, where) in scan.XamlTexts)
        {
            if (!english.ContainsKey(text) || UiText.IsHashKey(text))
            {
                problems.Add($"{where}: XAML text not in en.json: {text}");
            }
        }

        foreach (var (key, where) in scan.XamlKeys)
        {
            if (!english.ContainsKey(key))
            {
                problems.Add($"{where}: XAML hash key not in en.json: {key}");
            }
        }

        foreach (var (text, where) in scan.Lookups)
        {
            if (!english.ContainsKey(text) && !hashTexts.ContainsKey(text))
            {
                problems.Add($"{where}: not in en.json (catalog.py add): {text}");
            }
        }

        var pluralsUsed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (one, other, where) in scan.Plurals)
        {
            pluralsUsed.Add(other);
            if (!english.TryGetValue(UiText.PluralKey(other, PluralCategory.One), out string? oneForm) || oneForm != one
                || !english.ContainsKey(UiText.PluralKey(other, PluralCategory.Other)))
            {
                problems.Add($"{where}: plural string not in en.json as written (catalog.py add-plural): {one} / {other}");
            }
        }

        foreach ((string key, string text) in english)
        {
            bool used = UiText.TryParsePluralKey(key, out string? family, out _)
                ? pluralsUsed.Contains(family)
                : UiText.IsHashKey(key) ? scan.XamlKeys.Any(k => k.Key == key) || scan.AllLiterals.Contains(text) : scan.AllLiterals.Contains(key);
            if (!used)
            {
                problems.Add($"en.json: used nowhere in the sources (renamed or removed: catalog.py rename, or delete it): {key}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// A plural string takes each language's form for the count: English's one and other from the code, Chinese's single
    /// form for every count, and English again for a count whose form a catalog lacks (as for any missing string).
    /// </summary>
    [Fact]
    public void Plural_PicksTheLanguagesForm()
    {
        Assert.True(UiText.TryFind("zh-Hans", out var chinese));
        var invariant = CultureInfo.InvariantCulture;
        Assert.Equal("1 item", Items(1, UiText.English));
        Assert.Equal("0 items", Items(0, UiText.English));
        Assert.Equal("1,234 items", Items(1234, UiText.English));
        Assert.Equal("1 项", Items(1, chinese));
        Assert.Equal("5 项", Items(5, chinese));
        Assert.Equal("opened once", UiText.Plural(invariant, 1, $"opened once", $"opened {1:N0} times", UiText.English));
        Assert.Equal("已打开 1 次", UiText.Plural(invariant, 1, $"opened once", $"opened {1:N0} times", chinese));

        // A plural string no catalog knows: English's forms, in every language.
        Assert.Equal("1 widget", UiText.Plural(invariant, 1, $"{1} widget", $"{1} widgets", chinese));
        Assert.Equal("2 widgets", UiText.Plural(invariant, 2, $"{2} widget", $"{2} widgets", chinese));

        string Items(long count, UiLanguage language) =>
            UiText.Plural(invariant, count, $"{count:N0} item", $"{count:N0} items", language);
    }

    /// <summary>
    /// A form whose placeholders do not fit the values (a catalog bug the tests catch) gives the English form instead of
    /// an exception in the middle of drawing a window; a form a language lacks falls back to English too.
    /// </summary>
    [Fact]
    public void Plural_BrokenOrMissingForms_FallBackToEnglish()
    {
        var russianRules = PluralRules.Parse(new Dictionary<string, string>
        {
            ["one"] = "v = 0 and i % 10 = 1 and i % 100 != 11",
            ["few"] = "v = 0 and i % 10 = 2..4 and i % 100 != 12..14",
            ["many"] = "v = 0 and i % 10 = 0 or v = 0 and i % 10 = 5..9 or v = 0 and i % 100 = 11..14",
        });
        var russian = Language("xx", russianRules, new Dictionary<string, string>
        {
            ["{0} items#one"] = "{0} элемент",
            ["{0} items#few"] = "{0} элемента {3}",
            ["{0} items#other"] = "{0} элемента",
        });

        var invariant = CultureInfo.InvariantCulture;
        Assert.Equal("21 элемент", UiText.Plural(invariant, 21, $"{21} item", $"{21} items", russian));
        Assert.Equal("2 items", UiText.Plural(invariant, 2, $"{2} item", $"{2} items", russian));
        Assert.Equal("5 items", UiText.Plural(invariant, 5, $"{5} item", $"{5} items", russian));
        Assert.Equal("{0} items", UiText.PluralTemplate(7, "{0} item", "{0} items", russian));
        Assert.Equal("{0} элемент", UiText.PluralTemplate(1, "{0} item", "{0} items", russian));
    }

    /// <summary>
    /// A language is offered while it lacks at most one string in ten; a plural string counts once, and a language lacks
    /// it until every form its own rules need is there. English is always offered.
    /// </summary>
    [Fact]
    public void Coverage_DecidesWhetherALanguageIsOffered()
    {
        var englishStrings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{0} items#one"] = "{0} item",
            ["{0} items#other"] = "{0} items",
        };
        for (int i = 0; i < 9; i++)
        {
            englishStrings[$"Text {i}"] = $"Text {i}";
        }

        var english = Language("en", PluralRules.OtherOnly, englishStrings);
        Assert.Equal((0, 10), UiText.Coverage(english, english));
        Assert.True(UiText.IsOffered(english, english));

        var full = englishStrings.Keys.Where(k => !k.Contains('#')).ToDictionary(k => k, k => "x" + k);
        full["{0} items#other"] = "{0} x";
        var complete = Language("aa", PluralRules.OtherOnly, full);
        Assert.Equal((0, 10), UiText.Coverage(complete, english));

        var oneShort = Language("ab", PluralRules.OtherOnly, full.Where(p => p.Key != "Text 0").ToDictionary());
        Assert.Equal((1, 10), UiText.Coverage(oneShort, english));
        Assert.True(UiText.IsOffered(oneShort, english));

        var twoShort = Language("ac", PluralRules.OtherOnly, full.Where(p => p.Key is not "Text 0" and not "Text 1").ToDictionary());
        Assert.Equal((2, 10), UiText.Coverage(twoShort, english));
        Assert.False(UiText.IsOffered(twoShort, english));

        // A language with a one form lacks the plural string while it has only the other form.
        var withOne = Language("ad", PluralRules.Parse(new Dictionary<string, string> { ["one"] = "i = 1" }), full);
        Assert.Equal((1, 10), UiText.Coverage(withOne, english));
    }

    /// <summary>
    /// Lookups by English text: a text kept under a hash key is still found by its English (the C# route), and XAML's
    /// key route reads an English-text key like any other.
    /// </summary>
    [Fact]
    public void HashKeysAndEnglishKeys_BothResolve()
    {
        var hashKey = UiText.HashKey("A long text kept under a hash key");
        var language = Language("xx", PluralRules.OtherOnly, new Dictionary<string, string>
        {
            [hashKey] = "译文",
            ["Paste"] = "粘贴",
        });

        Assert.Matches("^T[0-9a-f]{12}$", hashKey);
        Assert.True(UiText.IsHashKey(hashKey));
        Assert.False(UiText.IsHashKey("Paste"));
        Assert.False(UiText.IsHashKey("T0123456789AB"));
        Assert.Equal("粘贴", UiText.Get("Paste", language));
        Assert.Equal("粘贴", UiText.Translate("Paste", language));
        Assert.Equal("Not here", UiText.Get("Not here", language));
        Assert.Equal("Not here", UiText.Translate("Not here", language));
        Assert.Equal("译文", UiText.Get(hashKey, language));
    }

    /// <summary>A plural key splits into its English other form and its category; nothing else is a plural key.</summary>
    [Fact]
    public void PluralKeys_RoundTrip()
    {
        Assert.Equal("{0:N0} items#few", UiText.PluralKey("{0:N0} items", PluralCategory.Few));
        Assert.True(UiText.TryParsePluralKey("{0:N0} items#few", out string? other, out var category));
        Assert.Equal(("{0:N0} items", PluralCategory.Few), (other, category));
        Assert.True(UiText.TryParsePluralKey("C# {0} items#other", out other, out _));
        Assert.Equal("C# {0} items", other);
        Assert.False(UiText.TryParsePluralKey("C#", out _, out _));
        Assert.False(UiText.TryParsePluralKey("#one", out _, out _));
        Assert.False(UiText.TryParsePluralKey("Issue #One", out _, out _));
    }

    /// <summary>A language made in the test, with a stand-in flag.</summary>
    /// <param name="tag">Its tag.</param>
    /// <param name="plurals">Its plural rules.</param>
    /// <param name="strings">Its strings.</param>
    /// <returns>The language.</returns>
    private static UiLanguage Language(string tag, PluralRules plurals, Dictionary<string, string> strings) =>
        new(tag, tag, tag, "us", [], [], plurals, strings);

    /// <summary>How many arguments a composite format needs: its highest placeholder index plus one (0 for none).</summary>
    /// <param name="format">The format.</param>
    /// <returns>The count.</returns>
    private static int MinimumArguments(string format)
    {
        int[] indices = Regex.Matches(format, @"\{(\d+)(?:[^}]*)\}").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        return indices.Length == 0 ? 0 : indices.Max() + 1;
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
