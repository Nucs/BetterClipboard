#:property PublishAot=false
#:property AllowUnsafeBlocks=true
#:property TargetFramework=net10.0-windows
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static Native;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────
// Probe: how long a paste from the BetterClipboard panel takes, as the user sees it: from the Enter that picks a card to the
// moment its text is in the window the user came from (CLAUDE.md §2.52).
//
// It injects keys (Win+V, Up, Enter, Esc) and takes the foreground for its run, so it runs ONLY inside a disposable
// claude-desktops Windows desktop: it refuses where C:\ProgramData\claude-desktop is missing (that folder exists only inside
// those desktops; probe_summon.cs uses the same guard). Start a BetterClipboard there first, with a few items in its history
// (pins included, to walk up to them like the user does), and wait ~5 s (the panel is prepared 3 s after startup), then:
//
//   dotnet run tools/probes/probe_paste.cs -- [--rounds 10] [--up 2] [--memory warm|trim|purge] [--think 150] [--idle 1500]
//   (or a published copy: dotnet publish tools/probes/probe_paste.cs -r win-x64 --self-contained, run the exe)
//
// Method. A "stage" window holds a multi-line Win32 edit box with the caret (the panel opens next to it, as over a text
// editor). Each round: the stage in front with an empty box; Win+V; once the panel is in front and the user would have looked
// at it (--think × 2), Up --up times (--think apart), then Enter. Measured from the injected Enter:
//   back     the stage became the foreground window again (the panel let go of the keyboard);
//   active   the stage's thread handled its activation (WM_ACTIVATE): it could take keys from here on;
//   keys     the stage's thread took the injected V of Ctrl+V from its queue;
//   text     the edit box's text changed (EN_CHANGE): the paste arrived.
// The last three are timestamped on the stage's own thread, so they show where a slow paste waited: for BetterClipboard to
// send Ctrl+V (keys late, active early), or for the stage's activation (active late: it waits on the old foreground window).
// --memory sets what BetterClipboard's memory looks like when the round starts:
//   warm     as it is (the panel was used a moment ago);
//   trim     its working set emptied (EmptyWorkingSet), as the app's own reclaim does 30 s after every use: the next touch
//            of a page is a soft fault from the standby list;
//   purge    trimmed, then the modified list flushed and the standby list purged (needs administrator rights, which the
//            desktops' agent has): every page comes back from disk, as on a PC whose memory other programs took meanwhile.
// Content-free: it reads no clipboard and prints only times and counts. It writes nothing to the clipboard itself.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────

const string GuardFolder = @"C:\ProgramData\claude-desktop";
if (!Directory.Exists(GuardFolder))
{
    Console.Error.WriteLine($"Refusing to run: {GuardFolder} is missing, so this is not a claude-desktops Windows desktop. "
        + "The probe injects Win+V, Up, Enter and Esc and takes the foreground.");
    return 2;
}

int rounds = 10;
int ups = 2;
string memory = "warm";
int think = 150;
int idle = 1500;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--rounds" when i + 1 < args.Length:
            rounds = int.Parse(args[++i]);
            break;
        case "--up" when i + 1 < args.Length:
            ups = int.Parse(args[++i]);
            break;
        case "--memory" when i + 1 < args.Length && args[i + 1] is "warm" or "trim" or "purge":
            memory = args[++i];
            break;
        case "--think" when i + 1 < args.Length:
            think = int.Parse(args[++i]);
            break;
        case "--idle" when i + 1 < args.Length:
            idle = int.Parse(args[++i]);
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

// Physical pixels everywhere, and 1-ms sleeps: the polling loops below then see a change within about a millisecond (the
// paste's own time is taken on the stage's thread anyway).
SetProcessDpiAwarenessContext(-4);
timeBeginPeriod(1);

var appProcesses = Process.GetProcessesByName("BetterClipboard").Select(p => (uint)p.Id).ToHashSet();
if (appProcesses.Count == 0)
{
    Console.Error.WriteLine("No BetterClipboard process runs: start one first.");
    return 3;
}

if (memory == "purge" && !MemoryLists.EnablePrivilege())
{
    Console.Error.WriteLine("--memory purge needs administrator rights (SeProfileSingleProcessPrivilege could not be enabled).");
    return 3;
}

