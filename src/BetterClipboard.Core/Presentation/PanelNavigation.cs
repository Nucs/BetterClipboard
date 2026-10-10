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
/// How the arrow keys treat the groups column during one walk through it. Decided once, when the keys walk in
/// (<see cref="PanelNavigation.EnterGroups"/>), from how many groups the list shows at that moment, and kept until the
/// walk ends — so the keys never change their meaning in the middle of a walk.
/// </summary>
public enum GroupWalkMode
{
    /// <summary>
    /// The view follows the keys: every step shows the group it lands on, alone — or the whole history on the clipboard
    /// icon. The walk while no group or one group is shown: there is no merged view a step could throw away.
    /// </summary>
    Show = 0,

    /// <summary>
    /// The keys only move: the groups shown stay as they are, and Space or Enter adds the group the keys are on to them,
    /// or takes it out (<see cref="PanelNavigation.PickKey"/>). The walk while several groups are merged into the list:
    /// a step that showed its group alone would throw away a view that took deliberate clicks to put together.
    /// </summary>
    Pick = 1,
}

/// <summary>
/// Where the arrow keys land when they walk into the groups column, and how they treat it from there
/// (<see cref="PanelNavigation.EnterGroups"/>).
/// </summary>
/// <param name="Position">
/// The position the keys land on, as <see cref="PanelNavigation"/> counts them: 0 is the clipboard icon, 1 and up are the
/// groups in column order.
/// </param>
/// <param name="Mode">Whether the steps of this walk show the groups they land on, or only move so that Space and Enter can pick.</param>
public readonly record struct GroupsEntry(int Position, GroupWalkMode Mode);

/// <summary>
/// What a Space or Enter key does with the group the arrow keys are on (<see cref="PanelNavigation.PickKey"/>).
/// </summary>
public enum GroupPickAction
{
    /// <summary>
    /// Not the groups' key: it keeps its meaning in the panel — Space is typed into the search box, Enter pastes the
    /// selected card (Shift+Enter as plain text, Ctrl+Enter runs a command).
    /// </summary>
    NotHandled = 0,

    /// <summary>
    /// Add the group the keys are on to the groups shown, or take it out when it is one of them — what a Ctrl+click on
    /// its icon does. On the clipboard icon: back to the whole history, like a click on it.
    /// </summary>
    Pick = 1,

