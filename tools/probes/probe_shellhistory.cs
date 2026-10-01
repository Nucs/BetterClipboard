#:property PublishAot=false
#:property AllowUnsafeBlocks=true
using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using static Native;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

// ─────────────────────────────────────────────────────────────────────────────────────────────────────
// Probe: what do PowerShell and cmd.exe remember of the commands typed into them, for "Pwsh" / "Cmd" tabs?
//
// Modes:
//   --inventory (default)
//       Read-only and content-free. PowerShell hosts and their PSReadLine defaults (profiles NOT loaded), what
//       PSReadLine's built-in filter keeps out of the file (asked with BC-TEST lines, never the user's), the
//       history files' STRUCTURE (sizes, record counts, line endings, multi-line records, duplicates, how many
//       records match credential patterns — counts only), whether a PowerShell session holds a file's mutex
//       right now, profiles that change history options (booleans), cmd/console settings, the default
//       terminal, running shells (counts) and other shells' history files (counts).
//       It never prints a command, a path inside a history or any other content the user typed.
//   --isolated
//       Hidden pseudoconsoles (CreatePseudoConsole: no window, no terminal hand-off) running pwsh 7, Windows
//       PowerShell 5.1 and cmd.exe on BC-TEST input only:
//       * PSReadLine: each session gets a scratch HistorySavePath (set before the first prompt, so the user's
//         history file is never read or written); timing of the write, multi-line encoding, duplicates, the
//         sensitive-line filter, readers that block or share, the history mutex, MaximumHistoryCount, a
//         deleted file, exit.
//       * cmd.exe (/d: no AutoRun): the console's command history read by attaching to the console
//         (GetConsoleCommandHistoryW), its cap, duplicates, console WinEvents, and what happens to a process
//         attached to a console when that console closes (a helper copy of this probe).
//       Scratch folder %TEMP%\bc-shellhistory-probe-<pid>, deleted afterwards.
//   --all  both.
//   (--hold-attached <pid> <seconds> <ignore|handle> <log>  internal: the helper of the console-close test.)
//   (--read-history <pid>  internal: attach, count cmd.exe's history entries, detach — the helper-process read.)
//
// Run: dotnet run tools/probes/probe_shellhistory.cs -- --all
// Results on Windows 11 26200 (2026-10-01), pwsh 7.5.8 (PSReadLine 2.3.6), Windows PowerShell 5.1 (PSReadLine
// 2.0.0): the commit that added this probe (CLAUDE.md gets the research section once the Run tab lands).
// ─────────────────────────────────────────────────────────────────────────────────────────────────────

if (args.Length > 0 && args[0] == "--hold-attached")
{
    return Helper.HoldAttached(args);
}

if (args.Length > 0 && args[0] == "--read-history")
{
    return Helper.ReadHistory(args);
}

var mode = args.Length > 0 ? args[0] : "--inventory";
if (mode is not ("--inventory" or "--isolated" or "--all"))
{
    Console.WriteLine("usage: probe_shellhistory.cs -- [--inventory | --isolated | --all]");
    return 2;
}

Console.WriteLine($"Windows {Environment.OSVersion.Version} · {RuntimeInformation.FrameworkDescription} · {DateTime.UtcNow:yyyy-MM-dd HH:mm}Z");
if (mode is "--inventory" or "--all")
{
    Inventory.Run();
}

if (mode is "--isolated" or "--all")
{
    return Isolated.Run();
}

return 0;

// ═════════════════════════════════════════════════════════════════════════════════════════════════════
// Read-only inventory
// ═════════════════════════════════════════════════════════════════════════════════════════════════════

static class Inventory
{
    public static void Run()
    {
        var work = Scratch.Create();
        try
        {
            Report.Section("PowerShell hosts (no profiles loaded)");
            var results = new List<HostDefaults>();
            foreach (var (label, exe) in Shells.PowerShellHosts())
            {
                var defaults = HostDefaults.Query(label, exe, work);
                if (defaults is null)
                {
                    continue;
                }

                results.Add(defaults);
                Report.Line(1, $"{label}: PowerShell {defaults.PowerShell} · PSReadLine {defaults.PsReadLine} · host {defaults.Host}");
                Report.Line(2, $"history file   {Paths.Redact(defaults.Path)}");
                Report.Line(2, $"save style     {defaults.Style} · in-memory cap {(defaults.Max == 0 ? "0 (set from $MaximumHistoryCount at the first prompt)" : defaults.Max)} · no-duplicates {defaults.NoDup}");
            }

            if (results.Count > 0)
            {
                Report.Line(1, "PSReadLine's default AddToHistoryHandler on BC-TEST lines (MemoryAndFile = written to the file;");
                Report.Line(1, "MemoryOnly = kept for the session, never written):");
                Report.Line(2, $"{"case",-22}{string.Join("", results.Select(r => $"{r.Label,-16}"))}line");
                foreach (var (id, line) in HostDefaults.Cases)
                {
                    var cells = results.Select(r => $"{(r.Options.TryGetValue(id, out var o) ? o : "?"),-16}");
                    Report.Line(2, $"{id,-22}{string.Join("", cells)}{line}");
                }
            }

            Report.Section("PSReadLine history files");
            HistoryFiles();

            Report.Section("PowerShell profiles that change history options (presence only)");
            Profiles();

            Report.Section("cmd.exe and the console host");
            Cmd();

            Report.Section("Other shells and terminals");
            Others();
        }
        finally
        {
            Scratch.Delete(work);
        }
    }

