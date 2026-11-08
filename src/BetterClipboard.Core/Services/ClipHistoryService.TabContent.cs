using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Services;

/// <summary>
/// Whether the panel's tabs that show only with something in them have it: the Run tab (an entry with a Win+R run time)
/// and the Snipping tab (a picture it lists). The worker keeps both answers current from every change it makes.
/// </summary>
/// <remarks>
/// <para>
/// <b>A witness, not a count.</b> For each tab the worker keeps the id of one entry the tab lists: the one used last when
/// the tab was looked up (<see cref="ClipStore.FindNewestInFilter"/>), the entry retention would prune last. A change then
/// costs at most one primary-key lookup (<see cref="ClipStore.IsInFilter"/>): an added or updated entry is looked at only
/// while its tab has no witness, a removed entry only when it is the witness, and a reset (clear, prune) only by checking
/// that the witness is still there and still listed. A tab is looked up again only when that answer may have changed: at
/// the worker's start, after an import while the tab has nothing (an import raises no event per entry), and when the
/// witness went — deleted, pruned, or no longer listed (a picture Snipping Tool copied, copied again from another app,
/// takes that app's name). A count would need a query per change: a full history prunes an entry with every copy, and the
/// Snipping lookup walks every picture when there is no snip (37 ms for 2,000 pictures, measured).
/// </para>
/// <para>
/// <b>Order.</b> Everything here runs on the worker, right after the change it follows and before the work item completes:
/// an awaited <see cref="AddAsync"/> or <see cref="DeleteAsync"/> returns with the answer already up to date, and no write
/// can slip between a change and its check. The answers themselves are read from any thread.
/// </para>
/// <para>
/// <b>Failures</b> are logged and leave the witness as it was: a tab that stays (or stays away) a while is better than a
/// work item that fails after its change was stored.
/// </para>
/// </remarks>
public sealed partial class ClipHistoryService
{
    /// <summary>The Run tab: entries that carry a Win+R run time, of any kind.</summary>
    private readonly TabWitness runTab = new(ClipFilter.Run, null);

    /// <summary>
    /// The Snipping tab, counting pictures only: text Snipping Tool copied (its text actions) is listed there too, but the
    /// tab is for screenshots, and only those make it show.
    /// </summary>
    private readonly TabWitness snippingTab = new(ClipFilter.Snipping, ClipKind.Image);

    /// <summary>
    /// Raised on the worker thread when <see cref="HasRunHistory"/> (with <see cref="ClipFilter.Run"/>) or
    /// <see cref="HasSnippingImages"/> (with <see cref="ClipFilter.Snipping"/>) changed — also right after <see cref="Start"/>
    /// for a store that already has such entries. Same handler rules as <see cref="Changed"/> (marshal to the UI and return).
    /// </summary>
    public event EventHandler<ClipFilter>? TabContentChanged;

    /// <summary>
    /// Whether the history holds at least one entry with a Win+R run time — the Run tab has something to list.
    /// <see langword="false"/> until the worker has looked (<see cref="Start"/>); readable from any thread.
    /// </summary>
    public bool HasRunHistory => runTab.Present;

    /// <summary>
    /// Whether the history holds at least one picture the Snipping tab lists — a screenshot file from the Screenshots folder,
    /// or a picture Snipping Tool copied. <see langword="false"/> until the worker has looked (<see cref="Start"/>);
    /// readable from any thread.
    /// </summary>
    public bool HasSnippingImages => snippingTab.Present;

    /// <summary>
    /// Looks up each tab that has no witness yet: at the worker's start (every tab), and after an import, which adds entries
    /// without an event of its own. Worker thread only.
    /// </summary>
    private void FindMissingTabContent()
    {
        foreach (var tab in (ReadOnlySpan<TabWitness>)[runTab, snippingTab])
        {
            if (tab.Id is null)
            {
                Refind(tab);
                Publish(tab);
            }
        }
    }

