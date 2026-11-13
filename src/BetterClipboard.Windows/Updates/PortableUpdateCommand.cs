using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using BetterClipboard.Core.Updates;
using BetterClipboard.Windows.Shell;

namespace BetterClipboard.Windows.Updates;

/// <summary>
/// What the old version asked the helper to do (<see cref="PortableUpdate"/>'s command line, parsed).
/// </summary>
/// <param name="TargetDirectory">The portable folder to update: the running copy's folder.</param>
/// <param name="ProcessId">The running copy's process id.</param>
/// <param name="ProcessStartTicks">Its start time in UTC ticks, so a stranger that got the id later is not mistaken for it.</param>
/// <param name="ResultPath">Where the outcome goes (<see cref="UpdateResult"/>), read by the app at its next start.</param>
/// <param name="LogPath">Where the helper writes what it did.</param>
public sealed record PortableUpdateRequest(string TargetDirectory, int ProcessId, long ProcessStartTicks, string ResultPath, string LogPath);

/// <summary>
/// The command line of a portable copy's update helper (<c>BetterClipboard.exe --apply-portable-update …</c>, run from the
/// new version's staging folder), and the cleanup of what an update leaves in the portable folder.
/// </summary>
/// <remarks>
/// Recognized by <c>Program.Main</c> before anything else: the helper takes no single-instance lock, starts no XAML and
/// opens no store; it only replaces files and reports (<see cref="PortableUpdateHelper"/>).
/// </remarks>
public static class PortableUpdateCommand
{
    /// <summary>Exit code: the update is in place and the new version was started.</summary>
    public const int Updated = 0;

    /// <summary>Exit code: the command line is not the contract's (nothing was done, not even a result written).</summary>
    public const int BadArguments = 2;

    /// <summary>Exit code: the target is not a BetterClipboard folder; the running copy was not disturbed.</summary>
    public const int NotAnAppFolder = 3;

    /// <summary>Exit code: the running copy did not close; nothing was replaced.</summary>
    public const int AppDidNotClose = 4;

    /// <summary>Exit code: a file could not be replaced; the previous version was put back and started again.</summary>
    public const int RolledBack = 5;

    /// <summary>Exit code: a file could not be replaced and the previous version could not be put back completely.</summary>
    public const int RollbackIncomplete = 6;

    /// <summary>Whether a command line asks for the helper.</summary>
    /// <param name="args">The process's arguments.</param>
    /// <returns><see langword="true"/> when the first argument is <see cref="PortableUpdate.ApplySwitch"/>.</returns>
    public static bool Matches(IReadOnlyList<string> args) =>
        args is { Count: > 0 } && string.Equals(args[0], PortableUpdate.ApplySwitch, StringComparison.Ordinal);

    /// <summary>
    /// Reads the helper's command line: <see cref="PortableUpdate.ApplySwitch"/> followed by the four switches with their
    /// values, in any order, each exactly once. Unknown switches are ignored, so a later release may pass more.
    /// </summary>
    /// <param name="args">The process's arguments.</param>
    /// <param name="request">The parsed request.</param>
    /// <returns><see langword="true"/> when every switch has a usable value: full paths, and a process as <c>pid:ticks</c>.</returns>
    public static bool TryParse(IReadOnlyList<string> args, [NotNullWhen(true)] out PortableUpdateRequest? request)
    {
        request = null;
        if (!Matches(args))
        {
            return false;
        }

        string? target = null, waitFor = null, result = null, log = null;
        for (int i = 1; i < args.Count; i++)
        {
            string name = args[i];
            if (name is not (PortableUpdate.TargetSwitch or PortableUpdate.WaitForSwitch or PortableUpdate.ResultSwitch or PortableUpdate.LogSwitch))
            {
                continue; // a switch of a later release, or its value
            }

            if (i + 1 >= args.Count)
            {
                return false;
            }

            string value = args[++i];

            // Given twice: not the contract, and no way to tell which one was meant.
            bool repeated = name switch
            {
                PortableUpdate.TargetSwitch => !Set(ref target, value),
                PortableUpdate.WaitForSwitch => !Set(ref waitFor, value),
                PortableUpdate.ResultSwitch => !Set(ref result, value),
                _ => !Set(ref log, value),
            };
            if (repeated)
            {
                return false;
            }
        }

        if (!IsFullPath(target) || !IsFullPath(result) || !IsFullPath(log) || !TryParseProcess(waitFor, out int id, out long ticks))
        {
            return false;
        }

        request = new PortableUpdateRequest(Path.TrimEndingDirectorySeparator(target!), id, ticks, result!, log!);
        return true;
    }