    private static void HistoryFiles()
    {
        string dir = Paths.PsReadLineFolder;
        if (!Directory.Exists(dir))
        {
            Report.Line(1, $"{Paths.Redact(dir)}: absent");
            return;
        }

        var all = Directory.GetFiles(dir);
        var histories = all.Where(f => f.EndsWith("_history.txt", StringComparison.OrdinalIgnoreCase)).ToList();
        Report.Line(1, $"{Paths.Redact(dir)}: {all.Length} file(s), {histories.Count} history file(s)");
        foreach (var file in histories)
        {
            var s = HistoryStats.Analyze(file);
            string host = Path.GetFileName(file)[..^"_history.txt".Length];

            // What a full rescan costs: read (sharing everything) + split + PSReadLine's continuation rule.
            var timings = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                var timer = Stopwatch.StartNew();
                var bytes = Files.ReadShared(file);
                _ = PsReadLine.ParseRecords(PsReadLine.SplitLines(Encoding.UTF8.GetString(bytes)), out _, out _);
                timings.Add(timer.Elapsed.TotalMilliseconds);
            }

            timings.Sort();
            Report.Line(1, $"[{host}]  {s.Bytes / 1024.0:0.0} KB · created {s.Created:yyyy-MM-dd} · last write {s.LastWrite:yyyy-MM-dd HH:mm}Z ({Ago(s.LastWrite)}) · full read+parse {timings[2]:0.0} ms (median of 5)");
            Report.Line(2, $"encoding       {(s.Bom ? "UTF-8 with BOM" : "UTF-8 without BOM")}, {(s.ValidUtf8 ? "valid" : "INVALID UTF-8 somewhere")} · ends with a newline: {s.EndsWithNewline}");
            Report.Line(2, $"line endings   CRLF {s.CrLf} · bare LF {s.BareLf} (after a backtick: {s.BacktickLf}) · backtick+CRLF {s.BacktickCrLf} · bare CR {s.BareCr}");
            Report.Line(2, $"records        {s.Records} from {s.Lines} lines · multi-line {s.MultiLine} (longest {s.MaxLinesInRecord} lines) · blank {s.Blank}");
            Report.Line(2, $"repeats        distinct {s.Distinct} ({100.0 * s.Distinct / Math.Max(1, s.Records):0}%) · same as the record before {s.ConsecutiveDuplicates}");
            Report.Line(2, $"length         median {s.MedianLength} · p95 {s.P95Length} · max {s.MaxLength} chars · over 32,768 chars: {s.Over32K}");
            Report.Line(2, $"rate           {s.Records / Math.Max(1.0, (s.LastWrite - s.Created).TotalDays):0.0} records/day over {(s.LastWrite - s.Created).TotalDays:0} days");
            Report.Line(2, $"reach          a new session loads the newest {Math.Min(s.Records, 4096)} of {s.Records} (MaximumHistoryCount 4096); the file is never trimmed");
            Report.Line(2, $"credential-ish records in the file (counts only): 2.3.6 pattern {s.Pattern236} · 2.0.0 pattern {s.Pattern200} · broader pattern {s.Broad}");
            string mutex = PsReadLine.MutexName(file);
            bool held = Mutex.TryOpenExisting(mutex, out var m);
            m?.Dispose();
            Report.Line(2, $"mutex          {mutex}: {(held ? "exists (an interactive PSReadLine session uses this file now)" : "absent (no PSReadLine session open on it)")}");
        }
    }

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        return span.TotalMinutes < 120 ? $"{span.TotalMinutes:0} min ago" : span.TotalHours < 48 ? $"{span.TotalHours:0} h ago" : $"{span.TotalDays:0} days ago";
    }

    private static void Profiles()
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var candidates = new List<string>();
        foreach (var root in new[] { Path.Combine(docs, "PowerShell"), Path.Combine(docs, "WindowsPowerShell"), Path.Combine(pf, "PowerShell", "7"), Path.Combine(sys, "WindowsPowerShell", "v1.0") })
        {
            foreach (var name in new[] { "profile.ps1", "Microsoft.PowerShell_profile.ps1", "Microsoft.VSCode_profile.ps1", "Microsoft.PowerShellISE_profile.ps1" })
            {
                candidates.Add(Path.Combine(root, name));
            }
        }

        string[] options = ["HistorySavePath", "HistorySaveStyle", "MaximumHistoryCount", "AddToHistoryHandler", "HistoryNoDuplicates"];
        int found = 0;
        foreach (var file in candidates.Where(File.Exists))
        {
            found++;
            string text = Files.ReadSharedText(file);
            var mentioned = options.Where(o => text.Contains(o, StringComparison.OrdinalIgnoreCase)).ToList();
            Report.Line(1, $"{Paths.Redact(file)}: {(mentioned.Count == 0 ? "no history option" : "mentions " + string.Join(", ", mentioned))}");
        }

        if (found == 0)
        {
            Report.Line(1, "no profile file exists (all hosts use the defaults above)");
        }
    }

    private static void Cmd()
    {
        using (var console = Registry.CurrentUser.OpenSubKey("Console"))
        {
            string Value(string name) => console?.GetValue(name) is int i ? i.ToString(CultureInfo.InvariantCulture) : "absent";
            Report.Line(1, $"HKCU\\Console: HistoryBufferSize {Value("HistoryBufferSize")} · NumberOfHistoryBuffers {Value("NumberOfHistoryBuffers")} · HistoryNoDup {Value("HistoryNoDup")}");
            var subkeys = console?.GetSubKeyNames() ?? [];
            int overriding = 0;
            bool cmdOverride = false;
            foreach (var name in subkeys)
            {
                using var sub = console!.OpenSubKey(name);
                bool overrides = sub?.GetValueNames().Any(v => v.StartsWith("History", StringComparison.OrdinalIgnoreCase)) == true;
                overriding += overrides ? 1 : 0;
                cmdOverride |= overrides && name.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase);
            }

            Report.Line(1, $"per-app console subkeys: {subkeys.Length}, of which {overriding} override History* (cmd.exe-specific: {cmdOverride})");
        }

        using (var startup = Registry.CurrentUser.OpenSubKey(@"Console\%%Startup"))
        {
            string console = startup?.GetValue("DelegationConsole") as string ?? "absent";
            string terminal = startup?.GetValue("DelegationTerminal") as string ?? "absent";
            Report.Line(1, $"default terminal: DelegationConsole {console} = {Terminals.Describe(console)} · DelegationTerminal {terminal} = {Terminals.Describe(terminal)}");
        }

        foreach (var (hive, name) in new[] { (Registry.CurrentUser, "HKCU"), (Registry.LocalMachine, "HKLM") })
        {
            using var key = hive.OpenSubKey(@"Software\Microsoft\Command Processor");
            Report.Line(1, $"{name}\\Software\\Microsoft\\Command Processor AutoRun: {(key?.GetValue("AutoRun") is null ? "absent" : "SET (value not shown)")}");
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var clinkDirs = new[] { Path.Combine(local, "clink"), @"C:\Program Files (x86)\clink", @"C:\Program Files\clink" }.Where(Directory.Exists).ToList();
        Report.Line(1, $"Clink: {(clinkDirs.Count == 0 ? "not installed (no profile or program folder)" : string.Join(", ", clinkDirs.Select(Paths.Redact)))}");
        foreach (var dir in clinkDirs)
        {
            foreach (var file in Directory.GetFiles(dir, "clink_history*"))
            {
                Report.Line(2, $"{Path.GetFileName(file)}: {new FileInfo(file).Length / 1024.0:0.0} KB, {Files.ReadSharedText(file).Split('\n').Length} lines");
            }
        }

        var snapshot = Procs.Snapshot();
        uint mySession = Procs.SessionOf((uint)Environment.ProcessId);
        string[] names = ["cmd.exe", "powershell.exe", "pwsh.exe", "bash.exe", "wsl.exe", "WindowsTerminal.exe", "OpenConsole.exe", "conhost.exe"];
        var inSession = snapshot.Where(p => Procs.SessionOf(p.Key) == mySession).ToList();
        Report.Line(1, "processes in this session (this probe's own tooling included): " +
            string.Join(" · ", names.Select(n => $"{n} {inSession.Count(p => p.Value.Name.Equals(n, StringComparison.OrdinalIgnoreCase))}")));
        var cmdParents = inSession
            .Where(p => p.Value.Name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => snapshot.TryGetValue(p.Value.Parent, out var parent) ? parent.Name : "(parent exited)", StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} {g.Count()}");
        Report.Line(1, $"cmd.exe by parent: {string.Join(" · ", cmdParents)}");
    }

    private static void Others()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string bashHistory = Path.Combine(home, ".bash_history");
        if (File.Exists(bashHistory))
        {
            var info = new FileInfo(bashHistory);
            int lines = Files.ReadSharedText(bashHistory).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            Report.Line(1, $"Git Bash ~/.bash_history: {info.Length / 1024.0:0.0} KB, {lines} lines, last write {info.LastWriteTimeUtc:yyyy-MM-dd HH:mm}Z");
        }
        else
        {
            Report.Line(1, "Git Bash ~/.bash_history: absent");
        }

        string git = @"C:\Program Files\Git";
        string[] knobs = ["HISTFILE", "HISTSIZE", "HISTFILESIZE", "HISTCONTROL", "PROMPT_COMMAND", "histappend", "history -a"];
        foreach (var file in new[] { Path.Combine(home, ".bashrc"), Path.Combine(home, ".bash_profile"), Path.Combine(home, ".profile"), Path.Combine(git, "etc", "bash.bashrc"), Path.Combine(git, "etc", "profile") }.Where(File.Exists))
        {
            string text = Files.ReadSharedText(file);
            var mentioned = knobs.Where(k => text.Contains(k, StringComparison.Ordinal)).ToList();
            Report.Line(2, $"{Paths.Redact(file)}: {(mentioned.Count == 0 ? "no history setting" : "mentions " + string.Join(", ", mentioned))}");
        }

        Report.Line(1, "WSL: not probed (opening \\\\wsl$ starts the VM); each distro keeps its own ~/.bash_history");

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var package in new[] { "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe" })
        {
            string state = Path.Combine(local, "Packages", package, "LocalState");
            if (!Directory.Exists(state))
            {
                continue;
            }

            string first = "(default: a new tab)";
            string settings = Path.Combine(state, "settings.json");
            if (File.Exists(settings))
            {
                try
                {
                    using var doc = JsonDocument.Parse(Files.ReadSharedText(settings), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    if (doc.RootElement.TryGetProperty("firstWindowPreference", out var pref))
                    {
                        first = pref.ToString();
                    }
                }
                catch (JsonException)
                {
                    first = "(settings.json not parseable)";
                }
            }

            int buffers = Directory.GetFiles(state, "buffer_*.txt").Length;
            Report.Line(1, $"{package.Split('_')[0]}: firstWindowPreference {first} · persisted buffer files {buffers}");
        }
    }
}

