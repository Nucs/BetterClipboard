using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Presentation;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// One row of Settings › Shortcut › Buttons in the panel (CLAUDE.md §2.55, §2.58): a header button's box — ticked while it
/// shows — and its up and down arrows, which move it left and right in the panel.
/// </summary>
/// <remarks>
/// <para>
/// <b>A row is a place, not a button.</b> The list's rows stay where they are and a move gives two rows each other's button
/// (<see cref="Assign"/>), instead of moving an item of the list: an items control recreates a moved item's container, and
/// the arrow that had the keyboard would be gone, sending the focus elsewhere and the page with it. This way the page stays
/// still and the keyboard can follow the button (<c>SettingsWindow.FocusPanelButtonArrow</c>), so pressing the arrow again
/// moves it on — the owner's "have the settings stationary".
/// </para>
/// <para>
/// Ticking or unticking the box reports to Settings (the constructor's callback), which saves the hidden list; an
/// <see cref="Assign"/> changes the box without reporting, since it only shows what is saved.
/// </para>
/// </remarks>
public sealed partial class PanelButtonRowViewModel : ObservableObject, ISearchableItem
{
    /// <summary>Called when the user ticks or unticks the box: the button and whether it should show.</summary>
    private readonly Action<PanelButton, bool> shownChanged;

    /// <summary>Whether <see cref="Assign"/> is setting the properties: the box's change is then no user's.</summary>
    private bool assigning;

    /// <summary>Creates a row for a place in the list; it shows nothing until <see cref="Assign"/>.</summary>
    /// <param name="position">The row's place, from the top (0).</param>
    /// <param name="shownChanged">Called when the user ticks or unticks the box: the button and whether it should show.</param>
    public PanelButtonRowViewModel(int position, Action<PanelButton, bool> shownChanged)
    {
        Position = position;
        this.shownChanged = shownChanged;
    }

    /// <summary>The row's place, from the top (0): the left-to-right place of its button among the listed ones. Never changes.</summary>
    public int Position { get; }

    /// <summary>The button this row shows now.</summary>
    [ObservableProperty]
    public partial PanelButton Button { get; private set; }

    /// <summary>The button's label, as its box says it (the same words as before the order existed).</summary>
    [ObservableProperty]
    public partial string Label { get; private set; } = string.Empty;

    /// <summary>The button's Segoe Fluent Icons glyph; empty for the groups button, whose ribbon is a path.</summary>
    [ObservableProperty]
    public partial string Glyph { get; private set; } = string.Empty;

    /// <summary>Shows the font glyph (every button but the groups one).</summary>
    [ObservableProperty]
    public partial Visibility GlyphVisibility { get; private set; }

    /// <summary>Shows the groups button's ribbon, the header's own path (Segoe Fluent Icons has no bookmark ribbon).</summary>
    [ObservableProperty]
    public partial Visibility RibbonVisibility { get; private set; } = Visibility.Collapsed;

    /// <summary>Whether the button shows in the header: the box's tick. The user's change is saved through the callback.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; set; }

    /// <summary>Whether the up arrow can move the button (another listed button lies above it).</summary>
    [ObservableProperty]
    public partial bool CanMoveUp { get; private set; }

    /// <summary>Whether the down arrow can move the button (another listed button lies below it).</summary>
    [ObservableProperty]
    public partial bool CanMoveDown { get; private set; }

    /// <summary>The up arrow's accessible name, which says which button it moves: "Move up: Groups".</summary>
    [ObservableProperty]
    public partial string MoveUpName { get; private set; } = string.Empty;

    /// <summary>The down arrow's accessible name: "Move down: Groups".</summary>
    [ObservableProperty]
    public partial string MoveDownName { get; private set; } = string.Empty;

    /// <summary>The label, for Settings' search ("groups" finds the card).</summary>
    public IEnumerable<string> SearchTexts => [Label];

    /// <summary>
    /// The label a button's box shows (the same strings as the boxes had before rows existed, so their translations stay).
    /// </summary>
    /// <param name="button">The button.</param>
    /// <returns>The label, in the interface language.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="button"/> is no member Settings knows.</exception>
    public static string LabelOf(PanelButton button) => button switch
    {
        PanelButton.KeepOpen => UiText.Translate("Pin: keep the panel open after pasting"),
        PanelButton.Updates => UiText.Translate("Updates"),
        PanelButton.Pause => UiText.Translate("Pause capturing"),
        PanelButton.Groups => UiText.Translate("Groups"),
        PanelButton.Clear => UiText.Translate("Clear history"),
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, "No label for it."),
    };

    /// <summary>
    /// The glyph a button's box shows, the header button's own (checked by rendering): E718 Pin, E895 Sync, E769 Pause, E74D
    /// Delete; empty for the groups button, drawn with the header's ribbon path.
    /// </summary>
    /// <param name="button">The button.</param>
    /// <returns>The glyph, or empty.</returns>
    public static string GlyphOf(PanelButton button) => button switch
    {
        PanelButton.KeepOpen => "\uE718",
        PanelButton.Updates => "\uE895",
        PanelButton.Pause => "\uE769",
        PanelButton.Clear => "\uE74D",
        _ => string.Empty,
    };

    /// <summary>
    /// Shows a button in this row: its icon, label, tick and which arrows can move it. Changes the tick without reporting it
    /// (it only shows what is saved).
    /// </summary>
    /// <param name="button">The button now in this place.</param>
    /// <param name="shown">Whether it shows in the header.</param>
    /// <param name="canMoveUp">Whether another listed button lies above it.</param>
    /// <param name="canMoveDown">Whether another listed button lies below it.</param>
    public void Assign(PanelButton button, bool shown, bool canMoveUp, bool canMoveDown)
    {
        assigning = true;
        try
        {
            Button = button;
            Label = LabelOf(button);
            Glyph = GlyphOf(button);
            bool ribbon = button == PanelButton.Groups;
            GlyphVisibility = ribbon ? Visibility.Collapsed : Visibility.Visible;
            RibbonVisibility = ribbon ? Visibility.Visible : Visibility.Collapsed;
            IsShown = shown;
            CanMoveUp = canMoveUp;
            CanMoveDown = canMoveDown;
            MoveUpName = UiText.Format($"Move up: {Label}");
            MoveDownName = UiText.Format($"Move down: {Label}");
        }
        finally
        {
            assigning = false;
        }
    }

    /// <summary>The user ticked or unticked the box: reports it, so Settings saves the hidden list.</summary>
    /// <param name="value">Whether the button should show.</param>
    partial void OnIsShownChanged(bool value)
    {
        if (!assigning)
        {
            shownChanged(Button, value);
        }
    }
}
