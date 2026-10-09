using System.Globalization;
using System.Text.Json;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Settings;

namespace BetterClipboard.Core.Updates;

/// <summary>
/// What an <see cref="UpdateService"/> needs to know about the copy it runs in.
/// </summary>
public sealed class UpdateServiceOptions
{
    /// <summary>The running app's version.</summary>
    public required SemanticVersion CurrentVersion { get; init; }

    /// <summary>This PC's architecture, <c>x64</c> or <c>arm64</c>: which package an update downloads.</summary>
    public required string Architecture { get; init; }

    /// <summary>How this copy was installed (<see cref="InstalledCopy.Classify"/>).</summary>
    public required UpdateInstallKind InstallKind { get; init; }

    /// <summary>The folder of the running copy, which an installed update replaces.</summary>
    public required string InstallDirectory { get; init; }

    /// <summary>The updater's working folder (<see cref="AppPaths.UpdatesDirectory"/>): release-list cache, downloads, the installer's result.</summary>
    public required string WorkDirectory { get; init; }

    /// <summary>Where the installer writes what it did (a file in the log folder).</summary>
    public required string InstallerLogPath { get; init; }

    /// <summary>Reads the current settings (<see cref="AppSettings.CheckForUpdates"/>, <see cref="AppSettings.SkippedUpdateVersion"/>).</summary>
    public required Func<AppSettings> Settings { get; init; }

    /// <summary>
    /// Whether this copy may ask for updates by itself at all. <see langword="false"/> for a Chocolatey copy (its package
    /// manager decides when it updates) and for an isolated test instance without a stand-in feed (tests must not use up
    /// the user's request allowance at GitHub). The user's own "Check now" always works.
    /// </summary>
    public required bool AllowAutomaticChecks { get; init; }

    /// <summary>Starts the installer for an approved update; <see langword="null"/> when this copy cannot install updates.</summary>
    public IUpdateInstaller? Installer { get; init; }

    /// <summary>The clock and timers (replaced in tests).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// The update system's core: checks the release feed once a day, tells whether a newer release exists, and — only after
/// the user approves — downloads its package, verifies it and hands it to the installer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is installed without approval.</b> A check only reads the public release list. <see cref="InstallAsync"/>
/// is the approval; it downloads the package for this architecture, compares its SHA-256 with the release's
/// <c>SHA256SUMS.txt</c> and with the host's own digest, and only then starts the installer, which closes the app,
/// swaps the files and starts the new version (<see cref="IUpdateInstaller"/>).
/// </para>
/// <para>
/// <b>Schedule.</b> While <see cref="UpdateSnapshot.AutomaticChecks"/> holds: a first check one minute after
/// <see cref="Start"/> if the last one is older than <see cref="CheckInterval"/>, then one a day. A failed check is tried
/// again after an hour, doubling up to a day, and never before a rate limit ends. The release list and its entity tag are
/// cached in the working folder, so an unchanged list costs a "not modified" answer, and after a restart the update
/// button shows what is known at once.
/// </para>
/// <para>
/// <b>Threads.</b> Every member may be called from any thread. <see cref="Changed"/> is raised on the thread that made
/// the change (a pool thread for checks and downloads); subscribers marshal to their own. State lives in one immutable
/// <see cref="UpdateSnapshot"/> that is swapped as a whole.
/// </para>
/// </remarks>
public sealed class UpdateService : IAsyncDisposable
{
    /// <summary>How long a successful check is good for before the next automatic one.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// How long after <see cref="Start"/> the first automatic check may run: after the sign-in rush and the app's own
    /// startup imports.
    /// </summary>
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(60);

    /// <summary>How often the schedule looks whether a check is due (also what catches up after sleep).</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    /// <summary>The longest the app waits for a started installer before it calls the installation failed.</summary>
    private static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(15);

    /// <summary>The largest <c>SHA256SUMS.txt</c> accepted, in bytes (the real one is under 1 KB).</summary>
    private const int MaxChecksumListBytes = 256 * 1024;

