using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using BetterClipboard.Core;
using BetterClipboard.Core.Updates;
using BetterClipboard.Windows.Input;

namespace BetterClipboard.Windows.Updates;

/// <summary>
/// Installs an approved update by running the new release's own <c>install.ps1</c> in update mode: it closes the app,
/// swaps the install folder, and starts the new version in the background.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a script from the new package.</b> A running program cannot replace its own folder, so another process must.
/// The package brings the installer that knows how its own version is laid out; the app only has to start it. The
/// script comes out of the package the update service verified (SHA-256 against the release's checksum list), never
/// from the installed copy and never from the network.
/// </para>
/// <para>
/// <b>The command line is a contract</b> with every released <c>install.ps1</c> to come:
/// <c>-Update -Package &lt;zip&gt; -PackageSha256 &lt;hash&gt; -InstallDir &lt;folder&gt; -ResultPath &lt;file&gt; -LogPath &lt;file&gt;</c>.
/// A later app version may add arguments only when every package it can update to accepts them.
/// </para>
/// <para>
/// <b>How it is started.</b> Windows PowerShell 5.1 (part of every Windows 10 and 11), without a window and without the
/// user's profile. <c>PSModulePath</c> is removed from its environment: an app that was started from PowerShell 7
/// inherits that shell's module path, with which Windows PowerShell cannot load its own modules (not even
/// <c>Get-FileHash</c>). The working folder is the temp folder, never the install folder, which is about to be renamed.
/// Nothing is redirected: the app closes while the script still runs, and a pipe to a closed process would end the
/// script mid-update. The script writes its own log (<c>-LogPath</c>) and result (<c>-ResultPath</c>) instead.
/// </para>
/// <para>
/// Footgun: an execution policy enforced by Group Policy overrides <c>-ExecutionPolicy Bypass</c>. The script then never
/// runs, the process ends at once with a code other than 0, and the update service reports that the installed version
/// is unchanged. The installer command of the README (<c>irm … | iex</c>) is not a script file and still works there.
/// </para>
/// <para>
/// <b>As administrator, for a folder only administrators may change</b> (<see cref="NeedsAdministrator"/>; a copy in Program
/// Files, where one with UI access must live, CLAUDE.md §2.63). The same command line goes to the same script, started
/// through the shell with the <c>runas</c> verb (<see cref="CreateElevatedStartInfo"/>): an administrator's account gets one
/// consent prompt. Only for a user who can elevate that way (<see cref="UiAccess.CanElevateWithConsent"/>), and only for a
/// release whose installer then starts BetterClipboard again with the user's normal rights (<see cref="ElevatedUpdate"/>);
/// otherwise the update stops with a message before anything changes. So does an isolated instance
/// (<see cref="AppPaths.DataDirectoryVariable"/>): Windows gives an elevated program its account's own environment, so the
/// installer would not see that instance's data folder.
/// </para>
/// </remarks>
public sealed class ScriptUpdateInstaller : IUpdateInstaller
{
    /// <summary>The installer's name at the root of a release zip (put there by <c>tools/release/package.ps1</c>).</summary>
    internal const string ScriptEntryName = "install.ps1";

    /// <summary>
    /// The largest installer script accepted, in bytes. The real one is under 100 KB; the limit only keeps a broken
    /// archive entry from filling the disk.
    /// </summary>
    internal const long MaxScriptBytes = 2 * 1024 * 1024;

    /// <summary>The environment variable removed from the installer's environment (see the class remarks).</summary>
    internal const string ModulePathVariable = "PSModulePath";

    private readonly string powerShellPath;

    /// <summary>Creates an installer that runs the script with this PC's Windows PowerShell.</summary>
    public ScriptUpdateInstaller()
        : this(DefaultPowerShellPath)
    {
    }

    /// <summary>
    /// Creates an installer that runs the script with a given PowerShell; tests use it to prove what happens without one.
    /// </summary>
    /// <param name="powerShellPath">Full path of <c>powershell.exe</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="powerShellPath"/> is null or blank.</exception>
    internal ScriptUpdateInstaller(string powerShellPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(powerShellPath);
        this.powerShellPath = powerShellPath;
    }

