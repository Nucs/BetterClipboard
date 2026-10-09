using System.Text.Json.Serialization;

namespace BetterClipboard.Core.Settings;

/// <summary>Where the flyout appears when summoned.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FlyoutPlacement>))]
public enum FlyoutPlacement
{
    /// <summary>Next to the text caret like Win+V; falls back to the mouse cursor when the app exposes no caret.</summary>
    NearCaret = 0,

    /// <summary>Next to the mouse cursor.</summary>
    NearCursor = 1,

    /// <summary>Centered on the monitor that holds the foreground window.</summary>
    CenterScreen = 2,
}

/// <summary>Light/dark choice for BetterClipboard's own windows.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
public enum AppTheme
{
    /// <summary>Follow the Windows app theme.</summary>
    System = 0,

    /// <summary>Always light.</summary>
    Light = 1,

    /// <summary>Always dark.</summary>
    Dark = 2,
}

/// <summary>
/// User settings, persisted as JSON by <see cref="SettingsStore"/>. Immutable: change settings with
/// <c>with</c> expressions through <see cref="SettingsStore.Update"/> so every reader sees a consistent snapshot.
/// </summary>
/// <remarks>
/// Adding a property is backward compatible (missing JSON members keep their defaults); renaming or
/// removing one silently resets that setting for existing users — avoid it, or migrate in
/// <see cref="Normalize"/> using <see cref="SchemaVersion"/>.
/// </remarks>
public sealed record AppSettings
{
    /// <summary>
    /// The panel's width when nothing was resized yet, in DIPs (Win+V is ~360×450; a little larger shows two more cards).
    /// The outer window's width, frame included, without the groups column.
    /// </summary>
    public const int DefaultFlyoutWidth = 400;

    /// <summary>The panel's height when nothing was resized yet, in DIPs (the outer window's, frame included).</summary>
    public const int DefaultFlyoutHeight = 560;

    /// <summary>
    /// The narrowest the user can make the panel, in DIPs (without the groups column): the header's logo, "Clipboard", the
    /// "Paused" chip and its four buttons still fit side by side, and the search box keeps room for text left of its
    /// Aa / W / .* toggles. Narrower, the header's title would be cut off. Enforced while resizing and when loading.
    /// </summary>
    public const int MinFlyoutWidth = 360;

    /// <summary>
    /// The lowest the user can make the panel, in DIPs: header, search box, tabs and footer take ~180 of them, so the list
    /// keeps room for about two cards. Enforced while resizing and when loading.
    /// </summary>
    public const int MinFlyoutHeight = 320;

    /// <summary>
    /// The largest size kept, in DIPs, either side: a sanity bound for hand-edited files, not a layout rule — the panel is
    /// shrunk to the monitor's work area when it is shown anyway (a size dragged across two monitors is kept, but shown
    /// at most as large as the monitor it opens on).
    /// </summary>
    public const int MaxFlyoutSize = 8192;

    /// <summary>
    /// The most shortcuts that open the panel (<see cref="OpenHotkey"/> plus <see cref="ExtraOpenHotkeys"/>): a sanity bound
    /// for hand-edited files and installer arguments. Each shortcut costs one registration, and in the worst case one more
    /// key the low-level keyboard hook checks on every keystroke.
    /// </summary>
    public const int MaxOpenHotkeys = 8;

    /// <summary>
    /// The shortcut the installer gives BetterClipboard when Win+V stays with Windows (<c>install.ps1 -NoTakeOverWinV</c>
    /// without <c>-Hotkey</c>). Free on a stock Windows 11, and unlike Ctrl+Alt+V (Office's Paste Special) or Ctrl+Shift+V
    /// (paste as plain text in browsers and editors) no app expects it.
    /// </summary>
    public const string AlternativeOpenHotkey = "Alt+Win+V";

    /// <summary>
    /// The most shortcuts <see cref="HotkeyHistory"/> keeps: room for the current ones (at most <see cref="MaxOpenHotkeys"/>)
    /// and as many again used before, which still fits the presets menu on one screen.
    /// </summary>
    public const int MaxHotkeyHistory = 16;

    /// <summary>
    /// The tab the panel opens in until the user chose one, and whenever the remembered one cannot be shown: "All", the
    /// whole history — what Win+V itself shows.
    /// </summary>
    public const string DefaultTab = nameof(Storage.ClipFilter.All);

