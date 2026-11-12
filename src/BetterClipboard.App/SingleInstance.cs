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
/// <para>
/// <b>Editions share the lock.</b> The Microsoft Store edition and every other copy use the same names (measured: named
/// objects are shared between packaged and unpackaged processes, CLAUDE.md §2.47), because only one BetterClipboard may
/// own the clipboard listener and Win+V. A Store edition that holds the lock also holds a marker event
/// (<c>….StoreEdition</c>), so a Store edition that starts second can tell whether the running copy is its own kind
/// (hand over as always) or another edition (<see cref="PrimaryIsStoreEdition"/>), which it then replaces: it asks
/// that copy to exit and takes the lock (<see cref="TryBecomePrimary"/>).
/// </para>
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private readonly string name;

    /// <summary>Whether this process is the Microsoft Store edition's regular instance, which marks the lock it holds.</summary>
    private readonly bool storeEdition;
    private readonly Mutex mutex;
    private readonly EventWaitHandle[] commands;
    private readonly ManualResetEvent stop = new(false);

    /// <summary>The marker a Store edition holds next to the lock; <see langword="null"/> for every other holder.</summary>
    private EventWaitHandle? storeMarker;

    /// <summary>This process's claim on the Store edition's setup (<see cref="TryClaimStoreSetup"/>); <see langword="null"/> without one.</summary>
    private Mutex? setupClaim;

    /// <summary>Set by a later launch that found the setup claimed: "show your window". <see langword="null"/> without a claim.</summary>
    private EventWaitHandle? setupShow;

    /// <summary>The pool's wait on <see cref="setupShow"/> (<see cref="ListenForSetupShow"/>); <see langword="null"/> while nobody listens.</summary>
    private RegisteredWaitHandle? setupShowWait;
    private Thread? listener;

    /// <summary>Wraps the acquired kernel objects.</summary>
    /// <param name="name">The instance name the object names are built from.</param>
    /// <param name="storeEdition">Whether this process is the Store edition's regular instance.</param>
    /// <param name="mutex">The instance mutex.</param>
    /// <param name="commands">One event per <see cref="InstanceCommand"/>, indexed by its value.</param>
    /// <param name="isPrimary">Whether this process created (and owns) the mutex.</param>
    private SingleInstance(string name, bool storeEdition, Mutex mutex, EventWaitHandle[] commands, bool isPrimary)
    {
        this.name = name;
        this.storeEdition = storeEdition;
        this.mutex = mutex;
        this.commands = commands;
        IsPrimary = isPrimary;
    }

    /// <summary>
    /// Whether this process is the owning instance: from the start, or since <see cref="TryBecomePrimary"/> took the
    /// lock over from another edition.
    /// </summary>
    public bool IsPrimary { get; private set; }

    /// <summary>
    /// Creates or opens the guard objects.
    /// </summary>
    /// <param name="name">Application name used to build the object names.</param>
    /// <param name="storeEdition">
    /// Whether this process is the Microsoft Store edition's regular instance: when it holds the lock, it marks it, so
    /// another Store edition process can tell (<see cref="PrimaryIsStoreEdition"/>).
    /// </param>
    /// <returns>The guard; check <see cref="IsPrimary"/>.</returns>
    /// <exception cref="UnauthorizedAccessException">An object with the same name exists with incompatible security (e.g. created by an elevated instance).</exception>
    public static SingleInstance Acquire(string name, bool storeEdition = false)
    {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{name}.Instance", out bool createdNew);
        var commands = Enum.GetValues<InstanceCommand>()
            .Select(command => new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.{command}"))
            .ToArray();
        var instance = new SingleInstance(name, storeEdition, mutex, commands, createdNew);
        if (createdNew)
        {
            instance.MarkStoreEdition();
        }

        return instance;
    }

    /// <summary>
    /// Whether the instance that holds the lock is the Microsoft Store edition (it holds the marker). For a process
    /// that is not the primary instance: <see langword="false"/> means the running copy is another edition, or a Store
    /// edition older than the marker (there is none).
    /// </summary>
    /// <returns><see langword="true"/> when the marker exists.</returns>
    public bool PrimaryIsStoreEdition()
    {
        if (EventWaitHandle.TryOpenExisting(MarkerName, out var marker))
        {
            marker.Dispose();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Takes the lock if it is free now: for a Store edition that asked another edition to exit and waits for it. Must
    /// run on the thread that called <see cref="Acquire"/> (a mutex belongs to a thread; here the UI thread, which is
    /// the main thread).
    /// </summary>
    /// <returns><see langword="true"/> once this process is the primary instance.</returns>
    public bool TryBecomePrimary()
    {
        if (IsPrimary)
        {
            return true;
        }

        try
        {
            if (!mutex.WaitOne(0))
            {
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // The copy that held it ended without releasing it (it was stopped): the lock is this thread's now.
        }

        IsPrimary = true;
        MarkStoreEdition();
        return true;
    }

    /// <summary>
    /// Forgets commands that were signalled while nobody listened: after a take-over, an exit request meant for the
    /// copy that is gone must not end this one the moment it starts listening.
    /// </summary>
    public void DiscardPendingCommands()
    {
        foreach (var command in commands)
        {
            command.Reset();
        }
    }

    /// <summary>
    /// Claims the Microsoft Store edition's setup for this process: for a Store edition that goes on without the lock
    /// because another edition holds it. While its setup window is open, the lock still belongs to that other copy, so a
    /// second launch of the Store edition would open a second setup window; this claim is what tells it that one is
    /// open already.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when this process now owns the setup (until it ends). <see langword="false"/> when another
    /// process of this edition owns it: that process was asked to bring its window to the front
    /// (<see cref="ListenForSetupShow"/>), and this one has nothing left to do.
    /// </returns>
    /// <exception cref="UnauthorizedAccessException">An object with the same name exists with incompatible security.</exception>
    public bool TryClaimStoreSetup()
    {
        if (setupClaim is not null)
        {
            return true;
        }

        // Its existence is the claim; nobody waits on it, so no thread owns it.
        var claim = new Mutex(initiallyOwned: false, $@"Local\{name}.StoreSetup", out bool createdNew);
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.StoreSetupShow");
        if (!createdNew)
        {
            show.Set();
            show.Dispose();
            claim.Dispose();
            return false;
        }

        setupClaim = claim;
        setupShow = show;
        return true;
    }

    /// <summary>
    /// Calls <paramref name="onShow"/> (on a pool thread) whenever a later launch of the Store edition found this
    /// process's setup open (<see cref="TryClaimStoreSetup"/>). Does nothing in a process that made no claim: there a
    /// later launch reaches the lock's own commands.
    /// </summary>
    /// <param name="onShow">Brings the setup window to the front; marshal to the UI thread inside it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="onShow"/> is <see langword="null"/>.</exception>
    public void ListenForSetupShow(Action onShow)
    {
        ArgumentNullException.ThrowIfNull(onShow);
        if (setupShow is not null && setupShowWait is null)
        {
            setupShowWait = ThreadPool.RegisterWaitForSingleObject(setupShow, (_, _) => onShow(), null, Timeout.Infinite, executeOnlyOnce: false);
        }
    }

    /// <summary>
    /// Gives the lock up without ending the process: for a Store edition that closed another edition for a move the user
    /// then left, right before it starts that copy again (a copy that starts into the tray ends at once while the lock is
    /// held). Stops listening too: commands sent from now on are for that copy. Must run on the thread that took the
    /// lock; does nothing when this process does not hold it.
    /// </summary>
    public void StepDown()
    {
        if (!IsPrimary)
        {
            return;
        }

        stop.Set();
        listener?.Join(TimeSpan.FromSeconds(1));
        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread (should not happen); the process is about to end, which frees it either way.
        }

        storeMarker?.Dispose();
        storeMarker = null;
        IsPrimary = false;
    }

    /// <summary>The marker's name next to the lock.</summary>
    private string MarkerName => $@"Local\{name}.StoreEdition";

    /// <summary>Creates the marker when this process is the Store edition's regular instance and holds the lock.</summary>
    private void MarkStoreEdition()
    {
        if (storeEdition && storeMarker is null)
        {
            storeMarker = new EventWaitHandle(false, EventResetMode.ManualReset, MarkerName);
        }
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
    /// Opens the command events of <paramref name="name"/> without joining the instance, so that a command sent while no
    /// process listens (between the two processes of a silent restart, CLAUDE.md §2.46) is kept until the next primary
    /// instance listens: a named event exists only while someone holds a handle to it, and its signal with it.
    /// </summary>
    /// <param name="name">Instance name (see <see cref="Core.AppPaths.InstanceName"/>).</param>
    /// <returns>The handles; dispose them once <see cref="Acquire"/> has opened the same events.</returns>
    /// <exception cref="UnauthorizedAccessException">An event with the same name exists with incompatible security.</exception>
    public static IDisposable KeepCommandsAlive(string name)
    {
        var events = Enum.GetValues<InstanceCommand>()
            .Select(command => new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.{command}"))
            .ToArray();
        return new HandleSet(events);
    }

    /// <summary>
    /// The name of the event a silent restart's new process sets once it holds the shortcuts and shows the tray icon, and
    /// the old process waits for before it lets go of them (CLAUDE.md §2.46).
    /// </summary>
    /// <param name="name">Instance name.</param>
    /// <returns>The kernel object name.</returns>
    public static string RestartReadyEventName(string name) => $@"Local\{name}.RestartReady";

    /// <summary>
    /// The name of the event an old process sets when it calls a silent restart off after the new process started (the
    /// panel opened in between): the new process then ends quietly instead of taking over.
    /// </summary>
    /// <param name="name">Instance name.</param>
    /// <returns>The kernel object name.</returns>
    public static string RestartCancelEventName(string name) => $@"Local\{name}.RestartCancel";

    /// <summary>
    /// Stops listening for commands without letting go of the instance lock: a process that hands over to a silent
    /// restart's new process leaves the commands sent from now on to it (<see cref="KeepCommandsAlive"/> keeps them).
    /// </summary>
    public void StopListening()
    {
        stop.Set();
        listener?.Join(TimeSpan.FromSeconds(1));
    }

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
        storeMarker?.Dispose();

        // The wait first: its callback must not run on an event that is being closed.
        setupShowWait?.Unregister(null);
        setupShow?.Dispose();
        setupClaim?.Dispose();
        foreach (var command in commands)
        {
            command.Dispose();
        }

        stop.Dispose();
    }

    /// <summary>Handles that are disposed together (<see cref="KeepCommandsAlive"/>).</summary>
    /// <param name="handles">The handles.</param>
    private sealed class HandleSet(IReadOnlyList<WaitHandle> handles) : IDisposable
    {
        /// <summary>Closes every handle; the events live on while another process holds them.</summary>
        public void Dispose()
        {
            foreach (var handle in handles)
            {
                handle.Dispose();
            }
        }
    }
}
