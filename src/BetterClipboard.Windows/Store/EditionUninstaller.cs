using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using BetterClipboard.Core.Editions;
using BetterClipboard.Windows.Updates;

namespace BetterClipboard.Windows.Store;

/// <summary>Why the installer copy could not be uninstalled, or could not be before trying.</summary>
public enum EditionUninstallFailure
{
    /// <summary>It worked.</summary>
    None = 0,

    /// <summary>This package carries no <c>install.ps1</c> (a broken or hand-made package).</summary>
    ScriptMissing = 1,

    /// <summary>Windows PowerShell is not where every Windows has it.</summary>
    PowerShellMissing = 2,

    /// <summary>
    /// Script files do not run on this PC (an execution policy set by Group Policy overrides the per-run bypass), or
    /// PowerShell could not be started at all.
    /// </summary>
    ScriptsBlocked = 3,

    /// <summary>The uninstaller ran and reported a failure, or ended with an error code.</summary>
    Failed = 4,

    /// <summary>The uninstaller did not finish in time and was left running.</summary>
    TimedOut = 5,

    /// <summary>The uninstaller reported success, but the copy's program or its Installed-apps entry is still there.</summary>
    StillInstalled = 6,

    /// <summary>The working folder could not be written.</summary>
    WorkFolder = 7,

    /// <summary>
    /// The check before the move found something this account may not change, although the uninstall has to: the other
    /// copy's folder, its Start menu shortcut, or one of the registry keys it wrote to. The detail names which.
    /// </summary>
    AccessDenied = 8,
}

/// <summary>How an uninstall, or the check before one, went.</summary>
/// <param name="Failure">What went wrong; <see cref="EditionUninstallFailure.None"/> for success.</param>
/// <param name="Detail">
/// More, in English, for the log and after the window's own sentence: the uninstaller's message, an exit code, a
/// Windows error text. <see langword="null"/> when there is nothing to add.
/// </param>
public sealed record EditionUninstallResult(EditionUninstallFailure Failure, string? Detail)
{
    /// <summary>Whether it worked.</summary>
    public bool Ok => Failure == EditionUninstallFailure.None;
}

/// <summary>
/// Uninstalls the copy of BetterClipboard that this user installed with <c>install.ps1</c>, for the Microsoft Store
/// edition that takes its place: runs the installer script this package carries, in its uninstall mode, against that
/// copy's folder.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this package's script and not the installed copy's own.</b> The uninstall mode of older scripts has two faults
/// this flow would hit: it closes every BetterClipboard of the session, which would end the Store edition in the
/// middle of the move, and scripts up to 0.2.5 empty Explorer's settings key when they give Win+V back
/// (CLAUDE.md §2.25). The script in this package is the current one, and it is told to spare this process
/// (<c>-KeepProcessId</c>).
/// </para>
/// <para>
/// <b>Why a child process can do it.</b> A packaged process's own registry writes are redirected, but a process it
/// starts runs outside the package's environment (measured, CLAUDE.md §2.47): the uninstaller's changes to the Run key,
/// the Installed-apps entry, the user's PATH and Explorer's shortcuts are real.
/// </para>
/// <para>
/// <b>The script is copied out first:</b> a process outside the package cannot be counted on to read files inside the
/// package's install folder, so the script is written into the app's working folder and run from there, as the
/// updater does with a release's script (<see cref="ScriptUpdateInstaller"/>).
/// </para>
/// <para>
/// Only the copy of this user is handled. A copy for every user, or a Chocolatey copy, belongs to the PC's
/// administrator and to every other user of it: the Store edition names the way to remove it and waits.
/// </para>
/// </remarks>
public static class EditionUninstaller
{
    /// <summary>The installer script's name, here in the package and in a release zip alike.</summary>
    internal const string ScriptName = "install.ps1";

    /// <summary>The name the script is copied out under.</summary>
    internal const string WorkScriptName = "uninstall-other-edition.ps1";

    /// <summary>The name of the probe script that proves script files run on this PC.</summary>
    internal const string ProbeScriptName = "script-check.ps1";

    /// <summary>The exit code the probe script ends with; any other answer means it did not run.</summary>
    internal const int ProbeExitCode = 42;

    /// <summary>Where the uninstaller writes its result.</summary>
    internal const string ResultFileName = "uninstall-result.json";

    /// <summary>Where the probe script writes what this account may not change (empty for nothing).</summary>
    internal const string ProbeResultFileName = "script-check.txt";

