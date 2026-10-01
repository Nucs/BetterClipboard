#:property PublishAot=false
#:property AllowUnsafeBlocks=true
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using static Native;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────
// Probe: how does a desktop app talk to voidtools Everything, and what exactly comes back?
//
// Two modes:
//   --exe <Everything.exe> [--instance NAME] [--keep] [--also-index <folder>]
//       Isolated. Copies that exe (+ Everything.lng) into %TEMP%\bc-everything-probe-<pid>\bin, writes a BC-TEST
//       file tree next to it and starts the copy as a private NAMED instance (-instance, -startup, -config, -db)
//       that indexes only that tree: no NTFS/ReFS volumes, no tray icon, no update check, no elevation, no
//       service. Then probes the WM_COPYDATA IPC (Everything 1.4 and 1.5) and, when present, the 1.5 named
//       pipe; asks the instance to exit (EVERYTHING_IPC_EXIT) and deletes the folder (unless --keep).
//       Paths are printed relative to the BC-TEST tree only. --also-index adds a big real folder (a folder
//       index: a read-only directory scan, no admin) for timings at scale; it prints counts and times only.
//   --attach [--instance NAME]
//       Read-only against an Everything that is already running (default: the unnamed instance, then "1.5a").
//       Prints version, state, timings and result COUNTS only — never a file name, because the index lists
//       the user's whole disk.
//
// Run: dotnet run tools/probes/probe_everything.cs -- --exe <path>\Everything.exe
// Portable zips: https://www.voidtools.com/downloads/ (check the published .sha256 and the voidtools
// signature before running a download).
// Results on Windows 11 26200 (2026-10-01), Everything 1.4.1.1032 and 1.5.0.1423b: see CLAUDE.md §2.14.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────

var options = ProbeOptions.Parse(args);
if (options is null)
{
    Console.WriteLine("usage: probe_everything.cs -- --exe <Everything.exe> [--instance NAME] [--keep] [--also-index <folder>]");
    Console.WriteLine("       probe_everything.cs -- --attach [--instance NAME]");
    return 2;
}

// Everything answers on the thread that owns the reply window, through sent messages; the whole probe runs on
// one STA thread that pumps while it waits, so no reply can arrive on a thread that is not listening.
int exitCode = 0;
var thread = new Thread(() => exitCode = options.Attach ? AttachProbe.Run(options) : IsolatedProbe.Run(options))
{
    IsBackground = false,
    Name = "Everything IPC probe",
};
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
return exitCode;

/// <summary>Command-line options of the probe; <see langword="null"/> from <see cref="Parse"/> means "print usage".</summary>
/// <param name="Exe">Everything.exe to copy and run as an isolated instance (isolated mode only).</param>
/// <param name="Instance">Instance name; isolated mode defaults to a private name, attach mode to "" then "1.5a".</param>
/// <param name="Attach">Talk to an already running Everything, printing counts only.</param>
/// <param name="Keep">Keep the temporary folder (tree, ini, db) for inspection after an isolated run.</param>
/// <param name="AlsoIndex">Extra real folder to index in isolated mode for timings at scale (counts only).</param>
sealed record ProbeOptions(string? Exe, string? Instance, bool Attach, bool Keep, string? AlsoIndex)
{
    /// <summary>Parses the probe's arguments.</summary>
    /// <param name="args">Raw arguments after <c>--</c>.</param>
    /// <returns>The options, or <see langword="null"/> when they are incomplete or contradictory.</returns>
    public static ProbeOptions? Parse(string[] args)
    {
        string? exe = null, instance = null, alsoIndex = null;
        bool attach = false, keep = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--exe" when i + 1 < args.Length: exe = args[++i]; break;
                case "--instance" when i + 1 < args.Length: instance = args[++i]; break;
                case "--also-index" when i + 1 < args.Length: alsoIndex = Path.GetFullPath(args[++i]).TrimEnd('\\'); break;
                case "--attach": attach = true; break;
                case "--keep": keep = true; break;
                default: return null;
            }
        }
        if (attach == (exe is not null) || (attach && alsoIndex is not null)) return null;
        return new ProbeOptions(exe, instance, attach, keep, alsoIndex);
    }
}

/// <summary>
/// Isolated mode: a private Everything instance over a BC-TEST tree, probed end to end and torn down again.
/// </summary>
static class IsolatedProbe
{
    /// <summary>Hebrew folder and file names (escapes keep the source ASCII), to check UTF-16 round trips.</summary>
    const string HebrewFolder = "\u05E2\u05D1\u05E8\u05D9\u05EA", HebrewFile = "\u05E7\u05D5\u05D1\u05E5.txt";

    /// <summary>
    /// File names with characters that mean something in Everything's search syntax (! NOT, ; list, % macros…);
    /// the characters Windows forbids in names (&lt; &gt; : " | ? *) cannot occur in a real path anyway.
    /// </summary>
    static readonly string[] WeirdNames =
    [
        "bang!name.txt", "semi;colon.txt", "amp&name.txt", "pct%20name.txt", "hash#name.txt", "quote'name.txt",
        "caret^name.txt", "comma,name.txt", "dollar$name.txt", "brace{x}.txt", "bracket[x].txt", "plus+eq=.txt",
        "tilde~1.txt", "paren (1).txt", "dash-name.txt", "at@name.txt",
    ];

