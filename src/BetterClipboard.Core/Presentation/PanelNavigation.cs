namespace BetterClipboard.Core.Presentation;

/// <summary>
/// What a Left or Right arrow key does in the panel (<see cref="PanelNavigation.HorizontalArrow"/>).
/// </summary>
public enum HorizontalArrowAction
{
    /// <summary>
    /// Not the panel's key: it goes on to the control that has the keyboard — the search box moves its caret, changes its
    /// selection or jumps a word.
    /// </summary>
    NotHandled = 0,

    /// <summary>Show the tab before the selected one (from the first tab: the last one, see <see cref="PanelNavigation.CycleTab"/>).</summary>
    PreviousTab = 1,

    /// <summary>Show the tab after the selected one (from the last tab: the first one).</summary>
    NextTab = 2,

    /// <summary>Right, while the arrow keys walk the groups column: back to the list.</summary>
    LeaveGroups = 3,

    /// <summary>
    /// Left, while the arrow keys walk the groups column: nothing lies further left. The key is taken anyway, so that it
    /// does not move the search box's caret behind the user's back.
    /// </summary>
    Swallow = 4,
}

/// <summary>
/// Where an Up, Down, Page Up or Page Down key takes the selection of the panel's list
/// (<see cref="PanelNavigation.MoveInList"/>).
/// </summary>
/// <param name="Index">The row to select, or -1 for none: the list is empty, or the key leaves the list.</param>
/// <param name="EntersGroups">
/// Whether the key walks from the top of the list into the groups column instead of selecting a row
/// (<paramref name="Index"/> is then -1).
/// </param>
public readonly record struct ListMove(int Index, bool EntersGroups);

/// <summary>
/// The rules of the panel's arrow keys: Up and Down move through the rows, Left and Right through the tabs in a circle, and
/// Up from the top row walks into the groups column, where Up and Down then move through the groups until Right goes back
/// to the list.
/// </summary>
/// <remarks>
/// <para>
/// Pure and free of UI types, so the rules are unit-tested; the panel reads its own state (which row is selected, how
/// many tabs show, whether the key repeats), asks here, and acts on the answer.
/// </para>
/// <para>
/// <b>Held keys stop at an edge, a new press crosses it.</b> A key that repeats because it is held down never wraps from
/// the last tab to the first (<see cref="CycleTab"/>) and never walks from the top row into the groups column
/// (<see cref="MoveInList"/>): holding Up to get back to the top of a long list would otherwise run on through every
/// group, and holding Right would spin through the tabs for as long as it is held. Pressing the key again crosses.
/// </para>
/// <para>
/// <b>Positions in the groups column</b> count from its top: 0 is the clipboard icon that stands for the regular view (the
/// whole history), 1 to <c>groupCount</c> are the groups in column order. The column's "+" button is not a position: it
/// creates a group, it does not show one.
/// </para>
/// </remarks>
public static class PanelNavigation
{
    /// <summary>
    /// Rows (or groups) a Page Up or Page Down key moves: about what the panel shows at its default size, so a page key
    /// brings a new screenful into view.
    /// </summary>
    public const int PageStep = 5;

    /// <summary>
    /// The tab a Left or Right key selects: the neighbor of the selected tab among the tabs that show, wrapping around at
    /// both ends — Left on the first tab selects the last one, Right on the last tab the first.
    /// </summary>
    /// <param name="current">
    /// The selected tab's position among the visible tabs, 0-based; any value outside <c>0..count-1</c> means none of them
    /// is selected (the selected tab was just hidden).
    /// </param>
    /// <param name="count">How many tabs show.</param>
    /// <param name="forward"><see langword="true"/> for Right (towards the last tab), <see langword="false"/> for Left.</param>
    /// <param name="isRepeat">
    /// Whether the key repeats because it is held down: a repeat stops at the first or last tab instead of wrapping (see
    /// the class remarks).
    /// </param>
    /// <returns>
    /// The position to select; <paramref name="current"/> itself when nothing changes (one tab only, or a held key at an
    /// end); -1 when no tab shows. With no tab selected, Right starts at the first tab and Left at the last.
    /// </returns>
    public static int CycleTab(int current, int count, bool forward, bool isRepeat)
    {
        if (count <= 0)
        {
            return -1;
        }

        int last = count - 1;
        if (current < 0 || current > last)
        {
            return forward ? 0 : last;
        }

        if (forward)
        {
            return current < last ? current + 1 : isRepeat ? current : 0;
        }

        return current > 0 ? current - 1 : isRepeat ? current : last;
    }

