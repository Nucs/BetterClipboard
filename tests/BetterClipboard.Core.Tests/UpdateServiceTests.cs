using System.Net;
using System.Security.Cryptography;
using System.Text;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Updates;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for <see cref="UpdateService"/>: the daily schedule, the cache, the settings' switches, and the approved
/// installation from download to installer — against a stand-in network, clock and installer.
/// </summary>
public sealed class UpdateServiceTests
{
    /// <summary>When the tests' clock starts.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// One service under test with everything around it: a temp working folder, the stand-in network serving a release
    /// list and one package, a manual clock, a recording installer and settings the test can change.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TempDirectory temp = TestData.NewTempDirectory();
        private int changes;
        private int feedVersion = 1;
        private IReadOnlyList<ReleaseInfo> served = [];

        /// <summary>Creates the harness; the service is not started.</summary>
        /// <param name="current">The running version.</param>
        /// <param name="kind">How the copy was installed.</param>
        /// <param name="allowAutomaticChecks">Whether the copy may check by itself at all.</param>
        /// <param name="workDirectory">A working folder to reuse (a second service over the same cache); a new one by default.</param>
        /// <param name="time">A clock to reuse; a new one at <see cref="Start"/> by default.</param>
        public Harness(string current = "0.2.6", UpdateInstallKind kind = UpdateInstallKind.Installer, bool allowAutomaticChecks = true, string? workDirectory = null, ManualTimeProvider? time = null)
        {
            Time = time ?? new ManualTimeProvider(Start);
            WorkDirectory = workDirectory ?? Path.Combine(temp.Path, "updates");
            Handler.Respond = Route;
            Service = new UpdateService(new UpdateClient(UpdateSource.GitHub, current, Handler), new UpdateServiceOptions
            {
                CurrentVersion = UpdateTestData.Version(current),
                Architecture = "x64",
                InstallKind = kind,
                InstallDirectory = Path.Combine(temp.Path, "app"),
                WorkDirectory = WorkDirectory,
                InstallerLogPath = Path.Combine(temp.Path, "logs", "betterclipboard-update.log"),
                Settings = () => Settings,
                AllowAutomaticChecks = allowAutomaticChecks,
                Installer = Installer,
                Time = Time,
            });
            Service.Changed += (_, _) => Interlocked.Increment(ref changes);
        }

        /// <summary>The service under test.</summary>
        public UpdateService Service { get; }

        /// <summary>The stand-in network.</summary>
        public FakeHttpHandler Handler { get; } = new();

        /// <summary>The manual clock.</summary>
        public ManualTimeProvider Time { get; }

        /// <summary>The recording installer.</summary>
        public FakeInstaller Installer { get; } = new();

        /// <summary>The settings the service reads; replace to simulate a settings change, then call <c>ApplySettings</c>.</summary>
        public AppSettings Settings { get; set; } = new AppSettings().Normalize();

        /// <summary>The service's working folder.</summary>
        public string WorkDirectory { get; }

        /// <summary>The bytes the stand-in serves as the newest release's x64 package.</summary>
        public byte[] Package { get; set; } = MakePackage(60_000);

        /// <summary>When set, the stand-in serves this as <c>SHA256SUMS.txt</c> instead of the true checksums.</summary>
        public string? ChecksumsOverride { get; set; }

        /// <summary>When set, feed requests fail with this instead of being answered.</summary>
        public Func<HttpResponseMessage>? FeedFailure { get; set; }

        /// <summary>When set, the package request is answered by this instead of the package's bytes.</summary>
        public Func<CancellationToken, Task<HttpResponseMessage>>? PackageResponse { get; set; }

        /// <summary>How often <see cref="UpdateService.Changed"/> was raised.</summary>
        public int Changes => Volatile.Read(ref changes);

        /// <summary>How many feed requests were made.</summary>
        public int FeedRequests => Handler.CountOf(UpdateSource.GitHub.FeedUrl);

        /// <summary>The address of the newest served release's x64 package.</summary>
        public Uri PackageUrl => Newest.FindAsset(UpdateCatalog.PackageFileName(Newest.Version, "x64"))!.DownloadUrl;

        /// <summary>The newest served release.</summary>
        private ReleaseInfo Newest => served.OrderByDescending(r => r.Version).First();

        /// <summary>Deterministic bytes for a "package".</summary>
        /// <param name="length">How many.</param>
        /// <returns>The bytes.</returns>
        public static byte[] MakePackage(int length)
        {
            var bytes = new byte[length];
            new Random(11).NextBytes(bytes);
            return bytes;
        }

