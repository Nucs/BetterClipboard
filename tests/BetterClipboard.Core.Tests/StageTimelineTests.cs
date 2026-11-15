using System.Diagnostics;
using BetterClipboard.Core.Diagnostics;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the generic stage timeline behind the summon's log line (CLAUDE.md §2.53); the paste's own rules are in
/// <see cref="PasteTimelineTests"/>, which also cover the arithmetic every timeline shares.
/// </summary>
public sealed class StageTimelineTests
{
    /// <summary>Stopwatch ticks for a number of milliseconds (the timestamps the timeline is fed with).</summary>
    /// <param name="milliseconds">Milliseconds.</param>
    /// <returns>Ticks of <see cref="Stopwatch"/>.</returns>
    private static long Ticks(double milliseconds) => (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000.0);

    /// <summary>
    /// A summon's stages read the way its log line shows them, and a stage marked at a timestamp taken earlier (the moment
    /// the panel was on screen) closes the list at that moment.
    /// </summary>
    [Fact]
    public void SummonStages_ReadInOrder_EndingAtTheGivenMoment()
    {
        long key = 7_000_000;
        var timeline = new StageTimeline(key);
        timeline.Mark("queued", key + Ticks(2.6));
        timeline.Mark("prepared", key + Ticks(63.9));
        timeline.Mark("moved", key + Ticks(65.2));
        timeline.Mark("activated", key + Ticks(135.4));
        timeline.Mark("shown", key + Ticks(154.0));

        Assert.Equal("queued 3 ms, prepared 61 ms, moved 1 ms, activated 70 ms, shown 19 ms", timeline.DescribeStages());
        Assert.Equal(154.0, timeline.Total.TotalMilliseconds, precision: 3);
        Assert.Equal(key, timeline.StartedAt);
    }

    /// <summary>A paste's timeline is a stage timeline: code that only reads stages takes either.</summary>
    [Fact]
    public void PasteTimeline_IsAStageTimeline()
    {
        StageTimeline timeline = PasteTimeline.StartNow();
        timeline.Mark("sent");
        Assert.StartsWith("sent ", timeline.DescribeStages(), StringComparison.Ordinal);
    }
}
