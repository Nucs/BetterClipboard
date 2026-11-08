namespace BetterClipboard.Core.Presentation;

/// <summary>
/// The rules for arranging the panel's filter tabs (All, Pinned, Text, …) from their right-click menu — <i>Move left</i>,
/// <i>Move right</i>, <i>Hide</i>, <i>Show all hidden</i>: the order to show from the stored one, which moves are
/// possible, where a move puts the tab, and what is stored.
/// </summary>
/// <remarks>
/// <para>
/// Pure and free of UI types, so the rules are unit-tested; the panel owns the tabs, asks here, and applies the answer.
/// A tab is named by its <c>ClipFilter</c> name, the tab's <c>Tag</c> in the XAML. The <b>default order</b> is the
/// XAML's, handed in by the panel, so the XAML stays the one place that order is written down.
/// </para>
/// <para>
/// <b>"All" is fixed first</b> (<see cref="FixedTab"/>; the owner's choice, 2026-11-08: "All always stays … and can't be
/// moved"): it is never moved, never hidden, and no tab moves before it. It stays the place the panel falls back to when
/// the tab it would open in is hidden or gone.
/// </para>
/// <para>
/// <b>Moves skip tabs that do not show</b> — hidden ones, and tabs of other apps that are not there right now (ShareX not
/// installed, no Win+R command yet…) — so every <i>Move left</i> or <i>Move right</i> visibly moves the tab one place.
/// The tabs it passes keep their places relative to the others: a tab not shown today comes back where it was.
/// </para>
/// </remarks>
public static class TabArrangement
{
    /// <summary>
    /// The tab that is always first, never moved and never hidden: "All", the whole history (the name of
    /// <c>ClipFilter.All</c>, also <c>AppSettings.DefaultTab</c>).
    /// </summary>
    public const string FixedTab = "All";

    /// <summary>
    /// The full order to show: the stored order, completed with the tabs it lacks, with <see cref="FixedTab"/> first.
    /// </summary>
    /// <param name="defaults">Every tab, in the panel's default order (the XAML's), each name once.</param>
    /// <param name="stored">
    /// The order the user arranged (<c>AppSettings.TabOrder</c>), or <see langword="null"/> or empty for the default order.
    /// Names are matched to <paramref name="defaults"/> ignoring case and surrounding spaces; repeats and names no tab
    /// answers to (a tab of a newer version, a typo in a hand-edited file) are skipped.
    /// </param>
    /// <returns>
    /// Every tab of <paramref name="defaults"/> exactly once, spelled as there. A tab the stored order lacks (one added by a
    /// newer version than the one that stored the order) goes right after its predecessor in the default order, wherever
    /// the user put that one — so it lands next to the tab it belongs with.
    /// </returns>
    public static IReadOnlyList<string> Arrange(IReadOnlyList<string> defaults, IReadOnlyList<string>? stored)
    {
        var result = new List<string>(defaults.Count);
        foreach (var name in stored ?? [])
        {
            if (Canonical(defaults, name) is { } tab && !result.Contains(tab))
            {
                result.Add(tab);
            }
        }

        // In default order, so a missing tab's predecessor is always placed already (stored, or inserted just before).
        for (int i = 0; i < defaults.Count; i++)
        {
            if (!result.Contains(defaults[i]))
            {
                int at = i == 0 ? 0 : result.IndexOf(defaults[i - 1]) + 1;
                result.Insert(at, defaults[i]);
            }
        }

        // Whatever a hand-edited file says, the whole history stays the first tab.
        if (Canonical(defaults, FixedTab) is { } fixedTab && result.IndexOf(fixedTab) > 0)
        {
            result.Remove(fixedTab);
            result.Insert(0, fixedTab);
        }

        return result;
    }

    /// <summary>
    /// Whether <i>Move left</i> or <i>Move right</i> can move a tab: there is a shown tab on that side, and on the left one
    /// other than <see cref="FixedTab"/>. Never for the fixed tab itself.
    /// </summary>
    /// <param name="order">The full order (<see cref="Arrange"/>).</param>
    /// <param name="tab">The tab to move.</param>
    /// <param name="toRight"><see langword="true"/> for <i>Move right</i>.</param>
    /// <param name="isShown">Whether a tab shows in the row right now (neither hidden nor absent).</param>
    /// <returns><see langword="true"/> when <see cref="Move"/> would change the order; the menu grays the item out otherwise.</returns>
    public static bool CanMove(IReadOnlyList<string> order, string tab, bool toRight, Func<string, bool> isShown) =>
        Target(order, IndexOf(order, tab), toRight, isShown) >= 0;

