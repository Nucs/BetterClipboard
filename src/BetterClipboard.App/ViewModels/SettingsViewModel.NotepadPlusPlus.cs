using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Settings;
using BetterClipboard.Windows.Integrations;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// The two Notepad++ cards (CLAUDE.md §2.41): the switches and their status lines — which Notepad++ was found, how many
/// unsaved tabs it remembers, and whether a running Notepad++ can be asked which files it opens. Never names a tab or a file.
/// </summary>
public sealed partial class SettingsViewModel
{
    /// <summary>See <see cref="AppSettings.KeepNotepadPlusPlusTabs"/> (on by default).</summary>
    [ObservableProperty]
    public partial bool KeepNotepadPlusPlusTabs { get; set; }

    /// <summary>See <see cref="AppSettings.RecordNotepadPlusPlusFiles"/> (on by default).</summary>
    [ObservableProperty]
    public partial bool RecordNotepadPlusPlusFiles { get; set; }

    /// <summary>The unsaved tabs' status: Notepad++ found or not, how many tabs it remembers, what was recorded.</summary>
    [ObservableProperty]
    public partial string NotepadPlusPlusTabsStatus { get; set; } = string.Empty;

    /// <summary>The opened files' status: the running Notepad++ windows, whether they can be asked, what was recorded.</summary>
    [ObservableProperty]
    public partial string NotepadPlusPlusFilesStatus { get; set; } = string.Empty;

    /// <summary>Re-reads the integration's state for both status lines.</summary>
    public void RefreshNotepadPlusPlusStatus()
    {
        var location = controller.NotepadPlusPlusLocation;
        string found = location.IsInstalled
            ? UiText.Format($"Notepad++ {location.Version ?? UiText.Translate("(version unknown)")} found.")
            : UiText.Translate("Notepad++ is not installed.");

        if (!KeepNotepadPlusPlusTabs)
        {
            NotepadPlusPlusTabsStatus = UiText.Translate("Off — Notepad++'s unsaved tabs are not read. Copies made in Notepad++ are still recorded like any copy.");
        }
        else if (!location.IsInstalled)
        {
            NotepadPlusPlusTabsStatus = found;
        }
        else if (!location.Config.KeepsUnsavedTabs)
        {
            // Without Notepad++'s session snapshot there are no backup files, so nothing to read.
            NotepadPlusPlusTabsStatus = $"{found}\n" + UiText.Translate("It keeps no unsaved tabs: in Notepad++, Settings › Preferences › Backup › “Enable session snapshot and periodic backup” is off.");
        }
        else
        {
            int tabs = controller.NotepadPlusPlusTabCount;
            int recorded = controller.NotepadPlusPlusTabsRecordedThisSession;

            // Counts are plural strings, one form per CLDR category of the interface language (CLAUDE.md §2.37).
            string watching = controller.IsNotepadPlusPlusTabsWatching
                ? UiText.Plural(
                    tabs,
                    $"It remembers {tabs:N0} unsaved tab; a tab is read once it has rested for a minute.",
                    $"It remembers {tabs:N0} unsaved tabs; a tab is read once it has rested for a minute.")
                : UiText.Translate("Looking for its settings folder…");
            string count = recorded > 0
                ? "\n" + UiText.Plural(
                    recorded,
                    $"{recorded:N0} tab version recorded since BetterClipboard started.",
                    $"{recorded:N0} tab versions recorded since BetterClipboard started.")
                : string.Empty;
            NotepadPlusPlusTabsStatus = $"{found}\n{watching}{count}";
        }

        if (!RecordNotepadPlusPlusFiles)
        {
            NotepadPlusPlusFilesStatus = UiText.Translate("Off — files opened in Notepad++ are not recorded.");
            return;
        }

        var instances = controller.NotepadPlusPlusInstances;
        string state;
        if (instances.Count == 0)
        {
            state = UiText.Translate("Notepad++ is not running; files are recorded while it runs.");
        }
        else if (instances.FirstOrDefault(i => i.Problem is not null) is { Problem: { } problem } && instances.All(i => i.Problem is not null))
        {
            // The reason comes from the owner check or Windows (administrator), worded in English for the log; it is
            // translated back through its template list, so no English sits inside a translated sentence. Content-free.
            string reason = UiText.TranslateFormatted(problem, NotepadPlusPlusClient.ProblemTemplates);
            state = UiText.Format($"Notepad++ cannot be asked which files it opens: {reason}.");
        }
        else
        {
            // Files, not documents: Notepad++'s own count includes untitled tabs and a hidden placeholder document.
            int open = instances.Sum(i => i.OpenFiles);
            state = UiText.Plural(open, $"Watching Notepad++ ({open:N0} file open).", $"Watching Notepad++ ({open:N0} files open).");
        }

        int files = controller.NotepadPlusPlusFilesRecordedThisSession;
        NotepadPlusPlusFilesStatus = files > 0
            ? state + "\n" + UiText.Plural(files, $"{files:N0} file recorded since BetterClipboard started.", $"{files:N0} files recorded since BetterClipboard started.")
            : state;
    }

    /// <summary>Persists the tabs switch; the tab and the status lines follow the controller's status event.</summary>
    /// <param name="value">New value.</param>
    partial void OnKeepNotepadPlusPlusTabsChanged(bool value)
    {
        Update(s => s with { KeepNotepadPlusPlusTabs = value });
        RefreshNotepadPlusPlusStatus();
    }

    /// <summary>Persists the opened-files switch; the tab and the status lines follow the controller's status event.</summary>
    /// <param name="value">New value.</param>
    partial void OnRecordNotepadPlusPlusFilesChanged(bool value)
    {
        Update(s => s with { RecordNotepadPlusPlusFiles = value });
        RefreshNotepadPlusPlusStatus();
    }
}
