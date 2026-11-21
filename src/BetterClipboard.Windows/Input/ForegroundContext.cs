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

    /// <summary>Whether this rectangle and <paramref name="other"/> share at least one pixel (touching edges do not).</summary>
    /// <param name="other">The other rectangle.</param>
    /// <returns><see langword="true"/> when they overlap; an empty rectangle overlaps nothing.</returns>
    public bool Intersects(ScreenRect other) =>
        Left < other.Right && other.Left < Right && Top < other.Bottom && other.Top < Bottom &&
        Width > 0 && Height > 0 && other.Width > 0 && other.Height > 0;
}

/// <summary>
/// A lookup of an app's accessibility (MSAA) caret that a summon may still make (<see cref="ForegroundContext.WithAccessibleCaret"/>):
/// the window to ask, and the bounds of the window in front, which the caret must lie on.
/// </summary>
/// <param name="Window">
/// The window to ask: the focus window of the window in front, else the window in front itself (a console window, whose
/// thread <c>GetGUIThreadInfo</c> refuses).
/// </param>
/// <param name="Within">The bounds of the window in front, in physical screen pixels; a caret outside them is none.</param>
public readonly record struct AccessibleCaretQuery(nint Window, ScreenRect Within);

/// <summary>
/// Snapshot of "where the user was" at the moment the hotkey fired: the window to paste back into and
/// the caret/cursor position to place the flyout next to.
/// </summary>
/// <remarks>
/// <para>
/// Must be captured <b>before</b> BetterClipboard activates its own window — afterwards the foreground
/// window is ours.
/// </para>
/// <para>
/// <b>Where the caret is known</b> (measured 2026-11-21, issue #10, CLAUDE.md §2.64): apps with a Win32 system caret
/// (classic Win32 edit boxes such as Notepad's, the Run box and Explorer's rename box, WinForms, WPF), read with
/// <c>GetGUIThreadInfo</c>; and, for a summon that places the panel by the caret (<see cref="WithAccessibleCaret"/>), apps
/// whose caret only Microsoft Active Accessibility reports (<see cref="AccessibleCaret"/>): Chromium browsers and apps built
/// on Chromium, Firefox, and the classic console. Chromium never keeps a Win32 caret, and Firefox keeps one only while its
/// accessibility runs — until 2026-11-21 this remark said both did, and in them the panel followed the mouse. Windows
/// Terminal and XAML/WinUI text boxes report neither: for them <see cref="Caret"/> is <see langword="null"/> and the cursor
/// position is used.
/// </para>
/// </remarks>
/// <param name="TargetWindow">Foreground top-level window to paste into (0 when unknown, e.g. opened from the tray).</param>
/// <param name="TargetProcessId">Process of <paramref name="TargetWindow"/>.</param>
/// <param name="Caret">Caret rectangle in screen pixels, when the target exposes a system caret.</param>
/// <param name="Cursor">Mouse cursor position.</param>
public sealed record ForegroundContext(nint TargetWindow, uint TargetProcessId, ScreenRect? Caret, ScreenPoint Cursor)
{
    /// <summary>
    /// The accessibility (MSAA) caret lookup a summon may still make: set by <see cref="Capture"/> when the window in front
    /// has no Win32 caret; <see langword="null"/> otherwise, and once <see cref="WithAccessibleCaret"/> made it.
    /// </summary>
    /// <remarks>
    /// Recorded, not made, on the shortcut's thread: that thread also runs the keyboard hook that takes a shortcut over from
    /// Explorer, and a hook's callback cannot run while its thread waits. Every key on the PC would wait for the lookup, and
    /// Windows silently removes a hook that keeps it waiting too long (CLAUDE.md §2.2, §2.64). Not recorded for Start or
    /// Search (the panel opens beside them, caret or not), nor for this process's own windows (the lookup would wait for
    /// the thread that has to answer it).
    /// </remarks>
    public AccessibleCaretQuery? CaretQuery { get; init; }

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

