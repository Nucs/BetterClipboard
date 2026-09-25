using System.IO.Pipes;
using System.Text;
using BetterClipboard.Core.Cli;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;
using BetterClipboard.Windows.Cli;
using BetterClipboard.Windows.Clipboard;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests the bclip pipe with real named pipes: round trip, refusal to share a name (squatting), concurrent
/// clients, hang-up cancellation and malformed input.
/// </summary>
public sealed class CliPipeServerTests
{
    /// <summary>A request travels to the handler and its response comes back intact (Unicode included).</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task RoundTrip()
    {
        await using var server = StartServer((request, _) => Task.FromResult(new CliResponse { Ok = true, Message = $"echo {request.Command} {request.Query}" }));
        var response = await Send(server, new CliRequest { Command = CliCommands.Search, Query = "שלום ✓" });
        Assert.True(response.Ok);
        Assert.Equal("echo search שלום ✓", response.Message);
    }

    /// <summary>A second server for the same name fails at start instead of sharing it (FirstPipeInstance).</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task SecondServer_IsRefused()
    {
        await using var first = StartServer((_, _) => Task.FromResult(new CliResponse { Ok = true }));
        await using var second = new CliPipeServer(first.PipeName, (_, _) => Task.FromResult(new CliResponse { Ok = true }));
        Assert.ThrowsAny<Exception>(second.Start);
        Assert.True((await Send(first, new CliRequest { Command = CliCommands.Status })).Ok);
    }

    /// <summary>A long-running request (like wait) does not block other clients.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task SlowRequest_DoesNotBlockOthers()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = StartServer(async (request, cancellationToken) =>
        {
            if (request.Command == CliCommands.Wait)
            {
                await release.Task.WaitAsync(cancellationToken);
            }

            return new CliResponse { Ok = true, Message = request.Command };
        });

        var slow = Send(server, new CliRequest { Command = CliCommands.Wait });
        var quick = await Send(server, new CliRequest { Command = CliCommands.Status }).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(CliCommands.Status, quick.Message);
        Assert.False(slow.IsCompleted);
        release.SetResult();
        Assert.Equal(CliCommands.Wait, (await slow).Message);
    }

    /// <summary>When the client hangs up mid-request (Ctrl+C on bclip wait), the handler's token fires.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task ClientHangUp_CancelsHandler()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = StartServer(async (_, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw;
            }

            return new CliResponse { Ok = true };
        });

        using var hangUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CliClient.SendAsync(server.PipeName, new CliRequest { Command = CliCommands.Wait }, TimeSpan.FromSeconds(5), hangUp.Token));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The pipe is owned by the token's <b>user</b> SID and the client check accepts it. Guards the CI
    /// failure of 2026-09-25: GitHub runners test elevated, where the token's default owner is
    /// <c>BUILTIN\Administrators</c>, and <see cref="PipeOptions.CurrentUserOnly"/> (which compares with
    /// that default owner) refused the app's own pipe — as it would for any admin terminal.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Owner_IsTheUser_EvenWhenElevated()
    {
        await using var server = StartServer((_, _) => Task.FromResult(new CliResponse { Ok = true }));
        using var pipe = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, TestContext.Current.CancellationToken);

        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var owner = pipe.GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier));
        Assert.Equal(identity.User, owner);
        CliClient.VerifyServerOwner(pipe); // must not throw, elevated or not
        TestContext.Current.TestOutputHelper?.WriteLine($"elevated token (default owner differs from user): {identity.Owner != identity.User}");
    }

    /// <summary>Malformed JSON gets a bad_request answer; the server keeps serving afterwards.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task MalformedRequest_IsAnswered()
    {
        await using var server = StartServer((_, _) => Task.FromResult(new CliResponse { Ok = true }));
        using (var pipe = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await pipe.ConnectAsync(5000, TestContext.Current.CancellationToken);
            CliClient.VerifyServerOwner(pipe);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes("{not json\n"), TestContext.Current.CancellationToken);
            var line = await CliWire.ReadLineAsync(pipe, 1 << 20, TestContext.Current.CancellationToken);
            var answer = CliJson.DeserializeResponse(line!);
            Assert.Equal(CliErrorCodes.BadRequest, answer.ErrorCode);
        }

        Assert.True((await Send(server, new CliRequest { Command = CliCommands.Status })).Ok);
    }

    /// <summary>
    /// Regression guard for the accept-loop race (the served instance was captured by variable, so a
    /// late-starting connection task served the next, unconnected instance and the client hung): many
    /// back-to-back and parallel connections must all be answered, each within a short deadline.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task ManyConnections_AreAllServed()
    {
        int served = 0;
        await using var server = StartServer((request, _) =>
        {
            Interlocked.Increment(ref served);
            return Task.FromResult(new CliResponse { Ok = true, Message = request.Query });
        });

        async Task<string?> Ask(int i)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var response = await CliClient.SendAsync(server.PipeName, new CliRequest { Command = CliCommands.Search, Query = $"q{i}" }, TimeSpan.FromSeconds(5), deadline.Token);
            return response.Message;
        }

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal($"q{i}", await Ask(i));
        }

        var parallel = await Task.WhenAll(Enumerable.Range(100, 24).Select(Ask));
        Assert.Equal(Enumerable.Range(100, 24).Select(i => $"q{i}"), parallel);
        Assert.Equal(124, served);
    }

    /// <summary>Starts a server on a unique pipe name.</summary>
    /// <param name="handler">Request handler.</param>
    /// <returns>The running server.</returns>
    private static CliPipeServer StartServer(Func<CliRequest, CancellationToken, Task<CliResponse>> handler)
    {
        var server = new CliPipeServer($"BetterClipboard.Tests.{Guid.NewGuid():N}", handler);
        server.Start();
        return server;
    }

    /// <summary>Sends through the real client.</summary>
    /// <param name="server">Server.</param>
    /// <param name="request">Request.</param>
    /// <returns>The response.</returns>
    private static Task<CliResponse> Send(CliPipeServer server, CliRequest request) =>
        CliClient.SendAsync(server.PipeName, request, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
}

