using System.Text.Json;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Settings;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for Settings › App › Keep the panel in memory (CLAUDE.md §2.69, issue #14): the slider's stops and positions, what a
/// settings file may hold, when a reclaim also hands the panel's pages to Windows, how soon the reclaim after a use runs, the
/// words for every stop in every language and in the log, the setting's default and round trip, and the log's phrase for a
/// trim.
/// </summary>
public sealed class PanelMemoryTests : IDisposable
{
    /// <summary>A fixed moment for the rule's clock: the tests never read the real one.</summary>
    private static readonly DateTimeOffset LastUse = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory temp = TestData.NewTempDirectory();

    /// <summary>Deletes the temp directory.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>
    /// The stops run from 1 second to 7 days, longer each time, and each is a whole number of the unit it is worded in, so a
    /// label never needs a fraction ("For 1.5 hours").
    /// </summary>
    [Fact]
    public void Stops_RunFromOneSecondToSevenDays_EachAWholeUnit()
    {
        Assert.Equal(1, PanelMemory.StopSeconds[0]);
        Assert.Equal(PanelMemory.MaxKeepSeconds, PanelMemory.StopSeconds[^1]);
        Assert.Equal(7 * 86_400, PanelMemory.MaxKeepSeconds);
        for (int i = 1; i < PanelMemory.StopSeconds.Count; i++)
        {
            Assert.True(PanelMemory.StopSeconds[i] > PanelMemory.StopSeconds[i - 1], $"stop {i} does not grow");
        }

        foreach (int seconds in PanelMemory.StopSeconds)
        {
            int unit = seconds >= 86_400 ? 86_400 : seconds >= 3_600 ? 3_600 : seconds >= 60 ? 60 : 1;
            Assert.Equal(0, seconds % unit);
        }

        Assert.Equal(PanelMemory.StopSeconds.Count + 1, PanelMemory.NeverReleasePosition);
        Assert.Equal(0, PanelMemory.NeverKeepPosition);
    }

    /// <summary>
    /// Every slider position stands for a keep time that shows at the same position again; the ends are "never keep" (0) and
    /// "never release" (null), and positions beyond the slider are taken as its ends.
    /// </summary>
    [Fact]
    public void Positions_RoundTripThroughTheirKeepTimes()
    {
        for (int position = PanelMemory.NeverKeepPosition; position <= PanelMemory.NeverReleasePosition; position++)
        {
            Assert.Equal(position, PanelMemory.PositionOf(PanelMemory.KeepSecondsAt(position)));
        }

        Assert.Equal(0, PanelMemory.KeepSecondsAt(PanelMemory.NeverKeepPosition));
        Assert.Null(PanelMemory.KeepSecondsAt(PanelMemory.NeverReleasePosition));
        Assert.Equal(1, PanelMemory.KeepSecondsAt(1));
        Assert.Equal(0, PanelMemory.KeepSecondsAt(-5));
        Assert.Null(PanelMemory.KeepSecondsAt(PanelMemory.NeverReleasePosition + 3));
        Assert.Equal(PanelMemory.NeverReleasePosition, PanelMemory.PositionOf(null));
        Assert.Equal(PanelMemory.NeverReleasePosition, PanelMemory.PositionOf(-1));
    }

    /// <summary>
    /// A settings file keeps only what the slider can show: the stops and both ends as they are, a hand-written negative
    /// number ("-1 = forever") as "never release", and anything else as its nearest stop on a logarithmic scale (45 seconds
    /// is nearer 1 minute than 30 seconds), at most 7 days.
    /// </summary>
    /// <param name="stored">What the file says (-999 stands for null, which xunit's inline data cannot pass as int?).</param>
    /// <param name="expected">What the setting becomes (-999 again for null).</param>
    [Theory]
    [InlineData(-999, -999)]
    [InlineData(-1, -999)]
    [InlineData(-100, -999)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 5)]
    [InlineData(40, 30)]
    [InlineData(45, 60)]
    [InlineData(90, 120)]
    [InlineData(600, 600)]
    [InlineData(86_400, 86_400)]
    [InlineData(604_800, 604_800)]
    [InlineData(2_592_000, 604_800)]
    [InlineData(int.MaxValue, 604_800)]
    public void Normalize_KeepsTheStopsAndMovesTheRestToTheNearest(int stored, int expected)
    {
        int? input = stored == -999 ? null : stored;
        int? output = expected == -999 ? null : expected;
        Assert.Equal(output, PanelMemory.Normalize(input));

        // The slider shows a normalized value where the raw one would show: Normalize and PositionOf agree.
        Assert.Equal(PanelMemory.PositionOf(input), PanelMemory.PositionOf(PanelMemory.Normalize(input)));
    }

    /// <summary>
    /// Over a sweep of raw values, normalizing twice changes nothing more than once, and every normalized value is a stop, 0
    /// or null.
    /// </summary>
    [Fact]
    public void Normalize_IsIdempotentAndLandsOnTheSlider()
    {
        var allowed = new HashSet<int?>(PanelMemory.StopSeconds.Select(s => (int?)s)) { 0, null };
        for (long raw = -3; raw <= 2_000_000; raw = raw < 100 ? raw + 1 : raw * 11 / 10)
        {
            var once = PanelMemory.Normalize((int)raw);
            Assert.Equal(once, PanelMemory.Normalize(once));
            Assert.Contains(once, allowed);
        }
    }

    /// <summary>
    /// The trim rule: "never release" never trims, whatever the time since the last use; "never keep" always does, also when
    /// the clock moved back since; a keep time trims from its end on, not a millisecond before.
    /// </summary>
    [Fact]
    public void ShouldTrim_FollowsTheKeepTime()
    {
        Assert.False(PanelMemory.ShouldTrim(null, LastUse, LastUse.AddYears(3)));
        Assert.False(PanelMemory.ShouldTrim(-1, LastUse, LastUse.AddYears(3)));

        Assert.True(PanelMemory.ShouldTrim(0, LastUse, LastUse));
        Assert.True(PanelMemory.ShouldTrim(0, LastUse, LastUse.AddMinutes(-5)));

        Assert.False(PanelMemory.ShouldTrim(600, LastUse, LastUse.AddSeconds(600).AddMilliseconds(-1)));
        Assert.True(PanelMemory.ShouldTrim(600, LastUse, LastUse.AddSeconds(600)));
        Assert.True(PanelMemory.ShouldTrim(600, LastUse, LastUse.AddDays(2)));
        Assert.False(PanelMemory.ShouldTrim(600, LastUse, LastUse.AddMinutes(-1)));
        Assert.True(PanelMemory.ShouldTrim(PanelMemory.MaxKeepSeconds, LastUse, LastUse.AddDays(7)));
        Assert.False(PanelMemory.ShouldTrim(PanelMemory.MaxKeepSeconds, LastUse, LastUse.AddDays(7).AddSeconds(-1)));
    }

    /// <summary>When the keep time ends: the last use plus the keep time; at once for "never keep"; never for "never release".</summary>
    [Fact]
    public void TrimDueAt_IsTheLastUsePlusTheKeepTime()
    {
        Assert.Null(PanelMemory.TrimDueAt(null, LastUse));
        Assert.Null(PanelMemory.TrimDueAt(-1, LastUse));
        Assert.Equal(LastUse, PanelMemory.TrimDueAt(0, LastUse));
        Assert.Equal(LastUse.AddMinutes(10), PanelMemory.TrimDueAt(600, LastUse));
        Assert.Equal(LastUse.AddDays(7), PanelMemory.TrimDueAt(PanelMemory.MaxKeepSeconds, LastUse));
    }

    /// <summary>
    /// The reclaim after a use waits its usual 30 seconds, or the keep time when that is shorter, so "never keep" and keep
    /// times of seconds trim that soon; a keep time as long as the usual delay, or longer, leaves the delay alone.
    /// </summary>
    [Fact]
    public void ReclaimDelayAfterUse_IsTheShorterOfTheTwo()
    {
        var usual = TimeSpan.FromSeconds(30);
        Assert.Equal(usual, PanelMemory.ReclaimDelayAfterUse(null, usual));
        Assert.Equal(usual, PanelMemory.ReclaimDelayAfterUse(-1, usual));
        Assert.Equal(TimeSpan.Zero, PanelMemory.ReclaimDelayAfterUse(0, usual));
        Assert.Equal(TimeSpan.FromSeconds(1), PanelMemory.ReclaimDelayAfterUse(1, usual));
        Assert.Equal(TimeSpan.FromSeconds(10), PanelMemory.ReclaimDelayAfterUse(10, usual));
        Assert.Equal(usual, PanelMemory.ReclaimDelayAfterUse(30, usual));
        Assert.Equal(usual, PanelMemory.ReclaimDelayAfterUse(600, usual));
    }

    /// <summary>The words under the slider in English: both ends, singular and plural of each unit, a value between stops.</summary>
    /// <param name="seconds">The keep time (-999 for null).</param>
    /// <param name="expected">The label.</param>
    [Theory]
    [InlineData(-999, "Never release from memory")]
    [InlineData(-1, "Never release from memory")]
    [InlineData(0, "Never keep in memory")]
    [InlineData(1, "For 1 second")]
    [InlineData(2, "For 2 seconds")]
    [InlineData(30, "For 30 seconds")]
    [InlineData(60, "For 1 minute")]
    [InlineData(600, "For 10 minutes")]
    [InlineData(3_600, "For 1 hour")]
    [InlineData(43_200, "For 12 hours")]
    [InlineData(86_400, "For 1 day")]
    [InlineData(604_800, "For 7 days")]
    [InlineData(90, "For 90 seconds")]
    public void Describe_WordsEachStopInEnglish(int seconds, string expected) =>
        Assert.Equal(expected, PanelMemory.Describe(seconds == -999 ? null : seconds));

    /// <summary>
    /// Every position's words in every shipped language: translated (never the English text), and holding the count wherever
    /// it is 3 or more (Hebrew words 1 and 2 out, as its catalog does elsewhere); Russian picks its three forms by the count.
    /// </summary>
    [Fact]
    public void Describe_EveryPositionInEveryLanguage_IsTranslated()
    {
        var previous = UiText.Language;
        try
        {
            foreach (var language in new[] { "zh-Hans", "ru", "es", "he" })
            {
                for (int position = PanelMemory.NeverKeepPosition; position <= PanelMemory.NeverReleasePosition; position++)
                {
                    var keep = PanelMemory.KeepSecondsAt(position);
                    UiText.Language = "en";
                    string english = PanelMemory.Describe(keep);
                    UiText.Language = language;
                    string translated = PanelMemory.Describe(keep);
                    Assert.False(string.IsNullOrWhiteSpace(translated));
                    Assert.NotEqual(english, translated);
                    if (keep is { } seconds && seconds > 0)
                    {
                        long count = seconds % 86_400 == 0 ? seconds / 86_400 : seconds % 3_600 == 0 ? seconds / 3_600 : seconds % 60 == 0 ? seconds / 60 : seconds;
                        if (count >= 3)
                        {
                            Assert.Contains(count.ToString(System.Globalization.CultureInfo.InvariantCulture), translated, StringComparison.Ordinal);
                        }
                    }
                }
            }

            UiText.Language = "ru";
            Assert.Equal("1 секунду", PanelMemory.Describe(1));
            Assert.Equal("2 секунды", PanelMemory.Describe(2));
            Assert.Equal("5 секунд", PanelMemory.Describe(5));
            Assert.Equal("30 секунд", PanelMemory.Describe(30));
            Assert.Equal("1 час", PanelMemory.Describe(3_600));
            Assert.Equal("12 часов", PanelMemory.Describe(43_200));
            Assert.Equal("2 дня", PanelMemory.Describe(172_800));
            UiText.Language = "zh-Hans";
            Assert.Equal("保留 10 分钟", PanelMemory.Describe(600));
            UiText.Language = "he";
            Assert.Contains("שעתיים", PanelMemory.Describe(7_200), StringComparison.Ordinal);
        }
        finally
        {
            UiText.Language = previous;
        }
    }

    /// <summary>The log's words stay English whatever the interface language (logs never carry translated text).</summary>
    [Fact]
    public void DescribeForLog_IsEnglishInEveryLanguage()
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = "ru";
            Assert.Equal("never released", PanelMemory.DescribeForLog(null));
            Assert.Equal("never released", PanelMemory.DescribeForLog(-1));
            Assert.Equal("never kept", PanelMemory.DescribeForLog(0));
            Assert.Equal("1 second", PanelMemory.DescribeForLog(1));
            Assert.Equal("30 seconds", PanelMemory.DescribeForLog(30));
            Assert.Equal("1 minute", PanelMemory.DescribeForLog(60));
            Assert.Equal("10 minutes", PanelMemory.DescribeForLog(600));
            Assert.Equal("4 hours", PanelMemory.DescribeForLog(14_400));
            Assert.Equal("2 days", PanelMemory.DescribeForLog(172_800));
        }
        finally
        {
            UiText.Language = previous;
        }
    }

    /// <summary>
    /// A new install, and a settings file from before the setting existed, never release the panel's memory (0.2.12's way); a
    /// keep time saved survives a reload under the member's own name, both ends included; a hand-edited value lands on the
    /// slider without the file being set aside.
    /// </summary>
    [Fact]
    public void Setting_DefaultsRoundTripsAndKeepsItsName()
    {
        Assert.Null(new AppSettings().KeepPanelInMemorySeconds);

        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ \"Theme\": \"Dark\" }");
        Assert.Null(new SettingsStore(path).Load().KeepPanelInMemorySeconds);

        var store = new SettingsStore(path);
        store.Load();
        store.Update(s => s with { KeepPanelInMemorySeconds = 600 });
        Assert.Equal(600, new SettingsStore(path).Load().KeepPanelInMemorySeconds);
        Assert.Contains("\"KeepPanelInMemorySeconds\": 600", File.ReadAllText(path), StringComparison.Ordinal);

        store.Update(s => s with { KeepPanelInMemorySeconds = 0 });
        Assert.Equal(0, new SettingsStore(path).Load().KeepPanelInMemorySeconds);
        store.Update(s => s with { KeepPanelInMemorySeconds = null });
        Assert.Null(new SettingsStore(path).Load().KeepPanelInMemorySeconds);

        File.WriteAllText(path, "{ \"KeepPanelInMemorySeconds\": 45 }");
        Assert.Equal(60, new SettingsStore(path).Load().KeepPanelInMemorySeconds);
        File.WriteAllText(path, "{ \"KeepPanelInMemorySeconds\": -1 }");
        Assert.Null(new SettingsStore(path).Load().KeepPanelInMemorySeconds);
        File.WriteAllText(path, "{ \"KeepPanelInMemorySeconds\": 99999999 }");
        Assert.Equal(PanelMemory.MaxKeepSeconds, new SettingsStore(path).Load().KeepPanelInMemorySeconds);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.corrupt*"));

        Assert.Contains("\"KeepPanelInMemorySeconds\":30", JsonSerializer.Serialize(new AppSettings { KeepPanelInMemorySeconds = 30 }), StringComparison.Ordinal);
    }

    /// <summary>
    /// The log's phrase for a trim names the memory Task Manager shows before and after (one decimal below 10 MB), and the
    /// working set where Windows does not report the private part.
    /// </summary>
    [Fact]
    public void DescribeWorkingSetChange_SaysWhatTaskManagerShows()
    {
        const long Mb = 1024 * 1024;
        var before = new MemoryUsage(140 * Mb, 180 * Mb, 45 * Mb, 7 * Mb);
        var after = new MemoryUsage(139 * Mb, Mb / 2, Mb * 3 / 10, 7 * Mb);
        Assert.Equal("memory 45 → 0.3 MB", MemoryUsage.DescribeWorkingSetChange(before, after));
        Assert.Equal("working set 180 → 0.5 MB", MemoryUsage.DescribeWorkingSetChange(before with { PrivateWorkingSetBytes = null }, after));
        Assert.Throws<ArgumentNullException>(() => MemoryUsage.DescribeWorkingSetChange(null!, after));
    }
}
