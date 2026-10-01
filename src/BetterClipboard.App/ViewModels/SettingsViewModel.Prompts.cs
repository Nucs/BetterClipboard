using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Prompts;
using BetterClipboard.Core.Settings;
using BetterClipboard.Windows.Integrations;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// The prompt archives' settings cards (Claude Code, Codex; CLAUDE.md §2.21): the two switches, their status lines and the
/// "Delete stored prompts" actions. Status lines carry folders and counts, never a prompt.
/// </summary>
public sealed partial class SettingsViewModel
{
    /// <summary>See <see cref="AppSettings.KeepClaudeCodePrompts"/> (on by default).</summary>
    [ObservableProperty]
    public partial bool KeepClaudeCodePrompts { get; set; }

    /// <summary>See <see cref="AppSettings.KeepCodexPrompts"/> (on by default).</summary>
    [ObservableProperty]
    public partial bool KeepCodexPrompts { get; set; }

    /// <summary>What Claude Code's archive reads, how many prompts it keeps, and whether a first import runs.</summary>
    [ObservableProperty]
    public partial string ClaudeCodePromptsStatus { get; set; } = string.Empty;

    /// <summary>What Codex's archive reads, how many prompts it keeps, and what it leaves out.</summary>
    [ObservableProperty]
    public partial string CodexPromptsStatus { get; set; } = string.Empty;

    /// <summary>Whether Claude Code's archive holds prompts (enables its "Delete stored prompts…").</summary>
    [ObservableProperty]
    public partial bool HasClaudeCodePrompts { get; set; }

    /// <summary>Whether Codex's archive holds prompts (enables its "Delete stored prompts…").</summary>
    [ObservableProperty]
    public partial bool HasCodexPrompts { get; set; }

    /// <summary>Claude Code's archived prompts at the last refresh (the delete confirmation names the number).</summary>
    public long ClaudeCodePromptCount { get; private set; }

    /// <summary>Codex's archived prompts at the last refresh.</summary>
    public long CodexPromptCount { get; private set; }

    /// <summary>Re-reads both archives' state into their status lines (counts are read on the pool).</summary>
    public void RefreshPromptsStatus()
    {
        _ = RefreshPromptStatusAsync(PromptAgent.ClaudeCode);
        _ = RefreshPromptStatusAsync(PromptAgent.Codex);
    }

    /// <summary>
    /// "Delete stored prompts": deletes an agent's whole archive (the agent's own files are never touched). While its switch
    /// is on, BetterClipboard imports what the agent still has right away — the confirmation says so.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <returns>How many prompts were deleted.</returns>
    public async Task<int> DeleteStoredPromptsAsync(PromptAgent agent)
    {
        int deleted = await controller.ClearPromptsAsync(agent);
        await RefreshPromptStatusAsync(agent);
        return deleted;
    }

    /// <summary>Re-reads one agent's archive state into its status line.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>A task completing when updated (failures are logged, never thrown).</returns>
    private async Task RefreshPromptStatusAsync(PromptAgent agent)
    {
        try
        {
            var stats = await controller.History.GetPromptStatsAsync(agent);
            var reader = controller.PromptsReader(agent);
            bool on = agent == PromptAgent.Codex ? KeepCodexPrompts : KeepClaudeCodePrompts;
            var status = PromptStatusLine(agent, on, reader, stats);
            if (agent == PromptAgent.Codex)
            {
                CodexPromptsStatus = status;
                CodexPromptCount = stats.Sends;
                HasCodexPrompts = stats.Sends > 0;
            }
            else
            {
                ClaudeCodePromptsStatus = status;
                ClaudeCodePromptCount = stats.Sends;
                HasClaudeCodePrompts = stats.Sends > 0;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Reading the {PromptAgents.NameOf(agent)} prompt archive's state failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The status line of an agent's card: off (and what stays), starting, importing (with progress), or what is read and
    /// how much is kept.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="on">Its switch.</param>
    /// <param name="reader">Its reader, or <see langword="null"/>.</param>
    /// <param name="stats">Its archive's counts.</param>
    /// <returns>The text.</returns>
    private static string PromptStatusLine(PromptAgent agent, bool on, AgentPromptsIntegration? reader, PromptStats stats)
    {
        var name = PromptAgents.NameOf(agent);
        var kept = stats.Sends == 0
            ? "No prompts stored yet."
            : $"{stats.Distinct:N0} prompt{(stats.Distinct == 1 ? string.Empty : "s")} stored ({stats.Sends:N0} sends)"
              + (stats.NewestSentUtc is { } newest ? $", the newest {RelativeTimeFormatter.Format(newest, DateTimeOffset.UtcNow)}." : ".");
        if (!on)
        {
            return stats.Sends == 0
                ? $"Off — {name}'s files are not read, and the panel has no {(agent == PromptAgent.Codex ? "Codex" : "Claude")} tab."
                : $"Off — {name}'s files are not read. {kept} They stay until you delete them.";
        }

        if (reader is not { IsRunning: true })
        {
            return "Starting… (if this stays, the log says why).";
        }

        var where = agent == PromptAgent.Codex
            ? $"Reads {reader.Root} (session files and the CLI history)"
            : $"Reads {Path.Combine(reader.Root, ClaudeCodeHistory.FileName)}";
        if (reader.IsImporting)
        {
            var (done, total) = reader.ImportProgress;
            return $"{where}. First import: {done:N0} of {total:N0} files read. {kept}";
        }

        var leftOut = reader.ExcludedFiles > 0
            ? $" {reader.ExcludedFiles:N0} session file{(reader.ExcludedFiles == 1 ? " is" : "s are")} left out: written by agents or codex exec runs, not typed by you."
            : string.Empty;
        var session = reader.AddedThisSession > 0 ? $" {reader.AddedThisSession:N0} added since BetterClipboard started." : string.Empty;
        return $"{where} as {name} writes them (only the new bytes).\n{kept}{session}{leftOut}";
    }

    /// <summary>Persists the Claude Code switch (the reader starts or stops; the status line follows its event).</summary>
    /// <param name="value">New value.</param>
    partial void OnKeepClaudeCodePromptsChanged(bool value)
    {
        Update(s => s with { KeepClaudeCodePrompts = value });
        RefreshPromptsStatus();
    }

    /// <summary>Persists the Codex switch (the reader starts or stops; the status line follows its event).</summary>
    /// <param name="value">New value.</param>
    partial void OnKeepCodexPromptsChanged(bool value)
    {
        Update(s => s with { KeepCodexPrompts = value });
        RefreshPromptsStatus();
    }
}
