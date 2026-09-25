using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace BetterClipboard.Core.Content;

/// <summary>
/// The key "Forget forever" recognizes copies by: SHA-256 over the clip's primary content, like
/// <see cref="ContentHasher"/>, except that <b>text is normalized</b> first. Line endings are unified and
/// surrounding whitespace is ignored, so a forgotten <c>secret</c> also keeps out <c>secret\r\n</c> copied
/// from a terminal and <c>  secret </c> from a web page.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the dedupe hash.</b> <see cref="ContentHasher.ForText"/> is exact on purpose: whitespace
/// variants stay separate history entries. For a rule that must never let something back in, exactness is
/// the wrong default — the same secret routinely arrives with and without a trailing newline depending on
/// the app it was copied from. Everything else still has to match exactly, letter case included
/// (passwords are case-sensitive).
/// </para>
/// <para>
/// <b>Other kinds</b> use their existing identity: file lists by their paths (already case-insensitive),
/// images by their decoded pixels when the analyzer computed them (so the same picture as DIB or PNG is one
/// forgotten image), rich-only content by its bytes. Text that is nothing but whitespace falls back to the
/// exact text hash, or forgetting one blank copy would keep out every blank copy.
/// </para>
/// <para>
/// <b>Stability.</b> Stored fingerprints are compared against fingerprints computed later, by newer builds.
/// Changing the normalization, the domain letter or the encoding silently "un-forgets" everything the user
/// asked never to record again; do it only with a migration that recomputes them (which needs the content,
/// which by design is gone) — in practice: never. <c>ForgetFingerprintTests</c> pins known answers.
/// </para>
/// </remarks>
public static class ForgetFingerprint
{
    /// <summary>
    /// Every UTF-16 code unit .NET's <see cref="string.Trim()"/> removes (<see cref="char.IsWhiteSpace(char)"/>),
    /// for SQL that must trim exactly like <see cref="NormalizeText"/> (see <c>ClipStore.Forget</c>).
    /// </summary>
    public static readonly string WhitespaceCharacters =
        new(Enumerable.Range(0, char.MaxValue + 1).Select(i => (char)i).Where(char.IsWhiteSpace).ToArray());

    /// <summary>Characters encoded per step when hashing text; bounds the rented buffer for huge texts.</summary>
    private const int ChunkChars = 16 * 1024;

    /// <summary>
    /// The fingerprint of a classified clip.
    /// </summary>
    /// <param name="clip">
    /// The classification, <b>after</b> image analysis replaced an image's byte hash by its pixel hash (the
    /// order the history service uses), or pixel-identical images in other encodings would not match.
    /// </param>
    /// <returns>64-character lowercase hex SHA-256.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clip"/> is <see langword="null"/>.</exception>
    public static string Of(ClassifiedClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return Of(clip.PlainText, clip.ContentHash);
    }

    /// <summary>
    /// The fingerprint from a clip's parts, for stored entries whose text is re-read from their formats.
    /// </summary>
    /// <param name="plainText">The clip's full text as the classifier sees it (<see cref="ClassifiedClip.PlainText"/>); <see langword="null"/> for files, images and rich-only content.</param>
    /// <param name="contentHash">The clip's dedupe key (the pixel hash for analyzed images).</param>
    /// <returns>64-character lowercase hex SHA-256.</returns>
    /// <exception cref="ArgumentException"><paramref name="contentHash"/> is null or blank.</exception>
    public static string Of(string? plainText, string contentHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        return plainText is { Length: > 0 } && ForText(plainText) is { } textFingerprint ? textFingerprint : contentHash;
    }

    /// <summary>
    /// Fingerprints text: SHA-256 over the domain letter <c>N</c>, a newline and the UTF-8 of
    /// <see cref="NormalizeText"/>.
    /// </summary>
    /// <param name="text">The full text.</param>
    /// <returns>The fingerprint, or <see langword="null"/> when the text is empty or only whitespace (callers then use the exact content hash).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static string? ForText(string text)
    {
        var normalized = Normalize(text, out _);
        return normalized.IsEmpty ? null : HashNormalized(normalized);
    }

    /// <summary>
    /// The normalized form the text fingerprint is computed over: <c>\r\n</c> and lone <c>\r</c> become
    /// <c>\n</c>, then leading and trailing whitespace (<see cref="WhitespaceCharacters"/>) is removed.
    /// </summary>
    /// <param name="text">The full text.</param>
    /// <returns>The normalized text; empty for whitespace-only input.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static string NormalizeText(string text) => Normalize(text, out _).ToString();

    /// <summary>
    /// Normalizes without copying when possible: most copies contain no <c>\r</c> at all, so the result is
    /// then a slice of the input.
    /// </summary>
    /// <param name="text">The full text.</param>
    /// <param name="unified">The newline-unified copy when one had to be made (kept alive for the returned span), else <see langword="null"/>.</param>
    /// <returns>The normalized text as a span over <paramref name="text"/> or <paramref name="unified"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    private static ReadOnlySpan<char> Normalize(string text, out string? unified)
    {
        ArgumentNullException.ThrowIfNull(text);
        unified = null;
        if (text.Contains('\r'))
        {
            // Order matters: CRLF first, or "\r\n" would become two newlines.
            unified = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        }

        return (unified ?? text).AsSpan().Trim();
    }

    /// <summary>
    /// Hashes normalized text as UTF-8 in chunks, so a 50 MB copy is not encoded into one 150 MB buffer.
    /// </summary>
    /// <remarks>
    /// The stateful <see cref="Encoder"/> carries a high surrogate that ends one chunk over to the next, so
    /// chunk boundaries never change the bytes — the result equals hashing the whole string's UTF-8 at once.
    /// </remarks>
    /// <param name="normalized">Non-empty normalized text.</param>
    /// <returns>Lowercase hex digest.</returns>
    /// <exception cref="InvalidOperationException">The encoder did not take a whole chunk (a sizing bug; never expected).</exception>
    private static string HashNormalized(ReadOnlySpan<char> normalized)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> prefix = [(byte)'N', (byte)'\n'];
        hash.AppendData(prefix);

        // Lone surrogates become U+FFFD exactly as Encoding.UTF8.GetBytes would encode them.
        var encoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetEncoder();

        // GetMaxByteCount(n) already allows for a high surrogate left over from the previous chunk.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(ChunkChars));
        try
        {
            while (!normalized.IsEmpty)
            {
                var chunk = normalized[..Math.Min(ChunkChars, normalized.Length)];
                normalized = normalized[chunk.Length..];
                encoder.Convert(chunk, buffer, flush: normalized.IsEmpty, out int charsUsed, out int bytesUsed, out _);
                if (charsUsed != chunk.Length)
                {
                    // A dropped tail would store a fingerprint that later copies never match — the forgotten item
                    // would quietly come back. Fail loudly instead.
                    throw new InvalidOperationException("The UTF-8 buffer was too small for a chunk of text.");
                }

                hash.AppendData(buffer, 0, bytesUsed);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return Convert.ToHexStringLower(digest);
    }
}