    /// <summary>
    /// Decides what a Left or Right arrow key does: switch tabs, leave the groups column, or nothing because the search
    /// box needs the key to edit its text.
    /// </summary>
    /// <remarks>
    /// The search box keeps the keyboard focus the whole time, so the same key must serve two masters. The rule: Left and
    /// Right belong to the text while it is being edited — the box holds text and the last key went into it — and to the
    /// tabs otherwise: with an empty box (there is no caret to move), or once an arrow key steered the panel
    /// (<paramref name="isNavigating"/>), which is what "using the arrow keys" means. Typing or clicking into the box
    /// hands them back to the text.
    /// </remarks>
    /// <param name="right"><see langword="true"/> for the Right key, <see langword="false"/> for Left.</param>
    /// <param name="hasModifier">
    /// Whether Ctrl, Shift, Alt or the Windows key is held: such chords are text-editing keys (word jumps, selections) or
    /// someone else's shortcuts, never tab switches.
    /// </param>
    /// <param name="inGroups">Whether the arrow keys walk the groups column right now.</param>
    /// <param name="inTextBox">Whether the key was typed into a text box (the search box), as opposed to a tab or a button that has the focus.</param>
    /// <param name="hasSearchText">Whether the search box holds any text.</param>
    /// <param name="isNavigating">
    /// Whether the last key the panel acted on was an arrow key (Up, Down, Left, Right, Page Up, Page Down) and nothing was
    /// typed or clicked into the search box since.
    /// </param>
    /// <returns>The action; <see cref="HorizontalArrowAction.NotHandled"/> leaves the key to the focused control.</returns>
    public static HorizontalArrowAction HorizontalArrow(bool right, bool hasModifier, bool inGroups, bool inTextBox, bool hasSearchText, bool isNavigating)
    {
        if (hasModifier)
        {
            return HorizontalArrowAction.NotHandled;
        }

        if (inGroups)
        {
            return right ? HorizontalArrowAction.LeaveGroups : HorizontalArrowAction.Swallow;
        }

        if (inTextBox && hasSearchText && !isNavigating)
        {
            return HorizontalArrowAction.NotHandled;
        }

        return right ? HorizontalArrowAction.NextTab : HorizontalArrowAction.PreviousTab;
    }

    /// <summary>
    /// Where an Up, Down, Page Up or Page Down key takes the list's selection: to another row (clamped to the list), or —
    /// for a new press of Up on the top row — into the groups column.
    /// </summary>
    /// <param name="selected">The selected row, or a negative value when no row is selected.</param>
    /// <param name="count">How many rows the list holds.</param>
    /// <param name="delta">Rows to move: -1 for Up, 1 for Down, ±<see cref="PageStep"/> for the page keys.</param>
    /// <param name="isRepeat">Whether the key repeats because it is held down (a repeat never enters the groups column).</param>
    /// <param name="groupsReachable">Whether the groups column is open and holds at least one group.</param>
    /// <returns>
    /// The row to select, or the walk into the groups column: only for Up (<paramref name="delta"/> -1, never a page key),
    /// pressed anew, with the column reachable, while the top row is selected or the list is empty. With no row selected,
    /// Up and Down select the first row, as they always did.
    /// </returns>
    public static ListMove MoveInList(int selected, int count, int delta, bool isRepeat, bool groupsReachable)
    {
        if (delta == -1 && !isRepeat && groupsReachable && (count <= 0 || selected == 0))
        {
            return new ListMove(-1, true);
        }

        if (count <= 0)
        {
            return new ListMove(-1, false);
        }

        // No selection counts as "just above the first row", so Down selects it (and Page Down the fifth row).
        long current = selected < 0 ? -1 : Math.Min(selected, count - 1);
        return new ListMove((int)Math.Clamp(current + delta, 0, count - 1), false);
    }

    /// <summary>
    /// The position the arrow keys land on when they walk from the top of the list into the groups column: the lowest
    /// group, from where further presses of Up go through the groups to the clipboard icon at the top.
    /// </summary>
    /// <param name="groupCount">How many groups the column holds.</param>
    /// <returns>The lowest group's position (<paramref name="groupCount"/>); 0, the clipboard icon, for a column without groups.</returns>
    public static int EnterGroups(int groupCount) => Math.Max(0, groupCount);

    /// <summary>
    /// Where an Up, Down, Page Up or Page Down key takes the position in the groups column. It stops at both ends: at the
    /// clipboard icon on top and at the lowest group — only Right (or the mouse) leaves the column.
    /// </summary>
    /// <param name="position">The position now (see the class remarks); values outside the column are clamped first.</param>
    /// <param name="groupCount">How many groups the column holds.</param>
    /// <param name="delta">Positions to move: negative towards the top.</param>
    /// <returns>The new position, between 0 and <paramref name="groupCount"/>.</returns>
    public static int MoveInGroups(int position, int groupCount, int delta)
    {
        int last = Math.Max(0, groupCount);
        long current = Math.Clamp(position, 0, last);
        return (int)Math.Clamp(current + delta, 0, last);
    }
}
