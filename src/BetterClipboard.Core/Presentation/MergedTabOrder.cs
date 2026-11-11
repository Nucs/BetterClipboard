using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.Presentation;

/// <summary>
/// Orders a panel tab that lists two sources together — the history entries that belong to the tab (kept from it, or
/// copied in its app) and the app's own items, read live — newest first by each row's newest activity, one row per
/// content. The Claude and Codex tabs (archived prompts, paged), Pwsh and Cmd (the commands a shell remembers) and
/// Everything (the files opened in it) are ordered by it.
/// </summary>
/// <typeparam name="TItem">The app's item: an archived prompt, a shell command, an Everything pick.</typeparam>
/// <remarks>
/// <para>
/// <b>Why.</b> The tabs listed every stored entry above the app's items, whatever their age. Pasting a prompt from the
/// Claude tab keeps it as a history entry, so a prompt pasted from there once stayed the tab's first row for good, above
/// every prompt sent since (reported 2026-11-11; the Codex, Pwsh and Cmd tabs did the same). The Everything tab sorted by
/// time, but a file kept from it sorted by when it was kept, also after it was opened again in Everything.
/// </para>
/// <para>
/// <b>The rules.</b>
/// <list type="bullet">
/// <item>A stored entry's time is its last use (<see cref="ClipEntry.LastUsedUtc"/>); an item's is its own (sent, typed,
/// opened). Items are handed in newest first. An item may have no time (PowerShell's history file keeps none): it keeps its
/// place among the items, and no entry is placed before it by time; a newest-item bound (<see cref="Next"/>) still puts the
/// entries used after the newest item first.</item>
/// <item>One row per content: an item whose content hash is a stored entry's (its "twin": the same prompt, command or file)
/// shows as the entry, which carries pins and groups, at the newer of the two times. An entry used at or after its twin's
/// time is placed by its own time, and the twin is dropped; one used before it is placed where the twin is, and the row's
/// time is the twin's (<see cref="MergedTabRow{TItem}.Time"/>; the card shows it).</item>
/// <item>With "Show pinned items first" on, pinned entries come first, most recently used first, as everywhere in the panel;
/// their twins are dropped.</item>
/// <item>Equal times: the stored entry first.</item>
/// <item>An entry that is older than every item and has no twin among them goes after them, once the last page is in.</item>
/// </list>
/// </para>
/// <para>
/// <b>Paging.</b> A tab that pages its items (the prompt archive, 60 texts a page) hands them in page by page: an entry
/// older than every item of a page waits for a later page, and the last page places what is left. One instance per load
/// of the list. Not thread-safe: the panel uses it on the UI thread only.
/// </para>
/// </remarks>
public sealed class MergedTabOrder<TItem>
    where TItem : class
{
    /// <summary>An item's content hash (a stored copy of the same content has it as <see cref="ClipEntry.ContentHash"/>).</summary>
    private readonly Func<TItem, string> hashOf;

    /// <summary>An item's own time, or <see langword="null"/> when its app keeps none.</summary>
    private readonly Func<TItem, DateTimeOffset?> timeOf;

    /// <summary>Pinned entries still to place on top (only while pinned ones go first), most recently used first.</summary>
    private readonly List<ClipEntry> pinned;

    /// <summary>Entries not placed yet, most recently used first.</summary>
    private readonly List<ClipEntry> waiting;

    /// <summary>
    /// How many entries of <see cref="waiting"/> have each content hash: tells in O(1) whether an item has a waiting twin,
    /// before the list is searched for it.
    /// </summary>
    private readonly Dictionary<string, int> waitingHashes = new(StringComparer.Ordinal);

    /// <summary>The content hashes placed so far (entries and items): a later item with one of them is not shown again.</summary>
    private readonly HashSet<string> placed = new(StringComparer.Ordinal);

    /// <summary>
    /// Starts the order of one load of a tab.
    /// </summary>
    /// <param name="stored">The tab's stored entries, any order (each one is placed exactly once).</param>
    /// <param name="pinnedFirst">Put pinned entries on top (the "Show pinned items first" setting).</param>
    /// <param name="hashOf">An item's content hash, as a stored copy of the same content would have it.</param>
    /// <param name="timeOf">An item's own time (sent, typed, opened), or <see langword="null"/> when its app keeps none.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>, or <paramref name="stored"/> holds a <see langword="null"/>.</exception>
    public MergedTabOrder(IEnumerable<ClipEntry> stored, bool pinnedFirst, Func<TItem, string> hashOf, Func<TItem, DateTimeOffset?> timeOf)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(hashOf);
        ArgumentNullException.ThrowIfNull(timeOf);
        this.hashOf = hashOf;
        this.timeOf = timeOf;

        // OrderBy is stable: entries used at the same time keep the store's own order.
        var entries = stored.Select(e => e ?? throw new ArgumentNullException(nameof(stored), "The stored entries hold a null."))
            .OrderByDescending(e => e.LastUsedUtc)
            .ToList();
        pinned = pinnedFirst ? entries.Where(e => e.IsPinned).ToList() : [];
        waiting = pinnedFirst ? entries.Where(e => !e.IsPinned).ToList() : entries;
        foreach (var entry in waiting)
        {
            waitingHashes[entry.ContentHash] = waitingHashes.GetValueOrDefault(entry.ContentHash) + 1;
        }
    }

    /// <summary>
    /// How many stored entries are not placed yet: their place lies among later pages (or after the last item). A footer
    /// that counts the tab's kept entries adds them to the ones shown.
    /// </summary>
    public int WaitingCount => pinned.Count + waiting.Count;

    /// <summary>
    /// Places the next page of the app's items, and the stored entries whose place lies among them.
    /// </summary>
    /// <param name="items">The page's items, newest first (an item without a time keeps its place among them).</param>
    /// <param name="last">
    /// Whether no page follows: the entries still waiting (older than every item, without a twin) are placed after the
    /// items. A tab without paging passes <see langword="true"/> with its one list.
    /// </param>
    /// <param name="newestItemUtc">
    /// When the newest item of the app came to be, for an app whose items carry no times (PowerShell: its history file's
    /// last write, when the newest command was typed): entries used at or after it are newer than every item and go
    /// first. <see langword="null"/> when the items' own times decide.
    /// </param>
    /// <returns>The rows in display order: the pinned entries on the first call, then entries and items newest first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="items"/> holds a <see langword="null"/>.</exception>
    public IReadOnlyList<MergedTabRow<TItem>> Next(IReadOnlyList<TItem> items, bool last, DateTimeOffset? newestItemUtc = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        var rows = new List<MergedTabRow<TItem>>(items.Count + pinned.Count);

        // The pins lead the list whatever the items' times (the panel's "Show pinned items first"), in the first page only.
        foreach (var entry in pinned)
        {
            Place(rows, entry, entry.LastUsedUtc);
        }

        pinned.Clear();
        if (newestItemUtc is { } newest)
        {
            PlaceUsedSince(rows, newest);
        }

        foreach (var item in items)
        {
            if (item is null)
            {
                throw new ArgumentException("The items hold a null.", nameof(items));
            }

            var time = timeOf(item);
            if (time is { } itemTime)
            {
                // Every entry used at or after this item is newer than it (and than every item after it): it goes first.
                PlaceUsedSince(rows, itemTime);
            }

            var hash = hashOf(item);
            if (placed.Contains(hash))
            {
                // Its entry, used later than this item, is shown already (or an earlier item had the same content).
                continue;
            }

            if (TakeTwin(hash) is { } twin)
            {
                // The entry was used before this item happened (or the app keeps no time, and its place in the app's list is
                // all there is to go by): the content shows here, as the entry, at the item's newer time.
                Place(rows, twin, time is { } newer && newer > twin.LastUsedUtc ? newer : twin.LastUsedUtc);
                continue;
            }

            placed.Add(hash);
            rows.Add(new MergedTabRow<TItem>(null, item, time));
        }

        if (last)
        {
            // Older than every item, and no twin came: after them, most recently used first.
            foreach (var entry in waiting)
            {
                Place(rows, entry, entry.LastUsedUtc);
            }

            waiting.Clear();
            waitingHashes.Clear();
        }

        return rows;
    }

    /// <summary>
    /// Leaves a stored entry out that was deleted after this order started (a history event while its page is not loaded
    /// yet): it must not come back as a card when its page is.
    /// </summary>
    /// <param name="entryId">The entry's id.</param>
    /// <returns>Whether it was still waiting (an entry placed already is the list's to remove).</returns>
    public bool Forget(long entryId)
    {
        if (pinned.RemoveAll(e => e.Id == entryId) > 0)
        {
            return true;
        }

        int index = waiting.FindIndex(e => e.Id == entryId);
        if (index < 0)
        {
            return false;
        }

        Unlist(index);
        return true;
    }

    /// <summary>Places, newest first, the waiting entries used at or after <paramref name="time"/>.</summary>
    /// <param name="rows">The rows being built.</param>
    /// <param name="time">The time of the item about to be placed (or the newest item's).</param>
    private void PlaceUsedSince(List<MergedTabRow<TItem>> rows, DateTimeOffset time)
    {
        while (waiting.Count > 0 && waiting[0].LastUsedUtc >= time)
        {
            var entry = waiting[0];
            Unlist(0);
            Place(rows, entry, entry.LastUsedUtc);
        }
    }

    /// <summary>Takes the waiting entry with a content hash out of the waiting list, when there is one.</summary>
    /// <param name="hash">The content hash.</param>
    /// <returns>The entry (the most recently used one with that hash), or <see langword="null"/>.</returns>
    private ClipEntry? TakeTwin(string hash)
    {
        if (!waitingHashes.ContainsKey(hash))
        {
            return null;
        }

        int index = waiting.FindIndex(e => string.Equals(e.ContentHash, hash, StringComparison.Ordinal));
        var entry = waiting[index];
        Unlist(index);
        return entry;
    }

    /// <summary>Removes a waiting entry from the list and from the hash counts.</summary>
    /// <param name="index">Its index in <see cref="waiting"/>.</param>
    private void Unlist(int index)
    {
        var hash = waiting[index].ContentHash;
        waiting.RemoveAt(index);
        if (waitingHashes.TryGetValue(hash, out int count) && count > 1)
        {
            waitingHashes[hash] = count - 1;
        }
        else
        {
            waitingHashes.Remove(hash);
        }
    }

    /// <summary>Adds an entry's row and marks its content as shown, so its twin (wherever it comes) is not shown again.</summary>
    /// <param name="rows">The rows being built.</param>
    /// <param name="entry">The entry.</param>
    /// <param name="time">The time the row sorts by.</param>
    private void Place(List<MergedTabRow<TItem>> rows, ClipEntry entry, DateTimeOffset time)
    {
        placed.Add(entry.ContentHash);
        rows.Add(new MergedTabRow<TItem>(entry, null, time));
    }
}

/// <summary>One row of a merged tab (<see cref="MergedTabOrder{TItem}"/>): a stored entry or an app's item — exactly one of them.</summary>
/// <typeparam name="TItem">The app's item type.</typeparam>
/// <param name="Entry">A stored entry, or <see langword="null"/> for an item.</param>
/// <param name="Item">An item of the app, or <see langword="null"/> for a stored entry.</param>
/// <param name="Time">
/// The time the row sorts by: an entry's last use, or its twin's time when that is newer (a kept prompt sent again, a kept
/// command typed again, a kept file opened again); an item's own time, or <see langword="null"/> when its app keeps none.
/// </param>
public sealed record MergedTabRow<TItem>(ClipEntry? Entry, TItem? Item, DateTimeOffset? Time)
    where TItem : class;
