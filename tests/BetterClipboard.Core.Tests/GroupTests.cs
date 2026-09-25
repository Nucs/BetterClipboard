using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for groups in the store: create/rename/re-icon/delete, membership and the group filter, protection
/// from retention and "clear" (like a pin), the reset retention clock when an item leaves its last group,
/// and the additive schema on a database created before groups existed.
/// </summary>
public sealed class GroupStoreTests : IDisposable
{
    private static readonly string Star = GroupIconCatalog.All[0].Glyph;
    private static readonly string Heart = GroupIconCatalog.All[1].Glyph;

    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private readonly ClipStore store;

    /// <summary>Creates a fresh store per test.</summary>
    public GroupStoreTests()
    {
        store = new ClipStore(temp.DatabasePath);
        store.Initialize();
    }

    /// <summary>Deletes the temp database.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>Groups come back in creation order with counts; names are trimmed; rename and re-icon apply; delete removes.</summary>
    [Fact]
    public void Groups_CreateListUpdateDelete()
    {
        var work = store.CreateGroup("  Work  ", Star, TestData.Now);
        var fun = store.CreateGroup("Fun", Heart, TestData.Now);
        Assert.Equal("Work", work.Name);
        Assert.Equal([(work.Id, "Work", 0), (fun.Id, "Fun", 1)], store.GetGroups().Select(g => (g.Id, g.Name, g.SortOrder)));

        Assert.True(store.UpdateGroup(work.Id, "Office", null));
        Assert.True(store.UpdateGroup(fun.Id, null, Star));
        var groups = store.GetGroups();
        Assert.Equal(("Office", Star), (groups[0].Name, groups[0].Glyph));
        Assert.Equal(("Fun", Star), (groups[1].Name, groups[1].Glyph));
        Assert.False(store.UpdateGroup(999, "Nobody", null));

        Assert.Empty(store.DeleteGroup(fun.Id, TestData.Now));
        Assert.Equal([work.Id], store.GetGroups().Select(g => g.Id));

        // A deleted group's id is never reused (AUTOINCREMENT): a stale id can't hit a new group.
        Assert.True(store.CreateGroup("Next", Heart, TestData.Now).Id > fun.Id);
    }

    /// <summary>Blank or overlong names and anything but one Private Use Area character as icon are refused.</summary>
    [Fact]
    public void Groups_ValidateNameAndGlyph()
    {
        Assert.Throws<ArgumentException>(() => store.CreateGroup("   ", Star, TestData.Now));
        Assert.Throws<ArgumentException>(() => store.CreateGroup(new string('x', ClipGroup.MaxNameLength + 1), Star, TestData.Now));
        Assert.Throws<ArgumentException>(() => store.CreateGroup("Letters", "A", TestData.Now));
        Assert.Throws<ArgumentException>(() => store.CreateGroup("Two", Star + Heart, TestData.Now));
        Assert.Throws<ArgumentException>(() => store.CreateGroup("Emoji", "😀", TestData.Now));
        var ok = store.CreateGroup(new string('x', ClipGroup.MaxNameLength), Star, TestData.Now);
        Assert.Throws<ArgumentException>(() => store.UpdateGroup(ok.Id, "", null));
        Assert.Throws<ArgumentException>(() => store.UpdateGroup(ok.Id, null, "x"));
        Assert.Empty(store.GetGroups().Where(g => g.Name.Trim().Length == 0));
    }

