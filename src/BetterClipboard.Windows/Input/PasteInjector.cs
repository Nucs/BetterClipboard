using System.Diagnostics;
using BetterClipboard.Core.Diagnostics;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Input;

/// <summary>What happened when BetterClipboard tried to paste into the window the user came from.</summary>
public enum PasteOutcome
{
    /// <summary>Ctrl+V was injected into the activated window.</summary>
    Pasted,

    /// <summary>There was no window to paste into (opened from the tray), or it is gone.</summary>
    NoTarget,

    /// <summary>
    /// The window did not become the foreground window in time, or another window came to the front while the paste waited
    /// for the window's input thread; nothing was injected.
    /// </summary>
    NotForeground,

    /// <summary>
    /// The window runs at a higher integrity level (as administrator) than BetterClipboard, so Windows would drop the
    /// keystrokes (UIPI): nothing was injected, the window was activated, and the item is on the clipboard for the user's
    /// own Ctrl+V.
    /// </summary>
    TargetElevated,

    /// <summary><c>SendInput</c> injected fewer events than asked (another app blocked input, e.g. a secure desktop).</summary>
    InjectionFailed,
}

/// <summary>
/// Pastes the current clipboard into a window, the way Win+V does after you pick an item: re-activate
/// the window the user came from, then inject Ctrl+V.
/// </summary>
/// <remarks>
/// Since 2026-11-17 (CLAUDE.md §2.54) the keys go in once the window's input thread answers after the settle time, and Ctrl
/// stays down until that thread took the V (<see cref="InputThreadWatch"/>). Before, one <c>SendInput</c> call sent Ctrl down, V down,
/// V up and Ctrl up together, and a window that took them late typed "v" when another program reset its key state in between
/// — the owner's report from an overloaded PC, reproduced in a test desktop.
/// </remarks>
public static class PasteInjector
{
    /// <summary>
    /// How long the target gets between its activation and the Ctrl+V: many apps (Chromium, Office) restore the focused
    /// control asynchronously after <c>WM_ACTIVATE</c>, and keys sent too early go nowhere.
    /// </summary>
    /// <remarks>
    /// Counted from the activation: whatever the caller does after activating the target counts toward it (CLAUDE.md §2.52).
    /// Waited with a high-resolution timer (<see cref="PreciseDelay"/>): <c>Task.Delay(40)</c> took 40 to 56 ms. Since
    /// 2026-11-17 the window's input thread is asked when it ends (<see cref="InputThreadWatch.WaitUntilReady(nint, TimeSpan, CancellationToken)"/>): a
    /// window that pumps its messages then gets the keys a ping's round trip later, one frozen at that moment once it pumps
    /// again.
    /// </remarks>
    public static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// Activates <paramref name="target"/> and sends Ctrl+V to it.
    /// </summary>
    /// <remarks>
    /// Ctrl+V is the universal paste gesture on Windows (consoles and Windows Terminal included since
    /// Windows 10). The target gets <see cref="SettleTime"/> after its activation before the keys arrive, and Ctrl stays down
    /// until it took the V (<see cref="PasteIntoWithOutcomeAsync"/>).
    /// </remarks>
    /// <param name="target">Top-level window captured in <see cref="ForegroundContext.TargetWindow"/>.</param>
    /// <param name="cancellationToken">Cancels the wait for activation.</param>
    /// <returns><see langword="true"/> when the keystrokes were injected into the activated target.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static async Task<bool> PasteIntoAsync(nint target, CancellationToken cancellationToken = default) =>
        await PasteIntoWithOutcomeAsync(target, cancellationToken: cancellationToken).ConfigureAwait(false) == PasteOutcome.Pasted;

