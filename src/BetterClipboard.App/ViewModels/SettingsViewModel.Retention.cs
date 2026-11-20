using BetterClipboard.Core.Content;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// Settings › History's "Now:" lines and the size budget's bar and warning (CLAUDE.md §2.60): what each limit counts at
/// the moment, so the user can pick values that fit (the owner's request of 2026-11-20, "show … the current number for
/// each configurable … and % used").
/// </summary>
/// <remarks>
/// The numbers are read on the pool when the window is presented and two seconds after the history last changed (the
/// window's recount timer), never per copy; a changed limit rewords the lines at once from the numbers already read
/// (<see cref="ApplyRetentionLines"/>, from <see cref="Load"/>).
/// </remarks>
public sealed partial class SettingsViewModel
{
    /// <summary>What the limits counted at the last read; <see langword="null"/> until the first read lands.</summary>
    private RetentionStats? retentionStats;

    /// <summary>The temporary pictures at the last read (count and bytes).</summary>
    private (int Count, long Bytes) tempPictures;

    /// <summary>"Now:" under <em>Maximum items</em>: the items the limit counts, how full it is, what it leaves out.</summary>
    [ObservableProperty]
    public partial string MaxItemsNow { get; set; } = string.Empty;

    /// <summary>"Now:" under <em>Keep items for (days)</em>: when the item the age limit reaches first was last used.</summary>
    [ObservableProperty]
    public partial string RetentionDaysNow { get; set; } = string.Empty;

    /// <summary>"Now:" under <em>Largest item (MB)</em>: the largest item kept.</summary>
    [ObservableProperty]
    public partial string MaxItemSizeNow { get; set; } = string.Empty;

    /// <summary>"Now:" under <em>History size budget (MB)</em>: used against the budget, the file on disk, what pins hold.</summary>
    [ObservableProperty]
    public partial string MaxTotalSizeNow { get; set; } = string.Empty;

    /// <summary>"Now:" under <em>Keep temporary pictures for (days)</em>: how many there are and their size.</summary>
    [ObservableProperty]
    public partial string TempImagesNow { get; set; } = string.Empty;

    /// <summary>How full the size budget is, 0 to 100, for the bar under its card (100 at most: a prune is due above).</summary>
    [ObservableProperty]
    public partial double BudgetPercentValue { get; set; }

    /// <summary>Whether there is a size budget (the bar shows only then).</summary>
    [ObservableProperty]
    public partial bool HasBudget { get; set; }

    /// <summary>
    /// Whether the budget's bar shows in its plain (accent) color: there is a budget, under 90 % of it. The bar is three bars,
    /// one per color, at most one shown: WinUI's own warning looks (<c>ShowPaused</c>, <c>ShowError</c>) would make a screen
    /// reader announce it as "Paused" or "Error".
    /// </summary>
    [ObservableProperty]
    public partial bool IsBudgetBarPlainShown { get; set; }

    /// <summary>Whether the budget's bar shows in its caution color: the budget is at 90 % or more, short of full.</summary>
    [ObservableProperty]
    public partial bool IsBudgetBarCautionShown { get; set; }

    /// <summary>Whether the budget's bar shows in its critical color: the budget is full and the oldest items are being removed.</summary>
    [ObservableProperty]
    public partial bool IsBudgetBarFullShown { get; set; }

    /// <summary>Whether the warning under the budget shows: from 90 % of it on.</summary>
    [ObservableProperty]
    public partial bool IsBudgetWarningShown { get; set; }

    /// <summary>
    /// The warning's title: the level reached, or that the budget is full (the notification has a title of its own, which
    /// also says what follows: <see cref="HistoryLimitsText.NotificationTitle"/>).
    /// </summary>
    [ObservableProperty]
    public partial string BudgetWarningTitle { get; set; } = string.Empty;

    /// <summary>The warning's message: what happens once it is full, and every way to keep the history.</summary>
    [ObservableProperty]
    public partial string BudgetWarningMessage { get; set; } = string.Empty;

    /// <summary>The warning's color: a caution before the budget is full, an error once it is.</summary>
    [ObservableProperty]
    public partial InfoBarSeverity BudgetWarningSeverity { get; set; } = InfoBarSeverity.Warning;

    /// <summary>
    /// Reads what each limit counts now (the store, on the pool) and the temporary pictures (a short folder walk, on the
    /// pool), then rewords the "Now:" lines. Failures are logged and leave the lines as they were.
    /// </summary>
    /// <returns>A task completing when the lines are updated.</returns>
    public async Task RefreshRetentionAsync()
    {
        try
        {
            var folder = controller.TempImagesFolder;
            var stats = await controller.History.GetRetentionStatsAsync();
            var pictures = await Task.Run(() => TempImageFiles.Measure(folder));
            retentionStats = stats;
            tempPictures = pictures;
        }
        catch (Exception ex)
        {
            // A store closing under the window (exit) or a busy disk: the lines keep their last words.
            AppLog.Warn($"Reading what the history's limits count failed: {ex.Message}");
            return;
        }

        ApplyRetentionLines();
    }

    /// <summary>
    /// Words the "Now:" lines, the bar and the warning from the numbers read last and the limits as they are set now: cheap
    /// (no I/O), so a changed limit shows its effect at once.
    /// </summary>
    private void ApplyRetentionLines()
    {
        if (retentionStats is not { } stats)
        {
            return;
        }

        var current = controller.Settings.Current;
        MaxItemsNow = HistoryLimitsText.ItemsNow(stats, current.MaxItems);
        RetentionDaysNow = HistoryLimitsText.AgeNow(stats, DateTimeOffset.UtcNow);
        MaxItemSizeNow = HistoryLimitsText.LargestNow(stats);
        MaxTotalSizeNow = HistoryLimitsText.BudgetNow(stats, current.MaxTotalSizeMB);
        TempImagesNow = HistoryLimitsText.TempPicturesNow(tempPictures.Count, tempPictures.Bytes);

        var percent = HistoryLimitsText.BudgetPercent(stats, current.MaxTotalSizeMB);
        // The service's mark: a budget the prune keeps a few per cent under full shows as full, as its notification said.
        var level = HistoryLimitsText.BudgetLevel(stats, current.MaxTotalSizeMB, controller.History.BudgetMark);
        bool full = level >= HistoryBudgetWarnings.FullLevel;
        HasBudget = percent is not null;
        BudgetPercentValue = Math.Clamp(percent ?? 0, 0, 100);
        IsBudgetBarPlainShown = HasBudget && level is null;
        IsBudgetBarCautionShown = HasBudget && level is not null && !full;
        IsBudgetBarFullShown = HasBudget && full;
        IsBudgetWarningShown = level is not null;
        if (level is { } reached)
        {
            BudgetWarningTitle = HistoryLimitsText.WarningTitle(reached);
            BudgetWarningMessage = HistoryLimitsText.WarningMessage(reached);
            BudgetWarningSeverity = full ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        }
    }
}