    /// <summary>File in the working folder holding the last release list as received.</summary>
    private const string CacheBodyFile = "releases.json";

    /// <summary>File in the working folder holding that list's entity tag and when it was last confirmed.</summary>
    private const string CacheStateFile = "releases-state.json";

    /// <summary>File in the working folder the installer writes its outcome to.</summary>
    private const string ResultFile = "result.json";

    private readonly Lock gate = new();
    private readonly UpdateClient client;
    private readonly UpdateServiceOptions options;
    private readonly CancellationTokenSource lifetime = new();

    /// <summary>The current state; replaced as a whole under <see cref="gate"/>, read without it.</summary>
    private UpdateSnapshot current;

    /// <summary>The last release list (from the feed or the cache); what <see cref="current"/>'s offer and changelog derive from.</summary>
    private IReadOnlyList<ReleaseInfo> releases = [];

    /// <summary>The entity tag of <see cref="releases"/>, sent with the next request; <see langword="null"/> without a usable cache.</summary>
    private string? etag;

    /// <summary>The check in flight, shared by everyone who asks meanwhile; <see langword="null"/> when none runs.</summary>
    private Task? runningCheck;

    /// <summary>The installation in flight; <see langword="null"/> when none runs.</summary>
    private Task? runningInstall;

    /// <summary>Cancels the download of the installation in flight.</summary>
    private CancellationTokenSource? installCancel;

    private ITimer? timer;

    /// <summary>Checks that failed in a row, for the retry delay.</summary>
    private int failedChecks;

    /// <summary>The earliest moment the schedule may check again.</summary>
    private DateTimeOffset nextAutomaticCheck;

    private bool started;
    private bool disposed;

    /// <summary>
    /// Creates the service; nothing is read or requested until <see cref="Start"/> or <see cref="CheckAsync"/>.
    /// </summary>
    /// <param name="client">The network client. The service disposes it.</param>
    /// <param name="options">What the service needs to know about this copy.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public UpdateService(UpdateClient client, UpdateServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        this.client = client;
        this.options = options;
        current = Derive(new UpdateSnapshot { CurrentVersion = options.CurrentVersion, InstallKind = options.InstallKind });
    }

    /// <summary>
    /// Raised after <see cref="Current"/> changed, on the thread that changed it (a pool thread for checks, downloads and
    /// progress — up to ten times a second while downloading).
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>The current state. Cheap to read from any thread.</summary>
    public UpdateSnapshot Current => Volatile.Read(ref current);

    /// <summary>Where the installer writes its outcome for the next start (<see cref="TakeLastResult"/>).</summary>
    public string ResultPath => Path.Combine(options.WorkDirectory, ResultFile);

    /// <summary>Where the installer writes what it did.</summary>
    public string InstallerLogPath => options.InstallerLogPath;

    /// <summary>
    /// Reads the outcome an installer run left behind and removes it, so it is reported once. Call it once at startup,
    /// before <see cref="Start"/>.
    /// </summary>
    /// <returns>The outcome, or <see langword="null"/> when no installer ran since the last call.</returns>
    public UpdateResult? TakeLastResult()
    {
        var path = ResultPath;
        var result = UpdateResult.TryRead(path);
        TryDelete(path);
        return result;
    }

