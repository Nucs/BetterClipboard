using System.Text;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the pure content helpers: codecs, detectors, hashing and the classification decision table.
/// </summary>
public sealed class ContentTests
{
    /// <summary>Clipboard text ends at the first NUL even when the HGLOBAL carries garbage after it.</summary>
    [Fact]
    public void UnicodeText_Decode_StopsAtTerminator()
    {
        var bytes = Encoding.Unicode.GetBytes("hello\0garbage");
        Assert.Equal("hello", UnicodeTextCodec.Decode(bytes));
    }

    /// <summary>A NUL byte inside a code unit ('A' = 41 00) must not be mistaken for the terminator.</summary>
    [Fact]
    public void UnicodeText_Decode_IgnoresHalfZeroCodeUnits()
    {
        Assert.Equal("AB", UnicodeTextCodec.Decode(UnicodeTextCodec.Encode("AB")));
    }

    /// <summary>Malformed odd-length input is tolerated (third-party producers are not trusted).</summary>
    [Fact]
    public void UnicodeText_Decode_ToleratesOddLength()
    {
        var bytes = Encoding.Unicode.GetBytes("ok").Append((byte)0x41).ToArray();
        Assert.Equal("ok", UnicodeTextCodec.Decode(bytes));
    }

    /// <summary>Encoding appends exactly one UTF-16 terminator.</summary>
    [Fact]
    public void UnicodeText_Encode_AppendsTerminator()
    {
        var bytes = UnicodeTextCodec.Encode("é");
        Assert.Equal(4, bytes.Length);
        Assert.Equal(0, bytes[2]);
        Assert.Equal(0, bytes[3]);
    }

    /// <summary>File lists round-trip through the DROPFILES wire format, including non-ASCII names.</summary>
    [Fact]
    public void DropFiles_RoundTrips()
    {
        string[] paths = [@"C:\temp\a.txt", @"D:\שלום\b.png"];
        Assert.Equal(paths, DropFilesCodec.Decode(DropFilesCodec.Encode(paths)));
    }

    /// <summary>Malformed headers and offsets produce an empty list instead of throwing.</summary>
    [Fact]
    public void DropFiles_Malformed_ReturnsEmpty()
    {
        Assert.Empty(DropFilesCodec.Decode([1, 2, 3]));
        var bogusOffset = new byte[24];
        bogusOffset[0] = 200; // pFiles beyond the buffer
        Assert.Empty(DropFilesCodec.Decode(bogusOffset));
    }

    /// <summary>Legacy ANSI (fWide = 0) lists are still readable.</summary>
    [Fact]
    public void DropFiles_DecodesAnsiLists()
    {
        var list = Encoding.ASCII.GetBytes("C:\\x.txt\0C:\\y.txt\0\0");
        var payload = new byte[DropFilesCodec.HeaderSize + list.Length];
        payload[0] = DropFilesCodec.HeaderSize;
        list.CopyTo(payload, DropFilesCodec.HeaderSize);
        Assert.Equal([@"C:\x.txt", @"C:\y.txt"], DropFilesCodec.Decode(payload));
    }

    /// <summary>Paths containing NUL are rejected because they would split the list.</summary>
    [Fact]
    public void DropFiles_Encode_RejectsNul()
    {
        Assert.Throws<ArgumentException>(() => DropFilesCodec.Encode(["a\0b"]));
    }

    /// <summary>All supported color literal syntaxes parse to the expected ARGB values.</summary>
    /// <param name="text">Literal.</param>
    /// <param name="a">Alpha.</param>
    /// <param name="r">Red.</param>
    /// <param name="g">Green.</param>
    /// <param name="b">Blue.</param>
    [Theory]
    [InlineData("#1E90FF", 255, 0x1E, 0x90, 0xFF)]
    [InlineData("  #1af  ", 255, 0x11, 0xAA, 0xFF)]
    [InlineData("#80FF0000", 0x80, 0xFF, 0, 0)]
    [InlineData("#f008", 0x88, 0xFF, 0, 0)]
    [InlineData("rgb(30, 144, 255)", 255, 30, 144, 255)]
    [InlineData("RGBA(0,0,0,0.5)", 128, 0, 0, 0)]
    public void ColorLiteral_ParsesSupportedSyntaxes(string text, int a, int r, int g, int b)
    {
        Assert.True(ColorLiteral.TryParse(text, out var color));
        Assert.Equal(((byte)a, (byte)r, (byte)g, (byte)b), color);
    }

