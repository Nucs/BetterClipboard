using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using BetterClipboard.Core.Diagnostics;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Interop;

/// <summary>
/// A hidden window with its own Win32 message loop on a dedicated background thread — the host for
/// everything in BetterClipboard that needs window messages (clipboard listener, hotkeys, the
/// low-level keyboard hook, the tray icon), kept off the UI thread on purpose.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why dedicated threads.</b> A low-level keyboard hook is called synchronously for every keystroke in
/// the session; if its thread is busy (e.g. reading a 50 MB clipboard bitmap, or a UI layout pass) the
/// whole system's typing lags and Windows silently removes hooks that exceed <c>LowLevelHooksTimeout</c>.
/// Likewise the clipboard thread may block in <c>GetClipboardData</c> while another app renders a
/// delayed format. Each concern therefore gets its own loop.
/// </para>
/// <para>
/// <b>Message-only vs top-level.</b> Message-only windows (<c>HWND_MESSAGE</c>) are invisible to
/// enumeration and cheap, but they do <b>not</b> receive broadcasts such as <c>TaskbarCreated</c> — the
/// tray icon therefore asks for a hidden top-level window instead.
/// </para>
/// <para>
/// All callbacks (<see cref="MessageHandler"/>, <see cref="Post"/> actions) run on the window thread and
/// are wrapped so an exception is logged instead of unwinding into user32 (which would crash the process).
/// </para>
/// </remarks>
public sealed class MessageWindowThread : IDisposable
{
    /// <summary>Private message that drains <see cref="pending"/> on the window thread.</summary>
    private const uint WM_INVOKE = WM_APP + 0x100;

    /// <summary><c>WS_POPUP</c>: borderless top-level style for the hidden (non message-only) variant.</summary>
    private const uint WS_POPUP = 0x80000000;

    /// <summary><c>WS_EX_TOOLWINDOW</c>: keeps the hidden top-level window out of Alt+Tab and the taskbar.</summary>
    private const uint WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>
    /// Work waiting for the window thread. <c>Abandon</c> (optional) is invoked instead of <c>Run</c> when the
    /// thread shuts down first, so awaiters of <see cref="InvokeAsync{T}"/> fault instead of hanging forever.
    /// </summary>
    private readonly ConcurrentQueue<(Action Run, Action? Abandon)> pending = new();
    private readonly Thread thread;
    private readonly string name;
    private readonly bool messageOnly;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WndProc? wndProc;
    private string? className;
    private nint classNamePointer;
    private nint instance;
    private volatile bool disposed;

    /// <summary>
    /// Starts the thread, creates the window and waits until it is ready.
    /// </summary>
    /// <param name="name">Diagnostic name (thread name, window title, class name prefix).</param>
    /// <param name="messageOnly"><see langword="true"/> for a message-only window; <see langword="false"/> for a hidden top-level window that also receives broadcasts.</param>
    /// <param name="sta">Run the thread in a single-threaded COM apartment (needed by OLE clipboard consumers).</param>
    /// <exception cref="Win32Exception">The window class or window could not be created.</exception>
    public MessageWindowThread(string name, bool messageOnly = true, bool sta = false)
    {
        this.name = name;
        this.messageOnly = messageOnly;
        thread = new Thread(Run) { IsBackground = true, Name = $"BetterClipboard.{name}" };
        if (sta)
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();

        // Constructor-time failure is far easier to diagnose than a half-alive object: surface window
        // creation errors synchronously.
        ready.Task.GetAwaiter().GetResult();
    }

    /// <summary>The window handle; valid until <see cref="Dispose"/>.</summary>
    public nint Handle { get; private set; }

    /// <summary>Native id of the window thread (for <c>AttachThreadInput</c>/<c>PostThreadMessage</c>).</summary>
    public uint NativeThreadId { get; private set; }

    /// <summary>
    /// Optional message hook, invoked on the window thread for every message except internal ones.
    /// Return a value to mark the message handled, or <see langword="null"/> to fall through to
    /// <c>DefWindowProc</c>. Must be set before messages of interest can arrive.
    /// </summary>
    public Func<uint, nint, nint, nint?>? MessageHandler { get; set; }

    /// <summary>Whether the caller is running on the window thread.</summary>
    public bool IsCurrentThread => Environment.CurrentManagedThreadId == thread.ManagedThreadId;

