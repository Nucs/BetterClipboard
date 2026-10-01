using System.Globalization;
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

        Assert.Equal([link.Id, text.Id], store.Query(new ClipQuery { GroupIds = [work.Id] }).Select(e => e.Id));
        Assert.Equal([link.Id], store.Query(new ClipQuery { GroupIds = [work.Id], Filter = ClipFilter.Links }).Select(e => e.Id));
        Assert.Equal([text.Id], store.Query(new ClipQuery { GroupIds = [work.Id], SearchText = "quarterly" }).Select(e => e.Id));
        Assert.Equal([link.Id], store.Query(new ClipQuery { GroupIds = [fun.Id] }).Select(e => e.Id));
        Assert.Equal(3, store.Query(new ClipQuery()).Count);

        Assert.Equal([2L, 1L], store.GetGroups().Select(g => g.ItemCount));
        Assert.Equal(2, store.GetStats().GroupedCount);
    }

    /// <summary>
    /// Several groups in one query (the panel's Ctrl/Shift multi-selection) are merged: an entry in two of them comes
    /// once, the page is interleaved by recency like any other — never group by group — and paging, filters, the search
    /// and its toggles apply to the merged list. The ids' order, repeats and unknown ids change nothing; no ids means no
    /// group filter. <see cref="ClipStore.CountInGroups"/> counts exactly what the merged list shows.
    /// </summary>
    [Fact]
    public void MergedGroups_AreOneListInTheUsualOrder()
    {
        var work = store.CreateGroup("Work", Star, TestData.Now);
        var home = store.CreateGroup("Home", Heart, TestData.Now);
        var empty = store.CreateGroup("Empty", Star, TestData.Now);
        var workOnly = Add(TestData.Text("BC-TEST work report", TestData.Now.AddMinutes(-6)));
        var homeOnly = Add(TestData.Text("BC-TEST home groceries", TestData.Now.AddMinutes(-5)));
        var shared = Add(TestData.Text("BC-TEST shared report", TestData.Now.AddMinutes(-4)));
        var workLink = Add(TestData.Text("https://example.com/BC-TEST/work", TestData.Now.AddMinutes(-3)));
        var homeNewest = Add(TestData.Text("BC-TEST home report", TestData.Now.AddMinutes(-2)));
        Add(TestData.Text("BC-TEST report in no group", TestData.Now.AddMinutes(-1)));
        foreach (var entry in new[] { workOnly, shared, workLink })
        {
            store.AddToGroup(entry.Id, work.Id, TestData.Now);
        }

        foreach (var entry in new[] { homeOnly, shared, homeNewest })
        {
            store.AddToGroup(entry.Id, home.Id, TestData.Now);
        }

        long[] merged = [homeNewest.Id, workLink.Id, shared.Id, homeOnly.Id, workOnly.Id];
        Assert.Equal(merged, Ids(new ClipQuery { GroupIds = [work.Id, home.Id] }));
        Assert.Equal(merged, Ids(new ClipQuery { GroupIds = [home.Id, work.Id, home.Id, 999_999] }));
        Assert.Equal(6, store.Query(new ClipQuery { GroupIds = [] }).Count);
        Assert.Empty(store.Query(new ClipQuery { GroupIds = [empty.Id, 999_999] }));

        // One list, plain offsets: the third and fourth cards of the merged view.
        Assert.Equal([shared.Id, homeOnly.Id], Ids(new ClipQuery { GroupIds = [work.Id, home.Id], Offset = 2, Limit = 2 }));

        // Filter tabs, the search and its toggles apply to the merged groups (not to "report in no group").
        Assert.Equal([workLink.Id], Ids(new ClipQuery { GroupIds = [work.Id, home.Id], Filter = ClipFilter.Links }));
        Assert.Equal([homeNewest.Id, shared.Id, workOnly.Id], Ids(new ClipQuery { GroupIds = [work.Id, home.Id], SearchText = "report" }));
        Assert.Equal(
            [homeNewest.Id, shared.Id, workOnly.Id],
            Ids(new ClipQuery { GroupIds = [work.Id, home.Id], SearchText = "report$", SearchOptions = SearchOptions.Regex }));
        Assert.Empty(store.Query(new ClipQuery { GroupIds = [work.Id, home.Id], SearchText = "Report", SearchOptions = SearchOptions.MatchCase }));

        // Pinned first holds for the merged list as a whole, not group by group.
        store.SetPinned(workOnly.Id, true, TestData.Now);
        Assert.Equal([workOnly.Id, homeNewest.Id, workLink.Id, shared.Id, homeOnly.Id], Ids(new ClipQuery { GroupIds = [work.Id, home.Id] }));
        Assert.Equal(merged, Ids(new ClipQuery { GroupIds = [work.Id, home.Id], PinnedFirst = false }));

        // "shared" counts once; the groups' own counts add up to one more.
        Assert.Equal(5, store.CountInGroups([work.Id, home.Id]));
        Assert.Equal(6, store.GetGroups().Where(g => g.Id == work.Id || g.Id == home.Id).Sum(g => g.ItemCount));
        Assert.Equal(3, store.CountInGroups([work.Id, work.Id]));
        Assert.Equal(5, store.CountInGroups([work.Id, home.Id, empty.Id, 999_999]));
        Assert.Equal(0, store.CountInGroups([empty.Id]));
        Assert.Equal(0, store.CountInGroups([]));
        Assert.Throws<ArgumentNullException>(() => store.CountInGroups(null!));
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

    /// <summary>Runs a query and keeps only the ids, in page order.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The ids.</returns>
    private List<long> Ids(ClipQuery query) => [.. store.Query(query).Select(e => e.Id)];

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

    /// <summary>
    /// The merged view's count (the footer of several groups shown together) counts an entry in two of them once, and
    /// works on its own copy of the ids: the panel may change its list right after asking.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task CountInGroups_CountsTheMergedList()
    {
        var a = (await service.AddAsync(TestData.Text("BC-TEST a")))!;
        var b = (await service.AddAsync(TestData.Text("BC-TEST b")))!;
        var work = await service.CreateGroupAsync("Work", GroupIconCatalog.All[0].Glyph);
        var home = await service.CreateGroupAsync("Home", GroupIconCatalog.All[1].Glyph);
        await service.AddToGroupAsync([a.Id, b.Id], work.Id);
        await service.AddToGroupAsync([b.Id], home.Id);

        var ids = new List<long> { work.Id, home.Id };
        var counting = service.CountInGroupsAsync(ids);
        ids.Clear();
        Assert.Equal(2, await counting);
        Assert.Equal(1, await service.CountInGroupsAsync([home.Id]));
        Assert.Equal(0, await service.CountInGroupsAsync([]));
        Assert.Throws<ArgumentNullException>(() => { _ = service.CountInGroupsAsync(null!); });
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

/// <summary>
/// Tests for <see cref="GroupSelection"/>: which groups the panel shows after plain, Ctrl, Shift and Ctrl+Shift clicks
/// on the group icons (Explorer's selection keys), the icon menu's toggle, and groups deleted while shown.
/// </summary>
public sealed class GroupSelectionTests
{
    /// <summary>A groups column, top to bottom (ids deliberately not 1, 2, 3: positions and ids must not be confused).</summary>
    private static readonly long[] Column = [10, 20, 30, 40, 50];

    /// <summary>
    /// A plain click shows one group; the same click again goes back to everything (as before groups could be merged);
    /// a click on another group, or on one of several merged ones, shows that group alone.
    /// </summary>
    [Fact]
    public void PlainClick_ShowsOneGroup_AndAgainGoesBackToEverything()
    {
        Assert.True(GroupSelection.None.IsEmpty);
        Assert.Null(GroupSelection.None.Anchor);

        var one = GroupSelection.None.Click(Column, 30, ctrl: false, shift: false);
        Assert.Equal([30L], one.Ids);
        Assert.Equal(30, one.Anchor);
        Assert.False(one.IsEmpty);
        Assert.False(one.IsMerged);
        Assert.True(one.Contains(30));
        Assert.False(one.Contains(20));

        Assert.Same(GroupSelection.None, one.Click(Column, 30, ctrl: false, shift: false));
        Assert.Equal([20L], one.Click(Column, 20, ctrl: false, shift: false).Ids);

        var merged = one.Click(Column, 50, ctrl: true, shift: false);
        Assert.Equal([30L], merged.Click(Column, 30, ctrl: false, shift: false).Ids);
    }

    /// <summary>
    /// Ctrl+click adds a group or takes it out, the ids stay in column order whatever the click order, the anchor moves
    /// to every Ctrl+clicked group (also one taken out, like Explorer), and taking out the last one is the regular view.
    /// The menu's toggle is the same as a Ctrl+click.
    /// </summary>
    [Fact]
    public void CtrlClick_AddsAndTakesOut_InColumnOrder()
    {
        var selection = GroupSelection.None.Click(Column, 50, ctrl: true, shift: false);
        Assert.Equal([50L], selection.Ids);

        selection = selection.Click(Column, 20, ctrl: true, shift: false);
        Assert.Equal([20L, 50L], selection.Ids);
        Assert.Equal(20, selection.Anchor);
        Assert.True(selection.IsMerged);

        selection = selection.Click(Column, 40, ctrl: true, shift: false).Click(Column, 50, ctrl: true, shift: false);
        Assert.Equal([20L, 40L], selection.Ids);
        Assert.Equal(50, selection.Anchor);

        Assert.Equal([20L, 30L, 40L], selection.Toggle(Column, 30).Ids);
        Assert.Equal([40L], selection.Toggle(Column, 20).Ids);
        var none = selection.Click(Column, 20, ctrl: true, shift: false).Click(Column, 40, ctrl: true, shift: false);
        Assert.Same(GroupSelection.None, none);
        Assert.Null(none.Anchor);
    }

    /// <summary>
    /// Shift+click shows the run from the anchor, which stays put so further Shift+clicks pivot around it; without an
    /// anchor the run starts at the clicked group; Ctrl+Shift+click adds the run to what is shown.
    /// </summary>
    [Fact]
    public void ShiftClick_ShowsTheRunFromTheAnchor()
    {
        var selection = GroupSelection.None.Click(Column, 20, ctrl: false, shift: false).Click(Column, 40, ctrl: false, shift: true);
        Assert.Equal([20L, 30L, 40L], selection.Ids);
        Assert.Equal(20, selection.Anchor);

        var pivoted = selection.Click(Column, 10, ctrl: false, shift: true);
        Assert.Equal([10L, 20L], pivoted.Ids);
        Assert.Equal(20, pivoted.Anchor);

        // The run already shown: the same list (no reload), whatever the instance.
        Assert.True(selection.Click(Column, 40, ctrl: false, shift: true).HasSameIds(selection));

        var fresh = GroupSelection.None.Click(Column, 40, ctrl: false, shift: true);
        Assert.Equal([40L], fresh.Ids);
        Assert.Equal(40, fresh.Anchor);

        var added = GroupSelection.None.Click(Column, 10, ctrl: false, shift: false)
            .Click(Column, 30, ctrl: true, shift: false)
            .Click(Column, 50, ctrl: true, shift: true);
        Assert.Equal([10L, 30L, 40L, 50L], added.Ids);
        Assert.Equal(30, added.Anchor);
    }

    /// <summary>
    /// A click on a group that is not in the column changes nothing; a reloaded column drops deleted groups (and an
    /// anchor on one: the next Shift+click starts afresh) and keeps the same instance when nothing vanished; a group
    /// being deleted leaves the view without moving the anchor elsewhere.
    /// </summary>
    [Fact]
    public void DeletedGroups_LeaveTheView()
    {
        var selection = GroupSelection.None.Click(Column, 20, ctrl: false, shift: false).Click(Column, 40, ctrl: true, shift: false);
        Assert.Same(selection, selection.Click(Column, 99, ctrl: false, shift: false));
        Assert.Same(selection, selection.Retain(Column));

        var retained = selection.Retain([10, 20, 30, 50]);
        Assert.Equal([20L], retained.Ids);
        Assert.Null(retained.Anchor);
        Assert.Equal([50L], retained.Click([10, 20, 30, 50], 50, ctrl: false, shift: true).Ids);
        Assert.Same(GroupSelection.None, selection.Retain([10, 30]));

        // The column's order wins, also after a reorder.
        Assert.Equal([40L, 20L], selection.Retain([40, 20]).Ids);

        var without = selection.Without(40);
        Assert.Equal([20L], without.Ids);
        Assert.Null(without.Anchor);
        Assert.Equal(40, selection.Without(20).Anchor);
        Assert.Same(selection, selection.Without(30));
        Assert.Same(GroupSelection.None, without.Without(20));
    }

    /// <summary>The ids cannot be changed through <see cref="GroupSelection.Ids"/>; null arguments are refused.</summary>
    [Fact]
    public void Selection_IsImmutable_AndRefusesNulls()
    {
        var selection = GroupSelection.None.Click(Column, 10, ctrl: false, shift: false);
        Assert.Throws<NotSupportedException>(() => ((IList<long>)selection.Ids).Add(20));
        Assert.Equal([10L], selection.Ids);
        Assert.Throws<ArgumentNullException>(() => selection.Click(null!, 10, ctrl: false, shift: false));
        Assert.Throws<ArgumentNullException>(() => selection.Retain(null!));
        Assert.Throws<ArgumentNullException>(() => selection.HasSameIds(null!));
    }
}

/// <summary>
/// Tests for <see cref="GroupViewText"/>: the header, placeholder, footer, empty-state and menu wording of one group
/// and of several merged ones, named while they fit and counted otherwise.
/// </summary>
public sealed class GroupViewTextTests
{
    private static readonly IReadOnlyList<ClipGroup> One = [Group("Work")];
    private static readonly IReadOnlyList<ClipGroup> Two = [Group("Work"), Group("Home")];
    private static readonly IReadOnlyList<ClipGroup> Three = [Group("Work"), Group("Home"), Group("Ideas")];
    private static readonly IReadOnlyList<ClipGroup> Four = [Group("Work"), Group("Home"), Group("Ideas"), Group("Code")];

    /// <summary>The header names every group, however many; the regular view has no suffix.</summary>
    [Fact]
    public void Title_NamesEveryGroup()
    {
        Assert.Equal(string.Empty, GroupViewText.Title([]));
        Assert.Equal("Work", GroupViewText.Title(One));
        Assert.Equal("Work + Home + Ideas + Code", GroupViewText.Title(Four));
    }

    /// <summary>
    /// The short texts name up to three groups with up to 24 characters of names together, and count beyond; one group
    /// is always named, even a 40-character one (as before groups could be merged).
    /// </summary>
    [Fact]
    public void ShortTexts_NameWhileTheyFit_AndCountOtherwise()
    {
        var invariant = CultureInfo.InvariantCulture;
        Assert.Equal("Work", GroupViewText.ShortName(One, invariant));
        Assert.Equal("Work + Home", GroupViewText.ShortName(Two, invariant));
        Assert.Equal("Work + Home + Ideas", GroupViewText.ShortName(Three, invariant));
        Assert.Equal("4 groups", GroupViewText.ShortName(Four, invariant));

        var longest = new string('x', ClipGroup.MaxNameLength);
        Assert.Equal(longest, GroupViewText.ShortName([Group(longest)], invariant));

        // 24 characters of names fit, 25 do not (the lengths are checked, not assumed).
        ClipGroup[] fits = [Group("Shopping list"), Group("Gift ideas!")];
        ClipGroup[] tooLong = [Group("Shopping list"), Group("Gift ideas!!")];
        Assert.Equal(GroupViewText.MaxNamedLength, fits.Sum(g => g.Name.Length));
        Assert.Equal(GroupViewText.MaxNamedLength + 1, tooLong.Sum(g => g.Name.Length));
        Assert.Equal("Shopping list + Gift ideas!", GroupViewText.ShortName(fits, invariant));
        Assert.Equal("2 groups", GroupViewText.ShortName(tooLong, invariant));

        Assert.Equal("Work", GroupViewText.AnyOf(One, invariant));
        Assert.Equal("Work or Home", GroupViewText.AnyOf(Two, invariant));
        Assert.Equal("Work, Home or Ideas", GroupViewText.AnyOf(Three, invariant));
        Assert.Equal("these 4 groups", GroupViewText.AnyOf(Four, invariant));

        Assert.Equal("Work", GroupViewText.AllOf(One, invariant));
        Assert.Equal("Work and Home", GroupViewText.AllOf(Two, invariant));
        Assert.Equal("Work, Home and Ideas", GroupViewText.AllOf(Three, invariant));
        Assert.Equal("these 2 groups", GroupViewText.AllOf(tooLong, invariant));
        Assert.Equal("these 2 groups", GroupViewText.AnyOf(tooLong, invariant));
    }

    /// <summary>The regular view is not a group view: the short texts refuse no groups, and null.</summary>
    [Fact]
    public void ShortTexts_NeedAGroup()
    {
        Assert.Throws<ArgumentException>(() => GroupViewText.ShortName([]));
        Assert.Throws<ArgumentException>(() => GroupViewText.AnyOf([]));
        Assert.Throws<ArgumentException>(() => GroupViewText.AllOf([]));
        Assert.Throws<ArgumentNullException>(() => GroupViewText.NamesFit(null!));
        Assert.Throws<ArgumentNullException>(() => GroupViewText.Title(null!));
    }

    /// <summary>A group snapshot with only a name that matters.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The group.</returns>
    private static ClipGroup Group(string name) => new(name.Length, name, GroupIconCatalog.Default.Glyph, 0, 0);
}
