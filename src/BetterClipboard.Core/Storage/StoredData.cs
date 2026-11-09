using BetterClipboard.Core.Everything;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Prompts;
using BetterClipboard.Core.Shells;

namespace BetterClipboard.Core.Storage;

/// <summary>
/// An integration whose stored data Settings can delete (<em>Delete stored…</em> on its card, CLAUDE.md §2.43). One per
/// Settings card of the Integrations section: the two Notepad++ cards are two sources.
/// </summary>
/// <remarks>Stored by name nowhere; the Settings cards name it in their buttons' <c>Tag</c>, so a rename breaks only XAML.</remarks>
public enum StoredSource
{
    /// <summary>ShareX screenshots: what the ShareX tab lists.</summary>
    ShareX,

    /// <summary>Snipping Tool and Win+PrtScn: what the Snipping tab lists.</summary>
    Snipping,

    /// <summary>Notepad++'s unsaved tabs: their recorded versions, and copies made in Notepad++.</summary>
    NotepadPlusPlusTabs,

    /// <summary>Files opened in Notepad++: their recorded paths, and copies made in Notepad++.</summary>
    NotepadPlusPlusFiles,

    /// <summary>The Everything tab's stored half, and the list of picks hidden from it.</summary>
    Everything,

    /// <summary>Win+R history: what the Run tab lists.</summary>
    RunHistory,

    /// <summary>The Pwsh tab's stored half, and the list of commands hidden from it.</summary>
    PowerShell,

    /// <summary>The Cmd tab: the commands kept from Command Prompt windows, its stored half, and its hidden list.</summary>
    Cmd,

    /// <summary>Claude Code: the prompt archive, prompts kept from the tab, and what Claude Code copied.</summary>
    ClaudeCode,

    /// <summary>Codex: the prompt archive and prompts kept from the tab.</summary>
    Codex,
}

/// <summary>What kind of data one part of an integration's stored data is, which decides how it is counted and deleted.</summary>
public enum StoredPartKind
{
    /// <summary>History entries (<see cref="StoredPart.Selections"/>): deleted and tombstoned, like a card's Delete.</summary>
    Entries,

    /// <summary>An agent's prompt archive (<see cref="StoredPart.Agent"/>): deleted like the card's old "Delete stored prompts".</summary>
    PromptArchive,

    /// <summary>The Cmd tab's list of commands kept from Command Prompt windows (the state <see cref="StoredPart.StateName"/>).</summary>
    KeptCommands,

    /// <summary>A tab's list of hidden picks or commands (the state <see cref="StoredPart.StateName"/>): emptied, so they show again.</summary>
    HiddenList,
}

/// <summary>Which entries of a filter a selection takes, by protection.</summary>
public enum StoredProtection
{
    /// <summary>Entries neither pinned nor in a group: what <em>Delete stored…</em> ticks by default.</summary>
    Unprotected,

    /// <summary>Pinned entries and entries in a group: kept unless the user ticks them.</summary>
    Protected,
}

/// <summary>
/// One slice of the history: the entries a tab lists (<paramref name="Filter"/>), narrowed by origin and protection. An
/// integration's stored entries are a few such slices, so its dialog can show where they came from.
/// </summary>
/// <param name="Filter">The tab whose entries these are (its SQL filter, the same the panel lists them with).</param>
/// <param name="Origins">
/// Only entries of these origins (or, with <paramref name="ExcludeOrigins"/>, of any other origin); <see langword="null"/>
/// for every origin.
/// </param>
/// <param name="ExcludeOrigins">Whether <paramref name="Origins"/> lists the origins to leave out instead.</param>
/// <param name="Protection">Unprotected entries, or pinned and grouped ones.</param>
public sealed record StoredSelection(
    ClipFilter Filter,
    IReadOnlyList<ClipOrigin>? Origins = null,
    bool ExcludeOrigins = false,
    StoredProtection Protection = StoredProtection.Unprotected);

