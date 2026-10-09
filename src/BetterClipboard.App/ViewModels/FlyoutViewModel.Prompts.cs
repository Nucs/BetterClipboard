using BetterClipboard.Core.Model;
using BetterClipboard.Core.Prompts;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// The prompt tabs' half of the flyout state (Claude, Codex; CLAUDE.md §2.21): loading a tab's rows — prompts kept from it
/// as history entries first, then the archive, one card per text, paged — and its empty state and footer.
/// </summary>
public sealed partial class FlyoutViewModel
{
    /// <summary>The prompt tabs' key hints: Enter pastes the prompt; Ctrl+P keeps it pinned; Delete removes it from the archive.</summary>
    public const string PromptKeyHint = "↵ paste · Ctrl+P pin · Del delete";

    /// <summary>Most kept prompts (history entries) a prompt tab loads above the archive.</summary>
    public const int MaxKeptPrompts = 200;

    /// <summary>Archive rows loaded so far in the current prompt tab (the next page's offset).</summary>
    private int promptOffset;

    /// <summary>The next synthetic (negative) id for a prompt card; continues across pages so ids stay unique.</summary>
    private long nextPromptCardId = -1;

    /// <summary>The kept prompts' text hashes, excluded from the archive pages so one text is one card.</summary>
    private IReadOnlyCollection<string> keptPromptHashes = [];

    /// <summary>The archive's counts at the last load of a prompt tab, for its footer and empty state.</summary>
    private PromptStats lastPromptStats = PromptStats.Empty;

    /// <summary>
    /// Whether the list is a prompt tab's view: the Claude or Codex filter in the regular view (in a group view the filter
    /// only narrows the groups' stored entries — archived prompts are in no group). Read from <see cref="GroupTitle"/>, like
    /// <see cref="IsShellView"/>.
    /// </summary>
    public bool IsPromptView => Filter is ClipFilter.ClaudeCode or ClipFilter.Codex && string.IsNullOrEmpty(GroupTitle);

    /// <summary>The agent of the current prompt tab (meaningful while <see cref="IsPromptView"/>).</summary>
    public PromptAgent CurrentAgent => Filter == ClipFilter.Codex ? PromptAgent.Codex : PromptAgent.ClaudeCode;

    /// <summary>
    /// Whether a stored card of the current prompt tab is a kept prompt, whose archived twin a Delete there must delete too
    /// (or the archive would show it again in the card's place).
    /// </summary>
    /// <param name="item">The card.</param>
    /// <returns><see langword="true"/> for a history entry kept from (or copied by) the tab's agent.</returns>
    public bool IsKeptPrompt(ClipItemViewModel item) =>
        IsPromptView && item.Prompt is null && keptPromptHashes.Contains(item.Entry.ContentHash);

    /// <summary>The search placeholder of a prompt tab.</summary>
    /// <param name="agent">The tab's agent.</param>
    /// <returns>E.g. "Search prompts you sent to Claude Code…".</returns>
    public static string PromptSearchPlaceholder(PromptAgent agent) => $"Search prompts you sent to {PromptAgents.NameOf(agent)}…";

    /// <summary>
    /// Loads a prompt tab's first page (called by <see cref="ReloadAsync"/> inside its error handling: an invalid or too slow
    /// pattern ends up in its empty state like for the history).
    /// </summary>
    /// <param name="cancellationToken">Cancels a superseded load.</param>
    /// <returns>A task completing when the list is replaced, or nothing happened (superseded).</returns>
    /// <exception cref="OperationCanceledException">Superseded by a newer load.</exception>
    /// <exception cref="SearchPatternException">The search is an invalid regular expression.</exception>
    /// <exception cref="SearchTooSlowException">The search's regular expression ran out of time on a prompt.</exception>
    private async Task ReloadPromptsAsync(CancellationToken cancellationToken)
    {
        var agent = CurrentAgent;
        var history = controller.History;
        var stored = await history.QueryAsync(new ClipQuery
        {
            SearchText = SearchText,
            SearchOptions = CurrentSearchOptions,
            Filter = Filter,
            Limit = MaxKeptPrompts,
            PinnedFirst = controller.Settings.Current.PinnedOnTop,
        }, cancellationToken);
        var kept = stored.Select(e => e.ContentHash).ToHashSet(StringComparer.Ordinal);
        var rows = await history.QueryPromptsAsync(new PromptQuery
        {
            Agent = agent,
            SearchText = SearchText,
            SearchOptions = CurrentSearchOptions,
            Limit = PageSize,
            ExcludeTextHashes = kept,
        }, includeText: false, cancellationToken);
        var stats = await history.GetPromptStatsAsync(agent);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // Built first, then swapped in card by card: a cleared list leaves a stale selection bar (ReplaceItems).
        var cards = new List<ClipItemViewModel>(stored.Count + rows.Count);
        foreach (var entry in stored)
        {
            cards.Add(CreateItem(entry));
        }

        nextPromptCardId = -1;
        foreach (var row in rows)
        {
            cards.Add(ClipItemViewModel.ForPrompt(row, nextPromptCardId--));
        }

        ReplaceItems(cards);
        keptPromptHashes = kept;
        lastPromptStats = stats;
        promptOffset = rows.Count;
        loaded = Items.Count;
        hasMore = rows.Count == PageSize;
        HasPatternError = false;
        UpdateEmptyState();
        Reloaded?.Invoke(this, EventArgs.Empty);
        await UpdateStatusAsync();
    }

