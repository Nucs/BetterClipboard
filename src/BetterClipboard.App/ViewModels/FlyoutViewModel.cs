using System.Collections.ObjectModel;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// State of the clipboard flyout: the visible page of history, the search text and filter, the empty
/// state and the footer status. UI thread only.
/// </summary>
/// <remarks>
/// Paging: the first <see cref="PageSize"/> matches load on every show/search; more pages load as the
/// user scrolls near the end (<see cref="LoadMoreAsync"/>). Queries are cancelled when superseded
/// (typing fast), and results of stale queries are dropped so the list never shows mixed results.
/// </remarks>
public sealed partial class FlyoutViewModel : ObservableObject
{
    /// <summary>Items per page; one page comfortably overfills the flyout.</summary>
    public const int PageSize = 60;

    /// <summary>Search placeholder of the regular view (a group view names its group instead).</summary>
    public const string DefaultSearchPlaceholder = "Search everything you've copied…";

    private readonly AppController controller;
    private CancellationTokenSource? queryCancellation;
    private int loaded;
    private bool hasMore;
    private bool loadingMore;
    private bool suppressReload;

    /// <summary>
    /// Creates the view model.
    /// </summary>
    /// <param name="controller">App controller (history access, settings).</param>
    public FlyoutViewModel(AppController controller) => this.controller = controller;

    /// <summary>
    /// Raised after <see cref="ReloadAsync"/> replaced the list (UI thread), so the view can restore a
    /// selection — a reload clears it, and Enter must always have an item to paste.
    /// </summary>
    public event EventHandler? Reloaded;

    /// <summary>The visible cards.</summary>
    public ObservableCollection<ClipItemViewModel> Items { get; } = [];

    /// <summary>Search box text; changes reload (debounced).</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Selected filter pill; changes reload.</summary>
    [ObservableProperty]
    public partial ClipFilter Filter { get; set; }

    /// <summary>Whether the empty-state panel shows.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    /// <summary>Empty-state title.</summary>
    [ObservableProperty]
    public partial string EmptyTitle { get; set; } = string.Empty;

    /// <summary>Empty-state explanation.</summary>
    [ObservableProperty]
    public partial string EmptyMessage { get; set; } = string.Empty;

    /// <summary>Footer text (counts).</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>Whether capture is paused (header toggle).</summary>
    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    /// <summary>The groups column, in column order (reloaded by <see cref="LoadGroupsAsync"/>).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ClipGroup> Groups { get; set; } = [];

    /// <summary>
    /// The group whose items the list shows, or <see langword="null"/> for the regular view (the clipboard
    /// icon); changes reload. Filter pills and search apply inside the group too.
    /// </summary>
    [ObservableProperty]
    public partial long? SelectedGroupId { get; set; }

    /// <summary>Header text after "Clipboard": "› Work" in a group view, empty otherwise.</summary>
    [ObservableProperty]
    public partial string GroupTitle { get; set; } = string.Empty;

    /// <summary>Search box placeholder ("Search in Work…" in a group view).</summary>
    [ObservableProperty]
    public partial string SearchPlaceholder { get; set; } = DefaultSearchPlaceholder;

    /// <summary>Raised (UI thread) after <see cref="Groups"/> was reloaded, so the view rebuilds its group icons.</summary>
    public event EventHandler? GroupsReloaded;

    /// <summary>The selected group's snapshot, or <see langword="null"/> in the regular view (or when it vanished).</summary>
    public ClipGroup? SelectedGroup => SelectedGroupId is { } id ? FindGroup(id) : null;

    /// <summary>Finds a group in the current <see cref="Groups"/> list.</summary>
    /// <param name="id">Group id.</param>
    /// <returns>The group, or <see langword="null"/>.</returns>
    public ClipGroup? FindGroup(long id) => Groups.FirstOrDefault(g => g.Id == id);

