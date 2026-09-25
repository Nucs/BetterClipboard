using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Input;

/// <summary>
/// Screen positions of pointers, for moving a caption-less window by dragging its background.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not Windows' own move loop</b> (<c>WM_NCLBUTTONDOWN</c> + <c>HTCAPTION</c>, the classic trick):
/// verified live on 2026-09-25 that in a WinUI 3 window the loop never ends. WinUI takes mouse input as
/// pointer messages, so the button-up goes to the XAML island instead of the loop, and the window stays
/// glued to the cursor until the next click. The drag is therefore done with pointer capture and
/// <see cref="WindowDragTracker"/>, where the release always arrives.
/// </para>
/// <para>
/// <b>Why screen coordinates from Win32:</b> a pointer's position <i>inside</i> the window changes as the
/// window moves under it, so deriving the screen position from it feeds the window's own movement back
/// into the drag (jitter, or drift if the island's origin updates late). Cursor and pointer-frame
/// positions are independent of the window.
/// </para>
/// </remarks>
public static class ScreenPointer
{
    /// <summary>
    /// The mouse cursor's position in physical screen pixels (per-monitor aware, so it matches window
    /// positions directly).
    /// </summary>
    /// <returns>The position, or <see langword="null"/> when it cannot be read (e.g. a secure desktop is active).</returns>
    public static ScreenPoint? Cursor() => GetCursorPos(out var point) ? new ScreenPoint(point.X, point.Y) : null;

    /// <summary>
    /// The screen position of a touch or pen pointer at the frame currently being processed on this thread
    /// (call it from that pointer's event handler).
    /// </summary>
    /// <param name="pointerId">The system pointer id (XAML's <c>Pointer.PointerId</c>).</param>
    /// <param name="position">The position in physical screen pixels.</param>
    /// <returns><see langword="false"/> when Windows has no frame for that pointer (then use another source).</returns>
    public static bool TryGetPointer(uint pointerId, out ScreenPoint position)
    {
        if (GetPointerInfo(pointerId, out var info))
        {
            position = new ScreenPoint(info.ptPixelLocation.X, info.ptPixelLocation.Y);
            return true;
        }

        position = default;
        return false;
    }
}

/// <summary>
/// Turns a background drag (mouse, touch or pen) into window positions. Pure arithmetic, so it is
/// unit-tested; the caller feeds it screen positions (see <see cref="ScreenPointer"/>) and moves the window.
/// </summary>
/// <remarks>
/// Only the pointer's movement since the press is applied to the window's position at the press, so any
/// constant offset in how positions are measured cancels out. Nothing moves until the pointer travels
/// more than <see cref="ThresholdPixels"/> on either axis, so a click or tap on the background stays one.
/// <see cref="WindowStart"/> lets Esc put the window back, like cancelling a title-bar drag.
/// </remarks>
public sealed class WindowDragTracker
{
    private ScreenPoint pointerStart;

    /// <summary>
    /// Creates a tracker.
    /// </summary>
    /// <param name="thresholdPixels">Travel before the window starts to follow (a DIP threshold scaled for the monitor); negative values count as 0.</param>
    public WindowDragTracker(int thresholdPixels)
    {
        ThresholdPixels = Math.Max(0, thresholdPixels);
    }

    /// <summary>Travel, in pixels on either axis, before the window starts to follow.</summary>
    public int ThresholdPixels { get; }

    /// <summary>Whether a drag is in progress (between <see cref="Begin"/> and <see cref="End"/>).</summary>
    public bool IsTracking { get; private set; }

    /// <summary>Whether the current drag passed the threshold, i.e. the window has been moved.</summary>
    public bool IsMoving { get; private set; }

    /// <summary>The window's position when the drag began (where Esc puts it back).</summary>
    public ScreenPoint WindowStart { get; private set; }

    /// <summary>
    /// Starts tracking a press.
    /// </summary>
    /// <param name="pointer">Pointer position on screen at the press.</param>
    /// <param name="window">Window position on screen at the press.</param>
    public void Begin(ScreenPoint pointer, ScreenPoint window)
    {
        pointerStart = pointer;
        WindowStart = window;
        IsTracking = true;
        IsMoving = false;
    }

    /// <summary>
    /// Follows the pointer.
    /// </summary>
    /// <param name="pointer">Current pointer position on screen.</param>
    /// <returns>Where the window must move now, or <see langword="null"/> when not tracking or still within the threshold.</returns>
    public ScreenPoint? Update(ScreenPoint pointer)
    {
        if (!IsTracking)
        {
            return null;
        }

        int dx = pointer.X - pointerStart.X;
        int dy = pointer.Y - pointerStart.Y;
        if (!IsMoving && Math.Abs(dx) <= ThresholdPixels && Math.Abs(dy) <= ThresholdPixels)
        {
            return null;
        }

        // Once past the threshold the window follows the full offset (no jump by the threshold amount).
        IsMoving = true;
        return new ScreenPoint(WindowStart.X + dx, WindowStart.Y + dy);
    }

    /// <summary>
    /// Ends the drag (release, cancel or capture loss).
    /// </summary>
    /// <returns>Whether the window was moved during this drag.</returns>
    public bool End()
    {
        bool moved = IsMoving;
        IsTracking = false;
        IsMoving = false;
        return moved;
    }
}
