using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Windows' own pins come over once (<see cref="WindowsPinLedger"/>): the ledger's rule and format, then the history
/// service through the reported bug (an unpinned item pinned again at every start) and its relatives (a deleted one back
/// after its tombstone expired; the first import under the rule; a new Windows pin; Win+R imports; forgotten content).
/// </summary>
public sealed class WindowsPinLedgerTests : IAsyncLifetime
{
    /// <summary>When the Windows pin was made, long before every import here (the reported pin dated from March 2026).</summary>
    private static readonly DateTimeOffset PinnedInWindows = TestData.Now.AddMonths(-8);

    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private ClipStore store = null!;
    private ClipHistoryService service = null!;

    /// <summary>Creates and starts a history service over a temp store, without retention.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask InitializeAsync()
    {
        store = new ClipStore(temp.DatabasePath);
        store.Initialize();
        service = new ClipHistoryService(store, imageAnalyzer: null, () => new CaptureRules { Retention = RetentionPolicy.Unlimited });
        service.Start();
        return ValueTask.CompletedTask;
    }

    /// <summary>Stops the service and deletes the temp store.</summary>
    /// <returns>A task completing after shutdown.</returns>
    public async ValueTask DisposeAsync()
    {
        await service.DisposeAsync();
        temp.Dispose();
    }

    /// <summary>Nothing stored yet is the first import under the rule, which lists nothing.</summary>
    [Fact]
    public void Parse_NothingStored_IsTheFirstImport()
    {
        var ledger = WindowsPinLedger.Parse(null);
        Assert.True(ledger.IsFirstImport);
        Assert.False(ledger.WasBroughtOver(Hash("a")));
        Assert.Equal("v1:", ledger.Format());
        Assert.True(ledger.Changed);
    }

    /// <summary>
    /// A value this version cannot read (a newer format, a hand edit) reads as the first import: the conservative reading,
    /// which never pins an entry that exists.
    /// </summary>
    /// <param name="stored">The stored value.</param>
    [Theory]
    [InlineData("v2:0123")]
    [InlineData("")]
    [InlineData("garbage")]
    public void Parse_AValueOfAnotherFormat_ReadsAsTheFirstImport(string stored)
    {
        var ledger = WindowsPinLedger.Parse(stored);
        Assert.True(ledger.IsFirstImport);
        Assert.Equal(WindowsPinDecision.LeaveAsIs, ledger.Decide(Hash("a"), entryPinned: false));
    }

    /// <summary>Tokens that are not content hashes are skipped and a repeat counts once; the rest keeps its order.</summary>
    [Fact]
    public void Parse_SkipsWhatIsNotAContentHash_AndRepeats()
    {
        var ledger = WindowsPinLedger.Parse($"v1:{Hash("a")}, not-a-hash ,{Hash("a").ToUpperInvariant()},{Hash("b")},{Hash("a")},");
        Assert.False(ledger.IsFirstImport);
        Assert.True(ledger.WasBroughtOver(Hash("a")));
        Assert.True(ledger.WasBroughtOver(Hash("b")));
        Assert.Equal($"v1:{Hash("a")},{Hash("b")}", ledger.Format());
    }

