using BetterClipboard.Core.Content;
using BetterClipboard.Core.Everything;
using BetterClipboard.Core.Integrations;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.NotepadPlusPlus;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Prompts;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Shells;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Settings' <em>Delete stored…</em> (CLAUDE.md §2.43): what each integration stores as parts (<see cref="StoredData"/>),
/// the store counting, showing and deleting them (<see cref="ClipStore.SummarizeStored"/>,
/// <see cref="ClipStore.GetStoredExamples"/>, <see cref="ClipStore.DeleteStored"/>), and the history service's preview,
/// counts and deletion of entries, lists and prompt archives.
/// </summary>
/// <remarks>
/// The owner's choices (2026-11-09): everything the integration's tab lists, pinned and grouped entries kept unless ticked,
/// and the source app's own files never touched (nothing here touches a file: the store is all there is).
/// </remarks>
public sealed class StoredDataTests : IAsyncLifetime
{
    /// <summary>Notepad++ as the source of copies made in it.</summary>
    private static readonly SourceAppInfo NotepadPlusPlus = NotepadPlusPlusSources.Source(@"C:\Program Files\Notepad++\notepad++.exe");

    /// <summary>Everything as the source of copies made in it.</summary>
    private static readonly SourceAppInfo EverythingApp = new("Everything", @"C:\Program Files\Everything\Everything.exe", "Everything");

    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private ClipStore store = null!;
    private ClipHistoryService? service;

    /// <summary>Creates a store over a temp database (the service is started by each test that needs it).</summary>
    /// <returns>A completed task.</returns>
    public ValueTask InitializeAsync()
    {
        store = new ClipStore(temp.DatabasePath);
        store.Initialize();
        return ValueTask.CompletedTask;
    }

    /// <summary>Stops the service, if one was started, and deletes the temp store.</summary>
    /// <returns>A task completing after shutdown.</returns>
    public async ValueTask DisposeAsync()
    {
        if (service is not null)
        {
            await service.DisposeAsync();
        }

        temp.Dispose();
    }

    /// <summary>
    /// Every source has parts with unique ids, a label of their own, a note, a name and a switch title; its pinned-or-grouped
    /// part covers exactly its entry parts' slices and starts unticked, like the hidden lists; entry parts take only
    /// unprotected entries.
    /// </summary>
    [Fact]
    public void Parts_AreWellFormed()
    {
        foreach (var source in StoredData.Sources)
        {
            var parts = StoredData.PartsOf(source);
            Assert.NotEmpty(parts);
            Assert.Equal(parts.Count, parts.Select(p => p.Id).Distinct().Count());
            Assert.All(parts, part => Assert.NotEqual(part.Id, StoredText.LabelOf(part.Id)));
            Assert.False(string.IsNullOrWhiteSpace(StoredText.NoteOf(source)));
            Assert.False(string.IsNullOrWhiteSpace(StoredText.SourceNameOf(source)));
            Assert.False(string.IsNullOrWhiteSpace(StoredText.SwitchTitleOf(source)));

            var entryParts = parts.Where(p => p.Kind == StoredPartKind.Entries && p.Id != StoredData.ProtectedPartId).ToList();
            Assert.All(entryParts, part =>
            {
                Assert.True(part.SelectedByDefault);
                Assert.All(part.Selections, s => Assert.Equal(StoredProtection.Unprotected, s.Protection));
            });
            var protectedPart = Assert.Single(parts, p => p.Id == StoredData.ProtectedPartId);
            Assert.False(protectedPart.SelectedByDefault);
            Assert.Equal(
                entryParts.SelectMany(p => p.Selections).Select(s => s with { Protection = StoredProtection.Protected }),
                protectedPart.Selections);
            Assert.All(parts.Where(p => p.Kind == StoredPartKind.HiddenList), part => Assert.False(part.SelectedByDefault));
            Assert.All(parts.Where(p => p.Kind is StoredPartKind.HiddenList or StoredPartKind.KeptCommands), part => Assert.NotNull(part.StateName));
            Assert.All(parts.Where(p => p.Kind == StoredPartKind.PromptArchive), part => Assert.NotNull(part.Agent));
        }
    }

