using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BetterClipboard.Core.Shells;

/// <summary>
/// The rules behind the panel's Cmd tab: telling new commands apart in a cmd window's history, and the list of cmd
/// commands BetterClipboard keeps (<see cref="KeptStateName"/>). Pure; the reading itself is in the Windows layer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a kept list.</b> <c>cmd.exe</c> writes no history file. Its console keeps the last commands per window
/// (50 by default, <c>HKCU\Console\HistoryBufferSize</c>) in memory and loses them when cmd exits (CLAUDE.md §2.16).
/// So BetterClipboard reads open windows' histories now and then and keeps what it saw, encrypted in its store —
/// that list is what survives a closed window. It is not part of the clipboard history: nothing in it counts against
/// the history's limits or shows outside the Cmd tab until the user pastes, copies or keeps a command.
/// </para>
/// <para>
/// <b>What a window's history does</b> (measured on conhost, Windows 11 26200): a command is appended when Enter is
/// pressed, failing ones included; the same command twice in a row is stored once; an older command typed again is
/// appended again (or, with <c>HistoryNoDup</c> on, moved to the end); when full, the oldest drops off the front.
/// <see cref="NewSince"/> reads two snapshots of one window with exactly those rules.
/// </para>
/// <para>
/// <b>How many.</b> As many as Settings › Cmd tab › <em>Maximum commands kept</em> allows
/// (<see cref="Settings.AppSettings.MaxKeptCmdCommands"/>, a <see cref="Settings.ListLimit"/>): no limit by default, the ones
/// seen longest ago going first under one. Until 2026-11-21 the newest 1,000 (a constant here, <c>MaxKept</c>).
/// </para>
/// <para>
/// <b>The reader's memory.</b> What each open window held at the last read, as fingerprints (<see cref="WindowsStateName"/>),
/// kept across restarts since 2026-11-21: a window open through a restart is compared with its last read
/// (<see cref="NewSinceRead"/>), so what <em>Delete stored…</em> or a lowered limit removed stays removed; only a window
/// truly seen for the first time is placed by its own order (<see cref="Remember"/>).
/// </para>
/// </remarks>
public static class CmdHistory
{
    /// <summary>Store state name of the kept commands (<see cref="FormatKept"/>).</summary>
    public const string KeptStateName = "cmd.kept";

    /// <summary>
    /// Store state name of the reader's memory (<see cref="FormatWindows"/>): what each open window held at the last read,
    /// as fingerprints. Kept across restarts (<c>CmdHistorySource</c> loads it at its first read) so a window open through
    /// one — the silent restart a few seconds after Settings closed, an update — is no window seen for the first time.
    /// Since 2026-11-21; before, the memory lived in the process alone, and every restart took an open window's commands
    /// for new ones: what <em>Delete stored…</em> or a lowered limit had removed came back (CLAUDE.md §2.62, found in the
    /// live check of the integrations' limits).
    /// </summary>
    public const string WindowsStateName = "cmd.windows";

    /// <summary>Longest command kept, in characters (a cmd line is limited to 8191 anyway).</summary>
    public const int MaxCommandLength = 8191;

    /// <summary>
    /// The commands appended to a window's history between two reads of it.
    /// </summary>
    /// <param name="previous">The earlier read, oldest first.</param>
    /// <param name="current">The later read, oldest first.</param>
    /// <returns>The new commands, oldest first; empty when nothing changed.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// The usual case is "the old list, minus what fell off the front, plus new commands at the end": the shortest
    /// eviction after which the rest of <paramref name="previous"/> starts <paramref name="current"/> wins. When no
    /// eviction explains it (an older command moved to the end under <c>HistoryNoDup</c>, or the history was cleared
    /// with Alt+F7), the commands not in <paramref name="previous"/> are new, plus a moved last command — never the
    /// whole list again, which would count every command as typed once more.
    /// </remarks>
    public static IReadOnlyList<string> NewSince(IReadOnlyList<string> previous, IReadOnlyList<string> current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        for (int evicted = 0; evicted <= previous.Count; evicted++)
        {
            int kept = previous.Count - evicted;
            if (kept > current.Count)
            {
                continue;
            }

            bool aligned = true;
            for (int i = 0; i < kept && aligned; i++)
            {
                aligned = string.Equals(previous[evicted + i], current[i], StringComparison.Ordinal);
            }

            if (aligned && (kept > 0 || previous.Count == 0))
            {
                return current.Skip(kept).ToList();
            }
        }

        // No eviction explains the change (see remarks).
        var before = previous.ToHashSet(StringComparer.Ordinal);
        var added = current.Where(c => !before.Contains(c)).ToList();
        if (current.Count > 0 && previous.Count > 0
            && !string.Equals(current[^1], previous[^1], StringComparison.Ordinal)
            && before.Contains(current[^1]))
        {
            // The last command was in the list before and is now at the end: it was typed again.
            added.Add(current[^1]);
        }

        return added;
    }