    /// <summary>Settings file format version, for future migrations.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// The main global shortcut that opens the flyout, e.g. <c>Win+V</c> or <c>Ctrl+Shift+V</c>: the first of
    /// <see cref="OpenHotkeys"/>, and the one the tray menu and tooltip name.
    /// </summary>
    /// <remarks>
    /// Kept as its own member (rather than folded into one list) so a settings file stays readable by versions from
    /// before <see cref="ExtraOpenHotkeys"/>: they open the panel with this shortcut alone.
    /// </remarks>
    public string OpenHotkey { get; init; } = "Win+V";

    /// <summary>
    /// More global shortcuts that open the flyout besides <see cref="OpenHotkey"/>, e.g. <c>Alt+Win+V</c> next to Win+V,
    /// or a second key for a keyboard without a Windows key. Empty by default.
    /// </summary>
    /// <remarks>
    /// Each one is taken over like the main shortcut: registered directly, or intercepted with the keyboard hook while
    /// another app or Windows owns it (<see cref="UseKeyboardHookFallback"/>). Entries are the canonical text the
    /// settings page and the installer save (<c>Ctrl+Alt+Shift+Win+Key</c>); invalid ones are skipped with a warning when
    /// the shortcuts are applied. Footgun: a version from before this member drops it when it saves the file, so going
    /// back to such a version and forward again leaves only <see cref="OpenHotkey"/>.
    /// </remarks>
    public IReadOnlyList<string> ExtraOpenHotkeys { get; init; } = [];

    /// <summary>
    /// Every shortcut that opens the flyout: <see cref="OpenHotkey"/> first, then <see cref="ExtraOpenHotkeys"/>. Derived,
    /// so it is never written to the file (an older version would otherwise see a member it does not know).
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> OpenHotkeys => [OpenHotkey, .. ExtraOpenHotkeys ?? []];

    /// <summary>
    /// Every shortcut that has opened the panel, most recently added first: the "Used before" part of the menu next to
    /// Settings' shortcut box, so a shortcut used once stays one click away after it was removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Always holds the current shortcuts: <see cref="Normalize"/> puts a missing one at the front (that is also how a file
    /// from before this member starts its history), and <see cref="WithOpenHotkeys"/> moves newly added ones to the front.
    /// Removing a shortcut keeps it here. Capped at <see cref="MaxHotkeyHistory"/> by dropping the oldest entry that is not a
    /// current shortcut.
    /// </para>
    /// <para>
    /// Entries are the canonical texts the settings page saves; blanks and repeats (by text, ignoring case and spaces) are
    /// dropped. The menu skips entries that are presets (they are listed above it) or no longer valid shortcuts, so a
    /// hand-edited entry costs nothing. Footgun: a version from before this member drops it when it saves the file; the next
    /// newer version then starts over from the current shortcuts.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> HotkeyHistory { get; init; } = [];

    /// <summary>
    /// When the shortcut is already owned by Windows (Win+V is, by Explorer), intercept it with a
    /// low-level keyboard hook instead of giving up. The hook only swallows the exact combination.
    /// </summary>
    public bool UseKeyboardHookFallback { get; init; } = true;

    /// <summary>Maximum entries kept apart from pinned and grouped ones (most recently used win); 0 = unlimited.</summary>
    public int MaxItems { get; init; } = 10_000;

    /// <summary>Drop entries not used for this many days (pinned and grouped ones never); 0 = keep forever.</summary>
    public int RetentionDays { get; init; }

    /// <summary>Largest single copy recorded, in MB (Win+V caps at 4 MB).</summary>
    public int MaxItemSizeMB { get; init; } = 64;

    /// <summary>Total payload budget for history apart from pinned and grouped entries, in MB; 0 = unlimited.</summary>
    public int MaxTotalSizeMB { get; init; } = 4096;

    /// <summary>Record image-only copies (screenshots etc.).</summary>
    public bool CaptureImages { get; init; } = true;

    /// <summary>Record file lists copied in Explorer (paths only, not file contents).</summary>
    public bool CaptureFiles { get; init; } = true;

    /// <summary>
    /// Also keep app-private formats (Office, IDEs, design tools) for highest paste fidelity. Costs a lot
    /// more disk space (Excel copies carry many formats), hence off by default.
    /// </summary>
    public bool PreserveAllFormats { get; init; }

