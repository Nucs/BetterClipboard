using BetterClipboard.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// The integrations' limits in their Settings cards (CLAUDE.md §2.62): one number per limit, 0 = unlimited, the default
/// (<see cref="ListLimit"/>). ShareX's and the Snipping tab's catch-up, the Everything tab's hidden files, the Pwsh tab's
/// hidden commands, and the Cmd tab's kept and hidden commands. Each number is a <c>NumberBox</c> value, hence
/// <see cref="double"/>; a box cleared by hand reports NaN, which keeps the saved value.
/// </summary>
public sealed partial class SettingsViewModel
{
    /// <summary>See <see cref="AppSettings.MaxShareXCatchUp"/>: most screenshots ShareX's catch-up imports (0 = all).</summary>
    [ObservableProperty]
    public partial double MaxShareXCatchUp { get; set; }

    /// <summary>See <see cref="AppSettings.MaxScreenshotsCatchUp"/>: most screenshots the Screenshots folder's catch-up imports (0 = all).</summary>
    [ObservableProperty]
    public partial double MaxScreenshotsCatchUp { get; set; }

    /// <summary>See <see cref="AppSettings.MaxHiddenEverythingPicks"/>: most files the Everything tab keeps hidden (0 = no limit).</summary>
    [ObservableProperty]
    public partial double MaxHiddenEverythingPicks { get; set; }

    /// <summary>See <see cref="AppSettings.MaxHiddenPowerShellCommands"/>: most commands the Pwsh tab keeps hidden (0 = no limit).</summary>
    [ObservableProperty]
    public partial double MaxHiddenPowerShellCommands { get; set; }

    /// <summary>See <see cref="AppSettings.MaxKeptCmdCommands"/>: most commands the Cmd tab keeps (0 = no limit).</summary>
    [ObservableProperty]
    public partial double MaxKeptCmdCommands { get; set; }

    /// <summary>See <see cref="AppSettings.MaxHiddenCmdCommands"/>: most commands the Cmd tab keeps hidden (0 = no limit).</summary>
    [ObservableProperty]
    public partial double MaxHiddenCmdCommands { get; set; }

    /// <summary>Copies the limits of a settings snapshot into the boxes (called by <see cref="Load"/>, which mutes saving).</summary>
    /// <param name="settings">The snapshot.</param>
    private void LoadListLimits(AppSettings settings)
    {
        MaxShareXCatchUp = settings.MaxShareXCatchUp;
        MaxScreenshotsCatchUp = settings.MaxScreenshotsCatchUp;
        MaxHiddenEverythingPicks = settings.MaxHiddenEverythingPicks;
        MaxHiddenPowerShellCommands = settings.MaxHiddenPowerShellCommands;
        MaxKeptCmdCommands = settings.MaxKeptCmdCommands;
        MaxHiddenCmdCommands = settings.MaxHiddenCmdCommands;
    }

    /// <summary>
    /// A box's value as a stored limit: <see langword="null"/> for NaN (the box cleared by hand: the saved value stays),
    /// else rounded to a whole number within 0..<see cref="ListLimit.Maximum"/>.
    /// </summary>
    /// <param name="value">The box's value.</param>
    /// <returns>The limit to store, or <see langword="null"/> to store nothing.</returns>
    private static int? LimitOf(double value) =>
        double.IsNaN(value) ? null : (int)Math.Clamp(Math.Round(value), 0, ListLimit.Maximum);

    /// <summary>Persists the change; the next catch-up of ShareX's folders follows it.</summary>
    /// <param name="value">New value.</param>
    partial void OnMaxShareXCatchUpChanged(double value)
    {
        if (LimitOf(value) is { } limit)
        {
            Update(s => s with { MaxShareXCatchUp = limit });
        }
    }

    /// <summary>Persists the change; the next catch-up of the Screenshots folder follows it.</summary>
    /// <param name="value">New value.</param>
    partial void OnMaxScreenshotsCatchUpChanged(double value)
    {
        if (LimitOf(value) is { } limit)
        {
            Update(s => s with { MaxScreenshotsCatchUp = limit });
        }
    }

    /// <summary>Persists the change; a lowered number shows the oldest hidden files again at once (<c>AppController.Limits.cs</c>).</summary>
    /// <param name="value">New value.</param>
    partial void OnMaxHiddenEverythingPicksChanged(double value)
    {
        if (LimitOf(value) is { } limit)
        {
            Update(s => s with { MaxHiddenEverythingPicks = limit });
        }
    }

    /// <summary>Persists the change; a lowered number shows the oldest hidden commands again at once.</summary>
    /// <param name="value">New value.</param>
    partial void OnMaxHiddenPowerShellCommandsChanged(double value)
    {
        if (LimitOf(value) is { } limit)
        {
            Update(s => s with { MaxHiddenPowerShellCommands = limit });
        }
    }

    /// <summary>
    /// Persists the change; a lowered number deletes the commands seen longest ago at once. The card's count line names the
    /// limit, so it is worded again here (a raised limit trims nothing, and so raises no status event of its own).
    /// </summary>
    /// <param name="value">New value.</param>
    partial void OnMaxKeptCmdCommandsChanged(double value)
    {
        if (LimitOf(value) is { } limit)
        {
            Update(s => s with { MaxKeptCmdCommands = limit });
            if (!loading)
            {
                RefreshShellHistoryStatus();
            }
        }
    }

    /// <summary>Persists the change; a lowered number shows the oldest hidden commands again at once.</summary>
    /// <param name="value">New value.</param>
    partial void OnMaxHiddenCmdCommandsChanged(double value)
    {
        if (LimitOf(value) is { } limit)
        {
            Update(s => s with { MaxHiddenCmdCommands = limit });
        }
    }
}
