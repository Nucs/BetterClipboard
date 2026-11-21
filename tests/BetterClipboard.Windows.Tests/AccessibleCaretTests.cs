using System.Diagnostics;
using System.Runtime.InteropServices;
using BetterClipboard.Windows.Input;
using BetterClipboard.Windows.Interop;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// "Next to the text cursor" in apps without a Win32 caret (issue #10, CLAUDE.md §2.64): what <see cref="AccessibleCaret"/>
/// accepts of an MSAA caret report, and real lookups through oleacc against a window of this process that answers
/// <c>WM_GETOBJECT(OBJID_CARET)</c> itself, the way Chromium does — which also proves the <c>IAccessible</c> vtable slot
/// and the VARIANT passed by value — with the deadline for a window whose thread does not answer, and what a summon's
/// lookup (<see cref="ForegroundContext.WithAccessibleCaret"/>) asks and keeps.
/// </summary>
/// <remarks>
/// <para>
/// The window answers with oleacc's standard object for its client area, whose location is the window's client
/// rectangle: a stand-in for a caret object that needs no COM server of the test's own.
/// </para>
/// <para>
/// Not here: oleacc's own caret object, which reads the Win32 caret of the thread in front only — measured: a test
/// window with a blinking caret at (10, 15, 12, 35) but not in front got S_FALSE and zeros. Making a test window the
/// foreground would take the keyboard from whoever uses the PC; the console and Notepad, read through that object while
/// in front, were checked in a test desktop instead (CLAUDE.md §5).
/// </para>
/// </remarks>
public sealed class AccessibleCaretTests
{
    /// <summary><c>WM_GETOBJECT</c>: oleacc asks a window for an accessibility object.</summary>
    private const uint WM_GETOBJECT = 0x003D;

    /// <summary><c>OBJID_CLIENT</c>: a window's client area.</summary>
    private const int OBJID_CLIENT = -4;

    /// <summary><c>SWP_NOZORDER</c> | <c>SWP_NOACTIVATE</c>: moves and sizes a window without showing or activating it.</summary>
    private const uint MoveQuietly = 0x0004 | 0x0010;

    /// <summary>Edge's window in the test desktop (1600 × 900), where the reports below were measured.</summary>
    private static readonly ScreenRect EdgeWindow = new(17, 10, 1053, 835);

    /// <summary>Bounds every test window lies in.</summary>
    private static readonly ScreenRect Everywhere = new(-10_000, -10_000, 10_000, 10_000);

    /// <summary>
    /// A report is a caret when it has a width and a height and lies on the window, whatever its MSAA state: Chromium's caret
    /// in a field, Firefox's and the console's (both flagged invisible) count; Chromium's caret with no field focused (0
    /// wide), oleacc's empty object (all zeros), a negative size, a caret off the window and numbers that overflow do not.
    /// </summary>
    /// <param name="left">Reported left.</param>
    /// <param name="top">Reported top.</param>
    /// <param name="width">Reported width.</param>
    /// <param name="height">Reported height.</param>
    /// <param name="accepted">Whether it is a caret.</param>
    [Theory]
    [MemberData(nameof(Reports))]
    public void Accept_TakesOnlyACaretWithASizeOnTheWindow(int left, int top, int width, int height, bool accepted)
    {
        var caret = AccessibleCaret.Accept(left, top, width, height, EdgeWindow);
        if (accepted)
        {
            Assert.Equal(new ScreenRect(left, top, left + width, top + height), caret);
        }
        else
        {
            Assert.Null(caret);
        }
    }

    /// <summary>The cases of <see cref="Accept_TakesOnlyACaretWithASizeOnTheWindow"/>: left, top, width, height, accepted.</summary>
    public static TheoryData<int, int, int, int, bool> Reports => new()
    {
        { 185, 210, 1, 24, true },              // Edge, a textarea
        { 246, 59, 1, 20, true },               // Edge, its address bar
        { 185, 210, 0, 24, false },             // Edge with no text field focused: hidden, 0 wide
        { 0, 0, 0, 0, false },                  // oleacc's object for a window without a caret
        { 177, 210, 1, 24, true },              // Firefox (flagged invisible, but real)
        { 478, 141, 8, 16, true },              // the classic console's cursor cell
        { 185, 210, -1, 24, false },            // a negative size
        { 185, 210, 1, 0, false },              // no height
        { 1500, 800, 1, 24, false },            // off the window: a stale caret elsewhere
        { 1053, 400, 1, 24, false },            // just past the window's right edge
        { 1052, 400, 1, 24, true },             // its last column
        { int.MaxValue - 5, 0, 10, 10, false }, // overflow
        { 0, int.MaxValue - 5, 10, 10, false },
    };

