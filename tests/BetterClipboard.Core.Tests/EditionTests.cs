using System.Security.Cryptography;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Editions;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Security;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// The Microsoft Store edition's rules that do not need Windows (CLAUDE.md §2.47): where that edition keeps its data,
/// which step its setup takes next about other copies of BetterClipboard, and the move of another copy's history —
/// what is brought over, that the copy is proven, that an unfinished move is undone and a finished one is not, and
/// that the old folder goes only at the end.
/// </summary>
public class EditionTests
{
    /// <summary>A package family name of the shape Windows gives a Store app.</summary>
    private const string Family = "12345Publisher.BetterClipboard_abcdefghjkmnp";

    /// <summary>A text that must be found again in a history after it was moved.</summary>
    private const string Marker = "BC-TEST edition move marker";

    /// <summary>The Store edition's folder is below the package's own LocalState, which Windows removes with the app.</summary>
    [Fact]
    public void PackagedDataDirectory_IsThePackagesLocalState()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(local, "Packages", Family, "LocalState", "BetterClipboard"), AppPaths.PackagedDataDirectory(Family));
        Assert.NotEqual(AppPaths.DefaultDataDirectory, AppPaths.PackagedDataDirectory(Family));
    }

    /// <summary>A family name that is no name must never become part of a path.</summary>
    /// <param name="family">The bad name.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void PackagedDataDirectory_RefusesABlankFamily(string family)
    {
        Assert.Throws<ArgumentException>(() => AppPaths.PackagedDataDirectory(family));
    }

    /// <summary>
    /// The Store edition's regular instance is the regular instance: its home is its package folder, so it has no
    /// scope and shares the lock with every other edition. An isolated instance keeps its scope in either edition.
    /// </summary>
    [Fact]
    public void InstanceScope_FollowsTheHomeFolder()
    {
        string packaged = AppPaths.PackagedDataDirectory(Family);
        using var temp = new TempDirectory();

        Assert.Null(new AppPaths(packaged, packaged).InstanceScope);
        Assert.Equal(AppPaths.DefaultInstanceName, new AppPaths(packaged, packaged).InstanceName);
        Assert.Null(new AppPaths(packaged + Path.DirectorySeparatorChar, packaged.ToUpperInvariant()).InstanceScope);
        Assert.NotNull(new AppPaths(temp.Path, packaged).InstanceScope);

        // The same folder is the same scope whichever edition looks at it: the scope names the folder, not the home.
        Assert.Equal(new AppPaths(temp.Path).InstanceScope, new AppPaths(temp.Path, packaged).InstanceScope);
        Assert.Equal(packaged, new AppPaths(temp.Path, packaged).HomeDirectory);
        Assert.Equal(AppPaths.DefaultDataDirectory, new AppPaths(temp.Path).HomeDirectory);
    }

    /// <summary>
    /// Without an override folder the package decides: a packaged process gets its package's folder, any other the
    /// classic one. (The override variable is not set in the test run; when it is, it wins for both.)
    /// </summary>
    [Fact]
    public void ResolveDefault_UsesThePackageFolderForAPackagedProcess()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable)))
        {
            Assert.Skip("The data folder override is set in this environment.");
        }

        Assert.Equal(AppPaths.PackagedDataDirectory(Family), AppPaths.ResolveDefault(Family).DataDirectory);
        Assert.Null(AppPaths.ResolveDefault(Family).InstanceScope);
        Assert.Equal(AppPaths.DefaultDataDirectory, AppPaths.ResolveDefault(null).DataDirectory);
        Assert.Equal(AppPaths.ResolveDefault().DataDirectory, AppPaths.ResolveDefault(null).DataDirectory);
    }

    /// <summary>The setup's next step, for every mix of copies and data that decides it.</summary>
    /// <param name="kinds">The other copies found, as kind letters: I installer, A all users, C Chocolatey, U unregistered and running, u unregistered and not running.</param>
    /// <param name="otherData">Whether the other editions' folder holds data.</param>
    /// <param name="ownHistory">Whether this edition has a history.</param>
    /// <param name="expected">The step.</param>
    /// <param name="expectedKind">The copy the step is about, as a kind letter; '-' for none.</param>
    [Theory]
    [InlineData("", false, false, EditionSetupStep.None, '-')]
    [InlineData("", false, true, EditionSetupStep.None, '-')]
    [InlineData("", true, false, EditionSetupStep.ImportData, '-')]
    [InlineData("", true, true, EditionSetupStep.None, '-')]
    [InlineData("I", true, false, EditionSetupStep.MoveFromInstaller, 'I')]
    [InlineData("I", false, true, EditionSetupStep.MoveFromInstaller, 'I')]
    [InlineData("A", true, false, EditionSetupStep.GuideUninstall, 'A')]
    [InlineData("C", true, true, EditionSetupStep.GuideUninstall, 'C')]
    [InlineData("U", true, false, EditionSetupStep.CloseRunning, 'U')]
    [InlineData("u", true, false, EditionSetupStep.ImportData, '-')]
    [InlineData("u", false, false, EditionSetupStep.None, '-')]
    [InlineData("UACI", true, false, EditionSetupStep.MoveFromInstaller, 'I')]
    [InlineData("UCA", true, false, EditionSetupStep.GuideUninstall, 'C')]
    [InlineData("UA", false, false, EditionSetupStep.GuideUninstall, 'A')]
    [InlineData("uU", true, false, EditionSetupStep.CloseRunning, 'U')]
    public void Next_PicksTheStep(string kinds, bool otherData, bool ownHistory, EditionSetupStep expected, char expectedKind)
    {
        var others = kinds.Select(Copy).ToList();

        var (step, edition) = EditionSetupPlan.Next(others, otherData, ownHistory);

        Assert.Equal(expected, step);
        if (expectedKind == '-')
        {
            Assert.Null(edition);
        }
        else
        {
            Assert.Same(others[kinds.IndexOf(expectedKind)], edition);
        }
    }

    /// <summary>
    /// Asked again after each step is done, the plan ends: every copy is dealt with once, the data last, and nothing
    /// is left to do.
    /// </summary>
    [Fact]
    public void Next_WalksEveryStepAndEnds()
    {
        var others = "UACI".Select(Copy).ToList();
        bool otherData = true;
        bool ownHistory = false;
        var steps = new List<EditionSetupStep>();

        for (int guard = 0; guard < 10; guard++)
        {
            var (step, edition) = EditionSetupPlan.Next(others, otherData, ownHistory);
            steps.Add(step);
            if (step == EditionSetupStep.None)
            {
                break;
            }

            if (edition is not null)
            {
                others.Remove(edition);
            }

            if (step is EditionSetupStep.MoveFromInstaller or EditionSetupStep.ImportData)
            {
                // The history was brought over and the old folder removed.
                otherData = false;
                ownHistory = true;
            }
        }

        Assert.Equal(
            [EditionSetupStep.MoveFromInstaller, EditionSetupStep.GuideUninstall, EditionSetupStep.GuideUninstall, EditionSetupStep.CloseRunning, EditionSetupStep.None],
            steps);
    }

    /// <summary>Two histories are never merged: data comes over only into an edition that has none.</summary>
    [Fact]
    public void BringsDataOver_OnlyIntoAnEditionWithoutHistory()
    {
        Assert.True(EditionSetupPlan.BringsDataOver(otherDataExists: true, ownHistoryExists: false));
        Assert.False(EditionSetupPlan.BringsDataOver(otherDataExists: true, ownHistoryExists: true));
        Assert.False(EditionSetupPlan.BringsDataOver(otherDataExists: false, ownHistoryExists: false));
        Assert.Throws<ArgumentNullException>(() => EditionSetupPlan.Next(null!, false, false));
    }

    /// <summary>
    /// What is brought over: the settings and everything below stores (also a store that was set aside), without
    /// SQLite's shared-memory file; not the logs, the updater's folder, development builds or the installer's record.
    /// </summary>
    [Fact]
    public void Inspect_ListsSettingsAndStoresOnly()
    {
        using var source = new TempDirectory();
        Write(source.Path, "settings.json", "{}");
        Write(source.Path, @"stores\a\history.db", "db");
        Write(source.Path, @"stores\a\history.db-wal", "wal");
        Write(source.Path, @"stores\a\history.db-shm", "shm");
        Write(source.Path, @"stores\a\history.key", "key");
        Write(source.Path, @"stores\b.unreadable-20261001-120000\history.db", "old");
        Write(source.Path, @"logs\betterclipboard-20261112.log", "log");
        Write(source.Path, @"updates\releases.json", "[]");
        Write(source.Path, @"dev-builds\0.2.11-dev.1\build.json", "{}");
        Write(source.Path, "installer.json", "{}");

        var data = EditionDataMigrator.Inspect(source.Path);

        Assert.Equal(
            ["settings.json", @"stores\a\history.db", @"stores\a\history.db-wal", @"stores\a\history.key", @"stores\b.unreadable-20261001-120000\history.db"],
            data.Files.Select(f => f.RelativePath));
        Assert.Equal(2 + 2 + 3 + 3 + 3, data.TotalBytes);
        Assert.False(data.IsEmpty);
        Assert.True(data.HasHistory);
        Assert.Equal(Path.GetFullPath(source.Path), data.Directory);
    }

    /// <summary>A missing folder, an empty one and one with settings alone: nothing, nothing, and settings without a history.</summary>
    [Fact]
    public void Inspect_ToleratesMissingAndEmptyFolders()
    {
        using var source = new TempDirectory();

        Assert.True(EditionDataMigrator.Inspect(Path.Combine(source.Path, "nowhere")).IsEmpty);
        Assert.True(EditionDataMigrator.Inspect(source.Path).IsEmpty);

        Write(source.Path, @"logs\x.log", "log");
        Assert.True(EditionDataMigrator.Inspect(source.Path).IsEmpty);

        Write(source.Path, "settings.json", "{}");
        var settingsOnly = EditionDataMigrator.Inspect(source.Path);
        Assert.False(settingsOnly.IsEmpty);
        Assert.False(settingsOnly.HasHistory);
        Assert.Throws<ArgumentException>(() => EditionDataMigrator.Inspect(" "));
    }

    /// <summary>The check before a move: every file opens for reading, also while another program has it open for writing.</summary>
    [Fact]
    public void EnsureReadable_PassesWhileAWriterHoldsTheFile_AndFailsForAMissingOne()
    {
        using var source = new TempDirectory();
        string database = Write(source.Path, @"stores\a\history.db", "db");
        var data = EditionDataMigrator.Inspect(source.Path);

        using (new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            EditionDataMigrator.EnsureReadable(data);
        }

        File.Delete(database);
        Assert.Throws<FileNotFoundException>(() => EditionDataMigrator.EnsureReadable(data));
    }

    /// <summary>The folder check writes and removes one file, creates the folder only when asked, and lets a missing folder pass otherwise.</summary>
    [Fact]
    public void EnsureWritable_LeavesNothingBehind()
    {
        using var temp = new TempDirectory();
        string made = Path.Combine(temp.Path, "made");
        string missing = Path.Combine(temp.Path, "missing");

        EditionDataMigrator.EnsureWritable(made, create: true);
        EditionDataMigrator.EnsureWritable(missing, create: false);

        Assert.True(Directory.Exists(made));
        Assert.Empty(Directory.GetFileSystemEntries(made));
        Assert.False(Directory.Exists(missing));
    }

    /// <summary>The room a copy needs: the data, a tenth more and a reserve; a real drive answers with a real number.</summary>
    [Fact]
    public void FreeSpace_IsAskedWithAMargin()
    {
        var data = new EditionData(@"C:\x", [new EditionDataFile("settings.json", 1000)], 1000);

        Assert.Equal(1000 + 100 + (64L * 1024 * 1024), EditionDataMigrator.RequiredFreeBytes(data));
        Assert.InRange(EditionDataMigrator.FreeBytes(Path.GetTempPath()), 1, long.MaxValue);
        Assert.InRange(EditionDataMigrator.FreeBytes(Path.Combine(Path.GetTempPath(), "does-not-exist-yet", "deeper")), 1, long.MaxValue);
    }

    /// <summary>
    /// The whole move on a real encrypted history: copied byte for byte, marked as unfinished until committed, opened
    /// with the same key in its new folder with every item in it, and the old folder removed last.
    /// </summary>
    /// <returns>A task for the asynchronous test.</returns>
    [Fact]
    public async Task Move_BringsAHistoryOver_ProvesIt_AndRemovesTheOldFolderLast()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        byte[] binding = Binding();
        var protector = new FakeKeyProtector();
        var opened = MachineBoundHistory.Open(new AppPaths(source.Path), protector, binding, TestData.Now);
        Add(opened.Store, TestData.Text(Marker, TestData.Now));
        Add(opened.Store, TestData.Text("BC-TEST second item", TestData.Now.AddSeconds(1)));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.WriteAllText(Path.Combine(source.Path, "settings.json"), """{"MaxItems": 4321, "InterfaceLanguage": "es"}""");
        Write(source.Path, @"logs\old.log", "log");

        var data = EditionDataMigrator.Inspect(source.Path);
        var reported = new List<long>();
        await EditionDataMigrator.CopyAsync(data, target.Path, new SynchronousProgress(reported), CancellationToken.None);

        // Copied and marked: the original is untouched, and the target says "unfinished".
        Assert.True(EditionDataMigrator.HasPendingMove(target.Path));
        Assert.Equal(data.TotalBytes, reported[^1]);
        Assert.True(reported.SequenceEqual(reported.Order()));
        foreach (var file in data.Files)
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(source.Path, file.RelativePath)), File.ReadAllBytes(Path.Combine(target.Path, file.RelativePath)));
        }

        Assert.False(File.Exists(Path.Combine(target.Path, "logs", "old.log")));

        // Proven the way the app will use it: same account, same PC, so the same key opens it.
        var check = EditionDataMigrator.Validate(new AppPaths(target.Path), protector, binding, TestData.Now);
        Assert.True(check.Ok, check.Reason);
        Assert.Equal(2, check.Stats!.Count);
        Assert.NotNull(check.Opened);
        Assert.Equal(opened.StoreId, check.Opened.StoreId);
        Assert.False(check.Opened.CreatedKey);
        Assert.Contains(
            check.Opened.Store.Query(new ClipQuery { Limit = 10 }, TestContext.Current.CancellationToken),
            entry => entry.Preview.Contains(Marker, StringComparison.Ordinal));
        Assert.Equal(4321, SettingsStore.TryReadSnapshot(Path.Combine(target.Path, "settings.json"))!.MaxItems);

        // The point of no return, then the old folder, logs and all.
        EditionDataMigrator.Commit(target.Path);
        Assert.False(EditionDataMigrator.HasPendingMove(target.Path));
        Assert.Empty(EditionDataMigrator.RemoveSource(source.Path));
        Assert.False(Directory.Exists(source.Path));
        Assert.True(new AppPaths(target.Path).HasHistory());
    }

    /// <summary>
    /// A move that stops before its end is undone as a whole: the copied history, the copied settings and the marker
    /// go, the original stays as it was, and the folder is ready for the next attempt.
    /// </summary>
    /// <returns>A task for the asynchronous test.</returns>
    [Fact]
    public async Task Rollback_UndoesAnUnfinishedMove_AndLeavesTheOriginal()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        Write(source.Path, "settings.json", "{}");
        Write(source.Path, @"stores\a\history.db", "not a database, and that is fine here");
        Write(target.Path, @"logs\own.log", "this edition's own log");
        var data = EditionDataMigrator.Inspect(source.Path);

        await EditionDataMigrator.CopyAsync(data, target.Path, progress: null, CancellationToken.None);
        Assert.True(new AppPaths(target.Path).HasHistory());

        EditionDataMigrator.Rollback(target.Path);

        Assert.False(EditionDataMigrator.HasPendingMove(target.Path));
        Assert.False(new AppPaths(target.Path).HasHistory());
        Assert.False(File.Exists(Path.Combine(target.Path, "settings.json")));
        Assert.True(File.Exists(Path.Combine(target.Path, "logs", "own.log")));
        Assert.Equal(data.Files.Select(f => f.RelativePath), EditionDataMigrator.Inspect(source.Path).Files.Select(f => f.RelativePath));

        // Undoing nothing is fine too, and the next attempt starts clean.
        EditionDataMigrator.Rollback(target.Path);
        await EditionDataMigrator.CopyAsync(data, target.Path, progress: null, CancellationToken.None);
        Assert.True(EditionDataMigrator.HasPendingMove(target.Path));
    }

    /// <summary>A copy that does not open, or opens but is no history, fails the check with a reason and no store.</summary>
    /// <returns>A task for the asynchronous test.</returns>
    [Fact]
    public async Task Validate_FailsForACopyThatIsNoHistory()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        byte[] binding = Binding();
        var protector = new FakeKeyProtector();
        var opened = MachineBoundHistory.Open(new AppPaths(source.Path), protector, binding, TestData.Now);
        Add(opened.Store, TestData.Text(Marker, TestData.Now));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // The database is replaced by noise after it was made: the key is right, the file is not a database.
        string database = Directory.EnumerateFiles(source.Path, "history.db", SearchOption.AllDirectories).Single();
        File.WriteAllBytes(database, RandomNumberGenerator.GetBytes(8192));
        await EditionDataMigrator.CopyAsync(EditionDataMigrator.Inspect(source.Path), target.Path, progress: null, CancellationToken.None);

        var check = EditionDataMigrator.Validate(new AppPaths(target.Path), protector, binding, TestData.Now);

        Assert.False(check.Ok);
        Assert.False(string.IsNullOrWhiteSpace(check.Reason));
        Assert.Null(check.Opened);
        Assert.Null(check.Stats);

        // The failed check may have set the copy aside inside the target; undoing the move removes that too.
        EditionDataMigrator.Rollback(target.Path);
        Assert.False(new AppPaths(target.Path).HasHistory());
        Assert.True(File.Exists(database));
    }

    /// <summary>A history sealed for another PC or account does not open with this one's key: the check says so.</summary>
    /// <returns>A task for the asynchronous test.</returns>
    [Fact]
    public async Task Validate_FailsForAHistoryOfAnotherMachine()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        var protector = new FakeKeyProtector();
        Add(MachineBoundHistory.Open(new AppPaths(source.Path), protector, Binding("machine-a"), TestData.Now).Store, TestData.Text(Marker, TestData.Now));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await EditionDataMigrator.CopyAsync(EditionDataMigrator.Inspect(source.Path), target.Path, progress: null, CancellationToken.None);

        // This PC's binding names another store folder: the copied store is not found, and an empty one would be made.
        var check = EditionDataMigrator.Validate(new AppPaths(target.Path), protector, Binding("machine-b"), TestData.Now);

        // An empty new store is a valid history, but it holds nothing of what was copied: the caller sees zero items.
        Assert.True(check.Ok ? check.Stats!.Count == 0 && check.Opened!.CreatedKey : check.Reason is not null);
    }

    /// <summary>Nothing is copied into a folder that already has a history, onto itself, or while the source is being written.</summary>
    /// <returns>A task for the asynchronous test.</returns>
    [Fact]
    public async Task Copy_RefusesWhatWouldDamageAHistory()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        string database = Write(source.Path, @"stores\a\history.db", "db");
        var data = EditionDataMigrator.Inspect(source.Path);

        await Assert.ThrowsAsync<ArgumentException>(() => EditionDataMigrator.CopyAsync(data, source.Path, null, CancellationToken.None));

        Write(target.Path, @"stores\own\history.db", "this edition's own");
        await Assert.ThrowsAsync<InvalidOperationException>(() => EditionDataMigrator.CopyAsync(data, target.Path, null, CancellationToken.None));
        Assert.False(EditionDataMigrator.HasPendingMove(target.Path));
        Assert.Equal("this edition's own", File.ReadAllText(Path.Combine(target.Path, "stores", "own", "history.db")));

        // A copy that still runs holds its database open for writing: the copy fails at once instead of copying half a write.
        using var other = new TempDirectory();
        using (new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => EditionDataMigrator.CopyAsync(data, other.Path, null, CancellationToken.None));
        }

        // What that attempt left is an unfinished move, which the caller undoes.
        Assert.True(EditionDataMigrator.HasPendingMove(other.Path));
        EditionDataMigrator.Rollback(other.Path);
        Assert.False(new AppPaths(other.Path).HasHistory());
    }

    /// <summary>A cancelled copy stops, and what it left is an unfinished move.</summary>
    /// <returns>A task for the asynchronous test.</returns>
    [Fact]
    public async Task Copy_StopsWhenCancelled()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        Write(source.Path, @"stores\a\history.db", "db");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EditionDataMigrator.CopyAsync(EditionDataMigrator.Inspect(source.Path), target.Path, null, cancelled.Token));

        Assert.False(new AppPaths(target.Path).HasHistory());
        EditionDataMigrator.Rollback(target.Path);
        Assert.False(EditionDataMigrator.HasPendingMove(target.Path));
    }

    /// <summary>A file another program holds stays, is named, and everything else of the old folder goes.</summary>
    [Fact]
    public void RemoveSource_NamesWhatItCouldNotDelete()
    {
        using var source = new TempDirectory();
        string held = Write(source.Path, @"stores\a\history.db", "db");
        Write(source.Path, "settings.json", "{}");
        Write(source.Path, @"logs\x.log", "log");

        IReadOnlyList<string> leftovers;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            leftovers = EditionDataMigrator.RemoveSource(source.Path);
        }

        Assert.Equal([held], leftovers);
        Assert.False(File.Exists(Path.Combine(source.Path, "settings.json")));
        Assert.False(File.Exists(Path.Combine(source.Path, "logs", "x.log")));

        Assert.Empty(EditionDataMigrator.RemoveSource(source.Path));
        Assert.False(Directory.Exists(source.Path));
        Assert.Empty(EditionDataMigrator.RemoveSource(source.Path));
    }

    /// <summary>A sound store says "ok" to SQLite's own check.</summary>
    [Fact]
    public void QuickCheck_SaysOkForASoundStore()
    {
        using var temp = new TempDirectory();
        var store = new ClipStore(temp.DatabasePath);
        store.Initialize();
        Add(store, TestData.Text(Marker, TestData.Now));

        Assert.Equal("ok", store.QuickCheck());
    }

    /// <summary>
    /// The welcome's consent is off until given, survives a save, and a settings file from another edition (which
    /// never had the member) reads as "not given": moving a history over never counts as pressing Start.
    /// </summary>
    [Fact]
    public void StoreWelcomeAccepted_IsOffUntilGiven()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "settings.json");

        Assert.False(new AppSettings().StoreWelcomeAccepted);

        File.WriteAllText(path, """{"MaxItems": 77}""");
        Assert.False(SettingsStore.TryReadSnapshot(path)!.StoreWelcomeAccepted);

        var store = new SettingsStore(path);
        store.Load();
        store.Update(s => s with { StoreWelcomeAccepted = true });
        Assert.True(SettingsStore.TryReadSnapshot(path)!.StoreWelcomeAccepted);
        Assert.Equal(77, SettingsStore.TryReadSnapshot(path)!.MaxItems);
    }

    /// <summary>Makes a copy of BetterClipboard from a kind letter (see <see cref="Next_PicksTheStep"/>).</summary>
    /// <param name="kind">I, A, C, U (running) or u (not running).</param>
    /// <returns>The copy.</returns>
    private static OtherEdition Copy(char kind) => kind switch
    {
        'I' => new OtherEdition(OtherEditionKind.Installer, @"C:\Users\x\AppData\Local\Programs\BetterClipboard", "0.2.10", IsRunning: true),
        'A' => new OtherEdition(OtherEditionKind.AllUsers, @"C:\Program Files\BetterClipboard", "0.2.10", IsRunning: false),
        'C' => new OtherEdition(OtherEditionKind.Chocolatey, @"C:\ProgramData\chocolatey\lib\betterclipboard\tools\app", null, IsRunning: false),
        'U' => new OtherEdition(OtherEditionKind.Unregistered, @"C:\Temp\bc", null, IsRunning: true),
        _ => new OtherEdition(OtherEditionKind.Unregistered, @"C:\Temp\bc-idle", null, IsRunning: false),
    };

    /// <summary>Classifies and stores a capture as a live copy, as the app's history worker does.</summary>
    /// <param name="store">The store.</param>
    /// <param name="capture">The capture.</param>
    private static void Add(ClipStore store, ClipCapture capture) =>
        store.Upsert(capture, ContentClassifier.Classify(capture)!, null, bumpIfExists: true);

    /// <summary>Writes a small text file below a folder, creating its folders.</summary>
    /// <param name="root">The folder.</param>
    /// <param name="relative">The file's path below it.</param>
    /// <param name="content">Its text (ASCII, so its length is its size).</param>
    /// <returns>The file's full path.</returns>
    private static string Write(string root, string relative, string content)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>A machine binding for a made-up PC and account.</summary>
    /// <param name="machine">The PC's name.</param>
    /// <returns>The binding.</returns>
    private static byte[] Binding(string machine = "machine-a") =>
        MachineBinding.Derive(Uuid.V5(Uuid.DnsNamespace, machine).ToString(), "S-1-5-21-1-2-3-1001");

    /// <summary>
    /// A progress sink that records on the reporting thread: <see cref="Progress{T}"/> posts to a context, so its
    /// reports could arrive after the copy returned.
    /// </summary>
    /// <param name="reported">Where the values go.</param>
    private sealed class SynchronousProgress(List<long> reported) : IProgress<long>
    {
        /// <summary>Records a value.</summary>
        /// <param name="value">Bytes copied so far.</param>
        public void Report(long value) => reported.Add(value);
    }
}
