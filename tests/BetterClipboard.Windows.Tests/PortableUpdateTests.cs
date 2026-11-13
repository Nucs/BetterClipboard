using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BetterClipboard.Core.Updates;
using BetterClipboard.Windows.Shell;
using BetterClipboard.Windows.Updates;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests for a portable copy's own update (CLAUDE.md §2.29): the helper that replaces the folder's files
/// (<see cref="PortableUpdateHelper"/>), its command line (<see cref="PortableUpdateCommand"/>), the installer that unpacks
/// the package and starts it (<see cref="PortableUpdateInstaller"/>), and what a starting copy reads about the others
/// (<see cref="RunningCopies"/>, <see cref="ProgramVersion"/>).
/// </summary>
/// <remarks>
/// Everything runs on folders in the temp folder with BC-TEST files: the "running copy" the helper closes is a child
/// process of this test (a <c>ping</c> that waits), and the app it starts again is a recorded call, never a real start.
/// </remarks>
public sealed class PortableUpdateTests : IDisposable
{
    /// <summary><c>LOAD_LIBRARY_AS_IMAGE_RESOURCE</c>: maps a DLL as an image without running it, as a program that loaded it does.</summary>
    private const uint LoadLibraryAsImageResource = 0x20;

    /// <summary>A folder with a space, a quote, an ampersand, percent signs and a non-ASCII letter, like a user's own folder.</summary>
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", $"portable {Guid.NewGuid():N} 'q' & %TEMP% \u00E9")).FullName;

    /// <summary>Child processes a test started, ended at the end.</summary>
    private readonly List<Process> children = [];

    /// <summary>The staging folder: the new version.</summary>
    private string Source => Path.Combine(root, "staging");

    /// <summary>The portable folder: the old version, plus the user's own files.</summary>
    private string Target => Path.Combine(root, "BetterClipboard-0.2.11-win-x64");