    /// <summary>
    /// Moves a tab one shown place: <i>Move left</i> puts it right before the nearest shown tab on its left, <i>Move
    /// right</i> right after the nearest shown tab on its right. Tabs that do not show in between keep their places.
    /// </summary>
    /// <param name="order">The full order (<see cref="Arrange"/>).</param>
    /// <param name="tab">The tab to move.</param>
    /// <param name="toRight"><see langword="true"/> for <i>Move right</i>.</param>
    /// <param name="isShown">Whether a tab shows in the row right now (neither hidden nor absent).</param>
    /// <returns>The new full order; <paramref name="order"/> itself when the move is not possible (<see cref="CanMove"/>).</returns>
    public static IReadOnlyList<string> Move(IReadOnlyList<string> order, string tab, bool toRight, Func<string, bool> isShown)
    {
        int from = IndexOf(order, tab);
        int target = Target(order, from, toRight, isShown);
        if (target < 0)
        {
            return order;
        }

        var result = order.ToList();
        result.RemoveAt(from);

        // To the left the target lies before the tab, so its index survives the removal: inserting there puts the tab
        // right before it. To the right the removal shifted the target down by one, so its old index is now the slot right
        // after it.
        result.Insert(target, order[from]);
        return result;
    }

    /// <summary>Whether a tab may be hidden: every tab but <see cref="FixedTab"/>.</summary>
    /// <param name="tab">The tab's name.</param>
    /// <returns><see langword="false"/> for "All" (any case), <see langword="true"/> otherwise.</returns>
    public static bool CanHide(string tab) => !IsFixed(tab);

    /// <summary>
    /// The order to store (<c>AppSettings.TabOrder</c>): empty while the tabs stand in the default order, so a later
    /// version's own default order (a tab added, the defaults rearranged) still reaches users who never moved a tab.
    /// </summary>
    /// <param name="order">The full order shown.</param>
    /// <param name="defaults">Every tab, in the default order.</param>
    /// <returns>An empty list for the default order, else the full order.</returns>
    public static IReadOnlyList<string> ToStored(IReadOnlyList<string> order, IReadOnlyList<string> defaults) =>
        order.SequenceEqual(defaults, StringComparer.OrdinalIgnoreCase) ? [] : order.ToArray();

    /// <summary>
    /// The index a move puts the tab at (see <see cref="Move"/>), or -1 when it cannot move: the tab is unknown or fixed, no
    /// shown tab lies on that side, or the nearest one on the left is the fixed tab.
    /// </summary>
    /// <param name="order">The full order.</param>
    /// <param name="from">The tab's index, or -1.</param>
    /// <param name="toRight">The direction.</param>
    /// <param name="isShown">Whether a tab shows.</param>
    /// <returns>The index of the nearest shown tab on that side, or -1.</returns>
    private static int Target(IReadOnlyList<string> order, int from, bool toRight, Func<string, bool> isShown)
    {
        if (from < 0 || IsFixed(order[from]))
        {
            return -1;
        }

        if (toRight)
        {
            for (int k = from + 1; k < order.Count; k++)
            {
                if (isShown(order[k]))
                {
                    return k;
                }
            }

            return -1;
        }

        for (int j = from - 1; j >= 0; j--)
        {
            if (isShown(order[j]))
            {
                // The fixed tab is shown and first: nothing passes it.
                return IsFixed(order[j]) ? -1 : j;
            }
        }

        return -1;
    }

    /// <summary>A tab's index in an order, its name compared ignoring case; -1 when absent.</summary>
    /// <param name="order">The order.</param>
    /// <param name="tab">The name.</param>
    /// <returns>The index, or -1.</returns>
    private static int IndexOf(IReadOnlyList<string> order, string tab)
    {
        for (int i = 0; i < order.Count; i++)
        {
            if (string.Equals(order[i], tab, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Whether a name is the fixed tab's (ignoring case and surrounding spaces).</summary>
    /// <param name="tab">The name.</param>
    /// <returns><see langword="true"/> for "All".</returns>
    private static bool IsFixed(string? tab) => string.Equals(tab?.Trim(), FixedTab, StringComparison.OrdinalIgnoreCase);

    /// <summary>The spelling <paramref name="defaults"/> uses for a name (ignoring case and surrounding spaces), if any.</summary>
    /// <param name="defaults">The known tabs.</param>
    /// <param name="name">A name from storage; may be <see langword="null"/>.</param>
    /// <returns>The known tab's name, or <see langword="null"/> for a name no tab answers to.</returns>
    private static string? Canonical(IReadOnlyList<string> defaults, string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        foreach (var tab in defaults)
        {
            if (string.Equals(tab, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return tab;
            }
        }

        return null;
    }
}
