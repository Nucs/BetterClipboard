using System.ComponentModel;
using System.Diagnostics;
using BetterClipboard.Core.Editions;
using BetterClipboard.Core.Updates;
using BetterClipboard.Windows.Updates;
using Microsoft.Win32;

namespace BetterClipboard.Windows.Store;

/// <summary>
/// Finds the other copies of BetterClipboard on this PC, for the Microsoft Store edition: the copy this user installed
/// with the installer, the copy for every user, the Chocolatey copy, and a copy that merely runs.
/// </summary>
/// <remarks>
/// <para>
/// Read-only, and it never throws: a registry or a process it cannot read simply finds nothing. Reads from inside the
/// package see the real registry (measured, CLAUDE.md §2.47), so the records are those the other editions wrote.
/// </para>
/// <para>
/// <b>The records</b> are the ones the app's updater already trusts (<see cref="InstalledCopyLocator"/>): the
/// Installed-apps entry of this user and of the PC, both written by <c>install.ps1</c>, and Chocolatey's folder shape
/// (<see cref="InstalledCopy.IsChocolateyFolder"/>). A copy without a record is only seen while it holds the regular
/// instance's lock, and is then asked to close, never ended.
/// </para>
/// <para>
/// An isolated instance (its own data folder) must not call this for a decision: test copies and the owner's dev copy
/// would otherwise "find" the installed app and offer to remove it. The caller checks that first.
/// </para>
/// </remarks>
public static class OtherEditionLocator
{
    /// <summary>The version an Installed-apps entry states.</summary>
    private const string DisplayVersionValue = "DisplayVersion";

    /// <summary>The app's program name, as install folders hold it.</summary>
    private const string ExeName = "BetterClipboard.exe";