    /// <summary>
    /// Removes what earlier updates left in a portable folder: backup folders (<see cref="PortableUpdate.BackupFolderPrefix"/>)
    /// whose files another program still held when the update ended (a DLL that a program loaded through the PATH). Run at
    /// the app's start; anything still in use stays for the next start.
    /// </summary>
    /// <param name="appDirectory">The running copy's folder.</param>
    /// <returns>How many leftover folders were removed. Never throws.</returns>
    public static int CleanLeftovers(string appDirectory)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(appDirectory))
            {
                return 0;
            }

            foreach (var folder in Directory.EnumerateDirectories(appDirectory, PortableUpdate.BackupFolderPrefix + "*"))
            {
                try
                {
                    // Hidden while it was written; a hidden or read-only file inside is still deleted by Directory.Delete
                    // only when it is not read-only, so attributes are cleared first.
                    foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }

                    Directory.Delete(folder, recursive: true);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still held by another program; the next start tries again.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Nothing depends on a tidy folder.
        }

        return removed;
    }

    /// <summary>Stores a switch's value unless the switch was given before.</summary>
    /// <param name="slot">The switch's value so far.</param>
    /// <param name="value">The new value.</param>
    /// <returns><see langword="false"/> when the switch already had a value (it is then left as it was).</returns>
    private static bool Set(ref string? slot, string value)
    {
        if (slot is not null)
        {
            return false;
        }

        slot = value;
        return true;
    }

    /// <summary>Whether a value is a full local or UNC path.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> for a full path.</returns>
    private static bool IsFullPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            return Path.IsPathFullyQualified(value);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Reads <c>pid:ticks</c>.</summary>
    /// <param name="value">The value.</param>
    /// <param name="id">The process id.</param>
    /// <param name="ticks">Its start time in UTC ticks.</param>
    /// <returns><see langword="true"/> when both parts are positive numbers.</returns>
    private static bool TryParseProcess(string? value, out int id, out long ticks)
    {
        id = 0;
        ticks = 0;
        var parts = value?.Split(':') ?? [];
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ticks) && ticks > 0;
    }
}

/// <summary>
/// Applies a portable copy's update: run as the new version's <c>BetterClipboard.exe</c> from its staging folder
/// (<see cref="PortableUpdateCommand"/>), it asks the running copy to exit, waits for it, replaces the folder's files one
/// by one, writes the outcome for the app, and starts BetterClipboard again from the same path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Replacing, not swapping.</b> An installer's update renames the whole folder aside and puts the new one in its place
/// (<c>install.ps1 -Update</c>). A portable folder may hold other things (a zip extracted into a shared tools folder), so
/// here only the files the release ships are touched: each old file is renamed into a backup folder inside the portable
/// folder (a rename, so a DLL another program loaded through the PATH can still be moved), then the new file is copied
/// to its place. Files the release does not ship stay as they are — the user's own files, and also files an older
/// release had and this one no longer ships (no list of them exists).
/// </para>
/// <para>
/// <b>All or nothing.</b> A file that cannot be moved or written within <see cref="FileRetry"/> (a program holds it open
/// without sharing it) undoes every replacement made so far, in reverse order; the previous version then starts again,
/// and the result file says which file and why. Only when putting a file back fails too does the folder hold a mix, and
/// the message says to extract the release over it.
/// </para>
/// <para>
/// <b>Logged</b> to the request's log file (the same file an installer's transcript goes to), with folder names but never
/// clipboard content. The helper is the new version, so its words are the new version's.
/// </para>
/// </remarks>
public sealed class PortableUpdateHelper
{
    /// <summary>The program at the root of a release, which must exist in both folders.</summary>
    private const string ProgramName = PortableUpdateInstaller.ProgramName;

