namespace BetterClipboard.Core.Updates;

/// <summary>
/// When the panel reminds the user of an update that waits: a tooltip that opens by itself at the update button, at most
/// once in <see cref="Interval"/>. This class is the rule; showing the tooltip is the panel's job.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a reminder at all.</b> The update button lights up when a newer release exists, and stays lit. A light that is
/// always on stops being seen, so once a week the button also says it in words, for a few seconds, without anything to
/// click away.
/// </para>
/// <para>
/// <b>No switch of its own, on purpose.</b> The reminder follows what the user already decided:
/// </para>
/// <list type="bullet">
/// <item>
/// "Check for updates automatically" off (<see cref="UpdateSnapshot.AutomaticChecks"/>): no reminder. Whoever told the
/// app not to look for updates by itself is not told about them by itself either, and the same holds for copies that
/// never check (a Chocolatey copy, an isolated test instance).
/// </item>
/// <item>"Skip this version" (<see cref="UpdateSnapshot.IsSkipped"/>): no reminder for that version; a newer one reminds again.</item>
/// <item>An approved update that is on its way (<see cref="UpdateSnapshot.IsInstalling"/>): nothing left to remind of.</item>
/// </list>
/// <para>
/// <b>One clock for everything.</b> The time of the last reminder is one value (<c>AppSettings.UpdateReminderShownUtc</c>),
/// not one per version: a release that arrives two days after a reminder waits for the rest of the week. Opening the
/// update dialog while an update is offered sets the same clock, so "Later" means "remind me in a week".
/// </para>
/// <para>Pure and clock-injected, so every rule here is unit-tested.</para>
/// </remarks>
public static class UpdateReminder
{
    /// <summary>The shortest time between two reminders.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(7);

    /// <summary>
    /// How far ahead of "now" a remembered reminder time may lie before it is taken for a mistake (see
    /// <see cref="IsDue"/>). A day is more than any time-zone or daylight-saving confusion and any clock correction, and
    /// far less than <see cref="Interval"/>.
    /// </summary>
    public static readonly TimeSpan ClockSlack = TimeSpan.FromDays(1);

    /// <summary>
    /// The free height over the panel's window, in DIPs, that the reminder needs to open there: a one-line tooltip
    /// (about 32 DIPs, more when Windows' text size is raised) and the gap between it and the window. With less room the
    /// panel puts the reminder beside the button, inside its header, instead of letting it cover the search box.
    /// </summary>
    public const double RoomAboveDip = 56;

    /// <summary>
    /// Whether this state calls for reminders at all, whatever the clock says: an update is offered that the user did not
    /// skip, nothing is being installed, and this copy checks for updates by itself.
    /// </summary>
    /// <param name="snapshot">The update state, or <see langword="null"/> when the update system did not start.</param>
    /// <returns><see langword="true"/> when a reminder may be shown once it is due.</returns>
    public static bool IsWanted(UpdateSnapshot? snapshot) =>
        snapshot is { AutomaticChecks: true, IsUpdateAvailable: true, IsSkipped: false, IsInstalling: false };

    /// <summary>
    /// Whether the reminder should be shown now: the state calls for one (<see cref="IsWanted"/>) and the last one is at
    /// least <see cref="Interval"/> old, or there never was one.
    /// </summary>
    /// <remarks>
    /// A remembered time that lies more than <see cref="ClockSlack"/> in the future cannot be right (the clock was wrong
    /// when it was written, or the file was edited). Believing it would postpone the reminder until that time plus a week,
    /// possibly for years, so it counts as "never": the reminder shows, and showing it writes the correct time. A time
    /// only a little ahead (a clock that was set back by minutes) is believed, so a correction right after a reminder
    /// does not show it twice.
    /// </remarks>
    /// <param name="snapshot">The update state, or <see langword="null"/> when the update system did not start.</param>
    /// <param name="lastShown">When the user was last reminded (<c>AppSettings.UpdateReminderShownUtc</c>); <see langword="null"/> for never.</param>
    /// <param name="now">The current time.</param>
    /// <returns><see langword="true"/> when the panel should show the reminder.</returns>
    public static bool IsDue(UpdateSnapshot? snapshot, DateTimeOffset? lastShown, DateTimeOffset now)
    {
        if (!IsWanted(snapshot))
        {
            return false;
        }

        if (lastShown is not { } last)
        {
            return true;
        }

        var since = now - last;
        return since >= Interval || since < -ClockSlack;
    }

    /// <summary>
    /// Whether the reminder fits over the panel's window, where it covers nothing of the panel.
    /// </summary>
    /// <param name="roomAboveDip">The height between the top of the monitor's work area and the top of the panel's window, in DIPs.</param>
    /// <returns><see langword="true"/> for over the window; <see langword="false"/> for beside the button (not a number counts as no room).</returns>
    public static bool FitsAbove(double roomAboveDip) => roomAboveDip >= RoomAboveDip;
}