/// <summary>
/// One part of what an integration stored, as <em>Delete stored…</em> shows it: a row with a checkbox, a count and
/// examples.
/// </summary>
/// <param name="Id">
/// Stable identifier ("sharex.saved", "protected"): the dialog maps it to a translated label, and the tests name parts by
/// it. Unique within a source.
/// </param>
/// <param name="Kind">What kind of data it is.</param>
/// <param name="SelectedByDefault">Whether its checkbox starts ticked (pinned or grouped entries and hidden lists do not).</param>
/// <param name="Selections">For <see cref="StoredPartKind.Entries"/>: the slices it is the union of; empty otherwise.</param>
/// <param name="StateName">For <see cref="StoredPartKind.KeptCommands"/> and <see cref="StoredPartKind.HiddenList"/>: the state it lives in.</param>
/// <param name="Agent">For <see cref="StoredPartKind.PromptArchive"/>: whose archive.</param>
public sealed record StoredPart(
    string Id,
    StoredPartKind Kind,
    bool SelectedByDefault,
    IReadOnlyList<StoredSelection> Selections,
    string? StateName = null,
    PromptAgent? Agent = null);

/// <summary>
/// What each integration stores, split into the parts its <em>Delete stored…</em> dialog shows (the owner's choices,
/// 2026-11-09: everything the integration's tab lists, pinned and grouped entries kept unless ticked, the source app's own
/// files never touched). Pure: the store counts and deletes the parts (<see cref="ClipStore.SummarizeStored"/>,
/// <see cref="ClipStore.DeleteStored"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything the tab lists.</b> Each source's entry parts together are exactly its tab's filter (Notepad++'s two cards
/// share the Notepad++ tab: each takes its own origin plus the copies made in Notepad++), so after deleting with the
/// default ticks the tab lists only pinned and grouped entries. The parts split the filter by origin where that tells the
/// user something: a screenshot ShareX saved versus what ShareX put on the clipboard.
/// </para>
/// <para>
/// <b>Last part: pinned or in a group.</b> Every source with entries ends with a <c>protected</c> part: the same slices,
/// pinned or grouped entries only, unticked.
/// </para>
/// </remarks>
public static class StoredData
{
    /// <summary>The id of every source's last entry part: its pinned and grouped entries.</summary>
    public const string ProtectedPartId = "protected";

    /// <summary>The origins the Win+R integration records with (a new run, and Windows' list at the first activation).</summary>
    private static readonly ClipOrigin[] RunOrigins = [ClipOrigin.RunDialog, ClipOrigin.RunDialogHistory];

    /// <summary>The two Notepad++ origins: an unsaved tab's version, and an opened file's path.</summary>
    private static readonly ClipOrigin[] NotepadPlusPlusOrigins = [ClipOrigin.NotepadPlusPlusTab, ClipOrigin.NotepadPlusPlusFile];

    /// <summary>Every source, in the order of their Settings cards.</summary>
    public static IReadOnlyList<StoredSource> Sources { get; } = Enum.GetValues<StoredSource>();

