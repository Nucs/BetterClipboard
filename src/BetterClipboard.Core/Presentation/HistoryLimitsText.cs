using System.Globalization;
using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Presentation;

/// <summary>
/// The words of the history's limits (CLAUDE.md §2.60): Settings › History's "Now:" line under each limit, the warning
/// there, and the notification that the history nears its size budget. Pure, so every wording and every length bound is
/// unit-tested.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the lines count.</b> The item limit and the size budget count only entries that are neither pinned nor in a
/// group (<see cref="RetentionStats.CountedItems"/>, <see cref="RetentionStats.CountedBytes"/>), so the lines say so whenever
/// some entries are left out. The age line names the counted entry the age limit reaches first. The budget line adds the
/// database file's size on disk, which is larger than the payload (its search index and thumbnails).
/// </para>
/// <para>
/// <b>Notification bounds.</b> Windows shows at most <see cref="MaxNotificationTitle"/> characters of a tray notification's
/// title and <see cref="MaxNotificationText"/> of its text (<c>NOTIFYICONDATAW</c>), and cuts the rest; every language's
/// wording is held under them by a test with the longest sizes these texts can carry.
/// </para>
/// </remarks>
public static class HistoryLimitsText
{
    /// <summary>The most characters of a notification's title Windows shows (<c>szInfoTitle</c> holds 64 with its NUL).</summary>
    public const int MaxNotificationTitle = 63;

    /// <summary>The most characters of a notification's text Windows shows (<c>szInfo</c> holds 256 with its NUL).</summary>
    public const int MaxNotificationText = 255;

    /// <summary>
    /// How full the size budget is, in per cent, or <see langword="null"/> without a budget: the bar under Settings' budget
    /// card follows it.
    /// </summary>
    /// <param name="stats">What the limits count now.</param>
    /// <param name="maxTotalSizeMB">The budget (<see cref="Settings.AppSettings.MaxTotalSizeMB"/>); 0 = none.</param>
    /// <returns>The percentage (may pass 100 until the next prune), or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stats"/> is <see langword="null"/>.</exception>
    public static double? BudgetPercent(RetentionStats stats, int maxTotalSizeMB)
    {
        ArgumentNullException.ThrowIfNull(stats);
        return maxTotalSizeMB > 0 ? HistoryBudgetWarnings.PercentUsed(stats.CountedBytes, BudgetBytes(maxTotalSizeMB)) : null;
    }

    /// <summary>
    /// The warning level the budget is at now (<see cref="HistoryBudgetWarnings.LevelOf"/>): 90 to 98, 100 when full, or
    /// <see langword="null"/> below 90 % or without a budget. Settings shows its warning from the first level on.
    /// </summary>
    /// <param name="stats">What the limits count now.</param>
    /// <param name="maxTotalSizeMB">The budget; 0 = none.</param>
    /// <returns>The level, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stats"/> is <see langword="null"/>.</exception>
    public static int? BudgetLevel(RetentionStats stats, int maxTotalSizeMB) =>
        BudgetPercent(stats, maxTotalSizeMB) is { } percent ? HistoryBudgetWarnings.LevelOf(percent, removedForSize: false) : null;

    /// <summary>
    /// A percentage as these texts show it: whole numbers rounded down from 10 % on, one decimal below (so a fresh history
    /// does not read "0 %"), and "100" once the budget counts as full (<see cref="HistoryBudgetWarnings.FullPercent"/>).
    /// Rounded down, never up: a usage of 89.9 % must not read "90 %" while no warning shows.
    /// </summary>
    /// <param name="percent">The percentage.</param>
    /// <param name="culture">The culture for the decimal mark; <see langword="null"/> for the current one.</param>
    /// <returns>The number, without the per cent sign.</returns>
    public static string PercentText(double percent, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (!double.IsFinite(percent) || percent <= 0)
        {
            return 0.ToString(culture);
        }

        if (percent >= HistoryBudgetWarnings.FullPercent)
        {
            return 100.ToString(culture);
        }

        return percent >= 10
            ? Math.Floor(percent).ToString("0", culture)
            : (Math.Floor(percent * 10) / 10).ToString("0.#", culture);
    }