    /// <summary>Appends the next page of a prompt tab's archive rows (scrolled near the end).</summary>
    /// <param name="cancellationToken">The current load's token (a newer load supersedes it).</param>
    /// <returns>A task completing when appended.</returns>
    /// <exception cref="OperationCanceledException">Superseded by a newer load.</exception>
    /// <exception cref="SearchTooSlowException">The search's regular expression ran out of time on a prompt.</exception>
    private async Task LoadMorePromptsAsync(CancellationToken cancellationToken)
    {
        var rows = await controller.History.QueryPromptsAsync(new PromptQuery
        {
            Agent = CurrentAgent,
            SearchText = SearchText,
            SearchOptions = CurrentSearchOptions,
            Offset = promptOffset,
            Limit = PageSize,
            ExcludeTextHashes = keptPromptHashes,
        }, includeText: false, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // A prompt sent while scrolling shifts the offsets by one: skip a text already shown.
        var shown = Items.Where(i => i.Prompt is not null).Select(i => i.Prompt!.TextHash).ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => shown.Add(r.TextHash)))
        {
            Items.Add(ClipItemViewModel.ForPrompt(row, nextPromptCardId--));
        }

        promptOffset += rows.Count;
        loaded = Items.Count;
        hasMore = rows.Count == PageSize;
    }

    /// <summary>The empty prompt tab's wording: no matches, a first import still running, or how the tab fills.</summary>
    private void UpdatePromptEmptyState()
    {
        var agent = CurrentAgent;
        var name = PromptAgents.NameOf(agent);
        var reader = controller.PromptsReader(agent);
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            EmptyTitle = "No matches";
            EmptyMessage = $"No prompt you sent to {name} {DescribeSearch()}.";
        }
        else if (reader is { IsImporting: true })
        {
            var (done, total) = reader.ImportProgress;
            EmptyTitle = $"Reading {name}'s prompts…";
            EmptyMessage = total > 0
                ? $"First import: {done:N0} of {total:N0} files read. Prompts appear here as they are stored."
                : "First import: prompts appear here as they are stored.";
        }
        else if (agent == PromptAgent.ClaudeCode)
        {
            EmptyTitle = "No Claude Code prompts yet";
            EmptyMessage = "Every prompt you type in Claude Code shows up here the moment Claude Code saves it — pastes included — with "
                + "every older one Claude Code still has. BetterClipboard keeps them after Claude Code prunes its history.";
        }
        else
        {
            EmptyTitle = "No Codex prompts yet";
            EmptyMessage = "Every prompt you send in Codex (the app, the CLI, the IDE extension) shows up here, with every older one in "
                + "Codex's session files. Prompts written by agents and codex exec runs are left out.";
        }
    }

    /// <summary>The prompt tab's footer: how many prompts are archived, how often they were sent, how many are kept.</summary>
    /// <returns>E.g. "14,156 prompts (17,169 sent) · 3 kept", "60+ matching of 14,156 prompts", or the import's progress.</returns>
    private string PromptStatusText()
    {
        var reader = controller.PromptsReader(CurrentAgent);
        if (reader is { IsImporting: true } && reader.ImportProgress is { Total: > 0 } progress)
        {
            return $"Importing… {progress.Done:N0} of {progress.Total:N0} files · {lastPromptStats.Distinct:N0} prompts so far";
        }

        int keptShown = Items.Count(i => i.Prompt is null);
        var kept = keptShown > 0 ? $" · {keptShown:N0} kept" : string.Empty;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            int matching = Items.Count(i => i.Prompt is not null);
            return $"{matching:N0}{(hasMore ? "+" : string.Empty)} matching of {lastPromptStats.Distinct:N0} prompts{kept}";
        }

        var stats = lastPromptStats;
        return stats.Sends == stats.Distinct
            ? $"{stats.Distinct:N0} prompt{(stats.Distinct == 1 ? string.Empty : "s")}{kept}"
            : $"{stats.Distinct:N0} prompts ({stats.Sends:N0} sent){kept}";
    }
}
