using System.Text;

namespace BetterClipboard.Core.Content;

/// <summary>
/// Names the container an image file's bytes are in ("PNG", "JPEG", …) from their first bytes, for the details kept with a
/// screenshot's file (<see cref="Model.SourceFile.Format"/>).
/// </summary>
/// <remarks>
/// The bytes decide, never the extension: a file named <c>.png</c> that holds a JPEG is reported as "JPEG", which is what an
/// app opening it will meet. Only the signature is read — nothing is decoded — so it is safe on any input, damaged or
/// hostile. The names are fixed English abbreviations (shown untranslated, like the extensions they mirror).
/// </remarks>
public static class ImageFileFormat
{
    /// <summary>How many leading bytes <see cref="Detect"/> needs at most to tell every format it knows.</summary>
    public const int HeaderLength = 16;

    /// <summary>The PNG signature (RFC 2083 §3.1).</summary>
    private static ReadOnlySpan<byte> Png => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Names the image format of a file from its first bytes.
    /// </summary>
    /// <param name="header">The file's first bytes (at least <see cref="HeaderLength"/> when the file has them; fewer is fine).</param>
    /// <returns>
    /// "PNG", "JPEG", "GIF", "BMP", "TIFF", "WebP", "HEIF", "AVIF", "ICO" or "JPEG XR"; <see langword="null"/> when the bytes
    /// start with none of their signatures (or are too short to tell).
    /// </returns>
    public static string? Detect(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(Png))
        {
            return "PNG";
        }

        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return "JPEG";
        }

        if (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8))
        {
            return "GIF";
        }

        // TIFF in either byte order; JPEG XR (HD Photo) shares Intel byte order's "II" with its own version byte 0xBC.
        if (header.StartsWith("II*\0"u8) || header.StartsWith("MM\0*"u8))
        {
            return "TIFF";
        }

        if (header.Length >= 3 && header[0] == (byte)'I' && header[1] == (byte)'I' && header[2] == 0xBC)
        {
            return "JPEG XR";
        }

        if (header.Length >= 12 && header.StartsWith("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
        {
            return "WebP";
        }

        // ISO base media files: a size, then "ftyp", then the major brand that says which image codec they carry.
        if (header.Length >= 12 && header[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = Encoding.ASCII.GetString(header[8..12]);
            if (brand is "avif" or "avis")
            {
                return "AVIF";
            }

            if (brand is "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "mif1" or "msf1")
            {
                return "HEIF";
            }

            return null;
        }

        // "BM" alone matches too much text to trust; a BMP file header's reserved words (bytes 6–9) are zero.
        if (header.Length >= 10 && header[0] == (byte)'B' && header[1] == (byte)'M' && header[6..10].IndexOfAnyExcept((byte)0) < 0)
        {
            return "BMP";
        }

        // An icon directory: reserved 0, type 1, at least one image.
        if (header.Length >= 6 && header[0] == 0 && header[1] == 0 && header[2] == 1 && header[3] == 0 && (header[4] | header[5]) != 0)
        {
            return "ICO";
        }

        return null;
    }
}
