<#
.SYNOPSIS
    Resizes BetterClipboard's open panel through a series of widths and captures it at each one: the frames of the
    README's hero animation (the panel widening until every tab shows).

.DESCRIPTION
    Finds the panel (BetterClipboard's visible window without a minimize box; Settings has one), keeps its top-left
    corner and height, and for each width sets the window's size with SetWindowPos (no activation, so the panel stays
    open), waits for WinUI to lay out and draw the new size, and captures it with capture-window.ps1 (the screen's
    pixels, DWM's border band included; finish-shot.py trims it later).

    The widths are visible widths in physical pixels, from -From to -To in -Count steps, spaced on a cosine so the
    motion eases in and out. The window's invisible resize borders are measured once and added. A resize from another
    process is not remembered as the panel's size: the app saves only a resize the user drags.

    Runs only inside a claude-desktops Windows desktop (CLAUDE.md section 3.4), with capture-window.ps1 next to it:
    it copies the screen. Stop the caret from blinking first (user32 SetCaretBlinkTime with 0xFFFFFFFF), or the
    search box's caret comes and goes between frames. tools/readme/hero-gif.py turns the frames into the GIF.

.PARAMETER From
    The first visible width in pixels: the panel's own width (696 with the groups column, 432 x 640 DIP at 150 %).

.PARAMETER To
    The last visible width in pixels: wide enough for every tab (1418).

.PARAMETER Count
    How many frames, both ends included.

.PARAMETER Left
    Where the window's visible left edge goes, so that the widest frame still fits on the screen.

.PARAMETER SettleMs
    How long to wait after each resize before capturing.

.PARAMETER OutDir
    The folder for the frames (00.png, 01.png, ...), on the guest's own disk: a new folder on the persist share (Z:)
    may not be created from inside the guest. Zip the frames into a folder that exists there to bring them out.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Z:\readme\hero-frames.ps1 -From 696 -To 1418 -Count 24 -Left 400 -OutDir C:\hero\frames *> C:\hero\frames.log

    The README's 2026-11-15 animation. Redirect the output to a file: piping it into Select-Object -First stops this
    script after that many lines.

.NOTES
    Throws when no panel is open or a capture fails ($ErrorActionPreference is Stop).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [int] $From,
    [Parameter(Mandatory)] [int] $To,
    [int] $Count = 22,
    [int] $Left = 300,
    [int] $SettleMs = 700,
    [string] $OutDir = 'C:\hero\frames'
)
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>The user32/dwmapi calls the resize needs.</summary>
public static class HeroNative
{
    /// <summary>A rectangle in screen pixels.</summary>
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }

    /// <summary>Enumerates top-level windows.</summary>
    /// <param name="hwnd">A window.</param>
    /// <param name="lParam">Unused.</param>
    /// <returns>True to go on.</returns>
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    /// <summary>Calls the callback for each top-level window.</summary>
    /// <param name="callback">The callback.</param>
    /// <param name="lParam">Passed through.</param>
    /// <returns>False when the enumeration failed or was stopped.</returns>
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    /// <summary>The process that owns a window.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="pid">Receives the process id.</param>
    /// <returns>The thread id.</returns>
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    /// <summary>Whether a window has WS_VISIBLE.</summary>
    /// <param name="hwnd">The window.</param>
    /// <returns>True when visible.</returns>
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);

    /// <summary>A window's style bits.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="index">-16 = GWL_STYLE.</param>
    /// <returns>The style.</returns>
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);

    /// <summary>The window's outer rectangle, invisible resize borders included.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="rect">Receives it.</param>
    /// <returns>False on failure.</returns>
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    /// <summary>Moves and sizes a window.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="after">Ignored with SWP_NOZORDER.</param>
    /// <param name="x">Left.</param>
    /// <param name="y">Top.</param>
    /// <param name="cx">Width.</param>
    /// <param name="cy">Height.</param>
    /// <param name="flags">0x14 = SWP_NOZORDER | SWP_NOACTIVATE.</param>
    /// <returns>False on failure.</returns>
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>Reads a DWM attribute (9 = the visible frame bounds).</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="attribute">The attribute.</param>
    /// <param name="value">Receives the rectangle.</param>
    /// <param name="size">16.</param>
    /// <returns>An HRESULT.</returns>
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);

    /// <summary>Makes this thread per-monitor DPI aware, so every coordinate is physical.</summary>
    /// <param name="value">-4 = per-monitor v2.</param>
    /// <returns>The previous context.</returns>
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
}
"@
[void][HeroNative]::SetThreadDpiAwarenessContext([IntPtr](-4))

# The panel: a visible top-level window of BetterClipboard without WS_MINIMIZEBOX (0x20000); Settings has one.
$pids = @(Get-Process BetterClipboard -ErrorAction Stop | ForEach-Object { $_.Id })
$script:panel = [IntPtr]::Zero
[void][HeroNative]::EnumWindows({
    param($h, $l)
    $procId = [uint32]0
    [void][HeroNative]::GetWindowThreadProcessId($h, [ref]$procId)
    if ($pids -contains [int]$procId -and [HeroNative]::IsWindowVisible($h) -and
        -not ([HeroNative]::GetWindowLong($h, -16) -band 0x20000)) {
        $outer = New-Object HeroNative+Rect
        [void][HeroNative]::GetWindowRect($h, [ref]$outer)
        if (($outer.Right - $outer.Left) -gt 300) { $script:panel = $h; return $false }
    }
    return $true
}, [IntPtr]::Zero)
if ($script:panel -eq [IntPtr]::Zero) { throw 'No open panel found: press Win+V first.' }
$hwnd = $script:panel

$outer = New-Object HeroNative+Rect; [void][HeroNative]::GetWindowRect($hwnd, [ref]$outer)
$visible = New-Object HeroNative+Rect; [void][HeroNative]::DwmGetWindowAttribute($hwnd, 9, [ref]$visible, 16)
# The invisible resize borders: outer minus visible, per side.
$borderLeft = $visible.Left - $outer.Left
$borderRight = $outer.Right - $visible.Right
$outerHeight = $outer.Bottom - $outer.Top
'panel 0x{0:x}: visible {1}x{2}, borders {3}/{4}' -f $hwnd.ToInt64(), ($visible.Right - $visible.Left), ($visible.Bottom - $visible.Top), $borderLeft, $borderRight

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$capture = Join-Path $PSScriptRoot 'capture-window.ps1'
for ($i = 0; $i -lt $Count; $i++) {
    # Cosine spacing: small steps near both ends, larger in the middle, so equal frame times ease in and out.
    $t = (1 - [Math]::Cos([Math]::PI * $i / ($Count - 1))) / 2
    $width = [int][Math]::Round($From + ($To - $From) * $t)
    [void][HeroNative]::SetWindowPos($hwnd, [IntPtr]::Zero, $Left - $borderLeft, $outer.Top, $width + $borderLeft + $borderRight, $outerHeight, 0x14)
    Start-Sleep -Milliseconds $SettleMs
    $file = Join-Path $OutDir ('{0:00}.png' -f $i)
    & powershell -NoProfile -ExecutionPolicy Bypass -File $capture -Hwnd ('0x{0:x}' -f $hwnd.ToInt64()) -Out $file
    if ($LASTEXITCODE -ne 0) { throw "Capture $i failed." }
}
'done: {0} frames in {1}' -f $Count, $OutDir
