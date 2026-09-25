using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using BetterClipboard.Core.Cli;
using BetterClipboard.Core.Diagnostics;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Cli;

/// <summary>
/// The app side of <c>bclip</c>: a named pipe that accepts one JSON request per connection and answers
/// with one JSON response, serving up to <see cref="MaxInstances"/> clients at once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who can connect.</b> The pipe's DACL grants access to the current user only and explicitly denies
/// network logons (<c>NT AUTHORITY\NETWORK</c>), so it is reachable by local programs running as this
/// user — which is exactly the population the setting warns about. The owner is set to the user so
/// clients using <see cref="PipeOptions.CurrentUserOnly"/> accept it.
/// </para>
/// <para>
/// <b>Squatting.</b> The first instance is created with <see cref="PipeOptions.FirstPipeInstance"/>: if any
/// process (another account, a stale second copy) already owns the name, <see cref="Start"/> fails instead
/// of silently sharing the name with an impostor.
/// </para>
/// <para>
/// <b>Lifetime of a connection.</b> Read one bounded line → handle it while watching for the client to
/// hang up (Ctrl+C on <c>bclip wait</c> cancels the handler) → write the response → wait (≤ 5 s) for the
/// client to close before disposing, because closing the server end first could discard an unread reply.
/// </para>
/// </remarks>
public sealed class CliPipeServer : IAsyncDisposable
{
    /// <summary>Concurrent connections (a long <c>wait</c> must not block other commands).</summary>
    public const int MaxInstances = 8;

    private readonly Func<CliRequest, CancellationToken, Task<CliResponse>> handler;
    private readonly PipeSecurity security = CreateSecurity();
    private readonly CancellationTokenSource shutdown = new();
    private readonly ConcurrentDictionary<Task, byte> connections = new();
    private Task? acceptLoop;

    /// <summary>
    /// Creates a server; nothing listens until <see cref="Start"/>.
    /// </summary>
    /// <param name="pipeName">From <see cref="CliEndpoint.PipeName"/>.</param>
    /// <param name="handler">Executes a request (normally <see cref="CliCommandProcessor.ExecuteAsync"/>); the token fires when the client disconnects or the server stops.</param>
    /// <exception cref="ArgumentException"><paramref name="pipeName"/> is null or blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    public CliPipeServer(string pipeName, Func<CliRequest, CancellationToken, Task<CliResponse>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        PipeName = pipeName;
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <summary>The pipe name served.</summary>
    public string PipeName { get; }

    /// <summary>
    /// Creates the first pipe instance (synchronously, so conflicts surface to the caller) and starts accepting.
    /// </summary>
    /// <exception cref="InvalidOperationException">Already started.</exception>
    /// <exception cref="UnauthorizedAccessException">The name is taken by a pipe we may not share (squatting or another account).</exception>
    /// <exception cref="IOException">The name is already served (a second server).</exception>
    public void Start()
    {
        if (acceptLoop is not null)
        {
            throw new InvalidOperationException("The command-line server is already running.");
        }

        var first = CreateInstance(firstInstance: true);
        acceptLoop = Task.Run(() => AcceptLoopAsync(first));
    }

    /// <summary>Stops accepting, cancels running requests and waits (bounded) for them to finish.</summary>
    /// <returns>A task completing when stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        await shutdown.CancelAsync().ConfigureAwait(false);
        var pending = connections.Keys.ToList();
        if (acceptLoop is not null)
        {
            pending.Add(acceptLoop);
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            AppLog.Warn("Command-line connections did not finish within 5 s of shutdown.");
        }

