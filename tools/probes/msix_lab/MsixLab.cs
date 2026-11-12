using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace MsixLab;

/// <summary>
/// msix_lab: one console program that is registered as a full-trust MSIX package (see <c>README.md</c>) and reports how
/// Windows treats such a process. Each command measures one thing BetterClipboard's Microsoft Store edition depends on:
/// where registry and AppData writes land, whether child processes leave the package's environment, whether DPAPI
/// blobs and named kernel objects are shared with unpackaged programs, which languages the process is given, whether a
/// startup task can be switched on, and what the WinRT clipboard history answers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where it may run.</b> It registers a package and writes BC-TEST keys and files into the user's profile, so it runs
/// only inside a claude-desktops Windows desktop, never on a PC in use. Everything it writes is named <c>BC-TEST-MSIX…</c>.
/// </para>
/// <para>
/// <b>Output.</b> Lines <c>key = value</c> on the console and, with <c>--out FILE</c>, appended to that file (give a path
/// outside AppData, where nothing is redirected). It never prints clipboard content: the history command prints a
/// status and a count.
/// </para>
/// </remarks>
public static unsafe class Program
{
    /// <summary>The registry key every registry test uses, under <c>HKEY_CURRENT_USER</c>.</summary>
    private const string TestKey = @"Software\BC-TEST-MSIX";

    /// <summary>A key an outside process creates and writes to, for the change-notification test.</summary>
    private const string WatchKey = @"Software\BC-TEST-MSIX-Watch";

    /// <summary><c>PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY</c>: attribute number 18, an input attribute.</summary>
    private const nint DesktopAppPolicyAttribute = 0x00020012;

    /// <summary><c>EXTENDED_STARTUPINFO_PRESENT</c> | <c>CREATE_NO_WINDOW</c> for <c>CreateProcessW</c>.</summary>
    private const uint ExtendedStartupNoWindow = 0x00080000 | 0x08000000;

    /// <summary>Where the result lines are appended, or <see langword="null"/> for the console alone.</summary>
    private static string? outputPath;

