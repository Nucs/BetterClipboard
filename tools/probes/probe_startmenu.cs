#:property PublishAot=false
#:property AllowUnsafeBlocks=true
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property EnableDefaultPageItems=false
#:property EnableDefaultApplicationDefinition=false
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Microsoft.Win32;
using static Native;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────
// Probe: how the Windows 11 Start menu works — what the taskbar's Start button is, what it opens, and how the Start
// window appears and goes away. CLAUDE.md §1.10 has the findings, and the static analysis (public symbols, WinRT
// metadata) behind the WNF state names used here.
//
// Two modes, both passive: they open, click, type and inject nothing, call nothing in Explorer, and are safe on any PC.
//   --inventory       (default) a snapshot: the Start host process (package, trust, parent, threads), the Start and
//                     Search windows (z-band, cloak state, rectangle), the other shell hosts, the taskbar's windows, the
//                     Start button as UI Automation sees it, the two launcher-visibility WNF states, Start's settings
//                     and files.
//   --watch SECONDS   an event log while a person (or a test desktop's input tools) opens and closes Start: window events
//                     of the Start and Search hosts (create, show, cloak, uncloak, move, foreground; an out-of-context
//                     WinEvent hook, nothing injected), shell-hook messages (HSHELL_TASKMAN …, through a hidden window),
//                     both WNF states, the Start host's thread states (suspended or not), and an "input" line whenever
//                     the session's last-input time moves (GetLastInputInfo), the mark to time Start from.
//
//   dotnet run tools/probes/probe_startmenu.cs -- [--inventory | --watch 60]
//   In a claude-desktops Windows desktop: dotnet publish tools/probes/probe_startmenu.cs -r win-x64 --self-contained,
//   copy the folder in, start the watch with the desktop's launch tool (cmd /c "… --watch 120 > C:\probe\watch.txt"),
//   then open and close Start with its press_keys / click tools and read the file back.
//
// Method notes:
// - EnumWindows does not list Start's window: "For Windows 8 and later, EnumWindows enumerates only top-level windows of
//   desktop apps" (its documentation). Start and Search are immersive CoreWindows (z-band 6), so this probe walks the
//   top-level windows with FindWindowEx(NULL, previous, NULL, NULL) instead, which does return them.
// - Content-free: no clipboard, no file contents; window titles only of the shell's own windows (never another app's),
//   for other apps only the process name and window class.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────

string mode = "inventory";
int watchSeconds = 60;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--inventory":
            mode = "inventory";
            break;
        case "--watch" when i + 1 < args.Length:
            mode = "watch";
            watchSeconds = int.Parse(args[++i]);
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

// Physical pixels everywhere: window rectangles and UI Automation's bounds then agree at any display scale.
SetProcessDpiAwarenessContext(-4);
Console.OutputEncoding = Encoding.UTF8;
if (mode == "watch")
{
    using (Observer.Start())
    {
        Console.WriteLine($"Watching for {watchSeconds} s: open and close Start now (times are from the start of the watch).");
        Thread.Sleep(TimeSpan.FromSeconds(watchSeconds));
    }

    return 0;
}

Inventory.Run();
return 0;

/// <summary>
/// <c>--inventory</c>: everything that can be read about Start without touching it. Each section prints facts only
/// (names, versions, states, rectangles, counts), so its output is safe to paste into notes.
/// </summary>
static class Inventory
{
    /// <summary>Prints every section in turn; a section that fails prints its error and the next one still runs.</summary>
    public static void Run()
    {
        Section("Windows", PrintWindowsVersion);
        Section("Start host", PrintStartHost);
        Section("Start and Search windows", PrintShellViews);
        Section("Other shell hosts", PrintOtherHosts);
        Section("Taskbar", PrintTaskbar);
        Section("Start button (UI Automation)", PrintStartButton);
        Section("Launcher visibility (WNF)", PrintWnf);
        Section("Settings", PrintSettings);
        Section("Files", PrintFiles);
    }