/// <summary>One PowerShell host's PSReadLine defaults, asked in a no-profile child process.</summary>
sealed class HostDefaults
{
    /// <summary>BC-TEST lines handed to PSReadLine's default AddToHistoryHandler (ours, never the user's).</summary>
    public static readonly (string Id, string Line)[] Cases =
    [
        ("plain", "Write-Output 'BC-TEST'"),
        ("password literal", "Connect-BcTest -Password 'BC-TEST'"),
        ("password variable", "Connect-BcTest -Password $bcTestPassword"),
        ("token assignment", "$env:GH_TOKEN = 'BC-TEST'"),
        ("-AsPlainText", "ConvertTo-SecureString 'BC-TEST' -AsPlainText -Force"),
        ("Get-Secret", "Get-Secret -Name BC-TEST"),
        ("'key' in prose", "git commit -m 'BC-TEST monkey'"),
        ("registry HKEY", "reg query HKEY_CURRENT_USER\\BC-TEST"),
        ("curl -u user:pass", "curl.exe -u bc:BC-TEST https://example.invalid"),
        ("mysql -pPASS", "mysql -u bc -pBC-TEST"),
        ("PAT assignment", "$env:GH_PAT = 'BC-TEST'"),
        ("Bearer header", "iwr https://example.invalid -Headers @{Authorization = 'Bearer BC-TEST'}"),
        ("sqlcmd -P", "sqlcmd -S . -U sa -P BC-TEST"),
    ];

    private const string Script = """
        param([string]$CasesPath)
        $ErrorActionPreference = 'Stop'
        Import-Module PSReadLine
        $o = Get-PSReadLineOption
        $m = Get-Module PSReadLine
        $cases = Get-Content -LiteralPath $CasesPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $results = @(foreach ($c in $cases) {
            $opt = [Microsoft.PowerShell.PSConsoleReadLine]::GetDefaultAddToHistoryOption([string]$c.line)
            [pscustomobject]@{ id = [string]$c.id; option = [string]$opt }
        })
        [pscustomobject]@{
            ps = $PSVersionTable.PSVersion.ToString()
            psrl = $m.Version.ToString()
            host = $Host.Name
            path = $o.HistorySavePath
            style = [string]$o.HistorySaveStyle
            max = $o.MaximumHistoryCount
            nodup = [bool]$o.HistoryNoDuplicates
            results = $results
        } | ConvertTo-Json -Depth 4 -Compress
        """;

    public required string Label { get; init; }
    public required string PowerShell { get; init; }
    public required string PsReadLine { get; init; }
    public required string Host { get; init; }
    public required string Path { get; init; }
    public required string Style { get; init; }
    public required int Max { get; init; }
    public required bool NoDup { get; init; }
    public required Dictionary<string, string> Options { get; init; }

    /// <summary>Runs the host without profiles and reads its defaults; null (with a report line) when it fails.</summary>
    public static HostDefaults? Query(string label, string exe, string work)
    {
        string script = System.IO.Path.Combine(work, $"defaults-{label}.ps1");
        string cases = System.IO.Path.Combine(work, "cases.json");
        File.WriteAllText(script, Script, new UTF8Encoding(true));
        File.WriteAllText(cases, JsonSerializer.Serialize(Cases.Select(c => new Dictionary<string, string> { ["id"] = c.Id, ["line"] = c.Line })), new UTF8Encoding(false));
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-NoProfile", "-NoLogo", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-CasesPath", cases })
        {
            psi.ArgumentList.Add(a);
        }

        Shells.CleanEnvironment(psi.Environment!);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill();
            Report.Line(1, $"{label}: timed out");
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout.Result.Trim());
            var root = doc.RootElement;
            return new HostDefaults
            {
                Label = label,
                PowerShell = root.GetProperty("ps").GetString()!,
                PsReadLine = root.GetProperty("psrl").GetString()!,
                Host = root.GetProperty("host").GetString()!,
                Path = root.GetProperty("path").GetString()!,
                Style = root.GetProperty("style").GetString()!,
                Max = root.GetProperty("max").GetInt32(),
                NoDup = root.GetProperty("nodup").GetBoolean(),
                Options = root.GetProperty("results").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetProperty("option").GetString()!),
            };
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Report.Line(1, $"{label}: unexpected output ({e.GetType().Name}); stderr {stderr.Result.Length} chars");
            return null;
        }
    }
}

