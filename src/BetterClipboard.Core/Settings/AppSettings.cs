using System.Globalization;
using System.Text.Json.Serialization;

namespace BetterClipboard.Core.Settings;

/// <summary>Where the flyout appears when summoned.</summary>
/// <remarks>
/// Written to the settings file by name. Footgun: never add a member. A version that does not know a name throws while
/// reading the file and sets the whole file aside as corrupt (<see cref="SettingsStore.Load"/>), losing every setting. A
/// new choice gets a member of <see cref="AppSettings"/> of its own instead, the way
/// <see cref="AppSettings.CenterOnMainScreen"/> refines <see cref="CenterScreen"/>.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<FlyoutPlacement>))]
public enum FlyoutPlacement
{
    /// <summary>Next to the text caret like Win+V; falls back to the mouse cursor when the app exposes no caret.</summary>
    NearCaret = 0,

    /// <summary>Next to the mouse cursor.</summary>
    NearCursor = 1,

    /// <summary>
    /// Centered on a screen's work area: the screen with the mouse pointer (the current screen), or the main screen when
    /// <see cref="AppSettings.CenterOnMainScreen"/> is on; <c>FlyoutPositioner.ChooseScreen</c> in BetterClipboard.Windows
    /// has the rule. Until 2026-11-20 the current screen was the one the user was typing on (the text cursor's, else the
    /// window's); the owner asked for the mouse's instead.
    /// </summary>
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
    /// Interface language: <c>system</c> (follow Windows) or a catalog's tag (<c>en</c>, <c>zh-Hans</c>, <c>ru</c>,
    /// <c>es</c>, <c>he</c>); missing values preserve the system-language default.
    /// </summary>
    public string InterfaceLanguage { get; init; } = "system";

    /// <summary>
    /// Whether the user has answered the language question: picked a language in the start's language picker (every way
    /// it closes saves one) or changed Settings › App › Language. While it is <see langword="false"/> and
    /// <see cref="InterfaceLanguage"/> names no language, every start shows the picker before any other window: a first
    /// start, and equally a start after an update from a version that never asked (the owner's request, 2026-11-09).
    /// See <c>Localization.LanguageChoice.ShouldAsk</c>.
    /// </summary>
    /// <remarks>
    /// Its own member because "system" cannot tell "never asked" from "chose Use Windows setting". An administrator who
    /// pre-seeds <c>settings.json</c> silences the picker with an explicit language, or with <c>"system"</c> and this
    /// set to <see langword="true"/>. Footgun: a version that does not know this member drops it when it saves the
    /// file (any version before 2026-11-09, 0.2.9 among them, which drops the language too); a user who then comes back
    /// to a newer version with "system" is asked once more. An explicit language is never asked over.
    /// </remarks>
    public bool LanguageChosen { get; init; }

    /// <summary>
    /// Whether the user pressed <em>Start</em> in the Microsoft Store edition's welcome window: their yes to keeping
    /// what they copy, and to the choices in that window (Win+V, Start with Windows). Until then that edition records
    /// nothing, takes no shortcut and shows the welcome at every start (CLAUDE.md §2.47). Other editions never read it:
    /// there, running the installer is the yes.
    /// </summary>
    /// <remarks>
    /// The Microsoft Store's policies ask for it (10.2.8: consent before the Windows experience is changed; 10.5.5:
    /// express consent before sensitive data is stored). Footgun: a version that does not know this member drops it
    /// when it saves the file; only a Store edition older than the member could, and there is none.
    /// </remarks>
    public bool StoreWelcomeAccepted { get; init; }

    /// <summary>
    /// The panel's width when nothing was resized yet, in DIPs (Win+V is ~360×450; a little larger shows two more cards).
    /// The outer window's width, frame included, without the groups column.
    /// </summary>
    /// <remarks>
    /// Footgun: not always the width a panel opens at. A summon opens it at least as wide as its header's buttons need
    /// (<see cref="MinFlyoutWidthFor"/>): with all six buttons shown that is 432 DIPs, so a panel left at this default opens
    /// 432 wide, and hiding two buttons gives the 400 back (CLAUDE.md §2.55).
    /// </remarks>
    public const int DefaultFlyoutWidth = 400;

    /// <summary>The panel's height when nothing was resized yet, in DIPs (the outer window's, frame included).</summary>
    public const int DefaultFlyoutHeight = 560;

    /// <summary>
    /// The narrowest the panel can ever be, in DIPs (without the groups column): the search box keeps room for text left of
    /// its Aa / W / .* toggles, and a header of at most four buttons fits with its logo, "Clipboard" and the "Paused" chip.
    /// Enforced when loading; the narrowest the user can drag a panel to is <see cref="MinFlyoutWidthFor"/> its buttons.
    /// </summary>
    /// <remarks>
    /// 360 until the update button joined the header in 2026-10 (four buttons took 312 of the 322 content DIPs), then 396
    /// for five buttons, and the floor again since the buttons can be hidden and the minimum follows them (2026-11-20). A
    /// width saved by an older version stays as it was: the panel opens at least as wide as its header needs anyway.
    /// </remarks>
    public const int MinFlyoutWidth = 360;

    /// <summary>
    /// The width one more header button needs, in DIPs: the 34-DIP button and the 2-DIP gap before it
    /// (<c>IconButtonStyle</c> and the header's button row in <c>ClipboardFlyout.xaml</c>).
    /// </summary>
    public const int HeaderButtonDip = 36;

    /// <summary>
    /// How many header buttons fit at <see cref="MinFlyoutWidth"/> next to the logo, "Clipboard" and the "Paused" chip.
    /// </summary>
    public const int HeaderButtonsAtMinWidth = 4;

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
    /// How many lines of its text a card shows until the user picks another number (<see cref="LinesPerCard"/>): four, what
    /// every card showed before the number became a setting.
    /// </summary>
    public const int DefaultLinesPerCard = 4;

    /// <summary>The fewest lines of text a card shows (<see cref="LinesPerCard"/>): one, for a compact list.</summary>
    public const int MinLinesPerCard = 1;

