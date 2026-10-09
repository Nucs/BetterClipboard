using System.Runtime.InteropServices;
using BetterClipboard.Core.Updates;
using Microsoft.Win32;

namespace BetterClipboard.Windows.Updates;

/// <summary>
/// Reads from Windows what the update system must know about the running copy: which folder <c>install.ps1</c>
/// registered as installed, and which architecture this PC has (so an update downloads the right package).
/// </summary>
/// <remarks>
/// Read-only, and it never throws: a registry it cannot read simply proves nothing, and the copy then counts as
/// extracted by hand (<see cref="UpdateInstallKind.Portable"/>), which never replaces itself.
/// </remarks>
public static class InstalledCopyLocator
{
    /// <summary>
    /// The per-user Installed-apps entry <c>install.ps1</c> writes (its <c>$UninstallKeyPath</c>). A constant shared with
    /// the installer by convention: renaming it there orphans every installed copy's self-update.
    /// </summary>
    internal const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BetterClipboard";

    /// <summary>The value of that entry naming the install folder.</summary>
    internal const string InstallLocationValue = "InstallLocation";

    /// <summary>
    /// This PC's architecture in the spelling of the release packages: <c>arm64</c> or <c>x64</c>.
    /// </summary>
    /// <remarks>
    /// The operating system's architecture, not this process's: an x64 build running emulated on an ARM64 PC updates to
    /// the native ARM64 package, which is also what <c>install.ps1</c> picks on a fresh install.
    /// </remarks>
    public static string OsArchitecture =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>
    /// The folder the current user's Installed-apps entry for BetterClipboard names.
    /// </summary>
    /// <returns>
    /// The entry's <c>InstallLocation</c>, or <see langword="null"/> when there is no entry, it has no such text, or the
    /// registry cannot be read.
    /// </returns>
    public static string? ReadRegisteredInstallLocation() => ReadRegisteredInstallLocation(UninstallKeyPath);

    /// <summary>
    /// The folder an Installed-apps entry names; the key is a parameter so tests read a scratch key instead of the user's.
    /// </summary>
    /// <param name="keyPath">Path of the entry's key under <c>HKEY_CURRENT_USER</c>.</param>
    /// <returns>The entry's <c>InstallLocation</c>, or <see langword="null"/> as for <see cref="ReadRegisteredInstallLocation()"/>.</returns>
    internal static string? ReadRegisteredInstallLocation(string keyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(InstallLocationValue) is string location && !string.IsNullOrWhiteSpace(location)
                ? location.Trim()
                : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Works out how the running copy was installed, from the folder it runs in, the Installed-apps entry and this
    /// instance's <c>installer.json</c>.
    /// </summary>
    /// <remarks>
    /// An isolated instance (its own data directory) is judged by its own <c>installer.json</c> alone. The Installed-apps
    /// entry belongs to the user's regular instance: an isolated instance started from the installed folder must not
    /// count as "installed" through it, or its self-update would replace that folder under the regular instance, which
    /// the installer would then have to end by force (its graceful exit signal reaches only the instance that asked).
    /// </remarks>
    /// <param name="appDirectory">The folder of the running <c>BetterClipboard.exe</c>.</param>
    /// <param name="installerStatePath">This instance's <c>installer.json</c> (<see cref="Core.AppPaths.InstallerStatePath"/>).</param>
    /// <param name="isolatedInstance">
    /// Whether this instance runs with its own data directory (<see cref="Core.AppPaths.InstanceScope"/> is not
    /// <see langword="null"/>); the Installed-apps entry is then not consulted.
    /// </param>
    /// <returns>The kind of install (<see cref="InstalledCopy.Classify"/>).</returns>
    /// <exception cref="ArgumentException"><paramref name="appDirectory"/> is null or blank.</exception>
    public static UpdateInstallKind Detect(string appDirectory, string? installerStatePath, bool isolatedInstance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        return InstalledCopy.Classify(
            appDirectory,
            isolatedInstance ? null : ReadRegisteredInstallLocation(),
            InstalledCopy.ReadInstallerStateDirectory(installerStatePath));
    }
}
