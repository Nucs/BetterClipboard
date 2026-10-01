using BetterClipboard.App.ViewModels;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Prompts;
using BetterClipboard.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace BetterClipboard.App.Views;

/// <summary>
/// The prompt tabs' half of the panel (Claude, Codex; CLAUDE.md §2.21): showing the tabs, catching up when one is entered,
/// and the menu of an archived prompt's card. The other card actions (paste, pin, delete, forget, groups, drag) branch on
/// <see cref="ClipItemViewModel.Prompt"/> in the main file, next to the shell commands and Everything picks they mirror.
/// </summary>
public sealed partial class ClipboardFlyout
{
    /// <summary>
    /// A prompt tab may have appeared or disappeared, or an agent's archive changed (UI thread): update the tabs, and reload
    /// a prompt tab of that agent while it shows, so a prompt sent meanwhile appears without reopening the panel.
    /// </summary>
    /// <param name="sender">Controller.</param>
    /// <param name="agent">The agent whose archive or reader changed.</param>
    private void OnPromptsStatusChanged(object? sender, PromptAgent agent)
    {
        UpdatePromptTabs();
        if (IsOpen && ViewModel.IsPromptView && ViewModel.CurrentAgent == agent)
        {
            _ = ReloadPromptTabAsync();
        }
    }

    /// <summary>
    /// Reloads the open prompt tab keeping the selected card when it is still listed — a new prompt arriving while the user
    /// scrolls must not throw the selection away.
    /// </summary>
    /// <returns>A task completing when reloaded.</returns>
    private async Task ReloadPromptTabAsync()
    {
        var selected = (ItemsList.SelectedItem as ClipItemViewModel)?.Entry.ContentHash;
        await ViewModel.ReloadAsync();
        if (selected is not null && ViewModel.Items.FirstOrDefault(i => i.Entry.ContentHash == selected) is { } again)
        {
            ItemsList.SelectedItem = again;
        }
    }

    /// <summary>
    /// Shows each prompt tab while its setting is on and the agent's folder or the archive has prompts. A tab that disappears
    /// while selected falls back to "All". The tab strip scrolls when the tabs no longer fit (its arrows follow the strip's
    /// new width by themselves).
    /// </summary>
    private void UpdatePromptTabs()
    {
        SetTabVisible(ClaudeFilter, controller.IsPromptTabAvailable(PromptAgent.ClaudeCode), ClipFilter.ClaudeCode);
        SetTabVisible(CodexFilter, controller.IsPromptTabAvailable(PromptAgent.Codex), ClipFilter.Codex);
    }

    /// <summary>
    /// Entering a prompt tab: bring the archive up to date first (a prompt typed a second ago, in a Codex thread whose file
    /// the poll has not looked at yet), then reload when that stored anything.
    /// </summary>
    /// <param name="agent">The tab's agent.</param>
    /// <returns>A task completing when done (never throws).</returns>
    private async Task CatchUpPromptTabAsync(PromptAgent agent)
    {
        int before = controller.PromptsReader(agent)?.AddedThisSession ?? 0;
        await controller.CatchUpPromptsAsync(agent);
        if ((controller.PromptsReader(agent)?.AddedThisSession ?? 0) != before && IsOpen && ViewModel.IsPromptView && ViewModel.CurrentAgent == agent)
        {
            await ReloadPromptTabAsync();
        }
    }

    /// <summary>Keeps an archived prompt and adds it to a group (the Groups submenu, a drop on a group icon).</summary>
    /// <param name="prompt">The prompt.</param>
    /// <param name="group">The group.</param>
    /// <returns>A task completing when stored (nothing happens when keeping was refused: paused, ignored, forgotten).</returns>
    private async Task KeepPromptAndAddToGroupAsync(PromptSummary prompt, ClipGroup group)
    {
        if (await controller.KeepPromptAsync(prompt, pin: false) is { } entry)
        {
            await AddToGroupAsync([entry.Id], group);
        }
    }

    /// <summary>
    /// The menu of an archived prompt: paste or copy it, keep it, add it to a group, delete it from the archive until it is
    /// sent again, or forget it forever. The agent's own history is never changed.
    /// </summary>
    /// <param name="item">The card (its <see cref="ClipItemViewModel.Prompt"/> is set; the actions reach it through the card).</param>
    /// <param name="target">Placement target.</param>
    /// <param name="position">Position relative to <paramref name="target"/>.</param>
    /// <returns>The menu, already opening.</returns>
    private MenuFlyout ShowPromptMenu(ClipItemViewModel item, FrameworkElement target, global::Windows.Foundation.Point position)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("Paste", "\uE77F", "Enter", () => _ = PasteItemAsync(item, plainText: false)));
        menu.Items.Add(MenuItem("Copy only", "\uE8C8", null, () => _ = PasteItemAsync(item, plainText: false, paste: false)));
        menu.Items.Add(MenuItem("Pin (keep in history)", "\uE718", "Ctrl+P", () => _ = TogglePinAsync(item)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(BuildGroupsSubMenu(item));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("Delete until sent again", "\uE74D", "Del", () => DeleteItem(item)));
        menu.Items.Add(MenuItem("Forget forever…", "\uE733", null, () => DispatcherQueue.TryEnqueue(() => ShowForgetFlyout(item))));
        menu.Opened += Popup_Opened;
        menu.Closed += Popup_Closed;
        menu.ShowAt(target, new FlyoutShowOptions { Position = position });
        return menu;
    }
}
