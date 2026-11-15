using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace BetterClipboard.Core.Diagnostics;

/// <summary>
/// The stages of one user-visible operation, timed from the key or click that asked for it — so a "this takes a moment"
/// report can be checked against the log: where the time went, stage by stage. Content-free: stage names and milliseconds
/// only. A paste (<see cref="PasteTimeline"/>) and a summon of the panel each keep one.
/// </summary>
/// <remarks>
/// <para>
/// One timeline belongs to one operation. Its stages are marked one after another along one flow that may hop between the
/// UI thread, the thread pool and other threads, so a lock keeps the list whole even though two marks never race in
/// practice.
/// </para>
/// <para>
/// A stage's time is the time since the stage marked before it (the first one's since the start), so the stages add up to
/// the total: "queued 2 ms, prepared 5 ms, activated 14 ms" reads as where the operation spent its time, in order.
/// </para>
/// </remarks>
public class StageTimeline
{
    /// <summary>Guards <see cref="marks"/>.</summary>
    private readonly Lock gate = new();

    /// <summary>The stages marked so far, in order, with their Stopwatch timestamps.</summary>
    private readonly List<(string Stage, long At)> marks = new(8);

    /// <summary>Starts a timeline at <paramref name="startedAt"/>.</summary>
    /// <param name="startedAt">A <see cref="Stopwatch.GetTimestamp"/> value: when the user asked for the operation.</param>
    public StageTimeline(long startedAt) => StartedAt = startedAt;

    /// <summary>When the user asked for the operation (a <see cref="Stopwatch.GetTimestamp"/> value).</summary>
    public long StartedAt { get; }

    /// <summary>Marks the end of a stage now.</summary>
    /// <param name="stage">The stage's name as the log shows it ("formats", "activated"; English, like every log line).</param>
    /// <exception cref="ArgumentException"><paramref name="stage"/> is null or blank.</exception>
    public void Mark(string stage) => Mark(stage, Stopwatch.GetTimestamp());

    /// <summary>Marks the end of a stage at a given moment (tests; and a stage measured elsewhere).</summary>
    /// <param name="stage">The stage's name as the log shows it.</param>
    /// <param name="at">A <see cref="Stopwatch.GetTimestamp"/> value; one earlier than the previous mark counts as zero time.</param>
    /// <exception cref="ArgumentException"><paramref name="stage"/> is null or blank.</exception>
    public void Mark(string stage, long at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        lock (gate)
        {
            marks.Add((stage, at));
        }
    }

    /// <summary>
    /// The time from the start to the last mark — for a finished paste, from the key to Ctrl+V; zero before any mark.
    /// </summary>
    public TimeSpan Total
    {
        get
        {
            lock (gate)
            {
                return marks.Count == 0 ? TimeSpan.Zero : Between(StartedAt, marks[^1].At);
            }
        }
    }

    /// <summary>
    /// The stages with their own times, in order: "search 0 ms, formats 3 ms, clipboard 2 ms" — whole milliseconds, rounded,
    /// invariant culture. Empty before any mark.
    /// </summary>
    /// <returns>The list, for a log line.</returns>
    public string DescribeStages()
    {
        lock (gate)
        {
            var text = new StringBuilder();
            long previous = StartedAt;
            foreach (var (stage, at) in marks)
            {
                if (text.Length > 0)
                {
                    text.Append(", ");
                }

                text.Append(stage).Append(' ').Append(Milliseconds(Between(previous, at)));
                text.Append(" ms");

                // A mark taken out of order (measured elsewhere, earlier) must not make the next stage look longer.
                previous = Math.Max(previous, at);
            }

            return text.ToString();
        }
    }

    /// <summary>Whole milliseconds of a duration, rounded to the nearest, for the invariant culture.</summary>
    /// <param name="duration">The duration.</param>
    /// <returns>The number without its unit ("412").</returns>
    public static string Milliseconds(TimeSpan duration) =>
        Math.Round(duration.TotalMilliseconds, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>The time between two Stopwatch timestamps, never negative.</summary>
    /// <param name="from">The earlier timestamp.</param>
    /// <param name="to">The later timestamp.</param>
    /// <returns>The duration; zero when <paramref name="to"/> is not after <paramref name="from"/>.</returns>
    private static TimeSpan Between(long from, long to) => to > from ? Stopwatch.GetElapsedTime(from, to) : TimeSpan.Zero;
}