    /// <summary>
    /// Sends Ctrl+V to <paramref name="target"/> once it is the foreground window, has had <see cref="SettleTime"/> since its
    /// activation and its input thread answers, keeping Ctrl down until that thread took the V — and reports what happened,
    /// so the caller can tell the user when Windows does not allow the paste.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A caller that activated the target itself passes when (<paramref name="activatedAt"/>), and only what is left of the
    /// settle time is waited. A target that is not in front (never activated, or its activation lost a race with another
    /// window) is activated here, and the settle time counts from then.
    /// </para>
    /// <para>
    /// <b>The order since 2026-11-17</b> (CLAUDE.md §2.54): when the settle time ends, the window's input thread is pinged,
    /// and the keys go in once it answers (<see cref="InputThreadWatch.WaitUntilReady(nint, TimeSpan, CancellationToken)"/>, at most a second beyond the settle
    /// time); a window still frozen then gets them anyway. Ctrl down, V down and V up go in one
    /// <c>SendInput</c> call, and Ctrl up once the thread answered that it took them (<see cref="InputThreadWatch.WaitUntilTaken(nint, CancellationToken)"/>,
    /// at most a second). The window must still be in front when the wait ends: a user who went elsewhere meanwhile gets no
    /// keys there (<see cref="PasteOutcome.NotForeground"/>).
    /// </para>
    /// <para>
    /// From the start until Ctrl is up again, <see cref="PasteKeyGuard"/> holds back the keys the user types and types them
    /// after the paste, in their order: a key typed meanwhile would otherwise land before the pasted text, or as Ctrl+key.
    /// </para>
    /// <para>
    /// The integrity check comes after the activation, on purpose: the user lands in the elevated window, where their own
    /// Ctrl+V works (the item is on the clipboard already). It must come before the injection because UIPI drops the keys
    /// silently — <c>SendInput</c> reports them all as injected — so the old "injected fewer events" check never fired for an
    /// elevated target (found in the 2026-10-09 QA pass on a Windows 11 VM with UAC on).
    /// </para>
    /// <para>
    /// An elevated BetterClipboard (a test, or an installer started from an admin prompt that launched it directly) pastes
    /// into elevated windows normally: only a target above this process's level is refused.
    /// </para>
    /// </remarks>
    /// <param name="target">Top-level window captured in <see cref="ForegroundContext.TargetWindow"/>.</param>
    /// <param name="activatedAt">
    /// The <see cref="Stopwatch.GetTimestamp"/> at which the caller activated <paramref name="target"/>, or 0 when it did not.
    /// </param>
    /// <param name="timeline">
    /// The paste's timeline, which gets its "settle", "ready" and "sent" stages, how long Ctrl stayed down
    /// (<see cref="PasteTimeline.ControlHeld"/>) and how many keys typed meanwhile were held back
    /// (<see cref="PasteTimeline.KeysHeldBack"/>, added to what a caller's own guard counted); <see langword="null"/> for none.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the waits (activation, readiness, the hold); Ctrl is released on cancellation as on every other path.
    /// </param>
    /// <returns>The outcome; only <see cref="PasteOutcome.Pasted"/> means the keys went in.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static async Task<PasteOutcome> PasteIntoWithOutcomeAsync(
        nint target, long activatedAt = 0, PasteTimeline? timeline = null, CancellationToken cancellationToken = default)
    {
        if (target == 0 || !IsWindow(target))
        {
            return PasteOutcome.NoTarget;
        }

        // Keys the user types from now until Ctrl is up again are held back and typed after the paste, in their order
        // (CLAUDE.md §2.54): typed during the waits they would land before the pasted text — or, in a window that takes them
        // late, as Ctrl+key, since the paste holds Ctrl down — and typed during the hold as Ctrl+key. A caller that engaged
        // the guard earlier (AppController, from the Enter on) is joined. Without a guard (it could not start), the paste
        // goes on as before.
        var guard = PasteKeyGuard.TryGetShared();
        bool guarding = guard is not null && await guard.EngageAsync().ConfigureAwait(false);
        try
        {
            return await PasteGuardedAsync(target, activatedAt, timeline, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // After the paste's Ctrl is up (PasteGuardedAsync releases it on every path): the held keys follow it.
            if (guarding)
            {
                var release = await guard!.ReleaseAsync().ConfigureAwait(false);
                if (timeline is not null)
                {
                    timeline.KeysHeldBack += release.Held;
                }
            }
        }
    }

    /// <summary>
    /// The paste itself, while the caller's key guard holds the user's keys back: activation, settle time, readiness, Ctrl+V
    /// with Ctrl held until the window took the V (see <see cref="PasteIntoWithOutcomeAsync"/>).
    /// </summary>
    /// <param name="target">The window to paste into; it exists.</param>
    /// <param name="activatedAt">When the caller activated it, or 0.</param>
    /// <param name="timeline">The paste's timeline, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the waits; Ctrl is released on every path.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    private static async Task<PasteOutcome> PasteGuardedAsync(
        nint target, long activatedAt, PasteTimeline? timeline, CancellationToken cancellationToken)
    {
        if (activatedAt == 0 || !ForegroundHelper.IsForeground(target))
        {
            ForegroundHelper.Activate(target);
            activatedAt = Stopwatch.GetTimestamp();
        }

        if (!await ForegroundHelper.WaitForForegroundAsync(target, TimeSpan.FromMilliseconds(600), cancellationToken).ConfigureAwait(false))
        {
            AppLog.Warn("Paste target did not become foreground; item left on the clipboard only.");
            return PasteOutcome.NotForeground;
        }

        if (ProcessIntegrity.IsAboveCurrent(target))
        {
            AppLog.Info("Not pasted: the target window runs as administrator, and Windows drops keys sent to it from BetterClipboard. The item is on the clipboard.");
            return PasteOutcome.TargetElevated;
        }

        // Ctrl+V goes in once the settle time since the activation has passed (only what the clipboard write and the rest
        // have not had already) and the window's input thread answers a ping: a window frozen at that moment gets the keys
        // once it pumps again, with nothing pressed meanwhile (CLAUDE.md §2.54). Off the caller's thread: the waits block.
        var settleLeft = SettleTime - Stopwatch.GetElapsedTime(activatedAt);
        long settleEnd = Stopwatch.GetTimestamp() + Math.Max(0, (long)(settleLeft.TotalSeconds * Stopwatch.Frequency));
        nint keyboardWindow = InputThreadWatch.KeyboardWindow(target);
        await Task.Run(() => InputThreadWatch.WaitUntilReady(keyboardWindow, settleLeft, cancellationToken), cancellationToken).ConfigureAwait(false);
        timeline?.Mark("settle", settleEnd);
        timeline?.Mark("ready");

        // The readiness wait can last up to a second: a user who went to another window meanwhile must not get the keys there.
        // Asked cautiously: Windows reports no foreground window at all for a moment while a program attaches to the window's
        // thread — the very thing this wait guards against — and that is not the user leaving.
        if (ForegroundHelper.IsAnotherWindowInForeground(target))
        {
            AppLog.Info("Not pasted: another window came to the front while the paste waited for its window. The item is on the clipboard.");
            return PasteOutcome.NotForeground;
        }

        KeyboardInput.ReleaseModifiers();
        bool injected = false;
        try
        {
            injected = KeyboardInput.SendCtrlVHoldingCtrl();
            timeline?.Mark("sent");
            if (injected)
            {
                // Ctrl stays down until the window took the V: a key-state reset meanwhile (another program attaching to the
                // window's thread) then copies a Ctrl that is down. Asked again: the focus may have moved since the
                // readiness wait (a browser restoring it), and the keys went to the window that has it now.
                nint takenBy = InputThreadWatch.KeyboardWindow(target);
                var held = await Task.Run(() => InputThreadWatch.WaitUntilTaken(takenBy, cancellationToken), cancellationToken).ConfigureAwait(false);
                if (timeline is not null)
                {
                    timeline.ControlHeld = held.Waited;
                    timeline.ControlHeldUntilTaken = held.Done;
                }
            }
        }
        finally
        {
            // Every path releases Ctrl — a failed or partial injection, a cancelled hold: one left down would turn every key
            // the user types into Ctrl+key.
            KeyboardInput.ReleaseCtrl();
        }

        if (!injected)
        {
            AppLog.Warn("Ctrl+V injection was blocked (fewer events injected than sent).");
            return PasteOutcome.InjectionFailed;
        }

        return PasteOutcome.Pasted;
    }
}
