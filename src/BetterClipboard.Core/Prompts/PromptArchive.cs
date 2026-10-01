using System.Globalization;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Prompts;

/// <summary>
/// One read of one agent file, ready to be stored: the prompts it yielded and the checkpoint that says how far it was
/// read. The store keeps both in one transaction, so a checkpoint never moves past prompts that were not stored.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pause.</b> A batch read while capture is paused is stored with <see cref="SkipPrompts"/>: its checkpoint moves, its
/// prompts are dropped — they are never recorded (the Win+R rule). The first import of an agent's files
/// (<see cref="IsImport"/>) is exempt: it brings in what the agent already kept, like Windows' own clipboard history.
/// </para>
/// <para>
/// <b>Rewrites.</b> When the file was read again from its start (<see cref="TailReadResult.IsReset"/>),
/// <see cref="RewrittenAfterUtc"/> carries the old high-water mark: prompts at or before it minus
/// <see cref="PromptRules.RewriteMargin"/> are not new and are left alone, even when they are not in the archive (deleted
/// by the user, or skipped while paused).
/// </para>
/// </remarks>
public sealed record PromptBatch
{
    /// <summary>The agent the file belongs to.</summary>
    public required PromptAgent Agent { get; init; }

    /// <summary>The kind of file.</summary>
    public required PromptSourceKind Kind { get; init; }

    /// <summary>
    /// The file's tracking key (unique per agent): <c>history.jsonl</c> for a history, the plain session file name
    /// (<see cref="CodexSessions.CanonicalName"/>) for a Codex session file.
    /// </summary>
    public required string FileKey { get; init; }

    /// <summary>Where the file was last seen (Settings and diagnostics; the key is what identifies it).</summary>
    public required string Path { get; init; }

    /// <summary>The checkpoint after this read.</summary>
    public required TailCheckpoint Checkpoint { get; init; }

    /// <summary>The prompts read, oldest first.</summary>
    public IReadOnlyList<AgentPrompt> Prompts { get; init; } = [];

    /// <summary>Whether this is the agent's first import (pause does not apply; see the remarks).</summary>
    public bool IsImport { get; init; }

    /// <summary>Drop the prompts and only move the checkpoint (capture is paused, or the thread is not the user's).</summary>
    public bool SkipPrompts { get; init; }

    /// <summary>The previous high-water mark when the file was read again from its start; <see langword="null"/> otherwise.</summary>
    public DateTimeOffset? RewrittenAfterUtc { get; init; }

    /// <summary>
    /// The Codex thread of a session file (its id, folder and whether it is the user's), kept with the checkpoint so a
    /// later read of an appended file needs not parse the first line again; <see langword="null"/> for other files.
    /// </summary>
    public CodexThread? Thread { get; init; }
}

/// <summary>What storing one <see cref="PromptBatch"/> did.</summary>
/// <param name="Added">New prompts in the archive.</param>
/// <param name="Merged">Prompts already archived from the other source of the same agent (a Codex prompt read from both its
/// history line and its session file) — or from the same file before (an earlier, older copy keeps its time).</param>
/// <param name="Skipped">Prompts not stored: paused, deleted before, forgotten forever, an ignored agent, over the size
/// limit, or older than a rewrite's high-water mark.</param>
public sealed record PromptIngestResult(int Added, int Merged, int Skipped)
{
    /// <summary>Nothing was read.</summary>
    public static readonly PromptIngestResult None = new(0, 0, 0);
}

/// <summary>A tracked agent file: its checkpoint and what it is.</summary>
/// <param name="Agent">The agent.</param>
/// <param name="Kind">The kind of file.</param>
/// <param name="FileKey">The tracking key.</param>
/// <param name="Path">Where it was last seen.</param>
/// <param name="Checkpoint">How far it was read.</param>
/// <param name="Thread">The Codex thread of a session file, or <see langword="null"/>.</param>
public sealed record PromptFile(PromptAgent Agent, PromptSourceKind Kind, string FileKey, string Path, TailCheckpoint Checkpoint, CodexThread? Thread);

/// <summary>
/// A page request against the prompt archive: one agent's prompts, newest first — one row per distinct text (how the
/// panel's prompt tabs list them) or one per send (the command line's <c>--all</c>).
/// </summary>
public sealed record PromptQuery
{
    /// <summary>The agent whose prompts to list; <see langword="null"/> for both.</summary>
    public PromptAgent? Agent { get; init; }

