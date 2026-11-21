using System.Diagnostics;
using System.Runtime.InteropServices;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Input;

/// <summary>What one wait on a window's input thread saw, for the paste's log line.</summary>
/// <param name="Done">
/// Whether the wait's condition held: the thread answered after the settle time
/// (<see cref="InputThreadWatch.WaitUntilReady(nint, TimeSpan, CancellationToken)"/>), or it answered as one that took the keys
/// sent to it (<see cref="InputThreadWatch.WaitUntilTaken(nint, CancellationToken)"/>). <see langword="false"/> when the wait's
/// cap ended it, or the window went away: the caller goes ahead anyway, as BetterClipboard did before the watch existed.
/// </param>
/// <param name="Waited">How long the wait lasted.</param>
/// <param name="Pings">How many WM_NULL pings it sent.</param>
public readonly record struct InputThreadWait(bool Done, TimeSpan Waited, int Pings);

/// <summary>
/// Waits on the thread that reads a window's keyboard input, by pinging the window with <c>WM_NULL</c>
/// (<c>SendMessageTimeout</c>): a thread answers a sent message only from its message loop, so an answer means it is taking
/// its messages again, and an answer within <see cref="QuickReply"/> that it has none piling up. A paste uses it twice: to send
/// Ctrl+V once a window busy at the end of the settle time pumps again, and to keep Ctrl down until the window took the V
/// (CLAUDE.md §2.54).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why Ctrl must stay down until the window took the V.</b> A window reads Ctrl's state from its thread's key state, which
/// Windows updates as the thread takes each key message, in order — so one <c>SendInput</c> call with Ctrl down, V down, V up
/// and Ctrl up arrives as Ctrl+V however late the window reads it. Except that every <c>AttachThreadInput</c> call on that
/// thread resets its key state to the keyboard's current state (Microsoft's documentation says so, and the probe
/// <c>tools/probes/probe_ctrlv.cs</c> shows it), and programs call it on whatever window comes to the front, to read its focus
/// or caret. When that lands after the window took the Ctrl and before it took the V, with Ctrl already up, the window types
/// "v": on an overloaded PC, where the window is late to take its keys, it did (the owner's report, 2026-11-17). With Ctrl
/// still down, the reset copies a Ctrl that is down, and the paste survives it.
/// </para>
/// <para>
/// <b>Why pings, not the key state.</b> The key state cannot tell whether the window took the keys: reading another thread's
/// key state needs <c>AttachThreadInput</c> — a reset itself — and any other program's reset rewrites it to the keyboard's
/// state, which already shows the keys as typed (the probe's key-state watch reported V taken 2 ms after it was sent, to a
/// window whose process was suspended). Nothing another program does can make a suspended or busy thread answer a ping.
/// </para>
/// <para>
/// <b>Why one answer is enough before the keys, and three quick ones after.</b> Right after an activation a window answers
/// pings in 2 to 86 ms for a while (its own activation work, and a busy PC's scheduling: measured in a test desktop on a
/// classic console too), so asking for quick answers before the keys made pastes 57 to 313 ms slower. Any answer tells that
/// the thread pumps again, which is what keeps the keys out of a frozen window. After the keys, quick answers in a row tell
/// that it worked through what was queued before them; on a PC where answers stay slow, <see cref="HoldFallback"/> ends the
/// hold so Ctrl does not stay down for long.
/// </para>
/// </remarks>
public static class InputThreadWatch
{
    /// <summary>The pause between two pings: the thread gets time to take its messages between them.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// An answer this fast means the thread was waiting for messages, not working through a backlog: it answers a ping only
    /// between two messages, so one busy with each answers later. Footgun: a busy PC's scheduling alone makes some answers
    /// slower than this (the reason for <see cref="HoldFallback"/>).
    /// </summary>
    public static readonly TimeSpan QuickReply = TimeSpan.FromMilliseconds(2);

    /// <summary>How many quick answers in a row, after the keys, tell that the thread took them.</summary>
    public const int QuickRepliesToRelease = 3;

    /// <summary>
    /// The least time Ctrl stays down after the thread first answered following the keys: its first answer can come before it
    /// took the V (it answers between two messages), the later ones after.
    /// </summary>
    public static readonly TimeSpan MinimumHold = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// After this long since its first answer following the keys, and <see cref="FallbackAnswers"/> answers, the thread is
    /// taken to have worked through its queue even without quick answers: on a busy PC they can stay slow for a while without
    /// anything being queued, and every key the user presses while Ctrl is down becomes Ctrl+key.
    /// </summary>
    public static readonly TimeSpan HoldFallback = TimeSpan.FromMilliseconds(200);

