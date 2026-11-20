using System.Globalization;

namespace BetterClipboard.Core.Services;

/// <summary>
/// A warning that the history nears its size budget or filled it: what <see cref="ClipHistoryService.BudgetWarning"/>
/// raises, and what the app turns into a notification (<c>AppController.ShowBudgetWarning</c>).
/// </summary>
/// <param name="Level">
/// The level reached: 90, 92, 94, 96 or 98 (per cent of the budget), or <see cref="HistoryBudgetWarnings.FullLevel"/> once
/// the budget is full and the oldest items are being removed.
/// </param>
/// <param name="UsedBytes">The payload the budget counts now: every entry that is neither pinned nor in a group.</param>
/// <param name="BudgetBytes">The budget (<see cref="Settings.AppSettings.MaxTotalSizeMB"/> in bytes).</param>
public sealed record HistoryBudgetWarning(int Level, long UsedBytes, long BudgetBytes)
{
    /// <summary>Whether the budget is full: the oldest items are now removed to make room for new copies.</summary>
    public bool IsFull => Level >= HistoryBudgetWarnings.FullLevel;
}

/// <summary>What <see cref="HistoryBudgetWarnings.Decide"/> concluded.</summary>
/// <param name="Warn">The level to warn about now, or <see langword="null"/> for no warning.</param>
/// <param name="Mark">
/// The highest level warned about that still holds afterwards (kept with the history, so a restart does not warn again), or
/// <see langword="null"/> when none does.
/// </param>
public readonly record struct BudgetWarningDecision(int? Warn, int? Mark);

/// <summary>
/// The mark as the history keeps it: the highest level warned about, and the budget it was reached under (see "A new budget
/// starts over" in <see cref="HistoryBudgetWarnings"/>). Immutable, so the history service hands it to other threads whole.
/// </summary>
/// <param name="Level">The level: 90, 92, 94, 96, 98 or <see cref="HistoryBudgetWarnings.FullLevel"/>.</param>
/// <param name="BudgetBytes">
/// The budget in bytes the level was reached under; 0 when not known (a mark of the feature's first builds, stored as a
/// plain level), which counts as the budget in effect.
/// </param>
public sealed record StoredBudgetMark(int Level, long BudgetBytes);

/// <summary>
/// When the user is warned that the history nears its size budget: from 90 % of it, at every further 2 % (92 %, 94 %, 96 %,
/// 98 %), and once more when it is full and the oldest items start to go — the moment the history stops being permanent.
/// The owner's request of 2026-11-20 (CLAUDE.md §2.60). Pure: the history service applies it after every prune.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each level once.</b> The highest level warned about is the <em>mark</em>, kept in the store's state
/// (<see cref="StateName"/>), so neither a restart nor the silent restart after Settings warns about the same level again.
/// Only a higher level warns; a usage that skipped levels (a big copy, the first check after an update) warns once, about
/// the level it reached.
/// </para>
/// <para>
/// <b>A whole step down re-arms.</b> When the usage falls more than one step (2 points) below the mark — the user deleted
/// things, or retention by age or count made room — the mark comes down to the level the usage is at
/// (or none below 90 %), so climbing back warns again. A smaller dip does not: deleting one item near a boundary and copying
/// a new one must not repeat the warning.
/// </para>
/// <para>
/// <b>Full stays full until there is room.</b> Once the budget is full the prune itself removes items, and with large items
/// (a 64 MB copy in a 1 GB budget) it can leave the usage several per cent under the budget: re-arming a step below full
/// would then warn 92, 94, 96, 98 and full again at every refill (seen in the live check of 2026-11-20). So a full mark comes
/// down only once the usage is below <see cref="RearmPercent"/> (88 %) without a removal: room the user made, by deleting or
/// with a larger budget. With the defaults (16 GB, items of at most 64 MB) a removal leaves at least 99.6 %, so a full
/// budget stays at the full mark.
/// </para>
/// <para>
/// <b>A new budget starts over.</b> The mark is kept with the budget it was reached under (<see cref="StoredBudgetMark"/>).
/// When the budget is another one (the user raised or lowered it in Settings), the next prune — which a changed limit runs
/// at once — brings the mark down to the level the usage is at under the new budget, whatever the distance, so the levels
/// above warn again; a usage already above the mark warns about the level it reached, as any climb does. Without it a full
/// budget raised a little (1 GB to 1.1 GB, 91 % used) would stay at the full mark: Settings would call it full, and filling
/// it again would warn about nothing.
/// </para>
/// <para>
/// <b>Full</b> is a prune that had to remove items for the budget, or a usage at <see cref="FullPercent"/> or more (the
/// next copy will make room then; Settings shows such a usage as "100 %"). The usage counts what the budget counts: the
/// payload of every entry that is neither pinned nor grouped, not the database file.
/// </para>
/// </remarks>
public static class HistoryBudgetWarnings
{
    /// <summary>The first level warned about, in per cent of the budget.</summary>
    public const int FirstLevel = 90;

