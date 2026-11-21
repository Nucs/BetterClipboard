using System.Globalization;
using System.Text.Json;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Settings;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// The panel that opens without taking the keyboard from the app the user types in (CLAUDE.md §2.59): its setting, and how
/// typed keys edit the search text without a caret (<see cref="SearchTyping"/>): added at the end, Backspace taking one whole
/// character, Ctrl+Backspace one word.
/// </summary>
public sealed class PanelWithoutFocusTests : IDisposable
{
    /// <summary>Pieces the random edits type: plain letters, digits, punctuation, white space, and characters made of several code units or code points.</summary>
    private static readonly string[] Pieces =
    [
        "a", "Z", "7", "_", "+", ".", "!", " ", "\u00A0", "\u05E9", "\u00E9", "e\u0301", "\U0001F600",
        "\U0001F468\u200D\U0001F469\u200D\U0001F467", "\U0001F1EE\U0001F1F1", "\u20AC", "\t", "\r", "\u001B",
    ];

    /// <summary>A temp folder for settings files.</summary>
    private readonly TempDirectory temp = TestData.NewTempDirectory();

    /// <summary>Texts and what a key typed, with the search text that follows: printable characters at the end, never a control character.</summary>
    public static TheoryData<string?, string?, string> Appends => new()
    {
        { null, "a", "a" },
        { "", "a", "a" },
        { "ab", "c", "abc" },
        { "ab", null, "ab" },
        { "ab", "", "ab" },

        // Enter, Tab, Esc, Ctrl+C's character, Ctrl+Backspace's DEL, and a C1 control: none of them belongs in a search.
        { "ab", "\r", "ab" },
        { "ab", "\t", "ab" },
        { "ab", "\u001B", "ab" },
        { "ab", "\u0003", "ab" },
        { "ab", "\u007F", "ab" },
        { "ab", "\u0085", "ab" },
        { "a", "\t b", "a b" },

        // A dead key that did not combine types two characters; a character beyond the BMP comes as a surrogate pair.
        { "", "\u00B4x", "\u00B4x" },
        { "x", "\U0001F600", "x\U0001F600" },
        { "", "\u05E9", "\u05E9" },
        { "\u20AC", "@", "\u20AC@" },
    };

    /// <summary>Texts and what Backspace leaves of them: one whole text element less, never half a character.</summary>
    public static TheoryData<string?, string> Erasures => new()
    {
        { null, "" },
        { "", "" },
        { "a", "" },
        { "ab", "a" },
        { "ab ", "ab" },

        // A letter with its combining accent, an emoji, a family joined by zero-width joiners, a flag, a Hebrew letter with
        // its marks: each is one character to the person typing.
        { "ae\u0301", "a" },
        { "a\U0001F600", "a" },
        { "a\U0001F468\u200D\U0001F469\u200D\U0001F467", "a" },
        { "a\U0001F1EE\U0001F1F1", "a" },
        { "a\u05E9\u05B8\u05C1", "a" },

        // A lone surrogate (never typed, but possible in pasted text) goes alone.
        { "a\uD83D", "a" },
    };

    /// <summary>Texts and what Ctrl+Backspace leaves of them: trailing white space, then a run of word characters or of other symbols.</summary>
    public static TheoryData<string?, string> WordErasures => new()
    {
        { null, "" },
        { "", "" },
        { "   ", "" },
        { "hello", "" },
        { "hello world", "hello " },
        { "hello world  ", "hello " },
        { "word\t", "" },
        { "a b\u00A0", "a " },
        { "x 123", "x " },
        { "a+b", "a+" },
        { "a++", "a" },
        { "abc!?", "abc" },
        { "foo_bar", "" },
        { "C:\\path\\to", "C:\\path\\" },
        { "cafe\u0301 latte", "cafe\u0301 " },
        { "latte cafe\u0301", "latte " },
        { "say \U0001F600\U0001F600", "say " },
        { "\u05E9\u05DC\u05D5\u05DD \u05E2\u05D5\u05DC\u05DD", "\u05E9\u05DC\u05D5\u05DD " },
    };

    /// <summary>Deletes the temp folder.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>
    /// On for a new install and for a settings file from before the setting (the owner's choice: the panel leaves the keyboard
    /// with the app by default), and a choice to turn it off survives a save and a load under its own name.
    /// </summary>
    [Fact]
    public void Setting_OnByDefault_AndKeptWhenTurnedOff()
    {
        var path = Path.Combine(temp.Path, "settings.json");
        Assert.True(new AppSettings().LeaveFocusInApp);
        Assert.True(new SettingsStore(path).Load().LeaveFocusInApp);

        var store = new SettingsStore(path);
        store.Load();
        store.Update(s => s with { LeaveFocusInApp = false });
        Assert.False(new SettingsStore(path).Load().LeaveFocusInApp);
        using (var file = JsonDocument.Parse(File.ReadAllText(path)))
        {
            Assert.False(file.RootElement.GetProperty(nameof(AppSettings.LeaveFocusInApp)).GetBoolean());
        }

        // What a version from before the setting wrote: the member is missing, so the default applies.
        File.WriteAllText(path, "{ \"MaxItems\": 50 }");
        var older = new SettingsStore(path).Load();
        Assert.True(older.LeaveFocusInApp);
        Assert.Equal(50, older.MaxItems);
    }