    /// <summary>After choosing an item, paste it into the previously focused app (otherwise only copy it).</summary>
    public bool PasteOnSelect { get; init; } = true;

    /// <summary>Move an item to the top of history when it is pasted from the flyout.</summary>
    public bool MoveToTopOnPaste { get; init; } = true;

    /// <summary>Show pinned items above everything else.</summary>
    public bool PinnedOnTop { get; init; } = true;

    /// <summary>
    /// Whether the panel shows its groups column (the bookmark button in the header toggles it; the panel
    /// then grows to the left). Remembered so the column is there again on the next Win+V.
    /// </summary>
    public bool ShowGroupsPane { get; init; }

    /// <summary>
    /// The panel's width the user last dragged it to, in DIPs (device-independent pixels, 1/96 inch): the outer window,
    /// frame included, without the groups column — which adds its own width on the left while it is open, so opening or
    /// closing the column never changes this.
    /// </summary>
    /// <remarks>
    /// Saved once when a resize ends (never during the drag), and only for a resize the user made: the panel moving to a
    /// monitor with another scale changes its pixels, not this. Every summon opens the panel at this size next to the
    /// caret, shrunk to the monitor's work area when larger (the shrunk size is not saved). Kept in
    /// <see cref="MinFlyoutWidth"/>..<see cref="MaxFlyoutSize"/>; resizing the panel wider shows more of the filter tabs,
    /// which scroll sideways when they do not all fit.
    /// </remarks>
    public int FlyoutWidth { get; init; } = DefaultFlyoutWidth;

    /// <summary>
    /// The panel's height the user last dragged it to, in DIPs: the outer window, frame included. Saved and applied like
    /// <see cref="FlyoutWidth"/>, kept in <see cref="MinFlyoutHeight"/>..<see cref="MaxFlyoutSize"/>.
    /// </summary>
    public int FlyoutHeight { get; init; } = DefaultFlyoutHeight;

    /// <summary>
    /// The panel's tab (All, Pinned, Text, …) the user last chose, as the name of its <see cref="Storage.ClipFilter"/>:
    /// every Win+V opens the panel in it, also after a restart. <see cref="DefaultTab"/> until a tab was chosen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only a choice changes it: a click on a tab, the Left and Right keys, a screen reader's select. A tab that is not
    /// there when the panel opens (ShareX was uninstalled, its setting is off, another app's tab is not ready yet) makes
    /// that summon start in All <i>without</i> changing this, so the panel is back in the remembered tab once it returns.
    /// Saved when the panel closes, not on every tab change: stepping through the tabs with the arrow keys must not write
    /// the file a dozen times.
    /// </para>
    /// <para>
    /// <b>A name, read leniently, on purpose</b> (<see cref="ParseTab"/>): an enum member written by name makes the
    /// whole file unreadable for a version that does not know the name (the JSON reader throws, and the file is set aside
    /// as corrupt), and written as a number it would silently mean another tab if the tabs were ever renumbered. An
    /// unknown name here only costs the remembered tab. Footgun: a version from before this member drops it when it saves
    /// the file; the next newer version then starts in All again.
    /// </para>
    /// </remarks>
    public string LastTab { get; init; } = DefaultTab;

    /// <summary>
    /// <see cref="LastTab"/> as a filter: what the panel selects when it opens. Derived, so it is never written to the file.
    /// </summary>
    [JsonIgnore]
    public Storage.ClipFilter LastTabFilter => ParseTab(LastTab);

    /// <summary>
    /// The panel search box's "Aa" toggle: words match only with the same upper and lower case
    /// (<see cref="Storage.SearchOptions.MatchCase"/>). Off by default.
    /// </summary>
    /// <remarks>
    /// The three search toggles are remembered, like an IDE's find box, while the search text itself is cleared on
    /// every Win+V: they describe how the user likes to search, not one search. The flip side: a toggle left on keeps
    /// changing what later searches find — the box shows it highlighted, and the empty state names it.
    /// </remarks>
    public bool SearchMatchCase { get; init; }

    /// <summary>
    /// The panel search box's "W" toggle: each word must stand alone (<see cref="Storage.SearchOptions.WholeWord"/>).
    /// Off by default; remembered (see <see cref="SearchMatchCase"/>).
    /// </summary>
    public bool SearchWholeWord { get; init; }

