using System.Globalization;
using System.Text;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Presentation;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// The previews the app words itself are shown in the interface language (<see cref="PreviewText"/>, 2026-11-16): the
/// classifier's own output goes through the display rule, so a change to one of its shapes fails here instead of showing
/// English in every language; and text the user copied is never translated, even when it reads like those words.
/// </summary>
/// <remarks>
/// The display rule follows <see cref="UiText.Current"/>: each test selects its language and puts the baseline back. Core
/// tests run one class at a time, so the setting cannot leak into another class.
/// </remarks>
public sealed class PreviewTextTests
{
    /// <summary>U+202B RIGHT-TO-LEFT EMBEDDING, written by its code point so no invisible character sits in this file.</summary>
    private const char Rle = (char)0x202B;

    /// <summary>U+202C POP DIRECTIONAL FORMATTING, which closes <see cref="Rle"/>.</summary>
    private const char Pdf = (char)0x202C;

    /// <summary>The formats of a plain text copy.</summary>
    private static readonly string[] TextFormats = [ClipFormatNames.UnicodeText];

    /// <summary>Eight file paths: a file list long enough for its preview's "+3 more" line.</summary>
    private static readonly string[] EightFiles =
        [.. Enumerable.Range(1, 8).Select(i => $@"C:\BC-TEST\file{i}.txt")];

    /// <summary>
    /// Texts the user could copy that read like the app's words, with the kind and formats a copy of them is stored with:
    /// each must come back exactly as it is, in any language.
    /// </summary>
    public static TheoryData<ClipKind, string, string[]> UserContent => new()
    {
        { ClipKind.Text, "Image · 1 × 1", TextFormats },
        { ClipKind.Text, "Image", TextFormats },
        { ClipKind.Text, "Formatted content", TextFormats },
        { ClipKind.RichText, "Formatted content", [ClipFormatNames.UnicodeText, ClipFormatNames.Html] },
        { ClipKind.Text, "a\nb\nc\nd\ne\n+3 more", TextFormats },
        { ClipKind.Text, "(whitespace · 3 chars) is what the card said", TextFormats },
        { ClipKind.Link, "(whitespace · 3 chars)", TextFormats },
        { ClipKind.Files, "a.txt\nb.txt", [ClipFormatNames.HDrop] },
        { ClipKind.Files, "a\nb\nc\nd\ne", [ClipFormatNames.HDrop] },
        { ClipKind.Files, "a\nb\nc\nd\ne\nf", [ClipFormatNames.HDrop] },
        { ClipKind.Image, "Image · 1920 x 1080", [ClipFormatNames.Png] },
        { ClipKind.Color, "#3366FF", TextFormats },
    };

    /// <summary>What the user copied comes back as it is, in Chinese and in English.</summary>
    /// <param name="kind">The entry's kind.</param>
    /// <param name="preview">The stored preview.</param>
    /// <param name="formats">The entry's stored formats.</param>
    [Theory]
    [MemberData(nameof(UserContent))]
    public void Display_LeavesWhatTheUserCopiedAsItIs(ClipKind kind, string preview, string[] formats)
    {
        string previous = UiText.Language;
        try
        {
            UiText.Language = "zh-Hans";
            Assert.Equal(preview, PreviewText.Display(kind, preview, formats));
            UiText.Language = "en";
            Assert.Equal(preview, PreviewText.Display(kind, preview, formats));
        }
        finally { UiText.Language = previous; }
    }

    /// <summary>
    /// The classifier's own words, as it stores them, read in Chinese: formatted content without text, a white-space-only
    /// text, a picture with and without its size, and a long file list's last line (its names stay as they are).
    /// </summary>
    [Fact]
    public void Display_TranslatesTheClassifiersOwnWords()
    {
        var richOnly = ContentClassifier.Classify(new ClipCapture
        {
            Formats = [new ClipFormatData(ClipFormatNames.Html, Encoding.UTF8.GetBytes("<b>BC-TEST</b>"))],
        })!;
        var spaces = ContentClassifier.Classify(TestData.Text("   "))!;
        var files = ContentClassifier.Classify(TestData.Files(EightFiles))!;
        string previous = UiText.Language;
        try
        {
            UiText.Language = "zh-Hans";
            Assert.Equal(ContentClassifier.RichOnlyPreview, richOnly.Preview);
            Assert.Equal("带格式内容", PreviewText.Display(richOnly.Kind, richOnly.Preview, [ClipFormatNames.Html]));
            Assert.Equal("（全是空白 · 3 个字符）", PreviewText.Display(spaces.Kind, spaces.Preview, TextFormats));
            Assert.Equal("（全是空白 · 1 个字符）", PreviewText.Display(ClipKind.Text, ContentClassifier.BuildTextPreview(" "), TextFormats));
            // Counts are formatted with the current culture (CultureInfo.CurrentCulture), whatever the machine runs.
            Assert.Equal($"（全是空白 · {1234.ToString("N0", CultureInfo.CurrentCulture)} 个字符）", PreviewText.Display(ClipKind.RichText, ContentClassifier.BuildTextPreview(new string(' ', 1234)), TextFormats));
            Assert.Equal("图片 · 1920 × 1080", PreviewText.Display(ClipKind.Image, ContentClassifier.DescribeImage(1920, 1080), [ClipFormatNames.Png]));
            Assert.Equal("图片", PreviewText.Display(ClipKind.Image, ContentClassifier.DescribeImage(null, null), [ClipFormatNames.Png]));
            Assert.Equal(
                "file1.txt\nfile2.txt\nfile3.txt\nfile4.txt\nfile5.txt\n还有 3 项",
                PreviewText.Display(files.Kind, files.Preview, [ClipFormatNames.HDrop]));
        }
        finally { UiText.Language = previous; }
    }