    /// <summary>
    /// The parts of what a source stores, in the order the dialog lists them: the integration's own records first, then
    /// copies made in its app, its own lists, and last the pinned or grouped entries.
    /// </summary>
    /// <param name="source">The integration.</param>
    /// <returns>Its parts (never empty).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="source"/> is not a defined value.</exception>
    public static IReadOnlyList<StoredPart> PartsOf(StoredSource source) => source switch
    {
        StoredSource.ShareX => WithProtected(
            Entries("sharex.saved", new StoredSelection(ClipFilter.ShareX, [ClipOrigin.ShareX])),
            Entries("sharex.copied", new StoredSelection(ClipFilter.ShareX, [ClipOrigin.ShareX], ExcludeOrigins: true))),
        StoredSource.Snipping => WithProtected(
            Entries("snipping.saved", new StoredSelection(ClipFilter.Snipping, [ClipOrigin.WindowsScreenshot])),
            Entries("snipping.copied", new StoredSelection(ClipFilter.Snipping, [ClipOrigin.WindowsScreenshot], ExcludeOrigins: true))),
        StoredSource.NotepadPlusPlusTabs => WithProtected(
            Entries("npp.tabs", new StoredSelection(ClipFilter.NotepadPlusPlus, [ClipOrigin.NotepadPlusPlusTab])),
            Entries("npp.copied", new StoredSelection(ClipFilter.NotepadPlusPlus, NotepadPlusPlusOrigins, ExcludeOrigins: true))),
        StoredSource.NotepadPlusPlusFiles => WithProtected(
            Entries("npp.files", new StoredSelection(ClipFilter.NotepadPlusPlus, [ClipOrigin.NotepadPlusPlusFile])),
            Entries("npp.copied", new StoredSelection(ClipFilter.NotepadPlusPlus, NotepadPlusPlusOrigins, ExcludeOrigins: true))),
        StoredSource.Everything => WithProtected(
            [Entries("everything.kept", new StoredSelection(ClipFilter.Everything, [ClipOrigin.Everything])),
             Entries("everything.copied", new StoredSelection(ClipFilter.Everything, [ClipOrigin.Everything], ExcludeOrigins: true))],
            [new StoredPart("everything.hidden", StoredPartKind.HiddenList, SelectedByDefault: false, [], StateName: EverythingTab.HiddenStateName)]),
        StoredSource.RunHistory => WithProtected(
            Entries("run.recorded", new StoredSelection(ClipFilter.Run, RunOrigins)),
            Entries("run.copied", new StoredSelection(ClipFilter.Run, RunOrigins, ExcludeOrigins: true))),
        StoredSource.PowerShell => WithProtected(
            [Entries("pwsh.kept", new StoredSelection(ClipFilter.PowerShell))],
            [new StoredPart("pwsh.hidden", StoredPartKind.HiddenList, SelectedByDefault: false, [], StateName: ShellTab.HiddenStateName(ShellKind.PowerShell))]),
        StoredSource.Cmd => WithProtected(
            [new StoredPart("cmd.list", StoredPartKind.KeptCommands, SelectedByDefault: true, [], StateName: CmdHistory.KeptStateName),
             Entries("cmd.kept", new StoredSelection(ClipFilter.Cmd))],
            [new StoredPart("cmd.hidden", StoredPartKind.HiddenList, SelectedByDefault: false, [], StateName: ShellTab.HiddenStateName(ShellKind.Cmd))]),
        StoredSource.ClaudeCode => WithProtected(
            [new StoredPart("claude.archive", StoredPartKind.PromptArchive, SelectedByDefault: true, [], Agent: PromptAgent.ClaudeCode),
             Entries("claude.kept", new StoredSelection(ClipFilter.ClaudeCode, [ClipOrigin.ClaudeCode])),
             Entries("claude.copied", new StoredSelection(ClipFilter.ClaudeCode, [ClipOrigin.ClaudeCode], ExcludeOrigins: true))],
            []),
        StoredSource.Codex => WithProtected(
            [new StoredPart("codex.archive", StoredPartKind.PromptArchive, SelectedByDefault: true, [], Agent: PromptAgent.Codex),
             Entries("codex.kept", new StoredSelection(ClipFilter.Codex))],
            []),
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Not a stored source."),
    };

    /// <summary>The tab whose stored entries a source holds (its parts' filter).</summary>
    /// <param name="source">The integration.</param>
    /// <returns>The tab's filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="source"/> is not a defined value.</exception>
    public static ClipFilter TabOf(StoredSource source) => source switch
    {
        StoredSource.ShareX => ClipFilter.ShareX,
        StoredSource.Snipping => ClipFilter.Snipping,
        StoredSource.NotepadPlusPlusTabs or StoredSource.NotepadPlusPlusFiles => ClipFilter.NotepadPlusPlus,
        StoredSource.Everything => ClipFilter.Everything,
        StoredSource.RunHistory => ClipFilter.Run,
        StoredSource.PowerShell => ClipFilter.PowerShell,
        StoredSource.Cmd => ClipFilter.Cmd,
        StoredSource.ClaudeCode => ClipFilter.ClaudeCode,
        StoredSource.Codex => ClipFilter.Codex,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Not a stored source."),
    };

