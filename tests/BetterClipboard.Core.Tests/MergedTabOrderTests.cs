using BetterClipboard.Core.Model;
using BetterClipboard.Core.Presentation;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for <see cref="MergedTabOrder{TItem}"/>, the order of the tabs that list stored entries next to another app's items
/// (Claude, Codex, Pwsh, Cmd, Everything): newest first by each row's newest activity, one row per content, pins on top only
/// when asked, items without times kept in their order, and pages that place each entry where its time falls.
/// </summary>
public sealed class MergedTabOrderTests
{
    private static readonly DateTimeOffset Now = TestData.Now;

    /// <summary>
    /// The reported fault (2026-11-11: "The first row in Claude tab is always old one"): a prompt pasted from the tab three
    /// days ago, kept as a history entry, led the tab above every prompt sent since. It now goes where its last use falls.
    /// </summary>
    [Fact]
    public void AnOldKeptEntry_NoLongerLeads()
    {
        var rows = Order([Entry(1, "old prompt", Now.AddDays(-3))])
            .Next([Item("sent 1 min ago", Now.AddMinutes(-1)), Item("sent 1 h ago", Now.AddHours(-1)), Item("sent 4 days ago", Now.AddDays(-4))], last: true);

        Assert.Equal(["sent 1 min ago", "sent 1 h ago", "#1", "sent 4 days ago"], Describe(rows));
        Assert.Equal(Now.AddDays(-3), rows[2].Time);
        Assert.Equal(Now.AddMinutes(-1), rows[0].Time);
    }

    /// <summary>An entry used after the newest item (a prompt just pasted from the tab) leads, until something newer comes.</summary>
    [Fact]
    public void ARecentlyUsedEntry_Leads()
    {
        var rows = Order([Entry(1, "pasted just now", Now), Entry(2, "pasted an hour ago", Now.AddHours(-1))])
            .Next([Item("sent 1 min ago", Now.AddMinutes(-1)), Item("sent 2 h ago", Now.AddHours(-2))], last: true);

        Assert.Equal(["#1", "sent 1 min ago", "#2", "sent 2 h ago"], Describe(rows));
    }

    /// <summary>
    /// One row per content, at the newer of the two times: an entry used before its twin was sent again shows where the twin
    /// is, with the twin's time (the card shows it); an entry used after its twin goes by its own time, and the twin is dropped.
    /// </summary>
    [Fact]
    public void ATwin_ShowsOnceAtTheNewerTime()
    {
        var keptLongAgo = Entry(1, "continue", Now.AddDays(-2));
        var usedLately = Entry(2, "commit", Now.AddMinutes(-2));
        var rows = Order([keptLongAgo, usedLately])
            .Next([Item("continue", Now.AddMinutes(-5)), Item("other", Now.AddMinutes(-30)), Item("commit", Now.AddHours(-1))], last: true);

        Assert.Equal(["#2", "#1", "other"], Describe(rows));
        Assert.Equal(Now.AddMinutes(-2), rows[0].Time);
        Assert.Equal(Now.AddMinutes(-5), rows[1].Time);
        Assert.Same(keptLongAgo, rows[1].Entry);
    }

    /// <summary>
    /// Pinned entries lead only while "Show pinned items first" is on (most recently used first), and their twins are not
    /// shown again; with it off they go by time like any entry.
    /// </summary>
    [Fact]
    public void PinnedEntries_LeadOnlyWhenAsked()
    {
        var olderPin = Entry(1, "pinned long ago", Now.AddDays(-30), pinned: true);
        var newerPin = Entry(2, "pinned yesterday", Now.AddDays(-1), pinned: true);
        IReadOnlyList<AppItem> items = [Item("new", Now), Item("pinned long ago", Now.AddMinutes(-1)), Item("old", Now.AddDays(-10))];

        Assert.Equal(["#2", "#1", "new", "old"], Describe(Order([olderPin, newerPin], pinnedFirst: true).Next(items, last: true)));

        // Off: the long-ago pin's twin (sent a minute ago) places it, the other pin goes by its own use.
        Assert.Equal(["new", "#1", "#2", "old"], Describe(Order([olderPin, newerPin], pinnedFirst: false).Next(items, last: true)));
    }

