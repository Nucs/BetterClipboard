using Microsoft.UI.Xaml;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// One row of Settings › Shortcut: a shortcut that opens the panel and how it is wired right now. Immutable — the whole
/// list is rebuilt whenever the shortcuts or their registrations change.
/// </summary>
/// <param name="Text">The shortcut in canonical form (<c>Alt+Win+V</c>), or the raw text of an entry that is not a shortcut.</param>
/// <param name="Status">A short sentence: registered, taken over with the keyboard hook, used by another app, or skipped.</param>
/// <param name="IsActive">Whether pressing it opens the panel right now (a broken row shows its status in the critical color).</param>
public sealed record HotkeyItemViewModel(string Text, string Status, bool IsActive)
{
    /// <summary>Whether the row's Remove button is enabled: never for the only shortcut (the panel would have no key).</summary>
    public bool CanRemove { get; init; }

    /// <summary>
    /// Accessible name of the row's Remove button. Every row has one, so the name says which shortcut it removes —
    /// otherwise a screen reader (or UI Automation) sees a column of identical buttons.
    /// </summary>
    public string RemoveName => $"Remove {Text}";

    /// <summary>Shows the status in the secondary color while the shortcut works.</summary>
    public Visibility WorkingVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows the status in the critical color while the shortcut does not work.</summary>
    public Visibility ProblemVisibility => IsActive ? Visibility.Collapsed : Visibility.Visible;
}
