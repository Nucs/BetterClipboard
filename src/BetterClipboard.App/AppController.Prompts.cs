using BetterClipboard.Core.Content;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Prompts;
using BetterClipboard.Core.Settings;
using BetterClipboard.Windows.Input;
using BetterClipboard.Windows.Integrations;

namespace BetterClipboard.App;

/// <summary>
/// The prompt tabs' half of the controller (Claude, Codex; CLAUDE.md §2.21): the two archive readers, whether each tab is
/// available, and what the tabs' archived prompts do when the user acts on one.
/// </summary>
/// <remarks>
/// An archived prompt is not a history entry. Pasting, copying or keeping one stores it — as <see cref="ClipOrigin.ClaudeCode"/>
/// or <see cref="ClipOrigin.Codex"/>, with the source <see cref="PromptAgents.SourceOf"/> gives — and only then do pause,
/// ignored apps and "Forget forever" decide whether it is kept, in the history service like for any copy. Deleting and
/// forgetting work on the archive directly. Nothing here ever logs a prompt.
/// </remarks>
public sealed partial class AppController
{
    /// <summary>Claude Code's archive reader (created in <see cref="Start"/>, switched with its setting, disposed on exit).</summary>
    private AgentPromptsIntegration? claudePrompts;

    /// <summary>Codex's archive reader.</summary>
    private AgentPromptsIntegration? codexPrompts;

    /// <summary>Whether Claude Code's folder or the archive had anything at the last look (pool thread writes, UI reads).</summary>
    private volatile bool claudeHasPrompts;

    /// <summary>Whether Codex's folder or the archive had anything at the last look.</summary>
    private volatile bool codexHasPrompts;

    /// <summary>
    /// Raised on the UI thread when a prompt tab may have appeared or disappeared, an archive reader started, stopped or made
    /// progress, or prompts were added or deleted; the flyout updates its tabs, Settings its status lines.
    /// </summary>
    public event EventHandler<PromptAgent>? PromptsStatusChanged;

    /// <summary>
    /// Whether the panel shows a prompt tab: its setting is on, and the agent's folder has files or the archive has its
    /// prompts (an agent uninstalled after use keeps its tab: the archive is the point).
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <returns><see langword="true"/> when the tab shows.</returns>
    public bool IsPromptTabAvailable(PromptAgent agent) => IsPromptSettingOn(agent) && (agent == PromptAgent.Codex ? codexHasPrompts : claudeHasPrompts);

    /// <summary>Whether an agent's archive setting is on.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The setting.</returns>
    public bool IsPromptSettingOn(PromptAgent agent) => agent == PromptAgent.Codex
        ? settings?.Current.KeepCodexPrompts == true
        : settings?.Current.KeepClaudeCodePrompts == true;

    /// <summary>An agent's archive reader (Settings reads its status), or <see langword="null"/> before <see cref="Start"/>.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The reader.</returns>
    public AgentPromptsIntegration? PromptsReader(PromptAgent agent) => agent == PromptAgent.Codex ? codexPrompts : claudePrompts;

