using BetterClipboard.Core.Editions;
using BetterClipboard.Windows.Packaging;
using BetterClipboard.Windows.Shell;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// The Windows side of the Microsoft Store edition's shared data folder (CLAUDE.md §2.47): cmd.exe as the program
/// outside the package (<see cref="CmdOutsideFileSystem"/>) with folder names that would break a command line, the quiet
/// start's exit code and variables it relies on, and the whole setup run with the real cmd on a scratch Local AppData.
/// </summary>
/// <remarks>
/// The test process has no package identity, so cmd and the test see the same real file system: what is checked here is
/// that the commands do what they say for any name. What Windows redirects inside a package is measured with
/// <c>tools/probes/msix_lab</c> and verified live with a test-signed package (CLAUDE.md §5).
/// </remarks>
public sealed class StoreDataFolderSetupTests : IDisposable
{
    /// <summary>A scratch folder of this test's own.</summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", "store-data-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the scratch folder.</summary>
    public StoreDataFolderSetupTests() => Directory.CreateDirectory(root);

    /// <summary>
    /// Folder names that would end a quoted path, expand a variable, start a command or escape a character on a cmd
    /// command line; each must arrive whole, because paths reach cmd only through variables.
    /// </summary>
    public static TheoryData<string> Names => new()
    {
        "plain",
        "with space",
        "100%PATH%",
        "a&b",
        "x^y",
        "bang!",
        "(paren) [bracket]",
        "é ü ñ",
        "עברית",
        "a;b,c=d",
    };

    /// <summary>Deletes the scratch folder.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a scratch folder left in the temp folder.
        }
    }

    /// <summary>A quiet start reports the program's exit code and gives it the variables asked for, only to it.</summary>
    [Fact]
    public void QuietProcess_ReportsTheExitCodeAndPassesVariables()
    {
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using (var plain = QuietProcess.Start(cmd, "/d /c exit 7", root, noConsoleWindow: true))
        {
            Assert.True(plain.WaitForExit(TimeSpan.FromSeconds(10)));
            Assert.Equal(7, plain.ExitCode);
        }

        var variables = new Dictionary<string, string> { ["BETTERCLIPBOARD_TEST_VARIABLE"] = "BC-TEST value" };
        using (var withVariable = QuietProcess.Start(cmd, "/d /c if \"%BETTERCLIPBOARD_TEST_VARIABLE%\"==\"BC-TEST value\" (exit 3) else (exit 4)", root, noConsoleWindow: true, variables))
        {
            Assert.True(withVariable.WaitForExit(TimeSpan.FromSeconds(10)));
            Assert.Equal(3, withVariable.ExitCode);
        }

        Assert.Null(Environment.GetEnvironmentVariable("BETTERCLIPBOARD_TEST_VARIABLE"));
        Assert.ThrowsAny<ArgumentException>(() => QuietProcess.Start(cmd, "/d /c exit 0", root, noConsoleWindow: true, new Dictionary<string, string> { ["A=B"] = "x" }));
    }

    /// <summary>cmd makes, finds and moves a folder of any name, and never moves onto something that exists.</summary>
    /// <param name="name">The folder's name.</param>
    [Theory]
    [MemberData(nameof(Names))]
    public void Cmd_MakesFindsAndMoves_AnyName(string name)
    {
        var outside = new CmdOutsideFileSystem();
        string folder = Path.Combine(root, name, "inner");
        string moved = Path.Combine(root, name, "moved");

        Assert.False(outside.Exists(folder));
        Assert.True(outside.CreateDirectory(folder));
        Assert.True(Directory.Exists(folder));
        Assert.True(outside.Exists(folder));

        File.WriteAllText(Path.Combine(folder, "file.txt"), "BC-TEST");
        Assert.True(outside.Exists(Path.Combine(folder, "file.txt")));
        Assert.True(outside.Move(folder, moved));
        Assert.False(Directory.Exists(folder));
        Assert.Equal("BC-TEST", File.ReadAllText(Path.Combine(moved, "file.txt")));

        // A move onto something that exists is refused (cmd would ask, and nobody can answer), and nothing changes.
        Directory.CreateDirectory(folder);
        Assert.False(outside.Move(folder, moved));
        Assert.True(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(moved, "file.txt")));
    }

    /// <summary>
    /// The whole setup with the real cmd on a scratch Local AppData whose path has a space and a percent sign: an
    /// earlier history and its settings move into the shared folder, which is proven and recorded.
    /// </summary>
    [Fact]
    public void Establish_WithTheRealCmd_MovesAnEarlierHistoryIn()
    {
        string localAppData = Path.Combine(root, "Local App%Data%");
        var places = StoreDataPlaces.For(localAppData, "Nucs.BetterClipboard_mgez7y7xa0my4");
        string store = Path.Combine(places.PackageDirectory, "stores", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(store, "history.db"), "BC-TEST history");
        File.WriteAllText(Path.Combine(places.PackageDirectory, "settings.json"), "{\"BC-TEST\":1}");

        var decision = StoreDataFolder.Establish(places, new CmdOutsideFileSystem(), StoreDataFolderSetup.WindowsBuild(), DateTimeOffset.UtcNow);

        Assert.Equal(StoreDataMode.Shared, decision.Mode);
        Assert.Equal(places.SharedDirectory, decision.Directory);
        Assert.Equal("BC-TEST history", File.ReadAllText(Path.Combine(places.SharedDirectory, "stores", Path.GetFileName(store), "history.db")));
        Assert.Equal("{\"BC-TEST\":1}", File.ReadAllText(Path.Combine(places.SharedDirectory, "settings.json")));
        Assert.Equal(StoreDataMode.Shared, StoreDataMarker.TryRead(places.MarkerPath)!.Mode);
        Assert.DoesNotContain(Directory.GetFiles(places.SharedDirectory), file => Path.GetFileName(file).StartsWith(StoreDataFolder.ProbePrefix, StringComparison.Ordinal));
    }

    /// <summary>The Windows build has four parts, the update revision last.</summary>
    [Fact]
    public void WindowsBuild_HasFourParts()
    {
        string build = StoreDataFolderSetup.WindowsBuild();

        Assert.Equal(4, build.Split('.').Length);
        Assert.StartsWith($"{Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}.{Environment.OSVersion.Version.Build}.", build, StringComparison.Ordinal);
    }
}
