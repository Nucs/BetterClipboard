using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Everything;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Prompts;
using BetterClipboard.Core.Shells;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Services;

/// <summary>
/// <em>Delete stored…</em> (CLAUDE.md §2.43): what each integration stored — counted, shown with examples, and deleted
/// through the worker. The source apps' own files are never touched (the owner's rule: "we leave the source alone,
/// always"); only BetterClipboard's copies go.
/// </summary>
public sealed partial class ClipHistoryService
{
    /// <summary>How many examples a part shows at most (a row of thumbnails fits four or five).</summary>
    public const int StoredExampleCount = 4;

    /// <summary>The longest example line kept; the dialog trims to its width anyway, this only bounds what is held.</summary>
    private const int MaxExampleLength = 160;

    /// <summary>
    /// What a source stores now, part by part, with the newest examples of each — read on the thread pool (it does not
    /// wait behind queued writes). Parts that hold nothing are left out.
    /// </summary>
    /// <param name="source">The integration.</param>
    /// <param name="cancellationToken">Cancels a preview nobody waits for any more (the dialog closed); checked between parts.</param>
    /// <returns>The preview.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">A read failed.</exception>
    public Task<StoredPreview> PreviewStoredAsync(StoredSource source, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var parts = StoredData.PartsOf(source);
        var entryParts = parts.Where(p => p.Kind == StoredPartKind.Entries).ToList();
        var stats = store.SummarizeStored(entryParts.Select(p => p.Selections).ToList());
        var previews = new List<StoredPartPreview>();
        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preview = part.Kind switch
            {
                StoredPartKind.Entries => PreviewEntries(part, stats[entryParts.IndexOf(part)]),
                StoredPartKind.PromptArchive => PreviewArchive(part, cancellationToken),
                StoredPartKind.KeptCommands => PreviewKeptCommands(part),
                _ => PreviewHiddenList(part),
            };
            if (preview.Count > 0)
            {
                previews.Add(preview);
            }
        }

