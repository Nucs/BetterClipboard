using System.ComponentModel;
using System.Diagnostics;
using BetterClipboard.Core.Startup;
using BetterClipboard.Core.Updates;
using BetterClipboard.Windows.Clipboard;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Shell;

/// <summary>
/// The other BetterClipboard processes of this sign-in session, as a starting copy sees them from outside: which program
/// each runs, its version, and whether it is the Microsoft Store edition (<see cref="InstanceTakeover"/> decides from it).
/// </summary>
/// <remarks>
/// <para>
/// <b>Only processes that run.</b> A crashed copy can leave a process object behind that has no threads and can be neither
/// opened nor stopped until Windows restarts (seen on the owner's PC, CLAUDE.md §4). It counts as nothing here, as in
/// <c>install.ps1</c>'s <c>Test-ProcessRuns</c>: a process list holding it must not keep a newer copy from taking over.
/// </para>
/// <para>
/// Reading another process needs only <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, which a copy of the same user always gets;
/// anything that cannot be read is reported as unknown (<see langword="null"/>), and the decision then keeps the hand-over.
/// </para>
/// </remarks>
public static class RunningCopies
{
    /// <summary>The process name of every BetterClipboard program (<c>BetterClipboard.exe</c>).</summary>
    private const string ProcessName = "BetterClipboard";

    /// <summary><c>ERROR_INSUFFICIENT_BUFFER</c>: <c>GetPackageFullName</c>'s answer, to the sizing call, for a packaged process.</summary>
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>
    /// Lists every other running BetterClipboard process of this session.
    /// </summary>
    /// <returns>The processes; empty when there are none. Never throws.</returns>
    public static IReadOnlyList<RunningCopy> ListOthers()
    {
        var found = new List<RunningCopy>();
        int ownId = Environment.ProcessId;
        int session;
        using (var self = Process.GetCurrentProcess())
        {
            session = self.SessionId;
        }

        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            using (process)
            {
                try
                {
                    if (process.Id == ownId || process.SessionId != session || process.Threads.Count == 0)
                    {
                        continue; // this process, another user's session, or a leftover process object that does not run
                    }

                    var path = SourceAppResolver.TryGetImagePath((uint)process.Id);
                    found.Add(new RunningCopy(process.Id, path, ProgramVersion.Read(path), IsPackaged(process.Id, path)));
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // It ended while it was being looked at.
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Whether a process runs from an app package (the Microsoft Store edition): asked from Windows, and taken from its path
    /// (Windows' package folder) when it cannot be asked.
    /// </summary>
    /// <param name="processId">The process.</param>
    /// <param name="path">Its program's path, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> for a packaged process.</returns>
    private static unsafe bool IsPackaged(int processId, string? path)
    {
        nint process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process != 0)
        {
            try
            {
                uint length = 0;
                return GetPackageFullName(process, ref length, null) == ErrorInsufficientBuffer;
            }
            catch (EntryPointNotFoundException)
            {
                // Never on a supported Windows; the path below still tells.
            }
            finally
            {
                CloseHandle(process);
            }
        }

        return path is not null && path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Waits on another process's end with no more rights than waiting needs.</summary>
public static class ProcessExitWait
{
    /// <summary>
    /// Opens a process for waiting on its end only (<c>SYNCHRONIZE</c>): what the next process of a silent restart needs to
    /// wait for the one it replaces (CLAUDE.md §2.46).
    /// </summary>
    /// <param name="processId">The process.</param>
    /// <returns>A handle the caller owns (signaled when the process ends), or <see langword="null"/> when the process is gone or cannot be opened. Never throws.</returns>
    /// <remarks>
    /// Not <see cref="Process.Handle"/>, which asks for every right on the process. A copy that Windows started with UI access
    /// was made at the High integrity level and lowered its own token to Medium at once (CLAUDE.md §2.63), but its process
    /// object keeps the High label it was made with, and Windows grants a Medium process only the right to wait and to read
    /// a little on it. With every right asked for, the next process of a silent restart could not wait, took the old process
    /// for gone, found it still holding the single-instance lock, and ended - so after Settings closed, no BetterClipboard
    /// ran at all (found in a test desktop, 2026-11-21).
    /// </remarks>
    public static Microsoft.Win32.SafeHandles.SafeWaitHandle? Open(int processId)
    {
        nint handle = OpenProcess(SYNCHRONIZE, false, unchecked((uint)processId));
        return handle == 0 ? null : new Microsoft.Win32.SafeHandles.SafeWaitHandle(handle, ownsHandle: true);
    }
}

/// <summary>Reads the version a BetterClipboard program reports, from its version resource.</summary>
public static class ProgramVersion
{
    /// <summary>
    /// The product version of a program (<c>0.2.10+a2c5fd7…</c>), with its build metadata dropped.
    /// </summary>
    /// <param name="path">The program's path, or <see langword="null"/>.</param>
    /// <returns>
    /// The version, or <see langword="null"/> when there is no file, it cannot be read, or its product version is not a
    /// version (a program that is not BetterClipboard's). Never throws.
    /// </returns>
    public static SemanticVersion? Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return SemanticVersion.TryParse(FileVersionInfo.GetVersionInfo(path).ProductVersion, out var version) ? version : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
