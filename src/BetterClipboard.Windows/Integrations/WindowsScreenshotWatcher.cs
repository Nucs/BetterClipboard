using System.Collections.Concurrent;
using System.Diagnostics;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Integrations;
using BetterClipboard.Core.Model;
using BetterClipboard.Windows.Imaging;

namespace BetterClipboard.Windows.Integrations;

/// <summary>
/// Turns every screenshot Windows' own tools save — Snipping Tool's auto-save and Win+PrtScn — into a history item the
/// moment its file in the Screenshots folder is complete (the panel's Snipping tab; CLAUDE.md §2.18).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pipeline per file:</b>
/// <list type="bullet">
/// <item>one folder, not recursive: both tools save straight into it, and subfolders are the user's own sorting;</item>
/// <item>image files only (<see cref="WindowsScreenshots.IsImageFile"/>); events for one path are debounced by
/// <see cref="SettleDelay"/>;</item>
/// <item>cloud placeholders (OneDrive "online-only") are never opened: reading one downloads it;</item>
/// <item>the file is read with every sharing mode granted and is complete when its bytes say so
/// (<see cref="WindowsScreenshots.LooksComplete"/>), or — for formats without an end marker — when its size and write
/// time held still for <see cref="StableDelay"/>. Never with <see cref="FileShare.Read"/>, which denies writing: a writer
/// that closes and reopens its file (WinRT's StorageFile does) would fail if its reopen landed inside that open;</item>
/// <item>a live event's file whose write time, once complete, is older than <see cref="WindowsScreenshots.FreshWriteWindow"/>
/// was copied or moved in, and is skipped (the catch-up decides by its marker instead);</item>
/// <item>animated GIFs and files over the size limit are skipped;</item>
/// <item>the capture: PNG (a PNG file's own bytes) + DIBV5, the tool named from the file name
/// (<see cref="WindowsScreenshots.Classify"/>), origin <see cref="ClipOrigin.WindowsScreenshot"/>.</item>
/// </list>
/// Files are processed one at a time, in arrival order.
/// </para>
/// <para>
/// <b>Dedupe:</b> size + write time, not the path, so the several events of one save — and a rename right after it — import
/// a file once. Across channels (Snipping Tool also copies every snip to the clipboard) the history's pixel hash merges the
/// file and the copy into one item.
/// </para>
/// </remarks>
public sealed class WindowsScreenshotWatcher : IDisposable
{
    /// <summary>Bound on remembered dedupe keys (a safety net; the store's hash is the real dedupe).</summary>
    private const int MaxRememberedFiles = 5000;

    /// <summary>
    /// Attributes of a cloud placeholder whose data is not on this disk (recall on open / on data access, offline):
    /// opening one makes the sync engine download it.
    /// </summary>
    private const FileAttributes PlaceholderAttributes = (FileAttributes)0x00040000 | (FileAttributes)0x00400000 | FileAttributes.Offline;

    private readonly Func<ClipCapture, Task<bool>> deliver;
    private readonly Func<long> maxBytes;
    private readonly TimeProvider time;
    private readonly Lock watcherGate = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> handled = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private volatile WindowsScreenshotsLocation location;
    private FileSystemWatcher? watcher;
    private volatile bool disposed;

