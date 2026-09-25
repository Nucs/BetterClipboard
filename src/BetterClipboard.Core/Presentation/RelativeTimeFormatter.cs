using System.Globalization;

namespace BetterClipboard.Core.Presentation;

/// <summary>
/// Formats timestamps the way people talk about recent copies: "just now", "5 min ago", "3 h ago",
/// "Yesterday 14:02", "Mon 09:15", "12 Mar", "12 Mar 2025".
/// </summary>
/// <remarks>
/// Pure and clock-injected so it is unit-testable; the UI re-formats on every flyout show, so strings
/// never go stale while the app runs for days.
/// </remarks>
public static class RelativeTimeFormatter
{
    /// <summary>
    /// Formats <paramref name="timestamp"/> relative to <paramref name="now"/>.
    /// </summary>
    /// <param name="timestamp">The moment to describe.</param>
    /// <param name="now">The reference "now".</param>
    /// <param name="zone">Time zone for calendar-day decisions (defaults to local).</param>
    /// <param name="culture">Culture for day/month names and time patterns (defaults to current UI culture).</param>
    /// <returns>The short description.</returns>
    public static string Format(DateTimeOffset timestamp, DateTimeOffset now, TimeZoneInfo? zone = null, CultureInfo? culture = null)
    {
        zone ??= TimeZoneInfo.Local;
        culture ??= CultureInfo.CurrentUICulture;
        var delta = now - timestamp;

        // Future timestamps (clock skew between machines/imports) read as "just now" rather than "-3 min".
        if (delta < TimeSpan.FromSeconds(45))
        {
            return "just now";
        }

        if (delta < TimeSpan.FromMinutes(60))
        {
            return $"{Math.Max(1, (int)Math.Round(delta.TotalMinutes))} min ago";
        }

        var local = TimeZoneInfo.ConvertTime(timestamp, zone);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var time = local.ToString(culture.DateTimeFormat.ShortTimePattern, culture);
        if (local.Date == localNow.Date)
        {
            return $"{(int)delta.TotalHours} h ago";
        }

        if (local.Date == localNow.Date.AddDays(-1))
        {
            return $"Yesterday {time}";
        }

        if (localNow.Date - local.Date < TimeSpan.FromDays(7))
        {
            return $"{local.ToString("ddd", culture)} {time}";
        }

        return local.Year == localNow.Year
            ? local.ToString("d MMM", culture)
            : local.ToString("d MMM yyyy", culture);
    }
}
