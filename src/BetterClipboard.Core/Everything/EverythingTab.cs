using System.Globalization;
using System.Text;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Presentation;

namespace BetterClipboard.Core.Everything;

/// <summary>
/// The rules of the panel's Everything tab: which rows it shows and in which order. It merges two sources —
/// history entries that came from Everything (<see cref="Storage.ClipFilter.Everything"/>) and the files opened in
/// Everything, read live (<see cref="EverythingPick"/>) — and keeps the user's "hide" choices.
/// </summary>
/// <remarks>
/// <para>
/// <b>Merging</b> (<see cref="MergedTabOrder{TItem}"/>). A pick whose file is also stored (same content
/// hash: the user pasted, copied or kept it) is shown once, as the stored entry, which carries pins and groups — at the
/// newer of its last use and the pick's last opening (until 2026-11-11 at its last use only: a file kept last month and
/// opened again today sorted as last month's). A pick on the "Forget forever" list is never shown. Rows sort newest first
/// by when they were last used or opened, with pinned entries on top when the user's "pinned on top" setting asks for it;
/// a pick Everything has no opening date for sorts last.
/// </para>
/// <para>
/// <b>Hiding.</b> Deleting a pick cannot delete anything in Everything (the integration only reads), so it hides
/// the pick instead, until it is opened in Everything again: the hide remembers the pick's last opening, and a
/// later one shows it again. Hides are kept in the encrypted store (<see cref="HiddenStateName"/>), as many as Settings
/// allows (<see cref="Settings.AppSettings.MaxHiddenEverythingPicks"/>: no limit by default; the newest 500 until
/// 2026-11-21), with exact ticks — a time rounded to milliseconds would compare earlier than the opening it recorded and
/// un-hide the pick at once.
/// </para>
/// <para>
/// <b>Every row.</b> The tab shows every pick Everything remembers and every stored entry from Everything (CLAUDE.md
/// §2.62): the panel merges them at once and turns them into cards a page at a time as the list scrolls
/// (<see cref="Presentation.RowPages{TRow}"/>). Until 2026-11-21 it asked Everything for the 200 newest picks and loaded 200
/// stored entries (the constants <c>MaxPicks</c> and <c>MaxStoredEntries</c>), and had no paging.
/// </para>
/// </remarks>
public static class EverythingTab
{
    /// <summary>Store state name of the hidden picks (<see cref="FormatHidden"/>).</summary>
    public const string HiddenStateName = "everything.hidden";

    /// <summary>
    /// Merges stored entries and live picks into the tab's rows (see class remarks).
    /// </summary>
    /// <param name="stored">Entries of <see cref="Storage.ClipFilter.Everything"/>, any order.</param>
    /// <param name="picks">Live picks, any order; a path listed twice shows once.</param>
    /// <param name="hidden">Hidden picks by path (case-insensitive), from <see cref="ParseHidden"/>.</param>
    /// <param name="forgottenHashes">The picks' <see cref="EverythingPick.FilesHash"/> values that are on the "Forget forever" list.</param>
    /// <param name="pinnedFirst">Put pinned entries on top (the "pinned on top" setting).</param>
    /// <returns>The rows in display order, each with the time it sorts by (<see cref="EverythingTabRow.Time"/>).</returns>
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

        // Filtered before the merge: a forgotten or hidden pick never shows, and so never places its stored twin either
        // (that entry then goes by its own last use). A repeated path is dropped before the hide is checked, as before.
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shown = new List<EverythingPick>(picks.Count);
        foreach (var pick in picks)
        {
            if (!seenPaths.Add(pick.FullPath) || forgottenHashes.Contains(pick.FilesHash) || IsHidden(pick, hidden))
            {
                continue;
            }

            shown.Add(pick);
        }

        // The merge takes the picks newest first. OrderBy is stable: equal dates keep Everything's own order. A pick without
        // a date counts as the oldest possible time, so every stored entry goes before it.
        var newestFirst = shown.OrderByDescending(OpenedOrOldest).ToList();
        var order = new MergedTabOrder<EverythingPick>(stored, pinnedFirst, p => p.FilesHash, p => OpenedOrOldest(p));
        return order.Next(newestFirst, last: true)
            .Select(r => new EverythingTabRow(r.Entry, r.Item, r.Time ?? DateTimeOffset.MinValue))
            .ToList();
    }

    /// <summary>A pick's sort time: its last opening, or the oldest possible time when Everything has no date for it.</summary>
    /// <param name="pick">The pick.</param>
    /// <returns>The time.</returns>
    private static DateTimeOffset OpenedOrOldest(EverythingPick pick) => pick.LastOpened ?? DateTimeOffset.MinValue;

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
    /// <param name="limit">
    /// Most hides kept (<see cref="Settings.ListLimit"/>: 0 or less = no limit, the default); the oldest go first, which can
    /// only bring an old pick back. The caller passes <see cref="Settings.AppSettings.MaxHiddenEverythingPicks"/>.
    /// </param>
    /// <returns>A new map with the hide, trimmed to the newest <paramref name="limit"/> when one is set.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static Dictionary<string, DateTimeOffset> Hide(IReadOnlyDictionary<string, DateTimeOffset> hidden, EverythingPick pick, DateTimeOffset now, int limit = 0)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        ArgumentNullException.ThrowIfNull(pick);
        var next = new Dictionary<string, DateTimeOffset>(hidden, StringComparer.OrdinalIgnoreCase)
        {
            // Its own opening time, not "now": an opening a moment later (even within the same millisecond as the
            // hide) then shows it again, and clock adjustments cannot hide later openings.
            [pick.FullPath] = pick.LastOpened ?? now,
        };
        return Trim(next, limit);
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
    /// <param name="limit">Most hides written, the newest (<see cref="Settings.ListLimit"/>: 0 or less = no limit, the default).</param>
    /// <returns>The state value, newest first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hidden"/> is <see langword="null"/>.</exception>
    public static string FormatHidden(IReadOnlyDictionary<string, DateTimeOffset> hidden, int limit = 0)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        var builder = new StringBuilder();
        foreach (var (path, at) in Trim(hidden, limit).OrderByDescending(p => p.Value))
        {
            builder.Append(at.UtcTicks.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(path).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// The hides under a limit: the newest <paramref name="limit"/> (a lowered setting applies at once,
    /// <c>AppController.Limits.cs</c>), or all of them when there is none.
    /// </summary>
    /// <param name="hidden">The hides (not changed).</param>
    /// <param name="limit">Most hides kept (<see cref="Settings.ListLimit"/>: 0 or less = no limit).</param>
    /// <returns>A trimmed copy, case-insensitive.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hidden"/> is <see langword="null"/>.</exception>
    public static Dictionary<string, DateTimeOffset> Trim(IReadOnlyDictionary<string, DateTimeOffset> hidden, int limit)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        return hidden.OrderByDescending(p => p.Value)
            .Take(Settings.ListLimit.Effective(limit))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>One row of the Everything tab: a stored entry or a live pick — exactly one of them.</summary>
/// <param name="Entry">A history entry from Everything, or <see langword="null"/> for a live pick.</param>
/// <param name="Pick">A live pick, or <see langword="null"/> for a stored entry.</param>
/// <param name="Time">
/// The sort time: when the entry was last used — or when Everything last opened its file, if that is newer (the card shows
/// it) — or when the pick was last opened; <see cref="DateTimeOffset.MinValue"/> for a pick without a date (sorts last).
/// </param>
public sealed record EverythingTabRow(ClipEntry? Entry, EverythingPick? Pick, DateTimeOffset Time);
