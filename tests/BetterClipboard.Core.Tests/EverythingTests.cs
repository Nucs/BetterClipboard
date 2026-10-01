using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Everything;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for Everything's window-message wire format: the query and command-line encodings Everything reads, and
/// the <c>LIST2</c> decoder, which must read the verified field order and refuse any reply that lies about its own
/// size — the reply comes from another process.
/// </summary>
public sealed class EverythingIpcTests
{
    /// <summary>A query is seven little-endian DWORDs, then the search as UTF-16 with a terminator.</summary>
    [Fact]
    public void EncodeQuery2_PacksSevenDwordsThenTheSearch()
    {
        const string search = "runcount: path:\"BC-TEST שלום\"";
        const uint fields = EverythingIpc.RequestFullPath | EverythingIpc.RequestRunCount | EverythingIpc.RequestDateRun;
        var bytes = EverythingIpc.EncodeQuery2(0x1234_5678, 42, search, fields, EverythingIpc.SortDateRunDescending, 200);

        Assert.Equal(28 + (search.Length + 1) * 2, bytes.Length);
        uint[] header = [.. Enumerable.Range(0, 7).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4)))];
        Assert.Equal([0x1234_5678u, 42u, 0u, 0u, 200u, fields, EverythingIpc.SortDateRunDescending], header);
        Assert.Equal(search, Encoding.Unicode.GetString(bytes, 28, search.Length * 2));
        Assert.Equal(new byte[] { 0, 0 }, bytes[^2..]);
        Assert.Throws<ArgumentNullException>(() => EverythingIpc.EncodeQuery2(1, 1, null!, fields, 1, 1));
    }

    /// <summary>
    /// Only the window handle's low 32 bits are sent (Everything's struct has a DWORD there); window handles fit
    /// in 32 bits on x64 too, so nothing is lost.
    /// </summary>
    [Fact]
    public void EncodeQuery2_SendsTheLow32BitsOfTheReplyWindow()
    {
        Assert.SkipUnless(Environment.Is64BitProcess, "A handle above 32 bits needs a 64-bit process.");
        var bytes = EverythingIpc.EncodeQuery2(unchecked((nint)0x7_0000_0042L), 1, string.Empty, EverythingIpc.RequestFullPath, 1, 1);
        Assert.Equal(0x42u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(30, bytes.Length); // the header and an empty, terminated search
    }

    /// <summary>A command line is the show command, then NUL-terminated UTF-8 (non-ASCII paths survive).</summary>
    [Fact]
    public void EncodeCommandLine_IsTheShowCommandThenUtf8()
    {
        const string line = "-s \"\\\"C:\\BC-TEST\\שלום.txt\\\"\"";
        var bytes = EverythingIpc.EncodeCommandLine(1, line);
        Assert.Equal(4 + Encoding.UTF8.GetByteCount(line) + 1, bytes.Length);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes));
        Assert.Equal(line, Encoding.UTF8.GetString(bytes, 4, bytes.Length - 5));
        Assert.Equal(0, bytes[^1]);
        Assert.Throws<ArgumentNullException>(() => EverythingIpc.EncodeCommandLine(1, null!));
    }

    /// <summary>The unnamed instance has the plain class; a named one (the 1.5 alpha, a test instance) adds <c>_(name)</c>.</summary>
    [Fact]
    public void WindowClassFor_NamesTheInstance()
    {
        Assert.Equal("EVERYTHING_TASKBAR_NOTIFICATION", EverythingIpc.WindowClassFor(null));
        Assert.Equal("EVERYTHING_TASKBAR_NOTIFICATION", EverythingIpc.WindowClassFor(string.Empty));
        Assert.Equal("EVERYTHING_TASKBAR_NOTIFICATION_(1.5a)", EverythingIpc.WindowClassFor("1.5a"));
    }

    /// <summary>
    /// The Everything tab's fields come back in request-bit order: full path, size, date modified, run count,
    /// date run. A folder's −1 size (1.4) and unset dates read as "unknown", never as a number or year 1601.
    /// </summary>
    [Fact]
    public void DecodeList2_ReadsTheTabsFieldsInRequestBitOrder()
    {
        const uint fields = EverythingIpc.RequestFullPath | EverythingIpc.RequestSize | EverythingIpc.RequestDateModified
                            | EverythingIpc.RequestRunCount | EverythingIpc.RequestDateRun;
        var modified = new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);
        var opened = new DateTimeOffset(2026, 10, 1, 9, 30, 15, TimeSpan.Zero).AddTicks(1_234_567);
        var reply = List2Reply.Build(fields, EverythingIpc.SortDateRunDescending, total: 7,
            (false, List2Reply.Fields(@"C:\BC-TEST\שלום עולם.txt", 1234L, modified.ToFileTime(), 3u, opened.ToFileTime())),
            (true, List2Reply.Fields(@"C:\BC-TEST\folder", -1L, 0L, 1u, 0L)));

        var list = EverythingIpc.DecodeList2(reply);

        Assert.Equal((7u, 0u, fields, EverythingIpc.SortDateRunDescending), (list.Total, list.Offset, list.RequestFlags, list.Sort));
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(new EverythingItem(@"C:\BC-TEST\שלום עולם.txt", false, 1234, modified, null, 3, opened), list.Items[0]);
        Assert.Equal(new EverythingItem(@"C:\BC-TEST\folder", true, null, null, null, 1, null), list.Items[1]);
    }

    /// <summary>
    /// Every field Everything can send is skipped by its own size when the tab does not need it, so the fields it
    /// does need are still read at the right place; bits above the known ones come last and are ignored.
    /// </summary>
    [Fact]
    public void DecodeList2_SkipsEveryOtherFieldBySize()
    {
        const uint fields = 0xFFFF | 0x10000;
        var at = new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.Zero).ToFileTime();
        var reply = List2Reply.Build(fields, 1, total: 1, (false, List2Reply.Fields(
            "name.txt", @"C:\BC-TEST", @"C:\BC-TEST\name.txt", "txt", // name, path, full path, extension
            99L, at, at + 1, at + 2,                                    // size, created, modified, accessed
            0x20u, "list.efu", 5u, at + 3, at + 4,                      // attributes, file list, run count, date run, recently changed
            "*name*.txt", @"C:\*BC-TEST*", @"C:\*BC-TEST*\name.txt"))); // highlighted name, path, full path

        var item = Assert.Single(EverythingIpc.DecodeList2(reply).Items);

        Assert.Equal(@"C:\BC-TEST\name.txt", item.FullPath);
        Assert.Equal(99, item.Size);
        Assert.Equal(DateTime.FromFileTimeUtc(at + 1), item.Modified?.UtcDateTime);
        Assert.Equal(0x20u, item.Attributes);
        Assert.Equal(5u, item.RunCount);
        Assert.Equal(DateTime.FromFileTimeUtc(at + 3), item.DateRun?.UtcDateTime);
    }

    /// <summary>
    /// A reply whose counts, offsets or lengths point past its end is refused with <see cref="FormatException"/>
    /// (never read past, never a huge allocation): a short header, too many items for the buffer (also
    /// <see cref="uint.MaxValue"/>), an item outside the reply, a string or number running past the end.
    /// </summary>
    [Fact]
    public void DecodeList2_RefusesRepliesThatLieAboutTheirSize()
    {
        Assert.Throws<FormatException>(() => EverythingIpc.DecodeList2(new byte[19]));

        var tooMany = List2Reply.Build(EverythingIpc.RequestFullPath, 1, total: 1000);
        BinaryPrimitives.WriteUInt32LittleEndian(tooMany.AsSpan(4), 1000);
        Assert.Throws<FormatException>(() => EverythingIpc.DecodeList2(tooMany));
        BinaryPrimitives.WriteUInt32LittleEndian(tooMany.AsSpan(4), uint.MaxValue);
        Assert.Throws<FormatException>(() => EverythingIpc.DecodeList2(tooMany));

        var outside = List2Reply.Build(EverythingIpc.RequestFullPath, 1, total: 1, (false, List2Reply.Fields(@"C:\BC-TEST\a.txt")));
        BinaryPrimitives.WriteUInt32LittleEndian(outside.AsSpan(24), (uint)outside.Length + 1);
        Assert.Throws<FormatException>(() => EverythingIpc.DecodeList2(outside));

        byte[] hugeString = [0xFF, 0xFF, 0xFF, 0xFF, 0x41, 0x00, 0x00, 0x00];
        Assert.Throws<FormatException>(() => EverythingIpc.DecodeList2(List2Reply.Build(EverythingIpc.RequestFullPath, 1, total: 1, (false, hugeString))));
        byte[] cutString = [0x05, 0x00, 0x00, 0x00, 0x41, 0x00];
        Assert.Throws<FormatException>(() => EverythingIpc.DecodeList2(List2Reply.Build(EverythingIpc.RequestFullPath, 1, total: 1, (false, cutString))));
        Assert.Throws<FormatException>(() => EverythingIpc.DecodeList2(List2Reply.Build(EverythingIpc.RequestSize, 1, total: 1, (false, new byte[4]))));
        Assert.Throws<FormatException>(() => EverythingIpc.DecodeList2(List2Reply.Build(EverythingIpc.RequestRunCount, 1, total: 1, (false, new byte[2]))));
    }

    /// <summary>
    /// Dates Everything leaves unset or sends out of range (0, negative, beyond <see cref="DateTime.MaxValue"/>)
    /// read as unknown instead of throwing.
    /// </summary>
    [Fact]
    public void DecodeList2_TreatsUnsetAndOutOfRangeDatesAsUnknown()
    {
        foreach (var raw in new[] { 0L, -1L, long.MinValue, long.MaxValue, 2_650_467_744_000_000_000L })
        {
            var reply = List2Reply.Build(EverythingIpc.RequestDateRun, 1, total: 1, (false, List2Reply.Fields(raw)));
            Assert.Null(Assert.Single(EverythingIpc.DecodeList2(reply).Items).DateRun);
        }
    }
}

