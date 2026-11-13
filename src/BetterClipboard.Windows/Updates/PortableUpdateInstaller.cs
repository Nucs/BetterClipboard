using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using BetterClipboard.Core.Updates;

namespace BetterClipboard.Windows.Updates;

/// <summary>
/// Installs an approved update into a portable copy's own folder (<see cref="UpdateInstallKind.Portable"/>): unpacks the
/// verified release zip into a staging folder and starts the <b>new</b> version's <c>BetterClipboard.exe</c> from there as
/// the helper (<see cref="PortableUpdate.ApplySwitch"/>, <see cref="PortableUpdateCommand"/>), which closes this app,
/// replaces the folder's files and starts the new version.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the new version applies itself.</b> A running program cannot replace its own files, so another process must; and
/// the new release knows its own layout. No PowerShell is involved, so a policy that blocks scripts does not stop a portable
/// update. The staging files come out of the package the update service verified (SHA-256 against the release's checksum
/// list) and carry no mark of the web, so SmartScreen does not ask about them.
/// </para>
/// <para>
/// <b>The command line is a contract</b> (<see cref="PortableUpdate"/>): the helper of every later release must accept what
/// <see cref="CreateStartInfo"/> passes.
/// </para>
/// <para>
/// <b>How it is started.</b> Directly (CreateProcess), with this process's environment, so an isolated instance
/// (<c>BETTERCLIPBOARD_DATA_DIR</c>) comes back as itself; the working folder is the temp folder, never the portable folder.
/// </para>
/// </remarks>
public sealed class PortableUpdateInstaller : IUpdateInstaller
{
    /// <summary>The program the helper is, at the root of a release zip.</summary>
    internal const string ProgramName = "BetterClipboard.exe";

    /// <summary>The name of the throwaway file <see cref="CanUpdateFolder"/> creates and deletes.</summary>
    private const string ProbeFilePrefix = ".betterclipboard-write-test-";

    private readonly int processId;
    private readonly long processStartTicks;

    /// <summary>Creates an installer for the running process (the copy that is replaced).</summary>
    public PortableUpdateInstaller()
    {
        using var self = Process.GetCurrentProcess();
        processId = self.Id;
        processStartTicks = self.StartTime.ToUniversalTime().Ticks;
    }

    /// <summary>
    /// Creates an installer that names another process as the copy being replaced; tests use it to check the command line.
    /// </summary>
    /// <param name="processId">The process the helper waits for.</param>
    /// <param name="processStartTicks">Its start time, in UTC ticks (a reused id is told apart by it).</param>
    internal PortableUpdateInstaller(int processId, long processStartTicks)
    {
        this.processId = processId;
        this.processStartTicks = processStartTicks;
    }

    /// <summary>
    /// Whether this user can replace files in a portable copy's folder: a throwaway file is created and deleted there. A
    /// folder under Program Files without administrator rights, read-only media or a share without write access say no, and
    /// such a copy keeps the download page and the install command.
    /// </summary>
    /// <param name="folder">The portable copy's folder.</param>
    /// <returns><see langword="true"/> when a file could be created and deleted. Never throws.</returns>
    public static bool CanUpdateFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        string probe;
        try
        {
            probe = Path.Combine(folder, ProbeFilePrefix + Guid.NewGuid().ToString("N"));
        }
        catch (ArgumentException)
        {
            return false;
        }