        /// <summary>
        /// Sets the release list the stand-in serves. The newest release's x64 package gets the real size and SHA-256 of
        /// <see cref="Package"/>, so an installation can be verified; a changed list gets a new entity tag.
        /// </summary>
        /// <param name="versions">The versions to list, in any order.</param>
        public void Serve(params string[] versions)
        {
            var newest = versions.Select(UpdateTestData.Version).Max();
            served = versions.Select(v =>
            {
                var release = UpdateTestData.Release(v, $"Notes of {v}");
                if (release.Version != newest)
                {
                    return release;
                }

                var name = UpdateCatalog.PackageFileName(release.Version, "x64");
                var assets = release.Assets.Select(a => a.Name == name ? a with { Size = Package.Length, Sha256 = Sha256Of(Package) } : a).ToArray();
                return release with { Assets = assets };
            }).ToArray();
            feedVersion++;
        }

        /// <summary>Waits until the service's state satisfies <paramref name="predicate"/> (at most five seconds).</summary>
        /// <param name="predicate">The state to wait for.</param>
        /// <returns>The first snapshot that satisfied it.</returns>
        /// <exception cref="TimeoutException">The state did not arrive in time.</exception>
        public async Task<UpdateSnapshot> WaitFor(Func<UpdateSnapshot, bool> predicate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                var snapshot = Service.Current;
                if (predicate(snapshot))
                {
                    return snapshot;
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"The update state did not arrive: phase {snapshot.Phase}, offer {snapshot.Offer?.Release.Version.ToString() ?? "none"}, check problem {snapshot.CheckProblem?.Failure.ToString() ?? "none"}, install problem {snapshot.InstallProblem?.Message ?? "none"}.");
                }

