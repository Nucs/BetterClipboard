using System.ComponentModel;
using System.Diagnostics;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Model;

namespace BetterClipboard.App;

/// <summary>What <see cref="AppController.SaveImageToTempAsync"/> did.</summary>
/// <param name="Path">The saved picture's full path, or <see langword="null"/> when it could not be saved.</param>
/// <param name="Problem">Why not, in words for the panel's footer (already translated); <see langword="null"/> on success.</param>
public sealed record TempImageSave(string? Path, string? Problem);

/// <summary>
/// A picture's file in the controller (CLAUDE.md §2.49): the card menu's <em>Copy path</em>, <em>Copy image as a file</em>,
/// <em>Open file</em> and <em>Open folder</em> on the file a screenshot was saved to, <em>Save to temp and</em>'s temporary
/// copy, and the start's clean-up of those copies.
/// </summary>
/// <remarks>
/// <para>
/// <b>Clipboard.</b> A path or a file put on the clipboard goes through the clipboard monitor, so it is BetterClipboard's own
/// write and is not recorded as a new copy: the picture's entry already holds what it came from, and its menu gives the
/// path again at any time. Like <em>Copy only</em>, it counts as using the picture (it moves to the top when
/// <c>MoveToTopOnPaste</c> is on), and the panel goes away with the focus handed back.
/// </para>
/// <para>
/// <b>Opening.</b> The file or folder is opened while the panel still holds the foreground, and the panel hides after (the
/// Run tab's order): the app Windows starts may then come to the front. A failure keeps the panel up with the reason in its
/// footer. Logs never name a path.
/// </para>
/// <para>
/// <b>Never on the UI thread.</b> <c>ShellExecuteEx</c> can run a message loop of its own while Windows activates the app
/// (a packaged app such as Photos is started through COM and DDE). On the UI thread that loop let WinUI re-enter from inside
/// the click that asked for the file, and WinUI ended the process (0xC000027B in <c>Microsoft.UI.Xaml.dll</c>: the viewer's
/// file-name link, found in the QA of 2026-11-13 and proven with a crash dump whose UI thread stood in <c>ShellExecuteExW</c>
/// under the link's Click handler). So the start runs on the thread pool, where .NET calls <c>ShellExecuteEx</c> on an STA
/// thread of its own, and the panel hides once it returned.
/// </para>
/// </remarks>
public sealed partial class AppController
{
    /// <summary>
    /// The folder <em>Save to temp and</em> writes to: <c>%TEMP%\BetterClipboard\Images</c>, or a scoped one for an isolated
    /// instance, so a test instance's clean-up never deletes the regular instance's copies (<see cref="TempImageFiles.RootIn"/>).
    /// </summary>
    public string TempImagesFolder => TempImageFiles.RootIn(Path.GetTempPath(), paths.InstanceScope);

    /// <summary>
    /// Puts a picture's file on the clipboard — as a file (<c>CF_HDROP</c>, drop effect "copy", like Explorer's Copy) or as
    /// its path in text — then hides the panel, handing the focus back to the app it came from.
    /// </summary>
    /// <param name="entry">The picture's entry (moved to the top when <c>MoveToTopOnPaste</c> is on).</param>
    /// <param name="path">The file: the picture's own, or its temporary copy.</param>
    /// <param name="asText">Put the path as text (<em>Copy path</em>) instead of the file.</param>
    /// <returns><see langword="true"/> when the clipboard holds it; failures are logged, never thrown.</returns>
    public async Task<bool> CopyImageFileAsync(ClipEntry entry, string path, bool asText)
    {
        try
        {
            await monitor!.WriteAsync(ReplayFormats.ForFiles([path], asText));
            if (Settings.Current.MoveToTopOnPaste)
            {
                _ = History.MarkUsedAsync(entry.Id);
            }

            flyout?.Dismiss(restoreFocus: true);
            return true;
        }
        catch (Exception ex)
        {
            // Content-free: never the path.
            AppLog.Error($"Copying the file of entry {entry.Id} to the clipboard failed.", ex);
            return false;
        }
    }

    /// <summary>
    /// Opens a file with the app Windows opens it with (<em>Open file</em>), then hides the panel. Call it from the UI
    /// thread; the start itself runs off it (see the class remarks).
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>
    /// A task giving <see langword="null"/> when it opened; else why not, for the panel's footer (logged without the path).
    /// Never faults.
    /// </returns>
    public Task<string?> OpenImageFileAsync(string path) =>
        LaunchAsync(new ProcessStartInfo(path) { UseShellExecute = true }, "Opening a picture's file");