    /// <summary>Near-misses stay plain text (no swatch for hashtags, out-of-range channels or prose).</summary>
    /// <param name="text">Candidate.</param>
    [Theory]
    [InlineData("#hashtag")]
    [InlineData("#12345")]
    [InlineData("rgb(300, 0, 0)")]
    [InlineData("rgba(0,0,0,2)")]
    [InlineData("the color #fff is white")]
    [InlineData("")]
    public void ColorLiteral_RejectsNonColors(string text)
    {
        Assert.False(ColorLiteral.TryParse(text, out _));
    }

    /// <summary>Only single web-ish URLs count as links; paths and prose do not.</summary>
    /// <param name="text">Candidate.</param>
    /// <param name="expected">Whether it is a link.</param>
    [Theory]
    [InlineData("https://learn.microsoft.com/windows", true)]
    [InlineData("  http://example.com  ", true)]
    [InlineData("www.example.com/path", true)]
    [InlineData("mailto:someone@example.com", true)]
    [InlineData(@"C:\Windows\System32", false)]
    [InlineData("see https://example.com for details", false)]
    [InlineData("foo:bar", false)]
    [InlineData("http://", false)]
    public void LinkDetector_RecognizesOnlyStandaloneUrls(string text, bool expected)
    {
        Assert.Equal(expected, LinkDetector.TryGetLink(text, out _));
    }

    /// <summary>Bare www hosts are normalized to https so "open link" works.</summary>
    [Fact]
    public void LinkDetector_NormalizesWww()
    {
        Assert.True(LinkDetector.TryGetLink("www.example.com", out var uri));
        Assert.Equal("https", uri!.Scheme);
    }

    /// <summary>Equal text hashes equally; the domain prefix separates text from images with the same bytes.</summary>
    [Fact]
    public void ContentHasher_IsStableAndDomainSeparated()
    {
        Assert.Equal(ContentHasher.ForText("abc"), ContentHasher.ForText("abc"));
        Assert.NotEqual(ContentHasher.ForText("abc"), ContentHasher.ForText("abd"));
        Assert.NotEqual(ContentHasher.ForText("abc"), ContentHasher.ForImage(Encoding.UTF8.GetBytes("abc")));
        Assert.Equal(64, ContentHasher.ForText("x").Length);
    }

    /// <summary>Pixel hashes depend on dimensions and pixels only, and never equal byte hashes.</summary>
    [Fact]
    public void ContentHasher_PixelHashIncludesDimensions()
    {
        byte[] pixels = new byte[16];
        Assert.Equal(ContentHasher.ForImagePixels(2, 2, pixels), ContentHasher.ForImagePixels(2, 2, pixels));
        Assert.NotEqual(ContentHasher.ForImagePixels(1, 4, pixels), ContentHasher.ForImagePixels(4, 1, pixels));
        Assert.NotEqual(ContentHasher.ForImagePixels(2, 2, pixels), ContentHasher.ForImage(pixels));
    }

    /// <summary>File lists hash case-insensitively (NTFS semantics).</summary>
    [Fact]
    public void ContentHasher_FilesAreCaseInsensitive()
    {
        Assert.Equal(ContentHasher.ForFiles([@"C:\A.txt"]), ContentHasher.ForFiles([@"c:\a.TXT"]));
    }

    /// <summary>The classification decision table (files beat text; text refines to link/color/rich).</summary>
    [Fact]
    public void Classifier_DecisionTable()
    {
        Assert.Equal(ClipKind.Text, ContentClassifier.Classify(TestData.Text("hello"))!.Kind);
        Assert.Equal(ClipKind.Link, ContentClassifier.Classify(TestData.Text("https://example.com"))!.Kind);
        Assert.Equal(ClipKind.Color, ContentClassifier.Classify(TestData.Text("#ff00aa"))!.Kind);
        Assert.Equal(ClipKind.RichText, ContentClassifier.Classify(TestData.Rich("hi", "<b>hi</b>"))!.Kind);
        Assert.Equal(ClipKind.Files, ContentClassifier.Classify(TestData.Files(@"C:\a.txt"))!.Kind);
        Assert.Equal(ClipKind.Image, ContentClassifier.Classify(TestData.Image(7))!.Kind);
    }

    /// <summary>Text plus a bitmap (Office/browser copies) is still a text clip, keyed by its text.</summary>
    [Fact]
    public void Classifier_TextWithBitmapIsText()
    {
        var capture = new ClipCapture
        {
            Formats =
            [
                new ClipFormatData(ClipFormatNames.UnicodeText, UnicodeTextCodec.Encode("cell")),
                new ClipFormatData(ClipFormatNames.Dib, new byte[100]),
            ],
        };
        var classified = ContentClassifier.Classify(capture)!;
        Assert.Equal(ClipKind.Text, classified.Kind);
        Assert.Equal(ContentHasher.ForText("cell"), classified.ContentHash);
    }

