using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using BetterClipboard.Core.Updates;
using BetterClipboard.Windows.Updates;
using Microsoft.Win32;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Runs alone: one test changes the process's own environment (a poisoned <c>PSModulePath</c>), which every process
/// another test starts meanwhile would inherit.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UpdateInstallerCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "Update installer (changes the process environment)";
}

/// <summary>
/// Tests for the Windows half of the update system: what Windows says about the installed copy
/// (<see cref="InstalledCopyLocator"/>) and how the new release's installer script is taken out of its package and
/// started (<see cref="ScriptUpdateInstaller"/>).
/// </summary>
/// <remarks>
/// Nothing here installs anything. The packages are small zips built in a temp folder, and their <c>install.ps1</c> is a
/// stand-in that only reports the arguments and environment it was started with. It is run by the real Windows
/// PowerShell, because the point of these tests is what really arrives there: paths with spaces, quotes and percent
/// signs, the exit code, and an environment without the caller's module path. Registry reads use a scratch key.
/// </remarks>
[Collection(UpdateInstallerCollection.Name)]
public sealed class UpdateInstallerTests : IDisposable
{
    /// <summary>
    /// A stand-in for <c>install.ps1</c> with the real script's update parameters. It writes what it received to
    /// <c>-ResultPath</c> in the result format the app reads (plus the report), and ends with exit code 7.
    /// </summary>
    private const string ReportingScript = """
        param([switch] $Update, [string] $Package, [string] $PackageSha256, [string] $InstallDir, [string] $ResultPath, [string] $LogPath)
        $report = [ordered]@{
            version       = '9.9.9'
            ok            = $false
            error         = 'BC-TEST stand-in installer'
            finishedUtc   = (Get-Date).ToUniversalTime().ToString('o')
            update        = [bool] $Update
            package       = $Package
            sha256        = $PackageSha256
            installDir    = $InstallDir
            logPath       = $LogPath
            modulePath    = [string] $env:PSModulePath
            workingFolder = [Environment]::CurrentDirectory
            canHash       = [bool] (Get-Command Get-FileHash -ErrorAction SilentlyContinue)
            edition       = [string] $PSVersionTable.PSVersion.Major
        }
        $report | ConvertTo-Json | Set-Content -LiteralPath $ResultPath -Encoding UTF8
        exit 7
        """;

    /// <summary>A plausible SHA-256 (the stand-in installer only echoes it).</summary>
    private const string Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>How long a started stand-in installer may take: Windows PowerShell starts in about a second.</summary>
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The test's own folder. Its name has a space, a quote, an ampersand, a percent pair and a letter outside ASCII:
    /// everything a user's profile path may hold that a command line could mangle.
    /// </summary>
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", $"upd {Guid.NewGuid():N} 'q' & %TEMP% \u00E9")).FullName;

    /// <summary>The scratch registry key of one test; deleted afterwards.</summary>
    private readonly string scratchKey = $@"Software\BetterClipboard-Tests\{Guid.NewGuid():N}";

    /// <summary>Removes the temp folder and the scratch registry key.</summary>
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

