namespace BetterClipboard.Core.Presentation;

/// <summary>
/// A button of the panel's header that Settings can hide and move (Settings › Shortcut › Buttons in the panel, CLAUDE.md
/// §2.55, §2.58), listed in the header's default order from left to right. The Settings button is not one: it always shows,
/// and it stays last, where it is (the owner's "have the settings stationary"), so Settings — where a hidden button is shown
/// again — stays one click away from the panel, at a place the hand knows.
/// </summary>
/// <remarks>
/// Stored by name in <see cref="Settings.AppSettings.HiddenPanelButtons"/> and <see cref="Settings.AppSettings.PanelButtonOrder"/>,
/// read leniently (<see cref="PanelButtons.TryParse"/>), never written as an enum: a name a version does not know would make
/// that version's JSON reader throw and set the whole settings file aside (<see cref="Settings.FlyoutPlacement"/>'s remarks).
/// So a later version may add a member, and an older one only drops its name. Never rename a member: its stored name would
/// stop answering, and that button would show again, at its default place.
/// </remarks>
public enum PanelButton
{
    /// <summary>
    /// The pin, left of the update button: keeps the panel open after pasting (<see cref="Settings.AppSettings.KeepPanelOpen"/>).
    /// Hidden, the panel is not kept open (<see cref="Settings.AppSettings.PanelStaysOpen"/>): no mode may be on that the
    /// panel does not show.
    /// </summary>
    KeepOpen,

    /// <summary>
    /// The update button (CLAUDE.md §2.29). Hidden, the tray menu's <i>Update to …</i> still opens the update dialog (over
    /// the Settings button then), Settings › Updates keeps its own dialog, and the weekly reminder, which is this button's
    /// tooltip, stays quiet.
    /// </summary>
    Updates,

    /// <summary>Pause / resume capturing. Hidden, the tray menu and Settings › Privacy still pause, and the "Paused" chip still shows.</summary>
    Pause,

    /// <summary>The bookmark that opens the groups column. Hidden, <c>Ctrl+G</c> still opens and closes it.</summary>
    Groups,

    /// <summary>The trash: clear history. Hidden, Settings › Privacy › Clear history still clears it.</summary>
    Clear,
}

/// <summary>
/// Rules for <see cref="Settings.AppSettings.HiddenPanelButtons"/> and <see cref="Settings.AppSettings.PanelButtonOrder"/>:
/// reading a stored name, keeping the lists canonical, the order to show and where a move puts a button (CLAUDE.md §2.58),
/// and counting the header's buttons for the panel's minimum width (<see cref="Settings.AppSettings.MinFlyoutWidthFor"/>).
/// </summary>
/// <remarks>
/// <para>Pure and thread-safe, so the rules are unit-tested; the panel and Settings only apply them.</para>
/// <para>
/// <b>The order</b> (the owner's request, 2026-11-20: "Order up/down arrows to resort the buttons and have them shown in the
/// sorted and also have the settings stationary"): Settings' list runs top to bottom as the header runs left to right, so
/// <i>up</i> moves a button left. The Settings button is in no order: it always ends the header. A move steps over the
/// buttons Settings does not list (the update button of a copy without an update system), so every press visibly moves
/// the row, and over nothing else: a hidden button is listed (unticked) and keeps its place for when it shows again.
/// </para>
/// </remarks>
public static class PanelButtons
{
    /// <summary>
    /// Every button Settings can hide, in the header's default order from left to right: the order of a copy whose user never
    /// moved a button, and the canonical order of <see cref="Settings.AppSettings.HiddenPanelButtons"/>.
    /// </summary>
    public static IReadOnlyList<PanelButton> All { get; } = Enum.GetValues<PanelButton>();

    /// <summary>
    /// Reads a stored button name: a <see cref="PanelButton"/> member's name, ignoring case and surrounding spaces.
    /// </summary>
    /// <remarks>
    /// Names only, compared one by one, as <see cref="Settings.AppSettings.TryParseTab"/> does for tabs:
    /// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> would also take numbers ("3", and "99", which is no
    /// button at all) and comma-separated lists, neither of which this app writes.
    /// </remarks>
    /// <param name="name">The stored name; may be <see langword="null"/> or anything a hand-edited file holds.</param>
    /// <param name="button">The button; <see cref="PanelButton.KeepOpen"/> when the name is unknown (check the result).</param>
    /// <returns><see langword="true"/> when the name is a button's (never an exception).</returns>
    public static bool TryParse(string? name, out PanelButton button)
    {
        var trimmed = name?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            foreach (var candidate in All)
            {
                if (string.Equals(candidate.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    button = candidate;
                    return true;
                }
            }
        }

        button = PanelButton.KeepOpen;
        return false;
    }

