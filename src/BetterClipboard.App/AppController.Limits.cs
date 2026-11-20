using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Everything;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Shells;
using BetterClipboard.Windows.Integrations;

namespace BetterClipboard.App;

/// <summary>
/// The integrations' list limits (CLAUDE.md §2.62): Settings' numbers for the Cmd tab's kept commands and for the hidden
/// commands and files of the Pwsh, Cmd and Everything tabs — no limit by default (<see cref="ListLimit"/>). The lists follow
/// a lowered number at once, so Settings' counts and the tabs show what the user just typed instead of waiting for the next
/// write to trim them. (The screenshot catch-up limits need nothing here: each catch-up asks its setting.)
/// </summary>
public sealed partial class AppController
{
    /// <summary>
    /// The list limits the stored lists were last brought in line with (<see cref="ApplyListLimitsAsync"/>); set by
    /// <see cref="StartCore"/> from the settings it reads, so the very first change to a limit is compared with them.
    /// Compared, not trimmed, at every settings change: most changes are to other settings.
    /// </summary>
    private (int KeptCmd, int HiddenPwsh, int HiddenCmd, int HiddenEverything)? appliedListLimits;

    /// <summary>The list limits of a settings snapshot, in the shape <see cref="appliedListLimits"/> keeps.</summary>
    /// <param name="settings">The snapshot.</param>
    /// <returns>The four limits.</returns>
    private static (int KeptCmd, int HiddenPwsh, int HiddenCmd, int HiddenEverything) ListLimitsOf(AppSettings settings) =>
        (settings.MaxKeptCmdCommands, settings.MaxHiddenPowerShellCommands, settings.MaxHiddenCmdCommands, settings.MaxHiddenEverythingPicks);

    /// <summary>The hide limit of a shell tab, from the current settings (<see cref="ListLimit"/>: 0 = none).</summary>
    /// <param name="shell">The tab's shell.</param>
    /// <returns>The limit.</returns>
    private int HiddenCommandsLimit(ShellKind shell) => shell == ShellKind.PowerShell
        ? Settings.Current.MaxHiddenPowerShellCommands
        : Settings.Current.MaxHiddenCmdCommands;

    /// <summary>
    /// Brings the stored lists in line with changed limits (UI thread, from the settings handler): the Cmd tab's kept
    /// commands (<see cref="CmdHistorySource.ApplyLimitAsync"/>) and the three hidden lists, whose oldest entries a lowered
    /// limit deletes. A raised or lifted limit changes nothing stored — nothing an earlier limit dropped comes back.
    /// </summary>
    /// <param name="next">The new settings.</param>
    /// <returns>A task completing when applied; failures are logged, never thrown.</returns>
    private async Task ApplyListLimitsAsync(AppSettings next)
    {
        var limits = ListLimitsOf(next);

        // Not set only before StartCore read the settings, when there are no lists to bring in line yet.
        if (appliedListLimits is not { } applied || applied == limits)
        {
            appliedListLimits = limits;
            return;
        }

        appliedListLimits = limits;
        if (limits.KeptCmd != applied.KeptCmd && cmdHistory is not null)
        {
            await cmdHistory.ApplyLimitAsync();
        }

        if (limits.HiddenPwsh != applied.HiddenPwsh)
        {
            await TrimHiddenCommandsAsync(ShellKind.PowerShell, limits.HiddenPwsh);
        }

        if (limits.HiddenCmd != applied.HiddenCmd)
        {
            await TrimHiddenCommandsAsync(ShellKind.Cmd, limits.HiddenCmd);
        }

        if (limits.HiddenEverything != applied.HiddenEverything)
        {
            await TrimHiddenPicksAsync(limits.HiddenEverything);
        }
    }

    /// <summary>
    /// Keeps a shell tab's newest <paramref name="limit"/> hides (Settings lowered it): the older hides go, so their
    /// commands show in the tab again. Written only when something was dropped.
    /// </summary>
    /// <param name="shell">The tab's shell.</param>
    /// <param name="limit">The new limit (<see cref="ListLimit"/>: 0 = none, which drops nothing).</param>
    /// <returns>A task completing when written; failures are logged, never thrown.</returns>
    private async Task TrimHiddenCommandsAsync(ShellKind shell, int limit)
    {
        try
        {
            var name = ShellTab.HiddenStateName(shell);
            var hidden = ShellTab.ParseHidden(await History.GetStateValueAsync(name));
            var kept = ShellTab.Trim(hidden, limit);
            if (kept.Count < hidden.Count)
            {
                await History.SetStateValueAsync(name, ShellTab.FormatHidden(kept));

                // Counts only: the hides are hashes, never a command.
                AppLog.Info($"The {shell} tab's hidden commands follow their new limit: {hidden.Count - kept.Count} shown again, {kept.Count} still hidden.");
                ShellHistoryStatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Applying the limit of a shell tab's hidden commands failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Keeps the Everything tab's newest <paramref name="limit"/> hides (Settings lowered it): the older hides go, so their
    /// files and folders show in the tab again. Written only when something was dropped.
    /// </summary>
    /// <param name="limit">The new limit (<see cref="ListLimit"/>: 0 = none, which drops nothing).</param>
    /// <returns>A task completing when written; failures are logged, never thrown.</returns>
    private async Task TrimHiddenPicksAsync(int limit)
    {
        try
        {
            var hidden = EverythingTab.ParseHidden(await History.GetStateValueAsync(EverythingTab.HiddenStateName));
            var kept = EverythingTab.Trim(hidden, limit);
            if (kept.Count < hidden.Count)
            {
                await History.SetStateValueAsync(EverythingTab.HiddenStateName, EverythingTab.FormatHidden(kept));

                // Counts only: never a path.
                AppLog.Info($"The Everything tab's hidden files follow their new limit: {hidden.Count - kept.Count} shown again, {kept.Count} still hidden.");
                EverythingStatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Applying the limit of the Everything tab's hidden files failed: {ex.Message}");
        }
    }
}
