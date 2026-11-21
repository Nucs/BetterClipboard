<#
.SYNOPSIS
  Where is the text caret of the window in front, by each of Windows' three ways of telling, and where does
  BetterClipboard's panel open? The probe behind issue #10, "Next to the text cursor" (CLAUDE.md §2.64).

.DESCRIPTION
  Modes:
    win32   GetGUIThreadInfo's caret of the thread in front: what BetterClipboard reads first. Read-only, and it touches
            no accessibility API, so it changes nothing in the app it asks.
    msaa    win32, plus the MSAA caret (AccessibleObjectFromWindow(OBJID_CARET), then accLocation and accState) of the
            window in front and of its focus window, each with the time it took: what BetterClipboard reads when there is
            no Win32 caret. The first query in a Firefox window starts Firefox's accessibility service (afterwards Firefox
            also keeps a hidden Win32 caret), so run it after anything that must see Firefox as it was.
    a11y    msaa, plus UI Automation: the focused element's TextPattern selection (an empty one widened by a character).
            UI Automation turns on accessibility in the app it asks (Chromium's whole web content among them), and its
            first call takes about a second.
    panel   BetterClipboard's top-level windows: rectangle, cloaked or shown, in front or not, and the mouse.
    summon  For each point of -Points: read the Win32 caret, put the mouse there, press Win+V, wait for the panel to be
            uncloaked, read where it opened, press Esc. INJECTS KEYS AND MOVES THE MOUSE: run it only inside a
            claude-desktops Windows desktop, never on a PC in use.

  Measured with it on 2026-11-21 (Windows 11 25H2 test desktop, BetterClipboard 0.2.11, "Next to the text cursor"):
    Notepad, the Run box, Explorer's rename box  a Win32 caret: the panel at the caret wherever the mouse was.
    Edge (textarea, input, contenteditable, address bar), Firefox, the classic console   no Win32 caret (the console's
      GetGUIThreadInfo even fails, error 87): the panel followed the mouse; MSAA reported the caret (5-42 ms here; the
      app's own first lookup into a process took up to 144 ms, CLAUDE.md §2.64).
    Windows Terminal, Explorer's address bar, the Settings app   neither: only UI Automation reported the caret.

  Run with Windows PowerShell 5.1 or PowerShell 7 inside the desktop, e.g. from the agent's run_command, which does not
  take the foreground:  powershell -NoProfile -ExecutionPolicy Bypass -File C:\qa\probe_caret.ps1 -Mode msaa
  Prints positions, sizes, times and window class names only: never a window's text or anything on the clipboard.

.PARAMETER Mode
  win32, msaa, a11y, panel or summon (above).

.PARAMETER Points
  For summon: mouse positions "x,y;x,y;..." in physical pixels; each gets one Win+V.
#>
param(
    [ValidateSet('win32', 'msaa', 'a11y', 'panel', 'summon')]
    [string] $Mode = 'win32',

    [string] $Points = '1300,750;200,700'
)

$ErrorActionPreference = 'Stop'

# The managed IAccessible lives in Accessibility.dll, which Windows PowerShell finds by its partial name.
$accessibility = [System.Reflection.Assembly]::LoadWithPartialName('Accessibility').Location
$source = @'
using System;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;

public static class CaretProbe
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO
    {
        public int cbSize; public int flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object accessible);

    static string Cls(IntPtr h)
    {
        if (h == IntPtr.Zero) return "-";
        StringBuilder sb = new StringBuilder(256);
        GetClassName(h, sb, 256);
        return sb.ToString();
    }

    static string Proc(IntPtr h)
    {
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        try { return Process.GetProcessById((int)pid).ProcessName; } catch { return "pid " + pid; }
    }

    static GUITHREADINFO ThreadInfo(IntPtr window, out bool ok)
    {
        uint pid;
        uint thread = GetWindowThreadProcessId(window, out pid);
        GUITHREADINFO info = new GUITHREADINFO();
        info.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
        ok = GetGUIThreadInfo(thread, ref info);
        return info;
    }

    // What BetterClipboard reads first (ForegroundContext.Capture): the Win32 caret of the thread in front.
    public static string Win32()
    {
        IntPtr fg = GetForegroundWindow();
        bool ok;
        GUITHREADINFO info = ThreadInfo(fg, out ok);
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("foreground: class=" + Cls(fg) + " process=" + Proc(fg));
        if (!ok)
        {
            sb.AppendLine("WIN32 CARET: GetGUIThreadInfo failed, error " + Marshal.GetLastWin32Error() + " (a console window reports cmd.exe's thread)");
            return sb.ToString();
        }
        sb.AppendLine("focus: class=" + Cls(info.hwndFocus));
        if (info.hwndCaret == IntPtr.Zero)
        {
            sb.AppendLine("WIN32 CARET: none");
            return sb.ToString();
        }
        POINT tl = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
        POINT br = new POINT { X = info.rcCaret.Right, Y = info.rcCaret.Bottom };
        ClientToScreen(info.hwndCaret, ref tl);
        ClientToScreen(info.hwndCaret, ref br);
        sb.AppendLine("WIN32 CARET: class=" + Cls(info.hwndCaret) + " (" + tl.X + "," + tl.Y + ")-(" + br.X + "," + br.Y + ") blinking=" + ((info.flags & 1) != 0));
        return sb.ToString();
    }

    // The MSAA caret: what the window answers to WM_GETOBJECT(OBJID_CARET), or oleacc's own caret object (which reports the
    // Win32 caret of the thread in front only).
    static string Msaa(IntPtr hwnd)
    {
        Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
        object o;
        Stopwatch watch = Stopwatch.StartNew();
        int hr = AccessibleObjectFromWindow(hwnd, 0xFFFFFFF8, ref iid, out o);
        if (hr != 0 || o == null) return "hr=0x" + hr.ToString("X8");
        Accessibility.IAccessible acc = o as Accessibility.IAccessible;
        if (acc == null) return "not an IAccessible";
        try
        {
            int l, t, w, h;
            acc.accLocation(out l, out t, out w, out h, 0);
            string state = "";
            try { state = " state=0x" + Convert.ToInt32(acc.get_accState(0)).ToString("X") + " (0x8000 = flagged invisible)"; } catch { }
            return "(" + l + "," + t + ") " + w + "x" + h + state + " [" + watch.Elapsed.TotalMilliseconds.ToString("0.0") + " ms]";
        }
        catch (Exception e)
        {
            return "accLocation failed: " + e.GetType().Name;
        }
    }

    public static string MsaaReport()
    {
        IntPtr fg = GetForegroundWindow();
        bool ok;
        GUITHREADINFO info = ThreadInfo(fg, out ok);
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("MSAA CARET of the window in front (" + Cls(fg) + "): " + Msaa(fg));
        if (ok && info.hwndFocus != IntPtr.Zero && info.hwndFocus != fg)
            sb.AppendLine("MSAA CARET of its focus window (" + Cls(info.hwndFocus) + "): " + Msaa(info.hwndFocus));
        return sb.ToString();
    }

    // BetterClipboard's panel: its WinUI window without a minimize box; null while cloaked.
    static RECT? Panel(bool all, StringBuilder report)
    {
        RECT? found = null;
        EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h) || !string.Equals(Proc(h), "BetterClipboard", StringComparison.OrdinalIgnoreCase)) return true;
            bool panel = Cls(h) == "WinUIDesktopWin32WindowClass" && (GetWindowLong(h, -16) & 0x20000) == 0;
            int cloaked;
            DwmGetWindowAttribute(h, 14, out cloaked, 4);
            RECT r;
            GetWindowRect(h, out r);
            if (report != null)
                report.AppendLine((panel ? "PANEL " : "window ") + Cls(h) + " (" + r.Left + "," + r.Top + ")-(" + r.Right + "," + r.Bottom + ")" +
                    (cloaked != 0 ? " cloaked" : " shown") + (h == GetForegroundWindow() ? " [in front]" : ""));
            if (panel && cloaked == 0) found = r;
            return all || found == null;
        }, IntPtr.Zero);
        return found;
    }

    public static string PanelReport()
    {
        StringBuilder sb = new StringBuilder();
        POINT c;
        GetCursorPos(out c);
        sb.AppendLine("mouse: (" + c.X + "," + c.Y + ")  in front: " + Cls(GetForegroundWindow()) + " / " + Proc(GetForegroundWindow()));
        Panel(true, sb);
        return sb.ToString();
    }

    static void Tap(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 2, UIntPtr.Zero);
    }

    // One Win+V with the mouse at (x, y): where did the panel open?
    public static string Summon(int x, int y)
    {
        string before = Win32().Replace(Environment.NewLine, " | ").TrimEnd(' ', '|');
        SetCursorPos(x, y);
        Thread.Sleep(250);
        keybd_event(0x5B, 0, 1, UIntPtr.Zero);      // Win down (extended)
        keybd_event(0x56, 0, 0, UIntPtr.Zero);      // V down
        keybd_event(0x56, 0, 2, UIntPtr.Zero);      // V up
        keybd_event(0x5B, 0, 3, UIntPtr.Zero);      // Win up
        Stopwatch watch = Stopwatch.StartNew();
        RECT? panel = null;
        while (watch.ElapsedMilliseconds < 3000 && panel == null) { Thread.Sleep(30); panel = Panel(false, null); }
        Thread.Sleep(300);
        panel = Panel(false, null);
        string where = panel == null ? "NO PANEL within 3 s" :
            "panel at (" + panel.Value.Left + "," + panel.Value.Top + ") " + (panel.Value.Right - panel.Value.Left) + "x" + (panel.Value.Bottom - panel.Value.Top);
        Tap(0x1B);                                    // Esc
        watch.Restart();
        while (watch.ElapsedMilliseconds < 3000 && Panel(false, null) != null) Thread.Sleep(30);
        Thread.Sleep(400);
        return "mouse (" + x + "," + y + ") -> " + where + "   [before: " + before + "]";
    }
}
'@
Add-Type -TypeDefinition $source -ReferencedAssemblies $accessibility