Console.WriteLine($"BetterClipboard processes: {string.Join(", ", appProcesses)}; {rounds} rounds, Up × {ups}, memory {memory}.");
using var stage = Stage.Create();
var back = new List<double>();
var active = new List<double>();
var keys = new List<double>();
var text = new List<double>();
var summon = new List<double>();
try
{
    for (int round = 1; round <= rounds; round++)
    {
        stage.Clear();
        stage.EnsureForeground();
        Thread.Sleep(idle);
        if (memory != "warm")
        {
            MemoryLists.Trim(appProcesses);
            if (memory == "purge")
            {
                MemoryLists.FlushAndPurge();
            }

            Thread.Sleep(300);
        }

        // The panel captures whatever window is in front at Win+V as its paste target: something that took the foreground
        // during the wait above (a console the agent opened) would receive the paste instead of the stage.
        if (GetForegroundWindow() != stage.Handle)
        {
            stage.EnsureForeground();
        }

        long summonAt = Stopwatch.GetTimestamp();
        Keys.WinV();
        nint panel = Polling.Wait(() => Foreground.Panel(appProcesses), TimeSpan.FromSeconds(5));
        if (panel == 0)
        {
            Console.WriteLine($"round {round,2}: skipped, the panel never came to the front after Win+V");
            continue;
        }

        double panelMs = Stopwatch.GetElapsedTime(summonAt).TotalMilliseconds;
        Thread.Sleep(think * 2);
        for (int up = 0; up < ups; up++)
        {
            Keys.Tap(VK_UP);
            Thread.Sleep(think);
        }

        Thread.Sleep(think);
        long enterAt = Stopwatch.GetTimestamp();
        Keys.Tap(VK_RETURN);
        long backAt = 0;
        while (Stopwatch.GetElapsedTime(enterAt).TotalMilliseconds < 3000)
        {
            if (backAt == 0 && GetForegroundWindow() == stage.Handle)
            {
                backAt = Stopwatch.GetTimestamp();
            }

            if (backAt != 0 && stage.ChangedSince(enterAt) != 0)
            {
                break;
            }

            Thread.Sleep(1);
        }

        long changedAt = stage.ChangedSince(enterAt);
        double? backMs = backAt == 0 ? null : Stopwatch.GetElapsedTime(enterAt, backAt).TotalMilliseconds;
        double? textMs = changedAt == 0 ? null : Stopwatch.GetElapsedTime(enterAt, changedAt).TotalMilliseconds;
        long activeAt = stage.ActivatedSince(enterAt);
        long keyAt = stage.KeyVSince(enterAt);
        double? activeMs = activeAt == 0 ? null : Stopwatch.GetElapsedTime(enterAt, activeAt).TotalMilliseconds;
        double? keyMs = keyAt == 0 ? null : Stopwatch.GetElapsedTime(enterAt, keyAt).TotalMilliseconds;
        Console.WriteLine(
            $"round {round,2}: panel in front {panelMs,6:0.0} ms after Win+V; after Enter: back {Report.Format(backMs)}, "
            + $"active {Report.Format(activeMs)}, keys {Report.Format(keyMs)}, text {Report.Format(textMs)} ({stage.TextLength} characters)");
        summon.Add(panelMs);
        if (backMs is { } b)
        {
            back.Add(b);
        }

        if (activeMs is { } a)
        {
            active.Add(a);
        }

        if (keyMs is { } k)
        {
            keys.Add(k);
        }

        if (textMs is { } t)
        {
            text.Add(t);
        }
        else
        {
            // Nothing arrived: whatever is still open (the panel, a menu) must not leak into the next round.
            Keys.Tap(VK_ESCAPE);
            Thread.Sleep(300);
        }
    }
}
finally
{
    timeEndPeriod(1);
}

Console.WriteLine();
Console.WriteLine($"summon (panel in front)  {Report.Summary(summon)}");
Console.WriteLine($"Enter → stage in front   {Report.Summary(back)}");
Console.WriteLine($"Enter → stage activated  {Report.Summary(active)}");
Console.WriteLine($"Enter → Ctrl+V taken     {Report.Summary(keys)}");
Console.WriteLine($"Enter → text pasted      {Report.Summary(text)}");
return 0;

/// <summary>Formatting of the measured times.</summary>
static class Report
{
    /// <summary>Milliseconds with one decimal, or "never".</summary>
    /// <param name="ms">A time, or <see langword="null"/> when the event never happened.</param>
    /// <returns>The text.</returns>
    public static string Format(double? ms) => ms is { } value ? $"{value,6:0.0} ms" : " never  ";

