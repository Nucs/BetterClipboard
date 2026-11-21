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
/// <b>Notification bounds.</b> Windows takes at most <see cref="MaxNotificationTitle"/> characters of a tray notification's
/// title and <see cref="MaxNotificationText"/> of its text (<c>NOTIFYICONDATAW</c>), and a Windows 11 toast shows far less:
/// two lines of title (about 37 characters each) and four lines of text (about 41), measured in a test desktop on
/// 2026-11-20, where the first wording ("… Click to open Settings, or let") lost its bclip sentence. So the notification
/// carries the level and what happens in its title, and its text only says what it means and what to do; every language's
/// wording is held within slightly less than that room by a test that wraps it (wide characters count two).
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
    /// The warning level Settings shows (<see cref="HistoryBudgetWarnings.ShownLevel"/>): 90 to 98, 100 when full (also a
    /// budget the prune keeps a few per cent under full, while the history service's mark says full), or
    /// <see langword="null"/> below 90 % or without a budget. Settings shows its warning from the first level on.
    /// </summary>
    /// <param name="stats">What the limits count now.</param>
    /// <param name="maxTotalSizeMB">The budget; 0 = none.</param>
    /// <param name="mark">
    /// The history service's mark (<see cref="ClipHistoryService.BudgetMark"/>), or <see langword="null"/> for none. A mark
    /// reached under another budget is left out: the budget was changed a moment ago, and the next prune (which a changed
    /// limit runs at once) starts the warnings over.
    /// </param>
    /// <returns>The level, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stats"/> is <see langword="null"/>.</exception>
    public static int? BudgetLevel(RetentionStats stats, int maxTotalSizeMB, StoredBudgetMark? mark = null)
    {
        if (BudgetPercent(stats, maxTotalSizeMB) is not { } percent)
        {
            return null;
        }

        // A mark whose budget is not known (0) counts as this budget's, as the history service treats it.
        int? standing = mark is { } held && (held.BudgetBytes == 0 || held.BudgetBytes == BudgetBytes(maxTotalSizeMB)) ? held.Level : null;
        return HistoryBudgetWarnings.ShownLevel(percent, standing);
    }

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

    /// <summary>The title of Settings' warning under the budget: the level reached, or that the budget is full.</summary>
    /// <param name="level">The level (<see cref="HistoryBudgetWarning.Level"/>): 90 to 98, or 100 when full.</param>
    /// <returns>The title.</returns>
    public static string WarningTitle(int level) => level >= HistoryBudgetWarnings.FullLevel
        ? UiText.Translate("Clipboard history is full")
        : UiText.Format($"Clipboard history at {level} % of its size budget");

    /// <summary>
    /// The notification's title: how full the history is and what follows, the oldest items deleted. The title carries
    /// both, because a toast shows its title whole and only four short lines of its text.
    /// </summary>
    /// <param name="level">The level (<see cref="HistoryBudgetWarning.Level"/>): 90 to 98, or 100 when full.</param>
    /// <returns>At most <see cref="MaxNotificationTitle"/> characters, two toast lines, in every shipped language (tested).</returns>
    public static string NotificationTitle(int level) => level >= HistoryBudgetWarnings.FullLevel
        ? UiText.Translate("Clipboard history is full: the oldest items are being deleted")
        : UiText.Format($"Clipboard history {level} % full: the oldest items will be deleted");

    /// <summary>
    /// The notification's text: that the history stops (or stopped) being permanent, and what to do — open Settings (a
    /// click on the notification does), or let an AI assistant clean up with <c>bclip</c>. A full budget's names its size.
    /// </summary>
    /// <param name="warning">The warning.</param>
    /// <returns>Four toast lines at most in every shipped language (tested), well under <see cref="MaxNotificationText"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="warning"/> is <see langword="null"/>.</exception>
    public static string NotificationText(HistoryBudgetWarning warning)
    {
        ArgumentNullException.ThrowIfNull(warning);
        var budget = ByteSizeText.Format(warning.BudgetBytes);
        return warning.IsFull
            ? UiText.Format($"The {budget} budget is full, so your history is no longer permanent. Click for Settings, or let an AI assistant clean up with bclip.")
            : UiText.Translate("When it fills up, your history stops being permanent. Click for Settings, or let an AI assistant clean up with bclip.");
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