switch ($Mode)
{
    'win32' { [CaretProbe]::Win32() }
    'msaa' { [CaretProbe]::Win32(); [CaretProbe]::MsaaReport() }
    'panel' { [CaretProbe]::PanelReport() }
    'summon'
    {
        foreach ($point in $Points.Split(';'))
        {
            $xy = $point.Split(',')
            [CaretProbe]::Summon([int]$xy[0], [int]$xy[1])
        }
    }
    'a11y'
    {
        [CaretProbe]::Win32()
        [CaretProbe]::MsaaReport()
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
        try
        {
            $watch = [System.Diagnostics.Stopwatch]::StartNew()
            $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
            $current = $focused.Current
            "UIA focused element (found in $([int]$watch.Elapsed.TotalMilliseconds) ms): type=$($current.ControlType.ProgrammaticName) class=$($current.ClassName) framework=$($current.FrameworkId)"
            $pattern = $null
            if ($focused.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref]$pattern))
            {
                foreach ($range in $pattern.GetSelection())
                {
                    $rectangles = $range.GetBoundingRectangles()
                    $widened = ''
                    if ($rectangles.Count -eq 0)
                    {
                        # A caret is an empty range: most providers give it no rectangle, so widen it by one character.
                        $wide = $range.Clone()
                        $moved = $wide.MoveEndpointByUnit([System.Windows.Automation.Text.TextPatternRangeEndpoint]::End, [System.Windows.Automation.Text.TextUnit]::Character, 1)
                        if ($moved -eq 0)
                        {
                            $moved = $wide.MoveEndpointByUnit([System.Windows.Automation.Text.TextPatternRangeEndpoint]::Start, [System.Windows.Automation.Text.TextUnit]::Character, -1)
                        }
                        $rectangles = $wide.GetBoundingRectangles()
                        $widened = " (empty selection widened by $moved character)"
                    }
                    "UIA CARET$widened`: " + (($rectangles | ForEach-Object { "($([int]$_.X),$([int]$_.Y)) $([int]$_.Width)x$([int]$_.Height)" }) -join ' ')
                }
            }
            else
            {
                'UIA: the focused element has no TextPattern'
            }
            "UIA total: $([int]$watch.Elapsed.TotalMilliseconds) ms"
        }
        catch
        {
            "UIA failed: $($_.Exception.GetType().Name)"
        }
    }
}