    /// <summary>Runs one section under a heading and keeps going after an exception (a probe should report, not stop).</summary>
    /// <param name="title">The heading.</param>
    /// <param name="body">The section's printer.</param>
    private static void Section(string title, Action body)
    {
        Console.WriteLine($"== {title}");
        try
        {
            body();
        }
        catch (Exception error)
        {
            Console.WriteLine($"  failed: {error.GetType().Name}: {error.Message}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// The build and update revision, which every finding depends on. The edition comes from EditionID: ProductName still
    /// says "Windows 10" on Windows 11.
    /// </summary>
    private static void PrintWindowsVersion()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string build = key?.GetValue("CurrentBuild") as string ?? "?";
        string windows = int.TryParse(build, out int number) && number >= 22000 ? "Windows 11" : "Windows 10";
        Console.WriteLine($"  {windows} {key?.GetValue("EditionID")} {key?.GetValue("DisplayVersion")}, build {build}.{key?.GetValue("UBR")}");
    }

    /// <summary>The Start host: identity, trust, parent, threads by state, and every window it owns.</summary>
    private static void PrintStartHost()
    {
        var host = Hosts.Find(Hosts.StartHostName);
        if (host is null)
        {
            Console.WriteLine("  StartMenuExperienceHost.exe is not running.");
            return;
        }

        PrintProcess(host);
        PrintWindowsOf(host.Id);
    }

    /// <summary>
    /// The two windows a Start open shows, found the way the shell's own tools find them (class and title), and how many
    /// windows EnumWindows would have reported for their processes (none: it skips immersive windows).
    /// </summary>
    private static void PrintShellViews()
    {
        foreach (string title in new[] { "Start", "Search" })
        {
            nint window = FindWindowEx(0, 0, "Windows.UI.Core.CoreWindow", title);
            if (window == 0)
            {
                Console.WriteLine($"  CoreWindow '{title}': none");
                continue;
            }

            var info = WindowInfo.Of(window);
            Console.WriteLine($"  {Hosts.NameOf((uint)info.ProcessId)}: {info.Describe(withTitle: true)}");
            Console.WriteLine($"    EnumWindows lists {WindowInfo.EnumWindowsCount(info.ProcessId)} window(s) of that process");
        }
    }

    /// <summary>The other shell experience hosts, for comparison: their windows, cloaked or not, and whether they sleep.</summary>
    private static void PrintOtherHosts()
    {
        foreach (string name in new[] { "SearchHost", "ShellExperienceHost", "ShellHost", "TextInputHost" })
        {
            var process = Hosts.Find(name);
            if (process is null)
            {
                Console.WriteLine($"  {name}: not running");
                continue;
            }

            Console.WriteLine($"  {name} pid {process.Id}: threads {Hosts.ThreadStates(process)}");
            foreach (var window in WindowInfo.TopLevelOf(process.Id))
            {
                Console.WriteLine($"    {window.Describe(withTitle: true)}");
            }
        }
    }

    /// <summary>The primary taskbar, its child windows (the hidden "Start" pearl among them) and the shell's special windows.</summary>
    private static void PrintTaskbar()
    {
        nint tray = Taskbar.Tray();
        GetWindowThreadProcessId(tray, out uint shellPid);
        Console.WriteLine($"  Shell_TrayWnd in explorer pid {shellPid}: {WindowInfo.Of(tray).Describe(withTitle: false)}");
        int secondary = 0;
        nint next = 0;
        while ((next = FindWindowEx(0, next, "Shell_SecondaryTrayWnd", null)) != 0)
        {
            secondary++;
        }

        Console.WriteLine($"  secondary taskbars: {secondary}");
        foreach (var child in WindowInfo.ChildrenOf(tray))
        {
            Console.WriteLine($"    child {child.Describe(withTitle: true)}");
        }

        foreach (var (label, handle) in new[] { ("shell window", GetShellWindow()), ("taskman window", GetTaskmanWindow()), ("progman window", GetProgmanWindow()) })
        {
            GetWindowThreadProcessId(handle, out uint pid);
            Console.WriteLine($"  {label}: 0x{handle:X} '{WindowInfo.ClassOf(handle)}' in {Hosts.NameOf(pid)} pid {pid}");
        }
    }

    /// <summary>The Start button's UI Automation element: what a screen reader and a test can find and press.</summary>
    private static void PrintStartButton()
    {
        var button = Taskbar.StartButton();
        if (button is null)
        {
            Console.WriteLine("  No element with AutomationId 'StartButton' under Shell_TrayWnd.");
            return;
        }

        var current = button.Current;
        Console.WriteLine($"  name '{current.Name}', class '{current.ClassName}', control type {current.ControlType.ProgrammaticName}, framework '{current.FrameworkId}'");
        Console.WriteLine($"  bounds {Format(current.BoundingRectangle)}, keyboard focusable {current.IsKeyboardFocusable}, native window 0x{current.NativeWindowHandle:X}");
        Console.WriteLine($"  patterns: {string.Join(", ", button.GetSupportedPatterns().Select(p => p.ProgrammaticName.Replace("PatternIdentifiers.Pattern", string.Empty)))}");
        if (button.TryGetCurrentPattern(TogglePattern.Pattern, out object toggle))
        {
            Console.WriteLine($"  toggle state: {((TogglePattern)toggle).Current.ToggleState}");
        }

        var parent = TreeWalker.ControlViewWalker.GetParent(button);
        if (parent is not null)
        {
            Console.WriteLine($"  parent: class '{parent.Current.ClassName}', automation id '{parent.Current.AutomationId}'");
        }
    }

    /// <summary>The current data of the two launcher-visibility WNF states (CLAUDE.md §1.10 has their names and values).</summary>
    private static void PrintWnf()
    {
        foreach (var (name, value) in Wnf.Names)
        {
            var (status, stamp, data) = Wnf.Query(value);
            Console.WriteLine(status == 0
                ? $"  {name} (0x{value:X16}): stamp {stamp}, {data.Length} bytes: {Convert.ToHexString(data)}"
                : $"  {name} (0x{value:X16}): NtQueryWnfStateData 0x{status:X8}");
        }
    }

    /// <summary>The Explorer and Start values that change how Start looks or where it opens (numbers only).</summary>
    private static void PrintSettings()
    {
        PrintValues(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", name => name.StartsWith("Start", StringComparison.Ordinal) || name is "TaskbarAl" or "TaskbarDa" or "TaskbarMn");
        PrintValues(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Start", _ => true);
        PrintValues(Registry.CurrentUser, @"Software\Policies\Microsoft\Windows\Explorer", name => name.Contains("Start", StringComparison.OrdinalIgnoreCase));
        PrintValues(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", name => name.Contains("Start", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Prints the numeric and short string values of one key that pass a name filter (binary values as sizes).</summary>
    /// <param name="root">The hive.</param>
    /// <param name="path">The key under it.</param>
    /// <param name="wanted">Which value names to print.</param>
    private static void PrintValues(RegistryKey root, string path, Func<string, bool> wanted)
    {
        using var key = root.OpenSubKey(path);
        if (key is null)
        {
            Console.WriteLine($"  {path}: missing");
            return;
        }

        var names = key.GetValueNames().Where(wanted).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Console.WriteLine($"  {path}: {(names.Count == 0 ? "no such values" : string.Empty)}");
        foreach (string name in names)
        {
            object? value = key.GetValue(name);
            Console.WriteLine($"    {name} = {(value is byte[] bytes ? $"<{bytes.Length} bytes>" : value)}");
        }
    }

    /// <summary>The binaries that make Start (names, versions) and the Start host's own state files (names, sizes).</summary>
    private static void PrintFiles()
    {
        string apps = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\SystemApps");
        foreach (string file in new[]
        {
            @"Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe",
            @"Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartDocked.dll",
            @"MicrosoftWindows.Client.Core_cw5n1h2txyewy\StartMenu.dll",
            @"MicrosoftWindows.Client.Core_cw5n1h2txyewy\Taskbar.View.dll",
            @"MicrosoftWindows.Client.Photon_cw5n1h2txyewy\Microsoft.ReactNative.dll",
        })
        {
            string path = Path.Combine(apps, file);
            Console.WriteLine(File.Exists(path)
                ? $"  {file}: {FileVersionInfo.GetVersionInfo(path).FileVersion}, {new FileInfo(path).Length / 1024} KB"
                : $"  {file}: missing");
        }

        string local = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Packages\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\LocalState");
        if (Directory.Exists(local))
        {
            foreach (var entry in new DirectoryInfo(local).EnumerateFileSystemInfos())
            {
                Console.WriteLine($"  LocalState\\{entry.Name}: {(entry is FileInfo file ? $"{file.Length} bytes" : "folder")}, written {entry.LastWriteTimeUtc:yyyy-MM-dd HH:mm}Z");
            }
        }
    }

    /// <summary>Identity and state of one host process.</summary>
    /// <param name="process">The process.</param>
    private static void PrintProcess(Process process)
    {
        var facts = Hosts.Facts(process);
        Console.WriteLine($"  pid {process.Id}, started {process.StartTime.ToUniversalTime():yyyy-MM-dd HH:mm:ss}Z, working set {process.WorkingSet64 / (1024 * 1024)} MB");
        Console.WriteLine($"  image {facts.Image}");
        Console.WriteLine($"  package {facts.Package ?? "(none)"}, app {facts.AppUserModelId ?? "(none)"}");
        Console.WriteLine($"  integrity {facts.Integrity}, AppContainer {facts.AppContainer}, parent {facts.ParentName} pid {facts.ParentId}");
        Console.WriteLine($"  threads: {Hosts.ThreadStates(process)}");
    }

    /// <summary>Every top-level window of a process (immersive ones included), and how many message-only windows it has.</summary>
    /// <param name="pid">The process.</param>
    private static void PrintWindowsOf(int pid)
    {
        var windows = WindowInfo.TopLevelOf(pid);
        Console.WriteLine($"  top-level windows: {windows.Count} (EnumWindows lists {WindowInfo.EnumWindowsCount(pid)})");
        foreach (var window in windows)
        {
            Console.WriteLine($"    {window.Describe(withTitle: true)}");
        }

        var messageOnly = WindowInfo.MessageOnlyOf(pid);
        Console.WriteLine($"  message-only windows: {messageOnly.Count} ({string.Join(", ", messageOnly.Select(w => w.Class).Distinct())})");
    }

    /// <summary>A UI Automation rectangle as left, top, width x height in pixels.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <returns>The text.</returns>
    private static string Format(System.Windows.Rect rect) => $"({rect.Left:0}, {rect.Top:0}) {rect.Width:0} x {rect.Height:0}";
}

/// <summary>Facts about one top-level or child window, read once (a snapshot, not a live view).</summary>
/// <param name="Handle">The window.</param>
/// <param name="ProcessId">Its process.</param>
/// <param name="Class">Its window class.</param>
/// <param name="Title">Its title (callers print it only for the shell's own windows).</param>
/// <param name="Visible">WS_VISIBLE (a cloaked window keeps it).</param>
/// <param name="Cloak">DWMWA_CLOAKED: 0, or 1 = by the app, 2 = by the shell, 4 = inherited from its owner.</param>
/// <param name="Band">The z-order band (GetWindowBand): 1 = desktop apps, higher = the shell's immersive bands.</param>
/// <param name="Rect">GetWindowRect, in physical pixels.</param>
/// <param name="Style">GWL_STYLE.</param>
/// <param name="ExStyle">GWL_EXSTYLE.</param>
sealed record WindowInfo(nint Handle, int ProcessId, string Class, string Title, bool Visible, int Cloak, uint Band, RECT Rect, long Style, long ExStyle)
{
    /// <summary>Reads a window's facts now.</summary>
    /// <param name="handle">The window.</param>
    /// <returns>The snapshot (empty strings and zeros for a window that is already gone).</returns>
    public static WindowInfo Of(nint handle)
    {
        GetWindowThreadProcessId(handle, out uint pid);
        DwmGetWindowAttribute(handle, DWMWA_CLOAKED, out int cloak, sizeof(int));
        GetWindowBand(handle, out uint band);
        GetWindowRect(handle, out RECT rect);
        return new WindowInfo(handle, (int)pid, ClassOf(handle), TitleOf(handle), IsWindowVisible(handle), cloak, band, rect,
            GetWindowLongPtr(handle, GWL_STYLE), GetWindowLongPtr(handle, GWL_EXSTYLE));
    }

    /// <summary>
    /// Every top-level window of one process, immersive ones included, in z-order: FindWindowEx with a null class and
    /// title walks all of them, where EnumWindows skips the shell's immersive windows (see the file header).
    /// </summary>
    /// <param name="pid">The process.</param>
    /// <returns>The windows.</returns>
    public static List<WindowInfo> TopLevelOf(int pid)
    {
        var found = new List<WindowInfo>();
        nint next = 0;
        while ((next = FindWindowEx(0, next, null, null)) != 0)
        {
            GetWindowThreadProcessId(next, out uint owner);
            if (owner == pid)
            {
                found.Add(Of(next));
            }
        }

        return found;
    }

    /// <summary>How many top-level windows of a process EnumWindows reports (to show what it leaves out).</summary>
    /// <param name="pid">The process.</param>
    /// <returns>The count.</returns>
    public static int EnumWindowsCount(int pid)
    {
        int count = 0;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out uint owner);
            if (owner == pid)
            {
                count++;
            }

            return true;
        }, 0);
        return count;
    }

    /// <summary>The message-only windows of one process (HWND_MESSAGE children: never on screen, but proof of live threads).</summary>
    /// <param name="pid">The process.</param>
    /// <returns>The windows.</returns>
    public static List<WindowInfo> MessageOnlyOf(int pid)
    {
        var found = new List<WindowInfo>();
        nint next = 0;
        while ((next = FindWindowEx(HWND_MESSAGE, next, null, null)) != 0)
        {
            GetWindowThreadProcessId(next, out uint owner);
            if (owner == pid)
            {
                found.Add(Of(next));
            }
        }

        return found;
    }

    /// <summary>The direct and indirect child windows of a window.</summary>
    /// <param name="parent">The window.</param>
    /// <returns>The children, in EnumChildWindows order.</returns>
    public static List<WindowInfo> ChildrenOf(nint parent)
    {
        var found = new List<WindowInfo>();
        EnumChildWindows(parent, (handle, _) =>
        {
            found.Add(Of(handle));
            return true;
        }, 0);
        return found;
    }

    /// <summary>A window's class name.</summary>
    /// <param name="handle">The window.</param>
    /// <returns>The class, or an empty string.</returns>
    public static string ClassOf(nint handle)
    {
        var text = new StringBuilder(256);
        GetClassName(handle, text, text.Capacity);
        return text.ToString();
    }

    /// <summary>A window's title.</summary>
    /// <param name="handle">The window.</param>
    /// <returns>The title, or an empty string.</returns>
    public static string TitleOf(nint handle)
    {
        var text = new StringBuilder(256);
        GetWindowText(handle, text, text.Capacity);
        return text.ToString();
    }

    /// <summary>The band's name from the shell's ZBID enumeration (community research, ADeltaX 2020).</summary>
    /// <param name="band">The band number.</param>
    /// <returns>The name, or the number.</returns>
    public static string BandName(uint band) => band switch
    {
        0 => "DEFAULT",
        1 => "DESKTOP",
        2 => "UIACCESS",
        3 => "IMMERSIVE_IHM",
        4 => "IMMERSIVE_NOTIFICATION",
        5 => "IMMERSIVE_APPCHROME",
        6 => "IMMERSIVE_MOGO",
        7 => "IMMERSIVE_EDGY",
        8 => "IMMERSIVE_INACTIVEMOBODY",
        9 => "IMMERSIVE_INACTIVEDOCK",
        10 => "IMMERSIVE_ACTIVEMOBODY",
        11 => "IMMERSIVE_ACTIVEDOCK",
        12 => "IMMERSIVE_BACKGROUND",
        13 => "IMMERSIVE_SEARCH",
        14 => "GENUINE_WINDOWS",
        15 => "IMMERSIVE_RESTRICTED",
        16 => "SYSTEM_TOOLS",
        17 => "LOCK",
        18 => "ABOVELOCK_UX",
        _ => band.ToString(),
    };

    /// <summary>One line with every fact; the title only when asked (the shell's own windows).</summary>
    /// <param name="withTitle">Whether to include the title.</param>
    /// <returns>The line.</returns>
    public string Describe(bool withTitle)
    {
        string cloak = Cloak switch { 0 => "no", 1 => "app", 2 => "shell", 4 => "inherited", _ => Cloak.ToString() };
        string title = withTitle ? $" '{Title}'" : string.Empty;
        return $"0x{Handle:X8} '{Class}'{title} visible {(Visible ? "yes" : "no")}, cloaked {cloak}, band {BandName(Band)}, "
            + $"({Rect.Left}, {Rect.Top}) {Rect.Right - Rect.Left} x {Rect.Bottom - Rect.Top}, style 0x{Style:X8}, ex 0x{ExStyle:X8}";
    }
}

/// <summary>The shell's host processes: finding them, and what they are.</summary>
static class Hosts
{
    /// <summary>The Start host's process name.</summary>
    public const string StartHostName = "StartMenuExperienceHost";

    /// <summary>Process names by id with the time they were looked up (Stopwatch ticks), for <see cref="CachedNameOf"/>.</summary>
    private static readonly ConcurrentDictionary<uint, (string Name, long Ticks)> Names = new();

    /// <summary>The process with this name in this session, or <see langword="null"/>.</summary>
    /// <param name="name">The process name without ".exe".</param>
    /// <returns>The process.</returns>
    public static Process? Find(string name)
    {
        int session = Process.GetCurrentProcess().SessionId;
        return Process.GetProcessesByName(name).FirstOrDefault(p => p.SessionId == session);
    }

    /// <summary>A process's name from the system process list (needs no handle, so it works for any process).</summary>
    /// <param name="pid">The process id.</param>
    /// <returns>The name, or "?" when it is gone.</returns>
    public static string NameOf(uint pid)
    {
        try
        {
            return Process.GetProcessById((int)pid).ProcessName;
        }
        catch (ArgumentException)
        {
            return "?";
        }
    }

    /// <summary>
    /// <see cref="NameOf"/> remembered for 5 s: each lookup walks the whole process list (~1 ms), and WinEvents come in
    /// bursts. A process id reused within those 5 s would show the old name, a risk a short probe run accepts.
    /// </summary>
    /// <param name="pid">The process id.</param>
    /// <returns>The name, or "?" when it is gone.</returns>
    public static string CachedNameOf(uint pid)
    {
        if (Names.TryGetValue(pid, out var known) && Stopwatch.GetElapsedTime(known.Ticks).TotalSeconds < 5)
        {
            return known.Name;
        }

        string name = NameOf(pid);
        Names[pid] = (name, Stopwatch.GetTimestamp());
        return name;
    }

    /// <summary>Thread counts by state, e.g. "Wait:UserRequest 31, Wait:Suspended 7": all suspended = frozen by the PLM.</summary>
    /// <param name="process">The process (refreshed here).</param>
    /// <returns>The summary.</returns>
    public static string ThreadStates(Process process)
    {
        process.Refresh();
        var states = process.Threads.Cast<ProcessThread>().GroupBy(StateOf).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}");
        return $"{process.Threads.Count} ({string.Join(", ", states)})";
    }

    /// <summary>How many of a process's threads are suspended, and how many it has.</summary>
    /// <param name="process">The process (refreshed here).</param>
    /// <returns>The two counts.</returns>
    public static (int Suspended, int Total) SuspendedCount(Process process)
    {
        process.Refresh();
        var threads = process.Threads.Cast<ProcessThread>().ToList();
        return (threads.Count(t => StateOf(t) == "Wait:Suspended"), threads.Count);
    }

    /// <summary>One thread's state as text ("Running", "Wait:Suspended", …), "gone" when it ended meanwhile.</summary>
    /// <param name="thread">The thread.</param>
    /// <returns>The state.</returns>
    private static string StateOf(ProcessThread thread)
    {
        try
        {
            return thread.ThreadState == System.Diagnostics.ThreadState.Wait ? $"Wait:{thread.WaitReason}" : thread.ThreadState.ToString();
        }
        catch (InvalidOperationException)
        {
            return "gone";
        }
    }

    /// <summary>The image path, package, app id, integrity, AppContainer flag and parent of a process.</summary>
    /// <param name="process">The process.</param>
    /// <returns>The facts ("?" where a query was refused).</returns>
    public static ProcessFacts Facts(Process process)
    {
        nint handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, process.Id);
        if (handle == 0)
        {
            return new ProcessFacts("?", null, null, "?", "?", 0, "?");
        }

        try
        {
            var image = new StringBuilder(1024);
            uint size = (uint)image.Capacity;
            QueryFullProcessImageName(handle, 0, image, ref size);
            var package = new StringBuilder(512);
            uint packageLength = (uint)package.Capacity;
            string? packageName = GetPackageFullName(handle, ref packageLength, package) == 0 ? package.ToString() : null;
            var app = new StringBuilder(512);
            uint appLength = (uint)app.Capacity;
            string? appId = GetApplicationUserModelId(handle, ref appLength, app) == 0 ? app.ToString() : null;
            var (integrity, appContainer) = TokenFacts(handle);
            var basic = default(PROCESS_BASIC_INFORMATION);
            int parent = NtQueryInformationProcess(handle, 0, ref basic, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) == 0
                ? (int)basic.InheritedFromUniqueProcessId
                : 0;
            return new ProcessFacts(image.ToString(), packageName, appId, integrity, appContainer, parent, parent == 0 ? "?" : NameOf((uint)parent));
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>The integrity level and the AppContainer flag of a process's token.</summary>
    /// <param name="process">A handle with PROCESS_QUERY_LIMITED_INFORMATION.</param>
    /// <returns>The level ("Low", "Medium", "High", "System" or a number) and "yes"/"no" ("?" when refused).</returns>
    private static unsafe (string Integrity, string AppContainer) TokenFacts(nint process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out nint token))
        {
            return ("?", "?");
        }

        try
        {
            int isAppContainer = 0;
            string appContainer = GetTokenInformation(token, 29 /* TokenIsAppContainer */, &isAppContainer, sizeof(int), out _)
                ? (isAppContainer != 0 ? "yes" : "no")
                : "?";
            byte* buffer = stackalloc byte[256];
            if (!GetTokenInformation(token, 25 /* TokenIntegrityLevel */, buffer, 256, out _))
            {
                return ("?", appContainer);
            }

            // TOKEN_MANDATORY_LABEL starts with SID_AND_ATTRIBUTES, whose first member points at the label's SID; its last
            // sub-authority is the level (0x1000 low, 0x2000 medium, 0x3000 high, 0x4000 system).
            nint sid = *(nint*)buffer;
            byte count = *GetSidSubAuthorityCount(sid);
            uint level = *GetSidSubAuthority(sid, (uint)(count - 1));
            string name = level switch { 0x1000 => "Low", 0x2000 => "Medium", 0x2100 => "Medium+", 0x3000 => "High", 0x4000 => "System", _ => $"0x{level:X}" };
            return (name, appContainer);
        }
        finally
        {
            CloseHandle(token);
        }
    }
}

/// <summary>What <see cref="Hosts.Facts"/> found out about a process.</summary>
/// <param name="Image">Its executable path.</param>
/// <param name="Package">Its package full name, or <see langword="null"/> for an unpackaged process.</param>
/// <param name="AppUserModelId">Its app id, or <see langword="null"/>.</param>
/// <param name="Integrity">Its integrity level.</param>
/// <param name="AppContainer">"yes" when it runs in an AppContainer.</param>
/// <param name="ParentId">The process that started it.</param>
/// <param name="ParentName">That process's name.</param>
sealed record ProcessFacts(string Image, string? Package, string? AppUserModelId, string Integrity, string AppContainer, int ParentId, string ParentName);

/// <summary>The taskbar and its Start button.</summary>
static class Taskbar
{
    /// <summary>The primary taskbar's window.</summary>
    /// <returns>Shell_TrayWnd, or 0 when Explorer's taskbar is not running.</returns>
    public static nint Tray() => FindWindow("Shell_TrayWnd", null);