    /// <summary>How many answers, counting the first, <see cref="HoldFallback"/> also needs: the thread kept pumping.</summary>
    public const int FallbackAnswers = 5;

    /// <summary>
    /// The longest wait for the thread to answer, counted from the end of the paste's settle time: a window that does not
    /// answer by then gets the keys anyway, as before the watch existed.
    /// </summary>
    public static readonly TimeSpan ReadyCap = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The longest time Ctrl stays down after the V: keys the user types while Ctrl is down become Ctrl+key, so a window that
    /// does not answer does not keep it down for long.
    /// </summary>
    public static readonly TimeSpan HoldCap = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The window that gets the keys sent to <paramref name="target"/>'s thread: the window with its keyboard focus, else its
    /// active window, else <paramref name="target"/> itself. Pinging it asks the thread that reads the keys.
    /// </summary>
    /// <param name="target">The top-level window the paste goes to.</param>
    /// <returns>The window to ping; <paramref name="target"/> when the thread's focus cannot be read.</returns>
    public static nint KeyboardWindow(nint target)
    {
        uint thread = target == 0 ? 0 : GetWindowThreadProcessId(target, out _);
        var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (thread != 0 && GetGUIThreadInfo(thread, ref info))
        {
            if (info.hwndFocus != 0)
            {
                return info.hwndFocus;
            }

            if (info.hwndActive != 0)
            {
                return info.hwndActive;
            }
        }

        return target;
    }

    /// <summary>
    /// Waits out <paramref name="settleLeft"/>, then until <paramref name="window"/>'s thread answers a ping — at most
    /// <see cref="ReadyCap"/> beyond the settle time. Blocks the calling thread (a thread-pool thread of the paste).
    /// </summary>
    /// <remarks>
    /// A window that pumps its messages costs the paste the one ping's round trip after the settle time (well under a
    /// millisecond on an idle PC). One that is frozen at that moment (an overloaded PC, a long message) gets the keys once it
    /// pumps again, with nothing pressed meanwhile: keys the user types then stay plain keys. Pinging during the settle time
    /// would tell nothing: a window answers while it is still restoring its focus, which is what the settle time waits for.
    /// </remarks>
    /// <param name="window">The window that gets the keys (<see cref="KeyboardWindow"/>).</param>
    /// <param name="settleLeft">What is left of the paste's settle time; zero or negative when it has passed.</param>
    /// <param name="cancellationToken">Cancels the wait (checked before and after the settle time, and between pings).</param>
    /// <returns>What the wait saw; <see cref="InputThreadWait.Done"/> is <see langword="false"/> when the cap ended it.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static InputThreadWait WaitUntilReady(nint window, TimeSpan settleLeft, CancellationToken cancellationToken = default)
    {
        long start = Stopwatch.GetTimestamp();
        return WaitUntilReady(timeout => Ping(window, timeout), () => Stopwatch.GetElapsedTime(start), PreciseDelay.Wait, () => IsWindow(window), settleLeft, cancellationToken);
    }

    /// <summary>
    /// Waits until <paramref name="window"/>'s thread has taken the keys just sent to it: it answered, then answered
    /// <see cref="QuickRepliesToRelease"/> pings in a row within <see cref="QuickReply"/> at least <see cref="MinimumHold"/>
    /// after its first answer — or, with answers that stay slow, <see cref="FallbackAnswers"/> of them over
    /// <see cref="HoldFallback"/>; at most <see cref="HoldCap"/>. Blocks the calling thread (a thread-pool thread of the paste).
    /// </summary>
    /// <param name="window">The window that got the keys (<see cref="KeyboardWindow"/>).</param>
    /// <param name="cancellationToken">Cancels the wait between two pings.</param>
    /// <returns>What the wait saw; <see cref="InputThreadWait.Done"/> is <see langword="false"/> when the cap ended it.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static InputThreadWait WaitUntilTaken(nint window, CancellationToken cancellationToken = default)
    {
        long start = Stopwatch.GetTimestamp();
        return WaitUntilTaken(timeout => Ping(window, timeout), () => Stopwatch.GetElapsedTime(start), PreciseDelay.Wait, () => IsWindow(window), cancellationToken);
    }