    /// <summary>A ticked-by-default part of unprotected entries.</summary>
    /// <param name="id">The part's id.</param>
    /// <param name="selection">Its one slice (unprotected).</param>
    /// <returns>The part.</returns>
    private static StoredPart Entries(string id, StoredSelection selection) => new(id, StoredPartKind.Entries, SelectedByDefault: true, [selection]);

    /// <summary>The entry parts followed by their pinned-or-grouped part.</summary>
    /// <param name="parts">The source's entry parts.</param>
    /// <returns>The parts, then <see cref="ProtectedPartId"/>.</returns>
    private static IReadOnlyList<StoredPart> WithProtected(params StoredPart[] parts) => WithProtected(parts, []);

    /// <summary>
    /// The source's parts in dialog order: its own parts, the pinned-or-grouped part over every entry part's slices, then
    /// the lists that are not entries (hidden lists sit last, after the entries they belong with).
    /// </summary>
    /// <param name="parts">The source's parts before the protected one (entries, archives, kept commands).</param>
    /// <param name="after">Parts listed after the protected one (hidden lists).</param>
    /// <returns>The parts.</returns>
    private static IReadOnlyList<StoredPart> WithProtected(IReadOnlyList<StoredPart> parts, IReadOnlyList<StoredPart> after)
    {
        var protectedSlices = parts
            .Where(p => p.Kind == StoredPartKind.Entries)
            .SelectMany(p => p.Selections)
            .Select(s => s with { Protection = StoredProtection.Protected })
            .ToArray();
        var all = new List<StoredPart>(parts);
        if (protectedSlices.Length > 0)
        {
            all.Add(new StoredPart(ProtectedPartId, StoredPartKind.Entries, SelectedByDefault: false, protectedSlices));
        }

        all.AddRange(after);
        return all;
    }
}

/// <summary>One example of a part, as the dialog shows it: a line of text, or a picture by its entry's thumbnail.</summary>
/// <param name="Text">The line (an entry's preview's first line, a prompt, a command, a path); <see langword="null"/> for a picture.</param>
/// <param name="ImageEntryId">The entry whose stored thumbnail shows the picture; <see langword="null"/> for text.</param>
/// <param name="IsCode">Whether the line is a command, a path or code, shown in a monospace font like its card.</param>
public sealed record StoredExample(string? Text, long? ImageEntryId, bool IsCode);

/// <summary>How much of one part is stored, with its newest examples.</summary>
/// <param name="Part">The part.</param>
/// <param name="Count">How many items: entries, archived prompts (distinct texts), kept commands, hidden picks or commands.</param>
/// <param name="Bytes">The entries' total size; 0 for the parts that are not entries.</param>
/// <param name="Newest">When the newest item was last used, sent or seen; <see langword="null"/> when unknown.</param>
/// <param name="Oldest">When the oldest item was last used, sent or seen; <see langword="null"/> when unknown.</param>
/// <param name="Examples">The newest few items (none when the part holds only fingerprints, like a shell tab's hidden list).</param>
/// <param name="Sends">For an archive: every send, a prompt sent twice counting twice; <see langword="null"/> otherwise.</param>
public sealed record StoredPartPreview(
    StoredPart Part,
    long Count,
    long Bytes,
    DateTimeOffset? Newest,
    DateTimeOffset? Oldest,
    IReadOnlyList<StoredExample> Examples,
    long? Sends = null);

/// <summary>What a source stores now: its non-empty parts, for the <em>Delete stored…</em> dialog.</summary>
/// <param name="Source">The integration.</param>
/// <param name="Parts">Its parts that hold anything, in dialog order.</param>
public sealed record StoredPreview(StoredSource Source, IReadOnlyList<StoredPartPreview> Parts)
{
    /// <summary>Whether nothing is stored: the dialog only says so.</summary>
    public bool IsEmpty => Parts.Count == 0;
}

/// <summary>What a deletion of stored parts removed (content-free; what the log line says).</summary>
/// <param name="Entries">History entries deleted.</param>
/// <param name="Prompts">Archived prompt sends deleted (every send of every cleared archive).</param>
/// <param name="Lists">Lists emptied: kept commands, hidden picks or commands.</param>
public sealed record StoredDeletion(int Entries, int Prompts, int Lists);
