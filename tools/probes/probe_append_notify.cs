// What a folder watcher (ReadDirectoryChangesW through FileSystemWatcher) and the folder's listing see while another
// process appends to a file (CLAUDE.md §2.21): Codex keeps its session files open and appends through one long-lived
// handle; Claude Code and Codex's CLI history open, append and close each time.
//
// Usage: dotnet run tools/probes/probe_append_notify.cs
//
// Writes BC-TEST lines to a temp folder only, then deletes it. Prints, per mode, the change notifications and the size the
// folder lists after each append (FileInfo and a directory enumeration, which read the folder's copy of the size), next to
// the size an open handle reports.
// Measured on Windows 11 26200 (2026-10-01): the long-lived handle raised no notification and the folder kept listing
// 0 bytes through 8 appends over 2.5 s, until the handle closed; with open-append-close every append raised Changed and
// the listing followed. A third mode shows that merely opening the file (another reader) updates the listing and raises
// Changed — what the prompt archive's poll relies on and must not loop on.
using System.Diagnostics;
using System.Text;

foreach (var mode in new[] { "long-lived handle", "open-append-close", "long-lived handle + a reader opening it" })
{
    var dir = Path.Combine(Path.GetTempPath(), "bc-append-notify-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, "rollout-BC-TEST.jsonl");
    var clock = Stopwatch.StartNew();
    var log = new List<string>();
    using var watcher = new FileSystemWatcher(dir)
    {
        NotifyFilter = NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
        InternalBufferSize = 64 * 1024,
    };
    watcher.Changed += (_, e) => { lock (log) log.Add($"{clock.ElapsedMilliseconds,6} ms  Changed"); };
    watcher.Created += (_, e) => { lock (log) log.Add($"{clock.ElapsedMilliseconds,6} ms  Created"); };
    watcher.EnableRaisingEvents = true;

    FileStream? held = mode.StartsWith("long-lived", StringComparison.Ordinal)
        ? new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)
        : null;
    for (int i = 0; i < 8; i++)
    {
        var line = Encoding.UTF8.GetBytes($"{{\"type\":\"BC-TEST\",\"n\":{i}}}\n");
        if (held is not null)
        {
            held.Write(line);
            held.Flush(); // to the OS, like Codex's tokio flush; no FlushFileBuffers
        }
        else
        {
            using var once = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            once.Write(line);
        }

        Thread.Sleep(300);
        long listed = new DirectoryInfo(dir).EnumerateFiles().First().Length;
        long opened = -1;
        if (mode.EndsWith("opening it", StringComparison.Ordinal))
        {
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            opened = reader.Length;
        }

        lock (log)
        {
            log.Add($"{clock.ElapsedMilliseconds,6} ms  appended {(i + 1) * line.Length} bytes; folder lists {listed}{(opened >= 0 ? $", reader's handle sees {opened}" : string.Empty)}");
        }
    }

    held?.Dispose();
    Thread.Sleep(800);
    lock (log)
    {
        log.Add($"{clock.ElapsedMilliseconds,6} ms  writer closed; folder lists {new DirectoryInfo(dir).EnumerateFiles().First().Length}");
    }

    watcher.EnableRaisingEvents = false;
    Console.WriteLine($"== {mode}");
    foreach (var entry in log)
    {
        Console.WriteLine("   " + entry);
    }

    Directory.Delete(dir, recursive: true);
}
