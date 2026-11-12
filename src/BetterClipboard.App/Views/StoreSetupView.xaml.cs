using BetterClipboard.App.ViewModels;
using BetterClipboard.Core.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace BetterClipboard.App.Views;

/// <summary>Which of the setup window's buttons was pressed.</summary>
public enum SetupChoice
{
    /// <summary>The accent button: go on (move, check again, close the other copy, start).</summary>
    Primary = 0,

    /// <summary>The second button, when the page has one (open Installed apps, copy a command, go on without).</summary>
    Secondary = 1,

    /// <summary>The way out: the link on the left, or the window's close button. The app then quits.</summary>
    Quit = 2,
}

/// <summary>
/// The content of the Microsoft Store edition's setup window: shows a <see cref="StoreSetupViewModel"/> and reports
/// which button was pressed. See <c>StoreSetupView.xaml</c> for the layout and <c>AppController.Store.cs</c> for the
/// flow.
/// </summary>
public sealed partial class StoreSetupView : UserControl
{
    /// <summary>
    /// Creates the view.
    /// </summary>
    /// <param name="viewModel">What it shows.</param>
    /// <exception cref="ArgumentNullException"><paramref name="viewModel"/> is <see langword="null"/>.</exception>
    public StoreSetupView(StoreSetupViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        InitializeComponent();
    }

    /// <summary>What the view shows.</summary>
    public StoreSetupViewModel ViewModel { get; }

    /// <summary>The root element, for the app's theme.</summary>
    public FrameworkElement ThemeRoot => Root;

    /// <summary>Raised when a button was pressed.</summary>
    public event EventHandler<SetupChoice>? ChoiceMade;

    /// <summary>Raised when the welcome's privacy link was pressed; the window's owner opens the page.</summary>
    public event EventHandler? PrivacyRequested;

    /// <summary>
    /// Words the welcome page's fixed texts in the interface language. Called right before the welcome shows, because
    /// the language may have been chosen only a moment ago, after this view was built.
    /// </summary>
    public void WordWelcome()
    {
        WinVTitle.Text = UiText.Translate("Open with Win+V");
        WinVDescription.Text = UiText.Translate("BetterClipboard answers Win+V instead of Windows' own clipboard panel while it runs. Quit BetterClipboard and Win+V is Windows' again. Off: the panel opens with Alt+Win+V, and you can pick any shortcut in Settings.");
        StartupTitle.Text = UiText.Translate("Start with Windows");
        KeptNote.Text = UiText.Translate("BetterClipboard keeps what you copy (text, pictures, files) encrypted on this PC until you delete it. Nothing is sent anywhere. Copies that password managers mark as private are never kept.");
        PrivacyLink.Content = UiText.Translate("Privacy");

        // The switches carry no visible label of their own: a screen reader reads their line's title.
        AutomationProperties.SetName(WinVSwitch, WinVTitle.Text);
        AutomationProperties.SetName(StartupSwitch, StartupTitle.Text);
    }

    /// <summary>Puts the keyboard on the accent button, so Enter goes on.</summary>
    public void FocusPrimary() => PrimaryButton.Focus(FocusState.Programmatic);

    /// <summary>The accent button.</summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">Click data.</param>
    private void PrimaryButton_Click(object sender, RoutedEventArgs e) => ChoiceMade?.Invoke(this, SetupChoice.Primary);

    /// <summary>The second button.</summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">Click data.</param>
    private void SecondaryButton_Click(object sender, RoutedEventArgs e) => ChoiceMade?.Invoke(this, SetupChoice.Secondary);

    /// <summary>The way out.</summary>
    /// <param name="sender">The link.</param>
    /// <param name="e">Click data.</param>
    private void QuitButton_Click(object sender, RoutedEventArgs e) => ChoiceMade?.Invoke(this, SetupChoice.Quit);

    /// <summary>The welcome's privacy link.</summary>
    /// <param name="sender">The link.</param>
    /// <param name="e">Click data.</param>
    private void PrivacyLink_Click(object sender, RoutedEventArgs e) => PrivacyRequested?.Invoke(this, EventArgs.Empty);
}
