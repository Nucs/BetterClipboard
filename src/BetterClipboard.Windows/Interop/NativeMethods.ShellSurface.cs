using System.Runtime.InteropServices;

namespace BetterClipboard.Windows.Interop;

/// <summary>
/// Declarations for telling the shell's own surfaces from app windows by the layer of Windows they live in
/// (<see cref="Input.ShellSurfaces"/>, CLAUDE.md §2.61).
/// </summary>
internal static partial class NativeMethods
{
    /// <summary>
    /// The z-order band a top-level window lives in (undocumented, exported by user32 since Windows 8). App windows are in
    /// the desktop band (1); Start, Search, the notification center, Quick settings and Task View are in bands above every
    /// app window, always-on-top ones included, which a window of an ordinary process can never be raised into.
    /// </summary>
    /// <remarks>
    /// Undocumented: the call may be missing from a future Windows (the caller then gets
    /// <see cref="EntryPointNotFoundException"/> at the first call) or fail for a window that closed meanwhile; callers treat
    /// both as "an app window", the behavior of every version before the band was read. The ids are the ones public
    /// research of user32 names (<c>ZBID_DESKTOP</c> = 1, <c>ZBID_IMMERSIVE_MOGO</c> = 6 for Start, …), checked on
    /// Windows 11 26200 (CLAUDE.md §2.61 has the table).
    /// </remarks>
    /// <param name="hWnd">A top-level window.</param>
    /// <param name="pdwBand">Receives the band id.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowBand(nint hWnd, out uint pdwBand);
}
