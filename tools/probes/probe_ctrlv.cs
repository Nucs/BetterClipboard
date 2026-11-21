#:property PublishAot=false
#:property AllowUnsafeBlocks=true
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using static Native;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────
// Probe: does an injected Ctrl+V reach a busy window as Ctrl+V, or as a plain "v"? (CLAUDE.md §2.54)
//
// The owner's report (2026-11-17): pasting from the panel into a terminal on an overloaded PC typed "v". This probe pastes a
// marker into a real window (Windows Terminal, a classic console, Notepad) the way BetterClipboard does, under simulated
// load, and reads back what the window received, through UI Automation.
//
// It injects keys and takes the foreground, so it runs ONLY inside a disposable claude-desktops Windows desktop: it refuses
// where C:\ProgramData\claude-desktop is missing (probe_paste.cs uses the same guard). It writes a BC-TEST marker to that
// desktop's clipboard.
//
//   dotnet run tools/probes/probe_ctrlv.cs -- --class CASCADIA_HOSTING_WINDOW_CLASS [--process NAME] [--rounds 5]
//       [--mode batch|hold:N|wait|pump|ready|user|read|harass:N] [--load none|busy-before:N|busy-after:N|starve:N]
//       [--hook-delay N] [--cpu N] [--attacher N] [--sample] [--type-after N]
//
// --class     the target's top-level window class: CASCADIA_HOSTING_WINDOW_CLASS (Windows Terminal, Agentmaster),
//             ConsoleWindowClass (a classic console), Notepad. The window must show a command prompt (cmd or PowerShell)
//             for the terminals: each round clears its input line with Esc and reads the line after the prompt's ">".
// --process   the target window's program name (Agentmaster, WindowsTerminal): Windows Terminal and Agentmaster share the
//             window class.
// --mode      how Ctrl+V is injected:
//             batch    one SendInput call with Ctrl down, V down, V up, Ctrl up (BetterClipboard until 2026-11-17);
//             hold:N   Ctrl down, V down, V up; N ms later Ctrl up;
//             wait     Ctrl down, V down, V up; Ctrl up once the target's thread has taken the V key-up from its queue
//                      (AttachThreadInput, then GetKeyState of the shared key state), at most 2 s;
//             pump     Ctrl down, V down, V up in one call; Ctrl up once the target's input thread has answered a WM_NULL
//                      (SendMessageTimeout) — it pumps its messages again — and then kept answering within 2 ms three times
//                      in a row, at least 50 ms after its first answer; at most 1.5 s after the keys went in.
//             ready    first waits until the target's input thread answers two pings in a row within 2 ms (at most 1 s), so
//                      the keys reach a thread that takes them at once; then as pump, with a 20-ms least hold.
// --load      busy-before:N suspends the target's process right after its activation for N ms (it handles neither its
//             activation nor the keys meanwhile); busy-after:N suspends it 40 ms after the activation (it handled the
//             activation, not the keys). The injection happens while it is suspended.
//             starve:N is the incident's order: the target in the background with its process suspended, a window of this
//             probe in front hands it the foreground, the keys go in once Windows reports the target in front (polled every
//             15 ms, at most 600 ms) and 40 ms passed, and the target runs again N ms after the hand-over.
// --hook-delay  a low-level keyboard hook in this process that sleeps N ms for each key event while Ctrl+V is injected:
//             SendInput then takes about 4 × N ms, as on the owner's PC ("sent 176–409 ms" in its paste log lines).
// --cpu       N threads of this process spin at normal priority from before the activation until the window had its
//             second to take the keys: a PC whose processors are all busy (the owner's "overloaded computer").
// --attacher  a thread of this probe attaches its input to the target's thread and detaches again every N ms while Ctrl+V
//             is injected and taken, as programs do that read the foreground window's focus or caret: Microsoft's
//             AttachThreadInput page says the key state "is reset after a call to AttachThreadInput".
// user pastes with BetterClipboard itself: a marker on the clipboard, then Win+V and Enter as a person presses them, round
// after round, and what the window received (BetterClipboard must run in the desktop); with --load busy-after:N and
// --attacher it also makes the window busy each time it comes to the front, and attaches to its thread all along; with
// --sample it pings the window's focus every 5 ms for 600 ms after each Enter and lists the answers slower than 2 ms (how busy
// a window is right after BetterClipboard activates it); with --type-after N it types an x N ms after each Enter, as a person
// typing on (PASTED+X or X+PASTED: the x arrived as itself; PASTED,CTRL-X or PASTED,X-LOST: it arrived as Ctrl+X); with
// --hook-delay every key event from the Enter to the read takes that long through a hook, BetterClipboard's own included.
// Two modes inject nothing, for checking the real app's paste: read prints the target's input line; harass:N watches for N s
// and suspends the target's process for the load's N ms (busy-after:N) each time it comes to the front, attaching to its
// thread all along with --attacher — paste into it with Win+V and Enter meanwhile.
// Output per round: what the window received (PASTED, V, NOTHING or OTHER), SendInput's time, and for --mode wait how
// long the target seemed to take to take the keys from its queue (fooled by key-state resets: CLAUDE.md §2.54).
// ─────────────────────────────────────────────────────────────────────────────────────────────────────

const string GuardFolder = @"C:\ProgramData\claude-desktop";
if (!Directory.Exists(GuardFolder))
{
    Console.Error.WriteLine($"Refusing to run: {GuardFolder} is missing, so this is not a claude-desktops Windows desktop.");
    return 2;
}

string className = "CASCADIA_HOSTING_WINDOW_CLASS";
string? processName = null;
int rounds = 5;
string mode = "batch";
string load = "none";
int hookDelay = 0;
int cpuThreads = 0;
int attacherPeriod = 0;
bool sample = false;
int typeAfter = -1;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--class" when i + 1 < args.Length:
            className = args[++i];
            break;
        case "--process" when i + 1 < args.Length:
            processName = args[++i];
            break;
        case "--cpu" when i + 1 < args.Length:
            cpuThreads = int.Parse(args[++i]);
            break;
        case "--sample":
            sample = true;
            break;
        case "--attacher" when i + 1 < args.Length:
            attacherPeriod = int.Parse(args[++i]);
            break;
        case "--rounds" when i + 1 < args.Length:
            rounds = int.Parse(args[++i]);
            break;
        case "--mode" when i + 1 < args.Length:
            mode = args[++i];
            break;
        case "--load" when i + 1 < args.Length:
            load = args[++i];
            break;
        case "--type-after" when i + 1 < args.Length:
            typeAfter = int.Parse(args[++i]);
            break;
        case "--hook-delay" when i + 1 < args.Length:
            hookDelay = int.Parse(args[++i]);
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

