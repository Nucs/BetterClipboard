using System.Text;
using BetterClipboard.Windows.Input;

namespace BetterClipboard.Windows.Shell;

/// <summary>What the "Release from Explorer" button would do next.</summary>
public enum ExplorerReleaseAction
{
    /// <summary>Nothing to do: no shortcut is a Win+letter one, and Win+V is with Explorer.</summary>
    None,

    /// <summary>Tell Explorer to stop registering <see cref="ExplorerReleasePlan.Keys"/> (add them to <c>DisabledHotkeys</c>).</summary>
    Release,

    /// <summary>Let Explorer register <see cref="ExplorerReleasePlan.Keys"/> again (remove them from <c>DisabledHotkeys</c>).</summary>
    GiveBack,
}

/// <summary>
/// The next step for Settings' "Release from Explorer" card, worked out from the shortcuts and Explorer's
/// <c>DisabledHotkeys</c> value.
/// </summary>
/// <param name="Action">What the button does.</param>
/// <param name="Keys">The letters or digits it adds or removes (upper case), in the shortcuts' order.</param>
/// <param name="Status">One or two sentences for the card: what is released now, and anything that does nothing.</param>
public sealed record ExplorerReleasePlan(ExplorerReleaseAction Action, IReadOnlyList<char> Keys, string Status)
{
    /// <summary>The keys as shortcut names, e.g. "Win+V and Win+C", for buttons and confirmations.</summary>
    public string KeyNames => ExplorerHotkeys.Names(Keys);
}

/// <summary>
/// Explorer's <c>DisabledHotkeys</c> list as BetterClipboard uses it: one character per Win+&lt;key&gt; shortcut Explorer
/// must not register, which frees that shortcut for a plain <c>RegisterHotKey</c> (verified for Win+V, Win+E, Win+R and
/// Win+1, CLAUDE.md §1.5). Pure string logic here; <see cref="WindowsClipboardSettings"/> reads and writes the value.
/// </summary>
/// <remarks>
/// <para>
/// Only shortcuts that are exactly Win plus a letter or digit can be released this way: the value names a character, not
/// a modifier set, and measured on 26200 the <c>V</c> freed Win+V alone — Win+Ctrl+V (Windows' sound output flyout) stayed
/// registered (probe of 2026-10-03). And only the ones Explorer registers itself: on the same build Win+C (Copilot) stayed
/// taken with <c>C</c> listed, held by another part of Windows. Everything still taken is taken over by the keyboard hook.
/// </para>
/// <para>
/// Other characters already in the value belong to the user or to other tools and are always kept. Of the keys
/// BetterClipboard did not put there itself, only <c>V</c> is treated as its own: releasing Win+V is what every
/// BetterClipboard version and its installer did, so a released Win+V that is no longer a BetterClipboard shortcut is a
/// key that does nothing, and is offered back to Explorer. A user's own released letter is never offered back.
/// </para>
/// <para>Changes take effect when Explorer restarts (<see cref="WindowsClipboardSettings.RestartExplorer"/>) or at the next sign-in.</para>
/// </remarks>
public static class ExplorerHotkeys
{
    /// <summary>
    /// The character Explorer's <c>DisabledHotkeys</c> would need to free <paramref name="gesture"/>: its letter or digit when
    /// the gesture is exactly Win plus that key.
    /// </summary>
    /// <param name="gesture">The shortcut.</param>
    /// <returns>The upper-case letter or digit, or <see langword="null"/> when Explorer's list cannot release this shortcut.</returns>
    public static char? ReleasableKey(HotkeyGesture gesture) =>
        gesture.Modifiers == HotkeyModifiers.Win && gesture.VirtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9'
            ? (char)gesture.VirtualKey
            : null;

    /// <summary>
    /// The distinct releasable keys of <paramref name="gestures"/>, in order (see <see cref="ReleasableKey"/>).
    /// </summary>
    /// <param name="gestures">The shortcuts.</param>
    /// <returns>Upper-case letters and digits, each once.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="gestures"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<char> ReleasableKeys(IEnumerable<HotkeyGesture> gestures)
    {
        ArgumentNullException.ThrowIfNull(gestures);
        return gestures.Select(ReleasableKey).OfType<char>().Distinct().ToArray();
    }

    /// <summary>Whether <paramref name="disabledHotkeys"/> releases Win+<paramref name="key"/> (case-insensitive, like Explorer).</summary>
    /// <param name="disabledHotkeys">The value, or <see langword="null"/> when absent.</param>
    /// <param name="key">A letter or digit.</param>
    /// <returns><see langword="true"/> when the key is listed.</returns>
    public static bool IsReleased(string? disabledHotkeys, char key) =>
        (disabledHotkeys ?? string.Empty).Any(c => char.ToUpperInvariant(c) == char.ToUpperInvariant(key));