    /// <summary>The staging folder holding the new version (the folder this helper runs from).</summary>
    public required string SourceDirectory { get; init; }

    /// <summary>The new version, as its program reports it (written into the result file).</summary>
    public required string Version { get; init; }

    /// <summary>
    /// Asks the running copy to exit (its instance's <c>Exit</c> event); <see langword="false"/> when no such instance listens.
    /// </summary>
    public required Func<bool> RequestExit { get; init; }

    /// <summary>
    /// Starts BetterClipboard from a program path, in the background; <see langword="false"/> when Windows refused. By
    /// default a quiet start (<see cref="QuietProcess"/>) with <c>--background</c>, in the program's folder.
    /// </summary>
    public Func<string, bool> StartApp { get; init; } = StartInBackground;

    /// <summary>How long the running copy may take to exit by itself (it saves its queued copies first).</summary>
    public TimeSpan ExitWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait after ending a copy that did not exit by itself.</summary>
    public TimeSpan KillWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long one file is retried while another program holds it (an antivirus scan, an editor).</summary>
    public TimeSpan FileRetry { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The pause between two tries of a held file.</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Runs the update.
    /// </summary>
    /// <param name="request">What to update, and where to report.</param>
    /// <returns>One of <see cref="PortableUpdateCommand"/>'s exit codes.</returns>
    /// <remarks>
    /// Never throws: every failure ends up in the result file and the log, and the app (whichever version is then in the
    /// folder) is started again unless the running copy never closed.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public int Run(PortableUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var log = HelperLog.Open(request.LogPath);
        log.Write($"BetterClipboard {Version}: updating the portable copy in {request.TargetDirectory} from {SourceDirectory}.");
        var target = request.TargetDirectory;
        var program = Path.Combine(target, ProgramName);
        bool closed = false;
        try
        {
            // Before the running copy is disturbed: a folder without the program is not what the old version meant to
            // update, and a staging folder without one could not start afterwards.
            if (!File.Exists(program) || !File.Exists(Path.Combine(SourceDirectory, ProgramName)) || SameFolder(target, SourceDirectory))
            {
                log.Write("The target holds no BetterClipboard.exe (or is the staging folder itself): nothing was touched.");
                Report(request, log, ok: false, UpdateMessages.Format(UpdateMessages.PortableNotAppFolder, target));
                return PortableUpdateCommand.NotAnAppFolder;
            }

            closed = CloseRunningCopy(request, log);
            if (!closed)
            {
                Report(request, log, ok: false, UpdateMessages.PortableAppDidNotClose);
                return PortableUpdateCommand.AppDidNotClose;
            }

            var outcome = Replace(target, log);
            if (outcome.Failure is null)
            {
                Report(request, log, ok: true, string.Empty);
                return PortableUpdateCommand.Updated;
            }

            var message = outcome.RestoredCompletely
                ? UpdateMessages.Format(UpdateMessages.PortableFileNotReplaced, outcome.FailedFile!, outcome.Failure)
                : UpdateMessages.Format(UpdateMessages.PortableRestoreIncomplete, outcome.FailedFile!, outcome.Failure, target);
            Report(request, log, ok: false, message);
            return outcome.RestoredCompletely ? PortableUpdateCommand.RolledBack : PortableUpdateCommand.RollbackIncomplete;
        }
        catch (Exception ex)
        {
            // A bug or an unexpected system error: report it rather than vanish, so the next start can say something.
            log.Write($"Unexpected failure: {ex}");
            Report(request, log, ok: false, UpdateMessages.UpdateFailedUnexpectedly);
            return PortableUpdateCommand.RollbackIncomplete;
        }
        finally
        {
            // Once the running copy was closed, BetterClipboard starts again whatever happened: the new version on
            // success, the previous one after a rollback. Its start reads the result file and says what happened.
            if (closed)
            {
                log.Write(StartApp(program) ? "Started BetterClipboard again from its folder." : "BetterClipboard could not be started again; start it by hand.");
            }
        }
    }

