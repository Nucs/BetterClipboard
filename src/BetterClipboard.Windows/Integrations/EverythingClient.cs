using System.Runtime.InteropServices;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Everything;
using BetterClipboard.Windows.Interop;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Integrations;

/// <summary>
/// Talks to a running voidtools Everything over its window-message IPC: finds and verifies its window, reads its
/// state, runs queries and command lines. Read-only towards Everything — it never changes Everything's settings,
/// database or run history.
/// </summary>
/// <remarks>
/// <para>
/// <b>Replies.</b> Everything answers a query with a <c>WM_COPYDATA</c> sent to a window of ours, after the sending
/// call returned. The reply window is message-only and lives on its own <see cref="MessageWindowThread"/>, so
/// replies arrive however busy the caller's thread is. The window accepts <c>WM_COPYDATA</c> from a lower
/// integrity level, so an elevated BetterClipboard still hears a non-elevated Everything (what ES does).
/// </para>
/// <para>
/// <b>One query at a time.</b> Everything runs one query per reply window and silently cancels the previous one,
/// which then never gets a reply (verified). The client mirrors that: a new query cancels the pending one
/// (its task is cancelled), and every query has a deadline — Everything holds queries while its database loads,
/// for seconds to minutes. Late replies, and replies whose id does not match, are dropped.
/// </para>
/// <para>
/// <b>Which Everything.</b> The unnamed instance (1.4 and the 1.5 beta), then the 1.5 alpha's <c>1.5a</c> — unless
/// <see cref="InstanceVariable"/> names a single instance (development and tests: a private named instance, never
/// the user's). A window whose owner fails <see cref="EverythingOwnerVerifier"/> is never queried.
/// </para>
/// </remarks>
public sealed class EverythingClient : IDisposable
{
    /// <summary>
    /// Environment variable naming the one Everything instance to talk to (e.g. <c>BCTEST</c>), for development and
    /// tests; the user's own Everything is then never contacted. Unset: <see cref="DefaultInstances"/>.
    /// </summary>
    public const string InstanceVariable = "BETTERCLIPBOARD_EVERYTHING_INSTANCE";

    /// <summary>Instances tried in order: the unnamed one (1.4, 1.5 beta), then the 1.5 alpha.</summary>
    public static readonly IReadOnlyList<string> DefaultInstances = ["", "1.5a"];

    /// <summary>Longest wait for one state question; a busy Everything answers in microseconds, a hung one never.</summary>
    private const uint StateTimeoutMs = 500;

    /// <summary>Longest wait for Everything to accept a query or command line (the reply comes later, separately).</summary>
    private const uint SendTimeoutMs = 2000;

    /// <summary><c>SW_SHOWNORMAL</c>: how "Show in Everything" opens its search window.</summary>
    private const int ShowNormal = 1;

    private readonly MessageWindowThread window;
    private readonly IReadOnlyList<string> instances;
    private readonly Func<uint, EverythingTrust> verifyOwner;
    private readonly Lock gate = new();
    private PendingQuery? pending;
    private uint nextReplyId;
    private bool disposed;

    /// <summary>
    /// Creates the client and its reply window.
    /// </summary>
    /// <param name="instances">Instances to look for, in order; <see langword="null"/> = <see cref="InstancesFromEnvironment"/>.</param>
    /// <param name="verifyOwner">
    /// Trust check for the process owning a found window; <see langword="null"/> = <see cref="EverythingOwnerVerifier.VerifyProcess"/>.
    /// Tests pass a fake Everything that is not voidtools-signed.
    /// </param>
    /// <exception cref="System.ComponentModel.Win32Exception">The reply window could not be created.</exception>
    public EverythingClient(IReadOnlyList<string>? instances = null, Func<uint, EverythingTrust>? verifyOwner = null)
    {
        this.instances = instances ?? InstancesFromEnvironment();
        this.verifyOwner = verifyOwner ?? EverythingOwnerVerifier.VerifyProcess;
        window = new MessageWindowThread("Everything", messageOnly: true) { MessageHandler = OnMessage };

        // Lets an Everything at a lower integrity level answer when BetterClipboard runs elevated (what ES does).
        _ = window.InvokeAsync(() => ChangeWindowMessageFilterEx(window.Handle, WM_COPYDATA, MSGFLT_ALLOW, 0));
    }

