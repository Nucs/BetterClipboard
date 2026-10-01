using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.Integrations;

/// <summary>
/// Pure logic over Windows' Win+R history (the Run dialog's "RunMRU" list): reading the list, telling which
/// commands were run since an earlier look, the snapshot kept between looks, and the captures that store a
/// command in the history. The registry access and the change watch live in the Windows layer.
/// </summary>
/// <remarks>
/// <para>
/// <b>The list</b> (<see cref="DefaultKeyPath"/>): up to <see cref="Capacity"/> values named <c>a</c>–<c>z</c>, each
/// the command as typed plus <c>\1</c> (a backslash and the show-command digit), and <c>MRUList</c>, their letters
/// most recent first. Only commands that ran successfully get in; a re-run moves its letter to the front instead
/// of adding a duplicate (compared case-insensitively); when the list is full, the least recent letter is
/// overwritten — that is how Windows forgets. Entries carry no time: only the key's last write says when the
/// newest one ran. Facts and how they were verified: CLAUDE.md §2.15.
/// </para>
/// <para>
/// <b>Runs since a snapshot.</b> Nothing reorders the list except a run, which moves (or adds) one command to the
/// front; evictions only remove from the end. So the commands run since an earlier list are the shortest prefix
/// of the current list after which the rest keeps the earlier relative order (<see cref="RunsSince"/>). Deliberate
/// limits: re-running the command that already was the newest changes nothing visible, and neither do runs that
/// restore an earlier order (run b, then a, on a list a, b) — those runs are not recorded twice.
/// </para>
/// <para>
/// <b>Snapshot.</b> Kept in the encrypted history between looks (<see cref="FormatSnapshot"/>). It holds
/// fingerprints only — a truncated SHA-256 of the case-folded command — never a command's text.
/// </para>
/// </remarks>
public static partial class RunMru
{
    /// <summary>Where Windows keeps the Win+R history, under <c>HKEY_CURRENT_USER</c>.</summary>
    public const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU";

    /// <summary>
    /// How many commands Windows keeps (comctl32's MRU list, sized 26 by the Run dialog): a full list evicts its
    /// least recent command with every new one.
    /// </summary>
    public const int Capacity = 26;

    /// <summary>
    /// The source recorded for Win+R commands: no process (the dialog runs inside Explorer, which must not be
    /// blamed for them), and the name "Win+R" on the cards. Adding "Win+R" to Ignored apps skips them like the
    /// Settings switch does.
    /// </summary>
    public static readonly SourceAppInfo Source = new("Win+R", null, "Win+R");

    /// <summary>Version tag of the stored snapshot; a value without it is "no snapshot" (first activation).</summary>
    private const string SnapshotPrefix = "v1:";

    /// <summary>
    /// Reads the list into commands, most recent first, the way the Run dialog's drop-down shows them.
    /// </summary>
    /// <remarks>
    /// Defensive, because the key is user-editable: letters of <paramref name="mruList"/> without a value, blank
    /// values and characters other than <c>a</c>–<c>z</c> are skipped, a command listed twice (only possible by
    /// hand) counts once, and values not named in <c>MRUList</c> are ignored like the dialog ignores them.
    /// </remarks>
    /// <param name="mruList">The <c>MRUList</c> value (letters, most recent first); <see langword="null"/> = empty list.</param>
    /// <param name="values">The letter values by name (compare names case-insensitively when building it).</param>
    /// <returns>The commands without their show-command suffix and surrounding whitespace, most recent first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> Parse(string? mruList, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var commands = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (char letter in mruList ?? string.Empty)
        {
            if (letter is < 'a' or > 'z' || !values.TryGetValue(letter.ToString(), out var raw))
            {
                continue;
            }

            var command = StripShowCommand(raw).Trim();
            if (command.Length > 0 && seen.Add(command))
            {
                commands.Add(command);
            }
        }