/// <summary>Content-free statistics of one PSReadLine history file.</summary>
sealed class HistoryStats
{
    // PSReadLine's own patterns (source, PowerShell/PSReadLine History.cs): v2.3.6 d2e770f, v2.0.0 6b5e9ff. Matched
    // here WITHOUT 2.3.6's syntax-tree exemptions, so a count is an upper bound of what 2.3.6 would keep out.
    private static readonly Regex Pattern236Regex = new("password|asplaintext|token|apikey|secret", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Pattern200Regex = new("password|asplaintext|token|key|secret", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // A broader, deliberately loose net for "this record may hold a credential" (counted, never shown).
    private static readonly Regex BroadRegex = new(
        @"passw(or)?d|\bpwd\b|secret|token|api[-_]?key|bearer\s|authorization|credential|ghp_|github_pat_|\bsk-[A-Za-z0-9_-]{16,}|xox[abprs]-|AKIA[0-9A-Z]{16}|BEGIN [A-Z ]*PRIVATE KEY|\s-p\S|\s-P\s",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public long Bytes { get; private init; }
    public DateTime Created { get; private init; }
    public DateTime LastWrite { get; private init; }
    public bool Bom { get; private init; }
    public bool ValidUtf8 { get; private init; }
    public bool EndsWithNewline { get; private init; }
    public int CrLf { get; private init; }
    public int BareLf { get; private init; }
    public int BacktickLf { get; private init; }
    public int BacktickCrLf { get; private init; }
    public int BareCr { get; private init; }
    public int Lines { get; private init; }
    public int Records { get; private init; }
    public int MultiLine { get; private init; }
    public int MaxLinesInRecord { get; private init; }
    public int Blank { get; private init; }
    public int Distinct { get; private init; }
    public int ConsecutiveDuplicates { get; private init; }
    public int MedianLength { get; private init; }
    public int P95Length { get; private init; }
    public int MaxLength { get; private init; }
    public int Over32K { get; private init; }
    public int Pattern236 { get; private init; }
    public int Pattern200 { get; private init; }
    public int Broad { get; private init; }

    public static HistoryStats Analyze(string path)
    {
        var info = new FileInfo(path);
        byte[] bytes = Files.ReadShared(path);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        string text;
        bool valid = true;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        }
        catch (DecoderFallbackException)
        {
            valid = false;
            text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        }

        int crlf = 0, bareLf = 0, backtickLf = 0, backtickCrLf = 0, bareCr = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0x0A)
            {
                if (i > 0 && bytes[i - 1] == 0x0D)
                {
                    crlf++;
                    backtickCrLf += i > 1 && bytes[i - 2] == 0x60 ? 1 : 0;
                }
                else
                {
                    bareLf++;
                    backtickLf += i > 0 && bytes[i - 1] == 0x60 ? 1 : 0;
                }
            }
            else if (bytes[i] == 0x0D && (i + 1 >= bytes.Length || bytes[i + 1] != 0x0A))
            {
                bareCr++;
            }
        }

        var lines = PsReadLine.SplitLines(text);
        var records = PsReadLine.ParseRecords(lines, out int multiLine, out int maxLines);
        var lengths = records.Select(r => r.Length).OrderBy(l => l).ToList();
        int consecutive = 0;
        for (int i = 1; i < records.Count; i++)
        {
            consecutive += string.Equals(records[i], records[i - 1], StringComparison.Ordinal) ? 1 : 0;
        }

        return new HistoryStats
        {
            Bytes = bytes.Length,
            Created = info.CreationTimeUtc,
            LastWrite = info.LastWriteTimeUtc,
            Bom = bom,
            ValidUtf8 = valid,
            EndsWithNewline = bytes.Length == 0 || bytes[^1] == 0x0A,
            CrLf = crlf,
            BareLf = bareLf,
            BacktickLf = backtickLf,
            BacktickCrLf = backtickCrLf,
            BareCr = bareCr,
            Lines = lines.Count,
            Records = records.Count,
            MultiLine = multiLine,
            MaxLinesInRecord = maxLines,
            Blank = records.Count(string.IsNullOrWhiteSpace),
            Distinct = records.Distinct(StringComparer.Ordinal).Count(),
            ConsecutiveDuplicates = consecutive,
            MedianLength = lengths.Count == 0 ? 0 : lengths[lengths.Count / 2],
            P95Length = lengths.Count == 0 ? 0 : lengths[Math.Min(lengths.Count - 1, (int)(lengths.Count * 0.95))],
            MaxLength = lengths.Count == 0 ? 0 : lengths[^1],
            Over32K = lengths.Count(l => l > 32_768),
            Pattern236 = records.Count(r => Pattern236Regex.IsMatch(r)),
            Pattern200 = records.Count(r => Pattern200Regex.IsMatch(r)),
            Broad = records.Count(r => BroadRegex.IsMatch(r)),
        };
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════
// Isolated experiments (hidden pseudoconsoles, BC-TEST input, scratch files)
// ═════════════════════════════════════════════════════════════════════════════════════════════════════

static class Isolated
{
    public static int Run()
    {
        string work = Scratch.Create();
        var userHistory = Path.Combine(Paths.PsReadLineFolder, "ConsoleHost_history.txt");
        var before = Files.Fingerprint(userHistory);
        try
        {
            foreach (var (label, exe) in Shells.PowerShellHosts())
            {
                Report.Section($"PSReadLine in a hidden pseudoconsole: {label}");
                PsReadLineRun(label, exe, work);
            }

            Report.Section("cmd.exe in a hidden pseudoconsole (/d: no AutoRun)");
            CmdRun(work);

            Report.Section("A process attached to a console when the console closes");
            CloseRun(work);
        }
        finally
        {
            Scratch.Delete(work);
            var after = Files.Fingerprint(userHistory);
            Report.Section("Side effects");
            Report.Line(1, $"your ConsoleHost_history.txt: {(before == after ? "unchanged (size and last write)" : "CHANGED during the run (your own PowerShell sessions may have written it)")}");
            Report.Line(1, $"scratch folder removed: {!Directory.Exists(work)}");
        }

        return 0;
    }

    private static void PsReadLineRun(string label, string exe, string work)
    {
        string dir = Path.Combine(work, label);
        Directory.CreateDirectory(dir);
        string hist = Path.Combine(dir, $"BC-TEST_{label}_history.txt");
        var events = new ConcurrentQueue<(WatcherChangeTypes Kind, long At)>();
        using var watcher = new FileSystemWatcher(dir, Path.GetFileName(hist))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Changed += (_, e) => events.Enqueue((e.ChangeType, Stopwatch.GetTimestamp()));
        watcher.Created += (_, e) => events.Enqueue((e.ChangeType, Stopwatch.GetTimestamp()));
        watcher.Deleted += (_, e) => events.Enqueue((e.ChangeType, Stopwatch.GetTimestamp()));
        watcher.EnableRaisingEvents = true;

        // The option is set by -Command before the first prompt; PSReadLine reads its history file at the first
        // prompt (DelayedOneTimeInitialize), so the user's ConsoleHost_history.txt is never opened.
        string command = $"\"{exe}\" -NoProfile -NoLogo -NoExit -Command \"Set-PSReadLineOption -HistorySavePath '{hist}'\"";
        var history = new HistoryProbe(hist);
        var start = Stopwatch.StartNew();
        using var session = PseudoConsoleSession.Start(command, dir);
        session.Type("'BC-TEST-ready'\r");
        if (history.WaitFor(r => r.Contains("'BC-TEST-ready'"), 60_000) < 0)
        {
            Report.Line(1, $"no record after 60 s; console output (VT stripped, last 600 chars): {session.PlainTail(600)}");
            return;
        }

        Report.Line(1, $"first record written {start.Elapsed.TotalSeconds:0.0} s after the process started");

        // Write latency: Enter → file-change event and → record readable.
        var visible = new List<double>();
        var notified = new List<double>();
        var kinds = new List<int>();
        for (int i = 1; i <= 5; i++)
        {
            events.Clear();
            long t0 = Stopwatch.GetTimestamp();
            session.Type($"'BC-TEST-lat-{i}'\r");
            double ms = history.WaitFor(r => r.Contains($"'BC-TEST-lat-{i}'"), 5_000);
            visible.Add(ms);
            Thread.Sleep(150);
            var seen = events.ToArray();
            kinds.Add(seen.Length);
            notified.Add(seen.Length == 0 ? -1 : Stopwatch.GetElapsedTime(t0, seen[0].At).TotalMilliseconds);
        }

        Report.Line(1, $"write latency  record readable {Median(visible):0.0} ms after the keystrokes were sent (median of 5; max {visible.Max():0.0}); first change event {Median(notified):0.0} ms; events per command {string.Join("/", kinds)}");

        // Written at Enter, before the command runs?
        session.Type("'BC-TEST-2'; Start-Sleep -Seconds 3\r");
        double accept = history.WaitFor(r => r.Contains("'BC-TEST-2'; Start-Sleep -Seconds 3"), 2_500);
        Report.Line(1, $"accept vs run  {(accept >= 0 ? $"record present {accept:0} ms after Enter, while its 3 s Start-Sleep was still running: written when the line is accepted" : "record NOT present during the command: written after it ran")}");
        Thread.Sleep(3_300);

        // Multi-line input: incomplete lines make PSReadLine insert a newline instead of accepting.
        session.Type("if ($true) {\r'BC-TEST-3'\r}\r");
        string multi = "if ($true) {\n'BC-TEST-3'\n}";
        bool multiOk = history.WaitFor(r => r.Contains(multi), 5_000) >= 0;
        byte[] raw = Files.ReadShared(hist);
        string rawText = Encoding.UTF8.GetString(raw);
        bool backtickLf = rawText.Contains("if ($true) {`\n'BC-TEST-3'`\n}\r\n", StringComparison.Ordinal);
        Report.Line(1, $"multi-line     one record of 3 lines: {multiOk}; stored as line`<LF>line`<LF>line<CRLF> (backtick + bare LF inside, CRLF at the end): {backtickLf}");

        // Duplicates: the same line twice in a row, then a repeat of an older line.
        session.Type("'BC-TEST-6'\r'BC-TEST-6'\r'BC-TEST-lat-1'\r'BC-TEST-6b'\r");
        history.WaitFor(r => r.Contains("'BC-TEST-6b'"), 5_000);
        var records = history.Records();
        Report.Line(1, $"duplicates     same line twice in a row → {records.Count(r => r == "'BC-TEST-6'")} record(s); an older line repeated later → {records.Count(r => r == "'BC-TEST-lat-1'")} records (the file keeps every non-consecutive repeat)");

        // A command that fails is still recorded (unlike Win+R).
        session.Type("throw 'BC-TEST-8'\r'BC-TEST-8b'\r");
        history.WaitFor(r => r.Contains("'BC-TEST-8b'"), 5_000);
        Report.Line(1, $"failing cmd    'throw' recorded: {history.Records().Contains("throw 'BC-TEST-8'")}");

        // The sensitive-line filter, live (the inventory asked the handler directly).
        string Sensitive(string line, string after)
        {
            session.Type(line + "\r" + after + "\r");
            bool flushed = history.WaitFor(r => r.Contains(after), 5_000) >= 0;
            return !flushed ? "?" : history.Records().Contains(line) ? "written" : "kept out";
        }

        Report.Line(1, $"filter         'BC-TEST-password' {Sensitive("'BC-TEST-password-4'", "'BC-TEST-4b'")} · 'BC-TEST-monkey' {Sensitive("'BC-TEST-monkey-5'", "'BC-TEST-5b'")} · Get-Secret {Sensitive("Get-Secret -Name BC-TEST-9", "'BC-TEST-9b'")} · curl -u {Sensitive("curl.exe -u bc:BC-TEST-pw https://example.invalid --max-time 1", "'BC-TEST-9c'")}");

        // A reader that does not share write access.
        string errorsBefore = session.PlainTail(65_536);
        int errorCountBefore = Regex.Matches(errorsBefore, "history file", RegexOptions.IgnoreCase).Count;
        using (new FileStream(hist, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            session.Type("'BC-TEST-10'\r");
            Thread.Sleep(1_500);
        }

        bool tenWhileBlocked = history.Records().Contains("'BC-TEST-10'");
        session.Type("'BC-TEST-11'\r");
        history.WaitFor(r => r.Contains("'BC-TEST-11'"), 5_000);
        records = history.Records();
        int i10 = records.IndexOf("'BC-TEST-10'");
        int i11 = records.IndexOf("'BC-TEST-11'");
        Thread.Sleep(300);
        bool errorShown = Regex.Matches(session.PlainTail(65_536), "history file", RegexOptions.IgnoreCase).Count > errorCountBefore;
        Report.Line(1, $"FileShare.Read reader held 1.5 s: record written meanwhile {tenWhileBlocked}; PSReadLine printed a history-file error {errorShown}; the record arrived with the next command {i10 >= 0 && i10 < i11}");

        using (new FileStream(hist, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            session.Type("'BC-TEST-12'\r");
            Report.Line(1, $"FileShare.ReadWrite|Delete reader held: record written meanwhile {history.WaitFor(r => r.Contains("'BC-TEST-12'"), 3_000) >= 0}");
        }

        // The history mutex: name check, and what holding it does.
        string name = PsReadLine.MutexName(hist);
        if (Mutex.TryOpenExisting(name, out var mutex))
        {
            using (mutex)
            {
                mutex.WaitOne();
                session.Type("'BC-TEST-13'\r");
                Thread.Sleep(1_500);
                bool thirteenWhileHeld = history.Records().Contains("'BC-TEST-13'");
                mutex.ReleaseMutex();
                session.Type("'BC-TEST-14'\r");
                history.WaitFor(r => r.Contains("'BC-TEST-14'"), 5_000);
                records = history.Records();
                Report.Line(1, $"mutex          {name} found (name scheme confirmed); held 1.5 s: record written meanwhile {thirteenWhileHeld}; arrived with the next command {records.IndexOf("'BC-TEST-13'") is >= 0 and var a && a < records.IndexOf("'BC-TEST-14'")}");
            }
        }
        else
        {
            Report.Line(1, $"mutex          {name} NOT found while the session ran (name scheme differs)");
        }

        // The in-memory cap does not trim the file.
        session.Type("Set-PSReadLineOption -MaximumHistoryCount 3\r");
        for (int i = 1; i <= 5; i++)
        {
            session.Type($"'BC-TEST-max-{i}'\r");
        }

        history.WaitFor(r => r.Contains("'BC-TEST-max-5'"), 5_000);
        records = history.Records();
        Report.Line(1, $"cap            MaximumHistoryCount 3, then 5 commands: {records.Count(r => r.StartsWith("'BC-TEST-max-", StringComparison.Ordinal))} of 5 in the file ({records.Count} records in all): the cap is in memory only");

        // The file deleted under a running session.
        File.Delete(hist);
        events.Clear();
        session.Type("'BC-TEST-15'\r");
        history.WaitFor(r => r.Contains("'BC-TEST-15'"), 5_000);
        Thread.Sleep(200);
        records = history.Records();
        Report.Line(1, $"deleted file   File.Delete succeeded (PSReadLine keeps no handle open); the next command re-created it with {records.Count} record(s); events {string.Join(",", events.Select(e => e.Kind))}");

        // Exit writes nothing more in SaveIncrementally ("exit" itself is an accepted line, so it is recorded first).
        var beforeExit = history.Records();
        session.Type("exit\r");
        bool exited = session.WaitForExit(10_000);
        var afterExit = history.Records();
        bool onlyExit = afterExit.Count == beforeExit.Count + 1 && afterExit[^1] == "exit" && afterExit.Take(beforeExit.Count).SequenceEqual(beforeExit);
        Report.Line(1, $"exit           process exited {exited}; the file gained only the 'exit' line itself (no rewrite at exit): {onlyExit}");
    }

    private static void CmdRun(string work)
    {
        string dir = Path.Combine(work, "cmd");
        Directory.CreateDirectory(dir);
        string comspec = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        using var hook = new ConsoleEventHook();
        using var session = PseudoConsoleSession.Start($"\"{comspec}\" /d /k", dir);
        session.Type("echo BC-TEST-ready\r");

        // Attach this probe to the hidden console. Detach before the console closes: a process still attached
        // then is terminated with it (the last section shows it).
        FreeConsole();
        var attachTimer = Stopwatch.StartNew();
        bool attached = false;
        while (!attached && attachTimer.ElapsedMilliseconds < 10_000)
        {
            attached = AttachConsole(session.ProcessId);
            if (!attached)
            {
                Thread.Sleep(20);
            }
        }

        if (!attached)
        {
            Report.Line(1, $"AttachConsole failed: {Marshal.GetLastWin32Error()}");
            return;
        }

        SetConsoleCtrlHandler(0, true);
        try
        {
            nint consoleWindow = GetConsoleWindow();
            bool ready = Wait(() => CmdHistory.Read().Contains("echo BC-TEST-ready"), 10_000);
            Report.Line(1, $"history read by attaching: {ready} (GetConsoleCommandHistoryW with exe name cmd.exe; length in bytes, entries NUL-separated)");
            if (!ready)
            {
                return;
            }

            var info = new CONSOLE_HISTORY_INFO { cbSize = (uint)Marshal.SizeOf<CONSOLE_HISTORY_INFO>() };
            Report.Line(1, GetConsoleHistoryInfo(ref info)
                ? $"this console's settings: HistoryBufferSize {info.HistoryBufferSize} · NumberOfHistoryBuffers {info.NumberOfHistoryBuffers} · HISTORY_NO_DUP_FLAG {(info.dwFlags & 1) != 0}"
                : $"GetConsoleHistoryInfo failed: {Marshal.GetLastWin32Error()}");

            session.Type("echo BC-TEST-c1\recho BC-TEST-c2\recho BC-TEST-c2\recho BC-TEST-c1\rrem BC-TEST-c3\r");
            Wait(() => CmdHistory.Read().Contains("rem BC-TEST-c3"), 5_000);
            var entries = CmdHistory.Read();
            Report.Line(1, $"duplicates     same command twice in a row → {entries.Count(e => e == "echo BC-TEST-c2")} entr{(entries.Count(e => e == "echo BC-TEST-c2") == 1 ? "y" : "ies")}; an older one repeated → {entries.Count(e => e == "echo BC-TEST-c1")} entr{(entries.Count(e => e == "echo BC-TEST-c1") == 1 ? "y (moved to the end)" : "ies")}; order {string.Join(" ", entries.Select(e => e.Replace("echo BC-TEST-", "").Replace("rem BC-TEST-", "")))}");

            // A failing command is in the history too.
            session.Type("bc-test-no-such-command\r");
            Wait(() => CmdHistory.Read().Contains("bc-test-no-such-command"), 5_000);
            Report.Line(1, $"failing cmd    an unknown command is kept: {CmdHistory.Read().Contains("bc-test-no-such-command")}");

            // The cap.
            var burst = new StringBuilder();
            for (int i = 1; i <= 60; i++)
            {
                burst.Append(CultureInfo.InvariantCulture, $"rem BC-TEST-cap-{i:00}\r");
            }

            session.Type(burst.ToString());
            Wait(() => CmdHistory.Read().Contains("rem BC-TEST-cap-60"), 15_000);
            entries = CmdHistory.Read();
            Report.Line(1, $"cap            after 60 more commands: {entries.Count} entries kept; oldest kept: {entries.FirstOrDefault()?.Replace("rem BC-TEST-", "")}; 'ready' still there: {entries.Contains("echo BC-TEST-ready")}");

            // What one read costs from scratch, as a rescan would pay it per console: attach, read, detach.
            var cycle = new List<double>();
            for (int i = 0; i < 10; i++)
            {
                FreeConsole();
                var timer = Stopwatch.StartNew();
                if (AttachConsole(session.ProcessId))
                {
                    _ = CmdHistory.Read();
                }

                FreeConsole();
                cycle.Add(timer.Elapsed.TotalMilliseconds);
            }

            AttachConsole(session.ProcessId);
            SetConsoleCtrlHandler(0, true);
            cycle.Sort();
            Report.Line(1, $"read cost      attach + read {entries.Count} entries + detach, in-process: median {cycle[5]:0.00} ms, max {cycle[^1]:0.00} ms");

            // The same through a short-lived helper process (the safe design: a console that closes while a
            // process is attached terminates that process — see the last section).
            var spawn = new List<double>();
            string answer = "";
            for (int i = 0; i < 3; i++)
            {
                var timer = Stopwatch.StartNew();
                var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                psi.ArgumentList.Add("--read-history");
                psi.ArgumentList.Add(session.ProcessId.ToString(CultureInfo.InvariantCulture));
                using var helper = Process.Start(psi)!;
                answer = helper.StandardOutput.ReadToEnd().Trim();
                helper.WaitForExit();
                spawn.Add(timer.Elapsed.TotalMilliseconds);
            }

            spawn.Sort();
            Report.Line(1, $"               through a helper process (this probe's .NET apphost): median {spawn[1]:0} ms per console; it answered '{answer}'");

            // Console WinEvents: cmd's own start (the hook ran before the session) and a program it starts.
            session.Type("cmd /d /c rem BC-TEST-ext\r");
            Thread.Sleep(1_500);
            var all = hook.Events.ToArray();
            int ownStart = all.Count(e => e.Event == EVENT_CONSOLE_START_APPLICATION && e.Pid == session.ProcessId);
            int onWindow = all.Count(e => consoleWindow != 0 && e.Hwnd == consoleWindow);
            Report.Line(1, $"WinEvents      hook installed {hook.Installed}; this pseudoconsole: START for cmd itself {ownStart}, any event on its window {onWindow}; events from any console so far {all.Length}");

            // The history after cmd itself has exited, while the console lives on (this probe is attached).
            session.Type("exit\r");
            bool cmdExited = session.WaitForExit(5_000);
            var afterExit = CmdHistory.Read();
            Report.Line(1, $"after exit     cmd exited {cmdExited}; the console still answers for cmd.exe: {afterExit.Count} entries");
        }
        finally
        {
            FreeConsole();
        }

        // Controls for the WinEvents, each running cmd, which starts a child cmd:
        // * a classic console host without a window (CREATE_NO_WINDOW, the way tools start cmd);
        // * a classic console host with a HIDDEN window (CREATE_NEW_CONSOLE + SW_HIDE). conhost never hands a
        //   hidden console to Windows Terminal (microsoft/terminal IoDispatchers.cpp _shouldAttemptHandoff), so
        //   nothing appears on screen, and it has a real window, which MSAA events need (AccessibilityNotifier.cpp).
        foreach (var (label, hidden) in new[] { ("windowless classic console (CREATE_NO_WINDOW)", false), ("hidden classic console window (SW_HIDE)", true) })
        {
            int before = hook.Events.Count;
            int classicPid = ClassicConsole.Run($"\"{comspec}\" /d /c \"{comspec}\" /d /c rem BC-TEST-child", dir, hidden);
            Thread.Sleep(500);
            var fresh = hook.Events.Skip(before).ToArray();
            var ownWindow = fresh.Where(e => e.Pid == classicPid).Select(e => e.Hwnd).FirstOrDefault();
            int sameWindow = ownWindow == 0 ? 0 : fresh.Count(e => e.Hwnd == ownWindow);
            Report.Line(1, $"               control, {label}: START/END for cmd itself {fresh.Count(e => e.Pid == classicPid)}, events on its window {sameWindow} (START {fresh.Count(e => ownWindow != 0 && e.Hwnd == ownWindow && e.Event == EVENT_CONSOLE_START_APPLICATION)}); from any console meanwhile {fresh.Length}");
        }
    }

    private static void CloseRun(string work)
    {
        string dir = Path.Combine(work, "close");
        Directory.CreateDirectory(dir);
        string comspec = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        foreach (var variant in new[] { "ignore", "handle" })
        {
            string log = Path.Combine(dir, $"helper-{variant}.log");
            var session = PseudoConsoleSession.Start($"\"{comspec}\" /d /k", dir);
            try
            {
                Thread.Sleep(500);
                var psi = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true, // no console window of its own; it detaches from it at once anyway
                };
                foreach (var a in new[] { "--hold-attached", session.ProcessId.ToString(CultureInfo.InvariantCulture), "20", variant, log })
                {
                    psi.ArgumentList.Add(a);
                }

                using var helper = Process.Start(psi)!;
                var first = helper.StandardOutput.ReadLineAsync();
                if (!first.Wait(10_000) || first.Result != "attached")
                {
                    Report.Line(1, $"{variant}: helper did not attach ({(first.IsCompleted ? first.Result : "no answer")})");
                    helper.Kill();
                    continue;
                }

                var timer = Stopwatch.StartNew();
                session.Dispose(); // ClosePseudoConsole
                bool exited = helper.WaitForExit(15_000);
                string received = File.Exists(log) ? File.ReadAllText(log).Trim() : "";
                Report.Line(1, $"{variant,-6}  helper ({(variant == "ignore" ? "SetConsoleCtrlHandler(NULL, TRUE)" : "a handler returning TRUE")}) exited {exited} {timer.ElapsedMilliseconds} ms after ClosePseudoConsole, exit code 0x{(exited ? helper.ExitCode : 0):X8}; control events seen: {(received.Length == 0 ? "none logged" : received)}");
                if (!exited)
                {
                    helper.Kill();
                }
            }
            finally
            {
                session.Dispose();
            }
        }
    }

    private static bool Wait(Func<bool> condition, int timeoutMs)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Where(v => v >= 0).OrderBy(v => v).ToList();
        return sorted.Count == 0 ? -1 : sorted[sorted.Count / 2];
    }
}

/// <summary>Reads cmd.exe's command history of the console this process is attached to.</summary>
static class CmdHistory
{
    public static List<string> Read()
    {
        uint length = GetConsoleCommandHistoryLengthW("cmd.exe");
        if (length == 0)
        {
            return [];
        }

        var buffer = new byte[length + 4];
        uint copied = GetConsoleCommandHistoryW(buffer, (uint)buffer.Length, "cmd.exe");
        string text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(copied == 0 ? length : copied, (uint)buffer.Length));
        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}

/// <summary>Out-of-context console WinEvents (EVENT_CONSOLE_START/END_APPLICATION) on a message-loop thread.</summary>
sealed class ConsoleEventHook : IDisposable
{
    private static WinEventProc? s_proc;
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new();
    private uint threadId;
    private nint hook;

    public ConcurrentQueue<(uint Event, nint Hwnd, int Pid, long At)> Events { get; } = new();

    public bool Installed => hook != 0;

    public ConsoleEventHook()
    {
        thread = new Thread(() =>
        {
            threadId = GetCurrentThreadId();
            PeekMessageW(out _, 0, 0, 0, 0); // create the queue before Dispose can post WM_QUIT
            s_proc = (_, evt, hwnd, idObject, _, _, _) => Events.Enqueue((evt, hwnd, idObject, Stopwatch.GetTimestamp()));
            hook = SetWinEventHook(EVENT_CONSOLE_START_APPLICATION, EVENT_CONSOLE_END_APPLICATION, 0, s_proc, 0, 0, WINEVENT_OUTOFCONTEXT);
            ready.Set();
            while (GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                DispatchMessageW(ref msg);
            }

            if (hook != 0)
            {
                UnhookWinEvent(hook);
            }
        })
        { IsBackground = true, Name = "WinEvents" };
        thread.Start();
        ready.Wait();
    }

    public void Dispose()
    {
        PostThreadMessageW(threadId, WM_QUIT, 0, 0);
        thread.Join(2_000);
    }
}

/// <summary>A child process on a hidden pseudoconsole: typed input in, output drained (kept for diagnostics only).</summary>
sealed class PseudoConsoleSession : IDisposable
{
    private readonly FileStream input;
    private readonly Thread drain;
    private readonly StringBuilder output = new();
    private readonly object gate = new();
    private nint pseudoConsole;
    private nint process;

    public int ProcessId { get; }

    private PseudoConsoleSession(nint pseudoConsole, nint process, int processId, FileStream input, FileStream outputStream)
    {
        this.pseudoConsole = pseudoConsole;
        this.process = process;
        ProcessId = processId;
        this.input = input;
        drain = new Thread(() =>
        {
            var buffer = new byte[8192];
            var chars = new char[16384];
            var decoder = Encoding.UTF8.GetDecoder();
            try
            {
                int n;
                while ((n = outputStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    int c = decoder.GetChars(buffer, 0, n, chars, 0);
                    lock (gate)
                    {
                        output.Append(chars, 0, c);
                        if (output.Length > 131_072)
                        {
                            output.Remove(0, output.Length - 65_536);
                        }
                    }
                }
            }
            catch (IOException)
            {
                // The pseudoconsole closed.
            }
            finally
            {
                outputStream.Dispose();
            }
        })
        { IsBackground = true, Name = "ConPTY drain" };
        drain.Start();
    }

    public static PseudoConsoleSession Start(string commandLine, string workingDirectory)
    {
        if (!CreatePipe(out var inputRead, out var inputWrite, 0, 0) || !CreatePipe(out var outputRead, out var outputWrite, 0, 0))
        {
            throw new Win32Exception();
        }

        int hr = CreatePseudoConsole(new COORD { X = 200, Y = 50 }, inputRead, outputWrite, 0, out var hpc);
        if (hr != 0)
        {
            throw new COMException("CreatePseudoConsole", hr);
        }

        // The pseudoconsole duplicated its ends; ours must close or the pipes never report EOF.
        inputRead.Dispose();
        outputWrite.Dispose();

        nint size = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        nint list = Marshal.AllocHGlobal(size);
        nint environment = 0;
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size) ||
                !UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hpc, IntPtr.Size, 0, 0))
            {
                throw new Win32Exception();
            }

            var si = new STARTUPINFOEXW();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEXW>();
            // Like Windows Terminal: no inherited std handles, so the child talks to the pseudoconsole only.
            si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            si.lpAttributeList = list;
            environment = Shells.EnvironmentBlock();
            if (!CreateProcessW(null, new StringBuilder(commandLine), 0, 0, false, EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, environment, workingDirectory, ref si, out var pi))
            {
                int error = Marshal.GetLastWin32Error();
                ClosePseudoConsole(hpc);
                throw new Win32Exception(error);
            }

            CloseHandle(pi.hThread);
            return new PseudoConsoleSession(hpc, pi.hProcess, pi.dwProcessId, new FileStream(inputWrite, FileAccess.Write, 1), new FileStream(outputRead, FileAccess.Read, 1));
        }
        finally
        {
            DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
            if (environment != 0)
            {
                Marshal.FreeHGlobal(environment);
            }
        }
    }

    public void Type(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        input.Write(bytes, 0, bytes.Length);
        input.Flush();
    }

    public bool WaitForExit(int timeoutMs) => process == 0 || WaitForSingleObject(process, (uint)timeoutMs) == 0;

    /// <summary>The newest console output with VT sequences removed (BC-TEST input and system text only).</summary>
    public string PlainTail(int chars)
    {
        string text;
        lock (gate)
        {
            text = output.ToString();
        }

        text = Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(\x07|\x1B\\)|\x1B[@-Z\\-_]", "");
        text = Regex.Replace(text, @"\s+", " ");
        return text.Length <= chars ? text : text[^chars..];
    }

    public void Dispose()
    {
        if (pseudoConsole != 0)
        {
            ClosePseudoConsole(pseudoConsole);
            pseudoConsole = 0;
        }

        input.Dispose();
        drain.Join(5_000);
        if (process != 0)
        {
            if (WaitForSingleObject(process, 3_000) != 0)
            {
                TerminateProcess(process, 1); // our own BC-TEST child only
            }

            CloseHandle(process);
            process = 0;
        }
    }
}

/// <summary>Polls a scratch PSReadLine history file the way a well-behaved reader must open it.</summary>
sealed class HistoryProbe(string path)
{
    public List<string> Records()
    {
        byte[] bytes = Files.ReadShared(path);
        return PsReadLine.ParseRecords(PsReadLine.SplitLines(Encoding.UTF8.GetString(bytes)), out _, out _);
    }

