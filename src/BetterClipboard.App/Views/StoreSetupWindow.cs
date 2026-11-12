using BetterClipboard.App.Interop;
using BetterClipboard.App.ViewModels;
using BetterClipboard.Windows.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace BetterClipboard.App.Views;

/// <summary>
/// The Microsoft Store edition's setup window (CLAUDE.md §2.47): what it does about another copy of BetterClipboard on
/// the PC (checks, the move, or how to remove it), and the welcome whose <em>Start</em> is the user's yes.
/// <c>AppController.Store.cs</c> fills its <see cref="ViewModel"/> page by page and waits for a button with
/// <see cref="NextChoiceAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Made in code, around a <see cref="StoreSetupView"/>: a window's own compiled bindings would keep the closed window
/// in memory (CLAUDE.md §2.45); a UserControl's do not. Windows draws its title bar, for the same section's other
/// reason (an extended title bar leaks GDI handles per window lifetime).
/// </para>
/// <para>
/// Closing the window is the way out, like its Quit link: every pending and later <see cref="NextChoiceAsync"/> then
/// answers <see cref="SetupChoice.Quit"/>, and the controller ends the app. Nothing is recorded before the welcome's
/// <em>Start</em>, so quitting here leaves no trace but the log.
/// </para>
/// </remarks>
public sealed class StoreSetupWindow : Window
{
    /// <summary>
    /// The window's size in DIPs: room for the lead, four lines with two-line details and an error bar under them
    /// (measured on the test desktop: at 640 DIPs the fourth line was cut off by the error bar). On a smaller screen
    /// the window is as tall as the work area (<see cref="SizeAndCenter"/>) and the lines scroll.
    /// </summary>
    private const double WidthDip = 600;

    /// <summary>See <see cref="WidthDip"/>.</summary>
    private const double HeightDip = 720;

    private readonly StoreSetupView view;
    private readonly nint hwnd;

    /// <summary>The button press the controller waits for; replaced after each press.</summary>
    private TaskCompletionSource<SetupChoice> choice = NewChoice();

    /// <summary>Set once the window closed: nothing is waited for any more.</summary>
    private bool closed;

    /// <summary>
    /// Creates the window (not yet shown; call <see cref="Present"/>).
    /// </summary>
    /// <param name="controller">App controller (theme, icon, opening the privacy page).</param>
    /// <param name="viewModel">What the window shows.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public StoreSetupWindow(AppController controller, StoreSetupViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        view = new StoreSetupView(viewModel);
        Content = view;
        hwnd = WindowNative.GetWindowHandle(this);

        Title = "BetterClipboard";
        AppWindow.SetIcon(AppController.IconPath);
        SystemBackdrop = new MicaBackdrop();
        controller.ApplyTheme(view.ThemeRoot);
        ApplyCaptionTheme();
        view.ThemeRoot.ActualThemeChanged += OnThemeChanged;

