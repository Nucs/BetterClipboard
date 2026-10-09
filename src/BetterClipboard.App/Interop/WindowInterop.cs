using System.Runtime.InteropServices;

namespace BetterClipboard.App.Interop;

/// <summary>
/// Small Win32/DWM helpers for styling WinUI windows beyond what <c>AppWindow</c> exposes.
/// </summary>
internal static partial class WindowInterop
{
    private const uint DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    private const uint DWMWA_CLOAK = 13;
    private const uint DWMWA_CLOAKED = 14;
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    /// <summary>
    /// Turns off DWM's own show and hide animations for a window (the zoom-and-fade Windows plays when "Animation effects"
    /// is on), so it appears and disappears in one frame. For a panel summoned by a shortcut, that animation is pure delay.
    /// </summary>
    /// <param name="hwnd">Top-level window.</param>
    /// <returns><see langword="true"/> when DWM took the setting.</returns>
    public static bool DisableTransitions(nint hwnd)
    {
        int disabled = 1;
        return DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref disabled, sizeof(int)) >= 0;
    }

    /// <summary>
    /// Cloaks or uncloaks a window: a cloaked window stays shown as far as Windows and XAML are concerned (it keeps its
    /// rendered content and is redrawn when its content changes) but DWM does not put it on screen.
    /// </summary>
    /// <remarks>
    /// This is how the panel appears instantly: it is never hidden, only cloaked, so a summon uncloaks a picture that is
    /// already drawn instead of waiting for WinUI to render a window that was hidden (PowerToys' Command Palette does the
    /// same). Hiding a window also tells Windows to pick the next foreground window; cloaking does not, so callers that
    /// give up the foreground still hide the window once (see <c>ClipboardFlyout.Conceal</c>).
    /// </remarks>
    /// <param name="hwnd">Top-level window of this process (DWM refuses other processes' windows).</param>
    /// <param name="cloaked">Cloak (<see langword="true"/>) or uncloak.</param>
    /// <returns><see langword="true"/> when DWM took the setting; <see langword="false"/> means callers must hide and show instead.</returns>
    public static bool SetCloaked(nint hwnd, bool cloaked)
    {
        int value = cloaked ? 1 : 0;
        return DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref value, sizeof(int)) >= 0;
    }

    /// <summary>
    /// Whether DWM keeps a window off the screen for any reason: cloaked by this app, by the shell (a window on another
    /// virtual desktop), or by its owner.
    /// </summary>
    /// <param name="hwnd">Top-level window.</param>
    /// <returns><see langword="true"/> when cloaked; <see langword="false"/> when on screen or when DWM cannot say.</returns>
    public static bool IsCloaked(nint hwnd)
    {
        return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int reasons, sizeof(int)) >= 0 && reasons != 0;
    }

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

    /// <summary>Reads a DWM window attribute that is a 32-bit value.</summary>
    /// <param name="hwnd">Window.</param>
    /// <param name="attribute">Attribute id.</param>
    /// <param name="value">Receives the value.</param>
    /// <param name="size">Value size.</param>
    /// <returns>HRESULT.</returns>
    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);

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
