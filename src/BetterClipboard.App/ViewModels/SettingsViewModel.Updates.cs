using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// Settings › Updates: the switch for the daily check, its status line, "Check now", and the words of the button that
/// opens the update dialog.
/// </summary>
/// <remarks>
/// The switch decides the one thing BetterClipboard asks a server by itself (the public release list, once a day), so
/// the status line always says what this copy does: whether it checks, when it last did, what it found, and why not
/// when a check or an installation failed (<see cref="UpdateText.SettingsStatus"/>).
/// </remarks>
public sealed partial class SettingsViewModel
{
    /// <summary>See <see cref="AppSettings.CheckForUpdates"/> (on by default).</summary>
    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    /// <summary>What is known about updates, in sentences; one line per fact.</summary>
    [ObservableProperty]
    public partial string UpdateStatus { get; set; } = string.Empty;

    /// <summary>Whether "Check now" can be pressed: the update system runs and is neither checking nor installing.</summary>
    [ObservableProperty]
    public partial bool CanCheckForUpdates { get; set; }

    /// <summary>
    /// Whether the switch means anything for this copy: not for a Chocolatey install (Chocolatey updates it, so it never
    /// checks by itself), and not when the update system did not start.
    /// </summary>
    [ObservableProperty]
    public partial bool CanToggleUpdateCheck { get; set; }

    /// <summary>Whether the update system runs at all (the button that opens the update dialog is disabled otherwise).</summary>
    [ObservableProperty]
    public partial bool HasUpdateSystem { get; set; }

    /// <summary>Whether an update waits that the user did not skip: the dialog's button is then the accent button.</summary>
    [ObservableProperty]
    public partial bool IsUpdateWaiting { get; set; }

    /// <summary>The label of the button that opens the update dialog: "Update to 0.2.7…" while one is offered, else "Release notes…".</summary>
    [ObservableProperty]
    public partial string UpdateActionText { get; set; } = "Release notes…";

    /// <summary>
    /// Re-reads the update status. Called for every change, which during a download is up to ten times a second: it
    /// only formats a few strings.
    /// </summary>
    public void RefreshUpdateStatus()
    {
        if (controller.UpdateStatus is not { } snapshot)
        {
            HasUpdateSystem = false;
            CanCheckForUpdates = false;
            CanToggleUpdateCheck = false;
            IsUpdateWaiting = false;
            UpdateActionText = "Release notes…";
            UpdateStatus = "Updates are not available in this build. Get new versions from the releases page on GitHub.";
            return;
        }

        HasUpdateSystem = true;
        CanCheckForUpdates = snapshot.Phase == UpdatePhase.Idle;
        CanToggleUpdateCheck = snapshot.InstallKind != UpdateInstallKind.Chocolatey;
        IsUpdateWaiting = snapshot.WantsAttention;
        UpdateActionText = snapshot.Offer is { } offer ? $"Update to {offer.Release.Version}…" : "Release notes…";
        UpdateStatus = UpdateText.SettingsStatus(snapshot, DateTimeOffset.Now);
    }

    /// <summary>"Check now": asks GitHub for the release list once, whatever the switch says.</summary>
    /// <returns>A task completing when the check ended; it never fails (the status line says how it went).</returns>
    public Task CheckForUpdatesNowAsync() => controller.CheckForUpdatesAsync();

    /// <summary>Persists the switch; the update service follows through the settings change, and the status line through its event.</summary>
    /// <param name="value">New value.</param>
    partial void OnCheckForUpdatesChanged(bool value)
    {
        Update(s => s with { CheckForUpdates = value });
        RefreshUpdateStatus();
    }
}
