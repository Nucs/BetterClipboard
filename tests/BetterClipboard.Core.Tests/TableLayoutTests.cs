using System.Globalization;
using System.Text;
using BetterClipboard.Core.TextView;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for how the text viewer lays out a Markdown table (<see cref="RtfWriter"/>): columns fitted into the text width,
/// cells wrapped inside their own column, wide characters counted as two, and columns standing at tab stops. The owner's
/// report (2026-11-20): "this table seems overflowing to other columns rather than more height per row or more with per
/// column".
/// </summary>
public sealed class TableLayoutTests
{
    /// <summary>The style at 100 %, without a text width: tables keep their natural widths.</summary>
    private static readonly TextViewStyle Unbounded = TextViewStyle.Default("Cascadia Mono");

    /// <summary>
    /// The owner's table, in BC-TEST words: short phases, long goals, and a column whose header is far wider than its
    /// values ("Computes option profit" over "No" and "Yes").
    /// </summary>
    private const string Roadmap =
        "| Phase | Goal | Computes option profit |\n|---|---|---|\n" +
        "| P0 | BC-TEST foundations: pick rule, offline trade replay, score, full cost table | No |\n" +
        "| P1 | BC-TEST live paper trading with the same rules | Yes |";

    /// <summary>The cases of <see cref="FitColumns_CutsTheWidestColumnsToOneLevel"/>: natural widths, the budget, the widths.</summary>
    public static TheoryData<int[], int, int[]> LevelCases => new()
    {
        { [5, 70, 22], 200, [5, 70, 22] },          // fits: natural widths
        { [5, 70, 22], 101, [5, 70, 22] },          // fits exactly (97 and two gaps of 2)
        { [5, 70, 22], int.MaxValue, [5, 70, 22] }, // no limit
        { [5, 70, 22], 80, [5, 49, 22] },           // the long column alone is cut
        { [5, 70, 60], 80, [5, 36, 35] },           // two long columns share the room; the odd character goes left
        { [3, 40], 12, [3, 7] },                    // a narrow column keeps its width
        { [30, 30, 30], 10, [6, 6, 6] },            // not even the floors fit: the floors, wider than the box
        { [1, 1, 1], 1, [1, 1, 1] },                // a budget below the gaps
        { [], 10, [] },                             // no columns
    };

    /// <summary>
    /// The water level: a table that fits keeps its natural widths; otherwise the columns narrower than one common width keep
    /// theirs, the wider ones are cut to it, what is left goes to the cut ones left to right, and nothing goes below 6.
    /// </summary>
    /// <param name="natural">The columns' natural widths.</param>
    /// <param name="budget">The characters a line may take.</param>
    /// <param name="expected">The widths.</param>
    [Theory]
    [MemberData(nameof(LevelCases))]
    public void FitColumns_CutsTheWidestColumnsToOneLevel(int[] natural, int budget, int[] expected) =>
        Assert.Equal(expected, RtfWriter.FitColumns(natural, budget));

    /// <summary>
    /// The data first: a column keeps at least its data's width (its header wraps instead), what is left raises the columns
    /// toward their natural widths; when even the data does not fit, the data's widths are cut by one level.
    /// </summary>
    [Fact]
    public void FitColumnsToData_WrapsHeadersBeforeData()
    {
        // "Computes option profit" (22) over values of 3, its longest header word 8: the room goes to the goals.
        Assert.Equal([5, 33, 8], RtfWriter.FitColumnsToData([5, 76, 22], [5, 76, 8], 50));

        // The data fits with room to spare: the header's column is raised toward its natural width.
        Assert.Equal([5, 30, 21], RtfWriter.FitColumnsToData([5, 76, 22], [5, 30, 8], 60));

        // A table that fits keeps its natural widths, and without a header wider than its data both layouts agree.
        Assert.Equal([5, 76, 22], RtfWriter.FitColumnsToData([5, 76, 22], [5, 76, 8], 200));
        Assert.Equal(RtfWriter.FitColumns([5, 70, 60], 80), RtfWriter.FitColumnsToData([5, 70, 60], [5, 70, 60], 80));
        Assert.Throws<ArgumentException>(() => RtfWriter.FitColumnsToData([5, 6], [5], 10));
    }

