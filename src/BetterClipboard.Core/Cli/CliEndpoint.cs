using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace BetterClipboard.Core.Cli;

/// <summary>
/// Where and how <c>bclip</c> reaches the running app: the per-user pipe name and the message limits.
/// </summary>
/// <remarks>
/// Pipe names live in one machine-wide namespace (every session can list <c>\\.\pipe\</c>), so the name
/// is derived from the user's SID and session id — two signed-in users, or one user in two sessions, get
/// separate pipes — and the SID is hashed so the listing does not reveal who runs BetterClipboard.
/// </remarks>
public static class CliEndpoint
{
    /// <summary>Protocol version of <see cref="CliRequest"/>/<see cref="CliResponse"/>; bump on breaking changes.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// Largest message either side accepts (64 MB): enough for a large image as Base64, small enough that a
    /// misbehaving client cannot make the app buffer unbounded data.
    /// </summary>
    public const int MaxMessageBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Builds the pipe name for a user and session.
    /// </summary>
    /// <param name="userSid">The Windows user SID (e.g. <c>S-1-5-21-…</c>); case-insensitive.</param>
    /// <param name="sessionId">The Windows session id (<c>Process.SessionId</c>).</param>
    /// <param name="instanceScope">
    /// <see cref="AppPaths.InstanceScope"/>: <see langword="null"/> for the installed app, otherwise the
    /// scope of an isolated (data-directory override) instance, which gets its own pipe — bclip run with
    /// the same override talks to that instance only.
    /// </param>
    /// <returns>A name like <c>BetterClipboard.Cli.1f2e3d4c5b6a7980.1</c> (plus <c>.dir-…</c> when scoped).</returns>
    /// <exception cref="ArgumentException"><paramref name="userSid"/> is null or blank.</exception>
    public static string PipeName(string userSid, int sessionId, string? instanceScope = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("BetterClipboard/CliPipe/v1:" + userSid.Trim().ToUpperInvariant()));
        var name = $"BetterClipboard.Cli.{Convert.ToHexStringLower(hash.AsSpan(0, 8))}.{sessionId}";
        return instanceScope is null ? name : $"{name}.{instanceScope}";
    }
}

/// <summary>
/// Line framing for the pipe: one UTF-8 JSON value terminated by <c>\n</c> per direction, read with a hard
/// size limit (a <see cref="StreamReader"/> would buffer an endless line forever).
/// </summary>
public static class CliWire
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Reads one line.
    /// </summary>
    /// <param name="stream">The pipe.</param>
    /// <param name="maxBytes">Size limit (without the newline).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The line without its newline, or <see langword="null"/> when the peer closed before sending anything.</returns>
    /// <exception cref="InvalidDataException">The line exceeds <paramref name="maxBytes"/> or is not valid UTF-8.</exception>
    /// <exception cref="IOException">The peer closed mid-line or the pipe broke.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static async Task<string?> ReadLineAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.Length == 0 ? null : throw new IOException("The connection closed in the middle of a message.");
            }

            int newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
            int take = newline >= 0 ? newline : read;
            if (buffer.Length + take > maxBytes)
            {
                throw new InvalidDataException($"Message larger than {maxBytes:N0} bytes.");
            }

            buffer.Write(chunk, 0, take);
            if (newline >= 0)
            {
                // Each side sends exactly one line and then waits, so nothing follows the newline.
                try
                {
                    return Utf8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                }
                catch (DecoderFallbackException ex)
                {
                    throw new InvalidDataException("Message is not valid UTF-8.", ex);
                }
            }
        }
    }

    /// <summary>
    /// Writes one line and flushes it.
    /// </summary>
    /// <param name="stream">The pipe.</param>
    /// <param name="line">The JSON value (must not contain raw newlines — compact JSON never does).</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task completing once the line is flushed.</returns>
    /// <exception cref="IOException">The pipe broke.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(line);
        var bytes = Utf8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The <c>bclip</c> side of the pipe: one request, one response, one connection.
/// </summary>
public static class CliClient
{
    /// <summary>
    /// Sends <paramref name="request"/> to the app and returns its response.
    /// </summary>
    /// <remarks>
    /// <see cref="PipeOptions.CurrentUserOnly"/> makes the client verify that the pipe is owned by the
    /// current user: a pipe pre-created under the same name by another account (squatting) is refused
    /// instead of being handed our request.
    /// </remarks>
    /// <param name="pipeName">From <see cref="CliEndpoint.PipeName"/>.</param>
    /// <param name="request">The request.</param>
    /// <param name="connectTimeout">How long to wait for the server to accept.</param>
    /// <param name="cancellationToken">Cancels the whole exchange (e.g. Ctrl+C during <c>wait</c>).</param>
    /// <returns>The response.</returns>
    /// <exception cref="TimeoutException">No server accepted in time (the app is not running or command-line access is off).</exception>
    /// <exception cref="UnauthorizedAccessException">The pipe exists but is not owned by the current user.</exception>
    /// <exception cref="IOException">The connection broke, the server closed without answering, or it did not answer within <see cref="ResponseTimeout"/>.</exception>
    /// <exception cref="InvalidDataException">The response was oversized or not UTF-8.</exception>
    /// <exception cref="System.Text.Json.JsonException">The response was not valid JSON.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<CliResponse> SendAsync(string pipeName, CliRequest request, TimeSpan connectTimeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(request);
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync((int)Math.Clamp(connectTimeout.TotalMilliseconds, 0, int.MaxValue), cancellationToken).ConfigureAwait(false);

        // An overall deadline, so a wedged server can never hang a script or an AI agent forever.
        var deadline = ResponseTimeout(request);
        using var exchange = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        exchange.CancelAfter(deadline);
        try
        {
            await CliWire.WriteLineAsync(pipe, CliJson.Serialize(request), exchange.Token).ConfigureAwait(false);
            var line = await CliWire.ReadLineAsync(pipe, CliEndpoint.MaxMessageBytes, exchange.Token).ConfigureAwait(false)
                ?? throw new IOException("BetterClipboard closed the connection without answering.");
            return CliJson.DeserializeResponse(line);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"BetterClipboard did not answer within {deadline.TotalSeconds:0} s.");
        }
    }

    /// <summary>
    /// The response deadline for a request: the <c>wait</c> timeout plus slack, otherwise two minutes (a
    /// large image export over the pipe takes well under a second; this only guards against a wedged app).
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The deadline.</returns>
    public static TimeSpan ResponseTimeout(CliRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Command == CliCommands.Wait
            ? TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds ?? 60, 1, 3600) + 30)
            : TimeSpan.FromMinutes(2);
    }
}
