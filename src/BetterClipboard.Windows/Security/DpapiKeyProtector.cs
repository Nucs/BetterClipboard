using System.Security.Cryptography;
using BetterClipboard.Core.Security;

namespace BetterClipboard.Windows.Security;

/// <summary>
/// Seals the history key with classic DPAPI (<c>CryptProtectData</c>) for the <b>current Windows user</b>,
/// with the machine binding as optional entropy.
/// </summary>
/// <remarks>
/// <para>
/// DPAPI keys derive from the user's logon secret and live in the profile
/// (<c>%APPDATA%\Microsoft\Protect\{SID}</c>): other users — including administrators, without the user's
/// password — cannot unseal the key, and the entropy adds that only BetterClipboard's derivation for this
/// machine opens it. Malware running <i>as the same user</i> can still unseal it (true of every DPAPI
/// consumer, browsers included); this protects the data at rest, in backups and on stolen disks.
/// </para>
/// <para>
/// <b>Footgun.</b> An administrator <i>resetting</i> (not changing) a local account's password destroys
/// access to that user's DPAPI master keys — the history then becomes unreadable and is quarantined.
/// </para>
/// </remarks>
public sealed class DpapiKeyProtector : IKeyProtector
{
    /// <inheritdoc />
    public string Scheme => "dpapi-user";

    /// <inheritdoc />
    public byte[] Protect(byte[] secret, byte[] entropy) =>
        ProtectedData.Protect(secret, entropy, DataProtectionScope.CurrentUser);

    /// <inheritdoc />
    public byte[] Unprotect(byte[] blob, byte[] entropy) =>
        ProtectedData.Unprotect(blob, entropy, DataProtectionScope.CurrentUser);
}