    /// <summary>Ends the child processes and removes the temp folder.</summary>
    public void Dispose()
    {
        foreach (var child in children)
        {
            try
            {
                child.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Ended already.
            }

            child.Dispose();
        }

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still in use on a slow machine; the temp folder is cleaned by Windows eventually.
        }
    }

    /// <summary>
    /// The update replaces exactly the release's files: changed ones get the new bytes, new ones appear, and files the
    /// release does not ship (the user's own, one an older release had) stay. The running copy was asked to exit, the result
    /// says the new version is in place, the program is started again from the same path, and no backup is left.
    /// </summary>
    [Fact]
    public void Run_ReplacesTheReleaseFiles_AndKeepsEverythingElse()
    {
        MakeFolders();
        var copy = StartRunningCopy();
        int exitRequests = 0;
        var started = new List<string>();
        var helper = Helper(() =>
        {
            exitRequests++;
            copy.Kill(entireProcessTree: true);
            return true;
        }, started);

        int code = helper.Run(Request(copy));

        Assert.Equal(PortableUpdateCommand.Updated, code);
        Assert.Equal(1, exitRequests);
        Assert.True(copy.HasExited);
        Assert.Equal("BC-TEST new exe", Read("BetterClipboard.exe"));
        Assert.Equal("BC-TEST new core", Read("core.dll"));
        Assert.Equal("BC-TEST new res", Read(@"sub\res.dll"));
        Assert.Equal("BC-TEST brand new", Read(@"fresh\only-new.dll"));
        Assert.Equal("BC-TEST new license", Read("LICENSE"));
        Assert.Equal("BC-TEST only the old release had this", Read("dropped.dll"));
        Assert.Equal("BC-TEST the user's own notes", Read("notes.txt"));
        Assert.Empty(Directory.GetDirectories(Target, PortableUpdate.BackupFolderPrefix + "*"));

        var result = UpdateResult.TryRead(ResultPath)!;
        Assert.True(result.Succeeded);
        Assert.Equal("0.2.12", result.Version);
        Assert.Equal(Path.Combine(Target, "BetterClipboard.exe"), Assert.Single(started));
        Assert.Contains("Replaced 5 files (1 new)", File.ReadAllText(LogPath), StringComparison.Ordinal);
    }

    /// <summary>
    /// A file another program holds open without sharing it cannot be replaced: after the retries, every file replaced
    /// before it is put back (a new one removed), the result names the file and says the previous version is back, and
    /// the previous program is started again.
    /// </summary>
    [Fact]
    public void Run_AFileHeldOpen_PutsThePreviousVersionBack()
    {
        MakeFolders();
        var started = new List<string>();
        int code;
        using (new FileStream(Path.Combine(Target, "sub", "res.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            code = Helper(() => true, started).Run(Request(EndedProcess()));
        }

        Assert.Equal(PortableUpdateCommand.RolledBack, code);
        Assert.Equal("BC-TEST old exe", Read("BetterClipboard.exe"));
        Assert.Equal("BC-TEST old core", Read("core.dll"));
        Assert.Equal("BC-TEST old res", Read(@"sub\res.dll"));
        Assert.Equal("BC-TEST old license", Read("LICENSE"));
        Assert.False(File.Exists(Path.Combine(Target, "fresh", "only-new.dll")));
        Assert.False(Directory.Exists(Path.Combine(Target, "fresh")));
        Assert.Empty(Directory.GetDirectories(Target, PortableUpdate.BackupFolderPrefix + "*"));

        var result = UpdateResult.TryRead(ResultPath)!;
        Assert.False(result.Succeeded);
        Assert.StartsWith(@"sub\res.dll could not be replaced (", result.Error, StringComparison.Ordinal);
        Assert.EndsWith("The previous version was put back.", result.Error, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(Target, "BetterClipboard.exe"), Assert.Single(started));
    }

    /// <summary>
    /// A DLL that another program loaded from the folder (ShareX loads BetterClipboard's DWriteCore.dll when the folder is on
    /// the PATH, CLAUDE.md §2.9) is renamed aside and replaced; its old copy waits in the backup folder until it is free,
    /// and the next start removes it (<see cref="PortableUpdateCommand.CleanLeftovers"/>).
    /// </summary>
    [Fact]
    public void Run_ADllLoadedElsewhere_IsReplaced_AndItsOldCopyGoesLater()
    {
        MakeFolders();
        var dll = Path.Combine(Target, "loaded.dll");
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), dll);
        File.WriteAllText(Path.Combine(Source, "loaded.dll"), "BC-TEST new loaded");
        nint module = LoadLibraryEx(dll, 0, LoadLibraryAsImageResource);
        Assert.NotEqual(0, module);
        try
        {
            Assert.Equal(PortableUpdateCommand.Updated, Helper(() => true, []).Run(Request(EndedProcess())));
            Assert.Equal("BC-TEST new loaded", Read("loaded.dll"));
            var backup = Assert.Single(Directory.GetDirectories(Target, PortableUpdate.BackupFolderPrefix + "*"));
            Assert.True(File.Exists(Path.Combine(backup, "loaded.dll")));
            Assert.Equal(0, PortableUpdateCommand.CleanLeftovers(Target));
        }
        finally
        {
            FreeLibrary(module);
        }

        Assert.Equal(1, PortableUpdateCommand.CleanLeftovers(Target));
        Assert.Empty(Directory.GetDirectories(Target, PortableUpdate.BackupFolderPrefix + "*"));
    }

    /// <summary>A folder without BetterClipboard.exe is not touched, and the running copy is not asked to exit.</summary>
    [Fact]
    public void Run_NotABetterClipboardFolder_TouchesNothing()
    {
        MakeFolders();
        File.Delete(Path.Combine(Target, "BetterClipboard.exe"));
        int exitRequests = 0;
        var started = new List<string>();

        int code = Helper(() => ++exitRequests > 0, started).Run(Request(EndedProcess()));

        Assert.Equal(PortableUpdateCommand.NotAnAppFolder, code);
        Assert.Equal(0, exitRequests);
        Assert.Empty(started);
        Assert.Equal("BC-TEST old core", Read("core.dll"));
        Assert.EndsWith("is not a BetterClipboard folder, so nothing in it was replaced.", UpdateResult.TryRead(ResultPath)!.Error, StringComparison.Ordinal);
    }

    /// <summary>A running copy that does not exit when asked is ended, then the update goes on.</summary>
    [Fact]
    public void Run_ACopyThatDoesNotExit_IsEnded()
    {
        MakeFolders();
        var copy = StartRunningCopy();
        var helper = new PortableUpdateHelper
        {
            SourceDirectory = Source,
            Version = "0.2.12",
            RequestExit = () => true,
            StartApp = _ => true,
            ExitWait = TimeSpan.FromMilliseconds(300),
            KillWait = TimeSpan.FromSeconds(10),
            FileRetry = TimeSpan.FromMilliseconds(300),
            RetryInterval = TimeSpan.FromMilliseconds(50),
        };

        Assert.Equal(PortableUpdateCommand.Updated, helper.Run(Request(copy)));
        Assert.True(copy.HasExited);
        Assert.Contains("did not exit within", File.ReadAllText(LogPath), StringComparison.Ordinal);
    }

    /// <summary>Command lines of the helper: what it accepts, and what it refuses.</summary>
    public static TheoryData<string[], bool> CommandLines => new()
    {
        { [PortableUpdate.ApplySwitch, "--target", @"C:\Apps\BC", "--wait-for", "1234:638900000000000000", "--result", @"C:\Data\updates\result.json", "--log", @"C:\Data\logs\u.log"], true },

        // Any order, and switches of a later release ignored.
        { [PortableUpdate.ApplySwitch, "--log", @"C:\l.log", "--future", "x", "--result", @"C:\r.json", "--wait-for", "1:2", "--target", @"\\server\share\BC"], true },

        { [PortableUpdate.ApplySwitch, "--target", @"C:\Apps\BC", "--wait-for", "1234:5", "--result", @"C:\r.json"], false },
        { [PortableUpdate.ApplySwitch, "--target", @"Apps\BC", "--wait-for", "1234:5", "--result", @"C:\r.json", "--log", @"C:\l.log"], false },
        { [PortableUpdate.ApplySwitch, "--target", @"C:\Apps\BC", "--wait-for", "1234", "--result", @"C:\r.json", "--log", @"C:\l.log"], false },
        { [PortableUpdate.ApplySwitch, "--target", @"C:\Apps\BC", "--wait-for", "-1:5", "--result", @"C:\r.json", "--log", @"C:\l.log"], false },
        { [PortableUpdate.ApplySwitch, "--target", @"C:\A", "--target", @"C:\B", "--wait-for", "1:5", "--result", @"C:\r.json", "--log", @"C:\l.log"], false },
        { [PortableUpdate.ApplySwitch, "--target", @"C:\Apps\BC", "--wait-for", "1:5", "--result", @"C:\r.json", "--log"], false },
        { ["--background", "--target", @"C:\Apps\BC", "--wait-for", "1:5", "--result", @"C:\r.json", "--log", @"C:\l.log"], false },
    };

    /// <summary>The helper's command line is read exactly as the contract states.</summary>
    /// <param name="args">The arguments.</param>
    /// <param name="valid">Whether they are a usable request.</param>
    [Theory]
    [MemberData(nameof(CommandLines))]
    public void TryParse_TheContract(string[] args, bool valid) =>
        Assert.Equal(valid, PortableUpdateCommand.TryParse(args, out _));

    /// <summary>
    /// The installer unpacks the verified package next to the app's data and starts its program with the contract's
    /// command line, which the helper reads back into the same request (paths with spaces and quotes arrive whole). A
    /// package without the program, or one that is not a zip, is refused with nothing left behind.
    /// </summary>
    [Fact]
    public void Installer_UnpacksThePackage_AndPassesTheContract()
    {
        var package = Path.Combine(root, "BetterClipboard-0.2.12-win-x64.zip");
        MakeZip(package, ("BetterClipboard.exe", "BC-TEST exe"), (@"sub/res.dll", "BC-TEST res"));
        var staging = Path.Combine(root, "updates", PortableUpdate.StagingFolderName(new SemanticVersion(0, 2, 12)));

        PortableUpdateInstaller.Unpack(package, staging, TestContext.Current.CancellationToken);
        Assert.Equal("BC-TEST res", File.ReadAllText(Path.Combine(staging, "sub", "res.dll")));

        var request = new UpdateInstallRequest(package, new string('a', 64), new SemanticVersion(0, 2, 12), Target + @"\",
            Path.Combine(root, "updates"), Path.Combine(root, "updates", "result.json"), Path.Combine(root, "logs", "betterclipboard-update.log"));
        var info = new PortableUpdateInstaller(4321, 638_900_000_000_000_000).CreateStartInfo(request, staging);
        Assert.Equal(Path.Combine(staging, "BetterClipboard.exe"), info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Equal(Path.GetTempPath(), info.WorkingDirectory);
        Assert.True(PortableUpdateCommand.TryParse(info.ArgumentList.ToArray(), out var parsed));
        Assert.Equal(new PortableUpdateRequest(Target, 4321, 638_900_000_000_000_000, request.ResultPath, request.LogPath), parsed);

        var noProgram = Path.Combine(root, "no-program.zip");
        MakeZip(noProgram, ("readme.txt", "BC-TEST"));
        var refused = Assert.Throws<UpdateException>(() => PortableUpdateInstaller.Unpack(noProgram, staging, TestContext.Current.CancellationToken));
        Assert.Equal(UpdateFailure.Installer, refused.Failure);
        Assert.False(Directory.Exists(staging));

        var notAZip = Path.Combine(root, "garbled.zip");
        File.WriteAllBytes(notAZip, Encoding.ASCII.GetBytes("BC-TEST not a zip"));
        Assert.Equal(UpdateFailure.Installer, Assert.Throws<UpdateException>(() => PortableUpdateInstaller.Unpack(notAZip, staging, TestContext.Current.CancellationToken)).Failure);
        Assert.False(Directory.Exists(staging));
    }

    /// <summary>A folder this user can write is one a portable copy may update; the check leaves nothing behind.</summary>
    [Fact]
    public void CanUpdateFolder_WritesNothingThatStays()
    {
        Assert.True(PortableUpdateInstaller.CanUpdateFolder(root));
        Assert.Empty(Directory.GetFiles(root));
        Assert.False(PortableUpdateInstaller.CanUpdateFolder(Path.Combine(root, "missing")));
        Assert.False(PortableUpdateInstaller.CanUpdateFolder(" "));
    }

    /// <summary>
    /// A program's version is read from its version resource with the build metadata dropped (what a BetterClipboard build
    /// stamps); a missing file has none.
    /// </summary>
    [Fact]
    public void ProgramVersion_ReadsTheProductVersion()
    {
        var assembly = typeof(PortableUpdateCommand).Assembly;
        var stamped = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.True(SemanticVersion.TryParse(stamped, out var expected));
        Assert.Equal(expected, ProgramVersion.Read(assembly.Location));
        Assert.Null(ProgramVersion.Read(Path.Combine(root, "missing.exe")));
        Assert.Null(ProgramVersion.Read(null));
    }

    /// <summary>The list of other copies never holds this process (it is a starting copy's view of the others).</summary>
    [Fact]
    public void RunningCopies_NeverListsThisProcess() =>
        Assert.DoesNotContain(RunningCopies.ListOthers(), copy => copy.ProcessId == Environment.ProcessId);

    /// <summary>Maps a DLL into this process (kernel32).</summary>
    /// <param name="fileName">The DLL.</param>
    /// <param name="file">Reserved, 0.</param>
    /// <param name="flags">How to map it.</param>
    /// <returns>The module, or 0.</returns>
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string fileName, nint file, uint flags);

    /// <summary>Unmaps a DLL (kernel32).</summary>
    /// <param name="module">The module.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(nint module);

    /// <summary>Where the helper writes its result in these tests.</summary>
    private string ResultPath => Path.Combine(root, "updates", "result.json");

    /// <summary>Where the helper writes its log in these tests.</summary>
    private string LogPath => Path.Combine(root, "logs", "betterclipboard-update.log");

    /// <summary>
    /// The two folders: the old version with a file the new release no longer ships and the user's own file, and the new
    /// version with a changed set and a file in a new subfolder.
    /// </summary>
    private void MakeFolders()
    {
        Write(Target, "BetterClipboard.exe", "BC-TEST old exe");
        Write(Target, "core.dll", "BC-TEST old core");
        Write(Target, @"sub\res.dll", "BC-TEST old res");
        Write(Target, "LICENSE", "BC-TEST old license");
        Write(Target, "dropped.dll", "BC-TEST only the old release had this");
        Write(Target, "notes.txt", "BC-TEST the user's own notes");

        Write(Source, "BetterClipboard.exe", "BC-TEST new exe");
        Write(Source, "core.dll", "BC-TEST new core");
        Write(Source, @"sub\res.dll", "BC-TEST new res");
        Write(Source, "LICENSE", "BC-TEST new license");
        Write(Source, @"fresh\only-new.dll", "BC-TEST brand new");
        Directory.CreateDirectory(Path.Combine(root, "updates"));
    }

    /// <summary>Builds a helper for these folders, with short waits and a recorded start.</summary>
    /// <param name="requestExit">The exit request.</param>
    /// <param name="started">Receives the program paths the helper starts.</param>
    /// <returns>The helper.</returns>
    private PortableUpdateHelper Helper(Func<bool> requestExit, List<string> started) => new()
    {
        SourceDirectory = Source,
        Version = "0.2.12",
        RequestExit = requestExit,
        StartApp = path =>
        {
            started.Add(path);
            return true;
        },
        ExitWait = TimeSpan.FromSeconds(10),
        KillWait = TimeSpan.FromSeconds(5),
        FileRetry = TimeSpan.FromMilliseconds(300),
        RetryInterval = TimeSpan.FromMilliseconds(50),
    };

    /// <summary>The request for these folders, naming a process.</summary>
    /// <param name="copy">The "running copy".</param>
    /// <returns>The request.</returns>
    private PortableUpdateRequest Request(Process copy) =>
        new(Target, copy.Id, copy.StartTime.ToUniversalTime().Ticks, ResultPath, LogPath);

    /// <summary>A stand-in for the running copy: a hidden child process that waits until it is ended.</summary>
    /// <returns>The process.</returns>
    private Process StartRunningCopy()
    {
        var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 120 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;
        children.Add(process);
        return process;
    }

    /// <summary>A process that has ended already (the copy that asked for the update is gone by the time the helper looks).</summary>
    /// <returns>The ended process; its id and start time still describe it.</returns>
    private Process EndedProcess()
    {
        var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        children.Add(process);
        _ = process.StartTime; // read while it runs or right after: the start time stays readable on the handle
        process.WaitForExit();
        return process;
    }

    /// <summary>Reads a file of the portable folder.</summary>
    /// <param name="relative">The file, relative to the folder.</param>
    /// <returns>Its text.</returns>
    private string Read(string relative) => File.ReadAllText(Path.Combine(Target, relative));

    /// <summary>Writes a file, creating its folder.</summary>
    /// <param name="folder">The folder.</param>
    /// <param name="relative">The file, relative to it.</param>
    /// <param name="text">Its text.</param>
    private static void Write(string folder, string relative, string text)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>Builds a zip from name and text pairs.</summary>
    /// <param name="path">The zip.</param>
    /// <param name="entries">Its files.</param>
    private static void MakeZip(string path, params (string Name, string Text)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }
    }
}