    /// <summary>The notification's title: the level reached, or that the budget is full.</summary>
    /// <param name="level">The level (<see cref="HistoryBudgetWarning.Level"/>): 90 to 98, or 100 when full.</param>
    /// <returns>At most <see cref="MaxNotificationTitle"/> characters in every shipped language (tested).</returns>
    public static string WarningTitle(int level) => level >= HistoryBudgetWarnings.FullLevel
        ? UiText.Translate("Clipboard history is full")
        : UiText.Format($"Clipboard history at {level} % of its size budget");

    /// <summary>
    /// The notification's text: how much of the budget is used, what happens once it is full, and what to do — open
    /// Settings (a click on the notification does), or let an AI assistant clean up with <c>bclip</c>.
    /// </summary>
    /// <param name="warning">The warning.</param>
    /// <returns>At most <see cref="MaxNotificationText"/> characters in every shipped language (tested).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="warning"/> is <see langword="null"/>.</exception>
    public static string NotificationText(HistoryBudgetWarning warning)
    {
        ArgumentNullException.ThrowIfNull(warning);
        var used = ByteSizeText.Format(warning.UsedBytes);
        var budget = ByteSizeText.Format(warning.BudgetBytes);
        return warning.IsFull
            ? UiText.Format($"The {budget} budget is full: the oldest items are now removed to make room for new copies, so your history is no longer permanent. Click to open Settings, or let an AI assistant clean up with bclip.")
            : UiText.Format($"{used} of {budget} used. Once the budget is full, the oldest items are removed to make room, so your history stops being permanent. Click to open Settings, or let an AI assistant clean up with bclip.");
    }

    /// <summary>
    /// Settings' warning above the history limits, from 90 % of the budget on: what happens once it is full, and every way
    /// to keep the history (a larger budget, deleting, an AI assistant with <c>bclip</c>).
    /// </summary>
    /// <param name="level">The level now (<see cref="BudgetLevel"/>): 90 to 98, or 100 when full.</param>
    /// <returns>The message (two sentences joined as one line, right to left as a whole in a right-to-left language).</returns>
    public static string WarningMessage(int level)
    {
        var lead = level >= HistoryBudgetWarnings.FullLevel
            ? UiText.Translate("The size budget is full: BetterClipboard now removes the items you used least recently to make room for new copies, so your history is no longer kept for good. Pinned items and items in groups stay.")
            : UiText.Translate("Once the size budget is full, BetterClipboard removes the items you used least recently to make room for new copies, so your history is no longer kept for good. Pinned items and items in groups stay.");
        var advice = UiText.Translate("To keep more, raise the budget below, delete what you no longer need, or let an AI assistant such as Claude Code or Codex clean up with bclip: turn on Command line in Settings, then ask it to free space. bclip status shows the budget, bclip list --sort size the largest items, and bclip delete removes the ones you pick.");
        return UiText.JoinLine(" ", [lead, advice]);
    }

    /// <summary>
    /// The "Now:" line under <em>Maximum items</em>: how many items the limit counts, how full the limit is when there is
    /// one, and how many pinned or grouped items it leaves out.
    /// </summary>
    /// <param name="stats">What the limits count now.</param>
    /// <param name="maxItems">The limit (<see cref="Settings.AppSettings.MaxItems"/>); 0 = none.</param>
    /// <returns>The line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stats"/> is <see langword="null"/>.</exception>
    public static string ItemsNow(RetentionStats stats, int maxItems)
    {
        ArgumentNullException.ThrowIfNull(stats);
        long counted = stats.CountedItems;
        var pieces = new List<string>(3) { UiText.Plural(counted, $"{counted:N0} item", $"{counted:N0} items") };
        if (maxItems > 0)
        {
            var percent = PercentText(counted * 100.0 / maxItems);
            pieces.Add(UiText.Format($"{percent} % of the limit"));
        }

        if (stats.ProtectedItems > 0)
        {
            long kept = stats.ProtectedItems;
            pieces.Add(UiText.Plural(kept, $"{kept:N0} pinned or in a group, not counted", $"{kept:N0} pinned or in groups, not counted"));
        }

        return Now(pieces);
    }