    /// <summary>
    /// The Start button as UI Automation sees it: the taskbar's XAML element with AutomationId "StartButton" (an
    /// ExperienceToggleButton in Taskbar.View.dll, CLAUDE.md §1.10).
    /// </summary>
    /// <returns>The element, or <see langword="null"/> when the taskbar has none.</returns>
    public static AutomationElement? StartButton()
    {
        nint tray = Tray();
        if (tray == 0)
        {
            return null;
        }

        var root = AutomationElement.FromHandle(tray);
        return root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"));
    }
}

/// <summary>
/// Windows Notification Facility states the shell publishes when Start opens or closes. The names and values were read
/// from twinui.pcshell.dll's public symbols (CLAUDE.md §1.10); the data layout is not documented, so it is printed raw.
/// </summary>
static class Wnf
{
    /// <summary>The launcher-visibility state twinui.pcshell publishes (XamlLauncher::PublishLauncherVisibilityState).</summary>
    public const ulong LauncherVisibility = 0x0F950324A3BC1035;

    /// <summary>The "Start visibility changed" state, also in twinui.pcshell.</summary>
    public const ulong StartVisibilityChanged = 0x0D83063EA3BCB035;

    /// <summary>Both states with their symbol names, for printing.</summary>
    public static readonly (string Name, ulong Value)[] Names =
    [
        ("WNF_IMSN_LAUNCHERVISIBILITY", LauncherVisibility),
        ("WNF_SHEL_START_VISIBILITY_CHANGED", StartVisibilityChanged),
    ];

