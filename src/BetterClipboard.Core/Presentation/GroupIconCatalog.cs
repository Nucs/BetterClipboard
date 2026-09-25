using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.Presentation;

/// <summary>One choice in the group icon picker: a Segoe Fluent Icons glyph and the name it suggests.</summary>
/// <param name="Glyph">The icon character (Private Use Area; see <see cref="ClipGroup.IsValidGlyph"/>).</param>
/// <param name="Name">Friendly name, used as the group's name when the user types none.</param>
public sealed record GroupIcon(string Glyph, string Name);

/// <summary>
/// The icons offered for groups, in picker order: a curated set of Segoe Fluent Icons glyphs whose
/// shapes were checked by rendering the font (2026-09-25, Windows 11 25H2 <c>SegoeIcons.ttf</c>).
/// </summary>
/// <remarks>
/// <para>
/// Curated rather than the whole font: the font has ~1,400 glyphs, most of them UI chrome (battery levels,
/// signal bars, arrows, keyboard layouts) that make no sense as a collection icon and bury the useful ones.
/// </para>
/// <para>
/// Code points are escapes, never raw characters: Private Use Area characters are invisible in editors and
/// diffs. Windows 10's Segoe MDL2 Assets (the fallback of <c>SymbolThemeFontFamily</c>) shares these code
/// points for the classic glyphs, so a group's icon survives on older systems.
/// </para>
/// </remarks>
public static class GroupIconCatalog
{
    /// <summary>Icon for a group created without choosing (e.g. by a script); also the first picker entry.</summary>
    public static GroupIcon Default => All[0];

    /// <summary>All icons in picker order (unique glyphs, unique names).</summary>
    public static IReadOnlyList<GroupIcon> All { get; } =
    [
        new("\uE734", "Star"),
        new("\uEB51", "Heart"),
        new("\uE7C1", "Flag"),
        new("\uE8EC", "Tag"),
        new("\uE718", "Pin"),
        new("\uE821", "Work"),
        new("\uE80F", "Home"),
        new("\uE943", "Code"),
        new("\uE715", "Mail"),
        new("\uE71B", "Links"),
        new("\uE82F", "Ideas"),
        new("\uE722", "Camera"),
        new("\uE91B", "Photos"),
        new("\uE714", "Video"),
        new("\uEC4F", "Music"),
        new("\uE7FC", "Games"),
        new("\uE7BF", "Shopping"),
        new("\uE8C7", "Payments"),
        new("\uE787", "Calendar"),
        new("\uE917", "Later"),
        new("\uE8A5", "Documents"),
        new("\uE8B7", "Folder"),
        new("\uE774", "Web"),
        new("\uE717", "Phone"),
        new("\uE716", "People"),
        new("\uE77B", "Person"),
        new("\uE72E", "Private"),
        new("\uE8D7", "Keys"),
        new("\uEA18", "Security"),
        new("\uE713", "Settings"),
        new("\uE90F", "Tools"),
        new("\uE709", "Travel"),
        new("\uE804", "Car"),
        new("\uE76E", "Fun"),
        new("\uE8F1", "Library"),
        new("\uE7BE", "Learning"),
        new("\uE790", "Design"),
        new("\uE771", "Art"),
        new("\uE8BD", "Messages"),
        new("\uE8F2", "Chat"),
        new("\uE789", "Announcements"),
        new("\uE7BA", "Warning"),
        new("\uE8C9", "Important"),
        new("\uE946", "Info"),
        new("\uE73E", "Done"),
        new("\uE945", "Quick"),
        new("\uE70F", "Drafts"),
        new("\uE706", "Day"),
        new("\uE708", "Night"),
        new("\uE753", "Cloud"),
        new("\uE8EF", "Numbers"),
        new("\uE8E1", "Liked"),
        new("\uE81D", "Places"),
        new("\uE7B8", "Archive"),
        new("\uEA86", "Puzzles"),
        new("\uE95E", "Health"),
        new("\uEB05", "Charts"),
        new("\uE9F9", "Reports"),
        new("\uE99A", "Bots"),
        new("\uE9CE", "Questions"),
        new("\uECAD", "Hot"),
        new("\uE7F8", "Laptop"),
        new("\uE7F4", "Screen"),
        new("\uE896", "Downloads"),
    ];

    /// <summary>
    /// The catalog name of a glyph, e.g. to prefill the name box when the user picks an icon.
    /// </summary>
    /// <param name="glyph">An icon character.</param>
    /// <returns>The name, or <see langword="null"/> for a glyph that is not in the catalog.</returns>
    public static string? NameOf(string? glyph) => All.FirstOrDefault(icon => icon.Glyph == glyph)?.Name;
}