    /// <summary>
    /// The parameters of <c>install.ps1</c> that <see cref="UninstallAsync(OtherEdition, string, string, CancellationToken)"/>
    /// passes together. The script must accept them in one parameter set: a test binds them against the real script,
    /// because the first live move failed on exactly that (PowerShell refused the command line before the script's
    /// first line, so the uninstall "ended with code 1" and said nothing). Keep it in step with the argument list there.
    /// </summary>
    internal static readonly string[] UninstallParameters = ["Uninstall", "InstallDir", "KeepProcessId", "ResultPath", "LogPath"];

    /// <summary>
    /// The probe: proves that script files run on this PC, and that this account may change what the uninstall has to
    /// change. It changes nothing itself: registry keys are only opened for writing, the folder gets a file that is
    /// deleted at once, the shortcut is only opened. It runs where the uninstaller will run (outside the package), so
    /// what it finds is what the uninstaller will meet. A missing key, folder or shortcut passes: there is nothing to
    /// remove there. The key paths and the shortcut's name are the installer's own (<c>install.ps1</c>).
    /// </summary>
    private const string ProbeScript =
        """
        # BetterClipboard: checks, without changing anything, that this account may do what uninstalling the other copy needs.
        param([string] $InstallDir, [string] $ResultPath)
        $problems = New-Object System.Collections.Generic.List[string]
        function Test-Key([string] $Path, [string] $Name) {
            try {
                $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($Path, $true)
                if ($null -ne $key) { $key.Dispose() }
            } catch { $problems.Add("registry:$Name") }
        }
        Test-Key 'Software\Microsoft\Windows\CurrentVersion\Run' 'Run'
        Test-Key 'Software\Microsoft\Windows\CurrentVersion\Uninstall\BetterClipboard' 'Uninstall'
        Test-Key 'Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' 'Explorer'
        Test-Key 'Environment' 'Environment'
        if ($InstallDir -and (Test-Path -LiteralPath $InstallDir)) {
            $file = Join-Path $InstallDir ('.remove-check-' + [guid]::NewGuid().ToString('N') + '.tmp')
            try { [IO.File]::WriteAllBytes($file, [byte[]] @(0)); [IO.File]::Delete($file) } catch { $problems.Add('folder') }
        }
        $programs = [Environment]::GetFolderPath('Programs')
        if ($programs) {
            $shortcut = Join-Path $programs 'BetterClipboard.lnk'
            if (Test-Path -LiteralPath $shortcut) {
                try { $stream = [IO.File]::Open($shortcut, 'Open', 'ReadWrite', 'ReadWrite'); $stream.Dispose() } catch { $problems.Add('shortcut') }
            }
        }
        if ($ResultPath) { [IO.File]::WriteAllText($ResultPath, ($problems -join ',')) }
        exit 42
        """;

    /// <summary>How long the uninstaller may take: it closes programs, restarts Explorer once and deletes a folder.</summary>
    private static readonly TimeSpan UninstallTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How long the probe may take; Windows PowerShell's first start on a slow disk takes seconds.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(90);

    /// <summary>The installer script inside this package.</summary>
    public static string PackagedScriptPath => Path.Combine(AppContext.BaseDirectory, ScriptName);

    /// <summary>
    /// Checks, without changing anything that stays, that an uninstall can run here: the script is in the package,
    /// PowerShell exists, the working folder can be written, a script file really runs (the probe must end with
    /// <see cref="ProbeExitCode"/>), and this account may change everything the uninstall has to change — the copy's
    /// folder, its shortcut, and the registry keys the installer wrote to (<see cref="ProbeScript"/>).
    /// </summary>
    /// <param name="edition">The copy that would be uninstalled.</param>
    /// <param name="workDirectory">The app's folder for this move; created when missing.</param>
    /// <param name="cancellationToken">Stops waiting for the probe.</param>
    /// <returns>
    /// The verdict; never throws for the failures it names. <see cref="EditionUninstallFailure.AccessDenied"/> carries
    /// what may not be changed in its detail, as comma-separated names (<c>folder</c>, <c>shortcut</c>,
    /// <c>registry:Run</c>, <c>registry:Uninstall</c>, <c>registry:Explorer</c>, <c>registry:Environment</c>).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="edition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="workDirectory"/> is null or blank.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static Task<EditionUninstallResult> CheckAsync(OtherEdition edition, string workDirectory, CancellationToken cancellationToken) =>
        CheckAsync(edition, workDirectory, PackagedScriptPath, cancellationToken);

