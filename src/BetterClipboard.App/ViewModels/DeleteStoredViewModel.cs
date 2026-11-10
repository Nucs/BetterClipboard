using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// The <em>Delete stored…</em> dialog of one integration card (CLAUDE.md §2.43; the owner's choice, 2026-11-09: "Parts,
/// counts, examples"): what the integration stored, one part per row with a checkbox, a count, a size and its newest
/// examples; a note on what is never touched; "Also turn off …"; and the deletion of the ticked parts.
/// </summary>
/// <remarks>
/// UI thread only. The preview is read on the pool after the dialog opened (<see cref="LoadAsync"/>), so a large history
/// shows the dialog at once with "Reading what is stored…". The examples are the user's own data shown in their own
/// window; nothing of them is logged.
/// </remarks>
public sealed partial class DeleteStoredViewModel : ObservableObject
{
    private readonly AppController controller;

    /// <summary>Creates the dialog's model; call <see cref="LoadAsync"/> to fill it.</summary>
    /// <param name="controller">App controller (reads the preview, deletes).</param>
    /// <param name="source">The integration whose card was clicked.</param>
    /// <param name="switchOn">Whether its switch is on now: only then is "Also turn off …" offered.</param>
    /// <exception cref="ArgumentNullException"><paramref name="controller"/> is <see langword="null"/>.</exception>
    public DeleteStoredViewModel(AppController controller, StoredSource source, bool switchOn)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        Source = source;
        // Every text that can name a shortcut ("Win+PrtScn", "Win+R") is glued, so a wrapping line keeps it whole.
        var name = StoredText.SourceNameOf(source);
        Title = StoredText.KeepShortcutsTogether(UiText.Format($"Delete what BetterClipboard stored from {name}?"));
        EmptyText = StoredText.KeepShortcutsTogether(UiText.Format($"Nothing is stored from {name}."));
        Note = StoredText.KeepShortcutsTogether(StoredText.NoteOf(source));
        var title = StoredText.SwitchTitleOf(source);
        TurnOffLabel = StoredText.KeepShortcutsTogether(UiText.Format($"Also turn off “{title}”"));
        CanTurnOff = switchOn;
    }

    /// <summary>The integration.</summary>
    public StoredSource Source { get; }

    /// <summary>The dialog's title.</summary>
    public string Title { get; }

    /// <summary>What the dialog says when nothing is stored.</summary>
    public string EmptyText { get; }

    /// <summary>What is never touched, and what comes back while the switch stays on.</summary>
    public string Note { get; }

    /// <summary>The label of the "Also turn off …" checkbox, naming the card's switch.</summary>
    public string TurnOffLabel { get; }

    /// <summary>Whether "Also turn off …" is offered (the switch is on).</summary>
    public bool CanTurnOff { get; }

    /// <summary>The parts that hold anything, in dialog order.</summary>
    public ObservableCollection<StoredPartViewModel> Parts { get; } = [];

    /// <summary>Whether the user ticked "Also turn off …".</summary>
    [ObservableProperty]
    public partial bool TurnOff { get; set; }

    /// <summary>While the preview is being read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete), nameof(HasParts))]
    public partial bool IsLoading { get; set; } = true;

    /// <summary>While the deletion runs (the buttons are disabled).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    public partial bool IsDeleting { get; set; }

    /// <summary>Whether nothing is stored (the dialog says so and offers only Close).</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    /// <summary>The line under the title: how many items, their size, newest and oldest.</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    /// <summary>Why the deletion or the preview failed (empty while all is well).</summary>
    [ObservableProperty]
    public partial string Error { get; set; } = string.Empty;

    /// <summary>Whether there are parts to show (hides the note and the turn-off box while loading or empty).</summary>
    public bool HasParts => !IsLoading && Parts.Count > 0;

    /// <summary>How many items the ticked parts hold together: what the delete button counts.</summary>
    public long SelectedCount => Parts.Where(p => p.IsSelected).Sum(p => p.Count);

    /// <summary>The delete button's text: "Delete 34 items", or plain "Delete" while nothing is ticked.</summary>
    public string PrimaryText
    {
        get
        {
            long count = SelectedCount;
            return count > 0
                ? UiText.Plural(count, $"Delete {count:N0} item", $"Delete {count:N0} items")
                : UiText.Translate("Delete");
        }
    }

    /// <summary>Whether the delete button is enabled: loaded, not deleting, something ticked.</summary>
    public bool CanDelete => !IsLoading && !IsDeleting && SelectedCount > 0;

    /// <summary>
    /// Reads what is stored and fills the parts (and starts their thumbnails). Called once, right after the dialog opened.
    /// </summary>
    /// <param name="cancellationToken">Cancelled when the dialog closes first.</param>
    /// <returns>A task completing when filled; a failure shows in <see cref="Error"/>, a cancellation ends quietly.</returns>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var preview = await controller.PreviewStoredAsync(Source, cancellationToken);
            foreach (var part in preview.Parts)
            {
                var row = new StoredPartViewModel(part);
                row.PropertyChanged += OnPartChanged;
                Parts.Add(row);
                foreach (var thumbnail in row.Thumbnails)
                {
                    _ = thumbnail.LoadAsync(controller.History.GetThumbnailAsync);
                }
            }

            IsEmpty = preview.IsEmpty;
            Summary = preview.IsEmpty ? string.Empty : SummaryOf(preview);
        }
        catch (OperationCanceledException)
        {
            // The dialog closed before the preview was read: nothing to show any more.
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Reading what {Source} stored failed: {ex.Message}");
            Error = UiText.Format($"Could not read what is stored: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            RaiseSelectionChanged();
        }
    }

    /// <summary>
    /// Deletes the ticked parts (the window flips the switch first when "Also turn off …" is ticked). A failure keeps the
    /// dialog open with the reason.
    /// </summary>
    /// <returns><see langword="true"/> when deleted (the dialog may close); <see langword="false"/> after a failure.</returns>
    public async Task<bool> DeleteAsync()
    {
        var parts = Parts.Where(p => p.IsSelected).Select(p => p.Part).ToList();
        if (parts.Count == 0)
        {
            return true;
        }

        IsDeleting = true;
        Error = string.Empty;
        try
        {
            await controller.DeleteStoredAsync(Source, parts);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Delete stored for {Source} failed: {ex.Message}");
            Error = UiText.Format($"Deleting failed: {ex.Message}");
            return false;
        }
        finally
        {
            IsDeleting = false;
        }
    }

    /// <summary>
    /// "34 items stored · 52.1 MB · newest 5 min ago, oldest 3 Oct": every part counted (ticked or not), sizes of the
    /// entries, and the span of time.
    /// </summary>
    /// <param name="preview">The preview.</param>
    /// <returns>The line.</returns>
    private static string SummaryOf(StoredPreview preview)
    {
        long total = preview.Parts.Sum(p => p.Count);
        long bytes = preview.Parts.Sum(p => p.Bytes);
        var pieces = new List<string> { UiText.Plural(total, $"{total:N0} item stored", $"{total:N0} items stored") };
        if (bytes > 0)
        {
            pieces.Add(SettingsViewModel.FormatBytes(bytes));
        }

        var now = DateTimeOffset.UtcNow;
        var newest = preview.Parts.Select(p => p.Newest).OfType<DateTimeOffset>().DefaultIfEmpty().Max();
        var oldest = preview.Parts.Select(p => p.Oldest ?? p.Newest).OfType<DateTimeOffset>().DefaultIfEmpty().Min();
        if (newest != default)
        {
            var newestText = RelativeTimeFormatter.Format(newest, now);
            var oldestText = oldest != default ? RelativeTimeFormatter.Format(oldest, now) : newestText;
            pieces.Add(oldestText == newestText
                ? UiText.Format($"newest {newestText}")
                : UiText.Format($"newest {newestText}, oldest {oldestText}"));
        }

        return UiText.JoinLine(" · ", pieces);
    }

    /// <summary>A part's checkbox changed: the button's count and state follow.</summary>
    /// <param name="sender">The part.</param>
    /// <param name="e">Which property changed.</param>
    private void OnPartChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StoredPartViewModel.IsSelected))
        {
            RaiseSelectionChanged();
        }
    }

    /// <summary>Announces that the ticked count, the button's text and its state may have changed.</summary>
    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(PrimaryText));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(HasParts));
    }
}

