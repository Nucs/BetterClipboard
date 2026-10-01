using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Windows.Interop;
using Microsoft.Win32;

namespace BetterClipboard.Windows.Integrations;

/// <summary>
/// Tells when Windows' Win+R history changes, the moment it changes: a dedicated thread waits on
/// <c>RegNotifyChangeKeyValue</c> for the key, or for its parent while the key does not exist yet.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a notification means.</b> Only "the key was written". One run raises up to two (the command, then the
/// order), and other writers may raise more; <see cref="Changed"/> carries no data, so the receiver debounces and
/// reads the key itself (with <see cref="RunMruReader.ReadSettledAsync"/>).
/// </para>
/// <para>
/// <b>Never miss a write.</b> Windows' registration is one-shot: after it fires, writes go unreported until the
/// next call. The loop therefore re-opens the key and re-registers <i>before</i> raising <see cref="Changed"/>, so
/// anything written after the event is reported again, and anything written in between is read by the rescan the
/// event triggers. Re-opening every round also follows a key that was deleted and created again.
/// </para>
/// <para>
/// <b>Keep the key open while waiting.</b> Closing a watched key ends its registration and signals the event, so a
/// loop that closed the key before waiting would wake at once, forever.
/// </para>
/// <para>
/// <b>Why a dedicated thread.</b> A registration belongs to the thread that made it and ends when that thread
/// exits, which pool threads do at will. Cost: one mostly blocked thread while the feature is on.
/// </para>
/// <para>
/// <b>Fallback.</b> When neither the key nor its parent exists (a test key), or registering fails, the thread
/// polls every <see cref="PollInterval"/> and reports a change only when the key appeared, disappeared or its last
/// write moved.
/// </para>
/// </remarks>
public sealed class RunMruWatcher : IDisposable
{
    /// <summary>How often the key is checked when it cannot be watched (no key and no parent, or a failed registration).</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long <see cref="Dispose"/> waits for the thread before leaving it to end on its own.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly string keyPath;
    private readonly ManualResetEvent stop = new(false);
    private Thread? thread;
    private int disposed;

    /// <summary>
    /// Creates the watcher; nothing is watched until <see cref="Start"/>.
    /// </summary>
    /// <param name="keyPath">The key, relative to <c>HKEY_CURRENT_USER</c> (<see cref="RunMruReader.ResolveKeyPath"/>).</param>
    /// <exception cref="ArgumentException"><paramref name="keyPath"/> is null or blank.</exception>
    public RunMruWatcher(string keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        this.keyPath = keyPath;
    }

    /// <summary>
    /// Raised on the watcher's thread after the key was written, created or deleted. Other subkeys of its parent
    /// coming and going (watched only to see the key appear) are not reported. Handlers must return quickly — the
    /// next change is only reported after they return — and should debounce.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Starts watching. Not raised for the state at start: the caller reads it itself right after.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Disposed.</exception>
    /// <exception cref="InvalidOperationException">Already started.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (thread is not null)
        {
            throw new InvalidOperationException("The Win+R history watcher was already started.");
        }

        thread = new Thread(Run) { IsBackground = true, Name = "Win+R history watch" };
        thread.Start();
    }

    /// <summary>Stops watching (waits up to 2 s for the thread); no <see cref="Changed"/> is raised afterwards.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        stop.Set();
        if (thread is null || thread.Join(StopTimeout))
        {
            stop.Dispose();
        }

        // Otherwise the thread still holds the event: it ends at its next wake-up (the event stays set), and the
        // finalizer releases the handle.
    }

    /// <summary>The watch loop (the dedicated thread).</summary>
    private void Run()
    {
        using var signal = new AutoResetEvent(false);
        var previousStamp = ReadStamp();
        bool wokeBySignal = false;
        bool watchedKeyItself = false;
        bool warned = false;
        while (true)
        {
            RegistryKey? watched = null;
            bool armed = false;
            bool isKey = false;
            try
            {
                watched = OpenWatchTarget(out isKey);
                if (watched is not null)
                {
                    // The key: any value written. Its parent (while the key is missing): subkeys created or deleted.
                    uint filter = isKey
                        ? NativeMethods.REG_NOTIFY_CHANGE_LAST_SET | NativeMethods.REG_NOTIFY_CHANGE_NAME
                        : NativeMethods.REG_NOTIFY_CHANGE_NAME;
                    int error = NativeMethods.RegNotifyChangeKeyValue(watched.Handle, false, filter, signal.SafeWaitHandle, true);
                    armed = error == 0;
                    if (!armed && !warned)
                    {
                        warned = true;
                        AppLog.Warn($"Cannot watch the Win+R history for changes (error {error}); checking every {PollInterval.TotalSeconds:0} s instead.");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A key deleted between opening and registering, or permissions changed: poll and retry next round.
                if (!warned)
                {
                    warned = true;
                    AppLog.Warn($"Watching the Win+R history failed ({ex.GetType().Name}); retrying every {PollInterval.TotalSeconds:0} s.");
                }
            }

            // Report only after re-arming (see the class remarks). A signal from the key itself is always a change;
            // one from its parent (any subkey of Explorer created or deleted) only when it was our key appearing, and
            // a poll round only when the key appeared, disappeared or was written.
            var stamp = ReadStamp();
            if ((wokeBySignal && watchedKeyItself) || stamp != previousStamp)
            {
                RaiseChanged();
            }

            previousStamp = stamp;
            watchedKeyItself = armed && isKey;

            // The watched key stays open until the wait ends: closing it would end the registration at once.
            int woke = WaitHandle.WaitAny([stop, signal], armed ? Timeout.InfiniteTimeSpan : PollInterval);
            watched?.Dispose();
            if (woke == 0)
            {
                return;
            }

            wokeBySignal = woke == 1;
        }
    }

    /// <summary>
    /// The change stamp used while polling: the key's last write, <see cref="DateTimeOffset.MinValue"/> when it
    /// exists but its metadata is unreadable, and <see langword="null"/> when it is missing or cannot be opened.
    /// </summary>
    /// <returns>The stamp.</returns>
    private DateTimeOffset? ReadStamp()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
            return key is null ? null : RunMruReader.LastWriteTime(key) ?? DateTimeOffset.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens what can be watched: the key itself, else its parent (to see the key being created).
    /// </summary>
    /// <param name="isKey">Whether the returned key is the watched key (not its parent).</param>
    /// <returns>An open key, or <see langword="null"/> when neither exists.</returns>
    /// <exception cref="System.Security.SecurityException">A key may not be read (policy).</exception>
    /// <exception cref="UnauthorizedAccessException">A key's permissions deny reading.</exception>
    private RegistryKey? OpenWatchTarget(out bool isKey)
    {
        isKey = true;
        var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        if (key is not null)
        {
            return key;
        }

        isKey = false;
        int separator = keyPath.LastIndexOf('\\');
        return separator > 0 ? Registry.CurrentUser.OpenSubKey(keyPath[..separator], writable: false) : null;
    }

    /// <summary>Raises <see cref="Changed"/>; a subscriber's exception is logged, never allowed to end the watch.</summary>
    private void RaiseChanged()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"A Win+R history change handler failed ({ex.GetType().Name}).");
        }
    }
}
