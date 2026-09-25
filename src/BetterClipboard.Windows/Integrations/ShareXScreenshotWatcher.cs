using System.Collections.Concurrent;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;
using BetterClipboard.Windows.Imaging;

namespace BetterClipboard.Windows.Integrations;

/// <summary>
/// Turns every screenshot ShareX saves into a history item the moment the file is complete.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why files and not ShareX's history database:</b> ShareX writes the image to its final path right
/// after capture, but appends its <c>History.db</c> row only when the whole task ends — after any
/// upload, and not at all when history saving is off. The file is the earliest reliable signal.
/// </para>
/// <para>
/// <b>Pipeline per file:</b>
/// <list type="bullet">
/// <item>only paths that <see cref="ShareXInstallation.IsScreenshotFile"/> accepts (image extension, not a
/// thumbnail, in a folder ShareX's patterns produce);</item>
/// <item>events for one path are debounced by <see cref="SettleDelay"/>;</item>
/// <item>the file is opened read-only with <see cref="FileShare.Read"/>, which fails while ShareX still
/// holds a write handle, so partial files are never read;</item>
/// <item>it is decoded (retried until <see cref="ReadTimeout"/>, in case the write is still settling);</item>
/// <item>animated GIFs (screen recordings) and files over the size limit are skipped;</item>
/// <item>the capture goes out with <see cref="ClipOrigin.ShareX"/>.</item>
/// </list>
/// Files are processed one at a time, in arrival order.
/// </para>
/// <para>
/// <b>Dedupe:</b> a path+size+write-time key stops the several events of one save from importing twice.
/// Across channels (ShareX also copying the screenshot to the clipboard) the history's pixel hash merges
/// the file and the clipboard copy into one item.
/// </para>
/// </remarks>
public sealed class ShareXScreenshotWatcher : IDisposable
{
    /// <summary>Bound on remembered dedupe keys (a safety net; the store's hash is the real dedupe).</summary>
    private const int MaxRememberedFiles = 5000;

    private readonly Func<ClipCapture, Task<bool>> deliver;
    private readonly Func<long> maxBytes;
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly Lock watchersGate = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> handled = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private volatile ShareXInstallation installation;
    private volatile bool disposed;

    /// <summary>
    /// Creates a watcher (nothing is watched until <see cref="Start"/>).
    /// </summary>
    /// <param name="installation">Where ShareX saves screenshots (from <see cref="ShareXLocator"/>).</param>
    /// <param name="deliver">Stores a capture; returns whether it became (or refreshed) a history item. Called on a pool thread, one file at a time.</param>
    /// <param name="maxBytes">Current largest-item limit in bytes; bigger files are skipped before being read.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public ShareXScreenshotWatcher(ShareXInstallation installation, Func<ClipCapture, Task<bool>> deliver, Func<long> maxBytes)
    {
        this.installation = installation ?? throw new ArgumentNullException(nameof(installation));
        this.deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
        this.maxBytes = maxBytes ?? throw new ArgumentNullException(nameof(maxBytes));
    }

    /// <summary>
    /// Raised after a screenshot file was dealt with — stored, merged, or deliberately skipped (paused,
    /// ignored, too large, an animation) — with the file's write time. Pool thread.
    /// </summary>
    /// <remarks>
    /// This is the "handled up to" signal for the startup catch-up, so it fires for skips too: a
    /// screenshot taken while capture was paused must not be imported later by the catch-up.
    /// </remarks>
    public event EventHandler<DateTimeOffset>? Handled;

    /// <summary>
    /// Raised when Windows dropped change events (buffer overflow, network folder hiccup). The caller should
    /// run <see cref="CatchUpAsync"/> from its last marker — events are gone, the files are not.
    /// </summary>
    public event EventHandler? EventsLost;

    /// <summary>Quiet time after the last event for a file before it is read (ShareX's write plus a margin).</summary>
    public TimeSpan SettleDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long to keep retrying a file that is locked or does not decode yet before giving up on it.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The installation currently watched.</summary>
    public ShareXInstallation Installation => installation;

    /// <summary>Folders actually being watched (existing ones among <see cref="ShareXInstallation.WatchFolders"/>).</summary>
    public IReadOnlyList<string> WatchedFolders
    {
        get
        {
            lock (watchersGate)
            {
                return watchers.Select(w => w.Path).ToArray();
            }
        }
    }