    /// <summary>
    /// <see cref="CheckAsync(OtherEdition, string, CancellationToken)"/> with the installer script expected at a path of
    /// the caller's choosing: the app passes <see cref="PackagedScriptPath"/>, the tests a stand-in, so the check runs
    /// there through the real Windows PowerShell without a package around it.
    /// </summary>
    /// <param name="edition">The copy that would be uninstalled.</param>
    /// <param name="workDirectory">The app's folder for this move; created when missing.</param>
    /// <param name="packagedScript">Where the installer script is expected; only its existence is checked here.</param>
    /// <param name="cancellationToken">Stops waiting for the probe.</param>
    /// <returns>The verdict, as for the public overload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="edition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="workDirectory"/> or <paramref name="packagedScript"/> is null or blank.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    internal static async Task<EditionUninstallResult> CheckAsync(OtherEdition edition, string workDirectory, string packagedScript, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edition);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagedScript);
        if (!File.Exists(packagedScript))
        {
            return new EditionUninstallResult(EditionUninstallFailure.ScriptMissing, null);
        }

        string powerShell = ScriptUpdateInstaller.DefaultPowerShellPath;
        if (!File.Exists(powerShell))
        {
            return new EditionUninstallResult(EditionUninstallFailure.PowerShellMissing, null);
        }

        string probe = Path.Combine(workDirectory, ProbeScriptName);
        string found = Path.Combine(workDirectory, ProbeResultFileName);
        try
        {
            Directory.CreateDirectory(workDirectory);
            TryDelete(found);

            // With a byte order mark: Windows PowerShell 5.1 reads a script without one in the PC's ANSI code page.
            await File.WriteAllTextAsync(probe, ProbeScript.ReplaceLineEndings("\r\n") + "\r\n", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new EditionUninstallResult(EditionUninstallFailure.WorkFolder, ex.Message);
        }

        try
        {
            var (code, timedOut) = await RunAsync(powerShell, ["-File", probe, "-InstallDir", edition.Directory, "-ResultPath", found], ProbeTimeout, cancellationToken).ConfigureAwait(false);
            if (timedOut)
            {
                return new EditionUninstallResult(EditionUninstallFailure.ScriptsBlocked, "PowerShell did not answer in time.");
            }

            if (code != ProbeExitCode)
            {
                return new EditionUninstallResult(EditionUninstallFailure.ScriptsBlocked, $"The check script ended with code {code}.");
            }

            // The script ran. No result file means it could not even write into the working folder it was given.
            string denied;
            try
            {
                denied = (await File.ReadAllTextAsync(found, cancellationToken).ConfigureAwait(false)).Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new EditionUninstallResult(EditionUninstallFailure.WorkFolder, ex.Message);
            }

            return denied.Length == 0
                ? new EditionUninstallResult(EditionUninstallFailure.None, null)
                : new EditionUninstallResult(EditionUninstallFailure.AccessDenied, denied);
        }
        catch (Win32Exception ex)
        {
            return new EditionUninstallResult(EditionUninstallFailure.ScriptsBlocked, ex.Message);
        }
        finally
        {
            TryDelete(probe);
            TryDelete(found);
        }
    }

    /// <summary>
    /// Uninstalls the installer copy: its startup entry, shortcut, Installed-apps entry, PATH entry and folder, and it
    /// gives the shortcuts it had released from Explorer back to Windows (which restarts Explorer once). Its history
    /// folder is left for the caller, which removes it after everything else is proven.
    /// </summary>
    /// <param name="edition">The copy, found as <see cref="OtherEditionKind.Installer"/>.</param>
    /// <param name="workDirectory">The app's folder for this move; the script and its result go there.</param>
    /// <param name="logPath">Where the uninstaller writes what it did (a PowerShell transcript).</param>
    /// <param name="cancellationToken">Stops waiting (the uninstaller itself is not stopped: half an uninstall is worse).</param>
    /// <returns>The outcome, proven: success means the copy's program and its Installed-apps entry are gone.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="edition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The copy is of another kind, or a path is null or blank.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static Task<EditionUninstallResult> UninstallAsync(OtherEdition edition, string workDirectory, string logPath, CancellationToken cancellationToken) =>
        UninstallAsync(edition, workDirectory, logPath, PackagedScriptPath, cancellationToken);

