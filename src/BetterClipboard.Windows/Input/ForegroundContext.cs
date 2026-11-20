using BetterClipboard.Windows.Diagnostics;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Input;

/// <summary>A point in physical screen pixels.</summary>
/// <param name="X">X coordinate.</param>
/// <param name="Y">Y coordinate.</param>
public readonly record struct ScreenPoint(int X, int Y);

/// <summary>A rectangle in physical screen pixels (exclusive right/bottom).</summary>
/// <param name="Left">Left edge.</param>
/// <param name="Top">Top edge.</param>
/// <param name="Right">Right edge.</param>
/// <param name="Bottom">Bottom edge.</param>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Width in pixels.</summary>
    public int Width => Right - Left;

    /// <summary>Height in pixels.</summary>
    public int Height => Bottom - Top;
}

/// <summary>
/// Snapshot of "where the user was" at the moment the hotkey fired: the window to paste back into and
/// the caret/cursor position to place the flyout next to.
/// </summary>
/// <remarks>
/// Must be captured <b>before</b> BetterClipboard activates its own window — afterwards the foreground
/// window is ours. The caret is only known for apps using the Win32 system caret (classic Win32, WPF,
/// WinForms, and Chromium/Firefox, which maintain an accessibility caret); for others
/// <see cref="Caret"/> is <see langword="null"/> and the cursor position is used.
/// </remarks>
/// <param name="TargetWindow">Foreground top-level window to paste into (0 when unknown, e.g. opened from the tray).</param>
/// <param name="TargetProcessId">Process of <paramref name="TargetWindow"/>.</param>
/// <param name="Caret">Caret rectangle in screen pixels, when the target exposes a system caret.</param>
/// <param name="Cursor">Mouse cursor position.</param>
public sealed record ForegroundContext(nint TargetWindow, uint TargetProcessId, ScreenRect? Caret, ScreenPoint Cursor)
{
    /// <summary>
    /// When the context was captured, as a <see cref="System.Diagnostics.Stopwatch"/> timestamp — for a shortcut, a few
    /// microseconds after the key reached BetterClipboard — so the panel can log how long it took to appear. 0 for a context
    /// built without <see cref="Capture"/> or <see cref="CursorOnly"/> (tests): nothing is measured from it then.
    /// </summary>
    public long CapturedAt { get; init; }

    /// <summary>
    /// This process's page-fault count when the context was captured (<see cref="ProcessMemory.PageFaults"/>), so the
    /// panel can log how many pages a slow summon had to bring back (CLAUDE.md §2.53). 0 for a context built without
    /// <see cref="Capture"/> or <see cref="CursorOnly"/>: no count is logged then.
    /// </summary>
    public uint PageFaultsAtCapture { get; init; }

    /// <summary>A context with no paste target, positioned at the mouse cursor (tray/menu invocations).</summary>
    /// <returns>The context.</returns>
    public static ForegroundContext CursorOnly()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        uint faults = ProcessMemory.PageFaults();
        GetCursorPos(out var cursor);
        return new ForegroundContext(0, 0, null, new ScreenPoint(cursor.X, cursor.Y)) { CapturedAt = now, PageFaultsAtCapture = faults };
    }

    /// <summary>
    /// A context naming <paramref name="window"/> as the paste target, with the mouse cursor now and no caret: the window of
    /// another app the user went to while a pinned panel stayed open (<see cref="ForegroundTracker"/>, CLAUDE.md §2.55), which
    /// that panel's next paste goes into.
    /// </summary>
    /// <remarks>
    /// No caret and no timestamp: nothing is placed next to it and no summon is timed from it. The window may close before
    /// the paste; the paste then reports it gone (<see cref="PasteOutcome.NoTarget"/>), as for any target.
    /// </remarks>
    /// <param name="window">The window; 0 makes a context with no target, like <see cref="CursorOnly"/>.</param>
    /// <returns>The context.</returns>
    public static ForegroundContext ForWindow(nint window)
    {
        GetCursorPos(out var cursor);
        uint processId = 0;
        if (window != 0)
        {
            GetWindowThreadProcessId(window, out processId);
        }

        return new ForegroundContext(window, processId, null, new ScreenPoint(cursor.X, cursor.Y));
    }

    /// <summary>
    /// Captures the current foreground window, its caret and the cursor.
    /// </summary>
    /// <returns>The context.</returns>
    public static ForegroundContext Capture()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();

        // Read first, with the time: the faults the summon takes from here on (this thread's included) are its own.
        uint faults = ProcessMemory.PageFaults();
        GetCursorPos(out var cursor);
        nint foreground = GetForegroundWindow();
        uint thread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
        GetWindowThreadProcessId(foreground, out uint processId);

        ScreenRect? caret = null;
        var info = new GUITHREADINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
        if (thread != 0 && GetGUIThreadInfo(thread, ref info) && info.hwndCaret != 0)
        {
            var topLeft = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
            var bottomRight = new POINT { X = info.rcCaret.Right, Y = info.rcCaret.Bottom };
            if (ClientToScreen(info.hwndCaret, ref topLeft) && ClientToScreen(info.hwndCaret, ref bottomRight))
            {
                caret = new ScreenRect(topLeft.X, topLeft.Y, Math.Max(bottomRight.X, topLeft.X + 1), Math.Max(bottomRight.Y, topLeft.Y + 1));
            }
        }

        return new ForegroundContext(foreground, processId, caret, new ScreenPoint(cursor.X, cursor.Y)) { CapturedAt = now, PageFaultsAtCapture = faults };
    }
}
