using System.Globalization;
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
/// </remarks>
public static class CmdHistory
{
    /// <summary>Store state name of the kept commands (<see cref="FormatKept"/>).</summary>
    public const string KeptStateName = "cmd.kept";

    /// <summary>Most commands kept; the ones seen longest ago go first.</summary>
    public const int MaxKept = 1000;

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
    /// Adds commands to the kept list: new ones are added, ones kept already get a later time, and — when they were
    /// typed again (<paramref name="typedAgain"/>) — a higher count.
    /// </summary>
    /// <param name="kept">The kept list (not changed).</param>
    /// <param name="commands">The commands, oldest first.</param>
    /// <param name="typedAgain">
    /// <see langword="true"/> for commands just appended to a window's history (<see cref="NewSince"/>): each one is
    /// one more typing. <see langword="false"/> for a window seen for the first time, whose commands may have been
    /// counted before (BetterClipboard restarted, the window did not): known ones keep their count and time.
    /// </param>
    /// <param name="now">When they were seen. Commands of one call are spaced a millisecond apart, oldest first, so
    /// the list keeps their order.</param>
    /// <returns>The new kept list, newest first, at most <see cref="MaxKept"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IReadOnlyList<KeptCommand> Remember(IReadOnlyList<KeptCommand> kept, IReadOnlyList<string> commands, bool typedAgain, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(kept);
        ArgumentNullException.ThrowIfNull(commands);
        var byText = new Dictionary<string, KeptCommand>(StringComparer.Ordinal);
        foreach (var command in kept)
        {
            byText.TryAdd(command.Text, command);
        }

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
                if (typedAgain)
                {
                    byText[text] = known with { Count = known.Count + 1, LastSeen = at > known.LastSeen ? at : known.LastSeen };
                }
            }
            else
            {
                byText[text] = new KeptCommand(text, 1, at);
            }
        }

        return byText.Values.OrderByDescending(c => c.LastSeen).Take(MaxKept).ToList();
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

    /// <summary>Writes the kept list for <see cref="ParseKept"/>, newest first, at most <see cref="MaxKept"/>.</summary>
    /// <param name="kept">The commands.</param>
    /// <returns>The state value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="kept"/> is <see langword="null"/>.</exception>
    public static string FormatKept(IEnumerable<KeptCommand> kept)
    {
        ArgumentNullException.ThrowIfNull(kept);
        var builder = new StringBuilder();
        foreach (var command in kept.Where(c => IsKeepable(c.Text)).OrderByDescending(c => c.LastSeen).Take(MaxKept))
        {
            builder.Append(command.LastSeen.UtcTicks.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(command.Count.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(command.Text).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Whether a command can be kept: not blank, one line (a cmd history entry never has a line break; one would
    /// break the line format), and not longer than <see cref="MaxCommandLength"/>.
    /// </summary>
    /// <param name="text">The command.</param>
    /// <returns><see langword="true"/> when it can be kept.</returns>
    public static bool IsKeepable(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= MaxCommandLength && text.AsSpan().IndexOfAny('\r', '\n') < 0;
}

/// <summary>A cmd command BetterClipboard keeps: its text, how often it was seen typed, and when last.</summary>
/// <param name="Text">The command, one line.</param>
/// <param name="Count">How often it was seen added to a window's history (at least 1).</param>
/// <param name="LastSeen">When it was last seen added (or first seen, for a window read for the first time).</param>
public sealed record KeptCommand(string Text, int Count, DateTimeOffset LastSeen);
