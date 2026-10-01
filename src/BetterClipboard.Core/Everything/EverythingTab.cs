using System.Globalization;
using System.Text;
using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.Everything;

/// <summary>
/// The rules of the panel's Everything tab: which rows it shows and in which order. It merges two sources —
/// history entries that came from Everything (<see cref="Storage.ClipFilter.Everything"/>) and the files opened in
/// Everything, read live (<see cref="EverythingPick"/>) — and keeps the user's "hide" choices.
/// </summary>
/// <remarks>
/// <para>
/// <b>Merging.</b> A pick whose file is also stored (same content hash: the user pasted, copied or kept it) is
/// shown once, as the stored entry, which carries pins and groups. A pick on the "Forget forever" list is never
/// shown. Rows sort newest first by when they were last used or opened, with pinned entries on top when the
/// user's "pinned on top" setting asks for it.
/// </para>
/// <para>
/// <b>Hiding.</b> Deleting a pick cannot delete anything in Everything (the integration only reads), so it hides
/// the pick instead, until it is opened in Everything again: the hide remembers the pick's last opening, and a
/// later one shows it again. Hides are kept in the encrypted store (<see cref="HiddenStateName"/>), newest
/// <see cref="MaxHidden"/>, with exact ticks — a time rounded to milliseconds would compare earlier than the
/// opening it recorded and un-hide the pick at once.
/// </para>
/// </remarks>
public static class EverythingTab
{
    /// <summary>Most live picks the tab asks Everything for (the newest openings).</summary>
    public const int MaxPicks = 200;

    /// <summary>Most stored entries the tab loads (the tab has no paging: two bounded lists are merged instead).</summary>
    public const int MaxStoredEntries = 200;

    /// <summary>Store state name of the hidden picks (<see cref="FormatHidden"/>).</summary>
    public const string HiddenStateName = "everything.hidden";

    /// <summary>Most hides kept; the oldest go first, which can only bring an old pick back.</summary>
    public const int MaxHidden = 500;

