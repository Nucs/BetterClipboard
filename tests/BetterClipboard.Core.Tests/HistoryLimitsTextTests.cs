using System.Globalization;
using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the words of the history's limits (CLAUDE.md §2.60): Settings' "Now:" line under each limit, its warning, and
/// the notification, in English; percentages rounded down; the service's full mark keeping Settings at "full"; and every
/// shipped language's notification held within what Windows takes (63 characters of title, 255 of text) and within what a
/// Windows 11 toast shows of them (two lines of title, four of text) at the longest sizes it can name.
/// </summary>
public sealed class HistoryLimitsTextTests
{
    private const long MB = 1024L * 1024;
    private const long GB = 1024L * MB;

    /// <summary>The item limit's line: the count, the share of a limit when there is one, and what pins and groups hold.</summary>
    [Fact]
    public void ItemsNow_CountsAndShare() => Invariant(() =>
    {
        Assert.Equal("Now: 3,214 items · 12 pinned or in groups, not counted", HistoryLimitsText.ItemsNow(Stats(counted: 3214, kept: 12), 0));
        Assert.Equal("Now: 3,214 items · 64 % of the limit · 12 pinned or in groups, not counted", HistoryLimitsText.ItemsNow(Stats(counted: 3214, kept: 12), 5000));
        Assert.Equal("Now: 1 item · 0.1 % of the limit · 1 pinned or in a group, not counted", HistoryLimitsText.ItemsNow(Stats(counted: 1, kept: 1), 1000));
        Assert.Equal("Now: 0 items", HistoryLimitsText.ItemsNow(Stats(), 0));
        Assert.Equal("Now: 0 items · 0 % of the limit", HistoryLimitsText.ItemsNow(Stats(), 10));
    });

    /// <summary>The age limit's line: when the item it reaches first was last used, in whole days, rounded down.</summary>
    [Fact]
    public void AgeNow_WholeDays() => Invariant(() =>
    {
        var now = TestData.Now;
        Assert.Equal("Now: no items yet", HistoryLimitsText.AgeNow(Stats(), now));
        Assert.Equal("Now: the oldest item was last used today", HistoryLimitsText.AgeNow(Stats(counted: 1, oldest: now.AddHours(-23)), now));
        Assert.Equal("Now: the oldest item was last used today", HistoryLimitsText.AgeNow(Stats(counted: 1, oldest: now.AddHours(5)), now));
        Assert.Equal("Now: the oldest item was last used 1 day ago", HistoryLimitsText.AgeNow(Stats(counted: 1, oldest: now.AddHours(-25)), now));
        Assert.Equal("Now: the oldest item was last used 1,040 days ago", HistoryLimitsText.AgeNow(Stats(counted: 1, oldest: now.AddDays(-1040.5)), now));
    });

    /// <summary>The largest item's line, pinned and grouped items included; nothing to name in an empty history.</summary>
    [Fact]
    public void LargestNow_NamesTheLargest() => Invariant(() =>
    {
        Assert.Equal("Now: no items yet", HistoryLimitsText.LargestNow(Stats(largest: 0)));
        Assert.Equal("Now: the largest item is 37.2 MB", HistoryLimitsText.LargestNow(Stats(counted: 2, largest: 39_007_028)));
        Assert.Equal("Now: the largest item is 512 B", HistoryLimitsText.LargestNow(Stats(kept: 1, largest: 512)));
    });

    /// <summary>
    /// The budget's line: what it counts against the budget with the share rounded down, or alone without a budget; the
    /// file on disk; and what pins and groups hold outside it.
    /// </summary>
    [Fact]
    public void BudgetNow_AgainstTheBudget() => Invariant(() =>
    {
        var stats = Stats(counted: 900, bytes: 15_783_867_597, kept: 4, keptBytes: 300 * MB, database: 19_541_812_019);
        Assert.Equal(
            "Now: 14.7 GB of 16 GB used (91 %) · the history file takes 18.2 GB on disk · pinned and grouped items (300 MB) are not counted",
            HistoryLimitsText.BudgetNow(stats, 16_384));
        Assert.Equal(
            "Now: 14.7 GB used, no budget · the history file takes 18.2 GB on disk · pinned and grouped items (300 MB) are not counted",
            HistoryLimitsText.BudgetNow(stats, 0));
        Assert.Equal("Now: 0 B of 16 GB used (0 %) · the history file takes 4 KB on disk", HistoryLimitsText.BudgetNow(Stats(database: 4096), 16_384));
    });

    /// <summary>The temporary pictures' line: how many and their size, or none.</summary>
    [Fact]
    public void TempPicturesNow_CountAndSize() => Invariant(() =>
    {
        Assert.Equal("Now: no temporary pictures", HistoryLimitsText.TempPicturesNow(0, 0));
        Assert.Equal("Now: 1 picture, 4.2 MB", HistoryLimitsText.TempPicturesNow(1, 4_404_019));
        Assert.Equal("Now: 3 pictures, 12 MB", HistoryLimitsText.TempPicturesNow(3, 12 * MB));
    });

