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
