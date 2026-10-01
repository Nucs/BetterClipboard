using System.Globalization;
using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.Presentation;

/// <summary>
/// Names a group view in the panel's texts, for one group or several merged ones (<see cref="GroupSelection"/>): the
/// header after "Clipboard ›", the search box's placeholder, the footer's count, the empty state and the card menu's
/// "Remove from …".
/// </summary>
/// <remarks>
/// <para>
/// Pure, so the wording is unit-tested. Groups are named in the order given (the column's, which
/// <see cref="GroupSelection.Ids"/> keeps).
/// </para>
/// <para>
/// <b>Naming or counting.</b> One group is always named, exactly as before groups could be merged. Several are named
/// only while they fit (<see cref="NamesFit"/>: at most <see cref="MaxNamedGroups"/> groups with at most
/// <see cref="MaxNamedLength"/> characters of names together), and counted otherwise ("4 groups"). The footer's count
/// sits in an auto-sized column next to the key hints, so two 40-character names would push the hints out and clip
/// the count itself; the placeholder is cut by the search box's width. Only <see cref="Title"/> always names every
/// group: the header trims at the window's edge.
/// </para>
/// </remarks>
public static class GroupViewText
{
    /// <summary>Most groups named in a short text before it counts them instead ("4 groups").</summary>
    public const int MaxNamedGroups = 3;

    /// <summary>Most characters of group names, all together, named in a short text before it counts them instead.</summary>
    public const int MaxNamedLength = 24;

    /// <summary>
    /// The header's text after "Clipboard ›": every name, joined with " + " (e.g. "Work + Home + Ideas"); the header
    /// trims what does not fit.
    /// </summary>
    /// <param name="groups">The groups shown, in column order.</param>
    /// <returns>The names; empty for no groups (the regular view has no suffix).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="groups"/> is <see langword="null"/>.</exception>
    public static string Title(IReadOnlyList<ClipGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        return string.Join(" + ", groups.Select(g => g.Name));
    }

    /// <summary>
    /// A short name for the view, after "Search in …" and "12 in …": "Work", "Work + Home", "Work + Home + Ideas", or
    /// "4 groups" when the names do not fit (see the class remarks).
    /// </summary>
    /// <param name="groups">The groups shown, in column order (at least one).</param>
    /// <param name="culture">Number formatting for the counted form (defaults to the current UI culture).</param>
    /// <returns>The name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="groups"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="groups"/> is empty: the regular view is not a group view.</exception>
    public static string ShortName(IReadOnlyList<ClipGroup> groups, CultureInfo? culture = null) =>
        NamesFit(groups) ? string.Join(" + ", groups.Select(g => g.Name)) : Count(groups.Count, culture);

    /// <summary>
    /// The view as "any of them", for sentences that are true of none of the groups: "Nothing in Work or Home yet" —
    /// "Work", "Work or Home", "Work, Home or Ideas", or "these 4 groups" when the names do not fit.
    /// </summary>
    /// <param name="groups">The groups shown, in column order (at least one).</param>
    /// <param name="culture">Number formatting for the counted form (defaults to the current UI culture).</param>
    /// <returns>The phrase.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="groups"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="groups"/> is empty.</exception>
    public static string AnyOf(IReadOnlyList<ClipGroup> groups, CultureInfo? culture = null) =>
        NamesFit(groups) ? Sentence(groups, "or") : "these " + Count(groups.Count, culture);

    /// <summary>
    /// The groups as "all of them", for actions on each: "Remove from Work and Home" — "Work", "Work and Home",
    /// "Work, Home and Ideas", or "these 4 groups" when the names do not fit.
    /// </summary>
    /// <param name="groups">The groups acted on, in column order (at least one).</param>
    /// <param name="culture">Number formatting for the counted form (defaults to the current UI culture).</param>
    /// <returns>The phrase.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="groups"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="groups"/> is empty.</exception>
    public static string AllOf(IReadOnlyList<ClipGroup> groups, CultureInfo? culture = null) =>
        NamesFit(groups) ? Sentence(groups, "and") : "these " + Count(groups.Count, culture);

    /// <summary>
    /// Whether the groups are named rather than counted in the short texts: always for one group (whatever its length,
    /// as before groups could be merged); for several, at most <see cref="MaxNamedGroups"/> of them with at most
    /// <see cref="MaxNamedLength"/> characters of names together.
    /// </summary>
    /// <param name="groups">The groups (at least one).</param>
    /// <returns><see langword="true"/> to name them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="groups"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="groups"/> is empty.</exception>
    public static bool NamesFit(IReadOnlyList<ClipGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (groups.Count == 0)
        {
            throw new ArgumentException("A group view has at least one group.", nameof(groups));
        }

        return groups.Count == 1 || (groups.Count <= MaxNamedGroups && groups.Sum(g => g.Name.Length) <= MaxNamedLength);
    }

    /// <summary>Names joined as an English list: "A", "A or B", "A, B or C".</summary>
    /// <param name="groups">The groups (at least one).</param>
    /// <param name="conjunction">"or" or "and", placed before the last name.</param>
    /// <returns>The list.</returns>
    private static string Sentence(IReadOnlyList<ClipGroup> groups, string conjunction) => groups.Count == 1
        ? groups[0].Name
        : $"{string.Join(", ", groups.Take(groups.Count - 1).Select(g => g.Name))} {conjunction} {groups[^1].Name}";

    /// <summary>"4 groups" with grouped digits.</summary>
    /// <param name="count">How many groups (more than one in practice: one group is always named).</param>
    /// <param name="culture">Number formatting (defaults to the current UI culture).</param>
    /// <returns>The phrase.</returns>
    private static string Count(int count, CultureInfo? culture) =>
        string.Create(culture ?? CultureInfo.CurrentUICulture, $"{count:N0} groups");
}
