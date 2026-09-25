using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterClipboard.Core.Security;

/// <summary>
/// The sealed database key could not be recovered (another machine or user, reinstalled Windows, reset
/// password, corrupted key file). The history it protects is unreadable by design.
/// </summary>
public sealed class HistoryKeyUnavailableException : Exception
{
    /// <summary>
    /// Creates the exception.
    /// </summary>
    /// <param name="message">What failed.</param>
    /// <param name="innerException">The underlying cryptographic or I/O error.</param>
    public HistoryKeyUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Owns the history's 256-bit database key: generated randomly once, persisted only <b>sealed</b> (via an
/// <see cref="IKeyProtector"/> with the machine binding as entropy) in a small JSON file next to the database.
/// </summary>
/// <remarks>
/// <para>
/// Key hierarchy: <c>MachineGuid + user SID → HKDF → binding</c>; <c>random DEK → DPAPI(user, entropy =
/// binding) → history.key</c>; <c>DEK → SQLite3MC (ChaCha20-Poly1305) → history.db</c>. The DEK is random
/// (not derived) so it can be re-sealed later (e.g. an export password) without re-encrypting the database.
/// </para>
/// <para>
/// The key file is written atomically (temp + move). Losing it loses the history — it is the one file that
/// must travel with <c>history.db</c> in any backup, and it only unseals on the originating machine/user.
/// </para>
/// </remarks>
public sealed class HistoryKeyVault
{
    /// <summary>Database key length in bytes.</summary>
    public const int KeyLength = 32;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IKeyProtector protector;
    private readonly byte[] binding;

    /// <summary>
    /// Creates a vault over <paramref name="keyFilePath"/>.
    /// </summary>
    /// <param name="keyFilePath">Path of <c>history.key</c>.</param>
    /// <param name="protector">Sealing implementation (DPAPI in production).</param>
    /// <param name="binding">Machine binding from <see cref="MachineBinding.Derive"/> (used as entropy).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public HistoryKeyVault(string keyFilePath, IKeyProtector protector, byte[] binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyFilePath);
        KeyFilePath = keyFilePath;
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
        this.binding = binding ?? throw new ArgumentNullException(nameof(binding));
    }

    /// <summary>Path of the sealed key file.</summary>
    public string KeyFilePath { get; }

    /// <summary>Whether a sealed key already exists.</summary>
    public bool Exists => File.Exists(KeyFilePath);

    /// <summary>
    /// Returns the database key, creating and sealing a new random one on first use.
    /// </summary>
    /// <returns>The 32-byte key (keep in memory only).</returns>
    /// <exception cref="HistoryKeyUnavailableException">A key file exists but cannot be read or unsealed.</exception>
    /// <exception cref="IOException">A new key file could not be written.</exception>
    public byte[] GetOrCreateKey()
    {
        if (Exists)
        {
            return LoadKey();
        }

        var key = RandomNumberGenerator.GetBytes(KeyLength);
        var file = new KeyFile(1, protector.Scheme, "sqlite3mc-chacha20", DateTimeOffset.UtcNow, Convert.ToBase64String(protector.Protect(key, binding)));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(KeyFilePath))!);
        var temp = KeyFilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions));
        File.Move(temp, KeyFilePath, overwrite: false);
        return key;
    }

    /// <summary>Reads and unseals the existing key file.</summary>
    /// <returns>The key.</returns>
    /// <exception cref="HistoryKeyUnavailableException">Unreadable, unknown scheme, wrong length or unsealing failed.</exception>
    private byte[] LoadKey()
    {
        KeyFile? file;
        try
        {
            file = JsonSerializer.Deserialize<KeyFile>(File.ReadAllText(KeyFilePath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new HistoryKeyUnavailableException($"The history key file '{KeyFilePath}' is unreadable.", ex);
        }

        if (file is null || file.Version != 1 || file.WrappedKey is null)
        {
            throw new HistoryKeyUnavailableException($"The history key file '{KeyFilePath}' has an unsupported format.");
        }

        if (!string.Equals(file.Protection, protector.Scheme, StringComparison.Ordinal))
        {
            throw new HistoryKeyUnavailableException($"The history key is sealed with '{file.Protection}', expected '{protector.Scheme}'.");
        }

        byte[] key;
        try
        {
            key = protector.Unprotect(Convert.FromBase64String(file.WrappedKey), binding);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Expected after Windows reinstall / profile moved to another PC / DPAPI keys lost.
            throw new HistoryKeyUnavailableException("The history key cannot be unsealed on this machine for this user.", ex);
        }

        if (key.Length != KeyLength)
        {
            throw new HistoryKeyUnavailableException("The unsealed history key has an unexpected length.");
        }

        return key;
    }

    /// <summary>On-disk JSON shape of <c>history.key</c>.</summary>
    /// <param name="Version">File format version.</param>
    /// <param name="Protection">Protector scheme (see <see cref="IKeyProtector.Scheme"/>).</param>
    /// <param name="Cipher">Database cipher the key is used with (informational).</param>
    /// <param name="CreatedUtc">When the key was generated.</param>
    /// <param name="WrappedKey">Base64 sealed key.</param>
    private sealed record KeyFile(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("protection")] string Protection,
        [property: JsonPropertyName("cipher")] string Cipher,
        [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc,
        [property: JsonPropertyName("wrappedKey")] string? WrappedKey);
}