    /// <summary>
    /// A command's fingerprint in the reader's memory (<see cref="WindowsStateName"/>): the first 8 bytes of SHA-256 over
    /// its UTF-8, as lowercase hex. Exact like a window's own history (letter case and spaces count, as in
    /// <see cref="NewSince"/>), and no text: the memory is written also while capture is paused, like the Win+R list's
    /// snapshot (CLAUDE.md §2.17), and a paused command must not reach the store.
    /// </summary>
    /// <param name="command">A command as a window's history holds it.</param>
    /// <returns>16 lowercase hex characters.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    public static string Fingerprint(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        // 64 bits tell a window's 50 commands apart with room to spare; the memory stays small and holds no text.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(command));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    /// <summary>
    /// The commands typed in a window since a read the reader remembers as fingerprints (<see cref="Fingerprint"/>):
    /// <see cref="NewSince"/> over the fingerprints, given back as this read's texts.
    /// </summary>
    /// <param name="previousFingerprints">The earlier read as fingerprints, oldest first.</param>
    /// <param name="commands">The window's commands now, oldest first.</param>
    /// <returns>The new commands, oldest first; empty when nothing changed.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> NewSinceRead(IReadOnlyList<string> previousFingerprints, IReadOnlyList<string> commands)
    {
        ArgumentNullException.ThrowIfNull(previousFingerprints);
        ArgumentNullException.ThrowIfNull(commands);
        var fingerprints = new List<string>(commands.Count);
        var textOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            var fingerprint = Fingerprint(command);
            fingerprints.Add(fingerprint);
            textOf.TryAdd(fingerprint, command);
        }