    /// <summary>Median, 90th percentile, minimum and maximum of a list of times.</summary>
    /// <param name="values">Times in ms.</param>
    /// <returns>One line, or "no data".</returns>
    public static string Summary(List<double> values)
    {
        if (values.Count == 0)
        {
            return "no data";
        }

        var sorted = values.Order().ToArray();
        double Percentile(double p) => sorted[(int)Math.Min(sorted.Length - 1, Math.Round(p * (sorted.Length - 1)))];
        return $"median {Percentile(0.5):0.0} ms, p90 {Percentile(0.9):0.0} ms, min {sorted[0]:0.0} ms, max {sorted[^1]:0.0} ms (n={sorted.Length})";
    }
}

/// <summary>
/// The stand-in for "the app the user pastes into": a window over the work area's left half, on its own STA thread, holding a
/// multi-line Win32 edit box that has the caret while the stage is in front. Every change of the box's text is timestamped
/// on the stage's thread the moment the box reports it (EN_CHANGE), so a paste is timed where it lands.
/// </summary>
sealed class Stage : IDisposable
{
    /// <summary>The edit box's control id (its EN_CHANGE notifications carry it).</summary>
    private const int EditId = 100;

    /// <summary>Stopwatch timestamps of the box's text changes.</summary>
    private static readonly ConcurrentQueue<long> Changes = new();

    /// <summary>Stopwatch timestamps of the stage's activations, as its thread handled them (WM_ACTIVATE, active).</summary>
    private static readonly ConcurrentQueue<long> Activations = new();

    /// <summary>Stopwatch timestamps of V key-downs the stage's thread took from its queue (the V of an injected Ctrl+V).</summary>
    private static readonly ConcurrentQueue<long> KeyDownsOfV = new();

    /// <summary>The window procedure, kept alive for as long as the window exists (a collected delegate crashes the app).</summary>
    private static WndProc? procedure;

    /// <summary>The edit box (set on the stage's thread when the window is created).</summary>
    private static nint edit;

    /// <summary>The thread that owns the window and pumps its messages (joined by <see cref="Dispose"/>).</summary>
    private readonly Thread thread;

    /// <summary>Wraps a stage window that <see cref="Create"/> made.</summary>
    /// <param name="thread">The thread that owns the window.</param>
    /// <param name="handle">The window.</param>
    /// <param name="bounds">Its screen rectangle.</param>
    private Stage(Thread thread, nint handle, RECT bounds)
    {
        this.thread = thread;
        Handle = handle;
        Bounds = bounds;
    }

    /// <summary>The stage's top-level window.</summary>
    public nint Handle { get; }

    /// <summary>Its screen rectangle.</summary>
    public RECT Bounds { get; }

    /// <summary>How many characters the edit box holds now.</summary>
    public int TextLength => (int)SendMessage(edit, WM_GETTEXTLENGTH, 0, 0);