    /// <summary>
    /// Runs the isolated probe; every created file, process and window is removed before returning.
    /// </summary>
    /// <param name="options">Parsed options; <see cref="ProbeOptions.Exe"/> is required.</param>
    /// <returns>0 when every expectation held, 1 when any failed, 3 when Everything could not be started.</returns>
    public static int Run(ProbeOptions options)
    {
        var instance = options.Instance ?? "BCPROBE" + Environment.ProcessId;
        var work = Path.Combine(Path.GetTempPath(), "bc-everything-probe-" + Environment.ProcessId);
        var bin = Path.Combine(work, "bin");
        var tree = Path.Combine(work, "tree");
        Directory.CreateDirectory(bin);
        var failures = 0;
        Process? everything = null;
        using var ipc = new IpcWindows();
        try
        {
            var foregroundBefore = Foreground.Describe();
            Console.WriteLine($"foreground before: {foregroundBefore}");

            // A copy, so the probe never writes an ini next to an installed Everything (Program Files would need
            // admin, and a real install's settings must stay untouched).
            var exeCopy = Path.Combine(bin, "Everything.exe");
            File.Copy(options.Exe!, exeCopy);
            var lng = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.Exe!))!, "Everything.lng");
            if (File.Exists(lng)) File.Copy(lng, Path.Combine(bin, "Everything.lng"));
            var version = FileVersionInfo.GetVersionInfo(exeCopy).FileVersion;
            Console.WriteLine($"exe version: {version}, instance: {instance}");

            BuildTree(tree);

            // app_data=0 is read only from the Everything.ini next to the exe: keeps search/run history, bookmarks
            // and the like out of %APPDATA%\Everything.
            File.WriteAllText(Path.Combine(bin, "Everything.ini"), "[Everything]\r\napp_data=0\r\n", new UTF8Encoding(false));
            var config = Path.Combine(work, "probe.ini");
            var db = Path.Combine(work, "probe.db");
            File.WriteAllText(config, string.Join("\r\n",
            [
                "[Everything]",
                "run_as_admin=0",
                "show_tray_icon=0",
                "check_for_updates_on_startup=0",
                "language=1033",
                "auto_include_fixed_volumes=0",
                "auto_include_removable_volumes=0",
                "auto_include_fixed_refs_volumes=0",
                "auto_include_removable_refs_volumes=0",
                "ntfs_volume_paths=",
                "ntfs_volume_includes=",
                // Quoted list values: inside quotes, backslash escapes (ini docs), so double every backslash.
                // The optional big folder is scanned once and not monitored (no ReadDirectoryChanges load).
                "folders=" + string.Join(",", new[] { tree, options.AlsoIndex }.OfType<string>().Select(f => "\"" + f.Replace("\\", "\\\\") + "\"")),
                "folder_monitor_changes=" + (options.AlsoIndex is null ? "1" : "1,0"),
                "index_size=1",
                "index_date_modified=1",
                "index_attributes=1",
                "",
            ]), new UTF8Encoding(false));

            var created = RegisterWindowMessageW("EVERYTHING_IPC_CREATED");
            var start = Stopwatch.StartNew();
            everything = Process.Start(new ProcessStartInfo(exeCopy)
            {
                // ShellExecute: the child must not inherit this console's handles (CLAUDE.md §4).
                UseShellExecute = true,
                ArgumentList = { "-instance", instance, "-startup", "-config", config, "-db", db },
                WorkingDirectory = bin,
            });
            if (everything is null) { Console.WriteLine("FAIL: Process.Start returned no process"); return 3; }

            var hwnd = IpcWindows.WaitForEverything(instance, TimeSpan.FromSeconds(15));
            Console.WriteLine($"IPC window {(hwnd == 0 ? "NOT found" : "found")} after {start.ElapsedMilliseconds} ms " +
                              $"(class {IpcWindows.ClassName(instance)}); EVERYTHING_IPC_CREATED broadcast seen: " +
                              $"{(ipc.CreatedBroadcastAt is { } at ? $"yes at {at} ms" : "no")} (msg 0x{created:X})");
            if (hwnd == 0) return 3;
            GetWindowThreadProcessId(hwnd, out var ownerPid);
            Console.WriteLine($"IPC window owner pid {ownerPid} (started pid {everything.Id}, " +
                              $"image {Path.GetFileName(ProcessImage.Path(ownerPid))})");

            PrintState(hwnd);

            // Everything answers no query while it builds its database (db_loaded=0 db_busy=1; a first folder scan
            // of K:\source was still running after 6 minutes), so wait on the state, not on a query. One query is
            // sent now, during the load, on the second reply window: is it dropped, or queued and answered later?
            var early = ipc.Send(hwnd, "wfn:\"notes.md\"", Query2.FullPath, 10, 1, window: 1);
            var ready = Stopwatch.StartNew();
            var limit = options.AlsoIndex is null ? TimeSpan.FromSeconds(20) : TimeSpan.FromMinutes(8);
            var reported = TimeSpan.Zero;
            while (ready.Elapsed < limit && !(Ask(hwnd, 401) == 1 && Ask(hwnd, 402) == 0))
            {
                if (ready.Elapsed - reported >= TimeSpan.FromSeconds(15))
                {
                    reported = ready.Elapsed;
                    Console.WriteLine($"  … {reported.TotalSeconds:0} s: db_loaded={Ask(hwnd, 401)} db_busy={Ask(hwnd, 402)}");
                }
                ipc.Pump(TimeSpan.FromMilliseconds(200));
            }
            Console.WriteLine($"database loaded and idle after {ready.ElapsedMilliseconds:N0} ms more");
            ipc.Pump(TimeSpan.FromMilliseconds(500));
            Console.WriteLine($"query sent during the load: {(ipc.ReplyDelay(early) is { } late ? $"answered {late:N0} ms after sending" : "never answered")}");
            PrintState(hwnd);

            // A folder index can report idle a moment before a known file is searchable: confirm with a query.
            var confirm = Stopwatch.StartNew();
            while (confirm.Elapsed < TimeSpan.FromSeconds(20) && ipc.Query(hwnd, "wfn:\"notes.md\"", Query2.FullPath, max: 10)?.Total != 1)
                ipc.Pump(TimeSpan.FromMilliseconds(200));
            Console.WriteLine($"BC-TEST tree searchable after {confirm.ElapsedMilliseconds} ms more");

            failures += RunQueryCases(ipc, hwnd, tree);
            RunLatency(ipc, hwnd, tree);
            failures += RunConcurrency(ipc, hwnd);
            if (options.AlsoIndex is { } big) RunScale(ipc, hwnd, big);
            failures += PipeProbe.Run(instance, tree, ownerPid);

            Console.WriteLine($"visible top-level windows of pid {ownerPid}: {Foreground.VisibleWindowCount(ownerPid)}");
            var foregroundAfter = Foreground.Describe();
            Console.WriteLine($"foreground after: {foregroundAfter} ({(foregroundAfter == foregroundBefore ? "unchanged" : "CHANGED")})");

            // EVERYTHING_IPC_EXIT returns 1 when the instance closes.
            var exitAnswer = SendMessageTimeoutW(hwnd, EVERYTHING_WM_IPC, (nint)EVERYTHING_IPC_EXIT, 0, SMTO_ABORTIFHUNG, 2000, out var exitResult);
            var exited = everything.WaitForExit(10_000);
            Console.WriteLine($"exit request: answered={exitAnswer != 0} result={exitResult}; process exited: {exited}");
            ReportWrittenFiles(work, tree);
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            if (everything is { HasExited: false })
            {
                // Only the process this probe started, and only when it ignored the exit request.
                try { everything.Kill(); everything.WaitForExit(5000); } catch (Exception e) { Console.WriteLine("kill: " + e.Message); }
            }
            if (!options.Keep)
            {
                for (var attempt = 0; attempt < 10 && Directory.Exists(work); attempt++)
                {
                    try { Directory.Delete(work, recursive: true); }
                    catch (IOException) { Thread.Sleep(300); }   // the exe/db can stay locked for a moment after exit
                    catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                }
                Console.WriteLine($"work folder removed: {!Directory.Exists(work)}");
            }
            else Console.WriteLine($"work folder kept: {work}");
        }
    }

    /// <summary>
    /// Privacy check after exit: which files did Everything write, and do any of them (besides its database,
    /// which lists the indexed names anyway) contain the probe's query texts? A client that puts copied paths
    /// into queries must know whether Everything keeps them, e.g. in its search history.
    /// </summary>
    /// <param name="work">The probe's work folder (bin, ini, db).</param>
    /// <param name="tree">The BC-TEST tree (excluded: it is the indexed data, not something Everything wrote).</param>
    static void ReportWrittenFiles(string work, string tree)
    {
        Console.WriteLine("── files in the work folder after exit (besides the BC-TEST tree) ──");
        // Fragments that exist only in queries, never in an indexed name: the modifiers, the names of files that
        // were looked up but do not exist (missing.md, index-missing.js) and the two-word user search.
        string[] queryMarkers = ["path:wfn:", "missing.md", "index-missing.js", "report final"];
        foreach (var file in Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories)
                     .Where(f => !f.StartsWith(tree + "\\", StringComparison.OrdinalIgnoreCase)))
        {
            var bytes = File.ReadAllBytes(file);
            var text = Encoding.UTF8.GetString(bytes) + Encoding.Unicode.GetString(bytes);
            var hits = queryMarkers.Where(m => text.Contains(m, StringComparison.Ordinal)).ToArray();
            Console.WriteLine($"  {Path.GetRelativePath(work, file)}: {bytes.Length:N0} bytes; query text inside: {(hits.Length == 0 ? "none" : string.Join(", ", hits))}");
        }
    }

    /// <summary>Creates the BC-TEST tree the instance indexes; contents are a marker plus padding to fixed sizes.</summary>
    /// <param name="tree">Root folder to fill (created).</param>
    static void BuildTree(string tree)
    {
        void Write(string relative, int size)
        {
            var full = Path.Combine(tree, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var bytes = new byte[size];
            Encoding.ASCII.GetBytes("BC-TEST").CopyTo(bytes, 0);
            File.WriteAllBytes(full, bytes);
        }

        Write(@"src\BetterClipboard.Core\Storage\ClipStore.cs", 1000);
        Write(@"other\src\BetterClipboard.Core\Storage\ClipStore.cs", 1001);
        Write(@"docs\notes.md", 64);
        Write(@"docs\notes.md.bak", 64);
        Write(@"a b\My Report (final).pdf", 2048);
        Write(Path.Combine(HebrewFolder, HebrewFile), 32);
        foreach (var name in WeirdNames) Write(Path.Combine("weird", name), 16);
        Write(@"Case\ReadMe.TXT", 16);
        Write(@"moved\report-2026.xlsx", 12345);
        for (var i = 0; i < 300; i++) Write($@"many\pkg{i:000}\index.js", 100 + i);
        Directory.CreateDirectory(Path.Combine(tree, @"src\app"));
        // Distinct modification times for the sort check: pkg299 newest.
        for (var i = 0; i < 300; i++)
            File.SetLastWriteTimeUtc(Path.Combine(tree, $@"many\pkg{i:000}\index.js"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i));
    }

    /// <summary>Asks Everything's IPC window one <c>EVERYTHING_WM_IPC</c> question (2 s limit, hung = no answer).</summary>
    /// <param name="hwnd">Everything's IPC window.</param>
    /// <param name="command">EVERYTHING_IPC_* query (0–5 version, 400+ index state).</param>
    /// <param name="lParam">Command argument (drive index, sort type, file-info type).</param>
    /// <returns>The answer, or -1 when the window did not answer.</returns>
    static int Ask(nint hwnd, uint command, nint lParam = 0) =>
        SendMessageTimeoutW(hwnd, EVERYTHING_WM_IPC, (nint)command, lParam, SMTO_ABORTIFHUNG, 2000, out var r) == 0 ? -1 : (int)r;

    /// <summary>Prints version, target machine and index state through <c>EVERYTHING_WM_IPC</c> messages.</summary>
    /// <param name="hwnd">Everything's IPC window.</param>
    static void PrintState(nint hwnd)
    {
        Console.WriteLine($"  version {Ask(hwnd, 0)}.{Ask(hwnd, 1)}.{Ask(hwnd, 2)}.{Ask(hwnd, 3)} target_machine={Ask(hwnd, 5)} " +
                          $"db_loaded={Ask(hwnd, 401)} db_busy={Ask(hwnd, 402)} is_admin={Ask(hwnd, 403)} is_appdata={Ask(hwnd, 404)} " +
                          $"ntfs_C_indexed={Ask(hwnd, 400, 2)} fast_sort(dm desc)={Ask(hwnd, 410, 14)} " +
                          $"info_indexed(size,dm,attr)={Ask(hwnd, 411, 1)},{Ask(hwnd, 411, 4)},{Ask(hwnd, 411, 6)}");
    }

    /// <summary>
    /// The query shapes an integration would send: exact name, exact full path, relative-path suffix, odd
    /// characters, Unicode, name+size relocation, many results, sorting and plain user text.
    /// </summary>
    /// <param name="ipc">Reply windows and pump.</param>
    /// <param name="hwnd">Everything's IPC window.</param>
    /// <param name="tree">The BC-TEST tree (results are printed relative to it).</param>
    /// <returns>Number of failed expectations.</returns>
    static int RunQueryCases(IpcWindows ipc, nint hwnd, string tree)
    {
        var failures = 0;
        string Rel(string? full) => full is null ? "?" : full.StartsWith(tree, StringComparison.OrdinalIgnoreCase) ? full[(tree.Length + 1)..] : "<outside tree>";
        string Quoted(string text) => "\"" + text + "\"";
        string Exact(string full) => "path:wfn:" + Quoted(full);

        // expectedTotal null = observe only: behavior this probe exists to find out, not a requirement.
        void Case(string label, string search, uint? expectedTotal, uint max = 50, uint sort = 1, uint request = Query2.FullPath | Query2.Size | Query2.DateModified | Query2.Attributes, bool show = true)
        {
            var sw = Stopwatch.StartNew();
            var list = ipc.Query(hwnd, search, request, max, sort);
            var ms = sw.Elapsed.TotalMilliseconds;
            if (list is null) { failures++; Console.WriteLine($"FAIL {label}: no reply ({ms:0.0} ms)"); return; }
            var ok = expectedTotal is null || list.Total == expectedTotal;
            if (!ok) failures++;
            var verdict = expectedTotal is null ? "info" : ok ? "ok  " : "FAIL";
            Console.WriteLine($"{verdict} {label}: total={list.Total}{(expectedTotal is { } want ? $" (want {want})" : "")} returned={list.Items.Length} " +
                              $"{ms:0.0} ms, {list.Bytes} bytes, flags=0x{list.RequestFlags:X} sort={list.Sort}");
            if (show && list.Items.Length <= 3)
                foreach (var item in list.Items)
                    Console.WriteLine($"       {(item.IsFolder ? "dir " : "file")} {Rel(item.FullPath)} size={item.Size} " +
                                      $"dm={item.Modified:yyyy-MM-dd HH:mm} attr=0x{item.Attributes:X}");
        }

        Console.WriteLine("── queries (WM_COPYDATA, EVERYTHING_IPC_COPYDATA_QUERY2W) ──");
        Case("exact name", "wfn:\"ClipStore.cs\"", 2);
        Case("relative suffix, unique (name + path contains)", "wfn:\"ClipStore.cs\" path:" + Quoted(@"\other\src\BetterClipboard.Core\Storage\ClipStore.cs"), 1);
        Case("relative suffix, ambiguous (name + path contains)", "wfn:\"ClipStore.cs\" path:" + Quoted(@"\src\BetterClipboard.Core\Storage\ClipStore.cs"), 2);
        Case("relative suffix via path:endwith:", "path:endwith:" + Quoted(@"\other\src\BetterClipboard.Core\Storage\ClipStore.cs"), null);
        Case("exact full path", Exact(Path.Combine(tree, @"docs\notes.md")), 1);
        Case("exact full path, other letter case", Exact(Path.Combine(tree, @"docs\notes.md").ToUpperInvariant()), 1);
        Case("plain path: (contains) also hits .bak", "path:" + Quoted(Path.Combine(tree, @"docs\notes.md")), 2);
        Case("exact full path of a folder", Exact(Path.Combine(tree, @"src\app")), 1);
        Case("exact full path, missing file", Exact(Path.Combine(tree, @"docs\missing.md")), 0);
        Case("exact full path with spaces and parentheses", Exact(Path.Combine(tree, @"a b\My Report (final).pdf")), 1);
        Case("exact full path, Hebrew", Exact(Path.Combine(tree, HebrewFolder, HebrewFile)), 1);
        Case("exact full path, forward slashes", Exact(Path.Combine(tree, @"docs\notes.md").Replace('\\', '/')), null);
        Case("exact full path, trailing backslash on a folder", Exact(Path.Combine(tree, @"src\app") + "\\"), null);
        foreach (var name in WeirdNames)
            Case($"exact full path, odd name {name}", Exact(Path.Combine(tree, "weird", name)), 1, show: false);
        Case("relocate by name + size", "wfn:\"report-2026.xlsx\" size:12345", 1);
        Case("relocate by name + wrong size", "wfn:\"report-2026.xlsx\" size:12346", 0);
        Case("many results, all requested", "wfn:\"index.js\"", 300, max: uint.MaxValue);
        Case("many results, first 20", "wfn:\"index.js\"", 300, max: 20);
        Case("many results, full path only", "wfn:\"index.js\"", 300, max: uint.MaxValue, request: Query2.FullPath);

        // Sort by date modified, newest first: pkg299 must come first.
        var sorted = ipc.Query(hwnd, "wfn:\"index.js\"", Query2.FullPath | Query2.DateModified, max: 3, sort: 14);
        var newestFirst = sorted?.Items.FirstOrDefault()?.FullPath?.Contains(@"\pkg299\", StringComparison.Ordinal) == true;
        if (!newestFirst) failures++;
        Console.WriteLine($"{(newestFirst ? "ok  " : "FAIL")} sort date-modified desc: first={Rel(sorted?.Items.FirstOrDefault()?.FullPath)} sort={sorted?.Sort}");

        Case("user text, two words (AND)", "report final", 1);
        Case("user text that is a relative path with forward slashes", "other/src/BetterClipboard.Core", null);
        Case("user text that is a relative path with backslashes", @"other\src\BetterClipboard.Core", null);
        return failures;
    }

    /// <summary>
    /// Times 30 single-result queries and one 20-path OR query (a batch existence check), spinning instead of
    /// waiting so the numbers are Everything's.
    /// </summary>
    /// <param name="ipc">Reply windows.</param>
    /// <param name="hwnd">Everything's IPC window.</param>
    /// <param name="tree">The BC-TEST tree.</param>
    static void RunLatency(IpcWindows ipc, nint hwnd, string tree)
    {
        Console.WriteLine("── latency (spinning pump) ──");
        var exact = "path:wfn:\"" + Path.Combine(tree, @"docs\notes.md") + "\"";
        var samples = Enumerable.Range(0, 30).Select(_ => ipc.Time(hwnd, exact)).Where(s => s is not null).Select(s => s!.Value).ToArray();
        if (samples.Length == 0) { Console.WriteLine("  no replies"); return; }
        double Percentile(IEnumerable<double> values, double q) { var v = values.Order().ToArray(); return v[(int)Math.Min(v.Length - 1, Math.Floor(q * v.Length))]; }
        Console.WriteLine($"  single exact path, {samples.Length} runs: reply median {Percentile(samples.Select(s => s.ReplyMs), 0.5):0.00} ms, " +
                          $"p90 {Percentile(samples.Select(s => s.ReplyMs), 0.9):0.00} ms; inside SendMessage median {Percentile(samples.Select(s => s.CallMs), 0.5):0.00} ms; " +
                          $"reply nested in the call: {samples.Count(s => s.Nested)}/{samples.Length}");
        // One OR query for 20 paths (half of them missing): the shape of "which of these cards still exist?".
        var batch = string.Join("|", Enumerable.Range(0, 20).Select(i => "path:wfn:\"" + Path.Combine(tree, $@"many\pkg{i * 2:000}\index{(i % 2 == 0 ? "" : "-missing")}.js") + "\""));
        var timed = ipc.Time(hwnd, batch);
        Console.WriteLine($"  20-path OR query ({batch.Length} chars): {timed?.Total} of 20 exist (want 10), reply {timed?.ReplyMs:0.00} ms");
    }

    /// <summary>
    /// Timings at scale over a real folder index (called after <see cref="Run"/> waited for the scan), aimed at
    /// sampled real files with the query shapes an integration would use. Prints counts and milliseconds only:
    /// the folder may hold the user's own files.
    /// </summary>
    /// <param name="ipc">Reply windows.</param>
    /// <param name="hwnd">Everything's IPC window.</param>
    /// <param name="folder">The extra indexed folder.</param>
    static void RunScale(IpcWindows ipc, nint hwnd, string folder)
    {
        Console.WriteLine("── scale (extra folder index; counts and times only) ──");
        // Run only after the load wait in Run: the database is loaded and idle here.
        Console.WriteLine($"  indexed items: {ipc.Time(hwnd, "")?.Total ?? 0:N0}");

        // Real files of that folder to aim at (names go into queries, never into the output): ten files at least
        // three levels down, so each has a two-folder relative suffix like a path copied from a repository.
        var samples = SampleFiles(folder, minDepth: 3, count: 10);
        if (samples.Count == 0) { Console.WriteLine("  no deep enough files to aim at"); return; }
        var target = samples[0];
        var name = Path.GetFileName(target);
        var segments = target[(folder.Length + 1)..].Split('\\');
        var suffix = "\\" + string.Join("\\", segments[^3..]);   // "\folder\folder\file.ext"
        var batch = string.Join("|", samples.Concat(samples.Select(f => f + ".missing")).Select(f => "path:wfn:\"" + f + "\""));
        // Same check with each path's name as a leading term (< > groups): the name index narrows first.
        var namedBatch = string.Join("|", samples.Concat(samples.Select(f => f + ".missing"))
            .Select(f => "<wfn:\"" + Path.GetFileName(f) + "\" path:wfn:\"" + f + "\">"));
        foreach (var (label, search, sort) in new (string, string, uint)[]
        {
            ("everything (empty search)", "", 1),
            ("exact name, common (index.js)", "wfn:\"index.js\"", 1),
            ("exact name, common (README.md)", "wfn:\"README.md\"", 1),
            ("exact name of a sample", "wfn:\"" + name + "\"", 1),
            ("relative suffix: name + path contains", "wfn:\"" + name + "\" path:\"" + suffix + "\"", 1),
            ("relative suffix: path:endwith: only", "path:endwith:\"" + suffix + "\"", 1),
            ("path contains only", "path:\"" + suffix + "\"", 1),
            ("exact full path", "path:wfn:\"" + target + "\"", 1),
            ("exact full path + name term", "wfn:\"" + name + "\" path:wfn:\"" + target + "\"", 1),
            ($"{samples.Count * 2}-path OR batch (half missing)", batch, 1),
            ($"{samples.Count * 2}-path OR batch with name terms", namedBatch, 1),
            ("user text, two words", "clip store", 1),
            ("ext:cs", "ext:cs", 1),
            ("ext:png newest first", "ext:png", 14),
        })
        {
            var times = Enumerable.Range(0, 5).Select(_ => ipc.Time(hwnd, search, max: 50, sort: sort)).Where(t => t is not null).Select(t => t!.Value).ToArray();
            if (times.Length == 0) { Console.WriteLine($"  {label}: no reply"); continue; }
            var median = times.Select(t => t.ReplyMs).Order().ElementAt(times.Length / 2);
            Console.WriteLine($"  {label}: total={times[0].Total:N0}, median {median:0.0} ms (first {times[0].ReplyMs:0.0} ms)");
        }
    }

    /// <summary>Picks files at least <paramref name="minDepth"/> levels below <paramref name="root"/>, breadth first.</summary>
    /// <param name="root">Folder to walk (unreadable subfolders are skipped).</param>
    /// <param name="minDepth">Fewest folder levels between the root and the file.</param>
    /// <param name="count">How many files to return.</param>
    /// <returns>Up to <paramref name="count"/> full paths.</returns>
    static List<string> SampleFiles(string root, int minDepth, int count)
    {
        var found = new List<string>();
        var level = new List<string> { root };
        for (var depth = 0; depth <= minDepth + 3 && level.Count > 0 && found.Count < count; depth++)
        {
            var next = new List<string>();
            foreach (var folder in level)
            {
                try
                {
                    if (depth >= minDepth)
                        found.AddRange(Directory.EnumerateFiles(folder).Take(count - found.Count));
                    next.AddRange(Directory.EnumerateDirectories(folder).Take(50));
                }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException) { }
                if (found.Count >= count) break;
            }
            level = next;
        }
        return found;
    }

    /// <summary>
    /// "Everything will only do one query per window": checks what a second query does to a pending one, on the
    /// same reply window and on two different ones.
    /// </summary>
    /// <param name="ipc">Reply windows and pump.</param>
    /// <param name="hwnd">Everything's IPC window.</param>
    /// <returns>Number of failed expectations (the second query must always be answered).</returns>
    static int RunConcurrency(IpcWindows ipc, nint hwnd)
    {
        Console.WriteLine("── concurrency ──");
        var failures = 0;
        foreach (var separateWindows in new[] { false, true })
        {
            var a = ipc.Send(hwnd, "wfn:\"index.js\"", Query2.FullPath | Query2.Size, uint.MaxValue, 1, window: 0);
            var b = ipc.Send(hwnd, "wfn:\"notes.md\"", Query2.FullPath, 10, 1, window: separateWindows ? 1 : 0);
            ipc.Pump(TimeSpan.FromSeconds(2));
            var gotA = ipc.TryTake(a, out var listA);
            var gotB = ipc.TryTake(b, out var listB);
            if (!gotB) failures++;
            Console.WriteLine($"{(gotB ? "ok  " : "FAIL")} {(separateWindows ? "two reply windows" : "one reply window ")}: " +
                              $"first query answered={gotA} (total {listA?.Total}), second answered={gotB} (total {listB?.Total})");
        }
        return failures;
    }
}

