using BetterClipboard.Core.Content;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Shells;
using BetterClipboard.Windows.Input;
using BetterClipboard.Windows.Integrations;

namespace BetterClipboard.App;

/// <summary>
/// The shell tabs' half of the controller (Pwsh, Cmd; CLAUDE.md §2.19): the two history sources, whether each tab
/// is available, and what the tabs' live commands do when the user acts on one.
/// </summary>
/// <remarks>
/// A live command is not a history entry. Pasting, copying or keeping one stores it — as
/// <see cref="ClipOrigin.PowerShell"/> or <see cref="ClipOrigin.Cmd"/>, with the source <see cref="ShellSources.For"/>
/// gives — and only then do pause, ignored apps and "Forget forever" decide whether it is kept, in the history
/// service like for any copy. Hiding and forgetting work without storing it. Nothing here ever logs a command.
/// </remarks>
public sealed partial class AppController
{
    /// <summary>PowerShell's history files (the Pwsh tab); created in <see cref="Start"/>, read only when the tab loads.</summary>
    private PowerShellHistorySource? powerShell;

    /// <summary>The cmd windows' history and the kept list (the Cmd tab); created in <see cref="Start"/>, reads on a timer while on.</summary>
    private CmdHistorySource? cmdHistory;

    /// <summary>Whether PowerShell's folder held a history file at the last look (pool thread writes, UI reads).</summary>
    private volatile bool powerShellHasHistory;

    /// <summary>
    /// Raised on the UI thread when a shell tab may have appeared or disappeared (a setting changed, PowerShell's file
    /// was found or lost) or the Cmd tab's kept list changed; the flyout updates its tabs, Settings its status lines.
    /// </summary>
    public event EventHandler? ShellHistoryStatusChanged;

    /// <summary>Whether the panel shows its Pwsh tab: the setting is on and PowerShell's folder has a history file.</summary>
    public bool IsPowerShellTabAvailable => settings?.Current.ShowPowerShellTab == true && powerShellHasHistory;

    /// <summary>
    /// Whether the panel shows its Cmd tab: the setting. Like the Run tab it waits for nothing — cmd exists on every
    /// Windows, and an empty tab explains how it fills.
    /// </summary>
    public bool IsCmdTabAvailable => settings?.Current.ShowCmdTab == true;

    /// <summary>The folder the Pwsh tab reads (for Settings), or <see langword="null"/> before <see cref="Start"/>.</summary>
    public string? PowerShellHistoryFolder => powerShell?.Folder;

    /// <summary>Commands in PowerShell's files at the last read (repeats included), -1 before the first read.</summary>
    public int PowerShellRecordCount => powerShell?.LastRecordCount ?? -1;

    /// <summary>Whether the Cmd tab's periodic reading runs.</summary>
    public bool IsCmdHistoryReading => cmdHistory?.IsRunning == true;

    /// <summary>Interactive cmd windows found at the last read (-1 before the first).</summary>
    public int CmdOpenWindows => cmdHistory?.OpenWindows ?? -1;

    /// <summary>Command Prompt commands BetterClipboard keeps for the Cmd tab.</summary>
    public int CmdKeptCount => cmdHistory?.KeptCount ?? 0;

    /// <summary>
    /// The live commands of a shell tab: PowerShell's files (read now, on the pool) or the cmd windows plus the kept
    /// list. Empty while the tab's setting is off.
    /// </summary>
    /// <param name="shell">The tab's shell.</param>
    /// <param name="cancellationToken">Cancels a superseded load.</param>
    /// <returns>The commands, newest first.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<IReadOnlyList<ShellCommand>> GetShellCommandsAsync(ShellKind shell, CancellationToken cancellationToken)
    {
        if (shell == ShellKind.PowerShell)
        {
            return powerShell is { } source && Settings.Current.ShowPowerShellTab
                ? await Task.Run(source.ReadCommands, cancellationToken)
                : [];
        }

        return cmdHistory is { } cmd && Settings.Current.ShowCmdTab ? await cmd.GetCommandsAsync(cancellationToken) : [];
    }

    /// <summary>
    /// Pastes (or only copies) a shell tab's live command: its text goes onto the clipboard (CRLF line breaks), and it is
    /// stored like a copy (see the class remarks) — our own clipboard write is not captured (echo suppression).
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="paste">Inject Ctrl+V into the target (otherwise only copy).</param>
    /// <returns>A task completing when done; failures are logged, never thrown (fire-and-forget safe).</returns>
    public async Task PasteCommandAsync(ShellCommand command, bool paste = true)
    {
        var target = pasteTarget?.TargetWindow ?? 0;
        try
        {
            await monitor!.WriteAsync(CommandFormats(command));
            _ = StoreCommandAsync(command, pin: false);
            flyout?.Dismiss(restoreFocus: true);
            if (paste && Settings.Current.PasteOnSelect && target != 0)
            {
                await InjectPasteAsync(target);
            }
        }
        catch (Exception ex)
        {
            // Content-free: never the command.
            AppLog.Error("Pasting a shell command failed.", ex);
            flyout?.Dismiss(restoreFocus: true);
        }
    }

    /// <summary>Keeps a live command in the history (Pin, or adding it to a group), which then takes its place in the tab.</summary>
    /// <param name="command">The command.</param>
    /// <param name="pin">Pin the stored entry.</param>
    /// <returns>The stored entry, or <see langword="null"/> when it was not kept (paused, ignored, forgotten, failed — logged).</returns>
    public Task<ClipEntry?> KeepCommandAsync(ShellCommand command, bool pin) => StoreCommandAsync(command, pin);