/// <summary>
/// Tests for the search texts sent to Everything: user words and paths must arrive as literals (Everything gives
/// meaning to spaces, quotes, <c>|</c>, <c>!</c> and <c>name:</c> prefixes), in the spelling both 1.4 and 1.5 know.
/// </summary>
public sealed class EverythingQueryTests
{
    /// <summary>
    /// Without search words the tab asks for every pick; each word becomes a quoted path term (all must match,
    /// anywhere, any case), quotes are dropped and syntax characters stay literal inside the quotes.
    /// </summary>
    [Fact]
    public void Picks_QuotesEachWordAsAPathTerm()
    {
        Assert.Equal("runcount:", EverythingQuery.Picks(null));
        Assert.Equal("runcount:", EverythingQuery.Picks(" \t "));
        Assert.Equal("runcount: path:\"report\" path:\"2026\"", EverythingQuery.Picks("  report   2026 "));
        Assert.Equal("runcount: path:\"ab\" path:\"c|d\" path:\"!e\" path:\"ext:png\"", EverythingQuery.Picks("a\"b c|d !e ext:png"));
        Assert.Equal("runcount: path:\"שלום\"", EverythingQuery.Picks("\" שלום"));
    }

    /// <summary>
    /// "Show in Everything" searches one quoted path; a trailing separator is dropped (Everything finds nothing
    /// for a folder written with one) except on a drive root, and a blank path is refused.
    /// </summary>
    [Fact]
    public void ForPath_QuotesThePathAndDropsATrailingSeparator()
    {
        Assert.Equal("\"C:\\BC-TEST\\a b (1).txt\"", EverythingQuery.ForPath(@"C:\BC-TEST\a b (1).txt"));
        Assert.Equal("\"C:\\BC-TEST\\folder\"", EverythingQuery.ForPath(@"C:\BC-TEST\folder\"));
        Assert.Equal("\"D:\\\"", EverythingQuery.ForPath(@"D:\"));
        Assert.Throws<ArgumentException>(() => EverythingQuery.ForPath(" "));
    }

    /// <summary>
    /// The command line passes the quoted search as one argument with the Windows quoting rules: inner quotes
    /// escaped, backslashes before a quote and before the closing quote doubled (round-tripped through
    /// <c>CommandLineToArgvW</c> in the Windows tests).
    /// </summary>
    [Fact]
    public void ShowCommandLine_QuotesTheSearchAsOneArgument()
    {
        Assert.Equal("-s \"\\\"C:\\BC-TEST\\a b.txt\\\"\"", EverythingQuery.ShowCommandLine(EverythingQuery.ForPath(@"C:\BC-TEST\a b.txt")));
        Assert.Equal("-s \"\\\"D:\\\\\\\"\"", EverythingQuery.ShowCommandLine(EverythingQuery.ForPath(@"D:\")));
        Assert.Equal("-s \"C:\\dir\\\\\"", EverythingQuery.ShowCommandLine(@"C:\dir\"));
        Assert.Throws<ArgumentNullException>(() => EverythingQuery.ShowCommandLine(null!));
    }
}

/// <summary>
/// Tests for reading Everything's saved <c>Run History.csv</c> (the tab's source while Everything is not running):
/// the verified format, CSV quoting, tolerance for odd rows, and where the file is looked for.
/// </summary>
public sealed class EverythingRunHistoryFileTests
{
    /// <summary>
    /// The verified format (quoted paths, decimal FILETIMEs) reads back newest opening first; commas and doubled
    /// quotes inside a quoted path, unquoted rows, Hebrew names, a byte order mark, CRLF and blank lines all work.
    /// </summary>
    [Fact]
    public void Parse_ReadsTheVerifiedFormatNewestFirst()
    {
        var older = At(2026, 9, 30);
        var newer = At(2026, 10, 1);
        var newest = At(2026, 10, 1).AddHours(1);
        var text = "\uFEFFFilename,Run Count,Last Run Date\r\n" +
                   $"\"C:\\BC-TEST\\a,b.txt\",3,{older.ToFileTime()}\r\n" +
                   "\r\n" +
                   $"C:\\BC-TEST\\plain.txt,1,{newest.ToFileTime()}\r\n" +
                   $"\"C:\\BC-TEST\\\"\"quoted\"\" שלום.txt\",12,{newer.ToFileTime()}\r\n";

        var picks = EverythingRunHistoryFile.Parse(text);

        Assert.Equal([@"C:\BC-TEST\plain.txt", @"C:\BC-TEST\""quoted"" שלום.txt", @"C:\BC-TEST\a,b.txt"], picks.Select(p => p.FullPath));
        Assert.Equal([1u, 12u, 3u], picks.Select(p => p.RunCount));
        Assert.Equal([newest, newer, older], picks.Select(p => p.LastOpened!.Value));
        Assert.All(picks, p => Assert.False(p.IsFolder));
    }

    /// <summary>Columns are found by their header names (any order, any case), so a reordered future file still reads.</summary>
    [Fact]
    public void Parse_FindsColumnsByHeaderName()
    {
        var opened = At(2026, 10, 1);
        var pick = Assert.Single(EverythingRunHistoryFile.Parse($"LAST RUN DATE,Extra,filename,run count\n{opened.ToFileTime()},x,C:\\BC-TEST\\a.txt,4\n"));
        Assert.Equal(new EverythingPick(@"C:\BC-TEST\a.txt", false, null, null, 4, opened), pick);
    }

    /// <summary>
    /// Rows without a path are skipped, an unreadable count reads as 1 and an unreadable date as unknown (sorted
    /// last, then by count); a file without a <c>Filename</c> column, or an empty one, gives nothing.
    /// </summary>
    [Fact]
    public void Parse_ToleratesOddRows()
    {
        var text = "Filename,Run Count,Last Run Date\n" +
                   ",5,1\n" +
                   "\"  \",5,1\n" +
                   "C:\\BC-TEST\\no-date.txt,7,soon\n" +
                   "C:\\BC-TEST\\bad-count.txt,-2,\n" +
                   $"C:\\BC-TEST\\dated.txt,x,{At(2026, 10, 1).ToFileTime()}\n" +
                   "C:\\BC-TEST\\short.txt\n";

        var picks = EverythingRunHistoryFile.Parse(text);

        Assert.Equal([@"C:\BC-TEST\dated.txt", @"C:\BC-TEST\no-date.txt", @"C:\BC-TEST\bad-count.txt", @"C:\BC-TEST\short.txt"], picks.Select(p => p.FullPath));
        Assert.Equal([1u, 7u, 1u, 1u], picks.Select(p => p.RunCount));
        Assert.Equal([false, true, true, true], picks.Select(p => p.LastOpened is null));
        Assert.Empty(EverythingRunHistoryFile.Parse("Path,Count\nC:\\BC-TEST\\a.txt,1\n"));
        Assert.Empty(EverythingRunHistoryFile.Parse(string.Empty));
        Assert.Throws<ArgumentNullException>(() => EverythingRunHistoryFile.Parse(null!));
    }

    /// <summary>
    /// A file cut off inside a quoted path (Everything killed mid-write) loses only that half path, never offering
    /// a file that does not exist; a last row without a final line break is complete and kept.
    /// </summary>
    [Fact]
    public void Parse_DropsAPathCutOffMidWrite()
    {
        var at = At(2026, 10, 1).ToFileTime();
        var cut = $"Filename,Run Count,Last Run Date\n\"C:\\BC-TEST\\whole.txt\",1,{at}\n\"C:\\BC-TEST\\ha";
        Assert.Equal([@"C:\BC-TEST\whole.txt"], EverythingRunHistoryFile.Parse(cut).Select(p => p.FullPath));

        var unterminated = $"Filename,Run Count,Last Run Date\n\"C:\\BC-TEST\\whole.txt\",1,{at}";
        Assert.Equal([@"C:\BC-TEST\whole.txt"], EverythingRunHistoryFile.Parse(unterminated).Select(p => p.FullPath));
    }

    /// <summary>
    /// The file is looked for in <c>%APPDATA%\Everything</c>, then next to the executable; a named instance adds
    /// <c>-name</c> to the file name (verified with a private instance and the 1.5 alpha).
    /// </summary>
    [Fact]
    public void CandidatePaths_CoverAppDataAndPortableCopies()
    {
        Assert.Equal(
            [@"C:\Users\BC-TEST\AppData\Roaming\Everything\Run History.csv", @"D:\Tools\Everything\Run History.csv"],
            EverythingRunHistoryFile.CandidatePaths(@"C:\Users\BC-TEST\AppData\Roaming", @"D:\Tools\Everything", null));
        Assert.Equal(
            [@"D:\Tools\Everything\Run History-1.5a.csv"],
            EverythingRunHistoryFile.CandidatePaths(" ", @"D:\Tools\Everything", "1.5a"));
        Assert.Empty(EverythingRunHistoryFile.CandidatePaths(null, null, null));
    }

    /// <summary>A UTC time on a date.</summary>
    /// <param name="year">Year.</param>
    /// <param name="month">Month.</param>
    /// <param name="day">Day.</param>
    /// <returns>Noon UTC on that day.</returns>
    private static DateTimeOffset At(int year, int month, int day) => new(year, month, day, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// Tests for the Everything tab's rules: which rows it shows (stored entries win over their picks; forgotten and
/// hidden picks never show), in which order, and how "hide until opened again" is kept.
/// </summary>
public sealed class EverythingTabTests
{
    private static readonly DateTimeOffset Now = TestData.Now;

    /// <summary>
    /// A pick's merge key is the content hash of a stored single-file list of the same path (case-insensitive,
    /// like NTFS), and its name and folder come from the path (a drive root keeps its full path).
    /// </summary>
    [Fact]
    public void Pick_HashNameAndFolder()
    {
        var pick = Pick(@"C:\BC-TEST\Report.TXT", Now);
        Assert.Equal(ContentHasher.ForFiles([@"c:\bc-test\report.txt"]), pick.FilesHash);
        Assert.Equal(ContentClassifier.Classify(TestData.Files(@"C:\BC-TEST\Report.TXT"))!.ContentHash, pick.FilesHash);
        Assert.Equal(("Report.TXT", @"C:\BC-TEST"), (pick.Name, pick.Folder));
        Assert.Equal(("folder", @"C:\BC-TEST"), (Pick(@"C:\BC-TEST\folder\", Now).Name, Pick(@"C:\BC-TEST\folder\", Now).Folder));
        Assert.Equal((@"D:\", (string?)null), (Pick(@"D:\", Now).Name, Pick(@"D:\", Now).Folder));
    }

    /// <summary>
    /// A pick whose file is stored shows once, as the stored entry (which carries pins and groups); forgotten and
    /// hidden picks and a repeated path are left out; the rest sort newest first.
    /// </summary>
    [Fact]
    public void Merge_StoredEntriesWin_ForgottenAndHiddenNeverShow()
    {
        var kept = Pick(@"C:\BC-TEST\kept.txt", Now.AddMinutes(-1));
        var forgotten = Pick(@"C:\BC-TEST\forgotten.txt", Now.AddMinutes(-2));
        var hidden = Pick(@"C:\BC-TEST\hidden.txt", Now.AddMinutes(-3));
        var plain = Pick(@"C:\BC-TEST\plain.txt", Now.AddMinutes(-4));
        var entry = Entry(1, kept.FilesHash, Now.AddMinutes(-10));
        var copy = Entry(2, ContentHasher.ForText("copied in Everything"), Now.AddMinutes(-5));

        var rows = EverythingTab.Merge(
            [entry, copy],
            [kept, forgotten, hidden, plain, plain with { RunCount = 9 }],
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase) { [@"c:\bc-test\HIDDEN.txt"] = Now.AddMinutes(-3) },
            new HashSet<string>([forgotten.FilesHash]),
            pinnedFirst: false);

        Assert.Equal([plain, null, null], rows.Select(r => r.Pick));
        Assert.Equal([null, 2L, 1L], rows.Select(r => r.Entry?.Id));
        Assert.Equal(Now.AddMinutes(-4), rows[0].Time);
    }

    /// <summary>
    /// Pinned entries go on top only when the "pinned on top" setting asks; otherwise everything is newest first.
    /// A pick Everything has no date for sorts last.
    /// </summary>
    [Fact]
    public void Merge_PinnedOnTopOnlyWhenAsked_UndatedLast()
    {
        var pinned = Entry(1, ContentHasher.ForText("pinned"), Now.AddDays(-30), pinned: true);
        var fresh = Pick(@"C:\BC-TEST\fresh.txt", Now);
        var undated = Pick(@"C:\BC-TEST\undated.txt", null);
        var none = new Dictionary<string, DateTimeOffset>();
        var nothingForgotten = new HashSet<string>();

        var pinnedFirst = EverythingTab.Merge([pinned], [undated, fresh], none, nothingForgotten, pinnedFirst: true);
        Assert.Equal([pinned, null, null], pinnedFirst.Select(r => r.Entry));
        Assert.Equal([null, fresh, undated], pinnedFirst.Select(r => r.Pick));

        var newestFirst = EverythingTab.Merge([pinned], [undated, fresh], none, nothingForgotten, pinnedFirst: false);
        Assert.Equal([fresh, null, undated], newestFirst.Select(r => r.Pick));
        Assert.Equal(DateTimeOffset.MinValue, newestFirst[2].Time);
    }

    /// <summary>
    /// A hide lasts until the file is opened again: it records the pick's own opening, so the same opening stays
    /// hidden and a later one (even one tick later) shows it again. A pick without a date stays hidden.
    /// </summary>
    [Fact]
    public void Hide_LastsUntilOpenedAgain()
    {
        var opened = Now.AddTicks(1);
        var hidden = EverythingTab.Hide(new Dictionary<string, DateTimeOffset>(), Pick(@"C:\BC-TEST\a.txt", opened), now: Now.AddHours(1));

        Assert.True(EverythingTab.IsHidden(Pick(@"C:\BC-TEST\a.txt", opened), hidden));
        Assert.True(EverythingTab.IsHidden(Pick(@"c:\bc-test\A.TXT", opened.AddTicks(-1)), hidden));
        Assert.False(EverythingTab.IsHidden(Pick(@"C:\BC-TEST\a.txt", opened.AddTicks(1)), hidden));
        Assert.False(EverythingTab.IsHidden(Pick(@"C:\BC-TEST\b.txt", opened), hidden));

        var undated = EverythingTab.Hide(hidden, Pick(@"C:\BC-TEST\undated.txt", null), now: Now.AddHours(1));
        Assert.Equal(Now.AddHours(1), undated[@"C:\BC-TEST\undated.txt"]);
        Assert.True(EverythingTab.IsHidden(Pick(@"C:\BC-TEST\undated.txt", null), undated));
        Assert.Equal(2, undated.Count);
        Assert.Single(hidden); // Hide returns a new map
    }

    /// <summary>
    /// Hides round-trip with exact ticks (a millisecond-rounded time would un-hide the pick at once); malformed
    /// lines are skipped, a repeated path keeps its newest hide, and paths compare case-insensitively.
    /// </summary>
    [Fact]
    public void HiddenState_RoundTripsExactly_AndSkipsMalformedLines()
    {
        var hides = new Dictionary<string, DateTimeOffset>
        {
            [@"C:\BC-TEST\a.txt"] = Now.AddTicks(1_234_567),
            [@"C:\BC-TEST\שלום עולם\b.txt"] = Now.AddDays(-1),
        };
        var parsed = EverythingTab.ParseHidden(EverythingTab.FormatHidden(hides));
        Assert.Equal(hides.OrderBy(h => h.Key), parsed.OrderBy(h => h.Key));
        Assert.True(parsed.ContainsKey(@"c:\BC-TEST\A.TXT"));

        var ticks = Now.UtcTicks.ToString(CultureInfo.InvariantCulture);
        var messy = string.Join('\n',
            "garbage",
            "\tC:\\BC-TEST\\no-ticks.txt",
            "123\t",
            "-5\tC:\\BC-TEST\\negative.txt",
            "99999999999999999999\tC:\\BC-TEST\\overflow.txt",
            "3155378976000000000\tC:\\BC-TEST\\past-max.txt",
            "x1\tC:\\BC-TEST\\letters.txt",
            $"{ticks}\tC:\\BC-TEST\\dup.txt\r",
            $"{Now.AddTicks(-1).UtcTicks}\tc:\\bc-test\\DUP.txt",
            string.Empty);
        var read = EverythingTab.ParseHidden(messy);
        Assert.Equal([@"C:\BC-TEST\dup.txt"], read.Keys);
        Assert.Equal(Now, read[@"C:\BC-TEST\dup.txt"]);
        Assert.Empty(EverythingTab.ParseHidden(null));
        Assert.Empty(EverythingTab.ParseHidden(string.Empty));
    }

    /// <summary>Only the newest hides are kept (the oldest can only bring an old pick back), newest first.</summary>
    [Fact]
    public void HiddenState_KeepsTheNewestOnly()
    {
        var many = Enumerable.Range(0, EverythingTab.MaxHidden + 100)
            .ToDictionary(i => $@"C:\BC-TEST\{i}.txt", i => Now.AddMinutes(i));
        var lines = EverythingTab.FormatHidden(many).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(EverythingTab.MaxHidden, lines.Length);
        Assert.EndsWith($@"\{EverythingTab.MaxHidden + 99}.txt", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(@"\100.txt", lines[^1], StringComparison.Ordinal);
        Assert.Equal(EverythingTab.MaxHidden, EverythingTab.Hide(many, Pick(@"C:\BC-TEST\new.txt", Now.AddDays(1)), Now).Count);
    }

    /// <summary>A pick as the live query or the saved file reports it.</summary>
    /// <param name="path">Full path.</param>
    /// <param name="opened">Last opening, or <see langword="null"/> when Everything has no date.</param>
    /// <returns>The pick.</returns>
    private static EverythingPick Pick(string path, DateTimeOffset? opened) => new(path, false, 10, Now.AddDays(-1), 1, opened);

    /// <summary>A stored entry of the Everything filter.</summary>
    /// <param name="id">Entry id.</param>
    /// <param name="hash">Content hash.</param>
    /// <param name="lastUsed">When it was last used.</param>
    /// <param name="pinned">Whether it is pinned.</param>
    /// <returns>The entry.</returns>
    private static ClipEntry Entry(long id, string hash, DateTimeOffset lastUsed, bool pinned = false) => new()
    {
        Id = id,
        Kind = ClipKind.Files,
        ContentHash = hash,
        LastUsedUtc = lastUsed,
        IsPinned = pinned,
        Origin = ClipOrigin.Everything,
    };
}

/// <summary>
/// Tests for the history side of the Everything tab: what <c>ClipFilter.Everything</c> lists, the
/// <c>Everything</c> origin's live-copy semantics, forgetting files by path, and the forgotten lookup that keeps
/// live picks out — plus the formats a pick is pasted with.
/// </summary>
public sealed class EverythingHistoryTests : IAsyncLifetime
{
    private static readonly SourceAppInfo Everything = new("Everything", @"C:\Program Files\Everything\Everything.exe", "Everything");
    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private CaptureRules rules = new() { Retention = RetentionPolicy.Unlimited };
    private ClipStore store = null!;
    private ClipHistoryService service = null!;

    /// <summary>Creates and starts a service over a temp store.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask InitializeAsync()
    {
        store = new ClipStore(temp.DatabasePath);
        store.Initialize();
        service = new ClipHistoryService(store, null, () => rules);
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

    /// <summary>
    /// The filter lists entries kept from the tab (origin Everything) and anything Everything.exe copied (by
    /// executable, any case, or by name), not other apps — <c>NotEverything.exe</c> included.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Filter_ListsKeptPicksAndCopiesFromEverything()
    {
        var kept = await Add(PickCapture(@"C:\BC-TEST\kept.txt", TestData.Now.AddMinutes(1)));
        var copied = await Add(TestData.Text("BC-TEST copied path", TestData.Now.AddMinutes(2), source: Everything));
        var byPath = await Add(TestData.Text("BC-TEST by path", TestData.Now.AddMinutes(3), source: new("everything", @"D:\Tools\EVERYTHING.EXE", "Tool")));
        await Add(TestData.Text("BC-TEST lookalike", TestData.Now.AddMinutes(4), source: new("NotEverything", @"C:\x\NotEverything.exe", "Not Everything")));
        await Add(TestData.Text("BC-TEST notepad", TestData.Now.AddMinutes(5)));

        var listed = await service.QueryAsync(new ClipQuery { Filter = ClipFilter.Everything, PinnedFirst = false });

        Assert.Equal([byPath.Id, copied.Id, kept.Id], listed.Select(e => e.Id));
        Assert.Equal(ClipOrigin.Everything, listed[2].Origin);
        Assert.Equal(ClipKind.Files, listed[2].Kind);
    }

    /// <summary>
    /// A pick kept from the tab behaves like a live copy: pause and the ignore list keep it out, it lifts a
    /// tombstone (an import of the same file stays deleted), and a repeat moves the entry to the top.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task EverythingOrigin_BehavesLikeALiveCopy()
    {
        rules = rules with { IsPaused = true };
        Assert.Null(await service.AddAsync(PickCapture(@"C:\BC-TEST\a.txt", TestData.Now)));
        rules = rules with { IsPaused = false, IgnoredProcessNames = new HashSet<string>(["Everything"], StringComparer.OrdinalIgnoreCase) };
        Assert.Null(await service.AddAsync(PickCapture(@"C:\BC-TEST\a.txt", TestData.Now)));
        rules = rules with { IgnoredProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) };

        var first = await Add(PickCapture(@"C:\BC-TEST\a.txt", TestData.Now));
        await service.DeleteAsync(first.Id);
        Assert.Null(await service.AddAsync(new ClipCapture { Formats = PickCapture(@"C:\BC-TEST\a.txt", TestData.Now).Formats, Origin = ClipOrigin.WindowsHistory }));
        var again = await Add(PickCapture(@"C:\BC-TEST\a.txt", TestData.Now.AddMinutes(1)));
        Assert.NotEqual(first.Id, again.Id);

        await Add(TestData.Text("BC-TEST newer", TestData.Now.AddMinutes(2)));
        var bumped = await Add(PickCapture(@"C:\BC-TEST\a.txt", TestData.Now.AddMinutes(3)));
        Assert.Equal(again.Id, bumped.Id);
        Assert.Equal(again.Id, (await service.QueryAsync(new ClipQuery { PinnedFirst = false, Limit = 1 })).Single().Id);
    }

    /// <summary>
    /// Forgetting a pick by its paths works whether or not it is stored: a stored copy is deleted (any case), the
    /// list says "1 file" from Everything, later copies of that file — a live one and one kept from the tab — are
    /// kept out and counted, and forgetting it again keeps the first list entry.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task ForgetFiles_KeepsTheFileOut_StoredOrNot()
    {
        var stored = await Add(TestData.Files(@"C:\BC-TEST\Secret.txt"));
        var result = await service.ForgetFilesAsync([@"c:\bc-test\secret.TXT"], "Everything");

        Assert.Equal([stored.Id], result.RemovedIds);
        Assert.Equal((ClipKind.Files, (int?)1, "Everything"), (result.Item.Kind, result.Item.FileCount, result.Item.SourceAppName));
        Assert.Null(await service.GetEntryAsync(stored.Id));
        Assert.Null(await service.AddAsync(TestData.Files(@"C:\BC-TEST\Secret.txt")));
        Assert.Null(await service.AddAsync(PickCapture(@"C:\BC-TEST\SECRET.txt", TestData.Now.AddMinutes(1))));
        Assert.Equal(2L, (await service.GetForgottenAsync()).Single().BlockedCount);

        var notStored = await service.ForgetFilesAsync([@"C:\BC-TEST\never-stored.txt"], null);
        Assert.Empty(notStored.RemovedIds);
        Assert.Null(notStored.Item.SourceAppName);

        var repeat = await service.ForgetFilesAsync([@"C:\BC-TEST\Secret.txt"], "Elsewhere");
        Assert.Equal((result.Item.Id, "Everything"), (repeat.Item.Id, repeat.Item.SourceAppName));
        Assert.Equal(2, (await service.GetForgottenAsync()).Count);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ForgetFilesAsync([], null));
    }

    /// <summary>
    /// The lookup that keeps live picks out answers only for forgotten fingerprints (duplicates fine) and is
    /// empty while the list is empty.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task FindForgotten_ReturnsOnlyForgottenFingerprints()
    {
        var secret = ContentHasher.ForFiles([@"C:\BC-TEST\secret.txt"]);
        var other = ContentHasher.ForFiles([@"C:\BC-TEST\other.txt"]);
        Assert.Empty(await service.FindForgottenAsync([secret, other]));

        await service.ForgetFilesAsync([@"C:\BC-TEST\secret.txt"], "Everything");

        Assert.Equal([secret], await service.FindForgottenAsync([secret, other, secret]));
        Assert.Empty(await service.FindForgottenAsync([]));
    }

    /// <summary>
    /// A pick pastes as Explorer's copy does (the file list plus a "copy" drop effect, so no target moves the
    /// file), or as its paths, one per line; no paths, or a blank one, is refused.
    /// </summary>
    [Fact]
    public void PickFormats_AreAFileCopyOrThePaths()
    {
        var files = ReplayFormats.ForFiles([@"C:\BC-TEST\a.txt", @"C:\BC-TEST\שלום"], plainTextOnly: false);
        Assert.Equal([ClipFormatNames.HDrop, ClipFormatNames.PreferredDropEffect], files.Select(f => f.Name));
        Assert.Equal([@"C:\BC-TEST\a.txt", @"C:\BC-TEST\שלום"], DropFilesCodec.Decode(files[0].Data));
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, files[1].Data);

        var text = Assert.Single(ReplayFormats.ForFiles([@"C:\BC-TEST\a.txt", @"C:\BC-TEST\b.txt"], plainTextOnly: true));
        Assert.Equal(ClipFormatNames.UnicodeText, text.Name);
        Assert.Equal("C:\\BC-TEST\\a.txt\r\nC:\\BC-TEST\\b.txt", UnicodeTextCodec.Decode(text.Data));

        Assert.Throws<ArgumentNullException>(() => ReplayFormats.ForFiles(null!, false));
        Assert.Throws<ArgumentException>(() => ReplayFormats.ForFiles([], false));
        Assert.Throws<ArgumentException>(() => ReplayFormats.ForFiles([@"C:\BC-TEST\a.txt", " "], true));
    }

    /// <summary>A file kept from the tab, as the app stores it.</summary>
    /// <param name="path">The file.</param>
    /// <param name="at">When it was kept.</param>
    /// <returns>The capture.</returns>
    private static ClipCapture PickCapture(string path, DateTimeOffset at) => new()
    {
        Formats = ReplayFormats.ForFiles([path], plainTextOnly: false),
        CapturedAtUtc = at,
        Origin = ClipOrigin.Everything,
        Source = Everything,
    };

    /// <summary>Adds a capture that must be stored.</summary>
    /// <param name="capture">The capture.</param>
    /// <returns>The stored entry.</returns>
    private async Task<ClipEntry> Add(ClipCapture capture) =>
        await service.AddAsync(capture) ?? throw new InvalidOperationException("The capture was not stored.");
}

/// <summary>
/// Builds <c>EVERYTHING_IPC_LIST2</c> replies the way Everything lays them out: the header, the item table, then
/// each item's fields (strings as a DWORD length, UTF-16 and a NUL; <see cref="long"/> as 8 bytes; <see cref="uint"/>
/// as 4), unaligned.
/// </summary>
internal static class List2Reply
{
    /// <summary>Lays out a reply.</summary>
    /// <param name="requestFlags">The fields present per item.</param>
    /// <param name="sort">The sort it reports.</param>
    /// <param name="total">Total matches it reports.</param>
    /// <param name="items">Each item's folder flag and field bytes (<see cref="Fields"/>).</param>
    /// <returns>The reply bytes.</returns>
    public static byte[] Build(uint requestFlags, uint sort, uint total, params (bool IsFolder, byte[] Fields)[] items)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(total);
        writer.Write((uint)items.Length);
        writer.Write(0u);
        writer.Write(requestFlags);
        writer.Write(sort);

        // Item data follows the table, in item order.
        uint dataAt = (uint)(20 + items.Length * 8);
        foreach (var (isFolder, fields) in items)
        {
            writer.Write(isFolder ? EverythingIpc.ItemFolder : 0u);
            writer.Write(dataAt);
            dataAt += (uint)fields.Length;
        }

        foreach (var (_, fields) in items)
        {
            writer.Write(fields);
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Encodes one item's fields in the given order.</summary>
    /// <param name="values">Strings, <see cref="long"/> (8-byte) and <see cref="uint"/> (4-byte) values.</param>
    /// <returns>The field bytes.</returns>
    /// <exception cref="ArgumentException">A value of another type.</exception>
    public static byte[] Fields(params object[] values)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var value in values)
        {
            switch (value)
            {
                case string text:
                    writer.Write((uint)text.Length);
                    writer.Write(Encoding.Unicode.GetBytes(text));
                    writer.Write((ushort)0);
                    break;
                case long number:
                    writer.Write(number);
                    break;
                case uint dword:
                    writer.Write(dword);
                    break;
                default:
                    throw new ArgumentException($"Unsupported field type {value.GetType()}.", nameof(values));
            }
        }

        writer.Flush();
        return stream.ToArray();
    }
}
