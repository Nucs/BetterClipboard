using System.Net;
using System.Security.Cryptography;
using System.Text;
using BetterClipboard.Core.Updates;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for <see cref="UpdateClient"/> against a stand-in network (<see cref="FakeHttpHandler"/>): what it sends, how it
/// reads answers, and how every kind of failure reaches the caller.
/// </summary>
public sealed class UpdateClientTests
{
    /// <summary>A package asset on GitHub's release downloads, with <paramref name="size"/> as its stated size.</summary>
    /// <param name="size">The size the release list states.</param>
    /// <param name="name">The file's name.</param>
    /// <returns>The asset.</returns>
    private static ReleaseAsset Package(long size, string name = "BetterClipboard-0.2.7-win-x64.zip") =>
        new(name, size, new Uri($"https://github.com/{UpdateSource.Repository}/releases/download/v0.2.7/{name}"), null);

    /// <summary>Deterministic bytes for a "package".</summary>
    /// <param name="length">How many.</param>
    /// <returns>The bytes.</returns>
    private static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        new Random(7).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Creates a client for GitHub over the stand-in network.</summary>
    /// <param name="handler">The stand-in.</param>
    /// <returns>The client.</returns>
    private static UpdateClient Client(FakeHttpHandler handler) => new(UpdateSource.GitHub, "0.2.6-dev.52539eb", handler);

    /// <summary>The stand-in for a PC without a connection: fails the way the network stack does when a host name does not resolve.</summary>
    /// <returns>Never returns.</returns>
    /// <exception cref="HttpRequestException">Always.</exception>
    private static Task<HttpResponseMessage> NoNetwork() => throw new HttpRequestException("No such host is known. (api.github.com:443)");

    /// <summary>Runs an action that must fail with an <see cref="UpdateException"/> and returns it.</summary>
    /// <param name="action">The failing call.</param>
    /// <returns>The exception.</returns>
    private static Task<UpdateException> Failure(Func<Task> action) => Assert.ThrowsAsync<UpdateException>(action);

    /// <summary>
    /// The release list request names the app and its version, asks for GitHub's JSON, and carries no credentials; the
    /// answer comes back parsed, with its entity tag and its bytes.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task GetReleases_SendsAnAnonymousRequest_AndReturnsTheList()
    {
        var json = UpdateTestData.ToJson(UpdateTestData.Release("0.2.7"), UpdateTestData.Release("0.2.6"));
        var handler = new FakeHttpHandler { Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(json, "W/\"abc\"")) };
        using var client = Client(handler);

        var feed = await client.GetReleasesAsync(null, TestContext.Current.CancellationToken);

