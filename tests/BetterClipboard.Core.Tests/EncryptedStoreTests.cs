using System.Security.Cryptography;
using System.Text;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Security;
using BetterClipboard.Core.Storage;
using Microsoft.Data.Sqlite;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for encryption at rest: nothing readable reaches the files (main database and WAL), the right key
/// reopens and searches, a wrong/missing key is reported as unreadable, legacy plaintext databases are
/// encrypted in place, and the machine-bound store adopts, persists and quarantines correctly.
/// </summary>
public sealed class EncryptedStoreTests : IDisposable
{
    /// <summary>A marker that must never appear in any file of an encrypted store, in any encoding.</summary>
    private const string Marker = "TopSecretClipboardMarker-7f3a";

    private readonly TempDirectory temp = TestData.NewTempDirectory();

    /// <summary>Deletes the temp directory.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>Text, search index and payloads are all ciphertext on disk, including the uncheckpointed WAL.</summary>
    [Fact]
    public void EncryptedStore_WritesNoPlaintext()
    {
        var store = NewEncrypted(RandomKey());
        Add(store, TestData.Text(Marker + " שלום עולם"));

        // Inspect while the pool still holds the WAL (the WAL is where fresh writes live first)...
        AssertNoPlaintext(store.DatabasePath);
        SqliteConnection.ClearAllPools();

        // ...and after the last connection closed and SQLite checkpointed into the main file.
        AssertNoPlaintext(store.DatabasePath);
        Assert.False(ClipStore.HasPlaintextHeader(store.DatabasePath));
        Assert.True(store.IsEncrypted);
    }

    /// <summary>The same key reopens the history with working trigram search, including Hebrew.</summary>
    [Fact]
    public void EncryptedStore_ReopensAndSearchesWithSameKey()
    {
        var key = RandomKey();
        var store = NewEncrypted(key);
        Add(store, TestData.Text("The Windows Clipboard forgets"));
        Add(store, TestData.Text("שלום עולם מהלוח"));
        SqliteConnection.ClearAllPools();

        var reopened = NewEncrypted(key);
        Assert.Equal(2, reopened.GetStats().Count);
        Assert.Equal("The Windows Clipboard forgets", reopened.Query(new ClipQuery { SearchText = "BOARD" }).Single().Preview);
        Assert.Equal("שלום עולם מהלוח", reopened.Query(new ClipQuery { SearchText = "עולם" }).Single().Preview);
        Assert.False(reopened.MigratedFromPlaintext);
    }

    /// <summary>Another key, or no key at all, cannot read the file and says so with a dedicated exception.</summary>
    [Fact]
    public void EncryptedStore_WrongOrMissingKey_IsUnreadable()
    {
        Add(NewEncrypted(RandomKey()), TestData.Text(Marker));
        SqliteConnection.ClearAllPools();

        Assert.Throws<HistoryUnreadableException>(() => new ClipStore(temp.DatabasePath, RandomKey()).Initialize());
        Assert.Throws<HistoryUnreadableException>(() => new ClipStore(temp.DatabasePath).Initialize());

        // The failed attempts must not leave pooled handles behind (the caller renames the folder next).
        var moved = temp.DatabasePath + ".moved";
        File.Move(temp.DatabasePath, moved);
        Assert.True(File.Exists(moved));
    }

    /// <summary>A pre-encryption database is encrypted in place, keeping every entry searchable.</summary>
    [Fact]
    public void Initialize_EncryptsLegacyPlaintextInPlace()
    {
        var plain = new ClipStore(temp.DatabasePath);
        plain.Initialize();
        Add(plain, TestData.Text(Marker + " legacy entry"));
        Add(plain, TestData.Text("second legacy entry", pin: true));
        Assert.False(plain.IsEncrypted);
        SqliteConnection.ClearAllPools();
        Assert.True(ClipStore.HasPlaintextHeader(temp.DatabasePath));

        var key = RandomKey();
        var encrypted = new ClipStore(temp.DatabasePath, key);
        encrypted.Initialize();
        Assert.True(encrypted.MigratedFromPlaintext);
        Assert.Equal(2, encrypted.GetStats().Count);
        Assert.Equal(1, encrypted.GetStats().PinnedCount);
        Assert.Single(encrypted.Query(new ClipQuery { SearchText = "legacy entry", Filter = ClipFilter.Pinned }));

        SqliteConnection.ClearAllPools();
        AssertNoPlaintext(temp.DatabasePath);

        // Idempotent: the second start finds ciphertext and does nothing special.
        var again = new ClipStore(temp.DatabasePath, key);
        again.Initialize();
        Assert.False(again.MigratedFromPlaintext);
        Assert.Equal(2, again.GetStats().Count);
    }