        // A fixed dialog: not resizable or maximizable; minimized, a first start would seem to have done nothing.
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        SizeAndCenter();
        view.ChoiceMade += OnChoiceMade;
        view.PrivacyRequested += (_, _) => controller.OpenPrivacyPage();
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
    }

    /// <summary>What the window shows.</summary>
    public StoreSetupViewModel ViewModel { get; }

    /// <summary>The view, for the welcome's wording (<see cref="StoreSetupView.WordWelcome"/>).</summary>
    public StoreSetupView View => view;

    /// <summary>Shows the window in front, with the keyboard on its accent button.</summary>
    public void Present()
    {
        if (closed)
        {
            return;
        }

        AppWindow.Show(true);
        Activate();
        ForegroundHelper.Activate(hwnd);
        DispatcherQueue.TryEnqueue(view.FocusPrimary);
    }

    /// <summary>Hides the window without closing it (while the language picker shows in between).</summary>
    public void Conceal()
    {
        if (!closed)
        {
            AppWindow.Hide();
        }
    }

    /// <summary>
    /// Waits for the next button press.
    /// </summary>
    /// <returns>The button; <see cref="SetupChoice.Quit"/> at once when the window was closed. Never faults.</returns>
    public Task<SetupChoice> NextChoiceAsync()
    {
        if (closed)
        {
            return Task.FromResult(SetupChoice.Quit);
        }

        if (choice.Task.IsCompleted)
        {
            choice = NewChoice();
        }

        return choice.Task;
    }

    /// <summary>
    /// Closes the window because the setup is over; unlike the user's close, this is no request to quit.
    /// </summary>
    public void CloseWhenDone()
    {
        closed = true;
        Close();
    }

    /// <summary>Makes a pending press; continuations run off the button's call stack.</summary>
    /// <returns>The source.</returns>
    private static TaskCompletionSource<SetupChoice> NewChoice() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A button of the view was pressed.</summary>
    /// <param name="sender">The view.</param>
    /// <param name="pressed">Which one.</param>
    private void OnChoiceMade(object? sender, SetupChoice pressed) => choice.TrySetResult(pressed);

    /// <summary>
    /// The user asked to close the window (its close button, Alt+F4). Refused while a move runs: its steps go on
    /// whether the window shows or not, and someone who closed it would not see how it ended. The controller's own
    /// close (<see cref="CloseWhenDone"/>) always passes.
    /// </summary>
    /// <param name="sender">The window's AppWindow.</param>
    /// <param name="args">Close request; cancelled while busy.</param>
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (ViewModel.IsBusy && !closed)
        {
            args.Cancel = true;
        }
    }

    /// <summary>
    /// The window closed. By the user: the way out. The handlers come off, so the closed window is not kept alive by
    /// them (a <see cref="Window"/> is outside WinUI's reference tracking, CLAUDE.md §2.45).
    /// </summary>
    /// <param name="sender">This window.</param>
    /// <param name="args">Close data.</param>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        Closed -= OnClosed;
        AppWindow.Closing -= OnClosing;
        view.ChoiceMade -= OnChoiceMade;
        view.ThemeRoot.ActualThemeChanged -= OnThemeChanged;
        closed = true;
        choice.TrySetResult(SetupChoice.Quit);
    }

    /// <summary>The content's theme resolved or changed: the title bar Windows draws follows it.</summary>
    /// <param name="sender">The view's root.</param>
    /// <param name="args">Nothing.</param>
    private void OnThemeChanged(FrameworkElement sender, object args) => ApplyCaptionTheme();

    /// <summary>Makes Windows' title bar dark when the content is (it does not follow an app's theme by itself).</summary>
    private void ApplyCaptionTheme()
    {
        var root = view.ThemeRoot;
        bool dark = root.RequestedTheme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => root.IsLoaded ? root.ActualTheme == ElementTheme.Dark : Application.Current.RequestedTheme == ApplicationTheme.Dark,
        };
        WindowInterop.SetDarkTitleBar(hwnd, dark);
    }

    /// <summary>
    /// Sizes the window at the scale of the monitor under the mouse (where the person looks right after starting the
    /// app) and centers it in that monitor's work area.
    /// </summary>
    private void SizeAndCenter()
    {
        var cursor = ScreenPointer.Cursor();
        var area = (cursor is { } point
            ? DisplayArea.GetFromPoint(new PointInt32(point.X, point.Y), DisplayAreaFallback.Primary)
            : DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary)).WorkArea;

        // Twice: the window's scale is its monitor's, and before the first move it is on whichever monitor Windows
        // chose. Placed on the target monitor first, the second pass sizes it with that monitor's scale.
        for (int pass = 0; pass < 2; pass++)
        {
            double scale = WindowInterop.GetScale(hwnd);
            int width = Math.Min((int)(WidthDip * scale), area.Width);
            int height = Math.Min((int)(HeightDip * scale), area.Height);
            AppWindow.MoveAndResize(new RectInt32(area.X + ((area.Width - width) / 2), area.Y + ((area.Height - height) / 2), width, height));
        }
    }
}
