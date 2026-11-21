using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Shells;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// The shell tabs' half of the flyout state (Pwsh, Cmd; CLAUDE.md §2.19): loading a tab's rows — commands kept from
/// it merged with the shell's live ones (<see cref="ShellTab.Merge"/>) — and its empty state and footer.
/// </summary>
public sealed partial class FlyoutViewModel
{
    /// <summary>The shell tabs' key hints: Enter pastes the command; Ctrl+P keeps it pinned; Delete only hides it.</summary>
    public const string ShellKeyHint = "↵ paste · Ctrl+P pin · Del hide";

    /// <summary>Search placeholder of the shell tabs, where the words also narrow the shell's own history.</summary>
    public const string ShellSearchPlaceholder = "Search commands you typed…";

    /// <summary>The live commands behind the shell tab's last load (all that matched the search), newest first.</summary>
    private IReadOnlyList<ShellCommand> lastShellCommands = [];

    /// <summary>
    /// The shell tab's merged rows of its last load, handed out as cards a page at a time as the list scrolls
    /// (<see cref="LoadMoreShellRows"/>); empty outside a shell tab. Every live command and every kept entry since
    /// 2026-11-21 (CLAUDE.md §2.62), where a tab used to show the newest 500 commands and 200 kept entries, all as cards.
    /// </summary>
    private RowPages<ShellTabRow> shellPages = RowPages<ShellTabRow>.Empty;

    /// <summary>The next synthetic (negative) id for a live command's card; continues across pages so ids stay unique.</summary>
    private long nextCommandCardId = -1;

    /// <summary>
    /// Whether the list is a shell tab's merged view: the Pwsh or Cmd filter in the regular view (in a group view the
    /// filter only narrows the groups' stored entries — live commands are in no group).
    /// </summary>
    /// <remarks>
    /// "The regular view" is read from <see cref="GroupTitle"/>, which is empty exactly then, rather than from the
    /// group selection's own model: the header text is set before every reload a selection change causes, and this keeps
    /// the shell tabs independent of how groups are selected (one group, or several merged).
    /// </remarks>
    public bool IsShellView => Filter is ClipFilter.PowerShell or ClipFilter.Cmd && string.IsNullOrEmpty(GroupTitle);

    /// <summary>The shell of the current shell tab (meaningful while <see cref="IsShellView"/>).</summary>
    public ShellKind CurrentShell => Filter == ClipFilter.Cmd ? ShellKind.Cmd : ShellKind.PowerShell;

    /// <summary>
    /// The live command a stored entry of the current shell tab corresponds to (same text), from the last load: what
    /// Delete on a kept command must also hide, or the live command would take the deleted card's place.
    /// </summary>
    /// <param name="contentHash">The stored entry's content hash.</param>
    /// <returns>The live command, or <see langword="null"/> when the shell does not list it (or it was not loaded).</returns>
    public ShellCommand? FindLiveCommand(string contentHash) =>
        lastShellCommands.FirstOrDefault(c => string.Equals(c.ContentHash, contentHash, StringComparison.Ordinal));