    /// <summary>
    /// The most lines of text a card shows (<see cref="LinesPerCard"/>): forty, the owner's bound (2026-11-20). A stored
    /// preview holds enough text for that many on a wide screen (<see cref="Content.ContentClassifier.PreviewMaxChars"/>),
    /// and a file list's preview names that many files (<see cref="Content.ContentClassifier.PreviewMaxFiles"/>).
    /// </summary>
    /// <remarks>
    /// Footgun: raising it needs those two raised with it (a unit test holds them together), or the extra lines of a long
    /// text or a long file list stay empty.
    /// </remarks>
    public const int MaxLinesPerCard = 40;

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

    /// <summary>
    /// The settings file format this build writes (<see cref="SchemaVersion"/>): 2 since 2026-11-20, when the retention
    /// defaults changed and files from before move to the new ones once (<see cref="Normalize"/>).
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// How many items history keeps apart from pinned and grouped ones until the user picks a number
    /// (<see cref="MaxItems"/>): no limit. The size budget (<see cref="DefaultMaxTotalSizeMB"/>) is what keeps the history
    /// in bounds; the owner's choice of 2026-11-20 ("set maximum items to 0 (unlimited) by default").
    /// </summary>
    public const int DefaultMaxItems = 0;

    /// <summary>
    /// The size budget for history apart from pinned and grouped entries until the user picks one
    /// (<see cref="MaxTotalSizeMB"/>): 16 GB, the owner's choice of 2026-11-20. From 90 % of it the user is warned
    /// (<see cref="Services.HistoryBudgetWarnings"/>); once it is full the oldest items are removed to make room.
    /// </summary>
    public const int DefaultMaxTotalSizeMB = 16_384;

    /// <summary>
    /// <see cref="MaxItems"/>' default in settings files of format 1 (2026-09-25 to 2026-11-20). A format-1 file that still
    /// holds it is moved to <see cref="DefaultMaxItems"/> when it is read (<see cref="Normalize"/>).
    /// </summary>
    public const int SchemaVersion1DefaultMaxItems = 10_000;

    /// <summary>
    /// <see cref="MaxTotalSizeMB"/>' default in settings files of format 1 (4 GB). A format-1 file that still holds it is
    /// moved to <see cref="DefaultMaxTotalSizeMB"/> when it is read (<see cref="Normalize"/>).
    /// </summary>
    public const int SchemaVersion1DefaultMaxTotalSizeMB = 4096;

    /// <summary>
    /// Settings file format version. Written as <see cref="CurrentSchemaVersion"/>; a file with a lower one is migrated in
    /// <see cref="Normalize"/>.
    /// </summary>
    /// <remarks>
    /// Every version writes this member, so a file that lacks it was written by hand (an administrator's pre-seeded file):
    /// it reads as the current format and is never migrated. Footgun: a version from before format 2 writes 1 again when it
    /// saves the file, so going back to such a version and forward again migrates the file a second time (a limit of
    /// exactly 10,000 items or 4,096 MB set there becomes the new default again).
    /// </remarks>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

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

    /// <summary>
    /// Maximum entries kept apart from pinned and grouped ones (most recently used win); 0 = unlimited, the default since
    /// 2026-11-20 (<see cref="DefaultMaxItems"/>; 10,000 before, and files that still held it were moved to unlimited).
    /// </summary>
    /// <remarks>
    /// Every released version reads 0 as unlimited (the clamp has allowed it since the first one), so going back to an older
    /// version keeps the history whole.
    /// </remarks>
    public int MaxItems { get; init; } = DefaultMaxItems;

    /// <summary>Drop entries not used for this many days (pinned and grouped ones never); 0 = keep forever.</summary>
    public int RetentionDays { get; init; }

    /// <summary>Largest single copy recorded, in MB (Win+V caps at 4 MB).</summary>
    public int MaxItemSizeMB { get; init; } = 64;

    /// <summary>
    /// Total payload budget for history apart from pinned and grouped entries, in MB; 0 = unlimited. 16 GB by default since
    /// 2026-11-20 (<see cref="DefaultMaxTotalSizeMB"/>; 4 GB before, and files that still held it were moved to 16 GB).
    /// </summary>
    /// <remarks>
    /// It counts the stored payload (what was copied), not the database file, whose search index and thumbnails add to it:
    /// Settings › History shows both. Once the payload passes the budget, retention removes the least recently used items
    /// until it fits (<see cref="Storage.ClipStore.Prune"/>), and from 90 % on the user is warned
    /// (<see cref="Services.HistoryBudgetWarnings"/>). Every released version accepts 16,384 (the clamp allows up to 1 TB).
    /// </remarks>
    public int MaxTotalSizeMB { get; init; } = DefaultMaxTotalSizeMB;

    /// <summary>Record image-only copies (screenshots etc.).</summary>
    public bool CaptureImages { get; init; } = true;

    /// <summary>
    /// How many days a picture the card menu's <em>Save to temp and</em> wrote is kept: the start deletes the ones whose last
    /// use is older (<see cref="Content.TempImageFiles"/>); 0 = every one at every start. A month by default.
    /// </summary>
    /// <remarks>
    /// Checked only when BetterClipboard starts (after Windows starts, as a rule), never while it runs: a picture just pasted
    /// as a file into a chat that uploads it later must still be there. Footgun: a version from before this member drops it
    /// when it saves the file; the next newer version then keeps a month again.
    /// </remarks>
    public int TempImageDays { get; init; } = Content.TempImageFiles.DefaultKeepDays;

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

    /// <summary>
    /// Show pinned items above everything else. The panel still selects the newest copy, the first item below the pins
    /// (<see cref="Presentation.PanelNavigation.ArmedRow{T}"/>): off, the list is in plain recency order and its top item is
    /// the one selected.
    /// </summary>
    public bool PinnedOnTop { get; init; } = true;

    /// <summary>
    /// Whether the panel shows its groups column (the bookmark button in the header toggles it; the panel
    /// then grows to the left). Remembered so the column is there again on the next Win+V.
    /// </summary>
    public bool ShowGroupsPane { get; init; }

