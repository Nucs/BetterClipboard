using System.Collections.ObjectModel;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Everything;
using BetterClipboard.Core.Model;
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
    private int loaded;
    private bool hasMore;
    private bool loadingMore;
    private bool suppressReload;

    /// <summary>The picks behind the Everything tab's last load (their source and problem word its empty state and footer).</summary>
    private EverythingPicksResult? lastPicks;

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

    /// <summary>
    /// Whether the empty Everything tab offers "Start Everything": it is not running, nothing could be shown, and
    /// an installed (or earlier seen) Everything can be started.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowStartEverything { get; set; }

    /// <summary>
    /// Whether the list is the Everything tab's merged view: the Everything filter in the regular view (in a group
    /// view the filter only narrows the group's stored entries — live picks are in no group).
    /// </summary>
    public bool IsEverythingView => Filter == ClipFilter.Everything && SelectedGroupId is null;

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

        // Unlike the text, the toggles stay as they were left (re-read: settings.json may have been edited meanwhile).
        ApplySavedSearchOptions();

        // The search box starts empty, and an empty search has no pattern to be wrong.
        HasPatternError = false;
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
                Reloaded?.Invoke(this, EventArgs.Empty);
                await UpdateStatusAsync();
                return;
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
            HasPatternError = false;
            UpdateEmptyState();
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
        // Run tab has room to say so).
        KeyHint = value == ClipFilter.Run ? RunKeyHint : DefaultKeyHint;
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

    /// <summary>Header suffix and search placeholder for the current view.</summary>
    private void UpdateGroupTexts()
    {
        var group = SelectedGroup;
        GroupTitle = group is null ? string.Empty : "› " + group.Name;
        SearchPlaceholder = group is not null ? $"Search in {group.Name}…"
            : IsEverythingView ? EverythingSearchPlaceholder
            : DefaultSearchPlaceholder;
    }

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
        GroupId = SelectedGroupId,
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

        if (SelectedGroup is { } group)
        {
            bool searching = !string.IsNullOrWhiteSpace(SearchText);
            EmptyTitle = searching ? "No matches" : $"Nothing in {group.Name} yet";
            EmptyMessage = searching
                ? $"Nothing in {group.Name} {DescribeSearch()}."
                : $"Open the full history (the clipboard icon above), then drag cards onto the {group.Name} icon.";
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
            if (SelectedGroup is { } group)
            {
                StatusText = $"{group.ItemCount:N0} in {group.Name}";
                return;
            }

            if (IsEverythingView)
            {
                StatusText = EverythingStatusText();
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
}