    /// <summary>Milliseconds until the condition held, or -1 after the timeout.</summary>
    public double WaitFor(Func<List<string>, bool> condition, int timeoutMs)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                if (condition(Records()))
                {
                    return timer.Elapsed.TotalMilliseconds;
                }
            }
            catch (IOException)
            {
                // Mid-write or mid-delete: try again.
            }

            // Spin for the first 100 ms: Thread.Sleep(n) rounds up to the 15.6 ms timer tick and would be measured.
            if (timer.ElapsedMilliseconds < 100)
            {
                Thread.Yield();
            }
            else
            {
                Thread.Sleep(2);
            }
        }

        return -1;
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════
// Helper process: attach to a console and wait for it to close
// ═════════════════════════════════════════════════════════════════════════════════════════════════════

static class Helper
{
    private static ConsoleCtrlHandler? s_handler;

    public static int HoldAttached(string[] args)
    {
        int pid = int.Parse(args[1], CultureInfo.InvariantCulture);
        int seconds = int.Parse(args[2], CultureInfo.InvariantCulture);
        bool handle = args[3] == "handle";
        string log = args[4];
        Console.Out.Flush(); // bind stdout (our pipe) before the console changes
        FreeConsole();
        if (!AttachConsole(pid))
        {
            Console.WriteLine($"attach failed {Marshal.GetLastWin32Error()}");
            return 3;
        }

        if (handle)
        {
            s_handler = type =>
            {
                File.AppendAllText(log, $"ctrl {type} ");
                return true;
            };
            SetConsoleCtrlHandler(s_handler, true);
        }
        else
        {
            SetConsoleCtrlHandler(0, true);
        }

        Console.WriteLine("attached");
        Console.Out.Flush();
        Thread.Sleep(seconds * 1000);
        Console.WriteLine("survived");
        return 0;
    }