    /// <summary>Creates the stage on its own message-loop thread and waits until it exists.</summary>
    /// <returns>The stage.</returns>
    /// <exception cref="InvalidOperationException">The window could not be created.</exception>
    public static Stage Create()
    {
        nint created = 0;
        RECT area = default;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            SystemParametersInfo(SPI_GETWORKAREA, 0, ref area, 0);
            procedure = WindowProcedure;
            var windowClass = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(procedure),
                hInstance = GetModuleHandle(null),
                hbrBackground = CreateSolidBrush(0x00F0F0F0),
                lpszClassName = "BetterClipboardPasteProbeStage",
                hCursor = LoadCursor(0, 32512),
            };
            RegisterClassEx(ref windowClass);
            int width = (area.Right - area.Left) / 2;
            created = CreateWindowEx(0, windowClass.lpszClassName!, "Paste probe stage", WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                area.Left, area.Top, width, area.Bottom - area.Top, 0, 0, windowClass.hInstance, 0);
            ready.Set();
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.message == WM_KEYDOWN && message.wParam == VK_V)
                {
                    KeyDownsOfV.Enqueue(Stopwatch.GetTimestamp());
                }

                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        })
        { IsBackground = true, Name = "Stage" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (created == 0)
        {
            throw new InvalidOperationException("The stage window could not be created.");
        }

        return new Stage(thread, created, area);
    }

    /// <summary>The first text change at or after <paramref name="ticks"/>.</summary>
    /// <param name="ticks">A Stopwatch timestamp.</param>
    /// <returns>Its Stopwatch timestamp, or 0 when the text has not changed since.</returns>
    public long ChangedSince(long ticks) => Changes.Where(at => at >= ticks).DefaultIfEmpty(0).Min();

    /// <summary>The first activation the stage's thread handled at or after <paramref name="ticks"/>.</summary>
    /// <param name="ticks">A Stopwatch timestamp.</param>
    /// <returns>Its Stopwatch timestamp, or 0 when the stage was not activated since.</returns>
    public long ActivatedSince(long ticks) => Activations.Where(at => at >= ticks).DefaultIfEmpty(0).Min();

    /// <summary>The first V key-down the stage's thread took from its queue at or after <paramref name="ticks"/>.</summary>
    /// <param name="ticks">A Stopwatch timestamp.</param>
    /// <returns>Its Stopwatch timestamp, or 0 when no V arrived since.</returns>
    public long KeyVSince(long ticks) => KeyDownsOfV.Where(at => at >= ticks).DefaultIfEmpty(0).Min();

    /// <summary>Empties the edit box (its own change notification arrives before the next round's Enter, and is ignored).</summary>
    public void Clear() => SendMessage(edit, WM_SETTEXT, 0, 0);

    /// <summary>
    /// Makes the stage the foreground window with the caret in its edit box (a click into the box when Windows refuses
    /// SetForegroundWindow), then parks the cursor in the stage's bottom-right corner, away from where the panel opens.
    /// </summary>
    public void EnsureForeground()
    {
        if (GetForegroundWindow() != Handle)
        {
            SetForegroundWindow(Handle);
            Thread.Sleep(50);
        }

        if (GetForegroundWindow() != Handle)
        {
            Mouse.Click(Bounds.Left + 60, Bounds.Top + 80);
            Thread.Sleep(150);
        }

        SetCursorPos(Bounds.Left + ((Bounds.Right - Bounds.Left) / 2) - 3, Bounds.Bottom - 3);
        uint stageThread = GetWindowThreadProcessId(Handle, out _);
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 2000)
        {
            var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
            if (GetGUIThreadInfo(stageThread, ref info) && info.hwndCaret == edit)
            {
                return;
            }

            Thread.Sleep(5);
        }

        Console.Error.WriteLine("The edit box has no caret after 2 s: the panel will open at the pointer.");
    }

    /// <summary>Closes the stage window and ends its thread.</summary>
    public void Dispose()
    {
        PostMessage(Handle, WM_CLOSE, 0, 0);
        thread.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>The stage's window procedure: the edit box made and kept focused and sized, its changes timestamped.</summary>
    /// <param name="hwnd">The stage.</param>
    /// <param name="message">The message.</param>
    /// <param name="wParam">Its first parameter.</param>
    /// <param name="lParam">Its second parameter.</param>
    /// <returns>The message's result.</returns>
    private static nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WM_CREATE:
                edit = CreateWindowEx(WS_EX_CLIENTEDGE, "EDIT", string.Empty,
                    WS_CHILD | WS_VISIBLE | WS_VSCROLL | ES_MULTILINE | ES_AUTOVSCROLL | ES_WANTRETURN,
                    12, 12, 600, 400, hwnd, EditId, GetModuleHandle(null), 0);
                return 0;
            case WM_SIZE:
                MoveWindow(edit, 12, 12, Math.Max(50, (int)(lParam & 0xFFFF) - 24), Math.Max(50, (int)((lParam >> 16) & 0xFFFF) - 24), true);
                return 0;
            case WM_SETFOCUS:
                SetFocus(edit);
                return 0;
            case WM_ACTIVATE when (wParam & 0xFFFF) != 0:
                // Active or click-active: the thread is handling its activation now. Windows' own handling follows (it gives
                // the focus back, which WM_SETFOCUS above passes on to the box).
                Activations.Enqueue(Stopwatch.GetTimestamp());
                return DefWindowProc(hwnd, message, wParam, lParam);
            case WM_COMMAND when (int)(wParam & 0xFFFF) == EditId && (int)((wParam >> 16) & 0xFFFF) == EN_CHANGE:
                Changes.Enqueue(Stopwatch.GetTimestamp());
                return 0;
            case WM_DESTROY:
                PostQuitMessage(0);
                return 0;
            default:
                return DefWindowProc(hwnd, message, wParam, lParam);
        }
    }
}

/// <summary>
/// Puts BetterClipboard's memory where the user's PC may have it when they press Win+V: its working set emptied (soft faults
/// from the standby list on the next touch), or emptied and its pages gone from RAM altogether (hard faults from disk).
/// </summary>
static class MemoryLists
{
    /// <summary>NtSetSystemInformation's class for the memory lists.</summary>
    private const int SystemMemoryListInformation = 80;

    /// <summary>Write the modified pages out (they then join the standby list).</summary>
    private const int MemoryFlushModifiedList = 3;

    /// <summary>Drop every standby page (their next touch reads them from disk).</summary>
    private const int MemoryPurgeStandbyList = 4;

