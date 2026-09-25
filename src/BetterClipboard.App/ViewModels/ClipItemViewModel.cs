using System.Runtime.InteropServices.WindowsRuntime;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Diagnostics;
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
    /// Creates the card model.
    /// </summary>
    /// <param name="entry">The history entry.</param>
    /// <param name="thumbnailLoader">Loads PNG thumbnail bytes by entry id (called at most once, on demand).</param>
    /// <param name="groupLookup">Finds a group by id for the card's group badges; <see langword="null"/> = no badges.</param>
    public ClipItemViewModel(ClipEntry entry, Func<long, Task<byte[]?>> thumbnailLoader, Func<long, ClipGroup?>? groupLookup = null)
    {
        Entry = entry;
        this.thumbnailLoader = thumbnailLoader;
        this.groupLookup = groupLookup ?? (_ => null);
        IsPinned = entry.IsPinned;
        Caption = BuildCaption();
        (GroupBadges, GroupBadgesTooltip) = BuildGroupBadges();

        if (entry.Kind == ClipKind.Color && ColorLiteral.TryParse(entry.Preview, out var argb))
        {
            ColorBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(argb.A, argb.R, argb.G, argb.B));
        }
    }

    /// <summary>The underlying entry snapshot.</summary>
    public ClipEntry Entry { get; private set; }

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

    /// <summary>Fluent glyph describing the kind.</summary>
    public string KindGlyph => Kind switch
    {
        ClipKind.RichText => "\uE8D3", // FontColor
        ClipKind.Link => "\uE71B",     // Link
        ClipKind.Color => "\uE790",    // Color
        ClipKind.Image => "\uE91B",    // Photo
        ClipKind.Files => "\uE8B7",    // Folder
        _ => "\uE8D2",                 // Font
    };

    /// <summary>Human label of the kind (tooltips, accessibility).</summary>
    public string KindLabel => Kind switch
    {
        ClipKind.RichText => "Formatted text",
        ClipKind.Link => "Link",
        ClipKind.Color => "Color",
        ClipKind.Image => "Image",
        ClipKind.Files => "Files",
        _ => "Text",
    };

    /// <summary>Text body visibility (text, rich text, links, files).</summary>
    public Visibility TextVisibility => Kind is ClipKind.Image or ClipKind.Color ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Thumbnail visibility.</summary>
    public Visibility ImageVisibility => Kind == ClipKind.Image ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Color swatch visibility.</summary>
    public Visibility ColorVisibility => Kind == ClipKind.Color ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Swatch brush for color entries.</summary>
    public Brush? ColorBrush { get; }

    /// <summary>Monospace for code-looking text and file paths; the UI font otherwise.</summary>
    public FontFamily BodyFontFamily => Kind != ClipKind.Link && CodeHeuristics.LooksLikeCode(Preview) ? MonoFont : UiFont;

    /// <summary>Accessible name for screen readers.</summary>
    public string AutomationName => $"{KindLabel}: {Preview}";

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
    }

    /// <summary>Re-formats the relative time in the caption.</summary>
    public void RefreshCaption() => Caption = BuildCaption();

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

        return (string.Join("\u2009", groups.Select(g => g.Glyph)), "In " + string.Join(", ", groups.Select(g => g.Name)));
    }

    /// <summary>
    /// Starts loading the thumbnail if this is an image entry and it has not been requested yet.
    /// Safe to call repeatedly (container recycling calls it on every realization).
    /// </summary>
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
            await bitmap.SetSourceAsync(stream);
            Thumbnail = bitmap;
        }
        catch (Exception ex)
        {
            // async void: an escaping exception would crash the app; a missing thumbnail is harmless.
            AppLog.Warn($"Thumbnail for entry {Id} failed: {ex.Message}");
        }
    }

    /// <summary>Builds "Source · 5 min ago · extra".</summary>
    /// <returns>The caption.</returns>
    private string BuildCaption()
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(Entry.SourceAppName))
        {
            parts.Add(Entry.SourceAppName!);
        }
        else if (Entry.Origin == ClipOrigin.Captured)
        {
            // A live copy whose producer could not be identified (protected process, exited too fast).
            parts.Add("Unknown app");
        }

        // Imported items (Windows never records their producer) show no source at all rather than a
        // placeholder label repeated on every card.
        parts.Add(RelativeTimeFormatter.Format(Entry.LastUsedUtc, DateTimeOffset.UtcNow));

        if (Kind == ClipKind.Image && Entry.ImageWidth is > 0 && Entry.ImageHeight is > 0)
        {
            parts.Add($"{Entry.ImageWidth} × {Entry.ImageHeight}");
        }
        else if (Kind == ClipKind.Files)
        {
            int count = Entry.Preview.Split('\n').Length;
            parts.Add(Entry.Preview.Contains(" more", StringComparison.Ordinal) ? "many files" : $"{count} file{(count == 1 ? "" : "s")}");
        }
        else if (Entry.HasRichFormats && Kind == ClipKind.RichText)
        {
            parts.Add("formatted");
        }

        return string.Join(" · ", parts);
    }
}