    /// <summary>
    /// Looks for other copies.
    /// </summary>
    /// <param name="ownDirectory">The folder this process runs from (never reported).</param>
    /// <param name="regularInstanceHeldByAnother">
    /// Whether another process holds the regular instance's lock right now (this process is not the primary instance):
    /// then a copy runs, and when no installed copy accounts for it, it is reported as
    /// <see cref="OtherEditionKind.Unregistered"/>.
    /// </param>
    /// <returns>The copies found: at most one per installed kind, plus at most one unregistered running copy.</returns>
    /// <exception cref="ArgumentException"><paramref name="ownDirectory"/> is null or blank.</exception>
    public static IReadOnlyList<OtherEdition> Find(string ownDirectory, bool regularInstanceHeldByAnother)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownDirectory);
        var running = RunningCopies(ownDirectory);
        var found = new List<OtherEdition>();

        if (InstalledCopyLocator.ReadRegisteredInstallLocation() is { } userFolder && !InstalledCopy.SameFolder(userFolder, ownDirectory))
        {
            found.Add(new OtherEdition(OtherEditionKind.Installer, userFolder, ReadUserVersion() ?? FileVersion(userFolder), RunsFrom(running, userFolder)));
        }

        if (InstalledCopyLocator.ReadMachineInstallLocation() is { } machineFolder && !InstalledCopy.SameFolder(machineFolder, ownDirectory))
        {
            found.Add(new OtherEdition(OtherEditionKind.AllUsers, machineFolder, ReadMachineVersion() ?? FileVersion(machineFolder), RunsFrom(running, machineFolder)));
        }

        if (ChocolateyFolder() is { } chocolateyFolder)
        {
            found.Add(new OtherEdition(OtherEditionKind.Chocolatey, chocolateyFolder, FileVersion(chocolateyFolder), RunsFrom(running, chocolateyFolder)));
        }

        // A copy holds the lock, and none of the installed copies runs: it is one nobody recorded.
        if (regularInstanceHeldByAnother && !found.Any(e => e.IsRunning))
        {
            string? folder = running.FirstOrDefault(r => !found.Any(e => InstalledCopy.SameFolder(e.Directory, r)));
            found.Add(new OtherEdition(OtherEditionKind.Unregistered, folder ?? string.Empty, folder is null ? null : FileVersion(folder), IsRunning: true));
        }

        return found;
    }

    /// <summary>
    /// Whether an installed copy is still there: its record still names its folder, or its program is still in it.
    /// Asked after an uninstall to prove it.
    /// </summary>
    /// <param name="edition">The copy as it was found.</param>
    /// <returns><see langword="true"/> while anything of it is left; an unregistered copy is never "installed".</returns>
    /// <exception cref="ArgumentNullException"><paramref name="edition"/> is <see langword="null"/>.</exception>
    public static bool IsStillInstalled(OtherEdition edition)
    {
        ArgumentNullException.ThrowIfNull(edition);
        bool programLeft = edition.Directory.Length > 0 && File.Exists(Path.Combine(edition.Directory, ExeName));
        return edition.Kind switch
        {
            OtherEditionKind.Installer => programLeft || InstalledCopy.SameFolder(edition.Directory, InstalledCopyLocator.ReadRegisteredInstallLocation()),
            OtherEditionKind.AllUsers => programLeft || InstalledCopy.SameFolder(edition.Directory, InstalledCopyLocator.ReadMachineInstallLocation()),
            OtherEditionKind.Chocolatey => programLeft,
            _ => false,
        };
    }

    /// <summary>The folders of the BetterClipboard processes of this session that do not run from this copy's folder.</summary>
    /// <param name="ownDirectory">This copy's folder.</param>
    /// <returns>The folders, each once; empty when none runs or none can be read.</returns>
    private static List<string> RunningCopies(string ownDirectory)
    {
        var folders = new List<string>();
        int session;
        using (var self = Process.GetCurrentProcess())
        {
            session = self.SessionId;
        }

        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != session || process.MainModule?.FileName is not { } path)
                    {
                        continue;
                    }

                    string? folder = Path.GetDirectoryName(path);
                    if (folder is not null && !InstalledCopy.SameFolder(folder, ownDirectory) && !folders.Any(f => InstalledCopy.SameFolder(f, folder)))
                    {
                        folders.Add(folder);
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // A copy that runs as administrator cannot be asked for its path, and one that just ended has none.
                }
            }
        }

        return folders;
    }

    /// <summary>Whether one of the running copies runs from a folder.</summary>
    /// <param name="running">The running copies' folders.</param>
    /// <param name="folder">The folder asked about.</param>
    /// <returns><see langword="true"/> when one does.</returns>
    private static bool RunsFrom(List<string> running, string folder) => running.Any(r => InstalledCopy.SameFolder(r, folder));

    /// <summary>The Chocolatey package's app folder, when its program is there.</summary>
    /// <returns>The folder, or <see langword="null"/>.</returns>
    private static string? ChocolateyFolder()
    {
        // ChocolateyInstall is set for every process once Chocolatey is installed; the default root covers a shell that
        // was open before.
        string?[] roots =
        [
            Environment.GetEnvironmentVariable("ChocolateyInstall"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey"),
        ];
        foreach (string? root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            try
            {
                string folder = Path.Combine(root, "lib", "betterclipboard", "tools", "app");
                if (File.Exists(Path.Combine(folder, ExeName)))
                {
                    return folder;
                }
            }
            catch (ArgumentException)
            {
                // A root that is not a path names no folder.
            }
        }

        return null;
    }

    /// <summary>The version this user's Installed-apps entry states.</summary>
    /// <returns>The version text, or <see langword="null"/>.</returns>
    private static string? ReadUserVersion()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InstalledCopyLocator.UninstallKeyPath);
            return key?.GetValue(DisplayVersionValue) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>The version the PC's Installed-apps entry states (64-bit view, where the installer writes it).</summary>
    /// <returns>The version text, or <see langword="null"/>.</returns>
    private static string? ReadMachineVersion()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(InstalledCopyLocator.UninstallKeyPath);
            return key?.GetValue(DisplayVersionValue) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>The product version of the program in a folder, without the commit suffix.</summary>
    /// <param name="folder">The folder.</param>
    /// <returns>The version, or <see langword="null"/> when there is no program or it states none.</returns>
    private static string? FileVersion(string folder)
    {
        try
        {
            string exe = Path.Combine(folder, ExeName);
            return File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0] : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