        return new StoredPreview(source, previews);
    }, cancellationToken);

    /// <summary>
    /// How many items each source stores, all parts together (pinned and grouped entries included): what enables each
    /// card's <em>Delete stored…</em>. One pass over the history for every source, plus the archives' and lists' counts.
    /// Read on the thread pool.
    /// </summary>
    /// <returns>Every source with its count (0 for nothing).</returns>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">A read failed.</exception>
    public Task<IReadOnlyDictionary<StoredSource, long>> CountStoredAsync() => Task.Run<IReadOnlyDictionary<StoredSource, long>>(() =>
    {
        var unions = new List<IReadOnlyList<StoredSelection>>();
        var owners = new List<StoredSource>();
        var counts = StoredData.Sources.ToDictionary(source => source, _ => 0L);
        foreach (var source in StoredData.Sources)
        {
            foreach (var part in StoredData.PartsOf(source))
            {
                switch (part.Kind)
                {
                    case StoredPartKind.Entries:
                        unions.Add(part.Selections);
                        owners.Add(source);
                        break;
                    case StoredPartKind.PromptArchive:
                        counts[source] += store.GetPromptStats(part.Agent).Distinct;
                        break;
                    default:
                        counts[source] += CountList(part);
                        break;
                }
            }
        }

        var stats = store.SummarizeStored(unions);
        for (int i = 0; i < stats.Count; i++)
        {
            counts[owners[i]] += stats[i].Count;
        }

        return counts;
    });

    /// <summary>
    /// Deletes stored parts, all in one slot of the worker so nothing queued meanwhile can interleave: the entries of the
    /// entry parts (tombstoned, see <see cref="ClipStore.DeleteStored"/>), the prompt archives (as Settings' old "Delete
    /// stored prompts" did: the next read of a reader that is on imports what the agent still has), and the kept and
    /// hidden lists (emptied).
    /// </summary>
    /// <remarks>
    /// The caller stops what would write the same data meanwhile: a prompt reader before its archive goes (and starts it
    /// again afterwards), and the Cmd tab's reader, which keeps its list in memory too (it forgets it after this). Raises
    /// one <see cref="ClipChangeKind.Reset"/> when entries went, and <see cref="PromptsChanged"/> per cleared archive. The
    /// log line counts what went, never what it held.
    /// </remarks>
    /// <param name="parts">The parts the user ticked (any source; duplicates are deleted once).</param>
    /// <returns>What was deleted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parts"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">A write failed (what was written before it stays done).</exception>
    public Task<StoredDeletion> DeleteStoredAsync(IReadOnlyList<StoredPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var chosen = parts.Distinct().ToList();
        return EnqueueAsync(() =>
        {
            var selections = chosen.Where(p => p.Kind == StoredPartKind.Entries).SelectMany(p => p.Selections).Distinct().ToList();
            int entries = store.DeleteStored(selections, time.GetUtcNow());
            int prompts = 0;
            foreach (var agent in chosen.Where(p => p.Kind == StoredPartKind.PromptArchive).Select(p => p.Agent).OfType<PromptAgent>().Distinct())
            {
                int cleared = store.ClearPrompts(agent);
                prompts += cleared;
                RaisePromptsChanged(new PromptsChangedEventArgs(agent, 0, cleared));
            }

            int lists = 0;
            foreach (var name in chosen.Where(p => p.Kind is StoredPartKind.KeptCommands or StoredPartKind.HiddenList).Select(p => p.StateName).OfType<string>().Distinct())
            {
                if (!string.IsNullOrEmpty(store.GetStateValue(name)))
                {
                    store.SetStateValue(name, string.Empty);
                    lists++;
                }
            }

            if (entries > 0)
            {
                Raise(new ClipChangedEventArgs(ClipChangeKind.Reset));
            }

            // Counts only, never what the entries, prompts or lists held.
            AppLog.Info($"Deleted stored data: {entries:N0} history entr{(entries == 1 ? "y" : "ies")}, {prompts:N0} archived prompt(s), {lists} list(s) emptied.");
            return Task.FromResult(new StoredDeletion(entries, prompts, lists));
        });
    }

    /// <summary>An entry part's preview: its counts and its newest entries.</summary>
    /// <param name="part">The part.</param>
    /// <param name="stats">Its counts from the one pass.</param>
    /// <returns>The preview.</returns>
    private StoredPartPreview PreviewEntries(StoredPart part, StoredStats stats)
    {
        var examples = stats.Count == 0 ? [] : store.GetStoredExamples(part.Selections, StoredExampleCount).Select(ExampleOf).ToList();
        return new StoredPartPreview(part, stats.Count, stats.Bytes, stats.Newest, stats.Oldest, examples);
    }

    /// <summary>An archive's preview: its prompts (distinct texts) and sends, and its newest prompts.</summary>
    /// <param name="part">The archive part.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The preview.</returns>
    private StoredPartPreview PreviewArchive(StoredPart part, CancellationToken cancellationToken)
    {
        var stats = store.GetPromptStats(part.Agent);
        var examples = stats.Distinct == 0
            ? []
            : store.QueryPrompts(new PromptQuery { Agent = part.Agent, Limit = StoredExampleCount }, includeText: false, cancellationToken)
                .Select(prompt => new StoredExample(FirstLine(prompt.Preview), null, IsCode: false))
                .ToList();
        return new StoredPartPreview(part, stats.Distinct, 0, stats.NewestSentUtc, null, examples, stats.Sends);
    }

    /// <summary>The Cmd tab's kept list: how many commands, the newest ones.</summary>
    /// <param name="part">The kept-commands part.</param>
    /// <returns>The preview.</returns>
    private StoredPartPreview PreviewKeptCommands(StoredPart part)
    {
        var kept = CmdHistory.ParseKept(store.GetStateValue(part.StateName!));
        var examples = kept.Take(StoredExampleCount).Select(command => new StoredExample(FirstLine(command.Text), null, IsCode: true)).ToList();
        return new StoredPartPreview(
            part,
            kept.Count,
            0,
            kept.Count > 0 ? kept.Max(c => c.LastSeen) : null,
            kept.Count > 0 ? kept.Min(c => c.LastSeen) : null,
            examples);
    }

    /// <summary>
    /// A hidden list: the Everything tab's hidden picks (their paths are the examples), or a shell tab's hidden commands
    /// (only fingerprints are kept, so there is nothing to show).
    /// </summary>
    /// <param name="part">The hidden-list part.</param>
    /// <returns>The preview.</returns>
    private StoredPartPreview PreviewHiddenList(StoredPart part)
    {
        var value = store.GetStateValue(part.StateName!);
        if (part.StateName == EverythingTab.HiddenStateName)
        {
            var hidden = EverythingTab.ParseHidden(value);
            var newestFirst = hidden.OrderByDescending(pair => pair.Value).ToList();
            return new StoredPartPreview(
                part,
                hidden.Count,
                0,
                newestFirst.Count > 0 ? newestFirst[0].Value : null,
                newestFirst.Count > 0 ? newestFirst[^1].Value : null,
                newestFirst.Take(StoredExampleCount).Select(pair => new StoredExample(pair.Key, null, IsCode: true)).ToList());
        }

        return new StoredPartPreview(part, ShellTab.ParseHidden(value).Count, 0, null, null, []);
    }

    /// <summary>How many items a list part holds (kept commands, hidden picks or commands).</summary>
    /// <param name="part">A <see cref="StoredPartKind.KeptCommands"/> or <see cref="StoredPartKind.HiddenList"/> part.</param>
    /// <returns>The count.</returns>
    private long CountList(StoredPart part)
    {
        var value = store.GetStateValue(part.StateName!);
        return part.Kind == StoredPartKind.KeptCommands ? CmdHistory.ParseKept(value).Count
            : part.StateName == EverythingTab.HiddenStateName ? EverythingTab.ParseHidden(value).Count
            : ShellTab.ParseHidden(value).Count;
    }

    /// <summary>
    /// How an entry shows as an example: its thumbnail when it has one, else the first line of its preview — monospace for
    /// commands and paths, as their cards show them.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The example.</returns>
    private static StoredExample ExampleOf(ClipEntry entry)
    {
        if (entry.HasThumbnail)
        {
            return new StoredExample(null, entry.Id, IsCode: false);
        }

        bool code = entry.Kind == ClipKind.Files || entry.PathCount > 0 || entry.LastRunUtc is not null
                    || entry.Origin is ClipOrigin.PowerShell or ClipOrigin.Cmd
                    || entry.SourceAppName is ShellSources.PowerShellName or ShellSources.CmdName;
        return new StoredExample(FirstLine(entry.Preview), null, code);
    }

    /// <summary>The first non-blank line of a text, trimmed and cut to <see cref="MaxExampleLength"/> characters.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The line (empty for a blank text).</returns>
    internal static string FirstLine(string? text)
    {
        var line = (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        return line.Length <= MaxExampleLength ? line : line[..(MaxExampleLength - 1)] + "…";
    }
}