    /// <summary>
    /// Loads a shell tab (called by <see cref="ReloadAsync"/> inside its error handling: an invalid or too slow
    /// pattern ends up in its empty state like for the history).
    /// </summary>
    /// <param name="cancellationToken">Cancels a superseded load.</param>
    /// <returns>A task completing when the list is replaced, or nothing happened (superseded).</returns>
    /// <exception cref="OperationCanceledException">Superseded by a newer load.</exception>
    /// <exception cref="SearchPatternException">The search is an invalid regular expression.</exception>
    /// <exception cref="SearchTooSlowException">The search's regular expression ran out of time on a command.</exception>
    private async Task ReloadShellAsync(CancellationToken cancellationToken)
    {
        var rows = await LoadShellRowsAsync(cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // Every row is merged; its cards are built a page at a time (RowPages): the first page now, the rest as the list
        // scrolls (LoadMoreShellRows). Built first, then swapped in card by card: a cleared list leaves a stale selection bar
        // (ReplaceItems).
        shellPages = new RowPages<ShellTabRow>(rows);
        nextCommandCardId = -1;
        ReplaceItems(ShellCards(shellPages.Next(PageSize)));
        loaded = Items.Count;
        hasMore = shellPages.HasMore;
        HasPatternError = false;
        UpdateEmptyState();
        Reloaded?.Invoke(this, EventArgs.Empty);
        await UpdateStatusAsync();
    }

    /// <summary>
    /// Appends the next page of the shell tab's merged rows as cards (scrolled near the end). Nothing is read: the last load
    /// merged every row already.
    /// </summary>
    private void LoadMoreShellRows()
    {
        foreach (var card in ShellCards(shellPages.Next(PageSize)))
        {
            Items.Add(card);
        }

        loaded = Items.Count;
        hasMore = shellPages.HasMore;
    }

    /// <summary>
    /// The cards of a page of shell-tab rows: a kept command's history entry (showing the newer time when it was typed again
    /// since it was kept — the time its place follows), or a live command (a fresh negative id each).
    /// </summary>
    /// <param name="rows">The rows, in display order.</param>
    /// <returns>The cards, in the same order.</returns>
    private List<ClipItemViewModel> ShellCards(IReadOnlyList<ShellTabRow> rows)
    {
        var cards = new List<ClipItemViewModel>(rows.Count);
        foreach (var row in rows)
        {
            cards.Add(row.Entry is { } stored ? CreateItem(stored, row.Time) : ClipItemViewModel.ForCommand(row.Command!, nextCommandCardId--));
        }

        return cards;
    }

    /// <summary>
    /// Loads a shell tab's rows: every stored entry kept from it and every live command the shell remembers, both narrowed by
    /// the search box (same toggles as the history), merged newest first without forgotten or hidden commands
    /// (<see cref="ShellTab.Merge"/>).
    /// </summary>
    /// <remarks>
    /// Until 2026-11-21 the live commands were capped at the newest 500 and the stored entries at 200, so most of a long
    /// PowerShell history could not be reached from the tab; the owner chose every row, loaded as the list scrolls
    /// (CLAUDE.md §2.62). The shell's commands are read whole anyway (PowerShell's file is parsed at once, the Cmd tab's
    /// kept list is in memory): only the cards cost, and they are built a page at a time.
    /// </remarks>
    /// <param name="cancellationToken">Cancels a superseded load.</param>
    /// <returns>The rows in display order.</returns>
    /// <exception cref="OperationCanceledException">Superseded by a newer load.</exception>
    /// <exception cref="SearchPatternException">The search is an invalid regular expression.</exception>
    /// <exception cref="SearchTooSlowException">The search's regular expression ran out of time on a command.</exception>
    private async Task<IReadOnlyList<ShellTabRow>> LoadShellRowsAsync(CancellationToken cancellationToken)
    {
        var shell = CurrentShell;
        var history = controller.History;
        var storedTask = QueryEveryEntryAsync(new ClipQuery
        {
            SearchText = SearchText,
            SearchOptions = CurrentSearchOptions,
            Filter = Filter,
            PinnedFirst = false, // the merge orders them
            HiddenRows = HiddenRows,
        }, cancellationToken);

        var all = await controller.GetShellCommandsAsync(shell, cancellationToken);

        // PowerShell's commands carry no times: when its newest one was typed is all the merge can weigh a command used
        // from the panel against (read right after the read that set it). The Command Prompt's carry their own.
        var newestTyped = shell == ShellKind.PowerShell ? controller.PowerShellNewestCommandUtc : null;

        // Off the UI thread: a backtracking pattern may take up to its timeout per command.
        var search = SearchText;
        var options = CurrentSearchOptions;
        var matching = await Task.Run(() =>
        {
            var matcher = SearchMatcher.Create(search, options);
            return matcher is null ? all : all.Where(c => matcher.IsMatch(c.ClipboardText, cancellationToken)).ToList();
        }, cancellationToken);

        // One query per 500 commands (ClipStore.FindForgotten), however long the history.
        var hidden = ShellTab.ParseHidden(await history.GetStateValueAsync(ShellTab.HiddenStateName(shell)));
        var forgotten = await history.FindForgottenAsync(matching.Select(c => c.Fingerprint).ToList());
        var stored = await storedTask;
        lastShellCommands = matching;
        return ShellTab.Merge(stored, matching, hidden, forgotten, controller.Settings.Current.PinnedOnTop, newestTyped);
    }

    /// <summary>The empty shell tab's wording: no matches, or how the tab fills (and why it may not).</summary>
    private void UpdateShellEmptyState()
    {
        bool powerShell = CurrentShell == ShellKind.PowerShell;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            EmptyTitle = UiText.Translate("No matches");
            EmptyMessage = UiText.Format($"No command you typed in {(powerShell ? ShellSources.PowerShellName : SourceNames.Display(ShellSources.CmdName))} {DescribeSearch()}.");
        }
        else if (powerShell)
        {
            EmptyTitle = UiText.Translate("No PowerShell history yet");
            EmptyMessage = UiText.Translate("PowerShell (PSReadLine) saves every command you type to a history file — none was found yet. ")
                + UiText.Translate("Type a command in PowerShell, and it shows up here, with every older one, searchable.");
        }
        else
        {
            EmptyTitle = UiText.Translate("No Command Prompt commands yet");
            EmptyMessage = controller.Settings.Current.IsCapturePaused
                ? UiText.Translate("Capturing is paused: commands in open Command Prompt windows show up here, but none are kept.")
                : UiText.Translate("cmd keeps no history file. Commands you type in a Command Prompt window show up here (read every 30 seconds), ")
                  + UiText.Translate("and BetterClipboard keeps them after the window closes.");
        }
    }

    /// <summary>The shell tab's footer: how many commands, from where, and how many kept.</summary>
    /// <returns>
    /// E.g. "2,949 of 2,952 commands in PowerShell's history · 3 kept" (the three kept ones show as their entries) or
    /// "12 Command Prompt commands · 1 open window".
    /// </returns>
    private string ShellStatusText()
    {
        // Every row of the load, the ones whose cards are not built yet included: the numbers do not depend on how far the
        // list was scrolled.
        int live = Items.Count(i => i.Command is not null) + shellPages.CountRemaining(r => r.Command is not null);
        int stored = Items.Count(i => i.Command is null) + shellPages.CountRemaining(r => r.Command is null);
        var kept = stored > 0 ? UiText.Format($" · {stored:N0} kept") : string.Empty;
        int total = lastShellCommands.Count;
        if (CurrentShell == ShellKind.PowerShell)
        {
            var shown = live < total ? UiText.Format($"{live:N0} of {total:N0}") : $"{live:N0}";
            return UiText.Plural(total, $"{shown} command in PowerShell's history{kept}", $"{shown} commands in PowerShell's history{kept}");
        }

        int windows = controller.CmdOpenWindows;
        var open = windows > 0 ? UiText.Plural(windows, $" · {windows:N0} open window", $" · {windows:N0} open windows") : string.Empty;
        return UiText.Plural(live, $"{live:N0} Command Prompt command{open}{kept}", $"{live:N0} Command Prompt commands{open}{kept}");
    }
}
