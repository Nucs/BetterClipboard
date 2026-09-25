using System.Runtime.InteropServices;
using System.Security.Cryptography;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Import;

/// <summary>
/// Minimal DPAPI-NG (CNG "protect secret") wrapper — the scheme Windows uses for its pinned clipboard
/// items (CMS EnvelopedData, KEK recipient OID 1.3.6.1.4.1.311.74.1, descriptor <c>LOCAL=user</c>,
/// AES-256 key wrap + AES-256-GCM content; see CLAUDE.md §1.4).
/// </summary>
/// <remarks>
/// A <c>LOCAL=user</c> blob can be decrypted by any process running as the same Windows user, which is
/// what lets BetterClipboard import Win+V pins — and why this is not a security boundary against
/// malware running as you.
/// </remarks>
public static class DpapiNg
{
    /// <summary>Descriptor Windows uses for clipboard pins: "only the current user on this machine".</summary>
    public const string LocalUserDescriptor = "LOCAL=user";

    /// <summary>
    /// Decrypts a DPAPI-NG blob. The protection descriptor is embedded in the blob.
    /// </summary>
    /// <param name="blob">The protected blob.</param>
    /// <returns>The plaintext.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="blob"/> is <see langword="null"/>.</exception>
    /// <exception cref="CryptographicException">Decryption failed (wrong user, corrupt blob, missing keys); carries the NTE_* status.</exception>
    public static byte[] Unprotect(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        int status = NCryptUnprotectSecret(0, NCRYPT_SILENT_FLAG, blob, (uint)blob.Length, 0, 0, out nint data, out uint length);
        if (status != 0)
        {
            throw new CryptographicException(status);
        }

        return CopyAndFree(data, length);
    }

    /// <summary>
    /// Encrypts data for a protection descriptor (used by tests to fabricate Windows-style pinned items).
    /// </summary>
    /// <param name="plaintext">Data to protect.</param>
    /// <param name="descriptor">Descriptor rule, e.g. <see cref="LocalUserDescriptor"/>.</param>
    /// <returns>The DPAPI-NG blob.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="CryptographicException">The descriptor is invalid or encryption failed.</exception>
    public static byte[] Protect(byte[] plaintext, string descriptor = LocalUserDescriptor)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(descriptor);
        int status = NCryptCreateProtectionDescriptor(descriptor, 0, out nint handle);
        if (status != 0)
        {
            throw new CryptographicException(status);
        }

        try
        {
            status = NCryptProtectSecret(handle, NCRYPT_SILENT_FLAG, plaintext, (uint)plaintext.Length, 0, 0, out nint blob, out uint length);
            if (status != 0)
            {
                throw new CryptographicException(status);
            }

            return CopyAndFree(blob, length);
        }
        finally
        {
            NCryptCloseProtectionDescriptor(handle);
        }
    }

    /// <summary>Copies an NCrypt-allocated buffer into managed memory, zeroes and frees the native copy.</summary>
    /// <param name="pointer">LocalAlloc'd buffer.</param>
    /// <param name="length">Length in bytes.</param>
    /// <returns>The managed copy.</returns>
    private static byte[] CopyAndFree(nint pointer, uint length)
    {
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, (int)length);
            return bytes;
        }
        finally
        {
            // The native buffer may hold plaintext secrets; wipe it before returning it to the heap.
            unsafe
            {
                new Span<byte>((void*)pointer, (int)length).Clear();
            }

            LocalFree(pointer);
        }
    }
}
