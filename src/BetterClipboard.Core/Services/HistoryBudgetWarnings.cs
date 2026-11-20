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
/// things, raised the budget, or retention by age or count made room — the mark comes down to the level the usage is at
/// (or none below 90 %), so climbing back warns again. A smaller dip does not: deleting one item near a boundary and copying
/// a new one must not repeat the warning. While the budget removes items, the usage stays just under it and the mark stays
/// at full.
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
    /// The store state that keeps the mark (<see cref="Storage.ClipStore.SetStateValue"/>): the level as digits, or empty for
    /// none. Kept with the history, not in the settings: a quarantined store starts over with its new history.
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
    /// <returns>The level to warn about (if any) and the mark to keep.</returns>
    public static BudgetWarningDecision Decide(int? mark, long usedBytes, long budgetBytes, bool removedForSize)
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

        // Fallen more than a whole step below the mark: lower it to where the usage is, so climbing back warns again. A
        // removal for the budget means it is still full, so the mark stays.
        if (mark is { } warned && !removedForSize && percent < warned - Step)
        {
            return new BudgetWarningDecision(null, level);
        }

        return new BudgetWarningDecision(null, mark);
    }

    /// <summary>Writes a mark for the store's state.</summary>
    /// <param name="mark">The mark, or <see langword="null"/> for none.</param>
    /// <returns>The level as invariant digits, or empty.</returns>
    public static string Format(int? mark) => mark?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>Reads a mark from the store's state.</summary>
    /// <param name="value">The stored value (missing, empty or anything unreadable counts as none).</param>
    /// <returns>The level, or <see langword="null"/>.</returns>
    public static int? Parse(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int level) && level is >= FirstLevel and <= FullLevel
            ? level
            : null;
}