    /// <summary>
    /// 2,000 random tables and budgets: no width above its natural width or below its floor; when the floors fit, the
    /// line takes at most the budget, and exactly the budget when the table was cut; the cut columns differ by at most one
    /// character, and no column kept whole is wider than a cut one (the water level). The data-first layout keeps every
    /// column at least as wide as its data whenever the data fits.
    /// </summary>
    [Fact]
    public void FitColumns_RandomTablesFitTheirBudget()
    {
        var random = new Random(20261121);
        for (int round = 0; round < 2000; round++)
        {
            int count = random.Next(1, 9);
            int[] natural = [.. Enumerable.Range(0, count).Select(_ => random.Next(1, 121))];
            int[] data = [.. natural.Select(width => random.Next(1, width + 1))];
            int budget = random.Next(1, 300);
            string label = $"natural [{string.Join(", ", natural)}], data [{string.Join(", ", data)}], budget {budget}";
            long gaps = RtfWriter.ColumnGapChars * (count - 1L);
            bool fits = natural.Sum() + gaps <= budget;
            bool floorsFit = natural.Sum(width => Math.Min(width, RtfWriter.MinColumnChars)) + gaps <= budget;

            var widths = RtfWriter.FitColumns(natural, budget);
            Assert.Equal(count, widths.Length);
            for (int c = 0; c < count; c++)
            {
                Assert.InRange(widths[c], Math.Min(natural[c], RtfWriter.MinColumnChars), natural[c]);
            }

            if (fits)
            {
                Assert.Equal(natural, widths);
            }
            else if (floorsFit)
            {
                Assert.True(widths.Sum() + gaps == budget, label);
                // A column one character above the level is made whole by the characters left over, so it may stand one
                // above a cut column that got none of them.
                var cut = Enumerable.Range(0, count).Where(c => widths[c] < natural[c]).Select(c => widths[c]).ToArray();
                Assert.True(cut.Max() - cut.Min() <= 1, label);
                Assert.All(Enumerable.Range(0, count).Where(c => widths[c] == natural[c]), c => Assert.True(widths[c] <= cut.Max() + 1, label));
            }

            var dataFirst = RtfWriter.FitColumnsToData(natural, data, budget);
            for (int c = 0; c < count; c++)
            {
                Assert.True(dataFirst[c] <= natural[c], label);
                if (data.Sum() + gaps <= budget)
                {
                    Assert.True(dataFirst[c] >= data[c], label);
                }
            }

            if (!fits && data.Sum(width => Math.Min(width, RtfWriter.MinColumnChars)) + gaps <= budget)
            {
                Assert.True(dataFirst.Sum() + gaps == budget, label);
            }
        }
    }

    /// <summary>The cases of <see cref="WrapCell_BreaksBetweenWords"/>: a cell's text, the column's width, its lines.</summary>
    public static TheoryData<string, int, string[]> WrapCases => new()
    {
        { "Foundations: pick rule, offline trade replay, score, full cost table", 20, ["Foundations: pick", "rule, offline trade", "replay, score, full", "cost table"] },
        { "abc def", 3, ["abc", "def"] },            // the blank after a full line is where it breaks
        { "abcdefgh", 3, ["abc", "def", "gh"] },     // a word wider than the column is cut
        { "ab  cd", 2, ["ab", "cd"] },               // a continuation line starts at the next word
        { "one\ntwo", 10, ["one", "two"] },          // a cell's own line break (a <br>) is kept
        { "one \n\ntwo", 10, ["one", string.Empty, "two"] },
        { string.Empty, 5, [string.Empty] },         // an empty cell is one empty line
        { "fits", 4, ["fits"] },
        { "x", 0, ["x"] },                           // a width below 1 counts as 1
        { "中文字符", 4, ["中文", "字符"] },         // two characters each
        { "✅ done", 4, ["✅", "done"] },
        { "中", 1, ["中"] },                         // a character wider than the column still moves on
        { "e\u0301e\u0301e\u0301", 2, ["e\u0301e\u0301", "e\u0301"] }, // a letter and its accent stay together
    };

