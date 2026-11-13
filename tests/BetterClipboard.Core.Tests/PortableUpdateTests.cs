using BetterClipboard.Core.Startup;
using BetterClipboard.Core.Updates;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for a portable copy's own update (<see cref="PortableUpdate"/>): which offers it installs in its folder, the
/// result file its helper writes for the next start, and when a newer copy takes over from an older one that runs
/// (<see cref="InstanceTakeover"/>), CLAUDE.md §2.29.
/// </summary>
public sealed class PortableUpdateTests
{
    /// <summary>Offers and folders: version, whether the folder can be written, and whether the update may happen in place.</summary>
    public static TheoryData<string, bool, bool> AcceptsCases => new()
    {
        // The first release with the helper, and later ones: in place.
        { "0.2.11", true, true },
        { "0.2.12", true, true },
        { "1.0.0", true, true },

        // Older releases do not know the helper's command line (their exe would start as a plain launch).
        { "0.2.10", true, false },
        { "0.2.9", true, false },

        // A development build of 0.2.11 comes before 0.2.11 itself.
        { "0.2.11-dev.4.gabc1234", true, false },

        // A folder this user may not write: never, whatever the release.
        { "0.2.12", false, false },
    };

    /// <summary>Only a release that applies itself, into a folder this user may write, is installed in place.</summary>
    /// <param name="offered">The offered version.</param>
    /// <param name="writable">Whether the folder can be written.</param>
    /// <param name="expected">Whether the update may happen in place.</param>
    [Theory]
    [MemberData(nameof(AcceptsCases))]
    public void Accepts_OnlyAReleaseThatAppliesItself(string offered, bool writable, bool expected) =>
        Assert.Equal(expected, PortableUpdate.Accepts(UpdateTestData.Version(offered), writable));

    /// <summary>The staging folder is named for its version, under the prefix the next start cleans up.</summary>
    [Fact]
    public void StagingFolder_NamedForItsVersion()
    {
        var name = PortableUpdate.StagingFolderName(UpdateTestData.Version("0.2.12"));
        Assert.Equal("portable-0.2.12", name);
        Assert.StartsWith(PortableUpdate.StagingFolderPrefix, name, StringComparison.Ordinal);
    }

    /// <summary>
    /// The result file the helper writes reads back exactly as <c>install.ps1</c>'s does: success, a failure with its
    /// reason (in any script), and the time; a missing folder is an error the helper reports in its log.
    /// </summary>
    [Fact]
    public void ResultFile_RoundTrips()
    {
        using var temp = TestData.NewTempDirectory();
        var path = Path.Combine(temp.Path, "result.json");
        var finished = new DateTimeOffset(2026, 11, 13, 10, 20, 30, TimeSpan.Zero);

        new UpdateResult("0.2.12", true, string.Empty, finished).Write(path);
        Assert.Equal(new UpdateResult("0.2.12", true, string.Empty, finished), UpdateResult.TryRead(path));
        Assert.False(File.Exists(path + ".tmp"));

        var reason = UpdateMessages.Format(UpdateMessages.PortableFileNotReplaced, @"Microsoft.UI.Xaml.dll", "הקובץ בשימוש \"x\"");
        new UpdateResult("0.2.12", false, reason, finished.ToOffset(TimeSpan.FromHours(2))).Write(path);
        var read = UpdateResult.TryRead(path)!;
        Assert.False(read.Succeeded);
        Assert.Equal(reason, read.Error);
        Assert.Equal(finished, read.FinishedAt);

        new UpdateResult("0.2.12", false, "no time", null).Write(path);
        Assert.Null(UpdateResult.TryRead(path)!.FinishedAt);

        Assert.ThrowsAny<IOException>(() => new UpdateResult("0.2.12", true, string.Empty, null).Write(Path.Combine(temp.Path, "missing", "result.json")));
    }

    /// <summary>A newer copy takes over only when every other copy in the session is an older, readable, unpackaged copy elsewhere.</summary>
    [Fact]
    public void Takeover_OnlyFromOlderCopiesElsewhere()
    {
        var own = UpdateTestData.Version("0.2.11");
        const string Here = @"C:\Users\me\Downloads\BetterClipboard-0.2.11-win-x64\BetterClipboard.exe";
        const string There = @"C:\Users\me\Downloads\BetterClipboard-0.2.10-win-x64\BetterClipboard.exe";
        RunningCopy Older(int id = 10, string? path = There) => new(id, path, UpdateTestData.Version("0.2.10"), Packaged: false);

        Assert.True(InstanceTakeover.ShouldTakeOver(own, Here, [Older()]));
        Assert.True(InstanceTakeover.ShouldTakeOver(own, Here, [Older(10), Older(11, @"D:\Tools\BetterClipboard\BetterClipboard.exe")]));
        Assert.True(InstanceTakeover.ShouldTakeOver(UpdateTestData.Version("0.2.11-dev.2.gabc1234"), Here, [Older()]));

        // Nobody else runs (the lock's holder ended a moment ago): nothing to take over.
        Assert.False(InstanceTakeover.ShouldTakeOver(own, Here, []));

        // The same or a newer version, one that cannot be read, a path that cannot be read, or the same program.
        Assert.False(InstanceTakeover.ShouldTakeOver(own, Here, [Older() with { Version = own }]));
        Assert.False(InstanceTakeover.ShouldTakeOver(own, Here, [Older() with { Version = UpdateTestData.Version("0.2.12") }]));
        Assert.False(InstanceTakeover.ShouldTakeOver(own, Here, [Older() with { Version = null }]));
        Assert.False(InstanceTakeover.ShouldTakeOver(own, Here, [Older(path: null)]));
        Assert.False(InstanceTakeover.ShouldTakeOver(own, Here, [Older(path: "  ")]));
        Assert.False(InstanceTakeover.ShouldTakeOver(own, Here, [Older(path: Here.ToUpperInvariant())]));

        // The Microsoft Store edition has its own rules (CLAUDE.md §2.47).
        Assert.False(InstanceTakeover.ShouldTakeOver(own, Here, [Older() with { Packaged = true }]));

        // One unclear copy among older ones keeps the hand-over.
        Assert.False(InstanceTakeover.ShouldTakeOver(own, Here, [Older(10), Older(11) with { Version = null }]));

        Assert.Throws<ArgumentNullException>(() => InstanceTakeover.ShouldTakeOver(own, Here, null!));
        Assert.Throws<ArgumentException>(() => InstanceTakeover.ShouldTakeOver(own, " ", [Older()]));
    }
}
