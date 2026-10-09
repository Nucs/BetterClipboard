namespace BetterClipboard.Core.Presentation;

/// <summary>
/// Decides whether the mouse may select the list row it is over ("hovering selects"): only once the cursor has really
/// moved since the keyboard, a reload or a summon last set the selection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a pointer event is not proof of movement.</b> Rows also come under a mouse that lies still: the list scrolls when
/// the arrow keys move the selection, a search or a new copy reorders it, and the panel itself appears under the cursor
/// when it is summoned. WinUI then tells the row under the cursor that the pointer entered it — it replays the last pointer
/// position after every frame that changed the picture (<c>CCoreServices::ReplayPreviousPointerUpdate</c> in
/// microsoft-ui-xaml, at most every 500 ms) — and Windows sends a mouse move to a window that shows up under the cursor.
/// Selecting on those events would take the selection away from the keyboard: Down would jump back to the row under the
/// mouse, and Enter after a typed search would paste that row instead of the best match.
/// </para>
/// <para>
/// <b>The rule.</b> Whoever acted last owns the selection. When the keyboard or the panel sets it, <see cref="Suspend(int, int, int)"/>
/// records where the cursor is; pointer events are then ignored until <see cref="Observe"/> sees the cursor farther than
/// the threshold from that spot on either axis. From then on the mouse owns the selection (<see cref="IsPointerActive"/>)
/// — every row it comes over is selected, also one that scrolls under it — until the next <c>Suspend</c>. A few pixels of
/// threshold keep a mouse that is nudged while typing from counting as moved.
/// </para>
/// <para>
/// Pure arithmetic on screen pixels, so it is unit-tested; the panel feeds it the cursor's position
/// (<c>GetCursorPos</c>), which unlike a position inside the window does not change when the window or its content moves.
/// Not thread-safe: UI thread only.
/// </para>
/// </remarks>
public sealed class PointerSelectionGate
{
    /// <summary>Where the cursor was when the selection was last set without the mouse (valid while <see cref="hasAnchor"/>).</summary>
    private int anchorX;

    /// <summary>The vertical half of <see cref="anchorX"/>.</summary>
    private int anchorY;

    /// <summary>
    /// Whether an anchor is known. It is not after a <see cref="Suspend(int)"/> without a position: the first position
    /// <see cref="Observe"/> sees then becomes the anchor.
    /// </summary>
    private bool hasAnchor;

    /// <summary>Travel from the anchor, in pixels on either axis, beyond which the mouse counts as moved.</summary>
    private int threshold;

    /// <summary>
    /// Whether the mouse owns the selection: a row the pointer comes over is selected. <see langword="false"/> from the
    /// start and after every <c>Suspend</c>, until the cursor has moved (<see cref="Observe"/>) or the mouse was used on
    /// purpose (<see cref="Activate"/>).
    /// </summary>
    public bool IsPointerActive { get; private set; }

    /// <summary>
    /// The keyboard or the panel set the selection: the mouse stops selecting until the cursor leaves the spot it is on now.
    /// </summary>
    /// <param name="x">The cursor's horizontal screen position now, in pixels.</param>
    /// <param name="y">The cursor's vertical screen position now, in pixels.</param>
    /// <param name="thresholdPixels">
    /// How far the cursor must travel on either axis to count as moved (a DIP threshold scaled for the monitor); negative
    /// values count as 0, where any change of position counts.
    /// </param>
    public void Suspend(int x, int y, int thresholdPixels)
    {
        IsPointerActive = false;
        anchorX = x;
        anchorY = y;
        hasAnchor = true;
        threshold = Math.Max(0, thresholdPixels);
    }

    /// <summary>
    /// Like <see cref="Suspend(int, int, int)"/> when the cursor's position cannot be read (a secure desktop is active): the
    /// first position <see cref="Observe"/> sees becomes the spot the cursor must leave, so one more event is ignored
    /// rather than a guess taken for movement.
    /// </summary>
    /// <param name="thresholdPixels">How far the cursor must travel on either axis to count as moved; negative values count as 0.</param>
    public void Suspend(int thresholdPixels)
    {
        IsPointerActive = false;
        hasAnchor = false;
        threshold = Math.Max(0, thresholdPixels);
    }

    /// <summary>
    /// Looks at the cursor's position when a pointer event arrives, and hands the selection to the mouse once the cursor
    /// has left the spot recorded by the last <c>Suspend</c>.
    /// </summary>
    /// <param name="x">The cursor's horizontal screen position, in pixels.</param>
    /// <param name="y">The cursor's vertical screen position, in pixels.</param>
    /// <returns>
    /// <see cref="IsPointerActive"/> after the look: <see langword="true"/> when the mouse may select the row it is over.
    /// Already active, it stays so wherever the cursor is.
    /// </returns>
    public bool Observe(int x, int y)
    {
        if (IsPointerActive)
        {
            return true;
        }

        if (!hasAnchor)
        {
            anchorX = x;
            anchorY = y;
            hasAnchor = true;
            return false;
        }

        // long: two screen coordinates can differ by more than an int holds only in theory, but the check costs nothing.
        if (Math.Abs((long)x - anchorX) > threshold || Math.Abs((long)y - anchorY) > threshold)
        {
            IsPointerActive = true;
        }

        return IsPointerActive;
    }

    /// <summary>
    /// The mouse was used on purpose without moving (its wheel turned over the list): it owns the selection from now on,
    /// like after a move.
    /// </summary>
    public void Activate() => IsPointerActive = true;
}
