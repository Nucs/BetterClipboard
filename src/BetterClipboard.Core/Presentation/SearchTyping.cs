using System.Globalization;
using System.Text;

namespace BetterClipboard.Core.Presentation;

/// <summary>
/// Edits the panel's search text without a caret: what typing does while the panel shows without the keyboard (it leaves
/// the focus in the app the user types in, CLAUDE.md §2.59). Characters are added at the end, Backspace takes the last
/// character away, Ctrl+Backspace the last word.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why at the end only.</b> The search box has no keyboard focus then, so it shows no caret, and a caret the user cannot
/// see cannot be moved on purpose: Left and Right switch tabs instead. Windows' emoji panel searches the same way.
/// </para>
/// <para>
/// <b>What a character is.</b> Backspace removes one text element (<see cref="StringInfo"/>): a letter with its combining
/// accents, a surrogate pair, an emoji sequence — never half of one, which would leave a lone surrogate in the search.
/// Pure, so the rules are unit-tested.
/// </para>
/// </remarks>
public static class SearchTyping
{
    /// <summary>
    /// The longest search text typing builds: far more than anyone types, short enough that a key held down for minutes (or
    /// a stuck key) cannot grow the text without bound. Characters past it are dropped.
    /// </summary>
    public const int MaxLength = 4000;

    /// <summary>The text with <paramref name="typed"/> added at its end, control characters left out.</summary>
    /// <param name="current">The search text now; <see langword="null"/> counts as empty.</param>
    /// <param name="typed">What a key typed (one character, or several for a dead key that did not combine).</param>
    /// <returns>The new text; <paramref name="current"/> itself (or empty) when nothing printable was typed or the text is full.</returns>
    /// <remarks>
    /// Control characters (C0, DEL, C1) are what Ctrl+letter or Enter translate to; they never belong in a search. A tab is
    /// one of them. A line break could only come from such a key, so it is left out too.
    /// </remarks>
    public static string Append(string? current, string? typed)
    {
        current ??= string.Empty;
        if (string.IsNullOrEmpty(typed) || current.Length >= MaxLength)
        {
            return current;
        }

        var builder = new StringBuilder(current, Math.Min(MaxLength, current.Length + typed.Length));
        foreach (char c in typed)
        {
            if (builder.Length >= MaxLength)
            {
                break;
            }

            if (!char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        // A surrogate pair cut by the length limit leaves a lone high surrogate: drop it rather than search for it.
        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1]) && builder.Length > current.Length)
        {
            builder.Length--;
        }

        return builder.ToString();
    }

    /// <summary>The text without its last text element (Backspace).</summary>
    /// <param name="current">The search text now; <see langword="null"/> counts as empty.</param>
    /// <returns>The text one character shorter, or empty when it was empty or one character long.</returns>
    public static string EraseLast(string? current)
    {
        if (string.IsNullOrEmpty(current))
        {
            return string.Empty;
        }

        // The start of the last text element: everything before it stays.
        int last = 0;
        var elements = StringInfo.GetTextElementEnumerator(current);
        while (elements.MoveNext())
        {
            last = elements.ElementIndex;
        }

        return current[..last];
    }

    /// <summary>
    /// The text without its last word (Ctrl+Backspace), the way a text box does it: white space at the end goes, then the
    /// run before it — letters and digits, or a run of other symbols when the text ends with those.
    /// </summary>
    /// <param name="current">The search text now; <see langword="null"/> counts as empty.</param>
    /// <returns>The shortened text; empty when only one word (or nothing) was there.</returns>
    public static string EraseLastWord(string? current)
    {
        if (string.IsNullOrEmpty(current))
        {
            return string.Empty;
        }

        // Text elements, so a word with accents or an emoji goes whole.
        var starts = new List<int>();
        var elements = StringInfo.GetTextElementEnumerator(current);
        while (elements.MoveNext())
        {
            starts.Add(elements.ElementIndex);
        }

        int index = starts.Count - 1;
        while (index >= 0 && IsSpace(current, starts[index]))
        {
            index--;
        }

        if (index < 0)
        {
            return string.Empty;
        }

        bool word = IsWordElement(current, starts[index]);
        while (index >= 0 && !IsSpace(current, starts[index]) && IsWordElement(current, starts[index]) == word)
        {
            index--;
        }

        return index < 0 ? string.Empty : current[..starts[index + 1]];
    }

    /// <summary>Whether the text element at <paramref name="start"/> is white space.</summary>
    /// <param name="text">The text.</param>
    /// <param name="start">The element's start index.</param>
    /// <returns><see langword="true"/> for white space.</returns>
    private static bool IsSpace(string text, int start) => char.IsWhiteSpace(text, start);

    /// <summary>
    /// Whether the text element at <paramref name="start"/> belongs to a word: a letter, a digit, a combining mark or a
    /// connector (<c>_</c>), read as a whole code point.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="start">The element's start index.</param>
    /// <returns><see langword="true"/> for a word character.</returns>
    private static bool IsWordElement(string text, int start)
    {
        var category = CharUnicodeInfo.GetUnicodeCategory(text, start);
        return char.IsLetterOrDigit(text, start)
            || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.ConnectorPunctuation;
    }
}