    /// <summary>Captures with nothing displayable (empty text, private formats only) are rejected.</summary>
    [Fact]
    public void Classifier_RejectsEmptyContent()
    {
        Assert.Null(ContentClassifier.Classify(TestData.Text("")));
        Assert.Null(ContentClassifier.Classify(new ClipCapture { Formats = [new ClipFormatData("Private", [1, 2])] }));
    }

    /// <summary>HTML without plain text still becomes a (rich) clip rather than being lost.</summary>
    [Fact]
    public void Classifier_RichOnly()
    {
        var capture = new ClipCapture { Formats = [new ClipFormatData(ClipFormatNames.Html, Encoding.UTF8.GetBytes("<i>x</i>"))] };
        Assert.Equal(ClipKind.RichText, ContentClassifier.Classify(capture)!.Kind);
    }

    /// <summary>Previews normalize newlines, drop leading blank lines, keep indentation and truncate with an ellipsis.</summary>
    [Fact]
    public void Preview_NormalizesAndTruncates()
    {
        Assert.Equal("  code\nmore", ContentClassifier.BuildTextPreview("\r\n\r\n  code\r\nmore  \r\n"));
        var longText = new string('x', 5000);
        var preview = ContentClassifier.BuildTextPreview(longText);
        Assert.Equal(ContentClassifier.PreviewMaxChars, preview.Length);
        Assert.EndsWith("…", preview);
    }

    /// <summary>Whitespace-only copies get a visible placeholder so the card is never blank.</summary>
    [Fact]
    public void Preview_WhitespaceGetsPlaceholder()
    {
        Assert.Equal("(whitespace · 1 char)", ContentClassifier.BuildTextPreview(" "));
        Assert.Equal("(whitespace · 3 chars)", ContentClassifier.BuildTextPreview(" \t\n"));
    }

    /// <summary>Truncation never splits a surrogate pair (which SQLite would store as U+FFFD).</summary>
    [Fact]
    public void Preview_DoesNotSplitSurrogates()
    {
        var text = new string('a', ContentClassifier.PreviewMaxChars - 2) + "😀😀😀";
        var preview = ContentClassifier.BuildTextPreview(text);
        Assert.DoesNotContain('\uFFFD', preview);
        Assert.False(char.IsHighSurrogate(preview[^2]) && preview[^1] == '…');
    }

    /// <summary>File previews list names and summarize the overflow.</summary>
    [Fact]
    public void DescribeFiles_SummarizesLongLists()
    {
        var paths = Enumerable.Range(1, 7).Select(i => $@"C:\dir\file{i}.txt").ToArray();
        var preview = ContentClassifier.DescribeFiles(paths);
        Assert.StartsWith("file1.txt\nfile2.txt", preview);
        Assert.EndsWith("+2 more", preview);
        Assert.Equal("folder", ContentClassifier.DescribeFiles([@"C:\folder\"]));
    }

    /// <summary>Plain-text replay keeps only text, and turns file lists into their paths.</summary>
    [Fact]
    public void Replay_PlainText()
    {
        var rich = TestData.Rich("hi", "<b>hi</b>").Formats;
        var plain = ReplayFormats.Prepare(rich, plainTextOnly: true);
        Assert.Single(plain);
        Assert.Equal(ClipFormatNames.UnicodeText, plain[0].Name);

        var files = ReplayFormats.Prepare(TestData.Files(@"C:\a.txt", @"C:\b.txt").Formats, plainTextOnly: true);
        Assert.Equal("C:\\a.txt" + Environment.NewLine + "C:\\b.txt", UnicodeTextCodec.Decode(files[0].Data));
        Assert.Empty(ReplayFormats.Prepare(TestData.Image(1).Formats, plainTextOnly: true));
    }

    /// <summary>Replaying an old "cut" never moves files: the drop effect is rewritten to copy.</summary>
    [Fact]
    public void Replay_NeutralizesCut()
    {
        var formats = TestData.Files(@"C:\a.txt").Formats
            .Append(new ClipFormatData(ClipFormatNames.PreferredDropEffect, [2, 0, 0, 0]))
            .ToList();
        var replay = ReplayFormats.Prepare(formats, plainTextOnly: false);
        Assert.Equal([1, 0, 0, 0], replay.Single(f => f.Name == ClipFormatNames.PreferredDropEffect).Data);
    }
}