    /// <summary>
    /// Windows PowerShell 5.1 in the system folder. A full path on purpose: a bare <c>powershell.exe</c> would be looked
    /// up on the PATH, where another program of that name could come first.
    /// </summary>
    public static string DefaultPowerShellPath =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public async Task<UpdateInstallerRun> StartAsync(UpdateInstallRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Reading the zip's directory and writing the script is disk work: off the caller's thread.
        var scriptPath = await Task.Run(() => ExtractScript(request.PackagePath, request.WorkDirectory, cancellationToken), cancellationToken).ConfigureAwait(false);

        // A copy in a folder only administrators may change is replaced by an installer that runs as administrator. The probe
        // is a file created and deleted: disk work, off the caller's thread too.
        bool elevate = await Task.Run(() => NeedsAdministrator(request.InstallDirectory), cancellationToken).ConfigureAwait(false);
        if (elevate && !ElevatedUpdate.Accepts(request.Version))
        {
            throw new UpdateException(UpdateFailure.Installer, UpdateMessages.Format(UpdateMessages.ElevatedUpdateTooOld, request.Version));
        }

        // Windows starts an elevated program with its account's own environment, never this process's: an isolated instance's
        // data folder (BETTERCLIPBOARD_DATA_DIR) would not reach the installer, whose --exit would then close the regular
        // instance and whose records would be the regular ones. Only tests and dev runs set it; they use a test desktop.
        if (elevate && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable)))
        {
            throw new UpdateException(UpdateFailure.Installer, UpdateMessages.Format(UpdateMessages.IsolatedInstanceNeedsAdministrator, AppPaths.DataDirectoryVariable));
        }

        if (elevate && !UiAccess.CanElevateWithConsent())
        {
            throw new UpdateException(UpdateFailure.Installer, UpdateMessages.NeedsAdministrator);
        }

        // The last moment an update can still be cancelled: after this line another process owns it.
        cancellationToken.ThrowIfCancellationRequested();
        var process = elevate ? StartElevated(CreateElevatedStartInfo(request, scriptPath)) : Start(CreateStartInfo(request, scriptPath));
        return new UpdateInstallerRun(WaitForExitAsync(process));
    }

    /// <summary>
    /// Whether replacing <paramref name="installDirectory"/> needs administrator rights: this process may not create a file
    /// next to it, where the installer puts the new folder and renames the old one (a copy in Program Files).
    /// </summary>
    /// <param name="installDirectory">The running copy's folder.</param>
    /// <returns>
    /// <see langword="true"/> when Windows denies the write; <see langword="false"/> when it is allowed, and also when the
    /// probe fails another way (a missing folder, a full disk): the installer then says what is wrong, as it always did.
    /// </returns>
    internal static bool NeedsAdministrator(string installDirectory)
    {
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory)));
        if (string.IsNullOrEmpty(parent))
        {
            return false;
        }

        // A name nothing else uses, deleted by Windows when the handle closes: nothing stays behind even if this process dies.
        string probe = Path.Combine(parent, $".betterclipboard-write-probe-{Guid.NewGuid():N}");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies the installer script out of a release zip into the working folder.
    /// </summary>
    /// <param name="packagePath">The verified release zip.</param>
    /// <param name="workDirectory">The folder the script is written to (created when missing).</param>
    /// <param name="cancellationToken">Cancels before the script is written.</param>
    /// <returns>Full path of the extracted script.</returns>
    /// <exception cref="UpdateException">
    /// The package is not a readable zip, holds no <see cref="ScriptEntryName"/> at its root, holds one that is empty or
    /// larger than <see cref="MaxScriptBytes"/>, or the script could not be written (<see cref="UpdateFailure.Installer"/>).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    internal static string ExtractScript(string packagePath, string workDirectory, CancellationToken cancellationToken)
    {
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);

            // Only the entry at the root: a file of that name in a subfolder is not the release's installer.
            var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.FullName, ScriptEntryName, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                throw new UpdateException(UpdateFailure.Installer, UpdateMessages.Format(UpdateMessages.PackageHoldsNoInstaller, ScriptEntryName));
            }

            if (entry.Length <= 0 || entry.Length > MaxScriptBytes)
            {
                throw new UpdateException(UpdateFailure.Installer, UpdateMessages.Format(UpdateMessages.InstallerImplausibleSize, entry.Length));
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(workDirectory);

            // A fixed name in our own folder: the entry's name is never used as a path.
            var scriptPath = Path.Combine(Path.GetFullPath(workDirectory), ScriptEntryName);
            using var source = entry.Open();
            using var target = new FileStream(scriptPath, FileMode.Create, FileAccess.Write, FileShare.None);

            // The stated length is the archive's claim; the copy stops at the limit whatever the entry really holds.
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > MaxScriptBytes)
                {
                    throw new UpdateException(UpdateFailure.Installer, UpdateMessages.InstallerLargerThanClaimed);
                }

                target.Write(buffer, 0, read);
            }

            return scriptPath;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new UpdateException(UpdateFailure.Installer, UpdateMessages.Format(UpdateMessages.InstallerNotTakenOut, ex.Message), ex);
        }
    }

    /// <summary>
    /// Builds the installer's process description: the contract command line, no window, the temp folder as working
    /// folder, and an environment without <see cref="ModulePathVariable"/>.
    /// </summary>
    /// <param name="request">What to install, and where.</param>
    /// <param name="scriptPath">The extracted installer script.</param>
    /// <returns>The start information; nothing is started.</returns>
    internal ProcessStartInfo CreateStartInfo(UpdateInstallRequest request, string scriptPath)
    {
        var info = new ProcessStartInfo(powerShellPath)
        {
            // CreateProcess, not the shell: the arguments reach the script exactly as listed, and the environment below applies.
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };

        // ArgumentList quotes each argument for the Windows command line, so paths with spaces arrive whole; -File hands
        // them to the script as plain text, with nothing interpreted on the way.
        foreach (var argument in ContractArguments(request, scriptPath))
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Remove(ModulePathVariable);
        return info;
    }

    /// <summary>
    /// Builds the process description of an installer that runs as administrator: the same command line as
    /// <see cref="CreateStartInfo"/>, started by the shell with the <c>runas</c> verb (one consent prompt), with no window
    /// and the temp folder as working folder.
    /// </summary>
    /// <param name="request">What to install, and where.</param>
    /// <param name="scriptPath">The extracted installer script.</param>
    /// <returns>The start information; nothing is started.</returns>
    /// <remarks>
    /// The environment is not touched, and cannot be through the shell: Windows starts an elevated program with its
    /// account's own environment, never the caller's, so PowerShell 7's module path cannot reach it either. The arguments
    /// are joined here with the quoting rules <see cref="ProcessStartInfo.ArgumentList"/> uses (<see cref="JoinArguments"/>),
    /// so the script receives exactly what the unelevated start would hand it.
    /// </remarks>
    internal ProcessStartInfo CreateElevatedStartInfo(UpdateInstallRequest request, string scriptPath) =>
        new(powerShellPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetTempPath(),
            Arguments = JoinArguments(ContractArguments(request, scriptPath)),
        };

    /// <summary>The contract command line (see the class remarks), one argument per item.</summary>
    /// <param name="request">What to install, and where.</param>
    /// <param name="scriptPath">The extracted installer script.</param>
    /// <returns>The arguments, in order.</returns>
    private static string[] ContractArguments(UpdateInstallRequest request, string scriptPath) =>
    [
        "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
        "-File", scriptPath,
        "-Update",
        "-Package", request.PackagePath,
        "-PackageSha256", request.PackageSha256,

        // No trailing separator: the script builds "<folder>.new" and "<folder>.old" from this text.
        "-InstallDir", Path.TrimEndingDirectorySeparator(request.InstallDirectory),
        "-ResultPath", request.ResultPath,
        "-LogPath", request.LogPath,
    ];

    /// <summary>
    /// Joins arguments into one Windows command line the way <see cref="ProcessStartInfo.ArgumentList"/> does for
    /// <c>CreateProcess</c>: an argument without spaces, tabs or quotes stays as it is; any other one is quoted, with a
    /// quote inside escaped by a backslash and the backslashes before a quote (or before the closing one) doubled, so
    /// <c>CommandLineToArgvW</c> and PowerShell read each one back whole.
    /// </summary>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The command line (without the program).</returns>
    internal static string JoinArguments(IEnumerable<string> arguments)
    {
        var line = new StringBuilder();
        foreach (var argument in arguments)
        {
            if (line.Length > 0)
            {
                line.Append(' ');
            }

            if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\"") < 0)
            {
                line.Append(argument);
                continue;
            }

            line.Append('"');
            for (int i = 0; i < argument.Length;)
            {
                char c = argument[i++];
                if (c == '\\')
                {
                    int backslashes = 1;
                    while (i < argument.Length && argument[i] == '\\')
                    {
                        i++;
                        backslashes++;
                    }

                    if (i == argument.Length)
                    {
                        // Before the closing quote: doubled, so none of them escapes it.
                        line.Append('\\', backslashes * 2);
                    }
                    else if (argument[i] == '"')
                    {
                        // Before a quote inside: doubled, plus one that escapes the quote.
                        line.Append('\\', (backslashes * 2) + 1).Append('"');
                        i++;
                    }
                    else
                    {
                        line.Append('\\', backslashes);
                    }

                    continue;
                }

                if (c == '"')
                {
                    line.Append('\\').Append('"');
                    continue;
                }

                line.Append(c);
            }

            line.Append('"');
        }

        return line.ToString();
    }

    /// <summary>Starts the installer process.</summary>
    /// <param name="info">The process description from <see cref="CreateStartInfo"/>.</param>
    /// <returns>The running process; the caller disposes it.</returns>
    /// <exception cref="UpdateException">
    /// Windows PowerShell is missing or could not be started (<see cref="UpdateFailure.Installer"/>). Nothing was changed.
    /// </exception>
    private static Process Start(ProcessStartInfo info)
    {
        if (!File.Exists(info.FileName))
        {
            throw new UpdateException(UpdateFailure.Installer, UpdateMessages.Format(UpdateMessages.PowerShellMissing, info.FileName));
        }

        try
        {
            return Process.Start(info)
                   ?? throw new UpdateException(UpdateFailure.Installer, UpdateMessages.InstallerProcessMissing);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new UpdateException(UpdateFailure.Installer, UpdateMessages.Format(UpdateMessages.InstallerNotStarted, ex.Message), ex);
        }
    }

    /// <summary>Starts the installer as administrator (<see cref="CreateElevatedStartInfo"/>); Windows asks for consent first.</summary>
    /// <param name="info">The process description.</param>
    /// <returns>The running process; the caller disposes it.</returns>
    /// <exception cref="UpdateException">
    /// Windows PowerShell is missing, the consent prompt was declined (<see cref="UpdateMessages.ElevationDeclined"/>), or the
    /// installer could not be started (<see cref="UpdateFailure.Installer"/>). Nothing was changed.
    /// </exception>
    private static Process StartElevated(ProcessStartInfo info)
    {
        if (!File.Exists(info.FileName))
        {
            throw new UpdateException(UpdateFailure.Installer, UpdateMessages.Format(UpdateMessages.PowerShellMissing, info.FileName));
        }

        try
        {
            return Process.Start(info)
                   ?? throw new UpdateException(UpdateFailure.Installer, UpdateMessages.InstallerProcessMissing);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Interop.NativeMethods.ERROR_CANCELLED)
        {
            throw new UpdateException(UpdateFailure.Installer, UpdateMessages.ElevationDeclined, ex);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new UpdateException(UpdateFailure.Installer, UpdateMessages.Format(UpdateMessages.InstallerNotStarted, ex.Message), ex);
        }
    }

    /// <summary>Waits for the installer process and gives its exit code.</summary>
    /// <param name="process">The running installer; disposed when it ends.</param>
    /// <returns>
    /// The exit code. On a successful update this never completes inside the app: the installer closes the app first.
    /// </returns>
    private static async Task<int> WaitForExitAsync(Process process)
    {
        using (process)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
    }
}
