using System.Runtime.InteropServices;
using BetterClipboard.Core.Diagnostics;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Input;

/// <summary>
/// Follows which window of another app the user is in, while the panel stays open after pasting (pinned, CLAUDE.md §2.55):
/// a pinned panel pastes into the last such window, not only into the one that was in front when the shortcut opened it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is needed.</b> A panel that closes after each paste learns its paste target once, from the shortcut
/// (<see cref="ForegroundContext.Capture"/>). A pinned panel stays up while the user clicks into other apps between pastes,
/// and each click on one of its cards activates the panel itself — by then, the window the user came from is no longer the
/// foreground window, and nothing at the click says which it was. So the tracker watches every change of the foreground
/// window while the pinned panel shows, and keeps the last one that belongs to another app.
/// </para>
/// <para>
/// <b>How.</b> A WinEvent hook on <c>EVENT_SYSTEM_FOREGROUND</c>, out of context: no code runs in other processes, and the
/// callback runs on the thread that called <see cref="Start"/>, from its message loop — the panel's UI thread, which can
/// hand the window straight to the paste code without locks. Nothing polls: an idle tracker costs nothing.
/// <c>WINEVENT_SKIPOWNPROCESS</c> leaves this process's own windows out (the panel, Settings, their popups and menus: never a
/// paste target), and <see cref="IsShellSurface"/> the shell's own surfaces (the taskbar, the tray's overflow, Start and
/// Search, Alt+Tab), which the user passes through on the way to another app but never pastes into.
/// </para>
/// <para>
/// Footgun: <see cref="Start"/> needs a thread that runs a message loop, and the callback comes back on that thread only;
/// started on a thread without one, it installs but never reports. Call <see cref="Stop"/> from the same thread.
/// </para>
/// </remarks>
public sealed class ForegroundTracker : IDisposable
{
    /// <summary>
    /// Window classes of the shell's own surfaces, never a paste target: the taskbar (<c>Shell_TrayWnd</c>, and
    /// <c>Shell_SecondaryTrayWnd</c> on other monitors), the notification area's overflow (Windows 10's
    /// <c>NotifyIconOverflowWindow</c>, Windows 11's <c>TopLevelWindowForOverflowXamlIsland</c>: where BetterClipboard's own
    /// tray icon may sit), the windows of Start, Search, the notification center and Widgets
    /// (<c>Windows.UI.Core.CoreWindow</c>), Alt+Tab and Task View (Windows 11's <c>XamlExplorerHostIslandWindow</c>,
    /// Windows 10's <c>MultitaskingViewFrame</c> and <c>TaskSwitcherWnd</c>), the taskbar's thumbnails
    /// (<c>TaskListThumbnailWnd</c>), and the window Windows shows for a moment while it switches to some apps
    /// (<c>ForegroundStaging</c>).
    /// </summary>
    /// <remarks>
    /// The desktop (<c>Progman</c>, <c>WorkerW</c>) is not among them: Win+V pastes onto it too (files, for one). An app
    /// window of the Microsoft Store is reported as its frame (<c>ApplicationFrameWindow</c>), not as a <c>CoreWindow</c>.
    /// </remarks>
    private static readonly string[] ShellSurfaceClasses =
    [
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow",
        "MultitaskingViewFrame",
        "TaskSwitcherWnd",
        "TaskListThumbnailWnd",
        "ForegroundStaging",
    ];

    /// <summary>Called with each window of another app that takes the foreground, on the thread that started the tracker.</summary>
    private readonly Action<nint> onWindow;

    /// <summary>
    /// The callback, kept in a field for as long as the tracker lives: Windows holds only the function pointer made from it
    /// (<see cref="callbackPointer"/>), and a collected delegate would leave Windows calling freed code.
    /// </summary>
    private readonly WinEventProc callback;

    /// <summary>The native pointer to <see cref="callback"/> that <c>SetWinEventHook</c> takes.</summary>
    private readonly nint callbackPointer;

    /// <summary>The installed hook; 0 while not running.</summary>
    private nint hook;

    /// <summary>Creates a tracker that is not running yet.</summary>
    /// <param name="onWindow">
    /// Told each window of another app that takes the foreground (never 0, never one of this process's, never a shell
    /// surface), on the thread that called <see cref="Start"/>. An exception it throws is logged and swallowed: it must
    /// not unwind into Windows.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="onWindow"/> is <see langword="null"/>.</exception>
    public ForegroundTracker(Action<nint> onWindow)
    {
        ArgumentNullException.ThrowIfNull(onWindow);
        this.onWindow = onWindow;
        callback = OnWinEvent;
        callbackPointer = Marshal.GetFunctionPointerForDelegate(callback);
    }

    /// <summary>Whether the hook is installed.</summary>
    public bool IsRunning => hook != 0;

    /// <summary>
    /// Starts following the foreground window. Call it on a thread that runs a message loop (the UI thread): that is where
    /// the reports come back. Idempotent.
    /// </summary>
    /// <returns><see langword="true"/> when the hook is installed; <see langword="false"/> when Windows refused it (the pinned panel then keeps pasting into the window it was opened from).</returns>
    public bool Start()
    {
        if (hook == 0)
        {
            hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, callbackPointer, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        }

        return hook != 0;
    }

    /// <summary>Stops following (from the thread that started it). Idempotent; reports queued before it are dropped.</summary>
    public void Stop()
    {
        if (hook != 0)
        {
            // Cleared first: a report already queued on this thread's message loop finds the tracker stopped and is ignored.
            nint installed = hook;
            hook = 0;
            UnhookWinEvent(installed);
        }
    }

    /// <summary>Stops following (<see cref="Stop"/>).</summary>
    public void Dispose() => Stop();

    /// <summary>
    /// Whether a window class is one of the shell's own surfaces (the taskbar, the tray's overflow, Start, Search, Alt+Tab;
    /// <see cref="ShellSurfaceClasses"/> has the list): passed through on the way to another app, never pasted into.
    /// </summary>
    /// <param name="className">The class name; <see langword="null"/> or empty for an unreadable one (not a shell surface).</param>
    /// <returns><see langword="true"/> for a shell surface, ignoring case as Windows does for class names.</returns>
    public static bool IsShellSurface(string? className) =>
        !string.IsNullOrEmpty(className) && ShellSurfaceClasses.Contains(className, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The WinEvent callback: reports a new foreground window of another app, unless it is a shell surface. Never lets an
    /// exception escape into Windows.
    /// </summary>
    /// <param name="hook">The hook that fired.</param>
    /// <param name="eventType">The event (only <c>EVENT_SYSTEM_FOREGROUND</c> is hooked).</param>
    /// <param name="hwnd">The window that took the foreground.</param>
    /// <param name="idObject">The object: <c>OBJID_WINDOW</c> for the window itself.</param>
    /// <param name="idChild">The child (unused).</param>
    /// <param name="eventThread">The thread that raised it (unused).</param>
    /// <param name="eventTime">When (unused).</param>
    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        try
        {
            // A report queued before Stop (it arrives through the message loop) belongs to a tracker that no longer runs.
            if (this.hook == 0 || hook != this.hook || eventType != EVENT_SYSTEM_FOREGROUND || hwnd == 0 || idObject != OBJID_WINDOW)
            {
                return;
            }

            if (IsShellSurface(MonitorLookup.ClassNameOf(hwnd)))
            {
                return;
            }

            onWindow(hwnd);
        }
        catch (Exception ex)
        {
            // Content-free: the exception's kind and message, never which window.
            AppLog.Warn($"Following the foreground window failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