    /// <summary>
    /// Membership: entries report their groups, adding twice is a no-op, missing entries or groups are quiet
    /// no-ops, the group filter combines with kind filters and search, and counts follow.
    /// </summary>
    [Fact]
    public void Membership_QueryAndCounts()
    {
        var work = store.CreateGroup("Work", Star, TestData.Now);
        var fun = store.CreateGroup("Fun", Heart, TestData.Now);
        var text = Add(TestData.Text("BC-TEST quarterly report", TestData.Now.AddMinutes(-3)));
        var link = Add(TestData.Text("https://example.com/BC-TEST", TestData.Now.AddMinutes(-2)));
        var other = Add(TestData.Text("BC-TEST unrelated", TestData.Now.AddMinutes(-1)));

        Assert.True(store.AddToGroup(text.Id, work.Id, TestData.Now));
        Assert.True(store.AddToGroup(link.Id, work.Id, TestData.Now));
        Assert.True(store.AddToGroup(link.Id, fun.Id, TestData.Now));
        Assert.False(store.AddToGroup(link.Id, fun.Id, TestData.Now));
        Assert.False(store.AddToGroup(99_999, work.Id, TestData.Now));
        Assert.False(store.AddToGroup(text.Id, 99_999, TestData.Now));

        Assert.Equal([work.Id, fun.Id], store.GetEntry(link.Id)!.GroupIds);
        Assert.True(store.GetEntry(text.Id)!.IsGrouped);
        Assert.False(store.GetEntry(other.Id)!.IsGrouped);

        Assert.Equal([link.Id, text.Id], store.Query(new ClipQuery { GroupId = work.Id }).Select(e => e.Id));
        Assert.Equal([link.Id], store.Query(new ClipQuery { GroupId = work.Id, Filter = ClipFilter.Links }).Select(e => e.Id));
        Assert.Equal([text.Id], store.Query(new ClipQuery { GroupId = work.Id, SearchText = "quarterly" }).Select(e => e.Id));
        Assert.Equal([link.Id], store.Query(new ClipQuery { GroupId = fun.Id }).Select(e => e.Id));
        Assert.Equal(3, store.Query(new ClipQuery()).Count);

        Assert.Equal([2L, 1L], store.GetGroups().Select(g => g.ItemCount));
        Assert.Equal(2, store.GetStats().GroupedCount);
    }

    /// <summary>Grouped entries survive every retention rule and "clear", like pinned ones; "clear everything" removes them but keeps the groups.</summary>
    [Fact]
    public void Grouped_AreKeptLikePinned()
    {
        var keep = store.CreateGroup("Keep", Star, TestData.Now);
        var old = Add(TestData.Text("BC-TEST grouped and ancient", TestData.Now.AddDays(-400)));
        Add(TestData.Text("BC-TEST ungrouped and ancient", TestData.Now.AddDays(-400)));
        store.AddToGroup(old.Id, keep.Id, TestData.Now);

        Assert.Equal(1, store.Prune(new RetentionPolicy(0, TimeSpan.FromDays(30), 0), TestData.Now));
        Assert.Equal([old.Id], store.Query(new ClipQuery()).Select(e => e.Id));

        for (int i = 0; i < 3; i++)
        {
            Add(TestData.Text($"BC-TEST new {i}", TestData.Now.AddMinutes(i)));
        }

        // Count: the grouped entry is neither pruned nor counted (1 slot, 3 new ones → 2 pruned).
        Assert.Equal(2, store.Prune(new RetentionPolicy(1, null, 0), TestData.Now));
        Assert.Contains(old.Id, store.Query(new ClipQuery()).Select(e => e.Id));

        // Size: a 1-byte budget prunes everything unprotected, never the grouped entry.
        Assert.Equal(1, store.Prune(new RetentionPolicy(0, null, 1), TestData.Now));
        Assert.Equal([old.Id], store.Query(new ClipQuery()).Select(e => e.Id));

        Add(TestData.Text("BC-TEST clear me", TestData.Now));
        Assert.Equal(1, store.ClearUnpinned(TestData.Now.AddMinutes(10)));
        Assert.Equal([old.Id], store.Query(new ClipQuery()).Select(e => e.Id));

        Assert.Equal(1, store.ClearAll(TestData.Now.AddMinutes(11)));
        Assert.Empty(store.Query(new ClipQuery()));
        Assert.Equal(0, store.GetGroups().Single().ItemCount);
    }

