using System.Security.Cryptography;
using System.Text;

namespace BetterClipboard.Core.Content;

/// <summary>
/// Computes the deduplication key of a clip: SHA-256 over a one-letter domain prefix plus the clip's
/// <b>primary</b> content (text, file list or image bytes), rendered as lowercase hex.
/// </summary>
/// <remarks>
/// <para>
/// Hashing only the primary content is a deliberate tradeoff: copying the same sentence from Chrome
/// (text + HTML) and from Notepad (text only) yields <b>one</b> history entry whose formats are replaced
/// by the latest copy. That matches what users perceive as "the same clip" and what Win+V does
/// (<c>ClipboardMonitor_RemovedDuplicateFromBuffer</c>), at the cost of not keeping both format variants.
/// </para>
/// <para>
/// The domain prefix (<c>T</c>/<c>F</c>/<c>I</c>/<c>R</c>) keeps, e.g., a text clip whose bytes happen
/// to equal an image payload from colliding. Changing this scheme breaks dedupe against existing
/// databases — bump the schema version and rehash if you ever must.
/// </para>
/// </remarks>
public static class ContentHasher
{
    /// <summary>Hashes a text clip (exact text, no normalization — whitespace differences are distinct clips).</summary>
    /// <param name="text">The decoded clip text.</param>
    /// <returns>64-character lowercase hex SHA-256.</returns>
    public static string ForText(string text) => Hash('T', Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Hashes a file-list clip. Paths are compared case-insensitively (NTFS semantics) and in order.
    /// </summary>
    /// <param name="paths">The decoded drop-file paths.</param>
    /// <returns>64-character lowercase hex SHA-256.</returns>
    public static string ForFiles(IReadOnlyList<string> paths) =>
        Hash('F', Encoding.UTF8.GetBytes(string.Join('\n', paths).ToUpperInvariant()));

    /// <summary>Hashes an image clip over the raw bytes of its primary image format.</summary>
    /// <param name="imageBytes">PNG or DIB bytes exactly as captured.</param>
    /// <returns>64-character lowercase hex SHA-256.</returns>
    public static string ForImage(ReadOnlySpan<byte> imageBytes) => Hash('I', imageBytes);

    /// <summary>
    /// Hashes an image by its decoded pixels, independent of how it was encoded (DIB, DIBV5, PNG, …).
    /// </summary>
    /// <remarks>
    /// Pixels must be top-down BGRA8 with straight alpha; dimensions are mixed in so a 2×8 and an 8×2
    /// image with identical bytes do not collide. Uses its own domain letter, so pixel hashes never equal
    /// the byte-based <see cref="ForImage"/> hashes of older rows.
    /// </remarks>
    /// <param name="width">Pixel width.</param>
    /// <param name="height">Pixel height.</param>
    /// <param name="bgraPixels">Decoded BGRA8 pixels.</param>
    /// <returns>64-character lowercase hex SHA-256.</returns>
    public static string ForImagePixels(int width, int height, ReadOnlySpan<byte> bgraPixels)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[10];
        header[0] = (byte)'P';
        header[1] = (byte)'\n';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header[2..], width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header[6..], height);
        hash.AppendData(header);
        hash.AppendData(bgraPixels);
        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>Hashes a clip that only has rich (HTML/RTF) content and no plain text.</summary>
    /// <param name="richBytes">The raw HTML or RTF payload.</param>
    /// <returns>64-character lowercase hex SHA-256.</returns>
    public static string ForRichOnly(ReadOnlySpan<byte> richBytes) => Hash('R', richBytes);

    /// <summary>Hashes <paramref name="payload"/> prefixed by a domain letter and a newline.</summary>
    /// <param name="domain">Single ASCII letter identifying the content domain.</param>
    /// <param name="payload">Content bytes.</param>
    /// <returns>Lowercase hex digest.</returns>
    private static string Hash(char domain, ReadOnlySpan<byte> payload)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> prefix = [(byte)domain, (byte)'\n'];
        hash.AppendData(prefix);
        hash.AppendData(payload);
        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return Convert.ToHexStringLower(digest);
    }
}