        // NewSince only compares and slices, so it reads fingerprints as it reads texts; each one it returns is one of
        // this read's, whose text is at hand.
        return NewSince(previousFingerprints, fingerprints).Select(fingerprint => textOf[fingerprint]).ToList();
    }

    /// <summary>
    /// Adds commands to the kept list: new ones are added, ones kept already get a later time, and — when they were
    /// typed again (<paramref name="typedAgain"/>) — a higher count.
    /// </summary>
    /// <param name="kept">The kept list (not changed).</param>
    /// <param name="commands">One window's commands, oldest first.</param>
    /// <param name="typedAgain">
    /// <see langword="true"/> for commands just appended to a window's history (<see cref="NewSince"/>): each one is
    /// one more typing. <see langword="false"/> for a window seen for the first time, whose commands may have been
    /// counted before (BetterClipboard restarted, the window did not): known ones keep their count and time, and each one
    /// the list does not keep goes just below the next newer command of the window (see remarks).
    /// </param>
    /// <param name="now">When they were seen. Commands of one call are spaced a millisecond apart, oldest first, so
    /// the list keeps their order.</param>
    /// <param name="limit">
    /// Most commands kept (<see cref="Settings.ListLimit"/>: 0 or less = no limit, the default); the ones seen longest ago
    /// go first. The caller passes Settings' <see cref="Settings.AppSettings.MaxKeptCmdCommands"/>.
    /// </param>
    /// <returns>The new kept list, newest first, at most <paramref name="limit"/> commands when one is set.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// <b>A window seen for the first time</b> (<paramref name="typedAgain"/> <see langword="false"/>: every start, the
    /// silent restart after Settings closed included, and the Cmd tab's view of open windows) holds commands the list does
    /// not keep for two reasons: typed while BetterClipboard did not read the window, or dropped from the list (a limit,
    /// <em>Delete stored…</em>). Which, nothing tells, but the window's own order does: a command older in the window than
    /// one the list keeps was there before it. So, walking from the window's newest command back, each command the list
    /// does not keep gets a time a millisecond below the next newer command of the window — <paramref name="now"/> for the
    /// newest, the kept time of a kept one. A window that only adds keeps the old spacing (newest at
    /// <paramref name="now"/>); one whose older commands a limit dropped puts them back below its kept newest, where the
    /// limit drops them again. Until 2026-11-21 every such command got a time near <paramref name="now"/>, so after a
    /// restart under a limit of 2 a window holding six commands replaced its two newest with its third and fourth (found
    /// in the live check of the integrations' limits, CLAUDE.md §2.62), and after <em>Delete stored…</em> the window's old
    /// commands were listed above the ones typed since. A window whose newest command was kept long ago (typed then in
    /// another window) puts its other commands below that old time: the order holds, the guess of their time is early.
    /// </remarks>
    public static IReadOnlyList<KeptCommand> Remember(IReadOnlyList<KeptCommand> kept, IReadOnlyList<string> commands, bool typedAgain, DateTimeOffset now, int limit = 0)
    {
        ArgumentNullException.ThrowIfNull(kept);
        ArgumentNullException.ThrowIfNull(commands);
        var byText = new Dictionary<string, KeptCommand>(StringComparer.Ordinal);
        foreach (var command in kept)
        {
            byText.TryAdd(command.Text, command);
        }

        if (typedAgain)
        {
            // Every command here was typed since the last read, oldest first: each is one more typing, newest at now.
            for (int i = 0; i < commands.Count; i++)
            {
                var text = commands[i];
                if (!IsKeepable(text))
                {
                    continue;
                }

                var at = now.AddMilliseconds(i - (commands.Count - 1));
                if (byText.TryGetValue(text, out var known))
                {
                    byText[text] = known with { Count = known.Count + 1, LastSeen = at > known.LastSeen ? at : known.LastSeen };
                }
                else
                {
                    byText[text] = new KeptCommand(text, 1, at);
                }
            }
        }
        else
        {
            // A window seen for the first time (see remarks): from its newest command back, each one not kept goes a
            // millisecond below the next newer one, so the window's order holds against what the list already keeps.
            var below = now.AddMilliseconds(1);
            for (int i = commands.Count - 1; i >= 0; i--)
            {
                var text = commands[i];
                if (!IsKeepable(text))
                {
                    continue;
                }

                if (byText.TryGetValue(text, out var known))
                {
                    // Kept already: its count and time stand, and the window's older commands go below it.
                    if (known.LastSeen < below)
                    {
                        below = known.LastSeen;
                    }

                    continue;
                }

                below = below.AddMilliseconds(-1);
                byText[text] = new KeptCommand(text, 1, below);
            }
        }

        return byText.Values.OrderByDescending(c => c.LastSeen).Take(Settings.ListLimit.Effective(limit)).ToList();
    }

    /// <summary>
    /// The kept list under a limit: its newest <paramref name="limit"/> commands (a lowered limit applies at once,
    /// <c>CmdHistorySource.ApplyLimitAsync</c>), or all of them when there is none.
    /// </summary>
    /// <param name="kept">The kept commands, any order (not changed).</param>
    /// <param name="limit">Most commands kept (<see cref="Settings.ListLimit"/>: 0 or less = no limit).</param>
    /// <returns>The commands kept, newest first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="kept"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<KeptCommand> Trim(IReadOnlyList<KeptCommand> kept, int limit)
    {
        ArgumentNullException.ThrowIfNull(kept);
        return kept.OrderByDescending(c => c.LastSeen).Take(Settings.ListLimit.Effective(limit)).ToList();
    }

    /// <summary>
    /// Reads the kept list: one command per line, <c>UTC ticks</c>, a tab, the count, a tab, the command (which may
    /// itself contain tabs: it is the rest of the line). Malformed lines are skipped — a bad line loses one command.
    /// </summary>
    /// <param name="value">The state value; <see langword="null"/> or empty when nothing is kept.</param>
    /// <returns>The kept commands, newest first, one per text.</returns>
    public static IReadOnlyList<KeptCommand> ParseKept(string? value)
    {
        var result = new List<KeptCommand>();
        if (string.IsNullOrEmpty(value))
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in value.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t', 3);
            if (parts.Length != 3
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1
                || !IsKeepable(parts[2]) || !seen.Add(parts[2]))
            {
                continue;
            }

            result.Add(new KeptCommand(parts[2], count, new DateTimeOffset(ticks, TimeSpan.Zero)));
        }

        return result.OrderByDescending(c => c.LastSeen).ToList();
    }

    /// <summary>Writes the kept list for <see cref="ParseKept"/>, newest first, at most <paramref name="limit"/> commands when one is set.</summary>
    /// <param name="kept">The commands.</param>
    /// <param name="limit">Most commands written (<see cref="Settings.ListLimit"/>: 0 or less = no limit, the default).</param>
    /// <returns>The state value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="kept"/> is <see langword="null"/>.</exception>
    public static string FormatKept(IEnumerable<KeptCommand> kept, int limit = 0)
    {
        ArgumentNullException.ThrowIfNull(kept);
        var builder = new StringBuilder();
        foreach (var command in kept.Where(c => IsKeepable(c.Text)).OrderByDescending(c => c.LastSeen).Take(Settings.ListLimit.Effective(limit)))
        {
            builder.Append(command.LastSeen.UtcTicks.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(command.Count.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(command.Text).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Writes the reader's memory for <see cref="ParseWindows"/>: one line per window, its process id, a tab, its start time
    /// (UTC ticks: process ids are reused), a tab, then its commands' fingerprints (<see cref="Fingerprint"/>) oldest first,
    /// separated by commas. Windows in process-id order, so the same memory always gives the same text (the reader writes
    /// it only when it changed).
    /// </summary>
    /// <param name="windows">Each open window's last read as fingerprints, by process id and start time.</param>
    /// <returns>The state value; empty when no window is open.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="windows"/> is <see langword="null"/>.</exception>
    public static string FormatWindows(IEnumerable<KeyValuePair<(uint ProcessId, long StartTicks), IReadOnlyList<string>>> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var builder = new StringBuilder();
        foreach (var (window, fingerprints) in windows.OrderBy(w => w.Key.ProcessId).ThenBy(w => w.Key.StartTicks))
        {
            builder.Append(window.ProcessId.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(window.StartTicks.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .AppendJoin(',', fingerprints).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reads the reader's memory (<see cref="FormatWindows"/>). A malformed line — a field that is not a number, a
    /// fingerprint that is not 16 lowercase hex characters, a window given twice — is skipped: that window is then read as
    /// one seen for the first time, whose commands <see cref="Remember"/> places by its own order.
    /// </summary>
    /// <param name="value">The state value; <see langword="null"/> or empty when nothing is remembered.</param>
    /// <returns>Each remembered window's fingerprints, oldest first, by process id and start time.</returns>
    public static IReadOnlyDictionary<(uint ProcessId, long StartTicks), IReadOnlyList<string>> ParseWindows(string? value)
    {
        var result = new Dictionary<(uint ProcessId, long StartTicks), IReadOnlyList<string>>();
        if (string.IsNullOrEmpty(value))
        {
            return result;
        }

        foreach (var line in value.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length != 3
                || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var startTicks))
            {
                continue;
            }

            var fingerprints = parts[2].Length == 0 ? [] : parts[2].Split(',');
            if (fingerprints.All(IsFingerprint))
            {
                result.TryAdd((processId, startTicks), fingerprints);
            }
        }

        return result;
    }

    /// <summary>
    /// Whether a command can be kept: not blank, one line (a cmd history entry never has a line break; one would
    /// break the line format), and not longer than <see cref="MaxCommandLength"/>.
    /// </summary>
    /// <param name="text">The command.</param>
    /// <returns><see langword="true"/> when it can be kept.</returns>
    public static bool IsKeepable(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= MaxCommandLength && text.AsSpan().IndexOfAny('\r', '\n') < 0;

    /// <summary>Whether a text is a fingerprint as <see cref="Fingerprint"/> writes it: 16 lowercase hex characters.</summary>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> for a fingerprint.</returns>
    private static bool IsFingerprint(string text) =>
        text.Length == 16 && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>A cmd command BetterClipboard keeps: its text, how often it was seen typed, and when last.</summary>
/// <param name="Text">The command, one line.</param>
/// <param name="Count">How often it was seen added to a window's history (at least 1).</param>
/// <param name="LastSeen">When it was last seen added (or first seen, for a window read for the first time).</param>
public sealed record KeptCommand(string Text, int Count, DateTimeOffset LastSeen);
