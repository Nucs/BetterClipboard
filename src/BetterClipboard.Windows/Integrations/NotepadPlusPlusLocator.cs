using System.Diagnostics;
using BetterClipboard.Core.NotepadPlusPlus;
using Microsoft.Win32;

namespace BetterClipboard.Windows.Integrations;

/// <summary>
/// What the Notepad++ integration found: the executable, its version, the settings folders whose <c>session.xml</c> it reads,
/// and Notepad++'s backup settings (CLAUDE.md §2.41).
/// </summary>
/// <param name="ExecutablePath">Notepad++'s executable (a running one first, else the installed one), or <see langword="null"/>.</param>
/// <param name="Version">Its file version ("8.9.1"), or <see langword="null"/>.</param>
/// <param name="DataFolders">Settings folders that hold (or will hold) a <c>session.xml</c>; usually one.</param>
/// <param name="DetectedBy">How it was found, in English (for the log only: the status line names the version).</param>
/// <param name="Config">Notepad++'s backup settings read from the first folder's <c>config.xml</c>.</param>
public sealed record NotepadPlusPlusLocation(string? ExecutablePath, string? Version, IReadOnlyList<string> DataFolders, string DetectedBy, NotepadPlusPlusConfig Config)
{
    /// <summary>Nothing found.</summary>
    public static NotepadPlusPlusLocation None { get; } = new(null, null, [], "not found", NotepadPlusPlusConfig.Defaults);

    /// <summary>Whether Notepad++ is installed or has left its settings folder on this PC.</summary>
    public bool IsInstalled => ExecutablePath is not null || DataFolders.Count > 0;

