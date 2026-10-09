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
    /// Activates <paramref name="target"/> and sends Ctrl+V to it.
    /// </summary>
    /// <remarks>
    /// Ctrl+V is the universal paste gesture on Windows (consoles and Windows Terminal included since
    /// Windows 10). The short settle delay after activation matters: many apps (Chromium, Office) restore
    /// the focused control asynchronously after <c>WM_ACTIVATE</c>, and keys sent too early go nowhere.
    /// </remarks>
    /// <param name="target">Top-level window captured in <see cref="ForegroundContext.TargetWindow"/>.</param>
    /// <param name="cancellationToken">Cancels the wait for activation.</param>
    /// <returns><see langword="true"/> when the keystrokes were injected into the activated target.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static async Task<bool> PasteIntoAsync(nint target, CancellationToken cancellationToken = default) =>
        await PasteIntoWithOutcomeAsync(target, cancellationToken).ConfigureAwait(false) == PasteOutcome.Pasted;

    /// <summary>
    /// Activates <paramref name="target"/> and sends Ctrl+V to it, reporting what happened — so the caller can tell the
    /// user when Windows does not allow the paste.
    /// </summary>
    /// <remarks>
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
    /// <param name="cancellationToken">Cancels the wait for activation.</param>
    /// <returns>The outcome; only <see cref="PasteOutcome.Pasted"/> means the keys went in.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static async Task<PasteOutcome> PasteIntoWithOutcomeAsync(nint target, CancellationToken cancellationToken = default)
    {
        if (target == 0 || !IsWindow(target))
        {
            return PasteOutcome.NoTarget;
        }

        ForegroundHelper.Activate(target);
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

        await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        KeyboardInput.ReleaseModifiers();
        if (!KeyboardInput.SendCtrlV())
        {
            AppLog.Warn("Ctrl+V injection was blocked (fewer events injected than sent).");
            return PasteOutcome.InjectionFailed;
        }

        return PasteOutcome.Pasted;
    }
}
