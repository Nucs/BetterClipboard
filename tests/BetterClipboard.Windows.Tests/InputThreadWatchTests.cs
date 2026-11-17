using System.Diagnostics;
using BetterClipboard.Windows.Input;
using BetterClipboard.Windows.Interop;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// The waits on a window's input thread that keep a paste from typing "v" (CLAUDE.md §2.54): when the keys go in
/// (<see cref="InputThreadWatch.WaitUntilReady(nint, TimeSpan, CancellationToken)"/>) and when Ctrl comes up
/// (<see cref="InputThreadWatch.WaitUntilTaken(nint, CancellationToken)"/>). Their rules run over a fake window and a fake
/// clock; the real ping runs against a message window whose thread a test keeps busy.
/// </summary>
public sealed class InputThreadWatchTests
{
    /// <summary>
    /// A quick window gets its keys when the settle time ends, after one ping: the paste costs it a round trip, not a series
    /// of pings.
    /// </summary>
    [Fact]
    public void Ready_AQuickWindow_GetsItsKeysWhenTheSettleTimeEnds()
    {
        var window = new FakeWindow();
        var wait = window.WaitUntilReady(settleLeft: TimeSpan.FromMilliseconds(40));

        Assert.True(wait.Done);
        Assert.InRange(wait.Waited.TotalMilliseconds, 40, 41);

        // Not pinged during the settle time: a window answers while it is still restoring its focus.
        Assert.Equal(1, wait.Pings);
    }

    /// <summary>With the settle time over already, one answer is all it takes.</summary>
    [Fact]
    public void Ready_SettleTimeOver_OneAnswerSuffices()
    {
        var window = new FakeWindow();
        var wait = window.WaitUntilReady(settleLeft: TimeSpan.FromMilliseconds(-10));

        Assert.True(wait.Done);
        Assert.Equal(1, wait.Pings);
        Assert.True(wait.Waited < TimeSpan.FromMilliseconds(1), $"Ready after {wait.Waited.TotalMilliseconds} ms");
    }

    /// <summary>
    /// A window busy past the settle time (a long message, an overloaded PC) gets its keys once it answers: the ping waits
    /// for it, so no key is pressed while it is frozen.
    /// </summary>
    [Fact]
    public void Ready_ABusyWindow_GetsItsKeysOnceItAnswers()
    {
        var window = new FakeWindow { BusyUntil = TimeSpan.FromMilliseconds(300) };
        var wait = window.WaitUntilReady(settleLeft: TimeSpan.FromMilliseconds(40));

        Assert.True(wait.Done);
        Assert.InRange(wait.Waited.TotalMilliseconds, 300, 301);
        Assert.Equal(1, wait.Pings);
    }

    /// <summary>
    /// Slow answers do not keep the keys back: right after an activation even an idle window answers slowly for a while (a
    /// test desktop measured 2 to 86 ms on a classic console), and waiting for quick ones made pastes up to 313 ms slower.
    /// </summary>
    [Fact]
    public void Ready_SlowAnswers_DoNotKeepTheKeysBack()
    {
        var window = new FakeWindow { SlowUntil = TimeSpan.FromMilliseconds(120), SlowReply = TimeSpan.FromMilliseconds(3) };
        var wait = window.WaitUntilReady(settleLeft: TimeSpan.FromMilliseconds(40));

        Assert.True(wait.Done);
        Assert.InRange(wait.Waited.TotalMilliseconds, 43, 44);
        Assert.Equal(1, wait.Pings);
    }

    /// <summary>A window that never answers gets its keys at the cap, as before the watch existed.</summary>
    [Fact]
    public void Ready_NoAnswer_EndsAtTheCap()
    {
        var window = new FakeWindow { NeverAnswers = true };
        var settle = TimeSpan.FromMilliseconds(40);
        var wait = window.WaitUntilReady(settle);

        Assert.False(wait.Done);
        Assert.Equal((settle + InputThreadWatch.ReadyCap).TotalMilliseconds, wait.Waited.TotalMilliseconds, precision: 3);
    }

