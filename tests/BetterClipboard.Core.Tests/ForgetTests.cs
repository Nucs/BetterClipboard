using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the "Forget forever" fingerprint: what counts as the same content (line endings and surrounding
/// whitespace do not, case and inner text do), the whitespace-only fallback, chunked hashing of huge texts, and
/// known answers that pin the scheme — a changed scheme would silently let every forgotten item back in.
/// </summary>
public sealed class ForgetFingerprintTests
{
    /// <summary>Line endings and surrounding whitespace are ignored; case, inner spaces and inner blank lines are not.</summary>
    [Fact]
    public void Text_IgnoresLineEndingsAndSurroundingWhitespaceOnly()
    {
        var secret = ForgetFingerprint.ForText("secret");
        Assert.NotNull(secret);
        Assert.All(["secret\n", "secret\r\n", "  secret \t", "\r\n\u00A0secret\r\n\r\n", "\u3000secret\u2029"],
            variant => Assert.Equal(secret, ForgetFingerprint.ForText(variant)));
        Assert.All(["Secret", "sec ret", "secret.", "secrets"], other => Assert.NotEqual(secret, ForgetFingerprint.ForText(other)));

        var lines = ForgetFingerprint.ForText("line 1\nline 2");
        Assert.Equal(lines, ForgetFingerprint.ForText("line 1\r\nline 2"));
        Assert.Equal(lines, ForgetFingerprint.ForText("line 1\rline 2\r\n"));
        Assert.NotEqual(lines, ForgetFingerprint.ForText("line 1\n\nline 2"));
        Assert.NotEqual(lines, ForgetFingerprint.ForText("line 1\n  line 2"));
    }

    /// <summary>
    /// Whitespace-only text has no normalized form: it falls back to the exact content hash, so forgetting one
    /// blank copy does not keep out every blank copy. Non-text clips always use their content hash.
    /// </summary>
    [Fact]
    public void WhitespaceOnlyAndNonText_UseTheContentHash()
    {
        Assert.Null(ForgetFingerprint.ForText("   \r\n\t"));
        Assert.Null(ForgetFingerprint.ForText(string.Empty));
        Assert.Equal("exact-hash", ForgetFingerprint.Of("  ", "exact-hash"));
        Assert.Equal("pixel-hash", ForgetFingerprint.Of(null, "pixel-hash"));
        Assert.Equal(ForgetFingerprint.ForText("x"), ForgetFingerprint.Of(" x ", "ignored-for-text"));
        Assert.Throws<ArgumentException>(() => ForgetFingerprint.Of("x", " "));

        var files = ContentClassifier.Classify(TestData.Files(@"C:\BC-TEST\a.txt"))!;
        Assert.Equal(files.ContentHash, ForgetFingerprint.Of(files));
    }

    /// <summary>
    /// Known answers computed independently (Python <c>hashlib.sha256</c> over <c>"N\n" + UTF-8</c>): the
    /// domain letter, separator, normalization and encoding are frozen. If this fails, forgotten items would
    /// quietly be recorded again — change the scheme only with a migration.
    /// </summary>
    [Fact]
    public void KnownAnswers_PinTheScheme()
    {
        Assert.Equal("9c0a996f95608ca60dc6c1ed1e92a6be19197a5e80b805343bbdfcd502f71fc3", ForgetFingerprint.ForText("\r\nsecret \r\n"));
        Assert.Equal("17b304abc39b62c1874254cd522632a731bd2903092b1cf349f3ee0912d176ad", ForgetFingerprint.ForText(" \u05E9\u05DC\u05D5\u05DD \U0001F511\n"));
    }

    /// <summary>
    /// A text far beyond one hashing chunk, with a surrogate pair straddling each chunk boundary, hashes exactly
    /// like its whole UTF-8 at once — chunking is invisible.
    /// </summary>
    [Fact]
    public void LongText_ChunkingIsInvisible()
    {
        var builder = new StringBuilder();
        for (int i = 0; i < 5; i++)
        {
            builder.Append('a', 16 * 1024 - 1).Append("\U0001F511");
        }

        var text = "\r\n" + builder + " tail\r\n";
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("N\n" + ForgetFingerprint.NormalizeText(text))));
        Assert.Equal(expected, ForgetFingerprint.ForText(text));
        Assert.StartsWith("aaaa", ForgetFingerprint.NormalizeText(text), StringComparison.Ordinal);
        Assert.EndsWith(" tail", ForgetFingerprint.NormalizeText(text), StringComparison.Ordinal);
    }

    /// <summary>The trim set handed to SQL is exactly what <see cref="string.Trim()"/> removes, character by character.</summary>
    [Fact]
    public void WhitespaceCharacters_MatchDotNetTrim()
    {
        for (int i = 0; i <= char.MaxValue; i++)
        {
            char c = (char)i;
            Assert.Equal(char.IsWhiteSpace(c), ForgetFingerprint.WhitespaceCharacters.Contains(c));
        }

        Assert.Equal(string.Empty, ForgetFingerprint.WhitespaceCharacters.Trim());
    }
}