/// <summary>Attach mode: read-only counts and timings against an Everything that is already running.</summary>
static class AttachProbe
{
    /// <summary>Finds the instance, prints its state and times a few typical queries (counts only).</summary>
    /// <param name="options">Parsed options; <see cref="ProbeOptions.Instance"/> picks the instance.</param>
    /// <returns>0 on success, 3 when no Everything instance answers.</returns>
    public static int Run(ProbeOptions options)
    {
        using var ipc = new IpcWindows();
        // Unnamed = Everything 1.4 and 1.5 beta; "1.5a" = the 1.5 alpha, which ran side by side with 1.4.
        string[] candidates = options.Instance is { } name ? [name] : ["", "1.5a"];
        foreach (var instance in candidates)
        {
            var hwnd = FindWindowW(IpcWindows.ClassName(instance), null);
            Console.WriteLine($"instance '{instance}': class {IpcWindows.ClassName(instance)} -> {(hwnd == 0 ? "not running" : "found")}");
            if (hwnd == 0) continue;
            GetWindowThreadProcessId(hwnd, out var pid);
            Console.WriteLine($"  owner image {Path.GetFileName(ProcessImage.Path(pid))}, elevated owner: {ProcessImage.IsElevated(pid)?.ToString() ?? "unknown"}");
            foreach (var (label, search, sort) in new[]
            {
                ("exact name notepad.exe", "wfn:\"notepad.exe\"", 1u),
                ("all .cs files", "ext:cs", 1u),
                ("modified today, newest first", "dm:today", 14u),
            })
            {
                // Spinning timer: the number is Everything's, not this thread's wait granularity.
                var timed = ipc.Time(hwnd, search, max: 50, sort: sort);
                Console.WriteLine(timed is { } t ? $"  {label}: total={t.Total:N0}, reply {t.ReplyMs:0.0} ms" : $"  {label}: no reply (database loading?)");
            }
            PipeProbe.Run(instance, tree: null, pid);
            return 0;
        }
        return 3;
    }
}

