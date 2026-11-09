using System.Globalization;
using System.Text.Json;
using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Updates;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Right-to-left languages in left-to-right windows (the owner's choice, 2026-11-09): Hebrew's translations come embedded
/// right to left, lines the code joins from pieces are embedded as a whole, and nothing else changes — English and the
/// other languages, values without right-to-left text, English fallbacks.
/// </summary>
public sealed class RightToLeftTextTests
{
    /// <summary>U+202B RIGHT-TO-LEFT EMBEDDING, written by its code point so no invisible character sits in this file.</summary>
    private const char Rle = (char)0x202B;

    /// <summary>U+202C POP DIRECTIONAL FORMATTING, which closes <see cref="Rle"/>.</summary>
    private const char Pdf = (char)0x202C;

    /// <summary>U+200F RIGHT-TO-LEFT MARK.</summary>
    private const char Rlm = (char)0x200F;

    /// <summary>
    /// Texts and whether they hold strongly right-to-left text: Hebrew and Arabic letters, a letter of Adlam (beyond the
    /// BMP), the right-to-left mark and the Arabic letter mark count; Latin, placeholders, Arabic-Indic digits (weak:
    /// they take the direction around them) and an empty text do not.
    /// </summary>
    public static TheoryData<string, bool> Texts => new()
    {
        { "שלום", true },
        { "مرحبا", true },
        { char.ConvertFromUtf32(0x1E900), true },
        { Rlm.ToString(), true },
        { ((char)0x061C).ToString(), true },
        { "Paste to {0} · 5 min ago", false },
        { "١٢٣", false },
        { string.Empty, false },
    };

    /// <summary>
    /// Catalog headers and their direction: none and <c>"ltr"</c> are left to right, <c>"rtl"</c> right to left, and
    /// anything else is refused (<see langword="null"/>), a misspelling or another case included.
    /// </summary>
    public static TheoryData<string, bool?> Directions => new()
    {
        { "{}", false },
        { """{ "direction": "ltr" }""", false },
        { """{ "direction": "rtl" }""", true },
        { """{ "direction": "RTL" }""", null },
        { """{ "direction": "rlt" }""", null },
        { """{ "direction": 1 }""", null },
        { """{ "direction": null }""", null },
    };

    /// <summary>
    /// Titles and the sentences the update texts make of them: a period is added unless one ends the title already, and
    /// for a right-to-left title it goes in before the embedding's closing mark, inside the sentence.
    /// </summary>
    public static TheoryData<string, string> Sentences => new()
    {
        { "Up to date", "Up to date." },
        { "Done.", "Done." },
        { "Downloading…", "Downloading…" },
        { "已是最新版本。", "已是最新版本。" },
        { $"{Rle}מעודכן{Pdf}", $"{Rle}מעודכן.{Pdf}" },
        { $"{Rle}מעודכן?{Pdf}", $"{Rle}מעודכן?{Pdf}" },
        { string.Empty, string.Empty },
        { $"{Rle}{Pdf}", $"{Rle}{Pdf}" },
    };

    /// <summary>The shipped Hebrew catalog.</summary>
    private static UiLanguage Hebrew => UiText.AllShipped.Single(language => language.Tag == "he");

    /// <summary>Hebrew is written right to left; every other shipped language, English first, is not.</summary>
    [Fact]
    public void Hebrew_IsTheOnlyRightToLeftLanguage()
    {
        Assert.True(Hebrew.IsRightToLeft);
        Assert.All(UiText.AllShipped.Where(language => language.Tag != "he"), language => Assert.False(language.IsRightToLeft, language.Tag));
    }

    /// <summary>
    /// Every Hebrew string with right-to-left text in it is embedded exactly once, its two marks at its ends and nowhere
    /// else (the catalog holds none of its own); the others are the few values without a letter of Hebrew, which must
    /// stay as they are: the list separator (embedding it would reorder English items in a list), the date patterns,
    /// PowerShell's name with its host, and a template of placeholders only.
    /// </summary>
    [Fact]
    public void HebrewStrings_AreEmbeddedOnce()
    {
        var plain = new List<string>();
        foreach ((string key, string text) in Hebrew.Strings)
        {
            if (text.Length > 0 && text[0] == Rle)
            {
                Assert.Equal(Pdf, text[^1]);
                string inner = text[1..^1];
                Assert.DoesNotContain(Rle, inner);
                Assert.DoesNotContain(Pdf, inner);
                Assert.True(UiText.HoldsRightToLeftText(inner), key);
                continue;
            }

            Assert.False(UiText.HoldsRightToLeftText(text), key);
            Assert.DoesNotContain(Pdf, text);
            plain.Add(key);
        }

        Assert.Equal([", ", "PowerShell ({0})", "d MMM", "d MMM yyyy", "{0}: {1}"], plain.Order(StringComparer.Ordinal));

        // A template with no letter of its own asks for the embedding with a leading mark: the phrase and its
        // parenthesis are then ordered right to left as one line.
        Assert.Equal($"{Rle}{Rlm}{{0}} ({{1}}){Pdf}", UiText.Translate("{0} ({1})", Hebrew));
    }

    /// <summary>
    /// Formatted text keeps its values inside the embedding (a group's name, a count), plural forms come embedded, a
    /// text no catalog has falls back to English without an embedding (English inside one would end with its period at
    /// the left), and the left-to-right languages hand out their text as it is.
    /// </summary>
    [Fact]
    public void Hebrew_FormattedText_KeepsItsValuesInside()
    {
        string previous = UiText.Language;
        try
        {
            UiText.Language = "he";

            // A variable, not a constant: an interpolation of constants only is a constant string, never a template.
            string group = "BC-TEST Work";
            string added = UiText.Format($"Add to {group}");
            Assert.Equal((Rle, Pdf), (added[0], added[^1]));
            Assert.Contains(group, added, StringComparison.Ordinal);

            int count = 5;
            string items = UiText.Plural(CultureInfo.InvariantCulture, count, $"{count:N0} item", $"{count:N0} items");
            Assert.Equal((Rle, Pdf), (items[0], items[^1]));
            Assert.Contains("5", items, StringComparison.Ordinal);

            Assert.Equal("BC-TEST no catalog holds this", UiText.Translate("BC-TEST no catalog holds this"));

            UiText.Language = "ru";
            Assert.DoesNotContain(Rle, UiText.Format($"Add to {group}"));
        }
        finally
        {
            UiText.Language = previous;
        }
    }

    /// <summary>
    /// A message worded in English for the log comes out in Hebrew embedded, with its matched parts inside, and a part
    /// that is itself one of the templates ("the release list") embedded inside it: every embedding is closed.
    /// </summary>
    [Fact]
    public void TranslateFormatted_InHebrew_IsEmbeddedAndBalanced()
    {
        string refused = UpdateMessages.Format(UpdateMessages.ServerRefused, UpdateMessages.TheReleaseList, 404);
        string hebrew = UiText.TranslateFormatted(refused, UpdateMessages.Templates, Hebrew);
        Assert.Equal((Rle, Pdf), (hebrew[0], hebrew[^1]));
        Assert.Contains("404", hebrew, StringComparison.Ordinal);
        Assert.True(hebrew.Count(c => c == Rle) >= 2, "the nested template is translated too");
        Assert.Equal(hebrew.Count(c => c == Rle), hebrew.Count(c => c == Pdf));
    }

    /// <summary>
    /// A line joined from pieces is embedded as a whole in Hebrew, so it reads from its first piece at the right, and is
    /// a plain join in every left-to-right language; an empty line gets no marks.
    /// </summary>
    [Fact]
    public void JoinLine_EmbedsOnlyRightToLeftLines()
    {
        Assert.Equal("Notepad · 5 min ago", UiText.JoinLine(" · ", ["Notepad", "5 min ago"], UiText.English));
        Assert.True(UiText.TryFind("zh-Hans", out var chinese));
        Assert.Equal("记事本 · 5 分钟前", UiText.JoinLine(" · ", ["记事本", "5 分钟前"], chinese));
        Assert.Equal($"{Rle}Notepad · לפני 5 דקות{Pdf}", UiText.JoinLine(" · ", ["Notepad", "לפני 5 דקות"], Hebrew));
        Assert.Equal(string.Empty, UiText.JoinLine(" · ", [], Hebrew));
        Assert.Throws<ArgumentNullException>(() => UiText.JoinLine(" · ", null!, Hebrew));
    }

    /// <summary>Which texts count as right-to-left (see <see cref="Texts"/>).</summary>
    /// <param name="text">The text.</param>
    /// <param name="expected">Whether it holds strongly right-to-left text.</param>
    [Theory]
    [MemberData(nameof(Texts))]
    public void HoldsRightToLeftText_CountsStrongCharactersOnly(string text, bool expected) =>
        Assert.Equal(expected, UiText.HoldsRightToLeftText(text));

    /// <summary>What a catalog header's direction means (see <see cref="Directions"/>).</summary>
    /// <param name="header">The header's JSON.</param>
    /// <param name="expected">Whether it is right to left, or <see langword="null"/> for a refused header.</param>
    [Theory]
    [MemberData(nameof(Directions))]
    public void ReadDirection_AcceptsOnlyLtrAndRtl(string header, bool? expected)
    {
        using var document = JsonDocument.Parse(header);
        if (expected is { } rightToLeft)
        {
            Assert.Equal(rightToLeft, UiText.ReadDirection(document.RootElement, "xx"));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => UiText.ReadDirection(document.RootElement, "xx"));
        }
    }

    /// <summary>A title made a sentence (see <see cref="Sentences"/>).</summary>
    /// <param name="title">The title.</param>
    /// <param name="expected">The sentence.</param>
    [Theory]
    [MemberData(nameof(Sentences))]
    public void AsSentence_PutsThePeriodInsideTheEmbedding(string title, string expected) =>
        Assert.Equal(expected, UpdateText.AsSentence(title));
}
