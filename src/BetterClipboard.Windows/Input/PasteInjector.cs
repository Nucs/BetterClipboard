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

    /// <summary>The window did not become the foreground window in time; nothing was injected.</summary>
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
public static class PasteInjector
{
    /// <summary>
    /// How long the target gets between its activation and the Ctrl+V: many apps (Chromium, Office) restore the focused
    /// control asynchronously after <c>WM_ACTIVATE</c>, and keys sent too early go nowhere.
    /// </summary>
    /// <remarks>
    /// Counted from the activation: whatever the caller does after activating the target counts toward it (CLAUDE.md §2.52).
    /// Waited with a high-resolution timer (<see cref="PreciseDelay"/>): <c>Task.Delay(40)</c> took 40 to 56 ms.
    /// </remarks>
    public static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// Activates <paramref name="target"/> and sends Ctrl+V to it.
    /// </summary>
    /// <remarks>
    /// Ctrl+V is the universal paste gesture on Windows (consoles and Windows Terminal included since
    /// Windows 10). The target gets <see cref="SettleTime"/> after its activation before the keys arrive.
    /// </remarks>
    /// <param name="target">Top-level window captured in <see cref="ForegroundContext.TargetWindow"/>.</param>
    /// <param name="cancellationToken">Cancels the wait for activation.</param>
    /// <returns><see langword="true"/> when the keystrokes were injected into the activated target.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static async Task<bool> PasteIntoAsync(nint target, CancellationToken cancellationToken = default) =>
        await PasteIntoWithOutcomeAsync(target, cancellationToken: cancellationToken).ConfigureAwait(false) == PasteOutcome.Pasted;

    /// <summary>
    /// Sends Ctrl+V to <paramref name="target"/> once it is the foreground window and has had <see cref="SettleTime"/> since
    /// its activation, reporting what happened — so the caller can tell the user when Windows does not allow the paste.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A caller that activated the target itself passes when (<paramref name="activatedAt"/>), and only what is left of the
    /// settle time is waited. A target that is not in front (never activated, or its activation lost a race with another
    /// window) is activated here, and the settle time counts from then.
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
    /// <param name="timeline">The paste's timeline, which gets its "settle" and "sent" stages; <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancels the wait for activation.</param>
    /// <returns>The outcome; only <see cref="PasteOutcome.Pasted"/> means the keys went in.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static async Task<PasteOutcome> PasteIntoWithOutcomeAsync(
        nint target, long activatedAt = 0, PasteTimeline? timeline = null, CancellationToken cancellationToken = default)
    {
        if (target == 0 || !IsWindow(target))
        {
            return PasteOutcome.NoTarget;
        }

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

        // Only what the activation has not already had: the clipboard write (and anything else) since then counts.
        await PreciseDelay.WaitAsync(SettleTime - Stopwatch.GetElapsedTime(activatedAt), cancellationToken).ConfigureAwait(false);
        timeline?.Mark("settle");
        KeyboardInput.ReleaseModifiers();
        bool injected = KeyboardInput.SendCtrlV();
        timeline?.Mark("sent");
        if (!injected)
        {
            AppLog.Warn("Ctrl+V injection was blocked (fewer events injected than sent).");
            return PasteOutcome.InjectionFailed;
        }

        return PasteOutcome.Pasted;
    }
}