    /// <summary>
    /// Reloads the groups column; falls back to the regular view when the selected group no longer exists,
    /// and refreshes the group badges of the visible cards (names and icons may have changed).
    /// </summary>
    /// <returns>A task completing when reloaded (failures are logged, never thrown).</returns>
    public async Task LoadGroupsAsync()
    {
        try
        {
            Groups = await controller.History.GetGroupsAsync();
            if (SelectedGroupId is { } id && FindGroup(id) is null)
            {
                SelectedGroupId = null; // deleted meanwhile: back to everything
            }

            foreach (var item in Items)
            {
                item.RefreshGroups(FindGroup);
            }

            UpdateGroupTexts();
            GroupsReloaded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Loading groups failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resets search/filter for a fresh summon without triggering intermediate reloads.
    /// </summary>
    public void ResetForShow()
    {
        suppressReload = true;
        SearchText = string.Empty;
        Filter = ClipFilter.All;

        // Like the filter, a summon always starts in the regular view: Win+V means "what did I copy last".
        SelectedGroupId = null;
        suppressReload = false;
        IsPaused = controller.Settings.Current.IsCapturePaused;
    }

    /// <summary>
    /// Loads the first page for the current search/filter, replacing the list.
    /// </summary>
    /// <param name="debounce">Wait briefly first (typing) so only the last keystroke queries.</param>
    /// <returns>A task completing when the list is updated (or the query was superseded).</returns>
    public async Task ReloadAsync(bool debounce = false)
    {
        queryCancellation?.Cancel();
        var cancellation = queryCancellation = new CancellationTokenSource();
        try
        {
            if (debounce)
            {
                await Task.Delay(120, cancellation.Token);
            }

            var entries = await controller.History.QueryAsync(CreateQuery(0), cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            Items.Clear();
            foreach (var entry in entries)
            {
                Items.Add(CreateItem(entry));
            }

            loaded = entries.Count;
            hasMore = entries.Count == PageSize;
            UpdateEmptyState();
            Reloaded?.Invoke(this, EventArgs.Empty);
            await UpdateStatusAsync();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer query.
        }
        catch (Exception ex)
        {
            AppLog.Error("Loading the flyout list failed.", ex);
        }
    }

    /// <summary>
    /// Appends the next page if there is one (called when scrolled near the end).
    /// </summary>
    /// <returns>A task completing when appended.</returns>
    public async Task LoadMoreAsync()
    {
        if (!hasMore || loadingMore)
        {
            return;
        }

        loadingMore = true;
        var token = queryCancellation?.Token ?? CancellationToken.None;
        try
        {
            var entries = await controller.History.QueryAsync(CreateQuery(loaded), token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            // Offsets shift when items arrive meanwhile; de-duplicate by id.
            var known = Items.Select(i => i.Id).ToHashSet();
            foreach (var entry in entries.Where(e => known.Add(e.Id)))
            {
                Items.Add(CreateItem(entry));
            }

            loaded += entries.Count;
            hasMore = entries.Count == PageSize;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error("Loading more flyout items failed.", ex);
        }
        finally
        {
            loadingMore = false;
        }
    }

    /// <summary>
    /// Applies an in-place update when possible (pin/recency of a visible card, deletion) to avoid
    /// resetting the scroll position; returns whether a full reload is still needed.
    /// </summary>
    /// <param name="change">The change.</param>
    /// <returns><see langword="true"/> when the caller should reload.</returns>
    public bool TryApplyInPlace(ClipChangedEventArgs change)
    {
        switch (change.Kind)
        {
            case ClipChangeKind.Removed when change.EntryId is { } removedId:
                var removed = Items.FirstOrDefault(i => i.Id == removedId);
                if (removed is not null)
                {
                    Items.Remove(removed);
                    loaded = Math.Max(0, loaded - 1);
                    UpdateEmptyState();
                    _ = UpdateStatusAsync();
                }

                return false;
            case ClipChangeKind.Updated when change.Entry is { } updated:
                var existing = Items.FirstOrDefault(i => i.Id == updated.Id);
                if (SelectedGroupId is { } groupId && !updated.GroupIds.Contains(groupId))
                {
                    // Taken out of the group being viewed ("Remove from group"): it leaves this view at
                    // once, in place, so the scroll position and the neighbors' selection survive.
                    if (existing is not null)
                    {
                        Items.Remove(existing);
                        loaded = Math.Max(0, loaded - 1);
                        UpdateEmptyState();
                        _ = UpdateStatusAsync();
                    }

                    return false;
                }

                if (existing is not null && existing.IsPinned == updated.IsPinned)
                {
                    existing.Update(updated);
                    return false;
                }

                return true;
            default:
                return true;
        }
    }

    /// <summary>Refreshes relative times of the visible cards.</summary>
    public void RefreshCaptions()
    {
        foreach (var item in Items)
        {
            item.RefreshCaption();
        }
    }

    /// <summary>Reloads when the search text changes (debounced).</summary>
    /// <param name="value">New text.</param>
    partial void OnSearchTextChanged(string value)
    {
        if (!suppressReload)
        {
            _ = ReloadAsync(debounce: true);
        }
    }

    /// <summary>Reloads when the filter changes.</summary>
    /// <param name="value">New filter.</param>
    partial void OnFilterChanged(ClipFilter value)
    {
        if (!suppressReload)
        {
            _ = ReloadAsync();
        }
    }

    /// <summary>Retitles and reloads when another group (or the regular view) is picked.</summary>
    /// <param name="value">New group id.</param>
    partial void OnSelectedGroupIdChanged(long? value)
    {
        UpdateGroupTexts();
        if (!suppressReload)
        {
            _ = ReloadAsync();
        }
    }

    /// <summary>Header suffix and search placeholder for the current view.</summary>
    private void UpdateGroupTexts()
    {
        var group = SelectedGroup;
        GroupTitle = group is null ? string.Empty : "› " + group.Name;
        SearchPlaceholder = group is null ? DefaultSearchPlaceholder : $"Search in {group.Name}…";
    }

    /// <summary>Builds the query for a page.</summary>
    /// <param name="offset">Rows to skip.</param>
    /// <returns>The query.</returns>
    private ClipQuery CreateQuery(int offset) => new()
    {
        SearchText = SearchText,
        Filter = Filter,
        Offset = offset,
        Limit = PageSize,
        PinnedFirst = controller.Settings.Current.PinnedOnTop,
        GroupId = SelectedGroupId,
    };

    /// <summary>Wraps an entry in a card model.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The card model.</returns>
    private ClipItemViewModel CreateItem(ClipEntry entry) => new(entry, controller.History.GetThumbnailAsync, FindGroup);

    /// <summary>Chooses the empty-state wording for the current search/filter.</summary>
    private void UpdateEmptyState()
    {
        IsEmpty = Items.Count == 0;
        if (!IsEmpty)
        {
            return;
        }

        if (SelectedGroup is { } group)
        {
            bool searching = !string.IsNullOrWhiteSpace(SearchText);
            EmptyTitle = searching ? "No matches" : $"Nothing in {group.Name} yet";
            EmptyMessage = searching
                ? $"Nothing in {group.Name} contains “{SearchText.Trim()}”."
                : $"Open the full history (the clipboard icon above), then drag cards onto the {group.Name} icon.";
        }
        else if (!string.IsNullOrWhiteSpace(SearchText))
        {
            EmptyTitle = "No matches";
            EmptyMessage = $"Nothing in your history contains “{SearchText.Trim()}”.";
        }
        else if (Filter == ClipFilter.Pinned)
        {
            EmptyTitle = "Nothing pinned yet";
            EmptyMessage = "Pin items (Ctrl+P) to keep them at hand forever.";
        }
        else if (Filter == ClipFilter.ShareX)
        {
            EmptyTitle = "No ShareX screenshots yet";
            EmptyMessage = controller.Settings.Current.ImportShareXScreenshots
                ? "Take a screenshot with ShareX — it shows up here the moment ShareX saves it."
                : "Turn on “Import ShareX screenshots” in Settings to collect them here. Copies from ShareX still show up.";
        }
        else if (Filter != ClipFilter.All)
        {
            EmptyTitle = "Nothing here yet";
            EmptyMessage = "Items of this kind will show up as soon as you copy one.";
        }
        else
        {
            EmptyTitle = "Your clipboard history is empty";
            EmptyMessage = "Copy something — it will be kept here, even after a restart.";
        }
    }

    /// <summary>Updates the footer counts.</summary>
    /// <returns>A task completing when updated.</returns>
    private async Task UpdateStatusAsync()
    {
        try
        {
            if (SelectedGroup is { } group)
            {
                StatusText = $"{group.ItemCount:N0} in {group.Name}";
                return;
            }

            var stats = await controller.History.GetStatsAsync();
            StatusText = stats.PinnedCount > 0
                ? $"{stats.Count:N0} items · {stats.PinnedCount:N0} pinned"
                : $"{stats.Count:N0} items";
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Reading history stats failed: {ex.Message}");
        }
    }
}
