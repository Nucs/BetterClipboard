using System.Diagnostics;
using BetterClipboard.Core;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Updates;
using BetterClipboard.Windows.Input;
using BetterClipboard.Windows.Updates;

namespace BetterClipboard.App;

/// <summary>
/// The update system's half of the controller: owns the <see cref="UpdateService"/>, decides which copy may check and
/// install by itself, and offers the panel's update dialog and the Settings card what they need.
/// </summary>
/// <remarks>
/// <para>
/// <b>What leaves this PC.</b> While <see cref="AppSettings.CheckForUpdates"/> is on, one request a day for this
/// project's public release list on GitHub (no account, no identifier; the request names only the app version), plus the
/// package and its checksum list once the user approves an update. Nothing else in BetterClipboard uses the network.
/// </para>
/// <para>
/// <b>Which copy does what</b> (<see cref="UpdateInstallKind"/>): a copy installed by <c>install.ps1</c> checks by itself
/// and can replace itself; one extracted by hand checks but is sent to the download page (an update swaps the whole
/// folder, which is only safe for a folder the installer made); a Chocolatey copy does neither — its package manager
/// owns its files. An isolated instance (<see cref="AppPaths.DataDirectoryVariable"/>) never checks by itself, so tests
/// and dev runs do not use up the user's request allowance at GitHub; only such an instance may be pointed at a stand-in
/// feed (<see cref="UpdateSource.FeedVariable"/>), so a variable set in the user's environment cannot redirect the
/// installed app's updates.
/// </para>
/// </remarks>
public sealed partial class AppController
{
    /// <summary>
    /// The installer's transcript in the log folder. The name matches the app log's pattern, so the 14-day log cleanup
    /// removes it too.
    /// </summary>
    private const string UpdateLogFileName = "betterclipboard-update.log";

    /// <summary>How old the release list may be when the update dialog opens before it is asked for again.</summary>
    private static readonly TimeSpan UpdateDialogFreshness = TimeSpan.FromMinutes(5);

    /// <summary>The longest installer message shown in a notification; Windows cuts balloon text at 255 characters.</summary>
    private const int MaxNotificationText = 220;

    /// <summary>The update service (created in <see cref="Start"/>, disposed on exit); <see langword="null"/> when it could not start.</summary>
    private UpdateService? updates;

    /// <summary>Where releases come from: GitHub, or a stand-in feed for an isolated test instance.</summary>
    private UpdateSource updateSource = UpdateSource.GitHub;

    /// <summary>
    /// Raised on the UI thread when anything about updates changed: a check started or ended, an update was found, a
    /// download moved on (up to ten times a second), or the settings changed what is offered.
    /// </summary>
    public event EventHandler? UpdateStatusChanged;

    /// <summary>
    /// What is known about updates right now, or <see langword="null"/> when the update system did not start (the panel
    /// then has no update button and Settings says so). Cheap to read from any thread.
    /// </summary>
    public UpdateSnapshot? UpdateStatus => updates?.Current;

    /// <summary>The page that lists every release, for "Open download page" and "View on GitHub".</summary>
    public Uri ReleasesPage => updateSource.ReleasesPage;

    /// <summary>Where the installer of an approved update writes what it did.</summary>
    public string UpdateLogPath => Path.Combine(paths.LogDirectory, UpdateLogFileName);