    /// <summary>
    /// Leaving the last group restarts the retention clock: an entry last used 100 days ago is not pruned by
    /// a 30-day limit right after it was ungrouped, ranks as newest for the count limit, and keeps its place
    /// in the list (last-used time unchanged). Leaving one of two groups does not reset anything.
    /// </summary>
    [Fact]
    public void Ungrouping_ResetsTheRetentionClockOnly()
    {
        var a = store.CreateGroup("A", Star, TestData.Now);
        var b = store.CreateGroup("B", Heart, TestData.Now);
        var ancient = Add(TestData.Text("BC-TEST ancient", TestData.Now.AddDays(-100)));
        var recent = Add(TestData.Text("BC-TEST recent", TestData.Now.AddDays(-1)));
        store.AddToGroup(ancient.Id, a.Id, TestData.Now.AddDays(-99));
        store.AddToGroup(ancient.Id, b.Id, TestData.Now.AddDays(-99));

        Assert.Equal(new GroupRemoval(true, false), store.RemoveFromGroup(ancient.Id, a.Id, TestData.Now));
        Assert.Equal(new GroupRemoval(true, true), store.RemoveFromGroup(ancient.Id, b.Id, TestData.Now));
        Assert.Equal(new GroupRemoval(false, false), store.RemoveFromGroup(ancient.Id, b.Id, TestData.Now));
        Assert.Equal(TestData.Now.AddDays(-100), store.GetEntry(ancient.Id)!.LastUsedUtc);
        Assert.Equal([recent.Id, ancient.Id], store.Query(new ClipQuery()).Select(e => e.Id));

        Assert.Equal(0, store.Prune(new RetentionPolicy(0, TimeSpan.FromDays(30), 0), TestData.Now.AddDays(1)));
        Assert.Equal(1, store.Prune(new RetentionPolicy(1, null, 0), TestData.Now.AddDays(1)));
        Assert.Equal([ancient.Id], store.Query(new ClipQuery()).Select(e => e.Id));

        // The fresh clock runs out like any other: 31 days after the ungrouping, the age limit applies again.
        Assert.Equal(1, store.Prune(new RetentionPolicy(0, TimeSpan.FromDays(30), 0), TestData.Now.AddDays(31)));
    }

    /// <summary>
    /// Deleting a group keeps its items; only those in no other group get the reset clock (and lose their
    /// protection), and the former members are reported.
    /// </summary>
    [Fact]
    public void DeletingAGroup_KeepsItemsAndResetsOnlyTheUnprotected()
    {
        var doomed = store.CreateGroup("Doomed", Star, TestData.Now);
        var stays = store.CreateGroup("Stays", Heart, TestData.Now);
        var both = Add(TestData.Text("BC-TEST in both", TestData.Now.AddDays(-100)));
        var only = Add(TestData.Text("BC-TEST only doomed", TestData.Now.AddDays(-100)));
        store.AddToGroup(both.Id, doomed.Id, TestData.Now);
        store.AddToGroup(both.Id, stays.Id, TestData.Now);
        store.AddToGroup(only.Id, doomed.Id, TestData.Now);

        Assert.Equal([both.Id, only.Id], store.DeleteGroup(doomed.Id, TestData.Now).Order());
        Assert.Equal([stays.Id], store.GetEntry(both.Id)!.GroupIds);
        Assert.Empty(store.GetEntry(only.Id)!.GroupIds);

        // The reset clock protects "only" from the 30-day limit today, but no longer from a clear.
        Assert.Equal(0, store.Prune(new RetentionPolicy(0, TimeSpan.FromDays(30), 0), TestData.Now));
        Assert.Equal(1, store.ClearUnpinned(TestData.Now.AddMinutes(1)));
        Assert.Equal([both.Id], store.Query(new ClipQuery()).Select(e => e.Id));
    }

    /// <summary>Deleting an entry removes its memberships (cascade), so counts stay honest.</summary>
    [Fact]
    public void DeletingAnEntry_RemovesItsMemberships()
    {
        var group = store.CreateGroup("G", Star, TestData.Now);
        var entry = Add(TestData.Text("BC-TEST member"));
        store.AddToGroup(entry.Id, group.Id, TestData.Now);
        Assert.True(store.Delete(entry.Id, TestData.Now));
        Assert.Equal(0, store.GetGroups().Single().ItemCount);
        Assert.Equal(0, store.GetStats().GroupedCount);
    }

    /// <summary>
    /// A database from before groups (no tables, no retention column) gets them on the next Initialize,
    /// keeps its entries, and Initialize stays idempotent.
    /// </summary>
    [Fact]
    public void Initialize_AddsGroupsToAnOlderDatabase()
    {
        var entry = Add(TestData.Text("BC-TEST from before groups"));
        ExecuteRaw("DROP TABLE clip_groups; DROP TABLE groups; ALTER TABLE clips DROP COLUMN retain_from_utc;");

        var reopened = new ClipStore(temp.DatabasePath);
        reopened.Initialize();
        reopened.Initialize();
        var group = reopened.CreateGroup("Later", Star, TestData.Now);
        Assert.True(reopened.AddToGroup(entry.Id, group.Id, TestData.Now));
        Assert.Equal([group.Id], reopened.GetEntry(entry.Id)!.GroupIds);
        Assert.Equal(new GroupRemoval(true, true), reopened.RemoveFromGroup(entry.Id, group.Id, TestData.Now));
    }