    /// <summary>
    /// <see cref="UninstallAsync(OtherEdition, string, string, CancellationToken)"/> with the installer script at a path
    /// of the caller's choosing: the app passes <see cref="PackagedScriptPath"/>, the tests a stand-in that only reports
    /// what it was started with, so the command line and the reading of the result are checked through the real Windows
    /// PowerShell while nothing is uninstalled.
    /// </summary>
    /// <param name="edition">The copy, found as <see cref="OtherEditionKind.Installer"/>.</param>
    /// <param name="workDirectory">The app's folder for this move; the script and its result go there.</param>
    /// <param name="logPath">Where the uninstaller writes what it did.</param>
    /// <param name="packagedScript">The installer script to copy out and run in its uninstall mode.</param>
    /// <param name="cancellationToken">Stops waiting (the uninstaller itself is not stopped).</param>
    /// <returns>The outcome, as for the public overload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="edition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The copy is of another kind, or a path is null or blank.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    internal static async Task<EditionUninstallResult> UninstallAsync(OtherEdition edition, string workDirectory, string logPath, string packagedScript, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edition);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagedScript);
        if (edition.Kind != OtherEditionKind.Installer)
        {
            throw new ArgumentException("Only the copy this user installed with the installer is uninstalled from here.", nameof(edition));
        }

        // Asked first, so a script that is not there is named as such whatever else is wrong (a missing folder around it
        // would otherwise read as a working folder that cannot be written), and nothing is created for nothing.
        if (!File.Exists(packagedScript))
        {
            return new EditionUninstallResult(EditionUninstallFailure.ScriptMissing, null);
        }

        string script = Path.Combine(workDirectory, WorkScriptName);
        string result = Path.Combine(workDirectory, ResultFileName);
        try
        {
            Directory.CreateDirectory(workDirectory);
            File.Copy(packagedScript, script, overwrite: true);
            TryDelete(result);
            TryDelete(logPath);
        }
        catch (FileNotFoundException)
        {
            return new EditionUninstallResult(EditionUninstallFailure.ScriptMissing, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new EditionUninstallResult(EditionUninstallFailure.WorkFolder, ex.Message);
        }

        // -KeepProcessId: the script closes every BetterClipboard of the session before it removes one; this process is
        // the one that asked.
        string[] arguments =
        [
            "-File", script,
            "-Uninstall",
            "-InstallDir", edition.Directory,
            "-KeepProcessId", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ResultPath", result,
            "-LogPath", logPath,
        ];

        int code;
        try
        {
            (code, bool timedOut) = await RunAsync(ScriptUpdateInstaller.DefaultPowerShellPath, arguments, UninstallTimeout, cancellationToken).ConfigureAwait(false);
            if (timedOut)
            {
                return new EditionUninstallResult(EditionUninstallFailure.TimedOut, null);
            }
        }
        catch (Win32Exception ex)
        {
            return new EditionUninstallResult(EditionUninstallFailure.ScriptsBlocked, ex.Message);
        }
        finally
        {
            TryDelete(script);
        }

        var (reported, error) = ReadResult(result);
        TryDelete(result);
        if (code != 0 || reported == false)
        {
            return new EditionUninstallResult(EditionUninstallFailure.Failed, error ?? $"The uninstaller ended with code {code}.");
        }

        return OtherEditionLocator.IsStillInstalled(edition)
            ? new EditionUninstallResult(EditionUninstallFailure.StillInstalled, null)
            : new EditionUninstallResult(EditionUninstallFailure.None, null);
    }

    /// <summary>
    /// Runs Windows PowerShell on a script file, without a window, without the user's profile, and without the module
    /// path of whatever shell started the app (<see cref="ScriptUpdateInstaller"/> has why), and waits for it.
    /// </summary>
    /// <param name="powerShell">Full path of <c>powershell.exe</c>.</param>
    /// <param name="arguments">The arguments after the fixed switches, starting with <c>-File</c>.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The exit code, and whether the wait ran out (the process then goes on by itself).</returns>
    /// <exception cref="Win32Exception">PowerShell could not be started.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    private static async Task<(int ExitCode, bool TimedOut)> RunAsync(string powerShell, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = powerShell,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (string fixedSwitch in (string[])["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass"])
        {
            start.ArgumentList.Add(fixedSwitch);
        }

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Remove(ScriptUpdateInstaller.ModulePathVariable);
        using var process = Process.Start(start) ?? throw new Win32Exception("PowerShell did not start.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return (process.ExitCode, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (-1, true);
        }
    }

    /// <summary>Reads the uninstaller's result file.</summary>
    /// <param name="path">The file.</param>
    /// <returns>What it reports (<see langword="null"/> when there is no readable report) and its error text, if any.</returns>
    private static (bool? Ok, string? Error) ReadResult(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return (null, null);
            }

            // ReadAllText drops the byte order mark Windows PowerShell 5.1 writes.
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            bool? ok = root.TryGetProperty("ok", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
            string? error = root.TryGetProperty("error", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
            return (ok, string.IsNullOrWhiteSpace(error) ? null : error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>Deletes a file if it can; a leftover in the working folder is harmless.</summary>
    /// <param name="path">The file.</param>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind: the next move overwrites it.
        }
    }
}
