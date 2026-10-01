namespace BetterClipboard.Core.Presentation;

/// <summary>
/// Which groups the panel's list shows: none (the regular view over the whole history), one, or several merged into
/// one list. Clicks on the group icons change it with Explorer's selection keys: a plain click shows one group (on the
/// only group shown it goes back to the regular view), Ctrl+click adds a group to the view or takes it out, Shift+click
/// shows the run of groups from the last clicked one, and Ctrl+Shift+click adds that run.
/// </summary>
/// <remarks>
/// <para>
/// Immutable and free of UI types, so the rules are unit-tested; the panel keeps one instance and replaces it on every
/// change. <see cref="Ids"/> always follows the column's order (the header names the groups in that order), whatever
/// order they were clicked in, and never holds an id twice. Every operation that changes nothing returns the same
/// instance, so a caller can tell "nothing happened" by reference.
/// </para>
/// <para>
/// <b>Merged, not concatenated.</b> The list of a selection of several groups is their union in the list's usual
/// order (see <see cref="Storage.ClipQuery.GroupIds"/>), so the order of <see cref="Ids"/> never orders cards.
/// </para>
/// <para>
/// <b>The anchor</b> (<see cref="Anchor"/>) is where a Shift+click run starts: the last group clicked without Shift,
/// like Explorer's. A Ctrl+click that takes a group out still moves the anchor there (Explorer does the same). The
/// anchor is dropped with the last group shown, so Shift+click in the regular view starts afresh from the clicked
/// group instead of from a group that is no longer highlighted, and when its group is deleted.
/// </para>
/// </remarks>
public sealed class GroupSelection
{
    /// <summary>The selected ids in column order (never shared with callers: <see cref="Ids"/> hands out a read-only view).</summary>
    private readonly long[] ids;

    /// <summary>Creates a selection; callers have already put <paramref name="ids"/> in column order without duplicates.</summary>
    /// <param name="ids">The selected ids, owned by the new instance from now on.</param>
    /// <param name="anchor">Where the next Shift+click run starts; ignored (stored as <see langword="null"/>) for an empty selection.</param>
    private GroupSelection(long[] ids, long? anchor)
    {
        this.ids = ids;
        Anchor = ids.Length == 0 ? null : anchor;
    }

    /// <summary>The regular view: no group selected, the list shows the whole history.</summary>
    public static GroupSelection None { get; } = new([], null);

    /// <summary>The selected groups' ids in column order; empty in the regular view.</summary>
    public IReadOnlyList<long> Ids => Array.AsReadOnly(ids);

    /// <summary>
    /// The group a Shift+click run starts from (the last group clicked without Shift); <see langword="null"/> in the
    /// regular view, or after its group was deleted (the next Shift+click then starts at the clicked group).
    /// </summary>
    public long? Anchor { get; }

    /// <summary>Whether this is the regular view (no group selected).</summary>
    public bool IsEmpty => ids.Length == 0;

    /// <summary>Whether several groups are merged into the list (texts then name more than one group).</summary>
    public bool IsMerged => ids.Length > 1;

    /// <summary>Whether a group is among those shown.</summary>
    /// <param name="id">Group id.</param>
    /// <returns><see langword="true"/> when the list shows that group's items.</returns>
    public bool Contains(long id) => Array.IndexOf(ids, id) >= 0;

    /// <summary>
    /// Whether <paramref name="other"/> shows the same groups — what decides if the list must reload. Two selections
    /// that differ only in their <see cref="Anchor"/> show the same list.
    /// </summary>
    /// <param name="other">The selection to compare with.</param>
    /// <returns><see langword="true"/> for the same ids in the same order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is <see langword="null"/>.</exception>
    public bool HasSameIds(GroupSelection other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ids.AsSpan().SequenceEqual(other.ids);
    }