/// <summary>
/// Tests for the "Forget forever" list in the store: forgetting deletes the entry and its stored look-alikes
/// (and nothing else), records content-free facts, survives every clear and prune, counts kept-out copies,
/// can be allowed again, and is added to a database created before it existed.
/// </summary>
public sealed class ForgetStoreTests : IDisposable
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private readonly ClipStore store;

    /// <summary>Creates a fresh store per test.</summary>
    public ForgetStoreTests()
    {
        store = new ClipStore(temp.DatabasePath);
        store.Initialize();
    }

    /// <summary>Deletes the temp database.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>Forgetting deletes the entry, lists it with its facts only, and makes its fingerprint forgotten.</summary>
    [Fact]
    public void Forget_DeletesAndLists()
    {
        var entry = Add(TestData.Text("BC-TEST secret"));
        var result = store.Forget(entry.Id, TestData.Now);

        Assert.NotNull(result);
        Assert.Equal([entry.Id], result!.RemovedIds);
        Assert.Null(store.GetEntry(entry.Id));
        Assert.Equal(new ForgottenItem(result.Item.Id, ClipKind.Text, 14, null, null, null, "Notepad", TestData.Now, 0, null), result.Item);
        Assert.Equal([result.Item], store.GetForgotten());
        Assert.True(store.IsForgotten(ForgetFingerprint.ForText("BC-TEST secret\r\n")!));
        Assert.False(store.IsForgotten(ForgetFingerprint.ForText("BC-TEST other")!));
        Assert.Equal((0L, 1L), (store.GetStats().Count, store.GetStats().ForgottenCount));
        Assert.Equal(1, store.CountForgotten());
        Assert.Null(store.Forget(entry.Id, TestData.Now));
    }

    /// <summary>
    /// Stored look-alikes (other line endings, surrounding whitespace) go with the entry; different text —
    /// another case, an extra character, the text inside a longer one — stays.
    /// </summary>
    [Fact]
    public void Forget_RemovesLookAlikesAndNothingElse()
    {
        var entry = Add(TestData.Text("BC-TEST token", TestData.Now.AddMinutes(-5)));
        var crlf = Add(TestData.Text("BC-TEST token\r\n", TestData.Now.AddMinutes(-4)));
        var spaced = Add(TestData.Text(" \tBC-TEST token  ", TestData.Now.AddMinutes(-3)));
        var rich = Add(TestData.Rich("BC-TEST token\n", "<b>BC-TEST token</b>"));
        var upper = Add(TestData.Text("BC-TEST TOKEN", TestData.Now.AddMinutes(-2)));
        var longer = Add(TestData.Text("BC-TEST token 2", TestData.Now.AddMinutes(-1)));
        var inside = Add(TestData.Text("see BC-TEST token", TestData.Now));

        var result = store.Forget(entry.Id, TestData.Now)!;
        Assert.Equal(entry.Id, result.RemovedIds[0]);
        Assert.Equal([entry.Id, crlf.Id, spaced.Id, rich.Id], result.RemovedIds.Order());
        Assert.Equal([inside.Id, longer.Id, upper.Id], store.Query(new ClipQuery()).Select(e => e.Id));
        Assert.Single(store.GetForgotten());
    }

    /// <summary>Pins and groups protect from retention, not from an explicit "forget"; memberships cascade.</summary>
    [Fact]
    public void Forget_IgnoresPinsAndGroups()
    {
        var group = store.CreateGroup("Work", GroupIconCatalog.Default.Glyph, TestData.Now);
        var entry = Add(TestData.Text("BC-TEST pinned and grouped", pin: true));
        store.AddToGroup(entry.Id, group.Id, TestData.Now);

        Assert.NotNull(store.Forget(entry.Id, TestData.Now));
        Assert.Null(store.GetEntry(entry.Id));
        Assert.Equal(0, store.GetGroups().Single().ItemCount);
    }

    /// <summary>File lists and images are forgotten by their own identity, with their count or size recorded.</summary>
    [Fact]
    public void Forget_FilesAndImages()
    {
        var files = Add(TestData.Files(@"C:\BC-TEST\a.txt", @"C:\BC-TEST\b.txt"));
        var filesItem = store.Forget(files.Id, TestData.Now)!.Item;
        Assert.Equal((ClipKind.Files, (int?)2, (int?)null), (filesItem.Kind, filesItem.FileCount, filesItem.TextLength));
        Assert.True(store.IsForgotten(files.ContentHash));

        var capture = TestData.Image(7);
        var classified = ContentClassifier.Classify(capture)! with { ContentHash = "BC-TEST-pixels" };
        var image = store.Upsert(capture, classified, new ImageAnalysis(1920, 1080, null, "BC-TEST-pixels"), bumpIfExists: true)!.Entry;
        var imageItem = store.Forget(image.Id, TestData.Now)!.Item;
        Assert.Equal((ClipKind.Image, (int?)1920, (int?)1080), (imageItem.Kind, imageItem.ImageWidth, imageItem.ImageHeight));
        Assert.True(store.IsForgotten("BC-TEST-pixels"));
    }

    /// <summary>Forgetting content that is already forgotten keeps the first entry (time and counters).</summary>
    [Fact]
    public void Forget_Twice_KeepsTheFirstEntry()
    {
        var first = Add(TestData.Text("BC-TEST again"));
        var item = store.Forget(first.Id, TestData.Now)!.Item;
        store.RecordForgottenBlock(ForgetFingerprint.ForText("BC-TEST again")!, TestData.Now.AddMinutes(1));

        // Stored directly, as an older build (which does not know the list) would have recorded it.
        var again = Add(TestData.Text("BC-TEST again\n", TestData.Now.AddMinutes(2)));
        var second = store.Forget(again.Id, TestData.Now.AddMinutes(3))!;
        Assert.Equal(item.Id, second.Item.Id);
        Assert.Equal((TestData.Now, 1L), (second.Item.ForgottenUtc, second.Item.BlockedCount));
        Assert.Single(store.GetForgotten());
    }

    /// <summary>No clear or prune touches the list; "Allow again" removes one entry, "Allow all again" the rest.</summary>
    [Fact]
    public void ClearsKeepTheList_AllowAgainRemoves()
    {
        var a = store.Forget(Add(TestData.Text("BC-TEST a")).Id, TestData.Now)!.Item;
        var b = store.Forget(Add(TestData.Text("BC-TEST b")).Id, TestData.Now.AddMinutes(1))!.Item;
        Add(TestData.Text("BC-TEST kept in history"));
        store.ClearUnpinned(TestData.Now.AddMinutes(2));
        store.ClearAll(TestData.Now.AddMinutes(3));
        store.Prune(new RetentionPolicy(1, TimeSpan.FromDays(1), 1), TestData.Now.AddDays(400));
        Assert.Equal([b.Id, a.Id], store.GetForgotten().Select(i => i.Id));

        Assert.True(store.RemoveForgotten(a.Id));
        Assert.False(store.RemoveForgotten(a.Id));
        Assert.False(store.IsForgotten(ForgetFingerprint.ForText("BC-TEST a")!));
        Assert.Equal(1, store.ClearForgotten());
        Assert.Empty(store.GetForgotten());
        Assert.Equal(0, store.ClearForgotten());
    }

    /// <summary>Each kept-out copy counts and stamps the entry; the stamp never goes back in time.</summary>
    [Fact]
    public void RecordForgottenBlock_CountsAndStamps()
    {
        store.Forget(Add(TestData.Text("BC-TEST blocked")).Id, TestData.Now);
        var fingerprint = ForgetFingerprint.ForText("BC-TEST blocked")!;
        Assert.True(store.RecordForgottenBlock(fingerprint, TestData.Now.AddMinutes(5)));
        Assert.True(store.RecordForgottenBlock(fingerprint, TestData.Now.AddMinutes(1)));
        Assert.False(store.RecordForgottenBlock(ForgetFingerprint.ForText("BC-TEST never forgotten")!, TestData.Now));
        var item = store.GetForgotten().Single();
        Assert.Equal((2L, (DateTimeOffset?)TestData.Now.AddMinutes(5)), (item.BlockedCount, item.LastBlockedUtc));
    }

    /// <summary>A database from before the list gets it on the next Initialize, keeping its entries; Initialize stays idempotent.</summary>
    [Fact]
    public void Initialize_AddsTheListToAnOlderDatabase()
    {
        var entry = Add(TestData.Text("BC-TEST from before forgetting"));
        ExecuteRaw("DROP TABLE forgotten;");

        var reopened = new ClipStore(temp.DatabasePath);
        reopened.Initialize();
        reopened.Initialize();
        Assert.Equal(0, reopened.GetStats().ForgottenCount);
        Assert.NotNull(reopened.Forget(entry.Id, TestData.Now));
        Assert.Equal(1, reopened.CountForgotten());
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
/// Tests for "Forget forever" in the capture pipeline: after forgetting, the content is kept out of every
/// channel (live copies, ShareX, imports), only new copies are counted, the order of the single worker is
/// respected, images match by pixels, and "Allow again" lets the content back in.
/// </summary>
public sealed class ForgetServiceTests : IAsyncLifetime
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private readonly List<ClipChangedEventArgs> changes = [];
    private readonly PixelAnalyzer analyzer = new();
    private int forgottenEvents;
    private ClipHistoryService service = null!;

    /// <summary>Starts a service over a temp store and records its events.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask InitializeAsync()
    {
        var store = new ClipStore(temp.DatabasePath);
        store.Initialize();
        service = new ClipHistoryService(store, analyzer, () => new CaptureRules { Retention = RetentionPolicy.Unlimited });
        service.Changed += (_, e) =>
        {
            lock (changes)
            {
                changes.Add(e);
            }
        };
        service.ForgottenChanged += (_, _) => Interlocked.Increment(ref forgottenEvents);
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

    /// <summary>
    /// Forgotten text is kept out of live copies (any whitespace variant, any app) and of imports; only the new
    /// copies are counted, and nothing is announced as added.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task ForgottenText_IsKeptOutOfEveryChannel()
    {
        var entry = (await service.AddAsync(TestData.Text("BC-TEST password")))!;
        var result = (await service.ForgetAsync(entry.Id))!;
        Assert.Equal([entry.Id], result.RemovedIds);
        Assert.Equal((ClipChangeKind.Removed, (long?)entry.Id), (Snapshot().Last().Kind, Snapshot().Last().EntryId));
        Assert.Equal(1, forgottenEvents);

        changes.Clear();
        Assert.Null(await service.AddAsync(TestData.Text("BC-TEST password\r\n", TestData.Now.AddMinutes(1))));
        Assert.Null(await service.AddAsync(TestData.Text("  BC-TEST password", TestData.Now.AddMinutes(2), source: TestData.ShareX)));
        Assert.Equal(0, await service.ImportAsync([TestData.Text("BC-TEST password", TestData.Now.AddMinutes(3), ClipOrigin.WindowsHistory)]));
        Assert.Empty(Snapshot());
        Assert.Empty(await service.QueryAsync(new ClipQuery()));

        var item = (await service.GetForgottenAsync()).Single();
        Assert.Equal((2L, (DateTimeOffset?)TestData.Now.AddMinutes(2)), (item.BlockedCount, item.LastBlockedUtc));
        Assert.Equal(3, forgottenEvents);
        Assert.Equal(1, (await service.GetStatsAsync()).ForgottenCount);

        // Unrelated content still flows.
        Assert.NotNull(await service.AddAsync(TestData.Text("BC-TEST something else")));
    }

    /// <summary>
    /// The worker's order holds: a copy queued right after "forget" is processed after it, so it is kept out
    /// even though it was queued before the forget finished.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task ACopyQueuedRightAfterForget_IsKeptOut()
    {
        var entry = (await service.AddAsync(TestData.Text("BC-TEST racing")))!;
        var forgetting = service.ForgetAsync(entry.Id);
        var adding = service.AddAsync(TestData.Text("BC-TEST racing", TestData.Now.AddMinutes(1)));
        Assert.NotNull(await forgetting);
        Assert.Null(await adding);
    }

    /// <summary>An image is forgotten by its pixels: the same picture in another encoding is kept out.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task ForgottenImage_IsKeptOutByPixels()
    {
        analyzer.PixelHash = "BC-TEST same pixels";
        var shot = (await service.AddAsync(TestData.Image(1)))!;
        Assert.NotNull(await service.ForgetAsync(shot.Id));
        Assert.Null(await service.AddAsync(TestData.Image(2)));
        Assert.Null(await service.AddAsync(TestData.ShareXShot(3, TestData.Now.AddMinutes(1))));

        analyzer.PixelHash = "BC-TEST other pixels";
        Assert.NotNull(await service.AddAsync(TestData.Image(4)));
    }

    /// <summary>"Allow again" and "Allow all again" let content back in from its next copy (nothing deleted returns).</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task AllowAgain_RecordsAgain()
    {
        var a = (await service.AddAsync(TestData.Text("BC-TEST allow a")))!;
        var b = (await service.AddAsync(TestData.Text("BC-TEST allow b")))!;
        var forgottenA = (await service.ForgetAsync(a.Id))!.Item;
        await service.ForgetAsync(b.Id);
        Assert.Empty(await service.QueryAsync(new ClipQuery()));

        Assert.True(await service.AllowAgainAsync(forgottenA.Id));
        Assert.False(await service.AllowAgainAsync(forgottenA.Id));
        Assert.NotNull(await service.AddAsync(TestData.Text("BC-TEST allow a", TestData.Now.AddMinutes(1))));
        Assert.Null(await service.AddAsync(TestData.Text("BC-TEST allow b", TestData.Now.AddMinutes(1))));

        Assert.Equal(1, await service.AllowAllAgainAsync());
        Assert.NotNull(await service.AddAsync(TestData.Text("BC-TEST allow b", TestData.Now.AddMinutes(2))));
        Assert.Equal(0, await service.AllowAllAgainAsync());
        Assert.Null(await service.ForgetAsync(424242));
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

    /// <summary>Analyzer that reports a configurable pixel hash (the same picture in any encoding).</summary>
    private sealed class PixelAnalyzer : IImageAnalyzer
    {
        /// <summary>The pixel hash to report.</summary>
        public string? PixelHash { get; set; }

        /// <inheritdoc />
        public Task<ImageAnalysis?> AnalyzeAsync(ClipCapture capture, CancellationToken cancellationToken) =>
            Task.FromResult<ImageAnalysis?>(new ImageAnalysis(640, 480, null, PixelHash));
    }
}

/// <summary>Tests for the Settings wording of forgotten entries.</summary>
public sealed class ForgottenTextTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>Titles name the kind and size of every kind of entry.</summary>
    [Fact]
    public void Title_PerKind()
    {
        Assert.Equal("Text · 1,234 characters", ForgottenText.Title(Item(ClipKind.Text, textLength: 1234), Culture));
        Assert.Equal("Link · 1 character", ForgottenText.Title(Item(ClipKind.Link, textLength: 1), Culture));
        Assert.Equal("Formatted text · 5 characters", ForgottenText.Title(Item(ClipKind.RichText, textLength: 5), Culture));
        Assert.Equal("Formatted content", ForgottenText.Title(Item(ClipKind.RichText), Culture));
        Assert.Equal("Color · 7 characters", ForgottenText.Title(Item(ClipKind.Color, textLength: 7), Culture));
        Assert.Equal("1 file", ForgottenText.Title(Item(ClipKind.Files, fileCount: 1), Culture));
        Assert.Equal("3 files", ForgottenText.Title(Item(ClipKind.Files, fileCount: 3), Culture));
        Assert.Equal("Image · 1920 × 1080", ForgottenText.Title(Item(ClipKind.Image, width: 1920, height: 1080), Culture));
        Assert.Equal("Image", ForgottenText.Title(Item(ClipKind.Image), Culture));
    }

    /// <summary>Details say where it came from, when it was forgotten, and whether copies were kept out since.</summary>
    [Fact]
    public void Details_SourceTimesAndCounts()
    {
        var now = TestData.Now;
        Assert.Equal("From Chrome · forgotten 5 min ago · not copied since",
            ForgottenText.Details(Item(ClipKind.Text, source: "Chrome", forgotten: now.AddMinutes(-5)), now, TimeZoneInfo.Utc, Culture));
        Assert.Equal("Forgotten 5 min ago · kept out 3 times, last just now",
            ForgottenText.Details(Item(ClipKind.Text, forgotten: now.AddMinutes(-5), blocked: 3, lastBlocked: now), now, TimeZoneInfo.Utc, Culture));
        Assert.Equal("Forgotten 2 h ago · kept out 1 time, last 1 min ago",
            ForgottenText.Details(Item(ClipKind.Text, forgotten: now.AddHours(-2), blocked: 1, lastBlocked: now.AddMinutes(-1)), now, TimeZoneInfo.Utc, Culture));
    }

    /// <summary>Builds an entry.</summary>
    /// <param name="kind">Kind.</param>
    /// <param name="textLength">Characters.</param>
    /// <param name="fileCount">Files.</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="source">Source app.</param>
    /// <param name="forgotten">When forgotten (defaults to <see cref="TestData.Now"/>).</param>
    /// <param name="blocked">Copies kept out.</param>
    /// <param name="lastBlocked">Last kept-out copy.</param>
    /// <returns>The entry.</returns>
    private static ForgottenItem Item(ClipKind kind, int? textLength = null, int? fileCount = null, int? width = null, int? height = null,
        string? source = null, DateTimeOffset? forgotten = null, long blocked = 0, DateTimeOffset? lastBlocked = null) =>
        new(1, kind, textLength, fileCount, width, height, source, forgotten ?? TestData.Now, blocked, lastBlocked);
}
