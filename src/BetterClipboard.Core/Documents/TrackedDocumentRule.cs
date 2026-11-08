using BetterClipboard.Core.Content;

namespace BetterClipboard.Core.Documents;

/// <summary>
/// Decides what a new version of a tracked document (a Notepad++ "new" tab, CLAUDE.md §2.41) does to the history entry that
/// holds it: update that entry's text in place, or keep the entry as it is and start a new copy. The owner's rule
/// (2026-11-08): "if content changes more than 20% then create a new copy", read as "only lost text counts".
/// </summary>
/// <remarks>
/// <para>
/// <b>What counts.</b> Only text that disappears: the stored copy's characters the new version no longer contains
/// (<see cref="TextLoss"/>). Typing more never splits, so a note written from nothing stays one entry; deleting or rewriting
/// splits, so a version that lost real text is kept.
/// </para>
/// <para>
/// <b>Added up since the copy started.</b> Each update in place adds its loss to <see cref="TrackedDocumentVersion.LostCharacters"/>,
/// and the copy splits as soon as the total is more than a fifth of the most text the copy held
/// (<see cref="TrackedDocumentVersion.PeakLength"/>). Deleting a fifth in one go, or in five small steps, keeps the same
/// version; a note that grew to 10,000 characters and loses 9,000 splits although the loss is measured against the longest
/// text, not the first. Exactly a fifth does not split ("more than").
/// </para>
/// <para>
/// <b>Which version survives.</b> On a split the entry keeps the text it last held (within a fifth of its peak), and the new
/// version starts as a new entry with its own clock. Nothing between two of the document's saves is seen, so "lost" is
/// measured between the versions BetterClipboard reads.
/// </para>
/// </remarks>
public static class TrackedDocumentRule
{
    /// <summary>
    /// The share of a copy's peak text that may be lost before it splits, as a fraction's denominator: 5 = a fifth (20 %).
    /// Integer arithmetic (<c>lost × 5 &gt; peak</c>) so the boundary is exact.
    /// </summary>
    public const int SplitDenominator = 5;

    /// <summary>
    /// Decides what <paramref name="current"/> does to the entry that holds <paramref name="stored"/>.
    /// </summary>
    /// <param name="stored">The text the entry holds now.</param>
    /// <param name="current">The document's new text.</param>
    /// <param name="version">The entry's version bookkeeping (what it lost so far, the most text it held).</param>
    /// <param name="workBudget">Comparisons the exact measurement may spend (see <see cref="TextLoss.DefaultWorkBudget"/>).</param>
    /// <returns>The decision and the bookkeeping the entry carries afterwards.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stored"/> or <paramref name="current"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workBudget"/> is not positive.</exception>
    public static TrackedDocumentDecision Decide(string stored, string current, TrackedDocumentVersion version, long workBudget = TextLoss.DefaultWorkBudget)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(current);
        if (string.Equals(stored, current, StringComparison.Ordinal))
        {
            return new TrackedDocumentDecision(TrackedDocumentChange.Unchanged, version, 0, IsExact: true);
        }

        // The peak is at least the stored text: an entry recorded by an older state (or bookkeeping lost with a database
        // fix) must not get a smaller allowance than the text it holds.
        long peak = Math.Max(version.PeakLength, stored.Length);
        long lostSoFar = Math.Max(0, version.LostCharacters);

        // Split when (lostSoFar + lost) × 5 > peak, i.e. lost > floor(peak / 5) − lostSoFar.
        long allowed = (peak / SplitDenominator) - lostSoFar;
        var measured = TextLoss.Measure(stored, current, allowed, workBudget);
        if (measured.ExceedsAllowed)
        {
            return new TrackedDocumentDecision(
                TrackedDocumentChange.NewCopy,
                new TrackedDocumentVersion(0, current.Length),
                measured.LostCharacters,
                measured.IsExact);
        }

        return new TrackedDocumentDecision(
            TrackedDocumentChange.UpdateInPlace,
            new TrackedDocumentVersion(lostSoFar + measured.LostCharacters, Math.Max(peak, current.Length)),
            measured.LostCharacters,
            measured.IsExact);
    }
}

/// <summary>
/// The bookkeeping of one copy of a tracked document: what it lost so far through updates in place, and the most text it
/// held. Kept with the document's mapping, so it survives restarts.
/// </summary>
/// <param name="LostCharacters">Characters lost by the copy's updates in place since it started.</param>
/// <param name="PeakLength">The longest text the copy held (in UTF-16 code units).</param>
public readonly record struct TrackedDocumentVersion(long LostCharacters, long PeakLength)
{
    /// <summary>The bookkeeping of a copy that just started with <paramref name="text"/>.</summary>
    /// <param name="text">Its first text.</param>
    /// <returns>Nothing lost, peak = the text's length.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static TrackedDocumentVersion Start(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new TrackedDocumentVersion(0, text.Length);
    }
}

/// <summary>What a new version of a tracked document does to its entry.</summary>
public enum TrackedDocumentChange
{
    /// <summary>The text is the same: nothing to write.</summary>
    Unchanged,

    /// <summary>Replace the entry's text where it is (it keeps its place, pins and groups).</summary>
    UpdateInPlace,

    /// <summary>Keep the entry as it is and store the new text as a new copy.</summary>
    NewCopy,
}

/// <summary>The outcome of <see cref="TrackedDocumentRule.Decide"/>.</summary>
/// <param name="Change">What to do.</param>
/// <param name="Next">The bookkeeping the (possibly new) copy carries afterwards.</param>
/// <param name="LostCharacters">The loss this version measured (a lower bound or estimate when not <paramref name="IsExact"/>).</param>
/// <param name="IsExact">Whether the loss was measured exactly.</param>
public readonly record struct TrackedDocumentDecision(TrackedDocumentChange Change, TrackedDocumentVersion Next, long LostCharacters, bool IsExact);
