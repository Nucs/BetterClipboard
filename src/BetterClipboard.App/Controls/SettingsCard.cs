using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace BetterClipboard.App.Controls;

/// <summary>
/// A settings row in the style of the Windows 11 Settings app: glyph, title, description, an action
/// control on the right (<see cref="ContentControl.Content"/>) and an optional full-width <see cref="Footer"/>.
/// </summary>
/// <remarks>
/// Templated control; its look lives in the implicit style in <c>App.xaml</c> (no <c>DefaultStyleKey</c>,
/// so there is no Generic.xaml dependency). The description and footer collapse when empty so short
/// cards keep a tidy single-line height.
/// </remarks>
public sealed partial class SettingsCard : ContentControl
{
    /// <summary>Identifies <see cref="Header"/>.</summary>
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingsCard), new PropertyMetadata(string.Empty));

    /// <summary>Identifies <see cref="Description"/>.</summary>
    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsCard), new PropertyMetadata(string.Empty, OnPartsChanged));

    /// <summary>Identifies <see cref="Glyph"/>.</summary>
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingsCard), new PropertyMetadata(string.Empty));

    /// <summary>Identifies <see cref="Footer"/>.</summary>
    public static readonly DependencyProperty FooterProperty =
        DependencyProperty.Register(nameof(Footer), typeof(object), typeof(SettingsCard), new PropertyMetadata(null, OnPartsChanged));

    private FrameworkElement? descriptionText;
    private FrameworkElement? footerPresenter;

    /// <summary>Card title.</summary>
    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>Secondary explanation under the title; say what the setting changes and its tradeoff.</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>Segoe Fluent Icons glyph (e.g. <c>&amp;#xE713;</c>).</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>Optional full-width content below the header row (wide inputs, extra buttons).</summary>
    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    /// <summary>Grabs template parts and applies initial visibility.</summary>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        descriptionText = GetTemplateChild("DescriptionText") as FrameworkElement;
        footerPresenter = GetTemplateChild("FooterPresenter") as FrameworkElement;
        UpdateParts();
        NameContent();
    }

    /// <summary>The card's action control was set or replaced: give it the card's title as its accessible name when it has none.</summary>
    /// <param name="oldContent">The previous content.</param>
    /// <param name="newContent">The new content.</param>
    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        NameContent();
    }

    /// <summary>
    /// Names an unnamed switch, combo box, number box or slider after the card's <see cref="Header"/>, so a screen reader says
    /// "Pause capturing, toggle switch, on" instead of an anonymous "toggle switch, on".
    /// </summary>
    /// <remarks>
    /// Found in a QA pass on 2026-10-09: UI Automation reported every Settings switch with an empty name, because the card's
    /// title is a separate text block the switch knows nothing about. Only these control types are named: a button names
    /// itself from its text ("Add bclip to PATH"), which a card title would wrongly replace, and a panel holding several
    /// controls (the shortcut box's row) names each of them itself. A name set in XAML always wins.
    /// </remarks>
    private void NameContent()
    {
        if (Content is ToggleSwitch or ComboBox or NumberBox or Slider &&
            Content is UIElement control &&
            !string.IsNullOrEmpty(Header) &&
            string.IsNullOrEmpty(AutomationProperties.GetName(control)))
        {
            AutomationProperties.SetName(control, Header);
        }
    }

    /// <summary>Re-evaluates part visibility when description/footer change.</summary>
    /// <param name="d">The card.</param>
    /// <param name="e">Change data.</param>
    private static void OnPartsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SettingsCard)d).UpdateParts();

    /// <summary>Collapses empty description/footer so they take no space.</summary>
    private void UpdateParts()
    {
        if (descriptionText is not null)
        {
            descriptionText.Visibility = string.IsNullOrEmpty(Description) ? Visibility.Collapsed : Visibility.Visible;
        }

        if (footerPresenter is not null)
        {
            footerPresenter.Visibility = Footer is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