    /// <summary>Whether two locations watch the same folders (case-insensitive).</summary>
    /// <param name="other">The other location.</param>
    /// <returns><see langword="true"/> when the folders are the same, in any order.</returns>
    public bool WatchesSameAs(NotepadPlusPlusLocation other) =>
        DataFolders.Count == other.DataFolders.Count && DataFolders.All(f => other.DataFolders.Contains(f, StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Finds Notepad++ and the settings folders it keeps its session in, by Notepad++'s own rules (8.9.1,
/// <c>NppParameters::load</c>; CLAUDE.md §2.41). Never throws; reads only.
/// </summary>
/// <remarks>
/// <para>
/// <b>The settings folder of one Notepad++:</b> <c>-settingsDir=</c> on its command line; else the executable's own folder when
/// <c>doLocalConf.xml</c> lies next to it (the portable build) and that folder is not directly in Program Files (Notepad++
/// ignores the file there); else <c>%APPDATA%\Notepad++</c>. The session always stays there, also with cloud settings (only
/// the backups move with those, and <c>session.xml</c> names each backup by its full path).
/// </para>
/// <para>
/// <b>Which Notepad++:</b> every running <c>notepad++.exe</c> (from its process), else the installed one (the registry's
/// <c>HKLM\SOFTWARE\Notepad++</c>), plus <c>%APPDATA%\Notepad++</c> whenever it holds a session.
/// <c>BETTERCLIPBOARD_NOTEPADPP_DIR</c> replaces all of it with one folder (tests and isolated instances never read the user's
/// Notepad++).
/// </para>
/// </remarks>
public static class NotepadPlusPlusLocator
{
    /// <summary>Environment variable naming a settings folder to use instead of every real one (tests, isolated runs).</summary>
    public const string OverrideVariable = "BETTERCLIPBOARD_NOTEPADPP_DIR";

    /// <summary>Finds Notepad++ on this PC.</summary>
    /// <returns>The location; <see cref="NotepadPlusPlusLocation.None"/> when nothing is found.</returns>
    public static NotepadPlusPlusLocation Locate()
    {
        try
        {
            var overrideFolder = Environment.GetEnvironmentVariable(OverrideVariable);
            if (!string.IsNullOrWhiteSpace(overrideFolder))
            {
                return Build(null, [Path.GetFullPath(overrideFolder)], $"{OverrideVariable}");
            }

            var folders = new List<string>();
            string? executable = null;
            string detectedBy = "not found";
            foreach (var (path, commandLine) in RunningCopies())
            {
                executable ??= path;
                detectedBy = "running";
                AddOnce(folders, SettingsFolderOf(path, commandLine));
            }

            if (executable is null && InstalledExecutable() is { } installed)
            {
                executable = installed;
                detectedBy = "installed";
                AddOnce(folders, SettingsFolderOf(installed, null));
            }

            var appData = AppDataFolder();
            if (appData is not null && File.Exists(Path.Combine(appData, "session.xml")))
            {
                AddOnce(folders, appData);
                if (detectedBy == "not found")
                {
                    detectedBy = "its settings folder";
                }
            }

            return Build(executable, folders.Where(Directory.Exists).ToList(), detectedBy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or InvalidOperationException)
        {
            return NotepadPlusPlusLocation.None;
        }
    }

    /// <summary>
    /// The settings folder of one Notepad++ (see the class remarks).
    /// </summary>
    /// <param name="executablePath">Its executable.</param>
    /// <param name="commandLine">Its command line, when known (for <c>-settingsDir=</c>).</param>
    /// <returns>The folder (it may not exist yet).</returns>
    internal static string SettingsFolderOf(string executablePath, string? commandLine)
    {
        if (SettingsDirArgument(commandLine) is { } fromCommandLine)
        {
            return fromCommandLine;
        }

        var folder = Path.GetDirectoryName(executablePath);
        if (folder is not null && File.Exists(Path.Combine(folder, "doLocalConf.xml")) && !IsDirectlyInProgramFiles(folder))
        {
            return folder;
        }

        return AppDataFolder() ?? folder ?? string.Empty;
    }

    /// <summary>The value of <c>-settingsDir=</c> on a command line (quoted or not), or <see langword="null"/>.</summary>
    /// <param name="commandLine">The command line.</param>
    /// <returns>The folder.</returns>
    internal static string? SettingsDirArgument(string? commandLine)
    {
        const string Flag = "-settingsDir=";
        if (string.IsNullOrEmpty(commandLine))
        {
            return null;
        }

        int at = commandLine.IndexOf(Flag, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var rest = commandLine[(at + Flag.Length)..];
        string value;
        if (rest.StartsWith('"'))
        {
            // A quoted folder may contain spaces; an unterminated quote takes the rest of the line.
            int end = rest.IndexOf('"', 1);
            value = end > 0 ? rest[1..end] : rest[1..];
        }
        else
        {
            value = rest.Split([' ', '\t'], 2)[0];
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Builds the location: version and the first folder's backup settings.</summary>
    /// <param name="executable">The executable, or <see langword="null"/>.</param>
    /// <param name="folders">The settings folders.</param>
    /// <param name="detectedBy">How it was found.</param>
    /// <returns>The location.</returns>
    private static NotepadPlusPlusLocation Build(string? executable, IReadOnlyList<string> folders, string detectedBy)
    {
        string? version = null;
        if (executable is not null)
        {
            try
            {
                version = FileVersionInfo.GetVersionInfo(executable).ProductVersion?.Trim();
            }
            catch (FileNotFoundException)
            {
                // Uninstalled meanwhile: no version, still a location.
            }
        }

        var config = NotepadPlusPlusConfig.Defaults;
        if (folders.Count > 0)
        {
            var configPath = Path.Combine(folders[0], "config.xml");
            try
            {
                // Shared for writing and deleting: Notepad++ writes this file at exit, and must never be locked out by a reader.
                using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                config = NotepadPlusPlusConfig.Parse(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                // Missing or unreadable: Notepad++'s defaults (it writes the file at its first exit).
            }
        }

        return new NotepadPlusPlusLocation(executable, version, folders, detectedBy, config);
    }

    /// <summary>The running Notepad++ processes: executable path and command line (when readable).</summary>
    /// <returns>The copies.</returns>
    private static IEnumerable<(string Path, string? CommandLine)> RunningCopies()
    {
        foreach (var (window, processId) in NotepadPlusPlusClient.FindWindows())
        {
            _ = window;
            var path = Clipboard.SourceAppResolver.TryGetImagePath(processId);
            if (path is null || !string.Equals(Path.GetFileName(path), NotepadPlusPlusSources.ExecutableName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return (path, CommandLineOf(processId));
        }
    }

    /// <summary>A process's command line, or <see langword="null"/> when it cannot be read.</summary>
    /// <param name="processId">The process.</param>
    /// <returns>The command line.</returns>
    private static string? CommandLineOf(uint processId)
    {
        nint handle = Interop.NativeMethods.OpenProcess(Interop.NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            return CommandLineFromHandle(handle);
        }
        finally
        {
            Interop.NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>Reads a command line through <c>NtQueryInformationProcess</c> (the same layout <see cref="NotepadPlusPlusClient.CommandLine"/> reads).</summary>
    /// <param name="handle">A process handle with limited query access.</param>
    /// <returns>The command line, or <see langword="null"/>.</returns>
    private static unsafe string? CommandLineFromHandle(nint handle)
    {
        var bytes = new byte[8192];
        fixed (byte* pointer = bytes)
        {
            if (Interop.NativeMethods.NtQueryInformationProcess(handle, Interop.NativeMethods.ProcessCommandLineInformation, pointer, bytes.Length, out _) != 0)
            {
                return null;
            }

            ushort lengthBytes = *(ushort*)pointer;
            nint characters = *(nint*)(pointer + nint.Size);
            return lengthBytes == 0 || characters == 0 ? string.Empty : new string((char*)characters, 0, lengthBytes / 2);
        }
    }

    /// <summary>The installed Notepad++ (the installer's <c>HKLM\SOFTWARE\Notepad++</c> default value is its folder).</summary>
    /// <returns>The executable, or <see langword="null"/>.</returns>
    private static string? InstalledExecutable()
    {
        foreach (var view in (ReadOnlySpan<RegistryView>)[RegistryView.Registry64, RegistryView.Registry32])
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = root.OpenSubKey(@"SOFTWARE\Notepad++");
            if (key?.GetValue(null) is string folder && !string.IsNullOrWhiteSpace(folder))
            {
                var exe = Path.Combine(folder.Trim(), NotepadPlusPlusSources.ExecutableName);
                if (File.Exists(exe))
                {
                    return exe;
                }
            }
        }

        return null;
    }

    /// <summary><c>%APPDATA%\Notepad++</c>, or <see langword="null"/> when there is no roaming folder.</summary>
    /// <returns>The folder.</returns>
    private static string? AppDataFolder()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "Notepad++");
    }

    /// <summary>
    /// Whether a folder lies directly in a Program Files folder, where Notepad++ ignores <c>doLocalConf.xml</c> (its UAC rule).
    /// </summary>
    /// <param name="folder">The executable's folder.</param>
    /// <returns><see langword="true"/> when its parent is a Program Files folder.</returns>
    private static bool IsDirectlyInProgramFiles(string folder)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder));
        return parent is not null && new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        }.Any(p => !string.IsNullOrEmpty(p) && string.Equals(Path.TrimEndingDirectorySeparator(p), parent, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds a folder unless it is already listed (case-insensitive).</summary>
    /// <param name="folders">The list.</param>
    /// <param name="folder">The folder.</param>
    private static void AddOnce(List<string> folders, string folder)
    {
        if (!string.IsNullOrWhiteSpace(folder) && !folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            folders.Add(folder);
        }
    }
}