/// <summary>
/// Everything 1.5's named-pipe IPC ("IPC3"): message = {DWORD code, DWORD size} + payload in both directions,
/// responses 200 OK / 100 more data / 4xx-5xx errors (voidtools/es src/ipc3.h, MIT).
/// </summary>
static class PipeProbe
{
    /// <summary>Probes the pipe of an instance if it exists (Everything 1.4 has none).</summary>
    /// <param name="instance">Instance name; "" is the unnamed instance.</param>
    /// <param name="tree">BC-TEST tree for attribute lookups, or <see langword="null"/> in attach mode (no paths sent).</param>
    /// <param name="expectedPid">Process that owns the IPC window: the pipe server must be the same process.</param>
    /// <returns>Number of failed expectations (0 when the pipe does not exist).</returns>
    public static int Run(string instance, string? tree, uint expectedPid)
    {
        var pipeName = instance.Length == 0 ? "Everything IPC" : $"Everything IPC ({instance})";
        Console.WriteLine($"── named pipe \\\\.\\pipe\\{pipeName} ──");
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        try { pipe.Connect(1000); }
        catch (TimeoutException) { Console.WriteLine("  no pipe (Everything 1.4, or the pipe server is busy)"); return 0; }

        var failures = 0;
        GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var serverPid);
        var samePid = serverPid == expectedPid;
        if (!samePid) failures++;
        Console.WriteLine($"  {(samePid ? "ok  " : "FAIL")} pipe server pid {serverPid} == IPC window pid {expectedPid}");

