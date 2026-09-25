namespace BetterClipboard.App;

/// <summary>Requests a later launch can send to the running instance.</summary>
public enum InstanceCommand
{
    /// <summary>Show the main (settings) window — a plain second launch.</summary>
    Activate,

    /// <summary>Open the clipboard flyout (<c>--show-flyout</c>; bind it to a mouse button, Stream Deck, …).</summary>
    ShowFlyout,

    /// <summary>Quit gracefully (<c>--exit</c>; installers/updaters, scripts).</summary>
    Exit,
}

/// <summary>
/// Per-session single-instance guard (named mutex) plus a tiny command channel (one named auto-reset
/// event per <see cref="InstanceCommand"/>) so later launches can drive the first instance.
/// </summary>
/// <remarks>
/// Names live in the <c>Local\</c> namespace: one instance per logon session, which is exactly the
/// clipboard's scope (the clipboard is per window station). A crashed owner abandons the mutex and the
/// kernel deletes it with the last handle, so a restart after a crash is never blocked. Events carry no
/// payload by design — nothing a second process sends can inject data into the running instance.
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle[] commands;
    private readonly ManualResetEvent stop = new(false);
    private Thread? listener;

    /// <summary>Wraps the acquired kernel objects.</summary>
    /// <param name="mutex">The instance mutex.</param>
    /// <param name="commands">One event per <see cref="InstanceCommand"/>, indexed by its value.</param>
    /// <param name="isPrimary">Whether this process created (and owns) the mutex.</param>
    private SingleInstance(Mutex mutex, EventWaitHandle[] commands, bool isPrimary)
    {
        this.mutex = mutex;
        this.commands = commands;
        IsPrimary = isPrimary;
    }

    /// <summary>Whether this process is the first (owning) instance.</summary>
    public bool IsPrimary { get; }

    /// <summary>
    /// Creates or opens the guard objects.
    /// </summary>
    /// <param name="name">Application name used to build the object names.</param>
    /// <returns>The guard; check <see cref="IsPrimary"/>.</returns>
    /// <exception cref="UnauthorizedAccessException">An object with the same name exists with incompatible security (e.g. created by an elevated instance).</exception>
    public static SingleInstance Acquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{name}.Instance", out bool createdNew);
        var commands = Enum.GetValues<InstanceCommand>()
            .Select(command => new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.{command}"))
            .ToArray();
        return new SingleInstance(mutex, commands, createdNew);
    }

    /// <summary>
    /// Whether an instance with <paramref name="name"/> is running in this session (its lock exists),
    /// without joining or disturbing it.
    /// </summary>
    /// <param name="name">Instance name (see <see cref="Core.AppPaths.InstanceName"/>).</param>
    /// <returns><see langword="true"/> when that instance holds its lock.</returns>
    public static bool IsRunning(string name)
    {
        if (Mutex.TryOpenExisting($@"Local\{name}.Instance", out var mutex))
        {
            mutex.Dispose();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Sends a command to the primary instance.
    /// </summary>
    /// <param name="command">The command.</param>
    public void Signal(InstanceCommand command) => commands[(int)command].Set();

    /// <summary>
    /// Starts a background listener that invokes <paramref name="onCommand"/> (on the listener thread) for
    /// every command other launches send. Primary instance only.
    /// </summary>
    /// <param name="onCommand">Callback; marshal to the UI thread inside it.</param>
    /// <exception cref="InvalidOperationException">Called on a non-primary instance or twice.</exception>
    public void Listen(Action<InstanceCommand> onCommand)
    {
        if (!IsPrimary || listener is not null)
        {
            throw new InvalidOperationException("Only the primary instance listens, and only once.");
        }

        listener = new Thread(() =>
        {
            var handles = new WaitHandle[] { stop }.Concat(commands).ToArray();
            int signaled;
            while ((signaled = WaitHandle.WaitAny(handles)) > 0)
            {
                onCommand((InstanceCommand)(signaled - 1));
            }
        })
        {
            IsBackground = true,
            Name = "BetterClipboard.Commands",
        };
        listener.Start();
    }

    /// <summary>Stops the listener and releases the mutex (must run on the thread that acquired it — Main).</summary>
    public void Dispose()
    {
        stop.Set();
        listener?.Join(TimeSpan.FromSeconds(1));
        if (IsPrimary)
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned by this thread (should not happen); the handle close below still frees it.
            }
        }

        mutex.Dispose();
        foreach (var command in commands)
        {
            command.Dispose();
        }

        stop.Dispose();
    }
}
