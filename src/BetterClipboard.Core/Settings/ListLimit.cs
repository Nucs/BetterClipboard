namespace BetterClipboard.Core.Settings;

/// <summary>
/// The rule every integration limit in Settings follows: a positive number caps a list (or one catch-up pass) at that
/// many, and 0 — the default — lifts the cap.
/// </summary>
/// <remarks>
/// <para>
/// Until 2026-11-21 these limits were fixed in code: the Cmd tab kept the newest 1,000 commands it saw, each shell tab
/// remembered 1,000 hidden commands and the Everything tab 500 hidden files, and ShareX and the Snipping tab imported at
/// most the 100 newest screenshots saved while BetterClipboard was not running. The owner asked for all of them to be
/// unlimited by default ("We should set and migrate all to default unlimited") and chose one number per limit in its
/// integration's card in Settings, 0 = unlimited (CLAUDE.md §2.62). Nothing needed moving: the old caps lived in code,
/// so every settings file, old or new, reads 0 for them.
/// </para>
/// <para>
/// A lowered limit applies at once to the lists BetterClipboard keeps (the oldest entries go); a raised or lifted one
/// cannot bring back what an earlier, smaller cap already dropped.
/// </para>
/// </remarks>
public static class ListLimit
{
    /// <summary>The highest limit Settings accepts; a hand-edited file is clamped to it (<see cref="Normalize"/>).</summary>
    public const int Maximum = 1_000_000;

    /// <summary>Whether a limit lifts the cap: 0, or a negative number a hand-edited file may hold.</summary>
    /// <param name="limit">The limit as stored.</param>
    /// <returns><see langword="true"/> when nothing is capped.</returns>
    public static bool IsUnlimited(int limit) => limit <= 0;

    /// <summary>
    /// How many items a limit lets through: the limit itself, or <see cref="int.MaxValue"/> when it is unlimited — the
    /// form <c>Take</c>, <c>TakeLast</c> and friends need.
    /// </summary>
    /// <param name="limit">The limit as stored.</param>
    /// <returns>A positive count.</returns>
    public static int Effective(int limit) => limit > 0 ? limit : int.MaxValue;

    /// <summary>Clamps a limit read from a settings file to 0..<see cref="Maximum"/> (a negative one means unlimited).</summary>
    /// <param name="limit">The limit as read.</param>
    /// <returns>The limit to keep.</returns>
    public static int Normalize(int limit) => Math.Clamp(limit, 0, Maximum);
}
