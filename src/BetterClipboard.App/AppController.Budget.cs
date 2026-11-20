using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Services;

namespace BetterClipboard.App;

/// <summary>
/// The size budget's warning, shown as a notification (CLAUDE.md §2.60): from 90 % of the budget, at every further 2 %,
/// and once it is full, the user is told that the oldest items will be removed (or are being removed), so the history stops
/// being permanent, and that Settings or an AI assistant with <c>bclip</c> can make room. A click on the notification opens
/// Settings at the budget's card.
/// </summary>
public sealed partial class AppController
{
    /// <summary>The tag of the budget warning's notification: a click on it opens Settings at the budget (<see cref="OnNotificationClicked"/>).</summary>
    private const string BudgetNotificationTag = "history-budget";

    /// <summary>
    /// Shows the budget warning as a notification (UI thread). The history service decided it is due and kept its mark
    /// already, so this is the only place that can lose it: an exiting app shows nothing, and a tray icon that is not in the
    /// notification area yet shows it once it is (<c>TrayIcon.ShowNotification</c>).
    /// </summary>
    /// <param name="warning">The level reached, with the sizes the text names.</param>
    private void ShowBudgetWarning(HistoryBudgetWarning warning)
    {
        if (exiting || tray is null)
        {
            return;
        }

        tray.ShowNotification(HistoryLimitsText.NotificationTitle(warning.Level), HistoryLimitsText.NotificationText(warning), BudgetNotificationTag);
    }

    /// <summary>
    /// A notification shown with a tag was clicked (UI thread): the budget warning's opens Settings at the budget's card,
    /// where its warning lists every way to make room.
    /// </summary>
    /// <param name="tag">The notification's tag.</param>
    private void OnNotificationClicked(string tag)
    {
        if (exiting || tag != BudgetNotificationTag)
        {
            return;
        }

        AppLog.Info("The history size budget's notification was clicked: Settings opens at the budget.");
        ShowSettings();

        // Null while the language picker or the Store edition's setup stands in front of Settings (ShowSettings shows those).
        settingsWindow?.ShowHistoryBudget();
    }
}
