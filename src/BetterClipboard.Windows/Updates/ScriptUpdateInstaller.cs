using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using BetterClipboard.Core.Updates;

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

        // The last moment an update can still be cancelled: after this line another process owns it.
        cancellationToken.ThrowIfCancellationRequested();
        var process = Start(CreateStartInfo(request, scriptPath));
        return new UpdateInstallerRun(WaitForExitAsync(process));
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
                throw new UpdateException(UpdateFailure.Installer, $"The update package holds no installer ({ScriptEntryName}), so it cannot be installed from here. The installed version is unchanged.");
            }

            if (entry.Length <= 0 || entry.Length > MaxScriptBytes)
            {
                throw new UpdateException(UpdateFailure.Installer, $"The installer inside the update package has an implausible size ({entry.Length:N0} bytes). The installed version is unchanged.");
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
                    throw new UpdateException(UpdateFailure.Installer, "The installer inside the update package is larger than it claims. The installed version is unchanged.");
                }

                target.Write(buffer, 0, read);
            }

            return scriptPath;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new UpdateException(UpdateFailure.Installer, $"The installer could not be taken out of the update package ({ex.Message}). The installed version is unchanged.", ex);
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
        string[] arguments =
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
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Remove(ModulePathVariable);
        return info;
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
            throw new UpdateException(UpdateFailure.Installer, $"Windows PowerShell was not found ({info.FileName}), and the installer needs it. The installed version is unchanged.");
        }

        try
        {
            return Process.Start(info)
                   ?? throw new UpdateException(UpdateFailure.Installer, "Windows did not start the installer. The installed version is unchanged.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new UpdateException(UpdateFailure.Installer, $"The installer could not be started ({ex.Message}). The installed version is unchanged.", ex);
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
