using BetterClipboard.Core.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

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

    /// <summary>Identifies <see cref="Keywords"/>.</summary>
    public static readonly DependencyProperty KeywordsProperty =
        DependencyProperty.Register(nameof(Keywords), typeof(string), typeof(SettingsCard), new PropertyMetadata(string.Empty));

    private FrameworkElement? descriptionText;
    private FrameworkElement? footerPresenter;

    /// <summary>The template's title text, highlighted by the search (<see cref="ApplySearchHighlights"/>).</summary>
    private TextBlock? headerText;

    /// <summary>
    /// The highlights asked for last (title, description, color), kept so a card whose template is applied later — or
    /// again, after a theme change — shows them too. <see langword="null"/> when nothing is highlighted.
    /// </summary>
    private (IReadOnlyList<TextSpan> Header, IReadOnlyList<TextSpan> Description, Brush Brush)? highlights;

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

    /// <summary>
    /// Words people may type to find this setting that its own texts lack ("hotkey" for a shortcut), comma-separated, in
    /// the interface language (a catalog string, so each language brings its own synonyms). Never shown: only Settings'
    /// search reads them.
    /// </summary>
    public string Keywords
    {
        get => (string)GetValue(KeywordsProperty);
        set => SetValue(KeywordsProperty, value);
    }

    /// <summary>Grabs template parts and applies initial visibility.</summary>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        descriptionText = GetTemplateChild("DescriptionText") as FrameworkElement;
        footerPresenter = GetTemplateChild("FooterPresenter") as FrameworkElement;
        headerText = GetTemplateChild("HeaderText") as TextBlock;
        UpdateParts();
        NameContent();
        PaintHighlights();
    }

    /// <summary>
    /// Shows what Settings' search matched in the card's title and description (the owner's choice, 2026-11-09: matches
    /// highlighted), or removes the highlights.
    /// </summary>
    /// <remarks>
    /// The stretches are offsets into the texts as they are now (<see cref="SettingsSearch.Match"/> over
    /// <see cref="Header"/> and <see cref="Description"/>); the window searches again whenever a status changes, so a
    /// description that changed meanwhile gets new stretches. Stretches past the end of the text are dropped: a highlighter
    /// with a range beyond its text throws.
    /// </remarks>
    /// <param name="header">Stretches of the title; empty for none.</param>
    /// <param name="description">Stretches of the description; empty for none.</param>
    /// <param name="brush">The highlight's background (a translucent accent, legible under text in both themes); <see langword="null"/> removes all highlights.</param>
    public void ApplySearchHighlights(IReadOnlyList<TextSpan> header, IReadOnlyList<TextSpan> description, Brush? brush)
    {
        highlights = brush is null || (header.Count == 0 && description.Count == 0) ? null : (header, description, brush);
        PaintHighlights();
    }

    /// <summary>Puts <see cref="highlights"/> on the template's text blocks (or clears them).</summary>
    private void PaintHighlights()
    {
        Paint(headerText, highlights?.Header, highlights?.Brush);
        Paint(descriptionText as TextBlock, highlights?.Description, highlights?.Brush);
    }

    /// <summary>Replaces a text block's highlighters with one that paints <paramref name="spans"/>.</summary>
    /// <param name="block">The text block (nothing happens before the template is applied).</param>
    /// <param name="spans">The stretches; <see langword="null"/> or empty clears.</param>
    /// <param name="brush">The background.</param>
    private static void Paint(TextBlock? block, IReadOnlyList<TextSpan>? spans, Brush? brush)
    {
        if (block is null)
        {
            return;
        }

        block.TextHighlighters.Clear();
        int length = block.Text?.Length ?? 0;
        if (spans is not { Count: > 0 } || brush is null || length == 0)
        {
            return;
        }

        var highlighter = new TextHighlighter { Background = brush };
        foreach (var span in spans)
        {
            if (span.Start >= 0 && span.Length > 0 && span.Start + span.Length <= length)
            {
                highlighter.Ranges.Add(new TextRange { StartIndex = span.Start, Length = span.Length });
            }
        }

        if (highlighter.Ranges.Count > 0)
        {
            block.TextHighlighters.Add(highlighter);
        }
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
