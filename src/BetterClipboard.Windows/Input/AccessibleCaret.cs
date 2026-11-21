using System.Diagnostics;
using BetterClipboard.Core.Diagnostics;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Input;

/// <summary>
/// The text caret as Microsoft Active Accessibility (MSAA) reports it (<c>OBJID_CARET</c>), for "Next to the text cursor"
/// in apps that keep no Win32 caret, which <c>GetGUIThreadInfo</c> cannot see: Chromium browsers and apps built on
/// Chromium, Firefox until its accessibility runs, and the classic console (issue #10, CLAUDE.md §2.64).
/// </summary>
/// <remarks>
/// <para><b>Who answers</b> (measured 2026-11-21 in a Windows 11 test desktop, with the reporter's 0.2.11):</para>
/// <list type="bullet">
/// <item>Chromium (Edge 156): its window answers <c>WM_GETOBJECT(OBJID_CARET)</c> with the caret of the field being typed in,
/// a textarea, an input, a contenteditable box or the address bar alike, 1 pixel wide. With no text field focused it
/// reports the last spot 0 wide and flagged invisible. The query turned on none of Chromium's web accessibility
/// (<c>edge://accessibility</c> kept "Web accessibility" off), unlike a UI Automation client, whose first question did.</item>
/// <item>Firefox: the right caret, flagged invisible. The first query starts Firefox's accessibility service, after which
/// Firefox also keeps a hidden Win32 caret that <c>GetGUIThreadInfo</c> finds.</item>
/// <item>The classic console (conhost): the cursor cell, flagged invisible (conhost draws its own cursor).
/// <c>GetGUIThreadInfo</c> cannot even be asked there: a console window reports cmd.exe's thread, not conhost's.</item>
/// <item>Windows Terminal and XAML/WinUI text boxes (Explorer's address bar, the Settings app): oleacc's empty caret object,
/// all zeros. The panel goes next to the mouse there, as before; only UI Automation sees those carets.</item>
/// </list>
/// <para>
/// <b>So the size decides, not the state:</b> a caret 0 wide or 0 high is none (Chromium's hidden one, oleacc's empty one),
/// and a real caret flagged invisible (Firefox's, the console's) counts. It must also lie on the window it was asked for:
/// a stale caret elsewhere would put the panel far from where the user types.
/// </para>
/// <para>
/// <b>A deadline, on a thread of its own.</b> The query sends the app <c>WM_GETOBJECT</c> and makes a cross-process COM call
/// into it, and both wait for the app's UI thread: a hung app would hold the summon. So it runs on its own MTA thread and
/// the caller waits at most the deadline (<see cref="DefaultDeadline"/> has the measured times). A lookup that outlives its
/// deadline is left to finish, and until it has, no other lookup starts: a hung app would otherwise collect one stuck
/// thread per <c>Win+V</c>. The panel then goes next to the mouse.
/// </para>
/// <para>
/// <b>Who waits.</b> The UI thread that opens the panel (<see cref="ForegroundContext.WithAccessibleCaret"/>), where .NET's
/// own waits pump messages and WinUI could re-enter: the wait is a plain <c>WaitForSingleObject</c>. Never the shortcut's
/// input thread, which also runs the keyboard hook: a hook's callback cannot run while its thread waits, so every key on the
/// PC would wait for the lookup, and Windows silently removes a hook that keeps it waiting too long. The first version
/// waited there (found the same day, before it shipped). The lookup's thread joins the multithreaded apartment so that no
/// wait pumps: an STA thread pumps while a COM call waits.
/// </para>
/// </remarks>
public static class AccessibleCaret
{
    /// <summary>
    /// How long a summon waits for an app's MSAA caret before the panel goes next to the mouse: 250 ms. Measured in a test
    /// desktop without a GPU (2026-11-21), the first lookup into a process takes longest, while COM connects the two
    /// processes: Chrome 70 and 107 ms (twice it missed a 100-ms deadline), VS Code 144 ms, Edge 20–45 ms, and Firefox 10 ms,
    /// which starts its accessibility then. Later lookups took 1–10 ms.
    /// </summary>
    /// <remarks>
    /// The price of a longer wait falls only on an app that answers late or never. A hung app holds one summon by the whole
    /// deadline, and the summons after it go straight to the mouse until it answers (one lookup at a time).
    /// </remarks>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromMilliseconds(250);

    /// <summary>1 while a lookup's thread runs, which may be after its caller gave up; 0 otherwise.</summary>
    private static int busy;

    /// <summary>1 once the first caret found through MSAA was logged (one line per session, not one per summon).</summary>
    private static int loggedFound;

    /// <summary>1 once a lookup that outlived its deadline was logged.</summary>
    private static int loggedLate;

    /// <summary>1 once a lookup that failed was logged.</summary>
    private static int loggedFailure;

