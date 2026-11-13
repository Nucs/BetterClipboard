using System.Globalization;
using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.Presentation;

/// <summary>
/// The words for a picture's source file (CLAUDE.md §2.49): the dimmed line the card menu shows above the file's actions, and
/// the facts and dates of the image viewer's two rows.
/// </summary>
/// <remarks>
/// <para>
/// Pure and clock-injected so it is unit-testable. Every piece goes through <see cref="UiText"/>; lines of several pieces are
/// joined with <see cref="UiText.JoinLine(string, IEnumerable{string})"/>, so a right-to-left interface reads them from the
/// right. A file's name, its folder, its format ("PNG") and the size's units are never translated — they are what Explorer
/// shows for the same file.
/// </para>
/// <para>
/// The menu tells the modified time like the card captions do ("5 min ago"); the viewer, with room to spare, tells exact
/// times in the date culture's short date and time (<see cref="UiText.DateCulture"/>), and leaves out the creation time when
/// it is within a minute of the last write — a screenshot is written once, and the two would only repeat each other.
/// </para>
/// </remarks>
public static class SourceFileText
{
    /// <summary>How close a file's creation and last write must be for the viewer to show only the latter.</summary>
    private static readonly TimeSpan SameMoment = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The card menu's dimmed two-line label above the file's actions: the file's name, then its format, size and when it was
    /// last modified — or, when the file is no longer on disk, that it was moved or deleted (the reason its actions are not
    /// offered).
    /// </summary>
    /// <param name="file">The picture's file.</param>
    /// <param name="onDisk">Whether the file is (or may be) still there: <see langword="false"/> only when it is known to be gone.</param>
    /// <param name="nowUtc">Now, for the relative time.</param>
    /// <returns>"name\nfacts" (two lines).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is <see langword="null"/>.</exception>
    public static string MenuLabel(SourceFile file, bool onDisk, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!onDisk)
        {
            return file.Name + "\n" + UiText.Translate("moved or deleted");
        }

        var parts = Facts(file, width: null, height: null).ToList();
        if (file.ModifiedUtc is { } modified)
        {
            parts.Add(UiText.Format($"modified {RelativeTimeFormatter.Format(modified, nowUtc)}"));
        }

        return parts.Count == 0 ? file.Name : file.Name + "\n" + UiText.JoinLine(" · ", parts);
    }

    /// <summary>
    /// The facts the viewer's first row puts after the file's name (and the menu's line starts with): its format, its size,
    /// and the picture's pixel size when given. Unknown facts are left out.
    /// </summary>
    /// <param name="file">The picture's file.</param>
    /// <param name="width">The picture's width in pixels, or <see langword="null"/> to leave the pixel size out.</param>
    /// <param name="height">The picture's height in pixels, or <see langword="null"/>.</param>
    /// <returns>The pieces in reading order ("PNG", "1.2 MB", "1920 × 1080"); possibly none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> Facts(SourceFile file, int? width, int? height)
    {
        ArgumentNullException.ThrowIfNull(file);
        var parts = new List<string>(3);

        // The bytes' format when it was recognized; else the extension, as Explorer would call the file.
        var format = file.Format ?? (file.Extension.Length > 1 ? file.Extension[1..].ToUpperInvariant() : null);
        if (!string.IsNullOrEmpty(format))
        {
            parts.Add(format);
        }

        if (file.SizeBytes is { } size)
        {
            parts.Add(ByteSizeText.Format(size));
        }

        if (width is > 0 && height is > 0)
        {
            parts.Add($"{width} × {height}");
        }

        return parts;
    }

    /// <summary>
    /// The dates the viewer's second row puts after the file's folder: when the file was created (only when that is more than
    /// a minute from its last write) and when it was last modified, in the date culture's short date and time.
    /// </summary>
    /// <param name="file">The picture's file.</param>
    /// <param name="zone">The time zone to tell the times in (the user's, <see cref="TimeZoneInfo.Local"/>).</param>
    /// <param name="culture">Windows' formatting culture; the interface language decides through <see cref="UiText.DateCulture"/>.</param>
    /// <returns>The pieces in reading order ("created …", "modified …"); none when the file system reported no times.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> Dates(SourceFile file, TimeZoneInfo zone, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(culture);
        var dates = UiText.DateCulture(culture);
        var parts = new List<string>(2);
        if (file.CreatedUtc is { } created && (file.ModifiedUtc is not { } written || (written - created).Duration() > SameMoment))
        {
            parts.Add(UiText.Format($"created {Exact(created)}"));
        }

        if (file.ModifiedUtc is { } modified)
        {
            parts.Add(UiText.Format($"modified {Exact(modified)}"));
        }

        return parts;

        // The culture's own short date and time ("13/11/2026 12:05" in en-GB, "2026/11/13 12:05" in zh-CN): no new
        // pattern for translators to keep right.
        string Exact(DateTimeOffset moment) => TimeZoneInfo.ConvertTime(moment, zone).ToString("g", dates);
    }
}
