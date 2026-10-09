<#
.SYNOPSIS
    Saves the whole primary screen to a PNG in physical pixels, and prints where one program's visible windows are.
    Made for the README photo of a flyout that leaves its window (the update dialog under the panel's update
    button), inside a claude-desktops Windows desktop.

.DESCRIPTION
    tools/readme/capture-window.ps1 saves one window. A flyout that is larger than its window lives in a window of
    its own, so a photo of both needs the screen, and the place of each window on it. This script gives both:
    the screen as a PNG, and one line per visible, uncloaked top-level window of the named program, front to back:

        0x<handle> <class> <left> <top> <width> <height>

    The numbers are DWM's extended frame bounds in physical pixels, the same rectangle capture-window.ps1 uses.
    tools/readme/compose-shot.py takes the PNG, a second PNG of the same screen without the windows, and these
    rectangles, and builds the photo.

    Like capture-window.ps1, the process first becomes per-monitor DPI aware (v2): without that, Windows PowerShell
    gets scaled coordinates at 150 %, and the copy is a blurry, shrunken image.

    Use it only on a throwaway desktop. On a real PC a copy of the screen holds every window's private content,
    which is why CLAUDE.md section 4 asks for PrintWindow there.

.PARAMETER Out
    The PNG file to write. Its folder is created.

.PARAMETER Process
    Name of the program whose windows are listed, without ".exe". Default: BetterClipboard. No such process
    running is not an error: the screen is saved and no window line is printed (the picture without the windows).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File C:\bc\readme\capture-screen.ps1 -Out Z:\shots\update-with.png

    With the panel and its update dialog open: saves the screen, and prints the panel's and the dialog's rectangles.

.OUTPUTS
    System.String. First "<file>: <width>x<height>", then one line per window as described above.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Out,
    [string] $Process = 'BetterClipboard'
)
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>The user32/dwmapi calls the screen capture needs, and the window enumeration (a callback, so it
/// lives here: PowerShell 5.1 cannot pass a script block as a native callback reliably).</summary>
public static class ScreenNative
{
    /// <summary>A rectangle in screen pixels.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    /// <summary>Sets the process's DPI awareness; fails once something already set it.</summary>
    /// <param name="value">A DPI_AWARENESS_CONTEXT pseudo handle (-4 = per-monitor v2).</param>
    /// <returns>False when the awareness was set before (by a manifest or an earlier call).</returns>
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    /// <summary>Sets this thread's DPI awareness, which wins over the process's for this thread.</summary>
    /// <param name="value">A DPI_AWARENESS_CONTEXT pseudo handle (-4 = per-monitor v2).</param>
    /// <returns>The thread's previous context, or zero when the value is invalid.</returns>
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);

    /// <summary>The primary screen's size along one axis, in the calling thread's DPI awareness.</summary>
    /// <param name="index">0 = SM_CXSCREEN (width), 1 = SM_CYSCREEN (height).</param>
    /// <returns>Pixels; physical ones for a per-monitor aware thread.</returns>
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int capacity);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowInt(IntPtr hwnd, int attribute, out int value, int size);

    /// <summary>Lists the windows of some processes that are on the screen, front to back.</summary>
    /// <remarks>
    /// Skipped: hidden windows, windows DWM cloaks (BetterClipboard keeps its panel shown but cloaked while it
    /// is concealed), and windows without an area. EnumWindows goes through the z-order from the top, so the
    /// first line is the window in front (the flyout), which is the order compose-shot.py pastes in reverse.
    /// </remarks>
    /// <param name="processIds">Ids of the processes whose windows count.</param>
    /// <returns>One "0x&lt;handle&gt; &lt;class&gt; &lt;left&gt; &lt;top&gt; &lt;width&gt; &lt;height&gt;" line per window.</returns>
    public static List<string> WindowsOf(uint[] processIds)
    {
        var wanted = new HashSet<uint>(processIds);
        var lines = new List<string>();
        EnumWindows((hwnd, lParam) =>
        {
            uint processId;
            GetWindowThreadProcessId(hwnd, out processId);
            if (!wanted.Contains(processId) || !IsWindowVisible(hwnd)) { return true; }

            // 14 = DWMWA_CLOAKED: nonzero while the window is kept off the screen.
            int cloaked;
            if (DwmGetWindowInt(hwnd, 14, out cloaked, 4) == 0 && cloaked != 0) { return true; }

            // 9 = DWMWA_EXTENDED_FRAME_BOUNDS: the visible frame in physical pixels.
            Rect rect;
            if (DwmGetWindowAttribute(hwnd, 9, out rect, 16) != 0) { return true; }
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) { return true; }

            var name = new StringBuilder(256);
            GetClassName(hwnd, name, name.Capacity);
            lines.Add(string.Format("0x{0:x} {1} {2} {3} {4} {5}", hwnd.ToInt64(), name, rect.Left, rect.Top, width, height));
            return true;
        }, IntPtr.Zero);
        return lines;
    }
}
"@

# Per-monitor v2 is -4. The process call fails harmlessly when PowerShell set an awareness already; the thread
# call then still makes the sizes and coordinates below physical for this thread, which does all the work.
[void][ScreenNative]::SetProcessDpiAwarenessContext([IntPtr](-4))
[void][ScreenNative]::SetThreadDpiAwarenessContext([IntPtr](-4))
Add-Type -AssemblyName System.Drawing

# The window list first: it is the cheaper call, and a flyout that closes during the screen copy would otherwise
# be in the picture and not in the list.
$processIds = [uint32[]] @(Get-Process -Name $Process -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
$windows = if ($processIds.Count -gt 0) { [ScreenNative]::WindowsOf($processIds) } else { @() }

$width = [ScreenNative]::GetSystemMetrics(0)
$height = [ScreenNative]::GetSystemMetrics(1)
$bitmap = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.CopyFromScreen(0, 0, 0, 0, (New-Object System.Drawing.Size $width, $height),
        [System.Drawing.CopyPixelOperation]::SourceCopy)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Out) | Out-Null
    $bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
} finally {
    # GDI+ objects hold native handles; release them even when the copy or the save fails.
    $graphics.Dispose()
    $bitmap.Dispose()
}
'{0}: {1}x{2}' -f $Out, $width, $height
$windows