        return commands;
    }

    /// <summary>
    /// Removes the show-command suffix the Run dialog appends to every value (<c>cmd\1</c> → <c>cmd</c>): a
    /// backslash and one or two digits at the very end.
    /// </summary>
    /// <remarks>
    /// Footgun: a value written without the suffix (by hand, or by a much older Windows) that ends in a folder
    /// named with one or two digits (<c>C:\data\12</c>) loses that folder name. Every value Windows 10 and 11
    /// write carries the suffix, and nothing else can tell the two apart.
    /// </remarks>
    /// <param name="value">A raw value.</param>
    /// <returns>The command as typed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static string StripShowCommand(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ShowCommandSuffix().Replace(value, string.Empty);
    }

    /// <summary>
    /// The snapshot fingerprint of a command: the first 8 bytes of SHA-256 over the upper-cased command, as hex.
    /// Case-insensitive like the Run dialog's own comparison, so a command typed again in other letter case is
    /// the same entry.
    /// </summary>
    /// <param name="command">A command as <see cref="Parse"/> returns it.</param>
    /// <returns>16 lowercase hex characters.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    public static string Fingerprint(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        // 64 bits are plenty to tell 26 entries apart; the snapshot stays small and holds no text.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(command.ToUpperInvariant()));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    /// <summary>
    /// The commands run since an earlier look at the list, most recent first (see the class remarks).
    /// </summary>
    /// <param name="previousFingerprints">The earlier list as fingerprints (<see cref="TryParseSnapshot"/>), most recent first.</param>
    /// <param name="currentCommands">The list now (<see cref="Parse"/>), most recent first.</param>
    /// <returns>A prefix of <paramref name="currentCommands"/>: empty when nothing visible changed (also when Windows' list was cleared).</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> RunsSince(IReadOnlyList<string> previousFingerprints, IReadOnlyList<string> currentCommands)
    {
        ArgumentNullException.ThrowIfNull(previousFingerprints);
        ArgumentNullException.ThrowIfNull(currentCommands);
        var current = currentCommands.Select(Fingerprint).ToArray();

        // The shortest prefix that explains the change wins: everything after it must be the earlier list in its
        // earlier order, minus the commands moved to the front, with gaps where entries were evicted or deleted
        // by hand. k == current.Length always qualifies (an empty rest), so the loop always returns.
        for (int k = 0; ; k++)
        {
            var moved = new HashSet<string>(current.Take(k), StringComparer.Ordinal);
            var rest = previousFingerprints.Where(fingerprint => !moved.Contains(fingerprint)).ToList();
            if (IsSubsequence(current.AsSpan(k), rest))
            {
                return currentCommands.Take(k).ToArray();
            }
        }
    }

    /// <summary>
    /// The snapshot to store after a look: the version tag and the fingerprints of the list, most recent first.
    /// </summary>
    /// <param name="commands">The list as <see cref="Parse"/> returned it (may be empty: an empty snapshot is not "none").</param>
    /// <returns>E.g. <c>v1:0a1b…,9f8e…</c>; <c>v1:</c> for an empty list.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="commands"/> is <see langword="null"/>.</exception>
    public static string FormatSnapshot(IEnumerable<string> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        return SnapshotPrefix + string.Join(",", commands.Select(Fingerprint));
    }

    /// <summary>
    /// Reads a stored snapshot.
    /// </summary>
    /// <param name="raw">The stored value; <see langword="null"/>, empty (switched off), or anything without the version tag = none.</param>
    /// <param name="fingerprints">The fingerprints, most recent first (possibly empty), or empty when there is none.</param>
    /// <returns>Whether a snapshot exists — <see langword="false"/> means "first look": import what Windows remembers.</returns>
    public static bool TryParseSnapshot(string? raw, out IReadOnlyList<string> fingerprints)
    {
        fingerprints = [];
        if (raw is null || !raw.StartsWith(SnapshotPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        fingerprints = raw[SnapshotPrefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries);
        return true;
    }

    /// <summary>
    /// Decides what one look at the list stores: on the first look (no snapshot) everything Windows still
    /// remembers, as an import; afterwards only the commands run since the snapshot, as new runs.
    /// </summary>
    /// <remarks>
    /// Times: Windows keeps one — the key's last write, when the newest command ran. The first look spreads the
    /// remembered commands one second apart below it (their real times are unknown; the order is kept), and runs
    /// found later get it minus a millisecond each (when the watch reports a run at once, that is its real time;
    /// runs found by a later rescan really happened at or before it). The captures come oldest first, so storing
    /// them in order leaves the newest on top.
    /// </remarks>
    /// <param name="previousFingerprints">The stored snapshot, or <see langword="null"/> when there is none (first look).</param>
    /// <param name="commands">The list now (<see cref="Parse"/>), most recent first.</param>
    /// <param name="newestRunUtc">When the newest command ran: the key's last-write time (or now when it is unknown).</param>
    /// <returns>The captures to store, oldest first; empty when there is nothing new.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="commands"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<ClipCapture> PlanCaptures(IReadOnlyList<string>? previousFingerprints, IReadOnlyList<string> commands, DateTimeOffset newestRunUtc)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (previousFingerprints is null)
        {
            return commands
                .Select((command, index) => CreateCapture(command, newestRunUtc - TimeSpan.FromSeconds(index), observedRun: false))
                .Reverse()
                .ToArray();
        }

        return RunsSince(previousFingerprints, commands)
            .Select((command, index) => CreateCapture(command, newestRunUtc - TimeSpan.FromMilliseconds(index), observedRun: true))
            .Reverse()
            .ToArray();
    }

    /// <summary>
    /// Builds the capture that stores one Win+R command: its text as <c>CF_UNICODETEXT</c> (what the Run dialog
    /// has — no formatting), <see cref="Source"/>, and the origin that decides the merge rules.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="ranAtUtc">When it ran (becomes its history time and its run time).</param>
    /// <param name="observedRun">
    /// <see langword="true"/> for a run seen since the last look or run again from the panel
    /// (<see cref="ClipOrigin.RunDialog"/>: a new event); <see langword="false"/> for a command Windows already
    /// remembered on the first look (<see cref="ClipOrigin.RunDialogHistory"/>: an import).
    /// </param>
    /// <returns>The capture.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is null or blank.</exception>
    public static ClipCapture CreateCapture(string command, DateTimeOffset ranAtUtc, bool observedRun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return new ClipCapture
        {
            Formats = [new ClipFormatData(ClipFormatNames.UnicodeText, UnicodeTextCodec.Encode(command))],
            CapturedAtUtc = ranAtUtc,
            Source = Source,
            Origin = observedRun ? ClipOrigin.RunDialog : ClipOrigin.RunDialogHistory,
        };
    }

    /// <summary>Whether <paramref name="items"/> appear in <paramref name="sequence"/> in the same order (gaps allowed).</summary>
    /// <param name="items">The candidate subsequence.</param>
    /// <param name="sequence">The sequence.</param>
    /// <returns><see langword="true"/> when every item is found after the previous one.</returns>
    private static bool IsSubsequence(ReadOnlySpan<string> items, List<string> sequence)
    {
        int position = 0;
        foreach (var item in items)
        {
            while (position < sequence.Count && !string.Equals(sequence[position], item, StringComparison.Ordinal))
            {
                position++;
            }

            if (position == sequence.Count)
            {
                return false;
            }

            position++;
        }

        return true;
    }

    /// <summary>The Run dialog's show-command suffix: a backslash and one or two digits at the end.</summary>
    /// <returns>The compiled expression.</returns>
    [GeneratedRegex(@"\\\d{1,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShowCommandSuffix();
}