    /// <summary>An entry used at the same moment as an item goes before it.</summary>
    [Fact]
    public void EqualTimes_TheEntryComesFirst()
    {
        var rows = Order([Entry(1, "kept", Now)]).Next([Item("sent", Now)], last: true);
        Assert.Equal(["#1", "sent"], Describe(rows));
    }

    /// <summary>
    /// Paging (the prompt archive): an entry older than every item of a page waits for the page its time falls on, and the
    /// last page places what is left after its items; the footer can count the waiting ones.
    /// </summary>
    [Fact]
    public void Pages_PlaceEachEntryOnThePageItsTimeFallsOn()
    {
        var order = Order([Entry(1, "kept 10 min ago", Now.AddMinutes(-10)), Entry(2, "kept last year", Now.AddYears(-1))]);

        var first = order.Next([Item("a", Now.AddMinutes(-1)), Item("b", Now.AddMinutes(-3))], last: false);
        Assert.Equal(["a", "b"], Describe(first));
        Assert.Equal(2, order.WaitingCount);

        var second = order.Next([Item("c", Now.AddMinutes(-5)), Item("d", Now.AddMinutes(-20))], last: false);
        Assert.Equal(["c", "#1", "d"], Describe(second));
        Assert.Equal(1, order.WaitingCount);

        var last = order.Next([Item("e", Now.AddDays(-2))], last: true);
        Assert.Equal(["e", "#2"], Describe(last));
        Assert.Equal(0, order.WaitingCount);

        // Nothing is left for a page after the last one.
        Assert.Equal(["f"], Describe(order.Next([Item("f", Now.AddDays(-3))], last: true)));
    }

    /// <summary>A twin on a later page pulls its waiting entry there, at the twin's time; one already shown drops its twin.</summary>
    [Fact]
    public void ATwinOnALaterPage_PlacesItsEntryThere()
    {
        var order = Order([Entry(1, "continue", Now.AddDays(-1)), Entry(2, "status", Now.AddMinutes(-1))]);

        Assert.Equal(["#2", "a"], Describe(order.Next([Item("a", Now.AddMinutes(-30))], last: false)));
        var second = order.Next([Item("status", Now.AddHours(-1)), Item("continue", Now.AddHours(-2)), Item("b", Now.AddHours(-3))], last: true);
        Assert.Equal(["#1", "b"], Describe(second));
        Assert.Equal(Now.AddHours(-2), second[0].Time);
    }

    /// <summary>
    /// Items without times (PowerShell's history) keep their order. A kept command shows where its twin is; one used after
    /// the newest command was typed leads; one with no twin and used before goes after the items.
    /// </summary>
    [Fact]
    public void ItemsWithoutTimes_KeepTheirOrder()
    {
        var atTwin = Entry(1, "b", Now.AddDays(-1));
        var usedAfterNewest = Entry(2, "used from the panel", Now.AddMinutes(-1));
        var noTwin = Entry(3, "no longer in the file", Now.AddHours(-1));
        IReadOnlyList<AppItem> commands = [Item("a", null), Item("b", null), Item("c", null)];

        var rows = Order([atTwin, usedAfterNewest, noTwin]).Next(commands, last: true, newestItemUtc: Now.AddMinutes(-10));
        Assert.Equal(["#2", "a", "#1", "c", "#3"], Describe(rows));
        Assert.Equal(Now.AddDays(-1), rows[2].Time);
        Assert.Null(rows[1].Time);

        // Without the newest command's time nothing can be weighed against the commands: the entries go after them.
        Assert.Equal(["a", "#1", "c", "#2", "#3"], Describe(Order([atTwin, usedAfterNewest, noTwin]).Next(commands, last: true)));
    }

