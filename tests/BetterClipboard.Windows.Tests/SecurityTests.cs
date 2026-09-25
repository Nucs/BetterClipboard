using System.Security.Cryptography;
using BetterClipboard.Core;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Security;
using BetterClipboard.Core.Storage;
using BetterClipboard.Windows.Security;
using Microsoft.Data.Sqlite;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests the real OS half of the key hierarchy on this machine: MachineGuid/SID discovery, DPAPI sealing
/// with the binding as entropy, and an end-to-end machine-bound store sealed by real DPAPI.
/// </summary>
public sealed class SecurityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", Guid.NewGuid().ToString("N"));

    /// <summary>Deletes the temp data directory.</summary>
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>The identity is readable, well-formed, stable, and never printed.</summary>
    [Fact]
    public void MachineIdentity_IsReadableStableAndRedacted()
    {
        var identity = MachineIdentity.Current();
        Assert.True(Guid.TryParse(identity.MachineGuid, out _));
        Assert.StartsWith("S-1-", identity.UserSid, StringComparison.Ordinal);
        Assert.Equal(identity, MachineIdentity.Current());
        Assert.DoesNotContain(identity.MachineGuid, identity.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(identity.UserSid, identity.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>DPAPI round-trips only with the same entropy (i.e. the same machine binding).</summary>
    [Fact]
    public void Dpapi_RequiresTheSameBinding()
    {
        var protector = new DpapiKeyProtector();
        var secret = RandomNumberGenerator.GetBytes(32);
        var binding = CurrentBinding();
        var blob = protector.Protect(secret, binding);

        Assert.Equal(secret, protector.Unprotect(blob, binding));
        var otherMachine = MachineBinding.Derive(Guid.NewGuid().ToString(), MachineIdentity.Current().UserSid);
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(blob, otherMachine));
    }

    /// <summary>A real DPAPI-sealed store persists across reopen and its key file holds no raw key.</summary>
    [Fact]
    public void MachineBoundStore_WithRealDpapi_Persists()
    {
        var paths = new AppPaths(root);
        var binding = CurrentBinding();
        var first = MachineBoundHistory.Open(paths, new DpapiKeyProtector(), binding, DateTimeOffset.Now);
        var capture = new ClipCapture
        {
            Formats = [new ClipFormatData(ClipFormatNames.UnicodeText, UnicodeTextCodec.Encode("sealed by dpapi"))],
            CapturedAtUtc = DateTimeOffset.Now,
        };
        first.Store.Upsert(capture, ContentClassifier.Classify(capture)!, null, bumpIfExists: true);
        SqliteConnection.ClearAllPools();

        var second = MachineBoundHistory.Open(paths, new DpapiKeyProtector(), binding, DateTimeOffset.Now);
        Assert.False(second.CreatedKey);
        Assert.Null(second.QuarantinedDirectory);
        Assert.Equal("sealed by dpapi", second.Store.Query(new ClipQuery()).Single().Preview);
        Assert.Contains("\"protection\": \"dpapi-user\"", File.ReadAllText(Path.Combine(second.StoreDirectory, MachineBoundHistory.KeyFileName)), StringComparison.Ordinal);
    }

    /// <summary>The binding for the machine and user running the tests.</summary>
    /// <returns>The binding.</returns>
    private static byte[] CurrentBinding()
    {
        var identity = MachineIdentity.Current();
        return MachineBinding.Derive(identity.MachineGuid, identity.UserSid);
    }
}
