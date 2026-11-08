namespace BetterClipboard.Core.Content;

/// <summary>
/// Measures how much of a stored text a newer version of it no longer contains: the characters of the stored text that
/// are not part of a longest common subsequence of the two. Added text costs nothing; deleted or rewritten text counts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a subsequence.</b> "Lost" means "gone from the text", whatever was added around it: a sentence typed in the middle
/// of a note loses nothing, a paragraph deleted loses its length, a word replaced loses the replaced word. The longest common
/// subsequence answers exactly that, and its size follows from the edit distance <c>D</c> (insertions plus deletions) as
/// <c>lost = (storedLength − currentLength + D) / 2</c>.
/// </para>
/// <para>
/// <b>Cost.</b> The common prefix and suffix are skipped first (linear), so an edit in one place — what typing does —
/// compares only the edited middle. The middle runs through Myers' O((N+M)·D) algorithm, stopped as soon as the loss is
/// known to exceed what the caller allows (<c>allowedLoss</c>) and when the work spent passes a budget. Only past the budget
/// (large texts edited in many places, e.g. a replace-all) the loss is estimated from sampled 16-character shingles instead
/// (<see cref="TextLossResult.IsExact"/> is then <see langword="false"/>): linear time, a few megabytes for the largest text
/// the history accepts.
/// </para>
/// <para>
/// Text is compared as UTF-16 code units, the unit every count in BetterClipboard uses; a character outside the Basic
/// Multilingual Plane counts twice, the same in both texts. Thread-safe (no shared state).
/// </para>
/// </remarks>
public static class TextLoss
{
    /// <summary>
    /// Work budget of the exact algorithm in comparisons: about a quarter of a second on a 2026 PC. Past it, the loss is
    /// estimated (see the class remarks).
    /// </summary>
    public const long DefaultWorkBudget = 40_000_000;

    /// <summary>Length of the shingles the estimate compares; long enough that unrelated text rarely shares one.</summary>
    internal const int ShingleLength = 16;

    /// <summary>
    /// One shingle in this many is sampled by the estimate (by its hash, so both texts sample the same shingles), which keeps
    /// its memory to a few megabytes for a 64 MB text.
    /// </summary>
    internal const int ShingleSampleRate = 8;