                await Task.Delay(5);
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            temp.Dispose();
        }

        /// <summary>The lower-case hex SHA-256 of bytes.</summary>
        /// <param name="bytes">The bytes.</param>
        /// <returns>64 hex digits.</returns>
        public static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

        /// <summary>Answers one request of the service: the feed (conditional), the checksum list or the package.</summary>
        /// <param name="request">The request.</param>
        /// <param name="cancellationToken">The request's token.</param>
        /// <returns>The answer.</returns>
        private Task<HttpResponseMessage> Route(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            if (url == UpdateSource.GitHub.FeedUrl)
            {
                if (FeedFailure is not null)
                {
                    return Task.FromResult(FeedFailure());
                }

                var tag = $"\"feed-{feedVersion}\"";
                return Task.FromResult(request.Headers.TryGetValues("If-None-Match", out var sent) && sent.Contains(tag)
                    ? FakeHttpHandler.Status(HttpStatusCode.NotModified)
                    : FakeHttpHandler.Json(UpdateTestData.ToJson([.. served]), tag));
            }

            if (served.Count > 0 && url == PackageUrl)
            {
                return PackageResponse is not null ? PackageResponse(cancellationToken) : Task.FromResult(FakeHttpHandler.Bytes(Package));
            }

            if (url.AbsolutePath.EndsWith("/" + UpdateCatalog.ChecksumsFileName, StringComparison.Ordinal))
            {
                var text = ChecksumsOverride ?? $"{Sha256Of(Package)}  {UpdateCatalog.PackageFileName(Newest.Version, "x64")}\n";
                return Task.FromResult(FakeHttpHandler.Bytes(Encoding.ASCII.GetBytes(text)));
            }

            return Task.FromResult(FakeHttpHandler.Status(HttpStatusCode.NotFound));
        }
    }

    /// <summary>Before the service starts and right after, nothing is requested and nothing is known.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Start_RequestsNothingByItself()
    {
        await using var harness = new Harness();
        harness.Serve("0.2.7", "0.2.6");

        var initial = harness.Service.Current;
        Assert.Equal("0.2.6", initial.CurrentVersion.ToString());
        Assert.Equal(UpdatePhase.Idle, initial.Phase);
        Assert.True(initial.AutomaticChecks);
        Assert.False(initial.IsUpdateAvailable);
        Assert.Null(initial.LastChecked);

        harness.Service.Start();
        harness.Time.Advance(UpdateService.InitialDelay - TimeSpan.FromSeconds(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(harness.Handler.Requests);
    }

    /// <summary>
    /// One minute after the start the first check runs by itself and finds the update; the button's state follows, and
    /// the next automatic check is a day later.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Schedule_FirstCheckAfterAMinute_ThenDaily()
    {
        await using var harness = new Harness();
        harness.Serve("0.2.7", "0.2.6", "0.2.5");
        harness.Service.Start();

        harness.Time.Advance(UpdateService.InitialDelay);
        var found = await harness.WaitFor(s => s.IsUpdateAvailable && s.Phase == UpdatePhase.Idle);

        Assert.Equal("0.2.7", found.Offer!.Release.Version.ToString());
        Assert.Equal("0.2.7", found.Latest!.Version.ToString());
        Assert.True(found.WantsAttention);
        Assert.True(found.CanInstall);
        Assert.Equal(ChangelogKind.NewerReleases, found.Changelog.Kind);
        Assert.Equal("Notes of 0.2.7", Assert.Single(found.Changelog.Releases).Notes);
        Assert.Equal(harness.Time.GetUtcNow(), found.LastChecked);
        Assert.Null(found.CheckProblem);
        Assert.Equal(1, harness.FeedRequests);
        Assert.True(harness.Changes >= 2);

        // Hourly ticks for 23 hours ask nothing; the tick after a full day does (conditionally: nothing changed).
        harness.Time.Advance(TimeSpan.FromHours(23));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, harness.FeedRequests);

        harness.Time.Advance(TimeSpan.FromHours(2));
        await harness.WaitFor(s => s.LastChecked > found.LastChecked && s.Phase == UpdatePhase.Idle);
        Assert.Equal(2, harness.FeedRequests);
        Assert.Equal("\"feed-2\"", harness.Handler.Requests[^1].IfNoneMatch);
        Assert.True(harness.Service.Current.IsUpdateAvailable);
    }

    /// <summary>
    /// The release list is cached: a restarted app shows the known update at once, without a request, and its next
    /// check is conditional. A list that is still fresh is not asked for again at startup.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Cache_ShowsTheKnownUpdateAfterARestart()
    {
        using var temp = TestData.NewTempDirectory();
        var work = Path.Combine(temp.Path, "updates");
        var time = new ManualTimeProvider(Start);
        await using (var first = new Harness(workDirectory: work, time: time))
        {
            first.Serve("0.2.7", "0.2.6");
            await first.Service.CheckAsync();
            Assert.True(first.Service.Current.IsUpdateAvailable);
        }

        time.Advance(TimeSpan.FromHours(2));
        await using var second = new Harness(workDirectory: work, time: time);
        second.Serve("0.2.7", "0.2.6");
        Assert.False(second.Service.Current.IsUpdateAvailable);

        second.Service.Start();
        var restored = second.Service.Current;
        Assert.Equal("0.2.7", restored.Offer?.Release.Version.ToString());
        Assert.Equal(Start, restored.LastChecked);
        Assert.Empty(second.Handler.Requests);

        // Checked two hours ago: the first tick is not due.
        time.Advance(UpdateService.InitialDelay);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(second.Handler.Requests);
    }

    /// <summary>A cache that cannot be read is ignored, and the next check replaces it.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Cache_ABrokenOneIsIgnored()
    {
        await using var harness = new Harness();
        Directory.CreateDirectory(harness.WorkDirectory);
        await File.WriteAllTextAsync(Path.Combine(harness.WorkDirectory, "releases.json"), "{ not json", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(harness.WorkDirectory, "releases-state.json"), "also broken", TestContext.Current.CancellationToken);
        harness.Serve("0.2.7");

        harness.Service.Start();
        Assert.False(harness.Service.Current.IsUpdateAvailable);
        Assert.Null(harness.Service.Current.LastChecked);

        await harness.Service.CheckAsync();
        Assert.True(harness.Service.Current.IsUpdateAvailable);
        Assert.StartsWith("[", await File.ReadAllTextAsync(Path.Combine(harness.WorkDirectory, "releases.json"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    /// <summary>
    /// Starting removes what earlier updates left in the working folder (packages, partial downloads, scripts) and keeps
    /// the cache.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Start_CleansOldDownloads_ButKeepsTheCache()
    {
        await using var harness = new Harness();
        harness.Serve("0.2.7");
        await harness.Service.CheckAsync();
        foreach (var leftover in new[] { "BetterClipboard-0.2.7-win-x64.zip", "BetterClipboard-0.2.7-win-x64.zip.part", "install-0.2.7.ps1", "result.json" })
        {
            await File.WriteAllTextAsync(Path.Combine(harness.WorkDirectory, leftover), "old", TestContext.Current.CancellationToken);
        }

        harness.Service.Start();

        Assert.Equal(["releases-state.json", "releases.json"], Directory.GetFiles(harness.WorkDirectory).Select(Path.GetFileName).Order());
    }

    /// <summary>With the setting off, the copy asks nothing by itself — not on the schedule, not when the dialog opens — until the user asks.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task AutomaticChecksOff_OnlyTheUserAsks()
    {
        await using var harness = new Harness();
        harness.Settings = harness.Settings with { CheckForUpdates = false };
        harness.Service.ApplySettings();
        harness.Serve("0.2.7");
        harness.Service.Start();
        Assert.False(harness.Service.Current.AutomaticChecks);

        harness.Time.Advance(TimeSpan.FromDays(3));
        await harness.Service.EnsureFreshAsync(TimeSpan.FromMinutes(5));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(harness.Handler.Requests);

        await harness.Service.CheckAsync(userInitiated: true);
        Assert.True(harness.Service.Current.IsUpdateAvailable);
        Assert.Equal(1, harness.FeedRequests);
    }

    /// <summary>A copy that must not check by itself (Chocolatey, an isolated test instance) never does, whatever the setting says.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task CopiesThatMustNotCheck_NeverDoByThemselves()
    {
        await using var harness = new Harness(kind: UpdateInstallKind.Chocolatey, allowAutomaticChecks: false);
        harness.Serve("0.2.7");
        harness.Service.Start();
        Assert.False(harness.Service.Current.AutomaticChecks);

        harness.Time.Advance(TimeSpan.FromDays(2));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(harness.Handler.Requests);

        await harness.Service.CheckAsync();
        var snapshot = harness.Service.Current;
        Assert.True(snapshot.IsUpdateAvailable);
        Assert.False(snapshot.CanInstall);
        await harness.Service.InstallAsync();
        Assert.Null(harness.Installer.Request);
    }

    /// <summary>Switching the automatic check on checks at once when one is due.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task SwitchingTheCheckOn_ChecksWhenDue()
    {
        await using var harness = new Harness();
        harness.Settings = harness.Settings with { CheckForUpdates = false };
        harness.Service.ApplySettings();
        harness.Serve("0.2.7");
        harness.Service.Start();

        harness.Settings = harness.Settings with { CheckForUpdates = true };
        harness.Service.ApplySettings();

        await harness.WaitFor(s => s.IsUpdateAvailable && s.Phase == UpdatePhase.Idle);
        Assert.True(harness.Service.Current.AutomaticChecks);
    }

    /// <summary>
    /// A failed check keeps what was known and says why; it is retried after an hour, then two, and a success clears the
    /// problem.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task FailedChecks_KeepTheOffer_AndBackOff()
    {
        await using var harness = new Harness();
        harness.Serve("0.2.7");
        harness.Service.Start();
        await harness.Service.CheckAsync();
        Assert.True(harness.Service.Current.IsUpdateAvailable);

        // The clock only ever stops on a schedule tick (one minute past each hour), so "now" is the same whether a
        // check's outcome is processed before or after the test looks.
        harness.FeedFailure = () => FakeHttpHandler.Status(HttpStatusCode.BadGateway);
        await harness.Service.CheckAsync(userInitiated: false);
        var failed = harness.Service.Current;
        Assert.Equal(UpdateFailure.Server, failed.CheckProblem!.Failure);
        Assert.True(failed.IsUpdateAvailable);
        Assert.Equal(Start, failed.LastChecked);
        Assert.Equal(2, harness.FeedRequests);

        // First failure at 08:00: not retried by the 08:01 tick, retried by the 09:01 one.
        harness.Time.Advance(UpdateService.InitialDelay);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(2, harness.FeedRequests);
        harness.Time.Advance(UpdateService.PollInterval);
        await harness.WaitFor(_ => harness.FeedRequests == 3);
        await harness.WaitFor(s => s.Phase == UpdatePhase.Idle);

        // Second failure at 09:01: the wait doubles, so the 10:01 tick asks nothing and the 11:01 one does.
        harness.Time.Advance(UpdateService.PollInterval);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(3, harness.FeedRequests);

        harness.FeedFailure = null;
        harness.Time.Advance(UpdateService.PollInterval);
        var recovered = await harness.WaitFor(s => s.CheckProblem is null && s.Phase == UpdatePhase.Idle);
        Assert.Equal(4, harness.FeedRequests);
        Assert.True(recovered.LastChecked > Start);
    }

    /// <summary>A rate limit is not retried before it ends.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task RateLimit_IsNotRetriedBeforeItEnds()
    {
        await using var harness = new Harness();
        harness.Serve("0.2.7");
        var reset = Start.AddHours(5);
        harness.FeedFailure = () =>
        {
            var response = FakeHttpHandler.Status(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return response;
        };
        harness.Service.Start();
        harness.Time.Advance(UpdateService.InitialDelay);
        var limited = await harness.WaitFor(s => s.CheckProblem is not null && s.Phase == UpdatePhase.Idle);
        Assert.Equal(UpdateFailure.RateLimited, limited.CheckProblem!.Failure);
        Assert.Equal(reset, limited.CheckProblem.RetryAfter);

        harness.Time.Advance(TimeSpan.FromHours(4));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, harness.FeedRequests);

        harness.FeedFailure = null;
        harness.Time.Advance(TimeSpan.FromHours(2));
        await harness.WaitFor(s => s.IsUpdateAvailable && s.Phase == UpdatePhase.Idle);
        Assert.Equal(2, harness.FeedRequests);
    }

    /// <summary>Checks asked for at the same time are one request.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task ConcurrentChecks_ShareOneRequest()
    {
        await using var harness = new Harness();
        harness.Serve("0.2.7");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = harness.Handler.Respond;
        harness.Handler.Respond = async (request, token) =>
        {
            await release.Task.WaitAsync(token);
            return await inner(request, token);
        };

        var first = harness.Service.CheckAsync();
        var second = harness.Service.CheckAsync();
        Assert.Equal(UpdatePhase.Checking, harness.Service.Current.Phase);
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, harness.FeedRequests);
        Assert.Equal(UpdatePhase.Idle, harness.Service.Current.Phase);
    }

    /// <summary>Opening the dialog refreshes a stale list and leaves a fresh one alone.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task EnsureFresh_AsksOnlyWhenStale()
    {
        await using var harness = new Harness();
        harness.Serve("0.2.7");
        await harness.Service.EnsureFreshAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(1, harness.FeedRequests);

        harness.Time.Advance(TimeSpan.FromMinutes(4));
        await harness.Service.EnsureFreshAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(1, harness.FeedRequests);

        harness.Time.Advance(TimeSpan.FromMinutes(2));
        await harness.Service.EnsureFreshAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(2, harness.FeedRequests);
    }

    /// <summary>
    /// A skipped version is still offered but not highlighted; a newer release ends the skip; and a settings change that
    /// has nothing to do with updates is no update news.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task SkippedVersion_IsOfferedWithoutHighlight()
    {
        await using var harness = new Harness();
        harness.Serve("0.2.7");
        await harness.Service.CheckAsync();
        Assert.True(harness.Service.Current.WantsAttention);

        harness.Settings = harness.Settings with { SkippedUpdateVersion = "v0.2.7" };
        harness.Service.ApplySettings();
        var skipped = harness.Service.Current;
        Assert.True(skipped.IsUpdateAvailable);
        Assert.True(skipped.IsSkipped);
        Assert.False(skipped.WantsAttention);

        int changes = harness.Changes;
        harness.Settings = harness.Settings with { FlyoutWidth = 500, IsCapturePaused = true };
        harness.Service.ApplySettings();
        Assert.Equal(changes, harness.Changes);
        Assert.Same(skipped, harness.Service.Current);

        harness.Serve("0.2.8", "0.2.7");
        await harness.Service.CheckAsync();
        Assert.Equal("0.2.8", harness.Service.Current.Offer?.Release.Version.ToString());
        Assert.False(harness.Service.Current.IsSkipped);
        Assert.True(harness.Service.Current.WantsAttention);
    }

    /// <summary>
    /// The approved update: the package is downloaded and verified, the installer gets exactly that file with its
    /// checksum, and while it runs the state says "installing". When the installer gives up, its reason is shown.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Install_DownloadsVerifiesAndStartsTheInstaller()
    {
        await using var harness = new Harness();
        harness.Serve("0.2.7", "0.2.6");
        await harness.Service.CheckAsync();

        var install = harness.Service.InstallAsync();
        var request = await harness.Installer.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var installing = await harness.WaitFor(s => s.Phase == UpdatePhase.Installing);

        Assert.True(installing.IsInstalling);
        Assert.True(installing.WantsAttention);
        Assert.Equal("0.2.7", request.Version.ToString());
        Assert.Equal(Harness.Sha256Of(harness.Package), request.PackageSha256);
        Assert.Equal(Path.Combine(harness.WorkDirectory, "BetterClipboard-0.2.7-win-x64.zip"), request.PackagePath);
        Assert.Equal(harness.Package, harness.Installer.PackageBytes);
        Assert.Equal(harness.WorkDirectory, request.WorkDirectory);
        Assert.Equal(harness.Service.ResultPath, request.ResultPath);
        Assert.EndsWith("betterclipboard-update.log", request.LogPath, StringComparison.Ordinal);
        Assert.False(install.IsCompleted);

        // The installer ends while the app still runs: it gave up, and said why.
        harness.Installer.End(1, "{\"version\":\"0.2.7\",\"ok\":false,\"error\":\"The install folder stayed in use by another program.\"}");
        await install;

        var failed = harness.Service.Current;
        Assert.Equal(UpdatePhase.Idle, failed.Phase);
        Assert.Equal(UpdateFailure.Installer, failed.InstallProblem!.Failure);
        Assert.Contains("The install folder stayed in use by another program.", failed.InstallProblem.Message, StringComparison.Ordinal);
        Assert.True(failed.IsUpdateAvailable);
        Assert.False(File.Exists(harness.Service.ResultPath));
    }

    /// <summary>
    /// An installer that ends without a result but wrote its log is reported with its exit code and that log to look at.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Install_AnInstallerThatEndsSilently_IsAFailure()
    {
        await using var harness = new Harness();
        harness.Serve("0.2.7");
        await harness.Service.CheckAsync();
        var install = harness.Service.InstallAsync();
        var request = await harness.Installer.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The installer started and logged what it did before it gave up.
        Directory.CreateDirectory(Path.GetDirectoryName(request.LogPath)!);
        File.WriteAllText(request.LogPath, "BC-TEST installer log");
        harness.Installer.End(5);
        await install;

        var problem = harness.Service.Current.InstallProblem!;
        Assert.Equal(UpdateFailure.Installer, problem.Failure);
        Assert.Contains("code 5", problem.Message, StringComparison.Ordinal);
        Assert.Contains("unchanged", problem.Message, StringComparison.Ordinal);
        Assert.Contains("betterclipboard-update.log", problem.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An installer that ends without a result and without a log never ran its first line (a policy that blocks scripts
    /// looks like this): the message does not point at a log that is not there — and not at the log of an earlier run,
    /// which is removed before the installer starts — and says where the new version can be had instead.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Install_AnInstallerThatNeverStarted_IsNotExplainedByAnOlderLog()
    {
        await using var harness = new Harness();
        harness.Serve("0.2.7");
        await harness.Service.CheckAsync();

        // The log a successful update left behind last month.
        var staleLog = harness.Service.InstallerLogPath;
        Directory.CreateDirectory(Path.GetDirectoryName(staleLog)!);
        File.WriteAllText(staleLog, "BC-TEST log of an earlier update");

        var install = harness.Service.InstallAsync();
        await harness.Installer.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(File.Exists(staleLog));

        harness.Installer.End(1);
        await install;

        var problem = harness.Service.Current.InstallProblem!;
        Assert.Equal(UpdateFailure.Installer, problem.Failure);
        Assert.Contains("code 1", problem.Message, StringComparison.Ordinal);
        Assert.Contains("unchanged", problem.Message, StringComparison.Ordinal);
        Assert.Contains("download page", problem.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("betterclipboard-update.log", problem.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failed installation is about one version: a check that confirms the same offer keeps its message (the user may be
    /// reading it), and a check that finds another version drops it, so it never sits under the wrong offer.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Check_KeepsAnInstallProblemOnlyForTheSameOffer()
    {
        await using var harness = new Harness();
        harness.Serve("0.2.7");
        await harness.Service.CheckAsync();
        var install = harness.Service.InstallAsync();
        await harness.Installer.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        harness.Installer.End(1);
        await install;
        Assert.NotNull(harness.Service.Current.InstallProblem);

        await harness.Service.CheckAsync();
        Assert.Equal("0.2.7", harness.Service.Current.Offer!.Release.Version.ToString());
        Assert.NotNull(harness.Service.Current.InstallProblem);

        harness.Serve("0.2.8", "0.2.7");
        await harness.Service.CheckAsync();
        Assert.Equal("0.2.8", harness.Service.Current.Offer!.Release.Version.ToString());
        Assert.Null(harness.Service.Current.InstallProblem);
    }

    /// <summary>
    /// Packages and partial downloads of other versions are removed when an installation starts (each is tens of megabytes
    /// that would otherwise wait for the next start); the release-list cache and other files stay.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Install_RemovesPackagesOfOtherVersions()
    {
        await using var harness = new Harness();
        harness.Serve("0.2.7");
        await harness.Service.CheckAsync();
        var older = Path.Combine(harness.WorkDirectory, "BetterClipboard-0.2.6-win-x64.zip");
        var cutOff = Path.Combine(harness.WorkDirectory, "BetterClipboard-0.2.5-win-x64.zip.part");
        var other = Path.Combine(harness.WorkDirectory, "notes.txt");
        foreach (var file in new[] { older, cutOff, other })
        {
            File.WriteAllText(file, "BC-TEST leftover");
        }

        var install = harness.Service.InstallAsync();
        await harness.Installer.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(File.Exists(older));
        Assert.False(File.Exists(cutOff));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(Path.Combine(harness.WorkDirectory, "releases.json")));
        Assert.True(File.Exists(Path.Combine(harness.WorkDirectory, "BetterClipboard-0.2.7-win-x64.zip")));

        harness.Installer.End(1);
        await install;
    }

    /// <summary>Download progress is published while the package arrives.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Install_PublishesProgress()
    {
        await using var harness = new Harness();
        harness.Package = Harness.MakePackage(400_000);
        harness.Serve("0.2.7");
        await harness.Service.CheckAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.PackageResponse = async token =>
        {
            await gate.Task.WaitAsync(token);
            return FakeHttpHandler.Bytes(harness.Package);
        };

        var install = harness.Service.InstallAsync();
        var downloading = await harness.WaitFor(s => s.Phase == UpdatePhase.Downloading);
        Assert.Equal(400_000, downloading.TotalBytes);
        Assert.Equal(0, downloading.DownloadedBytes);
        Assert.Equal(0, downloading.DownloadFraction);

        gate.SetResult();
        await harness.Installer.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        harness.Installer.End(1);
        await install;
    }

    /// <summary>
    /// A download whose SHA-256 is not the release's is deleted and never reaches the installer — whether the bytes were
    /// changed, the checksum list has no entry, or the two checksums of the release disagree.
    /// </summary>
    /// <param name="scenario">What is wrong.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("tampered-package")]
    [InlineData("no-entry")]
    [InlineData("digests-disagree")]
    public async Task Install_RefusesWhatCannotBeVerified(string scenario)
    {
        await using var harness = new Harness();
        harness.Serve("0.2.7");
        await harness.Service.CheckAsync();
        var packagePath = Path.Combine(harness.WorkDirectory, "BetterClipboard-0.2.7-win-x64.zip");
        switch (scenario)
        {
            case "tampered-package":
                // Same size, other bytes: only the hash can tell.
                var tampered = (byte[])harness.Package.Clone();
                tampered[100] ^= 0xFF;
                harness.PackageResponse = _ => Task.FromResult(FakeHttpHandler.Bytes(tampered));

                // The list and GitHub's digest both state the true hash.
                break;
            case "no-entry":
                harness.ChecksumsOverride = $"{UpdateTestData.Hash('c')}  BetterClipboard-0.2.7-win-arm64.zip\n";
                break;
            default:
                harness.ChecksumsOverride = $"{UpdateTestData.Hash('d')}  BetterClipboard-0.2.7-win-x64.zip\n";
                break;
        }

        await harness.Service.InstallAsync();

        var failed = harness.Service.Current;
        Assert.Equal(UpdatePhase.Idle, failed.Phase);
        Assert.Equal(UpdateFailure.Verification, failed.InstallProblem!.Failure);
        Assert.Contains("Nothing was installed", failed.InstallProblem.Message, StringComparison.Ordinal);
        Assert.Null(harness.Installer.Request);
        Assert.False(File.Exists(packagePath));
        Assert.Equal(scenario == "tampered-package" ? 1 : 0, harness.Handler.CountOf(harness.PackageUrl));
    }

    /// <summary>A verified package left by an earlier attempt is used again instead of downloaded again.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Install_ReusesAVerifiedDownload()
    {
        await using var harness = new Harness();
        harness.Serve("0.2.7");
        await harness.Service.CheckAsync();
        Directory.CreateDirectory(harness.WorkDirectory);
        await File.WriteAllBytesAsync(Path.Combine(harness.WorkDirectory, "BetterClipboard-0.2.7-win-x64.zip"), harness.Package, TestContext.Current.CancellationToken);

        var install = harness.Service.InstallAsync();
        await harness.Installer.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        harness.Installer.End(1);
        await install;

        Assert.Equal(0, harness.Handler.CountOf(harness.PackageUrl));
        Assert.Equal(harness.Package, harness.Installer.PackageBytes);
    }

    /// <summary>Cancelling during the download ends the attempt without a problem; the offer stays and can be approved again.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Install_CanBeCancelledWhileDownloading()
    {
        await using var harness = new Harness();
        harness.Serve("0.2.7");
        await harness.Service.CheckAsync();
        harness.PackageResponse = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ScriptedContent(harness.Package[..5000], ScriptedContent.Ending.Hang) });

        var install = harness.Service.InstallAsync();
        await harness.WaitFor(s => s.Phase == UpdatePhase.Downloading && File.Exists(Path.Combine(harness.WorkDirectory, "BetterClipboard-0.2.7-win-x64.zip.part")));
        harness.Service.CancelInstall();
        await install;

        var cancelled = harness.Service.Current;
        Assert.Equal(UpdatePhase.Idle, cancelled.Phase);
        Assert.Null(cancelled.InstallProblem);
        Assert.True(cancelled.IsUpdateAvailable);
        Assert.Null(harness.Installer.Request);
        Assert.Empty(Directory.GetFiles(harness.WorkDirectory, "*.part"));

        harness.PackageResponse = null;
        var again = harness.Service.InstallAsync();
        await harness.Installer.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        harness.Installer.End(1);
        await again;
    }

    /// <summary>An installer that cannot start is a failure with its own message, and nothing is left "installing".</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Install_AnInstallerThatCannotStart_IsReported()
    {
        await using var harness = new Harness();
        harness.Serve("0.2.7");
        await harness.Service.CheckAsync();
        harness.Installer.StartFailure = new UpdateException(UpdateFailure.Installer, "The update package holds no installer.");

        await harness.Service.InstallAsync();

        var failed = harness.Service.Current;
        Assert.Equal(UpdatePhase.Idle, failed.Phase);
        Assert.Equal("The update package holds no installer.", failed.InstallProblem!.Message);
    }

    /// <summary>Without an offer, or for a copy that cannot replace itself, approving does nothing.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Install_NeedsAnOfferAndAnInstallerCopy()
    {
        await using var upToDate = new Harness(current: "0.2.7");
        upToDate.Serve("0.2.7");
        await upToDate.Service.CheckAsync();
        await upToDate.Service.InstallAsync();
        Assert.Null(upToDate.Installer.Request);
        Assert.Equal(ChangelogKind.CurrentRelease, upToDate.Service.Current.Changelog.Kind);

        await using var portable = new Harness(kind: UpdateInstallKind.Portable);
        portable.Serve("0.2.7");
        await portable.Service.CheckAsync();
        Assert.True(portable.Service.Current.IsUpdateAvailable);
        Assert.False(portable.Service.Current.CanInstall);
        await portable.Service.InstallAsync();
        Assert.Null(portable.Installer.Request);
        Assert.Equal(0, portable.Handler.CountOf(portable.PackageUrl));
    }

    /// <summary>
    /// When the app closes while the installer runs — which is how a successful update goes — the service stops quietly:
    /// no failure is recorded and no further change is announced.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Dispose_WhileTheInstallerRuns_IsQuiet()
    {
        var harness = new Harness();
        harness.Serve("0.2.7");
        await harness.Service.CheckAsync();
        var install = harness.Service.InstallAsync();
        await harness.Installer.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await harness.WaitFor(s => s.Phase == UpdatePhase.Installing);
        int changes = harness.Changes;

        await harness.DisposeAsync();

        Assert.True(install.IsCompleted);
        Assert.Equal(changes, harness.Changes);
        Assert.Null(harness.Service.Current.InstallProblem);
        await harness.Service.CheckAsync();
        await harness.Service.InstallAsync();
    }

    /// <summary>The installer's result from before a restart is read once and then gone.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task TakeLastResult_ReportsOnce()
    {
        await using var harness = new Harness();
        Assert.Null(harness.Service.TakeLastResult());

        Directory.CreateDirectory(harness.WorkDirectory);
        await File.WriteAllTextAsync(harness.Service.ResultPath, "{\"version\":\"0.2.7\",\"ok\":true,\"error\":\"\"}", TestContext.Current.CancellationToken);
        var result = harness.Service.TakeLastResult();
        Assert.True(result?.Succeeded);
        Assert.Equal("0.2.7", result!.Version);
        Assert.Null(harness.Service.TakeLastResult());
    }
}
