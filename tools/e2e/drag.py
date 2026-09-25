"""Live check of "drag the flyout background to move it" on an ISOLATED test instance.

!!! Injects REAL mouse input and one Esc key press. Only run with the user's OK, hands off the machine.
Safety: every press is preceded by a WindowFromPoint check (its root window must be the test flyout,
otherwise the run stops with SKIP instead of clicking another app), Esc is only sent while the test
flyout is the foreground window, and the cursor is put back at the end. Prints positions only.

Steps (scale-aware, any monitor):
  A. press on empty header space, drag (+120, +60), release   -> window moves by exactly that
  C. then move the cursor with the button up                   -> window must NOT follow (a system
     move loop started from WinUI stays glued to the cursor here: the reason it is not used)
  B. press in the search box, drag (+100, 0), release           -> window must NOT move
  D. press on the footer, drag (-90, -50), Esc, release        -> moved, then put back; flyout stays open

Recipe (CLAUDE.md §4): start an isolated instance (BETTERCLIPBOARD_DATA_DIR=<scratch>, settings with
IsCapturePaused, ImportWindowsHistoryOnStartup false and an unused OpenHotkey), launched via
Start-Process (not `&`, or it inherits the caller's output pipe), then `--show-flyout` with the same
variable, then `python tools/e2e/drag.py <test-instance-pid>`, then `--exit` with the same variable.
Exit code: 0 = all passed, 1-4 = number of failed checks, 10 = skipped (point covered by another window).
"""
import ctypes
import sys
import time
from ctypes import wintypes

user32 = ctypes.windll.user32
user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))  # per-monitor v2: physical pixels everywhere

MOUSEEVENTF_MOVE, MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP = 0x0001, 0x0002, 0x0004
MOUSEEVENTF_ABSOLUTE, MOUSEEVENTF_VIRTUALDESK = 0x8000, 0x4000
SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN = 76, 77, 78, 79


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", ctypes.c_long), ("dy", ctypes.c_long), ("mouseData", ctypes.c_uint), ("dwFlags", ctypes.c_uint),
                ("time", ctypes.c_uint), ("dwExtraInfo", ctypes.c_void_p)]


class INPUT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("mi", MOUSEINPUT), ("pad", ctypes.c_byte * 32)]
    _fields_ = [("type", ctypes.c_uint), ("u", _U)]


def send_mouse(flags, x=0, y=0):
    vx, vy = user32.GetSystemMetrics(SM_XVIRTUALSCREEN), user32.GetSystemMetrics(SM_YVIRTUALSCREEN)
    vw, vh = user32.GetSystemMetrics(SM_CXVIRTUALSCREEN), user32.GetSystemMetrics(SM_CYVIRTUALSCREEN)
    event = INPUT(type=0)
    event.u.mi = MOUSEINPUT(int(round((x - vx) * 65535 / (vw - 1))), int(round((y - vy) * 65535 / (vh - 1))), 0,
                            flags | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, 0, None)
    user32.SendInput(1, ctypes.byref(event), ctypes.sizeof(INPUT))


def cursor():
    point = wintypes.POINT()
    user32.GetCursorPos(ctypes.byref(point))
    return point.x, point.y


def rect(hwnd):
    r = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return r.left, r.top, r.right, r.bottom


def find_flyout(pid):
    found = []
    proc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def callback(hwnd, _):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            n = user32.GetWindowTextLengthW(hwnd)
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            if buf.value == "BetterClipboard":
                found.append(hwnd)
        return True

    user32.EnumWindows(proc(callback), 0)
    return found[0] if found else None


class Covered(Exception):
    """The press point is not on the test flyout (another always-on-top window covers it)."""


def ensure_ours(hwnd, point):
    # Never click a window that is not the test flyout: WindowFromPoint gives the deepest window at the
    # point (the XAML island child); its root must be the flyout itself.
    user32.WindowFromPoint.restype = wintypes.HWND
    user32.WindowFromPoint.argtypes = [wintypes.POINT]
    user32.GetAncestor.restype = wintypes.HWND
    user32.GetAncestor.argtypes = [wintypes.HWND, ctypes.c_uint]
    target = user32.WindowFromPoint(wintypes.POINT(*point))
    root = user32.GetAncestor(target, 2) if target else None  # GA_ROOT
    if not root or root != hwnd:
        raise Covered(f"point {point} belongs to another window; not clicking it")


