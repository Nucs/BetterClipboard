<#
.SYNOPSIS
    Saves one window as the screen shows it (acrylic, Mica and DWM's border included) to a PNG, in physical
    pixels. Made for the README photos, inside a claude-desktops Windows desktop.

.DESCRIPTION
    The process first becomes per-monitor DPI aware (v2). Without that, Windows PowerShell gets DPI-scaled
    coordinates at 150 %: the window's bounds come back too small and the copy is a blurry, shrunken image.

    The bounds are DWM's extended frame bounds: the visible window, without the invisible resize borders around
    it, but with DWM's own border band (2 px at 150 %, half transparent, so it shows what lies behind the window).
    tools/readme/finish-shot.py trims that band and rounds the corners afterwards.

    It copies the screen, not the window's own drawing (PrintWindow cannot draw a system backdrop), so whatever
    covers the window is captured too: keep the window in front, and the pointer, tooltips and the image peek
    away from it.

    Use it only on a throwaway desktop. On a real PC a screen copy can catch other windows' private content,
    which is why CLAUDE.md section 4 asks for PrintWindow there.

.PARAMETER Hwnd
    The window handle in hexadecimal, as the desktop's list_windows prints it (for example 0xc006c).
    Default: the foreground window.

.PARAMETER Out
    The PNG file to write. Its folder is created.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Z:\readme\capture-window.ps1 -Out Z:\shots\panel.png

    Captures the foreground window, the panel right after it was summoned.
#>
[CmdletBinding()]
param(
    [string] $Hwnd,
    [Parameter(Mandatory)] [string] $Out
)
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

/// <summary>The few user32/dwmapi calls the capture needs.</summary>
public static class CaptureNative
{
    /// <summary>A window rectangle in screen pixels.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    /// <summary>Sets the process's DPI awareness; fails once something already set it.</summary>
    /// <param name="value">A DPI_AWARENESS_CONTEXT pseudo handle (-4 = per-monitor v2).</param>
    /// <returns>False when the awareness was set before (by a manifest or an earlier call).</returns>
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    /// <summary>Sets this thread's DPI awareness, which wins over the process's for this thread.</summary>
    /// <param name="value">A DPI_AWARENESS_CONTEXT pseudo handle (-4 = per-monitor v2).</param>
    /// <returns>The thread's previous context, or zero when the value is invalid.</returns>
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);

    /// <summary>The window the user works in.</summary>
    /// <returns>Its handle, or zero while no window is in front (during a switch).</returns>
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();

    /// <summary>Reads a DWM window attribute.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="attribute">9 = DWMWA_EXTENDED_FRAME_BOUNDS: the visible frame in physical pixels.</param>
    /// <param name="value">Receives the rectangle.</param>
    /// <param name="size">Size of <paramref name="value"/> in bytes (16).</param>
    /// <returns>S_OK (0), or an HRESULT such as E_HANDLE for a window that no longer exists.</returns>
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);
}
"@

# Per-monitor v2 is -4. The process call fails harmlessly when PowerShell set an awareness already; the thread
# call then still makes the coordinates below physical for this thread, which does all the work.
[void][CaptureNative]::SetProcessDpiAwarenessContext([IntPtr](-4))
[void][CaptureNative]::SetThreadDpiAwarenessContext([IntPtr](-4))
Add-Type -AssemblyName System.Drawing

$handle = if ($Hwnd) { [IntPtr][Convert]::ToInt64($Hwnd, 16) } else { [CaptureNative]::GetForegroundWindow() }
$rect = New-Object CaptureNative+Rect
$hr = [CaptureNative]::DwmGetWindowAttribute($handle, 9, [ref]$rect, 16)
if ($hr -ne 0) { throw ('DwmGetWindowAttribute failed with 0x{0:X8} for window 0x{1:x}.' -f $hr, $handle.ToInt64()) }
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top

$bitmap = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $width, $height),
        [System.Drawing.CopyPixelOperation]::SourceCopy)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Out) | Out-Null
    $bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
} finally {
    # GDI+ objects hold native handles; release them even when the copy or the save fails.
    $graphics.Dispose()
    $bitmap.Dispose()
}
'{0}: {1}x{2} at ({3},{4}), window 0x{5:x}' -f $Out, $width, $height, $rect.Left, $rect.Top, $handle.ToInt64()
