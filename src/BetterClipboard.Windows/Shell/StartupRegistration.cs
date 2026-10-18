using BetterClipboard.Core.Startup;
using Microsoft.Win32;

namespace BetterClipboard.Windows.Shell;

/// <summary>
/// "Start with Windows" via the per-user <c>Run</c> key (no admin rights, no scheduled task), and what Windows holds
/// besides it: the all-users entry an installation for every user of the PC wrote, and the on/off marks of Windows'
/// startup apps list.
/// </summary>
/// <remarks>
/// <para>
/// The registered command passes <c>--background</c> so the app starts silently into the tray. The value
/// stores an absolute path: if the user moves the app folder, the entry goes stale — <see cref="IsEnabled"/>
/// reports it as disabled and toggling it on again rewrites the path.
/// </para>
/// <para>
/// <b>Who decides what.</b> The rules are <see cref="SignInStartup"/>'s (pure); this class only reads the registry into
/// <see cref="SignInEntries"/> (<see cref="ReadEntries()"/>) and applies a <see cref="SignInChange"/>
/// (<see cref="Apply(SignInChange, string)"/>). It never writes the all-users entry: that needs an administrator, and
/// only the installers write it.
/// </para>
/// <para>
/// <b>Windows' marks</b> (<c>Explorer\StartupApproved\Run</c>, written by Task Manager and Settings › Apps › Startup): a
/// 12-byte value per entry name whose first byte is even for "on" (2, 6) and odd for "off" (3, 7, and 9 when a policy
/// locked it). No value means "on". The user's marks are in <c>HKCU</c>, the all-users entries' marks in <c>HKLM</c>
/// only.
/// </para>
/// </remarks>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BetterClipboard";

    /// <summary>Where Windows keeps its on/off mark for each entry of the <c>Run</c> key next to it.</summary>
    private const string ApprovedRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>The command-line switch that starts BetterClipboard without showing a window.</summary>
    public const string BackgroundSwitch = SignInStartup.BackgroundSwitch;

    /// <summary>
    /// The switch the all-users entry adds: the process came from an installation for every user and asks first whether
    /// this user wants it (<see cref="SignInStartup.ShouldContinueFromAllUsersEntry"/>).
    /// </summary>
    public const string AllUsersSwitch = SignInStartup.AllUsersSwitch;

    /// <summary>
    /// Whether BetterClipboard is registered to start at sign-in for <paramref name="executablePath"/>.
    /// </summary>
    /// <remarks>
    /// Only the user's own entry, as written; neither the all-users entry nor Windows' marks count here. What the switch
    /// shows comes from <see cref="SignInStartup.StartsThisCopy"/> over <see cref="ReadEntries()"/>.
    /// </remarks>
    /// <param name="executablePath">Path of the running executable.</param>
    /// <returns><see langword="true"/> when the Run value exists and points at this executable.</returns>
    public static bool IsEnabled(string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string command &&
               command.Contains(executablePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Adds or removes the Run entry.
    /// </summary>
    /// <param name="enabled">Whether to start at sign-in.</param>
    /// <param name="executablePath">Path of the executable to start.</param>
    /// <exception cref="UnauthorizedAccessException">The Run key is locked down by policy.</exception>
    /// <exception cref="System.Security.SecurityException">The user lacks registry permissions.</exception>
    public static void SetEnabled(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{executablePath}\" {BackgroundSwitch}", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Reads what Windows holds for starting BetterClipboard at sign-in: the user's entry and its mark from
    /// <c>HKCU</c>, the all-users entry and its mark from <c>HKLM</c> (64-bit view, where both installers write).
    /// </summary>
    /// <returns>The entries; <see cref="SignInEntries.None"/>'s parts for whatever could not be read.</returns>
    /// <remarks>Never throws: a key that cannot be read counts as holding nothing, like a missing one.</remarks>
    public static SignInEntries ReadEntries()
    {
        RegistryKey? machine = null;
        try
        {
            machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Without the PC's registry only the user's own entry is known; that is still a usable answer.
        }

        try
        {
            return ReadEntries(Registry.CurrentUser, machine, File.Exists);
        }
        finally
        {
            machine?.Dispose();
        }
    }

    /// <summary>
    /// Reads the entries from the given roots. Tests pass scratch keys for both, so the user's and the PC's real
    /// entries are never read or written.
    /// </summary>
    /// <param name="userRoot">The key that holds the user's <c>Software\…\Run</c> (<c>HKEY_CURRENT_USER</c>).</param>
    /// <param name="machineRoot">The key that holds the all-users <c>Software\…\Run</c> (<c>HKLM</c>), or <see langword="null"/> when it could not be opened.</param>
    /// <param name="fileExists">Whether a program path exists (<see cref="File.Exists(string)"/>; a fake in tests).</param>
    /// <returns>The entries.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="userRoot"/> or <paramref name="fileExists"/> is <see langword="null"/>.</exception>
    internal static SignInEntries ReadEntries(RegistryKey userRoot, RegistryKey? machineRoot, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(userRoot);
        ArgumentNullException.ThrowIfNull(fileExists);
        var userCommand = ReadCommand(userRoot);
        var machineCommand = machineRoot is null ? null : ReadCommand(machineRoot);
        return new SignInEntries(
            userCommand,
            UserTargetExists: TargetExists(userCommand, fileExists),
            UserDisabledInWindows: IsTurnedOffInWindows(userRoot),
            machineCommand,
            AllUsersDisabledInWindows: machineRoot is not null && IsTurnedOffInWindows(machineRoot));
    }

    /// <summary>
    /// Applies what one flip of the switch changes (<see cref="SignInStartup.Plan"/>) to the user's registry: writes or
    /// removes the user's entry, and removes Windows' "off" mark for it when asked.
    /// </summary>
    /// <param name="change">The change.</param>
    /// <param name="executablePath">Path of the running executable (what a written entry starts).</param>
    /// <exception cref="ArgumentNullException"><paramref name="change"/> is <see langword="null"/>.</exception>
    /// <exception cref="UnauthorizedAccessException">The Run key is locked down by policy.</exception>
    /// <exception cref="System.Security.SecurityException">The user lacks registry permissions.</exception>
    /// <exception cref="IOException">The registry refused the write (a key marked for deletion).</exception>
    /// <remarks>
    /// <c>AppSettings.SkipAllUsersStartup</c> (the change's third part) is the caller's to save: it lives in the settings
    /// file, not the registry.
    /// </remarks>
    public static void Apply(SignInChange change, string executablePath) => Apply(change, executablePath, Registry.CurrentUser);

    /// <summary>
    /// Applies a change under the given user root; tests pass a scratch key.
    /// </summary>
    /// <param name="change">The change.</param>
    /// <param name="executablePath">Path of the running executable.</param>
    /// <param name="userRoot">The key that holds the user's <c>Software\…\Run</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="change"/> or <paramref name="userRoot"/> is <see langword="null"/>.</exception>
    /// <exception cref="UnauthorizedAccessException">A key is locked down by policy.</exception>
    /// <exception cref="System.Security.SecurityException">The user lacks registry permissions.</exception>
    /// <exception cref="IOException">The registry refused the write.</exception>
    internal static void Apply(SignInChange change, string executablePath, RegistryKey userRoot)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(userRoot);
        switch (change.UserEntry)
        {
            case UserEntryChange.Write:
                // CreateSubKey opens an existing key as it is: never "recreate" the Run key, which holds every other app's
                // start-with-Windows entry.
                using (var run = userRoot.CreateSubKey(RunKeyPath, writable: true))
                {
                    run.SetValue(ValueName, SignInStartup.UserCommandFor(executablePath), RegistryValueKind.String);
                }

                break;

            case UserEntryChange.Remove:
                using (var run = userRoot.OpenSubKey(RunKeyPath, writable: true))
                {
                    run?.DeleteValue(ValueName, throwOnMissingValue: false);
                }

                break;
        }

        if (change.ClearUserDisabled)
        {
            // No mark means "on", which is also what Task Manager shows for an entry it has not seen before.
            using var approved = userRoot.OpenSubKey(ApprovedRunKeyPath, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Whether one of Windows' marks says "off": a binary value whose first byte is odd.
    /// </summary>
    /// <param name="mark">The <c>StartupApproved\Run</c> value's data, or <see langword="null"/> for none.</param>
    /// <returns><see langword="true"/> for an "off" mark; no mark, an "on" mark and data of another kind are "on".</returns>
    internal static bool IsOffMark(object? mark) => mark is byte[] { Length: > 0 } bytes && (bytes[0] & 1) == 1;

    /// <summary>Reads BetterClipboard's <c>Run</c> value under a root.</summary>
    /// <param name="root">A registry root holding <c>Software\…\Run</c>.</param>
    /// <returns>The command, or <see langword="null"/> when there is none, it is not text, or the key cannot be read.</returns>
    private static string? ReadCommand(RegistryKey root)
    {
        try
        {
            using var key = root.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command) ? command : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    /// <summary>Whether Windows' mark for BetterClipboard's entry under a root says "off".</summary>
    /// <param name="root">A registry root holding <c>Software\…\Explorer\StartupApproved\Run</c>.</param>
    /// <returns><see langword="true"/> for an "off" mark; <see langword="false"/> otherwise or when it cannot be read.</returns>
    private static bool IsTurnedOffInWindows(RegistryKey root)
    {
        try
        {
            using var key = root.OpenSubKey(ApprovedRunKeyPath);
            return IsOffMark(key?.GetValue(ValueName));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    /// <summary>Whether the program an entry's command names exists, with environment variables expanded as Windows does.</summary>
    /// <param name="command">The entry's command, or <see langword="null"/>.</param>
    /// <param name="fileExists">The existence check.</param>
    /// <returns><see langword="true"/> when the command names a program that exists.</returns>
    private static bool TargetExists(string? command, Func<string, bool> fileExists)
    {
        var target = SignInStartup.TargetOf(command);
        if (target is null)
        {
            return false;
        }

        try
        {
            return fileExists(Environment.ExpandEnvironmentVariables(target));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            // A path Windows could not start either.
            return false;
        }
    }
}
