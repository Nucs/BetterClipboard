using BetterClipboard.Core.Content;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for giving memory back (CLAUDE.md §2.45): when a reclaim runs (<see cref="MemoryReclaimPlanner"/>), how its outcome
/// is worded for the log (<see cref="MemoryUsage"/>), and the store's read of one payload that keeps the image overlays from
/// reading every format of an entry (<see cref="ClipStore.GetFirstFormat"/>).
/// </summary>
public sealed class MemoryReclaimTests : IDisposable
{
    /// <summary>The clock the planner tests start at.</summary>
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory temp = TestData.NewTempDirectory();

    /// <summary>Deletes the temp store.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>Nothing pending: nothing is due, nothing is taken, whatever the time.</summary>
    [Fact]
    public void Planner_NothingPending_TakesNothing()
    {
        var planner = new MemoryReclaimPlanner();
        Assert.Null(planner.DueAt);
        Assert.Empty(planner.Reasons);
        Assert.False(planner.TryTake(T0.AddHours(1), busy: false, out var taken));
        Assert.Empty(taken);
    }

    /// <summary>A request is due its delay after it; before that it is not taken, after it once, with its reason.</summary>
    [Fact]
    public void Planner_Request_IsDueAfterItsDelay_AndTakenOnce()
    {
        var planner = new MemoryReclaimPlanner();
        planner.Request(T0, TimeSpan.FromSeconds(3), "Settings closed");
        Assert.Equal(T0.AddSeconds(3), planner.DueAt);
        Assert.False(planner.TryTake(T0.AddSeconds(2.9), busy: false, out _));
        Assert.True(planner.TryTake(T0.AddSeconds(3), busy: false, out var taken));
        Assert.Equal(["Settings closed"], taken);
        Assert.Null(planner.DueAt);
        Assert.False(planner.TryTake(T0.AddSeconds(10), busy: false, out _));
    }

    /// <summary>
    /// A second request for the same reason postpones it (a burst ends first) and a shorter one never brings it forward;
    /// another reason keeps its own due time, so Settings closed is reclaimed three seconds later even while the panel's
    /// longer wait is pending — which then runs on its own.
    /// </summary>
    [Fact]
    public void Planner_SameReasonPostpones_OtherReasonsKeepTheirOwnTime()
    {
        var planner = new MemoryReclaimPlanner();
        planner.Request(T0, TimeSpan.FromSeconds(30), "using the panel");
        planner.Request(T0.AddSeconds(10), TimeSpan.FromSeconds(30), "using the panel");
        Assert.Equal(T0.AddSeconds(40), planner.DueAt);
        planner.Request(T0.AddSeconds(11), TimeSpan.FromSeconds(1), "using the panel");
        Assert.Equal(T0.AddSeconds(40), planner.DueAt);

        planner.Request(T0.AddSeconds(12), TimeSpan.FromSeconds(3), "Settings closed");
        Assert.Equal(T0.AddSeconds(15), planner.DueAt);
        Assert.Equal(["using the panel", "Settings closed"], planner.Reasons);

        Assert.True(planner.TryTake(T0.AddSeconds(15), busy: false, out var first));
        Assert.Equal(["Settings closed"], first);
        Assert.Equal(T0.AddSeconds(40), planner.DueAt);
        Assert.False(planner.TryTake(T0.AddSeconds(39), busy: false, out _));
        Assert.True(planner.TryTake(T0.AddSeconds(40), busy: false, out var second));
        Assert.Equal(["using the panel"], second);
        Assert.Null(planner.DueAt);
    }

    /// <summary>Reasons that are due by the time of one reclaim are all taken by it, in the order they were first asked for.</summary>
    [Fact]
    public void Planner_DueReasons_AreTakenTogether()
    {
        var planner = new MemoryReclaimPlanner();
        planner.Request(T0, TimeSpan.FromSeconds(90), "the start");
        planner.Request(T0.AddSeconds(5), TimeSpan.FromSeconds(30), "using the panel");
        planner.Request(T0.AddSeconds(80), TimeSpan.FromSeconds(60), "a list refresh");

        // The panel stayed open past both due times: both due reasons go in one reclaim, the refresh keeps waiting.
        Assert.False(planner.TryTake(T0.AddSeconds(95), busy: true, out _));
        Assert.True(planner.TryTake(T0.AddSeconds(100), busy: false, out var taken));
        Assert.Equal(["the start", "using the panel"], taken);
        Assert.Equal(["a list refresh"], planner.Reasons);
        Assert.Equal(T0.AddSeconds(140), planner.DueAt);
    }

