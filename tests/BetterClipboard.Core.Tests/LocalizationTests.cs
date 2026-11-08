using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Settings;

namespace BetterClipboard.Core.Tests;

/// <summary>Checks catalog integrity and language fallback without Windows UI or real clipboard access.</summary>
public sealed class LocalizationTests
{
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

    /// <summary>Every English entry has a translation; translated templates cannot introduce an invalid format or argument index.</summary>
    [Fact]
    public void CatalogsAreCompleteAndFormatsAreValid()
    {
        var assembly = typeof(UiText).Assembly;
        using Stream enStream = assembly.GetManifestResourceStream("BetterClipboard.Core.Localization.en.json")!;
        using Stream zhStream = assembly.GetManifestResourceStream("BetterClipboard.Core.Localization.zh-Hans.json")!;
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(enStream)!;
        var zh = JsonSerializer.Deserialize<Dictionary<string, string>>(zhStream)!;
        Assert.True(en.Count > 450);
        Assert.Equal(en.Keys.Order(), zh.Keys.Order());
        foreach ((string key, string original) in en)
        {
            Assert.False(string.IsNullOrWhiteSpace(zh[key]));
            int[] indices = Regex.Matches(original, @"\{(\d+)(?:[^}]*)\}").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
            if (indices.Length == 0) continue;
            int count = indices.Max() + 1;
            // Chinese does not need English plural suffix arguments; it may omit them but cannot add new ones.
            Assert.True(System.Text.CompositeFormat.Parse(zh[key]).MinimumArgumentCount <= count, original);
            _ = string.Format(CultureInfo.InvariantCulture, zh[key], Enumerable.Repeat<object>(1, count).ToArray());
        }
    }
}