    /// <summary>
    /// Brings a prompt tab up to date before it loads (the tab is being entered): the agent's histories and the files being
    /// written are checked, waiting at most a moment.
    /// </summary>
    /// <param name="agent">The tab's agent.</param>
    /// <returns>A task completing when the check ended or the wait ran out (never throws).</returns>
    public async Task CatchUpPromptsAsync(PromptAgent agent)
    {
        try
        {
            if (PromptsReader(agent) is { } reader && IsPromptSettingOn(agent))
            {
                await reader.CatchUpAsync(TimeSpan.FromMilliseconds(600));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Catching up the {PromptAgents.NameOf(agent)} prompts failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Pastes (or only copies) an archived prompt: its full text goes onto the clipboard (CRLF line breaks), and it is stored
    /// like a copy (see the class remarks) — our own clipboard write is not captured (echo suppression).
    /// </summary>
    /// <param name="prompt">The prompt (a tab row).</param>
    /// <param name="paste">Inject Ctrl+V into the target (otherwise only copy).</param>
    /// <returns>A task completing when done; failures are logged, never thrown (fire-and-forget safe).</returns>
    public async Task PastePromptAsync(PromptSummary prompt, bool paste = true)
    {
        var target = pasteTarget?.TargetWindow ?? 0;
        try
        {
            if (await LoadPromptTextAsync(prompt) is not { } text)
            {
                flyout?.Dismiss(restoreFocus: true);
                return;
            }

            await monitor!.WriteAsync(PromptFormats(text));
            _ = StorePromptAsync(prompt.Agent, text, pin: false);
            flyout?.Dismiss(restoreFocus: true);
            if (paste && Settings.Current.PasteOnSelect && target != 0)
            {
                await InjectPasteAsync(target);
            }
        }
        catch (Exception ex)
        {
            // Content-free: never the prompt.
            AppLog.Error("Pasting an archived prompt failed.", ex);
            flyout?.Dismiss(restoreFocus: true);
        }
    }

    /// <summary>Keeps an archived prompt in the history (Pin, or adding it to a group); it then takes its card's place in the tab.</summary>
    /// <param name="prompt">The prompt.</param>
    /// <param name="pin">Pin the stored entry.</param>
    /// <returns>The stored entry, or <see langword="null"/> when it was not kept (paused, ignored, forgotten, gone, failed — logged).</returns>
    public async Task<ClipEntry?> KeepPromptAsync(PromptSummary prompt, bool pin) =>
        await LoadPromptTextAsync(prompt) is { } text ? await StorePromptAsync(prompt.Agent, text, pin) : null;

    /// <summary>
    /// "Delete until sent again": every send of the prompt's text leaves the agent's archive (the agent's own files are never
    /// changed), and stays out of it until the text is sent again.
    /// </summary>
    /// <param name="agent">The tab's agent.</param>
    /// <param name="textHash">The prompt's text hash.</param>
    /// <returns>A task completing when stored; failures are logged, never thrown.</returns>
    public async Task DeletePromptAsync(PromptAgent agent, string textHash)
    {
        try
        {
            await History.DeletePromptsAsync(agent, textHash);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Deleting an archived prompt failed: {ex.Message}");
        }
    }

    /// <summary>
    /// "Forget forever" for an archived prompt: never recorded again — from any app, and never archived again — and it
    /// leaves both prompt tabs and the history now (stored copies and their look-alikes too).
    /// </summary>
    /// <param name="prompt">The prompt.</param>
    /// <returns>A task completing when stored; failures are logged, never thrown.</returns>
    public async Task ForgetPromptAsync(PromptSummary prompt)
    {
        try
        {
            if (await LoadPromptTextAsync(prompt) is { } text)
            {
                await History.ForgetTextAsync(ClipboardTextOf(text), PromptAgents.NameOf(prompt.Agent));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Forgetting an archived prompt failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Settings' "Delete stored prompts": the agent's whole archive is deleted. While the setting is on, the next pass
    /// imports what the agent still has — so Settings offers it next to the switch, which stops that.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <returns>How many prompts were deleted (0 on failure, logged).</returns>
    public async Task<int> ClearPromptsAsync(PromptAgent agent)
    {
        try
        {
            // Stopped first: a pass in flight would otherwise store prompts and checkpoints again right after the clear.
            // Restarted after: its tracked files are gone, so it must not go on reading only what is appended.
            var reader = PromptsReader(agent);
            if (reader is { IsRunning: true })
            {
                await reader.SetEnabledAsync(false);
            }

            int deleted = await History.ClearPromptsAsync(agent);
            if (reader is not null)
            {
                await reader.SetEnabledAsync(IsPromptSettingOn(agent));
            }

            RefreshPromptAvailability();
            return deleted;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Deleting the stored {PromptAgents.NameOf(agent)} prompts failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Creates both archive readers and starts those whose setting is on (from <see cref="Start"/>). A failure is logged:
    /// the rest of the app keeps working without the tabs.
    /// </summary>
    private void StartPromptArchives()
    {
        try
        {
            claudePrompts = new AgentPromptsIntegration(History, new ClaudeCodePromptSource());
            codexPrompts = new AgentPromptsIntegration(History, new CodexPromptSource());
            claudePrompts.StatusChanged += (_, _) => ui.TryEnqueue(() => PromptsStatusChanged?.Invoke(this, PromptAgent.ClaudeCode));
            codexPrompts.StatusChanged += (_, _) => ui.TryEnqueue(() => PromptsStatusChanged?.Invoke(this, PromptAgent.Codex));
            History.PromptsChanged += (_, e) => ui.TryEnqueue(() =>
            {
                // The first prompts of an agent whose folder was empty make its tab appear.
                if (e.Added > 0 && !(e.Agent == PromptAgent.Codex ? codexHasPrompts : claudeHasPrompts))
                {
                    RefreshPromptAvailability();
                }

                PromptsStatusChanged?.Invoke(this, e.Agent);
            });
            _ = StartPromptArchiveAsync(claudePrompts, Settings.Current.KeepClaudeCodePrompts);
            _ = StartPromptArchiveAsync(codexPrompts, Settings.Current.KeepCodexPrompts);
            RefreshPromptAvailability();
        }
        catch (Exception ex)
        {
            AppLog.Error("Starting the prompt archives failed.", ex);
        }
    }

    /// <summary>Applies the two prompt settings (UI thread, from the settings handler). Idempotent.</summary>
    /// <param name="next">The new settings.</param>
    /// <returns>A task completing when applied (never throws).</returns>
    private async Task ApplyPromptSettingsAsync(AppSettings next)
    {
        foreach (var (reader, on) in new[] { (claudePrompts, next.KeepClaudeCodePrompts), (codexPrompts, next.KeepCodexPrompts) })
        {
            if (reader is null)
            {
                continue;
            }

            try
            {
                await reader.SetEnabledAsync(on);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Switching the {PromptAgents.NameOf(reader.Agent)} prompts failed: {ex.Message}");
            }
        }

        PromptsStatusChanged?.Invoke(this, PromptAgent.ClaudeCode);
        PromptsStatusChanged?.Invoke(this, PromptAgent.Codex);
    }

    /// <summary>Stops both readers on exit (their archives and checkpoints stay: exit is not "off").</summary>
    /// <returns>A task completing when stopped.</returns>
    private async Task DisposePromptArchivesAsync()
    {
        foreach (var reader in new[] { claudePrompts, codexPrompts })
        {
            if (reader is not null)
            {
                await reader.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Looks on the pool for each agent's files and archived prompts (the folders may be redirected or slow) and tells the
    /// flyout when a prompt tab appears or disappears.
    /// </summary>
    private void RefreshPromptAvailability()
    {
        if (claudePrompts is not { } claude || codexPrompts is not { } codex || history is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                bool claudeHas = claude.HasData() || (await History.GetPromptStatsAsync(PromptAgent.ClaudeCode)).Sends > 0;
                bool codexHas = codex.HasData() || (await History.GetPromptStatsAsync(PromptAgent.Codex)).Sends > 0;
                bool claudeChanged = claudeHas != claudeHasPrompts;
                bool codexChanged = codexHas != codexHasPrompts;
                claudeHasPrompts = claudeHas;
                codexHasPrompts = codexHas;
                if (claudeChanged)
                {
                    ui.TryEnqueue(() => PromptsStatusChanged?.Invoke(this, PromptAgent.ClaudeCode));
                }

                if (codexChanged)
                {
                    ui.TryEnqueue(() => PromptsStatusChanged?.Invoke(this, PromptAgent.Codex));
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Looking for the agents' prompts failed: {ex.Message}");
            }
        });
    }

    /// <summary>Starts a reader without letting a failure escape into the fire-and-forget caller.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="enable">The setting at startup.</param>
    /// <returns>A task completing once started (or failed, logged).</returns>
    private static async Task StartPromptArchiveAsync(AgentPromptsIntegration reader, bool enable)
    {
        try
        {
            await reader.SetEnabledAsync(enable);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Starting the {PromptAgents.NameOf(reader.Agent)} prompt archive failed.", ex);
        }
    }

    /// <summary>
    /// The full text of a prompt row: list rows carry only a preview, so the archive row is read again (it may have been
    /// deleted meanwhile).
    /// </summary>
    /// <param name="prompt">The row.</param>
    /// <returns>The text, or <see langword="null"/> when the prompt is gone.</returns>
    private async Task<string?> LoadPromptTextAsync(PromptSummary prompt) =>
        prompt.Text ?? (await History.GetPromptAsync(prompt.Id))?.Text;

    /// <summary>The clipboard formats of a prompt: its text, CRLF line breaks.</summary>
    /// <param name="text">The prompt's text.</param>
    /// <returns>One <c>CF_UNICODETEXT</c> format.</returns>
    private static IReadOnlyList<ClipFormatData> PromptFormats(string text) =>
        [new ClipFormatData(ClipFormatNames.UnicodeText, UnicodeTextCodec.Encode(ClipboardTextOf(text)))];

    /// <summary>A prompt's text with CRLF line breaks — what goes onto the clipboard and into the history.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text with CRLF line breaks.</returns>
    private static string ClipboardTextOf(string text) => Core.Shells.ShellCommand.ToClipboardText(text);

    /// <summary>Stores a prompt as a kept entry through the history worker.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="text">The prompt's text.</param>
    /// <param name="pin">Pin the entry.</param>
    /// <returns>The entry, or <see langword="null"/> when rules rejected it or storing failed (logged).</returns>
    private async Task<ClipEntry?> StorePromptAsync(PromptAgent agent, string text, bool pin)
    {
        try
        {
            var entry = await History.AddAsync(new ClipCapture
            {
                Formats = PromptFormats(text),
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Origin = PromptAgents.OriginOf(agent),
                Pin = pin,
                Source = PromptAgents.SourceOf(agent),
            });
            if (entry is null)
            {
                AppLog.Info("A prompt was not kept (capturing paused, the agent ignored, or forgotten forever).");
            }

            return entry;
        }
        catch (Exception ex)
        {
            AppLog.Error("Keeping an archived prompt failed.", ex);
            return null;
        }
    }
}
