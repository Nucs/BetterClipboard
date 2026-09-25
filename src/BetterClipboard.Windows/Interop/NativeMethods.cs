using System.Runtime.InteropServices;

namespace BetterClipboard.Windows.Interop;

/// <summary>
/// Win32 declarations used by BetterClipboard, via source-generated <see cref="LibraryImportAttribute"/>
/// stubs (no runtime marshalling IL, trimming/AOT friendly).
/// </summary>
/// <remarks>
/// Conventions: every function whose failure is diagnosed with <c>GetLastError</c> sets
/// <c>SetLastError = true</c> — read it immediately with <see cref="Marshal.GetLastPInvokeError"/>, before any
/// other P/Invoke clobbers it. Handles are plain <see cref="nint"/>; ownership is documented at each
/// call site (clipboard HGLOBALs, for instance, belong to the system after a successful
/// <see cref="SetClipboardData"/> and must then never be freed by us).
/// </remarks>
internal static partial class NativeMethods
{
    // ───── Window messages ─────

    /// <summary>Sent to a window being destroyed.</summary>
    internal const uint WM_DESTROY = 0x0002;

    /// <summary>No-op message; posted after <c>TrackPopupMenuEx</c> so the menu dismisses correctly (KB135788).</summary>
    internal const uint WM_NULL = 0x0000;

    /// <summary>Timer tick for <see cref="SetTimer"/> timers without a callback.</summary>
    internal const uint WM_TIMER = 0x0113;

    /// <summary>A <see cref="RegisterHotKey"/> hotkey was pressed; wParam = hotkey id.</summary>
    internal const uint WM_HOTKEY = 0x0312;

    /// <summary>Clipboard content changed (sent to windows registered with <see cref="AddClipboardFormatListener"/>).</summary>
    internal const uint WM_CLIPBOARDUPDATE = 0x031D;

    /// <summary>Tray icon context-menu request (NOTIFYICON_VERSION_4 callback event).</summary>
    internal const uint WM_CONTEXTMENU = 0x007B;

    /// <summary>Base of the private application message range.</summary>
    internal const uint WM_APP = 0x8000;

    /// <summary>Base of the <c>WM_USER</c> range; tray <c>NIN_*</c> events live here.</summary>
    internal const uint WM_USER = 0x0400;

    /// <summary>Key pressed (low-level hook wParam).</summary>
    internal const uint WM_KEYDOWN = 0x0100;

    /// <summary>Key released (low-level hook wParam).</summary>
    internal const uint WM_KEYUP = 0x0101;

    /// <summary>Key pressed while Alt is held, or F10 (low-level hook wParam).</summary>
    internal const uint WM_SYSKEYDOWN = 0x0104;

    /// <summary>Key released while Alt is held (low-level hook wParam).</summary>
    internal const uint WM_SYSKEYUP = 0x0105;

    /// <summary>Parent value that creates a message-only window (no UI, no broadcasts, invisible to enumeration).</summary>
    internal static readonly nint HWND_MESSAGE = -3;

    // ───── Window management ─────

    /// <summary>Window procedure signature; kept alive by its owner for the window's whole lifetime.</summary>
    /// <param name="hwnd">Target window.</param>
    /// <param name="msg">Message id.</param>
    /// <param name="wParam">Message parameter.</param>
    /// <param name="lParam">Message parameter.</param>
    /// <returns>Message-specific result.</returns>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    /// <summary>Window class registration data (<c>WNDCLASSEXW</c>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WNDCLASSEXW
    {
        /// <summary>Must be <c>sizeof(WNDCLASSEXW)</c>.</summary>
        public uint cbSize;
        /// <summary>Class styles (<c>CS_*</c>).</summary>
        public uint style;
        /// <summary>Function pointer to the window procedure.</summary>
        public nint lpfnWndProc;
        /// <summary>Extra class bytes.</summary>
        public int cbClsExtra;
        /// <summary>Extra window bytes.</summary>
        public int cbWndExtra;
        /// <summary>Module instance that owns the class.</summary>
        public nint hInstance;
        /// <summary>Class icon.</summary>
        public nint hIcon;
        /// <summary>Class cursor.</summary>
        public nint hCursor;
        /// <summary>Background brush.</summary>
        public nint hbrBackground;
        /// <summary>Menu resource name.</summary>
        public nint lpszMenuName;
        /// <summary>Pointer to the NUL-terminated UTF-16 class name (must stay valid while registered).</summary>
        public nint lpszClassName;
        /// <summary>Small class icon.</summary>
        public nint hIconSm;
    }

