using System.Globalization;

namespace BetterClipboard.Core.Diagnostics;

/// <summary>
/// How much memory the process holds, as the numbers Windows shows for it (CLAUDE.md §2.45). Content-free: fit for the log.
/// </summary>
/// <param name="PrivateBytes">
/// Committed private memory (Task Manager's "Commit size"): everything the process allocated for itself, in RAM or paged out.
/// </param>
/// <param name="WorkingSetBytes">Physical memory in use, shared pages (DLLs, fonts) included.</param>
/// <param name="PrivateWorkingSetBytes">
/// Physical memory only this process uses (Task Manager's "Memory" column), or <see langword="null"/> where Windows does not
/// report it (before Windows 10 21H2, where the counters lack the field).
/// </param>
/// <param name="ManagedHeapBytes">
/// What the .NET garbage collector had committed for its heap (live objects and free space) at its last collection — after
/// a reclaim, now; before the process's first collection, the heap's current size instead.
/// </param>
public sealed record MemoryUsage(long PrivateBytes, long WorkingSetBytes, long? PrivateWorkingSetBytes, long ManagedHeapBytes)
{
    /// <summary>
    /// The numbers in one line: "private 182 MB, memory 131 MB (working set 240 MB), .NET heap 12 MB" — whole megabytes, and
    /// one decimal below 10 MB ("memory 0.3 MB" right after a trim, where whole megabytes would read as nothing at all).
    /// </summary>
    /// <returns>The line (English, like every log line).</returns>
    public string Describe()
    {
        string memory = PrivateWorkingSetBytes is { } privateWorkingSet
            ? string.Create(CultureInfo.InvariantCulture, $"memory {Megabytes(privateWorkingSet)} MB (working set {Megabytes(WorkingSetBytes)} MB)")
            : string.Create(CultureInfo.InvariantCulture, $"working set {Megabytes(WorkingSetBytes)} MB");
        return string.Create(CultureInfo.InvariantCulture, $"private {Megabytes(PrivateBytes)} MB, {memory}, .NET heap {Megabytes(ManagedHeapBytes)} MB");
    }

    /// <summary>
    /// What a reclaim gave back, for the log: "gave back 52 MB of private memory (182 → 130 MB)". A reclaim that gave back
    /// nothing (or that ran while something else allocated) says the numbers as they are, never a negative release.
    /// </summary>
    /// <param name="before">Measured before the reclaim.</param>
    /// <param name="after">Measured after it.</param>
    /// <returns>The phrase.</returns>
    /// <exception cref="ArgumentNullException">Either measurement is <see langword="null"/>.</exception>
    public static string DescribeRelease(MemoryUsage before, MemoryUsage after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        long released = before.PrivateBytes - after.PrivateBytes;
        return released > 0
            ? string.Create(CultureInfo.InvariantCulture, $"gave back {Megabytes(released)} MB of private memory ({Megabytes(before.PrivateBytes)} → {Megabytes(after.PrivateBytes)} MB)")
            : string.Create(CultureInfo.InvariantCulture, $"private memory {Megabytes(before.PrivateBytes)} → {Megabytes(after.PrivateBytes)} MB");
    }

