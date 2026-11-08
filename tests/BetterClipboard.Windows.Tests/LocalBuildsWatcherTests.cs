using BetterClipboard.Windows.Updates;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests for <see cref="LocalBuildsWatcher"/> on temp folders: the dev-builds folder appearing, a build published into
/// it, the folder removed and created again — each reported, so the update button lights up without waiting an hour.
/// </summary>
/// <remarks>
/// Every wait is on a condition with a deadline (CLAUDE.md §4): nothing here asserts that a report did not come.
/// </remarks>
public sealed class LocalBuildsWatcherTests : IDisposable
{
    /// <summary>A short quiet time, so the tests do not wait half a second per report.</summary>
    private static readonly TimeSpan QuietTime = TimeSpan.FromMilliseconds(100);

    /// <summary>The stand-in data directory; the dev-builds folder lives inside it.</summary>
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", "dev-builds-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the stand-in data directory (without a dev-builds folder).</summary>
    public LocalBuildsWatcherTests() => Directory.CreateDirectory(dataDirectory);

    /// <summary>The dev-builds folder under test.</summary>
    private string DevBuilds => Path.Combine(dataDirectory, "dev-builds");

    /// <summary>
    /// Before the first publish there is no folder: the watcher waits for it, reports its creation, then watches it and
    /// reports a build written into a subfolder of it.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task ReportsTheFolderAppearing_ThenBuildsInIt()
    {
        using var watcher = new LocalBuildsWatcher(DevBuilds, QuietTime);
        int reports = 0;
        watcher.Changed += (_, _) => Interlocked.Increment(ref reports);
        watcher.Start();
        Assert.False(watcher.IsWatchingFolder);

        Directory.CreateDirectory(DevBuilds);
        await RunHistoryIntegrationTests.WaitUntilAsync(() => Volatile.Read(ref reports) >= 1 && watcher.IsWatchingFolder);

        int before = Volatile.Read(ref reports);
        var build = Directory.CreateDirectory(Path.Combine(DevBuilds, ".staging-1"));
        await File.WriteAllTextAsync(Path.Combine(build.FullName, "build.json"), "{\"version\":\"0.2.10-dev.1.gabc\"}", TestContext.Current.CancellationToken);
        Directory.Move(build.FullName, Path.Combine(DevBuilds, "0.2.10-dev.1.gabc"));
        await RunHistoryIntegrationTests.WaitUntilAsync(() => Volatile.Read(ref reports) > before);
    }

    /// <summary>
    /// A folder deleted while it is watched is reported, and one created again is watched again: a build published into
    /// the new folder is reported too.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task SurvivesTheFolderBeingDeletedAndCreatedAgain()
    {
        Directory.CreateDirectory(Path.Combine(DevBuilds, "0.2.10-dev.1.gabc"));
        using var watcher = new LocalBuildsWatcher(DevBuilds, QuietTime);
        int reports = 0;
        watcher.Changed += (_, _) => Interlocked.Increment(ref reports);
        watcher.Start();
        Assert.True(watcher.IsWatchingFolder);

        Directory.Delete(DevBuilds, recursive: true);
        await RunHistoryIntegrationTests.WaitUntilAsync(() => Volatile.Read(ref reports) >= 1);

        // Created again (the next publish): watched again, and what is written into it is reported.
        await RunHistoryIntegrationTests.WaitUntilAsync(() => TryCreate(DevBuilds));
        await RunHistoryIntegrationTests.WaitUntilAsync(() => watcher.IsWatchingFolder);
        int before = Volatile.Read(ref reports);
        await File.WriteAllTextAsync(Path.Combine(DevBuilds, "marker.txt"), "BC-TEST", TestContext.Current.CancellationToken);
        await RunHistoryIntegrationTests.WaitUntilAsync(() => Volatile.Read(ref reports) > before);
    }

    /// <summary>Starting twice is harmless, disposing twice too, and a disposed watcher cannot start again.</summary>
    [Fact]
    public void Lifecycle_IsForgiving()
    {
        var watcher = new LocalBuildsWatcher(DevBuilds);
        watcher.Start();
        watcher.Start();
        watcher.Dispose();
        watcher.Dispose();
        Assert.Throws<ObjectDisposedException>(watcher.Start);
        Assert.Throws<ArgumentException>(() => new LocalBuildsWatcher(" "));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a lingering watch handle only leaves a temp folder behind.
        }
    }

    /// <summary>Creates a folder, tolerating the moment a deleted one with the same name is still being removed.</summary>
    /// <param name="path">The folder.</param>
    /// <returns><see langword="true"/> once the folder exists.</returns>
    private static bool TryCreate(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still "delete pending" while a handle to the old folder closes: try again shortly.
            return false;
        }
    }
}