        Assert.NotNull(feed);
        Assert.Equal(["0.2.7", "0.2.6"], feed.Releases.Select(r => r.Version.ToString()));
        Assert.Equal("W/\"abc\"", feed.ETag);
        Assert.Equal(json, Encoding.UTF8.GetString(feed.Body));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(UpdateSource.GitHub.FeedUrl, request.Url);
        Assert.Equal("BetterClipboard/0.2.6-dev.52539eb", request.UserAgent);
        Assert.Contains("application/vnd.github+json", request.Accept, StringComparison.Ordinal);
        Assert.Null(request.IfNoneMatch);
        Assert.False(request.HasCredentials);
    }

    /// <summary>With an entity tag the request is conditional, and "not modified" means "keep your list".</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task GetReleases_IsConditionalWithATag()
    {
        var handler = new FakeHttpHandler { Respond = (_, _) => Task.FromResult(FakeHttpHandler.Status(HttpStatusCode.NotModified)) };
        using var client = Client(handler);

        Assert.Null(await client.GetReleasesAsync("W/\"abc\"", TestContext.Current.CancellationToken));
        Assert.Equal("W/\"abc\"", Assert.Single(handler.Requests).IfNoneMatch);
    }

    /// <summary>A spent rate limit (GitHub's 403 with its headers) and a plain Retry-After are told apart from other refusals.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task GetReleases_ReportsRateLimitsWithTheirEnd()
    {
        var reset = new DateTimeOffset(2026, 10, 9, 13, 0, 0, TimeSpan.Zero);
        var handler = new FakeHttpHandler
        {
            Respond = (_, _) =>
            {
                var response = FakeHttpHandler.Status(HttpStatusCode.Forbidden);
                response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
                response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
                return Task.FromResult(response);
            },
        };
        using var client = Client(handler);
        var limited = await Failure(() => client.GetReleasesAsync(null, TestContext.Current.CancellationToken));
        Assert.Equal(UpdateFailure.RateLimited, limited.Failure);
        Assert.Equal(reset, limited.RetryAfter);

        handler.Respond = (_, _) =>
        {
            var response = FakeHttpHandler.Status(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return Task.FromResult(response);
        };
        var retry = await Failure(() => client.GetReleasesAsync(null, TestContext.Current.CancellationToken));
        Assert.Equal(UpdateFailure.RateLimited, retry.Failure);
        Assert.NotNull(retry.RetryAfter);

        // A 403 without the rate-limit headers is a refusal, not a limit.
        handler.Respond = (_, _) => Task.FromResult(FakeHttpHandler.Status(HttpStatusCode.Forbidden));
        Assert.Equal(UpdateFailure.BadResponse, (await Failure(() => client.GetReleasesAsync(null, TestContext.Current.CancellationToken))).Failure);
    }

    /// <summary>Each way a request can go wrong maps to its own kind of failure, with a sentence for the user.</summary>
    /// <param name="scenario">Which failure the stand-in produces.</param>
    /// <param name="expected">The failure kind the caller must see.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("server-error", UpdateFailure.Server)]
    [InlineData("not-found", UpdateFailure.BadResponse)]
    [InlineData("html", UpdateFailure.BadResponse)]
    [InlineData("too-large", UpdateFailure.BadResponse)]
    [InlineData("no-network", UpdateFailure.Network)]
    [InlineData("broken-body", UpdateFailure.Network)]
    public async Task GetReleases_MapsFailures(string scenario, UpdateFailure expected)
    {
        var handler = new FakeHttpHandler
        {
            Respond = (_, _) => scenario switch
            {
                "server-error" => Task.FromResult(FakeHttpHandler.Status(HttpStatusCode.BadGateway)),
                "not-found" => Task.FromResult(FakeHttpHandler.Status(HttpStatusCode.NotFound)),
                "html" => Task.FromResult(FakeHttpHandler.Json("<html>Sign in to continue</html>")),
                "too-large" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ScriptedContent(new byte[UpdateClient.MaxFeedBytes + 1], ScriptedContent.Ending.Complete) }),
                "no-network" => NoNetwork(),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ScriptedContent("[{\"tag_name\""u8.ToArray(), ScriptedContent.Ending.Fail) }),
            },
        };
        using var client = Client(handler);

        var failure = await Failure(() => client.GetReleasesAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(expected, failure.Failure);
        Assert.EndsWith(".", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The caller's own cancellation is a cancellation, not a "network failure".</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task GetReleases_CancellationStaysACancellation()
    {
        var handler = new FakeHttpHandler { Respond = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return FakeHttpHandler.Status(HttpStatusCode.OK); } };
        using var client = Client(handler);
        using var cancel = new CancellationTokenSource();
        var pending = client.GetReleasesAsync(null, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    /// <summary>The checksum list is read as text, from a trusted address only, and with a size cap.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task GetText_ReadsSmallTrustedFilesOnly()
    {
        var handler = new FakeHttpHandler { Respond = (_, _) => Task.FromResult(FakeHttpHandler.Bytes("abc  file.zip\n"u8.ToArray())) };
        using var client = Client(handler);
        var sums = Package(14, UpdateCatalog.ChecksumsFileName);

        Assert.Equal("abc  file.zip\n", await client.GetTextAsync(sums, 1024, TestContext.Current.CancellationToken));
        Assert.Equal(UpdateFailure.BadResponse, (await Failure(() => client.GetTextAsync(sums, 5, TestContext.Current.CancellationToken))).Failure);

        // An address outside this project's release downloads is refused before anything is requested.
        int before = handler.Requests.Count;
        var elsewhere = sums with { DownloadUrl = new Uri("https://example.org/SHA256SUMS.txt") };
        Assert.Equal(UpdateFailure.BadResponse, (await Failure(() => client.GetTextAsync(elsewhere, 1024, TestContext.Current.CancellationToken))).Failure);
        Assert.Equal(before, handler.Requests.Count);
    }

    /// <summary>A download lands whole in the target, its SHA-256 is that of the bytes, progress ends at the full size, and no partial file is left.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Download_WritesTheFile_AndReturnsItsHash()
    {
        using var temp = TestData.NewTempDirectory();
        var payload = Payload(250_000);
        var handler = new FakeHttpHandler { Respond = (_, _) => Task.FromResult(FakeHttpHandler.Bytes(payload)) };
        using var client = Client(handler);
        var target = Path.Combine(temp.Path, "nested", "package.zip");
        var reports = new List<long>();

        var hash = await client.DownloadAsync(Package(payload.Length), target, new SynchronousProgress(reports.Add), TestContext.Current.CancellationToken);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload)), hash);
        Assert.Equal(payload, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(target + ".part"));
        Assert.Equal(payload.Length, reports[^1]);
        Assert.Equal(reports.Order(), reports);
        Assert.Equal(hash, await UpdateClient.HashFileAsync(target, TestContext.Current.CancellationToken));
        Assert.False(Assert.Single(handler.Requests).HasCredentials);
    }

    /// <summary>
    /// A file that is not the size the release list states is refused — announced wrong, longer than stated, or cut
    /// short — and neither the target nor a partial file remains.
    /// </summary>
    /// <param name="scenario">How the size is wrong.</param>
    /// <param name="expected">The failure kind.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("announced-wrong", UpdateFailure.BadResponse)]
    [InlineData("longer", UpdateFailure.BadResponse)]
    [InlineData("shorter", UpdateFailure.Network)]
    [InlineData("breaks", UpdateFailure.Network)]
    [InlineData("server-error", UpdateFailure.Server)]
    public async Task Download_RefusesWhatIsNotTheStatedFile(string scenario, UpdateFailure expected)
    {
        using var temp = TestData.NewTempDirectory();
        var payload = Payload(10_000);
        var handler = new FakeHttpHandler
        {
            Respond = (_, _) => Task.FromResult(scenario switch
            {
                "announced-wrong" => FakeHttpHandler.Bytes(Payload(9_000)),
                "longer" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ScriptedContent(Payload(12_000), ScriptedContent.Ending.Complete) },
                "shorter" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ScriptedContent(Payload(4_000), ScriptedContent.Ending.Complete) },
                "breaks" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ScriptedContent(Payload(4_000), ScriptedContent.Ending.Fail, announcedLength: payload.Length) },
                _ => FakeHttpHandler.Status(HttpStatusCode.ServiceUnavailable),
            }),
        };
        using var client = Client(handler);
        var target = Path.Combine(temp.Path, "package.zip");

        var failure = await Failure(() => client.DownloadAsync(Package(payload.Length), target, null, TestContext.Current.CancellationToken));

        Assert.Equal(expected, failure.Failure);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    /// <summary>A failed download leaves an earlier complete file at the target untouched.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Download_AFailureKeepsTheFileThatWasThere()
    {
        using var temp = TestData.NewTempDirectory();
        var target = Path.Combine(temp.Path, "package.zip");
        await File.WriteAllTextAsync(target, "earlier", TestContext.Current.CancellationToken);
        var handler = new FakeHttpHandler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ScriptedContent(Payload(500), ScriptedContent.Ending.Fail) }) };
        using var client = Client(handler);

        await Failure(() => client.DownloadAsync(Package(10_000), target, null, TestContext.Current.CancellationToken));

        Assert.Equal("earlier", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
    }

    /// <summary>A connection that stops sending is given up after the stall limit, as a network failure.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Download_GivesUpOnAStalledConnection()
    {
        using var temp = TestData.NewTempDirectory();
        var handler = new FakeHttpHandler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ScriptedContent(Payload(2_000), ScriptedContent.Ending.Hang) }) };
        using var client = Client(handler);
        client.StallTimeout = TimeSpan.FromMilliseconds(200);
        var target = Path.Combine(temp.Path, "package.zip");

        var failure = await Failure(() => client.DownloadAsync(Package(10_000), target, null, TestContext.Current.CancellationToken));

        Assert.Equal(UpdateFailure.Network, failure.Failure);
        Assert.Contains("did not answer in time", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(target + ".part"));
    }

    /// <summary>Cancelling a download is a cancellation, and the partial file is removed.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Download_CancelRemovesThePartialFile()
    {
        using var temp = TestData.NewTempDirectory();
        var handler = new FakeHttpHandler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ScriptedContent(Payload(2_000), ScriptedContent.Ending.Hang) }) };
        using var client = Client(handler);
        var target = Path.Combine(temp.Path, "package.zip");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Wait until the partial file exists, so the cancellation really interrupts a download in progress.
        var pending = client.DownloadAsync(Package(10_000), target, null, cancel.Token);
        for (int i = 0; i < 200 && !File.Exists(target + ".part"); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    /// <summary>An untrusted address, an impossible size and an unwritable folder are refused with the right kind.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Download_RefusesUntrustedHugeAndUnwritable()
    {
        using var temp = TestData.NewTempDirectory();
        var handler = new FakeHttpHandler { Respond = (_, _) => Task.FromResult(FakeHttpHandler.Bytes(Payload(100))) };
        using var client = Client(handler);
        var target = Path.Combine(temp.Path, "package.zip");

        var elsewhere = Package(100) with { DownloadUrl = new Uri("https://downloads.example.org/BetterClipboard.zip") };
        Assert.Equal(UpdateFailure.BadResponse, (await Failure(() => client.DownloadAsync(elsewhere, target, null, TestContext.Current.CancellationToken))).Failure);
        Assert.Equal(UpdateFailure.BadResponse, (await Failure(() => client.DownloadAsync(Package(UpdateClient.MaxPackageBytes + 1), target, null, TestContext.Current.CancellationToken))).Failure);
        Assert.Empty(handler.Requests);

        // A file where the folder should be: the download cannot be saved.
        var blocker = Path.Combine(temp.Path, "blocker");
        await File.WriteAllTextAsync(blocker, "x", TestContext.Current.CancellationToken);
        var storage = await Failure(() => client.DownloadAsync(Package(100), Path.Combine(blocker, "package.zip"), null, TestContext.Current.CancellationToken));
        Assert.Equal(UpdateFailure.Storage, storage.Failure);
    }

    /// <summary>An <see cref="IProgress{T}"/> that reports on the calling thread (so the test sees every value in order).</summary>
    /// <param name="handler">Receives each value.</param>
    private sealed class SynchronousProgress(Action<long> handler) : IProgress<long>
    {
        /// <inheritdoc />
        public void Report(long value) => handler(value);
    }
}