/// <summary>One row of the dialog: a part's checkbox, its count and size, and its newest examples.</summary>
public sealed partial class StoredPartViewModel : ObservableObject
{
    /// <summary>Builds the row of a part that holds something.</summary>
    /// <param name="preview">The part's preview.</param>
    /// <exception cref="ArgumentNullException"><paramref name="preview"/> is <see langword="null"/>.</exception>
    public StoredPartViewModel(StoredPartPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        Part = preview.Part;
        Count = preview.Count;
        Label = StoredText.KeepShortcutsTogether(StoredText.LabelOf(preview.Part.Id));
        IsSelected = preview.Part.SelectedByDefault;
        Thumbnails = preview.Examples.Where(e => e.ImageEntryId is not null).Select(e => new StoredThumbnailViewModel(e.ImageEntryId!.Value)).ToArray();
        Lines = preview.Examples.Where(e => e.ImageEntryId is null && !string.IsNullOrWhiteSpace(e.Text)).Select(e => new StoredLineViewModel(e.Text!, e.IsCode)).ToArray();

        var meta = new List<string> { preview.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) };
        if (preview.Sends is { } sends && sends != preview.Count)
        {
            meta.Add(UiText.Plural(sends, $"{sends:N0} send", $"{sends:N0} sends"));
        }