        try
        {
            // DeleteOnClose: nothing is left behind even when the process ends right here.
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public async Task<UpdateInstallerRun> StartAsync(UpdateInstallRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var staging = Path.Combine(request.WorkDirectory, PortableUpdate.StagingFolderName(request.Version));

        // Some 190 MB to write: off the caller's thread.
        await Task.Run(() => Unpack(request.PackagePath, staging, cancellationToken), cancellationToken).ConfigureAwait(false);

        // The last moment an update can still be cancelled: after this line another process owns it.
        cancellationToken.ThrowIfCancellationRequested();
        var process = Start(CreateStartInfo(request, staging));
        return new UpdateInstallerRun(WaitForExitAsync(process));
    }

    /// <summary>
    /// Unpacks a verified release zip into a fresh staging folder and checks that it holds the program.
    /// </summary>
    /// <param name="packagePath">The verified zip.</param>
    /// <param name="staging">The staging folder; an earlier one of the same name is removed first.</param>
    /// <param name="cancellationToken">Cancels before the folder is written.</param>
    /// <exception cref="UpdateException">
    /// The zip could not be unpacked (unreadable, an entry that would land outside the folder, a full disk), or it holds no
    /// <see cref="ProgramName"/> at its root (<see cref="UpdateFailure.Installer"/>). Whatever was unpacked is removed.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    internal static void Unpack(string packagePath, string staging, CancellationToken cancellationToken)
    {
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // ExtractToDirectory refuses an entry whose path leads outside the folder (IOException), so a hostile archive
            // cannot write elsewhere; the package was verified before anyway.
            ZipFile.ExtractToDirectory(packagePath, staging, overwriteFiles: false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            TryDeleteFolder(staging);
            throw new UpdateException(UpdateFailure.Installer, UpdateMessages.Format(UpdateMessages.PortablePackageUnreadable, ex.Message.TrimEnd('.')), ex);
        }

        if (!File.Exists(Path.Combine(staging, ProgramName)))
        {
            TryDeleteFolder(staging);
            throw new UpdateException(UpdateFailure.Installer, UpdateMessages.PortablePackageNoProgram);
        }
    }

    /// <summary>
    /// Builds the helper's process description: the staged program with the contract command line
    /// (<see cref="PortableUpdate"/>), the temp folder as working folder, and this process's environment.
    /// </summary>
    /// <param name="request">What to install, and where.</param>
    /// <param name="staging">The staging folder holding the new version.</param>
    /// <returns>The start information; nothing is started.</returns>
    internal ProcessStartInfo CreateStartInfo(UpdateInstallRequest request, string staging)
    {
        var info = new ProcessStartInfo(Path.Combine(staging, ProgramName))
        {
            // CreateProcess, not the shell: the arguments arrive exactly as listed, and the environment is inherited as is.
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        };

        // ArgumentList quotes each argument for the Windows command line, so paths with spaces arrive whole.
        string[] arguments =
        [
            PortableUpdate.ApplySwitch,
            PortableUpdate.TargetSwitch, Path.TrimEndingDirectorySeparator(request.InstallDirectory),
            PortableUpdate.WaitForSwitch, string.Create(CultureInfo.InvariantCulture, $"{processId}:{processStartTicks}"),
            PortableUpdate.ResultSwitch, request.ResultPath,
            PortableUpdate.LogSwitch, request.LogPath,
        ];
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    /// <summary>Starts the helper.</summary>
    /// <param name="info">The process description from <see cref="CreateStartInfo"/>.</param>
    /// <returns>The running process; the caller disposes it.</returns>
    /// <exception cref="UpdateException">The program could not be started (<see cref="UpdateFailure.Installer"/>). Nothing was changed.</exception>
    private static Process Start(ProcessStartInfo info)
    {
        try
        {
            return Process.Start(info) ?? throw new UpdateException(UpdateFailure.Installer, UpdateMessages.Format(UpdateMessages.PortableHelperNotStarted, "no process"));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new UpdateException(UpdateFailure.Installer, UpdateMessages.Format(UpdateMessages.PortableHelperNotStarted, ex.Message.TrimEnd('.')), ex);
        }
    }

    /// <summary>Waits for the helper and gives its exit code.</summary>
    /// <param name="process">The running helper; disposed when it ends.</param>
    /// <returns>The exit code. On a successful update this never completes inside the app: the helper closes the app first.</returns>
    private static async Task<int> WaitForExitAsync(Process process)
    {
        using (process)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
    }

    /// <summary>Removes a folder and everything in it; failures are ignored (the next start cleans the updates folder).</summary>
    /// <param name="folder">The folder.</param>
    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next start's cleanup of the updates folder.
        }
    }
}