    /// <summary>An item listed twice shows once, at its first place.</summary>
    [Fact]
    public void ARepeatedItem_ShowsOnce()
    {
        var rows = Order([]).Next([Item("x", Now), Item("y", Now.AddMinutes(-1)), Item("x", Now.AddMinutes(-2))], last: true);
        Assert.Equal(["x", "y"], Describe(rows));
    }

    /// <summary>
    /// An entry deleted while it waits for its page is left out of that page; one already placed, or unknown, is not the
    /// order's to forget; a pin not placed yet can be forgotten too.
    /// </summary>
    [Fact]
    public void Forget_LeavesAWaitingEntryOut()
    {
        var order = Order([Entry(1, "old", Now.AddDays(-1)), Entry(2, "new", Now)]);
        Assert.Equal(["#2", "a"], Describe(order.Next([Item("a", Now.AddMinutes(-1))], last: false)));
        Assert.True(order.Forget(1));
        Assert.False(order.Forget(2));
        Assert.False(order.Forget(99));
        Assert.Equal(["b"], Describe(order.Next([Item("b", Now.AddDays(-2))], last: true)));

        var pins = Order([Entry(3, "pin", Now, pinned: true)], pinnedFirst: true);
        Assert.True(pins.Forget(3));
        Assert.Equal(0, pins.WaitingCount);
        Assert.Empty(pins.Next([], last: true));
    }

