using Microsoft.Win32;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Shell;

/// <summary>
/// Adds or removes a folder in the <b>user</b> <c>PATH</c> (<c>HKCU\Environment\Path</c>), so tools such as
/// <c>bclip</c> can be run by name from new terminals — the way VS Code's "Install code command" works.
/// </summary>
/// <remarks>
/// <para>
/// The value is read and written as <c>REG_EXPAND_SZ</c> with variables <b>unexpanded</b>
/// (<see cref="RegistryValueOptions.DoNotExpandEnvironmentNames"/>): expanding on read and writing back
/// would silently freeze every <c>%USERPROFILE%</c>-style entry the user has.
/// </para>
/// <para>
/// After a change, <c>WM_SETTINGCHANGE("Environment")</c> is broadcast so Explorer reloads the environment
/// for programs it starts next. Already-open terminals keep their old <c>PATH</c> until restarted.
/// </para>
/// </remarks>
public static class UserPath
{
    private const string EnvironmentKey = "Environment";
    private const string ValueName = "Path";

    /// <summary>
    /// Whether <paramref name="directory"/> is on the user PATH (case- and trailing-slash-insensitive).
    /// </summary>
    /// <param name="directory">Folder path.</param>
    /// <returns><see langword="true"/> when listed.</returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is null or blank.</exception>
    public static bool Contains(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return Entries(Read()).Any(entry => SamePath(entry, directory));
    }

    /// <summary>
    /// Appends <paramref name="directory"/> to the user PATH if it is not there yet.
    /// </summary>
    /// <param name="directory">Folder path.</param>
    /// <returns><see langword="true"/> when the PATH changed.</returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is null or blank.</exception>
    /// <exception cref="UnauthorizedAccessException">The environment key is not writable (policy).</exception>
    /// <exception cref="System.Security.SecurityException">The environment key is not accessible.</exception>
    public static bool Add(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!TryAppend(Read(), directory, out var updated))
        {
            return false;
        }

        Write(updated);
        return true;
    }

    /// <summary>
    /// Removes every entry equal to <paramref name="directory"/> from the user PATH.
    /// </summary>
    /// <param name="directory">Folder path.</param>
    /// <returns><see langword="true"/> when the PATH changed.</returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is null or blank.</exception>
    /// <exception cref="UnauthorizedAccessException">The environment key is not writable (policy).</exception>
    /// <exception cref="System.Security.SecurityException">The environment key is not accessible.</exception>
    public static bool Remove(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!TryRemove(Read(), directory, out var updated))
        {
            return false;
        }

        Write(updated);
        return true;
    }

    /// <summary>
    /// Computes a PATH value with <paramref name="directory"/> appended. Pure (no registry), so the rules —
    /// no duplicates (variables expanded only for the comparison), existing entries kept verbatim, empty
    /// entries dropped — are unit-tested without touching the real user PATH.
    /// </summary>
    /// <param name="value">Raw PATH value (may contain <c>%VAR%</c> entries; may be empty).</param>
    /// <param name="directory">Folder to add.</param>
    /// <param name="updated">The new value; <paramref name="value"/> unchanged when nothing was added.</param>
    /// <returns><see langword="true"/> when the folder was not listed and has been appended.</returns>
    internal static bool TryAppend(string value, string directory, out string updated)
    {
        var entries = Entries(value).ToList();
        if (entries.Any(entry => SamePath(entry, directory)))
        {
            updated = value;
            return false;
        }

        entries.Add(Path.TrimEndingDirectorySeparator(directory));
        updated = string.Join(';', entries);
        return true;
    }

    /// <summary>
    /// Computes a PATH value without any entry naming <paramref name="directory"/> (duplicates, other
    /// casing, a trailing slash or a <c>%VAR%</c> spelling included). Pure, like <see cref="TryAppend"/>.
    /// </summary>
    /// <param name="value">Raw PATH value.</param>
    /// <param name="directory">Folder to remove.</param>
    /// <param name="updated">The new value; <paramref name="value"/> unchanged when nothing matched.</param>
    /// <returns><see langword="true"/> when at least one entry was removed.</returns>
    internal static bool TryRemove(string value, string directory, out string updated)
    {
        var entries = Entries(value).ToList();
        if (entries.RemoveAll(entry => SamePath(entry, directory)) == 0)
        {
            updated = value;
            return false;
        }

        updated = string.Join(';', entries);
        return true;
    }

    /// <summary>Reads the raw (unexpanded) user PATH.</summary>
    /// <returns>The value, or empty when absent.</returns>
    private static string Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(EnvironmentKey);
        return key?.GetValue(ValueName, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
    }

    /// <summary>Writes the user PATH as REG_EXPAND_SZ and tells Explorer about it.</summary>
    /// <param name="value">New value.</param>
    private static void Write(string value)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(EnvironmentKey, writable: true))
        {
            key.SetValue(ValueName, value, RegistryValueKind.ExpandString);
        }

        // Best effort: a hung window must not block us (SMTO_ABORTIFHUNG, 2 s each).
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, 0, EnvironmentKey, SMTO_ABORTIFHUNG, 2000, out _);
    }

    /// <summary>Splits a PATH value into its non-empty entries.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Entries, trimmed.</returns>
    private static IEnumerable<string> Entries(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Compares a PATH entry (possibly with variables) with a folder.</summary>
    /// <param name="entry">PATH entry.</param>
    /// <param name="directory">Folder.</param>
    /// <returns>Whether they name the same folder.</returns>
    private static bool SamePath(string entry, string directory) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Environment.ExpandEnvironmentVariables(entry)),
            Path.TrimEndingDirectorySeparator(directory),
            StringComparison.OrdinalIgnoreCase);
}
