using System.Threading.Channels;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Services;

/// <summary>
/// The capture pipeline and the single writer of the history: captures and user mutations are queued
/// and executed one at a time on a background worker; reads go straight to the store on the thread pool.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a single worker.</b> Serializing writes keeps SQLite free of lock contention, gives captures and
/// user actions a total order (a "delete" queued after a capture always wins), and keeps slow work
/// (image decoding, pruning) off both the clipboard listener thread — which must return to its message
/// loop quickly or other clipboard viewers stall — and the UI thread.
/// </para>
/// <para>
/// <b>Failure policy.</b> A failing work item is logged and reported to its awaiter; it never kills the
/// worker. Live captures are fire-and-forget (<see cref="TryEnqueueCapture"/>) so a broken clipboard
/// producer cannot back-pressure the listener.
/// </para>
/// </remarks>
public sealed class ClipHistoryService : IAsyncDisposable
{
    private readonly ClipStore store;
    private readonly IImageAnalyzer? imageAnalyzer;
    private readonly Func<CaptureRules> rulesProvider;
    private readonly TimeProvider time;
    private readonly Channel<Func<Task>> queue = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly CancellationTokenSource shutdown = new();
    private Task? worker;

    /// <summary>
    /// Creates the service. Call <see cref="Start"/> before enqueueing work.
    /// </summary>
    /// <param name="store">An initialized store.</param>
    /// <param name="imageAnalyzer">Decoder for image thumbnails; <see langword="null"/> stores images without thumbnails.</param>
    /// <param name="rulesProvider">Returns the current rules; invoked per capture on the worker thread, so it must be thread-safe and cheap.</param>
    /// <param name="time">Clock (inject a fake in tests); defaults to <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="rulesProvider"/> is <see langword="null"/>.</exception>
    public ClipHistoryService(ClipStore store, IImageAnalyzer? imageAnalyzer, Func<CaptureRules> rulesProvider, TimeProvider? time = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.imageAnalyzer = imageAnalyzer;
        this.rulesProvider = rulesProvider ?? throw new ArgumentNullException(nameof(rulesProvider));
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Raised after every change, on the worker thread. Handlers must be quick and must not block on
    /// further service calls (that would deadlock the single worker); marshal to the UI and return.
    /// </summary>
    public event EventHandler<ClipChangedEventArgs>? Changed;

    /// <summary>
    /// Raised on the worker thread after the set of groups or a group's name, icon or item count changed.
    /// Membership changes also raise <see cref="Changed"/> (<see cref="ClipChangeKind.Updated"/>) for each
    /// affected entry, so a card can update its group badges in place.
    /// </summary>
    public event EventHandler? GroupsChanged;

    /// <summary>The underlying store (for read-only diagnostics such as the database path).</summary>
    public ClipStore Store => store;

    /// <summary>
    /// Starts the background worker. Idempotent.
    /// </summary>
    public void Start() => worker ??= Task.Run(RunAsync);

    /// <summary>
    /// Queues a live capture without waiting (used by the clipboard listener).
    /// </summary>
    /// <param name="capture">The capture.</param>
    /// <returns><see langword="false"/> only after shutdown began.</returns>
    public bool TryEnqueueCapture(ClipCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return queue.Writer.TryWrite(async () =>
        {
            try
            {
                await ProcessCaptureAsync(capture).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error("Failed to store a clipboard capture.", ex);
            }
        });
    }

    /// <summary>
    /// Queues a capture (live or imported) and waits until it has been stored or rejected.
    /// </summary>
    /// <param name="capture">The capture.</param>
    /// <returns>The stored entry, or <see langword="null"/> when the rules or classification rejected it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="capture"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">Persisting failed.</exception>
    public Task<ClipEntry?> AddAsync(ClipCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return EnqueueAsync(() => ProcessCaptureAsync(capture));
    }

    /// <summary>
    /// Imports a batch (e.g. Windows' current history) in one queue slot and raises a single
    /// <see cref="ClipChangeKind.Reset"/> when anything changed, instead of one event per item.
    /// </summary>
    /// <param name="captures">Captures with an import <see cref="ClipCapture.Origin"/>.</param>
    /// <returns>How many new entries were created.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="captures"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    public Task<int> ImportAsync(IReadOnlyList<ClipCapture> captures)
    {
        ArgumentNullException.ThrowIfNull(captures);
        return EnqueueAsync(async () =>
        {
            int created = 0;
            bool anyChange = false;
            foreach (var capture in captures)
            {
                try
                {
                    var result = await StoreAsync(capture, raiseEvents: false).ConfigureAwait(false);
                    if (result is not null)
                    {
                        created += result.IsNew ? 1 : 0;
                        anyChange |= result.Changed;
                    }
                }
                catch (Exception ex)
                {
                    // One unreadable Windows item must not abort the rest of the import.
                    AppLog.Error("Failed to import one clipboard item.", ex);
                }
            }

            if (anyChange)
            {
                store.Prune(rulesProvider().Retention, time.GetUtcNow());
                Raise(new ClipChangedEventArgs(ClipChangeKind.Reset));
            }

            return created;
        });
    }

    /// <summary>
    /// Pins or unpins an entry.
    /// </summary>
    /// <param name="id">Entry id.</param>
    /// <param name="pinned">New pin state.</param>
    /// <returns>A task completing after the change is persisted.</returns>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    public Task SetPinnedAsync(long id, bool pinned) => EnqueueAsync(() =>
    {
        if (store.SetPinned(id, pinned, time.GetUtcNow()) && store.GetEntry(id) is { } entry)
        {
            Raise(new ClipChangedEventArgs(ClipChangeKind.Updated, entry));
        }

        return Task.FromResult(true);
    });

