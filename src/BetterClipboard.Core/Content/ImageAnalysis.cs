using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.Content;

/// <summary>
/// Result of decoding a clip's image: pixel dimensions plus a small PNG thumbnail for the list UI.
/// </summary>
/// <param name="Width">Full-resolution pixel width.</param>
/// <param name="Height">Full-resolution pixel height.</param>
/// <param name="ThumbnailPng">
/// Downscaled PNG (bounded box, aspect preserved) stored alongside the entry so the list never has to
/// decode megabyte DIBs while scrolling; <see langword="null"/> when thumbnail generation failed.
/// </param>
/// <param name="PixelHash">
/// Deduplication key computed from the <b>decoded pixels</b> (see <see cref="ContentHasher.ForImagePixels"/>),
/// so the same picture arriving as a DIB from one app and as a PNG from another (or from Windows'
/// history import) is recognized as one clip. <see langword="null"/> when pixels were not hashed
/// (huge images); the byte-based hash from classification is used then.
/// </param>
public sealed record ImageAnalysis(int Width, int Height, byte[]? ThumbnailPng, string? PixelHash = null);

/// <summary>
/// Platform service that decodes image formats (DIB/DIBV5/PNG) — implemented with WinRT imaging in the
/// Windows layer so Core stays OS-agnostic.
/// </summary>
public interface IImageAnalyzer
{
    /// <summary>
    /// Decodes the best image format present in <paramref name="capture"/> and produces dimensions and a thumbnail.
    /// </summary>
    /// <remarks>
    /// Called on the history worker thread for every image capture, so implementations must be
    /// thread-agile and must not touch UI objects. Failures should return <see langword="null"/>
    /// (the clip is still stored, just without a thumbnail) rather than throw — a corrupt bitmap
    /// from some app must never stall the capture pipeline.
    /// </remarks>
    /// <param name="capture">A capture containing at least one format for which <see cref="ClipFormatNames.IsImage"/> is true.</param>
    /// <param name="cancellationToken">Cancels decoding when the service shuts down.</param>
    /// <returns>The analysis, or <see langword="null"/> when no image could be decoded.</returns>
    Task<ImageAnalysis?> AnalyzeAsync(ClipCapture capture, CancellationToken cancellationToken);
}
