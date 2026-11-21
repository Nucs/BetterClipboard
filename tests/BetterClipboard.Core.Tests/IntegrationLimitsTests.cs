using System.Text.Json;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// The integrations' limits (CLAUDE.md §2.62): the six settings and their rule (<see cref="ListLimit"/>: 0 = unlimited, the
/// default, for every settings file old or new), the pager the Pwsh, Cmd and Everything tabs hand their cards out with
/// (<see cref="RowPages{TRow}"/>), and the batched "Forget forever" lookup those tabs ask about every row with.
/// </summary>
public sealed class IntegrationLimitsTests : IDisposable
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();

    /// <summary>Deletes the temp directory and its files.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>The six limits' names in the settings file, each with the value a test stores under it.</summary>
    public static TheoryData<string, int> Limits => new()
    {
        { nameof(AppSettings.MaxShareXCatchUp), 7 },
        { nameof(AppSettings.MaxScreenshotsCatchUp), 8 },
        { nameof(AppSettings.MaxHiddenEverythingPicks), 900 },
        { nameof(AppSettings.MaxHiddenPowerShellCommands), 1200 },
        { nameof(AppSettings.MaxKeptCmdCommands), 5000 },
        { nameof(AppSettings.MaxHiddenCmdCommands), 3 },
    };

    /// <summary>The six limits' names in the settings file.</summary>
    public static TheoryData<string> LimitNames => new(Limits.Select(row => (string)row.Data.Item1));

    /// <summary>A limit as a stored setting, by its name in the file (the theory's way to reach each member).</summary>
    /// <param name="settings">The settings.</param>
    /// <param name="name">The member's name.</param>
    /// <returns>Its value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="name"/> names no limit (a typo in a test's data).</exception>
    private static int LimitOf(AppSettings settings, string name) => name switch
    {
        nameof(AppSettings.MaxShareXCatchUp) => settings.MaxShareXCatchUp,
        nameof(AppSettings.MaxScreenshotsCatchUp) => settings.MaxScreenshotsCatchUp,
        nameof(AppSettings.MaxHiddenEverythingPicks) => settings.MaxHiddenEverythingPicks,
        nameof(AppSettings.MaxHiddenPowerShellCommands) => settings.MaxHiddenPowerShellCommands,
        nameof(AppSettings.MaxKeptCmdCommands) => settings.MaxKeptCmdCommands,
        nameof(AppSettings.MaxHiddenCmdCommands) => settings.MaxHiddenCmdCommands,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a limit."),
    };

    /// <summary>
    /// Every limit is unlimited on a new install and in every file from before (which lacks the members: the old caps lived
    /// in code), so nothing needed moving for "migrate all to default unlimited".
    /// </summary>
    /// <param name="name">The limit's name in the file.</param>
    [Theory]
    [MemberData(nameof(LimitNames))]
    public void Defaults_AndOlderFiles_AreUnlimited(string name)
    {
        Assert.Equal(0, LimitOf(new AppSettings(), name));
        Assert.Equal(0, LimitOf(new SettingsStore(Path.Combine(temp.Path, "new.json")).Load(), name));

        // A format-1 file and a format-2 file written before these members existed.
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ \"SchemaVersion\": 1, \"MaxItems\": 10000, \"ShowCmdTab\": true }");
        Assert.Equal(0, LimitOf(new SettingsStore(path).Load(), name));
        File.WriteAllText(path, "{ \"SchemaVersion\": 2, \"ImportShareXScreenshots\": true }");
        Assert.Equal(0, LimitOf(new SettingsStore(path).Load(), name));
    }

    /// <summary>
    /// A limit survives a save and a load under its own name; a hand-edited negative one reads 0 (no limit) and a huge one
    /// the boxes' maximum.
    /// </summary>
    /// <param name="name">The limit's name in the file.</param>
    /// <param name="value">The value stored.</param>
    [Theory]
    [MemberData(nameof(Limits))]
    public void Limit_RoundTrips_AndIsKeptWithinItsBounds(string name, int value)
    {
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, $"{{ \"{name}\": {value} }}");
        var store = new SettingsStore(path);
        Assert.Equal(value, LimitOf(store.Load(), name));

        // Saved by an unrelated change, it is written back under its own name.
        store.Update(s => s with { LinesPerCard = 5 });
        using (var file = JsonDocument.Parse(File.ReadAllText(path)))
        {
            Assert.Equal(value, file.RootElement.GetProperty(name).GetInt32());
        }

        Assert.Equal(value, LimitOf(new SettingsStore(path).Load(), name));

        File.WriteAllText(path, $"{{ \"{name}\": -5 }}");
        Assert.Equal(0, LimitOf(new SettingsStore(path).Load(), name));
        File.WriteAllText(path, $"{{ \"{name}\": 2000000000 }}");
        Assert.Equal(ListLimit.Maximum, LimitOf(new SettingsStore(path).Load(), name));
    }

    /// <summary>The rule itself: 0 or less lets everything through, a positive number that many; stored limits are clamped.</summary>
    [Fact]
    public void ListLimit_ZeroIsUnlimited()
    {
        Assert.Equal(int.MaxValue, ListLimit.Effective(0));
        Assert.Equal(int.MaxValue, ListLimit.Effective(-3));
        Assert.Equal(int.MaxValue, ListLimit.Effective(int.MinValue));
        Assert.Equal(1, ListLimit.Effective(1));
        Assert.Equal(100, ListLimit.Effective(100));
        Assert.True(ListLimit.IsUnlimited(0));
        Assert.True(ListLimit.IsUnlimited(-1));
        Assert.False(ListLimit.IsUnlimited(1));
        Assert.Equal((0, 0, 42, ListLimit.Maximum), (ListLimit.Normalize(-1), ListLimit.Normalize(0), ListLimit.Normalize(42), ListLimit.Normalize(int.MaxValue)));

        // What the catch-ups pass to the watchers: every file of a list longer than the old cap of 100.
        Assert.Equal(150, Enumerable.Range(0, 150).TakeLast(ListLimit.Effective(0)).Count());
        Assert.Equal(new[] { 148, 149 }, Enumerable.Range(0, 150).TakeLast(ListLimit.Effective(2)));
    }

    /// <summary>Pages come in display order, each at most the page size, until every row was handed out.</summary>
    [Fact]
    public void RowPages_HandsOutEveryRowInOrder()
    {
        var pages = new RowPages<int>(Enumerable.Range(0, 130));
        Assert.True(pages.HasMore);
        Assert.Equal(130, pages.RemainingCount);
        Assert.Equal(Enumerable.Range(0, 60), pages.Next(60));
        Assert.Equal(70, pages.RemainingCount);
        Assert.Equal(Enumerable.Range(60, 60), pages.Next(60));
        Assert.True(pages.HasMore);
        Assert.Equal(Enumerable.Range(120, 10), pages.Next(60));
        Assert.False(pages.HasMore);
        Assert.Empty(pages.Next(60));
        Assert.Equal(0, pages.RemainingCount);

        Assert.False(RowPages<int>.Empty.HasMore);
        Assert.Empty(RowPages<int>.Empty.Next(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => pages.Next(0));
        Assert.Throws<ArgumentNullException>(() => new RowPages<int>(null!));
    }

    /// <summary>
    /// The footer's count covers the rows still to come; a row dropped before its page comes never shows, and one already
    /// handed out stays the caller's (it removes the card itself).
    /// </summary>
    [Fact]
    public void RowPages_CountsAndForgetsOnlyTheRowsStillToCome()
    {
        var source = Enumerable.Range(0, 10).ToList();
        var pages = new RowPages<int>(source);
        source.Clear();
        Assert.Equal(10, pages.RemainingCount);

        Assert.Equal(new[] { 0, 1, 2, 3 }, pages.Next(4));
        Assert.Equal(3, pages.CountRemaining(i => i % 2 == 0));
        Assert.Equal(0, pages.Forget(i => i == 2));
        Assert.Equal(2, pages.Forget(i => i is 5 or 7));
        Assert.Equal(new[] { 4, 6, 8, 9 }, pages.Next(10));
        Assert.Throws<ArgumentNullException>(() => pages.CountRemaining(null!));
        Assert.Throws<ArgumentNullException>(() => pages.Forget(null!));
    }

    /// <summary>
    /// 2,000 random runs of paging and dropping: the pages joined are always the rows minus the ones dropped before their page,
    /// in order, never longer than the page size; and the counts always add up.
    /// </summary>
    [Fact]
    public void RowPages_RandomRuns_KeepEveryRule()
    {
        var random = new Random(2161);
        for (int run = 0; run < 2000; run++)
        {
            var rows = Enumerable.Range(0, random.Next(0, 300)).ToList();
            var pages = new RowPages<int>(rows);
            var expected = new List<int>(rows);
            var shown = new List<int>();
            while (pages.HasMore)
            {
                if (random.Next(3) == 0 && expected.Count > shown.Count)
                {
                    // Drop a row not shown yet, or one shown already (which must stay).
                    int target = random.Next(rows.Count);
                    int dropped = pages.Forget(i => i == target);
                    if (!shown.Contains(target) && expected.Remove(target))
                    {
                        Assert.Equal(1, dropped);
                    }
                    else
                    {
                        Assert.Equal(0, dropped);
                    }
                }

                Assert.Equal(expected.Count - shown.Count, pages.RemainingCount);
                Assert.Equal(expected.Skip(shown.Count).Count(i => i % 3 == 0), pages.CountRemaining(i => i % 3 == 0));
                int size = random.Next(1, 80);
                var page = pages.Next(size);
                Assert.True(page.Count <= size);
                shown.AddRange(page);
            }

            Assert.Equal(expected, shown);
        }
    }

    /// <summary>
    /// The batched lookup finds exactly the forgotten fingerprints among 1,234 (three chunks of 500), duplicates and blanks
    /// included, and agrees with the one-at-a-time check for each.
    /// </summary>
    [Fact]
    public void FindForgotten_FindsExactlyTheForgottenOnes_AcrossChunks()
    {
        var store = new ClipStore(temp.DatabasePath);
        store.Initialize();
        var secrets = new[] { "BC-TEST secret one", "BC-TEST secret two", "BC-TEST secret three" };
        foreach (var secret in secrets)
        {
            store.ForgetText(secret, "Command Prompt", TestData.Now);
        }

        var forgotten = secrets.Select(s => ForgetFingerprint.ForText(s)!).ToList();
        var asked = Enumerable.Range(0, 1227).Select(i => ForgetFingerprint.ForText($"BC-TEST other {i}")!).ToList();
        asked.Insert(3, forgotten[0]);
        asked.Insert(600, forgotten[1]);
        asked.Add(forgotten[2]);
        asked.Add(forgotten[2]);
        asked.Add(string.Empty);
        asked.Add("   ");
        asked.Add(forgotten[0]);
        Assert.Equal(1234, asked.Count);

        var found = store.FindForgotten(asked);
        Assert.Equal(forgotten.OrderBy(f => f, StringComparer.Ordinal), found.OrderBy(f => f, StringComparer.Ordinal));
        foreach (var fingerprint in asked.Where(f => !string.IsNullOrWhiteSpace(f)).Take(30).Concat(forgotten))
        {
            Assert.Equal(store.IsForgotten(fingerprint), found.Contains(fingerprint));
        }

        Assert.Empty(store.FindForgotten([]));
        Assert.Empty(store.FindForgotten([" ", string.Empty]));
        Assert.Throws<ArgumentNullException>(() => store.FindForgotten(null!));
    }
}
