using System.ComponentModel;
using System.Runtime.InteropServices;
using BetterClipboard.Core.Diagnostics;

namespace BetterClipboard.App.Interop;

/// <summary>
/// Watches the user resizing a top-level window by its borders, and gives the window a minimum size that follows its
/// monitor's scale, by subclassing it (comctl32 <c>SetWindowSubclass</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why window messages.</b> <c>AppWindow.Changed</c> reports every size change alike — a drag on a border, the app's own
/// <c>MoveAndResize</c>, a move to a monitor with another scale — and during a drag it fires for every mouse move. Only
/// the window's own messages tell a user's resize apart and say when it ended: Windows runs a modal size loop between
/// <c>WM_ENTERSIZEMOVE</c> and <c>WM_EXITSIZEMOVE</c>, and sends <c>WM_SIZING</c> only while that loop resizes (a move
/// loop — Alt+Space › Move — gets the first two, never <c>WM_SIZING</c>). <see cref="UserResized"/> therefore fires once
/// per finished resize, so a caller can save it without writing a file on every mouse move.
/// </para>
/// <para>
/// <b>Why not <c>OverlappedPresenter.PreferredMinimumWidth</c>:</b> it takes raw pixels and keeps them when the window
/// moves to a monitor with another scale (microsoft-ui-xaml issues 10452 and 10475). <c>WM_GETMINMAXINFO</c> asks each
/// time, and the answer is computed from the window's scale at that moment.
/// </para>
/// <para>
/// <b>Lifetime and threading.</b> Create and dispose on the window's thread (subclassing is per thread). The native side
/// holds only a function pointer, so the delegate is kept in a field for as long as the subclass exists; the subclass is
/// removed by <see cref="Dispose"/> or when the window is destroyed (<c>WM_NCDESTROY</c>), whichever comes first. Every
/// callback runs inside the window procedure and is wrapped: an exception is logged and never unwinds into user32,
/// which would crash the process.
/// </para>
/// </remarks>
internal sealed partial class WindowSizeHook : IDisposable
{
    /// <summary><c>WM_GETMINMAXINFO</c>: Windows asks for the size limits (before and during every resize).</summary>
    private const uint WM_GETMINMAXINFO = 0x0024;

    /// <summary><c>WM_NCDESTROY</c>: the last message a window gets; the subclass must go with it.</summary>
    private const uint WM_NCDESTROY = 0x0082;

    /// <summary><c>WM_SIZING</c>: sent while a size loop changes the size (never during a move loop).</summary>
    private const uint WM_SIZING = 0x0214;

    /// <summary><c>WM_ENTERSIZEMOVE</c>: a modal move or size loop starts (the user pressed a border or the caption).</summary>
    private const uint WM_ENTERSIZEMOVE = 0x0231;

    /// <summary><c>WM_EXITSIZEMOVE</c>: the modal move or size loop ended (button released, Enter or Esc).</summary>
    private const uint WM_EXITSIZEMOVE = 0x0232;

    /// <summary>The subclass id, unique per callback and window (one hook per window here).</summary>
    private const nuint SubclassId = 0xB0C5;

    private readonly nint hwnd;
    private readonly Func<double, (int Width, int Height)> minimumSize;

    /// <summary>The window procedure, kept alive here: comctl32 holds only <see cref="procPointer"/>.</summary>
    private readonly SubclassProc proc;
    private readonly nint procPointer;

    /// <summary>Whether the subclass is installed (cleared by <see cref="Remove"/>).</summary>
    private bool installed;

    /// <summary>Whether the current modal loop resized the window (a <c>WM_SIZING</c> came since <c>WM_ENTERSIZEMOVE</c>).</summary>
    private bool resizedInLoop;