    /// <summary>In English the display is the stored words themselves (a count of a thousand or more gains its separator).</summary>
    [Fact]
    public void Display_InEnglish_IsTheStoredWords()
    {
        var files = ContentClassifier.Classify(TestData.Files(EightFiles))!;
        string previous = UiText.Language;
        try
        {
            UiText.Language = "en";
            foreach (string stored in new[] { ContentClassifier.BuildTextPreview(" "), ContentClassifier.BuildTextPreview(" \t\n") })
            {
                Assert.Equal(stored, PreviewText.Display(ClipKind.Text, stored, TextFormats));
            }

            Assert.Equal(ContentClassifier.RichOnlyPreview, PreviewText.Display(ClipKind.RichText, ContentClassifier.RichOnlyPreview, [ClipFormatNames.Rtf]));
            Assert.Equal(ContentClassifier.DescribeImage(640, 480), PreviewText.Display(ClipKind.Image, ContentClassifier.DescribeImage(640, 480), []));
            Assert.Equal(files.Preview, PreviewText.Display(ClipKind.Files, files.Preview, [ClipFormatNames.HDrop]));
            Assert.Equal($"(whitespace · {1500.ToString("N0", CultureInfo.CurrentCulture)} chars)", PreviewText.Display(ClipKind.Text, ContentClassifier.BuildTextPreview(new string('\n', 1500)), TextFormats));
        }
        finally { UiText.Language = previous; }
    }

    /// <summary>
    /// The white-space placeholder takes each language's plural forms (Russian's one, few and many; Hebrew's one and two),
    /// and a Hebrew translation comes embedded right to left like every Hebrew string.
    /// </summary>
    [Fact]
    public void Display_UsesEachLanguagesPluralForms()
    {
        string previous = UiText.Language;
        try
        {
            UiText.Language = "ru";
            Assert.Equal("(только пробелы · 1 символ)", Whitespace(1));
            Assert.Equal("(только пробелы · 3 символа)", Whitespace(3));
            Assert.Equal("(только пробелы · 5 символов)", Whitespace(5));
            Assert.Equal("ещё 3", Files().Split('\n')[^1]);

            UiText.Language = "he";
            Assert.Equal($"{Rle}(רק רווחים · תו אחד){Pdf}", Whitespace(1));
            Assert.Equal($"{Rle}(רק רווחים · שני תווים){Pdf}", Whitespace(2));
            Assert.Equal($"{Rle}(רק רווחים · 7 תווים){Pdf}", Whitespace(7));
            string files = Files();
            Assert.StartsWith("file1.txt\n", files, StringComparison.Ordinal);
            Assert.Equal($"{Rle}ועוד 3{Pdf}", files.Split('\n')[^1]);
        }
        finally { UiText.Language = previous; }

        static string Whitespace(int count) =>
            PreviewText.Display(ClipKind.Text, ContentClassifier.BuildTextPreview(new string(' ', count)), TextFormats);

        static string Files() =>
            PreviewText.Display(ClipKind.Files, ContentClassifier.DescribeFiles(EightFiles), [ClipFormatNames.HDrop]);
    }

    /// <summary>The entry overload reads the entry's kind, preview and formats; nulls are refused.</summary>
    [Fact]
    public void Display_OfAnEntry_AndItsArguments()
    {
        var entry = new ClipEntry { Kind = ClipKind.RichText, Preview = ContentClassifier.RichOnlyPreview, FormatNames = [ClipFormatNames.Html] };
        string previous = UiText.Language;
        try
        {
            UiText.Language = "zh-Hans";
            Assert.Equal("带格式内容", PreviewText.Display(entry));
            var withText = new ClipEntry { Kind = ClipKind.RichText, Preview = ContentClassifier.RichOnlyPreview, FormatNames = [ClipFormatNames.Html, ClipFormatNames.UnicodeText] };
            Assert.Equal(ContentClassifier.RichOnlyPreview, PreviewText.Display(withText));
        }
        finally { UiText.Language = previous; }

        Assert.Throws<ArgumentNullException>(() => PreviewText.Display(null!));
        Assert.Throws<ArgumentNullException>(() => PreviewText.Display(ClipKind.Text, null!, TextFormats));
        Assert.Throws<ArgumentNullException>(() => PreviewText.Display(ClipKind.Text, "x", null!));
    }
}