    /// <summary>
    /// "Everything its tab lists": a source's entry parts together — pinned and grouped included — are exactly the entries
    /// its tab lists, never one more, over a store with every origin, the copies of every app, and pinned and grouped
    /// entries. The two Notepad++ cards share the Notepad++ tab: together they cover it.
    /// </summary>
    [Fact]
    public void EntryParts_TogetherAreTheTab()
    {
        Seed();
        foreach (var source in StoredData.Sources.Where(s => s is not (StoredSource.NotepadPlusPlusTabs or StoredSource.NotepadPlusPlusFiles)))
        {
            var tab = TabIds(StoredData.TabOf(source));
            Assert.NotEmpty(tab);
            Assert.Equal(tab, UnionIds(EntrySelections(source)));
        }

        var notepad = EntrySelections(StoredSource.NotepadPlusPlusTabs).Concat(EntrySelections(StoredSource.NotepadPlusPlusFiles)).ToList();
        Assert.Equal(TabIds(ClipFilter.NotepadPlusPlus), UnionIds(notepad));
    }

    /// <summary>
    /// The counts of one pass: entries, bytes, newest and oldest use per union, an entry in two slices of a union counted
    /// once, and an empty union all zero.
    /// </summary>
    [Fact]
    public void Summarize_CountsEachUnion()
    {
        long shot = Add(TestData.ShareXShot(1, TestData.Now));
        Add(TestData.Text("BC-TEST copied by ShareX", TestData.Now.AddMinutes(5), source: TestData.ShareX));
        Add(TestData.Text("BC-TEST Notepad copy", TestData.Now.AddMinutes(9), source: TestData.Notepad));

        var parts = StoredData.PartsOf(StoredSource.ShareX);
        var stats = store.SummarizeStored([parts[0].Selections, parts[1].Selections, [.. parts[0].Selections, .. parts[0].Selections], []]);
        Assert.Equal(new StoredStats(1, store.GetEntry(shot)!.SizeBytes, TestData.Now, TestData.Now), stats[0]);
        Assert.Equal(1, stats[1].Count);
        Assert.Equal(TestData.Now.AddMinutes(5), stats[1].Newest);
        Assert.Equal(1, stats[2].Count);
        Assert.Equal(StoredStats.Empty, stats[3]);
        Assert.Empty(store.SummarizeStored([]));
    }