timeBeginPeriod(1);
nint target = FindTarget(className, processName);
if (target == 0)
{
    Console.Error.WriteLine($"No window of class {className}{(processName is null ? string.Empty : $" owned by {processName}")}.");
    return 3;
}

uint targetThread = GetWindowThreadProcessId(target, out uint targetPid);
int loadMs = load.Contains(':') ? int.Parse(load[(load.IndexOf(':') + 1)..]) : 0;
Console.WriteLine($"Target {className} ({Process.GetProcessById((int)targetPid).ProcessName}): window 0x{target:X}, thread {targetThread}, "
    + $"process {targetPid}; mode {mode}, load {load}, hook delay {hookDelay} ms, busy threads {cpuThreads} of {Environment.ProcessorCount} processors.");
// Modes without injection, for checking the real app's paste: read the target's input line, or make the target busy each time
// it comes to the front (suspended for the load's N ms, and attached to all along with --attacher) while a person or a script
// pastes into it with Win+V and Enter.
if (mode == "read")
{
    Console.WriteLine($"input line: [{Reading.InputLine(target)}]");
    return 0;
}

if (mode.StartsWith("harass:", StringComparison.Ordinal))
{
    int seconds = int.Parse(mode[7..]);
    using var harassing = attacherPeriod > 0 ? Attacher.Start(targetThread, attacherPeriod) : null;
    var until = Stopwatch.StartNew();
    bool inFront = InFront();
    int suspensions = 0;
    while (until.Elapsed.TotalSeconds < seconds)
    {
        // While the attacher keeps attaching to the window's thread, Windows mostly reports no foreground window at all:
        // only a window counts, and any window of the target's process.
        nint foreground = GetForegroundWindow();
        if (foreground == 0)
        {
            Thread.Sleep(1);
            continue;
        }

        bool now = InFront();
        if (now && !inFront && loadMs > 0)
        {
            // It just came to the front: busy for N ms, as an overloaded PC's window is when BetterClipboard hands it the keys.
            Suspension.For(targetPid, loadMs).Wait();
            suspensions++;
            Console.WriteLine($"{until.Elapsed.TotalSeconds:0.0} s: in front, suspended {loadMs} ms");
        }

        inFront = now;
        Thread.Sleep(1);
    }

    Console.WriteLine($"harassed for {seconds} s: {suspensions} suspensions, {harassing?.Stop() ?? 0} attaches");
    return 0;

    // Whether a window of the target's process is in front (its main window, or a popup of it).
    bool InFront()
    {
        nint foreground = GetForegroundWindow();
        return foreground != 0 && GetWindowThreadProcessId(foreground, out uint pid) != 0 && pid == targetPid;
    }
}

if (mode == "user")
{
    // BetterClipboard does the paste: each round puts a marker on the clipboard (BetterClipboard records it as its newest
    // item), presses Win+V and Enter as a person would, and reads what the window received. Run "harass:N" in another process
    // to make the window busy meanwhile.
    using var userClipboard = ClipboardOwner.Create();

    // With --hook-delay, every key event takes that long through the hooks from the Enter until the read, BetterClipboard's
    // own Ctrl+V included: the owner's PC, where ~20 programs hook the keyboard.
    using var userHook = hookDelay > 0 ? DelayHook.Start(hookDelay) : null;

    // With --load busy-after:N and --attacher, this process also makes the window busy each time it comes to the front and
    // attaches to its thread all along — in one process, since a desktop runs one command at a time.
    using var userAttacher = attacherPeriod > 0 ? Attacher.Start(targetThread, attacherPeriod) : null;
    using var stopHarassing = new CancellationTokenSource();
    var harasser = loadMs > 0 ? Task.Run(() => SuspendWhenInFront(stopHarassing.Token)) : Task.FromResult(0);
    var userOutcomes = new Dictionary<string, int>();
    for (int round = 1; round <= rounds; round++)
    {
        string marker = $"BCTEST{round}{Environment.TickCount64 % 1000}";
        Activate(target);
        Thread.Sleep(900);
        Keys.Send(Keys.Key(VK_ESCAPE, true), Keys.Key(VK_ESCAPE, false));
        Thread.Sleep(600);
        userClipboard.SetText(marker);
        Thread.Sleep(700);
        Keys.Send(Keys.Key(VK_LWIN, true), Keys.Key(VK_V, true), Keys.Key(VK_V, false), Keys.Key(VK_LWIN, false));
        Thread.Sleep(1200);
        Keys.Send(Keys.Key(VK_RETURN, true), Keys.Key(VK_RETURN, false));
        userHook?.Enable(true);
        if (typeAfter >= 0)
        {
            // A person typing on right after the Enter: the key must reach the window as itself (before or after the
            // paste), never as Ctrl+X while BetterClipboard holds Ctrl down.
            Thread.Sleep(typeAfter);
            Keys.Send(Keys.Key(VK_X, true), Keys.Key(VK_X, false));
        }

        string sampled = sample ? SampleReplies(TimeSpan.FromMilliseconds(600)) : string.Empty;
        Thread.Sleep(sample ? 1900 : 2500);
        userHook?.Enable(false);
        string received = Reading.InputLine(target);
        string verdict = typeAfter >= 0 && received.Contains(marker + "x", StringComparison.Ordinal) ? "PASTED+X"
            : typeAfter >= 0 && received.Contains("x" + marker, StringComparison.Ordinal) ? "X+PASTED"
            : typeAfter >= 0 && received.Contains(marker, StringComparison.Ordinal) && received.Contains("^X", StringComparison.Ordinal) ? "PASTED,CTRL-X"
            : typeAfter >= 0 && received.Contains(marker, StringComparison.Ordinal) ? $"PASTED,X-LOST [{received}]"
            : received.Contains(marker, StringComparison.Ordinal) ? "PASTED"
            : received.Length == 0 ? "NOTHING"
            : received.Trim().Equals("v", StringComparison.OrdinalIgnoreCase) ? "V"
            : $"OTHER [{received}]";
        string tally = verdict.StartsWith("OTHER", StringComparison.Ordinal) ? "OTHER" : verdict;
        userOutcomes[tally] = userOutcomes.GetValueOrDefault(tally) + 1;
        Console.WriteLine($"round {round}: {verdict}{sampled}");
    }

    stopHarassing.Cancel();
    int suspended = harasser.Result;
    int attachedTimes = userAttacher?.Stop() ?? 0;
    Activate(target);
    Keys.Send(Keys.Key(VK_ESCAPE, true), Keys.Key(VK_ESCAPE, false));
    timeEndPeriod(1);
    Console.WriteLine("summary: " + string.Join(", ", userOutcomes.Select(pair => $"{pair.Key} {pair.Value}"))
        + $" ({suspended} suspensions, {attachedTimes} attaches)");
    return 0;

    // Suspends the target's process for the load's N ms each time a window of it comes to the front; returns how often.
    int SuspendWhenInFront(CancellationToken stop)
    {
        bool wasInFront = OwnsForeground();
        int count = 0;
        while (!stop.IsCancellationRequested)
        {
            // While the attacher attaches, Windows mostly reports no foreground window: only a window counts.
            if (GetForegroundWindow() != 0)
            {
                bool inFrontNow = OwnsForeground();
                if (inFrontNow && !wasInFront)
                {
                    Suspension.For(targetPid, loadMs).Wait();
                    count++;
                }

                wasInFront = inFrontNow;
            }

            Thread.Sleep(1);
        }

        return count;
    }

    // After the Enter, pings the window that has the target thread's keyboard focus every 5 ms for a while and describes the
    // answers: when each slow one (over 2 ms) came and how long it took — how busy the window is after BetterClipboard
    // activates it.
    string SampleReplies(TimeSpan span)
    {
        var watch = Stopwatch.StartNew();
        var slow = new List<string>();
        int pings = 0;
        while (watch.Elapsed < span)
        {
            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            nint focus = GetGUIThreadInfo(targetThread, ref info) && info.hwndFocus != 0 ? info.hwndFocus : target;
            long ping = Stopwatch.GetTimestamp();
            bool answered = SendMessageTimeoutW(focus, 0, 0, 0, SMTO_ABORTIFHUNG, 1000, out _) != 0;
            double reply = Stopwatch.GetElapsedTime(ping).TotalMilliseconds;
            pings++;
            if (!answered || reply > 2)
            {
                slow.Add($"{watch.Elapsed.TotalMilliseconds - reply:0}:{(answered ? $"{reply:0}" : "none")}");
            }

            Thread.Sleep(5);
        }

        return $"  [{pings} pings; slow at ms:reply {string.Join(" ", slow)}]";
    }

    // Whether a window of the target's process is in front.
    bool OwnsForeground()
    {
        nint foreground = GetForegroundWindow();
        return foreground != 0 && GetWindowThreadProcessId(foreground, out uint pid) != 0 && pid == targetPid;
    }
}