    /// <summary>
    /// The notification and Settings' warning, in English: the level and what follows in the notification's title, what
    /// it means and what to do in its text, and the whole story under the budget's card.
    /// </summary>
    [Fact]
    public void Warning_WordsTheLevelAndTheWayOut() => Invariant(() =>
    {
        Assert.Equal("Clipboard history at 90 % of its size budget", HistoryLimitsText.WarningTitle(90));
        Assert.Equal("Clipboard history at 98 % of its size budget", HistoryLimitsText.WarningTitle(98));
        Assert.Equal("Clipboard history is full", HistoryLimitsText.WarningTitle(100));

        Assert.Equal("Clipboard history 92 % full: the oldest items will be deleted", HistoryLimitsText.NotificationTitle(92));
        Assert.Equal("Clipboard history is full: the oldest items are being deleted", HistoryLimitsText.NotificationTitle(100));
        Assert.Equal(
            "When it fills up, your history stops being permanent. Click for Settings, or let an AI assistant clean up with bclip.",
            HistoryLimitsText.NotificationText(new HistoryBudgetWarning(92, 15_783_867_597, 16 * GB)));
        Assert.Equal(
            "The 16 GB budget is full, so your history is no longer permanent. Click for Settings, or let an AI assistant clean up with bclip.",
            HistoryLimitsText.NotificationText(new HistoryBudgetWarning(100, 16 * GB, 16 * GB)));
        Assert.Throws<ArgumentNullException>(() => HistoryLimitsText.NotificationText(null!));

        var soon = HistoryLimitsText.WarningMessage(94);
        Assert.StartsWith("Once the size budget is full, BetterClipboard removes the items you used least recently", soon, StringComparison.Ordinal);
        Assert.EndsWith("bclip list --sort size the largest items, and bclip delete removes the ones you pick.", soon, StringComparison.Ordinal);
        Assert.StartsWith("The size budget is full: BetterClipboard now removes", HistoryLimitsText.WarningMessage(100), StringComparison.Ordinal);
    });