    /// <summary>A key's characters go at the end of the search text, without its control characters.</summary>
    /// <param name="current">The search text before.</param>
    /// <param name="typed">What the key typed.</param>
    /// <param name="expected">The search text after.</param>
    [Theory]
    [MemberData(nameof(Appends))]
    public void Append_AddsPrintableCharactersAtTheEnd(string? current, string? typed, string expected) =>
        Assert.Equal(expected, SearchTyping.Append(current, typed));

    /// <summary>
    /// The text stops growing at <see cref="SearchTyping.MaxLength"/>, a surrogate pair the limit would cut is left out whole,
    /// and a lone surrogate the text already ended with stays (only one this call added is dropped).
    /// </summary>
    [Fact]
    public void Append_StopsAtTheLimit_WithoutHalfACharacter()
    {
        string full = new('a', SearchTyping.MaxLength);
        Assert.Equal(full, SearchTyping.Append(full, "b"));

        string almost = new('a', SearchTyping.MaxLength - 1);
        Assert.Equal(almost + "b", SearchTyping.Append(almost, "bc"));
        Assert.Equal(almost, SearchTyping.Append(almost, "\U0001F600"));

        Assert.Equal("a\uD83D", SearchTyping.Append("a\uD83D", "\t"));
    }

    /// <summary>Backspace takes the last whole character.</summary>
    /// <param name="current">The search text before.</param>
    /// <param name="expected">The search text after.</param>
    [Theory]
    [MemberData(nameof(Erasures))]
    public void EraseLast_TakesOneWholeCharacter(string? current, string expected) =>
        Assert.Equal(expected, SearchTyping.EraseLast(current));

    /// <summary>Ctrl+Backspace takes the last word, the way a text box does.</summary>
    /// <param name="current">The search text before.</param>
    /// <param name="expected">The search text after.</param>
    [Theory]
    [MemberData(nameof(WordErasures))]
    public void EraseLastWord_TakesTheLastWord(string? current, string expected) =>
        Assert.Equal(expected, SearchTyping.EraseLastWord(current));

    /// <summary>
    /// 2,000 random runs of typing, Backspace and Ctrl+Backspace keep every rule: no control character, never longer than
    /// the limit, never a lone surrogate, Backspace one text element less and a prefix of what it erased, Ctrl+Backspace a
    /// prefix too, shorter whenever there was a word to take.
    /// </summary>
    [Fact]
    public void RandomEdits_KeepEveryRule()
    {
        var random = new Random(20261120);
        for (int run = 0; run < 2000; run++)
        {
            string text = string.Empty;
            int steps = random.Next(1, 41);
            for (int step = 0; step < steps; step++)
            {
                string before = text;
                switch (random.Next(4))
                {
                    case 0:
                    case 1:
                        text = SearchTyping.Append(text, Pieces[random.Next(Pieces.Length)]);
                        Assert.StartsWith(before, text, StringComparison.Ordinal);
                        break;
                    case 2:
                        text = SearchTyping.EraseLast(text);
                        Assert.StartsWith(text, before, StringComparison.Ordinal);
                        if (before.Length > 0)
                        {
                            Assert.Equal(new StringInfo(before).LengthInTextElements - 1, new StringInfo(text).LengthInTextElements);
                        }

                        break;
                    default:
                        text = SearchTyping.EraseLastWord(text);
                        Assert.StartsWith(text, before, StringComparison.Ordinal);
                        if (!string.IsNullOrWhiteSpace(before))
                        {
                            Assert.True(text.Length < before.Length, $"Ctrl+Backspace took nothing from {Escape(before)}.");
                        }

                        break;
                }

                Assert.True(text.Length <= SearchTyping.MaxLength);
                Assert.DoesNotContain(text, char.IsControl);
                Assert.True(IsWellFormed(text), $"A lone surrogate in {Escape(text)}.");
            }
        }
    }

    /// <summary>Whether a text holds no lone surrogate.</summary>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> when every surrogate is part of a pair.</returns>
    private static bool IsWellFormed(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A text with every character outside printable ASCII written as <c>\uXXXX</c>, for a failure message.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The escaped text.</returns>
    private static string Escape(string text) =>
        string.Concat(text.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $"\\u{(int)c:X4}"));
}