    /// <summary>
    /// Whether a lookup's thread is still running (tests: a lookup left behind by its deadline ends once the app answers).
    /// </summary>
    internal static bool IsBusy => Volatile.Read(ref busy) != 0;

    /// <summary>
    /// Asks <paramref name="window"/> for its MSAA caret and waits for the answer at most <paramref name="deadline"/>.
    /// </summary>
    /// <remarks>
    /// Callers ask only when <c>GetGUIThreadInfo</c> found no Win32 caret: for an app that has one, the answer would be the
    /// same rectangle, slower. Blocks its caller without pumping messages for up to <paramref name="deadline"/>: never call
    /// it on the shortcut's input thread (class remarks, "Who waits"). Footgun: asking a window of this process from a thread
    /// that window's thread waits for would stall until the deadline (<see cref="ForegroundContext.Capture"/> never records
    /// this process's windows).
    /// </remarks>
    /// <param name="window">The window to ask: the focus window of the window in front, else the window in front.</param>
    /// <param name="within">The bounds of the window in front, in physical screen pixels; a caret outside them is none.</param>
    /// <param name="deadline">The longest wait.</param>
    /// <returns>
    /// The caret in physical screen pixels, or <see langword="null"/> when the window reports none, reports one that is
    /// hidden or off the window, answers too late, or another lookup is still running.
    /// </returns>
    public static ScreenRect? Read(nint window, ScreenRect within, TimeSpan deadline)
    {
        if (window == 0 || within.Width <= 0 || within.Height <= 0)
        {
            return null;
        }

        // One lookup at a time: a previous one still running means an app stopped answering; this summon goes next to the
        // mouse rather than leaving one more thread stuck in that app.
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
        {
            return null;
        }

        var lookup = new Lookup(window, within);
        var watch = Stopwatch.StartNew();
        try
        {
            new Thread(lookup.Run) { IsBackground = true, Name = "BetterClipboard.CaretLookup" }.Start();
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ThreadStartException)
        {
            // The thread never ran, so neither its finally (which frees the gate) nor its share of the event will come.
            Volatile.Write(ref busy, 0);
            lookup.Abandon();
            LogOnce(ref loggedFailure, () => AppLog.Warn($"Could not start the lookup of a window's accessibility caret ({ex.GetType().Name}: {ex.Message})."));
            return null;
        }

        if (!lookup.Wait(deadline))
        {
            LogOnce(ref loggedLate, () => AppLog.Info(
                $"A window did not report its accessibility caret within {deadline.TotalMilliseconds:0} ms; the panel opened next to the mouse (logged once a session)."));
            return null;
        }

        if (lookup.Failure is { } failure)
        {
            LogOnce(ref loggedFailure, () => AppLog.Warn($"Reading a window's accessibility caret failed ({failure.GetType().Name}: {failure.Message}); the panel opened next to the mouse."));
            return null;
        }

        if (lookup.Result is not null)
        {
            LogOnce(ref loggedFound, () => AppLog.Info(
                $"Next to the text cursor: found the caret of a window that keeps no Win32 caret through accessibility (MSAA) in {watch.Elapsed.TotalMilliseconds:0.0} ms (logged once a session)."));
        }

        return lookup.Result;
    }

    /// <summary>
    /// The rule for what <c>accLocation</c> reported: a rectangle with a size, on the window it was asked for.
    /// </summary>
    /// <remarks>
    /// The MSAA state is not read: Chromium hides its caret object by making it 0 wide (it also flags it invisible), oleacc's
    /// object for a window without a caret is all zeros, but Firefox and the classic console flag their real caret invisible
    /// too. Arithmetic is checked: a lying object's huge numbers must not wrap into a rectangle that happens to fit.
    /// </remarks>
    /// <param name="left">Reported left, in physical screen pixels.</param>
    /// <param name="top">Reported top.</param>
    /// <param name="width">Reported width.</param>
    /// <param name="height">Reported height.</param>
    /// <param name="within">The bounds of the window in front.</param>
    /// <returns>The caret, or <see langword="null"/> when the report is no caret.</returns>
    internal static ScreenRect? Accept(int left, int top, int width, int height, ScreenRect within)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        long right = (long)left + width;
        long bottom = (long)top + height;
        if (right > int.MaxValue || bottom > int.MaxValue)
        {
            return null;
        }

