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

    /// <summary>The live commands behind the shell tab's last load (all that matched, before the cap), newest first.</summary>
    private IReadOnlyList<ShellCommand> lastShellCommands = [];

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

        Items.Clear();
        long syntheticId = -1;
        foreach (var row in rows)
        {
            Items.Add(row.Entry is { } stored ? CreateItem(stored) : ClipItemViewModel.ForCommand(row.Command!, syntheticId--));
        }

        loaded = Items.Count;
        hasMore = false;
        HasPatternError = false;
        UpdateEmptyState();
        Reloaded?.Invoke(this, EventArgs.Empty);
        await UpdateStatusAsync();
    }

    /// <summary>
    /// Loads a shell tab's rows: the stored entries kept from it and the shell's live commands, both narrowed by the
    /// search box (same toggles as the history), merged without forgotten or hidden commands, the live ones capped at
    /// <see cref="ShellTab.MaxCommands"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels a superseded load.</param>
    /// <returns>The rows in display order.</returns>
    /// <exception cref="OperationCanceledException">Superseded by a newer load.</exception>
    /// <exception cref="SearchPatternException">The search is an invalid regular expression.</exception>
    /// <exception cref="SearchTooSlowException">The search's regular expression ran out of time on a command.</exception>
    private async Task<IReadOnlyList<ShellTabRow>> LoadShellRowsAsync(CancellationToken cancellationToken)
    {
        var shell = CurrentShell;
        var history = controller.History;
        var storedTask = history.QueryAsync(new ClipQuery
        {
            SearchText = SearchText,
            SearchOptions = CurrentSearchOptions,
            Filter = Filter,
            Limit = ShellTab.MaxStoredEntries,
            PinnedFirst = false, // the merge orders them
        }, cancellationToken);

        var all = await controller.GetShellCommandsAsync(shell, cancellationToken);

        // Off the UI thread: a backtracking pattern may take up to its timeout per command.
        var search = SearchText;
        var options = CurrentSearchOptions;
        var matching = await Task.Run(() =>
        {
            var matcher = SearchMatcher.Create(search, options);
            return matcher is null ? all : all.Where(c => matcher.IsMatch(c.ClipboardText, cancellationToken)).ToList();
        }, cancellationToken);

        // Room for the ones the merge drops (hidden, forgotten, also stored) so the tab still shows a full page.
        var candidates = matching.Take(ShellTab.MaxCommands + ShellTab.MaxStoredEntries).ToList();
        var hidden = ShellTab.ParseHidden(await history.GetStateValueAsync(ShellTab.HiddenStateName(shell)));
        var forgotten = await history.FindForgottenAsync(candidates.Select(c => c.Fingerprint).ToList());
        var stored = await storedTask;
        lastShellCommands = matching;
        var rows = ShellTab.Merge(stored, candidates, hidden, forgotten, controller.Settings.Current.PinnedOnTop);

        // The live half is capped; stored rows come first and always stay.
        int storedRows = rows.Count(r => r.Entry is not null);
        return rows.Take(storedRows + ShellTab.MaxCommands).ToList();
    }

    /// <summary>The empty shell tab's wording: no matches, or how the tab fills (and why it may not).</summary>
    private void UpdateShellEmptyState()
    {
        bool powerShell = CurrentShell == ShellKind.PowerShell;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            EmptyTitle = "No matches";
            EmptyMessage = $"No command you typed in {(powerShell ? "PowerShell" : "Command Prompt")} {DescribeSearch()}.";
        }
        else if (powerShell)
        {
            EmptyTitle = "No PowerShell history yet";
            EmptyMessage = "PowerShell (PSReadLine) saves every command you type to a history file — none was found yet. "
                + "Type a command in PowerShell, and it shows up here, with every older one, searchable.";
        }
        else
        {
            EmptyTitle = "No Command Prompt commands yet";
            EmptyMessage = controller.Settings.Current.IsCapturePaused
                ? "Capturing is paused: commands in open Command Prompt windows show up here, but none are kept."
                : "cmd keeps no history file. Commands you type in a Command Prompt window show up here (read every 30 seconds), "
                  + "and BetterClipboard keeps them after the window closes.";
        }
    }

    /// <summary>The shell tab's footer: how many commands, from where, and how many kept.</summary>
    /// <returns>E.g. "500 of 2,952 commands in PowerShell's history · 3 kept" or "12 Command Prompt commands · 1 open window".</returns>
    private string ShellStatusText()
    {
        int live = Items.Count(i => i.Command is not null);
        int stored = Items.Count - live;
        var kept = stored > 0 ? $" · {stored:N0} kept" : string.Empty;
        int total = lastShellCommands.Count;
        if (CurrentShell == ShellKind.PowerShell)
        {
            var shown = live < total ? $"{live:N0} of {total:N0}" : $"{live:N0}";
            return $"{shown} command{(total == 1 ? string.Empty : "s")} in PowerShell's history{kept}";
        }

        int windows = controller.CmdOpenWindows;
        var open = windows > 0 ? $" · {windows:N0} open window{(windows == 1 ? string.Empty : "s")}" : string.Empty;
        return $"{live:N0} Command Prompt command{(live == 1 ? string.Empty : "s")}{open}{kept}";
    }
}
