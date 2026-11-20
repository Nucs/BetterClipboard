namespace BetterClipboard.Core.Presentation;

/// <summary>
/// A merged panel tab's rows, handed out a page at a time: the Pwsh, Cmd and Everything tabs merge all their rows at once
/// (their sources are read whole anyway: a history file, a kept list, Everything's reply), and the panel turns them into
/// cards only as the list scrolls near its end — the way the clipboard history and the prompt tabs page.
/// </summary>
/// <remarks>
/// <para>
/// Until 2026-11-21 those tabs had no paging and capped their lists instead (Pwsh and Cmd at the newest 500 live commands
/// and 200 kept entries, Everything at 200 of each), so most of a long PowerShell history could not be reached. The owner
/// chose to show every row and load it as the list scrolls ("Yes, load as you scroll"): building a card is the expensive
/// part, so a tab now builds the first page of cards instead of up to 700 (CLAUDE.md §2.62).
/// </para>
/// <para>
/// Not thread-safe: the panel's view model uses it on the UI thread only.
/// </para>
/// </remarks>
/// <typeparam name="TRow">The tab's row type (a stored entry or a live item, e.g. <c>ShellTabRow</c>).</typeparam>
public sealed class RowPages<TRow>
{
    private readonly List<TRow> rows;

    /// <summary>Index of the first row not handed out yet.</summary>
    private int next;

    /// <summary>Takes the rows of one load, in display order.</summary>
    /// <param name="rows">The rows (copied: the caller may change its own list afterwards).</param>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> is <see langword="null"/>.</exception>
    public RowPages(IEnumerable<TRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        this.rows = [.. rows];
    }

    /// <summary>No rows: what a tab holds before its first load.</summary>
    public static RowPages<TRow> Empty => new([]);

    /// <summary>Whether rows are left that no page has handed out yet.</summary>
    public bool HasMore => next < rows.Count;

    /// <summary>How many rows no page has handed out yet.</summary>
    public int RemainingCount => rows.Count - next;

    /// <summary>
    /// The next page: up to <paramref name="pageSize"/> rows after the ones handed out before, in display order.
    /// </summary>
    /// <param name="pageSize">Most rows to hand out.</param>
    /// <returns>The page; empty once every row was handed out.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageSize"/> is less than 1.</exception>
    public IReadOnlyList<TRow> Next(int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        int count = Math.Min(pageSize, rows.Count - next);
        var page = rows.GetRange(next, count);
        next += count;
        return page;
    }

    /// <summary>
    /// How many of the rows not handed out yet match: with the cards already built, what a tab's footer counts, so its
    /// numbers do not depend on how far the list was scrolled.
    /// </summary>
    /// <param name="predicate">The rows to count.</param>
    /// <returns>The count.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    public int CountRemaining(Func<TRow, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        int count = 0;
        for (int i = next; i < rows.Count; i++)
        {
            if (predicate(rows[i]))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Drops the rows not handed out yet that match — an entry deleted while the tab shows must not come back with a later
    /// page. Rows already handed out are the caller's cards, and stay its to remove.
    /// </summary>
    /// <param name="predicate">The rows to drop.</param>
    /// <returns>How many were dropped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    public int Forget(Func<TRow, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        int dropped = 0;
        for (int i = rows.Count - 1; i >= next; i--)
        {
            if (predicate(rows[i]))
            {
                rows.RemoveAt(i);
                dropped++;
            }
        }

        return dropped;
    }
}