    /// <summary>
    /// Starts watching every existing folder of <see cref="Installation"/> (recursively). Folders that do
    /// not exist yet are skipped; <see cref="Update"/> picks them up once they do.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The watcher was disposed.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (watchersGate)
        {
            foreach (var folder in installation.WatchFolders)
            {
                if (!Directory.Exists(folder))
                {
                    AppLog.Info("A ShareX screenshots folder does not exist yet; not watching it.");
                    continue;
                }

                try
                {
                    var watcher = new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,

                        // 64 KB, the documented maximum: a burst of captures must not overflow the default 8 KB.
                        InternalBufferSize = 64 * 1024,
                    };
                    watcher.Created += (_, e) => Schedule(e.FullPath);
                    watcher.Changed += (_, e) => Schedule(e.FullPath);
                    watcher.Renamed += (_, e) => Schedule(e.FullPath);
                    watcher.Error += (_, e) =>
                    {
                        AppLog.Warn($"The ShareX folder watch reported an error ({e.GetException().GetType().Name}); catching up.");
                        EventsLost?.Invoke(this, EventArgs.Empty);
                    };
                    watcher.EnableRaisingEvents = true;
                    watchers.Add(watcher);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    AppLog.Warn($"Cannot watch a ShareX screenshots folder: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Switches to a re-located installation (ShareX's settings changed, a folder appeared). The folder
    /// watches are re-created only when the set of existing folders to watch differs.
    /// </summary>
    /// <param name="updated">The new location facts.</param>
    /// <returns>Whether the watched folders changed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="updated"/> is <see langword="null"/>.</exception>
    public bool Update(ShareXInstallation updated)
    {
        ArgumentNullException.ThrowIfNull(updated);

        // Swap first: matching (IsScreenshotFile) uses the new rules even when the roots stay the same.
        installation = updated;
        var wanted = updated.WatchFolders.Where(Directory.Exists).ToArray();
        bool changed = !WatchedFolders.SequenceEqual(wanted, StringComparer.OrdinalIgnoreCase);
        if (changed && !disposed)
        {
            StopWatching();
            Start();
        }

        return changed;
    }

    /// <summary>
    /// Imports screenshots saved while nobody was watching (BetterClipboard not running, lost events):
    /// candidate files written after <paramref name="since"/>, oldest first, at most the
    /// <paramref name="maxFiles"/> newest.
    /// </summary>
    /// <param name="since">Only files written after this instant.</param>
    /// <param name="maxFiles">Cap, so a long absence cannot flood the history.</param>
    /// <param name="cancellationToken">Cancels between files.</param>
    /// <returns>How many screenshots were delivered.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<int> CatchUpAsync(DateTimeOffset since, int maxFiles, CancellationToken cancellationToken)
    {
        var current = installation;
        var files = new List<FileInfo>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        foreach (var folder in current.WatchFolders.Where(Directory.Exists))
        {
            try
            {
                files.AddRange(new DirectoryInfo(folder).EnumerateFiles("*", options)
                    .Where(file => file.LastWriteTimeUtc > since.UtcDateTime && current.IsScreenshotFile(file.FullName)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Could not scan a ShareX screenshots folder: {ex.Message}");
            }
        }

        int delivered = 0;
        foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc).TakeLast(Math.Max(0, maxFiles)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            delivered += await ProcessAsync(file.FullName).ConfigureAwait(false) ? 1 : 0;
        }

        return delivered;
    }

    /// <summary>Stops watching; queued and in-flight files are abandoned.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        shutdown.Cancel();
        StopWatching();
        foreach (var cts in pending.Values)
        {
            cts.Cancel();
        }
    }

    /// <summary>Debounces events for one path, then processes it.</summary>
    /// <param name="path">Changed file.</param>
    private void Schedule(string path)
    {
        if (disposed || !installation.IsScreenshotFile(path))
        {
            return;
        }

        var mine = new CancellationTokenSource();
        pending.AddOrUpdate(path, mine, (_, previous) =>
        {
            previous.Cancel();
            return mine;
        });

        _ = Task.Delay(SettleDelay, mine.Token).ContinueWith(
            async delay =>
            {
                // Remove only our own entry: a newer event may already have replaced it.
                pending.TryRemove(new KeyValuePair<string, CancellationTokenSource>(path, mine));
                mine.Dispose();
                if (!delay.IsCanceled && !disposed)
                {
                    await ProcessAsync(path).ConfigureAwait(false);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>Reads, checks, converts and delivers one file (serialized), then reports it as handled.</summary>
    /// <param name="path">File.</param>
    /// <returns>Whether a capture was delivered and stored.</returns>
    private async Task<bool> ProcessAsync(string path)
    {
        try
        {
            await serial.WaitAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        DateTimeOffset? writtenUtc = null;
        try
        {
            // Cheap pre-check before reading and decoding: a late duplicate event (or the catch-up meeting a
            // file the live watch already took) must not cost a full decode of a large screenshot.
            var before = new FileInfo(path);
            if (before.Exists && handled.ContainsKey(DedupeKey(path, before.Length, new DateTimeOffset(before.LastWriteTimeUtc, TimeSpan.Zero))))
            {
                return false;
            }

            var loaded = await LoadWhenCompleteAsync(path).ConfigureAwait(false);
            writtenUtc = loaded.WrittenUtc;
            if (loaded.Screenshot is not { } file)
            {
                return false;
            }

            if (handled.Count > MaxRememberedFiles)
            {
                handled.Clear();
            }

            if (!handled.TryAdd(DedupeKey(path, file.Length, file.WrittenUtc), 0))
            {
                writtenUtc = null; // another event of the same save: already reported
                return false;
            }

            var capture = new ClipCapture
            {
                Formats = file.Formats,
                CapturedAtUtc = file.WrittenUtc,
                Source = new SourceAppInfo("ShareX", installation.ExecutablePath, "ShareX"),
                Origin = ClipOrigin.ShareX,
            };

            return await deliver(capture).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            writtenUtc = null; // shutting down: the next start's catch-up handles the file
            return false;
        }
        catch (Exception ex)
        {
            // Storing failed (disk full, history shutting down): not handled, so the next catch-up retries
            // it. Never let one bad file stop the watcher; the log names no path (file names can hold
            // window titles).
            writtenUtc = null;
            AppLog.Warn($"A ShareX screenshot could not be imported ({ex.GetType().Name}).");
            return false;
        }
        finally
        {
            serial.Release();
            if (writtenUtc is { } when)
            {
                Handled?.Invoke(this, when);
            }
        }
    }

    /// <summary>
    /// Waits until the file can be read without a writer holding it and decodes it; skips animations and
    /// oversize files.
    /// </summary>
    /// <param name="path">File.</param>
    /// <returns>
    /// The converted screenshot (or <see langword="null"/> when skipped, vanished or never readable) and the
    /// file's write time (<see langword="null"/> only when the file vanished before it was seen).
    /// </returns>
    /// <exception cref="OperationCanceledException">The watcher is shutting down.</exception>
    private async Task<(LoadedScreenshot? Screenshot, DateTimeOffset? WrittenUtc)> LoadWhenCompleteAsync(string path)
    {
        var deadline = DateTime.UtcNow + ReadTimeout;
        Exception? last = null;
        DateTimeOffset? writtenUtc = null;
        while (DateTime.UtcNow < deadline)
        {
            shutdown.Token.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return (null, writtenUtc);
            }

            writtenUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (info.Length > maxBytes())
            {
                AppLog.Info($"Skipped a {info.Length:N0}-byte ShareX screenshot (over the size limit).");
                return (null, writtenUtc);
            }

            if (info.Length > 0)
            {
                try
                {
                    byte[] bytes;

                    // FileShare.Read: the open fails while ShareX still has the file open for writing.
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        bytes = new byte[stream.Length];
                        await stream.ReadExactlyAsync(bytes, shutdown.Token).ConfigureAwait(false);
                    }

                    if (string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase)
                        && await ImageCodec.GetFrameCountAsync(bytes, shutdown.Token).ConfigureAwait(false) > 1)
                    {
                        AppLog.Info("Skipped an animated GIF from ShareX (a screen recording, not a screenshot).");
                        return (null, writtenUtc);
                    }

                    var formats = await ImageCodec.ToClipboardFormatsAsync(bytes, shutdown.Token).ConfigureAwait(false);
                    info.Refresh();
                    writtenUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                    return (new LoadedScreenshot(formats, bytes.Length, writtenUtc.Value), writtenUtc);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    last = ex; // still being written, or briefly locked by a scanner
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    last = ex; // decoder rejected a file that may still be incomplete
                }
            }

            await Task.Delay(100, shutdown.Token).ConfigureAwait(false);
        }

        AppLog.Warn($"Gave up on a ShareX screenshot that stayed unreadable ({last?.GetType().Name ?? "empty file"}).");
        return (null, writtenUtc);
    }

    /// <summary>Identity of one saved version of a file (a rewrite of the same path is a new screenshot).</summary>
    /// <param name="path">File path.</param>
    /// <param name="length">Size in bytes.</param>
    /// <param name="writtenUtc">Last write time.</param>
    /// <returns>The key.</returns>
    private static string DedupeKey(string path, long length, DateTimeOffset writtenUtc) => $"{path}|{length}|{writtenUtc.UtcTicks}";

    /// <summary>Disposes all folder watchers.</summary>
    private void StopWatching()
    {
        lock (watchersGate)
        {
            foreach (var watcher in watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }

            watchers.Clear();
        }
    }

    /// <summary>A screenshot ready to store.</summary>
    /// <param name="Formats">PNG + DIBV5.</param>
    /// <param name="Length">File size (dedupe key part).</param>
    /// <param name="WrittenUtc">File write time (capture time and dedupe key part).</param>
    private readonly record struct LoadedScreenshot(IReadOnlyList<ClipFormatData> Formats, long Length, DateTimeOffset WrittenUtc);
}
