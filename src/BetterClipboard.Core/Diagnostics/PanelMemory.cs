using System.Globalization;
using BetterClipboard.Core.Localization;

namespace BetterClipboard.Core.Diagnostics;

/// <summary>
/// Settings › App › Keep the panel in memory (CLAUDE.md §2.69): how long the clipboard panel's memory stays in
/// BetterClipboard's working set after the panel was last used, as the slider offers it (its stops), the rule that says when
/// a memory reclaim also hands the pages to Windows, and the words for each stop. Pure: the caller supplies the clock, owns
/// the timers and does the trimming (<c>AppController.Memory.cs</c>, <c>ProcessMemory.Reclaim</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The trade-off.</b> The panel's window and list stay built while it is hidden (CLAUDE.md §2.27), and the memory they use
/// stays in the working set: Task Manager's "Memory" column (the private working set) reads 40 to 50 MB at rest on a real
/// history, and Win+V shows the panel without waiting. Handing those pages to Windows (a trim) makes the column read a few
/// megabytes; the next Win+V then brings them back, in a few milliseconds while Windows still holds them in RAM (its
/// standby list), or from the page file once Windows gave that RAM to another program: 382 and 538 ms on the owner's PC
/// under 0.2.11, which trimmed after every use. Committed memory (Task Manager's "Commit size") is the same either way: a
/// trim frees nothing, it only moves the pages out of that column (the owner's PC: a median of 145 MB with the trim, 144 MB
/// without).
/// </para>
/// <para>
/// <b>Why a setting</b> (issue #14, 2026-11-24): 0.2.12 stopped trimming (the owner's "Keep the panel ready", 2026-11-15,
/// CLAUDE.md §2.53), and a user saw "Memory" stay at 40 to 50 MB where 0.2.11 dropped to 4 to 6 MB. The owner chose a slider
/// with both ends (2026-11-25): "Never keep in memory" (a keep time of 0: the pages go back at every reclaim after a use,
/// 0.2.11's way), 1 second to 7 days (they go back once the panel went unused that long), and "Never release from memory"
/// (no keep time, <see langword="null"/>, the default: they never go back, 0.2.12's way). Keep times under 5 seconds, and
/// "never keep", trim 5 seconds after the use (<see cref="SettleAfterUse"/>): sooner, the panel's own put-away work brings
/// the pages straight back.
/// </para>
/// <para>
/// <b>Stored as seconds, not as a slider position,</b> so the table of stops may change without changing what a saved value
/// means. A value between two stops (a hand-edited settings file) is moved to the nearest one (<see cref="Normalize"/>), so
/// the slider always shows what applies.
/// </para>
/// </remarks>
public static class PanelMemory
{
    /// <summary>
    /// The slider's stops between its two ends, in seconds and shortest first: 1 second to 7 days, close together where a
    /// difference is felt (seconds, minutes) and wide apart where it is not (hours, days). Each stop is a whole number of its
    /// unit, so <see cref="Describe"/> never needs a fraction.
    /// </summary>
    public static IReadOnlyList<int> StopSeconds { get; } =
    [
        1, 2, 5, 10, 30,
        60, 120, 300, 600, 900, 1_800,
        3_600, 7_200, 14_400, 28_800, 43_200,
        86_400, 172_800, 259_200, 432_000, 604_800,
    ];

    /// <summary>The longest keep time the slider offers, 7 days; a longer stored value is moved down to it.</summary>
    public const int MaxKeepSeconds = 604_800;

    /// <summary>The slider's first position: "Never keep in memory", a keep time of 0.</summary>
    public const int NeverKeepPosition = 0;

    /// <summary>The slider's last position: "Never release from memory", no keep time (the default).</summary>
    public static int NeverReleasePosition => StopSeconds.Count + 1;

    /// <summary>Seconds in a minute, an hour and a day: the units <see cref="Describe"/> words a keep time in.</summary>
    private const int Minute = 60, Hour = 3_600, Day = 86_400;

