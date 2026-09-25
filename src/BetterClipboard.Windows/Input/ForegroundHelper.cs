using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Input;

/// <summary>
/// Reliable window activation despite Windows' foreground-lock rules.
/// </summary>
/// <remarks>
/// <c>SetForegroundWindow</c> only succeeds when the caller is allowed to steal focus (it owns the
/// foreground, received the last input event, got a <c>WM_HOTKEY</c>, …). A process woken by a
/// low-level hook qualifies for none of those, so escalating tricks are tried in order: plain call →
/// no-op <c>SendInput</c> ("we received the last input") → <c>AttachThreadInput</c> to the current
/// foreground thread. Each step is verified with <c>GetForegroundWindow</c>.
/// </remarks>
public static class ForegroundHelper
{
    /// <summary>
    /// Brings <paramref name="hwnd"/> to the foreground (restoring it if minimized).
    /// </summary>
    /// <param name="hwnd">Top-level window.</param>
    /// <returns><see langword="true"/> when the window is the foreground window afterwards.</returns>
    public static bool Activate(nint hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd))
        {
            return false;
        }

        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
        }

        if (IsForeground(hwnd) || (SetForegroundWindow(hwnd) && IsForeground(hwnd)))
        {
            return true;
        }

        KeyboardInput.SendNoOpMouseInput();
        if (SetForegroundWindow(hwnd) && IsForeground(hwnd))
        {
            return true;
        }

        nint foreground = GetForegroundWindow();
        uint foregroundThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
        uint self = GetCurrentThreadId();
        if (foregroundThread != 0 && foregroundThread != self && AttachThreadInput(self, foregroundThread, true))
        {
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                // Always detach: a lingering attachment merges our input queue with another app's and
                // makes both hang when either stops pumping messages.
                AttachThreadInput(self, foregroundThread, false);
            }
        }

        return IsForeground(hwnd);
    }

    /// <summary>
    /// Waits (polling) until <paramref name="hwnd"/> is the foreground window.
    /// </summary>
    /// <param name="hwnd">Window.</param>
    /// <param name="timeout">Maximum wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns><see langword="true"/> when it became foreground within the timeout.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static async Task<bool> WaitForForegroundAsync(nint hwnd, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsForeground(hwnd))
            {
                return true;
            }

            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }

        return IsForeground(hwnd);
    }

    /// <summary>Whether <paramref name="hwnd"/> (or its root owner) is the foreground window.</summary>
    /// <param name="hwnd">Window.</param>
    /// <returns><see langword="true"/> when foreground.</returns>
    private static bool IsForeground(nint hwnd)
    {
        nint foreground = GetForegroundWindow();
        return foreground == hwnd || (foreground != 0 && GetAncestor(foreground, GA_ROOTOWNER) == hwnd);
    }
}