    /// <summary>The distance between levels, in percentage points: 90, 92, 94, 96, 98.</summary>
    public const int Step = 2;

    /// <summary>The level of a full budget, whose warning says the oldest items are being removed now.</summary>
    public const int FullLevel = 100;

    /// <summary>The usage, in per cent, from which the budget counts as full without a removal (it shows as "100 %").</summary>
    public const double FullPercent = 99.5;

    /// <summary>
    /// The usage, in per cent, below which a full mark comes down (one step below the first level): the prune's own removals
    /// never take a budget this low, so only room the user made re-arms the warnings of a full budget.
    /// </summary>
    public const double RearmPercent = FirstLevel - Step;

    /// <summary>
    /// The store state that keeps the mark (<see cref="Storage.ClipStore.SetStateValue"/>): the level and the budget it was
    /// reached under, as invariant digits joined by "@" ("100@17179869184"), or empty for none (<see cref="Format"/>). Kept
    /// with the history, not in the settings: a quarantined store starts over with its new history.
    /// </summary>
    public const string StateName = "history_budget.warned";

    /// <summary>How full the budget is, in per cent (may pass 100 before a prune made room).</summary>
    /// <param name="usedBytes">The payload the budget counts.</param>
    /// <param name="budgetBytes">The budget; 0 or less means none.</param>
    /// <returns>The percentage; 0 without a budget.</returns>
    public static double PercentUsed(long usedBytes, long budgetBytes) =>
        budgetBytes <= 0 ? 0 : Math.Max(0, usedBytes) * 100.0 / budgetBytes;

    /// <summary>The level a usage is at.</summary>
    /// <param name="percent">The usage in per cent (<see cref="PercentUsed"/>).</param>
    /// <param name="removedForSize">Whether the prune just removed items for the budget (the budget is full then, whatever the percentage).</param>
    /// <returns>90, 92, 94, 96, 98 or <see cref="FullLevel"/>; <see langword="null"/> below 90 %.</returns>
    public static int? LevelOf(double percent, bool removedForSize)
    {
        if (removedForSize || percent >= FullPercent)
        {
            return FullLevel;
        }

        // NaN and anything below the first level warn about nothing.
        if (!(percent >= FirstLevel))
        {
            return null;
        }

        int steps = (int)Math.Floor((percent - FirstLevel) / Step);
        return Math.Min(FirstLevel + (steps * Step), FullLevel - Step);
    }