    /// <summary>
    /// A cell wraps at the last blank that fits, keeps its own line breaks, cuts a word wider than its column, counts a wide
    /// character as two, and never splits a letter from its accent.
    /// </summary>
    /// <param name="text">The cell's text.</param>
    /// <param name="width">The column's width.</param>
    /// <param name="expected">The lines.</param>
    [Theory]
    [MemberData(nameof(WrapCases))]
    public void WrapCell_BreaksBetweenWords(string text, int width, string[] expected) =>
        Assert.Equal(expected, RtfWriter.WrapCell(text, width));

    /// <summary>The cases of <see cref="CellWidth_CountsWideAndZeroWidthCharacters"/>: a text and its width in characters.</summary>
    public static TheoryData<string, int> WidthCases => new()
    {
        { string.Empty, 0 },
        { "abc", 3 },
        { "Ünïcödé", 7 },
        { "中文", 4 },              // CJK ideographs
        { "テキスト", 8 },          // katakana
        { "한국어", 6 },            // Hangul
        { "１２", 4 },              // fullwidth digits
        { "✅", 2 },                // an emoji in the dingbats block
        { "⚠\uFE0F", 2 },           // a symbol drawn as an emoji (the variation selector)
        { "\U0001F44D\U0001F3FD", 2 },              // an emoji and its skin tone: one grapheme
        { "e\u0301", 1 },           // a letter and a combining accent
        { "\u200Fא", 1 },           // a right-to-left mark takes no room
        { "a\u200Bb", 2 },          // nor a zero-width space
        { "─│", 2 },                // box drawing is in the code font
    };

    /// <summary>A text's width counts wide characters as two, format characters and accents as none, everything else as one.</summary>
    /// <param name="text">The text.</param>
    /// <param name="expected">Its width.</param>
    [Theory]
    [MemberData(nameof(WidthCases))]
    public void CellWidth_CountsWideAndZeroWidthCharacters(string text, int expected) =>
        Assert.Equal(expected, RtfWriter.CellWidth(text));

    /// <summary>
    /// A lone surrogate (half of a pair, as a cut-off copy can hold) counts as one character, drawn as a box, and wraps
    /// like one. A case of its own: theory data must round-trip through text, which a lone surrogate does not.
    /// </summary>
    [Fact]
    public void CellWidth_LoneSurrogateIsOneCharacter()
    {
        Assert.Equal(1, RtfWriter.CellWidth("\uD800"));
        Assert.Equal(3, RtfWriter.CellWidth("a\uDC00b"));
        Assert.Equal(["ab", "\uD800c"], RtfWriter.WrapCell("ab\uD800c", 2));
    }