    /// <summary>Reads a state's current data (a read: nothing is published or changed).</summary>
    /// <param name="name">The state name.</param>
    /// <returns>The NTSTATUS (0 = read), the change stamp and the data.</returns>
    public static unsafe (int Status, uint Stamp, byte[] Data) Query(ulong name)
    {
        byte* buffer = stackalloc byte[4096];
        uint size = 4096;
        int status = NtQueryWnfStateData(ref name, 0, 0, out uint stamp, buffer, ref size);
        return (status, stamp, status == 0 ? new ReadOnlySpan<byte>(buffer, (int)size).ToArray() : []);
    }
}

/// <summary>One thing the observer saw, with its Stopwatch timestamp.</summary>
/// <param name="Ticks">When (Stopwatch ticks).</param>
/// <param name="Kind">What: "create", "show", "hide", "uncloak", "cloak", "move", "destroy", "foreground", "shellhook", "wnf", "host", "input".</param>
/// <param name="Detail">The facts, content-free.</param>
sealed record ObservedEvent(long Ticks, string Kind, string Detail);

/// <summary>
/// Passive observation of Start: an out-of-context WinEvent hook (window events of the Start and Search hosts, and every
/// foreground change), a hidden shell-hook window (HSHELL_* messages, HSHELL_TASKMAN included), subscriptions to the two
/// WNF states, a 20-ms sampler of the Start host's threads (suspended or not; a new process after a restart), and a
/// 1-ms poll of the session's last-input time. Nothing is injected into any process. Each event is printed as it arrives,
/// with its time since the start of the watch.
/// </summary>
sealed class Observer : IDisposable
{
    /// <summary>The observer whose callbacks run (WinEvent and WNF callbacks are static; there is one observer at a time).</summary>
    private static Observer? current;

    /// <summary>The WinEvent callback, kept alive while hooked (a collected delegate crashes the process).</summary>
    private static WinEventProc? winEventProc;

    /// <summary>The shell-hook window's procedure, kept alive while the window exists.</summary>
    private static WndProc? windowProc;

    /// <summary>When the observation started (Stopwatch ticks), the origin of the printed times.</summary>
    private readonly long origin = Stopwatch.GetTimestamp();

