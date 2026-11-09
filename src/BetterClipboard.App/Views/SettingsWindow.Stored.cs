using System.ComponentModel;
using BetterClipboard.App.ViewModels;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Storage;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BetterClipboard.App.Views;

/// <summary>
/// <em>Delete stored…</em> on every integration card (CLAUDE.md §2.43): the confirmation dialog with what is stored, part by
/// part, and the deletion of what the user ticked; plus keeping each card's button enabled only while there is something to
/// delete.
/// </summary>
public sealed partial class SettingsWindow
{
    /// <summary>
    /// Counts again two seconds after the last history, prompt or shell change while Settings is open: copies come in bursts,
    /// and each count reads the whole history once (about half a second for 20,000 entries, measured 2026-11-09).
    /// </summary>
    private DispatcherQueueTimer? storedRefresh;

    /// <summary>Whether a Delete stored dialog is open: a second click while one shows does nothing (WinUI allows one dialog per window).</summary>
    private bool deleteStoredOpen;

    /// <summary>Creates the recount timer (from the constructor).</summary>
    private void InitializeStoredRefresh()
    {
        storedRefresh = DispatcherQueue.CreateTimer();
        storedRefresh.Interval = TimeSpan.FromSeconds(2);
        storedRefresh.IsRepeating = false;
        storedRefresh.Tick += (_, _) => _ = ViewModel.RefreshStoredAsync();
    }

    /// <summary>Asks for a recount of what the integrations stored, two seconds from now (see <see cref="storedRefresh"/>).</summary>
    private void ScheduleStoredRefresh()
    {
        storedRefresh?.Stop();
        storedRefresh?.Start();
    }

    /// <summary>
    /// Names each card's <em>Delete stored…</em> button after its card ("Delete stored: ShareX screenshots"): ten buttons
    /// with one text would otherwise all read the same to a screen reader and to UI Automation.
    /// </summary>
    /// <param name="sender">The button; its <c>Tag</c> names the card's <see cref="StoredSource"/>.</param>
    /// <param name="e">Event data.</param>
    private void DeleteStoredButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } button && Enum.TryParse(tag, out StoredSource source))
        {
            var title = Core.Presentation.StoredText.SwitchTitleOf(source);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, UiText.Format($"Delete stored: {title}"));
        }
    }

    /// <summary>
    /// <em>Delete stored…</em> on an integration card. The button's <c>Tag</c> names the card's <see cref="StoredSource"/>.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">Click data.</param>
    private async void DeleteStored_Click(object sender, RoutedEventArgs e)
    {
        if (deleteStoredOpen || sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse(tag, out StoredSource source))
        {
            return;
        }

        deleteStoredOpen = true;
        try
        {
            await ShowDeleteStoredAsync(source);
        }
        catch (Exception ex)
        {
            // async void's caller has nothing to catch it: a failure here must not crash the app.
            AppLog.Warn($"The Delete stored dialog for {source} failed: {ex.Message}");
        }
        finally
        {
            deleteStoredOpen = false;
        }
    }

    /// <summary>
    /// Shows the dialog at once ("Reading what is stored…"), fills it from the history, and deletes the ticked parts when
    /// the user confirms: "Also turn off" flips the switch first, so nothing is recorded again meanwhile. A failed
    /// deletion keeps the dialog open with the reason. Afterwards the cards' statuses and buttons are refreshed.
    /// </summary>
    /// <param name="source">The integration whose card was clicked.</param>
    /// <returns>A task completing when the dialog closed.</returns>
    private async Task ShowDeleteStoredAsync(StoredSource source)
    {
        var model = new DeleteStoredViewModel(controller, source, ViewModel.IsSwitchOn(source));
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = model.Title,
            Content = new DeleteStoredView(model),
            PrimaryButtonText = model.PrimaryText,
            IsPrimaryButtonEnabled = false,
            CloseButtonText = UiText.Translate("Cancel"),

            // Cancel is what Enter and Esc do: a deletion is never one stray key away.
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = Root.ActualTheme,
            PrimaryButtonStyle = (Style)Application.Current.Resources["DangerButtonStyle"],
        };

        // Wider than WinUI's 548 DIPs: a row of four thumbnails and a part's counts fit beside each other then. Set before
        // the dialog's template is applied, which reads it.
        dialog.Resources["ContentDialogMaxWidth"] = 640.0;

        void OnModelChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(DeleteStoredViewModel.PrimaryText) or nameof(DeleteStoredViewModel.CanDelete) or nameof(DeleteStoredViewModel.IsEmpty))
            {
                dialog.PrimaryButtonText = model.IsEmpty ? string.Empty : model.PrimaryText;
                dialog.IsPrimaryButtonEnabled = model.CanDelete;
                if (model.IsEmpty)
                {
                    dialog.CloseButtonText = UiText.Translate("Close");
                }
            }
        }

        model.PropertyChanged += OnModelChanged;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            // The dialog waits for the deletion; a failure cancels the close, so the reason stays in view.
            var deferral = args.GetDeferral();
            try
            {
                if (model.TurnOff && model.CanTurnOff)
                {
                    ViewModel.TurnOff(source);
                }

                if (!await model.DeleteAsync())
                {
                    args.Cancel = true;
                }
            }
            finally
            {
                deferral.Complete();
            }
        };

        using var loading = new CancellationTokenSource();
        var load = model.LoadAsync(loading.Token);
        await dialog.ShowAsync();
        loading.Cancel();
        await load; // never throws: LoadAsync reports its own failures
        model.PropertyChanged -= OnModelChanged;

        // Whatever happened, show the cards as they are now.
        await ViewModel.RefreshStoredAsync();
        ViewModel.RefreshPromptsStatus();
        ViewModel.RefreshShellHistoryStatus();
        _ = ViewModel.RefreshStatsAsync();
    }
}
