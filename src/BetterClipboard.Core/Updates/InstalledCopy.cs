using System.Text.Json;

namespace BetterClipboard.Core.Updates;

/// <summary>How the running copy of BetterClipboard got onto this PC — which decides how it may be updated.</summary>
public enum UpdateInstallKind
{
    /// <summary>
    /// Extracted or built by hand: nothing recorded that an installer owns its folder. It never replaces itself, because
    /// its folder may hold other files (a zip extracted into a shared tools folder), and an update swaps the whole
    /// folder. The dialog points at the download page instead.
    /// </summary>
    Portable = 0,

    /// <summary>
    /// Installed by <c>install.ps1</c>, which owns the folder (it created it, and re-running it swaps it): this copy
    /// can update itself the same way.
    /// </summary>
    Installer = 1,

    /// <summary>
    /// Installed by the Chocolatey package. Chocolatey must stay the one that changes its files, so the app neither
    /// updates itself nor checks by itself; the dialog names <c>choco upgrade betterclipboard</c>.
    /// </summary>
    Chocolatey = 2,

    /// <summary>
    /// Installed for every user of the PC by <c>install.ps1 -AllUsers</c> (by default into Program Files), which the
    /// PC's Installed-apps entry records. Only an administrator may change its files, and the app runs as a user who may
    /// not be one, so it neither replaces itself nor checks by itself, like a Chocolatey copy: the administrator who
    /// installed it for everyone updates it, and the dialog gives the command for that
    /// (<see cref="UpdateSource.AllUsersInstallCommand"/>, to run in PowerShell as administrator).
    /// </summary>
    AllUsers = 3,
}

/// <summary>
/// Works out how the running copy was installed (<see cref="UpdateInstallKind"/>), from the folder it runs in and from
/// what <c>install.ps1</c> recorded. Pure apart from <see cref="ReadInstallerStateDirectory"/>, which reads one small file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the proof must be the installer's own record.</b> A self-update replaces the app's whole folder
/// (<c>install.ps1</c> renames it aside and deletes it). That is safe only for a folder the installer made. A copy
/// extracted by hand has <c>install.ps1</c> next to its exe too (the zip ships it), so that file proves nothing.
/// </para>
/// <para>
/// <b>The two records of a copy installed for one user:</b> the Installed-apps entry's <c>InstallLocation</c> (HKCU,
/// written by <c>install.ps1</c>), and <c>installer.json</c> in the data directory (its <c>installDir</c>). Either one
/// naming the running folder is enough: an isolated instance has its own data directory and so its own
/// <c>installer.json</c>, which lets a test install and update a scratch copy without touching the registry.
/// </para>
/// <para>
/// <b>A copy installed for every user</b> (<c>install.ps1 -AllUsers</c>) is recognized by the PC's Installed-apps entry
/// (HKLM). It is checked before the per-user records, because the administrator who installed it may have an
/// <c>installer.json</c> of their own that names the same folder: read first, that record would make the copy try to
/// replace a Program Files folder it may not write.
/// </para>
/// </remarks>
public static class InstalledCopy
{
    /// <summary>The tail of the folder a Chocolatey install runs from: <c>&lt;root&gt;\lib\betterclipboard\tools\app</c>.</summary>
    private static readonly string[] ChocolateyTail = ["lib", "betterclipboard", "tools", "app"];

    /// <summary>
    /// Classifies the running copy.
    /// </summary>
    /// <param name="appDirectory">The folder of the running <c>BetterClipboard.exe</c>.</param>
    /// <param name="registeredInstallLocation">
    /// <c>InstallLocation</c> of the user's Installed-apps entry for BetterClipboard, or <see langword="null"/> when
    /// there is none.
    /// </param>
    /// <param name="installerStateDirectory">
    /// <c>installDir</c> from this instance's <c>installer.json</c> (<see cref="ReadInstallerStateDirectory"/>), or
    /// <see langword="null"/> when there is none.
    /// </param>
    /// <param name="machineInstallLocation">
    /// <c>InstallLocation</c> of the PC's Installed-apps entry for BetterClipboard (written by <c>install.ps1 -AllUsers</c>),
    /// or <see langword="null"/> when there is none or it was not read (an isolated instance).
    /// </param>
    /// <returns>
    /// <see cref="UpdateInstallKind.Chocolatey"/> for a folder of Chocolatey's shape (checked first: its package may be
    /// installed next to an installer copy); <see cref="UpdateInstallKind.AllUsers"/> when the PC's entry names
    /// <paramref name="appDirectory"/>; <see cref="UpdateInstallKind.Installer"/> when either per-user record names it;
    /// otherwise <see cref="UpdateInstallKind.Portable"/>.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="appDirectory"/> is null or blank.</exception>
    public static UpdateInstallKind Classify(string appDirectory, string? registeredInstallLocation, string? installerStateDirectory, string? machineInstallLocation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        if (IsChocolateyFolder(appDirectory))
        {
            return UpdateInstallKind.Chocolatey;
        }

        // Before the per-user records: the installing administrator's own installer.json may name this folder too.
        if (SameFolder(appDirectory, machineInstallLocation))
        {
            return UpdateInstallKind.AllUsers;
        }

        return SameFolder(appDirectory, registeredInstallLocation) || SameFolder(appDirectory, installerStateDirectory)
            ? UpdateInstallKind.Installer
            : UpdateInstallKind.Portable;
    }

    /// <summary>
    /// Whether a folder has the shape of a Chocolatey install of this package (<c>…\lib\betterclipboard\tools\app</c>),
    /// wherever Chocolatey's root is.
    /// </summary>
    /// <param name="appDirectory">The folder of the running exe.</param>
    /// <returns><see langword="true"/> for a Chocolatey copy.</returns>
    public static bool IsChocolateyFolder(string appDirectory)
    {
        if (string.IsNullOrWhiteSpace(appDirectory))
        {
            return false;
        }

        var parts = appDirectory.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= ChocolateyTail.Length)
        {
            return false;
        }

        for (int i = 0; i < ChocolateyTail.Length; i++)
        {
            if (!string.Equals(parts[parts.Length - ChocolateyTail.Length + i], ChocolateyTail[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads <c>installDir</c> from an <c>installer.json</c> (written by <c>install.ps1</c> into the data directory).
    /// </summary>
    /// <param name="installerStatePath">Path of the file.</param>
    /// <returns>
    /// The recorded install folder, or <see langword="null"/> when the file is missing, unreadable, not JSON or has no
    /// such member. Never throws: an unreadable record simply proves nothing.
    /// </returns>
    public static string? ReadInstallerStateDirectory(string? installerStatePath)
    {
        if (string.IsNullOrWhiteSpace(installerStatePath))
        {
            return null;
        }

        try
        {
            if (!File.Exists(installerStatePath))
            {
                return null;
            }

            // ReadAllText drops the byte order mark Windows PowerShell 5.1 writes with -Encoding UTF8.
            using var document = JsonDocument.Parse(File.ReadAllText(installerStatePath));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("installDir", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether two paths name the same folder: compared as full paths without a trailing separator, ignoring case
    /// (Windows' rule for local paths).
    /// </summary>
    /// <param name="folder">One folder.</param>
    /// <param name="other">The other; <see langword="null"/> or blank never matches.</param>
    /// <returns><see langword="true"/> when they are the same folder.</returns>
    public static bool SameFolder(string folder, string? other)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(other))
        {
            return false;
        }

        try
        {
            return string.Equals(Normalize(folder), Normalize(other), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            // A record holding something that is not a path names no folder.
            return false;
        }

        static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
    }
}