        shutdown.Dispose();
    }

    /// <summary>The DACL described in the class remarks.</summary>
    /// <returns>The security descriptor.</returns>
    private static PipeSecurity CreateSecurity()
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The process token has no user SID.");
        var security = new PipeSecurity();
        security.SetOwner(user);

        // Synchronize is required for the client's GENERIC_READ|GENERIC_WRITE open; CreateNewInstance lets
        // this process add instances; ReadPermissions (in ReadWrite) lets clients verify the owner.
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    /// <summary>Creates one listening instance.</summary>
    /// <param name="firstInstance">Fail if the name already exists (only for the very first instance).</param>
    /// <returns>The instance.</returns>
    /// <exception cref="IOException">All instances are busy, or (first instance) the name exists.</exception>
    /// <exception cref="UnauthorizedAccessException">(First instance) the name exists and belongs to someone else.</exception>
    private NamedPipeServerStream CreateInstance(bool firstInstance) =>
        NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            MaxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            // Real buffers: with 0 every write is a hand-off that blocks until the peer reads, which turns
            // any ordering slip into a hang instead of a delay.
            inBufferSize: PipeBufferBytes,
            outBufferSize: PipeBufferBytes,
            security);

    /// <summary>Pipe buffer per direction (a typical request or small response fits entirely).</summary>
    private const int PipeBufferBytes = 64 * 1024;

    /// <summary>Accepts clients until shutdown, keeping one instance listening at all times.</summary>
    /// <param name="first">The instance created by <see cref="Start"/>.</param>
    /// <returns>A task completing at shutdown.</returns>
    private async Task AcceptLoopAsync(NamedPipeServerStream first)
    {
        NamedPipeServerStream? listener = first;
        while (listener is not null)
        {
            try
            {
                await listener.WaitForConnectionAsync(shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                await listener.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException ex)
            {
                // A client that connected and vanished at once; keep serving.
                AppLog.Warn($"Command-line connection failed: {ex.Message}");
                await listener.DisposeAsync().ConfigureAwait(false);
                listener = await CreateWhenAvailableAsync().ConfigureAwait(false);
                continue;
            }

            // Capture the connected instance by value: the lambda would otherwise capture the *variable*
            // `listener`, which is reassigned to the next (unconnected) instance just below — if the thread
            // pool started the lambda late, the connected client was never read and its write blocked forever.
            var connected = listener;
            var connection = Task.Run(() => ServeAsync(connected));
            connections[connection] = 0;
            _ = connection.ContinueWith(done => connections.TryRemove(done, out _), TaskScheduler.Default);

            // A fresh instance right away, so the next client can connect while this one is served.
            listener = await CreateWhenAvailableAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Creates the next listening instance, waiting while all <see cref="MaxInstances"/> are in use.</summary>
    /// <returns>The instance, or <see langword="null"/> at shutdown.</returns>
    private async Task<NamedPipeServerStream?> CreateWhenAvailableAsync()
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                return CreateInstance(firstInstance: false);
            }
            catch (IOException)
            {
                // ERROR_PIPE_BUSY: every instance is serving a client; one will free up soon.
                try
                {
                    await Task.Delay(50, shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>Serves one connected client (see the class remarks for the sequence).</summary>
    /// <param name="pipe">Connected instance; disposed here.</param>
    /// <returns>A task completing when the connection is closed.</returns>
    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe.ConfigureAwait(false))
        {
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            try
            {
                var line = await CliWire.ReadLineAsync(pipe, CliEndpoint.MaxMessageBytes, connection.Token).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                // The client sends nothing after its request, so a pending read completes only when it
                // hangs up — which cancels the handler (e.g. Ctrl+C during "bclip wait").
                var hangUp = pipe.ReadAsync(new byte[1], connection.Token).AsTask();
                _ = hangUp.ContinueWith(_ => TryCancel(connection), TaskScheduler.Default);

                var response = await HandleAsync(line, pipe, connection.Token).ConfigureAwait(false);
                if (response is null)
                {
                    return; // client gone or shutting down: nobody to answer
                }

                await CliWire.WriteLineAsync(pipe, CliJson.Serialize(response), shutdown.Token).ConfigureAwait(false);
                await hangUp.WaitAsync(TimeSpan.FromSeconds(5), shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or ObjectDisposedException)
            {
                // Client vanished, shutdown, or a client that never closed: nothing more to do.
            }
            catch (InvalidDataException ex)
            {
                // Oversized or non-UTF-8 request: answer once, best effort.
                await TryWriteAsync(pipe, CliResponse.Fail(CliErrorCodes.BadRequest, ex.Message)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Last line of defense: a connection task must never fault unobserved (and must never take
                // the accept loop with it). Log, answer if still possible, move on.
                AppLog.Error("Command-line connection failed unexpectedly.", ex);
                await TryWriteAsync(pipe, CliResponse.Fail(CliErrorCodes.Internal, ex.Message)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Parses and executes one request line.</summary>
    /// <param name="line">The request JSON.</param>
    /// <param name="pipe">The connection (for the client pid in the log).</param>
    /// <param name="cancellationToken">Fires on hang-up or shutdown.</param>
    /// <returns>The response, or <see langword="null"/> when the request was cancelled.</returns>
    private async Task<CliResponse?> HandleAsync(string line, NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        CliRequest request;
        try
        {
            request = CliJson.DeserializeRequest(line);
        }
        catch (JsonException ex)
        {
            return CliResponse.Fail(CliErrorCodes.BadRequest, $"Malformed request: {ex.Message}");
        }

        // Audit trail: which program used the clipboard history, never what it read.
        AppLog.Info($"Command line: {request.Command} (client {DescribeClient(pipe)}).");
        try
        {
            return await handler(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>"name (pid N)" of the connected client, for the log.</summary>
    /// <param name="pipe">Connected instance.</param>
    /// <returns>The description.</returns>
    private static string DescribeClient(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid))
        {
            return "unknown";
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return $"{process.ProcessName} pid {pid}";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return $"pid {pid}";
        }
    }

    /// <summary>Writes a response, ignoring a vanished client.</summary>
    /// <param name="pipe">Connection.</param>
    /// <param name="response">Response.</param>
    /// <returns>A task.</returns>
    private async Task TryWriteAsync(NamedPipeServerStream pipe, CliResponse response)
    {
        try
        {
            await CliWire.WriteLineAsync(pipe, CliJson.Serialize(response), shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Nobody left to tell.
        }
    }

    /// <summary>Cancels a connection's token unless it was already disposed.</summary>
    /// <param name="connection">The source.</param>
    private static void TryCancel(CancellationTokenSource connection)
    {
        try
        {
            connection.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The connection already finished.
        }
    }
}
