using BetterClipboard.Core.Updates;

namespace BetterClipboard.Core.Startup;

/// <summary>
/// Another <c>BetterClipboard.exe</c> process of this sign-in session, as a starting copy sees it from outside.
/// </summary>
/// <param name="ProcessId">Its process id.</param>
/// <param name="ExecutablePath">Full path of its program, or <see langword="null"/> when it could not be read.</param>
/// <param name="Version">
/// The version its program reports (the product version, build metadata dropped), or <see langword="null"/> when it could
/// not be read or is not a version.
/// </param>
/// <param name="Packaged">Whether it runs from an app package (the Microsoft Store edition, under <c>WindowsApps</c>).</param>
public sealed record RunningCopy(int ProcessId, string? ExecutablePath, SemanticVersion? Version, bool Packaged);

/// <summary>
/// When a BetterClipboard that starts while another one runs replaces it instead of handing over to it: when it is a newer
/// version than every other copy in the session (CLAUDE.md §2.29, "A newer copy takes over").
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> A plain start that finds the instance lock taken hands over to the running copy (its Settings come up) and
/// ends. Seen in the portable-update test of 2026-11-13: a user who extracts a new release into a new folder and starts it
/// while the old one runs in the tray gets the <b>old</b> version's Settings, and nothing is updated. A newer copy now
/// asks the running one to exit (<c>--exit</c>, which every version understands, so the old copy needs no new code) and
/// becomes BetterClipboard itself.
/// </para>
/// <para>
/// <b>Conservative on purpose.</b> The starting copy cannot tell from outside which process holds its instance lock (an
/// isolated test instance runs under another lock), so it takes over only when <i>every</i> other BetterClipboard process
/// of the session is strictly older; the exit request itself goes through the instance's own named event, so only the
/// lock's holder receives it. Anything unclear — a version that cannot be read, the same or a newer version, the same
/// program path, a packaged copy (the Store edition has its own rules, CLAUDE.md §2.47) — keeps today's hand-over.
/// </para>
/// <para>Pure: the Windows layer lists the processes; this decides.</para>
/// </remarks>
public static class InstanceTakeover
{
    /// <summary>
    /// Whether a starting copy should ask the running BetterClipboard to exit and take its place.
    /// </summary>
    /// <param name="ownVersion">The starting copy's version.</param>
    /// <param name="ownExecutablePath">Full path of the starting copy's program.</param>
    /// <param name="others">Every other <c>BetterClipboard.exe</c> process of this session.</param>
    /// <returns>
    /// <see langword="true"/> when there is at least one other copy and each one runs a program at another path, reports a
    /// version strictly older than <paramref name="ownVersion"/>, and is not packaged; otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="others"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="ownExecutablePath"/> is null or blank.</exception>
    public static bool ShouldTakeOver(SemanticVersion ownVersion, string ownExecutablePath, IReadOnlyList<RunningCopy> others)
    {
        ArgumentNullException.ThrowIfNull(others);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownExecutablePath);
        if (others.Count == 0)
        {
            // Nobody else runs: the lock was held by a process that ended a moment ago, which the caller's next try handles.
            return false;
        }

        foreach (var other in others)
        {
            // Both paths are full paths as Windows reports them (the process image name, Environment.ProcessPath), so a
            // plain comparison ignoring case is enough; the same program is the same version, never "older".
            if (other.Packaged || other.Version is not { } version || version >= ownVersion || string.IsNullOrWhiteSpace(other.ExecutablePath)
                || string.Equals(other.ExecutablePath.Trim(), ownExecutablePath.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
