using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace BetterClipboard.Core.Updates;

/// <summary>
/// A release list as the feed returned it.
/// </summary>
/// <param name="Releases">The parsed releases (see <see cref="GitHubReleases.Parse"/>).</param>
/// <param name="ETag">
/// The answer's entity tag, or <see langword="null"/>. Sent back as <c>If-None-Match</c> with the next request: an
/// unchanged list then costs no transfer and does not count against GitHub's hourly limit.
/// </param>
/// <param name="Body">The answer's bytes, kept so the list can be read again after a "not modified" answer.</param>
public sealed record ReleaseFeed(IReadOnlyList<ReleaseInfo> Releases, string? ETag, byte[] Body);

/// <summary>
/// The updater's only network code: asks the release feed for the release list, reads a small text file of a release
/// (<c>SHA256SUMS.txt</c>) and downloads a package while computing its SHA-256.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a request carries:</b> the address, a <c>User-Agent</c> naming the app and its version (GitHub's API refuses
/// requests without one), and for the list an <c>If-None-Match</c> tag. No cookie, no account, no token, nothing about
/// the user, the PC or the clipboard. The system proxy is used, with the Windows account's credentials for the proxy
/// only, as browsers do.
/// </para>
/// <para>
/// <b>Bounds:</b> every answer has a size cap and a time limit, redirects are followed at most five times and never from
/// https to http, and a package is refused when its size is not the one the release list states.
/// </para>
/// <para>
/// Failures surface as <see cref="UpdateException"/> with a sentence for the user; cancellation as
/// <see cref="OperationCanceledException"/>. Thread-safe: one instance serves concurrent requests.
/// </para>
/// </remarks>
public sealed class UpdateClient : IDisposable
{
    /// <summary>The largest release list accepted, in bytes (GitHub's answer for 30 releases of this project is under 0.2 MB).</summary>
    public const int MaxFeedBytes = 8 * 1024 * 1024;

    /// <summary>The largest package accepted, in bytes (a release zip is about 75 MB).</summary>
    public const long MaxPackageBytes = 1024L * 1024 * 1024;

    /// <summary>How long a small request (the list, the checksum file) may take in all.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient http;