    /// <summary>A window on a monitor left of the main one has negative coordinates; its caret counts like any other.</summary>
    [Fact]
    public void Accept_NegativeCoordinates_OnALeftMonitor() =>
        Assert.Equal(new ScreenRect(-1500, 300, -1499, 320), AccessibleCaret.Accept(-1500, 300, 1, 20, new ScreenRect(-1920, 6, 0, 1038)));

    /// <summary>
    /// A real lookup: a window that answers <c>OBJID_CARET</c> itself gets its answer read through oleacc and the vtable
    /// (<see cref="AccessibleCaret.Locate"/> reports it as is, <see cref="AccessibleCaret.Read"/> judges it); a window that
    /// does not answer gets oleacc's own caret object, which reports nothing for a thread not in front; and bounds the
    /// caret is not in make it none.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Read_ReportsWhatTheWindowAnswers()
    {
        using var window = await AnsweringWindowAsync();
        window.Answer = false;
        Assert.Equal<(int, int, int, int)?>((0, 0, 0, 0), AccessibleCaret.Locate(window.Thread.Handle));
        Assert.Null(AccessibleCaret.Read(window.Thread.Handle, Everywhere, TimeSpan.FromSeconds(5)));

        window.Answer = true;
        Assert.Equal<(int, int, int, int)?>((100, 100, 300, 200), AccessibleCaret.Locate(window.Thread.Handle));
        Assert.Equal(new ScreenRect(100, 100, 400, 300), AccessibleCaret.Read(window.Thread.Handle, Everywhere, TimeSpan.FromSeconds(5)));

        // The same report with bounds of a window elsewhere: a stale caret, none.
        Assert.Null(AccessibleCaret.Read(window.Thread.Handle, new ScreenRect(1000, 1000, 1400, 1300), TimeSpan.FromSeconds(5)));
        Assert.Null(AccessibleCaret.Read(0, Everywhere, TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// A window whose thread does not answer holds the summon no longer than the deadline, and no second lookup starts
    /// while the first one waits: the next ask gives up at once. Once the thread answers, the lookup left behind ends and
    /// the next one gets the caret.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Read_AWindowThatDoesNotAnswer_GivesUpAtTheDeadline()
    {
        using var window = await AnsweringWindowAsync();
        using var gate = new ManualResetEventSlim(false);
        using var blocked = new ManualResetEventSlim(false);

        // The window's thread waits inside a message instead of pumping: WM_GETOBJECT cannot reach it.
        window.Thread.Post(() =>
        {
            blocked.Set();
            gate.Wait(TimeSpan.FromSeconds(30));
        });
        Assert.True(blocked.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        try
        {
            var watch = Stopwatch.StartNew();
            Assert.Null(AccessibleCaret.Read(window.Thread.Handle, Everywhere, TimeSpan.FromMilliseconds(150)));
            Assert.InRange(watch.ElapsedMilliseconds, 140, 3000);
            Assert.True(AccessibleCaret.IsBusy);

            watch.Restart();
            Assert.Null(AccessibleCaret.Read(window.Thread.Handle, Everywhere, TimeSpan.FromSeconds(5)));
            Assert.InRange(watch.ElapsedMilliseconds, 0, 1000);
        }
        finally
        {
            gate.Set();
        }

        // The lookup left behind finishes once the thread answers, and frees the gate for the next one.
        Assert.True(SpinWait.SpinUntil(() => !AccessibleCaret.IsBusy, TimeSpan.FromSeconds(10)));
        Assert.Equal(new ScreenRect(100, 100, 400, 300), AccessibleCaret.Read(window.Thread.Handle, Everywhere, TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// A summon's lookup (<see cref="ForegroundContext.WithAccessibleCaret"/>): a context with a Win32 caret, or with nothing
    /// recorded to ask, comes back as it is; a context recording a window to ask gets that window's caret and keeps the rest
    /// of what was captured, with no query left, so asking again asks nothing; a window that reports no caret leaves none.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task WithAccessibleCaret_AsksOnlyWhatCaptureRecorded()
    {
        using var window = await AnsweringWindowAsync();
        var cursor = new ScreenPoint(5, 5);
        var query = new AccessibleCaretQuery(window.Thread.Handle, Everywhere);

        // A Win32 caret was found: the query is never made.
        var withCaret = new ForegroundContext(window.Thread.Handle, 1, new ScreenRect(1, 2, 3, 4), cursor) { CaretQuery = query };
        Assert.Same(withCaret, withCaret.WithAccessibleCaret(TimeSpan.FromSeconds(5)));

        // Nothing recorded: a context from the tray, Start or Search, a window of this process.
        var nothing = new ForegroundContext(window.Thread.Handle, 1, null, cursor);
        Assert.Same(nothing, nothing.WithAccessibleCaret(TimeSpan.FromSeconds(5)));

        var asked = new ForegroundContext(window.Thread.Handle, 1, null, cursor) { CaretQuery = query, CapturedAt = 42 };
        var found = asked.WithAccessibleCaret(TimeSpan.FromSeconds(5));
        Assert.Equal(new ScreenRect(100, 100, 400, 300), found.Caret);
        Assert.Null(found.CaretQuery);
        Assert.Equal(42, found.CapturedAt);
        Assert.Equal(cursor, found.Cursor);
        Assert.Same(found, found.WithAccessibleCaret(TimeSpan.FromSeconds(5)));

        window.Answer = false;
        var none = asked.WithAccessibleCaret(TimeSpan.FromSeconds(5));
        Assert.Null(none.Caret);
        Assert.Null(none.CaretQuery);
        Assert.Same(none, none.WithAccessibleCaret(TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// A hidden window at (100, 100), 300 × 200, that answers <c>WM_GETOBJECT(OBJID_CARET)</c> with oleacc's standard object
    /// for its client area while <see cref="AnsweringWindow.Answer"/> is on.
    /// </summary>
    /// <returns>The window; never shown or activated.</returns>
    private static async Task<AnsweringWindow> AnsweringWindowAsync()
    {
        var window = new AnsweringWindow(new MessageWindowThread("Accessible caret test", messageOnly: false));
        window.Thread.MessageHandler = window.OnMessage;
        await window.Thread.InvokeAsync(() =>
        {
            // COM for LresultFromObject, which marshals the answer into a stream on this thread; the thread ends with the
            // window, so the apartment is never left.
            Assert.True(CoInitializeEx(0, COINIT_MULTITHREADED) >= 0);
            Assert.True(SetWindowPos(window.Thread.Handle, 0, 100, 100, 300, 200, MoveQuietly));
        });
        return window;
    }

    /// <summary>Moves and sizes a window (here: hidden, without activating it).</summary>
    /// <param name="hWnd">The window.</param>
    /// <param name="hWndInsertAfter">Z-order anchor; ignored with <c>SWP_NOZORDER</c>.</param>
    /// <param name="x">Left.</param>
    /// <param name="y">Top.</param>
    /// <param name="cx">Width.</param>
    /// <param name="cy">Height.</param>
    /// <param name="uFlags"><c>SWP_*</c> flags.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    /// <summary>oleacc's standard accessibility object for a part of a window.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="idObject">The part (<see cref="OBJID_CLIENT"/>).</param>
    /// <param name="riid">The interface.</param>
    /// <param name="ppvObject">Receives the interface pointer (one reference).</param>
    /// <returns>An HRESULT.</returns>
    [DllImport("oleacc.dll")]
    private static extern int CreateStdAccessibleObject(nint hwnd, int idObject, ref Guid riid, out nint ppvObject);

    /// <summary>Packs an interface pointer into a <c>WM_GETOBJECT</c> answer (it takes its own reference).</summary>
    /// <param name="riid">The interface.</param>
    /// <param name="wParam">The message's <c>wParam</c>.</param>
    /// <param name="punk">The interface pointer.</param>
    /// <returns>The answer, or a negative HRESULT.</returns>
    [DllImport("oleacc.dll")]
    private static extern nint LresultFromObject(ref Guid riid, nint wParam, nint punk);

    /// <summary>A test window that answers <c>OBJID_CARET</c> itself, and its thread.</summary>
    /// <param name="thread">The window's thread.</param>
    private sealed class AnsweringWindow(MessageWindowThread thread) : IDisposable
    {
        /// <summary>The window's thread (and through it the window).</summary>
        public MessageWindowThread Thread { get; } = thread;

        /// <summary>Backing field of <see cref="Answer"/>: written by the test, read on the window's thread.</summary>
        private volatile bool answer = true;

        /// <summary>Whether the window answers <c>OBJID_CARET</c>; off, oleacc's own caret object answers for it.</summary>
        public bool Answer
        {
            get => answer;
            set => answer = value;
        }

        /// <summary>Answers <c>WM_GETOBJECT(OBJID_CARET)</c> while <see cref="Answer"/> is on (the window's thread).</summary>
        /// <param name="msg">The message.</param>
        /// <param name="wParam">Its wParam.</param>
        /// <param name="lParam">Its lParam: the object id in its low 32 bits.</param>
        /// <returns>The answer, or <see langword="null"/> to let the window's default procedure answer.</returns>
        public nint? OnMessage(uint msg, nint wParam, nint lParam)
        {
            if (msg != WM_GETOBJECT || unchecked((uint)lParam) != OBJID_CARET || !Answer)
            {
                return null;
            }

            Guid iid = IID_IAccessible;
            if (CreateStdAccessibleObject(Thread.Handle, OBJID_CLIENT, ref iid, out nint client) < 0 || client == 0)
            {
                return null;
            }

            try
            {
                return LresultFromObject(ref iid, wParam, client);
            }
            finally
            {
                Marshal.Release(client);
            }
        }

        /// <summary>Destroys the window and ends its thread.</summary>
        public void Dispose() => Thread.Dispose();
    }
}