    /// <summary>
    /// Merges stored entries and live picks into the tab's rows (see class remarks).
    /// </summary>
    /// <param name="stored">Entries of <see cref="Storage.ClipFilter.Everything"/>, any order.</param>
    /// <param name="picks">Live picks, any order; a path listed twice shows once.</param>
    /// <param name="hidden">Hidden picks by path (case-insensitive), from <see cref="ParseHidden"/>.</param>
    /// <param name="forgottenHashes">The picks' <see cref="EverythingPick.FilesHash"/> values that are on the "Forget forever" list.</param>
    /// <param name="pinnedFirst">Put pinned entries on top (the "pinned on top" setting).</param>
    /// <returns>The rows in display order.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IReadOnlyList<EverythingTabRow> Merge(
        IReadOnlyList<ClipEntry> stored,
        IReadOnlyList<EverythingPick> picks,
        IReadOnlyDictionary<string, DateTimeOffset> hidden,
        IReadOnlySet<string> forgottenHashes,
        bool pinnedFirst)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(picks);
        ArgumentNullException.ThrowIfNull(hidden);
        ArgumentNullException.ThrowIfNull(forgottenHashes);

        var storedHashes = stored.Select(e => e.ContentHash).ToHashSet(StringComparer.Ordinal);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<EverythingTabRow>(stored.Count + picks.Count);
        rows.AddRange(stored.Select(e => new EverythingTabRow(e, null)));
        foreach (var pick in picks)
        {
            if (!seenPaths.Add(pick.FullPath))
            {
                continue;
            }

            var hash = pick.FilesHash;
            if (storedHashes.Contains(hash) || forgottenHashes.Contains(hash) || IsHidden(pick, hidden))
            {
                continue;
            }

            rows.Add(new EverythingTabRow(null, pick));
        }

        // OrderBy is stable: equal times keep the sources' own order (Everything's sort, the store's sort).
        return rows
            .OrderByDescending(r => pinnedFirst && r.Entry?.IsPinned == true)
            .ThenByDescending(r => r.Time)
            .ToList();
    }

    /// <summary>
    /// Whether a pick is hidden: hidden at or after its latest opening. A pick Everything has no opening date for
    /// stays hidden once hidden.
    /// </summary>
    /// <param name="pick">The pick.</param>
    /// <param name="hidden">Hidden picks by path.</param>
    /// <returns><see langword="true"/> when the tab must not show it.</returns>
    public static bool IsHidden(EverythingPick pick, IReadOnlyDictionary<string, DateTimeOffset> hidden)
    {
        ArgumentNullException.ThrowIfNull(pick);
        ArgumentNullException.ThrowIfNull(hidden);
        return hidden.TryGetValue(pick.FullPath, out var hiddenAt) && (pick.LastOpened is not { } opened || opened <= hiddenAt);
    }

    /// <summary>
    /// Records a hide: the pick stays hidden until it is opened again after its current opening.
    /// </summary>
    /// <param name="hidden">The current hides (not changed).</param>
    /// <param name="pick">The pick to hide.</param>
    /// <param name="now">Fallback for a pick without an opening date.</param>
    /// <returns>A new map with the hide, trimmed to the newest <see cref="MaxHidden"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static Dictionary<string, DateTimeOffset> Hide(IReadOnlyDictionary<string, DateTimeOffset> hidden, EverythingPick pick, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        ArgumentNullException.ThrowIfNull(pick);
        var next = new Dictionary<string, DateTimeOffset>(hidden, StringComparer.OrdinalIgnoreCase)
        {
            // Its own opening time, not "now": an opening a moment later (even within the same millisecond as the
            // hide) then shows it again, and clock adjustments cannot hide later openings.
            [pick.FullPath] = pick.LastOpened ?? now,
        };
        return Trim(next);
    }

    /// <summary>
    /// Reads the stored hides: one line per pick, <c>UTC ticks</c>, a tab, the path. Malformed lines are skipped (a
    /// bad line can only un-hide one pick).
    /// </summary>
    /// <param name="value">The state value; <see langword="null"/> or empty when nothing is hidden.</param>
    /// <returns>The hides by path, case-insensitive.</returns>
    public static Dictionary<string, DateTimeOffset> ParseHidden(string? value)
    {
        var hidden = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(value))
        {
            return hidden;
        }

        foreach (var line in value.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab <= 0 || tab == line.Length - 1 ||
                !long.TryParse(line.AsSpan(0, tab), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
            {
                continue;
            }

            var path = line[(tab + 1)..].TrimEnd('\r');
            var at = new DateTimeOffset(ticks, TimeSpan.Zero);
            if (!hidden.TryGetValue(path, out var existing) || at > existing)
            {
                hidden[path] = at;
            }
        }

        return hidden;
    }

    /// <summary>
    /// Writes hides for <see cref="ParseHidden"/>. Paths cannot contain tabs or line breaks (Windows forbids control
    /// characters in names), so the format needs no escaping.
    /// </summary>
    /// <param name="hidden">The hides.</param>
    /// <returns>The state value, newest <see cref="MaxHidden"/> only, newest first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hidden"/> is <see langword="null"/>.</exception>
    public static string FormatHidden(IReadOnlyDictionary<string, DateTimeOffset> hidden)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        var builder = new StringBuilder();
        foreach (var (path, at) in Trim(hidden).OrderByDescending(p => p.Value))
        {
            builder.Append(at.UtcTicks.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(path).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Keeps the newest <see cref="MaxHidden"/> hides.</summary>
    /// <param name="hidden">The hides.</param>
    /// <returns>A trimmed copy, case-insensitive.</returns>
    private static Dictionary<string, DateTimeOffset> Trim(IReadOnlyDictionary<string, DateTimeOffset> hidden) =>
        hidden.OrderByDescending(p => p.Value)
            .Take(MaxHidden)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>One row of the Everything tab: a stored entry or a live pick — exactly one of them.</summary>
/// <param name="Entry">A history entry from Everything, or <see langword="null"/> for a live pick.</param>
/// <param name="Pick">A live pick, or <see langword="null"/> for a stored entry.</param>
public sealed record EverythingTabRow(ClipEntry? Entry, EverythingPick? Pick)
{
    /// <summary>The sort time: when the entry was last used, or when the pick was last opened (no date sorts last).</summary>
    public DateTimeOffset Time => Entry?.LastUsedUtc ?? Pick?.LastOpened ?? DateTimeOffset.MinValue;
}
