using Microsoft.UI.Xaml;

namespace BetterClipboard.App.Views;

/// <summary>
/// Settings in the Microsoft Store edition (CLAUDE.md §2.47): which cards the page keeps, and the buttons that open
/// Windows' own pages for what that edition leaves to Windows.
/// </summary>
/// <remarks>
/// <para>
/// A packaged app's own registry writes stay inside its package, and the Store's policies ask for supported ways to
/// change Windows' settings. So that edition has no "Release from Explorer", starts through its startup task (which
/// Windows' Startup apps list can overrule), links to Windows' clipboard page instead of flipping Windows' switch, and
/// is updated by the Store. The view model has the states (<c>SettingsViewModel.Store.cs</c>).
/// </para>
/// <para>
/// <b>Cards are taken out of the page, not hidden.</b> The search shows and hides every card of the page by itself
/// (<c>SettingsWindow.Search.cs</c>), so a card that was only collapsed would come back with the next search. One that
/// is not in the page is not searched either.
/// </para>
/// </remarks>
public sealed partial class SettingsWindow
{
    /// <summary>
    /// Keeps only the cards that are true for this edition, and hides the box for a panel button it does not have (the
    /// update button, CLAUDE.md §2.55). Called once, right after the page was built and before the search indexes it.
    /// </summary>
    private void ApplyEdition()
    {
        if (AppController.IsStoreEdition)
        {
            SettingsPage.Children.Remove(ReleaseFromExplorerCard);
            SettingsPage.Children.Remove(UpdatesCard);

            // No update button in this edition's panel (its updates come from the Store), so nothing to show or hide. A box
            // in a card, not a card: collapsed, not removed (the search only shows and hides whole cards).
            UpdatesButtonBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            SettingsPage.Children.Remove(StoreUpdatesCard);
        }
    }

    /// <summary>"Open Windows settings" on the card of Windows' own clipboard history (Store edition).</summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">Click data.</param>
    private void OpenWindowsClipboardSettings_Click(object sender, RoutedEventArgs e) => AppController.OpenWindowsSettingsPage("clipboard");

    /// <summary>"Open Startup apps" on the Start with Windows card, shown while Windows' list has the app turned off (Store edition).</summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">Click data.</param>
    private void OpenStartupApps_Click(object sender, RoutedEventArgs e) => AppController.OpenWindowsSettingsPage("startupapps");

    /// <summary>"Open in Microsoft Store" on the Store edition's Updates card.</summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">Click data.</param>
    private void OpenStorePage_Click(object sender, RoutedEventArgs e) => AppController.OpenStorePage();
}