    /// <summary>
    /// Opens the folder of a file in File Explorer with the file selected (<em>Open folder</em>), then hides the panel. Call
    /// it from the UI thread; the start itself runs off it (see the class remarks).
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>A task giving <see langword="null"/> when Explorer was started; else why not, for the panel's footer. Never faults.</returns>
    public Task<string?> OpenImageFolderAsync(string path) =>
        LaunchAsync(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }, "Opening a picture's folder");

    /// <summary>
    /// Writes a picture as a PNG into <see cref="TempImagesFolder"/> for <em>Save to temp and</em>: under its file's name when
    /// one is known, else <c>Image yyyy-MM-dd HHmmss.png</c>; a save of the same picture finds the earlier copy, and never
    /// writes over one edited since (<see cref="TempImageFiles.Save"/>).
    /// </summary>
    /// <param name="entry">The picture's entry.</param>
    /// <returns>The saved file, or why it could not be saved (logged, never thrown).</returns>
    public async Task<TempImageSave> SaveImageToTempAsync(ClipEntry entry)
    {
        byte[]? png;
        try
        {
            png = await GetImagePngAsync(entry.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"Reading the picture of entry {entry.Id} for a temporary copy failed: {ex.Message}");
            png = null;
        }

        if (png is null)
        {
            return new TempImageSave(null, UiText.Translate("This image could not be loaded."));
        }

        var name = TempImageFiles.FileNameFor(entry.File, entry.CreatedUtc, TimeZoneInfo.Local);
        var folder = TempImagesFolder;
        try
        {
            // Off the UI thread: the disk may be slow, and an earlier copy is read back to compare.
            var path = await Task.Run(() => TempImageFiles.Save(folder, entry.ContentHash, name, png, DateTimeOffset.UtcNow));
            AppLog.Info($"Saved the picture of entry {entry.Id} to the temporary pictures folder ({png.Length:N0} bytes).");
            return new TempImageSave(path, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Warn($"Saving the picture of entry {entry.Id} to the temporary pictures folder failed ({ex.GetType().Name}: {ex.Message}).");
            return new TempImageSave(null, UiText.Format($"Could not save the picture: {ex.Message}"));
        }
    }

    /// <summary>
    /// The start's clean-up of <em>Save to temp and</em>'s pictures: deletes the ones not used for longer than
    /// <c>TempImageDays</c> (every one when it is 0), on the thread pool. Logged when it deleted anything; never thrown.
    /// </summary>
    /// <remarks>
    /// Only at the start, never while BetterClipboard runs: a picture just pasted as a file into an app that reads it later
    /// must not vanish under it. A file another app holds open is kept until a later start.
    /// </remarks>
    private void CleanTemporaryPictures()
    {
        var folder = TempImagesFolder;
        int keepDays = Settings.Current.TempImageDays;
        _ = Task.Run(() =>
        {
            try
            {
                int deleted = TempImageFiles.Clean(folder, DateTimeOffset.UtcNow, keepDays);
                if (deleted > 0)
                {
                    // 0 days is "until the next start" (the QA's first wording, "older than 0 days", read as a contradiction).
                    string plural = deleted == 1 ? string.Empty : "s";
                    AppLog.Info(keepDays <= 0
                        ? $"Deleted {deleted} temporary picture{plural} (Settings keeps them only until the next start)."
                        : $"Deleted {deleted} temporary picture{plural} not used for more than {keepDays} day{(keepDays == 1 ? string.Empty : "s")}.");
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Cleaning the temporary pictures folder failed ({ex.GetType().Name}: {ex.Message}).");
            }
        });
    }

    /// <summary>
    /// Starts a program for a picture's file while the panel still holds the foreground, then hides the panel (the opened
    /// window may come to the front); a failure keeps the panel up.
    /// </summary>
    /// <remarks>
    /// The start runs on the thread pool, never on the UI thread (the class remarks say why); the UI thread stays free
    /// meanwhile, and the continuation comes back to it to hide the panel. Foreground rights belong to the process, so the
    /// app started from the pool may come to the front as it did from the UI thread. As the Run tab does, a panel the user
    /// put away and summoned again during the start is hidden by it as well: the start takes a fraction of a second.
    /// </remarks>
    /// <param name="start">What to start.</param>
    /// <param name="what">The log line's subject ("Opening a picture's file").</param>
    /// <returns>A task giving <see langword="null"/> on success, else the reason in words for the footer. Never faults.</returns>
    private async Task<string?> LaunchAsync(ProcessStartInfo start, string what)
    {
        try
        {
            // On a pool thread (MTA) .NET runs ShellExecuteEx on a new STA thread and waits for it there: no message loop
            // of Windows' runs on the UI thread, inside the XAML event that asked for the start.
            await Task.Run(() => Process.Start(start)?.Dispose()).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            // Content-free: Windows' own reason (no app for the file type, access denied), never the path.
            AppLog.Warn($"{what} failed: {ex.Message}");
            return UiText.Format($"Could not open it: {ex.Message}");
        }

        flyout?.Dismiss(restoreFocus: false);
        return null;
    }
}