    /// <summary>
    /// Whether the panel stays open after a paste (the pin in its header, left of the update button; CLAUDE.md §2.55): the
    /// item still goes into the app the user was last in, and the panel stays where it is, so items can be clicked and
    /// pasted one after another. Off by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Remembered until unpinned</b> (the owner's choice, 2026-11-20), like <see cref="ShowGroupsPane"/>: every summon
    /// opens a pinned panel, also after a restart. Pinned, only the user closes the panel — Esc, the shortcut while the panel
    /// is in front, Alt+F4, the Settings button; a click elsewhere, a paste, <i>Copy only</i> or opening a file leave it up.
    /// The shortcut pressed while the pinned panel waits behind another app brings it forward in place
    /// (<see cref="Presentation.PanelShortcut"/>).
    /// </para>
    /// <para>
    /// In effect only while the pin itself shows (<see cref="PanelStaysOpen"/>): hiding the pin in Settings lets the panel
    /// close after pasting again, and showing it again restores this. Footgun: a version from before this member drops it
    /// when it saves the file, and the panel is unpinned again.
    /// </para>
    /// </remarks>
    public bool KeepPanelOpen { get; init; }

    /// <summary>
    /// The panel header's buttons the user hid (Settings › Shortcut › Buttons in the panel), as
    /// <see cref="Presentation.PanelButton"/> names; empty while every button shows (the default). The Settings button is
    /// never among them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hiding a button only takes it off the header: what it does stays in reach elsewhere (<c>Ctrl+G</c> for the groups
    /// column, the tray menu for pausing and updates, Settings for clearing the history), except the pin, whose mode ends
    /// with it (<see cref="PanelStaysOpen"/>). Each hidden button lets the panel be 36 DIPs narrower
    /// (<see cref="MinFlyoutWidthFor"/>).
    /// </para>
    /// <para>
    /// Names, read leniently (<see cref="Presentation.PanelButtons.TryParse"/>) and kept canonical by
    /// <see cref="Normalize"/>, never an enum written by name: an unknown name would make an older version set the whole file
    /// aside. Footgun: a version from before this member drops it when it saves the file, and every button shows again.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> HiddenPanelButtons { get; init; } = [];

    /// <summary>
    /// The panel header's buttons in the order the user arranged them (Settings › Shortcut › Buttons in the panel, the up and
    /// down arrows; CLAUDE.md §2.58), left to right, as <see cref="Presentation.PanelButton"/> names; empty while they stand
    /// in the default order. The Settings button is never among them: it always ends the header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written whole after a move (every button, hidden ones too, which keep their place for when they show again), and empty
    /// again once the order equals the default (<see cref="Presentation.PanelButtons.ToStored"/>), so a later version's default
    /// order still reaches users who never moved a button. The panel and Settings complete the list with buttons it lacks
    /// (<see cref="Presentation.PanelButtons.Arrange"/>), so any list here shows every button once.
    /// </para>
    /// <para>
    /// Names, read leniently like <see cref="HiddenPanelButtons"/> (<see cref="Normalize"/> keeps the known ones, spelled as
    /// their member, each once, in the stored order). Footgun: a version from before this member drops it when it saves the
    /// file, and the buttons are back in their default order.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> PanelButtonOrder { get; init; } = [];

    /// <summary>
    /// Whether the panel stays open after pasting right now: <see cref="KeepPanelOpen"/>, while the pin shows in the header.
    /// Derived, so it is never written to the file.
    /// </summary>
    [JsonIgnore]
    public bool PanelStaysOpen => KeepPanelOpen && !Presentation.PanelButtons.IsHidden(HiddenPanelButtons, Presentation.PanelButton.KeepOpen);

    /// <summary>
    /// Opens the panel without taking the keyboard from the app the user types in, the way Windows' own Win+V does: the focus
    /// stays in that app, so a control that ends its editing when it loses the focus — a rename in File Explorer, its search
    /// box or address bar, a note's name in Obsidian's file tree — is still there when the item is pasted into it. On by
    /// default (the owner's choice, 2026-11-20).
    /// </summary>
    /// <remarks>
    /// <para>
    /// While such a panel shows, a low-level keyboard hook gives it the keys (CLAUDE.md §2.59): the arrow keys, Enter, Esc
    /// and the panel's shortcuts act as always, and typed characters search, added at the end of the search text
    /// (<see cref="Presentation.SearchTyping"/>). Off, the panel takes the keyboard when it opens, as before: for screen
    /// readers, which follow the focus, and for input methods that compose text (Chinese, Japanese, Korean), whose keys a hook
    /// cannot compose. A click on the search box, Ctrl+F, a viewer or a menu opened from the keyboard give the panel the
    /// keyboard for that use anyway, and so does a window that runs as administrator (a hook does not see its keys).
    /// </para>
    /// <para>
    /// Footgun: a version from before this member drops it when it saves the file; the next newer version then opens the
    /// panel without the keyboard again (the default), also for a user who had turned it off.
    /// </para>
    /// </remarks>
    public bool LeaveFocusInApp { get; init; } = true;

    /// <summary>
    /// Whether the user closed the pin's tip (CLAUDE.md §2.55): the tip that points at the pin and says in a sentence and a
    /// half how pinning works. Until then it shows whenever the pinned panel is on screen (<see cref="KeepOpenTipDue"/>);
    /// once closed — with its close button or with Esc — never again. Off by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The owner's request (2026-11-20): "Add a tooltip shown once until dismissed when pinned enabled that in 1.5 sentences
    /// explains how pinning works". Only a close by the user spends it: the panel going away, its window moving, unpinning
    /// or hiding the pin take the tip off the screen and leave it due, so it comes back with the pinned panel.
    /// </para>
    /// <para>
    /// Footgun: a version from before this member drops it when it saves the file, and the tip shows once more.
    /// </para>
    /// </remarks>
    public bool KeepOpenTipDismissed { get; init; }