    /// <summary>Missing arguments and null elements are refused before anything is ordered.</summary>
    [Fact]
    public void Arguments_AreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => new MergedTabOrder<AppItem>(null!, false, i => i.Hash, i => i.At));
        Assert.Throws<ArgumentNullException>(() => new MergedTabOrder<AppItem>([], false, null!, i => i.At));
        Assert.Throws<ArgumentNullException>(() => new MergedTabOrder<AppItem>([], false, i => i.Hash, null!));
        Assert.Throws<ArgumentNullException>(() => new MergedTabOrder<AppItem>([null!], false, i => i.Hash, i => i.At));
        Assert.Throws<ArgumentNullException>(() => Order([]).Next(null!, last: true));
        Assert.Throws<ArgumentException>(() => Order([]).Next([null!], last: true));
    }

    /// <summary>
    /// Random lists keep the rules: every entry exactly once, no content twice, every other item shown, pins first when
    /// asked, rows newest first when every item has a time, an entry's row never older than its last use — and the same
    /// rows whether the items come in one page or in many.
    /// </summary>
    /// <param name="seed">The random seed (each one a different case).</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void RandomLists_KeepTheRules(int seed)
    {
        var random = new Random(seed);
        for (int round = 0; round < 200; round++)
        {
            bool pinnedFirst = random.Next(2) == 0;
            bool timed = random.Next(4) != 0;
            var texts = Enumerable.Range(0, random.Next(0, 40)).Select(i => $"text {i}").ToList();
            var entries = texts.Where(_ => random.Next(3) == 0)
                .Select((t, i) => Entry(i + 1, t, Now.AddMinutes(-random.Next(0, 500)), pinned: random.Next(5) == 0))
                .ToList();
            var items = texts.Where(_ => random.Next(4) != 0)
                .Select(t => Item(t, timed ? Now.AddMinutes(-random.Next(0, 500)) : null))
                .OrderByDescending(i => i.At)
                .ToList();

            var whole = Order(entries, pinnedFirst).Next(items, last: true);
            var paged = InPages(Order(entries, pinnedFirst), items, random);
            Assert.Equal(Describe(whole), Describe(paged));

            Assert.Equal(entries.Select(e => e.Id).Order(), whole.Where(r => r.Entry is not null).Select(r => r.Entry!.Id).Order());
            var hashes = whole.Select(r => r.Entry?.ContentHash ?? r.Item!.Hash).ToList();
            Assert.Equal(hashes.Count, hashes.Distinct().Count());
            var storedHashes = entries.Select(e => e.ContentHash).ToHashSet();
            Assert.Equal(items.Where(i => !storedHashes.Contains(i.Hash)).Select(i => i.Hash), whole.Where(r => r.Item is not null).Select(r => r.Item!.Hash));
            Assert.All(whole.Where(r => r.Entry is not null), r => Assert.True(r.Time >= r.Entry!.LastUsedUtc));

            int pins = pinnedFirst ? entries.Count(e => e.IsPinned) : 0;
            Assert.All(whole.Take(pins), r => Assert.True(r.Entry?.IsPinned));
            if (timed)
            {
                var times = whole.Skip(pins).Select(r => r.Time!.Value).ToList();
                Assert.Equal(times.OrderByDescending(t => t), times);
            }
        }
    }

    /// <summary>Hands items to an order in random page sizes, the last call marked last, and joins the rows.</summary>
    /// <param name="order">The order.</param>
    /// <param name="items">The items, newest first.</param>
    /// <param name="random">The random source of the page sizes.</param>
    /// <returns>Every page's rows, in order.</returns>
    private static List<MergedTabRow<AppItem>> InPages(MergedTabOrder<AppItem> order, IReadOnlyList<AppItem> items, Random random)
    {
        var rows = new List<MergedTabRow<AppItem>>();
        int at = 0;
        do
        {
            int size = random.Next(0, 8);
            var page = items.Skip(at).Take(size).ToList();
            at += page.Count;
            rows.AddRange(order.Next(page, last: at >= items.Count && random.Next(3) != 0));
        }
        while (at < items.Count);

        // A last empty page, as the panel asks for one more page when the previous one was full.
        rows.AddRange(order.Next([], last: true));
        return rows;
    }

    /// <summary>An order over test items, keyed by their hash and time.</summary>
    /// <param name="stored">The stored entries.</param>
    /// <param name="pinnedFirst">Pins first.</param>
    /// <returns>The order.</returns>
    private static MergedTabOrder<AppItem> Order(IEnumerable<ClipEntry> stored, bool pinnedFirst = false) =>
        new(stored, pinnedFirst, i => i.Hash, i => i.At);

    /// <summary>The rows as "#id" for entries and the text for items.</summary>
    /// <param name="rows">The rows.</param>
    /// <returns>The descriptions, in order.</returns>
    private static List<string> Describe(IEnumerable<MergedTabRow<AppItem>> rows) =>
        rows.Select(r => r.Entry is { } entry ? $"#{entry.Id}" : r.Item!.Text).ToList();

    /// <summary>A stored entry whose content is <paramref name="text"/>.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="text">Its content (the hash a twin item shares).</param>
    /// <param name="lastUsed">Its last use.</param>
    /// <param name="pinned">Whether pinned.</param>
    /// <returns>The entry.</returns>
    private static ClipEntry Entry(long id, string text, DateTimeOffset lastUsed, bool pinned = false) => new()
    {
        Id = id,
        Kind = ClipKind.Text,
        Preview = text,
        ContentHash = HashOf(text),
        CreatedUtc = lastUsed,
        LastUsedUtc = lastUsed,
        UseCount = 1,
        IsPinned = pinned,
        Origin = ClipOrigin.ClaudeCode,
    };

    /// <summary>An item of the other app.</summary>
    /// <param name="text">Its content.</param>
    /// <param name="at">Its own time, or <see langword="null"/> when its app keeps none.</param>
    /// <returns>The item.</returns>
    private static AppItem Item(string text, DateTimeOffset? at) => new(text, at);

    /// <summary>The test hash of a content (any stable string works: the order only compares them).</summary>
    /// <param name="text">The content.</param>
    /// <returns>The hash.</returns>
    private static string HashOf(string text) => "hash:" + text;

    /// <summary>An item of a made-up app: its content and its own time.</summary>
    /// <param name="Text">The content.</param>
    /// <param name="At">Its time, or <see langword="null"/>.</param>
    public sealed record AppItem(string Text, DateTimeOffset? At)
    {
        /// <summary>The content hash a stored copy of it would have.</summary>
        public string Hash => HashOf(Text);
    }
}
