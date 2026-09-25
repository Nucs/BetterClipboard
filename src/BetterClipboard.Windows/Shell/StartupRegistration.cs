using Microsoft.Win32;

namespace BetterClipboard.Windows.Shell;

/// <summary>
/// "Start with Windows" via the per-user <c>Run</c> key (no admin rights, no scheduled task).
/// </summary>
/// <remarks>
/// The registered command passes <c>--background</c> so the app starts silently into the tray. The value
/// stores an absolute path: if the user moves the app folder, the entry goes stale — <see cref="IsEnabled"/>
/// reports it as disabled and toggling it on again rewrites the path.
/// </remarks>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BetterClipboard";

    /// <summary>The command-line switch that starts BetterClipboard without showing a window.</summary>
    public const string BackgroundSwitch = "--background";

    /// <summary>
    /// Whether BetterClipboard is registered to start at sign-in for <paramref name="executablePath"/>.
    /// </summary>
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
}
