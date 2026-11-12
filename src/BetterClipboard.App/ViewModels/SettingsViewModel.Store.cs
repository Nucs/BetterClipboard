using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Localization;
using BetterClipboard.Windows.Shell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// Settings in the Microsoft Store edition (CLAUDE.md §2.47): which edition this is, and "Start with Windows" through
/// the package's startup task instead of the Run key.
/// </summary>
/// <remarks>
/// The startup task has more states than a Run entry: Windows' own Startup apps list (Settings › Apps › Startup, and
/// Task Manager) can turn it off, and then only that list turns it on again; an organization's policy can fix it either
/// way. The card says which state it is in, disables the switch when it cannot change anything, and offers Windows' list
/// when that is where the answer lies.
/// </remarks>
public sealed partial class SettingsViewModel
{
    /// <summary>
    /// Whether this copy runs from the Microsoft Store package (constant for the process). The page shows that edition's
    /// controls where the two differ.
    /// </summary>
    public bool IsStoreEdition => AppController.IsStoreEdition;

    /// <summary>The opposite of <see cref="IsStoreEdition"/>, for controls only the other editions have.</summary>
    public bool IsNotStoreEdition => !AppController.IsStoreEdition;

    /// <summary>
    /// Whether the Start with Windows switch can change anything. Always for a copy that uses the Run key; in the Store
    /// edition not while Windows' Startup apps list or a policy decides.
    /// </summary>
    [ObservableProperty]
    public partial bool CanChangeLaunchAtStartup { get; set; } = true;

    /// <summary>
    /// Whether the card offers Windows' Startup apps list: the Store edition's startup task was turned off there, and
    /// only there can it be turned on again.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowOpenStartupApps { get; set; }

    /// <summary>
    /// Asks Windows for the startup task's state and shows it. Never throws (fire-and-forget safe): a state that cannot
    /// be read shows as unavailable.
    /// </summary>
    /// <returns>A task completing once the card shows the state.</returns>
    private async Task RefreshPackagedStartupAsync()
    {
        try
        {
            ApplyPackagedStartup(await PackagedStartup.GetStateAsync());
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Reading the startup task's state failed: {ex.Message}");
            ApplyPackagedStartup(PackagedStartupState.Unavailable);
        }
    }

    /// <summary>
    /// Turns the startup task on or off as the switch asks, then shows what Windows says it is now: a request Windows
    /// did not grant must not leave the switch saying something untrue. Never throws (fire-and-forget safe).
    /// </summary>
    /// <param name="enabled">What the switch asks for.</param>
    /// <returns>A task completing once the card shows the new state.</returns>
    private async Task SetPackagedStartupAsync(bool enabled)
    {
        try
        {
            var state = await PackagedStartup.SetEnabledAsync(enabled);
            AppLog.Info($"Start with Windows (startup task): asked for {(enabled ? "on" : "off")}, now {state}.");
            ApplyPackagedStartup(state);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Changing the startup task failed: {ex.Message}");
            await RefreshPackagedStartupAsync();
        }
    }

    /// <summary>Shows a startup task state in the switch, its description and the button.</summary>
    /// <param name="state">The state Windows reported.</param>
    private void ApplyPackagedStartup(PackagedStartupState state)
    {
        // As loading: setting the switch below must not be taken for the user flipping it.
        bool before = loading;
        loading = true;
        try
        {
            LaunchAtStartup = state is PackagedStartupState.Enabled or PackagedStartupState.EnabledByPolicy;
            CanChangeLaunchAtStartup = state is PackagedStartupState.Enabled or PackagedStartupState.Disabled;
            ShowOpenStartupApps = state == PackagedStartupState.DisabledByUser;
            LaunchAtStartupDescription = state switch
            {
                PackagedStartupState.DisabledByUser => UiText.Translate("Turned off in Windows' Startup apps (Settings › Apps › Startup). Turn it on there to start BetterClipboard when you sign in."),
                PackagedStartupState.DisabledByPolicy => UiText.Translate("Turned off by your organization."),
                PackagedStartupState.EnabledByPolicy => UiText.Translate("Turned on by your organization."),
                PackagedStartupState.Unavailable => UiText.Translate("Not available in this copy."),
                _ => UiText.Translate("Start quietly in the notification area when you sign in. Windows lists it under Settings › Apps › Startup."),
            };
        }
        finally
        {
            loading = before;
        }
    }
}