        if (preview.Bytes > 0)
        {
            meta.Add(SettingsViewModel.FormatBytes(preview.Bytes));
        }

        if (!preview.Part.SelectedByDefault && preview.Part.Id == StoredData.ProtectedPartId)
        {
            meta.Add(UiText.Translate("kept unless ticked"));
        }

        Meta = UiText.JoinLine(" · ", meta);
        long shown = Thumbnails.Count + Lines.Count;
        More = preview.Count > shown && shown > 0 ? UiText.Format($"+{preview.Count - shown:N0} more") : string.Empty;
        AccessibleName = UiText.JoinLine(", ", [Label, Meta]);
    }

    /// <summary>The part.</summary>
    public StoredPart Part { get; }

    /// <summary>How many items it holds (what ticking it adds to the button's count).</summary>
    public long Count { get; }

    /// <summary>The checkbox's label ("Screenshots ShareX saved").</summary>
    public string Label { get; }

    /// <summary>"28 · 49.8 MB", "2 · kept unless ticked".</summary>
    public string Meta { get; }

    /// <summary>"+23 more" after the examples, empty when they are all shown.</summary>
    public string More { get; }

    /// <summary>What a screen reader says for the checkbox: the label and the counts.</summary>
    public string AccessibleName { get; }

    /// <summary>Whether "+N more" shows.</summary>
    public Visibility MoreVisibility => More.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The pictures among the newest examples.</summary>
    public IReadOnlyList<StoredThumbnailViewModel> Thumbnails { get; }

    /// <summary>The text lines among the newest examples.</summary>
    public IReadOnlyList<StoredLineViewModel> Lines { get; }

    /// <summary>Whether the row of pictures shows.</summary>
    public Visibility ThumbnailsVisibility => Thumbnails.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Whether the part is ticked (deleted when the user confirms).</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>One picture among a part's examples: its entry's stored thumbnail, loaded after the dialog opened.</summary>
/// <param name="entryId">The entry whose thumbnail it is.</param>
public sealed partial class StoredThumbnailViewModel(long entryId) : ObservableObject
{
    /// <summary>The picture, once loaded (nothing shows before, or when it cannot be read).</summary>
    [ObservableProperty]
    public partial ImageSource? Image { get; set; }

    /// <summary>
    /// Reads and decodes the thumbnail (the same 720×400 PNG the panel's card shows). Fire-and-forget from the UI thread: a
    /// failure leaves the frame empty and is logged by id only.
    /// </summary>
    /// <param name="loader">Reads a thumbnail's bytes by entry id (the history service's).</param>
    /// <returns>A task completing when shown.</returns>
    public async Task LoadAsync(Func<long, Task<byte[]?>> loader)
    {
        try
        {
            var bytes = await loader(entryId);
            if (bytes is not { Length: > 0 })
            {
                return;
            }

            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            Image = bitmap;
        }
        catch (Exception ex)
        {
            // async Task started without await: an escaping exception would go unobserved; a missing example is harmless.
            AppLog.Warn($"Thumbnail of entry {entryId} for Delete stored failed: {ex.Message}");
        }
    }
}

/// <summary>One line among a part's examples: a text's first line, a command, a path or a prompt.</summary>
/// <param name="text">The line.</param>
/// <param name="isCode">Whether it shows in the monospace font (commands and paths, as their cards show them).</param>
public sealed class StoredLineViewModel(string text, bool isCode)
{
    /// <summary>The line.</summary>
    public string Text { get; } = text;

    /// <summary>The font: the app's monospace family for code, XAML's default otherwise.</summary>
    public FontFamily Font { get; } = isCode && Application.Current.Resources.TryGetValue("MonoFontFamily", out var mono) && mono is FontFamily family
        ? family
        : FontFamily.XamlAutoFontFamily;
}