    /// <summary>
    /// Runs one command.
    /// </summary>
    /// <param name="args">The command and its arguments; <c>--out FILE</c> anywhere adds a result file.</param>
    /// <returns>0 when the command ran (whatever it measured), 2 for an unknown command, 1 when it threw.</returns>
    public static int Main(string[] args)
    {
        var list = args.ToList();
        int outIndex = list.IndexOf("--out");
        if (outIndex >= 0 && outIndex + 1 < list.Count)
        {
            outputPath = list[outIndex + 1];
            list.RemoveRange(outIndex, 2);
        }

        if (list.Count == 0)
        {
            Log("usage", "msixlab <identity|regwrite|regread|regdelete|child|selfchild|files|filewatch|dpapi|dpapi-make|mutex|mutex-open|lang|cliphistory|regnotify|regnotify-users|regpoll|startup|apps> [args] [--out FILE]");
            return 2;
        }

        try
        {
            switch (list[0])
            {
                case "identity": Identity(); break;
                case "regwrite": RegWrite(Arg(list, 1, "in-process")); break;
                case "regread": RegRead(); break;
                case "regdelete": RegDelete(Arg(list, 1, "outside")); break;
                case "child": Child(Arg(list, 1, "plain"), Arg(list, 2, AppContext.BaseDirectory)); break;
                case "selfchild": SelfChild(Arg(list, 1, "plain"), Arg(list, 2, Path.GetTempPath())); break;
                case "filewatch": FileWatch(Arg(list, 1, string.Empty), int.Parse(Arg(list, 2, "12"), CultureInfo.InvariantCulture)); break;
                case "files": Files(); break;
                case "dpapi": DpapiRead(Arg(list, 1, string.Empty)); break;
                case "dpapi-make": DpapiMake(Arg(list, 1, string.Empty)); break;
                case "mutex": HoldMutex(Arg(list, 1, "BC-TEST-MSIX-Mutex"), int.Parse(Arg(list, 2, "20"), CultureInfo.InvariantCulture)); break;
                case "mutex-open": OpenMutex(Arg(list, 1, "BC-TEST-MSIX-Mutex")); break;
                case "lang": Languages(); break;
                case "cliphistory": ClipboardHistory(); break;
                case "regnotify": RegNotify(int.Parse(Arg(list, 1, "20"), CultureInfo.InvariantCulture), throughUsersHive: false); break;
                case "regnotify-users": RegNotify(int.Parse(Arg(list, 1, "20"), CultureInfo.InvariantCulture), throughUsersHive: true); break;
                case "regpoll": RegPoll(int.Parse(Arg(list, 1, "20"), CultureInfo.InvariantCulture)); break;
                case "startup": StartupTask(Arg(list, 1, "MsixLabStartup")); break;
                case "apps": InstalledApps(); break;
                default:
                    Log("error", $"unknown command {list[0]}");
                    return 2;
            }

            return 0;
        }
        catch (Exception ex)
        {
            // A lab tool reports what went wrong instead of hiding it: the failure is the measurement.
            Log("exception", $"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Returns an argument or its default.</summary>
    /// <param name="list">The arguments.</param>
    /// <param name="index">Which one.</param>
    /// <param name="fallback">The value when it is missing.</param>
    /// <returns>The argument, or <paramref name="fallback"/>.</returns>
    private static string Arg(List<string> list, int index, string fallback) => index < list.Count ? list[index] : fallback;

    /// <summary>Writes one result line to the console and to the result file.</summary>
    /// <param name="key">What was measured.</param>
    /// <param name="value">The measurement.</param>
    private static void Log(string key, object? value)
    {
        string line = $"{key} = {value}";
        Console.WriteLine(line);
        if (outputPath is not null)
        {
            File.AppendAllText(outputPath, line + "\n", new UTF8Encoding(false));
        }
    }

    /// <summary>
    /// Prints whether the process has package identity, its package names and folder, and its integrity level.
    /// </summary>
    private static void Identity()
    {
        Log("packageFullName", PackageString(full: true) ?? "(none: not packaged)");
        Log("packageFamilyName", PackageString(full: false) ?? "(none)");
        Log("baseDirectory", AppContext.BaseDirectory);
        Log("processPath", Environment.ProcessPath);
        Log("currentDirectory", Environment.CurrentDirectory);
        Log("localAppData", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Log("tempPath", Path.GetTempPath());
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        Log("user", identity.Name);
        Log("elevated", new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));
        Log("parentProcess", ParentName());
    }

    /// <summary>
    /// Reads the current package's full or family name.
    /// </summary>
    /// <param name="full">The full name; otherwise the family name.</param>
    /// <returns>The name, or <see langword="null"/> when the process has no package identity (error 15700).</returns>
    private static string? PackageString(bool full)
    {
        uint length = 0;
        int result = full ? GetCurrentPackageFullName(ref length, null) : GetCurrentPackageFamilyName(ref length, null);
        if (result != 122 || length == 0) // ERROR_INSUFFICIENT_BUFFER is the answer with the length; anything else: no package
        {
            return null;
        }

        char* buffer = stackalloc char[(int)length];
        result = full ? GetCurrentPackageFullName(ref length, buffer) : GetCurrentPackageFamilyName(ref length, buffer);
        return result == 0 ? new string(buffer) : null;
    }

    /// <summary>Names the process that started this one, to tell an alias start from a child start.</summary>
    /// <returns>The parent's program name and id, or a note when it cannot be read.</returns>
    private static string ParentName()
    {
        try
        {
            var info = default(ProcessBasicInformation);
            int status = NtQueryInformationProcess(Process.GetCurrentProcess().Handle, 0, &info, (uint)sizeof(ProcessBasicInformation), null);
            if (status != 0)
            {
                return $"(NTSTATUS 0x{status:X8})";
            }

            using var parent = Process.GetProcessById((int)info.InheritedFromUniqueProcessId);
            return $"{parent.ProcessName} ({parent.Id})";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return "(gone)";
        }
    }

    /// <summary>Writes one value under the test key in this process, to see whether the write is redirected.</summary>
    /// <param name="name">The value's name.</param>
    private static void RegWrite(string name)
    {
        using var key = Registry.CurrentUser.CreateSubKey(TestKey, writable: true);
        key.SetValue(name, "1", RegistryValueKind.String);
        Log($"regwrite.{name}", "written (as this process sees it)");
    }

    /// <summary>Lists the test key's values as this process sees them (the merged view inside a package).</summary>
    private static void RegRead()
    {
        using var key = Registry.CurrentUser.OpenSubKey(TestKey);
        Log("regread", key is null ? "(key not visible)" : string.Join(", ", key.GetValueNames().Order(StringComparer.Ordinal)));
    }

    /// <summary>Deletes a value an outside process created, to see whether a delete reaches the real registry.</summary>
    /// <param name="name">The value's name.</param>
    private static void RegDelete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(TestKey, writable: true);
        if (key is null)
        {
            Log($"regdelete.{name}", "(key not visible)");
            return;
        }

        key.DeleteValue(name, throwOnMissingValue: false);
        Log($"regdelete.{name}", key.GetValue(name) is null ? "gone (as this process sees it)" : "still there");
    }

    /// <summary>
    /// Starts <c>child.cmd</c> (next to the exe: it writes a registry value and a file named after the mode) in one of
    /// several ways, so that an outside look can tell which ways leave the package's environment.
    /// </summary>
    /// <param name="mode">
    /// <c>plain</c> (CreateProcess), <c>shell</c> (ShellExecute), <c>policy1</c>/<c>policy2</c>/<c>policy4</c>
    /// (CreateProcess with that desktop app policy), <c>explorer</c> (a script file handed to explorer.exe),
    /// <c>shellcom</c> (the desktop shell's own ShellExecute), <c>runas</c> (ShellExecute, verb runas) or
    /// <c>schtask</c> (a one-time scheduled task).
    /// </param>
    /// <param name="scriptDirectory">The folder that holds <c>child.cmd</c>; also where helper scripts are written.</param>
    private static void Child(string mode, string scriptDirectory)
    {
        string script = Path.Combine(scriptDirectory, "child.cmd");
        string arguments = $"/c \"\"{script}\" {mode}\"";
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string cmd = Path.Combine(system, "cmd.exe");
        switch (mode)
        {
            case "plain":
                Wait(Process.Start(new ProcessStartInfo(cmd, arguments) { UseShellExecute = false, CreateNoWindow = true }), mode);
                break;
            case "shell":
                Wait(Process.Start(new ProcessStartInfo(cmd, arguments) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }), mode);
                break;
            case "runas":
                Wait(Process.Start(new ProcessStartInfo(cmd, arguments) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }), mode);
                break;
            case "policy1" or "policy2" or "policy4":
                CreateWithPolicy(cmd, arguments, uint.Parse(mode[^1..], CultureInfo.InvariantCulture), mode);
                break;
            case "explorer":
            {
                // Explorer takes a file to open, not a command line: a one-line script stands in for the arguments.
                string helper = Path.Combine(scriptDirectory, "run-explorer.cmd");
                File.WriteAllText(helper, $"@call \"{script}\" explorer\r\n", Encoding.ASCII);
                Wait(Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"\"{helper}\"") { UseShellExecute = false }), mode);
                break;
            }

