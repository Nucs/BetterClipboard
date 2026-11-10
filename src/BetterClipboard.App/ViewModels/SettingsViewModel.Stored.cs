using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// Which integration cards have anything stored, for their <em>Delete stored…</em> buttons (CLAUDE.md §2.43). One flag per
/// card, so XAML binds each button by name (<c>ViewModel.Stored.ShareX</c>); replaced as a whole when the counts change.
/// </summary>
/// <param name="ShareX">ShareX screenshots.</param>
/// <param name="Snipping">Snipping Tool and Win+PrtScn.</param>
/// <param name="NotepadPlusPlusTabs">Notepad++'s unsaved tabs.</param>
/// <param name="NotepadPlusPlusFiles">Files opened in Notepad++.</param>
/// <param name="Everything">The Everything tab.</param>
/// <param name="RunHistory">Win+R history.</param>
/// <param name="PowerShell">The Pwsh tab.</param>
/// <param name="Cmd">The Cmd tab.</param>
/// <param name="ClaudeCode">Claude Code prompts.</param>
/// <param name="Codex">Codex prompts.</param>
public sealed record StoredOverview(
    bool ShareX,
    bool Snipping,
    bool NotepadPlusPlusTabs,
    bool NotepadPlusPlusFiles,
    bool Everything,
    bool RunHistory,
    bool PowerShell,
    bool Cmd,
    bool ClaudeCode,
    bool Codex)
{
    /// <summary>Nothing known yet: every button disabled until the first count arrives (a fraction of a second).</summary>
    public static readonly StoredOverview None = new(false, false, false, false, false, false, false, false, false, false);

    /// <summary>Builds the flags from the history service's counts.</summary>
    /// <param name="counts">Items per source; a missing source counts as none.</param>
    /// <returns>The flags.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="counts"/> is <see langword="null"/>.</exception>
    public static StoredOverview From(IReadOnlyDictionary<StoredSource, long> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        bool Has(StoredSource source) => counts.TryGetValue(source, out long count) && count > 0;
        return new StoredOverview(
            Has(StoredSource.ShareX),
            Has(StoredSource.Snipping),
            Has(StoredSource.NotepadPlusPlusTabs),
            Has(StoredSource.NotepadPlusPlusFiles),
            Has(StoredSource.Everything),
            Has(StoredSource.RunHistory),
            Has(StoredSource.PowerShell),
            Has(StoredSource.Cmd),
            Has(StoredSource.ClaudeCode),
            Has(StoredSource.Codex));
    }
}

/// <summary>
/// Every integration card's <em>Delete stored…</em>: whether each has anything stored, and the switch "Also turn off" in
/// its dialog flips.
/// </summary>
public sealed partial class SettingsViewModel
{
    /// <summary>Which cards have anything stored (enables their buttons); refreshed by the window.</summary>
    [ObservableProperty]
    public partial StoredOverview Stored { get; set; } = StoredOverview.None;

    /// <summary>
    /// Counts what every integration stored (one pass over the history on the pool) and updates <see cref="Stored"/>. A
    /// failure is logged and leaves the buttons as they were.
    /// </summary>
    /// <returns>A task completing when updated; never throws.</returns>
    public async Task RefreshStoredAsync()
    {
        try
        {
            Stored = StoredOverview.From(await controller.CountStoredAsync());
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Counting what the integrations stored failed: {ex.Message}");
        }
    }

    /// <summary>Whether a source's switch is on: the dialog offers "Also turn off" only then.</summary>
    /// <param name="source">The integration.</param>
    /// <returns><see langword="true"/> while it records (or, for the Everything and Pwsh tabs, while the tab is on).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="source"/> is not a defined value.</exception>
    public bool IsSwitchOn(StoredSource source) => source switch
    {
        StoredSource.ShareX => ImportShareXScreenshots,
        StoredSource.Snipping => ImportWindowsScreenshots,
        StoredSource.NotepadPlusPlusTabs => KeepNotepadPlusPlusTabs,
        StoredSource.NotepadPlusPlusFiles => RecordNotepadPlusPlusFiles,
        StoredSource.Everything => ShowEverythingTab,
        StoredSource.RunHistory => RecordRunHistory,
        StoredSource.PowerShell => ShowPowerShellTab,
        StoredSource.Cmd => ShowCmdTab,
        StoredSource.ClaudeCode => KeepClaudeCodePrompts,
        StoredSource.Codex => KeepCodexPrompts,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Not a stored source."),
    };

    /// <summary>
    /// "Also turn off": flips the source's switch off exactly as the card's switch would (saved at once; the integration
    /// stops through the settings change), before the deletion runs, so nothing is recorded again meanwhile.
    /// </summary>
    /// <param name="source">The integration.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="source"/> is not a defined value.</exception>
    public void TurnOff(StoredSource source)
    {
        switch (source)
        {
            case StoredSource.ShareX: ImportShareXScreenshots = false; break;
            case StoredSource.Snipping: ImportWindowsScreenshots = false; break;
            case StoredSource.NotepadPlusPlusTabs: KeepNotepadPlusPlusTabs = false; break;
            case StoredSource.NotepadPlusPlusFiles: RecordNotepadPlusPlusFiles = false; break;
            case StoredSource.Everything: ShowEverythingTab = false; break;
            case StoredSource.RunHistory: RecordRunHistory = false; break;
            case StoredSource.PowerShell: ShowPowerShellTab = false; break;
            case StoredSource.Cmd: ShowCmdTab = false; break;
            case StoredSource.ClaudeCode: KeepClaudeCodePrompts = false; break;
            case StoredSource.Codex: KeepCodexPrompts = false; break;
            default: throw new ArgumentOutOfRangeException(nameof(source), source, "Not a stored source.");
        }
    }
}