using var hook = hookDelay > 0 ? DelayHook.Start(hookDelay) : null;
using var owner = ClipboardOwner.Create();
using var standIn = load.StartsWith("starve", StringComparison.Ordinal) ? StandIn.Create() : null;
var outcomes = new Dictionary<string, int>();
for (int round = 1; round <= rounds; round++)
{
    string marker = $"BCTEST{round}{Environment.TickCount64 % 1000}";

    // A clean input line, then the marker on the clipboard, then a moment of rest.
    Activate(target);
    Thread.Sleep(300);
    Keys.Send(Keys.Key(VK_ESCAPE, true), Keys.Key(VK_ESCAPE, false));
    Thread.Sleep(400);
    owner.SetText(marker);

    // BetterClipboard's order: activate the target, give it 40 ms, inject Ctrl+V — with every processor busy when asked.
    using var hog = cpuThreads > 0 ? CpuHog.Start(cpuThreads) : null;
    Task? resume = null;
    string activation = string.Empty;
    long activatedAt;
    if (load.StartsWith("starve", StringComparison.Ordinal))
    {
        // The incident's order (the owner's log: "settle 311 ms"): the target sits in the background with its thread not
        // running, a window of this process (the panel's stand-in) hands it the foreground, and the keys go in as soon as
        // Windows reports the target in front and 40 ms have passed — BetterClipboard's WaitForForegroundAsync, which polls
        // every 15 ms for at most 600 ms. The target's thread runs again after N ms, in the middle of that.
        standIn!.Activate();
        resume = Suspension.For(targetPid, loadMs);
        SetForegroundWindow(target);
        activatedAt = Stopwatch.GetTimestamp();
        nint reported = GetForegroundWindow();
        while (GetForegroundWindow() != target && Stopwatch.GetElapsedTime(activatedAt).TotalMilliseconds < 600)
        {
            Thread.Sleep(15);
        }

        double inFront = Stopwatch.GetElapsedTime(activatedAt).TotalMilliseconds;
        while (Stopwatch.GetElapsedTime(activatedAt).TotalMilliseconds < 40)
        {
            Thread.SpinWait(200);
        }

        string seen = reported == target ? "the target" : reported == standIn.Window ? "the stand-in" : reported == 0 ? "none" : $"0x{reported:X}";
        activation = $"in front at once: {seen}, the target after {inFront:0} ms; ";
    }
    else
    {
        Activate(target);
        activatedAt = Stopwatch.GetTimestamp();
        if (load.StartsWith("busy-before", StringComparison.Ordinal))
        {
            resume = Suspension.For(targetPid, loadMs);
        }

        while (Stopwatch.GetElapsedTime(activatedAt).TotalMilliseconds < 40)
        {
            Thread.SpinWait(200);
        }

        if (load.StartsWith("busy-after", StringComparison.Ordinal))
        {
            resume = Suspension.For(targetPid, loadMs);
        }
    }

    hook?.Enable(true);
    using var attacher = attacherPeriod > 0 ? Attacher.Start(targetThread, attacherPeriod) : null;
    string detail;
    long sendStart = Stopwatch.GetTimestamp();
    if (mode == "batch")
    {
        Keys.Send(Keys.Key(VK_CONTROL, true), Keys.Key(VK_V, true), Keys.Key(VK_V, false), Keys.Key(VK_CONTROL, false));
        detail = $"SendInput {Stopwatch.GetElapsedTime(sendStart).TotalMilliseconds:0} ms";
    }
    else if (mode.StartsWith("hold:", StringComparison.Ordinal))
    {
        int holdMs = int.Parse(mode[5..]);
        Keys.Send(Keys.Key(VK_CONTROL, true), Keys.Key(VK_V, true), Keys.Key(VK_V, false));
        double first = Stopwatch.GetElapsedTime(sendStart).TotalMilliseconds;
        Thread.Sleep(holdMs);
        Keys.Send(Keys.Key(VK_CONTROL, false));
        detail = $"SendInput {first:0} ms, Ctrl up after {Stopwatch.GetElapsedTime(sendStart).TotalMilliseconds:0} ms";
    }
    else if (mode == "wait")
    {
        detail = QueueWatch.InjectAndWait(targetThread);
    }
    else if (mode == "pump")
    {
        detail = Pump.InjectAndHold(target, targetThread);
    }
    else if (mode == "ready")
    {
        detail = Pump.WaitInjectAndHold(target, targetThread);
    }
    else
    {
        Console.Error.WriteLine($"Unknown mode {mode}.");
        return 2;
    }

    hook?.Enable(false);
    resume?.Wait();
    Thread.Sleep(1200);
    string attaches = attacher is null ? string.Empty : $"; {attacher.Stop()} attaches";

    // Reading through UI Automation with every processor busy takes seconds and changes nothing about what was received.
    hog?.Dispose();
    string line = Reading.InputLine(target);
    string outcome = line.Contains(marker, StringComparison.Ordinal) ? "PASTED"
        : line.Length == 0 ? "NOTHING"
        : line.Trim().Equals("v", StringComparison.OrdinalIgnoreCase) ? "V"
        : $"OTHER [{line}]";
    string key = outcome.StartsWith("OTHER", StringComparison.Ordinal) ? "OTHER" : outcome;
    outcomes[key] = outcomes.GetValueOrDefault(key) + 1;
    Console.WriteLine($"round {round}: {outcome,-8} ({activation}{detail}{attaches})");
}