            case "shellcom":
                ShellExecuteThroughDesktop(cmd, arguments);
                Log($"child.{mode}", "asked the desktop shell");
                break;
            case "schtask":
            {
                string schtasks = Path.Combine(system, "schtasks.exe");
                Wait(Process.Start(new ProcessStartInfo(schtasks, $"/create /tn BC-TEST-msixlab /tr \"cmd.exe /c \\\"{script}\\\" schtask\" /sc once /st 23:59 /f") { UseShellExecute = false, CreateNoWindow = true }), "schtask.create");
                Wait(Process.Start(new ProcessStartInfo(schtasks, "/run /tn BC-TEST-msixlab") { UseShellExecute = false, CreateNoWindow = true }), "schtask.run");
                Thread.Sleep(3000); // the task runs in the scheduler's own time; then it is removed again
                Wait(Process.Start(new ProcessStartInfo(schtasks, "/delete /tn BC-TEST-msixlab /f") { UseShellExecute = false, CreateNoWindow = true }), "schtask.delete");
                break;
            }

            default:
                Log($"child.{mode}", "unknown mode");
                break;
        }
    }

    /// <summary>
    /// Starts this very program again (the package's own executable, by its path) and has the new process report its
    /// identity: does a process that a packaged app starts from its own package keep the package identity? An app that
    /// restarts itself, or starts a helper of its own, depends on the answer.
    /// </summary>
    /// <param name="mode">
    /// <c>plain</c> (CreateProcess), <c>shell</c> (ShellExecute), or <c>policy1</c> / <c>policy4</c> (CreateProcess with
    /// that desktop app policy).
    /// </param>
    /// <param name="outDirectory">Where the child writes <c>selfchild-MODE.txt</c> (outside AppData).</param>
    /// <exception cref="InvalidOperationException">
    /// This process has no path of its own to start again (never the case for a program started from a file).
    /// </exception>
    private static void SelfChild(string mode, string outDirectory)
    {
        string self = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        string report = Path.Combine(outDirectory, $"selfchild-{mode}.txt");
        File.Delete(report);
        string arguments = $"identity --out \"{report}\"";
        switch (mode)
        {
            case "plain":
                Wait(Process.Start(new ProcessStartInfo(self, arguments) { UseShellExecute = false, CreateNoWindow = true }), $"self.{mode}");
                break;
            case "shell":
                Wait(Process.Start(new ProcessStartInfo(self, arguments) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }), $"self.{mode}");
                break;
            case "policy1" or "policy4":
                CreateWithPolicy(self, arguments, uint.Parse(mode[^1..], CultureInfo.InvariantCulture), $"self.{mode}");
                break;
            default:
                Log($"child.self.{mode}", "unknown mode");
                return;
        }

        // The child's own words: its package name, or that it has none.
        string identity = File.Exists(report)
            ? File.ReadLines(report).FirstOrDefault(line => line.StartsWith("packageFullName", StringComparison.Ordinal)) ?? "(no identity line)"
            : "(the child wrote no report)";
        Log($"selfchild.{mode}", identity);
    }

    /// <summary>
    /// Watches a folder for changes an outside process makes, the way BetterClipboard watches other apps' folders
    /// (Notepad++'s backups under AppData, for one): do file change notifications arrive inside a package for a real
    /// folder below AppData, where the package's own writes are redirected? Registry notifications do not.
    /// </summary>
    /// <param name="directory">The folder to watch; it must exist (the caller makes it, outside the package).</param>
    /// <param name="seconds">How long to watch.</param>
    private static void FileWatch(string directory, int seconds)
    {
        if (!Directory.Exists(directory))
        {
            Log("filewatch", $"(folder not visible: {directory})");
            return;
        }

        var events = new List<string>();
        using var watcher = new FileSystemWatcher(directory)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
        };
        void Note(string kind, string? name)
        {
            lock (events)
            {
                events.Add($"{kind}:{name}");
            }
        }

        watcher.Created += (_, e) => Note("created", e.Name);
        watcher.Changed += (_, e) => Note("changed", e.Name);
        watcher.Deleted += (_, e) => Note("deleted", e.Name);
        watcher.Renamed += (_, e) => Note("renamed", e.Name);
        watcher.Error += (_, e) => Note("error", e.GetException().GetType().Name);
        watcher.EnableRaisingEvents = true;
        Log("filewatch.armed", directory);
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        lock (events)
        {
            Log("filewatch.events", events.Count == 0 ? "(none)" : string.Join(", ", events.Distinct()));
        }

        Log("filewatch.filesNow", string.Join(", ", Directory.EnumerateFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal)));
    }

    /// <summary>Waits for a started process and logs its exit code.</summary>
    /// <param name="process">The process, or <see langword="null"/> when nothing was started.</param>
    /// <param name="label">What it was, for the log.</param>
    private static void Wait(Process? process, string label)
    {
        if (process is null)
        {
            Log($"child.{label}", "not started");
            return;
        }

        using (process)
        {
            bool ended = process.WaitForExit(20000);
            Log($"child.{label}", ended ? $"exit {process.ExitCode}" : "still running after 20 s");
        }
    }

    /// <summary>
    /// Creates a process with <c>PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY</c> set, the documented way to say whether a
    /// packaged desktop app's descendants stay in its environment.
    /// </summary>
    /// <param name="application">The program.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="policy">1 = descendants outside, 2 = descendants inside, 4 = this process inside.</param>
    /// <param name="label">The mode's name, for the log.</param>
    /// <exception cref="Win32Exception">
    /// Windows refused the attribute list, the policy attribute or the process itself; the lab then has no answer for
    /// this mode, which must not read as "the child ran".
    /// </exception>
    private static void CreateWithPolicy(string application, string arguments, uint policy, string label)
    {
        nuint size = 0;
        InitializeProcThreadAttributeList(null, 1, 0, ref size);
        byte* list = (byte*)NativeMemory.AllocZeroed(size);
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            if (!UpdateProcThreadAttribute(list, 0, DesktopAppPolicyAttribute, &policy, sizeof(uint), null, null))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var startup = default(StartupInfoEx);
            startup.StartupInfo.cb = (uint)sizeof(StartupInfoEx);
            startup.AttributeList = list;
            string commandLine = $"\"{application}\" {arguments}";
            fixed (char* command = commandLine)
            {
                // CreateProcessW may write into the command line, so it gets its own copy.
                char* copy = (char*)NativeMemory.Alloc((nuint)(commandLine.Length + 1) * sizeof(char));
                try
                {
                    new ReadOnlySpan<char>(command, commandLine.Length + 1).CopyTo(new Span<char>(copy, commandLine.Length + 1));
                    if (!CreateProcessW(null, copy, null, null, false, ExtendedStartupNoWindow, null, null, &startup, out var info))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    }

                    uint wait = WaitForSingleObject(info.Process, 20000);
                    GetExitCodeProcess(info.Process, out uint exit);
                    Log($"child.{label}", wait == 0 ? $"exit {exit}" : "still running after 20 s");
                    CloseHandle(info.Thread);
                    CloseHandle(info.Process);
                }
                finally
                {
                    NativeMemory.Free(copy);
                }
            }
        }
        finally
        {
            DeleteProcThreadAttributeList(list);
            NativeMemory.Free(list);
        }
    }

    /// <summary>
    /// Has the desktop shell (Explorer) start a program through its automation object, so the program is Explorer's
    /// child and not this process's.
    /// </summary>
    /// <param name="file">The program.</param>
    /// <param name="arguments">Its arguments.</param>
    private static void ShellExecuteThroughDesktop(string file, string arguments)
    {
        var shellWindowsType = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), throwOnError: true)!;
        dynamic shellWindows = Activator.CreateInstance(shellWindowsType)!;
        object location = 0;            // CSIDL_DESKTOP
        object empty = Type.Missing;
        int handle = 0;
        dynamic desktop = shellWindows.FindWindowSW(ref location, ref empty, 8 /* SWC_DESKTOP */, ref handle, 1 /* SWFO_NEEDDISPATCH */);
        dynamic application = desktop.Document.Application;
        application.ShellExecute(file, arguments, string.Empty, "open", 0);
    }

    /// <summary>
    /// Creates, changes, renames and deletes files under AppData in this process and says what it saw; an outside look
    /// then finds where each one really is.
    /// </summary>
    private static void Files()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // 1. A folder and a file that did not exist.
        string fresh = Path.Combine(local, "BC-TEST-MSIX-new");
        Try("files.newFolderAndFile", () =>
        {
            Directory.CreateDirectory(fresh);
            File.WriteAllText(Path.Combine(fresh, "a.txt"), "BC-TEST new");
            return "created";
        });

        // 2. A folder an outside process made before, with files in it.
        string existing = Path.Combine(local, "BC-TEST-MSIX-existing");
        Log("files.existingFolderVisible", Directory.Exists(existing));
        Try("files.appendToExisting", () => { File.AppendAllText(Path.Combine(existing, "existing.txt"), " +packaged"); return "appended"; });
        Try("files.newInExisting", () => { File.WriteAllText(Path.Combine(existing, "new-in-existing.txt"), "BC-TEST"); return "created"; });
        Try("files.deleteExisting", () => { File.Delete(Path.Combine(existing, "todelete.txt")); return File.Exists(Path.Combine(existing, "todelete.txt")) ? "still visible" : "gone"; });
        Try("files.renameExisting", () => { File.Move(Path.Combine(existing, "torename.txt"), Path.Combine(existing, "renamed.txt")); return "renamed"; });
        Try("files.replaceExisting", () =>
        {
            // The way a settings file is saved: a new file beside it, then moved over the old one.
            string target = Path.Combine(existing, "atomic.txt");
            string temp = target + ".tmp";
            File.WriteAllText(temp, "BC-TEST replaced by the packaged process");
            File.Move(temp, target, overwrite: true);
            return "replaced";
        });
        Try("files.deleteExistingFolder", () =>
        {
            string doomed = Path.Combine(local, "BC-TEST-MSIX-doomed");
            Directory.Delete(doomed, recursive: true);
            return Directory.Exists(doomed) ? "still visible" : "gone";
        });

        // 3. The package's own LocalState, by its real path.
        if (PackageString(full: false) is { } family)
        {
            string state = Path.Combine(local, "Packages", family, "LocalState");
            Try("files.localState", () => { Directory.CreateDirectory(state); File.WriteAllText(Path.Combine(state, "x.txt"), "BC-TEST"); return state; });
        }

        // 4. The temp folder (under AppData\Local\Temp).
        Try("files.temp", () => { string path = Path.Combine(Path.GetTempPath(), "BC-TEST-MSIX-temp.txt"); File.WriteAllText(path, "BC-TEST"); return path; });
        Log("files.listingOfNew", Directory.Exists(fresh) ? string.Join(", ", Directory.GetFiles(fresh).Select(Path.GetFileName)) : "(not visible)");
        Log("files.listingOfExisting", Directory.Exists(existing) ? string.Join(", ", Directory.GetFiles(existing).Select(Path.GetFileName).Order(StringComparer.Ordinal)) : "(not visible)");
    }

    /// <summary>Runs one file operation and logs its answer or its failure.</summary>
    /// <param name="key">The result's name.</param>
    /// <param name="action">The operation; its return value is logged.</param>
    private static void Try(string key, Func<string> action)
    {
        try
        {
            Log(key, action());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(key, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Unprotects a DPAPI blob another process made for this user (entropy "BC-TEST"), as BetterClipboard's history key
    /// is sealed.
    /// </summary>
    /// <param name="path">The blob file.</param>
    private static void DpapiRead(string path)
    {
        byte[] blob = File.ReadAllBytes(path);
        byte[] plain = ProtectedData.Unprotect(blob, Encoding.UTF8.GetBytes("BC-TEST"), DataProtectionScope.CurrentUser);
        Log("dpapi.unprotected", Encoding.UTF8.GetString(plain));
    }

    /// <summary>Protects a BC-TEST text in this process, for an outside process to unprotect.</summary>
    /// <param name="path">Where the blob goes.</param>
    private static void DpapiMake(string path)
    {
        byte[] blob = ProtectedData.Protect(Encoding.UTF8.GetBytes("BC-TEST sealed inside the package"), Encoding.UTF8.GetBytes("BC-TEST"), DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, blob);
        Log("dpapi.made", $"{blob.Length} bytes");
    }

    /// <summary>
    /// Creates a session-local named mutex and event and holds them, so an outside process can try to open them.
    /// </summary>
    /// <param name="name">The objects' base name.</param>
    /// <param name="seconds">How long to hold them.</param>
    private static void HoldMutex(string name, int seconds)
    {
        using var mutex = new Mutex(initiallyOwned: true, $@"Local\{name}", out bool createdNew);
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Event");
        Log("mutex.created", createdNew);
        bool signalled = signal.WaitOne(TimeSpan.FromSeconds(seconds));
        Log("mutex.eventSignalledByOutside", signalled);
    }

    /// <summary>Opens a named mutex and event another process holds, and signals the event.</summary>
    /// <param name="name">The objects' base name.</param>
    private static void OpenMutex(string name)
    {
        bool mutexFound = Mutex.TryOpenExisting($@"Local\{name}", out var mutex);
        mutex?.Dispose();
        Log("mutexOpen.mutexVisible", mutexFound);
        if (EventWaitHandle.TryOpenExisting($@"Local\{name}.Event", out var signal))
        {
            signal.Set();
            signal.Dispose();
            Log("mutexOpen.eventSignalled", true);
        }
        else
        {
            Log("mutexOpen.eventSignalled", false);
        }
    }

    /// <summary>Prints every language list a program might follow, to see which ones a package's manifest narrows.</summary>
    private static void Languages()
    {
        Log("lang.CurrentUICulture", CultureInfo.CurrentUICulture.Name);
        Log("lang.CurrentCulture", CultureInfo.CurrentCulture.Name);
        Log("lang.InstalledUICulture", CultureInfo.InstalledUICulture.Name);
        Log("lang.GetUserDefaultUILanguage", $"0x{GetUserDefaultUILanguage():X4}");
        Log("lang.GetUserPreferredUILanguages", PreferredLanguages(user: true));
        Log("lang.GetProcessPreferredUILanguages", PreferredLanguages(user: false));
        try
        {
            Log("lang.ApplicationLanguages", string.Join(", ", global::Windows.Globalization.ApplicationLanguages.Languages));
            Log("lang.ManifestLanguages", string.Join(", ", global::Windows.Globalization.ApplicationLanguages.ManifestLanguages));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Log("lang.ApplicationLanguages", $"{ex.GetType().Name}: 0x{ex.HResult:X8}");
        }

        Log("lang.GlobalizationPreferences", string.Join(", ", global::Windows.System.UserProfile.GlobalizationPreferences.Languages));
    }

    /// <summary>Reads the user's or the process's preferred UI languages as names.</summary>
    /// <param name="user">The user's list; otherwise the process's.</param>
    /// <returns>The names, comma separated, or the error.</returns>
    private static string PreferredLanguages(bool user)
    {
        const uint MuiLanguageName = 0x8;
        uint count = 0;
        uint length = 0;
        bool sized = user ? GetUserPreferredUILanguages(MuiLanguageName, out count, null, ref length) : GetProcessPreferredUILanguages(MuiLanguageName, out count, null, ref length);
        if (!sized || length == 0)
        {
            return $"(none; error {Marshal.GetLastPInvokeError()})";
        }

        char* buffer = stackalloc char[(int)length];
        bool read = user ? GetUserPreferredUILanguages(MuiLanguageName, out count, buffer, ref length) : GetProcessPreferredUILanguages(MuiLanguageName, out count, buffer, ref length);
        if (!read)
        {
            return $"(error {Marshal.GetLastPInvokeError()})";
        }

        // A double-NUL-terminated list.
        var names = new string(buffer, 0, (int)length).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return names.Length == 0 ? "(empty)" : string.Join(", ", names);
    }

    /// <summary>
    /// Asks the WinRT clipboard history from this process, which has no window: status and count only.
    /// </summary>
    private static void ClipboardHistory()
    {
        Log("cliphistory.IsHistoryEnabled", global::Windows.ApplicationModel.DataTransfer.Clipboard.IsHistoryEnabled());
        var result = global::Windows.ApplicationModel.DataTransfer.Clipboard.GetHistoryItemsAsync().AsTask().GetAwaiter().GetResult();
        Log("cliphistory.Status", result.Status);
        Log("cliphistory.Count", result.Items.Count);
    }

    /// <summary>
    /// Waits for a change of a key that an outside process writes to, the way BetterClipboard watches the Win+R list.
    /// </summary>
    /// <param name="seconds">How long to wait.</param>
    /// <param name="throughUsersHive">
    /// Open the key as <c>HKEY_USERS\&lt;SID&gt;\…</c> instead of <c>HKEY_CURRENT_USER\…</c>, to see whether that path
    /// escapes the package's merged view.
    /// </param>
    private static void RegNotify(int seconds, bool throughUsersHive)
    {
        string label = throughUsersHive ? "regnotifyUsers" : "regnotify";
        using var key = OpenWatchKey(throughUsersHive);
        if (key is null)
        {
            Log(label, "(key not visible)");
            return;
        }

        using var changed = new ManualResetEvent(false);
        const uint LastSetAndName = 0x4 | 0x1;
        int status = RegNotifyChangeKeyValue(key.Handle.DangerousGetHandle(), false, LastSetAndName, changed.SafeWaitHandle.DangerousGetHandle(), true);
        if (status != 0)
        {
            Log(label, $"registration failed ({status})");
            return;
        }

        Log($"{label}.armed", true);
        bool signalled = changed.WaitOne(TimeSpan.FromSeconds(seconds));
        Log($"{label}.signalled", signalled);
        Log($"{label}.valuesNow", string.Join(", ", key.GetValueNames().Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// Polls the watch key's last-write time twice a second and says when it moved: the fallback for a watch whose
    /// notification never comes.
    /// </summary>
    /// <param name="seconds">How long to poll.</param>
    private static void RegPoll(int seconds)
    {
        using var key = OpenWatchKey(throughUsersHive: false);
        if (key is null)
        {
            Log("regpoll", "(key not visible)");
            return;
        }

        long first = LastWrite(key);
        Log("regpoll.first", DateTime.FromFileTimeUtc(first).ToString("o", CultureInfo.InvariantCulture));
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            Thread.Sleep(500);
            long now = LastWrite(key);
            if (now != first)
            {
                Log("regpoll.changedAfterMs", watch.ElapsedMilliseconds);
                Log("regpoll.valuesNow", string.Join(", ", key.GetValueNames().Order(StringComparer.Ordinal)));
                return;
            }
        }

        Log("regpoll.changed", false);
    }

    /// <summary>Opens the watch key through one of the two paths to the user's hive.</summary>
    /// <param name="throughUsersHive"><c>HKEY_USERS\&lt;SID&gt;</c> instead of <c>HKEY_CURRENT_USER</c>.</param>
    /// <returns>The key, read-only, or <see langword="null"/> when this process cannot see it.</returns>
    private static RegistryKey? OpenWatchKey(bool throughUsersHive)
    {
        if (!throughUsersHive)
        {
            return Registry.CurrentUser.OpenSubKey(WatchKey);
        }

        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return Registry.Users.OpenSubKey($@"{identity.User!.Value}\{WatchKey}");
    }

    /// <summary>Reads a key's last-write time.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The time as a FILETIME, or 0 when it cannot be read.</returns>
    private static long LastWrite(RegistryKey key)
    {
        return RegQueryInfoKeyW(key.Handle.DangerousGetHandle(), null, null, null, null, null, null, null, null, null, null, out long lastWrite) == 0 ? lastWrite : 0;
    }

    /// <summary>
    /// Reads a startup task's state, asks to enable it, and disables it again.
    /// </summary>
    /// <param name="taskId">The task's id in the manifest.</param>
    private static void StartupTask(string taskId)
    {
        var task = global::Windows.ApplicationModel.StartupTask.GetAsync(taskId).AsTask().GetAwaiter().GetResult();
        Log("startup.stateBefore", task.State);
        var asked = task.RequestEnableAsync().AsTask().GetAwaiter().GetResult();
        Log("startup.afterRequestEnable", asked);
        Log("startup.stateAfterEnable", task.State);
        task.Disable();
        Log("startup.stateAfterDisable", task.State);
    }

    /// <summary>
    /// Shows that reads see the real registry: the Installed-apps entries named BetterClipboard (this user's and the
    /// PC's) and how many "start with Windows" values the user has.
    /// </summary>
    private static void InstalledApps()
    {
        const string uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BetterClipboard";
        using (var user = Registry.CurrentUser.OpenSubKey(uninstall))
        {
            Log("apps.userEntry", user is null ? "(none)" : $"{user.GetValue("DisplayVersion")} at {user.GetValue("InstallLocation")}");
            Log("apps.userUninstallString", user?.GetValue("UninstallString") ?? "(none)");
        }

        using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(uninstall))
        {
            Log("apps.machineEntry", machine is null ? "(none)" : $"{machine.GetValue("DisplayVersion")} at {machine.GetValue("InstallLocation")}");
        }

        using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        Log("apps.runValueNames", run is null ? "(none)" : string.Join(", ", run.GetValueNames()));
        using var advanced = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        Log("apps.DisabledHotkeys", advanced?.GetValue("DisabledHotkeys") ?? "(absent)");
    }

    /// <summary><c>PROCESS_BASIC_INFORMATION</c>, for the parent process id.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        /// <summary>Exit status.</summary>
        public nint ExitStatus;

        /// <summary>Address of the process environment block.</summary>
        public nint PebBaseAddress;

        /// <summary>Affinity mask.</summary>
        public nint AffinityMask;

        /// <summary>Base priority.</summary>
        public nint BasePriority;

        /// <summary>This process's id.</summary>
        public nint UniqueProcessId;

        /// <summary>The id of the process that created this one.</summary>
        public nint InheritedFromUniqueProcessId;
    }

    /// <summary><c>STARTUPINFOW</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        /// <summary>Size of the structure that follows (the extended one when an attribute list is passed).</summary>
        public uint cb;

        /// <summary>Reserved.</summary>
        public char* lpReserved;

        /// <summary>Desktop name.</summary>
        public char* lpDesktop;

        /// <summary>Window title.</summary>
        public char* lpTitle;

        /// <summary>Window position and size, console buffer and fill, flags, show command, reserved fields.</summary>
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;

        /// <summary>Show command.</summary>
        public ushort wShowWindow;

        /// <summary>Reserved.</summary>
        public ushort cbReserved2;

        /// <summary>Reserved.</summary>
        public byte* lpReserved2;

        /// <summary>Standard handles.</summary>
        public nint hStdInput, hStdOutput, hStdError;
    }

    /// <summary><c>STARTUPINFOEXW</c>: the startup info plus an attribute list.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        /// <summary>The plain startup info.</summary>
        public StartupInfo StartupInfo;

        /// <summary>The attribute list (<c>InitializeProcThreadAttributeList</c>).</summary>
        public byte* AttributeList;
    }

    /// <summary><c>PROCESS_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        /// <summary>The new process's handle.</summary>
        public nint Process;

        /// <summary>Its first thread's handle.</summary>
        public nint Thread;

        /// <summary>Process id.</summary>
        public uint ProcessId;

        /// <summary>Thread id.</summary>
        public uint ThreadId;
    }

    /// <summary>The current process's package full name (kernel32).</summary>
    /// <param name="packageFullNameLength">In: the buffer's size in characters; out: the size needed, terminator included.</param>
    /// <param name="packageFullName">The buffer, or <see langword="null"/> to ask for the size.</param>
    /// <returns>0 on success, 122 when the buffer is too small, 15700 when the process has no package identity.</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char* packageFullName);

    /// <summary>The current process's package family name (kernel32).</summary>
    /// <param name="packageFamilyNameLength">In: the buffer's size in characters; out: the size needed, terminator included.</param>
    /// <param name="packageFamilyName">The buffer, or <see langword="null"/> to ask for the size.</param>
    /// <returns>0 on success, 122 when the buffer is too small, 15700 when the process has no package identity.</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, char* packageFamilyName);

    /// <summary>Reads process information (ntdll); class 0 is <c>ProcessBasicInformation</c>.</summary>
    /// <param name="process">The process handle.</param>
    /// <param name="informationClass">Which information.</param>
    /// <param name="information">The buffer to fill.</param>
    /// <param name="length">Its size in bytes.</param>
    /// <param name="returnLength">Receives the size written; may be <see langword="null"/>.</param>
    /// <returns>An NTSTATUS; 0 is success.</returns>
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint process, int informationClass, void* information, uint length, uint* returnLength);

    /// <summary>Sizes or initializes a process attribute list (kernel32).</summary>
    /// <param name="attributeList">The list's memory, or <see langword="null"/> to ask for the size.</param>
    /// <param name="attributeCount">How many attributes it will hold.</param>
    /// <param name="flags">Reserved; 0.</param>
    /// <param name="size">In: the memory's size; out: the size needed.</param>
    /// <returns>Whether it succeeded (the sizing call fails by design with error 122).</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(byte* attributeList, uint attributeCount, uint flags, ref nuint size);

    /// <summary>Sets one attribute in a process attribute list (kernel32).</summary>
    /// <param name="attributeList">The initialized list.</param>
    /// <param name="flags">Reserved; 0.</param>
    /// <param name="attribute">Which attribute.</param>
    /// <param name="value">Its value; must stay valid until the list is deleted.</param>
    /// <param name="size">The value's size in bytes.</param>
    /// <param name="previousValue">Reserved; <see langword="null"/>.</param>
    /// <param name="returnSize">Reserved; <see langword="null"/>.</param>
    /// <returns>Whether it succeeded.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(byte* attributeList, uint flags, nint attribute, void* value, nuint size, void* previousValue, nuint* returnSize);

    /// <summary>Releases what an attribute list holds (kernel32); the memory itself is the caller's.</summary>
    /// <param name="attributeList">The list.</param>
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(byte* attributeList);

    /// <summary>Creates a process (kernel32), here with an extended startup info that carries an attribute list.</summary>
    /// <param name="applicationName">The program, or <see langword="null"/> to take it from the command line.</param>
    /// <param name="commandLine">The command line; the function may write into it.</param>
    /// <param name="processAttributes">Security attributes of the process; <see langword="null"/>.</param>
    /// <param name="threadAttributes">Security attributes of its first thread; <see langword="null"/>.</param>
    /// <param name="inheritHandles">Whether the child inherits this process's inheritable handles.</param>
    /// <param name="creationFlags">Creation flags.</param>
    /// <param name="environment">The child's environment, or <see langword="null"/> for this process's.</param>
    /// <param name="currentDirectory">The child's folder, or <see langword="null"/> for this process's.</param>
    /// <param name="startupInfo">The extended startup info.</param>
    /// <param name="processInformation">Receives the new process's handles and ids; the caller closes the handles.</param>
    /// <returns>Whether the process was created.</returns>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(char* applicationName, char* commandLine, void* processAttributes, void* threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, void* environment, char* currentDirectory, StartupInfoEx* startupInfo, out ProcessInformation processInformation);

    /// <summary>Waits for a handle to be signalled (kernel32).</summary>
    /// <param name="handle">The handle.</param>
    /// <param name="milliseconds">How long to wait.</param>
    /// <returns>0 when signalled, 258 on timeout.</returns>
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    /// <summary>Reads a process's exit code (kernel32).</summary>
    /// <param name="process">The process handle.</param>
    /// <param name="exitCode">Receives the code (259 while it still runs).</param>
    /// <returns>Whether it succeeded.</returns>
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(nint process, out uint exitCode);

    /// <summary>Closes a kernel handle (kernel32).</summary>
    /// <param name="handle">The handle.</param>
    /// <returns>Whether it succeeded.</returns>
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    /// <summary>The language of Windows' own interface for this user, as a language id (kernel32).</summary>
    /// <returns>The language id, e.g. 0x0409 for en-US.</returns>
    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    /// <summary>The user's preferred interface languages (kernel32).</summary>
    /// <param name="flags">The name format; 0x8 = language names.</param>
    /// <param name="numberOfLanguages">Receives how many there are.</param>
    /// <param name="languagesBuffer">A double-NUL-terminated list, or <see langword="null"/> to ask for the size.</param>
    /// <param name="bufferLength">In: the buffer's size in characters; out: the size needed.</param>
    /// <returns>Whether it succeeded.</returns>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserPreferredUILanguages(uint flags, out uint numberOfLanguages, char* languagesBuffer, ref uint bufferLength);

    /// <summary>The process's own preferred interface languages, when any were set (kernel32).</summary>
    /// <param name="flags">The name format; 0x8 = language names.</param>
    /// <param name="numberOfLanguages">Receives how many there are.</param>
    /// <param name="languagesBuffer">A double-NUL-terminated list, or <see langword="null"/> to ask for the size.</param>
    /// <param name="bufferLength">In: the buffer's size in characters; out: the size needed.</param>
    /// <returns>Whether it succeeded.</returns>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessPreferredUILanguages(uint flags, out uint numberOfLanguages, char* languagesBuffer, ref uint bufferLength);

    /// <summary>Asks to be told when a registry key changes (advapi32).</summary>
    /// <param name="key">The open key.</param>
    /// <param name="watchSubtree">Also its subkeys.</param>
    /// <param name="notifyFilter">Which changes: 0x1 names, 0x4 values.</param>
    /// <param name="notifyEvent">The event to signal.</param>
    /// <param name="asynchronous">Return at once and signal the event later.</param>
    /// <returns>0 on success, otherwise a Win32 error code.</returns>
    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(nint key, [MarshalAs(UnmanagedType.Bool)] bool watchSubtree, uint notifyFilter, nint notifyEvent, [MarshalAs(UnmanagedType.Bool)] bool asynchronous);

    /// <summary>Reads facts about a registry key (advapi32); only the last-write time is asked for here.</summary>
    /// <param name="key">The open key.</param>
    /// <param name="className">Unused; <see langword="null"/>.</param>
    /// <param name="classLength">Unused; <see langword="null"/>.</param>
    /// <param name="reserved">Reserved; <see langword="null"/>.</param>
    /// <param name="subKeys">Unused; <see langword="null"/>.</param>
    /// <param name="maxSubKeyLength">Unused; <see langword="null"/>.</param>
    /// <param name="maxClassLength">Unused; <see langword="null"/>.</param>
    /// <param name="values">Unused; <see langword="null"/>.</param>
    /// <param name="maxValueNameLength">Unused; <see langword="null"/>.</param>
    /// <param name="maxValueLength">Unused; <see langword="null"/>.</param>
    /// <param name="securityDescriptorLength">Unused; <see langword="null"/>.</param>
    /// <param name="lastWriteTime">Receives the key's last-write time as a FILETIME.</param>
    /// <returns>0 on success, otherwise a Win32 error code.</returns>
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryInfoKeyW(nint key, char* className, uint* classLength, uint* reserved, uint* subKeys, uint* maxSubKeyLength, uint* maxClassLength, uint* values, uint* maxValueNameLength, uint* maxValueLength, uint* securityDescriptorLength, out long lastWriteTime);
}
