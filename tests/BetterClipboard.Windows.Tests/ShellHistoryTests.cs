using System.Text;
using BetterClipboard.Core.Shells;
using BetterClipboard.Windows.Integrations;
using BetterClipboard.Windows.Shell;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests for the Pwsh tab's source on a temp folder of BC-TEST history files (never the user's real history): every
/// host's file is read, commands merge across hosts, a file being written stays readable, and a changed file is read
/// again while an unchanged one comes from the cache.
/// </summary>
public sealed class PowerShellHistorySourceTests : IDisposable
{
    private readonly string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", Guid.NewGuid().ToString("N"), "PSReadLine")).FullName;

    /// <summary>Deletes the temp folder.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
        catch (IOException)
        {
            // A scanner briefly holding the folder: the temp folder is cleaned up eventually anyway.
        }
    }

    /// <summary>
    /// Both hosts' files are read: the most recently written file's commands come first, a command typed in both
    /// shows once with the counts added, and each command names its host. A multi-line command keeps its lines.
    /// </summary>
    [Fact]
    public void ReadCommands_MergesHostsNewestFileFirst()
    {
        Write("Visual Studio Code Host_history.txt", "BC-TEST shared\r\nBC-TEST vscode only\r\n", DateTime.UtcNow.AddHours(-1));
        Write("ConsoleHost_history.txt", "BC-TEST old\r\nBC-TEST shared\r\nif (1) {`\nBC-TEST`\n}\r\nBC-TEST shared\r\n", DateTime.UtcNow);
        var source = new PowerShellHistorySource(folder);

        Assert.True(source.HasHistory());
        var commands = source.ReadCommands();
        Assert.Equal(
            ["BC-TEST shared", "if (1) {\nBC-TEST\n}", "BC-TEST old", "BC-TEST vscode only"],
            commands.Select(c => c.Text));
        Assert.Equal([3, 1, 1, 1], commands.Select(c => c.Count));
        Assert.Equal(["PowerShell", "PowerShell", "PowerShell", "PowerShell in VS Code"], commands.Select(c => c.Source));
        Assert.All(commands, c => Assert.Equal(ShellKind.PowerShell, c.Shell));
        Assert.All(commands, c => Assert.Null(c.LastSeen));
        Assert.Equal(6, source.LastRecordCount);
        Assert.Equal(2, source.LastFileCount);
    }

    /// <summary>
    /// A file another process holds open for writing (PSReadLine appends with read sharing only while it writes) is
    /// read anyway, and a command appended later shows up at the next read.
    /// </summary>
    [Fact]
    public void ReadCommands_ReadsAFileBeingWrittenAndSeesAppends()
    {
        var path = Write("ConsoleHost_history.txt", "BC-TEST first\r\n", DateTime.UtcNow);
        var source = new PowerShellHistorySource(folder);
        using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete))
        {
            Assert.Equal(["BC-TEST first"], source.ReadCommands().Select(c => c.Text));
            var more = Encoding.UTF8.GetBytes("BC-TEST second\r\n");
            writer.Write(more, 0, more.Length);
        }

        Assert.Equal(["BC-TEST second", "BC-TEST first"], source.ReadCommands().Select(c => c.Text));
    }

    /// <summary>No folder, or a folder without history files: no tab, no commands, no exception.</summary>
    [Fact]
    public void Absent_HasNoHistory()
    {
        var missing = new PowerShellHistorySource(Path.Combine(folder, "missing"));
        Assert.False(missing.HasHistory());
        Assert.Empty(missing.ReadCommands());

        File.WriteAllText(Path.Combine(folder, "not a history.txt"), "BC-TEST");
        var empty = new PowerShellHistorySource(folder);
        Assert.False(empty.HasHistory());
        Assert.Empty(empty.ReadCommands());
    }

    /// <summary>The environment variable moves the folder (isolated instances and tests), otherwise it is PSReadLine's.</summary>
    [Fact]
    public void ResolveFolder_HonorsTheOverride()
    {
        var previous = Environment.GetEnvironmentVariable(PowerShellHistorySource.FolderVariable);
        try
        {
            Environment.SetEnvironmentVariable(PowerShellHistorySource.FolderVariable, folder);
            Assert.Equal(folder, PowerShellHistorySource.ResolveFolder());
            Environment.SetEnvironmentVariable(PowerShellHistorySource.FolderVariable, null);
            Assert.EndsWith(@"Microsoft\Windows\PowerShell\PSReadLine", PowerShellHistorySource.ResolveFolder(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PowerShellHistorySource.FolderVariable, previous);
        }
    }

    /// <summary>Writes a BC-TEST history file with a given last-write time.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="content">Its text (UTF-8, no BOM, like PSReadLine).</param>
    /// <param name="lastWriteUtc">Its last-write time.</param>
    /// <returns>The path.</returns>
    private string Write(string name, string content, DateTime lastWriteUtc)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}

/// <summary>
/// Tests for the Cmd tab helper's wire format and argument checks (the attaching itself is never run in the test
/// process: attaching detaches it from its own console first).
/// </summary>
public sealed class ConsoleCommandHistoryTests
{
    /// <summary>Commands survive the trip as UTF-8 with NUL separators (Hebrew, quotes and spaces included); blanks are dropped.</summary>
    [Fact]
    public void EncodeDecode_RoundTrips()
    {
        string[] commands = ["echo BC-TEST", "dir \"C:\\BC TEST\"", "echo BC-TEST שלום", " "];
        Assert.Equal(commands[..3], ConsoleCommandHistory.Decode(ConsoleCommandHistory.Encode(commands)));
        Assert.Empty(ConsoleCommandHistory.Decode([]));
        Assert.Equal(["BC-TEST unterminated"], ConsoleCommandHistory.Decode(Encoding.UTF8.GetBytes("BC-TEST unterminated")));
    }

    /// <summary>Bad arguments end the helper with the usage code before anything is attached or written.</summary>
    [Fact]
    public void RunHelper_RefusesBadArguments()
    {
        using var output = new MemoryStream();
        Assert.Equal(ConsoleCommandHistory.ExitUsage, ConsoleCommandHistory.RunHelper([ConsoleCommandHistory.HelperSwitch], output));
        Assert.Equal(ConsoleCommandHistory.ExitUsage, ConsoleCommandHistory.RunHelper([ConsoleCommandHistory.HelperSwitch, "x"], output));
        Assert.Equal(ConsoleCommandHistory.ExitUsage, ConsoleCommandHistory.RunHelper([ConsoleCommandHistory.HelperSwitch, "0"], output));
        Assert.Equal(ConsoleCommandHistory.ExitUsage, ConsoleCommandHistory.RunHelper(["--other", "12"], output));
        Assert.Equal(0, output.Length);
    }
}