    /// <summary>
    /// Creates a client for <paramref name="source"/>.
    /// </summary>
    /// <param name="source">Where releases come from, and which download addresses are trusted.</param>
    /// <param name="appVersion">The running app's version, sent in the <c>User-Agent</c> (<c>BetterClipboard/0.2.6</c>).</param>
    /// <param name="handler">
    /// A message handler to send requests through instead of the network (tests). The client disposes it.
    /// <see langword="null"/> uses the system's network stack and proxy.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public UpdateClient(UpdateSource source, string appVersion, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        http = new HttpClient(handler ?? CreateNetworkHandler(), disposeHandler: true)
        {
            // Each request sets its own limit: the package download has none in total, only a stall limit.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // Without validation: a version such as 0.2.6-dev.52539eb+sha is a fine product token for servers, and a
        // strict parser must never keep the app from asking for updates.
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"BetterClipboard/{(string.IsNullOrWhiteSpace(appVersion) ? "0.0.0" : appVersion.Trim())}");
    }

    /// <summary>The source this client talks to.</summary>
    public UpdateSource Source { get; }

    /// <summary>
    /// How long a package download may receive nothing before it is given up as stalled. Settable so tests need not wait
    /// a minute.
    /// </summary>
    internal TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Asks the feed for the release list.
    /// </summary>
    /// <param name="etag">The entity tag of the list the caller already holds, or <see langword="null"/> to ask unconditionally.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The list, or <see langword="null"/> when the feed says the caller's list (<paramref name="etag"/>) is still current.</returns>
    /// <exception cref="UpdateException">
    /// The feed could not be reached (<see cref="UpdateFailure.Network"/>), limits requests right now
    /// (<see cref="UpdateFailure.RateLimited"/>), answered with an error (<see cref="UpdateFailure.Server"/>), or sent
    /// something that is not a release list (<see cref="UpdateFailure.BadResponse"/>).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<ReleaseFeed?> GetReleasesAsync(string? etag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Source.FeedUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(etag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return null;
            }

            ThrowForStatus(response, "the release list");
            var body = await ReadBoundedAsync(response.Content, MaxFeedBytes, "The release list", timeout.Token).ConfigureAwait(false);
            IReadOnlyList<ReleaseInfo> releases;
            try
            {
                releases = GitHubReleases.Parse(body);
            }
            catch (FormatException ex)
            {
                throw new UpdateException(UpdateFailure.BadResponse, "The release list from the server could not be read.", ex);
            }

            return new ReleaseFeed(releases, response.Headers.ETag?.ToString(), body);
        }
        catch (Exception ex) when (TranslateTransportFailure(ex, cancellationToken, "the release list") is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// Reads a small text file of a release, such as <c>SHA256SUMS.txt</c>.
    /// </summary>
    /// <param name="asset">The file; its address must be a trusted download of <see cref="Source"/>.</param>
    /// <param name="maxBytes">The most bytes accepted.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The file's text (UTF-8; a checksum list is ASCII).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="asset"/> is <see langword="null"/>.</exception>
    /// <exception cref="UpdateException">The address is not trusted or the file is too large (<see cref="UpdateFailure.BadResponse"/>), or the request failed (see <see cref="GetReleasesAsync"/>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<string> GetTextAsync(ReleaseAsset asset, int maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        EnsureTrusted(asset);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            ThrowForStatus(response, asset.Name);
            var bytes = await ReadBoundedAsync(response.Content, maxBytes, asset.Name, timeout.Token).ConfigureAwait(false);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (TranslateTransportFailure(ex, cancellationToken, asset.Name) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// Downloads a package to <paramref name="targetPath"/> and returns the SHA-256 of what was written.
    /// </summary>
    /// <remarks>
    /// The bytes go to <c>&lt;targetPath&gt;.part</c> first and are moved into place only when the whole file arrived at
    /// the size the release list states, so <paramref name="targetPath"/> never holds half a download. The hash is
    /// computed while writing; the caller compares it with the release's checksum before using the file.
    /// </remarks>
    /// <param name="asset">The package; its address must be a trusted download of <see cref="Source"/>.</param>
    /// <param name="targetPath">Where the finished file goes (its folder is created; an existing file is replaced).</param>
    /// <param name="progress">Told the bytes received so far, about ten times a second and once at the end; may be <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the download; the partial file is deleted.</param>
    /// <returns>The SHA-256 of the downloaded file as 64 lower-case hex digits.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="asset"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="targetPath"/> is null or blank.</exception>
    /// <exception cref="UpdateException">
    /// The address is not trusted, or the size is not the stated one (<see cref="UpdateFailure.BadResponse"/>); the
    /// connection failed or stalled (<see cref="UpdateFailure.Network"/>); the host answered with an error; or the file
    /// could not be written (<see cref="UpdateFailure.Storage"/>).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<string> DownloadAsync(ReleaseAsset asset, string targetPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        EnsureTrusted(asset);
        if (asset.Size > MaxPackageBytes)
        {
            throw new UpdateException(UpdateFailure.BadResponse, $"{asset.Name} is larger than an update can be.");
        }

        var partial = targetPath + ".part";
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            stall.CancelAfter(StallTimeout);
            using var response = await http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            ThrowForStatus(response, asset.Name);
            if (asset.Size > 0 && response.Content.Headers.ContentLength is { } announced && announced != asset.Size)
            {
                throw new UpdateException(UpdateFailure.BadResponse, $"{asset.Name} has another size on the server than the release states.");
            }

            long limit = asset.Size > 0 ? asset.Size : MaxPackageBytes;
            long received = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            // Every file operation is translated where it happens: a broken connection is an IOException too, so the two
            // can only be told apart by which call threw.
            FileStream file;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
                file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                throw StorageFailure(ex);
            }

            await using (file)
            await using (var body = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
            {
                var buffer = new byte[1 << 16];
                long lastReport = Environment.TickCount64;
                while (true)
                {
                    // Re-armed for every read: the limit is on silence, not on the whole download.
                    stall.CancelAfter(StallTimeout);
                    int read = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    received += read;
                    if (received > limit)
                    {
                        throw new UpdateException(UpdateFailure.BadResponse, $"{asset.Name} is larger on the server than the release states.");
                    }

                    hash.AppendData(buffer.AsSpan(0, read));
                    try
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsStorageFailure(ex))
                    {
                        throw StorageFailure(ex);
                    }

                    if (progress is not null && Environment.TickCount64 - lastReport >= 100)
                    {
                        lastReport = Environment.TickCount64;
                        progress.Report(received);
                    }
                }

                try
                {
                    await file.FlushAsync(stall.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsStorageFailure(ex))
                {
                    throw StorageFailure(ex);
                }
            }

            if (asset.Size > 0 && received != asset.Size)
            {
                throw new UpdateException(UpdateFailure.Network, $"The download of {asset.Name} ended early ({received:N0} of {asset.Size:N0} bytes).");
            }

            string sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            try
            {
                File.Move(partial, targetPath, overwrite: true);
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                throw StorageFailure(ex);
            }

            progress?.Report(received);
            return sha256;
        }
        catch (Exception ex)
        {
            TryDelete(partial);
            if (TranslateTransportFailure(ex, cancellationToken, asset.Name) is { } translated)
            {
                throw translated;
            }

            throw;
        }
    }

    /// <summary>
    /// Computes the SHA-256 of a file on disk (a package downloaded earlier), without loading it into memory.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The hash as 64 lower-case hex digits.</returns>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file is not readable for this account.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Closes the connections.</summary>
    public void Dispose() => http.Dispose();

    /// <summary>The handler for real requests: system proxy, compression, bounded redirects, no cookies.</summary>
    /// <returns>The handler.</returns>
    private static SocketsHttpHandler CreateNetworkHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),

        // The app runs for weeks and asks once a day: never hold a connection (or a stale DNS answer) in between.
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),

        // A company proxy that asks for the Windows account gets it, as it does from a browser; servers never do.
        DefaultProxyCredentials = CredentialCache.DefaultCredentials,
    };

    /// <summary>Refuses a file whose address the source does not trust, before anything is requested.</summary>
    /// <param name="asset">The file.</param>
    /// <exception cref="UpdateException">The address is not a trusted download (<see cref="UpdateFailure.BadResponse"/>).</exception>
    private void EnsureTrusted(ReleaseAsset asset)
    {
        if (!Source.IsTrustedDownload(asset.DownloadUrl))
        {
            throw new UpdateException(UpdateFailure.BadResponse, $"The release list names a download address for {asset.Name} that is not one of this project's release downloads, so it was not used.");
        }
    }

    /// <summary>
    /// Turns an error status into the matching <see cref="UpdateException"/>; returns for a success status.
    /// </summary>
    /// <param name="response">The answer.</param>
    /// <param name="what">What was asked for, for the message.</param>
    /// <exception cref="UpdateException">The status is not a success.</exception>
    private static void ThrowForStatus(HttpResponseMessage response, string what)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        int status = (int)response.StatusCode;
        if (status is 403 or 429 && TryReadRateLimit(response, out var retryAfter))
        {
            var when = retryAfter is { } at ? $" Try again after {at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}." : " Try again later.";
            throw new UpdateException(UpdateFailure.RateLimited, "GitHub limits how often this network address may ask for releases." + when, retryAfter: retryAfter);
        }

        throw status >= 500
            ? new UpdateException(UpdateFailure.Server, $"The server could not answer the request for {what} right now (error {status}).")
            : new UpdateException(UpdateFailure.BadResponse, $"The server refused the request for {what} (error {status}).");
    }

    /// <summary>
    /// Whether an error answer is a rate limit, and when it ends.
    /// </summary>
    /// <param name="response">A 403 or 429 answer.</param>
    /// <param name="retryAfter">When requests are accepted again, if the answer says so.</param>
    /// <returns><see langword="true"/> when the answer carries a spent rate limit or a <c>Retry-After</c>.</returns>
    private static bool TryReadRateLimit(HttpResponseMessage response, out DateTimeOffset? retryAfter)
    {
        retryAfter = null;
        if (response.Headers.RetryAfter is { } header)
        {
            retryAfter = header.Date ?? (header.Delta is { } delta ? DateTimeOffset.UtcNow + delta : null);
            return true;
        }

        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault()?.Trim() == "0")
        {
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset) &&
                long.TryParse(reset.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds) &&
                seconds is > 0 and < 253_402_300_800)
            {
                retryAfter = DateTimeOffset.FromUnixTimeSeconds(seconds);
            }

            return true;
        }

        return false;
    }

    /// <summary>Reads a body into memory, refusing more than <paramref name="maxBytes"/>.</summary>
    /// <param name="content">The answer's content.</param>
    /// <param name="maxBytes">The cap.</param>
    /// <param name="what">What is being read, for the message.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="UpdateException">The body is larger than the cap (<see cref="UpdateFailure.BadResponse"/>).</exception>
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maxBytes, string what, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 and var length && length > maxBytes)
        {
            throw new UpdateException(UpdateFailure.BadResponse, $"{what} is larger than expected and was not read.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[1 << 14];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > maxBytes)
            {
                throw new UpdateException(UpdateFailure.BadResponse, $"{what} is larger than expected and was not read.");
            }

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }

    /// <summary>
    /// Maps what the network stack throws to an <see cref="UpdateException"/>.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <param name="callerToken">The caller's token: when it is cancelled, the cancellation is the caller's and is not translated.</param>
    /// <param name="what">What was asked for, for the message.</param>
    /// <returns>
    /// The exception to throw instead, or <see langword="null"/> to let <paramref name="exception"/> travel on as it is
    /// (an <see cref="UpdateException"/> already, the caller's own cancellation, or a bug).
    /// </returns>
    private static UpdateException? TranslateTransportFailure(Exception exception, CancellationToken callerToken, string what) => exception switch
    {
        UpdateException => null,

        // Not the caller's cancellation: the request's own time limit (or the stall limit) ran out.
        OperationCanceledException when !callerToken.IsCancellationRequested =>
            new UpdateException(UpdateFailure.Network, $"The server did not answer in time ({what}).", exception),
        OperationCanceledException => null,
        HttpRequestException or IOException =>
            new UpdateException(UpdateFailure.Network, $"The server could not be reached ({Describe(exception)}).", exception),
        _ => null,
    };

    /// <summary>The innermost message of a network failure, without a trailing period (it goes inside brackets).</summary>
    /// <param name="exception">The failure.</param>
    /// <returns>A short reason.</returns>
    private static string Describe(Exception exception)
    {
        var inner = exception;
        while (inner.InnerException is { } next)
        {
            inner = next;
        }

        return inner.Message.Trim().TrimEnd('.');
    }

    /// <summary>Whether an exception thrown by a file operation is a problem with the local file or folder.</summary>
    /// <param name="exception">The exception.</param>
    /// <returns><see langword="true"/> for disk, path and permission failures.</returns>
    private static bool IsStorageFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException;

    /// <summary>Wraps a local file problem for the dialog.</summary>
    /// <param name="exception">The file operation's exception.</param>
    /// <returns>The exception to throw.</returns>
    private static UpdateException StorageFailure(Exception exception) =>
        new(UpdateFailure.Storage, $"The update could not be saved on this PC: {exception.Message}", exception);

    /// <summary>Deletes a partial download, ignoring a failure (the next download overwrites it anyway).</summary>
    /// <param name="path">The file.</param>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind: FileMode.Create replaces it next time.
        }
    }
}