    /// <summary>A burst that never ends still gets its reclaim <see cref="MemoryReclaimPlanner.MaxWait"/> after its first request.</summary>
    [Fact]
    public void Planner_EndlessBurst_IsBoundedByMaxWait()
    {
        var planner = new MemoryReclaimPlanner();
        for (int second = 0; second <= 600; second += 20)
        {
            planner.Request(T0.AddSeconds(second), TimeSpan.FromSeconds(30), "a list refresh");
            Assert.True(planner.DueAt <= T0 + MemoryReclaimPlanner.MaxWait || planner.DueAt == T0.AddSeconds(second));
        }

        // Past the bound, a new request makes it due at once.
        Assert.Equal(T0.AddSeconds(600), planner.DueAt);
        Assert.True(planner.TryTake(T0.AddSeconds(600), busy: false, out var taken));
        Assert.Equal(["a list refresh"], taken);

        // The next burst gets a bound of its own.
        planner.Request(T0.AddSeconds(700), TimeSpan.FromSeconds(30), "using the panel");
        Assert.Equal(T0.AddSeconds(730), planner.DueAt);
    }

    /// <summary>A due reclaim waits while the panel or Settings is on screen, and is taken once it is not.</summary>
    [Fact]
    public void Planner_Busy_WaitsAndKeepsThePendingReclaim()
    {
        var planner = new MemoryReclaimPlanner();
        planner.Request(T0, TimeSpan.FromSeconds(3), "Settings closed");
        Assert.False(planner.TryTake(T0.AddMinutes(5), busy: true, out var none));
        Assert.Empty(none);
        Assert.Equal(T0.AddSeconds(3), planner.DueAt);
        Assert.True(planner.TryTake(T0.AddMinutes(5), busy: false, out var taken));
        Assert.Equal(["Settings closed"], taken);
    }

    /// <summary>Requests are checked: no negative delay, and a reason the log can name.</summary>
    [Fact]
    public void Planner_Request_ChecksItsArguments()
    {
        var planner = new MemoryReclaimPlanner();
        Assert.Throws<ArgumentOutOfRangeException>(() => planner.Request(T0, TimeSpan.FromSeconds(-1), "x"));
        Assert.ThrowsAny<ArgumentException>(() => planner.Request(T0, TimeSpan.Zero, " "));
        Assert.ThrowsAny<ArgumentException>(() => planner.Request(T0, TimeSpan.Zero, null!));
        Assert.Null(planner.DueAt);

        // A zero delay is due at once.
        planner.Request(T0, TimeSpan.Zero, "the start");
        Assert.True(planner.TryTake(T0, busy: false, out _));
    }

    /// <summary>
    /// The usage line is whole megabytes from 10 MB on and one decimal below (a freshly trimmed working set of 0.3 MB once
    /// read "0 MB" in the log), names the private working set when Windows reports it, and leaves it out otherwise.
    /// </summary>
    [Fact]
    public void Usage_Describe_IsMegabytes_WithADecimalBelowTen()
    {
        var usage = new MemoryUsage(182L * 1048576 + 300_000, 240L * 1048576, 131L * 1048576, 12L * 1048576);
        Assert.Equal("private 182 MB, memory 131 MB (working set 240 MB), .NET heap 12 MB", usage.Describe());
        Assert.Equal("private 1.0 MB, working set 2.0 MB, .NET heap 0.0 MB", new MemoryUsage(1048576, 2 * 1048576, null, 10).Describe());
        var trimmed = new MemoryUsage(114L * 1048576, 400_000, 300_000, 3L * 1048576 + 200_000);
        Assert.Equal("private 114 MB, memory 0.3 MB (working set 0.4 MB), .NET heap 3.2 MB", trimmed.Describe());

        // Just under ten rounds to ten as a whole number, never to "10.0".
        Assert.Equal("private 10 MB, working set 9.9 MB, .NET heap 10 MB", new MemoryUsage(10_440_000, 10_380_000, null, 10_485_760).Describe());
    }

