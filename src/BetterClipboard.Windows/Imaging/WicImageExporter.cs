using BetterClipboard.Core.Cli;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;

namespace BetterClipboard.Windows.Imaging;

/// <summary>
/// <see cref="IImageExporter"/> on WIC: serves an image item as a full-resolution PNG for
/// <c>bclip get --format png</c>, whatever format the producer put on the clipboard.
/// </summary>
/// <remarks>
/// Uses the same format preference as thumbnails (<see cref="ContentClassifier.FindPrimaryImage"/>:
/// PNG, then DIBV5, then DIB), so the exported picture is the one the panel shows. DIBs are wrapped into
/// a BMP file first because WIC decodes files, not bare DIBs.
/// </remarks>
public sealed class WicImageExporter : IImageExporter
{
    /// <inheritdoc />
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public async Task<byte[]?> ToPngAsync(IReadOnlyList<ClipFormatData> formats, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(formats);
        var primary = ContentClassifier.FindPrimaryImage(new ClipCapture { Formats = formats, CapturedAtUtc = DateTimeOffset.UtcNow });
        if (primary is null)
        {
            return null;
        }

        try
        {
            var encoded = primary.Name is ClipFormatNames.Png or ClipFormatNames.PngMime ? primary.Data : DibImage.ToBmpFile(primary.Data);
            return await ImageCodec.ToPngAsync(encoded, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Producer-supplied bitmaps can be malformed; report "no image" rather than failing the request.
            AppLog.Warn($"Could not export {primary.Name} ({primary.Data.Length:N0} bytes) as PNG: {ex.Message}");
            return null;
        }
    }
}