    public static int ReadHistory(string[] args)
    {
        int pid = int.Parse(args[1], CultureInfo.InvariantCulture);
        Console.Out.Flush(); // bind stdout (our pipe) before the console changes
        FreeConsole();
        if (!AttachConsole(pid))
        {
            Console.WriteLine($"attach failed {Marshal.GetLastWin32Error()}");
            return 3;
        }

        SetConsoleCtrlHandler(0, true);
        int count = CmdHistory.Read().Count;
        FreeConsole();
        Console.WriteLine($"{count} entries");
        return 0;
    }
}

/// <summary>Runs a command on a classic console host (no window, or a hidden one) and waits for it.</summary>
static class ClassicConsole
{
    public static int Run(string commandLine, string workingDirectory, bool hiddenWindow)
    {
        var si = new STARTUPINFOEXW();
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOW>();
        if (hiddenWindow)
        {
            si.StartupInfo.dwFlags = STARTF_USESHOWWINDOW;
            si.StartupInfo.wShowWindow = 0; // SW_HIDE: never shown, never handed off to Windows Terminal
        }

        uint flags = (hiddenWindow ? CREATE_NEW_CONSOLE : CREATE_NO_WINDOW) | CREATE_UNICODE_ENVIRONMENT;
        nint environment = Shells.EnvironmentBlock();
        try
        {
            if (!CreateProcessW(null, new StringBuilder(commandLine), 0, 0, false, flags, environment, workingDirectory, ref si, out var pi))
            {
                throw new Win32Exception();
            }

            CloseHandle(pi.hThread);
            WaitForSingleObject(pi.hProcess, 10_000);
            CloseHandle(pi.hProcess);
            return pi.dwProcessId;
        }
        finally
        {
            Marshal.FreeHGlobal(environment);
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════
// Shared helpers
// ═════════════════════════════════════════════════════════════════════════════════════════════════════

static class PsReadLine
{
    /// <summary>
    /// The named mutex PSReadLine guards its history file with: "PSReadLineHistoryFile_" + FNV-1a (32-bit) over the
    /// UTF-16 code units (low byte, then high byte) of the lower-cased path (Windows), same in 2.0.0 and 2.3.6.
    /// </summary>
    public static string MutexName(string path)
    {
        uint hash = 2166136261;
        foreach (char ch in path.ToLower())
        {
            hash = unchecked((hash ^ (uint)(ch & 0xFF)) * 16777619);
            hash = unchecked((hash ^ (uint)(ch >> 8)) * 16777619);
        }

        return "PSReadLineHistoryFile_" + hash.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Splits like File.ReadAllLines (CRLF, LF and CR end a line; no empty last line).</summary>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is '\r' or '\n')
            {
                lines.Add(text[start..i]);
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    /// <summary>PSReadLine's reading rule: a line ending with a backtick continues on the next line.</summary>
    public static List<string> ParseRecords(List<string> lines, out int multiLine, out int maxLines)
    {
        var records = new List<string>();
        var current = new StringBuilder();
        int linesInRecord = 0;
        multiLine = 0;
        maxLines = 0;
        foreach (var line in lines)
        {
            linesInRecord++;
            if (line.EndsWith('`'))
            {
                current.Append(line, 0, line.Length - 1).Append('\n');
                continue;
            }

            current.Append(line);
            records.Add(current.ToString());
            multiLine += linesInRecord > 1 ? 1 : 0;
            maxLines = Math.Max(maxLines, linesInRecord);
            current.Clear();
            linesInRecord = 0;
        }

        return records;
    }
}

static class Shells
{
    /// <summary>pwsh 7 (Program Files or PATH) and Windows PowerShell 5.1, labelled "pwsh" and "powershell".</summary>
    public static List<(string Label, string Exe)> PowerShellHosts()
    {
        var hosts = new List<(string, string)>();
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string? pwsh = new[] { Path.Combine(pf, "PowerShell", "7", "pwsh.exe") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(d => Path.Combine(d.Trim(), "pwsh.exe")))
            .FirstOrDefault(File.Exists);
        if (pwsh is not null)
        {
            hosts.Add(("pwsh", pwsh));
        }

        string windows = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(windows))
        {
            hosts.Add(("powershell", windows));
        }

        return hosts;
    }

    /// <summary>
    /// Removes what this probe's own shell leaks into children: PSModulePath (Windows PowerShell 5.1 would load
    /// PowerShell 7's modules), TERM/MSYS settings, and switches off pwsh's telemetry and update check.
    /// </summary>
    public static void CleanEnvironment(IDictionary<string, string?> env)
    {
        foreach (var name in new[] { "PSModulePath", "TERM", "MSYSTEM", "CLAUDECODE" })
        {
            env.Remove(name);
        }

        env["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        env["POWERSHELL_UPDATECHECK"] = "Off";
    }

    /// <summary>This process's environment, cleaned, as a CREATE_UNICODE_ENVIRONMENT block (free with FreeHGlobal).</summary>
    public static nint EnvironmentBlock()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            env[(string)e.Key] = e.Value as string ?? "";
        }

        CleanEnvironment(env);
        var block = new StringBuilder();
        foreach (var pair in env.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }

        block.Append('\0');
        return Marshal.StringToHGlobalUni(block.ToString());
    }
}

static class Terminals
{
    public static string Describe(string guid) => guid.ToUpperInvariant() switch
    {
        "{00000000-0000-0000-0000-000000000000}" => "Let Windows decide",
        "{B23D10C0-E52E-411E-9D5B-C09FDF709C7D}" => "Windows Console Host",
        "{2EACA947-7F5F-4CFA-BA87-8F7FBEEFBE69}" or "{E12CFF52-A866-4C77-9A90-F570A7AA2C6B}" => "Windows Terminal",
        "{06EC847C-C0A5-46B8-92CB-7C92F6E35CD5}" or "{86633F1F-6454-40EC-89CE-DA4EBA977EE2}" => "Windows Terminal Preview",
        "ABSENT" => "absent",
        _ => "unknown",
    };
}

static class Procs
{
    public static Dictionary<uint, (string Name, uint Parent)> Snapshot()
    {
        var map = new Dictionary<uint, (string, uint)>();
        nint snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == -1)
        {
            return map;
        }

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref entry))
            {
                do
                {
                    map[entry.th32ProcessID] = (entry.szExeFile, entry.th32ParentProcessID);
                }
                while (Process32NextW(snap, ref entry));
            }
        }
        finally
        {
            CloseHandle(snap);
        }

        return map;
    }

    public static uint SessionOf(uint pid) => ProcessIdToSessionId(pid, out uint session) ? session : uint.MaxValue;
}

