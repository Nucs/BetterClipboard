using System.Runtime.InteropServices;

namespace BetterClipboard.Windows.Interop;

/// <summary>
/// Declarations for reading a window's text caret through Microsoft Active Accessibility (MSAA) when Windows reports no
/// Win32 caret for it (<see cref="Input.AccessibleCaret"/>, CLAUDE.md §2.64): oleacc's <c>AccessibleObjectFromWindow</c>,
/// COM's apartment calls, and the two <c>IAccessible</c> vtable slots the lookup calls through function pointers.
/// </summary>
internal static partial class NativeMethods
{
    /// <summary>
    /// <c>OBJID_CARET</c>: asks a window for its caret object. Chromium answers it from the caret position its input method
    /// tracks, Firefox from its accessibility service, and oleacc builds one from the Win32 caret for windows that do not
    /// answer (all zeros when the window's thread has none).
    /// </summary>
    internal const uint OBJID_CARET = 0xFFFFFFF8;

    /// <summary><c>IID_IAccessible</c> {618736E0-3C3D-11CF-810C-00AA00389B71}.</summary>
    internal static readonly Guid IID_IAccessible = new(0x618736E0, 0x3C3D, 0x11CF, 0x81, 0x0C, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);

    /// <summary><c>COINIT_MULTITHREADED</c>: joins the thread to the process's multithreaded apartment.</summary>
    internal const uint COINIT_MULTITHREADED = 0x0;

    /// <summary>
    /// Vtable slot of <c>IUnknown::Release</c> (QueryInterface 0, AddRef 1, Release 2).
    /// </summary>
    internal const int IUnknownReleaseSlot = 2;

    /// <summary>
    /// Vtable slot of <c>IAccessible::accLocation</c>: IUnknown's three methods, IDispatch's four, then IAccessible's own in
    /// oleacc.idl's order — get_accParent (7) through accSelect (21), accLocation (22). A wrong slot would call another
    /// method with the wrong arguments; the tests read a real caret through it (AccessibleCaretTests).
    /// </summary>
    internal const int IAccessibleAccLocationSlot = 22;

    /// <summary><c>VT_I4</c>: a VARIANT holding a 32-bit signed integer.</summary>
    internal const ushort VT_I4 = 3;

    /// <summary>
    /// A COM <c>VARIANT</c> laid out as the platform's: the type, three reserved words, and the 8-byte-aligned union of two
    /// pointers (16 bytes on 32-bit, 24 on 64-bit). Only <c>VT_I4</c> is ever stored: <c>accLocation</c>'s child id,
    /// <c>CHILDID_SELF</c> (0) for the caret object itself.
    /// </summary>
    /// <remarks>
    /// Passed by value to <c>accLocation</c> through an unmanaged function pointer, as the IDL declares it; the JIT passes a
    /// struct of this size the way the platform's ABI does (by a pointer to a copy on x64 and ARM64), so its size must be the
    /// real VARIANT's, which the second pointer field makes it.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VARIANT_I4
    {
        /// <summary>The variant type (<see cref="VT_I4"/>).</summary>
        public ushort vt;

        /// <summary>Reserved; 0.</summary>
        public ushort wReserved1;

        /// <summary>Reserved; 0.</summary>
        public ushort wReserved2;

        /// <summary>Reserved; 0.</summary>
        public ushort wReserved3;

        /// <summary>The union's first pointer; <c>lVal</c> is its low 32 bits on these little-endian platforms.</summary>
        public nint value;

        /// <summary>The union's second pointer (a <c>BRECORD</c>'s record info); 0. It only gives the struct VARIANT's size.</summary>
        public nint record;

        /// <summary><c>CHILDID_SELF</c> as a <c>VT_I4</c> VARIANT: the object itself rather than one of its children.</summary>
        public static VARIANT_I4 ChildSelf => new() { vt = VT_I4 };
    }

    /// <summary>
    /// Asks a window for one of its accessibility objects: it sends the window <c>WM_GETOBJECT</c> and unmarshals what the
    /// window's thread answers, or builds oleacc's standard object when the window does not answer.
    /// </summary>
    /// <remarks>
    /// Needs COM on the calling thread. It waits for the window's thread to answer, and the object it returns is a
    /// cross-process proxy whose calls wait for that thread too: never call it on a thread that must stay responsive, and
    /// never on an STA thread whose wait would pump messages (<see cref="Input.AccessibleCaret"/> uses an MTA thread of
    /// its own with a deadline).
    /// </remarks>
    /// <param name="hwnd">The window.</param>
    /// <param name="dwId">The object id (<see cref="OBJID_CARET"/>).</param>
    /// <param name="riid">The interface (<see cref="IID_IAccessible"/>).</param>
    /// <param name="ppvObject">Receives the interface pointer (release it through its vtable), or 0.</param>
    /// <returns>An HRESULT; negative on failure.</returns>
    [LibraryImport("oleacc.dll")]
    internal static partial int AccessibleObjectFromWindow(nint hwnd, uint dwId, in Guid riid, out nint ppvObject);

    /// <summary>Initializes COM on the calling thread.</summary>
    /// <param name="pvReserved">Must be 0.</param>
    /// <param name="dwCoInit">The apartment (<see cref="COINIT_MULTITHREADED"/>).</param>
    /// <returns>
    /// S_OK (0), or S_FALSE (1) when the thread was in that apartment already — both need a matching
    /// <see cref="CoUninitialize"/> — or a negative HRESULT (RPC_E_CHANGED_MODE for an STA thread) that needs none.
    /// </returns>
    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(nint pvReserved, uint dwCoInit);

    /// <summary>Balances one successful <see cref="CoInitializeEx"/> on the same thread.</summary>
    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();

    /// <summary>
    /// Gives the calling thread's window a caret (hidden until shown). Only the tests create one: a window of the test
    /// process whose caret oleacc's own caret object reports, so the MSAA lookup is tested without another app.
    /// </summary>
    /// <param name="hWnd">A window of the calling thread.</param>
    /// <param name="hBitmap">0 for a solid caret.</param>
    /// <param name="nWidth">Width in pixels.</param>
    /// <param name="nHeight">Height in pixels.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateCaret(nint hWnd, nint hBitmap, int nWidth, int nHeight);

    /// <summary>Moves the calling thread's caret (client coordinates of its window). Tests only, as <see cref="CreateCaret"/>.</summary>
    /// <param name="x">Left.</param>
    /// <param name="y">Top.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetCaretPos(int x, int y);

    /// <summary>Removes the calling thread's caret. Tests only, as <see cref="CreateCaret"/>.</summary>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyCaret();
}
