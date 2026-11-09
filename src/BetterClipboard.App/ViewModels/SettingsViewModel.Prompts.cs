using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Prompts;
using BetterClipboard.Core.Settings;
using BetterClipboard.Windows.Integrations;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// The prompt archives' settings cards (Claude Code, Codex; CLAUDE.md §2.21): the two switches and their status lines.
/// Their <em>Delete stored…</em> is every integration's (SettingsViewModel.Stored.cs). Status lines carry folders and
/// counts, never a prompt.
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

    /// <summary>Re-reads both archives' state into their status lines (counts are read on the pool).</summary>
    public void RefreshPromptsStatus()
    {
        _ = RefreshPromptStatusAsync(PromptAgent.ClaudeCode);
        _ = RefreshPromptStatusAsync(PromptAgent.Codex);
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
            }
            else
            {
                ClaudeCodePromptsStatus = status;
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
        // Two counts, two plurals: the sends are worded on their own, then placed in the prompts' sentence.
        var sends = UiText.Plural(stats.Sends, $"{stats.Sends:N0} send", $"{stats.Sends:N0} sends");
        var kept = stats.Sends == 0
            ? UiText.Translate("No prompts stored yet.")
            : UiText.Plural(stats.Distinct, $"{stats.Distinct:N0} prompt stored ({sends})", $"{stats.Distinct:N0} prompts stored ({sends})")
              + (stats.NewestSentUtc is { } newest ? UiText.Format($", the newest {RelativeTimeFormatter.Format(newest, DateTimeOffset.UtcNow)}.") : ".");
        if (!on)
        {
            return stats.Sends == 0
                ? UiText.Format($"Off — {name}'s files are not read, and the panel has no {(agent == PromptAgent.Codex ? "Codex" : "Claude")} tab.")
                : UiText.Format($"Off — {name}'s files are not read. {kept} They stay until you delete them.");
        }

        if (reader is not { IsRunning: true })
        {
            return UiText.Translate("Starting… (if this stays, the log says why).");
        }

        var where = agent == PromptAgent.Codex
            ? UiText.Format($"Reads {reader.Root} (session files and the CLI history)")
            : UiText.Format($"Reads {Path.Combine(reader.Root, ClaudeCodeHistory.FileName)}");
        if (reader.IsImporting)
        {
            var (done, total) = reader.ImportProgress;
            return UiText.Plural(total, $"{where}. First import: {done:N0} of {total:N0} file read. {kept}", $"{where}. First import: {done:N0} of {total:N0} files read. {kept}");
        }

        var leftOut = reader.ExcludedFiles > 0
            ? UiText.Plural(
                reader.ExcludedFiles,
                $" {reader.ExcludedFiles:N0} session file is left out: written by agents or codex exec runs, not typed by you.",
                $" {reader.ExcludedFiles:N0} session files are left out: written by agents or codex exec runs, not typed by you.")
            : string.Empty;
        var session = reader.AddedThisSession > 0 ? UiText.Format($" {reader.AddedThisSession:N0} added since BetterClipboard started.") : string.Empty;
        return UiText.Format($"{where} as {name} writes them (only the new bytes).\n{kept}{session}{leftOut}");
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