    /// <summary>
    /// The owner's table in a box of 50 characters (405 DIPs: 8 DIPs a character, and 1 % left free): wrapping the header leaves the goals
    /// 33 characters, so the table takes 8 lines where cutting the widest columns evenly took 9. Every column stands at a
    /// stop: 7 characters (56 DIPs, 840 twips) and 42 characters (336 DIPs, 5,040 twips) from the margin. A row that wraps
    /// gets a gap of 4 DIPs after it (60 twips), the last row the table's 8 (120 twips).
    /// </summary>
    [Fact]
    public void Roadmap_InA50CharacterBox_WrapsTheHeaderAndTheGoals()
    {
        var rtf = Draw(Roadmap, Unbounded with { TextWidth = 405 });
        Assert.Equal(
            "Phase\tGoal\tComputes\n\t\toption\n\t\tprofit\n" +
            "─────\t" + new string('─', 33) + "\t────────\n" +
            "P0\tBC-TEST foundations: pick rule,\tNo\n\toffline trade replay, score, full\n\tcost table\n" +
            "P1\tBC-TEST live paper trading with\tYes\n\tthe same rules",
            TextViewRenderingTests.RtfText(rtf));
        Assert.Contains(@"\pard\tx840\tx5040\sa0\f1\fs20", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\pard\tx840\tx5040\sa60\f1\fs20", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\pard\tx840\tx5040\sa120\f1\fs20", rtf, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same table in a box of 90 characters (728 DIPs): cutting the goals to 59 characters keeps the header on one line and takes 4
    /// lines, where wrapping the header first would take 6, so the goals are cut.
    /// </summary>
    [Fact]
    public void Roadmap_InA90CharacterBox_CutsTheGoals()
    {
        var text = TextViewRenderingTests.RtfText(Draw(Roadmap, Unbounded with { TextWidth = 728 }));
        Assert.Equal(
            "Phase\tGoal\tComputes option profit\n" +
            "─────\t" + new string('─', 59) + "\t" + new string('─', 22) + "\n" +
            "P0\tBC-TEST foundations: pick rule, offline trade replay,\tNo\n\tscore, full cost table\n" +
            "P1\tBC-TEST live paper trading with the same rules\tYes",
            text);
    }

    /// <summary>
    /// Without a text width (the viewer's Wrap off) every column keeps its natural width and every row is one line; the
    /// rows of a table that does not wrap get no gap between them.
    /// </summary>
    [Fact]
    public void Roadmap_WithoutAWidth_KeepsNaturalWidths()
    {
        var rtf = Draw(Roadmap, Unbounded);
        Assert.Equal(
            "Phase\tGoal\tComputes option profit\n" +
            "─────\t" + new string('─', 76) + "\t" + new string('─', 22) + "\n" +
            "P0\tBC-TEST foundations: pick rule, offline trade replay, score, full cost table\tNo\n" +
            "P1\tBC-TEST live paper trading with the same rules\tYes",
            TextViewRenderingTests.RtfText(rtf));
        Assert.DoesNotContain(@"\sa60", rtf, StringComparison.Ordinal);
    }

    /// <summary>
    /// A width is in DIPs, so a larger text size fits fewer characters into the same box: at 200 % the 400 DIPs hold 25
    /// characters of 15.6 DIPs (26-DIP text, which RTF rounds to 19.5 points), with 1 % left free.
    /// </summary>
    [Fact]
    public void TextWidth_IsKeptBySizeAndFitsFewerCharactersAtALargerSize()
    {
        var style = (Unbounded with { TextWidth = 400 }).Scaled(2);
        Assert.Equal(400, style.TextWidth);
        var text = TextViewRenderingTests.RtfText(Draw(Roadmap, style));
        Assert.StartsWith("Phase\tGoal\tComputes\n\t\toption\n\t\tprofit\n─────\t────────\t────────\nP0\tBC-TEST\tNo\n", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A measured advance sets the tab stops and the fit: Consolas, 1,126 of 2,048 units an em, draws a 13⅓-DIP character
    /// 7.33 DIPs wide, so the owner's table in 400 DIPs gets 54 characters (with 1 % left free) where 0.6 em gave 49, and
    /// its columns stand 7 and 32 characters from the margin: 770 and 3,519 twips. The goals take 23 characters and wrap;
    /// wrapping the header first would take as many lines, so the header stays whole.
    /// </summary>
    [Fact]
    public void CodeAdvance_SetsTheStopsAndTheFit()
    {
        var consolas = Unbounded with { TextWidth = 400, CodeAdvanceEm = 1126 / 2048.0 };
        var rtf = Draw(Roadmap, consolas);
        var lines = TextViewRenderingTests.RtfText(rtf).Split('\n');
        Assert.Equal("Phase\tGoal\tComputes option profit", lines[0]);
        Assert.Equal("─────\t" + new string('─', 23) + "\t" + new string('─', 22), lines[1]);
        Assert.Equal("P0\tBC-TEST foundations:\tNo", lines[2]);
        Assert.Contains(@"\pard\tx770\tx3519\sa0", rtf, StringComparison.Ordinal);

        // A small table keeps its natural widths, its stop at the measured advance: 3 characters, 330 twips.
        Assert.Contains(@"\pard\tx330\sa0", Draw("| A | Bee |\n|---|---|\n| 1 | 2 |", consolas), StringComparison.Ordinal);

        // An advance that is not a number counts as not measured (0.6 em: 3 characters are 360 twips).
        Assert.Contains(@"\pard\tx360\sa0", Draw("| A | Bee |\n|---|---|\n| 1 | 2 |", Unbounded with { CodeAdvanceEm = double.NaN }), StringComparison.Ordinal);
    }

    /// <summary>
    /// A table in a quote is fitted into what its indentation leaves: of 372 DIPs the quote takes 18, and the 354 left hold
    /// 43 whole characters (8 DIPs each, 1 % left free), 41 of them for the cells after the gap, so the values get 38 and
    /// "seven" goes to the next line; at the margin the same box holds 46 (44 for the cells), and the values' 41 take
    /// "seven" too.
    /// </summary>
    [Fact]
    public void QuotedTable_IsFittedRightOfItsIndentation()
    {
        const string Table = "| Key | Value |\n|---|---|\n| a | BC-TEST one two three four five six seven eight nine ten |";
        var quoted = TextViewRenderingTests.RtfText(Draw("> " + Table.Replace("\n", "\n> ", StringComparison.Ordinal), Unbounded with { TextWidth = 372 }));
        Assert.Equal(
            "Key\tValue\n───\t" + new string('─', 38) + "\na\tBC-TEST one two three four five six\n\tseven eight nine ten",
            quoted);

        var atTheMargin = TextViewRenderingTests.RtfText(Draw(Table, Unbounded with { TextWidth = 372 }));
        Assert.Equal(
            "Key\tValue\n───\t" + new string('─', 41) + "\na\tBC-TEST one two three four five six seven\n\teight nine ten",
            atTheMargin);
    }

    /// <summary>
    /// A cell's own line break (a <c>&lt;br&gt;</c>) starts a new line in its column, and a tab in a cell is a blank (it
    /// would jump to the next column's stop).
    /// </summary>
    [Fact]
    public void Cells_KeepTheirLineBreaks_AndLoseTabs()
    {
        var text = TextViewRenderingTests.RtfText(Draw("| A | B |\n|---|---|\n| one<br>two | x\ty |", Unbounded));
        Assert.Equal("A\tB\n───\t───\none\tx y\ntwo", text);
    }

    /// <summary>
    /// RichEdit keeps 32 tab stops a paragraph: a table of 34 columns is laid out with blanks instead, each column padded to
    /// its width and the gap.
    /// </summary>
    [Fact]
    public void TableOfMoreColumnsThanStops_UsesBlanks()
    {
        var header = string.Concat(Enumerable.Range(0, 34).Select(i => $"| c{i} "));
        var rule = string.Concat(Enumerable.Repeat("|---", 34));
        var row = string.Concat(Enumerable.Range(0, 34).Select(i => i % 2 == 0 ? "| x " : "| yyy "));
        var rtf = Draw($"{header}|\n{rule}|\n{row}|", Unbounded);
        Assert.DoesNotContain(@"\tx", rtf, StringComparison.Ordinal);
        var lines = TextViewRenderingTests.RtfText(rtf).Split('\n');
        Assert.DoesNotContain('\t', lines[2]);

        // The columns are 2 ("c0" over "x") and 3 ("c1" over "yyy") characters wide, each followed by the 2-character gap.
        Assert.StartsWith("c0  c1   c2  c3   ", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("──  ───  ──  ───  ", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("x   yyy  x   yyy  ", lines[2], StringComparison.Ordinal);
    }

    /// <summary>
    /// 1,000 random tables (words of Latin, Chinese, Japanese and emoji, accents, empty cells, line breaks, words too long
    /// for any column) in random boxes: no piece of a line is wider than its column (the widths read off the rule), unless it
    /// is one grapheme; the line fits the box whenever the narrowest columns do; and every character of every cell is drawn,
    /// none lost and none added.
    /// </summary>
    [Fact]
    public void RandomTables_NeverOverflowAColumnOrLoseACharacter()
    {
        string[] words = ["BC", "test", "a", "row", "foundations:", "replay,", "中文", "✅", "日本語テキスト", "x", "averyveryverylongwordthatmustbecut", "Ünïcödé", "e\u0301", "\U0001F44D\U0001F3FD", "⚠\uFE0F", "42", "<br>"];
        var random = new Random(20261120);
        var token = TestContext.Current.CancellationToken;
        for (int round = 0; round < 1000; round++)
        {
            int columns = random.Next(1, 7);
            int rows = random.Next(1, 6);
            var cells = new List<string[]>();
            var markdown = new StringBuilder();
            for (int r = 0; r <= rows; r++)
            {
                var row = new string[columns];
                for (int c = 0; c < columns; c++)
                {
                    // A header cell holds a word at least (a header row of nothing is no table); a body cell may be empty.
                    row[c] = string.Join(' ', Enumerable.Range(0, random.Next(r == 0 ? 1 : 0, 9)).Select(_ => words[random.Next(words.Length)]));
                }

                cells.Add(row);
                markdown.Append("| ").Append(string.Join(" | ", row)).Append(" |\n");
                if (r == 0)
                {
                    markdown.Append(string.Concat(Enumerable.Repeat("|---", columns))).Append("|\n");
                }
            }

            // At most this many characters: the writer leaves 1 % of the width free besides.
            double width = random.Next(40, 1400);
            int budget = (int)Math.Floor(width / 8.0);
            var view = TextViewRendering.Create(TextViewSource.Create(markdown.ToString(), cancellationToken: token), TextLanguage.Markdown, formatted: true, token);
            Assert.True(view.HasTables);
            var lines = TextViewRenderingTests.RtfText(view.ToRtf(SyntaxPalette.Dark, Unbounded with { TextWidth = width }, token)).Split('\n');
            string label = $"round {round}, {width} DIPs: {markdown}";

            // The rule under the header gives every column's width.
            var ruleLine = lines.Single(line => line.Length > 0 && line.All(ch => ch is '─' or '\t'));
            int[] widths = [.. ruleLine.Split('\t').Select(piece => piece.Length)];
            Assert.Equal(columns, widths.Length);
            long gaps = RtfWriter.ColumnGapChars * (columns - 1L);
            if (widths.Sum(w => Math.Min(w, RtfWriter.MinColumnChars)) + gaps <= budget)
            {
                Assert.True(widths.Sum() + gaps <= budget, label);
            }

            foreach (var line in lines.Where(line => !ReferenceEquals(line, ruleLine)))
            {
                var pieces = line.Split('\t');
                Assert.True(pieces.Length <= columns, label);
                for (int c = 0; c < pieces.Length; c++)
                {
                    bool oneGrapheme = new StringInfo(pieces[c]).LengthInTextElements <= 1;
                    Assert.True(RtfWriter.CellWidth(pieces[c]) <= widths[c] || oneGrapheme, $"{label}\nline \"{line}\", column {c}");
                }
            }

            // Every non-blank character of the cells is drawn, in the same numbers (a <br> is a line break, not text).
            static string Ink(IEnumerable<string> texts) =>
                new([.. string.Concat(texts).Replace("<br>", string.Empty, StringComparison.Ordinal).Where(ch => !char.IsWhiteSpace(ch)).Order()]);
            Assert.Equal(Ink(cells.SelectMany(row => row)), Ink(lines.Where(line => !ReferenceEquals(line, ruleLine))));
        }
    }

    /// <summary>A Markdown view knows whether it holds a table, also inside a quote; a code view never does.</summary>
    [Fact]
    public void HasTables_FindsTablesAtAnyDepth()
    {
        static TextViewRendering View(string text, bool formatted = true) =>
            TextViewRendering.Create(TextViewSource.Create(text), TextLanguage.Markdown, formatted);

        Assert.True(View("| a |\n|---|\n| 1 |").HasTables);
        Assert.True(View("> > | a |\n> > |---|\n> > | 1 |").HasTables);
        Assert.False(View("# BC-TEST\n\n- one\n> quoted").HasTables);
        Assert.False(View("| a |\n|---|\n| 1 |", formatted: false).HasTables);
    }

    /// <summary>Draws Markdown as the viewer does: parsed with its layout kept, written with the given style.</summary>
    /// <param name="markdown">The Markdown.</param>
    /// <param name="style">The style (its text width included).</param>
    /// <returns>The RTF.</returns>
    private static string Draw(string markdown, TextViewStyle style)
    {
        var token = TestContext.Current.CancellationToken;
        var view = TextViewRendering.Create(TextViewSource.Create(markdown, cancellationToken: token), TextLanguage.Markdown, formatted: true, token);
        return view.ToRtf(SyntaxPalette.Dark, style, token);
    }
}