static class Paths
{
    public static string PsReadLineFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "PowerShell", "PSReadLine");

    /// <summary>Replaces the profile folders with their variables, so no user name is printed.</summary>
    public static string Redact(string path)
    {
        foreach (var (folder, name) in new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "%DOCUMENTS%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"),
        })
        {
            if (folder.Length > 0 && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                return name + path[folder.Length..];
            }
        }

        return path;
    }
}

static class Files
{
    /// <summary>Reads without ever blocking a writer: shares read, write and delete. Missing file = empty.</summary>
    public static byte[] ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    public static string ReadSharedText(string path) => Encoding.UTF8.GetString(ReadShared(path));

    public static string Fingerprint(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "absent";
    }
}

static class Scratch
{
    public static string Create()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"bc-shellhistory-probe-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Delete(string dir)
    {
        for (int attempt = 0; attempt < 20 && Directory.Exists(dir); attempt++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                Thread.Sleep(250); // a child that just exited may still hold its working directory
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
        }
    }
}

static class Report
{
    public static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"== {title}");
    }

    public static void Line(int indent, string text) => Console.WriteLine(new string(' ', indent * 2) + text);
}

static class Native
{
    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const uint CREATE_NO_WINDOW = 0x08000000;
    public const uint CREATE_NEW_CONSOLE = 0x00000010;
    public const int STARTF_USESHOWWINDOW = 0x00000001;
    public const int STARTF_USESTDHANDLES = 0x00000100;
    public const nint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    public const uint EVENT_CONSOLE_START_APPLICATION = 0x4006;
    public const uint EVENT_CONSOLE_END_APPLICATION = 0x4007;
    public const uint WINEVENT_OUTOFCONTEXT = 0;
    public const uint WM_QUIT = 0x0012;
    public const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOW
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CONSOLE_HISTORY_INFO
    {
        public uint cbSize;
        public uint HistoryBufferSize;
        public uint NumberOfHistoryBuffers;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    public delegate void WinEventProc(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time);

    public delegate bool ConsoleCtrlHandler(uint ctrlType);

    [DllImport("kernel32", SetLastError = true)] public static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, uint size);
    [DllImport("kernel32")] public static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint pseudoConsole);
    [DllImport("kernel32")] public static extern void ClosePseudoConsole(nint pseudoConsole);
    [DllImport("kernel32", SetLastError = true)] public static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);
    [DllImport("kernel32", SetLastError = true)] public static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);
    [DllImport("kernel32")] public static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool CreateProcessW(string? application, StringBuilder commandLine, nint processAttributes, nint threadAttributes, bool inheritHandles, uint flags, nint environment, string? currentDirectory, ref STARTUPINFOEXW startupInfo, out PROCESS_INFORMATION information);
    [DllImport("kernel32", SetLastError = true)] public static extern bool CloseHandle(nint handle);
    [DllImport("kernel32", SetLastError = true)] public static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32", SetLastError = true)] public static extern bool TerminateProcess(nint process, uint exitCode);
    [DllImport("kernel32", SetLastError = true)] public static extern bool FreeConsole();
    [DllImport("kernel32", SetLastError = true)] public static extern bool AttachConsole(int processId);
    [DllImport("kernel32", SetLastError = true)] public static extern nint GetConsoleWindow();
    [DllImport("kernel32", SetLastError = true)] public static extern bool SetConsoleCtrlHandler(nint handler, bool add);
    [DllImport("kernel32", SetLastError = true)] public static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, bool add);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] public static extern uint GetConsoleCommandHistoryLengthW(string exeName);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] public static extern uint GetConsoleCommandHistoryW(byte[] buffer, uint length, string exeName);
    [DllImport("kernel32", SetLastError = true)] public static extern bool GetConsoleHistoryInfo(ref CONSOLE_HISTORY_INFO info);
    [DllImport("kernel32")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32", SetLastError = true)] public static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool Process32FirstW(nint snapshot, ref PROCESSENTRY32W entry);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool Process32NextW(nint snapshot, ref PROCESSENTRY32W entry);
    [DllImport("kernel32", SetLastError = true)] public static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("user32")] public static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32")] public static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32")] public static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32")] public static extern bool PeekMessageW(out MSG msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32")] public static extern nint DispatchMessageW(ref MSG msg);
    [DllImport("user32", SetLastError = true)] public static extern bool PostThreadMessageW(uint threadId, uint msg, nint wParam, nint lParam);
}
