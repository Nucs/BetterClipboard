using System.Security.Cryptography;
using System.Text;

namespace BetterClipboard.Core.Security;

/// <summary>
/// RFC 4122 name-based UUIDs (version 5, SHA-1) — .NET only ships random (v4) and time-ordered (v7) GUIDs.
/// </summary>
/// <remarks>
/// Deterministic: the same namespace + name always yields the same GUID, which is what makes a store id
/// "computed from" its inputs instead of stored. One-way: the GUID reveals nothing practical about the
/// name (SHA-1 is broken for collisions, not for preimages, which is the property that matters here).
/// </remarks>
public static class Uuid
{
    /// <summary>RFC 4122 DNS namespace (<c>6ba7b810-9dad-11d1-80b4-00c04fd430c8</c>), used by the test vectors.</summary>
    public static readonly Guid DnsNamespace = new("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

    /// <summary>
    /// Creates a version-5 UUID for <paramref name="name"/> within <paramref name="namespaceId"/>.
    /// </summary>
    /// <param name="namespaceId">Namespace UUID.</param>
    /// <param name="name">Name; hashed as UTF-8.</param>
    /// <returns>The UUID.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public static Guid V5(Guid namespaceId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // RFC 4122 hashes the namespace in network (big-endian) byte order; Guid.ToByteArray is
        // little-endian for the first three fields, hence the explicit big-endian flag both ways.
        Span<byte> namespaceBytes = stackalloc byte[16];
        namespaceId.TryWriteBytes(namespaceBytes, bigEndian: true, out _);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[16 + nameBytes.Length];
        namespaceBytes.CopyTo(input);
        nameBytes.CopyTo(input, 16);

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);
        var uuid = hash[..16];
        uuid[6] = (byte)((uuid[6] & 0x0F) | 0x50); // version 5
        uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(uuid, bigEndian: true);
    }
}
