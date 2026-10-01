using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Settings;
using BetterClipboard.Windows.Integrations;

namespace BetterClipboard.App;

/// <summary>
/// The Snipping tab's half of the controller (CLAUDE.md §2.18): Windows' own screenshots — Snipping Tool's auto-save and
/// Win+PrtScn — imported from the Screenshots folder while <see cref="AppSettings.ImportWindowsScreenshots"/> is on.
/// </summary>
/// <remarks>
/// Like ShareX's screenshots, every file becomes a history entry the moment it is complete; Snipping Tool's own clipboard
/// copy of a snip merges into the same entry by its pixels. The integration only reads the folder (and a private copy of
/// Snipping Tool's settings, for the status line).
/// </remarks>
public sealed partial class AppController
{
    /// <summary>The Screenshots folder watch (created in <see cref="Start"/>, disposed on exit).</summary>
    private WindowsScreenshotsIntegration? snipping;

    /// <summary>
    /// Raised on the UI thread when the Screenshots folder watch started or stopped, the folder or Snipping Tool changed,
    /// or a screenshot was imported (the flyout shows or hides its Snipping tab; Settings refreshes its status line).
    /// </summary>
    public event EventHandler? SnippingStatusChanged;

    /// <summary>
    /// Whether the panel shows its Snipping tab: the setting. Like the Run tab it waits for nothing — Win+PrtScn exists on
    /// every Windows, and an empty tab explains how it fills.
    /// </summary>
    public bool IsSnippingTabAvailable => settings?.Current.ImportWindowsScreenshots == true;

    /// <summary>What was found about the Screenshots folder and Snipping Tool (<see cref="WindowsScreenshotsLocation.None"/> until located).</summary>
    public WindowsScreenshotsLocation SnippingLocation => snipping?.Location ?? WindowsScreenshotsLocation.None;

    /// <summary>The Screenshots folder being watched right now, or <see langword="null"/> (off, or the folder does not exist yet).</summary>
    public string? SnippingWatchedFolder => snipping?.WatchedFolder;

    /// <summary>Screenshots stored from the folder since the app started.</summary>
    public int SnippingImportedThisSession => snipping?.ImportedThisSession ?? 0;

    /// <summary>
    /// Creates the integration and starts it with the current setting (the lookup runs on the pool; the tab follows the
    /// setting at once). A failure is logged: capture, paste and the other tabs keep working without it.
    /// </summary>
    private void StartSnipping()
    {
        snipping = new WindowsScreenshotsIntegration(History, () => Settings.Current.MaxItemSizeMB * 1024L * 1024L);
        snipping.StatusChanged += (_, _) => ui.TryEnqueue(() => SnippingStatusChanged?.Invoke(this, EventArgs.Empty));
        _ = StartSnippingAsync(snipping, Settings.Current.ImportWindowsScreenshots);
    }

    /// <summary>Starts the integration without letting a failure escape into the fire-and-forget caller.</summary>
    /// <param name="integration">The integration.</param>
    /// <param name="enable">The setting at startup.</param>
    /// <returns>A task completing once started (or failed, logged).</returns>
    private static async Task StartSnippingAsync(WindowsScreenshotsIntegration integration, bool enable)
    {
        try
        {
            await integration.StartAsync(enable);
        }
        catch (Exception ex)
        {
            // Optional like ShareX: the rest of the app keeps working without it.
            AppLog.Error("Starting the Windows screenshots integration failed.", ex);
        }
    }

    /// <summary>
    /// Follows the setting (idempotent: the integration ignores an unchanged value) and tells the tab, which follows the
    /// setting even before the watch has changed.
    /// </summary>
    /// <param name="next">The new settings.</param>
    /// <returns>A task completing once applied.</returns>
    private async Task ApplySnippingSettingsAsync(AppSettings next)
    {
        if (snipping is not null)
        {
            await snipping.SetEnabledAsync(next.ImportWindowsScreenshots);
        }

        SnippingStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Locates the folder again when nothing is being watched while the setting is on — the first screenshot may just have
    /// created it — so the tab's next load sees screenshots without waiting for the periodic check. Cheap otherwise.
    /// </summary>
    private void RefreshSnippingIfWaiting()
    {
        if (snipping is { IsWatching: false } integration && IsSnippingTabAvailable)
        {
            _ = integration.RefreshAsync();
        }
    }

    /// <summary>Stops the watch before the history closes: the catch-up marker must only move for screenshots really stored.</summary>
    /// <returns>A task completing once stopped.</returns>
    private async Task DisposeSnippingAsync()
    {
        if (snipping is not null)
        {
            await snipping.DisposeAsync();
        }
    }
}
