using BetterClipboard.Core.Presentation;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the image overlays' arithmetic (<see cref="ImagePreviewLayout"/>): how the hover peek fits an image into a
/// box, and the fit/clamp/zoom-towards-the-cursor maths of the eye-icon viewer.
/// </summary>
public sealed class ImagePreviewLayoutTests
{
    /// <summary>A wide image larger than the box is scaled down to touch the limiting edge, aspect ratio preserved.</summary>
    [Fact]
    public void FitWithin_ScalesALargeImageDownKeepingAspect()
    {
        // 4000x2000 into a 1000x1000 box: width is the tighter ratio (0.25), so 1000x500.
        var size = ImagePreviewLayout.FitWithin(4000, 2000, 1000, 1000, allowUpscale: false);
        Assert.Equal(1000, size.Width, 6);
        Assert.Equal(500, size.Height, 6);
    }

    /// <summary>Without upscaling, an image smaller than the box keeps its natural size (the peek stays crisp).</summary>
    [Fact]
    public void FitWithin_LeavesASmallImageAloneWhenUpscalingIsOff()
    {
        var size = ImagePreviewLayout.FitWithin(200, 100, 1000, 1000, allowUpscale: false);
        Assert.Equal(200, size.Width, 6);
        Assert.Equal(100, size.Height, 6);
    }

    /// <summary>With upscaling on (the viewer's fit-to-window), a small image grows to fill the box.</summary>
    [Fact]
    public void FitWithin_EnlargesASmallImageWhenUpscalingIsOn()
    {
        // 200x100 into 1000x1000: height is the tighter ratio (x10), so 2000 would exceed width — width ratio x5 wins → 1000x500.
        var size = ImagePreviewLayout.FitWithin(200, 100, 1000, 1000, allowUpscale: true);
        Assert.Equal(1000, size.Width, 6);
        Assert.Equal(500, size.Height, 6);
    }

    /// <summary>A non-positive content or box yields an empty size, so callers can show nothing without a guard.</summary>
    [Theory]
    [InlineData(0, 100, 500, 500)]
    [InlineData(100, 0, 500, 500)]
    [InlineData(100, 100, 0, 500)]
    [InlineData(100, 100, 500, -1)]
    public void FitWithin_IsEmptyForNonPositiveInput(double cw, double ch, double mw, double mh)
    {
        Assert.True(ImagePreviewLayout.FitWithin(cw, ch, mw, mh, allowUpscale: true).IsEmpty);
    }

    /// <summary>The fit zoom is the tighter of the two viewport-to-content ratios.</summary>
    [Fact]
    public void FitZoom_IsTheTighterRatio()
    {
        // 1000x500 content in an 800x800 viewport: 0.8 vs 1.6 → 0.8.
        Assert.Equal(0.8, ImagePreviewLayout.FitZoom(1000, 500, 800, 800), 6);
    }

    /// <summary>A non-positive dimension falls back to 1.0 (show at natural size), never a divide-by-zero.</summary>
    [Fact]
    public void FitZoom_FallsBackToOne()
    {
        Assert.Equal(1.0, ImagePreviewLayout.FitZoom(0, 500, 800, 800), 6);
        Assert.Equal(1.0, ImagePreviewLayout.FitZoom(1000, 500, 0, 800), 6);
    }

    /// <summary>Clamp keeps a factor inside its range, tolerates reversed bounds, and maps NaN to the low bound.</summary>
    [Fact]
    public void ClampZoom_BoundsAndNaN()
    {
        Assert.Equal(1.5, ImagePreviewLayout.ClampZoom(1.5, 0.5, 4), 6);
        Assert.Equal(0.5, ImagePreviewLayout.ClampZoom(0.1, 0.5, 4), 6);
        Assert.Equal(4, ImagePreviewLayout.ClampZoom(9, 0.5, 4), 6);
        Assert.Equal(0.5, ImagePreviewLayout.ClampZoom(0.1, 4, 0.5), 6); // bounds reversed
        Assert.Equal(0.5, ImagePreviewLayout.ClampZoom(double.NaN, 0.5, 4), 6);
    }

    /// <summary>Zooming towards a point keeps that point's content pixel under the pointer.</summary>
    /// <remarks>
    /// Pointer 100 DIPs into the viewport, offset 50, so the scaled-content coordinate under it is 150. Doubling the zoom
    /// must double that coordinate to 300, leaving it under the still-100 pointer: offset becomes 200.
    /// </remarks>
    [Fact]
    public void ZoomOffset_KeepsThePointerAnchored()
    {
        double offset = ImagePreviewLayout.ZoomOffset(oldZoom: 1, newZoom: 2, pointerInViewport: 100, currentOffset: 50);
        Assert.Equal(200, offset, 6);

        // The anchored content coordinate (offset + pointer) ÷ zoom is the same pixel before and after.
        Assert.Equal((50 + 100) / 1.0, (200 + 100) / 2.0, 6);
    }

    /// <summary>A non-positive old zoom leaves the offset untouched instead of dividing by it.</summary>
    [Fact]
    public void ZoomOffset_GuardsAgainstZeroOldZoom()
    {
        Assert.Equal(50, ImagePreviewLayout.ZoomOffset(0, 2, 100, 50), 6);
    }
}
