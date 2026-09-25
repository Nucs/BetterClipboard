using BetterClipboard.Core.Content;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;

namespace BetterClipboard.Windows.Imaging;

/// <summary>
/// <see cref="IImageAnalyzer"/> for clipboard images: picks the best image format of a capture (PNG,
/// else DIBV5, else DIB), decodes it with WIC and renders a thumbnail for the flyout cards.
/// </summary>
public sealed class WinRtImageAnalyzer : IImageAnalyzer
{
    /// <summary>
    /// Thumbnail bounding box in pixels. Sized for a ~380 DIP wide card at 150–200% scaling, so the
    /// list stays crisp on high-DPI displays while thumbnails stay ~20–80 KB.
    /// </summary>
    public (uint Width, uint Height) ThumbnailBounds { get; init; } = (720, 400);

    /// <inheritdoc />
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public async Task<ImageAnalysis?> AnalyzeAsync(ClipCapture capture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var primary = ContentClassifier.FindPrimaryImage(capture);
        if (primary is null)
        {
            return null;
        }

        try
        {
            var encoded = primary.Name is ClipFormatNames.Png or ClipFormatNames.PngMime ? primary.Data : DibImage.ToBmpFile(primary.Data);
            return await ImageCodec.AnalyzeAsync(encoded, ThumbnailBounds.Width, ThumbnailBounds.Height, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Producer-supplied bitmaps can be anything; a decode failure only costs the thumbnail.
            AppLog.Warn($"Could not decode {primary.Name} ({primary.Data.Length:N0} bytes): {ex.Message}");
            return null;
        }
    }
}