    /// <summary>Enables the privilege the memory-list commands need (administrators hold it, disabled).</summary>
    /// <returns><see langword="true"/> when it is enabled now.</returns>
    public static bool EnablePrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out nint token))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out long luid))
            {
                return false;
            }

            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            return AdjustTokenPrivileges(token, false, ref privileges, 0, 0, 0) && Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>Empties the working sets of BetterClipboard's processes, as its own reclaim does after every use.</summary>
    /// <param name="processes">Their ids.</param>
    /// <exception cref="Win32Exception">A process could not be opened or trimmed.</exception>
    public static void Trim(IEnumerable<uint> processes)
    {
        foreach (uint id in processes)
        {
            nint process = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA, false, id);
            if (process == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Process {id} could not be opened.");
            }

            try
            {
                if (!EmptyWorkingSet(process))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Process {id}'s working set could not be emptied.");
                }
            }
            finally
            {
                CloseHandle(process);
            }
        }
    }

    /// <summary>Writes the modified list out and purges the standby list: what was trimmed must come back from disk.</summary>
    /// <exception cref="Win32Exception">A command was refused (the privilege is not enabled).</exception>
    public static void FlushAndPurge()
    {
        foreach (int command in new[] { MemoryFlushModifiedList, MemoryPurgeStandbyList })
        {
            int value = command;
            int status = NtSetSystemInformation(SystemMemoryListInformation, ref value, sizeof(int));
            if (status != 0)
            {
                throw new Win32Exception($"NtSetSystemInformation({command}) failed with NTSTATUS 0x{status:X8}.");
            }
        }
    }
}

/// <summary>Waiting on conditions instead of fixed sleeps.</summary>
static class Polling
{
    /// <summary>Polls <paramref name="probe"/> every millisecond until it returns non-zero or <paramref name="timeout"/> passes.</summary>
    /// <param name="probe">The condition; non-zero ends the wait.</param>
    /// <param name="timeout">The longest wait.</param>
    /// <returns>The probe's last result (0 on a timeout).</returns>
    public static nint Wait(Func<nint> probe, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        nint result;
        while ((result = probe()) == 0 && watch.Elapsed < timeout)
        {
            Thread.Sleep(1);
        }

        return result;
    }
}

/// <summary>Which window is in front.</summary>
static class Foreground
{
    /// <summary>The foreground window when it belongs to BetterClipboard (the panel, during a summon), else 0.</summary>
    /// <param name="appProcesses">BetterClipboard's process ids.</param>
    /// <returns>The window or 0.</returns>
    public static nint Panel(HashSet<uint> appProcesses)
    {
        nint hwnd = GetForegroundWindow();
        GetWindowThreadProcessId(hwnd, out uint pid);
        return hwnd != 0 && appProcesses.Contains(pid) ? hwnd : 0;
    }
}

/// <summary>Injected keys, the way a keyboard sends them (virtual key and scan code).</summary>
static class Keys
{
    /// <summary>Win+V: Win down, V down, V up, Win up, in one SendInput call (no gap a scheduler could widen).</summary>
    public static void WinV() => Send(
        Key(VK_LWIN, down: true, extended: true),
        Key(VK_V, down: true),
        Key(VK_V, down: false),
        Key(VK_LWIN, down: false, extended: true));

    /// <summary>Presses and releases one key.</summary>
    /// <param name="vk">Virtual key.</param>
    public static void Tap(ushort vk) => Send(Key(vk, down: true, extended: vk == VK_UP), Key(vk, down: false, extended: vk == VK_UP));

    /// <summary>One keyboard event.</summary>
    /// <param name="vk">Virtual key.</param>
    /// <param name="down">Key down, else key up.</param>
    /// <param name="extended">An extended key (the Windows keys and the arrow keys are).</param>
    /// <returns>The event.</returns>
    private static INPUT Key(ushort vk, bool down, bool extended = false) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT
        {
            wVk = vk,
            wScan = (ushort)MapVirtualKey(vk, 0),
            dwFlags = (down ? 0u : KEYEVENTF_KEYUP) | (extended ? KEYEVENTF_EXTENDEDKEY : 0u),
        },
    };

    /// <summary>Injects the events and reports a short count (UIPI drops them silently, which this cannot see).</summary>
    /// <param name="inputs">The events.</param>
    public static void Send(params INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            Console.Error.WriteLine($"SendInput injected {sent} of {inputs.Length} events (error {Marshal.GetLastWin32Error()}).");
        }
    }
}

/// <summary>Injected mouse clicks.</summary>
static class Mouse
{
    /// <summary>Moves the cursor to a screen point and clicks the left button there.</summary>
    /// <param name="x">Screen x in physical pixels.</param>
    /// <param name="y">Screen y in physical pixels.</param>
    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        Keys.Send(
            new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } },
            new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } });
    }
}

