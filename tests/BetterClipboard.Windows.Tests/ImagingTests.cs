using System.Buffers.Binary;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Windows.Imaging;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests for DIB byte math and WinRT (WIC) imaging, including the "same pixels = same clip" guarantee.
/// </summary>
public sealed class ImagingTests
{
    /// <summary>A 40-byte header 24-bpp DIB has pixels right after the header.</summary>
    [Fact]
    public void PixelOffset_InfoHeader24bpp()
    {
        Assert.Equal(40, DibImage.GetPixelDataOffset(InfoHeader(bitCount: 24, compression: 0)));
    }

    /// <summary>BI_BITFIELDS with a 40-byte header carries three DWORD masks after it.</summary>
    [Fact]
    public void PixelOffset_BitfieldsMasks()
    {
        Assert.Equal(52, DibImage.GetPixelDataOffset(InfoHeader(bitCount: 32, compression: 3)));
    }

    /// <summary>8-bpp DIBs have a 256-entry palette unless biClrUsed says otherwise.</summary>
    [Fact]
    public void PixelOffset_Palette()
    {
        Assert.Equal(40 + (256 * 4), DibImage.GetPixelDataOffset(InfoHeader(bitCount: 8, compression: 0)));
        Assert.Equal(40 + (16 * 4), DibImage.GetPixelDataOffset(InfoHeader(bitCount: 8, compression: 0, colorsUsed: 16)));
    }

    /// <summary>Malformed/truncated producer data raises FormatException (callers skip the thumbnail).</summary>
    [Fact]
    public void PixelOffset_RejectsGarbage()
    {
        Assert.Throws<FormatException>(() => DibImage.GetPixelDataOffset(new byte[8]));
        var unknown = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(unknown, 77);
        Assert.Throws<FormatException>(() => DibImage.GetPixelDataOffset(unknown));
        var truncatedPalette = InfoHeader(bitCount: 8, compression: 0).AsSpan(0, 40).ToArray();
        Assert.Throws<FormatException>(() => DibImage.GetPixelDataOffset(truncatedPalette));
    }

    /// <summary>The BMP wrapper points bfOffBits at the pixel data and records the file size.</summary>
    [Fact]
    public void ToBmpFile_WritesFileHeader()
    {
        var dib = DibImage.CreateDibV5(new byte[2 * 2 * 4], 2, 2);
        var bmp = DibImage.ToBmpFile(dib);
        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal((uint)bmp.Length, BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(2)));
        Assert.Equal(14u + 124u, BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(10)));
    }

    /// <summary>DIBV5 rows are stored bottom-up: the first stored row is the image's last row.</summary>
    [Fact]
    public void CreateDibV5_IsBottomUp()
    {
        byte[] topDown = [1, 1, 1, 255, 2, 2, 2, 255]; // 1×2: top pixel = 1, bottom pixel = 2
        var dib = DibImage.CreateDibV5(topDown, 1, 2);
        Assert.Equal(124, BinaryPrimitives.ReadInt32LittleEndian(dib));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8)));
        Assert.Equal(2, dib[124]);
        Assert.Equal(1, dib[128]);
        Assert.Throws<ArgumentException>(() => DibImage.CreateDibV5(new byte[3], 1, 1));
    }

    /// <summary>WIC decodes our DIBV5 (via the BMP wrapper): dimensions, thumbnail and pixel hash are produced.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Analyze_DecodesDib()
    {
        var pixels = Gradient(64, 32);
        var bmp = DibImage.ToBmpFile(DibImage.CreateDibV5(pixels, 64, 32));
        var analysis = await ImageCodec.AnalyzeAsync(bmp, 16, 16, TestContext.Current.CancellationToken);
        Assert.Equal(64, analysis.Width);
        Assert.Equal(32, analysis.Height);
        Assert.NotNull(analysis.ThumbnailPng);
        Assert.Equal(0x89, analysis.ThumbnailPng![0]); // PNG signature
        Assert.Equal(ContentHasher.ForImagePixels(64, 32, pixels), analysis.PixelHash);
    }

    /// <summary>
    /// The dedupe guarantee: the same pixels as a DIB and as a PNG (what Windows-history import yields)
    /// produce the same pixel hash, so they merge into one history entry.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task PixelHash_IsEncodingIndependent()
    {
        var pixels = Gradient(40, 30);
        var bmp = DibImage.ToBmpFile(DibImage.CreateDibV5(pixels, 40, 30));
        var formats = await ImageCodec.ToClipboardFormatsAsync(bmp, TestContext.Current.CancellationToken);
        Assert.Equal([ClipFormatNames.Png, ClipFormatNames.DibV5], formats.Select(f => f.Name));

        var fromDib = await ImageCodec.AnalyzeAsync(bmp, 16, 16, TestContext.Current.CancellationToken);
        var fromPng = await ImageCodec.AnalyzeAsync(formats[0].Data, 16, 16, TestContext.Current.CancellationToken);
        Assert.Equal(fromDib.PixelHash, fromPng.PixelHash);
    }

    /// <summary>The analyzer picks PNG over DIB and never throws on garbage (returns null instead).</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Analyzer_IsRobust()
    {
        var analyzer = new WinRtImageAnalyzer();
        var garbage = new ClipCapture { Formats = [new ClipFormatData(ClipFormatNames.Dib, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12])] };
        Assert.Null(await analyzer.AnalyzeAsync(garbage, TestContext.Current.CancellationToken));

        var dib = DibImage.CreateDibV5(Gradient(8, 8), 8, 8);
        var valid = new ClipCapture { Formats = [new ClipFormatData(ClipFormatNames.DibV5, dib)] };
        var analysis = await analyzer.AnalyzeAsync(valid, TestContext.Current.CancellationToken);
        Assert.Equal(8, analysis!.Width);
    }

    /// <summary>Builds a minimal BITMAPINFOHEADER-based DIB (header + palette/masks, 4 bytes of pixels).</summary>
    /// <param name="bitCount">Bits per pixel.</param>
    /// <param name="compression">BI_* compression.</param>
    /// <param name="colorsUsed">biClrUsed.</param>
    /// <returns>The DIB bytes.</returns>
    private static byte[] InfoHeader(int bitCount, uint compression, uint colorsUsed = 0)
    {
        int extra = compression == 3 ? 12 : bitCount <= 8 ? (int)((colorsUsed == 0 ? 1u << bitCount : colorsUsed) * 4) : 0;
        var dib = new byte[40 + extra + 4];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), (ushort)bitCount);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16), compression);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(32), colorsUsed);
        return dib;
    }

    /// <summary>Opaque BGRA gradient (opaque so premultiplication cannot change values).</summary>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <returns>Top-down BGRA8 pixels.</returns>
    private static byte[] Gradient(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                pixels[i] = (byte)(x * 4);
                pixels[i + 1] = (byte)(y * 8);
                pixels[i + 2] = (byte)((x + y) * 2);
                pixels[i + 3] = 255;
            }
        }

        return pixels;
    }
}