    /// <summary>
    /// Measures the characters of <paramref name="stored"/> that <paramref name="current"/> no longer contains.
    /// </summary>
    /// <param name="stored">The older text (what was kept).</param>
    /// <param name="current">The newer text.</param>
    /// <param name="allowedLoss">
    /// The most loss the caller still tolerates: once the loss is known to be larger, the measurement stops and reports
    /// <see cref="TextLossResult.ExceedsAllowed"/> with a lower bound. <see cref="long.MaxValue"/> (the default) always
    /// measures to the end. Negative counts as 0.
    /// </param>
    /// <param name="workBudget">Comparisons the exact algorithm may spend before it estimates (see the class remarks).</param>
    /// <returns>The loss, whether it is exact, and whether it exceeds <paramref name="allowedLoss"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stored"/> or <paramref name="current"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workBudget"/> is not positive.</exception>
    public static TextLossResult Measure(string stored, string current, long allowedLoss = long.MaxValue, long workBudget = DefaultWorkBudget)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workBudget);
        allowedLoss = Math.Max(0, allowedLoss);

        // Skip what both share at the ends: typing changes one place, so the middle is usually a few characters wide.
        int prefix = CommonPrefix(stored, current);
        int suffix = CommonSuffix(stored, current, prefix);
        var a = stored.AsSpan(prefix, stored.Length - prefix - suffix);
        var b = current.AsSpan(prefix, current.Length - prefix - suffix);
        if (a.IsEmpty)
        {
            return new TextLossResult(0, IsExact: true, ExceedsAllowed: false);
        }

        if (b.IsEmpty)
        {
            return new TextLossResult(a.Length, IsExact: true, ExceedsAllowed: a.Length > allowedLoss);
        }

        // lost = (n − m + D) / 2 ≤ allowed  ⇔  D ≤ 2·allowed − n + m. Every D is at least |n − m|, so a limit below that
        // proves the loss too large without any search.
        long n = a.Length, m = b.Length;
        long dLimit = Math.Min(n + m, allowedLoss >= long.MaxValue / 4 ? n + m : (2 * allowedLoss) - n + m);
        if (dLimit < Math.Abs(n - m))
        {
            // The difference in length alone loses more than allowed: report that lower bound.
            return new TextLossResult(Math.Max(n - m, allowedLoss + 1), IsExact: false, ExceedsAllowed: true);
        }

        long? distance = EditDistance(a, b, dLimit, workBudget, out bool budgetSpent);
        if (distance is { } d)
        {
            long lost = (n - m + d) / 2;
            return new TextLossResult(lost, IsExact: true, ExceedsAllowed: lost > allowedLoss);
        }

        if (!budgetSpent)
        {
            // The search ended past dLimit within its budget: the loss is larger than allowed (and at least allowed + 1).
            return new TextLossResult(allowedLoss + 1, IsExact: false, ExceedsAllowed: true);
        }

        long estimate = EstimateLoss(a, b);
        return new TextLossResult(estimate, IsExact: false, ExceedsAllowed: estimate > allowedLoss);
    }

    /// <summary>Length of the common prefix of two texts.</summary>
    /// <param name="a">First text.</param>
    /// <param name="b">Second text.</param>
    /// <returns>The number of leading code units they share.</returns>
    private static int CommonPrefix(string a, string b) => a.AsSpan().CommonPrefixLength(b.AsSpan());

    /// <summary>Length of the common suffix of two texts, never reaching into an already shared prefix.</summary>
    /// <param name="a">First text.</param>
    /// <param name="b">Second text.</param>
    /// <param name="prefix">The common prefix already taken from both (the suffix must not overlap it).</param>
    /// <returns>The number of trailing code units they share outside the prefix.</returns>
    private static int CommonSuffix(string a, string b, int prefix)
    {
        int limit = Math.Min(a.Length, b.Length) - prefix;
        int i = 0;
        while (i < limit && a[a.Length - 1 - i] == b[b.Length - 1 - i])
        {
            i++;
        }

        return i;
    }

    /// <summary>
    /// The insertion-plus-deletion edit distance of two texts by Myers' greedy forward algorithm (lengths only, no edit
    /// script: O(<paramref name="dLimit"/>) memory), or <see langword="null"/> when it exceeds <paramref name="dLimit"/> or
    /// the work budget ran out first.
    /// </summary>
    /// <param name="a">The older middle.</param>
    /// <param name="b">The newer middle.</param>
    /// <param name="dLimit">Largest distance worth finding.</param>
    /// <param name="workBudget">Comparisons allowed.</param>
    /// <param name="budgetSpent"><see langword="true"/> when the result is <see langword="null"/> because the budget ran out.</param>
    /// <returns>The distance, or <see langword="null"/>.</returns>
    private static long? EditDistance(ReadOnlySpan<char> a, ReadOnlySpan<char> b, long dLimit, long workBudget, out bool budgetSpent)
    {
        budgetSpent = false;
        int n = a.Length, m = b.Length;

        // The frontier array grows with the distance actually reached, not with dLimit: most searches end within a few
        // steps, and dLimit can be millions for a large text.
        int capacity = (int)Math.Min(dLimit, 1024);
        int[] v = new int[(2 * capacity) + 3];
        int offset = capacity + 1;
        long work = 0;
        for (long d = 0; d <= dLimit; d++)
        {
            if (d > capacity - 1)
            {
                // Double the frontier, re-centred, keeping the values of the diagonals reached so far.
                int newCapacity = (int)Math.Min(dLimit + 1, (long)capacity * 2);
                var grown = new int[(2 * newCapacity) + 3];
                Array.Copy(v, 0, grown, newCapacity - capacity, v.Length);
                v = grown;
                offset = newCapacity + 1;
                capacity = newCapacity;
            }

            int di = (int)d;
            for (int k = -di; k <= di; k += 2)
            {
                int x = k == -di || (k != di && v[offset + k - 1] < v[offset + k + 1])
                    ? v[offset + k + 1]
                    : v[offset + k - 1] + 1;
                int y = x - k;
                int snakeStart = x;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;

                // Each diagonal costs one step plus the characters its snake walked over.
                work += 1 + (x - snakeStart);
                if (x >= n && y >= m)
                {
                    return d;
                }
            }

            if (work > workBudget)
            {
                budgetSpent = true;
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Estimates the loss from sampled shingles: the share of the older text's sampled 16-character windows the newer text
    /// no longer has (counted as multisets, so repeated lines are matched once each), scaled to the older text's length.
    /// </summary>
    /// <param name="a">The older middle (at least one character).</param>
    /// <param name="b">The newer middle.</param>
    /// <returns>The estimated number of lost characters, between 0 and <c>a.Length</c>.</returns>
    private static long EstimateLoss(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        var counts = new Dictionary<ulong, int>();
        int sampledA = 0;
        foreach (var hash in SampledShingles(a))
        {
            counts[hash] = counts.GetValueOrDefault(hash) + 1;
            sampledA++;
        }

        if (sampledA == 0)
        {
            // Too short or too uniform to sample: treat the whole middle as changed (it is the safe side: a new copy).
            return a.Length;
        }

        int common = 0;
        foreach (var hash in SampledShingles(b))
        {
            if (counts.TryGetValue(hash, out int count) && count > 0)
            {
                counts[hash] = count - 1;
                common++;
            }
        }

        double keptShare = (double)common / sampledA;
        return (long)Math.Round(a.Length * (1 - keptShare));
    }

    /// <summary>
    /// The hashes of a text's 16-character windows that the sample keeps (a polynomial rolling hash, so each window costs
    /// two multiplications).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The sampled hashes, in text order.</returns>
    private static List<ulong> SampledShingles(ReadOnlySpan<char> text)
    {
        const ulong Base = 1_000_003;
        var sampled = new List<ulong>(Math.Max(0, text.Length / ShingleSampleRate));
        if (text.Length < ShingleLength)
        {
            return sampled;
        }

        ulong power = 1;
        for (int i = 1; i < ShingleLength; i++)
        {
            power *= Base;
        }

        ulong hash = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (i >= ShingleLength)
            {
                // Drop the character leaving the window before adding the new one (wrapping arithmetic is intended).
                hash -= text[i - ShingleLength] * power;
            }

            hash = (hash * Base) + text[i];
            if (i >= ShingleLength - 1)
            {
                // Mix before sampling: the low bits of a polynomial hash follow the last character too closely.
                ulong mixed = hash ^ (hash >> 29);
                mixed *= 0xBF58476D1CE4E5B9;
                mixed ^= mixed >> 32;
                if (mixed % ShingleSampleRate == 0)
                {
                    sampled.Add(mixed);
                }
            }
        }

        return sampled;
    }
}

/// <summary>What <see cref="TextLoss.Measure"/> found.</summary>
/// <param name="LostCharacters">
/// Characters of the stored text the newer one no longer contains — exact when <paramref name="IsExact"/>, an estimate or a
/// lower bound otherwise.
/// </param>
/// <param name="IsExact">Whether <paramref name="LostCharacters"/> is the exact loss (the search finished within its limits).</param>
/// <param name="ExceedsAllowed">Whether the loss is larger than the caller's allowance.</param>
public readonly record struct TextLossResult(long LostCharacters, bool IsExact, bool ExceedsAllowed);
