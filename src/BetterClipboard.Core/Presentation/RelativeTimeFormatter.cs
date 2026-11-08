using BetterClipboard.Core.Localization;
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
    /// <param name="culture">
    /// Windows' culture for day/month names and time patterns (defaults to current UI culture). When
    /// <paramref name="localized"/>, it is used only while it belongs to the interface language
    /// (<see cref="UiText.DateCulture"/>); otherwise the interface language's own culture names the days.
    /// </param>
    /// <param name="localized">
    /// Whether the words ("just now", "min ago", "Yesterday"), the day and month names and the date patterns follow the
    /// interface language (<see cref="UiText"/>). The panel and Settings want that; <c>bclip</c> passes
    /// <see langword="false"/>: its output is read by scripts and AI agents and stays English whatever the app's or
    /// Windows' language.
    /// </param>
    /// <returns>The short description.</returns>
    public static string Format(DateTimeOffset timestamp, DateTimeOffset now, TimeZoneInfo? zone = null, CultureInfo? culture = null, bool localized = true)
    {
        zone ??= TimeZoneInfo.Local;
        culture ??= CultureInfo.CurrentUICulture;

        // Day and month names in the interface language: an English Windows with the Chinese interface must not show
        // "Wed 14:02" in a Chinese caption, nor a Chinese Windows "周三" in an English one.
        if (localized)
        {
            culture = UiText.DateCulture(culture);
        }

        var delta = now - timestamp;

        // Future timestamps (clock skew between machines/imports) read as "just now" rather than "-3 min".
        if (delta < TimeSpan.FromSeconds(45))
        {
            return localized ? UiText.Translate("just now") : "just now";
        }

        if (delta < TimeSpan.FromMinutes(60))
        {
            return Phrase($"{Math.Max(1, (int)Math.Round(delta.TotalMinutes))} min ago");
        }

        var local = TimeZoneInfo.ConvertTime(timestamp, zone);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var time = local.ToString(culture.DateTimeFormat.ShortTimePattern, culture);
        if (local.Date == localNow.Date)
        {
            return Phrase($"{(int)delta.TotalHours} h ago");
        }

        if (local.Date == localNow.Date.AddDays(-1))
        {
            return Phrase($"Yesterday {time}");
        }

        if (localNow.Date - local.Date < TimeSpan.FromDays(7))
        {
            return $"{local.ToString("ddd", culture)} {time}";
        }

        // The patterns are catalog entries too: "12 Mar" is English word order, a Chinese date is "3月12日". A translator
        // writes a .NET custom date pattern there; English keeps these exact patterns.
        return Date(local.Year == localNow.Year ? "d MMM" : "d MMM yyyy");

        // The English wording is also the catalog's key: translated for the UI, kept as it is for bclip. Both format the
        // numbers with the current culture, as the interpolation did before translation existed.
        string Phrase(FormattableString english) =>
            localized ? UiText.Format(english) : english.ToString(CultureInfo.CurrentCulture);

        // A catalog's pattern that .NET refuses (a lone "%", say: a translator's slip the tests catch) must not take the
        // whole caption down: the English pattern stands in.
        string Date(string english)
        {
            if (localized)
            {
                try
                {
                    return local.ToString(UiText.Translate(english), culture);
                }
                catch (FormatException)
                {
                }
            }

            return local.ToString(english, culture);
        }
    }
}
