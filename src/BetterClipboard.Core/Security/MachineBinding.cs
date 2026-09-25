using System.Security.Cryptography;
using System.Text;

namespace BetterClipboard.Core.Security;

/// <summary>
/// Derives the secret that binds BetterClipboard's history to <b>this machine</b> (Windows'
/// <c>MachineGuid</c>) and <b>this Windows user</b> (SID), and the store id computed from it.
/// </summary>
/// <remarks>
/// <para>
/// The binding is HKDF-SHA256(ikm = MachineGuid, salt = versioned BetterClipboard label, info = user SID).
/// It is never stored anywhere: it is recomputed at every start and used as DPAPI "optional entropy" when
/// sealing the database key (see <see cref="HistoryKeyVault"/>), so the sealed key only opens when
/// BetterClipboard's derivation runs on the same machine for the same user.
/// </para>
/// <para>
/// <b>Consequences.</b> A reinstall of Windows (new MachineGuid), a restored profile on another PC, or a
/// different Windows account yields a different binding: the old history becomes unreadable by design and
/// a fresh store is started (the old one is quarantined, not deleted). MachineGuid alone is not secret —
/// the protection comes from DPAPI (tied to the user's logon secret) plus this binding, not from hiding
/// the GUID.
/// </para>
/// </remarks>
public static class MachineBinding
{
    /// <summary>Length of the derived binding in bytes (256 bits).</summary>
    public const int Length = 32;

    /// <summary>Versioned HKDF salt; changing it orphans every existing store, so bump only with a migration.</summary>
    private const string Salt = "BetterClipboard/MachineBinding/v1";

    /// <summary>Fixed namespace for store ids (random GUID chosen once for BetterClipboard; never change it).</summary>
    private static readonly Guid StoreNamespace = new("5b1f0e6c-4c1d-4f58-9a8e-2f3d9c7b6a41");

    /// <summary>
    /// Derives the binding secret.
    /// </summary>
    /// <param name="machineGuid">Windows <c>MachineGuid</c> (any casing/braces; normalized).</param>
    /// <param name="userSid">SID of the current Windows user, e.g. <c>S-1-5-21-…</c>.</param>
    /// <returns>32 bytes; treat as secret (do not log or persist).</returns>
    /// <exception cref="ArgumentException">An input is null/blank or the GUID does not parse.</exception>
    public static byte[] Derive(string machineGuid, string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineGuid);
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        if (!Guid.TryParse(machineGuid.Trim(), out var guid))
        {
            throw new ArgumentException($"MachineGuid '{machineGuid}' is not a GUID.", nameof(machineGuid));
        }

        // Normalizing through Guid makes "{ABC…}" and "abc…" derive the same binding.
        var ikm = Encoding.UTF8.GetBytes("machine:" + guid.ToString("D"));
        var salt = Encoding.UTF8.GetBytes(Salt);
        var info = Encoding.UTF8.GetBytes("user:" + userSid.Trim().ToUpperInvariant());
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, Length, salt, info);
    }

    /// <summary>
    /// Computes the store id (the history folder name) from a binding: a UUIDv5 that is stable for the
    /// same machine + user and reveals nothing about the MachineGuid or the binding itself.
    /// </summary>
    /// <param name="binding">Output of <see cref="Derive"/>.</param>
    /// <returns>The store id.</returns>
    /// <exception cref="ArgumentException">The binding has the wrong length.</exception>
    public static Guid StoreId(ReadOnlySpan<byte> binding)
    {
        if (binding.Length != Length)
        {
            throw new ArgumentException($"Binding must be {Length} bytes.", nameof(binding));
        }

        // Hash the binding first so the UUID input is not the secret itself.
        return Uuid.V5(StoreNamespace, "store:" + Convert.ToHexStringLower(SHA256.HashData(binding)));
    }
}