        Registry.CurrentUser.DeleteSubKeyTree(scratchKey, throwOnMissingSubKey: false);
    }

    /// <summary>
    /// The architecture is one of the two the releases are packaged for, in the packages' own spelling.
    /// </summary>
    [Fact]
    public void OsArchitecture_IsOneOfThePackagedOnes()
    {
        Assert.Contains(InstalledCopyLocator.OsArchitecture, new[] { "x64", "arm64" });
        Assert.EndsWith($"-win-{InstalledCopyLocator.OsArchitecture}.zip", UpdateCatalog.PackageFileName(new SemanticVersion(1, 2, 3), InstalledCopyLocator.OsArchitecture));
    }

    /// <summary>
    /// The Installed-apps entry's folder is read as text, trimmed; no key, no value, a blank value and a value of
    /// another type all mean "nothing registered" instead of a failure.
    /// </summary>
    [Fact]
    public void RegisteredInstallLocation_ReadsTheEntry_AndToleratesEverythingElse()
    {
        Assert.Null(InstalledCopyLocator.ReadRegisteredInstallLocation(scratchKey));

        using (var key = Registry.CurrentUser.CreateSubKey(scratchKey, writable: true))
        {
            Assert.Null(InstalledCopyLocator.ReadRegisteredInstallLocation(scratchKey));

            key.SetValue(InstalledCopyLocator.InstallLocationValue, @"  C:\BC-TEST\Programs\BetterClipboard  ", RegistryValueKind.String);
            Assert.Equal(@"C:\BC-TEST\Programs\BetterClipboard", InstalledCopyLocator.ReadRegisteredInstallLocation(scratchKey));

            key.SetValue(InstalledCopyLocator.InstallLocationValue, "   ", RegistryValueKind.String);
            Assert.Null(InstalledCopyLocator.ReadRegisteredInstallLocation(scratchKey));

            key.SetValue(InstalledCopyLocator.InstallLocationValue, 42, RegistryValueKind.DWord);
            Assert.Null(InstalledCopyLocator.ReadRegisteredInstallLocation(scratchKey));
        }
    }

    /// <summary>
    /// A copy counts as installed when this instance's <c>installer.json</c> names its folder (how a test instance
    /// updates a scratch copy), as a Chocolatey copy by its folder's shape, and as extracted by hand otherwise.
    /// </summary>
    /// <remarks>
    /// Run as an isolated instance, so the user's own Installed-apps entry is never read: the answers must not depend on
    /// what is installed on the PC that runs the tests.
    /// </remarks>
    [Fact]
    public void Detect_UsesTheInstallerRecordOfThisInstance()
    {
        var app = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
        var state = Path.Combine(root, "installer.json");

        Assert.Equal(UpdateInstallKind.Portable, InstalledCopyLocator.Detect(app, state, isolatedInstance: true));

        // Written like install.ps1 does under Windows PowerShell 5.1: UTF-8 with a byte order mark.
        File.WriteAllText(state, JsonSerializer.Serialize(new { version = "0.2.6", installDir = app + @"\" }), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal(UpdateInstallKind.Installer, InstalledCopyLocator.Detect(app, state, isolatedInstance: true));
        Assert.Equal(UpdateInstallKind.Portable, InstalledCopyLocator.Detect(Path.Combine(root, "other"), state, isolatedInstance: true));

        var chocolatey = Path.Combine(root, "chocolatey", "lib", "betterclipboard", "tools", "app");
        File.WriteAllText(state, JsonSerializer.Serialize(new { installDir = chocolatey }));
        Assert.Equal(UpdateInstallKind.Chocolatey, InstalledCopyLocator.Detect(chocolatey, state, isolatedInstance: true));
    }

    /// <summary>
    /// The regular instance also counts the Installed-apps entry; an isolated instance started from the very same folder
    /// does not, so it never replaces that folder under the regular instance.
    /// </summary>
    [Fact]
    public void Detect_IsolatedInstance_IgnoresTheInstalledAppsEntry()
    {
        // The folder the user's own entry names, when this PC has one; without one there is nothing to tell apart.
        var registered = InstalledCopyLocator.ReadRegisteredInstallLocation();
        Assert.SkipWhen(registered is null, "No BetterClipboard is installed for this user, so there is no Installed-apps entry to ignore.");
        Assert.SkipWhen(InstalledCopy.IsChocolateyFolder(registered!), "The registered folder has a Chocolatey shape, which is classified before any record.");

        var noRecord = Path.Combine(root, "missing-installer.json");
        Assert.Equal(UpdateInstallKind.Installer, InstalledCopyLocator.Detect(registered!, noRecord, isolatedInstance: false));
        Assert.Equal(UpdateInstallKind.Portable, InstalledCopyLocator.Detect(registered!, noRecord, isolatedInstance: true));
    }

    /// <summary>
    /// The installer script is copied out byte for byte from the package's root, whatever the case of its name, into a
    /// fixed file of the working folder (created when missing); an older script there is replaced.
    /// </summary>
    /// <param name="entryName">The script's name inside the zip.</param>
    [Theory]
    [InlineData("install.ps1")]
    [InlineData("Install.PS1")]
    public void ExtractScript_CopiesTheRootScript(string entryName)
    {
        var script = Encoding.UTF8.GetBytes("Write-Output 'BC-TEST installer'\n");
        var package = WritePackage("ok.zip", (entryName, script), ("BetterClipboard.exe", new byte[] { 1, 2, 3 }), ("tools/install.ps1", "Write-Output 'not this one'"u8.ToArray()));
        var work = Path.Combine(root, "work", "updates");

        var path = ScriptUpdateInstaller.ExtractScript(package, work, CancellationToken.None);

        Assert.Equal(Path.Combine(work, ScriptUpdateInstaller.ScriptEntryName), path);
        Assert.Equal(script, File.ReadAllBytes(path));

        // A second update overwrites the script of the first.
        var newer = WritePackage("newer.zip", ("install.ps1", "Write-Output 'BC-TEST newer'"u8.ToArray()));
        Assert.Equal(path, ScriptUpdateInstaller.ExtractScript(newer, work, CancellationToken.None));
        Assert.Equal("Write-Output 'BC-TEST newer'", File.ReadAllText(path));
    }

    /// <summary>
    /// A package the installer cannot be taken from fails as an installer problem that says the installed version is
    /// unchanged, and writes no script: no installer at the root (one in a subfolder does not count), an empty one, one
    /// over the size limit, and a file that is no zip at all.
    /// </summary>
    /// <param name="kind">Which broken package to build.</param>
    [Theory]
    [InlineData("missing")]
    [InlineData("subfolder-only")]
    [InlineData("empty")]
    [InlineData("oversized")]
    [InlineData("not-a-zip")]
    public void ExtractScript_RefusesPackagesWithoutAUsableInstaller(string kind)
    {
        string package = kind switch
        {
            "missing" => WritePackage("p.zip", ("BetterClipboard.exe", new byte[] { 1 })),
            "subfolder-only" => WritePackage("p.zip", ("tools/install.ps1", "Write-Output 1"u8.ToArray())),
            "empty" => WritePackage("p.zip", ("install.ps1", [])),
            "oversized" => WritePackage("p.zip", ("install.ps1", new byte[ScriptUpdateInstaller.MaxScriptBytes + 1])),
            _ => WriteFile("p.zip", "this is not a zip archive"u8.ToArray()),
        };
        var work = Path.Combine(root, "work");

        var ex = Assert.Throws<UpdateException>(() => ScriptUpdateInstaller.ExtractScript(package, work, CancellationToken.None));

        Assert.Equal(UpdateFailure.Installer, ex.Failure);
        Assert.Contains("unchanged", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(work, ScriptUpdateInstaller.ScriptEntryName)));
    }

    /// <summary>A cancelled update stops before the script is written, and reports the cancellation as such.</summary>
    [Fact]
    public void ExtractScript_HonorsCancellation()
    {
        var package = WritePackage("p.zip", ("install.ps1", "Write-Output 1"u8.ToArray()));
        var work = Path.Combine(root, "work");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => ScriptUpdateInstaller.ExtractScript(package, work, cancelled.Token));
        Assert.False(File.Exists(Path.Combine(work, ScriptUpdateInstaller.ScriptEntryName)));
    }

    /// <summary>
    /// The command line is the contract with every released <c>install.ps1</c>: these arguments, in this order, each
    /// one whole. The process has no window, does not use the shell, works in the temp folder (never the install
    /// folder, which is renamed) and gets no module path from the app.
    /// </summary>
    [Fact]
    public void StartInfo_IsTheContractCommandLine()
    {
        var request = Request(Path.Combine(root, "pkg", "BetterClipboard-9.9.9-win-x64.zip"), installDirectory: Path.Combine(root, "Programs", "BetterClipboard") + @"\");
        var script = Path.Combine(request.WorkDirectory, "install.ps1");

        var info = new ScriptUpdateInstaller().CreateStartInfo(request, script);

        Assert.Equal(ScriptUpdateInstaller.DefaultPowerShellPath, info.FileName);
        Assert.Equal(
            new[]
            {
                "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", script,
                "-Update",
                "-Package", request.PackagePath,
                "-PackageSha256", Sha256,
                "-InstallDir", Path.Combine(root, "Programs", "BetterClipboard"),
                "-ResultPath", request.ResultPath,
                "-LogPath", request.LogPath,
            },
            info.ArgumentList);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.False(info.RedirectStandardOutput || info.RedirectStandardError || info.RedirectStandardInput);
        Assert.Equal(Path.GetTempPath(), info.WorkingDirectory);
        Assert.DoesNotContain(info.Environment.Keys, name => string.Equals(name, ScriptUpdateInstaller.ModulePathVariable, StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith(@"\WindowsPowerShell\v1.0\powershell.exe", info.FileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// End to end through the real Windows PowerShell: the script taken from the package runs in update mode and
    /// receives every path whole (spaces, a quote, an ampersand, percent signs, a non-ASCII letter), its exit code
    /// comes back, it works in the temp folder, and a module path the app inherited from another PowerShell does not
    /// reach it — so its own modules load.
    /// </summary>
    /// <returns>A task completing when the stand-in installer ended and was checked.</returns>
    [Fact]
    public async Task Start_RunsThePackagesScript_WithEveryArgumentWhole()
    {
        var package = WritePackage("BetterClipboard-9.9.9-win-x64.zip", ("install.ps1", Encoding.ASCII.GetBytes(ReportingScript)), ("BetterClipboard.exe", new byte[] { 1 }));
        var request = Request(package, installDirectory: Path.Combine(root, "Programs", "Better Clipboard") + @"\");
        var poison = Path.Combine(root, "BC-TEST poisoned modules");
        var previous = Environment.GetEnvironmentVariable(ScriptUpdateInstaller.ModulePathVariable);

        // What an app started from PowerShell 7 looks like to its children.
        Environment.SetEnvironmentVariable(ScriptUpdateInstaller.ModulePathVariable, poison);
        int exitCode;
        try
        {
            var run = await new ScriptUpdateInstaller().StartAsync(request, TestContext.Current.CancellationToken);
            exitCode = await run.ExitCode.WaitAsync(ScriptTimeout, TestContext.Current.CancellationToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ScriptUpdateInstaller.ModulePathVariable, previous);
        }

        Assert.Equal(7, exitCode);
        using var report = JsonDocument.Parse(File.ReadAllText(request.ResultPath));
        var json = report.RootElement;
        Assert.True(json.GetProperty("update").GetBoolean());
        Assert.Equal(package, json.GetProperty("package").GetString());
        Assert.Equal(Sha256, json.GetProperty("sha256").GetString());
        Assert.Equal(Path.Combine(root, "Programs", "Better Clipboard"), json.GetProperty("installDir").GetString());
        Assert.Equal(request.LogPath, json.GetProperty("logPath").GetString());
        Assert.Equal("5", json.GetProperty("edition").GetString());
        Assert.True(json.GetProperty("canHash").GetBoolean());
        Assert.DoesNotContain("BC-TEST poisoned modules", json.GetProperty("modulePath").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(json.GetProperty("workingFolder").GetString()!)),
            ignoreCase: true);

        // The same file is what the app reads to tell the user why an update failed.
        var result = UpdateResult.TryRead(request.ResultPath);
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.Equal("9.9.9", result.Version);
        Assert.Equal("BC-TEST stand-in installer", result.Error);
    }

    /// <summary>
    /// Without Windows PowerShell nothing is started and the failure says so; the script was extracted, which changes
    /// nothing outside the working folder.
    /// </summary>
    /// <returns>A task completing when the refusal was checked.</returns>
    [Fact]
    public async Task Start_WithoutPowerShell_FailsAsAnInstallerProblem()
    {
        var package = WritePackage("p.zip", ("install.ps1", Encoding.ASCII.GetBytes(ReportingScript)));
        var request = Request(package, installDirectory: Path.Combine(root, "Programs", "BetterClipboard"));
        var installer = new ScriptUpdateInstaller(Path.Combine(root, "missing", "powershell.exe"));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => installer.StartAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(UpdateFailure.Installer, ex.Failure);
        Assert.Contains("Windows PowerShell was not found", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(request.ResultPath));
    }

    /// <summary>A cancelled update starts no installer.</summary>
    /// <returns>A task completing when the cancellation was checked.</returns>
    [Fact]
    public async Task Start_CancelledBeforehand_StartsNothing()
    {
        var package = WritePackage("p.zip", ("install.ps1", Encoding.ASCII.GetBytes(ReportingScript)));
        var request = Request(package, installDirectory: Path.Combine(root, "Programs", "BetterClipboard"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ScriptUpdateInstaller().StartAsync(request, cancelled.Token));

        Assert.False(File.Exists(request.ResultPath));
    }

    /// <summary>
    /// For a copy in a folder only administrators may change (Program Files, where one with UI access lives, CLAUDE.md §2.63)
    /// the same contract command line goes to the same Windows PowerShell, started by the shell with the <c>runas</c> verb
    /// (one consent prompt): no window, the temp folder as working folder, and every argument whole once Windows splits the
    /// line again - also the test folder's space, quote, ampersand, percent signs and non-ASCII letter.
    /// </summary>
    [Fact]
    public void ElevatedStartInfo_IsTheContractCommandLine_ThroughTheShell()
    {
        var request = Request(Path.Combine(root, "pkg", "BetterClipboard-9.9.9-win-x64.zip"), installDirectory: Path.Combine(root, "Program Files", "BetterClipboard") + @"\");
        var script = Path.Combine(request.WorkDirectory, "install.ps1");
        var installer = new ScriptUpdateInstaller();

        var elevated = installer.CreateElevatedStartInfo(request, script);
        var plain = installer.CreateStartInfo(request, script);

        Assert.Equal(ScriptUpdateInstaller.DefaultPowerShellPath, elevated.FileName);
        Assert.True(elevated.UseShellExecute);
        Assert.Equal("runas", elevated.Verb);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, elevated.WindowStyle);
        Assert.Equal(Path.GetTempPath(), elevated.WorkingDirectory);
        Assert.Empty(elevated.ArgumentList);
        Assert.Equal(plain.ArgumentList, Split(elevated.Arguments));
    }

    /// <summary>
    /// The shell takes one command line, joined here with the rules <c>ProcessStartInfo.ArgumentList</c> uses: whatever the
    /// argument holds - spaces, tabs, quotes, backslashes before a quote or at the end, nothing at all - Windows' own
    /// splitter (<c>CommandLineToArgvW</c>, the rules PowerShell's -File reads by) gives every argument back whole.
    /// </summary>
    /// <param name="argument">One argument, between two plain ones.</param>
    [Theory]
    [InlineData("plain")]
    [InlineData(@"C:\Program Files\BetterClipboard")]
    [InlineData(@"C:\Program Files\BetterClipboard\")]
    [InlineData(@"C:\a\\")]
    [InlineData("say \"hi\"")]
    [InlineData(@"a\""b")]
    [InlineData(@"a\\""b")]
    [InlineData(@"\\server\share\x y")]
    [InlineData("tab\there")]
    [InlineData("")]
    [InlineData("\"")]
    [InlineData(@"\")]
    [InlineData("100% & 'q' \u00E9 \u05E9\u05DC\u05D5\u05DD")]
    public void JoinArguments_EveryArgumentComesBackWhole(string argument)
    {
        string[] arguments = ["-First", argument, "-Last"];

        Assert.Equal(arguments, Split(ScriptUpdateInstaller.JoinArguments(arguments)));
    }

    /// <summary>
    /// Administrator rights are needed exactly when Windows refuses this account a new file next to the install folder,
    /// where the installer puts the new version and renames the old one: a folder the account may write is updated as
    /// always, one with a deny rule for it (Program Files to an account that is not elevated) needs them, and a folder
    /// whose parent does not exist leaves the installer to say what is wrong. The probe leaves nothing behind.
    /// </summary>
    [Fact]
    public void NeedsAdministrator_OnlyWhenWindowsDeniesTheWrite()
    {
        var writable = Directory.CreateDirectory(Path.Combine(root, "writable")).FullName;
        Assert.False(ScriptUpdateInstaller.NeedsAdministrator(Path.Combine(writable, "BetterClipboard")));
        Assert.Empty(Directory.GetFileSystemEntries(writable));

        Assert.False(ScriptUpdateInstaller.NeedsAdministrator(Path.Combine(root, "missing parent", "BetterClipboard")));

        var (locked, undo) = LockedFolder();
        try
        {
            Assert.True(ScriptUpdateInstaller.NeedsAdministrator(Path.Combine(locked, "BetterClipboard") + @"\"));
        }
        finally
        {
            undo();
        }

        Assert.Empty(Directory.GetFileSystemEntries(locked));
    }

    /// <summary>
    /// A copy that needs administrator rights is never handed to an installer that would then start BetterClipboard as
    /// administrator (a release before <see cref="ElevatedUpdate.MinimumVersion"/>, such as the way back from a development
    /// build): the update stops with a message that names the installer command, before any process is started.
    /// </summary>
    /// <returns>A task completing when the refusal was checked.</returns>
    [Fact]
    public async Task Start_ElevatedUpdateToAnOlderInstaller_IsRefused()
    {
        var package = WritePackage("BetterClipboard-0.2.11-win-x64.zip", ("install.ps1", Encoding.ASCII.GetBytes(ReportingScript)));
        var (locked, undo) = LockedFolder();
        UpdateException ex;
        try
        {
            var request = Request(package, Path.Combine(locked, "BetterClipboard"), new SemanticVersion(0, 2, 11));
            ex = await Assert.ThrowsAsync<UpdateException>(() => new ScriptUpdateInstaller().StartAsync(request, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(request.ResultPath));
        }
        finally
        {
            undo();
        }

        Assert.Equal(UpdateFailure.Installer, ex.Failure);
        Assert.Equal(UpdateMessages.Format(UpdateMessages.ElevatedUpdateTooOld, "0.2.11"), ex.Message);
    }

    /// <summary>
    /// An isolated instance (<c>BETTERCLIPBOARD_DATA_DIR</c>) never runs an installer as administrator: Windows would start it
    /// with the account's own environment, so its exit request and its records would reach the regular instance instead.
    /// </summary>
    /// <returns>A task completing when the refusal was checked.</returns>
    [Fact]
    public async Task Start_ElevatedUpdateOfAnIsolatedInstance_IsRefused()
    {
        var package = WritePackage("BetterClipboard-9.9.9-win-x64.zip", ("install.ps1", Encoding.ASCII.GetBytes(ReportingScript)));
        var (locked, undo) = LockedFolder();
        var previous = Environment.GetEnvironmentVariable(Core.AppPaths.DataDirectoryVariable);
        UpdateException ex;
        try
        {
            Environment.SetEnvironmentVariable(Core.AppPaths.DataDirectoryVariable, Path.Combine(root, "data dir"));
            var request = Request(package, Path.Combine(locked, "BetterClipboard"));
            ex = await Assert.ThrowsAsync<UpdateException>(() => new ScriptUpdateInstaller().StartAsync(request, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(request.ResultPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Core.AppPaths.DataDirectoryVariable, previous);
            undo();
        }

        Assert.Equal(UpdateFailure.Installer, ex.Failure);
        Assert.Contains("isolated instance", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Makes a folder in the test's folder that this account may not create files in: a deny rule for the account's own SID,
    /// which holds for an elevated administrator too (CI runs elevated), as Program Files holds against a normal one.
    /// </summary>
    /// <returns>
    /// The folder, and the action that takes the rule away again, so the test's folder can be deleted (call it in a
    /// <c>finally</c>).
    /// </returns>
    private (string Folder, Action Undo) LockedFolder()
    {
        var locked = Directory.CreateDirectory(Path.Combine(root, $"locked {Guid.NewGuid():N}"));
        var deny = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles | FileSystemRights.AppendData | FileSystemRights.WriteData,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny);
        var security = locked.GetAccessControl();
        security.AddAccessRule(deny);
        locked.SetAccessControl(security);
        return (locked.FullName, () =>
        {
            security.RemoveAccessRule(deny);
            locked.SetAccessControl(security);
        });
    }

    /// <summary>Splits a command line like Windows programs do (<c>CommandLineToArgvW</c>).</summary>
    /// <param name="commandLine">The arguments, without the program.</param>
    /// <returns>The arguments.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The line could not be split.</exception>
    private static string[] Split(string commandLine)
    {
        // A program name first: CommandLineToArgvW reads the first token by other rules (no backslash escapes).
        nint argv = CommandLineToArgvW("powershell.exe " + commandLine, out int count);
        if (argv == 0)
        {
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }

        try
        {
            return [.. Enumerable.Range(1, count - 1).Select(i => System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(argv, i * IntPtr.Size))!)];
        }
        finally
        {
            LocalFree(argv);
        }
    }

    /// <summary><c>CommandLineToArgvW</c>: the shell's reference command-line splitter.</summary>
    /// <param name="lpCmdLine">The command line.</param>
    /// <param name="pNumArgs">Receives the argument count.</param>
    /// <returns>An array of string pointers, freed with <see cref="LocalFree"/>; 0 on failure.</returns>
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    /// <summary>Frees the array <see cref="CommandLineToArgvW"/> returned.</summary>
    /// <param name="hMem">The array.</param>
    /// <returns>0 on success.</returns>
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint hMem);

    /// <summary>Builds a request whose working, result and log paths lie in the test's folder.</summary>
    /// <param name="package">The package's path.</param>
    /// <param name="installDirectory">The folder the update would replace.</param>
    /// <param name="version">The version to install; 9.9.9 when <see langword="null"/>.</param>
    /// <returns>The request.</returns>
    private UpdateInstallRequest Request(string package, string installDirectory, SemanticVersion? version = null)
    {
        var work = Path.Combine(root, "data dir", "updates");
        return new UpdateInstallRequest(
            package,
            Sha256,
            version ?? new SemanticVersion(9, 9, 9),
            installDirectory,
            work,
            Path.Combine(work, "result.json"),
            Path.Combine(root, "data dir", "logs", "betterclipboard-update.log"));
    }

    /// <summary>Writes a zip into the test's folder.</summary>
    /// <param name="name">The zip's file name.</param>
    /// <param name="entries">The entries: name inside the zip, and content.</param>
    /// <returns>The zip's full path.</returns>
    private string WritePackage(string name, params (string Name, byte[] Content)[] entries)
    {
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "pkg")).FullName, name);
        using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            using var stream = archive.CreateEntry(entryName, CompressionLevel.Fastest).Open();
            stream.Write(content);
        }

        return path;
    }

    /// <summary>Writes a plain file into the test's folder.</summary>
    /// <param name="name">The file's name.</param>
    /// <param name="content">Its content.</param>
    /// <returns>The file's full path.</returns>
    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "pkg")).FullName, name);
        File.WriteAllBytes(path, content);
        return path;
    }
}