    /// <summary>
    /// Whether the pin's tip should show while the panel is on screen: the panel stays open after pasting
    /// (<see cref="PanelStaysOpen"/>: pinned, with the pin shown) and the user never closed the tip
    /// (<see cref="KeepOpenTipDismissed"/>). Derived, so it is never written to the file.
    /// </summary>
    [JsonIgnore]
    public bool KeepOpenTipDue => PanelStaysOpen && !KeepOpenTipDismissed;

    /// <summary>
    /// The panel's width the user last dragged it to, in DIPs (device-independent pixels, 1/96 inch): the outer window,
    /// frame included, without the groups column — which adds its own width on the left while it is open, so opening or
    /// closing the column never changes this.
    /// </summary>
    /// <remarks>
    /// Saved once when a resize ends (never during the drag), and only for a resize the user made: the panel moving to a
    /// monitor with another scale changes its pixels, not this. Every summon opens the panel at this size next to the
    /// caret, shrunk to the monitor's work area when larger (the shrunk size is not saved), and widened to what its
    /// header's buttons need when narrower (<see cref="MinFlyoutWidthFor"/>; the widened size is not saved either, so hiding
    /// buttons gives the narrower width back). Kept in <see cref="MinFlyoutWidth"/>..<see cref="MaxFlyoutSize"/>; resizing
    /// the panel wider shows more of the filter tabs, which scroll sideways when they do not all fit.
    /// </remarks>
    public int FlyoutWidth { get; init; } = DefaultFlyoutWidth;

    /// <summary>
    /// The panel's height the user last dragged it to, in DIPs: the outer window, frame included. Saved and applied like
    /// <see cref="FlyoutWidth"/>, kept in <see cref="MinFlyoutHeight"/>..<see cref="MaxFlyoutSize"/>.
    /// </summary>
    public int FlyoutHeight { get; init; } = DefaultFlyoutHeight;

    /// <summary>
    /// How many lines of its text each card in the panel shows before the rest is cut off with "…" (Settings › Shortcut ›
    /// Lines per card), <see cref="MinLinesPerCard"/> to <see cref="MaxLinesPerCard"/>; <see cref="DefaultLinesPerCard"/>
    /// until the user picks another number. A card that shows less than its whole text still gets the eye, the dotted line
    /// under its text and the hover peek, which show all of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The owner's request (2026-11-20): "make the number of lines per card configurable 1-40". Only the text's lines change:
    /// the caption line above them, a picture's thumbnail and a color's swatch keep their size. Applied to the panel at once,
    /// also while it shows (<c>ClipboardFlyout.ApplyCardLines</c>); nothing is loaded again, since every card already holds
    /// a preview long enough for the most lines (CLAUDE.md §2.57).
    /// </para>
    /// <para>
    /// Footgun: a version from before this member drops it when it saves the file, and the cards show four lines again.
    /// </para>
    /// </remarks>
    public int LinesPerCard { get; init; } = DefaultLinesPerCard;

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
    /// The panel's tabs in the order the user arranged them (right-click a tab › <i>Move left</i> / <i>Move right</i>), as
    /// the names of their <see cref="Storage.ClipFilter"/>s; empty while they stand in the panel's own order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written whole after a move (every tab, also those not shown right now), and empty again once the order equals the
    /// default (<see cref="Presentation.TabArrangement.ToStored"/>), so a later version's default order still reaches users
    /// who never moved a tab. The panel completes the list with tabs it lacks and keeps "All" first
    /// (<see cref="Presentation.TabArrangement.Arrange"/>), so any list here shows every tab once.
    /// </para>
    /// <para>
    /// Names, read leniently like <see cref="LastTab"/> (<see cref="Normalize"/> keeps the known ones, spelled as their
    /// filter, each once): a tab name a version does not know only drops that entry. Footgun: a version from before this
    /// member drops it when it saves the file, and the tabs are back in their default order.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> TabOrder { get; init; } = [];

    /// <summary>
    /// The tabs the user took out of the row (right-click a tab › <i>Hide tab</i> or <i>Hide tab &amp; clipboard rows</i>;
    /// <i>Show all hidden</i> empties the list), as <see cref="Storage.ClipFilter"/> names. Never "All": it always stays.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hiding only takes a tab out of the row (the owner's choice, 2026-11-08): what feeds a hidden tab keeps working — the
    /// Settings switches of the tabs of other apps stay on, Command Prompt windows are still read, prompts still archived —
    /// so a tab shown again has everything. A hidden tab is skipped by the Left and Right keys and by the moves; when it is
    /// the tab the user last chose (<see cref="LastTab"/>), the panel opens in "All" until it is shown again.
    /// </para>
    /// <para>
    /// Read leniently like <see cref="TabOrder"/>. Same footgun: a version from before this member drops it when it saves
    /// the file, and every tab shows again.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> HiddenTabs { get; init; } = [];

    /// <summary>
    /// The hidden tabs whose clipboard rows are hidden too (right-click a tab › <i>Hide tab &amp; clipboard rows</i>; <i>Show
    /// all hidden</i> empties it with <see cref="HiddenTabs"/>), as <see cref="Storage.ClipFilter"/> names: the entries such a
    /// tab lists are left out of every list the panel shows (<see cref="Storage.ClipQuery.HiddenRows"/>), except entries
    /// that are in a group.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Hidden, not removed</b> (the owner's choices, 2026-11-09): nothing is deleted or kept from being recorded, the
    /// command line still lists every entry, and an entry the user put in a group stays wherever it shows — it was kept on
    /// purpose. A pinned entry is hidden like any other.
    /// </para>
    /// <para>
    /// Always a subset of <see cref="HiddenTabs"/> (<see cref="Normalize"/> drops a name whose tab shows): a tab in the row
    /// never has its rows hidden. Read leniently like <see cref="TabOrder"/>. Footgun: a version from before this member
    /// drops it when it saves the file, and the rows show again while their tabs stay hidden.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> HiddenTabRows { get; init; } = [];

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

