using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Shells;
using BetterClipboard.Windows.Integrations;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// The shell tabs' settings cards (Pwsh, Cmd; CLAUDE.md §2.19): the two switches and their status lines. Status lines
/// carry counts and folders, never a command.
/// </summary>
public sealed partial class SettingsViewModel
{
    /// <summary>See <see cref="AppSettings.ShowPowerShellTab"/> (on by default).</summary>
    [ObservableProperty]
    public partial bool ShowPowerShellTab { get; set; }

    /// <summary>See <see cref="AppSettings.ShowCmdTab"/> (on by default).</summary>
    [ObservableProperty]
    public partial bool ShowCmdTab { get; set; }

    /// <summary>Where the Pwsh tab reads from, and how many commands PowerShell's files held at the last read.</summary>
    [ObservableProperty]
    public partial string PowerShellHistoryStatus { get; set; } = string.Empty;

    /// <summary>Whether cmd windows are being read, how many are open, and how many commands are kept.</summary>
    [ObservableProperty]
    public partial string CmdHistoryStatus { get; set; } = string.Empty;

    /// <summary>Re-reads both shell tabs' state into their status lines.</summary>
    public void RefreshShellHistoryStatus()
    {
        if (!ShowPowerShellTab)
        {
            PowerShellHistoryStatus = "Off — the panel has no Pwsh tab. Commands kept from it stay in your history.";
        }
        else
        {
            var folder = controller.PowerShellHistoryFolder ?? PowerShellHistorySource.ResolveFolder();
            int records = controller.PowerShellRecordCount;
            PowerShellHistoryStatus = !controller.IsPowerShellTabAvailable
                ? $"No PowerShell history file yet in {folder} — the tab appears once PowerShell saved a command."
                : records < 0
                    ? $"Reads {folder} when the tab opens."
                    : $"Reads {folder} when the tab opens: {records:N0} commands at the last look (PowerShell itself loads the newest 4,096).";
        }

        if (!ShowCmdTab)
        {
            CmdHistoryStatus = "Off — Command Prompt windows are not read, and nothing is kept.";
            return;
        }

        if (!controller.IsCmdHistoryReading)
        {
            CmdHistoryStatus = "Starting… (if this stays, the log says why).";
            return;
        }

        int windows = controller.CmdOpenWindows;
        var open = windows < 0 ? "Reading open Command Prompt windows every 30 seconds."
            : $"Reading open Command Prompt windows every 30 seconds: {windows:N0} open now.";
        int kept = controller.CmdKeptCount;
        CmdHistoryStatus = $"{open}\n{kept:N0} command{(kept == 1 ? string.Empty : "s")} kept (newest {CmdHistory.MaxKept:N0}).";
    }

    /// <summary>Persists the Pwsh tab switch; the tab and the status line follow the controller's status event.</summary>
    /// <param name="value">New value.</param>
    partial void OnShowPowerShellTabChanged(bool value)
    {
        Update(s => s with { ShowPowerShellTab = value });
        RefreshShellHistoryStatus();
    }

    /// <summary>
    /// Persists the Cmd tab switch (the controller starts the reading, or stops it and deletes the kept list); the
    /// status line follows the controller's status event.
    /// </summary>
    /// <param name="value">New value.</param>
    partial void OnShowCmdTabChanged(bool value)
    {
        Update(s => s with { ShowCmdTab = value });
        RefreshShellHistoryStatus();
    }
}
