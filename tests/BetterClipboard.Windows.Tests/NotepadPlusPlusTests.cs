using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Documents;
using BetterClipboard.Core.NotepadPlusPlus;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Storage;
using BetterClipboard.Windows.Integrations;
using BetterClipboard.Windows.Interop;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests for <see cref="NotepadPlusPlusTabWatcher"/> on a temp folder standing in for Notepad++'s settings folder (CLAUDE.md
/// §2.41): only the session's untitled tabs, read once they rest or at the latest after the longest wait, renames followed,
/// closed tabs forgotten. A manual clock decides "rested"; real folder notifications decide "changed".
/// </summary>
public sealed class NotepadPlusPlusTabWatcherTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", Guid.NewGuid().ToString("N"));
    private readonly string data;
    private readonly string backup;
    private readonly FakeTarget target = new();
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
    private int stamps;

    /// <summary>Creates the stand-in settings folder with its backup folder.</summary>
    public NotepadPlusPlusTabWatcherTests()
    {
        data = Directory.CreateDirectory(Path.Combine(root, "Notepad++")).FullName;
        backup = Directory.CreateDirectory(Path.Combine(data, "backup")).FullName;
    }

    /// <summary>Deletes the temp tree.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>
    /// The first read hands over the session's untitled tabs only — not a saved file's backup, not a backup the session no
    /// longer names — with their names and texts, as an import.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Start_ReadsTheSessionsUntitledTabs()
    {
        var new1 = WriteBackup("new 1", "BC-TEST first tab");
        var plans = WriteBackup("Plans", "BC-TEST renamed tab");
        var saved = WriteBackup("notes.txt", "BC-TEST unsaved changes of a saved file");
        WriteBackup("new 9", "BC-TEST orphan");
        WriteSession(("new 1", new1), ("Plans", plans), (@"C:\BC-TEST\notes.txt", saved));

        using var watcher = CreateWatcher();
        await watcher.StartAsync(isImport: true);
        var recorded = target.Recorded.ToArray();
        Assert.Equal(2, recorded.Length);
        Assert.All(recorded, r => Assert.True(r.IsImport));
        Assert.Equal(["BC-TEST first tab", "BC-TEST renamed tab"], recorded.Select(r => r.Version.Text).Order());
        Assert.Contains(recorded, r => r.Version.TabName == "Plans" && r.Version.Key == Path.GetFullPath(plans));
        Assert.Equal(2, watcher.TabCount);
    }

    /// <summary>
    /// A change is not read on the spot: only once the tab rested for the quiet time — and a tab that keeps changing is read at
    /// the latest after the longest wait.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Change_IsReadOnceTheTabRests()
    {
        var new1 = WriteBackup("new 1", "BC-TEST v1");
        WriteSession(("new 1", new1));
        using var watcher = CreateWatcher();
        await watcher.StartAsync(isImport: false);
        target.Recorded.Clear();

        File.WriteAllText(new1, "BC-TEST v2");
        await WaitUntilAsync(() => watcher.PendingCount == 1);
        await watcher.ProcessDueAsync();
        Assert.Empty(target.Recorded);

        clock.Advance(watcher.QuietTime);
        await watcher.ProcessDueAsync();
        var read = Assert.Single(target.Recorded);
        Assert.Equal("BC-TEST v2", read.Version.Text);
        Assert.False(read.IsImport);
        target.Recorded.Clear();

        // Saves every 20 s for longer than the longest wait: read once, at the longest wait.
        for (int i = 0; i < 20 && target.Recorded.IsEmpty; i++)
        {
            File.WriteAllText(new1, $"BC-TEST typing {i}");
            await WaitUntilAsync(() => watcher.PendingCount == 1);
            clock.Advance(TimeSpan.FromSeconds(20));
            await watcher.ProcessDueAsync();
        }

        var late = Assert.Single(target.Recorded);
        Assert.StartsWith("BC-TEST typing", late.Version.Text);
    }

    /// <summary>A renamed tab (Notepad++ renames its backup, keeping the stamp) is followed at once, with its new name.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Rename_FollowsTheBackup()
    {
        var new1 = WriteBackup("new 1", "BC-TEST rename me");
        WriteSession(("new 1", new1));
        using var watcher = CreateWatcher();
        await watcher.StartAsync(isImport: false);

        var renamed = Path.Combine(backup, "Ideas" + Path.GetFileName(new1)[Path.GetFileName(new1).LastIndexOf('@')..]);
        File.Move(new1, renamed);
        await WaitUntilAsync(() => !target.Renames.IsEmpty);
        var rename = Assert.Single(target.Renames);
        Assert.Equal((Path.GetFullPath(new1), Path.GetFullPath(renamed), "Ideas"), rename);
    }

    /// <summary>
    /// A session rewritten without a tab (closed, or saved as a file) forgets it; a tab new in the session is read once it
    /// rests.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Session_ClosedTabsGoNewTabsAreRead()
    {
        var new1 = WriteBackup("new 1", "BC-TEST stays");
        var new2 = WriteBackup("new 2", "BC-TEST closed");
        WriteSession(("new 1", new1), ("new 2", new2));
        using var watcher = CreateWatcher();
        await watcher.StartAsync(isImport: false);
        target.Recorded.Clear();

        var new3 = WriteBackup("new 3", "BC-TEST added");
        WriteSession(("new 1", new1), ("new 3", new3));

        // The session's rewrite is noticed by the folder watch; the next pass reads it again.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (target.Forgotten.IsEmpty && DateTime.UtcNow < deadline)
        {
            await watcher.ProcessDueAsync();
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.Equal([Path.GetFullPath(new2)], target.Forgotten.Single());

        clock.Advance(watcher.QuietTime);
        await watcher.ProcessDueAsync();
        Assert.Contains(target.Recorded, r => r.Version.Text == "BC-TEST added");
    }

    /// <summary>A backup is read while Notepad++ could write it: the watcher's open never locks Notepad++ out.</summary>
    [Fact]
    public void Read_NeverLocksNotepadPlusPlusOut()
    {
        var new1 = WriteBackup("new 1", "BC-TEST shared");
        using var reader = new FileStream(new1, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        File.WriteAllText(new1, "BC-TEST written while read");
        File.Delete(new1);
        Assert.False(File.Exists(new1));
    }

    /// <summary>Creates the watcher over the stand-in folder (big size limit, Windows-1252 as the ANSI code page).</summary>
    /// <returns>The watcher (not started).</returns>
    private NotepadPlusPlusTabWatcher CreateWatcher() =>
        new([data], target, () => 64L * 1024 * 1024, 1252, clock, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));

    /// <summary>Writes a backup file named like Notepad++'s.</summary>
    /// <param name="tabName">The tab's name.</param>
    /// <param name="text">The text (UTF-8, no BOM, like a new document).</param>
    /// <returns>The file's path.</returns>
    private string WriteBackup(string tabName, string text)
    {
        // A valid, distinct stamp per tab (10:mm:ss), as Notepad++ writes the local time of the tab's first backup.
        int n = stamps++;
        var path = Path.Combine(backup, $"{tabName}@2026-11-08_10{n / 60:D2}{n % 60:D2}");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    /// <summary>Writes the session file, one <c>File</c> per tab.</summary>
    /// <param name="tabs">(file name, backup path) pairs.</param>
    private void WriteSession(params (string FileName, string Backup)[] tabs)
    {
        var files = string.Concat(tabs.Select(t => $"<File encoding=\"-1\" filename=\"{t.FileName}\" backupFilePath=\"{t.Backup}\" />"));
        File.WriteAllText(Path.Combine(data, "session.xml"), $"<?xml version=\"1.0\" encoding=\"UTF-8\" ?><NotepadPlus><Session activeView=\"0\"><mainView activeIndex=\"0\">{files}</mainView><subView activeIndex=\"0\" /></Session></NotepadPlus>");
    }

    /// <summary>Polls until a condition holds (10 s cap).</summary>
    /// <param name="condition">The condition.</param>
    /// <returns>A task.</returns>
    /// <exception cref="TimeoutException">It never held.</exception>
    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not become true within 10 s.");
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Records what the watcher hands over.</summary>
    private sealed class FakeTarget : INotepadPlusPlusTabTarget
    {
        /// <summary>Versions recorded, with whether they were an import.</summary>
        public ConcurrentQueue<(NotepadPlusPlusTabVersion Version, bool IsImport)> Recorded { get; } = new();

        /// <summary>Renames.</summary>
        public ConcurrentQueue<(string Old, string New, string Title)> Renames { get; } = new();

        /// <summary>Forgotten keys, per call.</summary>
        public ConcurrentQueue<string[]> Forgotten { get; } = new();

        /// <inheritdoc/>
        public Task RecordAsync(IReadOnlyList<NotepadPlusPlusTabVersion> versions, bool isImport)
        {
            foreach (var version in versions)
            {
                Recorded.Enqueue((version, isImport));
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task RenameAsync(string oldKey, string newKey, string newTitle)
        {
            Renames.Enqueue((oldKey, newKey, newTitle));
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task ForgetAsync(IReadOnlyCollection<string> keys)
        {
            Forgotten.Enqueue([.. keys]);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// Tests for <see cref="NotepadPlusPlusIntegration"/> over a real history service and a stand-in settings folder: the first
/// activation imports the tabs, a restart catches up (a small change in place, a big one as a new copy), and off forgets the
/// mappings while the entries stay.
/// </summary>
public sealed class NotepadPlusPlusIntegrationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", Guid.NewGuid().ToString("N"));
    private string data = null!;
    private string small = null!;
    private string big = null!;
    private ClipStore store = null!;
    private ClipHistoryService history = null!;

    /// <summary>Creates the settings folder with two tabs and a history over a temp store.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask InitializeAsync()
    {
        data = Directory.CreateDirectory(Path.Combine(root, "Notepad++")).FullName;
        var backup = Directory.CreateDirectory(Path.Combine(data, "backup")).FullName;
        small = Path.Combine(backup, "new 1@2026-11-08_100000");
        big = Path.Combine(backup, "new 2@2026-11-08_100100");
        File.WriteAllText(small, "BC-TEST small change: alpha beta gamma");
        File.WriteAllText(big, "BC-TEST big change: " + new string('x', 80));
        File.WriteAllText(Path.Combine(data, "session.xml"),
            $"<NotepadPlus><Session><mainView><File encoding=\"-1\" filename=\"new 1\" backupFilePath=\"{small}\" /><File encoding=\"-1\" filename=\"new 2\" backupFilePath=\"{big}\" /></mainView></Session></NotepadPlus>");
        store = new ClipStore(Path.Combine(root, "history.db"));
        store.Initialize();
        history = new ClipHistoryService(store, null, () => new CaptureRules { Retention = RetentionPolicy.Unlimited });
        history.Start();
        return ValueTask.CompletedTask;
    }

    /// <summary>Stops the history and deletes the temp tree.</summary>
    /// <returns>A task.</returns>
    public async ValueTask DisposeAsync()
    {
        await history.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>The whole life cycle: first import, restart catch-up, off.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task FirstImport_CatchUp_Off()
    {
        await using (var integration = Create())
        {
            await integration.StartAsync(keepTabs: true, recordFiles: false);
            Assert.True(integration.IsWatchingTabs);
            Assert.Equal(2, integration.TabCount);
        }

        var first = store.Query(new ClipQuery { Filter = ClipFilter.NotepadPlusPlus }, TestContext.Current.CancellationToken);
        Assert.Equal(2, first.Count);
        Assert.Equal(["new 1", "new 2"], first.Select(e => e.SourceTitle).Order());
        Assert.Equal("1", store.GetStateValue(NotepadPlusPlusIntegration.ImportedStateName));
        long smallId = first.Single(e => e.SourceTitle == "new 1").Id;

        // While BetterClipboard did not run: a small change and a big one.
        File.WriteAllText(small, "BC-TEST small change: alpha beta gamma delta");
        File.WriteAllText(big, "BC-TEST rewritten");
        await using (var integration = Create())
        {
            await integration.StartAsync(keepTabs: true, recordFiles: false);
            Assert.Equal(2, integration.TabsRecordedThisSession);
            var after = store.Query(new ClipQuery { Filter = ClipFilter.NotepadPlusPlus }, TestContext.Current.CancellationToken);
            Assert.Equal(3, after.Count);
            var updated = store.GetEntry(smallId)!;
            Assert.NotNull(updated.EditedUtc);
            Assert.Equal("BC-TEST small change: alpha beta gamma delta", UnicodeTextCodec.Decode(store.GetFormats(smallId).Single().Data));
            Assert.Equal(2, after.Count(e => e.SourceTitle == "new 2"));

            await integration.SetEnabledAsync(keepTabs: false, recordFiles: false);
            Assert.False(integration.IsWatchingTabs);
        }

        Assert.Empty(store.GetDocuments(NotepadPlusPlusSources.DocumentScope));
        Assert.True(string.IsNullOrEmpty(store.GetStateValue(NotepadPlusPlusIntegration.ImportedStateName)));
        Assert.Equal(3, store.Query(new ClipQuery { Filter = ClipFilter.NotepadPlusPlus }, TestContext.Current.CancellationToken).Count);
    }

    /// <summary>No Notepad++ found: nothing is watched, nothing breaks.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task NotInstalled_WatchesNothing()
    {
        await using var integration = new NotepadPlusPlusIntegration(history, () => long.MaxValue, () => NotepadPlusPlusLocation.None);
        await integration.StartAsync(keepTabs: true, recordFiles: false);
        Assert.False(integration.IsWatchingTabs);
        Assert.False(integration.Location.IsInstalled);
    }

    /// <summary>The integration over the stand-in folder (no running Notepad++ is asked: opened files stay off in these tests).</summary>
    /// <returns>The integration (not started).</returns>
    private NotepadPlusPlusIntegration Create() =>
        new(history, () => 64L * 1024 * 1024, () => new NotepadPlusPlusLocation(null, null, [data], "test", NotepadPlusPlusConfig.Defaults));
}

/// <summary>
/// Tests for <see cref="NotepadPlusPlusClient"/>, <see cref="NotepadPlusPlusFileWatcher"/> and the locator's rules against a
/// fake Notepad++ inside the test process: a window of a class of its own answering Notepad++'s plugin messages, writing
/// paths into the buffer the client places in "its" process (this one). Nothing reaches a real Notepad++.
/// </summary>
public sealed class NotepadPlusPlusClientTests
{
    /// <summary>
    /// The client reads what the fake Notepad++ answers: counts per view, ids, the document in front, paths through its
    /// buffer, the version, and its process's start time and command line.
    /// </summary>
    [Fact]
    public void Client_ReadsDocumentsAndPaths()
    {
        using var fake = new FakeNotepadPlusPlus();
        fake.SetDocuments(main: [(101, @"C:\BC-TEST\a.txt"), (102, "new 1")], sub: [(103, @"\\server\share\BC-TEST.txt")], current: 102);
        var window = Assert.Single(NotepadPlusPlusClient.FindWindows(fake.ClassName));
        using var client = NotepadPlusPlusClient.TryOpen(window.Window, window.ProcessId, Trusted, out var problem)!;
        Assert.Null(problem);
        Assert.Equal(3, client.OpenCount());
        var documents = client.Documents()!.Value;
        Assert.Equal([101L, 102L, 103L], documents.Ids);
        Assert.Equal(102, documents.Current);
        Assert.Equal(@"C:\BC-TEST\a.txt", client.PathOf(101));
        Assert.Equal(@"\\server\share\BC-TEST.txt", client.PathOf(103));
        Assert.Null(client.PathOf(999));
        Assert.Equal("8.9.1", client.Version());
        Assert.NotNull(client.StartTime());
        Assert.False(string.IsNullOrEmpty(client.CommandLine()));
    }

    /// <summary>An untrusted owner is refused with its reason, before anything is opened.</summary>
    [Fact]
    public void Client_RefusesAnUntrustedOwner()
    {
        using var fake = new FakeNotepadPlusPlus();
        var window = Assert.Single(NotepadPlusPlusClient.FindWindows(fake.ClassName));
        Assert.Null(NotepadPlusPlusClient.TryOpen(window.Window, window.ProcessId, _ => new NotepadPlusPlusTrust(false, null, "BC-TEST impostor"), out var problem));
        Assert.Equal("BC-TEST impostor", problem);
        Assert.False(NotepadPlusPlusOwnerVerifier.VerifyFile(Environment.ProcessPath!).IsTrusted);
    }

    /// <summary>
    /// The watcher: the documents open at its first look are a baseline; a file opened afterwards is handed over once, an
    /// untitled tab never, a tab switch never, and a file closed and opened again is handed over again.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task FileWatcher_HandsOverOpenedFiles()
    {
        using var fake = new FakeNotepadPlusPlus();
        fake.SetDocuments(main: [(1, @"C:\BC-TEST\restored.txt")], sub: [], current: 1);
        var opened = new ConcurrentQueue<string>();
        using var watcher = new NotepadPlusPlusFileWatcher((path, _, _) => { opened.Enqueue(path); return Task.CompletedTask; }, Trusted, fake.ClassName);
        watcher.Start();
        await watcher.ScanNowAsync();
        Assert.Empty(opened);
        var instance = Assert.Single(watcher.Instances);
        Assert.Equal("8.9.1", instance.Version);

        fake.SetDocuments(main: [(1, @"C:\BC-TEST\restored.txt"), (2, @"C:\BC-TEST\opened.txt"), (3, "new 1")], sub: [], current: 2);
        await watcher.ScanNowAsync();
        Assert.Equal([@"C:\BC-TEST\opened.txt"], opened);

        fake.SetDocuments(main: [(1, @"C:\BC-TEST\restored.txt"), (2, @"C:\BC-TEST\opened.txt"), (3, "new 1")], sub: [], current: 1);
        await watcher.ScanNowAsync();
        Assert.Single(opened);

        fake.SetDocuments(main: [(1, @"C:\BC-TEST\restored.txt"), (3, "new 1")], sub: [], current: 1);
        await watcher.ScanNowAsync();
        fake.SetDocuments(main: [(1, @"C:\BC-TEST\restored.txt"), (3, "new 1"), (2, @"C:\BC-TEST\opened.txt")], sub: [], current: 2);
        await watcher.ScanNowAsync();
        Assert.Equal(2, opened.Count);
        Assert.Equal(2, watcher.RecordedThisSession);
    }

    /// <summary><c>-settingsDir=</c> on a command line, quoted or not.</summary>
    /// <param name="commandLine">The command line.</param>
    /// <param name="expected">The folder, or <see langword="null"/>.</param>
    [Theory]
    [InlineData("\"C:\\npp\\notepad++.exe\" -settingsDir=\"D:\\BC-TEST settings\" a.txt", "D:\\BC-TEST settings")]
    [InlineData("C:\\npp\\notepad++.exe -settingsDir=D:\\BC-TEST -nosession", "D:\\BC-TEST")]
    [InlineData("C:\\npp\\notepad++.exe -nosession", null)]
    [InlineData(null, null)]
    public void Locator_SettingsDirArgument(string? commandLine, string? expected) =>
        Assert.Equal(expected, NotepadPlusPlusLocator.SettingsDirArgument(commandLine));

    /// <summary>A portable Notepad++ (doLocalConf.xml next to it, outside Program Files) keeps its settings in its own folder.</summary>
    [Fact]
    public void Locator_PortableKeepsItsOwnFolder()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", Guid.NewGuid().ToString("N"), "npp")).FullName;
        try
        {
            var exe = Path.Combine(folder, "notepad++.exe");
            Assert.NotEqual(folder, NotepadPlusPlusLocator.SettingsFolderOf(exe, null));
            File.WriteAllText(Path.Combine(folder, "doLocalConf.xml"), string.Empty);
            Assert.Equal(folder, NotepadPlusPlusLocator.SettingsFolderOf(exe, null));
            Assert.Equal(@"D:\BC-TEST", NotepadPlusPlusLocator.SettingsFolderOf(exe, "x -settingsDir=D:\\BC-TEST"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    /// <summary>The real Notepad++ on this PC, when one is installed, passes the owner check (signer "NOTEPAD++").</summary>
    [Fact]
    public void OwnerVerifier_TrustsTheRealNotepadPlusPlus()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Notepad++", "notepad++.exe");
        Assert.SkipUnless(File.Exists(installed), "Notepad++ is not installed here.");
        var trust = NotepadPlusPlusOwnerVerifier.VerifyFile(installed);
        Assert.True(trust.IsTrusted, trust.Reason);
    }

    /// <summary>Trusts the fake's owner (this process), as the real check would trust a signed Notepad++.</summary>
    /// <param name="processId">The process.</param>
    /// <returns>Trusted.</returns>
    private static NotepadPlusPlusTrust Trusted(uint processId) => new(true, Environment.ProcessPath, "BC-TEST fake");

    /// <summary>
    /// A fake Notepad++: a visible, zero-sized top-level window of a class of its own on its own thread, answering the plugin
    /// messages the client sends from a document list the test sets.
    /// </summary>
    private sealed class FakeNotepadPlusPlus : IDisposable
    {
        private readonly MessageWindowThread thread;
        private readonly Lock gate = new();
        private (long Id, string Path)[] main = [];
        private (long Id, string Path)[] sub = [];
        private long current;

        /// <summary>Creates the window and shows it (zero-sized: nothing is drawn).</summary>
        public FakeNotepadPlusPlus()
        {
            ClassName = "BC-TEST Notepad++ " + Guid.NewGuid().ToString("N");
            thread = new MessageWindowThread("BC-TEST Notepad++", messageOnly: false, className: ClassName) { MessageHandler = Answer };
            ShowWindow(thread.Handle, 8);
        }

        /// <summary>The window class tests look for.</summary>
        public string ClassName { get; }

        /// <summary>Replaces the documents and changes the title (as Notepad++ does when a document comes to the front).</summary>
        /// <param name="main">The main view's documents.</param>
        /// <param name="sub">The sub view's documents.</param>
        /// <param name="current">The id in front.</param>
        public void SetDocuments((long Id, string Path)[] main, (long Id, string Path)[] sub, long current)
        {
            lock (gate)
            {
                this.main = main;
                this.sub = sub;
                this.current = current;
            }

            SetWindowText(thread.Handle, $"BC-TEST {current} - Notepad++");
        }

        /// <summary>Closes the window and its thread.</summary>
        public void Dispose() => thread.Dispose();

        /// <summary>Answers Notepad++'s plugin messages.</summary>
        /// <param name="message">The message.</param>
        /// <param name="wParam">First parameter.</param>
        /// <param name="lParam">Second parameter.</param>
        /// <returns>The answer, or <see langword="null"/> for other messages.</returns>
        private nint? Answer(uint message, nint wParam, nint lParam)
        {
            lock (gate)
            {
                switch (message)
                {
                    case NativeMethods.NPPM_GETNBOPENFILES:
                        return lParam switch { 0 => main.Length + sub.Length, 1 => main.Length, 2 => sub.Length, _ => 0 };
                    case NativeMethods.NPPM_GETBUFFERIDFROMPOS:
                        var view = lParam == 0 ? main : sub;
                        return wParam >= 0 && wParam < view.Length ? (nint)view[wParam].Id : 0;
                    case NativeMethods.NPPM_GETCURRENTBUFFERID:
                        return (nint)current;
                    case NativeMethods.NPPM_GETNPPVERSION:
                        return (8 << 16) | 910;
                    case NativeMethods.NPPM_GETFULLPATHFROMBUFFERID:
                        var document = main.Concat(sub).FirstOrDefault(d => d.Id == wParam);
                        if (document.Path is null)
                        {
                            return -1;
                        }

                        if (lParam != 0)
                        {
                            // The "remote" buffer lives in this very process: copy like Notepad++'s lstrcpy, terminator included.
                            Marshal.Copy((document.Path + "\0").ToCharArray(), 0, lParam, document.Path.Length + 1);
                        }

                        return document.Path.Length;
                    default:
                        return null;
                }
            }
        }

        /// <summary>Shows a window (8 = SW_SHOWNA: without activating it).</summary>
        /// <param name="hWnd">The window.</param>
        /// <param name="nCmdShow">How.</param>
        /// <returns>Whether it was visible before.</returns>
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(nint hWnd, int nCmdShow);

        /// <summary>Sets a window's title.</summary>
        /// <param name="hWnd">The window.</param>
        /// <param name="text">The title.</param>
        /// <returns>Whether it was set.</returns>
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowTextW")]
        private static extern bool SetWindowText(nint hWnd, string text);
    }
}

/// <summary>A clock the test moves by hand; its timers never fire (tests call the work directly).</summary>
/// <param name="start">The first time.</param>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;

    /// <summary>Moves the clock forward.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) => now += by;

    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow() => now;

    /// <inheritdoc/>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NeverTimer();

    /// <summary>A timer that never fires.</summary>
    private sealed class NeverTimer : ITimer
    {
        /// <inheritdoc/>
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        /// <inheritdoc/>
        public void Dispose()
        {
        }

        /// <inheritdoc/>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