    /// <summary>
    /// The rule, case by case: a pin brought over before is left as the user keeps it; on the first import under the rule
    /// an existing entry is too; everything else is applied. Unpinned or missing entries left alone are counted for the log.
    /// </summary>
    /// <param name="ledgerState">"brought" (the ledger lists the content), "first" (nothing stored) or "other" (a ledger without it).</param>
    /// <param name="entryPinned">The entry's pin, or <see langword="null"/> when no entry holds the content.</param>
    /// <param name="expected">The decision.</param>
    /// <param name="kept">The expected <see cref="WindowsPinLedger.KeptCount"/>.</param>
    [Theory]
    [InlineData("brought", true, WindowsPinDecision.LeaveAsIs, 0)]
    [InlineData("brought", false, WindowsPinDecision.LeaveAsIs, 1)]
    [InlineData("brought", null, WindowsPinDecision.LeaveAsIs, 1)]
    [InlineData("first", true, WindowsPinDecision.LeaveAsIs, 0)]
    [InlineData("first", false, WindowsPinDecision.LeaveAsIs, 1)]
    [InlineData("first", null, WindowsPinDecision.Apply, 0)]
    [InlineData("other", true, WindowsPinDecision.Apply, 0)]
    [InlineData("other", false, WindowsPinDecision.Apply, 0)]
    [InlineData("other", null, WindowsPinDecision.Apply, 0)]
    public void Decide_FollowsTheRule(string ledgerState, bool? entryPinned, WindowsPinDecision expected, int kept)
    {
        var ledger = WindowsPinLedger.Parse(ledgerState switch
        {
            "brought" => $"v1:{Hash("other")},{Hash("x")}",
            "other" => $"v1:{Hash("other")}",
            _ => null,
        });

        Assert.Equal(expected, ledger.Decide(Hash("x"), entryPinned));
        Assert.Equal(kept, ledger.KeptCount);

        // The same pin offered twice in one import (WinRT's item and the pinned store's) decides and counts the same way.
        Assert.Equal(expected, ledger.Decide(Hash("x"), entryPinned));
        Assert.Equal(kept, ledger.KeptCount);
        Assert.Equal(1, ledger.SeenCount);
    }

    /// <summary>A malformed hash is refused before it is recorded: the next parse would skip it, and the pin would revive.</summary>
    /// <param name="hash">Not a content hash.</param>
    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde,")]
    public void Decide_RefusesAMalformedHash(string hash)
    {
        var ledger = WindowsPinLedger.Parse("v1:");
        Assert.Throws<ArgumentException>(() => ledger.Decide(hash, entryPinned: null));
        Assert.Equal(0, ledger.SeenCount);
        Assert.False(ledger.Changed);
    }

    /// <summary>An import moves the pins it saw to the end, in the order of first sight; the others keep their places.</summary>
    [Fact]
    public void Format_MovesWhatThisImportSawToTheEnd()
    {
        var ledger = WindowsPinLedger.Parse($"v1:{Hash("a")},{Hash("b")},{Hash("c")}");
        ledger.Decide(Hash("new"), entryPinned: null);
        ledger.Decide(Hash("a"), entryPinned: true);
        Assert.Equal($"v1:{Hash("b")},{Hash("c")},{Hash("new")},{Hash("a")}", ledger.Format());
        Assert.True(ledger.Changed);
    }

    /// <summary>
    /// The cap drops the hashes seen least recently; a pin Windows still keeps was just seen, so it is never the one
    /// dropped, however old it is.
    /// </summary>
    [Fact]
    public void Format_TrimsTheLeastRecentlySeen_NeverAPinWindowsStillKeeps()
    {
        var stored = Enumerable.Range(0, WindowsPinLedger.MaxEntries).Select(i => Hash($"stale {i}")).ToList();
        var ledger = WindowsPinLedger.Parse("v1:" + string.Join(',', stored));

        // The oldest entry is a pin Windows still keeps; two more are new.
        ledger.Decide(stored[0], entryPinned: false);
        ledger.Decide(Hash("new 1"), entryPinned: null);
        ledger.Decide(Hash("new 2"), entryPinned: null);

        var after = WindowsPinLedger.Parse(ledger.Format());
        Assert.True(after.WasBroughtOver(stored[0]));
        Assert.True(after.WasBroughtOver(Hash("new 1")));
        Assert.True(after.WasBroughtOver(Hash("new 2")));
        Assert.False(after.WasBroughtOver(stored[1]));
        Assert.False(after.WasBroughtOver(stored[2]));
        Assert.True(after.WasBroughtOver(stored[3]));
        Assert.Equal(WindowsPinLedger.MaxEntries, after.Format().Split(',').Length);
    }

    /// <summary>An import that saw the same pins in the same order changes nothing, so the store is not written.</summary>
    [Fact]
    public void Changed_OnlyWhenTheValueDiffers()
    {
        var ledger = WindowsPinLedger.Parse($"v1:{Hash("a")},{Hash("b")}");
        Assert.False(ledger.Changed);
        ledger.Decide(Hash("b"), entryPinned: true);
        Assert.False(ledger.Changed);
        ledger.Decide(Hash("a"), entryPinned: true);
        Assert.True(ledger.Changed);
    }

