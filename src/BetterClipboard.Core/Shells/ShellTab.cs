using System.Globalization;
using System.Text;
using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.Shells;

/// <summary>
/// The rules of the panel's shell tabs (Pwsh, Cmd): which rows they show, in which order, and the user's "hide"
/// choices. Each tab merges two sources: history entries kept from it (<see cref="Storage.ClipFilter.PowerShell"/>,
/// <see cref="Storage.ClipFilter.Cmd"/>) and the commands the shell remembers, read live (<see cref="ShellCommand"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A live view, stored only on action</b> — the Everything tab's design (CLAUDE.md §2.14). PowerShell's history
/// file already keeps every command forever, and it holds thousands: importing them would bury the clipboard
/// history under them and spend its item limit. So a command becomes a history entry only when the user pastes,
/// copies, pins or groups it.
/// </para>
/// <para>
/// <b>Merging.</b> Stored entries first (the commands the user actually used or kept), pinned ones on top when the
/// "pinned on top" setting asks for it, then most recently used; then the live commands in the source's order
/// (newest first). A live command that is also stored (same content hash) shows once, as the entry, which carries
/// pins and groups. Forgotten commands never show; hidden ones neither.
/// </para>
/// <para>
/// <b>Hiding.</b> Delete on a live command cannot delete anything from the shell (BetterClipboard only reads), so it
/// hides the command until it is typed again: the hide remembers <see cref="ShellCommand.Count"/>, and a higher count
/// shows it again. Kept in the encrypted store (<see cref="HiddenStateName"/>), newest <see cref="MaxHidden"/> first.
/// </para>
/// </remarks>
public static class ShellTab
{
    /// <summary>Most live commands a tab shows (newest first, after the search narrowed them; no paging).</summary>
    public const int MaxCommands = 500;

    /// <summary>Most stored entries a tab loads.</summary>
    public const int MaxStoredEntries = 200;

    /// <summary>Most hides kept per shell; the oldest go first, which can only bring an old command back.</summary>
    public const int MaxHidden = 1000;

    /// <summary>The store state name of a shell's hidden commands (<see cref="FormatHidden"/>).</summary>
    /// <param name="shell">The shell.</param>
    /// <returns><c>pwsh.hidden</c> or <c>cmd.hidden</c>.</returns>
    public static string HiddenStateName(ShellKind shell) => shell == ShellKind.PowerShell ? "pwsh.hidden" : "cmd.hidden";