    /// <summary>
    /// Decides whether to warn now, given the mark kept so far and what a prune just measured (see the class remarks).
    /// </summary>
    /// <param name="mark">The highest level warned about so far, or <see langword="null"/> for none.</param>
    /// <param name="usedBytes">The payload the budget counts after the prune.</param>
    /// <param name="budgetBytes">The budget in bytes; 0 or less (no budget) warns about nothing and leaves the mark as it is.</param>
    /// <param name="removedForSize">Whether the prune removed items for the budget.</param>
    /// <param name="markBudgetBytes">
    /// The budget the mark was reached under (<see cref="StoredBudgetMark.BudgetBytes"/>); 0 when not known, which counts as
    /// <paramref name="budgetBytes"/>. Another budget brings the mark down to the level the usage is at.
    /// </param>
    /// <returns>The level to warn about (if any) and the mark to keep.</returns>
    public static BudgetWarningDecision Decide(int? mark, long usedBytes, long budgetBytes, bool removedForSize, long markBudgetBytes = 0)
    {
        if (budgetBytes <= 0)
        {
            return new BudgetWarningDecision(null, mark);
        }

        double percent = PercentUsed(usedBytes, budgetBytes);
        int? level = LevelOf(percent, removedForSize);
        if (level is { } current && (mark is null || current > mark))
        {
            return new BudgetWarningDecision(current, current);
        }

        // A new budget starts over: the mark spoke of another budget, so it comes down to the level the usage is at under this
        // one (none below 90 %), whatever the distance. A level above the mark warned just above, as any climb does.
        if (mark is not null && markBudgetBytes > 0 && markBudgetBytes != budgetBytes)
        {
            return new BudgetWarningDecision(null, level);
        }

        // Fallen below the mark: lower it to where the usage is, so climbing back warns again. Below full, a whole step down
        // is enough (only the user's deletes and the age and item limits lower the usage then). A full mark needs a usage
        // under RearmPercent: the prune's own removals keep a full budget above that, and re-arming on them would warn at
        // every refill. A removal for the budget means it is still full, so the mark stays (LevelOf answers full for it, so
        // the first branch or the full mark already holds; the check says so here too, should that ever change).
        if (mark is { } warned && !removedForSize && percent < (warned >= FullLevel ? RearmPercent : warned - Step))
        {
            return new BudgetWarningDecision(null, level);
        }

        return new BudgetWarningDecision(null, mark);
    }

    /// <summary>
    /// The level Settings shows for a usage, so that it says what the notification said: the level the usage is at, or full
    /// while the mark is full and the usage has not fallen under <see cref="RearmPercent"/> (a budget of large items that
    /// the prune keeps a few per cent under the budget is still full: its oldest items go at the next copies).
    /// </summary>
    /// <param name="percent">The usage in per cent (<see cref="PercentUsed"/>).</param>
    /// <param name="mark">
    /// The mark as the history service keeps it (<see cref="ClipHistoryService.BudgetMark"/>), or <see langword="null"/>.
    /// A mark not brought down yet (the budget was raised a moment ago) shows nothing wrong: the usage then lies under the
    /// re-arm line.
    /// </param>
    /// <returns>90 to 98, <see cref="FullLevel"/>, or <see langword="null"/> below 90 %.</returns>
    public static int? ShownLevel(double percent, int? mark) =>
        mark >= FullLevel && percent >= RearmPercent ? FullLevel : LevelOf(percent, removedForSize: false);

    /// <summary>Writes a mark for the store's state (<see cref="StateName"/>).</summary>
    /// <param name="mark">The mark, or <see langword="null"/> for none.</param>
    /// <returns>"level@budget" in invariant digits ("96@17179869184"), or empty for none.</returns>
    public static string Format(StoredBudgetMark? mark) => mark is null
        ? string.Empty
        : string.Create(CultureInfo.InvariantCulture, $"{mark.Level}@{Math.Max(0, mark.BudgetBytes)}");

    /// <summary>Reads a mark from the store's state.</summary>
    /// <param name="value">
    /// The stored value: "level@budget", or a plain level (the feature's first builds), whose budget is then not known (0).
    /// Missing, empty, a level outside 90 to 100, or anything else unreadable counts as none; an unreadable budget as not
    /// known.
    /// </param>
    /// <returns>The mark, or <see langword="null"/>.</returns>
    public static StoredBudgetMark? Parse(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        int at = value.IndexOf('@', StringComparison.Ordinal);
        string levelText = at < 0 ? value : value[..at];
        if (!int.TryParse(levelText, NumberStyles.None, CultureInfo.InvariantCulture, out int level) || level is < FirstLevel or > FullLevel)
        {
            return null;
        }

        // A budget that does not read (or a plain level) is "not known": the mark then holds for whatever budget is in effect,
        // and the next prune stores it with that one.
        long budget = at >= 0 && long.TryParse(value[(at + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : 0;
        return new StoredBudgetMark(level, budget);
    }
}