    /// <summary>Delete, retention and incremental vacuum keep working on ciphertext pages.</summary>
    [Fact]
    public void EncryptedStore_DeletePruneAndVacuumWork()
    {
        var store = NewEncrypted(RandomKey());
        var victim = Add(store, TestData.Image(1, 256 * 1024))!;
        for (byte i = 2; i < 6; i++)
        {
            Add(store, TestData.Image(i, 64 * 1024));
        }

        Assert.True(store.Delete(victim.Entry.Id, TestData.Now));
        Assert.Equal(2, store.Prune(new RetentionPolicy(MaxItems: 2, MaxAge: null, MaxTotalBytes: 0), TestData.Now));
        Assert.Equal(2, store.GetStats().Count);
    }

    /// <summary>Only 256-bit keys are accepted.</summary>
    [Fact]
    public void Constructor_RejectsWrongKeyLength() =>
        Assert.Throws<ArgumentException>(() => new ClipStore(temp.DatabasePath, new byte[16]));

    /// <summary>Moving a database takes its WAL/SHM sidecars along and never overwrites a target.</summary>
    [Fact]
    public void MoveDatabaseFiles_MovesSidecarsAndRefusesOverwrite()
    {
        var source = Path.Combine(temp.Path, "old", "history.db");
        var destination = Path.Combine(temp.Path, "new", "history.db");
        Assert.False(ClipStore.MoveDatabaseFiles(source, destination));

        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.WriteAllText(source + suffix, suffix);
        }

        Assert.True(ClipStore.MoveDatabaseFiles(source, destination));
        Assert.Equal("-wal", File.ReadAllText(destination + "-wal"));
        Assert.True(File.Exists(destination + "-shm"));
        Assert.False(File.Exists(source));

