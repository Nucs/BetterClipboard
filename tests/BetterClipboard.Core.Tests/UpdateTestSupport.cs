using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BetterClipboard.Core.Updates;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// A stand-in for the network: answers each request with whatever the test's <see cref="Respond"/> returns and keeps a
/// record of what was asked, so update tests never touch a real server.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Lock gate = new();
    private readonly List<RecordedRequest> requests = [];

    /// <summary>
    /// Produces the answer for a request; the default answers 404. It may throw (a network failure) or wait on the
    /// token (a server that never answers).
    /// </summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

    /// <summary>Every request received so far, in order (a copy).</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (gate)
            {
                return [.. requests];
            }
        }
    }

    /// <summary>How many requests asked for <paramref name="url"/> (compared as absolute addresses).</summary>
    /// <param name="url">The address.</param>
    /// <returns>The count.</returns>
    public int CountOf(Uri url) => Requests.Count(r => r.Url == url);

    /// <summary>A JSON answer with an entity tag.</summary>
    /// <param name="json">The body.</param>
    /// <param name="etag">The entity tag, quotes included; <see langword="null"/> for none.</param>
    /// <returns>The answer.</returns>
    public static HttpResponseMessage Json(string json, string? etag = "\"tag-1\"")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (etag is not null)
        {
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        }

        return response;
    }

    /// <summary>A binary answer; its length is announced as the bytes' length.</summary>
    /// <param name="bytes">The body.</param>
    /// <returns>The answer.</returns>
    public static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    /// <summary>An answer with only a status.</summary>
    /// <param name="status">The status code.</param>
    /// <returns>The answer.</returns>
    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new ByteArrayContent([]) };

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            requests.Add(new RecordedRequest(
                request.RequestUri!,
                request.Headers.TryGetValues("If-None-Match", out var tags) ? string.Join(",", tags) : null,
                request.Headers.TryGetValues("User-Agent", out var agents) ? string.Join(" ", agents) : null,
                request.Headers.TryGetValues("Accept", out var accepts) ? string.Join(",", accepts) : null,
                request.Headers.Contains("Cookie") || request.Headers.Contains("Authorization")));
        }

        return Respond(request, cancellationToken);
    }
}

/// <summary>
/// What one request carried, for assertions about what the updater sends.
/// </summary>
/// <param name="Url">The requested address.</param>
/// <param name="IfNoneMatch">The entity tag sent, or <see langword="null"/>.</param>
/// <param name="UserAgent">The User-Agent sent, or <see langword="null"/>.</param>
/// <param name="Accept">The Accept header sent, or <see langword="null"/>.</param>
/// <param name="HasCredentials">Whether a cookie or an authorization header was sent (the updater must send neither).</param>
internal sealed record RecordedRequest(Uri Url, string? IfNoneMatch, string? UserAgent, string? Accept, bool HasCredentials);

/// <summary>
/// A response body that hands out its bytes in pieces and can then fail or hang, to exercise the downloader against a
/// connection that breaks or stalls halfway.
/// </summary>
/// <param name="data">The bytes to deliver before <paramref name="ending"/> happens.</param>
/// <param name="ending">What happens after the last byte.</param>
/// <param name="announcedLength">The Content-Length to announce; <see langword="null"/> announces none.</param>
internal sealed class ScriptedContent(byte[] data, ScriptedContent.Ending ending, long? announcedLength = null) : HttpContent
{
    /// <summary>What a <see cref="ScriptedContent"/> does once its bytes are delivered.</summary>
    public enum Ending
    {
        /// <summary>The body ends normally.</summary>
        Complete,

        /// <summary>The connection breaks (an <see cref="IOException"/>).</summary>
        Fail,

        /// <summary>Nothing more arrives until the read is cancelled.</summary>
        Hang,
    }

