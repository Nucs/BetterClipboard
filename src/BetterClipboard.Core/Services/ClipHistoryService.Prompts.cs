using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Prompts;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Services;

/// <summary>
/// The prompt archive half of the service (CLAUDE.md §2.21): stores what the agents' files yielded through the single
/// worker, applying the capture rules, and reads the archive on the pool.
/// </summary>
public sealed partial class ClipHistoryService
{
    /// <summary>
    /// Most prompts stored in one worker turn. A first import (17,000+ Claude Code prompts on this PC) is split into
    /// slices so live clipboard copies queued meanwhile are not held up behind it; only the last slice moves the file's
    /// checkpoint (<see cref="ClipStore.IngestPrompts"/>), and the keys merge whatever a crash between slices left stored.
    /// </summary>
    public const int PromptSliceSize = 1000;

    /// <summary>
    /// Raised on the worker thread after the archive changed: prompts were added, deleted, or an agent's archive was
    /// cleared. Same handler rules as <see cref="Changed"/> (marshal to the UI and return).
    /// </summary>
    public event EventHandler<PromptsChangedEventArgs>? PromptsChanged;

    /// <summary>
    /// Stores one read of an agent file (see <see cref="PromptBatch"/>), applying the rules a copy gets: pause (except for
    /// a first import), the ignored apps (<c>claude</c>, <c>codex</c> — the agents' own executables) and the item size
    /// limit; "Forget forever" and deletions are applied by the store. The checkpoint moves either way.
    /// </summary>
    /// <param name="batch">The read.</param>
    /// <returns>What was added, merged and skipped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="batch"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The write failed (the checkpoint did not move; the read is repeated).</exception>
    public async Task<PromptIngestResult> IngestPromptsAsync(PromptBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        int added = 0, merged = 0, skipped = 0;
        int sliceCount = Math.Max(1, (batch.Prompts.Count + PromptSliceSize - 1) / PromptSliceSize);
        for (int slice = 0; slice < sliceCount; slice++)
        {
            bool last = slice == sliceCount - 1;
            var part = batch.Prompts.Count <= PromptSliceSize ? batch.Prompts : batch.Prompts.Skip(slice * PromptSliceSize).Take(PromptSliceSize).ToList();
            var result = await EnqueueAsync(() =>
            {
                // Decided per slice, on the worker: a pause switched on mid-import applies from the next slice.
                var rules = rulesProvider();
                bool skip = batch.SkipPrompts
                    || (rules.IsPaused && !batch.IsImport)
                    || rules.IsIgnored(PromptAgents.SourceOf(batch.Agent));
                var stored = store.IngestPrompts(batch with { Prompts = part, SkipPrompts = skip }, rules.MaxItemBytes, writeCheckpoint: last, time.GetUtcNow());
                if (stored.Added > 0)
                {
                    RaisePromptsChanged(new PromptsChangedEventArgs(batch.Agent, stored.Added, 0));
                }

                return Task.FromResult(stored);
            }).ConfigureAwait(false);
            added += result.Added;
            merged += result.Merged;
            skipped += result.Skipped;
        }

        return new PromptIngestResult(added, merged, skipped);
    }

    /// <summary>Lists one page of the archive on the thread pool (see <see cref="ClipStore.QueryPrompts"/>).</summary>
    /// <param name="query">Agent, search, paging, mode.</param>
    /// <param name="includeText">Also load each row's full text.</param>
    /// <param name="cancellationToken">Cancels a superseded load (also inside a toggled search's scan).</param>
    /// <returns>The rows, newest send first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is <see langword="null"/>.</exception>
    /// <exception cref="SearchPatternException">The search is a regular expression that does not parse.</exception>
    /// <exception cref="SearchTooSlowException">The regular expression ran out of time on a prompt.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The read failed.</exception>
    public Task<IReadOnlyList<PromptSummary>> QueryPromptsAsync(PromptQuery query, bool includeText = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.Run(() => store.QueryPrompts(query, includeText, cancellationToken), cancellationToken);
    }

    /// <summary>One archived send with its full text, read on the thread pool.</summary>
    /// <param name="id">The archive row id.</param>
    /// <returns>The row, or <see langword="null"/> when it does not exist (deleted meanwhile).</returns>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The read failed.</exception>
    public Task<PromptSummary?> GetPromptAsync(long id) => Task.Run(() => store.GetPrompt(id));

    /// <summary>Counts over an agent's archive (or both), read on the thread pool.</summary>
    /// <param name="agent">The agent, or <see langword="null"/> for both.</param>
    /// <returns>Sends, distinct texts, tracked files, newest send.</returns>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The read failed.</exception>
    public Task<PromptStats> GetPromptStatsAsync(PromptAgent? agent) => Task.Run(() => store.GetPromptStats(agent));

    /// <summary>An agent's tracked files and their checkpoints, read on the thread pool.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The files.</returns>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The read failed.</exception>
    public Task<IReadOnlyList<PromptFile>> GetPromptFilesAsync(PromptAgent agent) => Task.Run(() => store.GetPromptFiles(agent));

    /// <summary>
    /// "Delete" on a prompt card: every send of the text leaves the agent's archive and stays out of it until the text is
    /// sent again (<see cref="ClipStore.DeletePrompts"/>). Through the worker, so a read queued after it cannot bring it back.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="textHash">The text hash.</param>
    /// <returns>How many sends were deleted.</returns>
    /// <exception cref="ArgumentException"><paramref name="textHash"/> is null or blank.</exception>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The write failed.</exception>
    public Task<int> DeletePromptsAsync(PromptAgent agent, string textHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(textHash);
        return EnqueueAsync(() =>
        {
            int deleted = store.DeletePrompts(agent, textHash, time.GetUtcNow());
            RaisePromptsChanged(new PromptsChangedEventArgs(agent, 0, deleted));
            return Task.FromResult(deleted);
        });
    }

    /// <summary>
    /// Deletes an agent's whole archive and what was read of its files (Settings' "Delete stored prompts"): the next read
    /// is a first import again. The agent's own files are never touched.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <returns>How many prompts were deleted.</returns>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The write failed.</exception>
    public Task<int> ClearPromptsAsync(PromptAgent agent) => EnqueueAsync(() =>
    {
        int deleted = store.ClearPrompts(agent);

        // Counts only, never a prompt.
        AppLog.Info($"Deleted {deleted:N0} stored {PromptAgents.NameOf(agent)} prompt(s).");
        RaisePromptsChanged(new PromptsChangedEventArgs(agent, 0, deleted));
        return Task.FromResult(deleted);
    });

    /// <summary>Raises <see cref="PromptsChanged"/>, shielding the worker from subscriber exceptions.</summary>
    /// <param name="args">The event data.</param>
    private void RaisePromptsChanged(PromptsChangedEventArgs args)
    {
        try
        {
            PromptsChanged?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            AppLog.Error("A prompt archive subscriber threw.", ex);
        }
    }
}

/// <summary>What changed in the prompt archive (<see cref="ClipHistoryService.PromptsChanged"/>).</summary>
/// <param name="agent">The agent whose archive changed.</param>
/// <param name="added">Prompts added.</param>
/// <param name="removed">Prompts deleted.</param>
public sealed class PromptsChangedEventArgs(PromptAgent agent, int added, int removed) : EventArgs
{
    /// <summary>The agent whose archive changed.</summary>
    public PromptAgent Agent { get; } = agent;

    /// <summary>Prompts added.</summary>
    public int Added { get; } = added;

    /// <summary>Prompts deleted.</summary>
    public int Removed { get; } = removed;
}