/// <summary>Win32 and NT declarations the probe uses.</summary>
static class Native
{
    /// <summary>Message ids.</summary>
    public const uint WM_CREATE = 0x0001, WM_DESTROY = 0x0002, WM_SIZE = 0x0005, WM_ACTIVATE = 0x0006, WM_SETFOCUS = 0x0007,
        WM_CLOSE = 0x0010, WM_SETTEXT = 0x000C, WM_GETTEXTLENGTH = 0x000E, WM_KEYDOWN = 0x0100, WM_COMMAND = 0x0111;

    /// <summary>The edit box's change notification (in WM_COMMAND's high word).</summary>
    public const int EN_CHANGE = 0x0300;

    /// <summary>Window styles.</summary>
    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_VISIBLE = 0x10000000, WS_CHILD = 0x40000000, WS_VSCROLL = 0x00200000,
        ES_MULTILINE = 0x0004, ES_AUTOVSCROLL = 0x0040, ES_WANTRETURN = 0x1000, WS_EX_CLIENTEDGE = 0x00000200;

    /// <summary>Virtual keys.</summary>
    public const ushort VK_LWIN = 0x5B, VK_V = 0x56, VK_UP = 0x26, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B;

    /// <summary>SendInput constants.</summary>
    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1, KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2,
        MOUSEEVENTF_LEFTDOWN = 2, MOUSEEVENTF_LEFTUP = 4;

    /// <summary>The work area's SystemParametersInfo action.</summary>
    public const uint SPI_GETWORKAREA = 0x0030;

    /// <summary>Process access rights for EmptyWorkingSet.</summary>
    public const uint PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_SET_QUOTA = 0x0100;

    /// <summary>Token access rights and the enabled-privilege flag.</summary>
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020, TOKEN_QUERY = 0x0008, SE_PRIVILEGE_ENABLED = 0x0002;

