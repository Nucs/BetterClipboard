using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Settings;

namespace BetterClipboard.Windows.Input;

/// <summary>
/// <c>BetterClipboard.exe --set-hotkeys [--validate] SHORTCUT...</c>: checks shortcut texts and, without
/// <c>--validate</c>, saves them as the shortcuts that open the panel — the installer's way to pick the shortcuts
/// (<c>install.ps1 -Hotkey</c>, <c>-NoTakeOverWinV</c>) through the app's own parser and settings store.
/// </summary>
/// <remarks>
/// <para>
/// Headless: the app's entry point runs it before the single-instance lock and before any XAML, and the process ends
/// with the command's exit code. Output goes to standard output as UTF-8, one line each: the canonical shortcuts on
/// success (<c>Alt+Win+V</c> for <c>win + alt + v</c>), lines starting with <c>error:</c> otherwise. A GUI-subsystem
/// process has no console, so the output is only seen when the caller redirects it (the installer does).
/// </para>
/// <para>
/// Saving refuses while that data folder's BetterClipboard runs: the running app holds the settings in memory and would
/// overwrite the file with its next change, and it would not notice new shortcuts anyway. The installer closes the app
/// first; anyone else changes running shortcuts in Settings › Shortcut.
/// </para>
/// <para>
/// Footgun: versions from before this command ignore unknown arguments and start normally (a plain launch opens
/// Settings). Callers that may hold an older <c>BetterClipboard.exe</c> must check its version first, as the installer
/// does for the release it installs.
/// </para>
/// </remarks>
public static class HotkeySetupCommand
{
    /// <summary>The switch that selects this command; it must be the first argument.</summary>
    public const string Switch = "--set-hotkeys";

    /// <summary>Check and print the shortcuts without saving them (the installer runs it before closing the app).</summary>
    public const string ValidateSwitch = "--validate";

    /// <summary>Exit code: the shortcuts are valid (and saved, without <see cref="ValidateSwitch"/>).</summary>
    public const int ExitOk = 0;

    /// <summary>Exit code: no shortcut given, one is not a shortcut, or there are more than <see cref="AppSettings.MaxOpenHotkeys"/>.</summary>
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

    /// <summary>Whether the command only checks (<see cref="ValidateSwitch"/> given), so nothing is saved or logged.</summary>
    /// <param name="args">The process's arguments.</param>
    /// <returns><see langword="true"/> when <see cref="ValidateSwitch"/> is among them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static bool IsValidateOnly(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Skip(1).Any(a => string.Equals(a, ValidateSwitch, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Runs the command.
    /// </summary>
    /// <param name="args">The process's arguments, starting with <see cref="Switch"/>; every argument after it but <see cref="ValidateSwitch"/> is a shortcut, in order (the first becomes the main one the tray names).</param>
    /// <param name="output">Where the canonical shortcuts or the <c>error:</c> lines go.</param>
    /// <param name="settingsPath">The data folder's <c>settings.json</c>.</param>
    /// <param name="isAppRunning">Whether BetterClipboard runs with that data folder (asked only before saving).</param>
    /// <returns><see cref="ExitOk"/>, <see cref="ExitInvalid"/>, <see cref="ExitAppRunning"/> or <see cref="ExitSaveFailed"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="args"/> does not start with <see cref="Switch"/>.</exception>
    public static int Run(IReadOnlyList<string> args, TextWriter output, string settingsPath, Func<bool> isAppRunning)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentNullException.ThrowIfNull(isAppRunning);
        if (!Matches(args))
        {
            throw new ArgumentException($"The arguments must start with {Switch}.", nameof(args));
        }

        bool validateOnly = IsValidateOnly(args);
        var texts = args.Skip(1).Where(a => !string.Equals(a, ValidateSwitch, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (texts.Length == 0)
        {
            output.WriteLine($"error: name at least one shortcut, e.g. {Switch} Win+V Alt+Win+V");
            return ExitInvalid;
        }

        var parsed = HotkeyList.Parse(texts);
        if (parsed.Invalid.Count > 0)
        {
            foreach (var text in parsed.Invalid)
            {
                // The parser's own reason: an unknown key name, a modifier alone, or a gesture no shortcut may have.
                HotkeyGesture.TryParse(text, out _, out var problem);
                output.WriteLine($"error: \"{text}\" is not a shortcut BetterClipboard can use. {problem}");
            }

            return ExitInvalid;
        }

        if (parsed.Truncated)
        {
            output.WriteLine($"error: at most {AppSettings.MaxOpenHotkeys} shortcuts can open BetterClipboard.");
            return ExitInvalid;
        }

        var canonical = parsed.CanonicalTexts;
        if (!validateOnly)
        {
            if (isAppRunning())
            {
                output.WriteLine("error: BetterClipboard is running. Close it first, or change the shortcuts in Settings > Shortcut.");
                return ExitAppRunning;
            }

            try
            {
                // Load first: the change keeps every other setting, and a corrupt file is set aside exactly as the app
                // would do at its next start.
                var store = new SettingsStore(settingsPath);
                store.Load();
                store.Update(s => s.WithOpenHotkeys(canonical));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                output.WriteLine($"error: the settings could not be saved: {ex.Message}");
                return ExitSaveFailed;
            }

            // Shortcut names only: they are not clipboard content, and "who changed my shortcut?" is answered by the log.
            AppLog.Info($"Shortcuts set from the command line: {string.Join(", ", canonical)}.");
        }

        foreach (var text in canonical)
        {
            output.WriteLine(text);
        }

        return ExitOk;
    }
}
