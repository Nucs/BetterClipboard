using System.Collections.ObjectModel;

namespace BetterClipboard.Core.Presentation;

/// <summary>
/// Turns one ordered list of item instances into another with the fewest practical changes — removes, moves and inserts —
/// instead of clearing it and adding everything again, so a list control keeps the containers (layout, drawing, decoded
/// images) of every item that stays.
/// </summary>
/// <remarks>
/// <para>
/// Why: the panel's list used to be cleared and refilled on every load. The list control then dropped every card, created,
/// laid out and drew them all again, decoded each image card's thumbnail again, and played its add animations. A refresh of
/// the hidden panel after a copy should cost one new card, not a page of them (<c>FlyoutViewModel.ApplyEntries</c>).
/// </para>
/// <para>
/// Items are matched by <b>reference</b>, never by <c>Equals</c>: the caller decides which instances to keep (the panel
/// reuses the card of every entry still listed) and hands them in the order they must end in. Each instance may appear
/// once in <c>wanted</c>; a repeated instance throws, since a collection that holds one card twice confuses every list
/// control.
/// </para>
/// <para>
/// Cost: one pass of removes, then for each position at most one move or insert, with a linear search for the instance
/// to move — quadratic in the worst case, which for a page of cards (60) is nothing.
/// </para>
/// </remarks>
public static class CollectionPatch
{
    /// <summary>
    /// Makes <paramref name="items"/> hold exactly the instances of <paramref name="wanted"/>, in that order: instances not
    /// wanted are removed, wanted ones already present are moved into place, new ones are inserted. A collection that
    /// already matches raises no change notifications at all.
    /// </summary>
    /// <typeparam name="T">Item type (a reference type: items are matched by reference).</typeparam>
    /// <param name="items">The collection to change (raises its usual Remove, Move and Add notifications).</param>
    /// <param name="wanted">The instances it must hold afterwards, in order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> or <paramref name="wanted"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="wanted"/> holds the same instance twice, or holds a <see langword="null"/>.</exception>
    public static void Apply<T>(ObservableCollection<T> items, IReadOnlyList<T> wanted)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(wanted);
        var keep = new HashSet<T>(ReferenceEqualityComparer.Instance);
        foreach (var item in wanted)
        {
            if (item is null)
            {
                throw new ArgumentException("The wanted items hold a null.", nameof(wanted));
            }

            if (!keep.Add(item))
            {
                throw new ArgumentException("The wanted items hold the same instance twice.", nameof(wanted));
            }
        }

        // Removed from the end down, so the indexes still to visit do not shift.
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(items[i]))
            {
                items.RemoveAt(i);
            }
        }

        // Every item left is wanted, so each position is fixed by moving its instance up from further down (it cannot be
        // above: those positions are already final) or inserting a new one. Duplicates already in the collection are
        // handled too: the second copy of an instance ends up past the wanted ones and is removed below.
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < items.Count && ReferenceEquals(items[i], wanted[i]))
            {
                continue;
            }

            int from = -1;
            for (int j = i + 1; j < items.Count; j++)
            {
                if (ReferenceEquals(items[j], wanted[i]))
                {
                    from = j;
                    break;
                }
            }

            if (from >= 0)
            {
                items.Move(from, i);
            }
            else
            {
                items.Insert(i, wanted[i]);
            }
        }

        while (items.Count > wanted.Count)
        {
            items.RemoveAt(items.Count - 1);
        }
    }
}
