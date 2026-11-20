using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Services;

/// <summary>
/// The size budget's watch and the numbers behind Settings › History's "Now:" lines (CLAUDE.md §2.60): every prune measures
/// what the budget counts, and the user is warned from 90 % in 2 % steps and once the budget is full
/// (<see cref="HistoryBudgetWarnings"/>). Also the several-at-once delete <c>bclip delete</c> uses to free space.
/// </summary>
public sealed partial class ClipHistoryService
{
    /// <summary>
    /// The highest budget level warned about (<see cref="HistoryBudgetWarnings"/>), as kept in the store's state; read once,
    /// on the first prune that measures. Worker thread only, like every prune.
    /// </summary>
    private int? budgetMark;

    /// <summary>Whether <see cref="budgetMark"/> was read from the store yet. Worker thread only.</summary>
    private bool budgetMarkRead;

    /// <summary>
    /// Raised on the worker thread when the history reaches a new level of its size budget: 90, 92, 94, 96 or 98 %, or full
    /// (the oldest items are now removed to make room). Each level once (the mark is kept with the history), and again only
    /// after the usage fell a whole step below it. Same handler rules as <see cref="Changed"/>: marshal to the UI and return.
    /// </summary>
    public event EventHandler<HistoryBudgetWarning>? BudgetWarning;

    /// <summary>
    /// Reads what each retention limit has to work with now (<see cref="ClipStore.GetRetentionStats"/>) on the thread pool,
    /// without waiting behind queued writes.
    /// </summary>
    /// <returns>The numbers.</returns>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The read failed.</exception>
    public Task<RetentionStats> GetRetentionStatsAsync() => Task.Run(store.GetRetentionStats);

    /// <summary>
    /// Deletes several entries through the worker, in one transaction (<see cref="ClipStore.DeleteMany"/>): each is
    /// tombstoned against re-import, and the file gives the freed space back at once. <c>bclip delete</c> with several ids.
    /// </summary>
    /// <param name="ids">Entry ids; unknown ids and repeats are skipped.</param>
    /// <returns>The ids that were deleted, in the order given.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ids"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The write failed (nothing was deleted).</exception>
    public Task<IReadOnlyList<long>> DeleteManyAsync(IReadOnlyList<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        // Copied on the caller's thread: the worker runs later, and the caller's list may change meanwhile.
        var copy = ids.ToArray();
        return EnqueueAsync(() =>
        {
            var deleted = store.DeleteMany(copy, time.GetUtcNow());
            foreach (long id in deleted)
            {
                Raise(new ClipChangedEventArgs(ClipChangeKind.Removed, entryId: id));
            }

            if (deleted.Count > 0)
            {
                // Content-free: how many went, never what they held.
                AppLog.Info($"Deleted {deleted.Count:N0} history entr{(deleted.Count == 1 ? "y" : "ies")} at once (command line).");
            }

            return Task.FromResult(deleted);
        });
    }

    /// <summary>
    /// Applies retention and watches the size budget: the one way every write path prunes, so the user is warned whichever
    /// way the history grew (a copy, an import, a Notepad++ tab, a changed limit). Worker thread only.
    /// </summary>
    /// <param name="retention">The limits to apply.</param>
    /// <returns>Entries removed.</returns>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The prune failed.</exception>
    private int ApplyRetention(RetentionPolicy retention)
    {
        var result = store.PruneAndMeasure(retention, time.GetUtcNow());
        WatchBudget(result, retention.MaxTotalBytes);
        return result.Removed;
    }

    /// <summary>
    /// Decides, after a prune, whether the user is to be warned about the size budget, keeps the mark with the history, and
    /// raises <see cref="BudgetWarning"/>. Never throws: a failed state read or write is logged, and the next prune tries again.
    /// </summary>
    /// <param name="result">What the prune measured.</param>
    /// <param name="budgetBytes">The budget it applied; 0 for none (nothing is measured then).</param>
    private void WatchBudget(PruneResult result, long budgetBytes)
    {
        if (result.CountedBytes is not { } used || budgetBytes <= 0)
        {
            return;
        }

        try
        {
            if (!budgetMarkRead)
            {
                budgetMark = HistoryBudgetWarnings.Parse(store.GetStateValue(HistoryBudgetWarnings.StateName));
                budgetMarkRead = true;
            }

            var decision = HistoryBudgetWarnings.Decide(budgetMark, used, budgetBytes, result.RemovedForSize > 0);
            if (decision.Mark != budgetMark)
            {
                // Kept before the warning goes out: a warning whose mark could not be stored would come again at every copy.
                store.SetStateValue(HistoryBudgetWarnings.StateName, HistoryBudgetWarnings.Format(decision.Mark));
                budgetMark = decision.Mark;
            }

            if (decision.Warn is { } level)
            {
                // Content-free: sizes only.
                double percent = HistoryBudgetWarnings.PercentUsed(used, budgetBytes);
                AppLog.Info(level >= HistoryBudgetWarnings.FullLevel
                    ? $"History size budget full ({used:N0} of {budgetBytes:N0} bytes, {percent:0.#} %): the oldest items are removed to make room; the user is told."
                    : $"History size budget at {percent:0.#} % ({used:N0} of {budgetBytes:N0} bytes): the user is told (the {level} % level).");
                RaiseBudgetWarning(new HistoryBudgetWarning(level, used, budgetBytes));
            }
        }
        catch (Exception ex)
        {
            // Nothing here may fail the copy whose prune measured: the warning is a courtesy, the copy is stored already.
            AppLog.Warn($"Checking the history size budget failed: {ex.Message}");
        }
    }

    /// <summary>Raises <see cref="BudgetWarning"/>, shielding the worker from subscriber exceptions.</summary>
    /// <param name="warning">The warning.</param>
    private void RaiseBudgetWarning(HistoryBudgetWarning warning)
    {
        try
        {
            BudgetWarning?.Invoke(this, warning);
        }
        catch (Exception ex)
        {
            AppLog.Error("A budget warning subscriber threw.", ex);
        }
    }
}