    /// <summary>A queued message (<c>MSG</c>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        /// <summary>Target window.</summary>
        public nint hwnd;
        /// <summary>Message id.</summary>
        public uint message;
        /// <summary>Parameter.</summary>
        public nint wParam;
        /// <summary>Parameter.</summary>
        public nint lParam;
        /// <summary>Post time.</summary>
        public uint time;
        /// <summary>Cursor position at post time.</summary>
        public POINT pt;
        /// <summary>Private.</summary>
        public uint lPrivate;
    }

    /// <summary>A screen or client point.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        /// <summary>X coordinate.</summary>
        public int X;
        /// <summary>Y coordinate.</summary>
        public int Y;
    }

    /// <summary>A rectangle (exclusive right/bottom).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        /// <summary>Left edge.</summary>
        public int Left;
        /// <summary>Top edge.</summary>
        public int Top;
        /// <summary>Right edge (exclusive).</summary>
        public int Right;
        /// <summary>Bottom edge (exclusive).</summary>
        public int Bottom;
    }

    /// <summary>Registers a window class.</summary>
    /// <param name="lpwcx">Class description.</param>
    /// <returns>The class atom, or 0 on failure (see last error).</returns>
    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    internal static partial ushort RegisterClassEx(in WNDCLASSEXW lpwcx);

    /// <summary>Unregisters a window class (all its windows must be destroyed first).</summary>
    /// <param name="lpClassName">Class name.</param>
    /// <param name="hInstance">Owning module.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterClass(string lpClassName, nint hInstance);

    /// <summary>Creates a window.</summary>
    /// <param name="dwExStyle">Extended style.</param>
    /// <param name="lpClassName">Registered class name.</param>
    /// <param name="lpWindowName">Window title.</param>
    /// <param name="dwStyle">Style.</param>
    /// <param name="x">X.</param>
    /// <param name="y">Y.</param>
    /// <param name="nWidth">Width.</param>
    /// <param name="nHeight">Height.</param>
    /// <param name="hWndParent">Parent, or <see cref="HWND_MESSAGE"/> for a message-only window.</param>
    /// <param name="hMenu">Menu or child id.</param>
    /// <param name="hInstance">Module instance.</param>
    /// <param name="lpParam">Creation parameter.</param>
    /// <returns>The window handle, or 0 on failure.</returns>
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    /// <summary>Destroys a window; must be called on the window's own thread.</summary>
    /// <param name="hWnd">The window.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint hWnd);

    /// <summary>Default message processing.</summary>
    /// <param name="hWnd">Window.</param>
    /// <param name="msg">Message.</param>
    /// <param name="wParam">Parameter.</param>
    /// <param name="lParam">Parameter.</param>
    /// <returns>Message result.</returns>
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static partial nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary>Retrieves the next message, blocking until one arrives.</summary>
    /// <param name="lpMsg">Receives the message.</param>
    /// <param name="hWnd">Window filter (0 = any on this thread).</param>
    /// <param name="wMsgFilterMin">Min message filter.</param>
    /// <param name="wMsgFilterMax">Max message filter.</param>
    /// <returns>&gt;0 for a message, 0 for <c>WM_QUIT</c>, -1 on error.</returns>
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    internal static partial int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    /// <summary>Translates virtual-key messages into character messages.</summary>
    /// <param name="lpMsg">The message.</param>
    /// <returns>Whether a character message was produced.</returns>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(in MSG lpMsg);

    /// <summary>Dispatches a message to its window procedure.</summary>
    /// <param name="lpMsg">The message.</param>
    /// <returns>The window procedure's result.</returns>
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static partial nint DispatchMessage(in MSG lpMsg);

    /// <summary>Posts a message to a window's queue without waiting.</summary>
    /// <param name="hWnd">Target window.</param>
    /// <param name="msg">Message.</param>
    /// <param name="wParam">Parameter.</param>
    /// <param name="lParam">Parameter.</param>
    /// <returns><see langword="true"/> when queued.</returns>
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary>Posts <c>WM_QUIT</c> to the calling thread, ending its <see cref="GetMessage"/> loop.</summary>
    /// <param name="nExitCode">Exit code carried in wParam.</param>
    [LibraryImport("user32.dll")]
    internal static partial void PostQuitMessage(int nExitCode);

    /// <summary>Creates or resets a timer that posts <see cref="WM_TIMER"/>.</summary>
    /// <param name="hWnd">Window receiving the ticks.</param>
    /// <param name="nIDEvent">Timer id (re-using an id resets that timer).</param>
    /// <param name="uElapse">Interval in ms.</param>
    /// <param name="lpTimerFunc">Callback (0 = post WM_TIMER).</param>
    /// <returns>Non-zero on success.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nuint SetTimer(nint hWnd, nuint nIDEvent, uint uElapse, nint lpTimerFunc);

    /// <summary>Destroys a timer.</summary>
    /// <param name="hWnd">Owning window.</param>
    /// <param name="uIDEvent">Timer id.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool KillTimer(nint hWnd, nuint uIDEvent);

    /// <summary>Registers (or looks up) a system-wide message id by name, e.g. <c>TaskbarCreated</c>.</summary>
    /// <param name="lpString">Message name.</param>
    /// <returns>The message id (0xC000–0xFFFF), or 0 on failure.</returns>
    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterWindowMessage(string lpString);

    /// <summary>Whether a handle identifies an existing window.</summary>
    /// <param name="hWnd">Handle.</param>
    /// <returns><see langword="true"/> for a live window.</returns>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint hWnd);

    /// <summary>Whether a window is minimized.</summary>
    /// <param name="hWnd">Window.</param>
    /// <returns><see langword="true"/> when minimized.</returns>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint hWnd);

    /// <summary>Sets a window's show state.</summary>
    /// <param name="hWnd">Window.</param>
    /// <param name="nCmdShow"><c>SW_*</c> command.</param>
    /// <returns>Whether the window was previously visible.</returns>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hWnd, int nCmdShow);

    /// <summary><c>SW_RESTORE</c>: activate and restore a minimized window.</summary>
    internal const int SW_RESTORE = 9;

    /// <summary>The current foreground (active top-level) window.</summary>
    /// <returns>The window, or 0 during activation transitions.</returns>
    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    /// <summary>Asks to bring a window to the foreground; subject to the foreground lock rules.</summary>
    /// <param name="hWnd">Window.</param>
    /// <returns><see langword="true"/> when the window was brought to the foreground.</returns>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hWnd);

    /// <summary>Brings a window to the top of the Z order (activates it when top-level).</summary>
    /// <param name="hWnd">Window.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BringWindowToTop(nint hWnd);

    /// <summary>Shares input state (focus, active window, key state) between two threads.</summary>
    /// <param name="idAttach">Thread to attach.</param>
    /// <param name="idAttachTo">Thread to attach to.</param>
    /// <param name="fAttach"><see langword="true"/> to attach, <see langword="false"/> to detach.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    /// <summary>Gets the thread (and process) that created a window.</summary>
    /// <param name="hWnd">Window.</param>
    /// <param name="lpdwProcessId">Receives the process id.</param>
    /// <returns>The thread id, or 0 for an invalid window.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    /// <summary>Root/owner ancestor of a window.</summary>
    /// <param name="hwnd">Window.</param>
    /// <param name="gaFlags"><c>GA_*</c> flag.</param>
    /// <returns>The ancestor window.</returns>
    [LibraryImport("user32.dll")]
    internal static partial nint GetAncestor(nint hwnd, uint gaFlags);

    /// <summary><c>GA_ROOTOWNER</c>: the owned-window chain's root.</summary>
    internal const uint GA_ROOTOWNER = 3;

    /// <summary>GUI state of a thread (<c>GUITHREADINFO</c>): focus, caret, capture…</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GUITHREADINFO
    {
        /// <summary>Must be <c>sizeof(GUITHREADINFO)</c>.</summary>
        public uint cbSize;
        /// <summary><c>GUI_*</c> flags (e.g. caret blinking, in menu mode).</summary>
        public uint flags;
        /// <summary>Active window.</summary>
        public nint hwndActive;
        /// <summary>Focused window.</summary>
        public nint hwndFocus;
        /// <summary>Mouse capture window.</summary>
        public nint hwndCapture;
        /// <summary>Menu owner window.</summary>
        public nint hwndMenuOwner;
        /// <summary>Move/size loop window.</summary>
        public nint hwndMoveSize;
        /// <summary>Window owning the system caret (0 when the app draws its own caret).</summary>
        public nint hwndCaret;
        /// <summary>Caret rectangle in <see cref="hwndCaret"/> client coordinates.</summary>
        public RECT rcCaret;
    }

    /// <summary>Reads a GUI thread's focus/caret state.</summary>
    /// <param name="idThread">Thread id (0 = foreground thread).</param>
    /// <param name="pgui">In: <c>cbSize</c> set; out: the state.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO pgui);

    /// <summary>Converts a client point to screen coordinates.</summary>
    /// <param name="hWnd">Window whose client area the point is in.</param>
    /// <param name="lpPoint">In/out point.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ClientToScreen(nint hWnd, ref POINT lpPoint);

    /// <summary>Current cursor position in screen coordinates.</summary>
    /// <param name="lpPoint">Receives the position.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT lpPoint);

    /// <summary>
    /// Binds the calling thread to a desktop (used only to run clipboard tests inside a private window
    /// station). Fails with <c>ERROR_BUSY</c> once the thread owns any window or hook — including COM's
    /// hidden STA window.
    /// </summary>
    /// <param name="hDesktop">Desktop handle from <c>CreateDesktop</c>/<c>OpenDesktop</c>.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetThreadDesktop(nint hDesktop);

    // ───── Clipboard ─────

    /// <summary>Starts sending <see cref="WM_CLIPBOARDUPDATE"/> to a window.</summary>
    /// <param name="hwnd">Listener window.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AddClipboardFormatListener(nint hwnd);

    /// <summary>Stops sending <see cref="WM_CLIPBOARDUPDATE"/> to a window.</summary>
    /// <param name="hwnd">Listener window.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RemoveClipboardFormatListener(nint hwnd);

    /// <summary>The window-station clipboard serial number (incremented on every change).</summary>
    /// <returns>The sequence number (0 if the caller has no window-station access).</returns>
    [LibraryImport("user32.dll")]
    internal static partial uint GetClipboardSequenceNumber();

    /// <summary>Opens the clipboard; fails while another window holds it open.</summary>
    /// <param name="hWndNewOwner">Window to associate (required for <see cref="EmptyClipboard"/> to set an owner).</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenClipboard(nint hWndNewOwner);

    /// <summary>Closes the clipboard (must pair every successful <see cref="OpenClipboard"/>).</summary>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseClipboard();

    /// <summary>Empties the clipboard and makes the opener the owner.</summary>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyClipboard();

    /// <summary>Enumerates available formats (original ones first, then synthesized ones).</summary>
    /// <param name="format">Previous format (0 to start).</param>
    /// <returns>Next format, or 0 at the end/on error.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint EnumClipboardFormats(uint format);

    /// <summary>Gets a format's data handle (renders delayed formats on demand).</summary>
    /// <param name="uFormat">Format id.</param>
    /// <returns>The handle (owned by the clipboard — never free it), or 0.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint GetClipboardData(uint uFormat);

    /// <summary>Places data on the clipboard; on success the system owns <paramref name="hMem"/>.</summary>
    /// <param name="uFormat">Format id.</param>
    /// <param name="hMem">A <c>GMEM_MOVEABLE</c> HGLOBAL (or GDI handle for GDI formats).</param>
    /// <returns><paramref name="hMem"/> on success, 0 on failure (then the caller still owns and must free it).</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetClipboardData(uint uFormat, nint hMem);

    /// <summary>Whether a format is on the clipboard (does not require opening it).</summary>
    /// <param name="format">Format id.</param>
    /// <returns><see langword="true"/> when available.</returns>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsClipboardFormatAvailable(uint format);

    /// <summary>Registers (or looks up) a clipboard format by name; idempotent per session.</summary>
    /// <param name="lpszFormat">Format name.</param>
    /// <returns>The format id (0xC000–0xFFFF), or 0 on failure.</returns>
    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterClipboardFormat(string lpszFormat);

    /// <summary>Gets a registered format's name (fails for standard formats).</summary>
    /// <param name="format">Format id.</param>
    /// <param name="lpszFormatName">Pinned output buffer (a raw pointer: <c>[Out] char[]</c> would need runtime marshalling).</param>
    /// <param name="cchMaxCount">Buffer length in chars.</param>
    /// <returns>Characters copied, or 0 on failure.</returns>
    [LibraryImport("user32.dll", EntryPoint = "GetClipboardFormatNameW", SetLastError = true)]
    internal static unsafe partial int GetClipboardFormatName(uint format, char* lpszFormatName, int cchMaxCount);

    /// <summary>Window that owns the current clipboard content (0 if emptied with a NULL owner).</summary>
    /// <returns>The owner window.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint GetClipboardOwner();

    /// <summary>Window that currently has the clipboard open (diagnostics for "clipboard busy").</summary>
    /// <returns>The window, or 0.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint GetOpenClipboardWindow();

    // ───── Keyboard / input ─────

    /// <summary>Low-level keyboard hook procedure signature.</summary>
    /// <param name="nCode">Hook code (&lt;0 = pass to next hook without processing).</param>
    /// <param name="wParam">Key message (<see cref="WM_KEYDOWN"/> etc.).</param>
    /// <param name="lParam">Pointer to <see cref="KBDLLHOOKSTRUCT"/>.</param>
    /// <returns>Non-zero to swallow the event; otherwise the result of <see cref="CallNextHookEx"/>.</returns>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint HookProc(int nCode, nint wParam, nint lParam);

    /// <summary><c>WH_KEYBOARD_LL</c> hook id.</summary>
    internal const int WH_KEYBOARD_LL = 13;

    /// <summary><c>LLKHF_INJECTED</c>: the event came from <c>SendInput</c>/<c>keybd_event</c>.</summary>
    internal const uint LLKHF_INJECTED = 0x10;

    /// <summary>Low-level keyboard event data.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        /// <summary>Virtual-key code.</summary>
        public uint vkCode;
        /// <summary>Hardware scan code.</summary>
        public uint scanCode;
        /// <summary><c>LLKHF_*</c> flags.</summary>
        public uint flags;
        /// <summary>Event time.</summary>
        public uint time;
        /// <summary>Extra info (set by injectors).</summary>
        public nuint dwExtraInfo;
    }

    /// <summary>Installs a hook.</summary>
    /// <param name="idHook">Hook type.</param>
    /// <param name="lpfn">Hook procedure pointer (must stay alive until unhooked).</param>
    /// <param name="hmod">Module handle (for LL hooks any module of the process works).</param>
    /// <param name="dwThreadId">0 for global LL hooks.</param>
    /// <returns>The hook handle, or 0 on failure.</returns>
    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    internal static partial nint SetWindowsHookEx(int idHook, nint lpfn, nint hmod, uint dwThreadId);

    /// <summary>Removes a hook.</summary>
    /// <param name="hhk">Hook handle.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWindowsHookEx(nint hhk);

    /// <summary>Passes a hook event to the next hook.</summary>
    /// <param name="hhk">Ignored.</param>
    /// <param name="nCode">Hook code.</param>
    /// <param name="wParam">Parameter.</param>
    /// <param name="lParam">Parameter.</param>
    /// <returns>The next hook's result.</returns>
    [LibraryImport("user32.dll")]
    internal static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    /// <summary>Asynchronous (physical + injected) key state; bit 15 = currently down.</summary>
    /// <param name="vKey">Virtual key.</param>
    /// <returns>The state bits.</returns>
    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int vKey);

    /// <summary>Registers a system-wide hotkey; fails with 1409 when another thread owns the combination.</summary>
    /// <param name="hWnd">Window receiving <see cref="WM_HOTKEY"/>.</param>
    /// <param name="id">Hotkey id.</param>
    /// <param name="fsModifiers"><c>MOD_*</c> flags.</param>
    /// <param name="vk">Virtual key.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    /// <summary>Unregisters a hotkey.</summary>
    /// <param name="hWnd">Registering window.</param>
    /// <param name="id">Hotkey id.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(nint hWnd, int id);

    /// <summary><c>MOD_NOREPEAT</c>: do not repeat WM_HOTKEY while the combination is held.</summary>
    internal const uint MOD_NOREPEAT = 0x4000;

    /// <summary><c>ERROR_HOTKEY_ALREADY_REGISTERED</c>.</summary>
    internal const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    /// <summary><c>INPUT_MOUSE</c>.</summary>
    internal const uint INPUT_MOUSE = 0;

    /// <summary><c>INPUT_KEYBOARD</c>.</summary>
    internal const uint INPUT_KEYBOARD = 1;

    /// <summary><c>KEYEVENTF_KEYUP</c>.</summary>
    internal const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>Synthetic input event (<c>INPUT</c>); the union is laid out at the platform's natural alignment.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        /// <summary><see cref="INPUT_MOUSE"/> or <see cref="INPUT_KEYBOARD"/>.</summary>
        public uint type;
        /// <summary>Event payload.</summary>
        public InputUnion u;
    }

    /// <summary>Payload union of <see cref="INPUT"/>.</summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        /// <summary>Mouse payload (the largest member; defines the union size).</summary>
        [FieldOffset(0)] public MOUSEINPUT mi;
        /// <summary>Keyboard payload.</summary>
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    /// <summary>Mouse event payload.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        /// <summary>X delta/position.</summary>
        public int dx;
        /// <summary>Y delta/position.</summary>
        public int dy;
        /// <summary>Wheel/X-button data.</summary>
        public uint mouseData;
        /// <summary><c>MOUSEEVENTF_*</c> flags (0 = no-op event).</summary>
        public uint dwFlags;
        /// <summary>Timestamp (0 = system).</summary>
        public uint time;
        /// <summary>Extra info.</summary>
        public nint dwExtraInfo;
    }

    /// <summary>Keyboard event payload.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        /// <summary>Virtual key.</summary>
        public ushort wVk;
        /// <summary>Scan code.</summary>
        public ushort wScan;
        /// <summary><c>KEYEVENTF_*</c> flags.</summary>
        public uint dwFlags;
        /// <summary>Timestamp (0 = system).</summary>
        public uint time;
        /// <summary>Extra info.</summary>
        public nint dwExtraInfo;
    }

    /// <summary>Injects synthetic input into the system input stream.</summary>
    /// <param name="cInputs">Number of events.</param>
    /// <param name="pInputs">Events.</param>
    /// <param name="cbSize">Must be <c>sizeof(INPUT)</c>.</param>
    /// <returns>Events injected (0 when blocked, e.g. by UIPI towards an elevated foreground app).</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint SendInput(uint cInputs, [In] INPUT[] pInputs, int cbSize);

    // ───── Tray icon & menus ─────

    /// <summary>Tray icon data (<c>NOTIFYICONDATAW</c>, full Vista+ layout).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal unsafe struct NOTIFYICONDATAW
    {
        /// <summary>Must be <c>sizeof(NOTIFYICONDATAW)</c>.</summary>
        public uint cbSize;
        /// <summary>Window receiving callback messages.</summary>
        public nint hWnd;
        /// <summary>Icon id (per window).</summary>
        public uint uID;
        /// <summary><c>NIF_*</c> flags naming the valid members.</summary>
        public uint uFlags;
        /// <summary>Callback message id.</summary>
        public uint uCallbackMessage;
        /// <summary>Icon handle.</summary>
        public nint hIcon;
        /// <summary>Tooltip text (NUL-terminated).</summary>
        public fixed char szTip[128];
        /// <summary>Icon state.</summary>
        public uint dwState;
        /// <summary>Valid state bits.</summary>
        public uint dwStateMask;
        /// <summary>Balloon text.</summary>
        public fixed char szInfo[256];
        /// <summary>Balloon timeout / interface version (union).</summary>
        public uint uTimeoutOrVersion;
        /// <summary>Balloon title.</summary>
        public fixed char szInfoTitle[64];
        /// <summary>Balloon flags.</summary>
        public uint dwInfoFlags;
        /// <summary>Icon GUID (unused).</summary>
        public Guid guidItem;
        /// <summary>Balloon icon.</summary>
        public nint hBalloonIcon;
    }

    /// <summary>Adds, modifies or deletes a tray icon.</summary>
    /// <param name="dwMessage"><c>NIM_*</c> command.</param>
    /// <param name="lpData">Icon data.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Shell_NotifyIcon(uint dwMessage, in NOTIFYICONDATAW lpData);

    /// <summary>Creates an empty popup menu.</summary>
    /// <returns>The menu handle, or 0.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint CreatePopupMenu();

    /// <summary>Appends an item to a menu.</summary>
    /// <param name="hMenu">Menu.</param>
    /// <param name="uFlags"><c>MF_*</c> flags.</param>
    /// <param name="uIDNewItem">Command id.</param>
    /// <param name="lpNewItem">Item text.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AppendMenu(nint hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    /// <summary>Destroys a menu.</summary>
    /// <param name="hMenu">Menu.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyMenu(nint hMenu);

    /// <summary>Shows a popup menu and (with <c>TPM_RETURNCMD</c>) returns the chosen command id.</summary>
    /// <param name="hMenu">Menu.</param>
    /// <param name="uFlags"><c>TPM_*</c> flags.</param>
    /// <param name="x">Screen X.</param>
    /// <param name="y">Screen Y.</param>
    /// <param name="hwnd">Owner window (must be foreground for correct dismissal).</param>
    /// <param name="lptpm">Exclusion rectangle (0 = none).</param>
    /// <returns>The chosen command id, or 0.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int TrackPopupMenuEx(nint hMenu, uint uFlags, int x, int y, nint hwnd, nint lptpm);

    /// <summary>Loads an icon/image.</summary>
    /// <param name="hInst">Module (0 with <c>LR_LOADFROMFILE</c>).</param>
    /// <param name="name">File path or resource name.</param>
    /// <param name="type"><c>IMAGE_ICON</c> etc.</param>
    /// <param name="cx">Desired width.</param>
    /// <param name="cy">Desired height.</param>
    /// <param name="fuLoad"><c>LR_*</c> flags.</param>
    /// <returns>The handle, or 0.</returns>
    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint LoadImage(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

    /// <summary>Destroys an icon created by <see cref="LoadImage"/>.</summary>
    /// <param name="hIcon">Icon.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint hIcon);

    /// <summary>System metric for a specific DPI (PMv2-correct).</summary>
    /// <param name="nIndex"><c>SM_*</c> index.</param>
    /// <param name="dpi">DPI.</param>
    /// <returns>The metric.</returns>
    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetricsForDpi(int nIndex, uint dpi);

    /// <summary>System DPI.</summary>
    /// <returns>The DPI.</returns>
    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForSystem();

    // ───── Monitors ─────

    /// <summary>Monitor information (<c>MONITORINFO</c>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        /// <summary>Must be <c>sizeof(MONITORINFO)</c>.</summary>
        public uint cbSize;
        /// <summary>Full monitor rectangle.</summary>
        public RECT rcMonitor;
        /// <summary>Work area (excludes taskbar).</summary>
        public RECT rcWork;
        /// <summary><c>MONITORINFOF_PRIMARY</c> etc.</summary>
        public uint dwFlags;
    }

    /// <summary>Monitor containing (or nearest to) a point.</summary>
    /// <param name="pt">Screen point.</param>
    /// <param name="dwFlags"><c>MONITOR_DEFAULTTONEAREST</c> = 2.</param>
    /// <returns>The monitor handle.</returns>
    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromPoint(POINT pt, uint dwFlags);

    /// <summary>Monitor containing (or nearest to) a window.</summary>
    /// <param name="hwnd">Window.</param>
    /// <param name="dwFlags"><c>MONITOR_DEFAULTTONEAREST</c> = 2.</param>
    /// <returns>The monitor handle.</returns>
    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromWindow(nint hwnd, uint dwFlags);

    /// <summary>Monitor rectangles.</summary>
    /// <param name="hMonitor">Monitor.</param>
    /// <param name="lpmi">In: <c>cbSize</c>; out: info.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    /// <summary>Effective DPI of a monitor.</summary>
    /// <param name="hmonitor">Monitor.</param>
    /// <param name="dpiType">0 = effective DPI.</param>
    /// <param name="dpiX">Horizontal DPI.</param>
    /// <param name="dpiY">Vertical DPI.</param>
    /// <returns>HRESULT.</returns>
    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ───── Memory, processes, modules ─────

    /// <summary><c>GMEM_MOVEABLE</c>: required for clipboard HGLOBALs.</summary>
    internal const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>Allocates global memory.</summary>
    /// <param name="uFlags"><c>GMEM_*</c> flags.</param>
    /// <param name="dwBytes">Size.</param>
    /// <returns>The handle, or 0.</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);

    /// <summary>Locks global memory and returns a pointer to it.</summary>
    /// <param name="hMem">Handle.</param>
    /// <returns>Pointer, or 0 (e.g. not an HGLOBAL, or discarded).</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalLock(nint hMem);

    /// <summary>Unlocks global memory.</summary>
    /// <param name="hMem">Handle.</param>
    /// <returns><see langword="true"/> while still locked; <see langword="false"/> when unlocked (check last error for real failures).</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(nint hMem);

    /// <summary>Size of a global memory block (may exceed the size requested at allocation).</summary>
    /// <param name="hMem">Handle.</param>
    /// <returns>Size in bytes, or 0 on failure.</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nuint GlobalSize(nint hMem);

    /// <summary>Frees global memory.</summary>
    /// <param name="hMem">Handle.</param>
    /// <returns>0 on success.</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalFree(nint hMem);

    /// <summary>Frees memory allocated by <c>LocalAlloc</c> (e.g. NCrypt output buffers).</summary>
    /// <param name="hMem">Handle.</param>
    /// <returns>0 on success.</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint LocalFree(nint hMem);

    /// <summary>Module handle of a loaded module (<see langword="null"/> = the process EXE).</summary>
    /// <param name="lpModuleName">Module name or <see langword="null"/>.</param>
    /// <returns>The handle, or 0.</returns>
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandle(string? lpModuleName);

    /// <summary>Native id of the calling thread.</summary>
    /// <returns>The thread id.</returns>
    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    /// <summary><c>PROCESS_QUERY_LIMITED_INFORMATION</c>: enough for the image path, allowed even for most elevated processes.</summary>
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>Opens a process handle.</summary>
    /// <param name="dwDesiredAccess">Access mask.</param>
    /// <param name="bInheritHandle">Inheritable.</param>
    /// <param name="dwProcessId">Process id.</param>
    /// <returns>The handle (close with <see cref="CloseHandle"/>), or 0.</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    /// <summary>Closes a kernel handle.</summary>
    /// <param name="hObject">Handle.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint hObject);

    /// <summary>Full Win32 path of a process image.</summary>
    /// <param name="hProcess">Process handle with query access.</param>
    /// <param name="dwFlags">0 = Win32 path format.</param>
    /// <param name="lpExeName">Pinned output buffer.</param>
    /// <param name="lpdwSize">In: buffer chars; out: chars written.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool QueryFullProcessImageName(nint hProcess, uint dwFlags, char* lpExeName, ref uint lpdwSize);

    // ───── DPAPI-NG (CNG data protection) ─────

    /// <summary><c>NCRYPT_SILENT_FLAG</c>: never show UI (e.g. for descriptors that could prompt).</summary>
    internal const uint NCRYPT_SILENT_FLAG = 0x40;

    /// <summary>Decrypts a DPAPI-NG blob; the protection descriptor is read from the blob itself.</summary>
    /// <param name="phDescriptor">Optional out descriptor handle (pass 0).</param>
    /// <param name="dwFlags">Flags.</param>
    /// <param name="pbProtectedBlob">Blob.</param>
    /// <param name="cbProtectedBlob">Blob length.</param>
    /// <param name="pMemPara">Custom allocator (0 = LocalAlloc).</param>
    /// <param name="hWnd">Parent for UI (0).</param>
    /// <param name="ppbData">Receives the plaintext (free with <see cref="LocalFree"/>).</param>
    /// <param name="pcbData">Receives the plaintext length.</param>
    /// <returns>SECURITY_STATUS (0 = success).</returns>
    [LibraryImport("ncrypt.dll")]
    internal static partial int NCryptUnprotectSecret(nint phDescriptor, uint dwFlags, [In] byte[] pbProtectedBlob, uint cbProtectedBlob,
        nint pMemPara, nint hWnd, out nint ppbData, out uint pcbData);

    /// <summary>Encrypts data under a protection descriptor.</summary>
    /// <param name="hDescriptor">Descriptor from <see cref="NCryptCreateProtectionDescriptor"/>.</param>
    /// <param name="dwFlags">Flags.</param>
    /// <param name="pbData">Plaintext.</param>
    /// <param name="cbData">Plaintext length.</param>
    /// <param name="pMemPara">Custom allocator (0 = LocalAlloc).</param>
    /// <param name="hWnd">Parent for UI (0).</param>
    /// <param name="ppbProtectedBlob">Receives the blob (free with <see cref="LocalFree"/>).</param>
    /// <param name="pcbProtectedBlob">Receives the blob length.</param>
    /// <returns>SECURITY_STATUS (0 = success).</returns>
    [LibraryImport("ncrypt.dll")]
    internal static partial int NCryptProtectSecret(nint hDescriptor, uint dwFlags, [In] byte[] pbData, uint cbData,
        nint pMemPara, nint hWnd, out nint ppbProtectedBlob, out uint pcbProtectedBlob);

    /// <summary>Creates a protection descriptor from a rule string such as <c>LOCAL=user</c>.</summary>
    /// <param name="pwszDescriptorString">Descriptor rule.</param>
    /// <param name="dwFlags">Flags.</param>
    /// <param name="phDescriptor">Receives the handle (close with <see cref="NCryptCloseProtectionDescriptor"/>).</param>
    /// <returns>SECURITY_STATUS (0 = success).</returns>
    [LibraryImport("ncrypt.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int NCryptCreateProtectionDescriptor(string pwszDescriptorString, uint dwFlags, out nint phDescriptor);

    /// <summary>Closes a protection descriptor.</summary>
    /// <param name="hDescriptor">Descriptor.</param>
    /// <returns>SECURITY_STATUS (0 = success).</returns>
    [LibraryImport("ncrypt.dll")]
    internal static partial int NCryptCloseProtectionDescriptor(nint hDescriptor);
}