        uint Dword(uint command, byte[]? payload = null)
        {
            var (code, data) = Call(pipe, command, payload ?? []);
            return code == 200 && data.Length >= 4 ? BitConverter.ToUInt32(data, 0) : 0xFFFFFFFF;
        }
        Console.WriteLine($"  pipe version {Dword(0)}, Everything {Dword(1)}.{Dword(2)}.{Dword(3)}.{Dword(4)}, target {Dword(5)}, db loaded {Dword(8)}");

        if (tree is not null)
        {
            foreach (var (label, path, expectFound) in new[]
            {
                ("indexed file", Path.Combine(tree, @"docs\notes.md"), true),
                ("indexed folder", Path.Combine(tree, @"src\app"), true),
                ("missing file", Path.Combine(tree, @"docs\missing.md"), false),
                ("file on disk but not indexed", Environment.ProcessPath ?? @"C:\Windows\System32\notepad.exe", false),
            })
            {
                var sw = Stopwatch.StartNew();
                var (code, data) = Call(pipe, 19, Encoding.UTF8.GetBytes(path));
                var found = code == 200 && data.Length >= 4 && BitConverter.ToUInt32(data, 0) != 0xFFFFFFFF;
                var ok = found == expectFound;
                if (!ok) failures++;
                Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} GET_FILE_ATTRIBUTES {label}: response {code}, " +
                                  $"attributes {(data.Length >= 4 ? "0x" + BitConverter.ToUInt32(data, 0).ToString("X") : "-")}, {sw.Elapsed.TotalMilliseconds:0.00} ms");
            }
        }
        return failures;
    }

    /// <summary>Sends one IPC3 command and collects the whole response (following "more data" frames).</summary>
    /// <param name="pipe">Connected pipe.</param>
    /// <param name="command">IPC3_COMMAND_* code.</param>
    /// <param name="payload">Command input (UTF-8 path for GET_FILE_ATTRIBUTES; empty for the getters).</param>
    /// <returns>The final response code and the concatenated payload.</returns>
    /// <exception cref="EndOfStreamException">The server closed the pipe mid-response.</exception>
    static (uint Code, byte[] Data) Call(NamedPipeClientStream pipe, uint command, byte[] payload)
    {
        var header = new byte[8];
        BitConverter.TryWriteBytes(header.AsSpan(0, 4), command);
        BitConverter.TryWriteBytes(header.AsSpan(4, 4), (uint)payload.Length);
        pipe.Write(header);
        pipe.Write(payload);
        pipe.Flush();
        var all = new MemoryStream();
        while (true)
        {
            pipe.ReadExactly(header);
            var code = BitConverter.ToUInt32(header, 0);
            var size = BitConverter.ToUInt32(header, 4);
            var data = new byte[size];
            pipe.ReadExactly(data);
            all.Write(data);
            if (code != 100) return (code, all.ToArray());
        }
    }
}