    /// <summary>
    /// The panel search box's ".*" toggle: the search text is a regular expression (<see cref="Storage.SearchOptions.Regex"/>).
    /// Off by default; remembered (see <see cref="SearchMatchCase"/>).
    /// </summary>
    public bool SearchUseRegex { get; init; }

    /// <summary>Import Windows' current Win+V history (including its pinned items) at every startup.</summary>
    public bool ImportWindowsHistoryOnStartup { get; init; } = true;

    /// <summary>Temporarily stop recording live copies.</summary>
    public bool IsCapturePaused { get; init; }

    /// <summary>
    /// Add every screenshot ShareX saves to history the moment the file is written (only when ShareX is
    /// installed; its tab in the panel appears either way).
    /// </summary>
    /// <remarks>
    /// On by default. It changes nothing when ShareX is absent, and when present it is what a ShareX user
    /// expects. Screenshots ShareX only uploads or only copies to the clipboard are not files. The
    /// clipboard copies arrive through normal capture anyway, and pause, ignored apps (add "ShareX") and
    /// size limits apply to both paths.
    /// </remarks>
    public bool ImportShareXScreenshots { get; init; } = true;

    /// <summary>
    /// Add every screenshot Windows' own tools save — Snipping Tool's auto-save (Win+Shift+S, PrtScn) and Win+PrtScn —
    /// to history the moment the file is written into the Screenshots folder, and show the panel's Snipping tab.
    /// </summary>
    /// <remarks>
    /// On by default, like ShareX's: the files are on disk anyway, and Snipping Tool already copies every snip to the
    /// clipboard, so the history records snips either way — this adds the files' screenshots (Win+PrtScn's, and snips
    /// taken while BetterClipboard was not running, through the startup catch-up), the tools' names, and the tab. Only
    /// the Screenshots folder is watched, never written. Off stops watching and forgets the catch-up marker, so on
    /// again does not import what was saved meanwhile. Pause, ignored apps ("SnippingTool", "Win+PrtScn") and the size
    /// limit apply; the tab keeps showing Snipping Tool's clipboard copies only while this is on (it is hidden when off).
    /// </remarks>
    public bool ImportWindowsScreenshots { get; init; } = true;

    /// <summary>
    /// Keep every command run with Win+R (the Run dialog) in the history, and show the panel's Run tab.
    /// </summary>
    /// <remarks>
    /// On by default: Windows keeps only the last 26 commands (a full list evicts the oldest with every new one)
    /// in plain text in the registry, so mirroring them into the encrypted history adds no exposure — it stops
    /// the loss. The first activation imports what Windows still remembers; afterwards each new run is recorded
    /// the moment Windows writes it, and entering the Run tab rescans. Off stops watching and forgets the
    /// snapshot, so switching it on again starts over with what Windows remembers then (nothing is
    /// duplicated). Pause capturing skips runs made while paused; commands recorded earlier stay either way.
    /// </remarks>
    public bool RecordRunHistory { get; init; } = true;

    /// <summary>
    /// Show the panel's Everything tab while voidtools Everything is installed or running: the files and folders
    /// you opened from Everything (newest first, live from Everything's run history), next to everything copied from it.
    /// </summary>
    /// <remarks>
    /// On by default: it only reads, and only when the tab is opened. The picks themselves are not stored — the
    /// history only gets the ones you paste, copy or keep (pin, group) there. Off hides the tab and stops talking
    /// to Everything; copies made in Everything are still recorded like any copy (pause, ignored apps — add
    /// "Everything" — and the size limit apply to them).
    /// </remarks>
    public bool ShowEverythingTab { get; init; } = true;

    /// <summary>
    /// Show the panel's Pwsh tab: every command in PowerShell's own history file (PSReadLine's, shared by Windows
    /// PowerShell and PowerShell 7), newest first, searchable beyond the 4,096 PowerShell itself loads.
    /// </summary>
    /// <remarks>
    /// On by default: PowerShell already keeps these commands in plain text, the tab only reads that file (when it
    /// opens, never in between), and nothing is stored until you paste, copy or keep (pin, group) a command there.
    /// The file is never written. Off hides the tab and stops reading it; commands kept from the tab stay.
    /// </remarks>
    public bool ShowPowerShellTab { get; init; } = true;

