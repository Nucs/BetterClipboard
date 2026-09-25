using System.Runtime.InteropServices.WindowsRuntime;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace BetterClipboard.Windows.Imaging;

/// <summary>
/// Image decoding/encoding on top of WinRT <c>Windows.Graphics.Imaging</c> (WIC under the hood): BMP, PNG,
/// JPEG, GIF, TIFF, HEIF (with the extension) etc.
/// </summary>
/// <remarks>
/// The WinRT imaging classes are agile, so these methods may run on any thread (the history worker uses
/// the thread pool). All decoding is done in memory; nothing is written to disk.
/// </remarks>
public static class ImageCodec
{
    /// <summary>
    /// Largest image (in pixels) whose full-resolution pixels are hashed for deduplication. Above it
    /// (e.g. 8K+ renders: 130+ MB of BGRA) the byte-based hash is kept to bound memory and CPU.
    /// </summary>
    public const long MaxPixelsForPixelHash = 40_000_000;

    /// <summary>
    /// Decodes an encoded image and produces its dimensions, a pixel-based dedupe hash and a PNG thumbnail
    /// that fits inside <paramref name="maxThumbnailWidth"/> × <paramref name="maxThumbnailHeight"/> (never upscaled).
    /// </summary>
    /// <param name="encoded">A complete image file (PNG, BMP, …).</param>
    /// <param name="maxThumbnailWidth">Thumbnail bounding width in pixels.</param>
    /// <param name="maxThumbnailHeight">Thumbnail bounding height in pixels.</param>
    /// <param name="cancellationToken">Cancels between WinRT steps.</param>
    /// <returns>The analysis.</returns>
    /// <exception cref="Exception">WIC rejects the data (COMException with WINCODEC_ERR_* HRESULTs).</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static async Task<ImageAnalysis> AnalyzeAsync(byte[] encoded, uint maxThumbnailWidth, uint maxThumbnailHeight, CancellationToken cancellationToken)
    {
        using var input = await ToStreamAsync(encoded).ConfigureAwait(false);
        var decoder = await BitmapDecoder.CreateAsync(input).AsTask(cancellationToken).ConfigureAwait(false);
        uint width = decoder.PixelWidth;
        uint height = decoder.PixelHeight;

        string? pixelHash = null;
        if ((long)width * height <= MaxPixelsForPixelHash)
        {
            // Straight alpha + sRGB so the same picture hashes identically whatever container it came in.
            var full = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
                    ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb)
                .AsTask(cancellationToken).ConfigureAwait(false);
            pixelHash = ContentHasher.ForImagePixels((int)width, (int)height, full.DetachPixelData());
        }

        double scale = Math.Min(1.0, Math.Min((double)maxThumbnailWidth / width, (double)maxThumbnailHeight / height));
        uint thumbWidth = Math.Max(1, (uint)Math.Round(width * scale));
        uint thumbHeight = Math.Max(1, (uint)Math.Round(height * scale));

        var transform = new BitmapTransform
        {
            ScaledWidth = thumbWidth,
            ScaledHeight = thumbHeight,
            // Fant = high-quality downscaling (area averaging); the default nearest-neighbor would alias text in screenshots.
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb)
            .AsTask(cancellationToken).ConfigureAwait(false);

        var png = await EncodePngAsync(pixels.DetachPixelData(), thumbWidth, thumbHeight, BitmapAlphaMode.Premultiplied, cancellationToken).ConfigureAwait(false);
        return new ImageAnalysis((int)width, (int)height, png, pixelHash);
    }

    /// <summary>
    /// Converts any encoded image (e.g. the bitmap stream of a Windows history item) into the clipboard
    /// formats BetterClipboard stores for images: <c>PNG</c> (lossless, alpha) followed by <c>CF_DIBV5</c>
    /// (what classic Win32 apps paste).
    /// </summary>
    /// <param name="encoded">A complete image file.</param>
    /// <param name="cancellationToken">Cancels between WinRT steps.</param>
    /// <returns>The two formats in placement order.</returns>
    /// <exception cref="Exception">WIC rejects the data.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static async Task<IReadOnlyList<ClipFormatData>> ToClipboardFormatsAsync(byte[] encoded, CancellationToken cancellationToken)
    {
        using var input = await ToStreamAsync(encoded).ConfigureAwait(false);
        var decoder = await BitmapDecoder.CreateAsync(input).AsTask(cancellationToken).ConfigureAwait(false);
        uint width = decoder.PixelWidth;
        uint height = decoder.PixelHeight;
        var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb)
            .AsTask(cancellationToken).ConfigureAwait(false);
        var bgra = pixels.DetachPixelData();

        // Keep an existing PNG byte-for-byte (no re-encode, no quality change); otherwise encode one.
        bool isPng = encoded.Length > 8 && encoded[0] == 0x89 && encoded[1] == (byte)'P' && encoded[2] == (byte)'N' && encoded[3] == (byte)'G';
        var png = isPng ? encoded : await EncodePngAsync(bgra, width, height, BitmapAlphaMode.Straight, cancellationToken).ConfigureAwait(false);
        var dib = DibImage.CreateDibV5(bgra, (int)width, (int)height);
        return [new ClipFormatData(ClipFormatNames.Png, png), new ClipFormatData(ClipFormatNames.DibV5, dib)];
    }

    /// <summary>Encodes BGRA8 pixels as PNG.</summary>
    /// <param name="bgra">Top-down BGRA8 pixels.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="alphaMode">How <paramref name="bgra"/>'s alpha is encoded.</param>
    /// <param name="cancellationToken">Cancels the flush.</param>
    /// <returns>PNG bytes.</returns>
    private static async Task<byte[]> EncodePngAsync(byte[] bgra, uint width, uint height, BitmapAlphaMode alphaMode, CancellationToken cancellationToken)
    {
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output).AsTask(cancellationToken).ConfigureAwait(false);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, alphaMode, width, height, 96, 96, bgra);
        await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
        output.Seek(0);
        // Use the buffer ReadAsync returns: WinRT may hand back a different IBuffer than the one passed in.
        var buffer = await output.ReadAsync(new global::Windows.Storage.Streams.Buffer((uint)output.Size), (uint)output.Size, InputStreamOptions.None)
            .AsTask(cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>Wraps bytes in a seekable WinRT stream positioned at 0.</summary>
    /// <param name="bytes">Data.</param>
    /// <returns>The stream (caller disposes).</returns>
    private static async Task<InMemoryRandomAccessStream> ToStreamAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer()).AsTask().ConfigureAwait(false);
        stream.Seek(0);
        return stream;
    }
}