/// <summary>Request flags of EVERYTHING_IPC_QUERY2 (everything_ipc.h). Item data follows this bit order.</summary>
static class Query2
{
    /// <summary>Full path and name: DWORD length + UTF-16 text + NUL.</summary>
    public const uint FullPath = 0x4;
    /// <summary>Size: 8-byte LARGE_INTEGER.</summary>
    public const uint Size = 0x10;
    /// <summary>Date modified: 8-byte FILETIME (UTC).</summary>
    public const uint DateModified = 0x40;
    /// <summary>Attributes: DWORD.</summary>
    public const uint Attributes = 0x100;
}

/// <summary>One result of a QUERY2 reply.</summary>
/// <param name="Flags">EVERYTHING_IPC_FOLDER (1) / EVERYTHING_IPC_ROOT (2).</param>
/// <param name="FullPath">Full path, when requested and returned.</param>
/// <param name="Size">Size in bytes, when requested and returned.</param>
/// <param name="Modified">Date modified (UTC), when requested and returned.</param>
/// <param name="Attributes">File attributes, when requested and returned.</param>
sealed record EvItem(uint Flags, string? FullPath, long? Size, DateTime? Modified, uint? Attributes)
{
    /// <summary>Whether Everything flagged the result as a folder.</summary>
    public bool IsFolder => (Flags & 1) != 0;
}

/// <summary>A parsed EVERYTHING_IPC_LIST2 reply.</summary>
/// <param name="Total">Number of matches in the whole index.</param>
/// <param name="Offset">Index of the first returned match.</param>
/// <param name="RequestFlags">Fields actually present per item (may differ from the request).</param>
/// <param name="Sort">Sort actually applied (may differ from the request).</param>
/// <param name="Items">The returned window of results.</param>
/// <param name="Bytes">Size of the WM_COPYDATA payload.</param>
sealed record EvList(uint Total, uint Offset, uint RequestFlags, uint Sort, EvItem[] Items, int Bytes)
{
    /// <summary>
    /// Parses an EVERYTHING_IPC_LIST2 buffer: header {totitems, numitems, offset, request_flags, sort_type},
    /// then numitems × {flags, data_offset}; each item's fields sit at data_offset in request-bit order
    /// (voidtools/es _es_ipc2_get_column_data; the header comment in everything_ipc.h lists another order).
    /// </summary>
    /// <param name="data">Copied WM_COPYDATA payload.</param>
    /// <returns>The parsed list.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The buffer is shorter than its own offsets claim.</exception>
    public static EvList Parse(byte[] data)
    {
        uint U32(int at) => BitConverter.ToUInt32(data, at);
        var total = U32(0); var count = U32(4); var offset = U32(8); var flags = U32(12); var sort = U32(16);
        var items = new EvItem[count];
        for (var i = 0; i < count; i++)
        {
            var itemFlags = U32(20 + i * 8);
            var p = (int)U32(20 + i * 8 + 4);
            string? fullPath = null; long? size = null; DateTime? modified = null; uint? attributes = null;
            // DWORD length in characters (without the terminator), the characters, then a NUL.
            string Text() { var len = (int)U32(p); var s = Encoding.Unicode.GetString(data, p + 4, len * 2); p += 4 + (len + 1) * 2; return s; }
            for (var bit = 1u; bit <= 0x8000; bit <<= 1)
            {
                if ((flags & bit) == 0) continue;
                switch (bit)
                {
                    case 0x1 or 0x2 or 0x8 or 0x200 or 0x2000 or 0x4000 or 0x8000: _ = Text(); break;   // name, path, ext, file list, highlights
                    case 0x4: fullPath = Text(); break;
                    case 0x10: size = BitConverter.ToInt64(data, p); p += 8; break;
                    case 0x40:
                        var ft = BitConverter.ToInt64(data, p); p += 8;
                        modified = ft is > 0 and < 0x7FFF_FFFF_FFFF_FFFF ? DateTime.FromFileTimeUtc(ft) : null;
                        break;
                    case 0x20 or 0x80 or 0x800 or 0x1000: p += 8; break;   // created, accessed, run date, recently changed
                    case 0x100: attributes = U32(p); p += 4; break;
                    case 0x400: p += 4; break;   // run count
                }
            }
            items[i] = new EvItem(itemFlags, fullPath, size, modified, attributes);
        }
        return new EvList(total, offset, flags, sort, items, data.Length);
    }
}