    /// <summary>The WNF subscriptions, cancelled by <see cref="Dispose"/>.</summary>
    private readonly List<nint> subscriptions = [];

    /// <summary>The thread that owns the hooks and the shell-hook window and pumps their messages.</summary>
    private Thread? hookThread;

    /// <summary>That thread's id, for WM_QUIT.</summary>
    private uint hookThreadId;

    /// <summary>The threads that sample the Start host and poll the last-input time.</summary>
    private Thread? samplerThread, inputThread;

    /// <summary>Set by <see cref="Dispose"/>: the sampler's and the input poll's stop signal.</summary>
    private volatile bool stopping;

    /// <summary>The registered shell-hook message ("SHELLHOOK").</summary>
    private uint shellHookMessage;

    /// <summary>The Start host's current process id (0 while it is not running); the sampler updates it.</summary>
    private volatile int startHostPid;

    /// <summary>Creates an observer; <see cref="Start"/> wires it up.</summary>
    private Observer()
    {
    }

    /// <summary>Starts observing and returns once the hooks are in place.</summary>
    /// <returns>The observer; dispose it to stop.</returns>
    /// <exception cref="InvalidOperationException">The hook thread could not create its window or hooks.</exception>
    public static unsafe Observer Start()
    {
        var observer = new Observer();
        current = observer;
        observer.startHostPid = Hosts.Find(Hosts.StartHostName)?.Id ?? 0;
        using var ready = new ManualResetEventSlim();
        string? failure = null;
        observer.hookThread = new Thread(() =>
        {
            observer.hookThreadId = GetCurrentThreadId();
            try
            {
                observer.InstallHooks();
            }
            catch (Exception error)
            {
                failure = error.Message;
            }

            ready.Set();
            if (failure is not null)
            {
                return;
            }

            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        })
        { IsBackground = true, Name = "Observer hooks" };
        observer.hookThread.SetApartmentState(ApartmentState.STA);
        observer.hookThread.Start();
        ready.Wait();
        if (failure is not null)
        {
            throw new InvalidOperationException(failure);
        }

        foreach (var (_, name) in Wnf.Names)
        {
            // The current stamp is passed, so the callback reports changes only, not the state as it is now.
            var (_, stamp, _) = Wnf.Query(name);
            if (RtlSubscribeWnfStateChangeNotification(out nint subscription, name, stamp, &OnWnf, 0, 0, 0, 0) == 0)
            {
                observer.subscriptions.Add(subscription);
            }
            else
            {
                observer.Add("wnf", $"could not subscribe to 0x{name:X16}");
            }
        }

        observer.samplerThread = new Thread(observer.SampleHost) { IsBackground = true, Name = "Observer sampler" };
        observer.samplerThread.Start();
        observer.inputThread = new Thread(observer.PollInput) { IsBackground = true, Name = "Observer input" };
        observer.inputThread.Start();
        return observer;
    }

    /// <summary>
    /// Creates the hidden shell-hook window and the WinEvent hooks on the calling thread (they deliver to this thread's
    /// message queue, which the caller then pumps).
    /// </summary>
    /// <exception cref="InvalidOperationException">The window or a hook could not be created.</exception>
    private void InstallHooks()
    {
        windowProc = WindowProcedure;
        var windowClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(windowProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "BetterClipboardStartProbeShellHook",
        };
        RegisterClassEx(ref windowClass);
        // A hidden top-level popup (never shown): RegisterShellHookWindow wants a top-level window.
        nint window = CreateWindowEx(WS_EX_TOOLWINDOW, windowClass.lpszClassName!, "Start probe shell hook", WS_POPUP, 0, 0, 0, 0, 0, 0, windowClass.hInstance, 0);
        if (window == 0)
        {
            throw new InvalidOperationException("The shell-hook window could not be created.");
        }

        shellHookMessage = RegisterWindowMessage("SHELLHOOK");
        if (!RegisterShellHookWindow(window))
        {
            Add("shellhook", "RegisterShellHookWindow refused");
        }

        winEventProc = OnWinEvent;
        foreach (var (min, max) in new (uint, uint)[]
        {
            (EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND),
            (EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE),
            (EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE),
            (EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED),
        })
        {
            if (SetWinEventHook(min, max, 0, winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS) == 0)
            {
                throw new InvalidOperationException($"SetWinEventHook 0x{min:X}..0x{max:X} failed.");
            }
        }
    }

    /// <summary>
    /// The WinEvent callback: window events (OBJID_WINDOW, CHILDID_SELF) of the Start and Search hosts and of Explorer's
    /// popup menus, and every foreground change (other apps by process and class only).
    /// </summary>
    /// <param name="hook">The hook.</param>
    /// <param name="eventType">The EVENT_ constant.</param>
    /// <param name="hwnd">The window.</param>
    /// <param name="idObject">The object (0 = the window itself).</param>
    /// <param name="idChild">The child (0 = the object itself).</param>
    /// <param name="thread">The raising thread.</param>
    /// <param name="time">The event's tick count.</param>
    private static void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        var observer = current;
        if (observer is null || hwnd == 0 || idObject != 0 || idChild != 0)
        {
            return;
        }

        GetWindowThreadProcessId(hwnd, out uint pid);
        string process = Hosts.CachedNameOf(pid);
        bool startHost = pid != 0 && (pid == observer.startHostPid || process == Hosts.StartHostName);
        bool shellHost = startHost || process == "SearchHost";
        // Popup menus (#32768) of Explorer: the Start button's right-click (Win+X) menu shows as one.
        bool interesting = shellHost || (process == "explorer" && WindowInfo.ClassOf(hwnd) == "#32768");
        string kind = eventType switch
        {
            EVENT_SYSTEM_FOREGROUND => "foreground",
            EVENT_OBJECT_CREATE => "create",
            EVENT_OBJECT_DESTROY => "destroy",
            EVENT_OBJECT_SHOW => "show",
            EVENT_OBJECT_HIDE => "hide",
            EVENT_OBJECT_LOCATIONCHANGE => "move",
            EVENT_OBJECT_CLOAKED => "cloak",
            EVENT_OBJECT_UNCLOAKED => "uncloak",
            _ => $"0x{eventType:X}",
        };
        if (kind == "foreground")
        {
            // Another app's window: its process and class only, never its title (that can be a document's name).
            string cls = WindowInfo.ClassOf(hwnd);
            observer.Add(kind, interesting ? $"{process} {WindowInfo.Of(hwnd).Describe(withTitle: shellHost)}" : $"{process} '{cls}'");
            return;
        }

        if (!interesting)
        {
            return;
        }

        // A destroyed window has no facts left; everything else is described as it is at the event.
        observer.Add(kind, kind == "destroy" ? $"{process} 0x{hwnd:X8}" : $"{process} {WindowInfo.Of(hwnd).Describe(withTitle: shellHost)}");
    }

    /// <summary>The WNF callback (an ntdll worker thread): records the new data of a state.</summary>
    /// <param name="stateName">The state.</param>
    /// <param name="changeStamp">Its new stamp.</param>
    /// <param name="typeId">The type id (unused).</param>
    /// <param name="context">The context (unused).</param>
    /// <param name="buffer">The new data.</param>
    /// <param name="size">Its size.</param>
    /// <returns>0 (STATUS_SUCCESS).</returns>
    [UnmanagedCallersOnly]
    private static unsafe int OnWnf(ulong stateName, uint changeStamp, nint typeId, nint context, nint buffer, uint size)
    {
        try
        {
            var name = Wnf.Names.FirstOrDefault(n => n.Value == stateName).Name ?? $"0x{stateName:X16}";
            var data = size == 0 || buffer == 0 ? [] : new ReadOnlySpan<byte>((void*)buffer, (int)Math.Min(size, 256)).ToArray();
            current?.Add("wnf", $"{name} stamp {changeStamp}: {Convert.ToHexString(data)}");
        }
        catch
        {
            // An exception must not unwind into ntdll's worker; a lost line is the lesser evil.
        }

        return 0;
    }