    /// <summary>
    /// Creates and starts the update service, and tells the user how an update that ran before this start ended. A
    /// failure is logged and leaves the app without updates: capture, paste and the tabs do not depend on them.
    /// </summary>
    private void StartUpdates()
    {
        try
        {
            if (!SemanticVersion.TryParse(Version, out var current))
            {
                AppLog.Warn($"Updates are unavailable: this build's version ({Version}) is not a release version.");
                return;
            }

            updateSource = ResolveUpdateSource();
            var appDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            var kind = InstalledCopyLocator.Detect(appDirectory, paths.InstallerStatePath, isolatedInstance: paths.InstanceScope is not null);
            var service = new UpdateService(
                new UpdateClient(updateSource, Version),
                new UpdateServiceOptions
                {
                    CurrentVersion = current,
                    Architecture = InstalledCopyLocator.OsArchitecture,
                    InstallKind = kind,
                    InstallDirectory = appDirectory,
                    WorkDirectory = paths.UpdatesDirectory,
                    InstallerLogPath = UpdateLogPath,
                    Settings = () => Settings.Current,

                    // Chocolatey updates its own copy. An isolated instance stays silent unless a test gave it a feed.
                    AllowAutomaticChecks = kind != UpdateInstallKind.Chocolatey && (paths.InstanceScope is null || updateSource.IsOverride),
                    Installer = kind == UpdateInstallKind.Installer ? new ScriptUpdateInstaller() : null,
                });
            service.Changed += (_, _) => ui.TryEnqueue(() => UpdateStatusChanged?.Invoke(this, EventArgs.Empty));

            // Before Start, which empties the working folder: the installer's report of the update that led to this start.
            var last = service.TakeLastResult();
            updates = service;
            service.Start();
            AppLog.Info($"Updates: this copy is {DescribeInstallKind(kind)}; automatic check {(service.Current.AutomaticChecks ? "on" : "off")}.");
            if (last is not null)
            {
                ReportLastUpdate(last);
            }
        }
        catch (Exception ex)
        {
            // Optional like the integrations: the rest of the app keeps working without it.
            AppLog.Error("Starting the update system failed.", ex);
        }
    }

    /// <summary>
    /// Picks the release feed: GitHub, unless this is an isolated instance that a test pointed at a stand-in feed.
    /// </summary>
    /// <returns>The source to use.</returns>
    private UpdateSource ResolveUpdateSource()
    {
        var feed = Environment.GetEnvironmentVariable(UpdateSource.FeedVariable);
        if (string.IsNullOrWhiteSpace(feed))
        {
            return UpdateSource.GitHub;
        }

        if (paths.InstanceScope is null)
        {
            // The installed app only ever takes updates from GitHub, whatever its environment says.
            AppLog.Warn($"{UpdateSource.FeedVariable} is ignored: only an isolated instance ({AppPaths.DataDirectoryVariable}) may use a stand-in update feed.");
            return UpdateSource.GitHub;
        }

        if (UpdateSource.TryCreateOverride(feed) is { } standIn)
        {
            AppLog.Info("Updates: this isolated instance uses a stand-in release feed.");
            return standIn;
        }

        AppLog.Warn($"{UpdateSource.FeedVariable} is not a usable feed address (https, or http on this PC) and is ignored.");
        return UpdateSource.GitHub;
    }

    /// <summary>Words an install kind for the log.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>A short description.</returns>
    private static string DescribeInstallKind(UpdateInstallKind kind) => kind switch
    {
        UpdateInstallKind.Installer => "installed by the installer (it can update itself)",
        UpdateInstallKind.Chocolatey => "a Chocolatey install (Chocolatey updates it)",
        _ => "not installed by the installer (updates come from the download page)",
    };

    /// <summary>
    /// Tells the user how the update that restarted the app ended: the installer ran after the old version closed, so
    /// its report (<see cref="UpdateResult"/>) is the only way the outcome reaches them.
    /// </summary>
    /// <param name="result">The installer's report.</param>
    private void ReportLastUpdate(UpdateResult result)
    {
        if (result.Succeeded)
        {
            AppLog.Info($"The update to {Version} was installed.");
            tray?.ShowNotification($"BetterClipboard was updated to {Version}", "The update button in the panel shows what is new.");
            return;
        }

        var version = result.Version.Length > 0 ? $" to {result.Version}" : string.Empty;
        AppLog.Warn($"The update{version} was not installed: {result.Error}");
        var reason = result.Error.Length > MaxNotificationText ? result.Error[..MaxNotificationText] + "…" : result.Error;
        tray?.ShowNotification(
            $"The update{version} was not installed",
            reason.Length > 0 ? reason : $"The installed version is unchanged. Details: {UpdateLogPath}");
    }

    /// <summary>"Check now": asks the feed for the release list at once, whatever the automatic setting says.</summary>
    /// <returns>A task completing when the check ended; it never fails (a failure is in <see cref="UpdateStatus"/>).</returns>
    public Task CheckForUpdatesAsync() => updates?.CheckAsync(userInitiated: true) ?? Task.CompletedTask;

