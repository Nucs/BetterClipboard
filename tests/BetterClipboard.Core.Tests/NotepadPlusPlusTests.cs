using System.Text;
using BetterClipboard.Core.Cli;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.NotepadPlusPlus;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the Notepad++ integration's pure parts (CLAUDE.md §2.41): <c>session.xml</c> and <c>config.xml</c>, backup names
/// and their text, the command line a Notepad++ process was started with, the tracker that tells opened files from restored
/// ones, the captures, and the command line's filter.
/// </summary>
public sealed class NotepadPlusPlusTests
{
    /// <summary>A session shaped like Notepad++ 8.9.1's (attributes as it writes them; names and paths BC-TEST).</summary>
    private const string SessionXml =
        """
        <?xml version="1.0" encoding="UTF-8" ?>
        <NotepadPlus>
            <Session activeView="0">
                <mainView activeIndex="1">
                    <File firstVisibleLine="0" xOffset="0" scrollWidth="64" startPos="0" endPos="0" selMode="0" offset="0" wrapCount="1" lang="None (Normal Text)" encoding="-1" userReadOnly="no" filename="C:\BC-TEST\notes.md" backupFilePath="" originalFileLastModifTimestamp="1" originalFileLastModifTimestampHigh="2" tabColourId="-1" RTL="no" tabPinned="no" mapFirstVisibleDisplayLine="-1" />
                    <File encoding="-1" filename="new 3" backupFilePath="C:\Users\BC-TEST\AppData\Roaming\Notepad++\backup\new 3@2026-11-08_101530" />
                    <File encoding="1255" filename="Plans" untitleTabRenamed="yes" backupFilePath="C:\Users\BC-TEST\AppData\Roaming\Notepad++\backup\Plans@2026-11-07_090000">
                        <Mark line="3" />
                    </File>
                </mainView>
                <subView activeIndex="0">
                    <File encoding="-1" filename="\\server\share\BC-TEST.txt" backupFilePath="C:\Users\BC-TEST\AppData\Roaming\Notepad++\backup\BC-TEST.txt@2026-11-08_110000" />
                </subView>
            </Session>
        </NotepadPlus>
        """;

    /// <summary>The session's tabs come out in view and tab order, untitled ones told apart from files.</summary>
    [Fact]
    public void Session_ReadsTabsAndBackups()
    {
        var files = NotepadPlusPlusSession.Parse(Stream(SessionXml));
        Assert.Equal(4, files.Count);
        Assert.Equal(new NotepadPlusPlusSessionFile(@"C:\BC-TEST\notes.md", null, -1, false, false, 0, 0), files[0]);
        Assert.Equal(new NotepadPlusPlusSessionFile("new 3", @"C:\Users\BC-TEST\AppData\Roaming\Notepad++\backup\new 3@2026-11-08_101530", -1, true, false, 0, 1), files[1]);
        Assert.True(files[2].IsUntitled);
        Assert.True(files[2].IsRenamed);
        Assert.Equal(1255, files[2].Encoding);
        Assert.False(files[3].IsUntitled);
        Assert.Equal(1, files[3].View);
    }

