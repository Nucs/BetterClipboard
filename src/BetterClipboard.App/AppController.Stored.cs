using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Prompts;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.App;

/// <summary>
/// Settings' <em>Delete stored…</em> on every integration card (CLAUDE.md §2.43): what each integration stored, shown and
/// deleted. The history service counts and deletes; this half makes sure nothing writes the same data back meanwhile — a
/// prompt reader is stopped while its archive goes, and the Cmd tab's reader forgets the list it holds in memory.
/// </summary>
/// <remarks>
/// The source apps' own files are never touched (the owner's rule, 2026-11-09: "we leave the source alone, always"):
/// ShareX's screenshots stay in ShareX's folders, PowerShell's history file and Claude Code's files stay as they are. Only
/// BetterClipboard's copies go.
/// </remarks>
public sealed partial class AppController
{
    /// <summary>What a source stores now, part by part, with examples (read on the pool).</summary>
    /// <param name="source">The integration.</param>
    /// <param name="cancellationToken">Cancels a preview of a dialog that closed.</param>
    /// <returns>The preview.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">A read failed.</exception>
    public Task<StoredPreview> PreviewStoredAsync(StoredSource source, CancellationToken cancellationToken) =>
        History.PreviewStoredAsync(source, cancellationToken);

    /// <summary>How many items each source stores (enables each card's button; read on the pool).</summary>
    /// <returns>Every source with its count.</returns>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">A read failed.</exception>
    public Task<IReadOnlyDictionary<StoredSource, long>> CountStoredAsync() => History.CountStoredAsync();

    /// <summary>
    /// Deletes the parts of a source the user ticked: stops the prompt reader of an archive that goes (a pass in flight
    /// would store prompts and checkpoints again right after), deletes through the history service, starts the reader again
    /// when its switch is on (it then reads what the agent still has, as a first import), and makes the Cmd tab's reader
    /// forget its kept list.
    /// </summary>
    /// <param name="source">The integration (for the log line).</param>
    /// <param name="parts">The ticked parts.</param>
    /// <returns>What was deleted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parts"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The history service is shutting down.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">A write failed.</exception>
    public async Task<StoredDeletion> DeleteStoredAsync(StoredSource source, IReadOnlyList<StoredPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var agents = parts.Where(p => p.Kind == StoredPartKind.PromptArchive).Select(p => p.Agent).OfType<PromptAgent>().Distinct().ToList();
        foreach (var agent in agents)
        {
            if (PromptsReader(agent) is { IsRunning: true } reader)
            {
                await reader.SetEnabledAsync(false);
            }
        }

        StoredDeletion result;
        try
        {
            result = await History.DeleteStoredAsync(parts);
        }
        finally
        {
            // Started again whatever happened: a failed delete must not leave a switched-on archive unread. Its tracked
            // files are gone with the archive, so the reader starts over with a first import.
            foreach (var agent in agents)
            {
                if (PromptsReader(agent) is { } reader)
                {
                    await reader.SetEnabledAsync(IsPromptSettingOn(agent));
                }
            }

            if (agents.Count > 0)
            {
                RefreshPromptAvailability();
            }
        }

        if (parts.Any(p => p.Kind == StoredPartKind.KeptCommands) && cmdHistory is { } cmd)
        {
            // After the store's list is empty: a read that ran meanwhile wrote the old list from memory, and this forgets it.
            await cmd.ForgetKeptAsync();
        }

        // Which card, never what was deleted.
        AppLog.Info($"Settings › {source}: Delete stored removed {result.Entries:N0} entr{(result.Entries == 1 ? "y" : "ies")}, {result.Prompts:N0} archived prompt(s), {result.Lists} list(s).");
        return result;
    }
}
