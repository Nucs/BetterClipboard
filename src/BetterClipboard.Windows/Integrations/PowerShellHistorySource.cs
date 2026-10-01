using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Shells;

namespace BetterClipboard.Windows.Integrations;

/// <summary>
/// Reads PowerShell's history files for the panel's Pwsh tab: every <c>*_history.txt</c> PSReadLine keeps in
/// <c>%APPDATA%\Microsoft\Windows\PowerShell\PSReadLine</c> — the console host's file, shared by Windows PowerShell
/// 5.1 and PowerShell 7, and the VS Code extension's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never disturbs PowerShell</b> (verified, CLAUDE.md §2.16): files are opened with
/// <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/> and closed at once — a reader that did not
/// share write access made PSReadLine print "Error reading or writing history file" into the user's console. Its
/// named mutex is never taken (holding it silently postponed a command's write). The files are never written.
/// </para>
/// <para>
/// <b>Cost.</b> Read only when the tab loads (opening it, typing a search), never on a timer. A file is parsed again
/// only when its size or last-write time changed: 295 KB and 5,837 commands took 2.5 ms. Files over
/// <see cref="MaxFileBytes"/> are read from their last <see cref="MaxFileBytes"/> only.
/// </para>
/// <para>
/// <b>Dev/test:</b> <see cref="FolderVariable"/> points it at another folder, so an isolated instance or a test never
/// reads the user's real history. A profile can move PSReadLine's file (<c>Set-PSReadLineOption -HistorySavePath</c>);
/// such a file is not found (reading the profile would mean running it).
/// </para>
/// Thread-safe: <see cref="ReadCommands"/> may run on any thread; the parse cache is locked.
/// </remarks>
public sealed class PowerShellHistorySource
{
    /// <summary>Environment variable that replaces the PSReadLine folder (dev and test runs).</summary>
    public const string FolderVariable = "BETTERCLIPBOARD_PSREADLINE_DIR";

    /// <summary>Largest part of one file read (its end, where the newest commands are).</summary>
    public const long MaxFileBytes = 32L * 1024 * 1024;

    /// <summary>The parsed commands of each file, by path, with the size and write time they were parsed at.</summary>
    private readonly Dictionary<string, (long Length, DateTime LastWriteUtc, IReadOnlyList<string> Records)> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Guards <see cref="cache"/>.</summary>
    private readonly object gate = new();

    /// <summary>
    /// Creates the source over PSReadLine's folder (or <see cref="FolderVariable"/>'s).
    /// </summary>
    /// <param name="folder">The folder to read; <see langword="null"/> = <see cref="ResolveFolder"/>.</param>
    public PowerShellHistorySource(string? folder = null)
    {
        Folder = folder ?? ResolveFolder();
    }

    /// <summary>The folder read.</summary>
    public string Folder { get; }

    /// <summary>How many commands the files held at the last read (all records, repeats included); -1 before the first.</summary>
    public int LastRecordCount { get; private set; } = -1;

    /// <summary>How many history files were found at the last read.</summary>
    public int LastFileCount { get; private set; }

    /// <summary>
    /// PSReadLine's folder: <see cref="FolderVariable"/> when set, else
    /// <c>Environment.GetFolderPath(ApplicationData)\Microsoft\Windows\PowerShell\PSReadLine</c> — the known folder,
    /// exactly as PSReadLine builds it (not the <c>%APPDATA%</c> variable).
    /// </summary>
    /// <returns>The folder (it may not exist).</returns>
    public static string ResolveFolder()
    {
        var overridden = Environment.GetEnvironmentVariable(FolderVariable);
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "PowerShell", "PSReadLine")
            : overridden.Trim();
    }

    /// <summary>Whether there is a history file to read (decides whether the panel shows the Pwsh tab). Cheap.</summary>
    /// <returns><see langword="true"/> when the folder holds at least one <c>*_history.txt</c>.</returns>
    public bool HasHistory()
    {
        try
        {
            return Directory.Exists(Folder) && Directory.EnumerateFiles(Folder, "*_history.txt").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads every history file and returns its distinct commands, newest first. A command typed in several hosts
    /// shows once (at its place in the most recently written file), with the counts added up.
    /// </summary>
    /// <returns>The commands; empty when there is no file. Never throws: an unreadable file is skipped (logged).</returns>
    public IReadOnlyList<ShellCommand> ReadCommands()
    {
        List<FileInfo> files;
        try
        {
            files = Directory.Exists(Folder)
                ? new DirectoryInfo(Folder).EnumerateFiles("*_history.txt").OrderByDescending(f => f.LastWriteTimeUtc).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Listing PowerShell's history folder failed: {ex.Message}");
            files = [];
        }

        var order = new List<(string Text, string Source)>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int records = 0;
        foreach (var file in files)
        {
            var parsed = Parse(file);
            records += parsed.Count;
            var source = PsReadLineHistory.SourceName(file.Name);
            foreach (var (text, count) in PsReadLineHistory.NewestFirst(parsed))
            {
                if (counts.TryGetValue(text, out var known))
                {
                    counts[text] = known + count;
                }
                else
                {
                    counts[text] = count;
                    order.Add((text, source));
                }
            }
        }

        LastRecordCount = records;
        LastFileCount = files.Count;
        return order.Select(o => new ShellCommand(ShellKind.PowerShell, o.Text, counts[o.Text], null, o.Source)).ToList();
    }

    /// <summary>One file's commands, oldest first, from the cache when its size and write time are unchanged.</summary>
    /// <param name="file">The file.</param>
    /// <returns>The commands (empty when unreadable, logged).</returns>
    private IReadOnlyList<string> Parse(FileInfo file)
    {
        lock (gate)
        {
            if (cache.TryGetValue(file.FullName, out var cached) && cached.Length == file.Length && cached.LastWriteUtc == file.LastWriteTimeUtc)
            {
                return cached.Records;
            }
        }

        IReadOnlyList<string> records;
        try
        {
            records = PsReadLineHistory.ParseRecords(PsReadLineHistory.Decode(ReadShared(file.FullName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Content-free: the host's file name only.
            AppLog.Warn($"Reading PowerShell's history file '{file.Name}' failed: {ex.Message}");
            return [];
        }

        lock (gate)
        {
            cache[file.FullName] = (file.Length, file.LastWriteTimeUtc, records);
        }

        return records;
    }

    /// <summary>
    /// Reads a file without ever blocking PSReadLine (see class remarks): shares read, write and delete, and reads at
    /// most the last <see cref="MaxFileBytes"/>, from the first line boundary in that window.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>Its bytes (or the end of them).</returns>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access was denied.</exception>
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
        long start = Math.Max(0, stream.Length - MaxFileBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var memory = new MemoryStream((int)Math.Min(stream.Length - start, int.MaxValue));
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        if (start == 0)
        {
            return bytes;
        }

        // Started mid-file: drop the partial first line (it may even split a UTF-8 sequence).
        int newline = Array.IndexOf(bytes, (byte)'\n');
        return newline < 0 ? [] : bytes[(newline + 1)..];
    }
}