    /// <summary>The shell-hook window's procedure: records HSHELL_* codes (with the window's process and class).</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="message">The message.</param>
    /// <param name="wParam">For the shell-hook message: the HSHELL_ code.</param>
    /// <param name="lParam">For the shell-hook message: usually a window.</param>
    /// <returns>The default procedure's result.</returns>
    private static nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        var observer = current;
        if (observer is not null && message == observer.shellHookMessage && message != 0)
        {
            int code = (int)wParam;
            string name = (code & 0x7FFF) switch
            {
                1 => "HSHELL_WINDOWCREATED",
                2 => "HSHELL_WINDOWDESTROYED",
                3 => "HSHELL_ACTIVATESHELLWINDOW",
                4 => (code & 0x8000) != 0 ? "HSHELL_RUDEAPPACTIVATED" : "HSHELL_WINDOWACTIVATED",
                5 => "HSHELL_GETMINRECT",
                6 => (code & 0x8000) != 0 ? "HSHELL_FLASH" : "HSHELL_REDRAW",
                7 => "HSHELL_TASKMAN",
                8 => "HSHELL_LANGUAGE",
                9 => "HSHELL_SYSMENU",
                10 => "HSHELL_ENDTASK",
                12 => "HSHELL_APPCOMMAND",
                13 => "HSHELL_WINDOWREPLACED",
                14 => "HSHELL_WINDOWREPLACING",
                16 => "HSHELL_MONITORCHANGED",
                _ => $"code 0x{code:X}",
            };
            string detail = name;
            if (code is 1 or 2 or 4 or 0x8004 or 6 or 0x8006 or 13 or 14 or 16 && lParam != 0)
            {
                GetWindowThreadProcessId(lParam, out uint pid);
                detail += $" {Hosts.CachedNameOf(pid)} '{WindowInfo.ClassOf(lParam)}'";
            }
            else if (code == 7)
            {
                detail += $" (lParam {lParam})";
            }

            observer.Add("shellhook", detail);
            return 0;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    /// <summary>
    /// Every 20 ms: notices the Start host starting or ending, and records when its threads become suspended or resume
    /// (a frozen UWP process has all its threads in Wait:Suspended).
    /// </summary>
    private void SampleHost()
    {
        int lastSuspended = -1;
        long lastLookup = 0;
        Process? host = startHostPid == 0 ? null : TryGet(startHostPid);
        while (!stopping)
        {
            if (host is null || host.HasExited)
            {
                if (host is not null)
                {
                    Add("host", $"StartMenuExperienceHost pid {host.Id} ended");
                    host = null;
                    startHostPid = 0;
                    lastSuspended = -1;
                }

                // Looking the process up by name walks every process: twice a second is enough to time a restart.
                if (Stopwatch.GetElapsedTime(lastLookup).TotalMilliseconds >= 500)
                {
                    lastLookup = Stopwatch.GetTimestamp();
                    host = Hosts.Find(Hosts.StartHostName);
                    if (host is not null)
                    {
                        startHostPid = host.Id;
                        Add("host", $"StartMenuExperienceHost pid {host.Id} running (started {host.StartTime.ToUniversalTime():HH:mm:ss.fff}Z)");
                    }
                }
            }
            else
            {
                try
                {
                    var (suspended, total) = Hosts.SuspendedCount(host);
                    if (suspended != lastSuspended)
                    {
                        Add("host", $"threads suspended {suspended} of {total}");
                        lastSuspended = suspended;
                    }
                }
                catch (InvalidOperationException)
                {
                    // It ended between the check and the read; the next pass reports it.
                }
            }

            Thread.Sleep(20);
        }
    }

    /// <summary>
    /// Every millisecond: the session's last-input time (GetLastInputInfo). A change means a key or a mouse event arrived
    /// (any device, any window): the mark the other events are timed from. Its resolution is the system tick (~15.6 ms),
    /// and nothing is hooked: the time is public session state.
    /// </summary>
    private void PollInput()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        GetLastInputInfo(ref info);
        uint last = info.dwTime;
        while (!stopping)
        {
            if (GetLastInputInfo(ref info) && info.dwTime != last)
            {
                Add("input", $"last input time moved by {unchecked(info.dwTime - last)} ms");
                last = info.dwTime;
            }

            Thread.Sleep(1);
        }
    }

    /// <summary>A process by id, or <see langword="null"/> when it is gone.</summary>
    /// <param name="pid">The process id.</param>
    /// <returns>The process.</returns>
    private static Process? TryGet(int pid)
    {
        try
        {
            return Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Records one event and prints it with its time since the start of the watch.</summary>
    /// <param name="kind">What happened.</param>
    /// <param name="detail">The facts.</param>
    private void Add(string kind, string detail)
    {
        var observed = new ObservedEvent(Stopwatch.GetTimestamp(), kind, detail);
        Console.WriteLine($"{Stopwatch.GetElapsedTime(origin, observed.Ticks).TotalMilliseconds,10:0.0} ms  {kind,-10} {detail}");
    }

    /// <summary>Stops the input poll, the sampler, the WNF subscriptions, the hooks and the hook thread.</summary>
    public void Dispose()
    {
        stopping = true;
        foreach (nint subscription in subscriptions)
        {
            RtlUnsubscribeWnfStateChangeNotification(subscription);
        }

        if (hookThreadId != 0)
        {
            // WM_QUIT ends GetMessage; the hooks and the window go with the thread.
            PostThreadMessage(hookThreadId, WM_QUIT, 0, 0);
        }

        hookThread?.Join(2000);
        samplerThread?.Join(2000);
        inputThread?.Join(2000);
        current = null;
    }
}

/// <summary>A window rectangle.</summary>
[StructLayout(LayoutKind.Sequential)]
struct RECT
{
    /// <summary>Left edge.</summary>
    public int Left;

    /// <summary>Top edge.</summary>
    public int Top;

    /// <summary>Right edge (exclusive).</summary>
    public int Right;

    /// <summary>Bottom edge (exclusive).</summary>
    public int Bottom;
}

/// <summary>A screen point.</summary>
[StructLayout(LayoutKind.Sequential)]
struct POINT
{
    /// <summary>X.</summary>
    public int X;

    /// <summary>Y.</summary>
    public int Y;
}

/// <summary>A queued window message.</summary>
[StructLayout(LayoutKind.Sequential)]
struct MSG
{
    /// <summary>The window.</summary>
    public nint hwnd;

    /// <summary>The message.</summary>
    public uint message;

    /// <summary>First parameter.</summary>
    public nint wParam;

    /// <summary>Second parameter.</summary>
    public nint lParam;

    /// <summary>When it was posted.</summary>
    public uint time;

    /// <summary>The cursor position then.</summary>
    public POINT pt;
}

/// <summary>A window class registration.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct WNDCLASSEX
{
    /// <summary>The structure's size.</summary>
    public uint cbSize;

    /// <summary>Class styles.</summary>
    public uint style;

    /// <summary>The window procedure.</summary>
    public nint lpfnWndProc;

    /// <summary>Extra class bytes.</summary>
    public int cbClsExtra;

    /// <summary>Extra window bytes.</summary>
    public int cbWndExtra;

    /// <summary>The module.</summary>
    public nint hInstance;

    /// <summary>The icon.</summary>
    public nint hIcon;

    /// <summary>The cursor.</summary>
    public nint hCursor;

    /// <summary>The background brush.</summary>
    public nint hbrBackground;

    /// <summary>The menu resource.</summary>
    public string? lpszMenuName;

    /// <summary>The class name.</summary>
    public string? lpszClassName;

    /// <summary>The small icon.</summary>
    public nint hIconSm;
}

/// <summary>GetLastInputInfo's result.</summary>
[StructLayout(LayoutKind.Sequential)]
struct LASTINPUTINFO
{
    /// <summary>The structure's size.</summary>
    public uint cbSize;

    /// <summary>The tick count (GetTickCount) of the session's last input event.</summary>
    public uint dwTime;
}

/// <summary>NtQueryInformationProcess's ProcessBasicInformation.</summary>
[StructLayout(LayoutKind.Sequential)]
struct PROCESS_BASIC_INFORMATION
{
    /// <summary>The exit status.</summary>
    public nint ExitStatus;

    /// <summary>The PEB's address.</summary>
    public nint PebBaseAddress;

    /// <summary>The affinity mask.</summary>
    public nint AffinityMask;

    /// <summary>The base priority.</summary>
    public nint BasePriority;

    /// <summary>The process id.</summary>
    public nint UniqueProcessId;

    /// <summary>The id of the process that created it.</summary>
    public nint InheritedFromUniqueProcessId;
}

/// <summary>A window procedure.</summary>
/// <param name="hwnd">The window.</param>
/// <param name="message">The message.</param>
/// <param name="wParam">First parameter.</param>
/// <param name="lParam">Second parameter.</param>
/// <returns>The message's result.</returns>
delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

/// <summary>EnumWindows' and EnumChildWindows' callback.</summary>
/// <param name="hwnd">A window.</param>
/// <param name="lParam">The caller's value.</param>
/// <returns><see langword="true"/> to continue.</returns>
delegate bool EnumWindowsProc(nint hwnd, nint lParam);

/// <summary>A WinEvent callback.</summary>
/// <param name="hook">The hook.</param>
/// <param name="eventType">The EVENT_ constant.</param>
/// <param name="hwnd">The window.</param>
/// <param name="idObject">The object.</param>
/// <param name="idChild">The child.</param>
/// <param name="thread">The raising thread.</param>
/// <param name="time">The event's tick count.</param>
delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time);

/// <summary>Win32 and native declarations (all of them reads, hooks out of context, or this process's own window).</summary>
static unsafe class Native
{
    /// <summary>DWMWA_CLOAKED: why a window is cloaked (0 = it is not).</summary>
    public const uint DWMWA_CLOAKED = 14;

