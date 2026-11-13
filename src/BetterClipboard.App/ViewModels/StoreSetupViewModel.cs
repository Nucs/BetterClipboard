using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterClipboard.App.ViewModels;

/// <summary>Where one line of the Microsoft Store edition's setup stands.</summary>
public enum SetupRowState
{
    /// <summary>Not looked at or not started yet.</summary>
    Pending = 0,

    /// <summary>Being checked or done right now (a small ring).</summary>
    Running = 1,

    /// <summary>Checked and fine, or done (a green check).</summary>
    Ok = 2,

    /// <summary>Fine to go on, with something the user should know (an amber sign).</summary>
    Warning = 3,

    /// <summary>In the way, or failed (a red cross): the move does not start, or stopped here.</summary>
    Failed = 4,
}

/// <summary>
/// One line of the setup window: a check before the move ("Your history can be read"), or a step of it ("Copy the
/// history"), with its state and one sentence of detail.
/// </summary>
public sealed partial class SetupRowViewModel : ObservableObject
{
    /// <summary>Creates a line.</summary>
    /// <param name="title">What is checked or done, as a short sentence.</param>
    public SetupRowViewModel(string title)
    {
        Title = title;
    }

    /// <summary>What is checked or done.</summary>
    [ObservableProperty]
    public partial string Title { get; set; }

    /// <summary>What was found or what happened; empty for nothing to add.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    public partial string Detail { get; set; } = string.Empty;

    /// <summary>Where the line stands; the booleans below follow it (each shows one of the row's five signs).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(IsRunning), nameof(IsOk), nameof(IsWarning), nameof(IsFailed))]
    public partial SetupRowState State { get; set; }

    /// <summary>Whether there is a detail line to show.</summary>
    public bool HasDetail => Detail.Length > 0;

    /// <summary>Not started.</summary>
    public bool IsPending => State == SetupRowState.Pending;

    /// <summary>In progress.</summary>
    public bool IsRunning => State == SetupRowState.Running;

    /// <summary>Fine or done.</summary>
    public bool IsOk => State == SetupRowState.Ok;

    /// <summary>Fine, with a note.</summary>
    public bool IsWarning => State == SetupRowState.Warning;

    /// <summary>In the way or failed.</summary>
    public bool IsFailed => State == SetupRowState.Failed;

    /// <summary>Sets state and detail together.</summary>
    /// <param name="state">The new state.</param>
    /// <param name="detail">The new detail; <see langword="null"/> keeps the old one.</param>
    public void Set(SetupRowState state, string? detail = null)
    {
        if (detail is not null)
        {
            Detail = detail;
        }

        State = state;
    }
}

/// <summary>
/// What the Microsoft Store edition's setup window shows (CLAUDE.md §2.47): a title, a lead, the lines of a check or a
/// move, or the two switches of the welcome, an error, a progress bar and up to three buttons.
/// <c>AppController.Store.cs</c> fills it page by page and waits for a button
/// (<see cref="Views.StoreSetupWindow.NextChoiceAsync"/>); the view only shows it.
/// </summary>
public sealed partial class StoreSetupViewModel : ObservableObject
{
    /// <summary>The page's heading.</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>One or two sentences under the heading: what was found, what will happen.</summary>
    [ObservableProperty]
    public partial string Lead { get; set; } = string.Empty;

    /// <summary>The checks or steps of the current page; empty on the welcome page.</summary>
    public ObservableCollection<SetupRowViewModel> Rows { get; } = [];

    /// <summary>Whether the welcome page shows (its two switches and its note) instead of lines.</summary>
    [ObservableProperty]
    public partial bool IsWelcome { get; set; }

    /// <summary>The welcome's first switch: let Win+V open BetterClipboard instead of Windows' own clipboard panel.</summary>
    [ObservableProperty]
    public partial bool UseWinV { get; set; } = true;

    /// <summary>The welcome's second switch: start with Windows.</summary>
    [ObservableProperty]
    public partial bool StartWithWindows { get; set; } = true;

    /// <summary>Whether the second switch can be changed: not when Windows or a policy decided it.</summary>
    [ObservableProperty]
    public partial bool CanChangeStartup { get; set; } = true;

    /// <summary>What the second switch's line says under its name (why it cannot be changed, when it cannot).</summary>
    [ObservableProperty]
    public partial string StartupDescription { get; set; } = string.Empty;

    /// <summary>A line of good news on the welcome page ("Brought over 1,240 items…"); empty for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWelcomeNote))]
    public partial string WelcomeNote { get; set; } = string.Empty;

    /// <summary>Whether <see cref="WelcomeNote"/> shows.</summary>
    public bool HasWelcomeNote => WelcomeNote.Length > 0;

    /// <summary>What went wrong, in the user's language; empty for nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorText { get; set; } = string.Empty;

    /// <summary>Whether <see cref="ErrorText"/> shows.</summary>
    public bool HasError => ErrorText.Length > 0;

    /// <summary>
    /// Whether something runs: the progress bar shows, the buttons that would start something are off, and the window
    /// cannot be closed (a move that is half done must reach its end or its own failure, not be cut off by a click).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    public partial bool IsBusy { get; set; }

    /// <summary>The opposite of <see cref="IsBusy"/>: whether the way out and the second button can be pressed.</summary>
    public bool IsNotBusy => !IsBusy;

    /// <summary>Whether the progress bar has no known end (everything but the copy).</summary>
    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; } = true;

    /// <summary>The copy's progress, 0 to 100.</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    /// <summary>A quiet line left of the buttons ("Nothing is recorded until you press Start."); empty for none.</summary>
    [ObservableProperty]
    public partial string FooterNote { get; set; } = string.Empty;

    /// <summary>The accent button's label.</summary>
    [ObservableProperty]
    public partial string PrimaryText { get; set; } = string.Empty;

    /// <summary>Whether the accent button can be pressed (not while busy, not while a check stands in the way).</summary>
    [ObservableProperty]
    public partial bool IsPrimaryEnabled { get; set; } = true;

    /// <summary>The second button's label; empty hides it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSecondary))]
    public partial string SecondaryText { get; set; } = string.Empty;

    /// <summary>Whether the second button shows.</summary>
    public bool HasSecondary => SecondaryText.Length > 0;

    /// <summary>The label of the link that ends the setup and the app ("Quit", or "Not now" on the welcome).</summary>
    [ObservableProperty]
    public partial string QuitText { get; set; } = string.Empty;

    /// <summary>
    /// Starts a new page: clears the lines, the error and the progress, and sets the texts.
    /// </summary>
    /// <param name="title">The heading.</param>
    /// <param name="lead">The sentences under it.</param>
    /// <param name="welcome">Whether it is the welcome page.</param>
    public void ShowPage(string title, string lead, bool welcome = false)
    {
        Rows.Clear();
        ErrorText = string.Empty;
        FooterNote = string.Empty;
        SecondaryText = string.Empty;
        IsBusy = false;
        IsProgressIndeterminate = true;
        Progress = 0;
        IsPrimaryEnabled = true;
        IsWelcome = welcome;
        Title = title;
        Lead = lead;
    }

    /// <summary>Adds a line to the current page.</summary>
    /// <param name="title">What is checked or done.</param>
    /// <returns>The line, to set its state as things happen.</returns>
    public SetupRowViewModel AddRow(string title)
    {
        var row = new SetupRowViewModel(title);
        Rows.Add(row);
        return row;
    }
}