    /// <summary>Subclasses <paramref name="hwnd"/>.</summary>
    /// <param name="hwnd">A top-level window of the calling thread.</param>
    /// <param name="minimumSize">
    /// The minimum outer size in physical pixels for a window scale (1.0 = 96 DPI), asked on every
    /// <c>WM_GETMINMAXINFO</c>; it may depend on the caller's state (the flyout adds its groups column while open). Its
    /// answer only ever raises what Windows and the window proposed, never lowers it.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="minimumSize"/> is <see langword="null"/>.</exception>
    /// <exception cref="Win32Exception">The subclass could not be installed (another thread's window, or a destroyed one).</exception>
    public WindowSizeHook(nint hwnd, Func<double, (int Width, int Height)> minimumSize)
    {
        ArgumentNullException.ThrowIfNull(minimumSize);
        this.hwnd = hwnd;
        this.minimumSize = minimumSize;
        proc = WindowProc;
        procPointer = Marshal.GetFunctionPointerForDelegate(proc);
        if (!SetWindowSubclass(hwnd, procPointer, SubclassId, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Subclassing the window failed.");
        }

        installed = true;
    }

    /// <summary>
    /// Raised on the window's thread right after the user finished resizing the window by a border (the modal size loop
    /// ended with <c>WM_EXITSIZEMOVE</c>), once per resize. Not raised for moves, for size changes made by code, or for a
    /// move to a monitor with another scale.
    /// </summary>
    /// <remarks>
    /// Raised from inside the window procedure: keep the handler short (read the new size, save it); anything that could
    /// show UI or pump messages belongs on the dispatcher queue.
    /// </remarks>
    public event EventHandler? UserResized;

    /// <summary>Removes the subclass (idempotent); the window keeps working with its own procedure.</summary>
    public void Dispose() => Remove();

    /// <summary>The window procedure in front of the window's own.</summary>
    /// <param name="window">The window.</param>
    /// <param name="message">The message.</param>
    /// <param name="wParam">Message data.</param>
    /// <param name="lParam">Message data (for <c>WM_GETMINMAXINFO</c>, a <c>MINMAXINFO*</c>).</param>
    /// <param name="id">The subclass id.</param>
    /// <param name="refData">Unused reference data.</param>
    /// <returns>The window's own answer (every message is passed on).</returns>
    private nint WindowProc(nint window, uint message, nint wParam, nint lParam, nuint id, nuint refData)
    {
        if (message == WM_NCDESTROY)
        {
            // The window is going: the subclass must be removed here (comctl32's documented order: remove, then pass the
            // message on), or comctl32 would keep calling a procedure whose owner may be collected.
            Remove();
            return DefSubclassProc(window, message, wParam, lParam);
        }

        // The window's own procedure (WinUI's, then DefWindowProc) answers first: for WM_GETMINMAXINFO it fills in the
        // defaults that are only raised below, never lowered.
        nint result = DefSubclassProc(window, message, wParam, lParam);
        try
        {
            switch (message)
            {
                case WM_GETMINMAXINFO:
                    ApplyMinimum(lParam);
                    break;
                case WM_ENTERSIZEMOVE:
                    resizedInLoop = false;
                    break;
                case WM_SIZING:
                    resizedInLoop = true;
                    break;
                case WM_EXITSIZEMOVE when resizedInLoop:
                    resizedInLoop = false;
                    UserResized?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Handling a window size message failed.", ex);
        }

        return result;
    }

    /// <summary>Raises the minimum track size in a <c>MINMAXINFO</c> to the caller's minimum for the window's scale.</summary>
    /// <param name="lParam">The <c>MINMAXINFO*</c> of the message (null is ignored).</param>
    private unsafe void ApplyMinimum(nint lParam)
    {
        if (lParam == 0)
        {
            return;
        }

        // GetDpiForWindow answers for the monitor the window is on now, also while a DPI change is being applied.
        var (width, height) = minimumSize(WindowInterop.GetScale(hwnd));
        var info = (MINMAXINFO*)lParam;
        info->ptMinTrackSize.X = Math.Max(info->ptMinTrackSize.X, width);
        info->ptMinTrackSize.Y = Math.Max(info->ptMinTrackSize.Y, height);
    }

    /// <summary>Uninstalls the subclass once; failures are logged (the window may already be gone).</summary>
    private void Remove()
    {
        if (!installed)
        {
            return;
        }

        installed = false;
        if (!RemoveWindowSubclass(hwnd, procPointer, SubclassId))
        {
            AppLog.Warn($"Removing the window's size hook failed (error {Marshal.GetLastPInvokeError()}).");
        }
    }

    /// <summary>comctl32's <c>SUBCLASSPROC</c>.</summary>
    /// <param name="window">The window.</param>
    /// <param name="message">The message.</param>
    /// <param name="wParam">Message data.</param>
    /// <param name="lParam">Message data.</param>
    /// <param name="id">The subclass id.</param>
    /// <param name="refData">Reference data given to <c>SetWindowSubclass</c>.</param>
    /// <returns>The message's result.</returns>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint window, uint message, nint wParam, nint lParam, nuint id, nuint refData);

    /// <summary>Installs a subclass procedure (comctl32 5.8+, present in every supported Windows).</summary>
    /// <param name="window">A window of the calling thread.</param>
    /// <param name="subclassProc">The procedure (a function pointer from <see cref="Marshal.GetFunctionPointerForDelegate{TDelegate}(TDelegate)"/>).</param>
    /// <param name="id">The subclass id.</param>
    /// <param name="refData">Reference data passed back to the procedure.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint window, nint subclassProc, nuint id, nuint refData);

    /// <summary>Calls the next procedure in the window's subclass chain (finally the window's own).</summary>
    /// <param name="window">The window.</param>
    /// <param name="message">The message.</param>
    /// <param name="wParam">Message data.</param>
    /// <param name="lParam">Message data.</param>
    /// <returns>The message's result.</returns>
    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);

    /// <summary>Removes a subclass procedure installed by <see cref="SetWindowSubclass"/>.</summary>
    /// <param name="window">The window.</param>
    /// <param name="subclassProc">The procedure's function pointer.</param>
    /// <param name="id">The subclass id.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint window, nint subclassProc, nuint id);

    /// <summary>Win32 <c>POINT</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        /// <summary>Horizontal value.</summary>
        public int X;

        /// <summary>Vertical value.</summary>
        public int Y;
    }

    /// <summary>Win32 <c>MINMAXINFO</c>: the size limits Windows asks for with <c>WM_GETMINMAXINFO</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        /// <summary>Reserved.</summary>
        public POINT ptReserved;

        /// <summary>Maximized size.</summary>
        public POINT ptMaxSize;

        /// <summary>Maximized position.</summary>
        public POINT ptMaxPosition;

        /// <summary>The smallest size the user may drag the window to.</summary>
        public POINT ptMinTrackSize;

        /// <summary>The largest size the user may drag the window to.</summary>
        public POINT ptMaxTrackSize;
    }
}