Activate(target);
Keys.Send(Keys.Key(VK_ESCAPE, true), Keys.Key(VK_ESCAPE, false));
timeEndPeriod(1);
Console.WriteLine("summary: " + string.Join(", ", outcomes.Select(pair => $"{pair.Key} {pair.Value}")));
return 0;

/// <summary>
/// The first top-level window of <paramref name="windowClass"/> (in z-order), or the first one whose program is
/// <paramref name="program"/>: Windows Terminal and Agentmaster both use CASCADIA_HOSTING_WINDOW_CLASS.
/// </summary>
/// <param name="windowClass">The window class.</param>
/// <param name="program">The owning process's name without ".exe" (any case), or <see langword="null"/> for any.</param>
/// <returns>The window, or 0 when there is none.</returns>
static nint FindTarget(string windowClass, string? program)
{
    for (nint window = FindWindowExW(0, 0, windowClass, null); window != 0; window = FindWindowExW(0, window, windowClass, null))
    {
        if (program is null)
        {
            return window;
        }

        GetWindowThreadProcessId(window, out uint pid);
        try
        {
            if (string.Equals(Process.GetProcessById((int)pid).ProcessName, program, StringComparison.OrdinalIgnoreCase))
            {
                return window;
            }
        }
        catch (ArgumentException)
        {
            // The process ended between the lookup and the question: not the target.
        }
    }

    return 0;
}

/// <summary>Brings a window to the front (the probe owns the last input, so Windows allows it) and waits until it is.</summary>
/// <param name="window">The window.</param>
static void Activate(nint window)
{
    // A tap of an unassigned key first: the process that sent the last input may set the foreground (the probe may have sent
    // nothing yet), and the key itself means nothing to any window.
    Keys.Send(Keys.Key(0xE8, true), Keys.Key(0xE8, false));
    SetForegroundWindow(window);
    var watch = Stopwatch.StartNew();
    while (GetForegroundWindow() != window && watch.ElapsedMilliseconds < 2000)
    {
        Thread.Sleep(5);
    }
}

/// <summary>Suspends a process for a while, as a stand-in for a program too busy to read its input.</summary>
static class Suspension
{
    /// <summary>Suspends the process now and resumes it after <paramref name="milliseconds"/> on a pool thread.</summary>
    /// <param name="pid">The process.</param>
    /// <param name="milliseconds">How long it stays suspended.</param>
    /// <returns>A task completing once it was resumed.</returns>
    /// <exception cref="InvalidOperationException">The process could not be opened.</exception>
    public static Task For(uint pid, int milliseconds)
    {
        nint process = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        if (process == 0)
        {
            throw new InvalidOperationException($"OpenProcess({pid}) failed: {Marshal.GetLastPInvokeError()}");
        }

        NtSuspendProcess(process);
        return Task.Run(() =>
        {
            Thread.Sleep(milliseconds);
            NtResumeProcess(process);
            CloseHandle(process);
        });
    }
}

/// <summary>
/// Injects Ctrl down, V down, V up, and lifts Ctrl only once the target's thread has taken the V key-up from its queue: this
/// thread attaches to the target's input (AttachThreadInput), so GetKeyState reads the key state the two now share, which
/// changes as the target takes its key messages.
/// </summary>
static class QueueWatch
{
    /// <summary>Runs the injection and the wait; returns a description of what was seen.</summary>
    /// <param name="targetThread">The thread that owns the target window.</param>
    /// <returns>A line for the round's report.</returns>
    public static string InjectAndWait(uint targetThread)
    {
        // A message queue of its own, which AttachThreadInput requires.
        PeekMessageW(out _, 0, 0, 0, 0);
        uint self = GetCurrentThreadId();
        bool attached = AttachThreadInput(self, targetThread, true);
        try
        {
            short before = GetKeyState(VK_V);
            long start = Stopwatch.GetTimestamp();
            Keys.Send(Keys.Key(VK_CONTROL, true), Keys.Key(VK_V, true), Keys.Key(VK_V, false));
            double sent = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            double taken = -1;
            short last = before;
            while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 2000)
            {
                last = GetKeyState(VK_V);

                // The toggle bit flips on every key-down the target takes; the down bit clears with the key-up it takes.
                if ((last & 1) != (before & 1) && (last & 0x8000) == 0)
                {
                    taken = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    break;
                }

                Thread.Sleep(1);
            }

            Keys.Send(Keys.Key(VK_CONTROL, false));
            return $"attached {attached}, SendInput {sent:0} ms, V taken {(taken < 0 ? "never (2 s)" : $"{taken:0} ms")}, "
                + $"key state 0x{(ushort)before:X4} → 0x{(ushort)last:X4}";
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(self, targetThread, false);
            }
        }
    }
}