    /// <summary>
    /// Merges stored entries and live commands into a tab's rows (see class remarks).
    /// </summary>
    /// <param name="stored">The tab's stored entries, any order.</param>
    /// <param name="commands">Live commands, newest first; a text listed twice shows once.</param>
    /// <param name="hidden">Hidden commands (<see cref="ParseHidden"/>).</param>
    /// <param name="forgottenFingerprints">The commands' <see cref="ShellCommand.Fingerprint"/>s that are on the "Forget forever" list.</param>
    /// <param name="pinnedFirst">Put pinned entries on top (the "pinned on top" setting).</param>
    /// <returns>The rows in display order.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IReadOnlyList<ShellTabRow> Merge(
        IReadOnlyList<ClipEntry> stored,
        IReadOnlyList<ShellCommand> commands,
        IReadOnlyList<HiddenCommand> hidden,
        IReadOnlySet<string> forgottenFingerprints,
        bool pinnedFirst)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(hidden);
        ArgumentNullException.ThrowIfNull(forgottenFingerprints);

        var rows = new List<ShellTabRow>(stored.Count + commands.Count);
        rows.AddRange(stored
            .OrderByDescending(e => pinnedFirst && e.IsPinned)
            .ThenByDescending(e => e.LastUsedUtc)
            .Select(e => new ShellTabRow(e, null)));

        var storedHashes = stored.Select(e => e.ContentHash).ToHashSet(StringComparer.Ordinal);
        var hiddenCounts = ToLookup(hidden);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            if (!listed.Add(command.ContentHash)
                || storedHashes.Contains(command.ContentHash)
                || forgottenFingerprints.Contains(command.Fingerprint)
                || IsHidden(command, hiddenCounts))
            {
                continue;
            }

            rows.Add(new ShellTabRow(null, command));
        }

        return rows;
    }

    /// <summary>Whether a command is hidden: hidden at its current count or a higher one (not typed since).</summary>
    /// <param name="command">The command.</param>
    /// <param name="hidden">Hidden counts by content hash (<see cref="ToLookup"/>).</param>
    /// <returns><see langword="true"/> when the tab must not show it.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static bool IsHidden(ShellCommand command, IReadOnlyDictionary<string, int> hidden)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(hidden);
        return hidden.TryGetValue(command.ContentHash, out var count) && command.Count <= count;
    }

    /// <summary>
    /// Records a hide: the command stays hidden until it is typed again (its count grows past the current one).
    /// </summary>
    /// <param name="hidden">The current hides, newest first (not changed).</param>
    /// <param name="contentHash">The command's <see cref="ShellCommand.ContentHash"/>.</param>
    /// <param name="count">Its current <see cref="ShellCommand.Count"/>.</param>
    /// <returns>The new hides, newest first (this one), at most <see cref="MaxHidden"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hidden"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="contentHash"/> is null or blank.</exception>
    public static IReadOnlyList<HiddenCommand> Hide(IReadOnlyList<HiddenCommand> hidden, string contentHash, int count)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        var next = new List<HiddenCommand>(Math.Min(hidden.Count + 1, MaxHidden)) { new(contentHash, Math.Max(1, count)) };
        next.AddRange(hidden.Where(h => !string.Equals(h.ContentHash, contentHash, StringComparison.Ordinal)).Take(MaxHidden - 1));
        return next;
    }

    /// <summary>The hides as a lookup by content hash (the first, i.e. newest, wins for a hash listed twice).</summary>
    /// <param name="hidden">The hides, newest first.</param>
    /// <returns>Count by content hash.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hidden"/> is <see langword="null"/>.</exception>
    public static IReadOnlyDictionary<string, int> ToLookup(IReadOnlyList<HiddenCommand> hidden)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        var lookup = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var h in hidden)
        {
            lookup.TryAdd(h.ContentHash, h.Count);
        }

        return lookup;
    }

    /// <summary>
    /// Reads the stored hides: one per line, the count, a tab, the content hash; newest first. Malformed lines are
    /// skipped — a bad line can only un-hide one command. No command text is ever stored here, only its hash.
    /// </summary>
    /// <param name="value">The state value; <see langword="null"/> or empty when nothing is hidden.</param>
    /// <returns>The hides, newest first.</returns>
    public static IReadOnlyList<HiddenCommand> ParseHidden(string? value)
    {
        var result = new List<HiddenCommand>();
        if (string.IsNullOrEmpty(value))
        {
            return result;
        }

        foreach (var line in value.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int tab = line.IndexOf('\t', StringComparison.Ordinal);
            var hash = tab < 0 ? string.Empty : line[(tab + 1)..].TrimEnd('\r');
            if (tab <= 0 || hash.Length == 0
                || !int.TryParse(line.AsSpan(0, tab), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1)
            {
                continue;
            }

            result.Add(new HiddenCommand(hash, count));
        }

        return result.Take(MaxHidden).ToList();
    }

    /// <summary>Writes hides for <see cref="ParseHidden"/>, in the given order (newest first), at most <see cref="MaxHidden"/>.</summary>
    /// <param name="hidden">The hides.</param>
    /// <returns>The state value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hidden"/> is <see langword="null"/>.</exception>
    public static string FormatHidden(IReadOnlyList<HiddenCommand> hidden)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        var builder = new StringBuilder();
        foreach (var h in hidden.Take(MaxHidden))
        {
            builder.Append(h.Count.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(h.ContentHash).Append('\n');
        }

        return builder.ToString();
    }
}

/// <summary>One row of a shell tab: a stored entry or a live command — exactly one of them.</summary>
/// <param name="Entry">A history entry kept from the tab, or <see langword="null"/> for a live command.</param>
/// <param name="Command">A live command, or <see langword="null"/> for a stored entry.</param>
public sealed record ShellTabRow(ClipEntry? Entry, ShellCommand? Command);

/// <summary>A hidden command: its content hash and the count it had when hidden.</summary>
/// <param name="ContentHash">The command's <see cref="ShellCommand.ContentHash"/>.</param>
/// <param name="Count">Its <see cref="ShellCommand.Count"/> at the hide; a higher count shows it again.</param>
public sealed record HiddenCommand(string ContentHash, int Count);