    /// <summary>Half-written, foreign or hostile files are refused with a <see cref="FormatException"/>.</summary>
    /// <param name="xml">The file's text.</param>
    [Theory]
    [InlineData("<NotepadPlus><Session><mainView><File filename=\"new 1\"")]
    [InlineData("<NotepadPlus><GUIConfigs /></NotepadPlus>")]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e \"boom\">]><NotepadPlus><Session/></NotepadPlus>")]
    [InlineData("")]
    public void Session_RefusesWhatIsNotASession(string xml) =>
        Assert.Throws<FormatException>(() => NotepadPlusPlusSession.Parse(Stream(xml)));

    /// <summary>Untitled names are anything that is not a path.</summary>
    /// <param name="name">A session file name.</param>
    /// <param name="untitled">Whether it is an untitled tab's name.</param>
    [Theory]
    [InlineData("new 1", true)]
    [InlineData("nouveau 12", true)]
    [InlineData("חדש 3", true)]
    [InlineData("my notes.txt", true)]
    [InlineData(@"C:\a.txt", false)]
    [InlineData(@"\\server\share\a.txt", false)]
    [InlineData("C:", false)]
    [InlineData("", false)]
    public void Session_UntitledNames(string name, bool untitled) => Assert.Equal(untitled, NotepadPlusPlusSession.IsUntitledName(name));

    /// <summary>The settings the status line explains, with Notepad++'s defaults for what is missing.</summary>
    [Fact]
    public void Config_ReadsSessionAndBackupSettings()
    {
        const string Config =
            """
            <NotepadPlus><GUIConfigs>
              <GUIConfig name="RememberLastSession">no</GUIConfig>
              <GUIConfig name="Backup" action="0" useCustumDir="no" dir="" isSnapshotMode="yes" snapshotBackupTiming="15000" />
            </GUIConfigs></NotepadPlus>
            """;
        var config = NotepadPlusPlusConfig.Parse(Stream(Config));
        Assert.False(config.RememberSession);
        Assert.True(config.SnapshotMode);
        Assert.Equal(TimeSpan.FromSeconds(15), config.BackupInterval);
        Assert.False(config.KeepsUnsavedTabs);

        Assert.Equal(NotepadPlusPlusConfig.Defaults, NotepadPlusPlusConfig.Parse(Stream("<NotepadPlus><GUIConfigs/></NotepadPlus>")));
        var snapshotOff = NotepadPlusPlusConfig.Parse(Stream("<NotepadPlus><GUIConfigs><GUIConfig name=\"Backup\" isSnapshotMode=\"no\" /></GUIConfigs></NotepadPlus>"));
        Assert.False(snapshotOff.KeepsUnsavedTabs);
        Assert.Throws<FormatException>(() => NotepadPlusPlusConfig.Parse(Stream("<NotepadPlus><GUIConfigs>")));
    }

    /// <summary>Backup names split at the last '@' into the tab's name and a valid stamp; anything else is not a backup.</summary>
    /// <param name="fileName">The file name.</param>
    /// <param name="tab">The tab's name, or <see langword="null"/> when it is not a backup.</param>
    [Theory]
    [InlineData("new 3@2026-11-08_101530", "new 3")]
    [InlineData("a@b@2026-01-31_235959", "a@b")]
    [InlineData("notes.txt@2026-11-08_101530", "notes.txt")]
    [InlineData("new 3@2026-13-08_101530", null)]
    [InlineData("new 3@2026-11-08_1015", null)]
    [InlineData("@2026-11-08_101530", null)]
    [InlineData("new 3", null)]
    [InlineData("new 3@2026-11-08_101530.tmp", null)]
    public void Backup_Names(string fileName, string? tab)
    {
        bool parsed = NotepadPlusPlusBackup.TryParseName(fileName, out var name, out var stamp);
        Assert.Equal(tab is not null, parsed);
        if (tab is not null)
        {
            Assert.Equal(tab, name);
            Assert.Equal(17, stamp.Length);
        }
    }

    /// <summary>Backups decode by BOM, by the session's code page, as UTF-8, or as the system's ANSI code page.</summary>
    [Fact]
    public void Backup_DecodesEveryEncodingNotepadPlusPlusWrites()
    {
        const string Text = "BC-TEST שלום café";
        Assert.Equal(Text, NotepadPlusPlusBackup.DecodeText(Encoding.UTF8.GetBytes(Text), -1, 1252));
        Assert.Equal(Text, NotepadPlusPlusBackup.DecodeText([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Text)], -1, 1252));
        Assert.Equal(Text, NotepadPlusPlusBackup.DecodeText([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Text)], -1, 1252));
        Assert.Equal(Text, NotepadPlusPlusBackup.DecodeText([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(Text)], -1, 1252));

        var hebrew = System.Text.CodePagesEncodingProvider.Instance.GetEncoding(1255)!;
        Assert.Equal("BC-TEST שלום", NotepadPlusPlusBackup.DecodeText(hebrew.GetBytes("BC-TEST שלום"), 1255, 1252));

        // Not UTF-8 and no code page in the session: the system's ANSI code page.
        var western = System.Text.CodePagesEncodingProvider.Instance.GetEncoding(1252)!;
        Assert.Equal("BC-TEST café", NotepadPlusPlusBackup.DecodeText(western.GetBytes("BC-TEST café"), -1, 1252));
        Assert.Equal(string.Empty, NotepadPlusPlusBackup.DecodeText([], -1, 1252));
    }

    /// <summary>The files a Notepad++ process was started to open, the way Notepad++ splits its command line.</summary>
    /// <param name="commandLine">The process's command line.</param>
    /// <param name="expected">The files, separated by '|'; empty for none.</param>
    [Theory]
    [InlineData("\"C:\\Program Files\\Notepad++\\notepad++.exe\" \"C:\\BC-TEST\\a b.txt\"", "C:\\BC-TEST\\a b.txt")]
    [InlineData("\"C:\\Program Files\\Notepad++\\notepad++.exe\" -multiInst -nosession C:\\BC-TEST\\a.txt \"D:\\x y\\b.log\"", "C:\\BC-TEST\\a.txt|D:\\x y\\b.log")]
    [InlineData("\"C:\\Program Files\\Notepad++\\notepad++.exe\" -n42 -lpython C:\\BC-TEST\\script.py", "C:\\BC-TEST\\script.py")]
    [InlineData("\"C:\\Program Files\\Notepad++\\notepad++.exe\" -settingsDir=\"C:\\BC-TEST settings\" C:\\BC-TEST\\a.txt", "C:\\BC-TEST\\a.txt")]
    [InlineData("\"C:\\Program Files\\Notepad++\\notepad++.exe\" -notepadStyleCmdline -z \"C:\\WINDOWS\\system32\\NOTEPAD.EXE\" C:\\BC-TEST\\my folder\\my file.txt", "C:\\BC-TEST\\my folder\\my file.txt")]
    [InlineData("\"C:\\Program Files\\Notepad++\\notepad++.exe\" -openSession C:\\BC-TEST\\session.xml", "")]
    [InlineData("\"C:\\Program Files\\Notepad++\\notepad++.exe\" -quickPrint C:\\BC-TEST\\a.txt", "")]
    [InlineData("\"C:\\Program Files\\Notepad++\\notepad++.exe\" -notepadStyleCmdline /p C:\\BC-TEST\\a.txt", "")]
    [InlineData("\"C:\\Program Files\\Notepad++\\notepad++.exe\" relative.txt \"\\\\server\\share\\BC-TEST.txt\"", "\\\\server\\share\\BC-TEST.txt")]
    [InlineData("C:\\npp\\notepad++.exe C:\\BC-TEST\\a.txt c:\\bc-test\\A.TXT", "C:\\BC-TEST\\a.txt")]
    [InlineData("\"C:\\Program Files\\Notepad++\\notepad++.exe\"", "")]
    [InlineData("", "")]
    public void CommandLine_FilesToOpen(string commandLine, string expected) =>
        Assert.Equal(expected.Length == 0 ? [] : expected.Split('|'), NotepadPlusPlusCommandLine.FilesToOpen(commandLine));

    /// <summary>
    /// Nothing is learned while the window is hidden (the session loads); the first visible look is a baseline — restored
    /// documents are not openings, except the files the process was started with.
    /// </summary>
    [Fact]
    public void Tracker_BaselineAndStartupFiles()
    {
        var paths = new Dictionary<long, string?> { [1] = @"C:\BC-TEST\restored.txt", [2] = "new 1", [3] = @"C:\BC-TEST\clicked.txt" };
        var tracker = new NotepadPlusPlusOpenTracker([@"C:\BC-TEST\clicked.txt"]);
        Assert.Empty(tracker.Observe([1, 2], 1, windowVisible: false, id => paths[id]));
        Assert.False(tracker.HasBaseline);

        Assert.Equal([@"C:\BC-TEST\clicked.txt"], tracker.Observe([1, 2, 3], 3, windowVisible: true, id => paths[id]));
        Assert.True(tracker.HasBaseline);
        Assert.Equal(3, tracker.OpenCount);
    }

    /// <summary>
    /// The status line's count is files, each once: not untitled tabs, not the empty document Notepad++ keeps in its hidden
    /// second view (which its own count includes: "6 documents open" over 5 tabs in the live check), not a document cloned
    /// into both views, not one path reported twice in another case.
    /// </summary>
    [Fact]
    public void Tracker_OpenFileCount_CountsFilesOnce()
    {
        var paths = new Dictionary<long, string?>
        {
            [1] = @"C:\BC-TEST\a.txt",
            [2] = "new 2",
            [3] = @"C:\BC-TEST\b.txt",
            [4] = @"c:\bc-test\A.TXT",
            [5] = "new 1",
        };
        var tracker = new NotepadPlusPlusOpenTracker([]);

        // Id 1 is cloned into the second view, so it is listed twice; id 5 is the hidden second view's placeholder.
        tracker.Observe([1, 2, 3, 4, 1, 5], 1, windowVisible: true, id => paths[id]);

        Assert.Equal(5, tracker.OpenCount);
        Assert.Equal(2, tracker.OpenFileCount);
    }

    /// <summary>
    /// After the baseline: a new document is an opening, an untitled one is not, switching tabs opens nothing, a Save As of
    /// the document in front counts, and a file closed and opened again counts again (also at a reused address).
    /// </summary>
    [Fact]
    public void Tracker_OpeningsAfterTheBaseline()
    {
        var paths = new Dictionary<long, string?> { [1] = @"C:\BC-TEST\a.txt", [2] = @"C:\BC-TEST\b.txt" };
        int reads = 0;
        string? Resolve(long id)
        {
            reads++;
            return paths.GetValueOrDefault(id);
        }

        var tracker = new NotepadPlusPlusOpenTracker([]);
        Assert.Empty(tracker.Observe([1, 2], 1, true, Resolve));

        paths[3] = @"C:\BC-TEST\c.txt";
        paths[4] = "new 2";
        Assert.Equal([@"C:\BC-TEST\c.txt"], tracker.Observe([1, 2, 3, 4], 3, true, Resolve));

        // Switching to b: its path is read again (it is in front) but nothing changed.
        reads = 0;
        Assert.Empty(tracker.Observe([1, 2, 3, 4], 2, true, Resolve));
        Assert.Equal(1, reads);

        // "new 2" saved as a file.
        paths[4] = @"C:\BC-TEST\saved.txt";
        Assert.Equal([@"C:\BC-TEST\saved.txt"], tracker.Observe([1, 2, 3, 4], 4, true, Resolve));

        // a closed, then opened again at the same address.
        Assert.Empty(tracker.Observe([2, 3, 4], 2, true, Resolve));
        Assert.Equal([@"C:\BC-TEST\a.txt"], tracker.Observe([1, 2, 3, 4], 1, true, Resolve));

        // Several at once, one of them cloned into the second view (its id twice).
        paths[5] = @"C:\BC-TEST\e.txt";
        paths[6] = @"C:\BC-TEST\f.txt";
        Assert.Equal([@"C:\BC-TEST\e.txt", @"C:\BC-TEST\f.txt"], tracker.Observe([1, 2, 3, 4, 5, 6, 5], 6, true, Resolve));
    }

    /// <summary>A tab's capture and a file's capture carry the integration's origin, source and title.</summary>
    [Fact]
    public void Captures_CarryOriginSourceAndTitle()
    {
        var tab = NotepadPlusPlusSources.TabCapture("BC-TEST\nline two\n", "new 3", TestData.Now, @"C:\npp\notepad++.exe");
        Assert.Equal(ClipOrigin.NotepadPlusPlusTab, tab.Origin);
        Assert.Equal("new 3", tab.SourceTitle);
        Assert.Equal("notepad++", tab.Source!.ProcessName);
        Assert.Equal(TestData.Now, tab.CapturedAtUtc);
        Assert.Equal("BC-TEST\nline two\n", Content.UnicodeTextCodec.Decode(tab.Find(ClipFormatNames.UnicodeText)!.Data));

        var file = NotepadPlusPlusSources.FileCapture(@"C:\BC-TEST\a.txt", TestData.Now, null);
        Assert.Equal(ClipOrigin.NotepadPlusPlusFile, file.Origin);
        Assert.Equal(NotepadPlusPlusSources.DisplayName, file.Source!.DisplayName);
        Assert.Throws<ArgumentException>(() => NotepadPlusPlusSources.FileCapture(" ", TestData.Now, null));
    }

    /// <summary>The command line's filter names and origin names.</summary>
    [Fact]
    public void CommandLine_FilterAndOrigins()
    {
        foreach (var name in new[] { "notepad++", "NPP", "notepadpp" })
        {
            Assert.True(CliCommandProcessor.TryParseFilter(name, out var filter));
            Assert.Equal(ClipFilter.NotepadPlusPlus, filter);
        }

        var tab = CliCommandProcessor.ToItem(new ClipEntry { Origin = ClipOrigin.NotepadPlusPlusTab, SourceTitle = "new 3", EditedUtc = TestData.Now });
        Assert.Equal("notepad++-tab", tab.Origin);
        Assert.Equal("new 3", tab.SourceTitle);
        Assert.Equal(TestData.Now, tab.Edited);
        Assert.Equal("notepad++-file", CliCommandProcessor.ToItem(new ClipEntry { Origin = ClipOrigin.NotepadPlusPlusFile }).Origin);
    }

    /// <summary>A stream over a text's UTF-8 bytes.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The stream.</returns>
    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));
}
