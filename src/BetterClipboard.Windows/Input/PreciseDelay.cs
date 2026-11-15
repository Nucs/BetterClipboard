using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Input;

/// <summary>
/// Waits that end on time: a high-resolution waitable timer instead of <c>Task.Delay</c>, which rounds every wait up to the
/// next tick of the system timer (15.6 ms by default) — a "40 ms" wait took 40 to 56 ms in the paste measurements
/// (CLAUDE.md §2.52), and that difference sits right between the user's Enter and the paste.
/// </summary>
internal static class PreciseDelay
{
    /// <summary>
    /// Waits <paramref name="delay"/> on a thread-pool thread (which it blocks for that long), so the caller's own thread —
    /// the UI thread included — stays free.
    /// </summary>
    /// <param name="delay">How long; zero or negative completes at once.</param>
    /// <param name="cancellationToken">Cancels a wait that has not started yet (a running one ends on time anyway).</param>
    /// <returns>A task completing after the delay.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled before the wait started.</exception>
    public static Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Run(() => Wait(delay), cancellationToken);

    /// <summary>
    /// Blocks the calling thread for <paramref name="delay"/>, to within about a millisecond where Windows has
    /// high-resolution timers (10 1803 and later), and like <c>Thread.Sleep</c> where it has not.
    /// </summary>
    /// <param name="delay">How long; zero or negative returns at once.</param>
    public static void Wait(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        // An unnamed timer for this one wait: creating one costs microseconds, and nothing can be left armed afterwards.
        nint timer = CreateWaitableTimerEx(0, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_SET_AND_WAIT);
        if (timer == 0)
        {
            Thread.Sleep(delay);
            return;
        }

        try
        {
            // Negative = relative to now, in 100-ns units: exactly what a TimeSpan's ticks count.
            long due = -delay.Ticks;
            uint limit = (uint)Math.Min(int.MaxValue, Math.Ceiling(delay.TotalMilliseconds) + 1000);
            if (!SetWaitableTimerEx(timer, in due, 0, 0, 0, 0, 0) || WaitForSingleObject(timer, limit) != WAIT_OBJECT_0)
            {
                // Refused or failed: the plain sleep still keeps the promised minimum.
                Thread.Sleep(delay);
            }
        }
        finally
        {
            CloseHandle(timer);
        }
    }
}
