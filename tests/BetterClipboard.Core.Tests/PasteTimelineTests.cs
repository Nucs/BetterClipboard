using System.Diagnostics;
using System.Globalization;
using BetterClipboard.Core.Diagnostics;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the paste's timeline (CLAUDE.md §2.52): each stage's own time, the total from the key, which pastes are logged,
/// and the wording of the log line's numbers whatever the culture.
/// </summary>
public sealed class PasteTimelineTests
{
    /// <summary>Stopwatch ticks for a number of milliseconds (the timestamps the timeline is fed with).</summary>
    /// <param name="milliseconds">Milliseconds.</param>
    /// <returns>Ticks of <see cref="Stopwatch"/>.</returns>
    private static long Ticks(double milliseconds) => (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000.0);

    /// <summary>Each stage reads its own time since the stage before it, so the stages add up to the total.</summary>
    [Fact]
    public void Stages_ReadTheirOwnTimes_AndAddUpToTheTotal()
    {
        long start = 1_000_000;
        var timeline = new PasteTimeline(start);
        timeline.Mark("search", start + Ticks(0.2));
        timeline.Mark("formats", start + Ticks(3.4));
        timeline.Mark("panel away", start + Ticks(4.1));
        timeline.Mark("settle", start + Ticks(44.3));
        timeline.Mark("sent", start + Ticks(45.5));

        Assert.Equal("search 0 ms, formats 3 ms, panel away 1 ms, settle 40 ms, sent 1 ms", timeline.DescribeStages());
        Assert.Equal(45.5, timeline.Total.TotalMilliseconds, precision: 3);
    }

    /// <summary>Before any mark there is nothing to say: no stages, no time.</summary>
    [Fact]
    public void NoMarks_NoStagesAndNoTime()
    {
        var timeline = new PasteTimeline(42);
        Assert.Equal(string.Empty, timeline.DescribeStages());
        Assert.Equal(TimeSpan.Zero, timeline.Total);
    }

    /// <summary>
    /// A mark earlier than the one before it (measured elsewhere) counts as no time, and does not make the next stage look
    /// longer than it was; a mark before the start counts as no time either.
    /// </summary>
    [Fact]
    public void MarksOutOfOrder_NeverReadNegative_NorStretchTheNext()
    {
        long start = 5_000_000;
        var timeline = new PasteTimeline(start);
        timeline.Mark("a", start - Ticks(10));
        timeline.Mark("b", start + Ticks(20));
        timeline.Mark("c", start + Ticks(15));
        timeline.Mark("d", start + Ticks(25));

        Assert.Equal("a 0 ms, b 20 ms, c 0 ms, d 5 ms", timeline.DescribeStages());
        Assert.Equal(25, timeline.Total.TotalMilliseconds, precision: 3);
    }

    /// <summary>The session's first paste is always logged; later ones only from the slow threshold on.</summary>
    [Fact]
    public void ShouldLog_FirstAlways_LaterOnlyWhenSlow()
    {
        Assert.True(PasteTimeline.ShouldLog(TimeSpan.FromMilliseconds(5), firstOfSession: true));
        Assert.False(PasteTimeline.ShouldLog(PasteTimeline.SlowPaste - TimeSpan.FromMilliseconds(1), firstOfSession: false));
        Assert.True(PasteTimeline.ShouldLog(PasteTimeline.SlowPaste, firstOfSession: false));
        Assert.True(PasteTimeline.ShouldLog(TimeSpan.FromSeconds(2), firstOfSession: false));
    }

    /// <summary>Milliseconds are whole, rounded half away from zero, and never take the culture's digits or separators.</summary>
    [Fact]
    public void Milliseconds_AreWholeAndInvariant()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.Equal("1235", PasteTimeline.Milliseconds(TimeSpan.FromMilliseconds(1234.5)));
            Assert.Equal("0", PasteTimeline.Milliseconds(TimeSpan.FromMilliseconds(0.4)));
            Assert.Equal("41", PasteTimeline.Milliseconds(TimeSpan.FromMilliseconds(40.5)));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    /// <summary>A stage needs a name: the log line would otherwise show a bare number.</summary>
    [Fact]
    public void Mark_RefusesABlankStage()
    {
        var timeline = PasteTimeline.StartNow();
        Assert.Throws<ArgumentException>(() => timeline.Mark(" "));
        Assert.Throws<ArgumentNullException>(() => timeline.Mark(null!));
    }

    /// <summary>
    /// Marks from several threads at once (the paste's flow hops between the UI thread, the pool and the clipboard thread)
    /// all arrive.
    /// </summary>
    [Fact]
    public void Marks_FromManyThreads_AllArrive()
    {
        var timeline = PasteTimeline.StartNow();
        Parallel.For(0, 200, i => timeline.Mark($"s{i}"));
        Assert.Equal(200, timeline.DescribeStages().Split(", ").Length);
    }
}
