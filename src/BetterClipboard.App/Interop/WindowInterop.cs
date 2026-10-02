using System.Runtime.InteropServices;

namespace BetterClipboard.App.Interop;

/// <summary>
/// Small Win32/DWM helpers for styling WinUI windows beyond what <c>AppWindow</c> exposes.
/// </summary>
internal static partial class WindowInterop
{
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    /// <summary>
    /// Asks DWM for Windows 11 rounded corners. Needed for the caption-less flyout, which DWM would
    /// otherwise render square. No-op on Windows 10 (the attribute is ignored there).
    /// </summary>
    /// <param name="hwnd">Top-level window.</param>
    public static void UseRoundedCorners(nint hwnd)
    {
        int preference = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    /// <summary>
    /// Scale factor of the monitor a window is on (1.0 = 96 DPI).
    /// </summary>
    /// <param name="hwnd">Window.</param>
    /// <returns>The scale factor.</returns>
    public static double GetScale(nint hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>
    /// Screen position, in physical pixels, of a window's client-area top-left corner.
    /// </summary>
    /// <remarks>
    /// Needed to place a windowed popup (<c>ShouldConstrainToRootBounds = false</c>) over the whole monitor: a popup's
    /// offset is measured from the window content's origin, so the image overlays convert the monitor's work area into
    /// that space with this. Taken from the OS (<c>ClientToScreen</c>) rather than derived from <c>AppWindow.Position</c>
    /// plus a guessed frame width, which the caption-less resize border makes imprecise.
    /// </remarks>
    /// <param name="hwnd">Window.</param>
    /// <returns>The client origin in screen pixels; <c>(0, 0)</c> if the call fails (the overlay is then a few pixels off, never wrong).</returns>
    public static (int X, int Y) GetClientOrigin(nint hwnd)
    {
        var point = default(POINT);
        return ClientToScreen(hwnd, ref point) ? (point.X, point.Y) : (0, 0);
    }

    /// <summary>Sets a DWM window attribute.</summary>
    /// <param name="hwnd">Window.</param>
    /// <param name="attribute">Attribute id.</param>
    /// <param name="value">Attribute value.</param>
    /// <param name="size">Value size.</param>
    /// <returns>HRESULT.</returns>
    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, int size);

    /// <summary>DPI of the monitor a window is on.</summary>
    /// <param name="hwnd">Window.</param>
    /// <returns>DPI (0 on failure).</returns>
    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    /// <summary>Converts a client-area point to screen coordinates (both in physical pixels).</summary>
    /// <param name="hwnd">Window.</param>
    /// <param name="point">In: client point; out: the same point in screen pixels.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint hwnd, ref POINT point);

    /// <summary>The Win32 <c>POINT</c> (two 32-bit screen/client coordinates).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        /// <summary>X coordinate.</summary>
        public int X;

        /// <summary>Y coordinate.</summary>
        public int Y;
    }
}