    /// <summary>
    /// Queues an action to run on the window thread (fire-and-forget; exceptions are logged).
    /// </summary>
    /// <param name="action">The action.</param>
    /// <returns><see langword="false"/> when the window is gone and the action will never run.</returns>
    public bool Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Enqueue(action, null);
    }

    /// <summary>
    /// Runs a function on the window thread and returns its result. Runs inline when already on it.
    /// </summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="func">The function.</param>
    /// <returns>A task with the function's result or exception.</returns>
    /// <exception cref="ObjectDisposedException">The window thread has been shut down.</exception>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        if (IsCurrentThread)
        {
            try
            {
                return Task.FromResult(func());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = Enqueue(
            () =>
            {
                try
                {
                    completion.TrySetResult(func());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            },
            () => completion.TrySetException(new ObjectDisposedException(nameof(MessageWindowThread))));

        return queued ? completion.Task : Task.FromException<T>(new ObjectDisposedException(nameof(MessageWindowThread)));
    }

    /// <summary>
    /// Runs an action on the window thread and completes when it has run.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <returns>A task completing after the action (faulted with its exception).</returns>
    /// <exception cref="ObjectDisposedException">The window thread has been shut down.</exception>
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeAsync(() =>
        {
            action();
            return true;
        });
    }

    /// <summary>
    /// Destroys the window (on its own thread, as Win32 requires) and ends the message loop.
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        // Queue the destroy before flipping the flag so it is accepted; everything queued after it is abandoned.
        pending.Enqueue((() => DestroyWindow(Handle), null));
        PostMessage(Handle, WM_INVOKE, 0, 0);
        disposed = true;
        if (!IsCurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>Thread body: create the window, pump messages, clean up the class.</summary>
    private void Run()
    {
        try
        {
            NativeThreadId = GetCurrentThreadId();
            instance = GetModuleHandle(null);
            wndProc = WindowProcedure;
            className = $"BetterClipboard.{name}.{Guid.NewGuid():N}";
            classNamePointer = Marshal.StringToHGlobalUni(className);
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
                hInstance = instance,
                lpszClassName = classNamePointer,
            };
            if (RegisterClassEx(windowClass) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"RegisterClassEx failed for {name}.");
            }

            Handle = messageOnly
                ? CreateWindowEx(0, className, $"BetterClipboard {name}", 0, 0, 0, 0, 0, HWND_MESSAGE, 0, instance, 0)
                : CreateWindowEx(WS_EX_TOOLWINDOW, className, $"BetterClipboard {name}", WS_POPUP, 0, 0, 0, 0, 0, 0, instance, 0);
            if (Handle == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"CreateWindowEx failed for {name}.");
            }

            ready.TrySetResult();
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
            Cleanup();
            return;
        }

        while (GetMessage(out var message, 0, 0, 0) > 0)
        {
            TranslateMessage(message);
            DispatchMessage(message);
        }

        Cleanup();
    }

    /// <summary>The window procedure; never lets an exception escape into native code.</summary>
    /// <param name="hwnd">Window.</param>
    /// <param name="msg">Message.</param>
    /// <param name="wParam">Parameter.</param>
    /// <param name="lParam">Parameter.</param>
    /// <returns>Message result.</returns>
    private nint WindowProcedure(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == WM_INVOKE)
            {
                while (pending.TryDequeue(out var work))
                {
                    try
                    {
                        work.Run();
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error($"Action on {name} thread failed.", ex);
                    }
                }

                return 0;
            }

            if (msg == WM_DESTROY)
            {
                MessageHandler?.Invoke(msg, wParam, lParam);
                PostQuitMessage(0);
                return 0;
            }

            var handled = MessageHandler?.Invoke(msg, wParam, lParam);
            if (handled.HasValue)
            {
                return handled.Value;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Message 0x{msg:X4} handler on {name} thread failed.", ex);
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Unregisters the window class and frees the pinned class name.</summary>
    private void Cleanup()
    {
        if (className is not null)
        {
            UnregisterClass(className, instance);
        }

        if (classNamePointer != 0)
        {
            Marshal.FreeHGlobal(classNamePointer);
            classNamePointer = 0;
        }

        // Anything still queued can never run now: fault InvokeAsync awaiters rather than leaving them hanging.
        while (pending.TryDequeue(out var work))
        {
            try
            {
                work.Abandon?.Invoke();
            }
            catch (Exception ex)
            {
                AppLog.Error($"Abandoning queued work on {name} thread failed.", ex);
            }
        }
    }

    /// <summary>Queues work and wakes the window thread.</summary>
    /// <param name="run">Runs on the window thread.</param>
    /// <param name="abandon">Runs instead of <paramref name="run"/> if the thread shuts down first; may be <see langword="null"/>.</param>
    /// <returns><see langword="false"/> when the window is already gone.</returns>
    private bool Enqueue(Action run, Action? abandon)
    {
        if (disposed)
        {
            return false;
        }

        pending.Enqueue((run, abandon));
        if (PostMessage(Handle, WM_INVOKE, 0, 0))
        {
            return true;
        }

        // The post can fail if the window died between the check and now; the queued item will be
        // abandoned by Cleanup (or run by a later successful post), never silently lost.
        return !disposed;
    }
}