/// <summary>
/// A visible top-level window of this probe on a thread of its own that pumps messages: BetterClipboard's panel's stand-in,
/// which has the foreground before it hands it to the target, and answers the messages Windows sends a window that loses the
/// foreground (a window that did not would hold the target's activation back).
/// </summary>
sealed class StandIn : IDisposable
{
    /// <summary>The window's thread, for posting it WM_QUIT.</summary>
    private uint threadId;

    /// <summary>The window.</summary>
    public nint Window { get; private set; }

    /// <summary>Creates the window on a new thread and waits until it exists.</summary>
    /// <returns>The stand-in.</returns>
    public static StandIn Create()
    {
        var standIn = new StandIn();
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            standIn.threadId = GetCurrentThreadId();
            standIn.Window = CreateWindowExW(WS_EX_TOPMOST, "STATIC", "BC-TEST stand-in", WS_POPUP | WS_VISIBLE | WS_BORDER, 1200, 700, 300, 120, 0, 0, 0, 0);
            ready.Set();
            while (GetMessageW(out var message, 0, 0, 0) > 0)
            {
                DispatchMessageW(ref message);
            }

            DestroyWindow(standIn.Window);
        }) { IsBackground = true, Name = "Stand-in" };
        thread.Start();
        ready.Wait();
        return standIn;
    }

    /// <summary>Brings the stand-in to the front and waits until Windows reports it there (at most 2 s).</summary>
    public void Activate()
    {
        SetForegroundWindow(Window);
        var watch = Stopwatch.StartNew();
        while (GetForegroundWindow() != Window && watch.ElapsedMilliseconds < 2000)
        {
            Thread.Sleep(5);
        }
    }

    /// <summary>Ends the window's thread, which destroys the window.</summary>
    public void Dispose() => PostThreadMessageW(threadId, WM_QUIT, 0, 0);
}

/// <summary>
/// A thread that attaches its input to the target's thread and detaches again, over and over: a stand-in for the programs
/// that do so to read the foreground window's focus or caret (Microsoft: the key state "is reset after a call to
/// AttachThreadInput").
/// </summary>
sealed class Attacher : IDisposable
{
    /// <summary>Set to stop the thread.</summary>
    private volatile bool stop;

    /// <summary>How many attach-detach pairs succeeded.</summary>
    private int count;

    /// <summary>The thread.</summary>
    private Thread? thread;

    /// <summary>Starts attaching to <paramref name="targetThread"/> every <paramref name="periodMs"/> ms.</summary>
    /// <param name="targetThread">The target's thread.</param>
    /// <param name="periodMs">The pause between two attach-detach pairs.</param>
    /// <returns>The running attacher.</returns>
    public static Attacher Start(uint targetThread, int periodMs)
    {
        var attacher = new Attacher();
        attacher.thread = new Thread(() =>
        {
            // A message queue of its own, which AttachThreadInput requires.
            PeekMessageW(out _, 0, 0, 0, 0);
            uint self = GetCurrentThreadId();
            while (!attacher.stop)
            {
                if (AttachThreadInput(self, targetThread, true))
                {
                    AttachThreadInput(self, targetThread, false);
                    attacher.count++;
                }

                Thread.Sleep(periodMs);
            }
        }) { IsBackground = true, Name = "Attacher" };
        attacher.thread.Start();
        return attacher;
    }

    /// <summary>Stops the thread and waits for it.</summary>
    /// <returns>How many attach-detach pairs it made.</returns>
    public int Stop()
    {
        stop = true;
        thread?.Join();
        return count;
    }

    /// <summary>Stops the thread (a second call does nothing).</summary>
    public void Dispose() => Stop();
}

/// <summary>
/// Injects Ctrl down, V down and V up in one call and lifts Ctrl only once the target's input thread has pumped its messages
/// again and stayed responsive: pinged with WM_NULL (SendMessageTimeout), which a thread answers only from its message loop.
/// Unlike the key-state watch of <see cref="QueueWatch"/>, nothing another program does to the target's key state can fake it.
/// </summary>
static class Pump
{
    /// <summary>The least time Ctrl stays down after the target first answered.</summary>
    private const double MinimumAfterAnswerMs = 50;

    /// <summary>A reply this fast means the thread was waiting for messages, not working through a backlog.</summary>
    private const double QuickReplyMs = 2;

    /// <summary>How many quick replies in a row end the hold.</summary>
    private const int QuickRepliesNeeded = 3;

    /// <summary>The longest hold, counted from the injection.</summary>
    private const double CapMs = 1500;

    /// <summary>Runs the injection and the hold; returns a description of what was seen.</summary>
    /// <param name="target">The target's top-level window.</param>
    /// <param name="targetThread">The thread that owns it.</param>
    /// <returns>A line for the round's report.</returns>
    public static string InjectAndHold(nint target, uint targetThread)
    {
        // The window that takes the keys belongs to the thread that reads them: the focus window, else the target itself.
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        nint focus = GetGUIThreadInfo(targetThread, ref info) && info.hwndFocus != 0 ? info.hwndFocus : target;
        long start = Stopwatch.GetTimestamp();
        Keys.Send(Keys.Key(VK_CONTROL, true), Keys.Key(VK_V, true), Keys.Key(VK_V, false));
        double sent = Elapsed(start);
        double firstAnswer = Ping(focus, CapMs - Elapsed(start)) ? Elapsed(start) : -1;
        int pings = 1;
        int quick = 0;
        while (firstAnswer >= 0 && Elapsed(start) < CapMs && (quick < QuickRepliesNeeded || Elapsed(start) - firstAnswer < MinimumAfterAnswerMs))
        {
            Thread.Sleep(5);
            long ping = Stopwatch.GetTimestamp();
            if (!Ping(focus, CapMs - Elapsed(start)))
            {
                break;
            }

            pings++;
            quick = Elapsed(ping) <= QuickReplyMs ? quick + 1 : 0;
        }

        double released = Elapsed(start);
        Keys.Send(Keys.Key(VK_CONTROL, false));
        return $"SendInput {sent:0} ms, first answer {(firstAnswer < 0 ? "never" : $"{firstAnswer:0} ms")}, {pings} pings, Ctrl up after {released:0} ms";
    }