    /// <summary>
    /// The value with <paramref name="keys"/> added (<paramref name="release"/>) or removed, keeping every other character
    /// and its order: the user's own entries must survive.
    /// </summary>
    /// <remarks>
    /// Adding leaves a key that is already listed (in either case) where it is, so releasing what is released returns the
    /// value unchanged — callers compare the result with the old value to decide whether Explorer must restart, and a
    /// reshuffled but equivalent value would restart it for nothing. Removing drops every spelling of the key.
    /// </remarks>
    /// <param name="disabledHotkeys">The current value, or <see langword="null"/> when absent.</param>
    /// <param name="keys">Letters or digits to add or remove (any case).</param>
    /// <param name="release"><see langword="true"/> to add them, <see langword="false"/> to remove them.</param>
    /// <returns>The new value; empty means the registry value should be deleted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="keys"/> is <see langword="null"/>.</exception>
    public static string WithKeys(string? disabledHotkeys, IEnumerable<char> keys, bool release)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var current = disabledHotkeys ?? string.Empty;
        var upper = keys.Select(char.ToUpperInvariant).Distinct().ToArray();
        var builder = new StringBuilder();
        if (release)
        {
            builder.Append(current);
            foreach (char key in upper.Where(k => !IsReleased(current, k)))
            {
                builder.Append(key);
            }

            return builder.ToString();
        }

        foreach (char c in current)
        {
            if (!upper.Contains(char.ToUpperInvariant(c)))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Works out what Settings' "Release from Explorer" card offers for these shortcuts.
    /// </summary>
    /// <remarks>
    /// Release while any Win+letter shortcut is not released yet; give back once all are (plus a released Win+V that is no
    /// longer a shortcut — see the class remarks); nothing when no shortcut can be released and Win+V is with Explorer.
    /// </remarks>
    /// <param name="shortcuts">BetterClipboard's shortcuts, in order.</param>
    /// <param name="disabledHotkeys">Explorer's current value, or <see langword="null"/> when absent.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shortcuts"/> is <see langword="null"/>.</exception>
    public static ExplorerReleasePlan Plan(IReadOnlyList<HotkeyGesture> shortcuts, string? disabledHotkeys)
    {
        var releasable = ReleasableKeys(shortcuts);
        var released = releasable.Where(k => IsReleased(disabledHotkeys, k)).ToArray();
        var missing = releasable.Where(k => !IsReleased(disabledHotkeys, k)).ToArray();
        bool strayV = IsReleased(disabledHotkeys, 'V') && !releasable.Contains('V');
        string strayNote = strayV
            ? " Win+V is released from Explorer too, but it is not one of your shortcuts, so it does nothing at the moment."
            : string.Empty;

        if (releasable.Count == 0)
        {
            return strayV
                ? new ExplorerReleasePlan(ExplorerReleaseAction.GiveBack, ['V'],
                    "None of your shortcuts is a Win+letter one." + strayNote)
                : new ExplorerReleasePlan(ExplorerReleaseAction.None, [],
                    "None of your shortcuts is a Win+letter one, so there is nothing to release.");
        }

        if (missing.Length > 0)
        {
            var text = $"{Names(missing)} {IsAre(missing)} not released: BetterClipboard takes {ItThem(missing)} over with the keyboard hook while it runs.";
            if (released.Length > 0)
            {
                text += $" {Names(released)} {IsAre(released)} released.";
            }

            return new ExplorerReleasePlan(ExplorerReleaseAction.Release, missing, text + strayNote);
        }

        char[] giveBack = strayV ? [.. releasable, 'V'] : [.. releasable];
        return new ExplorerReleasePlan(ExplorerReleaseAction.GiveBack, giveBack,
            $"{Names(releasable)} {IsAre(releasable)} released from Explorer: Explorer does not register {ItThem(releasable)}, so BetterClipboard can." + strayNote);
    }

    /// <summary>Shortcut names for keys: "Win+V", "Win+V and Win+C", "Win+V, Win+C and Win+1".</summary>
    /// <param name="keys">Letters or digits.</param>
    /// <returns>The names joined for a sentence; empty for no keys.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="keys"/> is <see langword="null"/>.</exception>
    public static string Names(IReadOnlyList<char> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var names = keys.Select(k => $"Win+{char.ToUpperInvariant(k)}").ToArray();
        return names.Length switch
        {
            0 => string.Empty,
            1 => names[0],
            _ => $"{string.Join(", ", names[..^1])} and {names[^1]}",
        };
    }

    /// <summary>"is" or "are" for a list of keys.</summary>
    /// <param name="keys">The keys.</param>
    /// <returns>The verb.</returns>
    private static string IsAre(IReadOnlyCollection<char> keys) => keys.Count == 1 ? "is" : "are";

    /// <summary>"it" or "them" for a list of keys.</summary>
    /// <param name="keys">The keys.</param>
    /// <returns>The pronoun.</returns>
    private static string ItThem(IReadOnlyCollection<char> keys) => keys.Count == 1 ? "it" : "them";
}