    /// <summary>Search text, matched like the panel's search (<see cref="ClipQuery.SearchText"/>).</summary>
    public string? SearchText { get; init; }

    /// <summary>The search box's toggles (<see cref="ClipQuery.SearchOptions"/>).</summary>
    public SearchOptions SearchOptions { get; init; }

    /// <summary>One row per distinct text (newest send, with how often it was sent) instead of one per send.</summary>
    public bool Distinct { get; init; } = true;

    /// <summary>Rows to skip (paging).</summary>
    public int Offset { get; init; }

    /// <summary>Maximum rows; clamped to 1–1000 by the store.</summary>
    public int Limit { get; init; } = 60;

    /// <summary>Only prompts sent at or after this instant; <see langword="null"/> = no limit.</summary>
    public DateTimeOffset? SentSince { get; init; }

    /// <summary>
    /// Text hashes to leave out — the prompts the tab already shows as kept history entries, so one text is one card.
    /// At most 500 are used.
    /// </summary>
    public IReadOnlyCollection<string>? ExcludeTextHashes { get; init; }
}

/// <summary>
/// One row of the prompt archive as the panel and the command line see it: a prompt (its newest send in distinct mode),
/// with how often and when it was sent.
/// </summary>
public sealed record PromptSummary
{
    /// <summary>The archive row id of the newest send (what <c>bclip prompt ID</c> prints).</summary>
    public required long Id { get; init; }

    /// <summary>The agent.</summary>
    public required PromptAgent Agent { get; init; }

    /// <summary>The text hash (<see cref="AgentPrompt.TextHash"/>): the card's identity, and a kept copy's content hash.</summary>
    public required string TextHash { get; init; }

    /// <summary>Short display text (first lines; never the full prompt).</summary>
    public required string Preview { get; init; }

    /// <summary>The working folder of the newest send, or <see langword="null"/>.</summary>
    public string? Project { get; init; }

    /// <summary>The session or thread of the newest send, or <see langword="null"/>.</summary>
    public string? SessionId { get; init; }

    /// <summary>When it was last sent.</summary>
    public required DateTimeOffset LastSentUtc { get; init; }

    /// <summary>When it was first sent.</summary>
    public required DateTimeOffset FirstSentUtc { get; init; }

    /// <summary>How often it was sent (1 per send in non-distinct mode).</summary>
    public required int Sends { get; init; }

    /// <summary>Images attached to the newest send.</summary>
    public int ImageCount { get; init; }

    /// <summary>Whether it is a slash command.</summary>
    public bool IsCommand { get; init; }

    /// <summary>The length of the full text in characters.</summary>
    public int TextLength { get; init; }

    /// <summary>The full text, when the query asked for it (the command line's <c>--full</c>); <see langword="null"/> otherwise.</summary>
    public string? Text { get; init; }
}

/// <summary>Counts over the prompt archive.</summary>
/// <param name="Sends">Archived prompts (one per send).</param>
/// <param name="Distinct">Distinct texts among them.</param>
/// <param name="Files">Agent files being tracked.</param>
/// <param name="NewestSentUtc">When the newest prompt was sent, or <see langword="null"/> when there is none.</param>
public sealed record PromptStats(long Sends, long Distinct, long Files, DateTimeOffset? NewestSentUtc)
{
    /// <summary>An empty archive.</summary>
    public static readonly PromptStats Empty = new(0, 0, 0, null);
}

/// <summary>Names of the prompt archive's integration state (<see cref="Storage.ClipStore.GetStateValue"/>).</summary>
public static class PromptArchiveState
{
    /// <summary>
    /// The state set once an agent's first full read completed (its value is when, round-trip format). Until then every
    /// batch of that agent is a first import (<see cref="PromptBatch.IsImport"/>); "Delete stored prompts" removes it.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <returns><c>prompts.claude.imported</c> or <c>prompts.codex.imported</c>.</returns>
    public static string ImportedStateName(PromptAgent agent) => $"prompts.{PromptAgents.WireName(agent)}.imported";

    /// <summary>Formats the time an import completed.</summary>
    /// <param name="at">The time.</param>
    /// <returns>The state value.</returns>
    public static string FormatImported(DateTimeOffset at) => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