    /// <summary>
    /// Waits until the target's input thread answers pings promptly, then injects Ctrl down, V down and V up and holds Ctrl as
    /// <see cref="InjectAndHold"/> does, with a shorter least hold: the keys reached a thread that was taking its messages.
    /// </summary>
    /// <param name="target">The target's top-level window.</param>
    /// <param name="targetThread">The thread that owns it.</param>
    /// <returns>A line for the round's report.</returns>
    public static string WaitInjectAndHold(nint target, uint targetThread)
    {
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        nint focus = GetGUIThreadInfo(targetThread, ref info) && info.hwndFocus != 0 ? info.hwndFocus : target;
        long wait = Stopwatch.GetTimestamp();
        int readyPings = 0;
        int quickBefore = 0;
        while (quickBefore < 2 && Elapsed(wait) < 1000)
        {
            long ping = Stopwatch.GetTimestamp();
            if (!Ping(focus, 1000 - Elapsed(wait)))
            {
                break;
            }

            readyPings++;
            quickBefore = Elapsed(ping) <= QuickReplyMs ? quickBefore + 1 : 0;
            if (quickBefore < 2)
            {
                Thread.Sleep(5);
            }
        }

        double ready = Elapsed(wait);
        long start = Stopwatch.GetTimestamp();
        Keys.Send(Keys.Key(VK_CONTROL, true), Keys.Key(VK_V, true), Keys.Key(VK_V, false));
        double sent = Elapsed(start);
        double firstAnswer = Ping(focus, CapMs - Elapsed(start)) ? Elapsed(start) : -1;
        int pings = 1;
        int quick = 0;
        while (firstAnswer >= 0 && Elapsed(start) < 1000 && (quick < QuickRepliesNeeded || Elapsed(start) - firstAnswer < 20))
        {
            Thread.Sleep(5);
            long ping = Stopwatch.GetTimestamp();
            if (!Ping(focus, 1000 - Elapsed(start)))
            {
                break;
            }

            pings++;
            quick = Elapsed(ping) <= QuickReplyMs ? quick + 1 : 0;
        }

        double released = Elapsed(start);
        Keys.Send(Keys.Key(VK_CONTROL, false));
        return $"ready after {ready:0} ms ({readyPings} pings), SendInput {sent:0} ms, first answer {(firstAnswer < 0 ? "never" : $"{firstAnswer:0} ms")}, "
            + $"{pings} pings, Ctrl up after {released:0} ms";
    }

    /// <summary>Sends WM_NULL and waits for the reply.</summary>
    /// <param name="window">The window.</param>
    /// <param name="timeoutMs">The longest wait.</param>
    /// <returns><see langword="true"/> when the window's thread answered in time.</returns>
    private static bool Ping(nint window, double timeoutMs) =>
        timeoutMs > 0 && SendMessageTimeoutW(window, 0, 0, 0, SMTO_ABORTIFHUNG, (uint)Math.Ceiling(timeoutMs), out _) != 0;

    /// <summary>Milliseconds since a timestamp.</summary>
    /// <param name="start">The <see cref="Stopwatch.GetTimestamp"/> to count from.</param>
    /// <returns>The milliseconds.</returns>
    private static double Elapsed(long start) => Stopwatch.GetElapsedTime(start).TotalMilliseconds;
}

/// <summary>Threads that spin at normal priority: a PC whose processors are all busy.</summary>
sealed class CpuHog : IDisposable
{
    /// <summary>The spinning threads.</summary>
    private readonly List<Thread> threads = [];

    /// <summary>Set to stop the threads.</summary>
    private volatile bool stop;

    /// <summary>Starts <paramref name="count"/> spinning threads.</summary>
    /// <param name="count">How many.</param>
    /// <returns>The running hog.</returns>
    public static CpuHog Start(int count)
    {
        var hog = new CpuHog();
        for (int i = 0; i < count; i++)
        {
            var thread = new Thread(hog.Spin) { IsBackground = true, Name = $"Busy {i}" };
            hog.threads.Add(thread);
            thread.Start();
        }

        return hog;
    }

    /// <summary>Stops the threads and waits for them; a second call does nothing.</summary>
    public void Dispose()
    {
        if (stop)
        {
            return;
        }

        stop = true;
        foreach (var thread in threads)
        {
            thread.Join();
        }
    }

    /// <summary>Burns processor time until stopped.</summary>
    private void Spin()
    {
        ulong value = 1;
        while (!stop)
        {
            // Arithmetic, not SpinWait: SpinWait's pause instruction would hand the core's time to its sibling.
            value = (value * 6364136223846793005UL) + 1442695040888963407UL;
        }

        GC.KeepAlive(value);
    }
}

/// <summary>Reads what the target shows on its input line, through UI Automation's text pattern.</summary>
static class Reading
{
    /// <summary>The text after the last prompt (">") on the last non-empty line of the window's text.</summary>
    /// <param name="window">The target window.</param>
    /// <returns>The input line's text, trimmed; empty when there is none.</returns>
    public static string InputLine(nint window)
    {
        // The window's longest text: the terminal's buffer, not a tab's title (Windows Terminal exposes both).
        var root = AutomationElement.FromHandle(window);
        string text = string.Empty;
        foreach (AutomationElement element in root.FindAll(TreeScope.Subtree, new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true)))
        {
            string candidate = ((TextPattern)element.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1);
            if (candidate.Length > text.Length)
            {
                text = candidate;
            }
        }

        var lines = text.Replace("\r\n", "\n").Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length > 0).ToArray();
        if (lines.Length == 0)
        {
            return string.Empty;
        }

        string lastLine = lines[^1];
        int prompt = lastLine.LastIndexOf('>');
        return (prompt >= 0 ? lastLine[(prompt + 1)..] : lastLine).Trim();
    }
}

/// <summary>Owns the clipboard through a message-only window (SetClipboardData needs an owner) and writes text to it.</summary>
sealed class ClipboardOwner : IDisposable
{
    /// <summary>The message-only window.</summary>
    private nint window;

    /// <summary>The window's thread, for posting it WM_QUIT.</summary>
    private uint threadId;