    /// <summary>
    /// The reported bug: an item Windows pins, unpinned here, came back pinned at every start (each update restarts the
    /// app). Now it stays unpinned through any number of imports, and the user's own later pin stays too.
    /// </summary>
    /// <returns>A task completing after the check.</returns>
    [Fact]
    public async Task Unpinned_StaysUnpinned_ThroughEveryLaterImport()
    {
        const string text = @"C:\BC-TEST\.local\bin\tool.exe";
        await service.ImportAsync(EveryStartOffers(text));
        var entry = Assert.Single(await service.QueryAsync(new ClipQuery(), TestContext.Current.CancellationToken));
        Assert.True(entry.IsPinned);

        await service.SetPinnedAsync(entry.Id, false);
        for (int start = 0; start < 3; start++)
        {
            await service.ImportAsync(EveryStartOffers(text));
            Assert.False(store.FindPinnedState(ContentHasher.ForText(text)));
        }

        // Pinned again here, it stays pinned: the import never unpins either.
        await service.SetPinnedAsync(entry.Id, true);
        await service.ImportAsync(EveryStartOffers(text));
        Assert.True(store.FindPinnedState(ContentHasher.ForText(text)));
        Assert.Single(await service.QueryAsync(new ClipQuery(), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// An item Windows pins, deleted here, came back pinned once its 30-day tombstone expired (Windows keeps pins for good).
    /// Now it stays out, also when Windows' history offers it without the pin mark.
    /// </summary>
    /// <returns>A task completing after the check.</returns>
    [Fact]
    public async Task Deleted_StaysOut_AfterItsTombstoneExpires()
    {
        const string text = "BC-TEST pinned in Windows, deleted here";
        await service.ImportAsync(EveryStartOffers(text));
        var entry = Assert.Single(await service.QueryAsync(new ClipQuery(), TestContext.Current.CancellationToken));
        await service.DeleteAsync(entry.Id);

        // A month later, Prune purges the tombstone; Windows still offers its pin, as an item it marks and one it does not.
        store.Prune(RetentionPolicy.Unlimited, DateTimeOffset.UtcNow.AddDays(31));
        await service.ImportAsync(
        [
            TestData.Text(text, PinnedInWindows, ClipOrigin.WindowsHistory),
            TestData.Text(text, PinnedInWindows, ClipOrigin.WindowsPinned, pin: true),
        ]);

        Assert.Null(store.FindPinnedState(ContentHasher.ForText(text)));
        Assert.Empty(await service.QueryAsync(new ClipQuery(), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The first import under the rule (an update from a version without it) leaves existing entries as they are: every
    /// older version pinned them at its last import, so an unpinned one was unpinned by the user. Content not here yet is
    /// brought over pinned, and every pin is recorded.
    /// </summary>
    /// <returns>A task completing after the check.</returns>
    [Fact]
    public async Task FirstImportUnderTheRule_LeavesExistingEntriesAsTheyAre()
    {
        Assert.NotNull(await service.AddAsync(TestData.Text("BC-TEST unpinned here")));
        var pinned = (await service.AddAsync(TestData.Text("BC-TEST pinned here")))!;
        await service.SetPinnedAsync(pinned.Id, true);
        Assert.Null(store.GetStateValue(WindowsPinLedger.StateName));

        int created = await service.ImportAsync(
        [
            WindowsPin("BC-TEST unpinned here"),
            WindowsPin("BC-TEST pinned here"),
            WindowsPin("BC-TEST new in Windows"),
        ]);

        Assert.Equal(1, created);
        Assert.False(store.FindPinnedState(ContentHasher.ForText("BC-TEST unpinned here")));
        Assert.True(store.FindPinnedState(ContentHasher.ForText("BC-TEST pinned here")));
        Assert.True(store.FindPinnedState(ContentHasher.ForText("BC-TEST new in Windows")));

        var ledger = WindowsPinLedger.Parse(store.GetStateValue(WindowsPinLedger.StateName));
        Assert.False(ledger.IsFirstImport);
        Assert.True(ledger.WasBroughtOver(ContentHasher.ForText("BC-TEST unpinned here")));
        Assert.True(ledger.WasBroughtOver(ContentHasher.ForText("BC-TEST pinned here")));
        Assert.True(ledger.WasBroughtOver(ContentHasher.ForText("BC-TEST new in Windows")));
    }

    /// <summary>
    /// A pin Windows made since the last import is applied, also to content already here; a first import that found no pin
    /// at all still ends the first import, so such a pin is not mistaken for one an older version had applied.
    /// </summary>
    /// <returns>A task completing after the check.</returns>
    [Fact]
    public async Task ANewWindowsPin_OfContentAlreadyHere_IsApplied()
    {
        await service.ImportAsync([TestData.Text("BC-TEST in Windows' history", PinnedInWindows, ClipOrigin.WindowsHistory)]);
        Assert.Equal("v1:", store.GetStateValue(WindowsPinLedger.StateName));

        Assert.NotNull(await service.AddAsync(TestData.Text("BC-TEST copied, then pinned in Windows")));
        await service.ImportAsync([WindowsPin("BC-TEST copied, then pinned in Windows")]);
        Assert.True(store.FindPinnedState(ContentHasher.ForText("BC-TEST copied, then pinned in Windows")));
    }

    /// <summary>An import of Windows' Win+R list carries no Windows pin and neither reads nor writes the ledger.</summary>
    /// <returns>A task completing after the check.</returns>
    [Fact]
    public async Task WinRListImports_LeaveTheLedgerAlone()
    {
        Assert.Equal(1, await service.ImportAsync([TestData.Text("BC-TEST notepad", PinnedInWindows, ClipOrigin.RunDialogHistory)]));
        Assert.Null(store.GetStateValue(WindowsPinLedger.StateName));
    }

    /// <summary>
    /// Forgotten content is never recorded as brought over: its Windows pin is kept out while it is forgotten, and after
    /// "Allow again" it comes over once more, pinned.
    /// </summary>
    /// <returns>A task completing after the check.</returns>
    [Fact]
    public async Task ForgottenContent_ComesOverOnceAllowedAgain()
    {
        const string text = "BC-TEST forgotten, pinned in Windows";
        var entry = (await service.AddAsync(TestData.Text(text)))!;
        Assert.NotNull(await service.ForgetAsync(entry.Id));

        await service.ImportAsync([WindowsPin(text)]);
        Assert.Null(store.FindPinnedState(ContentHasher.ForText(text)));
        Assert.False(WindowsPinLedger.Parse(store.GetStateValue(WindowsPinLedger.StateName)).WasBroughtOver(ContentHasher.ForText(text)));

        Assert.True(await service.AllowAgainAsync(Assert.Single(await service.GetForgottenAsync()).Id));
        await service.ImportAsync([WindowsPin(text)]);
        Assert.True(store.FindPinnedState(ContentHasher.ForText(text)));
    }

    /// <summary>A content hash for a test label (the shape <see cref="WindowsPinLedger"/> accepts).</summary>
    /// <param name="label">Any text.</param>
    /// <returns>Its text hash.</returns>
    private static string Hash(string label) => ContentHasher.ForText(label);

    /// <summary>A capture of Windows' pinned store, pinned there long ago.</summary>
    /// <param name="text">Its text.</param>
    /// <returns>The capture.</returns>
    private static ClipCapture WindowsPin(string text) => TestData.Text(text, PinnedInWindows, ClipOrigin.WindowsPinned, pin: true);

    /// <summary>
    /// What every start's import offers for one item Windows pins: the WinRT history item (marked pinned by its timestamp)
    /// and the pinned store's item.
    /// </summary>
    /// <param name="text">Its text.</param>
    /// <returns>The two captures, in the importer's order.</returns>
    private static ClipCapture[] EveryStartOffers(string text) =>
    [
        TestData.Text(text, PinnedInWindows, ClipOrigin.WindowsHistory, pin: true),
        WindowsPin(text),
    ];
}
