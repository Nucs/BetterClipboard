using System.Text.Json.Nodes;
using BetterClipboard.Core.Settings;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the retention defaults of 2026-11-20 (CLAUDE.md §2.60): no item limit and a 16 GB size budget, and the move of
/// settings files that still hold format 1's defaults (10,000 items, 4,096 MB) to them, while every value a user picked
/// stays.
/// </summary>
public sealed class HistoryLimitsSettingsTests : IDisposable
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();

    /// <summary>Deletes the temp folder.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>A fresh install gets no item limit and a 16 GB budget, written as format 2.</summary>
    [Fact]
    public void Defaults_AreUnlimitedItemsAndSixteenGigabytes()
    {
        var fresh = new AppSettings();
        Assert.Equal((0, 16_384, 2), (fresh.MaxItems, fresh.MaxTotalSizeMB, fresh.SchemaVersion));
        Assert.Equal((AppSettings.DefaultMaxItems, AppSettings.DefaultMaxTotalSizeMB), (fresh.MaxItems, fresh.MaxTotalSizeMB));

        var path = SettingsPath();
        var store = new SettingsStore(path);
        store.Load();
        store.Update(s => s);
        var written = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(2, (int)written["SchemaVersion"]!);
        Assert.Equal(0, (int)written["MaxItems"]!);
        Assert.Equal(16_384, (int)written["MaxTotalSizeMB"]!);
    }

    /// <summary>
    /// The owner's case: a file a version before 2026-11-20 wrote, still at its defaults, reads as unlimited items and a
    /// 16 GB budget; the next save writes format 2, and reading it again changes nothing more.
    /// </summary>
    [Fact]
    public void FormatOne_AtItsDefaults_MovesToTheNewDefaults()
    {
        var path = WriteFormatOne(maxItems: 10_000, maxTotalSizeMB: 4096);
        var store = new SettingsStore(path);
        var loaded = store.Load();
        Assert.Equal((0, 16_384), (loaded.MaxItems, loaded.MaxTotalSizeMB));
        Assert.Equal(77, loaded.RetentionDays); // the rest of the file was read as written

        // Nothing is written by reading: the file keeps format 1 until the next save.
        Assert.Equal(1, (int)JsonNode.Parse(File.ReadAllText(path))!["SchemaVersion"]!);

        store.Update(s => s with { IsCapturePaused = true });
        var saved = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal((2, 0, 16_384), ((int)saved["SchemaVersion"]!, (int)saved["MaxItems"]!, (int)saved["MaxTotalSizeMB"]!));

        var again = new SettingsStore(path).Load();
        Assert.Equal((0, 16_384), (again.MaxItems, again.MaxTotalSizeMB));
        Assert.Null(AppSettings.DescribeMigration(again));
    }

    /// <summary>Values a user picked are kept; only the limit still at format 1's default moves.</summary>
    /// <param name="maxItems">The item limit in the format-1 file.</param>
    /// <param name="maxTotalSizeMB">The size budget in the format-1 file.</param>
    /// <param name="expectedItems">The item limit read.</param>
    /// <param name="expectedMB">The size budget read.</param>
    [Theory]
    [InlineData(5_000, 2_048, 5_000, 2_048)]
    [InlineData(10_000, 2_048, 0, 2_048)]
    [InlineData(5_000, 4_096, 5_000, 16_384)]
    [InlineData(0, 0, 0, 0)]
    [InlineData(10_001, 4_095, 10_001, 4_095)]
    [InlineData(1_000_000, 1_048_576, 1_000_000, 1_048_576)]
    public void FormatOne_ChosenValues_StayAsTheyAre(int maxItems, int maxTotalSizeMB, int expectedItems, int expectedMB)
    {
        var loaded = new SettingsStore(WriteFormatOne(maxItems, maxTotalSizeMB)).Load();
        Assert.Equal((expectedItems, expectedMB), (loaded.MaxItems, loaded.MaxTotalSizeMB));
    }

    /// <summary>
    /// A file without the member was written by hand (an administrator's pre-seeded file): it reads as the current format,
    /// so its 10,000 and 4,096 are values someone chose, and stay.
    /// </summary>
    [Fact]
    public void FileWithoutAFormat_IsNeverMigrated()
    {
        var path = SettingsPath();
        File.WriteAllText(path, "{ \"MaxItems\": 10000, \"MaxTotalSizeMB\": 4096 }");
        var loaded = new SettingsStore(path).Load();
        Assert.Equal((10_000, 4_096), (loaded.MaxItems, loaded.MaxTotalSizeMB));
    }

    /// <summary>
    /// 10,000 items and 4,096 MB chosen after the move are the user's: format 2 is written, so no later start moves them.
    /// </summary>
    [Fact]
    public void FormatTwo_TheOldDefaultsChosenAgain_AreKept()
    {
        var path = WriteFormatOne(maxItems: 10_000, maxTotalSizeMB: 4096);
        var store = new SettingsStore(path);
        store.Load();
        store.Update(s => s with { MaxItems = 10_000, MaxTotalSizeMB = 4096 });

        var reloaded = new SettingsStore(path).Load();
        Assert.Equal((10_000, 4_096), (reloaded.MaxItems, reloaded.MaxTotalSizeMB));
    }

    /// <summary>The read-only snapshot <c>bclip</c> and the edition move use reads a format-1 file the same way.</summary>
    [Fact]
    public void Snapshot_MigratesTheSameWay()
    {
        var snapshot = SettingsStore.TryReadSnapshot(WriteFormatOne(maxItems: 10_000, maxTotalSizeMB: 4096))!;
        Assert.Equal((0, 16_384, 2), (snapshot.MaxItems, snapshot.MaxTotalSizeMB, snapshot.SchemaVersion));
    }

    /// <summary>The log line names what moved, content-free; nothing for a file that moves nothing or is current.</summary>
    [Fact]
    public void DescribeMigration_NamesWhatMoved()
    {
        Assert.Equal(
            "Settings of format 1 read as format 2: the item limit 10,000 (format 1's default) is now unlimited, and the history size budget 4,096 MB (format 1's default) is now 16,384 MB.",
            AppSettings.DescribeMigration(new AppSettings { SchemaVersion = 1, MaxItems = 10_000, MaxTotalSizeMB = 4096 }));
        Assert.Equal(
            "Settings of format 1 read as format 2: the item limit 10,000 (format 1's default) is now unlimited.",
            AppSettings.DescribeMigration(new AppSettings { SchemaVersion = 1, MaxItems = 10_000, MaxTotalSizeMB = 2048 }));
        Assert.Null(AppSettings.DescribeMigration(new AppSettings { SchemaVersion = 1, MaxItems = 500, MaxTotalSizeMB = 2048 }));
        Assert.Null(AppSettings.DescribeMigration(new AppSettings { MaxItems = 10_000, MaxTotalSizeMB = 4096 }));
        Assert.Throws<ArgumentNullException>(() => AppSettings.DescribeMigration(null!));
    }

    /// <summary>Normalizing twice gives what normalizing once gave (it runs on every load and save).</summary>
    [Fact]
    public void Normalize_IsIdempotent()
    {
        var once = new AppSettings { SchemaVersion = 1, MaxItems = 10_000, MaxTotalSizeMB = 4096 }.Normalize();
        var twice = once.Normalize();
        Assert.Equal((once.SchemaVersion, once.MaxItems, once.MaxTotalSizeMB), (twice.SchemaVersion, twice.MaxItems, twice.MaxTotalSizeMB));
        Assert.Equal((2, 0, 16_384), (twice.SchemaVersion, twice.MaxItems, twice.MaxTotalSizeMB));
    }

    /// <summary>The settings file's path in the temp folder.</summary>
    /// <returns>The path.</returns>
    private string SettingsPath() => Path.Combine(temp.Path, "settings.json");

    /// <summary>
    /// Writes a settings file as a version from before 2026-11-20 did: format 1, the two limits, and one other member to
    /// show the rest of the file is read.
    /// </summary>
    /// <param name="maxItems">The item limit written.</param>
    /// <param name="maxTotalSizeMB">The size budget written.</param>
    /// <returns>The file's path.</returns>
    private string WriteFormatOne(int maxItems, int maxTotalSizeMB)
    {
        var path = SettingsPath();
        File.WriteAllText(path, $$"""
            {
              "SchemaVersion": 1,
              "MaxItems": {{maxItems}},
              "RetentionDays": 77,
              "MaxTotalSizeMB": {{maxTotalSizeMB}}
            }
            """);
        return path;
    }
}
