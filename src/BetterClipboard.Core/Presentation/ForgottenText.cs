using BetterClipboard.Core.Localization;
using System.Globalization;
using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.Presentation;

/// <summary>
/// Describes "Forget forever" entries for the Settings list: what the item was and what happened since,
/// from the content-free facts the store keeps (the content itself is gone by design).
/// </summary>
/// <remarks>
/// Pure and clock-injected like <see cref="RelativeTimeFormatter"/>, so the wording is unit-tested and the
/// list never shows a stale "5 min ago" after a refresh.
/// </remarks>
public static class ForgottenText
{
    /// <summary>
    /// The entry's first line: its kind and size, e.g. "Text · 23 characters", "Link · 41 characters",
    /// "3 files", "Image · 1920 × 1080", "Formatted content".
    /// </summary>
    /// <param name="item">The entry.</param>
    /// <param name="culture">Number formatting (defaults to the current UI culture).</param>
    /// <returns>The title.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is <see langword="null"/>.</exception>
    public static string Title(ForgottenItem item, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        culture ??= CultureInfo.CurrentUICulture;
        return item.Kind switch
        {
            ClipKind.Files => item.FileCount is { } files ? Count(files, UiText.Translate("file"), UiText.Translate("files"), culture) : UiText.Translate("Files"),
            ClipKind.Image => item.ImageWidth is > 0 && item.ImageHeight is > 0
                ? UiText.Format(culture, $"Image · {item.ImageWidth} × {item.ImageHeight}")
                : UiText.Translate("Image"),

            // Rich text without plain text (HTML/RTF only) has no characters to count.
            ClipKind.RichText when item.TextLength is null => UiText.Translate("Formatted content"),
            _ => item.TextLength is { } length ? $"{KindName(item.Kind)} · {Count(length, "character", "characters", culture)}" : KindName(item.Kind),
        };
    }

    /// <summary>
    /// The entry's second line: where it came from, when it was forgotten and how often it was kept out
    /// since, e.g. "From Chrome · forgotten 5 min ago · kept out 3 times, last just now".
    /// </summary>
    /// <param name="item">The entry.</param>
    /// <param name="now">The reference "now" for relative times.</param>
    /// <param name="zone">Time zone for calendar-day wording (defaults to local).</param>
    /// <param name="culture">Culture for numbers and dates (defaults to the current UI culture).</param>
    /// <returns>The details.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is <see langword="null"/>.</exception>
    public static string Details(ForgottenItem item, DateTimeOffset now, TimeZoneInfo? zone = null, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        culture ??= CultureInfo.CurrentUICulture;
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(item.SourceAppName))
        {
            parts.Add(UiText.Format($"from {item.SourceAppName}"));
        }

        parts.Add(UiText.Format($"forgotten {RelativeTimeFormatter.Format(item.ForgottenUtc, now, zone, culture)}"));
        parts.Add(item.BlockedCount == 0 || item.LastBlockedUtc is not { } last
            ? UiText.Translate("not copied since")
            : UiText.Format($"kept out {Count(item.BlockedCount, "time", "times", culture)}, last {RelativeTimeFormatter.Format(last, now, zone, culture)}"));

        var text = string.Join(" · ", parts);
        return char.ToUpper(text[0], culture) + text[1..];
    }

    /// <summary>A kind's display name as a noun ("Text", "Link"…).</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The name.</returns>
    private static string KindName(ClipKind kind) => kind switch
    {
        ClipKind.RichText => UiText.Translate("Formatted text"),
        ClipKind.Link => UiText.Translate("Link"),
        ClipKind.Color => UiText.Translate("Color"),
        ClipKind.Image => UiText.Translate("Image"),
        ClipKind.Files => UiText.Translate("Files"),
        _ => UiText.Translate("Text"),
    };

    /// <summary>"1 file" / "3 files" with grouped digits ("1,234 characters").</summary>
    /// <param name="count">The count.</param>
    /// <param name="singular">Noun for exactly one.</param>
    /// <param name="plural">Noun otherwise (zero included).</param>
    /// <param name="culture">Number formatting.</param>
    /// <returns>The phrase.</returns>
    private static string Count(long count, string singular, string plural, CultureInfo culture) =>
        UiText.Format(culture, $"{count:N0} {(count == 1 ? singular : plural)}");
}
