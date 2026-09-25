using System.Security.Cryptography;
using System.Text;
using BetterClipboard.Core.Security;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the key hierarchy: UUIDv5, the machine binding (with known-answer values computed by an
/// independent Python implementation, so an accidental change to the derivation — which would orphan every
/// user's history — fails loudly), and the sealed key vault.
/// </summary>
public sealed class SecurityTests : IDisposable
{
    private const string SampleMachineGuid = "0c7f5bd4-2a4e-4a1b-9d7e-3f2b1c0a9e88";
    private const string SampleSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private readonly TempDirectory temp = TestData.NewTempDirectory();

    /// <summary>Deletes the temp directory.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>RFC 9562 appendix A.4 and the Python documentation vectors.</summary>
    /// <param name="name">Name in the DNS namespace.</param>
    /// <param name="expected">Expected UUID.</param>
    [Theory]
    [InlineData("www.example.com", "2ed6657d-e927-568b-95e1-2665a8aea6a2")]
    [InlineData("python.org", "886313e1-3b8a-5372-9b90-0c9aee199e5d")]
    public void UuidV5_MatchesPublishedVectors(string name, string expected)
    {
        var uuid = Uuid.V5(Uuid.DnsNamespace, name);
        Assert.Equal(Guid.Parse(expected), uuid);
        Assert.Equal(5, uuid.Version);
    }

    /// <summary>The binding and store id match values computed independently (Python hmac/hashlib/uuid).</summary>
    [Fact]
    public void MachineBinding_MatchesKnownAnswer()
    {
        var binding = MachineBinding.Derive(SampleMachineGuid, SampleSid);
        Assert.Equal("2282d3470298fa4fdf6f96b613988802115255172e290270fab199232ddc95cd", Convert.ToHexStringLower(binding));
        Assert.Equal(Guid.Parse("22b41d51-e529-5025-98ca-74685d7b121b"), MachineBinding.StoreId(binding));
    }

    /// <summary>Registry formatting differences (braces, casing, whitespace) must not change the binding.</summary>
    [Fact]
    public void MachineBinding_NormalizesInputs()
    {
        var reference = MachineBinding.Derive(SampleMachineGuid, SampleSid);
        Assert.Equal(reference, MachineBinding.Derive("{" + SampleMachineGuid.ToUpperInvariant() + "}", SampleSid.ToLowerInvariant()));
        Assert.Equal(reference, MachineBinding.Derive("  " + SampleMachineGuid + " ", " " + SampleSid));
    }

    /// <summary>Another machine or another user gets a different binding and a different store.</summary>
    [Fact]
    public void MachineBinding_DiffersPerMachineAndUser()
    {
        var reference = MachineBinding.Derive(SampleMachineGuid, SampleSid);
        var otherMachine = MachineBinding.Derive(Guid.NewGuid().ToString(), SampleSid);
        var otherUser = MachineBinding.Derive(SampleMachineGuid, "S-1-5-21-1111111111-2222222222-3333333333-1002");

        Assert.NotEqual(reference, otherMachine);
        Assert.NotEqual(reference, otherUser);
        Assert.NotEqual(MachineBinding.StoreId(reference), MachineBinding.StoreId(otherMachine));
        Assert.NotEqual(MachineBinding.StoreId(reference), MachineBinding.StoreId(otherUser));
    }

    /// <summary>Garbage in is rejected instead of silently binding to a constant.</summary>
    [Fact]
    public void MachineBinding_RejectsInvalidInput()
    {
        Assert.Throws<ArgumentException>(() => MachineBinding.Derive("not-a-guid", SampleSid));
        Assert.Throws<ArgumentException>(() => MachineBinding.Derive(" ", SampleSid));
        Assert.Throws<ArgumentException>(() => MachineBinding.Derive(SampleMachineGuid, ""));
        Assert.Throws<ArgumentException>(() => MachineBinding.StoreId(new byte[16]));
    }

    /// <summary>The first call creates a sealed key; later vaults over the same file get the same key.</summary>
    [Fact]
    public void KeyVault_CreatesOnceAndReloads()
    {
        var binding = MachineBinding.Derive(SampleMachineGuid, SampleSid);
        var path = Path.Combine(temp.Path, "history.key");
        var vault = new HistoryKeyVault(path, new FakeKeyProtector(), binding);
        Assert.False(vault.Exists);

        var key = vault.GetOrCreateKey();
        Assert.Equal(HistoryKeyVault.KeyLength, key.Length);
        Assert.True(vault.Exists);
        Assert.Equal(key, new HistoryKeyVault(path, new FakeKeyProtector(), binding).GetOrCreateKey());

        // The file holds only the sealed form.
        var text = File.ReadAllText(path);
        Assert.DoesNotContain(Convert.ToBase64String(key), text, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(key), text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"protection\": \"fake\"", text, StringComparison.Ordinal);
    }

    /// <summary>A different binding (other machine/user) cannot unseal the key.</summary>
    [Fact]
    public void KeyVault_WrongBinding_IsUnavailable()
    {
        var path = Path.Combine(temp.Path, "history.key");
        new HistoryKeyVault(path, new FakeKeyProtector(), MachineBinding.Derive(SampleMachineGuid, SampleSid)).GetOrCreateKey();
        var elsewhere = new HistoryKeyVault(path, new FakeKeyProtector(), MachineBinding.Derive(Guid.NewGuid().ToString(), SampleSid));
        Assert.Throws<HistoryKeyUnavailableException>(() => elsewhere.GetOrCreateKey());
    }

    /// <summary>Corrupted files, foreign schemes and OS failures all surface as "key unavailable".</summary>
    [Fact]
    public void KeyVault_BadFiles_AreUnavailable()
    {
        var binding = MachineBinding.Derive(SampleMachineGuid, SampleSid);
        var path = Path.Combine(temp.Path, "history.key");
        new HistoryKeyVault(path, new FakeKeyProtector(), binding).GetOrCreateKey();

        Assert.Throws<HistoryKeyUnavailableException>(() => new HistoryKeyVault(path, new FakeKeyProtector { Scheme = "other" }, binding).GetOrCreateKey());
        Assert.Throws<HistoryKeyUnavailableException>(() => new HistoryKeyVault(path, new FakeKeyProtector { FailUnprotect = true }, binding).GetOrCreateKey());

        File.WriteAllText(path, "{ not json");
        Assert.Throws<HistoryKeyUnavailableException>(() => new HistoryKeyVault(path, new FakeKeyProtector(), binding).GetOrCreateKey());

        File.WriteAllText(path, """{ "version": 2, "protection": "fake", "cipher": "x", "createdUtc": "2026-01-01T00:00:00Z", "wrappedKey": "AA==" }""");
        Assert.Throws<HistoryKeyUnavailableException>(() => new HistoryKeyVault(path, new FakeKeyProtector(), binding).GetOrCreateKey());
    }
}

/// <summary>
/// Deterministic stand-in for DPAPI: the blob carries SHA-256(entropy) plus the masked secret, so a wrong
/// entropy (another machine binding) fails exactly like DPAPI does.
/// </summary>
internal sealed class FakeKeyProtector : IKeyProtector
{
    /// <inheritdoc />
    public string Scheme { get; init; } = "fake";

    /// <summary>Simulates lost DPAPI master keys (every unseal fails).</summary>
    public bool FailUnprotect { get; init; }

    /// <inheritdoc />
    public byte[] Protect(byte[] secret, byte[] entropy) => [.. SHA256.HashData(entropy), .. Mask(secret)];

    /// <inheritdoc />
    public byte[] Unprotect(byte[] blob, byte[] entropy)
    {
        if (FailUnprotect || blob.Length < 32 || !blob.AsSpan(0, 32).SequenceEqual(SHA256.HashData(entropy)))
        {
            throw new CryptographicException("Fake protector: wrong entropy or lost keys.");
        }

        return Mask(blob[32..]);
    }

    /// <summary>Reversible masking so the raw key never appears in the file (the tests check that).</summary>
    /// <param name="data">Bytes to mask/unmask.</param>
    /// <returns>The masked bytes.</returns>
    private static byte[] Mask(byte[] data)
    {
        var pad = SHA256.HashData(Encoding.UTF8.GetBytes("fake-protector-pad"));
        return data.Select((b, i) => (byte)(b ^ pad[i % pad.Length])).ToArray();
    }
}
