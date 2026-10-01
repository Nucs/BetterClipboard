using BetterClipboard.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// The Snipping tab's settings card (CLAUDE.md §2.18): the switch and its status line — the folder watched, what Snipping
/// Tool's own settings mean for it, and how many screenshots came in. Never names a file.
/// </summary>
public sealed partial class SettingsViewModel
{
    /// <summary>See <see cref="AppSettings.ImportWindowsScreenshots"/> (on by default).</summary>
    [ObservableProperty]
    public partial bool ImportWindowsScreenshots { get; set; }

    /// <summary>The folder watched (or waited for), Snipping Tool's saving, and the screenshots added this session.</summary>
    [ObservableProperty]
    public partial string SnippingStatus { get; set; } = string.Empty;

    /// <summary>
    /// Re-reads the integration's state. The folder is listed as watched only while really watched, so a folder that does
    /// not exist yet (Windows creates it with the first screenshot) shows as awaited.
    /// </summary>
    public void RefreshSnippingStatus()
    {
        if (!ImportWindowsScreenshots)
        {
            SnippingStatus = "Off — screenshot files are not imported, and the panel has no Snipping tab. Snipping Tool's clipboard copies are still recorded like any copy.";
            return;
        }

        var location = controller.SnippingLocation;
        var watched = controller.SnippingWatchedFolder;
        var folder = watched is not null ? $"Watching {watched}."
            : location.Folder is { } waiting ? $"Waiting for {waiting} — Windows creates it with the first screenshot."
            : "Looking for the Screenshots folder…";

        // What Snipping Tool's own switches mean here. Its snips reach the clipboard either way; only the file depends on them.
        var snippingTool = location.SnippingToolVersion is not { } version
            ? "Snipping Tool is not installed — Win+PrtScn screenshots still arrive."
            : location.SnippingTool is not { } saving
                ? $"Snipping Tool {version} — its settings could not be read right now (it may be running); its snips' clipboard copies arrive either way."
                : !saving.SavesScreenshots
                    ? $"Snipping Tool {version} does not save snips (its “Automatically save original screenshots” is off): they arrive as clipboard copies only."
                    : saving.HasCustomFolder
                        ? $"Snipping Tool {version} saves to a folder you chose, which is not watched: its snips arrive as clipboard copies; Win+PrtScn screenshots arrive from the Screenshots folder."
                        : $"Snipping Tool {version} saves every snip here, and copies it to the clipboard (both become one entry).";
        int imported = controller.SnippingImportedThisSession;
        var count = imported > 0 ? $"\n{imported:N0} screenshot{(imported == 1 ? string.Empty : "s")} added since BetterClipboard started." : string.Empty;
        SnippingStatus = $"{folder}\n{snippingTool}{count}";
    }

    /// <summary>Persists the switch; the tab and the status line follow the controller's status event.</summary>
    /// <param name="value">New value.</param>
    partial void OnImportWindowsScreenshotsChanged(bool value)
    {
        Update(s => s with { ImportWindowsScreenshots = value });
        RefreshSnippingStatus();
    }
}
