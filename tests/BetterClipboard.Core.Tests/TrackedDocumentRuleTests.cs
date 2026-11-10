using BetterClipboard.Core.Content;
using BetterClipboard.Core.Documents;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for <see cref="TextLoss"/> (how much of a stored text a newer one lost) and <see cref="TrackedDocumentRule"/> (the
/// owner's 20% rule, read as "only lost text counts", added up since the copy started, CLAUDE.md §2.41).
/// </summary>
public sealed class TrackedDocumentRuleTests
{
    /// <summary>The note of the question page (2026-11-08), step by step.</summary>
    private const string Step1 = "Groceries: milk, eggs";

    /// <summary>Step 2: more items added to the line.</summary>
    private const string Step2 = "Groceries: milk, eggs, bread, coffee, apples, rice, pasta";

    /// <summary>Step 3: three to-do lines added.</summary>
    private const string Step3 = Step2 + "\nCall Dana about the trip\nBook the dentist for Tuesday\nReturn the library books";

    /// <summary>Step 4: half the shopping list deleted (26.5 % of step 3's text).</summary>
    private const string Step4 = "Groceries: milk, eggs\nCall Dana about the trip\nBook the dentist for Tuesday\nReturn the library books";

    /// <summary>Step 5: cleared and rewritten.</summary>
    private const string Step5 = "Meeting notes: Q3 budget review\n- hiring plan\n- travel freeze";

    /// <summary>Insertions lose nothing; deletions and replacements lose exactly what disappeared.</summary>
    /// <param name="stored">The older text.</param>
    /// <param name="current">The newer text.</param>
    /// <param name="lost">The expected loss.</param>
    [Theory]
    [MemberData(nameof(LossCases))]
    public void Measure_IsTheStoredTextNotKept(string stored, string current, long lost)
    {
        var result = TextLoss.Measure(stored, current);
        Assert.True(result.IsExact);
        Assert.Equal(lost, result.LostCharacters);
        Assert.False(result.ExceedsAllowed);
    }

    /// <summary>Loss cases: (stored, current, characters lost).</summary>
    public static TheoryData<string, string, long> LossCases => new()
    {
        { "", "", 0 },
        { "", "anything", 0 },
        { "abc", "", 3 },
        { "abc", "abc", 0 },
        { "hello world", "hello brave new world", 0 },
        { "hello brave new world", "hello world", 10 },
        { "the cat sat", "the dog sat", 3 },
        { "aaaa", "aaa", 1 },
        { "abcdef", "fedcba", 5 },
        { Step1, Step2, 0 },
        { Step2, Step3, 0 },
        { Step3, Step4, 36 },
        { "line one\r\nline two\r\n", "line one\nline two\n", 2 },
        { "😀 smile", "smile", 3 },
    };

    /// <summary>
    /// Against a brute-force longest common subsequence: 400 random pairs over a small alphabet (many repeats, the hard case),
    /// with and without an allowance, always exact for small texts.
    /// </summary>
    [Fact]
    public void Measure_MatchesTheLongestCommonSubsequence()
    {
        var random = new Random(20261108);
        for (int round = 0; round < 400; round++)
        {
            string a = RandomText(random, random.Next(0, 40));
            string b = Mutate(random, a);
            long expected = a.Length - Lcs(a, b);
            var unbounded = TextLoss.Measure(a, b);
            Assert.True(unbounded.IsExact);
            Assert.Equal(expected, unbounded.LostCharacters);

            long allowed = random.Next(0, 12);
            var bounded = TextLoss.Measure(a, b, allowed);
            Assert.Equal(expected > allowed, bounded.ExceedsAllowed);
            if (!bounded.ExceedsAllowed)
            {
                Assert.Equal(expected, bounded.LostCharacters);
            }
            else
            {
                Assert.True(bounded.LostCharacters > allowed);
            }
        }
    }

    /// <summary>
    /// Past the work budget the loss is estimated from sampled shingles: close to the truth for a large text edited in many
    /// places, and flagged as not exact.
    /// </summary>
    [Fact]
    public void Measure_EstimatesWhenTheBudgetRunsOut()
    {
        var random = new Random(7);
        var words = Enumerable.Range(0, 20_000).Select(_ => RandomWord(random)).ToArray();
        string stored = string.Join(' ', words);

        // Every tenth word rewritten with '#', which the original never has: exactly those words' letters are lost, in 2,000
        // places — far beyond the exact search's budget.
        string current = string.Join(' ', words.Select((w, i) => i % 10 == 0 ? new string('#', w.Length) : w));
        double truth = (double)words.Where((_, i) => i % 10 == 0).Sum(w => w.Length) / stored.Length;
        var estimated = TextLoss.Measure(stored, current);
        Assert.False(estimated.IsExact);

        // Each scattered edit spoils every 16-character window over it, so the estimate errs towards more loss (a new copy,
        // the side that keeps text): about 0.3 here against 0.09.
        double share = (double)estimated.LostCharacters / stored.Length;
        Assert.InRange(share, truth, truth + 0.30);
    }

    /// <summary>A length difference alone larger than the allowance is decided without a search.</summary>
    [Fact]
    public void Measure_LengthAloneExceedsTheAllowance()
    {
        var result = TextLoss.Measure(new string('x', 1000), "y", allowedLoss: 10);
        Assert.True(result.ExceedsAllowed);
        Assert.True(result.LostCharacters > 10);
    }

    /// <summary>Arguments are checked.</summary>
    [Fact]
    public void Measure_ChecksArguments()
    {
        Assert.Throws<ArgumentNullException>(() => TextLoss.Measure(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => TextLoss.Measure("x", null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextLoss.Measure("x", "y", workBudget: 0));
    }

    /// <summary>
    /// The question page's note, exactly as it showed the owner (option "Only lost text counts"): growing never splits; the
    /// 26.5 % deletion and the rewrite each start a new copy — three entries in the end.
    /// </summary>
    [Fact]
    public void Decide_TheQuestionPageExample_EndsWithThreeEntries()
    {
        var version = TrackedDocumentVersion.Start(Step1);
        int entries = 1;
        string stored = Step1;
        foreach (var (next, expected) in new[] { (Step2, TrackedDocumentChange.UpdateInPlace), (Step3, TrackedDocumentChange.UpdateInPlace), (Step4, TrackedDocumentChange.NewCopy), (Step5, TrackedDocumentChange.NewCopy) })
        {
            var decision = TrackedDocumentRule.Decide(stored, next, version);
            Assert.Equal(expected, decision.Change);
            entries += decision.Change == TrackedDocumentChange.NewCopy ? 1 : 0;
            version = decision.Next;
            stored = next;
        }

        Assert.Equal(3, entries);
    }

    /// <summary>A note written from nothing, one character at a time, stays one entry whatever its length.</summary>
    [Fact]
    public void Decide_TypingFromNothing_NeverSplits()
    {
        string text = "a";
        var version = TrackedDocumentVersion.Start(text);
        for (int i = 0; i < 300; i++)
        {
            string next = text + (char)('a' + (i % 26));
            var decision = TrackedDocumentRule.Decide(text, next, version);
            Assert.Equal(TrackedDocumentChange.UpdateInPlace, decision.Change);
            version = decision.Next;
            text = next;
        }

        Assert.Equal(0, version.LostCharacters);
        Assert.Equal(301, version.PeakLength);
    }

    /// <summary>Exactly a fifth lost does not split ("more than"); one character more does.</summary>
    [Fact]
    public void Decide_ExactlyAFifth_IsNotMoreThanAFifth()
    {
        string stored = new('x', 100);
        var start = TrackedDocumentVersion.Start(stored);
        Assert.Equal(TrackedDocumentChange.UpdateInPlace, TrackedDocumentRule.Decide(stored, new string('x', 80), start).Change);
        Assert.Equal(TrackedDocumentChange.NewCopy, TrackedDocumentRule.Decide(stored, new string('x', 79), start).Change);
    }

    /// <summary>
    /// Small deletions add up: five deletions of 5 % each stay in place until the total passes a fifth of the copy's peak.
    /// </summary>
    [Fact]
    public void Decide_SmallDeletionsAddUp()
    {
        string text = string.Concat(Enumerable.Range(0, 100).Select(i => (char)('A' + (i % 26))));
        var version = TrackedDocumentVersion.Start(text);
        var changes = new List<TrackedDocumentChange>();
        for (int step = 0; step < 5; step++)
        {
            string next = text[5..];
            var decision = TrackedDocumentRule.Decide(text, next, version);
            changes.Add(decision.Change);
            version = decision.Next;
            text = next;
        }

        // 5, 10, 15, 20 lost: in place; 25 > 20: a new copy.
        Assert.Equal([TrackedDocumentChange.UpdateInPlace, TrackedDocumentChange.UpdateInPlace, TrackedDocumentChange.UpdateInPlace, TrackedDocumentChange.UpdateInPlace, TrackedDocumentChange.NewCopy], changes);
        Assert.Equal(new TrackedDocumentVersion(0, 75), version);
    }

    /// <summary>A note that grew big and then loses most of it splits, though its first text was tiny.</summary>
    [Fact]
    public void Decide_GrowThenDelete_Splits()
    {
        string first = "x";
        string grown = new('x', 10_000);
        var version = TrackedDocumentRule.Decide(first, grown, TrackedDocumentVersion.Start(first)).Next;
        Assert.Equal(10_000, version.PeakLength);
        Assert.Equal(TrackedDocumentChange.NewCopy, TrackedDocumentRule.Decide(grown, new string('x', 1000), version).Change);
    }

    /// <summary>An unchanged text keeps the bookkeeping; a stored text longer than the recorded peak raises the allowance.</summary>
    [Fact]
    public void Decide_UnchangedAndPeakAtLeastTheStoredText()
    {
        var version = new TrackedDocumentVersion(3, 10);
        var same = TrackedDocumentRule.Decide("hello", "hello", version);
        Assert.Equal(TrackedDocumentChange.Unchanged, same.Change);
        Assert.Equal(version, same.Next);

        // Peak 10 recorded, but the stored text is 100 long: the allowance is 20, not 2.
        string stored = new('y', 100);
        Assert.Equal(TrackedDocumentChange.UpdateInPlace, TrackedDocumentRule.Decide(stored, new string('y', 85), new TrackedDocumentVersion(0, 10)).Change);
    }

    /// <summary>A brute-force longest common subsequence (dynamic programming, two rows).</summary>
    /// <param name="a">First text.</param>
    /// <param name="b">Second text.</param>
    /// <returns>Its length.</returns>
    private static int Lcs(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        foreach (char ch in a)
        {
            for (int j = 0; j < b.Length; j++)
            {
                current[j + 1] = ch == b[j] ? previous[j] + 1 : Math.Max(previous[j + 1], current[j]);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>A random text over "abc " (repeats on purpose).</summary>
    /// <param name="random">The generator.</param>
    /// <param name="length">The length.</param>
    /// <returns>The text.</returns>
    private static string RandomText(Random random, int length) =>
        new([.. Enumerable.Range(0, length).Select(_ => "abc "[random.Next(4)])]);

    /// <summary>Applies a few random insertions, deletions and replacements.</summary>
    /// <param name="random">The generator.</param>
    /// <param name="text">The text.</param>
    /// <returns>The edited text.</returns>
    private static string Mutate(Random random, string text)
    {
        var chars = text.ToList();
        int edits = random.Next(0, 8);
        for (int i = 0; i < edits; i++)
        {
            int position = chars.Count == 0 ? 0 : random.Next(chars.Count);
            switch (random.Next(3))
            {
                case 0:
                    chars.Insert(position, "abcd"[random.Next(4)]);
                    break;
                case 1 when chars.Count > 0:
                    chars.RemoveAt(position);
                    break;
                case 2 when chars.Count > 0:
                    chars[position] = "abcd"[random.Next(4)];
                    break;
            }
        }

        return new string([.. chars]);
    }

    /// <summary>A random lowercase word of 3–9 letters.</summary>
    /// <param name="random">The generator.</param>
    /// <returns>The word.</returns>
    private static string RandomWord(Random random) =>
        new([.. Enumerable.Range(0, random.Next(3, 10)).Select(_ => (char)('a' + random.Next(26)))]);
}
