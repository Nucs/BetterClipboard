using BetterClipboard.Core.Editions;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// The Microsoft Store edition's shared data folder (CLAUDE.md §2.47, the owner's "Share it", 2026-11-14): which folder a
/// start decides on, that the shared folder is made by the program outside the package and proven once per Windows
/// build, that a private copy is repaired and never deleted, that an earlier history moves in only when the shared folder
/// has none, and that every failure falls back to the package's own folder with nothing lost.
/// </summary>
/// <remarks>
/// The test process has no package identity, so its own writes are real: "outside" is played by <see cref="FakeOutside"/>,
/// which does real file operations in a scratch folder and can be told to hide the probe (a redirected write) or to fail.
/// </remarks>
public class StoreDataFolderTests
{
    /// <summary>A package family name of the shape Windows gives a Store app.</summary>
    private const string Family = "Nucs.BetterClipboard_mgez7y7xa0my4";

    /// <summary>The Windows build of most tests.</summary>
    private const string Build = "10.0.26200.8875";

    /// <summary>A moment, so set-aside names and markers are predictable.</summary>
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 30, 0, TimeSpan.Zero);

    /// <summary>The places are the real paths Windows uses for a package, under the given Local AppData.</summary>
    [Fact]
    public void Places_AreTheRealPaths()
    {
        using var temp = new TempDirectory();
        var places = StoreDataPlaces.For(temp.Path, Family);

        Assert.Equal(Path.Combine(temp.Path, "BetterClipboard"), places.SharedDirectory);
        Assert.Equal(Path.Combine(temp.Path, "Packages", Family, "LocalState", "BetterClipboard"), places.PackageDirectory);
        Assert.Equal(Path.Combine(temp.Path, "Packages", Family, "LocalCache", "Local", "BetterClipboard"), places.RedirectDirectory);
        Assert.Equal(Path.Combine(temp.Path, "Packages", Family, "LocalState", "data-folder.json"), places.MarkerPath);
        Assert.Throws<ArgumentException>(() => StoreDataPlaces.For(temp.Path, " "));
        Assert.Equal(AppPaths.PackagedDataDirectory(Family), StoreDataPlaces.For(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Family).PackageDirectory);
    }

    /// <summary>
    /// bclip's read-only answer: the shared folder only with a recorded proof and no private copy shadowing it; the
    /// package's own folder for no marker, a malformed one, a fallback, or a private copy.
    /// </summary>
    [Fact]
    public void ReadOnly_SharedOnlyWithAProofAndNoPrivateCopy()
    {
        using var temp = new TempDirectory();
        var places = StoreDataPlaces.For(temp.Path, Family);

        Assert.Equal(places.PackageDirectory, StoreDataFolder.ResolveReadOnly(places));

        new StoreDataMarker(StoreDataMode.Shared, Build, Now, null).TryWrite(places.MarkerPath);
        Assert.Equal(places.SharedDirectory, StoreDataFolder.ResolveReadOnly(places));

        Directory.CreateDirectory(places.RedirectDirectory);
        Assert.Equal(places.PackageDirectory, StoreDataFolder.ResolveReadOnly(places));
        Directory.Delete(places.RedirectDirectory);

        new StoreDataMarker(StoreDataMode.Package, Build, Now, "BC-TEST reason").TryWrite(places.MarkerPath);
        Assert.Equal(places.PackageDirectory, StoreDataFolder.ResolveReadOnly(places));

        File.WriteAllText(places.MarkerPath, "{ not json");
        Assert.Equal(places.PackageDirectory, StoreDataFolder.ResolveReadOnly(places));
    }

    /// <summary>The marker survives a round trip, and anything that is not a whole marker reads as none.</summary>
    /// <param name="content">A file that is not a valid marker.</param>
    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"folder\":\"elsewhere\",\"windowsBuild\":\"1\",\"checkedUtc\":\"2026-11-14T09:30:00Z\"}")]
    [InlineData("{\"folder\":\"shared\",\"checkedUtc\":\"2026-11-14T09:30:00Z\"}")]
    [InlineData("{\"folder\":\"shared\",\"windowsBuild\":\"1\",\"checkedUtc\":\"not a time\"}")]
    public void Marker_RoundTripsAndRejectsAnythingElse(string content)
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "LocalState", "data-folder.json");
        var marker = new StoreDataMarker(StoreDataMode.Package, Build, Now, "BC-TEST reason");

        Assert.True(marker.TryWrite(path));
        Assert.Equal(marker, StoreDataMarker.TryRead(path));
        Assert.False(File.Exists(path + ".tmp"));

        File.WriteAllText(path, content);
        Assert.Null(StoreDataMarker.TryRead(path));
        Assert.Null(StoreDataMarker.TryRead(Path.Combine(temp.Path, "missing.json")));
    }

    /// <summary>
    /// A PC where nothing exists yet: the shared folder is made by the outside program (never by this process, whose own
    /// creation would be redirected), proven with a probe that is gone afterwards, and recorded for this build.
    /// </summary>
    [Fact]
    public void Establish_FreshPc_MakesProvesAndRecords()
    {
        using var temp = new TempDirectory();
        var places = StoreDataPlaces.For(temp.Path, Family);
        var outside = new FakeOutside();

        var decision = StoreDataFolder.Establish(places, outside, Build, Now);

        Assert.Equal(StoreDataMode.Shared, decision.Mode);
        Assert.Equal(places.SharedDirectory, decision.Directory);
        Assert.Equal(["mkdir BetterClipboard", "exists probe"], outside.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(places.SharedDirectory));
        Assert.Equal(new StoreDataMarker(StoreDataMode.Shared, Build, Now, null), StoreDataMarker.TryRead(places.MarkerPath));
        Assert.Contains(decision.Notes, note => note.Contains("Made the shared data folder", StringComparison.Ordinal));
    }

    /// <summary>A start on a build that was proven already costs no outside program at all.</summary>
    [Fact]
    public void Establish_SameBuild_DoesNothingTwice()
    {
        using var temp = new TempDirectory();
        var places = StoreDataPlaces.For(temp.Path, Family);
        StoreDataFolder.Establish(places, new FakeOutside(), Build, Now);
        var outside = new FakeOutside();

        var decision = StoreDataFolder.Establish(places, outside, Build, Now.AddDays(1));

        Assert.Equal(StoreDataMode.Shared, decision.Mode);
        Assert.Empty(outside.Calls);
        Assert.Empty(decision.Notes);
    }

    /// <summary>Another Windows build is proven again, and the marker names the new build.</summary>
    [Fact]
    public void Establish_NewBuild_ProvesAgain()
    {
        using var temp = new TempDirectory();
        var places = StoreDataPlaces.For(temp.Path, Family);
        StoreDataFolder.Establish(places, new FakeOutside(), Build, Now);
        var outside = new FakeOutside();

        var decision = StoreDataFolder.Establish(places, outside, "10.0.26200.9000", Now.AddDays(30));

        Assert.Equal(StoreDataMode.Shared, decision.Mode);
        Assert.Equal(["exists probe"], outside.Calls);
        Assert.Equal("10.0.26200.9000", StoreDataMarker.TryRead(places.MarkerPath)!.WindowsBuild);
    }

    /// <summary>
    /// A write that does not reach the real folder (the probe is not seen from outside): the package's own folder, the
    /// reason recorded for this build so later starts do not try again, and a new build tries again.
    /// </summary>
    [Fact]
    public void Establish_ProofFails_FallsBackAndRemembers()
    {
        using var temp = new TempDirectory();
        var places = StoreDataPlaces.For(temp.Path, Family);

        var first = StoreDataFolder.Establish(places, new FakeOutside { ProbesInvisible = true }, Build, Now);
        Assert.Equal(StoreDataMode.Package, first.Mode);
        Assert.Equal(places.PackageDirectory, first.Directory);
        var marker = StoreDataMarker.TryRead(places.MarkerPath)!;
        Assert.Equal(StoreDataMode.Package, marker.Mode);
        Assert.Equal(Build, marker.WindowsBuild);
        Assert.Contains("did not reach the real folder", marker.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.GetFiles(places.SharedDirectory), file => Path.GetFileName(file).StartsWith(StoreDataFolder.ProbePrefix, StringComparison.Ordinal));

        var again = new FakeOutside();
        Assert.Equal(StoreDataMode.Package, StoreDataFolder.Establish(places, again, Build, Now.AddHours(1)).Mode);
        Assert.Empty(again.Calls);

        Assert.Equal(StoreDataMode.Shared, StoreDataFolder.Establish(places, new FakeOutside(), "10.0.26200.9000", Now.AddDays(30)).Mode);
    }

    /// <summary>A shared folder that cannot be made: the package's own folder, recorded.</summary>
    [Fact]
    public void Establish_FolderCannotBeMade_FallsBack()
    {
        using var temp = new TempDirectory();
        var places = StoreDataPlaces.For(temp.Path, Family);

        var decision = StoreDataFolder.Establish(places, new FakeOutside { CreateFails = true }, Build, Now);

        Assert.Equal(StoreDataMode.Package, decision.Mode);
        Assert.False(Directory.Exists(places.SharedDirectory));
        Assert.Equal(StoreDataMode.Package, StoreDataMarker.TryRead(places.MarkerPath)!.Mode);
    }

    /// <summary>
    /// A private copy with no real folder: moved to the real path by the outside program, so its files become the shared
    /// folder's.
    /// </summary>
    [Fact]
    public void Establish_PrivateCopyWithoutRealFolder_BecomesTheRealOne()
    {
        using var temp = new TempDirectory();
        var places = StoreDataPlaces.For(temp.Path, Family);
        Directory.CreateDirectory(Path.Combine(places.RedirectDirectory, "logs"));
        File.WriteAllText(Path.Combine(places.RedirectDirectory, "logs", "a.log"), "BC-TEST");

        var decision = StoreDataFolder.Establish(places, new FakeOutside(), Build, Now);

        Assert.Equal(StoreDataMode.Shared, decision.Mode);
        Assert.False(Directory.Exists(places.RedirectDirectory));
        Assert.Equal("BC-TEST", File.ReadAllText(Path.Combine(places.SharedDirectory, "logs", "a.log")));
    }

    /// <summary>
    /// A private copy beside the real folder: set aside inside the real folder under a dated name, every file kept, the
    /// real folder's own files untouched.
    /// </summary>
    [Fact]
    public void Establish_PrivateCopyBesideTheRealFolder_IsSetAsideInsideIt()
    {
        using var temp = new TempDirectory();
        var places = StoreDataPlaces.For(temp.Path, Family);
        Directory.CreateDirectory(places.RedirectDirectory);
        File.WriteAllText(Path.Combine(places.RedirectDirectory, "settings.json"), "{\"private\":1}");
        Directory.CreateDirectory(places.SharedDirectory);
        File.WriteAllText(Path.Combine(places.SharedDirectory, "settings.json"), "{\"real\":1}");

        var decision = StoreDataFolder.Establish(places, new FakeOutside(), Build, Now);

        Assert.Equal(StoreDataMode.Shared, decision.Mode);
        Assert.False(Directory.Exists(places.RedirectDirectory));
        Assert.Equal("{\"real\":1}", File.ReadAllText(Path.Combine(places.SharedDirectory, "settings.json")));
        string aside = Path.Combine(places.SharedDirectory, StoreDataFolder.RecoveredPrefix + "20261114-093000");
        Assert.Equal("{\"private\":1}", File.ReadAllText(Path.Combine(aside, "settings.json")));
    }

    /// <summary>A private copy that cannot be moved: the package's own folder for this start only, nothing recorded.</summary>
    [Fact]
    public void Establish_PrivateCopyThatCannotMove_UsesTheOwnFolderForNow()
    {
        using var temp = new TempDirectory();
        var places = StoreDataPlaces.For(temp.Path, Family);
        Directory.CreateDirectory(places.RedirectDirectory);

        var decision = StoreDataFolder.Establish(places, new FakeOutside { MovesFail = true }, Build, Now);

        Assert.Equal(StoreDataMode.Package, decision.Mode);
        Assert.True(Directory.Exists(places.RedirectDirectory));
        Assert.Null(StoreDataMarker.TryRead(places.MarkerPath));
    }

    /// <summary>
    /// Version 1.2.11's history in the package's own folder, and no history in the shared one: the history and the settings
    /// move in, one rename each, and the shared folder is used.
    /// </summary>
    [Fact]
    public void Establish_MovesAnEarlierHistoryIn()
    {
        using var temp = new TempDirectory();
        var places = StoreDataPlaces.For(temp.Path, Family);
        string store = WriteHistory(places.PackageDirectory, "BC-TEST own history");
        File.WriteAllText(Path.Combine(places.PackageDirectory, "settings.json"), "{\"own\":1}");
        Directory.CreateDirectory(Path.Combine(places.PackageDirectory, "logs"));

        var decision = StoreDataFolder.Establish(places, new FakeOutside(), Build, Now);

        Assert.Equal(StoreDataMode.Shared, decision.Mode);
        Assert.Equal("BC-TEST own history", File.ReadAllText(Path.Combine(places.SharedDirectory, "stores", store, "history.db")));
        Assert.Equal("{\"own\":1}", File.ReadAllText(Path.Combine(places.SharedDirectory, "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(places.PackageDirectory, "stores")));
        Assert.False(File.Exists(Path.Combine(places.PackageDirectory, "settings.json")));
        Assert.True(Directory.Exists(Path.Combine(places.PackageDirectory, "logs")));
        Assert.Contains(decision.Notes, note => note.Contains("Moved this edition's history", StringComparison.Ordinal));
    }

    /// <summary>
    /// The shared folder holds settings but no history (an installer's options before a first start): they are set aside
    /// under a dated name, and the moved history's own settings take their place.
    /// </summary>
    [Fact]
    public void Establish_EarlierHistory_SetsTheSharedSettingsAside()
    {
        using var temp = new TempDirectory();
        var places = StoreDataPlaces.For(temp.Path, Family);
        WriteHistory(places.PackageDirectory, "BC-TEST own history");
        File.WriteAllText(Path.Combine(places.PackageDirectory, "settings.json"), "{\"own\":1}");
        Directory.CreateDirectory(Path.Combine(places.SharedDirectory, "stores"));
        File.WriteAllText(Path.Combine(places.SharedDirectory, "settings.json"), "{\"installer\":1}");

        var decision = StoreDataFolder.Establish(places, new FakeOutside(), Build, Now);

        Assert.Equal(StoreDataMode.Shared, decision.Mode);
        Assert.Equal("{\"own\":1}", File.ReadAllText(Path.Combine(places.SharedDirectory, "settings.json")));
        Assert.Equal("{\"installer\":1}", File.ReadAllText(Path.Combine(places.SharedDirectory, "settings-set-aside-20261114-093000.json")));
    }

    /// <summary>
    /// Both folders hold a history: nothing moves, nothing is set aside, the package's own folder stays in use until
    /// Windows changes (recorded, so bclip agrees), and a later start on that build does nothing.
    /// </summary>
    [Fact]
    public void Establish_TwoHistories_StayApart()
    {
        using var temp = new TempDirectory();
        var places = StoreDataPlaces.For(temp.Path, Family);
        string own = WriteHistory(places.PackageDirectory, "BC-TEST own history");
        string shared = WriteHistory(places.SharedDirectory, "BC-TEST shared history", Guid.NewGuid());

        var decision = StoreDataFolder.Establish(places, new FakeOutside(), Build, Now);

        Assert.Equal(StoreDataMode.Package, decision.Mode);
        Assert.Equal("BC-TEST own history", File.ReadAllText(Path.Combine(places.PackageDirectory, "stores", own, "history.db")));
        Assert.Equal("BC-TEST shared history", File.ReadAllText(Path.Combine(places.SharedDirectory, "stores", shared, "history.db")));
        Assert.Equal(StoreDataMode.Package, StoreDataMarker.TryRead(places.MarkerPath)!.Mode);
        Assert.Equal(places.PackageDirectory, StoreDataFolder.ResolveReadOnly(places));

        var again = new FakeOutside();
        Assert.Equal(StoreDataMode.Package, StoreDataFolder.Establish(places, again, Build, Now.AddHours(1)).Mode);
        Assert.Empty(again.Calls);
    }

    /// <summary>
    /// The package's own folder holds only settings while the shared folder has its own: both stay where they are, and the
    /// shared folder is used.
    /// </summary>
    [Fact]
    public void Establish_OwnSettingsOnly_LeavesBoth()
    {
        using var temp = new TempDirectory();
        var places = StoreDataPlaces.For(temp.Path, Family);
        Directory.CreateDirectory(places.PackageDirectory);
        File.WriteAllText(Path.Combine(places.PackageDirectory, "settings.json"), "{\"own\":1}");
        Directory.CreateDirectory(places.SharedDirectory);
        File.WriteAllText(Path.Combine(places.SharedDirectory, "settings.json"), "{\"shared\":1}");

        var decision = StoreDataFolder.Establish(places, new FakeOutside(), Build, Now);

        Assert.Equal(StoreDataMode.Shared, decision.Mode);
        Assert.Equal("{\"shared\":1}", File.ReadAllText(Path.Combine(places.SharedDirectory, "settings.json")));
        Assert.Equal("{\"own\":1}", File.ReadAllText(Path.Combine(places.PackageDirectory, "settings.json")));
    }

    /// <summary>
    /// An unfinished move of version 1.2.11 whose original (the shared folder) still exists: the half copy is undone,
    /// the original is untouched, and the shared folder is used.
    /// </summary>
    [Fact]
    public void Establish_UnfinishedMove_IsUndoneWhileItsOriginalExists()
    {
        using var temp = new TempDirectory();
        var places = StoreDataPlaces.For(temp.Path, Family);
        string original = WriteHistory(places.SharedDirectory, "BC-TEST original");
        WriteHistory(places.PackageDirectory, "BC-TEST half copy", Guid.Parse(original));
        File.WriteAllText(Path.Combine(places.PackageDirectory, EditionDataMigrator.PendingMarkerName), "pending");

        var decision = StoreDataFolder.Establish(places, new FakeOutside(), Build, Now);

        Assert.Equal(StoreDataMode.Shared, decision.Mode);
        Assert.False(Directory.Exists(Path.Combine(places.PackageDirectory, "stores")));
        Assert.False(EditionDataMigrator.HasPendingMove(places.PackageDirectory));
        Assert.Equal("BC-TEST original", File.ReadAllText(Path.Combine(places.SharedDirectory, "stores", original, "history.db")));
    }

    /// <summary>
    /// An unfinished move whose original is gone (the shared folder did not exist): the copy is all there is, so it is
    /// kept and moved into the newly made shared folder.
    /// </summary>
    [Fact]
    public void Establish_UnfinishedMove_IsKeptWhenItsOriginalIsGone()
    {
        using var temp = new TempDirectory();
        var places = StoreDataPlaces.For(temp.Path, Family);
        string store = WriteHistory(places.PackageDirectory, "BC-TEST only copy");
        File.WriteAllText(Path.Combine(places.PackageDirectory, EditionDataMigrator.PendingMarkerName), "pending");

        var decision = StoreDataFolder.Establish(places, new FakeOutside(), Build, Now);

        Assert.Equal(StoreDataMode.Shared, decision.Mode);
        Assert.Equal("BC-TEST only copy", File.ReadAllText(Path.Combine(places.SharedDirectory, "stores", store, "history.db")));
        Assert.False(EditionDataMigrator.HasPendingMove(places.PackageDirectory));
    }

    /// <summary>
    /// The earlier history cannot be moved (a file in use): the package's own folder for this start, with the history
    /// still in it, and nothing recorded, so the next start tries again.
    /// </summary>
    [Fact]
    public void Establish_HistoryThatCannotMove_StaysForNow()
    {
        using var temp = new TempDirectory();
        var places = StoreDataPlaces.For(temp.Path, Family);
        string store = WriteHistory(places.PackageDirectory, "BC-TEST own history");

        var decision = StoreDataFolder.Establish(places, new FakeOutside { MovesFail = true }, Build, Now);

        Assert.Equal(StoreDataMode.Package, decision.Mode);
        Assert.True(File.Exists(Path.Combine(places.PackageDirectory, "stores", store, "history.db")));
        Assert.Equal(StoreDataMode.Shared, StoreDataMarker.TryRead(places.MarkerPath)!.Mode);

        Assert.Equal(StoreDataMode.Shared, StoreDataFolder.Establish(places, new FakeOutside(), Build, Now.AddMinutes(1)).Mode);
        Assert.True(File.Exists(Path.Combine(places.SharedDirectory, "stores", store, "history.db")));
    }

    /// <summary>A history is anything in <c>stores</c> or a v0.1 database; an empty <c>stores</c> folder is none.</summary>
    [Fact]
    public void HasHistory_CountsAStoreOrAnOldDatabase()
    {
        using var temp = new TempDirectory();

        Assert.False(StoreDataFolder.HasHistory(temp.Path));
        Directory.CreateDirectory(Path.Combine(temp.Path, "stores"));
        Assert.False(StoreDataFolder.HasHistory(temp.Path));
        File.WriteAllText(Path.Combine(temp.Path, "history.db"), "BC-TEST");
        Assert.True(StoreDataFolder.HasHistory(temp.Path));
        File.Delete(Path.Combine(temp.Path, "history.db"));
        WriteHistory(temp.Path, "BC-TEST");
        Assert.True(StoreDataFolder.HasHistory(temp.Path));
    }

    /// <summary>
    /// The app's established folder is the Store edition's home (so it is the regular instance, without a scope), and a
    /// copy whose data folder is the default one knows it shares it.
    /// </summary>
    [Fact]
    public void ResolveDefault_TakesTheEstablishedFolder()
    {
        using var temp = new TempDirectory();
        var paths = AppPaths.ResolveDefault(Family, temp.Path);

        Assert.Equal(Path.GetFullPath(temp.Path), paths.HomeDirectory);
        Assert.True(new AppPaths(AppPaths.DefaultDataDirectory).UsesSharedDataDirectory);
        Assert.True(new AppPaths(AppPaths.DefaultDataDirectory + Path.DirectorySeparatorChar, AppPaths.DefaultDataDirectory).UsesSharedDataDirectory);
        Assert.False(new AppPaths(temp.Path).UsesSharedDataDirectory);
        Assert.False(new AppPaths(AppPaths.PackagedDataDirectory(Family), AppPaths.PackagedDataDirectory(Family)).UsesSharedDataDirectory);
    }

    /// <summary>Writes a history: <c>stores\{id}\history.db</c> with a text in it.</summary>
    /// <param name="dataDirectory">The data folder.</param>
    /// <param name="content">The database file's text.</param>
    /// <param name="id">The store id; a new one when <see langword="null"/>.</param>
    /// <returns>The store folder's name.</returns>
    private static string WriteHistory(string dataDirectory, string content, Guid? id = null)
    {
        string name = (id ?? Guid.NewGuid()).ToString("D");
        string store = Path.Combine(dataDirectory, "stores", name);
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(store, "history.db"), content);
        return name;
    }

    /// <summary>
    /// Plays the program outside the package: real file operations (the test process is not packaged, so its own view is
    /// real too), a log of the calls, and switches to hide the probe or to fail.
    /// </summary>
    private sealed class FakeOutside : IOutsideFileSystem
    {
        /// <summary>The calls, as <c>verb name</c> (the probe's name is shortened to <c>probe</c>).</summary>
        public List<string> Calls { get; } = [];

        /// <summary>The probe is never seen: a write that Windows redirected.</summary>
        public bool ProbesInvisible { get; init; }

        /// <summary><see cref="CreateDirectory"/> fails.</summary>
        public bool CreateFails { get; init; }

        /// <summary>Every <see cref="Move"/> fails.</summary>
        public bool MovesFail { get; init; }

        /// <inheritdoc/>
        public bool Exists(string path)
        {
            bool probe = Path.GetFileName(path).StartsWith(StoreDataFolder.ProbePrefix, StringComparison.Ordinal);
            Calls.Add("exists " + (probe ? "probe" : Path.GetFileName(path)));
            return !(probe && ProbesInvisible) && (File.Exists(path) || Directory.Exists(path));
        }

        /// <inheritdoc/>
        public bool CreateDirectory(string path)
        {
            Calls.Add("mkdir " + Path.GetFileName(path));
            if (CreateFails)
            {
                return false;
            }

            Directory.CreateDirectory(path);
            return true;
        }

        /// <inheritdoc/>
        public bool Move(string source, string target)
        {
            Calls.Add("move " + Path.GetFileName(source));
            if (MovesFail || File.Exists(target) || Directory.Exists(target))
            {
                return false;
            }

            if (Directory.Exists(source))
            {
                Directory.Move(source, target);
            }
            else
            {
                File.Move(source, target);
            }

            return true;
        }
    }
}