    /// <summary>
    /// Show the panel's Cmd tab: the commands typed in Command Prompt windows. cmd keeps no history file, so
    /// BetterClipboard reads open windows' histories (every 30 seconds while one is open, and when the tab opens) and
    /// keeps what it saw, encrypted — the commands then survive their window.
    /// </summary>
    /// <remarks>
    /// On by default. Each read runs a short helper process that attaches to the window's console for a moment (never
    /// BetterClipboard itself: a console that closes while a process is attached ends that process). Only windows a
    /// person opened are read — consoles started by tools are skipped. Pause capturing stops the reading. Off stops
    /// it and forgets the kept list; commands kept from the tab as history entries stay.
    /// </remarks>
    public bool ShowCmdTab { get; init; } = true;

    /// <summary>
    /// Keep every prompt you send to Claude Code in BetterClipboard's encrypted prompt archive, and show the panel's Claude
    /// tab: newest first, searchable, one card per text with how often it was sent.
    /// </summary>
    /// <remarks>
    /// On by default: Claude Code already keeps these prompts in plain text (<c>history.jsonl</c> in its config folder, pastes
    /// beside it) and prunes them after its cleanup period; the encrypted copy adds no exposure and stops the loss. The
    /// first activation imports what Claude Code still has; afterwards each new prompt is read the moment Claude Code writes
    /// it (only the new bytes). Claude Code's files are never written. Pause capturing skips prompts sent while paused, and
    /// "Ignored apps" (add "claude") stops the archive. Off stops reading and hides the tab; the archive stays until
    /// Settings' "Delete stored prompts". The archive is not history: no retention prunes it, and only prompts you paste,
    /// copy or keep from the tab become history entries.
    /// </remarks>
    public bool KeepClaudeCodePrompts { get; init; } = true;

    /// <summary>
    /// Keep every prompt you send to Codex (the app, the CLI, the IDE extension) in the encrypted prompt archive, and show
    /// the panel's Codex tab.
    /// </summary>
    /// <remarks>
    /// On by default, like <see cref="KeepClaudeCodePrompts"/>. Read from Codex's session files (the app writes nowhere
    /// else) and its CLI history; threads spawned by agents, <c>codex exec</c> runs and MCP sessions are left out — their
    /// prompts were written by programs. "Ignored apps" matches "codex".
    /// </remarks>
    public bool KeepCodexPrompts { get; init; } = true;

    /// <summary>Process names (e.g. <c>KeePass</c>) whose copies are never recorded.</summary>
    /// <remarks>
    /// Starts out holding <see cref="KnownPasswordManagers.All"/>: <see cref="Normalize"/> merges every
    /// catalogued name not yet in <see cref="SeededIgnoredApps"/>, for new and existing users alike.
    /// Entries are free text (a name, <c>name.exe</c> or a full path) and matched after
    /// <see cref="Services.CaptureRules.NormalizeProcessName"/>.
    /// </remarks>
    public IReadOnlyList<string> IgnoredApps { get; init; } = [];

    /// <summary>
    /// Catalogued password-manager process names (<see cref="KnownPasswordManagers"/>) already offered to
    /// <see cref="IgnoredApps"/> — whether or not the user kept them.
    /// </summary>
    /// <remarks>
    /// This is what makes a deletion stick: a name listed here is never merged in again, so removing
    /// <c>KeePass</c> from the list survives restarts and updates, while names a later release adds to the
    /// catalog still arrive exactly once. Missing (settings from before the catalog) = nothing offered yet.
    /// Footgun: an older version saving the file drops this member, and the next newer version then offers
    /// the whole catalog again.
    /// </remarks>
    public IReadOnlyList<string> SeededIgnoredApps { get; init; } = [];

    /// <summary>Where the flyout appears.</summary>
    public FlyoutPlacement Placement { get; init; } = FlyoutPlacement.NearCaret;

    /// <summary>Theme of BetterClipboard's windows.</summary>
    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>
    /// Serve the <c>bclip</c> command line (scripts, AI assistants): list, search, read, copy and add
    /// history items over a named pipe restricted to this Windows account.
    /// </summary>
    /// <remarks>
    /// <b>Off by default, on purpose:</b> while on, <i>any</i> program running as the user can read the
    /// whole (otherwise encrypted) history through the pipe. The pipe only exists while this is on, so
    /// turning it off is a real barrier, not just a flag the client checks.
    /// </remarks>
    public bool EnableCommandLine { get; init; }

