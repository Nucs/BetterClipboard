using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using BetterClipboard.Core.Editions;
using BetterClipboard.Windows.Store;
using BetterClipboard.Windows.Updates;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests for how the Microsoft Store edition uninstalls the copy it replaces (<see cref="EditionUninstaller"/>,
/// CLAUDE.md §2.47): the check before the move, the uninstaller's command line, and how its outcome is read and proven.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here uninstalls anything. The "installed copy" is a scratch folder with a one-byte program in it, and the
/// installer script is a stand-in that reports what it was started with and then does what the test asks: remove the
/// scratch folder, refuse, or claim success while leaving the program. Both run through the real Windows PowerShell,
/// because what really arrives there is the point: paths with spaces, a quote, an ampersand, percent signs and a
/// letter outside ASCII, the exit code, and the result file as Windows PowerShell 5.1 writes it (with a byte order
/// mark).
/// </para>
/// <para>
/// The check's probe opens this account's own registry keys for writing and closes them again (the Run key, the
/// installer's Installed-apps entry, Explorer's settings, the environment): nothing is written, so it is safe on a PC
/// that has BetterClipboard installed.
/// </para>
/// </remarks>
public sealed class EditionUninstallerTests : IDisposable
{
    /// <summary>
    /// A stand-in for <c>install.ps1</c> with the real script's uninstall parameters. It writes what it received next
    /// to the result file, then acts as the file <c>bc-test-mode.txt</c> in the install folder says: <c>fail</c>
    /// reports a failure and ends with code 1; <c>keep</c> reports success and removes nothing; anything else removes
    /// the folder and reports success.
    /// </summary>
    private const string StandInScript = """
        param([switch] $Uninstall, [string] $InstallDir, [int] $KeepProcessId, [string] $ResultPath, [string] $LogPath)
        $mode = ''
        $modeFile = Join-Path $InstallDir 'bc-test-mode.txt'
        if (Test-Path -LiteralPath $modeFile) { $mode = (Get-Content -LiteralPath $modeFile -Raw).Trim() }
        $report = [ordered]@{
            uninstall     = [bool] $Uninstall
            installDir    = $InstallDir
            keepProcessId = $KeepProcessId
            logPath       = $LogPath
            scriptName    = [IO.Path]::GetFileName($PSCommandPath)
            edition       = [string] $PSVersionTable.PSVersion.Major
        }
        $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $ResultPath) 'bc-test-report.json') -Encoding UTF8
        if ($mode -eq 'fail') {
            '{"ok": false, "error": "BC-TEST uninstall refused"}' | Set-Content -LiteralPath $ResultPath -Encoding UTF8
            exit 1
        }
        if ($mode -ne 'keep') { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
        '{"ok": true}' | Set-Content -LiteralPath $ResultPath -Encoding UTF8
        exit 0
        """;

    /// <summary>
    /// The test's own folder. Its name has a space, a quote, an ampersand, a percent pair and a letter outside ASCII:
    /// everything a user's profile path may hold that a command line could mangle.
    /// </summary>
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", $"edn {Guid.NewGuid():N} 'q' & %TEMP% {(char)0xE9}")).FullName;

    /// <summary>Removes the temp folder, giving back the rights one test takes away first.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still in use on a slow machine; the temp folder is cleaned by Windows eventually.
        }
    }

    /// <summary>The app's working folder for a move, inside the test's folder.</summary>
    private string Work => Path.Combine(root, "data dir", "setup");

    /// <summary>Where the uninstaller is told to write its log.</summary>
    private string LogPath => Path.Combine(root, "data dir", "logs", "betterclipboard-edition-move.log");

    /// <summary>
    /// End to end through the real Windows PowerShell: the script is copied out under its working name and run in
    /// uninstall mode with the copy's folder, this process's id to spare, and the result and log paths, every one whole;
    /// a reported success counts once the program is really gone; the working files are removed again.
    /// </summary>
    /// <returns>A task completing when the stand-in uninstaller ended and was checked.</returns>
    [Fact]
    public async Task Uninstall_RunsTheScriptInUninstallMode_AndProvesTheCopyIsGone()
    {
        var edition = InstalledCopy("Programs", "Better Clipboard");
        string script = WriteScript();

        var result = await EditionUninstaller.UninstallAsync(edition, Work, LogPath, script, TestContext.Current.CancellationToken);

        Assert.True(result.Ok, $"{result.Failure}: {result.Detail}");
        Assert.False(Directory.Exists(edition.Directory));
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(Work, "bc-test-report.json")));
        var json = report.RootElement;
        Assert.True(json.GetProperty("uninstall").GetBoolean());
        Assert.Equal(edition.Directory, json.GetProperty("installDir").GetString());
        Assert.Equal(Environment.ProcessId, json.GetProperty("keepProcessId").GetInt32());
        Assert.Equal(LogPath, json.GetProperty("logPath").GetString());
        Assert.Equal(EditionUninstaller.WorkScriptName, json.GetProperty("scriptName").GetString());
        Assert.Equal("5", json.GetProperty("edition").GetString());

        // What was copied out and what the script reported do not stay behind.
        Assert.False(File.Exists(Path.Combine(Work, EditionUninstaller.WorkScriptName)));
        Assert.False(File.Exists(Path.Combine(Work, EditionUninstaller.ResultFileName)));

        // The stand-in the test wrote is untouched: only a copy of it ran.
        Assert.True(File.Exists(script));
    }

    /// <summary>
    /// A failure the uninstaller reports comes back as a failure, with the uninstaller's own words for the log and the
    /// window; the copy is still there.
    /// </summary>
    /// <returns>A task completing when the failure was checked.</returns>
    [Fact]
    public async Task Uninstall_AReportedFailure_ComesBackWithItsWords()
    {
        var edition = InstalledCopy("Programs", "BetterClipboard", mode: "fail");

        var result = await EditionUninstaller.UninstallAsync(edition, Work, LogPath, WriteScript(), TestContext.Current.CancellationToken);

        Assert.Equal(EditionUninstallFailure.Failed, result.Failure);
        Assert.Equal("BC-TEST uninstall refused", result.Detail);
        Assert.True(File.Exists(Path.Combine(edition.Directory, "BetterClipboard.exe")));
    }

    /// <summary>
    /// An uninstaller that says it worked is not believed while the copy's program is still in its folder: the move must
    /// not go on to delete the old history of a copy that is still installed.
    /// </summary>
    /// <returns>A task completing when the verdict was checked.</returns>
    [Fact]
    public async Task Uninstall_SuccessReportedButTheProgramIsStillThere_IsNotSuccess()
    {
        var edition = InstalledCopy("Programs", "BetterClipboard", mode: "keep");

        var result = await EditionUninstaller.UninstallAsync(edition, Work, LogPath, WriteScript(), TestContext.Current.CancellationToken);

        Assert.Equal(EditionUninstallFailure.StillInstalled, result.Failure);
        Assert.False(result.Ok);
    }

    /// <summary>Without the installer script (a broken package) nothing runs, and the result says what is missing.</summary>
    /// <returns>A task completing when the refusal was checked.</returns>
    [Fact]
    public async Task Uninstall_WithoutTheScript_SaysSo()
    {
        var edition = InstalledCopy("Programs", "BetterClipboard");

        var result = await EditionUninstaller.UninstallAsync(edition, Work, LogPath, Path.Combine(root, "missing", "install.ps1"), TestContext.Current.CancellationToken);

        Assert.Equal(EditionUninstallFailure.ScriptMissing, result.Failure);
        Assert.True(File.Exists(Path.Combine(edition.Directory, "BetterClipboard.exe")));
        Assert.False(File.Exists(Path.Combine(Work, "bc-test-report.json")));
    }

    /// <summary>
    /// Only the copy this user installed with the installer is uninstalled from here: a copy for every user and a
    /// Chocolatey copy belong to the PC's administrator, and asking for them is a programming error.
    /// </summary>
    /// <param name="kind">A kind that is not the installer copy.</param>
    /// <returns>A task completing when the refusal was checked.</returns>
    [Theory]
    [InlineData(OtherEditionKind.AllUsers)]
    [InlineData(OtherEditionKind.Chocolatey)]
    [InlineData(OtherEditionKind.Unregistered)]
    public async Task Uninstall_RefusesEveryOtherKindOfCopy(OtherEditionKind kind)
    {
        var edition = InstalledCopy("Programs", "BetterClipboard") with { Kind = kind };

        await Assert.ThrowsAsync<ArgumentException>(() => EditionUninstaller.UninstallAsync(edition, Work, LogPath, WriteScript(), TestContext.Current.CancellationToken));

        Assert.True(File.Exists(Path.Combine(edition.Directory, "BetterClipboard.exe")));
        Assert.False(Directory.Exists(Work));
    }

    /// <summary>
    /// The check before a move passes for a folder this account may change, and leaves nothing behind: no probe file in
    /// the copy's folder, no script and no result in the working folder.
    /// </summary>
    /// <returns>A task completing when the check ran.</returns>
    [Fact]
    public async Task Check_PassesForACopyThisAccountMayRemove_AndLeavesNothing()
    {
        var edition = InstalledCopy("Programs", "Better Clipboard");

        var result = await EditionUninstaller.CheckAsync(edition, Work, WriteScript(), TestContext.Current.CancellationToken);

        Assert.True(result.Ok, $"{result.Failure}: {result.Detail}");
        Assert.Equal(["BetterClipboard.exe"], Directory.GetFiles(edition.Directory).Select(Path.GetFileName));
        Assert.Empty(Directory.GetFiles(Work));
    }

    /// <summary>
    /// A copy whose folder is already gone (its Installed-apps entry outlived it) passes the check: there is nothing to
    /// remove there, and the uninstall only has entries to clean up.
    /// </summary>
    /// <returns>A task completing when the check ran.</returns>
    [Fact]
    public async Task Check_AFolderThatNoLongerExists_Passes()
    {
        var edition = new OtherEdition(OtherEditionKind.Installer, Path.Combine(root, "Programs", "gone"), "0.2.10", IsRunning: false);

        var result = await EditionUninstaller.CheckAsync(edition, Work, WriteScript(), TestContext.Current.CancellationToken);

        Assert.True(result.Ok, $"{result.Failure}: {result.Detail}");
    }

    /// <summary>
    /// A folder this account may not write to fails the check, and the result names the folder as what is in the way:
    /// the move then never starts, so nothing is closed or copied for an uninstall that could not finish.
    /// </summary>
    /// <returns>A task completing when the refusal was checked.</returns>
    [Fact]
    public async Task Check_AFolderThisAccountMayNotChange_IsNamed()
    {
        var edition = InstalledCopy("Programs", "BetterClipboard");
        var folder = new DirectoryInfo(edition.Directory);
        var deny = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles | FileSystemRights.AppendData | FileSystemRights.WriteData,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny);
        var security = folder.GetAccessControl();
        security.AddAccessRule(deny);
        folder.SetAccessControl(security);
        EditionUninstallResult result;
        try
        {
            result = await EditionUninstaller.CheckAsync(edition, Work, WriteScript(), TestContext.Current.CancellationToken);
        }
        finally
        {
            // Back to what it was, so the test's folder can be deleted.
            security.RemoveAccessRule(deny);
            folder.SetAccessControl(security);
        }

        Assert.Equal(EditionUninstallFailure.AccessDenied, result.Failure);
        Assert.Contains("folder", result.Detail!.Split(','));
    }

    /// <summary>Without the installer script the check fails at once, before PowerShell is started.</summary>
    /// <returns>A task completing when the refusal was checked.</returns>
    [Fact]
    public async Task Check_WithoutTheScript_SaysSo()
    {
        var edition = InstalledCopy("Programs", "BetterClipboard");

        var result = await EditionUninstaller.CheckAsync(edition, Work, Path.Combine(root, "missing", "install.ps1"), TestContext.Current.CancellationToken);

        Assert.Equal(EditionUninstallFailure.ScriptMissing, result.Failure);
        Assert.False(Directory.Exists(Work));
    }

    /// <summary>
    /// An installed copy that is gone is not "still installed", one whose program is left is, and a copy without an
    /// install record never is: that is what proves an uninstall.
    /// </summary>
    [Fact]
    public void IsStillInstalled_FollowsTheProgramInTheFolder()
    {
        var edition = InstalledCopy("Programs", "BetterClipboard");

        Assert.True(OtherEditionLocator.IsStillInstalled(edition));
        Assert.False(OtherEditionLocator.IsStillInstalled(edition with { Kind = OtherEditionKind.Unregistered }));

        Directory.Delete(edition.Directory, recursive: true);

        Assert.False(OtherEditionLocator.IsStillInstalled(edition));
        Assert.False(OtherEditionLocator.IsStillInstalled(edition with { Kind = OtherEditionKind.Chocolatey }));
    }

    /// <summary>
    /// The real <c>install.ps1</c> accepts the uninstall's parameters together: they are all members of its uninstall
    /// parameter set. Asked of Windows PowerShell with <c>Get-Command</c>, which reads a script's parameter block
    /// without running the script, so nothing is uninstalled. Guards the fault of the first live move, where the two
    /// report parameters belonged to the install set only and PowerShell refused the whole command line.
    /// </summary>
    /// <returns>A task completing when the parameter set was read and checked.</returns>
    [Fact]
    public async Task InstallScript_TakesTheUninstallParametersTogether()
    {
        string? script = FindRepositoryFile(EditionUninstaller.ScriptName);
        Assert.SkipWhen(script is null, "The tests do not run from a checkout of the repository, so there is no install.ps1 to ask.");

        var start = new ProcessStartInfo(ScriptUpdateInstaller.DefaultPowerShellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (string argument in (string[])["-NoLogo", "-NoProfile", "-NonInteractive", "-Command"])
        {
            start.ArgumentList.Add(argument);
        }

        // The path travels in a variable, so no character of it can be read as PowerShell.
        start.ArgumentList.Add("((Get-Command -Name $env:BC_TEST_SCRIPT).ParameterSets | Where-Object { $_.Name -eq 'Uninstall' }).Parameters.Name -join ','");
        start.Environment["BC_TEST_SCRIPT"] = script;
        start.Environment.Remove(ScriptUpdateInstaller.ModulePathVariable);
        using var process = Process.Start(start)!;
        string output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        string[] accepted = output.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(accepted);
        Assert.All(EditionUninstaller.UninstallParameters, name => Assert.Contains(name, accepted));
    }

    /// <summary>Finds a file at the root of the repository the tests were built from.</summary>
    /// <param name="name">The file's name.</param>
    /// <returns>Its full path, or <see langword="null"/> when no folder above the test binaries holds the solution and the file.</returns>
    private static string? FindRepositoryFile(string name)
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "BetterClipboard.sln")))
            {
                string path = Path.Combine(folder.FullName, name);
                return File.Exists(path) ? path : null;
            }
        }

        return null;
    }

    /// <summary>Creates a scratch "installed copy": a folder with a one-byte program, and the stand-in's mode file.</summary>
    /// <param name="parent">A folder name below the test's folder.</param>
    /// <param name="name">The copy's folder name.</param>
    /// <param name="mode">What the stand-in uninstaller does (<c>fail</c>, <c>keep</c>); <see langword="null"/> to remove the folder.</param>
    /// <returns>The copy, as the Store edition would have found it.</returns>
    private OtherEdition InstalledCopy(string parent, string name, string? mode = null)
    {
        string folder = Directory.CreateDirectory(Path.Combine(root, parent, name)).FullName;
        File.WriteAllBytes(Path.Combine(folder, "BetterClipboard.exe"), [1]);
        if (mode is not null)
        {
            File.WriteAllText(Path.Combine(folder, "bc-test-mode.txt"), mode, Encoding.ASCII);
        }

        return new OtherEdition(OtherEditionKind.Installer, folder, "0.2.10", IsRunning: false);
    }

    /// <summary>Writes the stand-in installer script into the test's folder.</summary>
    /// <returns>Its full path.</returns>
    private string WriteScript()
    {
        string path = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "package")).FullName, EditionUninstaller.ScriptName);
        File.WriteAllText(path, StandInScript, Encoding.ASCII);
        return path;
    }
}