/// <summary>
/// The probe's windows on the probe thread: two message-only reply windows (does Everything answer a
/// message-only window?) and one hidden top-level window that can hear the EVERYTHING_IPC_CREATED broadcast
/// (broadcasts never reach message-only windows).
/// </summary>
sealed class IpcWindows : IDisposable
{
    static readonly WndProc Proc = WindowProc;   // kept alive: native code holds the pointer
    static readonly Dictionary<uint, byte[]> Replies = [];
    static readonly Dictionary<uint, long> ArrivedAt = [];
    static readonly Dictionary<uint, (long Before, long After)> SentAt = [];
    static uint createdMessage;
    static Stopwatch? sinceCreate;
    static long? createdAt;
    static uint nextId = 0x1000;
    readonly nint[] replyWindows = new nint[2];
    readonly nint listener;

    /// <summary>Creates the windows on the calling (pumping) thread.</summary>
    /// <exception cref="InvalidOperationException">Window class or window creation failed.</exception>
    public IpcWindows()
    {
        createdMessage = RegisterWindowMessageW("EVERYTHING_IPC_CREATED");
        sinceCreate = Stopwatch.StartNew();
        var cls = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = "BCProbe.EverythingReply",
        };
        if (RegisterClassExW(ref cls) == 0) throw new InvalidOperationException("RegisterClassEx " + Marshal.GetLastPInvokeError());
        for (var i = 0; i < replyWindows.Length; i++)
        {
            replyWindows[i] = CreateWindowExW(0, cls.lpszClassName, "", 0, 0, 0, 0, 0, HWND_MESSAGE, 0, cls.hInstance, 0);
            if (replyWindows[i] == 0) throw new InvalidOperationException("CreateWindowEx " + Marshal.GetLastPInvokeError());
            // Lets an Everything at a lower integrity level answer if this probe runs elevated (what ES does).
            ChangeWindowMessageFilterEx(replyWindows[i], WM_COPYDATA, MSGFLT_ALLOW, 0);
        }
        listener = CreateWindowExW(0, cls.lpszClassName, "", WS_POPUP, 0, 0, 0, 0, 0, 0, cls.hInstance, 0);
    }

    /// <summary>Milliseconds after construction at which the EVERYTHING_IPC_CREATED broadcast arrived, if it did.</summary>
    public long? CreatedBroadcastAt => createdAt;

    /// <summary>Everything's IPC window class for an instance ("" = unnamed).</summary>
    /// <param name="instance">Instance name.</param>
    /// <returns><c>EVERYTHING_TASKBAR_NOTIFICATION</c> or <c>EVERYTHING_TASKBAR_NOTIFICATION_(name)</c>.</returns>
    public static string ClassName(string instance) =>
        instance.Length == 0 ? "EVERYTHING_TASKBAR_NOTIFICATION" : $"EVERYTHING_TASKBAR_NOTIFICATION_({instance})";

    /// <summary>Polls for the IPC window while pumping (so the creation broadcast is received meanwhile).</summary>
    /// <param name="instance">Instance name.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>The window, or 0 on timeout.</returns>
    public static nint WaitForEverything(string instance, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var hwnd = FindWindowW(ClassName(instance), null);
            if (hwnd != 0) return hwnd;
            PumpOnce(TimeSpan.FromMilliseconds(20));
        }
        return 0;
    }

    /// <summary>Sends a QUERY2 and pumps until its reply or a 5 s timeout.</summary>
    /// <param name="everything">Everything's IPC window.</param>
    /// <param name="search">Search text in Everything's syntax.</param>
    /// <param name="request">QUERY2 request flags.</param>
    /// <param name="max">Maximum results (uint.MaxValue = all).</param>
    /// <param name="sort">EVERYTHING_IPC_SORT_* (1 = name ascending, always fast).</param>
    /// <returns>The reply, or <see langword="null"/> when the query was refused or not answered in time.</returns>
    public EvList? Query(nint everything, string search, uint request, uint max, uint sort = 1)
    {
        var id = Send(everything, search, request, max, sort, window: 0);
        if (id == 0) return null;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (TryTake(id, out var list)) return list;
            PumpOnce(TimeSpan.FromMilliseconds(5));
        }
        return null;
    }

    /// <summary>Sends a QUERY2 without waiting.</summary>
    /// <param name="everything">Everything's IPC window.</param>
    /// <param name="search">Search text.</param>
    /// <param name="request">QUERY2 request flags.</param>
    /// <param name="max">Maximum results.</param>
    /// <param name="sort">Sort type.</param>
    /// <param name="window">Which reply window (0 or 1).</param>
    /// <returns>The reply id (COPYDATASTRUCT.dwData of the answer), or 0 when Everything refused the query.</returns>
    public unsafe uint Send(nint everything, string search, uint request, uint max, uint sort, int window)
    {
        var id = ++nextId;
        // EVERYTHING_IPC_QUERY2 (packed): 7 DWORDs then the NUL-terminated UTF-16 search. reply_hwnd is a DWORD:
        // window handles fit in 32 bits even on x64.
        var size = 7 * 4 + (search.Length + 1) * 2;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var p = (uint*)buffer;
            p[0] = (uint)replyWindows[window]; p[1] = id; p[2] = 0; p[3] = 0; p[4] = max; p[5] = request; p[6] = sort;
            fixed (char* text = search) Buffer.MemoryCopy(text, (byte*)buffer + 28, size - 28, search.Length * 2);
            ((char*)((byte*)buffer + 28))[search.Length] = '\0';
            var cds = new COPYDATASTRUCT { dwData = 18, cbData = (uint)size, lpData = buffer };   // EVERYTHING_IPC_COPYDATA_QUERY2W
            // SMTO_NORMAL (not SMTO_BLOCK): Everything may send the reply while we wait; we must be able to take it.
            var before = Stopwatch.GetTimestamp();
            var answered = SendMessageTimeoutW(everything, WM_COPYDATA, replyWindows[window], (nint)(&cds), SMTO_ABORTIFHUNG, 2000, out var result);
            SentAt[id] = (before, Stopwatch.GetTimestamp());
            return answered != 0 && result != 0 ? id : 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Takes a received reply.</summary>
    /// <param name="id">Reply id from <see cref="Send"/>.</param>
    /// <param name="list">The parsed reply.</param>
    /// <returns>Whether that reply has arrived.</returns>
    public bool TryTake(uint id, out EvList? list)
    {
        list = null;
        if (!Replies.Remove(id, out var data)) return false;
        list = EvList.Parse(data);
        return true;
    }

    /// <summary>
    /// Sends a QUERY2 and spins on PeekMessage (no blocking wait) until the reply, to time Everything rather than
    /// this thread's wait granularity.
    /// </summary>
    /// <param name="everything">Everything's IPC window.</param>
    /// <param name="search">Search text.</param>
    /// <returns>Time inside SendMessage, time from the send to the reply, whether the reply arrived while
    /// SendMessage was still running, and the reply's total; <see langword="null"/> without a reply within 2 s.</returns>
    /// <param name="max">Maximum results to transfer.</param>
    /// <param name="sort">EVERYTHING_IPC_SORT_*.</param>
    public (double CallMs, double ReplyMs, bool Nested, uint Total)? Time(nint everything, string search, uint max = 10, uint sort = 1)
    {
        var id = Send(everything, search, Query2.FullPath, max, sort, window: 0);
        if (id == 0) return null;
        // Long enough for a path search over millions of items.
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 30;
        while (!ArrivedAt.ContainsKey(id) && Stopwatch.GetTimestamp() < deadline)
            while (PeekMessageW(out var msg, 0, 0, 0, PM_REMOVE)) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
        if (!ArrivedAt.Remove(id, out var arrived)) return null;
        Replies.Remove(id, out var data);
        var (before, after) = SentAt[id];
        return (Stopwatch.GetElapsedTime(before, after).TotalMilliseconds, Stopwatch.GetElapsedTime(before, arrived).TotalMilliseconds,
                arrived <= after, data is null ? 0 : EvList.Parse(data).Total);
    }

    /// <summary>How long after sending a query its reply arrived.</summary>
    /// <param name="id">Reply id from <see cref="Send"/>.</param>
    /// <returns>Milliseconds from the send to the reply, or <see langword="null"/> when it has not arrived.</returns>
    public double? ReplyDelay(uint id) =>
        id != 0 && ArrivedAt.TryGetValue(id, out var arrived) && SentAt.TryGetValue(id, out var sent)
            ? Stopwatch.GetElapsedTime(sent.Before, arrived).TotalMilliseconds
            : null;

    /// <summary>Pumps messages for a fixed time.</summary>
    /// <param name="duration">How long.</param>
    public void Pump(TimeSpan duration)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration) PumpOnce(TimeSpan.FromMilliseconds(10));
    }

    /// <summary>Dispatches pending messages (sent messages are delivered inside PeekMessage), then waits briefly.</summary>
    /// <param name="wait">Longest wait for new input.</param>
    static void PumpOnce(TimeSpan wait)
    {
        while (PeekMessageW(out var msg, 0, 0, 0, PM_REMOVE)) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
        MsgWaitForMultipleObjects(0, 0, false, (uint)wait.TotalMilliseconds, QS_ALLINPUT);
    }

    /// <summary>Copies WM_COPYDATA replies (the buffer is only valid during the call) and notes the broadcast.</summary>
    static unsafe nint WindowProc(nint hwnd, uint msg, nint w, nint l)
    {
        if (msg == WM_COPYDATA)
        {
            var cds = (COPYDATASTRUCT*)l;
            var data = new byte[cds->cbData];
            Marshal.Copy(cds->lpData, data, 0, data.Length);
            Replies[(uint)cds->dwData] = data;
            ArrivedAt[(uint)cds->dwData] = Stopwatch.GetTimestamp();
            return 1;
        }
        if (msg == createdMessage && createdMessage != 0)
        {
            createdAt ??= sinceCreate?.ElapsedMilliseconds;
            return 0;
        }
        return DefWindowProcW(hwnd, msg, w, l);
    }

    /// <summary>Destroys the windows.</summary>
    public void Dispose()
    {
        foreach (var hwnd in replyWindows) if (hwnd != 0) DestroyWindow(hwnd);
        if (listener != 0) DestroyWindow(listener);
    }
}

