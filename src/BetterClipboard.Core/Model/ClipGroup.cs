namespace BetterClipboard.Core.Model;

/// <summary>
/// A user-made collection of history items, shown as one icon in the panel's groups column.
/// </summary>
/// <remarks>
/// <para>
/// <b>Membership protects like a pin.</b> An item in at least one group is exempt from every retention
/// rule (age, count, size) and survives "clear history", exactly like a pinned item. When it leaves its
/// last group it re-enters retention with a fresh clock (see <see cref="Storage.ClipStore.RemoveFromGroup"/>),
/// so an old item is not pruned the moment it is ungrouped.
/// </para>
/// <para>
/// Snapshot semantics like <see cref="ClipEntry"/>: read once, never updated in place; changes go through
/// the store and a fresh list is read back.
/// </para>
/// </remarks>
/// <param name="Id">Database identity (<c>groups.id</c>), never reused.</param>
/// <param name="Name">Display name (tooltip, header); 1–<see cref="MaxNameLength"/> characters.</param>
/// <param name="Glyph">One Segoe Fluent Icons character (Private Use Area) drawn as the group's icon.</param>
/// <param name="SortOrder">Position in the groups column (ascending; new groups go last).</param>
/// <param name="ItemCount">How many history items are in the group right now.</param>
public sealed record ClipGroup(long Id, string Name, string Glyph, int SortOrder, long ItemCount)
{
    /// <summary>Longest accepted group name (longer input is rejected, not cut, so nothing is silently lost).</summary>
    public const int MaxNameLength = 40;

    /// <summary>
    /// Whether <paramref name="glyph"/> is something the store accepts as a group icon: exactly one
    /// character from the Unicode Private Use Area (U+E000–U+F8FF), where Segoe Fluent Icons keeps its glyphs.
    /// </summary>
    /// <remarks>
    /// Kept this strict on purpose: the icon is rendered with the symbol font, and any other text (letters,
    /// emoji, several characters) would render as a box or overflow the 36-pixel button.
    /// </remarks>
    /// <param name="glyph">Candidate glyph.</param>
    /// <returns><see langword="true"/> for a single PUA character.</returns>
    public static bool IsValidGlyph(string? glyph) => glyph is { Length: 1 } && glyph[0] is >= '\uE000' and <= '\uF8FF';
}