    /// <summary>
    /// The text viewer's text size (the eye's viewer and the hover peek of a long text), as a factor of the normal size:
    /// 1 is 100 %. Changed with Ctrl+wheel, Ctrl+= and Ctrl+- in the viewer and remembered (the owner's choice,
    /// 2026-11-13); clamped to 0.5–3 by <see cref="Normalize"/>.
    /// </summary>
    public double TextViewerTextSize { get; init; } = 1;

    /// <summary>
    /// The text viewer's Wrap switch: long lines wrap at the window's edge (on, the default) or keep their shape and scroll
    /// sideways (off). Remembered like <see cref="TextViewerTextSize"/>.
    /// </summary>
    public bool TextViewerWrap { get; init; } = true;

    /// <summary>
    /// Whether the text viewer shows line numbers next to code (never next to Markdown drawn as a document). On by default;
    /// remembered.
    /// </summary>
    public bool TextViewerLineNumbers { get; init; } = true;

    /// <summary>Import Windows' current Win+V history (including its pinned items) at every startup.</summary>
    public bool ImportWindowsHistoryOnStartup { get; init; } = true;

    /// <summary>Temporarily stop recording live copies.</summary>
    public bool IsCapturePaused { get; init; }

    /// <summary>
    /// This user turned "Start with Windows" off while an installation for every user of the PC starts BetterClipboard at
    /// each sign-in (its all-users entry, <see cref="Startup.SignInStartup"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows keeps an all-users entry's on/off mark for the whole PC, and only an administrator may change it, so this
    /// user's own "off" is kept here: a BetterClipboard that the all-users entry starts reads it before anything else and
    /// ends at once. Settings › Start with Windows sets and clears it; nothing else does. Off by default: an installation
    /// for every user starts BetterClipboard for each of them until they turn it off.
    /// </para>
    /// <para>
    /// Footgun: an older version that saves the settings file drops this member, and the all-users entry then starts
    /// BetterClipboard for this user again from the next sign-in on.
    /// </para>
    /// </remarks>
    public bool SkipAllUsersStartup { get; init; }

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
    /// Most ShareX screenshots one catch-up imports — the newest of those saved while BetterClipboard was not watching
    /// (not running, or the watched folders just changed); 0 = every one of them (the default, <see cref="ListLimit"/>).
    /// </summary>
    /// <remarks>
    /// Until 2026-11-21 a catch-up imported at most the 100 newest, so a long absence could not flood the history; the
    /// owner chose no limit by default, with this number in the ShareX card for whoever wants one back. The screenshots
    /// left out stay in ShareX's folders, and the catch-up marker moves past them: they are never imported later.
    /// </remarks>
    public int MaxShareXCatchUp { get; init; }

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
    /// Most screenshots one catch-up of the Screenshots folder imports — the newest of those Snipping Tool and Win+PrtScn
    /// saved while BetterClipboard was not watching; 0 = every one of them (the default, <see cref="ListLimit"/>).
    /// </summary>
    /// <remarks>
    /// Until 2026-11-21 the 100 newest (see <see cref="MaxShareXCatchUp"/>, the same rule for ShareX). The ones left out
    /// stay in the folder and are never imported later: the catch-up marker moves past them.
    /// </remarks>
    public int MaxScreenshotsCatchUp { get; init; }

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
    /// Most files and folders the Everything tab keeps hidden (Delete on a pick hides it until it is opened in Everything
    /// again); the hides made longest ago go first, which shows their picks again. 0 = no limit (the default,
    /// <see cref="ListLimit"/>).
    /// </summary>
    /// <remarks>Until 2026-11-21 the newest 500. A lowered number applies at once (<c>AppController.Limits.cs</c>).</remarks>
    public int MaxHiddenEverythingPicks { get; init; }

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
    /// Most commands the Pwsh tab keeps hidden (Delete on a command hides it until it is typed again); the hides made
    /// longest ago go first, which shows their commands again. 0 = no limit (the default, <see cref="ListLimit"/>).
    /// </summary>
    /// <remarks>Until 2026-11-21 the newest 1,000. A lowered number applies at once (<c>AppController.Limits.cs</c>).</remarks>
    public int MaxHiddenPowerShellCommands { get; init; }

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
    /// Most Command Prompt commands the Cmd tab keeps after their window closed (the ones seen longest ago go first);
    /// 0 = no limit (the default, <see cref="ListLimit"/>).
    /// </summary>
    /// <remarks>
    /// Until 2026-11-21 the newest 1,000 — the cap the owner asked about ("are there any with a limit like I think Cmd has
    /// (of 1000)?"). A lowered number applies at once: the oldest kept commands are deleted. Kept commands are not
    /// clipboard history and count against none of its limits (<see cref="Shells.CmdHistory"/>).
    /// </remarks>
    public int MaxKeptCmdCommands { get; init; }

    /// <summary>
    /// Most commands the Cmd tab keeps hidden (Delete on a command hides it until it is typed again); the hides made
    /// longest ago go first. 0 = no limit (the default, <see cref="ListLimit"/>).
    /// </summary>
    /// <remarks>Until 2026-11-21 the newest 1,000. A lowered number applies at once (<c>AppController.Limits.cs</c>).</remarks>
    public int MaxHiddenCmdCommands { get; init; }

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

    /// <summary>
    /// Keep the text of Notepad++'s unsaved tabs ("new 3") in the history, updated as it changes, and show the panel's
    /// Notepad++ tab (CLAUDE.md §2.41).
    /// </summary>
    /// <remarks>
    /// On by default, like the other integrations: Notepad++ already keeps this text in plain files (its backup folder), the
    /// encrypted copy adds no exposure and keeps what Notepad++ forgets when a tab is closed. Read only from Notepad++'s
    /// own files, once a tab's text has rested (never on every 7-second save). A change that loses more than a fifth of the
    /// text keeps the old version as its own entry (<see cref="Documents.TrackedDocumentRule"/>). Off stops reading and
    /// forgets which entry belongs to which tab; the entries stay. "Ignored apps" matches "notepad++".
    /// </remarks>
    public bool KeepNotepadPlusPlusTabs { get; init; } = true;