    /// <summary>
    /// The keep time a slider position stands for. Positions outside the slider are clamped to its ends (a slider cannot
    /// produce them, but a rounded double might).
    /// </summary>
    /// <param name="position">0 (never keep) to <see cref="NeverReleasePosition"/> (never release).</param>
    /// <returns>Seconds (0 for "never keep"), or <see langword="null"/> for "never release".</returns>
    public static int? KeepSecondsAt(int position)
    {
        if (position <= NeverKeepPosition)
        {
            return 0;
        }

        return position >= NeverReleasePosition ? null : StopSeconds[position - 1];
    }

    /// <summary>
    /// The slider position that shows a keep time: its stop, or the nearest stop for a value between two (on a logarithmic
    /// scale, so 45 seconds shows as 1 minute rather than 30 seconds).
    /// </summary>
    /// <param name="keepSeconds">A stored keep time; <see langword="null"/> or negative means "never release".</param>
    /// <returns>0 to <see cref="NeverReleasePosition"/>.</returns>
    public static int PositionOf(int? keepSeconds)
    {
        if (keepSeconds is not { } seconds || seconds < 0)
        {
            return NeverReleasePosition;
        }

        return seconds == 0 ? NeverKeepPosition : NearestStopIndex(seconds) + 1;
    }

    /// <summary>
    /// The keep time a settings file may hold: <see langword="null"/> stays (never release), a negative number becomes
    /// <see langword="null"/> too (the usual hand-written spelling of "forever", -1), 0 stays (never keep), and anything else
    /// becomes its nearest stop, at most <see cref="MaxKeepSeconds"/>.
    /// </summary>
    /// <param name="keepSeconds">What the file says.</param>
    /// <returns>A value <see cref="PositionOf"/> and <see cref="KeepSecondsAt"/> agree on.</returns>
    public static int? Normalize(int? keepSeconds)
    {
        if (keepSeconds is not { } seconds || seconds < 0)
        {
            return null;
        }

        return seconds == 0 ? 0 : StopSeconds[NearestStopIndex(seconds)];
    }