    /// <summary>
    /// Asks the running copy to exit and waits for it; ends it when it does not exit in time.
    /// </summary>
    /// <param name="request">Names the process.</param>
    /// <param name="log">The helper's log.</param>
    /// <returns><see langword="true"/> once the process is gone.</returns>
    private bool CloseRunningCopy(PortableUpdateRequest request, HelperLog log)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(request.ProcessId);
            if (process.StartTime.ToUniversalTime().Ticks != request.ProcessStartTicks)
            {
                process.Dispose();
                log.Write("The copy that asked for the update has ended already.");
                return true; // a stranger with its id
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            log.Write("The copy that asked for the update has ended already.");
            return true;
        }

        using (process)
        {
            var watch = Stopwatch.StartNew();
            log.Write(RequestExit()
                ? $"Asked the running copy (process {request.ProcessId}) to exit."
                : $"The running copy (process {request.ProcessId}) does not listen for an exit request; waiting for it anyway.");
            if (process.WaitForExit(ExitWait))
            {
                log.Write($"It exited after {watch.ElapsedMilliseconds} ms.");
                return true;
            }

            log.Write($"It did not exit within {ExitWait.TotalSeconds:0} s; ending it.");
            try
            {
                process.Kill();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                log.Write($"Ending it failed: {ex.Message}");
            }

            if (process.WaitForExit(KillWait))
            {
                return true;
            }

            log.Write("It is still running: nothing was replaced.");
            return false;
        }
    }

    /// <summary>
    /// Replaces the folder's files with the staging folder's, all or nothing.
    /// </summary>
    /// <param name="target">The portable folder.</param>
    /// <param name="log">The helper's log.</param>
    /// <returns>What happened.</returns>
    internal ReplaceOutcome Replace(string target, HelperLog log)
    {
        var watch = Stopwatch.StartNew();

        // The program last: until every other file is in place, a start from this folder (a sign-in entry, a double click)
        // runs the old program, which never loads a half-new set of files by name.
        var files = Directory.EnumerateFiles(SourceDirectory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(SourceDirectory, file))
            .OrderBy(relative => string.Equals(relative, ProgramName, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(relative => relative, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var backup = Path.Combine(target, $"{PortableUpdate.BackupFolderPrefix}-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}");
        try
        {
            Directory.CreateDirectory(backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder cannot be written (it could when the update was offered): nothing was touched yet.
            log.Write($"The backup folder could not be created: {ex.Message}");
            return new ReplaceOutcome(ProgramName, ex.Message.TrimEnd('.'), RestoredCompletely: true);
        }

        TryHide(backup);
        log.Write($"Replacing {files.Count:N0} files; the previous ones are kept in {backup} until the end.");

        var done = new List<Replacement>(files.Count);
        var createdFolders = new List<string>();
        foreach (var relative in files)
        {
            var newFile = Path.Combine(target, relative);
            var aside = Path.Combine(backup, relative);
            try
            {
                if (Directory.Exists(newFile))
                {
                    throw new IOException("a folder of that name is in the way");
                }

                bool existed = File.Exists(newFile);
                if (existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(aside)!);

                    // A rename: it works for a file another program has loaded (a DLL found through the PATH), and keeps
                    // the old bytes untouched for the way back.
                    Retry(() => File.Move(newFile, aside));
                }

                try
                {
                    EnsureFolder(Path.GetDirectoryName(newFile)!, createdFolders);
                    Retry(() =>
                    {
                        // A try that failed half-way leaves a partial file behind; the next try starts from nothing.
                        if (File.Exists(newFile))
                        {
                            File.Delete(newFile);
                        }

                        File.Copy(Path.Combine(SourceDirectory, relative), newFile);
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // This file's own half first: its old copy goes back before the earlier files are undone.
                    if (existed)
                    {
                        TryDelete(newFile);
                        TryMove(aside, newFile);
                    }

                    throw;
                }

                done.Add(new Replacement(newFile, existed ? aside : null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Write($"{relative} could not be replaced: {ex.Message} Putting the previous version back.");
                bool restored = Undo(done, createdFolders, log);
                TryDeleteFolder(backup);
                return new ReplaceOutcome(relative, ex.Message.TrimEnd('.'), restored);
            }
        }

        log.Write($"Replaced {files.Count:N0} files ({done.Count(step => step.Aside is null):N0} new) in {watch.ElapsedMilliseconds:N0} ms.");
        log.Write(TryDeleteFolder(backup)
            ? "Removed the previous version's files."
            : $"Some of the previous version's files are still in use by another program; they stay in {backup} and are removed at a later start.");
        return new ReplaceOutcome(null, null, RestoredCompletely: true);
    }

    /// <summary>Undoes replacements in reverse order: the new file deleted, the old one renamed back.</summary>
    /// <param name="done">The replacements made.</param>
    /// <param name="createdFolders">Folders created for new files, deepest last.</param>
    /// <param name="log">The helper's log.</param>
    /// <returns><see langword="true"/> when every file is back as it was.</returns>
    private bool Undo(List<Replacement> done, List<string> createdFolders, HelperLog log)
    {
        bool complete = true;
        for (int i = done.Count - 1; i >= 0; i--)
        {
            var step = done[i];
            try
            {
                Retry(() =>
                {
                    if (File.Exists(step.Path))
                    {
                        File.Delete(step.Path);
                    }
                });

                if (step.Aside is not null)
                {
                    Retry(() => File.Move(step.Aside, step.Path));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                complete = false;
                log.Write($"{step.Path} could not be put back: {ex.Message}");
            }
        }

        // Folders made for files the old version did not have: removed when they are empty again.
        for (int i = createdFolders.Count - 1; i >= 0; i--)
        {
            try
            {
                if (Directory.Exists(createdFolders[i]) && !Directory.EnumerateFileSystemEntries(createdFolders[i]).Any())
                {
                    Directory.Delete(createdFolders[i]);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An empty folder left behind costs nothing.
            }
        }

        log.Write(complete ? "The previous version is back in place." : "The previous version could not be put back completely.");
        return complete;
    }

    /// <summary>Creates a folder (and its parents), remembering each one that did not exist.</summary>
    /// <param name="folder">The folder.</param>
    /// <param name="created">Receives the folders created, parents first.</param>
    private static void EnsureFolder(string folder, List<string> created)
    {
        if (Directory.Exists(folder))
        {
            return;
        }

        var missing = new Stack<string>();
        for (var current = folder; current is not null && !Directory.Exists(current); current = Path.GetDirectoryName(current))
        {
            missing.Push(current);
        }

        while (missing.TryPop(out var next))
        {
            Directory.CreateDirectory(next);
            created.Add(next);
        }
    }

    /// <summary>
    /// Runs a file operation, trying again while another program holds the file, for at most <see cref="FileRetry"/>.
    /// </summary>
    /// <param name="operation">The operation.</param>
    /// <exception cref="IOException">The last try's failure, after the time ran out.</exception>
    /// <exception cref="UnauthorizedAccessException">The last try's failure, after the time ran out.</exception>
    private void Retry(Action operation)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && watch.Elapsed < FileRetry)
            {
                // A file in use (an antivirus scanning a freshly written DLL, an editor): it is usually free a moment later.
                Thread.Sleep(RetryInterval);
            }
        }
    }

    /// <summary>Writes the result file; a failure to write it is logged (the app then says the installer wrote nothing).</summary>
    /// <param name="request">Names the file.</param>
    /// <param name="log">The helper's log.</param>
    /// <param name="ok">Whether the new version is in place.</param>
    /// <param name="error">The reason when it is not; empty otherwise.</param>
    private void Report(PortableUpdateRequest request, HelperLog log, bool ok, string error)
    {
        log.Write(ok ? $"Result: BetterClipboard {Version} is in place." : $"Result: not updated. {error}");
        try
        {
            new UpdateResult(Version, ok, error, DateTimeOffset.UtcNow).Write(request.ResultPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log.Write($"The result file could not be written: {ex.Message}");
        }
    }

    /// <summary>Starts BetterClipboard in the background from a program path, quietly (no busy pointer).</summary>
    /// <param name="program">The program's path.</param>
    /// <returns><see langword="true"/> when Windows started it.</returns>
    private static bool StartInBackground(string program)
    {
        try
        {
            using var started = QuietProcess.Start(program, "--background", Path.GetDirectoryName(program)!);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or IOException)
        {
            return false;
        }
    }

    /// <summary>Whether two paths name the same folder (full paths, ignoring case and a trailing separator).</summary>
    /// <param name="first">One folder.</param>
    /// <param name="second">The other.</param>
    /// <returns><see langword="true"/> when they are the same.</returns>
    private static bool SameFolder(string first, string second) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);

    /// <summary>Marks the backup folder hidden, so Explorer does not show it while the update runs; failures are ignored.</summary>
    /// <param name="folder">The folder.</param>
    private static void TryHide(string folder)
    {
        try
        {
            File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.Hidden);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Visible is only untidy.
        }
    }

    /// <summary>Deletes a file, ignoring a failure.</summary>
    /// <param name="path">The file.</param>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The undo below reports what stays wrong.
        }
    }

    /// <summary>Renames a file back, ignoring a failure (the caller's undo reports the outcome).</summary>
    /// <param name="from">The file now.</param>
    /// <param name="to">Where it belongs.</param>
    private static void TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in the backup folder; the result says the previous version could not be put back.
        }
    }

    /// <summary>Removes a folder and its contents.</summary>
    /// <param name="folder">The folder.</param>
    /// <returns><see langword="false"/> when something in it is still in use (left for <see cref="PortableUpdateCommand.CleanLeftovers"/>).</returns>
    private static bool TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>One replaced file.</summary>
    /// <param name="Path">The file in the portable folder (now the new version's).</param>
    /// <param name="Aside">Where its old copy waits in the backup folder; <see langword="null"/> for a file the old version did not have.</param>
    private sealed record Replacement(string Path, string? Aside);
}