    /// <summary>
    /// The canonical form of a stored list of hidden buttons: the known names, spelled as their member, each once, in the
    /// default order (<see cref="All"/>).
    /// </summary>
    /// <remarks>
    /// Idempotent, since <see cref="Settings.AppSettings.Normalize"/> runs on every load and save. A name no button answers
    /// to (a button of a newer version, a typo in a hand-edited file, a blank) is dropped: the cost is that a newer
    /// version's button shows again after an older version saved the file, never a broken file. The default order, not the
    /// stored one nor the user's arrangement (<see cref="Settings.AppSettings.PanelButtonOrder"/>), so hiding buttons in
    /// another order, or after moving them, writes the same file.
    /// </remarks>
    /// <param name="names">The stored names (may be <see langword="null"/> or hold <see langword="null"/>).</param>
    /// <returns>The known names, canonical, distinct and in the default order.</returns>
    public static string[] Normalize(IReadOnlyList<string>? names)
    {
        var hidden = new HashSet<PanelButton>();
        foreach (var name in names ?? [])
        {
            if (TryParse(name, out var button))
            {
                hidden.Add(button);
            }
        }

        return All.Where(hidden.Contains).Select(button => button.ToString()).ToArray();
    }

    /// <summary>Whether <paramref name="button"/> is among <paramref name="hidden"/>, read leniently (a list not normalized yet counts too).</summary>
    /// <param name="hidden">The stored names (may be <see langword="null"/>).</param>
    /// <param name="button">The button asked about.</param>
    /// <returns><see langword="true"/> when the list names it.</returns>
    public static bool IsHidden(IReadOnlyList<string>? hidden, PanelButton button)
    {
        foreach (var name in hidden ?? [])
        {
            if (TryParse(name, out var stored) && stored == button)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The hidden list after Settings showed or hid one button: <paramref name="button"/> taken out of
    /// <paramref name="hidden"/> or added to it, the rest kept, canonical.
    /// </summary>
    /// <param name="hidden">The stored names (may be <see langword="null"/>).</param>
    /// <param name="button">The button the user ticked or unticked.</param>
    /// <param name="shown">Whether it should show.</param>
    /// <returns>The new list, normalized (<see cref="Normalize"/>).</returns>
    public static string[] WithShown(IReadOnlyList<string>? hidden, PanelButton button, bool shown)
    {
        var names = Normalize(hidden).Where(name => !string.Equals(name, button.ToString(), StringComparison.Ordinal));
        return Normalize(shown ? [.. names] : [.. names, button.ToString()]);
    }

    /// <summary>
    /// The canonical form of a stored order (<see cref="Settings.AppSettings.PanelButtonOrder"/>): the known names, spelled as
    /// their member, each once, in the stored order. Not completed: <see cref="Arrange"/> does that when the order is read.
    /// </summary>
    /// <remarks>
    /// Idempotent, since <see cref="Settings.AppSettings.Normalize"/> runs on every load and save. A name no button answers to
    /// (a button of a newer version, "Settings", a typo, a blank) is dropped: the cost is that button's place, which a version
    /// that knows it fills again with the button's default place (<see cref="Arrange"/>), never a broken file.
    /// </remarks>
    /// <param name="names">The stored names (may be <see langword="null"/> or hold <see langword="null"/>).</param>
    /// <returns>The known names, canonical and distinct, in the stored order.</returns>
    public static string[] NormalizeOrder(IReadOnlyList<string>? names)
    {
        var result = new List<string>();
        foreach (var name in names ?? [])
        {
            if (TryParse(name, out var button) && !result.Contains(button.ToString()))
            {
                result.Add(button.ToString());
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// The full order to show, left to right before the Settings button: the stored order, completed with the buttons it
    /// lacks. A button the stored order lacks (one a newer version added, or one an older version's save dropped) goes right
    /// after its predecessor in <see cref="All"/>, wherever the user put that one, so it lands next to the button it came
    /// with, as <see cref="TabArrangement.Arrange"/> places a tab.
    /// </summary>
    /// <param name="stored">
    /// <see cref="Settings.AppSettings.PanelButtonOrder"/>, or <see langword="null"/> or empty for the default order. Names
    /// are read leniently (<see cref="TryParse"/>); repeats and names no button answers to are skipped.
    /// </param>
    /// <returns>Every button of <see cref="All"/> exactly once.</returns>
    public static IReadOnlyList<PanelButton> Arrange(IReadOnlyList<string>? stored)
    {
        var result = new List<PanelButton>(All.Count);
        foreach (var name in stored ?? [])
        {
            if (TryParse(name, out var button) && !result.Contains(button))
            {
                result.Add(button);
            }
        }

        // In default order, so a missing button's predecessor is always placed already (stored, or inserted just before).
        for (int i = 0; i < All.Count; i++)
        {
            if (!result.Contains(All[i]))
            {
                int at = i == 0 ? 0 : result.IndexOf(All[i - 1]) + 1;
                result.Insert(at, All[i]);
            }
        }

        return result;
    }

    /// <summary>
    /// Whether Settings' up or down arrow can move a button: Settings lists it, and another listed button lies that way. The
    /// arrow is disabled otherwise (the top row's up arrow, the bottom row's down arrow); a button Settings does not list has
    /// no arrows and never moves.
    /// </summary>
    /// <param name="order">The full order (<see cref="Arrange"/>).</param>
    /// <param name="button">The button to move.</param>
    /// <param name="later"><see langword="true"/> for down (right in the panel), <see langword="false"/> for up (left).</param>
    /// <param name="isListed">
    /// Whether Settings lists a button: every button, except the update button in a copy without an update system (the
    /// Microsoft Store edition), which has nothing to show or hide.
    /// </param>
    /// <returns><see langword="true"/> when <see cref="Move"/> would change the order.</returns>
    public static bool CanMove(IReadOnlyList<PanelButton> order, PanelButton button, bool later, Func<PanelButton, bool> isListed) =>
        Target(order, IndexOf(order, button), later, isListed) >= 0;

    /// <summary>
    /// Moves a button one listed place: up puts it right before the nearest listed button above it (left in the panel),
    /// down right after the nearest listed button below it. A button Settings does not list keeps its place between them.
    /// </summary>
    /// <param name="order">The full order (<see cref="Arrange"/>).</param>
    /// <param name="button">The button to move.</param>
    /// <param name="later"><see langword="true"/> for down (right in the panel), <see langword="false"/> for up (left).</param>
    /// <param name="isListed">Whether Settings lists a button (<see cref="CanMove"/>).</param>
    /// <returns>The new full order; <paramref name="order"/> itself when the move is not possible (<see cref="CanMove"/>).</returns>
    public static IReadOnlyList<PanelButton> Move(IReadOnlyList<PanelButton> order, PanelButton button, bool later, Func<PanelButton, bool> isListed)
    {
        int from = IndexOf(order, button);
        int target = Target(order, from, later, isListed);
        if (target < 0)
        {
            return order;
        }

        var result = order.ToList();
        result.RemoveAt(from);

        // Up, the target lies before the button, so its index survives the removal: inserting there puts the button right
        // before it. Down, the removal shifted the target down by one, so its old index is now the slot right after it.
        result.Insert(target, order[from]);
        return result;
    }

    /// <summary>
    /// The order to store (<see cref="Settings.AppSettings.PanelButtonOrder"/>): empty while the buttons stand in the default
    /// order, so a later version's own default order (a button added, the defaults rearranged) still reaches users who never
    /// moved a button; otherwise every button's name, in order.
    /// </summary>
    /// <param name="order">The full order shown.</param>
    /// <returns>An empty array for the default order, else the full order's names.</returns>
    public static string[] ToStored(IReadOnlyList<PanelButton> order) =>
        order.SequenceEqual(All) ? [] : order.Select(button => button.ToString()).ToArray();

    /// <summary>
    /// The index a move puts the button at (see <see cref="Move"/>), or -1 when it cannot move: the button is not in the
    /// order, Settings does not list it (it has no arrows there), or no listed button lies that way.
    /// </summary>
    /// <param name="order">The full order.</param>
    /// <param name="from">The button's index, or -1.</param>
    /// <param name="later">The direction.</param>
    /// <param name="isListed">Whether Settings lists a button.</param>
    /// <returns>The index of the nearest listed button that way, or -1.</returns>
    private static int Target(IReadOnlyList<PanelButton> order, int from, bool later, Func<PanelButton, bool> isListed)
    {
        if (from < 0 || !isListed(order[from]))
        {
            return -1;
        }

        int step = later ? 1 : -1;
        for (int k = from + step; k >= 0 && k < order.Count; k += step)
        {
            if (isListed(order[k]))
            {
                return k;
            }
        }

        return -1;
    }

    /// <summary>A button's index in an order; -1 when absent.</summary>
    /// <param name="order">The order.</param>
    /// <param name="button">The button.</param>
    /// <returns>The index, or -1.</returns>
    private static int IndexOf(IReadOnlyList<PanelButton> order, PanelButton button)
    {
        for (int i = 0; i < order.Count; i++)
        {
            if (order[i] == button)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// How many buttons the header's right-hand group shows — the Settings button included — for the panel's minimum
    /// width (<see cref="Settings.AppSettings.MinFlyoutWidthFor"/>).
    /// </summary>
    /// <param name="hidden">The stored hidden names (may be <see langword="null"/>).</param>
    /// <param name="hasUpdateButton">
    /// Whether this copy has an update button at all: not when its update system did not start (the Microsoft Store
    /// edition, whose updates come from the Store), whatever the list says.
    /// </param>
    /// <returns>Between 1 (Settings alone) and <see cref="All"/>'s count plus one.</returns>
    public static int CountShown(IReadOnlyList<string>? hidden, bool hasUpdateButton)
    {
        int shown = 1; // Settings, which no list can hide
        foreach (var button in All)
        {
            if ((button != PanelButton.Updates || hasUpdateButton) && !IsHidden(hidden, button))
            {
                shown++;
            }
        }

        return shown;
    }
}
