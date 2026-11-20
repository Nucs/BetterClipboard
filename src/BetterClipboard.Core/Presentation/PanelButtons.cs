namespace BetterClipboard.Core.Presentation;

/// <summary>
/// A button of the panel's header that Settings can hide (Settings › Shortcut › Buttons in the panel, CLAUDE.md §2.55),
/// listed in the header's order from left to right. The Settings button is not one: it always shows, so Settings — where a
/// hidden button is shown again — stays one click away from the panel.
/// </summary>
/// <remarks>
/// Stored by name in <see cref="Settings.AppSettings.HiddenPanelButtons"/> and read leniently
/// (<see cref="PanelButtons.TryParse"/>), never written as an enum: a name a version does not know would make that version's
/// JSON reader throw and set the whole settings file aside (<see cref="Settings.FlyoutPlacement"/>'s remarks). So a later
/// version may add a member, and an older one only drops its name. Never rename a member: its stored name would stop
/// answering, and that button would show again.
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
/// Rules for <see cref="Settings.AppSettings.HiddenPanelButtons"/>: reading a stored name, keeping the list canonical, and
/// counting the header's buttons for the panel's minimum width (<see cref="Settings.AppSettings.MinFlyoutWidthFor"/>).
/// </summary>
/// <remarks>Pure and thread-safe, so the rules are unit-tested; the panel and Settings only apply them.</remarks>
public static class PanelButtons
{
    /// <summary>Every button Settings can hide, in the header's order from left to right.</summary>
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
    /// header's order.
    /// </summary>
    /// <remarks>
    /// Idempotent, since <see cref="Settings.AppSettings.Normalize"/> runs on every load and save. A name no button answers
    /// to (a button of a newer version, a typo in a hand-edited file, a blank) is dropped: the cost is that a newer
    /// version's button shows again after an older version saved the file, never a broken file. The header's order, not
    /// the stored one, so hiding buttons in another order writes the same file.
    /// </remarks>
    /// <param name="names">The stored names (may be <see langword="null"/> or hold <see langword="null"/>).</param>
    /// <returns>The known names, canonical, distinct and in the header's order.</returns>
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