/// <summary>What <see cref="PortableUpdateHelper.Replace"/> did.</summary>
/// <param name="FailedFile">The file (relative to the folder) that could not be replaced; <see langword="null"/> on success.</param>
/// <param name="Failure">Why, in the system's words; <see langword="null"/> on success.</param>
/// <param name="RestoredCompletely">After a failure: whether every file is back as it was. Always true on success.</param>
internal sealed record ReplaceOutcome(string? FailedFile, string? Failure, bool RestoredCompletely);

/// <summary>
/// The helper's log: one line per step with a time, written through at once, so a helper that is ended half-way still
/// leaves what it did. Never throws: a log that cannot be written is simply not kept.
/// </summary>
internal sealed class HelperLog : IDisposable
{
    private readonly StreamWriter? writer;

    /// <summary>Wraps an open writer, or none.</summary>
    /// <param name="writer">The writer; <see langword="null"/> when the log could not be opened.</param>
    private HelperLog(StreamWriter? writer) => this.writer = writer;

    /// <summary>Opens (replaces) the log file.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The log; one that writes nothing when the file cannot be opened.</returns>
    public static HelperLog Open(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Shared for reading: the app, or the person troubleshooting, may look at it while the helper works.
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            return new HelperLog(new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new HelperLog(null);
        }
    }

    /// <summary>Writes one line with the local time in front.</summary>
    /// <param name="message">The line.</param>
    public void Write(string message)
    {
        try
        {
            writer?.WriteLine($"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}  {message}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // A full disk: the log stops, the update goes on.
        }
    }

    /// <summary>Closes the file.</summary>
    public void Dispose() => writer?.Dispose();
}