    /// <summary>
    /// Record the full path of every file opened in Notepad++, as if it was copied: opening it again moves its one entry to
    /// now (CLAUDE.md §2.41).
    /// </summary>
    /// <remarks>
    /// On by default. Asks a running, signed Notepad++ through its plugin messages which documents it has open (reading paths
    /// through a buffer BetterClipboard places in Notepad++'s process); nothing in Notepad++ changes. The files it restores
    /// from its last session are not openings. Not for a Notepad++ running as administrator (Windows blocks the messages).
    /// </remarks>
    public bool RecordNotepadPlusPlusFiles { get; init; } = true;

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

    /// <summary>
    /// Where the panel appears at every summon (Settings › Shortcut › Panel position): next to the text cursor (the
    /// default, like Win+V), next to the mouse, or centered on a screen (<see cref="CenterOnMainScreen"/> says which).
    /// </summary>
    /// <remarks>
    /// Only where a summon puts the panel: a panel the user dragged elsewhere is placed this way again at the next summon,
    /// and its size is always <see cref="FlyoutWidth"/> × <see cref="FlyoutHeight"/>, shrunk to the chosen screen's work
    /// area.
    /// </remarks>
    public FlyoutPlacement Placement { get; init; } = FlyoutPlacement.NearCaret;

    /// <summary>
    /// With <see cref="Placement"/> <see cref="FlyoutPlacement.CenterScreen"/>: center the panel on the main screen (the
    /// one Windows' display settings call the main display) instead of the screen with the mouse pointer. Off by default;
    /// the other placements ignore it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A member of its own, not a fourth <see cref="FlyoutPlacement"/> value, on purpose:</b> the placement is written
    /// by name, and a name an older version does not know makes that version set the whole settings file aside as
    /// corrupt. This way an older version reads <c>CenterScreen</c>, which it knows, and centers on the current screen.
    /// </para>
    /// <para>
    /// Footgun: an older version that saves the settings file drops this member; the next newer version then centers on
    /// the current screen until the main screen is chosen again.
    /// </para>
    /// </remarks>
    public bool CenterOnMainScreen { get; init; }

    /// <summary>Theme of BetterClipboard's windows.</summary>
    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>
    /// How much of what lies behind the panel shows through its frosted glass (Settings › App › Background transparency,
    /// CLAUDE.md §2.58): <see cref="Presentation.PanelBackdrop.MinLevel"/> (0, the most frosted) to
    /// <see cref="Presentation.PanelBackdrop.MaxLevel"/> (100, the clearest); <see cref="Presentation.PanelBackdrop.DefaultLevel"/>
    /// (50) is Windows' own acrylic, the panel's look before the setting existed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The glass stays blurred at every level (the owner's "it always stays blur"): the level only sets how strongly its frost
    /// covers the blurred background (<see cref="Presentation.PanelBackdrop.LookFor"/>). Where Windows draws no glass at all
    /// (transparency effects off, battery saver, a remote session) the panel is a solid color whatever this says.
    /// </para>
    /// <para>
    /// Only the panel's background: Settings keeps Windows' Mica, and the panel's cards, menus and viewers their own colors.
    /// Footgun: a version from before this member drops it when it saves the file, and the panel is back to Windows' look.
    /// </para>
    /// </remarks>
    public int BackgroundTransparency { get; init; } = Presentation.PanelBackdrop.DefaultLevel;

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
    /// Ask GitHub once a day whether a newer release of BetterClipboard exists, so the panel's update button can light up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On by default, and the only request the app makes by itself.</b> It is a plain download of this project's public
    /// release list: nothing about the user, the PC or the clipboard is sent (the server sees what any web request shows:
    /// the network address, and the app's name and version). Off means no request at all until the user presses
    /// "Check now"; <c>install.ps1 -NoUpdateCheck</c> installs with it off.
    /// </para>
    /// <para>
    /// Checking never installs anything: an update is downloaded only after the user approves it in the update dialog.
    /// Copies that a package manager updates (Chocolatey), copies installed for every user of the PC (which only an
    /// administrator may update) and isolated test instances never check by themselves, whatever this says.
    /// </para>
    /// </remarks>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>
    /// A release the user chose not to be reminded of ("Skip this version" in the update dialog), as its version text
    /// (<c>0.2.7</c>); empty for none.
    /// </summary>
    /// <remarks>
    /// Only the highlight is skipped: the dialog still offers that version. A newer release ends the skip by itself,
    /// because only an exact match counts. Text that is not a version skips nothing.
    /// </remarks>
    public string SkippedUpdateVersion { get; init; } = string.Empty;

