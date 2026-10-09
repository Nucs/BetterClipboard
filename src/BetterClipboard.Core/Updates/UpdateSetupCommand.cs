using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Settings;

namespace BetterClipboard.Core.Updates;

/// <summary>
/// <c>BetterClipboard.exe --set-update-check on|off</c>: switches the automatic update check
/// (<see cref="AppSettings.CheckForUpdates"/>) through the app's own settings store — the installer's way to install
/// with the check off (<c>install.ps1 -NoUpdateCheck</c>), so that not even a first request is made.
/// </summary>
/// <remarks>
/// <para>
/// Headless, like <c>--set-hotkeys</c>: the app's entry point runs it before the single-instance lock and before any
/// XAML, and the process ends with the command's exit code. Output goes to standard output as UTF-8: one line
/// <c>update-check: on</c> or <c>update-check: off</c> on success, a line starting with <c>error:</c> otherwise. A
/// GUI-subsystem process has no console, so the output is only seen when the caller redirects it (the installer does).
/// </para>
/// <para>
/// Saving refuses while that data folder's BetterClipboard runs: the running app holds the settings in memory and would
/// overwrite the file with its next change. The installer closes the app first; anyone else uses Settings › Updates.
/// </para>
/// <para>
/// Footgun: versions from before this command ignore unknown arguments and start normally (a plain launch opens
/// Settings). The installer therefore runs it only for a release that has it.
/// </para>
/// </remarks>
public static class UpdateSetupCommand
{
    /// <summary>The switch that selects this command; it must be the first argument.</summary>
    public const string Switch = "--set-update-check";

    /// <summary>Exit code: the setting was saved.</summary>
    public const int ExitOk = 0;

    /// <summary>Exit code: the argument after the switch is neither <c>on</c> nor <c>off</c>.</summary>
    public const int ExitInvalid = 2;

    /// <summary>Exit code: BetterClipboard runs with this data folder, so nothing was saved.</summary>
    public const int ExitAppRunning = 3;

    /// <summary>Exit code: the settings file could not be written.</summary>
    public const int ExitSaveFailed = 4;

    /// <summary>Whether <paramref name="args"/> start this command.</summary>
    /// <param name="args">The process's arguments.</param>
    /// <returns><see langword="true"/> when the first argument is <see cref="Switch"/> (any case).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static bool Matches(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Count > 0 && string.Equals(args[0], Switch, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs the command.
    /// </summary>
    /// <param name="args">The process's arguments: <see cref="Switch"/>, then <c>on</c> or <c>off</c> (any case).</param>
    /// <param name="output">Where the answer or the <c>error:</c> line goes.</param>
    /// <param name="settingsPath">The data folder's <c>settings.json</c>.</param>
    /// <param name="isAppRunning">Whether BetterClipboard runs with that data folder (asked only before saving).</param>
    /// <returns><see cref="ExitOk"/>, <see cref="ExitInvalid"/>, <see cref="ExitAppRunning"/> or <see cref="ExitSaveFailed"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="args"/> does not start with <see cref="Switch"/>, or <paramref name="settingsPath"/> is blank.</exception>
    public static int Run(IReadOnlyList<string> args, TextWriter output, string settingsPath, Func<bool> isAppRunning)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentNullException.ThrowIfNull(isAppRunning);
        if (!Matches(args))
        {
            throw new ArgumentException($"The arguments must start with {Switch}.", nameof(args));
        }

        bool? value = args.Count == 2
            ? args[1].Trim().ToLowerInvariant() switch { "on" => true, "off" => false, _ => null }
            : null;
        if (value is not { } enabled)
        {
            output.WriteLine($"error: say whether BetterClipboard checks for updates by itself: {Switch} on, or {Switch} off");
            return ExitInvalid;
        }

        if (isAppRunning())
        {
            output.WriteLine("error: BetterClipboard is running. Close it first, or use Settings > Updates.");
            return ExitAppRunning;
        }

        try
        {
            // Load first: the change keeps every other setting, and a corrupt file is set aside exactly as the app would
            // do at its next start.
            var store = new SettingsStore(settingsPath);
            store.Load();
            store.Update(s => s with { CheckForUpdates = enabled });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"error: the settings could not be saved: {ex.Message}");
            return ExitSaveFailed;
        }

        AppLog.Info($"Automatic update check switched {(enabled ? "on" : "off")} from the command line.");
        output.WriteLine($"update-check: {(enabled ? "on" : "off")}");
        return ExitOk;
    }
}