    /// <summary>Stores a capture as a live copy.</summary>
    /// <param name="capture">The capture.</param>
    /// <returns>The stored entry.</returns>
    private ClipEntry Add(ClipCapture capture) =>
        store.Upsert(capture, ContentClassifier.Classify(capture)!, null, bumpIfExists: true)!.Entry;

    /// <summary>Runs SQL directly against the (plaintext test) database.</summary>
    /// <param name="sql">Statements.</param>
    private void ExecuteRaw(string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// Tests for the group operations of <see cref="ClipHistoryService"/>: they run through the worker and
/// announce themselves (per-entry updates for memberships, one reset for a deleted group).
/// </summary>
public sealed class GroupServiceTests : IAsyncLifetime
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private readonly List<ClipChangedEventArgs> changes = [];
    private int groupEvents;
    private ClipHistoryService service = null!;

    /// <summary>Starts a service over a temp store and records its events.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask InitializeAsync()
    {
        var store = new ClipStore(temp.DatabasePath);
        store.Initialize();
        service = new ClipHistoryService(store, null, () => new CaptureRules { Retention = RetentionPolicy.Unlimited });
        service.Changed += (_, e) =>
        {
            lock (changes)
            {
                changes.Add(e);
            }
        };
        service.GroupsChanged += (_, _) => Interlocked.Increment(ref groupEvents);
        service.Start();
        return ValueTask.CompletedTask;
    }

    /// <summary>Stops the service and deletes the store.</summary>
    /// <returns>A task.</returns>
    public async ValueTask DisposeAsync()
    {
        await service.DisposeAsync();
        temp.Dispose();
    }

    /// <summary>Create, add (updates per entry), remove (reset reported), delete (one reset), each with a groups event.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task GroupOperations_AreAnnounced()
    {
        var entry = (await service.AddAsync(TestData.Text("BC-TEST card")))!;
        var group = await service.CreateGroupAsync("Work", GroupIconCatalog.Default.Glyph);
        Assert.Equal(1, groupEvents);

        changes.Clear();
        Assert.Equal(1, await service.AddToGroupAsync([entry.Id, entry.Id, 424242], group.Id));
        Assert.Equal(2, groupEvents);
        var updated = Assert.Single(Snapshot());
        Assert.Equal(ClipChangeKind.Updated, updated.Kind);
        Assert.Equal([group.Id], updated.Entry!.GroupIds);
        Assert.Equal(0, await service.AddToGroupAsync([entry.Id], group.Id));
        Assert.Equal(2, groupEvents);

        Assert.True(await service.UpdateGroupAsync(group.Id, "Office", null));
        Assert.Equal("Office", (await service.GetGroupsAsync()).Single().Name);

        changes.Clear();
        Assert.Equal(new GroupRemoval(true, true), await service.RemoveFromGroupAsync(entry.Id, group.Id));
        Assert.Empty(Assert.Single(Snapshot()).Entry!.GroupIds);

        await service.AddToGroupAsync([entry.Id], group.Id);
        changes.Clear();
        Assert.Equal(1, await service.DeleteGroupAsync(group.Id));
        Assert.Equal(ClipChangeKind.Reset, Assert.Single(Snapshot()).Kind);
        Assert.Empty(await service.GetGroupsAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateGroupAsync("Bad", "B"));
    }

    /// <summary>Snapshot of the recorded changes.</summary>
    /// <returns>The changes in order.</returns>
    private List<ClipChangedEventArgs> Snapshot()
    {
        lock (changes)
        {
            return [.. changes];
        }
    }
}

/// <summary>Tests for the group icon catalog.</summary>
public sealed class GroupIconCatalogTests
{
    /// <summary>Every icon is one valid Private Use Area glyph; glyphs and names are unique; names are short.</summary>
    [Fact]
    public void Catalog_IsValidAndUnique()
    {
        var icons = GroupIconCatalog.All;
        Assert.True(icons.Count >= 40);
        Assert.All(icons, icon => Assert.True(ClipGroup.IsValidGlyph(icon.Glyph), icon.Name));
        Assert.All(icons, icon => Assert.InRange(icon.Name.Length, 1, 16));
        Assert.Equal(icons.Count, icons.Select(i => i.Glyph).Distinct().Count());
        Assert.Equal(icons.Count, icons.Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("Star", GroupIconCatalog.NameOf(GroupIconCatalog.Default.Glyph));
        Assert.Null(GroupIconCatalog.NameOf("x"));
    }
}