    /// <summary>
    /// The "Now:" line under <em>Keep items for (days)</em>: when the counted item the age limit reaches first was last used.
    /// </summary>
    /// <param name="stats">What the limits count now.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stats"/> is <see langword="null"/>.</exception>
    public static string AgeNow(RetentionStats stats, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(stats);
        if (stats.OldestCountedUseUtc is not { } oldest)
        {
            return Now([UiText.Translate("no items yet")]);
        }

        // Whole days, rounded down: an item used 23 hours ago was used today. A clock that moved back counts as today.
        long days = Math.Max(0, (long)Math.Floor((now - oldest).TotalDays));
        return Now([days == 0
            ? UiText.Translate("the oldest item was last used today")
            : UiText.Plural(days, $"the oldest item was last used {days:N0} day ago", $"the oldest item was last used {days:N0} days ago")]);
    }

    /// <summary>The "Now:" line under <em>Largest item (MB)</em>: the largest item kept, pinned and grouped ones included.</summary>
    /// <param name="stats">What the limits count now.</param>
    /// <returns>The line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stats"/> is <see langword="null"/>.</exception>
    public static string LargestNow(RetentionStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        return stats.CountedItems + stats.ProtectedItems == 0
            ? Now([UiText.Translate("no items yet")])
            : Now([UiText.Format($"the largest item is {ByteSizeText.Format(stats.LargestItemBytes)}")]);
    }

    /// <summary>
    /// The "Now:" line under <em>History size budget (MB)</em>: the payload the budget counts against the budget (or alone,
    /// without one), the database file's size on disk, and what pinned and grouped items hold outside the budget.
    /// </summary>
    /// <param name="stats">What the limits count now.</param>
    /// <param name="maxTotalSizeMB">The budget (<see cref="Settings.AppSettings.MaxTotalSizeMB"/>); 0 = none.</param>
    /// <returns>The line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stats"/> is <see langword="null"/>.</exception>
    public static string BudgetNow(RetentionStats stats, int maxTotalSizeMB)
    {
        ArgumentNullException.ThrowIfNull(stats);
        var used = ByteSizeText.Format(stats.CountedBytes);
        var pieces = new List<string>(3);
        if (maxTotalSizeMB > 0)
        {
            var budget = ByteSizeText.Format(BudgetBytes(maxTotalSizeMB));
            var percent = PercentText(BudgetPercent(stats, maxTotalSizeMB) ?? 0);
            pieces.Add(UiText.Format($"{used} of {budget} used ({percent} %)"));
        }
        else
        {
            pieces.Add(UiText.Format($"{used} used, no budget"));
        }

        pieces.Add(UiText.Format($"the history file takes {ByteSizeText.Format(stats.DatabaseBytes)} on disk"));
        if (stats.ProtectedBytes > 0)
        {
            pieces.Add(UiText.Format($"pinned and grouped items ({ByteSizeText.Format(stats.ProtectedBytes)}) are not counted"));
        }

        return Now(pieces);
    }

    /// <summary>The "Now:" line under <em>Keep temporary pictures for (days)</em>: how many there are and their size.</summary>
    /// <param name="count">Saved pictures (<see cref="Content.TempImageFiles.Measure"/>).</param>
    /// <param name="bytes">Their size together.</param>
    /// <returns>The line.</returns>
    public static string TempPicturesNow(int count, long bytes) => count <= 0
        ? Now([UiText.Translate("no temporary pictures")])
        : Now([UiText.Plural(count, $"{count:N0} picture, {ByteSizeText.Format(bytes)}", $"{count:N0} pictures, {ByteSizeText.Format(bytes)}")]);

    /// <summary>A budget in MB as bytes.</summary>
    /// <param name="maxTotalSizeMB">The budget in MB.</param>
    /// <returns>The bytes.</returns>
    private static long BudgetBytes(int maxTotalSizeMB) => maxTotalSizeMB * 1024L * 1024L;

    /// <summary>Puts the pieces of a line behind "Now:" (one template, so a right-to-left language orders the whole line).</summary>
    /// <param name="pieces">The pieces in reading order, joined with " · ".</param>
    /// <returns>The line.</returns>
    private static string Now(IEnumerable<string> pieces) => UiText.Format($"Now: {string.Join(" · ", pieces)}");
}
