using BetterClipboard.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BetterClipboard.App.Views;

/// <summary>
/// One choice of Settings › App › Language, drawn the way its place asks for: in the open list as flag, own name and
/// English name ("🇨🇳 简体中文  Chinese (Simplified)", "🇺🇸 Use Windows setting  English"); as the closed box's choice as
/// flag and the name in effect, with "· Windows setting" dimmed after it while BetterClipboard follows Windows
/// ("🇺🇸 English · Windows setting", "🇨🇳 简体中文"). The owner's choice, 2026-11-09 ("Plus a Windows hint").
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the view decides, and not a second template.</b> A ComboBox draws its closed box with the template of its list
/// items: WinUI's <c>ComboBox::SetContentPresenter</c> copies the chosen item container's ContentTemplate and
/// ContentTemplateSelector onto the closed box's presenter at every selection change, so a template set on that
/// presenter from outside would be overwritten at the next one. So the single item template holds this view, and the
/// view looks where it sits: inside a <see cref="ComboBoxItem"/> it is a list row; inside the ComboBox's own template it
/// is the closed box.
/// </para>
/// <para>
/// Its data is its DataContext (the item template's <see cref="LanguageOptionViewModel"/>); it draws again when the
/// ComboBox hands it another item. Until it is in the visual tree its place is unknown, so it draws as a list row and
/// <see cref="FrameworkElement.Loaded"/> draws it again. UI thread only.
/// </para>
/// </remarks>
public sealed partial class LanguageOptionView : UserControl
{
    /// <summary>
    /// The separator before the closed box's hint. It is kept out of the translated hint on purpose: inside a
    /// right-to-left translation (which comes embedded) the dot would be drawn at the run's right end, away from the name
    /// it separates the hint from.
    /// </summary>
    private const string HintSeparator = "· ";

    /// <summary>Whether the view sits in the ComboBox's closed box; known once it is Loaded, a list row until then.</summary>
    private bool inClosedBox;

    /// <summary>Creates the view; its data arrives through its DataContext.</summary>
    public LanguageOptionView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Render();
        Loaded += OnLoaded;
    }

    /// <summary>Learns where the view sits, now that it is in the visual tree, and draws it for that place.</summary>
    /// <param name="sender">This view.</param>
    /// <param name="e">Unused.</param>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        inClosedBox = IsInClosedBox(this);
        Render();
    }

    /// <summary>
    /// Whether an element sits in a ComboBox's closed box: walking up, the ComboBox comes before any ComboBoxItem. A list
    /// row's view finds its ComboBoxItem first (the drop-down's popup is the ComboBox's too, but the item is nearer).
    /// </summary>
    /// <param name="element">The element.</param>
    /// <returns><see langword="true"/> in the closed box; <see langword="false"/> in a list row or outside any ComboBox.</returns>
    private static bool IsInClosedBox(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ComboBoxItem)
            {
                return false;
            }

            if (parent is ComboBox)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Fills the flag and the two texts for the current data and place; nothing without a row as data.</summary>
    private void Render()
    {
        if (DataContext is not LanguageOptionViewModel option)
        {
            return;
        }

        FlagImage.Source = option.Flag;
        if (inClosedBox)
        {
            NameText.Text = option.SelectedName;
            NameText.Language = option.SelectedNameLanguageTag;
            DetailText.Text = option.SelectedHint.Length > 0 ? HintSeparator + option.SelectedHint : string.Empty;
        }
        else
        {
            NameText.Text = option.Name;
            NameText.Language = option.LanguageTag;
            DetailText.Text = option.EnglishName;
        }

        // An empty detail (English's English name, a picked language's hint) takes no room, nor the spacing before it.
        DetailText.Visibility = DetailText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
