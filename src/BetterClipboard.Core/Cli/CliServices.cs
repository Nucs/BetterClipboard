using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.Cli;

/// <summary>
/// Places formats on the system clipboard on behalf of the command line. Implemented by the Windows
/// layer's clipboard monitor, whose echo suppression keeps the write from being recorded twice.
/// </summary>
public interface IClipboardWriter
{
    /// <summary>
    /// Replaces the clipboard content.
    /// </summary>
    /// <param name="formats">Formats in placement order.</param>
    /// <returns>A task completing once the clipboard is closed again.</returns>
    /// <exception cref="IOException">Another application kept the clipboard locked.</exception>
    Task WriteAsync(IReadOnlyList<ClipFormatData> formats);
}

/// <summary>
/// Produces a full-resolution PNG for an image item stored only as a device-independent bitmap
/// (<c>CF_DIB</c>/<c>CF_DIBV5</c>). Implemented with WIC in the Windows layer.
/// </summary>
public interface IImageExporter
{
    /// <summary>
    /// Encodes the item's best bitmap format as PNG.
    /// </summary>
    /// <param name="formats">The item's stored formats.</param>
    /// <param name="cancellationToken">Cancels the encode.</param>
    /// <returns>PNG bytes, or <see langword="null"/> when no decodable bitmap is present.</returns>
    Task<byte[]?> ToPngAsync(IReadOnlyList<ClipFormatData> formats, CancellationToken cancellationToken);
}