    /// <summary>A window procedure.</summary>
    /// <param name="hwnd">Window.</param>
    /// <param name="message">Message.</param>
    /// <param name="wParam">First parameter.</param>
    /// <param name="lParam">Second parameter.</param>
    /// <returns>The result.</returns>
    public delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>A rectangle in screen pixels.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        /// <summary>Edges.</summary>
        public int Left, Top, Right, Bottom;
    }

    /// <summary>A message from the queue.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        /// <summary>Fields of MSG.</summary>
        public nint hwnd, message, wParam, lParam;

        /// <summary>Time and cursor position.</summary>
        public int time, x, y, lPrivate;
    }

    /// <summary>A window class.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        /// <summary>Size and style.</summary>
        public uint cbSize, style;

        /// <summary>The window procedure.</summary>
        public nint lpfnWndProc;

        /// <summary>Extra bytes.</summary>
        public int cbClsExtra, cbWndExtra;

        /// <summary>Handles.</summary>
        public nint hInstance, hIcon, hCursor, hbrBackground;

        /// <summary>Names.</summary>
        public string? lpszMenuName, lpszClassName;

        /// <summary>Small icon.</summary>
        public nint hIconSm;
    }

    /// <summary>A keyboard event for SendInput.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        /// <summary>Key codes.</summary>
        public ushort wVk, wScan;

        /// <summary>Flags and time.</summary>
        public uint dwFlags, time;

        /// <summary>Extra information.</summary>
        public nint dwExtraInfo;
    }

    /// <summary>A mouse event for SendInput.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        /// <summary>Movement.</summary>
        public int dx, dy;

        /// <summary>Data, flags and time.</summary>
        public uint mouseData, dwFlags, time;

        /// <summary>Extra information.</summary>
        public nint dwExtraInfo;
    }

    /// <summary>SendInput's event: 40 bytes on x64 (type, padding, a 32-byte union).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct INPUT
    {
        /// <summary>INPUT_MOUSE or INPUT_KEYBOARD.</summary>
        [FieldOffset(0)] public uint type;

        /// <summary>The keyboard event.</summary>
        [FieldOffset(8)] public KEYBDINPUT ki;

        /// <summary>The mouse event.</summary>
        [FieldOffset(8)] public MOUSEINPUT mi;
    }

    /// <summary>A thread's GUI state: its focus, active window and caret.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        /// <summary>Size of the structure, and flags.</summary>
        public uint cbSize, flags;

        /// <summary>Active, focus, capture, menu owner, move/size and caret windows.</summary>
        public nint hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;

        /// <summary>The caret's rectangle in its window's client coordinates.</summary>
        public RECT rcCaret;
    }

    /// <summary>One privilege to change (TOKEN_PRIVILEGES with a single LUID_AND_ATTRIBUTES).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct TOKEN_PRIVILEGES
    {
        /// <summary>1.</summary>
        public uint PrivilegeCount;

        /// <summary>The privilege's LUID.</summary>
        public long Luid;

        /// <summary>SE_PRIVILEGE_ENABLED.</summary>
        public uint Attributes;
    }

    /// <summary>Sets the process's DPI awareness; -4 = per-monitor v2, so every coordinate is in physical pixels.</summary>
    /// <param name="value">A DPI_AWARENESS_CONTEXT value.</param>
    /// <returns><see langword="true"/> when set.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetProcessDpiAwarenessContext(nint value);

    /// <summary>Raises the system timer's resolution for this process (1 ms: short sleeps really are short).</summary>
    /// <param name="period">Milliseconds.</param>
    /// <returns>0 on success.</returns>
    [DllImport("winmm.dll")]
    public static extern uint timeBeginPeriod(uint period);

    /// <summary>Undoes <see cref="timeBeginPeriod"/>.</summary>
    /// <param name="period">The same milliseconds.</param>
    /// <returns>0 on success.</returns>
    [DllImport("winmm.dll")]
    public static extern uint timeEndPeriod(uint period);

    /// <summary>Injects input events into the session's input stream.</summary>
    /// <param name="count">Number of events.</param>
    /// <param name="inputs">The events.</param>
    /// <param name="size">Size of one <see cref="INPUT"/> (40 on x64).</param>
    /// <returns>How many were injected.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, INPUT[] inputs, int size);

    /// <summary>Translates a virtual key to its scan code (map type 0).</summary>
    /// <param name="code">The virtual key.</param>
    /// <param name="mapType">0 = virtual key to scan code.</param>
    /// <returns>The scan code, or 0.</returns>
    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint code, uint mapType);

    /// <summary>The window that receives keyboard input now.</summary>
    /// <returns>The window, or 0 while activation is changing.</returns>
    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    /// <summary>Asks Windows to activate a window.</summary>
    /// <param name="hwnd">The window.</param>
    /// <returns><see langword="true"/> when it was brought forward.</returns>
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(nint hwnd);

    /// <summary>The thread and process that own a window.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="processId">Receives the process id.</param>
    /// <returns>The thread id (0 for an invalid window).</returns>
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    /// <summary>A GUI thread's state (the caret window tells whether the edit box holds the caret).</summary>
    /// <param name="thread">The thread.</param>
    /// <param name="info">Receives the state; its <c>cbSize</c> must be set.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    public static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);

    /// <summary>Moves the mouse cursor.</summary>
    /// <param name="x">Screen x.</param>
    /// <param name="y">Screen y.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    /// <summary>Gives a window of this thread the keyboard.</summary>
    /// <param name="hwnd">The window.</param>
    /// <returns>The window that had it.</returns>
    [DllImport("user32.dll")]
    public static extern nint SetFocus(nint hwnd);

    /// <summary>Moves and sizes a window.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="x">Left edge.</param>
    /// <param name="y">Top edge.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="repaint">Repaint it.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    public static extern bool MoveWindow(nint hwnd, int x, int y, int width, int height, bool repaint);

    /// <summary>Registers a window class.</summary>
    /// <param name="windowClass">The class.</param>
    /// <returns>Its atom, or 0 on failure.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    /// <summary>Creates a window.</summary>
    /// <param name="exStyle">Extended styles.</param>
    /// <param name="className">A registered class.</param>
    /// <param name="title">The title.</param>
    /// <param name="style">Styles.</param>
    /// <param name="x">Left edge.</param>
    /// <param name="y">Top edge.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="parent">Parent or owner (0 = none).</param>
    /// <param name="menu">Menu, or a child's control id.</param>
    /// <param name="instance">Module of the class.</param>
    /// <param name="param">Creation data (0 = none).</param>
    /// <returns>The window, or 0 on failure.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    /// <summary>Windows' default handling of a message.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="message">The message.</param>
    /// <param name="wParam">First parameter.</param>
    /// <param name="lParam">Second parameter.</param>
    /// <returns>The default result.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>Sends a message and waits for its result.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="message">The message.</param>
    /// <param name="wParam">First parameter.</param>
    /// <param name="lParam">Second parameter (0 for an empty WM_SETTEXT).</param>
    /// <returns>The result.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>Waits for the next message of this thread.</summary>
    /// <param name="message">Receives the message.</param>
    /// <param name="hwnd">0 = any window of the thread.</param>
    /// <param name="min">Lowest message id (0 = no filter).</param>
    /// <param name="max">Highest message id (0 = no filter).</param>
    /// <returns>&gt; 0 for a message, 0 for WM_QUIT, -1 on error.</returns>
    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG message, nint hwnd, uint min, uint max);

    /// <summary>Turns key messages into WM_CHAR (the edit box types with them).</summary>
    /// <param name="message">The message.</param>
    /// <returns><see langword="true"/> when a character message was posted.</returns>
    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG message);

    /// <summary>Sends a message to its window procedure.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The procedure's result.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint DispatchMessage(ref MSG message);

    /// <summary>Queues a message for a window's thread.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="message">The message.</param>
    /// <param name="wParam">First parameter.</param>
    /// <param name="lParam">Second parameter.</param>
    /// <returns><see langword="true"/> when queued.</returns>
    [DllImport("user32.dll")]
    public static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>Ends this thread's message loop (GetMessage returns 0).</summary>
    /// <param name="exitCode">The exit code carried by WM_QUIT.</param>
    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int exitCode);

    /// <summary>Loads a system cursor.</summary>
    /// <param name="instance">0 for a system cursor.</param>
    /// <param name="name">The cursor id (32512 = the arrow).</param>
    /// <returns>The cursor.</returns>
    [DllImport("user32.dll")]
    public static extern nint LoadCursor(nint instance, int name);

    /// <summary>Reads a rectangle setting (the work area).</summary>
    /// <param name="action">The SPI_ action.</param>
    /// <param name="param">Action-specific.</param>
    /// <param name="value">Receives the rectangle.</param>
    /// <param name="flags">Update flags.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    public static extern bool SystemParametersInfo(uint action, uint param, ref RECT value, uint flags);

    /// <summary>A solid brush (the stage's background).</summary>
    /// <param name="color">0x00BBGGRR.</param>
    /// <returns>The brush.</returns>
    [DllImport("gdi32.dll")]
    public static extern nint CreateSolidBrush(uint color);

    /// <summary>The handle of a loaded module.</summary>
    /// <param name="name"><see langword="null"/> = this executable.</param>
    /// <returns>The module.</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandle(string? name);

    /// <summary>Opens a process.</summary>
    /// <param name="access">Access rights.</param>
    /// <param name="inherit">Inheritable handle.</param>
    /// <param name="processId">The process.</param>
    /// <returns>The handle, or 0.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint OpenProcess(uint access, bool inherit, uint processId);

    /// <summary>The current process's pseudo-handle.</summary>
    /// <returns>The pseudo-handle (never closed).</returns>
    [DllImport("kernel32.dll")]
    public static extern nint GetCurrentProcess();

    /// <summary>Closes a handle.</summary>
    /// <param name="handle">The handle.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(nint handle);

    /// <summary>Removes every page it can from a process's working set (they go to the standby or modified list).</summary>
    /// <param name="process">A handle with query and set-quota rights.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool EmptyWorkingSet(nint process);

    /// <summary>Opens a process's token.</summary>
    /// <param name="process">The process.</param>
    /// <param name="access">Access rights.</param>
    /// <param name="token">Receives the token.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(nint process, uint access, out nint token);

    /// <summary>A privilege's LUID by name.</summary>
    /// <param name="system"><see langword="null"/> = this machine.</param>
    /// <param name="name">The privilege's name.</param>
    /// <param name="luid">Receives the LUID.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    /// <summary>Enables or disables privileges of a token (success with error 1300 means it was not held).</summary>
    /// <param name="token">The token.</param>
    /// <param name="disableAll">Disable every privilege.</param>
    /// <param name="state">The privilege to set.</param>
    /// <param name="bufferLength">0: the previous state is not wanted.</param>
    /// <param name="previous">0.</param>
    /// <param name="returnLength">0.</param>
    /// <returns><see langword="true"/> when the call ran (check GetLastError for 1300).</returns>
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(nint token, bool disableAll, ref TOKEN_PRIVILEGES state, int bufferLength, nint previous, nint returnLength);

    /// <summary>Sets system information (here: the memory lists' commands).</summary>
    /// <param name="infoClass">The class (80 = SystemMemoryListInformation).</param>
    /// <param name="value">The command.</param>
    /// <param name="length">Its size.</param>
    /// <returns>An NTSTATUS (0 = success).</returns>
    [DllImport("ntdll.dll")]
    public static extern int NtSetSystemInformation(int infoClass, ref int value, int length);
}
