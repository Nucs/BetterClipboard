namespace BetterClipboard.Core.Presentation;

/// <summary>
/// A rendered size in DIPs: how large an image is drawn once fitted into a box.
/// </summary>
/// <param name="Width">The drawn width in DIPs (0 when there is nothing to draw, or no room).</param>
/// <param name="Height">The drawn height in DIPs.</param>
public readonly record struct PreviewSize(double Width, double Height)
{
    /// <summary>Whether the size is empty (either dimension is zero or negative), i.e. nothing to show.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// The pure arithmetic behind the image overlays (<c>ClipboardFlyout.ImagePreview.cs</c>): how large the hover peek
/// draws an image inside the monitor, and the fit/zoom/pan maths of the eye-icon zoom viewer. Kept here, free of UI
/// types, so the rules are unit-tested and behave identically to the panel (the project's convention for presentation
/// logic, like <see cref="TabStripScroll"/>).
/// </summary>
/// <remarks>
/// <para>
/// All values are DIPs unless named a zoom factor. "Content" is the image at its natural pixel size expressed in DIPs
/// (pixels ÷ the monitor scale), so a factor of 1.0 draws it pixel-for-pixel. The viewer scales that content with a
/// <c>ZoomFactor</c> and scrolls it by an offset measured in the <i>scaled</i> content's DIPs — exactly a
/// WinUI <c>ScrollViewer</c>'s <c>HorizontalOffset</c>/<c>VerticalOffset</c> and <c>ZoomFactor</c> — so
/// <see cref="ZoomOffset"/> speaks that coordinate space directly.
/// </para>
/// <para>
/// <b>Footguns handled here so callers need no guards:</b> a zero or negative content/box/viewport (an image whose
/// dimensions are not known yet, a window laid out to nothing) yields an empty size or a safe factor of 1, and a
/// non-positive old zoom leaves the offset untouched rather than dividing by it.
/// </para>
/// </remarks>
public static class ImagePreviewLayout
{
    /// <summary>
    /// The fraction of the monitor's work area the hover peek may fill (its "80% max"): large enough to see the picture,
    /// with a margin so it never reaches the screen edges and reads as a floating tooltip rather than a full-screen takeover.
    /// </summary>
    public const double PeekMaxFraction = 0.8;

    /// <summary>
    /// Scales a content size down (and, when asked, up) to fit inside a box while keeping its aspect ratio — the hover
    /// peek's "sizing to fit content".
    /// </summary>
    /// <param name="contentWidth">The image's natural width in DIPs.</param>
    /// <param name="contentHeight">The image's natural height in DIPs.</param>
    /// <param name="maxWidth">The box's width in DIPs.</param>
    /// <param name="maxHeight">The box's height in DIPs.</param>
    /// <param name="allowUpscale">
    /// <see langword="true"/> to enlarge an image smaller than the box (the viewer's fit-to-window); <see langword="false"/>
    /// to leave a small image at its natural size (the peek, which must stay crisp: upscaling a small clip would blur it).
    /// </param>
    /// <returns>The drawn size, never exceeding the box; <see cref="PreviewSize.IsEmpty"/> when there is nothing to draw or no room.</returns>
    public static PreviewSize FitWithin(double contentWidth, double contentHeight, double maxWidth, double maxHeight, bool allowUpscale)
    {
        // Anything non-positive (dimensions unknown, a box collapsed during layout) has no sensible fit: show nothing.
        if (contentWidth <= 0 || contentHeight <= 0 || maxWidth <= 0 || maxHeight <= 0)
        {
            return new PreviewSize(0, 0);
        }

        // The tighter of the two ratios keeps the whole image inside the box; capping at 1 is what keeps a small image crisp.
        double scale = Math.Min(maxWidth / contentWidth, maxHeight / contentHeight);
        if (!allowUpscale)
        {
            scale = Math.Min(scale, 1.0);
        }

        return new PreviewSize(contentWidth * scale, contentHeight * scale);
    }

    /// <summary>
    /// The zoom factor that makes a content size fit entirely within a viewport (the eye viewer's initial "fit to window"):
    /// the tighter of the width and height ratios.
    /// </summary>
    /// <param name="contentWidth">The image's natural width in DIPs.</param>
    /// <param name="contentHeight">The image's natural height in DIPs.</param>
    /// <param name="viewportWidth">The viewport's width in DIPs.</param>
    /// <param name="viewportHeight">The viewport's height in DIPs.</param>
    /// <returns>The fit factor; 1.0 when any dimension is non-positive (a safe "show at natural size" fallback).</returns>
    public static double FitZoom(double contentWidth, double contentHeight, double viewportWidth, double viewportHeight)
    {
        if (contentWidth <= 0 || contentHeight <= 0 || viewportWidth <= 0 || viewportHeight <= 0)
        {
            return 1.0;
        }

        return Math.Min(viewportWidth / contentWidth, viewportHeight / contentHeight);
    }

    /// <summary>
    /// Keeps a zoom factor within the viewer's limits, tolerating bounds given in either order and a NaN input.
    /// </summary>
    /// <param name="zoom">The requested factor (NaN counts as <paramref name="minZoom"/>).</param>
    /// <param name="minZoom">One bound.</param>
    /// <param name="maxZoom">The other bound.</param>
    /// <returns>The factor clamped to the inclusive range of the two bounds.</returns>
    public static double ClampZoom(double zoom, double minZoom, double maxZoom)
    {
        // Order the bounds defensively: callers derive them from a fit factor that can land either side of 1.
        double lo = Math.Min(minZoom, maxZoom);
        double hi = Math.Max(minZoom, maxZoom);
        return double.IsNaN(zoom) ? lo : Math.Clamp(zoom, lo, hi);
    }

    /// <summary>
    /// The new scroll offset (along one axis) that keeps the content point under the pointer fixed while the zoom changes
    /// — "zoom towards the cursor", so the pixel beneath the mouse does not drift as the wheel turns.
    /// </summary>
    /// <param name="oldZoom">The zoom factor before the change.</param>
    /// <param name="newZoom">The zoom factor after the change.</param>
    /// <param name="pointerInViewport">The pointer's position from the viewport's edge on this axis, in DIPs.</param>
    /// <param name="currentOffset">The current scroll offset on this axis, in scaled-content DIPs.</param>
    /// <returns>
    /// The offset to apply after zooming. Callers still clamp it to the scrollable range; a scroll viewer does that
    /// itself. When <paramref name="oldZoom"/> is not positive the offset is returned unchanged (nothing to scale from).
    /// </returns>
    /// <remarks>
    /// Derivation: the scaled-content coordinate under the pointer is <c>currentOffset + pointerInViewport</c>. Scaling the
    /// underlying pixel by <c>newZoom/oldZoom</c> moves that coordinate by the same ratio; subtracting the pointer position
    /// again yields the offset that leaves the pointer over the same pixel.
    /// </remarks>
    public static double ZoomOffset(double oldZoom, double newZoom, double pointerInViewport, double currentOffset)
    {
        if (oldZoom <= 0)
        {
            return currentOffset;
        }

        double ratio = newZoom / oldZoom;
        return ((currentOffset + pointerInViewport) * ratio) - pointerInViewport;
    }
}