    /// <summary>Creates a watcher (nothing is watched until <see cref="Start"/>).</summary>
    /// <param name="location">Where screenshots go (from <see cref="WindowsScreenshotsLocator"/>).</param>
    /// <param name="deliver">Stores a capture; returns whether it became (or refreshed) a history item. Called on a pool thread, one file at a time.</param>
    /// <param name="maxBytes">Current largest-item limit in bytes; bigger files are skipped before being read.</param>
    /// <param name="time">Clock for the fresh-write rule; defaults to the system clock.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public WindowsScreenshotWatcher(WindowsScreenshotsLocation location, Func<ClipCapture, Task<bool>> deliver, Func<long> maxBytes, TimeProvider? time = null)
    {
        this.location = location ?? throw new ArgumentNullException(nameof(location));
        this.deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
        this.maxBytes = maxBytes ?? throw new ArgumentNullException(nameof(maxBytes));
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Raised after a new screenshot was dealt with — stored, merged, or deliberately skipped (paused, ignored, too large,
    /// an animation, undecodable) — with the file's write time. Pool thread.
    /// </summary>
    /// <remarks>
    /// The "handled up to" signal for the startup catch-up, so it fires for skips too: a screenshot taken while capture was
    /// paused must not be imported later by the catch-up. Files copied or moved in are not screenshots and do not raise it.
    /// </remarks>
    public event EventHandler<DateTimeOffset>? Handled;

    /// <summary>
    /// Raised when Windows dropped change events (buffer overflow). The caller should run <see cref="CatchUpAsync"/> from
    /// its last marker — events are gone, the files are not.
    /// </summary>
    public event EventHandler? EventsLost;

    /// <summary>Quiet time after the last event for a file before it is read (the tool's write plus a margin).</summary>
    public TimeSpan SettleDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long to keep checking a file that is incomplete or locked before giving up on it.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>For formats without an end marker: how long size and write time must hold still to call the file complete.</summary>
    public TimeSpan StableDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>The location currently used.</summary>
    public WindowsScreenshotsLocation Location => location;

    /// <summary>The folder being watched, or <see langword="null"/> when none (it does not exist yet, or the watch failed).</summary>
    public string? WatchedFolder
    {
        get
        {
            lock (watcherGate)
            {
                return watcher?.Path;
            }
        }
    }

    /// <summary>
    /// Starts watching <see cref="Location"/>'s folder. A folder that does not exist yet is skipped; <see cref="Update"/>
    /// picks it up once it does.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The watcher was disposed.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (watcherGate)
        {
            if (watcher is not null || location.Folder is not { } folder)
            {
                return;
            }

            if (!Directory.Exists(folder))
            {
                AppLog.Info("The Screenshots folder does not exist yet (Windows creates it with the first screenshot); not watching it.");
                return;
            }

            try
            {
                var created = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,

                    // 64 KB, the documented maximum: a burst of snips must not overflow the default 8 KB.
                    InternalBufferSize = 64 * 1024,
                };
                created.Created += (_, e) => Schedule(e.FullPath);
                created.Changed += (_, e) => Schedule(e.FullPath);
                created.Renamed += (_, e) => Schedule(e.FullPath);
                created.Error += (_, e) =>
                {
                    AppLog.Warn($"The Screenshots folder watch reported an error ({e.GetException().GetType().Name}); catching up.");
                    EventsLost?.Invoke(this, EventArgs.Empty);
                };
                created.EnableRaisingEvents = true;
                watcher = created;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Cannot watch the Screenshots folder: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Switches to a new snapshot of the facts (the folder moved or appeared, Snipping Tool was installed). The folder watch
    /// is re-created only when the folder to watch changed, or when it now exists and was not watched.
    /// </summary>
    /// <param name="updated">The new facts.</param>
    /// <returns>Whether the watched folder changed (the caller then catches up from its marker).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="updated"/> is <see langword="null"/>.</exception>
    public bool Update(WindowsScreenshotsLocation updated)
    {
        ArgumentNullException.ThrowIfNull(updated);

        // Swap first: a file processed from now on is attributed with the new Snipping Tool path.
        var previous = location;
        location = updated;
        var watched = WatchedFolder;
        bool wantWatch = updated.Folder is { } folder && Directory.Exists(folder);
        bool changed = !updated.WatchesSameAs(previous) || (watched is null && wantWatch) || (watched is not null && !wantWatch);
        if (changed && !disposed)
        {
            StopWatching();
            Start();
        }

        return changed;
    }

    /// <summary>
    /// Imports screenshots saved while nobody was watching (BetterClipboard not running, lost events): image files written
    /// after <paramref name="since"/>, oldest first, at most the <paramref name="maxFiles"/> newest. Cloud placeholders are
    /// left alone.
    /// </summary>
    /// <param name="since">Only files written after this instant.</param>
    /// <param name="maxFiles">Cap, so a long absence cannot flood the history.</param>
    /// <param name="cancellationToken">Cancels between files.</param>
    /// <returns>How many screenshots were delivered.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<int> CatchUpAsync(DateTimeOffset since, int maxFiles, CancellationToken cancellationToken)
    {
        if (location.Folder is not { } folder || !Directory.Exists(folder))
        {
            return 0;
        }

        List<FileInfo> files;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            files = new DirectoryInfo(folder).EnumerateFiles("*", options)
                .Where(file => file.LastWriteTimeUtc > since.UtcDateTime && WindowsScreenshots.IsImageFile(file.Name) && (file.Attributes & PlaceholderAttributes) == 0)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Could not scan the Screenshots folder: {ex.Message}");
            return 0;
        }

        int delivered = 0;
        foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc).TakeLast(Math.Max(0, maxFiles)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            delivered += await ProcessAsync(file.FullName, live: false).ConfigureAwait(false) ? 1 : 0;
        }

        return delivered;
    }

    /// <summary>Stops watching; queued and in-flight files are abandoned (the next start's catch-up finds them).</summary>
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

    /// <summary>Debounces events for one path, then processes it as a live event.</summary>
    /// <param name="path">Changed file.</param>
    private void Schedule(string path)
    {
        if (disposed || !WindowsScreenshots.IsImageFile(path))
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
                    await ProcessAsync(path, live: true).ConfigureAwait(false);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>Reads, checks, converts and delivers one file (serialized), then reports it as handled.</summary>
    /// <param name="path">File.</param>
    /// <param name="live">From a live event (the fresh-write rule applies) rather than the catch-up.</param>
    /// <returns>Whether a capture was delivered and stored.</returns>
    private async Task<bool> ProcessAsync(string path, bool live)
    {
        try
        {
            await serial.WaitAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        DateTimeOffset? handledAt = null;
        try
        {
            // Cheap checks before reading a large file: gone, a placeholder, or a save already dealt with.
            var before = new FileInfo(path);
            if (!before.Exists || (before.Attributes & PlaceholderAttributes) != 0 || handled.ContainsKey(Key(before.Length, before.LastWriteTimeUtc)))
            {
                return false;
            }

            var loaded = await LoadWhenCompleteAsync(path).ConfigureAwait(false);
            if (loaded.Outcome is LoadOutcome.Vanished or LoadOutcome.TimedOut)
            {
                return false;
            }

            if (live && !WindowsScreenshots.IsFresh(loaded.WrittenUtc, time.GetUtcNow()))
            {
                // Copied or moved in: not a screenshot taken now. Remembered so its later events are not read again.
                handled.TryAdd(Key(loaded.Length, loaded.WrittenUtc.UtcDateTime), 0);
                AppLog.Info("Skipped an image copied or moved into the Screenshots folder (not a new screenshot).");
                return false;
            }

            if (handled.Count > MaxRememberedFiles)
            {
                handled.Clear();
            }

            if (!handled.TryAdd(Key(loaded.Length, loaded.WrittenUtc.UtcDateTime), 0))
            {
                return false; // another event of the same save (or the catch-up meeting a file the live watch took)
            }

            handledAt = loaded.WrittenUtc;
            if (loaded.Outcome == LoadOutcome.TooLarge)
            {
                AppLog.Info($"Skipped a {loaded.Length:N0}-byte screenshot (over the size limit).");
                return false;
            }

            var bytes = loaded.Bytes!;
            if (Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase)
                && await ImageCodec.GetFrameCountAsync(bytes, shutdown.Token).ConfigureAwait(false) > 1)
            {
                AppLog.Info("Skipped an animated GIF in the Screenshots folder (a recording, not a screenshot).");
                return false;
            }

            IReadOnlyList<ClipFormatData> formats;
            try
            {
                formats = await ImageCodec.ToClipboardFormatsAsync(bytes, shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Complete by its own markers yet not decodable (a codec Windows lacks, a damaged file): retrying gains nothing.
                AppLog.Warn($"A screenshot file could not be decoded ({ex.GetType().Name}).");
                return false;
            }

            var capture = new ClipCapture
            {
                Formats = formats,
                CapturedAtUtc = loaded.WrittenUtc,
                Source = WindowsScreenshots.SourceFor(WindowsScreenshots.Classify(path), location.SnippingToolPath),
                Origin = ClipOrigin.WindowsScreenshot,
            };
            return await deliver(capture).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            handledAt = null; // shutting down: the next start's catch-up handles the file
            return false;
        }
        catch (Exception ex)
        {
            // Storing failed (disk full, history shutting down): not handled, so the next catch-up retries it. The log
            // names no file: a name of another tool can hold a window title.
            handledAt = null;
            AppLog.Warn($"A screenshot could not be imported ({ex.GetType().Name}).");
            return false;
        }
        finally
        {
            serial.Release();
            if (handledAt is { } when)
            {
                Handled?.Invoke(this, when);
            }
        }
    }

    /// <summary>
    /// Waits until a file is complete without ever locking its writer out (see the class remarks), and reads it.
    /// </summary>
    /// <param name="path">File.</param>
    /// <returns>What happened: the bytes and write time, too large (not read), vanished, or never complete in time.</returns>
    /// <exception cref="OperationCanceledException">The watcher is shutting down.</exception>
    private async Task<Loaded> LoadWhenCompleteAsync(string path)
    {
        var clock = Stopwatch.StartNew();
        (long Length, DateTime WrittenUtc)? lastShape = null;
        var stableSince = TimeSpan.Zero;
        while (clock.Elapsed < ReadTimeout)
        {
            shutdown.Token.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new Loaded(LoadOutcome.Vanished, null, 0, default);
            }

            var written = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (info.Length > maxBytes())
            {
                return new Loaded(LoadOutcome.TooLarge, null, info.Length, written);
            }

            if (info.Length > 0)
            {
                byte[]? bytes = null;
                try
                {
                    bytes = await ReadSharedAsync(path).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A writer holding it exclusively (no sharing at all), or a scanner: try again shortly.
                }

                if (bytes is not null)
                {
                    info.Refresh();
                    written = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                    var complete = WindowsScreenshots.LooksComplete(bytes, path);
                    if (complete == true && info.Length == bytes.LongLength)
                    {
                        return new Loaded(LoadOutcome.Ready, bytes, bytes.LongLength, written);
                    }

                    if (complete is null)
                    {
                        // No end marker in this format: complete once size and write time stop changing.
                        var shape = (bytes.LongLength, info.LastWriteTimeUtc);
                        if (lastShape != shape)
                        {
                            lastShape = shape;
                            stableSince = clock.Elapsed;
                        }
                        else if (clock.Elapsed - stableSince >= StableDelay)
                        {
                            return new Loaded(LoadOutcome.Ready, bytes, bytes.LongLength, written);
                        }
                    }
                }
            }

            await Task.Delay(100, shutdown.Token).ConfigureAwait(false);
        }

        AppLog.Warn("Gave up on a screenshot file that stayed incomplete or locked for 10 s.");
        return new Loaded(LoadOutcome.TimedOut, null, 0, default);
    }

    /// <summary>
    /// Reads a whole file while granting every sharing mode, so a writer that has it open — or opens it now — is never
    /// refused because of this read.
    /// </summary>
    /// <param name="path">File.</param>
    /// <returns>Its bytes as of the read.</returns>
    /// <exception cref="IOException">The file is held without any sharing, or vanished during the read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The watcher is shutting down.</exception>
    private async Task<byte[]> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);

        // The length can still grow while reading (a writer appending): copy what is there rather than a fixed count.
        using var copy = new MemoryStream(checked((int)Math.Min(stream.Length, int.MaxValue)));
        await stream.CopyToAsync(copy, shutdown.Token).ConfigureAwait(false);
        return copy.ToArray();
    }