    /// <summary>
    /// When the user was last reminded of an update that waits: the tooltip that opens by itself at the panel's update
    /// button was on screen, or they opened the update dialog while an update was offered. <see langword="null"/> for
    /// never.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It only spaces the reminders (<see cref="Updates.UpdateReminder"/>: at most one in seven days). It is not a
    /// switch, and the reminder has none of its own: it follows <see cref="CheckForUpdates"/>, and
    /// <see cref="SkippedUpdateVersion"/> silences it for one version.
    /// </para>
    /// <para>
    /// Footguns: a time in the future (written while the clock was wrong) would postpone the reminder, so the reader
    /// takes one more than a day ahead for "never". An older version that saves the settings file drops this member; the
    /// next newer version then reminds once more, at most a week early.
    /// </para>
    /// </remarks>
    public DateTimeOffset? UpdateReminderShownUtc { get; init; }

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
    /// The narrowest a panel whose header shows <paramref name="headerButtons"/> buttons can be, in DIPs (without the
    /// groups column): its logo, "Clipboard", the "Paused" chip and those buttons fit side by side, never below
    /// <see cref="MinFlyoutWidth"/>. 360 for four buttons or fewer, 396 for five, 432 for all six.
    /// </summary>
    /// <remarks>
    /// The panel answers it as the minimum Windows lets the user drag it to, and opens at least this wide whatever width is
    /// remembered — never saved, so hiding buttons in Settings gives a narrower remembered width back (CLAUDE.md §2.55).
    /// Linear on purpose: each button and its gap take <see cref="HeaderButtonDip"/>, measured on the five-button header
    /// (348 of the 358 content DIPs at 396).
    /// </remarks>
    /// <param name="headerButtons">
    /// The buttons in the header's right-hand group, the Settings button included (<see cref="Presentation.PanelButtons.CountShown"/>);
    /// negative counts as none.
    /// </param>
    /// <returns>The minimum width in DIPs.</returns>
    public static int MinFlyoutWidthFor(int headerButtons) =>
        MinFlyoutWidth + (HeaderButtonDip * Math.Max(0, headerButtons - HeaderButtonsAtMinWidth));

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
    /// Reads a tab name as stored in <see cref="TabOrder"/>, <see cref="HiddenTabs"/> or <see cref="HiddenTabRows"/>: the name of a
    /// <see cref="Storage.ClipFilter"/> member, ignoring case and surrounding spaces — and, unlike <see cref="ParseTab"/>,
    /// tells an unknown name apart instead of reading it as "All".
    /// </summary>
    /// <remarks>
    /// Names only, compared one by one, for the reason <see cref="ParseTab"/> gives: <see cref="Enum.TryParse{TEnum}(string,
    /// bool, out TEnum)"/> would also accept numbers and comma-separated lists, which this app never writes.
    /// </remarks>
    /// <param name="name">The stored name; may be <see langword="null"/> or anything a hand-edited file holds.</param>
    /// <param name="filter">The tab's filter; <see cref="Storage.ClipFilter.All"/> when the name is unknown.</param>
    /// <returns><see langword="true"/> when the name is a tab's (never an exception).</returns>
    public static bool TryParseTab(string? name, out Storage.ClipFilter filter)
    {
        var trimmed = name?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            foreach (var candidate in Enum.GetValues<Storage.ClipFilter>())
            {
                if (string.Equals(candidate.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    filter = candidate;
                    return true;
                }
            }
        }

        filter = Storage.ClipFilter.All;
        return false;
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

    /// <summary>
    /// Normalizes a stored list of tab names (<see cref="TabOrder"/>, <see cref="HiddenTabs"/>, <see cref="HiddenTabRows"/>):
    /// every name a tab answers to,
    /// spelled as its <see cref="Storage.ClipFilter"/>, in the stored order, each once.
    /// </summary>
    /// <remarks>
    /// Idempotent, since <see cref="Normalize"/> runs on every load and save. Names no tab answers to — a tab of a newer
    /// version, a typo in a hand-edited file, a blank — are dropped rather than kept for later: a version that knows such a
    /// tab puts it back in its default place (<see cref="Presentation.TabArrangement.Arrange"/>), so dropping costs at most
    /// that tab's place, never a broken file.
    /// </remarks>
    /// <param name="names">The stored names (may be <see langword="null"/> or hold <see langword="null"/>, from a hand-edited file).</param>
    /// <returns>The known names, canonical and distinct.</returns>
    private static string[] NormalizeTabNames(IReadOnlyList<string>? names)
    {
        var result = new List<string>();
        foreach (var name in names ?? [])
        {
            if (TryParseTab(name, out var filter) && !result.Contains(filter.ToString()))
            {
                result.Add(filter.ToString());
            }
        }

        return result.ToArray();
    }

    /// <summary>Removes every whitespace character from a shortcut text, for <see cref="SameHotkeyText"/>.</summary>
    /// <param name="text">The text (may be <see langword="null"/>).</param>
    /// <returns>The text without whitespace; empty for <see langword="null"/>.</returns>
    private static string CompactHotkeyText(string? text) =>
        string.Concat((text ?? string.Empty).Where(c => !char.IsWhiteSpace(c)));

    /// <summary>
    /// Words what <see cref="Normalize"/> changes when it reads a file of an older format, for the log line
    /// <see cref="SettingsStore.Load"/> writes: content-free, the retention limits only, in English with invariant numbers
    /// whatever Windows' formats (logs stay English, CLAUDE.md §2.37).
    /// </summary>
    /// <remarks>
    /// The migration happens in memory at every start until the file is saved, so the line repeats until then (most uses of
    /// the panel save the file: the remembered tab, a resize).
    /// </remarks>
    /// <param name="loaded">The settings as read from the file, before <see cref="Normalize"/>.</param>
    /// <returns>The sentence, or <see langword="null"/> when the file is of the current format or the migration changes nothing in it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="loaded"/> is <see langword="null"/>.</exception>
    public static string? DescribeMigration(AppSettings loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        if (loaded.SchemaVersion >= CurrentSchemaVersion)
        {
            return null;
        }

        var changes = new List<string>(2);
        if (loaded.MaxItems == SchemaVersion1DefaultMaxItems)
        {
            changes.Add(string.Create(CultureInfo.InvariantCulture, $"the item limit {SchemaVersion1DefaultMaxItems:N0} (format {loaded.SchemaVersion}'s default) is now unlimited"));
        }

        if (loaded.MaxTotalSizeMB == SchemaVersion1DefaultMaxTotalSizeMB)
        {
            changes.Add(string.Create(CultureInfo.InvariantCulture, $"the history size budget {SchemaVersion1DefaultMaxTotalSizeMB:N0} MB (format {loaded.SchemaVersion}'s default) is now {DefaultMaxTotalSizeMB:N0} MB"));
        }

        return changes.Count == 0
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"Settings of format {loaded.SchemaVersion} read as format {CurrentSchemaVersion}: {string.Join(", and ", changes)}.");
    }

