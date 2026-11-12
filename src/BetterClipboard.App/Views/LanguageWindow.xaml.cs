using BetterClipboard.App.Interop;
using BetterClipboard.App.ViewModels;
using BetterClipboard.Core.Localization;
using BetterClipboard.Windows.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace BetterClipboard.App.Views;

/// <summary>
/// The first start's language picker: every language this build ships, as "own name · flag · English name", the one
/// that fits Windows' language selected, and a green check. <see cref="Choice"/> completes with the selected language
/// on the check, Enter, Esc, a double click or the window's close button: a language is always chosen, and the most
/// likely one costs a single click.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AppController"/> shows it before any other window (<see cref="LanguageChoice.ShouldAsk"/>), and builds the
/// panel and Settings only after the choice: XAML reads its labels when a window is built, so the chosen language
/// applies everywhere at once, without the restart a change in Settings needs.
/// </para>
/// <para>
/// The heading, the hint and the check's name follow the selected row (<see cref="UiText.Get(string, UiLanguage)"/>), not
/// the interface language, which nobody has chosen yet: someone who does not read English reads their own language the
/// moment their row is selected.
/// </para>
/// </remarks>
public sealed partial class LanguageWindow : Window
{
    /// <summary>The window's width in DIPs: room for a long own name, the flag and a long English name.</summary>
    private const double WidthDip = 460;

    /// <summary>
    /// Title bar, heading, list padding and the check's row together, in DIPs (the rows come on top). Measured on screen
    /// with two languages: 252 left about 50 DIPs empty under the list; 216 keeps about 20 for a hint that wraps to a
    /// second line in a longer language.
    /// </summary>
    private const double ChromeHeightDip = 216;

    /// <summary>One language row in DIPs: the template's 52 plus the item's margins.</summary>
    private const double RowHeightDip = 56;

    /// <summary>The tallest the window grows; more languages scroll.</summary>
    private const double MaxHeightDip = 640;

    /// <summary>Completes once, with the chosen language; continuations run off the closing window's call stack.</summary>
    private readonly TaskCompletionSource<UiLanguage> choice = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly nint hwnd;

    /// <summary>
    /// Creates the picker (not yet shown; call <see cref="Present"/>).
    /// </summary>
    /// <param name="controller">App controller (theme and icon).</param>
    /// <param name="suggested">The language to select first: the one that fits Windows (<see cref="LanguageChoice.Suggest"/>).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public LanguageWindow(AppController controller, UiLanguage suggested)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(suggested);
        Options = [.. UiText.Languages.Select(language => new LanguageOptionViewModel(language))];
        InitializeComponent();
        hwnd = WindowNative.GetWindowHandle(this);

        Title = "BetterClipboard";
        AppWindow.SetIcon(AppController.IconPath);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        SystemBackdrop = new MicaBackdrop();
        controller.ApplyTheme(Root);

        // A fixed little dialog: it cannot be resized, maximized or minimized (minimized, a first start would seem stuck).
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        SizeAndCenter();

        // Items set here rather than through x:Bind, so the selection below never meets an empty list (a window's
        // compiled bindings may run later than its constructor).
        LanguageList.ItemsSource = Options;
        var start = Options.FindIndex(option => option.Language == suggested);
        LanguageList.SelectedIndex = Math.Max(0, start);
        ApplyLanguage(Options[LanguageList.SelectedIndex].Language ?? UiText.English);