    /// <summary>
    /// A hung window fails each ping at once (SMTO_ABORTIFHUNG): the wait paces its pings and still ends at the cap, without
    /// spinning.
    /// </summary>
    [Fact]
    public void Ready_HungWindow_PacedPings_EndAtTheCap()
    {
        var window = new FakeWindow { Hung = true };
        var wait = window.WaitUntilReady(settleLeft: TimeSpan.Zero);

        Assert.False(wait.Done);
        Assert.Equal(InputThreadWatch.ReadyCap.TotalMilliseconds, wait.Waited.TotalMilliseconds, precision: 3);
        Assert.InRange(wait.Pings, 190, 210);
    }

    /// <summary>A window that closes ends the wait at once, without a ping.</summary>
    [Fact]
    public void Ready_ClosedWindow_EndsTheWait()
    {
        var window = new FakeWindow { ClosesAt = TimeSpan.FromMilliseconds(10) };
        var wait = window.WaitUntilReady(settleLeft: TimeSpan.FromMilliseconds(40));

        Assert.False(wait.Done);
        Assert.True(wait.Waited < InputThreadWatch.ReadyCap);
        Assert.Equal(0, wait.Pings);
    }

    /// <summary>A cancelled paste stops waiting before the settle time is slept.</summary>
    [Fact]
    public void Ready_Cancelled_Throws()
    {
        var window = new FakeWindow();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => window.WaitUntilReady(TimeSpan.FromMilliseconds(40), cancellation.Token));
        Assert.Equal(TimeSpan.Zero, window.Now);
    }

    /// <summary>
    /// After the keys, a quick window has Ctrl released once it answered at once three times in a row and the least hold since
    /// its first answer passed — a few pings, about 20 ms.
    /// </summary>
    [Fact]
    public void Taken_AQuickWindow_IsReleasedAfterTheLeastHold()
    {
        var window = new FakeWindow();
        var wait = window.WaitUntilTaken();

        Assert.True(wait.Done);
        Assert.InRange(wait.Waited.TotalMilliseconds, InputThreadWatch.MinimumHold.TotalMilliseconds, 26);
        Assert.Equal(5, wait.Pings);
    }

    /// <summary>
    /// A window that takes the keys late keeps Ctrl down until it answers at once again, past the least hold since its first
    /// answer — the case the probe reproduced with Agentmaster (CLAUDE.md §2.54).
    /// </summary>
    [Fact]
    public void Taken_ABusyWindow_KeepsCtrlDownUntilItAnswersAtOnce()
    {
        var window = new FakeWindow { BusyUntil = TimeSpan.FromMilliseconds(300) };
        var wait = window.WaitUntilTaken();

        Assert.True(wait.Done);
        Assert.InRange(wait.Waited.TotalMilliseconds, 300 + InputThreadWatch.MinimumHold.TotalMilliseconds, 330);
    }

    /// <summary>A backlog after the first answer (every answer slow) keeps Ctrl down until three quick answers in a row.</summary>
    [Fact]
    public void Taken_ABacklog_KeepsCtrlDown()
    {
        var window = new FakeWindow { SlowUntil = TimeSpan.FromMilliseconds(150), SlowReply = TimeSpan.FromMilliseconds(4) };
        var wait = window.WaitUntilTaken();

        Assert.True(wait.Done);
        Assert.True(wait.Waited.TotalMilliseconds >= 150 + (2 * InputThreadWatch.PingInterval.TotalMilliseconds), $"Released after {wait.Waited.TotalMilliseconds} ms");

        // Released by the quick answers, well before the fallback would have.
        Assert.True(wait.Waited < TimeSpan.FromMilliseconds(4) + InputThreadWatch.HoldFallback, $"Released after {wait.Waited.TotalMilliseconds} ms");
    }

    /// <summary>
    /// Answers that stay slow (a busy PC's scheduling, with nothing queued) release Ctrl once the window kept answering for
    /// the fallback time since its first answer: Ctrl does not stay down for the whole cap.
    /// </summary>
    [Fact]
    public void Taken_AnswersThatStaySlow_ReleaseAfterTheFallback()
    {
        var reply = TimeSpan.FromMilliseconds(4);
        var window = new FakeWindow { SlowUntil = TimeSpan.FromSeconds(10), SlowReply = reply };
        var wait = window.WaitUntilTaken();

        // The first answer comes at 4 ms; then one every ping interval plus the answer's time.
        double first = reply.TotalMilliseconds;
        double earliest = first + InputThreadWatch.HoldFallback.TotalMilliseconds;
        Assert.True(wait.Done);
        Assert.InRange(wait.Waited.TotalMilliseconds, earliest, earliest + InputThreadWatch.PingInterval.TotalMilliseconds + reply.TotalMilliseconds);
        Assert.True(wait.Pings >= InputThreadWatch.FallbackAnswers);
    }

    /// <summary>
    /// A window that answered once and then stopped answering (hung) did not show that it kept pumping: the fallback needs
    /// its answers too, so Ctrl stays down to the cap, as for a window that never answers.
    /// </summary>
    [Fact]
    public void Taken_AWindowThatStopsAnswering_IsHeldToTheCap()
    {
        var window = new FakeWindow { HungFrom = TimeSpan.FromMilliseconds(1) };
        var wait = window.WaitUntilTaken();

        Assert.False(wait.Done);
        Assert.Equal(InputThreadWatch.HoldCap.TotalMilliseconds, wait.Waited.TotalMilliseconds, precision: 3);
    }

    /// <summary>A window that never answers has Ctrl released at the cap: keys the user types must not stay Ctrl+key.</summary>
    [Fact]
    public void Taken_NoAnswer_ReleasedAtTheCap()
    {
        var window = new FakeWindow { NeverAnswers = true };
        var wait = window.WaitUntilTaken();

        Assert.False(wait.Done);
        Assert.Equal(InputThreadWatch.HoldCap.TotalMilliseconds, wait.Waited.TotalMilliseconds, precision: 3);
    }

    /// <summary>A window that closes has Ctrl released at once.</summary>
    [Fact]
    public void Taken_ClosedWindow_ReleasedAtOnce()
    {
        var window = new FakeWindow { NeverAnswers = true, ClosesAt = TimeSpan.Zero };
        var wait = window.WaitUntilTaken();

        Assert.False(wait.Done);
        Assert.Equal(0, wait.Pings);
    }

    /// <summary>A cancelled paste stops holding (the caller releases Ctrl in its finally).</summary>
    [Fact]
    public void Taken_Cancelled_Throws()
    {
        var window = new FakeWindow();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => window.WaitUntilTaken(cancellation.Token));
    }

    /// <summary>A real window's thread answers a ping from its message loop, in far less than the timeout.</summary>
    [Fact]
    public void Ping_ARealWindow_AnswersFromItsMessageLoop()
    {
        using var window = new MessageWindowThread("Input thread watch test");
        var answer = InputThreadWatch.Ping(window.Handle, TimeSpan.FromSeconds(5));

        Assert.NotNull(answer);
        Assert.True(answer < TimeSpan.FromSeconds(5));
        Assert.Null(InputThreadWatch.Ping(0, TimeSpan.FromMilliseconds(50)));
    }

    /// <summary>
    /// With the window's thread busy (a long message), the keys wait for it: the wait ends only after the thread finished its
    /// work — told by the order of events, not by a time — and both waits then succeed.
    /// </summary>
    [Fact]
    public void Waits_ARealBusyThread_EndAfterItsWork()
    {
        using var window = new MessageWindowThread("Input thread watch test");

        var finishedAt = Busy(window, TimeSpan.FromMilliseconds(250));
        var ready = InputThreadWatch.WaitUntilReady(window.Handle, TimeSpan.Zero);
        Assert.True(ready.Done);
        Assert.NotEqual(0, finishedAt());

        finishedAt = Busy(window, TimeSpan.FromMilliseconds(250));
        var taken = InputThreadWatch.WaitUntilTaken(window.Handle);
        Assert.True(taken.Done);
        Assert.NotEqual(0, finishedAt());
    }

    /// <summary>
    /// A thread without keyboard focus or an active window (a message-only window's) has no other window to ask: the target
    /// itself is pinged. No window, nothing to find.
    /// </summary>
    [Fact]
    public void KeyboardWindow_FallsBackToTheTarget()
    {
        using var window = new MessageWindowThread("Input thread watch test");
        Assert.Equal(window.Handle, InputThreadWatch.KeyboardWindow(window.Handle));
        Assert.Equal(0, InputThreadWatch.KeyboardWindow(0));
    }

    /// <summary>
    /// Keeps a window's thread busy for a while with a message of its own, and returns once that work has begun.
    /// </summary>
    /// <param name="window">The window whose thread to keep busy.</param>
    /// <param name="duration">How long the work lasts.</param>
    /// <returns>Reads the <see cref="Stopwatch"/> timestamp at which the work ended; 0 while it runs.</returns>
    private static Func<long> Busy(MessageWindowThread window, TimeSpan duration)
    {
        long end = 0;
        using var started = new ManualResetEventSlim();
        Assert.True(window.Post(() =>
        {
            started.Set();
            Thread.Sleep(duration);
            Volatile.Write(ref end, Stopwatch.GetTimestamp());
        }));

        // The pings must find the thread busy, so the wait starts only once the work has.
        Assert.True(started.Wait(TimeSpan.FromSeconds(30)), "The window thread did not start the work within 30 s");
        return () => Volatile.Read(ref end);
    }

    /// <summary>
    /// A fake window and clock for the waits' rules: answers take a set time, the clock moves only when a ping or a pause
    /// moves it, so every test is exact and instant.
    /// </summary>
    private sealed class FakeWindow
    {
        /// <summary>The fake clock: the time since the wait began.</summary>
        private TimeSpan now;

        /// <summary>The fake clock's time: what the waits have spent so far.</summary>
        public TimeSpan Now => now;

        /// <summary>Until when the window's thread does not answer at all (a long message): a ping then waits for it.</summary>
        public TimeSpan BusyUntil { get; init; }

        /// <summary>Until when each answer takes <see cref="SlowReply"/> (a backlog of messages).</summary>
        public TimeSpan SlowUntil { get; init; }

        /// <summary>How long an answer takes before <see cref="SlowUntil"/>.</summary>
        public TimeSpan SlowReply { get; init; }

        /// <summary>The window never answers: each ping waits its whole timeout.</summary>
        public bool NeverAnswers { get; init; }

        /// <summary>The window is hung: each ping fails at once.</summary>
        public bool Hung { get; init; }

        /// <summary>From when the window is hung (each ping fails at once); <see langword="null"/> for never.</summary>
        public TimeSpan? HungFrom { get; init; }

        /// <summary>When the window closes; <see langword="null"/> for never.</summary>
        public TimeSpan? ClosesAt { get; init; }

        /// <summary>Runs the readiness rule over this window.</summary>
        /// <param name="settleLeft">What is left of the settle time.</param>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <returns>What the wait saw.</returns>
        /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
        public InputThreadWait WaitUntilReady(TimeSpan settleLeft, CancellationToken cancellationToken = default) =>
            InputThreadWatch.WaitUntilReady(Ping, () => now, Sleep, Exists, settleLeft, cancellationToken);

        /// <summary>Runs the hold rule over this window.</summary>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <returns>What the wait saw.</returns>
        /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
        public InputThreadWait WaitUntilTaken(CancellationToken cancellationToken = default) =>
            InputThreadWatch.WaitUntilTaken(Ping, () => now, Sleep, Exists, cancellationToken);

        /// <summary>A ping: moves the clock by the answer's time (or the timeout) and returns the answer's time.</summary>
        /// <param name="timeout">The longest wait.</param>
        /// <returns>The answer's time, or <see langword="null"/> for none.</returns>
        private TimeSpan? Ping(TimeSpan timeout)
        {
            // A hung or closed window fails at once (SMTO_ABORTIFHUNG; an invalid handle).
            if (Hung || (HungFrom is { } hungFrom && now >= hungFrom) || !Exists())
            {
                return null;
            }

            TimeSpan reply = NeverAnswers ? TimeSpan.MaxValue
                : now < BusyUntil ? BusyUntil - now + TimeSpan.FromMilliseconds(0.1)
                : now < SlowUntil ? SlowReply
                : TimeSpan.FromMilliseconds(0.1);
            if (reply > timeout)
            {
                now += timeout;
                return null;
            }

            now += reply;
            return reply;
        }

        /// <summary>A pause: moves the clock.</summary>
        /// <param name="pause">How long.</param>
        private void Sleep(TimeSpan pause) => now += pause;

        /// <summary>Whether the window still exists at the fake clock's time.</summary>
        /// <returns><see langword="false"/> from <see cref="ClosesAt"/> on.</returns>
        private bool Exists() => ClosesAt is not { } closes || now < closes;
    }
}