    /// <summary>
    /// Clamps every value into its supported range so a hand-edited or corrupted file cannot put the app
    /// into a broken state (e.g. a negative item cap), merges catalogued password managers the
    /// settings have not seen yet into <see cref="IgnoredApps"/> (see <see cref="SeededIgnoredApps"/>), and migrates a file
    /// of an older format (<see cref="SchemaVersion"/>) to the current one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs on every load and save, so everything here is idempotent. The catalog merge happens in memory
    /// on load and reaches the file with the next save — until then it simply repeats on each start.
    /// </para>
    /// <para>
    /// <b>Format 1 → 2</b> (2026-11-20, the owner's request "set maximum items to 0 (unlimited) by default and for all users
    /// that had previous default"): a file whose <see cref="MaxItems"/> is still format 1's default (10,000) gets no item
    /// limit, and one whose <see cref="MaxTotalSizeMB"/> is still format 1's default (4,096 MB) gets the 16 GB budget. A value
    /// the user picked stays. Like the catalog merge, this happens in memory at every start until the file is saved, which
    /// gives the same result each time; <see cref="DescribeMigration"/> words it for the log.
    /// </para>
    /// </remarks>
    /// <returns>A normalized copy.</returns>
    public AppSettings Normalize()
    {
        // Only a file of format 1 still holding format 1's defaults moves; every later save writes the current format, so a
        // value picked afterwards (10,000 included) is never touched again.
        bool formatOne = SchemaVersion < CurrentSchemaVersion;
        int maxItems = formatOne && MaxItems == SchemaVersion1DefaultMaxItems ? DefaultMaxItems : MaxItems;
        int maxTotalSizeMB = formatOne && MaxTotalSizeMB == SchemaVersion1DefaultMaxTotalSizeMB ? DefaultMaxTotalSizeMB : MaxTotalSizeMB;

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

        // "All" always stays, so it is never hidden (a hand-edited file cannot hide it); a tab's rows are hidden only while
        // the tab itself is, so a tab in the row never misses rows of its own.
        var hiddenTabs = NormalizeTabNames(HiddenTabs).Where(name => name != DefaultTab).ToArray();

        var normalized = this with
        {
            SchemaVersion = CurrentSchemaVersion,
            OpenHotkey = primary,
            ExtraOpenHotkeys = extras.ToArray(),
            HotkeyHistory = NormalizeHotkeyHistory([primary, .. extras], HotkeyHistory),

            // Canonical, so the file never keeps a name no tab answers to ("pinned" becomes "Pinned", a typo becomes "All").
            LastTab = ParseTab(LastTab).ToString(),

            // Known names only, each once (the hidden lists are worked out above).
            TabOrder = NormalizeTabNames(TabOrder),
            HiddenTabs = hiddenTabs,
            HiddenTabRows = NormalizeTabNames(HiddenTabRows).Where(name => hiddenTabs.Contains(name)).ToArray(),

            // Known buttons only, each once, in the header's order (a hand-edited "pause" becomes "Pause"; "Settings" and
            // typos are dropped: the Settings button always shows).
            HiddenPanelButtons = Presentation.PanelButtons.Normalize(HiddenPanelButtons),

            // Known buttons only, each once, in the stored order ("Settings" and typos dropped: it always ends the header);
            // completed with missing buttons when it is read (PanelButtons.Arrange), never here.
            PanelButtonOrder = Presentation.PanelButtons.NormalizeOrder(PanelButtonOrder),

            // A hand-edited level outside 0..100 still gives a panel with glass.
            BackgroundTransparency = Presentation.PanelBackdrop.Normalize(BackgroundTransparency),
            MaxItems = Math.Clamp(maxItems, 0, 1_000_000),
            RetentionDays = Math.Clamp(RetentionDays, 0, 36_500),
            MaxItemSizeMB = Math.Clamp(MaxItemSizeMB, 1, 1024),
            MaxTotalSizeMB = Math.Clamp(maxTotalSizeMB, 0, 1_048_576),
            TempImageDays = Math.Clamp(TempImageDays, 0, Content.TempImageFiles.MaxKeepDays),

            // The integrations' limits (ListLimit): a hand-edited negative number is "no limit" like 0, and a huge one the
            // NumberBox's own maximum.
            MaxShareXCatchUp = ListLimit.Normalize(MaxShareXCatchUp),
            MaxScreenshotsCatchUp = ListLimit.Normalize(MaxScreenshotsCatchUp),
            MaxHiddenEverythingPicks = ListLimit.Normalize(MaxHiddenEverythingPicks),
            MaxHiddenPowerShellCommands = ListLimit.Normalize(MaxHiddenPowerShellCommands),
            MaxKeptCmdCommands = ListLimit.Normalize(MaxKeptCmdCommands),
            MaxHiddenCmdCommands = ListLimit.Normalize(MaxHiddenCmdCommands),

            // A size from a hand-edited file, or from a version with other limits, must still open a usable panel.
            FlyoutWidth = Math.Clamp(FlyoutWidth, MinFlyoutWidth, MaxFlyoutSize),
            FlyoutHeight = Math.Clamp(FlyoutHeight, MinFlyoutHeight, MaxFlyoutSize),

            // A hand-edited 0 would hide every card's text, and 1,000 lines more than any preview holds.
            LinesPerCard = Math.Clamp(LinesPerCard, MinLinesPerCard, MaxLinesPerCard),

            // The viewer's text size within what its zoom allows; a hand-edited NaN or infinity is 100 %.
            TextViewerTextSize = double.IsFinite(TextViewerTextSize) ? Math.Clamp(TextViewerTextSize, TextView.TextViewStyle.MinScale, TextView.TextViewStyle.MaxScale) : 1,
            IgnoredApps = withCatalog,
            SeededIgnoredApps = seen,

            // Free text from the file: kept short, compared as a version by the updater (anything else skips nothing).
            SkippedUpdateVersion = (SkippedUpdateVersion ?? string.Empty).Trim() is { Length: <= 64 } skipped ? skipped : string.Empty,

            // One spelling in the file whatever offset a hand edit used. Whether the time makes sense is the reader's
            // question (UpdateReminder.IsDue): this method has no clock.
            UpdateReminderShownUtc = UpdateReminderShownUtc?.ToUniversalTime(),
            Placement = Enum.IsDefined(Placement) ? Placement : FlyoutPlacement.NearCaret,
            Theme = Enum.IsDefined(Theme) ? Theme : AppTheme.System,
        };
        return normalized;
    }
}