    /// <summary>
    /// Follows one change of the history in both tabs' witnesses (see the class remarks), and tells
    /// <see cref="TabContentChanged"/> when a tab gained its first entry or lost its last. Worker thread only, after the
    /// change was stored.
    /// </summary>
    /// <param name="change">The change, as raised to <see cref="Changed"/>.</param>
    private void TrackTabContent(ClipChangedEventArgs change)
    {
        foreach (var tab in (ReadOnlySpan<TabWitness>)[runTab, snippingTab])
        {
            try
            {
                switch (change.Kind)
                {
                    case ClipChangeKind.Added or ClipChangeKind.Updated when change.Entry is { } entry:
                        if (tab.Id is null)
                        {
                            // The first entry of an empty tab: it becomes the witness, no lookup needed.
                            if (Lists(tab, entry))
                            {
                                tab.Id = entry.Id;
                            }
                        }
                        else if (tab.Id == entry.Id && !Lists(tab, entry))
                        {
                            // The witness itself left the tab: another entry may still be in it.
                            Refind(tab);
                        }

                        break;
                    case ClipChangeKind.Removed when change.EntryId is { } removed && removed == tab.Id:
                        Refind(tab);
                        break;
                    case ClipChangeKind.Reset when tab.Id is { } witness && !store.IsInFilter(witness, tab.Filter, tab.Kind):
                        // A clear or prune took the witness. A reset never adds anything except an import's, which looks
                        // up the empty tabs itself (FindMissingTabContent): an empty tab is not looked up here, or a full
                        // history would walk every picture after every copy it prunes for.
                        Refind(tab);
                        break;
                }
            }
            catch (Exception ex)
            {
                // Content-free: the tab and the error only.
                AppLog.Warn($"Checking whether the {tab.Filter} tab has entries failed: {ex.Message}");
            }

            Publish(tab);
        }
    }

    /// <summary>Whether an entry is one a tab counts: of its kind (when it has one) and listed by its filter.</summary>
    /// <param name="tab">The tab.</param>
    /// <param name="entry">The entry as it is now (from a change event).</param>
    /// <returns><see langword="true"/> when the entry is in the tab.</returns>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The lookup failed.</exception>
    private bool Lists(TabWitness tab, ClipEntry entry) =>
        (tab.Kind is not { } kind || entry.Kind == kind) && store.IsInFilter(entry.Id, tab.Filter, tab.Kind);

    /// <summary>
    /// Looks a tab up again, newest first: its new witness, or none. A failed lookup is logged and keeps the old witness.
    /// </summary>
    /// <param name="tab">The tab.</param>
    private void Refind(TabWitness tab)
    {
        try
        {
            tab.Id = store.FindNewestInFilter(tab.Filter, tab.Kind);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Looking up the {tab.Filter} tab's entries failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Makes a tab's answer match its witness, raising <see cref="TabContentChanged"/> when it flipped. A subscriber that
    /// throws is logged and cannot stop the worker.
    /// </summary>
    /// <param name="tab">The tab.</param>
    private void Publish(TabWitness tab)
    {
        bool present = tab.Id is not null;
        if (present == tab.Present)
        {
            return;
        }

        tab.Present = present;
        try
        {
            TabContentChanged?.Invoke(this, tab.Filter);
        }
        catch (Exception ex)
        {
            AppLog.Error("A tab content subscriber threw.", ex);
        }
    }

    /// <summary>One tab that shows only with something in it, and the entry that proves it has something.</summary>
    /// <param name="filter">The tab's filter.</param>
    /// <param name="kind">Only entries of this kind count; <see langword="null"/> for every kind.</param>
    private sealed class TabWitness(ClipFilter filter, ClipKind? kind)
    {
        /// <summary>Written by <see cref="Publish"/> on the worker, read by <see cref="Present"/> from any thread.</summary>
        private volatile bool present;

        /// <summary>The tab's filter.</summary>
        public ClipFilter Filter { get; } = filter;

        /// <summary>Only entries of this kind count; <see langword="null"/> for every kind.</summary>
        public ClipKind? Kind { get; } = kind;

        /// <summary>
        /// An entry the tab lists, or <see langword="null"/> when it lists none (or before the first lookup). Worker thread
        /// only: a nullable long is not read atomically, which is why other threads read <see cref="Present"/> instead.
        /// </summary>
        public long? Id { get; set; }

        /// <summary>The published answer: whether the tab has anything (its witness exists).</summary>
        public bool Present
        {
            get => present;
            set => present = value;
        }
    }
}