    /// <summary>A part's examples are its newest entries, most recently used first, at most as many as asked.</summary>
    [Fact]
    public void Examples_AreTheNewest()
    {
        for (int i = 0; i < 6; i++)
        {
            Add(TestData.ShareXShot((byte)(10 + i), TestData.Now.AddMinutes(i)));
        }

        var newest = store.GetStoredExamples(StoredData.PartsOf(StoredSource.ShareX)[0].Selections, 4);
        Assert.Equal([TestData.Now.AddMinutes(5), TestData.Now.AddMinutes(4), TestData.Now.AddMinutes(3), TestData.Now.AddMinutes(2)],
            newest.Select(e => e.LastUsedUtc));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.GetStoredExamples([], 0));
    }

    /// <summary>
    /// Deleting the default ticks takes the tab's unprotected entries only — pinned and grouped ones and other tabs' entries
    /// stay — and tombstones each: ShareX's catch-up cannot bring a deleted screenshot back, a new live copy of it is
    /// recorded again.
    /// </summary>
    [Fact]
    public void Delete_DefaultTicks_KeepsPinsGroupsAndOtherTabs_AndTombstones()
    {
        long shot = Add(TestData.ShareXShot(1, TestData.Now));
        long copy = Add(TestData.Text("BC-TEST copied by ShareX", TestData.Now.AddMinutes(1), source: TestData.ShareX));
        long pinned = Add(TestData.ShareXShot(2, TestData.Now.AddMinutes(2)));
        Assert.True(store.SetPinned(pinned, pinned: true, TestData.Now));
        long grouped = Add(TestData.ShareXShot(3, TestData.Now.AddMinutes(3)));
        var group = store.CreateGroup("Work", GroupIconCatalog.All[0].Glyph, TestData.Now);
        Assert.True(store.AddToGroup(grouped, group.Id, TestData.Now));
        long other = Add(TestData.Text("BC-TEST Notepad copy", TestData.Now.AddMinutes(4), source: TestData.Notepad));

        var ticked = StoredData.PartsOf(StoredSource.ShareX).Where(p => p.SelectedByDefault).SelectMany(p => p.Selections).ToList();
        Assert.Equal(2, store.DeleteStored(ticked, TestData.Now.AddMinutes(10)));
        Assert.Null(store.GetEntry(shot));
        Assert.Null(store.GetEntry(copy));
        Assert.NotNull(store.GetEntry(pinned));
        Assert.NotNull(store.GetEntry(grouped));
        Assert.NotNull(store.GetEntry(other));

        // ShareX's origin is import-like: a catch-up of the same file is suppressed; a live copy lifts the tombstone.
        Assert.Null(store.Upsert(TestData.ShareXShot(1, TestData.Now.AddMinutes(11)), Classify(TestData.ShareXShot(1)), null, bumpIfExists: true));
        var live = new ClipCapture
        {
            Formats = TestData.ShareXShot(1).Formats,
            CapturedAtUtc = TestData.Now.AddMinutes(12),
            Origin = ClipOrigin.Captured,
            Source = TestData.ShareX,
        };
        Assert.NotNull(store.Upsert(live, Classify(live), null, bumpIfExists: true));
    }

    /// <summary>
    /// Ticking "Pinned or in a group" takes those too; the group itself stays (empty). An entry outside the tab — a pinned
    /// copy without a source path, whose ShareX filter is NULL — is never taken by a protected slice.
    /// </summary>
    [Fact]
    public void Delete_ProtectedPart_TakesPinsAndGroups_NeverOtherTabs()
    {
        long pinned = Add(TestData.ShareXShot(2, TestData.Now));
        Assert.True(store.SetPinned(pinned, pinned: true, TestData.Now));
        long grouped = Add(TestData.ShareXShot(3, TestData.Now.AddMinutes(1)));
        var group = store.CreateGroup("Work", GroupIconCatalog.All[0].Glyph, TestData.Now);
        Assert.True(store.AddToGroup(grouped, group.Id, TestData.Now));
        long pinnedElsewhere = Add(TestData.Text("BC-TEST pinned, no path", TestData.Now.AddMinutes(2), source: new SourceAppInfo("tool", null, "Tool")));
        Assert.True(store.SetPinned(pinnedElsewhere, pinned: true, TestData.Now));

        var protectedPart = StoredData.PartsOf(StoredSource.ShareX).Single(p => p.Id == StoredData.ProtectedPartId);
        Assert.Equal(2, store.DeleteStored(protectedPart.Selections, TestData.Now.AddMinutes(10)));
        Assert.NotNull(store.GetEntry(pinnedElsewhere));
        Assert.Single(store.GetGroups(), g => g.Id == group.Id);
    }

    /// <summary>Deleting nothing, or an empty union, changes nothing.</summary>
    [Fact]
    public void Delete_Nothing()
    {
        Add(TestData.ShareXShot(1, TestData.Now));
        Assert.Equal(0, store.DeleteStored([], TestData.Now));
        Assert.Equal(0, store.DeleteStored(StoredData.PartsOf(StoredSource.Codex).Where(p => p.Kind == StoredPartKind.Entries).SelectMany(p => p.Selections).ToList(), TestData.Now));
        Assert.Equal(1, store.GetStats().Count);
    }

    /// <summary>
    /// The dialog's texts keep a shortcut on one line: a word joiner on each side of a "+" between two letters or digits
    /// (the title once broke as "…Win" / "+PrtScn?"), and nothing anywhere else.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="expected">The text as shown.</param>
    [Theory]
    [InlineData("Delete what BetterClipboard stored from Snipping Tool and Win+PrtScn?", "Delete what BetterClipboard stored from Snipping Tool and Win\u2060+\u2060PrtScn?")]
    [InlineData("Ctrl+Shift+V", "Ctrl\u2060+\u2060Shift\u2060+\u2060V")]
    [InlineData("截图工具和 Win+PrtScn", "截图工具和 Win\u2060+\u2060PrtScn")]
    [InlineData("Notepad++", "Notepad++")]
    [InlineData("C++17", "C++17")]
    [InlineData("60+ matching", "60+ matching")]
    [InlineData("+v", "+v")]
    [InlineData("", "")]
    public void KeepShortcutsTogether_GluesOnlyShortcuts(string text, string expected) =>
        Assert.Equal(expected, StoredText.KeepShortcutsTogether(text));

    /// <summary>
    /// The service's preview and counts: every part of the Cmd card (kept commands newest first, kept entries, the hidden
    /// list with no examples — only fingerprints are kept), the Everything card's hidden paths newest first, an archive with
    /// its prompts and sends, and every source's total for the cards' buttons.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Service_PreviewAndCounts()
    {
        Seed();
        store.SetStateValue(CmdHistory.KeptStateName, CmdHistory.FormatKept([
            new KeptCommand("dir BC-TEST", 2, TestData.Now.AddMinutes(1)),
            new KeptCommand("cd BC-TEST", 1, TestData.Now.AddMinutes(3)),
        ]));
        store.SetStateValue(ShellTab.HiddenStateName(ShellKind.Cmd), ShellTab.FormatHidden([new HiddenCommand("hash-a", 1), new HiddenCommand("hash-b", 2)]));
        store.SetStateValue(EverythingTab.HiddenStateName, EverythingTab.FormatHidden(new Dictionary<string, DateTimeOffset>
        {
            [@"C:\BC-TEST\old.txt"] = TestData.Now,
            [@"C:\BC-TEST\new.txt"] = TestData.Now.AddHours(1),
        }));
        var history = await StartAsync();
        await IngestAsync(history, PromptAgent.ClaudeCode, ("BC-TEST first prompt", 1), ("BC-TEST second prompt", 2), ("BC-TEST first prompt", 3));

        var cmd = await history.PreviewStoredAsync(StoredSource.Cmd);
        // No pinned or grouped Cmd entry: that part holds nothing and is left out.
        Assert.Equal(["cmd.list", "cmd.kept", "cmd.hidden"], cmd.Parts.Select(p => p.Part.Id));
        var kept = cmd.Parts[0];
        Assert.Equal(2, kept.Count);
        Assert.Equal(["cd BC-TEST", "dir BC-TEST"], kept.Examples.Select(e => e.Text));
        Assert.All(kept.Examples, e => Assert.True(e.IsCode));
        Assert.Equal(TestData.Now.AddMinutes(3), kept.Newest);
        Assert.Equal(TestData.Now.AddMinutes(1), kept.Oldest);
        var hidden = cmd.Parts[2];
        Assert.Equal(2, hidden.Count);
        Assert.Empty(hidden.Examples);

        var everything = await history.PreviewStoredAsync(StoredSource.Everything);
        var hiddenPicks = everything.Parts.Single(p => p.Part.Id == "everything.hidden");
        Assert.Equal([@"C:\BC-TEST\new.txt", @"C:\BC-TEST\old.txt"], hiddenPicks.Examples.Select(e => e.Text));

        var claude = await history.PreviewStoredAsync(StoredSource.ClaudeCode);
        var archive = claude.Parts.Single(p => p.Part.Id == "claude.archive");
        Assert.Equal(2, archive.Count);
        Assert.Equal(3, archive.Sends);
        Assert.Contains("BC-TEST second prompt", archive.Examples.Select(e => e.Text));

        var counts = await history.CountStoredAsync();
        Assert.Equal(StoredData.Sources.Count, counts.Count);
        Assert.Equal(2 + TabIds(ClipFilter.Cmd).Count + 2, counts[StoredSource.Cmd]);
        Assert.Equal(2 + TabIds(ClipFilter.ClaudeCode).Count, counts[StoredSource.ClaudeCode]);
        Assert.Equal(TabIds(ClipFilter.ShareX).Count, counts[StoredSource.ShareX]);
        Assert.Equal(TabIds(ClipFilter.Codex).Count, counts[StoredSource.Codex]);
    }

    /// <summary>
    /// The service's deletion: entries go in one Reset, the kept list is emptied, an untouched hidden list stays, an archive
    /// clears with its event, and a tab that showed only with something in it (Run) learns it is empty.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Service_Delete()
    {
        Seed();
        store.SetStateValue(CmdHistory.KeptStateName, CmdHistory.FormatKept([new KeptCommand("dir BC-TEST", 1, TestData.Now)]));
        store.SetStateValue(ShellTab.HiddenStateName(ShellKind.Cmd), ShellTab.FormatHidden([new HiddenCommand("hash-a", 1)]));
        var history = await StartAsync();
        await IngestAsync(history, PromptAgent.Codex, ("BC-TEST codex prompt", 1));
        int resets = 0;
        var cleared = new List<PromptAgent>();
        history.Changed += (_, e) => resets += e.Kind == ClipChangeKind.Reset ? 1 : 0;
        history.PromptsChanged += (_, e) =>
        {
            if (e.Removed > 0)
            {
                cleared.Add(e.Agent);
            }
        };

        var cmdTicked = StoredData.PartsOf(StoredSource.Cmd).Where(p => p.SelectedByDefault).ToList();
        var cmdResult = await history.DeleteStoredAsync(cmdTicked);
        Assert.Equal(0, TabIdsUnprotected(ClipFilter.Cmd));
        Assert.Equal(1, cmdResult.Lists);
        Assert.Equal(0, cmdResult.Prompts);
        Assert.Empty(CmdHistory.ParseKept(store.GetStateValue(CmdHistory.KeptStateName)));
        Assert.Single(ShellTab.ParseHidden(store.GetStateValue(ShellTab.HiddenStateName(ShellKind.Cmd))));
        Assert.Equal(1, resets);

        var codexResult = await history.DeleteStoredAsync(StoredData.PartsOf(StoredSource.Codex).Where(p => p.Kind == StoredPartKind.PromptArchive).ToList());
        Assert.Equal(1, codexResult.Prompts);
        Assert.Equal(0, codexResult.Entries);
        Assert.Equal([PromptAgent.Codex], cleared);
        Assert.Equal(0, store.GetPromptStats(PromptAgent.Codex).Sends);
        Assert.Equal(1, resets);

        Assert.True(history.HasRunHistory);
        await history.DeleteStoredAsync(StoredData.PartsOf(StoredSource.RunHistory));
        Assert.Empty(TabIds(ClipFilter.Run));
        Assert.False(history.HasRunHistory);
    }

    /// <summary>
    /// Fills the store with one entry of every kind the integration tabs list — each origin, copies made in each app, pinned
    /// and grouped ones — plus plain copies no tab lists.
    /// </summary>
    private void Seed()
    {
        var at = TestData.Now;
        var group = store.CreateGroup("Work", GroupIconCatalog.All[0].Glyph, at);
        int minute = 0;
        DateTimeOffset Next() => at.AddMinutes(++minute);

        Add(TestData.ShareXShot(1, Next()));
        Add(TestData.Text("BC-TEST copied by ShareX", Next(), source: TestData.ShareX));
        Pin(Add(TestData.ShareXShot(2, Next())));
        Add(new ClipCapture
        {
            Formats = [new ClipFormatData(ClipFormatNames.Png, Enumerable.Repeat((byte)7, 64).ToArray())],
            CapturedAtUtc = Next(),
            Origin = ClipOrigin.WindowsScreenshot,
            Source = WindowsScreenshots.WinPrtScnSource,
        });
        Add(TestData.Text("BC-TEST snipped text", Next(), source: WindowsScreenshots.SnippingToolSource(@"C:\Program Files\WindowsApps\Microsoft.ScreenSketch\SnippingTool\SnippingTool.exe")));
        Add(TestData.Text("BC-TEST unsaved tab", Next(), ClipOrigin.NotepadPlusPlusTab, source: NotepadPlusPlus));
        Add(TestData.Text(@"C:\BC-TEST\opened.txt", Next(), ClipOrigin.NotepadPlusPlusFile, source: NotepadPlusPlus));
        Group(Add(TestData.Text("BC-TEST copied in Notepad++", Next(), source: NotepadPlusPlus)), group.Id);
        Add(TestData.Text(@"C:\BC-TEST\pick.txt", Next(), ClipOrigin.Everything, source: EverythingApp));
        Add(TestData.Text("BC-TEST copied in Everything", Next(), source: EverythingApp));
        Add(RunMru.CreateCapture("BC-TEST notepad", Next(), observedRun: true));
        Add(RunMru.CreateCapture("BC-TEST calc", Next(), observedRun: false));
        Pin(Add(RunMru.CreateCapture("BC-TEST winver", Next(), observedRun: true)));
        Add(TestData.Text("Get-ChildItem BC-TEST", Next(), ClipOrigin.PowerShell, source: ShellSources.For(ShellKind.PowerShell)));
        Add(TestData.Text("dir BC-TEST kept", Next(), ClipOrigin.Cmd, source: ShellSources.For(ShellKind.Cmd)));
        Add(TestData.Text("BC-TEST kept prompt", Next(), ClipOrigin.ClaudeCode, source: PromptAgents.SourceOf(PromptAgent.ClaudeCode)));
        Add(TestData.Text("BC-TEST /copy output", Next(), source: PromptAgents.SourceOf(PromptAgent.ClaudeCode)));
        Add(TestData.Text("BC-TEST kept codex prompt", Next(), ClipOrigin.Codex, source: PromptAgents.SourceOf(PromptAgent.Codex)));
        Add(TestData.Text("BC-TEST plain copy", Next(), source: TestData.Notepad));
        Pin(Add(TestData.Text("BC-TEST pinned plain copy", Next(), source: new SourceAppInfo("tool", null, "Tool"))));
        Group(Add(TestData.Text("BC-TEST grouped plain copy", Next())), group.Id);

        void Pin(long id) => Assert.True(store.SetPinned(id, pinned: true, at));
        void Group(long id, long groupId) => Assert.True(store.AddToGroup(id, groupId, at));
    }

    /// <summary>A source's entry slices: every entry part's, the pinned-or-grouped part's included.</summary>
    /// <param name="source">The source.</param>
    /// <returns>The slices.</returns>
    private static List<StoredSelection> EntrySelections(StoredSource source) =>
        StoredData.PartsOf(source).Where(p => p.Kind == StoredPartKind.Entries).SelectMany(p => p.Selections).ToList();

    /// <summary>The ids a tab lists (every one of them, pinned and grouped included), sorted.</summary>
    /// <param name="filter">The tab.</param>
    /// <returns>The ids.</returns>
    private List<long> TabIds(ClipFilter filter) =>
        store.Query(new ClipQuery { Filter = filter, Limit = 10_000, PinnedFirst = false }).Select(e => e.Id).Order().ToList();

    /// <summary>How many entries a tab lists that are neither pinned nor grouped.</summary>
    /// <param name="filter">The tab.</param>
    /// <returns>The count.</returns>
    private int TabIdsUnprotected(ClipFilter filter) =>
        store.Query(new ClipQuery { Filter = filter, Limit = 10_000 }).Count(e => !e.IsPinned && e.GroupIds.Count == 0);

    /// <summary>The ids a union of slices takes, sorted (through its examples, which list all of them at a large limit).</summary>
    /// <param name="union">The slices.</param>
    /// <returns>The ids.</returns>
    private List<long> UnionIds(IReadOnlyList<StoredSelection> union) =>
        store.GetStoredExamples(union, 10_000).Select(e => e.Id).Order().ToList();

    /// <summary>Stores a capture as the service would (a new event bumps a duplicate, an import leaves it alone).</summary>
    /// <param name="capture">The capture.</param>
    /// <returns>The entry's id.</returns>
    private long Add(ClipCapture capture) =>
        store.Upsert(capture, Classify(capture), null, bumpIfExists: capture.Origin != ClipOrigin.RunDialogHistory)!.Entry.Id;

    /// <summary>Classifies a capture as the service would.</summary>
    /// <param name="capture">The capture.</param>
    /// <returns>Its classification.</returns>
    private static ClassifiedClip Classify(ClipCapture capture) => ContentClassifier.Classify(capture)!;

    /// <summary>Starts a history service over the test store and waits for its worker's start-up lookups.</summary>
    /// <returns>The started service.</returns>
    private async Task<ClipHistoryService> StartAsync()
    {
        service = new ClipHistoryService(store, null, () => new CaptureRules { Retention = RetentionPolicy.Unlimited });
        service.Start();
        await service.PruneAsync();
        return service;
    }

    /// <summary>Archives prompts of one agent as one read (distinct keys; equal texts make one prompt sent twice).</summary>
    /// <param name="history">The service.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="prompts">Each prompt's text and the minute it was sent.</param>
    /// <returns>A task completing when stored.</returns>
    private static Task IngestAsync(ClipHistoryService history, PromptAgent agent, params (string Text, int Minute)[] prompts)
    {
        var kind = agent == PromptAgent.Codex ? PromptSourceKind.CodexHistory : PromptSourceKind.ClaudeHistory;
        return history.IngestPromptsAsync(new PromptBatch
        {
            Agent = agent,
            Kind = kind,
            FileKey = Guid.NewGuid().ToString("N"),
            Path = "BC-TEST",
            Checkpoint = new TailCheckpoint { Offset = 1, Length = 1 },
            Prompts = prompts.Select((p, i) => new AgentPrompt(kind, $"bc-test-{agent}-{i}", "s", null, TestData.Now.AddMinutes(p.Minute), p.Text)).ToList(),
        });
    }
}
