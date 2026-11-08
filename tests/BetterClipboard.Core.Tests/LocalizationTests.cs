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

    /// <summary>Simplified Chinese is selected only by the explicit choice or a compatible system culture.</summary>
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
        Assert.Equal(expected, UiText.UsesSimplifiedChinese(selection, system));

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
    /// Every shipped catalog has every English key and nothing else; translated templates cannot introduce an invalid
    /// format or argument index (they may leave out English plural arguments, never add one).
    /// </summary>
    [Fact]
    public void CatalogsAreCompleteAndFormatsAreValid()
    {
        var english = UiText.English.Strings;
        Assert.True(english.Count > 450);
        Assert.True(UiText.Languages.Count >= 2);
        foreach (var language in UiText.Languages.Skip(1))
        {
            Assert.Equal(english.Keys.Order(StringComparer.Ordinal), language.Strings.Keys.Order(StringComparer.Ordinal));
            foreach ((string key, string original) in english)
            {
                string translated = language.Strings[key];
                Assert.False(string.IsNullOrWhiteSpace(translated), $"{language.Tag}: {key} is empty");
                int[] indices = Regex.Matches(original, @"\{(\d+)(?:[^}]*)\}").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
                if (indices.Length == 0)
                {
                    continue;
                }

                int count = indices.Max() + 1;
                Assert.True(CompositeFormat.Parse(translated).MinimumArgumentCount <= count, $"{language.Tag}: {original}");
                _ = string.Format(CultureInfo.InvariantCulture, translated, Enumerable.Repeat<object>(1, count).ToArray());
            }
        }
    }

    /// <summary>
    /// This build ships English first (the source and every fallback) and Simplified Chinese, each with its own name,
    /// its English name and its flag, all read from the embedded catalogs.
    /// </summary>
    [Fact]
    public void Languages_EnglishFirst_ThenSimplifiedChinese()
    {
        Assert.Equal(new[] { "en", "zh-Hans" }, UiText.Languages.Select(l => l.Tag));
        Assert.Same(UiText.English, UiText.Languages[0]);

        var english = UiText.Languages[0];
        Assert.Equal(("English", "English", "us"), (english.Name, english.EnglishName, english.Flag));

        var chinese = UiText.Languages[1];
        Assert.Equal(("简体中文", "Chinese (Simplified)", "cn"), (chinese.Name, chinese.EnglishName, chinese.Flag));
        Assert.Equal(new[] { "zh" }, chinese.AlsoFor);
    }

    /// <summary>
    /// Every shipped flag is a 3:2 SVG drawn only with what Direct2D's SVG renderer (WinUI's SvgImageSource) draws:
    /// no use, text, style, image or filter elements.
    /// </summary>
    [Fact]
    public void EveryFlagIsADrawableSvg()
    {
        XNamespace svg = "http://www.w3.org/2000/svg";
        string[] unsupported = ["use", "text", "style", "image", "filter", "foreignObject"];
        foreach (var language in UiText.Languages)
        {
            var document = XDocument.Parse(Encoding.UTF8.GetString(language.FlagSvg.Span));
            Assert.Equal(svg + "svg", document.Root!.Name);
            Assert.Equal("0 0 900 600", (string?)document.Root.Attribute("viewBox"));
            Assert.Empty(document.Descendants().Where(e => unsupported.Contains(e.Name.LocalName)));
            Assert.NotEmpty(document.Descendants(svg + "path"));
        }
    }

    /// <summary>
    /// Every key is the hash of its English text (what tools/localization/catalog.py writes), and no two keys share an
    /// English text: C# looks strings up by their English text, so a duplicate could never be told apart.
    /// </summary>
    [Fact]
    public void EnglishKeysAreTheirTextsHashAndUnique()
    {
        foreach ((string key, string text) in UiText.English.Strings)
        {
            string expected = "T" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
            Assert.True(key == expected, $"{key} is not the key of {text}");
        }

        var duplicates = UiText.English.Strings.GroupBy(p => p.Value, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key);
        Assert.Empty(duplicates);
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
            Assert.Equal("完成", UiText.Get(UiText.English.Strings.First(p => p.Value == "Done").Key, chinese));
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