    /// <summary>
    /// Creates the message-only window on a thread of its own that pumps messages: the next program that writes the clipboard
    /// sends its owner WM_DESTROYCLIPBOARD and waits for the answer (BetterClipboard's write took 2.5 s against an owner
    /// whose thread did not pump).
    /// </summary>
    /// <returns>The owner.</returns>
    public static ClipboardOwner Create()
    {
        var owner = new ClipboardOwner();
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            owner.threadId = GetCurrentThreadId();
            owner.window = CreateWindowExW(0, "STATIC", "probe_ctrlv", 0, 0, 0, 0, 0, HWND_MESSAGE, 0, 0, 0);
            ready.Set();
            while (GetMessageW(out var message, 0, 0, 0) > 0)
            {
                DispatchMessageW(ref message);
            }

            DestroyWindow(owner.window);
        }) { IsBackground = true, Name = "Clipboard owner" };
        thread.Start();
        ready.Wait();
        return owner;
    }

    /// <summary>Puts Unicode text on the clipboard.</summary>
    /// <param name="text">The text.</param>
    /// <exception cref="InvalidOperationException">The clipboard could not be opened.</exception>
    public void SetText(string text)
    {
        for (int attempt = 0; !OpenClipboard(window); attempt++)
        {
            if (attempt > 50)
            {
                throw new InvalidOperationException("The clipboard stayed locked.");
            }

            Thread.Sleep(10);
        }

        try
        {
            EmptyClipboard();
            int bytes = (text.Length + 1) * 2;
            nint memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes);
            nint pointer = GlobalLock(memory);
            Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
            Marshal.WriteInt16(pointer, text.Length * 2, 0);
            GlobalUnlock(memory);
            SetClipboardData(CF_UNICODETEXT, memory);
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Ends the window's thread, which destroys the window.</summary>
    public void Dispose() => PostThreadMessageW(threadId, WM_QUIT, 0, 0);
}

/// <summary>
/// A low-level keyboard hook on a thread of its own that sleeps for each key event while enabled, so SendInput (which waits
/// for every low-level hook) takes as long as on a PC whose hooks are slow.
/// </summary>
sealed class DelayHook : IDisposable
{
    /// <summary>The hook procedure, kept alive while the hook is installed (a collected delegate crashes the process).</summary>
    private static HookProc? procedure;

    /// <summary>The delay per event, in ms.</summary>
    private static int delay;

    /// <summary>Whether the hook sleeps right now.</summary>
    private static volatile bool enabled;

    /// <summary>The hook thread's id, for posting it WM_QUIT.</summary>
    private uint threadId;

    /// <summary>Installs the hook on a new thread and waits until it is in place.</summary>
    /// <param name="milliseconds">The delay per key event while enabled.</param>
    /// <returns>The hook.</returns>
    public static DelayHook Start(int milliseconds)
    {
        delay = milliseconds;
        var hook = new DelayHook();
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            hook.threadId = GetCurrentThreadId();
            procedure = (code, wParam, lParam) =>
            {
                if (code >= 0 && enabled)
                {
                    Thread.Sleep(delay);
                }

                return CallNextHookEx(0, code, wParam, lParam);
            };
            nint handle = SetWindowsHookExW(WH_KEYBOARD_LL, procedure, GetModuleHandleW(null), 0);
            ready.Set();
            while (GetMessageW(out var message, 0, 0, 0) > 0)
            {
                DispatchMessageW(ref message);
            }

            UnhookWindowsHookEx(handle);
        }) { IsBackground = true, Name = "Delay hook" };
        thread.Start();
        ready.Wait();
        return hook;
    }

    /// <summary>Turns the delay on or off.</summary>
    /// <param name="on">Whether the hook sleeps for each event.</param>
    public void Enable(bool on) => enabled = on;

    /// <summary>Removes the hook.</summary>
    public void Dispose() => PostThreadMessageW(threadId, WM_QUIT, 0, 0);
}

/// <summary>Keyboard injection.</summary>
static class Keys
{
    /// <summary>A virtual-key event, as BetterClipboard injects them (no scan code).</summary>
    /// <param name="vk">The key.</param>
    /// <param name="down">Down or up.</param>
    /// <returns>The event.</returns>
    public static INPUT Key(ushort vk, bool down) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wVk = vk, dwFlags = down ? 0u : KEYEVENTF_KEYUP },
    };

    /// <summary>Injects events in one SendInput call.</summary>
    /// <param name="inputs">The events.</param>
    public static void Send(params INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            Console.Error.WriteLine($"SendInput injected {sent} of {inputs.Length} events (error {Marshal.GetLastPInvokeError()}).");
        }
    }
}

/// <summary>Win32 declarations.</summary>
static class Native
{
    /// <summary>Virtual keys.</summary>
    public const ushort VK_CONTROL = 0x11, VK_V = 0x56, VK_ESCAPE = 0x1B, VK_LWIN = 0x5B, VK_RETURN = 0x0D, VK_X = 0x58;

    /// <summary>SendInput constants.</summary>
    public const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2;

    /// <summary>Hook, message and clipboard constants.</summary>
    public const int WH_KEYBOARD_LL = 13, WM_QUIT = 0x12;

    /// <summary>CF_UNICODETEXT.</summary>
    public const uint CF_UNICODETEXT = 13;

    /// <summary>GMEM_MOVEABLE.</summary>
    public const uint GMEM_MOVEABLE = 2;

    /// <summary>PROCESS_SUSPEND_RESUME.</summary>
    public const uint PROCESS_SUSPEND_RESUME = 0x0800;

    /// <summary>SendMessageTimeout: return at once when the window's thread is hung.</summary>
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>The parent of message-only windows.</summary>
    public static readonly nint HWND_MESSAGE = -3;

    /// <summary>Window styles for the stand-in: a visible, bordered popup above other windows.</summary>
    public const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000, WS_BORDER = 0x00800000, WS_EX_TOPMOST = 0x00000008;

    /// <summary>A low-level hook procedure.</summary>
    /// <param name="code">The hook code.</param>
    /// <param name="wParam">The message.</param>
    /// <param name="lParam">The event.</param>
    /// <returns>The next hook's result.</returns>
    public delegate nint HookProc(int code, nint wParam, nint lParam);

    /// <summary>A keyboard event for SendInput.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        /// <summary>Virtual key.</summary>
        public ushort wVk;

        /// <summary>Scan code.</summary>
        public ushort wScan;

        /// <summary>Flags.</summary>
        public uint dwFlags;

        /// <summary>Timestamp.</summary>
        public uint time;

