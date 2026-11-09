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
            ClipKind.Files => item.FileCount is { } files ? UiText.Plural(culture, files, $"{files:N0} file", $"{files:N0} files") : UiText.Translate("Files"),
            ClipKind.Image => item.ImageWidth is > 0 && item.ImageHeight is > 0
                ? UiText.Format(culture, $"Image · {item.ImageWidth} × {item.ImageHeight}")
                : UiText.Translate("Image"),

            // Rich text without plain text (HTML/RTF only) has no characters to count.
            ClipKind.RichText when item.TextLength is null => UiText.Translate("Formatted content"),
            _ => item.TextLength is { } length
                ? $"{KindName(item.Kind)} · {UiText.Plural(culture, length, $"{length:N0} character", $"{length:N0} characters")}"
                : KindName(item.Kind),
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
            // Stored in English; a name BetterClipboard made up ("Command Prompt") is shown in the interface language.
            parts.Add(UiText.Format($"from {SourceNames.Display(item.SourceAppName)}"));
        }

        parts.Add(UiText.Format($"forgotten {RelativeTimeFormatter.Format(item.ForgottenUtc, now, zone, culture)}"));
        parts.Add(item.BlockedCount == 0 || item.LastBlockedUtc is not { } last
            ? UiText.Translate("not copied since")
            : KeptOut(item.BlockedCount, RelativeTimeFormatter.Format(last, now, zone, culture), culture));

        // One line of pieces: in a right-to-left language it is ordered right to left as a whole. Its first character is
        // then the embedding's invisible mark, which has no upper case, and right-to-left scripts have no case either.
        var text = UiText.JoinLine(" · ", parts);
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

    /// <summary>
    /// "kept out 3 times, last just now": one whole phrase per plural form, because a language words the count, the
    /// noun and the clause around them together (a noun translated alone once showed "3 times" inside Chinese text).
    /// </summary>
    /// <param name="count">How often the copy was kept out.</param>
    /// <param name="last">When it was last kept out, already worded.</param>
    /// <param name="culture">Number formatting.</param>
    /// <returns>The phrase.</returns>
    private static string KeptOut(long count, string last, CultureInfo culture) =>
        UiText.Plural(culture, count, $"kept out {count:N0} time, last {last}", $"kept out {count:N0} times, last {last}");
}
