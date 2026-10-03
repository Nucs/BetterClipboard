using BetterClipboard.Core.Settings;

namespace BetterClipboard.Windows.Input;

/// <summary>
/// The result of reading a list of shortcut texts: the distinct gestures in the order given, and the texts that are not
/// valid shortcuts.
/// </summary>
/// <param name="Gestures">Distinct, valid gestures in their first-seen order; repeats are dropped silently.</param>
/// <param name="Invalid">The texts that did not parse, in order (blank ones included as they were given).</param>
/// <param name="Truncated">Whether valid gestures past <see cref="AppSettings.MaxOpenHotkeys"/> were left out.</param>
public sealed record HotkeyListParse(IReadOnlyList<HotkeyGesture> Gestures, IReadOnlyList<string> Invalid, bool Truncated)
{
    /// <summary>The gestures in their saved (canonical) text form, e.g. <c>Alt+Win+V</c>.</summary>
    public IReadOnlyList<string> CanonicalTexts => Gestures.Select(g => g.ToString()).ToArray();
}

/// <summary>
/// Turns the settings' shortcut texts (<see cref="AppSettings.OpenHotkeys"/>) or installer arguments into gestures to
/// register, so every path agrees on what counts as the same shortcut.
/// </summary>
/// <remarks>
/// Two texts are the same shortcut when they parse to the same modifiers and key, whatever their spelling or modifier
/// order (<c>win+alt+v</c>, <c>Alt + Win + V</c>): text comparison alone (<see cref="AppSettings.SameHotkeyText"/>) would
/// register such a pair twice and the second registration would fail as "already used".
/// </remarks>
public static class HotkeyList
{
    /// <summary>
    /// Parses shortcut texts, keeping the first of any repeated gesture and at most <see cref="AppSettings.MaxOpenHotkeys"/>.
    /// </summary>
    /// <param name="texts">The texts in order (the first is the main shortcut); <see langword="null"/> entries count as invalid.</param>
    /// <returns>The gestures and the texts that are not shortcuts.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="texts"/> is <see langword="null"/>.</exception>
    public static HotkeyListParse Parse(IEnumerable<string?> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var gestures = new List<HotkeyGesture>();
        var invalid = new List<string>();
        bool truncated = false;
        foreach (var text in texts)
        {
            if (!HotkeyGesture.TryParse(text, out var gesture))
            {
                invalid.Add(text ?? string.Empty);
                continue;
            }

            if (gestures.Contains(gesture))
            {
                continue;
            }

            if (gestures.Count == AppSettings.MaxOpenHotkeys)
            {
                truncated = true;
                continue;
            }

            gestures.Add(gesture);
        }

        return new HotkeyListParse(gestures, invalid, truncated);
    }
}
