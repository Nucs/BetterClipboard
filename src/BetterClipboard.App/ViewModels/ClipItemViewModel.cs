using BetterClipboard.Core.Localization;
using System.Runtime.InteropServices.WindowsRuntime;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Everything;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Presentation;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// One card in the flyout list. Wraps an immutable <see cref="ClipEntry"/> snapshot plus lazily loaded
/// UI state (thumbnail) and the few properties that change while the card is visible (pin, caption).
/// </summary>
/// <remarks>
/// Must be created and used on the UI thread (it creates XAML brushes and bitmaps). Per-kind visibility
/// properties are computed once — the kind of an entry never changes.
/// </remarks>
public sealed partial class ClipItemViewModel : ObservableObject
{
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Cascadia Code, Consolas");
    private static readonly FontFamily UiFont = FontFamily.XamlAutoFontFamily;

    private readonly Func<long, Task<byte[]?>> thumbnailLoader;
    private Func<long, ClipGroup?> groupLookup;
    private bool thumbnailRequested;

    /// <summary>
    /// A newer time than the entry's last use that the card's tab knows its content by (<see cref="ShowNewerTime"/>), or
    /// <see langword="null"/>. Kept across <see cref="Update"/>: the caption shows whichever of the two is newer.
    /// </summary>
    private DateTimeOffset? newerTime;

    /// <summary>
    /// Creates the card model.
    /// </summary>
    /// <param name="entry">The history entry.</param>
    /// <param name="thumbnailLoader">Loads PNG thumbnail bytes by entry id (called at most once, on demand).</param>
    /// <param name="groupLookup">Finds a group by id for the card's group badges; <see langword="null"/> = no badges.</param>
    /// <param name="pick">The Everything pick behind a stand-in entry (use <see cref="ForPick"/>); <see langword="null"/> for a history entry.</param>
    public ClipItemViewModel(ClipEntry entry, Func<long, Task<byte[]?>> thumbnailLoader, Func<long, ClipGroup?>? groupLookup = null, EverythingPick? pick = null)
    {
        Entry = entry;
        Pick = pick;
        this.thumbnailLoader = thumbnailLoader;
        this.groupLookup = groupLookup ?? (_ => null);
        IsPinned = entry.IsPinned;
        Caption = BuildCaption();
        (GroupBadges, GroupBadgesTooltip) = BuildGroupBadges();

        if (entry.Kind == ClipKind.Color && ColorLiteral.TryParse(entry.Preview, out var argb))
        {
            ColorBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(argb.A, argb.R, argb.G, argb.B));
        }