        /// <summary>Extra info.</summary>
        public nint dwExtraInfo;
    }

    /// <summary>SendInput's event: 40 bytes on x64 (type, padding, a 32-byte union).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct INPUT
    {
        /// <summary>The kind of event.</summary>
        [FieldOffset(0)] public uint type;

        /// <summary>The keyboard event.</summary>
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    /// <summary>GetGUIThreadInfo's answer.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        /// <summary>The structure's size.</summary>
        public int cbSize;

        /// <summary>GUI_ flags.</summary>
        public uint flags;

        /// <summary>The thread's active window.</summary>
        public nint hwndActive;

        /// <summary>The window with the keyboard focus.</summary>
        public nint hwndFocus;

        /// <summary>The window that captured the mouse.</summary>
        public nint hwndCapture;

        /// <summary>The menu owner.</summary>
        public nint hwndMenuOwner;

        /// <summary>The window being moved or sized.</summary>
        public nint hwndMoveSize;

        /// <summary>The window with the caret.</summary>
        public nint hwndCaret;

        /// <summary>The caret's rectangle: left.</summary>
        public int caretLeft;

        /// <summary>Top.</summary>
        public int caretTop;

        /// <summary>Right.</summary>
        public int caretRight;

        /// <summary>Bottom.</summary>
        public int caretBottom;
    }

    /// <summary>A message.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        /// <summary>Window.</summary>
        public nint hwnd;

        /// <summary>Message.</summary>
        public uint message;

        /// <summary>wParam.</summary>
        public nint wParam;

        /// <summary>lParam.</summary>
        public nint lParam;

        /// <summary>Time.</summary>
        public uint time;

        /// <summary>Cursor x.</summary>
        public int x;

        /// <summary>Cursor y.</summary>
        public int y;
    }

    /// <summary>Injects input.</summary>
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] inputs, int size);

    /// <summary>Finds a top-level window.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint FindWindowW(string? className, string? title);

    /// <summary>Sends a message and waits for the reply, at most a while.</summary>
    /// <param name="window">The window.</param>
    /// <param name="message">The message.</param>
    /// <param name="wParam">wParam.</param>
    /// <param name="lParam">lParam.</param>
    /// <param name="flags">SMTO_ flags.</param>
    /// <param name="timeout">The longest wait, in ms.</param>
    /// <param name="result">The reply.</param>
    /// <returns>Non-zero when the window answered in time.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SendMessageTimeoutW(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    /// <summary>A GUI thread's active, focus and caret windows.</summary>
    /// <param name="thread">The thread.</param>
    /// <param name="info">The answer; cbSize set by the caller.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll", SetLastError = true)] public static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);

    /// <summary>Finds the next top-level window of a class after another one (0 for the first), in z-order.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint FindWindowExW(nint parent, nint after, string? className, string? title);

    /// <summary>A window's thread and process.</summary>
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint window, out uint pid);

    /// <summary>Activates a window.</summary>
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint window);

    /// <summary>The foreground window.</summary>
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();

    /// <summary>Shares input state with another thread.</summary>
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint to, bool on);

    /// <summary>The synchronous key state.</summary>
    [DllImport("user32.dll")] public static extern short GetKeyState(int vk);

    /// <summary>Creates a message queue for the thread.</summary>
    [DllImport("user32.dll")] public static extern bool PeekMessageW(out MSG message, nint window, uint min, uint max, uint remove);

    /// <summary>Gets a message.</summary>
    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG message, nint window, uint min, uint max);

    /// <summary>Dispatches a message.</summary>
    [DllImport("user32.dll")] public static extern nint DispatchMessageW(ref MSG message);

    /// <summary>Posts to a thread.</summary>
    [DllImport("user32.dll")] public static extern bool PostThreadMessageW(uint thread, int message, nint wParam, nint lParam);

    /// <summary>Installs a hook.</summary>
    [DllImport("user32.dll", SetLastError = true)] public static extern nint SetWindowsHookExW(int id, HookProc proc, nint module, uint thread);

    /// <summary>Removes a hook.</summary>
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint hook);

    /// <summary>Calls the next hook.</summary>
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    /// <summary>Creates a window.</summary>
    /// <param name="exStyle">Extended styles.</param>
    /// <param name="className">The window class.</param>
    /// <param name="title">The title.</param>
    /// <param name="style">Styles.</param>
    /// <param name="x">Left.</param>
    /// <param name="y">Top.</param>
    /// <param name="w">Width.</param>
    /// <param name="h">Height.</param>
    /// <param name="parent">The parent (HWND_MESSAGE for a message-only window).</param>
    /// <param name="menu">The menu or child id.</param>
    /// <param name="instance">The module.</param>
    /// <param name="param">Creation data.</param>
    /// <returns>The window, or 0.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    /// <summary>Destroys a window.</summary>
    [DllImport("user32.dll")] public static extern bool DestroyWindow(nint window);

    /// <summary>Opens the clipboard.</summary>
    [DllImport("user32.dll")] public static extern bool OpenClipboard(nint owner);

    /// <summary>Empties the clipboard.</summary>
    [DllImport("user32.dll")] public static extern bool EmptyClipboard();

    /// <summary>Sets clipboard data.</summary>
    [DllImport("user32.dll")] public static extern nint SetClipboardData(uint format, nint memory);

    /// <summary>Closes the clipboard.</summary>
    [DllImport("user32.dll")] public static extern bool CloseClipboard();

    /// <summary>Allocates global memory.</summary>
    [DllImport("kernel32.dll")] public static extern nint GlobalAlloc(uint flags, nuint bytes);

    /// <summary>Locks global memory.</summary>
    [DllImport("kernel32.dll")] public static extern nint GlobalLock(nint memory);

    /// <summary>Unlocks global memory.</summary>
    [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(nint memory);

    /// <summary>A module's handle.</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);

    /// <summary>The calling thread's id.</summary>
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    /// <summary>Opens a process.</summary>
    [DllImport("kernel32.dll", SetLastError = true)] public static extern nint OpenProcess(uint access, bool inherit, uint pid);

    /// <summary>Closes a handle.</summary>
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(nint handle);

    /// <summary>Suspends every thread of a process.</summary>
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(nint process);

    /// <summary>Resumes every thread of a process.</summary>
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(nint process);

    /// <summary>1-ms timer resolution.</summary>
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint period);

    /// <summary>Ends the 1-ms timer resolution.</summary>
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint period);
}