        // Closing the window (its close button, Alt+F4, the app exiting) is a choice too: the selected row.
        Closed += OnClosed;
    }

    /// <summary>The rows, one per shipped language, English first (<see cref="UiText.Languages"/>).</summary>
    public List<LanguageOptionViewModel> Options { get; }

    /// <summary>
    /// The chosen language: completes once, when the user finishes or the window closes. Never faults or cancels.
    /// </summary>
    public Task<UiLanguage> Choice => choice.Task;

    /// <summary>Shows the picker in front and puts the keyboard on the selected row (arrows move, Enter finishes).</summary>
    public void Present()
    {
        AppWindow.Show(true);
        Activate();
        ForegroundHelper.Activate(hwnd);

        // Focus the selected row, not the list: the arrow keys then move the selection right away.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (LanguageList.ContainerFromIndex(LanguageList.SelectedIndex) is ListViewItem row)
            {
                row.Focus(FocusState.Programmatic);
            }
            else
            {
                LanguageList.Focus(FocusState.Programmatic);
            }
        });
    }

    /// <summary>
    /// Sizes the window for its rows at the scale of the monitor under the mouse (where the person looks on a first
    /// start, which a tray app's sign-in start does not know otherwise) and centers it in that monitor's work area.
    /// </summary>
    private void SizeAndCenter()
    {
        var cursor = ScreenPointer.Cursor();
        var area = (cursor is { } point
            ? DisplayArea.GetFromPoint(new PointInt32(point.X, point.Y), DisplayAreaFallback.Primary)
            : DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary)).WorkArea;
        double heightDip = Math.Min(MaxHeightDip, ChromeHeightDip + (Options.Count * RowHeightDip));

        // Twice: the window's scale is the scale of the monitor it is on, and before the first move it is on whichever
        // monitor Windows chose. Placed on the target monitor first, the second pass sizes it with that monitor's scale.
        for (int pass = 0; pass < 2; pass++)
        {
            double scale = WindowInterop.GetScale(hwnd);
            int width = Math.Min((int)(WidthDip * scale), area.Width);
            int height = Math.Min((int)(heightDip * scale), area.Height);
            AppWindow.MoveAndResize(new RectInt32(area.X + ((area.Width - width) / 2), area.Y + ((area.Height - height) / 2), width, height));
        }
    }

    /// <summary>
    /// Words the heading, the hint and the check in a language, and tags them with it so their characters get its font.
    /// </summary>
    /// <param name="language">The selected row's language.</param>
    private void ApplyLanguage(UiLanguage language)
    {
        HeadingPanel.Language = language.Tag;
        Heading.Text = UiText.Translate("Choose your language", language);
        Hint.Text = UiText.Translate("You can change it later in Settings.", language);
        AutomationProperties.SetName(LanguageList, Heading.Text);

        string done = UiText.Translate("Done", language);
        AutomationProperties.SetName(DoneButton, done);
        ToolTipService.SetToolTip(DoneButton, done);
    }

    /// <summary>Completes <see cref="Choice"/> with the selected row's language; later calls do nothing.</summary>
    private void Finish()
    {
        var selected = LanguageList.SelectedItem as LanguageOptionViewModel;
        choice.TrySetResult(selected?.Language ?? UiText.English);
    }

    /// <summary>
    /// The window closed (its close button, Alt+F4, the app exiting, or <see cref="FinishAndClose"/>): that is a choice too,
    /// the selected row's. The handler takes itself off: a <see cref="Window"/> is outside WinUI's reference tracking, so a
    /// handler left on it that holds this window would keep the closed picker in memory for good (CLAUDE.md §2.45).
    /// </summary>
    /// <param name="sender">This window.</param>
    /// <param name="args">Close data.</param>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        Closed -= OnClosed;
        Finish();
    }

    /// <summary>Finishes with the selection and closes the window.</summary>
    private void FinishAndClose()
    {
        Finish();
        Close();
    }

    /// <summary>The selection moved: the heading follows it into that row's language.</summary>
    /// <param name="sender">The list.</param>
    /// <param name="e">Selection data.</param>
    private void LanguageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageList.SelectedItem is LanguageOptionViewModel { Language: { } language })
        {
            ApplyLanguage(language);
        }
    }

    /// <summary>A double click on a row chooses it at once.</summary>
    /// <param name="sender">The list.</param>
    /// <param name="e">Tap data.</param>
    private void LanguageList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // The first tap already selected the row under the pointer; a double tap on the list's empty space keeps the
        // selection, which is still a choice.
        e.Handled = true;
        FinishAndClose();
    }

    /// <summary>The green check.</summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">Click data.</param>
    private void DoneButton_Click(object sender, RoutedEventArgs e) => FinishAndClose();

    /// <summary>
    /// Enter and Esc finish with the selected row, wherever the keyboard is: Esc cannot mean "no language", so it keeps
    /// the selection like the close button does.
    /// </summary>
    /// <param name="sender">The root.</param>
    /// <param name="e">Key data.</param>
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Enter or VirtualKey.Escape)
        {
            e.Handled = true;
            FinishAndClose();
        }
    }
}
