using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Settings;
using BetterClipboard.Windows.Integrations;

namespace BetterClipboard.App;

/// <summary>
/// The Notepad++ tab's half of the controller (CLAUDE.md §2.41): the text of Notepad++'s unsaved "new" tabs while
/// <see cref="AppSettings.KeepNotepadPlusPlusTabs"/> is on, and the path of every file opened in Notepad++ while
/// <see cref="AppSettings.RecordNotepadPlusPlusFiles"/> is on.
/// </summary>
/// <remarks>
/// The tabs come from Notepad++'s own files (read once a tab rests); the opened files from asking a running, signed
/// Notepad++ through its plugin messages. Nothing in Notepad++ changes.
/// </remarks>
public sealed partial class AppController
{
    /// <summary>The Notepad++ integration (created in <see cref="Start"/>, disposed on exit).</summary>
    private NotepadPlusPlusIntegration? notepadPlusPlus;

    /// <summary>
    /// Raised on the UI thread when what the integration found or did changed, or the history gained its first entry for the
    /// Notepad++ tab or lost its last (the flyout shows or hides its Notepad++ tab; Settings refreshes its status lines).
    /// </summary>
    public event EventHandler? NotepadPlusPlusStatusChanged;

    /// <summary>
    /// Whether the panel shows its Notepad++ tab: one of the two settings is on and the history holds at least one entry the
    /// tab lists (<see cref="Core.Services.ClipHistoryService.HasNotepadPlusPlusEntries"/>: a tab's text, an opened file, or a
    /// copy made in Notepad++) — an empty tab would only take room in the strip.
    /// </summary>
    public bool IsNotepadPlusPlusTabAvailable =>
        settings?.Current is { } current && (current.KeepNotepadPlusPlusTabs || current.RecordNotepadPlusPlusFiles) && history?.HasNotepadPlusPlusEntries == true;

    /// <summary>What was found about Notepad++ (<see cref="NotepadPlusPlusLocation.None"/> until located).</summary>
    public NotepadPlusPlusLocation NotepadPlusPlusLocation => notepadPlusPlus?.Location ?? NotepadPlusPlusLocation.None;

    /// <summary>Whether Notepad++'s tabs are being followed.</summary>
    public bool IsNotepadPlusPlusTabsWatching => notepadPlusPlus?.IsWatchingTabs == true;

    /// <summary>How many unsaved tabs Notepad++ remembers (0 while not following).</summary>
    public int NotepadPlusPlusTabCount => notepadPlusPlus?.TabCount ?? 0;

    /// <summary>Tab versions recorded since the app started.</summary>
    public int NotepadPlusPlusTabsRecordedThisSession => notepadPlusPlus?.TabsRecordedThisSession ?? 0;

    /// <summary>The running Notepad++ windows the opened-files half knows.</summary>
    public IReadOnlyList<NotepadPlusPlusInstance> NotepadPlusPlusInstances => notepadPlusPlus?.Instances ?? [];

    /// <summary>Files recorded as opened since the app started.</summary>
    public int NotepadPlusPlusFilesRecordedThisSession => notepadPlusPlus?.FilesRecordedThisSession ?? 0;

    /// <summary>
    /// Creates the integration and starts it with the current settings (located on the pool; the tabs' catch-up runs in the
    /// background). A failure is logged: capture, paste and the other tabs keep working without it.
    /// </summary>
    private void StartNotepadPlusPlus()
    {
        notepadPlusPlus = new NotepadPlusPlusIntegration(History, () => Settings.Current.MaxItemSizeMB * 1024L * 1024L);
        notepadPlusPlus.StatusChanged += (_, _) => ui.TryEnqueue(() => NotepadPlusPlusStatusChanged?.Invoke(this, EventArgs.Empty));
        _ = StartNotepadPlusPlusAsync(notepadPlusPlus, Settings.Current);
    }

    /// <summary>Starts the integration without letting a failure escape into the fire-and-forget caller.</summary>
    /// <param name="integration">The integration.</param>
    /// <param name="current">The settings at startup.</param>
    /// <returns>A task completing once started (or failed, logged).</returns>
    private static async Task StartNotepadPlusPlusAsync(NotepadPlusPlusIntegration integration, AppSettings current)
    {
        try
        {
            await integration.StartAsync(current.KeepNotepadPlusPlusTabs, current.RecordNotepadPlusPlusFiles);
        }
        catch (Exception ex)
        {
            AppLog.Error("Starting the Notepad++ integration failed.", ex);
        }
    }

    /// <summary>Follows changed settings (idempotent) and tells the tab, which follows the settings at once.</summary>
    /// <param name="next">The new settings.</param>
    /// <returns>A task completing once applied.</returns>
    private async Task ApplyNotepadPlusPlusSettingsAsync(AppSettings next)
    {
        if (notepadPlusPlus is not null)
        {
            await notepadPlusPlus.SetEnabledAsync(next.KeepNotepadPlusPlusTabs, next.RecordNotepadPlusPlusFiles);
        }

        NotepadPlusPlusStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Locates Notepad++ again (Settings opened: a Notepad++ installed or started meanwhile shows at once).</summary>
    private void RefreshNotepadPlusPlus() => _ = notepadPlusPlus?.RefreshAsync();

    /// <summary>Stops both halves before the history closes: a version must only be recorded while the history runs.</summary>
    /// <returns>A task completing once stopped.</returns>
    private async Task DisposeNotepadPlusPlusAsync()
    {
        if (notepadPlusPlus is not null)
        {
            await notepadPlusPlus.DisposeAsync();
        }
    }
}