        // A preview that is only the start of its text is cut off before any layout says so (ClipItemViewModel.TextView.cs).
        RefreshCutOff();
    }

    /// <summary>
    /// A card for a file opened in Everything (the Everything tab's live picks). It is not a history entry, so its
    /// <see cref="Entry"/> is a stand-in: a negative <paramref name="syntheticId"/> that no stored entry can have
    /// (selection, drag and in-place updates key on ids), kind Files, the pick's name and folder as preview, its
    /// last opening as time. Actions must check <see cref="Pick"/> before treating the card as stored.
    /// </summary>
    /// <param name="pick">The pick.</param>
    /// <param name="syntheticId">A negative id, unique within the list.</param>
    /// <returns>The card model.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pick"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="syntheticId"/> is not negative.</exception>
    public static ClipItemViewModel ForPick(EverythingPick pick, long syntheticId)
    {
        ArgumentNullException.ThrowIfNull(pick);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(syntheticId, 0);
        var opened = pick.LastOpened ?? DateTimeOffset.MinValue;
        var entry = new ClipEntry
        {
            Id = syntheticId,
            Kind = ClipKind.Files,
            Preview = pick.Folder is { Length: > 0 } folder ? $"{pick.Name}\n{folder}" : pick.Name,
            ContentHash = pick.FilesHash,
            CreatedUtc = opened,
            LastUsedUtc = opened,
            UseCount = (int)Math.Min(pick.RunCount, int.MaxValue),
            Origin = ClipOrigin.Everything,
            SourceAppName = "Everything",
            SizeBytes = pick.Size ?? 0,
            FormatNames = [ClipFormatNames.HDrop],
        };
        return new ClipItemViewModel(entry, _ => Task.FromResult<byte[]?>(null), null, pick);
    }

    /// <summary>The underlying entry snapshot (a stand-in for a <see cref="Pick"/>, see <see cref="ForPick"/>).</summary>
    public ClipEntry Entry { get; private set; }

    /// <summary>
    /// The file opened in Everything this card shows, or <see langword="null"/> for a history entry. Pick cards
    /// paste the file (or its path), keep it on Pin, and hide it on Delete — nothing of theirs is stored until then.
    /// </summary>
    public EverythingPick? Pick { get; }

    /// <summary>Entry id.</summary>
    public long Id => Entry.Id;

    /// <summary>Entry kind.</summary>
    public ClipKind Kind => Entry.Kind;

    /// <summary>Display text (already truncated by Core).</summary>
    public string Preview => Entry.Preview;

    /// <summary>Whether the entry is pinned; changes in place when the user toggles it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinVisibility))]
    public partial bool IsPinned { get; set; }

    /// <summary>Source app · relative time · extra facts; refreshed on every flyout show.</summary>
    [ObservableProperty]
    public partial string Caption { get; set; }

    /// <summary>Thumbnail for image entries, loaded on first realization.</summary>
    [ObservableProperty]
    public partial ImageSource? Thumbnail { get; set; }

    /// <summary>Pin badge visibility.</summary>
    public Visibility PinVisibility => IsPinned ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The icons of the groups the entry is in, as symbol-font text (thin-space separated), shown in the
    /// card header so a drop onto a group icon is visibly confirmed; empty when in none.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupBadgesVisibility))]
    public partial string GroupBadges { get; set; }

    /// <summary>Tooltip naming the groups behind <see cref="GroupBadges"/>.</summary>
    [ObservableProperty]
    public partial string GroupBadgesTooltip { get; set; }

    /// <summary>Group badges visibility.</summary>
    public Visibility GroupBadgesVisibility => GroupBadges.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Whether the entry is in the group with <paramref name="groupId"/>.</summary>
    /// <param name="groupId">Group id.</param>
    /// <returns><see langword="true"/> for a member.</returns>
    public bool IsInGroup(long groupId) => Entry.GroupIds.Contains(groupId);

    /// <summary>
    /// Fluent glyph describing the kind. Text that is nothing but paths gets the folder of a file list, since it
    /// is listed in the Files tab next to them. A file opened in Everything shows a document (E8A5), a folder the
    /// folder (both checked by rendering). A command run with Win+R shows the command prompt (E756) wherever it is
    /// listed, so it stands out as runnable (Ctrl+Enter) also outside the Run tab. A shell command (a Pwsh or Cmd tab's
    /// live command, or one kept from there) shows it too. A prompt sent to an AI agent (a Claude or Codex tab's archived
    /// prompt, or one kept from there) shows a speech bubble (<see cref="PromptGlyph"/>).
    /// </summary>
    public string KindGlyph => Pick is { } pick ? (pick.IsFolder ? "\uE8B7" : "\uE8A5") : IsPromptCard ? PromptGlyph : Entry.HasRunHistory || IsShellCommandCard ? "\uE756" : Entry.IsPathText ? "\uE8B7" : Kind switch
    {
        ClipKind.RichText => "\uE8D3", // FontColor
        ClipKind.Link => "\uE71B",     // Link
        ClipKind.Color => "\uE790",    // Color
        ClipKind.Image => "\uE91B",    // Photo
        ClipKind.Files => "\uE8B7",    // Folder
        _ => "\uE8D2",                 // Font
    };

    /// <summary>
    /// Human label of the kind (tooltips, accessibility): "Path"/"Paths" for text that is nothing but paths,
    /// which a screen reader would otherwise announce as plain text inside the Files tab; for a pick, where it
    /// comes from ("File opened in Everything"); and "Win+R command" for a command run with Win+R, so a screen
    /// reader announces that Ctrl+Enter runs it. A shell command names its shell ("PowerShell command"), a prompt its agent
    /// ("Claude Code prompt"), and an unsaved Notepad++ tab's text says so ("Notepad++ tab"; its name is in the caption).
    /// </summary>
    public string KindLabel => Pick is { } pick ? (pick.IsFolder ? UiText.Translate("Folder opened in Everything") : UiText.Translate("File opened in Everything"))
        : Entry.HasRunHistory ? UiText.Translate("Win+R command")
        : Entry.Origin == ClipOrigin.NotepadPlusPlusTab ? UiText.Translate("Notepad++ tab")
        : ShellCommandLabel is { } shellLabel ? shellLabel
        : PromptLabel is { } promptLabel ? promptLabel
        : Entry.IsPathText ? (Entry.PathCount == 1 ? UiText.Translate("Path") : UiText.Translate("Paths")) : Kind switch
    {
        ClipKind.RichText => UiText.Translate("Formatted text"),
        ClipKind.Link => UiText.Translate("Link"),
        ClipKind.Color => UiText.Translate("Color"),
        ClipKind.Image => UiText.Translate("Image"),
        ClipKind.Files => UiText.Translate("Files"),
        _ => UiText.Translate("Text"),
    };

    /// <summary>Text body visibility (text, rich text, links, files).</summary>
    public Visibility TextVisibility => Kind is ClipKind.Image or ClipKind.Color ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Thumbnail visibility.</summary>
    public Visibility ImageVisibility => Kind == ClipKind.Image ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Color swatch visibility.</summary>
    public Visibility ColorVisibility => Kind == ClipKind.Color ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Swatch brush for color entries.</summary>
    public Brush? ColorBrush { get; }

    /// <summary>
    /// Monospace for code-looking text and for text that is nothing but paths; the UI font otherwise — also for a
    /// pick, whose preview is a file name over its folder, not code. A shell command is always monospace: it is code.
    /// </summary>
    public FontFamily BodyFontFamily => Pick is null && (IsShellCommandCard || (Kind != ClipKind.Link && (Entry.IsPathText || CodeHeuristics.LooksLikeCode(Preview)))) ? MonoFont : UiFont;

    /// <summary>
    /// Accessible name for screen readers: the kind, then the preview, joined the interface language's way (a
    /// full-width colon in Chinese). The preview is the user's content and goes in as a value, never translated.
    /// </summary>
    public string AutomationName => UiText.Format($"{KindLabel}: {Preview}");

    /// <summary>
    /// Replaces the snapshot after an update (pin/recency change) without recreating the card.
    /// </summary>
    /// <param name="entry">The fresh snapshot (same id).</param>
    public void Update(ClipEntry entry)
    {
        Entry = entry;
        IsPinned = entry.IsPinned;
        Caption = BuildCaption();
        (GroupBadges, GroupBadgesTooltip) = BuildGroupBadges();
        RefreshCutOff();
    }

    /// <summary>Re-formats the relative time in the caption.</summary>
    public void RefreshCaption() => Caption = BuildCaption();

    /// <summary>
    /// Shows a newer time than the entry's last use in the caption: the time a merged tab (Claude, Codex, Pwsh, Cmd,
    /// Everything) placed the card by, when its content happened again in the other app since it was last used here — a kept
    /// prompt sent again, a kept command typed again, a kept file opened again (<see cref="Core.Presentation.MergedTabOrder{TItem}"/>).
    /// Without it, such a card would read "3 days ago" among rows of minutes ago, and look misplaced.
    /// </summary>
    /// <param name="time">The row's time; <see langword="null"/> or a time not newer than the last use changes nothing.</param>
    public void ShowNewerTime(DateTimeOffset? time)
    {
        if (time is { } newer && newer > Entry.LastUsedUtc && newer != newerTime)
        {
            newerTime = newer;
            Caption = BuildCaption();
        }
    }

    /// <summary>
    /// Re-renders the group badges after groups changed (renamed, new icon, deleted) — the entry snapshot
    /// itself only holds group ids.
    /// </summary>
    /// <param name="lookup">Finds a group by id in the fresh group list.</param>
    public void RefreshGroups(Func<long, ClipGroup?> lookup)
    {
        groupLookup = lookup;
        (GroupBadges, GroupBadgesTooltip) = BuildGroupBadges();
    }

    /// <summary>Builds the badge glyphs and their tooltip from the entry's group ids.</summary>
    /// <returns>Glyph text and tooltip (both empty when the entry is in no known group).</returns>
    private (string Badges, string Tooltip) BuildGroupBadges()
    {
        // A group deleted meanwhile (id without a lookup hit) simply shows no badge.
        var groups = Entry.GroupIds.Select(groupLookup).OfType<ClipGroup>().ToArray();
        if (groups.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        // Group names are the user's own; only the words around them and the list separator follow the interface language.
        var names = string.Join(UiText.Translate(", "), groups.Select(g => g.Name));
        return (string.Join("\u2009", groups.Select(g => g.Glyph)), UiText.Format($"In {names}"));
    }

    /// <summary>
    /// Starts loading the thumbnail if this is an image entry and it has not been requested yet.
    /// Safe to call repeatedly (container recycling calls it on every realization).
    /// </summary>
    /// <remarks>
    /// The image is handed to the card (<see cref="Thumbnail"/>) before its bytes are set: a bitmap that is already shown
    /// when its source arrives is decoded at the size it is drawn (WinUI's decode to render size), about 230 × 128 DIPs in
    /// a card, instead of the thumbnail's full 720 × 400 pixels — a quarter or less of the memory per card, for every image
    /// card realized in the list. A bitmap given its source first is decoded at its natural size, as before 2026-11-12.
    /// </remarks>
    public async void EnsureThumbnail()
    {
        if (thumbnailRequested || !Entry.HasThumbnail)
        {
            return;
        }

        thumbnailRequested = true;
        try
        {
            var bytes = await thumbnailLoader(Id);
            if (bytes is null || bytes.Length == 0)
            {
                return;
            }

            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage();
            Thumbnail = bitmap;
            await bitmap.SetSourceAsync(stream);
        }
        catch (Exception ex)
        {
            // async void: an escaping exception would crash the app; a missing thumbnail is harmless. A bitmap that never got
            // its picture must not stand in for one (the image peek falls back to the thumbnail).
            Thumbnail = null;
            AppLog.Warn($"Thumbnail for entry {Id} failed: {ex.Message}");
        }
    }

    /// <summary>Builds "Source · 5 min ago · extra" ("Everything · 5 min ago · opened 3 times" for a pick).</summary>
    /// <returns>The caption.</returns>
    private string BuildCaption()
    {
        if (Command is { } command)
        {
            return CommandCaption(command);
        }

        if (Prompt is { } prompt)
        {
            return PromptCaption(prompt);
        }

        if (Pick is { } pick)
        {
            // When it was last opened, and how often: Everything keeps one row per file, not one per opening.
            var times = UiText.Plural(pick.RunCount, $"opened once", $"opened {pick.RunCount:N0} times");
            return pick.LastOpened is { } opened
                ? $"Everything · {RelativeTimeFormatter.Format(opened, DateTimeOffset.UtcNow)} · {times}"
                : $"Everything · {times}";
        }

        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(Entry.SourceAppName))
        {
            // Stored in English (the tabs filter by it); a name BetterClipboard made up is shown in the interface language.
            parts.Add(SourceNames.Display(Entry.SourceAppName));
        }
        else if (Entry.Origin == ClipOrigin.Captured)
        {
            // A live copy whose producer could not be identified (protected process, exited too fast).
            parts.Add(UiText.Translate("Unknown app"));
        }

        // The document inside the app, when an integration knows it: a Notepad++ tab's name ("Notepad++ · new 3 · …").
        // Its own statement, after the app's: chained to the app's if/else, a known app without a title read
        // "Notepad++ · Unknown app" (found in the live check, 2026-11-08).
        if (!string.IsNullOrWhiteSpace(Entry.SourceTitle))
        {
            parts.Add(Entry.SourceTitle!);
        }

        // Imported items (Windows never records their producer) show no source at all rather than a
        // placeholder label repeated on every card. In a merged tab the time is the newer of the last use and the time the
        // content happened again in the other app (ShowNewerTime): the time the card's place in the list follows.
        var time = newerTime is { } newer && newer > Entry.LastUsedUtc ? newer : Entry.LastUsedUtc;
        parts.Add(RelativeTimeFormatter.Format(time, DateTimeOffset.UtcNow));

        // A text replaced in place keeps the card's place (its time above); the edit is a time of its own. Shown only while it
        // is the newer of the two: a later copy of the same text moves the card, and then says it all.
        if (Entry.EditedUtc is { } edited && edited > Entry.LastUsedUtc)
        {
            parts.Add(UiText.Format($"edited {RelativeTimeFormatter.Format(edited, DateTimeOffset.UtcNow)}"));
        }

        if (Kind == ClipKind.Image && Entry.ImageWidth is > 0 && Entry.ImageHeight is > 0)
        {
            parts.Add($"{Entry.ImageWidth} × {Entry.ImageHeight}");
        }
        else if (Kind == ClipKind.Files)
        {
            int count = Entry.Preview.Split('\n').Length;
            parts.Add(Entry.Preview.Contains(" more", StringComparison.Ordinal)
                ? UiText.Translate("many files")
                : UiText.Plural(count, $"{count:N0} file", $"{count:N0} files"));
        }
        else if (Entry.IsPathText)
        {
            // Tells why a text card shows up in the Files tab (and wins over "formatted" for a path copied
            // from a web page).
            parts.Add(UiText.Plural(Entry.PathCount, $"path", $"{Entry.PathCount:N0} paths"));
        }
        else if (Entry.HasRichFormats && Kind == ClipKind.RichText)
        {
            parts.Add(UiText.Translate("formatted"));
        }

        // One line of pieces: in a right-to-left language it reads from the app's name, at the right.
        return UiText.JoinLine(" · ", parts);
    }
}