        var caret = new ScreenRect(left, top, (int)right, (int)bottom);
        return caret.Intersects(within) ? caret : null;
    }

    /// <summary>
    /// The lookup itself, on the lookup's own thread: the window's report (<see cref="Locate"/>), judged by
    /// <see cref="Accept"/>.
    /// </summary>
    /// <param name="window">The window to ask.</param>
    /// <param name="within">The bounds of the window in front.</param>
    /// <returns>The caret, or <see langword="null"/> (see <see cref="Read"/>).</returns>
    private static ScreenRect? Query(nint window, ScreenRect within) =>
        Locate(window) is { } report ? Accept(report.Left, report.Top, report.Width, report.Height, within) : null;

    /// <summary>
    /// What a window reports for its MSAA caret, as it reports it: the caret object's <c>accLocation</c>, unjudged.
    /// </summary>
    /// <remarks>
    /// Waits for the window's thread, with no deadline: call it only on a thread that may wait (the lookup's own; tests).
    /// Initializes COM on the calling thread for the call (multithreaded apartment) and balances it.
    /// </remarks>
    /// <param name="window">The window to ask.</param>
    /// <returns>
    /// The reported rectangle (left, top, width, height in physical screen pixels, any of them 0), or <see langword="null"/>
    /// when the window has no caret object or <c>accLocation</c> failed.
    /// </returns>
    internal static unsafe (int Left, int Top, int Width, int Height)? Locate(nint window)
    {
        // This thread's own apartment, multithreaded: an STA would pump messages while the cross-process call waits, and
        // oleacc needs COM initialized. A negative result (the thread was an STA after all) needs no CoUninitialize.
        int init = CoInitializeEx(0, COINIT_MULTITHREADED);
        try
        {
            if (AccessibleObjectFromWindow(window, OBJID_CARET, in IID_IAccessible, out nint accessible) < 0 || accessible == 0)
            {
                return null;
            }

            // Called through the vtable: IAccessible is an IDispatch interface the source-generated COM interop does not
            // take, and only two of its methods are needed.
            nint* vtable = *(nint**)accessible;
            try
            {
                int left = 0, top = 0, width = 0, height = 0;
                var accLocation = (delegate* unmanaged[Stdcall]<nint, int*, int*, int*, int*, VARIANT_I4, int>)vtable[IAccessibleAccLocationSlot];
                int hr = accLocation(accessible, &left, &top, &width, &height, VARIANT_I4.ChildSelf);
                return hr < 0 ? null : (left, top, width, height);
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, uint>)vtable[IUnknownReleaseSlot])(accessible);
            }
        }
        finally
        {
            if (init >= 0)
            {
                CoUninitialize();
            }
        }
    }

    /// <summary>Writes a log line the first time only (one per session, whichever thread gets there first).</summary>
    /// <param name="flag">The flag that records it was written.</param>
    /// <param name="log">Writes the line.</param>
    private static void LogOnce(ref int flag, Action log)
    {
        if (Interlocked.Exchange(ref flag, 1) == 0)
        {
            log();
        }
    }

    /// <summary>
    /// One lookup: what the lookup's thread hands back, and the event its caller waits on, closed by whichever of the two
    /// is done with it last (the caller may give up first and the thread finish long after).
    /// </summary>
    /// <param name="window">The window to ask.</param>
    /// <param name="within">The bounds of the window in front.</param>
    private sealed class Lookup(nint window, ScreenRect within)
    {
        /// <summary>Set by the lookup's thread when it is done, whatever the outcome.</summary>
        private readonly ManualResetEvent done = new(false);

        /// <summary>The caller and the lookup's thread; the event is closed when both let go.</summary>
        private int holders = 2;

        /// <summary>The caret found; read by the caller only after <see cref="Wait"/> returned <see langword="true"/>.</summary>
        public ScreenRect? Result { get; private set; }

        /// <summary>What the lookup threw, if anything; read like <see cref="Result"/>.</summary>
        public Exception? Failure { get; private set; }

        /// <summary>The lookup's thread body: never throws, always frees the gate and signals the caller.</summary>
        public void Run()
        {
            try
            {
                Result = Query(window, within);
            }
            catch (Exception ex)
            {
                Failure = ex;
            }
            finally
            {
                // The gate first: a caller woken by the event may start the next lookup at once.
                Volatile.Write(ref busy, 0);
                done.Set();
                LetGo();
            }
        }

        /// <summary>Waits for the lookup's thread without pumping messages, then lets go of the event.</summary>
        /// <param name="deadline">The longest wait.</param>
        /// <returns><see langword="true"/> when the thread finished in time.</returns>
        public bool Wait(TimeSpan deadline)
        {
            try
            {
                // WaitForSingleObject, not WaitOne: .NET's waits pump messages on an STA thread such as the UI thread.
                uint milliseconds = (uint)Math.Clamp(deadline.TotalMilliseconds, 0, uint.MaxValue - 1);
                return WaitForSingleObject(done.SafeWaitHandle.DangerousGetHandle(), milliseconds) == WAIT_OBJECT_0;
            }
            finally
            {
                LetGo();
            }
        }

        /// <summary>Lets go of both shares when the thread could not be started.</summary>
        public void Abandon()
        {
            LetGo();
            LetGo();
        }

        /// <summary>Lets go of one share; the last one closes the event.</summary>
        private void LetGo()
        {
            if (Interlocked.Decrement(ref holders) == 0)
            {
                done.Dispose();
            }
        }
    }
}