    /// <summary>A release is worded as what was given back; no release, or a growth meanwhile, as the plain numbers.</summary>
    [Fact]
    public void Usage_DescribeRelease_NeverSaysANegativeRelease()
    {
        var before = new MemoryUsage(182L * 1048576, 0, null, 0);
        var after = new MemoryUsage(130L * 1048576, 0, null, 0);
        Assert.Equal("gave back 52 MB of private memory (182 → 130 MB)", MemoryUsage.DescribeRelease(before, after));
        Assert.Equal("private memory 130 → 182 MB", MemoryUsage.DescribeRelease(after, before));
        Assert.Equal("private memory 182 → 182 MB", MemoryUsage.DescribeRelease(before, before));
        Assert.Throws<ArgumentNullException>(() => MemoryUsage.DescribeRelease(null!, after));
        Assert.Throws<ArgumentNullException>(() => MemoryUsage.DescribeRelease(before, null!));
    }

    /// <summary>
    /// The formats a reader of the picture asks for are the ones the classifier picks from, in its order: whichever of
    /// them an entry stores first in that order is the one <see cref="ContentClassifier.FindPrimaryImage"/> chooses.
    /// </summary>
    [Fact]
    public void PrimaryImageFormats_MatchFindPrimaryImage()
    {
        var names = ContentClassifier.PrimaryImageFormats;
        Assert.Equal([ClipFormatNames.Png, ClipFormatNames.PngMime, ClipFormatNames.DibV5, ClipFormatNames.Dib], names);
        for (int first = 0; first < names.Count; first++)
        {
            // Every format from this one down the list is stored, in reverse order: the classifier must still pick this one.
            var formats = names.Skip(first).Reverse().Select(name => new ClipFormatData(name, [1, 2, 3])).ToList();
            var capture = new ClipCapture { Formats = formats, CapturedAtUtc = TestData.Now };
            Assert.Equal(names[first], ContentClassifier.FindPrimaryImage(capture)?.Name);
        }
    }

    /// <summary>
    /// <see cref="ClipStore.GetFirstFormat"/> returns the most wanted payload an entry stores, whatever the stored order,
    /// the first stored of one name, and nothing for an entry without any of them or one that does not exist.
    /// </summary>
    [Fact]
    public void Store_GetFirstFormat_ReadsTheMostWantedPayload()
    {
        var store = new ClipStore(temp.DatabasePath);
        store.Initialize();
        byte[] dib = Enumerable.Repeat((byte)7, 4096).ToArray();
        byte[] png = Enumerable.Repeat((byte)9, 64).ToArray();
        var capture = new ClipCapture
        {
            // The DIB first, as a producer may put it: the reader must still get the PNG.
            Formats = [new ClipFormatData(ClipFormatNames.DibV5, dib), new ClipFormatData(ClipFormatNames.Png, png), new ClipFormatData(ClipFormatNames.Png, [1])],
            CapturedAtUtc = TestData.Now,
            Source = TestData.Notepad,
        };
        long id = store.Upsert(capture, ContentClassifier.Classify(capture)!, new ImageAnalysis(32, 32, [1, 2, 3]), bumpIfExists: true)!.Entry.Id;

        var first = store.GetFirstFormat(id, ContentClassifier.PrimaryImageFormats);
        Assert.Equal(ClipFormatNames.Png, first?.Name);
        Assert.Equal(png, first?.Data);

        var onlyDib = store.GetFirstFormat(id, [ClipFormatNames.Dib, ClipFormatNames.DibV5]);
        Assert.Equal(ClipFormatNames.DibV5, onlyDib?.Name);
        Assert.Equal(dib, onlyDib?.Data);

        Assert.Null(store.GetFirstFormat(id, [ClipFormatNames.UnicodeText]));
        Assert.Null(store.GetFirstFormat(id + 1000, ContentClassifier.PrimaryImageFormats));
        Assert.Null(store.GetFirstFormat(id, []));
        Assert.Throws<ArgumentNullException>(() => store.GetFirstFormat(id, null!));
    }
}