    /// <summary>GWL_STYLE.</summary>
    public const int GWL_STYLE = -16;

    /// <summary>GWL_EXSTYLE.</summary>
    public const int GWL_EXSTYLE = -20;

    /// <summary>HWND_MESSAGE: the parent of message-only windows.</summary>
    public const nint HWND_MESSAGE = -3;

    /// <summary>WS_POPUP.</summary>
    public const uint WS_POPUP = 0x80000000;

    /// <summary>WS_EX_TOOLWINDOW: no taskbar button.</summary>
    public const uint WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>WM_QUIT.</summary>
    public const uint WM_QUIT = 0x0012;

    /// <summary>EVENT_SYSTEM_FOREGROUND.</summary>
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;

    /// <summary>EVENT_OBJECT_CREATE.</summary>
    public const uint EVENT_OBJECT_CREATE = 0x8000;

    /// <summary>EVENT_OBJECT_DESTROY.</summary>
    public const uint EVENT_OBJECT_DESTROY = 0x8001;

    /// <summary>EVENT_OBJECT_SHOW.</summary>
    public const uint EVENT_OBJECT_SHOW = 0x8002;

    /// <summary>EVENT_OBJECT_HIDE.</summary>
    public const uint EVENT_OBJECT_HIDE = 0x8003;

    /// <summary>EVENT_OBJECT_LOCATIONCHANGE.</summary>
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;

    /// <summary>EVENT_OBJECT_CLOAKED.</summary>
    public const uint EVENT_OBJECT_CLOAKED = 0x8017;

    /// <summary>EVENT_OBJECT_UNCLOAKED.</summary>
    public const uint EVENT_OBJECT_UNCLOAKED = 0x8018;

    /// <summary>WINEVENT_OUTOFCONTEXT: callbacks on the hooking thread, no DLL injected anywhere.</summary>
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    /// <summary>WINEVENT_SKIPOWNPROCESS.</summary>
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    /// <summary>PROCESS_QUERY_LIMITED_INFORMATION: enough for names, the token and the package of any process of this user.</summary>
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>TOKEN_QUERY.</summary>
    public const uint TOKEN_QUERY = 0x0008;

    /// <summary>Sets this process's DPI awareness (-4 = per-monitor v2: physical pixels everywhere).</summary>
    /// <param name="value">The awareness context.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(nint value);

    /// <summary>Finds a top-level window by class and title.</summary>
    /// <param name="className">The class, or <see langword="null"/>.</param>
    /// <param name="title">The title, or <see langword="null"/>.</param>
    /// <returns>The window, or 0.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint FindWindow(string? className, string? title);

    /// <summary>
    /// Finds a window after another: with parent 0, top-level windows (immersive ones included); with HWND_MESSAGE,
    /// message-only windows; null class and title match any.
    /// </summary>
    /// <param name="parent">The parent, 0 = the desktop.</param>
    /// <param name="after">The previous match, 0 = start.</param>
    /// <param name="className">The class, or <see langword="null"/>.</param>
    /// <param name="title">The title, or <see langword="null"/>.</param>
    /// <returns>The window, or 0.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint FindWindowEx(nint parent, nint after, string? className, string? title);

    /// <summary>Calls back for each top-level window of a desktop app (not the shell's immersive windows, since Windows 8).</summary>
    /// <param name="callback">The callback.</param>
    /// <param name="lParam">Passed through.</param>
    /// <returns><see langword="true"/> unless the callback stopped it.</returns>
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    /// <summary>Calls back for each child window, recursively.</summary>
    /// <param name="parent">The parent.</param>
    /// <param name="callback">The callback.</param>
    /// <param name="lParam">Passed through.</param>
    /// <returns><see langword="true"/> unless the callback stopped it.</returns>
    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint lParam);

    /// <summary>The thread and process that own a window.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="processId">Receives the process id.</param>
    /// <returns>The thread id.</returns>
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    /// <summary>A window's class name.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="text">Receives the name.</param>
    /// <param name="capacity">The buffer's size.</param>
    /// <returns>The length.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(nint hwnd, StringBuilder text, int capacity);

    /// <summary>A window's title.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="text">Receives the title.</param>
    /// <param name="capacity">The buffer's size.</param>
    /// <returns>The length.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(nint hwnd, StringBuilder text, int capacity);