    /// <summary>
    /// The rule of <see cref="WaitUntilReady(nint, TimeSpan, CancellationToken)"/> over seams, so tests drive it with a fake
    /// window and a fake clock.
    /// </summary>
    /// <param name="ping">Pings the window with a timeout; returns how long the answer took, or <see langword="null"/> for none.</param>
    /// <param name="elapsed">The time since the wait began.</param>
    /// <param name="sleep">Pauses the calling thread.</param>
    /// <param name="exists">Whether the window still exists: a closed one ends the wait.</param>
    /// <param name="settleLeft">What is left of the settle time; zero or negative when it has passed.</param>
    /// <param name="cancellationToken">Cancels the wait before and after the settle time, and between pings.</param>
    /// <returns>What the wait saw.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    internal static InputThreadWait WaitUntilReady(
        Func<TimeSpan, TimeSpan?> ping, Func<TimeSpan> elapsed, Action<TimeSpan> sleep, Func<bool> exists, TimeSpan settleLeft, CancellationToken cancellationToken)
    {
        // A paste cancelled already (the app exiting) does not sleep the settle time first.
        cancellationToken.ThrowIfCancellationRequested();
        if (settleLeft > TimeSpan.Zero)
        {
            sleep(settleLeft);
        }
        else
        {
            settleLeft = TimeSpan.Zero;
        }

        var deadline = settleLeft + ReadyCap;
        int pings = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = elapsed();
            if (now >= deadline || !exists())
            {
                return new InputThreadWait(false, now, pings);
            }

            // The ping itself waits for a busy thread, up to the cap: any answer means it pumps its messages again.
            var reply = ping(deadline - now);
            pings++;
            now = elapsed();
            if (reply is not null)
            {
                return new InputThreadWait(true, now, pings);
            }

            // No answer: the cap passed during the ping, or a hung window failed it at once — paced, so it is not spun on.
            var pause = deadline - now < PingInterval ? deadline - now : PingInterval;
            if (pause > TimeSpan.Zero)
            {
                sleep(pause);
            }
        }
    }

    /// <summary>
    /// The rule of <see cref="WaitUntilTaken(nint, CancellationToken)"/> over seams, so tests drive it with a fake window and a
    /// fake clock.
    /// </summary>
    /// <param name="ping">Pings the window with a timeout; returns how long the answer took, or <see langword="null"/> for none.</param>
    /// <param name="elapsed">The time since the keys were sent.</param>
    /// <param name="sleep">Pauses the calling thread.</param>
    /// <param name="exists">Whether the window still exists: a closed one ends the wait.</param>
    /// <param name="cancellationToken">Cancels the wait between two pings.</param>
    /// <returns>What the wait saw.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    internal static InputThreadWait WaitUntilTaken(
        Func<TimeSpan, TimeSpan?> ping, Func<TimeSpan> elapsed, Action<TimeSpan> sleep, Func<bool> exists, CancellationToken cancellationToken)
    {
        int pings = 0;
        int quick = 0;
        int answers = 0;
        TimeSpan? firstAnswer = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = elapsed();
            if (now >= HoldCap || !exists())
            {
                return new InputThreadWait(false, now, pings);
            }

            var reply = ping(HoldCap - now);
            pings++;
            now = elapsed();
            if (reply is { } answer)
            {
                firstAnswer ??= now;
                answers++;
                quick = answer <= QuickReply ? quick + 1 : 0;
            }
            else
            {
                quick = 0;
            }

            if (firstAnswer is { } first
                && ((quick >= QuickRepliesToRelease && now - first >= MinimumHold)
                    || (answers >= FallbackAnswers && now - first >= HoldFallback)))
            {
                return new InputThreadWait(true, now, pings);
            }

            // Paced, so the thread gets time to take its messages between two pings; never past the cap.
            var pause = HoldCap - now < PingInterval ? HoldCap - now : PingInterval;
            if (pause > TimeSpan.Zero)
            {
                sleep(pause);
            }
        }
    }

    /// <summary>
    /// Sends <c>WM_NULL</c> to <paramref name="window"/> and waits for the answer, at most <paramref name="timeout"/>; a hung
    /// window (one that has not taken its messages for 5 s) fails at once.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="timeout">The longest wait; at least a millisecond is waited.</param>
    /// <returns>How long the answer took, or <see langword="null"/> when none came (the timeout, a hung or gone window).</returns>
    internal static TimeSpan? Ping(nint window, TimeSpan timeout)
    {
        uint milliseconds = (uint)Math.Clamp(Math.Ceiling(timeout.TotalMilliseconds), 1, int.MaxValue);
        long start = Stopwatch.GetTimestamp();
        return SendMessageTimeout(window, WM_NULL, 0, 0, SMTO_ABORTIFHUNG, milliseconds, out _) != 0
            ? Stopwatch.GetElapsedTime(start)
            : null;
    }
}
