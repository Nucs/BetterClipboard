using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.Prompts;

/// <summary>
/// The AI coding agent a prompt was sent to — which of the panel's prompt tabs (Claude, Codex) lists it. Persisted as
/// an integer (<c>prompts.agent</c>, <c>prompt_files.agent</c>): append only, never renumber.
/// </summary>
public enum PromptAgent
{
    /// <summary>
    /// Anthropic's Claude Code. Its prompt history (<c>~/.claude/history.jsonl</c>) holds every prompt typed into its
    /// input box, pastes kept beside it (CLAUDE.md §2.21); Claude Code prunes it after its cleanup period.
    /// </summary>
    ClaudeCode = 1,

    /// <summary>
    /// OpenAI's Codex (the app, the CLI, the IDE extension). Its session files (<c>~/.codex/sessions/**/rollout-*.jsonl</c>)
    /// record every prompt; its own <c>history.jsonl</c> only the CLI's, and it can be trimmed.
    /// </summary>
    Codex = 2,
}

/// <summary>
/// Which kind of file a prompt was read from. Persisted as an integer (<c>prompts.source</c>, <c>prompt_files.kind</c>):
/// append only, never renumber.
/// </summary>
/// <remarks>
/// The kind decides how a file is parsed and how its prompts merge: the same Codex prompt arrives from
/// <see cref="CodexHistory"/> and from <see cref="CodexRollout"/>, and the store merges the two by session, text and a
/// time window (<see cref="PromptRules.CodexMergeWindow"/>) rather than by key.
/// </remarks>
public enum PromptSourceKind
{
    /// <summary>Claude Code's <c>history.jsonl</c>: one line per prompt (display text, pastes, project, session, time).</summary>
    ClaudeHistory = 1,

    /// <summary>Codex's <c>history.jsonl</c>: one line per prompt the CLI recorded (session id, time in seconds, text).</summary>
    CodexHistory = 2,

    /// <summary>
    /// A Codex session file (a "rollout"): the thread's metadata on its first line, then every event; the prompts are its
    /// <c>user_message</c> events (older threads) or completed <c>UserMessage</c> items (newer ones).
    /// </summary>
    CodexRollout = 3,
}

/// <summary>
/// Names and identities of the two agents: what cards, footers, Settings and the command line call them, and the source
/// app a prompt kept in the history carries.
/// </summary>
/// <remarks>
/// The source display names are unique on purpose: a copy made in a terminal belongs to the terminal's window, never to
/// the agent, so only a keep from a prompt tab produces them, and <see cref="Storage.ClipFilter.ClaudeCode"/> /
/// <see cref="Storage.ClipFilter.Codex"/> can find a row such a keep bumped by its source name alone. They are SQL
/// literals in the store's filter: never put a quote in them.
/// </remarks>
public static class PromptAgents
{
    /// <summary>Display name of Claude Code (cards, Settings, the source of kept prompts).</summary>
    public const string ClaudeCodeName = "Claude Code";

    /// <summary>Display name of Codex.</summary>
    public const string CodexName = "Codex";

    /// <summary>The display name of an agent.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>"Claude Code" or "Codex".</returns>
    public static string NameOf(PromptAgent agent) => agent == PromptAgent.Codex ? CodexName : ClaudeCodeName;

    /// <summary>
    /// The command line's name of an agent (<c>bclip prompts -a claude</c>, JSON <c>"agent"</c>): short, lowercase,
    /// stable across versions.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <returns><c>claude</c> or <c>codex</c>.</returns>
    public static string WireName(PromptAgent agent) => agent == PromptAgent.Codex ? "codex" : "claude";

    /// <summary>Parses a command-line agent name (case-insensitive; <c>claude-code</c> and <c>cc</c> are accepted too).</summary>
    /// <param name="name">The name.</param>
    /// <param name="agent">The agent.</param>
    /// <returns>Whether the name is known.</returns>
    public static bool TryParse(string? name, out PromptAgent agent)
    {
        switch ((name ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "claude" or "claude-code" or "claudecode" or "cc":
                agent = PromptAgent.ClaudeCode;
                return true;
            case "codex":
                agent = PromptAgent.Codex;
                return true;
            default:
                agent = default;
                return false;
        }
    }

    /// <summary>
    /// The source of a prompt kept in the history (pasted, copied, pinned or grouped from a prompt tab). The process name
    /// is what "Ignored apps" matches: <c>claude</c> or <c>codex</c> — the agents' own executables — so ignoring
    /// <c>claude.exe</c> also stops the archive and keeps from the Claude tab.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The source app info (no executable path: the prompt did not come from one process's clipboard write).</returns>
    public static SourceAppInfo SourceOf(PromptAgent agent) => agent == PromptAgent.Codex
        ? new SourceAppInfo("codex", null, CodexName)
        : new SourceAppInfo("claude", null, ClaudeCodeName);

    /// <summary>The history origin of a kept prompt.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns><see cref="ClipOrigin.ClaudeCode"/> or <see cref="ClipOrigin.Codex"/>.</returns>
    public static ClipOrigin OriginOf(PromptAgent agent) => agent == PromptAgent.Codex ? ClipOrigin.Codex : ClipOrigin.ClaudeCode;

    /// <summary>The agent a source kind belongs to.</summary>
    /// <param name="source">The source kind.</param>
    /// <returns>Its agent.</returns>
    public static PromptAgent AgentOf(PromptSourceKind source) => source == PromptSourceKind.ClaudeHistory ? PromptAgent.ClaudeCode : PromptAgent.Codex;
}