    /// <summary>Whether a window has WS_VISIBLE (a cloaked window keeps it).</summary>
    /// <param name="hwnd">The window.</param>
    /// <returns><see langword="true"/> when visible.</returns>
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint hwnd);

    /// <summary>A window's rectangle.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="rect">Receives it.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(nint hwnd, out RECT rect);

    /// <summary>A window's style or extended style.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="index">GWL_STYLE or GWL_EXSTYLE.</param>
    /// <returns>The value.</returns>
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern long GetWindowLongPtr(nint hwnd, int index);

    /// <summary>A window's z-order band (undocumented user32 export; the shell's windows live above band 1).</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="band">Receives the band.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    public static extern bool GetWindowBand(nint hwnd, out uint band);

    /// <summary>The desktop's shell window (Explorer's Progman).</summary>
    /// <returns>The window.</returns>
    [DllImport("user32.dll")]
    public static extern nint GetShellWindow();

    /// <summary>The window registered with SetTaskmanWindow (undocumented user32 export).</summary>
    /// <returns>The window, or 0.</returns>
    [DllImport("user32.dll")]
    public static extern nint GetTaskmanWindow();

    /// <summary>The window registered as Progman (undocumented user32 export).</summary>
    /// <returns>The window, or 0.</returns>
    [DllImport("user32.dll")]
    public static extern nint GetProgmanWindow();

    /// <summary>The session's last input event time.</summary>
    /// <param name="info">Receives it (cbSize set by the caller).</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    /// <summary>Posts a message to a thread's queue (WM_QUIT to this probe's own hook thread).</summary>
    /// <param name="threadId">The thread.</param>
    /// <param name="message">The message.</param>
    /// <param name="wParam">First parameter.</param>
    /// <param name="lParam">Second parameter.</param>
    /// <returns><see langword="true"/> when queued.</returns>
    [DllImport("user32.dll")]
    public static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

    /// <summary>Hooks a range of WinEvents (out of context: delivered to this thread's queue, nothing injected).</summary>
    /// <param name="eventMin">First event.</param>
    /// <param name="eventMax">Last event.</param>
    /// <param name="module">0 for an out-of-context hook.</param>
    /// <param name="callback">The callback.</param>
    /// <param name="processId">0 = every process.</param>
    /// <param name="threadId">0 = every thread.</param>
    /// <param name="flags">WINEVENT_ flags.</param>
    /// <returns>The hook, or 0.</returns>
    [DllImport("user32.dll")]
    public static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback, uint processId, uint threadId, uint flags);

    /// <summary>Registers a window for shell-hook messages (HSHELL_* through the "SHELLHOOK" message).</summary>
    /// <param name="hwnd">A top-level window.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("user32.dll")]
    public static extern bool RegisterShellHookWindow(nint hwnd);

    /// <summary>Registers (or looks up) a message by name.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The message id.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string name);

    /// <summary>Registers a window class.</summary>
    /// <param name="windowClass">The class.</param>
    /// <returns>The class atom, or 0.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    /// <summary>Creates a window.</summary>
    /// <param name="exStyle">Extended style.</param>
    /// <param name="className">The class.</param>
    /// <param name="title">The title.</param>
    /// <param name="style">Style.</param>
    /// <param name="x">Left.</param>
    /// <param name="y">Top.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="parent">The parent or owner.</param>
    /// <param name="menu">The menu.</param>
    /// <param name="instance">The module.</param>
    /// <param name="param">Creation data.</param>
    /// <returns>The window, or 0.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    /// <summary>The default window procedure.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="message">The message.</param>
    /// <param name="wParam">First parameter.</param>
    /// <param name="lParam">Second parameter.</param>
    /// <returns>The result.</returns>
    [DllImport("user32.dll")]
    public static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>Takes the next message from this thread's queue.</summary>
    /// <param name="message">Receives it.</param>
    /// <param name="hwnd">0 = any window.</param>
    /// <param name="min">First message.</param>
    /// <param name="max">Last message.</param>
    /// <returns>0 for WM_QUIT, -1 on error, else positive.</returns>
    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG message, nint hwnd, uint min, uint max);

    /// <summary>Translates key messages to character messages.</summary>
    /// <param name="message">The message.</param>
    /// <returns><see langword="true"/> when translated.</returns>
    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG message);

    /// <summary>Calls the window procedure for a message.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The procedure's result.</returns>
    [DllImport("user32.dll")]
    public static extern nint DispatchMessage(ref MSG message);

    /// <summary>The calling thread's id.</summary>
    /// <returns>The id.</returns>
    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    /// <summary>The handle of a loaded module.</summary>
    /// <param name="name"><see langword="null"/> = this executable.</param>
    /// <returns>The module.</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandle(string? name);

    /// <summary>Opens a process.</summary>
    /// <param name="access">The access wanted.</param>
    /// <param name="inherit">Whether children inherit the handle.</param>
    /// <param name="processId">The process.</param>
    /// <returns>The handle, or 0.</returns>
    [DllImport("kernel32.dll")]
    public static extern nint OpenProcess(uint access, bool inherit, int processId);

    /// <summary>Closes a handle.</summary>
    /// <param name="handle">The handle.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(nint handle);

    /// <summary>A process's executable path.</summary>
    /// <param name="process">The process.</param>
    /// <param name="flags">0 = Win32 path.</param>
    /// <param name="path">Receives the path.</param>
    /// <param name="size">In: the buffer's size; out: the length.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref uint size);

    /// <summary>The package full name of a packaged process.</summary>
    /// <param name="process">The process.</param>
    /// <param name="length">In: the buffer's size; out: the length.</param>
    /// <param name="name">Receives the name.</param>
    /// <returns>0 on success, APPMODEL_ERROR_NO_PACKAGE for an unpackaged process.</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetPackageFullName(nint process, ref uint length, StringBuilder name);

    /// <summary>The app user model id of a packaged process.</summary>
    /// <param name="process">The process.</param>
    /// <param name="length">In: the buffer's size; out: the length.</param>
    /// <param name="id">Receives the id.</param>
    /// <returns>0 on success.</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetApplicationUserModelId(nint process, ref uint length, StringBuilder id);

    /// <summary>Opens a process's token.</summary>
    /// <param name="process">The process.</param>
    /// <param name="access">TOKEN_QUERY.</param>
    /// <param name="token">Receives the token.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("advapi32.dll")]
    public static extern bool OpenProcessToken(nint process, uint access, out nint token);

    /// <summary>Reads a token property.</summary>
    /// <param name="token">The token.</param>
    /// <param name="infoClass">The TOKEN_INFORMATION_CLASS.</param>
    /// <param name="buffer">Receives the data.</param>
    /// <param name="length">The buffer's size.</param>
    /// <param name="returned">Receives the data's size.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("advapi32.dll")]
    public static extern bool GetTokenInformation(nint token, int infoClass, void* buffer, int length, out int returned);

    /// <summary>A pointer to a SID's sub-authority count.</summary>
    /// <param name="sid">The SID.</param>
    /// <returns>The pointer.</returns>
    [DllImport("advapi32.dll")]
    public static extern byte* GetSidSubAuthorityCount(nint sid);

    /// <summary>A pointer to one of a SID's sub-authorities.</summary>
    /// <param name="sid">The SID.</param>
    /// <param name="index">The index.</param>
    /// <returns>The pointer.</returns>
    [DllImport("advapi32.dll")]
    public static extern uint* GetSidSubAuthority(nint sid, uint index);

    /// <summary>Process information (class 0 = basic: the parent's id).</summary>
    /// <param name="process">The process.</param>
    /// <param name="infoClass">The class.</param>
    /// <param name="info">Receives it.</param>
    /// <param name="length">Its size.</param>
    /// <param name="returned">Receives the size written.</param>
    /// <returns>The NTSTATUS.</returns>
    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(nint process, int infoClass, ref PROCESS_BASIC_INFORMATION info, int length, out int returned);

    /// <summary>Reads a WNF state's data (undocumented, stable since Windows 8).</summary>
    /// <param name="stateName">The state.</param>
    /// <param name="typeId">0.</param>
    /// <param name="explicitScope">0 = the state's own scope.</param>
    /// <param name="changeStamp">Receives the stamp.</param>
    /// <param name="buffer">Receives the data.</param>
    /// <param name="bufferSize">In: the buffer's size; out: the data's size.</param>
    /// <returns>The NTSTATUS.</returns>
    [DllImport("ntdll.dll")]
    public static extern int NtQueryWnfStateData(ref ulong stateName, nint typeId, nint explicitScope, out uint changeStamp, byte* buffer, ref uint bufferSize);

    /// <summary>Subscribes to a WNF state's changes (the callback runs on an ntdll worker thread).</summary>
    /// <param name="subscription">Receives the subscription.</param>
    /// <param name="stateName">The state.</param>
    /// <param name="changeStamp">Only changes after this stamp are reported.</param>
    /// <param name="callback">The callback.</param>
    /// <param name="context">Passed through.</param>
    /// <param name="typeId">0.</param>
    /// <param name="serializationGroup">0.</param>
    /// <param name="flags">0.</param>
    /// <returns>The NTSTATUS.</returns>
    [DllImport("ntdll.dll")]
    public static extern int RtlSubscribeWnfStateChangeNotification(out nint subscription, ulong stateName, uint changeStamp, delegate* unmanaged<ulong, uint, nint, nint, nint, uint, int> callback, nint context, nint typeId, uint serializationGroup, uint flags);

    /// <summary>Ends a WNF subscription.</summary>
    /// <param name="subscription">The subscription.</param>
    /// <returns>The NTSTATUS.</returns>
    [DllImport("ntdll.dll")]
    public static extern int RtlUnsubscribeWnfStateChangeNotification(nint subscription);

    /// <summary>Reads a DWM attribute that is an integer (DWMWA_CLOAKED).</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="attribute">The attribute.</param>
    /// <param name="value">Receives the value.</param>
    /// <param name="size">Its size.</param>
    /// <returns>0 (S_OK) on success.</returns>
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);
}
