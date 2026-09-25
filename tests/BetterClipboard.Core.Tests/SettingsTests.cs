using BetterClipboard.Core.Model;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for settings persistence/normalization, rule derivation and the search query builder.
/// </summary>
public sealed class SettingsTests : IDisposable
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();

    /// <summary>Deletes the temp directory.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>A missing file yields defaults (the app must always start).</summary>
    [Fact]
    public void Load_MissingFile_UsesDefaults()
    {
        var store = new SettingsStore(Path.Combine(temp.Path, "settings.json"));
        Assert.Equal("Win+V", store.Load().OpenHotkey);
    }

    /// <summary>A corrupt file is quarantined (recoverable) and defaults are used.</summary>
    [Fact]
    public void Load_CorruptFile_IsQuarantined()
    {
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ not json");
        var store = new SettingsStore(path);
        Assert.Equal(10_000, store.Load().MaxItems);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(temp.Path, "settings.json.corrupt-*"));
    }

    /// <summary>Updates persist, normalize and notify.</summary>
    [Fact]
    public void Update_PersistsNormalizesAndNotifies()
    {
        var path = Path.Combine(temp.Path, "settings.json");
        var store = new SettingsStore(path);
        store.Load();
        AppSettings? notified = null;
        store.Changed += (_, s) => notified = s;

        store.Update(s => s with { MaxItems = -5, IgnoredApps = [" KeePass.exe ", "keepass.exe", ""], Placement = (FlyoutPlacement)42 });

        Assert.Equal(0, store.Current.MaxItems);
        Assert.Equal(["KeePass.exe"], store.Current.IgnoredApps);
        Assert.Equal(FlyoutPlacement.NearCaret, store.Current.Placement);
        Assert.Same(store.Current, notified);

        var reloaded = new SettingsStore(path).Load();
        Assert.Equal(store.Current.IgnoredApps, reloaded.IgnoredApps);
        Assert.Contains("\"NearCaret\"", File.ReadAllText(path)); // enums persist by name
    }

    /// <summary>Process names from user input are normalized for the ignore list.</summary>
    /// <param name="input">User input.</param>
    /// <param name="expected">Normalized name.</param>
    [Theory]
    [InlineData("KeePass.exe", "KeePass")]
    [InlineData(@"C:\Program Files\KeePass\KeePass.EXE", "KeePass")]
    [InlineData("  1Password  ", "1Password")]
    [InlineData("", "")]
    public void NormalizeProcessName(string input, string expected)
    {
        Assert.Equal(expected, CaptureRules.NormalizeProcessName(input));
    }

    /// <summary>Settings map onto capture rules (MB → bytes, days → TimeSpan, 0 = unlimited).</summary>
    [Fact]
    public void Rules_FromSettings()
    {
        var rules = CaptureRules.FromSettings(new AppSettings { MaxItemSizeMB = 2, RetentionDays = 0, MaxTotalSizeMB = 1, IgnoredApps = ["KeePass.exe"] });
        Assert.Equal(2L * 1024 * 1024, rules.MaxItemBytes);
        Assert.Null(rules.Retention.MaxAge);
        Assert.Equal(1024L * 1024, rules.Retention.MaxTotalBytes);
        Assert.True(rules.IsIgnored(new SourceAppInfo("keepass", null, "KeePass")));
        Assert.False(rules.IsIgnored(null));
    }

    /// <summary>Long terms go to the trigram index, short ones to escaped LIKE patterns.</summary>
    [Fact]
    public void SearchQueryBuilder_SplitsTerms()
    {
        var (fts, likes) = SearchQueryBuilder.Build("clip \"quoted\" 5% ab");
        Assert.Equal("\"clip\" AND \"\"\"quoted\"\"\"", fts);
        Assert.Equal([@"%5\%%", "%ab%"], likes);

        var (blankFts, blankLikes) = SearchQueryBuilder.Build("   ");
        Assert.Null(blankFts);
        Assert.Empty(blankLikes);
    }

    /// <summary>A single emoji is one text element, so it is matched with LIKE (trigrams need 3 chars).</summary>
    [Fact]
    public void SearchQueryBuilder_CountsTextElements()
    {
        var (fts, likes) = SearchQueryBuilder.Build("😀");
        Assert.Null(fts);
        Assert.Single(likes);
    }
}