    /// <summary>
    /// Megabytes for a log line: whole ones, rounded to the nearest, from 10 MB on; one decimal below that, where a whole
    /// number hides the difference that matters (a trimmed working set of 0.3 MB is not "0 MB").
    /// </summary>
    /// <param name="bytes">A byte count.</param>
    /// <returns>The number, without its unit, formatted for the invariant culture.</returns>
    private static string Megabytes(long bytes)
    {
        double megabytes = bytes / 1048576.0;
        return Math.Abs(megabytes) < 9.95
            ? Math.Round(megabytes, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture)
            : Math.Round(megabytes, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// When to give memory back (CLAUDE.md §2.45): after something that leaves garbage behind — Settings closed, the panel put
/// away, the start's imports done — once that thing has been quiet for a while, and never while the panel or Settings is on
/// screen. Pure: the caller supplies the clock, owns the timer, and does the reclaiming.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is needed at all.</b> Most of what BetterClipboard's windows hold is native memory owned by WinUI (XAML elements,
/// decoded images, text layouts), freed only when the .NET objects that refer to it are collected. A tray app allocates
/// little while it waits, so the garbage collector may not run for hours, and that native memory stays in use all the
/// while — Windows counts it, the user sees it in Task Manager.
/// </para>
/// <para>
/// <b>One due time per reason.</b> Each reason waits for its own quiet period: a second request for the same reason
/// postpones it (a burst of summons ends before anything is collected), but never past <see cref="MaxWait"/> after the
/// first request of the burst, so a burst that never ends is still reclaimed. Reasons do not wait for each other: Settings
/// closed ten seconds after the start is reclaimed three seconds later, not when the start's longer wait ends (which then
/// runs on its own). A reclaim that falls due while the panel or Settings is on screen waits until it is put away: the
/// collection pauses every thread for a few milliseconds, and the window must answer every key at once.
/// </para>
/// </remarks>
public sealed class MemoryReclaimPlanner
{
    /// <summary>The longest a reason waits for its activity to end, counted from the first request of its burst.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(2);

    /// <summary>Pending reasons in the order they were first asked for: when their burst began, and when they are due.</summary>
    private readonly List<(string Reason, DateTimeOffset First, DateTimeOffset Due)> pending = [];

    /// <summary>When the earliest pending reason is due, or <see langword="null"/> when nothing is pending.</summary>
    public DateTimeOffset? DueAt => pending.Count == 0 ? null : pending.Min(entry => entry.Due);

    /// <summary>The pending reasons, in the order they were first asked for (empty when nothing is pending).</summary>
    public IReadOnlyList<string> Reasons => pending.Select(entry => entry.Reason).ToArray();

    /// <summary>
    /// Asks for a reclaim once <paramref name="reason"/> has been quiet for <paramref name="delay"/>: due
    /// <paramref name="delay"/> after <paramref name="now"/>, or later when that reason already waits for later — but never
    /// later than <see cref="MaxWait"/> after the first request of its burst.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="delay">How long the reason should stay quiet first (zero or more).</param>
    /// <param name="reason">Why, for the log ("Settings closed"); content-free. Requests with the same reason are one burst.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delay"/> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is null, empty or white space.</exception>
    public void Request(DateTimeOffset now, TimeSpan delay, string reason)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var wanted = now + delay;
        int index = pending.FindIndex(entry => string.Equals(entry.Reason, reason, StringComparison.Ordinal));
        if (index < 0)
        {
            pending.Add((reason, now, wanted));
            return;
        }

        // The longer quiet period asked for wins, within the burst's bound; a bound already passed makes it due now.
        var (_, first, due) = pending[index];
        var latest = first + MaxWait;
        var postponed = due > wanted ? due : wanted;
        pending[index] = (reason, first, postponed <= latest ? postponed : (latest > now ? latest : now));
    }

    /// <summary>
    /// Takes every reason that is due when nothing is <paramref name="busy"/>: the caller then reclaims once for all of them,
    /// and those reasons are no longer pending (the others keep waiting for their own time).
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="busy">Whether the panel or Settings is on screen: nothing is taken then.</param>
    /// <param name="taken">The reasons of the reclaim to run now, in the order they were first asked for; empty when nothing runs.</param>
    /// <returns><see langword="true"/> when the caller should reclaim now.</returns>
    public bool TryTake(DateTimeOffset now, bool busy, out IReadOnlyList<string> taken)
    {
        if (busy || pending.Count == 0 || DueAt > now)
        {
            taken = [];
            return false;
        }

        taken = pending.Where(entry => entry.Due <= now).Select(entry => entry.Reason).ToArray();
        pending.RemoveAll(entry => entry.Due <= now);
        return true;
    }
}