def drag(start, delta, steps=12, hwnd=None):
    if hwnd is not None:
        ensure_ours(hwnd, start)
    x, y = start
    send_mouse(MOUSEEVENTF_MOVE, x, y)
    time.sleep(0.15)
    send_mouse(MOUSEEVENTF_LEFTDOWN, x, y)
    time.sleep(0.15)
    for i in range(1, steps + 1):
        send_mouse(MOUSEEVENTF_MOVE, x + delta[0] * i // steps, y + delta[1] * i // steps)
        time.sleep(0.03)
    time.sleep(0.1)
    send_mouse(MOUSEEVENTF_LEFTUP, x + delta[0], y + delta[1])
    time.sleep(0.35)


def main():
    pid = int(sys.argv[1])
    hwnd = None
    for _ in range(40):
        hwnd = find_flyout(pid)
        if hwnd:
            break
        time.sleep(0.25)
    if not hwnd:
        print("FAIL: test flyout window not found")
        return 2

    time.sleep(0.8)  # entrance animation + first layout
    scale = user32.GetDpiForWindow(hwnd) / 96.0
    original_cursor = cursor()
    failures = 0
    try:
        left, top, right, bottom = rect(hwnd)
        print(f"flyout at {left},{top} size {right - left}x{bottom - top} scale {scale:.2f}")

        # A: empty header space (between the "Clipboard" title and the buttons), 22 DIP down.
        start = (left + int(200 * scale), top + int(22 * scale))
        drag(start, (120, 60), hwnd=hwnd)
        l2, t2, _, _ = rect(hwnd)
        moved = (l2 - left, t2 - top)
        ok = abs(moved[0] - 120) <= 2 and abs(moved[1] - 60) <= 2
        failures += not ok
        print(f"{'ok  ' if ok else 'FAIL'} A: header drag moved the window by {moved} (expected (120, 60))")

        # C: the drag really ended — moving the cursor with the button up must not drag the window along
        # (the first implementation's system move loop stayed glued to the cursor here).
        x, y = cursor()
        for i in range(1, 9):
            send_mouse(MOUSEEVENTF_MOVE, x + 80 * i // 8, y + 40 * i // 8)
            time.sleep(0.03)
        time.sleep(0.3)
        lc, tc, _, _ = rect(hwnd)
        ok = (lc, tc) == (l2, t2)
        failures += not ok
        print(f"{'ok  ' if ok else 'FAIL'} C: after release, a plain cursor move moved the window by {(lc - l2, tc - t2)} (expected (0, 0))")

        # B: inside the search box (row 2: ~10 + 32 + 8 + 16 DIP down) must not move the window.
        start = (l2 + int(150 * scale), t2 + int(66 * scale))
        drag(start, (100, 0), hwnd=hwnd)
        l3, t3, _, _ = rect(hwnd)
        ok = (l3, t3) == (l2, t2)
        failures += not ok
        print(f"{'ok  ' if ok else 'FAIL'} B: search-box drag moved the window by {(l3 - l2, t3 - t2)} (expected (0, 0))")

        # D: Esc mid-drag puts the window back (press on the footer, move, Esc, release).
        start = (l3 + int(200 * scale), t3 + int(549 * scale))
        ensure_ours(hwnd, start)
        send_mouse(MOUSEEVENTF_MOVE, *start)
        time.sleep(0.15)
        send_mouse(MOUSEEVENTF_LEFTDOWN, *start)
        time.sleep(0.15)
        for i in range(1, 9):
            send_mouse(MOUSEEVENTF_MOVE, start[0] - 90 * i // 8, start[1] - 50 * i // 8)
            time.sleep(0.03)
        time.sleep(0.2)
        lm, tm, _, _ = rect(hwnd)
        user32.GetForegroundWindow.restype = wintypes.HWND
        if user32.GetForegroundWindow() != hwnd:
            send_mouse(MOUSEEVENTF_LEFTUP, start[0] - 90, start[1] - 50)
            raise Covered("the test flyout is not the foreground window; not sending Esc")
        user32.keybd_event(0x1B, 0, 0, 0)
        time.sleep(0.03)
        user32.keybd_event(0x1B, 0, 2, 0)
        time.sleep(0.25)
        send_mouse(MOUSEEVENTF_LEFTUP, start[0] - 90, start[1] - 50)
        time.sleep(0.3)
        l4, t4, _, _ = rect(hwnd)
        ok = (lm - l3, tm - t3) == (-90, -50) and (l4, t4) == (l3, t3) and user32.IsWindowVisible(hwnd)
        failures += not ok
        print(f"{'ok  ' if ok else 'FAIL'} D: footer drag moved {(lm - l3, tm - t3)}, Esc put it back: {(l4, t4) == (l3, t3)}, still open: {bool(user32.IsWindowVisible(hwnd))}")
    except Covered as covered:
        print(f"SKIP: {covered}")
        failures = 10
    finally:
        send_mouse(MOUSEEVENTF_MOVE, *original_cursor)
        time.sleep(0.1)
        print("cursor restored:", cursor() == original_cursor)
    return failures


if __name__ == "__main__":
    sys.exit(main())
