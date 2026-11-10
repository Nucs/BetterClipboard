using BetterClipboard.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BetterClipboard.App.Views;

/// <summary>
/// The content of Settings' <em>Delete stored…</em> dialog: the parts of what an integration stored, with checkboxes,
/// counts and examples (CLAUDE.md §2.43). The window owns the dialog and its buttons; this view only shows the model.
/// </summary>
public sealed partial class DeleteStoredView : UserControl
{
    /// <summary>Creates the view over a model (filled later by <see cref="DeleteStoredViewModel.LoadAsync"/>).</summary>
    /// <param name="viewModel">The dialog's model.</param>
    /// <exception cref="ArgumentNullException"><paramref name="viewModel"/> is <see langword="null"/>.</exception>
    public DeleteStoredView(DeleteStoredViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    /// <summary>The model x:Bind reads.</summary>
    public DeleteStoredViewModel ViewModel { get; }

    /// <summary>x:Bind helper: visible when the text is non-empty.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The visibility.</returns>
    public Visibility HasText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// x:Bind helper: "Also turn off …" shows once the parts are there, and only while the switch is on (turning off a switch
    /// that is off means nothing).
    /// </summary>
    /// <param name="hasParts">Whether the parts are loaded and not empty.</param>
    /// <returns>The visibility.</returns>
    public Visibility TurnOffVisibility(bool hasParts) => hasParts && ViewModel.CanTurnOff ? Visibility.Visible : Visibility.Collapsed;
}