    /// <summary>
    /// Percentages: rounded down (89.9 never reads 90 while no warning shows), one decimal below 10, and 100 once the budget
    /// counts as full.
    /// </summary>
    /// <param name="percent">The percentage.</param>
    /// <param name="expected">As shown.</param>
    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(-3.0, "0")]
    [InlineData(double.NaN, "0")]
    [InlineData(0.04, "0")]
    [InlineData(0.37, "0.3")]
    [InlineData(9.99, "9.9")]
    [InlineData(10.0, "10")]
    [InlineData(89.99, "89")]
    [InlineData(99.49, "99")]
    [InlineData(99.5, "100")]
    [InlineData(250.0, "100")]
    public void PercentText_RoundsDown(double percent, string expected) =>
        Assert.Equal(expected, HistoryLimitsText.PercentText(percent, CultureInfo.InvariantCulture));

    /// <summary>The bar's value and the warning's level follow the budget, and are absent without one.</summary>
    [Fact]
    public void BudgetPercentAndLevel()
    {
        var stats = Stats(bytes: 15_783_867_597);
        Assert.Equal(91.874, HistoryLimitsText.BudgetPercent(stats, 16_384)!.Value, 3);
        Assert.Equal(90, HistoryLimitsText.BudgetLevel(stats, 16_384));
        Assert.Null(HistoryLimitsText.BudgetPercent(stats, 0));
        Assert.Null(HistoryLimitsText.BudgetLevel(stats, 0));
        Assert.Null(HistoryLimitsText.BudgetLevel(Stats(bytes: GB), 16_384));
        Assert.Equal(100, HistoryLimitsText.BudgetLevel(Stats(bytes: 16 * GB), 16_384));
        Assert.Throws<ArgumentNullException>(() => HistoryLimitsText.BudgetPercent(null!, 1));

        // The service's full mark: a budget kept a few per cent under full still shows full (also a mark whose budget is not
        // known); a mark reached under another budget is left out, the budget having changed a moment ago.
        Assert.Equal(100, HistoryLimitsText.BudgetLevel(stats, 16_384, new StoredBudgetMark(100, 16 * GB)));
        Assert.Equal(100, HistoryLimitsText.BudgetLevel(stats, 16_384, new StoredBudgetMark(100, 0)));
        Assert.Equal(90, HistoryLimitsText.BudgetLevel(stats, 16_384, new StoredBudgetMark(94, 16 * GB)));
        Assert.Equal(90, HistoryLimitsText.BudgetLevel(stats, 16_384, new StoredBudgetMark(100, 15 * GB)));
        Assert.Null(HistoryLimitsText.BudgetLevel(stats, 65_536, new StoredBudgetMark(100, 16 * GB)));
        Assert.Null(HistoryLimitsText.BudgetLevel(stats, 0, new StoredBudgetMark(100, 16 * GB)));
    }

    /// <summary>
    /// Every shipped language's notification fits what a Windows 11 toast shows, with a little less room than measured (a
    /// title of 2 lines of 35 characters, a text of 4 lines of 38; measured about 37 and 41, and the first wording lost its
    /// last sentence): its title at every level within Windows' 63 characters too, and its text at the longest size a
    /// budget can have (the largest budget Settings allows, 1 TB); and the "Now:" lines are worded.
    /// </summary>
    [Fact]
    public void EveryLanguage_FitsTheNotification()
    {
        var previous = UiText.Language;
        try
        {
            foreach (var language in UiText.AllShipped)
            {
                UiText.Language = language.Tag;
                foreach (int level in new[] { 90, 92, 94, 96, 98, 100 })
                {
                    var title = HistoryLimitsText.NotificationTitle(level);
                    Assert.True(title.Length <= HistoryLimitsText.MaxNotificationTitle, $"{language.Tag} title at {level}: {title.Length} characters");
                    Assert.True(ToastLines(title, 35) <= 2, $"{language.Tag} title at {level}: {ToastLines(title, 35)} lines");
                }

                foreach (var warning in new[] { new HistoryBudgetWarning(98, 1_099_500_000_000, 1024 * GB), new HistoryBudgetWarning(100, 1023 * GB, 1024 * GB) })
                {
                    var text = HistoryLimitsText.NotificationText(warning);
                    Assert.True(text.Length <= HistoryLimitsText.MaxNotificationText, $"{language.Tag} text at {warning.Level}: {text.Length} characters");
                    Assert.True(ToastLines(text, 38) <= 4, $"{language.Tag} text at {warning.Level}: {ToastLines(text, 38)} lines");
                }

                Assert.False(string.IsNullOrWhiteSpace(HistoryLimitsText.WarningMessage(96)));
                var line = HistoryLimitsText.BudgetNow(Stats(counted: 2, bytes: 3 * GB, kept: 2, keptBytes: MB, database: 4 * GB), 16_384);
                Assert.Contains("18", line, StringComparison.Ordinal); // 18 %, in whatever words
            }
        }
        finally
        {
            UiText.Language = previous;
        }
    }

    /// <summary>
    /// How many lines a toast wraps a text into, greedily by words, at a width in characters: Chinese, Japanese and Korean
    /// characters (and full-width signs) count two and may break anywhere, the way a toast breaks them.
    /// </summary>
    /// <param name="text">The text (direction marks and other format characters take no room).</param>
    /// <param name="width">The line's width in narrow characters.</param>
    /// <returns>The number of lines.</returns>
    private static int ToastLines(string text, int width)
    {
        static int Width(char c) => char.GetUnicodeCategory(c) == UnicodeCategory.Format ? 0
            : c is (>= '\u1100' and <= '\u115F') or (>= '\u2E80' and <= '\uA4CF') or (>= '\uAC00' and <= '\uD7A3')
                or (>= '\uF900' and <= '\uFAFF') or (>= '\uFE30' and <= '\uFE4F') or (>= '\uFF00' and <= '\uFF60') ? 2 : 1;

        // A wide character is a word of its own: the toast breaks between any two of them.
        var words = new List<string>();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var run = new System.Text.StringBuilder();
            foreach (char c in word)
            {
                if (Width(c) == 2)
                {
                    if (run.Length > 0)
                    {
                        words.Add(run.ToString());
                        run.Clear();
                    }

                    words.Add(c.ToString());
                }
                else
                {
                    run.Append(c);
                }
            }

            if (run.Length > 0)
            {
                words.Add(run.ToString());
            }
        }

        int lines = 0, used = 0;
        foreach (var word in words)
        {
            int size = word.Sum(Width);
            int needed = used == 0 ? size : used + 1 + size;
            if (used > 0 && needed > width)
            {
                lines++;
                used = size;
            }
            else
            {
                used = needed;
            }
        }

        return used > 0 ? lines + 1 : lines;
    }

    /// <summary>Runs a check with invariant number formats, so the expected texts do not follow this PC's formats.</summary>
    /// <param name="check">The check.</param>
    private static void Invariant(Action check)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            check();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>What the limits count, with whatever a check needs.</summary>
    /// <param name="counted">Entries the limits count.</param>
    /// <param name="bytes">Their bytes.</param>
    /// <param name="kept">Pinned or grouped entries.</param>
    /// <param name="keptBytes">Their bytes.</param>
    /// <param name="oldest">When the counted entry the age limit reaches first was last used.</param>
    /// <param name="largest">The largest entry's bytes.</param>
    /// <param name="database">The database file's bytes.</param>
    /// <returns>The stats.</returns>
    private static RetentionStats Stats(long counted = 0, long bytes = 0, long kept = 0, long keptBytes = 0, DateTimeOffset? oldest = null, long largest = 0, long database = 0) =>
        new(counted, bytes, kept, keptBytes, oldest, largest, database);
}