    /// <summary>Identity of one saved version of a file: size and write time (a rename keeps both; a new save changes them).</summary>
    /// <param name="length">Size in bytes.</param>
    /// <param name="writtenUtc">Last write time (UTC).</param>
    /// <returns>The key.</returns>
    private static string Key(long length, DateTime writtenUtc) => $"{length}|{writtenUtc.Ticks}";

    /// <summary>Disposes the folder watcher.</summary>
    private void StopWatching()
    {
        lock (watcherGate)
        {
            if (watcher is null)
            {
                return;
            }

            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            watcher = null;
        }
    }

    /// <summary>How reading a file ended.</summary>
    private enum LoadOutcome
    {
        /// <summary>Complete and read.</summary>
        Ready,

        /// <summary>Over the size limit: not read.</summary>
        TooLarge,

        /// <summary>Deleted or renamed away before it was complete.</summary>
        Vanished,

        /// <summary>Still incomplete or locked after <see cref="ReadTimeout"/>.</summary>
        TimedOut,
    }

    /// <summary>The result of <see cref="LoadWhenCompleteAsync"/>.</summary>
    /// <param name="Outcome">How it ended.</param>
    /// <param name="Bytes">The file (only for <see cref="LoadOutcome.Ready"/>).</param>
    /// <param name="Length">Its size in bytes (0 when vanished or timed out).</param>
    /// <param name="WrittenUtc">Its write time when read or sized.</param>
    private readonly record struct Loaded(LoadOutcome Outcome, byte[]? Bytes, long Length, DateTimeOffset WrittenUtc);
}