    /// <summary>
    /// Marks an entry as just pasted from history (moves it to the top).
    /// </summary>
    /// <param name="id">Entry id.</param>
    /// <returns>A task completing after the change is persisted.</returns>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    public Task MarkUsedAsync(long id) => EnqueueAsync(() =>
    {
        if (store.Touch(id, time.GetUtcNow()) && store.GetEntry(id) is { } entry)
        {
            Raise(new ClipChangedEventArgs(ClipChangeKind.Updated, entry));
        }

        return Task.FromResult(true);
    });

    /// <summary>
    /// Deletes an entry (and tombstones it against re-import).
    /// </summary>
    /// <param name="id">Entry id.</param>
    /// <returns>A task completing after the deletion is persisted.</returns>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    public Task DeleteAsync(long id) => EnqueueAsync(() =>
    {
        if (store.Delete(id, time.GetUtcNow()))
        {
            Raise(new ClipChangedEventArgs(ClipChangeKind.Removed, entryId: id));
        }

        return Task.FromResult(true);
    });

    /// <summary>
    /// Clears history.
    /// </summary>
    /// <param name="includePinned"><see langword="false"/> keeps pinned entries (Win+V "Clear all" semantics).</param>
    /// <returns>The number of deleted entries.</returns>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    public Task<int> ClearAsync(bool includePinned) => EnqueueAsync(() =>
    {
        var now = time.GetUtcNow();
        int removed = includePinned ? store.ClearAll(now) : store.ClearUnpinned(now);
        Raise(new ClipChangedEventArgs(ClipChangeKind.Reset));
        return Task.FromResult(removed);
    });

    /// <summary>
    /// Applies the current retention rules immediately (e.g. after the user lowered a limit).
    /// </summary>
    /// <returns>The number of pruned entries.</returns>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    public Task<int> PruneAsync() => EnqueueAsync(() =>
    {
        int removed = store.Prune(rulesProvider().Retention, time.GetUtcNow());
        if (removed > 0)
        {
            Raise(new ClipChangedEventArgs(ClipChangeKind.Reset));
        }

        return Task.FromResult(removed);
    });

    /// <summary>
    /// Reads a page of history on the thread pool (does not wait behind queued writes).
    /// </summary>
    /// <param name="query">The page request.</param>
    /// <param name="cancellationToken">Cancels a superseded query (e.g. while the user types).</param>
    /// <returns>The entries.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled before the query ran.</exception>
    public Task<IReadOnlyList<ClipEntry>> QueryAsync(ClipQuery query, CancellationToken cancellationToken = default) =>
        Task.Run(() => store.Query(query), cancellationToken);

    /// <summary>
    /// Reads one entry on the thread pool (does not wait behind queued writes).
    /// </summary>
    /// <param name="id">Entry id.</param>
    /// <returns>The entry, or <see langword="null"/> when it does not exist.</returns>
    public Task<ClipEntry?> GetEntryAsync(long id) => Task.Run(() => store.GetEntry(id));

    /// <summary>
    /// Reads the search text of the most recent entries on the thread pool, for regular-expression
    /// scans (see <see cref="ClipStore.GetSearchTexts"/> for what the text contains and where it is cut).
    /// </summary>
    /// <param name="filter">The slice to scan.</param>
    /// <param name="limit">Most recent entries to scan.</param>
    /// <returns>(Id, Text) pairs, most recently used first.</returns>
    public Task<IReadOnlyList<(long Id, string Text)>> GetSearchTextsAsync(ClipFilter filter, int limit) =>
        Task.Run(() => store.GetSearchTexts(filter, limit));

    /// <summary>
    /// Loads an entry's payloads on the thread pool.
    /// </summary>
    /// <param name="id">Entry id.</param>
    /// <returns>The formats in replay order (empty if the entry is gone).</returns>
    public Task<IReadOnlyList<ClipFormatData>> GetFormatsAsync(long id) => Task.Run(() => store.GetFormats(id));

    /// <summary>
    /// Loads an entry's thumbnail on the thread pool.
    /// </summary>
    /// <param name="id">Entry id.</param>
    /// <returns>PNG bytes or <see langword="null"/>.</returns>
    public Task<byte[]?> GetThumbnailAsync(long id) => Task.Run(() => store.GetThumbnail(id));

    /// <summary>
    /// Reads aggregate statistics on the thread pool.
    /// </summary>
    /// <returns>The statistics.</returns>
    public Task<StoreStats> GetStatsAsync() => Task.Run(store.GetStats);

    /// <summary>
    /// Reads integration state kept with this history (see <see cref="ClipStore.GetStateValue"/>) on the
    /// thread pool.
    /// </summary>
    /// <param name="name">State name.</param>
    /// <returns>The value, or <see langword="null"/> when never set.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid state name.</exception>
    public Task<string?> GetStateValueAsync(string name) => Task.Run(() => store.GetStateValue(name));

    /// <summary>
    /// Writes integration state through the worker, like every other write, so it is serialized with the
    /// captures it describes (a "last handled" marker never overtakes the capture it marks).
    /// </summary>
    /// <param name="name">State name.</param>
    /// <param name="value">The value.</param>
    /// <returns>A task completing once written.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid state name.</exception>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    public Task SetStateValueAsync(string name, string value) => EnqueueAsync(() =>
    {
        store.SetStateValue(name, value);
        return Task.FromResult(true);
    });

    /// <summary>
    /// Reads all groups (column order, with item counts) on the thread pool.
    /// </summary>
    /// <returns>The groups.</returns>
    public Task<IReadOnlyList<ClipGroup>> GetGroupsAsync() => Task.Run(store.GetGroups);

    /// <summary>
    /// Creates a group through the worker.
    /// </summary>
    /// <param name="name">Display name (trimmed; 1–<see cref="ClipGroup.MaxNameLength"/> characters).</param>
    /// <param name="glyph">Icon character (see <see cref="ClipGroup.IsValidGlyph"/>).</param>
    /// <returns>The new group.</returns>
    /// <exception cref="ArgumentException">Invalid name or glyph.</exception>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    public Task<ClipGroup> CreateGroupAsync(string name, string glyph) => EnqueueAsync(() =>
    {
        var group = store.CreateGroup(name, glyph, time.GetUtcNow());
        RaiseGroupsChanged();
        return Task.FromResult(group);
    });

    /// <summary>
    /// Renames a group and/or changes its icon (<see langword="null"/> keeps that part).
    /// </summary>
    /// <param name="id">Group id.</param>
    /// <param name="name">New name, or <see langword="null"/>.</param>
    /// <param name="glyph">New icon, or <see langword="null"/>.</param>
    /// <returns>Whether the group exists.</returns>
    /// <exception cref="ArgumentException">Invalid name or glyph.</exception>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    public Task<bool> UpdateGroupAsync(long id, string? name, string? glyph) => EnqueueAsync(() =>
    {
        bool exists = store.UpdateGroup(id, name, glyph);
        if (exists)
        {
            RaiseGroupsChanged();
        }

        return Task.FromResult(exists);
    });

    /// <summary>
    /// Deletes a group; its items stay (those in no other group re-enter retention from now).
    /// </summary>
    /// <param name="id">Group id.</param>
    /// <returns>How many items were in it.</returns>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    public Task<int> DeleteGroupAsync(long id) => EnqueueAsync(() =>
    {
        var members = store.DeleteGroup(id, time.GetUtcNow());

        // Every former member's badges changed: one reset instead of an event per card.
        Raise(new ClipChangedEventArgs(ClipChangeKind.Reset));
        RaiseGroupsChanged();
        return Task.FromResult(members.Count);
    });

    /// <summary>
    /// Adds entries to a group (e.g. cards dropped on its icon). Entries already in it are skipped.
    /// </summary>
    /// <param name="clipIds">Entry ids.</param>
    /// <param name="groupId">Group id.</param>
    /// <returns>How many memberships were added.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clipIds"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    public Task<int> AddToGroupAsync(IReadOnlyList<long> clipIds, long groupId)
    {
        ArgumentNullException.ThrowIfNull(clipIds);
        return EnqueueAsync(() =>
        {
            var now = time.GetUtcNow();
            int added = 0;
            foreach (var clipId in clipIds.Distinct())
            {
                if (store.AddToGroup(clipId, groupId, now) && store.GetEntry(clipId) is { } entry)
                {
                    added++;
                    Raise(new ClipChangedEventArgs(ClipChangeKind.Updated, entry));
                }
            }

            if (added > 0)
            {
                RaiseGroupsChanged();
            }

            return Task.FromResult(added);
        });
    }

    /// <summary>
    /// Takes an entry out of a group; when it was its last group, it re-enters retention with a fresh clock.
    /// </summary>
    /// <param name="clipId">Entry id.</param>
    /// <param name="groupId">Group id.</param>
    /// <returns>What happened.</returns>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    public Task<GroupRemoval> RemoveFromGroupAsync(long clipId, long groupId) => EnqueueAsync(() =>
    {
        var removal = store.RemoveFromGroup(clipId, groupId, time.GetUtcNow());
        if (removal.Removed)
        {
            if (store.GetEntry(clipId) is { } entry)
            {
                Raise(new ClipChangedEventArgs(ClipChangeKind.Updated, entry));
            }

            RaiseGroupsChanged();
        }

        return Task.FromResult(removal);
    });

    /// <summary>
    /// Stops accepting work, drains what is already queued (so no capture is lost on exit) and stops the worker.
    /// </summary>
    /// <returns>A task completing when the worker has finished.</returns>
    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        if (worker is not null)
        {
            // Give queued captures a bounded chance to persist; then cancel whatever is still running.
            var finished = await Task.WhenAny(worker, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            if (finished != worker)
            {
                await shutdown.CancelAsync().ConfigureAwait(false);
            }

            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the drain timed out.
            }
        }

        shutdown.Dispose();
    }

    /// <summary>Worker loop: executes queued work items strictly in order.</summary>
    /// <returns>A task completing when the queue is completed and drained, or cancelled.</returns>
    private async Task RunAsync()
    {
        await foreach (var work in queue.Reader.ReadAllAsync(shutdown.Token).ConfigureAwait(false))
        {
            // Work items capture their own exceptions (see EnqueueAsync/TryEnqueueCapture); this catch is
            // the last line of defense that keeps the worker alive.
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error("History worker item failed.", ex);
            }
        }
    }

    /// <summary>Queues <paramref name="work"/> and returns a task for its result.</summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="work">The work to run on the worker.</param>
    /// <returns>The result task; faults with the work's exception.</returns>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    private Task<T> EnqueueAsync<T>(Func<Task<T>> work)
    {
        // RunContinuationsAsynchronously: never resume awaiters inline on the worker thread, or a caller
        // awaiting inside a UI continuation could end up executing on — and blocking — the worker.
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = queue.Writer.TryWrite(async () =>
        {
            try
            {
                completion.TrySetResult(await work().ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                AppLog.Error("History operation failed.", ex);
                completion.TrySetException(ex);
            }
        });

        return queued ? completion.Task : Task.FromException<T>(new InvalidOperationException("The history service is shutting down."));
    }

    /// <summary>Runs the full capture path for one capture and raises its event.</summary>
    /// <param name="capture">The capture.</param>
    /// <returns>The stored entry, or <see langword="null"/> when rejected.</returns>
    private async Task<ClipEntry?> ProcessCaptureAsync(ClipCapture capture)
    {
        var result = await StoreAsync(capture, raiseEvents: true).ConfigureAwait(false);
        return result?.Entry;
    }

    /// <summary>Applies rules, classifies, analyzes, persists and (optionally) prunes and raises events.</summary>
    /// <param name="capture">The capture.</param>
    /// <param name="raiseEvents">Whether to raise per-item events (false inside batch imports).</param>
    /// <returns>The upsert result, or <see langword="null"/> when rejected.</returns>
    private async Task<UpsertResult?> StoreAsync(ClipCapture capture, bool raiseEvents)
    {
        var rules = rulesProvider();

        // A ShareX screenshot is a new event like a live copy, so "pause capturing" covers it too — the
        // user pausing for privacy expects nothing new to be recorded, whatever the channel.
        bool isNewEvent = capture.Origin is ClipOrigin.Captured or ClipOrigin.ShareX;
        if (isNewEvent && rules.IsPaused)
        {
            return null;
        }

        if (rules.IsIgnored(capture.Source))
        {
            AppLog.Info($"Skipped a copy from ignored app '{capture.Source!.ProcessName}'.");
            return null;
        }

        if (capture.TotalBytes > rules.MaxItemBytes)
        {
            AppLog.Info($"Skipped a {capture.TotalBytes:N0}-byte copy (limit {rules.MaxItemBytes:N0}).");
            return null;
        }

        var classified = ContentClassifier.Classify(capture);
        if (classified is null ||
            (classified.Kind == ClipKind.Image && !rules.CaptureImages) ||
            (classified.Kind == ClipKind.Files && !rules.CaptureFiles))
        {
            return null;
        }

        ImageAnalysis? image = null;
        if (classified.Kind == ClipKind.Image && imageAnalyzer is not null)
        {
            try
            {
                image = await imageAnalyzer.AnalyzeAsync(capture, shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A corrupt bitmap only costs us the thumbnail; the clip itself is still worth keeping.
                AppLog.Warn($"Image analysis failed: {ex.Message}");
            }

            // Identify images by pixels, not encoding: the same picture as DIB (live copy) and as PNG
            // (Windows-history import, browsers) must collapse into one entry.
            if (image?.PixelHash is { } pixelHash)
            {
                classified = classified with { ContentHash = pixelHash };
            }
        }

        // New events (live copies, ShareX screenshots) move an existing duplicate to the top — ShareX often
        // copied the same screenshot to the clipboard a moment earlier, and the pixel hash merges the two.
        var result = store.Upsert(capture, classified, image, bumpIfExists: isNewEvent);
        if (result is null || !raiseEvents)
        {
            return result;
        }

        if (result.IsNew)
        {
            Raise(new ClipChangedEventArgs(ClipChangeKind.Added, result.Entry));
            if (store.Prune(rules.Retention, time.GetUtcNow()) > 0)
            {
                Raise(new ClipChangedEventArgs(ClipChangeKind.Reset));
            }
        }
        else if (result.Changed)
        {
            Raise(new ClipChangedEventArgs(ClipChangeKind.Updated, result.Entry));
        }

        return result;
    }

    /// <summary>Raises <see cref="Changed"/>, isolating the worker from subscriber exceptions.</summary>
    /// <param name="args">The event data.</param>
    private void Raise(ClipChangedEventArgs args)
    {
        try
        {
            Changed?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            AppLog.Error("A history change subscriber threw.", ex);
        }
    }

    /// <summary>Raises <see cref="GroupsChanged"/>, shielding the worker from subscriber exceptions.</summary>
    private void RaiseGroupsChanged()
    {
        try
        {
            GroupsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Error("A groups change subscriber threw.", ex);
        }
    }
}