        File.WriteAllText(source, "another");
        Assert.Throws<IOException>(() => ClipStore.MoveDatabaseFiles(source, destination));
        Assert.True(File.Exists(source));
    }

    /// <summary>First run creates a key and an encrypted store in the binding's folder; the next run reuses both.</summary>
    [Fact]
    public void MachineBound_CreatesThenReopens()
    {
        var paths = new AppPaths(temp.Path);
        var binding = Binding("machine-a");

        var first = MachineBoundHistory.Open(paths, new FakeKeyProtector(), binding, TestData.Now);
        Assert.True(first.CreatedKey);
        Assert.False(first.AdoptedLegacyDatabase);
        Assert.Null(first.QuarantinedDirectory);
        Assert.True(first.Store.IsEncrypted);
        Assert.Equal(paths.StoreDirectory(MachineBinding.StoreId(binding)), first.StoreDirectory);
        Assert.True(File.Exists(Path.Combine(first.StoreDirectory, MachineBoundHistory.KeyFileName)));
        Add(first.Store, TestData.Text(Marker));
        SqliteConnection.ClearAllPools();

        var second = MachineBoundHistory.Open(paths, new FakeKeyProtector(), binding, TestData.Now);
        Assert.False(second.CreatedKey);
        Assert.Equal(first.StoreId, second.StoreId);
        Assert.Equal(Marker, second.Store.Query(new ClipQuery()).Single().Preview);
        SqliteConnection.ClearAllPools();
        AssertNoPlaintext(Path.Combine(second.StoreDirectory, MachineBoundHistory.DatabaseFileName));
    }

    /// <summary>The v0.1 plaintext history.db is moved into the store and encrypted, keeping its entries.</summary>
    [Fact]
    public void MachineBound_AdoptsLegacyDatabase()
    {
        var paths = new AppPaths(temp.Path);
        var legacy = new ClipStore(paths.LegacyDatabasePath);
        legacy.Initialize();
        Add(legacy, TestData.Text(Marker + " from v0.1"));
        SqliteConnection.ClearAllPools();

        var opened = MachineBoundHistory.Open(paths, new FakeKeyProtector(), Binding("machine-a"), TestData.Now);
        Assert.True(opened.AdoptedLegacyDatabase);
        Assert.True(opened.Store.MigratedFromPlaintext);
        Assert.False(File.Exists(paths.LegacyDatabasePath));
        Assert.Equal(Marker + " from v0.1", opened.Store.Query(new ClipQuery()).Single().Preview);
        SqliteConnection.ClearAllPools();
        AssertNoPlaintext(Path.Combine(opened.StoreDirectory, MachineBoundHistory.DatabaseFileName));
    }

    /// <summary>When the key cannot be unsealed any more, the store is set aside (not deleted) and a fresh one starts.</summary>
    [Fact]
    public void MachineBound_QuarantinesWhenKeyIsLost()
    {
        var paths = new AppPaths(temp.Path);
        var binding = Binding("machine-a");
        Add(MachineBoundHistory.Open(paths, new FakeKeyProtector(), binding, TestData.Now).Store, TestData.Text(Marker));
        SqliteConnection.ClearAllPools();

        var reopened = MachineBoundHistory.Open(paths, new FakeKeyProtector { FailUnprotect = true }, binding, TestData.Now);
        Assert.NotNull(reopened.QuarantinedDirectory);
        Assert.NotNull(reopened.QuarantineReason);
        Assert.True(File.Exists(Path.Combine(reopened.QuarantinedDirectory!, MachineBoundHistory.DatabaseFileName)));
        Assert.True(reopened.CreatedKey);
        Assert.Equal(0, reopened.Store.GetStats().Count);
    }

    /// <summary>An encrypted database whose key file vanished is quarantined instead of being overwritten.</summary>
    [Fact]
    public void MachineBound_QuarantinesWhenKeyFileIsMissing()
    {
        var paths = new AppPaths(temp.Path);
        var binding = Binding("machine-a");
        var first = MachineBoundHistory.Open(paths, new FakeKeyProtector(), binding, TestData.Now);
        Add(first.Store, TestData.Text(Marker));
        SqliteConnection.ClearAllPools();
        File.Delete(Path.Combine(first.StoreDirectory, MachineBoundHistory.KeyFileName));

        var second = MachineBoundHistory.Open(paths, new FakeKeyProtector(), binding, TestData.Now);
        Assert.NotNull(second.QuarantinedDirectory);
        Assert.Equal(0, second.Store.GetStats().Count);

        // A second quarantine in the same second gets a distinct folder name.
        SqliteConnection.ClearAllPools();
        File.Delete(Path.Combine(second.StoreDirectory, MachineBoundHistory.KeyFileName));
        var third = MachineBoundHistory.Open(paths, new FakeKeyProtector(), binding, TestData.Now);
        Assert.NotEqual(second.QuarantinedDirectory, third.QuarantinedDirectory);
        Assert.True(Directory.Exists(second.QuarantinedDirectory));
        Assert.True(Directory.Exists(third.QuarantinedDirectory));
    }

    /// <summary>Two machines (or users) sharing a data folder get separate, mutually unreadable stores.</summary>
    [Fact]
    public void MachineBound_SeparatesMachines()
    {
        var paths = new AppPaths(temp.Path);
        var a = MachineBoundHistory.Open(paths, new FakeKeyProtector(), Binding("machine-a"), TestData.Now);
        var b = MachineBoundHistory.Open(paths, new FakeKeyProtector(), Binding("machine-b"), TestData.Now);
        Add(a.Store, TestData.Text("only on a"));

        Assert.NotEqual(a.StoreDirectory, b.StoreDirectory);
        Assert.Equal(0, b.Store.GetStats().Count);
        Assert.Equal(1, a.Store.GetStats().Count);
    }

    /// <summary>Creates and initializes an encrypted store at the temp database path.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The store.</returns>
    private ClipStore NewEncrypted(byte[] key)
    {
        var store = new ClipStore(temp.DatabasePath, key);
        store.Initialize();
        return store;
    }

    /// <summary>A fresh random 256-bit key.</summary>
    /// <returns>The key.</returns>
    private static byte[] RandomKey() => RandomNumberGenerator.GetBytes(ClipStore.KeyLength);

    /// <summary>A binding for a named fake machine (fixed user).</summary>
    /// <param name="machine">Any label; hashed into a GUID.</param>
    /// <returns>The binding.</returns>
    private static byte[] Binding(string machine) =>
        MachineBinding.Derive(Uuid.V5(Uuid.DnsNamespace, machine).ToString(), "S-1-5-21-1-2-3-1001");

    /// <summary>Classifies and stores a capture as a live copy.</summary>
    /// <param name="store">Target store.</param>
    /// <param name="capture">The capture.</param>
    /// <returns>The upsert result.</returns>
    private static UpsertResult? Add(ClipStore store, ClipCapture capture) =>
        store.Upsert(capture, ContentClassifier.Classify(capture)!, null, bumpIfExists: capture.Origin == ClipOrigin.Captured);

    /// <summary>Asserts that no file of the database (main, WAL, SHM, journal) contains <see cref="Marker"/> in UTF-8 or UTF-16.</summary>
    /// <param name="databasePath">Main database path.</param>
    private static void AssertNoPlaintext(string databasePath)
    {
        var needles = new[] { Encoding.UTF8.GetBytes(Marker), Encoding.Unicode.GetBytes(Marker), Encoding.UTF8.GetBytes("שלום") };
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(databasePath)!, Path.GetFileName(databasePath) + "*"))
        {
            byte[] bytes;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }

            foreach (var needle in needles)
            {
                Assert.True(bytes.AsSpan().IndexOf(needle) < 0, $"Plaintext found in {Path.GetFileName(file)}.");
            }
        }
    }
}