    /// <summary>
    /// The shell surface that had the foreground when the context was captured — Start, Search, Quick settings, the
    /// notification center or Task View, which Windows keeps above every app window (<see cref="ShellSurfaces"/>,
    /// CLAUDE.md §2.61) — or <see cref="ShellSurface.None"/> for an app window. Set by <see cref="Capture"/> only.
    /// </summary>
    /// <remarks>
    /// A summon reads it to open beside Start or Search, or to close the surface before it opens; a context made after the
    /// surface was closed has it cleared, so a surface is never closed twice (<see cref="ShellSurfaces.CloseAsync"/>).
    /// </remarks>
    public ShellSurface Surface { get; init; }

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
    /// Captures the current foreground window, its caret, the cursor, and whether the window is a shell surface above every
    /// app window (<see cref="Surface"/>).
    /// </summary>
    /// <remarks>
    /// The caret comes from <c>GetGUIThreadInfo</c> when the window's thread has a Win32 caret. Without one, the context only
    /// records which window to ask for its accessibility caret (<see cref="CaretQuery"/>); a summon that places the panel by
    /// the caret asks on the UI thread (<see cref="WithAccessibleCaret"/>). Nothing here waits for another app, so it is safe
    /// on the shortcut's thread, which also runs the keyboard hook.
    /// </remarks>
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
        bool threadInfo = thread != 0 && GetGUIThreadInfo(thread, ref info);
        if (threadInfo && info.hwndCaret != 0)
        {
            var topLeft = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
            var bottomRight = new POINT { X = info.rcCaret.Right, Y = info.rcCaret.Bottom };
            if (ClientToScreen(info.hwndCaret, ref topLeft) && ClientToScreen(info.hwndCaret, ref bottomRight))
            {
                caret = new ScreenRect(topLeft.X, topLeft.Y, Math.Max(bottomRight.X, topLeft.X + 1), Math.Max(bottomRight.Y, topLeft.Y + 1));
            }
        }

        // Two calls into user32 (the window's band and rectangle): read here, on the shortcut's thread, while the surface is
        // still the window in front; the panel decides on the UI thread where it may open.
        var surface = ShellSurfaces.Describe(foreground);

        // No Win32 caret: Chromium (Chrome, Edge, apps built on it), Firefox (until its accessibility runs) and the classic
        // console report theirs only through MSAA (issue #10). Only noted here, for the summon to ask on the UI thread
        // (CaretQuery says why). Not for Start or Search (the panel opens beside them, caret or not), nor for this process's
        // own windows. A console window reports cmd.exe's thread, which GetGUIThreadInfo refuses: the window in front is asked.
        AccessibleCaretQuery? query = null;
        if (caret is null && foreground != 0 && surface.Kind == ShellSurfaceKind.None &&
            processId != (uint)Environment.ProcessId && GetWindowRect(foreground, out var bounds))
        {
            nint asked = threadInfo && info.hwndFocus != 0 ? info.hwndFocus : foreground;
            query = new AccessibleCaretQuery(asked, new ScreenRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
        }

        return new ForegroundContext(foreground, processId, caret, new ScreenPoint(cursor.X, cursor.Y))
        {
            CapturedAt = now,
            PageFaultsAtCapture = faults,
            Surface = surface,
            CaretQuery = query,
        };
    }

    /// <summary>
    /// This context with the accessibility (MSAA) caret of the app it names, when <see cref="Capture"/> found no Win32 caret
    /// there (<see cref="CaretQuery"/>): asks the app (<see cref="AccessibleCaret.Read"/>) and waits up to
    /// <paramref name="deadline"/> for the answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call it only for a summon that places the panel by the caret ("Next to the text cursor"): a lookup costs time (the
    /// first into a process up to about 150 ms in a test desktop, later ones 1–10 ms), and the first in a Firefox window
    /// starts Firefox's accessibility service.
    /// </para>
    /// <para>
    /// Blocks the calling thread, without pumping messages, for up to <paramref name="deadline"/>: call it on the UI thread
    /// that opens the panel, never on the shortcut's input thread (<see cref="CaretQuery"/> says why). Call it before the
    /// panel takes the foreground: a Chromium window that lost the keyboard reports its caret hidden. The summon's log line
    /// counts the wait in its "queued" stage.
    /// </para>
    /// </remarks>
    /// <param name="deadline">The longest wait (<see cref="AccessibleCaret.DefaultDeadline"/>).</param>
    /// <returns>
    /// This context when it has a caret already or nothing to ask; otherwise a copy with the caret the app reported, or none
    /// (<see cref="AccessibleCaret.Read"/> says when), and no query left, so asking again asks nothing.
    /// </returns>
    public ForegroundContext WithAccessibleCaret(TimeSpan deadline)
    {
        if (Caret is not null || CaretQuery is not { } query)
        {
            return this;
        }

        return this with { Caret = AccessibleCaret.Read(query.Window, query.Within, deadline), CaretQuery = null };
    }
}
