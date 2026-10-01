using Microsoft.Win32;

namespace BetterClipboard.Windows.Integrations;

/// <summary>
/// Finds an installed voidtools Everything that is not necessarily running — so the Everything tab can show the
/// last saved run history and offer to start it.
/// </summary>
/// <remarks>
/// <para>
/// Reads the uninstall entries (HKLM 64- and 32-bit views, HKCU) whose display name starts with "Everything": the
/// exe installers write the key <c>Everything</c> (1.4, 1.5 beta) or <c>Everything 1.5a</c>, the MSI a
/// product-code key with a display name like <c>Everything 1.4.1.1032 (x64)</c>. The executable is looked for in
/// the install location, the display icon and the uninstaller's folder, and must pass
/// <see cref="EverythingOwnerVerifier.VerifyFile"/> — a lookalike entry ("Everything Toolbar", another vendor's
/// app) does not count.
/// </para>
/// <para>
/// Portable copies (and package managers that unpack them) have no entry; they are found while they run, through
/// their IPC window. With <see cref="EverythingClient.InstanceVariable"/> set (development, tests) nothing is read
/// from the registry: the user's own install must stay out of an isolated test.
/// </para>
/// </remarks>
public static class EverythingLocator
{
    /// <summary>The uninstall registry path below HKLM/HKCU.</summary>
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// Looks for an installed Everything. Blocking (registry reads and, once per Everything build, a signature
    /// check): call it off the UI thread.
    /// </summary>
    /// <returns>The installation, or <see cref="EverythingInstallation.NotFound"/>; never throws.</returns>
    public static EverythingInstallation Locate()
    {
        if (EverythingClient.IsInstanceOverridden)
        {
            return EverythingInstallation.NotFound with { DetectedBy = $"not looked for ({EverythingClient.InstanceVariable} is set)" };
        }

        try
        {
            return Resolve(ReadUninstallEntries(), File.Exists, path => EverythingOwnerVerifier.VerifyFile(path).IsTrusted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return EverythingInstallation.NotFound with { DetectedBy = "the registry could not be read" };
        }
    }

    /// <summary>
    /// Picks the installation from uninstall entries (pure: file checks injected, for tests).
    /// </summary>
    /// <param name="entries">Uninstall entries, in registry order.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="isTrusted">Whether an executable is a voidtools-signed Everything.</param>
    /// <returns>The first entry whose executable exists and is trusted, or <see cref="EverythingInstallation.NotFound"/>.</returns>
    internal static EverythingInstallation Resolve(IEnumerable<UninstallEntry> entries, Func<string, bool> fileExists, Func<string, bool> isTrusted)
    {
        foreach (var entry in entries)
        {
            if (entry.DisplayName is not { } name || !name.StartsWith("Everything", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var candidate in Candidates(entry))
            {
                if (fileExists(candidate) && isTrusted(candidate))
                {
                    return new EverythingInstallation(true, candidate, entry.DisplayVersion, $"installed ({entry.KeyName})");
                }
            }
        }

        return EverythingInstallation.NotFound;
    }

    /// <summary>The executable paths an entry points at: install folder, display icon, uninstaller folder.</summary>
    /// <param name="entry">The uninstall entry.</param>
    /// <returns>Candidate paths (unchecked), most likely first.</returns>
    private static IEnumerable<string> Candidates(UninstallEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.InstallLocation))
        {
            yield return Path.Combine(Unquote(entry.InstallLocation), "Everything.exe");
        }

        if (!string.IsNullOrWhiteSpace(entry.DisplayIcon))
        {
            // "C:\Program Files\Everything\Everything.exe,0" or "\"…\Everything.exe\",0": the icon index is not part
            // of the path. It goes first: a quoted path ends at its closing quote, before the index.
            var icon = entry.DisplayIcon.Trim();
            int comma = icon.LastIndexOf(',');
            if (comma > 0 && int.TryParse(icon.AsSpan(comma + 1).Trim(), out _))
            {
                icon = icon[..comma];
            }

            icon = Unquote(icon);
            if (icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                yield return icon;
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.UninstallString))
        {
            // "C:\Program Files\Everything\Uninstall.exe" (possibly quoted, possibly with arguments).
            var uninstaller = entry.UninstallString.Trim();
            var exe = uninstaller.StartsWith('"') ? uninstaller[1..].Split('"')[0] : uninstaller.Split(' ')[0];
            if (Path.GetDirectoryName(exe) is { Length: > 0 } folder)
            {
                yield return Path.Combine(folder, "Everything.exe");
            }
        }
    }

    /// <summary>Removes surrounding quotes and whitespace.</summary>
    /// <param name="value">A registry value.</param>
    /// <returns>The unquoted value.</returns>
    private static string Unquote(string value) => value.Trim().Trim('"').Trim();

    /// <summary>Reads every uninstall entry from HKLM (both views) and HKCU.</summary>
    /// <returns>The entries.</returns>
    private static List<UninstallEntry> ReadUninstallEntries()
    {
        var entries = new List<UninstallEntry>();
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Registry64) })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallPath);
            if (uninstall is null)
            {
                continue;
            }

            foreach (var keyName in uninstall.GetSubKeyNames())
            {
                using var key = uninstall.OpenSubKey(keyName);
                if (key?.GetValue("DisplayName") is not string displayName || !displayName.StartsWith("Everything", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                entries.Add(new UninstallEntry(
                    keyName,
                    displayName,
                    key.GetValue("DisplayVersion") as string,
                    key.GetValue("InstallLocation") as string,
                    key.GetValue("DisplayIcon") as string,
                    key.GetValue("UninstallString") as string));
            }
        }

        return entries;
    }

    /// <summary>One uninstall entry, as far as finding Everything needs it.</summary>
    /// <param name="KeyName">The key's name (e.g. <c>Everything</c> or an MSI product code).</param>
    /// <param name="DisplayName">Its display name.</param>
    /// <param name="DisplayVersion">Its version.</param>
    /// <param name="InstallLocation">Its install folder.</param>
    /// <param name="DisplayIcon">Its icon path (often the executable, with <c>,0</c>).</param>
    /// <param name="UninstallString">Its uninstaller command line.</param>
    internal sealed record UninstallEntry(string KeyName, string? DisplayName, string? DisplayVersion, string? InstallLocation, string? DisplayIcon, string? UninstallString);
}

/// <summary>An installed Everything, as found by <see cref="EverythingLocator.Locate"/>.</summary>
/// <param name="IsInstalled">Whether a voidtools-signed Everything is installed.</param>
/// <param name="ExecutablePath">Its <c>Everything.exe</c>.</param>
/// <param name="Version">The version its uninstall entry names.</param>
/// <param name="DetectedBy">How it was found (or why not), for the Settings status.</param>
public sealed record EverythingInstallation(bool IsInstalled, string? ExecutablePath, string? Version, string DetectedBy)
{
    /// <summary>No installed Everything was found.</summary>
    public static EverythingInstallation NotFound { get; } = new(false, null, null, "not installed");
}