    /// <summary>The instances this client looks for, in order.</summary>
    public IReadOnlyList<string> Instances => instances;

    /// <summary>
    /// The instances to look for: just the one in <see cref="InstanceVariable"/> when it is set, else
    /// <see cref="DefaultInstances"/>.
    /// </summary>
    /// <returns>The instance names.</returns>
    public static IReadOnlyList<string> InstancesFromEnvironment() =>
        Environment.GetEnvironmentVariable(InstanceVariable) is { Length: > 0 } instance ? [instance.Trim()] : DefaultInstances;

    /// <summary>Whether <see cref="InstanceVariable"/> pins the client to one instance (then the user's install must not be looked at either).</summary>
    public static bool IsInstanceOverridden => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(InstanceVariable));

    /// <summary>
    /// Finds Everything and reads its state: the first instance with a window decides. Fast (a window lookup, a
    /// cached signature check and a few state questions) but blocking for up to a few hundred milliseconds when
    /// Everything hangs — call it off the UI thread.
    /// </summary>
    /// <returns>The status; <see cref="EverythingState.NotRunning"/> when no window exists. Never throws.</returns>
    public EverythingStatus GetStatus()
    {
        foreach (var instance in instances)
        {
            nint hwnd = FindWindow(EverythingIpc.WindowClassFor(instance), null);
            if (hwnd == 0)
            {
                continue;
            }

            GetWindowThreadProcessId(hwnd, out uint processId);
            var trust = verifyOwner(processId);
            if (!trust.IsTrusted)
            {
                return new EverythingStatus(EverythingState.Untrusted, instance, hwnd, processId, null, false, trust);
            }

            var major = Ask(hwnd, EverythingIpc.GetMajorVersion);
            var loaded = Ask(hwnd, EverythingIpc.IsDbLoaded);
            if (major is null || loaded is null)
            {
                return new EverythingStatus(EverythingState.NotResponding, instance, hwnd, processId, null, false, trust);
            }

            var version = $"{major}.{Ask(hwnd, EverythingIpc.GetMinorVersion) ?? 0}.{Ask(hwnd, EverythingIpc.GetRevision) ?? 0}.{Ask(hwnd, EverythingIpc.GetBuildNumber) ?? 0}";
            bool busy = Ask(hwnd, EverythingIpc.IsDbBusy) == 1;
            var state = loaded == 1 ? EverythingState.Ready : EverythingState.Loading;
            return new EverythingStatus(state, instance, hwnd, processId, version, busy, trust);
        }

        return EverythingStatus.NotRunning;
    }

    /// <summary>
    /// Runs a query and waits for its reply (see class remarks for cancellation and deadlines).
    /// </summary>
    /// <param name="status">A <see cref="EverythingState.Ready"/> status from <see cref="GetStatus"/>.</param>
    /// <param name="search">Search text in Everything's syntax (<see cref="EverythingQuery"/>).</param>
    /// <param name="requestFlags">Fields to return (<c>EverythingIpc.Request*</c>).</param>
    /// <param name="sort">Sort (<c>EverythingIpc.Sort*</c>).</param>
    /// <param name="maxResults">Most results to return.</param>
    /// <param name="timeout">Deadline for the reply.</param>
    /// <param name="cancellationToken">Cancels the wait (Everything keeps the query until the next one replaces it).</param>
    /// <returns>The decoded reply.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="status"/> is not ready.</exception>
    /// <exception cref="EverythingUnavailableException">Everything refused the query or did not take it in time.</exception>
    /// <exception cref="TimeoutException">No reply before <paramref name="timeout"/> (e.g. its database is loading).</exception>
    /// <exception cref="OperationCanceledException">Cancelled, or replaced by a newer query.</exception>
    /// <exception cref="FormatException">The reply was malformed.</exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public async Task<EverythingList> QueryAsync(EverythingStatus status, string search, uint requestFlags, uint sort, uint maxResults, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(search);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (status.State != EverythingState.Ready)
        {
            throw new InvalidOperationException($"Everything is not ready ({status.State}).");
        }

        var query = new PendingQuery(Interlocked.Increment(ref nextReplyId));
        PendingQuery? previous;
        lock (gate)
        {
            previous = pending;
            pending = query;
        }

        // Everything cancels the previous query of this window itself; its awaiter must not wait out a deadline.
        previous?.Completion.TrySetCanceled();
        try
        {
            var bytes = EverythingIpc.EncodeQuery2(window.Handle, query.ReplyId, search, requestFlags, sort, maxResults);

            // The send blocks until Everything has taken the query (not answered it): off the caller's thread.
            bool accepted = await Task.Run(() => SendCopyData(status.Window, EverythingIpc.CopyDataQuery2W, bytes), cancellationToken).ConfigureAwait(false);
            if (!accepted)
            {
                throw new EverythingUnavailableException("Everything did not accept the query (it may have exited or be hung).");
            }

            var reply = await query.Completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return EverythingIpc.DecodeList2(reply);
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(pending, query))
                {
                    pending = null;
                }
            }
        }
    }

    /// <summary>
    /// Runs an Everything command line in the running instance, e.g. <c>-s "C:\x.txt"</c> to open a search window
    /// showing a file ("Show in Everything"). Lets Everything take the foreground, which a background process may
    /// otherwise not do.
    /// </summary>
    /// <param name="status">A status with a window (<see cref="EverythingStatus.Window"/> ≠ 0) that is trusted.</param>
    /// <param name="commandLine">The arguments (<see cref="EverythingQuery.ShowCommandLine"/>).</param>
    /// <returns><see langword="true"/> when Everything accepted it.</returns>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public bool RunCommandLine(EverythingStatus status, string commandLine)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(commandLine);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (status.Window == 0 || status.Trust?.IsTrusted != true)
        {
            return false;
        }

        // Only the foreground process may hand the foreground on; the panel is in front when the user asks.
        _ = AllowSetForegroundWindow(status.ProcessId);
        return SendCopyData(status.Window, EverythingIpc.CopyDataCommandLineUtf8, EverythingIpc.EncodeCommandLine(ShowNormal, commandLine));
    }

    /// <summary>Destroys the reply window; a pending query is cancelled.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        PendingQuery? last;
        lock (gate)
        {
            last = pending;
            pending = null;
        }

        last?.Completion.TrySetCanceled();
        window.Dispose();
    }

    /// <summary>
    /// The reply window's message hook: completes the pending query with a matching <c>WM_COPYDATA</c> reply. The
    /// bytes are copied before returning — the system frees them right after this call.
    /// </summary>
    /// <param name="msg">Message.</param>
    /// <param name="wParam">Sender window.</param>
    /// <param name="lParam">A <see cref="COPYDATASTRUCT"/> pointer for <c>WM_COPYDATA</c>.</param>
    /// <returns>1 for a handled reply; <see langword="null"/> to let the default procedure run.</returns>
    private unsafe nint? OnMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg != WM_COPYDATA || lParam == 0)
        {
            return null;
        }

        var data = (COPYDATASTRUCT*)lParam;
        PendingQuery? query;
        lock (gate)
        {
            query = pending;
        }

        if (query is null || data->dwData != query.ReplyId)
        {
            // A late reply to a replaced or timed-out query, or not ours at all.
            return 1;
        }

        var bytes = new byte[data->cbData];
        if (bytes.Length > 0)
        {
            Marshal.Copy(data->lpData, bytes, 0, bytes.Length);
        }

        query.Completion.TrySetResult(bytes);
        return 1;
    }

    /// <summary>
    /// Sends bytes to Everything's window with <c>WM_COPYDATA</c>, waiting at most <see cref="SendTimeoutMs"/> and
    /// failing at once for a hung window.
    /// </summary>
    /// <param name="everything">Everything's IPC window.</param>
    /// <param name="command">The <c>dwData</c> (an <c>EverythingIpc.CopyData*</c> command).</param>
    /// <param name="bytes">The payload.</param>
    /// <returns>Whether Everything took it (its message result was non-zero).</returns>
    private unsafe bool SendCopyData(nint everything, uint command, byte[] bytes)
    {
        fixed (byte* payload = bytes)
        {
            // The system copies the payload into Everything's process during the call; pinning it for the call is enough.
            var copy = new COPYDATASTRUCT { dwData = command, cbData = (uint)bytes.Length, lpData = (nint)payload };
            nint ok = SendMessageTimeout(everything, WM_COPYDATA, window.Handle, (nint)(&copy), SMTO_ABORTIFHUNG, SendTimeoutMs, out nint result);
            return ok != 0 && result != 0;
        }
    }

    /// <summary>Asks the IPC window one state question (<c>EVERYTHING_WM_IPC</c>).</summary>
    /// <param name="everything">Everything's IPC window.</param>
    /// <param name="command">An <c>EverythingIpc</c> state command.</param>
    /// <returns>The answer, or <see langword="null"/> when the window did not answer in time.</returns>
    private static int? Ask(nint everything, uint command) =>
        SendMessageTimeout(everything, EverythingIpc.WmIpc, (nint)command, 0, SMTO_ABORTIFHUNG, StateTimeoutMs, out nint result) == 0
            ? null
            : (int)result;

    /// <summary>The query waiting for its reply: its id and the task the reply completes.</summary>
    /// <param name="ReplyId">The id Everything echoes as the reply's <c>dwData</c>.</param>
    private sealed record PendingQuery(uint ReplyId)
    {
        /// <summary>Completed with the reply bytes; cancelled when replaced or when the client is disposed.</summary>
        public TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>What BetterClipboard knows about a running Everything.</summary>
public enum EverythingState
{
    /// <summary>No IPC window: not running (or the Lite build, which has no IPC, or another session's).</summary>
    NotRunning,

    /// <summary>A window with Everything's class exists, but its process is not a voidtools-signed Everything; never queried.</summary>
    Untrusted,

    /// <summary>The window does not answer state questions (hung).</summary>
    NotResponding,

    /// <summary>Running, but its database is still loading: queries would wait until it is done.</summary>
    Loading,

    /// <summary>Running with its database loaded: queries are answered.</summary>
    Ready,
}

/// <summary>A snapshot of a running Everything, from <see cref="EverythingClient.GetStatus"/>.</summary>
/// <param name="State">What it is doing.</param>
/// <param name="Instance">The instance name (empty for the unnamed instance), or <see langword="null"/> when not running.</param>
/// <param name="Window">Its IPC window (0 when not running).</param>
/// <param name="ProcessId">The window's process.</param>
/// <param name="Version">Its version, e.g. <c>1.5.0.1423</c>, when it answered.</param>
/// <param name="IsBusy">Rebuilding or updating its database (queries may be slow).</param>
/// <param name="Trust">The owner check's verdict, when a window was found.</param>
public sealed record EverythingStatus(EverythingState State, string? Instance, nint Window, uint ProcessId, string? Version, bool IsBusy, EverythingTrust? Trust)
{
    /// <summary>No Everything window was found.</summary>
    public static EverythingStatus NotRunning { get; } = new(EverythingState.NotRunning, null, 0, 0, null, false, null);

    /// <summary>The executable of the running, trusted Everything, when known.</summary>
    public string? ExecutablePath => Trust?.IsTrusted == true ? Trust.ExecutablePath : null;
}

/// <summary>Everything refused a request or did not take it in time.</summary>
public sealed class EverythingUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public EverythingUnavailableException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What failed.</param>
    public EverythingUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="innerException">The cause.</param>
    public EverythingUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