    /// <summary>
    /// Makes the release list current for the update dialog (asked again when older than five minutes). Requests nothing
    /// while this copy does not check by itself: opening a dialog must not undo "never ask GitHub".
    /// </summary>
    /// <returns>A task completing when the list is fresh enough or the check ended; it never fails.</returns>
    public Task RefreshUpdatesForDialogAsync() => updates?.EnsureFreshAsync(UpdateDialogFreshness) ?? Task.CompletedTask;

    /// <summary>
    /// The user approved the offered update: download, verify, then the installer closes the app and starts the new
    /// version. Does nothing when this copy cannot install updates or none is offered.
    /// </summary>
    /// <returns>A task completing when the attempt ended inside this process (failure or cancellation); it never fails.</returns>
    public Task InstallUpdateAsync() => updates?.InstallAsync() ?? Task.CompletedTask;

    /// <summary>Cancels an approved update while it downloads or is verified; too late once the installer runs.</summary>
    public void CancelUpdate() => updates?.CancelInstall();

    /// <summary>
    /// Skips the offered version (the update button stops being highlighted for it; a newer one highlights it again), or
    /// stops skipping it.
    /// </summary>
    /// <param name="skip"><see langword="true"/> to skip the offered version, <see langword="false"/> to offer it again.</param>
    public void SkipOfferedUpdate(bool skip)
    {
        var offered = updates?.Current.Offer?.Release.Version.ToString();
        if (skip && offered is null)
        {
            return;
        }

        try
        {
            Settings.Update(s => s with { SkippedUpdateVersion = skip ? offered! : string.Empty });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Saving the skipped update failed.", ex);
        }
    }

    /// <summary>
    /// Opens a web link from the update dialog (a release page, a link inside release notes) in the default browser.
    /// </summary>
    /// <remarks>
    /// Only http and https: release notes are text from the network, and a link in them must never start a program or
    /// open a local file. The Markdown parser already keeps other schemes out; this is the second lock on the same door.
    /// </remarks>
    /// <param name="link">The link.</param>
    /// <returns><see langword="true"/> when the link was handed to Windows.</returns>
    public bool OpenWebLink(Uri? link)
    {
        if (link is not { IsAbsoluteUri: true } || (link.Scheme != Uri.UriSchemeHttps && link.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Warn($"Opening a link in the browser failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Puts a command on the clipboard (the dialog's "Copy command" for copies that do not update themselves). Written
    /// through the clipboard monitor, so it counts as BetterClipboard's own write and is not recorded as a new copy.
    /// </summary>
    /// <param name="text">The text to copy.</param>
    /// <returns>A task completing when the text is on the clipboard; failures are logged, never thrown.</returns>
    public async Task CopyTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text) || monitor is null)
        {
            return;
        }

        try
        {
            await monitor.WriteAsync([new ClipFormatData(ClipFormatNames.UnicodeText, UnicodeTextCodec.Encode(text))]);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Copying a command to the clipboard failed: {ex.Message}");
        }
    }

    /// <summary>Opens the panel with the update dialog showing (the tray menu's "Update…" entry).</summary>
    public void ShowUpdate()
    {
        if (exiting || updates is null)
        {
            return;
        }

        var window = EnsureFlyout();
        window.OpenUpdateDialogWhenShown();
        if (window.IsOpen)
        {
            return;
        }

        ShowFlyout(ForegroundContext.CursorOnly());
    }

    /// <summary>Follows a settings change: whether this copy checks by itself, and whether the offered version is skipped.</summary>
    private void ApplyUpdateSettings() => updates?.ApplySettings();

    /// <summary>
    /// Stops the schedule and cancels a check or download in flight. A started installer keeps running: it is what asked
    /// the app to exit.
    /// </summary>
    /// <returns>A task completing when nothing of the update service runs any more.</returns>
    private async Task DisposeUpdatesAsync()
    {
        if (updates is { } service)
        {
            updates = null;
            await service.DisposeAsync();
        }
    }
}