    /// <summary>
    /// Whether a memory reclaim that runs now also hands the panel's pages to Windows: the setting has a keep time, and the
    /// panel has gone unused at least that long. The caller checks first that the panel is not on screen (a reclaim never
    /// runs while it is).
    /// </summary>
    /// <param name="keepSeconds">The setting (see <see cref="Normalize"/>).</param>
    /// <param name="lastUse">When the panel was last put away, or the process started when it was not used yet.</param>
    /// <param name="now">The current time.</param>
    /// <returns>
    /// <see langword="true"/> to trim. With a keep time of 0 always (also when the clock moved back since the last use);
    /// with "never release" never.
    /// </returns>
    public static bool ShouldTrim(int? keepSeconds, DateTimeOffset lastUse, DateTimeOffset now)
    {
        if (keepSeconds is not { } seconds || seconds < 0)
        {
            return false;
        }

        return seconds == 0 || now - lastUse >= TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// When the panel's pages should go back to Windows if nothing uses the panel meanwhile: the last use plus the keep time.
    /// </summary>
    /// <param name="keepSeconds">The setting.</param>
    /// <param name="lastUse">When the panel was last put away (or the process started).</param>
    /// <returns>The moment, or <see langword="null"/> for "never release".</returns>
    public static DateTimeOffset? TrimDueAt(int? keepSeconds, DateTimeOffset lastUse) =>
        keepSeconds is { } seconds && seconds >= 0 ? lastUse + TimeSpan.FromSeconds(seconds) : null;

    /// <summary>
    /// How long the panel goes on touching its memory after it was put away: the view's reset for the next summon, the hidden
    /// window drawn again, a pasted item's list refresh. A trim before this hands back pages that come right back: measured in
    /// a test desktop on the owner's history (2026-11-25), a trim 0.3 s after Esc took the private working set from 25.9 to
    /// 4.2 MB, it was back at 11.8 MB 1.6 s later, and it stayed at 12.1 to 12.5 MB for the next 80 s.
    /// </summary>
    public static readonly TimeSpan SettleAfterUse = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long after a use the reclaim that follows it waits: its usual quiet period (<paramref name="usual"/>), or the keep
    /// time when that is shorter, so keep times of a few seconds hand the pages back that soon instead of after the usual 30
    /// seconds; never less than <see cref="SettleAfterUse"/>, so "Never keep in memory" and keep times under 5 seconds trim
    /// once the panel has finished putting itself away. A longer keep time leaves the usual delay: that reclaim then frees
    /// garbage only, and the trim follows at <see cref="TrimDueAt"/>.
    /// </summary>
    /// <param name="keepSeconds">The setting.</param>
    /// <param name="usual">The reclaim's own quiet period after a use.</param>
    /// <returns>The delay to ask the reclaim for.</returns>
    public static TimeSpan ReclaimDelayAfterUse(int? keepSeconds, TimeSpan usual)
    {
        if (keepSeconds is not { } seconds || seconds < 0 || TimeSpan.FromSeconds(seconds) >= usual)
        {
            return usual;
        }

        var keep = TimeSpan.FromSeconds(seconds);
        return keep > SettleAfterUse ? keep : SettleAfterUse < usual ? SettleAfterUse : usual;
    }

    /// <summary>
    /// The words for a keep time in the interface language, as the slider's label shows them: "Never keep in memory", "For 10
    /// minutes", "Never release from memory".
    /// </summary>
    /// <param name="keepSeconds">The setting (normalized or not: a value between stops is worded as it is).</param>
    /// <returns>The label.</returns>
    public static string Describe(int? keepSeconds)
    {
        if (keepSeconds is not { } seconds || seconds < 0)
        {
            return UiText.Translate("Never release from memory");
        }

        if (seconds == 0)
        {
            return UiText.Translate("Never keep in memory");
        }

        // The largest unit the time is a whole number of: every stop is one (StopSeconds).
        if (seconds % Day == 0)
        {
            long days = seconds / Day;
            return UiText.Plural(days, $"For {days:N0} day", $"For {days:N0} days");
        }

        if (seconds % Hour == 0)
        {
            long hours = seconds / Hour;
            return UiText.Plural(hours, $"For {hours:N0} hour", $"For {hours:N0} hours");
        }

        if (seconds % Minute == 0)
        {
            long minutes = seconds / Minute;
            return UiText.Plural(minutes, $"For {minutes:N0} minute", $"For {minutes:N0} minutes");
        }

        return UiText.Plural(seconds, $"For {seconds:N0} second", $"For {seconds:N0} seconds");
    }

    /// <summary>
    /// The setting in English for the log, which never carries translated text (CLAUDE.md §4): "never released", "never kept",
    /// or the keep time ("10 minutes", "1 second").
    /// </summary>
    /// <param name="keepSeconds">The setting.</param>
    /// <returns>The phrase.</returns>
    public static string DescribeForLog(int? keepSeconds)
    {
        if (keepSeconds is not { } seconds || seconds < 0)
        {
            return "never released";
        }

        if (seconds == 0)
        {
            return "never kept";
        }

        var (count, unit) = seconds % Day == 0 ? (seconds / Day, "day")
            : seconds % Hour == 0 ? (seconds / Hour, "hour")
            : seconds % Minute == 0 ? (seconds / Minute, "minute")
            : (seconds, "second");
        return string.Create(CultureInfo.InvariantCulture, $"{count:N0} {unit}{(count == 1 ? string.Empty : "s")}");
    }

    /// <summary>
    /// The index into <see cref="StopSeconds"/> nearest to a positive number of seconds, by the ratio between them (a
    /// logarithmic scale: the stops themselves grow roughly geometrically); a tie goes to the shorter stop.
    /// </summary>
    /// <param name="seconds">1 or more.</param>
    /// <returns>0 to <c>StopSeconds.Count - 1</c>; the last for anything above <see cref="MaxKeepSeconds"/>.</returns>
    private static int NearestStopIndex(int seconds)
    {
        double target = Math.Log(seconds);
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < StopSeconds.Count; i++)
        {
            double distance = Math.Abs(Math.Log(StopSeconds[i]) - target);
            if (distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }

        return best;
    }
}