    /// <inheritdoc />
    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new ScriptedStream(data, ending));

    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(data).AsTask();

    /// <inheritdoc />
    protected override bool TryComputeLength(out long length)
    {
        length = announcedLength ?? 0;
        return announcedLength is not null;
    }

    /// <summary>The stream behind <see cref="ScriptedContent"/>: at most 1,000 bytes per read, then the scripted ending.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="ending">What happens after them.</param>
    private sealed class ScriptedStream(byte[] data, Ending ending) : Stream
    {
        private int position;

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (position < data.Length)
            {
                int count = Math.Min(Math.Min(buffer.Length, 1000), data.Length - position);
                data.AsMemory(position, count).CopyTo(buffer);
                position += count;
                return count;
            }

            switch (ending)
            {
                case Ending.Fail:
                    throw new IOException("The connection was reset (test).");
                case Ending.Hang:
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                    return 0;
                default:
                    return 0;
            }
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        /// <exception cref="NotSupportedException">Always: the stream cannot seek.</exception>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        /// <exception cref="NotSupportedException">Always: the stream is read-only.</exception>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        /// <exception cref="NotSupportedException">Always: the stream is read-only.</exception>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// A clock and timer source the test moves by hand, so the update schedule (a first check after a minute, then one a
/// day) is tested in milliseconds.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock gate = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now;

    /// <summary>Creates the provider at a start time.</summary>
    /// <param name="start">The first "now".</param>
    public ManualTimeProvider(DateTimeOffset start) => now = start;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return now;
        }
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (gate)
        {
            timers.Add(timer);
        }

        return timer;
    }

    /// <summary>
    /// Moves the clock forward and runs, on the calling thread and in time order, every timer callback that falls due on
    /// the way.
    /// </summary>
    /// <param name="by">How far to move.</param>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (gate)
        {
            target = now + by;
        }

        while (true)
        {
            ManualTimer? next;
            lock (gate)
            {
                next = timers.Where(t => t.Due is not null && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    now = target;
                    return;
                }

                now = next.Due!.Value;
            }

            next.Fire();
        }
    }

    /// <summary>Forgets a disposed timer.</summary>
    /// <param name="timer">The timer.</param>
    private void Remove(ManualTimer timer)
    {
        lock (gate)
        {
            timers.Remove(timer);
        }
    }

    /// <summary>One timer of a <see cref="ManualTimeProvider"/>.</summary>
    /// <param name="owner">The provider.</param>
    /// <param name="callback">What to run when due.</param>
    /// <param name="state">The callback's argument.</param>
    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan period = Timeout.InfiniteTimeSpan;

        /// <summary>When the timer fires next; <see langword="null"/> while it is stopped.</summary>
        public DateTimeOffset? Due { get; private set; }

        /// <inheritdoc />
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            this.period = period;
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;
            return true;
        }

        /// <summary>Runs the callback and schedules the next period, if any.</summary>
        public void Fire()
        {
            Due = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? null : owner.GetUtcNow() + period;
            callback(state);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Due = null;
            owner.Remove(this);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// An installer that only records what it was asked to install; the test decides when, and with which exit code, it "ends".
/// </summary>
internal sealed class FakeInstaller : IUpdateInstaller
{
    private readonly TaskCompletionSource<int> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<UpdateInstallRequest> started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The request the service handed over; <see langword="null"/> while none arrived.</summary>
    public UpdateInstallRequest? Request { get; private set; }

    /// <summary>The package's bytes as they were on disk when the installer was started; <see langword="null"/> before.</summary>
    public byte[]? PackageBytes { get; private set; }

    /// <summary>When set, <see cref="StartAsync"/> fails with it instead of starting.</summary>
    public UpdateException? StartFailure { get; set; }

    /// <summary>Completes when the installer was started, with its request.</summary>
    public Task<UpdateInstallRequest> Started => started.Task;

    /// <summary>
    /// Ends the installer process: writes <paramref name="resultJson"/> to the request's result path when given, then
    /// completes with <paramref name="exitCode"/>.
    /// </summary>
    /// <param name="exitCode">The process's exit code.</param>
    /// <param name="resultJson">The result file's content, or <see langword="null"/> to write none.</param>
    public void End(int exitCode, string? resultJson = null)
    {
        if (resultJson is not null && Request is not null)
        {
            File.WriteAllText(Request.ResultPath, resultJson);
        }

        exit.TrySetResult(exitCode);
    }

    /// <inheritdoc />
    /// <exception cref="UpdateException"><see cref="StartFailure"/> is set.</exception>
    public Task<UpdateInstallerRun> StartAsync(UpdateInstallRequest request, CancellationToken cancellationToken)
    {
        if (StartFailure is not null)
        {
            throw StartFailure;
        }

        Request = request;
        PackageBytes = File.ReadAllBytes(request.PackagePath);
        started.TrySetResult(request);
        return Task.FromResult(new UpdateInstallerRun(exit.Task));
    }
}
