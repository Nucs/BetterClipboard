using System.Diagnostics;
using BetterClipboard.Core.Diagnostics;
using Microsoft.Win32;

namespace BetterClipboard.Windows.Shell;

/// <summary>
/// Reads/writes the Windows-side switches that matter when replacing Win+V (see CLAUDE.md §1.5):
/// the built-in clipboard history toggle and Explorer's <c>DisabledHotkeys</c> list.
/// </summary>
public static class WindowsClipboardSettings
{
    private const string ClipboardKeyPath = @"Software\Microsoft\Clipboard";
    private const string ExplorerAdvancedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string PolicyKeyPath = @"SOFTWARE\Policies\Microsoft\Windows\System";

    /// <summary>
    /// Whether Windows' own clipboard history is enabled for this user (<c>EnableClipboardHistory</c>).
    /// </summary>
    /// <returns><see langword="true"/>/<see langword="false"/>, or <see langword="null"/> when never configured (Windows default: off).</returns>
    public static bool? IsWindowsHistoryEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ClipboardKeyPath);
        return key?.GetValue("EnableClipboardHistory") is int value ? value != 0 : null;
    }

    /// <summary>
    /// Whether group policy disables Windows clipboard history (<c>AllowClipboardHistory = 0</c>).
    /// </summary>
    /// <returns><see langword="true"/> when blocked by policy.</returns>
    public static bool IsWindowsHistoryBlockedByPolicy()
    {
        using var machine = Registry.LocalMachine.OpenSubKey(PolicyKeyPath);
        using var user = Registry.CurrentUser.OpenSubKey(PolicyKeyPath);
        return machine?.GetValue("AllowClipboardHistory") is 0 || user?.GetValue("AllowClipboardHistory") is 0;
    }

    /// <summary>
    /// Turns Windows' own clipboard history on or off (the same value the Settings app writes).
    /// </summary>
    /// <remarks>
    /// Turning it off stops the Windows service from keeping its own copy of everything you copy (a
    /// duplicate once BetterClipboard records it) and makes the WinRT history import unavailable — but
    /// the on-disk pins are still importable.
    /// </remarks>
    /// <param name="enabled">The new state.</param>
    /// <exception cref="UnauthorizedAccessException">The key is not writable.</exception>
    public static void SetWindowsHistoryEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ClipboardKeyPath, writable: true);
        key.SetValue("EnableClipboardHistory", enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    /// <summary>
    /// Whether Explorer is told not to register <c>Win+V</c> (<c>DisabledHotkeys</c> contains <c>V</c>).
    /// </summary>
    /// <returns><see langword="true"/> when Win+V is released by Explorer (after its next restart).</returns>
    public static bool IsWinVReleasedByExplorer()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ExplorerAdvancedKeyPath);
        return key?.GetValue("DisabledHotkeys") is string value && value.Contains('V', StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Adds or removes <c>V</c> in Explorer's <c>DisabledHotkeys</c>, preserving other letters the user set.
    /// Takes effect only after Explorer restarts (see <see cref="RestartExplorer"/>) or at next sign-in.
    /// </summary>
    /// <remarks>
    /// With Win+V released, BetterClipboard can use plain <c>RegisterHotKey</c> instead of a keyboard hook,
    /// and Win+V keeps working for it even while its hook would be unavailable (e.g. elevated windows
    /// focused). The downside: while BetterClipboard is not running, Win+V does nothing at all.
    /// </remarks>
    /// <param name="release"><see langword="true"/> to release Win+V from Explorer.</param>
    /// <exception cref="UnauthorizedAccessException">The key is not writable.</exception>
    public static void SetWinVReleasedByExplorer(bool release)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvancedKeyPath, writable: true);
        var current = key.GetValue("DisabledHotkeys") as string ?? string.Empty;
        var without = new string(current.Where(c => char.ToUpperInvariant(c) != 'V').ToArray());
        var next = release ? without + "V" : without;
        if (next.Length == 0)
        {
            key.DeleteValue("DisabledHotkeys", throwOnMissingValue: false);
        }
        else
        {
            key.SetValue("DisabledHotkeys", next, RegistryValueKind.String);
        }
    }

    /// <summary>
    /// Restarts Explorer for the current session so hotkey changes apply. Disruptive (taskbar and open
    /// File Explorer windows disappear for a moment) — only call after explicit user confirmation.
    /// </summary>
    /// <remarks>
    /// Winlogon usually auto-restarts the shell after it is killed (<c>AutoRestartShell</c>); a new
    /// explorer.exe is started only if none came back, to avoid opening a stray File Explorer window.
    /// </remarks>
    /// <returns>A task completing once Explorer was seen restarting on its own (within ~3 s) or was started by us.</returns>
    public static async Task RestartExplorer()
    {
        int session = Process.GetCurrentProcess().SessionId;
        foreach (var explorer in Process.GetProcessesByName("explorer").Where(p => p.SessionId == session))
        {
            try
            {
                explorer.Kill();
                await explorer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                AppLog.Warn($"Could not stop explorer.exe ({explorer.Id}): {ex.Message}");
            }
            finally
            {
                explorer.Dispose();
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (Process.GetProcessesByName("explorer").Any(p => p.SessionId == session))
            {
                return;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true })?.Dispose();
    }
}