    /// <summary>
    /// Hides a command from its shell tab until it is typed again (Delete: the shell's own history is never changed).
    /// Kept in the encrypted store, by content hash — never the command's text.
    /// </summary>
    /// <param name="shell">The tab's shell.</param>
    /// <param name="contentHash">The command's <see cref="ShellCommand.ContentHash"/>.</param>
    /// <param name="count">Its <see cref="ShellCommand.Count"/> now.</param>
    /// <returns>A task completing when stored; failures are logged, never thrown.</returns>
    public async Task HideCommandAsync(ShellKind shell, string contentHash, int count)
    {
        try
        {
            var name = ShellTab.HiddenStateName(shell);
            var hidden = ShellTab.ParseHidden(await History.GetStateValueAsync(name));
            await History.SetStateValueAsync(name, ShellTab.FormatHidden(ShellTab.Hide(hidden, contentHash, count)));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Hiding a command in a shell tab failed: {ex.Message}");
        }
    }

    /// <summary>
    /// "Forget forever" for a live command: never recorded again, from any app, and the tabs stop showing it. Works
    /// without storing it first (and while paused); stored copies and their look-alikes are deleted.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <returns>A task completing when stored; failures are logged, never thrown.</returns>
    public async Task ForgetCommandAsync(ShellCommand command)
    {
        try
        {
            await History.ForgetTextAsync(command.ClipboardText, command.Source);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Forgetting a command from a shell tab failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates both sources and starts the Cmd tab's reading when its setting is on (from <see cref="Start"/>). A failure
    /// is logged: the rest of the app keeps working without the tabs.
    /// </summary>
    private void StartShellHistories()
    {
        try
        {
            powerShell = new PowerShellHistorySource();
            RefreshPowerShellAvailability();

            cmdHistory = new CmdHistorySource(History, () => Settings.Current.IsCapturePaused, ExecutablePath);
            cmdHistory.StatusChanged += (_, _) => ui.TryEnqueue(() => ShellHistoryStatusChanged?.Invoke(this, EventArgs.Empty));
            _ = StartCmdHistoryAsync(cmdHistory, Settings.Current.ShowCmdTab);
        }
        catch (Exception ex)
        {
            AppLog.Error("Starting the shell history tabs failed.", ex);
        }
    }

    /// <summary>Applies the two shell-tab settings (UI thread, from the settings handler). Idempotent.</summary>
    /// <param name="next">The new settings.</param>
    /// <returns>A task completing when applied (never throws).</returns>
    private async Task ApplyShellSettingsAsync(AppSettings next)
    {
        if (cmdHistory is not null)
        {
            try
            {
                await cmdHistory.SetEnabledAsync(next.ShowCmdTab);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Switching the Cmd tab failed: {ex.Message}");
            }
        }

        // The Pwsh tab needs nothing started; it only appears or disappears.
        ShellHistoryStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops the cmd reader on exit (its kept list stays: exit is not "off").</summary>
    /// <returns>A task completing when stopped.</returns>
    private async Task DisposeShellHistoriesAsync()
    {
        if (cmdHistory is not null)
        {
            await cmdHistory.DisposeAsync();
        }
    }

    /// <summary>
    /// Looks for PowerShell's history file on the pool (the folder may be redirected to a share, which must not stall
    /// the UI thread) and tells the flyout when the Pwsh tab appears or disappears.
    /// </summary>
    private void RefreshPowerShellAvailability()
    {
        if (powerShell is not { } source)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            bool has = source.HasHistory();
            if (has != powerShellHasHistory)
            {
                powerShellHasHistory = has;
                ui.TryEnqueue(() => ShellHistoryStatusChanged?.Invoke(this, EventArgs.Empty));
            }
        });
    }

    /// <summary>Starts the Cmd tab's reading without letting a failure escape into the fire-and-forget caller.</summary>
    /// <param name="source">The source.</param>
    /// <param name="enable">The setting at startup.</param>
    /// <returns>A task completing once started (or failed, logged).</returns>
    private static async Task StartCmdHistoryAsync(CmdHistorySource source, bool enable)
    {
        try
        {
            await source.SetEnabledAsync(enable);
        }
        catch (Exception ex)
        {
            AppLog.Error("Starting the Cmd tab's reading failed.", ex);
        }
    }

    /// <summary>The clipboard formats of a command: its text, CRLF line breaks.</summary>
    /// <param name="command">The command.</param>
    /// <returns>One <c>CF_UNICODETEXT</c> format.</returns>
    private static IReadOnlyList<ClipFormatData> CommandFormats(ShellCommand command) =>
        [new ClipFormatData(ClipFormatNames.UnicodeText, UnicodeTextCodec.Encode(command.ClipboardText))];

    /// <summary>Stores a command as a shell item through the history worker.</summary>
    /// <param name="command">The command.</param>
    /// <param name="pin">Pin the entry.</param>
    /// <returns>The entry, or <see langword="null"/> when rules rejected it or storing failed (logged).</returns>
    private async Task<ClipEntry?> StoreCommandAsync(ShellCommand command, bool pin)
    {
        try
        {
            var entry = await History.AddAsync(new ClipCapture
            {
                Formats = CommandFormats(command),
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Origin = ShellSources.OriginOf(command.Shell),
                Pin = pin,
                Source = ShellSources.For(command.Shell),
            });
            if (entry is null)
            {
                AppLog.Info("A shell command was not kept (capturing paused, the shell ignored, or forgotten forever).");
            }

            return entry;
        }
        catch (Exception ex)
        {
            AppLog.Error("Keeping a shell command failed.", ex);
            return null;
        }
    }
}