    /// <summary>
    /// Returns a copy whose shortcuts are <paramref name="hotkeys"/>: the first becomes <see cref="OpenHotkey"/>, the rest
    /// <see cref="ExtraOpenHotkeys"/>. Use it instead of setting the two members apart, which can leave the main shortcut
    /// duplicated among the extras.
    /// </summary>
    /// <remarks>
    /// The texts are taken as given (callers validate them as gestures first); <see cref="Normalize"/> then trims them,
    /// drops blanks and repeats, and caps the list at <see cref="MaxOpenHotkeys"/>. Shortcuts that were not among the
    /// current ones go to the front of <see cref="HotkeyHistory"/>, so the "Used before" menu lists the newest first.
    /// </remarks>
    /// <param name="hotkeys">The shortcuts in order; the first one is the main shortcut the tray names.</param>
    /// <returns>The changed copy (not yet normalized).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hotkeys"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="hotkeys"/> holds no shortcut: the panel would have no key at all.</exception>
    public AppSettings WithOpenHotkeys(IReadOnlyList<string> hotkeys)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);
        if (hotkeys.Count == 0)
        {
            throw new ArgumentException("At least one shortcut must open the panel.", nameof(hotkeys));
        }

        // Only the newly added ones move: re-saving an unchanged list (or removing one) must not reorder the history.
        var before = OpenHotkeys;
        var added = hotkeys.Where(h => !string.IsNullOrWhiteSpace(h) && !before.Any(b => SameHotkeyText(b, h)));
        return this with
        {
            OpenHotkey = hotkeys[0],
            ExtraOpenHotkeys = hotkeys.Skip(1).ToArray(),
            HotkeyHistory = [.. added, .. HotkeyHistory ?? []],
        };
    }

    /// <summary>
    /// Whether two shortcut texts name the same keys as far as text can tell: compared ignoring case and spaces, so
    /// <c>win + v</c> equals <c>Win+V</c>. Modifier order still counts (<c>Win+Alt+V</c> is not <c>Alt+Win+V</c> here);
    /// the gesture parser in BetterClipboard.Windows catches those when the shortcuts are applied.
    /// </summary>
    /// <param name="a">One shortcut text.</param>
    /// <param name="b">The other.</param>
    /// <returns><see langword="true"/> when they are the same text apart from case and spaces.</returns>
    public static bool SameHotkeyText(string? a, string? b) =>
        string.Equals(CompactHotkeyText(a), CompactHotkeyText(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a tab name as stored in <see cref="LastTab"/>: the name of a <see cref="Storage.ClipFilter"/> member, ignoring
    /// case and surrounding spaces.
    /// </summary>
    /// <remarks>
    /// Names only, compared one by one. <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> would also accept
    /// numbers ("3", and "99", which is no tab at all) and comma-separated lists ("Pinned, Text", which it combines into
    /// a third tab) — neither is something this app ever writes, so both are treated like any other unknown text.
    /// </remarks>
    /// <param name="name">The stored name; may be <see langword="null"/> or anything a hand-edited file holds.</param>
    /// <returns>The tab's filter, or <see cref="Storage.ClipFilter.All"/> for a blank or unknown name (never an exception).</returns>
    public static Storage.ClipFilter ParseTab(string? name)
    {
        var trimmed = name?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            foreach (var filter in Enum.GetValues<Storage.ClipFilter>())
            {
                if (string.Equals(filter.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return filter;
                }
            }
        }

        return Storage.ClipFilter.All;
    }

    /// <summary>
    /// Builds the normalized <see cref="HotkeyHistory"/>: the current shortcuts that are missing go first, then the history
    /// in its order, without blanks and repeats, capped at <see cref="MaxHotkeyHistory"/>.
    /// </summary>
    /// <remarks>
    /// Idempotent, since <see cref="Normalize"/> runs on every load and save: once every current shortcut is in the list,
    /// nothing moves. The cap drops the oldest entries that are not current shortcuts, so a current one is never lost.
    /// </remarks>
    /// <param name="current">The current shortcuts, already trimmed and distinct, main one first.</param>
    /// <param name="history">The stored history (may be <see langword="null"/> or hold <see langword="null"/>, from a hand-edited file).</param>
    /// <returns>The normalized history, most recent first.</returns>
    private static string[] NormalizeHotkeyHistory(IReadOnlyList<string> current, IReadOnlyList<string>? history)
    {
        var known = (history ?? []).Select(t => t?.Trim() ?? string.Empty).Where(t => t.Length > 0).ToList();
        var merged = new List<string>();
        foreach (var text in current.Where(c => !known.Any(k => SameHotkeyText(k, c))).Concat(known))
        {
            if (!merged.Any(m => SameHotkeyText(m, text)))
            {
                merged.Add(text);
            }
        }

        while (merged.Count > MaxHotkeyHistory)
        {
            int oldest = merged.FindLastIndex(m => !current.Any(c => SameHotkeyText(c, m)));
            if (oldest < 0)
            {
                break; // only current shortcuts left: never drop one of those
            }

            merged.RemoveAt(oldest);
        }

        return merged.ToArray();
    }

    /// <summary>Removes every whitespace character from a shortcut text, for <see cref="SameHotkeyText"/>.</summary>
    /// <param name="text">The text (may be <see langword="null"/>).</param>
    /// <returns>The text without whitespace; empty for <see langword="null"/>.</returns>
    private static string CompactHotkeyText(string? text) =>
        string.Concat((text ?? string.Empty).Where(c => !char.IsWhiteSpace(c)));

    /// <summary>
    /// Clamps every value into its supported range so a hand-edited or corrupted file cannot put the app
    /// into a broken state (e.g. a negative item cap), and merges catalogued password managers the
    /// settings have not seen yet into <see cref="IgnoredApps"/> (see <see cref="SeededIgnoredApps"/>).
    /// </summary>
    /// <remarks>
    /// Runs on every load and save, so everything here is idempotent. The catalog merge happens in memory
    /// on load and reaches the file with the next save — until then it simply repeats on each start.
    /// </remarks>
    /// <returns>A normalized copy.</returns>
    public AppSettings Normalize()
    {
        var ignored = (IgnoredApps ?? [])
            .Select(a => a?.Trim() ?? string.Empty)
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Stored normalized, so "KeePass.exe" typed into a hand-edited file still counts as seen.
        var seeded = (SeededIgnoredApps ?? [])
            .Select(Services.CaptureRules.NormalizeProcessName)
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var (withCatalog, seen) = KnownPasswordManagers.Seed(ignored, seeded);

        // The main shortcut is never empty (a panel without any key could only be opened from the tray); the extras lose
        // blanks, repeats of each other and of the main one, and anything past the cap — first come, first kept.
        var primary = string.IsNullOrWhiteSpace(OpenHotkey) ? "Win+V" : OpenHotkey.Trim();
        var extras = new List<string>();
        foreach (var text in ExtraOpenHotkeys ?? [])
        {
            var trimmed = text?.Trim() ?? string.Empty;
            if (trimmed.Length > 0 && extras.Count < MaxOpenHotkeys - 1 &&
                !SameHotkeyText(trimmed, primary) && !extras.Any(e => SameHotkeyText(e, trimmed)))
            {
                extras.Add(trimmed);
            }
        }

        var normalized = this with
        {
            SchemaVersion = 1,
            OpenHotkey = primary,
            ExtraOpenHotkeys = extras.ToArray(),
            HotkeyHistory = NormalizeHotkeyHistory([primary, .. extras], HotkeyHistory),

            // Canonical, so the file never keeps a name no tab answers to ("pinned" becomes "Pinned", a typo becomes "All").
            LastTab = ParseTab(LastTab).ToString(),
            MaxItems = Math.Clamp(MaxItems, 0, 1_000_000),
            RetentionDays = Math.Clamp(RetentionDays, 0, 36_500),
            MaxItemSizeMB = Math.Clamp(MaxItemSizeMB, 1, 1024),
            MaxTotalSizeMB = Math.Clamp(MaxTotalSizeMB, 0, 1_048_576),

            // A size from a hand-edited file, or from a version with other limits, must still open a usable panel.
            FlyoutWidth = Math.Clamp(FlyoutWidth, MinFlyoutWidth, MaxFlyoutSize),
            FlyoutHeight = Math.Clamp(FlyoutHeight, MinFlyoutHeight, MaxFlyoutSize),
            IgnoredApps = withCatalog,
            SeededIgnoredApps = seen,
            Placement = Enum.IsDefined(Placement) ? Placement : FlyoutPlacement.NearCaret,
            Theme = Enum.IsDefined(Theme) ? Theme : AppTheme.System,
        };
        return normalized;
    }
}
