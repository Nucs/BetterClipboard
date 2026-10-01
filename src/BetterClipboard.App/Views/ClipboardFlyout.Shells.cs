using BetterClipboard.App.ViewModels;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Shells;
using BetterClipboard.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace BetterClipboard.App.Views;

/// <summary>
/// The shell tabs' half of the panel (Pwsh, Cmd; CLAUDE.md §2.19): showing the tabs, and the menu of a live command
/// card. The other card actions (paste, pin, delete, forget, groups, drag) branch on <see cref="ClipItemViewModel.Command"/>
/// in the main file, next to the Everything picks they mirror.
/// </summary>
public sealed partial class ClipboardFlyout
{
    /// <summary>
    /// A shell tab may have appeared or disappeared, or the Cmd tab's kept list changed (UI thread): update the tabs,
    /// and reload a shell tab that is showing, so a command typed meanwhile appears without reopening the panel.
    /// </summary>
    /// <param name="sender">Controller.</param>
    /// <param name="e">Unused.</param>
    private void OnShellHistoryStatusChanged(object? sender, EventArgs e)
    {
        UpdateShellTabs();
        if (IsOpen && ViewModel.IsShellView)
        {
            _ = ViewModel.ReloadAsync();
        }
    }

    /// <summary>
    /// Shows the Pwsh tab while its setting is on and PowerShell's history file exists, and the Cmd tab while its
    /// setting is on. A tab that disappears while selected falls back to "All", so the list never stays filtered by an
    /// invisible tab. The tab strip scrolls when the tabs no longer fit (its arrows follow the strip's new width by
    /// themselves).
    /// </summary>
    private void UpdateShellTabs()
    {
        SetTabVisible(PwshFilter, controller.IsPowerShellTabAvailable, ClipFilter.PowerShell);
        SetTabVisible(CmdFilter, controller.IsCmdTabAvailable, ClipFilter.Cmd);
    }

    /// <summary>Shows or hides one tab, leaving it for "All" when it disappears while selected.</summary>
    /// <param name="tab">The tab.</param>
    /// <param name="available">Whether it should show.</param>
    /// <param name="filter">Its filter.</param>
    /// <returns><see langword="true"/> when its visibility changed.</returns>
    private bool SetTabVisible(SelectorBarItem tab, bool available, ClipFilter filter)
    {
        var visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (tab.Visibility == visibility)
        {
            return false;
        }

        tab.Visibility = visibility;
        if (!available && ViewModel.Filter == filter)
        {
            SelectFilter(ClipFilter.All);
        }

        return true;
    }

    /// <summary>Keeps a shell tab's live command and adds it to a group (the Groups submenu).</summary>
    /// <param name="command">The command.</param>
    /// <param name="group">The group.</param>
    /// <returns>A task completing when stored (nothing happens when keeping was refused: paused, ignored, forgotten).</returns>
    private async Task KeepCommandAndAddToGroupAsync(ShellCommand command, ClipGroup group)
    {
        if (await controller.KeepCommandAsync(command, pin: false) is { } entry)
        {
            await AddToGroupAsync([entry.Id], group);
        }
    }

    /// <summary>
    /// The menu of a shell tab's live command: paste or copy it, keep it, add it to a group, hide it, or forget it
    /// forever. There is no "Run": a command from a shell's history has no safe place to run (its directory, its
    /// session's variables are gone); paste it into a shell instead.
    /// </summary>
    /// <param name="item">The card (its <see cref="ClipItemViewModel.Command"/> is set; the actions reach it through the card).</param>
    /// <param name="target">Placement target.</param>
    /// <param name="position">Position relative to <paramref name="target"/>.</param>
    /// <returns>The menu, already opening.</returns>
    private MenuFlyout ShowCommandMenu(ClipItemViewModel item, FrameworkElement target, global::Windows.Foundation.Point position)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("Paste", "\uE77F", "Enter", () => _ = PasteItemAsync(item, plainText: false)));
        menu.Items.Add(MenuItem("Copy only", "\uE8C8", null, () => _ = PasteItemAsync(item, plainText: false, paste: false)));
        menu.Items.Add(MenuItem("Pin (keep in history)", "\uE718", "Ctrl+P", () => _ = TogglePinAsync(item)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(BuildGroupsSubMenu(item));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("Hide until typed again", "\uED1A", "Del", () => DeleteItem(item)));
        menu.Items.Add(MenuItem("Forget forever…", "\uE733", null, () => DispatcherQueue.TryEnqueue(() => ShowForgetFlyout(item))));
        menu.Opened += Popup_Opened;
        menu.Closed += Popup_Closed;
        menu.ShowAt(target, new FlyoutShowOptions { Position = position });
        return menu;
    }
}
