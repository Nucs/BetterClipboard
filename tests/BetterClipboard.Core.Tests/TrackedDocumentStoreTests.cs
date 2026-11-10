using BetterClipboard.Core.Content;
using BetterClipboard.Core.Documents;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.NotepadPlusPlus;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Storage;
using Microsoft.Data.Sqlite;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for tracked documents in the store and the history service (CLAUDE.md §2.41): a Notepad++ tab's text recorded,
/// updated in place (keeping its place, pins and groups), split into a new copy, renamed, deleted and imported — and the
/// Notepad++ tab's filter and witness.
/// </summary>
public sealed class TrackedDocumentStoreTests : IAsyncLifetime
{
    /// <summary>Notepad++'s executable as installed.</summary>
    private const string Exe = @"C:\Program Files\Notepad++\notepad++.exe";

    /// <summary>The backup key of the test tab.</summary>
    private const string Key = @"C:\Users\BC-TEST\AppData\Roaming\Notepad++\backup\new 3@2026-11-08_101530";

    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private readonly List<ClipChangedEventArgs> events = [];
    private CaptureRules rules = new() { Retention = RetentionPolicy.Unlimited };
    private ClipStore store = null!;
    private ClipHistoryService? service;

    /// <summary>Creates a store over a temp database.</summary>
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
    /// A first version becomes an entry with the tab's name, the Notepad++ source and the tab origin; the same text again
    /// writes nothing.
    /// </summary>
    [Fact]
    public void Record_FirstVersion_ThenUnchanged()
    {
        var first = Record("BC-TEST groceries: milk, eggs", TestData.Now);
        Assert.Equal(TrackedDocumentOutcomeKind.Created, first.Kind);
        Assert.True(first.IsNewEntry);
        var entry = first.Entry!;
        Assert.Equal("new 3", entry.SourceTitle);
        Assert.Equal(NotepadPlusPlusSources.DisplayName, entry.SourceAppName);
        Assert.Equal(ClipOrigin.NotepadPlusPlusTab, entry.Origin);
        Assert.Null(entry.EditedUtc);

        var again = Record("BC-TEST groceries: milk, eggs", TestData.Now.AddMinutes(1));
        Assert.Equal(TrackedDocumentOutcomeKind.Unchanged, again.Kind);
        Assert.Null(again.Entry);
        Assert.Single(store.Query(new ClipQuery(), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A version that loses little replaces the text in place: same id, same place (last used unchanged), pin and group kept,
    /// the edit time set, the old text gone from search, the new one found.
    /// </summary>
    [Fact]
    public void Record_UpdateInPlace_KeepsPlacePinAndGroup()
    {
        long id = Record("BC-TEST alpha beta gamma", TestData.Now).Entry!.Id;
        var other = store.Upsert(TestData.Text("BC-TEST a later copy", TestData.Now.AddMinutes(5)), Classify(TestData.Text("BC-TEST a later copy")), null, bumpIfExists: true)!.Entry;
        Assert.True(store.SetPinned(id, true, TestData.Now.AddMinutes(6)));
        var group = store.CreateGroup("Work", "\uE8EC", TestData.Now);
        Assert.True(store.AddToGroup(id, group.Id, TestData.Now));

        var updated = Record("BC-TEST alpha beta gamma delta", TestData.Now.AddMinutes(10));
        Assert.Equal(TrackedDocumentOutcomeKind.Updated, updated.Kind);
        var entry = updated.Entry!;
        Assert.Equal(id, entry.Id);
        Assert.Equal(TestData.Now, entry.LastUsedUtc);
        Assert.Equal(TestData.Now.AddMinutes(10), entry.EditedUtc);
        Assert.True(entry.IsPinned);
        Assert.Contains(group.Id, entry.GroupIds);
        Assert.Equal(ContentHasher.ForText("BC-TEST alpha beta gamma delta"), entry.ContentHash);
        Assert.Equal("BC-TEST alpha beta gamma delta", UnicodeTextCodec.Decode(store.GetFormats(id).Single().Data));
        Assert.Single(store.Query(new ClipQuery { SearchText = "delta" }, TestContext.Current.CancellationToken));

        // Pinned entries lead; the later copy keeps its place after the pin.
        Assert.Equal([id, other.Id], store.Query(new ClipQuery(), TestContext.Current.CancellationToken).Select(e => e.Id));
    }

    /// <summary>A version that loses more than a fifth leaves the entry as it was and becomes a new copy.</summary>
    [Fact]
    public void Record_NewCopy_KeepsTheOldVersion()
    {
        string old = "BC-TEST " + new string('a', 92);
        long id = Record(old, TestData.Now).Entry!.Id;
        var split = Record("BC-TEST " + new string('a', 50), TestData.Now.AddMinutes(1));
        Assert.Equal(TrackedDocumentOutcomeKind.NewCopy, split.Kind);
        Assert.Equal(id, split.PreviousEntryId);
        Assert.NotEqual(id, split.Entry!.Id);
        Assert.Equal(old, UnicodeTextCodec.Decode(store.GetFormats(id).Single().Data));
        Assert.Equal(2, store.Query(new ClipQuery(), TestContext.Current.CancellationToken).Count);

        // The document follows its new copy: a small change updates that one.
        var after = Record("BC-TEST " + new string('a', 51), TestData.Now.AddMinutes(2));
        Assert.Equal(TrackedDocumentOutcomeKind.Updated, after.Kind);
        Assert.Equal(split.Entry.Id, after.Entry!.Id);
    }

    /// <summary>
    /// A deleted entry's text is never recorded again while the tab holds it; the tab's next change is recorded as a new
    /// entry, like a new copy.
    /// </summary>
    [Fact]
    public void Record_DeletedEntry_SameTextStaysGone_ChangeComesBack()
    {
        long id = Record("BC-TEST secret plan", TestData.Now).Entry!.Id;
        Assert.True(store.Delete(id, TestData.Now.AddMinutes(1)));
        Assert.Equal(TrackedDocumentOutcomeKind.Unchanged, Record("BC-TEST secret plan", TestData.Now.AddMinutes(2)).Kind);
        Assert.Empty(store.Query(new ClipQuery(), TestContext.Current.CancellationToken));

        var changed = Record("BC-TEST secret plan B", TestData.Now.AddMinutes(3));
        Assert.Equal(TrackedDocumentOutcomeKind.Created, changed.Kind);
        Assert.Single(store.Query(new ClipQuery(), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// An import never resurrects deleted content (the tombstone) and is remembered without an entry; text older than the
    /// last clear is suppressed too.
    /// </summary>
    [Fact]
    public void Record_Import_RespectsTombstonesAndTheLastClear()
    {
        var copy = TestData.Text("BC-TEST deleted before", TestData.Now);
        long id = store.Upsert(copy, Classify(copy), null, bumpIfExists: true)!.Entry.Id;
        Assert.True(store.Delete(id, TestData.Now.AddMinutes(1)));
        var suppressed = store.RecordDocument(Document("BC-TEST deleted before", TestData.Now.AddMinutes(2)), Classify("BC-TEST deleted before"), isImport: true);
        Assert.Equal(TrackedDocumentOutcomeKind.Suppressed, suppressed.Kind);
        Assert.Equal(TrackedDocumentOutcomeKind.Unchanged, Record("BC-TEST deleted before", TestData.Now.AddMinutes(3)).Kind);

        store.ClearAll(TestData.Now.AddHours(1));
        var old = store.RecordDocument(Document("BC-TEST written before the clear", TestData.Now.AddMinutes(30), key: Key + "x"), Classify("BC-TEST written before the clear"), isImport: true);
        Assert.Equal(TrackedDocumentOutcomeKind.Suppressed, old.Kind);
        Assert.Empty(store.Query(new ClipQuery(), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// An import that meets the same text in history takes that entry without moving it; a live first version bumps it.
    /// </summary>
    [Fact]
    public void Record_Duplicate_ImportKeepsItsPlace_LiveBumps()
    {
        var copy = TestData.Text("BC-TEST shared", TestData.Now);
        long id = store.Upsert(copy, Classify(copy), null, bumpIfExists: true)!.Entry.Id;
        var imported = store.RecordDocument(Document("BC-TEST shared", TestData.Now.AddMinutes(5)), Classify("BC-TEST shared"), isImport: true);
        Assert.Equal(id, imported.Entry!.Id);
        Assert.False(imported.IsNewEntry);
        Assert.Equal(TestData.Now, imported.Entry.LastUsedUtc);

        var live = store.RecordDocument(Document("BC-TEST shared", TestData.Now.AddMinutes(9), key: Key + "2"), Classify("BC-TEST shared"), isImport: false);
        Assert.Equal(id, live.Entry!.Id);
        Assert.Equal(TestData.Now.AddMinutes(9), live.Entry.LastUsedUtc);
    }

    /// <summary>
    /// An update whose text another entry already holds moves the document there (the hash is unique) and keeps both
    /// entries' texts.
    /// </summary>
    [Fact]
    public void Record_UpdateOntoAnExistingText_FollowsThatEntry()
    {
        long tab = Record("BC-TEST note one", TestData.Now).Entry!.Id;
        var copy = TestData.Text("BC-TEST note one!", TestData.Now.AddMinutes(1));
        long other = store.Upsert(copy, Classify(copy), null, bumpIfExists: true)!.Entry.Id;
        var moved = Record("BC-TEST note one!", TestData.Now.AddMinutes(2));
        Assert.Equal(TrackedDocumentOutcomeKind.Updated, moved.Kind);
        Assert.Equal(other, moved.Entry!.Id);
        Assert.Equal("BC-TEST note one", UnicodeTextCodec.Decode(store.GetFormats(tab).Single().Data));
        Assert.Equal(other, store.GetDocuments(NotepadPlusPlusSources.DocumentScope).Single().EntryId);
    }

    /// <summary>A renamed tab renames its entry, through a new name for the same text or a moved key.</summary>
    [Fact]
    public void Rename_TitleAndKey()
    {
        long id = Record("BC-TEST rename me", TestData.Now).Entry!.Id;
        var renamed = store.RecordDocument(Document("BC-TEST rename me", TestData.Now, title: "Plans"), Classify("BC-TEST rename me"), isImport: false);
        Assert.Equal(TrackedDocumentOutcomeKind.Unchanged, renamed.Kind);
        Assert.Equal("Plans", renamed.Entry!.SourceTitle);

        Assert.True(store.RenameDocument(NotepadPlusPlusSources.DocumentScope, Key, Key + "-moved", "Ideas"));
        Assert.Equal("Ideas", store.GetEntry(id)!.SourceTitle);
        Assert.False(store.RenameDocument(NotepadPlusPlusSources.DocumentScope, Key, Key + "-other", null));
        Assert.Equal(Key + "-moved", store.GetDocuments(NotepadPlusPlusSources.DocumentScope).Single().Key);

        Assert.Equal(1, store.RemoveDocuments(NotepadPlusPlusSources.DocumentScope, [Key + "-moved"]));
        Assert.Empty(store.GetDocuments(NotepadPlusPlusSources.DocumentScope));
        Assert.NotNull(store.GetEntry(id));
    }

    /// <summary>
    /// The Notepad++ filter lists both origins and every copy made in Notepad++, never another app's copies.
    /// </summary>
    [Fact]
    public void Filter_ListsTabsFilesAndNotepadPlusPlusCopies()
    {
        long tab = Record("BC-TEST tab text", TestData.Now).Entry!.Id;
        var file = NotepadPlusPlusSources.FileCapture(@"C:\BC-TEST\notes.md", TestData.Now.AddMinutes(1), Exe);
        long opened = store.Upsert(file, Classify(file), null, bumpIfExists: true)!.Entry.Id;
        var copied = TestData.Text("BC-TEST copied in Notepad++", TestData.Now.AddMinutes(2), source: NotepadPlusPlusSources.Source(Exe));
        long copy = store.Upsert(copied, Classify(copied), null, bumpIfExists: true)!.Entry.Id;
        var elsewhere = TestData.Text("BC-TEST copied in Notepad", TestData.Now.AddMinutes(3), source: TestData.Notepad);
        store.Upsert(elsewhere, Classify(elsewhere), null, bumpIfExists: true);

        var listed = store.Query(new ClipQuery { Filter = ClipFilter.NotepadPlusPlus }, TestContext.Current.CancellationToken).Select(e => e.Id).ToArray();
        Assert.Equal([copy, opened, tab], listed);
        Assert.True(store.GetEntry(opened)!.IsPathText);
        Assert.Contains(store.Query(new ClipQuery { Filter = ClipFilter.Files }, TestContext.Current.CancellationToken), e => e.Id == opened);
    }

    /// <summary>
    /// Reopening a file moves its one entry from its old date to now, and lifts a tombstone (opening is explicit).
    /// </summary>
    [Fact]
    public void FileOpened_Reopen_MovesTheOneEntry()
    {
        var first = NotepadPlusPlusSources.FileCapture(@"C:\BC-TEST\a.txt", TestData.Now, Exe);
        long id = store.Upsert(first, Classify(first), null, bumpIfExists: true)!.Entry.Id;
        var later = TestData.Text("BC-TEST something else", TestData.Now.AddMinutes(1));
        store.Upsert(later, Classify(later), null, bumpIfExists: true);
        var again = NotepadPlusPlusSources.FileCapture(@"C:\BC-TEST\a.txt", TestData.Now.AddMinutes(2), Exe);
        var bumped = store.Upsert(again, Classify(again), null, bumpIfExists: true)!;
        Assert.False(bumped.IsNew);
        Assert.Equal(id, bumped.Entry.Id);
        Assert.Equal(TestData.Now.AddMinutes(2), bumped.Entry.LastUsedUtc);
        Assert.Equal(id, store.Query(new ClipQuery(), TestContext.Current.CancellationToken).First().Id);

        Assert.True(store.Delete(id, TestData.Now.AddMinutes(3)));
        var reopened = NotepadPlusPlusSources.FileCapture(@"C:\BC-TEST\a.txt", TestData.Now.AddMinutes(4), Exe);
        Assert.NotNull(store.Upsert(reopened, Classify(reopened), null, bumpIfExists: true));
    }

    /// <summary>
    /// A store from before the columns and the table gets them at the next start: existing rows read without a title and
    /// never edited, and recording works.
    /// </summary>
    [Fact]
    public void Schema_AddedToAnOlderStore()
    {
        var copy = TestData.Text("BC-TEST from an older build", TestData.Now);
        long id = store.Upsert(copy, Classify(copy), null, bumpIfExists: true)!.Entry.Id;
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE tracked_documents; ALTER TABLE clips DROP COLUMN source_title; ALTER TABLE clips DROP COLUMN edited_utc;";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var reopened = new ClipStore(temp.DatabasePath);
        reopened.Initialize();
        var entry = reopened.GetEntry(id)!;
        Assert.Null(entry.SourceTitle);
        Assert.Null(entry.EditedUtc);
        store = reopened;
        Assert.Equal(TrackedDocumentOutcomeKind.Created, Record("BC-TEST after the upgrade", TestData.Now).Kind);
    }

    /// <summary>
    /// Through the service: pause skips live versions but not the first activation's import; ignored apps, the size limit
    /// and "Forget forever" always apply.
    /// </summary>
    /// <returns>A task completing after the checks.</returns>
    [Fact]
    public async Task Service_AppliesTheCaptureRules()
    {
        var history = Start();
        rules = rules with { IsPaused = true };
        Assert.Equal(TrackedDocumentOutcomeKind.Skipped, (await history.RecordDocumentAsync(Document("BC-TEST while paused", TestData.Now))).Kind);
        var imported = await history.RecordDocumentsAsync([Document("BC-TEST while paused", TestData.Now)], isImport: true);
        Assert.Equal(TrackedDocumentOutcomeKind.Created, imported.Single().Kind);

        rules = new CaptureRules { Retention = RetentionPolicy.Unlimited, IgnoredProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "notepad++" } };
        Assert.Equal(TrackedDocumentOutcomeKind.Skipped, (await history.RecordDocumentAsync(Document("BC-TEST ignored", TestData.Now, key: Key + "i"))).Kind);

        rules = new CaptureRules { Retention = RetentionPolicy.Unlimited, MaxItemBytes = 10 };
        Assert.Equal(TrackedDocumentOutcomeKind.Skipped, (await history.RecordDocumentAsync(Document("BC-TEST too large", TestData.Now, key: Key + "s"))).Kind);

        rules = new CaptureRules { Retention = RetentionPolicy.Unlimited };
        var copy = TestData.Text("BC-TEST forgotten text", TestData.Now);
        var stored = await history.AddAsync(copy);
        await history.ForgetAsync(stored!.Id);
        Assert.Equal(TrackedDocumentOutcomeKind.Skipped, (await history.RecordDocumentAsync(Document("BC-TEST forgotten text", TestData.Now, key: Key + "f"))).Kind);
    }

    /// <summary>
    /// Through the service: a new entry raises Added, an update in place raises Updated, a batch raises one Reset — and the
    /// Notepad++ tab's witness follows (the tab shows with its first entry).
    /// </summary>
    /// <returns>A task completing after the checks.</returns>
    [Fact]
    public async Task Service_EventsAndTheNotepadPlusPlusTab()
    {
        var history = Start();
        await history.PruneAsync();
        Assert.False(history.HasNotepadPlusPlusEntries);

        var created = await history.RecordDocumentAsync(Document("BC-TEST one", TestData.Now));
        Assert.True(history.HasNotepadPlusPlusEntries);
        await history.RecordDocumentAsync(Document("BC-TEST one two", TestData.Now.AddMinutes(1)));
        await history.RecordDocumentsAsync([Document("BC-TEST other tab", TestData.Now, key: Key + "o")], isImport: false);
        ClipChangeKind[] kinds;
        lock (events)
        {
            kinds = [.. events.Select(e => e.Kind)];
        }

        Assert.Equal([ClipChangeKind.Added, ClipChangeKind.Updated, ClipChangeKind.Reset], kinds);

        await history.DeleteAsync(created.Entry!.Id);
        Assert.True(history.HasNotepadPlusPlusEntries);
        var remaining = store.Query(new ClipQuery { Filter = ClipFilter.NotepadPlusPlus }, TestContext.Current.CancellationToken).Single();
        await history.DeleteAsync(remaining.Id);
        Assert.False(history.HasNotepadPlusPlusEntries);
    }

    /// <summary>Starts a history service over the test store, recording its change events.</summary>
    /// <returns>The started service.</returns>
    private ClipHistoryService Start()
    {
        service = new ClipHistoryService(store, null, () => rules);
        service.Changed += (_, change) =>
        {
            lock (events)
            {
                events.Add(change);
            }
        };
        service.Start();
        return service;
    }

    /// <summary>Records a version of the test tab straight into the store (a live version).</summary>
    /// <param name="text">The tab's text.</param>
    /// <param name="at">When Notepad++ wrote it.</param>
    /// <returns>The outcome.</returns>
    private TrackedDocumentOutcome Record(string text, DateTimeOffset at) =>
        store.RecordDocument(Document(text, at), Classify(text), isImport: false);

    /// <summary>A version of a Notepad++ tab.</summary>
    /// <param name="text">The tab's text.</param>
    /// <param name="at">When Notepad++ wrote it.</param>
    /// <param name="key">The tab's backup key (default: the test tab).</param>
    /// <param name="title">The tab's name (default: "new 3").</param>
    /// <returns>The document.</returns>
    private static TrackedDocument Document(string text, DateTimeOffset at, string key = Key, string title = "new 3") =>
        new(NotepadPlusPlusSources.DocumentScope, key, title, NotepadPlusPlusSources.TabCapture(text, title, at, Exe));

    /// <summary>Classifies a text as a tab's capture would be classified.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The classification.</returns>
    private static ClassifiedClip Classify(string text) => ContentClassifier.Classify(NotepadPlusPlusSources.TabCapture(text, null, TestData.Now, Exe))!;

    /// <summary>Classifies a capture.</summary>
    /// <param name="capture">The capture.</param>
    /// <returns>The classification.</returns>
    private static ClassifiedClip Classify(ClipCapture capture) => ContentClassifier.Classify(capture)!;
}
