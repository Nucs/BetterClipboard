namespace BetterClipboard.Core.Security;

/// <summary>
/// Seals/unseals small secrets (the database key) with an OS-held key — DPAPI in the Windows layer — so
/// Core never handles OS key material and tests can substitute a fake.
/// </summary>
public interface IKeyProtector
{
    /// <summary>Short identifier persisted with sealed blobs (e.g. <c>dpapi-user</c>) so future protectors can coexist.</summary>
    string Scheme { get; }

    /// <summary>
    /// Seals <paramref name="secret"/>; only <see cref="Unprotect"/> with the same <paramref name="entropy"/>
    /// (and the same OS identity) can recover it.
    /// </summary>
    /// <param name="secret">Plaintext secret.</param>
    /// <param name="entropy">Additional secret mixed into the sealing (the machine binding).</param>
    /// <returns>The sealed blob.</returns>
    /// <exception cref="System.Security.Cryptography.CryptographicException">Sealing failed.</exception>
    byte[] Protect(byte[] secret, byte[] entropy);

    /// <summary>
    /// Unseals a blob produced by <see cref="Protect"/>.
    /// </summary>
    /// <param name="blob">Sealed blob.</param>
    /// <param name="entropy">The same entropy used when sealing.</param>
    /// <returns>The secret.</returns>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// Wrong entropy (other machine/user), another user's blob, lost DPAPI master keys, or corruption.
    /// </exception>
    byte[] Unprotect(byte[] blob, byte[] entropy);
}
