using BetterClipboard.Core.Diagnostics;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Input;

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
    public static async Task<bool> PasteIntoAsync(nint target, CancellationToken cancellationToken = default)
    {
        if (target == 0 || !IsWindow(target))
        {
            return false;
        }

        ForegroundHelper.Activate(target);
        if (!await ForegroundHelper.WaitForForegroundAsync(target, TimeSpan.FromMilliseconds(600), cancellationToken).ConfigureAwait(false))
        {
            AppLog.Warn("Paste target did not become foreground; item left on the clipboard only.");
            return false;
        }

        await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        KeyboardInput.ReleaseModifiers();
        if (!KeyboardInput.SendCtrlV())
        {
            AppLog.Warn("Ctrl+V injection was blocked (UIPI: the target is probably elevated).");
            return false;
        }

        return true;
    }
}
