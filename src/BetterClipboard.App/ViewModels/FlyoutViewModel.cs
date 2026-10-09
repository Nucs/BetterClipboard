using System.Collections.ObjectModel;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Everything;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Storage;
using BetterClipboard.Windows.Integrations;
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

    /// <summary>Search placeholder of the Everything tab, where the words also narrow Everything's run history.</summary>
    public const string EverythingSearchPlaceholder = "Search files you opened in Everything…";

    private readonly AppController controller;
    private CancellationTokenSource? queryCancellation;

    /// <summary>The latest load started by <see cref="ReloadAsync"/> (it never throws), for <see cref="SettleSearchAsync"/> to await.</summary>
    private Task currentReload = Task.CompletedTask;

    /// <summary>
    /// The search text the shown list was loaded for, or <see langword="null"/> before the first load: when it differs from
    /// <see cref="SearchText"/>, a typed search is still waiting for its debounce.
    /// </summary>
    private string? loadedSearchText;
    private int loaded;
    private bool hasMore;
    private bool loadingMore;
    private bool suppressReload;

    /// <summary>The picks behind the Everything tab's last load (their source and problem word its empty state and footer).</summary>
    private EverythingPicksResult? lastPicks;

    /// <summary>
    /// Counts the history changes the view reported (<see cref="MarkHistoryChanged"/>): a load remembers the count it
    /// started at, so a change that arrives while it runs still leaves the list marked stale.
    /// </summary>
    private int historyVersion;

    /// <summary>
    /// What the shown list was loaded for — the view and <see cref="historyVersion"/> at the start of the latest load that
    /// finished — or <see langword="null"/> before the first load. <see cref="IsListCurrent"/> compares it with now.
    /// </summary>
    private (ViewKey View, int HistoryVersion)? loadedState;

    /// <summary>
    /// Creates the view model, with the search toggles as the user last left them.
    /// </summary>
    /// <param name="controller">App controller (history access, settings).</param>
    public FlyoutViewModel(AppController controller)
    {
        this.controller = controller;
        ApplySavedSearchOptions();
    }

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

    /// <summary>
    /// The search box's "Aa" toggle (<see cref="SearchOptions.MatchCase"/>). Changes are remembered in settings and
    /// reload a search in progress (see <see cref="OnSearchOptionChanged"/>).
    /// </summary>
    [ObservableProperty]
    public partial bool MatchCase { get; set; }

    /// <summary>The search box's "W" toggle (<see cref="SearchOptions.WholeWord"/>); remembered like <see cref="MatchCase"/>.</summary>
    [ObservableProperty]
    public partial bool WholeWord { get; set; }

    /// <summary>The search box's ".*" toggle (<see cref="SearchOptions.Regex"/>); remembered like <see cref="MatchCase"/>.</summary>
    [ObservableProperty]
    public partial bool UseRegex { get; set; }

    /// <summary>The three toggles as the store's <see cref="SearchOptions"/> (what <see cref="CreateQuery"/> sends).</summary>
    public SearchOptions CurrentSearchOptions =>
        (MatchCase ? SearchOptions.MatchCase : SearchOptions.None)
        | (WholeWord ? SearchOptions.WholeWord : SearchOptions.None)
        | (UseRegex ? SearchOptions.Regex : SearchOptions.None);

    /// <summary>
    /// Whether the regular expression in the search box is why the list is empty: it does not parse
    /// (<see cref="SearchPatternException"/>) or ran out of time (<see cref="SearchTooSlowException"/>). The ".*" toggle
    /// shows it with a red outline while it is set.
    /// </summary>
    /// <remarks>
    /// Set by <see cref="ShowSearchProblem"/>; cleared by the next load that succeeds, whatever changed (the pattern,
    /// a toggle, the tab), and by <see cref="ResetForShow"/>. Not cleared at the start of a load: while a still-invalid
    /// pattern is being typed, the outline would flicker back to normal for every keystroke's debounce.
    /// </remarks>
    [ObservableProperty]
    public partial bool HasPatternError { get; set; }

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

    /// <summary>
    /// The footer's key hints for the current tab (<see cref="DefaultKeyHint"/>, or <see cref="RunKeyHint"/> in the
    /// Run tab, where Ctrl+Enter runs the selected command).
    /// </summary>
    [ObservableProperty]
    public partial string KeyHint { get; set; } = DefaultKeyHint;

    /// <summary>The footer's key hints everywhere but the Run tab.</summary>
    public const string DefaultKeyHint = "↵ paste · ⇧↵ plain text · Ctrl+P pin · Del delete";

    /// <summary>The Run tab's key hints: Enter still pastes; Ctrl+Enter runs like Win+R, Ctrl+Shift+Enter as administrator.</summary>
    public const string RunKeyHint = "↵ paste · Ctrl+↵ run · Ctrl+⇧↵ run as admin";

    /// <summary>Whether capture is paused (header toggle).</summary>
    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    /// <summary>The groups column, in column order (reloaded by <see cref="LoadGroupsAsync"/>).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ClipGroup> Groups { get; set; } = [];

    /// <summary>
    /// The groups whose items the list shows — one, or several merged into one list (each item once, in the list's usual
    /// order) — or <see cref="GroupSelection.None"/> for the regular view (the clipboard icon). Filter pills, the search
    /// and its toggles apply inside the groups too.
    /// </summary>
    /// <remarks>
    /// Changed through <see cref="ClickGroup"/> (plain, Ctrl and Shift clicks on the icons), <see cref="ToggleGroupInView"/>
    /// (the icon menu), <see cref="ShowAllHistory"/> and <see cref="RemoveGroupFromView"/>; a reloaded column drops
    /// deleted groups (<see cref="LoadGroupsAsync"/>). A new selection of the same groups (only its Shift anchor moved)
    /// raises <see cref="ObservableObject.PropertyChanged"/> but does not reload
    /// (<see cref="OnGroupSelectionChanged(GroupSelection, GroupSelection)"/>).
    /// </remarks>
    [ObservableProperty]
    public partial GroupSelection GroupSelection { get; set; } = GroupSelection.None;

    /// <summary>Header text after "Clipboard": "› Work" or "› Work + Home" in a group view, empty otherwise.</summary>
    [ObservableProperty]
    public partial string GroupTitle { get; set; } = string.Empty;

    /// <summary>Search box placeholder ("Search in Work…" or "Search in Work + Home…" in a group view).</summary>
    [ObservableProperty]
    public partial string SearchPlaceholder { get; set; } = DefaultSearchPlaceholder;

    /// <summary>
    /// Whether the empty Everything tab offers "Start Everything": it is not running, nothing could be shown, and
    /// an installed (or earlier seen) Everything can be started.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowStartEverything { get; set; }

    /// <summary>
    /// Whether the list is the Everything tab's merged view: the Everything filter in the regular view (in a group
    /// view the filter only narrows the groups' stored entries — live picks are in no group).
    /// </summary>
    public bool IsEverythingView => Filter == ClipFilter.Everything && GroupSelection.IsEmpty;

    /// <summary>Whether the list shows groups (one or several) rather than the whole history.</summary>
    public bool IsGroupView => !GroupSelection.IsEmpty;

    /// <summary>
    /// Whether the list already shows what a load would show now: it was loaded for the current search, tab, groups and
    /// search toggles, and no history change was reported since that load started. A summon then shows the list as it is,
    /// without the reload that used to rebuild every card after the panel appeared.
    /// </summary>
    /// <remarks>
    /// Only views of stored history qualify. The Everything, shell and prompt tabs also show what other apps keep (picks,
    /// shell histories, the prompt archive), which no history event announces, so they always reload — a summon never
    /// starts in them anyway (it shows "All").
    /// </remarks>
    public bool IsListCurrent =>
        !IsEverythingView && !IsShellView && !IsPromptView
        && loadedState is { } state && state.View == CurrentViewKey && state.HistoryVersion == historyVersion;

    /// <summary>Whether <see cref="LoadGroupsAsync"/> succeeded once (until then the cards show no group badges).</summary>
    public bool GroupsLoaded { get; private set; }

    /// <summary>The view a load is made for (see <see cref="IsListCurrent"/>).</summary>
    private ViewKey CurrentViewKey => new(SearchText, Filter, string.Join(",", GroupSelection.Ids), CurrentSearchOptions);

    /// <summary>
    /// Notes that the history changed (an entry added, updated, removed or reordered), whether or not the change was applied
    /// to the shown cards in place: <see cref="IsListCurrent"/> is then false until the next load that starts after it.
    /// </summary>
    public void MarkHistoryChanged() => historyVersion++;

    /// <summary>Raised (UI thread) after <see cref="Groups"/> was reloaded, so the view rebuilds its group icons.</summary>
    public event EventHandler? GroupsReloaded;

    /// <summary>
    /// Snapshots of the groups shown, in column order; empty in the regular view. A group that vanished from
    /// <see cref="Groups"/> before <see cref="LoadGroupsAsync"/> took it out of the selection is skipped.
    /// </summary>
    public IReadOnlyList<ClipGroup> SelectedGroups => [.. GroupSelection.Ids.Select(FindGroup).OfType<ClipGroup>()];

    /// <summary>Finds a group in the current <see cref="Groups"/> list.</summary>
    /// <param name="id">Group id.</param>
    /// <returns>The group, or <see langword="null"/>.</returns>
    public ClipGroup? FindGroup(long id) => Groups.FirstOrDefault(g => g.Id == id);

    /// <summary>
    /// A click on a group icon, with the modifier keys held at that moment: a plain click shows that group alone (or, on
    /// the only group shown, the whole history again), Ctrl adds the group to the groups shown or takes it out, Shift
    /// shows the run of groups from the last clicked one, Ctrl+Shift adds that run (<see cref="GroupSelection.Click"/>).
    /// </summary>
    /// <param name="groupId">The clicked group.</param>
    /// <param name="ctrl">Ctrl was down.</param>
    /// <param name="shift">Shift was down.</param>
    public void ClickGroup(long groupId, bool ctrl, bool shift) => GroupSelection = GroupSelection.Click(GroupColumn(), groupId, ctrl, shift);

    /// <summary>
    /// Adds a group to the groups shown, or takes it out — a Ctrl+click for the icon menu's "Add to view" / "Remove from
    /// view" (touch, pen and screen readers have no Ctrl+click).
    /// </summary>
    /// <param name="groupId">The group.</param>
    public void ToggleGroupInView(long groupId) => GroupSelection = GroupSelection.Toggle(GroupColumn(), groupId);

    /// <summary>Back to the regular view over the whole history (the logo, closing the groups column).</summary>
    public void ShowAllHistory() => GroupSelection = GroupSelection.None;

    /// <summary>
    /// Takes a group out of the view without touching the other groups shown (it is about to be deleted); the regular
    /// view when it was the only one. No-op when it is not shown.
    /// </summary>
    /// <param name="groupId">The group.</param>
    public void RemoveGroupFromView(long groupId) => GroupSelection = GroupSelection.Without(groupId);

    /// <summary>
    /// Reloads the groups column; drops groups that no longer exist from the view (none left: back to the regular
    /// view), refreshes the group badges of the visible cards (names and icons may have changed) and, in a group view,
    /// the footer's count (memberships may have changed).
    /// </summary>
    /// <returns>A task completing when reloaded (failures are logged, never thrown).</returns>
    public async Task LoadGroupsAsync()
    {
        try
        {
            Groups = await controller.History.GetGroupsAsync();
            GroupsLoaded = true;

            // Deleted meanwhile: out of the view; Retain hands back the same instance when nothing vanished, so an
            // unchanged view is not reloaded.
            GroupSelection = GroupSelection.Retain(GroupColumn());

            foreach (var item in Items)
            {
                item.RefreshGroups(FindGroup);
            }

            UpdateGroupTexts();
            GroupsReloaded?.Invoke(this, EventArgs.Empty);

            // A membership change ("Remove from Work", a drop) reaches the list before the groups reload, so the footer
            // it refreshed still counted from the old snapshot (one group's count comes from Groups).
            if (IsGroupView)
            {
                await UpdateStatusAsync();
            }
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
        GroupSelection = GroupSelection.None;
        suppressReload = false;
        IsPaused = controller.Settings.Current.IsCapturePaused;

        // Unlike the text, the toggles stay as they were left (re-read: settings.json may have been edited meanwhile).
        ApplySavedSearchOptions();

        // The search box starts empty, and an empty search has no pattern to be wrong.
        HasPatternError = false;
    }

    /// <summary>
    /// Loads the first page for the current search/filter, replacing the list.
    /// </summary>
    /// <param name="debounce">Wait briefly first (typing) so only the last keystroke queries.</param>
    /// <returns>A task completing when the list is updated (or the query was superseded); it never throws.</returns>
    public Task ReloadAsync(bool debounce = false) => currentReload = ReloadCoreAsync(debounce);

    /// <summary>
    /// Makes the list match the search box before a paste with Enter: when the text typed last is still waiting for its
    /// debounce (or was never loaded), the search runs now; when a load is already running, it is awaited.
    /// </summary>
    /// <remarks>
    /// Found in the QA pass (2026-10-09): "delta" typed and Enter pressed at once pasted the newest item, because the search
    /// runs 120 ms after the last key and Enter pasted whatever was selected before. A failed search (an invalid pattern)
    /// leaves the list empty, so nothing is pasted then.
    /// </remarks>
    /// <returns>A task completing when the list shows the current search's results; it never throws.</returns>
    public Task SettleSearchAsync() =>
        string.Equals(loadedSearchText, SearchText, StringComparison.Ordinal) ? currentReload : ReloadAsync();

    /// <summary>The body of <see cref="ReloadAsync"/>.</summary>
    /// <param name="debounce">Wait briefly first (typing) so only the last keystroke queries.</param>
    /// <returns>A task completing when the list is updated (or the query was superseded); it never throws.</returns>
    private async Task ReloadCoreAsync(bool debounce)
    {
        queryCancellation?.Cancel();
        var cancellation = queryCancellation = new CancellationTokenSource();
        try
        {
            if (debounce)
            {
                await Task.Delay(120, cancellation.Token);
            }

            // The text this load searches for: what SettleSearchAsync compares with the box once the list is in. The view and
            // the history version are taken at the same moment: a change reported after this point marks the result stale.
            var search = SearchText;
            var view = CurrentViewKey;
            int version = historyVersion;
            if (IsShellView)
            {
                // A shell tab: kept commands merged with the shell's live ones (FlyoutViewModel.Shells.cs), no paging.
                await ReloadShellAsync(cancellation.Token);
                MarkLoaded(search, cancellation, view, version);
                return;
            }

            if (IsPromptView)
            {
                // A prompt tab: kept prompts, then the archive one card per text, paged (FlyoutViewModel.Prompts.cs).
                await ReloadPromptsAsync(cancellation.Token);
                MarkLoaded(search, cancellation, view, version);
                return;
            }

            if (IsEverythingView)
            {
                // Two bounded lists merged (stored entries, Everything's picks): no paging.
                var rows = await LoadEverythingRowsAsync(cancellation.Token);
                if (cancellation.IsCancellationRequested)
                {
                    return;
                }

                Items.Clear();
                long syntheticId = -1;
                foreach (var row in rows)
                {
                    Items.Add(row.Entry is { } stored ? CreateItem(stored) : ClipItemViewModel.ForPick(row.Pick!, syntheticId--));
                }

                loaded = Items.Count;
                hasMore = false;
                HasPatternError = false;
                UpdateEmptyState();
                MarkLoaded(search, cancellation, view, version);
                Reloaded?.Invoke(this, EventArgs.Empty);
                await UpdateStatusAsync();
                return;
            }

            var entries = await controller.History.QueryAsync(CreateQuery(0), cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            // Patched, not rebuilt: unchanged cards keep their layout and decoded thumbnails (see ApplyEntries).
            ApplyEntries(entries);
            loaded = entries.Count;
            hasMore = entries.Count == PageSize;
            HasPatternError = false;
            UpdateEmptyState();
            MarkLoaded(search, cancellation, view, version);
            Reloaded?.Invoke(this, EventArgs.Empty);
            await UpdateStatusAsync();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer query.
        }
        catch (SearchPatternException ex)
        {
            // An unfinished pattern ("(foo") is an ordinary moment while typing, not a failure worth logging: say what
            // is wrong, and how to search for the characters themselves. A superseded query's verdict is stale.
            if (!cancellation.IsCancellationRequested)
            {
                ShowSearchProblem("Not a valid regular expression", $"{ex.Reason} Turn off .* (Alt+E) to search for these characters as they are.");
            }
        }
        catch (SearchTooSlowException)
        {
            if (!cancellation.IsCancellationRequested)
            {
                AppLog.Info("A search's regular expression ran out of time on an item; the search was stopped.");
                ShowSearchProblem(
                    "This regular expression is too slow",
                    $"It needed more than {SearchMatcher.MatchTimeout.TotalMilliseconds:0} ms on one item (it backtracks heavily). Simplify it, or turn off .* (Alt+E).");
            }
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
            if (IsPromptView)
            {
                // The archive pages by its own offset (the kept prompts above it are loaded once).
                await LoadMorePromptsAsync(token);
                return;
            }

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
        catch (SearchTooSlowException)
        {
            // The first page matched in time, a later entry did not: keep what is shown, stop paging.
            hasMore = false;
            AppLog.Info("A search's regular expression ran out of time while paging; no more items are loaded for it.");
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
                if (IsGroupView && !updated.GroupIds.Any(GroupSelection.Contains))
                {
                    // Taken out of the groups being viewed ("Remove from group"): it leaves this view at once, in
                    // place, so the scroll position and the neighbors' selection survive. In a merged view only
                    // once it is in none of them — still in another group shown, it stays (its badges update below).
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

    /// <summary>
    /// Takes a card out of the list in place (a hidden or forgotten Everything pick, which no history event
    /// announces), keeping the scroll position; the empty state and footer follow.
    /// </summary>
    /// <param name="item">The card.</param>
    public void RemoveItem(ClipItemViewModel item)
    {
        if (Items.Remove(item))
        {
            loaded = Math.Max(0, loaded - 1);
            UpdateEmptyState();
            _ = UpdateStatusAsync();
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

    /// <summary>
    /// Records that the list now shows the results for <paramref name="search"/> in <paramref name="view"/> as of history
    /// version <paramref name="version"/> — unless a newer load took over meanwhile, whose own result is the one that counts.
    /// </summary>
    /// <param name="search">The search text the finished load used.</param>
    /// <param name="cancellation">That load's cancellation (set when a newer load superseded it).</param>
    /// <param name="view">The view the load was made for.</param>
    /// <param name="version">The history version when the load started (see <see cref="IsListCurrent"/>).</param>
    private void MarkLoaded(string search, CancellationTokenSource cancellation, ViewKey view, int version)
    {
        if (!cancellation.IsCancellationRequested)
        {
            loadedSearchText = search;
            loadedState = (view, version);
        }
    }

    /// <summary>
    /// Makes <see cref="Items"/> show <paramref name="entries"/> in their order, keeping the card of every entry that is
    /// already shown (its data updated in place) and creating cards only for entries that are new to the list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A reload used to clear the list and add a new card for every entry, so each card was laid out and drawn again, each
    /// image card decoded its thumbnail again, and the list played its add animations — after the panel was already on
    /// screen. Patching the collection (remove what left, move what moved, insert what is new) leaves an unchanged card
    /// alone, which is what lets the hidden panel refresh its list cheaply after every copy (<c>ClipboardFlyout.Summon.cs</c>).
    /// </para>
    /// <para>
    /// Only cards of stored entries are reused. A card that stands for something not stored — an Everything pick, a shell
    /// command, an archived prompt — has an id that means nothing outside the load that made it, so it is never kept.
    /// </para>
    /// </remarks>
    /// <param name="entries">The entries to show, in order, each id once (a query result).</param>
    private void ApplyEntries(IReadOnlyList<ClipEntry> entries)
    {
        var reusable = new Dictionary<long, ClipItemViewModel>();
        foreach (var item in Items)
        {
            if (item.Pick is null && item.Command is null && item.Prompt is null)
            {
                reusable.TryAdd(item.Id, item);
            }
        }

        var wanted = new List<ClipItemViewModel>(entries.Count);
        foreach (var entry in entries)
        {
            if (reusable.Remove(entry.Id, out var card))
            {
                // Same entry, maybe newer data (used again, pinned, regrouped); Update also recomputes the relative time.
                card.Update(entry);
                wanted.Add(card);
            }
            else
            {
                wanted.Add(CreateItem(entry));
            }
        }

        // Removes what left, moves what moved, inserts what is new; an unchanged list raises no notification at all.
        CollectionPatch.Apply(Items, wanted);
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

    /// <summary>Reloads when the filter changes (and retitles the search box: the Everything tab searches Everything too).</summary>
    /// <param name="value">New filter.</param>
    partial void OnFilterChanged(ClipFilter value)
    {
        UpdateGroupTexts();

        // The Run tab is where Ctrl+Enter runs a command (it works on any card with a run time, but only the
        // Run tab has room to say so). In a shell tab Delete hides a command rather than deleting anything.
        KeyHint = value == ClipFilter.Run ? RunKeyHint
            : value is ClipFilter.PowerShell or ClipFilter.Cmd ? ShellKeyHint
            : value is ClipFilter.ClaudeCode or ClipFilter.Codex ? PromptKeyHint
            : DefaultKeyHint;
        if (!suppressReload)
        {
            _ = ReloadAsync();
        }
    }

    /// <summary>
    /// Retitles and reloads when other groups (or the regular view) are picked. A selection of the same groups whose
    /// Shift anchor alone moved (e.g. a Shift+click on the run already shown) shows the same list: nothing to do.
    /// </summary>
    /// <param name="oldValue">The groups shown before.</param>
    /// <param name="newValue">The groups shown now.</param>
    partial void OnGroupSelectionChanged(GroupSelection oldValue, GroupSelection newValue)
    {
        if (oldValue.HasSameIds(newValue))
        {
            return;
        }

        UpdateGroupTexts();
        if (!suppressReload)
        {
            _ = ReloadAsync();
        }
    }

    /// <summary>The "Aa" toggle changed (click, Alt+C).</summary>
    /// <param name="value">New state.</param>
    partial void OnMatchCaseChanged(bool value) => OnSearchOptionChanged();

    /// <summary>The "W" toggle changed (click, Alt+W).</summary>
    /// <param name="value">New state.</param>
    partial void OnWholeWordChanged(bool value) => OnSearchOptionChanged();

    /// <summary>The ".*" toggle changed (click, Alt+E).</summary>
    /// <param name="value">New state.</param>
    partial void OnUseRegexChanged(bool value) => OnSearchOptionChanged();

    /// <summary>
    /// A search toggle changed: remember all three, and re-run the search at once (no debounce: a click is one
    /// deliberate change, not a burst of keystrokes).
    /// </summary>
    /// <remarks>
    /// With an empty search box the toggles change nothing that is shown, so the list is left alone — a reload would
    /// only throw away the scroll position and the selection. Skipped entirely while <see cref="ApplySavedSearchOptions"/>
    /// restores them (nothing new to save, and the summon reloads anyway).
    /// </remarks>
    private void OnSearchOptionChanged()
    {
        if (suppressReload)
        {
            return;
        }

        try
        {
            controller.Settings.Update(s => s with { SearchMatchCase = MatchCase, SearchWholeWord = WholeWord, SearchUseRegex = UseRegex });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The store keeps the change in memory; it only fails to reach the next start.
            AppLog.Warn($"Saving the search toggles failed: {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            _ = ReloadAsync();
        }
    }

    /// <summary>Sets the three toggles from settings without saving them back or reloading.</summary>
    private void ApplySavedSearchOptions()
    {
        var settings = controller.Settings.Current;
        bool wasSuppressed = suppressReload;
        suppressReload = true;
        MatchCase = settings.SearchMatchCase;
        WholeWord = settings.SearchWholeWord;
        UseRegex = settings.SearchUseRegex;
        suppressReload = wasSuppressed;
    }

    /// <summary>
    /// Header suffix and search placeholder for the current view: every group shown in the header ("› Work + Home",
    /// trimmed by the header), a short name in the placeholder ("Search in Work + Home…", or "Search in 4 groups…"
    /// when the names would not fit; see <see cref="GroupViewText"/>).
    /// </summary>
    private void UpdateGroupTexts()
    {
        var groups = SelectedGroups;
        GroupTitle = groups.Count == 0 ? string.Empty : "› " + GroupViewText.Title(groups);
        SearchPlaceholder = groups.Count > 0 ? $"Search in {GroupViewText.ShortName(groups)}…"
            : IsEverythingView ? EverythingSearchPlaceholder
            : IsShellView ? ShellSearchPlaceholder
            : IsPromptView ? PromptSearchPlaceholder(CurrentAgent)
            : DefaultSearchPlaceholder;
    }

    /// <summary>The ids of the groups column, top to bottom (what <see cref="GroupSelection"/> orders and runs by).</summary>
    /// <returns>A fresh list (the column may be reloaded while a caller holds it).</returns>
    private List<long> GroupColumn() => [.. Groups.Select(g => g.Id)];

    /// <summary>
    /// Loads the Everything tab's rows: stored Everything entries and Everything's picks, both narrowed by the search
    /// words, merged by <see cref="EverythingTab.Merge"/> without hidden or forgotten picks.
    /// </summary>
    /// <param name="cancellationToken">Cancels a superseded load.</param>
    /// <returns>The rows in display order.</returns>
    /// <exception cref="OperationCanceledException">Superseded by a newer load.</exception>
    private async Task<IReadOnlyList<EverythingTabRow>> LoadEverythingRowsAsync(CancellationToken cancellationToken)
    {
        var history = controller.History;
        var storedTask = history.QueryAsync(new ClipQuery
        {
            SearchText = SearchText,
            Filter = ClipFilter.Everything,
            Limit = EverythingTab.MaxStoredEntries,
            PinnedFirst = false, // the merge orders both sources together
        }, cancellationToken);

        var picks = controller.Everything is { } everything
            ? await everything.GetPicksAsync(SearchText, EverythingTab.MaxPicks, cancellationToken)
            : new EverythingPicksResult([], EverythingPicksSource.None, null);
        var stored = await storedTask;
        var hidden = EverythingTab.ParseHidden(await history.GetStateValueAsync(EverythingTab.HiddenStateName));
        var forgotten = await history.FindForgottenAsync(picks.Picks.Select(p => p.FilesHash).ToList());
        lastPicks = picks;
        return EverythingTab.Merge(stored, picks.Picks, hidden, forgotten, controller.Settings.Current.PinnedOnTop);
    }

    /// <summary>Builds the query for a page.</summary>
    /// <param name="offset">Rows to skip.</param>
    /// <returns>The query.</returns>
    private ClipQuery CreateQuery(int offset) => new()
    {
        SearchText = SearchText,
        SearchOptions = CurrentSearchOptions,
        Filter = Filter,
        Offset = offset,
        Limit = PageSize,
        PinnedFirst = controller.Settings.Current.PinnedOnTop,

        // Several groups are merged by the store: each item once, ordered like any other page.
        GroupIds = GroupSelection.IsEmpty ? null : GroupSelection.Ids,
    };

    /// <summary>
    /// Describes the current search for the empty state: the verb, the text and the toggles that shape it, e.g.
    /// <c>contains “foo bar” (whole words, match case)</c> or <c>matches “^\d+$” (regular expression)</c> — a toggle
    /// left on is the likeliest reason a search finds nothing, so it is named.
    /// </summary>
    /// <returns>The phrase, to follow "Nothing in …".</returns>
    private string DescribeSearch()
    {
        var qualifiers = new List<string>(3);
        if (UseRegex)
        {
            qualifiers.Add("regular expression");
        }

        if (WholeWord)
        {
            qualifiers.Add(UseRegex ? "whole words only" : "whole words");
        }

        if (MatchCase)
        {
            qualifiers.Add("match case");
        }

        // A pattern is shown as typed (its spaces may matter); words are trimmed like the search trims them.
        var phrase = UseRegex ? $"matches “{SearchText}”" : $"contains “{SearchText.Trim()}”";
        return qualifiers.Count == 0 ? phrase : $"{phrase} ({string.Join(", ", qualifiers)})";
    }

    /// <summary>
    /// Replaces the list with an explanation instead of results: the search cannot run (a pattern that does not parse)
    /// or was abandoned (too slow). Goes through <see cref="UpdateEmptyState"/> first, so everything an empty list
    /// resets is reset, then words the panel for the problem and marks the ".*" toggle (<see cref="HasPatternError"/>).
    /// </summary>
    /// <param name="title">The empty state's title.</param>
    /// <param name="message">What went wrong and what to do.</param>
    private void ShowSearchProblem(string title, string message)
    {
        Items.Clear();
        loaded = 0;
        hasMore = false;
        UpdateEmptyState();
        EmptyTitle = title;
        EmptyMessage = message;

        // Both callers are pattern problems (only a regular expression can fail to parse or time out).
        HasPatternError = true;
        Reloaded?.Invoke(this, EventArgs.Empty);
        _ = UpdateStatusAsync();
    }

    /// <summary>Wraps an entry in a card model.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The card model.</returns>
    private ClipItemViewModel CreateItem(ClipEntry entry) => new(entry, controller.History.GetThumbnailAsync, FindGroup);

    /// <summary>Chooses the empty-state wording for the current search/filter.</summary>
    private void UpdateEmptyState()
    {
        IsEmpty = Items.Count == 0;
        ShowStartEverything = false;
        if (!IsEmpty)
        {
            return;
        }

        if (IsEverythingView)
        {
            UpdateEverythingEmptyState();
            return;
        }

        if (IsShellView)
        {
            UpdateShellEmptyState();
            return;
        }

        if (IsPromptView)
        {
            UpdatePromptEmptyState();
            return;
        }

        if (SelectedGroups is { Count: > 0 } groups)
        {
            // "Work", "Work or Home", "these 4 groups": true of none of the groups shown.
            bool searching = !string.IsNullOrWhiteSpace(SearchText);
            var name = GroupViewText.AnyOf(groups);
            EmptyTitle = searching ? "No matches" : $"Nothing in {name} yet";
            EmptyMessage = searching
                ? $"Nothing in {name} {DescribeSearch()}."
                : groups.Count == 1
                    ? $"Open the full history (the clipboard icon above), then drag cards onto the {name} icon."
                    : "Open the full history (the clipboard icon above), then drag cards onto their icons.";
        }
        else if (!string.IsNullOrWhiteSpace(SearchText))
        {
            EmptyTitle = "No matches";
            EmptyMessage = $"Nothing in your history {DescribeSearch()}.";
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
        else if (Filter == ClipFilter.Snipping)
        {
            EmptyTitle = "No screenshots yet";
            EmptyMessage = controller.Settings.Current.ImportWindowsScreenshots
                ? "Snip with Win+Shift+S or PrtScn, or press Win+PrtScn — the screenshot shows up here the moment Windows saves it."
                : "Turn on “Snipping Tool and Win+PrtScn” in Settings to collect Windows' screenshots here.";
        }
        else if (Filter == ClipFilter.Files)
        {
            EmptyTitle = "No files or paths yet";
            EmptyMessage = @"Copy files in Explorer, or copy a path such as C:\folder\file.txt, /var/log/syslog or src/app.cs.";
        }
        else if (Filter == ClipFilter.Run)
        {
            EmptyTitle = "No Win+R commands yet";
            EmptyMessage = controller.Settings.Current.RecordRunHistory
                ? "Run something with Win+R — it shows up here at once, and stays after Windows forgets it (Windows keeps only 26)."
                : "Turn on “Win+R history” in Settings to keep every command you run with Win+R here.";
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

    /// <summary>
    /// The empty Everything tab's wording: no matches, Everything not running (with "Start Everything" when
    /// possible), still loading, not answering, or simply nothing opened yet.
    /// </summary>
    private void UpdateEverythingEmptyState()
    {
        var state = controller.Everything?.Status.State ?? EverythingState.NotRunning;
        var problem = lastPicks?.Problem;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            EmptyTitle = "No matches";
            EmptyMessage = $"Nothing you opened in Everything or copied from it contains “{SearchText.Trim()}”.";
        }
        else if (state == EverythingState.Loading)
        {
            EmptyTitle = "Everything is loading its index";
            EmptyMessage = "The files you open in Everything show up here as soon as it is ready.";
        }
        else if (state is EverythingState.NotRunning or EverythingState.Untrusted)
        {
            EmptyTitle = "Everything is not running";
            ShowStartEverything = controller.Everything?.CanStart == true;
            EmptyMessage = ShowStartEverything
                ? "Start it to see the files you open in it here, newest first."
                : "Open Everything (voidtools) to see the files you open in it here, newest first.";
        }
        else if (problem is not null)
        {
            EmptyTitle = "Everything did not answer";
            EmptyMessage = $"{char.ToUpperInvariant(problem[0])}{problem[1..]}. Try again in a moment.";
        }
        else
        {
            EmptyTitle = "Nothing opened in Everything yet";
            EmptyMessage = "Files and folders you open from Everything's results show up here, newest first — and so does anything you copy in Everything.";
        }
    }

    /// <summary>The Everything tab's footer: how many picks, where from, and how many kept items.</summary>
    /// <returns>E.g. "37 opened in Everything · 2 kept" or "12 from Everything's saved history (not running)".</returns>
    private string EverythingStatusText()
    {
        int picks = Items.Count(i => i.Pick is not null);
        int stored = Items.Count - picks;
        var kept = stored > 0 ? $" · {stored:N0} kept" : string.Empty;
        return lastPicks?.Source switch
        {
            EverythingPicksSource.Live => $"{picks:N0} opened in Everything{kept}",
            EverythingPicksSource.SavedFile => $"{picks:N0} from Everything's saved history{kept}",
            _ => stored > 0 ? $"{stored:N0} from Everything" : string.Empty,
        };
    }

    /// <summary>Updates the footer counts.</summary>
    /// <returns>A task completing when updated.</returns>
    private async Task UpdateStatusAsync()
    {
        try
        {
            var groups = SelectedGroups;
            if (groups.Count == 1)
            {
                StatusText = $"{groups[0].ItemCount:N0} in {groups[0].Name}";
                return;
            }

            if (groups.Count > 1)
            {
                // Merged: an item in two of the groups is one card and counts once, which the groups' own counts
                // cannot tell, so the store counts the merged list.
                var selection = GroupSelection;
                long count = await controller.History.CountInGroupsAsync(selection.Ids);

                // Another click may have changed the view during the await: its own update writes the footer.
                if (selection.HasSameIds(GroupSelection))
                {
                    StatusText = $"{count:N0} in {GroupViewText.ShortName(groups)}";
                }

                return;
            }

            if (IsEverythingView)
            {
                StatusText = EverythingStatusText();
                return;
            }

            if (IsShellView)
            {
                StatusText = ShellStatusText();
                return;
            }

            if (IsPromptView)
            {
                StatusText = PromptStatusText();
                return;
            }

            var stats = await controller.History.GetStatsAsync();
            if (Filter == ClipFilter.Run)
            {
                // The number that matters here: how many commands are kept, against Windows' own 26.
                StatusText = $"{stats.RunCount:N0} command{(stats.RunCount == 1 ? string.Empty : "s")} kept";
                return;
            }

            StatusText = stats.PinnedCount > 0
                ? $"{stats.Count:N0} items · {stats.PinnedCount:N0} pinned"
                : $"{stats.Count:N0} items";
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Reading history stats failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Everything that decides which entries a load shows: two keys are equal exactly when a load for one would show the
    /// same list as a load for the other (with the same history), which is what <see cref="IsListCurrent"/> relies on.
    /// </summary>
    /// <param name="Search">The search box's text, as typed.</param>
    /// <param name="Filter">The tab.</param>
    /// <param name="Groups">The ids of the groups shown, in column order, comma-separated (empty in the regular view).</param>
    /// <param name="Options">The search toggles.</param>
    private readonly record struct ViewKey(string Search, ClipFilter Filter, string Groups, SearchOptions Options);
}