/// <summary>
/// End to end in a private window station: bclip client → pipe → processor → real clipboard monitor.
/// <c>put</c> and <c>copy</c> really write the (isolated) clipboard, and <c>wait</c> sees a copy made by
/// another program — without ever touching the user's clipboard or Win+V history.
/// </summary>
[Collection(IsolatedClipboardCollection.Name)]
public sealed class CliEndToEndTests : IAsyncLifetime
{
    private readonly IsolatedClipboard isolation = new();
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", Guid.NewGuid().ToString("N"));
    private ClipHistoryService history = null!;
    private ClipboardMonitor monitor = null!;
    private CliPipeServer server = null!;

    /// <summary>Wires store → history → monitor (isolated) → processor → pipe.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask InitializeAsync()
    {
        var store = new ClipStore(Path.Combine(dataDirectory, "history.db"));
        store.Initialize();
        history = new ClipHistoryService(store, null, () => new CaptureRules());
        history.Start();
        monitor = new ClipboardMonitor(() => new CaptureOptions(), new SourceAppResolver()) { IsolatedDesktop = isolation.Desktop };
        monitor.Captured += (_, capture) => history.TryEnqueueCapture(capture);
        monitor.Start();
        var processor = new CliCommandProcessor(history, monitor, null, () => new AppSettings(), () => null, "test");
        server = new CliPipeServer($"BetterClipboard.Tests.{Guid.NewGuid():N}", processor.ExecuteAsync);
        server.Start();
        return ValueTask.CompletedTask;
    }

    /// <summary>Tears everything down and restores the window station.</summary>
    /// <returns>A task.</returns>
    public async ValueTask DisposeAsync()
    {
        await server.DisposeAsync();
        monitor.Dispose();
        await history.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        isolation.Dispose();
        try
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>put → clipboard + history (not re-captured as a second item); copy restores an older item.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task PutAndCopy_WriteTheClipboard()
    {
        using var producer = new ClipboardProducer(isolation);
        var put = await Send(new CliRequest { Command = CliCommands.Put, Text = "from bclip ✓" });
        Assert.True(put.Ok, put.Error);
        Assert.Equal("from bclip ✓", await producer.ReadTextAsync());

        await producer.WriteBurstAsync(["copied by another app"]);
        await WaitUntil(async () => (await history.GetStatsAsync()).Count == 2);

        var copy = await Send(new CliRequest { Command = CliCommands.Copy, Id = put.Item!.Id });
        Assert.True(copy.Ok, copy.Error);
        Assert.Equal("from bclip ✓", await producer.ReadTextAsync());

        // Our own writes were ignored by the monitor: still exactly two items.
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(2, (await history.GetStatsAsync()).Count);
    }

    /// <summary>wait returns what another program copies next, with its full text.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Wait_SeesTheNextCopy()
    {
        using var producer = new ClipboardProducer(isolation);
        var waiting = Send(new CliRequest { Command = CliCommands.Wait, TimeoutSeconds = 10 });
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await producer.WriteBurstAsync(["the error message\nat line 42"]);
        var arrived = await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(arrived.Ok, arrived.Error);
        Assert.Equal("the error message\nat line 42", arrived.Item!.Text);
    }

    /// <summary>Sends through the real client.</summary>
    /// <param name="request">Request.</param>
    /// <returns>The response.</returns>
    private Task<CliResponse> Send(CliRequest request) =>
        CliClient.SendAsync(server.PipeName, request, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    /// <summary>Polls an async condition for up to 5 s.</summary>
    /// <param name="condition">Condition.</param>
    /// <returns>A task.</returns>
    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the capture.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