    /// <summary>
    /// Loads the cached release list (so <see cref="Current"/> shows a known update at once), removes old downloads and
    /// starts the daily schedule. Requests nothing by itself before <see cref="InitialDelay"/>.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The service was disposed.</exception>
    public void Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started)
            {
                return;
            }

            started = true;
        }

        CleanWorkDirectory();
        LoadCache();
        lock (gate)
        {
            // Due at the first tick unless the cached list is still fresh.
            var now = options.Time.GetUtcNow();
            nextAutomaticCheck = current.LastChecked is { } last ? last + CheckInterval : now;
            if (!disposed)
            {
                timer = options.Time.CreateTimer(_ => OnTimer(), null, InitialDelay, PollInterval);
            }
        }
    }

    /// <summary>
    /// Asks the feed for the release list now and updates <see cref="Current"/>. Callers that arrive while a check runs
    /// get that check.
    /// </summary>
    /// <param name="userInitiated">
    /// <see langword="true"/> for "Check now": the request is unconditional, so a problem with the cache cannot hide a
    /// new release. <see langword="false"/> for the schedule.
    /// </param>
    /// <returns>
    /// A task completing when the check ended. It never fails: a failed check is in
    /// <see cref="UpdateSnapshot.CheckProblem"/>.
    /// </returns>
    public Task CheckAsync(bool userInitiated = true)
    {
        Task check;
        lock (gate)
        {
            if (disposed)
            {
                return Task.CompletedTask;
            }

            if (runningCheck is { IsCompleted: false } running)
            {
                return running;
            }

            // An installation in flight owns the state; its end leaves the offer as it was.
            if (current.IsInstalling)
            {
                return Task.CompletedTask;
            }

            SetLocked(current with { Phase = UpdatePhase.Checking });
            check = runningCheck = Task.Run(() => RunCheckAsync(userInitiated));
        }

        RaiseChanged();
        return check;
    }

    /// <summary>
    /// Checks now when the release list is older than <paramref name="maxAge"/> — what opening the update dialog does, so
    /// the changelog it shows is current. Does nothing while this copy does not ask by itself
    /// (<see cref="UpdateSnapshot.AutomaticChecks"/> off): then only "Check now" requests anything.
    /// </summary>
    /// <param name="maxAge">How old the release list may be.</param>
    /// <returns>A task completing when the list is fresh enough, or the check ended.</returns>
    public Task EnsureFreshAsync(TimeSpan maxAge)
    {
        var snapshot = Current;
        if (!snapshot.AutomaticChecks || snapshot.IsInstalling)
        {
            return Task.CompletedTask;
        }

        bool fresh = snapshot.LastChecked is { } last && options.Time.GetUtcNow() - last <= maxAge;
        return fresh ? Task.CompletedTask : CheckAsync(userInitiated: false);
    }

    /// <summary>
    /// The user approved the update: downloads the offered package, verifies it and starts the installer, which closes
    /// the app. Progress and failures show in <see cref="Current"/>.
    /// </summary>
    /// <returns>
    /// A task completing when the attempt ended inside this process: after a failure or a cancellation. After a success
    /// the app is closed by the installer first. It never fails: a failure is in <see cref="UpdateSnapshot.InstallProblem"/>.
    /// </returns>
    public Task InstallAsync()
    {
        CancellationTokenSource cancel;
        UpdateOffer offer;
        Task install;
        lock (gate)
        {
            if (disposed)
            {
                return Task.CompletedTask;
            }

            if (runningInstall is { IsCompleted: false } running)
            {
                return running;
            }

            if (current.Offer is not { } offered || !current.CanInstall || options.Installer is not { } installer || current.Phase != UpdatePhase.Idle)
            {
                return Task.CompletedTask;
            }

            offer = offered;
            installCancel?.Dispose();
            cancel = installCancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            SetLocked(current with
            {
                Phase = UpdatePhase.Downloading,
                InstallProblem = null,
                DownloadedBytes = 0,
                TotalBytes = offer.Package.Size,
            });
            install = runningInstall = Task.Run(() => RunInstallAsync(offer, installer, cancel.Token));
        }

        RaiseChanged();
        return install;
    }

    /// <summary>
    /// Cancels an approved update while it downloads or is verified; the partial download is deleted and the offer stays.
    /// Too late once the installer runs.
    /// </summary>
    public void CancelInstall()
    {
        lock (gate)
        {
            if (current.Phase is UpdatePhase.Downloading or UpdatePhase.Verifying)
            {
                installCancel?.Cancel();
            }
        }
    }

    /// <summary>
    /// Re-reads the settings: whether the offered version is skipped, and whether this copy checks by itself. Call it
    /// when the settings changed. Switching the automatic check on checks at once when one is due.
    /// </summary>
    public void ApplySettings()
    {
        bool changed;
        bool checkNow;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            // Only the two parts that follow from the settings, compared before anything is replaced: the settings
            // change for many reasons (a resized panel, a toggled tab), and none of those is news about updates.
            var before = current;
            var settings = options.Settings();
            bool automatic = options.AllowAutomaticChecks && settings.CheckForUpdates;
            bool skipped = IsSkipped(before.Offer, settings);
            changed = automatic != before.AutomaticChecks || skipped != before.IsSkipped;
            if (changed)
            {
                SetLocked(before with { AutomaticChecks = automatic, IsSkipped = skipped });
            }

            checkNow = started && automatic && !before.AutomaticChecks && options.Time.GetUtcNow() >= nextAutomaticCheck;
        }

        if (changed)
        {
            RaiseChanged();
        }

        if (checkNow)
        {
            _ = CheckAsync(userInitiated: false);
        }
    }

    /// <summary>
    /// Stops the schedule, cancels a check or download in flight and waits for it to end. A started installer is not
    /// stopped: it is what closes the app.
    /// </summary>
    /// <returns>A task completing when nothing of the service runs any more.</returns>
    public async ValueTask DisposeAsync()
    {
        Task? check;
        Task? install;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            check = runningCheck;
            install = runningInstall;
            timer?.Dispose();
            timer = null;
        }

        await lifetime.CancelAsync().ConfigureAwait(false);
        foreach (var task in new[] { check, install })
        {
            if (task is not null)
            {
                // Both swallow their own failures; the wait only keeps them from touching a disposed client.
                await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        installCancel?.Dispose();
        lifetime.Dispose();
        client.Dispose();
    }

    /// <summary>The schedule's tick: checks when one is due and this copy checks by itself.</summary>
    private void OnTimer()
    {
        bool due;
        lock (gate)
        {
            due = !disposed && current.AutomaticChecks && current.Phase == UpdatePhase.Idle && options.Time.GetUtcNow() >= nextAutomaticCheck;
        }

        if (due)
        {
            _ = CheckAsync(userInitiated: false);
        }
    }

    /// <summary>One check: request, cache, new state. Never throws.</summary>
    /// <param name="userInitiated">Whether the user asked (an unconditional request).</param>
    /// <returns>A task completing when the state is updated.</returns>
    private async Task RunCheckAsync(bool userInitiated)
    {
        UpdateProblem? problem = null;
        try
        {
            string? tag;
            lock (gate)
            {
                // The tag is only sent while the list it belongs to is in memory: "not modified" needs something to keep.
                tag = userInitiated || releases.Count == 0 ? null : etag;
            }

            var feed = await client.GetReleasesAsync(tag, lifetime.Token).ConfigureAwait(false);
            var now = options.Time.GetUtcNow();
            lock (gate)
            {
                var offeredBefore = current.Offer?.Release.Version;
                if (feed is not null)
                {
                    releases = feed.Releases;
                    etag = feed.ETag;
                }

                failedChecks = 0;
                nextAutomaticCheck = now + CheckInterval;
                var next = Derive(current with { LastChecked = now, CheckProblem = null });

                // A failed installation is about one version. Once another one is offered (or none), its message would
                // sit under the wrong offer: seen in a test, "the installer ended…" for 0.2.8 under an offer of 0.2.9.
                if (next.Offer?.Release.Version != offeredBefore)
                {
                    next = next with { InstallProblem = null };
                }

                SetLocked(next);
            }

            SaveCache(feed, now);
            var snapshot = Current;
            AppLog.Info(snapshot.Offer is { } offer
                ? $"Update check: BetterClipboard {offer.Release.Version} is available (running {snapshot.CurrentVersion})."
                : $"Update check: up to date (running {snapshot.CurrentVersion}, latest release {snapshot.Latest?.Version.ToString() ?? "unknown"}).");
        }
        catch (UpdateException ex)
        {
            problem = UpdateProblem.From(ex);

            // No connection, a busy server or a rate limit are everyday events on a laptop: information, not a warning.
            // Anything else means the feed answered something unexpected and is worth a look.
            var line = $"Update check failed ({ex.Failure}): {ex.Message}";
            if (ex.Failure is UpdateFailure.Network or UpdateFailure.RateLimited or UpdateFailure.Server)
            {
                AppLog.Info(line);
            }
            else
            {
                AppLog.Warn(line);
            }
        }
        catch (OperationCanceledException)
        {
            // The service is being disposed.
        }
        catch (Exception ex)
        {
            // A bug must not stop the schedule or reach the pool unobserved.
            AppLog.Error("Update check failed unexpectedly.", ex);
            problem = new UpdateProblem(UpdateFailure.BadResponse, "The check for updates failed unexpectedly. The log has the details.");
        }

        lock (gate)
        {
            if (problem is not null)
            {
                failedChecks++;
                var now = options.Time.GetUtcNow();

                // 1 h, 2 h, 4 h, ... up to a day; and never before a rate limit ends.
                var delay = TimeSpan.FromHours(Math.Min(CheckInterval.TotalHours, Math.Pow(2, Math.Min(failedChecks - 1, 10))));
                nextAutomaticCheck = now + delay;
                if (problem.RetryAfter is { } retry && retry + TimeSpan.FromMinutes(1) > nextAutomaticCheck)
                {
                    nextAutomaticCheck = retry + TimeSpan.FromMinutes(1);
                }
            }

            SetLocked(current with
            {
                Phase = current.Phase == UpdatePhase.Checking ? UpdatePhase.Idle : current.Phase,
                CheckProblem = problem ?? current.CheckProblem,
            });
        }

        RaiseChanged();
    }

    /// <summary>One approved installation: checksum list, download, verification, installer. Never throws.</summary>
    /// <param name="offer">The update the user approved.</param>
    /// <param name="installer">Starts the installer.</param>
    /// <param name="cancellationToken">Cancelled by <see cref="CancelInstall"/> and by disposal.</param>
    /// <returns>A task completing when the attempt ended inside this process.</returns>
    private async Task RunInstallAsync(UpdateOffer offer, IUpdateInstaller installer, CancellationToken cancellationToken)
    {
        UpdateProblem? problem = null;
        bool installerStarted = false;
        try
        {
            AppLog.Info($"Update to {offer.Release.Version} approved: downloading {offer.Package.Name} ({offer.Package.Size:N0} bytes).");

            // The expected hash first: without it nothing would be worth downloading.
            var checksums = await client.GetTextAsync(offer.Checksums, MaxChecksumListBytes, cancellationToken).ConfigureAwait(false);
            if (!Sha256Sums.TryFind(checksums, offer.Package.Name, out var expected))
            {
                throw new UpdateException(UpdateFailure.Verification, $"The release's checksum list has no entry for {offer.Package.Name}, so the update cannot be verified. Nothing was installed.");
            }

            // GitHub computes its own digest when a file is uploaded: a second witness that must agree with the list.
            if (offer.Package.Sha256 is { } digest && !string.Equals(digest, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException(UpdateFailure.Verification, $"The release's two checksums for {offer.Package.Name} disagree, so the update cannot be trusted. Nothing was installed.");
            }

            var package = Path.Combine(options.WorkDirectory, offer.Package.Name);

            // What earlier attempts at other versions left behind (a package whose installer failed, a download that was
            // cut off) is of no use to this one, and each is some 70 MB that would otherwise stay until the next start.
            RemoveOtherPackages(offer.Package.Name);
            string? actual = await TryHashExistingAsync(package, offer.Package.Size, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                var progress = new InlineProgress(bytes => ReportProgress(bytes));
                actual = await client.DownloadAsync(offer.Package, package, progress, cancellationToken).ConfigureAwait(false);
            }

            Set(s => s with { Phase = UpdatePhase.Verifying, DownloadedBytes = s.TotalBytes });
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(package);
                throw new UpdateException(UpdateFailure.Verification, "The download does not match the release's checksum, so it was deleted. Nothing was installed. Try again; if it happens again, download the release from GitHub.");
            }

            AppLog.Info("Update package verified: its SHA-256 matches the release's checksum list.");
            cancellationToken.ThrowIfCancellationRequested();

            // From here on there is no cancelling: the installer is its own process.
            Set(s => s with { Phase = UpdatePhase.Installing });
            TryDelete(ResultPath);

            // An earlier run's log must not pass for this run's: a failure below points the user at this file, and an
            // installer that never got to write one would otherwise be "explained" by the log of the last success.
            TryDelete(options.InstallerLogPath);
            var request = new UpdateInstallRequest(package, expected, offer.Release.Version, options.InstallDirectory, options.WorkDirectory, ResultPath, options.InstallerLogPath);
            AppLog.Info($"Starting the installer for {offer.Release.Version}: it closes BetterClipboard, replaces its files and starts the new version.");
            var run = await installer.StartAsync(request, cancellationToken).ConfigureAwait(false);
            installerStarted = true;

            // A successful installer closes this app before it ends, so reaching the line after this wait means it gave up
            // (or the app is exiting anyway, which cancels the wait).
            int exitCode = await run.ExitCode.WaitAsync(InstallerTimeout, options.Time, lifetime.Token).ConfigureAwait(false);
            var result = UpdateResult.TryRead(ResultPath);
            TryDelete(ResultPath);
            throw new UpdateException(UpdateFailure.Installer, DescribeInstallerFailure(result, exitCode));
        }
        catch (UpdateException ex)
        {
            problem = UpdateProblem.From(ex);
            AppLog.Warn($"Update to {offer.Release.Version} failed ({ex.Failure}): {ex.Message}");
        }
        catch (TimeoutException)
        {
            problem = new UpdateProblem(UpdateFailure.Installer, $"The installer did not finish in {InstallerTimeout.TotalMinutes:0} minutes. Details: {options.InstallerLogPath}");
            AppLog.Warn($"Update to {offer.Release.Version}: the installer did not finish in time.");
        }
        catch (OperationCanceledException)
        {
            // After the installer started, the only cancellation is the app closing — which the installer asked for.
            AppLog.Info(installerStarted
                ? $"Update to {offer.Release.Version}: BetterClipboard is closing so that the installer can replace its files."
                : $"Update to {offer.Release.Version}: cancelled before anything was installed.");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Update to {offer.Release.Version} failed unexpectedly.", ex);
            problem = new UpdateProblem(UpdateFailure.Installer, "The update failed unexpectedly. The installed version is unchanged. The log has the details.");
        }

        Set(s => s with { Phase = UpdatePhase.Idle, InstallProblem = problem, DownloadedBytes = 0 });
    }

    /// <summary>
    /// Words why an installer that ended while the app still runs did not update it, from what it left behind.
    /// </summary>
    /// <remarks>
    /// Three cases, most telling first: the installer's own report (it got as far as the swap and says what stopped
    /// it); its log (it started and wrote what it did); or neither — it ended before its first line, which is what a
    /// policy that blocks PowerShell scripts looks like, so the message says where the new version can be had instead.
    /// </remarks>
    /// <param name="result">The installer's report, or <see langword="null"/> when it wrote none.</param>
    /// <param name="exitCode">The installer process's exit code.</param>
    /// <returns>A complete sentence or two for the user.</returns>
    private string DescribeInstallerFailure(UpdateResult? result, int exitCode)
    {
        if (result is { Succeeded: false, Error.Length: > 0 })
        {
            return $"The installer could not update BetterClipboard: {result.Error}";
        }

        return File.Exists(options.InstallerLogPath)
            ? $"The installer ended without updating BetterClipboard (code {exitCode}). The installed version is unchanged. Details: {options.InstallerLogPath}"
            : $"The installer ended before it could start (code {exitCode}), so the installed version is unchanged. If this PC blocks PowerShell scripts, get the new version from the download page.";
    }

    /// <summary>
    /// The SHA-256 of a package left by an earlier attempt, when it has the stated size; lets a retry skip the download.
    /// </summary>
    /// <param name="path">The package's path in the working folder.</param>
    /// <param name="expectedSize">The size the release states (0 = unknown, then nothing is reused).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The hash, or <see langword="null"/> when there is no such file or it cannot be read.</returns>
    private static async Task<string?> TryHashExistingAsync(string path, long expectedSize, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (expectedSize <= 0 || !info.Exists || info.Length != expectedSize)
            {
                return null;
            }

            return await UpdateClient.HashFileAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Publishes download progress.</summary>
    /// <param name="bytes">Bytes received so far.</param>
    private void ReportProgress(long bytes) =>
        Set(s => s.Phase == UpdatePhase.Downloading ? s with { DownloadedBytes = bytes } : s);

    /// <summary>
    /// Fills the parts of a snapshot that follow from the release list and the settings: the newest release, the offer,
    /// the changelog, whether the offer is skipped, and whether this copy checks by itself.
    /// </summary>
    /// <param name="snapshot">The snapshot to complete.</param>
    /// <returns>The completed snapshot.</returns>
    private UpdateSnapshot Derive(UpdateSnapshot snapshot)
    {
        var settings = options.Settings();
        var offer = UpdateCatalog.FindUpdate(options.CurrentVersion, releases, options.Architecture);
        return snapshot with
        {
            Latest = UpdateCatalog.LatestStable(releases),
            Offer = offer,
            Changelog = UpdateCatalog.SelectChangelog(options.CurrentVersion, releases),
            IsSkipped = IsSkipped(offer, settings),
            AutomaticChecks = options.AllowAutomaticChecks && settings.CheckForUpdates,
        };
    }

    /// <summary>Whether the user chose to skip exactly the offered version.</summary>
    /// <param name="offer">The offer, or <see langword="null"/>.</param>
    /// <param name="settings">The settings holding the skipped version's text.</param>
    /// <returns><see langword="true"/> when the offer's version is the skipped one.</returns>
    private static bool IsSkipped(UpdateOffer? offer, AppSettings settings) =>
        offer is not null &&
        SemanticVersion.TryParse(settings.SkippedUpdateVersion, out var skipped) &&
        skipped == offer.Release.Version;

    /// <summary>Applies a change to the state and announces it.</summary>
    /// <param name="change">Returns the new snapshot from the old one (the same instance for "no change").</param>
    private void Set(Func<UpdateSnapshot, UpdateSnapshot> change)
    {
        bool changed;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            var before = current;
            SetLocked(change(before));
            changed = !ReferenceEquals(before, current);
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    /// <summary>Swaps the snapshot; the caller holds <see cref="gate"/>.</summary>
    /// <param name="next">The new snapshot.</param>
    private void SetLocked(UpdateSnapshot next) => Volatile.Write(ref current, next);

    /// <summary>Raises <see cref="Changed"/> unless the service is disposed; a subscriber's failure is logged, not propagated.</summary>
    private void RaiseChanged()
    {
        if (Volatile.Read(ref disposed))
        {
            return;
        }

        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Error("An update status subscriber failed.", ex);
        }
    }

    /// <summary>
    /// Loads the cached release list and its state. A cache that cannot be read is ignored (and replaced by the next
    /// check): the cache is a convenience, never a source of failures.
    /// </summary>
    private void LoadCache()
    {
        try
        {
            var bodyPath = Path.Combine(options.WorkDirectory, CacheBodyFile);
            var statePath = Path.Combine(options.WorkDirectory, CacheStateFile);
            if (!File.Exists(bodyPath) || !File.Exists(statePath) || new FileInfo(bodyPath).Length > UpdateClient.MaxFeedBytes)
            {
                return;
            }

            var cached = GitHubReleases.Parse(File.ReadAllBytes(bodyPath));
            using var state = JsonDocument.Parse(File.ReadAllText(statePath));
            var root = state.RootElement;
            string? tag = root.TryGetProperty("etag", out var tagValue) && tagValue.ValueKind == JsonValueKind.String ? tagValue.GetString() : null;
            DateTimeOffset? checkedAt = root.TryGetProperty("checkedUtc", out var checkedValue) && checkedValue.ValueKind == JsonValueKind.String &&
                                        DateTimeOffset.TryParse(checkedValue.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;

            // A clock that was set back must not make the list look checked in the future (it would never be due).
            var now = options.Time.GetUtcNow();
            if (checkedAt > now)
            {
                checkedAt = null;
            }

            lock (gate)
            {
                releases = cached;
                etag = string.IsNullOrWhiteSpace(tag) ? null : tag;
                SetLocked(Derive(current with { LastChecked = checkedAt }));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or JsonException or NotSupportedException)
        {
            AppLog.Info($"The cached release list could not be read and is ignored ({ex.GetType().Name}).");
        }
    }

    /// <summary>Writes the release list (when a new one arrived) and its state; a failure only costs the cache.</summary>
    /// <param name="feed">The new list, or <see langword="null"/> after "not modified" (only the time is renewed).</param>
    /// <param name="checkedAt">When the feed answered.</param>
    private void SaveCache(ReleaseFeed? feed, DateTimeOffset checkedAt)
    {
        try
        {
            Directory.CreateDirectory(options.WorkDirectory);
            if (feed is not null)
            {
                WriteAtomically(Path.Combine(options.WorkDirectory, CacheBodyFile), feed.Body);
            }

            string? tag;
            lock (gate)
            {
                tag = etag;
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("etag", tag ?? string.Empty);
                writer.WriteString("checkedUtc", checkedAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            WriteAtomically(Path.Combine(options.WorkDirectory, CacheStateFile), buffer.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AppLog.Info($"The release list could not be cached ({ex.Message}); the next check asks again.");
        }
    }

    /// <summary>Writes a file through a temporary one, so a crash never leaves half a cache.</summary>
    /// <param name="path">The file.</param>
    /// <param name="bytes">Its content.</param>
    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Removes what earlier updates left in the working folder (packages, partial downloads, installer scripts), keeping
    /// the release-list cache. Failures are ignored: a file still in use is removed at the next start.
    /// </summary>
    private void CleanWorkDirectory()
    {
        try
        {
            if (!Directory.Exists(options.WorkDirectory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(options.WorkDirectory))
            {
                var name = Path.GetFileName(file);
                if (!name.Equals(CacheBodyFile, StringComparison.OrdinalIgnoreCase) && !name.Equals(CacheStateFile, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing depends on a clean folder.
        }
    }

    /// <summary>
    /// Removes the packages and partial downloads of other versions from the working folder, keeping the one about to be
    /// used (a complete package of the offered version lets a retry skip its download) and the release-list cache.
    /// Failures are ignored: a file still in use goes at the next start.
    /// </summary>
    /// <param name="keepName">The file name of the offered package.</param>
    private void RemoveOtherPackages(string keepName)
    {
        try
        {
            if (!Directory.Exists(options.WorkDirectory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(options.WorkDirectory))
            {
                var name = Path.GetFileName(file);
                bool isPackage = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".zip.part", StringComparison.OrdinalIgnoreCase);
                bool isWanted = name.Equals(keepName, StringComparison.OrdinalIgnoreCase) || name.Equals(keepName + ".part", StringComparison.OrdinalIgnoreCase);
                if (isPackage && !isWanted)
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing depends on a tidy folder.
        }
    }

    /// <summary>Deletes a file, ignoring a failure.</summary>
    /// <param name="path">The file.</param>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Left behind; CleanWorkDirectory removes it at the next start.
        }
    }

    /// <summary>
    /// An <see cref="IProgress{T}"/> that calls its handler on the reporting thread. <see cref="Progress{T}"/> would post
    /// to a synchronization context and reorder reports, so a late "40 %" could follow "100 %".
    /// </summary>
    /// <param name="handler">Receives each report.</param>
    private sealed class InlineProgress(Action<long> handler) : IProgress<long>
    {
        /// <inheritdoc />
        public void Report(long value) => handler(value);
    }
}