/// <summary>Foreground and visibility checks: the probe must not steal focus or show windows.</summary>
static class Foreground
{
    /// <summary>Describes the foreground window by its process name only (titles can be private).</summary>
    /// <returns>"process.exe" or "none".</returns>
    public static string Describe()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return "none";
        GetWindowThreadProcessId(hwnd, out var pid);
        return Path.GetFileName(ProcessImage.Path(pid)) ?? $"pid {pid}";
    }

    /// <summary>Counts visible top-level windows of a process.</summary>
    /// <param name="pid">Process id.</param>
    /// <returns>Number of visible top-level windows.</returns>
    public static int VisibleWindowCount(uint pid)
    {
        var count = 0;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid && IsWindowVisible(hwnd)) count++;
            return true;
        }, 0);
        return count;
    }
}

/// <summary>Process image path and elevation, for "who answered?" checks.</summary>
static class ProcessImage
{
    /// <summary>Full image path of a process.</summary>
    /// <param name="pid">Process id.</param>
    /// <returns>The path, or <see langword="null"/> when it cannot be opened.</returns>
    public static string? Path(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == 0) return null;
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>Whether a process runs elevated (TokenElevation).</summary>
    /// <param name="pid">Process id.</param>
    /// <returns>The answer, or <see langword="null"/> when the token cannot be read.</returns>
    public static bool? IsElevated(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == 0) return null;
        try
        {
            if (!OpenProcessToken(handle, 0x0008 /* TOKEN_QUERY */, out var token)) return null;
            try { return GetTokenInformation(token, 20 /* TokenElevation */, out var elevated, 4, out _) ? elevated != 0 : null; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(handle); }
    }
}

/// <summary>Win32 declarations used by the probe.</summary>
static class Native
{
    public const uint WM_COPYDATA = 0x004A, EVERYTHING_WM_IPC = 0x0400, EVERYTHING_IPC_EXIT = 4;
    public const uint SMTO_ABORTIFHUNG = 0x2, PM_REMOVE = 0x1, QS_ALLINPUT = 0x04FF, MSGFLT_ALLOW = 1, WS_POPUP = 0x80000000;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public static readonly nint HWND_MESSAGE = -3;

    public delegate nint WndProc(nint hwnd, uint msg, nint w, nint l);
    public delegate bool EnumWindowsProc(nint hwnd, nint l);

    [StructLayout(LayoutKind.Sequential)] public struct COPYDATASTRUCT { public nuint dwData; public uint cbData; public nint lpData; }
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public nint hwnd; public uint message; public nint wParam; public nint lParam; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize, style; public nint lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public nint hIconSm;
    }

    [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode)] public static extern nint FindWindowW(string cls, string? title);
    [DllImport("user32", SetLastError = true)] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32", SetLastError = true)] public static extern nint SendMessageTimeoutW(nint hwnd, uint msg, nint w, nint l, uint flags, uint timeout, out nint result);
    [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32", SetLastError = true)] public static extern ushort RegisterClassExW(ref WNDCLASSEXW cls);
    [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode)] public static extern nint CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32", SetLastError = true)] public static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32")] public static extern nint DefWindowProcW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32", SetLastError = true)] public static extern bool ChangeWindowMessageFilterEx(nint hwnd, uint msg, uint action, nint info);
    [DllImport("user32")] public static extern bool PeekMessageW(out MSG msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32")] public static extern nint DispatchMessageW(ref MSG msg);
    [DllImport("user32")] public static extern uint MsgWaitForMultipleObjects(uint count, nint handles, bool waitAll, uint ms, uint mask);
    [DllImport("user32")] public static extern nint GetForegroundWindow();
    [DllImport("user32")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32")] public static extern bool EnumWindows(EnumWindowsProc proc, nint l);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);
    [DllImport("kernel32", SetLastError = true)] public static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32", SetLastError = true)] public static extern bool CloseHandle(nint handle);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageNameW(nint process, uint flags, char[] buffer, ref uint size);
    [DllImport("kernel32", SetLastError = true)] public static extern bool GetNamedPipeServerProcessId(nint pipe, out uint pid);
    [DllImport("advapi32", SetLastError = true)] public static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32", SetLastError = true)] public static extern bool GetTokenInformation(nint token, int cls, out uint value, uint size, out uint returned);
}
