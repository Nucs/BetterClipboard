using System.Buffers.Binary;

namespace BetterClipboard.Windows.Imaging;

/// <summary>
/// Byte-level helpers for device-independent bitmaps (<c>CF_DIB</c>/<c>CF_DIBV5</c>): a DIB is a BMP file
/// without its 14-byte <c>BITMAPFILEHEADER</c>.
/// </summary>
/// <remarks>
/// Clipboard DIBs come from arbitrary producers, so every offset is validated and malformed input raises
/// <see cref="FormatException"/> (callers treat that as "no thumbnail") instead of reading out of bounds.
/// </remarks>
public static class DibImage
{
    private const int FileHeaderSize = 14;
    private const uint BI_BITFIELDS = 3;
    private const uint BI_ALPHABITFIELDS = 6;
    private const int BitmapV5HeaderSize = 124;

    /// <summary>
    /// Wraps a DIB in a <c>BITMAPFILEHEADER</c> so image decoders (WIC) can read it as a .bmp file.
    /// </summary>
    /// <param name="dib">Raw <c>CF_DIB</c>/<c>CF_DIBV5</c> bytes.</param>
    /// <returns>A complete BMP file.</returns>
    /// <exception cref="FormatException">The header is malformed or truncated.</exception>
    public static byte[] ToBmpFile(ReadOnlySpan<byte> dib)
    {
        int pixelOffset = GetPixelDataOffset(dib);
        var file = new byte[FileHeaderSize + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(2), (uint)file.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(10), (uint)(FileHeaderSize + pixelOffset));
        dib.CopyTo(file.AsSpan(FileHeaderSize));
        return file;
    }

    /// <summary>
    /// Computes where the pixel array starts inside a DIB: header + optional bit masks + color table.
    /// </summary>
    /// <param name="dib">Raw DIB bytes.</param>
    /// <returns>Offset of the first pixel byte.</returns>
    /// <exception cref="FormatException">The header is malformed or truncated.</exception>
    public static int GetPixelDataOffset(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 12)
        {
            throw new FormatException("DIB is shorter than the smallest bitmap header.");
        }

        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        int offset;
        if (headerSize == 12)
        {
            // BITMAPCOREHEADER: RGBTRIPLE (3-byte) palette entries.
            int coreBits = BinaryPrimitives.ReadUInt16LittleEndian(dib[10..]);
            offset = 12 + (coreBits <= 8 ? (1 << coreBits) * 3 : 0);
        }
        else if (headerSize is 40 or 52 or 56 or 64 or 108 or 124)
        {
            if (dib.Length < 40)
            {
                throw new FormatException("DIB header truncated.");
            }

            int bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
            uint compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
            uint colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);

            // Only the 40-byte header stores masks after itself; V2+ headers embed them.
            int masks = headerSize == 40 ? compression switch
            {
                BI_BITFIELDS => 12,
                BI_ALPHABITFIELDS => 16,
                _ => 0,
            } : 0;

            long colors = colorsUsed != 0 ? colorsUsed : bitCount <= 8 ? 1L << bitCount : 0;
            if (colors > 256 && bitCount <= 8)
            {
                throw new FormatException("DIB declares an impossible palette size.");
            }

            long computed = headerSize + masks + (colors * 4);
            if (computed > int.MaxValue)
            {
                throw new FormatException("DIB palette size overflows.");
            }

            offset = (int)computed;
        }
        else
        {
            throw new FormatException($"Unknown DIB header size {headerSize}.");
        }

        if (offset > dib.Length)
        {
            throw new FormatException("DIB is truncated before its pixel data.");
        }

        return offset;
    }

    /// <summary>
    /// Builds a 32-bpp bottom-up <c>CF_DIBV5</c> (sRGB, straight alpha) from top-down BGRA pixels —
    /// the format that preserves transparency when pasting into Office, browsers and design tools.
    /// </summary>
    /// <remarks>
    /// Bottom-up (positive height) is used deliberately: some older consumers mishandle top-down DIBs.
    /// The system synthesizes <c>CF_DIB</c> and <c>CF_BITMAP</c> from this for apps that want those.
    /// </remarks>
    /// <param name="bgra">Top-down BGRA8 pixels with straight (non-premultiplied) alpha, <c>width*height*4</c> bytes.</param>
    /// <param name="width">Width in pixels (&gt; 0).</param>
    /// <param name="height">Height in pixels (&gt; 0).</param>
    /// <returns>The DIBV5 bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Dimensions are not positive or too large.</exception>
    /// <exception cref="ArgumentException">The pixel buffer length does not match the dimensions.</exception>
    public static byte[] CreateDibV5(ReadOnlySpan<byte> bgra, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        long stride = (long)width * 4;
        long imageSize = stride * height;
        if (imageSize > int.MaxValue - BitmapV5HeaderSize)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image too large for a DIB.");
        }

        if (bgra.Length != imageSize)
        {
            throw new ArgumentException($"Expected {imageSize} pixel bytes, got {bgra.Length}.", nameof(bgra));
        }

        var dib = new byte[BitmapV5HeaderSize + imageSize];
        var header = dib.AsSpan(0, BitmapV5HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header, BitmapV5HeaderSize);          // bV5Size
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);                  // bV5Width
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height);                 // bV5Height (+ = bottom-up)
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);                    // bV5Planes
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32);                   // bV5BitCount
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], BI_BITFIELDS);         // bV5Compression
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)imageSize);      // bV5SizeImage
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], 3780);                  // bV5XPelsPerMeter (96 DPI)
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], 3780);                  // bV5YPelsPerMeter
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], 0x00FF0000);           // bV5RedMask
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..], 0x0000FF00);           // bV5GreenMask
        BinaryPrimitives.WriteUInt32LittleEndian(header[48..], 0x000000FF);           // bV5BlueMask
        BinaryPrimitives.WriteUInt32LittleEndian(header[52..], 0xFF000000);           // bV5AlphaMask
        BinaryPrimitives.WriteUInt32LittleEndian(header[56..], 0x73524742);           // bV5CSType = 'sRGB'
        BinaryPrimitives.WriteUInt32LittleEndian(header[108..], 4);                   // bV5Intent = LCS_GM_IMAGES

        // Flip rows: source is top-down, DIB rows are stored bottom-up.
        var pixels = dib.AsSpan(BitmapV5HeaderSize);
        int rowBytes = (int)stride;
        for (int y = 0; y < height; y++)
        {
            bgra.Slice(y * rowBytes, rowBytes).CopyTo(pixels.Slice((height - 1 - y) * rowBytes, rowBytes));
        }

        return dib;
    }
}