    /// <summary>
    /// Applies a click on a group icon with the modifier keys held at that moment (see the class summary for the rules).
    /// </summary>
    /// <param name="column">The ids of the groups column, top to bottom (Shift+click runs and the order of <see cref="Ids"/> come from it).</param>
    /// <param name="clicked">The clicked group's id.</param>
    /// <param name="ctrl">Ctrl was down: add the group (or the run, with <paramref name="shift"/>) to the view, or take the group out.</param>
    /// <param name="shift">Shift was down: select the run of groups from <see cref="Anchor"/> to <paramref name="clicked"/>.</param>
    /// <returns>
    /// The new selection; <see cref="None"/> when the click left no group selected; this instance when
    /// <paramref name="clicked"/> is not in <paramref name="column"/> (an icon whose group was deleted while the click
    /// was on its way: there is nothing left to show for it).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is <see langword="null"/>.</exception>
    public GroupSelection Click(IReadOnlyList<long> column, long clicked, bool ctrl, bool shift)
    {
        ArgumentNullException.ThrowIfNull(column);
        int clickedAt = IndexOf(column, clicked);
        if (clickedAt < 0)
        {
            return this;
        }

        if (shift)
        {
            // The run starts at the anchor; without one (regular view, anchor's group deleted) at the clicked group.
            // The anchor stays where it was, so further Shift+clicks pivot around the same group, as in Explorer.
            int anchorAt = Anchor is { } anchor ? IndexOf(column, anchor) : -1;
            if (anchorAt < 0)
            {
                anchorAt = clickedAt;
            }

            int from = Math.Min(anchorAt, clickedAt);
            var run = column.Skip(from).Take(Math.Abs(anchorAt - clickedAt) + 1);
            return Create(column, ctrl ? ids.Concat(run) : run, column[anchorAt]);
        }

        if (ctrl)
        {
            return Contains(clicked)
                ? Create(column, ids.Where(id => id != clicked), clicked)
                : Create(column, ids.Append(clicked), clicked);
        }

        // A plain click on the only group shown goes back to everything (clicking a shown group again "closes" it, as it
        // did before groups could be merged). On any other group, or on one of several merged ones, it shows that group alone.
        return ids is [var only] && only == clicked ? None : new GroupSelection([clicked], clicked);
    }

    /// <summary>
    /// Adds a group to the view or takes it out: Ctrl+click without the mouse (the group icon's menu, for touch, pen and
    /// screen readers, whose "invoke" is a plain click).
    /// </summary>
    /// <param name="column">The ids of the groups column, top to bottom.</param>
    /// <param name="id">The group to add or take out.</param>
    /// <returns>The new selection (see <see cref="Click"/>).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is <see langword="null"/>.</exception>
    public GroupSelection Toggle(IReadOnlyList<long> column, long id) => Click(column, id, ctrl: true, shift: false);

    /// <summary>
    /// Takes one group out of the view without touching the others — the group is being deleted. Unlike a Ctrl+click,
    /// the anchor does not move there (it is dropped if it was there: the group is about to vanish).
    /// </summary>
    /// <param name="id">The group.</param>
    /// <returns>The selection without it; <see cref="None"/> when it was the only one; this instance when it was not shown.</returns>
    public GroupSelection Without(long id)
    {
        if (!Contains(id))
        {
            return this;
        }

        long[] rest = [.. ids.Where(other => other != id)];
        return rest.Length == 0 ? None : new GroupSelection(rest, Anchor == id ? null : Anchor);
    }

    /// <summary>
    /// Keeps only the groups that still exist after the column was reloaded (a group deleted elsewhere, e.g. from its
    /// menu), in the column's order; drops the anchor when its group is gone.
    /// </summary>
    /// <param name="column">The reloaded column's ids, top to bottom.</param>
    /// <returns>This instance when nothing vanished (and the order still holds); otherwise the reduced selection, <see cref="None"/> when no group is left.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is <see langword="null"/>.</exception>
    public GroupSelection Retain(IReadOnlyList<long> column)
    {
        ArgumentNullException.ThrowIfNull(column);
        long? anchor = Anchor is { } a && IndexOf(column, a) >= 0 ? a : null;
        var kept = Create(column, ids, anchor);
        return kept.HasSameIds(this) && kept.Anchor == Anchor ? this : kept;
    }

    /// <summary>Builds a selection from any ids: only those in the column, in its order, each once.</summary>
    /// <param name="column">The column's ids, top to bottom.</param>
    /// <param name="selected">The ids to select (any order, duplicates allowed, unknown ids dropped).</param>
    /// <param name="anchor">The anchor to keep (see <see cref="Anchor"/>).</param>
    /// <returns>The selection, or <see cref="None"/> when none of the ids is in the column.</returns>
    private static GroupSelection Create(IReadOnlyList<long> column, IEnumerable<long> selected, long? anchor)
    {
        var wanted = selected.ToHashSet();
        long[] ordered = [.. column.Where(wanted.Contains).Distinct()];
        return ordered.Length == 0 ? None : new GroupSelection(ordered, anchor);
    }

    /// <summary>Position of an id in the column.</summary>
    /// <param name="column">The column's ids.</param>
    /// <param name="id">The id to find.</param>
    /// <returns>Its index, or -1 when it is not in the column.</returns>
    private static int IndexOf(IReadOnlyList<long> column, long id)
    {
        for (int i = 0; i < column.Count; i++)
        {
            if (column[i] == id)
            {
                return i;
            }
        }

        return -1;
    }
}
