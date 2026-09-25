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
}
