using System.Globalization;
using System.Security.Cryptography;
using BetterClipboard.Core.Security;

namespace BetterClipboard.Core.Storage;

/// <summary>
/// Outcome of <see cref="MachineBoundHistory.Open"/>: the ready store plus what happened on the way, for the
/// log and the settings page. Contains no key material.
/// </summary>
/// <param name="Store">The initialized, encrypted store.</param>
/// <param name="StoreId">Id of the store (folder name), computed from the machine binding.</param>
/// <param name="StoreDirectory">Absolute path of the store folder.</param>
/// <param name="CreatedKey">A new database key was generated (first run on this machine/account, or after a quarantine).</param>
/// <param name="AdoptedLegacyDatabase">The pre-encryption <c>history.db</c> was moved into this store (and encrypted).</param>
/// <param name="QuarantinedDirectory">Where an unreadable store was set aside, or <see langword="null"/>.</param>
/// <param name="QuarantineReason">Why it was set aside, or <see langword="null"/>.</param>
public sealed record MachineBoundHistoryResult(
    ClipStore Store,
    Guid StoreId,
    string StoreDirectory,
    bool CreatedKey,
    bool AdoptedLegacyDatabase,
    string? QuarantinedDirectory,
    string? QuarantineReason);

/// <summary>
/// Opens the history that belongs to <b>this machine and this Windows user</b>: resolves the store folder
/// from the machine binding, unseals (or creates) its database key, adopts a legacy plaintext database, and
/// sets aside a store that can no longer be decrypted instead of failing to start.
/// </summary>
/// <remarks>
/// <para>
/// Layout: <c>{data}\stores\{storeId}\history.db</c> (SQLite3MC-encrypted) and <c>history.key</c> (the
/// database key sealed by <see cref="IKeyProtector"/> with the binding as entropy). The store id is derived
/// from the binding, so the folder name itself proves nothing and reveals nothing — the key does the work.
/// </para>
/// <para>
/// <b>Quarantine, never delete.</b> When the key cannot be unsealed (Windows reinstalled, DPAPI keys lost,
/// profile copied from another PC) or does not decrypt the database, the whole folder is renamed to
/// <c>{storeId}.unreadable-{timestamp}</c> and a fresh store is created. Renaming it back (on the machine and
/// account that created it) restores it; nothing is ever destroyed automatically.
/// </para>
/// </remarks>
public static class MachineBoundHistory
{
    /// <summary>Database file name inside a store folder.</summary>
    public const string DatabaseFileName = "history.db";

    /// <summary>Sealed key file name inside a store folder.</summary>
    public const string KeyFileName = "history.key";

    /// <summary>
    /// Opens (creating/migrating as needed) this machine's encrypted history.
    /// </summary>
    /// <param name="paths">Data locations.</param>
    /// <param name="protector">Key sealing (DPAPI for the current user in production).</param>
    /// <param name="binding">Machine binding from <see cref="MachineBinding.Derive"/>; the caller may zero it afterwards.</param>
    /// <param name="now">Clock for the quarantine folder name.</param>
    /// <returns>The opened store and what happened.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The binding has the wrong length.</exception>
    /// <exception cref="CryptographicException">A new key could not be sealed (DPAPI unavailable) — the app cannot store history safely.</exception>
    /// <exception cref="IOException">The store folder could not be created, adopted into, or quarantined.</exception>
    /// <exception cref="InvalidOperationException">The database was written by a newer BetterClipboard.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The database is locked beyond the timeout or the disk is full.</exception>
    public static MachineBoundHistoryResult Open(AppPaths paths, IKeyProtector protector, byte[] binding, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(binding);

        var storeId = MachineBinding.StoreId(binding);
        var storeDirectory = paths.StoreDirectory(storeId);
        Directory.CreateDirectory(storeDirectory);
        var databasePath = Path.Combine(storeDirectory, DatabaseFileName);

        // Only adopt into a store that has no database yet; an existing store always wins over the legacy
        // file (which then stays where it is, untouched, for the user to inspect).
        bool adopted = !File.Exists(databasePath) && ClipStore.MoveDatabaseFiles(paths.LegacyDatabasePath, databasePath);

        try
        {
            var (store, createdKey) = OpenStore(storeDirectory, protector, binding);
            return new MachineBoundHistoryResult(store, storeId, storeDirectory, createdKey, adopted, null, null);
        }
        catch (Exception ex) when (ex is HistoryKeyUnavailableException or HistoryUnreadableException)
        {
            var quarantined = Quarantine(storeDirectory, now);
            Directory.CreateDirectory(storeDirectory);

            // A second failure on a brand-new folder is not a "wrong key" situation any more — let it surface.
            var (store, createdKey) = OpenStore(storeDirectory, protector, binding);
            return new MachineBoundHistoryResult(store, storeId, storeDirectory, createdKey, adopted, quarantined, ex.Message);
        }
    }

    /// <summary>Unseals/creates the key and initializes the encrypted store in <paramref name="storeDirectory"/>.</summary>
    /// <param name="storeDirectory">Existing store folder.</param>
    /// <param name="protector">Key sealing.</param>
    /// <param name="binding">Machine binding (DPAPI entropy).</param>
    /// <returns>The initialized store and whether its key was newly created.</returns>
    /// <exception cref="HistoryKeyUnavailableException">The key file exists but cannot be unsealed.</exception>
    /// <exception cref="HistoryUnreadableException">The key does not decrypt the database.</exception>
    private static (ClipStore Store, bool CreatedKey) OpenStore(string storeDirectory, IKeyProtector protector, byte[] binding)
    {
        var vault = new HistoryKeyVault(Path.Combine(storeDirectory, KeyFileName), protector, binding);
        bool createdKey = !vault.Exists;
        var key = vault.GetOrCreateKey();
        try
        {
            var store = new ClipStore(Path.Combine(storeDirectory, DatabaseFileName), key);
            store.Initialize();
            return (store, createdKey);
        }
        finally
        {
            // The store keeps its own (hex) copy for new connections; this byte copy has no further use.
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Renames an unreadable store folder out of the way.</summary>
    /// <param name="storeDirectory">The store folder.</param>
    /// <param name="now">Timestamp for the new name.</param>
    /// <returns>The quarantine folder path.</returns>
    /// <exception cref="IOException">The folder is in use and cannot be renamed.</exception>
    private static string Quarantine(string storeDirectory, DateTimeOffset now)
    {
        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = $"{storeDirectory}.unreadable-{stamp}";

        // Two quarantines in the same second (e.g. a crash loop) must not collide.
        for (int i = 2; Directory.Exists(target) || File.Exists(target); i++)
        {
            target = $"{storeDirectory}.unreadable-{stamp}-{i}";
        }

        Directory.Move(storeDirectory, target);
        return target;
    }
}