    /// <summary>
    /// A repeat of a pick key that is held down. The key is taken — nothing is typed and nothing is pasted — but the pick
    /// is not repeated: it would add the group and take it out again, reloading the list each time, for as long as the
    /// key is held.
    /// </summary>
    Swallow = 2,
}

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
/// <para>
/// <b>Two ways to walk the groups column</b> (<see cref="GroupWalkMode"/>, decided by <see cref="EnterGroups"/>). While no
/// group or one group is shown, the view follows the keys: every step shows the group it lands on. While several groups
/// are merged into the list, the keys only move, and Space or Enter adds the group they are on to the merged view or takes
/// it out (<see cref="PickKey"/>) — a step that showed its group alone would throw the merged view away.
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
    /// Where the arrow keys land when they walk from the top of the list into the groups column, and how they treat the
    /// column from there. They land on what the column highlights: the first group the list shows — the topmost one of
    /// several — and the clipboard icon when it shows none (the whole history). With several groups shown the walk
    /// only picks (<see cref="GroupWalkMode.Pick"/>); otherwise it shows every group it steps on
    /// (<see cref="GroupWalkMode.Show"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Walking in never changes what is shown.</b> The keys continue from what the list shows: the ring appears on an
    /// icon that is highlighted already, and nothing else on screen changes. Only the next step does something. Until
    /// 2026-10-10 the keys always landed on the lowest group and showed it alone, which replaced whatever was shown —
    /// the whole history, one group or a merged view — with the last group of the column the moment Up was pressed.
    /// </para>
    /// <para>
    /// <b>No group shown means the clipboard icon, not the first group.</b> Footgun for whoever reads "the top of the
    /// column" as its first group, as the first version of this rule did: the icon on top of the column is the clipboard
    /// icon, which stands for the whole history, and that is what the list shows then. The first group is one step down.
    /// </para>
    /// <para>
    /// <b>The mode holds for the whole walk.</b> It is decided here and not asked again at every key: a walk that picks
    /// goes on picking after Space took a group out and one group or none is left, so the next group can be added. The
    /// next walk decides anew.
    /// </para>
    /// <para>
    /// The groups shown are counted against <paramref name="column"/>, not by the length of <paramref name="shown"/>: an
    /// id that is no longer in the column has no icon to land on and no cards in the list, so it neither is "the first
    /// one" nor makes the view a merged one. Ids are unique within the column (they are the groups' keys).
    /// </para>
    /// </remarks>
    /// <param name="column">The ids of the groups column, top to bottom.</param>
    /// <param name="shown">The ids of the groups the list shows, in any order; empty for the whole history.</param>
    /// <returns>
    /// The position to land on and the mode of the walk: the clipboard icon (0), showing, when the list shows no group
    /// of the column; otherwise the position of the topmost shown group (1 and up), showing for one group and picking
    /// for several.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> or <paramref name="shown"/> is <see langword="null"/>.</exception>
    public static GroupsEntry EnterGroups(IReadOnlyList<long> column, IReadOnlyList<long> shown)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(shown);

        int first = 0;
        int count = 0;
        for (int i = 0; i < column.Count; i++)
        {
            if (!Contains(shown, column[i]))
            {
                continue;
            }

            count++;
            if (first == 0)
            {
                first = i + 1;
            }
        }

        if (count == 0)
        {
            // Nothing shown that the column still has: the list shows the whole history, which is the clipboard icon's.
            return new GroupsEntry(0, GroupWalkMode.Show);
        }

        return new GroupsEntry(first, count > 1 ? GroupWalkMode.Pick : GroupWalkMode.Show);
    }

    /// <summary>
    /// Decides whether a Space or Enter key picks the group the arrow keys are on instead of keeping its meaning in the
    /// panel. It does only during a walk that picks (<see cref="GroupWalkMode.Pick"/>), where it is the one way the keys
    /// change which groups are shown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Enter picks only when pressed alone.</b> With Ctrl or Shift it is the panel's "run" and "paste as plain text";
    /// those keep working on the selected card during the walk, as Ctrl+P and Del do.
    /// </para>
    /// <para>
    /// <b>Space picks with or without Ctrl and Shift</b> (Ctrl+Space is Explorer's key for the same thing, and neither
    /// chord means anything else in the search box). Footgun: this takes Space away from the search box for the length
    /// of the walk — a second search word needs a letter first (typing ends the walk) or Right.
    /// </para>
    /// <para>
    /// With Alt or the Windows key, both keys are Windows' or another program's shortcut (the window menu, the input
    /// language, a launcher), and AltGr arrives as Ctrl+Alt: never a pick.
    /// </para>
    /// </remarks>
    /// <param name="enter"><see langword="true"/> for Enter, <see langword="false"/> for Space.</param>
    /// <param name="inGroups">Whether the arrow keys walk the groups column right now.</param>
    /// <param name="mode">How this walk treats the column; ignored while <paramref name="inGroups"/> is <see langword="false"/>.</param>
    /// <param name="ctrl">Whether Ctrl is held.</param>
    /// <param name="shift">Whether Shift is held.</param>
    /// <param name="altOrWin">Whether Alt or the Windows key is held.</param>
    /// <param name="isRepeat">Whether the key repeats because it is held down (a repeat is taken, but picks nothing).</param>
    /// <returns>What the key does; <see cref="GroupPickAction.NotHandled"/> leaves it its usual meaning.</returns>
    public static GroupPickAction PickKey(bool enter, bool inGroups, GroupWalkMode mode, bool ctrl, bool shift, bool altOrWin, bool isRepeat)
    {
        if (!inGroups || mode != GroupWalkMode.Pick || altOrWin || (enter && (ctrl || shift)))
        {
            return GroupPickAction.NotHandled;
        }

        return isRepeat ? GroupPickAction.Swallow : GroupPickAction.Pick;
    }

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

    /// <summary>Whether a list of ids holds an id (a plain scan: a groups column is a handful of icons).</summary>
    /// <param name="ids">The ids to look through.</param>
    /// <param name="id">The id to find.</param>
    /// <returns><see langword="true"/> when <paramref name="id"/> is among <paramref name="ids"/>.</returns>
    private static bool Contains(IReadOnlyList<long> ids, long id)
    {
        for (int i = 0; i < ids.Count; i++)
        {
            if (ids[i] == id)
            {
                return true;
            }
        }

        return false;
    }
}
